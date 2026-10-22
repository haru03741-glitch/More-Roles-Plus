using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 噴き出しの絵。WaterSim の粒が 1 刻みで動いた線 (前の位置 → 今の位置・高さは上へずらす) を、
// 漏れの口を中心にした小さな絵へ「水の濃さ」と「泡」として描き込み、シェーダ MRP/Water (_Mode = 1) が見た目を決める。
// 粒が密な口の近くは一枚の膜に、まばらな先は筋と粒に見える (形は決め打ちせず粒の動きから出る)。
// 泡 (白さ) = 口を出たばかり・壁に当たって止まった・床に落ちる直前の粒ほど多い。
// 刻みが進んだ時だけ描き直す (毎秒 GameClock.Hz 回)。会議中は隠す (計算は WaterSim が続ける)
internal static class WaterSpray
{
    private const float Ppu = 24f;            // 画素/単位
    private const float Half = 2.4f;          // 口からの範囲 (届く距離 1.5 × 破裂 1.3 より広く)
    private const float Hit = 0.6f;           // 粒の線の真ん中の濃さ (1 本でも細い筋として見える)
    private const float Side = 0.2f, Corner = 0.06f;
    private const float Life = 16f;           // 噴き出し (15 秒) + 落ち切るまで
    private const int MaxSprays = 3;          // 毎刻み全面を描き直すので数を絞る (超えたら古い物から消す)

    private sealed class Spray
    {
        public float Cx, Cy, Age;
        public bool Hidden;
        public GameObject Go;
        public SpriteRenderer Sr;
        public Texture2D Tex;
        public Sprite Sp;
        public Material Mat;
        public byte[] Px;
        public float[] Dens, Foam;
        public int N;
        public readonly int[] Box = { 0, 0, -1, -1 }; // 前の刻みで粒が触った範囲 (C++ が消す・詰め直す所)
    }

    private static readonly List<Spray> Sprays = new();
    private static int _lastStep = -1;
    private static long _lastMs;
    internal static double LastDrawMs { get; private set; }

    // 時戻しで取り消した漏れの噴き出しを消す
    internal static void StopAt(Vector2 at)
    {
        for (int i = Sprays.Count - 1; i >= 0; i--)
        {
            var s = Sprays[i];
            if (MathF.Abs(s.Cx - at.x) >= 0.01f || MathF.Abs(s.Cy - at.y) >= 0.01f) continue;
            Destroy(s);
            Sprays.RemoveAt(i);
        }
    }

