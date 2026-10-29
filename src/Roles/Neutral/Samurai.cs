using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// サムライ (第三陣営): 刀を抜いている間だけ、向いた方向の扇の中にいる全員を斬れる。
// 始めは守の道 = インポスターを PathKills 人斬って最後まで生きていれば、勝った陣営と一緒に勝つ。
// インポスター以外を FallKills 人斬ると修羅の道へ堕ち、以後は自分以外の生存者が 1 人以下になった時に単独で勝つ。
// 抜いた刀は全員に見える。斬った数と堕ちたかはホストが数え、本人にだけ知らせる (全員へ知らせるかは設定)
public sealed class Samurai : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#4A8CFF";
    public override Text Name => new("Samurai.Name");
    public override string Reading => "さむらい";
    public override Text Blurb => new("Samurai.Blurb");
    public override HelpSection[] HelpSections => new HelpSection[]
    {
        new("Samurai.Draw", "katana_draw"), new("Samurai.Slash", "katana_slash"), new("Samurai.Paths"),
    };

    public static readonly IntOpt PathKills = new("Samurai.PathKills", 1, 1, 3);
    public static readonly IntOpt FallKills = new("Samurai.FallKills", 2, 1, 5);
    public static readonly BoolOpt AnnounceFall = new("Samurai.AnnounceFall", false);
    public static readonly FloatOpt SlashCooldown = new("Samurai.SlashCooldown", 20f, 5f, 60f, 2.5f, "s");
    public static readonly FloatOpt SlashRange = new("Samurai.SlashRange", 2f, 1f, 3.5f, 0.25f);

    // 斬れる扇の半分の角度 (度)・振りの溜め (ホストは溜めの後の位置で当てる = 見てから避けられる)・抜刀/納刀の動作の長さ
    internal const float HalfArcDeg = 55f;
    internal const float WindUp = 0.25f;
    internal const float DrawTime = 0.6f;
    private const float CooldownSlack = 1.5f; // ホストが待ち時間を確かめる時の通信の遅れの分 (秒)
    private const float RangeSlack = 0.3f;    // 溜めの間に相手が動いた分の余裕
    private const int WinCheckMs = 500;

    // 修羅に堕ちたらキル役として数える (インポスターがいなくなっても生存者が多ければクルーの勝ちにしない)。
    // ホストの判定で読むので、ホストの値で決まる
    public override bool IsKiller => _fallen;
    public override bool CanKill => false; // 本編のキルボタンは使わない (斬りは自前の依頼)

    // 抜いているか (全員の端末で持つ)・抜いた時刻 (動作の間は斬れない)
    internal bool Drawn { get; private set; }
    private float _drawnAt = -10f;
    // ホストが数える値。本人の端末には知らせで写す。時戻しで戻る
    private readonly Tracked<int> _impostorKills = new(0);
    private readonly Tracked<int> _otherKills = new(0);
    private readonly Tracked<bool> _fallen = new(false);
    private readonly Tracked<bool> _pathDone = new(false);

    private Ability _draw, _slash;
    private bool? _shownDrawn;
    private SamuraiBlade _blade;
    // ホスト: 溜めている振り (向き・当てる時刻)
    private Vector2 _pendingDir;
    private long _pendingAt;
    private long _nextWinCheck;

    internal bool Fallen => _fallen;
    internal bool PathDone => _pathDone;

    public override void OnAssigned()
    {
        _blade = new SamuraiBlade(this);
        Lifespan.OnRelease(_blade.Dispose);
        _draw = AddAbility(new Ability(new Text("Samurai.Draw"), "katana_draw")
        {
            Cooldown = () => DrawTime,
            CooldownAtStart = false,
            OnUse = ToggleDraw,
        });
        _slash = AddAbility(new Ability(new Text("Samurai.Slash"), "katana_slash")
        {
            Cooldown = () => SlashCooldown,
            OnUse = Slash,
        });
    }

    // ---- 抜刀 / 納刀 ----

    private bool ToggleDraw()
    {
        bool drawn = !Drawn;
        SetDrawn(drawn);
        DrawCall.Send(drawn);
        return true;
    }

    private void SetDrawn(bool drawn)
    {
        if (Drawn == drawn) return;
        Drawn = drawn;
        _drawnAt = Time.time;
        _blade.SetDrawn(drawn);
    }

    private void RefreshButtons()
    {
        _draw?.Button?.SetLabel(new Text(Drawn ? "Samurai.Sheathe" : "Samurai.Draw"));
        _slash?.Button?.SetUsable(Drawn);
    }

    // 抜いた全員の端末で。抜いた人は送った時に手元で済ませている
    private static readonly RemoteCall<bool> DrawCall = new("Samurai.Draw", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadBoolean(),
        (sender, v) => (RoleState.Of(sender) as Samurai)?.SetDrawn(v), perSecond: 6);

    // 会議が終わると刀は鞘に戻っている (全員の端末で同じに戻す)
    private void OnMeetingEnd(MeetingEndEvent e) => SetDrawn(false);

    // ---- 斬る ----

    private bool Slash() => Slash(Aim(Player));

    private bool Slash(Vector2 dir)
    {
        if (!Drawn || Time.time - _drawnAt < DrawTime) return false;
        _blade.Swing(dir, Fallen);
        SlashCall.Send(dir);
        return true;
    }

    // 確かめ用: 向きを決めて斬るのボタンを押したのと同じにする (待ち時間と回数も使う)
    internal bool SlashForTest(Vector2 dir)
    {
        if (_slash?.Button == null || _slash.Button.CoolingDown || !Slash(dir)) return false;
        _slash.Spend();
        _slash.Button.StartUsed();
        return true;
    }

    // PC はマウスの指す方向 (ボタンをマウスで押した時はボタンの上なので歩く向き)。スマホは歩く向き
    private static Vector2 Aim(PlayerControl lp)
    {
        Vector2 walk = Terrain.HammerSwing.Aim(lp, out _);
        if (OperatingSystem.IsAndroid()) return walk;
        if (PassiveButtonManager.InstanceExists && PassiveButtonManager.Instance.currentOver) return walk;
        var cam = Camera.main;
        if (!cam) return walk;
        Vector3 m = cam.ScreenToWorldPoint(Input.mousePosition);
        Vector2 at = lp.GetTruePosition();
        float dx = m.x - at.x, dy = m.y - at.y, len = MathF.Sqrt(dx * dx + dy * dy);
        return len > 0.05f ? FxMath.V2(dx / len, dy / len) : walk;
    }

    private static readonly RemoteCall<Vector2> SlashCall = new("Samurai.Slash", Route.ToHost,
        (w, v) => { w.Write(v.x); w.Write(v.y); }, r => FxMath.V2(r.ReadSingle(), r.ReadSingle()),
        (sender, v) => (RoleState.Of(sender) as Samurai)?.HostSlash(v));

    // 振った姿 (ホスト → 全員)。値 = 振った人 | 向きの角度 (0..65535) << 8 | 修羅の色 << 24。振った本人は手元で済ませている
    private static readonly RemoteCall<int> SwingCall = new("Samurai.Swing", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadInt32(),
        (_, v) => (RoleState.Of((byte)v) as Samurai)?.ShowSwing(v));

    private void ShowSwing(int v)
    {
        if (IsLocal) return;
        float a = ((v >> 8) & 0xFFFF) / 65535f * MathF.PI * 2f;
        _blade.Swing(FxMath.V2(MathF.Cos(a), MathF.Sin(a)), FallenTrailSeen && (v >> 24 & 1) != 0);
    }

    // 修羅の軌跡の色を他の人に見せるか (今は常に見せる)
    private static bool FallenTrailSeen => true;

    // ホスト: 振りの依頼を確かめ、溜めの後に当てる
    private void HostSlash(Vector2 dir)
    {
        string why = HostRefuse(dir);
        if (why != null)
        {
            Plugin.Logger.LogWarning($"Samurai slash refused {PlayerId}: {why}");
            return;
        }
        float len = MathF.Sqrt(dir.x * dir.x + dir.y * dir.y);
        _pendingDir = FxMath.V2(dir.x / len, dir.y / len);
        _pendingAt = Environment.TickCount64 + (long)(WindUp * 1000f);
        _slash.HostLastMs = Environment.TickCount64;
        float ang = MathF.Atan2(_pendingDir.y, _pendingDir.x);
        if (ang < 0f) ang += MathF.PI * 2f;
        int a16 = (int)(ang / (MathF.PI * 2f) * 65535f) & 0xFFFF;
        SwingCall.Send(PlayerId | a16 << 8 | (_fallen ? 1 : 0) << 24);
        if (!IsLocal) ShowSwing(PlayerId | a16 << 8 | (_fallen ? 1 : 0) << 24);
    }

    private string HostRefuse(Vector2 dir)
    {
        if (AmongUsClient.Instance.IsGameOver || !ShipStatus.Instance || MeetingHud.Instance || ExileController.Instance) return "not now";
        var p = Player;
        if (!p || p.Data == null || p.Data.IsDead || p.Data.Disconnected) return "user";
        if (p.inVent || p.inMovingPlat) return "user busy";
        if (!Drawn || Time.time - _drawnAt < DrawTime - 0.2f) return "sheathed";
        if (_pendingAt != 0) return "swinging";
        if (float.IsNaN(dir.x) || float.IsNaN(dir.y) || dir.x * dir.x + dir.y * dir.y < 0.01f) return "dir";
        if ((Environment.TickCount64 - _slash.HostLastMs) / 1000f < SlashCooldown - CooldownSlack && !Dev.DevCooldown.Skips(p)) return "cooldown";
        return null;
    }

    public override void OnFixedUpdate()
    {
        _blade.Tick();
        // ボタンは HUD が出てから作られるので、出来た後に今の抜き具合へ合わせる
        if (IsLocal && _slash?.Button != null && _shownDrawn != Drawn)
        {
            _shownDrawn = Drawn;
            RefreshButtons();
        }
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        long now = Environment.TickCount64;
        if (_pendingAt != 0 && now >= _pendingAt)
        {
            _pendingAt = 0;
            HostResolve(_pendingDir);
        }
        if (_fallen && now >= _nextWinCheck)
        {
            _nextWinCheck = now + WinCheckMs;
            HostCheckFallenWin();
        }
    }

    // ホスト: 扇の中の生きている人を全員斬る (壁越しは斬れない)
    private void HostResolve(Vector2 dir)
    {
        var p = Player;
        if (HostRefuseResolve(p)) return;
        Vector2 me = p.GetTruePosition();
        float reach = SlashRange + RangeSlack, minDot = MathF.Cos(HalfArcDeg * FxMath.Deg2Rad);
        var all = GameData.Instance.AllPlayers;
        // 先に当たる人を全員決めてから斬る (斬った後の本編の処理で一覧が変わる)
        Span<byte> hits = stackalloc byte[16];
        int n = 0;
        for (int i = 0; i < all.Count && n < hits.Length; i++)
        {
            var info = all[i];
            if (info == null || info.Disconnected || info.IsDead || info.PlayerId == PlayerId) continue;
            var pc = info.Object;
            if (!pc || pc.inVent || pc.inMovingPlat) continue;
            Vector2 at = pc.GetTruePosition();
            float dx = at.x - me.x, dy = at.y - me.y, sq = dx * dx + dy * dy;
            if (sq > reach * reach) continue;
            float len = MathF.Sqrt(sq);
            // 体が重なるほど近い人は向きによらず当たる
            if (len > 0.25f && (dx * dir.x + dy * dir.y) / len < minDot) continue;
            if (PhysicsHelpers.AnythingBetween(me, at, Constants.ShipAndObjectsMask, false)) continue;
            hits[n++] = info.PlayerId;
        }
        for (int i = 0; i < n; i++)
        {
            var target = GameData.Instance.GetPlayerById(hits[i])?.Object;
            if (!target || target.Data == null || target.Data.IsDead) continue;
            if (BeforeMurderEvent.Ask(p, target) != null) continue;
            bool impostor = target.Data.Role && target.Data.Role.IsImpostor;
            p.RpcMurderPlayer(target, true);
            if (impostor) _impostorKills.Value++;
            else _otherKills.Value++;
        }
        if (n > 0) HostUpdatePath();
    }

    private static bool HostRefuseResolve(PlayerControl p) =>
        AmongUsClient.Instance.IsGameOver || !ShipStatus.Instance || MeetingHud.Instance || ExileController.Instance
        || !p || p.Data == null || p.Data.IsDead || p.Data.Disconnected || p.inVent || p.inMovingPlat;

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
        if (IsLocal)
        {
            RefreshButtons();
            RoleDisplay.EnsureTaskHeader(Player);
        }
    }

    // 自分の画面のタスク欄の先頭: 今の道と進み
    public override Text Description => !IsLocal ? Blurb
        : Fallen ? new Text("Samurai.FallenDesc")
        : new Text("Samurai.PathDesc").With("n", _impostorKills.Value).With("max", PathKills.Value);

    // 確かめ用
    internal string Probe() =>
        $"samurai={PlayerId} drawn={Drawn} imp={_impostorKills.Value} other={_otherKills.Value} fallen={Fallen} path={PathDone} killer={IsKiller} pending={_pendingAt != 0} blade[{_blade?.Describe()}]";
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
