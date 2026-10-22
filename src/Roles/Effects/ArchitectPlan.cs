// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Behavior/MeetingMapLayer.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 構築士の設計図: 本編の地図を開き、その上に「歩ける所の地図」(SolidMap) を青図の色で重ね、他の人を色なしの影で出す。
// 開いている間は自分の体を止める (本編の地図は開いたまま歩ける)。会議・死亡・地図を閉じると終わる。
// 置き場所は本編の自分の印 (HerePoint) と同じ親・同じ式なので、地図の倍率と左右反転はそのまま揃う。
// 地図の上をなぞると (押したまま動かす) 白い線が引かれ、引いた長さだけゲージを使う。離すと線に沿って壁が立つ。
// 線を引けるのは自分 (= 設計台の前) から届く範囲の円の中だけ。下の帯がゲージの残り
internal static class ArchitectPlan
{
    internal const string LayerName = "MrpArchitectPlan";

    private const float FloorZ = -0.05f;      // 地図の絵のすぐ手前 (部屋の名前・自分の印より奥)
    private const float WaterZ = -0.06f;      // 床の水 (床の絵のすぐ手前)
    private const float ShadeZ = -0.9f;       // 本編の自分の印 (-1) のすぐ奥
    private const float WallBand = 2f;        // 歩ける所の周りの壁をこの幅だけ紺で塗る (部屋の間の厚い壁も埋まる。その外は本編の地図の絵のまま)
    private const int ShadeEvery = 2;         // 影の位置を書き直す間隔 (FixedUpdate の回数)
    private const float InkZ = -0.95f;        // なぞった線 (影の手前・自分の印の奥)
    private const float DroneZ = -0.97f;      // ドローンの印 (空を飛んでいるので人の影と線の手前・自分の印の奥)
    private const float DroneW = 0.48f;       // ドローンの印の幅 (地図の長さ)
    private const float DroneRim = 1.25f;     // 印の縁取り (紺の同じ絵) の大きさの比 (明るい床の上でも形が読めるように)
    private const float RingZ = -0.07f;       // 届く範囲の円 (水の手前)
    private const float PointStep = 0.25f;    // なぞった線はこれだけ動くごとに点を足す (世界の長さ)
    private const float PenWidth = 0.035f;    // 線の太さ (地図の長さ)
    private const float BarW = 2.6f, BarH = 0.09f, BarLift = 0.16f; // ゲージの帯 (地図の長さ・地図の絵の下の縁からの高さ)
    private const int RingPx = 512;
    private const float RingLine = 0.006f;    // 円の線の太さ (直径に対して)

    // 青図の色 (RGBA)
    private static readonly byte[] FloorRgba = { 40, 100, 186, 250 };
    private static readonly byte[] GridRgba = { 66, 128, 212, 250 };
    private static readonly byte[] EdgeRgba = { 222, 240, 255, 255 };
    private static readonly byte[] WallRgba = { 14, 36, 78, 250 };
    // 水回りの部屋 (配管を裂ける部屋) は青緑に寄せる
    private static readonly byte[] WetFloorRgba = { 30, 116, 150, 250 };
    private static readonly byte[] WetGridRgba = { 52, 142, 178, 250 };
    private static readonly byte[] WetEdgeRgba = { 170, 245, 255, 255 };
    // 床の水 (浅い・深い)
    private static readonly byte[] WaterRgba = { 120, 214, 246, 255 };
    private static readonly byte[] DeepRgba = { 74, 180, 236, 255 };
    private static readonly Color ShadeColor = new(0.58f, 0.64f, 0.74f, 1f);
    private static readonly Color InkColor = new(1f, 1f, 1f, 0.95f);
    private static readonly Color RingColor = new(0.85f, 0.93f, 1f, 0.55f);
    private static readonly Color BarBack = new(0.03f, 0.08f, 0.18f, 0.9f);
    private static readonly Color BarFill = new(0.55f, 0.82f, 1f, 1f);
    private static readonly Color DroneColor = new(0.9f, 0.96f, 1f, 1f);
    private static readonly Color DroneRimColor = new(0.03f, 0.1f, 0.24f, 0.9f);

