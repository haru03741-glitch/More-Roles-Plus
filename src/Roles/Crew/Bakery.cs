// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Bakery.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// パン屋 (クルー): 近くの人にパンを渡す (1 ターンに決めた数まで)。パンを持つ人は会議の名札にパンの印が出る。
// 会議が終わるとパンは消える。パン屋が生きていれば、追放の画面にパンの一言が出る
public sealed class Bakery : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#03FC6F";
    public override Text Name => new("Bakery.Name");
    public override string Reading => "ぱんや";
    public override Text Blurb => new("Bakery.Blurb");
    public override string Origin => "Uchu";
    public override bool CanBeMadmate => true;
    public override HelpSection[] HelpSections => new HelpSection[] { new("Bakery.Give", "bakery_bread") };

    public static readonly FloatOpt GiveCooldown = new("Bakery.GiveCooldown", 10f, 0f, 60f, 2.5f, "s");
    public static readonly IntOpt BreadPerMeeting = new("Bakery.BreadPerMeeting", 3, 1, 15);

    // パンを持っている人 (全員の端末で同じ)
    private static readonly HashSet<byte> Holders = new();
    private static object _markMatch;

    public override void OnAssigned()
    {
        if (!ReferenceEquals(_markMatch, RoleState.Match))
        {
            _markMatch = RoleState.Match;
            RoleState.Match.OnRelease(() => { Holders.Clear(); _markMatch = null; });
            // 会議の名札にパンの印 (全員に見える)
            RoleFrame.AddExtraMark(RoleState.Match, seen => MeetingHud.Instance && Holders.Contains(seen.PlayerId) ? "<color=#E0A060>●</color>" : null);
        }
        AddAbility(new Ability(new Text("Bakery.Give"), "bakery_bread")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => GiveCooldown,
            UsesPerMeeting = BreadPerMeeting,
            CanTarget = (_, t) => !Holders.Contains(t.PlayerId),
            OnUsed = (_, t) =>
            {
                Holders.Add(t.PlayerId);
                if (t.AmOwner) PortKit.Notice(new Text("Bakery.Got").ToString());
            },
        });
    }

    internal static bool HasBread(byte id) => Holders.Contains(id);

    private void OnMeetingEnd(MeetingEndEvent e) => Holders.Clear();

    // 追放の画面: パン屋が生きていれば一言足す
    private void OnExileShown(ExileShownEvent e)
    {
        if (!PortKit.Alive(Player) || !e.Controller) return;
        // 同じ画面に何人ものパン屋がいても一言は 1 つ
        foreach (var r in RoleState.All)
            if (RoleState.Acting(r) is Bakery b && PortKit.Alive(b.Player)) { if (b != this) return; break; }
        float roll = UnityEngine.Random.value * 100f;
        string key = roll < 0.6f ? "Bakery.Exile.Festival" : roll < 10.6f ? "Bakery.Exile.Burnt" : "Bakery.Exile.Smell";
        try { e.Controller.completeString += "\n" + new Text(key).ToString(); }
        catch (System.Exception ex) { Plugin.Logger.LogWarning($"bakery exile text: {ex.Message}"); }
    }
}
