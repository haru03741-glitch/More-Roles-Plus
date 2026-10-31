using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 刀で斬った扉は壊れて閉まらなくなる (扉のサボタージュも効かない)。どの扉が壊れているかはホストが持ち、全員へ写す。
// 閉まらないようにするのは本編の扉の SetDoorway (型ごとに別の override) の前置きで、閉める呼び出しを開ける呼び出しに変える。
// ホストのサボタージュも客の同期の受け取り (Deserialize) も同じ所を通るので、ここ 1 か所で全員の扉が開いたままになる。
// 壊れている間は扉の絵を消し、戻る時 (設定: 次の会議 / 秒数 / ずっと) に絵を戻す (扉は開いたまま)
internal static class CutDoors
{
    private static ChoiceOpt Restore => Neutral.Samurai.CutRestore;
    private static FloatOpt RestoreSeconds => Neutral.Samurai.CutRestoreTime;

    private static int ShipMask => Constants.ShipAndObjectsMask;

    // 壊れている扉 (ship.AllDoors の添字) と、ホストだけが持つ戻す時刻 (秒数の設定の時)
    private static readonly HashSet<int> Broken = new();
    private static readonly Dictionary<int, float> RestoreAt = new();
    private static readonly List<int> Due = new();
    private static Lifespan _match;
    internal static int Cuts, Restored, Blocked; // 計器: 壊した数・戻した数・閉めるのを止めた数

    internal static bool Any => Broken.Count > 0;

    // サムライが付いた時に (全員の端末で)。試合ごとに 1 回だけ
    internal static void Use()
    {
        var match = RoleState.Match;
        if (_match == match && !match.IsDead) return;
        _match = match;
        Broken.Clear();
        RestoreAt.Clear();
        DoorCutFx.Clear();
        Events<MeetingStartEvent>.Subscribe(_ => OnMeeting(), match, owner: "CutDoors");
        match.OnRelease(() =>
        {
            Broken.Clear();
            RestoreAt.Clear();
            DoorCutFx.Clear();
        });
    }

    internal static bool IsBroken(OpenableDoor door) => Broken.Count > 0 && door && Broken.Contains(IndexOf(door));

