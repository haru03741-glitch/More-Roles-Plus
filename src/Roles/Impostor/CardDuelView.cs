using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Options;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles.Impostor;

// カードバトルの画面 (全員)。会議の電話の上に、上 = 相手側・下 = 自分側 (観戦者は 下 = カードゲーマー) を並べる。
// 名札は隠すので投票はできない。チャットは本編のまま使える。ホストから届いた状態を映すだけで、決まりの計算はしない
internal static class CardDuelView
{
    private const float Z = -50f;
    private const float TopY = 1.62f, BottomY = -0.86f, FieldY = 0.42f, HandY = -1.86f, HeadY = 2.28f;
    private const float CardW = 0.95f, CardH = 1.3f, MiniW = 0.42f, MiniH = 0.58f;

    private static GameObject _root;
    private static readonly List<GameObject> Hidden = new();
    private static readonly List<int> Slots = new();
    private static byte _gamer = 255, _target = 255;
    private static DuelState _st;
    private static float _stAt, _flipAt = -10f, _endAt = -10f, _openAt;
    private static byte _mine = CardBook.None;
    private static float _mineAt;
    private static int _round = -1;
    private static DuelPhase _phase = DuelPhase.Pick;

    // 部品
    private static TextMeshPro _head, _timer, _center, _spectate, _banner;
    private static SpriteRenderer _timerBar, _bannerBack;
    private static Row _top, _bottom;
    private static CardFace _slotTop, _slotBottom;
    private static readonly CardFace[] _hand = new CardFace[6];
    private static CardFace _charge;
    private static readonly CardFace[] _mini = new CardFace[6];
    private static readonly List<(TextMeshPro t, float at, Vector3 from)> Pops = new();

    // ── 開く・閉じる ──

    internal static void Open(byte gamer, byte target)
    {
        Close();
        var hud = MeetingHud.Instance;
        if (!hud || !UiAssets.TextProto) return;
        _gamer = gamer; _target = target;
        _st = null; _round = -1; _mine = CardBook.None; _flipAt = _endAt = -10f; _openAt = Time.time;
        // 名札と「投票スキップ」を隠す (投票できないように)
        foreach (var a in hud.playerStates)
            if (a && a.gameObject.activeSelf) { a.gameObject.SetActive(false); Hidden.Add(a.gameObject); }
        if (hud.SkipVoteButton && hud.SkipVoteButton.gameObject.activeSelf) { hud.SkipVoteButton.gameObject.SetActive(false); Hidden.Add(hud.SkipVoteButton.gameObject); }
        Build(hud);
    }

    internal static void Close()
    {
        foreach (var go in Hidden) if (go) go.SetActive(true);
        Hidden.Clear();
        if (_root) Object.Destroy(_root);
        _root = null;
        UiActions.Release(Slots);
        Pops.Clear();
        _st = null;
    }

    // ── 状態が届いた ──

    internal static void Apply(DuelState d)
    {
        if (!_root)
        {
            if (!MeetingHud.Instance) return;
            Open(d.Gamer, d.Target); // 始まりの知らせより先に状態が届いた時
            if (!_root) return;
        }
        var prev = _st;
        _st = d;
        _stAt = Time.time;
        if (d.Round != _round)
        {
            _round = d.Round;
            if (d.Phase == DuelPhase.Pick) _mine = CardBook.None;
        }
        if (d.Phase == DuelPhase.Reveal && (prev == null || prev.Phase != DuelPhase.Reveal)) Revealed(d);
        if (d.Phase == DuelPhase.End && (prev == null || prev.Phase != DuelPhase.End))
        {
            if (prev == null || prev.Phase != DuelPhase.Reveal) Revealed(d);
            _endAt = Time.time + 1.2f; // 最後の 1 手を見せてから題字
        }
        _phase = d.Phase;
        Refresh();
    }

    private static byte Me => PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : (byte)255;
    private static bool Spectator => Me != _gamer && Me != _target;
    private static DuelSide BottomSide(DuelState d) => Me == d.B.Id ? d.B : d.A;
    private static DuelSide TopSide(DuelState d) => d.Other(BottomSide(d));

    // ── 作る ──

