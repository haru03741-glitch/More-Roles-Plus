using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 空間のずれ: 居合の段 3〜4 の斬線が床に残り、線を歩いて跨ぐと線に沿って横にずれた所へ出る (全員・斬った本人も)。
// 線の左側の空間が線に沿って Shift だけ滑った形にする: 右から左へ跨ぐと +Shift、左から右へ跨ぐと -Shift。跨ぎ直せば元の所へ戻る。
// ずれた先が壁・家具の中・別の島・線の範囲の外なら、ずれを縮めて床のある所へ寄せる (どこも無ければ普通に跨ぐ)。
// 線はホストが作って全員へ写す。跨いだかは各端末が自分の足元で見て、自分の位置を自分で動かす (位置は本編の同期と Teleport で届く)
internal static class SpaceRifts
{
    private static ChoiceOpt Restore => Neutral.Samurai.CutRestore;
    private static FloatOpt RestoreSeconds => Neutral.Samurai.CutRestoreTime;

    private const int MaxRifts = 8;        // 同時に残る線 (超えたら古い物から消す)
    private const float MaxShift = 3f;     // ずれの上限 (線の長さの 1/3 まで)
    private const float ShiftStep = 0.1f;  // 寄せる時に縮める刻み
    private const float MinShift = 0.3f;   // これより小さくしか寄せられなければずらさない
    private const float EndPad = 0.1f;     // 線の端からこれ以内には出さない
    private const float Body = 0.2f;       // 着く所に要る体の半径
    private const float MaxStep = 1.5f;    // 1 フレームにこれ以上動いたのは跨ぎでなく飛んだ (Teleport・ベント)
    private const float PassGap = 0.25f;   // 跨いだ直後は数えない (飛んだ先で同じ線を拾わない)
    private const float Straddle = 0.35f;  // 線ができた時にこれより近くに立っていた人は、一度離れるまでその線で数えない

    internal struct Rift
    {
        public byte Id, By;
        public float X0, Y0, Ux, Uy, Len, Shift;
        public bool Grace; // 自分の端末だけ: 線ができた時に線の上に立っていた (斬った本人は線の上から駆け抜ける)
    }

    private static readonly List<Rift> Rifts = new();
    private static readonly Dictionary<byte, float> RestoreAt = new(); // ホストだけ
    private static readonly List<byte> Due = new();
    private static Lifespan _match;
    private static byte _nextId;
    private static bool _hasPrev;
    private static float _prevX, _prevY, _quietUntil;
    internal static int Cuts, Passes, Pinned; // 計器: 作った数・ずれた数・寄せられず普通に跨いだ数
    internal static string LastPass = "-";

    internal static bool Any => Rifts.Count > 0;

    // サムライが付いた時に (全員の端末で)。試合ごとに 1 回だけ
    internal static void Use()
    {
        var match = RoleState.Match;
        if (match == null || _match == match && !match.IsDead) return;
        _match = match;
        Reset();
        Events<MeetingStartEvent>.Subscribe(_ => OnMeeting(), match, owner: "SpaceRifts");
        match.OnRelease(Reset);
    }

    private static void Reset()
    {
        Rifts.Clear();
        RestoreAt.Clear();
        _hasPrev = false;
        RiftArt.Clear();
    }

    // ---- ホスト ----

    // 居合の段 3〜4 を振った: from (斬った人の足元) を通り deg の向きの線を、壁と物まで伸ばして残す
    internal static void HostCut(Vector2 from, float deg, int tier, byte by)
    {
        if (tier < 3) return;
        float r = deg * FxMath.Deg2Rad, dx = FxMath.Cos(r), dy = FxMath.Sin(r);
        // 斬線の見た目と同じ長さ (段 4 は画面の端まで届く長さの代わりに 8)
        float wantF = tier >= 4 ? 8f : 4.5f, wantB = tier >= 4 ? 8f : 1.6f;
        float fwd = SamuraiBlade.WallDist(from.x, from.y, dx, dy, wantF, out _);
        float back = SamuraiBlade.WallDist(from.x, from.y, -dx, -dy, wantB, out _);
        float len = fwd + back;
        if (len < 1f) return;
        while (Rifts.Count >= MaxRifts) HostRemove(Rifts[0].Id);
        byte id = _nextId++;
        float x0 = from.x - dx * back, y0 = from.y - dy * back;
        var m = new Msg(id, by, true, x0, y0, from.x + dx * fwd, from.y + dy * fwd);
        if (Restore.Value == 1) RestoreAt[id] = Time.time + RestoreSeconds.Value;
        Cuts++;
        RiftCall.Send(m);
        Apply(m);
    }

