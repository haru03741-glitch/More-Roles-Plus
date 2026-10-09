// Based on https://github.com/Gurge44/EndlessHostRoles and https://github.com/waffle-ful/Aeterna-End-K-not
// Patches/TextBoxPatch.cs and Patches/ChatControlPatch.cs (command suggestion, paste), and
// https://github.com/KARPED1EM/TownOfNext Patches/TextBoxPatch.cs (character limit) (GPL-3.0)
using System;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Commands;

// チャットの入力欄を広げる。
// - 記号や全角を含む全部の文字・1 回に 300 文字まで。300 文字は全部が 3 バイトの文字 (日本語など) でも 900 バイトに収まる長さ
//   (公式サーバーは 1 通がおよそ 1000 バイトを超えると送った人を切断する)。本編の入力欄が持っている切り替えを立てるだけ。
// - Ctrl+V で貼り付け (PC。本編の貼り付けの切り替えはチャット欄では効かない)。改行は空白にする (改行は送信になる)。
// - / で始めると、合うコマンドを入力欄に薄く出し、Tab で補う。使い方 (引数) も薄く出す。
// 文字の入力の処理そのもの (TextBoxTMP の Update / SetText) は本編のまま。欄の文字を読むのは文字が変わった時とキーを押した時だけで、
// 薄い文字の部品 (入力欄の文字の複製) の文字は読み返さない (補う文字は手元の文字列で持つ)。
// 毎フレームの問い合わせは「何かキーが押されたか」の 1 つだけ
internal static class ChatInput
{
    public const int Limit = 300;
    private static readonly Color GhostColor = new(0.45f, 0.45f, 0.45f, 0.75f);

    private static TextMeshPro _ghost;    // 入力欄の文字の複製 (入力欄の子なので欄と一緒に消える)
    private static IntPtr _ghostOwner;    // 複製を作った入力欄
    private static string _complete = ""; // Tab で入れる文字 (空 = 補うものなし)
    internal static IntPtr ChatArea;       // チャット欄の入力欄 (文字を全部通す相手)

    public static void Apply(FreeChatInputField field)
    {
        var area = field ? field.textArea : null;
        if (!area) return;
        ChatArea = area.Pointer;
        area.allowAllCharacters = true;
        area.AllowSymbols = true;
        area.AllowEmail = true;
        area.characterLimit = Limit;
        // < > も打てるので、入力欄では <b> などを装飾として読まない (読むと見える文字数と文字列の長さがずれ、
        // 本編のカーソル位置の計算が範囲の外を読んで例外を出し続ける)
        if (area.outputText) area.outputText.richText = false;
    }

    // チャット欄の文字が変わった (本編の SetText の後)
    internal static void OnSetText(TextBoxTMP box, string compo)
    {
        var chat = Vanilla.Chat;
        var field = chat ? chat.freeChatField : null;
        if (!field || !field.textArea || field.textArea.Pointer != box.Pointer) return;
        string text = box.text ?? "";
#if !ANDROID
        // 日本語入力の変換中の文字を、本編は常に末尾に描く (確定すると本編がカーソルの所へ入れる)。カーソルの所に描き直す
        if (!string.IsNullOrEmpty(compo) && box.caretPos >= 0 && box.caretPos < text.Length && box.outputText)
            box.outputText.text = text.Insert(box.caretPos, compo);
#endif
        if (field.charCountText) field.charCountText.text = $"{text.Length}/{Limit}";
        ShowSuggestion(box, text);
    }

    private static void ShowSuggestion(TextBoxTMP box, string text)
    {
        string ghost = "";
        _complete = "";
        if (text.Length >= 2 && text[0] == '/' && text[1] != ' ')
        {
            int sp = text.IndexOf(' ');
            if (sp < 0)
            {
                if (ChatCommands.Suggest(text[1..], out string name, out string usage))
                {
                    // 打った分は本編の文字が上に重なるので、薄い文字は打った分の綴りのまま続きを足す
                    ghost = text + name[(text.Length - 1)..] + (usage.Length > 0 ? " " + usage : "");
                    _complete = "/" + name + " ";
                }
            }
            else if (sp == text.Length - 1 && ChatCommands.Suggest(text[1..sp], out string name, out string usage)
                     && name.Equals(text[1..sp], StringComparison.OrdinalIgnoreCase) && usage.Length > 0)
                ghost = text + usage;
        }
        SetGhost(box, ghost);
    }

