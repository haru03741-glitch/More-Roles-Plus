using System;
using AmongUs.Data;
using HarmonyLib;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// ロビーの部屋コードの「コピー」の右に、シェアのボタンを足す。押すと参加リンク (https://…/mrp/join/<コード>) をコピーする。
// リンクを開くとブラウザーからインストーラー (mrp://join/…) 経由でゲームが開いてその部屋に入る (Net.JoinLink)。
// リンクの行き先は MRP のサーバーなので、MRP のサーバーの部屋の時だけ出す
[HarmonyPatch]
internal static class RoomShare
{
    private const string ButtonName = "MrpShareLinkButton";
    private const float Gap = 0.62f; // コピーとシェアのボタンの間 (ボタンの幅ほど)

    private static Sprite _icon;
    internal static bool ForceShow; // 試験用 (LAN の部屋でも出す)

    // 試験用 (ブリッジ): roomshare show | hide | click
    internal static void RegisterBridge()
    {
        Bridge.TestBridge.Register("roomshare", "show | hide | click ロビーの部屋コードのシェアのボタン (show = LAN の部屋でも出す)", (args, reply) =>
        {
            switch (args.Trim())
            {
                case "show": ForceShow = true; break;
                case "hide": ForceShow = false; break;
                case "click": Share(); break;
                case "public": AmongUsClient.Instance?.ChangeGamePublic(true); break;
                case "private": AmongUsClient.Instance?.ChangeGamePublic(false); break;
            }
            reply($"OK roomshare force={ForceShow} clip={GUIUtility.systemCopyBuffer}");
        });
    }

    public static string Link(string code) => Net.MrpRegion.WebBase + "/mrp/join/" + code;

    [HarmonyPatch(typeof(LobbyInfoPane), nameof(LobbyInfoPane.Update)), HarmonyPostfix]
    public static void Update(LobbyInfoPane __instance)
    {
        try { Sync(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"room share: {e.Message}"); }
    }

    private static Transform _share;

    private static void Sync(LobbyInfoPane pane)
    {
        var copy = pane ? pane.CopyCodeButton : null;
        if (!copy) return;
        // 毎フレーム探さない: 作った物を控えて、消えた時だけ探し直す
        var share = _share;
        if (!share || share.parent != copy.transform.parent)
        {
            share = copy.transform.parent.Find(ButtonName);
            if (!share) share = Make(copy);
            _share = share;
        }
        if (!share) return;
        var c = AmongUsClient.Instance;
        bool online = c && c.NetworkMode == NetworkModes.OnlineGame && c.GameId != 0 && Net.MrpRegion.IsCurrent();
        bool show = ForceShow || copy.gameObject.activeSelf && online;
        if (ForceShow && !copy.gameObject.activeSelf) copy.gameObject.SetActive(true);
        if (share.gameObject.activeSelf != show) share.gameObject.SetActive(show);
        // シェアは本編のコピーの元の場所 (板の右端) に置き、出ている間はコピーをその左へずらす (コードとの間は空いている)
        var p = copy.transform.localPosition;
        float x = show ? share.localPosition.x - Gap : share.localPosition.x;
        if (!Mathf.Approximately(p.x, x)) copy.transform.localPosition = new Vector3(x, p.y, p.z);
    }

    private static Transform Make(UiElement copy)
    {
        var go = UnityEngine.Object.Instantiate(copy.gameObject, copy.transform.parent);
        go.name = ButtonName;
        go.transform.localPosition = copy.transform.localPosition;
        // 本編の「隠す」の仕組みは写さない (出す・隠すはこちらで決める)
        foreach (var h in go.GetComponents<ConditionalHide>()) UnityEngine.Object.Destroy(h);
        var pb = go.GetComponent<PassiveButton>();
        if (pb)
        {
            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            pb.OnClick.AddListener((Action)Share);
        }
        var icon = go.transform.Find("Icon")?.GetComponent<SpriteRenderer>();
        var src = copy.transform.Find("Icon")?.GetComponent<SpriteRenderer>();
        if (icon)
        {
            icon.sprite = _icon ??= ShareIcon();
            // コピーの絵と同じ大きさ (高さ) に揃える
            float h = src && src.sprite ? src.sprite.bounds.size.y * src.transform.localScale.y : 0.42f;
            icon.transform.localScale = new Vector3(h, h, 1f);
            icon.color = src ? src.color : Color.white; // コピーの絵と同じ白
        }
        return go.transform;
    }

    private static void Share()
    {
        var c = AmongUsClient.Instance;
        if (!c || c.GameId == 0) return;
        string code = InnerNet.GameCode.IntToGameName(c.GameId);
        string url = Link(code);
        GUIUtility.systemCopyBuffer = url;
        var pane = Vanilla.LobbyInfo;
        if (pane && pane.CopyCodeSound) SoundManager.Instance.PlaySound(pane.CopyCodeSound, false);
        bool streamer = DataManager.Settings != null && DataManager.Settings.Gameplay != null && DataManager.Settings.Gameplay.StreamerMode;
        ChatNotes.Post(new Text("RoomShare.Copied").With("url", streamer ? "******" : url));
    }

    // シェアの印: 3 つの丸を 2 本の線でつなぐ (右上・左・右下)
    private static Sprite ShareIcon()
    {
        var a = new Vector2(0.24f, 0.3f);
        var b = new Vector2(-0.24f, 0f);
        var d = new Vector2(0.24f, -0.3f);
        const float r = 0.13f, line = 0.045f, aa = 0.012f;
        return FxSprites.Rect(128, 128, (x, y) =>
        {
            var p = new Vector2(x, y);
            float dist = Mathf.Min(Mathf.Min((p - a).magnitude, (p - b).magnitude), (p - d).magnitude) - r;
            dist = Mathf.Min(dist, Segment(p, a, b) - line);
            dist = Mathf.Min(dist, Segment(p, d, b) - line);
            return 1f - Mathf.Clamp01((dist + aa) / (2f * aa));
        }, "MrpShareIcon");
    }

    private static float Segment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return (p - (a + ab * t)).magnitude;
    }
}
