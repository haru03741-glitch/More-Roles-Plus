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
    private static Sprite _plate;
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

    internal static Sprite DishSprite(bool full)
    {
        _dishSprites ??= ButtonIcons.Frames("spectre_dish", 2, 100f);
        if (_dishSprites != null) return _dishSprites[full ? 1 : 0];
        // 絵が無い時: 白い皿 (楕円)。盛った時は上にお揚げの絵を重ねる (MakeArt)
        return _plate ? _plate : _plate = FxSprites.Rect(96, 64, (x, y) => Ellipse(x, y * 1.5f, 0.5f), "MrpSpectreDish");
    }

    private static float Ellipse(float x, float y, float r)
    {
        float d = FxMath.Sqrt(x * x + y * y) / r;
        return d >= 1f ? 0f : d > 0.85f ? 1f : 0.9f;
    }

    private static SpriteRenderer MakeArt(Vector2 at)
    {
        var go = new GameObject("MrpSpectreDish");
        go.transform.position = new Vector3(at.x, at.y, at.y / 1000f + 0.002f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = DishSprite(false);
        var top = new GameObject("Fries");
        top.transform.SetParent(go.transform, false);
        top.transform.localPosition = new Vector3(0f, 0.05f, -0.001f);
        var fr = top.AddComponent<SpriteRenderer>();
        fr.sprite = SpectreMinigame.FriesSprite(0);
        top.transform.localScale = Vector3.one * 0.35f;
        top.SetActive(false);
        return sr;
    }

    private static void Draw(Dish d)
    {
        if (!d.Art) return;
        bool full = !d.Ate;
        d.Art.sprite = DishSprite(full);
        // 皿の絵が用意されていない時は、上にお揚げの絵を重ねて盛ったことを見せる
        var top = d.Art.transform.childCount > 0 ? d.Art.transform.GetChild(0).gameObject : null;
        if (top) top.SetActive(full && _dishSprites == null);
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
        d.Ate = ate;
        d.Respawn = Spectre.Replenish;
        Draw(d);
        if (ate) SpectreMinigame.CloseIf(id);
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
    private static Sprite _bg;
    private static readonly Sprite[] _fallback = new Sprite[3];
    private static float _closeAt = -1f;

    internal static bool IsOpen => _root;

    // お揚げの絵 (3 段: 0 = まるごと・1 = かじった・2 = 残り少し)
    internal static Sprite FriesSprite(int stage)
    {
        _friesSprites ??= ButtonIcons.Frames("spectre_fries", 3, 100f);
        if (_friesSprites != null) return _friesSprites[Mathf.Clamp(stage, 0, 2)];
        stage = Mathf.Clamp(stage, 0, 2);
        if (_fallback[stage]) return _fallback[stage];
        // 絵が無い時: 角の丸いきつね色の四角。かじった分だけ右から欠ける
        float cut = 0.5f - stage * 0.22f;
        return _fallback[stage] = FxSprites.Rect(128, 96, (x, y) =>
        {
            float ax = Mathf.Abs(x) - 0.38f, ay = Mathf.Abs(y) - 0.3f;
            float r = 0.1f;
            float d = FxMath.Sqrt(Mathf.Max(ax, 0f) * Mathf.Max(ax, 0f) + Mathf.Max(ay, 0f) * Mathf.Max(ay, 0f));
            if (d > r) return 0f;
            // かじった跡 (丸い歯形)
            float bx = x - cut, by = y;
            if (stage > 0 && bx * bx + by * by < 0.06f) return 0f;
            if (x > cut + 0.12f) return 0f;
            return 1f;
        }, "MrpSpectreFries" + stage);
    }

    private static Sprite Bg()
    {
        var s = ButtonIcons.Has("spectre_minigame") ? ButtonIcons.Get("spectre_minigame") : null;
        if (s) return s;
        return _bg ? _bg : _bg = FxSprites.Rect(300, 200, (x, y) =>
        {
            float ax = Mathf.Abs(x) - 0.46f, ay = Mathf.Abs(y) - 0.44f;
            float d = FxMath.Sqrt(Mathf.Max(ax, 0f) * Mathf.Max(ax, 0f) + Mathf.Max(ay, 0f) * Mathf.Max(ay, 0f));
            return d > 0.04f ? 0f : 1f;
        }, "MrpSpectreBg");
    }

    internal static void Open(int dishId, Action onDone)
    {
        Close();
        var hud = Vanilla.Hud;
        var lp = PlayerControl.LocalPlayer;
        if (!hud || !lp) return;
        _dish = dishId;
        _onDone = onDone;
        _closeAt = -1f;
        _root = new GameObject("MrpSpectreMinigame");
        _root.transform.SetParent(hud.transform, false);
        _root.transform.localPosition = new Vector3(0f, 0f, -60f);
        RoleState.Match.Bind(_root);

        var bg = Child("Bg", Bg(), new Vector3(0f, 0f, 0f), Vector3.one * (ButtonIcons.Has("spectre_minigame") ? 1f : 4.2f));
        if (!ButtonIcons.Has("spectre_minigame")) bg.color = new Color(0.16f, 0.12f, 0.2f, 0.95f);
        var plate = Child("Plate", SpectreDishes.DishSprite(false), new Vector3(0f, -0.15f, -0.1f), Vector3.one * 5f);
        plate.color = new Color(0.95f, 0.95f, 0.95f, 1f);
        for (int i = 0; i < 3; i++)
        {
            Stage[i] = 0;
            Bites[i] = 0;
            Fries[i] = Child("Fries" + i, FriesSprite(0), new Vector3(-1.1f + 1.1f * i, -0.1f + (i == 1 ? 0.2f : 0f), -0.2f - 0.01f * i), Vector3.one * 1.5f);
            if (_friesSprites == null) Fries[i].color = new Color(0.86f, 0.6f, 0.25f, 1f);
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

    internal static void CloseIf(int dishId)
    {
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
        // 枠の外を押したら閉じる
        if (Mathf.Abs(l.x) > 2.1f || Mathf.Abs(l.y) > 1.45f) { Close(); return; }
        for (int i = 0; i < 3; i++)
        {
            var f = Fries[i];
            if (!f || !f.gameObject.activeSelf) continue;
            Vector3 c = f.transform.localPosition;
            if (Mathf.Abs(l.x - c.x) > 0.55f || Mathf.Abs(l.y - c.y) > 0.45f) continue;
            Bite(i);
            return;
        }
    }

    private static void Bite(int i)
    {
        PortSound.Play("spectre_eat", () => PortSound.Fallback("spectre_eat"), 0.8f);
        var f = Fries[i];
        // かじった時に少し揺らす
        f.transform.localScale = Vector3.one * 1.38f;
        PortKit.After(0.06f, () => { if (f) f.transform.localScale = Vector3.one * 1.5f; });
        if (++Bites[i] < 2) return;
        Bites[i] = 0;
        if (++Stage[i] < 3) { f.sprite = FriesSprite(Stage[i]); return; }
        f.gameObject.SetActive(false);
        foreach (var x in Fries) if (x && x.gameObject.activeSelf) return;
        // 全部食べた
        var done = _onDone;
        int dish = _dish;
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
    private static readonly List<(SpriteRenderer back, SpriteRenderer fill, float width)> Units = new();
    private static Sprite _barSprite, _unitSprite;
    private static Sprite[] _foxSprites;
    private static readonly Color Purple = new(202f / 255f, 148f / 255f, 221f / 255f);
    private static readonly Color Gray = new(0.2f, 0.2f, 0.2f, 1f);

    internal static void Ensure(Lifespan life, float max)
    {
        if (_root) return;
        var hud = Vanilla.Hud;
        if (!hud) return;
        _root = new GameObject("MrpSpectreGauge");
        _root.transform.SetParent(hud.transform, false);
        _root.layer = 5;
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

        _foxSprites ??= ButtonIcons.Frames("spectre_gauge_fox", 2, 100f);
        _fox = Child("Fox", _foxSprites != null ? _foxSprites[1] : ButtonIcons.Get("role_Spectre"), new Vector3(0.05f, 0.04f, -0.1f), Vector3.one * (_foxSprites != null ? 0.6f : 0.25f));

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
        _root.transform.localScale = MeetingHud.Instance ? new Vector3(0.8f, 0.8f, 1f) : Vector3.one;
        if (_foxSprites != null) _fox.sprite = _foxSprites[level < required ? 0 : 1];
        else _fox.color = level < required ? new Color(0.6f, 0.6f, 0.6f, 1f) : UnityEngine.Color.white;
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
            r.color = d.Ate ? new Color(1f, 1f, 1f, 0.55f) : RoleDisplay.ParseColor(Spectre.Hex);
            Vector3 pos = d.At;
            pos /= ShipStatus.Instance.MapScale;
            pos.x *= Mathf.Sign(ShipStatus.Instance.transform.localScale.x);
            pos.z = -1.5f;
            r.transform.localPosition = pos;
            r.transform.localScale = Vector3.one * 0.55f;
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

// 透視: 自分がスペクター (設定) の間、視界の影を作らない (壁越しに見える)
[HarmonyPatch(typeof(LightSourceGpuRenderer), nameof(LightSourceGpuRenderer.GPUShadows))]
internal static class SpectreClairvoyanceGpuPatch
{
    public static bool Prefix(LightSourceGpuRenderer __instance)
    {
        if (!Spectre.LocalClairvoyant) return true;
        __instance.ClearEdges();
        return false;
    }
}

[HarmonyPatch(typeof(LightSourceRaycastRenderer), nameof(LightSourceRaycastRenderer.RaycastShadows))]
internal static class SpectreClairvoyanceRaycastPatch
{
    public static bool Prefix() => !Spectre.LocalClairvoyant;
}
