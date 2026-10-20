// Based on the end-of-game role summary in https://github.com/TheOtherRolesAU/TheOtherRoles and
// https://github.com/Gurge44/EndlessHostRoles (GPL-3.0)
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 試合の終わりの画面の左上に、全員の役職と生死 (/l と同じ文) を小さく並べる。終わりの画面を作る時に 1 回だけ置く
internal static class EndSummary
{
    public static void Show(EndGameManager egm)
    {
        if (!ClientOptions.EndSummary.Value || !egm || !egm.WinText) return;
        string text = Roles.MatchLog.LastResult;
        if (string.IsNullOrEmpty(text)) return;
        var cam = Camera.main;
        if (!cam) return;

        var go = Object.Instantiate(egm.WinText.gameObject, egm.transform);
        go.name = "MrpEndSummary";
        var tmp = go.GetComponent<TextMeshPro>();
        // 画面の左上 (カメラの見える範囲から測る。終わりの画面は画面の真ん中が原点)
        float h = cam.orthographicSize, w = h * cam.aspect;
        go.transform.localScale = Vector3.one;
        go.transform.position = new Vector3(cam.transform.position.x - w + 0.25f, cam.transform.position.y + h - 0.25f, egm.WinText.transform.position.z);
        tmp.rectTransform.pivot = new Vector2(0f, 1f);
        tmp.rectTransform.sizeDelta = new Vector2(w, h * 2f - 0.5f);
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.enableAutoSizing = false;
        tmp.enableWordWrapping = false;
        tmp.fontSize = 1.6f;
        tmp.color = Color.white;
        tmp.richText = true;
        tmp.text = Tr.Get("EndSummary.Results") + text;
    }
}
