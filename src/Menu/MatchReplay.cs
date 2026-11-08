using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 試合のリプレイ。試合中は全員の位置・生死・ベントの中か・会議中かを 0.2 秒ごとに控え、キル・通報・追放を出来事として控える
// (この端末に見えていた物を手元に控えるだけで、通信はしない)。
// 試合が終わったら前の試合として残し、終わりの画面の「リプレイ」かロビーの /replay で、地図の上に全員を動かして見せる。
// 再生・止める・最初へ・10 秒戻す/進める・速さ (×1〜×8)・逆向き・つまみで好きな時刻へ。キーは Space = 再生/止める・← → = 5 秒。
// 地図の絵は試合の始めに本編の地図 (MapPrefab の Background) を写し取っておく (船が消えた後も見せるため)
internal static class MatchReplay
{
    private const float SampleEvery = 0.2f;

    private const byte FDead = 1, FVent = 2, FGone = 4;

    internal enum EvKind : byte { Kill, Meeting, Exile }

    internal sealed class Ev
    {
        public float T;
        public EvKind Kind;
        public byte A = 255, B = 255; // キル = 倒した人・倒された人 / 会議 = 開いた人・通報された死体 (255 = 緊急ボタン) / 追放 = 追放された人
        public Vector2 Where;
    }

    internal sealed class Player
    {
        public byte Id;
        public string Name;
        public int ColorId;
        public Color Color;
        public string Role;   // 試合の終わりの役職 (色付き)
        public bool Won;
    }

    internal sealed class Record
    {
        public readonly List<Player> Players = new();
        public readonly Dictionary<byte, int> Index = new();
        public readonly List<float> T = new();
        public readonly List<bool> Meeting = new();
        public readonly List<float[]> X = new(), Y = new();
        public readonly List<byte[]> F = new();
        public readonly List<Ev> Events = new();
        public float Start;
        public float Duration => T.Count > 0 ? T[^1] : 0f;
        public Sprite Map;
        public Color MapColor = Color.white;
        public Rect MapRect; // 地図の絵が覆う範囲 (地図の中の座標 = 世界の座標 / MapScale・左右反転込み)
        public float MapScale = 1f, Flip = 1f;
        public string Winners;
        public bool Painted;
    }

    private static Record _rec;
    private static float _nextSample;

    internal static Record Last { get; private set; }

    // ── 記録 ──

    internal static void Begin()
    {
        var ship = ShipStatus.Instance;
        var data = GameData.Instance;
        if (!ship || !data) { _rec = null; return; }
        var r = new Record { Start = Time.time, MapScale = ship.MapScale > 0f ? ship.MapScale : 1f, Flip = Mathf.Sign(ship.transform.localScale.x) };
        AddPlayers(r, data);
        try { CaptureMap(ship, r); }
        catch (Exception e) { Plugin.Logger.LogWarning($"replay map: {e.Message}"); }
        _rec = r;
        _nextSample = 0f;
        Sample(true);
    }

    // 始めにいた人と、途中から入った人 (前の控えには「いない」として足す)
    private static void AddPlayers(Record r, GameData data)
    {
        foreach (var p in data.AllPlayers)
        {
            if (p == null || r.Index.ContainsKey(p.PlayerId)) continue;
            for (int s = 0; s < r.T.Count; s++)
            {
                int m = r.X[s].Length;
                var xs = new float[m + 1]; var ys = new float[m + 1]; var fs = new byte[m + 1];
                Array.Copy(r.X[s], xs, m); Array.Copy(r.Y[s], ys, m); Array.Copy(r.F[s], fs, m);
                fs[m] = FGone;
                r.X[s] = xs; r.Y[s] = ys; r.F[s] = fs;
            }
            int color = p.DefaultOutfit != null ? p.DefaultOutfit.ColorId : 0;
            r.Index[p.PlayerId] = r.Players.Count;
            r.Players.Add(new Player
            {
                Id = p.PlayerId,
                Name = p.DefaultOutfit != null ? p.DefaultOutfit.PlayerName : p.PlayerName,
                ColorId = color,
                Color = color >= 0 && color < Palette.PlayerColors.Length ? (Color)Palette.PlayerColors[color] : Color.white,
            });
        }
    }

