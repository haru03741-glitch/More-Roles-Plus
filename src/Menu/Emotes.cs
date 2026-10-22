using System;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Roles;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// ロビーと試合中のエモート (手を振る・跳ねる・拍手・手招き)。右下の「エモート」ボタンで 4 つのボタンを扇形に開いて選ぶ。
// 扇を開いている間は、扇に掛かりうる周りの UI (右下の並びの他のボタン・ロビーの部屋の情報の板) を隠し (UI が増えても重ならない)、
// 扇のボタンごとに暗い丸の下地を敷く (船の床や家具の上でもボタンと字が読める)。
// PC は B キーで開閉・開いている間は 1〜4 キーでも選べる。生きている人だけが出せ、全員の画面に出る。
// 受け手は送り手が生きているか・間隔・種類の範囲を自分で確かめる (改造した客が連打しても画面を埋めない)
internal static class Emotes
{
    private const float Cooldown = 1.5f;     // 出した人のボタンの待ち時間
    private const long MinGapMs = 1200;      // 受け手が同じ人から受け付ける間隔 (待ち時間より短く・届く時刻の揺れの分)
    private const float RingScale = 0.8f;
    // 扇は「エモート」の周りの円弧 (半径・すき間は並びの升の大きさの倍数)。真上から左回りに 4 つ。
    // 上に画面の上端 (右上のボタンの段) までの空きが足りなければ、始まりを左へ下げて角度を詰め、詰めた分だけ半径を伸ばす
    private const float FanRadius = 1.8f;    // 4 つが互いに重ならない長さに少し足した
    private const float FanSpread = 30f;     // 隣どうしの角度
    private const float FanEnd = 215f;       // 扇の終わりの角度の上限
    private const float FanGap = 0.95f;      // 扇の隣どうしの中心の間 (升の倍数)
    private const float TopBar = 1.5f;       // 画面の上端から右上のボタンの段の下まで (升の倍数)
    private const float BackdropSize = 1.85f; // 下地の丸の直径 (ボタンの単位・絵と下の字を包む)
    private const float BackdropDrop = 0.08f; // 下地の中心をボタンの中心から下げる (手は上・字は下にあり、その真ん中へ)
    private const int BackdropPx = 128;
    private const float IconScale = 0.7f;    // 開いた手 (1.5 単位) が約 1.05 単位になる (隣の本編のボタンの絵と同じくらい)
    private const float IconLift = 0.17f;    // 手の中心をボタンの絵の中心からこれだけ上へ
    private static readonly string[] Names = { "Emote.Wave", "Emote.Jump", "Emote.Clap", "Emote.Beckon" };

    private static readonly RemoteCall<byte> PlayCall = new("Emote.Play", Route.Anyone,
        (w, kind) => w.Write(kind), r => r.ReadByte(), Receive, Delivery.Queued);

    private static readonly long[] LastMs = new long[256];
    private static Lifespan _life;
    private static ModButton _main;
    private static GameObject _ring;
    private static Sprite _backdropSprite;
    private static string _fanInfo = "";
    private static bool _retry, _iconDim, _lobby;
    private static SpriteRenderer _mainIcon;
    private static readonly System.Collections.Generic.List<(Transform T, Vector3 Scale)> Hidden = new();
    private static int _frame;
    internal static int Dropped;

    private static bool Alive(PlayerControl p) => p && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

    // ロビーか試合の作業中 (会議・追放の間は出さない)
    private static bool InTasks => (ShipStatus.Instance || LobbyBehaviour.Instance) && !MeetingHud.Instance && !ExileController.Instance;

    private static void Receive(PlayerControl sender, byte kind)
    {
        long now = Environment.TickCount64;
        if (!sender || kind >= EmoteArt.Count || !Alive(sender) || !InTasks || now - LastMs[sender.PlayerId] < MinGapMs)
        {
            Dropped++;
            return;
        }
        LastMs[sender.PlayerId] = now;
        EmoteArt.Play(sender, kind);
    }

    // 自分がエモートを出す (手元に出して全員へ送る)
    internal static bool PlayLocal(byte kind)
    {
        var lp = PlayerControl.LocalPlayer;
        if (kind >= EmoteArt.Count || !Alive(lp) || !InTasks || lp.inVent || !lp.Visible) return false;
        if (!EmoteArt.Play(lp, kind)) return false;
        LastMs[lp.PlayerId] = Environment.TickCount64;
        PlayCall.Send(kind);
        return true;
    }

    // ── ボタンと扇 ───────────────────────────────────────────────

