using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 泡でふさがった口を剥がして、もう一度吸い出させる (突き落と師)。ホストが確かめて刻みを押して全員へ配り、
// 全員が同じ刻みで口を開け直す (開通した時と同じく 6 秒後にまた泡が吹き付けられる)。
// 時戻しで剥がす前へ戻ったら、ふさがり・泡の始まり・穴の相手・視界の線・泡の絵を元へ戻す
internal static partial class Decompression
{
    internal const float StripReach = 1.2f;   // 体から口の線までこの距離以内なら剥がせる
    private const float StripHostReach = 2.0f; // ホストが確かめる距離 (位置の同期の遅れの分の余裕)

    // 剥がした記録 (時戻しで戻す用・口ごと)
    internal sealed class StripRec
    {
        public int Tick, PrevStart;
        public byte PrevActor;
    }

    // 剥がせる口があるか (ボタンの判定で毎フレーム呼ばれるので Unity の算術を使わない)
    internal static bool CanStrip(Vector2 p) => StripTarget(p.x, p.y, StripReach) >= 0;

    // 泡でふさがった口が 1 つでもあるか (自分の位置を読む前に安く断る)
    internal static bool AnySealed
    {
        get
        {
            if (!_running) return false;
            for (int i = 0; i < Breaches.Count; i++)
                if (Breaches[i].Sealed && !Breaches[i].ByProp) return true;
            return false;
        }
    }

    private static int StripTarget(float px, float py, float reach)
    {
        if (!_running) return -1;
        int best = -1;
        float bd = reach * reach;
        for (int i = 0; i < Breaches.Count; i++)
        {
            var br = Breaches[i];
            if (!br.Sealed || br.ByProp) continue;
            foreach (var (a, b) in br.Lines)
            {
                float abx = b.x - a.x, aby = b.y - a.y, len = abx * abx + aby * aby;
                float t = len > 1e-6f ? Math.Clamp(((px - a.x) * abx + (py - a.y) * aby) / len, 0f, 1f) : 0f;
                float qx = a.x + abx * t - px, qy = a.y + aby * t - py;
                float d = qx * qx + qy * qy;
                if (d < bd) { bd = d; best = i; }
            }
        }
        return best;
    }

    // 突き飛ばせる距離 (口への道のり)。気圧の強さは見ない (弱い所から危ない所まで運ぶ)
    internal const int ShoveDist = 6 * PerUnit;

    // 開いている口への道のり (Far = 届かない・ボタンの判定で毎フレーム呼ばれるので Unity の算術を使わない)
    internal static int PathDist(float px, float py)
    {
        if (!_running || !Pulling) return Far;
        int x = CellX(px), y = CellY(py);
        if (x < 1 || y < 1 || x >= _w - 1 || y >= _h - 1) return Far;
        int k = y * _w + x;
        return _comp[k] < 0 ? Far : _dist[k];
    }

