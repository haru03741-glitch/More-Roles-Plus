// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/Phosphorus.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ホスフォラス (クルー): ランタンを置ける。ランタンは次のターンから全員に見え、「点灯」で周りを照らす。
// ランタンは地形の中の物として振る舞う: 光は壁で影になり (壊れた壁の穴からは漏れる)、水に浸かると消え、
// 燃えている所の近くでは勝手に点き、爆発や打撃で倒れるとガラスが割れて油がこぼれる (点いていれば油に火がつく)。
// 水・火・爆発はどの端末でも同じに進むので、ランタンの状態は各端末がそれぞれ決める (電文は置く・点けるの 2 つだけ)。
// 油と火を床に入れるのはホストだけ (地形の火はホストを通して全員へ配られる)
public sealed class Phosphorus : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#F9BC51";
    public override Text Name => new("Phosphorus.Name");
    public override string Reading => "ほすふぉらす";
    public override Text Blurb => new("Phosphorus.Blurb");
    public override string Origin => "Nebula";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Phosphorus.Put", "snr_lantern"), new("Phosphorus.Light", "snr_light") };
    public static readonly FloatOpt PutCooldown = new("Phosphorus.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt PutLimit = new("Phosphorus.Limit", 1, 1, 10);
    public static readonly FloatOpt LightCooldown = new("Phosphorus.LightCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt LightDuration = new("Phosphorus.Duration", 10f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt LightRange = new("Phosphorus.Range", 0.5f, 0.1f, 5f, 0.1f, "x");

    private const float ArtH = 0.45f;            // ランタンの高さ (単位)
    private const float FlameH = 0.118f;         // 足元から炎までの画面の高さ (光の中心・絵の描画から測った値)
    private const float GlassBottom = 0.15f;     // 水がこの深さを超えると炎が浸かって消える
    private const float CheckEvery = 0.5f;       // 水・火を見て光の形を作り直す間隔
    private const int Rays = 96;                 // 光の形を作る光線の数
    private const float FireReach = 0.4f;        // この距離の床が燃えていれば勝手に点く
    private const float BlastSlack = 0.3f;       // 爆発の半径にこれを足した内側のランタンは倒れる
    private const float StrikeReach = 0.6f;      // 打撃が壁に当たった点からこの距離のランタンは倒れる
    private const float SpillRadius = 0.6f, IgniteRadius = 0.4f;

    private sealed class Lantern
    {
        public Phosphorus Owner;
        public GameObject Go;
        public SpriteRenderer Body, Glow;
        public GameObject LightGo;
        public MeshRenderer LightR;
        public Mesh Mesh;
        public float X, Y;
        public bool Active, Broken, Wet, Shown;
        public float FireUntil, NextCheck, Phase;
    }

    private readonly List<Lantern> _lanterns = new();
    private bool _manual;                                         // 「点灯」の効果中か
    private static readonly List<Lantern> All = new();            // 全員のランタン (爆発の当たりを見る)
    private static readonly List<(Vector2 at, bool lit, int ship)> SpillDue = new(); // 倒れたランタンの油と火 (全員が積み、次の刻みでホストだけが入れる)
    private static Sprite _off, _on, _broken, _glow;
    private static Material _lightMat;

    internal static int Breaks, Douses, FireLights, Builds;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Phosphorus.Put"), "snr_lantern")
        {
            Cooldown = () => PutCooldown,
            MaxUses = () => PutLimit,
            OnUse = () =>
            {
                var at = Player.GetTruePosition();
                PutCall.Send(at);
                Put(at);
                return true;
            },
        });
        AddAbility(new Ability(new Text("Phosphorus.Light"), "snr_light")
        {
            Cooldown = () => LightCooldown,
            Duration = () => LightDuration,
            Cancellable = true,
            OnUse = () =>
            {
                if (!_lanterns.Exists(CanLight)) return false;
                LightCall.Send(true);
                Lighting(true);
                return true;
            },
            OnEnd = () => { LightCall.Send(false); Lighting(false); },
        });
        Lifespan.OnRelease(() =>
        {
            foreach (var l in _lanterns)
            {
                All.Remove(l);
                if (l.Go) Object.Destroy(l.Go);
                if (l.LightGo) Object.Destroy(l.LightGo);
                if (l.Mesh) Object.Destroy(l.Mesh);
            }
            _lanterns.Clear();
        });
    }

    private static bool CanLight(Lantern l) => l.Go && l.Active && !l.Broken && !l.Wet;

    // 会議の後: 置いたランタンを全員に見えるようにする
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        foreach (var l in _lanterns)
        {
            if (!l.Go) continue;
            l.Active = true;
            l.Body.color = UnityEngine.Color.white;
            Refresh(l, Time.time);
        }
    }

    private static readonly RemoteCall<Vector2> PutCall = new("Phosphorus.Put", Route.Anyone,
        (w, p) => { w.Write(p.x); w.Write(p.y); }, r => new Vector2(r.ReadSingle(), r.ReadSingle()),
        (sender, p) => { if (RoleState.Of(sender) is Phosphorus ph) ph.Put(p); });
    private static readonly RemoteCall<bool> LightCall = new("Phosphorus.Light", Route.Anyone,
        (w, on) => w.Write(on), r => r.ReadBoolean(),
        (sender, on) => { if (RoleState.Of(sender) is Phosphorus ph) ph.Lighting(on); });

    // 全員の端末で: ランタンを置く (置いた本人には薄く見え、ほかの人には見えない)
    private void Put(Vector2 at)
    {
        EnsureArt();
        var go = new GameObject("MrpLantern") { layer = 9 };
        go.transform.position = FxMath.V3(at.x, at.y, at.y / 1000f + 0.001f);
        var body = go.AddComponent<SpriteRenderer>();
        body.sprite = _off;
        body.color = FxMath.Rgba(1f, 1f, 1f, IsLocal ? 0.5f : 0f);

        var g = new GameObject("Glow") { layer = 9 };
        g.transform.SetParent(go.transform, false);
        g.transform.localPosition = FxMath.V3(0f, FlameH, -0.002f);
        g.transform.localScale = FxMath.V3(0.9f, 0.9f, 1f);
        var glow = g.AddComponent<SpriteRenderer>();
        glow.sprite = _glow;
        glow.color = FxMath.Rgba(1f, 0.62f, 0.2f, 0f);

        // 影の層に置いて本編の視界の影を切り抜く光。形は光線で作る (壁で影になる)
        var lg = new GameObject("MrpLanternLight") { layer = LayerMask.NameToLayer("Shadow") };
        lg.transform.position = FxMath.V3(at.x, at.y + FlameH, -50f);
        var mf = lg.AddComponent<MeshFilter>();
        var mesh = new Mesh { name = "MrpLanternLight" };
        mf.mesh = mesh;
        var mr = lg.AddComponent<MeshRenderer>();
        var mat = LightMaterial();
        if (mat) mr.sharedMaterial = mat;
        mr.enabled = false;

        var l = new Lantern { Owner = this, Go = go, Body = body, Glow = glow, LightGo = lg, LightR = mr, Mesh = mesh, X = at.x, Y = at.y, Phase = FxMath.Range(0f, 6.28f) };
        _lanterns.Add(l);
        All.Add(l);
        Rewind.Undo(_ => Remove(l)); // 置く前へ戻るとランタンは無くなる (置いた回数は能力が戻す)
        FxRoot.TrackWorld(go); // 逆再生の間は控えの絵で出す (光は視界の影を切るので今のまま)
    }

    private void Remove(Lantern l)
    {
        _lanterns.Remove(l);
        All.Remove(l);
        if (l.Go) Object.Destroy(l.Go);
        if (l.LightGo) Object.Destroy(l.LightGo);
        if (l.Mesh) Object.Destroy(l.Mesh);
    }

    private void Lighting(bool on)
    {
        bool was = _manual;
        if (was != on) Rewind.Undo(_ => Lighting(was));
        _manual = on;
        float now = Time.time;
        foreach (var l in _lanterns) Refresh(l, now);
    }

    private int _tick;

    public override void OnFixedUpdate()
    {
        FlushSpills();
        if (_lanterns.Count == 0) return;
        _tick++;
        float now = Time.time;
        for (int i = 0; i < _lanterns.Count; i++)
        {
            var l = _lanterns[i];
            if (!l.Go || l.Broken) continue;
            if (now >= l.NextCheck)
            {
                l.NextCheck = now + CheckEvery;
                Check(l, now);
            }
            if (l.Shown && (_tick & 1) == 0) Flicker(l, now);
        }
    }

    // 水と火を見て、点くか消えるかを決める。点いていれば光の形を作り直す (壁が壊れた・扉が開いた分を拾う)
    private void Check(Lantern l, float now)
    {
        bool wet = WaterSim.DepthAt(FxMath.V2(l.X, l.Y)) >= (int)(WaterSim.Full * GlassBottom);
        if (wet != l.Wet)
        {
            l.Wet = wet;
            if (wet && l.Shown)
            {
                Douses++;
                BreakNoise.PlayAt("noise_steam", FxMath.V2(l.X, l.Y), 6f, 3f, 0.6f);
            }
        }
        if (l.Active && !l.Wet && FireNear(l.X, l.Y))
        {
            if (now >= l.FireUntil) FireLights++;
            l.FireUntil = now + LightDuration;
        }
        if (!Refresh(l, now) && l.Shown) Build(l);
    }

    // 点くか消えるかを見た目に反映する。点いて光の形を作り直したら true
    private bool Refresh(Lantern l, float now)
    {
        if (!l.Go || l.Broken) return false;
        bool lit = CanLight(l) && (_manual || now < l.FireUntil);
        if (lit == l.Shown) return false;
        l.Shown = lit;
        l.Body.sprite = lit ? _on : _off;
        l.Glow.color = FxMath.Rgba(1f, 0.62f, 0.2f, lit ? 0.38f : 0f);
        l.LightR.enabled = lit && _lightMat;
        if (lit)
        {
            Build(l);
            BreakNoise.PlayAt("noise_fire_ignite", FxMath.V2(l.X, l.Y), 6f, 3f, 0.35f);
        }
        return lit;
    }

    // 炎のゆらぎ: にじみと光の広さを少しだけ揺らす (円の拡縮でなく、不規則な 2 つの波の和)
    private static void Flicker(Lantern l, float now)
    {
        float t = now * 7f + l.Phase;
        float k = 1f + 0.03f * FxMath.Sin(t) + 0.015f * FxMath.Sin(t * 2.7f + 1.3f);
        l.Glow.transform.localScale = FxMath.V3(0.9f * k, 0.9f * k, 1f);
        l.LightGo.transform.localScale = FxMath.V3(k, k, 1f);
    }

    private static float Radius => ShipStatus.Instance ? ShipStatus.Instance.MaxLightRadius * LightRange * 3f : 1f;

    // 光の形: 炎から光線を飛ばし、壁で止まった所までを扇に並べる。真ん中ほど明るい (UV で薄れ方の絵を貼る)
    private static void Build(Lantern l)
    {
        if (!l.Mesh) return;
        Builds++;
        float r = Radius;
        var c = FxMath.V2(l.X, l.Y + FlameH);
        var pos = new Vector3[Rays + 1];
        var uv = new Vector2[Rays + 1];
        var idx = new int[Rays * 3];
        pos[0] = FxMath.V3(0f, 0f, 0f);
        uv[0] = FxMath.V2(0.5f, 0.5f);
        int mask = Constants.ShipAndObjectsMask;
        for (int i = 0; i < Rays; i++)
        {
            float a = i * (2f * System.MathF.PI / Rays);
            float dx = FxMath.Cos(a), dy = FxMath.Sin(a);
            var hit = Physics2D.Raycast(c, FxMath.V2(dx, dy), r, mask);
            float d = hit.distance > 0f ? hit.distance + 0.05f : r; // 当たらなければ 0 (値で読む・当たった物は引かない)
            pos[i + 1] = FxMath.V3(dx * d, dy * d, 0f);
            uv[i + 1] = FxMath.V2(0.5f + dx * d / (2f * r), 0.5f + dy * d / (2f * r));
            // カメラから見て時計回り (逆だと裏向きで描かれない)
            idx[i * 3] = 0;
            idx[i * 3 + 1] = i + 1 == Rays ? 1 : i + 2;
            idx[i * 3 + 2] = i + 1;
        }
        l.Mesh.Clear();
        l.Mesh.SetVertices(pos);
        l.Mesh.SetUVs(0, uv);
        l.Mesh.SetTriangles(idx, 0);
    }

    private static bool FireNear(float x, float y)
    {
        if (!FireSim.Ready) return false;
        var org = FireSim.Origin;
        float cell = FireSim.Cell;
        int w = FireSim.W, h = FireSim.H;
        int x0 = (int)FxMath.Floor((x - FireReach - org.x) / cell), x1 = (int)FxMath.Floor((x + FireReach - org.x) / cell);
        int y0 = (int)FxMath.Floor((y - FireReach - org.y) / cell), y1 = (int)FxMath.Floor((y + FireReach - org.y) / cell);
        for (int cy = FxMath.Max(0, y0); cy <= FxMath.Min(h - 1, y1); cy++)
            for (int cx = FxMath.Max(0, x0); cx <= FxMath.Min(w - 1, x1); cx++)
                if (FireSim.Burning(cy * w + cx)) return true;
        return false;
    }

    // 全員の端末で (地形の壊れた知らせは全員に同じ順で届く): 爆発・打撃がランタンに届いたら倒す
    private void OnTerrainBroken(TerrainBrokenEvent e)
    {
        float reach = e.Kind == DamageKind.Explosion ? e.Radius + BlastSlack : e.Kind is DamageKind.Blunt or DamageKind.Gunfire ? StrikeReach : -1f;
        if (reach <= 0f) return;
        for (int i = 0; i < _lanterns.Count; i++)
        {
            var l = _lanterns[i];
            if (!l.Go || l.Broken) continue;
            float dx = l.X - e.Position.x, dy = l.Y - e.Position.y;
            if (dx * dx + dy * dy > reach * reach) continue;
            Break(l, dx);
        }
    }

    // 倒れてガラスが割れる。隠れていたランタンも全員に見える (油がこぼれるので何かあったと分かる)
    private void Break(Lantern l, float awayX)
    {
        bool wasLit = l.Shown, wasActive = l.Active;

        l.Broken = true;
        l.Active = true;
        l.Shown = false;
        Breaks++;
        l.Body.sprite = _broken;
        l.Body.color = UnityEngine.Color.white;
        l.Body.flipX = awayX < 0f;
        l.Glow.color = FxMath.Rgba(1f, 0.62f, 0.2f, 0f);
        l.LightR.enabled = false;
        BreakNoise.PlayAt("noise_gat_case_2", FxMath.V2(l.X, l.Y), 8f, 4f, 0.8f);
        // 全員が積む: 倒れた後にホストが替わっても、新しいホストが入れられる
        var spill = (FxMath.V2(l.X, l.Y), wasLit, GameClock.ShipGen);
        SpillDue.Add(spill);
        // 倒れる前へ戻ると立ち直る。こぼれた油と火は地形の時戻しが戻し、まだ地形へ入れていない分はここで外す
        Rewind.Undo(_ => { SpillDue.Remove(spill); Unbreak(l, wasActive); });
    }

    private void Unbreak(Lantern l, bool active)
    {
        if (!l.Go || !l.Broken) return;
        l.Broken = false;
        l.Active = active;
        l.Shown = false;
        l.Body.sprite = _off;
        l.Body.flipX = false;
        l.Body.color = active ? UnityEngine.Color.white : FxMath.Rgba(1f, 1f, 1f, IsLocal ? 0.5f : 0f);
        l.NextCheck = 0f;
        Refresh(l, Time.time);
    }

    // 倒れたランタンの油をこぼし、点いていたなら火をつける (地形の処理の外で・ホストだけ・別の試合の分は捨てる)
    private static void FlushSpills()
    {
        if (SpillDue.Count == 0) return;
        bool host = AmongUsClient.Instance && AmongUsClient.Instance.AmHost;
        for (int i = 0; host && i < SpillDue.Count; i++)
        {
            var (at, lit, ship) = SpillDue[i];
            if (ship != GameClock.ShipGen) continue;
            TerrainApi.WorldSpill(at, SpillRadius, 1f);
            if (lit) TerrainApi.WorldIgnite(at, IgniteRadius, 1f);
        }
        SpillDue.Clear();
    }

    internal static void RevealForTest()
    {
        foreach (var l in All) if (l.Go && !l.Active) { l.Active = true; l.Body.color = UnityEngine.Color.white; }
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("lanterns=").Append(All.Count).Append(" breaks=").Append(Breaks).Append(" douses=").Append(Douses)
          .Append(" fireLights=").Append(FireLights).Append(" builds=").Append(Builds);
        foreach (var l in All)
            sb.Append(" [").Append(l.X.ToString("0.0")).Append(',').Append(l.Y.ToString("0.0"))
              .Append(l.Active ? " active" : " hidden").Append(l.Broken ? " broken" : "").Append(l.Wet ? " wet" : "").Append(l.Shown ? " lit" : "").Append(']');
        return sb.ToString();
    }

    // ── 絵 ─────────────────────────────────────────

    private static void EnsureArt()
    {
        if (_off) return;
        float ppu = 256f / ArtH;
        // 足の位置は斜め上から見た壺の底の真ん中 (手前の縁はそれより下に、影は右へ伸びて描かれる)
        _off = ButtonIcons.Load("lantern_off", ppu, FxMath.V2(0.2300f, 0.1621f));
        _on = ButtonIcons.Load("lantern_on", ppu, FxMath.V2(0.2308f, 0.1621f));
        _broken = ButtonIcons.Load("lantern_broken", ppu, FxMath.V2(0.3633f, 0.2393f)); // flipX は足の位置を軸に返る
        _glow = FxSprites.Rect(64, 64, FxSprites.Soft, "mrp_lantern_glow");
    }

    // 光の材質は全部のランタンで 1 つ (真ん中ほど濃い丸を貼る。縁の 3 分の 1 で薄れる)
    private static Material LightMaterial()
    {
        if (_lightMat) return _lightMat;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !lp.LightPrefab || !lp.LightPrefab.LightCutawayMaterial) return null;
        var falloff = FxSprites.Rect(128, 128, (x, y) => FxMath.Clamp01((1f - FxMath.Sqrt(x * x + y * y) * 2f) * 3f), "mrp_lantern_falloff");
        _lightMat = new Material(lp.LightPrefab.LightCutawayMaterial.shader) { name = "MrpLanternLight", mainTexture = falloff.texture };
        _lightMat.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return _lightMat;
    }
}
