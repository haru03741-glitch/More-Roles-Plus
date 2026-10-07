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
    }

    private static readonly List<Spray> Sprays = new();
    private static int _lastStep = -1;
    private static long _lastMs;
    internal static double LastDrawMs { get; private set; }

    // 噴き出しを見せ始める (WaterLeak から)。dir = 主に噴く向き (流れの筋の向き)
    public static void Start(Vector2 at, Vector2 dir, float z)
    {
        var mat0 = MrpBundle.WaterMaterial;
        if (!mat0) return;
        while (Sprays.Count >= MaxSprays) { Destroy(Sprays[0]); Sprays.RemoveAt(0); }
        int n = (int)(Half * 2f * Ppu);
        var s = new Spray { Cx = at.x, Cy = at.y, N = n, Px = new byte[n * n * 4], Dens = new float[n * n], Foam = new float[n * n] };
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

    private static unsafe void Draw(Spray s)
    {
        Array.Clear(s.Dens);
        Array.Clear(s.Foam);
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
                Splat(s, (int)(ax + dx * f), (int)(ay + dy * f), w, foam);
            }
        }
        for (int i = 0; i < n * n; i++)
        {
            float d = s.Dens[i];
            int o = i * 4;
            s.Px[o] = (byte)Math.Min(255f, d * 255f);
            s.Px[o + 1] = d > 0f ? (byte)Math.Min(255f, s.Foam[i] / d * 255f) : (byte)0;
            s.Px[o + 2] = 0;
            s.Px[o + 3] = 255;
        }
        fixed (byte* b = s.Px) s.Tex.LoadRawTextureData((IntPtr)b, s.Px.Length);
        s.Tex.Apply(false, false);
    }

    private static void Splat(Spray s, int x, int y, float w, float foam)
    {
        int n = s.N;
        if (x < 1 || y < 1 || x >= n - 1 || y >= n - 1) return;
        int c = y * n + x;
        Add(s, c, Hit * w, foam);
        Add(s, c - 1, Side * w, foam); Add(s, c + 1, Side * w, foam); Add(s, c - n, Side * w, foam); Add(s, c + n, Side * w, foam);
        Add(s, c - n - 1, Corner * w, foam); Add(s, c - n + 1, Corner * w, foam); Add(s, c + n - 1, Corner * w, foam); Add(s, c + n + 1, Corner * w, foam);
    }

    private static void Add(Spray s, int i, float v, float foam)
    {
        s.Dens[i] += v;
        s.Foam[i] += v * foam;
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
