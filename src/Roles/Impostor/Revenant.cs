// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Ghost/Revenant.cs (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// レヴェナント (幽霊役職・インポスター側): ネクロマンサーに呪われた幽霊がなる。幽霊のタスクを全部終えるごとに 1 回
// 「憑依」でき、生きているクルーに取り憑いて、しばらくその人の視界を奪い通報もできなくする (会議が終わると解ける)。
// 設定はネクロマンサーの所にある
public sealed class Revenant : RoleBase, ITaskDoer
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Revenant.Name");
    public override string Reading => "れゔぇなんと";
    public override Text Blurb => new("Revenant.Blurb");
    public override string Origin => "SNR";
    public override bool MadeByOthers => true;
    public override bool TasksCount => false;
    public override HelpSection[] HelpSections => new HelpSection[] { new("Revenant.Haunt", "snr_curse") };

    // 取り憑かれている人 (番号 → 解ける時刻)。全員の端末で同じ
    private static readonly Dictionary<byte, float> Haunted = new();
    private static readonly Dictionary<byte, SpriteRenderer> Wisps = new();
    internal static bool LocalHaunted;

    private Ability _haunt;
    private int _charges;
    private readonly Dictionary<byte, WorldArrow> _arrows = new();

    public override void OnAssigned()
    {
        ClearHaunt(PlayerId);
        _haunt = AddAbility(new Ability(new Text("Revenant.Haunt"), "snr_curse")
        {
            Ghost = true,
            Target = AbilityTarget.Player,
            CanTarget = (_, t) => (!IsLocal || _charges > 0) && !PortKit.IsImpostor(t), // 回数は本人の端末だけで数える
            OnUsed = (user, target) =>
            {
                if (user.AmOwner)
                {
                    _charges--;
                    _haunt?.Button?.SetUses(_charges);
                }
                SetHaunt(target.PlayerId, true);
            },
        });
        Lifespan.OnRelease(() =>
        {
            foreach (var w in Wisps.Values) if (w) Object.Destroy(w.gameObject);
            Wisps.Clear();
            Haunted.Clear();
            LocalHaunted = false;
            SnrKit.FixedVision = -1f;
        });
        if (!IsLocal) return;
        if (Minigame.Instance) Minigame.Instance.Close();
        PortKit.After(0.3f, () =>
        {
            if (RoleState.Of(PlayerControl.LocalPlayer) != this) return;
            _haunt?.Button?.SetUses(_charges);
            ReassignTasks();
        });
    }

    // 必要なタスクの数を、本編の設定の共通・短い・長いの割合で引き直して配る
    private static void ReassignTasks()
    {
        var o = GameOptionsManager.Instance.CurrentGameOptions;
        var kinds = new List<int>();
        for (int i = 0; i < o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumCommonTasks); i++) kinds.Add(0);
        for (int i = 0; i < o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumShortTasks); i++) kinds.Add(1);
        for (int i = 0; i < o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumLongTasks); i++) kinds.Add(2);
        for (int i = kinds.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (kinds[i], kinds[j]) = (kinds[j], kinds[i]);
        }
        int common = 0, shortT = 0, longT = 0;
        for (int i = 0; i < Necromancer.RequiredTasks; i++)
        {
            int k = i < kinds.Count ? kinds[i] : 1;
            if (k == 0) common++;
            else if (k == 1) shortT++;
            else longT++;
        }
        SnrKit.AssignOwnTasks(common, shortT, longT);
    }

    // 自分のタスクを全部終えたら、配り直して憑依を 1 回増やす
    [LocalOnly]
    private void OnTask(TaskCompletedEvent e)
    {
        if (!e.Player || !e.Player.AmOwner || !PortKit.AllTasksDone(e.Player)) return;
        _charges++;
        _haunt?.Button?.SetUses(_charges);
        ReassignTasks();
    }

    // 会議が終わったら憑依は全部解ける
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        foreach (var id in new List<byte>(Haunted.Keys)) SetHaunt(id, false);
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        // ネクロマンサーへの矢印
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null) continue;
            bool necro = RoleState.Of(p) is Necromancer && !p.Data.Disconnected;
            if (!_arrows.TryGetValue(p.PlayerId, out var arrow))
            {
                if (!necro) continue;
                arrow = _arrows[p.PlayerId] = new WorldArrow(Lifespan, UnityEngine.Color.red);
            }
            arrow.SetActive(necro && !MeetingHud.Instance);
            if (necro) arrow.Update(p.transform.position);
        }
    }

    // 全員の端末で: 憑依の時間切れと人魂の位置
    internal static void Tick()
    {
        if (Haunted.Count == 0) return;
        List<byte> done = null;
        foreach (var kv in Haunted)
        {
            if (kv.Value <= Time.time) (done ??= new List<byte>()).Add(kv.Key);
            else if (Wisps.TryGetValue(kv.Key, out var w) && w && PortKit.ById(kv.Key) is { } p)
                w.transform.position = p.transform.position + new Vector3(0f, 0.6f, -0.0001f);
        }
        if (done != null) foreach (var id in done) SetHaunt(id, false);
    }

    private static bool SeesWisps => RoleState.Local is Necromancer or Revenant;

    private static void SetHaunt(byte id, bool on)
    {
        var p = PortKit.ById(id);
        if (Wisps.TryGetValue(id, out var old))
        {
            if (old) Object.Destroy(old.gameObject);
            Wisps.Remove(id);
        }
        if (on)
        {
            Haunted[id] = Time.time + Necromancer.HauntDuration;
            if (SeesWisps && p) Wisps[id] = PortKit.Marker(RoleState.Match, p.transform.position, "snr_hitodama", 0.5f);
        }
        else Haunted.Remove(id);
        if (p && p.AmOwner)
        {
            LocalHaunted = on;
            SnrKit.FixedVision = on ? Necromancer.HauntVision : -1f;
            if (on) StatusEffects.ShowFor("Revenant", new Text("Revenant.Haunted"), "#FF1919", Necromancer.HauntDuration);
            else StatusEffects.Hide("Revenant");
        }
    }

    private static void ClearHaunt(byte id)
    {
        if (Haunted.ContainsKey(id)) SetHaunt(id, false);
    }

    internal static bool ReportBlocked => LocalHaunted && Necromancer.CannotReportWhileHaunted;
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class RevenantHudPatch
{
    public static void Postfix(HudManager __instance)
    {
        Revenant.Tick();
        if ((Revenant.ReportBlocked || Neutral.God.LocalNoReport) && __instance.ReportButton) __instance.ReportButton.SetDisabled();
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdReportDeadBody))]
internal static class RevenantReportPatch
{
    public static bool Prefix(PlayerControl __instance) => !(__instance.AmOwner && (Revenant.ReportBlocked || Neutral.God.LocalNoReport));
}

[HarmonyPatch(typeof(EmergencyMinigame), nameof(EmergencyMinigame.Begin))]
internal static class RevenantEmergencyPatch
{
    public static void Postfix(EmergencyMinigame __instance)
    {
        if (Revenant.ReportBlocked) __instance.Close();
        else if (Neutral.God.LocalNoEmergency)
        {
            __instance.Close();
            PortKit.Notice(new Text("God.NoEmergencyText").ToString());
        }
        else if (Neutral.Bullet.LocalNoEmergency)
        {
            __instance.Close();
            PortKit.Notice(new Text("Bullet.NoEmergencyText").ToString());
        }
    }
}
