// Ported from https://github.com/Tomi-1103/TownOfHost-hamo Roles/Neutral/Null.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// ヌル (第三陣営): 2 段の条件を満たして生き残ると勝つ。
// フェーズ 1 = 誰かのそばにいた時間が累計で決めた秒に届く → フェーズ 2 = 誰もそばにいない時間が累計で決めた秒に届く → 条件達成。
// 条件を達成したまま試合の終わりに生きていれば勝ち (単独で勝ちを奪う / 勝った陣営に相乗り)。
// 時間を数えるのはホストだけで、1 秒ごととフェーズが変わった時に全員へ配る。ボタンの満ち方と残りの秒で進みが見える
public sealed class Null : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#FFFFFF";
    public override Text Name => new("Null.Name");
    public override string Reading => "ぬる";
    public override Text Blurb => new("Null.Blurb");
    public override string Origin => "TOH-hamo";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Null.Phase1", "null_phase"), new("Null.Phase2", "null_phase") };

    public static readonly BoolOpt SoloWin = new("Null.SoloWin", true);
    public static readonly FloatOpt TogetherRange = new("Null.TogetherRange", 1f, 0.25f, 5f, 0.25f, "x");
    public static readonly FloatOpt AloneRange = new("Null.AloneRange", 2f, 0.25f, 5f, 0.25f, "x");
    public static readonly FloatOpt TogetherTime = new("Null.TogetherTime", 100f, 5f, 300f, 5f, "s");
    public static readonly FloatOpt AloneTime = new("Null.AloneTime", 100f, 5f, 300f, 5f, "s");

    // 1 = 誰かと一緒・2 = 一人きり・3 = 条件達成
    private int _phase = 1;
    private float _together, _alone;
    private int _sentSec = -1;
    private bool _advancing;
    private Ability _gauge;

    public override void OnAssigned()
    {
        _gauge = AddAbility(new Ability(new Text("Null.Phase1"), "null_phase")
        {
            CooldownAtStart = false,
            OnUse = () => false,
        });
    }

    public override bool AlsoWins(GameResult result) => !SoloWin && Done;
    public override bool StealsWin(GameResult result) => SoloWin && Done;

    private bool Done => _phase == 3 && PortKit.Alive(Player);

    public override void OnFixedUpdate()
    {
        if (IsLocal) Draw();
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        if (_phase == 3 || !PortKit.Alive(Player) || MeetingHud.Instance || ExileController.Instance || !ShipStatus.Instance) return;
        bool near = SomeoneWithin(_phase == 1 ? TogetherRange : AloneRange);
        bool changed = false;
        float dt = Time.fixedDeltaTime;
        if (_phase == 1)
        {
            _advancing = near;
            if (near) _together += dt;
            if (_together >= TogetherTime) { _phase = 2; changed = true; }
        }
        else
        {
            _advancing = !near;
            if (!near) _alone += dt;
            if (_alone >= AloneTime) { _phase = 3; changed = true; }
        }
        int sec = (int)(_phase == 1 ? _together : _alone) + _phase * 1000 + (_advancing ? 0 : 500);
        if (!changed && sec == _sentSec) return;
        _sentSec = sec;
        Sync.Send((PlayerId, (byte)_phase, _together, _alone, _advancing));
        Receive(this, _phase, _together, _alone, _advancing, changed);
    }

    private bool SomeoneWithin(float range)
    {
        Vector2 me = Player.GetTruePosition();
        float sq = range * range;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.PlayerId == PlayerId) continue;
            Vector2 at = p.GetTruePosition();
            float dx = at.x - me.x, dy = at.y - me.y;
            if (dx * dx + dy * dy <= sq) return true;
        }
        return false;
    }

    private static readonly RemoteCall<(byte id, byte phase, float together, float alone, bool advancing)> Sync = new("Null.Sync", Route.HostToAll,
        (w, m) => { w.Write(m.id); w.Write(m.phase); w.Write(m.together); w.Write(m.alone); w.Write(m.advancing); },
        r => (r.ReadByte(), r.ReadByte(), r.ReadSingle(), r.ReadSingle(), r.ReadBoolean()),
        (_, m) =>
        {
            if (RoleState.Of(m.id) is Null n) Receive(n, m.phase, m.together, m.alone, m.advancing, false);
        });

    // 全員の端末で: ホストの数えた値を写す。本人の端末ではフェーズが進んだ瞬間に知らせる
    private static void Receive(Null n, int phase, float together, float alone, bool advancing, bool hostChanged)
    {
        bool up = phase > n._phase || hostChanged;
        n._phase = phase;
        n._together = together;
        n._alone = alone;
        n._advancing = advancing;
        if (!up || !n.IsLocal) return;
        PortKit.Flash(RoleDisplay.ParseColor("#FFFFFF"), 0.8f);
        var hud = Vanilla.Hud;
        if (SoundManager.Instance && hud && hud.TaskCompleteSound) SoundManager.Instance.PlaySound(hud.TaskCompleteSound, false, 0.8f);
        PortKit.Notice(new Text(phase == 3 ? "Null.Achieved" : "Null.Phase2Started").ToString());
        if (phase == 3) { StatusEffects.Hide("Null"); n._shown = -1; }
    }

    // 自分の端末: ボタンの名前をフェーズに、満ち方を残りに合わせる
    private void Draw()
    {
        var b = _gauge?.Button;
        if (b == null) return;
        b.SetUsable(false);
        if (_phase == 3)
        {
            b.SetLabel(new Text("Null.Achieved.Label").ToString());
            b.ShowGauge(0f, 1f, null);
            return;
        }
        float need = _phase == 1 ? TogetherTime : AloneTime;
        float done = _phase == 1 ? _together : _alone;
        b.SetLabel(new Text(_phase == 1 ? "Null.Phase1" : "Null.Phase2").ToString());
        b.ShowGauge(need - done, need, Mathf.CeilToInt(need - done).ToString());
        int shown = _phase * 100000 + (int)done * 2 + (_advancing ? 1 : 0);
        if (shown == _shown) return;
        _shown = shown;
        StatusEffects.ShowUntil("Null", new Text(_phase == 1 ? "Null.Status1" : "Null.Status2")
            .With("n", (int)done).With("max", (int)need), _advancing ? "#FFFFFF" : "#9A9A9A", Text.Raw(_advancing ? "▶" : "■"));
    }

    private int _shown = -1;
}
