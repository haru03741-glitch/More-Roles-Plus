using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Native;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// サムライの必殺技の全画面の演出 (約 4 秒・30 コマ/秒)。攻撃者と被害者の端末で流す。
// 事前に描いたコマ (katana_ult.cine) = 攻撃者の体と影を黒で描いた絵 + 体と影の重み (本人の色を足す) + 不透明度。
// 透明な所 (試合の画面の板と、その破片) には生の画面を貼る: 板が無傷の間は本物のカメラを描いたカメラに合わせて動かし、
// 板が斬れて開く/閉じる間はそのカメラで止めて、描き上がった画面を板と破片の多角形に写す。
// 絵の展開と色の足し算は C++ の部品 (作業スレッドで先読み)。主スレッドはテクスチャへの書き込みと置くだけ
internal static unsafe partial class KatanaCinematic
{
    private const string Res = "MoreRolesPlus.Resources.Cinema.katana_ult.cine";
    private const int Rec = 128, Slots = 6, Workers = 2;
    private const int Order = 32000;       // 本編の HUD より手前に描く
    private const int HeadBytes = 80;      // 頭 76 バイト + 表の先頭までの番地
    private const int MaxSurfVerts = 512, MaxSurfTris = 768;
    private const float PlateWorld = 6f / 10.8f;   // 試合の画面の板の 1 = 本編の画面の高さ 6 (orthographicSize 3) / 板の高さ 10.8

    // ---- 素材 ----
    private static Stream _stream;
    private static byte* _data;
    private static byte[] _copy;
    private static GCHandle _copyPin;
    private static int _w, _h, _n, _fps, _nVic;
    private static long _blob;
    private static float _hw, _hh;

    private static uint U(int f, int k) => ((uint*)(_data + HeadBytes))[f * Rec + k];
    private static float F(int f, int k) => ((float*)(_data + HeadBytes))[f * Rec + k];
    // 頭の数値は 'MRPK'+版+寸法 6 個 (20 バイト) の直後から
    private static float Head(int k) => ((float*)(_data + 20))[k];

    // ---- 先読み ----
    private static readonly Thread[] _workers = new Thread[Workers];
    private static volatile bool _stop;
    private static int _decodeNext, _shown = -1;
    private static readonly int[] SlotFrame = new int[Slots];
    private static readonly byte[][] SlotBuf = new byte[Slots][];
    private static readonly GCHandle[] SlotPin = new GCHandle[Slots];
    private static float _bodyR, _bodyG, _bodyB, _shR, _shG, _shB;
    private static float _body2R, _body2G, _body2B, _sh2R, _sh2G, _sh2B;
    private static bool _grad;
    private static float _gwx, _gwy, _gw0, _gpos, _gsoft;

    // ---- 再生 ----
    private static bool _on;
    private static long _start;
    private static int _shipGen;
    private static PlayerControl _atk;
    private static bool _hidAtk;

    private static GameObject _root;
    private static Texture2D _colTex, _alphaTex, _maskTex;
    private static Material _frameMat, _plateMat;
    private static MeshRenderer _frameMr, _plateMr;
    private static Mesh _quad, _plate;
    private static Il2CppStructArray<Vector3> _pv;
    private static Il2CppStructArray<Vector2> _puv;
    private static Il2CppStructArray<int> _pt;
    private static float _frameW, _frameH, _screenW, _screenH;
    private static readonly System.Collections.Generic.List<Camera> _hiddenCams = new();

    // ---- 本物のカメラ ----
    private static FollowerCamera _follow;
    private static Camera _cam;
    private static float _baseSize;
    private static Vector2 _baseOffset;
    private static Quaternion _baseRot;

    internal static bool Playing => _on;
    internal static string Why => _why;
    internal const float FieldToWorld = PlateWorld; // 演出の場の 1 = ワールドの長さ
    private static int _hold = -1;      // 確かめ用: このコマで止める
    private static string _why;         // 確かめ用: 始められなかった理由
    private static PlayerControl _lp;
    private static bool _frozen, _wasMoveable;

    // victims = 斬られる人 (近い順)。空間に出すのは先頭の 5 人まで
    internal static bool Play(PlayerControl attacker, PlayerControl[] victims)
    {
        if (_on) return false;
        try { return PlayCore(attacker, victims); }
        catch (Exception e) { Plugin.Logger.LogError($"[KatanaCinematic] play: {e}"); Teardown(); StopSound(); return false; }
    }

