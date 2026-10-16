using System;
using UnityEngine;

namespace MoreRolesPlus.Fx;

// Unity の値型 (Vector2/3・Rect・Bounds・Color など) の小さなメソッドの、この mod の中で計算する版。
// interop 経由で呼ぶと 1 回ごとに il2cpp_runtime_invoke と戻り値の箱 (ゲーム側のごみ) になる。
// ビルドの後に tools/StructInline が、この mod の中の呼び出しをここへ差し替える (ソースは普通に Unity の書き方のままでよい)。
// 式はゲーム側と同じ結果になるように書く (地形の指紋が端末間で一致する前提)。ここでは差し替え対象のメソッドを使わず、
// フィールドだけを読み書きする (使うと自分自身へ差し替えられて止まらなくなる)。
internal static class UnityStructs
{
    private const float Epsilon = 1E-05F; // Vector2/3 の正規化と == の閾値

    // ── Vector2 ──
    public static Vector2 NewVector2(float x, float y) { Vector2 v = default; v.x = x; v.y = y; return v; }
    public static void InitVector2(ref Vector2 v, float x, float y) { v.x = x; v.y = y; }
    public static Vector2 V2Add(Vector2 a, Vector2 b) => NewVector2(a.x + b.x, a.y + b.y);
    public static Vector2 V2Sub(Vector2 a, Vector2 b) => NewVector2(a.x - b.x, a.y - b.y);
    public static Vector2 V2MulVS(Vector2 a, float d) => NewVector2(a.x * d, a.y * d);
    public static Vector2 V2MulSV(float d, Vector2 a) => NewVector2(a.x * d, a.y * d);
    public static Vector2 V2Div(Vector2 a, float d) => NewVector2(a.x / d, a.y / d);
    public static Vector2 V2Neg(Vector2 a) => NewVector2(-a.x, -a.y);
    public static Vector2 V2FromV3(Vector3 v) => NewVector2(v.x, v.y);
    public static Vector3 V3FromV2(Vector2 v) => NewVector3(v.x, v.y, 0f);
    public static float V2SqrMagnitude(ref Vector2 v) => v.x * v.x + v.y * v.y;
    public static float V2Magnitude(ref Vector2 v) => (float)Math.Sqrt(v.x * v.x + v.y * v.y);
    public static Vector2 V2Normalized(ref Vector2 v)
    {
        float m = (float)Math.Sqrt(v.x * v.x + v.y * v.y);
        return m > Epsilon ? NewVector2(v.x / m, v.y / m) : default;
    }
    public static bool V2Equal(Vector2 a, Vector2 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y;
        return dx * dx + dy * dy < Epsilon * Epsilon;
    }
    public static bool V2NotEqual(Vector2 a, Vector2 b) => !V2Equal(a, b);
    public static float V2Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
    public static Vector2 V2Lerp(Vector2 a, Vector2 b, float t)
    {
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        return NewVector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
    }
    public static Vector2 V2Zero() => default;
    public static Vector2 V2One() => NewVector2(1f, 1f);
    public static Vector2 V2Right() => NewVector2(1f, 0f);
    public static Vector2 V2Left() => NewVector2(-1f, 0f);
    public static Vector2 V2Up() => NewVector2(0f, 1f);
    public static Vector2 V2Down() => NewVector2(0f, -1f);

