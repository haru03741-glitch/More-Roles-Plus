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
    private const float OriginX = 20000f;
    private static readonly Vector3 Origin = new(OriginX, 0f, 0f);
    internal static Vector3 OriginPos => Origin;
    private const int MaxEvents = 128;

    internal static bool Active { get; private set; }
    private static int _exitFrame = -10;
    // 3D の場面がマウスとキーを使っている (出た直後のフレームも含む・Esc で出た同じ押下を本編のメニューが拾わないように)。
    // 役職や地形の入力はこれが真の間は読まない
    internal static bool TakesInput => Active || Time.frameCount - _exitFrame <= 1;
    internal static string Stats = "";
    // 確かめ用の視点 (yaw・pitch・距離・見る点 = 船の中の座標)。カメラは C++ が決める (炎や粒の板を同じカメラへ向けるため)
    internal static void SetView(float yaw, float pitch, float dist, float x, float y, float z)
    {
        if (_world == IntPtr.Zero) return;
        float* v = stackalloc float[6];
        v[0] = yaw; v[1] = pitch; v[2] = dist; v[3] = x; v[4] = y; v[5] = z;
        FlightNative.View(_world, v);
    }

    private static IntPtr _world;
    private static GameObject _root;
    private static Camera _cam;
    private static Transform _camT, _skyT, _ret1, _ret2;
    private static MeshRenderer _skyR;
    private static readonly Transform[] ShipT = new Transform[FlightNative.MaxShips];
    private static readonly MeshRenderer[] ShipR = new MeshRenderer[FlightNative.MaxShips];
    private static readonly float[] Flash = new float[FlightNative.MaxShips];
    // シールドの光 (スケルドだけ・0 = 消えている・1 = 点いている)。ShipTasks.ShieldOn へ寄せる
    private static readonly float[] Shield = new float[FlightNative.MaxShips];
    private static int _shieldId;
    private static readonly float[] Hp = new float[FlightNative.MaxShips];
    private static readonly int[] ShipModel = new int[FlightNative.MaxShips];
    private static MaterialPropertyBlock _mpb;
    private static Mesh _boltMesh, _fxMesh;
    private static byte* _boltBuf, _fxBuf;
    private static int _fxPrev;
    private static float* _shipOut, _camOut, _hud;
    private static FlightNative.Event* _ev;
    private static int _boltPrev, _shipN, _local, _hits, _kills, _grazes, _bigShots;
    private static float _fov, _statT, _retNear = 60f, _retFar = 120f;
    private static int _idleFrames;
    private static float FxClamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    private static bool _hudHidden;
    private static bool _hudWaitLogged;
    private static int _flashId, _partBaseId, _partsId;
    // 動く部品の姿 (C++ がこの il2cpp の配列の中身へ直接書き、フレームに 1 回シェーダの global の配列へ渡す)
    private static Il2CppStructArray<Vector4> _partArr;
    private static float* _partPtr;
    // 一瞬だけ照らす光 (砲口の閃光・当たり・爆発)。部品と同じく C++ が中身へ直接書く
    private static Il2CppStructArray<Vector4> _lightArr;
    private static float* _lightPtr;
    private static int _lightsId;

    // 確かめ用: 自分の船 (スケルド) と的の船を n 隻出して入る
    // 戦う場所: 0 = 宇宙・1 = 夜の街 (飛ぶ範囲の下に街が広がる)
    internal static int Stage = 1;

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
        bool city = Stage == 1 && MrpBundle.FlightMaterials[4] && MrpBundle.FlightMaterials[6];
        FlightParams.Values[FlightParams.Find("floorY")] = city ? FlightCity.FloorY : -1e6f;
        FlightParams.Values[FlightParams.Find("floorMargin")] = city ? FlightCity.FloorMargin : 70f;
        FlightParams.Values[FlightParams.Find("groundY")] = city ? FlightCity.Ground : -1e6f;
        FlightParams.Values[FlightParams.Find("dustRed")] = city ? 1f : 0f;
        FlightParams.Values[FlightParams.Find("enemyWarm")] = city ? 1f : 0f;
        FlightParams.Values[FlightParams.Find("dustAlpha")] = city ? 0.45f : 0.8f;
        PushParams();
        ShipTasks.Begin();
        Register(0, skeld);
        Register(1, dummy);
        Alloc();
        Build();
        if (city && !FlightCity.Build(_root.transform, Layer, 20261118u, _world)) city = false;
        ApplyStage(city);

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
        _hits = _kills = _grazes = _bigShots = 0;
        Plugin.Logger.LogInfo($"flight: enter targets={targets}");
        return "ok";
    }

    // 入る途中で失敗した時にも呼ぶので、Active に関わらず作った物を全部片付ける (何度呼んでもよい)
    internal static void Exit()
    {
        bool was = Active;
        Active = false;
        if (was) _exitFrame = Time.frameCount;
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
        if (_boltMesh) UnityEngine.Object.Destroy(_boltMesh);
        _boltMesh = null;
        if (_fxMesh) UnityEngine.Object.Destroy(_fxMesh);
        _fxMesh = null;
        FlightCity.Unlend(_world);
        if (_world != IntPtr.Zero) FlightNative.Destroy(_world);
        _world = IntPtr.Zero;
        FlightCity.Clear();
        _holdOn = false;
        Free();
        HideHud(false);
        _hudWaitLogged = false;
        for (int k = 0; k < ShipT.Length; k++) { ShipT[k] = null; ShipR[k] = null; }
        _shipN = 0;
        FlightSound.Stop();
        if (was) Plugin.Logger.LogInfo("flight: exit");
    }

    // 確かめ用: 的 1 から自分の船へ、横に side だけずれて抜ける弾を撃つ (間に合う距離から・回避の確かめ)
    internal static bool DebugShoot(float side)
    {
        if (!Active || _world == IntPtr.Zero) return false;
        float* o = stackalloc float[10];
        FlightNative.Get(_world, _local, o);
        var p = new Vector3(o[0], o[1], o[2]);
        var v = new Vector3(o[3], o[4], o[5]);
        var q = new Quaternion(o[6], o[7], o[8], o[9]);
        Vector3 fwd = q * Vector3.forward, right = q * Vector3.right;
        float bs = FlightParams.Values[FlightParams.Find("boltSpeed")];
        // 船と同じ速さで流れる弾にすると、船から見て真っすぐ後ろへ抜ける (横のずれ = side)
        var from = p + fwd * 160f + right * side;
        var vel = v - fwd * bs;
        float* pp = stackalloc float[3] { from.x, from.y, from.z };
        float* vv = stackalloc float[3] { vel.x, vel.y, vel.z };
        FlightNative.Bolt(_world, _shipN > 1 ? 1 : 0, pp, vv, 1.5f, FlightParams.Values[FlightParams.Find("boltDamage")]);
        return true;
    }

    // 確かめ用: 的 1 を自分の真後ろ (dist) へ置き直し、同じ向きで追わせる
    internal static bool DebugTail(float dist)
    {
        if (!Active || _world == IntPtr.Zero || _shipN < 2) return false;
        float* o = stackalloc float[10];
        FlightNative.Get(_world, _local, o);
        var p = new Vector3(o[0], o[1], o[2]);
        var q = new Quaternion(o[6], o[7], o[8], o[9]);
        float* pp = stackalloc float[3];
        var at = p - q * Vector3.forward * dist;
        pp[0] = at.x; pp[1] = at.y; pp[2] = at.z;
        float* qq = stackalloc float[4] { q.x, q.y, q.z, q.w };
        FlightNative.Spawn(_world, 1, ShipModel[1], 1, 0, pp, qq, Hp[1] > 0 ? Hp[1] : 160f);
        return true;
    }

    // 確かめ用: 自分の船を (x, y, z) に向き (yaw・pitch 度) で置き直す
    internal static bool DebugPlace(Vector3 p, float yaw, float pitch)
    {
        if (!Active || _world == IntPtr.Zero) return false;
        // 絵の物 (GameObject) は今の物を使い続け、シムの船だけを置き直す
        var q = Quaternion.Euler(pitch, yaw, 0f);
        float* pp = stackalloc float[3] { p.x, p.y, p.z };
        float* qq = stackalloc float[4] { q.x, q.y, q.z, q.w };
        FlightNative.Spawn(_world, _local, 0, 0, 0, pp, qq, 100);
        Hp[_local] = 100;
        return true;
    }

    // 確かめ用: 自分の船を毎フレーム同じ所へ置き直す (止めたカメラで街を撮る)
    private static bool _holdOn;
    private static Vector3 _holdP;
    private static float _holdYaw, _holdPitch;

    internal static void DebugHold(bool on, Vector3 p, float yaw, float pitch)
    {
        _holdOn = on; _holdP = p; _holdYaw = yaw; _holdPitch = pitch;
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
        Shield[idx] = model == 0 && ShipTasks.ShieldOn ? 1f : 0f;
        SetBlock(idx);
    }

    private static void Alloc()
    {
        _boltBuf = (byte*)NativeMemory.AllocZeroed((nuint)(FlightNative.MaxBolts * 4 * FlightNative.BoltStride));
        _fxBuf = (byte*)NativeMemory.AllocZeroed((nuint)(FlightNative.MaxFx * 4 * FlightNative.BoltStride));
        _fxPrev = 0;
        _shipOut = (float*)NativeMemory.AllocZeroed((nuint)(FlightNative.MaxShips * 8 * 4));
        _camOut = (float*)NativeMemory.AllocZeroed(8 * 4);
        _hud = (float*)NativeMemory.AllocZeroed(10 * 4);
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
        _shieldId = Shader.PropertyToID("_Shield");
        _partBaseId = Shader.PropertyToID("_PartBase");
        _partsId = Shader.PropertyToID("_MrpFltParts");
        _lightsId = Shader.PropertyToID("_MrpFltLights");
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
        _skyR = smr;
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
    }

    // 場所ごとの光と空: 宇宙 = 恒星の白い光と青い縁の照り返し・夜の街 = 弱い月明かりと下の街明かりの赤
    private static void ApplyStage(bool city)
    {
        _skyR.sharedMaterial = MrpBundle.FlightMaterials[city ? 6 : 2];
        if (city)
        {
            Shader.SetGlobalVector("_MrpFltLight", new Vector4(0.3f, 0.6f, -0.45f, 1f));
            Shader.SetGlobalVector("_MrpFltSky", FlightCity.Sky);
            Shader.SetGlobalVector("_MrpFltRim", new Vector4(1f, 0.22f, 0.24f, 0.42f));
            Shader.SetGlobalVector("_MrpFltAmb", new Vector4(0.6f, 0.46f, 0.52f, 1f));
        }
        else
        {
            Shader.SetGlobalVector("_MrpFltLight", new Vector4(-0.45f, 0.7f, 0.35f, 1f));
            Shader.SetGlobalVector("_MrpFltSky", new Vector4(0.02f, 0.03f, 0.07f, 1600f));
            Shader.SetGlobalVector("_MrpFltRim", new Vector4(0.35f, 0.55f, 1f, 0.28f));
            Shader.SetGlobalVector("_MrpFltAmb", new Vector4(1f, 1f, 1f, 1f));
        }
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

    internal static void HudReshown() => _hudHidden = false;

    private static void HideHud(bool hide)
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (hide && !_hudHidden)
        {
            // 本編の SetHudActive(bool) は自分の役職を読む。試合が始まった直後は役職がまだ無く、本編の中で落ちる
            // (Android で入った瞬間に実測)。役職が付くまで毎フレーム試す (Tick)
            var lp = PlayerControl.LocalPlayer;
            if (!lp || lp.Data == null || !lp.Data.Role)
            {
                if (!_hudWaitLogged) { _hudWaitLogged = true; Plugin.Logger.LogInfo("flight: hud hide waits for role"); }
                return;
            }
            hud.SetHudActive(false);
            _hudHidden = true;
        }
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
        if (!_hudHidden) HideHud(true);
        var input = FlightInput.Read(out bool leave);
        if (leave) { Exit(); return; }

        ShipTasks.Tick(_world, _shipN, ShipModel, Time.time);
        if (_holdOn) DebugPlace(_holdP, _holdYaw, _holdPitch);
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
            LightOut = _lightPtr,
        };
        FlightNative.Frame(&a);
        if (_partPtr != null) Shader.SetGlobalVectorArray(_partsId, _partArr);
        if (_lightPtr != null)
        {
            // シムの座標は場面の原点 (Origin) から測っているので、シェーダの世界の座標へずらす
            for (int k = 0; k < FlightNative.OutLights; k++) _lightPtr[k * 8] += OriginX;
            FlightCars.WriteSlots(_lightPtr, LocalShipPos);
            Shader.SetGlobalVectorArray(_lightsId, _lightArr);
        }
        if (a.Steps == 0) _idleFrames++;

        for (int k = 0; k < _shipN; k++)
        {
            var t = ShipT[k];
            // 船の物は 3D の場面を出る時にまとめて壊して表も空にするので、間は null の比較で足りる (Unity の生存確認は毎フレームの interop)
            if (t is null) continue;
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
        int fxUp = Math.Max(a.FxN, _fxPrev);
        if (fxUp > 0)
            _fxMesh.InternalSetVertexBufferData(0, (IntPtr)_fxBuf, 0, 0, fxUp * 4, FlightNative.BoltStride,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds);
        _fxPrev = a.FxN;

        HandleEvents(a.EvN);
        FlightSound.Tick(_hud[8], _hud[9]);
        float frameDt = Time.deltaTime;
        FlightSound.TickScrape(frameDt);
        FlightCity.Tick(frameDt);
        TickFlash();

        _statT += Time.deltaTime;
        if (_statT > 0.5f)
        {
            _statT = 0;
            float* lo = _shipOut + _local * 8;
            Stats = $"ship=({lo[0]:0.0},{lo[1]:0.0},{lo[2]:0.0}) q=({lo[3]:0.000},{lo[4]:0.000},{lo[5]:0.000},{lo[6]:0.000}) speed={_hud[0]:0} boost={_hud[1]:0.00} hp={_hud[2]:0.00} bolts={a.BoltN} steps={a.Steps} idle={_idleFrames} hits={_hits} kills={_kills} charge={_hud[6]:0.00} tailed={_hud[7]:0} grazes={_grazes} big={_bigShots} cam={cp} fov={_fov:0} light0={(_lightPtr != null ? $"{_lightPtr[3]:0.0}/{_lightPtr[4]:0.00},{_lightPtr[5]:0.00}" : "none")}";
        }
    }

    private static int _bldSounds;

    // 自分の船の前の向き (確かめ用)
    internal static Vector3 LocalShipForward
    {
        get
        {
            if (_shipOut == null || _local < 0) return Vector3.forward;
            float* lo = _shipOut + _local * 8;
            return new Quaternion(lo[3], lo[4], lo[5], lo[6]) * Vector3.forward;
        }
    }

    // 自分の船の位置 (場面の中・シムと同じ座標)
    internal static Vector3 LocalShipPos
    {
        get
        {
            if (_shipOut == null || _local < 0) return Vector3.zero;
            float* lo = _shipOut + _local * 8;
            return new Vector3(lo[0], lo[1], lo[2]);
        }
    }

    private static void HandleEvents(int n)
    {
        // 主砲は砲口ごとに出来事が来るので、音は船ごとに 1 フレーム 1 回
        int fired = 0;
        _bldSounds = 0;
        for (int i = 0; i < n; i++)
        {
            var e = _ev[i];
            if (e.Kind == (int)FlightNative.Ev.Fire && e.A >= 0 && e.A < 30 && (fired & (1 << e.A)) == 0)
            {
                fired |= 1 << e.A;
                float vol = 1f;
                if (e.A != _local)
                {
                    // 他の船の砲声は離れるほど小さく (400 で聞こえなくなる)
                    float* lo = _shipOut + _local * 8;
                    float dx = e.X - lo[0], dy = e.Y - lo[1], dz = e.Z - lo[2];
                    float d = (float)System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    vol = 0.6f * (1f - d / 400f);
                }
                if (vol > 0.02f) FlightSound.Fire(e.C == 1, vol);
            }
            if (e.Kind == (int)FlightNative.Ev.Graze) { if (e.A == _local) _grazes++; continue; }
            if (e.Kind == (int)FlightNative.Ev.Ignite) { if (e.A == _local) FlightSound.Ignite(); continue; }
            if (e.Kind == (int)FlightNative.Ev.Scrape)
            {
                if (e.A == _local) { if (e.C == 1) FlightSound.Crash(e.F); else FlightSound.Scrape(e.F); }
                continue;
            }
            if (e.Kind == (int)FlightNative.Ev.Building)
            {
                // 今は一人で確かめる段なので、どの当たりもその場で棟を削る (通信の段でホストが持つ形にする)
                FlightCity.Damage(e.B, e.F, new Vector3(e.X, e.Y, e.Z));
                // 船がぶつかった削れ (0x20000) は擦れ/衝突の音が鳴るので、弾の当たりだけ鳴らす (1 フレームに 2 つまで)
                if ((e.C & 0x20000) != 0 || _bldSounds >= 2) continue;
                float* lo = _shipOut + _local * 8;
                float dx = e.X - lo[0], dy = e.Y - lo[1], dz = e.Z - lo[2];
                float d = (float)System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
                float vol = 1f - d / 700f;
                if (vol > 0.03f) { FlightSound.BuildingHit(vol); _bldSounds++; }
                continue;
            }
            if (e.Kind == (int)FlightNative.Ev.Ground)
            {
                // 路面に当たった: 車なら燃やす (今は自分の画面だけで決める・通信の段でホストが持つ形にする)。路面だけなら軽い砕けの音
                var at = new Vector3(e.X, e.Y, e.Z);
                // 車は高さがあるので、浅い角度の弾は路面に落ちる手前で車体を抜ける。撃った船の方へ戻って車体の高さも見る
                float* sh = _shipOut + (e.A >= 0 && e.A < FlightNative.MaxShips ? e.A : _local) * 8;
                if (FlightCity.Active && FlightCars.Hit(at, sh[0], sh[1], sh[2])) continue;
                if ((e.C & 1) != 0) FlightCars.Burst(at, 45f);
                if (_bldSounds >= 2) continue;
                float* lo = _shipOut + _local * 8;
                float dx = e.X - lo[0], dy = e.Y - lo[1], dz = e.Z - lo[2];
                float vol = 0.6f * (1f - (float)System.Math.Sqrt(dx * dx + dy * dy + dz * dz) / 600f);
                if (vol > 0.03f) { FlightSound.BuildingHit(vol); _bldSounds++; }
                continue;
            }
            if (e.Kind == (int)FlightNative.Ev.Fire) { if (e.C == 1 && e.A == _local) _bigShots++; continue; }
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
            // 変わる物が無い船は managed の比較だけで抜ける (Unity の物の生存確認は interop)
            float sh = ShipModel[k] == 0 && ShipTasks.ShieldOn ? 1f : 0f;
            if (Flash[k] <= 0 && Shield[k] == sh) continue;
            if (!ShipR[k]) continue;
            Flash[k] = Mathf.Max(0, Flash[k] - dt * 8f);
            // 点く時は本編の光と同じくらいで (約 0.6 秒)・消える時は少し速く
            Shield[k] = sh > Shield[k] ? Math.Min(sh, Shield[k] + dt * 1.7f) : Math.Max(sh, Shield[k] - dt * 3f);
            SetBlock(k);
        }
    }

    // 船ごとの値 (当たりの光・動く部品の配列の始まり) は同じ塊で渡す (片方だけ入れると他方が消える)
    private static void SetBlock(int k)
    {
        if (!ShipR[k]) return;
        _mpb.SetFloat(_flashId, Flash[k] * 0.8f);
        _mpb.SetFloat(_shieldId, Shield[k]);
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
        LightArray();
    }

    private static void LightArray()
    {
        int n = FlightNative.OutLights * 2;
        if (_lightArr == null || _lightArr.Length != n)
        {
            _lightArr = new Il2CppStructArray<Vector4>((long)n);
            _lightPtr = null;
            var probe = new Vector4(1.5f, -2.25f, 3.125f, 7f);
            _lightArr[1] = probe;
            float* p = (float*)(IL2CPP.Il2CppObjectBaseToPtrNotNull(_lightArr) + 4 * IntPtr.Size);
            if (p[4] == probe.x && p[5] == probe.y && p[6] == probe.z && p[7] == probe.w) _lightPtr = p;
            else Plugin.Logger.LogError("flight: light array layout mismatch (no flash lighting)");
        }
        // 光の無い形 (届く距離 1・色 0)
        for (int i = 0; i < n; i += 2)
        {
            _lightArr[i] = new Vector4(0, 0, 0, 1);
            _lightArr[i + 1] = Vector4.zero;
        }
        Shader.SetGlobalVectorArray(_lightsId, _lightArr);
    }
}

// 本編は試合の始まりの紹介が終わる時などに HUD を出し直す。3D の場面の間に出し直されたら、次のフレームでまた隠す
[HarmonyLib.HarmonyPatch(typeof(HudManager), nameof(HudManager.SetHudActive), typeof(PlayerControl), typeof(RoleBehaviour), typeof(bool))]
internal static class FlightHudReshowPatch
{
    public static void Postfix(bool isActive)
    {
        if (isActive && Arena.Active) Arena.HudReshown();
    }
}
