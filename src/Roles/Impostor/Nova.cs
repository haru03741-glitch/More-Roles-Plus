// Based on the Nova role of https://github.com/ykundesu/SuperNewRoles (GPL-3.0) as described by its role card
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ノヴァ (インポスター): 力を溜めて、まっすぐな光線を撃つ。溜めるほど光線は太く、何人も貫き、壁を大きく焼く。
// 溜め 0 (短く押しただけ) の光線は最初に当たった 1 人で止まり、満タンなら帯の中の全員を倒す。壁ではどれも止まる。
// 押している間は溜まり (設定の秒で満タン)、離した時に撃つ。溜めている間は動けず、狙いはゆっくりしか回らない。
// 溜まり具合はノヴァの脇の筒で全員に見える。撃つ向きは設定で選ぶ: 自由 (マウスの向き) / 左右 (向いている方) / 上下左右・8 方向 (マウスの向きに近い方)。
// PC は右クリックで構え (もう一度でしまう)・左クリックを押している間溜める (発射のボタンを押しに行くとマウスが下へ動き、狙いが必ず下になるため)。
// ボタンは構えの切り替えと待ち時間の表示に使う。スマホはボタンを押している間溜め、最後に歩いた向きへ撃つ
public sealed partial class Nova : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Nova.Name");
    public override string Reading => "のゔぁ";
    public override Text Blurb => new("Nova.Blurb");
    public override string Origin => "uchu";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Nova.Fire", "nova_fire") };

    public static readonly ChoiceOpt Mode = new("Nova.Mode", 0,
        new Text("Nova.Mode.Free"), new Text("Nova.Mode.LeftRight"), new Text("Nova.Mode.FourWay"), new Text("Nova.Mode.EightWay"));
    public static readonly FloatOpt Cooldown = new("Nova.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt Size = new("Nova.Size", 1f, 0.25f, 5f, 0.25f, "x");
    public static readonly FloatOpt Charge = new("Nova.Charge", 5f, 1f, 10f, 0.5f, "s");
    public static readonly ChoiceOpt BeamColor = new("Nova.BeamColor", 0,
        new Text("Nova.BeamColor.Mono"), new Text("Nova.BeamColor.Violet"));

    private const float MaxLength = 40f;
    private const float ChargeTurn = 25f;     // 溜めている間の狙いの回る速さ (度/秒)
    private const float AimSendGap = 0.25f;   // 溜めている間の狙いを全員へ送る間 (2° 以上変わった時だけ)
    private const float WindUp = 0.12f;       // 離してから光線が出るまで
    private const float HoldLimit = 30f;      // 満タンのあと押したままにできる長さ (受け手が溜めを捨てる目安)

    private Ability _fire;
    private bool _stance;      // 自分の端末: 構えている (PC)
    private bool _charging;    // 自分の端末: 溜めている
    private float _chargeAt, _chargeAngle, _sentAngle, _sentAt;
    private int _reachFrame;   // 照準の線の長さを測り直すフレーム
    private float _reach = MaxLength;

    // 全員の端末: 溜めを受けた時刻 (撃った時の溜め量の上限をここから測る)
    private float _heardChargeAt = -1f;

    // 自分の端末のノヴァ (PC の右クリック/左クリックとスマホの長押しを毎フレーム見る)
    private static Nova _mine;
    private static bool Pc => !System.OperatingSystem.IsAndroid();

    public override void OnAssigned()
    {
        _fire = AddAbility(Pc
            ? new Ability(new Text("Nova.Fire"), "nova_fire")
            {
                Cooldown = () => Cooldown,
                OnUse = () => { ToggleStance(); return false; },
            }
            : new Ability(new Text("Nova.Fire"), "nova_fire")
            {
                Cooldown = () => Cooldown,
                HoldMin = () => 0f,
                HoldMax = () => Charge,
                OnHold = _ => _charging && Release(),
            });
        if (IsLocal)
        {
            _mine = this;
            Lifespan.OnRelease(() =>
            {
                if (_stance) EndStance();
                if (_charging) Abort();
                if (_mine == this) _mine = null;
            });
        }
    }

    private float Level => FxMath.Clamp01((Time.time - _chargeAt) / Charge);

    private void StartCharge(float angle)
    {
        _charging = true;
        _chargeAt = Time.time;
        _chargeAngle = _sentAngle = angle;
        _sentAt = Time.time;
        MoveSpeed.Add("NovaCharge", 0f, 600f);
        ChargeCall.Send(angle);
        OnCharge(this, angle);
    }

    // 離した: 溜めた分で撃つ
    private bool Release()
    {
        if (!_charging) return false;
        float level = Level;
        _charging = false;
        _stance = false;
        MoveSpeed.Remove("NovaCharge");
        MoveSpeed.Add("NovaCharge", 0f, WindUp + 0.1f);
        byte lv = (byte)System.MathF.Round(level * 255f);
        FireCall.Send((_chargeAngle, lv));
        OnFire(this, _chargeAngle, lv);
        return true;
    }

    // 溜めの途中で撃てなくなった (会議・死・動けない)
    private void Abort()
    {
        _charging = false;
        MoveSpeed.Remove("NovaCharge");
        NovaFx.CancelCharge(Player);
    }

    private void ToggleStance()
    {
        if (_charging) return;
        if (_stance) { EndStance(); return; }
        if (!PortKit.Alive(Player) || !Player.CanMove || RewindPlayback.Active) return;
        _stance = true;
        _reachFrame = 0;
    }

    private void EndStance()
    {
        _stance = false;
        NovaFx.EndStance(Player);
    }

    // ボット (ホストが動かすダミー): angle の向きへ seconds 溜めて撃つ (溜めの間の見た目と音は本人が撃つ時と同じ電文で全員へ)
    private bool _botCharging;
    internal bool BotCharging => _botCharging;

    internal bool BotFire(float angle, float seconds)
    {
        if (!IsBot || _botCharging || RewindPlayback.Active || !PortKit.Alive(Player) || !Player.CanMove) return false;
        _botCharging = true;
        ChargeCall.SendAs(Player, angle);
        OnCharge(this, angle);
        float hold = System.Math.Clamp(seconds, 0f, Charge);
        PortKit.After(hold, () =>
        {
            _botCharging = false;
            var p = Player;
            if (!IsBot || MeetingHud.Instance || !PortKit.Alive(p))
            {
                if (p) NovaFx.CancelCharge(p);
                return;
            }
            byte lv = (byte)System.MathF.Round(FxMath.Clamp01(hold / Charge) * 255f);
            FireCall.SendAs(p, (angle, lv));
            OnFire(this, angle, lv);
        });
        return true;
    }

    // 試験用: 右クリック/左クリックと同じ道 (bridge の nova stance / nova fire / nova charge)
    private static float _testReleaseAt = -1f;
    internal static string StanceForTest() { if (_mine == null) return "no nova"; _mine.ToggleStance(); return _mine._stance ? "on" : "off"; }
    internal static string FireForTest() => ChargeForTest(0f);
    internal static string ChargeForTest(float seconds)
    {
        if (_mine == null) return "no nova";
        if (!_mine.TryStartCharge()) return "not ready";
        _testReleaseAt = Time.time + seconds;
        return "charging";
    }

    private bool TryStartCharge()
    {
        var button = _fire?.Button;
        if (!_stance || _charging || button == null || button.CoolingDown || RewindPlayback.Active || !PortKit.Alive(Player) || !Player.CanMove) return false;
        StartCharge(Aim(Player));
        return true;
    }

    // 毎フレーム (NovaFx.Tick から): PC の右クリックで構え・左クリックを押している間溜める。押し始めがボタン・パネルの上なら何もしない
    internal static void LocalTick()
    {
        var n = _mine;
        if (n == null) return;
        var p = n.Player;
        bool blocked = !p || MeetingHud.Instance || Minigame.Instance || RewindPlayback.Active || !PortKit.Alive(p);
        if (n._charging)
        {
            if (blocked) { n.Abort(); return; }
            n.TickCharge(p);
            return;
        }
        if (!Pc)
        {
            // スマホ: ボタンを押し始めたら溜め始める (離した時はボタンが OnHold を呼ぶ)
            var b = n._fire?.Button;
            if (b != null && b.Holding && !blocked && p.CanMove) n.StartCharge(Aim(p));
            return;
        }
        if (n._stance && (blocked || !p.CanMove)) { n.EndStance(); return; }
        bool right = Input.GetMouseButtonDown(1), left = n._stance && Input.GetMouseButtonDown(0);
        if ((right || left) && !Minigame.Instance && !MeetingHud.Instance && !OverUi())
        {
            if (right) n.ToggleStance();
            else if (n.TryStartCharge()) return;
        }
        if (!n._stance) return;
        float angle = Aim(p);
        // 照準の線の長さ (壁まで) は数フレームに 1 回測る
        if (--n._reachFrame <= 0)
        {
            n._reachFrame = 4;
            n._reach = Reach(p.GetTruePosition(), angle, out _);
        }
        NovaFx.Stance(p, angle, n._reach);
    }

    // 溜めている間: 狙いはゆっくり回り、変わったら時々全員へ送る。離すか試験の時刻で撃つ
    private void TickCharge(PlayerControl p)
    {
        float target = Aim(p);
        float d = target - _chargeAngle;
        d -= 360f * System.MathF.Round(d / 360f);
        float step = ChargeTurn * Time.deltaTime;
        _chargeAngle += d > step ? step : d < -step ? -step : d;
        float now = Time.time;
        if (now - _sentAt >= AimSendGap)
        {
            float sd = _chargeAngle - _sentAngle;
            sd -= 360f * System.MathF.Round(sd / 360f);
            if (sd > 2f || sd < -2f) { _sentAngle = _chargeAngle; _sentAt = now; AimCall.Send(_chargeAngle); }
        }
        if (--_reachFrame <= 0)
        {
            _reachFrame = 4;
            _reach = Reach(p.GetTruePosition(), _chargeAngle, out _);
        }
        NovaFx.ChargeAim(p, _chargeAngle, _reach);
        bool release = Pc && !Input.GetMouseButton(0);
        if (_testReleaseAt >= 0f) release = now >= _testReleaseAt;
        if (!release) return;
        _testReleaseAt = -1f;
        if (Release() && Pc) _fire?.Button?.StartCooldown();
    }

    private static bool OverUi() =>
        PassiveButtonManager.InstanceExists && PassiveButtonManager.Instance.currentOver is { } over && over && !over.GetComponent<PlayerControl>();

    // スマホは最後に歩いた向きへ撃つので、自分の端末で向きを覚えておく
    public override void OnFixedUpdate()
    {
        if (_blSam != 255) HostLockTick();
        if (IsLocal && System.OperatingSystem.IsAndroid()) ShotKit.Track();
    }

    // 撃つ向き (度・右が 0)。PC はマウスの方・スマホは最後に歩いた向き (タッチの位置は押したボタンの所になるため)
    internal static float? TestAngle; // 試験用: 撃つ向きを固定する (bridge の nova aim)

    private static float Aim(PlayerControl lp)
    {
        if (TestAngle is float fixedAngle) return fixedAngle;
        bool left = lp.cosmetics && lp.cosmetics.FlipX;
        if (Mode.Value == 1) return left ? 180f : 0f;
        float a;
        var cam = Camera.main;
        if (System.OperatingSystem.IsAndroid() || !cam)
        {
            var f = ShotKit.Facing;
            a = FxMath.Atan2(f.y, f.x) * (180f / System.MathF.PI);
        }
        else
        {
            Vector2 me = lp.GetTruePosition();
            Vector2 mouse = cam.ScreenToWorldPoint(Input.mousePosition);
            a = FxMath.Atan2(mouse.y - me.y, mouse.x - me.x) * (180f / System.MathF.PI);
        }
        if (Mode.Value == 2) a = System.MathF.Round(a / 90f) * 90f;
        else if (Mode.Value == 3) a = System.MathF.Round(a / 45f) * 45f;
        return a;
    }

    private static readonly RemoteCall<float> ChargeCall = new("Nova.Charge", Route.Anyone,
        (w, a) => w.Write(a), r => r.ReadSingle(),
        (sender, a) => { if (RoleState.Of(sender) is Nova n && n.AcceptCharge()) OnCharge(n, a); }, perSecond: 4);

    private static readonly RemoteCall<float> AimCall = new("Nova.Aim", Route.Anyone,
        (w, a) => w.Write(a), r => r.ReadSingle(),
        (sender, a) => { if (RoleState.Of(sender) is Nova n && n._heardChargeAt >= 0f && n.Player) NovaFx.ChargeAim(n.Player, a, Reach(n.Player.GetTruePosition(), a, out _)); },
        Delivery.Queued, perSecond: 8);

    private static readonly RemoteCall<(float angle, byte level)> FireCall = new("Nova.Fire", Route.Anyone,
        (w, m) => { w.Write(m.angle); w.Write(m.level); }, r => (r.ReadSingle(), r.ReadByte()),
        (sender, m) => { if (RoleState.Of(sender) is Nova n && n.AcceptFire()) OnFire(n, m.angle, m.level); });

    // 受けた側の端末で: 待ち時間より明らかに早い溜め・発射は無視する
    private float _lastFireAt = -999f;

    private bool AcceptCharge() => Time.time - _lastFireAt >= Cooldown * 0.5f || Dev.DevCooldown.Zero;

    private bool AcceptFire()
    {
        float now = Time.time;
        if (now - _lastFireAt < Cooldown * 0.5f && !Dev.DevCooldown.Zero) return false;
        _lastFireAt = now;
        return true;
    }

    // 全員の端末で: 溜め始め (キャノンを構えて溜まりの筒を出す)
    private static void OnCharge(Nova n, float angle)
    {
        var p = n.Player;
        if (!p) return;
        n._heardChargeAt = Time.time;
        Vector2 from0 = p.GetTruePosition();
        NovaFx.BeginCharge(p, angle, Charge, Size, Reach(from0, angle, out _), Charge + HoldLimit);
    }

    // 全員の端末で: 撃った。溜め量は送られた値と、溜め始めを受けてからの時間の小さい方 (溜めずに満タンを名乗る客を通さない)
    private static void OnFire(Nova n, float angle, byte lv)
    {
        var p = n.Player;
        if (!p) return;
        float level = lv / 255f;
        float heard = n._heardChargeAt < 0f ? 0f : (Time.time - n._heardChargeAt) / Charge + 0.1f;
        if (level > heard) level = FxMath.Clamp01(heard);
        n._heardChargeAt = -1f;
        RangedKillSnapPatch.Mark(n.PlayerId, WindUp + 2f);
        PortKit.After(WindUp, () =>
        {
            if (MeetingHud.Instance || !ShipStatus.Instance || !PortKit.Alive(p)) { NovaFx.CancelCharge(p); return; }
            Beam(n, p.GetTruePosition(), angle, level);
        });
    }

    // 光線の届く長さ (壁で止まる)
    private static float Reach(Vector2 from, float angle, out bool wall)
    {
        float r = angle * (System.MathF.PI / 180f);
        var hit = Physics2D.Raycast(from, FxMath.V2(FxMath.Cos(r), FxMath.Sin(r)), MaxLength, Constants.ShipOnlyMask);
        wall = hit.collider;
        return wall ? hit.distance : MaxLength;
    }

    // 溜め量 → 光線の太さの倍率・貫く人数 (満タンは全員)
    private static float Thick(float level) => 1f + 0.6f * level; // 太さは控えめ (広がりは円錐が受け持つ・根元は細いまま)
    private static int Pierce(float level) => level >= 0.999f ? int.MaxValue : 1 + (int)(level * 4f);
    // 溜め量 → 光線の片側の幅 (世界の単位)。光線は砲口の前の頂点 1 点から弾丸形に開き、幅 = これ × tanh(頂点からの距離 / (0.85 × これ))
    private static float Half(float level) => Size * (HalfMin + HalfGain * level);
    internal const float HalfMin = 0.55f, HalfGain = 1.45f; // 溜め無しの片側の幅と、満タンまでに足される幅 (太さの設定 1 あたり)
    internal const float ApexBack = 1.2f; // 足元から光線の頂点まで (狙いの向きの長さ・縦向きのキャノンの縮みの分だけ控えめ)

    private static void Beam(Nova n, Vector2 from, float angle, float level)
    {
        float r = angle * (System.MathF.PI / 180f);
        Vector2 dir = FxMath.V2(FxMath.Cos(r), FxMath.Sin(r));
        float length = Reach(from, angle, out bool wall);
        float size = Size * Thick(level);
        // 貫ける人数を超えたら、その人の所で光線が止まる (どの端末でも同じ並びから決める)
        float half = Half(level);
        int hits = Victims(n.Player, from, dir, length, half, Pierce(level), level >= 0.999f, out float stopAt);
        if (stopAt < length) { length = stopAt; wall = false; }
        // サムライで止まった所は、壁に当たった時と同じく光が弾ける (壁は焼かない)
        NovaFx.Fire(n.Player, from, angle, length, size, wall || _samuraiStop, half);
        if (AmongUsClient.Instance.AmHost)
        {
            HostBeam(n, hits, from, dir, length, size, wall, level);
            HostBeamPuppets(n.Player, from, dir, length, half);
        }
    }

    // ホスト: 光線の中のドッペルゲンガーの分身を倒す (人と同じ形で当てる・貫ける人数には数えない)
    private static void HostBeamPuppets(PlayerControl invoker, Vector2 from, Vector2 dir, float length, float half)
    {
        if (!Crew.DoppelPuppet.Any || !PortKit.Alive(invoker)) return;
        float grow = 0.85f * half;
        PuppetHits.Clear();
        foreach (var m in Crew.DoppelPuppet.Each)
        {
            Vector2 d = m.True - from;
            float along = d.x * dir.x + d.y * dir.y;
            if (along < ApexBack || along > length) continue;
            float side = FxMath.Abs(d.x * dir.y - d.y * dir.x);
            float e = FxMath.Exp(-2f * (along - ApexBack) / grow);
            if (side > half * (1f - e) / (1f + e) + 0.2f) continue;
            PuppetHits.Add(m.Owner);
        }
        foreach (var id in PuppetHits) Crew.Doppelganger.HostStrikeRanged(invoker, id);
    }
    private static readonly System.Collections.Generic.List<byte> PuppetHits = new();

    private static readonly System.Collections.Generic.List<(float along, PlayerControl p)> Hit = new();

    // 光線 (頂点から開く形・片側の幅 half) の中の生きている人を近い順に、貫ける人数まで。超えた所で光線を止める長さを返す
    // 満タンでない光線は、抜いていても抜いていなくても最初に当たったサムライで止まる (刀で受ける・体で受ける)
    private static int Victims(PlayerControl invoker, Vector2 from, Vector2 dir, float length, float half, int pierce, bool full, out float stopAt)
    {
        Hit.Clear();
        stopAt = float.MaxValue;
        float grow = 0.85f * half;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.inVent || p.PlayerId == invoker.PlayerId) continue;
            Vector2 d = p.GetTruePosition() - from;
            float along = d.x * dir.x + d.y * dir.y;
            if (along < ApexBack || along > length) continue;
            float side = FxMath.Abs(d.x * dir.y - d.y * dir.x);
            // 見た目の光線と同じ形 + 体の半径
            float e = FxMath.Exp(-2f * (along - ApexBack) / grow);
            float w = half * (1f - e) / (1f + e); // tanh
            if (side > w + 0.2f) continue;
            // 芯から外れた所は、撃った所から壁を越えては届かない
            if (side > 0.35f)
            {
                float dl = FxMath.Sqrt(d.x * d.x + d.y * d.y);
                if (dl > 0.01f && Physics2D.Raycast(from, FxMath.V2(d.x / dl, d.y / dl), dl, Constants.ShipOnlyMask).collider) continue;
            }
            Hit.Add((along, p));
        }
        Hit.Sort((a, b) => a.along.CompareTo(b.along));
        _samuraiStop = false;
        if (!full && Neutral.Samurai.Count > 0)
            for (int i = 0; i < Hit.Count && i < pierce; i++)
            {
                if (RoleState.Of(Hit[i].p) is not Neutral.Samurai) continue;
                if (Hit.Count > i + 1) Hit.RemoveRange(i + 1, Hit.Count - i - 1);
                stopAt = FxMath.Max(Hit[i].along - SamuraiStop, ApexBack + 0.1f);
                _samuraiStop = true;
                return Hit.Count;
            }
        if (Hit.Count > pierce)
        {
            // 透明な人は貫ける人数に数えず、光線もそこで止めない (止まった所で居場所が分かる)。見えている光線の中にいれば倒れる
            int seen = 0, cut = -1;
            for (int i = 0; i < Hit.Count; i++)
            {
                if (!Hit[i].p.Visible) continue;
                if (++seen > pierce) { cut = i; break; }
                stopAt = Hit[i].along + 0.3f;
            }
            if (cut < 0) { stopAt = float.MaxValue; return Hit.Count; }
            Hit.RemoveRange(cut, Hit.Count - cut);
            for (int i = Hit.Count - 1; i >= 0; i--)
                if (Hit[i].along >= stopAt) Hit.RemoveAt(i);
        }
        return Hit.Count;
    }

    // ホスト: 光線が貫いた人を倒す (Victims の結果)。キルを防ぐ役職には聞く
    private static void HostBeam(Nova n, int hits, Vector2 from, Vector2 dir, float length, float size, bool wall, float level)
    {
        var invoker = n.Player;
        if (!PortKit.Alive(invoker)) return;
        HostScorch(from, dir, length, size, wall);
        for (int i = 0; i < hits && i < Hit.Count; i++)
        {
            var p = Hit[i].p;
            if (RoleState.Of(p) is Neutral.Samurai s) { n.HostSamurai(s, from, level); continue; }
            if (BeforeMurderEvent.Ask(invoker, p) != null) continue;
            invoker.RpcMurderPlayer(p, true);
        }
    }

    // ホスト: 当たった壁をプラズマの爆発で抜き (光線の向きへ伸びた形)、通った床に熱を足す (水は湯気になり、燃える物には火が付く)。
    // 熱を足す所は長さに応じて最大 5 か所 (電文を増やしすぎない)
    private const int MaxHeatPoints = 5;

    private static void HostScorch(Vector2 from, Vector2 dir, float length, float size, bool wall)
    {
        if (wall)
        {
            float r = System.Math.Clamp(0.6f + 0.5f * size, 0.6f, Terrain.TerrainPermit.MaxRadius);
            var res = Terrain.TerrainApi.WorldBlast(from + dir * length, r, dir, 0.7f);
            if (!res.Ok) Plugin.Logger.LogInfo($"nova: terrain blast refused ({res.Why})");
        }
        int n = System.Math.Clamp((int)(length / 2f), 1, MaxHeatPoints);
        float rad = 0.3f + 0.2f * size;
        for (int i = 0; i < n; i++)
            Terrain.TerrainApi.WorldIgnite(from + dir * (length * (i + 0.5f) / n), rad, 0.6f);
    }
}