    private static void CaptureMap(ShipStatus ship, Record r)
    {
        var prefab = ship.MapPrefab;
        if (!prefab) return;
        var bg = prefab.transform.Find("Background");
        var sr = bg ? bg.GetComponent<SpriteRenderer>() : null;
        if (!sr || !sr.sprite) return;
        var parent = prefab.HerePoint ? prefab.HerePoint.transform.parent : prefab.transform;
        var s = sr.sprite;
        var b = s.bounds;
        var toParent = parent.worldToLocalMatrix * bg.localToWorldMatrix;
        Vector3 c0 = toParent.MultiplyPoint3x4(b.min), c1 = toParent.MultiplyPoint3x4(b.max);
        r.MapRect = Rect.MinMaxRect(Math.Min(c0.x, c1.x), Math.Min(c0.y, c1.y), Math.Max(c0.x, c1.x), Math.Max(c0.y, c1.y));
        r.MapColor = sr.color;

        // 地図の絵を自前の絵へ写す (本編の地図の資源は船と一緒に片付けられることがあるので)
        var tex = s.texture;
        var tr = s.textureRect;
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
        var prev = RenderTexture.active;
        try
        {
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            int w = Mathf.Max(1, (int)tr.width), h = Mathf.Max(1, (int)tr.height);
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            copy.ReadPixels(new Rect(tr.x, tr.y, w, h), 0, 0);
            copy.Apply(false, false);
            if (Last?.Map && Last.Map.texture) Object.Destroy(Last.Map.texture);
            r.Map = Sprite.Create(copy, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            r.Map.hideFlags = HideFlags.HideAndDontSave;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    // 本編の地図の絵は部屋の形 (赤) だけで、色と縁は材質が付ける。見る時に 1 回だけ、部屋の塗りと縁の色に描き直す
    internal static void Paint(Record r)
    {
        if (r.Painted || !r.Map || !r.Map.texture) return;
        r.Painted = true;
        var tex = r.Map.texture;
        int w = tex.width, h = tex.height;
        var src = tex.GetPixels32();
        int n = w * h;
        var inside = new bool[n];
        for (int i = 0; i < n; i++) { var c = src[i]; inside[i] = c.a > 100 && c.r > 110; }
        var fill = new Color32(38, 72, 118, 255);
        var edge = new Color32(150, 200, 245, 255);
        var clear = new Color32(0, 0, 0, 0);
        int k = Math.Max(1, Math.Min(w, h) / 220); // 縁の太さ
        var outp = new Color32[n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!inside[i]) { outp[i] = clear; continue; }
                bool border = false;
                for (int dy = -k; dy <= k && !border; dy++)
                    for (int dx = -k; dx <= k; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h || !inside[yy * w + xx]) { border = true; break; }
                    }
                outp[i] = border ? edge : fill;
            }
        tex.SetPixels32(outp);
        tex.Apply(false, false);
        r.MapColor = Color.white;
    }

    private static void Sample(bool force)
    {
        var r = _rec;
        if (r == null) return;
        float now = Time.time;
        if (!force && now < _nextSample) return;
        _nextSample = now + SampleEvery;
        var data = GameData.Instance;
        if (data && data.AllPlayers.Count > r.Players.Count) AddPlayers(r, data);
        int n = r.Players.Count;
        var xs = new float[n];
        var ys = new float[n];
        var fs = new byte[n];
        for (int i = 0; i < n; i++)
        {
            var info = data ? data.GetPlayerById(r.Players[i].Id) : null;
            var pc = info != null ? info.Object : null;
            if (info == null || info.Disconnected || !pc)
            {
                fs[i] = FGone;
                if (r.X.Count > 0) { xs[i] = r.X[^1][i]; ys[i] = r.Y[^1][i]; }
                continue;
            }
            var pos = pc.GetTruePosition();
            xs[i] = pos.x;
            ys[i] = pos.y;
            byte f = 0;
            if (info.IsDead) f |= FDead;
            if (pc.inVent) f |= FVent;
            fs[i] = f;
        }
        r.T.Add(now - r.Start);
        r.Meeting.Add(MeetingHud.Instance || ExileController.Instance);
        r.X.Add(xs);
        r.Y.Add(ys);
        r.F.Add(fs);
    }

    internal static void OnKill(PlayerControl killer, PlayerControl target)
    {
        var r = _rec;
        if (r == null || !target) return;
        bool self = !killer || killer.PlayerId == target.PlayerId;
        r.Events.Add(new Ev { T = Time.time - r.Start, Kind = EvKind.Kill, A = self ? (byte)255 : killer.PlayerId, B = target.PlayerId, Where = target.GetTruePosition() });
    }

    internal static void OnMeeting(PlayerControl reporter, NetworkedPlayerInfo body)
    {
        var r = _rec;
        if (r == null || !reporter) return;
        r.Events.Add(new Ev { T = Time.time - r.Start, Kind = EvKind.Meeting, A = reporter.PlayerId, B = body != null ? body.PlayerId : (byte)255 });
    }

    internal static void OnExiled(PlayerControl p)
    {
        var r = _rec;
        if (r == null || !p || !MeetingHud.Instance && !ExileController.Instance) return;
        r.Events.Add(new Ev { T = Time.time - r.Start, Kind = EvKind.Exile, A = p.PlayerId });
    }

    // 試合の終わり (役職がまだ残っている間)。役職と勝ち負けを付けて前の試合として残す
    internal static void NoteResult(byte pid, string role, bool won)
    {
        var r = _rec;
        if (r == null || !r.Index.TryGetValue(pid, out int i)) return;
        r.Players[i].Role = role;
        r.Players[i].Won = won;
    }

    internal static void Finish(string winners)
    {
        var r = _rec;
        if (r == null) return;
        Sample(true);
        r.Winners = winners;
        _rec = null;
        if (r.T.Count < 2) return;
        if (Last != null && Last != r && Last.Map && Last.Map != r.Map && Last.Map.texture) Object.Destroy(Last.Map.texture);
        Last = r;
        Plugin.Logger.LogInfo($"[replay] saved {r.Players.Count} players, {r.T.Count} samples, {r.Events.Count} events, {r.Duration:0.0}s, map={(r.Map ? "yes" : "no")}");
    }

    internal static void Tick()
    {
        if (_rec != null)
        {
            if (!ShipStatus.Instance) { if (!AmongUsClient.Instance || AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Ended) _rec = null; }
            else Sample(false);
        }
        ReplayView.Tick();
    }

    // チャットの /replay
    internal static string Command(string _)
    {
        if (ShipStatus.Instance && AmongUsClient.Instance && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started)
            return new Text("Replay.NotNow");
        if (Last == null) return new Text("Replay.None");
        return ReplayView.Open() ? new Text("Replay.Opened") : new Text("Replay.None");
    }

    // 終わりの画面の右上に「リプレイ」のボタンを置く
    internal static void EndButton(EndGameManager egm)
    {
        if (Last == null || !egm) return;
        var cam = Camera.main;
        if (!cam) return;
        float h = cam.orthographicSize, w = h * cam.aspect;
        var c = cam.transform.position;
        var node = new GameObject("MrpReplayButton") { layer = 5 };
        node.transform.SetParent(egm.transform, false);
        node.transform.position = new Vector3(c.x + w - 1.1f, c.y + h - 1.05f, -20f);
        var screen = new UiScreen(node.transform) { BaseOrder = 200 };
        screen.Set(new Button(new Label(new Text("Replay.Button"), 1.8f) { TextAlign = TextAlignmentOptions.Center, Bold = true }, () => ReplayView.Open())
        {
            Size = new Vector2(1.9f, 0.5f),
        });
    }
}

// 試合の始め (イントロの後) から記録する
[HarmonyPatch(typeof(HudManager), nameof(HudManager.OnGameStart))]
internal static class MatchReplayStartPatch
{
    public static void Postfix()
    {
        try { MatchReplay.Begin(); }
        catch (Exception e) { Plugin.Logger.LogError($"replay start: {e}"); }
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
internal static class MatchReplayMeetingPatch
{
    public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] NetworkedPlayerInfo target)
    {
        try { MatchReplay.OnMeeting(__instance, target); }
        catch (Exception e) { Plugin.Logger.LogError($"replay meeting: {e}"); }
    }
}

// 地図の上に記録を動かして見せる窓
internal static class ReplayView
{
    private const float MapW = 6.6f, MapH = 3.75f, SideW = 2.85f, Dot = 0.2f;
    private static readonly float[] Speeds = { 1f, 2f, 4f, 8f };

