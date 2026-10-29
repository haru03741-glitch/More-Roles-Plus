using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles.Neutral;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// サムライの足元の「気」の輪 (本人の画面だけ)。前 (手前) から左右へ気の分だけ満ちる。
// 居合を溜めている間は白く明るくなり、溜め切ると脈打つ。尽きた間は暗い赤で点滅する。
// 輪の奥側には斬った数の刻み (守の道 = 青・修羅へ近づく数 = 赤) を並べる。
// 網は値が刻み (2%) を跨いだ時だけ書き直す (毎フレームは比べるだけ)
internal sealed class SamuraiRing
{
    private const int Segs = 48;
    private const float Rx = 0.40f, Ry = 0.15f, Thick = 0.045f, FeetY = -0.40f;
    private const int MaxMarks = 8;
    private const float MarkW = 0.035f, MarkH = 0.07f, MarkGap = 0.075f;

    private static Material _mat;

    private readonly Samurai _role;
    private GameObject _go;
    private Mesh _mesh;
    private Il2CppStructArray<Color32> _colors; // 持ち回す (書き直すたびに il2cpp の配列を作らない)
    private int _shownKey = -1;

    internal SamuraiRing(Samurai role) => _role = role;

    internal void Tick(bool visible)
    {
        if (!visible)
        {
            if (_go && _go.activeSelf) _go.SetActive(false);
            return;
        }
        if (!Ensure()) return;
        if (!_go.activeSelf) _go.SetActive(true);
        float now = Time.time;
        int ki = (int)(_role.Ki / Samurai.KiMax * 50f);
        float charge = _role.Charge;
        int ch = (int)(charge * 10f);
        int blink = 0;
        // 溜め切った・尽きた間は 8 回/秒で明滅
        if (_role.Spent || charge >= 1f) blink = 1 + ((int)(now * 8f) & 1);
        int key = ki | ch << 6 | blink << 10 | (_role.Spent ? 1 : 0) << 12 | (_role.Drawn ? 1 : 0) << 13
            | System.Math.Min(_role.ImpostorKills, 7) << 14 | System.Math.Min(_role.OtherKills, 7) << 17 | (_role.Fallen ? 1 : 0) << 20;
        if (key == _shownKey) return;
        _shownKey = key;
        Paint(ki / 50f, charge, blink == 2);
    }

    private bool Ensure()
    {
        if (_go) return true;
        var pc = _role.Player;
        if (!pc) return false;
        if (!_mat)
        {
            var shader = Shader.Find("Sprites/Default");
            if (!shader) return false;
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }
        // 体ごと作り直された時は前の網を捨てる (網は GameObject と一緒には消えない)
        if (_mesh) Object.Destroy(_mesh);
        _go = new GameObject("MrpSamuraiRing") { layer = pc.gameObject.layer };
        var tf = _go.transform;
        tf.SetParent(pc.transform, false);
        float scale = pc.transform.localScale.x;
        float inv = scale > 0.01f ? 1f / scale : 1f;
        tf.localScale = FxMath.V3(inv, inv, 1f);
        // 体より奥 (足元の床の上)
        tf.localPosition = FxMath.V3(0f, FeetY * inv, 0.0009f);
        _mesh = new Mesh { name = "MrpSamuraiRing" };
        int quads = Segs + MaxMarks;
        var v = new Vector3[quads * 4];
        var tri = new int[quads * 6];
        for (int i = 0; i < Segs; i++)
        {
            float a0 = (i / (float)Segs) * FxMath.PI * 2f - FxMath.PI * 0.5f;
            float a1 = ((i + 1) / (float)Segs) * FxMath.PI * 2f - FxMath.PI * 0.5f;
            Ring(v, i * 4, a0, a1);
        }
        // 刻みは輪の奥の上に横一列
        for (int m = 0; m < MaxMarks; m++)
        {
            float x = (m - (MaxMarks - 1) * 0.5f) * MarkGap, y = Ry + Thick + 0.03f;
            int o = (Segs + m) * 4;
            v[o] = FxMath.V3(x - MarkW, y, 0f);
            v[o + 1] = FxMath.V3(x + MarkW, y, 0f);
            v[o + 2] = FxMath.V3(x + MarkW * 0.4f, y + MarkH, 0f);
            v[o + 3] = FxMath.V3(x - MarkW * 0.4f, y + MarkH, 0f);
        }
        for (int q = 0; q < quads; q++)
        {
            int o = q * 4, t = q * 6;
            tri[t] = o; tri[t + 1] = o + 2; tri[t + 2] = o + 1;
            tri[t + 3] = o; tri[t + 4] = o + 3; tri[t + 5] = o + 2;
        }
        _mesh.vertices = v;
        _mesh.triangles = tri;
        _colors = new Il2CppStructArray<Color32>((long)(quads * 4));
        _mesh.colors32 = _colors;
        _go.AddComponent<MeshFilter>().mesh = _mesh;
        _go.AddComponent<MeshRenderer>().sharedMaterial = _mat;
        _shownKey = -1;
        return true;
    }

