using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace MoreRolesPlus.Menu;

// ランクマッチの見た目 (色・板・字) をまとめる。メニューのボタンと同じ「紺の板 + 水色の差し色」
internal static class RankStyle
{
    public static readonly Color Panel = new(0.09f, 0.11f, 0.20f, 0.92f);
    public static readonly Color PanelEdge = new(0.22f, 0.30f, 0.44f, 1f);
    public static readonly Color Accent = new(0.30f, 0.92f, 0.82f, 1f);
    public static readonly Color Sub = new(0.74f, 0.80f, 0.90f, 1f);
    public static readonly Color Warn = new(1f, 0.55f, 0.45f, 1f);
    public static readonly Color BarBack = new(0.05f, 0.06f, 0.12f, 1f);

    public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

    // 縁の付いた板 (縁の板の上に一回り小さい中の板)
    public static SpriteRenderer Box(Transform parent, string name, Vector2 size, Vector3 pos, int order, Color? fill = null)
    {
        var edge = MenuArt.Panel(parent, name + "Edge", 0.16f, size, pos + new Vector3(0f, 0f, 0.02f), PanelEdge, null);
        edge.sortingOrder = order;
        var body = MenuArt.Panel(parent, name, 0.14f, size - new Vector2(0.05f, 0.05f), pos, fill ?? Panel, null);
        body.sortingOrder = order + 1;
        return body;
    }

    public static TextMeshPro Text(Transform parent, string name, Vector3 pos, Vector2 size, float fontSize, TextAlignmentOptions align, int order, TextMeshPro fontFrom)
    {
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        var t = go.AddComponent<TextMeshPro>();
        if (fontFrom) { t.font = fontFrom.font; t.fontSharedMaterial = fontFrom.fontSharedMaterial; }
        t.fontSize = fontSize;
        t.alignment = align;
        t.enableWordWrapping = false;
        t.rectTransform.sizeDelta = size;
        t.sortingOrder = order;
        return t;
    }

    public static SpriteRenderer Sprite(Transform parent, string name, Sprite sprite, Vector3 pos, float scale, int order)
    {
        var sr = new GameObject(name) { layer = parent.gameObject.layer }.AddComponent<SpriteRenderer>();
        sr.transform.SetParent(parent, false);
        sr.transform.localPosition = pos;
        sr.transform.localScale = Vector3.one * scale;
        sr.sprite = sprite;
        sr.sortingOrder = order;
        return sr;
    }

    // 横棒 (左から伸びる)。k = 0〜1
    public static void SetBar(SpriteRenderer fill, float fullWidth, float height, float left, float y, float k)
    {
        float w = Mathf.Max(0.04f, fullWidth * Mathf.Clamp01(k));
        fill.size = new Vector2(w, height);
        fill.transform.localPosition = new Vector3(left + w * 0.5f, y, fill.transform.localPosition.z);
    }
}

// メニューの画面の上の真ん中の知らせ (探している・入っている・うまくいかなかった)。窓を開かずにそのまま部屋へ飛ぶ
internal static class RankedToast
{
    private static GameObject _root;
    private static TextMeshPro _text;
    private static string _body = "";
    private static bool _error;
    private static float _until = -1f, _since;

    public static void Show(string body, bool error)
    {
        _body = body;
        _error = error;
        _since = Time.realtimeSinceStartup;
        _until = error ? _since + 5f : -1f;
    }

    public static void Hide()
    {
        _body = "";
        if (_root) _root.SetActive(false);
    }