    private static int IndexOf(OpenableDoor door)
    {
        var all = ShipStatus.Instance ? ShipStatus.Instance.AllDoors : null;
        if (all == null) return -1;
        IntPtr p = door.Pointer;
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && all[i].Pointer == p) return i;
        return -1;
    }

    // 斬れる扉か (除染室の扉は本編が順に開け閉めするので斬らない)
    private static bool Cuttable(OpenableDoor d) => d && d.Room != SystemTypes.Decontamination;

    // 扉の当たり判定 (絵の割り方と当たりの距離に使う)
    internal static Collider2D ColliderOf(OpenableDoor d)
    {
        var plain = d.TryCast<PlainDoor>();
        if (plain != null) return plain.myCollider;
        var mush = d.TryCast<MushroomWallDoor>();
        return mush != null ? mush.wallCollider : null;
    }

    // 扉の真ん中。開いた扉は当たり判定が止まっていて bounds が空になるので、形の中心を transform から出す
    internal static Vector2 CenterOf(Collider2D col)
    {
        Vector3 c = col.transform.TransformPoint(FxMath.V3(col.offset.x, col.offset.y, 0f));
        return FxMath.V2(c.x, c.y);
    }

    // ---- ホスト ----

    // 一振りの扇 (from から a0 度の向きへ sweep 度・reach まで) に刀が通った扉を壊す。thrust = 突き (a0 の向きへ細く)。
    // 扉は長いので真ん中の 1 点でなく扉の線の上を何点か見て、刀の届く所に入った点があれば斬る
    internal static void HostCut(Vector2 from, float a0, float sweep, float reach, bool thrust, byte by)
    {
        var ship = ShipStatus.Instance;
        var all = ship ? ship.AllDoors : null;
        if (all == null) return;
        float span = FxMath.Abs(sweep);
        float tr = a0 * FxMath.Deg2Rad, tc = FxMath.Cos(tr), tsn = FxMath.Sin(tr);
        for (int i = 0; i < all.Length; i++)
        {
            var d = all[i];
            if (Broken.Contains(i) || !Cuttable(d)) continue;
            var col = ColliderOf(d);
            if (!col) continue;
            SpanOf(col, out Vector2 e0, out Vector2 e1);
            // 真ん中から外へ順に見る (端は隣の壁に接していて間の壁の確かめに掛かりやすい)
            for (int k = 0; k < Along.Length; k++)
            {
                float s = Along[k];
                float px = (e0.x + e1.x) * 0.5f + (e1.x - e0.x) * 0.5f * s, py = (e0.y + e1.y) * 0.5f + (e1.y - e0.y) * 0.5f * s;
                float dx = px - from.x, dy = py - from.y;
                if (dx * dx + dy * dy > reach * reach) continue;
                float ta = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;
                if (thrust)
                {
                    float along = dx * tc + dy * tsn, side = FxMath.Abs(dx * tsn - dy * tc);
                    if (along <= 0f || side > ThrustHalfWidth) continue;
                }
                else
                {
                    float off = (sweep >= 0f ? ta - a0 : a0 - ta) % 360f;
                    if (off < 0f) off += 360f;
                    if (off > span + ArcPad && off < 360f - ArcPad) continue;
                }
                // 間に別の壁があれば届かない (最初に当たったのが当の扉なら通す)
                if (WallBetween(from, dx, dy, col, d)) continue;
                // 割る線 = 斬った刀の通り道 (扉の所での刀の向き)。扇の振りなら相手への向きに直角、突きならその向き
                HostBreak(i, thrust ? ta : ta + 90f, by);
                break;
            }
        }
    }

    private const float ArcPad = 15f;          // 扇の両端に足す余裕 (度・人の当たりと同じ)
    private const float ThrustHalfWidth = 0.3f; // 突きの筋の太さの半分
    private static readonly float[] Along = { 0f, -0.3f, 0.3f, -0.6f, 0.6f, -0.85f, 0.85f };

    // 扉の線の両端。箱の当たり判定なら長い向きの両端、それ以外は真ん中の 1 点
    private static void SpanOf(Collider2D col, out Vector2 a, out Vector2 b)
    {
        float hx = 0f, hy = 0f;
        var box = col.TryCast<BoxCollider2D>();
        if (box != null)
        {
            Vector2 sz = box.size;
            if (sz.x >= sz.y) hx = sz.x * 0.5f;
            else hy = sz.y * 0.5f;
        }
        Vector2 o = col.offset;
        var t = col.transform;
        Vector3 pa = t.TransformPoint(FxMath.V3(o.x - hx, o.y - hy, 0f));
        Vector3 pb = t.TransformPoint(FxMath.V3(o.x + hx, o.y + hy, 0f));
        a = FxMath.V2(pa.x, pa.y);
        b = FxMath.V2(pb.x, pb.y);
    }

    // from から扉までの間に、その扉でない壁があるか (Linecast は Android の本体に無いので細い円で掃く)
    private static bool WallBetween(Vector2 from, float dx, float dy, Collider2D col, OpenableDoor d)
    {
        float len = FxMath.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) return false;
        foreach (var h in Physics2D.CircleCastAll(from, 0.03f, FxMath.V2(dx / len, dy / len), len, ShipMask))
        {
            var c = h.collider;
            if (!c || c.isTrigger || c.Pointer == col.Pointer || IsPartOf(c, d)) continue;
            return true;
        }
        return false;
    }

    private static bool IsPartOf(Collider2D c, OpenableDoor d)
    {
        var od = c.GetComponentInParent<OpenableDoor>();
        return od && od.Pointer == d.Pointer;
    }

    private static void HostBreak(int i, float cutDeg, byte by)
    {
        if (!Broken.Add(i)) return;
        Cuts++;
        if (Restore.Value == 1) RestoreAt[i] = Time.time + RestoreSeconds.Value;
        int ang = ((int)MathF.Round(cutDeg) % 360 + 360) % 360;
        int v = i & 0xFF | ang << 8 | 1 << 17 | by << 18;
        DoorCall.Send(v);
        Apply(v);
        OpenNow(i);
    }

    // ホスト: 戻す時刻の来た扉を戻す (毎フレーム・サムライが死んでも回す)
    internal static void HostTick()
    {
        if (RestoreAt.Count == 0 || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        float now = Time.time;
        Due.Clear();
        foreach (var kv in RestoreAt)
            if (now >= kv.Value) Due.Add(kv.Key);
        foreach (int i in Due) HostRestore(i);
    }

    private static void HostRestore(int i)
    {
        RestoreAt.Remove(i);
        if (!Broken.Contains(i)) return;
        int v = i & 0xFF;
        DoorCall.Send(v);
        Apply(v);
    }

    private static void OnMeeting()
    {
        if (Restore.Value != 0 || Broken.Count == 0 || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        Due.Clear();
        Due.AddRange(Broken);
        foreach (int i in Due) HostRestore(i);
    }

    // 閉まっている扉をすぐ開けて、本編の同期で客へ配る
    private static void OpenNow(int i)
    {
        var ship = ShipStatus.Instance;
        var d = ship.AllDoors[i];
        if (!d || d.IsOpen) return;
        var plain = d.TryCast<PlainDoor>();
        if (plain != null) plain.SetDoorway(true);
        else d.TryCast<MushroomWallDoor>()?.SetDoorway(true);
        if (ship.Systems.ContainsKey(SystemTypes.Doors)) TimeRewind.MarkSystemDirty(ship.Systems[SystemTypes.Doors]);
    }

    // ---- 全員の端末 ----

    // 値 = 扉の添字 | 割る線の角度 (0〜359) << 8 | 壊す (1) / 戻す (0) << 17 | 斬った人 << 18
    private static readonly RemoteCall<int> DoorCall = new("Samurai.Door", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadInt32(), (_, v) => Apply(v));

    private static void Apply(int v)
    {
        Use();
        var ship = ShipStatus.Instance;
        int i = v & 0xFF;
        if (!ship || ship.AllDoors == null || i >= ship.AllDoors.Length) return;
        var d = ship.AllDoors[i];
        if (!d) return;
        if ((v >> 17 & 1) != 0)
        {
            Broken.Add(i);
            DoorCutFx.Break(d, (v >> 8) & 0x1FF, (byte)(v >> 18 & 0xFF));
        }
        else
        {
            Broken.Remove(i);
            Restored++;
            DoorCutFx.Mend(d);
        }
    }

    // ---- 確かめ用 ----

    internal static string Describe()
    {
        var ship = ShipStatus.Instance;
        int n = ship && ship.AllDoors != null ? ship.AllDoors.Length : 0;
        return $"doors={n} broken={Broken.Count}[{string.Join(",", Broken)}] cuts={Cuts} restored={Restored} blocked={Blocked} restore={Restore.Value} fx[{DoorCutFx.Describe()}]";
    }

    internal static string List()
    {
        var ship = ShipStatus.Instance;
        if (!ship || ship.AllDoors == null) return "no ship";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < ship.AllDoors.Length; i++)
        {
            var d = ship.AllDoors[i];
            if (!d) continue;
            var col = ColliderOf(d);
            Vector2 c = col ? CenterOf(col) : (Vector2)d.transform.position;
            string kind = d.TryCast<AutoOpenDoor>() != null ? "auto" : d.TryCast<PlainDoor>() != null ? "plain" : d.TryCast<MushroomWallDoor>() != null ? "mush" : "?";
            sb.Append($"{i}:{kind}:{d.Room}@{c.x:0.00},{c.y:0.00}{(d.IsOpen ? "" : ":closed")}{(Broken.Contains(i) ? ":broken" : "")} ");
        }
        return sb.ToString();
    }

    // 確かめ用: 添字の扉を手で壊す / 戻す (ホストだけ)
    internal static bool ForTest(int i, bool breakIt, float cutDeg)
    {
        var ship = ShipStatus.Instance;
        if (!ship || ship.AllDoors == null || i < 0 || i >= ship.AllDoors.Length || !AmongUsClient.Instance.AmHost) return false;
        if (breakIt) HostBreak(i, cutDeg, PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : (byte)0);
        else HostRestore(i);
        return true;
    }
}

// 壊れた扉は閉めない。型ごとに override があるので板の扉と茸の扉の両方に当てる (基底に当てても override は通らない)。
// Skeld の自動で開く扉 (AutoOpenDoor) は自前の SetDoorway が小さく、当てると開く時にプロセスごと落ちた (実測)。
// 中で板の扉の SetDoorway を呼ぶので、板の扉の方で止まる
[HarmonyPatch]
internal static class CutDoorsKeepOpenPatch
{
    [HarmonyPatch(typeof(PlainDoor), nameof(PlainDoor.SetDoorway)), HarmonyPrefix]
    public static void Plain(PlainDoor __instance, ref bool open) => Keep(__instance, ref open);

    [HarmonyPatch(typeof(MushroomWallDoor), nameof(MushroomWallDoor.SetDoorway)), HarmonyPrefix]
    public static void Mushroom(MushroomWallDoor __instance, ref bool open) => Keep(__instance, ref open);

    private static void Keep(OpenableDoor door, ref bool open)
    {
        if (open || !CutDoors.Any || !CutDoors.IsBroken(door)) return;
        open = true;
        CutDoors.Blocked++;
    }
}
