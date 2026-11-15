using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦の場面。本編の画面の上に遠近のカメラで宇宙を描き、入っている間は本編の HUD と歩く操作を預かる。
// 出る時 (自分で抜ける・会議・試合の終わり・船が消えた) は必ず全部を戻す。
// 計算は全部 C++ (FlightNative.Frame) で、C# はフレームに 1 回それを呼び、出てきた姿勢を Transform へ、弾の頂点をメッシュへ書くだけ。
// 物は本編のカメラに映らないよう層 30 に置き、さらに船から遠い場所 (Origin) の子にする
internal static unsafe class Arena
{
    internal const int Layer = 30;
    private static readonly Vector3 Origin = new(20000f, 0f, 0f);
    private const int MaxEvents = 128;

    internal static bool Active { get; private set; }
    internal static string Stats = "";

    private static IntPtr _world;
    private static GameObject _root;
    private static Camera _cam;
    private static Transform _camT, _skyT, _ret1, _ret2;
    private static readonly Transform[] ShipT = new Transform[FlightNative.MaxShips];
    private static readonly MeshRenderer[] ShipR = new MeshRenderer[FlightNative.MaxShips];
    private static readonly float[] Flash = new float[FlightNative.MaxShips];
    private static readonly float[] Hp = new float[FlightNative.MaxShips];
    private static readonly int[] ShipModel = new int[FlightNative.MaxShips];
    private static MaterialPropertyBlock _mpb;
    private static Mesh _boltMesh;
    private static byte* _boltBuf;
    private static float* _shipOut, _camOut, _hud;
    private static FlightNative.Event* _ev;
    private static int _boltPrev, _shipN, _local, _hits, _kills;
    private static float _fov, _statT, _retNear = 60f, _retFar = 120f;
    private static int _idleFrames;
    private static bool _hudHidden;
    private static int _flashId;

    // 確かめ用: 自分の船 (スケルド) と的の船を n 隻出して入る
    internal static string Enter(int targets)
    {
        if (Active) return "already";
        if (!FlightNative.Ready()) return "native not ready";
        if (!MrpBundle.FlightReady || !MrpBundle.FlightMaterials[0]) return "materials not loaded";
        var skeld = ModelBank.Get(0);
        var dummy = ModelBank.Get(1);
        if (skeld == null || dummy == null) return "models not loaded";

        _world = FlightNative.Create((uint)Environment.TickCount);
        if (_world == IntPtr.Zero) return "create failed";
        PushParams();
        Register(0, skeld);
        Register(1, dummy);
        Alloc();
        Build();

        _shipN = 0;
        _local = 0;
        Spawn(0, 0, new Vector3(0, 0, -260), Quaternion.identity, 100);
        var rnd = new System.Random(Environment.TickCount);
        for (int i = 0; i < targets && i + 1 < FlightNative.MaxShips; i++)
        {
            // 的はこちらへ向かって来る列 (正面から撃てば当たる)
            var p = i == 0 ? new Vector3(0, 0, 200) : new Vector3((float)(rnd.NextDouble() - 0.5) * 40, (float)(rnd.NextDouble() - 0.5) * 20, 200 + i * 160);
            Spawn(i + 1, 1, p, Quaternion.Euler(0, 180f, 0), 160);
        }
        HideHud(true);
        Active = true;
        _hits = _kills = 0;
        Plugin.Logger.LogInfo($"flight: enter targets={targets}");
        return "ok";
    }

    // 入る途中で失敗した時にも呼ぶので、Active に関わらず作った物を全部片付ける (何度呼んでもよい)
    internal static void Exit()
    {
        bool was = Active;
        Active = false;
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
        if (_boltMesh) UnityEngine.Object.Destroy(_boltMesh);
        _boltMesh = null;
        if (_world != IntPtr.Zero) FlightNative.Destroy(_world);
        _world = IntPtr.Zero;
        Free();
        HideHud(false);
        for (int k = 0; k < ShipT.Length; k++) { ShipT[k] = null; ShipR[k] = null; }
        _shipN = 0;
        if (was) Plugin.Logger.LogInfo("flight: exit");
    }

    internal static void PushParams()
    {
        if (_world == IntPtr.Zero) return;
        fixed (float* v = FlightParams.Values) FlightNative.Params(_world, v, FlightParams.Values.Length);
    }

