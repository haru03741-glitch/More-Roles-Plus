// Ported from https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Roles/Neutral/Spectre.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// スペクター (第三陣営・妖狐): 時間とともにお腹が減り、満腹度が 0 になると死ぬ。マップの皿のお揚げを食べると満腹度が 1 増える。
// 「消える」で満腹度を使って少しの間透明になる。キル役の居場所が矢印で分かる (設定)。インポスターの視界で、停電の影響を受けず、
// 壁越しにも見える (設定)。試合の終わりに生きていて満腹度が決めた値より多ければ、勝ちを奪う (破壊工作での負けは除く)。
// 子狐・背徳者 (設定) が仲間に付き、スペクターが死ぬと後を追う
public sealed class Spectre : RoleBase
{
    public const string Hex = "#B998C5";

    public override Team BaseTeam => Team.Neutral;
    public override string Color => Hex;
    public override Text Name => new("Spectre.Name");
    public override string Reading => "ようこ";
    public override Text Blurb => new("Spectre.Blurb");
    public override string Origin => "Nebula";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Spectre.Vanish", "spectre_vanish"), new("Spectre.Eat", "spectre_eat") };

    public static readonly BoolOpt Vent = new("Spectre.Vent", true);
    public static readonly BoolOpt ShowKillers = new("Spectre.ShowKillers", true);
    public static readonly BoolOpt Clairvoyance = new("Spectre.Clairvoyance", true);
    public static readonly ChoiceOpt Immoralist = new("Spectre.Immoralist", 0,
        new Text("Spectre.Immoralist.None"), new Text("Spectre.Immoralist.Follower"), new Text("Spectre.Immoralist.Immoralist"),
        new Text("Spectre.Immoralist.Random"), new Text("Spectre.Immoralist.Alternate"));
    public static readonly IntOpt FollowerChance = new("Spectre.FollowerChance", 50, 10, 90) { When = () => Immoralist.Value == 3 };
    public static readonly IntOpt NumOfDishes = new("Spectre.NumOfDishes", 6, 0, 20);
    public static readonly FloatOpt SatietyRate = new("Spectre.SatietyRate", 45f, 20f, 100f, 2.5f, "s");
    public static readonly FloatOpt MaxSatiety = new("Spectre.MaxSatiety", 5f, 1f, 8f, 0.25f, "x");
    public static readonly FloatOpt InitialSatiety = new("Spectre.InitialSatiety", 4f, 0.5f, 8f, 0.25f, "x");
    public static readonly FloatOpt RequiredSatiety = new("Spectre.RequiredSatiety", 4f, 0f, 7.75f, 0.25f, "x");
    public static readonly BoolOpt ShowDishesOnMap = new("Spectre.ShowDishesOnMap", true);
    public static readonly FloatOpt ReplenishCooldown = new("Spectre.Replenish", 80f, 20f, 240f, 5f, "s");
    public static readonly FloatOpt VanishCost = new("Spectre.VanishCost", 0.5f, 0f, 2f, 0.25f, "x");
    public static readonly FloatOpt VanishCooldown = new("Spectre.VanishCooldown", 20f, 0f, 60f, 2.5f, "s");
    private const float CloakSoundRange = 6f;

    // 消える/現れる時の音 (どの端末でも・透明化を配る電文の受け手から)。現れる時は本人がまだ見えないので場所だけの音にする
    internal static void OnCloak(byte id, bool on)
    {
        var p = PortKit.ById(id);
        if (!p || p.inVent || RoleState.Of(p) is not Spectre || !PortKit.Alive(p)) return;
        Vector2 at = p.GetTruePosition();
        Fx.Hearing.At(on ? "spectre_vanish" : "spectre_appear", at.x, at.y, CloakSoundRange, on ? 0.7f : 0.6f, on ? p : null);
    }

    public static readonly FloatOpt VanishDuration = new("Spectre.VanishDuration", 15f, 2.5f, 30f, 2.5f, "s");
    public static readonly BoolOpt Adrenaline = new("Spectre.Adrenaline", false);
    public static readonly FloatOpt AdrenalineRate = new("Spectre.AdrenalineRate", 0.25f, 0f, 0.875f, 0.125f, "x") { When = () => Adrenaline };

