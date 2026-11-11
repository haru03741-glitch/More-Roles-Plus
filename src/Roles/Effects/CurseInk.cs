using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// わら人形が呪った会議の開票: 返した票の印が墨を吸って黒ずみ、墨が呪われた人の名札の紙へ這い広がり、
// 名札の下の縁から垂れ、呪われた人の顔の絵が黒ずむ (全員の端末・開票の画面が出ている約 5 秒の間)。
// 墨は名札の子の 1 枚のテクスチャに描く (名札の紙の上・印と顔と名前の下)。広がりは紙の毛管と同じく半径が時間の平方根で伸びるので、
// 出どころごとに「墨が届く時刻」を一度だけ計算しておき、毎回の更新では時刻と比べて塗るだけにする
internal static class CurseInk
{
    private const int Ppu = 100;                           // テクスチャの 1 単位あたりの画素
    private const float CardW = 2.7f, CardH = 0.63f;       // 名札の紙 (votePlayerBase) の大きさ
    private const float Below = 0.75f;                     // 名札の下へ垂れる分の高さ
    private const float InkZ = 0.05f;                      // 紙 (0.1) と印・顔 (0 以下) の間
    private const float BuildAt = 1.3f;                    // 印が並び終わるまで待ってから出どころを決める (秒)
    private const float ChipStart = 1.3f, ChipGap = 0.18f, ChipGrow = 3.3f, ChipR = 0.56f, ChipRStep = 0.05f;
    private const float Creep = 0.10f;                     // 広がりきった後もゆっくり這う割合
    private const float DarkChip = 0.8f, DarkFace = 0.82f;
    private const float FaceFrom = 2.2f, FaceDur = 2.6f;
    private const float DripDur = 2.3f, DripW = 0.022f;
    private const float Life = 9f;                         // 開票が長引いても消える上限 (秒)
    private const float Gap = 1f / 30f;                    // 描き直しの間隔
    // 会議の画面はガラスの照りが上に重なって黒も持ち上がるので、本編の縁取りと同じ黒さになるよう墨はほぼ黒・ほぼ不透明
    private const byte InkR = 8, InkG = 6, InkB = 7;
    private const float DarkR = 0.06f, DarkG = 0.05f, DarkB = 0.055f;

    // 隅へ遅れて這う墨 (名札の中心からの位置・半径・出始め)。隅に白い紙が残らないように
    private static readonly (float x, float y, float r, float at)[] Corners =
        { (1.1f, 0.0f, 0.7f, 3.0f), (-0.2f, 0.2f, 0.64f, 3.4f), (0.6f, 0.22f, 0.7f, 3.7f), (-0.75f, -0.1f, 0.55f, 3.2f), (-1.05f, 0.17f, 0.45f, 3.6f),
          (1.25f, 0.2f, 0.5f, 3.1f), (0.15f, 0.24f, 0.55f, 3.0f), (-1.15f, -0.2f, 0.45f, 2.8f) };
    private static readonly (float at, float len)[] DripPlan = { (2.5f, 0.45f), (3.0f, 0.7f), (3.6f, 0.32f) };

    private struct Source { public float X, Y, R, At, Grow, P1, P2; }

    private sealed class Ink
    {
        public PlayerVoteArea Card;
        public GameObject Go;
        public Texture2D Tex;
        public Sprite Sp;
        public byte[] Px;
        public float[] T, G, Body;
        public float[] Mask;
        public int W, H, CardRows;
        public float Born, Drawn = -1f;
        public bool Built;
        public IEnumerator<bool> Steps;
        public List<int> Mine;
        public readonly List<SpriteRenderer> Chips = new();
        public Color[] ChipBase;
        public float[] ChipAt;
        public SpriteRenderer[] Face;
        public Color[] FaceBase;
        public (float x, float at, float len)[] Drips;
    }

    private static readonly List<Ink> Live = new();
    internal static int Made; // 確かめ用

