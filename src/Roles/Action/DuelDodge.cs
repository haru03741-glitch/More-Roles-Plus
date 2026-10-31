using System;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 立ち合いの相手の手 = 回避。立ち合いの間だけ、サムライでない側が短く飛び退ける (PC = 回避のキー (既定 Shift・Space は本編の「使う」と重なる)・スマホ = 右の縁のボタン)。
// 飛び退く向きは歩いている向き (止まっていれば相手から離れる向き)。MaxStock 回ぶんまで溜められ、1 回ぶんは Recharge 秒で溜まる
// (立ち合いに入った時は満タン)。続けて使う時は Gap 秒空ける。
// ホストは回避を受け取ってから少しの間、その人への斬りを当てない (無敵)。飛び退いた跡には全員の画面で残像が残る
internal static class DuelDodge
{
    internal const int MaxStock = 2;
    internal const float Recharge = 3f, Gap = 0.4f;
    private const float HostSlack = 0.3f;   // ホストの数え方の余裕 (届くまでの遅れの揺れ・固まって届く分)
    internal const float Speed = 13f;
    private const float MoveTime = 0.18f;
    private const float Safe = 0.35f;       // ホストが受け取ってから当てない秒
    private const float TrailTime = 0.26f;  // 残像を置く秒
    private const byte None = 255;

    // 自分の端末
    private static bool _on; // 自分が組にいて回避を持つ
    // 溜まり具合 = _stock (時刻 _stockAt の値) + 経った秒 / Recharge (MaxStock まで)
    private static float _stock, _stockAt, _lastUse = -10f;
    private static float _moveUntil = -10f;
    private static bool _moveOn;
    private static Vector2 _dir;
    private static bool _usable;
    internal static int Used, Refused;
    internal static float TestSafe; // 確かめ用: 無敵の秒を伸ばす (0 = 既定)
    // ホスト
    private static readonly float[] HostStock = new float[256], HostStockAt = new float[256], HostLastUse = new float[256];
    private static readonly float[] SafeUntil = new float[256];
    private static readonly float[] SafeFrom = new float[256];
    // 残像を置いている人 (全員の端末)
    private static readonly byte[] TrailId = new byte[8];
    private static readonly float[] TrailUntil = new float[8];
    private static readonly float[] TrailLast = new float[8];
    private static int _trails;
    private static bool _hooked;

