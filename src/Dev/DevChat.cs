using System.Text;
using HarmonyLib;

namespace MoreRolesPlus.Dev;

// 開発者がチャット欄から開発者コマンドを打てるようにする (/dummy 1 のように / を付ける)。
// コマンドは送信せず手元で実行し、結果はチャット欄に自分にだけ見える注意書きとして出す。
// 試合中もチャット欄を出せる (chat コマンド / PC は Shift+Enter+C)。
[HarmonyPatch(typeof(ChatController), nameof(ChatController.SendChat))]
internal static class DevChat
{
    public static void SetShown(bool on)
    {
        var hud = HudManager.Instance;
        if (!hud || !hud.Chat) return;
        hud.Chat.SetVisible(on);
    }

    public static bool Prefix(ChatController __instance)
    {
        var field = __instance.freeChatField;
        string text;
        try { text = field.Text?.Trim() ?? ""; }
        catch { return true; }
        if (text.Length < 2 || text[0] != '/') return true;

        string line = text[1..];
        int sp = line.IndexOf(' ');
        string name = sp < 0 ? line : line[..sp];
        // 練習とフリープレイでは誰でも地形の破壊を試せる (/hammer /blast)
        if (Terrain.SandboxCommands.TryHandle(name, sp < 0 ? "" : line[(sp + 1)..], out string sandbox))
        {
            field.Clear();
            __instance.AddChatWarning(sandbox);
            return false;
        }
        if (!DevUsers.AmDev) return true;
        // MRP のコマンドでない / (本編の /cmd など) はそのまま本編へ
        if (!Bridge.TestBridge.Has(name) && name != "help") return true;

        var sb = new StringBuilder();
        Bridge.TestBridge.Run(line, s => { if (sb.Length > 0) sb.Append('\n'); sb.Append(s); });
        Plugin.Logger.LogInfo($"dev chat> {line}");
        field.Clear();
        __instance.AddChatWarning(sb.ToString());
        return false;
    }
}
