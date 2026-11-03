using System;

namespace MoreRolesPlus.Voice;

// ノイズキャンセリング (自分の声を送る前に・48kHz)。
// 1024 点ずつ半分重ねて周波数に分け (√Hann の窓)、周波数ごとに「ずっと鳴っている音 (雑音)」の大きさを覚えておき、
// 雑音に対する声の大きさから音を残す割合を決める (Wiener・決定指向の推定)。雑音は小さい方へはすぐ、大きい方へはゆっくり追う。
// 残す割合は 0.08 (-22dB) より下げない (声が水の中のように聞こえる音の欠けを抑える)。遅れは 1024 点 (約 21ms)
internal static class VoiceDenoise
{
    private const int N = 1024, Hop = N / 2, Bins = N / 2 + 1;
    private const float Floor = 0.08f;          // 残す割合の下限
    private const float OverSub = 1.6f;         // 雑音を少し多めに見る
    private const float Rise = 1.012f;          // 雑音の見積もりが上がる速さ (1 回 = 約 11ms ごと)
    private const float Alpha = 0.97f;          // 前の回の推定をどれだけ残すか

    private static readonly float[] Win = new float[N];
    private static readonly float[] Re = new float[N], Im = new float[N];
    private static readonly float[] In = new float[N];       // 直近の N 点 (半分ずつ入れ替える)
    private static readonly float[] Out = new float[N];      // 重ねて足す出口
    private static readonly float[] Ready = new float[Hop];  // できた Hop 点
    private static readonly float[] Noise = new float[Bins];
    private static readonly float[] PrevGain = new float[Bins], PrevPost = new float[Bins];
    private static readonly float[] Gain = new float[Bins];
    private static int _pos, _frames;
    private static bool _init;

    internal static void Reset()
    {
        if (!_init)
        {
            for (int i = 0; i < N; i++) Win[i] = MathF.Sqrt(0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / N)); // √Hann (周期型: 半分重ねで元に戻る)
            _init = true;
        }
        Array.Clear(In, 0, N);
        Array.Clear(Out, 0, N);
        Array.Clear(Ready, 0, Hop);
        Array.Clear(PrevGain, 0, Bins);
        Array.Clear(PrevPost, 0, Bins);
        for (int k = 0; k < Bins; k++) Noise[k] = 1e-6f;
        _pos = 0;
        _frames = 0;
    }

    // 1 点入れて 1 点出す (出るのは前の溜めの分)
    internal static float Next(float x)
    {
        if (!_init) Reset();
        float y = Ready[_pos];
        In[Hop + _pos] = x;
        if (++_pos == Hop)
        {
            _pos = 0;
            Frame();
            Array.Copy(In, Hop, In, 0, Hop);
        }
        return y;
    }

    private static void Frame()
    {
        for (int i = 0; i < N; i++)
        {
            Re[i] = In[i] * Win[i];
            Im[i] = 0f;
        }
        Fft(Re, Im, false);
        _frames++;
        bool learning = _frames < 20; // 最初の約 0.2 秒は雑音を覚えるだけ (話し始めはふつう雑音から)
        for (int k = 0; k < Bins; k++)
        {
            float p = Re[k] * Re[k] + Im[k] * Im[k];
            float n = Noise[k];
            if (learning) n = _frames == 1 ? p : n + (p - n) * 0.3f;
            else if (p < n) n += (p - n) * 0.3f;
            else n = Math.Min(n * Rise, p);
            n = Math.Max(n, 1e-9f);
            Noise[k] = n;
            float post = p / (n * OverSub);
            float prio = Alpha * PrevGain[k] * PrevGain[k] * PrevPost[k] + (1f - Alpha) * Math.Max(post - 1f, 0f);
            float g = prio / (1f + prio);
            Gain[k] = Math.Max(g, Floor);
            PrevGain[k] = g;
            PrevPost[k] = post;
        }
        // 隣り合う周波数と少しならす (残す割合のばらつきでピロピロ鳴る音を抑える)
        float prev = Gain[0];
        for (int k = 1; k < Bins - 1; k++)
        {
            float cur = Gain[k];
            Gain[k] = 0.25f * prev + 0.5f * cur + 0.25f * Gain[k + 1];
            prev = cur;
        }
        for (int k = 0; k < Bins; k++)
        {
            Re[k] *= Gain[k];
            Im[k] *= Gain[k];
            if (k > 0 && k < Bins - 1)
            {
                Re[N - k] = Re[k];
                Im[N - k] = -Im[k];
            }
        }
        Fft(Re, Im, true);
        // 重ねて足す: 前半は前の回の後半と合わせて出来上がり、後半は次の回へ
        for (int i = 0; i < N; i++) Out[i] += Re[i] * Win[i];
        Array.Copy(Out, 0, Ready, 0, Hop);
        Array.Copy(Out, Hop, Out, 0, Hop);
        Array.Clear(Out, Hop, Hop);
    }

    // 2 の累乗の長さの FFT (その場で書き換える)。inverse は 1/N まで掛ける
    private static void Fft(float[] re, float[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            float ang = 2f * MathF.PI / len * (inverse ? 1f : -1f);
            float wr = MathF.Cos(ang), wi = MathF.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1f, ci = 0f;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    float tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    float ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
        if (!inverse) return;
        float inv = 1f / n;
        for (int i = 0; i < n; i++)
        {
            re[i] *= inv;
            im[i] *= inv;
        }
    }
}