    // HUD が出た時に作る (ロビーか試合中・もう出ていれば何もしない)
    internal static void EnsureButtons()
    {
        // 船があれば試合中 (フリープレイは試合の状態が「開始」にならない)・無くてロビーの物があればロビー
        // 生きているかは自分で見る (HUD の出入りを数える ModButton の後置とこの後置の順は決まっていない)
        if (_life is { IsDead: false } || !Alive(PlayerControl.LocalPlayer)) return;
        bool ship = ShipStatus.Instance;
        if (!ship && !LobbyBehaviour.Instance) return;
        _lobby = !ship;
        _life = RoleState.Match.Child();
        _main = ModButton.Create(_life, null, new Text("Emote.Button"), Cooldown, () =>
        {
            SetRing(!RingOpen);
            return false;
        });
        if (_main == null || !_main.Transform)
        {
            _life.Release();
            _life = null;
            return;
        }
        _mainIcon = HandIcon(_main, EmoteArt.Wave);
        _iconDim = false;
        _ring = new GameObject("MrpEmoteRing");
        _ring.transform.SetParent(_main.Transform, false);
        _ring.layer = _main.Transform.gameObject.layer;
        _ring.SetActive(false);
        for (byte k = 0; k < EmoteArt.Count; k++)
        {
            byte kind = k;
            var b = ModButton.Create(_life, null, new Text(Names[kind]), 0f, () => Choose(kind), _ring.transform);
            if (b == null || !b.Transform) continue;
            // 扇のボタンは扇 (_ring) の出し入れで隠すので、自分は出したままにする。置き場所は開く時に決める
            b.Transform.gameObject.SetActive(true);
            HandIcon(b, kind);
            NewBackdrop(b);
            b.Transform.localScale = new Vector3(RingScale, RingScale, 1f);
        }
        _life.OnRelease(() =>
        {
            ShowOthers();
            _main = null;
            _ring = null;
            _mainIcon = null;
        });
    }

