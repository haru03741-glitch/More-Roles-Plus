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

// 時戻しを押した本人の画面だけに出す全画面の演出 (約 4.6 秒・60 コマ/秒)。
// 事前に描いたコマ (rewind_ult.cine) を流す: 背景の絵 (クルーの体と影を黒で描いた物) に体と影の重み × 本人の色を足して本人の色にし
// (クルーは平らな塗りなので、光の足し算も含めて色について線形)、本人の着せ替えは立ち絵の空間の格子をコマごとに画面へ写して貼る。
// 入りと出は試合の画面のまま: カメラを本人へ寄せ、事前に描いた光だけを重ね、出では着せ替えとペットが本物のまま飛んで戻る。
// 絵の展開と色の足し算・格子の写しは C++ の部品 (作業スレッドで先読み)。主スレッドはテクスチャへの書き込みと置くだけ。
// 途中で死んだ・会議になった時は画面にヒビが入って砕け、時戻しは起きない
internal static unsafe class RewindCinematic
{
    private const string Res = "MoreRolesPlus.Resources.Cinema.rewind_ult.cine";
    private const string SoundRes = "MoreRolesPlus.Resources.Cinema.rewind_ult.pcm";
    private const int Rec = 64, Slots = 6, Workers = 2, Grid = 4;
    private const int Order = 32000;       // 本編の HUD より手前に描く
    private const float ShatterSeconds = 0.75f;

    private enum Item { Skin, Visor, Hat }

    // ---- 素材 ----
    private static Stream _stream;
    private static byte* _data;
    private static byte[] _copy;
    private static GCHandle _copyPin;
    private static int _w, _h, _n, _fps, _sgw, _sgh, _hgw, _hgh;
    private static int _white;             // 出の頭 (真っ白)。ここで時戻しの依頼を出す
    private static float _sx0, _sy0, _hx0, _hy0, _step, _bpx, _bpy, _hax, _hay, _nearUp;
    private static long _blob;

    private static uint U(int f, int k) => ((uint*)(_data + 68))[f * Rec + k];
    private static float F(int f, int k) => ((float*)(_data + 68))[f * Rec + k];

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
    private static bool _on, _shattering, _asked;
    private static long _start, _pressed;
    private static Action<int> _onWhite;
    private static int _shipGen;
    private static PlayerControl _lp;
    private static bool _wasMoveable;

    private static GameObject _root;
    private static Texture2D _fullTex, _lightTex, _solidTex;
    private static SpriteRenderer _fullSr, _lightSr, _darkSr;
    private static float _frameW, _frameH;
    private static AudioSource _voice;
    private static AudioClip _clip;
    private static AudioSource[] _muted = Array.Empty<AudioSource>();
    private static readonly System.Collections.Generic.List<Camera> _hiddenCams = new();

    internal static bool Playing => _on;

    // 押した本人の端末で呼ぶ。onWhite (押してからの ms) = 画面が真っ白になった時。演出が出せない時は false (呼んだ側がすぐ戻す)
    internal static bool Play(Action<int> onWhite)
    {
        if (_on) return false;
        try { return PlayCore(onWhite); }
        catch (Exception e) { Plugin.Logger.LogError($"[RewindCinematic] play: {e}"); Teardown(); return false; }
    }

