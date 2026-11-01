// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Pusher.cs, CustomObject/PushedPlayerDeadbody.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 突き落と師 (インポスター・スケルド / ポーラス / エアシップ / ファングル): 船の縁 (決まった場所) に立っているクルーを
// 「突き落とす」と、その人は落ちて死体も残らない。はしごの上にいる人を突き落とすと、はしごの下に死体が残る
public sealed class Pusher : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Pusher.Name");
    public override string Reading => "つきおとし";
    public override Text Blurb => new("Pusher.Blurb");
    public override string Origin => "SNR";
    public override byte[] OnlyMaps => new byte[] { 0, 2, 4, 5 };
    public override HelpSection[] HelpSections => new HelpSection[] { new("Pusher.Push", "snr_push") };

    public static readonly FloatOpt PushCooldown = new("Pusher.Cooldown", 15f, 0f, 180f, 2.5f, "s");

    private enum Dir { Right, Left, Down, Up }

    // 突き落とせる場所 (中心・広さ・突き落とす時に立つ所・落ちる向き)
    private static readonly Dictionary<byte, (Vector2 at, float r, Vector2 push, Dir dir)[]> Spots = new()
    {
        { 4, new[] {
            (new Vector2(5.8f, 8.95f), 1f, new Vector2(5.8132f, 8.8f), Dir.Right),
            (new Vector2(10f, 9f), 1f, new Vector2(9.7064f, 8.8f), Dir.Left),
            (new Vector2(26.85f, 0.5f), 1f, new Vector2(26.6653f, 0.45f), Dir.Right),
            (new Vector2(28.15f, -1.5f), 1f, new Vector2(28.1792f, -1.65f), Dir.Left),
            (new Vector2(-13.84f, -16.3494f), 1.15f, new Vector2(-13.89f, -16.5f), Dir.Down),
            (new Vector2(7.3f, -15.6f), 2.2f, new Vector2(7.05f, -14.672f), Dir.Down),
            (new Vector2(10.3f, -16.7f), 1f, new Vector2(10.27f, -16.3995f), Dir.Down),
            (new Vector2(10.2f, -15.1f), 1f, new Vector2(10.59f, -15.1f), Dir.Right) } },
        { 0, new[] {
            (new Vector2(11.93f, -12.14f), 1.75f, new Vector2(10.3f, -12.24f), Dir.Right),
            (new Vector2(7.7f, -10.8f), 1f, new Vector2(8.7f, -11.21f), Dir.Left),
            (new Vector2(7.8f, -13.4f), 0.75f, new Vector2(8.3f, -13.05f), Dir.Left),
            (new Vector2(0.2f, -17.1f), 0.55f, new Vector2(0.2f, -17.1f), Dir.Down),
            (new Vector2(10.9f, 0.7f), 1.25f, new Vector2(9.98f, 0.95f), Dir.Right),
            (new Vector2(7.5f, 2.78f), 0.9f, new Vector2(8.2f, 2.4f), Dir.Left),
            (new Vector2(8.08f, -0.17f), 0.85f, new Vector2(8.2f, 0.78f), Dir.Down) } },
        { 2, new[] {
            (new Vector2(24.2f, -3f), 0.8f, new Vector2(24.2f, -3f), Dir.Left),
            (new Vector2(4.79f, -4.07f), 0.8f, new Vector2(4.79f, -4.07f), Dir.Right),
            (new Vector2(33.97f, -4.85f), 1.5f, new Vector2(34.5f, -5.18f), Dir.Up),
            (new Vector2(32.33f, -14.32f), 1.4f, new Vector2(31.5f, -13.9f), Dir.Right),
            (new Vector2(32.11f, -15.75f), 0.75f, new Vector2(32.68f, -15.728f), Dir.Right),
            (new Vector2(31.55f, -16.9f), 0.75f, new Vector2(31.17f, -16.95f), Dir.Down) } },
        { 5, new[] {
            (new Vector2(-23.81f, -7.9f), 1.2f, new Vector2(-23.26f, -7.12f), Dir.Left),
            (new Vector2(16.5f, 13.49f), 1.3f, new Vector2(16.8f, 13.6f), Dir.Left),
            (new Vector2(6.8f, -0.14f), 0.75f, new Vector2(6.8f, -0.14f), Dir.Left),
            (new Vector2(5.85f, 1.19f), 0.75f, new Vector2(5.85f, 1.19f), Dir.Left) } },
    };

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Pusher.Push"), "snr_push")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => PushCooldown,
            CanTarget = (_, t) => !PortKit.IsImpostor(t) && (SpotOf(t) >= 0 || LadderOf(t)),
            OnUsed = (user, target) => Push(user, target),
        });
    }

    private static byte MapId => GameManager.Instance ? GameManager.Instance.LogicOptions.MapId : (byte)255;

    private static int SpotOf(PlayerControl p)
    {
        if (!p || !Spots.TryGetValue(MapId, out var list)) return -1;
        for (int i = 0; i < list.Length; i++)
            if (Vector2.Distance(list[i].at, p.transform.position) <= list[i].r) return i;
        return -1;
    }

    private static Ladder LadderOf(PlayerControl p)
    {
        var ship = ShipStatus.Instance;
        if (!p || !ship || ship.Ladders == null) return null;
        foreach (var l in ship.Ladders)
            if (l && l.IsTop && Vector2.Distance(l.transform.position, p.transform.position) <= l.UsableDistance) return l;
        return null;
    }

    // 全員の端末で: 突き落とす。死体の扱いはホストが決める
    private static void Push(PlayerControl user, PlayerControl target)
    {
        int spot = SpotOf(target);
        var ladder = spot < 0 ? LadderOf(target) : null;
        if (spot < 0 && !ladder) return;
        Vector2 at = ladder ? (Vector2)ladder.transform.position + new Vector2(0f, 0.15f) : Spots[MapId][spot].push;
        Dir dir = ladder ? Dir.Down : Spots[MapId][spot].dir;
        if (user.AmOwner)
        {
            Net.Teleport.Self(at);
            user.SetKillTimer(GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown));
        }
        Fall(target, at, dir, ladder ? (Vector2?)((Vector2)ladder.Destination.transform.position + new Vector2(0.15f, 0.2f)) : null);
        if (!AmongUsClient.Instance.AmHost) return;
        if (ladder)
        {
            byte id = target.PlayerId;
            Vector2 bottom = (Vector2)ladder.Destination.transform.position + new Vector2(0.15f, 0.2f);
            PortKit.HostKill(user, target);
            BodyCall.Send((id, bottom));
            MoveBody(id, bottom);
        }
        else PortKit.HostVanish(user, target);
    }

    private static readonly Net.RemoteCall<(byte id, Vector2 at)> BodyCall = new("Pusher.Body", Net.Route.HostToAll,
        (w, m) => { w.Write(m.id); w.Write(m.at.x); w.Write(m.at.y); }, r => (r.ReadByte(), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (_, m) => MoveBody(m.id, m.at));

    // 死体ができるのを少し待ってから、はしごの下へ置く
    private static void MoveBody(byte id, Vector2 at)
    {
        PortKit.After(0.3f, () =>
        {
            foreach (var b in Object.FindObjectsOfType<DeadBody>())
                if (b && b.ParentId == id) b.transform.position = new Vector3(at.x, at.y, at.y / 1000f);
        });
    }

    private static void Fall(PlayerControl target, Vector2 from, Dir dir, Vector2? to)
    {
        float dx = dir == Dir.Right ? 1f : dir == Dir.Left ? -1f : 0f, dy = dir == Dir.Up ? 1f : dir == Dir.Down ? -1f : 0f;
        PushFall.Start(target, dx, dy, to);
    }
}
