using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦の夜の街 (飛ぶ範囲の下の景色)。形は C++ (native/src/flight_city.cpp) が種から作り、
// ここは区画ごとの静的なメッシュに詰めて場面の子に置くだけ (毎フレームの仕事は警報の間の値 2 つ)。
// 街の当たりの表は C++ の街が持つので、街は場面を出るまで消さずにシムへ貸す
internal static unsafe class FlightCity
{
    internal const float Ground = -300f;   // 地面の高さ (飛ぶ場の中心から)
    internal const float FloorY = Ground + 14f; // 飛べる高さの下限 (地面のすぐ上・ビルとは当たりで当たる)
    internal const float FloorMargin = 30f;     // 地面に近づくと引き起こしを始める高さ
    private const int Stride = 44;

    private static delegate* unmanaged[Cdecl]<uint, float, IntPtr> _create;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _count;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int*, int*, float*, int> _info;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int*, void> _fill;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _destroy;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _col;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> _lend;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _bldCount;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int*, float*, int> _bldInfo;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, byte*> _verts;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int, float, int> _mark;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int> _flatten;
    private static delegate* unmanaged[Cdecl]<IntPtr, float*, float, float, float, int, void> _ruin;
    private static delegate* unmanaged[Cdecl]<IntPtr, float*, float*, int, void> _burn;
    private static int _state;
    private static IntPtr _city;

    private static readonly System.Collections.Generic.List<Mesh> Meshes = new();
    private static int _cityId;
    private const float TowerX = 380f, TowerZ = 560f; // 停電が広がり始める点 (電波塔・native と同じ値)
    private const float SirenAt = 1.2f, SpinUp = 4f, WindDown = 5f;
    internal static readonly Vector4 Sky = new(0.075f, 0.006f, 0.012f, 4500f); // 街の霞の色 (停電で暗くする)
    private static Transform _root;
    private static int _alarmId, _skyId;
    private static float _alarmS = -1f, _alarmD, _nextAlarm, _dark;
    private static bool _sirenOn;
    internal static bool Active { get; private set; }