    private static bool PlayCore(Action<int> onWhite)
    {
        var hud = Vanilla.Hud;
        var lp = PlayerControl.LocalPlayer;
        if (!hud || !hud.UICamera || !lp || lp.Data == null || lp.Data.IsDead || MeetingHud.Instance || !GameClock.ShipAlive) return false;
        if (Volatile.Read(ref _workerRunning)) return false; // 前の回の展開がまだ終わっていない
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!MrpNative.Loaded || MrpNative.CineFrame == null || !Open()) return false;
        _lp = lp;
        _onWhite = onWhite;
        _pressed = Environment.TickCount64;
        _shipGen = GameClock.ShipGen;
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        SetColors(lp.Data.DefaultOutfit.ColorId);
        BuildView(hud);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        HideOverlayCams(hud.UICamera);
        BuildItems(lp);
        LiveBegin(lp);
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        _shown = -1;
        StartWorker();
        long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        StartSound();
        long t5 = System.Diagnostics.Stopwatch.GetTimestamp();
        double k = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Plugin.Logger.LogInfo($"[RewindCinematic] start ms open={(t1 - t0) * k:0.0} view={(t2 - t1) * k:0.0} items={(t3 - t2) * k:0.0} worker={(t4 - t3) * k:0.0} sound={(t5 - t4) * k:0.0}");
        _wasMoveable = lp.moveable;
        lp.moveable = false;
        _frozen = true;
        _asked = false;
        _shattering = false;
        _missed = _skipped = _shownCount = _ticks = _maxJump = 0;
        _lastMiss = -1;
        _worstMs = _sumMs = 0;
        _start = System.Diagnostics.Stopwatch.GetTimestamp();
        _on = true;
        return true;
    }

    internal static void Tick()
    {
        if (!_on) return;
        try
        {
            if (_shattering) { ShatterTick(); return; }
            if (GameClock.ShipGen != _shipGen || !GameClock.ShipAlive || !_lp) { Refund(); Teardown(); return; }
            bool dead = _lp.Data == null || _lp.Data.IsDead;
            if (dead || MeetingHud.Instance)
            {
                if (!_asked)
                {
                    if (!dead) Refund();
                    StartShatter();
                    return;
                }
                // 依頼を出した後の会議: 時戻しは起きているので、画面とカメラだけ片付ける
                if (MeetingHud.Instance) { Teardown(); return; }
            }
            // TickCount64 は約 15.6ms 刻みで 60 コマだと飛ぶ。始めたフレームは開始の処理で長く、時刻がフレームの途中になるので、
            // 次のフレーム (同じ位置で呼ばれる) で 1 コマ前に合わせ直す → 以後のフレームはコマの境目の上に来るので半コマ足して境目から離す
            // (揺れで同じコマが 2 回・次が飛ぶのを防ぐ)
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (++_ticks == 2) _start = now - System.Diagnostics.Stopwatch.Frequency / _fps;
            long t = (now - _start) * _fps * 1000 / System.Diagnostics.Stopwatch.Frequency + 500;
            int f = Math.Max((int)(t / 1000), _shown); // 合わせ直しでコマを戻さない
            if (f >= _n) { Teardown(); return; }
            Volatile.Write(ref _decodeNext, Math.Max(Volatile.Read(ref _decodeNext), f));
            if (!_asked && f >= _white)
            {
                _asked = true;
                RestoreMove(); // ここから先は世界の逆再生が本人も止める
                int elapsed = (int)Math.Min(int.MaxValue, Environment.TickCount64 - _pressed);
                try { _onWhite?.Invoke(elapsed); }
                catch (Exception e) { Plugin.Logger.LogError($"[RewindCinematic] onWhite: {e}"); }
            }
            if (f != _shown) Show(f);
            // 入りと出のカメラと着せ替えは描画のフレームごとに次のコマとの間を埋める (光の絵だけがコマ単位)
            if (_shown >= 0 && U(_shown, 0) == 0 && U(f, 0) == 0) LiveFrame(f, (t % 1000) * 0.001f);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[RewindCinematic] tick: {e}");
            if (!_asked && !_shattering) Refund();
            Teardown();
        }
    }

    private static bool _frozen;
    private static readonly System.Collections.Generic.List<Sprite> _sprites = new();

    // 演出の間は本人を止める (会議は本編が動きを決めるので触らない)
    private static void RestoreMove()
    {
        if (!_frozen) return;
        _frozen = false;
        if (_lp && !MeetingHud.Instance) _lp.moveable = _wasMoveable;
    }

    private static void Refund() => (RoleState.Local as Crew.Rewinder)?.Refund();

    // ---- 素材を開く (DLL の中を指すだけで写さない。写せない時だけ配列へ) ----
    private static bool Open()
    {
        if (_data != null) return true;
        _stream = typeof(RewindCinematic).Assembly.GetManifestResourceStream(Res);
        if (_stream == null) { Plugin.Logger.LogWarning("[RewindCinematic] no data"); return false; }
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
        if (_data[0] != (byte)'M' || _data[3] != (byte)'C' || *(uint*)(_data + 4) != 1) { Close(); return false; }
        var h = (ushort*)(_data + 8);
        _w = h[0]; _h = h[1]; _n = h[2]; _fps = h[3]; _sgw = h[4]; _sgh = h[5]; _hgw = h[6]; _hgh = h[7];
        var g = (float*)(_data + 24);
        _sx0 = g[0]; _sy0 = g[1]; _hx0 = g[2]; _hy0 = g[3]; _step = g[4]; _bpx = g[5]; _bpy = g[6]; _hax = g[7]; _hay = g[8]; _nearUp = g[9];
        _blob = *(uint*)(_data + 64);
        _white = _n;
        for (int f = 1; f < _n; f++)
            if (U(f - 1, 0) == 1 && U(f, 0) == 0) { _white = f; break; }
        if (_white == _n) Plugin.Logger.LogWarning("[RewindCinematic] no white frame (時戻しの依頼が出ない素材)");
        if (_fps <= 0) { Close(); return false; }
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
        // グラデーションの色: 作業スレッドは色の表を読まないので、ここで写しておく
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
        _decTicks = _decWorst = _decCount = 0;
        _lutWarm = false;
        _stop = false;
        _workerRunning = true;
        _workersAlive = Workers;
        for (int i = 0; i < Workers; i++)
        {
            int id = i;
            try
            {
                _workers[i] = new Thread(() => WorkerLoop(id)) { IsBackground = true, Name = "MrpCinema" + i };
                _workers[i].Start();
            }
            catch (Exception e)
            {
                // 起動できなかった分は抜けた扱いにする (数が残ると次の回が始まらない)
                _workers[i] = null;
                Plugin.Logger.LogError($"[RewindCinematic] worker start: {e}");
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

    private static void ReleaseData()
    {
        lock (WorkerLock)
        {
            if (_workerRunning) { _releaseOnExit = true; return; }
            Close();
            for (int i = 0; i < Slots; i++)
            {
                if (SlotPin[i].IsAllocated) SlotPin[i].Free();
                SlotBuf[i] = null;
            }
        }
    }

    private static void AllocSlots()
    {
        for (int i = 0; i < Slots; i++)
        {
            if (SlotBuf[i] != null && SlotBuf[i].Length >= _w * _h * 4) continue;
            if (SlotPin[i].IsAllocated) SlotPin[i].Free();
            SlotBuf[i] = new byte[_w * _h * 4];
            SlotPin[i] = GCHandle.Alloc(SlotBuf[i], GCHandleType.Pinned);
        }
    }

    // 2 本で先のコマを分け合う。番号は ClaimLock の中で 1 つずつ取る (表示に遅れたら表示中のコマまで飛ばす)。
    // 置き場の値: -1 = 空き・-2 = 展開中・0 以上 = 展開済みのコマ (表示より前なら空きと同じ)
    private static void WorkerLoop(int id)
    {
        try
        {
            // 置き場 (計 12MB) の確保は主スレッドで 2ms ほど止まるのでこちらで。C++ の色の変換表は最初の展開で作られる
            // → 2 本目は 1 本目が置き場を用意して 1 コマ終えてから
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
                // 展開に失敗したコマは出さない (前のコマのまま)
                long d0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool ok = Decode(f, (byte*)SlotPin[slot].AddrOfPinnedObject());
                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - d0;
                Volatile.Write(ref SlotFrame[slot], ok ? f : -1);
                _lutWarm = true;
                Interlocked.Add(ref _decTicks, dt);
                Interlocked.Increment(ref _decCount);
                long w;
                while (dt > (w = Interlocked.Read(ref _decWorst)) && Interlocked.CompareExchange(ref _decWorst, dt, w) != w) { }
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"[RewindCinematic] worker: {e}"); }
        finally
        {
            _lutWarm = true; // 1 本目が置き場の確保で落ちても 2 本目を待たせ続けない
            lock (WorkerLock)
            {
                if (--_workersAlive == 0)
                {
                    _workerRunning = false;
                    if (_releaseOnExit)
                    {
                        _releaseOnExit = false;
                        Close();
                        for (int i = 0; i < Slots; i++)
                        {
                            if (SlotPin[i].IsAllocated) SlotPin[i].Free();
                            SlotBuf[i] = null;
                        }
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
    private struct LightArgs
    {
        public byte* Img; public int ImgLen;
        public byte* Out; public int OutCap;
        public int W, H, Ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WarpArgs
    {
        public short* Grid; public int Gw, Gh;
        public float Gx0, Gy0, Gstep;
        public float* In; public int N;
        public fixed float Fly[6];
        public float Sx, Sy, Ox, Oy;
        public float* Out; public int Stride;
    }

    private static bool Decode(int f, byte* dst)
    {
        byte* blob = _data + _blob;
        if (U(f, 0) == 0)
        {
            var a = new LightArgs { Img = blob + U(f, 2), ImgLen = (int)U(f, 3), Out = dst, OutCap = _w * _h * 4 };
            MrpNative.CineLight(&a);
            return a.Ok != 0;
        }
        var b = new FrameArgs { Base = blob + U(f, 2), BaseLen = (int)U(f, 3), Out = dst, OutCap = _w * _h * 4 };
        if (U(f, 5) > 0)
        {
            b.Weight = blob + U(f, 4);
            b.WeightLen = (int)U(f, 5);
            b.CropX = (int)U(f, 6);
            b.CropY = (int)U(f, 7);
            b.Negate = (int)U(f, 13);
            // 体の枠 (画素・上から) = 色の向きの u, v の 0〜1
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
        return b.Ok != 0;
    }

    // ---- 表示 ----
    private static void BuildView(HudManager hud)
    {
        var cam = hud.UICamera;
        int layer = hud.gameObject.layer;
        _root = new GameObject("MrpRewindCinema") { layer = layer };
        _root.transform.SetParent(hud.transform, false);
        float fsZ = hud.FullScreen ? hud.FullScreen.transform.localPosition.z : -500f;
        _root.transform.localPosition = FxMath.V3(0f, 0f, fsZ + 0.5f);
        float sh = cam.orthographicSize * 2f, sw = sh * cam.aspect;
        // 16:9 の絵で画面を覆う (はみ出しは切れる)
        float k = FxMath.Max(sw / (_w / 100f), sh / (_h / 100f));
        _frameW = _w / 100f * k;
        _frameH = _h / 100f * k;
        if (!_fullTex)
        {
            _fullTex = new Texture2D(_w, _h, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            _lightTex = new Texture2D(_w, _h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            _solidTex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var px = new byte[16];
            for (int i = 0; i < 16; i++) px[i] = 255;
            fixed (byte* p = px) _solidTex.LoadRawTextureData((IntPtr)p, 16);
            _solidTex.Apply(false);
        }
        // 全画面の板は四角のまま (既定の Tight は絵の画素から輪郭の網を作り、960x540 で 1 枚約 10ms 止まる)
        _darkSr = Part("Dark", Sprite.Create(_solidTex, new Rect(0, 0, 2, 2), FxMath.V2(0.5f, 0.5f), 2f, 0, SpriteMeshType.FullRect), layer, 0f, Order);
        _darkSr.transform.localScale = FxMath.V3(sw + 1f, sh + 1f, 1f);
        _lightSr = Part("Light", Sprite.Create(_lightTex, new Rect(0, 0, _w, _h), FxMath.V2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect), layer, -0.01f, Order + 1);
        _lightSr.transform.localScale = FxMath.V3(k, k, 1f);
        _fullSr = Part("Full", Sprite.Create(_fullTex, new Rect(0, 0, _w, _h), FxMath.V2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect), layer, -0.02f, Order + 2);
        _fullSr.transform.localScale = FxMath.V3(k, k, 1f);
        _darkSr.color = FxMath.Rgba(0.102f, 0.047f, 0.008f, 0f);
        _fullSr.enabled = false;
        _lightSr.enabled = false;
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

    private static SpriteRenderer Part(string name, Sprite sp, int layer, float z, int order)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = FxMath.V3(0f, 0f, z);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.sortingOrder = order;
        _sprites.Add(sp);
        return sr;
    }

    private static void Show(int f)
    {
        int slot = -1;
        for (int i = 0; i < Slots; i++)
            if (Volatile.Read(ref SlotFrame[i]) == f) { slot = i; break; }
        if (slot < 0) { if (f != _lastMiss) { _lastMiss = f; _missed++; } return; } // 展開が間に合っていない: 前のコマのまま (時計は進めるので音とはずれない)
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_shown >= 0 && f > _shown + 1) { _skipped += f - _shown - 1; if (f - _shown > _maxJump) _maxJump = f - _shown; }
        _shownCount++;
        Volatile.Write(ref _shown, f);
        var buf = (IntPtr)SlotPin[slot].AddrOfPinnedObject();
        bool full = U(f, 0) == 1;
        if (full)
        {
            _fullTex.LoadRawTextureData(buf, _w * _h * 3);
            _fullTex.Apply(false);
        }
        else
        {
            _lightTex.LoadRawTextureData(buf, _w * _h * 4);
            _lightTex.Apply(false);
        }
        Volatile.Write(ref SlotFrame[slot], -1);
        _fullSr.enabled = full;
        _lightSr.enabled = !full;
        _darkSr.enabled = !full;
        if (full) ItemsFrame(f);
        else ItemsHide();
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms > _worstMs) _worstMs = ms;
        _sumMs += ms;
    }

    private static int _missed, _skipped, _shownCount, _lastMiss = -1;
    private static long _decTicks, _decWorst, _decCount; // 作業スレッドが足す (終わりのログで読むだけ)
    private static int _ticks, _maxJump;
    private static double _worstMs, _sumMs;

    // ---- 演出の中の着せ替え (格子で体へ貼る) ----
    private sealed class ItemMesh
    {
        public GameObject Go;
        public Mesh Mesh;
        public Material Mat;
        public Il2CppStructArray<Vector3> Verts;
        public float[] Idle;
        public GCHandle IdlePin;
        public int N;
        public bool Head;
        public float Alpha = -1f;
    }

    private static readonly ItemMesh[] Items = new ItemMesh[3];

    private static void BuildItems(PlayerControl lp)
    {
        var cos = lp.cosmetics;
        if (!cos) return;
        int color = lp.Data.DefaultOutfit.ColorId;
        try
        {
            var skin = cos.skin;
            if (skin && skin.skin != null && skin.skin.IdleFrame)
                Items[0] = MakeItem("Skin", skin.skin.IdleFrame, skin.layer, false, skin.skin.MatchPlayerColor, color, Order + 3);
            var visor = cos.visor;
            var vv = visor ? visor.viewAsset?.GetAsset() : null;
            if (vv != null && vv.IdleFrame)
                Items[1] = MakeItem("Visor", vv.IdleFrame, visor.Image, true, vv.MatchPlayerColor, color, Order + 4);
            var hat = cos.hat;
            var hv = hat ? hat.viewAsset?.GetAsset() : null;
            if (hv != null && hv.MainImage)
                Items[2] = MakeItem("Hat", hv.MainImage, hat.FrontLayer, true, hv.MatchPlayerColor, color, Order + 5);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[RewindCinematic] items: {e.Message}"); }
    }

    private static ItemMesh MakeItem(string name, Sprite sp, SpriteRenderer src, bool head, bool match, int color, int order)
    {
        var tex = sp.texture;
        if (!tex) return null;
        var rect = sp.rect;
        var piv = sp.pivot;
        float rw = rect.width, rh = rect.height;
        int cols = Math.Max(1, (int)Math.Ceiling(rw / Grid)), rows = Math.Max(1, (int)Math.Ceiling(rh / Grid));
        int n = (cols + 1) * (rows + 1);
        var it = new ItemMesh { Head = head, N = n, Idle = new float[n * 2] };
        it.IdlePin = GCHandle.Alloc(it.Idle, GCHandleType.Pinned);
        try { return FillItem(it, name, sp, tex, src, match, color, order, rect, piv, rw, rh, cols, rows, n); }
        catch
        {
            if (it.IdlePin.IsAllocated) it.IdlePin.Free();
            if (it.Mesh) UnityEngine.Object.Destroy(it.Mesh);
            if (it.Mat) UnityEngine.Object.Destroy(it.Mat);
            throw;
        }
    }

    private static ItemMesh FillItem(ItemMesh it, string name, Sprite sp, Texture2D tex, SpriteRenderer src, bool match, int color, int order,
        Rect rect, Vector2 piv, float rw, float rh, int cols, int rows, int n)
    {
        bool head = it.Head;
        var uv = new Il2CppStructArray<Vector2>((long)n);
        float ox = (head ? _hax : _bpx) - piv.x, oy = (head ? _hay : _bpy) - (rh - piv.y);
        float tw = tex.width, th = tex.height;
        for (int r = 0; r <= rows; r++)
            for (int c = 0; c <= cols; c++)
            {
                int i = r * (cols + 1) + c;
                float x = FxMath.Min(c * Grid, rw), y = FxMath.Min(r * Grid, rh);   // 絵の左上から (下向き)
                it.Idle[i * 2] = x + ox;
                it.Idle[i * 2 + 1] = y + oy;
                uv[i] = FxMath.V2((rect.x + x) / tw, (rect.y + rh - y) / th);
            }
        var tri = new Il2CppStructArray<int>((long)(cols * rows * 6));
        int t = 0;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int a = r * (cols + 1) + c, b = a + 1, d = a + cols + 1, e = d + 1;
                tri[t++] = a; tri[t++] = d; tri[t++] = b;
                tri[t++] = b; tri[t++] = d; tri[t++] = e;
            }
        it.Verts = new Il2CppStructArray<Vector3>((long)n);
        it.Go = new GameObject(name) { layer = _root.layer };
        it.Go.transform.SetParent(_root.transform, false);
        it.Go.transform.localPosition = FxMath.V3(0f, 0f, -0.03f);
        it.Mesh = new Mesh { name = "MrpCinema" + name };
        it.Mesh.vertices = it.Verts;
        it.Mesh.uv = uv;
        it.Mesh.triangles = tri;
        it.Mesh.bounds = new Bounds(Vector3.zero, FxMath.V3(1000f, 1000f, 10f));
        it.Go.AddComponent<MeshFilter>().mesh = it.Mesh;
        var mr = it.Go.AddComponent<MeshRenderer>();
        var baseMat = src ? src.sharedMaterial : null;
        it.Mat = baseMat ? new Material(baseMat) : new Material(Shader.Find("Sprites/Default"));
        it.Mat.mainTexture = tex;
        mr.sharedMaterial = it.Mat;
        mr.sortingOrder = order;
        if (match) PlayerMaterial.SetColors(color, mr);
        it.Go.SetActive(false);
        return it;
    }

    private static void ItemsHide()
    {
        foreach (var it in Items) if (it != null && it.Go.activeSelf) it.Go.SetActive(false);
    }

    private static void ItemsFrame(int f)
    {
        if (U(f, 1) == 0) { ItemsHide(); return; }
        var grid = (short*)(_data + _blob + U(f, 12));
        for (int k = 0; k < 3; k++)
        {
            var it = Items[k];
            if (it == null) continue;
            float alpha = F(f, 58 + k);
            if (alpha <= 0.003f) { if (it.Go.activeSelf) it.Go.SetActive(false); continue; }
            var w = new WarpArgs
            {
                Grid = it.Head ? grid + _sgw * _sgh * 2 : grid,
                Gw = it.Head ? _hgw : _sgw, Gh = it.Head ? _hgh : _sgh,
                Gx0 = it.Head ? _hx0 : _sx0, Gy0 = it.Head ? _hy0 : _sy0, Gstep = _step,
                In = (float*)it.IdlePin.AddrOfPinnedObject(), N = it.N,
                Sx = _frameW, Ox = -0.5f * _frameW, Sy = -_frameH, Oy = 0.5f * _frameH,
                Out = (float*)((byte*)it.Verts.Pointer + 4 * IntPtr.Size), Stride = 3,
            };
            for (int j = 0; j < 6; j++) w.Fly[j] = F(f, 40 + k * 6 + j);
            MrpNative.CineWarp(&w);
            it.Mesh.vertices = it.Verts;
            if (FxMath.Abs(alpha - it.Alpha) > 0.01f)
            {
                it.Alpha = alpha;
                it.Mat.color = FxMath.Rgba(1f, 1f, 1f, alpha);
            }
            if (!it.Go.activeSelf) it.Go.SetActive(true);
        }
    }

    private static void ItemsDestroy()
    {
        for (int k = 0; k < 3; k++)
        {
            var it = Items[k];
            if (it == null) continue;
            if (it.Mesh) UnityEngine.Object.Destroy(it.Mesh);
            if (it.Mat) UnityEngine.Object.Destroy(it.Mat);
            if (it.IdlePin.IsAllocated) it.IdlePin.Free();
            Items[k] = null;
        }
    }

    // ---- 入りと出 (試合の画面のまま) ----
    private static FollowerCamera _follow;
    private static Camera _cam;
    private static float _baseSize, _upUnits;
    private static Vector2 _baseOffset;
    private static bool _faceLeft, _liveTouched;
    private static readonly Vector3[] HomePos = new Vector3[3];
    private static readonly Quaternion[] HomeRot = new Quaternion[3];
    private static readonly float[] CenterX = new float[3], CenterY = new float[3];

    private static Renderer ItemRenderer(CosmeticsLayer cos, int k) => k switch
    {
        0 => cos.skin ? cos.skin.layer : null,
        1 => cos.visor ? cos.visor.Image : null,
        _ => cos.hat ? cos.hat.FrontLayer : null,
    };
    private static Color _hatColor, _skinColor;

    private static void LiveBegin(PlayerControl lp)
    {
        var hud = Vanilla.Hud;
        _follow = hud ? hud.PlayerCam : null;
        _cam = _follow ? _follow.GetComponent<Camera>() : Camera.main;
        _baseSize = _cam ? _cam.orthographicSize : 3f;
        _baseOffset = _follow ? _follow.Offset : default;
        var cos = lp.cosmetics;
        _faceLeft = cos && cos.FlipX;
        _upUnits = _nearUp * 0.01f * (cos ? cos.transform.lossyScale.y : 1f);
        _liveTouched = false;
    }

    private static Transform ItemTf(int k)
    {
        var cos = _lp ? _lp.cosmetics : null;
        if (!cos) return null;
        return k switch
        {
            0 => cos.skin ? cos.skin.transform : null,
            1 => cos.visor ? cos.visor.transform : null,
            _ => cos.hat ? cos.hat.transform : null,
        };
    }

    // 記録 k をコマ f と次のコマの間 t (0〜1) で。次が全画面のコマか終わりなら f のまま
    private static float Lerp(int f, int k, float t)
    {
        float a = F(f, k);
        return f + 1 < _n && U(f + 1, 0) == 0 ? a + (F(f + 1, k) - a) * t : a;
    }

    private static void LiveFrame(int f, float t)
    {
        float width = Lerp(f, 20, t), toward = Lerp(f, 21, t), dark = Lerp(f, 22, t), petA = Lerp(f, 23, t), petS = Lerp(f, 24, t);
        if (_cam) _cam.orthographicSize = _baseSize * width;
        if (_follow) _follow.Offset = FxMath.V2(_baseOffset.x, _baseOffset.y + _upUnits * toward);
        var c = _darkSr.color;
        c.a = dark;
        _darkSr.color = c;
        var cos = _lp ? _lp.cosmetics : null;
        if (!cos) return;
        var pet = cos.CurrentPet;
        if (pet)
        {
            pet.SetAlpha(petA);
            pet.transform.localScale = FxMath.V3(petS, petS, 1f);
        }
        if (f < _white) return;
        // 出: 着せ替えが逆再生で飛んで戻る (人形は左向きで描いてある → 右向きなら左右を返す)
        if (!_liveTouched)
        {
            _liveTouched = true;
            for (int k = 0; k < 3; k++)
            {
                var tf = ItemTf(k);
                if (!tf) continue;
                HomePos[k] = tf.localPosition;
                HomeRot[k] = tf.localRotation;
                // 回す中心 = 絵の真ん中 (層の原点は体の中心にあり、そこで回すと絵が大きく振り回される)
                var r = ItemRenderer(cos, k);
                var par = tf.parent;
                if (r && par)
                {
                    var ctr = par.InverseTransformPoint(r.bounds.center);
                    CenterX[k] = ctr.x - HomePos[k].x;
                    CenterY[k] = ctr.y - HomePos[k].y;
                }
                else CenterX[k] = CenterY[k] = 0f;
            }
            if (cos.hat && cos.hat.FrontLayer) _hatColor = cos.hat.FrontLayer.color;
            if (cos.skin && cos.skin.layer) _skinColor = cos.skin.layer.color;
        }
        float mirror = _faceLeft ? 1f : -1f;
        for (int k = 0; k < 3; k++)
        {
            var tf = ItemTf(k);
            if (!tf) continue;
            float dx = Lerp(f, 25 + k * 4, t) * 0.01f * mirror, dy = Lerp(f, 26 + k * 4, t) * 0.01f, rot = Lerp(f, 27 + k * 4, t) * mirror, a = Lerp(f, 28 + k * 4, t);
            var hp = HomePos[k];
            float th = -rot * (MathF.PI / 180f), co = MathF.Cos(th), si = MathF.Sin(th);
            float cx = CenterX[k], cy = CenterY[k];
            tf.localPosition = FxMath.V3(hp.x + dx + cx - (co * cx - si * cy), hp.y + dy + cy - (si * cx + co * cy), hp.z);
            tf.localRotation = FxMath.RotZ(-rot);
            SetItemAlpha(cos, k, a);
        }
    }

    private static void SetItemAlpha(CosmeticsLayer cos, int k, float a)
    {
        switch (k)
        {
            case 0:
                if (cos.skin && cos.skin.layer) { var c = _skinColor; c.a *= a; cos.skin.layer.color = c; }
                break;
            case 1:
                if (cos.visor) cos.visor.Alpha = a;
                break;
            default:
                if (cos.hat) { var c = _hatColor; c.a *= a; cos.hat.SpriteColor = c; }
                break;
        }
    }

    private static void LiveRestore()
    {
        if (_cam) _cam.orthographicSize = _baseSize;
        if (_follow) _follow.Offset = _baseOffset;
        var cos = _lp ? _lp.cosmetics : null;
        if (!cos) return;
        var pet = cos.CurrentPet;
        if (pet)
        {
            pet.SetAlpha(1f);
            pet.transform.localScale = FxMath.V3(1f, 1f, 1f);
        }
        if (!_liveTouched) return;
        for (int k = 0; k < 3; k++)
        {
            var tf = ItemTf(k);
            if (!tf) continue;
            tf.localPosition = HomePos[k];
            tf.localRotation = HomeRot[k];
            SetItemAlpha(cos, k, 1f);
        }
        _liveTouched = false;
    }

    // ---- 音: 演出の音を鳴らし、その間は本編の環境音と音楽を止める (静寂から一気に、が効くように) ----
    private static void StartSound()
    {
        var sm = SoundManager.Instance;
        if (!sm) return;
        if (!_clip) _clip = LoadClip();
        if (!_clip) return;
        var all = UnityEngine.Object.FindObjectsOfType<AudioSource>();
        var list = new System.Collections.Generic.List<AudioSource>();
        IntPtr amb = sm.AmbienceChannel ? sm.AmbienceChannel.Pointer : IntPtr.Zero, mus = sm.MusicChannel ? sm.MusicChannel.Pointer : IntPtr.Zero;
        foreach (var s in all)
        {
            if (!s || s.mute) continue;
            var g = s.outputAudioMixerGroup;
            IntPtr gp = g ? g.Pointer : IntPtr.Zero;
            if ((gp != IntPtr.Zero && (gp == amb || gp == mus)) || (s.loop && s.isPlaying))
            {
                s.mute = true;
                list.Add(s);
            }
        }
        _muted = list.ToArray();
        if (!_voice)
        {
            var go = new GameObject("MrpRewindCinemaVoice");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _voice = go.AddComponent<AudioSource>();
            _voice.playOnAwake = false;
            _voice.loop = false;
            _voice.spatialBlend = 0f;
            _voice.outputAudioMixerGroup = sm.SfxChannel;
        }
        _voice.clip = _clip;
        _voice.volume = 1f;
        _voice.Play();
    }

    private static void StopSound(bool fade)
    {
        if (_voice && !fade) _voice.Stop();
        foreach (var s in _muted) if (s) s.mute = false;
        _muted = Array.Empty<AudioSource>();
    }

    private static AudioClip LoadClip()
    {
        using var st = typeof(RewindCinematic).Assembly.GetManifestResourceStream(SoundRes);
        if (st == null) return null;
        var raw = new byte[st.Length];
        for (int n = 0; n < raw.Length;)
        {
            int r = st.Read(raw, n, raw.Length - n);
            if (r <= 0) return null;
            n += r;
        }
        // 頭 4 バイト = 1 秒あたりの数・あとは 16bit の 1ch
        int rate = BitConverter.ToInt32(raw, 0);
        int count = (raw.Length - 4) / 2;
        var data = new Il2CppStructArray<float>((long)count);
        float* dp = (float*)((byte*)data.Pointer + 4 * IntPtr.Size);
        fixed (byte* p = raw)
        {
            short* s = (short*)(p + 4);
            for (int i = 0; i < count; i++) dp[i] = s[i] / 32768f;
        }
        var clip = AudioClip.Create("MrpRewindUlt", count, 1, rate, false);
        clip.SetData(data, 0);
        clip.hideFlags = HideFlags.HideAndDontSave;
        return clip;
    }

    // ---- 打ち切り: ヒビが入って砕ける ----
    private const int ShardCols = 7, ShardRows = 4;
    private static GameObject _shardGo;
    private static Mesh _shardMesh;
    private static Material _shardMat;
    private static Il2CppStructArray<Vector3> _shardVerts;
    private static Il2CppStructArray<Color32> _shardCols;
    private static float[] _shardBase, _shardVel, _shardSpin;
    private static long _shatterStart;

    private static void StartShatter()
    {
        _shattering = true;
        _shatterStart = Environment.TickCount64;
        StopWorker();
        StopSound(false);
        ItemsHide();
        LiveRestore();
        RestoreMove();
        bool full = _fullSr && _fullSr.enabled;
        var tex = full ? (Texture)_fullTex : _lightTex;
        if (_fullSr) _fullSr.enabled = false;
        if (_lightSr) _lightSr.enabled = false;
        if (_darkSr) _darkSr.enabled = false;
        BuildShards(tex);
        BreakNoise.PlayFlat("noise_bump_clash_1", 0.9f);
    }

    // 画面を三角の破片に割る (格子の点を揺らして 1 升 2 枚)。破片ごとに頂点を持ち、中心から外へ飛ばす
    private static void BuildShards(Texture tex)
    {
        int gx = ShardCols + 1, gy = ShardRows + 1;
        var px = new float[gx * gy * 2];
        var rnd = new System.Random(Environment.TickCount);
        for (int y = 0; y < gy; y++)
            for (int x = 0; x < gx; x++)
            {
                float u = (float)x / ShardCols, v = (float)y / ShardRows;
                if (x > 0 && x < ShardCols) u += ((float)rnd.NextDouble() - 0.5f) * 0.6f / ShardCols;
                if (y > 0 && y < ShardRows) v += ((float)rnd.NextDouble() - 0.5f) * 0.6f / ShardRows;
                px[(y * gx + x) * 2] = u;
                px[(y * gx + x) * 2 + 1] = v;
            }
        int tris = ShardCols * ShardRows * 2;
        _shardVerts = new Il2CppStructArray<Vector3>((long)(tris * 3));
        _shardCols = new Il2CppStructArray<Color32>((long)(tris * 3));
        var uv = new Il2CppStructArray<Vector2>((long)(tris * 3));
        var idx = new Il2CppStructArray<int>((long)(tris * 3));
        _shardBase = new float[tris * 3 * 2];
        _shardVel = new float[tris * 2];
        _shardSpin = new float[tris];
        int t = 0;
        for (int y = 0; y < ShardRows; y++)
            for (int x = 0; x < ShardCols; x++)
            {
                int a = y * gx + x, b = a + 1, c = a + gx, d = c + 1;
                bool flip = ((x + y) & 1) == 0;
                int[][] pair = flip ? new[] { new[] { a, c, b }, new[] { b, c, d } } : new[] { new[] { a, c, d }, new[] { a, d, b } };
                foreach (var tri in pair)
                {
                    float cx = 0f, cy = 0f;
                    for (int j = 0; j < 3; j++)
                    {
                        float u = px[tri[j] * 2], v = px[tri[j] * 2 + 1];
                        int vi = t * 3 + j;
                        _shardBase[vi * 2] = (u - 0.5f) * _frameW;
                        _shardBase[vi * 2 + 1] = (v - 0.5f) * _frameH;
                        uv[vi] = FxMath.V2(u, v);
                        idx[vi] = vi;
                        cx += _shardBase[vi * 2] / 3f;
                        cy += _shardBase[vi * 2 + 1] / 3f;
                    }
                    float len = FxMath.Max(0.3f, FxMath.Sqrt(cx * cx + cy * cy));
                    float sp = 4f + (float)rnd.NextDouble() * 5f;
                    _shardVel[t * 2] = cx / len * sp;
                    _shardVel[t * 2 + 1] = cy / len * sp + 2f;
                    _shardSpin[t] = ((float)rnd.NextDouble() - 0.5f) * 720f;
                    t++;
                }
            }
        _shardGo = new GameObject("MrpRewindShatter") { layer = _root.layer };
        _shardGo.transform.SetParent(_root.transform, false);
        _shardGo.transform.localPosition = FxMath.V3(0f, 0f, -0.04f);
        _shardMesh = new Mesh { name = "MrpRewindShatter" };
        _shardMesh.vertices = _shardVerts;
        _shardMesh.uv = uv;
        _shardMesh.colors32 = _shardCols;
        _shardMesh.triangles = idx;
        _shardMesh.bounds = new Bounds(Vector3.zero, FxMath.V3(1000f, 1000f, 10f));
        _shardGo.AddComponent<MeshFilter>().mesh = _shardMesh;
        var mr = _shardGo.AddComponent<MeshRenderer>();
        _shardMat = new Material(Shader.Find("Sprites/Default")) { mainTexture = tex };
        mr.sharedMaterial = _shardMat;
        mr.sortingOrder = Order + 6;
        ShatterTick();
    }

    // 0〜0.12 秒 = ヒビ (破片が少しずれて白い割れ目が光る)・その後は外へ飛び、回り、落ち、薄れる
    private static void ShatterTick()
    {
        float s = (Environment.TickCount64 - _shatterStart) / 1000f;
        if (s >= ShatterSeconds || !_shardMesh) { Teardown(); return; }
        const float crack = 0.12f;
        float fly = FxMath.Max(0f, s - crack);
        float gap = s < crack ? 0.02f * s / crack : 0.02f;
        byte alpha = (byte)(255f * FxMath.Clamp01(1f - fly / (ShatterSeconds - crack)));
        float* vp = (float*)((byte*)_shardVerts.Pointer + 4 * IntPtr.Size);
        byte* cp = (byte*)_shardCols.Pointer + 4 * IntPtr.Size;
        int tris = _shardSpin.Length;
        for (int t = 0; t < tris; t++)
        {
            float cx = 0f, cy = 0f;
            for (int j = 0; j < 3; j++) { cx += _shardBase[(t * 3 + j) * 2] / 3f; cy += _shardBase[(t * 3 + j) * 2 + 1] / 3f; }
            float len = FxMath.Max(0.3f, FxMath.Sqrt(cx * cx + cy * cy));
            float ox = cx / len * gap * _frameH + _shardVel[t * 2] * fly;
            float oy = cy / len * gap * _frameH + _shardVel[t * 2 + 1] * fly - 9f * fly * fly;
            float ang = _shardSpin[t] * fly * (MathF.PI / 180f);
            float co = FxMath.Cos(ang), si = FxMath.Sin(ang);
            float sc = 1f - 0.3f * fly;
            for (int j = 0; j < 3; j++)
            {
                int vi = t * 3 + j;
                float dx = (_shardBase[vi * 2] - cx) * sc, dy = (_shardBase[vi * 2 + 1] - cy) * sc;
                vp[vi * 3] = cx + ox + dx * co - dy * si;
                vp[vi * 3 + 1] = cy + oy + dx * si + dy * co;
                vp[vi * 3 + 2] = 0f;
                byte* c = cp + vi * 4;
                // ヒビの間は破片の縁が白く光る (頂点の色で全体を明るく)
                c[0] = c[1] = c[2] = 255;
                c[3] = alpha;
            }
        }
        _shardMesh.vertices = _shardVerts;
        _shardMesh.colors32 = _shardCols;
        if (_darkSr && s < crack)
        {
            _darkSr.enabled = true;
            _darkSr.color = FxMath.Rgba(1f, 1f, 1f, 0.9f * (1f - s / crack));
            _darkSr.sortingOrder = Order + 5;
        }
        else if (_darkSr) _darkSr.enabled = false;
    }

    // ---- 片付け ----
    private static void Teardown()
    {
        if (_on) Plugin.Logger.LogInfo($"[RewindCinematic] end shown={_shownCount} skipped={_skipped} ticks={_ticks} jump={_maxJump} missed={_missed} main ms avg={(_shownCount > 0 ? _sumMs / _shownCount : 0):0.00} worst={_worstMs:0.00} decode n={_decCount} ms avg={(_decCount > 0 ? _decTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _decCount : 0):0.00} worst={_decWorst * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.00}");
        _on = false;
        _shattering = false;
        StopWorker();
        StopSound(false);
        LiveRestore();
        _follow = null;
        _cam = null;
        RestoreMove();
        foreach (var c in _hiddenCams) if (c) c.enabled = true;
        _hiddenCams.Clear();
        ItemsDestroy();
        foreach (var sp in _sprites) if (sp) UnityEngine.Object.Destroy(sp);
        _sprites.Clear();
        if (_shardMesh) UnityEngine.Object.Destroy(_shardMesh);
        if (_shardMat) UnityEngine.Object.Destroy(_shardMat);
        _shardMesh = null;
        _shardMat = null;
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
        _lp = null;
        _onWhite = null;
        // 次に押すまで使わないので、テクスチャ・音・展開の置き場も手放す (押した時に作り直す)
        foreach (var o in new UnityEngine.Object[] { _fullTex, _lightTex, _solidTex, _clip }) if (o) UnityEngine.Object.Destroy(o);
        _fullTex = _lightTex = _solidTex = null;
        _clip = null;
        ReleaseData();
    }

    // 確かめ用 (ブリッジ)
    internal static void Register()
    {
        Bridge.TestBridge.Register("rewindult", "[ask | kill | probe] 時戻しの発動者の全画面演出を流す (既定 = 時戻しの依頼は出さない・ask = 押した時と同じ・kill = 0.8 秒後に打ち切りの砕けを見る)", (args, reply) =>
        {
            string a = (args ?? "").Trim();
            if (a == "probe") { reply($"OK rewindult {Probe()}"); return; }
            if (a.StartsWith("wear ")) { reply($"OK rewindult {Wear(a.Substring(5).Split(' ', StringSplitOptions.RemoveEmptyEntries))}"); return; }
            if (a == "kill") { if (_on && !_shattering) StartShatter(); reply($"OK rewindult {Probe()}"); return; }
            if (a == "notif") { reply($"OK rewindult {NotifProbe()}"); return; }
            bool ok = Play(a == "ask" ? ms => TimeRewind.Ask(ms) : _ => { });
            reply($"OK rewindult playing={ok} frames={_n} size={_w}x{_h} items={(Items[0] != null ? 1 : 0)}{(Items[1] != null ? 1 : 0)}{(Items[2] != null ? 1 : 0)}");
        });
    }

    // 自分に着せ替えを付ける (名前の一部・- は外す)。スキン バイザー 帽子 の順
    private static string Wear(string[] p)
    {
        var lp = PlayerControl.LocalPlayer;
        var hm = HatManager.Instance;
        if (!lp || !hm || p.Length < 3) return $"need <skin> <visor> <hat> lp={(bool)lp} hm={(bool)hm} n={p.Length}";
        int color = lp.Data.DefaultOutfit.ColorId;
        string Find<T>(Il2CppReferenceArray<T> all, string key) where T : CosmeticData
        {
            if (key == "-") return "";
            foreach (var c in all) if (c != null && c.ProdId.Contains(key, StringComparison.OrdinalIgnoreCase)) return c.ProdId;
            return null;
        }
        string sk = Find(hm.allSkins, p[0]), vi = Find(hm.allVisors, p[1]), ha = Find(hm.allHats, p[2]);
        if (sk != null) lp.SetSkin(sk, color);
        if (vi != null) lp.SetVisor(vi, color);
        if (ha != null) lp.SetHat(ha, color);
        return $"skin={sk ?? "?"} visor={vi ?? "?"} hat={ha ?? "?"}";
    }

    // 上に出る通知 (フレンドのトースト・HUD の通知) を 1 つずつ出し、どの層とカメラで描かれるかを返す
    private static string NotifProbe()
    {
        var sb = new System.Text.StringBuilder();
        void Desc(string tag, Component c)
        {
            if (!c) { sb.Append($" {tag}=none"); return; }
            var t = c.transform;
            string path = t.name;
            for (var q = t.parent; q; q = q.parent) path = q.name + "/" + path;
            var p = t.position;
            sb.Append($" {tag}=[{path} layer={c.gameObject.layer} active={c.gameObject.activeInHierarchy} z={p.z:0.###}");
            foreach (var r in c.GetComponentsInChildren<Renderer>(true)) sb.Append($" {r.name}:o{r.sortingOrder}/z{r.transform.position.z:0.##}");
            sb.Append(']');
        }
        var fl = FriendsListManager.InstanceExists ? FriendsListManager.Instance : null;
        var fn = fl ? fl.FriendListNotification : null;
        if (fn) fn.SetUp("rewind notif test", FriendListNotification.IconType.Friend);
        Desc("friend", fn);
        var hud = Vanilla.Hud;
        if (hud && hud.Notifier) hud.Notifier.AddDisconnectMessage("rewind notif test");
        Desc("notifier", hud ? hud.Notifier : null);
        foreach (var cam in Camera.allCameras) sb.Append($" cam:{cam.name} depth={cam.depth} mask={cam.cullingMask:X}");
        if (_root) sb.Append($" cinema z={_root.transform.position.z:0.###} layer={_root.layer}");
        return sb.ToString();
    }

    internal static string Probe() => $"on={_on} shown={_shown} decodeNext={_decodeNext} shattering={_shattering} asked={_asked} voice={(_voice ? _voice.isPlaying : false)} clip={(_clip ? _clip.length : 0f):0.00}s muted={_muted.Length} count={_shownCount} skipped={_skipped} missed={_missed} worst={_worstMs:0.00}ms";
}
