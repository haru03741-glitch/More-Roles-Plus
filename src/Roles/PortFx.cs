using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 移した役職が使う体の効果: 足の速さ・透明化・画面の引き。どれも試合が終わると戻る

// 自分の足の速さに掛ける倍率 (自分の端末だけ。他の人には本編の位置の同期で届く)。同じ鍵は置き換え
public static class MoveSpeed
{
    private struct Entry
    {
        public string Key;
        public float Mul, Until;
    }

    private static readonly List<Entry> _list = new();
    private static object _match;

    internal static bool Any => _list.Count != 0;

    // seconds 秒の間 mul 倍にする (0 で止まる)
    public static void Add(string key, float mul, float seconds)
    {
        if (!ReferenceEquals(_match, RoleState.Match))
        {
            _match = RoleState.Match;
            RoleState.Match.OnRelease(() => { _list.Clear(); _match = null; });
        }
        Remove(key);
        _list.Add(new Entry { Key = key, Mul = mul, Until = Time.time + seconds });
    }

    public static void Remove(string key) => _list.RemoveAll(e => e.Key == key);

    internal static float Factor()
    {
        float now = Time.time, f = 1f;
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            if (now >= _list[i].Until) { _list.RemoveAt(i); continue; }
            f *= _list[i].Mul;
        }
        return f;
    }
}

[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.TrueSpeed), MethodType.Getter)]
internal static class MoveSpeedPatch
{
    public static void Postfix(PlayerPhysics __instance, ref float __result)
    {
        if (!MoveSpeed.Any) return;
        var p = __instance.myPlayer;
        if (!p || !p.AmOwner) return;
        __result *= MoveSpeed.Factor();
    }
}

// 透明化。透明な人は他の人の画面では薄れて消え (名前も)、狙えなくなる。本人と幽霊には薄く見える。
// 始め・終わりは本人の端末から全員へ配り、各端末が手元で薄くする。会議が始まると全部切れる
public static class Cloak
{
    private const float Fade = 0.5f;        // 消える・現れるのにかかる秒
    private const float SelfAlpha = 0.35f;  // 本人と幽霊に見える濃さ

    private sealed class State
    {
        public float Until, Alpha = 1f;
        public bool Hidden; // 本編の Visible を消した
        public bool On;     // 始めと終わりの音を 1 回ずつ鳴らすための今の状態 (時間切れと終わりの電文のどちらが先でも)
    }

    private static readonly Dictionary<byte, State> _all = new();
    private static object _match;

    public static bool IsOn(byte id) => _all.TryGetValue(id, out var s) && s.Until > Time.time;

    // 自分の端末から: 自分を seconds 秒だけ透明にする (0 以下で今やめる)
    public static void Self(float seconds)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        Call.Send(seconds);
        Apply(lp.PlayerId, seconds);
    }

    private static readonly RemoteCall<float> Call = new("Cloak.Set", Route.Anyone,
        (w, s) => w.Write(s), r => r.ReadSingle(),
        (sender, s) => { if (sender) Apply(sender.PlayerId, s); });

    private static void Apply(byte id, float seconds)
    {
        if (!ReferenceEquals(_match, RoleState.Match))
        {
            _match = RoleState.Match;
            RoleState.Match.OnRelease(RestoreAll);
        }
        if (!_all.TryGetValue(id, out var s)) _all[id] = s = new State();
        s.Until = seconds > 0f ? Time.time + seconds : 0f;
        Turn(id, s, seconds > 0f);
    }

    private static void Turn(byte id, State s, bool on)
    {
        if (s.On == on) return;
        s.On = on;
        Neutral.Spectre.OnCloak(id, on);
    }

    // 毎 FixedUpdate (RoleFrame.Tick から)
    internal static void Tick()
    {
        if (_all.Count == 0) return;
        if (MeetingHud.Instance || !ShipStatus.Instance) { RestoreAll(); return; }
        var lp = PlayerControl.LocalPlayer;
        bool ghost = lp && lp.Data != null && lp.Data.IsDead;
        float now = Time.time, step = Time.fixedDeltaTime / Fade;
        List<byte> done = null;
        foreach (var kv in _all)
        {
            var p = PortKit.ById(kv.Key);
            var s = kv.Value;
            if (!p) { (done ??= new()).Add(kv.Key); continue; }
            bool on = s.Until > now && PortKit.Alive(p);
            if (!on && s.On) Turn(kv.Key, s, false);
            float want = !on ? 1f : p.AmOwner || ghost ? SelfAlpha : 0f;
            s.Alpha = Mathf.MoveTowards(s.Alpha, want, step);
            if (on && want == 0f && s.Alpha <= 0f)
            {
                if (!s.Hidden && p.Visible) { p.Visible = false; s.Hidden = true; }
                continue;
            }
            if (s.Hidden) { s.Hidden = false; if (!p.inVent) p.Visible = true; }
            PortKit.SetOpacity(p, s.Alpha);
            if (!on && s.Alpha >= 1f) (done ??= new()).Add(kv.Key);
        }
        if (done != null) foreach (var id in done) _all.Remove(id);
    }

    private static void RestoreAll()
    {
        foreach (var kv in _all)
        {
            var p = PortKit.ById(kv.Key);
            if (!p) continue;
            if (kv.Value.Hidden && !p.inVent) p.Visible = true;
            PortKit.SetOpacity(p, 1f);
        }
        _all.Clear();
        _match = null;
    }
}

// 自分の画面を引く (見える範囲を k 倍に広げる)。影と HUD のカメラも同じだけ広げる。会議と試合の終わりで戻る
public static class ViewScale
{
    private const float BaseSize = 3f;
    private static float _k = 1f, _want = 1f;
    private static Vector3 _shadowBase;
    private static Transform _shadow;

    public static void Set(float k) => _want = Mathf.Max(k, 1f);

    internal static void Tick()
    {
        if (MeetingHud.Instance || !ShipStatus.Instance) _want = 1f;
        if (_k == _want) return;
        _k = Mathf.MoveTowards(_k, _want, Time.fixedDeltaTime * 1.5f);
        var cam = Camera.main;
        if (cam) cam.orthographicSize = BaseSize * _k;
        var hud = Vanilla.Hud;
        // HUD の端に寄せる部品は世界のカメラの大きさで位置を決めるので、HUD のカメラも同じだけ引いて置き直す
        // (引かないと HUD が画面の外へずれる。Zoom と同じ作り)
        if (hud && hud.UICamera) hud.UICamera.orthographicSize = BaseSize * _k;
        Zoom.Relayout();
        var q = hud && hud.ShadowQuad ? hud.ShadowQuad.transform : null;
        if (q)
        {
            if (_shadow != q) { _shadow = q; _shadowBase = q.localScale; }
            q.localScale = _shadowBase * _k;
        }
    }
}
