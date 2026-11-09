// Ported from https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Roles/Neutral/Spectre.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// スペクターのお揚げの皿。ホストが試合の始めにマップへ置いて配る。食べると空になり、決めた秒の後にまた盛られる
internal static class SpectreDishes
{
    internal sealed class Dish
    {
        public int Id;
        public Vector2 At;
        public bool Ate = true;
        public float Respawn;
        public SpriteRenderer Art;
    }

    internal static readonly List<Dish> All = new();
    private static readonly float[] InitialRespawnCoeff = { 1.0f, 0.6f, 0.2f, 0.5f, 0.8f, 0.3f, 0.7f, 0.4f, 0.9f, 1.0f, 1.0f, 1.0f, 1.0f };
    private static bool _spawned;
    private static Sprite[] _dishSprites;
    internal const float UseDistance = 0.9f;

    internal static bool Spawned => _spawned;

    // ---- 置く ----

    // ホスト: まだ置いていなければ置いて配る
    internal static void HostSpawn(int count)
    {
        if (_spawned || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        var at = PickSpots(count, 7.5f);
        var flat = new float[at.Count * 2];
        for (int i = 0; i < at.Count; i++) { flat[i * 2] = at[i].x; flat[i * 2 + 1] = at[i].y; }
        SpawnCall.Send(flat);
        Spawn(flat);
    }

    private static readonly RemoteCall<float[]> SpawnCall = new("Spectre.Dishes", Route.HostToAll,
        (w, a) => { w.Write((byte)(a.Length / 2)); foreach (var f in a) w.Write(f); },
        r => { var a = new float[r.ReadByte() * 2]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadSingle(); return a; },
        (_, a) => Spawn(a));

    private static void Spawn(float[] flat)
    {
        if (_spawned) return;
        _spawned = true;
        RoleState.Match.OnRelease(Clear);
        float cool = Spectre.Replenish;
        for (int i = 0; i * 2 + 1 < flat.Length; i++)
        {
            var d = new Dish { Id = i, At = FxMath.V2(flat[i * 2], flat[i * 2 + 1]) };
            // 偶数の皿は盛った状態で・奇数の皿は少しずつずらして盛られる
            if (i % 2 == 0) { d.Ate = false; d.Respawn = cool; }
            else d.Respawn = cool * InitialRespawnCoeff[Mathf.Min(i / 2, InitialRespawnCoeff.Length - 1)];
            d.Art = MakeArt(d.At);
            All.Add(d);
            Draw(d);
        }
    }

    private static void Clear()
    {
        foreach (var d in All) if (d.Art) UnityEngine.Object.Destroy(d.Art.gameObject);
        All.Clear();
        _spawned = false;
        SpectreMinigame.Close();
        SpectreMinigame.Forget();
        SpectreMapLayer.Clear();
    }

    // 置き場所: 本編の端末の近くの歩ける所 (端末の前の机などに置いたように見える)。皿どうしは minGap 離す
    private static List<Vector2> PickSpots(int count, float minGap)
    {
        var cand = new List<Vector2>();
        var ship = ShipStatus.Instance;
        if (ship && ship.AllConsoles != null)
            foreach (var c in ship.AllConsoles)
                if (c && Floor(c.transform.position, out var p)) cand.Add(p);
        if (ship && ship.AllVents != null)
            foreach (var v in ship.AllVents)
                if (v && Floor(v.transform.position, out var p)) cand.Add(p);
        var rng = new System.Random();
        for (int i = cand.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (cand[i], cand[j]) = (cand[j], cand[i]); }
        var picked = new List<Vector2>();
        for (float gap = minGap; picked.Count < count && gap > 0.5f; gap *= 0.7f)
            foreach (var p in cand)
            {
                if (picked.Count >= count) break;
                bool ok = true;
                foreach (var q in picked) if (Vector2.Distance(p, q) < gap) { ok = false; break; }
                if (ok) picked.Add(p);
            }
        return picked;
    }

    // 端末から少しずつ離れて、壁から体の幅だけ空いた床を探す
    private static bool Floor(Vector3 from, out Vector2 at)
    {
        at = default;
        if (!SolidMap.Ensure()) { at = from; return true; }
        for (float r = 0.35f; r <= 1.4f; r += 0.15f)
            for (int k = 0; k < 12; k++)
            {
                float a = k * Mathf.PI / 6f + r;
                var p = FxMath.V2(from.x + Mathf.Cos(a) * r, from.y + Mathf.Sin(a) * r);
                if (SolidMap.Solid(p) || SolidMap.Solid(p.x + 0.25f, p.y) || SolidMap.Solid(p.x - 0.25f, p.y)
                    || SolidMap.Solid(p.x, p.y + 0.25f) || SolidMap.Solid(p.x, p.y - 0.25f)) continue;
                at = p;
                return true;
            }
        return false;
    }

    // ---- 絵 ----

    // 立体で描いた長角皿 (空・お揚げ 3 枚を盛った)。絵の中心が皿を置いた床の点
    internal static Sprite DishSprite(bool full)
    {
        _dishSprites ??= ButtonIcons.Frames("spectre_dish", 2, 200f);
        return _dishSprites[full ? 1 : 0];
    }

    private static readonly BodyKind DishKind = new("SpectreDish", BodyMaterial.Stone, BodySize.Medium, 0f);

    private static SpriteRenderer MakeArt(Vector2 at)
    {
        var go = new GameObject("MrpSpectreDish");
        // 奥行きは人と同じ決め方 (足元 + 0.3636): 皿の手前に立つ人は前・奥に立つ人は後ろに回る
        go.transform.position = new Vector3(at.x, at.y, (at.y + 0.3636f) / 1000f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = DishSprite(false);
        Bodies.Prop(DishKind, go.transform, 0f, 0.3f); // 壊れると食べられなくなるので、当たっても火花だけ
        FxRoot.TrackWorld(go); // 逆再生の間は控えの絵で出す (食べた皿が盛られた姿へ戻っていく)
        return sr;
    }

    private static void Draw(Dish d)
    {
        if (d.Art) d.Art.sprite = DishSprite(!d.Ate);
    }

    // ---- 状態の変化 ----

    // 皿の状態を変えて全員へ (eater = 食べた人・255 なら無し)
    internal static void Set(int id, bool ate, byte eater = 255)
    {
        SetCall.Send((id, ate, eater));
        Apply(id, ate, eater);
    }

    private static readonly RemoteCall<(int id, bool ate, byte eater)> SetCall = new("Spectre.Dish", Route.Anyone,
        (w, m) => { w.WritePacked(m.id); w.Write(m.ate); w.Write(m.eater); }, r => (r.ReadPackedInt32(), r.ReadBoolean(), r.ReadByte()),
        (_, m) => Apply(m.id, m.ate, m.eater));

    private static void Apply(int id, bool ate, byte eater)
    {
        if (id < 0 || id >= All.Count) return;
        var d = All[id];
        bool wasAte = d.Ate;
        float wasRespawn = d.Respawn;
        Rewind.Undo(_ => { d.Ate = wasAte; d.Respawn = wasRespawn; Draw(d); }); // 食べる・盛る前へ戻ると皿も戻る
        d.Ate = ate;
        d.Respawn = Spectre.Replenish;
        Draw(d);
        if (!ate)
        {
            if (wasAte) Fx.Hearing.At("spectre_refill", d.At.x, d.At.y, 6f, 0.6f);
            return;
        }
        SpectreMinigame.CloseIf(id);
        // 食べた本人はかじる音を手元で聞いているので、周りの人にだけ皿の所で鳴らす
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.PlayerId == eater) return;
        Fx.Hearing.At("spectre_eat", d.At.x, d.At.y, 6f, 0.8f, eater == 255 ? null : PortKit.ById(eater));
    }

    // 毎フレーム (どの端末でも): 空の皿の盛り直しを数える。盛るのはホスト
    private static float _ticked = -1f;

    internal static void Tick(float dt)
    {
        if (All.Count == 0 || _ticked == Time.fixedTime) return; // スペクターが何人いても 1 回だけ
        _ticked = Time.fixedTime;
        bool paused = MeetingHud.Instance || ExileController.Instance || !ShipStatus.Instance;
        bool host = AmongUsClient.Instance && AmongUsClient.Instance.AmHost;
        foreach (var d in All)
        {
            if (!d.Ate || paused) continue;
            d.Respawn -= dt;
            if (d.Respawn < 0f && host) { d.Respawn = Spectre.Replenish; Set(d.Id, false); }
        }
    }

    // 自分のいちばん近くの、条件に合う皿 (壁越しは除く)
    internal static Dish Nearest(Vector2 from, Func<Dish, bool> ok, bool throughWalls = false, float range = UseDistance)
    {
        Dish best = null;
        float bd = range;
        foreach (var d in All)
        {
            if (!ok(d)) continue;
            float dist = Vector2.Distance(from, d.At);
            if (dist > bd) continue;
            if (!throughWalls && PhysicsHelpers.AnythingBetween(from, d.At, Constants.ShipAndObjectsMask, false)) continue;
            bd = dist;
            best = d;
        }
        return best;
    }

    // 狙っている皿をスペクターの色で光らせる (自分の端末)
    private static Dish _lit;

    internal static void Highlight(Dish d, Color color)
    {
        if (_lit != null && _lit != d && _lit.Art) _lit.Art.color = Color.white;
        _lit = d;
        if (d == null || !d.Art) return;
        float k = 0.5f + 0.5f * Mathf.Sin(Time.time * 5f);
        d.Art.color = Color.Lerp(Color.white, color, 0.35f + 0.4f * k);
    }
}

// お揚げを食べる画面: 3 枚のお揚げを 2 回ずつ 3 段かじる。全部食べると満腹度が増える
internal static class SpectreMinigame
{
    private static GameObject _root;
    private static int _dish = -1;
    private static Action _onDone;
    private static readonly SpriteRenderer[] Fries = new SpriteRenderer[3];
    private static readonly int[] Stage = new int[3];
    private static readonly int[] Bites = new int[3];
    private static Sprite[] _friesSprites;
    private static float _closeAt = -1f;
    private static int _progressDish = -1; // かじりかけの皿 (閉じても同じ皿を開き直せば続きから)

