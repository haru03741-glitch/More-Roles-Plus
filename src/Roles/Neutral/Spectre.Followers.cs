// Ported from https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Roles/Neutral/Spectre.Followers.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 子狐 (スペクターの仲間): スペクターと同じ皿のお揚げを食べて、スペクターの満腹度を足す (決めた割合)。
// スペクターの満腹度が見える。インポスターの視界で停電の影響を受けない。スペクターが勝つと一緒に勝ち、死ぬと後を追う
public sealed class SpectreFollower : RoleBase, ISpectreTeam
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => Spectre.Hex;
    public override Text Name => new("SpectreFollower.Name");
    public override string Reading => "こぎつね";
    public override Text Blurb => new("SpectreFollower.Blurb");
    public override string Origin => "Nebula";
    public override bool MadeByOthers => true;
    public override HelpSection[] HelpSections => new HelpSection[] { new("Spectre.Eat", "spectre_eat") };

    public static readonly BoolOpt Vent = new("SpectreFollower.Vent", true);
    public static readonly FloatOpt SatietyRate = new("SpectreFollower.SatietyRate", 0.5f, 0.25f, 2f, 0.25f, "x");
    public static readonly BoolOpt ShowDishesOnMap = new("SpectreFollower.ShowDishesOnMap", true);

    public override bool? CanVent => Vent;
    public byte SpectreId { get; set; } = 255;
    private Spectre MySpectre => SpectreId == 255 ? null : RoleState.Of(SpectreId) as Spectre;
    private Ability _eat;

    public override void OnAssigned()
    {
        _eat = Spectre.AddEatAbility(this, () => { if (SpectreId != 255) Spectre.SendSatiety(SpectreId, SatietyRate); });
        if (!IsLocal) return;
        SpectreGauge.Ensure(Lifespan, Spectre.MaxSatiety);
        SpectreMapLayer.ShouldShow = () => ShowDishesOnMap;
        Lifespan.OnRelease(() => SpectreMapLayer.ShouldShow = null);
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        Spectre.UpdateEat(_eat);
        var s = MySpectre;
        SpectreGauge.Draw(PortKit.Alive(Player) && s != null, s?.Satiety ?? 0f, Spectre.RequiredSatiety, false);
    }

    public override void ModifyVision(ref float radius) => PortKit.ImpostorVision(ref radius);
}

// 背徳者 (スペクターの仲間): 生きている間は空の皿にお揚げを盛り直せる (少しの間その場で)。
// 幽霊になると皿のお揚げを取り除ける (スペクターが生きている間)。自分で死ねる (設定)。誰かがキルされると画面が光る (設定)
public sealed class SpectreImmoralist : RoleBase, ISpectreTeam
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => Spectre.Hex;
    public override Text Name => new("SpectreImmoralist.Name");
    public override string Reading => "はいとくしゃ";
    public override Text Blurb => new("SpectreImmoralist.Blurb");
    public override string Origin => "Nebula";
    public override bool MadeByOthers => true;
    public override HelpSection[] HelpSections => new HelpSection[]
    {
        new("SpectreImmoralist.Place", "spectre_place"), new("SpectreImmoralist.Remove", "spectre_remove"), new("SpectreImmoralist.Suicide", "spectre_suicide"),
    };

    public static readonly BoolOpt Vent = new("SpectreImmoralist.Vent", true);
    public static readonly FloatOpt RespawnCooldown = new("SpectreImmoralist.RespawnCooldown", 20f, 0f, 60f, 2.5f, "s");
    public static readonly FloatOpt RespawnDuration = new("SpectreImmoralist.RespawnDuration", 3f, 0.5f, 10f, 0.5f, "s");
    public static readonly BoolOpt ShowDishesOnMap = new("SpectreImmoralist.ShowDishesOnMap", false);
    public static readonly BoolOpt CanSuicide = new("SpectreImmoralist.CanSuicide", true);
    public static readonly BoolOpt ShowKillFlash = new("SpectreImmoralist.ShowKillFlash", true);

    public override bool? CanVent => Vent;
    public byte SpectreId { get; set; } = 255;
    private Spectre MySpectre => SpectreId == 255 ? null : RoleState.Of(SpectreId) as Spectre;
    private Ability _place, _remove;
    private int _placing = -1;

    public override void OnAssigned()
    {
        _place = AddAbility(new Ability(new Text("SpectreImmoralist.Place"), "spectre_place")
        {
            Cooldown = () => RespawnCooldown,
            Duration = () => RespawnDuration,
            OnUse = () =>
            {
                var d = Near(true);
                if (d == null) return false;
                _placing = d.Id;
                return true;
            },
            OnEnd = () =>
            {
                int id = _placing;
                _placing = -1;
                var lp = PlayerControl.LocalPlayer;
                if (id < 0 || !PortKit.Alive(lp) || !lp.CanMove) return;
                var d = Near(true);
                if (d == null || d.Id != id) return;
                SpectreDishes.Set(id, false);
            },
        });
        _remove = AddAbility(new Ability(new Text("SpectreImmoralist.Remove"), "spectre_remove")
        {
            Ghost = true,
            CooldownAtStart = false,
            OnUse = () =>
            {
                var s = MySpectre;
                var d = Near(false);
                if (d == null || s == null || !PortKit.Alive(s.Player)) return false;
                SpectreDishes.Set(d.Id, true);
                PortSound.Play("spectre_eat", () => PortSound.Fallback("spectre_eat"), 0.8f);
                return true;
            },
        });
        if (CanSuicide)
            AddAbility(new Ability(new Text("SpectreImmoralist.Suicide"), "spectre_suicide")
            {
                Cooldown = () => 5f,
                OnUse = () => { PortKit.Suicide(); return true; },
            });
        if (!IsLocal) return;
        SpectreMapLayer.ShouldShow = () => ShowDishesOnMap;
        Lifespan.OnRelease(() => SpectreMapLayer.ShouldShow = null);
    }

    // 生きている間は空の皿・幽霊は盛った皿 (幽霊は壁越しでも)
    private static SpectreDishes.Dish Near(bool empty)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return null;
        bool dead = lp.Data != null && lp.Data.IsDead;
        return SpectreDishes.Nearest(lp.GetTruePosition(), d => d.Ate == empty, dead, 0.8f);
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        bool dead = Player.Data != null && Player.Data.IsDead;
        var target = dead ? Near(false) : Near(true);
        if (!dead)
        {
            if (_place?.InEffect == true)
            {
                // 盛っている途中で皿から離れたら取りやめる
                var d = Near(true);
                if (d == null || d.Id != _placing) _place.EndEffect();
            }
            else _place?.Button?.SetUsable(target != null);
        }
        else
        {
            var s = MySpectre;
            _remove?.Button?.SetUsable(target != null && s != null && PortKit.Alive(s.Player));
        }
        SpectreDishes.Highlight(target, RoleDisplay.ParseColor(Spectre.Hex));
    }

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!ShowKillFlash || MeetingHud.Instance || ExileController.Instance) return;
        if (e.Target && e.Target.PlayerId == PlayerId) return;
        PortKit.Flash(RoleDisplay.ParseColor(Spectre.Hex));
    }

    public override void ModifyVision(ref float radius) => PortKit.ImpostorVision(ref radius);
}
