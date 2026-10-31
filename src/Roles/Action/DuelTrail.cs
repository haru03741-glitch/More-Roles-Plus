using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 立ち合いの人の少し前までの足元の位置 (全員の端末で取るが、使うのはホストの斬りの当たりだけ)。
// 刀の絵はなぞりに合わせて先に相手を通るのに、一振りがホストへ届くのは振り終わった後なので、
// 刀が相手の向きを通った時刻の位置で当たりを決めるのに使う。位置は DuelFacing が毎回読んでいる物をそのまま貰う (読み取りを増やさない)
internal static class DuelTrail
{
    private const int Len = 24;            // 1 人あたりの数 (Every 秒おきで約 0.6 秒分)
    private const float Every = 1f / 40f;  // これより短い間隔では取らない
    private const float Stale = 0.25f;     // 一番新しい物がこれより古ければ持っていない扱い

    private static readonly float[] T = new float[256 * Len];
    private static readonly float[] X = new float[256 * Len];
    private static readonly float[] Y = new float[256 * Len];
    private static readonly byte[] Head = new byte[256];  // 次に書く所
    private static readonly byte[] Count = new byte[256];

    internal static void Record(byte id, Vector2 p, float now)
    {
        int b = id * Len, h = Head[id];
        if (Count[id] > 0)
        {
            int last = b + (h + Len - 1) % Len;
            if (now - T[last] < Every) return;
            // 前の試合の残り・時刻が戻った (巻き戻し) 時は捨てて取り直す
            if (now < T[last] || now - T[last] > 1f) Count[id] = 0;
        }
        T[b + h] = now;
        X[b + h] = p.x;
        Y[b + h] = p.y;
        Head[id] = (byte)((h + 1) % Len);
        if (Count[id] < Len) Count[id]++;
    }

    internal static bool Tracks(byte id, float now)
    {
        if (Count[id] == 0) return false;
        return now - T[id * Len + (Head[id] + Len - 1) % Len] < Stale;
    }

    // 時刻 t の位置 (間は線で結ぶ・持っている範囲の外は端の物)
    internal static void At(byte id, float t, out float x, out float y)
    {
        int b = id * Len, n = Count[id], h = Head[id];
        int newest = b + (h + Len - 1) % Len;
        x = X[newest];
        y = Y[newest];
        if (n == 0 || t >= T[newest]) return;
        for (int k = 1; k < n; k++)
        {
            int i = b + (h + Len - 1 - k) % Len, j = b + (h + Len - k) % Len; // i が古い方
            if (t < T[i]) { x = X[i]; y = Y[i]; continue; }
            float span = T[j] - T[i];
            float u = span > 1e-5f ? (t - T[i]) / span : 1f;
            x = X[i] + (X[j] - X[i]) * u;
            y = Y[i] + (Y[j] - Y[i]) * u;
            return;
        }
    }

    internal static string Describe(byte id) => $"trail[{id}]={Count[id]}";
}