    private static bool Ready()
    {
        if (_state != 0) return _state == 1;
        try
        {
            _create = (delegate* unmanaged[Cdecl]<uint, float, IntPtr>)Need("mrp_flt_city_create");
            _count = (delegate* unmanaged[Cdecl]<IntPtr, int>)Need("mrp_flt_city_count");
            _info = (delegate* unmanaged[Cdecl]<IntPtr, int, int*, int*, float*, int>)Need("mrp_flt_city_info");
            _fill = (delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int*, void>)Need("mrp_flt_city_fill");
            _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Need("mrp_flt_city_destroy");
            _col = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Need("mrp_flt_city_col");
            _lend = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)Need("mrp_flt_city");
            _bldCount = (delegate* unmanaged[Cdecl]<IntPtr, int>)Need("mrp_flt_city_blds");
            _bldInfo = (delegate* unmanaged[Cdecl]<IntPtr, int, int*, float*, int>)Need("mrp_flt_city_bld");
            _verts = (delegate* unmanaged[Cdecl]<IntPtr, int, byte*>)Need("mrp_flt_city_verts");
            _mark = (delegate* unmanaged[Cdecl]<IntPtr, int, int, float, int>)Need("mrp_flt_city_mark");
            _flatten = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)Need("mrp_flt_city_flatten");
            _ruin = (delegate* unmanaged[Cdecl]<IntPtr, float*, float, float, float, int, void>)Need("mrp_flt_ruin");
            _burn = (delegate* unmanaged[Cdecl]<IntPtr, float*, float*, int, void>)Need("mrp_flt_burn");
            _state = 1;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"flight city native: {e.Message}");
            _state = 2;
        }
        return _state == 1;
    }

    private static IntPtr Need(string name)
    {
        IntPtr p = Native.MrpNative.Export(name);
        if (p == IntPtr.Zero) throw new InvalidOperationException($"export not found: {name}");
        return p;
    }

    // 場面の root の子に街を作る。layer = 3D の場面の層
    internal static bool Build(Transform root, int layer, uint seed, IntPtr world)
    {
        Clear();
        if (!Ready() || !MrpBundle.FlightMaterials[4] || !MrpBundle.FlightMaterials[5]) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        IntPtr c = _create(seed, Ground);
        if (c == IntPtr.Zero) return false;
        int n = _count(c), verts = 0, chunks = 0;
        _chunk = new Mesh[n];
        _lampChunk = n - 1;
        float* b = stackalloc float[6];
        try
        {
            for (int k = 0; k < n; k++)
            {
                int vn, inN;
                int kind = _info(c, k, &vn, &inN, b);
                if (kind < 0 || vn == 0 || inN == 0) continue;
                // 16 ビットの添字に収める (超えた区画は描かない・作る側で収まるよう升を切ってある)
                if (vn > 65535) { Plugin.Logger.LogError($"flight city chunk {k} too big: {vn}"); continue; }
                var vbuf = new byte[vn * Stride];
                var ibuf = new int[inN];
                fixed (byte* vp = vbuf)
                fixed (int* ip = ibuf)
                    _fill(c, k, vp, ip);
                var mesh = new Mesh { name = "MrpFlightCity" + k };
                var attrs = new Il2CppStructArray<VertexAttributeDescriptor>((long)5);
                attrs[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
                attrs[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4, 0);
                attrs[2] = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 0);
                attrs[3] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 0);
                attrs[4] = new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4, 0);
                mesh.SetVertexBufferParamsFromArray(vn, attrs);
                fixed (byte* vp = vbuf)
                    mesh.InternalSetVertexBufferData(0, (IntPtr)vp, 0, 0, vn, Stride, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                mesh.triangles = ibuf;
                var mn = new Vector3(b[0], b[1], b[2]);
                var mx = new Vector3(b[3], b[4], b[5]);
                // 崩れる棟は傾いて瓦礫が外へ広がるので、横に余白を足す (区画の端の棟が欠けないように)
                mesh.bounds = new Bounds((mn + mx) * 0.5f, mx - mn + new Vector3(120f, 2f, 120f));
                Meshes.Add(mesh);
                _chunk[k] = mesh;
                var go = new GameObject(mesh.name) { layer = layer };
                go.transform.SetParent(root, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = MrpBundle.FlightMaterials[kind == 1 ? 5 : 4];
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                verts += vn;
                chunks++;
            }
        }
        catch
        {
            _destroy(c);
            throw;
        }
        _city = c;
        if (world != IntPtr.Zero) _lend(world, _col(c));
        _world = world;
        LoadBuildings();
        _cityId = Shader.PropertyToID("_MrpCity");
        _alarmId = Shader.PropertyToID("_MrpCityAlarm");
        _skyId = Shader.PropertyToID("_MrpFltSky");
        _root = root;
        _nextAlarm = 70f + (float)(seed % 50);
        ResetAlarm();
        Active = true;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Plugin.Logger.LogInfo($"flight city: chunks={chunks} verts={verts} {ms:0.0}ms");
        return true;
    }

    // 毎フレーム (街の場面の間だけ): 時々警報が鳴り、街が停電する。塔から外へ明かりが消えていき、
    // サイレンはモーターが回り出すように高さを上げ、明かりが戻る頃に回転が落ちて下がりながら止む
    internal static void Tick(float dt)
    {
        if (!Active) return;
        TickClock(dt);
        if (_alarmS < 0f)
        {
            _nextAlarm -= dt;
            if (_nextAlarm <= 0f) StartAlarm(20f);
            return;
        }
        float s = _alarmS += dt;
        float end = _alarmD + WindDown;
        if (s >= end)
        {
            FlightSound.Siren(false, 0f, 1f);
            _sirenOn = false;
            _nextAlarm = 80f + UnityEngine.Random.value * 70f;
            ResetAlarm();
            return;
        }
        // 街全体の暗さ (空と霞): 前線が街の中ほどへ届く 6 秒で暗くなり、戻り始めて 3 秒で明るさを返す
        float dark = Math.Min(1f, s / 6f) * (1f - Math.Clamp((s - _alarmD) / 3f, 0f, 1f));
        dark = dark * dark * (3f - 2f * dark);
        if (Math.Abs(dark - _dark) > 0.004f || (dark == 0f) != (_dark == 0f))
        {
            _dark = dark;
            Shader.SetGlobalVector(_cityId, new Vector4(dark, Ground, 1f, 0f));
            float k = 1f - 0.45f * dark;
            Shader.SetGlobalVector(_skyId, new Vector4(Sky.x * k, Sky.y * k, Sky.z * k, Sky.w));
        }
        // 毎フレーム書く値は構造体のフィールドだけを変えて渡す (Vector4 の生成は interop)
        _alarmV.x = s;
        Shader.SetGlobalVector(_alarmId, _alarmV);
        if (s >= SirenAt)
        {
            float up = Math.Min(1f, (s - SirenAt) / SpinUp);
            float down = Math.Clamp((s - (_alarmD - 1f)) / WindDown, 0f, 1f);
            float spin = (1f - (1f - up) * (1f - up)) * (1f - down * down);
            FlightSound.Siren(true, 0.6f * spin, 0.5f + 0.5f * spin);
            _sirenOn = true;
        }
    }

    private static Vector4 _alarmV;

    // sec = 停電が続く秒 (その後 5 秒でサイレンが止む)
    internal static void StartAlarm(float sec)
    {
        if (!Active) return;
        if (_sirenOn) FlightSound.Siren(false, 0f, 1f);
        _sirenOn = false;
        _alarmS = 0f;
        _alarmD = Math.Max(sec, 2f);
        Vector3 o = _root ? _root.position : Vector3.zero;
        _alarmV.x = 0f;
        _alarmV.y = _alarmD;
        _alarmV.z = o.x + TowerX;
        _alarmV.w = o.z + TowerZ;
        FlightSound.Blackout();
    }

    private static void ResetAlarm()
    {
        _alarmS = -1f;
        _dark = 0f;
        Shader.SetGlobalVector(_cityId, new Vector4(0f, Ground, 1f, 0f));
        Shader.SetGlobalVector(_alarmId, new Vector4(-1000f, 0f, 0f, 0f));
        Shader.SetGlobalVector(_skyId, Sky);
    }

    // 世界を消す前に呼ぶ (シムが街の当たりを指したまま街を消さない)
    internal static void Unlend(IntPtr world)
    {
        if (_state == 1 && world != IntPtr.Zero) _lend(world, IntPtr.Zero);
    }

    // ---- 壊れるビル ----
    // 棟ごとの耐久は C# が持つ (今は自分の画面だけで決める)。半分を切ると燃え、0 で崩れる。
    // 状態が変わった時だけ、その棟の頂点の範囲を C++ の街の控えからメッシュへ書き直す (毎フレームの書き込みは無い)
    private static Mesh[] _chunk = Array.Empty<Mesh>();
    private static int _lampChunk;
    private static IntPtr _world;
    private static int[] _range = Array.Empty<int>();   // 棟ごとに 5 個 (区画・頂点の始め・終わり・灯の始め・終わり)
    private static float[] _box = Array.Empty<float>(); // 棟ごとに 6 個 (足元の最小 xyz・最大 xyz)
    private static float[] _hp = Array.Empty<float>(), _hpMax = Array.Empty<float>();
    private static byte[] _bstate = Array.Empty<byte>(); // 0 立っている・1 燃えている・2 崩れた
    private static float _clock, _clockUntil = -1f, _clockScale = 1f;
    // 崩れ落ちるのを待って当たりを下げる棟 (落ちている間は見えているビルに弾も船も当たる)
    private static readonly System.Collections.Generic.List<(int Bld, float At)> Flatten = new();
    private static int _clockId;
    internal static int Collapsed { get; private set; }

    private static void LoadBuildings()
    {
        int n = _bldCount(_city);
        _range = new int[n * 5];
        _box = new float[n * 6];
        _hp = new float[n];
        _hpMax = new float[n];
        _bstate = new byte[n];
        fixed (int* r = _range)
        fixed (float* b = _box)
            for (int k = 0; k < n; k++)
            {
                _bldInfo(_city, k, r + k * 5, b + k * 6);
                float w = b[k * 6 + 3] - b[k * 6], d = b[k * 6 + 5] - b[k * 6 + 2], h = b[k * 6 + 4] - b[k * 6 + 1];
                // 大きな棟ほど硬い (高さ 300・足元 60 四方で約 210)
                _hp[k] = _hpMax[k] = 30f + w * d * h / 6000f;
            }
        _clock = 0f;
        _clockUntil = -1f;
        Flatten.Clear();
        _clockScale = 1f;
        Collapsed = 0;
        _clockId = Shader.PropertyToID("_MrpCityClock");
        Shader.SetGlobalFloat(_clockId, 0f);
    }

    // 崩れの時計: 崩れている棟がある間だけ進めてシェーダへ渡す (止まっている間は書かない)
    private static void TickClock(float dt)
    {
        if (_clockUntil < 0f) return;
        _clock += dt * _clockScale;
        Shader.SetGlobalFloat(_clockId, _clock);
        for (int i = Flatten.Count - 1; i >= 0; i--)
            if (_clock >= Flatten[i].At) { _flatten(_city, Flatten[i].Bld); Flatten.RemoveAt(i); }
        if (_clock > _clockUntil) _clockUntil = -1f;
    }

    // 確かめ用: 崩れの時計の速さ (0.1 = 10 倍ゆっくり)
    internal static void SetClockScale(float k) => _clockScale = Math.Max(0f, k);

    internal static int BuildingCount => _bstate.Length;

    // p (場面の中の位置) に一番近い立っている棟 (無ければ -1)
    internal static int Nearest(Vector3 p, float maxDist)
    {
        int best = -1;
        float bd = maxDist * maxDist;
        for (int k = 0; k < _bstate.Length; k++)
        {
            if (_bstate[k] == 2) continue;
            float cx = Math.Clamp(p.x, _box[k * 6], _box[k * 6 + 3]), cy = Math.Clamp(p.y, _box[k * 6 + 1], _box[k * 6 + 4]), cz = Math.Clamp(p.z, _box[k * 6 + 2], _box[k * 6 + 5]);
            float d = (p.x - cx) * (p.x - cx) + (p.y - cy) * (p.y - cy) + (p.z - cz) * (p.z - cz);
            if (d < bd) { bd = d; best = k; }
        }
        return best;
    }

    // 棟 k を dmg 削る (hit = 当たった所・場面の中)。燃え始めた/崩れ始めたら true
    internal static bool Damage(int k, float dmg, Vector3 hit)
    {
        if (!Active || _city == IntPtr.Zero || k < 0 || k >= _bstate.Length || _bstate[k] == 2) return false;
        _hp[k] -= dmg;
        if (_hp[k] <= 0f) { Collapse(k); return true; }
        if (_bstate[k] == 0 && _hp[k] < _hpMax[k] * 0.5f)
        {
            _bstate[k] = 1;
            _mark(_city, k, 1, 0f);
            Rewrite(k, false);
            // 火元は当たった所の一番近い壁の上に置く
            float* n = stackalloc float[3];
            float* p = stackalloc float[3];
            p[0] = hit.x; p[1] = hit.y; p[2] = hit.z;
            WallNormal(k, hit, n);
            if (_world != IntPtr.Zero) _burn(_world, p, n, k);
            FlightSound.BuildingBurn(Volume(hit));
            return true;
        }
        return false;
    }

    private static void Collapse(int k)
    {
        _bstate[k] = 2;
        Collapsed++;
        float t0 = _clock + 0.001f;
        _mark(_city, k, 1, t0);
        Rewrite(k, true);
        float h = _box[k * 6 + 4] - _box[k * 6 + 1];
        float fallT = (float)Math.Sqrt(2f * h / 60f) + 0.35f;
        _clockUntil = Math.Max(_clockUntil, t0 + fallT + 0.5f);
        Flatten.Add((k, t0 + fallT * 0.8f));
        Shader.SetGlobalFloat(_clockId, _clock);
        float* c = stackalloc float[3];
        c[0] = (_box[k * 6] + _box[k * 6 + 3]) * 0.5f; c[1] = _box[k * 6 + 1]; c[2] = (_box[k * 6 + 2] + _box[k * 6 + 5]) * 0.5f;
        if (_world != IntPtr.Zero) _ruin(_world, c, (_box[k * 6 + 3] - _box[k * 6]) * 0.5f, (_box[k * 6 + 5] - _box[k * 6 + 2]) * 0.5f, h, k);
        FlightSound.BuildingCollapse(Volume(new Vector3(c[0], c[1] + h * 0.3f, c[2])), h);
    }

    // 自分の船からの距離で音の大きさ (1500 で聞こえなくなる)
    private static float Volume(Vector3 p) => Math.Clamp(1f - Vector3.Distance(p, Arena.LocalShipPos) / 1500f, 0f, 1f);

    private static void WallNormal(int k, Vector3 p, float* n)
    {
        float* b = stackalloc float[6];
        for (int i = 0; i < 6; i++) b[i] = _box[k * 6 + i];
        float dx0 = Math.Abs(p.x - b[0]), dx1 = Math.Abs(p.x - b[3]), dz0 = Math.Abs(p.z - b[2]), dz1 = Math.Abs(p.z - b[5]), dy = Math.Abs(p.y - b[4]);
        float m = Math.Min(Math.Min(Math.Min(dx0, dx1), Math.Min(dz0, dz1)), dy);
        n[0] = m == dx0 ? -1f : (m == dx1 ? 1f : 0f);
        n[1] = m == dy && n[0] == 0f ? 1f : 0f;
        n[2] = n[0] == 0f && n[1] == 0f ? (m == dz0 ? -1f : 1f) : 0f;
    }

    // 棟 k の頂点 (と崩れたなら障害灯) をメッシュへ書き直す
    private static void Rewrite(int k, bool lamps)
    {
        int mi = _range[k * 5], v0 = _range[k * 5 + 1], v1 = _range[k * 5 + 2];
        WriteRange(mi, v0, v1);
        if (lamps) WriteRange(_lampChunk, _range[k * 5 + 3], _range[k * 5 + 4]);
    }

    private static void WriteRange(int mi, int v0, int v1)
    {
        if (v1 <= v0 || mi < 0 || mi >= _chunk.Length || !_chunk[mi]) return;
        byte* src = _verts(_city, mi);
        if (src == null) return;
        _chunk[mi].InternalSetVertexBufferData(0, (IntPtr)(src + (long)v0 * Stride), 0, v0, v1 - v0, Stride,
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds);
    }

    internal static void Clear()
    {
        if (_city != IntPtr.Zero) _destroy(_city);
        _city = IntPtr.Zero;
        _world = IntPtr.Zero;
        _chunk = Array.Empty<Mesh>();
        _bstate = Array.Empty<byte>();
        _clockUntil = -1f;
        foreach (var m in Meshes) if (m) UnityEngine.Object.Destroy(m);
        Meshes.Clear();
        if (Active)
        {
            FlightSound.Siren(false, 0f, 1f);
            ResetAlarm();
        }
        _sirenOn = false;
        _root = null;
        Active = false;
    }
}