    internal static void Tick()
    {
        if (_body.Length == 0) return;
        float now = Time.realtimeSinceStartup;
        if (_until > 0f && now > _until) { Hide(); return; }
        var menu = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
        var cam = Camera.main;
        if (!menu || !cam) { if (_root) _root.SetActive(false); return; }
        if (!_root || _root.transform.parent != menu.transform) Build(menu.transform);
        if (!_root) return;
        if (!_root.activeSelf) _root.SetActive(true);
        var p = cam.ViewportToWorldPoint(new Vector3(0.5f, 0.83f, 0f));
        _root.transform.position = new Vector3(p.x, p.y, -60f);
        string dots = _error ? "" : new string('.', 1 + (int)((now - _since) * 3f) % 3);
        string color = _error ? RankStyle.Hex(RankStyle.Warn) : "FFFFFF";
        _text.text = $"<color=#{RankStyle.Hex(RankStyle.Accent)}><b>{new Text("Ranked.Title")}</b></color>   <color=#{color}>{_body}{dots}</color>";
    }

    private static void Build(Transform parent)
    {
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = new GameObject("MrpRankedToast") { layer = parent.gameObject.layer };
        _root.transform.SetParent(parent, false);
        RankStyle.Box(_root.transform, "Back", new Vector2(7.6f, 0.62f), Vector3.zero, 200);
        _text = RankStyle.Text(_root.transform, "Text", new Vector3(0f, 0f, -0.05f), new Vector2(7.3f, 0.6f), 1.9f, TextAlignmentOptions.Center, 203, Ranked.FontFrom(parent));
    }
}

// 自分のランクのゲージ (印・段の名前・数・次の段までの進み・勝ち負け)。
// ランクマッチのロビーでは左上、終了画面では右上 (左上は試合の結果の一覧があるので)
internal static class RankHud
{
    private static GameObject _root;
    private static TextMeshPro _title, _sub;
    private static SpriteRenderer _fill, _emblem;
    private const float W = 4.4f, H = 1.3f, BarW = 2.75f, BarH = 0.13f, BarLeft = -0.75f, BarY = -0.12f;
    private static int _seen = -1;
    private static bool _end;

    internal static void Hide()
    {
        if (_root && _root.activeSelf && !_end) _root.SetActive(false);
    }