    // アイコン = 本人の色に塗った手 (本編のボタンの絵の材質では色のひな形が塗られないので、隠して手の絵を重ねる)
    private static SpriteRenderer HandIcon(ModButton b, byte kind)
    {
        var g = b.Graphic;
        var mat = FxHands.PlayerMat();
        var sprite = EmoteArt.Icon(kind);
        var lp = PlayerControl.LocalPlayer;
        if (!g || !mat || !sprite || !lp || lp.Data == null) return null;
        g.enabled = false;
        var go = new GameObject("MrpEmoteIcon") { layer = g.gameObject.layer };
        go.transform.SetParent(g.transform, false);
        // 本編のボタンの絵は下の字 (「エモート」) の所を空けてあるので、手は字より上の枠に収める (中心を上げて小さく)
        var c = EmoteArt.IconCenter(kind);
        go.transform.localPosition = new Vector3(-c.x * IconScale, IconLift - c.y * IconScale, -0.01f);
        go.transform.localScale = new Vector3(IconScale, IconScale, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sharedMaterial = mat;
        sr.sprite = sprite;
        sr.sortingLayerID = g.sortingLayerID;
        sr.sortingOrder = g.sortingOrder + 1;
        PlayerMaterial.SetColors(lp.Data.DefaultOutfit.ColorId, sr);
        return sr;
    }

    private static bool RingOpen => _ring && _ring.activeSelf;

    private static void SetRing(bool open)
    {
        if (!_ring) return;
        if (open && _main != null && _main.CoolingDown) return;
        if (!open)
        {
            // 開いた途中で落ちて扇が閉じたままの時も、隠した物は必ず戻す
            ShowOthers();
            _ring.SetActive(false);
            return;
        }
        if (RingOpen) return;
        try
        {
            HideOthers();
            PlaceFan();
            _ring.SetActive(true);
        }
        catch (Exception e)
        {
            ShowOthers();
            Plugin.Logger.LogError($"emote fan: {e}");
        }
    }

    // 下地 = ふちがぼけた暗い丸。扇のボタンの子で、ボタンの絵より後ろ (描く順を 1 つ下げる)
    private static void NewBackdrop(ModButton b)
    {
        var g = b.Graphic;
        var sprite = BackdropSprite();
        if (!g || !sprite) return;
        var go = new GameObject("MrpEmoteBackdrop") { layer = b.Transform.gameObject.layer };
        go.transform.SetParent(b.Transform, false);
        go.transform.localPosition = new Vector3(0f, -BackdropDrop, 0.05f);
        go.transform.localScale = new Vector3(BackdropSize, BackdropSize, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = new Color(0.04f, 0.05f, 0.1f, 0.72f);
        sr.sortingLayerID = g.sortingLayerID;
        sr.sortingOrder = g.sortingOrder - 1;
    }

    // 1 単位の丸 (外側の 16% でなめらかに消える)。起動中は持ち続ける
    private static unsafe Sprite BackdropSprite()
    {
        if (_backdropSprite) return _backdropSprite;
        const int n = BackdropPx;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
            float t = FxMath.Clamp01((1f - MathF.Sqrt(dx * dx + dy * dy)) / 0.16f);
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(t * t * (3f - 2f * t) * 255f + 0.5f);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpEmoteBackdrop", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        _backdropSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n, 0, SpriteMeshType.FullRect);
        _backdropSprite.hideFlags = HideFlags.HideAndDontSave;
        return _backdropSprite;
    }

    // 扇に掛かりうる UI を大きさ 0 にして隠す (出し入れ (SetActive) だと本編の並べ直しで「エモート」の位置が動き、
    // 本編が自分で出し入れする物とも取り合いになる)。閉じたら元の大きさへ
    private static void HideOthers()
    {
        ShowOthers();
        var me = _main.Transform;
        var grid = me.parent;
        for (int i = 0; i < grid.childCount; i++)
        {
            var c = grid.GetChild(i);
            if (c != me && c.gameObject.activeSelf) Hide(c);
        }
        var pane = _lobby ? Vanilla.LobbyInfo : null;
        if (pane) Hide(pane.transform);
    }

    private static void Hide(Transform t)
    {
        Hidden.Add((t, t.localScale));
        t.localScale = Vector3.zero;
    }

    private static void ShowOthers()
    {
        foreach (var (t, scale) in Hidden)
            if (t) t.localScale = scale;
        Hidden.Clear();
    }

    // 扇のボタンを「エモート」の周りに置く。半径は並びの升の大きさから (場面で升の大きさが違う)
    private static void PlaceFan()
    {
        var grid = _main.Transform.parent;
        Vector3 me = _main.Transform.localPosition;
        var arrange = grid.GetComponent<GridArrange>();
        float step = arrange ? FxMath.Max(arrange.CellSize.x, arrange.CellSize.y) : 0f;
        if (step < 0.3f) step = 1.1f;
        var cam = Camera.main;
        float top = cam ? grid.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(0.5f, 1f, 0f))).y : float.MaxValue;
        float room = top - me.y - step * (TopBar + 0.45f);
        float r = step * FanRadius, spread = FanSpread;
        float start = FanStart(room, r);
        for (int pass = 0; pass < 3 && start > 90f; pass++)
        {
            spread = FxMath.Min(FanSpread, (FanEnd - start) / 3f);
            r = FxMath.Max(step * FanRadius, step * FanGap / (2f * MathF.Sin(spread * 0.5f * FxMath.Deg2Rad)));
            start = FanStart(room, r);
        }
        if (start > 90f) spread = FxMath.Min(FanSpread, (FanEnd - start) / 3f);
        for (int k = 0; k < _ring.transform.childCount; k++)
        {
            var b = _ring.transform.GetChild(k);
            float a = (start + spread * k) * FxMath.Deg2Rad;
            b.position = grid.TransformPoint(new Vector3(me.x + FxMath.Cos(a) * r, me.y + FxMath.Sin(a) * r, me.z));
            Vector3 lp = b.localPosition;
            b.localPosition = new Vector3(lp.x, lp.y, -0.5f);
        }
        _fanInfo = $"step={step:0.##} r={r:0.##} start={start:0} spread={spread:0} gridScale={grid.lossyScale.x:0.###} mainScale={_main.Transform.lossyScale.x:0.###}";
    }

    // いちばん上のボタンが room (「エモート」から上へ使える長さ) に収まる、真上 (90°) 以上の始まりの角度
    private static float FanStart(float room, float r)
    {
        if (room >= r) return 90f;
        // 上に全く空きが無くても真左より少し上から始める (下へ回しすぎると半径が伸び続ける)
        return FxMath.Min(170f, 180f - MathF.Asin(FxMath.Clamp(room / r, -1f, 1f)) * FxMath.Rad2Deg);
    }

    private static bool Choose(byte kind)
    {
        SetRing(false);
        if (_main != null && _main.CoolingDown) return false;
        if (PlayLocal(kind)) _main?.StartCooldown();
        return false;
    }

