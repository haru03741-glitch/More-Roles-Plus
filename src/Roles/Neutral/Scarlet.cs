// Ported from https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Roles/Neutral/Scarlet.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// スカーレット (第三陣営): 「キープ」で近くの人を自分の恋人 (キープ) に、「本命」で 1 人だけ本命にする。
// 本命を決めるまでの猶予 (砂時計) が切れると死ぬ (会議中と会議の後 5 秒は止まる)。キープの役職が見える。
// 本命が勝った時に本命と自分が生きていれば (本命と一緒にいて溜まるゲージが足りていれば) 勝ちを奪って 2 人で勝つ。
// 会議の「号令」で、キープと本命の票を自分の投票先へ向けさせる。本命と自分は片方が死ぬともう片方も死ぬ。
// 恋人からはスカーレットを撃てない (ゲッサー)
public sealed class Scarlet : RoleBase, IGuessBlock
{
    public const string Hex = "#8A1A31";

    public override Team BaseTeam => Team.Neutral;
    public override string Color => Hex;
    public override Text Name => new("Scarlet.Name");
    public override string Reading => "すかーれっと";
    public override Text Blurb => new("Scarlet.Blurb");
    public override string Origin => "Nebula";
    public override HelpSection[] HelpSections => new HelpSection[]
    {
        new("Scarlet.Keep", "scarlet_keep"), new("Scarlet.Favorite", "scarlet_favorite"),
        new("Scarlet.Hourglass", "scarlet_hourglass"), new("Scarlet.Command", "scarlet_command"),
    };

    public static readonly FloatOpt Grace = new("Scarlet.Grace", 120f, 30f, 600f, 10f, "s");
    public static readonly IntOpt NumOfKept = new("Scarlet.NumOfKept", 3, 1, 10);
    public static readonly IntOpt NumOfCommand = new("Scarlet.NumOfCommand", 2, 1, 10);
    public static readonly BoolOpt CanOverrideTaskWin = new("Scarlet.CanOverrideTaskWin", false);
    public static readonly BoolOpt WithGauge = new("Scarlet.WithGauge", true);
    public static readonly FloatOpt RequiredGauge = new("Scarlet.RequiredGauge", 20f, 10f, 150f, 5f, "s") { When = () => WithGauge };
    public static readonly FloatOpt SurplusGauge = new("Scarlet.SurplusGauge", 20f, 5f, 80f, 5f, "s") { When = () => WithGauge };
    public static readonly FloatOpt GaugeReduction = new("Scarlet.GaugeReduction", 0.25f, 0f, 2f, 0.125f, "x") { When = () => WithGauge };
    public static readonly BoolOpt CanLocateLovers = new("Scarlet.CanLocateLovers", false);
    public static readonly BoolOpt PreAssignedLovers = new("Scarlet.PreAssignedLovers", false);
    public static readonly BoolOpt Vent = new("Scarlet.Vent", true);

    public override bool? CanVent => Vent;

    private const float NearDistance = 2f;

    // 恋人 (全員の端末で同じ)。値 = 本命か
    private readonly Dictionary<byte, bool> _lovers = new();
    private byte _favorite = 255;
    private float _gauge;            // 本人の端末が数えて配る
    private float _grace;            // 本人の端末: 本命を決めるまでの残り
    private float _afterMeeting;
    private int _leftCommand;
    private float _shareIn = 0.5f;
    private float _progressGrace;
    private bool _inProgress;
    private Ability _keep, _fav, _status;
    private readonly Dictionary<byte, WorldArrow> _arrows = new();

    // その会議で号令をかけたスカーレット (1 回の会議で 1 人だけ・後から押した人)
    private static byte _commandBy = 255;

    private static float GaugeMax => RequiredGauge + SurplusGauge;
    private bool GaugeMet => !WithGauge || _gauge >= RequiredGauge;
    private PlayerControl Favorite => _favorite == 255 ? null : PortKit.ById(_favorite);
    private bool IsLover(byte id) => _lovers.ContainsKey(id);

