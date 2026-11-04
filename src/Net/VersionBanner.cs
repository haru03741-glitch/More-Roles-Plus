using System.Text;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Net;

// 版の違い・MRP の無い人を、ロビーの画面の上の真ん中に赤い帯で出し続ける (チャットの知らせは流れて見落とすため)。
//   ホスト: 始められない人の名前と理由の一覧 (確認中の人は黄色)
//   客: ホストと版が違う時、その理由 (自分の画面だけ)
// 揃ったら消える。試合中は出さない
internal static class VersionBanner
{
    private const int Every = 25; // FixedUpdate 25 回ごと (0.5 秒)
    private static int _tick;
    private static GameObject _root;
    private static TextMeshPro _text;
    private static SpriteRenderer _back;
    private static Sprite _sprite;
    private static string _mine; // 客の側: ホストと違う理由 (無ければ null)

    private static bool _hostSeen;  // ホストから版の返事が来た
    private static float _joinedAt;
    private const float HostWait = 12f; // これだけ待ってもホストから返事が無ければ、ホストに MRP が無いと見る

    internal static void SetClientReason(string reason) => _mine = reason;
    internal static void HostSeen() => _hostSeen = true;
    internal static void Clear()
    {
        _mine = null;
        _hostSeen = false;
        _joinedAt = Time.realtimeSinceStartup;
    }

    private static string ClientText()
    {
        if (_mine != null) return $"<b><color=#FFD0D0>{new Text("VersionBanner.ClientTitle")}</color></b>\n{_mine}";
        if (!_hostSeen && Time.realtimeSinceStartup - _joinedAt > HostWait) return $"<b><color=#FFD0D0>{new Text("VersionBanner.ClientTitle")}</color></b>\n{new Text("VersionBanner.HostNoMrp")}";
        return null;
    }

    internal static void Tick()
    {
        if (++_tick % Every != 0) return;
        try { Step(); }
        catch (System.Exception e) { Plugin.Logger.LogError($"version banner: {e.Message}"); }
    }

    private static void Step()
    {
        var hud = Vanilla.Hud;
        var client = AmongUsClient.Instance;
        string body = null;
        if (hud && client && LobbyBehaviour.Instance && client.GameState == InnerNet.InnerNetClient.GameStates.Joined)
            body = client.AmHost ? HostText() : ClientText();
        if (string.IsNullOrEmpty(body))
        {
            if (_root && _root.activeSelf) _root.SetActive(false);
            return;
        }
        if (!_root) Build(hud);
        if (!_root) return;
        if (!_root.activeSelf) _root.SetActive(true);
        Place(hud);
        if (_text.text == body) return;
        _text.text = body;
        _text.ForceMeshUpdate();
        var size = _text.GetRenderedValues(false);
        float w = Mathf.Max(size.x + 0.6f, 3f), h = size.y + 0.4f;
        _back.transform.localScale = new Vector3(h, h, 1f);
        _back.drawMode = SpriteDrawMode.Sliced;
        _back.size = new Vector2(w / h, 1f);
    }

    private static string HostText()
    {
        var list = VersionCheck.Blockers(out int checking);
        if (list.Count == 0) return null;
        var sb = new StringBuilder();
        sb.Append("<b><color=#FFD0D0>").Append(new Text("VersionBanner.HostTitle")).Append("</color></b>");
        foreach (var line in list) sb.Append('\n').Append("・").Append(line);
        if (checking > 0 && checking == list.Count) return $"<color=#FFE07A>{new Text("VersionBanner.Checking")}</color>";
        sb.Append("\n<size=80%><color=#E0E0E0>").Append(new Text("VersionBanner.HostHint")).Append("</color></size>");
        return sb.ToString();
    }

    private static void Build(HudManager hud)
    {
        var src = hud.UseButton ? hud.UseButton.buttonLabelText : null;
        if (!src) return;
        _root = new GameObject("MrpVersionBanner") { layer = hud.gameObject.layer };
        _root.transform.SetParent(hud.transform, false);

        _back = new GameObject("Back") { layer = _root.layer }.AddComponent<SpriteRenderer>();
        _back.transform.SetParent(_root.transform, false);
        _back.transform.localPosition = new Vector3(0f, 0f, 0.1f);
        _back.sprite = _sprite ??= Panel();
        _back.color = new Color(0.45f, 0.05f, 0.08f, 0.88f);
        _back.sortingOrder = 50;

        var go = Object.Instantiate(src.gameObject, _root.transform);
        go.name = "Text";
        var tr = go.GetComponent<TextTranslatorTMP>();
        if (tr) Object.Destroy(tr);
        _text = go.GetComponent<TextMeshPro>();
        _text.transform.localPosition = Vector3.zero;
        _text.transform.localScale = Vector3.one;
        _text.enableAutoSizing = false;
        _text.fontSize = 2.2f;
        _text.alignment = TextAlignmentOptions.Center;
        _text.enableWordWrapping = true;
        _text.color = Color.white;
        _text.outlineWidth = 0.15f;
        _text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _text.rectTransform.sizeDelta = new Vector2(9f, 1f);
        _text.sortingOrder = 51;
        _text.text = "";
    }

    // 上の段のボタンの下・画面の真ん中
    private static void Place(HudManager hud)
    {
        var cam = hud.UICamera;
        if (!cam) return;
        var p = cam.ViewportToWorldPoint(new Vector3(0.42f, 0.74f, 0f));
        _root.transform.position = new Vector3(p.x, p.y, hud.transform.position.z - 50f);
    }

    // 角の丸い板 (9 分割で伸ばす)
    private static Sprite Panel()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpVersionBannerBack", wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        const float r = 16f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = Mathf.Max(Mathf.Max(r - x - 0.5f, x + 0.5f - (n - r)), 0f);
            float dy = Mathf.Max(Mathf.Max(r - y - 0.5f, y + 0.5f - (n - r)), 0f);
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            byte a = (byte)(Mathf.Clamp01(r - d) * 255f);
            px[y * n + x] = new Color32(255, 255, 255, a);
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }
}