    internal static bool IsOpen => _root;

    // 窓は本編の作業画面くらいの大きさ (HUD の単位で 5.9 x 4)。お揚げは皿の見込みの上 (窓の中心より少し上) に横に並べる
    private const float HudPpu = 180f;
    private const float RowY = 0.12f, Gap = 1.467f, HalfW = 2.93f, HalfH = 2f, PieceHalfW = 0.64f, PieceHalfH = 0.75f;

    // お揚げの絵 (3 段: 0 = まるごと・1 = かじった・2 = 残り少し)。食べる画面の皿と同じ角度で描いた物
    internal static Sprite FriesSprite(int stage)
    {
        _friesSprites ??= ButtonIcons.Frames("spectre_fries", 3, HudPpu);
        return _friesSprites[Mathf.Clamp(stage, 0, 2)];
    }

    // 木の卓に置いた長角皿 (角の丸い枠つき)
    private static Sprite Bg() => ButtonIcons.Load("spectre_minigame", HudPpu, new Vector2(0.5f, 0.5f));

    internal static void Open(int dishId, Action onDone)
    {
        Close();
        var hud = Vanilla.Hud;
        var lp = PlayerControl.LocalPlayer;
        if (!hud || !lp) return;
        _dish = dishId;
        _onDone = onDone;
        _closeAt = -1f;
        _root = FxRoot.NewHud("MrpSpectreMinigame", 0);
        _root.transform.localPosition = new Vector3(0f, 0f, -150f); // タスクの一覧や能力ボタンより手前
        RoleState.Match.Bind(_root);

        Child("Bg", Bg(), Vector3.zero, Vector3.one);
        bool resume = _progressDish == dishId;
        _progressDish = dishId;
        for (int i = 0; i < 3; i++)
        {
            if (!resume) { Stage[i] = 0; Bites[i] = 0; }
            Fries[i] = Child("Fries" + i, FriesSprite(Stage[i]), new Vector3(-Gap + Gap * i, RowY, -0.2f - 0.01f * i), Vector3.one);
            if (Stage[i] >= 3) Fries[i].gameObject.SetActive(false);
        }
        lp.moveable = false;
    }

