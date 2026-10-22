using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// インポスター: 設計台を置き、その前で設計図 (歩ける所を上から見た地図) を開く。設計図をなぞると、なぞった線に沿って
// 床から壁がせり上がる (人に掛かれば横へ押し出す)。壁はゲージを使い、一定時間で崩れる。
// 水回りの部屋の壁の近くを軽く押すと、ゲージを多めに使って壁の中の配管を裂き、水を噴き出させる。
// 設計台は全員に見える。設計図を開いている間、本体は設計台の前で立ち止まる。
// 設計台はほかの人も使える。使った人は構築士のゲージで壁を引け (無駄うちさせる・自分の役に立てる)、構築士の設計図は閉じる
public sealed class Architect : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#3C7FD8";
    public override Text Name => new("Architect.Name");
    public override string Reading => "こうちくし";
    public override Text Blurb => new("Architect.Blurb");
    public override string[] HelpSections => new[] { "Architect.Desk", "Architect.Plan", "Architect.Pipe", "Architect.Drone" };

    public static readonly IntOpt DeskCount = new("Architect.DeskCount", 2, 1, 5);
    public static readonly FloatOpt DeskCooldown = new("Architect.DeskCooldown", 10f, 0f, 60f, 2.5f, "s");
    public static readonly FloatOpt GaugeLength = new("Architect.GaugeLength", 12f, 4f, 40f, 2f);
    public static readonly FloatOpt GaugeRefill = new("Architect.GaugeRefill", 30f, 5f, 120f, 5f, "s");
    public static readonly FloatOpt WallLife = new("Architect.WallLife", 15f, 5f, 60f, 2.5f, "s");
    public static readonly FloatOpt PipeCost = new("Architect.PipeCost", 4f, 1f, 12f, 1f);
    public static readonly BoolOpt OwnRubble = new("Architect.OwnRubble", false);

    public override TerrainPermit TerrainPermit => TerrainPermit.None.WithWall(TerrainPermit.MaxWallLength, push: true).WithWater();

    private const float MinGap = 3f;          // 設計台どうしの間
    internal const float DeskBack = 0.5f;     // 設計台は置いた人の足元からこれだけ奥 (置いた人が台の手前に立つ)
    // 足元が設計台の床の点からこの範囲 (左右・手前・奥) にあれば設計図を開ける
    private const float UseHalfW = 0.75f, UseFront = 1.0f, UseBack = 0.15f;
    private const int CheckEvery = 5;         // 設計図のボタンを点けるかを見直す間隔 (FixedUpdate の回数・0.1 秒)
    // 壁は本人 (= 設計台の前) からこの距離まで (地形の依頼の届く距離から、立ち位置のずれの分を引く)
    internal const float Reach = TerrainSync.MaxBlastDistance - 0.5f;
    // なぞった線を壁に切る: 1 枚の長さの上限・線が直線からこれだけ外れたら折る・端どうしを重ねる長さ
    private const float ChunkMax = 2.4f, ChunkBend = 0.18f, Joint = 0.3f, ChunkMin = 0.6f;
    private const float BuildGap = 0.12f;     // 1 回の線の壁を 1 枚ずつ立てる間隔 (同じフレームに何枚も作らない)
    private const float FloorProbe = 0.125f;  // なぞった線が床の上か壁の中かを調べる間隔
    private const int HostEvery = 10;         // ホストが崩す壁を見直す間隔 (FixedUpdate の回数)
    private const float PipeSearch = 1.2f;    // 押した所からこの距離までの壁の面で配管を裂く
    private const float PipeStep = 0.0625f;
    private const int PipeDirs = 16;
    private const float PipeInto = 0.06f;     // 歩ける所の地図は壁を太らせてあるので、面の点を壁の線の側へ寄せる
    // 設計台の当たり判定のまわり: 人の当たり判定の半径 (+少し)・脇を通れる隙間 (クルーの直径 ≈ 0.45 + 余白)・隙間を調べる間隔
    private const float BodyClear = 0.25f, PassGap = 0.5f, GapStep = 0.0625f;
    private const int BlockMask = (1 << 9) | (1 << 12); // 船の層・家具の層

    // 置いた (持ち主 → 全員)。受け手は送り主が構築士か・数と間隔の決まりを自分で確かめる
    private static readonly RemoteCall<Vector2> DeskCall = new("Architect.Desk", Route.Anyone,
        (w, v) => { w.Write(v.x); w.Write(v.y); }, r => FxMath.V2(r.ReadSingle(), r.ReadSingle()),
        (sender, v) => Of(sender)?.TryAdd(v.x, v.y));

    // 設計図を開いた・閉じた (持ち主 → 全員)。値 = 開いた設計台の番号 (閉じた = ClosedDesk)。全員の端末でドローンを飛ばす
    private static readonly RemoteCall<byte> PlanCall = new("Architect.Plan", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadByte(), (sender, v) => Of(sender)?.OnPlan(v));

    // 瓦礫を拾いたい (持ち主 → ホスト)。ホストが確かめて、拾うと決めたら全員へ配る
    private static readonly RemoteCall<Vector2> PickCall = new("Architect.Pick", Route.ToHost,
        (w, v) => { w.Write(v.x); w.Write(v.y); }, r => FxMath.V2(r.ReadSingle(), r.ReadSingle()),
        (sender, v) => Of(sender)?.HostPick(v));

    private static readonly RemoteCall<(byte Actor, Vector2 At)> PickedCall = new("Architect.Picked", Route.HostToAll,
        (w, v) => { w.Write(v.Actor); w.Write(v.At.x); w.Write(v.At.y); }, r => (r.ReadByte(), FxMath.V2(r.ReadSingle(), r.ReadSingle())),
        (_, v) => (RoleState.Of(v.Actor) as Architect)?.OnPicked(v.At));

    // ほかの人が設計台を使い始めた・やめた (使う人 → 全員)。値 = (構築士, 台の番号・やめた = ClosedDesk)。
    // 使っている間はその人に壁の許可を貸す。構築士が開いていれば構築士の設計図は閉じる (後から開いた方が使う)
    private static readonly RemoteCall<(byte Owner, byte Desk)> UseCall = new("Architect.Use", Route.Anyone,
        (w, v) => { w.Write(v.Owner); w.Write(v.Desk); }, r => (r.ReadByte(), r.ReadByte()),
        (sender, v) => (RoleState.Of(v.Owner) as Architect)?.OnUse(sender, v.Desk));

    // 構築士の今のゲージ (持ち主 → 全員・台を使われた時)。値 = ゲージ × GaugeQuant。使っている人の端末が写しを持つ
    private static readonly RemoteCall<byte> GaugeCall = new("Architect.Gauge", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadByte(), (sender, v) => Of(sender)?.OnGauge(v));

    // 設計台をハンマーで叩いた (叩いた人 → ホスト)。回数はホストだけが数える
    private static readonly RemoteCall<(byte Owner, byte Desk)> HitCall = new("Architect.Hit", Route.ToHost,
        (w, v) => { w.Write(v.Owner); w.Write(v.Desk); }, r => (r.ReadByte(), r.ReadByte()),
        (sender, v) => (RoleState.Of(v.Owner) as Architect)?.HostHit(sender, v.Desk));

    // 叩かれた回数 (ホスト → 全員)。DeskHits 回目で壊れる
    private static readonly RemoteCall<(byte Owner, byte Desk, byte Hitter, byte Hits)> HitsCall = new("Architect.Hits", Route.HostToAll,
        (w, v) => { w.Write(v.Owner); w.Write(v.Desk); w.Write(v.Hitter); w.Write(v.Hits); }, r => (r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (_, v) => (RoleState.Of(v.Owner) as Architect)?.ShowHit(v.Desk, v.Hitter, v.Hits));

    private const byte ClosedDesk = 255;
    private const int DeskHits = 3;
    private const float HammerSlack = 1f;     // ホストが叩いた人の位置を確かめる時の遅れの分
    private const byte NoUser = 255;
    private const float GaugeQuant = 5f;
    private const float RubbleGain = 2f;      // 瓦礫 1 個で戻るゲージ
    private const float PickEvery = 0.5f;     // 次に拾う瓦礫を探す間隔
    private const float PickTimeout = 2.5f;   // 拾う頼みに返事が来なければ忘れる
    private const float HostPickGap = 0.4f;   // ホストが 1 人の構築士に拾わせる間隔の下限

    // 構築士がいる数 (いる試合だけ大きな瓦礫を焼かずに残して拾えるようにする)
    private static int _live;

    // 立った壁 (全員の端末で記録・崩すのはホスト)。ホストが替わっても新しいホストが続きから崩す
    private sealed class Built
    {
        public string Name;
        public Vector2 At;
        public float Length, Since;
        public byte Owner;
    }
    private static readonly List<Built> Walls = new();
    private static bool _listening;
    private static int _wallsGen = -1;

    private readonly List<ArchitectDeskArt.Handle> _desks = new();
    private Ability _plan;
    private int _check, _hostCheck;
    private float _gauge, _dt;
    private int _shownGauge = -1;
    // なぞり終えた線を立てる順番待ち (真ん中・向き・長さ)
    private readonly Queue<(Vector2 At, Vector2 Dir, float Len)> _queue = new();
    private float _nextBuild;
    private int _built, _refused, _pipes;
    private string _lastPipe = "-", _lastDesk = "-";
    // 設計図とドローン (全員の端末で持つ)。_sentOpen = 自分の設計図の開け閉めを最後に配った状態
    private ArchitectDrone _drone;
    private int _openDesk = -1;
    private bool _sentOpen;
    private float _pickT, _pendingAt = -1f, _hostPickAt = -10f;
    private int _picked;
    // 設計台を使っているほかの人 (全員の端末で持つ・NoUser = いない)。ドローンはどちらかが使っている台から飛ぶ
    private byte _user = NoUser;
    private int _userDesk = -1, _droneDesk = -1;
    private int _lent;
    // やめた直後の人の壁 (閉じた知らせが壁より先に届く) もこの構築士の壁として数える
    private const float UserGrace = 3f;
    private byte _lastUser = NoUser;
    private float _lastUserAt = -10f;

    // いる構築士 (台を使うボタンと、壁の許可を貸す判定に)
    private static readonly List<Architect> All = new();
    private static Lifespan _useLife;
    private static ModButton _useButton;
    private static int _useCheck;

    internal int DeskTotal => _desks.Count;
    internal float Gauge => _gauge;

    // 時戻し: ゲージは持ち主の端末だけが数えている
    internal override int RewindFloats => 1;
    internal override void SaveRewind(Span<float> into) => into[0] = _gauge;
    internal override void LoadRewind(ReadOnlySpan<float> from)
    {
        if (IsLocal) _gauge = MathF.Min(GaugeLength, MathF.Max(0f, from[0]));
    }
    internal float GaugeMax => GaugeLength;
    internal ArchitectDrone Drone => _drone;

    public override void OnAssigned()
    {
        ArchitectDeskArt.Prewarm();
        ArchitectDrone.Prewarm();
        if (!_listening) { TrickWall.Placed += OnWallPlaced; _listening = true; }
        _live++;
        RubbleBlocks.Pickable = () => _live > 0;
        RubbleBlocks.CrumblePickable = () => _live > 0 && OwnRubble;
        All.Add(this);
        TerrainPermits.Lent = Lends;
        _gauge = GaugeLength;
        _dt = Time.fixedDeltaTime;
        AddAbility(new Ability(new Text("Architect.Desk"), "desk")
        {
            Cooldown = () => DeskCooldown,
            MaxUses = () => DeskCount,
            OnUse = PlaceHere,
        });
        _plan = AddAbility(new Ability(new Text("Architect.Plan"), "blueprint")
        {
            CooldownAtStart = false,
            OnUse = OpenPlan,
        });
        Lifespan.OnRelease(() =>
        {
            _live--;
            All.Remove(this);
            _drone?.Destroy();
            _drone = null;
            foreach (var d in _desks) ArchitectDeskArt.Remove(d);
            _desks.Clear();
            _queue.Clear();
            _user = NoUser;
            if (ArchitectPlan.Owner == this) ArchitectPlan.Close();
        });
    }

    private static Architect Of(PlayerControl sender) => sender == null ? null : RoleState.Of(sender.PlayerId) as Architect;

    // 自分の番号 (役職が無い人もいるので本編の自分の人から)
    private static byte LocalId
    {
        get
        {
            var lp = PlayerControl.LocalPlayer;
            return lp ? lp.PlayerId : NoUser;
        }
    }

    // この人がいずれかの構築士の設計台を使っているか (全員の端末で同じ電文から決まる)
    private static bool Lends(byte playerId) => UsedBy(playerId) != null;

    private static Architect UsedBy(byte playerId)
    {
        foreach (var a in All)
            if (a._user == playerId) return a;
        return null;
    }

    private static Architect UsedRecentlyBy(byte playerId)
    {
        var a = UsedBy(playerId);
        if (a != null) return a;
        float now = Time.time;
        foreach (var b in All)
            if (b._lastUser == playerId && now - b._lastUserAt < UserGrace) return b;
        return null;
    }

    private static void OnWallPlaced(string name, Vector2 at, float length, byte actor)
    {
        ArchitectPlan.MarkDirty();
        if (_wallsGen != GameClock.ShipGen) { Walls.Clear(); _wallsGen = GameClock.ShipGen; }
        // 台を使った人の壁は、その台の構築士の壁として崩す。構築士の端末はその分のゲージを減らす (継ぎ目の重なりは払わない)
        var a = RoleState.Of(actor) as Architect ?? UsedRecentlyBy(actor);
        if (a == null) return;
        if (a.PlayerId != actor)
        {
            a._lent++;
            if (a.IsLocal) a._gauge = MathF.Max(0f, a._gauge - MathF.Max(0f, length - Joint));
        }
        a.DroneTo(at.x, at.y);
        Walls.Add(new Built { Name = name, At = at, Length = length, Since = Time.time, Owner = a.PlayerId });
    }

    // 壊れた・崩れた (全員の端末で)。設計図を開いていれば焼き直す
    private void OnTerrainBroken(TerrainBrokenEvent e)
    {
        if (IsLocal) ArchitectPlan.MarkDirty();
        if (e.PlayerId == PlayerId && e.Kind == DamageKind.Water) DroneTo(e.Position.x, e.Position.y);
        // 爆発が台に掛かると壊れる。地形の結果と同じ順に全員の端末へ届くので、全員が同じ台を壊す
        if (e.Kind != DamageKind.Explosion || e.Radius <= 0f) return;
        Vector2 c = e.Position;
        for (int i = 0; i < _desks.Count; i++)
        {
            var d = _desks[i];
            if (d.Broken) continue;
            Vector2 q = ClosestOnFoot(d, c.x, c.y);
            float dx = q.x - c.x, dy = q.y - c.y;
            if (dx * dx + dy * dy > e.Radius * e.Radius) continue;
            float ox = d.X - c.x, oy = d.Y + 0.4f - c.y, len = MathF.Sqrt(ox * ox + oy * oy);
            Vector2 away = len > 1e-3f ? FxMath.V2(ox / len, oy / len) : FxMath.V2(0f, 1f);
            BreakDesk((byte)i, away, c, (ushort)(PlayerId * 31 + i * 7 + 101));
        }
    }

    // 壁・配管の所へドローンを飛ばす。真上だと影が壁の絵に隠れるので、手前 (画面の下) の床の上で止まる
    private const float DroneFront = 0.6f;
    private void DroneTo(float x, float y)
    {
        if (_drone == null || _drone.Done) return;
        float tx = x, ty = y - DroneFront;
        if (SolidMap.Valid && SolidMap.Solid(tx, ty) && SolidMap.NearestFloor(x, y, 1.5f, 0.3f, out var floor)) { tx = floor.x; ty = floor.y; }
        _drone.Visit(tx, ty);
    }

    // ---- 設計図の開け閉めとドローン (全員の端末) ----

    private void OnPlan(byte desk)
    {
        if (desk == ClosedDesk)
        {
            _openDesk = -1;
            RefreshDrone();
            return;
        }
        if (desk >= _desks.Count || _desks[desk].Broken) return;
        _openDesk = desk;
        // 構築士が開き直したら、台を使っていた人は降ろされる
        if (_user != NoUser) EndUse();
        RefreshDrone();
    }

    // 全員: ほかの人が設計台を使い始めた・やめた。構築士が開いていれば閉じる (閉じた知らせは持ち主の SyncPlan が配る)
    private void OnUse(PlayerControl sender, byte desk)
    {
        if (!sender || sender.PlayerId == PlayerId) return;
        byte id = sender.PlayerId;
        if (desk == ClosedDesk)
        {
            if (_user == id) { ForgetUser(); RefreshDrone(); }
            return;
        }
        if (desk >= _desks.Count || _desks[desk].Broken || sender.Data == null || sender.Data.IsDead || sender.Data.Disconnected || MeetingHud.Instance) return;
        if (_user != NoUser && _user != id) EndUse();
        _user = id;
        _userDesk = desk;
        _openDesk = -1;
        if (IsLocal)
        {
            if (ArchitectPlan.Owner == this) ArchitectPlan.Close();
            GaugeCall.Send((byte)Math.Clamp((int)(_gauge * GaugeQuant), 0, 255));
        }
        RefreshDrone();
    }

    // 台を使っていた人を降ろす (その人の端末なら設計図を閉じ、立てる前の線は捨てる)。
    // 降ろされた本人もやめたと配る (使い始めと開き直しがすれ違うと、ほかの端末に使用中のまま残るため)
    private void EndUse()
    {
        bool mine = _user == LocalId;
        ForgetUser();
        if (!mine) return;
        _queue.Clear();
        if (ArchitectPlan.Owner == this) ArchitectPlan.Close();
        UseCall.Send((PlayerId, ClosedDesk));
    }

    private void ForgetUser()
    {
        if (_user != NoUser) { _lastUser = _user; _lastUserAt = Time.time; }
        _user = NoUser;
        _userDesk = -1;
    }

    // 台を使っている人の端末: 構築士から届いた今のゲージを写す
    private void OnGauge(byte q)
    {
        if (IsLocal || _user != LocalId) return;
        _gauge = MathF.Min(GaugeLength, q / GaugeQuant);
    }

    // ドローンは設計台が使われている間 (構築士でもほかの人でも) その台から飛ぶ
    private void RefreshDrone()
    {
        int want = _openDesk >= 0 ? _openDesk : _userDesk;
        if (want < 0 || want >= _desks.Count || _desks[want].Broken) { _drone?.Close(); _droneDesk = -1; return; }
        if (_drone != null && _drone.Airborne && _droneDesk == want) return;
        _drone?.Destroy();
        var d = _desks[want];
        _drone = ArchitectDrone.Launch(d.X, d.Y);
        _droneDesk = want;
    }

    // ほかの人の端末: 足元から使える設計台 (いちばん近い構築士の台)。無ければ false
    private static bool FindUsable(out Architect owner, out int desk)
    {
        owner = null;
        desk = -1;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead || !lp.CanMove || lp.inVent || lp.onLadder || lp.inMovingPlat) return false;
        if (MeetingHud.Instance || ArchitectPlan.IsOpen) return false;
        Vector2 me = lp.GetTruePosition();
        float bestD = float.MaxValue;
        foreach (var a in All)
        {
            if (a.PlayerId == lp.PlayerId) continue;
            var h = a.DeskInReach(me.x, me.y);
            if (h == null) continue;
            float dx = me.x - h.X, dy = me.y - h.Y, dd = dx * dx + dy * dy;
            if (dd >= bestD) continue;
            bestD = dd;
            owner = a;
            desk = a._desks.IndexOf(h);
        }
        return owner != null && desk >= 0;
    }

    private static bool UseNearest() => FindUsable(out var a, out int desk) && a.BeginUse(desk);

    // ほかの人の端末: 設計台で設計図を開く。ゲージは構築士から届くまで 0 (引けない)
    private bool BeginUse(int desk)
    {
        _gauge = 0f;
        _queue.Clear();
        if (!ArchitectPlan.Open(this)) return false;
        OnUse(PlayerControl.LocalPlayer, (byte)desk);
        UseCall.Send((PlayerId, (byte)desk));
        return true;
    }

    // 台を使っている人の端末: なぞった線を壁にする。閉じたら、立てる途中の壁を立て終えてからやめたと配る
    private void BorrowTick()
    {
        bool open = ArchitectPlan.Owner == this;
        if (open) ArchitectPlan.Tick();
        BuildNext();
        float max = GaugeLength;
        if (_gauge < max) _gauge = MathF.Min(max, _gauge + max / MathF.Max(1f, GaugeRefill) * _dt);
        if (open || _queue.Count > 0) return;
        OnUse(PlayerControl.LocalPlayer, ClosedDesk);
        UseCall.Send((PlayerId, ClosedDesk));
    }

    // 構築士でない人の端末: 設計台を使うボタン (誰かの台の手前に立った時だけ押せる)。いる構築士の 1 人目が回す
    private void TickUseButton()
    {
        if (++_useCheck < CheckEvery) return;
        _useCheck = 0;
        if (RoleState.Local is Architect)
        {
            _useLife?.Release();
            _useLife = null;
            _useButton = null;
            return;
        }
        if (_useLife == null || _useLife.IsDead)
        {
            if (!Vanilla.Hud) return;
            _useLife = Lifespan.Child();
            _useButton = ModButton.Create(_useLife, ButtonIcons.Get("blueprint"), new Text("Architect.UseDesk"), 0f, UseNearest);
        }
        _useButton?.SetUsable(FindUsable(out _, out _));
    }

    // 自分の設計図の開け閉めが変わったら配る (閉じる道は会議・死亡・ボタンなど色々あるので状態を見る)
    private void SyncPlan()
    {
        bool open = ArchitectPlan.IsOpen;
        if (open == _sentOpen) return;
        _sentOpen = open;
        byte v = open && _openDesk >= 0 ? (byte)_openDesk : ClosedDesk;
        OnPlan(v);
        PlanCall.Send(v);
    }

    // 自分: 設計図を開いている間、届く円の中の止まった瓦礫を 1 つずつ拾う頼みを出す (ゲージが満タンなら拾わない)
    private void TickPick()
    {
        if (_openDesk < 0 || _openDesk >= _desks.Count || _desks[_openDesk].Broken || (_pickT -= _dt) > 0f) return;
        _pickT = PickEvery;
        if (_pendingAt >= 0f && Time.time - _pendingAt < PickTimeout) return;
        _pendingAt = -1f;
        if (_gauge >= GaugeLength - 0.01f) return;
        var d = _desks[_openDesk];
        var b = RubbleBlocks.NearestFree(d.X, d.Y, Reach);
        if (b == null) return;
        _pendingAt = Time.time;
        PickCall.Send(FxMath.V2(b.X, b.Y));
    }

    // ホスト: 開いている設計台から届く所の、まだ拾われていない瓦礫か確かめて全員へ配る
    private void HostPick(Vector2 at)
    {
        var pc = Player;
        if (!pc || pc.Data == null || pc.Data.IsDead || _openDesk < 0 || _openDesk >= _desks.Count || _desks[_openDesk].Broken || MeetingHud.Instance) return;
        if (Time.time - _hostPickAt < HostPickGap || RubbleBlocks.FreeAt(at.x, at.y) == null) return;
        var d = _desks[_openDesk];
        float dx = at.x - d.X, dy = at.y - d.Y;
        if (dx * dx + dy * dy > Reach * Reach) return;
        _hostPickAt = Time.time;
        OnPicked(at);
        PickedCall.Send((PlayerId, at));
    }

    // 全員: 拾うと決まった。ドローンが着いた時に瓦礫を消す (飛んでいなければすぐ)。持ち主はゲージを戻す
    private void OnPicked(Vector2 at)
    {
        var b = RubbleBlocks.FreeAt(at.x, at.y);
        if (b == null) return;
        b.Taken = true;
        if (IsLocal)
        {
            _gauge = Math.Min(GaugeLength, _gauge + RubbleGain);
            _picked++;
            _pendingAt = -1f;
        }
        if (_drone != null && !_drone.Done) _drone.Visit(b.X, b.Y, () => RubbleBlocks.Remove(b));
        else RubbleBlocks.Remove(b);
    }

    // ---- ハンマーで設計台を壊す ----

    // 足元 at から aim の左右 60° 以内で、maxDist までに届く設計台のいちばん近い当たり (face = 当たる点・toward = 打つ向き)
    internal static bool HammerTarget(Vector2 at, Vector2 aim, float maxDist, out byte owner, out byte desk, out Vector2 face, out Vector2 toward)
    {
        owner = 0; desk = 0; face = toward = default;
        float best = float.MaxValue;
        foreach (var a in All)
        {
            for (int i = 0; i < a._desks.Count; i++)
            {
                var d = a._desks[i];
                if (d.Broken) continue;
                Vector2 q = ClosestOnFoot(d, at.x, at.y);
                float dx = q.x - at.x, dy = q.y - at.y, len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1e-3f || len > maxDist || len >= best || (dx * aim.x + dy * aim.y) < 0.5f * len) continue;
                if (!OpenBetween(at.x, at.y, q.x, q.y)) continue;
                best = len;
                owner = a.PlayerId;
                desk = (byte)i;
                face = q;
                toward = FxMath.V2(dx / len, dy / len);
            }
        }
        return best < float.MaxValue;
    }

    internal static void SendHammer(byte owner, byte desk) => HitCall.Send((owner, desk));

    // 台の当たり判定の四角で (px, py) にいちばん近い点
    private static Vector2 ClosestOnFoot(ArchitectDeskArt.Handle d, float px, float py) =>
        FxMath.V2(Math.Clamp(px, d.X - ArchitectDeskArt.FootHalfW, d.X + ArchitectDeskArt.FootHalfW),
            Math.Clamp(py, d.Y + ArchitectDeskArt.FootFront, d.Y + ArchitectDeskArt.FootBack));

    // 二点の間に壁が無いか (歩ける所の地図で・終わりの点は台の縁なので見ない)
    private static bool OpenBetween(float ax, float ay, float bx, float by)
    {
        if (!SolidMap.Valid) return true;
        float dx = bx - ax, dy = by - ay;
        int n = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dy * dy) / FloorProbe));
        for (int i = 1; i < n; i++)
        {
            float t = i / (float)n;
            if (SolidMap.Solid(ax + dx * t, ay + dy * t)) return false;
        }
        return true;
    }

    // ホスト: 叩いた人が本当に台のそばにいてハンマーを持っていれば 1 回と数えて全員へ配る
    private void HostHit(PlayerControl sender, byte desk)
    {
        if (desk >= _desks.Count || _desks[desk].Broken || !sender || sender.Data == null || sender.Data.IsDead || !GameClock.ShipAlive || MeetingHud.Instance) return;
        var d = _desks[desk];
        Vector2 p = sender.GetTruePosition();
        Vector2 q = ClosestOnFoot(d, p.x, p.y);
        float dx = q.x - p.x, dy = q.y - p.y;
        float reach = DamageProfile.Of(DamageKind.Blunt).Reach + HammerSlack;
        if (dx * dx + dy * dy > reach * reach || !OpenBetween(p.x, p.y, q.x, q.y)) return;
        // ハンマーを持っている人だけ・ハンマーの待ち時間ごとに 1 回 (続けて送られても数えない)
        if (TerrainPermits.TryUse(sender.PlayerId, TerrainUse.Hammer, 0f, host: true) != null) return;
        byte hits = (byte)Math.Min(DeskHits, d.Hits + 1);
        HitsCall.Send((PlayerId, desk, sender.PlayerId, hits));
        ShowHit(desk, sender.PlayerId, hits);
    }

    // 全員: 叩かれた (他の人の振りはここで見せる)。DeskHits 回目で壊れ、使っていた人は降ろされる
    private void ShowHit(byte desk, byte hitter, byte hits)
    {
        if (desk >= _desks.Count || _desks[desk].Broken) return;
        var d = _desks[desk];
        d.Hits = hits;
        var info = GameData.Instance ? GameData.Instance.GetPlayerById(hitter) : null;
        var obj = info != null ? info.Object : null;
        Vector2 at = obj ? obj.GetTruePosition() : FxMath.V2(d.X, d.Y - 1f);
        Vector2 face = ClosestOnFoot(d, at.x, at.y);
        float dx = face.x - at.x, dy = face.y - at.y, len = MathF.Max(1e-3f, MathF.Sqrt(dx * dx + dy * dy));
        Vector2 toward = FxMath.V2(dx / len, dy / len);
        if (obj && hitter != LocalId) HammerSwing.PlayOther(face, toward, info.DefaultOutfit.ColorId);
        ushort seed = (ushort)(PlayerId * 31 + desk * 7 + hits);
        if (hits < DeskHits) { ArchitectDeskArt.Crack(d, face, toward, seed); return; }
        BreakDesk(desk, toward, at, seed);
    }

    // 全員: 台が壊れた。使っていた人は降ろされる (dir = 欠片が飛ぶ向き・home = 壊した側の床の点)
    private void BreakDesk(byte desk, Vector2 dir, Vector2 home, ushort seed)
    {
        ArchitectDeskArt.Break(_desks[desk], dir, home, seed);
        if (_openDesk == desk)
        {
            if (IsLocal && ArchitectPlan.Owner == this) ArchitectPlan.Close(); // 閉じた知らせは SyncPlan が配る
            _openDesk = -1;
        }
        // 借りた人は設計図を閉じるだけ。離し終えた線の壁は立て終えてから BorrowTick がやめたと配る (それまで壁の許可は貸したまま)
        if (_userDesk == desk && _user == LocalId && ArchitectPlan.Owner == this) ArchitectPlan.Close();
        RefreshDrone();
        if (IsLocal) ArchitectPlan.MarkDirty();
    }

    private bool PlaceHere()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.inVent || lp.onLadder || lp.inMovingPlat) return false;
        Vector2 me = lp.GetTruePosition();
        float x = me.x, y = me.y + DeskBack;
        if (Crew.PortalMaker.NearFixture(FxMath.V2(x, y)) || (SolidMap.Valid && SolidMap.Solid(me.x, me.y))) return false;
        string why = Blocked(x, y);
        _lastDesk = why ?? "placed";
        if (why != null) { Plugin.Logger.LogInfo($"Architect: desk refused {why}"); return false; }
        if (!TryAdd(x, y)) return false;
        DeskCall.Send(FxMath.V2(x, y));
        return true;
    }

    private bool TryAdd(float x, float y)
    {
        if (_desks.Count >= DeskCount || !float.IsFinite(x) || !float.IsFinite(y)) return false;
        foreach (var d in _desks)
        {
            if (d.Broken) continue;
            float dx = x - d.X, dy = y - d.Y;
            if (dx * dx + dy * dy < MinGap * MinGap) return false;
        }
        var h = ArchitectDeskArt.Place(x, y);
        if (h == null) return false;
        _desks.Add(h);
        PushOut(x, y);
        return true;
    }

    // 置く人の端末だけ: 脚の下の四角が床の上で、人が立っておらず、脇を通れるか (理由・よければ null)。
    // 人の位置は端末ごとに違うので受け手は見ない (受け手は掛かっていた自分の体を出すだけ)
    private static string Blocked(float x, float y)
    {
        const float hw = ArchitectDeskArt.FootHalfW, f = ArchitectDeskArt.FootFront, bk = ArchitectDeskArt.FootBack;
        if (!SolidMap.Ensure() || !SolidMap.Valid) return "no map";
        for (int i = -1; i <= 1; i++)
            for (int j = 0; j <= 2; j++)
                if (!TrickWall.FreeForPushWall(FxMath.V2(x + hw * i, y + f + (bk - f) * 0.5f * j))) return "not on floor";
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc || pc.Data == null || pc.Data.IsDead) continue;
            Vector2 q = pc.GetTruePosition();
            if (MathF.Abs(q.x - x) < hw + BodyClear && q.y > y + f - BodyClear && q.y < y + bk + BodyClear) return "someone in the way";
        }
        // 左右の両方、または上下の両方が狭いと通り道を塞ぐ
        float mid = y + (f + bk) * 0.5f;
        if (Gap(x - hw, mid, -1f, 0f) < PassGap && Gap(x + hw, mid, 1f, 0f) < PassGap) return "blocks the way";
        if (Gap(x, y + bk, 0f, 1f) < PassGap && Gap(x, y + f, 0f, -1f) < PassGap) return "blocks the way";
        return null;
    }

    // (x, y) から (dx, dy) の向きに、壁か物に当たる手前まで空いている床の長さ (PassGap まで空いていれば PassGap)
    private static float Gap(float x, float y, float dx, float dy)
    {
        for (float d = GapStep; d <= PassGap + 1e-4f; d += GapStep)
        {
            float px = x + dx * d, py = y + dy * d;
            if (SolidMap.Solid(px, py)) return d - GapStep;
            foreach (var col in Physics2D.OverlapCircleAll(FxMath.V2(px, py), 0.02f, BlockMask))
                if (col && !col.isTrigger) return d - GapStep;
        }
        return PassGap;
    }

    // 置かれた設計台に自分の体が掛かっていたら (届くまでの間に歩いて入った)、出る距離のいちばん短い辺の外へ出す。
    // 動かすのは自分の体だけ。はしご・昇降機・ジップラインの途中と会議中は本編が体を動かしているので触らない
    private static void PushOut(float x, float y)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead || lp.inVent || lp.inMovingPlat || lp.onLadder || MeetingHud.Instance) return;
        Vector2 me = lp.GetTruePosition();
        float l = x - ArchitectDeskArt.FootHalfW - BodyClear, r = x + ArchitectDeskArt.FootHalfW + BodyClear;
        float b = y + ArchitectDeskArt.FootFront - BodyClear, t = y + ArchitectDeskArt.FootBack + BodyClear;
        if (me.x <= l || me.x >= r || me.y <= b || me.y >= t) return;
        float tx = l - 0.05f, ty = me.y, best = me.x - l;
        if (r - me.x < best) { best = r - me.x; tx = r + 0.05f; }
        if (me.y - b < best) { best = me.y - b; tx = me.x; ty = b - 0.05f; }
        if (t - me.y < best) { tx = me.x; ty = t + 0.05f; }
        if (SolidMap.Ensure() && SolidMap.Valid && SolidMap.Solid(tx, ty))
        {
            if (!SolidMap.NearestFloor(tx, ty, 1.5f, 0.2f, out var floor)) return;
            tx = floor.x; ty = floor.y;
        }
        var root = lp.transform.position;
        Teleport.Self(FxMath.V2(tx - (me.x - root.x), ty - (me.y - root.y)));
    }

    // 自分の足元から使える設計台 (いちばん近い 1 つ)。無ければ null
    private ArchitectDeskArt.Handle DeskInReach(float px, float py)
    {
        ArchitectDeskArt.Handle best = null;
        float bestD = float.MaxValue;
        foreach (var d in _desks)
        {
            if (d.Broken) continue;
            float dx = px - d.X, dy = py - d.Y;
            if (MathF.Abs(dx) > UseHalfW || dy > UseBack || dy < -UseFront) continue;
            float dd = dx * dx + dy * dy;
            if (dd < bestD) { bestD = dd; best = d; }
        }
        return best;
    }

    private bool CanPlan(PlayerControl lp)
    {
        if (!lp || lp.Data == null || lp.Data.IsDead || !lp.CanMove || lp.inVent || lp.onLadder || lp.inMovingPlat) return false;
        if (MeetingHud.Instance || ArchitectPlan.IsOpen) return false;
        Vector2 me = lp.GetTruePosition();
        return DeskInReach(me.x, me.y) != null;
    }

    private bool OpenPlan()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!CanPlan(lp)) return false;
        Vector2 me = lp.GetTruePosition();
        int desk = _desks.IndexOf(DeskInReach(me.x, me.y));
        if (desk < 0 || !ArchitectPlan.Open(this)) return false;
        _openDesk = desk;
        return true;
    }

    // ---- ゲージと壁 (自分の端末) ----

    // なぞった長さの分だけゲージを使う。足りなければ使わずに false
    internal bool TrySpend(float length)
    {
        if (_gauge < length) return false;
        _gauge -= length;
        return true;
    }

    // なぞり終えた線 (世界の座標の点の列) を、床の上を通る区間ごとに壁へ切って順番待ちへ
    // (壁の中・机・端末・ベント・扉などに掛かる所には立てない)。
    // 使った長さとの差 (壁の中を通った分など) は戻す
    internal void Build(List<Vector2> pts, float spent)
    {
        float used = 0f;
        var run = new List<Vector2>();
        for (int i = 1; i < pts.Count; i++)
        {
            Vector2 a = pts[i - 1], b = pts[i];
            int n = Math.Max(1, (int)MathF.Ceiling(Dist(a, b) / FloorProbe));
            for (int k = i == 1 ? 0 : 1; k <= n; k++)
            {
                float t = k / (float)n, qx = a.x + (b.x - a.x) * t, qy = a.y + (b.y - a.y) * t;
                var q = FxMath.V2(qx, qy);
                if (TrickWall.FreeForPushWall(q)) run.Add(q);
                else { used += Chunk(run); run.Clear(); }
            }
        }
        used += Chunk(run);
        // 端の重なりと最短の長さの分は払わない。払いすぎた分は戻す
        float back = spent - Math.Min(spent, used);
        _gauge = Math.Min(GaugeLength, _gauge + back);
    }

    // 床の上の 1 続きの点の列を、直線に近い区間ごとの壁に切る。切った長さの合計
    private float Chunk(List<Vector2> pts)
    {
        if (pts.Count < 2) return 0f;
        float used = 0f;
        int s = 0;
        for (int i = 2; i < pts.Count; i++)
        {
            if (Dist(pts[s], pts[i]) <= ChunkMax && MaxBend(pts, s, i) <= ChunkBend) continue;
            used += Enqueue(pts[s], pts[i - 1]);
            s = i - 1;
        }
        return used + Enqueue(pts[s], pts[^1]);
    }

    private float Enqueue(Vector2 a, Vector2 b)
    {
        float len = Dist(a, b);
        if (len < 0.05f) return 0f;
        float dx = (b.x - a.x) / len, dy = (b.y - a.y) / len;
        float wall = Math.Clamp(len + Joint, ChunkMin, TerrainPermit.MaxWallLength);
        _queue.Enqueue((FxMath.V2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f), FxMath.V2(dx, dy), wall));
        return len;
    }

    private static float Dist(Vector2 a, Vector2 b)
    {
        float dx = b.x - a.x, dy = b.y - a.y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // 点 s から e の直線から、間の点がいちばん離れている距離
    private static float MaxBend(List<Vector2> pts, int s, int e)
    {
        Vector2 a = pts[s], b = pts[e];
        float dx = b.x - a.x, dy = b.y - a.y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-4f) return 0f;
        float worst = 0f;
        for (int k = s + 1; k < e; k++)
            worst = MathF.Max(worst, MathF.Abs((pts[k].x - a.x) * dy - (pts[k].y - a.y) * dx) / len);
        return worst;
    }

    // 設計図を軽く押した (世界の座標)。水回りの部屋の中で、近くの壁の面の配管を裂く。裂けたら true
    internal bool Burst(Vector2 at)
    {
        float cost = PipeCost;
        // 台を借りた人は配管を裂けない (壁だけ)
        if (!IsLocal) { _lastPipe = "not owner"; return false; }
        if (_gauge < cost || !SolidMap.Valid || SolidMap.Solid(at.x, at.y) || !WaterLeak.IsWaterRoom(at)) { _lastPipe = "not here"; return false; }
        // いちばん近い壁の面 (歩ける所の地図の縁) を 16 方向に探す
        float best = float.MaxValue, bx = 0f, by = 0f;
        for (int k = 0; k < PipeDirs; k++)
        {
            float a = k * MathF.PI * 2f / PipeDirs, dx = MathF.Cos(a), dy = MathF.Sin(a);
            for (float d = PipeStep; d <= PipeSearch && d < best; d += PipeStep)
            {
                if (!SolidMap.Solid(at.x + dx * d, at.y + dy * d)) continue;
                best = d; bx = dx; by = dy;
                break;
            }
        }
        if (best == float.MaxValue) { _lastPipe = "no wall near"; return false; }
        var face = FxMath.V2(at.x + bx * (best + PipeInto), at.y + by * (best + PipeInto));
        var res = TerrainApi.BurstPipe(face, FxMath.V2(-bx, -by));
        _lastPipe = res.Ok ? $"burst ({face.x:0.00},{face.y:0.00})" : $"refused {res.Why}";
        if (!res.Ok) return false;
        _gauge -= cost;
        _pipes++;
        ArchitectPlan.MarkDirty();
        return true;
    }

    private void BuildNext()
    {
        if (_queue.Count == 0 || Time.time < _nextBuild) return;
        var w = _queue.Dequeue();
        _nextBuild = Time.time + BuildGap;
        var res = TerrainApi.PushWall(w.At, w.Dir, w.Len);
        if (res.Ok) { _built++; return; }
        _refused++;
        _gauge = Math.Min(GaugeLength, _gauge + Math.Max(0f, w.Len - Joint));
        Plugin.Logger.LogInfo($"Architect: wall refused {res.Why}");
    }

    public override void OnFixedUpdate()
    {
        if (IsLocal) LocalTick();
        else if (_user != NoUser && _user == LocalId) BorrowTick();
        if (All.Count > 0 && All[0] == this) TickUseButton();
        if (++_hostCheck >= HostEvery)
        {
            _hostCheck = 0;
            if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost) CrumbleOld();
            // 持ち主が抜けると閉じた知らせが来ないので、他の端末は自分でドローンを降ろす
            if (!IsLocal && _openDesk >= 0)
            {
                var pc = Player;
                if (!pc || pc.Data == null || pc.Data.IsDead || pc.Data.Disconnected) OnPlan(ClosedDesk);
            }
            // 台を使っていた人が抜けた・死んだ時も同じ (自分なら設計図が閉じて BorrowTick が配る)
            if (_user != NoUser && _user != LocalId)
            {
                var u = GameData.Instance ? GameData.Instance.GetPlayerById(_user) : null;
                if (u == null || u.IsDead || u.Disconnected) { EndUse(); RefreshDrone(); }
            }
        }
    }

    private void LocalTick()
    {
        ArchitectPlan.Tick();
        SyncPlan();
        TickPick();
        BuildNext();
        float max = GaugeLength;
        if (_gauge < max) _gauge = MathF.Min(max, _gauge + max / MathF.Max(1f, GaugeRefill) * _dt);
        if (++_check < CheckEvery) return;
        _check = 0;
        var b = _plan?.Button;
        if (b == null) return;
        b.SetUsable(CanPlan(PlayerControl.LocalPlayer));
        int shown = (int)_gauge;
        if (shown != _shownGauge) { _shownGauge = shown; b.SetUses(shown); }
    }

    // ホスト: この構築士の壁で、立ってから時間が経った物を崩す (先に壊れていれば崩す依頼は何もしない)
    private void CrumbleOld()
    {
        if (_wallsGen != GameClock.ShipGen || Walls.Count == 0 || MeetingHud.Instance) return;
        float now = Time.time, life = WallLife;
        for (int i = Walls.Count - 1; i >= 0; i--)
        {
            var w = Walls[i];
            if (w.Owner != PlayerId || now - w.Since < life) continue;
            // 先に壊れた壁は忘れる。崩れの依頼は真ん中の点の物に当たるので、別の壁が真ん中に重なっている間は次の周期に回す
            if (!BreakableProps.Intact(w.Name)) { Walls.RemoveAt(i); continue; }
            if (BreakableProps.IntactNameAt(w.At) != w.Name) continue;
            Walls.RemoveAt(i);
            // 崩す途中で投げても、この役職の毎刻の処理 (設計図を閉じた知らせ・ドローンを降ろす・ボタン) は止めない。その壁は崩さずに忘れる
            try
            {
                var res = TerrainApi.WorldCrumble(w.At, w.Length);
                if (!res.Ok) Plugin.Logger.LogInfo($"Architect: crumble skipped {res.Why}");
            }
            catch (Exception e) { Plugin.Logger.LogError($"Architect: crumble {w.Name}: {e}"); }
        }
    }

    internal string Describe()
    {
        var sb = new System.Text.StringBuilder($"desks={_desks.Count}/{(int)DeskCount} gauge={_gauge:0.0}/{(float)GaugeLength:0} queue={_queue.Count} built={_built} refused={_refused} walls={Walls.Count} pipes={_pipes} pipe={_lastPipe} desk={_lastDesk} picked={_picked} user={(_user == NoUser ? "-" : _user.ToString())}@{_userDesk} lent={_lent} drone={(_drone == null ? "-" : _drone.Describe())} plan={ArchitectPlan.Describe()}");
        foreach (var d in _desks) sb.Append($" ({d.X:0.00},{d.Y:0.00}{(d.Broken ? " broken" : d.Hits > 0 ? $" hits={d.Hits}" : "")})");
        sb.Append(" rubble=").Append(RubbleBlocks.DescribeKept());
        return sb.ToString();
    }

    // テスト用: 自分の端末で (x, y) に置いた扱いにして全員へ配る
    internal bool PlaceForTest(float x, float y)
    {
        if (!TryAdd(x, y)) return false;
        DeskCall.Send(FxMath.V2(x, y));
        return true;
    }

    // テスト用: 能力ボタンと同じ道で自分の足元に置く (待ち時間と回数は使わない)
    internal bool HereForTest() => PlaceHere();

    // テスト用: 設計図を開かずに自分の端末だけでドローンを飛ばす (見た目の確認用) / 行き先を足す / 降ろす
    internal bool FlyForTest()
    {
        if (_desks.Count == 0) return false;
        OnPlan(0);
        return _drone != null;
    }

    internal bool VisitForTest(float x, float y)
    {
        if (_drone == null || _drone.Done) return false;
        _drone.Visit(x, y);
        return true;
    }

    internal void LandForTest() => OnPlan(ClosedDesk);

    // テスト用: 床の点 (x, y) に置けるか (理由・よければ null)
    internal static string CheckForTest(float x, float y) => Blocked(x, y);

    // テスト用: 自分の足元を設計台 i の手前へ移す
    internal bool GoForTest(int i)
    {
        if (i < 0 || i >= _desks.Count) return false;
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return false;
        Vector2 me = lp.GetTruePosition();
        var root = lp.transform.position;
        Teleport.Self(FxMath.V2(_desks[i].X + (root.x - me.x), _desks[i].Y - DeskBack + (root.y - me.y)));
        return true;
    }

    internal bool OpenForTest() => OpenPlan();

    // テスト用: 自分 (構築士でない人) が足元の設計台を使う (ボタンと同じ道)
    internal static bool UseForTest() => UseNearest();

    // テスト用: この端末だけで、ほかの人の構築士の設計台を (x, y) に置いた扱いにする
    internal bool PlaceLocalForTest(float x, float y) => TryAdd(x, y);

    // テスト用: 番号の人が設計台 desk を使い始めた (ClosedDesk = やめた) と届いた扱いにする (この端末だけ)
    internal bool SimUseForTest(byte id, byte desk)
    {
        var pc = GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;
        if (!pc) return false;
        OnUse(pc, desk);
        return true;
    }

    // テスト用: 台 desk が hits 回目を叩かれたと届いた扱いにする (この端末だけ・hitter = 叩いた人)
    internal bool SmashForTest(byte desk, byte hitter, byte hits)
    {
        if (desk >= _desks.Count || _desks[desk].Broken) return false;
        ShowHit(desk, hitter, hits);
        return true;
    }

    // テスト用: 世界の座標の点の列をなぞった扱いにする (ゲージも使う)。使えた長さ
    internal float DrawForTest(List<Vector2> pts)
    {
        var kept = new List<Vector2> { pts[0] };
        float spent = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            float len = Dist(kept[^1], pts[i]);
            if (!TrySpend(len)) break;
            spent += len;
            kept.Add(pts[i]);
        }
        if (kept.Count >= 2) Build(kept, spent);
        return spent;
    }
}
