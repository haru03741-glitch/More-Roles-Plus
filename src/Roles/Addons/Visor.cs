// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Visor.cs, Scripts/Abilities/VisorAbility.cs (VisorModifierU and the role's battery; role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to Extreme Roles https://github.com/yukieiji/ExtremeRoles
using System.Linq;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Options;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// バイザー (アドオン・インポスター以外): 電池式の持ち運びアドミン。普通のマップを開くと、充電が残っていればアドミンが見え
// (死体も数える)、開いている間だけ充電が減る。充電が切れるとマップが閉じ、次に開くと普通のマップになる。
// タスクを 1 つ終えるごとに少し充電される。設定で、会議中も見える・マップを開いたまま動ける
public sealed class Visor : AddonBase
{
    public override string Color => "#1D3091";
    public override Text Name => new("Visor.Name");
    public override string Reading => "ばいざー";
    public override Text Blurb => new("Visor.Blurb");
    public override string Origin => "Uchu, ER";
    protected internal override bool DefaultOnImpostor => false;
    public override bool CanAttach(Team team, RoleBase role) => team != Team.Impostor && base.CanAttach(team, role);

    public static readonly BoolOpt CanMoveWithMap = new("Visor.CanMoveWithMapWatching", false);
    public static readonly BoolOpt CanUseOnMeeting = new("Visor.CanUseAdminOnMeeting", false);
    public static readonly FloatOpt PortableAdminCharge = new("Visor.PortableAdminCharge", 10f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt MaxPortableAdminCharge = new("Visor.MaxPortableAdminCharge", 10f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt ChargesPerTasks = new("Visor.ChargesPerTasks", 1f, 0.5f, 10f, 0.5f, "s");

    // 自分の端末のバイザー (無ければ null)。マップを開いた時に見る
    internal static Visor Local
    {
        get
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || lp.Data == null || lp.Data.IsDead) return null;
            return RoleState.AddonsOf(lp.PlayerId).OfType<Visor>().FirstOrDefault();
        }
    }

    // アドミンで開いている間
    internal bool AdminOpen;

    private float _charge = -1f;
    private HudReadout _readout;
    private GameObject _mapRoot;
    private TextMeshPro _mapText;
    private int _drawKey = int.MinValue;
    private string _num = "";

    internal float Charge
    {
        get
        {
            if (_charge < 0f) _charge = FxMath.Min(PortableAdminCharge, MaxPortableAdminCharge);
            return _charge;
        }
    }

    [LocalOnly, OnlyMine]
    private void OnTaskCompleted(TaskCompletedEvent e)
    {
        _charge = FxMath.Min(MaxPortableAdminCharge, Charge + ChargesPerTasks);
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        var map = MapBehaviour.Instance;
        bool open = map && map.IsOpen;
        if (AdminOpen && !open) AdminOpen = false;
        if (AdminOpen && Charge > 0f)
        {
            _charge = FxMath.Max(0f, Charge - Time.fixedDeltaTime);
            if (_charge <= 0f && map) map.Close(); // 充電切れ: マップが閉じる
        }
        Draw();
    }

    // 残りの充電: 画面の下の札・マップを開いている間は画面上
    private void Draw()
    {
        bool alive = PortKit.Alive(Player);
        float max = FxMath.Max(MaxPortableAdminCharge, 0.1f);
        int key = (int)(Charge * 10f) + (alive ? 1 : 0) + (AdminOpen ? 2 : 0) * 100000;
        if (key != _drawKey) { _drawKey = key; _num = Charge.ToString("0.0"); }
        _readout ??= new HudReadout(Lifespan, UI.RoleArt.Own(Id), UchuKit.ParseColor(Color));
        _readout.Set(alive, _num, Charge / max);
        ShowMapText(alive && AdminOpen, _num);
    }

    private void ShowMapText(bool on, string num)
    {
        if (!_mapRoot)
        {
            if (!on) return;
            var hud = Vanilla.Hud;
            var src = hud && hud.TaskPanel ? hud.TaskPanel.taskText : null;
            if (!src) return;
            _mapRoot = FxRoot.NewHud("MrpVisorCharge", 5);
            var aspect = _mapRoot.AddComponent<AspectPosition>();
            aspect.Alignment = AspectPosition.EdgeAlignments.Top;
            aspect.DistanceFromEdge = new Vector3(0f, 0.55f, -20f);
            var go = Object.Instantiate(src.gameObject, _mapRoot.transform);
            go.name = "Text";
            go.layer = 5;
            for (int i = go.transform.childCount - 1; i >= 0; i--) Object.Destroy(go.transform.GetChild(i).gameObject);
            foreach (var comp in go.GetComponents<Component>())
                if (!comp.TryCast<Transform>() && !comp.TryCast<TextMeshPro>() && !comp.TryCast<Renderer>() && !comp.TryCast<MeshFilter>()) Object.Destroy(comp);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one;
            _mapText = go.GetComponent<TextMeshPro>();
            _mapText.rectTransform.sizeDelta = new Vector2(4f, 0.5f);
            _mapText.enableWordWrapping = false;
            _mapText.margin = default;
            _mapText.fontSize = 2.2f;
            _mapText.fontStyle = FontStyles.Bold;
            _mapText.alignment = TextAlignmentOptions.Center;
            _mapText.color = Lighten(UchuKit.ParseColor(Color));
            Lifespan.OnRelease(() => { if (_mapRoot) Object.Destroy(_mapRoot); });
            aspect.AdjustPosition();
        }
        if (_mapRoot.activeSelf != on) _mapRoot.SetActive(on);
        if (on && _mapText) _mapText.text = $"{new Text("Visor.Battery")}  {num}s";
    }

    private static UnityEngine.Color Lighten(UnityEngine.Color c) => new(Mathf.Min(c.r + 0.35f, 1f), Mathf.Min(c.g + 0.35f, 1f), Mathf.Min(c.b + 0.45f, 1f), 1f);
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.ShowNormalMap))]
internal static class VisorMapPatch
{
    // アドミンを出す
    internal static void ShowAdmin(MapBehaviour map, bool canMove)
    {
        var overlay = map.countOverlay;
        if (!overlay) return;
        overlay.gameObject.SetActive(true);
        overlay.SetOptions(true, true);
        if (map.taskOverlay) map.taskOverlay.Hide();
        map.countOverlayAllowsMovement = canMove;
        if (!canMove && PlayerControl.LocalPlayer.NetTransform) PlayerControl.LocalPlayer.NetTransform.Halt();
    }

    public static void Postfix(MapBehaviour __instance)
    {
        try
        {
            var v = Visor.Local;
            if (v == null || v.Charge <= 0f) return;
            if (MeetingHud.Instance && !Visor.CanUseOnMeeting) return;
            v.AdminOpen = true;
            ShowAdmin(__instance, Visor.CanMoveWithMap);
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"visor map: {e.Message}"); }
    }
}
