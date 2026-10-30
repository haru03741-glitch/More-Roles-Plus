using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 空間の裂け目の見た目: メインカメラが描き上げた画面を、斬った線を境に線に沿ってせん断して描き直す (MRP/RiftWarp)。
// 線の左右は逆向きに半分ずつすべり、ずれは線の上でだけ切れて、線から離れるほど・両端へ近づくほど滑らかに 0 へ戻る。
// 画面の画素をずらすだけなので、物が二つに写ることはなく、線をまたいで立つ人だけが体の途中で分かれて見える。
// 視界の影まで描いた後の画面をずらすので、暗い所は暗いまま動き、壁の向こうが見えることはない。HUD は別のカメラなのでずれない。
// 位置や当たり判定は変わらない (見た目だけ)。全部の裂け目を 1 枚の網 (裂け目ごとの四角) にまとめ、画面に入っている時だけ出す
internal static class RiftView
{
    private const int Slots = 8;            // 同時に残る線の数と同じ
    private const float OpenTime = 0.25f, CloseTime = 0.3f;
    private static float _width = 3f;       // 線から左右へ、ずれが 0 へ戻るまでの幅 (広いほど遠くの景色まで曲がる)
    private static float _shiftScale = 1f;  // 確かめ用: ずれ幅に掛ける値

    private struct View
    {
        public bool On;
        public byte Id;
        public float X0, Y0, Ux, Uy, Len, Shift;
        public float Born, TClose;
    }

    private static readonly View[] Views = new View[Slots];
    private static GameObject _go;
    private static Mesh _mesh;
    private static Material _mat;
    private static Il2CppStructArray<Vector3> _v;
    private static Il2CppStructArray<Vector2> _uv0, _uv1, _uv2;
    private static int _gen = -1;
    private static bool _busy = true; // 開く・閉じるの途中か、一覧が変わった (網を書き直す)
    private static bool _shown;       // 網を出しているか
    private static bool _anyOn;       // 出ている裂け目 (閉じかけを含む) があるか

    // 人とカメラが動き終わった後に (Plugin の LateUpdate)
    internal static void LateTick()
    {
        var rifts = SpaceRifts.All;
        // 裂け目の無い間は数の比較だけで帰る
        if (rifts.Count == 0 && !_anyOn) return;
        _anyOn = Sync(rifts);
        if (!_anyOn) { Show(false); return; }
        var main = Camera.main;
        if (!main || !ShipStatus.Instance || MeetingHud.Instance || ExileController.Instance || !InView(main)) { Show(false); return; }
        if (!Ensure()) return;
        Show(true);
        if (_busy) _busy = Write(Time.time);
    }

    private static void Show(bool on)
    {
        if (_shown == on) return;
        _shown = on;
        if (_go) _go.SetActive(on);
    }

    // 裂け目の一覧とそろえる: 増えた物を開き、消えた物を閉じ始める。出ている物 (閉じかけを含む) があれば true
    private static bool Sync(List<SpaceRifts.Rift> rifts)
    {
        float now = Time.time;
        bool any = false;
        for (int s = 0; s < Slots; s++)
        {
            ref var v = ref Views[s];
            if (!v.On) continue;
            if (v.TClose < 0f && IndexOf(rifts, v.Id) < 0) { v.TClose = now; _busy = true; }
            if (v.TClose >= 0f && now - v.TClose >= CloseTime) { v.On = false; _busy = true; continue; }
            any = true;
        }
        for (int i = 0; i < rifts.Count; i++)
        {
            var rf = rifts[i];
            bool have = false;
            for (int s = 0; s < Slots; s++)
                if (Views[s].On && Views[s].TClose < 0f && Views[s].Id == rf.Id) { have = true; break; }
            if (have) continue;
            int slot = -1;
            for (int s = 0; s < Slots && slot < 0; s++) if (!Views[s].On) slot = s;
            for (int s = 0; s < Slots && slot < 0; s++) if (Views[s].TClose >= 0f) slot = s;
            if (slot < 0) continue;
            Views[slot] = new View { On = true, Id = rf.Id, X0 = rf.X0, Y0 = rf.Y0, Ux = rf.Ux, Uy = rf.Uy, Len = rf.Len, Shift = rf.Shift, Born = now, TClose = -1f };
            _busy = true;
            any = true;
        }
        return any;
    }

    private static int IndexOf(List<SpaceRifts.Rift> rifts, byte id)
    {
        for (int i = 0; i < rifts.Count; i++) if (rifts[i].Id == id) return i;
        return -1;
    }

    // どれかの裂け目の四角 (線の長さ × 左右に幅) が画面に掛かるか
    private static bool InView(Camera main)
    {
        Vector3 cp = main.transform.position;
        float cx = cp.x, cy = cp.y, hh = main.orthographicSize, hw = hh * main.aspect;
        for (int s = 0; s < Slots; s++)
        {
            ref var v = ref Views[s];
            if (!v.On) continue;
            float x1 = v.X0 + v.Ux * v.Len, y1 = v.Y0 + v.Uy * v.Len;
            float minX = FxMath.Min(v.X0, x1) - _width, maxX = FxMath.Max(v.X0, x1) + _width;
            float minY = FxMath.Min(v.Y0, y1) - _width, maxY = FxMath.Max(v.Y0, y1) + _width;
            if (maxX > cx - hw && minX < cx + hw && maxY > cy - hh && minY < cy + hh) return true;
        }
        return false;
    }

