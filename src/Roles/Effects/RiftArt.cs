using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 空間の裂け目の線 (全員の画面)。2 つの空間の間の何も無い溝 (暗い芯) を、左を青・右を桃の細く明るい縁が挟む。
// 画面のせん断 (RiftView) で線の左右は逆向きにずれるが、どちらも線に沿う向きなので、この線は割れずに切れ目を覆う。開く時は真ん中から伸びる。
// 全部の線を 1 枚の網にまとめ、開閉の途中は毎フレーム、揺らぎだけの間は 20 回/秒だけ書き直す
internal static unsafe class RiftArt
{
    private const int Slots = 32;                 // 裂け目 16 + 閉じかけの物
    private const int Cols = 12, Rows = 7, Verts = Cols * Rows;
    private const float OpenTime = 0.18f, CloseTime = 0.3f, BurnTime = 0.35f;
    private const float EndTaper = 0.35f;         // 両端の細る長さ
    private const float IdleRate = 1f / 20f;      // 揺らぎだけの間の書き直しの間隔
    // 透明物は網の中心の z で前後が決まる。人より手前・視界の影の板 (-5) より奥 (暗い所では線も見えない)
    private const float Z = -1.2f;

    // 帯の横の位置 (線からの離れ)・濃さ・色 (0 = 左の青, 1 = 白, 2 = 右の桃, 3 = 溝の暗さ)
    private static readonly float[] RowAcross = { -0.09f, -0.03f, -0.018f, 0f, 0.018f, 0.03f, 0.09f };
    private static readonly float[] RowA = { 0f, 0.95f, 1f, 1f, 1f, 0.95f, 0f };
    private static readonly int[] RowTint = { 0, 1, 3, 3, 3, 1, 2 };

    private struct Seg
    {
        public bool On;
        public byte Id;
        public float X0, Y0, Ux, Uy, Len;
        public float T0, TClose;
    }

    private static readonly Seg[] Segs = new Seg[Slots];
    private static GameObject _go;
    private static Mesh _mesh;
    private static Il2CppStructArray<Vector3> _v;
    private static Il2CppStructArray<Color32> _c;
    private static Material _mat;
    private static int _gen = -1, _active;
    private static float _nextBuild;
    private static bool _busy; // 開く・閉じるの途中 (毎フレーム書き直す)

    internal static void Open(in SpaceRifts.Rift rf)
    {
        // 空いた枠が無ければ閉じかけの枠を使う (上限を超えると古い線を閉じてすぐ新しい線を開く)
        int slot = -1;
        for (int i = 0; i < Slots && slot < 0; i++)
            if (!Segs[i].On) slot = i;
        for (int i = 0; i < Slots && slot < 0; i++)
            if (Segs[i].TClose >= 0f) slot = i;
        if (slot < 0) return;
        float now = Time.time;
        Segs[slot] = new Seg { On = true, Id = rf.Id, X0 = rf.X0, Y0 = rf.Y0, Ux = rf.Ux, Uy = rf.Uy, Len = rf.Len, T0 = now, TClose = -1f };
        Wake();
    }

    internal static void Close(byte id)
    {
        for (int i = 0; i < Slots; i++)
            if (Segs[i].On && Segs[i].Id == id && Segs[i].TClose < 0f) Segs[i].TClose = Time.time;
        _busy = true;
    }

    internal static void Clear()
    {
        for (int i = 0; i < Slots; i++) Segs[i].On = false;
        _active = 0;
        if (_go) _go.SetActive(false);
    }

    private static void Wake()
    {
        _busy = true;
        _nextBuild = 0f;
        _active = 1;
    }

    // 毎フレーム (Plugin)。何も出ていなければ数の比較だけで帰る
    internal static void Tick()
    {
        if (_active == 0) return;
        float now = Time.time;
        if (!_busy && now < _nextBuild) return;
        if (!Ensure()) { Clear(); return; }
        _nextBuild = now + IdleRate;
        if (!Build(now))
        {
            _active = 0;
            _go.SetActive(false);
        }
    }

    private static bool Ensure()
    {
        var ship = ShipStatus.Instance;
        if (!ship) return false;
        if (_go && _gen == GameClock.ShipGen)
        {
            if (!_go.activeSelf) _go.SetActive(true);
            return true;
        }
        _gen = GameClock.ShipGen;
        // GameObject は前の船ごと消えるが、Mesh は親付けでは消えない
        if (_mesh) UnityEngine.Object.Destroy(_mesh);
        _go = new GameObject("MrpSpaceRifts");
        _go.transform.SetParent(ship.transform, false);
        // 船の根は倍率や左右反転を持つことがあるので、網は世界の座標のまま置けるよう根の変換を打ち消す
        _go.transform.position = Vector3.zero;
        _go.transform.rotation = Quaternion.identity;
        Vector3 ls = ship.transform.lossyScale;
        _go.transform.localScale = FxMath.V3(1f / ls.x, 1f / ls.y, 1f / ls.z);
        _v = new Il2CppStructArray<Vector3>((long)(Slots * Verts));
        _c = new Il2CppStructArray<Color32>((long)(Slots * Verts));
        var idx = new Il2CppStructArray<int>((long)(Slots * (Cols - 1) * (Rows - 1) * 6));
        int o = 0;
        for (int l = 0; l < Slots; l++)
        for (int c = 0; c < Cols - 1; c++)
        for (int r = 0; r < Rows - 1; r++)
        {
            int a = l * Verts + c * Rows + r, b = a + 1, d = a + Rows, e = d + 1;
            idx[o++] = a; idx[o++] = d; idx[o++] = b;
            idx[o++] = b; idx[o++] = d; idx[o++] = e;
        }
        _mesh = new Mesh { name = "MrpSpaceRifts" };
        _mesh.vertices = _v;
        _mesh.colors32 = _c;
        _mesh.triangles = idx;
        _mesh.bounds = new Bounds(FxMath.V3(0f, 0f, Z), FxMath.V3(1000f, 1000f, 0.1f));
        _go.AddComponent<MeshFilter>().mesh = _mesh;
        var mr = _go.AddComponent<MeshRenderer>();
        if (!_mat)
        {
            var shader = Shader.Find("Sprites/Default");
            _mat = new Material(shader);
            _mat.hideFlags = HideFlags.HideAndDontSave;
        }
        mr.sharedMaterial = _mat;
        return true;
    }

