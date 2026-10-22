using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

public enum AbilityTarget : byte
{
    Self,   // 狙い無し。押した人の端末ですぐ効く (他の人に知らせる時は役職が自分で電文を送る)
    Player, // 近くの人を狙う。ホストが確かめてから全員の端末で OnUsed が呼ばれる
    Pick,   // 押すと人を選ぶ画面 (PlayerPicker) が開き、選んだ人を狙う (距離は見ない)。ホストが確かめてから全員の端末で OnUsed が呼ばれる
    Meeting, // 会議中に名札を押すと ✓/✗ の隣に出るボタンで人を選ぶ。ホストが確かめてから全員の端末で OnUsed が呼ばれる
}

// 能力 1 つ (ボタン 1 つ)。役職の OnAssigned の中で作る:
//   AddAbility(new Ability(new Text("Demolisher.Hammer"), "hammer")
//   {
//       Cooldown = () => SmashCooldown,
//       MaxUses = () => SmashUses,
//       OnUse = () => Terrain.TerrainApi.Hammer().Ok,
//   });
// 待ち時間・回数・狙いの条件は、ホストが客の依頼を確かめる時にも使うので設定値だけから決める (その端末の状態を見ない)。
// 待ち時間は本編の能力と同じく、試合の始めと会議の後に最初から数え直し、動けない間は止まる。
public sealed class Ability
{
    public readonly Text Label;
    public readonly string Icon;

    public AbilityTarget Target { get; init; }

    // 待ち時間 (秒・null = 無し)
    public Func<float> Cooldown { get; init; }

    // 効果の長さ (秒・null = 無し)。使ってから効果の間はボタンに残りを出し、切れてから待ち時間を始める (会議で切れる)
    public Func<float> Duration { get; init; }

    // 効果が切れた時 (使った人の端末で)
    public Action OnEnd { get; init; }

    // 使える回数 (null か 0 以下 = 何回でも)
    public Func<int> MaxUses { get; init; }

    // Meeting: 1 回の会議で使える回数 (0 = 何回でも)
    public int UsesPerMeeting { get; init; }

    // Player の届く距離 (0 = 部屋の設定のキルの距離)
    public float Range { get; init; }

    // Player / Pick / Meeting: 狙ってよい相手か (使う人, 相手)。null = 生きている誰でも
    public Func<PlayerControl, PlayerControl, bool> CanTarget { get; init; }

    // Self: 押した時 (自分の端末)。true = 使えた (待ち時間と回数を使う)
    public Func<bool> OnUse { get; init; }

    // Player / Pick / Meeting: ホストが通した後に全員の端末で (使う人, 相手)
    public Action<PlayerControl, PlayerControl> OnUsed { get; init; }

    // 試合の始めと会議の後は待ち時間から始める
    public bool CooldownAtStart { get; init; } = true;

    public RoleBase Role { get; private set; }
    public int Index { get; private set; }

    // 残りの回数 (-1 = 何回でも)。Player の能力は全員の端末で同じ数になる
    public int UsesLeft { get; private set; } = -1;

    internal ModButton Button;
    internal PlayerControl Aim;   // 自分の端末で今狙っている相手
    internal long PendingUntil;   // ホストの返事待ち (この時刻まで押し直さない)
    internal long HostLastMs;     // ホストが最後に通した時刻 (ホストの判定だけが使う)
    internal Il2CppSystem.Nullable<Color> OutlineOn; // 縁取りの色 (最初に狙った時に 1 回だけ作る)
    internal int MeetingUses, MeetingUsesGen;     // この会議で使った回数 (MeetingUsesGen が今の会議の番号の時だけ有効)

    // この会議であと何回使えるか (-1 = 何回でも)
    internal int MeetingLeft
    {
        get
        {
            if (UsesPerMeeting <= 0) return -1;
            int used = MeetingUsesGen == MeetingAbilities.Gen ? MeetingUses : 0;
            return Math.Max(UsesPerMeeting - used, 0);
        }
    }

    public Ability(Text label, string icon)
    {
        Label = label;
        Icon = icon;
    }

    internal void Bind(RoleBase role, int index)
    {
        Role = role;
        Index = index;
        int max = MaxUses?.Invoke() ?? 0;
        UsesLeft = max > 0 ? max : -1;
        // 割り当ては始めの演出の前なので、ここから数えればホストの判定は本人のボタンより緩い側にずれる
        HostLastMs = CooldownAtStart ? Environment.TickCount64 : long.MinValue / 2;
    }