    // 網を書き直す。開く・閉じるの途中なら true (次のフレームも書く)
    private static unsafe bool Write(float now)
    {
        float* vp = (float*)((byte*)_v.Pointer + 4 * System.IntPtr.Size);
        float* u0 = (float*)((byte*)_uv0.Pointer + 4 * System.IntPtr.Size);
        float* u1 = (float*)((byte*)_uv1.Pointer + 4 * System.IntPtr.Size);
        float* u2 = (float*)((byte*)_uv2.Pointer + 4 * System.IntPtr.Size);
        bool busy = false;
        float w = _width;
        for (int s = 0; s < Slots; s++)
        {
            ref var v = ref Views[s];
            int b = s * 4;
            if (!v.On)
            {
                for (int k = b; k < b + 4; k++) { vp[k * 3] = vp[k * 3 + 1] = vp[k * 3 + 2] = 0f; u1[k * 2] = u1[k * 2 + 1] = 0f; }
                continue;
            }
            // 開く時は 0 から滑らかに開き、閉じる時は 0 へ戻る
            float open = FxMath.Clamp01((now - v.Born) / OpenTime);
            if (open < 1f) busy = true;
            if (v.TClose >= 0f) { open = FxMath.Min(open, 1f - FxMath.Clamp01((now - v.TClose) / CloseTime)); busy = true; }
            open = open * open * (3f - 2f * open);
            float half = 0.5f * v.Shift * _shiftScale * open;
            float nx = -v.Uy, ny = v.Ux;
            for (int k = 0; k < 4; k++)
            {
                float t = (k & 2) == 0 ? 0f : v.Len, d = (k & 1) == 0 ? -w : w;
                int i = b + k;
                vp[i * 3] = v.X0 + v.Ux * t + nx * d;
                vp[i * 3 + 1] = v.Y0 + v.Uy * t + ny * d;
                vp[i * 3 + 2] = 0f;
                u0[i * 2] = t;
                u0[i * 2 + 1] = d;
                u1[i * 2] = v.Ux * half;
                u1[i * 2 + 1] = v.Uy * half;
                u2[i * 2] = v.Len;
                u2[i * 2 + 1] = w;
            }
        }
        _mesh.vertices = _v;
        _mesh.uv = _uv0;
        _mesh.uv2 = _uv1;
        _mesh.uv3 = _uv2;
        return busy;
    }

    private static bool Ensure()
    {
        if (_gen == GameClock.ShipGen && _go) return true;
        var ship = ShipStatus.Instance;
        var src = MrpBundle.RiftWarpMaterial;
        if (!ship || !src) return false;
        _gen = GameClock.ShipGen;
        // GameObject は前の船ごと消えるが、Mesh は親付けでは消えない
        if (_mesh) Object.Destroy(_mesh);
        _go = new GameObject("MrpRiftWarp");
        _shown = true;
        _go.transform.SetParent(ship.transform, false);
        // 船の根は倍率や左右反転を持つことがあるので、網は世界の座標のまま置けるよう根の変換を打ち消す
        _go.transform.position = Vector3.zero;
        _go.transform.rotation = Quaternion.identity;
        Vector3 ls = ship.transform.lossyScale;
        _go.transform.localScale = FxMath.V3(1f / ls.x, 1f / ls.y, 1f / ls.z);
        _v = new Il2CppStructArray<Vector3>((long)(Slots * 4));
        _uv0 = new Il2CppStructArray<Vector2>((long)(Slots * 4));
        _uv1 = new Il2CppStructArray<Vector2>((long)(Slots * 4));
        _uv2 = new Il2CppStructArray<Vector2>((long)(Slots * 4));
        var idx = new Il2CppStructArray<int>((long)(Slots * 6));
        for (int s = 0, o = 0; s < Slots; s++)
        {
            int a = s * 4;
            idx[o++] = a; idx[o++] = a + 2; idx[o++] = a + 1;
            idx[o++] = a + 1; idx[o++] = a + 2; idx[o++] = a + 3;
        }
        _mesh = new Mesh { name = "MrpRiftWarp" };
        _mesh.vertices = _v;
        _mesh.uv = _uv0;
        _mesh.uv2 = _uv1;
        _mesh.uv3 = _uv2;
        _mesh.triangles = idx;
        // 画面に入っているかは自分で見るので、カメラに切られないよう広く取る
        _mesh.bounds = new Bounds(Vector3.zero, FxMath.V3(2000f, 2000f, 100f));
        _go.AddComponent<MeshFilter>().mesh = _mesh;
        if (!_mat)
        {
            _mat = new Material(src) { name = "MrpRiftWarp" };
            _mat.hideFlags |= HideFlags.HideAndDontSave;
        }
        _go.AddComponent<MeshRenderer>().sharedMaterial = _mat;
        _busy = true;
        return true;
    }

    internal static void Clear()
    {
        for (int s = 0; s < Slots; s++) Views[s].On = false;
        _busy = true;
        _anyOn = false;
        Show(false);
    }

    // 確かめ用: ずれの幅・ずれ幅に掛ける値・線の真上のずらさない溝の半幅・両端の細る長さの上限をその場で変える
    internal static void Tune(float width, float shiftScale, float gap, float taper)
    {
        _width = FxMath.Max(0.5f, width);
        _shiftScale = shiftScale;
        if (_mat) { _mat.SetFloat("_Gap", gap); _mat.SetFloat("_TaperMax", FxMath.Max(0.01f, taper)); }
        _busy = true;
    }

    internal static string Describe()
    {
        int n = 0;
        for (int s = 0; s < Slots; s++) if (Views[s].On) n++;
        return $"views={n} on={_shown} width={_width:0.##} shiftScale={_shiftScale:0.##} mat={(bool)MrpBundle.RiftWarpMaterial}";
    }
}
