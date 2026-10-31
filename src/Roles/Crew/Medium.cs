// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/Medium.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ミーディアム (クルー): 「霊視」の間は幽霊が見え、その間に「霊話」で幽霊から 1 つ話を聞ける
// (倒した人の役職・倒した人の色の明暗・死んだ部屋・その人の役職のどれか)。話は次の会議のチャットに出る
public sealed class Medium : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#4FFFE3";
    public override Text Name => new("Medium.Name");
    public override string Reading => "みーでぃあむ";
    public override Text Blurb => new("Medium.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Medium.Vision", "snr_soul"), new("Medium.Talk", "snr_read") };

    public static readonly FloatOpt VisionCooldown = new("Medium.Cooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt VisionDuration = new("Medium.Duration", 5, 1, 30, 1, "s");
    public static readonly IntOpt TalkMaxUses = new("Medium.TalkMaxUses", 1, 1, 20);

    // SNR の明るい色 (本編の色の番号)
    private static readonly HashSet<int> LighterColors = new() { 3, 4, 5, 7, 10, 11, 13, 14, 17 };

    private Ability _vision;
    private readonly List<string> _pending = new();

    public override void OnAssigned()
    {
        _vision = AddAbility(new Ability(new Text("Medium.Vision"), "snr_soul")
        {
            Cooldown = () => VisionCooldown,
            Duration = () => VisionDuration,
            OnUse = () =>
            {
                StatusEffects.ShowFor("Medium", new Text("Medium.VisionActive"), Color, VisionDuration);
                return true;
            },
            OnEnd = () => StatusEffects.Hide("Medium"),
        });
        AddAbility(new Ability(new Text("Medium.Talk"), "snr_read")
        {
            Target = AbilityTarget.Player,
            TargetDead = true,
            MaxUses = () => TalkMaxUses,
            CanTarget = (_, _) => !IsLocal || (_vision != null && _vision.InEffect),
            OnUsed = (user, target) =>
            {
                if (!user.AmOwner) return;
                _pending.Add(Talk(target));
                _vision?.EndEffect();
            },
        });
    }

    // 霊視の間は幽霊を見る
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (_vision != null && _vision.InEffect && !MeetingHud.Instance) SnrKit.ShowGhosts();
    }

    // 会議が始まったら、聞いた話をチャットに出す (自分の画面だけ)
    [LocalOnly]
    private void OnMeeting(MeetingStartEvent e)
    {
        if (_vision != null && _vision.InEffect) _vision.EndEffect();
        if (_pending.Count == 0) return;
        PortKit.After(0.5f, () =>
        {
            var chat = Vanilla.Chat;
            if (!chat || !Player) return;
            foreach (var m in _pending) chat.AddChat(Player, m, false);
            _pending.Clear();
        });
    }

    private static string Talk(PlayerControl target)
    {
        string name = target.Data?.PlayerName ?? "?";
        var d = MatchLog.Of(target.PlayerId);
        if (target.Data == null || target.Data.Disconnected || d == null || d.Kind != MatchLog.Kind.Killed)
            return new Text("Medium.Exiled").With("name", name).ToString();
        switch (Random.Range(1, 5))
        {
            case 1:
                return new Text("Medium.KillerRole").With("name", name).With("role", d.KillerRole ?? "?").ToString();
            case 2:
                var killer = PortKit.ById(d.KillerId);
                bool light = killer && killer.Data != null && LighterColors.Contains(killer.Data.DefaultOutfit.ColorId);
                return new Text("Medium.KillerColor").With("name", name).With("color", new Text(light ? "Medium.Light" : "Medium.Dark")).ToString();
            case 3:
                return new Text("Medium.Place").With("name", name).With("place", Room(d.Where)).ToString();
            default:
                var role = RoleState.Of(target);
                return new Text("Medium.Role").With("name", name).With("role", role != null ? role.Name.ToString() : "?").ToString();
        }
    }

    private static string Room(Vector2 at)
    {
        var ship = ShipStatus.Instance;
        if (ship && ship.AllRooms != null)
            foreach (var room in ship.AllRooms)
                if (room && room.roomArea && room.roomArea.OverlapPoint(at))
                    return TranslationController.Instance.GetString(room.RoomId);
        return new Text("Medium.NoRoom").ToString();
    }
}