    private static SpriteRenderer Child(string name, Sprite sp, Vector3 pos, Vector3 scale)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.layer = 5;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }

    // 皿を片付けた時 (試合の終わり): かじりかけを忘れる
    internal static void Forget() => _progressDish = -1;

    internal static void CloseIf(int dishId)
    {
        if (_progressDish == dishId) _progressDish = -1; // 誰かが食べた・取り除いた皿は盛り直されたら新しいお揚げ
        if (_root && _dish == dishId && _closeAt < 0f) Close();
    }

    internal static void Close()
    {
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
        _dish = -1;
        _onDone = null;
        var lp = PlayerControl.LocalPlayer;
        if (lp && !lp.moveable && !MeetingHud.Instance) lp.moveable = true;
    }

    // 毎フレーム (自分の端末): クリックでかじる。会議・死亡・Esc・右クリックで閉じる
    internal static void Tick()
    {
        if (!_root) return;
        var lp = PlayerControl.LocalPlayer;
        if (MeetingHud.Instance || !lp || lp.Data == null || lp.Data.IsDead || Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) { Close(); return; }
        if (_closeAt >= 0f)
        {
            if (Time.time >= _closeAt) Close();
            return;
        }
        if (lp.moveable) lp.moveable = false;
        if (!Input.GetMouseButtonDown(0)) return;
        var cam = Camera.main;
        if (!cam) return;
        Vector3 l = _root.transform.InverseTransformPoint(cam.ScreenToWorldPoint(Input.mousePosition));
        // 枠の外を押しても閉じない (押し損ねでかじった分を失わない)。閉じるのは Esc・右クリック・会議・死んだ時
        if (Mathf.Abs(l.x) > HalfW || Mathf.Abs(l.y) > HalfH) return;
        for (int i = 0; i < 3; i++)
        {
            var f = Fries[i];
            if (!f || !f.gameObject.activeSelf) continue;
            Vector3 c = f.transform.localPosition;
            if (Mathf.Abs(l.x - c.x) > PieceHalfW || Mathf.Abs(l.y - c.y) > PieceHalfH) continue;
            Bite(i);
            return;
        }
    }