    internal float CooldownSeconds => Math.Max(Cooldown?.Invoke() ?? 0f, 0f);

    internal void Spend()
    {
        if (UsesLeft > 0) UsesLeft--;
        if (Target == AbilityTarget.Meeting)
        {
            if (MeetingUsesGen != MeetingAbilities.Gen) { MeetingUsesGen = MeetingAbilities.Gen; MeetingUses = 0; }
            MeetingUses++;
        }
        Button?.SetUses(UsesLeft);
    }
}

// 能力を動かす所。自分の能力のボタン・狙い (FixedUpdate) と、Player の能力の依頼 (客 → ホスト → 全員)
internal static class Abilities
{
    private const float DistanceSlack = 1f;   // 通信の遅れで相手が動いた分の余裕
    // 待ち時間の確かめの余裕 (秒)。通信の遅れは待ち時間の長さによらないので割合でなく秒で取る
    private const float CooldownSlack = 1.5f;
    private const int PendingMs = 1500;       // ホストの返事を待つ間は押し直さない
    private const int ScanEvery = 5;          // 狙う相手を探し直す間隔 (FixedUpdate の回数・0.1 秒)

    private static RoleBase _local;
    private static int _scan;
    private static Il2CppSystem.Nullable<Color> _outlineOff;

    // 確かめ用: ホストが最後に断った理由
    internal static string LastRefusal = "-";

    internal static IReadOnlyList<Ability> Local => _local != null ? _local.Abilities : Array.Empty<Ability>();

    internal static void OnAssigned(RoleBase role)
    {
        if (!role.IsLocal || role.Abilities.Count == 0) return;
        _local = role;
        EnsureButtons();
    }

    internal static void Clear()
    {
        if (_local != null)
            foreach (var a in _local.Abilities) SetAim(a, null);
        _local = null;
        LastRefusal = "-";
    }

    // ボタンを作る (HUD がまだ無くて作れなかった物は、次に HUD が出た時に作り直す)
    internal static void EnsureButtons()
    {
        var role = _local;
        if (role == null || role.Lifespan == null || role.Lifespan.IsDead) return;
        foreach (var a in role.Abilities)
        {
            if (a.Button != null || a.Target == AbilityTarget.Meeting) continue; // 会議の能力は名札に出す (MeetingAbilities)
            var ab = a;
            a.Button = ModButton.Create(role.Lifespan, ButtonIcons.Get(a.Icon), a.Label, a.CooldownSeconds, () => Click(ab));
            if (a.Button == null) continue;
            a.Button.ResetOnShow = a.CooldownAtStart;
            a.Button.Effect = a.Duration;
            a.Button.OnEffectEnd = a.OnEnd;
            a.Button.SetUses(a.UsesLeft);
            a.Button.SetUsable(a.Target != AbilityTarget.Player && a.UsesLeft != 0);
            if (a.CooldownAtStart) a.Button.StartCooldown();
        }
    }

