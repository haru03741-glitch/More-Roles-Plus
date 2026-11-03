using System;
using System.Text;
using HarmonyLib;
using MoreRolesPlus.Fx;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// ホーム画面 (メインメニュー) を見やすくする。
//   右の窓の下に MRP の帯 (名前・版・接続先) を敷き、「リンク・コードで参加」と「バグ報告・お問い合わせ」を並べる。
//   リンクで参加は、コピーしたリンク (…/mrp/join/<コード>・mrp://join/<コード>) か部屋コードを読んで MRP のサーバーの部屋に入る
//   (非公開の部屋も入れる。PC はコピーしてある間ボタンにコードを出す)。
//   本編の MOD の印は友達のボタンに重なるので左下 (版の字の右) へ移し、版の字は少し大きくする。
// 更新のボタン (ModUpdate) は帯の上の右に出る
[HarmonyPatch]
internal static class HomeScreen
{
    // 右の窓 (RightPanel) の中の位置。窓の見える所はおよそ x -4.45〜3.65・y -2.55〜2.9 だが、
    // 右下は本編の赤い × の飾りが窓の手前に重なるので、帯は x 1.95 までにする。
    // ボタンは本編のニュースのボタンの写し (幅およそ ButtonW) を 2 つ並べる。
    // 窓の中を漂う乗組員より手前に出すため、帯もボタンも z を手前にする (ボタンの絵は本体より 1 手前にある)
    private const float BarX = -1.2f, BarY = -1.86f, BarW = 6.3f, BarH = 1.22f, BarZ = -2.05f;
    private const float TitleX = -4.2f, SubX = 1.8f, TitleY = -1.55f, TextZ = -3f;
    private const float RowY = -2.14f, JoinX = -2.73f, ReportX = 0.33f, ButtonW = 2.95f, ButtonZ = -1.3f;
    internal const float UpdateX = 0.33f, UpdateY = -0.95f;
    private const float StampScale = 0.55f;
    private static readonly Vector3 StampAt = new(0.255f, 0.026f, 0f); // 画面の割合 (版の字の右)
    private const float VersionScale = 1.25f;
    private const int VisibilityEvery = 10;
    private const int ClipEvery = 50; // コピーした物を見直す間 (FixedUpdate の回数・およそ 1 秒)
    private const float StatusSeconds = 4f;

    private static readonly Color BarColor = new(0.04f, 0.06f, 0.08f, 0.82f);
    private static readonly Color LineColor = new(0.5f, 0.83f, 1f, 0.9f);