    private static MatchReplay.Record _r;
    private static float _t;
    private static bool _playing, _reverse;
    private static int _speed;

    private static Transform _canvas;
    private static Vector2 _origin, _scale; // 地図の中の座標 → 窓の中の座標
    private static Transform[] _dots;
    private static SpriteRenderer[][] _dotSr;
    private static TextMeshPro[] _names;
    private static Transform[] _bodies;
    private static Slider _slider;
    private static TextMeshPro _time, _log, _playLabel, _speedLabel, _dirLabel, _status;
    private static string _logShown;

    private static bool IsOpen => UiWindow.Current != null && UiWindow.Current.Root && UiWindow.Current.Root.name == "MrpWindow_Replay";

    internal static bool Open()
    {
        var r = MatchReplay.Last;
        if (r == null) return false;
        var w = UiWindow.Open("Replay", new Vector2(10f, 5.7f), () => { _canvas = null; _r = null; });
        if (w == null) return false;
        _r = r;
        try { MatchReplay.Paint(r); }
        catch (Exception e) { Plugin.Logger.LogWarning($"replay paint: {e.Message}"); }
        _t = 0f;
        _playing = true;
        _reverse = false;
        _speed = 0;
        Build();
        return true;
    }

    private static void Build()
    {
        var w = UiWindow.Current;
        var r = _r;
        if (w == null || r == null) return;
        _logShown = null;
        var title = new Label(new Text("Replay.Title"), 2.2f) { MenuFont = true, Bold = true };
        var status = new Label("", 1.3f) { Width = 5.5f, TextAlign = TextAlignmentOptions.Right };
        var log = new Label("", 1.05f) { Width = SideW, TextAlign = TextAlignmentOptions.TopLeft };
        var canvas = new Canvas();
        _slider = new Slider(9.3f, 0f, v => { if (_r != null) { _t = v * _r.Duration; ShowFrame(); } return v; });
        var time = new Label("", 1.3f) { Width = 1.45f, TextAlign = TextAlignmentOptions.Right };
        var play = new Label("", 1.25f) { TextAlign = TextAlignmentOptions.Center, Width = 0.8f };
        var speed = new Label("", 1.25f) { TextAlign = TextAlignmentOptions.Center, Width = 0.8f };
        var dir = new Label("", 1.25f) { TextAlign = TextAlignmentOptions.Center, Width = 0.8f };
        Button B(Widget content, Action a, float wd = 0.92f) => new(content, a) { Size = new Vector2(wd, 0.42f), Padding = new Vector2(0.04f, 0.04f) };
        Label L(string key) => new(new Text(key), 1.25f) { TextAlign = TextAlignmentOptions.Center, Width = 0.84f, MinFontSize = 0.9f };

        var col = new Stack(false) { Gap = 0.08f, Width = 9.6f };
        col.Add(new Stack(true, title, new Spacer(0.3f, 0.1f), status) { Gap = 0.1f });
        col.Add(new Stack(true, canvas, new Box(log) { Size = new Vector2(SideW + 0.15f, MapH), Padding = new Vector2(0.07f, 0.07f) }) { Gap = 0.12f });
        col.Add(_slider);
        col.Add(new Stack(true,
            B(L("Replay.First"), () => { _t = 0f; ShowFrame(); }),
            B(L("Replay.PrevEvent"), () => JumpEvent(-1)),
            B(L("Replay.Back10"), () => Seek(-10f)),
            B(play, () => { if (_t >= _r.Duration && !_reverse) _t = 0f; _playing = !_playing; ShowFrame(); }),
            B(L("Replay.Fwd10"), () => Seek(10f)),
            B(L("Replay.NextEvent"), () => JumpEvent(1)),
            B(speed, () => { _speed = (_speed + 1) % Speeds.Length; ShowFrame(); }),
            B(dir, () => { _reverse = !_reverse; _playing = true; ShowFrame(); }),
            time) { Gap = 0.06f });
        w.Body.Set(col);
        _time = time.Built;
        _log = log.Built;
        _playLabel = play.Built;
        _speedLabel = speed.Built;
        _dirLabel = dir.Built;
        _status = status.Built;
        if (_log) _log.rectTransform.sizeDelta = new Vector2(SideW, MapH - 0.2f);
        ShowFrame();
    }

