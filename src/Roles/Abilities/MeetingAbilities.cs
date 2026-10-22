// Ability buttons added next to the vote buttons of each name plate based on https://github.com/All-Of-Us-Mods/MiraAPI
// (MeetingButtonManager / TargetedMeetingButton, LGPL-2.1)
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace MoreRolesPlus.Roles;

// 会議の能力 (AbilityTarget.Meeting) のボタン。本編は名札を押すと ✓/✗ (とジャッジの木槌) を名札の上に並べて出すので、
// その並び (PlayerVoteArea.Buttons) に能力ごとのボタンを 1 つずつ足す。本編の ✓ を複製して絵だけ差し替える。
// 本編は自分が投票し終えた後は名札を開かないので、会議の能力は投票する前に使う。
// ボタンは会議の画面の子なので、会議が終わると一緒に消える
internal static class MeetingAbilities
{
    private const float IconPixelsPerUnit = 297f; // 220 px の絵を本編の ✓ の丸 (0.74 単位) と同じ大きさに
    private const float RightX = 1.3f, Spacing = 0.65f; // 本編の並びの右端 (✗) と間隔
    private const string TileIcon = "meeting_tile";    // tools/make-meeting-tile.py
    private const float TilePixelsPerUnit = 265f;      // タイルの外形を本編の ✓ のタイルと同じ大きさに (実測)
    private const float IconScale = 0.8f;

    private struct Entry
    {
        public Ability Ability;
        public byte Target;
        public GameObject Button;
    }

    private static readonly List<Entry> _buttons = new();
    // 押した時の処理は (能力の番号, 相手) ごとに 1 回だけ作って使い回す (会議ごとに作ると UnityEvent に渡すたびに漏れる)
    private static readonly Dictionary<int, UnityAction> Clicks = new();

    // 会議の番号 (会議ごとの回数を数え直す印)。全員の端末で会議が始まるたびに増える
    internal static int Gen { get; private set; }

