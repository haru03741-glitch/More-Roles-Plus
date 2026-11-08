// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Arsonist.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// アーソニスト (第三陣営): 「塗る」で近くの人に少しの間そばにいるとオイルを塗れる (離れると失敗)。生きている全員に塗ってから「燃やす」と単独勝ち
public sealed class Arsonist : RoleBase
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => "#FF6600";
    public override Text Name => new("Arsonist.Name");
    public override string Reading => "あーそにすと";
    public override Text Blurb => new("Arsonist.Blurb");
    public override string Origin => "TOU";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Arsonist.Douse", "snr_oil"), new("Arsonist.Ignite", "snr_fire") };

    public static readonly FloatOpt DouseCooldown = new("Arsonist.Cooldown", 10f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt DouseDuration = new("Arsonist.Duration", 1f, 0.5f, 10f, 0.5f, "s");
    public static readonly BoolOpt Vent = new("Arsonist.CanVent", true);
    public static readonly BoolOpt ImpostorSight = new("Arsonist.ImpostorVision", true);

    private readonly HashSet<byte> _doused = new();
    private byte _current = 255;   // 塗っている途中の人 (自分の端末だけ)
    private Ability _douse;

    public override bool? CanVent => Vent;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    public override void OnAssigned()
    {
        _douse = AddAbility(new Ability(new Text("Arsonist.Douse"), "snr_oil")
        {
            Cooldown = () => DouseCooldown,
            Duration = () => DouseDuration,
            // 塗っている間はその人、待っている間は押せば塗る人
            ShowAim = () => _douse.InEffect ? PortKit.ById(_current) : NearestTarget(),
            OnUse = () =>
            {
                var t = NearestTarget();
                if (!t) return false;
                _current = t.PlayerId;
                _douseStart = Time.time;
                return true;
            },
            OnEnd = () =>
            {
                byte id = _current;
                _current = 255;
                var t = PortKit.ById(id);
                if (!PortKit.Alive(t) || _doused.Contains(id)) return;
                DouseCall.Send(id);
                _doused.Add(id);
                Splash(t);
            },
        });
        AddAbility(new Ability(new Text("Arsonist.Ignite"), "snr_fire")
        {
            Usable = () => AllDoused(), // 使えない間はボタンを暗くする
            OnUse = () =>
            {
                if (!AllDoused()) return false;
                IgniteCall.Send(0);
                return true;
            },
        });
    }

    // キルの届く所で、まだ塗っていない一番近い人
    private PlayerControl NearestTarget() =>
        Targeting.ClosestPlayer(Player, GameManager.Instance.LogicOptions.GetKillDistance(), _notDoused ??= (_, p) => !_doused.Contains(p.PlayerId));

    private System.Func<PlayerControl, PlayerControl, bool> _notDoused;

    // 塗っている途中に相手が死ぬか、一番近い人が変わったら失敗
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        Fx();
        if (_current == 255 || _douse == null || !_douse.InEffect) return;
        var t = PortKit.ById(_current);
        if (PortKit.Alive(t) && NearestTarget() == t) return;
        _current = 255;
        _douse.EndEffect();
    }

    private static readonly RemoteCall<byte> DouseCall = new("Arsonist.Douse", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Arsonist a) a._doused.Add(id); });

    // 自分以外の生きている全員に塗ったか
    private bool AllDoused()
    {
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p.PlayerId != PlayerId && PortKit.Alive(p) && !_doused.Contains(p.PlayerId)) return false;
        return true;
    }

    private static readonly RemoteCall<byte> IgniteCall = new("Arsonist.Ignite", Route.ToHost,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) =>
        {
            if (RoleState.Of(sender) is not Arsonist a || a._ignited || !PortKit.Alive(sender) || !a.AllDoused()) return;
            a._ignited = true;
            // 塗った人の足元のオイルに火が付く (床の火の計算に載せるので全員の画面で燃え、床の材質と水で燃え方が変わる)。
            // 燃え上がるのを見せてから勝ちを決める
            foreach (var p in PlayerControl.AllPlayerControls)
            {
                if (!PortKit.Alive(p) || !a._doused.Contains(p.PlayerId)) continue;
                Vector2 at = p.GetTruePosition();
                Terrain.TerrainApi.WorldSpill(at, IgniteRadius, 1f);
                Terrain.TerrainApi.WorldIgnite(at, IgniteRadius, 1f);
            }
            PortKit.After(IgniteDelay, () => { if (PortKit.Alive(sender) && RoleState.Of(sender) == a) GameEnd.Win(a); });
        });

    private const float IgniteRadius = 0.7f, IgniteDelay = 2.5f;
    private bool _ignited; // ホストだけ: 火を付けてから勝ちが決まるまでの間にもう一度押されても数えない

    // ── 見た目 (自分の画面だけ) ──
    // 塗っている間: 自分の手から相手へ油が弧を描いて飛び、相手の足元に進み具合の輪が出る。
    // 塗り終えた人: 体から油がときどき垂れて、足元に小さな油だまりが光る

    private static readonly Color Oil = new(0.36f, 0.22f, 0.05f, 0.95f);
    private static readonly Color OilShine = new(1f, 0.72f, 0.25f, 0.85f);
    private static readonly Color RingColor = new(1f, 0.4f, 0f, 0.9f);
    private float _douseStart, _dripNext, _streamNext;
    private readonly DotSet _ring = new("MrpArsonistRing");

    private static bool Shown(PlayerControl p) => p && !p.inVent && p.Visible && !p.Data.IsDead;

    private void Fx()
    {
        if (MeetingHud.Instance || !Shown(Player)) { _ring.Hide(); return; }
        var cur = _current != 255 && _douse != null && _douse.InEffect ? PortKit.ById(_current) : null;
        if (Shown(cur))
        {
            Vector2 feet = cur.GetTruePosition();
            float k = Mathf.Clamp01((Time.time - _douseStart) / Mathf.Max(0.1f, DouseDuration));
            _ring.Ring(feet, 0.42f, k, RingColor);
            // 間隔ごとに 1 粒。呼ばれる間が空いても途切れないよう、遅れた分は途中まで飛んだ粒で埋める
            if (_streamNext < Time.time - 0.3f) _streamNext = Time.time;
            while (_streamNext <= Time.time)
            {
                float age = Time.time - _streamNext;
                _streamNext += 0.025f;
                Vector3 from = Player.transform.position + new Vector3(Player.cosmetics.FlipX ? -0.18f : 0.18f, 0.05f, 0f);
                Vector3 to = cur.transform.position + new Vector3(Random.Range(-0.15f, 0.15f), Random.Range(0f, 0.25f), 0f);
                const float time = 0.32f, grav = 6f;
                var vel = (to - from) / time;
                vel.y += 0.5f * grav * time;
                from.z = RoleSparks.DepthAt(Mathf.Min(Player.GetTruePosition().y, feet.y));
                RoleSparks.Spawn(from, vel, Random.value < 0.25f ? OilShine : Oil, time, 0.09f, 0.06f, grav, 0f, age);
            }
        }
        else _ring.Hide();

        if (Time.time < _dripNext) return;
        _dripNext = Time.time + 0.12f;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!_doused.Contains(p.PlayerId) || !Shown(p) || Random.value > 0.6f) continue;
            Vector2 feet = p.GetTruePosition();
            float z = RoleSparks.DepthAt(feet.y);
            // 体から垂れるしずく
            var from = new Vector3(p.transform.position.x + Random.Range(-0.2f, 0.2f), p.transform.position.y + Random.Range(-0.15f, 0.2f), z);
            RoleSparks.Spawn(from, new Vector3(0f, -0.2f, 0f), Oil, 0.45f, 0.07f, 0.04f, 2.2f);
            // 足元の油だまり (少しの間だけ残る)
            var pool = new Vector3(feet.x + Random.Range(-0.22f, 0.22f), feet.y + Random.Range(-0.05f, 0.05f), z + 0.01f);
            RoleSparks.Spawn(pool, Vector3.zero, Random.value < 0.3f ? OilShine * new Color(1f, 1f, 1f, 0.7f) : Oil * new Color(1f, 1f, 1f, 0.85f), 1.6f, 0.1f, 0.17f);
        }
    }

    // 塗り終えた瞬間に油が跳ねる
    private static void Splash(PlayerControl t)
    {
        if (!Shown(t)) return;
        var c = t.transform.position;
        c.z = RoleSparks.DepthAt(t.GetTruePosition().y);
        for (int i = 0; i < 14; i++)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            var v = new Vector3(Mathf.Cos(a) * Random.Range(0.8f, 1.8f), Mathf.Sin(a) * 0.7f + Random.Range(0.8f, 1.8f), 0f);
            RoleSparks.Spawn(c, v, i % 3 == 0 ? OilShine : Oil, 0.55f, 0.1f, 0.05f, 6f);
        }
    }

    public override string NameMark(PlayerControl seen) =>
        _doused.Contains(seen.PlayerId) ? $"<color={Color}>§</color>" : null;
}
