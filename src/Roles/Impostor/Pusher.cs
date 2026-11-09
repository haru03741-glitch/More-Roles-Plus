// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Pusher.cs, CustomObject/PushedPlayerDeadbody.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 突き落と師 (インポスター・スケルド / ポーラス / エアシップ / ファングル): 船の縁 (決まった場所) に立っているクルーを
// 「突き落とす」と、その人は落ちて死体も残らない。はしごの上にいる人を突き落とすと、はしごの下に死体が残る
public sealed class Pusher : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
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

    private const float StandRadius = 0.2f;
    private const int FurnitureLayer = 1 << 12; // 机・緊急ボタンの台 (本編の ShipAndObjectsMask に入っていない)

    private static bool CanStand(Vector2 at)
    {
        foreach (var c in Physics2D.OverlapCircleAll(at, StandRadius, Constants.ShipAndObjectsMask | FurnitureLayer))
            if (c && !c.isTrigger) return false;
        return true;
    }

    private static byte MapId => GameManager.Instance ? GameManager.Instance.LogicOptions.MapId : (byte)255;

    // 船を左右反転している時は、場所を鏡に映して左右の向きを入れ替える (突き落とす時だけ呼ぶ)
    private static (Vector2 push, Dir dir) Spot(int i)
    {
        var s = Spots[MapId][i];
        if (!MapFlip.Flipped) return (s.push, s.dir);
        Dir dir = s.dir == Dir.Right ? Dir.Left : s.dir == Dir.Left ? Dir.Right : s.dir;
        return (new Vector2(MapFlip.Mirror(s.push.x), s.push.y), dir);
    }

    // ボタンの狙いの判定で毎フレーム呼ばれるので、Unity の算術を使わずに比べる
    private static int SpotOf(PlayerControl p)
    {
        if (!p || !Spots.TryGetValue(MapId, out var list)) return -1;
        var pos = p.transform.position;
        float px = pos.x, py = pos.y;
        bool flip = MapFlip.Flipped;
        for (int i = 0; i < list.Length; i++)
        {
            var s = list[i];
            float dx = (flip ? MapFlip.Mirror(s.at.x) : s.at.x) - px, dy = s.at.y - py;
            if (dx * dx + dy * dy <= s.r * s.r) return i;
        }
        return -1;
    }

    private static Ladder LadderOf(PlayerControl p)
    {
        int i = LadderIndex(p);
        return i >= 0 ? ShipStatus.Instance.Ladders[i] : null;
    }

    private static int LadderIndex(PlayerControl p)
    {
        var ship = ShipStatus.Instance;
        if (!p || !ship || ship.Ladders == null) return -1;
        var ls = ship.Ladders;
        for (int i = 0; i < ls.Length; i++)
            if (ls[i] && ls[i].IsTop && Vector2.Distance(ls[i].transform.position, p.transform.position) <= ls[i].UsableDistance) return i;
        return -1;
    }

    // 全員の端末で (ホストが狙いを確かめた後): ホストが守りを確かめてから結果を全員へ配る
    private static void Push(PlayerControl user, PlayerControl target)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        int spot = SpotOf(target);
        int ladder = spot < 0 ? LadderIndex(target) : -1;
        if (spot < 0 && ladder < 0) return;
        // 守り (守護者・ベスト・盾など) で止まった時は落とさない。はしごの上なら下へ降ろすだけ
        bool blocked = PortKit.Alive(target) && BeforeMurderEvent.Ask(user, target) != null;
        var m = (user.PlayerId, target.PlayerId, (sbyte)spot, (sbyte)ladder, blocked);
        ResultCall.Send(m);
        OnResult(m);
    }

    private static readonly Net.RemoteCall<(byte user, byte target, sbyte spot, sbyte ladder, bool blocked)> ResultCall = new("Pusher.Result", Net.Route.HostToAll,
        (w, m) => { w.Write(m.user); w.Write(m.target); w.Write((byte)m.spot); w.Write((byte)m.ladder); w.Write(m.blocked); },
        r => (r.ReadByte(), r.ReadByte(), (sbyte)r.ReadByte(), (sbyte)r.ReadByte(), r.ReadBoolean()),
        (_, m) => OnResult(m));

    private static void OnResult((byte user, byte target, sbyte spot, sbyte ladder, bool blocked) m)
    {
        var user = PortKit.ById(m.user);
        var target = PortKit.ById(m.target);
        if (!user || !target) return;
        var ship = ShipStatus.Instance;
        Ladder ladder = m.ladder >= 0 && ship && ship.Ladders != null && m.ladder < ship.Ladders.Length ? ship.Ladders[m.ladder] : null;
        if (m.spot < 0 && !ladder) return;
        if (user.AmOwner) user.SetKillTimer(GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown));
        Vector2? bottom = ladder ? (Vector2)ladder.Destination.transform.position + new Vector2(0.15f, 0.2f) : null;
        if (m.blocked)
        {
            if (ladder && target.AmOwner) Net.Teleport.Self(bottom.Value);
            return;
        }
        Vector2 at = ladder ? (Vector2)ladder.transform.position + new Vector2(0f, 0.15f) : Spot(m.spot).push;
        Dir dir = ladder ? Dir.Down : Spot(m.spot).dir;
        // 突き落とす時に立つ所が壁や家具に掛かっていたら移らない (その場から押す)
        if (user.AmOwner && CanStand(at)) Net.Teleport.Self(at);
        Fall(target, at, dir, bottom);
        if (!AmongUsClient.Instance.AmHost || !PortKit.Alive(user) || !PortKit.Alive(target)) return;
        if (ladder)
        {
            user.RpcMurderPlayer(target, true); // 守りは Push で確かめ済み
            BodyCall.Send((target.PlayerId, bottom.Value));
            MoveBody(target.PlayerId, bottom.Value);
        }
        else PortKit.HostVanish(user, target, PortKit.Harm.Fated);
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