    // ── Vector3 ──
    public static Vector3 NewVector3(float x, float y, float z) { Vector3 v = default; v.x = x; v.y = y; v.z = z; return v; }
    public static void InitVector3(ref Vector3 v, float x, float y, float z) { v.x = x; v.y = y; v.z = z; }
    public static Vector3 NewVector3XY(float x, float y) => NewVector3(x, y, 0f);
    public static void InitVector3XY(ref Vector3 v, float x, float y) { v.x = x; v.y = y; v.z = 0f; }
    public static Vector3 V3Add(Vector3 a, Vector3 b) => NewVector3(a.x + b.x, a.y + b.y, a.z + b.z);
    public static Vector3 V3Sub(Vector3 a, Vector3 b) => NewVector3(a.x - b.x, a.y - b.y, a.z - b.z);
    public static Vector3 V3MulVS(Vector3 a, float d) => NewVector3(a.x * d, a.y * d, a.z * d);
    public static Vector3 V3MulSV(float d, Vector3 a) => NewVector3(a.x * d, a.y * d, a.z * d);
    public static Vector3 V3Div(Vector3 a, float d) => NewVector3(a.x / d, a.y / d, a.z / d);
    public static Vector3 V3Neg(Vector3 a) => NewVector3(-a.x, -a.y, -a.z);
    public static float V3Magnitude(ref Vector3 v) => (float)Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
    public static float V3SqrMagnitude(ref Vector3 v) => v.x * v.x + v.y * v.y + v.z * v.z;
    public static Vector3 V3Zero() => default;
    public static Vector3 V3One() => NewVector3(1f, 1f, 1f);

    // ── Vector4 ──
    public static Vector4 NewVector4(float x, float y, float z, float w) { Vector4 v = default; v.x = x; v.y = y; v.z = z; v.w = w; return v; }
    public static void InitVector4(ref Vector4 v, float x, float y, float z, float w) { v.x = x; v.y = y; v.z = z; v.w = w; }

    // ── Rect (左下 m_XMin/m_YMin と幅・高さ。幅が負でも xMin は m_XMin のまま) ──
    public static Rect NewRect(float x, float y, float w, float h) { Rect r = default; r.m_XMin = x; r.m_YMin = y; r.m_Width = w; r.m_Height = h; return r; }
    public static void InitRect(ref Rect r, float x, float y, float w, float h) { r.m_XMin = x; r.m_YMin = y; r.m_Width = w; r.m_Height = h; }
    public static Rect RectMinMax(float xmin, float ymin, float xmax, float ymax) => NewRect(xmin, ymin, xmax - xmin, ymax - ymin);
    public static float RectXMin(ref Rect r) => r.m_XMin;
    public static float RectYMin(ref Rect r) => r.m_YMin;
    public static float RectXMax(ref Rect r) => r.m_Width + r.m_XMin;
    public static float RectYMax(ref Rect r) => r.m_Height + r.m_YMin;
    public static float RectWidth(ref Rect r) => r.m_Width;
    public static float RectHeight(ref Rect r) => r.m_Height;
    public static Vector2 RectCenter(ref Rect r) => NewVector2(r.m_XMin + r.m_Width / 2f, r.m_YMin + r.m_Height / 2f);
    public static Vector2 RectPosition(ref Rect r) => NewVector2(r.m_XMin, r.m_YMin);
    public static Vector2 RectSize(ref Rect r) => NewVector2(r.m_Width, r.m_Height);
    // 下と左の辺は含み、上と右の辺は含まない
    public static bool RectContains2(ref Rect r, Vector2 p) =>
        p.x >= r.m_XMin && p.x < r.m_Width + r.m_XMin && p.y >= r.m_YMin && p.y < r.m_Height + r.m_YMin;
    public static bool RectContains3(ref Rect r, Vector3 p) =>
        p.x >= r.m_XMin && p.x < r.m_Width + r.m_XMin && p.y >= r.m_YMin && p.y < r.m_Height + r.m_YMin;
    public static bool RectOverlaps(ref Rect r, Rect o) =>
        o.m_Width + o.m_XMin > r.m_XMin && o.m_XMin < r.m_Width + r.m_XMin && o.m_Height + o.m_YMin > r.m_YMin && o.m_YMin < r.m_Height + r.m_YMin;

