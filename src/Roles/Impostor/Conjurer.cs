// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Conjurer.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 魔術師 (インポスター): マーカーを 3 つ置いて「発動」すると、3 つを結んだ三角形の中の人を全員キルする。普通のキルはできない。
// 2 つ目からは前のマーカーから決めた距離の中にしか置けない。マーカーは自分にしか見えず、会議で消える
public sealed class Conjurer : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Conjurer.Name");
    public override string Reading => "まじゅつし";
    public override Text Blurb => new("Conjurer.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Conjurer.Mark", "snr_marker"), new("Conjurer.Crush", "snr_crush") };
    public static readonly FloatOpt AddLength = new("Conjurer.AddLength", 10f, 1f, 40f, 1f);
    public static readonly FloatOpt MarkCooldown = new("Conjurer.MarkCooldown", 15f, 5f, 60f, 1f, "s");
    public static readonly BoolOpt KillImpostor = new("Conjurer.CanKillImpostor", false);
    public static readonly BoolOpt ShowFlash = new("Conjurer.ShowFlash", false);

    private readonly List<SpriteRenderer> _marks = new();

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Conjurer.Mark"), "snr_marker")
        {
            Cooldown = () => MarkCooldown,
            OnUse = () =>
            {
                Vector2 at = Player.transform.position;
                if (_marks.Count >= 3) return false;
                if (_marks.Count > 0 && _marks[^1] && Vector2.Distance(at, _marks[^1].transform.position) >= AddLength) return false;
                _marks.Add(PortKit.Marker(Lifespan, at, "snr_marker", 0.5f, new Color(0.8f, 0.3f, 1f)));
                return true;
            },
        });
        AddAbility(new Ability(new Text("Conjurer.Crush"), "snr_crush")
        {
            CooldownAtStart = false,
            OnUse = () =>
            {
                if (_marks.Count < 3) return false;
                var tri = new Tri(_marks[0].transform.position, _marks[1].transform.position, _marks[2].transform.position);
                ClearMarks();
                CrushCall.Send(tri);
                if (AmongUsClient.Instance.AmHost) Crush(Player, tri);
                return true;
            },
        });
    }

    private void ClearMarks()
    {
        foreach (var m in _marks) if (m) Object.Destroy(m.gameObject);
        _marks.Clear();
    }

    [LocalOnly]
    private void OnMeetingEnd(MeetingEndEvent e) => ClearMarks();

    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (e.Killer && e.Killer.PlayerId == PlayerId && !_crushing) e.Cancel("conjurer");
    }

    private static bool _crushing;

    public readonly struct Tri
    {
        public readonly Vector2 A, B, C;
        public Tri(Vector2 a, Vector2 b, Vector2 c) { A = a; B = b; C = c; }
        public bool Has(Vector2 p)
        {
            float d1 = Cross(p, A, B), d2 = Cross(p, B, C), d3 = Cross(p, C, A);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
        private static float Cross(Vector2 p, Vector2 a, Vector2 b) => (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
    }

    private static readonly RemoteCall<Tri> CrushCall = new("Conjurer.Crush", Route.ToHost,
        (w, t) => { w.Write(t.A.x); w.Write(t.A.y); w.Write(t.B.x); w.Write(t.B.y); w.Write(t.C.x); w.Write(t.C.y); },
        r => new Tri(new Vector2(r.ReadSingle(), r.ReadSingle()), new Vector2(r.ReadSingle(), r.ReadSingle()), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (sender, t) => { if (RoleState.Of(sender) is Conjurer && !sender.AmOwner) Crush(sender, t); });

    private static readonly RemoteCall<byte> FlashCall = new("Conjurer.Flash", Route.HostToAll,
        (w, _) => w.Write((byte)0), r => r.ReadByte(), (_, _) => PortKit.Flash(Palette.Blue, 1f));

    // ホストの端末で: 三角形の中の人を全員キル
    private static void Crush(PlayerControl user, Tri tri)
    {
        if (!PortKit.Alive(user)) return;
        if (ShowFlash) FlashCall.Send(0);
        if (ShowFlash) PortKit.Flash(Palette.Blue, 1f);
        _crushing = true;
        try
        {
            foreach (var p in PlayerControl.AllPlayerControls)
                if (PortKit.Alive(p) && tri.Has(p.GetTruePosition()) && (KillImpostor || !PortKit.IsImpostor(p))) PortKit.HostKill(user, p);
        }
        finally { _crushing = false; }
    }
}
