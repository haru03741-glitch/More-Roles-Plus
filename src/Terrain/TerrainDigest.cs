using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 地形の指紋。破壊を 1 件適用するごとに「書き換えた壁の線・作った蓋・耐久」を数えて、連番の順に積み上げる。
// 客は時々 (連番, 指紋) をホストへ返し、ホストは自分の同じ連番の指紋と比べる (計算が端末ごとに割れたら分かる)。
// 1 件の中の順番は端末で変わりうる (壁を集める順など) ので、件の中は足し算 (順番に依らない) でまとめる。
// 座標は 1/64 に丸める (電文の固定小数より粗く、浮動小数の末尾の差では変わらない)。
// 丸めの境目は 1/512 の格子 (蓋・壁の線を揃える格子) の点と点のちょうど間へずらす。ずらさないと格子に乗った横・縦の線の
// 4 本に 1 本が境目に乗り、それを切った点が PC と Android の浮動小数の末尾の差で境目の両側に割れて、同じ地形でも指紋が違う
internal static class TerrainDigest
{
    private const float Quant = 64f;
    private const int Keep = 256; // ホストが比べるために残す連番の数

    private static ulong _event;   // 適用中の 1 件の和
    private static bool _open;
    private static readonly Dictionary<ushort, uint> History = new();

    public static uint Running { get; private set; }
    public static ushort LastSeq { get; private set; }
    public static bool Any { get; private set; }

    public static void Reset()
    {
        Running = 0;
        LastSeq = 0;
        Any = false;
        _open = false;
        History.Clear();
    }

    public static void Begin()
    {
        _event = 0;
        _open = true;
    }

    // 1 件を閉じて積み上げる。seq = その件の連番
    public static void End(ushort seq)
    {
        _open = false;
        Running = (uint)Mix(Running ^ _event ^ ((ulong)seq << 48));
        LastSeq = seq;
        Any = true;
        History[seq] = Running;
        History.Remove((ushort)(seq - Keep));
    }

    public static bool TryGet(ushort seq, out uint hash) => History.TryGetValue(seq, out hash);

    // 書き換えた壁の線 (世界座標)
    public static void Chain(List<Vector2> pts)
    {
        if (!_open) return;
        ulong h = 0x9E3779B97F4A7C15UL;
        foreach (var p in pts) h = Mix(h ^ Q(p.x) ^ (Q(p.y) << 32));
        _event += h;
    }

    // 線が全部抜けて消えた壁 (元の両端で区別する)
    public static void Removed(Vector2 a, Vector2 b)
    {
        if (!_open) return;
        _event += Mix(0xD1B54A32D192ED03UL ^ Q(a.x) ^ (Q(a.y) << 16) ^ (Q(b.x) << 32) ^ (Q(b.y) << 48));
    }

    // 耐久の格子 (場所の番号と残り)
    public static void Hp(long cell, int hp)
    {
        if (!_open) return;
        _event += Mix((ulong)cell * 31UL ^ (uint)hp ^ 0x94D049BB133111EBUL);
    }

    private const float BoundaryShift = 1f / 16f; // v*64 の格子の点は m/8。境目 (k+0.5) は (2m+1)/16 に来ない
    private static ulong Q(float v) => (ulong)(uint)(int)System.MathF.Round(v * Quant + BoundaryShift);

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
