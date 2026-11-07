// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Neutral/Seed.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// シード (第三陣営): 「共生」で近くの人と共生する (相手に ▼。シードにだけ見え、設定で役職も見える)。
// 共生した相手の誰かが勝てば、一緒に勝つ (相手が死んでいたら・自分が死んでいたら勝てない設定あり)。
// ゲージありなら、相手の近くにいるとゲージが貯まり、離れると減る。勝つにはゲージが満ちている必要がある
public sealed class Seed : RoleBase
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => "#6AB2BD";
    public override Text Name => new("Seed.Name");
    public override string Reading => "しーど";
    public override Text Blurb => new("Seed.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Seed.Symbiosis", "seed_symbiosis"), new("Seed.Gauge") };

    public static readonly FloatOpt SymbiosisCooldown = new("Seed.SymbiosisCooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt NumOfSymbiosis = new("Seed.NumOfSymbiosis", 2, 1, 15);
    public static readonly BoolOpt SeeRole = new("Seed.SeeRole", true);
    public static readonly BoolOpt NoWinIfPartnerDead = new("Seed.NoWinIfPartnerDead", true);
    public static readonly BoolOpt NoWinIfSelfDead = new("Seed.NoWinIfSelfDead", true);
    public static readonly BoolOpt UseGauge = new("Seed.UseGauge", true);
    public static readonly FloatOpt GaugeRange = new("Seed.GaugeRange", 2f, 0.5f, 10f, 0.25f, "") { When = () => UseGauge };
    public static readonly FloatOpt GaugeNeed = new("Seed.GaugeNeed", 20f, 5f, 120f, 5f, "s") { When = () => UseGauge };
    public static readonly FloatOpt GaugeExtra = new("Seed.GaugeExtra", 20f, 0f, 120f, 5f, "s") { When = () => UseGauge };
    public static readonly FloatOpt GaugeDecay = new("Seed.GaugeDecay", 0.25f, 0f, 2f, 0.05f, "") { When = () => UseGauge };
    public static readonly FloatOpt PauseAfterMeeting = new("Seed.PauseAfterMeeting", 5f, 0f, 30f, 1f, "s") { When = () => UseGauge };

    private readonly HashSet<byte> _partners = new(); // 全員の端末で同じ
    private float _gauge;                              // 自分の端末
    private float _pauseUntil;
    private bool _full;                                // 全員の端末で同じ (満ちたか)

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Seed.Symbiosis"), "seed_symbiosis")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => SymbiosisCooldown,
            MaxUses = () => NumOfSymbiosis,
            CanTarget = (_, t) => !_partners.Contains(t.PlayerId),
            OnUsed = (_, t) => _partners.Add(t.PlayerId),
        });
    }

    private void OnMeetingEnd(MeetingEndEvent e) => _pauseUntil = Time.time + PauseAfterMeeting;

    private HudReadout _readout;

    // 共生のゲージは画面の下の札に出す (元は名前の横の字)
    private void ShowReadout()
    {
        _readout ??= new HudReadout(Lifespan, UI.RoleArt.Own(Id), new Color32(106, 178, 189, 255));
        bool on = UseGauge && _partners.Count > 0 && PortKit.Alive(Player);
        if (!on) { _readout.Set(false); return; }
        float need = GaugeNeed;
        string col = _gauge >= need ? "#6AB2BD" : "#FFFFFF";
        _readout.Set(true, $"<color={col}>{Mathf.FloorToInt(_gauge)}/{Mathf.RoundToInt(need)}</color>", need > 0f ? _gauge / need : 1f);
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        ShowReadout();
        if (!UseGauge || _partners.Count == 0 || MeetingHud.Instance || !PortKit.Alive(Player) || Time.time < _pauseUntil) return;
        bool near = false;
        Vector2 me = Player.GetTruePosition();
        foreach (var id in _partners)
        {
            var p = PortKit.ById(id);
            if (PortKit.Alive(p) && Vector2.Distance(p.GetTruePosition(), me) <= GaugeRange) { near = true; break; }
        }
        float dt = Time.fixedDeltaTime;
        _gauge = Mathf.Clamp(_gauge + (near ? dt : -GaugeDecay * dt), 0f, GaugeNeed + GaugeExtra);
        bool full = _gauge >= GaugeNeed;
        if (full != _full)
        {
            _full = full;
            FullCall.Send(full);
        }
    }

    private static readonly RemoteCall<bool> FullCall = new("Seed.Full", Route.Anyone,
        (w, m) => w.Write(m), r => r.ReadBoolean(),
        (sender, m) => { if (RoleState.Of(sender) is Seed s) s._full = m; }, perSecond: 4);

    // 共生した相手の誰かが勝てば一緒に勝つ
    public override bool AlsoWins(GameResult result)
    {
        if (NoWinIfSelfDead && !PortKit.Alive(Player)) return false;
        if (UseGauge && !_full) return false;
        foreach (var id in _partners)
        {
            if (!result.Winners.Contains(id)) continue;
            if (NoWinIfPartnerDead && !PortKit.Alive(PortKit.ById(id))) continue;
            return true;
        }
        return false;
    }

    public override string NameMark(PlayerControl seen)
    {
        if (seen.PlayerId == PlayerId) return null;
        if (!_partners.Contains(seen.PlayerId)) return null;
        string mark = "<color=#6AB2BD>▼</color>";
        if (SeeRole && RoleState.Of(seen) is { } r) mark += $" <size=70%>{r.ShownColoredName}</size>";
        return mark;
    }
}
