using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 地形同期の電文。値は固定小数に丸めて送り、送り手 (ホスト) も丸めた値で適用する (全員が同じ数で計算するため)。
// 位置 = int16 の 1/256 単位 (±128)・大きさ = byte の 1/32 単位 (〜8)・向き = ushort の角度・力 = byte の 1/255。
// 依頼 11B / 爆発 14B + 瓦礫 / 打撃 17B + 瓦礫 (瓦礫 = 件数 1B + 1 件 6B [並び・位置・半径 1/64 単位] × 最大 3)
internal static class TerrainWire
{
    public const byte OpRequest = 1; // 客 → ホスト: [op][件数] + 依頼 × 件数
    // ホスト → 全員: [op][最初の連番 u16][件数] + 結果 × 件数。結果の末尾に瓦礫を足した時に 2 → 3、壊した人を足した時に 3 → 5、
    // 試合の刻みを足した時に 5 → 8 へ (古い版と混ざった時に、黙ってずれて読まず「知らない op」として捨てるため)
    public const byte OpBatch = 8;
    public const byte OpDigest = 4; // 客 → ホスト: [op][適用し終えた連番 u16][地形の指紋 u32]
    public const byte OpPlace = 6;  // 客 → ホスト: [op][位置 4B][半径 1B] 爆弾を置く依頼
    // ホスト → 全員: [op][置いた人][位置 4B][半径 1B] 爆弾が置かれた。爆発は導火線の後にホストが普通の結果として配る
    public const byte OpBomb = 7;
    public const int BombBytes = 5;

    public const int MaxRequestBytes = 11;
    public const int MaxResolvedBytes = 17 + 1 + RubbleBlocks.MaxPerEvent * 6;
    public const int BatchHeader = 4;

    private const float PosScale = 256f;
    private const float SizeScale = 32f;
    private const float RadiusScale = 64f;
    private const double AngleScale = 65536.0 / (2.0 * Math.PI);

    public static Vector2 Q(Vector2 v) => new(Dq(QPos(v.x)), Dq(QPos(v.y)));
    public static float QSize(float s) => QSizeByte(s) / SizeScale;
    public static Vector2 QNormal(Vector2 n) => FromAngle(QAngle(n));
    public static float QForce(float f) => QForceByte(f) / 255f;
    public static float QRadius(float r) => QRadiusByte(r) / RadiusScale;
    private static byte QRadiusByte(float r) => (byte)Math.Clamp(Math.Round(r * RadiusScale), 1, 255);

    private static short QPos(float x) => (short)Math.Clamp(Math.Round(x * PosScale), short.MinValue, short.MaxValue);
    private static float Dq(short q) => q / PosScale;
    private static byte QSizeByte(float s) => (byte)Math.Clamp(Math.Round(s * SizeScale), 0, 255);
    private static byte QForceByte(float f) => (byte)Math.Clamp(Math.Round(f * 255.0), 0, 255);
    // 電文の角度。QNormal 済みの向きからは送った値そのものが戻る (割れ目の向きを全員で同じ整数から作るため)
    public static ushort AngleIndex(Vector2 n) => QAngle(n);
    private static ushort QAngle(Vector2 n) => (ushort)((long)Math.Round(Math.Atan2(n.y, n.x) * AngleScale) & 0xffff);
    private static Vector2 FromAngle(ushort a)
    {
        double r = a / AngleScale;
        return new Vector2((float)Math.Cos(r), (float)Math.Sin(r));
    }

    public static int WriteRequest(byte[] b, int o, in DamageEvent e)
    {
        b[o++] = (byte)e.Kind;
        o = WritePos(b, o, e.Position);
        o = WriteU16(b, o, QAngle(e.Direction));
        b[o++] = QSizeByte(e.Size);
        b[o++] = QForceByte(e.Force);
        return WriteU16(b, o, e.Seed);
    }

    public static int ReadRequest(byte[] b, int o, out DamageEvent e)
    {
        var kind = (DamageKind)b[o++];
        Vector2 pos = ReadPos(b, ref o);
        Vector2 dir = FromAngle(ReadU16(b, ref o));
        float size = b[o++] / SizeScale;
        float force = b[o++] / 255f;
        ushort seed = ReadU16(b, ref o);
        e = new DamageEvent(kind, pos, dir, size, force, seed);
        return o;
    }

