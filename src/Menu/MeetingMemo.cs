// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Behavior/MeetingMapLayer.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 会議中に開いた地図へ、自分にだけ見えるメモを書く: 引けば線・軽く押せばその場所に人の選び出しが開き、選んだ人の印を置く。
// 印はつかんで動かせる。消し方 = 右クリック / 長押し (指) で 1 つ・取り消しのボタンで最後の 1 つ・全部消すのボタン。
// 通信はしない。書いた物は同じ試合の次の会議にも残し、船が替わったら消す。
// 座標はメモの層 (地図の子) の中で持つので、地図の倍率と左右反転はそのまま乗る
internal static class MeetingMemo
{
    internal const string LayerName = "MrpMeetingMemo";

    private const float PenWidth = 0.035f;
    private const float PointStep = 0.05f;    // これだけ動いたら線に点を足す
    private const float IconScale = 1f;       // 置いた印 (本編の自分の印と同じ大きさ)
    private const float PickScale = 1.3f;     // 選び出しの人 (押しやすく)
    private const float PickRadius = 0.16f;   // 印・線をつかむ・消す近さ
    private const float HoldSeconds = 0.6f;   // 長押しで消す
    private const float PickStep = 0.42f;     // 選び出しの人の間隔
    private const int PickPerRow = 8;
    private static readonly Color Pen = new(0.95f, 0.95f, 0.95f, 0.9f);
    private static readonly Color PickBack = new(0f, 0f, 0f, 0.7f);
    private static readonly Color ClearTint = new(1f, 0.55f, 0.55f, 1f);

    private sealed class Stroke
    {
        public readonly List<Vector2> Points = new();
        public GameObject Go;
    }

    private sealed class Mark
    {
        public byte PlayerId;
        public Vector2 Pos;
        public SpriteRenderer Sr;
    }

    // 書いた物 (層の座標)。History は書いた順 (取り消しは最後から)
    private static readonly List<object> History = new();
    private static int _gen = -1;

    // 今の地図の上の物
    private static GameObject _layer;
    private static PassiveButton _layerButton;
    private static SpriteRenderer _proto;
    private static Rect _bounds;              // 地図の絵の範囲 (層の座標)
    private static bool _open;

    // 人の選び出し (開いている間だけ)
    private static GameObject _picker;
    private static Vector2 _pickAt;
    private static readonly List<(byte Id, Vector2 Pos)> PickIcons = new();      // 開いている間の人と位置 (層の座標)
    private static readonly List<(byte Id, SpriteRenderer Sr, Vector2 Off)> PickRow = new();
    private static float _pickW, _pickH;
    private static readonly Color Faded = new(1f, 1f, 1f, 0.45f);

    // 押している間
    private enum Drag { None, Line, Mark, Wait }
    private static Drag _drag;
    private static Stroke _stroke;
    private static Mark _mark;
    private static object _holdTarget;
    private static Vector2 _downAt;
    private static float _downTime;
    private static bool _moved;

    private static Sprite _white, _undoSprite, _clearSprite;

    private static readonly UnityAction UndoClick = (UnityAction)Undo;
    private static readonly UnityAction ClearClick = (UnityAction)Clear;
    private static readonly UnityAction NoClick = (UnityAction)(() => { });

    internal static int Lines { get { int n = 0; foreach (var h in History) if (h is Stroke) n++; return n; } }
    internal static int Marks { get { int n = 0; foreach (var h in History) if (h is Mark) n++; return n; } }

    // ---- 地図を開いた・閉じた ----

    internal static void OnMapShown(MapBehaviour map, MapOptions opts)
    {
        bool want = MeetingHud.Instance && opts != null && opts.Mode == MapOptions.Modes.Normal;
        ClosePicker();
        _drag = Drag.None;
        if (!want)
        {
            if (_layer) _layer.SetActive(false);
            _open = false;
            return;
        }
        if (_gen != Terrain.GameClock.ShipGen) { History.Clear(); _gen = Terrain.GameClock.ShipGen; }
        if (!_layer || _layer.transform.parent != map.transform) Build(map);
        _layer.SetActive(true);
        _open = true;
    }

