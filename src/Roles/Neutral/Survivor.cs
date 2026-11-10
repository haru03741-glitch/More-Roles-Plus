// Ported from https://github.com/AU-Avengers/TOU-Mira TownOfUs/Roles/Classic/Neutral/NeutralBenign/SurvivorRole.cs, Options/Roles/Neutral/SurvivorOptions.cs, Events/Neutral/SurvivorEvents.cs, Modifiers/ScatterModifier.cs (GPL-3.0)
using System.Collections.Generic;
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

    public static readonly FloatOpt VestCooldown = new("Survivor.VestCooldown", 25f, 5f, 120f, 2.5f, "s");
    public static readonly FloatOpt VestDuration = new("Survivor.VestDuration", 10f, 3f, 20f, 1f, "s");
    public static readonly IntOpt MaxVests = new("Survivor.MaxVests", 10, 1, 15);
    // Scatter: 同じ場所 (直近 3 か所のどれかから半径 5 以内) にとどまり続けると、時間切れで死ぬ
    public static readonly BoolOpt ScatterOn = new("Survivor.Scatter", false);
    public static readonly FloatOpt ScatterTime = new("Survivor.ScatterTimer", 25f, 5f, 120f, 2.5f, "s") { When = () => ScatterOn };

    // ベストを脱ぐ時刻 (各端末の Time.time・全員の端末で持つ。判定に使うのはホストの値)
    private float _vestUntil;

    public override bool AlsoWins(GameResult result) => Player && !Player.Data.IsDead;

    // ---- 原作の WinConditionMet: 生存者が生きていて、キル役がいなくて、生き残りが 3 人以下で、
    // クルーとキルしない善い第三陣営だけではない (= 勝ちに絡まない第三陣営が残っている) なら、試合を終わらせる ----
    // 数の勝ち (GameEnd.Decide) がインポスターの居ない試合で決められない所から呼ばれる。決まらなければ null
    internal static GameResult EndResult()
    {
        var gd = GameData.Instance;
        if (!gd) return null;
        var all = gd.AllPlayers;
        int alive = 0;
        bool other = false;
        Survivor first = null;
        var ids = new List<byte>();
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.Disconnected || p.IsDead) continue;
            alive++;
            var role = RoleState.Of(p.PlayerId);
            if (role is Survivor s) { first ??= s; ids.Add(p.PlayerId); continue; }
            if (role == null) continue;
            if (role.CanKill || role.Team == Team.Madmate || role.Madden) return null;
            if (role.Team == Team.Neutral) other = true;
        }
        if (first == null || !other || alive > 3) return null;
        var r = new GameResult { Role = first };
        foreach (byte id in ids) r.Winners.Add(id);
        return r;
    }

    // ---- Scatter (自分の端末で数えて、時間切れなら自分を倒すようホストへ頼む) ----
    private float _scatterLeft = -1f;
    private readonly Vector2[] _spots = new Vector2[3];
    private int _spotCount;
    private float _suicideAt = -999f;
    private HudReadout _readout;
    private int _shownSec = -1;
    private string _shownText;

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (!ScatterOn) { _readout?.Set(false); return; }
        var hud = Vanilla.Hud;
        bool play = PortKit.Alive(Player) && ShipStatus.Instance && !MeetingHud.Instance && !ExileController.Instance
            && !(hud && hud.IsIntroDisplayed);
        float total = ScatterTime;
        if (!play)
        {
            // 会議・導入・死亡の間は数えず、始まり直しは満タンから
            _scatterLeft = total;
            _readout?.Set(false);
            return;
        }
        if (_scatterLeft < 0f || _scatterLeft > total) _scatterLeft = total;
        _scatterLeft -= Time.fixedDeltaTime;
        Vector2 at = Player.GetTruePosition();
        bool near = false;
        for (int i = 0; i < _spotCount; i++)
        {
            float dx = _spots[i].x - at.x, dy = _spots[i].y - at.y;
            if (dx * dx + dy * dy < 25f) { near = true; break; }
        }
        if (!near)
        {
            _scatterLeft = total;
            for (int i = System.Math.Min(_spotCount, _spots.Length - 1); i > 0; i--) _spots[i] = _spots[i - 1];
            _spots[0] = at;
            if (_spotCount < _spots.Length) _spotCount++;
        }
        _readout ??= new HudReadout(Lifespan, UI.RoleArt.Own(Id), new Color32(255, 230, 77, 255));
        float left = System.Math.Max(_scatterLeft, 0f);
        int sec = (int)System.Math.Ceiling(left);
        if (sec != _shownSec)
        {
            _shownSec = sec;
            string col = left > 10f ? "#55FF55" : left > 5f ? "#FFFF55" : "#FF5555";
            _shownText = $"<color={col}>{sec}s</color>";
        }
        _readout.Set(true, _shownText, left / total);
        if (_scatterLeft <= 0f && Time.time - _suicideAt > 2f)
        {
            _suicideAt = Time.time;
            _scatterLeft = total;
            PortKit.Suicide();
        }
    }

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
