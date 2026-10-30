using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 空間の裂け目: 居合の段 3〜4 の斬線が空間の裂け目として残る。裂け目の左右の景色は線に沿って逆向きにずれて見える (RiftView)。
// ずれは見た目だけで、立っている位置や当たり判定は変わらない。裂け目をまたいで立つと、体が裂け目で分かれて見える。
// 線はホストが作って全員へ写す。戻り方は斬った扉と同じ設定
internal static class SpaceRifts
{
    private static ChoiceOpt Restore => Neutral.Samurai.CutRestore;
    private static FloatOpt RestoreSeconds => Neutral.Samurai.CutRestoreTime;

    private const int MaxRifts = 8;        // 同時に残る線 (超えたら古い物から消す)
    private const float MaxShift = 1.5f;   // 左右のずれの合計の上限 (線の長さの 1/6 まで)

    internal struct Rift
    {
        public byte Id, By;
        public float X0, Y0, Ux, Uy, Len, Shift;
    }

    private static readonly List<Rift> Rifts = new();
    private static readonly Dictionary<byte, float> RestoreAt = new(); // ホストだけ
    private static readonly List<byte> Due = new();
    private static Lifespan _match;
    private static byte _nextId;
    internal static int Cuts; // 計器: 作った数

    internal static bool Any => Rifts.Count > 0;
    internal static List<Rift> All => Rifts; // 読むだけ (景色の帯)

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
        RiftArt.Clear();
        RiftView.Clear();
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
        var rf = new Rift { Id = m.Id, By = m.By, X0 = m.X0, Y0 = m.Y0, Ux = dx / len, Uy = dy / len, Len = len, Shift = FxMath.Min(len / 6f, MaxShift) };
        Rifts.Add(rf);
        RiftArt.Open(rf);
    }

    private static int IndexOf(byte id)
    {
        for (int i = 0; i < Rifts.Count; i++)
            if (Rifts[i].Id == id) return i;
        return -1;
    }

    // 毎フレーム (Plugin)
    internal static void Tick() => HostTick();

    // ---- 確かめ用 ----

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"rifts={Rifts.Count} cuts={Cuts} restore={Restore.Value}");
        foreach (var rf in Rifts)
            sb.Append($" #{rf.Id}:{rf.X0:0.00},{rf.Y0:0.00}->{rf.X0 + rf.Ux * rf.Len:0.00},{rf.Y0 + rf.Uy * rf.Len:0.00} len={rf.Len:0.00} shift={rf.Shift:0.00}");
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
