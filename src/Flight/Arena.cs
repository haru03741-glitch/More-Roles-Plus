using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
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
    internal static float ViewYaw, ViewPitch, ViewDist;

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
    private static Mesh _boltMesh, _fxMesh;
    private static byte* _boltBuf, _fxBuf;
    private static int _fxPrev;
    private static float* _shipOut, _camOut, _hud;
    private static FlightNative.Event* _ev;
    private static int _boltPrev, _shipN, _local, _hits, _kills;
    private static float _fov, _statT, _retNear = 60f, _retFar = 120f;
    private static int _idleFrames;
    private static float _plumeLen = -1f;
    private static float FxClamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    private static bool _hudHidden;
    private static int _flashId, _partBaseId, _partsId;
    // 動く部品の姿 (C++ がこの il2cpp の配列の中身へ直接書き、フレームに 1 回シェーダの global の配列へ渡す)
    private static Il2CppStructArray<Vector4> _partArr;
    private static float* _partPtr;

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
        if (_fxMesh) UnityEngine.Object.Destroy(_fxMesh);
        _fxMesh = null;
        if (_world != IntPtr.Zero) FlightNative.Destroy(_world);
        _world = IntPtr.Zero;
        Free();
        HideHud(false);
        for (int k = 0; k < ShipT.Length; k++) { ShipT[k] = null; ShipR[k] = null; }
        Array.Clear(Plumes);
        Array.Fill(PlumePart, -1);
        _plumeLen = -1f;
        _shipN = 0;
        if (was) Plugin.Logger.LogInfo("flight: exit");
    }

    internal static bool DebugExplode()
    {
        if (!Active || _world == IntPtr.Zero) return false;
        float* so = _shipOut + _local * 8;
        var q = new Quaternion(so[3], so[4], so[5], so[6]);
        var p = new Vector3(so[0], so[1], so[2]) + q * new Vector3(0f, 0f, 90f);
        float* pp = stackalloc float[3] { p.x, p.y, p.z };
        FlightNative.Explode(_world, pp, 1.4f);
        return true;
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
        if (m.Parts.Length > 0)
            fixed (FlightNative.Part* pp = m.Parts)
            fixed (int* mp = m.MarkerPart)
                FlightNative.Parts(_world, id, pp, m.Parts.Length, mp, m.MarkerPart.Length);
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
        SetBlock(idx);
        // 噴射の炎: 噴射口の印ごとに、光弾と同じ足し算の板を十字に組んで後ろへ伸ばす (長さは速さで変える)
        var m = ModelBank.Get(model);
        int pn = 0;
        for (int mi = 0; mi < m.Markers.Length; mi++)
        {
            var mk = m.Markers[mi];
            if (mk.Kind != 2 || pn >= PlumesPerShip) continue;
            var pg = new GameObject("MrpFlightPlume") { layer = Layer };
            pg.transform.SetParent(go.transform, false);
            pg.transform.localPosition = new Vector3(mk.Px, mk.Py, mk.Pz);
            pg.transform.localRotation = Quaternion.LookRotation(new Vector3(mk.Dx, mk.Dy, mk.Dz), Vector3.up);
            float w = mk.Id == 1 ? 3.4f : 5.6f;
            pg.transform.localScale = new Vector3(w, w, 1f);
            pg.AddComponent<MeshFilter>().sharedMesh = PlumeMesh(mk.Id == 1 ? new Color32(255, 190, 110, 255) : (model == 0 ? new Color32(110, 210, 255, 255) : new Color32(255, 70, 120, 255)));
            var pr = pg.AddComponent<MeshRenderer>();
            pr.sharedMaterial = MrpBundle.FlightMaterials[1];
            pr.shadowCastingMode = ShadowCastingMode.Off;
            // 首を振る噴射口に付いた炎は、毎フレーム噴射口の動きに合わせて置き直す
            PlumePart[idx * PlumesPerShip + pn] = idx < FlightNative.PartShips && mi < m.MarkerPart.Length ? m.MarkerPart[mi] : -1;
            int pk = idx * PlumesPerShip + pn;
            PlumeBase[pk * 3] = mk.Px; PlumeBase[pk * 3 + 1] = mk.Py; PlumeBase[pk * 3 + 2] = mk.Pz;
            var r0 = pg.transform.localRotation;
            PlumeRot[pk * 4] = r0.x; PlumeRot[pk * 4 + 1] = r0.y; PlumeRot[pk * 4 + 2] = r0.z; PlumeRot[pk * 4 + 3] = r0.w;
            Array.Clear(PlumeLast, pk * 8, 8);
            Plumes[idx * PlumesPerShip + pn++] = pg.transform;
        }
    }

    private const int PlumesPerShip = 4;
    private static readonly Transform[] Plumes = new Transform[FlightNative.MaxShips * PlumesPerShip];
    private static readonly int[] PlumePart = new int[FlightNative.MaxShips * PlumesPerShip];
    // 炎の元の位置と向き (x,y,z / x,y,z,w) と、前のフレームに書いた部品の姿 (8 個)。計算は managed の float で (Unity の算術は interop になる)
    private static readonly float[] PlumeBase = new float[FlightNative.MaxShips * PlumesPerShip * 3];
    private static readonly float[] PlumeRot = new float[FlightNative.MaxShips * PlumesPerShip * 4];
    private static readonly float[] PlumeLast = new float[FlightNative.MaxShips * PlumesPerShip * 8];
    private static Mesh _plumeCyan, _plumeHot, _plumeRed;

    // 十字に組んだ 2 枚の板 (長さ 1・幅 1)。uv.x = 噴射口 1 → 先 0 (光弾のシェーダは uv.x が大きいほど明るい)
    private static Mesh PlumeMesh(Color32 col)
    {
        int which = col.r != 255 ? 0 : (col.g == 190 ? 1 : 2);
        var have = which == 0 ? _plumeCyan : which == 1 ? _plumeHot : _plumeRed;
        if (have) return have;
        Mesh slot;
        var v = new Vector3[8];
        var uv = new Vector2[8];
        var c = new Color32[8];
        float h = 0.5f;
        v[0] = new Vector3(-h, 0, 0); v[1] = new Vector3(h, 0, 0); v[2] = new Vector3(h, 0, 1); v[3] = new Vector3(-h, 0, 1);
        v[4] = new Vector3(0, -h, 0); v[5] = new Vector3(0, h, 0); v[6] = new Vector3(0, h, 1); v[7] = new Vector3(0, -h, 1);
        for (int k = 0; k < 2; k++)
        {
            uv[k * 4] = new Vector2(1, 0); uv[k * 4 + 1] = new Vector2(1, 1); uv[k * 4 + 2] = new Vector2(0, 1); uv[k * 4 + 3] = new Vector2(0, 0);
        }
        for (int k = 0; k < 8; k++) c[k] = col;
        slot = new Mesh { name = "MrpFlightPlume", vertices = v, uv = uv, colors32 = c, triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 } };
        slot.bounds = new Bounds(new Vector3(0, 0, 0.5f), new Vector3(1, 1, 1));
        slot.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        if (which == 0) _plumeCyan = slot; else if (which == 1) _plumeHot = slot; else _plumeRed = slot;
        return slot;
    }

    private static void Alloc()
    {
        _boltBuf = (byte*)NativeMemory.AllocZeroed((nuint)(FlightNative.MaxBolts * 4 * FlightNative.BoltStride));
        _fxBuf = (byte*)NativeMemory.AllocZeroed((nuint)(FlightNative.MaxFx * 4 * FlightNative.BoltStride));
        _fxPrev = 0;
        _shipOut = (float*)NativeMemory.AllocZeroed((nuint)(FlightNative.MaxShips * 8 * 4));
        _camOut = (float*)NativeMemory.AllocZeroed(8 * 4);
        _hud = (float*)NativeMemory.AllocZeroed(8 * 4);
        _ev = (FlightNative.Event*)NativeMemory.AllocZeroed((nuint)(MaxEvents * sizeof(FlightNative.Event)));
        _boltPrev = 0;
    }

    private static void Free()
    {
        NativeMemory.Free(_boltBuf); _boltBuf = null;
        NativeMemory.Free(_fxBuf); _fxBuf = null;
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
        _partBaseId = Shader.PropertyToID("_PartBase");
        _partsId = Shader.PropertyToID("_MrpFltParts");
        _mpb ??= new MaterialPropertyBlock();
        PartArray();

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

        // 火花・炎・煙・塵 = もう 1 枚の動的メッシュ (C++ が頂点を書く)
        var fxo = new GameObject("MrpFlightFx") { layer = Layer };
        fxo.transform.SetParent(_root.transform, false);
        _fxMesh = QuadMesh("MrpFlightFx", _fxBuf, FlightNative.MaxFx);
        fxo.AddComponent<MeshFilter>().sharedMesh = _fxMesh;
        var fmr = fxo.AddComponent<MeshRenderer>();
        fmr.sharedMaterial = MrpBundle.FlightMaterials[3];
        fmr.shadowCastingMode = ShadowCastingMode.Off;

        // 照準 (機首の先の 2 つの枠)
        float conv = FlightParams.Values[FlightParams.Find("converge")];
        _retNear = conv * 0.5f;
        _retFar = conv;
        _ret1 = Reticle("MrpFlightReticle1", 2.2f);
        _ret2 = Reticle("MrpFlightReticle2", 3.2f);

        MrpBundle.FlightMaterials[0].SetFloat("_Outline", 0.0052f);
        Shader.SetGlobalVector("_MrpFltLight", new Vector4(-0.45f, 0.7f, 0.35f, 1f));
        Shader.SetGlobalVector("_MrpFltSky", new Vector4(0.02f, 0.03f, 0.07f, 1600f));
        Shader.SetGlobalVector("_MrpFltRim", new Vector4(0.35f, 0.55f, 1f, 0.28f));
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

    private static Mesh BoltMesh() => QuadMesh("MrpFlightBolts", _boltBuf, FlightNative.MaxBolts);

    // 1 枚 4 頂点の板を quads 枚並べた動的メッシュ (位置・色・uv の 24 バイト)。頂点は buf から毎フレーム書く
    private static Mesh QuadMesh(string name, byte* buf, int quads)
    {
        int vn = quads * 4, inN = quads * 6;
        var mesh = new Mesh { name = name };
        mesh.MarkDynamic();
        var attrs = new Il2CppStructArray<VertexAttributeDescriptor>((long)3);
        attrs[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
        attrs[1] = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 0);
        attrs[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 0);
        mesh.SetVertexBufferParamsFromArray(vn, attrs);
        mesh.InternalSetVertexBufferData(0, (IntPtr)buf, 0, 0, vn, FlightNative.BoltStride, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        var idx = new int[inN];
        for (int i = 0; i < quads; i++)
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
            FxVerts = _fxBuf,
            FxCap = FlightNative.MaxFx,
            FxPrev = _fxPrev,
            PartOut = _partPtr,
        };
        FlightNative.Frame(&a);
        if (_partPtr != null) Shader.SetGlobalVectorArray(_partsId, _partArr);
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
        if (ViewDist > 0)
        {
            // 確かめ用: 自分の船の周りの決めた角度から見る (見た目の検収・毎フレームの経路ではない)
            float* so = _shipOut + _local * 8;
            var center = new Vector3(so[0], so[1], so[2]);
            // 角度は船の向きから測る (船が傾いていても同じ見え方)
            var shipq = new Quaternion(so[3], so[4], so[5], so[6]);
            var rotq = shipq * Quaternion.Euler(ViewPitch, ViewYaw, 0f);
            cp = center + rotq * new Vector3(0f, 0f, -ViewDist);
            cq = Quaternion.LookRotation(center - cp, shipq * Vector3.up);
        }
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

        // 噴射の炎の長さ = 自分の船は今の速さ (巡航で 1・加速で伸び・減速で縮む)。変わった時だけ書く
        {
            float cruise = FlightParams.Values[0], boost = FlightParams.Values[1];
            float k = 4f + 10f * FxClamp01((_hud[0] - cruise * 0.4f) / (boost - cruise * 0.4f + 0.01f));
            if (Math.Abs(k - _plumeLen) > 0.05f)
            {
                _plumeLen = k;
                for (int i = 0; i < PlumesPerShip; i++)
                {
                    var t = Plumes[_local * PlumesPerShip + i];
                    if (!t) continue;
                    var sc = t.localScale;
                    t.localScale = new Vector3(sc.x, sc.y, k * (sc.x < 4f ? 0.7f : 1f));
                }
            }
        }

        if (_partPtr != null) FollowPlumes();

        int upload = Math.Max(a.BoltN, _boltPrev);
        if (upload > 0)
            _boltMesh.InternalSetVertexBufferData(0, (IntPtr)_boltBuf, 0, 0, upload * 4, FlightNative.BoltStride,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds);
        _boltPrev = a.BoltN;
        int fxUp = Math.Max(a.FxN, _fxPrev);
        if (fxUp > 0)
            _fxMesh.InternalSetVertexBufferData(0, (IntPtr)_fxBuf, 0, 0, fxUp * 4, FlightNative.BoltStride,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds);
        _fxPrev = a.FxN;

        HandleEvents(a.EvN);
        TickFlash();

        _statT += Time.deltaTime;
        if (_statT > 0.5f)
        {
            _statT = 0;
            float* lo = _shipOut + _local * 8;
            Stats = $"ship=({lo[0]:0.0},{lo[1]:0.0},{lo[2]:0.0}) q=({lo[3]:0.000},{lo[4]:0.000},{lo[5]:0.000},{lo[6]:0.000}) speed={_hud[0]:0} boost={_hud[1]:0.00} hp={_hud[2]:0.00} bolts={a.BoltN} steps={a.Steps} idle={_idleFrames} hits={_hits} kills={_kills} cam={cp} fov={_fov:0}";
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
                    // 落ちた船の真ん中で爆発させてから消す (閃光の陰で消えるので途切れが見えない)
                    float* so = _shipOut + target * 8;
                    FlightNative.Explode(_world, so, ShipModel[target] == 0 ? 1.2f : 2.0f);
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
            SetBlock(k);
        }
    }

    // 首を振る噴射口の炎 (部品を動かす船だけ・1 隻に数本)
    private static void FollowPlumes()
    {
        int n = Math.Min(_shipN, FlightNative.PartShips) * PlumesPerShip;
        for (int i = 0; i < n; i++)
        {
            int part = PlumePart[i];
            var t = Plumes[i];
            if (part < 0 || t is null) continue;
            float* x = _partPtr + ((i / PlumesPerShip) * FlightNative.MaxParts + part) * 8;
            // 部品の姿 (8 個) が前と同じ間 (まっすぐ飛ぶ間) は書かない
            int l = i * 8;
            bool same = true;
            for (int k = 0; k < 8; k++)
                if (PlumeLast[l + k] != x[k]) { same = false; PlumeLast[l + k] = x[k]; }
            if (same) continue;
            float qx = x[0], qy = x[1], qz = x[2], qw = x[3], s = x[7];
            float px = PlumeBase[i * 3] * s, py = PlumeBase[i * 3 + 1] * s, pz = PlumeBase[i * 3 + 2] * s;
            // 位置 = q で回して足す (Unity の Quaternion * Vector3 と同じ式)
            float tx = 2f * (qy * pz - qz * py), ty = 2f * (qz * px - qx * pz), tz = 2f * (qx * py - qy * px);
            float rx = px + qw * tx + (qy * tz - qz * ty) + x[4];
            float ry = py + qw * ty + (qz * tx - qx * tz) + x[5];
            float rz = pz + qw * tz + (qx * ty - qy * tx) + x[6];
            // 向き = q * 元の向き
            float bx = PlumeRot[i * 4], by = PlumeRot[i * 4 + 1], bz = PlumeRot[i * 4 + 2], bw = PlumeRot[i * 4 + 3];
            t.localPosition = new Vector3(rx, ry, rz);
            t.localRotation = new Quaternion(qw * bx + qx * bw + qy * bz - qz * by, qw * by + qy * bw + qz * bx - qx * bz,
                qw * bz + qz * bw + qx * by - qy * bx, qw * bw - qx * bx - qy * by - qz * bz);
        }
    }

    // 船ごとの値 (当たりの光・動く部品の配列の始まり) は同じ塊で渡す (片方だけ入れると他方が消える)
    private static void SetBlock(int k)
    {
        if (!ShipR[k]) return;
        _mpb.SetFloat(_flashId, Flash[k] * 0.8f);
        _mpb.SetFloat(_partBaseId, _partPtr != null && k < FlightNative.PartShips ? k * FlightNative.MaxParts * 2 : -1f);
        ShipR[k].SetPropertyBlock(_mpb);
    }

    // il2cpp の配列の中身の始まりを、添字で書いた値を読み返して確かめる (合わなければ部品は動かさない)
    private static void PartArray()
    {
        int n = FlightNative.PartShips * FlightNative.MaxParts * 2;
        if (_partArr == null || _partArr.Length != n)
        {
            _partArr = new Il2CppStructArray<Vector4>((long)n);
            _partPtr = null;
            var probe = new Vector4(1.5f, -2.25f, 3.125f, 7f);
            _partArr[1] = probe;
            float* p = (float*)(IL2CPP.Il2CppObjectBaseToPtrNotNull(_partArr) + 4 * IntPtr.Size);
            if (p[4] == probe.x && p[5] == probe.y && p[6] == probe.z && p[7] == probe.w) _partPtr = p;
            else Plugin.Logger.LogError("flight: part array layout mismatch (parts stay still)");
        }
        // 動かない形 (四元数 0,0,0,1・大きさ 1) で埋める
        for (int i = 0; i < n; i += 2)
        {
            _partArr[i] = new Vector4(0, 0, 0, 1);
            _partArr[i + 1] = new Vector4(0, 0, 0, 1);
        }
        Shader.SetGlobalVectorArray(_partsId, _partArr);
    }
}
