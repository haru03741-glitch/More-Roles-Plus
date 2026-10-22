using System.Collections.Generic;

namespace MoreRolesPlus.Menu;

// 自分にだけ見える注意書きをチャットに出す。チャット欄が隠れている間 (部屋に着いた直後・試合の読み込み中) に足すと
// 本編の表示の処理が止まるので、欄が出るまで溜めておく。部屋を移ったら捨てる
internal static class ChatNotes
{
    private static readonly List<string> Pending = new();

    public static void Post(string text)
    {
        if (!string.IsNullOrEmpty(text)) Pending.Add(text);
    }

    public static void Clear() => Pending.Clear();

    public static void Tick()
    {
        if (Pending.Count == 0) return;
        var hud = Vanilla.Hud;
        if (!hud || !hud.Chat || !hud.Chat.gameObject.activeInHierarchy || !PlayerControl.LocalPlayer) return;
        foreach (var t in Pending) hud.Chat.AddChatWarning(t);
        Pending.Clear();
    }
}
