// Helpers for the roles ported from https://github.com/TeamUchuaddon/UchuAddon (role idea and logic; UchuAddon allows porting them, no assets used)
using System;
using System.Collections.Generic;
using System.Linq;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// Uchu から移した役職で共通に使う物
public static class UchuKit
{
    private static readonly List<Func<bool>> Ticks = new();
    private static object _match;

    // 自分の端末で毎 FixedUpdate に呼ぶ。false を返すと止まる。試合が終わると捨てる
    public static void Every(Func<bool> tick)
    {
        if (!ReferenceEquals(_match, RoleState.Match))
        {
            _match = RoleState.Match;
            RoleState.Match.OnRelease(() => { Ticks.Clear(); _match = null; });
        }
        Ticks.Add(tick);
    }

    internal static void Tick()
    {
        for (int i = Ticks.Count - 1; i >= 0; i--)
        {
            bool keep;
            try { keep = Ticks[i](); }
            catch (Exception e) { Plugin.Logger.LogError($"UchuKit.Every: {e}"); keep = false; }
            if (!keep && i < Ticks.Count) Ticks.RemoveAt(i);
        }
        Hidden.Tick();
    }

    // 自分の画面に、その場所への矢印を seconds 秒出す (会議で消える)
    public static void Arrow(Vector2 at, Color color, float seconds)
    {
        var arrow = new WorldArrow(RoleState.Match, color);
        float end = Time.time + seconds;
        Every(() =>
        {
            if (!arrow.Alive) return false;
            if (Time.time > end || MeetingHud.Instance || !ShipStatus.Instance) { arrow.Destroy(); return false; }
            arrow.Update(new Vector3(at.x, at.y, 0f));
            return true;
        });
    }

    // その人への矢印 (相手が動いても追う)
    public static void ArrowTo(PlayerControl target, Color color, float seconds)
    {
        var arrow = new WorldArrow(RoleState.Match, color);
        float end = Time.time + seconds;
        Every(() =>
        {
            if (!arrow.Alive) return false;
            if (Time.time > end || !target || MeetingHud.Instance || !ShipStatus.Instance) { arrow.Destroy(); return false; }
            arrow.Update(target.transform.position);
            return true;
        });
    }

    // 試合に出る役職 (確率が 0 より大きい物) から、その陣営の役職を 1 つ無作為に選ぶ。無ければ陣営の役職全部から。
    // except の役職は避ける (他に無ければ使う)
    internal static RoleBase RandomRole(Team team, string exceptId = null, Func<RoleBase, bool> also = null)
    {
        bool Fits(RoleBase r) => r.BaseTeam == team && !r.MadeByOthers && !r.IsVanilla && !r.AssignedOnDeath && (also == null || also(r));
        var pool = Registry.Roles.Where(r => Fits(r) && r.Appears).ToList();
        if (pool.Count == 0) pool = Registry.Roles.Where(Fits).ToList();
        if (pool.Count == 0) return null;
        var other = pool.Where(r => r.Id != exceptId).ToList();
        if (other.Count > 0) pool = other;
        return pool[UnityEngine.Random.Range(0, pool.Count)];
    }

    // 本当のクルーか (クルー陣営で、インポスター側・第三陣営の勝ちにも付かない)
    public static bool TrueCrew(PlayerControl p) =>
        RoleState.Of(p) is { } r && r.Team == Team.Crew && r.WinsWithTeam == null && r.WinsWithFaction == null && !PortKit.IsImpostor(p);

    internal static Color ParseColor(string hex) => RoleDisplay.ParseColor(hex);

    // 試合の間ずっと、決めた人にだけ見えない人 (インビジブル・ボイジャー)。
    // hidden(その人) が true の間、自分の端末でその人を消す。本人には薄く見せる
    public static class Hidden
    {
        private sealed class Entry { public Func<bool> On; public Func<bool> SelfSees; public bool Gone; public float Alpha = 1f; }
        private static readonly Dictionary<byte, Entry> All = new();
        private static object _m;