    private static void Register(int id, ModelBank.Model m)
    {
        fixed (FlightNative.Sphere* s = m.Spheres)
        fixed (FlightNative.Marker* k = m.Markers)
            FlightNative.Model(_world, id, s, m.Spheres.Length, k, m.Markers.Length);
    }

    private static void Spawn(int idx, int model, Vector3 p, Quaternion q, float hp)
    {
        float* pp = stackalloc float[3] { p.x, p.y, p.z };
        float* qq = stackalloc float[4] { q.x, q.y, q.z, q.w };
        FlightNative.Spawn(_world, idx, model, model == 0 ? 0 : 1, 0, pp, qq, hp);
        Hp[idx] = hp;
        ShipModel[idx] = model;
        if (idx >= _shipN) _shipN = idx + 1;
        var go = new GameObject("MrpFlightShip" + idx) { layer = Layer };
        go.transform.SetParent(_root.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = ModelBank.Get(model).Mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = MrpBundle.FlightMaterials[0];
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        ShipT[idx] = go.transform;
        ShipR[idx] = mr;
        Flash[idx] = 0;
    }

    private static void Alloc()
    {
        _boltBuf = (byte*)NativeMemory.AllocZeroed((nuint)(FlightNative.MaxBolts * 4 * FlightNative.BoltStride));
        _shipOut = (float*)NativeMemory.AllocZeroed((nuint)(FlightNative.MaxShips * 8 * 4));
        _camOut = (float*)NativeMemory.AllocZeroed(8 * 4);
        _hud = (float*)NativeMemory.AllocZeroed(8 * 4);
        _ev = (FlightNative.Event*)NativeMemory.AllocZeroed((nuint)(MaxEvents * sizeof(FlightNative.Event)));
        _boltPrev = 0;
    }

    private static void Free()
    {
        NativeMemory.Free(_boltBuf); _boltBuf = null;
        NativeMemory.Free(_shipOut); _shipOut = null;
        NativeMemory.Free(_camOut); _camOut = null;
        NativeMemory.Free(_hud); _hud = null;
        NativeMemory.Free(_ev); _ev = null;
    }

    private static void Build()
    {
        _root = new GameObject("MrpFlightArena") { layer = Layer };
        _root.transform.position = Origin;
        _flashId = Shader.PropertyToID("_Flash");
        _mpb ??= new MaterialPropertyBlock();

        var camGo = new GameObject("MrpFlightCam") { layer = Layer };
        camGo.transform.SetParent(_root.transform, false);
        _cam = camGo.AddComponent<Camera>();
        _cam.cullingMask = 1 << Layer;
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = Color.black;
        _cam.orthographic = false;
        _cam.nearClipPlane = 0.5f;
        _cam.farClipPlane = 6000f;
        _cam.fieldOfView = _fov = FlightParams.Values[FlightParams.Find("fov")];
        var main = Camera.main;
        _cam.depth = (main ? main.depth : 0) + 10;
        _camT = camGo.transform;

        // 宇宙の背景 = カメラと一緒に動く内向きの球
        var sky = new GameObject("MrpFlightSky") { layer = Layer };
        sky.transform.SetParent(_root.transform, false);
        sky.transform.localScale = Vector3.one * 4000f;
        sky.AddComponent<MeshFilter>().sharedMesh = SkyMesh();
        var smr = sky.AddComponent<MeshRenderer>();
        smr.sharedMaterial = MrpBundle.FlightMaterials[2];
        smr.shadowCastingMode = ShadowCastingMode.Off;
        _skyT = sky.transform;

        // 光弾 = 1 枚の動的メッシュ (C++ が頂点を書く)
        var bolt = new GameObject("MrpFlightBolts") { layer = Layer };
        bolt.transform.SetParent(_root.transform, false);
        _boltMesh = BoltMesh();
        bolt.AddComponent<MeshFilter>().sharedMesh = _boltMesh;
        var bmr = bolt.AddComponent<MeshRenderer>();
        bmr.sharedMaterial = MrpBundle.FlightMaterials[1];
        bmr.shadowCastingMode = ShadowCastingMode.Off;

        // 照準 (機首の先の 2 つの枠)
        float conv = FlightParams.Values[FlightParams.Find("converge")];
        _retNear = conv * 0.5f;
        _retFar = conv;
        _ret1 = Reticle("MrpFlightReticle1", 2.2f);
        _ret2 = Reticle("MrpFlightReticle2", 3.2f);

        Shader.SetGlobalVector("_MrpFltLight", new Vector4(-0.45f, 0.7f, 0.35f, 1f));
        Shader.SetGlobalVector("_MrpFltSky", new Vector4(0.02f, 0.03f, 0.07f, 1600f));
        Shader.SetGlobalVector("_MrpFltRim", new Vector4(0.35f, 0.55f, 1f, 0.55f));
    }

    private static Mesh _skyMesh;
    private static Sprite _reticle;

    private static Mesh SkyMesh()
    {
        if (_skyMesh) return _skyMesh;
        const int lat = 16, lon = 32;
        var v = new Vector3[(lat + 1) * (lon + 1)];
        var t = new int[lat * lon * 6];
        int n = 0;
        for (int i = 0; i <= lat; i++)
        {
            float a = Mathf.PI * i / lat;
            for (int j = 0; j <= lon; j++)
            {
                float b = 2 * Mathf.PI * j / lon;
                v[n++] = new Vector3(Mathf.Sin(a) * Mathf.Cos(b), Mathf.Cos(a), Mathf.Sin(a) * Mathf.Sin(b));
            }
        }
        n = 0;
        for (int i = 0; i < lat; i++)
            for (int j = 0; j < lon; j++)
            {
                int a = i * (lon + 1) + j, b = a + lon + 1;
                t[n++] = a; t[n++] = b; t[n++] = a + 1;
                t[n++] = a + 1; t[n++] = b; t[n++] = b + 1;
            }
        _skyMesh = new Mesh { name = "MrpFlightSky", vertices = v, triangles = t };
        _skyMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
        _skyMesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return _skyMesh;
    }

    private static Mesh BoltMesh()
    {
        int vn = FlightNative.MaxBolts * 4, inN = FlightNative.MaxBolts * 6;
        var mesh = new Mesh { name = "MrpFlightBolts" };
        mesh.MarkDynamic();
        var attrs = new Il2CppStructArray<VertexAttributeDescriptor>((long)3);
        attrs[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
        attrs[1] = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 0);
        attrs[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 0);
        mesh.SetVertexBufferParamsFromArray(vn, attrs);
        mesh.InternalSetVertexBufferData(0, (IntPtr)_boltBuf, 0, 0, vn, FlightNative.BoltStride, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        var idx = new int[inN];
        for (int i = 0; i < FlightNative.MaxBolts; i++)
        {
            int v = i * 4, k = i * 6;
            idx[k] = v; idx[k + 1] = v + 1; idx[k + 2] = v + 2;
            idx[k + 3] = v; idx[k + 4] = v + 2; idx[k + 5] = v + 3;
        }
        mesh.triangles = idx;
        // 弾は場のどこにでも出るので、場全体を囲う範囲にしておく (毎フレーム範囲を計り直さない)
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 8000f);
        mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return mesh;
    }

    private static Transform Reticle(string name, float scale)
    {
        if (!_reticle)
        {
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    int dx = Math.Min(x, S - 1 - x), dy = Math.Min(y, S - 1 - y);
                    bool frame = (dx < 4 || dy < 4) && (dx < 18 && dy < 18);
                    bool dot = Math.Abs(x - S / 2) < 3 && Math.Abs(y - S / 2) < 3;
                    px[y * S + x] = frame || dot ? new Color32(120, 255, 170, 230) : new Color32(0, 0, 0, 0);
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            _reticle = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 16f, 0, SpriteMeshType.FullRect);
            _reticle.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        }
        var go = new GameObject(name) { layer = Layer };
        go.transform.SetParent(_root.transform, false);
        go.transform.localScale = Vector3.one * scale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _reticle;
        sr.sortingOrder = 100;
        return go.transform;
    }

