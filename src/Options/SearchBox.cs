// Based on https://github.com/Gurge44/EndlessHostRoles Patches/GameOptionsMenuPatch.cs (GPL-3.0)
using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// 設定画面の検索欄。本編のチャット入力欄を複製し、文字入力の部品 (TextBoxTMP) だけを生かして使う。
// - 複製したら、チャットへ送る部品 (FreeChatInputField・送信ボタンの処理) は動き出す前に外す。
//   残すと Enter や送信ボタンで検索語がチャットに流れたり、文字数の表示を書き換えに来たりする。
// - 文字が変わったことは TextBoxTMP.SetText の後置で受け取る (欄の OnChange に管理側のデリゲートを入れない)。
//   受け取った文字は控えに持ち、本編の欄の text を読み返さない (破棄後の読み出しで落ちることがあるため)。
// - 開くたびに作り、画面を閉じたら画面ごと破棄される。使い回さない。
internal sealed class SearchBox
{
    private static SearchBox _current;
    private static readonly Color Field = new(0.07f, 0.075f, 0.095f, 1f), FieldOver = new(0.1f, 0.11f, 0.14f, 1f);

    private readonly TextBoxTMP _text;
    private readonly TextMeshPro _hint;
    private readonly Action<string> _changed;
    public string Query { get; private set; } = "";
    public GameObject Root { get; }

    private SearchBox(GameObject root, TextBoxTMP text, TextMeshPro hint, Action<string> changed)
    {
        Root = root;
        _text = text;
        _hint = hint;
        _changed = changed;
    }