    private static bool _want, _open;
    private static GameObject _layer;
    private static Transform _floorTf;
    private static SpriteRenderer _floor;
    private static Texture2D _tex;
    private static Sprite _sprite;
    private static byte[] _px;
    private static int _texW, _texH;
    private static float _ppu;
    private static float _scale = 1f, _sign = 1f;
    private static readonly List<SpriteRenderer> Shades = new();
    private static int _shown, _tick, _opens;
    private static Transform _droneMark;
    private static bool _droneShown;
    private static float _droneMx, _droneMy; // 最後に書いた印の位置 (地図の長さ・テスト用の表示に。印は船替えで消えていることがあるので読まない)

    private static Impostor.Architect _owner;
    private static PassiveButton _layerButton;
    private static Sprite _white, _ringSprite;
    private static Transform _ink, _ring, _barFill;
    private static Rect _bounds;
    private static readonly List<SpriteRenderer> Segs = new();
    private static int _segsUsed;
    private static bool _drawing;
    private static readonly List<Vector2> Line = new(); // なぞっている線 (世界の座標)
    private static float _spent, _barShown = -1f;
    private static Vector2 _me;                          // 開いた時の自分の足元 (体は止まっている)
    private static int _lines;
    private static bool _dirty;
    private static float _nextBake;
    private static int _bakes;
    private static bool[] _rowNear, _near;
    private const float BakeGap = 0.4f;      // 開いている間に地形が変わった時、焼き直す最短の間隔 (秒・壁は 0.12 秒おきに立つので何枚かまとめて)
    private const float WaterBakeGap = 0.5f; // 床に水がある間は、この間隔で焼き直して水の広がりを見せる
    private const int WaterShow = WaterSim.Full / 50, WaterDeep = WaterSim.Full / 4; // 部屋に薄く広がった水も見せる
    private static bool _hadWater;
    private const float WatchWater = 20f;     // 地形が変わった・配管を裂いた後、水が出てくるまでこの秒数は見張る
    private static float _watchUntil;
    private static double _bakeMs;
    private static byte[] _wetRoom;          // 歩ける所の地図の升ごと: 水回りの部屋の中か (船ごとに 1 回)
    private static int _wetRoomGen = -1;
    // 床の水は水の格子 (0.25 単位) の大きさの別の絵にして床の絵の手前に重ねる (水が動くたびに床の絵を焼き直さない)
    private static Texture2D _waterTex;
    private static Sprite _waterSprite;
    private static SpriteRenderer _waterSr;
    private static byte[] _waterPx;
    private static int _waterW, _waterH;
    private static float _nextWater;
    private static double _waterMs;

    internal static bool IsOpen => _open;
    // 開いている設計図の構築士 (閉じていれば null)
    internal static Impostor.Architect Owner => _open ? _owner : null;

    // 地図を設計図として開く。開けたら true
    internal static bool Open(Impostor.Architect owner)
    {
        _owner = owner;
        var hud = Vanilla.Hud;
        if (!hud) return false;
        var cur = MapBehaviour.Instance;
        if (cur && cur.IsOpen) cur.Close();
        _want = true;
        try { hud.ToggleMapVisible(new MapOptions { Mode = MapOptions.Modes.Normal }); }
        finally { _want = false; }
        return _open;
    }

    internal static void Close()
    {
        if (!_open) return;
        var map = MapBehaviour.Instance;
        Hide();
        _owner = null;
        if (map && map.IsOpen) map.Close();
    }

    private static void Hide()
    {
        CancelLine();
        if (_layer) _layer.SetActive(false);
        _open = false;
    }

