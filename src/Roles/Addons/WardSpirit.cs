using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// 守り霊 (幽霊のアドオン・クルーの死者に): 地形で崩れ落ちた人・外壁の穴の縁にしがみついている人 (瀕死の人) の所へ行き、
// 「救う」でその場で助ける。瀕死の人が出ると守り霊の画面にその人への矢印が出る。
// 本編の幽霊の憑依 (押すと人の所へすぐ飛べる) は使えない (駆けつける間が守り霊の遊びなので)
public sealed class WardSpirit : AddonBase
{
    public override string Color => "#F4E3A8";
    public override Text Name => new("WardSpirit.Name");
    public override string Reading => "まもりれい";
    public override Text Blurb => new("WardSpirit.Blurb");
    public override HelpSection[] HelpSections => new HelpSection[] { new("WardSpirit.Save", "ward_save") };
    public override bool Ghost => true;

    protected internal override bool DefaultOnNeutral => false;
    public override bool CanAttach(Team team, RoleBase role) => team == Team.Crew && base.CanAttach(team, role);

    public static readonly IntOpt Uses = new("WardSpirit.Uses", 1, 1, 5);
    private const float Reach = 1.5f;

    private static readonly Color ArrowColor = new(0.96f, 0.89f, 0.66f, 1f);
    private readonly Dictionary<byte, SpiritArrow> _arrows = new();
    private readonly List<byte> _drop = new();

    // 自分が守り霊の幽霊の間 (本編の憑依を止める)
    internal static bool LocalSealsHaunt;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("WardSpirit.Save"), "ward_save")
        {
            Ghost = true,
            Target = AbilityTarget.Player,
            Range = Reach,
            IgnoreWalls = true,
            CooldownAtStart = false,
            MaxUses = () => Uses,
            CanTarget = (_, t) => Rescue.Dying(t.PlayerId),
            OnUsed = (u, t) => Rescue.Apply(t.PlayerId, u.PlayerId, Rescue.BySpirit),
        });
        if (IsLocal) Lifespan.OnRelease(() => { LocalSealsHaunt = false; ClearArrows(); });
    }

    // 自分の端末: 瀕死の人への矢印・憑依のボタンを隠す
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        var me = Player;
        bool ghost = me && me.Data != null && me.Data.IsDead;
        // 止めるのは本編の憑依だけ (守護天使の護衛など、幽霊の別の能力のボタンは残す)
        LocalSealsHaunt = ghost && me.Data.Role && me.Data.Role.Role == AmongUs.GameOptions.RoleTypes.CrewmateGhost;
        if (LocalSealsHaunt) HideHaunt();
        bool any = ghost && (TerrainDeath.AnyDoomed || Terrain.CrewGrip.AnyClinging) && !MeetingHud.Instance && !ExileController.Instance;
        if (!any)
        {
            ClearArrows();
            return;
        }
        var cam = Camera.main;
        if (!cam) return;
        var view = new SpiritArrow.View(cam);
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null || p.Data.IsDead) continue;
            byte id = p.PlayerId;
            if (!Rescue.Dying(id)) continue;
            if (!_arrows.TryGetValue(id, out var a) || !a.Alive) _arrows[id] = a = new SpiritArrow(ArrowColor);
            a.Point(p.transform.position, view);
        }
        _drop.Clear();
        foreach (var (id, a) in _arrows) if (!Rescue.Dying(id)) { a.Destroy(); _drop.Add(id); }
        foreach (var id in _drop) _arrows.Remove(id);
    }

    private void ClearArrows()
    {
        if (_arrows.Count == 0) return;
        foreach (var a in _arrows.Values) a.Destroy();
        _arrows.Clear();
    }

    private static void HideHaunt()
    {
        var hud = Vanilla.Hud;
        var b = hud ? hud.AbilityButton : null;
        if (b && b.gameObject.activeSelf) b.gameObject.SetActive(false);
    }

    // 全員: 守り霊が救った (光の演出)
    internal static void OnSaved(byte target, byte by)
    {
        var p = PortKit.ById(target);
        if (p) WardSpiritFx.Rise(p);
    }
}

// 守り霊の間は本編の憑依のボタンを押しても何もしない (隠しきる前の 1 刻みの押し)
[HarmonyPatch(typeof(AbilityButton), nameof(AbilityButton.DoClick))]
internal static class WardSpiritHauntPatch
{
    public static bool Prefix(AbilityButton __instance)
    {
        if (!WardSpirit.LocalSealsHaunt) return true;
        var hud = Vanilla.Hud;
        return !hud || __instance != hud.AbilityButton;
    }
}

// 守り霊の画面の、瀕死の人への矢印。カメラの子に置き、相手が画面の外なら画面の端、中なら頭の上に出す
// (幽霊のカメラでも必ず写るよう、奥行きはカメラの手前の面から決め、層はカメラと同じにする)
internal sealed class SpiritArrow
{
    private const float Margin = 0.45f, Above = 1.2f, Size = 0.4f;
    private readonly GameObject _go;
    private readonly Transform _tf;

    public SpiritArrow(Color color)
    {
        var cam = Camera.main;
        _go = cam ? FxRoot.NewOnCamera("MrpSpiritArrow", cam) : null;
        if (!_go) return;
        _tf = _go.transform;
        var sr = _go.AddComponent<SpriteRenderer>();
        sr.sprite = ButtonIcons.Get("snr_arrowhead");
        sr.color = color;
        _tf.localScale = Fx.FxMath.V3(Size, Size, 1f);
    }

    public bool Alive => _go;

    public void Destroy()
    {
        if (_go) Object.Destroy(_go);
    }

    // 1 刻みに 1 回だけ読むカメラの値 (矢印の本数ぶん読まない)
    public readonly struct View
    {
        public readonly float X, Y, HalfW, HalfH, Near;
        public View(Camera cam)
        {
            var c = cam.transform.position;
            X = c.x; Y = c.y;
            HalfH = cam.orthographicSize;
            HalfW = HalfH * cam.aspect;
            Near = cam.nearClipPlane;
        }
    }

    public void Point(Vector3 target, in View v)
    {
        if (!_go) return;
        float dx = target.x - v.X, dy = target.y - v.Y + Above;
        float hx = v.HalfW - Margin, hy = v.HalfH - Margin;
        bool inside = dx > -hx && dx < hx && dy > -hy && dy < hy;
        float ax = dx, ay = dy, deg = -90f;
        if (!inside)
        {
            // 画面の真ん中から相手への線が、画面の縁 (少し内側) と交わる所
            float k = Fx.FxMath.Min(hx / Fx.FxMath.Max(Fx.FxMath.Abs(dx), 1e-3f), hy / Fx.FxMath.Max(Fx.FxMath.Abs(dy), 1e-3f));
            ax = dx * k;
            ay = dy * k;
            deg = System.MathF.Atan2(dy, dx) * 57.29578f;
        }
        _tf.localPosition = Fx.FxMath.V3(ax, ay, v.Near + 0.3f);
        _tf.localRotation = Fx.FxMath.RotZ(deg);
    }
}
