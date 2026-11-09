using System;
using System.Collections.Generic;
using System.Text;
using MoreRolesPlus.UI;
using PowerTools;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 本物の船の上で見るリプレイ (幽霊になって試合を見て回る感じ)。
// 試合の後 (終わりの画面・ロビー) に、その試合の船の絵を読み込み直して、動く部品を外した「見た目だけの船」を遠くへ置き、
// カメラをそこへ移す。全員は本編の立ち絵 (PoolablePlayer) に着せ替えを着せて、記録した位置を歩かせる (歩き・止まり・幽霊のアニメ)。
// カメラは選んだ人を追う (人の名前のボタン・Tab で次の人) か、自由に動かす (WASD・ホイールで寄る/引く)。
// 閉じるとカメラを元に戻し、船と人を捨て、読み込んだ船を手放す
internal static class ReplayStage
{
    private static readonly Vector3 Origin = new(600f, 600f, 0f);
    private static readonly float[] Speeds = { 1f, 2f, 4f, 8f };
    private const int OverlayLayer = 5;

    private static MatchReplay.Record _r;
    private static GameObject _root, _overlay;
    private static AssetReference _ref;
    private static bool _loadedByUs, _built;
    private static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> _op;
    private static bool _opValid;
    private static float _wait;

    // カメラの元の状態
    private static Camera _cam;
    private static Vector3 _camPos;
    private static float _camSize;
    private static int _camMask;
    private static FollowerCamera _follower;
    private static bool _followerOn;

    // 再生
    private static float _t;
    private static bool _playing, _reverse;
    private static int _speed;
    private static int _follow; // 追う人 (-1 = 自由)
    private static Vector2 _free;
    private static float _zoom = 3f;

    // 人
    private sealed class Actor
    {
        public PoolablePlayer Pp;
        public SpriteAnim Body;
        public Vector3 Fix;      // 足元から PoolablePlayer の原点へのずれ
        public bool Left, Running, Ghost, Shown = true;
        public AnimationClip Clip;
        public GameObject DeadBody;
        public TextMeshPro Label;
    }
    private static Actor[] _actors;

    // 画面の部品
    private static Slider _slider;
    private static TextMeshPro _time, _log, _playLabel, _speedLabel, _dirLabel, _status, _view;
    private static string _logShown;
    private static GameObject _meetPanel;
    private static float _overlayH = 1f; // 部品を並べた時のカメラの大きさ (寄る・引くに合わせて同じ見え方に保つ)
    private static TextMeshPro _meetTitle, _meetPeople, _meetChat;
    private static string _meetShown;
    private const float MeetW = 6.6f, MeetH = 3.5f, MeetX = -1.4f, MeetY = -0.1f;

    internal static bool IsOpen => _r != null;

    internal static bool Open()
    {
        var r = MatchReplay.Last;
        if (r == null || r.MapId < 0 || IsOpen) return IsOpen;
        var prefabs = AmongUsClient.Instance ? AmongUsClient.Instance.ShipPrefabs : null;
        if (prefabs == null || r.MapId >= prefabs.Count) return false;
        if (!Proto()) return false;
        _cam = Camera.main;
        if (!_cam) return false;
        _r = r;
        _built = false;
        _wait = 0f;
        _ref = prefabs[r.MapId];
        // もう読み込まれていればそれを使い、無ければ読み込む (読み込んだ物だけ閉じる時に手放す)
        _loadedByUs = false;
        _opValid = false;
        if (!(_ref.Asset && _ref.Asset.TryCast<GameObject>()))
        {
            // 他が読み込んでいる途中なら出来上がりを待つ
            if (!_ref.IsValid()) { _op = _ref.LoadAssetAsync<GameObject>(); _opValid = true; _loadedByUs = true; }
        }

        // カメラを舞台へ
        _camPos = _cam.transform.position;
        _camSize = _cam.orthographicSize;
        _camMask = _cam.cullingMask;
        _follower = _cam.GetComponent<FollowerCamera>();
        _followerOn = _follower && _follower.enabled;
        if (_follower) _follower.enabled = false;
        if (r.CamMask != -1) _cam.cullingMask = r.CamMask | (1 << OverlayLayer);
        _zoom = 3f;
        _cam.orthographicSize = _zoom;

        _root = new GameObject("MrpReplayStage");
        _root.transform.position = Origin;
        _t = 0f;
        _playing = false;
        _reverse = false;
        _speed = 0;
        _follow = 0;
        _free = Vector2.zero;
        BuildOverlay();
        Plugin.Logger.LogInfo($"[replay] stage open map={r.MapId} cached={(_opValid ? "no" : "yes")}");
        return true;
    }