    internal static void OnMapShown(MapBehaviour map, MapOptions opts)
    {
        bool want = _want && opts != null && opts.Mode == MapOptions.Modes.Normal && !MeetingHud.Instance;
        if (!want || !Build(map))
        {
            Hide();
            return;
        }
        _layer.SetActive(true);
        _open = true;
        _opens++;
        _tick = 0;
        var lp = PlayerControl.LocalPlayer;
        _me = lp ? lp.GetTruePosition() : default;
        float d = Impostor.Architect.Reach * 2f / _scale;
        _ring.localPosition = FxMath.V3(_me.x * _sign / _scale, _me.y / _scale, RingZ);
        _ring.localScale = FxMath.V3(d, d, 1f);
        _barShown = -1f;
        _nextWater = 0f;
        BakeWater();
        ShowGauge();
        PlaceShades();
        PlaceDrone();
    }

    // 自分の端末の FixedUpdate ごと (構築士の時だけ呼ばれる)
    internal static void Tick()
    {
        if (!_open) return;
        var map = MapBehaviour.Instance;
        var lp = PlayerControl.LocalPlayer;
        if (!map || !map.IsOpen || MeetingHud.Instance || !lp || lp.Data == null || lp.Data.IsDead)
        {
            Close();
            return;
        }
        ShowGauge();
        if ((_hadWater || Time.time < _watchUntil) && Time.time >= _nextWater)
        {
            _nextWater = Time.time + WaterBakeGap;
            BakeWater();
        }
        if (_dirty && Time.time >= _nextBake)
        {
            _dirty = false;
            _nextBake = Time.time + BakeGap;
            BakeFloor();
        }
        PlaceDrone(); // ドローンは秒速 9 で飛ぶので影より細かく (FixedUpdate ごと)
        if (++_tick < ShadeEvery) return;
        _tick = 0;
        PlaceShades();
    }

    private static bool Build(MapBehaviour map)
    {
        var ship = ShipStatus.Instance;
        if (!ship || !map.HerePoint || !SolidMap.Ensure() || !SolidMap.Valid) return false;
        var parent = map.HerePoint.transform.parent;
        if (!_layer || _layer.transform.parent != parent)
        {
            if (_layer) Object.Destroy(_layer);
            Shades.Clear();
            _shown = 0; // 前の層で出していた影の数 (残すと次に開いた時に空の一覧を読む)
            _droneMark = null;
            _droneShown = false;
            _layer = new GameObject(LayerName) { layer = map.gameObject.layer };
            _layer.transform.SetParent(parent, false);
            var fgo = new GameObject("floor") { layer = _layer.layer };
            fgo.transform.SetParent(_layer.transform, false);
            _floorTf = fgo.transform;
            _floor = fgo.AddComponent<SpriteRenderer>();
            var wgo = new GameObject("water") { layer = _layer.layer };
            wgo.transform.SetParent(_layer.transform, false);
            _waterSr = wgo.AddComponent<SpriteRenderer>();
            _waterSr.enabled = false;
            BuildInput(map);
        }
        _scale = ship.MapScale > 0f ? ship.MapScale : 1f;
        _sign = ship.transform.localScale.x < 0f ? -1f : 1f;
        BakeFloor();
        _floor.sprite = _sprite;
        Vector2 org = SolidMap.Org;
        _floorTf.localPosition = FxMath.V3(org.x * _sign / _scale, org.y / _scale, FloorZ);
        _floorTf.localScale = FxMath.V3(_sign / _scale, 1f / _scale, 1f);
        return true;
    }

