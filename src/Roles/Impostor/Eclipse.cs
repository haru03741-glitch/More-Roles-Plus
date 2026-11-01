// Ported from https://github.com/Dolly1016/UchuAddon UchuAddon/Scripts/Role/Impostor/Eclipse.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// エクリプス (インポスター): 「マーク」で足元に隕石の落ちる所の印を置く (少しの間動けない・音が鳴る)。
// 「メテオ」で印の所へ隕石を落とす。全員の画面に赤い円で警告が出て、決めた秒の後に落ち、円の中の人を味方も自分も問わず倒す。
// 印は自分にしか見えない。会議で印を消す (設定)
public sealed class Eclipse : RoleBase
{
    public override Team Team => Team.Impostor;
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

    private Vector2? _mark;
    private SpriteRenderer _markArt;
    private Sprite[] _antenna;
    private int _frame;
    private float _frameT;
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
    }

    private bool PlaceMark()
    {
        var lp = PlayerControl.LocalPlayer;
        Vector2 at = lp.transform.position;
        ClearMark();
        _mark = at;
        _antenna ??= ButtonIcons.Frames("eclipse_antenna", 4, 200f);
        _markArt = PortArt.Spawn(_antenna != null ? _antenna[0] : ButtonIcons.Get("snr_marker"), at, _antenna != null ? 1f : 0.5f, UnityEngine.Color.white, at.y / 1000f + 0.01f);
        Lifespan.Bind(_markArt.gameObject);
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
        if (_markArt) Object.Destroy(_markArt.gameObject);
        _markArt = null;
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        _blast?.Button?.SetUsable(_mark != null && _blast.UsesLeft != 0);
        if (!_markArt || _antenna == null) return;
        _frameT -= Time.fixedDeltaTime;
        if (_frameT > 0f) return;
        _frameT = 0.21f;
        _markArt.sprite = _antenna[++_frame % _antenna.Length];
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (IsLocal && MeetingReset) ClearMark();
    }

    private static readonly RemoteCall<(float x, float y, float size)> BlastCall = new("Eclipse.Blast", Route.Anyone,
        (w, m) => { w.Write(m.x); w.Write(m.y); w.Write(m.size); }, r => (r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        (sender, m) => { if (RoleState.Of(sender) is Eclipse e) OnBlast(e, FxMath.V2(m.x, m.y), m.size); });

    // 全員の端末で: 警告の円 → 隕石 → 爆発。倒すのはホスト
    private static void OnBlast(Eclipse e, Vector2 at, float size)
    {
        float warn = WarningTime;
        RangedKillSnapPatch.Mark(e.PlayerId, warn + 3f);
        PortSound.Play("eclipse_alert", () => PortSound.Fallback("eclipse_alert"), 0.8f);
        var circle = PortArt.Spawn(PortArt.Disc, at, size * 2f, new UnityEngine.Color(1f, 0f, 0f, 0.5f), -9f);
        PortKit.After(warn, () =>
        {
            if (circle) Object.Destroy(circle.gameObject);
            if (MeetingHud.Instance || !ShipStatus.Instance) return;
            Fall(e, at, size);
        });
    }

    private static Sprite[] _meteor, _explosion;

    private static void Fall(Eclipse e, Vector2 at, float size)
    {
        _meteor ??= ButtonIcons.Frames("eclipse_meteor_fall", 2, 100f);
        _explosion ??= ButtonIcons.Frames("eclipse_explosion", 8, 100f, 2);
        var art = PortArt.Spawn(_meteor != null ? _meteor[0] : PortArt.Disc, at, _meteor != null ? 1f : 0.4f,
            _meteor != null ? UnityEngine.Color.white : new UnityEngine.Color(1f, 0.55f, 0.15f, 1f), -10f);
        if (_meteor != null) PortKit.After(0.05f, () => { if (art) art.sprite = _meteor[1]; });
        PortKit.After(0.1f, () =>
        {
            PortSound.Play("eclipse_boom", () => PortSound.Fallback("eclipse_boom"), 1f, at, 20f);
            if (AmongUsClient.Instance.AmHost) HostBlast(e.Player, at, size);
            if (!art) return;
            if (_explosion != null)
            {
                art.color = UnityEngine.Color.white;
                for (int i = 0; i < _explosion.Length; i++)
                {
                    int f = i;
                    PortKit.After(0.12f * f, () => { if (art) { art.sprite = _explosion[f]; art.transform.localScale = Vector3.one * Mathf.Max(1f, size * 0.8f); } });
                }
                PortKit.After(0.12f * _explosion.Length, () => { if (art) Object.Destroy(art.gameObject); });
                return;
            }
            // 絵が無い時: 橙の丸が円の大きさまで広がって薄れる
            for (int i = 0; i <= 8; i++)
            {
                float t = i / 8f;
                PortKit.After(0.06f * i, () =>
                {
                    if (!art) return;
                    art.transform.localScale = Vector3.one * Mathf.Lerp(0.4f, size * 2f, t);
                    art.color = new UnityEngine.Color(1f, 0.55f + 0.3f * t, 0.15f, 1f - t);
                });
            }
            PortKit.After(0.55f, () => { if (art) Object.Destroy(art.gameObject); });
        });
    }

    // ホスト: 円の中の生きている人を全員倒す (味方も自分も)。キルを防ぐ役職には聞く
    private static void HostBlast(PlayerControl invoker, Vector2 at, float size)
    {
        if (!invoker) return;
        bool self = false;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.inVent) continue;
            if (Vector2.Distance(p.GetTruePosition(), at) > size) continue;
            if (p.PlayerId == invoker.PlayerId) { self = true; continue; }
            if (BeforeMurderEvent.Ask(invoker, p) != null) continue;
            invoker.RpcMurderPlayer(p, true);
        }
        // 自分が巻き込まれた時は、他の人を倒し終えてから
        if (self) PortKit.HostSuicide(invoker);
    }
}
