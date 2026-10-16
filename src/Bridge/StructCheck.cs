using System;
using MoreRolesPlus.Fx;
using UnityEngine;
using U = MoreRolesPlus.Fx.UnityStructs;

namespace MoreRolesPlus.Bridge;

// `structcheck [回数]`: UnityStructs (mod の中の計算) とゲーム側の Unity の値型のメソッドを乱数の入力で突き合わせ、
// 結果がビット単位で同じかを数える。この型の中はビルド後の差し替えをしない (ゲーム側を呼ぶため)
[NoStructInline]
internal static class StructCheck
{
    private static int _n, _bad;
    private static string _first;

    public static void Register()
    {
        TestBridge.Register("structcheck", "[回数=2000] mod の中の Vector2/Rect/Bounds などの計算がゲーム側とビット単位で同じか (乱数の入力)", (args, reply) =>
        {
            int count = int.TryParse(args.Trim(), out int c) ? Math.Clamp(c, 10, 100000) : 2000;
            _n = _bad = 0;
            _first = null;
            var rng = new System.Random(12345);
            float R() => rng.Next(8) switch
            {
                0 => 0f,
                1 => (float)(rng.NextDouble() * 2e-5 - 1e-5), // 正規化の閾値の近く
                2 => -(float)(rng.NextDouble() * 50),
                3 => (float)Math.Round(rng.NextDouble() * 40 - 20, 2),
                _ => (float)(rng.NextDouble() * 200 - 100),
            };
            for (int i = 0; i < count; i++)
            {
                var a = new Vector2(R(), R());
                var b = new Vector2(R(), R());
                var a3 = new Vector3(R(), R(), R());
                var b3 = new Vector3(R(), R(), R());
                float s = R(), t = R();
                if (s == 0f) s = 1.5f;
                V("V2.ctor", new Vector2(a.x, b.y), U.NewVector2(a.x, b.y));
                V("V2+", a + b, U.V2Add(a, b));
                V("V2-", a - b, U.V2Sub(a, b));
                V("V2*s", a * s, U.V2MulVS(a, s));
                V("s*V2", s * a, U.V2MulSV(s, a));
                V("V2/s", a / s, U.V2Div(a, s));
                V("-V2", -a, U.V2Neg(a));
                V("V2<-V3", (Vector2)a3, U.V2FromV3(a3));
                V3("V3<-V2", (Vector3)a, U.V3FromV2(a));
                F("V2.sqrMag", a.sqrMagnitude, U.V2SqrMagnitude(ref a));
                F("V2.mag", a.magnitude, U.V2Magnitude(ref a));
                V("V2.norm", a.normalized, U.V2Normalized(ref a));
                var near = new Vector2(a.x + R() * 1e-6f, a.y);
                Bo("V2==", a == near, U.V2Equal(a, near));
                Bo("V2!=", a != b, U.V2NotEqual(a, b));
                F("V2.Dot", Vector2.Dot(a, b), U.V2Dot(a, b));
                V("V2.Lerp", Vector2.Lerp(a, b, t), U.V2Lerp(a, b, t));
                V3("V3+", a3 + b3, U.V3Add(a3, b3));
                V3("V3-", a3 - b3, U.V3Sub(a3, b3));
                V3("V3*s", a3 * s, U.V3MulVS(a3, s));
                V3("V3/s", a3 / s, U.V3Div(a3, s));
                V3("-V3", -a3, U.V3Neg(a3));
                F("V3.mag", a3.magnitude, U.V3Magnitude(ref a3));
                F("V3.sqrMag", a3.sqrMagnitude, U.V3SqrMagnitude(ref a3));
                V3("V3.ctorXY", new Vector3(s, t), U.NewVector3XY(s, t));

                var r = new Rect(R(), R(), R(), R());
                var o = Rect.MinMaxRect(R(), R(), R(), R());
                var ro = U.RectMinMax(o.m_XMin, o.m_YMin, o.m_XMin + o.m_Width, o.m_YMin + o.m_Height);
                F("Rect.xMin", r.xMin, U.RectXMin(ref r));
                F("Rect.yMin", r.yMin, U.RectYMin(ref r));
                F("Rect.xMax", r.xMax, U.RectXMax(ref r));
                F("Rect.yMax", r.yMax, U.RectYMax(ref r));
                F("Rect.w", r.width, U.RectWidth(ref r));
                F("Rect.h", r.height, U.RectHeight(ref r));
                V("Rect.center", r.center, U.RectCenter(ref r));
                V("Rect.size", r.size, U.RectSize(ref r));
                V("Rect.pos", r.position, U.RectPosition(ref r));
                Bo("Rect.Contains", r.Contains(a), U.RectContains2(ref r, a));
                var edge = new Vector2(r.xMax, r.yMin);
                Bo("Rect.ContainsEdge", r.Contains(edge), U.RectContains2(ref r, edge));
                Bo("Rect.Contains3", r.Contains(a3), U.RectContains3(ref r, a3));
                Bo("Rect.Overlaps", r.Overlaps(o), U.RectOverlaps(ref r, o));
                F("MinMaxRect.w", Rect.MinMaxRect(a.x, a.y, b.x, b.y).width, U.RectMinMax(a.x, a.y, b.x, b.y).m_Width);
                F("MinMaxRect.x", o.m_XMin, ro.m_XMin);

                var ri = new RectInt(rng.Next(-50, 50), rng.Next(-50, 50), rng.Next(-20, 20), rng.Next(-20, 20));
                I("RectInt.xMin", ri.xMin, U.RectIntXMin(ref ri));
                I("RectInt.yMin", ri.yMin, U.RectIntYMin(ref ri));
                I("RectInt.xMax", ri.xMax, U.RectIntXMax(ref ri));
                I("RectInt.yMax", ri.yMax, U.RectIntYMax(ref ri));
                I("RectInt.w", ri.width, U.RectIntWidth(ref ri));
                I("RectInt.h", ri.height, U.RectIntHeight(ref ri));

                var bd = new Bounds(a3, b3);
                V3("Bounds.center", bd.center, U.BoundsCenter(ref bd));
                V3("Bounds.ext", bd.extents, U.BoundsExtents(ref bd));
                V3("Bounds.size", bd.size, U.BoundsSize(ref bd));
                V3("Bounds.min", bd.min, U.BoundsMin(ref bd));
                V3("Bounds.max", bd.max, U.BoundsMax(ref bd));

                C("Color4", new Color(s, t, a.x, b.y), U.NewColor(s, t, a.x, b.y));
                C("Color3", new Color(s, t, a.x), U.NewColorRgb(s, t, a.x));
                byte cr = (byte)rng.Next(256), cg = (byte)rng.Next(256), cb = (byte)rng.Next(256), ca = (byte)rng.Next(256);
                var c32 = new Color32(cr, cg, cb, ca);
                I("Color32.rgba", c32.rgba, U.NewColor32(cr, cg, cb, ca).rgba);

                I("CeilToInt", Mathf.CeilToInt(s), U.CeilToInt(s));
                I("FloorToInt", Mathf.FloorToInt(s), U.FloorToInt(s));
                I("RoundToInt", Mathf.RoundToInt(s), U.RoundToInt(s));
                F("Max", Mathf.Max(s, t), U.MaxF(s, t));
                F("Min", Mathf.Min(s, t), U.MinF(s, t));
                F("Abs", Mathf.Abs(s), U.AbsF(s));
                F("Clamp01", Mathf.Clamp01(t / 50f), U.Clamp01(t / 50f));
            }
            C("white", Color.white, U.ColorWhite());
            C("black", Color.black, U.ColorBlack());
            C("gray", Color.gray, U.ColorGray());
            C("clear", Color.clear, U.ColorClear());
            V("zero", Vector2.zero, U.V2Zero());
            V("one", Vector2.one, U.V2One());
            V("right", Vector2.right, U.V2Right());
            V("up", Vector2.up, U.V2Up());
            V3("V3.one", Vector3.one, U.V3One());
            reply($"OK structcheck checks={_n} mismatches={_bad}{(_first != null ? " first=" + _first : "")}");
        });
    }

    private static void Count(string name, bool same, string detail)
    {
        _n++;
        if (same) return;
        _bad++;
        _first ??= $"{name}({detail})";
    }
    private static bool Same(float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y) || (float.IsNaN(x) && float.IsNaN(y));
    private static void F(string n, float x, float y) => Count(n, Same(x, y), $"{x:R} vs {y:R}");
    private static void I(string n, int x, int y) => Count(n, x == y, $"{x} vs {y}");
    private static void Bo(string n, bool x, bool y) => Count(n, x == y, $"{x} vs {y}");
    private static void V(string n, Vector2 x, Vector2 y) => Count(n, Same(x.x, y.x) && Same(x.y, y.y), $"{x.x:R},{x.y:R} vs {y.x:R},{y.y:R}");
    private static void V3(string n, Vector3 x, Vector3 y) => Count(n, Same(x.x, y.x) && Same(x.y, y.y) && Same(x.z, y.z), $"{x.x:R},{x.y:R},{x.z:R} vs {y.x:R},{y.y:R},{y.z:R}");
    private static void C(string n, Color x, Color y) => Count(n, Same(x.r, y.r) && Same(x.g, y.g) && Same(x.b, y.b) && Same(x.a, y.a), $"{x} vs {y}");
}
