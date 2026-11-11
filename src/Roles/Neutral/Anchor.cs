// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Neutral/Anchor.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// アンカー (第三陣営): 足元にアンカーを置く (全員に見える)。近くの人には少しの間アンカーへの矢印が出る。
// 「幻聴」で地図のアンカーを押すと、その場所からもう一度知らせる。全部置いた後の会議の終わりに生きていれば単独で勝つ
public sealed class Anchor : RoleBase
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => "#0800ED";
    public override Text Name => new("Anchor.Name");
    public override string Reading => "あんかー";
    public override Text Blurb => new("Anchor.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Anchor.Place", "anchor_anchor"), new("Anchor.Echo", "anchor_echo") };

    public static readonly FloatOpt PlaceCooldown = new("Anchor.PlaceCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt NumOfAnchor = new("Anchor.NumOfAnchor", 3, 1, 10);
    public static readonly IntOpt PerTurn = new("Anchor.PerTurn", 1, 1, 15);
    public static readonly FloatOpt NoticeRange = new("Anchor.NoticeRange", 15f, 1f, 40f, 1f, "");
    public static readonly FloatOpt ArrowDuration = new("Anchor.ArrowDuration", 3f, 1f, 10f, 0.5f, "s");
    public static readonly FloatOpt EchoCooldown = new("Anchor.EchoCooldown", 15f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt NumOfEcho = new("Anchor.NumOfEcho", 3, 1, 15);
    public static readonly BoolOpt Vent = new("Anchor.CanVent", true);

    public override bool? CanVent => Vent;

    private readonly List<Vector2> _anchors = new(); // 全員の端末で同じ
    private bool _picking;                            // 自分の端末: 幻聴で地図を開いている
    private readonly List<SpriteRenderer> _pins = new();

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Anchor.Place"), "anchor_anchor")
        {
            Cooldown = () => PlaceCooldown,
            MaxUses = () => NumOfAnchor,
            UsesPerMeeting = PerTurn,
            OnUse = () =>
            {
                Vector2 at = Player.GetTruePosition();
                PlaceCall.Send((at.x, at.y));
                Place(at);
                return true;
            },
        });
        AddAbility(new Ability(new Text("Anchor.Echo"), "anchor_echo")
        {
            Cooldown = () => EchoCooldown,
            MaxUses = () => NumOfEcho,
            Usable = () => _anchors.Count > 0, // 使えない間はボタンを暗くする
            OnUse = () =>
            {
                if (_anchors.Count == 0) return false;
                _picking = true;
                UchuKit.OpenMap();
                UchuKit.EveryFrame(PickTick);
                return false; // 地図でアンカーを押した時に回数を使う
            },
        });
    }

    private static readonly RemoteCall<(float x, float y)> PlaceCall = new("Anchor.Place", Route.Anyone,
        (w, m) => { w.Write(m.x); w.Write(m.y); }, r => (r.ReadSingle(), r.ReadSingle()),
        (sender, m) => { if (RoleState.Of(sender) is Anchor a) a.Place(new Vector2(m.x, m.y)); }, perSecond: 2);

    private static readonly BodyKind AnchorKind = new("AnchorAnchor", BodyMaterial.Metal, BodySize.Medium, 0f);

    private void Place(Vector2 at)
    {
        _anchors.Add(at);
        CrewTraits.AddHold(at); // 重い錨: そばの人は掴まって、水の流れと吸い出しに押されない
        Bodies.Prop(AnchorKind, FloorPropArt.Place(Lifespan, at, "anchor").transform, 0.05f, 0.3f); // 重い錨は当たっても火花だけ
        MoreRolesPlus.Fx.Hearing.At("anchor_drop", at.x, at.y, 8f, 0.7f, Player);
        Notify(at);
    }

    // 全員の端末で: 近くにいればアンカーへの矢印
    private static void Notify(Vector2 at, bool echo = false)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!PortKit.Alive(lp) || MeetingHud.Instance) return;
        if (Vector2.Distance(lp.GetTruePosition(), at) > NoticeRange) return;
        UchuKit.Arrow(at, new Color32(8, 0, 237, 255), ArrowDuration);
        if (echo) MoreRolesPlus.Fx.Hearing.Self("anchor_echo", 0.55f);
    }

    private static readonly RemoteCall<(float x, float y)> EchoCall = new("Anchor.Echo", Route.Anyone,
        (w, m) => { w.Write(m.x); w.Write(m.y); }, r => (r.ReadSingle(), r.ReadSingle()),
        (sender, m) => { if (RoleState.Of(sender) is Anchor) Notify(new Vector2(m.x, m.y), true); }, perSecond: 2);

    // 自分の端末で毎フレーム: 地図のアンカーを押した
    private bool PickTick()
    {
        var map = MapBehaviour.Instance;
        if (!_picking || !map || !map.IsOpen || MeetingHud.Instance) { _picking = false; return false; }
        if (UchuKit.MapClick() is not Vector2 at) return true;
        int best = -1;
        float bestD = 3f;
        for (int i = 0; i < _anchors.Count; i++)
        {
            float d = Vector2.Distance(_anchors[i], at);
            if (d < bestD) { best = i; bestD = d; }
        }
        if (best < 0) return true;
        var a = _anchors[best];
        EchoCall.Send((a.x, a.y));
        Notify(a, true);
        _picking = false;
        var echo = Abilities[1];
        echo.Spend();
        echo.Button?.StartCooldown();
        if (echo.UsesLeft == 0) echo.Button?.SetUsable(false);
        map.Close();
        return false;
    }

    // アンカーの地図: 置いたアンカーのピン
    internal void UpdateMap(MapBehaviour map)
    {
        if (!map.HerePoint) return;
        while (_pins.Count < _anchors.Count) _pins.Add(UchuKit.MapPin(map, Player, _anchors[_pins.Count]));
    }

    // ホスト: 全部置いた後の会議の終わりに生きていれば勝ち
    [HostOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (_anchors.Count >= NumOfAnchor && PortKit.Alive(Player)) PortKit.After(0.5f, () => { if (PortKit.Alive(Player)) GameEnd.Win(this); });
    }
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
internal static class AnchorMapPatch
{
    public static void Postfix(MapBehaviour __instance)
    {
        if (RoleState.Local is not Anchor a) return;
        try { a.UpdateMap(__instance); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"anchor map: {e.Message}"); }
    }
}