    // 床の水の絵: 水の格子の升ごとに、浅い水 = 水色・深い水 = 濃い水色・無い所 = 透明 (色は水と同じにしてぼかしの縁を黒くしない)
    private static unsafe void BakeWater()
    {
        if (!WaterSim.Ready || !_waterSr)
        {
            if (_waterSr) _waterSr.enabled = false;
            _hadWater = false;
            return;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int w = WaterSim.W, h = WaterSim.H;
        var hgt = WaterSim.HeightCells;
        var open = WaterSim.OpenCells;
        if (_waterTex == null || _waterW != w || _waterH != h)
        {
            if (_waterSprite) Object.Destroy(_waterSprite);
            if (_waterTex) Object.Destroy(_waterTex);
            _waterTex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "MrpArchitectWater", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            _waterTex.hideFlags |= HideFlags.HideAndDontSave;
            _waterW = w; _waterH = h;
            _waterPx = new byte[w * h * 4];
            _waterSprite = null;
        }
        var px = _waterPx;
        bool any = false;
        for (int k = 0; k < w * h; k++)
        {
            int d = open[k] != 0 ? hgt[k] : 0, o = k * 4;
            var c = d >= WaterDeep ? DeepRgba : WaterRgba;
            px[o] = c[0]; px[o + 1] = c[1]; px[o + 2] = c[2];
            if (d < WaterShow) { px[o + 3] = 0; continue; }
            any = true;
            px[o + 3] = c[3];
        }
        _hadWater = any;
        _waterSr.enabled = any;
        if (!any) return;
        fixed (byte* b = px) _waterTex.LoadRawTextureData((IntPtr)b, px.Length);
        _waterTex.Apply(false, false);
        if (!_waterSprite)
        {
            _waterSprite = Sprite.Create(_waterTex, new Rect(0, 0, w, h), FxMath.V2(0f, 0f), 1f / WaterSim.Cell, 0, SpriteMeshType.FullRect);
            _waterSprite.hideFlags |= HideFlags.HideAndDontSave;
        }
        _waterSr.sprite = _waterSprite;
        Vector2 org = WaterSim.Origin;
        var tf = _waterSr.transform;
        tf.localPosition = FxMath.V3(org.x * _sign / _scale, org.y / _scale, WaterZ);
        tf.localScale = FxMath.V3(_sign / _scale, 1f / _scale, 1f);
        _waterMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    // 地形が変わった (壁が立った・崩れた)。開いている間だけ次の Tick で焼き直す
    internal static void MarkDirty()
    {
        if (!_open) return;
        _dirty = true;
        _watchUntil = Time.time + WatchWater;
    }

    // 歩ける所 = 青・その縁 = 白・1 単位ごとの方眼・歩ける所の近くの壁 = 紺・それより外は透明 (本編の地図の絵)
    private static unsafe void BakeFloor()
    {
        int w = SolidMap.W, h = SolidMap.H;
        var open = SolidMap.OpenCells;
        _ppu = SolidMap.Ppu;
        if (_tex == null || _texW != w || _texH != h)
        {
            if (_sprite) Object.Destroy(_sprite);
            if (_tex) Object.Destroy(_tex);
            _tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "MrpArchitectPlan", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            _tex.hideFlags |= HideFlags.HideAndDontSave;
            _texW = w; _texH = h;
            _px = new byte[w * h * 4];
            _sprite = null;
        }
        int band = Math.Max(1, (int)MathF.Round(WallBand * _ppu));
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var near = NearOpen(open, w, h, band);
        _bakes++;
        int grid = Math.Max(1, (int)MathF.Round(_ppu));
        var wet = WetRooms(w, h);
        var px = _px;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int k = row + x;
                byte[] c;
                if (open[k] != 0)
                {
                    bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1
                        || open[k - 1] == 0 || open[k + 1] == 0 || open[k - w] == 0 || open[k + w] == 0;
                    bool wr = wet[k] != 0;
                    c = edge ? (wr ? WetEdgeRgba : EdgeRgba)
                        : (x % grid == 0 || y % grid == 0) ? (wr ? WetGridRgba : GridRgba)
                        : wr ? WetFloorRgba : FloorRgba;
                }
                else c = near[k] ? WallRgba : null;
                int o = k * 4;
                if (c == null) { px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 0; continue; }
                px[o] = c[0]; px[o + 1] = c[1]; px[o + 2] = c[2]; px[o + 3] = c[3];
            }
        }
        fixed (byte* b = px) _tex.LoadRawTextureData((IntPtr)b, px.Length);
        _tex.Apply(false, false);
        _bakeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (!_sprite)
        {
            _sprite = Sprite.Create(_tex, new Rect(0, 0, w, h), FxMath.V2(0f, 0f), _ppu, 0, SpriteMeshType.FullRect);
            _sprite.hideFlags |= HideFlags.HideAndDontSave;
        }
    }