    // 毎フレーム: PC のキー (B で開閉・開いている間は 1〜4)
    internal static void Tick()
    {
        if (_main == null)
        {
            // HUD が出た時に船がまだ無かった試合と、HUD の出入りが無いロビーは、ここで 0.5 秒ごとに作る
            if (++_frame % 30 == 0 && (_retry || LobbyBehaviour.Instance)) EnsureButtons();
            return;
        }
        // ロビーで作ったボタンは試合が始まったら作り直す (並びの中身と HUD の出入りが試合用に替わる)・逆も同じ
        if (++_frame % 30 == 0)
        {
            if (_lobby == (bool)ShipStatus.Instance)
            {
                _life?.Release();
                _life = null;
                _retry = true;
                return;
            }
        }
        // 待ち時間の間はアイコンを暗く (本編のボタンの暗くする処理は隠した本編の絵にしか掛からない)
        if (_main.CoolingDown != _iconDim && _mainIcon)
        {
            _iconDim = _main.CoolingDown;
            _mainIcon.color = _iconDim ? new Color(0.45f, 0.45f, 0.45f, 1f) : Color.white;
        }
        if (!ModButton.HudShown) return;
        if (Input.GetKeyDown(KeyCode.B))
        {
            // 文字を打っている間 (チャット・ロビーの設定画面の検索欄) は B を奪わない
            var chat = Vanilla.Chat;
            if (!(chat && chat.IsOpenOrOpening) && !GameSettingMenu.Instance) SetRing(!RingOpen);
            return;
        }
        if (!RingOpen) return;
        for (byte k = 0; k < EmoteArt.Count; k++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + k))
            {
                Choose(k);
                return;
            }
    }

    internal static void CloseRing() => SetRing(false);

    internal static void OnHudShown(bool shown)
    {
        _retry = shown;
        if (shown) EnsureButtons();
        else SetRing(false);
    }

    // 確認用: dev `emote`
    internal static string Command(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string head = parts.Length > 0 ? parts[0] : "stats";
        switch (head)
        {
            case "ring":
                EnsureButtons();
                SetRing(!RingOpen);
                break;
            case "pick" when parts.Length > 1 && byte.TryParse(parts[1], out byte pk):
                // 扇のボタンを本編の押下の道で押す (開いていなければ開いてから)
                if (!RingOpen) SetRing(true);
                return $"{(_ring && pk < _ring.transform.childCount ? Press(pk) : "no ring")} {Stats()}";
            case "hold" when parts.Length > 2 && byte.TryParse(parts[1], out byte hk) && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ht):
                // 姿を止めて見る (手の形と置き場所の確認)
                if (!EmoteArt.Hold(PlayerControl.LocalPlayer, hk, ht)) return $"ERR emote hold refused {Stats()}";
                break;
            case "off":
                EmoteArt.ClearAll();
                break;
            case "stats":
                break;
            default:
                if (!byte.TryParse(head, out byte kind)) return "ERR emote [ring | pick <0-3> | hold <0-3> <秒> | off | stats | <0-3> [番号]]";
                if (parts.Length > 1 && byte.TryParse(parts[1], out byte pid))
                {
                    // 他人 (ダミー) が送ってきたのと同じ受け手の道を通す
                    var p = GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;
                    if (!p) return $"ERR no player {pid}";
                    Receive(p, kind);
                }
                else if (!PlayLocal(kind)) return $"ERR emote refused {Stats()}";
                break;
        }
        return $"OK emote {Stats()}";
    }

    private static string Press(byte i)
    {
        var b = _ring.transform.GetChild(i).GetComponent<PassiveButton>();
        if (!b) return "no button";
        b.OnClick.Invoke();
        return $"pressed={i}";
    }

    private static string Stats() =>
        $"{EmoteArt.Describe()} dropped={Dropped} sent={PlayCall.Sent} recv={PlayCall.Received} main={(_main != null ? _main.Describe() : "none")} ring={(RingOpen ? "open" : "closed")} fan[{_fanInfo}]";
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.SetHudActive), typeof(PlayerControl), typeof(RoleBehaviour), typeof(bool))]
internal static class EmoteHudPatch
{
    public static void Postfix(bool isActive) => Emotes.OnHudShown(isActive);
}

// 会議が始まったら出ているエモートを全部止め (跳ねた体を元の高さへ)、扇を閉じて隠したボタンを戻す
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class EmoteMeetingPatch
{
    public static void Postfix()
    {
        EmoteArt.ClearAll();
        Emotes.CloseRing();
    }
}
