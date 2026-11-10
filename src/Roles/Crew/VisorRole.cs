// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Visor.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to Extreme Roles https://github.com/yukieiji/ExtremeRoles
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles.Addons;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// バイザー (クルー): 電池式の持ち運びアドミン。ボタンで普通のマップを開くとアドミンが見え、開いている間だけ充電が減る。
// 充電が切れるとマップが閉じる。タスクを 1 つ終えるごとに少し充電される。(アドオンのバイザーは Addons/Visor.cs・こちらは役職)
public sealed class VisorRole : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#1D3091";
    public override Text Name => new("VisorRole.Name");
    public override string Reading => "ばいざー";
    public override Text Blurb => new("VisorRole.Blurb");
    public override string Origin => "Uchu, ER";
    public override bool CanBeMadmate => true;
    public override HelpSection[] HelpSections => new HelpSection[] { new("VisorRole.Open", "role_VisorRole") };

    public static readonly BoolOpt CanMoveWithMap = new("VisorRole.CanMoveWithMapWatching", false);
    public static readonly BoolOpt CanUseOnMeeting = new("VisorRole.CanUseAdminOnMeeting", false);
    public static readonly FloatOpt PortableAdminCharge = new("VisorRole.PortableAdminCharge", 10f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt MaxPortableAdminCharge = new("VisorRole.MaxPortableAdminCharge", 10f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt ChargesPerTasks = new("VisorRole.ChargesPerTasks", 1f, 0.5f, 10f, 0.5f, "s");

    // ボタンで開いた直後の ShowNormalMap をアドミンにする印 / アドミンで開いている間
    internal static bool Pending;
    internal static bool AdminOpen;

    private float _charge = -1f;
    private Ability _button;
    private HudReadout _readout;
    private GameObject _mapRoot;
    private TextMeshPro _mapText;
    private int _drawKey = int.MinValue;
    private string _num = "";

    private float Charge
    {
        get
        {
            if (_charge < 0f) _charge = FxMath.Min(PortableAdminCharge, MaxPortableAdminCharge);
            return _charge;
        }
    }

    public override void OnAssigned()
    {
        _button = new Ability(new Text("VisorRole.Open"), "role_VisorRole")
        {
            Usable = () => Charge > 0f,
            OnUse = () => { Open(); return false; },
        };
        AddAbility(_button);
        // 会議中も使える設定の時は、名札の横のボタンから (HUD のボタンは会議中は出ない)
        AddAbility(new Ability(new Text("VisorRole.Open"), "role_VisorRole")
        {
            Target = AbilityTarget.Meeting,
            MeetingSelf = true,
            Usable = () => CanUseOnMeeting && Charge > 0f,
            OnUsed = (_, _) => { if (IsLocal) Open(); },
        });
    }

    private void Open()
    {
        if (!IsLocal || !PortKit.Alive(Player) || Charge <= 0f) return;
        if (MeetingHud.Instance && !CanUseOnMeeting) return;
        var map = MapBehaviour.Instance;
        if (map && map.IsOpen) return;
        Pending = true;
        try { UchuKit.OpenMap(); }
        finally { Pending = false; }
    }

    [LocalOnly, OnlyMine]
    private void OnTaskCompleted(TaskCompletedEvent e)
    {
        _charge = FxMath.Min(MaxPortableAdminCharge, Charge + ChargesPerTasks);
    }

    public override void OnFixedUpdate()
    {
        if (!IsLocal) return;
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

    // 残りの充電: ボタンの数字・左下の札 (PC もスマホも)・マップを開いている間は画面上
    private void Draw()
    {
        bool alive = PortKit.Alive(Player);
        float max = FxMath.Max(MaxPortableAdminCharge, 0.1f);
        int key = (int)(Charge * 10f) + (alive ? 1 : 0) + (AdminOpen ? 2 : 0) * 100000;
        if (key != _drawKey) { _drawKey = key; _num = Charge.ToString("0.0"); }
        // 本編がボタンの絵を毎フレーム書き戻すことがあるので、ボタンの数は毎回書く (中で同じ値なら何もしない)
        _button?.Button?.ShowGauge(Charge, max, _num);
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
            _mapText.color = Color32To(UchuKit.ParseColor(Color));
            Lifespan.OnRelease(() => { if (_mapRoot) Object.Destroy(_mapRoot); });
            aspect.AdjustPosition();
        }
        if (_mapRoot.activeSelf != on) _mapRoot.SetActive(on);
        if (on && _mapText) _mapText.text = $"{new Text("VisorRole.Battery")}  {num}s";
    }

    private static UnityEngine.Color Color32To(UnityEngine.Color c) => new(Mathf.Min(c.r + 0.35f, 1f), Mathf.Min(c.g + 0.35f, 1f), Mathf.Min(c.b + 0.45f, 1f), 1f);
}

// ボタンで開いたマップだけアドミンにする (アドオンのバイザーと同じ見せ方・Addons/Visor.cs)
[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.ShowNormalMap))]
internal static class VisorRoleMapPatch
{
    public static void Postfix(MapBehaviour __instance)
    {
        try
        {
            if (!VisorRole.Pending) return;
            VisorRole.Pending = false;
            VisorRole.AdminOpen = true;
            VisorMapPatch.ShowAdmin(__instance, VisorRole.CanMoveWithMap);
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"visor role map: {e.Message}"); }
    }
}
