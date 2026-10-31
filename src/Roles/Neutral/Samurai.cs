using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// サムライ (第三陣営): なぞり (PC = 左ドラッグ・スマホ = 画面の右半分) で刀を振って斬る。納めている時のなぞりは居合 (抜きながら斬る)。
// 操作と「気」は Samurai.Controls.cs。当たった相手とは立ち合い (Duel) になり、斬るたびに体力が減って 0 で倒れる。
// 始めは守の道 = インポスターを PathKills 人斬って最後まで生きていれば、勝った陣営と一緒に勝つ。
// インポスター以外を FallKills 人斬ると修羅の道へ堕ち、以後は自分以外の生存者が 1 人以下になった時に単独で勝つ。
// 抜いた刀は全員に見える。一振りはホストが当たりを決め、斬った数と堕ちたかもホストが数えて本人にだけ知らせる (全員へ知らせるかは設定)
public sealed partial class Samurai : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#4A8CFF";
    public override Text Name => new("Samurai.Name");
    public override string Reading => "さむらい";
    public override Epithet Epithet => Epithet.Wayfinder;
    public override Text Blurb => new("Samurai.Blurb");
    public override HelpSection[] HelpSections => new HelpSection[]
    {
        new("Samurai.Draw", "katana_draw"), new("Samurai.Slash", "katana_slash"), new("Samurai.Ki"), new("Samurai.Paths"),
    };

    public static readonly IntOpt PathKills = new("Samurai.PathKills", 1, 1, 3);
    public static readonly IntOpt FallKills = new("Samurai.FallKills", 2, 1, 5);
    public static readonly BoolOpt AnnounceFall = new("Samurai.AnnounceFall", false);
    public static readonly FloatOpt SlashRange = new("Samurai.SlashRange", 2f, 1f, 3.5f, 0.25f);
    // 斬った扉と空間のずれ (斬り跡) の戻り方: 次の会議 / 秒数 / ずっと
    public static readonly ChoiceOpt CutRestore = new("Samurai.CutRestore", 1,
        new Text("Samurai.CutRestoreMeeting"), new Text("Samurai.CutRestoreSeconds"), new Text("Samurai.CutRestoreNever"));
    public static readonly FloatOpt CutRestoreTime = new("Samurai.CutRestoreTime", 30f, 5f, 120f, 5f) { When = () => CutRestore.Value == 1 };

    internal const float DrawTime = 0.6f;         // 抜刀/納刀の動作の長さ
    private const float RangeSlack = 0.3f;        // 振っている間に相手が動いた分の余裕
    private const float ArcPad = 15f;             // 掃引角の両端に足す当たりの余裕 (度)
    private const float DoorReachSlack = 0.25f;   // 扉の厚みの分 (刀の切っ先より少し先の扉まで斬れる)
    private const float Touching = 0.3f;          // これより近い人は向きによらず当たる
    private const int WinCheckMs = 500;
    private const float MinStrokeSeconds = 0.12f;
    // 突き: 斬りより遠くまで細く届く (斬れる距離 + ThrustExtra・向きの左右 12 度・体の幅 ThrustWidth までは向きがずれても当たる)
    internal const float ThrustExtra = 0.8f;
    private const float ThrustWidth = 0.3f, ThrustTan = 0.2126f; // tan 12°
    private const int ThrustDamage = 30;
    internal const ushort ThrustMs = 120;
    internal const byte ThrustFlag = 0x80; // 一振りの段の上の bit = 突き

    // 一振りの威力 = 角速度 (度/秒) の帯。居合は溜めた分を足す
    private static int Damage(float degPerSec) => degPerSec >= 1000f ? 40 : degPerSec >= 600f ? 30 : 20;
    private const int IaiCharged = 30; // 溜め切った居合に足す分 (溜め無しの居合は普通の一振りと同じ)
    private const float FullCharge = 0.93f; // ここまで溜めた居合を溜め切りとみなす (不意打ちなら一撃)

    // 修羅に堕ちたらキル役として数える (インポスターがいなくなっても生存者が多ければクルーの勝ちにしない)。
    // ホストの判定で読むので、ホストの値で決まる
    public override bool IsKiller => _fallen;
    public override bool CanKill => false; // 本編のキルボタンは使わない (斬りは自前の依頼)

    // 抜いているか (全員の端末で持つ)・抜いた時刻 (動作の間は振れない)
    internal bool Drawn { get; private set; }
    private float _drawnAt = -10f;
    private float _sheathedAt = -10f; // 納めた時刻 (居合の溜めの上限をホストが見る)
    // ホストが数える値。本人の端末には知らせで写す。時戻しで戻る
    private readonly Tracked<int> _impostorKills = new(0);
    private readonly Tracked<int> _otherKills = new(0);
    private readonly Tracked<bool> _fallen = new(false);
    private readonly Tracked<bool> _pathDone = new(false);

    private SamuraiBlade _blade;
    private long _nextWinCheck;
    private int _sent, _hostStrokes, _hostRefused;
    private string _lastRefusal = "-";

    internal bool Fallen => _fallen;
    internal int ImpostorKills => _impostorKills.Value;
    internal int OtherKills => _otherKills.Value;
    internal bool PathDone => _pathDone;

    public override void OnAssigned()
    {
        Duel.Use();
        CutDoors.Use();
        DuelTongue.Use();
        SpaceRifts.Use();
        ResetClash();
        _blade = new SamuraiBlade(this);
        Lifespan.OnRelease(_blade.Dispose);
        if (IsLocal) SamuraiBlade.LocalFollow = _blade;
        HookControls();
    }

    // ---- 抜刀 / 納刀 ----

    // iai = 居合 (抜く動作を飛ばし、そのまま振れる)
    private void SetDrawn(bool drawn, bool iai = false)
    {
        if (Drawn == drawn) return;
        Drawn = drawn;
        // 居合は抜く動作を済ませた扱い (丸めで「まだ抜いている途中」に見えないよう少し前へ)
        _drawnAt = iai ? Time.time - DrawTime - 0.05f : Time.time;
        if (!drawn) _sheathedAt = Time.time;
        // 抜く時に出ていたエモートは打ち切る (抜いている間は出せない)
        if (drawn) Fx.EmoteArt.Stop(PlayerId);
        _blade.SetDrawn(drawn, iai);
    }

    // 倒れて刀を片付けた (全員の端末で)。刀は鞘に戻した扱いにして、生き返った後の一振りを居合で抜き直す (抜き直しで刀を作り直す)
    internal void OnBladeLost()
    {
        Drawn = false;
        _iaiStroke = false;
        _sheathedAt = Time.time;
    }

    // 納めた全員の端末で (抜くのは居合の一振りと一緒に届く)。納めた人は送った時に手元で済ませている
    private static readonly RemoteCall<bool> DrawCall = new("Samurai.Draw", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadBoolean(),
        (sender, v) => (RoleState.Of(sender) as Samurai)?.SetDrawn(v), perSecond: 6);

    // 会議が終わると刀は鞘に戻り、気も満ちている (全員の端末で同じに戻す)
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        SamuraiSound.Muted = true;
        EndLock();
        SetDrawn(false);
        SamuraiSound.Muted = false;
        _ki = _hostKi = KiMax;
        _spentUntil = -10f;
        _blade.SetSpent(false);
    }

    // ---- 斬る (なぞりの一振り) ----

    // 一振り = 始めの角度・符号付きの掃引角 (度・整数)・かかった ms・居合 (0 = 違う・1〜255 = 居合で溜め (v-1)/254)・居合の段 (1〜4・突きは ThrustFlag を足す)
    private readonly struct Stroke
    {
        public readonly short A0, Sweep;
        public readonly ushort Ms;
        public readonly byte Iai, Tier;

        public Stroke(short a0, short sweep, ushort ms, byte iai, byte tier)
        {
            A0 = a0;
            Sweep = sweep;
            Ms = ms;
            Iai = iai;
            Tier = tier;
        }
    }

    // 居合の段: 抜いた時に残っていた気を 4 つに区切る (気が多いほど派手)。
    // 溜め無しの居合は普通の一振りなので段 1・段 4 は溜め切った時だけ (足りなければ段 3)
    internal static int IaiTier(float ki, float charge)
    {
        if (charge <= 0.02f) return 1;
        int t = ki >= 75f ? 4 : ki >= 50f ? 3 : ki >= 25f ? 2 : 1;
        return t == 4 && charge < FullCharge ? 3 : t;
    }

    // 自分の端末: なぞりで一振りが出来た。軌跡は手元で描いているので、ホストへ当たりを頼むだけ
    private void OnLocalStroke(float a0, float sweep, float seconds)
    {
        if (!Drawn || Spent) return;
        bool iai = _iaiStroke;
        _iaiStroke = false;
        UseKi(LocalStrokeCost(!iai));
        _sent++;
        byte charge = iai ? (byte)(1 + (int)MathF.Round(_iaiCharge * 254f)) : (byte)0;
        _sentIai = charge;
        StrokeCall.Send(new Stroke((short)MathF.Round(a0), (short)MathF.Round(sweep), (ushort)Math.Clamp((int)(seconds * 1000f), 1, 60000), charge, (byte)(iai ? _iaiTier : 1)));
    }

    // 自分の端末: 外へ押し出して突いた。納めていれば抜きながら突く (居合・溜めた分だけ強い・段は 1)。
    // 突いた向きへ少しだけ前へ出る (気は突きの分だけ)
    private void OnLocalThrust(float angle)
    {
        if (Spent) return;
        bool iai = _iaiStroke;
        _iaiStroke = false;
        if (!Drawn)
        {
            iai = true;
            _iaiCharge = ChargeOf(Time.time - SwipeInput.PressedAt);
            SetDrawn(true, iai: true);
            _blade.ArmIai(_iaiCharge, 1);
        }
        UseKi(LocalStrokeCost(false));
        _sent++;
        _blade.Thrust(angle, _fallen);
        float ar = angle * FxMath.Deg2Rad;
        Lunge(FxMath.Cos(ar), FxMath.Sin(ar));
        byte charge = iai ? (byte)(1 + (int)MathF.Round(_iaiCharge * 254f)) : (byte)0;
        _sentIai = charge;
        StrokeCall.Send(new Stroke((short)MathF.Round(angle), 0, ThrustMs, charge, (byte)(1 | ThrustFlag)));
    }

    // 確かめ用: 外へ突いた扱いにする (hold 秒押したまま溜めてから)
    internal bool ThrustForTest(float angle, float hold = 0f)
    {
        if (!SwipeInput.Armed) return false;
        SwipeInput.SynthThrust(angle, hold);
        return true;
    }

    // 確かめ用: なぞりを作り物で流す (a0 → a1 度を秒で・hold 秒押したまま溜めてから)
    internal bool SwipeForTest(float a0, float a1, float seconds, float hold = 0f)
    {
        if (!SwipeInput.Armed) return false;
        SwipeInput.Synth(a0, a1, seconds, hold);
        return true;
    }

    // 確かめ用: 他人から届いた一振りと同じ再生を自分の刀で流す (当たりは無し)
    internal void ShowForTest(float a0, float sweep, float seconds, float iaiPow = -1f, int tier = 0)
    {
        if (iaiPow >= 0f) _blade.ArmIai(iaiPow, tier > 0 ? tier : IaiTier(KiMax, iaiPow));
        _blade.Replay(a0, sweep, seconds, _fallen);
    }

    private static readonly RemoteCall<Stroke> StrokeCall = new("Samurai.Stroke", Route.ToHost,
        // Hazel に short の書き手は無い (int の 4 バイトになる)。読み手の ReadInt16 と合わせて 2 バイトで書く
        (w, s) => { w.Write(unchecked((ushort)s.A0)); w.Write(unchecked((ushort)s.Sweep)); w.Write(s.Ms); w.Write(s.Iai); w.Write(s.Tier); },
        r => new Stroke(r.ReadInt16(), r.ReadInt16(), r.ReadUInt16(), r.ReadByte(), r.ReadByte()),
        (sender, s) => (RoleState.Of(sender) as Samurai)?.HostStroke(s), perSecond: 8);

    // 振った姿 (ホスト → 全員)。値 = 振った人 | 修羅の色 << 7 | 始めの角度 + 180 << 8 | 掃引角 + 512 << 17 (10 bit) | 居合 << 27 | 居合の溜め (0〜15) << 28・ms と居合の段は別
    private static readonly RemoteCall<(int head, ushort ms, byte tier)> ShowCall = new("Samurai.Show", Route.HostToAll,
        (w, m) => { w.Write(m.head); w.Write(m.ms); w.Write(m.tier); }, r => (r.ReadInt32(), r.ReadUInt16(), r.ReadByte()),
        (_, m) => (RoleState.Of((byte)(m.head & 0x7F)) as Samurai)?.ShowStroke(m.head, m.ms, m.tier));

    private void ShowStroke(int head, ushort ms, byte tier)
    {
        if (IsLocal) return;
        bool fallen = (head >> 7 & 1) != 0;
        bool thrust = (tier & ThrustFlag) != 0;
        // ホストは確かめた時に引いている
        if (!AmongUsClient.Instance.AmHost) _hostKi = HostKiNow() - SeenStrokeCost(!thrust && (head >> 27 & 1) == 0);
        tier &= 0x7F;
        float a0 = ((head >> 8) & 0x1FF) - 180f;
        float sweep = ((head >> 17) & 0x3FF) - 512f;
        if ((head >> 27 & 1) != 0)
        {
            SetDrawn(true, iai: true);
            _blade.ArmIai(((head >> 28) & 0xF) / 15f, tier);
        }
        if (thrust) _blade.Thrust(a0, fallen);
        else _blade.Replay(a0, sweep, ms / 1000f, fallen);
    }

    // ホスト: 一振りを確かめ、掃引した扇の中の生きている人に当てる (壁越しは当たらない)
    private void HostStroke(Stroke s)
    {
        _hostIai = s.Iai;
        string why = HostRefuse(s, out bool iai);
        if (why != null)
        {
            _hostRefused++;
            _lastRefusal = why;
            Plugin.Logger.LogWarning($"Samurai stroke refused {PlayerId}: {why}");
            // 居合を断っても本人の端末では抜いている。抜いたことだけは全員に揃える (当たりは無し)
            if (iai && !Drawn && why == "sheathing")
            {
                SetDrawn(true, iai: true);
                int h = PlayerId & 0x7F | (_fallen ? 1 : 0) << 7 | (Math.Clamp((int)s.A0, -180, 180) + 180) << 8 | 512 << 17 | 1 << 27;
                ShowCall.Send((h, (ushort)100, (byte)1));
            }
            return;
        }
        _hostStrokes++;
        bool thrust = (s.Tier & ThrustFlag) != 0;
        float charge = 0f;
        if (iai)
        {
            // 溜めは客の申告。納めてから経った時間で溜められた分までに丸める
            charge = FxMath.Min(s.Iai > 0 ? (s.Iai - 1) / 254f : 0f, ChargeOf(Time.time - _sheathedAt + 0.3f));
            SetDrawn(true, iai: true);
        }
        // 段も客の申告。ホストの見積もりの気 (余裕込み) で出せる段までに丸める
        byte tier = (byte)(iai && !thrust ? Math.Min(Math.Clamp(s.Tier & 0x7F, 1, 4), IaiTier(DevFree(Player) ? KiMax : HostKiNow() + KiSlack, charge)) : 1);
        float cost = SeenStrokeCost(!thrust && !iai);
        if (!DevFree(Player)) _hostKi -= cost;
        float a0 = Math.Clamp((int)s.A0, -180, 180);
        float sweep = thrust ? 0f : Math.Clamp((int)s.Sweep, -(int)SwipeInput.MaxSweep, (int)SwipeInput.MaxSweep);
        // 速さ (= 威力) は客の申告なので、人の手で出せない短さは丸める
        float secs = thrust ? ThrustMs / 1000f : Math.Clamp(s.Ms / 1000f, MinStrokeSeconds, SwipeInput.MaxSeconds);
        if (thrust) tier |= ThrustFlag;
        int head = PlayerId & 0x7F | (_fallen ? 1 : 0) << 7 | ((int)a0 + 180) << 8 | ((int)sweep + 512) << 17 | (iai ? 1 : 0) << 27 | (int)MathF.Round(charge * 15f) << 28;
        ShowCall.Send((head, (ushort)(secs * 1000f), tier));
        if (!IsLocal) ShowStroke(head, (ushort)(secs * 1000f), tier);
        // 道を決めた後の溜め切った段 4 の居合は必殺技 (範囲に人がいれば普通の当たりと斬線は出さない・Samurai.Ult.cs)
        if (iai && !thrust && tier == 4 && charge >= FullCharge && HostTryUlt()) return;
        // 段 3〜4 の居合は斬線が空間のずれとして床に残る (向き = 振った刀の真ん中)
        if (iai && !thrust && (tier & 0x7F) >= 3) SpaceRifts.HostCut(Player.GetTruePosition(), a0 + sweep * 0.5f, tier & 0x7F, PlayerId);
        int dmg = thrust ? ThrustDamage : Damage(FxMath.Abs(sweep) / secs);
        if (iai) dmg += (int)(IaiCharged * charge);
        _lastDamage = dmg;
        _hostStrokeIai = iai;
        _hostStrokeFull = iai && charge >= FullCharge;
        _hostStrokeSmash = iai && !thrust && (tier & 0x7F) >= SmashTier;
        _hostStrokeSecs = secs;
        HostHit(a0, sweep, dmg, thrust);
    }

    private int _lastDamage;
    private int _missBusy, _missFar, _missArc, _missWall, _missDodge;
    private byte _sentIai, _hostIai;

    // iai = 納めている所からの一振り (居合)。ホスト自身がサムライなら一振りの始めに手元で抜いているので、申告で見る
    private string HostRefuse(in Stroke s, out bool iai)
    {
        iai = !Drawn || IsLocal && s.Iai != 0;
        if (AmongUsClient.Instance.IsGameOver || !ShipStatus.Instance || MeetingHud.Instance || ExileController.Instance) return "not now";
        var p = Player;
        if (!p || p.Data == null || p.Data.IsDead || p.Data.Disconnected) return "user";
        if (p.inVent || p.inMovingPlat) return "user busy";
        if (_ultStart >= 0f) return "ult";
        if (DuelTumble.Busy(PlayerId)) return "tumble";
        if (Time.time < _hostStunUntil) return "guarded";
        if (Time.time - _drawnAt < DrawTime - 0.2f) return iai ? "sheathing" : "drawing";
        if (HostKiNow() < -KiSlack && !DevFree(p)) return "ki";
        return null;
    }

    // thrust = 突き (a0 の向きへ細く遠く・sweep は使わない)
    private void HostHit(float a0, float sweep, int damage, bool thrust = false)
    {
        var p = Player;
        Vector2 me = p.GetTruePosition();
        float reach = SlashRange + RangeSlack + (thrust ? ThrustExtra : 0f);
        float tr = a0 * FxMath.Deg2Rad, tc = FxMath.Cos(tr), tsn = FxMath.Sin(tr);
        float span = FxMath.Abs(sweep);
        // 自分へ伸びている舌を斬ったか、舌を出しかけたインポスターに受けられたら、その人には当てない
        byte spared = DuelTongue.HostStroke(this, me, a0, sweep, thrust, _hostStrokeSecs);
        var all = GameData.Instance.AllPlayers;
        // 先に当たる人を全員決めてから斬る (斬った後の本編の処理で一覧が変わる)
        Span<byte> hits = stackalloc byte[16];
        int n = 0, aimedBits = 0; // aimedBits = 刀の向きが相手へ向いていた当たり (近すぎて向きを問わず当たった物でない)
        for (int i = 0; i < all.Count && n < hits.Length; i++)
        {
            var info = all[i];
            if (info == null || info.Disconnected || info.IsDead || info.PlayerId == PlayerId || info.PlayerId == spared) continue;
            var pc = info.Object;
            if (!pc || pc.inVent || pc.inMovingPlat) { _missBusy++; continue; }
            Vector2 at = pc.GetTruePosition();
            float dx = at.x - me.x, dy = at.y - me.y, sq = dx * dx + dy * dy;
            if (sq > reach * reach) { _missFar++; continue; }
            bool aimed;
            if (thrust)
            {
                // 突いた向きの前で、向きのずれが 12° 以内か、線からの離れが体の幅以内
                float along = dx * tc + dy * tsn, side = FxMath.Abs(dx * tsn - dy * tc);
                aimed = along > 0f && (side <= ThrustWidth || side <= along * ThrustTan);
            }
            else
            {
                // 振った向きに沿って、始めの角度から相手の角度までの回り (0〜360)
                float ta = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;
                float d = (sweep >= 0f ? ta - a0 : a0 - ta) % 360f;
                if (d < 0f) d += 360f;
                aimed = d <= span + ArcPad || d >= 360f - ArcPad;
            }
            // これより近い人は向きによらず当たる
            if (!aimed && sq > Touching * Touching) { _missArc++; continue; }
            if (PhysicsHelpers.AnythingBetween(me, at, Constants.ShipAndObjectsMask, false)) { _missWall++; continue; }
            // 回避で飛び退いている間は当たらない
            if (DuelDodge.HostSafe(info.PlayerId)) { _missDodge++; continue; }
            if (aimed) aimedBits |= 1 << n;
            hits[n++] = info.PlayerId;
        }
        bool counted = false;
        for (int i = 0; i < n; i++)
        {
            var target = GameData.Instance.GetPlayerById(hits[i])?.Object;
            if (!target || target.Data == null || target.Data.IsDead) continue;
            // 立ち合いの外からの溜め切りの居合 (不意打ち) は誰でも一太刀で倒れる
            int dmg = _hostStrokeFull && !Duel.InDuel(target.PlayerId) ? Duel.MaxHp : damage;
            // サムライへの斬りは、相手の斬り (納めていれば居合) とぶつかるかを少し待ってから決める (Samurai.Clash.cs)
            if (RoleState.Of(target) is Samurai ts && HostClashOrHold(ts, dmg, (aimedBits >> i & 1) != 0)) continue;
            // 立ち合いの相手のインポスターへの斬りは、口で受けられるかを少し待ってから決める (Samurai.Tongue.cs)
            if (HostHoldForGuard(target, dmg, (aimedBits >> i & 1) != 0)) continue;
            counted |= HostApply(target, dmg, _hostStrokeSmash);
        }
        if (counted) HostUpdatePath();
        // 扉は刀の絵が届く所だけ (人の当たりは振っている間に動く分の余裕があるので扉より遠い)
        CutDoors.HostCut(me, a0, sweep, thrust ? SlashRange + ThrustExtra : SamuraiBlade.BladeReach + DoorReachSlack, thrust, PlayerId);
    }

    // 立ち合い (体力の削り合い) になる相手 = 刀を受けて戦える人。それ以外は一太刀で倒れる
    private static bool Fighter(PlayerControl p) =>
        p.Data.Role && p.Data.Role.IsImpostor || RoleState.Of(p) is Samurai or { CanKill: true } or { IsKiller: true };

    // ホスト: 斬りを当てる。倒したら斬った数を数えて true (道の更新は呼んだ側)
    private bool HostApply(PlayerControl target, int damage)
    {
        var p = Player;
        if (!p || !target || target.Data == null || target.Data.IsDead) return false;
        int left = Duel.HpOf(target.PlayerId);
        bool fighter = Fighter(target);
        int dmg = fighter ? damage : left;
        // 倒す一振りだけキルとして確かめる (盾などに止められたら体力 1 で残す)
        if (left - dmg <= 0 && BeforeMurderEvent.Ask(p, target) != null) dmg = left - 1;
        if (!Duel.HostDamage(PlayerId, target.PlayerId, dmg, start: fighter && dmg < left)) return false;
        bool impostor = target.Data.Role && target.Data.Role.IsImpostor;
        p.RpcMurderPlayer(target, true);
        Duel.HostLeave(target.PlayerId);
        if (impostor) _impostorKills.Value++;
        else _otherKills.Value++;
        return true;
    }

    public override void OnFixedUpdate()
    {
        _blade.Tick();
        if (IsLocal && Player)
        {
            TickKi(Player);
            TickControls();
        }
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        Duel.HostTick();
        HostClashTick();
        HostUltTick();
        long now = Environment.TickCount64;
        if (_fallen && now >= _nextWinCheck)
        {
            _nextWinCheck = now + WinCheckMs;
            HostCheckFallenWin();
        }
    }

    // ホスト: 斬った数から道を決め、本人へ知らせる (堕ちたのは設定で全員へ)。値 = 本人 | 変わった道 << 8 | 斬ったインポスターの数 << 16
    private void HostUpdatePath()
    {
        var s = PathState.Progress;
        if (!_fallen && _otherKills >= FallKills) s = PathState.Fallen;
        else if (!_fallen && !_pathDone && _impostorKills >= PathKills) s = PathState.PathDone;
        int v = PlayerId | (int)s << 8 | Math.Min(_impostorKills.Value, 255) << 16;
        ApplyPath(v);
        // 全員へ知らせる時は斬った数を載せない (使うのは本人とホストだけ)。ホスト自身がサムライなら手元だけ
        if (s == PathState.Fallen && AnnounceFall) PathCall.Send(v & 0xFFFF, -1);
        else if (!IsLocal && OwnerClient() >= 0) PathCall.Send(v, OwnerClient());
    }

    private int OwnerClient() => Player ? Player.OwnerId : -2;

    private enum PathState : byte { Progress = 0, PathDone = 1, Fallen = 2 }

    private static readonly RemoteCall<int> PathCall = new("Samurai.Path", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadInt32(),
        (_, v) => (RoleState.Of((byte)v) as Samurai)?.ApplyPath(v));

    // 道が変わった・進んだ (ホスト・本人の端末・堕ちたのを全員へ知らせる設定なら全員の端末)
    private void ApplyPath(int v)
    {
        var s = (PathState)(v >> 8 & 0xFF);
        if (IsLocal || AmongUsClient.Instance.AmHost) _impostorKills.Value = Math.Max(_impostorKills.Value, v >> 16 & 0xFF);
        if (s == PathState.Fallen) _fallen.Value = true;
        else if (s == PathState.PathDone) _pathDone.Value = true;
        _blade.SetFallen(Fallen && IsLocal);
        if (IsLocal) RoleDisplay.EnsureTaskHeader(Player);
        if (s == PathState.Progress) return;
        var hud = Vanilla.Hud;
        if (!hud || !hud.Notifier) return;
        if (IsLocal) hud.Notifier.AddDisconnectMessage(new Text(s == PathState.Fallen ? "Samurai.FellNotice" : "Samurai.PathNotice"));
        else if (s == PathState.Fallen && AnnounceFall) hud.Notifier.AddDisconnectMessage(new Text("Samurai.FellAnnounce"));
    }

    // ホスト: 修羅は自分以外の生存者が 1 人以下になったら単独で勝つ
    private void HostCheckFallenWin()
    {
        var p = Player;
        if (!p || p.Data == null || p.Data.IsDead || p.Data.Disconnected || !GameData.Instance) return;
        int others = 0;
        var all = GameData.Instance.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var info = all[i];
            if (info == null || info.Disconnected || info.IsDead || info.PlayerId == PlayerId) continue;
            if (++others > 1) return;
        }
        GameEnd.Win(this);
    }

    // 守の道: インポスターを決めた人数斬り、生きていれば勝った陣営と一緒に勝つ (修羅に堕ちていない時だけ)
    public override bool AlsoWins(GameResult result) => !_fallen && _pathDone && Player && !Player.Data.IsDead;

    public override void OnRewound()
    {
        _ultStart = -1f; // 進行中の必殺技は打ち切る (戻された人を後から倒さない)
        _blade.SetFallen(Fallen && IsLocal);
        if (IsLocal) RoleDisplay.EnsureTaskHeader(Player);
    }

    // 自分の画面のタスク欄の先頭: 今の道と進み
    public override Text Description => !IsLocal ? Blurb
        : Fallen ? new Text("Samurai.FallenDesc")
        : new Text("Samurai.PathDesc").With("n", _impostorKills.Value).With("max", PathKills.Value);

    // 確かめ用
    internal string Probe() =>
        $"samurai={PlayerId} drawn={Drawn} ki={_ki:0} spent={Spent} charge={Charge:0.00} hostKi={_hostKi:0} lastDmg={_lastDamage} iai[starts={_starts} sent={_sentIai} host={_hostIai}] miss[busy={_missBusy} far={_missFar} arc={_missArc} wall={_missWall} dodge={_missDodge}] imp={_impostorKills.Value} other={_otherKills.Value} fallen={Fallen} path={PathDone} killer={IsKiller} sent={_sent} hostStrokes={_hostStrokes} refused={_hostRefused}({_lastRefusal}) blade[{_blade?.Describe()}] screen[{SamuraiScreen.Describe()}] sound[{SamuraiSound.Describe()}] clash[{DescribeClash()} locked={Locked}] duel[{Duel.Describe()}] ult[{DescribeUlt()}]";
}

