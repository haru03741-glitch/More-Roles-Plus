// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Kunoichi.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/ykundesu/SuperNewRoles (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Options;
using MoreRolesPlus.UI;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// クノイチ (インポスター): 「装備」でクナイを持つ (持っている間は本編のキルは無い)。クナイはマウスの向きに投げ、
// 仲間以外に当たるとその人の当たった数が増え、決めた数で倒す。倒した死体にはクナイが残る。
// 止まっていると透明になれる (動くと解ける)。会議で全部解ける。
// PC は右クリックを押している間構え、左クリックを押すと投げる (機関銃・ノヴァと同じ)。スマホは装備のボタンで構え、射撃スティックを倒すと投げる。
// 構えた手とクナイは全員に見える
public sealed partial class Kunoichi : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Kunoichi.Name");
    public override string Reading => "くのいち";
    public override Text Blurb => new("Kunoichi.Blurb");
    public override string Origin => "Uchu, SNR";
    public override HelpSection[] HelpSections => new HelpSection[]
        { new("Kunoichi.Equip", "kunoichi_equip"), new("Kunoichi.Throw", "kunoichi_kunai"), new("Kunoichi.Hide", "kunoichi_hide") };

    public static readonly FloatOpt ThrowCooldown = new("Kunoichi.ThrowCooldown", 0.5f, 0f, 10f, 0.25f, "s");
    public static readonly FloatOpt KunaiSpeed = new("Kunoichi.KunaiSpeed", 3f, 0.5f, 10f, 0.5f, "x");
    public static readonly BoolOpt WallStops = new("Kunoichi.WallStops", true);
    public static readonly IntOpt HitsToKill = new("Kunoichi.HitsToKill", 10, 1, 30);
    public static readonly FloatOpt KunaiSize = new("Kunoichi.KunaiSize", 1.25f, 0.25f, 4f, 0.25f, "x");
    public static readonly BoolOpt FriendlyFire = new("Kunoichi.FriendlyFire", false);
    public static readonly BoolOpt KunaiOnBody = new("Kunoichi.KunaiOnBody", true);
    public static readonly BoolOpt ResetHitsOnMeeting = new("Kunoichi.ResetHitsOnMeeting", false);
    public static readonly BoolOpt CanHide = new("Kunoichi.CanHide", true);
    public static readonly FloatOpt HideWait = new("Kunoichi.HideWait", 3f, 0.5f, 10f, 0.5f, "s") { When = () => CanHide };
    public static readonly BoolOpt HideButton = new("Kunoichi.HideButton", false) { When = () => CanHide }; // 既定は止まったら自動 (原作の既定はボタン)
    public static readonly BoolOpt ThrowWhileHidden = new("Kunoichi.ThrowWhileHidden", true) { When = () => CanHide };

    private const float MaxTravel = 30f, DefaultSize = 1.25f;
    private static float HitLength => KunaiSize;       // 当たりの長さ (原作 KunaiSize)
    private static float HitRadius => KunaiSize * 0.4f; // 当たりの半径 (原作 size*0.4)
    internal static float SizeScale => KunaiSize / DefaultSize; // 絵の倍率 (既定 1.25 で 1)
    private const float SettleLen = 0.6f; // 投げたクナイの絵が、手を離れた所から狙いの線の上へ寄りきるまでに飛ぶ長さ

    private bool _equipped;
    internal bool Equipped => _equipped;
    private bool _hidden;
    private float _still;
    private Vector2 _lastPos;
    private Ability _throw, _hide;

    // 自分の端末: 右クリックで構えた (離すとしまう)・前の tick のボタン・スティックで最後に狙った向き
    private bool _byRight, _rightPrev, _leftPrev, _stickPrev, _stickAimed, _stickTilted;
    private int _lineFrame;     // 狙いの線の長さ (壁まで) を測り直す tick
    private const float LineMax = 7f; // 狙いの線の長さの上限 (画面の中で足りる長さ)
    private Vector2 _stickDir = Vector2.right;
    // 自分の端末: 構えと狙いを最後に配った値と時刻 (変わった時だけ・間を空けて送る)
    private bool _sentOn;
    private float _sentAngle, _poseAt = -99f;
    private const float PoseGap = 0.1f, AimGap = 0.15f, AimSendDeg = 3f;

    // 当たった数 (ホストが数えて全員へ配る)
    private readonly Dictionary<byte, int> _hits = new();

    public override void OnAssigned()
    {
        // 透明でも体はあるので、水と粉に跡が残り煙を押し分ける
        CrewTraits.Set(PlayerId, CrewTraits.Trait.Trace, () => Cloak.IsOn(PlayerId));
        AddAbility(new Ability(new Text("Kunoichi.Equip"), "kunoichi_equip")
        {
            CooldownAtStart = false,
            PcMouse = true, // PC は右クリック
            OnUse = () => { SetEquip(!_equipped); return false; },
        });
        _throw = AddAbility(new Ability(new Text("Kunoichi.Throw"), "kunoichi_kunai")
        {
            Cooldown = () => ThrowCooldown,
            CooldownAtStart = false,
            PcMouse = true, // PC は左クリック
            Usable = () => _equipped && (!_hidden || ThrowWhileHidden), // 使えない間はボタンを暗くする
            OnUse = () =>
            {
                if (!_equipped || (_hidden && !ThrowWhileHidden) || Knocked) return false; // 弾き飛ばされたクナイを握り直すまでは投げられない
                Vector2 from = Player.GetTruePosition();
                float angle = Aim();
                if (Blocked(from, angle)) return false;
                byte seq = _seq++;
                ThrowCall.Send((from.x, from.y, angle, seq));
                Fly(this, from, angle, true, seq, KunaiHand.Throw(Player, angle, ThrowCooldown));
                return true;
            },
        });
        if (CanHide && HideButton)
            _hide = AddAbility(new Ability(new Text("Kunoichi.Hide"), "kunoichi_hide")
            {
                CooldownAtStart = false,
                Usable = () => _hidden || _still >= HideWait, // 使えない間はボタンを暗くする (透明の間は押すと解ける)
                OnUse = () =>
                {
                    if (_hidden) { Hide(false); return false; } // 押し直すと解ける (原作は切り替え)
                    if (_still < HideWait) return false;
                    Hide(true);
                    return false;
                },
            });
        Lifespan.OnRelease(() =>
        {
            KunaiHand.Show(Player, false, 0f);
            HeldBody(false);
            if (IsLocal && _equipped && Stick) FireStick.Arm(false);
        });
    }

    private void SetEquip(bool on)
    {
        if (_equipped == on) return;
        _equipped = on;
        if (!on) _byRight = false;
        float a = Aim();
        KunaiHand.Show(Player, on, a);
        HeldBody(on);
        if (Stick) FireStick.Arm(on);
        // 構えたかは全員へ配る (本編のキルを止めるのはホスト・手とクナイは全員の画面に出す)
        SendPose(a, false);
    }

    // 構えと狙いを配る。force でなければ、構えが変わった時か狙いが動いた時だけ、間を空けて送る
    // (受け取りの上限を超えると捨てられるので、最後の値は間が空いてから必ず送り直す)
    private void SendPose(float angle, bool force)
    {
        float now = Time.time;
        if (!force)
        {
            if (_equipped != _sentOn) { if (now - _poseAt < PoseGap) return; }
            else
            {
                if (!_equipped || now - _poseAt < AimGap) return;
                float d = angle - _sentAngle;
                d -= 360f * System.MathF.Round(d / 360f);
                if (d < AimSendDeg && d > -AimSendDeg) return;
            }
        }
        _sentOn = _equipped;
        _sentAngle = angle;
        _poseAt = now;
        PoseCall.Send((_equipped, angle));
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost) _equippedHost = _equipped;
    }

    private static readonly RemoteCall<(bool on, float angle)> PoseCall = new("Kunoichi.Pose", Route.Anyone,
        (w, m) => { w.Write(m.on); w.Write(m.angle); }, r => (r.ReadBoolean(), r.ReadSingle()),
        (sender, m) =>
        {
            if (RoleState.Of(sender) is not Kunoichi k) return;
            k._equippedHost = m.on;
            if (m.on && k._shownOn) KunaiHand.Aim(sender, m.angle);
            else { KunaiHand.Show(sender, m.on, m.angle); k.HeldBody(m.on); }
            k._shownOn = m.on;
        }, perSecond: 12);

    // ほかの端末: 届いた構え (構え始めは狙いへすぐ向ける・続きはなめらかに寄せる)
    private bool _shownOn;

    // スマホは射撃スティックで狙う (確かめ用に PC でも出せる)
    private static bool Stick => System.OperatingSystem.IsAndroid() || FireStick.ForceShow;

    // 確かめ用: 投げる向きを固定する (度・0 = 右)。null = マウス / 歩いた向き
    internal static float? TestAngle;

    private float Aim()
    {
        if (TestAngle is float fixedAngle) return fixedAngle;
        var cam = Camera.main;
        Vector2 me = Player.GetTruePosition();
        if (!cam || Stick)
        {
            // スティックを倒していればその向き (離した瞬間も)・触れていなければ最後に歩いた向き
            var f = _stickAimed ? _stickDir : ShotKit.Facing;
            return FxMath.Atan2(f.y, f.x) * (180f / System.MathF.PI);
        }
        Vector2 mouse = cam.ScreenToWorldPoint(Input.mousePosition);
        return Mathf.Atan2(mouse.y - me.y, mouse.x - me.x) * Mathf.Rad2Deg;
    }

    // 投げる所のすぐ先が壁なら投げられない
    private static bool Blocked(Vector2 from, float angle)
    {
        Vector2 dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
        return PhysicsHelpers.AnythingBetween(from, from + dir * 0.6f, Constants.ShipOnlyMask, false);
    }

    private static readonly RemoteCall<(float x, float y, float a, byte seq)> ThrowCall = new("Kunoichi.Throw", Route.Anyone,
        (w, m) => { w.Write(m.x); w.Write(m.y); w.Write(m.a); w.Write(m.seq); }, r => (r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadByte()),
        (sender, m) => { if (RoleState.Of(sender) is Kunoichi k) Fly(k, new Vector2(m.x, m.y), m.a, false, m.seq, KunaiHand.Throw(sender, m.a, ThrowCooldown)); }, perSecond: 12);

    // 全員の端末で飛ばす。当たりの判定は投げた本人の端末 (頑丈な役職への当たりだけはその人の端末・Kunoichi.Body.cs)。
    // 当たりは足元の高さの線で測り、絵は手の高さを飛ぶ。手を離れた所 (hand) から、少し飛ぶ間に狙いの線の上へ寄る
    private static void Fly(Kunoichi k, Vector2 from, float angle, bool judge, byte seq, Vector2 hand)
    {
        var sr = FloorPropArt.Place(k.Lifespan, from, "kunai");
        sr.transform.rotation = Quaternion.Euler(0f, 0f, angle);
        sr.transform.localScale = FxMath.V3(SizeScale, SizeScale, 1f);
        k.AddKunai(seq, sr, judge);
        Vector2 dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
        float speed = KunaiSpeed * 4f, gone = 0f, lift = KunaiHand.FlyHeight;
        float offX = hand.x - from.x, offY = hand.y - from.y - lift; // 手を離れた所と、狙いの線の上の手の高さとの差
        Vector2 at = from;
        sr.transform.position = FxMath.V3(hand.x, hand.y, from.y / 1000f - 0.5f);
        UchuKit.Every(() =>
        {
            if (!sr) { k.DropKunai(seq); return false; }
            if (MeetingHud.Instance || !ShipStatus.Instance) { k.EndKunai(seq); return false; }
            float step = speed * Time.fixedDeltaTime;
            Vector2 next = at + dir * step;
            if (WallStops && PhysicsHelpers.AnythingBetween(at, next, Constants.ShipOnlyMask, false)) { k.EndKunai(seq); return false; }
            at = next;
            gone += step;
            float off = gone < SettleLen ? 1f - gone / SettleLen : 0f;
            sr.transform.position = FxMath.V3(at.x + offX * off, at.y + lift + offY * off, at.y / 1000f - 0.5f);
            if (gone >= MaxTravel) { k.EndKunai(seq); return false; }
            if (!judge && !AnyToughHere()) return true;
            foreach (var p in PlayerControl.AllPlayerControls)
            {
                if (!PortKit.Alive(p) || p.PlayerId == k.PlayerId || p.inVent) continue;
                if (!FriendlyFire && PortKit.IsImpostor(p)) continue;
                if (Cloak.IsOn(p.PlayerId) || UchuKit.Hidden.IsHiddenFromMe(p.PlayerId)) continue; // 透明な人には当たらない (原作 IsInvisible)
                bool tough = Duel.Tough(p);
                if (tough ? !DecidesFor(p) : !judge) continue;
                Vector2 d = p.GetTruePosition() - at;
                float along = Mathf.Abs(d.x * dir.x + d.y * dir.y);
                float side = Mathf.Abs(d.x * dir.y - d.y * dir.x);
                if (along > HitLength / 2f || side > HitRadius) continue;
                if (tough)
                {
                    // 刀で斬る・受け流す・踏み込みで抜けるは弾と同じ受け方 (気も同じだけ使う)
                    var fate = ToughCatch(p, dir);
                    if (fate == Neutral.Samurai.BulletFate.Pass) continue;
                    if (fate == Neutral.Samurai.BulletFate.Hit) ToughHitCall.Send((k.PlayerId, p.PlayerId));
                    else Spark(at.x, at.y, new Strike(StrikeElement.Slash, 1f, 1, at.x, at.y, -dir.x, -dir.y, p.PlayerId));
                    GoneCall.Send((k.PlayerId, seq));
                }
                else HitCall.Send(p.PlayerId);
                k.EndKunai(seq);
                return false;
            }
            return true;
        });
    }

    private static readonly RemoteCall<byte> HitCall = new("Kunoichi.Hit", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Kunoichi k && PortKit.Alive(sender)) k.HostHit(id); }, perSecond: 12);

    private void HostHit(byte id)
    {
        var t = PortKit.ById(id);
        if (!PortKit.Alive(t) || MeetingHud.Instance) return;
        if (Duel.Tough(t))
        {
            // 頑丈な役職は 1 本ごとに体力が減り、普通の人が倒れる本数で倒れる
            Duel.HostSmash(Player, t, (Duel.MaxHp + HitsToKill - 1) / HitsToKill);
            return;
        }
        _hits.TryGetValue(id, out int n);
        n++;
        bool kill = n >= HitsToKill;
        if (kill) n = 0;
        HitsCall.Send((PlayerId, id, (byte)n, kill));
        OnHits(PlayerId, id, (byte)n, kill);
        if (kill)
        {
            UchuKit.HostAbilityKill(Player, t, PortKit.Harm.Slash);
        }
    }

    private static readonly RemoteCall<(byte k, byte t, byte n, bool kill)> HitsCall = new("Kunoichi.Hits", Route.HostToAll,
        (w, m) => { w.Write(m.k); w.Write(m.t); w.Write(m.n); w.Write(m.kill); }, r => (r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadBoolean()),
        (_, m) => OnHits(m.k, m.t, m.n, m.kill));

    private static void OnHits(byte kid, byte tid, byte n, bool kill)
    {
        if (RoleState.Of(kid) is not Kunoichi k) return;
        k._hits[tid] = n;
        if (!kill) return;
        RangedKillSnapPatch.Mark(kid, 2f); // 遠くから倒すので、体を相手の所へ動かさない
        if (KunaiOnBody)
        {
            var t = PortKit.ById(tid);
            if (t && k.Player)
            {
                // 飛んできた向き = 投げた人から倒れた人へ (全員の端末で同じに出せるので向きは送らない)
                Vector2 d = t.GetTruePosition() - k.Player.GetTruePosition();
                float angle = d.sqrMagnitude > 0.0001f ? Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg : 0f;
                PortKit.After(0.3f, () => { if (t) StickInBody(t, angle, StickTries); });
            }
        }
    }

    // 死体に残ったクナイ (全員の端末で)。会議で消える
    private static readonly System.Collections.Generic.List<SpriteRenderer> BodyKunai = new();

    // 倒れ終わった死体の絵の真ん中 (絵の原点から・左右が返ると x も返る)。倒れる途中の絵は範囲が違うので、最初はこの値で見積もる
    private const float BodyMidX = 0.085f, BodyMidY = -0.1f;
    private const float BodySettled = 1f; // 刺してから死体が倒れ終わるまで (秒)。この後に絵の真ん中へ合わせ直す

    private const int StickTries = 5;          // 死体がまだ無い時に探し直す回数 (0.2 秒ごと)
    private const float QuiverTime = 0.35f;    // 刺さった衝撃で柄が震える秒
    private const float QuiverDeg = 9f, QuiverHz = 15f; // 震えの振れ幅 (度)・速さ

    // 死体の絵の真ん中へ、飛んできた向きのまま刃を沈める。刺さった絵は死体の上にだけ出す
    // (死体が見つからなければ出さない: 体の無い所に出すと、刃の沈んだ絵が欠けた絵に見える)
    private static void StickInBody(PlayerControl t, float angle, int tries)
    {
        DeadBody found = null;
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
            if (b && b.ParentId == t.PlayerId && b.bodyRenderers != null && b.bodyRenderers.Length > 0 && b.bodyRenderers[0]) { found = b; break; }
        if (!found)
        {
            if (tries > 0) PortKit.After(0.2f, () => { if (t) StickInBody(t, angle, tries - 1); });
            return;
        }
        var body = found.bodyRenderers[0];
        var p = body.transform.position;
        // 倒れた姿に差し替えた死体は絵が止まっているので、その場で絵の真ん中に刺す
        bool still = Fx.FallenCorpse.Dressed(found);
        var bc = still ? body.bounds.center : p;
        var at = still ? FxMath.V3(bc.x, bc.y, p.z - FloorPropArt.StuckGap)
            : FxMath.V3(p.x + (body.flipX ? BodyMidX : -BodyMidX), p.y + BodyMidY, p.z - FloorPropArt.StuckGap);
        var sr = FloorPropArt.Stuck(RoleState.Match, at, angle, out float turn);
        BodyKunai.Add(sr);
        // 死体の子にする: 死体が運ばれれば一緒に動き、食べられる・時が戻るなどで消えれば一緒に消える
        sr.transform.SetParent(found.transform, true);
        Quiver(sr.transform, turn);
        PortKit.After(BodySettled, () =>
        {
            if (!sr || !body) return;
            var c = body.bounds.center;
            sr.transform.position = FxMath.V3(c.x, c.y, sr.transform.position.z);
        });
    }

    // 刺さった衝撃で柄が細かく震えて収まる (刺さった点を軸に回す)
    private static void Quiver(Transform tr, float turn)
    {
        float t0 = Time.time;
        UchuKit.Every(() =>
        {
            if (!tr) return false;
            float e = Time.time - t0;
            bool done = e >= QuiverTime;
            float k = done ? 0f : 1f - e / QuiverTime;
            tr.rotation = Quaternion.Euler(0f, 0f, turn + QuiverDeg * k * k * FxMath.Sin(e * QuiverHz * 2f * System.MathF.PI));
            return !done;
        });
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (_equipped) UchuKit.HideKillButton();
        var p = Player;
        if (!PortKit.Alive(p)) { SetEquip(false); SendPose(_sentAngle, false); return; }
        ShotKit.Track();
        if (MeetingHud.Instance) { _rightPrev = true; _rightClicked = _leftClicked = false; } // 右を押したまま会議が終わっても、押し始めと取らない
        else if (Stick) ReadStick();
        else ReadMouse();
        if (_equipped)
        {
            // 握ったクナイが狙いへ向く (壁で投げられない時は自分の画面でだけ赤)
            float a = Aim();
            Vector2 at = p.GetTruePosition();
            KunaiHand.Aim(p, a, Blocked(at, a));
            // 狙いの線: PC は構えている間ずっと・スマホはスティックを倒している間 (長さは数 tick に 1 回測る)
            bool line = !Stick || _stickTilted;
            if (!line) KunaiHand.AimLine(p, 0f);
            else if (--_lineFrame <= 0)
            {
                _lineFrame = 4;
                float r = a * (System.MathF.PI / 180f);
                var dir = FxMath.V2(FxMath.Cos(r), FxMath.Sin(r));
                KunaiHand.AimLine(p, WallStops ? ShotKit.WallDistance(at, dir, LineMax, Constants.ShipOnlyMask) : LineMax);
            }
            SendPose(a, false);
        }
        else SendPose(_sentAngle, false);
        _throw?.Button?.SetUsable(_equipped && (!_hidden || ThrowWhileHidden));

        // 止まっていると透明になれる
        Vector2 pos = p.GetTruePosition();
        bool moved = Vector2.Distance(pos, _lastPos) > 0.01f;
        _lastPos = pos;
        // 煙の中では動いても透明のまま (体が煙を押し分けるので居場所は煙の形に出る)
        if (MeetingHud.Instance || (moved && !(_hidden && Terrain.SmokeBodies.InSmoke(pos))))
        {
            _still = 0f;
            if (_hidden) Hide(false);
        }
        else if (moved) _still = 0f;
        else _still += Time.fixedDeltaTime;
        if (!CanHide) return;
        _hide?.Button?.SetUsable(_hidden || _still >= HideWait);
        // 既定は決めた秒だけ止まっていると自動で透明になる・設定でボタンにもできる
        if (!HideButton && !_hidden && _still >= HideWait) Hide(true);
    }

    // PC: 右クリックを押すと構え、離すとしまう。構えている間の左クリックで投げる。押し始めがボタン・パネルの上なら何もしない
    private void ReadMouse()
    {
        bool right = Input.GetMouseButton(1), left = Input.GetMouseButton(0);
        bool rightDown = right && !_rightPrev || _rightClicked, leftDown = left && !_leftPrev || _leftClicked;
        _rightPrev = right;
        _leftPrev = left;
        _rightClicked = _leftClicked = false;
        if (_byRight && !right) SetEquip(false);
        else if (rightDown && !_equipped && Player.CanMove && !Player.inVent && !Menu.Emotes.Covered() && !FireStick.OverUi())
        {
            SetEquip(true);
            _byRight = true;
        }
        if (leftDown && _equipped && !FireStick.OverUi() && !Minigame.Instance) _throw?.Button?.Press();
    }

    // 毎フレーム (Plugin.Update): 押し始めを覚えておく (tick の間に押して離した短いクリックも落とさない)
    private static bool _leftClicked, _rightClicked;
    internal static void LocalTick()
    {
        if (RoleState.Local is not Kunoichi || Stick) return;
        if (Input.GetMouseButtonDown(0)) _leftClicked = true;
        if (Input.GetMouseButtonDown(1)) _rightClicked = true;
    }

    // スマホ: 構えている間、射撃スティックを倒した向きへ狙い (狙いの線が出る)、指を離すとその向きへ投げる。
    // 中心へ戻してから離せば投げない。PC の「右で狙って左で投げる」と同じだけ狙ってから投げられる
    private void ReadStick()
    {
        bool touching = _equipped && FireStick.Touching;
        if (touching)
        {
            _stickTilted = FireStick.Held;
            if (_stickTilted) { _stickDir = FireStick.Dir; _stickAimed = true; }
        }
        if (_stickPrev && !touching && _stickTilted && _equipped) _throw?.Button?.Press();
        if (!touching) _stickAimed = _stickTilted = false;
        _stickPrev = touching;
    }

    private void Hide(bool on)
    {
        _hidden = on;
        Cloak.Self(on ? 9999f : 0f);
        _hide?.Button?.SetLabel(new Text(on ? "Kunoichi.Unhide" : "Kunoichi.Hide").ToString());
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (ResetHitsOnMeeting) _hits.Clear();
        foreach (var r in BodyKunai) if (r) Object.Destroy(r.gameObject); // 死体に残ったクナイは会議で消える
        BodyKunai.Clear();
        // 会議では持ち主の端末もクナイをしまうので、ホストの控えも同じく戻す (電文が届かなくても食い違わない)
        _equippedHost = false;
        if (!IsLocal) return;
        SetEquip(false);
        _hidden = false;
        _hide?.Button?.SetLabel(new Text("Kunoichi.Hide").ToString());
        _still = 0f;
        _rightPrev = true;
        SendPose(_sentAngle, true);
    }

    // クノイチにだけ: 倒せる人の名前の横に、あと何回当てれば倒せるか
    public override string NameMark(PlayerControl seen)
    {
        if (seen.PlayerId == PlayerId || !PortKit.Alive(seen) || (!FriendlyFire && PortKit.IsImpostor(seen))) return null;
        _hits.TryGetValue(seen.PlayerId, out int n);
        return $"<color=#FF1919>[{HitsToKill - n}]</color>";
    }

    // ホスト: クナイを持っている間は本編のキルを通さない
    [HostOnly]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (e.Canceled || !e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        if (_equippedHost && UchuKit.AbilityKill != PlayerId) e.Cancel("kunoichi holds kunai");
    }

    // ホストの端末で: この人がクナイを持っているか (持ち主の端末から届いた値)
    private bool _equippedHost;
}