    internal static void Show()
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (!_root || _end) Build(hud.transform, false);
        if (!_root) return;
        if (!_root.activeSelf) _root.SetActive(true);
        var cam = hud.UICamera;
        if (cam)
        {
            var p = cam.ViewportToWorldPoint(new Vector3(0.012f, 0.975f, 0f));
            float s = _root.transform.lossyScale.x;
            _root.transform.position = new Vector3(p.x + W * 0.5f * s, p.y - H * 0.5f * s, hud.transform.position.z - 40f);
        }
        if (_seen != MrpAccount.Version) Refresh();
    }

    internal static void ShowEnd(EndGameManager egm)
    {
        Build(egm.transform, true);
        var cam = Camera.main;
        if (cam && _root)
        {
            var p = cam.ViewportToWorldPoint(new Vector3(0.988f, 0.975f, 0f));
            float s = _root.transform.lossyScale.x;
            _root.transform.position = new Vector3(p.x - W * 0.5f * s, p.y - H * 0.5f * s, egm.transform.position.z - 20f);
        }
        Refresh();
    }

    // 終了画面では、サーバーが結果を決めて数が変わったら描き直す
    internal static void Watch()
    {
        if (_root && _root.activeInHierarchy && _seen != MrpAccount.Version) Refresh();
    }

    private static void Build(Transform parent, bool end)
    {
        if (_root) UnityEngine.Object.Destroy(_root);
        _end = end;
        _root = new GameObject("MrpRankHud") { layer = parent.gameObject.layer };
        _root.transform.SetParent(parent, false);
        var t = _root.transform;
        RankStyle.Box(t, "Back", new Vector2(W, H), new Vector3(0f, 0f, 0.1f), 60);
        // 左の細い差し色
        var accent = MenuArt.Panel(t, "Accent", 0.03f, new Vector2(0.06f, H - 0.3f), new Vector3(-W * 0.5f + 0.1f, 0f, 0.05f), RankStyle.Accent, null);
        accent.sortingOrder = 62;
        _emblem = RankStyle.Sprite(t, "Emblem", RankEmblem.Get(0), new Vector3(-W * 0.5f + 0.72f, 0f, 0f), 1.0f, 63);
        var font = Ranked.FontFrom(parent);
        _title = RankStyle.Text(t, "Title", new Vector3(BarLeft + BarW * 0.5f, 0.27f, -0.02f), new Vector2(BarW, 0.4f), 2.1f, TextAlignmentOptions.Left, 64, font);
        MenuArt.Panel(t, "BarBack", 0.06f, new Vector2(BarW, BarH), new Vector3(BarLeft + BarW * 0.5f, BarY, 0f), RankStyle.BarBack, null).sortingOrder = 63;
        _fill = MenuArt.Panel(t, "Fill", 0.06f, new Vector2(BarW, BarH), new Vector3(BarLeft + BarW * 0.5f, BarY, -0.01f), Color.white, null);
        _fill.sortingOrder = 64;
        _sub = RankStyle.Text(t, "Sub", new Vector3(BarLeft + BarW * 0.5f, -0.39f, -0.02f), new Vector2(BarW, 0.3f), 1.35f, TextAlignmentOptions.Left, 64, font);
    }

    private static void Refresh()
    {
        if (!_title || !_fill) return;
        _seen = MrpAccount.Version;
        if (!MrpAccount.Ready)
        {
            _title.text = new Text("Shop.Loading").ToString();
            _sub.text = "";
            RankStyle.SetBar(_fill, BarW, BarH, BarLeft, BarY, 0f);
            return;
        }
        int tier = Mathf.Clamp(MrpAccount.Tier, 0, 5);
        var col = MrpAccount.TierColors[tier];
        _emblem.sprite = RankEmblem.Get(tier);
        string delta = "";
        int d = Ranked.Delta;
        if (_end && d != 0) delta = d > 0 ? $"  <color=#7CFF9C>+{d}</color>" : $"  <color=#FF8A80>{d}</color>";
        _title.text = $"<b><color=#{RankStyle.Hex(col)}>{MrpAccount.TierName(tier)}</color></b>  <size=80%>{MrpAccount.Rating} RP</size>{delta}";
        RankStyle.SetBar(_fill, BarW, BarH, BarLeft, BarY, MrpAccount.Progress(MrpAccount.Rating));
        _fill.color = col;
        string next;
        if (tier >= MrpAccount.TierFloor.Length - 1) next = new Text("Rank.Top").ToString();
        else
        {
            int need = Math.Max(0, MrpAccount.TierFloor[tier + 1] - MrpAccount.Rating);
            var nc = MrpAccount.TierColors[Mathf.Clamp(tier + 1, 0, 5)];
            next = new Text("Rank.Next").With("tier", $"<color=#{RankStyle.Hex(nc)}>{MrpAccount.TierName(tier + 1)}</color>").With("n", need).ToString();
        }
        _sub.text = $"<color=#{RankStyle.Hex(RankStyle.Sub)}>{next}   {new Text("Rank.Record").With("w", MrpAccount.Wins).With("l", MrpAccount.Losses)}</color>";
    }

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate))]
    private static class FramePatch
    {
        public static void Postfix()
        {
            try { Watch(); }
            catch (Exception) { }
        }
    }
}

// ランクマッチのロビーの上の真ん中の帯 (集まった人数の棒・開始までの知らせ・待ち時間のミニゲームのボタン)
internal static class LobbyBanner
{
    private static GameObject _root;
    private static TextMeshPro _head, _sub, _btnText, _score;
    private static SpriteRenderer _fill, _btnBack;
    private const float W = 4.4f, H = 1.6f, BarW = 3.9f, BarH = 0.12f, BarY = 0.2f;
    private const float HudH = 1.3f, Gap = 0.12f; // 上のランクのゲージ (RankHud) の高さと間
    private static string _last = "";

    internal static void Hide()
    {
        if (_root && _root.activeSelf) _root.SetActive(false);
    }