    private static void Build(MeetingHud hud)
    {
        _root = new GameObject("MrpCardDuel") { layer = hud.gameObject.layer };
        _root.transform.SetParent(hud.transform, false);
        _root.transform.localPosition = new Vector3(0f, 0f, Z);
        var t = _root.transform;

        // 卓 (電話の画面を覆う)
        MenuArt.Panel(t, "Table", 0.2f, new Vector2(8.6f, 5.3f), new Vector3(0f, 0.15f, 0f), new UnityEngine.Color(0.05f, 0.09f, 0.12f, 0.97f), null);
        MenuArt.Panel(t, "Felt", 0.18f, new Vector2(8.3f, 2.1f), new Vector3(0f, FieldY + 0.15f, -0.01f), new UnityEngine.Color(0.07f, 0.24f, 0.2f, 1f), null);
        MenuArt.Panel(t, "Rail", 0.04f, new Vector2(8.3f, 0.03f), new Vector3(0f, FieldY - 0.9f, -0.02f), new UnityEngine.Color(0.85f, 0.7f, 0.35f, 0.5f), null);
        MenuArt.Panel(t, "Rail2", 0.04f, new Vector2(8.3f, 0.03f), new Vector3(0f, FieldY + 1.2f, -0.02f), new UnityEngine.Color(0.85f, 0.7f, 0.35f, 0.5f), null);

        _head = Txt(t, "Head", new Vector3(-3.9f, HeadY, -0.1f), 1.5f, TextAlignmentOptions.Left, 6f);
        _timer = Txt(t, "Timer", new Vector3(3.9f, HeadY, -0.1f), 1.5f, TextAlignmentOptions.Right, 3f);
        _timerBar = Quad(t, "TimerBar", new Vector3(0f, HeadY - 0.2f, -0.1f), new Vector2(7.8f, 0.05f), new UnityEngine.Color(1f, 0.85f, 0.35f, 0.9f));

        _top = new Row(t, TopY);
        _bottom = new Row(t, BottomY);

        _slotTop = new CardFace(t, "SlotTop", new Vector3(-0.85f, FieldY, -0.2f), CardW, CardH);
        _slotBottom = new CardFace(t, "SlotBottom", new Vector3(0.85f, FieldY, -0.2f), CardW, CardH);
        var vs = Txt(t, "Vs", new Vector3(0f, FieldY, -0.15f), 2f, TextAlignmentOptions.Center, 1f);
        vs.text = "VS";
        vs.color = new UnityEngine.Color(1f, 0.85f, 0.4f, 0.35f);
        _center = Txt(t, "Center", new Vector3(2.75f, FieldY, -0.3f), 1.4f, TextAlignmentOptions.Center, 2.6f);
        _center.enableWordWrapping = true;
        _center.rectTransform.sizeDelta = new Vector2(2.4f, 1.4f);

        for (int i = 0; i < _mini.Length; i++)
            _mini[i] = new CardFace(t, "Mini" + i, new Vector3(-1.4f + i * 0.5f, TopY - 0.62f, -0.2f), MiniW, MiniH);

        for (int i = 0; i < _hand.Length; i++)
        {
            byte slot = (byte)i;
            _hand[i] = new CardFace(t, "Hand" + i, Vector3.zero, CardW, CardH);
            _hand[i].Clickable(() => Choose(slot));
        }
        _charge = new CardFace(t, "Charge", Vector3.zero, CardW, CardH);
        _charge.Clickable(() => Choose(CardBook.Charge));

        _spectate = Txt(t, "Spectate", new Vector3(0f, HandY - 0.82f, -0.1f), 1.2f, TextAlignmentOptions.Center, 7f);
        _bannerBack = Quad(t, "BannerBack", new Vector3(0f, FieldY, -0.5f), new Vector2(8.6f, 1.1f), new UnityEngine.Color(0f, 0f, 0f, 0.8f));
        _banner = Txt(t, "Banner", new Vector3(0f, FieldY, -0.55f), 4f, TextAlignmentOptions.Center, 8f);
        _bannerBack.gameObject.SetActive(false);
        _banner.gameObject.SetActive(false);
        Refresh();
    }

    private static TextMeshPro Txt(Transform parent, string name, Vector3 pos, float size, TextAlignmentOptions align, float width)
    {
        var t = Object.Instantiate(UiAssets.TextProto, parent);
        t.name = name;
        t.gameObject.SetActive(true);
        t.gameObject.layer = parent.gameObject.layer;
        t.transform.localPosition = pos;
        t.transform.localScale = Vector3.one;
        t.enableAutoSizing = false;
        t.enableWordWrapping = false;
        t.margin = Vector4.zero;
        t.richText = true;
        t.alignment = align;
        t.fontSize = size;
        t.color = UnityEngine.Color.white;
        t.outlineWidth = 0.2f;
        t.outlineColor = UnityEngine.Color.black;
        t.rectTransform.sizeDelta = new Vector2(width, 0.5f);
        t.text = "";
        return t;
    }

    private static SpriteRenderer Quad(Transform parent, string name, Vector3 pos, Vector2 size, UnityEngine.Color c, Sprite sprite = null)
    {
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        var r = go.AddComponent<SpriteRenderer>();
        r.sprite = sprite ? sprite : White;
        r.color = c;
        return r;
    }

    private static Sprite _white;
    private static Sprite White => _white ??= FxSprites.Rect(4, 4, (_, _) => 1f, "MrpCardWhite");

    // ── 押した ──

    private static void Choose(byte slot)
    {
        var d = _st;
        if (d == null || d.Phase != DuelPhase.Pick || Spectator || _mine != CardBook.None) return;
        var s = BottomSide(d);
        if (s.Picked || !DuelState.CanPlay(s, slot)) return;
        _mine = slot;
        _mineAt = Time.time;
        CardDuelHost.Pick(slot);
        Refresh();
    }

    // ── 開いた時の演出 (ダメージの数など) ──

    private static void Revealed(DuelState d)
    {
        _flipAt = Time.time;
        foreach (var (s, row) in new[] { (TopSide(d), _top), (BottomSide(d), _bottom) })
        {
            var at = row.HpAnchor;
            float dx = 0f;
            if (s.Took > 0) { Pop($"<color=#FF5050>-{s.Took}</color>", at + new Vector3(dx, 0f, 0f), 0.35f); dx += 0.6f; row.Hit(); }
            if (s.Healed > 0) { Pop($"<color=#60FF70>+{s.Healed} HP</color>", at + new Vector3(dx, 0f, 0f), 0.35f); dx += 0.9f; }
            if (s.Gained > 0) Pop($"<color=#FFD840>+{s.Gained}</color>", row.EnAnchor, 0.45f);
            if (s.Lost > 0) Pop($"<color=#B080FF>-{s.Lost}</color>", row.EnAnchor + new Vector3(0.5f, 0f, 0f), 0.45f);
        }
    }