    private static void Seek(float dt)
    {
        if (_r == null) return;
        _t = Mathf.Clamp(_t + dt, 0f, _r.Duration);
        ShowFrame();
    }

    private static void JumpEvent(int dir)
    {
        if (_r == null) return;
        var evs = _r.Events;
        if (dir > 0)
        {
            foreach (var e in evs) if (e.T > _t + 0.6f) { _t = Mathf.Max(0f, e.T - 1.5f); ShowFrame(); return; }
            _t = _r.Duration;
        }
        else
        {
            for (int i = evs.Count - 1; i >= 0; i--) if (evs[i].T - 1.5f < _t - 0.6f) { _t = Mathf.Max(0f, evs[i].T - 1.5f); ShowFrame(); return; }
            _t = 0f;
        }
        ShowFrame();
    }

    internal static void Tick()
    {
        if (_r == null) return;
        if (!IsOpen || !_canvas) { _r = null; _canvas = null; return; }
        if (Input.GetKeyDown(KeyCode.Space)) _playing = !_playing;
        if (Input.GetKeyDown(KeyCode.LeftArrow)) _t -= 5f;
        if (Input.GetKeyDown(KeyCode.RightArrow)) _t += 5f;
        if (_playing) _t += Time.deltaTime * Speeds[_speed] * (_reverse ? -1f : 1f);
        if (_t >= _r.Duration && !_reverse) { _t = _r.Duration; _playing = false; }
        if (_t <= 0f && _reverse) { _t = 0f; _playing = false; }
        _t = Mathf.Clamp(_t, 0f, _r.Duration);
        ShowFrame();
    }