    // 残っているいちばん左のお揚げをかじる (確認用)。かじった番号・無ければ -1
    internal static int BiteFirst()
    {
        if (!_root || _closeAt >= 0f) return -1;
        for (int i = 0; i < 3; i++)
            if (Fries[i] && Fries[i].gameObject.activeSelf) { Bite(i); return i; }
        return -1;
    }

    private static void Bite(int i)
    {
        PortSound.Play("spectre_eat", () => PortSound.Fallback("spectre_eat"), 0.8f);
        var f = Fries[i];
        // かじった時に少し揺らす
        f.transform.localScale = Vector3.one * 0.92f;
        PortKit.After(0.06f, () => { if (f) f.transform.localScale = Vector3.one; });
        if (++Bites[i] < 2) return;
        Bites[i] = 0;
        if (++Stage[i] < 3) { f.sprite = FriesSprite(Stage[i]); return; }
        f.gameObject.SetActive(false);
        foreach (var x in Fries) if (x && x.gameObject.activeSelf) return;
        // 全部食べた
        var done = _onDone;
        int dish = _dish;
        _progressDish = -1;
        _closeAt = Time.time + 0.25f;
        if (dish >= 0) SpectreDishes.Set(dish, true, PlayerControl.LocalPlayer.PlayerId);
        done?.Invoke();
    }
}

// 満腹度のゲージ (自分の端末・画面の左下): 狐の顔と、満腹度 1 ごとのお揚げ。少ないお揚げは点滅する
internal static class SpectreGauge
{
    private static GameObject _root;
    private static SpriteRenderer _bar, _fox;
    private static UI.StickClear _clear;
    private static readonly List<(SpriteRenderer back, SpriteRenderer fill, float width)> Units = new();
    private static Sprite _barSprite, _unitSprite;
    private static Sprite[] _foxSprites;
    private static int _foxShown; // 今出している狐 (0 = 足りない・1 = 足りた)
    private static readonly Color Purple = new(202f / 255f, 148f / 255f, 221f / 255f);
    private static readonly Color Gray = new(0.2f, 0.2f, 0.2f, 1f);