    private static void Pop(string text, Vector3 at, float delay)
    {
        if (!_root) return;
        var t = Txt(_root.transform, "Pop", at, 2.2f, TextAlignmentOptions.Center, 2f);
        t.text = text;
        t.gameObject.SetActive(false);
        Pops.Add((t, Time.time + delay, at));
    }

    // 開いた 2 枚から、場の真ん中に出す一言
    private static string Moment(DuelState d)
    {
        var a = d.A.Shown; var b = d.B.Shown;
        if (a == CardBook.None || b == CardBook.None) return "";
        CardKind? ka = a == CardBook.Charge ? null : CardBook.Get(a).Kind, kb = b == CardBook.Charge ? null : CardBook.Get(b).Kind;
        bool atkA = ka is CardKind.Strike or CardKind.Pierce, atkB = kb is CardKind.Strike or CardKind.Pierce;
        if (ka == CardKind.Counter && atkB || kb == CardKind.Counter && atkA) return new Text("CardGamer.Moment.Counter").ToString();
        if (ka == CardKind.Counter || kb == CardKind.Counter) return new Text("CardGamer.Moment.Whiff").ToString();
        if (kb == CardKind.Guard && ka == CardKind.Strike && d.B.Took == 0 || ka == CardKind.Guard && kb == CardKind.Strike && d.A.Took == 0)
            return new Text("CardGamer.Moment.Blocked").ToString();
        if (ka == CardKind.Pierce && kb == CardKind.Guard || kb == CardKind.Pierce && ka == CardKind.Guard) return new Text("CardGamer.Moment.Pierce").ToString();
        if (d.A.Took > 0 && d.B.Took > 0) return new Text("CardGamer.Moment.Trade").ToString();
        return "";
    }

    // ── 名前 (伏せる設定ならカードゲーマーは「？？？」) ──

    private static (string name, UnityEngine.Color color, bool hidden) Who(DuelState d, byte id)
    {
        if (d.Anonymous && id == d.Gamer && Me != d.Gamer)
            return (new Text("CardGamer.Unknown").ToString(), new UnityEngine.Color(0.6f, 0.6f, 0.6f, 1f), true);
        var p = PortKit.ById(id);
        if (!p || p.Data == null) return ("?", UnityEngine.Color.white, false);
        int cid = p.Data.DefaultOutfit.ColorId;
        var pc = Palette.PlayerColors;
        var c = cid >= 0 && cid < pc.Length ? (UnityEngine.Color)pc[cid] : UnityEngine.Color.white;
        return (p.Data.PlayerName, c, false);
    }

    // ── 毎フレーム ──

    internal static void Tick()
    {
        var hud = MeetingHud.Instance;
        if (!hud)
        {
            if (_root || CardDuelHost.Busy) { Close(); CardDuelHost.Reset(); }
            return;
        }
        if (!_root) return;
        // 会議の時間を止める (全員の端末で。ホストの時計が会議の終わりを決める)
        if (CardGamer.PauseMeeting && hud.state is MeetingHud.VoteStates.Discussion or MeetingHud.VoteStates.NotVoted or MeetingHud.VoteStates.Voted)
            hud.discussionTimer -= Time.deltaTime;
        // 本編が名札や「投票スキップ」を出し直したら隠し直す
        foreach (var a in hud.playerStates)
            if (a && a.gameObject.activeSelf) { a.gameObject.SetActive(false); if (!Hidden.Contains(a.gameObject)) Hidden.Add(a.gameObject); }
        if (hud.SkipVoteButton && hud.SkipVoteButton.gameObject.activeSelf) { hud.SkipVoteButton.gameObject.SetActive(false); if (!Hidden.Contains(hud.SkipVoteButton.gameObject)) Hidden.Add(hud.SkipVoteButton.gameObject); }
        Animate();
    }