    internal static void Show(int players, int max, bool starting)
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (!_root) Build(hud);
        if (!_root) return;
        if (!_root.activeSelf) _root.SetActive(true);
        var cam = hud.UICamera;
        if (cam)
        {
            // 左上のランクのゲージのすぐ下 (同じ幅)
            var p = cam.ViewportToWorldPoint(new Vector3(0.012f, 0.975f, 0f));
            float s = _root.transform.lossyScale.y;
            _root.transform.position = new Vector3(p.x + W * 0.5f * s, p.y - (HudH + Gap + H * 0.5f) * s, hud.transform.position.z - 40f);
        }
        string head = $"<b><color=#{RankStyle.Hex(RankStyle.Accent)}>{new Text("Ranked.Title")}</color></b>   {new Text("Ranked.Players").With("n", players).With("max", max)}";
        string sub = starting ? new Text("Ranked.Starting").ToString() : new Text("Ranked.Wait").With("n", Math.Max(0, max - players)).ToString();
        string key = head + sub;
        if (key != _last)
        {
            _last = key;
            _head.text = head;
            _sub.text = $"<color=#{RankStyle.Hex(RankStyle.Sub)}>{sub}</color>";
            RankStyle.SetBar(_fill, BarW, BarH, -BarW * 0.5f, BarY, max > 0 ? players / (float)max : 0f);
        }
        _btnText.text = new Text(RankedPastime.Active ? "Ranked.PastimeStop" : "Ranked.Pastime").ToString();
        _score.gameObject.SetActive(RankedPastime.Active);
        if (RankedPastime.Active) _score.text = RankedPastime.ScoreText();
    }

    private static void Build(HudManager hud)
    {
        _root = new GameObject("MrpRankedBanner") { layer = hud.gameObject.layer };
        var t = _root.transform;
        t.SetParent(hud.transform, false);
        RankStyle.Box(t, "Back", new Vector2(W, H), new Vector3(0f, 0f, 0.1f), 60);
        var font = hud.UseButton ? hud.UseButton.buttonLabelText : Ranked.FontFrom(hud.transform);
        _head = RankStyle.Text(t, "Head", new Vector3(0f, 0.5f, -0.02f), new Vector2(W - 0.3f, 0.4f), 1.9f, TextAlignmentOptions.Center, 64, font);
        MenuArt.Panel(t, "BarBack", 0.06f, new Vector2(BarW, BarH), new Vector3(0f, BarY, 0f), RankStyle.BarBack, null).sortingOrder = 63;
        _fill = MenuArt.Panel(t, "Fill", 0.06f, new Vector2(BarW, BarH), new Vector3(0f, BarY, -0.01f), RankStyle.Accent, null);
        _fill.sortingOrder = 64;
        _sub = RankStyle.Text(t, "Sub", new Vector3(0f, -0.1f, -0.02f), new Vector2(W - 0.3f, 0.34f), 1.45f, TextAlignmentOptions.Center, 64, font);

        // ミニゲームのボタン (下の真ん中)
        var bgo = new GameObject("PastimeButton") { layer = _root.layer };
        bgo.transform.SetParent(t, false);
        bgo.transform.localPosition = new Vector3(0f, -0.5f, -0.03f);
        var size = new Vector2(2.8f, 0.42f);
        _btnBack = bgo.AddComponent<SpriteRenderer>();
        _btnBack.sprite = MenuSkin.Button(false);
        _btnBack.drawMode = SpriteDrawMode.Sliced;
        _btnBack.size = size;
        _btnBack.sortingOrder = 65;
        bgo.AddComponent<BoxCollider2D>().size = size;
        _btnText = RankStyle.Text(bgo.transform, "Text", new Vector3(0f, 0f, -0.02f), size, 1.4f, TextAlignmentOptions.Center, 66, font);
        var pb = bgo.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        pb.OnClick.AddListener((UnityAction)RankedPastime.Toggle);
        // 乗せた時の処理は 1 回だけ作って使い回す (作るたびに管理ラムダを渡すと手放されずに残る)
        _btnSize = size;
        _onOver ??= (UnityAction)(() => Hover(true));
        _onOut ??= (UnityAction)(() => Hover(false));
        pb.OnMouseOver.AddListener(_onOver);
        pb.OnMouseOut.AddListener(_onOut);

        // ミニゲームの点 (帯の下)
        _score = RankStyle.Text(t, "Score", new Vector3(0f, -H * 0.5f - 0.25f, -0.02f), new Vector2(W + 1f, 0.36f), 1.6f, TextAlignmentOptions.Center, 64, font);
        _score.gameObject.SetActive(false);
    }

    private static Vector2 _btnSize;
    private static UnityAction _onOver, _onOut;

    private static void Hover(bool on)
    {
        if (!_btnBack) return;
        _btnBack.sprite = MenuSkin.Button(on);
        _btnBack.size = _btnSize;
    }
}