    // 出ている物が残っていれば true
    private static bool Build(float now)
    {
        bool any = false, busy = false;
        float* vp = (float*)((byte*)_v.Pointer + 4 * IntPtr.Size);
        byte* cp = (byte*)_c.Pointer + 4 * IntPtr.Size;
        for (int l = 0; l < Slots; l++)
        {
            ref var s = ref Segs[l];
            int vb = l * Verts;
            float grow = 1f, alpha = 1f, burn = 0f;
            if (s.On)
            {
                float age = now - s.T0;
                if (age < OpenTime) { grow = age / OpenTime; grow = 1f - (1f - grow) * (1f - grow); busy = true; }
                if (age < OpenTime + BurnTime) { burn = 1f - FxMath.Max(0f, age - OpenTime) / BurnTime; busy = true; }
                if (s.TClose >= 0f)
                {
                    float c = (now - s.TClose) / CloseTime;
                    if (c >= 1f) s.On = false;
                    else { alpha = 1f - c; busy = true; }
                }
            }
            if (!s.On)
            {
                for (int v = vb; v < vb + Verts; v++)
                {
                    vp[v * 3] = vp[v * 3 + 1] = vp[v * 3 + 2] = 0f;
                    cp[v * 4 + 3] = 0;
                }
                continue;
            }
            any = true;
            WriteSeg(vp, cp, vb, in s, now, grow, alpha, burn);
        }
        _busy = busy;
        _mesh.vertices = _v;
        _mesh.colors32 = _c;
        // 透明物はカメラから網の範囲の中心までの距離で並ぶので、中心をカメラの真ん中に置いて z だけで前後が決まるようにする
        var cam = Camera.main;
        if (cam)
        {
            Vector3 at = cam.transform.position;
            _mesh.bounds = new Bounds(FxMath.V3(at.x, at.y, Z), FxMath.V3(1000f, 1000f, 0.1f));
        }
        return any;
    }

    private static void WriteSeg(float* vp, byte* cp, int vb, in Seg s, float now, float grow, float alpha, float burn)
    {
        float nx = -s.Uy, ny = s.Ux;
        // 真ん中から両へ伸びる
        float a0 = s.Len * 0.5f * (1f - grow), a1 = s.Len * 0.5f * (1f + grow);
        float span = a1 - a0;
        for (int c = 0; c < Cols; c++)
        {
            float k = c / (float)(Cols - 1);
            float along = a0 + span * k;
            float cx = s.X0 + s.Ux * along, cy = s.Y0 + s.Uy * along;
            // 両端は細る
            float endD = FxMath.Min(along - a0, a1 - along);
            float taper = FxMath.Clamp01(endD / EndTaper);
            taper = taper * (2f - taper);
            // 縁の明るさがわずかに流れる揺らぎ (溝の暗さは揺らさない)
            float sh = 0.88f + 0.12f * FxMath.Sin(now * 3.1f + c * 0.9f + s.Id * 1.7f);
            // 長さに沿って青 → 桃へ寄る
            float hue = k;
            for (int r = 0; r < Rows; r++)
            {
                int v = vb + c * Rows + r;
                float off = RowAcross[r] * taper * (1f + 0.6f * burn);
                vp[v * 3] = cx + nx * off;
                vp[v * 3 + 1] = cy + ny * off;
                vp[v * 3 + 2] = Z;
                float rr, gg, bb;
                int tint = RowTint[r];
                if (tint == 3) { rr = 0.02f; gg = 0.02f; bb = 0.05f; }
                else if (tint == 1) { rr = gg = bb = 1f; }
                else if (tint == 0) { rr = 0.30f + 0.25f * hue; gg = 0.62f - 0.12f * hue; bb = 1f; }
                else { rr = 1f; gg = 0.42f + 0.10f * (1f - hue); bb = 0.72f + 0.2f * (1f - hue); }
                // 焼けている間は白へ寄る
                rr += (1f - rr) * burn * 0.7f;
                gg += (1f - gg) * burn * 0.7f;
                bb += (1f - bb) * burn * 0.7f;
                float a = RowA[r] * alpha * (tint == 3 ? 1f : sh) * taper;
                cp[v * 4] = (byte)(rr * 255f);
                cp[v * 4 + 1] = (byte)(gg * 255f);
                cp[v * 4 + 2] = (byte)(bb * 255f);
                cp[v * 4 + 3] = (byte)(FxMath.Clamp01(a) * 255f);
            }
        }
    }

    internal static string Describe()
    {
        int n = 0;
        for (int i = 0; i < Slots; i++) if (Segs[i].On) n++;
        return $"segs={n} active={_active} busy={_busy}";
    }
}