    private static bool Click(Ability a)
    {
        if (a.UsesLeft == 0) return false;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead || !lp.CanMove) return false;
        if (a.Target == AbilityTarget.Self)
        {
            if (a.OnUse == null || !a.OnUse()) return false;
            a.Spend();
            if (a.UsesLeft == 0) a.Button?.SetUsable(false);
            return true;
        }
        long now = Environment.TickCount64;
        if (a.Target == AbilityTarget.Pick)
        {
            if (now < a.PendingUntil) return false;
            PlayerPicker.Open(info => !info.IsDead && info.PlayerId != lp.PlayerId && info.Object && (a.CanTarget == null || a.CanTarget(lp, info.Object)),
                id => PickChosen(a, id));
            return false;
        }
        var target = a.Aim;
        if (target is null || !target || now < a.PendingUntil) return false;
        a.PendingUntil = now + PendingMs;
        a.Button?.SetUsable(false);
        // 待ち時間はホストが通した時 (Apply) に始める。ホスト自身なら Send の中で手元で判定する
        Request.Send(((byte)a.Index, target.PlayerId));
        return false;
    }

    // 毎 FixedUpdate。Player の能力がある時だけ、0.1 秒ごとに狙う相手を選び直す
    internal static void Tick()
    {
        var role = _local;
        if (role == null || ++_scan < ScanEvery) return;
        _scan = 0;
        var lp = role.Player;
        bool live = ModButton.HudShown && lp && lp.Data != null && !lp.Data.IsDead;
        long now = Environment.TickCount64;
        foreach (var a in role.Abilities)
        {
            if (a.Target == AbilityTarget.Pick && a.Button != null)
                a.Button.SetUsable(live && a.UsesLeft != 0 && now >= a.PendingUntil);
            if (a.Target != AbilityTarget.Player || a.Button == null) continue;
            var aim = live && lp.CanMove ? Targeting.ClosestPlayer(lp, Reach(a), a.CanTarget) : null;
            SetAim(a, aim);
            a.Button.SetUsable(aim is not null && a.UsesLeft != 0 && now >= a.PendingUntil);
        }
    }

    // キー (Menu.Keybinds) で i 番目のボタンを押す (右下に並んでいる能力のボタンの順)
    internal static void PressSlot(int i)
    {
        var role = _local;
        if (role == null) return;
        foreach (var a in role.Abilities)
        {
            if (a.Button == null) continue;
            if (i-- == 0) { a.Button.Press(); return; }
        }
    }

    // 人を選ぶ画面で選んだ (自分の端末)。待ち時間はホストが通した時 (Apply) に始める
    private static void PickChosen(Ability a, byte targetId)
    {
        long now = Environment.TickCount64;
        if (a.UsesLeft == 0 || now < a.PendingUntil) return;
        a.PendingUntil = now + PendingMs;
        a.Button?.SetUsable(false);
        Request.Send(((byte)a.Index, targetId));
    }

    // 会議の名札のボタンを押した (自分の端末)
    internal static void MeetingClick(Ability a, byte targetId)
    {
        long now = Environment.TickCount64;
        if (a.UsesLeft == 0 || a.MeetingLeft == 0 || now < a.PendingUntil) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead) return;
        a.PendingUntil = now + PendingMs;
        Request.Send(((byte)a.Index, targetId));
    }

    private static float Reach(Ability a) => a.Range > 0f ? a.Range : GameManager.Instance.LogicOptions.GetKillDistance();

    // 狙う相手の縁取り (相手が替わった時だけ書く)
    private static void SetAim(Ability a, PlayerControl aim)
    {
        var old = a.Aim;
        // ゲーム側の物の包みは取り出すたびに別の物になりうるので、同じ人かは番号で比べる
        if (old is null ? aim is null : aim is not null && old.PlayerId == aim.PlayerId) return;
        a.Aim = aim;
        if (old is not null && old && old.cosmetics)
            old.cosmetics.SetOutline(false, _outlineOff ??= new Il2CppSystem.Nullable<Color>(new Color(0f, 0f, 0f, 0f)));
        if (aim is not null && aim.cosmetics)
            aim.cosmetics.SetOutline(true, a.OutlineOn ??= new Il2CppSystem.Nullable<Color>(OutlineColor(a.Role.Color)));
    }

    // 役職の色 "#RRGGBB"
    private static Color OutlineColor(string hex)
    {
        if (hex == null || hex.Length < 7 || hex[0] != '#'
            || !uint.TryParse(hex.AsSpan(1, 6), System.Globalization.NumberStyles.HexNumber, null, out uint v))
            return new Color(1f, 1f, 1f, 1f);
        return new Color(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, 1f);
    }

    // ---- ホスト ----

    private static readonly RemoteCall<(byte ability, byte target)> Request = new("Ability.Request", Route.ToHost,
        (w, m) => { w.Write(m.ability); w.Write(m.target); },
        r => (r.ReadByte(), r.ReadByte()),
        (user, m) => HostUse(user, m.ability, m.target));

    private static readonly RemoteCall<(byte user, byte ability, byte target)> Used = new("Ability.Used", Route.HostToAll,
        (w, m) => { w.Write(m.user); w.Write(m.ability); w.Write(m.target); },
        r => (r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (_, m) => Apply(m.user, m.ability, m.target));

    private static void HostUse(PlayerControl user, byte index, byte targetId)
    {
        var target = GameData.Instance ? GameData.Instance.GetPlayerById(targetId)?.Object : null;
        var role = RoleState.Of(user);
        var a = role != null && index < role.Abilities.Count ? role.Abilities[index] : null;
        string why = Refuse(user, a, target);
        if (why != null)
        {
            LastRefusal = $"{(user ? user.PlayerId : -1)}#{index}->{targetId}: {why}";
            Plugin.Logger.LogWarning($"ability refused {LastRefusal}");
            return;
        }
        a.HostLastMs = Environment.TickCount64;
        Used.Send((user.PlayerId, index, targetId));
        Apply(user.PlayerId, index, targetId); // 配る電文は送った本人の手元では動かない
    }

    private static string Refuse(PlayerControl user, Ability a, PlayerControl target)
    {
        if (a == null || a.Target == AbilityTarget.Self) return "ability";
        bool meeting = a.Target == AbilityTarget.Meeting;
        if (AmongUsClient.Instance.IsGameOver || !ShipStatus.Instance) return "not now";
        var hud = MeetingHud.Instance;
        if (meeting ? !hud || hud.state is MeetingHud.MeetingStates.Results or MeetingHud.MeetingStates.Proceeding
                    : hud || ExileController.Instance) return "not now";
        if (!user || user.Data == null || user.Data.IsDead || user.Data.Disconnected) return "user";
        if (!meeting && (user.inVent || user.inMovingPlat)) return "user busy";
        if (a.UsesLeft == 0) return "no uses";
        if (a.MeetingLeft == 0) return "no uses this meeting";
        if (!target || target == user || target.Data == null || target.Data.IsDead || target.Data.Disconnected) return "target";
        if (a.CanTarget != null && !a.CanTarget(user, target)) return "target not allowed";
        if (meeting) return null; // 会議の能力は距離と待ち時間を見ない
        if (a.Target == AbilityTarget.Pick)
            return (Environment.TickCount64 - a.HostLastMs) / 1000f < a.CooldownSeconds - CooldownSlack && !Dev.DevCooldown.Skips(user) ? "cooldown" : null;
        if (target.inVent || target.inMovingPlat) return "target busy";
        string far = Targeting.Unreachable(user, target, Reach(a) + DistanceSlack);
        if (far != null) return far;
        if ((Environment.TickCount64 - a.HostLastMs) / 1000f < a.CooldownSeconds - CooldownSlack && !Dev.DevCooldown.Skips(user)) return "cooldown";
        return null;
    }

    // ホストが通した (全員の端末で)
    private static void Apply(byte userId, byte index, byte targetId)
    {
        var role = RoleState.Of(userId);
        if (role == null || index >= role.Abilities.Count) return;
        var a = role.Abilities[index];
        a.Spend();
        if (role.IsLocal && a.Target == AbilityTarget.Meeting)
        {
            a.PendingUntil = 0;
            MeetingAbilities.Refresh();
        }
        if (role.IsLocal && a.Button != null)
        {
            a.PendingUntil = 0;
            a.Button.StartUsed();
            if (a.UsesLeft == 0) a.Button.SetUsable(false);
        }
        var target = GameData.Instance ? GameData.Instance.GetPlayerById(targetId)?.Object : null;
        if (a.OnUsed == null || !role.Player || !target) return;
        try { a.OnUsed(role.Player, target); }
        catch (Exception e) { Plugin.Logger.LogError($"{role.Id} ability #{index}: {e}"); }
    }

    // 確かめ用の一覧 (press = 押す番号・-1 で押さない)
    internal static string Probe(int press)
    {
        var list = Local;
        var sb = new System.Text.StringBuilder($"role={_local?.Id ?? "-"} n={list.Count}");
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            string aim = a.Aim is not null && a.Aim ? a.Aim.PlayerId.ToString() : "-";
            sb.Append($" #{i}={a.Label}/{a.Target} uses={a.UsesLeft} cd={a.CooldownSeconds:0.#} aim={aim} button=[{(a.Button != null ? a.Button.Describe() : "none")}]");
        }
        if (press >= 0 && press < list.Count && list[press].Button != null)
        {
            list[press].Button.Press();
            sb.Append($" pressed=#{press} -> [{list[press].Button.Describe()}]");
        }
        sb.Append($" hostRefused=[{LastRefusal}]");
        return sb.ToString();
    }
}

// 能力ボタンを作れなかった時 (HUD がまだ無かった) に、HUD が出たら作り直す
[HarmonyPatch(typeof(HudManager), nameof(HudManager.SetHudActive), typeof(PlayerControl), typeof(RoleBehaviour), typeof(bool))]
internal static class AbilityHudPatch
{
    public static void Postfix(bool isActive)
    {
        if (isActive) Abilities.EnsureButtons();
    }
}