    internal static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Duel.OnJoin += OnJoin;
        Duel.OnLeave += OnLeave;
    }

    private static byte LocalId => PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : None;

    // 回避を使えるのはサムライでない側 (サムライは踏み込みを持つ)
    private static bool CanHave(byte id) => RoleState.Of(id) is not Neutral.Samurai;

    private static float StockAt(float stock, float at, float now) => FxMath.Min(MaxStock, stock + (now - at) / Recharge);

    // 自分の今の溜まり具合 (0〜MaxStock)
    internal static float Stock => StockAt(_stock, _stockAt, Time.time);

    // 組に入るたびに満タンにする (組同士の合流で移った時は今のまま)
    private static void OnJoin(byte id, byte g, bool fresh)
    {
        if (Duel.Moved) return;
        float now = Time.time;
        HostStock[id] = MaxStock;
        HostStockAt[id] = now;
        HostLastUse[id] = -10f;
        SafeUntil[id] = 0f;
        if (id != LocalId || !CanHave(id)) return;
        _on = true;
        _stock = MaxStock;
        _stockAt = now;
        _lastUse = -10f;
    }

    // ホスト: この人が今回避を使えるか (使えたら 1 回ぶん減らす)
    private static bool HostUse(byte id, float now)
    {
        float cur = StockAt(HostStock[id], HostStockAt[id], now);
        if (cur < 1f - HostSlack || now - HostLastUse[id] < Gap - HostSlack) return false;
        HostStock[id] = FxMath.Max(cur - 1f, 0f);
        HostStockAt[id] = now;
        HostLastUse[id] = now;
        return true;
    }

    private static void OnLeave(byte id, byte g)
    {
        // 組同士の合流で移っただけ (移った先に入れてから呼ばれる)
        if (Duel.InDuel(id)) return;
        if (id != LocalId) return;
        _on = false;
        _usable = false;
        DodgePad.Hide();
    }

    // ホスト: この人は今、斬りを避けているか
    internal static bool HostSafe(byte id) => Time.time < SafeUntil[id];

    // 時刻 t (ホストの時計) に無敵だったか (刀が通った時刻で見る・回避が刀より後なら当たる)
    internal static bool HostSafeAt(byte id, float t) => t >= SafeFrom[id] && t < SafeUntil[id];

    // 毎フレーム。立ち合い中でも残像も無ければ比較だけで帰る
    internal static void Tick()
    {
        if (_trails > 0) TickTrails();
        if (!_on) return;
        var p = PlayerControl.LocalPlayer;
        if (!p || p.Data == null || p.Data.IsDead || !Duel.InDuel(p.PlayerId))
        {
            _on = false;
            DodgePad.Hide();
            return;
        }
        // 動けるか (毎フレームの読みを減らすため 4 フレームに 1 回)
        if (Time.frameCount % 4 == 0) _usable = p.CanMove && !p.inVent && !MeetingHud.Instance;
        float now = Time.time, stock = StockAt(_stock, _stockAt, now);
        bool ready = stock >= 1f && now - _lastUse >= Gap;
        bool pad = DodgePad.Tick(stock, _usable && ready, now);
        // 吹っ飛ばされている間の回避のキーは受け身 (回避の回数は使わない)
        if (DuelTumble.Busy(p.PlayerId))
        {
            bool tech = pad && DodgePad.Pressed()
                        || !System.OperatingSystem.IsAndroid() && Menu.Keybinds.Down(Menu.Keybinds.Slot.Dodge);
            if (tech) DuelTumble.TryTech(p);
            return;
        }
        if (!_usable || !ready) return;
        bool press = pad && DodgePad.Pressed();
        if (!press && !System.OperatingSystem.IsAndroid() && Menu.Keybinds.Down(Menu.Keybinds.Slot.Dodge))
        {
            var hud = Vanilla.Hud;
            press = !(hud && hud.Chat && hud.Chat.IsOpenOrOpening) && !Dev.DevConsole.IsOpen;
        }
        if (press) Dodge(p);
    }

    // 同じ組で一番近い、生きている人
    private static PlayerControl Nearest(PlayerControl p)
    {
        byte g = Duel.GroupOf(p.PlayerId);
        Vector3 me = p.transform.position;
        PlayerControl best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < Duel.CountOf(g); i++)
        {
            byte id = Duel.MemberOf(g, i);
            var o = id == p.PlayerId || !GameData.Instance ? null : GameData.Instance.GetPlayerById(id)?.Object;
            if (!o || o.Data == null || o.Data.IsDead) continue;
            Vector3 at = o.transform.position;
            float dx = at.x - me.x, dy = at.y - me.y, d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = o; }
        }
        return best;
    }

    private static void Dodge(PlayerControl p)
    {
        var v = p.MyPhysics ? p.MyPhysics.body.velocity : default;
        float dx = v.x, dy = v.y;
        if (dx * dx + dy * dy < 0.01f)
        {
            // 止まっていれば一番近い相手から離れる向き
            var opp = Nearest(p);
            Vector3 me = p.transform.position;
            Vector3 o = opp ? opp.transform.position : me;
            dx = me.x - o.x;
            dy = me.y - o.y;
            if (dx * dx + dy * dy < 1e-4f) { dx = p.cosmetics && p.cosmetics.FlipX ? 1f : -1f; dy = 0f; }
        }
        float l = FxMath.Sqrt(dx * dx + dy * dy);
        _dir = FxMath.V2(dx / l, dy / l);
        _moveUntil = Time.time + MoveTime;
        _moveOn = true;
        float now = Time.time;
        _stock = FxMath.Max(StockAt(_stock, _stockAt, now) - 1f, 0f);
        _stockAt = now;
        _lastUse = now;
        Used++;
        DodgePad.OnUse();
        byte ang = AngleByte(_dir.x, _dir.y);
        DodgeCall.Send(ang);
        Apply(p.PlayerId, ang);
    }

    // ホスト: 練習の相手 (ダミー) が回避した。使えたら true (動かすのは呼んだ側)
    internal static bool BotDodge(byte id, float dx, float dy)
    {
        if (!Duel.InDuel(id) || !CanHave(id) || !HostUse(id, Time.time)) return false;
        byte ang = AngleByte(dx, dy);
        Apply(id, ang);
        BotDodgeCall.Send((ushort)(id | ang << 8));
        return true;
    }

    // 飛び退く向き (256 分割の角度)
    private static byte AngleByte(float dx, float dy) => (byte)((int)MathF.Round(FxMath.Atan2(dy, dx) * FxMath.Rad2Deg / 360f * 256f) & 0xFF);

    internal static float BotDodgeTime => MoveTime;

    // 値 = 回避した人 | 向き << 8
    private static readonly RemoteCall<ushort> BotDodgeCall = new("Duel.BotDodge", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadUInt16(), (_, v) => StartTrail((byte)v, (byte)(v >> 8)));

    // 回避した (全員の端末で・送った本人とホスト自身は手元で)。値 = 向き
    private static readonly RemoteCall<byte> DodgeCall = new("Duel.Dodge", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadByte(), (sender, ang) => { if (sender) Apply(sender.PlayerId, ang); }, perSecond: 6);

    private static void Apply(byte id, byte ang)
    {
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost)
        {
            // ボットの回避は BotDodge が数え終えている
            if (Duel.InDuel(id) && CanHave(id) && (DuelBot.Has(id) || HostUse(id, Time.time)))
            {
                SafeFrom[id] = Time.time;
                SafeUntil[id] = Time.time + (TestSafe > 0f ? TestSafe : Safe);
            }
            else Refused++;
        }
        StartTrail(id, ang);
    }

    private static void StartTrail(byte id, byte ang)
    {
        float ar = ang * (360f / 256f) * FxMath.Deg2Rad;
        DuelTumble.Roll(id, FxMath.Cos(ar), FxMath.Sin(ar));
        int slot = -1;
        for (int i = 0; i < _trails; i++)
            if (TrailId[i] == id) slot = i;
        if (slot < 0)
        {
            if (_trails >= TrailId.Length) return;
            slot = _trails++;
        }
        TrailId[slot] = id;
        TrailUntil[slot] = Time.time + TrailTime;
        SamuraiSound.Dodge(GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null);
        TrailLast[slot] = -10f;
    }

    private static void TickTrails()
    {
        float now = Time.time;
        var gd = GameData.Instance;
        for (int i = _trails - 1; i >= 0; i--)
        {
            if (now >= TrailUntil[i] || !gd)
            {
                _trails--;
                TrailId[i] = TrailId[_trails];
                TrailUntil[i] = TrailUntil[_trails];
                TrailLast[i] = TrailLast[_trails];
                continue;
            }
            if (now - TrailLast[i] < 1f / 30f) continue;
            var info = gd.GetPlayerById(TrailId[i]);
            var pc = info != null ? info.Object : null;
            if (!pc || !pc.Visible) continue;
            int color = pc.CurrentOutfit != null ? pc.CurrentOutfit.ColorId : info.DefaultOutfit.ColorId;
            if (SamuraiBlade.GhostOf(pc, color, false, now)) TrailLast[i] = now;
        }
    }

    internal static bool Moving(out Vector2 dir)
    {
        dir = _dir;
        if (!_moveOn) return false;
        if (Time.time < _moveUntil) return true;
        _moveOn = false;
        return false;
    }

    // 確かめ用
    internal static bool ForTest()
    {
        var p = PlayerControl.LocalPlayer;
        if (!_on || !p || Stock < 1f) return false;
        Dodge(p);
        return true;
    }

    internal static string Describe() =>
        $"on={_on} stock={Stock:0.00} usable={_usable} used={Used} refused={Refused} pad={DodgePad.Shown} trails={_trails}";
}

// 回避: 本編の歩行の後で、短い間だけ体の速さを飛び退く向きへ書き換える (壁は物理が止める)
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
internal static class DuelDodgeMovePatch
{
    public static void Postfix(PlayerPhysics __instance)
    {
        if (!DuelDodge.Moving(out var dir) || !__instance.AmOwner) return;
        var pc = __instance.myPlayer;
        if (!pc || !pc.CanMove || pc.inVent) return;
        __instance.body.velocity = FxMath.V2(dir.x * DuelDodge.Speed, dir.y * DuelDodge.Speed);
    }
}
