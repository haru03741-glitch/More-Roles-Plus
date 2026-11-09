// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Witch.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/TheOtherRolesAU/TheOtherRoles (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ウィッチ (インポスター): 「魔術」で近くの人に呪文を唱え続ける (離れると中断・1 ターンに 1 回)。
// 唱え終えた人は会議の名札に魔術の印が付き (全員に見える)、会議の終わりにウィッチが生きていれば死ぬ
public sealed class Witch : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Witch.Name");
    public override string Reading => "うぃっち";
    public override Text Blurb => new("Witch.Blurb");
    public override string Origin => "Uchu, TOR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Witch.Spell", "witch_spell") };

    public static readonly FloatOpt SpellCooldown = new("Witch.SpellCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt SpellDuration = new("Witch.SpellDuration", 3f, 0.5f, 10f, 0.5f, "s");
    public static readonly BoolOpt NormalKill = new("Witch.NormalKill", true);

    // 呪われた人 (全員の端末で同じ)
    private static readonly HashSet<byte> Spelled = new();
    private static object _match;

    private PlayerControl _target; // 自分の端末: 唱えている相手
    private bool _broken;
    private Ability _spell;

    public override void OnAssigned()
    {
        if (!ReferenceEquals(_match, RoleState.Match))
        {
            _match = RoleState.Match;
            RoleState.Match.OnRelease(() => { Spelled.Clear(); _match = null; });
            RoleFrame.AddExtraMark(RoleState.Match, seen => MeetingHud.Instance && Spelled.Contains(seen.PlayerId) ? "<color=#BF40BF>☆</color>" : null);
        }
        _spell = AddAbility(new Ability(new Text("Witch.Spell"), "witch_spell")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => SpellCooldown,
            Duration = () => SpellDuration,
            UsesPerMeeting = 1,
            CanTarget = (_, t) => !PortKit.IsImpostor(t) && !Spelled.Contains(t.PlayerId),
            OnUsed = (user, t) =>
            {
                if (!user.AmOwner) return;
                _target = t;
                _broken = false;
                _start = Time.time;
            },
            OnEnd = () =>
            {
                var t = _target;
                _target = null;
                if (_broken || !PortKit.Alive(t) || MeetingHud.Instance) return;
                SpellCall.Send(t.PlayerId);
                OnSpelled(t.PlayerId);
                PortKit.Flash(new Color(1f, 0.1f, 0.1f, 0.5f));
                Burst(t);
            },
        });
    }

    private static readonly RemoteCall<byte> SpellCall = new("Witch.Spell", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Witch) OnSpelled(id); }, perSecond: 2);

    private static void OnSpelled(byte id) => Spelled.Add(id);

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (!NormalKill) UchuKit.HideKillButton();
        Fx();
        if (_target == null) return;
        // 離れたら中断
        if (!PortKit.Alive(_target) || !PortKit.Alive(Player)
            || Vector2.Distance(_target.GetTruePosition(), Player.GetTruePosition()) > GameManager.Instance.LogicOptions.GetKillDistance() * 1.5f)
        {
            _broken = true;
            _spell?.EndEffect();
            PortKit.Notice(new Text("Witch.Broken").ToString());
        }
    }

    // ── 見た目 (自分の画面だけ) ──
    // 唱えている間: 相手の足元に魔法陣 (回る二重の輪と五芒星) が描き上がっていき、自分の手から相手へ光の粒が渦を巻いて流れる。
    // 唱え終えると紫の星が弾け、呪った人の周りには時々小さな星が舞う

    private static readonly Color Rune = new(0.78f, 0.3f, 1f, 0.9f);
    private static readonly Color RuneDim = new(0.45f, 0.1f, 0.7f, 0.6f);
    private static readonly Color Star = new(1f, 0.85f, 1f, 0.95f);
    private readonly DotSet _circle = new("MrpWitchCircle");
    private float _start, _flowNext, _twinkleNext;

    private static bool Shown(PlayerControl p) => p && !p.inVent && p.Visible && !p.Data.IsDead;

    private void Fx()
    {
        var t = _target;
        if (MeetingHud.Instance || !Shown(t) || !Shown(Player)) _circle.Hide();
        else
        {
            float k = Mathf.Clamp01((Time.time - _start) / Mathf.Max(0.1f, SpellDuration));
            Vector2 c = t.GetTruePosition();
            float z = RoleSparks.DepthAt(c.y) + 0.02f; // 足元の床の絵 (体より奥)
            const int ring = 28, star = 5, perEdge = 7;
            float spin = Time.time * 0.8f;
            float rx = 0.62f, ry = rx * 0.5f;
            int edges = Mathf.CeilToInt(star * Mathf.Clamp01(k * 1.25f) * perEdge);
            int shown = ring * 2 + edges;
            _circle.Show(shown, i =>
            {
                if (i < ring * 2)
                {
                    // 外の輪は回り、内の輪は逆へ回る。描き上がるまでは一部だけ
                    bool outer = i < ring;
                    int j = outer ? i : i - ring;
                    float a = (outer ? spin : -spin * 1.4f) + j * Mathf.PI * 2f / ring;
                    float rr = outer ? 1f : 0.82f;
                    bool on = j < ring * Mathf.Clamp01(k * 2f);
                    var col = on ? (outer ? Rune : RuneDim) : UnityEngine.Color.clear;
                    return (new Vector3(c.x + Mathf.Cos(a) * rx * rr, c.y + Mathf.Sin(a) * ry * rr, z), col, outer ? 0.06f : 0.04f);
                }
                // 五芒星: 頂点を 2 つ飛ばしで結ぶ
                int e = (i - ring * 2) / perEdge, s = (i - ring * 2) % perEdge;
                float a0 = spin * 0.5f + Mathf.PI * 0.5f + e * 2f * Mathf.PI * 2f / star;
                float a1 = a0 + 2f * Mathf.PI * 2f / star;
                float f = (s + 0.5f) / perEdge;
                float ax = Mathf.Lerp(Mathf.Cos(a0), Mathf.Cos(a1), f) * rx * 0.8f, ay = Mathf.Lerp(Mathf.Sin(a0), Mathf.Sin(a1), f) * ry * 0.8f;
                float glow = 0.75f + 0.25f * Mathf.Sin(Time.time * 6f + i);
                return (new Vector3(c.x + ax, c.y + ay, z), Rune * new Color(1f, 1f, 1f, glow), 0.045f);
            });
            // 手から相手へ渦を巻いて流れる光
            if (_flowNext < Time.time - 0.3f) _flowNext = Time.time;
            while (_flowNext <= Time.time)
            {
                float age = Time.time - _flowNext;
                _flowNext += 0.05f;
                Vector3 from = Player.transform.position + new Vector3(Player.cosmetics.FlipX ? -0.2f : 0.2f, 0.1f, 0f);
                Vector3 to = t.transform.position + new Vector3(0f, 0.1f, 0f);
                float ang = Random.Range(0f, Mathf.PI * 2f);
                var vel = (to - from) / 0.45f + new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f) * 0.6f;
                from.z = RoleSparks.DepthAt(Mathf.Min(Player.GetTruePosition().y, c.y));
                RoleSparks.Spawn(from, vel, Random.value < 0.4f ? Star : Rune, 0.45f, 0.05f, 0.02f, 0f, 0f, age);
            }
        }

        // 呪った人の周りに時々星が舞う
        if (MeetingHud.Instance || Time.time < _twinkleNext || Spelled.Count == 0) return;
        _twinkleNext = Time.time + 0.25f;
        foreach (var id in Spelled)
        {
            var p = PortKit.ById(id);
            if (!Shown(p) || Random.value > 0.5f) continue;
            var at = p.transform.position + new Vector3(Random.Range(-0.3f, 0.3f), Random.Range(-0.1f, 0.45f), 0f);
            at.z = RoleSparks.DepthAt(p.GetTruePosition().y);
            RoleSparks.Spawn(at, new Vector3(0f, 0.25f, 0f), Random.value < 0.5f ? Star : Rune, 0.8f, 0.07f, 0f);
        }
    }

    // 唱え終えた時: 紫の星が弾ける
    private static void Burst(PlayerControl t)
    {
        if (!Shown(t)) return;
        var c = t.transform.position;
        c.z = RoleSparks.DepthAt(t.GetTruePosition().y);
        for (int i = 0; i < 22; i++)
        {
            float a = i * Mathf.PI * 2f / 22f;
            float v = Random.Range(1.2f, 2.2f);
            RoleSparks.Spawn(c, new Vector3(Mathf.Cos(a) * v, Mathf.Sin(a) * v * 0.8f, 0f), i % 2 == 0 ? Star : Rune, 0.6f, 0.08f, 0.01f, 0f, 2.5f);
        }
    }

    // ホスト: 会議の終わりに、生きているウィッチがいれば呪われた人が死ぬ (死体は残らない)
    [HostOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (Spelled.Count == 0) return;
        Witch alive = null;
        foreach (var r in RoleState.All)
            if (r is Witch w && PortKit.Alive(w.Player)) { alive = w; break; }
        if (alive != this)
        {
            // 生きているウィッチがいない: 呪いは解ける (客の ☆ と選べない印も消す・2 人目のウィッチは先頭の Count == 0 で帰る)
            if (alive == null) { Spelled.Clear(); ClearCall.Send(0); }
            return;
        }
        var ids = new List<byte>(Spelled);
        Spelled.Clear();
        foreach (var id in ids)
        {
            var p = PortKit.ById(id);
            if (!PortKit.Alive(p)) continue;
            // 守り (守護者の身代わりなど) は試合中のキルと同じく通す。ウィッチ自身の「普通のキル無し」で断らないよう能力の印を付けて聞く
            string no;
            UchuKit.AbilityKill = PlayerId;
            try { no = p.PlayerId != PlayerId ? BeforeMurderEvent.Ask(Player, p) : null; }
            finally { UchuKit.AbilityKill = -1; }
            if (no == null) PortKit.HostVanish(Player, p, PortKit.Harm.Curse);
        }
        ClearCall.Send(0);
    }

    private static readonly RemoteCall<byte> ClearCall = new("Witch.Clear", Route.HostToAll,
        (w, _) => w.Write((byte)0), r => r.ReadByte(), (_, _) => Spelled.Clear());

    [HostOnly]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (NormalKill || e.Canceled || !e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        if (UchuKit.AbilityKill != PlayerId) e.Cancel("witch has no normal kill");
    }
}