    // 口への道の向き (強さは見ない)。道が無ければ false
    internal static bool PathAt(Vector2 p, out Vector2 dir)
    {
        dir = default;
        if (PathDist(p.x, p.y) == Far) return false;
        int k = CellY(p.y) * _w + CellX(p.x);
        int t = k;
        for (int i = 0; i < 3 && _par[t] >= 0; i++) t = _par[t];
        Vector2 to = _par[t] < 0 && _dist[t] == 0 ? NearestMouth(p) : FxMath.V2(_org.x + (t % _w + 0.5f) * _cell, _org.y + (t / _w + 0.5f) * _cell);
        float dx = to.x - p.x, dy = to.y - p.y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-4f) return false;
        dir = FxMath.V2(dx / len, dy / len);
        return true;
    }

    // ふさがった口の線のうち p にいちばん近い点 (剥がす手を置く所)。無ければ false
    internal static bool SealedMouthAt(Vector2 p, float reach, out Vector2 at, out Vector2 away)
    {
        at = away = default;
        int b = StripTarget(p.x, p.y, reach);
        if (b < 0) return false;
        away = Breaches[b].Away;
        float best = float.MaxValue;
        foreach (var (a, e) in Breaches[b].Lines)
        {
            float abx = e.x - a.x, aby = e.y - a.y, len = abx * abx + aby * aby;
            float t = len > 1e-6f ? Math.Clamp(((p.x - a.x) * abx + (p.y - a.y) * aby) / len, 0f, 1f) : 0f;
            float qx = a.x + abx * t, qy = a.y + aby * t, d = (qx - p.x) * (qx - p.x) + (qy - p.y) * (qy - p.y);
            if (d < best) { best = d; at = FxMath.V2(qx, qy); }
        }
        return true;
    }

    // 剥がす人の端末から: ホストへ頼む (一人の時はそのまま)
    internal static void RequestStrip(PlayerControl by)
    {
        if (!by) return;
        var p = by.GetTruePosition();
        if (AmongUsClient.Instance.AmHost) HostStrip(by, p);
        else StripRequest.Send((p.x, p.y));
    }

    private static readonly RemoteCall<(float x, float y)> StripRequest = new("Decomp.StripAsk", Route.ToHost,
        (w, m) => { w.Write(m.x); w.Write(m.y); }, r => (r.ReadSingle(), r.ReadSingle()),
        (sender, m) => { if (sender) HostStrip(sender, new Vector2(m.x, m.y)); });

    private static void HostStrip(PlayerControl by, Vector2 at)
    {
        if (AmongUsClient.Instance.IsGameOver || MeetingHud.Instance || ExileController.Instance) return;
        if (by.Data == null || by.Data.IsDead || by.inVent) return;
        // 頼んだ位置と体の位置のどちらも口の近く (位置の同期が遅れていても、遠くから頼めない)
        var pos = by.GetTruePosition();
        if (StripTarget(pos.x, pos.y, StripHostReach) < 0 || StripTarget(at.x, at.y, StripHostReach) < 0)
        {
            Plugin.Logger.LogWarning($"[Decompression] strip refused {by.PlayerId} at {pos.x:0.00},{pos.y:0.00}");
            return;
        }
        ushort stamp = GameClock.Stamp;
        var m = (stamp, at.x, at.y, by.PlayerId);
        StripCall.Send(m);
        OnStrip(m);
    }

    private static readonly RemoteCall<(ushort tick, float x, float y, byte by)> StripCall = new("Decomp.Strip", Route.HostToAll,
        (w, m) => { w.Write(m.tick); w.Write(m.x); w.Write(m.y); w.Write(m.by); },
        r => (r.ReadUInt16(), r.ReadSingle(), r.ReadSingle(), r.ReadByte()),
        (_, m) => OnStrip(m));

    private static void OnStrip((ushort tick, float x, float y, byte by) m)
    {
        try
        {
            if (!_running) return;
            Enqueue(new Pending { Tick = GameClock.Expand(m.tick), Strip = true, At = new Vector2(m.x, m.y), Actor = m.by });
            Roles.Impostor.PusherFx.Strip(m.by, new Vector2(m.x, m.y));
        }
        catch (Exception e) { Fail("strip", e); }
    }

    // 刻みの中 (StepOnce): 頼んだ位置にいちばん近いふさがった口を開け直す。開けたら true
    private static bool StripAt(Vector2 at, byte actor)
    {
        int b = StripTarget(at.x, at.y, StripHostReach + 0.5f);
        if (b < 0) return false;
        var br = Breaches[b];
        br.Strips.Add(new StripRec { Tick = _step, PrevStart = br.Start, PrevActor = br.Actor });
        br.Sealed = false;
        br.Start = _step;
        br.Actor = actor;
        DropSeal(br);
        FoamArt.Tear(br);
        Plugin.Logger.LogInfo($"[Decompression] strip mouth {b} by {actor} step={_step}");
        DecompFx.OnBreach(_step, br.Lines, br.W256 / 256f, br.Away);
        return true;
    }

    private static void DropSeal(Breach br)
    {
        foreach (var go in br.Seals)
        {
            if (!go) continue;
            go.SetActive(false);
            UnityEngine.Object.Destroy(go);
        }
        br.Seals.Clear();
    }

    // 時戻し (RewindHead の戻す合図): 戻す刻み target 以後に剥がした口を元のふさがりへ戻す
    private static void UnstripAfter(int target)
    {
        foreach (var br in Breaches)
        {
            bool changed = false;
            while (br.Strips.Count > 0 && br.Strips[br.Strips.Count - 1].Tick >= target)
            {
                var s = br.Strips[br.Strips.Count - 1];
                br.Strips.RemoveAt(br.Strips.Count - 1);
                br.Start = s.PrevStart;
                br.Actor = s.PrevActor;
                changed = true;
            }
            if (!changed) continue;
            FoamArt.Forget(br);
            DropSeal(br);
            br.Sealed = _step - br.Start >= FoamDelay + FoamTime;
            if (br.Sealed) SealSight(br);
            _sealDirty = true;
        }
    }
}
