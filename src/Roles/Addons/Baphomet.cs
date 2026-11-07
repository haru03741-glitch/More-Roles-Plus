// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Ghost/Complex/Baphomet.cs (BaphometU, FamulusU; role idea and logic; UchuAddon allows porting them, no assets used)
using System.Linq;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// バフォメット (幽霊役職・どの陣営の死者にも): なった時に画面が光る。「眷属」(1 回) で近くの生きている人をファムルス (眷属) にする。
// 生きているファムルスのすぐ近くで止まっているとゲージが貯まり (止まって少しすると、生きている人にだけ見える亡霊が出る)、
// 満ちた後の試合の終わり (破壊工作以外) にファムルスが生きていれば、バフォメットとファムルスの勝ちに替わる。会議の終わりにゲージが減る (満ちていれば減らない)
public sealed class Baphomet : AddonBase, IWinOverride
{
    public const string Hex = "#1F1A7A";

    public override string Color => Hex;
    public override Text Name => new("Baphomet.Name");
    public override string Reading => "ばふぉめっと";
    public override Text Blurb => new("Baphomet.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Baphomet.Family", "baphomet_family"), new("Baphomet.Gauge") };
    public override bool Ghost => true;
    public override bool AffectsWin => true;

    public static readonly FloatOpt TotalStandingTimeToWin = new("Baphomet.TotalStandingTimeToWin", 30f, 15f, 120f, 2.5f, "s");
    public static readonly FloatOpt RewindGaugeToMeetingEnd = new("Baphomet.RewindGaugeToMeetingEnd", 5f, 0f, 60f, 2.5f, "s");

    private static readonly Color32 Tint = new(31, 26, 122, 255);

    private byte _famulus = 255;     // 全員の端末で同じ
    private float _progress, _standing, _standCooldown = 10f; // 自分の端末
    private bool _canWin;            // 全員の端末で同じ (満ちた時に配る)
    private WorldArrow _arrow;
    private GameObject _ghost;       // 全員の端末: 亡霊
    private float _ghostAlpha;
    private bool _ghostOn;

    // ホストで決めた、この試合の終わりがバフォメットの勝ちか
    internal static GameResult WinResult;

    public override void OnAssigned()
    {
        if (IsLocal)
        {
            PortKit.Flash(Tint);
            PortSound.Play("baphomet_awake", () => PortSound.Fallback("notice"), 0.8f);
        }
        AddAbility(new Ability(new Text("Baphomet.Family"), "baphomet_family")
        {
            Ghost = true,
            Target = AbilityTarget.Player,
            Cooldown = () => 5f,
            MaxUses = () => 1,
            CanTarget = (_, t) => !Famulus.Of(t),
            OnUsed = (_, t) =>
            {
                _famulus = t.PlayerId;
                if (AmongUsClient.Instance.AmHost) RoleState.AddAddon(t, RoleState.AddonProto<Famulus>());
                // 全員にカットイン (光と音)
                PortKit.Flash(Tint, 1.2f);
                PortSound.Play("baphomet_family", () => PortSound.Fallback("notice"), 0.8f);
                PortKit.Notice(new Text("Baphomet.Cutin").ToString());
            },
        });
        Lifespan.OnRelease(() => { if (_ghost) Object.Destroy(_ghost); });
    }

    internal bool IsMyFamulus(PlayerControl p) => p && p.PlayerId == _famulus;
    private PlayerControl FamulusPlayer => _famulus == 255 ? null : PortKit.ById(_famulus);

    private void OnMeetingStart(MeetingStartEvent e)
    {
        _standCooldown = 5f;
        SetGhost(false);
    }

    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (!IsLocal || _canWin) return;
        _progress = Mathf.Max(0f, _progress - RewindGaugeToMeetingEnd);
    }

    public override void OnFixedUpdate()
    {
        FadeGhost();
        if (!IsLocal || !Player || Player.Data == null || !Player.Data.IsDead) return;
        float dt = Time.fixedDeltaTime;
        var fam = FamulusPlayer;
        // 眷属への矢印
        _arrow ??= new WorldArrow(Lifespan, Tint);
        bool showArrow = PortKit.Alive(fam) && !MeetingHud.Instance;
        _arrow.SetActive(showArrow);
        if (showArrow) _arrow.Update(fam.transform.position);

        bool meeting = MeetingHud.Instance || ExileController.Instance;
        bool near = PortKit.Alive(fam) && Vector2.Distance(fam.transform.position, Player.transform.position) < 1.5f;
        if (_standCooldown > 0f)
        {
            if (!meeting) _standCooldown -= dt;
            _standing = 0f;
        }
        else if (!meeting && Player.MyPhysics && Player.MyPhysics.Velocity.magnitude < 0.001f && near) _standing += dt;
        else _standing = 0f;

        if (_standing > 0.6f && !_ghostOn) GhostCall.Send((true, Player.transform.position.x, Player.transform.position.y, Player.cosmetics && Player.cosmetics.FlipX));
        if (_standing <= 0f && _ghostOn) GhostCall.Send((false, 0f, 0f, false));
        if (_standing > 0.6f && !_ghostOn) SetGhost(true, Player.transform.position, Player.cosmetics && Player.cosmetics.FlipX);
        if (_standing <= 0f && _ghostOn) SetGhost(false);

        if (!_canWin && _standing > 0.8f && near)
        {
            _progress += dt;
            if (_progress > TotalStandingTimeToWin)
            {
                _canWin = true;
                WinCall.Send(true);
                PortKit.Flash(Tint);
            }
        }
    }

    private static readonly RemoteCall<bool> WinCall = new("Baphomet.Win", Route.Anyone,
        (w, m) => w.Write(m), r => r.ReadBoolean(),
        (sender, m) => { foreach (var a in RoleState.AddonsOf(sender.PlayerId)) if (a is Baphomet b) b._canWin = m; }, perSecond: 2);

    private static readonly RemoteCall<(bool on, float x, float y, bool flip)> GhostCall = new("Baphomet.Ghost", Route.Anyone,
        (w, m) => { w.Write(m.on); w.Write(m.x); w.Write(m.y); w.Write(m.flip); }, r => (r.ReadBoolean(), r.ReadSingle(), r.ReadSingle(), r.ReadBoolean()),
        (sender, m) => { foreach (var a in RoleState.AddonsOf(sender.PlayerId)) if (a is Baphomet b) b.SetGhost(m.on, new Vector3(m.x, m.y, 0f), m.flip); }, perSecond: 6);

    // 亡霊 (生きている人にだけ見える自分の姿)
    private void SetGhost(bool on, Vector3 at = default, bool flip = false)
    {
        _ghostOn = on;
        if (!on) return;
        if (_ghost) Object.Destroy(_ghost);
        var hud = Vanilla.Hud;
        var proto = hud && hud.IntroPrefab ? hud.IntroPrefab.PlayerPrefab : null;
        if (!proto || Player.Data == null) return;
        var pp = Object.Instantiate(proto);
        pp.name = "MrpBaphometGhost";
        pp.transform.position = new Vector3(at.x, at.y, at.y / 1000f);
        pp.transform.localScale = new Vector3(0.5f, 0.5f, 1f);
        try
        {
            pp.UpdateFromPlayerOutfit(Player.Data.DefaultOutfit, PlayerMaterial.MaskType.None, false, false);
            pp.ToggleName(false);
            pp.SetFlipX(flip);
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"baphomet ghost: {e.Message}"); }
        int layer = LayerMask.NameToLayer("Players");
        foreach (var t in pp.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        _ghost = pp.gameObject;
        _ghostAlpha = 0f;
        Alpha(0f);
    }

    private void FadeGhost()
    {
        if (!_ghost) return;
        float dt = Time.fixedDeltaTime;
        _ghostAlpha = _ghostOn ? Mathf.Min(1f, _ghostAlpha + dt * 0.6f) : _ghostAlpha - dt * 2.5f;
        if (_ghostAlpha <= 0f && !_ghostOn) { Object.Destroy(_ghost); _ghost = null; return; }
        var lp = PlayerControl.LocalPlayer;
        Alpha(lp && lp.Data != null && !lp.Data.IsDead ? _ghostAlpha : 0f);
    }

    private void Alpha(float a)
    {
        foreach (var sr in _ghost.GetComponentsInChildren<SpriteRenderer>(true)) sr.color = new UnityEngine.Color(sr.color.r, sr.color.g, sr.color.b, a);
    }

    // 自分の名前の下にゲージ。眷属の名前はバフォメットの色に見える
    public override string NameMark(PlayerControl seen)
    {
        if (!IsLocal) return null;
        if (seen.PlayerId == PlayerId && Player.Data != null && Player.Data.IsDead)
        {
            if (_canWin) return $"<size=70%><color=#40FF40>{new Text("Baphomet.GaugeText")}</color></size>";
            string col = _progress > 0f ? (_standing < 0.8f ? "#808000" : "#FFFF00") : (_standing < 0.8f ? "#808080" : "#FFFFFF");
            return $"<size=70%><color={col}>{new Text("Baphomet.GaugeText")} {_progress:0.#}s/{TotalStandingTimeToWin.Value:0.#}s</color></size>";
        }
        return null;
    }

    // ホスト: 試合の終わりにバフォメットの勝ちへ替える
    public GameResult OverrideWin(GameResult r)
    {
        WinResult = null;
        if (!_canWin || r.BySabotage || !PortKit.Alive(FamulusPlayer)) return null;
        var nr = new GameResult { Role = RoleState.Of(PlayerId) };
        nr.Winners.Add(PlayerId);
        nr.Winners.Add(_famulus);
        WinResult = nr;
        return nr;
    }

    public override bool LosesWin(GameResult r) => !ReferenceEquals(r, WinResult);
}