    internal static void OnMeetingStart(MeetingHud hud)
    {
        Gen++;
        _buttons.Clear();
        var lp = PlayerControl.LocalPlayer;
        if (!hud || !lp || lp.Data == null || lp.Data.IsDead) return;
        var list = Abilities.Local;
        // 本編は ✗・✓・(ジャッジなら木槌) を右から Spacing おきに並べる。その左へ能力ごとに 1 つずつ
        var vanillaRole = lp.Data.Role;
        int slot = vanillaRole && vanillaRole.Role == AmongUs.GameOptions.RoleTypes.Judge ? 3 : 2;
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.Target != AbilityTarget.Meeting) continue;
            float x = RightX - Spacing * slot++;
            foreach (var pva in hud.playerStates)
            {
                if (!pva || pva.AmDead) continue;
                byte id = pva.PlayerId.Value;
                var target = GameData.Instance.GetPlayerById(id)?.Object;
                if (!target || id == lp.PlayerId || (a.CanTarget != null && !a.CanTarget(lp, target))) continue;
                var go = Create(pva, a, id, x);
                if (go) _buttons.Add(new Entry { Ability = a, Target = id, Button = go });
            }
        }
        Refresh();
    }

    private static GameObject Create(PlayerVoteArea pva, Ability a, byte targetId, float x)
    {
        var proto = pva.ConfirmButton;
        if (!proto || !pva.Buttons) return null;
        var go = UnityEngine.Object.Instantiate(proto.gameObject, pva.Buttons.transform);
        go.name = "MrpMeetingAbility";
        var p = go.transform.localPosition;
        go.transform.localPosition = new Vector3(x, p.y, p.z);
        var sr = go.GetComponent<SpriteRenderer>();
        if (sr)
        {
            // 本編の ✓ と同じ作りの無地のタイルに能力の絵を載せる (本編のジャッジの木槌と同じく、絵はタイルより少し小さく)
            sr.sprite = ButtonIcons.Load(TileIcon, TilePixelsPerUnit, new Vector2(0.5f, 0.5f));
            var icon = new GameObject("Icon") { layer = go.layer }; // 会議の画面のカメラは UI の層だけを映す
            icon.transform.SetParent(go.transform, false);
            icon.transform.localPosition = new Vector3(0f, 0.02f, -0.01f);
            icon.transform.localScale = new Vector3(IconScale, IconScale, 1f);
            var isr = icon.AddComponent<SpriteRenderer>();
            isr.sprite = ButtonIcons.Load(a.Icon, IconPixelsPerUnit, new Vector2(0.5f, 0.5f));
            isr.sharedMaterial = sr.sharedMaterial;
            isr.sortingLayerID = sr.sortingLayerID;
            isr.sortingOrder = sr.sortingOrder;
            isr.maskInteraction = sr.maskInteraction;
        }
        var btn = go.GetComponent<PassiveButton>();
        if (btn)
        {
            btn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            int key = a.Index << 8 | targetId;
            if (!Clicks.TryGetValue(key, out var act)) Clicks[key] = act = (UnityAction)(() => Click(key >> 8, (byte)key));
            btn.OnClick.AddListener(act);
            // 複製元の ✓ の光り方の処理を持ち越さない
            btn.OnMouseOver = new UnityEvent();
            btn.OnMouseOut = new UnityEvent();
        }
        return go;
    }

    private static void Click(int index, byte targetId)
    {
        var hud = MeetingHud.Instance;
        if (hud)
            foreach (var pva in hud.playerStates)
                if (pva && pva.PlayerId.Value == targetId) { pva.Cancel(); break; } // 本編の ✗ と同じく並びを閉じる
        var list = Abilities.Local;
        if (index < list.Count && list[index].Target == AbilityTarget.Meeting) Abilities.MeetingClick(list[index], targetId);
    }

    // 使えなくなった能力のボタンを隠す (本編は出ているボタンだけを並べる)
    internal static void Refresh()
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            var e = _buttons[i];
            if (!e.Button) continue;
            bool on = e.Ability.UsesLeft != 0 && e.Ability.MeetingLeft != 0;
            if (e.Button.activeSelf != on) e.Button.SetActive(on);
        }
    }

    // 確かめ用: その人の名札を押した時と同じく並びを開く
    internal static string Open(byte target)
    {
        var hud = MeetingHud.Instance;
        if (!hud) return "no meeting";
        foreach (var pva in hud.playerStates)
            if (pva && pva.PlayerId.Value == target)
            {
                var plate = pva.transform.Find("votePlayerBase");
                var pb = plate ? plate.GetComponent<PassiveButton>() : null;
                if (pb) pb.OnClick.Invoke();
                else pva.Select();
                return $"opened {target} via={(pb ? "plate" : "select")} buttons={(pva.Buttons && pva.Buttons.activeSelf)}";
            }
        return "no plate";
    }

    // 確かめ用: 能力 index の target のボタンを押す (本物の押下と同じ OnClick)。-1 = 一覧だけ
    internal static string Probe(int index, int target)
    {
        var sb = new System.Text.StringBuilder($"gen={Gen} buttons={_buttons.Count}");
        foreach (var e in _buttons)
        {
            if (!e.Button) continue;
            var t = e.Button.transform;
            sb.Append($" #{e.Ability.Index}>{e.Target}:{(e.Button.activeSelf ? "on" : "off")}@{t.localPosition.x:0.##}");
        }
        if (index < 0) return sb.ToString();
        foreach (var e in _buttons)
        {
            if (e.Ability.Index != index || e.Target != target || !e.Button) continue;
            e.Button.GetComponent<PassiveButton>()?.OnClick.Invoke();
            return sb.Append($" pressed=#{index}>{target}").ToString();
        }
        return sb.Append(" press: no button").ToString();
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class MeetingAbilitiesStartPatch
{
    public static void Postfix(MeetingHud __instance)
    {
        try { MeetingAbilities.OnMeetingStart(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"meeting abilities: {e}"); }
    }
}
