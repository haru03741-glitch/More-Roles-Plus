using System.Collections.Generic;

namespace MoreRolesPlus.Menu;

// 自分にだけ見える注意書きをチャットに出す。チャット欄が隠れている間 (部屋に着いた直後・試合の読み込み中) に足すと
// 本編の表示の処理が止まるので、欄が出るまで溜めておく。部屋を移ったら捨てる
internal static class ChatNotes
{
    private static readonly List<string> Pending = new();
    private const int MaxPending = 20;

    public static void Post(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Pending.Count >= MaxPending) Pending.RemoveAt(0);
        Pending.Add(text);
    }

    public static void Clear() => Pending.Clear();

    public static void Tick()
    {
        if (Pending.Count == 0) return;
        var hud = Vanilla.Hud;
        if (!hud || !hud.Chat || !hud.Chat.gameObject.activeInHierarchy || !PlayerControl.LocalPlayer) return;
        // 先に取り出して空にする (書く途中で落ちても毎フレーム同じ物を書き直さない)
        var notes = Pending.ToArray();
        Pending.Clear();
        foreach (var t in notes)
        {
            try { hud.Chat.AddChatWarning(t); }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"chat note: {e.Message}"); }
        }
    }
}