    // 升ごとに水回りの部屋 (配管を裂ける部屋) の中か。部屋の範囲の当たり判定を 0.25 単位ごとに調べて塗る (船ごとに 1 回)
    private static byte[] WetRooms(int w, int h)
    {
        if (_wetRoomGen == GameClock.ShipGen && _wetRoom != null && _wetRoom.Length == w * h) return _wetRoom;
        _wetRoom = new byte[w * h];
        _wetRoomGen = GameClock.ShipGen;
        var ship = ShipStatus.Instance;
        if (!ship) return _wetRoom;
        int step = Math.Max(1, (int)MathF.Round(_ppu * 0.25f));
        Vector2 so = SolidMap.Org;
        foreach (var r in ship.AllRooms)
        {
            if (!r || !r.roomArea || !WaterLeak.IsWaterRoomName(r.RoomId.ToString())) continue;
            var b = r.roomArea.bounds;
            Vector3 lo = b.min, hi = b.max;
            int x0 = Math.Max(0, (int)MathF.Floor((lo.x - so.x) * _ppu)), x1 = Math.Min(w - 1, (int)MathF.Ceiling((hi.x - so.x) * _ppu));
            int y0 = Math.Max(0, (int)MathF.Floor((lo.y - so.y) * _ppu)), y1 = Math.Min(h - 1, (int)MathF.Ceiling((hi.y - so.y) * _ppu));
            var area = r.roomArea;
            for (int y = y0; y <= y1; y += step)
                for (int x = x0; x <= x1; x += step)
                {
                    if (!area.OverlapPoint(FxMath.V2(so.x + (x + step * 0.5f) / _ppu, so.y + (y + step * 0.5f) / _ppu))) continue;
                    for (int yy = y; yy < Math.Min(h, y + step); yy++)
                        for (int xx = x; xx < Math.Min(w, x + step); xx++) _wetRoom[yy * w + xx] = 1;
                }
        }
        return _wetRoom;
    }

    // 歩ける升から縦横 band 升以内か (横に広げてから縦に広げる)
    private static bool[] NearOpen(byte[] open, int w, int h, int band)
    {
        if (_near == null || _near.Length != w * h) { _near = new bool[w * h]; _rowNear = new bool[w * h]; }
        var rowNear = _rowNear;
        for (int y = 0; y < h; y++)
        {
            int row = y * w, last = -1 - band;
            for (int x = 0; x < w; x++) { if (open[row + x] != 0) last = x; rowNear[row + x] = x - last <= band; }
            last = w + band;
            for (int x = w - 1; x >= 0; x--) { if (open[row + x] != 0) last = x; if (last - x <= band) rowNear[row + x] = true; }
        }
        var near = _near;
        for (int x = 0; x < w; x++)
        {
            int last = -1 - band;
            for (int y = 0; y < h; y++) { if (rowNear[y * w + x]) last = y; near[y * w + x] = y - last <= band; }
            last = h + band;
            for (int y = h - 1; y >= 0; y--) { if (rowNear[y * w + x]) last = y; if (last - y <= band) near[y * w + x] = true; }
        }
        return near;
    }