    private static string Clock(float s)
    {
        int v = Mathf.Max(0, Mathf.FloorToInt(s));
        return $"{v / 60}:{v % 60:00}";
    }

    private static string Name(byte id)
    {
        var r = _r;
        if (id == 255 || r == null || !r.Index.TryGetValue(id, out int i)) return "?";
        var p = r.Players[i];
        return $"<color=#{ColorUtility.ToHtmlStringRGB(Lift(p.Color))}>{p.Name}</color>";
    }

    // 黒っぽい色の名前を黒い地の上でも読めるように明るくする
    private static Color Lift(Color c)
    {
        float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        return m < 0.35f ? Color.Lerp(c, Color.white, 0.45f) : c;
    }

    private static string Describe(MatchReplay.Ev e) => e.Kind switch
    {
        MatchReplay.EvKind.Kill when e.A == 255 => new Text("Replay.EvDied").With("victim", Name(e.B)),
        MatchReplay.EvKind.Kill => new Text("Replay.EvKill").With("killer", Name(e.A)).With("victim", Name(e.B)),
        MatchReplay.EvKind.Meeting when e.B == 255 => new Text("Replay.EvButton").With("who", Name(e.A)),
        MatchReplay.EvKind.Meeting => new Text("Replay.EvReport").With("who", Name(e.A)).With("victim", Name(e.B)),
        _ => new Text("Replay.EvExile").With("who", Name(e.A)),
    };