    private static void Animate()
    {
        float now = Time.time;
        var d = _st;
        // 残り時間
        if (d != null && d.Phase == DuelPhase.Pick)
        {
            float left = Mathf.Max(0f, d.Left - (now - _stAt));
            _timer.text = $"{Mathf.CeilToInt(left)}";
            _timer.color = left <= 5f ? new UnityEngine.Color(1f, 0.4f, 0.35f, 1f) : UnityEngine.Color.white;
            float k = Mathf.Clamp01(left / Mathf.Max(1f, CardGamer.TurnTime));
            _timerBar.transform.localScale = new Vector3(7.8f * k, 0.05f, 1f);
            _timerBar.gameObject.SetActive(true);
        }
        else
        {
            _timer.text = "";
            _timerBar.gameObject.SetActive(false);
        }
        // 場のカードを返す (伏せた面 → 表)。返し終わるまでは裏
        float f = now - _flipAt;
        bool flipping = f >= 0f && f < 0.36f;
        foreach (var c in new[] { _slotTop, _slotBottom })
        {
            if (!c.Root.activeSelf) continue;
            float sx = flipping ? Mathf.Abs(Mathf.Cos(f / 0.36f * Mathf.PI)) : 1f;
            if (flipping) c.ShowBack(f < 0.18f);
            c.Root.transform.localScale = new Vector3(Mathf.Max(0.02f, sx) * c.Pulse(now), c.Pulse(now), 1f);
        }
        // 伏せたばかりの札は少し跳ねる
        foreach (var c in _hand) c?.Hover(now);
        _charge?.Hover(now);
        _top.Tick(now);
        _bottom.Tick(now);
        // 数字の飛び出し
        for (int i = Pops.Count - 1; i >= 0; i--)
        {
            var (t, at, from) = Pops[i];
            if (!t) { Pops.RemoveAt(i); continue; }
            float a = now - at;
            if (a < 0f) continue;
            if (a > 1.3f) { Object.Destroy(t.gameObject); Pops.RemoveAt(i); continue; }
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            float pop = a < 0.12f ? 0.6f + a / 0.12f * 0.6f : 1.2f - Mathf.Min(0.2f, (a - 0.12f) * 0.8f);
            t.transform.localPosition = from + new Vector3(0f, 0.25f + a * 0.35f, -0.4f);
            t.transform.localScale = Vector3.one * pop;
            t.alpha = a > 0.9f ? 1f - (a - 0.9f) / 0.4f : 1f;
        }
        // 終わりの題字
        if (d != null && d.Phase == DuelPhase.End && now >= _endAt)
        {
            if (!_banner.gameObject.activeSelf)
            {
                _banner.gameObject.SetActive(true);
                _bannerBack.gameObject.SetActive(true);
                _banner.text = BannerText(d);
            }
            float a = now - _endAt;
            float s = a < 0.2f ? 1.6f - a / 0.2f * 0.6f : 1f;
            _banner.transform.localScale = Vector3.one * s;
            _bannerBack.color = new UnityEngine.Color(0f, 0f, 0f, Mathf.Min(0.8f, a * 4f));
        }
    }

    private static string BannerText(DuelState d)
    {
        if (d.Winner == 255) return $"<color=#E0E0E0>{new Text("CardGamer.Draw")}</color>";
        if (!Spectator) return d.Winner == Me ? $"<color=#FFD840>{new Text("CardGamer.Win")}</color>" : $"<color=#7090FF>{new Text("CardGamer.Lose")}</color>";
        var (name, _, _) = Who(d, d.Winner);
        return $"<size=60%>{new Text("CardGamer.ResultWin").With("name", name)}</size>";
    }

    // ── 状態を部品に映す ──

    private static void Refresh()
    {
        if (!_root) return;
        var d = _st;
        if (d == null)
        {
            _head.text = new Text("CardGamer.Title").ToString();
            _center.text = new Text("CardGamer.Dealing").ToString();
            _top.Hide(); _bottom.Hide();
            _slotTop.Root.SetActive(false); _slotBottom.Root.SetActive(false);
            foreach (var c in _hand) c.Root.SetActive(false);
            foreach (var c in _mini) c.Root.SetActive(false);
            _charge.Root.SetActive(false);
            _spectate.text = Spectator ? new Text("CardGamer.Spectating").ToString() : "";
            return;
        }
        var bottom = BottomSide(d);
        var top = TopSide(d);
        _head.text = $"{new Text("CardGamer.Title")}  <size=75%><color=#FFE08A>{new Text("CardGamer.Round").With("n", d.Round).With("max", d.MaxRounds)}</color></size>";
        var wt = Who(d, top.Id);
        var wb = Who(d, bottom.Id);
        _top.Show(d, top, wt);
        _bottom.Show(d, bottom, wb);

        // 場
        ShowSlot(_slotTop, d, top, false);
        ShowSlot(_slotBottom, d, bottom, !Spectator);

        // 真ん中の一言
        _center.text = d.Phase switch
        {
            DuelPhase.Pick when Spectator => new Text("CardGamer.Choosing").ToString(),
            DuelPhase.Pick when bottom.Picked || _mine != CardBook.None => new Text("CardGamer.Waiting").ToString(),
            DuelPhase.Pick => new Text("CardGamer.PickHint").ToString(),
            _ => Moment(d),
        };

        // 上の人の手札 (小さく: 裏か、見える設定なら表)
        for (int i = 0; i < _mini.Length; i++)
        {
            var c = _mini[i];
            bool on = i < top.Hand.Count;
            c.Root.SetActive(on);
            if (!on) continue;
            byte id = top.Hand[i];
            if (id == CardBook.None) c.ShowBack(true);
            else { c.SetCard(id); c.ShowBack(false); }
            c.Dim(false);
        }

        // 下の人の手札 (自分なら押せる)
        bool mine = !Spectator;
        bool showHand = mine || d.ShowHands;
        int n = showHand ? bottom.Hand.Count : 0;
        float span = (n + (mine ? 1 : 0)) * (CardW + 0.12f) - 0.12f;
        float x0 = -span / 2f + CardW / 2f;
        bool canPick = mine && d.Phase == DuelPhase.Pick && !bottom.Picked && _mine == CardBook.None;
        for (int i = 0; i < _hand.Length; i++)
        {
            var c = _hand[i];
            bool on = i < n;
            c.Root.SetActive(on);
            if (!on) continue;
            c.Home = new Vector3(x0 + i * (CardW + 0.12f), HandY, -0.2f);
            byte id = bottom.Hand[i];
            if (id == CardBook.None) { c.ShowBack(true); c.Dim(false); c.Enable(false); continue; }
            c.SetCard(id);
            c.ShowBack(false);
            bool afford = DuelState.CanPlay(bottom, (byte)i);
            bool chosen = _mine == i;
            c.Dim(mine && (!afford || _mine != CardBook.None && !chosen));
            c.Enable(canPick && afford);
            c.Lift = chosen ? 0.25f : 0f;
        }
        _charge.Root.SetActive(mine);
        if (mine)
        {
            _charge.Home = new Vector3(x0 + n * (CardW + 0.12f), HandY, -0.2f);
            _charge.SetCard(CardBook.Charge);
            _charge.ShowBack(false);
            _charge.Dim(_mine != CardBook.None && _mine != CardBook.Charge || bottom.Energy >= d.MaxEnergy && d.Phase == DuelPhase.Pick && false);
            _charge.Enable(canPick);
            _charge.Lift = _mine == CardBook.Charge ? 0.25f : 0f;
        }
        _spectate.text = Spectator ? new Text("CardGamer.Spectating").ToString() : "";
        if (d.Phase != DuelPhase.End)
        {
            _banner.gameObject.SetActive(false);
            _bannerBack.gameObject.SetActive(false);
        }
    }