    private static bool PlayCore(PlayerControl atk, PlayerControl[] victims)
    {
        var hud = Vanilla.Hud;
        _why = "scene";
        if (!hud || !hud.UICamera || !atk || atk.Data == null || MeetingHud.Instance || !GameClock.ShipAlive) return false;
        _why = "worker";
        if (Volatile.Read(ref _workerRunning)) return false;
        _why = "native";
        if (!MrpNative.Loaded || MrpNative.CineFrame == null || MrpNative.CinePlane == null) return false;
        _why = "data";
        if (!MrpBundle.KatanaMaterials[0] || !MrpBundle.KatanaMaterials[1] || !Open()) return false;
        _why = null;
        _atk = atk;
        _shipGen = GameClock.ShipGen;
        SetColors(atk.Data.DefaultOutfit.ColorId);
        BuildView(hud);
        HideOverlayCams(hud.UICamera);
        CamBegin();
        BuildVictims(victims, _root.layer);
        BuildHits(victims, _root.layer);
        _hidAtk = atk.Visible;
        if (_hidAtk) atk.Visible = false;       // 攻撃者はコマの中に描いてある
        // 演出の間は本人をその場で止める (会議は本編が動きを決めるので戻す時に触らない)
        _lp = PlayerControl.LocalPlayer;
        if (_lp)
        {
            _wasMoveable = _lp.moveable;
            _lp.moveable = false;
            _frozen = true;
        }
        _shown = -1;
        StartWorker();
        _missed = _skipped = _shownCount = _ticks = 0;
        _lastMiss = -1;
        _worstMs = _sumMs = 0;
        _start = System.Diagnostics.Stopwatch.GetTimestamp();
        _on = true;
        StartSound();
        return true;
    }

    internal static void Tick()
    {
        if (!_on) { SoundTick(); if (_easing) EaseTick(); return; }
        try
        {
            if (GameClock.ShipGen != _shipGen || !GameClock.ShipAlive || !_atk || MeetingHud.Instance) { Teardown(); StopSound(); return; }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (++_ticks == 2) _start = now - System.Diagnostics.Stopwatch.Frequency / _fps;
            long t = (now - _start) * _fps * 1000 / System.Diagnostics.Stopwatch.Frequency + 500;
            int f = Math.Max((int)(t / 1000), _shown);
            if (_hold >= 0 && f > _hold) f = _hold;
            if (f >= _n) { Teardown(true); return; }
            Volatile.Write(ref _decodeNext, Math.Max(Volatile.Read(ref _decodeNext), f));
            if (f != _shown) Show(f);
            // 本編のキルの演出は倒した人を見える状態に・動ける状態に戻すので、閉じた後に倒れた時も戻し直す
            if (_hidAtk && _atk.Visible) _atk.Visible = false;
            if (_frozen && _lp && _lp.moveable) _lp.moveable = false;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[KatanaCinematic] tick: {e}");
            Teardown();
            StopSound();
        }
    }

    // ---- 素材を開く (DLL の中を指すだけで写さない。写せない時だけ配列へ) ----
    private static bool Open()
    {
        if (_data != null) return true;
        _stream = typeof(KatanaCinematic).Assembly.GetManifestResourceStream(Res);
        if (_stream == null) { Plugin.Logger.LogWarning("[KatanaCinematic] no data"); return false; }
        if (_stream is UnmanagedMemoryStream ums) _data = ums.PositionPointer;
        else
        {
            _copy = new byte[_stream.Length];
            for (int n = 0; n < _copy.Length;)
            {
                int r = _stream.Read(_copy, n, _copy.Length - n);
                if (r <= 0) { _copy = null; return false; }
                n += r;
            }
            _copyPin = GCHandle.Alloc(_copy, GCHandleType.Pinned);
            _data = (byte*)_copyPin.AddrOfPinnedObject();
        }
        if (_data[0] != (byte)'M' || _data[3] != (byte)'K' || *(uint*)(_data + 4) != 1) { Close(); return false; }
        var h = (ushort*)(_data + 8);
        _w = h[0]; _h = h[1]; _n = h[2]; _fps = h[3]; _nVic = h[4];
        _hw = Head(0); _hh = Head(1);
        _blob = *(uint*)(_data + 76);
        if (_fps <= 0 || _n <= 0) { Close(); return false; }
        return true;
    }