    private static void ShowFrame()
    {
        var r = _r;
        if (r == null || !_canvas) return;
        float t = _t;
        // 今の時刻をはさむ 2 つの控え
        int hi = r.T.BinarySearch(t);
        if (hi < 0) hi = ~hi;
        hi = Mathf.Clamp(hi, 0, r.T.Count - 1);
        int lo = Mathf.Max(0, hi - 1);
        float k = r.T[hi] > r.T[lo] ? Mathf.Clamp01((t - r.T[lo]) / (r.T[hi] - r.T[lo])) : 1f;
        int near = k < 0.5f ? lo : hi;
        for (int i = 0; i < r.Players.Count; i++)
        {
            var dot = _dots[i];
            if (!dot) continue;
            byte f = r.F[near][i];
            bool gone = (f & 4) != 0;
            dot.gameObject.SetActive(!gone);
            if (gone) continue;
            // 大きく飛んだ (ベント・転送) 時はなめらかに動かさない
            float x0 = r.X[lo][i], y0 = r.Y[lo][i], x1 = r.X[hi][i], y1 = r.Y[hi][i];
            bool jump = (x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) > 4f;
            float x = jump ? (near == lo ? x0 : x1) : Mathf.Lerp(x0, x1, k);
            float y = jump ? (near == lo ? y0 : y1) : Mathf.Lerp(y0, y1, k);
            dot.localPosition = new Vector3(ToX(x), ToY(y), -0.05f - i * 0.001f);
            bool dead = (f & 1) != 0, vent = (f & 2) != 0;
            float a = dead ? 0.35f : vent ? 0.55f : 1f;
            foreach (var sr in _dotSr[i]) { var c = sr.color; c.a = a; sr.color = c; }
            if (_names[i]) _names[i].alpha = dead ? 0.45f : 1f;
        }

        // 死体: キルから次の会議まで
        var sb = new StringBuilder();
        int shownLines = 0;
        for (int i = 0; i < _bodies.Length; i++) if (_bodies[i]) _bodies[i].gameObject.SetActive(false);
        var lines = new List<string>();
        foreach (var e in r.Events)
        {
            if (e.T > t) break;
            lines.Add($"<color=#BBBBBB>{Clock(e.T)}</color> {Describe(e)}");
            if (e.Kind != MatchReplay.EvKind.Kill || !r.Index.TryGetValue(e.B, out int bi) || !_bodies[bi]) continue;
            bool cleared = false;
            foreach (var m in r.Events) if (m.Kind == MatchReplay.EvKind.Meeting && m.T >= e.T && m.T <= t) { cleared = true; break; }
            if (cleared) continue;
            _bodies[bi].gameObject.SetActive(true);
            _bodies[bi].localPosition = new Vector3(ToX(e.Where.x), ToY(e.Where.y), -0.03f);
        }
        for (int i = Mathf.Max(0, lines.Count - 13); i < lines.Count; i++) { sb.Append(lines[i]).Append('\n'); shownLines++; }
        if (shownLines == 0) sb.Append(new Text("Replay.NoEvents").ToString());
        string log = sb.ToString();
        if (_log && log != _logShown) { _log.text = log; _logShown = log; }

        if (_time) _time.text = $"{Clock(t)} / {Clock(r.Duration)}";
        if (_playLabel) _playLabel.text = _playing ? new Text("Replay.Pause") : new Text("Replay.Play");
        if (_speedLabel) _speedLabel.text = $"×{Speeds[_speed]:0}";
        if (_dirLabel) _dirLabel.text = _reverse ? new Text("Replay.Backward") : new Text("Replay.Forward");
        if (_status) _status.text = r.Meeting[near] ? new Text("Replay.InMeeting").ToString() : "";
        _slider?.Show(r.Duration > 0f ? t / r.Duration : 0f);
    }

    private static float ToX(float worldX) => _origin.x + worldX / _r.MapScale * _r.Flip * _scale.x;
    private static float ToY(float worldY) => _origin.y + worldY / _r.MapScale * _scale.y;