    // 場の札: 選ぶ間は伏せて置く (自分の札は自分にだけ表で薄く)。開いたら表
    private static void ShowSlot(CardFace c, DuelState d, DuelSide s, bool mineSide)
    {
        if (d.Phase == DuelPhase.Pick)
        {
            bool placed = s.Picked || mineSide && _mine != CardBook.None;
            c.Root.SetActive(placed);
            if (!placed) return;
            if (mineSide && _mine != CardBook.None)
            {
                c.SetCard(_mine == CardBook.Charge ? CardBook.Charge : _mine < s.Hand.Count ? s.Hand[_mine] : CardBook.None);
                c.ShowBack(false);
                c.Dim(true);
            }
            else { c.ShowBack(true); c.Dim(false); }
            c.Placed();
            return;
        }
        bool shown = s.Shown != CardBook.None;
        c.Root.SetActive(shown);
        if (!shown) return;
        c.SetCard(s.Shown);
        c.ShowBack(Time.time - _flipAt < 0.18f);
        c.Dim(false);
    }

    // ── 片方の人の段 (名前・HP・エナジー) ──

    private sealed class Row
    {
        private readonly GameObject _root;
        private readonly SpriteRenderer _dot;
        private readonly TextMeshPro _name, _hpLabel, _enLabel;
        private readonly SpriteRenderer[] _hp = new SpriteRenderer[10], _en = new SpriteRenderer[10];
        private float _hitAt = -10f;
        private readonly float _y;

        internal Vector3 HpAnchor => new(-0.3f, _y + 0.05f, -0.3f);
        internal Vector3 EnAnchor => new(2.6f, _y + 0.05f, -0.3f);

        internal Row(Transform parent, float y)
        {
            _y = y;
            _root = new GameObject("Row") { layer = parent.gameObject.layer };
            _root.transform.SetParent(parent, false);
            _root.transform.localPosition = new Vector3(0f, y, -0.1f);
            var t = _root.transform;
            MenuArt.Panel(t, "Plate", 0.12f, new Vector2(8.2f, 0.46f), Vector3.zero, new UnityEngine.Color(0f, 0f, 0f, 0.45f), null);
            _dot = Quad(t, "Dot", new Vector3(-3.85f, 0f, -0.01f), new Vector2(0.3f, 0.3f), UnityEngine.Color.white, CardArt.Circle);
            _name = Txt(t, "Name", new Vector3(-2.5f, 0f, -0.02f), 1.45f, TextAlignmentOptions.Left, 2.4f);
            _name.transform.localPosition = new Vector3(-2.45f, 0f, -0.02f);
            _hpLabel = Txt(t, "HpLabel", new Vector3(-0.85f, 0f, -0.02f), 1.1f, TextAlignmentOptions.Right, 0.8f);
            _hpLabel.text = "HP";
            _hpLabel.color = new UnityEngine.Color(1f, 0.55f, 0.55f, 1f);
            _enLabel = Txt(t, "EnLabel", new Vector3(1.95f, 0f, -0.02f), 1.1f, TextAlignmentOptions.Right, 0.8f);
            _enLabel.text = "EN";
            _enLabel.color = new UnityEngine.Color(1f, 0.85f, 0.35f, 1f);
            for (int i = 0; i < 10; i++)
            {
                _hp[i] = Quad(t, "Hp" + i, new Vector3(-0.3f + i * 0.24f, 0f, -0.02f), new Vector2(0.22f, 0.22f), UnityEngine.Color.white, CardArt.Heart);
                _en[i] = Quad(t, "En" + i, new Vector3(2.12f + i * 0.19f, 0f, -0.02f), new Vector2(0.17f, 0.17f), UnityEngine.Color.white, CardArt.Gem);
            }
        }

        internal void Hide() => _root.SetActive(false);