    internal static void Ensure(Lifespan life, float max)
    {
        if (_root) return;
        var hud = Vanilla.Hud;
        if (!hud) return;
        _root = FxRoot.NewHud("MrpSpectreGauge", 5);
        life.Bind(_root);
        life.OnRelease(() => { _root = null; Units.Clear(); });
        var aspect = _root.AddComponent<AspectPosition>();
        aspect.Alignment = AspectPosition.EdgeAlignments.LeftBottom;
        aspect.DistanceFromEdge = new Vector3(0.6f, 0.55f, -20f);
        aspect.AdjustPosition();

        float unit = max > 5f ? 0.36f : 0.45f;
        float width = 0.75f + unit * max;
        _barSprite ??= FxSprites.Rect(256, 64, (x, y) =>
        {
            float ax = Mathf.Abs(x) - 0.45f, ay = Mathf.Abs(y) - 0.3f;
            float d = FxMath.Sqrt(Mathf.Max(ax, 0f) * Mathf.Max(ax, 0f) + Mathf.Max(ay, 0f) * Mathf.Max(ay, 0f));
            return d > 0.2f ? 0f : d > 0.15f ? 1f : 0.75f;
        }, "MrpSpectreBar");
        _bar = Child("Bar", _barSprite, new Vector3(width * 0.5f - 0.2f, 0f, 0f), new Vector3(width / 4f, 0.55f, 1f));
        _bar.color = new Color(0.12f, 0.1f, 0.16f, 0.9f);
        // スマホ版の移動パッドに掛からないように
        _clear = new UI.StickClear(aspect, _bar);

        _foxSprites ??= SpectreFoxArt.Frames;
        _foxShown = 1;
        _fox = Child("Fox", _foxSprites[1], new Vector3(0.02f, 0.06f, -0.1f), Vector3.one * 0.68f);

        _unitSprite ??= ButtonIcons.Has("spectre_gauge_fries") ? ButtonIcons.Get("spectre_gauge_fries") : SpectreMinigame.FriesSprite(0);
        int n = Mathf.CeilToInt(max);
        for (int i = 0; i < n; i++)
        {
            float w = Mathf.Min(1f, max - i); // 最後の 1 つは設定の端数だけ
            var pos = new Vector3(0.55f + unit * i, 0f, -0.1f);
            var size = _unitSprite ? _unitSprite.bounds.size : Vector3.one;
            float k = unit * 0.8f / Mathf.Max(size.y, 0.01f);
            var back = Child("Back" + i, _unitSprite, pos, new Vector3(k * w, k, 1f));
            back.color = Gray;
            var fill = Child("Fill" + i, _unitSprite, pos + new Vector3(0f, 0f, -0.01f), back.transform.localScale);
            Units.Add((back, fill, back.transform.localScale.x));
        }
    }

    private static SpriteRenderer Child(string name, Sprite sp, Vector3 pos, Vector3 scale)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.layer = 5;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }

    // level = 今の満腹度・required = 勝つのに要る満腹度・danger = 近くにキル役 (縁を赤く脈打たせる)
    internal static void Draw(bool show, float level, float required, bool danger)
    {
        if (!_root) return;
        var map = MapBehaviour.Instance;
        bool on = show && !(map && map.IsOpen) && !ExileController.Instance;
        if (_root.activeSelf != on) _root.SetActive(on);
        if (!on) return;
        _clear?.Update();
        _root.transform.localScale = MeetingHud.Instance ? new Vector3(0.8f, 0.8f, 1f) : Vector3.one;
        int fox = level < required ? 0 : 1;
        if (fox != _foxShown) { _foxShown = fox; _fox.sprite = _foxSprites[fox]; }
        for (int i = 0; i < Units.Count; i++)
        {
            var (back, fill, width) = Units[i];
            float part = Mathf.Clamp01(level - i);
            if (part >= 1f) { fill.enabled = true; fill.color = Purple; SetFill(fill, back, 1f, width); continue; }
            if (part <= 0f) { fill.enabled = false; continue; }
            // 今減っているお揚げ: 残りが少ないほど速く点滅し、半分を切ると赤くなる
            bool blink = part < 0.25f ? Mathf.Repeat(Time.time, 0.52f) < 0.26f : part < 0.65f ? Mathf.Repeat(Time.time, 0.8f) < 0.5f : Mathf.Repeat(Time.time, 0.9f) < 0.65f;
            fill.enabled = !blink;
            fill.color = part < 0.5f ? UnityEngine.Color.red : Purple;
            SetFill(fill, back, part, width);
        }
        if (danger)
        {
            float p = Mathf.Sin(Time.time * 6.7f) * 0.2f + 0.6f;
            _bar.color = new Color(0.12f + 0.5f * (1f - p), 0.1f * p, 0.16f * p, 0.95f);
        }
        else _bar.color = new Color(0.12f, 0.1f, 0.16f, 0.9f);
    }

    // 左から part の分だけ見せる (中心が揃った絵なので、縮めた分だけ左へ寄せる)
    private static void SetFill(SpriteRenderer fill, SpriteRenderer back, float part, float width)
    {
        var s = back.transform.localScale;
        fill.transform.localScale = new Vector3(width * part, s.y, 1f);
        var bounds = back.sprite ? back.sprite.bounds.size.x : 1f;
        var p = back.transform.localPosition;
        fill.transform.localPosition = new Vector3(p.x - (1f - part) * width * bounds * 0.5f, p.y, p.z - 0.01f);
    }
}