    private static PoolablePlayer Proto()
    {
        var egm = Object.FindObjectOfType<EndGameManager>();
        if (egm && egm.PlayerPrefab) return egm.PlayerPrefab;
        var hud = Vanilla.Hud;
        return hud && hud.IntroPrefab ? hud.IntroPrefab.PlayerPrefab : null;
    }

    internal static void Close()
    {
        if (_r == null) return;
        _r = null;
        if (_root) Object.Destroy(_root);
        if (_overlay) Object.Destroy(_overlay);
        _root = _overlay = null;
        _actors = null;
        if (_cam)
        {
            _cam.transform.position = _camPos;
            _cam.orthographicSize = _camSize;
            _cam.cullingMask = _camMask;
        }
        if (_follower) _follower.enabled = _followerOn;
        if (_loadedByUs && _ref != null)
        {
            try { _ref.ReleaseAsset(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"replay release: {e.Message}"); }
        }
        _ref = null;
        _loadedByUs = _opValid = false;
    }

    // ── 舞台を組む ──

    private static void BuildShip(GameObject prefab)
    {
        var r = _r;
        // 止めた親の下に複製して、本編の処理 (Awake) を走らせずに見た目以外の部品を外す
        var holder = new GameObject("Ship");
        holder.SetActive(false);
        holder.transform.SetParent(_root.transform, false);
        var ship = Object.Instantiate(prefab, holder.transform);
        ship.transform.localPosition = r.ShipPos;
        ship.transform.localScale = r.ShipScale;
        Strip(ship);
        holder.SetActive(true);
    }

    private static void Strip(GameObject ship)
    {
        // 何度かに分けて外す (他の部品が頼っている部品は後で外れる)
        for (int pass = 0; pass < 4; pass++)
        {
            int left = 0;
            foreach (var c in ship.GetComponentsInChildren<Component>(true))
            {
                if (!c || Keep(c)) continue;
                try { Object.DestroyImmediate(c); }
                catch { left++; }
            }
            if (left == 0) break;
        }
        // 当たり判定に頼っていた本編の音の箱なども無くなった。影を作る層 (本編の影のカメラが見る層) の物は映さない
    }

    private static bool Keep(Component c) =>
        c.TryCast<Transform>() != null || c.TryCast<SpriteRenderer>() != null || c.TryCast<MeshRenderer>() != null || c.TryCast<MeshFilter>() != null
        || c.TryCast<SpriteMask>() != null || c.TryCast<UnityEngine.Rendering.SortingGroup>() != null || c.TryCast<Animator>() != null
        || c.TryCast<SpriteAnim>() != null || c.TryCast<LineRenderer>() != null;

