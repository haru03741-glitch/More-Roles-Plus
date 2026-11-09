// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/NiceDecorator.cs, Impostor/EvilDecorator.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// デコレーター (ナイス / イビル 共通): 「装飾」で近くの人の役職を無作為の別の役職に変える。
// 本当のクルーならクルーの役職から、インポスターならインポスターの役職から選ぶ (前にその人に付けた役職は避ける)。
// 第三陣営とマッドメイトは変わらず、回数だけ減る
public abstract class DecoratorBase : RoleBase
{
    public override HelpSection[] HelpSections => new HelpSection[] { new(Id + ".Decorate", "decorator_ribbon") };

    protected abstract float DecorateCooldown { get; }
    protected abstract int DecorateUses { get; }

    // その人に前に付けた役職 (ホストの端末)
    private readonly Dictionary<byte, string> _given = new();

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text(Id + ".Decorate"), "decorator_ribbon")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => DecorateCooldown,
            MaxUses = () => DecorateUses,
            OnUsed = (_, t) =>
            {
                if (!AmongUsClient.Instance.AmHost) return;
                var now = RoleState.Of(t);
                if (now == null) return;
                RoleBase next = null;
                if (PortKit.IsImpostor(t)) next = UchuKit.RandomRole(Team.Impostor, _given.TryGetValue(t.PlayerId, out var a) ? a : now.Id, r => r.Id != now.Id);
                else if (UchuKit.TrueCrew(t)) next = UchuKit.RandomRole(Team.Crew, _given.TryGetValue(t.PlayerId, out var b) ? b : now.Id, r => r.Id != now.Id);
                if (next == null) return;
                _given[t.PlayerId] = next.Id;
                // 飾られた本人に、役職が変わった訳を知らせる (新しい役職は役職を配る時の知らせが続けて出す)
                if (t.AmOwner) ShowDecorated();
                else DecoratedCall.Send(t.PlayerId);
                RoleState.Change(t, next);
            },
        });
    }

    private static readonly Net.RemoteCall<byte> DecoratedCall = new("Decorator.Decorated", Net.Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (_, id) => { var lp = PlayerControl.LocalPlayer; if (lp && lp.PlayerId == id) ShowDecorated(); });

    private static void ShowDecorated() => PortKit.Notice(new Text("Decorator.Decorated").ToString());
}

public sealed class NiceDecorator : DecoratorBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#FDBB16";
    public override Text Name => new("NiceDecorator.Name");
    public override string Reading => "ないすでこれーたー";
    public override Text Blurb => new("NiceDecorator.Blurb");
    public override string Origin => "Uchu";
    public override bool CanBeMadmate => true;

    public static readonly FloatOpt Cooldown = new("NiceDecorator.Cooldown", 15f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt Uses = new("NiceDecorator.Uses", 5, 1, 99);

    protected override float DecorateCooldown => Cooldown;
    protected override int DecorateUses => Uses;
}