        internal void Show(DuelState d, DuelSide s, (string name, UnityEngine.Color color, bool hidden) who)
        {
            _root.SetActive(true);
            _dot.color = who.color;
            _name.text = who.name;
            _name.color = who.hidden ? new UnityEngine.Color(0.7f, 0.7f, 0.7f, 1f) : UnityEngine.Color.white;
            // 名前の長さで札の並びを詰めない (固定の位置)
            for (int i = 0; i < 10; i++)
            {
                bool on = i < d.MaxHp;
                _hp[i].gameObject.SetActive(on);
                if (on) _hp[i].color = i < s.Hp ? new UnityEngine.Color(1f, 0.28f, 0.32f, 1f) : new UnityEngine.Color(0.25f, 0.1f, 0.12f, 1f);
                bool en = i < d.MaxEnergy;
                _en[i].gameObject.SetActive(en);
                if (en) _en[i].color = i < s.Energy ? new UnityEngine.Color(1f, 0.85f, 0.3f, 1f) : new UnityEngine.Color(0.25f, 0.22f, 0.1f, 1f);
            }
        }

        internal void Hit() => _hitAt = Time.time + 0.35f;

        internal void Tick(float now)
        {
            float a = now - _hitAt;
            float shake = a >= 0f && a < 0.4f ? Mathf.Sin(a * 70f) * 0.06f * (1f - a / 0.4f) : 0f;
            _root.transform.localPosition = new Vector3(shake, _y, -0.1f);
        }
    }

    // ── 1 枚の札 ──

    private sealed class CardFace
    {
        internal readonly GameObject Root;
        private readonly GameObject _front, _back;
        private readonly SpriteRenderer _frame, _inner, _icon, _costDot;
        private readonly TextMeshPro _name, _desc, _cost, _power;
        private readonly float _w, _h;
        private readonly bool _mini;
        private PassiveButton _button;
        private bool _over, _enabled;
        private float _lift, _placedAt = -10f;
        private byte _card = 0xFE - 1;
        internal Vector3 Home;
        internal float Lift { set => _lift = value; }

        internal CardFace(Transform parent, string name, Vector3 pos, float w, float h)
        {
            _w = w; _h = h; _mini = w < 0.6f;
            Home = pos;
            Root = new GameObject(name) { layer = parent.gameObject.layer };
            Root.transform.SetParent(parent, false);
            Root.transform.localPosition = pos;
            var t = Root.transform;
            _front = new GameObject("Front") { layer = Root.layer };
            _front.transform.SetParent(t, false);
            var f = _front.transform;
            _frame = MenuArt.Panel(f, "Frame", 0.1f, new Vector2(w, h), Vector3.zero, UnityEngine.Color.white, null);
            _inner = MenuArt.Panel(f, "Inner", 0.08f, new Vector2(w - 0.08f, h - 0.08f), new Vector3(0f, 0f, -0.01f), new UnityEngine.Color(0.1f, 0.1f, 0.14f, 1f), null);
            float ic = _mini ? 0.26f : 0.5f;
            _icon = Quad(f, "Icon", new Vector3(0f, _mini ? 0.02f : 0.05f, -0.02f), new Vector2(ic, ic), UnityEngine.Color.white, CardArt.Strike);
            if (!_mini)
            {
                _name = Txt(f, "Name", new Vector3(0f, h / 2f - 0.17f, -0.03f), 0.95f, TextAlignmentOptions.Center, w);
                _desc = Txt(f, "Desc", new Vector3(0f, -h / 2f + 0.17f, -0.03f), 0.8f, TextAlignmentOptions.Center, w);
                _desc.color = new UnityEngine.Color(0.85f, 0.85f, 0.85f, 1f);
                _costDot = Quad(f, "CostDot", new Vector3(-w / 2f + 0.14f, h / 2f - 0.14f, -0.04f), new Vector2(0.22f, 0.22f), new UnityEngine.Color(1f, 0.85f, 0.3f, 1f), CardArt.Gem);
                _cost = Txt(f, "Cost", new Vector3(-w / 2f + 0.14f, h / 2f - 0.15f, -0.05f), 1f, TextAlignmentOptions.Center, 0.3f);
                _cost.color = new UnityEngine.Color(0.15f, 0.1f, 0f, 1f);
                _cost.outlineWidth = 0f;
                _power = Txt(f, "Power", new Vector3(w / 2f - 0.16f, -0.12f, -0.05f), 1.8f, TextAlignmentOptions.Center, 0.4f);
            }
            _back = new GameObject("Back") { layer = Root.layer };
            _back.transform.SetParent(t, false);
            var b = _back.transform;
            MenuArt.Panel(b, "BackFrame", 0.1f, new Vector2(w, h), new Vector3(0f, 0f, -0.06f), new UnityEngine.Color(0.85f, 0.7f, 0.35f, 1f), null);
            MenuArt.Panel(b, "BackInner", 0.08f, new Vector2(w - 0.08f, h - 0.08f), new Vector3(0f, 0f, -0.07f), new UnityEngine.Color(0.32f, 0.12f, 0.16f, 1f), null);
            Quad(b, "BackMark", new Vector3(0f, 0f, -0.08f), new Vector2(w * 0.6f, w * 0.6f), new UnityEngine.Color(0.85f, 0.7f, 0.35f, 0.8f), CardArt.Mark);
            _back.SetActive(false);
        }