// ファムルス (バフォメットの眷属・バフォメットから付けられるだけ): 名前がバフォメットの色に見える。バフォメットの勝ちでだけ勝ち、タスクは数えない
public sealed class Famulus : AddonBase
{
    public override string Color => Baphomet.Hex;
    public override Text Name => new("Famulus.Name");
    public override string Reading => "ふぁむるす";
    public override Text Blurb => new("Famulus.Blurb");
    public override string Origin => "Uchu";
    public override bool MadeByOthers => true;
    public override bool AffectsWin => true;
    public override bool TasksCount => false;

    internal static bool Of(PlayerControl p) => p && RoleState.AddonsOf(p.PlayerId).Any(a => a is Famulus);

    public override void OnAssigned()
    {
        if (IsLocal) PortKit.Flash(new Color32(31, 26, 122, 255), 0.4f);
    }

    public override bool LosesWin(GameResult r) => !ReferenceEquals(r, Baphomet.WinResult);

    // 眷属とバフォメットの名前は、お互いの端末でバフォメットの色に見える
    public override void OnFixedUpdate()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !Player || !Player.cosmetics || !Player.cosmetics.nameText) return;
        var mine = RoleState.AllAddons.OfType<Baphomet>().FirstOrDefault(b => b.IsMyFamulus(Player));
        if (mine == null) return;
        UnityEngine.Color c = new Color32(31, 26, 122, 255);
        if (lp.PlayerId == mine.PlayerId) Player.cosmetics.nameText.color = c;
        if (lp.PlayerId == PlayerId && mine.Player && mine.Player.cosmetics && mine.Player.cosmetics.nameText) mine.Player.cosmetics.nameText.color = c;
    }
}
