// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/DimensionWalker.cs, CustomObject/WormHole.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ディメンションウォーカー (インポスター): 足元にワームホールを置く (ドアのそばには置けない)。ワームホールは設定の時間が経つと
// ベントになり、同じ人のワームホール同士がつながる (ベントを使える人なら誰でも通れる)。起動までの残りはインポスターに見える。
// 自分のワームホールにベントのボタンを向けている時に押すと回収でき、置ける数が戻る
public sealed class DimensionWalker : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("DimensionWalker.Name");
    public override string Reading => "でぃめんしょんうぉーかー";
    public override Text Blurb => new("DimensionWalker.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("DimensionWalker.Place", "snr_wormhole") };
    public static readonly FloatOpt PlaceCooldown = new("DimensionWalker.Cooldown", 25f, 0f, 120f, 2.5f, "s");
    public static readonly FloatOpt ActivateTime = new("DimensionWalker.ActivateTime", 195f, 0f, 600f, 15f, "s");
    public static readonly IntOpt HoleCount = new("DimensionWalker.HoleCount", 3, 1, 10);

    private sealed class Hole
    {
        public byte Owner;
        public Vent Vent;
        public GameObject Go;
        public TMPro.TextMeshPro Timer;
        public float Left;
        public bool Active;
        public int Shown = int.MinValue;
    }

    // 全員の端末で同じ順に持つ
    private static readonly List<Hole> Holes = new();
    private int _left = -1;
    private Ability _place;

    private int Left => _left < 0 ? HoleCount : _left;

    public override void OnAssigned()
    {
        _place = AddAbility(new Ability(new Text("DimensionWalker.Place"), "snr_wormhole")
        {
            Cooldown = () => PlaceCooldown,
            OnUse = () =>
            {
                if (!PortKit.Alive(Player)) return false;
                var own = TargetOwnHole();
                if (own != null)
                {
                    CollectCall.Send(own.Vent.Id);
                    Collect(Player, own.Vent.Id);
                    return true;
                }
                if (Left <= 0 || !Player.CanMove || NearDoor(Player.GetTruePosition())) return false;
                Vector2 at = Player.GetTruePosition();
                PlaceCall.Send(at);
                Place(Player, at);
                return true;
            },
        });
        RoleState.Match.OnRelease(ClearAll);
    }

    private static void ClearAll()
    {
        foreach (var h in Holes) RemoveVent(h);
        Holes.Clear();
    }

    private static bool NearDoor(Vector2 at)
    {
        var ship = ShipStatus.Instance;
        if (!ship || ship.AllDoors == null) return false;
        foreach (var d in ship.AllDoors) if (d && Vector2.Distance(at, d.transform.position) <= 1f) return true;
        return false;
    }

    // ベントのボタンが向いている自分のワームホール
    private Hole TargetOwnHole()
    {
        var hud = Vanilla.Hud;
        var target = hud && hud.ImpostorVentButton ? hud.ImpostorVentButton.currentTarget : null;
        if (!target) return null;
        foreach (var h in Holes) if (h.Vent == target && h.Owner == PlayerId) return h;
        return null;
    }

    private static readonly RemoteCall<Vector2> PlaceCall = new("DimensionWalker.Place", Route.Anyone,
        (w, at) => { w.Write(at.x); w.Write(at.y); }, r => new Vector2(r.ReadSingle(), r.ReadSingle()), (sender, at) => Place(sender, at));
    private static readonly RemoteCall<int> CollectCall = new("DimensionWalker.Collect", Route.Anyone,
        (w, id) => w.WritePacked(id), r => r.ReadPackedInt32(), (sender, id) => Collect(sender, id));

    // 全員の端末で: ワームホールを置く (まだ使えないベントを作る)
    private static void Place(PlayerControl owner, Vector2 at)
    {
        var ship = ShipStatus.Instance;
        if (RoleState.Of(owner) is not DimensionWalker dw || !ship || ship.AllVents == null || ship.AllVents.Length == 0) return;
        dw._left = dw.Left - 1;
        if (dw.IsLocal) dw._place?.Button?.SetUses(dw._left);
        int id = 0;
        foreach (var v in ship.AllVents) if (v) id = Mathf.Max(id, v.Id + 1);
        var go = new GameObject("WormHole");
        go.transform.position = new Vector3(at.x, at.y, at.y / 1000f + 0.1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = ButtonIcons.Get("snr_wormhole");
        sr.color = new UnityEngine.Color(0.4f, 0.2f, 0.9f, 0.9f);
        go.transform.localScale = Vector3.one * (0.7f / 1.28f);
        var vent = Object.Instantiate(ship.AllVents[0], go.transform);
        vent.transform.position = new Vector3(at.x, at.y, at.y / 1000f + 0.1f);
        vent.transform.localScale = Vector3.one * (1.28f / 0.7f);
        vent.Id = id;
        vent.Left = null;
        vent.Right = null;
        vent.Center = null;
        vent.name = "WormHoleVent";
        if (vent.myRend) vent.myRend.enabled = false;
        vent.gameObject.SetActive(false);
        var hole = new Hole { Owner = owner.PlayerId, Vent = vent, Go = go, Left = ActivateTime };
        var hud = Vanilla.Hud;
        if (hud && hud.ImpostorVentButton && hud.ImpostorVentButton.buttonLabelText && PortKit.IsImpostor(PlayerControl.LocalPlayer))
        {
            hole.Timer = Object.Instantiate(hud.ImpostorVentButton.buttonLabelText, go.transform);
            hole.Timer.transform.position = go.transform.position + new Vector3(0f, 0f, -0.5f);
            hole.Timer.color = Palette.DisabledClear;
        }
        var list = new List<Vent>(ship.AllVents);
        list.Add(vent);
        ship.AllVents = list.ToArray();
        Holes.Add(hole);
        if (ActivateTime <= 0f) Activate(hole);
    }

    private static void Collect(PlayerControl owner, int ventId)
    {
        if (RoleState.Of(owner) is not DimensionWalker dw) return;
        var h = Holes.Find(x => x.Vent && x.Vent.Id == ventId && x.Owner == owner.PlayerId);
        if (h == null) return;
        dw._left = dw.Left + 1;
        if (dw.IsLocal) dw._place?.Button?.SetUses(dw._left);
        Holes.Remove(h);
        RemoveVent(h);
        Connect(owner.PlayerId);
    }

    private static void RemoveVent(Hole h)
    {
        var ship = ShipStatus.Instance;
        if (ship && ship.AllVents != null && h.Vent)
        {
            var list = new List<Vent>(ship.AllVents);
            list.Remove(h.Vent);
            ship.AllVents = list.ToArray();
        }
        if (h.Go) Object.Destroy(h.Go);
    }

    private static void Activate(Hole h)
    {
        h.Active = true;
        if (h.Vent) h.Vent.gameObject.SetActive(true);
        if (h.Timer) h.Timer.gameObject.SetActive(false);
        Connect(h.Owner);
    }

    // 同じ人の使えるワームホールを輪につなぐ
    private static void Connect(byte owner)
    {
        var mine = Holes.FindAll(x => x.Owner == owner && x.Active && x.Vent);
        foreach (var h in mine)
        {
            h.Vent.Left = null;
            h.Vent.Right = null;
        }
        for (int i = 0; i < mine.Count - 1; i++)
        {
            mine[i].Vent.Right = mine[i + 1].Vent;
            mine[i + 1].Vent.Left = mine[i].Vent;
        }
        if (mine.Count > 1)
        {
            mine[0].Vent.Left = mine[^1].Vent;
            mine[^1].Vent.Right = mine[0].Vent;
        }
    }

    // 起動までの時間を数える (全員の端末で。1 人目のディメンションウォーカーがまとめて見る)
    public override void OnFixedUpdate()
    {
        if (FirstWalker() != this) return;
        foreach (var h in Holes)
        {
            if (h.Active) continue;
            h.Left -= Time.fixedDeltaTime;
            if (h.Timer)
            {
                int n = Mathf.Max(0, Mathf.CeilToInt(h.Left));
                if (n != h.Shown)
                {
                    h.Shown = n;
                    h.Timer.text = n.ToString();
                }
            }
            if (h.Left <= 0f) Activate(h);
        }
    }

    private static DimensionWalker FirstWalker()
    {
        foreach (var r in RoleState.All) if (r is DimensionWalker d) return d;
        return null;
    }
}