    // 地図と人の印
    private sealed class Canvas : Widget
    {
        public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
        {
            var r = _r;
            var go = Node("ReplayCanvas", parent, ctx.Layer);
            size = new Vector2(MapW, MapH);
            _canvas = go.transform;

            // 下敷き
            var back = Node("Back", go.transform, ctx.Layer).AddComponent<SpriteRenderer>();
            back.sprite = UiAssets.Blank;
            back.color = new Color(0.05f, 0.06f, 0.09f, 1f);
            var bb = UiAssets.Blank ? UiAssets.Blank.bounds.size : Vector3.one;
            back.transform.localScale = new Vector3(MapW / bb.x, MapH / bb.y, 1f);

            // 地図の範囲 (絵が無ければ記録した位置の広がり)
            Rect area = r.MapRect;
            if (!r.Map || area.width <= 0f || area.height <= 0f)
            {
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                for (int s = 0; s < r.X.Count; s++)
                    for (int i = 0; i < r.Players.Count; i++)
                    {
                        if ((r.F[s][i] & 4) != 0) continue;
                        float x = r.X[s][i] / r.MapScale * r.Flip, y = r.Y[s][i] / r.MapScale;
                        x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                    }
                if (x1 < x0) { x0 = -1f; x1 = 1f; y0 = -1f; y1 = 1f; }
                area = Rect.MinMaxRect(x0 - 0.3f, y0 - 0.3f, x1 + 0.3f, y1 + 0.3f);
            }
            float fit = Math.Min((MapW - 0.1f) / area.width, (MapH - 0.1f) / area.height);
            _scale = new Vector2(fit, fit);
            _origin = new Vector2(-area.center.x * fit, -area.center.y * fit);

            if (r.Map)
            {
                var map = Node("Map", go.transform, ctx.Layer).AddComponent<SpriteRenderer>();
                map.sprite = r.Map;
                map.color = r.MapColor;
                var ms = r.Map.bounds.size;
                map.transform.localScale = new Vector3(area.width * fit / ms.x, area.height * fit / ms.y, 1f);
                map.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            }

            // 死体の印 (×)
            int n = r.Players.Count;
            _bodies = new Transform[n];
            var bodies = Node("Bodies", go.transform, ctx.Layer);
            for (int i = 0; i < n; i++)
            {
                var x = new Label("×", 2.4f) { Color = new Color(1f, 0.25f, 0.25f, 1f), Bold = true, TextAlign = TextAlignmentOptions.Center, NoWrap = true }
                    .Build(bodies.transform, ctx, 1f, out _);
                x.SetActive(false);
                _bodies[i] = x.transform;
            }

            // 人の印 = 色の丸 (黒い縁) と名前
            _dots = new Transform[n];
            _dotSr = new SpriteRenderer[n][];
            _names = new TextMeshPro[n];
            var dots = Node("Dots", go.transform, ctx.Layer);
            var circle = UiAssets.Circle;
            for (int i = 0; i < n; i++)
            {
                var p = r.Players[i];
                var d = Node("P" + p.Id, dots.transform, ctx.Layer).transform;
                var rim = Slider.Disc(d, "Rim", circle, Dot, Color.black, ctx.Layer);
                var face = Slider.Disc(rim.transform, "Face", circle, 0.78f, p.Color, ctx.Layer);
                var nameLabel = new Label(p.Name, 0.9f) { Color = Color.white, NoWrap = true, TextAlign = TextAlignmentOptions.Center, Outline = 0.2f };
                var ng = nameLabel.Build(d, ctx, 2f, out _);
                ng.transform.localPosition = new Vector3(0f, Dot * 0.9f, -0.02f);
                if (nameLabel.Built) { nameLabel.Built.outlineWidth = 0.25f; nameLabel.Built.outlineColor = Color.black; }
                _dots[i] = d;
                _dotSr[i] = new[] { rim, face };
                _names[i] = nameLabel.Built;
            }
            return go;
        }
    }
}
