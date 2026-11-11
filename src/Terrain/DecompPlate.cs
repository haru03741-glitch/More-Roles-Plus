using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 鉄板 (アルケミストが瓦礫から作る仕掛け壁) で外壁の穴の口をふさぐ。立った・壊れたは地形の出来事の順で全員に届くので、
// その刻みに積んで全員が同じ刻みで口を閉じ・開け直す (指紋が割れない)。口の線が途中の壁の塊で分かれていると 1 枚では
// 届かないので、口ごとに覆った鉄板を覚えておき、合わせて口の幅の大半を覆えばふさがる。鉄板は泡と違って剥がせず、
// ふさいでいた鉄板が壊れると口が開き直して泡の時間も最初から数え直す。時戻しは剥がした記録と同じ列に積んだ記録で戻す
internal static partial class Decompression
{
    // 口を覆った鉄板 1 枚 (名前・真ん中・傾いた板を囲む四角の半分)
    internal readonly struct PlateBox
    {
        public readonly string Name;
        public readonly Vector2 C, Half;
        public PlateBox(string name, Vector2 c, Vector2 half) { Name = name; C = c; Half = half; }
    }

    // 全員 (仕掛け壁の鉄板が立った時・適用の順で): その刻みに、覆った開いた口に数える
    internal static void OnPlate(int tick, string name, Vector2 center, Vector2 half)
    {
        if (!_running) return;
        Enqueue(new Pending { Tick = tick, Plate = true, Name = name, At = center, Half = half });
    }

    // 全員 (鉄板が壊れた時・適用の順で): その刻みに、その鉄板が覆っていた口から外す
    internal static void OnPlateBroken(int tick, string name, byte actor)
    {
        if (!_running) return;
        Enqueue(new Pending { Tick = tick, Unplate = true, Name = name, Actor = actor });
    }

    private static bool PlateAt(in Pending p)
    {
        bool any = false;
        var box = new PlateBox(p.Name, p.At, p.Half);
        for (int i = 0; i < Breaches.Count; i++)
        {
            var br = Breaches[i];
            if (br.Sealed || CoverOf(i, box) <= 0f) continue;
            br.Strips.Add(Snapshot(br));
            br.Cover.Add(box);
            float ratio = CoverRatio(i, br.Cover);
            if (ratio >= CoverShare)
            {
                br.Sealed = true;
                br.Plate = p.Name;
                FoamArt.Forget(br); // 膨らみかけの泡は鉄板の裏で消す
                any = true;
            }
            Plugin.Logger.LogInfo($"[Decompression] plate {p.Name} covers mouth {i} {ratio:0.00}{(br.Sealed ? " sealed" : "")} step={_step}");
        }
        return any;
    }

    private static bool UnplateAt(in Pending p)
    {
        bool any = false;
        string name = p.Name;
        for (int i = 0; i < Breaches.Count; i++)
        {
            var br = Breaches[i];
            int k = br.Cover.FindIndex(c => string.Equals(c.Name, name, StringComparison.Ordinal));
            if (k < 0) continue;
            br.Strips.Add(Snapshot(br));
            br.Cover.RemoveAt(k);
            if (br.Plate == null || CoverRatio(i, br.Cover) >= CoverShare) continue; // 泡でふさがった口・残りの鉄板でまだ覆えている口はそのまま
            br.Sealed = false;
            br.Plate = null;
            br.Start = _step;
            if (p.Actor != ResolvedDamage.NoActor) br.Actor = p.Actor;
            Plugin.Logger.LogInfo($"[Decompression] plate {p.Name} broken, mouth {i} reopens step={_step}");
            DecompFx.OnBreach(_step, br.Lines, br.W256 / 256f, br.Away);
            any = true;
        }
        return any;
    }

    private static StripRec Snapshot(Breach br) => new()
    {
        Tick = _step, PrevStart = br.Start, PrevActor = br.Actor, Exact = true,
        PrevSealed = br.Sealed, PrevPlate = br.Plate, PrevCover = br.Cover.ToArray(),
    };

    // 口 b の線の長さのうち、鉄板の四角のどれかが覆っている割合
    private static float CoverRatio(int b, List<PlateBox> boxes)
    {
        float total = 0f, covered = 0f;
        var spans = new List<(float Lo, float Hi)>();
        foreach (var (a, e) in Breaches[b].Lines)
        {
            float dx = e.x - a.x, dy = e.y - a.y, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f) continue;
            total += len;
            spans.Clear();
            foreach (var box in boxes)
                if (Span(a, dx / len, dy / len, len, box, out float lo, out float hi)) spans.Add((lo, hi));
            spans.Sort((x, y) => x.Lo.CompareTo(y.Lo));
            float end = 0f;
            foreach (var (lo, hi) in spans)
            {
                float from = MathF.Max(lo, end);
                if (hi > from) { covered += hi - from; end = hi; }
            }
        }
        return total > 0f ? covered / total : 0f;
    }

    private static float CoverOf(int b, in PlateBox box) => CoverRatio(b, new List<PlateBox> { box });

    // 線 (始点 a・向き u・長さ len) のうち四角が覆う区間 (線に触れていなければ false)
    private static bool Span(Vector2 a, float ux, float uy, float len, in PlateBox box, out float lo, out float hi)
    {
        float along = box.Half.x * MathF.Abs(ux) + box.Half.y * MathF.Abs(uy);
        float across = box.Half.x * MathF.Abs(uy) + box.Half.y * MathF.Abs(ux);
        float rx = box.C.x - a.x, ry = box.C.y - a.y;
        float t = rx * ux + ry * uy;
        lo = MathF.Max(0f, t - along);
        hi = MathF.Min(len, t + along);
        return MathF.Abs(rx * -uy + ry * ux) - across <= CoverTouch && hi > lo;
    }

    // 鉄板を立てる所の候補: ふさがっていない口の線のうち、鉄板でまだ覆っていない物を点 p に近い順に (線の真ん中・線の向き・宇宙への向き)
    internal static List<(Vector2 Mid, Vector2 Along, Vector2 Away)> OpenMouthsNear(Vector2 p, float reach)
    {
        var found = new List<(float D, Vector2 Mid, Vector2 Along, Vector2 Away)>();
        if (_running)
            foreach (var br in Breaches)
            {
                if (br.Sealed) continue;
                foreach (var (a, b) in br.Lines)
                {
                    float d = SegDist(p, a, b), len = (b - a).magnitude;
                    if (d > reach || len < 0.05f) continue;
                    Vector2 u = (b - a) / len, mid = (a + b) * 0.5f;
                    bool covered = false;
                    foreach (var box in br.Cover)
                        if (Span(a, u.x, u.y, len, box, out float lo, out float hi) && hi - lo >= len * CoverShare) { covered = true; break; }
                    if (covered) continue;
                    found.Add((d, mid, u, br.Away.sqrMagnitude > 1e-6f ? br.Away.normalized : Vector2.zero));
                }
            }
        found.Sort((x, y) => x.D.CompareTo(y.D));
        var list = new List<(Vector2, Vector2, Vector2)>(found.Count);
        foreach (var f in found) list.Add((f.Mid, f.Along, f.Away));
        return list;
    }
}