    private static void Close()
    {
        _data = null;
        _stream?.Dispose();
        _stream = null;
        if (_copyPin.IsAllocated) _copyPin.Free();
        _copy = null;
    }

    private static void SetColors(int colorId)
    {
        var pc = Palette.PlayerColors;
        var sc = Palette.ShadowColors;
        int id = colorId >= 0 && colorId < pc.Length ? colorId : 0;
        Color32 b = pc[id], s = sc[id];
        _bodyR = Lin(b.r); _bodyG = Lin(b.g); _bodyB = Lin(b.b);
        _shR = Lin(s.r); _shG = Lin(s.g); _shB = Lin(s.b);
        _grad = Menu.CustomColors.TryGradient(id, out var b2, out var s2, out var dir, out _gpos, out _gsoft);
        if (!_grad) { b2 = b; s2 = s; }
        _body2R = Lin(b2.r); _body2G = Lin(b2.g); _body2B = Lin(b2.b);
        _sh2R = Lin(s2.r); _sh2G = Lin(s2.g); _sh2B = Lin(s2.b);
        (_gwx, _gwy, _gw0) = _grad ? dir : (0f, 0f, 0f);
    }

    private static float Lin(byte v)
    {
        float c = v / 255f;
        return c <= 0.04045f ? c / 12.92f : FxMath.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    // ---- 作業スレッド: 表示より先のコマを展開して色を足す ----
    private static void StartWorker()
    {
        for (int i = 0; i < Slots; i++) SlotFrame[i] = -1;
        _decodeNext = _claimNext = 0;
        _lutWarm = false;
        _stop = false;
        _workerRunning = true;
        _workersAlive = Workers;
        for (int i = 0; i < Workers; i++)
        {
            int id = i;
            try
            {
                _workers[i] = new Thread(() => WorkerLoop(id)) { IsBackground = true, Name = "MrpKatana" + i };
                _workers[i].Start();
            }
            catch (Exception e)
            {
                _workers[i] = null;
                Plugin.Logger.LogError($"[KatanaCinematic] worker start: {e}");
                lock (WorkerLock) if (--_workersAlive == 0) _workerRunning = false;
                if (i == 0) _lutWarm = true;
            }
        }
    }

    private static readonly object WorkerLock = new(), ClaimLock = new();
    private static bool _releaseOnExit, _workerRunning;
    private static int _workersAlive, _claimNext;
    private static volatile bool _lutWarm;

    // 展開の途中で止めると C++ が素材を読んでいる最中なので、作業スレッドが抜けるまで素材と置き場を手放さない
    private static void StopWorker()
    {
        _stop = true;
        for (int i = 0; i < Workers; i++)
        {
            var w = _workers[i];
            if (w != null && w.Join(1000)) _workers[i] = null;
        }
    }

    private static void FreeSlots()
    {
        for (int i = 0; i < Slots; i++)
        {
            if (SlotPin[i].IsAllocated) SlotPin[i].Free();
            SlotBuf[i] = null;
        }
    }

    private static void ReleaseData()
    {
        lock (WorkerLock)
        {
            if (_workerRunning) { _releaseOnExit = true; return; }
            Close();
            FreeSlots();
        }
    }

    // 置き場 1 つ = 色 (RGB24) + 不透明度 (1 バイト) + 手前の型 (1 バイト)
    private static int SlotBytes => _w * _h * 5;

    private static void AllocSlots()
    {
        for (int i = 0; i < Slots; i++)
        {
            if (SlotBuf[i] != null && SlotBuf[i].Length >= SlotBytes) continue;
            if (SlotPin[i].IsAllocated) SlotPin[i].Free();
            SlotBuf[i] = new byte[SlotBytes];
            SlotPin[i] = GCHandle.Alloc(SlotBuf[i], GCHandleType.Pinned);
        }
    }

    // 2 本で先のコマを分け合う。置き場の値: -1 = 空き・-2 = 展開中・0 以上 = 展開済みのコマ (表示より前なら空きと同じ)
    private static void WorkerLoop(int id)
    {
        try
        {
            if (id == 0) AllocSlots();
            while (id > 0 && !_lutWarm && !_stop) Thread.Sleep(1);
            while (!_stop)
            {
                int f, slot = -1;
                lock (ClaimLock)
                {
                    f = Math.Max(Volatile.Read(ref _decodeNext), _claimNext);
                    if (f < _n)
                    {
                        int shown = Volatile.Read(ref _shown);
                        for (int i = 0; i < Slots; i++)
                        {
                            int sf = Volatile.Read(ref SlotFrame[i]);
                            if (sf == -1 || (sf >= 0 && sf < shown)) { slot = i; break; }
                        }
                        if (slot >= 0)
                        {
                            Volatile.Write(ref SlotFrame[slot], -2);
                            _claimNext = f + 1;
                        }
                    }
                }
                if (f >= _n) { Thread.Sleep(5); continue; }
                if (slot < 0) { Thread.Sleep(2); continue; }
                bool ok = Decode(f, (byte*)SlotPin[slot].AddrOfPinnedObject());
                Volatile.Write(ref SlotFrame[slot], ok ? f : -1);
                _lutWarm = true;
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"[KatanaCinematic] worker: {e}"); }
        finally
        {
            _lutWarm = true;
            lock (WorkerLock)
            {
                if (--_workersAlive == 0)
                {
                    _workerRunning = false;
                    if (_releaseOnExit)
                    {
                        _releaseOnExit = false;
                        Close();
                        FreeSlots();
                    }
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameArgs
    {
        public byte* Base; public int BaseLen;
        public byte* Weight; public int WeightLen;
        public int CropX, CropY;
        public fixed float Body[3]; public fixed float Shadow[3];
        public fixed float Body2[3]; public fixed float Shadow2[3];
        public float Gx0, Gy0, Gsx, Gsy;
        public float Gwx, Gwy, Gw0, Gpos, Gsoft;
        public int Negate;
        public byte* Out; public int OutCap;
        public int W, H, Ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlaneArgs
    {
        public byte* Img; public int ImgLen;
        public int CropX, CropY;
        public int Fill;
        public byte* Out; public int W, H;
        public int Ok;
    }

    private static bool Plane(byte* blob, int f, int kOff, int fill, byte* dst)
    {
        var p = new PlaneArgs { Fill = fill, Out = dst, W = _w, H = _h };
        if (U(f, kOff + 1) > 0)
        {
            p.Img = blob + U(f, kOff);
            p.ImgLen = (int)U(f, kOff + 1);
            p.CropX = (int)U(f, kOff + 2);
            p.CropY = (int)U(f, kOff + 3);
        }
        MrpNative.CinePlane(&p);
        return p.Ok != 0;
    }

    private static bool Decode(int f, byte* dst)
    {
        byte* blob = _data + _blob;
        var b = new FrameArgs { Base = blob + U(f, 2), BaseLen = (int)U(f, 3), Out = dst, OutCap = _w * _h * 3 };
        uint flags = U(f, 1);
        if (U(f, 0) == 0 && U(f, 5) > 0)
        {
            b.Weight = blob + U(f, 4);
            b.WeightLen = (int)U(f, 5);
            b.CropX = (int)U(f, 6);
            b.CropY = (int)U(f, 7);
            b.Negate = (flags & 2) != 0 ? 1 : 0;
            uint bw = U(f, 10), bh = U(f, 11);
            if (_grad && bw > 0 && bh > 0)
            {
                b.Gx0 = U(f, 8); b.Gy0 = U(f, 9); b.Gsx = 1f / bw; b.Gsy = 1f / bh;
                b.Gwx = _gwx; b.Gwy = _gwy; b.Gw0 = _gw0; b.Gpos = _gpos; b.Gsoft = _gsoft;
            }
        }
        b.Body[0] = _bodyR; b.Body[1] = _bodyG; b.Body[2] = _bodyB;
        b.Shadow[0] = _shR; b.Shadow[1] = _shG; b.Shadow[2] = _shB;
        b.Body2[0] = _body2R; b.Body2[1] = _body2G; b.Body2[2] = _body2B;
        b.Shadow2[0] = _sh2R; b.Shadow2[1] = _sh2G; b.Shadow2[2] = _sh2B;
        MrpNative.CineFrame(&b);
        if (b.Ok == 0) return false;
        int px = _w * _h;
        // 不透明度: 印の無いコマは全部不透明 (PNG が無ければ 255 で埋まる)
        if (!Plane(blob, f, 12, 255, dst + px * 3)) return false;
        if ((flags & 16) != 0 && !Plane(blob, f, 16, 0, dst + px * 4)) return false;
        return true;
    }

    // ---- 表示 ----
    private static void BuildView(HudManager hud)
    {
        var cam = hud.UICamera;
        int layer = hud.gameObject.layer;
        _root = new GameObject("MrpKatanaCinema") { layer = layer };
        _root.transform.SetParent(hud.transform, false);
        float fsZ = hud.FullScreen ? hud.FullScreen.transform.localPosition.z : -500f;
        _root.transform.localPosition = FxMath.V3(0f, 0f, fsZ + 0.5f);
        _screenH = cam.orthographicSize * 2f;
        _screenW = _screenH * cam.aspect;
        // 16:9 の絵で画面を覆う (はみ出しは切れる)
        float k = FxMath.Max(_screenW / _w, _screenH / _h);
        _frameW = _w * k;
        _frameH = _h * k;
        if (!_colTex)
        {
            _colTex = new Texture2D(_w, _h, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            _alphaTex = new Texture2D(_w, _h, TextureFormat.R8, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            _maskTex = new Texture2D(_w, _h, TextureFormat.R8, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        }
        _plateMat = new Material(MrpBundle.KatanaMaterials[0]) { hideFlags = HideFlags.HideAndDontSave };
        _frameMat = new Material(MrpBundle.KatanaMaterials[1]) { hideFlags = HideFlags.HideAndDontSave };
        _frameMat.SetTexture("_MainTex", _colTex);
        _frameMat.SetTexture("_AlphaTex", _alphaTex);
        _frameMat.SetTexture("_MaskTex", _maskTex);
        _frameMat.SetFloat("_UseMask", 0f);
        _frameMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
        _frameMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);

        _plate = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        _plate.MarkDynamic();
        _pv = new Il2CppStructArray<Vector3>((long)MaxSurfVerts);
        _puv = new Il2CppStructArray<Vector2>((long)MaxSurfVerts);
        _pt = new Il2CppStructArray<int>((long)(MaxSurfTris * 3));
        _plateMr = MeshPart("Plate", _plate, _plateMat, layer, 0f, Order);

        _quad = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        float hx = _frameW * 0.5f, hy = _frameH * 0.5f;
        _quad.vertices = new Vector3[] { FxMath.V3(-hx, -hy, 0f), FxMath.V3(hx, -hy, 0f), FxMath.V3(hx, hy, 0f), FxMath.V3(-hx, hy, 0f) };
        _quad.uv = new Vector2[] { FxMath.V2(0f, 0f), FxMath.V2(1f, 0f), FxMath.V2(1f, 1f), FxMath.V2(0f, 1f) };
        _quad.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        _frameMr = MeshPart("Frame", _quad, _frameMat, layer, -0.02f, Order + 2);
        _frameMr.enabled = false;
        _plateMr.enabled = false;
    }

    private static MeshRenderer MeshPart(string name, Mesh mesh, Material mat, int layer, float z, int order)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = FxMath.V3(0f, 0f, z);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.sortingOrder = order;
        return mr;
    }

    // HUD のカメラより後に描くカメラ (フレンドのトースト = 層 19 の専用カメラ) は並び順では隠せないので、演出の間だけ止める
    private static void HideOverlayCams(Camera ui)
    {
        foreach (var c in Camera.allCameras)
        {
            if (!c || c == ui || !c.enabled || c.depth <= ui.depth) continue;
            c.enabled = false;
            _hiddenCams.Add(c);
        }
    }

    private static void Show(int f)
    {
        int slot = -1;
        for (int i = 0; i < Slots; i++)
            if (Volatile.Read(ref SlotFrame[i]) == f) { slot = i; break; }
        if (slot < 0) { if (f != _lastMiss) { _lastMiss = f; _missed++; } return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_shown >= 0 && f > _shown + 1) _skipped += f - _shown - 1;
        _shownCount++;
        Volatile.Write(ref _shown, f);
        var buf = (byte*)SlotPin[slot].AddrOfPinnedObject();
        int px = _w * _h;
        _colTex.LoadRawTextureData((IntPtr)buf, px * 3);
        _colTex.Apply(false);
        _alphaTex.LoadRawTextureData((IntPtr)(buf + px * 3), px);
        _alphaTex.Apply(false);
        if ((U(f, 1) & 16) != 0)
        {
            _maskTex.LoadRawTextureData((IntPtr)(buf + px * 4), px);
            _maskTex.Apply(false);
        }
        Volatile.Write(ref SlotFrame[slot], -1);
        _frameMr.enabled = true;
        CamFrame(f);
        PlateFrame(f);
        VictimsFrame(f);
        PiecesFrame(f);
        Shake(f);
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms > _worstMs) _worstMs = ms;
        _sumMs += ms;
    }

    private static int _missed, _skipped, _shownCount, _lastMiss = -1, _ticks;
    private static double _worstMs, _sumMs;

    // ---- 生の画面の板 ----
    // 多角形 (頂点 = 画面の 0〜1 の位置と、生の画面を取る位置) を毎コマ扇に割って置く。不透明のコマ (インパクト) では出さない
    private static void PlateFrame(int f)
    {
        uint n = U(f, 21);
        if (U(f, 0) != 0 || n == 0) { _plateMr.enabled = false; return; }
        byte* p = _data + _blob + U(f, 20);
        var v = (Vector3*)((byte*)_pv.Pointer + 4 * IntPtr.Size);
        var uv = (Vector2*)((byte*)_puv.Pointer + 4 * IntPtr.Size);
        var tri = (int*)((byte*)_pt.Pointer + 4 * IntPtr.Size);
        // 板の画面の位置 (16:9 の絵の 0〜1) → HUD の座標・取る位置 → 本物の画面の 0〜1 (絵ははみ出して切れているので縮める)
        float gx = _frameW / _screenW, gy = _frameH / _screenH;
        int nv = 0, nt = 0;
        for (uint i = 0; i < n; i++)
        {
            uint cnt = *(uint*)p;
            p += 8;
            int first = nv;
            for (uint j = 0; j < cnt; j++, p += 16)
            {
                float* q = (float*)p;
                if (nv >= MaxSurfVerts) continue;
                v[nv] = FxMath.V3((q[0] - 0.5f) * _frameW, (q[1] - 0.5f) * _frameH, 0f);
                uv[nv] = FxMath.V2(0.5f + (q[2] - 0.5f) * gx, 0.5f + (q[3] - 0.5f) * gy);
                nv++;
            }
            for (int j = first + 1; j + 1 < nv && nt < MaxSurfTris; j++, nt++)
            {
                tri[nt * 3] = first;
                tri[nt * 3 + 1] = j + 1;
                tri[nt * 3 + 2] = j;
            }
        }
        for (int i = nt; i < MaxSurfTris; i++) tri[i * 3] = tri[i * 3 + 1] = tri[i * 3 + 2] = 0;
        _plate.vertices = _pv;
        _plate.uv = _puv;
        _plate.triangles = _pt;
        _plateMat.SetFloat("_Dim", F(f, 29));
        _plateMat.SetFloat("_Sat", F(f, 30));
        _plateMr.enabled = true;
    }

    // 止めの揺れ: 絵と板を一緒に動かし、端が見えないよう少し寄る (絵の画素 → HUD の座標)
    private static void Shake(int f)
    {
        float k = _frameW / _w;
        _root.transform.localPosition = FxMath.V3(F(f, 26) * k, -F(f, 27) * k, _root.transform.localPosition.z);
        float z = F(f, 28) > 0f ? F(f, 28) : 1f;
        _root.transform.localScale = FxMath.V3(z, z, 1f);
    }

    // ---- 本物のカメラを描いたカメラ (板が無傷だった最後のカメラ) に合わせる ----
    private static void CamBegin()
    {
        EaseEnd(1f);
        var hud = Vanilla.Hud;
        _follow = hud ? hud.PlayerCam : null;
        _cam = _follow ? _follow.GetComponent<Camera>() : Camera.main;
        _baseSize = _cam ? _cam.orthographicSize : 3f;
        _baseOffset = _follow ? _follow.Offset : default;
        _baseRot = _cam ? _cam.transform.localRotation : Quaternion.identity;
        _camSnapped = false;
        // 被害者の画面も攻撃者を中心に (板の原点 = 攻撃者を映した画面の中心)
        var lp = PlayerControl.LocalPlayer;
        _toAtk = lp && _atk && lp != _atk ? (Vector2)(_atk.transform.position - lp.transform.position) : default;
        _camCenter0 = _cam ? _cam.transform.position + new Vector3(_toAtk.x, _toAtk.y, 0f) : default;
    }

    private static Vector2 _toAtk;

    private static void CamFrame(int f)
    {
        if (U(f, 0) != 0) return;
        float cx = F(f, 114), cz = F(f, 115), o = F(f, 116), roll = F(f, 117);
        if (_cam) _cam.orthographicSize = _baseSize * o / (2f * _hw);
        if (_follow) _follow.Offset = FxMath.V2(_baseOffset.x + _toAtk.x + cx * PlateWorld, _baseOffset.y + _toAtk.y + cz * PlateWorld);
        if (_cam) _cam.transform.localRotation = FxMath.RotZ(roll * (180f / MathF.PI));
        // 最初のコマは追いかけずに一度で合わせる (被害者の画面は攻撃者まで動くので、滑って見えないように)
        if (!_camSnapped && _follow) { _camSnapped = true; _follow.SnapToTarget(); }
    }

    private static bool _camSnapped;

    // 最後のコマのカメラは本来の大きさの少し手前で終わるので、一度に戻すと画面が跳ぶ。0.2 秒で戻す
    private const float EaseSec = 0.2f;
    private static bool _easing;        // 出ていない時に毎フレーム Camera の生存を問わない
    private static Camera _easeCam;
    private static FollowerCamera _easeFollow;
    private static float _easeSize, _easeRoll, _easeBase;
    private static Vector2 _easeOffset, _easeBaseOffset;
    private static long _easeStart;

    private static void CamEaseOut()
    {
        if (!_cam || _shown < 0) { CamRestore(); return; }
        _easeCam = _cam;
        _easeFollow = _follow;
        _easeSize = _cam.orthographicSize;
        _easeRoll = F(_shown, 117) * (180f / MathF.PI);
        _easeBase = _baseSize;
        _easeOffset = _follow ? _follow.Offset : default;
        _easeBaseOffset = _baseOffset;
        _easeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _follow = null;
        _cam = null;
        _easing = true;
    }

    private static void EaseTick()
    {
        if (GameClock.ShipGen != _shipGen || !GameClock.ShipAlive) { _easing = false; _easeCam = null; _easeFollow = null; return; }
        float t = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - _easeStart) / (double)System.Diagnostics.Stopwatch.Frequency / EaseSec);
        EaseEnd(t);
    }

    // t ≥ 1 で本来の値に戻して終える
    private static void EaseEnd(float t)
    {
        if (!_easing) return;
        if (!_easeCam) { _easing = false; _easeCam = null; _easeFollow = null; return; }
        if (t >= 1f)
        {
            _easeCam.orthographicSize = _easeBase;
            _easeCam.transform.localRotation = _baseRot;
            if (_easeFollow) _easeFollow.Offset = _easeBaseOffset;
            _easing = false;
            _easeCam = null;
            _easeFollow = null;
            return;
        }
        float k = t * t * (3f - 2f * t), j = 1f - k;
        _easeCam.orthographicSize = _easeSize * j + _easeBase * k;
        _easeCam.transform.localRotation = FxMath.RotZ(_easeRoll * j);
        if (_easeFollow) _easeFollow.Offset = FxMath.V2(_easeOffset.x * j + _easeBaseOffset.x * k, _easeOffset.y * j + _easeBaseOffset.y * k);
    }

    private static void CamRestore()
    {
        if (_cam)
        {
            _cam.orthographicSize = _baseSize;
            _cam.transform.localRotation = _baseRot;
        }
        if (_follow) _follow.Offset = _baseOffset;
        _follow = null;
        _cam = null;
    }

    // ease = 最後まで流れて終わった時だけカメラを滑らかに戻す (打ち切りは一度に戻す)
    private static void Teardown(bool ease = false)
    {
        if (_on) Plugin.Logger.LogInfo($"[KatanaCinematic] end shown={_shownCount} skipped={_skipped} ticks={_ticks} missed={_missed} main ms avg={(_shownCount > 0 ? _sumMs / _shownCount : 0):0.00} worst={_worstMs:0.00}");
        _on = false;
        StopWorker();
        if (ease) CamEaseOut();
        else CamRestore();
        VictimsDestroy();
        if (_hidAtk && _atk) _atk.Visible = true;
        _hidAtk = false;
        if (_frozen && _lp && !MeetingHud.Instance && GameClock.ShipGen == _shipGen && GameClock.ShipAlive) _lp.moveable = _wasMoveable;
        _frozen = false;
        _lp = null;
        foreach (var c in _hiddenCams) if (c) c.enabled = true;
        _hiddenCams.Clear();
        foreach (var o in new UnityEngine.Object[] { _frameMat, _plateMat, _quad, _plate, _colTex, _alphaTex, _maskTex }) if (o) UnityEngine.Object.Destroy(o);
        _frameMat = _plateMat = null;
        _quad = _plate = null;
        _colTex = _alphaTex = _maskTex = null;
        _pv = null;
        _puv = null;
        _pt = null;
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
        _atk = null;
        ReleaseData();
    }

    // 確かめ用 (ブリッジ)
    internal static void Register()
    {
        Bridge.TestBridge.Register("katanault", "[probe] サムライの必殺技の全画面演出を自分を攻撃者として流す", (args, reply) =>
        {
            string a = (args ?? "").Trim();
            if (a == "probe") { reply($"OK katanault {Probe()}"); return; }
            if (a == "skin")
            {
                var cos0 = PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.cosmetics : null;
                var sl = cos0 && cos0.skin ? cos0.skin.layer : null;
                reply($"OK katanault skin layer={(bool)sl} enabled={(sl ? sl.enabled : false)} sprite={(sl && sl.sprite ? sl.sprite.name : "null")} go={(sl ? sl.gameObject.activeInHierarchy : false)}");
                return;
            }
            if (a == "stop") { _hold = -1; if (_on) Teardown(); StopSound(); reply("OK katanault stopped"); return; }
            int hi = a.IndexOf("hold ", StringComparison.Ordinal);
            if (hi >= 0 && int.TryParse(a.Substring(hi + 5).Split(' ')[0], out int hf))
            {
                _hold = hf;
                if (_on) { reply($"OK katanault hold={hf} {Probe()}"); return; }
            }
            else _hold = -1;
            // 既定の被害者 = 攻撃者から 4 以内の生きている人 (近い順)。from <番号> = その人を攻撃者にする (自分が被害者の画面を見る)
            var lp = PlayerControl.LocalPlayer;
            int fi = a.IndexOf("from ", StringComparison.Ordinal);
            if (fi >= 0 && byte.TryParse(a.Substring(fi + 5).Split(' ')[0], out byte fid))
                foreach (var pc in PlayerControl.AllPlayerControls) if (pc && pc.PlayerId == fid) lp = pc;
            var list = new System.Collections.Generic.List<(float, PlayerControl)>();
            if (lp)
            {
                var me = lp.GetTruePosition();
                foreach (var pc in PlayerControl.AllPlayerControls)
                {
                    if (!pc || pc == lp || pc.Data == null || pc.Data.IsDead || pc.Data.Disconnected) continue;
                    float d = (pc.GetTruePosition() - me).magnitude;
                    if (d <= 4f) list.Add((d, pc));
                }
            }
            list.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            var vic = list.ConvertAll(t => t.Item2).ToArray();
            bool ok = Play(lp, vic);
            reply($"OK katanault playing={ok} why={_why} alive={_workersAlive} frames={_n} size={_w}x{_h} victims={vic.Length}/{_vicCount} mats={(bool)MrpBundle.KatanaMaterials[0]}{(bool)MrpBundle.KatanaMaterials[1]}{(bool)MrpBundle.KatanaMaterials[2]}");
        });
    }

    internal static string Probe() => $"on={_on} shown={_shown} count={_shownCount} skipped={_skipped} missed={_missed} worst={_worstMs:0.00}ms n={_n} vic={_vicCount} pieces={_pcCount} {HitsProbe()} sound[{(_voice && _voice.isPlaying ? $"{_voice.time:0.00}s" : "off")} muted={_muted.Length} clip={(bool)_clip}]";
}
