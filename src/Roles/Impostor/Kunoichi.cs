// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Kunoichi.cs, Roles/Ability/KunoichiAbility.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// クノイチ (インポスター): 普通のキルはできない。「クナイ」でマウスの方へクナイを投げ、同じ人に決まった数当てるとキルする
// (クナイは自分の画面にだけ見える)。設定で、しばらく止まっていると (かボタンで) 透明になり、動くと元に戻る
public sealed class Kunoichi : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Kunoichi.Name");
    public override string Reading => "くのいち";
    public override Text Blurb => new("Kunoichi.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Kunoichi.Kunai", "snr_kunai"), new("Kunoichi.Show", "snr_kunai"), new("Kunoichi.Hide", "snr_kunai") };

    public static readonly FloatOpt KunaiCooldown = new("Kunoichi.Cooldown", 2.5f, 0f, 15f, 0.5f, "s");
    public static readonly IntOpt KillKunai = new("Kunoichi.KillKunai", 10, 1, 20);
    public static readonly BoolOpt IsHide = new("Kunoichi.IsHide", true);
    public static readonly BoolOpt HideByButton = new("Kunoichi.HideByButton", true) { When = () => IsHide };
    public static readonly FloatOpt HideTime = new("Kunoichi.HideTime", 3f, 0.5f, 10f, 0.5f, "s") { When = () => IsHide };
    public static readonly BoolOpt ThrowWhileHidden = new("Kunoichi.ThrowWhileHidden", false) { When = () => IsHide };

    private const float Speed = 10f, Life = 1.5f, MaxDistance = 7f, HitRadius = 0.35f, Hand = 0.8f, Height = 0.35f;

    private sealed class Kunai
    {
        public GameObject Obj;
        public Vector2 Velocity;
        public float Born;
    }

    private readonly List<Kunai> _kunai = new();
    private readonly Dictionary<byte, int> _hits = new();
    private GameObject _held;
    private bool _show;

    // 透明 (全員の端末で同じように持つ)
    private bool _hidden;
    private float _still;
    private Vector2 _last;

    private bool Hidden => _hidden;
    private bool CanThrow => !_hidden || ThrowWhileHidden;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Kunoichi.Kunai"), "snr_kunai")
        {
            Cooldown = () => KunaiCooldown,
            CooldownAtStart = false,
            OnUse = () =>
            {
                if (!CanThrow || Player.inVent || !Player.CanMove) return false;
                Throw();
                return true;
            },
        });
        AddAbility(new Ability(new Text("Kunoichi.Show"), "snr_kunai")
        {
            CooldownAtStart = false,
            OnUse = () =>
            {
                if (!CanThrow) return false;
                _show = !_show;
                return true;
            },
        });
        if (IsHide && HideByButton && HideTime > 0f)
            AddAbility(new Ability(new Text("Kunoichi.Hide"), "snr_kunai")
            {
                CooldownAtStart = false,
                OnUse = () =>
                {
                    if (_hidden || _still < HideTime || !Player.CanMove) return false;
                    HideCall.Send(true);
                    SetHidden(true);
                    return true;
                },
            });
        Lifespan.OnRelease(() =>
        {
            ClearKunai();
            if (_held) Object.Destroy(_held);
            SetHidden(false);
        });
    }

    // マウスの向き (スマホでは最後に歩いた向き)
    private Vector2 Aim()
    {
        if (System.OperatingSystem.IsAndroid() || !Camera.main) return ShotKit.Facing;
        Vector2 mouse = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        var d = mouse - Player.GetTruePosition();
        return d.sqrMagnitude < 0.0001f ? Vector2.right : d.normalized;
    }

    private static GameObject MakeKunai(string name)
    {
        var go = new GameObject(name);
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 3;
        lr.useWorldSpace = false;
        lr.SetPosition(0, new Vector3(-0.3f, 0f, 0f));
        lr.SetPosition(1, new Vector3(0.15f, 0f, 0f));
        lr.SetPosition(2, new Vector3(0.35f, 0f, 0f));
        lr.widthCurve = new AnimationCurve(new Keyframe(0f, 0.05f), new Keyframe(0.6f, 0.12f), new Keyframe(1f, 0.01f));
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = new Color(0.25f, 0.25f, 0.3f);
        lr.endColor = new Color(0.8f, 0.82f, 0.9f);
        return go;
    }

    // 自分の端末で: クナイを投げる
    private void Throw()
    {
        var dir = Aim();
        var at = Player.GetTruePosition() + dir * Hand + new Vector2(0f, Height);
        var go = MakeKunai("MrpKunai");
        go.transform.position = new Vector3(at.x, at.y, Player.transform.position.z - 0.5f);
        go.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        _kunai.Add(new Kunai { Obj = go, Velocity = dir * Speed, Born = Time.time });
    }

    private void ClearKunai()
    {
        foreach (var k in _kunai) if (k.Obj) Object.Destroy(k.Obj);
        _kunai.Clear();
    }

    public override void OnFixedUpdate()
    {
        TickHide();
        if (!IsLocal) return;
        ShotKit.Track();
        // キルボタンは使わない
        var hud = Vanilla.Hud;
        if (hud && hud.KillButton && hud.KillButton.gameObject.activeSelf) hud.KillButton.gameObject.SetActive(false);
        TickHeld();
        if (_kunai.Count == 0) return;
        if (!PortKit.Alive(Player) || MeetingHud.Instance)
        {
            ClearKunai();
            return;
        }
        for (int i = _kunai.Count - 1; i >= 0; i--)
        {
            var k = _kunai[i];
            if (!k.Obj) { _kunai.RemoveAt(i); continue; }
            k.Obj.transform.position += (Vector3)(k.Velocity * Time.fixedDeltaTime);
            Vector2 pos = k.Obj.transform.position;
            if (Time.time - k.Born > Life || Vector2.Distance(Player.GetTruePosition(), pos) > MaxDistance)
            {
                Object.Destroy(k.Obj);
                _kunai.RemoveAt(i);
                continue;
            }
            foreach (var p in PlayerControl.AllPlayerControls)
            {
                if (!PortKit.Alive(p) || p.PlayerId == PlayerId || p.inVent) continue;
                if (Vector2.Distance(p.GetTruePosition() + new Vector2(0f, 0.4f), pos) >= HitRadius) continue;
                Hit(p);
                Object.Destroy(k.Obj);
                _kunai.RemoveAt(i);
                break;
            }
        }
    }

    // 自分の端末で: 当たった数を数え、決まった数に届いたらキルする
    private void Hit(PlayerControl p)
    {
        _hits.TryGetValue(p.PlayerId, out int n);
        n++;
        if (n >= KillKunai)
        {
            n = 0;
            KillCall.Send(p.PlayerId);
            if (AmongUsClient.Instance.AmHost) PortKit.HostKill(Player, p);
        }
        _hits[p.PlayerId] = n;
    }

    private static readonly RemoteCall<byte> KillCall = new("Kunoichi.Kill", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) =>
        {
            if (RoleState.Of(sender) is Kunoichi && PortKit.Alive(sender) && PortKit.ById(id) is { } t) PortKit.HostKill(sender, t);
        });

    // 手に持ったクナイ (自分の画面だけ)
    private void TickHeld()
    {
        bool show = _show && CanThrow && PortKit.Alive(Player) && !MeetingHud.Instance && !Player.inVent;
        if (!show)
        {
            if (_held) _held.SetActive(false);
            return;
        }
        if (!_held) _held = MakeKunai("MrpKunaiHeld");
        var dir = Aim();
        var at = Player.GetTruePosition() + dir * Hand;
        _held.transform.position = new Vector3(at.x, at.y + Height, Player.transform.position.z - 0.5f);
        _held.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        _held.SetActive(true);
    }

    // 全員の端末で: 止まっている時間を数え、動いたら透明をやめる
    private void TickHide()
    {
        if (!IsHide || HideTime <= 0f || !Player) return;
        if (!PortKit.Alive(Player))
        {
            SetHidden(false);
            _still = 0f;
            return;
        }
        var now = Player.GetTruePosition();
        if (Vector2.Distance(now, _last) < 0.01f) _still += Time.fixedDeltaTime;
        else
        {
            _still = 0f;
            if (_hidden) SetHidden(false);
        }
        _last = now;
        if (!HideByButton && !_hidden && !MeetingHud.Instance && Player.CanMove && _still >= HideTime) SetHidden(true);
    }

    private static readonly RemoteCall<bool> HideCall = new("Kunoichi.Hide", Route.Anyone,
        (w, on) => w.Write(on), r => r.ReadBoolean(),
        (sender, on) => { if (RoleState.Of(sender) is Kunoichi k) k.SetHidden(on); });

    // 仲間 (インポスター) からはうっすら、他の人からは見えない
    private void SetHidden(bool on)
    {
        if (_hidden == on || !Player) return;
        _hidden = on;
        float alpha = !on ? 1f : PortKit.IsImpostor(PlayerControl.LocalPlayer) ? 0.1f : 0f;
        PortKit.SetOpacity(Player, alpha);
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        SetHidden(false);
        ClearKunai();
    }

    // 会議が終わったら死んだ人の数は消す
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        var dead = new List<byte>();
        foreach (var kv in _hits) if (!PortKit.Alive(PortKit.ById(kv.Key))) dead.Add(kv.Key);
        foreach (var id in dead) _hits.Remove(id);
    }

    // 自分の画面: クナイを当てた数
    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId != PlayerId && _hits.TryGetValue(seen.PlayerId, out int n) && n > 0 ? $" <color=#FF1919>({n}/{(int)KillKunai})</color>" : null;
}
