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
    private const int Stride = 28;

    private static delegate* unmanaged[Cdecl]<uint, float, IntPtr> _create;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _count;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int*, int*, float*, int> _info;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int*, void> _fill;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _destroy;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _col;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> _lend;
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
                var attrs = new Il2CppStructArray<VertexAttributeDescriptor>((long)4);
                attrs[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
                attrs[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4, 0);
                attrs[2] = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 0);
                attrs[3] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 0);
                mesh.SetVertexBufferParamsFromArray(vn, attrs);
                fixed (byte* vp = vbuf)
                    mesh.InternalSetVertexBufferData(0, (IntPtr)vp, 0, 0, vn, Stride, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                mesh.triangles = ibuf;
                var mn = new Vector3(b[0], b[1], b[2]);
                var mx = new Vector3(b[3], b[4], b[5]);
                mesh.bounds = new Bounds((mn + mx) * 0.5f, mx - mn + Vector3.one * 2f);
                Meshes.Add(mesh);
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

    internal static void Clear()
    {
        if (_city != IntPtr.Zero) _destroy(_city);
        _city = IntPtr.Zero;
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
