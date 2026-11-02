// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Arsonist.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// アーソニスト (第三陣営): 「塗る」で近くの人に少しの間そばにいるとオイルを塗れる (離れると失敗)。生きている全員に塗ってから「燃やす」と単独勝ち
public sealed class Arsonist : RoleBase
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => "#FF6600";
    public override Text Name => new("Arsonist.Name");
    public override string Reading => "あーそにすと";
    public override Text Blurb => new("Arsonist.Blurb");
    public override string Origin => "TOU";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Arsonist.Douse", "snr_oil"), new("Arsonist.Ignite", "snr_fire") };

    public static readonly FloatOpt DouseCooldown = new("Arsonist.Cooldown", 10f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt DouseDuration = new("Arsonist.Duration", 1f, 0.5f, 10f, 0.5f, "s");
    public static readonly BoolOpt Vent = new("Arsonist.CanVent", true);
    public static readonly BoolOpt ImpostorSight = new("Arsonist.ImpostorVision", true);

    private readonly HashSet<byte> _doused = new();
    private byte _current = 255;   // 塗っている途中の人 (自分の端末だけ)
    private Ability _douse;

    public override bool? CanVent => Vent;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    public override void OnAssigned()
    {
        _douse = AddAbility(new Ability(new Text("Arsonist.Douse"), "snr_oil")
        {
            Cooldown = () => DouseCooldown,
            Duration = () => DouseDuration,
            // 塗っている間はその人、待っている間は押せば塗る人
            ShowAim = () => _douse.InEffect ? PortKit.ById(_current) : NearestTarget(),
            OnUse = () =>
            {
                var t = NearestTarget();
                if (!t) return false;
                _current = t.PlayerId;
                return true;
            },
            OnEnd = () =>
            {
                byte id = _current;
                _current = 255;
                var t = PortKit.ById(id);
                if (!PortKit.Alive(t) || _doused.Contains(id)) return;
                DouseCall.Send(id);
                _doused.Add(id);
            },
        });
        AddAbility(new Ability(new Text("Arsonist.Ignite"), "snr_fire")
        {
            OnUse = () =>
            {
                if (!AllDoused()) return false;
                IgniteCall.Send(0);
                return true;
            },
        });
    }

    // キルの届く所で、まだ塗っていない一番近い人
    private PlayerControl NearestTarget() =>
        Targeting.ClosestPlayer(Player, GameManager.Instance.LogicOptions.GetKillDistance(), _notDoused ??= (_, p) => !_doused.Contains(p.PlayerId));

    private System.Func<PlayerControl, PlayerControl, bool> _notDoused;

    // 塗っている途中に相手が死ぬか、一番近い人が変わったら失敗
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (_current == 255 || _douse == null || !_douse.InEffect) return;
        var t = PortKit.ById(_current);
        if (PortKit.Alive(t) && NearestTarget() == t) return;
        _current = 255;
        _douse.EndEffect();
    }

    private static readonly RemoteCall<byte> DouseCall = new("Arsonist.Douse", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Arsonist a) a._doused.Add(id); });

    // 自分以外の生きている全員に塗ったか
    private bool AllDoused()
    {
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p.PlayerId != PlayerId && PortKit.Alive(p) && !_doused.Contains(p.PlayerId)) return false;
        return true;
    }

    private static readonly RemoteCall<byte> IgniteCall = new("Arsonist.Ignite", Route.ToHost,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) =>
        {
            if (RoleState.Of(sender) is not Arsonist a || a._ignited || !PortKit.Alive(sender) || !a.AllDoused()) return;
            a._ignited = true;
            // 塗った人の足元のオイルに火が付く (床の火の計算に載せるので全員の画面で燃え、床の材質と水で燃え方が変わる)。
            // 燃え上がるのを見せてから勝ちを決める
            foreach (var p in PlayerControl.AllPlayerControls)
            {
                if (!PortKit.Alive(p) || !a._doused.Contains(p.PlayerId)) continue;
                Vector2 at = p.GetTruePosition();
                Terrain.TerrainApi.WorldSpill(at, IgniteRadius, 1f);
                Terrain.TerrainApi.WorldIgnite(at, IgniteRadius, 1f);
            }
            PortKit.After(IgniteDelay, () => { if (PortKit.Alive(sender) && RoleState.Of(sender) == a) GameEnd.Win(a); });
        });

    private const float IgniteRadius = 0.7f, IgniteDelay = 2.5f;
    private bool _ignited; // ホストだけ: 火を付けてから勝ちが決まるまでの間にもう一度押されても数えない

    public override string NameMark(PlayerControl seen) =>
        _doused.Contains(seen.PlayerId) ? $"<color={Color}>§</color>" : null;
}
