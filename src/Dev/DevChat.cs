using System.Text;
using HarmonyLib;

namespace MoreRolesPlus.Dev;

// チャット欄からコマンドを打てるようにする (/end のように / を付ける)。遊ぶ人のコマンドは誰でも、
// テスト用ブリッジのコマンドは開発者だけ (/dummy 1 など)。
// コマンドは送信せず手元で実行し、結果はチャット欄に自分にだけ見える注意書きとして出す。
// 試合中もチャット欄を出せる (chat コマンド / PC は Shift+Enter+C)。
[HarmonyPatch(typeof(ChatController), nameof(ChatController.SendChat))]
internal static class DevChat
{
    public static void SetShown(bool on)
    {
        var hud = Vanilla.Hud;
        if (!hud || !hud.Chat) return;
        hud.Chat.SetVisible(on);
    }

    // 自分にだけ見える注意書きを出す。チャット欄が隠れている間 (試合の読み込み中など) に足すと、
    // 本編の表示の処理が途中で止まってロビーの部品が毎フレーム例外を出し続けるので、その時はログだけにする
    public static void Notice(ChatController chat, string text)
    {
        if (chat && chat.gameObject.activeInHierarchy)
        {
            // ボタンの絵の入った文 (/r など) は、吹き出しの字に絵の素材を付けてから出す (吹き出しは使い回しなので全部に)
            if (text.Contains("<sprite") && chat.chatBubblePool)
            {
                foreach (var list in new[] { chat.chatBubblePool.activeChildren, chat.chatBubblePool.inactiveChildren })
                    if (list != null)
                        foreach (var c in list)
                        {
                            var b = c ? c.TryCast<ChatBubble>() : null;
                            if (b) Menu.ChatIcons.Apply(b.TextArea);
                        }
            }
            chat.AddChatWarning(text);
            Menu.ChatRoleDoc.Attach(chat);
        }
        else Plugin.Logger.LogInfo($"chat notice (hidden): {text}");
    }

    // 本編は送ってから 3 秒経つまで次を送らせない。送る前と後で「3 秒経った」にしておくと、
    // 待ちの表示も出ずにすぐ次を送れる (毎フレームは触らない)
    private const float SendWait = 3f;

    public static void Postfix(ChatController __instance) => __instance.timeSinceLastMessage = SendWait;

    public static bool Prefix(ChatController __instance)
    {
        __instance.timeSinceLastMessage = SendWait;
        // 隠れているチャット欄と、開始の数えが終わって試合を読み込んでいる間は何もしない (本編に任せる)。
        // その間に入力欄を消すと、試合が始まった後もロビーの部品が残って毎フレーム例外を出し、
        // 蘇生などチャット欄を閉じる処理も止まる
        if (!__instance.gameObject.activeInHierarchy) return true;
        var client = AmongUsClient.Instance;
        if (client && client.GameState == InnerNet.InnerNetClient.GameStates.Started && !ShipStatus.Instance) return true;
        var field = __instance.freeChatField;
        string text;
        try { text = field.Text?.Trim() ?? ""; }
        catch { return true; }
        Commands.ChatInput.Remember(text);
        // 耳打ちの相手を選んでいたら、次の送信は耳打ち (空で送ると取りやめ)
        if (Commands.Whisper.TrySend(__instance, text))
        {
            field.Clear();
            Commands.Whisper.AfterClear(field);
            return false;
        }
        // 陣営チャット (会議中に「仲間へ」を選んでいる時と、タスク中に出したチャット欄)
        if (Commands.TeamChat.TrySend(__instance, text))
        {
            field.Clear();
            Commands.TeamChat.AfterClear(field);
            return false;
        }
        if (text.Length < 2 || text[0] != '/') return true;

        string line = text[1..];
        int sp = line.IndexOf(' ');
        string name = sp < 0 ? line : line[..sp];
        // 練習とフリープレイでは誰でも地形の破壊を試せる (/hammer /blast)
        if (Terrain.SandboxCommands.TryHandle(name, sp < 0 ? "" : line[(sp + 1)..], out string sandbox))
        {
            field.Clear();
            Notice(__instance, sandbox);
            return false;
        }
        // 遊ぶ人のコマンド (/help で一覧。ホスト用のものはホストだけ)
        if (Commands.ChatCommands.TryRun(name, sp < 0 ? "" : line[(sp + 1)..], out string reply))
        {
            field.Clear();
            if (!string.IsNullOrEmpty(reply)) Notice(__instance, reply);
            return false;
        }
        if (!DevUsers.AmDev) return true;
        // MRP のコマンドでない / (本編の /cmd など) はそのまま本編へ
        if (!Bridge.TestBridge.Has(name)) return true;

        var sb = new StringBuilder();
        Bridge.TestBridge.Run(line, s => { if (sb.Length > 0) sb.Append('\n'); sb.Append(s); });
        Plugin.Logger.LogInfo($"dev chat> {line}");
        field.Clear();
        Notice(__instance, sb.ToString());
        return false;
    }
}