    // 生きている他の人を色なしの影で出す (ベントの中は出さない)。自分は本編の印のまま
    private static void PlaceShades()
    {
        var map = MapBehaviour.Instance;
        var lp = PlayerControl.LocalPlayer;
        int n = 0;
        // ほかの人の影が見えるのは構築士本人だけ (台を借りた人には居場所を教えない)
        bool show = _owner != null && _owner.IsLocal;
        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (!show) break;
            if (!pc || pc == lp || pc.Data == null || pc.Data.IsDead || pc.Data.Disconnected || pc.inVent) continue;
            var sr = Shade(n, map);
            if (!sr) break;
            Vector3 p = pc.transform.position;
            sr.transform.localPosition = FxMath.V3(p.x * _sign / _scale, p.y / _scale, ShadeZ);
            if (n >= _shown) sr.gameObject.SetActive(true);
            n++;
        }
        for (int i = n; i < _shown; i++) Shades[i].gameObject.SetActive(false);
        _shown = n;
    }

    // 構築士のドローンを、飛んでいる間 (台へ戻って降りるまで) 印で出す。
    // ドローンは世界で全員に見えているので、台を借りた人の設計図にも出す
    private static void PlaceDrone()
    {
        var d = _owner?.Drone;
        if (d == null || d.Done)
        {
            if (_droneShown) { _droneMark.gameObject.SetActive(false); _droneShown = false; }
            return;
        }
        if (_droneMark is null) // 層を作り直すと null に戻る (毎回 Unity の生存確認はしない)
        {
            var sp = ArchitectDrone.BodySprite;
            if (!sp) return;
            _droneMark = new GameObject("drone") { layer = _layer.layer }.transform;
            _droneMark.SetParent(_layer.transform, false);
            // 機体の影の絵は幅 1 (世界の長さ) なので、そのまま地図の長さの倍率にする
            _droneMark.localScale = FxMath.V3(DroneW, DroneW, 1f);
            var rim = NewSprite(_droneMark, "rim", sp, DroneRimColor).transform;
            rim.localScale = FxMath.V3(DroneRim, DroneRim, 1f);
            rim.localPosition = FxMath.V3(0f, 0f, 0.001f);
            NewSprite(_droneMark, "body", sp, DroneColor);
            _droneShown = true;
        }
        _droneMx = d.X * _sign / _scale;
        _droneMy = d.Y / _scale;
        _droneMark.localPosition = FxMath.V3(_droneMx, _droneMy, DroneZ);
        if (!_droneShown) { _droneMark.gameObject.SetActive(true); _droneShown = true; }
    }

    private static SpriteRenderer Shade(int i, MapBehaviour map)
    {
        if (i < Shades.Count) return Shades[i];
        if (!map || !map.HerePoint) return null;
        var sr = Object.Instantiate(map.HerePoint, _layer.transform);
        sr.name = "shade";
        sr.gameObject.SetActive(false);
        PlayerMaterial.SetColors(ShadeColor, sr);
        Shades.Add(sr);
        return sr;
    }

    // 地図の上の押しを受ける当たり判定・なぞった線の入れ物・届く範囲の円・ゲージの帯 (層を作った時に 1 回)
    private static void BuildInput(MapBehaviour map)
    {
        // 押しを受ける当たり判定は地図の絵より手前に出す (本編の閉じるボタンなどはこれより手前)
        var hit = new GameObject("input") { layer = _layer.layer };
        hit.transform.SetParent(_layer.transform, false);
        hit.transform.localPosition = FxMath.V3(0f, 0f, -2f);
        var col = hit.AddComponent<BoxCollider2D>();
        col.size = FxMath.V2(30f, 30f);
        _layerButton = hit.AddComponent<PassiveButton>();
        _layerButton.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        _layerButton.OnMouseOver = new UnityEngine.Events.UnityEvent();
        _layerButton.OnMouseOut = new UnityEngine.Events.UnityEvent();

        var ink = new GameObject("ink") { layer = _layer.layer };
        ink.transform.SetParent(_layer.transform, false);
        ink.transform.localPosition = FxMath.V3(0f, 0f, InkZ);
        _ink = ink.transform;
        Segs.Clear();
        _segsUsed = 0;

        _ring = NewSprite(_layer.transform, "reach", RingSprite(), RingColor).transform;

        var bg = map.transform.Find("Background");
        var bgSr = bg ? bg.GetComponent<SpriteRenderer>() : null;
        if (bgSr)
        {
            var b = bgSr.bounds;
            Vector3 lo = _layer.transform.InverseTransformPoint(b.min), hi = _layer.transform.InverseTransformPoint(b.max);
            _bounds = Rect.MinMaxRect(Math.Min(lo.x, hi.x), Math.Min(lo.y, hi.y), Math.Max(lo.x, hi.x), Math.Max(lo.y, hi.y));
        }
        else _bounds = Rect.MinMaxRect(-4.9f, -2.8f, 4.9f, 2.8f);
        float by = _bounds.yMin + BarLift, bx = (_bounds.xMin + _bounds.xMax) * 0.5f;
        var back = NewSprite(_layer.transform, "gaugeBack", WhiteSprite(), BarBack).transform;
        back.localPosition = FxMath.V3(bx, by, InkZ);
        back.localScale = FxMath.V3(BarW + 0.04f, BarH + 0.04f, 1f);
        _barFill = NewSprite(_layer.transform, "gaugeFill", WhiteSprite(), BarFill).transform;
    }

    // ゲージの帯: 左端を揃えて、残りの割合だけ伸ばす (変わった時だけ書く)
    private static void ShowGauge()
    {
        if (_owner == null || !_barFill) return;
        float k = Math.Clamp(_owner.Gauge / Math.Max(0.01f, _owner.GaugeMax), 0f, 1f);
        if (MathF.Abs(k - _barShown) < 0.004f) return;
        _barShown = k;
        float left = (_bounds.xMin + _bounds.xMax) * 0.5f - BarW * 0.5f;
        _barFill.localPosition = FxMath.V3(left + BarW * k * 0.5f, _bounds.yMin + BarLift, InkZ - 0.01f);
        _barFill.localScale = FxMath.V3(BarW * k, BarH, 1f);
    }

    // 押す・なぞる・離す (開いている間だけ・毎フレーム)
    internal static void Frame()
    {
        if (!_open) return;
        // 毎フレームの入口なので、ここで投げると後ろの処理まで止まる
        try { FrameCore(); }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"architect plan input: {e}");
            Close();
        }
    }

    private static void FrameCore()
    {
        // 役職の毎刻の処理が止まっても、地図が閉じた・層が消えたら閉じる (体を止めたままにしない)
        var map = MapBehaviour.Instance;
        if (_owner == null || !_layer || !map || !map.IsOpen) { Close(); return; }
        bool down = Input.GetMouseButtonDown(0), held = Input.GetMouseButton(0);
        if (!down && !held && !_drawing) return;
        if (_drawing && !held) { FinishLine(); return; }
        var cam = Camera.main;
        if (!cam) return;
        Vector3 l3 = _layer.transform.InverseTransformPoint(cam.ScreenToWorldPoint(Input.mousePosition));
        var w = FxMath.V2(l3.x * _scale * _sign, l3.y * _scale);
        if (down)
        {
            CancelLine();
            if (!OverLayer() || !InReach(w)) return;
            _drawing = true;
            Line.Add(w);
            return;
        }
        if (!_drawing) return;
        var last = Line[^1];
        float dx = w.x - last.x, dy = w.y - last.y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < PointStep || !InReach(w)) return;
        // ゲージが足りなければそこで止まる (離すまで線は伸びない)
        if (!_owner.TrySpend(len)) return;
        _spent += len;
        Line.Add(w);
        AddSeg(last, w);
    }

    private static bool InReach(Vector2 w)
    {
        float dx = w.x - _me.x, dy = w.y - _me.y;
        return dx * dx + dy * dy <= Impostor.Architect.Reach * Impostor.Architect.Reach;
    }

    // 押した所が設計図の上か (閉じるボタンや本編の UI の上なら false)
    private static bool OverLayer()
    {
        if (!PassiveButtonManager.InstanceExists) return false;
        var over = PassiveButtonManager.Instance.currentOver;
        return over && _layerButton && over.Pointer == _layerButton.Pointer;
    }

    private static void FinishLine()
    {
        if (Line.Count >= 2)
        {
            _owner.Build(new List<Vector2>(Line), _spent);
            _lines++;
        }
        // 動かさずに離した = 軽く押した: 近くの壁の配管を裂く
        else if (Line.Count == 1) _owner.Burst(Line[0]);
        ClearInk();
    }

    // 線を引いている途中で閉じた・押し直した: 使ったゲージを戻して線を消す
    private static void CancelLine()
    {
        if (_spent > 0f && _owner != null) _owner.Build(new List<Vector2>(), _spent);
        ClearInk();
    }

    private static void ClearInk()
    {
        _drawing = false;
        _spent = 0f;
        Line.Clear();
        for (int i = 0; i < _segsUsed; i++) if (Segs[i]) Segs[i].gameObject.SetActive(false);
        _segsUsed = 0;
    }

    // 線の 1 区間 = 白い 1 画素の絵を長さと太さに伸ばして回した板 (地図の座標で置く)
    private static void AddSeg(Vector2 a, Vector2 b)
    {
        float ax = a.x * _sign / _scale, ay = a.y / _scale, bx = b.x * _sign / _scale, by = b.y / _scale;
        SpriteRenderer sr;
        if (_segsUsed < Segs.Count) sr = Segs[_segsUsed];
        else { sr = NewSprite(_ink, "seg", WhiteSprite(), InkColor); Segs.Add(sr); }
        _segsUsed++;
        sr.gameObject.SetActive(true);
        float dx = bx - ax, dy = by - ay, len = MathF.Sqrt(dx * dx + dy * dy);
        var tr = sr.transform;
        tr.localPosition = FxMath.V3((ax + bx) * 0.5f, (ay + by) * 0.5f, 0f);
        tr.localRotation = FxMath.RotZ(MathF.Atan2(dy, dx) * 57.29578f);
        tr.localScale = FxMath.V3(len + PenWidth, PenWidth, 1f);
    }

    private static SpriteRenderer NewSprite(Transform parent, string name, Sprite sp, Color color)
    {
        var go = new GameObject(name) { layer = _layer.layer };
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.color = color;
        return sr;
    }

    private static Sprite WhiteSprite()
    {
        if (_white) return _white;
        var tex = Texture2D.whiteTexture;
        _white = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), FxMath.V2(0.5f, 0.5f), tex.width);
        _white.hideFlags = HideFlags.HideAndDontSave;
        return _white;
    }

    // 直径 1 の輪 (起動中に 1 回だけ作る)
    private static unsafe Sprite RingSprite()
    {
        if (_ringSprite) return _ringSprite;
        int n = RingPx;
        var px = new byte[n * n * 4];
        float c = (n - 1) * 0.5f, r = n * 0.5f - 2f, half = RingLine * n * 0.5f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x - c, dy = y - c, d = MathF.Abs(MathF.Sqrt(dx * dx + dy * dy) - (r - half));
                float a = Math.Clamp(half + 0.5f - d, 0f, 1f);
                int k = (y * n + x) * 4;
                px[k] = px[k + 1] = px[k + 2] = 255;
                px[k + 3] = (byte)(a * 255f);
            }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpArchitectReach", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = px) tex.LoadRawTextureData((IntPtr)b, px.Length);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;
        _ringSprite = Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n, 0, SpriteMeshType.FullRect);
        _ringSprite.hideFlags |= HideFlags.HideAndDontSave;
        return _ringSprite;
    }

    internal static string Describe() =>
        $"open={_open} opens={_opens} tex={_texW}x{_texH} ppu={_ppu} scale={_scale:0.###} sign={_sign} shades={_shown} drone={(_droneShown ? $"{_droneMx:0.00},{_droneMy:0.00}" : "-")} bakes={_bakes} bakeMs={_bakeMs:0.0} water={_hadWater} waterMs={_waterMs:0.00} drawing={_drawing} pts={Line.Count} spent={_spent:0.0} lines={_lines}";
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.Show))]
internal static class ArchitectPlanShowPatch
{
    public static void Postfix(MapBehaviour __instance, [HarmonyArgument(0)] MapOptions opts)
    {
        try { ArchitectPlan.OnMapShown(__instance, opts); }
        catch (Exception e) { Plugin.Logger.LogError($"architect plan: {e}"); }
    }
}

// 設計図を開いている間、本編の歩行 (と流れや水の動き) の後で自分の体を止める
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
[HarmonyPriority(Priority.Last)]
internal static class ArchitectPlanHold
{
    public static void Postfix(PlayerPhysics __instance)
    {
        if (!ArchitectPlan.IsOpen || !__instance.AmOwner) return;
        __instance.body.velocity = FxMath.V2(0f, 0f);
    }
}