    private static void Ring(Vector3[] v, int o, float a0, float a1)
    {
        float c0 = FxMath.Cos(a0), s0 = FxMath.Sin(a0), c1 = FxMath.Cos(a1), s1 = FxMath.Sin(a1);
        float ix = Rx - Thick, iy = Ry - Thick * 0.45f;
        v[o] = FxMath.V3(c0 * ix, s0 * iy, 0f);
        v[o + 1] = FxMath.V3(c1 * ix, s1 * iy, 0f);
        v[o + 2] = FxMath.V3(c1 * Rx, s1 * Ry, 0f);
        v[o + 3] = FxMath.V3(c0 * Rx, s0 * Ry, 0f);
    }

    // 色を塗り直す。fill = 気 (0〜1)・charge = 居合の溜め・flash = 明滅の明るい側
    private void Paint(float fill, float charge, bool flash)
    {
        bool spent = _role.Spent;
        for (int i = 0; i < Segs; i++)
        {
            // 手前 (真下) から左右へ。0 = 手前・1 = 奥
            float u = ((i + 0.5f) / Segs) * 2f;
            float d = u > 1f ? 2f - u : u;
            Color32 c;
            if (spent) c = flash ? FxMath.Rgba32(220, 40, 60, 230) : FxMath.Rgba32(90, 14, 24, 170);
            else if (d <= fill)
            {
                // 青 #4a8cff → 桃 #ff5fa8 (手前から奥へ)。溜めるほど白へ
                float r = 74f + (255f - 74f) * d, g = 140f + (95f - 140f) * d, b = 255f + (168f - 255f) * d;
                float w = charge >= 1f ? (flash ? 1f : 0.75f) : charge * 0.7f;
                c = FxMath.Rgba32((byte)(r + (255f - r) * w), (byte)(g + (255f - g) * w), (byte)(b + (255f - b) * w), (byte)(_role.Drawn ? 235 : 170));
            }
            else c = FxMath.Rgba32(20, 22, 40, 110);
            int o = i * 4;
            _colors[o] = _colors[o + 1] = _colors[o + 2] = _colors[o + 3] = c;
        }
        // 刻み: 守の道の数 (青・斬った分だけ明るい)、続けてインポスター以外を斬った数 (赤)。修羅に堕ちたら全部赤
        int path = Samurai.PathKills.Value, fall = Samurai.FallKills.Value;
        int imp = _role.ImpostorKills, other = _role.OtherKills;
        for (int m = 0; m < MaxMarks; m++)
        {
            Color32 c = default;
            if (_role.Fallen) c = m < other ? FxMath.Rgba32(200, 30, 50, 240) : default;
            else if (m < path) c = m < imp ? FxMath.Rgba32(120, 190, 255, 250) : FxMath.Rgba32(40, 70, 130, 150);
            else if (m < path + fall && m - path < other) c = FxMath.Rgba32(200, 30, 50, 240);
            int o = (Segs + m) * 4;
            _colors[o] = _colors[o + 1] = _colors[o + 2] = _colors[o + 3] = c;
        }
        _mesh.colors32 = _colors;
    }

    internal void Dispose()
    {
        if (_go) Object.Destroy(_go);
        if (_mesh) Object.Destroy(_mesh);
        _go = null;
        _mesh = null;
    }
}