    public override bool? CanVent => Vent;

    internal static float Replenish => ReplenishCooldown;
    private static bool _alternate;

    private readonly Tracked<float> _left = new(); // 残りの生きられる秒 (満腹度 × 1 満腹の秒)。本人の端末が数えて配る・時戻しで戻る
    private float _shareIn;
    private int _warnSec = -1;
    private float _rate = 1f; // 減る速さ。他の端末は届いた速さで次の値まで自分で減らす
    private float _suicideAt = -10f;
    private Ability _vanish, _eat;
    private readonly Dictionary<byte, WorldArrow> _killerArrows = new();
    private readonly HashSet<byte> _team = new(); // 子狐・背徳者 (全員の端末で同じ)

    public float Satiety => _left.Value / SatietyRate;
    private bool CanWin => PortKit.Alive(Player) && Satiety > RequiredSatiety;

    // 自分の端末が透視しているか (視界の影を作らない)
    internal static bool LocalClairvoyant =>
        (Clairvoyance && RoleState.Local is Spectre s && PortKit.Alive(s.Player) && !MeetingHud.Instance) || MoreRolesPlus.Roles.Addons.SpectrePossession.LocalClairvoyant;

    public override void OnAssigned()
    {
        _left.Value = InitialSatiety * SatietyRate;
        _vanish = AddAbility(new Ability(new Text("Spectre.Vanish"), "spectre_vanish")
        {
            Cooldown = () => VanishCooldown,
            Duration = () => VanishDuration,
            Usable = () => VanishCost + 1f < Satiety, // 使えない間はボタンを暗くする
            OnUse = () =>
            {
                if (!(VanishCost + 1f < Satiety)) return false;
                AddSatiety(-VanishCost);
                Cloak.Self(VanishDuration);
                return true;
            },
            OnEnd = () => Cloak.Self(0f),
        });
        _eat = AddEatAbility(this, () => AddSatiety(1f));

        // スペクターと仲間は互いに分かる (名前の後ろに印)
        RoleFrame.AddExtraMark(Lifespan, seen =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || seen.PlayerId == lp.PlayerId) return null;
            bool mine = lp.PlayerId == PlayerId && _team.Contains(seen.PlayerId);
            bool spectre = _team.Contains(lp.PlayerId) && seen.PlayerId == PlayerId;
            return mine || spectre ? $"<color={Hex}>★</color>" : null;
        });

        if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost)
        {
            PortKit.After(0.5f, () => SpectreDishes.HostSpawn(NumOfDishes));
            PortKit.After(1f, AssignFollower);
        }
        if (IsLocal)
        {
            SpectreGauge.Ensure(Lifespan, MaxSatiety);
            SpectreMapLayer.ShouldShow = () => ShowDishesOnMap;
            Lifespan.OnRelease(() => { SpectreMapLayer.ShouldShow = null; _killerArrows.Clear(); });
        }
    }

    // 食べる能力 (スペクター・子狐で共通): 近くの盛った皿でお揚げを食べる画面を開く
    internal static Ability AddEatAbility(RoleBase role, System.Action onEaten) =>
        role.AddAbility(new Ability(new Text("Spectre.Eat"), "spectre_eat")
        {
            CooldownAtStart = false,
            OnUse = () =>
            {
                var lp = PlayerControl.LocalPlayer;
                var d = SpectreDishes.Nearest(lp.GetTruePosition(), x => !x.Ate);
                if (d == null) return false;
                SpectreMinigame.Open(d.Id, onEaten);
                return false;
            },
        });

    // 自分の端末: 近くに盛った皿があれば食べるボタンを点け、皿を光らせる
    internal static void UpdateEat(Ability eat)
    {
        var lp = PlayerControl.LocalPlayer;
        var b = eat?.Button;
        if (!lp || b == null) return;
        bool can = PortKit.Alive(lp) && lp.CanMove && !SpectreMinigame.IsOpen;
        var d = can ? SpectreDishes.Nearest(lp.GetTruePosition(), x => !x.Ate) : null;
        b.SetUsable(d != null);
        SpectreDishes.Highlight(d, RoleDisplay.ParseColor(Hex));
    }

    // 満腹度を足す (全員の端末で・本人の端末の値が正)
    private void AddSatiety(float satiety)
    {
        _left.Value = Mathf.Clamp(_left.Value + satiety * SatietyRate, 0f, MaxSatiety * SatietyRate);
        if (IsLocal) ShareNow();
    }

    internal static void SendSatiety(byte spectre, float amount)
    {
        AddCall.Send((spectre, amount));
        if (RoleState.Of(spectre) is Spectre s) s.AddSatiety(amount);
    }

    private static readonly RemoteCall<(byte id, float amount)> AddCall = new("Spectre.AddSatiety", Route.Anyone,
        (w, m) => { w.Write(m.id); w.Write(m.amount); }, r => (r.ReadByte(), r.ReadSingle()),
        (_, m) => { if (RoleState.Of(m.id) is Spectre s) s.AddSatiety(m.amount); });

    // 送るのは 1.5 秒ごとと減る速さが変わった時だけ (公式鯖の通信の予算を食わない)。間は他の端末が同じ速さで減らす
    public override void OnRewound()
    {
        if (IsLocal) ShareNow(); // 戻った満腹度を配り直す
    }

    private void ShareNow()
    {
        _shareIn = 1.5f;
        ShareCall.Send((PlayerId, _left.Value, _rate));
    }

    private static readonly RemoteCall<(byte id, float left, float rate)> ShareCall = new("Spectre.Share", Route.Anyone,
        (w, m) => { w.Write(m.id); w.Write(m.left); w.Write(m.rate); }, r => (r.ReadByte(), r.ReadSingle(), r.ReadSingle()),
        (_, m) => { if (RoleState.Of(m.id) is Spectre s && !s.IsLocal) { s._left.Value = m.left; s._rate = m.rate; } });

    public override void OnFixedUpdate()
    {
        if (RewindPlayback.Active) return; // 逆再生の間は満腹度を減らさない (戻し終わりに記録の時の値へ戻る)
        float dt = Time.fixedDeltaTime;
        if (IsLocal) TickLocal(dt);
        else if (_left.Value > 0f && _rate > 0f && !MeetingHud.Instance && !ExileController.Instance && ShipStatus.Instance && PortKit.Alive(Player))
            _left.Value = Mathf.Max(0f, _left.Value - dt * _rate);
        SpectreDishes.Tick(dt);
    }

    private void TickLocal(float dt)
    {
        bool meeting = MeetingHud.Instance || ExileController.Instance;
        bool alive = PortKit.Alive(Player);
        bool danger = Adrenaline && KillerNearby();
        float rate = danger ? AdrenalineRate : 1f;
        if (!meeting && alive && ShipStatus.Instance)
        {
            _left.Value -= dt * rate;
            CountdownWarn.Tick(ref _warnSec, _left.Value, 5); // 速さで割らない (危険の出入りで秒が行き来して鳴り直さない)
        }
        if (!(_left.Value > 0f))
        {
            _left.Value = 0f;
            if (alive && Time.time > _suicideAt + 0.2f) { _suicideAt = Time.time; PortKit.Suicide(); }
        }
        _shareIn -= dt;
        if (rate != _rate && _shareIn <= 1f) { _rate = rate; ShareNow(); } // 速さの切り替わりも 0.5 秒に 1 回まで
        else if (_shareIn <= 0f) ShareNow();

        _vanish?.Button?.SetUsable(VanishCost + 1f < Satiety && !Cloak.IsOn(PlayerId));
        UpdateEat(_eat);
        SpectreGauge.Draw(alive, Satiety, RequiredSatiety, danger);
        UpdateKillerArrows(alive && !meeting);
    }

    // 4 歩以内に (自分以外の) キル役がいる
    private bool KillerNearby()
    {
        Vector2 me = Player.GetTruePosition();
        foreach (var p in PlayerControl.AllPlayerControls)
            if (PortKit.Alive(p) && p.PlayerId != PlayerId && IsKiller(p, out _) && Vector2.Distance(p.GetTruePosition(), me) < 4f) return true;
        return false;
    }

    private static bool IsKiller(PlayerControl p, out Color color)
    {
        color = UnityEngine.Color.white;
        if (PortKit.IsImpostor(p)) { color = Palette.ImpostorRed; return true; }
        var role = RoleState.Of(p);
        if (role is Crew.Sheriff) return true;
        if (role != null && role.IsKiller) { color = RoleDisplay.ParseColor(role.Color); return true; }
        return false;
    }

    private void UpdateKillerArrows(bool show)
    {
        if (!ShowKillers) return;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.PlayerId == PlayerId) continue;
            var color = UnityEngine.Color.white;
            bool killer = PortKit.Alive(p) && IsKiller(p, out color);
            if (!_killerArrows.TryGetValue(p.PlayerId, out var arrow))
            {
                if (!killer) continue;
                _killerArrows[p.PlayerId] = arrow = new WorldArrow(Lifespan, color);
            }
            bool on = show && killer;
            arrow.SetActive(on);
            if (on) arrow.Update(p.transform.position);
        }
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (IsLocal) { _left.Value = Mathf.Max(20f, _left.Value); ShareNow(); }
    }

    public override void ModifyVision(ref float radius) => PortKit.ImpostorVision(ref radius);

    // ---- 仲間 (子狐・背徳者) ----

    // ホスト: 設定に合わせて、クルーの 1 人を子狐か背徳者にする
    private void AssignFollower()
    {
        int v = Immoralist.Value;
        if (v == 0 || !PortKit.Alive(Player)) return;
        bool follower = v switch
        {
            1 => true,
            2 => false,
            3 => Random.Range(0, 100) < FollowerChance,
            _ => (_alternate = !_alternate) == false,
        };
        var cand = new List<PlayerControl>();
        var plain = new List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.PlayerId == PlayerId || PortKit.IsImpostor(p)) continue;
            var role = RoleState.Of(p);
            if (role != null && role.Team != Roles.Team.Crew) continue;
            if (PortKit.IsLover(p)) continue;
            cand.Add(p);
            if (role == null || role.IsVanilla) plain.Add(p);
        }
        var pool = plain.Count > 0 ? plain : cand;
        if (pool.Count == 0) return;
        var target = pool[Random.Range(0, pool.Count)];
        RoleState.Change(target, follower ? RoleState.Proto<SpectreFollower>() : RoleState.Proto<SpectreImmoralist>());
        LinkCall.Send((target.PlayerId, PlayerId));
        Link(target.PlayerId, PlayerId);
    }

    private static readonly RemoteCall<(byte follower, byte spectre)> LinkCall = new("Spectre.Link", Route.HostToAll,
        (w, m) => { w.Write(m.follower); w.Write(m.spectre); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => Link(m.follower, m.spectre));

    private static void Link(byte follower, byte spectre)
    {
        if (RoleState.Of(spectre) is not Spectre s) return;
        s._team.Add(follower);
        if (RoleState.Of(follower) is ISpectreTeam t) t.SpectreId = spectre;
    }

    // スペクターが死ぬと、生きている仲間が後を追う (追放なら一緒に追放)
    [HostOnly, OnlyMine]
    private void OnMurdered(PlayerMurderedEvent e) => Follow(false);

    [HostOnly, OnlyMine]
    private void OnExiled(PlayerExiledEvent e) => Follow(true);

    private void Follow(bool exiled)
    {
        foreach (var id in _team)
        {
            var p = PortKit.ById(id);
            if (!PortKit.Alive(p)) continue;
            PortKit.After(0.05f, () =>
            {
                if (!PortKit.Alive(p)) return;
                if (exiled || MeetingHud.Instance || ExileController.Instance) PortKit.HostVanish(p, p, PortKit.Harm.Fated);
                else PortKit.HostSuicide(p);
            });
        }
    }

    // ---- 勝ち ----

    public override bool StealsWin(GameResult result) => CanWin && !result.BySabotage;

    public override IEnumerable<byte> StealsWith => _team;
}

// スペクターの仲間 (子狐・背徳者)
internal interface ISpectreTeam
{
    byte SpectreId { get; set; }
}