        internal void Clickable(System.Action onClick)
        {
            var col = Root.AddComponent<BoxCollider2D>();
            col.size = new Vector2(_w, _h);
            _button = Root.AddComponent<PassiveButton>();
            _button.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            _button.OnMouseOver = new UnityEvent();
            _button.OnMouseOut = new UnityEvent();
            _button.OnClick.AddListener(UiActions.Take(onClick, Slots));
            _button.OnMouseOver.AddListener(UiActions.Take(() => _over = true, Slots));
            _button.OnMouseOut.AddListener(UiActions.Take(() => _over = false, Slots));
        }

        internal void Enable(bool on)
        {
            _enabled = on;
            if (_button) _button.enabled = on;
            if (!on) _over = false;
        }

        internal void SetCard(byte id)
        {
            if (id == _card) return;
            _card = id;
            if (id == CardBook.None) { ShowBack(true); return; }
            var (col, icon) = CardArt.Look(id);
            _frame.color = col;
            _inner.color = UnityEngine.Color.Lerp(new UnityEngine.Color(0.08f, 0.08f, 0.12f, 1f), col, 0.22f);
            _icon.sprite = icon;
            _icon.color = UnityEngine.Color.Lerp(col, UnityEngine.Color.white, 0.25f);
            if (_mini) return;
            if (id == CardBook.Charge)
            {
                _name.text = new Text("CardGamer.Card.Charge").ToString();
                _desc.text = new Text("CardGamer.Card.Charge.Desc").ToString();
                _cost.text = "0";
                _power.text = "";
                return;
            }
            var c = CardBook.Get(id);
            _name.text = new Text("CardGamer.Card." + c.Key).ToString();
            _desc.text = new Text("CardGamer.Card." + c.Key + ".Desc").ToString();
            _cost.text = c.Cost.ToString();
            _power.text = c.Kind is CardKind.Strike or CardKind.Guard or CardKind.Pierce ? c.Power.ToString() : "";
        }

        internal void ShowBack(bool back)
        {
            if (_back.activeSelf != back) _back.SetActive(back);
            if (_front.activeSelf == back) _front.SetActive(!back);
        }

        private bool _dim;
        internal void Dim(bool dim)
        {
            if (_dim == dim) return;
            _dim = dim;
            foreach (var r in _front.GetComponentsInChildren<SpriteRenderer>(true))
            {
                var c = r.color;
                c.a = dim ? 0.4f : 1f;
                r.color = c;
            }
            foreach (var t in _front.GetComponentsInChildren<TextMeshPro>(true)) t.alpha = dim ? 0.45f : 1f;
        }

        internal void Placed()
        {
            if (_placedAt < 0f || Time.time - _placedAt > 30f) _placedAt = Time.time;
        }

        internal float Pulse(float now)
        {
            float a = now - _placedAt;
            return a >= 0f && a < 0.25f ? 1.25f - a : 1f;
        }

        internal void Hover(float now)
        {
            if (!Root || !Root.activeSelf) return;
            float up = _lift + (_over && _enabled ? 0.12f : 0f);
            var p = Root.transform.localPosition;
            var target = Home + new Vector3(0f, up, 0f);
            Root.transform.localPosition = Vector3.Lerp(p, target, Mathf.Min(1f, Time.deltaTime * 14f));
            float s = _over && _enabled ? 1.06f : 1f;
            Root.transform.localScale = Vector3.Lerp(Root.transform.localScale, new Vector3(s, s, 1f), Mathf.Min(1f, Time.deltaTime * 14f));
        }
    }
}

// 札の絵 (式で焼く白い形。色は SpriteRenderer.color で付ける)
internal static class CardArt
{
    private const int N = 96;
    private static Sprite _strike, _guard, _counter, _pierce, _heal, _drain, _charge, _heart, _gem, _mark, _circle;

    // 距離 (外側が正) を縁の柔らかさ付きの不透明度に
    private static float A(float d) => Mathf.Clamp01(0.5f - d * N);

    internal static Sprite Circle => _circle ??= FxSprites.Rect(N, N, (x, y) => A(Mathf.Sqrt(x * x + y * y) - 0.46f), "MrpCardCircle");

    internal static Sprite Heart => _heart ??= FxSprites.Rect(N, N, (x, y) =>
    {
        // 二つの丸と下向きの三角
        float y2 = y + 0.05f;
        float c1 = Mathf.Sqrt((x + 0.2f) * (x + 0.2f) + (y2 - 0.12f) * (y2 - 0.12f)) - 0.22f;
        float c2 = Mathf.Sqrt((x - 0.2f) * (x - 0.2f) + (y2 - 0.12f) * (y2 - 0.12f)) - 0.22f;
        float tri = Mathf.Max(Mathf.Abs(x) - (y2 + 0.44f) * 0.95f, y2 - 0.14f);
        return A(Mathf.Min(Mathf.Min(c1, c2), tri));
    }, "MrpCardHeart");

    internal static Sprite Gem => _gem ??= FxSprites.Rect(N, N, (x, y) => A(Mathf.Abs(x) * 1.25f + Mathf.Abs(y) - 0.46f), "MrpCardGem");

    // 攻撃: 8 本の尖りの星
    internal static Sprite Strike => _strike ??= FxSprites.Rect(N, N, (x, y) =>
    {
        float r = Mathf.Sqrt(x * x + y * y), a = Mathf.Atan2(y, x);
        float edge = 0.3f + 0.15f * Mathf.Pow(Mathf.Abs(Mathf.Cos(a * 4f)), 3f);
        return A((r - edge) * 0.8f);
    }, "MrpCardStrike");