    private static void HideHud(bool hide)
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (hide && !_hudHidden) { hud.SetHudActive(false); _hudHidden = true; }
        else if (!hide && _hudHidden)
        {
            _hudHidden = false;
            if (!MeetingHud.Instance && PlayerControl.LocalPlayer) hud.SetHudActive(true);
        }
    }

    // 毎フレーム (Update)。入っていなければ何もしない
    internal static void Tick()
    {
        if (!Active) return;
        if (MeetingHud.Instance || !ShipStatus.Instance || !PlayerControl.LocalPlayer || _world == IntPtr.Zero) { Exit(); return; }
        var input = FlightInput.Read(out bool leave);
        if (leave) { Exit(); return; }

        var a = new FlightNative.FrameArgs
        {
            World = _world,
            Dt = Time.deltaTime,
            Local = _local,
            In = input,
            Now = Time.time,
            ShipOut = _shipOut,
            CamOut = _camOut,
            BoltVerts = _boltBuf,
            BoltCap = FlightNative.MaxBolts,
            BoltPrev = _boltPrev,
            EvOut = _ev,
            EvCap = MaxEvents,
            Hud = _hud,
        };
        FlightNative.Frame(&a);
        if (a.Steps == 0) _idleFrames++;

        for (int k = 0; k < _shipN; k++)
        {
            var t = ShipT[k];
            if (!t) continue;
            float* o = _shipOut + k * 8;
            bool vis = o[7] > 0.5f;
            if (t.gameObject.activeSelf != vis) t.gameObject.SetActive(vis);
            if (!vis) continue;
            t.localPosition = new Vector3(o[0], o[1], o[2]);
            t.localRotation = new Quaternion(o[3], o[4], o[5], o[6]);
        }
        var cp = new Vector3(_camOut[0], _camOut[1], _camOut[2]);
        var cq = new Quaternion(_camOut[3], _camOut[4], _camOut[5], _camOut[6]);
        _camT.localPosition = cp;
        _camT.localRotation = cq;
        _skyT.localPosition = cp;
        if (Mathf.Abs(_camOut[7] - _fov) > 0.05f) { _fov = _camOut[7]; _cam.fieldOfView = _fov; }

        // 照準: 機首の先 (見た目の傾きを除いた向き) に 2 つ。カメラへ向ける
        {
            float* o = _shipOut + _local * 8;
            var sp = new Vector3(o[0], o[1], o[2]);
            var sq = new Quaternion(o[3], o[4], o[5], o[6]);
            var fwd = sq * Vector3.forward;
            _ret1.localPosition = sp + fwd * _retNear;
            _ret2.localPosition = sp + fwd * _retFar;
            _ret1.localRotation = cq;
            _ret2.localRotation = cq;
        }

        int upload = Math.Max(a.BoltN, _boltPrev);
        if (upload > 0)
            _boltMesh.InternalSetVertexBufferData(0, (IntPtr)_boltBuf, 0, 0, upload * 4, FlightNative.BoltStride,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds);
        _boltPrev = a.BoltN;

        HandleEvents(a.EvN);
        TickFlash();

        _statT += Time.deltaTime;
        if (_statT > 0.5f)
        {
            _statT = 0;
            Stats = $"speed={_hud[0]:0} boost={_hud[1]:0.00} hp={_hud[2]:0.00} bolts={a.BoltN} steps={a.Steps} idle={_idleFrames} hits={_hits} kills={_kills} cam={cp} fov={_fov:0}";
        }
    }

    private static void HandleEvents(int n)
    {
        for (int i = 0; i < n; i++)
        {
            var e = _ev[i];
            if (e.Kind != (int)FlightNative.Ev.Hit) continue;
            int target = e.B;
            bool mine = (e.C & 0x10000) != 0;
            if (target < 0 || target >= FlightNative.MaxShips) continue;
            Flash[target] = 1f;
            // 今は一人で確かめる段なので、自分の弾の当たりはその場で耐久を減らす (通信の段でホストが持つ形にする)
            if (mine || e.A == _local || target == _local)
            {
                _hits++;
                Hp[target] -= e.F;
                FlightNative.Hp(_world, target, Hp[target]);
                if (Hp[target] <= 0 && target != _local)
                {
                    FlightNative.Despawn(_world, target);
                    _kills++;
                }
            }
        }
    }

    private static void TickFlash()
    {
        float dt = Time.deltaTime;
        for (int k = 0; k < _shipN; k++)
        {
            if (Flash[k] <= 0 || !ShipR[k]) continue;
            Flash[k] = Mathf.Max(0, Flash[k] - dt * 8f);
            _mpb.SetFloat(_flashId, Flash[k] * 0.8f);
            ShipR[k].SetPropertyBlock(_mpb);
        }
    }
}