    private static void SetGhost(TextBoxTMP box, string s)
    {
        if (s.Length == 0)
        {
            if (_ghost) _ghost.enabled = false;
            return;
        }
        if (!_ghost || _ghostOwner != box.Pointer)
        {
            if (_ghost) UnityEngine.Object.Destroy(_ghost.gameObject);
            var src = box.outputText;
            if (!src) return;
            _ghost = UnityEngine.Object.Instantiate(src, src.transform.parent);
            _ghost.name = "MrpChatSuggestion";
            _ghost.color = GhostColor;
            var p = src.transform.localPosition;
            _ghost.transform.localPosition = new Vector3(p.x, p.y, p.z + 0.001f); // 本編の文字の奥
            _ghostOwner = box.Pointer;
        }
        _ghost.text = s;
        _ghost.enabled = true;
    }

#if !ANDROID
    public static void Tick()
    {
        if (!Input.anyKeyDown) return;
        var chat = Vanilla.Chat;
        if (!chat || !chat.IsOpenOrOpening) return;
        var area = chat.freeChatField ? chat.freeChatField.textArea : null;
        if (!area || !area.hasFocus) return;
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            if (_complete.Length > 0) SetText(area, _complete, _complete.Length);
            return;
        }
        if (Input.GetKeyDown(KeyCode.V) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            Paste(area, GUIUtility.systemCopyBuffer);
    }

    private static void Paste(TextBoxTMP area, string clip)
    {
        if (string.IsNullOrEmpty(clip)) return;
        clip = clip.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        if (clip.Length > Limit) clip = clip[..Limit]; // 巨大な貼り付けで文字列を作り過ぎない (残りは本編が上限で切る)
        string cur = area.text ?? "";
        int at = Math.Clamp(area.caretPos, 0, cur.Length);
        SetText(area, cur.Insert(at, clip), at + clip.Length); // カーソルの所へ入れる
    }

    // 文字を丸ごと入れ替えて、文字の位置 (カーソル) を caret へ。
    // 本編の SetText は増えた文字数だけカーソルを進めてから描画の文字情報で位置を測るので、一度に何文字も増やすと
    // 古い文字情報の範囲の外を読んで例外になる。先に入力欄の描画を新しい文字で作り直しておく
    private static void SetText(TextBoxTMP area, string text, int caret)
    {
        if (text.Length > Limit) text = text[..Limit];
        var output = area.outputText;
        if (output)
        {
            output.text = text;
            output.ForceMeshUpdate(true, true);
        }
        area.SetText(text, "");
        area.caretPos = Math.Clamp(caret, 0, text.Length);
        try
        {
            area.MoveCaret();
            area.SetPipePosition();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"chat caret: {e.Message}"); }
    }
#else
    public static void Tick() { }
#endif

    internal static string Describe()
    {
        var chat = Vanilla.Chat;
        var area = chat && chat.freeChatField ? chat.freeChatField.textArea : null;
        if (!area) return "ERR chatin no chat field";
#if ANDROID
        const int caret = -1; // Android の入力欄はカーソルを持たない
#else
        int caret = area.caretPos;
#endif
        return $"OK chatin caret={caret} all={area.allowAllCharacters} limit={area.characterLimit} focus={area.hasFocus} text=[{area.text}] count=[{(chat.freeChatField.charCountText ? chat.freeChatField.charCountText.text : "-")}] complete=[{_complete}] ghost={(_ghost && _ghost.enabled)}";
    }

    // 確認用: tab / paste は Tab・Ctrl+V と同じ処理、type は打ったのと同じ
    internal static string Simulate(string args)
    {
        var chat = Vanilla.Chat;
        var area = chat && chat.freeChatField ? chat.freeChatField.textArea : null;
        if (!area) return "ERR chatin no chat field";
#if !ANDROID
        if (args == "tab") { if (_complete.Length > 0) SetText(area, _complete, _complete.Length); }
        else if (args.StartsWith("caret ")) { area.caretPos = int.Parse(args[6..]); try { area.MoveCaret(); } catch { } }
        else if (args.StartsWith("type ")) area.SetText(args[5..], "");
        else if (args.StartsWith("compo ")) area.SetText(area.text ?? "", args[6..]);
        else if (args.StartsWith("paste ")) Paste(area, args[6..].Replace("\\n", "\n"));
#endif
        return Describe();
    }
}

[HarmonyPatch]
internal static class ChatInputPatches
{
    [HarmonyPatch(typeof(FreeChatInputField), nameof(FreeChatInputField.Awake)), HarmonyPostfix]
    public static void Awake(FreeChatInputField __instance)
    {
        try { ChatInput.Apply(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"chat input: {e.Message}"); }
    }

    // 本編は allowAllCharacters を立てても < > や全角の数字などを落とす。落とすと本編の SetText はカーソルを
    // 落とした分も進めたまま位置を測って例外になるので、チャット欄では見える文字を全部通す。
    // Backspace などの制御文字は本編の判定のまま (通すと □ として入る)
    [HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.IsCharAllowed)), HarmonyPostfix]
    public static void IsCharAllowed(TextBoxTMP __instance, [HarmonyArgument(0)] char c, ref bool __result)
    {
        if (!__result && !char.IsControl(c) && __instance.Pointer == ChatInput.ChatArea) __result = true;
    }

    // 本編がチャットの種類の切り替えなどで入力欄を作り直した時のため、開くたびにも立て直す
    [HarmonyPatch(typeof(ChatController), nameof(ChatController.Toggle)), HarmonyPostfix]
    public static void Toggle(ChatController __instance)
    {
        try { ChatInput.Apply(__instance.freeChatField); }
        catch (Exception e) { Plugin.Logger.LogError($"chat input: {e.Message}"); }
    }
}
