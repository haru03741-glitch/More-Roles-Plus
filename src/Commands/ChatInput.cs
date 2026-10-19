// Based on https://github.com/Gurge44/EndlessHostRoles and https://github.com/waffle-ful/Aeterna-End-K-not
// Patches/TextBoxPatch.cs and Patches/ChatControlPatch.cs (command suggestion, paste), and
// https://github.com/KARPED1EM/TownOfNext Patches/TextBoxPatch.cs (character limit) (GPL-3.0)
using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Commands;

// チャットの入力欄を広げる。
// - 記号や全角を含む全部の文字・1 回に 300 文字まで。300 文字は全部が 3 バイトの文字 (日本語など) でも 900 バイトに収まる長さ
//   (公式サーバーは 1 通がおよそ 1000 バイトを超えると送った人を切断する)。本編の入力欄が持っている切り替えを立てるだけ。
// - Ctrl+V で貼り付け (PC。本編の貼り付けの切り替えはチャット欄では効かない)。改行は空白にする (改行は送信になる)。
// - / で始めると、合うコマンドを入力欄に薄く出し、Tab で補う。使い方 (引数) も薄く出す。
// - ↑ / ↓ で前に送った文 (コマンドも) を入力欄へ呼び戻す (PC・新しい順に 30 件・部屋を出ても覚えている)。
//   打ちかけの文は ↓ で一番下まで戻ると元に戻る。
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

    private const int HistoryMax = 30;
    private static readonly List<string> History = new(); // 送った文 (古い順)
    private static int _historyAt;                         // 呼び戻している位置 (History.Count = 打ちかけの文)
    private static string _draft = "";                     // 呼び戻す前に打っていた文

    // 送った文を覚える (コマンドは送られないので送る処理の入口で呼ぶ)。同じ文を続けて送った時は 1 つにまとめる
    internal static void Remember(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (History.Count == 0 || History[^1] != text)
        {
            History.Add(text);
            if (History.Count > HistoryMax) History.RemoveAt(0);
        }
        _historyAt = History.Count;
        _draft = "";
    }

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
        AfkGuard.Touched = true;
        if (Input.GetKeyDown(KeyCode.F1)) { Menu.SettingsOverlay.Toggle(); return; }
        var chat = Vanilla.Chat;
        if (!chat || !chat.IsOpenOrOpening) return;
        var area = chat.freeChatField ? chat.freeChatField.textArea : null;
        if (!area || !area.hasFocus) return;
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            if (_complete.Length > 0) SetText(area, _complete, _complete.Length);
            return;
        }
        if (Input.GetKeyDown(KeyCode.UpArrow)) { Recall(area, -1); return; }
        if (Input.GetKeyDown(KeyCode.DownArrow)) { Recall(area, 1); return; }
        if (Input.GetKeyDown(KeyCode.V) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            Paste(area, GUIUtility.systemCopyBuffer);
    }

    private static void Recall(TextBoxTMP area, int delta)
    {
        if (History.Count == 0) return;
        if (_historyAt > History.Count) _historyAt = History.Count;
        int to = Math.Clamp(_historyAt + delta, 0, History.Count);
        if (to == _historyAt) return;
        if (_historyAt == History.Count) _draft = area.text ?? "";
        _historyAt = to;
        string s = to == History.Count ? _draft : History[to];
        SetText(area, s, s.Length);
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
        else if (args == "up") Recall(area, -1);
        else if (args == "down") Recall(area, 1);
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
    // Backspace などの制御文字は本編の判定のまま (通すと □ として入る)。
    // この関数の char の引数は下位 1 バイトに切られて届く (一 U+4E00 → U+0000・会 U+4F1A → U+001A・
    // 下位バイトが 0x80 以上なら U+FFFD。2026-10-19 実測)。届いた値だけで制御文字を見ると、一・三・上・会・言 などが落ちる。
    // そこで本物の文字を別に持つ: SetText の中では渡された文字列、キー入力の処理 (PC) ではその時の Input.inputString。
    // 本編は文字を先頭から順に 1 つずつ尋ねるので、届いた値と切った値を照らしながら位置を進める。合わない時は届いた値で判定する
    private static string _pending;   // 本編が今 1 文字ずつ尋ねている文字列 (null = 分からない)
    private static int _pendingAt;
    private static bool _inSetText;
#if !ANDROID
    private static int _inputFrame = -1;
#endif

    [HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.SetText)), HarmonyPrefix]
    public static void SetTextPrefix(TextBoxTMP __instance, [HarmonyArgument(0)] string input, [HarmonyArgument(1)] string inputCompo)
    {
        if (__instance.Pointer != ChatInput.ChatArea) return;
        _inSetText = true;
        _pending = string.IsNullOrEmpty(inputCompo) ? input : input + inputCompo;
        _pendingAt = 0;
    }

    [HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.SetText)), HarmonyPostfix]
    public static void SetTextPostfix()
    {
        _inSetText = false;
        _pending = null;
    }

    [HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.IsCharAllowed)), HarmonyPostfix]
    public static void IsCharAllowed(TextBoxTMP __instance, [HarmonyArgument(0)] char c, ref bool __result)
    {
        if (__instance.Pointer != ChatInput.ChatArea) return;
#if !ANDROID
        // キー入力の処理から尋ねられた時は、本編が断った時だけその回の入力の文字列を読む (毎フレームは読まない)
        if (!_inSetText && !__result && _inputFrame != Time.frameCount)
        {
            _inputFrame = Time.frameCount;
            _pending = Input.inputString;
            _pendingAt = 0;
        }
#endif
        char real = Advance(c);
        if (!__result && !char.IsControl(real)) __result = true;
    }

    // 届いた値 c に合う本物の文字を、尋ねている文字列の今の位置から探して返す (見つからなければ c のまま)
    private static char Advance(char c)
    {
        var s = _pending;
        if (s == null) return c;
        for (int i = _pendingAt; i < s.Length; i++)
        {
            if (!Matches(s[i], c)) continue;
            _pendingAt = i + 1;
            return s[i];
        }
        return c;
    }

    // 本物の文字 real が、1 バイトに切られて c として届いたものか (切られずに届く環境ならそのまま一致する)
    private static bool Matches(char real, char c)
    {
        if (real == c) return true;
        int low = real & 0xFF;
        return low < 0x80 ? c == low : c == (char)0xFFFD;
    }

    // 本編がチャットの種類の切り替えなどで入力欄を作り直した時のため、開くたびにも立て直す
    [HarmonyPatch(typeof(ChatController), nameof(ChatController.Toggle)), HarmonyPostfix]
    public static void Toggle(ChatController __instance)
    {
        try { ChatInput.Apply(__instance.freeChatField); }
        catch (Exception e) { Plugin.Logger.LogError($"chat input: {e.Message}"); }
    }
}
