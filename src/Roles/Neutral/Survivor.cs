using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 生存者 (第三陣営・相乗り): 誰が勝っても、最後まで生きていれば一緒に勝つ。
// 「ベスト」を着ると決めた時間だけキルを防ぐ (防いだキルは本編の守りが割れる演出になり、キルした人の待ち時間が戻る)。
// 死体を残さずに倒す能力 (吹き飛ばしなど) は防げない
public sealed class Survivor : RoleBase
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => "#FFE64D";
    public override Text Name => new("Survivor.Name");
    public override string Reading => "せいぞんしゃ";
    public override string Origin => "TOU";
    public override Text Blurb => new("Survivor.Blurb");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Survivor.Vest", "survivor_vest") };

    public static readonly FloatOpt VestCooldown = new("Survivor.VestCooldown", 25f, 10f, 60f, 2.5f, "s");
    public static readonly FloatOpt VestDuration = new("Survivor.VestDuration", 10f, 3f, 20f, 1f, "s");
    public static readonly IntOpt MaxVests = new("Survivor.MaxVests", 5, 1, 15);

    // ベストを脱ぐ時刻 (各端末の Time.time・全員の端末で持つ。判定に使うのはホストの値)
    private float _vestUntil;

    public override bool AlsoWins(GameResult result) => Player && !Player.Data.IsDead;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Survivor.Vest"), "survivor_vest")
        {
            Cooldown = () => VestCooldown,
            Duration = () => VestDuration,
            MaxUses = () => MaxVests,
            OnUse = () =>
            {
                VestCall.Send(PlayerId);
                Wear();
                return true;
            },
        });
    }

    private void Wear() => _vestUntil = Time.time + VestDuration;

    // ホストが受け付けたベストの回数と時刻 (待ち時間と回数を守らない送り主の分は捨てる)
    private int _worn;
    private float _wornAt = -999f;

    private void WearFromRemote()
    {
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost)
        {
            if (_worn >= MaxVests || Time.time - _wornAt < VestCooldown * 0.8f) return;
            _worn++;
            _wornAt = Time.time;
        }
        Wear();
    }

    private bool Wearing => Time.time < _vestUntil;

    // 全員の端末で: その人がベストを着ているか (着た知らせは全員に届く)
    internal static bool VestOn(PlayerControl p) => RoleState.Of(p) is Survivor s && s.Wearing && PortKit.Alive(p);

    private static readonly RemoteCall<byte> VestCall = new("Survivor.Vest", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (sender && sender.PlayerId == id && RoleState.Of(id) is Survivor s) s.WearFromRemote(); }, perSecond: 2);

    // 会議で脱ぐ (ボタンの効果も会議で切れる)
    private void OnMeetingStart(MeetingStartEvent e) => _vestUntil = 0f;

    // ホスト: ベストの間はキルを止め、キルした人の待ち時間を戻す
    [HostOnly, OnlyMine]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (!e.Killer || e.Killer.PlayerId == PlayerId || !Wearing || !PortKit.Alive(Player)) return;
        e.Cancel("vest");
        PortKit.HostBlocked(e.Killer, Player);
        SavedCall.Send(PlayerId);
        OnSaved(PlayerId);
    }

    private static readonly RemoteCall<byte> SavedCall = new("Survivor.Saved", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => OnSaved(id));

    // 全員の端末で: 着ている本人にも守りが割れる演出と知らせを出す
    private static void OnSaved(byte id)
    {
        var p = PortKit.ById(id);
        if (!p || !p.AmOwner) return;
        p.ShowFailedMurder();
        PortKit.Notice(new Text("Survivor.VestSaved").ToString());
    }
}