// 本編のキルの演出は、倒した人を倒された人の所へ瞬間移動させる (コルーチン CoPerformKill の中)。
// サムライの斬りと舌の一撃は間合いの外から当たるので、移された同じフレームのうちに元の場所へ戻す (全員の端末で同じに)
[HarmonyLib.HarmonyPatch]
internal static class SamuraiKillSnapPatch
{
    private static System.Reflection.PropertyInfo _source;
    private static bool _has;
    private static Vector3 _saved;

    public static System.Reflection.MethodBase TargetMethod()
    {
        var t = System.Linq.Enumerable.First(typeof(KillAnimation).GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            n => n.Name.Contains("CoPerformKill"));
        _source = HarmonyLib.AccessTools.Property(t, "source");
        if (_source == null) Plugin.Logger.LogWarning("Samurai: CoPerformKill has no source field (kill snap not undone)");
        return HarmonyLib.AccessTools.Method(t, "MoveNext");
    }

    public static void Prefix(object __instance)
    {
        _has = false;
        if (_source == null || RoleState.All.Count == 0) return;
        var src = _source.GetValue(__instance) as PlayerControl;
        if (!src || RoleState.Of(src) is not Samurai && !DuelTongue.JustKilled(src.PlayerId)) return;
        _saved = src.transform.position;
        _has = true;
    }

    public static void Postfix(object __instance)
    {
        if (!_has) return;
        _has = false;
        var src = _source.GetValue(__instance) as PlayerControl;
        if (!src || !src.NetTransform) return;
        Vector3 now = src.transform.position;
        float dx = now.x - _saved.x, dy = now.y - _saved.y;
        if (dx * dx + dy * dy > 0.0025f) src.NetTransform.SnapTo(FxMath.V2(_saved.x, _saved.y));
    }
}
