// Based on the end-of-game role summary in https://github.com/TheOtherRolesAU/TheOtherRoles and
// https://github.com/Gurge44/EndlessHostRoles (GPL-3.0)
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 試合の終わりの画面の左上に、全員の役職と生死 (/l と同じ文) を小さく並べる。終わりの画面を作る時に 1 回だけ置く。
// 置けるのは左上から「終了」ボタンの上まで。入らない人数では文字を縮め、縮めきっても入らなければ
// 画面の左半分に列を並べ、それでも溢れる分はページ送りにする
internal static class EndSummary
{
    private const float Size = 1.6f, MinSize = 1f, Gap = 0.3f;

    public static void Show(EndGameManager egm)
    {
        if (!ClientOptions.EndSummary.Value || !egm || !egm.WinText) return;
        string text = Roles.MatchLog.LastResult;
        if (string.IsNullOrEmpty(text)) return;
        var cam = Camera.main;
        if (!cam) return;

        string header = Tr.Get("EndSummary.Results");
        string[] lines = text.Split('\n');
        // 画面の左上 (カメラの見える範囲から測る。終わりの画面は画面の真ん中が原点)
        float h = cam.orthographicSize, w = h * cam.aspect;
        var c = cam.transform.position;
        float left = c.x - w + 0.25f, top = c.y + h - 0.25f;
        float bottom = c.y - h + 1.7f; // 「終了」ボタンの上 (本編は画面の下から一定の位置に置く)
        var nav = egm.Navigation ? egm.Navigation.transform.Find("ExitGameButton") : null;
        if (nav) bottom = nav.position.y + 0.8f;
        float height = top - bottom, width = c.x - 0.3f - left;

        var first = Make(egm, Size);
        // 全文の高さを Size で測る (1 行の高さは字で変わるので、実際の文で測る)
        float total = first.GetPreferredValues(header + text).y;
        float line = total > 0f ? total / (lines.Length + 1) : 0.25f;
        float size = Mathf.Min(Size, Size * height / (total > 0f ? total : 1f) * 0.98f);
        Plugin.Logger.LogInfo($"[endsummary] lines={lines.Length} height={height:F2} total={total:F2} size={size:F2}");
        if (size >= MinSize)
        {
            Place(first, left, top, size, header + text);
            return;
        }

        // 縮めきっても入らない: 1 行の高さで割った行数ずつ列にし、左半分に入る列の数を 1 ページにする
        int rows = Mathf.Max(1, (int)(height / (line * MinSize / Size)) - 1);
        var chunks = new List<string>();
        for (int i = 0; i < lines.Length; i += rows)
            chunks.Add(string.Join("\n", lines, i, Mathf.Min(rows, lines.Length - i)));
        first.fontSize = MinSize;
        float col = 0f;
        foreach (var s in chunks) col = Mathf.Max(col, first.GetPreferredValues(s).x);
        int perPage = Mathf.Clamp((int)((width + Gap) / (col + Gap)), 1, chunks.Count);
        int pages = (chunks.Count + perPage - 1) / perPage;

        var cols = new TextMeshPro[perPage];
        cols[0] = first;
        for (int i = 1; i < perPage; i++) cols[i] = Make(egm, MinSize);
        for (int i = 0; i < perPage; i++) Place(cols[i], left + i * (col + Gap), top, MinSize, "");
        void ShowPage(int page)
        {
            for (int i = 0; i < perPage; i++)
            {
                int k = page * perPage + i;
                // 見出しは 1 列目だけ。ほかの列は 1 行空けて頭をそろえる
                string body = k < chunks.Count ? chunks[k] : "";
                cols[i].text = (i == 0 ? header : "\n") + body;
            }
        }
        if (pages <= 1) { ShowPage(0); return; }
        // ページの矢印は一覧の下、「終了」ボタンの右に置く
        var ui = new Vector3(left + 2.4f, bottom - 0.4f, first.transform.position.z);
        Options.CrowdPages.OpenText(egm, pages, ShowPage, egm.WinText, egm.transform.InverseTransformPoint(ui));
    }

    private static TextMeshPro Make(EndGameManager egm, float size)
    {
        var go = Object.Instantiate(egm.WinText.gameObject, egm.transform);
        go.name = "MrpEndSummary";
        var tmp = go.GetComponent<TextMeshPro>();
        go.transform.localScale = Vector3.one;
        tmp.rectTransform.pivot = new Vector2(0f, 1f);
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.enableAutoSizing = false;
        tmp.enableWordWrapping = false;
        tmp.fontSize = size;
        tmp.color = Color.white;
        tmp.richText = true;
        return tmp;
    }

    private static void Place(TextMeshPro tmp, float x, float y, float size, string text)
    {
        tmp.transform.position = new Vector3(x, y, tmp.transform.position.z);
        tmp.rectTransform.sizeDelta = new Vector2(10f, 10f);
        tmp.fontSize = size;
        tmp.text = text;
    }
}

// 試合から戻ったロビーのチャットに、前の試合の結果 (/l と同じ文) を自分にだけ 1 回出す
internal static class ChatSummary
{
    private static int _postedGen;

    public static void OnLobby()
    {
        int gen = Roles.MatchLog.ResultGen;
        if (!ClientOptions.SummaryToChat.Value || gen == 0 || gen == _postedGen) return;
        _postedGen = gen;
        string header = new Text("EndSummary.ChatHeader").With("winners", Roles.MatchLog.LastWinners);
        ChatNotes.Post(header + "\n" + Roles.MatchLog.LastResult);
    }
}

[HarmonyLib.HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class ChatSummaryLobbyPatch
{
    public static void Postfix() => ChatSummary.OnLobby();
}