    private static void Build(MapBehaviour map)
    {
        if (_layer) Object.Destroy(_layer);
        _layer = new GameObject(LayerName);
        _layer.layer = map.gameObject.layer;
        _layer.transform.SetParent(map.transform, false);
        _layer.transform.localPosition = FxMath.V3(0f, 0f, -2f);

        // 地図の上の押しを受ける当たり判定 (押した所がメモの上かの目印。本編のボタンはこれより手前)
        var col = _layer.AddComponent<BoxCollider2D>();
        col.size = FxMath.V2(30f, 30f);
        _layerButton = _layer.AddComponent<PassiveButton>();
        _layerButton.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        _layerButton.OnMouseOver = new UnityEvent();
        _layerButton.OnMouseOut = new UnityEvent();
        _layerButton.OnClick.AddListener(NoClick);

        _proto = map.HerePoint;
        var bg = map.transform.Find("Background");
        var bgSr = bg ? bg.GetComponent<SpriteRenderer>() : null;
        if (bgSr)
        {
            var b = bgSr.bounds;
            Vector3 lo = _layer.transform.InverseTransformPoint(b.min), hi = _layer.transform.InverseTransformPoint(b.max);
            _bounds = Rect.MinMaxRect(Math.Min(lo.x, hi.x), Math.Min(lo.y, hi.y), Math.Max(lo.x, hi.x), Math.Max(lo.y, hi.y));
        }
        else _bounds = Rect.MinMaxRect(-4.9f, -2.8f, 4.9f, 2.8f);
        foreach (var h in History) Draw(h);

        // 取り消し・全部消すのボタン = 地図の閉じるボタンの写しの絵を本編のクイックチャットの物に替えた物 (閉じるボタンの右に並べる)
        LookSprites();
        var close = map.transform.Find("CloseButton");
        if (close)
        {
            MakeButton(close, "MrpMemoUndo", _undoSprite, 0.7f, Color.white, UndoClick);
            MakeButton(close, "MrpMemoClear", _clearSprite, 1.4f, ClearTint, ClearClick);
        }
    }

    private static void MakeButton(Transform close, string name, Sprite sprite, float dx, Color tint, UnityAction act)
    {
        var b = Object.Instantiate(close.gameObject, _layer.transform);
        b.name = name;
        var at = _layer.transform.InverseTransformPoint(close.position);
        b.transform.localPosition = FxMath.V3(at.x + dx, at.y, at.z);
        // 閉じるボタンの別の働き (コントローラーの戻るで閉じる) は外す
        var console = b.GetComponent<CloseButtonConsoleBehaviour>();
        if (console) Object.Destroy(console);
        var sr = b.GetComponent<SpriteRenderer>();
        if (sr)
        {
            if (sprite) sr.sprite = sprite;
            sr.color = tint;
        }
        // 地図の閉じるボタンは ButtonBehavior (押すと地図を閉じる) なので、押した時の処理ごと差し替える
        var bb = b.GetComponent<ButtonBehavior>();
        if (bb)
        {
            bb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            bb.OnClick.AddListener(act);
        }
    }