    // ── RectInt (幅が負なら xMin/xMax が入れ替わる) ──
    public static RectInt NewRectInt(int x, int y, int w, int h) { RectInt r = default; r.m_XMin = x; r.m_YMin = y; r.m_Width = w; r.m_Height = h; return r; }
    public static void InitRectInt(ref RectInt r, int x, int y, int w, int h) { r.m_XMin = x; r.m_YMin = y; r.m_Width = w; r.m_Height = h; }
    public static int RectIntXMin(ref RectInt r) => Math.Min(r.m_XMin, r.m_XMin + r.m_Width);
    public static int RectIntYMin(ref RectInt r) => Math.Min(r.m_YMin, r.m_YMin + r.m_Height);
    public static int RectIntXMax(ref RectInt r) => Math.Max(r.m_XMin, r.m_XMin + r.m_Width);
    public static int RectIntYMax(ref RectInt r) => Math.Max(r.m_YMin, r.m_YMin + r.m_Height);
    public static int RectIntWidth(ref RectInt r) => r.m_Width;
    public static int RectIntHeight(ref RectInt r) => r.m_Height;

    // ── Bounds (中心と半分の大きさ) ──
    public static Vector3 BoundsCenter(ref Bounds b) => b.m_Center;
    public static Vector3 BoundsExtents(ref Bounds b) => b.m_Extents;
    public static Vector3 BoundsSize(ref Bounds b) => V3MulVS(b.m_Extents, 2f);
    public static Vector3 BoundsMin(ref Bounds b) => V3Sub(b.m_Center, b.m_Extents);
    public static Vector3 BoundsMax(ref Bounds b) => V3Add(b.m_Center, b.m_Extents);

    // ── Color / Color32 ──
    public static Color NewColor(float r, float g, float b, float a) { Color c = default; c.r = r; c.g = g; c.b = b; c.a = a; return c; }
    public static void InitColor(ref Color c, float r, float g, float b, float a) { c.r = r; c.g = g; c.b = b; c.a = a; }
    public static Color NewColorRgb(float r, float g, float b) => NewColor(r, g, b, 1f);
    public static void InitColorRgb(ref Color c, float r, float g, float b) { c.r = r; c.g = g; c.b = b; c.a = 1f; }
    public static Color ColorWhite() => NewColor(1f, 1f, 1f, 1f);
    public static Color ColorBlack() => NewColor(0f, 0f, 0f, 1f);
    public static Color ColorGray() => NewColor(.5f, .5f, .5f, 1f);
    public static Color ColorClear() => default;
    public static Color32 NewColor32(byte r, byte g, byte b, byte a) { Color32 c = default; c.r = r; c.g = g; c.b = b; c.a = a; return c; }
    public static void InitColor32(ref Color32 c, byte r, byte g, byte b, byte a) { c.rgba = 0; c.r = r; c.g = g; c.b = b; c.a = a; }

    // ── RaycastHit2D (当たった物 collider は ID からの引き当てなので差し替えない) ──
    public static float HitDistance(ref RaycastHit2D h) => h.m_Distance;
    public static Vector2 HitPoint(ref RaycastHit2D h) => h.m_Point;
    public static Vector2 HitNormal(ref RaycastHit2D h) => h.m_Normal;

    // ── Mathf (整数への丸めと大小だけ) ──
    public static int CeilToInt(float f) => (int)Math.Ceiling(f);
    public static int FloorToInt(float f) => (int)Math.Floor(f);
    public static int RoundToInt(float f) => (int)Math.Round(f);
    public static float MaxF(float a, float b) => a > b ? a : b;
    public static float MinF(float a, float b) => a < b ? a : b;
    public static int MaxI(int a, int b) => a > b ? a : b;
    public static int MinI(int a, int b) => a < b ? a : b;
    public static float AbsF(float f) => Math.Abs(f);
    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}

// この属性の付いた型の中は差し替えない (差し替え前のゲーム側の計算と突き合わせる確認用)
[AttributeUsage(AttributeTargets.Class)]
internal sealed class NoStructInlineAttribute : Attribute { }