    private static MainMenuManager _menu;
    private static GameObject _root;
    private static Transform _screens, _credits;
    private static TextMeshPro _title, _sub, _joinText;
    private static bool _made;
    private static int _tick;
    private static string _clipCode, _status;
    private static float _statusUntil;
    private static Sprite _barSprite, _linkIcon;
    private static UnityAction _onJoin, _onReport;

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start)), HarmonyPostfix]
    public static void MenuStart(MainMenuManager __instance)
    {
        _menu = __instance;
        _made = false;
        _root = null;
        _clipCode = null;
    }

    // 版の字 (左下) を少し大きくする
    [HarmonyPatch(typeof(VersionShower), nameof(VersionShower.Start)), HarmonyPostfix]
    public static void VersionStart(VersionShower __instance)
    {
        if (!__instance || !Object.FindObjectOfType<MainMenuManager>()) return;
        var t = __instance.transform;
        t.localScale = t.localScale * VersionScale;
    }

    // 本編の MOD の印はメニューでは右上 (友達のボタンの上) に出るので、左下の版の字の右へ移す
    [HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate)), HarmonyPostfix]
    public static void StampLate(ModManager __instance)
    {
        var stamp = __instance ? __instance.ModStamp : null;
        if (!stamp) return;
        if (_menu is null || !_menu)
        {
            // メニューを出たら大きさを戻す (位置は本編が毎フレーム決め直す)
            if (_stampScale.HasValue) { stamp.transform.localScale = _stampScale.Value; _stampScale = null; }
            return;
        }
        var cam = __instance.localCamera ? __instance.localCamera : Camera.main;
        if (!cam || !stamp.gameObject.activeInHierarchy) return;
        _stampScale ??= stamp.transform.localScale;
        stamp.transform.localScale = _stampScale.Value * StampScale;
        var p = cam.ViewportToWorldPoint(StampAt);
        stamp.transform.position = new Vector3(p.x, p.y, stamp.transform.position.z);
    }

    private static Vector3? _stampScale;

    // FixedUpdate から
    public static void Tick()
    {
        if (_menu is null || ++_tick % VisibilityEvery != 0) return;
        if (!_menu)
        {
            _menu = null;
            _root = null;
            return;
        }
        if (!_made) Make();
        if (!_root) return;
        // 遊び方の選択・クレジットなど、右の窓に別の物が出ている間は隠す
        bool busy = _credits && _credits.gameObject.activeSelf;
        if (!busy && _screens)
        {
            for (int i = 0, n = _screens.childCount; i < n && !busy; i++)
                busy = _screens.GetChild(i).gameObject.activeSelf;
        }
        if (_root.activeSelf == busy) _root.SetActive(!busy);
        if (busy) return;
        if (_tick % ClipEvery == 0) RefreshJoin();
        RefreshSub();
    }

    private static void Make()
    {
        _made = true;
        var proto = _menu.newsButton;
        var panel = _menu.transform.Find("MainUI/AspectScaler/RightPanel");
        if (!proto || !panel) { Plugin.Logger.LogWarning("home: menu parts not found"); return; }
        _screens = panel.Find("MaskedBlackScreen");
        _credits = panel.Find("CreditsSizer/CreditsScreen");

        _root = new GameObject("MrpHome");
        _root.transform.SetParent(panel, false);
        _root.transform.localPosition = Vector3.zero;

        // ボタンの絵と同じ順で敷き、前後は z で決める (順を下げると窓の中を漂う乗組員の後ろになる)
        var protoBack = proto.GetComponentInChildren<SpriteRenderer>(true);
        int order = protoBack ? protoBack.sortingOrder : 0;
        int layer = protoBack ? protoBack.sortingLayerID : 0;

        var bar = new GameObject("Bar").AddComponent<SpriteRenderer>();
        bar.transform.SetParent(_root.transform, false);
        bar.sprite = _barSprite ??= BarSprite();
        bar.color = BarColor;
        bar.sortingLayerID = layer;
        bar.sortingOrder = order;
        bar.transform.localPosition = FxMath.V3(BarX, BarY, BarZ);
        bar.transform.localScale = new Vector3(BarH, BarH, 1f);

        var line = new GameObject("Line").AddComponent<SpriteRenderer>();
        line.transform.SetParent(_root.transform, false);
        line.sprite = bar.sprite;
        line.color = LineColor;
        line.sortingLayerID = layer;
        line.sortingOrder = order;
        line.transform.localPosition = FxMath.V3(BarX, BarY + BarH / 2f - 0.03f, BarZ - 0.01f);
        line.transform.localScale = new Vector3(BarH * 0.94f, 0.035f, 1f);

        var label = proto.transform.Find("FontPlacer/Text_TMP")?.GetComponent<TextMeshPro>();
        _title = MakeText(label, "Title", TitleX, TitleY, 2.4f, order + 1, 0f);
        _title.text = $"<color=#7FD4FF>More Roles Plus</color>  <size=72%><color=#C8D2DA>v{Plugin.Version}</color></size>";
        _sub = MakeText(label, "Sub", SubX, TitleY, 1.8f, order + 1, 1f);

        var join = CopyButton(proto, "MrpJoinLinkButton", JoinX, out _joinText);
        // 絵は普段の物と光った時の物の 2 つある。本編の絵と同じ高さの鎖の絵にする (鎖の絵は縦 1 単位)
        foreach (var icon in join.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (icon.name != "Icon") continue;
            float h = icon.sprite ? icon.sprite.bounds.size.y * icon.transform.localScale.y : 0.5f;
            icon.sprite = _linkIcon ??= LinkIcon();
            icon.transform.localScale = new Vector3(h, h, 1f);
            icon.color = new Color(0.5f, 0.83f, 1f);
        }
        _onJoin ??= (UnityAction)Join;
        ClientOptions.SetClick(join, _onJoin);

        var report = CopyButton(proto, "MrpReportButton", ReportX, out var reportText);
        if (reportText) reportText.text = new Text("Report.Open");
        FitLabel(reportText);
        _onReport ??= (UnityAction)(() => { if (!Report.ReportWindow.IsOpen) Report.ReportWindow.Open(); });
        ClientOptions.SetClick(report, _onReport);

        RefreshJoin(force: true);
        RefreshSub();
    }

    private static PassiveButton CopyButton(PassiveButton proto, string name, float x, out TextMeshPro text)
    {
        var go = Object.Instantiate(proto.gameObject, _root.transform);
        go.name = name;
        var t = go.transform;
        t.localPosition = FxMath.V3(x, RowY, ButtonZ);
        t.localScale = proto.transform.localScale;
        var news = go.GetComponent<NewsCountButton>();
        if (news) Object.Destroy(news);
        var badge = t.Find("NewItem");
        if (badge) badge.gameObject.SetActive(false);
        var label = t.Find("FontPlacer/Text_TMP");
        var tr = label ? label.GetComponent<TextTranslatorTMP>() : null;
        if (tr) Object.Destroy(tr);
        text = label ? label.GetComponent<TextMeshPro>() : null;
        var pb = go.GetComponent<PassiveButton>();
        // 幅を縮め、左の絵と右の字は縮めた分だけ内へ寄せる。
        // 元の幅は本編のボタンの枠の絵の見た目の幅で測る (画面の単位)
        float w0 = 0f;
        foreach (var sr in proto.GetComponentsInChildren<SpriteRenderer>())
            if (sr.drawMode != SpriteDrawMode.Simple) w0 = Math.Max(w0, sr.bounds.size.x);
        float w1 = ButtonW * Math.Abs(_root.transform.lossyScale.x);
        // 字と絵は本編の部品 (AspectPosition) が毎フレーム元の幅の位置へ戻すので、写しでは止める
        foreach (var ap in go.GetComponentsInChildren<AspectPosition>(true))
        {
            ap.enabled = false;
            Object.Destroy(ap);
        }
        ClientOptions.FitBack(pb, w1 / Math.Max(Math.Abs(t.lossyScale.x), 1e-4f));
        if (w0 > w1)
        {
            var shift = new Vector3((w0 - w1) / 2f, 0f, 0f);
            foreach (var sr in pb.GetComponentsInChildren<SpriteRenderer>(true))
                if (sr.name == "Icon") sr.transform.position += shift;
            if (text) text.transform.position -= shift;
        }
        if (text)
        {
            text.enableAutoSizing = false;
            _labelSize ??= text.fontSize;
        }
        return pb;
    }

    private static float? _labelSize;

    // ボタンの字を、絵の右からボタンの右端までに収まる大きさにする (本編の字の大きさより大きくはしない)
    private static void FitLabel(TextMeshPro text)
    {
        if (!text || !_labelSize.HasValue) return;
        text.fontSize = _labelSize.Value;
        text.ForceMeshUpdate();
        float avail = (ButtonW - 1.0f) * Math.Abs(_root.transform.lossyScale.x);
        float need = text.GetPreferredValues(text.text).x * Math.Abs(text.transform.lossyScale.x);
        if (need > avail && need > 0f) text.fontSize = _labelSize.Value * avail / need;
    }

    // 字体だけ本編のボタンの字から写す (写した字の物には位置を決める部品が付いていて動かせない)
    private static TextMeshPro MakeText(TextMeshPro src, string name, float x, float y, float size, int order, float pivotX)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        var t = go.AddComponent<TextMeshPro>();
        if (src)
        {
            t.font = src.font;
            t.fontSharedMaterial = src.fontSharedMaterial;
        }
        t.enableAutoSizing = false;
        t.fontSize = size;
        t.enableWordWrapping = false;
        t.overflowMode = TextOverflowModes.Overflow;
        t.alignment = pivotX > 0.5f ? TextAlignmentOptions.Right : TextAlignmentOptions.Left;
        t.color = Color.white;
        t.rectTransform.pivot = new Vector2(pivotX, 0.5f);
        t.rectTransform.sizeDelta = new Vector2(4.2f, 0.5f);
        t.sortingOrder = order;
        t.transform.localScale = Vector3.one;
        t.transform.localPosition = FxMath.V3(x, y, TextZ);
        return t;
    }

    // ── リンク・コードで参加 ────────────────────────────────

    private static void RefreshJoin(bool force = false)
    {
        // Android はコピーした物を読むたびに「貼り付けました」の知らせが出るので、押した時だけ読む
        string code = OperatingSystem.IsAndroid() ? null : Net.JoinLink.Parse(SafeClipboard());
        if (!force && code == _clipCode) return;
        _clipCode = code;
        if (_joinText) _joinText.text = code != null ? new Text("Home.JoinCode").With("code", code) : new Text("Home.JoinLink");
        FitLabel(_joinText);
    }

    private static void Join()
    {
        string code = Net.JoinLink.Parse(SafeClipboard());
        if (code == null)
        {
            Status(new Text("Home.JoinNone"));
            return;
        }
        Status(new Text("Home.Joining").With("code", code));
        Net.JoinLink.Request(code);
    }

    private static string SafeClipboard()
    {
        try { return GUIUtility.systemCopyBuffer; }
        catch { return null; }
    }

    private static void Status(string text)
    {
        _status = text;
        _statusUntil = Time.realtimeSinceStartup + StatusSeconds;
        RefreshSub();
    }

    private static void RefreshSub()
    {
        if (!_sub) return;
        string s;
        if (_status != null && Time.realtimeSinceStartup < _statusUntil) s = $"<color=#FFE07A>{_status}</color>";
        else
        {
            _status = null;
            var region = ServerManager.Instance ? ServerManager.Instance.CurrentRegion : null;
            string name = region != null ? StripTags(region.Name) : "-";
            s = $"<color=#9AA3AD>{new Text("Home.Server")}</color> <color=#{(Net.MrpRegion.IsCurrent() ? "00E676" : "FFFFFF")}>{name}</color>";
        }
        if (_sub.text != s) _sub.text = s;
    }

    private static string StripTags(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool tag = false;
        foreach (char c in s)
        {
            if (c == '<') tag = true;
            else if (c == '>') tag = false;
            else if (!tag && c != '\n') sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    // ── 絵 ────────────────────────────────────────────

    // 帯: 角の丸い長方形 (縦 1 単位・横 BarW/BarH)
    private static Sprite BarSprite()
    {
        float aspect = BarW / BarH;
        int h = 96, w = (int)(h * aspect);
        const float r = 0.16f, aa = 0.012f;
        return FxSprites.Rect(w, h, (x, y) =>
        {
            float px = Math.Abs(x * aspect), py = Math.Abs(y);
            float qx = Math.Max(px - (aspect / 2f - r), 0f), qy = Math.Max(py - (0.5f - r), 0f);
            float d = (float)Math.Sqrt(qx * qx + qy * qy) + Math.Min(Math.Max(px - (aspect / 2f - r), py - (0.5f - r)), 0f) - r;
            return 1f - FxMath.Clamp01((d + aa) / (2f * aa));
        }, "MrpHomeBar");
    }

    // 鎖の輪 2 つ (斜め)
    private static Sprite LinkIcon()
    {
        const float line = 0.06f, aa = 0.012f;
        var dir = new Vector2(0.7071f, 0.7071f);
        var n = new Vector2(-dir.y, dir.x);
        return FxSprites.Rect(128, 128, (x, y) =>
        {
            var p = new Vector2(x, y);
            float d = Math.Min(Ring(p, dir * -0.13f, dir, n), Ring(p, dir * 0.13f, dir, n));
            return 1f - FxMath.Clamp01((d - line + aa) / (2f * aa));
        }, "MrpLinkIcon");
    }

    // 角の丸い輪 (長さ 0.34・幅 0.2) の線からの距離
    private static float Ring(Vector2 p, Vector2 c, Vector2 dir, Vector2 n)
    {
        var q = p - c;
        float u = Math.Abs(Vector2.Dot(q, dir)) - 0.09f, v = Math.Abs(Vector2.Dot(q, n));
        float d = u > 0 ? new Vector2(u, v).magnitude : v;
        return Math.Abs(d - 0.11f);
    }

    // 確かめ用 (ブリッジ): home [join | dump]
    internal static void RegisterBridge()
    {
        Bridge.TestBridge.Register("home", "[join | dump] ホーム画面の MRP の帯: 状態 / リンクで参加を押す / 部品の一覧", (args, reply) =>
        {
            switch (args.Trim())
            {
                case "join": Join(); break;
                case "dump":
                    if (_root) Dump(_root.transform, 0, reply);
                    break;
            }
            reply(KeyNav.Describe());
            reply($"OK home made={_made} root={(_root ? _root.activeSelf.ToString() : "none")} clip={_clipCode ?? "-"} join=[{(_joinText ? _joinText.text : "-")}] sub=[{(_sub ? _sub.text : "-")}]");
        });
    }

    private static void Dump(Transform t, int depth, Action<string> reply)
    {
        if (!t || depth > 6) return;
        var sr = t.GetComponent<SpriteRenderer>();
        string extra = sr ? $" sr={(sr.sprite ? sr.sprite.name : "-")} size={(sr.sprite ? sr.sprite.bounds.size : Vector3.zero)} order={sr.sortingOrder} mode={sr.drawMode}" : "";
        var comps = new StringBuilder();
        foreach (var c in t.GetComponents<Component>()) if (c) comps.Append(c.GetIl2CppType().Name).Append(',');
        reply($"DUMP {new string(' ', depth * 2)}{t.name} active={t.gameObject.activeSelf} pos={t.localPosition} scale={t.localScale}{extra} [{comps}]");
        for (int i = 0; i < t.childCount; i++) Dump(t.GetChild(i), depth + 1, reply);
    }
}