    // 噴き出しを見せ始める (WaterLeak から)。dir = 主に噴く向き (流れの筋の向き)
    public static void Start(Vector2 at, Vector2 dir, float z)
    {
        var mat0 = MrpBundle.WaterMaterial;
        if (!mat0) return;
        while (Sprays.Count >= MaxSprays) { Destroy(Sprays[0]); Sprays.RemoveAt(0); }
        int n = (int)(Half * 2f * Ppu);
        var s = new Spray { Cx = at.x, Cy = at.y, N = n, Px = new byte[n * n * 4], Dens = new float[n * n], Foam = new float[n * n] };
        for (int i = 3; i < s.Px.Length; i += 4) s.Px[i] = 255; // 濃さ 0 の画素 (C++ は粒が触った範囲だけ詰め直す)
        s.Tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpSpray", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        s.Sp = Sprite.Create(s.Tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), Ppu, 0, SpriteMeshType.FullRect);
        s.Sp.name = "MrpSpray";
        s.Mat = new Material(mat0) { name = "MrpSpray" };
        s.Mat.SetFloat("_Mode", 1f);
        s.Mat.SetVector("_Flow", new Vector4(dir.x, dir.y, 0f, 0f));
        s.Go = new GameObject("MrpSpray") { layer = 0 };
        s.Sr = s.Go.AddComponent<SpriteRenderer>();
        s.Sr.sprite = s.Sp;
        s.Sr.sharedMaterial = s.Mat;
        s.Go.transform.position = FxMath.V3(at.x, at.y, z);
        Sprays.Add(s);
    }

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[WaterSpray] {e}"); Clear(); }
    }

    private static void TickCore()
    {
        if (Sprays.Count == 0) { _lastMs = 0; return; }
        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.1f, (now - _lastMs) / 1000f);
        _lastMs = now;
        if (!WaterSim.Ready) { Clear(); return; }
        bool meeting = MeetingHud.Instance;
        for (int i = Sprays.Count - 1; i >= 0; i--)
        {
            var s = Sprays[i];
            s.Age += dt;
            if (s.Age >= Life) { Destroy(s); Sprays.RemoveAt(i); continue; }
            if (s.Hidden != meeting) { s.Hidden = meeting; s.Sr.enabled = !meeting; }
        }
        if (meeting || WaterSim.Step == _lastStep) return;
        _lastStep = WaterSim.Step;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var s in Sprays) Draw(s);
        LastDrawMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct SprayArgs
    {
        public int* Ox, Oy, Oh, Px, Py, Ph;
        public byte* Stuck;
        public int Count;
        public float* Dens, Foam;
        public byte* Out;
        public int N;
        public float OrgX, OrgY, X0, Y0;
        public int* Box;
    }

    internal static long VerifyDraws, VerifyDrawDiff; // 答え合わせ (native verify): C# でも描いて画素を 1 バイトずつ比べる

    // 絵は C++ で描く (粒 1024 × 噴き出し 3 枚を刻みごとに描き直す・消すのと詰めるのは粒が触った範囲だけ)
    private static unsafe void Draw(Spray s)
    {
        var org = WaterSim.Origin;
        fixed (int* ox = WaterSim.Ox) fixed (int* oy = WaterSim.Oy) fixed (int* oh = WaterSim.Oh)
        fixed (int* px = WaterSim.Px) fixed (int* py = WaterSim.Py) fixed (int* ph = WaterSim.Ph) fixed (bool* st = WaterSim.Stuck)
        fixed (float* dens = s.Dens) fixed (float* foam = s.Foam) fixed (byte* outPx = s.Px) fixed (int* box = s.Box)
        {
            var a = new SprayArgs
            {
                Ox = ox, Oy = oy, Oh = oh, Px = px, Py = py, Ph = ph, Stuck = (byte*)st, Count = WaterSim.Particles,
                Dens = dens, Foam = foam, Out = outPx, N = s.N,
                OrgX = org.x, OrgY = org.y, X0 = s.Cx - Half, Y0 = s.Cy - Half, Box = box,
            };
            Native.MrpNative.WaterSpray(&a);
        }
        if (Native.MrpNative.Verify)
        {
            var px2 = new byte[s.Px.Length];
            DrawCs(s, px2, new float[s.Dens.Length], new float[s.Foam.Length]);
            long diff = 0;
            for (int i = 0; i < px2.Length; i++) if (px2[i] != s.Px[i]) diff++;
            VerifyDraws++;
            VerifyDrawDiff += diff;
        }
        fixed (byte* b = s.Px) s.Tex.LoadRawTextureData((IntPtr)b, s.Px.Length);
        s.Tex.Apply(false, false);
    }

    // C# 版 (照合用)。px・dens・foam に描く
    private static void DrawCs(Spray s, byte[] outPx, float[] dens, float[] foamBuf)
    {
        int n = s.N;
        var org = WaterSim.Origin;
        float x0 = s.Cx - Half, y0 = s.Cy - Half;
        const float inv = 1f / WaterSim.Unit;
        int count = WaterSim.Particles;
        for (int p = 0; p < count; p++)
        {
            // 世界 → 絵の画素。高さは上へずらす (真上から斜めに見ている)
            float ax = (org.x + WaterSim.Ox[p] * inv - x0) * Ppu, ay = (org.y + (WaterSim.Oy[p] + WaterSim.Oh[p]) * inv - y0) * Ppu;
            float bx = (org.x + WaterSim.Px[p] * inv - x0) * Ppu, by = (org.y + (WaterSim.Py[p] + Math.Max(0, WaterSim.Ph[p])) * inv - y0) * Ppu;
            if ((ax < 0 && bx < 0) || (ay < 0 && by < 0) || (ax >= n && bx >= n) || (ay >= n && by >= n)) continue;
            float h = WaterSim.Ph[p] * (1f / WaterSim.MouthH);
            float foam = WaterSim.Stuck[p] ? 0.9f : h > 0.9f ? 0.45f : h < 0.15f ? 0.3f : 0.03f;
            float dx = bx - ax, dy = by - ay;
            int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dy * dy)));
            float w = 1f / steps * Math.Min(steps, 3); // 長い線ほど 1 画素あたりは薄く (速い所は細い筋)
            for (int k = 0; k <= steps; k++)
            {
                float f = (float)k / steps;
                Splat(dens, foamBuf, n, (int)(ax + dx * f), (int)(ay + dy * f), w, foam);
            }
        }
        for (int i = 0; i < n * n; i++)
        {
            float d = dens[i];
            int o = i * 4;
            outPx[o] = (byte)Math.Min(255f, d * 255f);
            outPx[o + 1] = d > 0f ? (byte)Math.Min(255f, foamBuf[i] / d * 255f) : (byte)0;
            outPx[o + 2] = 0;
            outPx[o + 3] = 255;
        }
    }

    private static void Splat(float[] dens, float[] foamBuf, int n, int x, int y, float w, float foam)
    {
        if (x < 1 || y < 1 || x >= n - 1 || y >= n - 1) return;
        int c = y * n + x;
        Add(dens, foamBuf, c, Hit * w, foam);
        Add(dens, foamBuf, c - 1, Side * w, foam); Add(dens, foamBuf, c + 1, Side * w, foam); Add(dens, foamBuf, c - n, Side * w, foam); Add(dens, foamBuf, c + n, Side * w, foam);
        Add(dens, foamBuf, c - n - 1, Corner * w, foam); Add(dens, foamBuf, c - n + 1, Corner * w, foam); Add(dens, foamBuf, c + n - 1, Corner * w, foam); Add(dens, foamBuf, c + n + 1, Corner * w, foam);
    }

    private static void Add(float[] dens, float[] foamBuf, int i, float v, float foam)
    {
        dens[i] += v;
        foamBuf[i] += v * foam;
    }

    private static void Destroy(Spray s)
    {
        if (s.Go) UnityEngine.Object.Destroy(s.Go);
        if (s.Sp) UnityEngine.Object.Destroy(s.Sp);
        if (s.Tex) UnityEngine.Object.Destroy(s.Tex);
        if (s.Mat) UnityEngine.Object.Destroy(s.Mat);
    }

    internal static void Clear()
    {
        foreach (var s in Sprays) Destroy(s);
        Sprays.Clear();
        _lastStep = -1;
    }

    internal static string Describe() => $"sprays={Sprays.Count} sprayMs={LastDrawMs:0.00}";
}
