using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Voice;

// 話している人の頭の上 (名前の上) に ♪ を出す (ホストの設定「話している人を見せる」が ON の時)。
// 陣営の無線の声は誰の声か出さないので付けない。姿が見えていない人 (透明・ベントの中・画面の外) には付けない
internal static class VoiceMarks
{
    private const string MarkName = "MrpVoiceMark";
    private static readonly Color Green = new(0.49f, 1f, 0.49f, 1f);
    private static readonly List<GameObject> Shown = new();

    // VoiceChat.Tick から (FixedUpdate)
    internal static void Tick(bool on)
    {
        on = on && VoiceChat.ShowSpeaker.Value && Menu.ClientOptions.VoiceHear.Value;
        foreach (var go in Shown) if (go && go.activeSelf) go.SetActive(false);
        Shown.Clear();
        if (!on) return;
        var me = PlayerControl.LocalPlayer;
        float pulse = 1f + 0.12f * Mathf.Sin(Time.time * 10f);
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.AmOwner || p.Data == null || !p.cosmetics || !p.Visible) continue;
            if (!Speakers.Speaking(p.PlayerId) || Speakers.SpeakingRadio(p.PlayerId)) continue;
            if (VoiceRules.Volume(me, p) <= 0f) continue;
            var mark = Mark(p);
            if (!mark) continue;
            mark.SetActive(true);
            mark.transform.localScale = Vector3.one * pulse;
            Shown.Add(mark);
        }
    }

    private static GameObject Mark(PlayerControl p)
    {
        var name = p.cosmetics.nameText;
        if (!name) return null;
        var parent = name.transform.parent ? name.transform.parent : name.transform;
        var t = parent.Find(MarkName);
        if (t) return t.gameObject;
        var go = new GameObject(MarkName) { layer = name.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = name.transform.localPosition + new Vector3(0f, 0.5f, -0.01f);
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.font = name.font;
        tmp.fontSharedMaterial = name.fontSharedMaterial;
        tmp.fontSize = name.fontSize * 1.1f;
        tmp.enableAutoSizing = false;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = new Vector2(1f, 0.4f);
        tmp.color = Green;
        tmp.text = "♪";
        var mr = go.GetComponent<MeshRenderer>();
        var nr = name.GetComponent<MeshRenderer>();
        if (mr && nr) { mr.sortingLayerID = nr.sortingLayerID; mr.sortingOrder = nr.sortingOrder + 1; }
        go.SetActive(false);
        return go;
    }
}
