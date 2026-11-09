// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Eclipse.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// エクリプス (インポスター): 「マーク」で足元に隕石の落ちる所の印を置く (少しの間動けない・音が鳴る)。
// 「メテオ」で印の所へ隕石を落とす。全員の画面に赤い円で警告が出て、決めた秒の後に落ち、円の中の人を味方も自分も問わず倒す。
// 印は自分にしか見えない。会議で印を消す (設定)
public sealed partial class Eclipse : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Eclipse.Name");
    public override string Reading => "えくりぷす";
    public override Text Blurb => new("Eclipse.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Eclipse.Mark", "eclipse_mark"), new("Eclipse.Blast", "eclipse_meteor") };

    public static readonly IntOpt Uses = new("Eclipse.Uses", 3, 1, 99);
    public static readonly FloatOpt MarkCooldown = new("Eclipse.MarkCooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt BlastCooldown = new("Eclipse.BlastCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt WarningTime = new("Eclipse.WarningTime", 1f, 0f, 3f, 0.25f, "s");
    public static readonly FloatOpt MarkStop = new("Eclipse.MarkStop", 1f, 0.5f, 5f, 0.5f, "s");
    public static readonly FloatOpt Size = new("Eclipse.Size", 1.5f, 1f, 15f, 0.25f, "x");
    public static readonly BoolOpt ResetBlastCool = new("Eclipse.ResetBlastCool", false);
    public static readonly BoolOpt MeetingReset = new("Eclipse.MeetingReset", true);
    public static readonly BoolOpt MarkSound = new("Eclipse.MarkSound", true);
    public static readonly FloatOpt SoundRange = new("Eclipse.SoundRange", 10f, 2.5f, 20f, 2.5f, "x") { When = () => MarkSound };

    private const float FallSoundLength = 1f, SoundReach = 25f;

    private Vector2? _mark;
    private Ability _markAbility, _blast;

    public override void OnAssigned()
    {
        _markAbility = AddAbility(new Ability(new Text("Eclipse.Mark"), "eclipse_mark")
        {
            Cooldown = () => MarkCooldown,
            OnUse = PlaceMark,
        });
        _blast = AddAbility(new Ability(new Text("Eclipse.Blast"), "eclipse_meteor")
        {
            Cooldown = () => BlastCooldown,
            MaxUses = () => Uses,
            OnUse = Blast,
        });
        if (IsLocal) Lifespan.OnRelease(EclipseFx.HideMark);
    }

    private bool PlaceMark()
    {
        var lp = PlayerControl.LocalPlayer;
        Vector2 at = lp.transform.position;
        ClearMark();
        _mark = at;
        EclipseFx.ShowMark(at, Size);
        MoveSpeed.Add("EclipseStop", 0f, MarkStop);
        if (MarkSound) PortSound.PlayAll("eclipse_mark", at, SoundRange, 0.6f);
        else PortSound.Play("eclipse_mark", () => PortSound.Fallback("eclipse_mark"), 0.6f);
        if (ResetBlastCool) _blast?.Button?.StartCooldown();
        return true;
    }

    private bool Blast()
    {
        if (_mark is not Vector2 at) return false;
        BlastCall.Send((at.x, at.y, Size));
        OnBlast(this, at, Size);
        _markAbility?.Button?.StartCooldown();
        ClearMark();
        return true;
    }

    private void ClearMark()
    {
        _mark = null;
        if (IsLocal) EclipseFx.HideMark();
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        _blast?.Button?.SetUsable(_mark != null && _blast.UsesLeft != 0);
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (IsLocal && MeetingReset) ClearMark();
    }

    private static readonly RemoteCall<(float x, float y, float size)> BlastCall = new("Eclipse.Blast", Route.Anyone,
        (w, m) => { w.Write(m.x); w.Write(m.y); w.Write(m.size); }, r => (r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        (sender, m) => { if (RoleState.Of(sender) is Eclipse e && e.AcceptBlast()) OnBlast(e, FxMath.V2(m.x, m.y), Size); });

    // 受けた側の端末で: 回数と待ち時間を超えた爆発は無視する。大きさは届いた値でなく設定の値を使う
    private int _blastsSeen;
    private float _lastBlastAt = -999f;

    private bool AcceptBlast()
    {
        float now = Time.time;
        if (_blastsSeen >= Uses || (now - _lastBlastAt < BlastCooldown * 0.5f && !Dev.DevCooldown.Zero)) return false;
        _blastsSeen++;
        _lastBlastAt = now;
        return true;
    }

    // 全員の端末で: 影と照準の警告 → 隕石が落ちる → 着弾 (Settle)。倒すのと地形を壊すのはホスト
    private static void OnBlast(Eclipse e, Vector2 at, float size)
    {
        float impact = EclipseFx.ImpactDelay(WarningTime);
        RangedKillSnapPatch.Mark(e.PlayerId, impact + 3f);
        PortSound.Play("eclipse_warn", () => PortSound.Fallback("eclipse_alert"), 0.7f);
        var st = AddStrike(e.PlayerId, at, size, impact);
        EclipseFx.Begin(at, size, WarningTime, st.Key);
        // 迫る唸りは 1 秒の音なので、着弾のちょうど 1 秒前から (警告が短ければすぐ) 鳴らす
        float fallAt = impact > FallSoundLength ? impact - FallSoundLength : 0f;
        PortKit.After(fallAt, () =>
        {
            if (MeetingHud.Instance || !ShipStatus.Instance) return;
            PortSound.Play("eclipse_fall", null, 1f, at, SoundReach);
        });
        PortKit.After(impact, () => Settle(st, e));
    }

    // ホスト: 円の中の生きている人を全員倒す (味方も自分も)。キルを防ぐ役職には聞く。サムライは体力が減るだけ
    private static void HostBlast(PlayerControl invoker, Vector2 at, float size)
    {
        if (!invoker) return;
        bool self = false;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.inVent) continue;
            if (Vector2.Distance(p.GetTruePosition(), at) > size) continue;
            if (p.PlayerId == invoker.PlayerId) { self = true; continue; }
            if (RoleState.Of(p) is Neutral.Samurai) { HostHitSamurai(invoker, p); continue; }
            if (BeforeMurderEvent.Ask(invoker, p) != null) continue;
            PortKit.HostKillAsked(invoker, p, PortKit.Harm.Blast);
        }
        Crew.Doppelganger.HostStrikeArea(invoker, at, size); // 分身も円の中なら倒れる (本物が分身の所で)
        // 地形: 範囲の壁が壊れて床が焦げ、着弾点に小さな火が残る (地形の爆発は半径 3 まで)
        var res = Terrain.TerrainApi.WorldBlast(at, System.Math.Min(size, Terrain.TerrainPermit.MaxRadius));
        if (!res.Ok) Plugin.Logger.LogInfo($"eclipse: terrain blast refused ({res.Why})");
        Terrain.TerrainApi.WorldIgnite(at, 0.5f, 0.6f);
        // 自分が巻き込まれた時は、他の人を倒して地形を壊し終えてから
        if (self) PortKit.HostSuicide(invoker);
    }
}