    // 守り: 盾
    internal static Sprite Guard => _guard ??= FxSprites.Rect(N, N, (x, y) =>
    {
        float half = y > 0.05f ? 0.36f : 0.36f * Mathf.Sqrt(Mathf.Max(0f, (y + 0.45f) / 0.5f));
        float d = Mathf.Max(Mathf.Abs(x) - half, Mathf.Max(y - 0.4f, -0.45f - y));
        return A(d);
    }, "MrpCardGuard");

    // カウンター: 欠けた輪と矢じり
    internal static Sprite Counter => _counter ??= FxSprites.Rect(N, N, (x, y) =>
    {
        float r = Mathf.Sqrt(x * x + y * y), a = Mathf.Atan2(y, x);
        float ring = Mathf.Abs(r - 0.3f) - 0.07f;
        if (a > 0.2f && a < 1.2f) ring = 1f;
        // 矢じり (輪の切れ目の先)
        float hx = x - 0.3f, hy = y - 0.06f;
        float head = Mathf.Max(-hy, Mathf.Abs(hx) * 1.1f + hy - 0.18f);
        return A(Mathf.Min(ring, head));
    }, "MrpCardCounter");

    // 貫き: 右向きの矢
    internal static Sprite Pierce => _pierce ??= FxSprites.Rect(N, N, (x, y) =>
    {
        float shaft = Mathf.Max(Mathf.Abs(y) - 0.06f, Mathf.Max(-0.42f - x, x - 0.1f));
        float head = Mathf.Max(0.02f - x, Mathf.Abs(y) * 0.9f + x - 0.42f);
        float tail = Mathf.Max(Mathf.Max(-0.44f - x, x + 0.26f), Mathf.Abs(Mathf.Abs(y) - (x + 0.44f) * 0.6f - 0.04f) - 0.05f);
        return A(Mathf.Min(Mathf.Min(shaft, head), tail));
    }, "MrpCardPierce");

    // 手当: 十字
    internal static Sprite Heal => _heal ??= FxSprites.Rect(N, N, (x, y) =>
        A(Mathf.Min(Mathf.Max(Mathf.Abs(x) - 0.12f, Mathf.Abs(y) - 0.38f), Mathf.Max(Mathf.Abs(y) - 0.12f, Mathf.Abs(x) - 0.38f))), "MrpCardHeal");

    // 吸収: しずく
    internal static Sprite Drain => _drain ??= FxSprites.Rect(N, N, (x, y) =>
    {
        float c = Mathf.Sqrt(x * x + (y + 0.12f) * (y + 0.12f)) - 0.26f;
        float tip = Mathf.Max(Mathf.Abs(x) * 1.6f + (y - 0.42f) * 0.75f, -y - 0.05f);
        return A(Mathf.Min(c, tip));
    }, "MrpCardDrain");

    // ためる: 上向きの山 2 つ
    internal static Sprite Charge => _charge ??= FxSprites.Rect(N, N, (x, y) =>
    {
        float c1 = Mathf.Max(Mathf.Abs(Mathf.Abs(x) * 0.9f + (y - 0.25f)) - 0.08f, Mathf.Max(Mathf.Abs(x) - 0.4f, 0f));
        float c2 = Mathf.Max(Mathf.Abs(Mathf.Abs(x) * 0.9f + (y + 0.1f)) - 0.08f, Mathf.Max(Mathf.Abs(x) - 0.4f, 0f));
        return A(Mathf.Min(c1, c2));
    }, "MrpCardCharge");

    // 裏の印: 菱形の中に菱形
    internal static Sprite Mark => _mark ??= FxSprites.Rect(N, N, (x, y) =>
    {
        float d = Mathf.Abs(x) + Mathf.Abs(y);
        return A(Mathf.Min(Mathf.Abs(d - 0.38f) - 0.05f, d - 0.16f));
    }, "MrpCardMark");

    internal static (UnityEngine.Color color, Sprite icon) Look(byte id)
    {
        if (id == CardBook.Charge) return (new UnityEngine.Color(0.95f, 0.85f, 0.45f, 1f), Charge);
        return CardBook.Get(id).Kind switch
        {
            CardKind.Strike => (new UnityEngine.Color(0.95f, 0.38f, 0.28f, 1f), Strike),
            CardKind.Guard => (new UnityEngine.Color(0.35f, 0.6f, 1f, 1f), Guard),
            CardKind.Counter => (new UnityEngine.Color(0.72f, 0.45f, 1f, 1f), Counter),
            CardKind.Pierce => (new UnityEngine.Color(0.25f, 0.85f, 0.75f, 1f), Pierce),
            CardKind.Heal => (new UnityEngine.Color(0.4f, 0.9f, 0.4f, 1f), Heal),
            _ => (new UnityEngine.Color(0.95f, 0.5f, 0.8f, 1f), Drain),
        };
    }
}

// バトルの間は票を入れられない (名札は隠すが、念のため確定も止める)
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Confirm))]
internal static class CardDuelVoteBlock
{
    [HarmonyPriority(Priority.First)]
    public static bool Prefix() => !CardDuelHost.Busy;
}