    // 開票を並べた直後 (全員の端末)。呪ったわら人形の票が人に入っていれば、その人の名札に墨を出す
    internal static void OnResults(MeetingHud hud, Il2CppStructArray<MeetingHud.VoterState> states)
    {
        if (!hud || states == null || hud.playerStates == null) return;
        foreach (var ink in Live) Dispose(ink);
        Live.Clear();
        int n = states.Length;
        for (int pi = 0; pi < hud.playerStates.Length; pi++)
        {
            var card = hud.playerStates[pi];
            if (!card) continue;
            byte target = card.PlayerId.Value;
            // この名札に入った票のうち、何番目が呪った人の票か (印は票の順に名札の子として作られる)
            List<int> mine = null;
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                var s = states[i];
                if (s.VotedForId != target) continue;
                // 投票した人がもういない行は印が作られないので数えない (番号がずれないように)
                if (GameData.Instance && GameData.Instance.GetPlayerById(s.VoterId) == null) continue;
                if (Crew.Waraningyou.CursedNow(s.VoterId)) (mine ??= new List<int>()).Add(k);
                k++;
            }
            if (mine == null) continue;
            Live.Add(Begin(card, mine));
        }
        // 会議の中なので場所の音にしない。墨がにじみ始める時 (印が並んだ後) に合わせる
        if (Live.Count > 0) PortKit.After(ChipStart, () => { if (Live.Count > 0) PortSound.Play("curse_ink", null, 0.75f); });
    }

    private static Ink Begin(PlayerVoteArea card, List<int> mine)
    {
        var ink = new Ink { Card = card, Born = Time.time, Mine = mine };
        ink.W = (int)(CardW * Ppu);
        ink.CardRows = (int)(CardH * Ppu);
        ink.H = ink.CardRows + (int)(Below * Ppu);
        ink.Px = new byte[ink.W * ink.H * 4];
        ink.Tex = new Texture2D(ink.W, ink.H, TextureFormat.RGBA32, false)
            { name = "MrpCurseInk", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        // 上端を原点にした絵: 名札の上端がテクスチャの上端
        var sp = ink.Sp = Sprite.Create(ink.Tex, new UnityEngine.Rect(0, 0, ink.W, ink.H), new Vector2(0.5f, 1f), Ppu);
        ink.Go = new GameObject("MrpCurseInk") { layer = card.gameObject.layer };
        ink.Go.transform.SetParent(card.transform, false);
        ink.Go.transform.localPosition = FxMath.V3(0f, CardH / 2f, InkZ);
        ink.Go.AddComponent<SpriteRenderer>().sprite = sp;
        Upload(ink);
        Made++;
        return ink;
    }

    // 印 (playerVote(Clone)) を作られた順に拾い、呪った人の分を控える。顔の絵も控える
    private static void FindParts(Ink ink)
    {
        var ct = ink.Card.transform;
        int idx = 0;
        for (int i = 0; i < ct.childCount; i++)
        {
            var c = ct.GetChild(i);
            if (!c.name.StartsWith("playerVote")) continue;
            if (ink.Mine.Contains(idx))
            {
                var sr = c.GetComponent<SpriteRenderer>();
                if (sr) ink.Chips.Add(sr);
            }
            idx++;
        }
        ink.ChipBase = new Color[ink.Chips.Count];
        ink.ChipAt = new float[ink.Chips.Count];
        for (int i = 0; i < ink.Chips.Count; i++)
        {
            ink.ChipBase[i] = ink.Chips[i].color;
            ink.ChipAt[i] = ChipStart + ChipGap * i;
        }
        var face = ink.Card.PlayerIcon ? ink.Card.PlayerIcon.GetComponentsInChildren<SpriteRenderer>(true) : null;
        if (face == null) return;
        ink.Face = new SpriteRenderer[face.Length];
        ink.FaceBase = new Color[face.Length];
        for (int i = 0; i < face.Length; i++) { ink.Face[i] = face[i]; ink.FaceBase[i] = face[i].color; }
    }

    internal static void Tick()
    {
        if (Live.Count == 0) return;
        float now = Time.time;
        bool built = false; // 出どころの計算は重いので、1 フレームに 1 つの名札の数行ずつだけ進める
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var ink = Live[i];
            float t = now - ink.Born;
            if (!ink.Go || !ink.Card || t > Life || !MeetingHud.Instance)
            {
                Dispose(ink);
                Live.RemoveAt(i);
                continue;
            }
            if (!ink.Built)
            {
                if (t < BuildAt || built) continue;
                if (ink.Steps == null)
                {
                    FindParts(ink);
                    if (ink.Chips.Count == 0)
                    {
                        Dispose(ink);
                        Live.RemoveAt(i);
                        continue;
                    }
                    ink.Steps = Build(ink);
                }
                ink.Steps.MoveNext();
                built = true;
                continue;
            }
            if (t - ink.Drawn < Gap) continue;
            ink.Drawn = t;
            Draw(ink, t);
        }
    }

    // ── 届く時刻の計算 (一度だけ) ─────────────────────────────

    private static IEnumerator<bool> Build(Ink ink)
    {
        int w = ink.W, h = ink.H, rows = ink.CardRows;
        var rng = new System.Random(ink.Card.PlayerId.Value * 7919 + Environment.TickCount);
        // 紙の濃淡: 大きな塊のゆがみ・和紙の漉き目 (横長)・細かいほつれ・中の濃淡
        var blob = new Fbm(rng, 0.74f * Ppu, 0.74f * Ppu, 3);
        var fib = new Fbm(rng, 1.3f * Ppu, 0.37f * Ppu, 3);
        var fine = new Fbm(rng, 0.22f * Ppu, 0.056f * Ppu, 3);
        var dens = new Fbm(rng, 0.28f * Ppu, 0.28f * Ppu, 3);

        var src = new List<Source>();
        for (int i = 0; i < ink.Chips.Count; i++)
        {
            var c = ink.Chips[i] ? ink.Chips[i].transform.localPosition : FxMath.V3(0f, -0.16f, 0f);
            src.Add(new Source { X = c.x, Y = c.y, R = ChipR + ChipRStep * i, At = ink.ChipAt[i], Grow = ChipGrow,
                P1 = (float)rng.NextDouble() * 6.28f, P2 = (float)rng.NextDouble() * 6.28f });
        }
        foreach (var (x, y, r, at) in Corners)
            src.Add(new Source { X = x, Y = y, R = r, At = at, Grow = 3.7f, P1 = (float)rng.NextDouble() * 6.28f, P2 = (float)rng.NextDouble() * 6.28f });

        ink.T = new float[w * rows];
        ink.Body = new float[w * rows];
        ink.Mask = new float[w * rows];
        float half = CardW / 2f;
        for (int y = 0; y < rows; y++)
        {
            float uy = CardH / 2f - (y + 0.5f) / Ppu;            // 名札の中心からの高さ (単位)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float ux = (x + 0.5f) / Ppu - half;
                float b = blob.At(x, y) - 0.5f, f = fib.At(x, y) - 0.5f, e = fine.At(x, y) - 0.5f;
                float best = float.MaxValue;
                foreach (var s in src)
                {
                    float dx = ux - s.X, dy = (uy - s.Y) * 1.15f;
                    float d = FxMath.Sqrt(dx * dx + dy * dy);
                    float a = FxMath.Atan2(dy, dx);
                    float lobe = 1f + 0.25f * FxMath.Cos(2f * a) + 0.18f * FxMath.Cos(3f * a + s.P1) + 0.12f * FxMath.Cos(5f * a + s.P2);
                    float fld = d / lobe - b * s.R * 0.8f - f * s.R * 0.3f - e * s.R * 0.1f;
                    float u = fld / s.R;
                    float p = u <= 0f ? 0f : u <= 1f ? u * u : 1f + (u - 1f) / Creep;
                    float at = s.At + s.Grow * p;
                    if (at < best) best = at;
                }
                ink.T[i] = best;
                ink.Body[i] = 0.97f + 0.03f * (dens.At(x, y) - 0.5f) * 2f;
                ink.Mask[i] = RoundRect(ux, uy, half - 0.005f, CardH / 2f - 0.005f, 0.07f);
            }
            if (y % RowsPerFrame == RowsPerFrame - 1) yield return false;
        }
        // 境目の幅 = 1 画素ぶんの時刻の差 (速く広がる所は鋭く、這う所も 1 画素ほどで切れる)
        ink.G = new float[w * rows];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float gx = x + 1 < w ? FxMath.Abs(ink.T[i + 1] - ink.T[i]) : 0f;
                float gy = y + 1 < rows ? FxMath.Abs(ink.T[i + w] - ink.T[i]) : 0f;
                ink.G[i] = FxMath.Max(0.004f, FxMath.Max(gx, gy) * 1.3f);
            }

        // 垂れ: 呪った人の印の下から (左端・真ん中・右端)
        int m = ink.Chips.Count;
        int[] pick = m >= 3 ? new[] { 0, m / 2, m - 1 } : m == 2 ? new[] { 0, 1 } : new[] { 0 };
        ink.Drips = new (float, float, float)[pick.Length];
        for (int j = 0; j < pick.Length; j++)
        {
            var c = ink.Chips[pick[j]] ? ink.Chips[pick[j]].transform.localPosition.x : 0f;
            ink.Drips[j] = (c + ((float)rng.NextDouble() - 0.5f) * 0.12f, DripPlan[j].at, DripPlan[j].len);
        }
        ink.Built = true;
        yield return true;
    }

    private const int RowsPerFrame = 12;

    private static float RoundRect(float x, float y, float hw, float hh, float rad)
    {
        float qx = FxMath.Abs(x) - (hw - rad), qy = FxMath.Abs(y) - (hh - rad);
        float ox = FxMath.Max(qx, 0f), oy = FxMath.Max(qy, 0f);
        float dist = FxMath.Sqrt(ox * ox + oy * oy) + FxMath.Min(FxMath.Max(qx, qy), 0f) - rad;
        return FxMath.Clamp01(0.5f - dist * Ppu);
    }

    // ── 描く (時刻と比べて塗るだけ) ──────────────────────────────

    private static void Draw(Ink ink, float t)
    {
        int w = ink.W, rows = ink.CardRows, h = ink.H;
        var px = ink.Px;
        for (int i = 0; i < w * rows; i++)
        {
            int o = (h - 1 - i / w) * w + i % w;   // テクスチャは下の行から数える
            float dt = t - ink.T[i];
            float a = 0f;
            if (dt > 0f)
            {
                float inside = FxMath.Clamp01(dt / ink.G[i]);
                // 前が通ったばかりの所は顔料が溜まって少し濃い
                a = inside * (ink.Body[i] + 0.12f * FxMath.Clamp01(1f - dt / 0.35f)) * ink.Mask[i];
            }
            Put(px, o, a);
        }
        // 名札の下: 垂れの筋と先の滴 (テクスチャの下の行が名札の下)
        Array.Clear(px, 0, (h - rows) * w * 4);
        foreach (var (dx, at, len) in ink.Drips)
        {
            float q = (t - at) / DripDur;
            if (q <= 0f) continue;
            float qq = FxMath.Min(q, 1f);
            float ln = len * (1f - (1f - qq) * (1f - qq));
            float cx = (dx + CardW / 2f) * Ppu;
            float dwPx = DripW * Ppu;
            int y1 = rows + (int)(ln * Ppu) + (int)(dwPx * 2f) + 2;
            for (int y = rows - 3; y < FxMath.Min(y1, ink.H); y++)
            {
                float yd = (y + 0.5f - rows) / Ppu;              // 名札の下の縁からの長さ (単位)
                float tt = FxMath.Clamp01(yd / FxMath.Max(ln, 0.001f));
                float wd = dwPx * (1f - 0.45f * tt) * (1f + 0.6f * FxMath.Max(0f, 1f - (yd * Ppu + 4f) / 10f));
                float sx = cx + 1.2f * FxMath.Sin(y * 0.05f + dx);
                float by = ln * Ppu + rows, br = dwPx * 1.15f;
                for (int x = (int)(sx - wd - br - 2f); x <= (int)(sx + wd + br + 2f); x++)
                {
                    if (x < 0 || x >= w) continue;
                    float v = yd <= ln ? FxMath.Clamp01(wd - FxMath.Abs(x + 0.5f - sx) + 0.5f) : 0f;
                    float ddy = (y + 0.5f - by) * (y > by ? 0.8f : 1.3f);
                    float ddx = x + 0.5f - sx;
                    float bead = FxMath.Clamp01(br - FxMath.Sqrt(ddx * ddx + ddy * ddy) + 0.5f);
                    v = FxMath.Max(v, bead) * 0.95f;
                    int o = (h - 1 - y) * w + x;
                    if (v * 255f > px[o * 4 + 3]) Put(px, o, v);
                }
            }
        }
        Upload(ink);

        // 返した票の印が墨を吸って黒ずむ・呪われた人の顔の絵が黒ずむ
        for (int i = 0; i < ink.Chips.Count; i++)
        {
            var sr = ink.Chips[i];
            if (!sr) continue;
            float k = DarkChip * FxMath.Clamp01((t - ink.ChipAt[i]) / 1.0f);
            sr.color = Mix(ink.ChipBase[i], k);
        }
        if (ink.Face != null)
        {
            float k = DarkFace * FxMath.Clamp01((t - FaceFrom) / FaceDur);
            for (int i = 0; i < ink.Face.Length; i++)
                if (ink.Face[i]) ink.Face[i].color = Mix(ink.FaceBase[i], k);
        }
    }

    // 名札と一緒に GameObject が消えても、テクスチャとスプライトは残るので自分で消す
    private static void Dispose(Ink ink)
    {
        if (ink.Go) Object.Destroy(ink.Go);
        if (ink.Sp) Object.Destroy(ink.Sp);
        if (ink.Tex) Object.Destroy(ink.Tex);
        ink.Go = null; ink.Sp = null; ink.Tex = null;
    }

    private static unsafe void Upload(Ink ink)
    {
        fixed (byte* p = ink.Px) ink.Tex.LoadRawTextureData((IntPtr)p, ink.Px.Length);
        ink.Tex.Apply(false, false);
    }

    private static void Put(byte[] px, int i, float a)
    {
        int o = i * 4;
        px[o] = InkR; px[o + 1] = InkG; px[o + 2] = InkB;
        px[o + 3] = (byte)(FxMath.Clamp01(a) * 255f + 0.5f);
    }

    private static Color Mix(Color c, float k) =>
        FxMath.Rgba(c.r + (DarkR - c.r) * k, c.g + (DarkG - c.g) * k, c.b + (DarkB - c.b) * k, c.a);

    // 値のノイズを重ねた物 (格子は折り返すので出どころをずらしても同じ式で引ける)
    private sealed class Fbm
    {
        private readonly float[][] _g;
        private readonly int[] _gw, _gh;
        private readonly float[] _sx, _sy;

        public Fbm(System.Random rng, float spacingX, float spacingY, int octaves)
        {
            _g = new float[octaves][]; _gw = new int[octaves]; _gh = new int[octaves]; _sx = new float[octaves]; _sy = new float[octaves];
            for (int o = 0; o < octaves; o++)
            {
                float k = 1 << o;
                _sx[o] = FxMath.Max(1f, spacingX / k); _sy[o] = FxMath.Max(1f, spacingY / k);
                _gw[o] = (int)(CardW * Ppu / _sx[o]) + 3; _gh[o] = (int)((CardH + Below) * Ppu / _sy[o]) + 3;
                var g = new float[_gw[o] * _gh[o]];
                for (int i = 0; i < g.Length; i++) g[i] = (float)rng.NextDouble();
                _g[o] = g;
            }
        }

        public float At(float x, float y)
        {
            float sum = 0f, amp = 1f, tot = 0f;
            for (int o = 0; o < _g.Length; o++)
            {
                float gx = x / _sx[o], gy = y / _sy[o];
                int x0 = (int)gx, y0 = (int)gy;
                float fx = gx - x0, fy = gy - y0;
                fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
                int w = _gw[o], h = _gh[o];
                int xa = x0 % w, xb = (x0 + 1) % w, ya = y0 % h, yb = (y0 + 1) % h;
                var g = _g[o];
                float top = g[ya * w + xa] + (g[ya * w + xb] - g[ya * w + xa]) * fx;
                float bot = g[yb * w + xa] + (g[yb * w + xb] - g[yb * w + xa]) * fx;
                sum += amp * (top + (bot - top) * fy); tot += amp; amp *= 0.55f;
            }
            return sum / tot;
        }
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.PopulateResults))]
internal static class CurseInkResultsPatch
{
    public static void Postfix(MeetingHud __instance, Il2CppStructArray<MeetingHud.VoterState> __0)
    {
        try { CurseInk.OnResults(__instance, __0); }
        catch (Exception e) { Plugin.Logger.LogError($"curse ink: {e}"); }
    }
}