    // 幅 width・高さ height (親の座標の単位) の欄を parent の pos に作る。作れなければ null
    public static SearchBox Create(Transform parent, Vector3 pos, float width, float height, Text hint, Action<string> changed)
    {
        var chat = Vanilla.Chat;
        if (!chat) return null;
        var source = chat.freeChatField;
        if (!source) return null;

        // 元が非表示のことがある (チャットの窓が閉じている)。複製も非表示のまま作り、部品を外してから表示する
        var root = Object.Instantiate(source.gameObject, parent);
        root.name = "MrpSearchBox";
        root.SetActive(false);
        try
        {
            var field = root.GetComponent<FreeChatInputField>();
            var textArea = field ? field.textArea : root.GetComponentInChildren<TextBoxTMP>(true);
            if (field) Object.DestroyImmediate(field);
            var send = root.transform.Find("ChatSendButton");
            if (send) Object.DestroyImmediate(send.gameObject);
            if (!textArea) { Object.Destroy(root); return null; }

            // 欄のイベントは空のものに差し替える (複製元のチャットへの配線が残っているため)
            textArea.OnEnter = new UnityEngine.UI.Button.ButtonClickedEvent();
            textArea.OnChange = new UnityEngine.UI.Button.ButtonClickedEvent();
            textArea.OnFocusLost = new UnityEngine.UI.Button.ButtonClickedEvent();
            textArea.OnFocus = new UnityEngine.UI.Button.ButtonClickedEvent();
            textArea.allowAllCharacters = true;
            textArea.AllowSymbols = true;
            textArea.AllowPaste = true;
            textArea.characterLimit = 24;
            textArea.ClearOnFocus = false;

            // 大きさ: 縦は全体の縮尺で、横は背景の絵の幅で合わせる (文字が横に潰れないように)
            var bg = root.transform.Find("Background");
            var bgSprite = bg ? bg.GetComponent<SpriteRenderer>() : null;
            float baseH = bgSprite ? bgSprite.size.y : 0.62f;
            float s = height / baseH;
            root.transform.localScale = new Vector3(s, s, 1f);
            root.transform.localPosition = pos;
            float w = width / s;
            if (bgSprite)
            {
                bgSprite.size = new Vector2(w, baseH);
                var col = bg.GetComponent<BoxCollider2D>();
                if (col) col.size = new Vector2(w, col.size.y);
                var pb = bg.GetComponent<PassiveButton>();
                if (pb)
                {
                    pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                    pb.OnClick.AddListener(FocusAction);
                }
            }
            // 見た目: 角の丸い暗い欄 (輪郭 + 地) と左の虫眼鏡。字は白
            float textX = -w / 2f + 0.46f / s;
            if (bgSprite)
            {
                float e = 0.025f / s;
                bgSprite.sprite = MenuArt.Round(baseH / 2f - e);
                bgSprite.drawMode = SpriteDrawMode.Sliced;
                bgSprite.size = new Vector2(w - e * 2f, baseH - e * 2f);
                bgSprite.color = Field;
                var roll = bg.GetComponent<ButtonRolloverHandler>();
                if (roll) { roll.Target = bgSprite; roll.OutColor = Field; roll.OverColor = FieldOver; }
                MenuArt.Panel(root.transform, "MrpSearchEdge", baseH / 2f, new Vector2(w, baseH), new Vector3(0f, 0f, 0.05f), new Color(0.24f, 0.26f, 0.32f, 1f), null);
                var glass = new GameObject("MrpSearchIcon");
                glass.layer = root.layer;
                glass.transform.SetParent(root.transform, false);
                glass.transform.localPosition = new Vector3(-w / 2f + 0.24f / s, 0f, -0.02f);
                glass.transform.localScale = new Vector3(0.8f / s, 0.8f / s, 1f);
                var gs = glass.AddComponent<SpriteRenderer>();
                gs.sprite = MenuArt.Glass();
                gs.color = new Color(0.55f, 0.58f, 0.66f, 1f);
            }
            textArea.transform.localPosition = new Vector3(textX, 0f, 0f);
            if (textArea.outputText)
            {
                var rt = textArea.outputText.rectTransform;
                rt.sizeDelta = new Vector2(w - 0.6f / s, rt.sizeDelta.y);
                textArea.outputText.color = Color.white;
            }
            var pipe = textArea.transform.Find("Pipe");
            var pipeText = pipe ? pipe.GetComponent<TextMeshPro>() : null;
            if (pipeText) pipeText.color = new Color(1f, 0.85f, 0.4f, 1f);

            // 文字数の表示の欄を、空の時の案内に使う
            TextMeshPro hintTmp = null;
            var counter = root.transform.Find("CharCounter (TMP)");
            if (counter)
            {
                hintTmp = counter.GetComponent<TextMeshPro>();
                var tr = counter.GetComponent<TextTranslatorTMP>();
                if (tr) Object.DestroyImmediate(tr);
                // 入力の印 (|) の右から出す (重なると 1 文字目が隠れる)
                counter.localPosition = new Vector3(textX + 0.12f, 0f, -0.01f);
                if (hintTmp)
                {
                    hintTmp.text = hint;
                    hintTmp.fontSize = textArea.outputText ? textArea.outputText.fontSize * 0.85f : 1.8f;
                    hintTmp.alignment = TextAlignmentOptions.Left;
                    hintTmp.color = new Color(0.5f, 0.53f, 0.6f, 1f);
                    MenuFont.Apply(hintTmp);
                    hintTmp.rectTransform.pivot = new Vector2(0f, 0.5f);
                    hintTmp.rectTransform.sizeDelta = new Vector2(w - 0.8f / s, baseH);
                    hintTmp.enableWordWrapping = false;
                }
            }

            root.SetActive(true);
            textArea.SetText("", "");
            _current = new SearchBox(root, textArea, hintTmp, changed);
            return _current;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"search box: {e}");
            Object.Destroy(root);
            return null;
        }
    }

    // 欄を押した時に入力を受け付ける (Android ではキーボードが開く)
    private static readonly UnityAction FocusAction = (UnityAction)(() =>
    {
        if (_current != null && _current._text) _current._text.GiveFocus();
    });

    public void Clear()
    {
        if (_text) _text.SetText("", "");
    }

    // 試験用: 打ち込んだのと同じことをする
    public void Type(string s)
    {
        if (_text) _text.SetText(s, "");
    }

    public static void Forget(SearchBox box)
    {
        if (_current == box) _current = null;
    }

    internal static void OnSetText(TextBoxTMP box, string input, string compo)
    {
        var cur = _current;
        if (cur == null || !cur._text || box.Pointer != cur._text.Pointer) return;
        string q = (input ?? "") + (compo ?? "");
        if (cur._hint) cur._hint.enabled = q.Length == 0;
        if (q == cur.Query) return;
        cur.Query = q;
        cur._changed(q);
    }
}

[HarmonyPatch(typeof(TextBoxTMP))]
internal static class TextBoxSetTextPatch
{
    [HarmonyPatch(nameof(TextBoxTMP.SetText)), HarmonyPostfix]
    public static void SetText(TextBoxTMP __instance, [HarmonyArgument(0)] string input, [HarmonyArgument(1)] string inputCompo)
    {
        try { SearchBox.OnSetText(__instance, input, inputCompo); }
        catch (Exception e) { Plugin.Logger.LogError($"search box text: {e.Message}"); }
    }
}