// 地図 (ふつうの地図のみ・アドミンでは出さない) に皿を映す
internal static class SpectreMapLayer
{
    private static readonly List<SpriteRenderer> Icons = new();
    internal static Func<bool> ShouldShow;

    internal static void Clear()
    {
        foreach (var r in Icons) if (r) UnityEngine.Object.Destroy(r.gameObject);
        Icons.Clear();
    }

    internal static void Update(MapBehaviour map)
    {
        if (!map || !map.HerePoint || !ShipStatus.Instance) return;
        bool admin = map.countOverlay && map.countOverlay.gameObject.activeSelf;
        bool show = ShouldShow != null && ShouldShow() && !admin;
        if (Icons.Count != SpectreDishes.All.Count || Icons.Count > 0 && !Icons[0])
        {
            Clear();
            foreach (var d in SpectreDishes.All)
            {
                var go = new GameObject("MrpSpectreDishMap");
                go.transform.SetParent(map.HerePoint.transform.parent, false);
                go.layer = map.HerePoint.gameObject.layer;
                var sr = go.AddComponent<SpriteRenderer>();
                Icons.Add(sr);
            }
        }
        for (int i = 0; i < Icons.Count; i++)
        {
            var r = Icons[i];
            if (!r) continue;
            r.gameObject.SetActive(show);
            if (!show) continue;
            var d = SpectreDishes.All[i];
            r.sprite = SpectreDishes.DishSprite(!d.Ate);
            r.color = d.Ate ? new Color(1f, 1f, 1f, 0.55f) : Color.white;
            Vector3 pos = d.At;
            pos /= ShipStatus.Instance.MapScale;
            pos.x *= Mathf.Sign(ShipStatus.Instance.transform.localScale.x);
            pos.z = -1.5f;
            r.transform.localPosition = pos;
            r.transform.localScale = Vector3.one * 0.6f;
        }
    }
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
internal static class SpectreMapPatch
{
    public static void Postfix(MapBehaviour __instance)
    {
        if (SpectreDishes.All.Count == 0) return;
        try { SpectreMapLayer.Update(__instance); }
        catch (Exception e) { Plugin.Logger.LogWarning($"spectre map: {e.Message}"); }
    }
}

// 透視: 自分がスペクター (設定) の間、視界の影を作らない (壁越しに見える)。
// 影の絵は毎フレーム描き直す物なので、本編の描画を止めると一度も描かれず全面が影になる。
// 本編に壁込みで描かせた後、壁の無い状態で同じ半径の光を描き直して上書きする
[HarmonyPatch(typeof(LightSourceGpuRenderer), nameof(LightSourceGpuRenderer.GPUShadows))]
internal static class SpectreClairvoyanceGpuPatch
{
    public static void Postfix(LightSourceGpuRenderer __instance)
    {
        if (!Spectre.LocalClairvoyant) return;
        var ls = __instance.lightSource;
        if (!ls) return;
        __instance.ClearEdges();
        __instance.UpdateOccMesh();
        __instance.DrawOcclusion(ls.viewDistance);
    }
}

// 壁を探す光線の当たり先を空にして本編に描かせる (光は壁を抜けた丸になる)。描いた後に元へ戻す
[HarmonyPatch(typeof(LightSourceRaycastRenderer), nameof(LightSourceRaycastRenderer.RaycastShadows))]
internal static class SpectreClairvoyanceRaycastPatch
{
    private static ContactFilter2D _saved;

    public static void Prefix(LightSourceRaycastRenderer __instance, out bool __state)
    {
        __state = Spectre.LocalClairvoyant;
        if (!__state) return;
        _saved = __instance.filter;
        var none = _saved;
        none.useLayerMask = true;
        none.layerMask = 0;
        __instance.filter = none;
    }

    public static void Postfix(LightSourceRaycastRenderer __instance, bool __state)
    {
        if (__state) __instance.filter = _saved;
    }
}
