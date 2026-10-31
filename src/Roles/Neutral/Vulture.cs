// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Vulture.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// ヴァルチャー (第三陣営): 死体の近くで「喰らう」と死体が消える。決めた数を食べるとその場で単独勝ち。死体への矢印が見える
public sealed class Vulture : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#8B4513";
    public override Text Name => new("Vulture.Name");
    public override string Reading => "ゔぁるちゃー";
    public override Text Blurb => new("Vulture.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Vulture.Eat", "snr_eat") };

    public static readonly FloatOpt EatCooldown = new("Vulture.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt RequiredBodies = new("Vulture.RequiredBodies", 3, 1, 10);
    public static readonly BoolOpt Vent = new("Vulture.CanVent", false);
    public static readonly BoolOpt ShowArrows = new("Vulture.ShowArrows", true);

    private int _eaten;
    private BodyArrows _arrows;

    public override bool? CanVent => Vent ? true : null;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Vulture.Eat"), "snr_eat")
        {
            Cooldown = () => EatCooldown,
            OnUse = () =>
            {
                var body = NearestBody(PlayerControl.LocalPlayer.GetTruePosition());
                if (!body) return false;
                EatCall.Send(body.ParentId);
                OnEat(this, body.ParentId);
                return true;
            },
        });
    }

    // 死体への矢印
    [LocalOnly]
    public override void OnFixedUpdate() =>
        (_arrows ??= new BodyArrows(Lifespan, new Color32(139, 69, 19, 255))).Update(ShowArrows && PortKit.Alive(Player));

    // 通報できる距離で、間に壁の無い一番近い死体
    private static DeadBody NearestBody(Vector2 from)
    {
        DeadBody best = null;
        float bestD = PlayerControl.LocalPlayer.MaxReportDistance;
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
        {
            if (!b || b.Reported || !b.gameObject.activeInHierarchy) continue;
            float d = Vector2.Distance(from, b.TruePosition);
            if (d > bestD || PhysicsHelpers.AnythingBetween(from, b.TruePosition, Constants.ShipAndObjectsMask, false)) continue;
            best = b;
            bestD = d;
        }
        return best;
    }

    private static readonly RemoteCall<byte> EatCall = new("Vulture.Eat", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) =>
        {
            if (RoleState.Of(sender) is Vulture v) OnEat(v, id);
        });

    // 全員の端末で: 死体を消して食べた数を数える。ホストが勝ちを決める
    private static void OnEat(Vulture v, byte bodyId)
    {
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
            if (b && b.ParentId == bodyId) Object.Destroy(b.gameObject);
        v._eaten++;
        if (v.IsLocal) StatusEffects.ShowUntil("Vulture", new Text("Vulture.Count").With("n", v._eaten).With("max", RequiredBodies.Value), v.Color, Text.Raw(""));
        if (v._eaten >= RequiredBodies && AmongUsClient.Instance.AmHost) GameEnd.Win(v);
    }
}