    public static int WriteResolved(byte[] b, int o, in ResolvedDamage r)
    {
        b[o++] = (byte)r.Kind;
        b[o++] = r.Actor;
        o = WriteU16(b, o, r.Tick);
        o = WritePos(b, o, r.Position);
        o = WriteU16(b, o, QAngle(r.Direction));
        b[o++] = QForceByte(r.Force);
        if (r.Kind == DamageKind.Blunt)
        {
            o = WriteU16(b, o, QAngle(r.Normal));
            b[o++] = unchecked((byte)r.Hp);
        }
        b[o++] = QSizeByte(r.Size);
        o = WriteU16(b, o, r.Seed);
        var land = r.Landings;
        int n = land == null ? 0 : Math.Min(land.Length, RubbleBlocks.MaxPerEvent);
        b[o++] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            b[o++] = land[i].Rank;
            o = WritePos(b, o, land[i].Position);
            b[o++] = QRadiusByte(land[i].Radius);
        }
        return o;
    }

    // 読めない (途中で切れている) 時は -1
    public static int ReadResolved(byte[] b, int o, out ResolvedDamage r)
    {
        r = default;
        if (o + 14 > b.Length) return -1;
        var kind = (DamageKind)b[o];
        if (kind == DamageKind.Blunt && o + 17 > b.Length) return -1;
        o++;
        byte actor = b[o++];
        ushort tick = ReadU16(b, ref o);
        Vector2 pos = ReadPos(b, ref o);
        Vector2 dir = FromAngle(ReadU16(b, ref o));
        float force = b[o++] / 255f;
        Vector2 normal = Vector2.zero;
        sbyte hp = 0;
        if (kind == DamageKind.Blunt)
        {
            normal = FromAngle(ReadU16(b, ref o));
            hp = unchecked((sbyte)b[o++]);
        }
        float size = b[o++] / SizeScale;
        ushort seed = ReadU16(b, ref o);
        int n = b[o++];
        if (n > RubbleBlocks.MaxPerEvent || o + n * 6 > b.Length) return -1;
        var land = n == 0 ? Array.Empty<RubbleLanding>() : new RubbleLanding[n];
        for (int i = 0; i < n; i++)
        {
            byte rank = b[o++];
            Vector2 at = ReadPos(b, ref o);
            land[i] = new RubbleLanding(rank, at, b[o++] / RadiusScale);
        }
        r = new ResolvedDamage(kind, pos, normal, dir, force, size, hp, seed, land, actor, tick);
        return o;
    }

    // 爆弾の位置と半径 (置く依頼・置かれた知らせ)
    public static int WriteBomb(byte[] b, int o, Vector2 pos, float radius)
    {
        o = WritePos(b, o, pos);
        b[o++] = QSizeByte(radius);
        return o;
    }

    public static int ReadBomb(byte[] b, int o, out Vector2 pos, out float radius)
    {
        pos = ReadPos(b, ref o);
        radius = b[o++] / SizeScale;
        return o;
    }

    // 送り手が自分で適用する前に、受け手が読むのと同じ値へ丸める
    public static ResolvedDamage RoundTrip(in ResolvedDamage r)
    {
        var b = new byte[MaxResolvedBytes];
        int len = WriteResolved(b, 0, r);
        Array.Resize(ref b, len);
        ReadResolved(b, 0, out var q);
        return q;
    }

    private static int WritePos(byte[] b, int o, Vector2 v)
    {
        o = WriteU16(b, o, unchecked((ushort)QPos(v.x)));
        return WriteU16(b, o, unchecked((ushort)QPos(v.y)));
    }

    private static Vector2 ReadPos(byte[] b, ref int o)
    {
        float x = Dq(unchecked((short)ReadU16(b, ref o)));
        float y = Dq(unchecked((short)ReadU16(b, ref o)));
        return new Vector2(x, y);
    }

    public static int WriteU16(byte[] b, int o, ushort v)
    {
        b[o++] = (byte)v;
        b[o++] = (byte)(v >> 8);
        return o;
    }

    public static ushort ReadU16(byte[] b, ref int o)
    {
        ushort v = (ushort)(b[o] | (b[o + 1] << 8));
        o += 2;
        return v;
    }

    public static int WriteU32(byte[] b, int o, uint v)
    {
        o = WriteU16(b, o, (ushort)v);
        return WriteU16(b, o, (ushort)(v >> 16));
    }

    public static uint ReadU32(byte[] b, ref int o) => ReadU16(b, ref o) | ((uint)ReadU16(b, ref o) << 16);
}