    public override void OnAssigned()
    {
        _leftCommand = NumOfCommand;
        _grace = Grace;
        if (!PreAssignedLovers)
        {
            _keep = AddAbility(new Ability(new Text("Scarlet.Keep"), "scarlet_keep")
            {
                Target = AbilityTarget.Player,
                Cooldown = () => 2f,
                MaxUses = () => NumOfKept,
                CanTarget = (_, t) => !IsLover(t.PlayerId),
                OnUsed = (_, t) => AddLover(t.PlayerId, false),
            });
            _fav = AddAbility(new Ability(new Text("Scarlet.Favorite"), "scarlet_favorite")
            {
                Target = AbilityTarget.Player,
                Cooldown = () => 2f,
                MaxUses = () => 1,
                CanTarget = (_, t) => _favorite == 255 && !IsLover(t.PlayerId),
                OnUsed = (_, t) => AddLover(t.PlayerId, true),
            });
        }
        // 砂時計 (本命が決まるまで) → 決まった後は恋のゲージ。押せない表示だけのボタン
        _status = AddAbility(new Ability(new Text("Scarlet.Hourglass"), "scarlet_hourglass")
        {
            CooldownAtStart = false,
            OnUse = () => false,
        });
        AddAbility(new Ability(new Text("Scarlet.Command"), "scarlet_command")
        {
            Target = AbilityTarget.Meeting,
            UsesPerMeeting = 1,
            CanTarget = (u, t) => t.PlayerId == PlayerId && _leftCommand > 0 && _commandBy != PlayerId && PortKit.Alive(Player),
            OnUsed = (_, _) =>
            {
                _commandBy = PlayerId;
                if (IsLocal) PortKit.Notice(new Text("Scarlet.Commanded").ToString());
            },
        });
        // 恋人の画面: 自分とスカーレットに ♡
        RoleFrame.AddExtraMark(Lifespan, seen =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || lp.PlayerId == PlayerId || !IsLover(lp.PlayerId)) return null;
            return seen.PlayerId == lp.PlayerId || seen.PlayerId == PlayerId ? $"<color={Hex}>♡</color>" : null;
        });
        if (PreAssignedLovers && AmongUsClient.Instance && AmongUsClient.Instance.AmHost) PortKit.After(0.5f, PreAssign);
        if (IsLocal) Lifespan.OnRelease(() => { StatusEffects.Hide("Scarlet"); _arrows.Clear(); });
    }

    // 全員の端末で: 恋人を足す
    private void AddLover(byte id, bool favorite)
    {
        if (IsLover(id)) return;
        _lovers[id] = favorite;
        if (favorite) _favorite = id;
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.PlayerId == id && lp.PlayerId != PlayerId)
        {
            // 本命かキープかは選ばれた側に伝えない (♡ の印と同じ)
            PortKit.Notice(new Text("Scarlet.YouWereChosen").ToString());
            Fx.Hearing.Self("scarlet_chosen", 0.45f);
        }
        if (!IsLocal) return;
        var other = favorite ? _keep : _fav;
        other?.Button?.StartCooldown();
        if (CanLocateLovers) _arrows[id] = new WorldArrow(Lifespan, favorite ? RoleDisplay.ParseColor(Hex) : UnityEngine.Color.white);
        var p = PortKit.ById(id);
        if (p && p.Data != null)
            PortKit.Notice(new Text(favorite ? "Scarlet.FavoriteChosen" : "Scarlet.KeptChosen").With("name", p.Data.PlayerName).ToString());
    }

    // ホスト: 始めから恋人が決まっている設定 (先頭が本命)
    private void PreAssign()
    {
        if (!PortKit.Alive(Player)) return;
        var cand = new List<byte>();
        foreach (var p in PlayerControl.AllPlayerControls)
            if (PortKit.Alive(p) && p.PlayerId != PlayerId) cand.Add(p.PlayerId);
        var rng = new System.Random();
        for (int i = cand.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (cand[i], cand[j]) = (cand[j], cand[i]); }
        int n = Mathf.Min(NumOfKept + 1, cand.Count);
        var picked = cand.GetRange(0, n).ToArray();
        PreCall.Send((PlayerId, picked));
        ApplyPre(this, picked);
    }

    private static readonly RemoteCall<(byte scarlet, byte[] ids)> PreCall = new("Scarlet.Pre", Route.HostToAll,
        (w, m) => { w.Write(m.scarlet); w.Write((byte)m.ids.Length); foreach (var id in m.ids) w.Write(id); },
        r =>
        {
            byte s = r.ReadByte();
            var ids = new byte[r.ReadByte()];
            for (int i = 0; i < ids.Length; i++) ids[i] = r.ReadByte();
            return (s, ids);
        },
        (_, m) => { if (RoleState.Of(m.scarlet) is Scarlet s) ApplyPre(s, m.ids); });

    private static void ApplyPre(Scarlet s, byte[] ids)
    {
        for (int i = 0; i < ids.Length; i++) s.AddLover(ids[i], i == 0);
    }

    // スカーレットはキープの役職が見える。本命は ♥・キープは ♡
    public override string NameMark(PlayerControl seen)
    {
        if (!_lovers.TryGetValue(seen.PlayerId, out bool fav)) return null;
        if (fav) return $"<color={Hex}>♥</color>";
        var role = RoleState.Of(seen);
        string label = role != null ? $" <size=70%>{role.ColoredName}</size>" : "";
        return $"<color={Hex}>♡</color>{label}";
    }

    public bool BlocksGuess(PlayerControl guesser, PlayerControl target) =>
        guesser && target && target.PlayerId == PlayerId && IsLover(guesser.PlayerId);

    public override void OnFixedUpdate()
    {
        if (!IsLocal) return;
        float dt = Time.fixedDeltaTime;
        bool meeting = MeetingHud.Instance || ExileController.Instance;
        UpdateGrace(dt, meeting);
        UpdateGauge(dt, meeting);
        UpdateArrows();
        DrawStatus();
        DrawCommand();
    }

    // 本命を決めるまでの猶予。切れたら死ぬ
    private void UpdateGrace(float dt, bool meeting)
    {
        if (PreAssignedLovers || _favorite != 255 || !PortKit.Alive(Player)) return;
        if (meeting) { _afterMeeting = 5f; return; }
        if (_afterMeeting > 0f) { _afterMeeting -= dt; return; }
        if (!ShipStatus.Instance) return;
        _grace -= dt;
        if (_grace > 0f) return;
        _grace = 0f;
        PortKit.Suicide();
    }

    // 本命の近く (2 歩) にいる間ゲージが溜まり、離れると減る。0.5 秒ごとに全員へ配る
    private void UpdateGauge(float dt, bool meeting)
    {
        _inProgress = false;
        if (!WithGauge) return;
        var fav = Favorite;
        if (!fav || !PortKit.Alive(fav) || !PortKit.Alive(Player)) return;
        if (meeting)
        {
            if (!(_progressGrace > 0f))
            {
                GaugeCall.Send((PlayerId, _gauge));
                _progressGrace = 5f;
                _shareIn = 0.5f;
            }
            return;
        }
        if (_progressGrace > 0f) { _progressGrace -= dt; return; }
        _shareIn -= dt;
        if (_shareIn < 0f)
        {
            GaugeCall.Send((PlayerId, _gauge));
            _shareIn = 0.5f;
        }
        bool visible = fav.Visible && !Cloak.IsOn(fav.PlayerId);
        if (visible && Vector2.Distance(fav.GetTruePosition(), Player.GetTruePosition()) < NearDistance)
        {
            _gauge = Mathf.Clamp(_gauge + dt, 0f, GaugeMax);
            _inProgress = true;
        }
        else _gauge = Mathf.Clamp(_gauge - dt * GaugeReduction, 0f, GaugeMax);
    }

    private static readonly RemoteCall<(byte id, float gauge)> GaugeCall = new("Scarlet.Gauge", Route.Anyone,
        (w, m) => { w.Write(m.id); w.Write(m.gauge); }, r => (r.ReadByte(), r.ReadSingle()),
        (_, m) => { if (RoleState.Of(m.id) is Scarlet s) s._gauge = m.gauge; });

    private void UpdateArrows()
    {
        if (_arrows.Count == 0) return;
        bool show = PortKit.Alive(Player) && !MeetingHud.Instance;
        foreach (var (id, arrow) in _arrows)
        {
            var p = PortKit.ById(id);
            bool on = show && PortKit.Alive(p);
            arrow.SetActive(on);
            if (on) arrow.Update(p.transform.position);
        }
    }

    private int _drawn = int.MinValue;

    // 表示だけのボタン: 本命が決まるまでは猶予の残り、決まった後はゲージ
    private void DrawStatus()
    {
        var b = _status?.Button;
        if (b == null) return;
        b.SetUsable(false);
        var fav = Favorite;
        if (fav == null)
        {
            if (PreAssignedLovers) return;
            b.SetLabel(new Text("Scarlet.Hourglass").ToString());
            b.ShowGauge(_grace, Grace, Mathf.CeilToInt(_grace).ToString());
            int sec = Mathf.CeilToInt(_grace);
            if (sec == _drawn) return;
            _drawn = sec;
            StatusEffects.ShowUntil("Scarlet", new Text("Scarlet.GraceStatus").With("n", sec), Hex, Text.Raw("⌛"));
            return;
        }
        if (!WithGauge)
        {
            b.SetLabel(new Text("Scarlet.Gauge").ToString());
            b.ShowGauge(0f, 1f, null);
            if (_drawn == -2) return;
            _drawn = -2;
            StatusEffects.ShowUntil("Scarlet", new Text("Scarlet.FavoriteStatus").With("name", fav.Data?.PlayerName ?? ""), Hex, Text.Raw("♥"));
            return;
        }
        float pct = _gauge / RequiredGauge * 100f;
        b.SetLabel(new Text("Scarlet.Gauge").ToString());
        b.ShowGauge(Mathf.Max(RequiredGauge - _gauge, 0f), RequiredGauge, GaugeMet ? "♥" : $"{pct:F0}%");
        int shown = (int)(pct * 10f) * 2 + (_inProgress ? 1 : 0);
        if (shown == _drawn) return;
        _drawn = shown;
        StatusEffects.ShowUntil("Scarlet", new Text("Scarlet.GaugeStatus").With("name", fav.Data?.PlayerName ?? "").With("n", pct.ToString("F1")),
            GaugeMet ? Hex : _inProgress ? "#FF8FA8" : "#9A9A9A", Text.Raw(_inProgress ? "▲" : GaugeMet ? "♥" : "▼"));
    }

    // 号令をかけている間、自分の投票先の名札を縁取る
    private static Sprite _frame;
    private SpriteRenderer _frameArt;

    private void DrawCommand()
    {
        var mh = MeetingHud.Instance;
        if (!mh || _commandBy != PlayerId) { if (_frameArt) Object.Destroy(_frameArt.gameObject); _frameArt = null; return; }
        PlayerVoteArea mine = null, target = null;
        foreach (var pva in mh.playerStates) if (pva && pva.PlayerId.Value == PlayerId) mine = pva;
        if (mine && mine.DidVote)
            foreach (var pva in mh.playerStates) if (pva && pva.PlayerId.Value == mine.VotedForId.Value) target = pva;
        if (!target) { if (_frameArt) _frameArt.enabled = false; return; }
        _frame ??= FxSprites.Rect(256, 64, (x, y) =>
        {
            float ex = 0.5f - Mathf.Abs(x), ey = (0.5f - Mathf.Abs(y)) / 4f;
            float e = Mathf.Min(ex, ey);
            return e < 0.012f ? 1f : e < 0.03f ? (0.03f - e) / 0.018f * 0.7f : 0f;
        }, "MrpScarletFrame");
        if (!_frameArt)
        {
            var go = new GameObject("MrpScarletCommand");
            _frameArt = go.AddComponent<SpriteRenderer>();
            _frameArt.sprite = _frame;
            Lifespan.Bind(go);
        }
        _frameArt.enabled = true;
        if (_frameArt.transform.parent != target.transform)
        {
            _frameArt.transform.SetParent(target.transform, false);
            _frameArt.transform.localPosition = new Vector3(0f, 0f, -0.5f);
            _frameArt.transform.localScale = new Vector3(0.68f, 0.68f, 1f);
        }
        var c = RoleDisplay.ParseColor(Hex);
        c.a = 0.65f + 0.35f * Mathf.Sin(Time.time * 6f);
        _frameArt.color = c;
    }

    private void OnMeetingStart(MeetingStartEvent e) => _commandBy = 255;

    // ホスト: 号令が出ていれば、生きている恋人の票をスカーレットの投票先へ向ける
    [HostOnly]
    private void OnVoteTally(VoteTallyEvent e)
    {
        if (_commandBy != PlayerId || !PortKit.Alive(Player)) return;
        byte to = e.TargetOf(PlayerId);
        if (to == 255) return;
        foreach (var id in _lovers.Keys)
            if (PortKit.Alive(PortKit.ById(id))) e.Redirect(id, to);
        CommandUsedCall.Send(PlayerId);
        _leftCommand = Mathf.Max(_leftCommand - 1, 0);
    }

    private static readonly RemoteCall<byte> CommandUsedCall = new("Scarlet.CommandUsed", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (_, id) => { if (RoleState.Of(id) is Scarlet s) s._leftCommand = Mathf.Max(s._leftCommand - 1, 0); });

    // ---- 本命と自分の死の連動 (ホスト) ----

    [HostOnly]
    private void OnMurdered(PlayerMurderedEvent e) => Link(e.Target, false);

    [HostOnly]
    private void OnExiled(PlayerExiledEvent e) => Link(e.Player, true);

    private void Link(PlayerControl dead, bool exiled)
    {
        if (!dead || _favorite == 255) return;
        PlayerControl follower;
        if (dead.PlayerId == _favorite)
        {
            // 本命がジェスターで追放されて勝つ時は、ゲージが溜まっていれば後を追わない (勝ちを奪うため)
            if (exiled && RoleState.Of(dead) is Jester && GaugeMet) return;
            follower = Player;
        }
        else if (dead.PlayerId == PlayerId) follower = Favorite;
        else return;
        if (!PortKit.Alive(follower)) return;
        PortKit.After(0.05f, () =>
        {
            if (!PortKit.Alive(follower)) return;
            if (exiled || MeetingHud.Instance || ExileController.Instance) PortKit.HostVanish(follower, follower);
            else PortKit.HostSuicide(follower);
        });
    }

    // ---- 勝ち ----

    // 本命が勝つ時に 2 人とも生きていれば (ゲージが足りていれば) 2 人だけの勝ちにする
    public override bool StealsWin(GameResult result)
    {
        var fav = Favorite;
        if (!fav || !PortKit.Alive(fav) || !PortKit.Alive(Player) || !GaugeMet) return false;
        if (!CanOverrideTaskWin && result.ByTasks) return false;
        return result.Winners.Contains(fav.PlayerId);
    }

    public override IEnumerable<byte> StealsWith => _favorite == 255 ? null : new[] { _favorite };

    // 本命は、スカーレットが死んでいる (ジェスターの勝ちは除く) かゲージが足りないと、自分の陣営で勝てない
    public override bool BlocksWinOf(byte pid, GameResult result)
    {
        if (pid != _favorite || result.Role == this) return false;
        if (!PortKit.Alive(Player) && result.Role is not Jester) return true;
        return WithGauge && !GaugeMet;
    }

    // タスク勝利を奪えない設定では、本命のタスクはクルーのタスクに数えない
    public override IEnumerable<byte> IgnoresTasksOf => !CanOverrideTaskWin && _favorite != 255 ? new[] { _favorite } : null;
}
