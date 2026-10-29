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
    public override Text Blurb => new("Samurai.Blurb");
    public override HelpSection[] HelpSections => new HelpSection[]
    {
        new("Samurai.Draw", "katana_draw"), new("Samurai.Slash", "katana_slash"), new("Samurai.Ki"), new("Samurai.Paths"),
    };

    public static readonly IntOpt PathKills = new("Samurai.PathKills", 1, 1, 3);
    public static readonly IntOpt FallKills = new("Samurai.FallKills", 2, 1, 5);
    public static readonly BoolOpt AnnounceFall = new("Samurai.AnnounceFall", false);
    public static readonly FloatOpt SlashRange = new("Samurai.SlashRange", 2f, 1f, 3.5f, 0.25f);

    internal const float DrawTime = 0.6f;         // 抜刀/納刀の動作の長さ
    private const float RangeSlack = 0.3f;        // 振っている間に相手が動いた分の余裕
    private const float ArcPad = 15f;             // 掃引角の両端に足す当たりの余裕 (度)
    private const float Touching = 0.3f;          // これより近い人は向きによらず当たる
    private const int WinCheckMs = 500;
    private const float MinStrokeSeconds = 0.12f;

    // 一振りの威力 = 角速度 (度/秒) の帯。居合は溜めた分を足す
    private static int Damage(float degPerSec) => degPerSec >= 1000f ? 40 : degPerSec >= 600f ? 30 : 20;
    private const int IaiCharged = 30; // 溜め切った居合に足す分 (溜め無しの居合は普通の一振りと同じ)

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

    // 納めた全員の端末で (抜くのは居合の一振りと一緒に届く)。納めた人は送った時に手元で済ませている
    private static readonly RemoteCall<bool> DrawCall = new("Samurai.Draw", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadBoolean(),
        (sender, v) => (RoleState.Of(sender) as Samurai)?.SetDrawn(v), perSecond: 6);

    // 会議が終わると刀は鞘に戻り、気も満ちている (全員の端末で同じに戻す)
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        EndLock();
        SetDrawn(false);
        _ki = _hostKi = KiMax;
        _spentUntil = -10f;
        _blade.SetSpent(false);
    }

    // ---- 斬る (なぞりの一振り) ----

    // 一振り = 始めの角度・符号付きの掃引角 (度・整数)・かかった ms・居合 (0 = 違う・1〜255 = 居合で溜め (v-1)/254)
    private readonly struct Stroke
    {
        public readonly short A0, Sweep;
        public readonly ushort Ms;
        public readonly byte Iai;

        public Stroke(short a0, short sweep, ushort ms, byte iai)
        {
            A0 = a0;
            Sweep = sweep;
            Ms = ms;
            Iai = iai;
        }
    }

    // 自分の端末: なぞりで一振りが出来た。軌跡は手元で描いているので、ホストへ当たりを頼むだけ
    private void OnLocalStroke(float a0, float sweep, float seconds)
    {
        if (!Drawn) return;
        bool iai = _iaiStroke;
        _iaiStroke = false;
        UseKi(StrokeCost);
        _sent++;
        byte charge = iai ? (byte)(1 + (int)MathF.Round(_iaiCharge * 254f)) : (byte)0;
        _sentIai = charge;
        StrokeCall.Send(new Stroke((short)MathF.Round(a0), (short)MathF.Round(sweep), (ushort)Math.Clamp((int)(seconds * 1000f), 1, 60000), charge));
    }

    // 確かめ用: なぞりを作り物で流す (a0 → a1 度を秒で・hold 秒押したまま溜めてから)
    internal bool SwipeForTest(float a0, float a1, float seconds, float hold = 0f)
    {
        if (!SwipeInput.Armed) return false;
        SwipeInput.Synth(a0, a1, seconds, hold);
        return true;
    }

    // 確かめ用: 他人から届いた一振りと同じ再生を自分の刀で流す (当たりは無し)
    internal void ShowForTest(float a0, float sweep, float seconds, float iaiPow = -1f)
    {
        if (iaiPow >= 0f) _blade.ArmIai(iaiPow);
        _blade.Replay(a0, sweep, seconds, _fallen);
    }

    private static readonly RemoteCall<Stroke> StrokeCall = new("Samurai.Stroke", Route.ToHost,
        // Hazel に short の書き手は無い (int の 4 バイトになる)。読み手の ReadInt16 と合わせて 2 バイトで書く
        (w, s) => { w.Write(unchecked((ushort)s.A0)); w.Write(unchecked((ushort)s.Sweep)); w.Write(s.Ms); w.Write(s.Iai); },
        r => new Stroke(r.ReadInt16(), r.ReadInt16(), r.ReadUInt16(), r.ReadByte()),
        (sender, s) => (RoleState.Of(sender) as Samurai)?.HostStroke(s), perSecond: 8);

    // 振った姿 (ホスト → 全員)。値 = 振った人 | 修羅の色 << 7 | 始めの角度 + 180 << 8 | 掃引角 + 512 << 17 (10 bit) | 居合 << 27 | 居合の溜め (0〜15) << 28・ms は別
    private static readonly RemoteCall<(int head, ushort ms)> ShowCall = new("Samurai.Show", Route.HostToAll,
        (w, m) => { w.Write(m.head); w.Write(m.ms); }, r => (r.ReadInt32(), r.ReadUInt16()),
        (_, m) => (RoleState.Of((byte)(m.head & 0x7F)) as Samurai)?.ShowStroke(m.head, m.ms));

    private void ShowStroke(int head, ushort ms)
    {
        if (IsLocal) return;
        bool fallen = (head >> 7 & 1) != 0;
        float a0 = ((head >> 8) & 0x1FF) - 180f;
        float sweep = ((head >> 17) & 0x3FF) - 512f;
        if ((head >> 27 & 1) != 0)
        {
            SetDrawn(true, iai: true);
            _blade.ArmIai(((head >> 28) & 0xF) / 15f);
        }
        _blade.Replay(a0, sweep, ms / 1000f, fallen);
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
                ShowCall.Send((h, (ushort)100));
            }
            return;
        }
        _hostStrokes++;
        float charge = 0f;
        if (iai)
        {
            // 溜めは客の申告。納めてから経った時間で溜められた分までに丸める
            charge = FxMath.Min(s.Iai > 0 ? (s.Iai - 1) / 254f : 0f, ChargeOf(Time.time - _sheathedAt + 0.3f));
            SetDrawn(true, iai: true);
        }
        if (!DevFree(Player)) _hostKi -= StrokeCost;
        float a0 = Math.Clamp((int)s.A0, -180, 180);
        float sweep = Math.Clamp((int)s.Sweep, -(int)SwipeInput.MaxSweep, (int)SwipeInput.MaxSweep);
        // 速さ (= 威力) は客の申告なので、人の手で出せない短さは丸める
        float secs = Math.Clamp(s.Ms / 1000f, MinStrokeSeconds, SwipeInput.MaxSeconds);
        int head = PlayerId & 0x7F | (_fallen ? 1 : 0) << 7 | ((int)a0 + 180) << 8 | ((int)sweep + 512) << 17 | (iai ? 1 : 0) << 27 | (int)MathF.Round(charge * 15f) << 28;
        ShowCall.Send((head, (ushort)(secs * 1000f)));
        if (!IsLocal) ShowStroke(head, (ushort)(secs * 1000f));
        int dmg = Damage(FxMath.Abs(sweep) / secs);
        if (iai) dmg += (int)(IaiCharged * charge);
        _lastDamage = dmg;
        _hostStrokeIai = iai;
        HostHit(a0, sweep, dmg);
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
        if (Time.time - _drawnAt < DrawTime - 0.2f) return iai ? "sheathing" : "drawing";
        if (HostKiNow() < -KiSlack && !DevFree(p)) return "ki";
        return null;
    }

    private void HostHit(float a0, float sweep, int damage)
    {
        var p = Player;
        Vector2 me = p.GetTruePosition();
        float reach = SlashRange + RangeSlack;
        float span = FxMath.Abs(sweep);
        var all = GameData.Instance.AllPlayers;
        // 先に当たる人を全員決めてから斬る (斬った後の本編の処理で一覧が変わる)
        Span<byte> hits = stackalloc byte[16];
        int n = 0;
        for (int i = 0; i < all.Count && n < hits.Length; i++)
        {
            var info = all[i];
            if (info == null || info.Disconnected || info.IsDead || info.PlayerId == PlayerId) continue;
            var pc = info.Object;
            if (!pc || pc.inVent || pc.inMovingPlat) { _missBusy++; continue; }
            Vector2 at = pc.GetTruePosition();
            float dx = at.x - me.x, dy = at.y - me.y, sq = dx * dx + dy * dy;
            if (sq > reach * reach) { _missFar++; continue; }
            if (sq > Touching * Touching)
            {
                // 振った向きに沿って、始めの角度から相手の角度までの回り (0〜360)
                float ta = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;
                float d = (sweep >= 0f ? ta - a0 : a0 - ta) % 360f;
                if (d < 0f) d += 360f;
                if (d > span + ArcPad && d < 360f - ArcPad) { _missArc++; continue; }
            }
            if (PhysicsHelpers.AnythingBetween(me, at, Constants.ShipAndObjectsMask, false)) { _missWall++; continue; }
            // 回避で飛び退いている間は当たらない
            if (DuelDodge.HostSafe(info.PlayerId)) { _missDodge++; continue; }
            hits[n++] = info.PlayerId;
        }
        bool counted = false;
        for (int i = 0; i < n; i++)
        {
            var target = GameData.Instance.GetPlayerById(hits[i])?.Object;
            if (!target || target.Data == null || target.Data.IsDead) continue;
            // サムライへの斬りは、相手の斬り (納めていれば居合) とぶつかるかを少し待ってから決める (Samurai.Clash.cs)
            if (RoleState.Of(target) is Samurai ts && HostClashOrHold(ts, damage)) continue;
            counted |= HostApply(target, damage);
        }
        if (counted) HostUpdatePath();
    }

    // ホスト: 斬りを当てる。倒したら斬った数を数えて true (道の更新は呼んだ側)
    private bool HostApply(PlayerControl target, int damage)
    {
        var p = Player;
        if (!p || !target || target.Data == null || target.Data.IsDead) return false;
        // 倒す一振りだけキルとして確かめる (盾などに止められたら体力 1 で残す)
        int dmg = damage, left = Duel.HpOf(target.PlayerId);
        if (left - dmg <= 0 && BeforeMurderEvent.Ask(p, target) != null) dmg = left - 1;
        if (!Duel.HostDamage(PlayerId, target.PlayerId, dmg)) return false;
        bool impostor = target.Data.Role && target.Data.Role.IsImpostor;
        p.RpcMurderPlayer(target, true);
        if (Duel.OpponentOf(PlayerId) == target.PlayerId) Duel.HostEnd(PlayerId);
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
        _blade.SetFallen(Fallen && IsLocal);
        if (IsLocal) RoleDisplay.EnsureTaskHeader(Player);
    }

    // 自分の画面のタスク欄の先頭: 今の道と進み
    public override Text Description => !IsLocal ? Blurb
        : Fallen ? new Text("Samurai.FallenDesc")
        : new Text("Samurai.PathDesc").With("n", _impostorKills.Value).With("max", PathKills.Value);

    // 確かめ用
    internal string Probe() =>
        $"samurai={PlayerId} drawn={Drawn} ki={_ki:0} spent={Spent} charge={Charge:0.00} hostKi={_hostKi:0} lastDmg={_lastDamage} iai[starts={_starts} sent={_sentIai} host={_hostIai}] miss[busy={_missBusy} far={_missFar} arc={_missArc} wall={_missWall} dodge={_missDodge}] imp={_impostorKills.Value} other={_otherKills.Value} fallen={Fallen} path={PathDone} killer={IsKiller} sent={_sent} hostStrokes={_hostStrokes} refused={_hostRefused}({_lastRefusal}) blade[{_blade?.Describe()}] screen[{SamuraiScreen.Describe()}] clash[{DescribeClash()} locked={Locked}] duel[{Duel.Describe()}]";
}

// 本編のキルの演出は、倒した人を倒された人の所へ瞬間移動させる (コルーチン CoPerformKill の中)。
// サムライの斬りは間合いの外から当たるので、移された同じフレームのうちに元の場所へ戻す (全員の端末で同じに)
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
        if (!src || RoleState.Of(src) is not Samurai) return;
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
