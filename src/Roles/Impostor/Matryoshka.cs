// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Matryoshka.cs, Roles/Ability/MatryoshkaAbility.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// マトリョーシカ (インポスター): 「着る」で近くの死体を着てその人の姿になる (死体は自分に付いて回る)。時間が来るか、もう一度押すと
// その場に脱ぎ捨てる。着るたびにキルの待ち時間が延びる。設定で、着ている間も死体を通報できる
public sealed class Matryoshka : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Matryoshka.Name");
    public override string Reading => "まとりょーしか";
    public override Text Blurb => new("Matryoshka.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Matryoshka.Wear", "snr_doll") };
    public static readonly FloatOpt WearCooldown = new("Matryoshka.Cooldown", 30f, 0f, 180f, 2.5f, "s");
    public static readonly IntOpt WearLimit = new("Matryoshka.WearLimit", 3, 1, 15);
    public static readonly BoolOpt WearReport = new("Matryoshka.WearReport", false);
    public static readonly FloatOpt WearDuration = new("Matryoshka.Duration", 7.5f, 0.5f, 60f, 0.5f, "s");
    public static readonly FloatOpt AddKillCooldown = new("Matryoshka.AddKillCooldown", 2.5f, 0f, 30f, 0.5f, "s");

    // 着ている人 → 隠した死体 (全員の端末で持つ)
    private static readonly Dictionary<byte, DeadBody> Worn = new();
    private int _wears;
    private Ability _wear;

    public override void ModifyKillCooldown(ref float seconds) => seconds += AddKillCooldown * _wears;

    public override void OnAssigned()
    {
        _wear = AddAbility(new Ability(new Text("Matryoshka.Wear"), "snr_doll")
        {
            Cooldown = () => WearCooldown,
            Duration = () => WearDuration,
            MaxUses = () => WearLimit,
            Cancellable = true,
            OnUse = () =>
            {
                var body = NearestFree(Player.transform.position);
                if (!body) return false;
                WearCall.Send(body.ParentId);
                Wear(Player, body.ParentId);
                PortKit.Morph(PortKit.ById(body.ParentId));
                return true;
            },
            OnEnd = TakeOff,
            OnCancel = TakeOff,
        });
        Lifespan.OnRelease(TakeOff);
    }

    // 2 以内で、誰も使っていない一番近い死体
    private static DeadBody NearestFree(Vector2 from)
    {
        DeadBody best = null;
        float bestD = 2f;
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
        {
            if (!b || !b.gameObject.activeInHierarchy || InUse(b)) continue;
            float d = Vector2.Distance(from, b.transform.position);
            if (d > bestD) continue;
            best = b;
            bestD = d;
        }
        return best;
    }

    private static bool InUse(DeadBody b)
    {
        foreach (var w in Worn.Values) if (w == b) return true;
        return false;
    }

    private void TakeOff()
    {
        if (!Worn.ContainsKey(PlayerId)) return;
        WearCall.Send(255);
        Wear(Player, 255);
        PortKit.Morph(null);
        if (_wear != null && _wear.InEffect) _wear.EndEffect();
    }

    [LocalOnly]
    private void OnMeetingCalled(MeetingCalledEvent e) => TakeOff();

    private void OnMeeting(MeetingStartEvent e) => Worn.Remove(PlayerId);

    public override void OnFixedUpdate()
    {
        // 着ている死体は自分に付いて回る
        if (Worn.TryGetValue(PlayerId, out var b) && b) b.transform.position = Player.transform.position;
        if (IsLocal && Worn.ContainsKey(PlayerId) && !PortKit.Alive(Player)) TakeOff();
    }

    private static readonly RemoteCall<byte> WearCall = new("Matryoshka.Wear", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Matryoshka) Wear(sender, id); });

    // 全員の端末で: 死体を隠す (255 = 今の位置に脱ぎ捨てる)
    private static void Wear(PlayerControl user, byte bodyId)
    {
        if (!user) return;
        if (bodyId == 255)
        {
            if (Worn.Remove(user.PlayerId, out var b) && b)
            {
                b.transform.position = new Vector3(user.transform.position.x, user.transform.position.y, b.transform.position.z);
                b.Reported = false;
                SetVisible(b, true);
                if (b.myCollider) b.myCollider.enabled = true;
            }
            return;
        }
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
        {
            if (!b || b.ParentId != bodyId || InUse(b)) continue;
            b.Reported = !WearReport;
            SetVisible(b, false);
            if (b.myCollider) b.myCollider.enabled = WearReport;
            Worn[user.PlayerId] = b;
            if (RoleState.Of(user) is Matryoshka m) m._wears++;
            return;
        }
    }

    private static void SetVisible(DeadBody b, bool on)
    {
        if (b.bodyRenderers == null) return;
        foreach (var r in b.bodyRenderers) if (r) r.enabled = on;
    }
}