    private static void BuildActors()
    {
        var r = _r;
        var proto = Proto();
        int n = r.Players.Count;
        _actors = new Actor[n];
        var people = new GameObject("People");
        people.transform.SetParent(_root.transform, false);
        for (int i = 0; i < n; i++)
        {
            var p = r.Players[i];
            var a = new Actor();
            _actors[i] = a;
            if (!proto) continue;
            var pp = Object.Instantiate(proto, people.transform);
            pp.name = "Replay_" + p.Name;
            a.Pp = pp;
            try
            {
                var o = new NetworkedPlayerInfo.PlayerOutfit
                {
                    PlayerName = p.Name,
                    ColorId = p.ColorId,
                    HatId = p.HatId,
                    VisorId = p.VisorId,
                    SkinId = p.SkinId,
                    PetId = "",
                    NamePlateId = "",
                };
                pp.UpdateFromPlayerOutfit(o, PlayerMaterial.MaskType.None, false, false);
                pp.ToggleName(false);
                pp.TogglePet(false);
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"replay dress {p.Name}: {e.Message}"); }

            // 大きさを試合の体に合わせる
            var body = pp.cosmetics && pp.cosmetics.currentBodySprite != null ? pp.cosmetics.currentBodySprite.BodySprite : null;
            float h = body && body.sprite ? body.bounds.size.y : 0f;
            if (h > 0.01f)
            {
                float k = r.BodyHeight / h;
                pp.transform.localScale = Vector3.Scale(pp.transform.localScale, new Vector3(k, k, 1f));
            }
            if (body)
            {
                // 足元 → 立ち絵の原点のずれ (体の中心を記録の体の中心へ)
                Vector3 center = body.bounds.center - pp.transform.position;
                a.Fix = new Vector3(r.BodyOffset.x - center.x, r.BodyOffset.y - center.y, 0f);
                a.Body = body.GetComponent<SpriteAnim>();
                if (!a.Body)
                {
                    try { a.Body = body.gameObject.AddComponent<SpriteAnim>(); }
                    catch (Exception e) { Plugin.Logger.LogWarning($"replay anim: {e.Message}"); }
                }
            }

            // 頭の上の名前
            var proto2 = UiAssets.TextProto;
            if (proto2)
            {
                var t = Object.Instantiate(proto2, people.transform);
                t.name = "Name_" + p.Name;
                t.gameObject.SetActive(true);
                t.gameObject.layer = pp.gameObject.layer;
                t.enableWordWrapping = false;
                t.alignment = TextAlignmentOptions.Center;
                t.fontSize = 0.9f;
                t.text = p.Name;
                t.color = Color.white;
                t.outlineWidth = 0.25f;
                t.outlineColor = Color.black;
                t.rectTransform.sizeDelta = new Vector2(3f, 0.5f);
                a.Label = t;
            }

            // 死体 (倒された所に、次の会議まで)
            if (MatchReplay.BodyPrefab)
            {
                var hold = new GameObject("BodyHold");
                hold.SetActive(false);
                hold.transform.SetParent(people.transform, false);
                var db = Object.Instantiate(MatchReplay.BodyPrefab.gameObject, hold.transform);
                foreach (var sr in db.GetComponentsInChildren<SpriteRenderer>(true)) PlayerMaterial.SetColors(p.ColorId, sr);
                Strip(db);
                db.transform.SetParent(people.transform, false);
                Object.Destroy(hold);
                db.SetActive(false);
                a.DeadBody = db;
            }
        }
        _built = true;
    }

    // ── 画面の部品 ──

    private static void BuildOverlay()
    {
        _overlay = Widget.Node("MrpReplayOverlay", _cam.transform, OverlayLayer);
        _overlay.transform.localPosition = new Vector3(0f, 0f, 5f);
        var group = _overlay.AddComponent<UnityEngine.Rendering.SortingGroup>();
        group.sortingOrder = 500;
        float h = _cam.orthographicSize, w = h * _cam.aspect;
        _overlayH = h;

        // 下の帯 (つまみとボタン)
        var bottom = Widget.Node("Bottom", _overlay.transform, OverlayLayer);
        bottom.transform.localPosition = new Vector3(0f, -h + 0.62f, 0f);
        var bs = new UiScreen(bottom.transform) { BaseOrder = 10, MaxWidth = w * 2f };
        var time = new Label("", 1.3f) { Width = 1.45f, TextAlign = TextAlignmentOptions.Right };
        var play = new Label("", 1.25f) { TextAlign = TextAlignmentOptions.Center, Width = 0.8f };
        var speed = new Label("", 1.25f) { TextAlign = TextAlignmentOptions.Center, Width = 0.8f };
        var dir = new Label("", 1.25f) { TextAlign = TextAlignmentOptions.Center, Width = 0.8f };
        Button B(Widget content, Action a, float wd = 0.92f) => new(content, a) { Size = new Vector2(wd, 0.42f), Padding = new Vector2(0.04f, 0.04f), Fill = new Color(0f, 0f, 0f, 0.75f) };
        Label L(string key) => new(new Text(key), 1.25f) { TextAlign = TextAlignmentOptions.Center, Width = 0.84f, MinFontSize = 0.9f };
        _slider = new Slider(Math.Min(9.3f, w * 2f - 0.6f), 0f, v => { if (_r != null) { _t = v * _r.Duration; } return v; });
        var col = new Stack(false) { Gap = 0.06f };
        col.Add(_slider);
        col.Add(new Stack(true,
            B(L("Replay.First"), () => _t = 0f),
            B(L("Replay.PrevEvent"), () => JumpEvent(-1)),
            B(L("Replay.Back10"), () => Seek(-10f)),
            B(play, () => { if (_t >= _r.Duration && !_reverse) _t = 0f; _playing = !_playing; }),
            B(L("Replay.Fwd10"), () => Seek(10f)),
            B(L("Replay.NextEvent"), () => JumpEvent(1)),
            B(speed, () => _speed = (_speed + 1) % Speeds.Length),
            B(dir, () => { _reverse = !_reverse; _playing = true; }),
            B(L("Replay.Close"), Close),
            time) { Gap = 0.06f });
        bs.Set(col);
        _time = time.Built;
        _playLabel = play.Built;
        _speedLabel = speed.Built;
        _dirLabel = dir.Built;

        // 左上: 見出し・今の視点・会議中
        var top = Widget.Node("Top", _overlay.transform, OverlayLayer);
        top.transform.localPosition = new Vector3(-w + 0.2f, h - 0.2f, 0f);
        var ts = new UiScreen(top.transform) { Anchor = Align.Start, BaseOrder = 10, MaxWidth = 5f };
        var title = new Label(new Text("Replay.Title"), 2f) { MenuFont = true, Bold = true, Outline = 0.2f };
        var view = new Label("", 1.3f) { Width = 4.5f };
        var status = new Label("　", 1.5f) { Width = 4.5f }; // 空だと高さが 0 で並べられ、後から入る字が上の行に重なるので 1 行分を取っておく
        var help = new Label(new Text("Replay.StageHelp"), 1f) { Width = 4.5f, Color = new Color(0.8f, 0.8f, 0.8f, 1f) };
        ts.Set(new Box(new Stack(false, title, view, status, help) { Gap = 0.04f, Align = Align.Start }) { Padding = new Vector2(0.12f, 0.08f), Fill = new Color(0f, 0f, 0f, 0.55f), Hollow = true });
        _view = view.Built;
        _status = status.Built;

        // 右上: 出来事
        var right = Widget.Node("Log", _overlay.transform, OverlayLayer);
        right.transform.localPosition = new Vector3(w - 3.25f, h - 0.2f, 0f);
        var rs = new UiScreen(right.transform) { Anchor = Align.Start, BaseOrder = 10, MaxWidth = 3.1f };
        var log = new Label("", 1.05f) { Width = 3f, TextAlign = TextAlignmentOptions.TopLeft };
        rs.Set(new Box(log) { Size = new Vector2(3.1f, 1.75f), Padding = new Vector2(0.07f, 0.07f), Fill = new Color(0f, 0f, 0f, 0.55f), Hollow = true });
        _log = log.Built;
        if (_log) _log.rectTransform.sizeDelta = new Vector2(3f, 1.6f);
        _logShown = null;

        // 右: 人のボタン (押した人を追う・「自由」でカメラを自由に動かす)
        var people = Widget.Node("People", _overlay.transform, OverlayLayer);
        people.transform.localPosition = new Vector3(w - 1.75f, h - 2.15f, 0f);
        var ps = new UiScreen(people.transform) { Anchor = Align.Start, BaseOrder = 10, MaxWidth = 1.6f };
        var list = new Stack(false) { Gap = 0.03f, Align = Align.Start };
        list.Add(new Button(new Label(new Text("Replay.Free"), 1.1f) { TextAlign = TextAlignmentOptions.Center, Width = 1.45f }, () => SetFollow(-1))
        { Size = new Vector2(1.55f, 0.3f), Padding = new Vector2(0.03f, 0.02f), Fill = new Color(0f, 0f, 0f, 0.75f) });
        for (int i = 0; i < _r.Players.Count && i < 15; i++)
        {
            int idx = i;
            var p = _r.Players[i];
            string name = $"<color=#{ColorUtility.ToHtmlStringRGB(p.Color)}>●</color> {p.Name}";
            list.Add(new Button(new Label(name, 1.05f) { Width = 1.45f, NoWrap = true, MinFontSize = 0.7f }, () => SetFollow(idx))
            { Size = new Vector2(1.55f, 0.27f), Padding = new Vector2(0.03f, 0.02f), Fill = new Color(0f, 0f, 0f, 0.75f) });
        }
        ps.Set(list);

        // 会議の画面 (会議の間だけ・真ん中)
        _meetPanel = Widget.Node("Meeting", _overlay.transform, OverlayLayer);
        _meetPanel.transform.localPosition = new Vector3(MeetX, MeetY, -0.1f);
        var ms = new UiScreen(_meetPanel.transform) { BaseOrder = 30, MaxWidth = 8f };
        var mTitle = new Label("", 1.6f) { Width = MeetW - 0.3f, Bold = true, TextAlign = TextAlignmentOptions.Top };
        var mPeople = new Label("", 1.2f) { Width = 3.3f, TextAlign = TextAlignmentOptions.TopLeft };
        var mChat = new Label("", 1.05f) { Width = 2.8f, TextAlign = TextAlignmentOptions.TopLeft };
        ms.Set(new Box(new Stack(false, mTitle,
            new Stack(true, mPeople, mChat) { Gap = 0.2f, Align = Align.Start }) { Gap = 0.1f })
        { Size = new Vector2(MeetW, MeetH), Padding = new Vector2(0.15f, 0.12f), Fill = new Color(0.05f, 0.07f, 0.12f, 0.88f), Hollow = true });
        _meetTitle = mTitle.Built;
        _meetPeople = mPeople.Built;
        _meetChat = mChat.Built;
        // 字の欄の大きさは中身で変わるので、窓の上から決まった所へ置き直す (字は欄の上から並ぶ)
        var pt = _meetPanel.transform.position;
        void Pin(TextMeshPro t, float w, float h, float left, float top)
        {
            if (!t) return;
            t.rectTransform.sizeDelta = new Vector2(w, h);
            t.transform.position = new Vector3(pt.x - MeetW / 2f + left + w / 2f, pt.y + MeetH / 2f - top - h / 2f, t.transform.position.z);
        }
        Pin(_meetTitle, MeetW - 0.3f, 0.4f, 0.15f, 0.12f);
        Pin(_meetPeople, 3.3f, MeetH - 0.7f, 0.2f, 0.6f);
        Pin(_meetChat, 2.8f, MeetH - 0.7f, 3.65f, 0.6f);
        _meetShown = null;
        _meetPanel.SetActive(false);

        // 読み込み中
        ShowFrameUi();
    }

    private static void SetFollow(int i)
    {
        if (i < 0 && _follow >= 0 && _cam) _free = (Vector2)(_cam.transform.position - Origin);
        _follow = i;
    }

    private static void Seek(float dt) => _t = Mathf.Clamp(_t + dt, 0f, _r.Duration);

    private static void JumpEvent(int dir)
    {
        var evs = _r.Events;
        if (dir > 0)
        {
            foreach (var e in evs) if (e.T > _t + 0.6f) { _t = Mathf.Max(0f, e.T - 1.5f); Focus(e); return; }
            _t = _r.Duration;
            return;
        }
        for (int i = evs.Count - 1; i >= 0; i--) if (evs[i].T - 1.5f < _t - 0.6f) { _t = Mathf.Max(0f, evs[i].T - 1.5f); Focus(evs[i]); return; }
        _t = 0f;
    }

    // 出来事へ飛んだらその人を追う (キル = 倒した人・会議 = 開いた人)
    private static void Focus(MatchReplay.Ev e)
    {
        byte who = e.Kind == MatchReplay.EvKind.Kill && e.A == 255 ? e.B : e.A;
        if (_r.Index.TryGetValue(who, out int i)) _follow = i;
    }

    // ── 毎フレーム ──

    internal static void Tick()
    {
        var r = _r;
        if (r == null) return;
        if (!_root || !_cam || !_overlay) { Close(); return; }
        // ロビーで開いたまま試合が始まったら閉じる (ロビーと試合は同じ場面なので部品は壊れず、カメラが舞台に残って真っ暗になる)
        if (IntroCutscene.Instance || (AmongUsClient.Instance && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started && ShipStatus.Instance))
        { Close(); return; }
        if (!_built)
        {
            GameObject prefab = _ref.Asset ? _ref.Asset.TryCast<GameObject>() : null;
            if (!prefab && _opValid && _op.IsDone) prefab = _op.Result;
            if (!prefab)
            {
                _wait += Time.deltaTime;
                if ((_opValid && _op.IsDone) || _wait > 30f) { Plugin.Logger.LogWarning("[replay] ship failed to load"); Close(); ReplayView.Open(); return; }
                if (_status) _status.text = new Text("Replay.Loading");
                return;
            }
            try { BuildShip(prefab); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[replay] ship: {e}"); }
            try { BuildActors(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[replay] actors: {e}"); _built = true; }
            _playing = true;
        }
        if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
        if (Input.GetKeyDown(KeyCode.Space)) _playing = !_playing;
        if (Input.GetKeyDown(KeyCode.LeftArrow)) Seek(-5f);
        if (Input.GetKeyDown(KeyCode.RightArrow)) Seek(5f);
        if (Input.GetKeyDown(KeyCode.Tab)) _follow = (_follow + 1) % r.Players.Count;
        float wheel = Input.mouseScrollDelta.y;
        if (Math.Abs(wheel) > 0.01f) _zoom = Mathf.Clamp(_zoom - wheel * 0.5f, 1.5f, 9f);
        _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, _zoom, 0.25f);
        if (_overlay) _overlay.transform.localScale = Vector3.one * (_cam.orthographicSize / _overlayH);

        if (_playing) _t += Time.deltaTime * Speeds[_speed] * (_reverse ? -1f : 1f);
        if (_t >= r.Duration && !_reverse) { _t = r.Duration; _playing = false; }
        if (_t <= 0f && _reverse) { _t = 0f; _playing = false; }
        _t = Mathf.Clamp(_t, 0f, r.Duration);

        ShowFrame();
        ShowFrameUi();
    }

    private static void Sample(int i, float t, out Vector2 pos, out byte f, out Vector2 vel)
    {
        var r = _r;
        int hi = r.T.BinarySearch(t);
        if (hi < 0) hi = ~hi;
        hi = Mathf.Clamp(hi, 0, r.T.Count - 1);
        int lo = Mathf.Max(0, hi - 1);
        float dt = r.T[hi] - r.T[lo];
        float k = dt > 0f ? Mathf.Clamp01((t - r.T[lo]) / dt) : 1f;
        int near = k < 0.5f ? lo : hi;
        f = r.F[near][i];
        Vector2 a = new(r.X[lo][i], r.Y[lo][i]), b = new(r.X[hi][i], r.Y[hi][i]);
        bool jump = (b - a).sqrMagnitude > 4f;
        pos = jump ? (near == lo ? a : b) : Vector2.Lerp(a, b, k);
        vel = !jump && dt > 0f ? (b - a) / dt : Vector2.zero;
    }

    private static void ShowFrame()
    {
        var r = _r;
        if (!_built || _actors == null) return;
        float t = _t;
        float animSpeed = Speeds[_speed];
        Vector2 focus = _free;
        for (int i = 0; i < _actors.Length; i++)
        {
            var a = _actors[i];
            if (a.Pp == null || !a.Pp) continue;
            Sample(i, t, out var pos, out byte f, out var vel);
            bool gone = (f & 4) != 0, dead = (f & 1) != 0, vent = (f & 2) != 0;
            bool shown = !gone && !vent;
            if (shown != a.Shown) { a.Pp.gameObject.SetActive(shown); if (a.Label) a.Label.gameObject.SetActive(shown); a.Shown = shown; }
            if (i == _follow) focus = pos + r.BodyOffset;
            if (!shown) continue;
            a.Pp.transform.localPosition = new Vector3(pos.x + a.Fix.x, pos.y + a.Fix.y, pos.y / 1000f - 0.0005f);
            if (a.Label)
            {
                a.Label.transform.localPosition = new Vector3(pos.x + r.BodyOffset.x, pos.y + r.BodyOffset.y + r.BodyHeight * 0.68f, -0.5f);
                a.Label.alpha = dead ? 0.6f : 1f;
            }

            // 向き (逆再生でも歩いた向きを向く)
            if (Math.Abs(vel.x) > 0.05f)
            {
                bool left = vel.x < 0f;
                if (left != a.Left) { a.Left = left; try { a.Pp.SetFlipX(left); } catch { } }
            }
            // 幽霊
            if (dead != a.Ghost)
            {
                a.Ghost = dead;
                try
                {
                    if (dead) a.Pp.SetBodyAsGhost();
                    else
                    {
                        var p = r.Players[i];
                        var o = new NetworkedPlayerInfo.PlayerOutfit { PlayerName = p.Name, ColorId = p.ColorId, HatId = p.HatId, VisorId = p.VisorId, SkinId = p.SkinId, PetId = "", NamePlateId = "" };
                        a.Pp.UpdateFromPlayerOutfit(o, PlayerMaterial.MaskType.None, false, false);
                        a.Pp.SetFlipX(a.Left);
                    }
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"replay ghost: {e.Message}"); }
                a.Clip = null;
            }
            // 歩き・止まり・幽霊のアニメ
            bool running = !dead && vel.sqrMagnitude > 0.25f && _playing;
            var clip = dead ? MatchReplay.GhostClip : running ? MatchReplay.RunClip : MatchReplay.IdleClip;
            if (a.Body && clip && clip != a.Clip)
            {
                try { a.Body.Play(clip, animSpeed); } catch { }
                a.Clip = clip;
                if (!dead && a.Pp.cosmetics)
                {
                    try { if (running) a.Pp.cosmetics.AnimateSkinRun(); else a.Pp.cosmetics.AnimateSkinIdle(); } catch { }
                }
            }
            if (a.Body) a.Body.Speed = _playing ? animSpeed : 0f;
            a.Running = running;
        }

        // 死体: キルから次の会議まで
        for (int i = 0; i < _actors.Length; i++) if (_actors[i].DeadBody) _actors[i].DeadBody.SetActive(false);
        foreach (var e in r.Events)
        {
            if (e.T > t) break;
            if (e.Kind != MatchReplay.EvKind.Kill || !r.Index.TryGetValue(e.B, out int bi)) continue;
            var db = _actors[bi].DeadBody;
            if (!db) continue;
            bool cleared = false;
            foreach (var m in r.Events) if (m.Kind == MatchReplay.EvKind.Meeting && m.T >= e.T && m.T <= t) { cleared = true; break; }
            if (cleared) continue;
            db.SetActive(true);
            db.transform.localPosition = new Vector3(e.Where.x, e.Where.y + 0.05f, e.Where.y / 1000f);
        }

        // カメラ
        if (_follow < 0)
        {
            var move = new Vector2((Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0), (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0));
            _free += move * Time.deltaTime * 6f * (_zoom / 3f);
            focus = _free;
        }
        else _free = focus;
        var cp = _cam.transform.position;
        var want = new Vector3(Origin.x + focus.x, Origin.y + focus.y, cp.z);
        _cam.transform.position = (want - cp).sqrMagnitude > 25f ? want : Vector3.Lerp(cp, want, 0.2f);
    }

    private static string Clock(float s)
    {
        int v = Mathf.Max(0, Mathf.FloorToInt(s));
        return $"{v / 60}:{v % 60:00}";
    }

    private static string Name(byte id)
    {
        if (id == 255 || !_r.Index.TryGetValue(id, out int i)) return "?";
        var p = _r.Players[i];
        return $"<color=#{ColorUtility.ToHtmlStringRGB(p.Color)}>{p.Name}</color>";
    }

    private static string Describe(MatchReplay.Ev e) => e.Kind switch
    {
        MatchReplay.EvKind.Kill when e.A == 255 => new Text("Replay.EvDied").With("victim", Name(e.B)),
        MatchReplay.EvKind.Kill => new Text("Replay.EvKill").With("killer", Name(e.A)).With("victim", Name(e.B)),
        MatchReplay.EvKind.Meeting when e.B == 255 => new Text("Replay.EvButton").With("who", Name(e.A)),
        MatchReplay.EvKind.Meeting => new Text("Replay.EvReport").With("who", Name(e.A)).With("victim", Name(e.B)),
        MatchReplay.EvKind.Votes when e.A == 255 => new Text(e.Tie ? "Replay.EvTie" : "Replay.EvNoExile"),
        MatchReplay.EvKind.Votes => new Text("Replay.EvVotedOut").With("who", Name(e.A)),
        _ => new Text("Replay.EvExile").With("who", Name(e.A)),
    };

    private static void ShowFrameUi()
    {
        var r = _r;
        if (r == null) return;
        float t = _t;
        var lines = new List<string>();
        foreach (var e in r.Events)
        {
            if (e.T > t) break;
            if (e.Kind == MatchReplay.EvKind.Votes && e.A != 255) continue; // 追放は追放の出来事で出す
            lines.Add($"<color=#BBBBBB>{Clock(e.T)}</color> {Describe(e)}");
        }
        var sb = new StringBuilder();
        for (int i = Mathf.Max(0, lines.Count - 6); i < lines.Count; i++) sb.Append(lines[i]).Append('\n');
        if (lines.Count == 0) sb.Append(new Text("Replay.NoEvents").ToString());
        string log = sb.ToString();
        if (_log && log != _logShown) { _log.text = log; _logShown = log; }

        if (_time) _time.text = $"{Clock(t)} / {Clock(r.Duration)}";
        if (_playLabel) _playLabel.text = _playing ? new Text("Replay.Pause") : new Text("Replay.Play");
        if (_speedLabel) _speedLabel.text = $"×{Speeds[_speed]:0}";
        if (_dirLabel) _dirLabel.text = _reverse ? new Text("Replay.Backward") : new Text("Replay.Forward");
        if (_view)
        {
            string v = _follow >= 0 && _follow < r.Players.Count
                ? new Text("Replay.Following").With("name", Name(r.Players[_follow].Id)).ToString() + (r.Players[_follow].Role != null ? $" ({r.Players[_follow].Role})" : "")
                : new Text("Replay.FreeCam").ToString();
            _view.text = v;
        }
        if (_status && _built)
        {
            int hi = r.T.BinarySearch(t);
            if (hi < 0) hi = ~hi;
            hi = Mathf.Clamp(hi, 0, r.T.Count - 1);
            _status.text = r.Meeting[hi] ? new Text("Replay.InMeeting").ToString() : "　";
        }
        _slider?.Show(r.Duration > 0f ? t / r.Duration : 0f);
        ShowMeeting(t);
    }

    // 今の時刻を含む会議 (会議の出来事から、会議中の印が消えるまで)
    private static MatchReplay.Ev MeetingAt(float t, out float end)
    {
        var r = _r;
        end = 0f;
        MatchReplay.Ev cur = null;
        foreach (var e in r.Events)
        {
            if (e.T > t) break;
            if (e.Kind == MatchReplay.EvKind.Meeting) cur = e;
        }
        if (cur == null) return null;
        int k = r.T.BinarySearch(cur.T);
        if (k < 0) k = ~k;
        end = r.Duration;
        for (int i = k; i < r.T.Count; i++)
            if (r.T[i] > cur.T + 1f && !r.Meeting[i]) { end = r.T[i]; break; }
        return t < end || end >= r.Duration ? cur : null;
    }

    private static void ShowMeeting(float t)
    {
        var r = _r;
        if (!_meetPanel) return;
        var m = _built ? MeetingAt(t, out float end) : null;
        if (m == null) { if (_meetPanel.activeSelf) _meetPanel.SetActive(false); return; }
        if (!_meetPanel.activeSelf) _meetPanel.SetActive(true);
        end = 0f;

        // 投票の結果 (この会議の中で、もう出ていれば)
        MatchReplay.Ev votes = null;
        foreach (var e in r.Events)
        {
            if (e.T > t) break;
            if (e.T >= m.T && e.Kind == MatchReplay.EvKind.Votes) votes = e;
        }
        int startIdx = r.T.BinarySearch(m.T);
        if (startIdx < 0) startIdx = ~startIdx;
        startIdx = Mathf.Clamp(startIdx, 0, r.T.Count - 1);
        int nowIdx = r.T.BinarySearch(t);
        if (nowIdx < 0) nowIdx = ~nowIdx;
        nowIdx = Mathf.Clamp(nowIdx, 0, r.T.Count - 1);

        string title = (m.B == 255 ? new Text("Replay.EvButton").With("who", Name(m.A)) : new Text("Replay.EvReport").With("who", Name(m.A)).With("victim", Name(m.B))).ToString();
        if (votes != null)
            title += "\n<size=80%>" + (votes.A == 255 ? new Text(votes.Tie ? "Replay.EvTie" : "Replay.EvNoExile").ToString() : new Text("Replay.EvVotedOut").With("who", Name(votes.A)).ToString()) + "</size>";

        var people = new StringBuilder();
        for (int i = 0; i < r.Players.Count; i++)
        {
            var p = r.Players[i];
            byte f0 = r.F[startIdx][i], fn = r.F[nowIdx][i];
            if ((f0 & 4) != 0) continue;
            bool dead = (f0 & 1) != 0;
            string dot = $"<color=#{ColorUtility.ToHtmlStringRGB(p.Color)}>●</color>";
            if (dead) { people.Append($"<color=#777777>{dot} {p.Name} {new Text("Replay.MeetDead")}</color>\n"); continue; }
            people.Append(dot).Append(' ').Append(p.Name);
            if (votes != null)
            {
                byte to = 254;
                foreach (var (voter, target) in votes.Votes) if (voter == p.Id) { to = target; break; }
                string tgt = to == 254 ? new Text("Replay.MeetNoVote").ToString() : to >= 252 ? new Text("Replay.MeetSkip").ToString() : Name(to);
                people.Append(" <color=#BBBBBB>→</color> ").Append(tgt);
                if (votes.A == p.Id) people.Append(" <color=#FF6666>").Append(new Text("Replay.MeetExiled").ToString()).Append("</color>");
            }
            else if ((fn & MatchReplay.FVoted) != 0) people.Append(" <color=#7CFC9A>").Append(new Text("Replay.MeetVoted").ToString()).Append("</color>");
            people.Append('\n');
        }

        var chat = new List<string>();
        foreach (var c in r.Chats)
        {
            if (c.T > t) break;
            if (c.T < m.T) continue;
            chat.Add($"{Name(c.Who)}: {Esc(c.Text)}");
        }
        var cs = new StringBuilder();
        cs.Append("<color=#BBBBBB>").Append(new Text("Replay.MeetChat").ToString()).Append("</color>\n");
        for (int i = Mathf.Max(0, chat.Count - 9); i < chat.Count; i++) cs.Append(chat[i]).Append('\n');

        string key = title + "|" + people + "|" + cs;
        if (key == _meetShown) return;
        _meetShown = key;
        if (_meetTitle) _meetTitle.text = title;
        if (_meetPeople) _meetPeople.text = people.ToString();
        if (_meetChat) _meetChat.text = cs.ToString();
    }

    // チャットの文の中のタグを効かせない
    private static string Esc(string s) => s.Replace("<", "<​");
}