// ロビーで皆の頭の上 (名前の上) にランクの印と数を出す。ランクマッチの部屋だけ (MRP のサーバーで結ばれた人)
internal static class RankTags
{
    private const string TagName = "MrpRankTag";
    private static float _at;
    private static readonly List<GameObject> Shown = new();

    internal static void Tick()
    {
        if (Time.unscaledTime < _at) return;
        _at = Time.unscaledTime + 0.3f;
        foreach (var go in Shown) if (go) go.SetActive(false);
        Shown.Clear();
        if (!Ranked.InRankedRoom || !LobbyBehaviour.Instance || ShipStatus.Instance) return;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null || !p.cosmetics || !p.cosmetics.nameText) continue;
            var r = MrpAccount.RankOf(p);
            if (r == null) continue;
            var tag = Tag(p);
            if (!tag) continue;
            var tmp = tag.GetComponentInChildren<TextMeshPro>(true);
            var icon = tag.GetComponentInChildren<SpriteRenderer>(true);
            int tier = Mathf.Clamp(r.Tier, 0, 5);
            string text = $"<color=#{MrpAccount.TierColorHex(tier)}>{r.Rating}</color>";
            if (tmp && tmp.text != text) tmp.text = text;
            if (icon) icon.sprite = RankEmblem.Get(tier);
            tag.SetActive(true);
            Shown.Add(tag);
        }
    }

    private static GameObject Tag(PlayerControl p)
    {
        var name = p.cosmetics.nameText;
        var parent = name.transform.parent ? name.transform.parent : name.transform;
        var t = parent.Find(TagName);
        if (t) return t.gameObject;
        var go = new GameObject(TagName) { layer = name.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = name.transform.localPosition + new Vector3(0f, 0.3f, -0.01f);
        var nr = name.GetComponent<MeshRenderer>();
        var icon = new GameObject("Emblem") { layer = go.layer }.AddComponent<SpriteRenderer>();
        icon.transform.SetParent(go.transform, false);
        icon.transform.localPosition = new Vector3(-0.2f, 0f, 0f);
        icon.transform.localScale = Vector3.one * 0.26f;
        if (nr) { icon.sortingLayerID = nr.sortingLayerID; icon.sortingOrder = nr.sortingOrder + 1; }
        var tgo = new GameObject("Text") { layer = go.layer };
        tgo.transform.SetParent(go.transform, false);
        tgo.transform.localPosition = new Vector3(0.18f, 0f, 0f);
        var tmp = tgo.AddComponent<TextMeshPro>();
        tmp.font = name.font;
        tmp.fontSharedMaterial = name.fontSharedMaterial;
        tmp.fontSize = name.fontSize * 0.72f;
        tmp.enableAutoSizing = false;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = new Vector2(0.6f, 0.3f);
        var mr = tgo.GetComponent<MeshRenderer>();
        if (mr && nr) { mr.sortingLayerID = nr.sortingLayerID; mr.sortingOrder = nr.sortingOrder + 1; }
        go.SetActive(false);
        return go;
    }
}