        public static void Set(byte id, Func<bool> on, Func<bool> selfSees = null)
        {
            if (!ReferenceEquals(_m, RoleState.Match))
            {
                _m = RoleState.Match;
                RoleState.Match.OnRelease(Restore);
            }
            All[id] = new Entry { On = on, SelfSees = selfSees };
        }

        public static bool IsHiddenFromMe(byte id) => All.TryGetValue(id, out var e) && e.Gone;

        internal static void Tick()
        {
            if (All.Count == 0) return;
            var lp = PlayerControl.LocalPlayer;
            if (!lp) return;
            bool ghost = lp.Data != null && lp.Data.IsDead;
            bool meeting = MeetingHud.Instance || ExileController.Instance || !ShipStatus.Instance;
            foreach (var kv in All)
            {
                var p = PortKit.ById(kv.Key);
                var e = kv.Value;
                if (!p) continue;
                bool on = !meeting && PortKit.Alive(p) && SafeOn(e);
                bool mine = p.AmOwner;
                float want = !on ? 1f : mine ? (e.SelfSees == null || e.SelfSees() ? 0.35f : 1f) : ghost || PortKit.IsImpostor(lp) ? 0.35f : 0f;
                e.Alpha = Mathf.MoveTowards(e.Alpha, want, Time.fixedDeltaTime * 2f);
                if (want == 0f && e.Alpha <= 0f)
                {
                    if (!e.Gone && p.Visible) { p.Visible = false; e.Gone = true; }
                    continue;
                }
                if (e.Gone) { e.Gone = false; if (!p.inVent) p.Visible = true; }
                PortKit.SetOpacity(p, e.Alpha);
            }
        }

        private static bool SafeOn(Entry e)
        {
            try { return e.On(); } catch { return false; }
        }

        private static void Restore()
        {
            foreach (var kv in All)
            {
                var p = PortKit.ById(kv.Key);
                if (!p) continue;
                if (kv.Value.Gone && !p.inVent) p.Visible = true;
                PortKit.SetOpacity(p, 1f);
            }
            All.Clear();
            _m = null;
        }
    }

    // ---- 地図 ----

    // 自分の地図を開く (会議中も開ける)
    public static void OpenMap()
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (MapBehaviour.Instance && MapBehaviour.Instance.IsOpen) return;
        hud.ToggleMapVisible(new MapOptions { Mode = MapOptions.Modes.Normal });
    }

    // 地図の上の位置 (HerePoint の親の中の位置) ⇔ 船の位置
    public static Vector3 ToMap(Vector2 world, float z)
    {
        var ship = ShipStatus.Instance;
        Vector3 pos = world / ship.MapScale;
        pos.x *= Mathf.Sign(ship.transform.localScale.x);
        pos.z = z;
        return pos;
    }

    // このフレームで開いている地図を押したなら、その船の位置
    public static Vector2? MapClick()
    {
        if (!Input.GetMouseButtonDown(0)) return null;
        var map = MapBehaviour.Instance;
        var cam = Camera.main;
        if (!map || !map.IsOpen || !map.HerePoint || !ShipStatus.Instance || !cam) return null;
        var parent = map.HerePoint.transform.parent;
        Vector3 local = parent.InverseTransformPoint(cam.ScreenToWorldPoint(Input.mousePosition));
        var ship = ShipStatus.Instance;
        return new Vector2(local.x * Mathf.Sign(ship.transform.localScale.x), local.y) * ship.MapScale;
    }

    // 地図の上のその人の色のピン (地図の HerePoint の写し)。地図が閉じると見えなくなる (地図の子)
    public static SpriteRenderer MapPin(MapBehaviour map, PlayerControl owner, Vector2 world)
    {
        var parent = map.HerePoint.transform.parent;
        var r = UnityEngine.Object.Instantiate(map.HerePoint, parent);
        r.name = "MrpMapPin";
        r.material.SetInt(PlayerMaterial.MaskLayer, 255);
        if (owner) owner.SetPlayerMaterialColors(r);
        r.transform.localPosition = ToMap(world, -1.2f);
        r.gameObject.SetActive(true);
        return r;
    }
}