    // ホスト: 戻す時刻の来た線を消す (毎フレーム)
    internal static void HostTick()
    {
        if (RestoreAt.Count == Rifts.Count && RestoreAt.Count == 0 || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        // ホストが替わった: 前のホストの戻す時刻は届いていないので、残っている線をここから数え直す
        if (RestoreAt.Count < Rifts.Count && Restore.Value == 1)
            foreach (var rf in Rifts)
                RestoreAt.TryAdd(rf.Id, Time.time + RestoreSeconds.Value);
        if (RestoreAt.Count == 0) return;
        float now = Time.time;
        Due.Clear();
        foreach (var kv in RestoreAt)
            if (now >= kv.Value) Due.Add(kv.Key);
        foreach (byte id in Due) HostRemove(id);
    }

    private static void HostRemove(byte id)
    {
        RestoreAt.Remove(id);
        var m = new Msg(id, 0, false, 0f, 0f, 0f, 0f);
        RiftCall.Send(m);
        Apply(m);
    }

    private static void OnMeeting()
    {
        // 会議の後は湧く場所へ飛ぶので、明けてしばらくは跨ぎを数えない
        _hasPrev = false;
        _quietUntil = float.MaxValue;
        if (Restore.Value != 0 || Rifts.Count == 0 || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        Due.Clear();
        foreach (var rf in Rifts) Due.Add(rf.Id);
        foreach (byte id in Due) HostRemove(id);
    }

    // ---- 全員の端末 ----

    private readonly struct Msg
    {
        public readonly byte Id, By;
        public readonly bool Open;
        public readonly float X0, Y0, X1, Y1;
        public Msg(byte id, byte by, bool open, float x0, float y0, float x1, float y1)
        {
            Id = id; By = by; Open = open; X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
        }
    }

    // 線を開く (両端) / 閉じる
    private static readonly RemoteCall<Msg> RiftCall = new("Samurai.Rift", Route.HostToAll,
        (w, m) =>
        {
            w.Write(m.Id);
            w.Write(m.By);
            w.Write(m.Open);
            if (!m.Open) return;
            w.Write(m.X0); w.Write(m.Y0); w.Write(m.X1); w.Write(m.Y1);
        },
        r =>
        {
            byte id = r.ReadByte(), by = r.ReadByte();
            bool open = r.ReadBoolean();
            return open ? new Msg(id, by, true, r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) : new Msg(id, by, false, 0f, 0f, 0f, 0f);
        },
        (_, m) => Apply(m));

    private static void Apply(in Msg m)
    {
        Use();
        int at = IndexOf(m.Id);
        if (at >= 0)
        {
            Rifts.RemoveAt(at);
            RiftArt.Close(m.Id);
        }
        if (!m.Open) return;
        if (!float.IsFinite(m.X0) || !float.IsFinite(m.Y0) || !float.IsFinite(m.X1) || !float.IsFinite(m.Y1)) return;
        float dx = m.X1 - m.X0, dy = m.Y1 - m.Y0, len = FxMath.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f || len > 40f) return;
        var rf = new Rift { Id = m.Id, By = m.By, X0 = m.X0, Y0 = m.Y0, Ux = dx / len, Uy = dy / len, Len = len, Shift = FxMath.Min(len / 3f, MaxShift) };
        var lp = PlayerControl.LocalPlayer;
        if (lp)
        {
            Vector2 f = lp.GetTruePosition();
            float t = (f.x - rf.X0) * rf.Ux + (f.y - rf.Y0) * rf.Uy;
            float side = rf.Ux * (f.y - rf.Y0) - rf.Uy * (f.x - rf.X0);
            rf.Grace = FxMath.Abs(side) < Straddle && t > -Straddle && t < len + Straddle;
        }
        Rifts.Add(rf);
        RiftArt.Open(rf);
    }

    private static int IndexOf(byte id)
    {
        for (int i = 0; i < Rifts.Count; i++)
            if (Rifts[i].Id == id) return i;
        return -1;
    }

    // 毎フレーム (Plugin)。線が無ければ何もしない
    internal static void Tick()
    {
        HostTick();
        if (Rifts.Count == 0) { _hasPrev = false; return; }
        var lp = PlayerControl.LocalPlayer;
        if (!lp) { _hasPrev = false; return; }
        Vector2 f = lp.GetTruePosition();
        float px = _prevX, py = _prevY;
        bool had = _hasPrev;
        _prevX = f.x;
        _prevY = f.y;
        _hasPrev = true;
        if (_quietUntil == float.MaxValue)
        {
            if (MeetingHud.Instance || ExileController.Instance) return;
            _quietUntil = Time.time + 1f;
        }
        if (!had || Time.time < _quietUntil || Teleport.Pending) return;
        float mx = f.x - px, my = f.y - py, step = mx * mx + my * my;
        if (step < 1e-8f || step > MaxStep * MaxStep) return;
        for (int i = 0; i < Rifts.Count; i++)
        {
            var rf = Rifts[i];
            // 左 (線の向きから反時計回り) が正
            float s0 = rf.Ux * (py - rf.Y0) - rf.Uy * (px - rf.X0);
            float s1 = rf.Ux * (f.y - rf.Y0) - rf.Uy * (f.x - rf.X0);
            if (rf.Grace)
            {
                if (FxMath.Abs(s1) > Straddle) { rf.Grace = false; Rifts[i] = rf; }
                continue;
            }
            if (s0 < 0f == s1 < 0f) continue;
            float a = s0 / (s0 - s1);
            float t = (px + mx * a - rf.X0) * rf.Ux + (py + my * a - rf.Y0) * rf.Uy;
            if (t < 0f || t > rf.Len) continue;
            Cross(lp, f, rf, t, s1 >= 0f ? 1f : -1f);
            return;
        }
    }

    // 自分が線を跨いだ: 線に沿って sign 向きへ、着ける所までずらす
    private static void Cross(PlayerControl lp, Vector2 f, in Rift rf, float t, float sign)
    {
        if (lp.Data == null || lp.Data.IsDead || lp.inVent || lp.onLadder || lp.inMovingPlat || lp.walkingToVent || Neutral.Samurai.LeapActive || MeetingHud.Instance) return;
        int home = SolidMap.Valid ? SolidMap.IslandNear(f.x, f.y, 0.5f) : 0;
        for (float s = rf.Shift; s >= MinShift; s -= ShiftStep)
        {
            float t1 = t + s * sign;
            if (t1 < EndPad || t1 > rf.Len - EndPad) continue;
            float tx = f.x + rf.Ux * s * sign, ty = f.y + rf.Uy * s * sign;
            if (!Arrivable(tx, ty, home)) continue;
            Vector3 root = lp.transform.position;
            Teleport.Self(FxMath.V2(root.x + tx - f.x, root.y + ty - f.y));
            _prevX = tx;
            _prevY = ty;
            _quietUntil = Time.time + PassGap;
            Passes++;
            LastPass = $"rift={rf.Id} from={f.x:0.00},{f.y:0.00} to={tx:0.00},{ty:0.00} shift={s * sign:0.00}/{rf.Shift:0.00}";
            float deg = FxMath.Atan2(rf.Uy, rf.Ux) * FxMath.Rad2Deg;
            RiftArt.Pass(f.x, f.y, tx, ty, deg);
            PassCall.Send((byte)(rf.Id));
            return;
        }
        Pinned++;
        LastPass = $"rift={rf.Id} pinned at t={t:0.00}";
    }

    // 体が収まり、壁や家具に埋まらず、跨いだ所と同じ島 (崖の下などへ出ない) か
    private static bool Arrivable(float x, float y, int home)
    {
        if (SolidMap.Valid)
        {
            if (SolidMap.Solid(x, y) || SolidMap.Solid(x - Body, y) || SolidMap.Solid(x + Body, y) || SolidMap.Solid(x, y - Body) || SolidMap.Solid(x, y + Body)) return false;
            if (home != 0 && SolidMap.IslandNear(x, y, 0.3f) != home) return false;
        }
        return !Neutral.Samurai.Buried(FxMath.V2(x, y));
    }

    // 誰かがずれた (本人 → 全員)。見た目だけ (位置は本編の同期で届く)
    private static readonly RemoteCall<byte> PassCall = new("Samurai.RiftPass", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadByte(),
        (sender, v) => { if (sender) RiftArt.Flicker(v); },
        perSecond: 8);

    // ---- 確かめ用 ----

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"rifts={Rifts.Count} cuts={Cuts} passes={Passes} pinned={Pinned} last[{LastPass}] restore={Restore.Value}");
        foreach (var rf in Rifts)
            sb.Append($" #{rf.Id}{(rf.Grace ? "(grace)" : "")}:{rf.X0:0.00},{rf.Y0:0.00}->{rf.X0 + rf.Ux * rf.Len:0.00},{rf.Y0 + rf.Uy * rf.Len:0.00} len={rf.Len:0.00} shift={rf.Shift:0.00}");
        return sb.ToString();
    }

    // 確かめ用: ホストで (x, y) を通る deg の向きの線を段 tier で作る / 全部消す
    internal static bool ForTest(float x, float y, float deg, int tier)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return false;
        Use();
        HostCut(FxMath.V2(x, y), deg, tier, PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : (byte)0);
        return true;
    }

    internal static bool ClearForTest()
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return false;
        Due.Clear();
        foreach (var rf in Rifts) Due.Add(rf.Id);
        foreach (byte id in Due) HostRemove(id);
        return true;
    }
}