    // 本編のクイックチャットの取り消し・削除の絵 (無ければ閉じるボタンの絵のまま)
    private static void LookSprites()
    {
        if (_undoSprite && _clearSprite) return;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var s = o.TryCast<Sprite>();
            if (!s) continue;
            if (s.name == "ICON-QCUndo") _undoSprite = s;
            else if (s.name == "ICON-QCDelete") _clearSprite = s;
        }
    }

    private static SpriteRenderer NewIcon(Transform parent, byte playerId, Vector2 pos, float z, string name, float scale = IconScale)
    {
        if (!_proto) return null;
        var sr = Object.Instantiate(_proto, parent);
        sr.name = name;
        sr.gameObject.SetActive(true);
        var pulse = sr.GetComponent<AlphaPulse>();
        if (pulse) Object.Destroy(pulse);
        sr.color = Color.white;
        sr.transform.localPosition = FxMath.V3(pos.x, pos.y, z);
        var s = _proto.transform.localScale;
        sr.transform.localScale = FxMath.V3(Math.Abs(s.x) * scale, Math.Abs(s.y) * scale, 1f);
        var info = GameData.Instance ? GameData.Instance.GetPlayerById(playerId) : null;
        if (info != null) PlayerMaterial.SetColors(info.DefaultOutfit.ColorId, sr);
        return sr;
    }

    // ---- 人の選び出し ----

    // 押した所の上に、部屋の全員の印を並べた札を開く (幽霊・抜けた人は薄く)。地図の絵の外へはみ出さないよう寄せる。
    // 札は人の顔ぶれが変わった時だけ作り直し、閉じる時は隠すだけ (印を塗るたびに本編が材質を複製しても増え続けないように)
    private static void OpenPicker(Vector2 at)
    {
        ClosePicker();
        if (!GameData.Instance || !_proto) return;
        var players = new List<NetworkedPlayerInfo>();
        foreach (var p in GameData.Instance.AllPlayers) if (p != null) players.Add(p);
        if (players.Count == 0) return;
        bool same = _picker && PickRow.Count == players.Count;
        for (int i = 0; same && i < players.Count; i++) same = PickRow[i].Id == players[i].PlayerId;
        if (!same) BuildPicker(players);

        float cx = Math.Clamp(at.x, _bounds.xMin + _pickW / 2f, _bounds.xMax - _pickW / 2f);
        float cy = at.y + 0.2f + _pickH / 2f;
        if (cy + _pickH / 2f > _bounds.yMax) cy = at.y - 0.2f - _pickH / 2f;
        _pickAt = at;
        _picker.transform.localPosition = FxMath.V3(cx, cy, -0.5f);
        PickIcons.Clear();
        for (int i = 0; i < PickRow.Count; i++)
        {
            var (id, sr, off) = PickRow[i];
            if (sr) sr.color = players[i].IsDead || players[i].Disconnected ? Faded : Color.white;
            PickIcons.Add((id, FxMath.V2(cx + off.x, cy + off.y)));
        }
        _picker.SetActive(true);
    }

    private static void BuildPicker(List<NetworkedPlayerInfo> players)
    {
        if (_picker) Object.Destroy(_picker);
        PickRow.Clear();
        int cols = Math.Min(PickPerRow, players.Count), rows = (players.Count + PickPerRow - 1) / PickPerRow;
        _pickW = cols * PickStep + 0.1f;
        _pickH = rows * PickStep + 0.1f;
        _picker = new GameObject("MrpMemoPicker");
        _picker.layer = _layer.layer;
        _picker.transform.SetParent(_layer.transform, false);
        var back = NewSprite(_picker.transform, "Back", PickBack);
        back.transform.localScale = FxMath.V3(_pickW, _pickH, 1f);
        for (int i = 0; i < players.Count; i++)
        {
            int inRow = Math.Min(PickPerRow, players.Count - i / PickPerRow * PickPerRow);
            float x = (i % PickPerRow - (inRow - 1) / 2f) * PickStep;
            float y = ((rows - 1) / 2f - i / PickPerRow) * PickStep;
            var sr = NewIcon(_picker.transform, players[i].PlayerId, FxMath.V2(x, y), -0.1f, "Pick", PickScale);
            PickRow.Add((players[i].PlayerId, sr, FxMath.V2(x, y)));
        }
    }

    private static void ClosePicker()
    {
        if (_picker) _picker.SetActive(false);
        PickIcons.Clear();
    }

    private static byte HitPicker(Vector2 p)
    {
        foreach (var (id, c) in PickIcons)
        {
            float dx = p.x - c.x, dy = p.y - c.y;
            if (dx * dx + dy * dy <= PickStep * PickStep * 0.25f) return id;
        }
        return byte.MaxValue;
    }

    // ---- 描く ----

    private static void Draw(object h)
    {
        if (h is Mark m) m.Sr = NewIcon(_layer.transform, m.PlayerId, m.Pos, -0.2f, "MrpMemoMark");
        else if (h is Stroke s) DrawStroke(s);
    }

    private static void DrawStroke(Stroke s)
    {
        if (s.Go) Object.Destroy(s.Go);
        s.Go = new GameObject("MrpMemoLine");
        s.Go.layer = _layer.layer;
        s.Go.transform.SetParent(_layer.transform, false);
        s.Go.transform.localPosition = FxMath.V3(0f, 0f, -0.1f);
        for (int i = 1; i < s.Points.Count; i++) AddSegment(s, s.Points[i - 1], s.Points[i]);
    }

    // 線の 1 区間 = 白い 1 画素の絵を長さと太さに伸ばして回した板
    private static void AddSegment(Stroke s, Vector2 a, Vector2 b)
    {
        var sr = NewSprite(s.Go.transform, "Seg", Pen);
        float dx = b.x - a.x, dy = b.y - a.y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        var tr = sr.transform;
        tr.localPosition = FxMath.V3((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, 0f);
        tr.localRotation = FxMath.RotZ(MathF.Atan2(dy, dx) * 57.29578f);
        tr.localScale = FxMath.V3(len + PenWidth, PenWidth, 1f);
    }

    private static SpriteRenderer NewSprite(Transform parent, string name, Color color)
    {
        if (!_white)
        {
            var tex = Texture2D.whiteTexture;
            _white = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), FxMath.V2(0.5f, 0.5f), tex.width);
            _white.hideFlags = HideFlags.HideAndDontSave;
        }
        var go = new GameObject(name);
        go.layer = _layer.layer;
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _white;
        sr.color = color;
        return sr;
    }

    private static void Remove(object h)
    {
        History.Remove(h);
        if (h is Mark m && m.Sr) Object.Destroy(m.Sr.gameObject);
        else if (h is Stroke s && s.Go) Object.Destroy(s.Go);
    }

    internal static void Undo()
    {
        ClosePicker();
        if (History.Count > 0) Remove(History[^1]);
    }

    internal static void Clear()
    {
        ClosePicker();
        while (History.Count > 0) Remove(History[^1]);
    }

    // ---- 押す・引く (地図を開いている間だけ・毎フレーム) ----

    public static void Tick()
    {
        if (!_open) return;
        var map = MapBehaviour.Instance;
        if (!_layer || !map || !map.IsOpen || !MeetingHud.Instance)
        {
            _open = false;
            _drag = Drag.None;
            ClosePicker();
            if (_layer) _layer.SetActive(false);
            return;
        }
        bool down = Input.GetMouseButtonDown(0), held = Input.GetMouseButton(0), right = Input.GetMouseButtonDown(1);
        if (!down && !held && !right && _drag == Drag.None) return;

        var cam = Camera.main;
        if (!cam) return;
        Vector3 l3 = _layer.transform.InverseTransformPoint(cam.ScreenToWorldPoint(Input.mousePosition));
        var p = FxMath.V2(l3.x, l3.y);

        if (right)
        {
            ClosePicker();
            if (OverLayer()) { var hit = HitTest(p); if (hit != null) Remove(hit); }
            return;
        }
        if (down) { Press(p); return; }
        if (!held) { Release(); return; }

        float mx = p.x - _downAt.x, my = p.y - _downAt.y;
        if (mx * mx + my * my > PointStep * PointStep) _moved = true;
        // 動かさずに長押し = その物を消す
        if (_holdTarget != null && !_moved && Time.unscaledTime - _downTime >= HoldSeconds)
        {
            Remove(_holdTarget);
            _holdTarget = null;
            _drag = Drag.None;
            _mark = null;
            _stroke = null;
            return;
        }
        if (_moved) _holdTarget = null;

        if (_drag == Drag.Wait && _moved) _drag = Drag.Line; // 線の上から引き始めても線を引く
        if (_drag == Drag.Mark && _mark != null && _moved)
        {
            _mark.Pos = p;
            if (_mark.Sr) _mark.Sr.transform.localPosition = FxMath.V3(p.x, p.y, -0.2f);
        }
        else if (_drag == Drag.Line && _stroke != null)
        {
            var last = _stroke.Points[^1];
            float dx = p.x - last.x, dy = p.y - last.y;
            if (dx * dx + dy * dy < PointStep * PointStep) return;
            if (_stroke.Go == null)
            {
                History.Add(_stroke);
                DrawStroke(_stroke);
            }
            _stroke.Points.Add(p);
            AddSegment(_stroke, last, p);
        }
    }

    private static void Press(Vector2 p)
    {
        _drag = Drag.None;
        if (!OverLayer()) { ClosePicker(); return; }
        if (PickIcons.Count > 0)
        {
            byte id = HitPicker(p);
            ClosePicker();
            if (id != byte.MaxValue)
            {
                var m = new Mark { PlayerId = id, Pos = _pickAt };
                History.Add(m);
                Draw(m);
                return;
            }
        }
        _downAt = p;
        _downTime = Time.unscaledTime;
        _moved = false;
        var hit = HitTest(p);
        _holdTarget = hit;
        if (hit is Mark mk) { _mark = mk; _drag = Drag.Mark; return; }
        _stroke = new Stroke();
        _stroke.Points.Add(p);
        _drag = hit != null ? Drag.Wait : Drag.Line;
    }

    private static void Release()
    {
        // 何もない所を動かさずに押して離した = その場所に人の選び出しを開く
        if (_drag == Drag.Line && !_moved && _holdTarget == null) OpenPicker(_downAt);
        _drag = Drag.None;
        _stroke = null;
        _mark = null;
        _holdTarget = null;
    }

    // 押した所がメモの層の上か (取り消し・閉じるのボタンや本編の UI の上なら false)
    private static bool OverLayer()
    {
        if (!PassiveButtonManager.InstanceExists) return false;
        var over = PassiveButtonManager.Instance.currentOver;
        return over && _layerButton && over.Pointer == _layerButton.Pointer;
    }

    // いちばん上に描いた物から: 印は中心の近さ・線は区間までの近さ
    private static object HitTest(Vector2 p)
    {
        for (int i = History.Count - 1; i >= 0; i--)
        {
            var h = History[i];
            if (h is Mark m)
            {
                float dx = p.x - m.Pos.x, dy = p.y - m.Pos.y;
                if (dx * dx + dy * dy <= PickRadius * PickRadius) return m;
            }
            else if (h is Stroke s)
            {
                for (int k = 1; k < s.Points.Count; k++)
                    if (SegDist2(p, s.Points[k - 1], s.Points[k]) <= PickRadius * PickRadius * 0.5f) return s;
            }
        }
        return null;
    }

    private static float SegDist2(Vector2 p, Vector2 a, Vector2 b)
    {
        float vx = b.x - a.x, vy = b.y - a.y, wx = p.x - a.x, wy = p.y - a.y;
        float l2 = vx * vx + vy * vy;
        float t = l2 > 0f ? Math.Clamp((wx * vx + wy * vy) / l2, 0f, 1f) : 0f;
        float ex = wx - vx * t, ey = wy - vy * t;
        return ex * ex + ey * ey;
    }

    // ---- 確認用 ----

    internal static string Describe(string args)
    {
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = a.Length > 0 ? a[0] : "";
        try
        {
            switch (cmd)
            {
                case "meeting":
                {
                    // 本編のホストが通報を受けた時と同じ道で会議を始める
                    var lp = PlayerControl.LocalPlayer;
                    if (!lp || !AmongUsClient.Instance.AmHost) return "ERR memo meeting needs host";
                    MeetingRoomManager.Instance.AssignSelf(lp, null);
                    Vanilla.Hud?.OpenMeetingRoom(lp);
                    lp.RpcStartMeeting(null);
                    return "OK memo meeting";
                }
                case "open":
                    if (!MeetingHud.Instance || !Vanilla.Hud) return "ERR memo open needs meeting";
                    Vanilla.Hud.ToggleMapVisible(new MapOptions { Mode = MapOptions.Modes.Normal });
                    break;
                case "line" when a.Length >= 5:
                {
                    var s = new Stroke();
                    float x1 = F(a[1]), y1 = F(a[2]), x2 = F(a[3]), y2 = F(a[4]);
                    const int n = 8;
                    for (int i = 0; i <= n; i++) s.Points.Add(FxMath.V2(x1 + (x2 - x1) * i / n, y1 + (y2 - y1) * i / n));
                    History.Add(s);
                    if (_layer) DrawStroke(s);
                    break;
                }
                case "mark" when a.Length >= 4:
                {
                    var m = new Mark { PlayerId = byte.Parse(a[1]), Pos = FxMath.V2(F(a[2]), F(a[3])) };
                    History.Add(m);
                    if (_layer) Draw(m);
                    break;
                }
                case "pick" when a.Length >= 3: if (_layer) OpenPicker(FxMath.V2(F(a[1]), F(a[2]))); break;
                case "choose" when a.Length >= 2:
                {
                    // 開いている選び出しの n 番目を押したのと同じ
                    int k = int.Parse(a[1]);
                    if (k < 0 || k >= PickIcons.Count) return "ERR memo no pick " + k;
                    var m = new Mark { PlayerId = PickIcons[k].Id, Pos = _pickAt };
                    ClosePicker();
                    History.Add(m);
                    Draw(m);
                    break;
                }
                case "undo": Undo(); break;
                case "clear": Clear(); break;
            }
        }
        catch (Exception e) { return "ERR memo " + e.Message; }
        return $"OK memo open={_open} layer={(bool)_layer} lines={Lines} marks={Marks} picker={PickIcons.Count} bounds=({_bounds.xMin:0.0},{_bounds.yMin:0.0})-({_bounds.xMax:0.0},{_bounds.yMax:0.0}) undoSprite={(bool)_undoSprite} clearSprite={(bool)_clearSprite}";
    }

    private static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.Show))]
internal static class MeetingMemoShowPatch
{
    public static void Postfix(MapBehaviour __instance, [HarmonyArgument(0)] MapOptions opts)
    {
        try { MeetingMemo.OnMapShown(__instance, opts); }
        catch (Exception e) { Plugin.Logger.LogError($"meeting memo: {e}"); }
    }
}
