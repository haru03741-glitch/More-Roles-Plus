// Based on the Nova role of https://github.com/ykundesu/SuperNewRoles (GPL-3.0) as described by its role card
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ノヴァ (インポスター): 「発射」で溜めてから、まっすぐな光線を撃つ。光線に当たった人を倒す (壁で止まる)。
// 撃つ向きは設定で選ぶ: 自由 (マウスの向き) / 左右 (向いている方) / 上下左右 (マウスの向きに近い方)。溜めている間は動けない
public sealed class Nova : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Nova.Name");
    public override string Reading => "のゔぁ";
    public override Text Blurb => new("Nova.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Nova.Fire", "nova_fire") };

    public static readonly ChoiceOpt Mode = new("Nova.Mode", 0,
        new Text("Nova.Mode.Free"), new Text("Nova.Mode.LeftRight"), new Text("Nova.Mode.FourWay"));
    public static readonly FloatOpt Cooldown = new("Nova.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt Size = new("Nova.Size", 1f, 0.25f, 5f, 0.25f, "x");
    public static readonly FloatOpt Charge = new("Nova.Charge", 1.5f, 0f, 5f, 0.25f, "s");

    private const float MaxLength = 40f;
    private static readonly UnityEngine.Color BeamColor = new(1f, 0.85f, 0.4f, 1f);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Nova.Fire"), "nova_fire")
        {
            Cooldown = () => Cooldown,
            OnUse = () =>
            {
                var lp = PlayerControl.LocalPlayer;
                float angle = Aim(lp);
                MoveSpeed.Add("NovaCharge", 0f, Charge + 0.2f);
                FireCall.Send(angle);
                OnFire(this, angle);
                return true;
            },
        });
    }

    // スマホは最後に歩いた向きへ撃つので、自分の端末で向きを覚えておく
    public override void OnFixedUpdate()
    {
        if (IsLocal && System.OperatingSystem.IsAndroid()) ShotKit.Track();
    }

    // 撃つ向き (度・右が 0)。PC はマウスの方・スマホは最後に歩いた向き (タッチの位置は押したボタンの所になるため)
    private static float Aim(PlayerControl lp)
    {
        bool left = lp.cosmetics && lp.cosmetics.FlipX;
        if (Mode.Value == 1) return left ? 180f : 0f;
        float a;
        var cam = Camera.main;
        if (System.OperatingSystem.IsAndroid() || !cam)
        {
            var f = ShotKit.Facing;
            a = Mathf.Atan2(f.y, f.x) * Mathf.Rad2Deg;
        }
        else
        {
            Vector2 me = lp.GetTruePosition();
            Vector2 mouse = cam.ScreenToWorldPoint(Input.mousePosition);
            a = Mathf.Atan2(mouse.y - me.y, mouse.x - me.x) * Mathf.Rad2Deg;
        }
        if (Mode.Value == 2) a = Mathf.Round(a / 90f) * 90f;
        return a;
    }

    private static readonly RemoteCall<float> FireCall = new("Nova.Fire", Route.Anyone,
        (w, a) => w.Write(a), r => r.ReadSingle(),
        (sender, a) => { if (RoleState.Of(sender) is Nova n && n.AcceptFire()) OnFire(n, a); });

    // 受けた側の端末で: 待ち時間より明らかに早い発射は無視する
    private float _lastFireAt = -999f;

    private bool AcceptFire()
    {
        float now = Time.time;
        if (now - _lastFireAt < Cooldown * 0.5f && !Dev.DevCooldown.Zero) return false;
        _lastFireAt = now;
        return true;
    }

    // 全員の端末で: 溜めの光 → 光線。倒すのはホスト
    private static void OnFire(Nova n, float angle)
    {
        var p = n.Player;
        if (!p) return;
        float charge = Charge;
        RangedKillSnapPatch.Mark(n.PlayerId, charge + 2f);
        PortSound.Play("nova_charge", () => PortSound.Fallback("nova_charge"), 0.7f, p.GetTruePosition(), 15f);
        var glow = PortArt.Spawn(PortArt.Disc, p.GetTruePosition(), 0.2f, new UnityEngine.Color(BeamColor.r, BeamColor.g, BeamColor.b, 0.8f), -9f);
        int steps = Mathf.Max(1, (int)(charge / 0.05f));
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            PortKit.After(charge * t, () =>
            {
                if (!glow || !p) return;
                glow.transform.position = new Vector3(p.GetTruePosition().x, p.GetTruePosition().y + 0.2f, -9f);
                glow.transform.localScale = Vector3.one * Mathf.Lerp(0.2f, Size * 1.2f, t);
            });
        }
        PortKit.After(charge, () =>
        {
            if (glow) Object.Destroy(glow.gameObject);
            if (MeetingHud.Instance || !ShipStatus.Instance || !PortKit.Alive(p)) return;
            Beam(n, p.GetTruePosition(), angle);
        });
    }

    private static void Beam(Nova n, Vector2 from, float angle)
    {
        Vector2 dir = FxMath.V2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
        float length = MaxLength;
        var hit = Physics2D.Raycast(from, dir, MaxLength, Constants.ShipOnlyMask);
        if (hit.collider) length = hit.distance;
        float size = Size;
        PortSound.Play("nova_beam", () => PortSound.Fallback("nova_beam"), 1f, from, 25f);
        // 帯の絵は幅 4・高さ 1 単位。長さ × 太さに伸ばして、撃った所から先へ置く
        var beam = PortArt.Spawn(PortArt.Beam, from + dir * (length * 0.5f), 1f, BeamColor, -9f);
        beam.transform.rotation = Quaternion.Euler(0f, 0f, angle);
        beam.transform.localScale = new Vector3(length / 4f, size, 1f);
        for (int i = 0; i <= 10; i++)
        {
            float t = i / 10f;
            PortKit.After(0.05f * i, () =>
            {
                if (!beam) return;
                beam.color = new UnityEngine.Color(BeamColor.r, BeamColor.g, BeamColor.b, 1f - t);
                beam.transform.localScale = new Vector3(length / 4f, size * (1f - 0.6f * t), 1f);
            });
        }
        PortKit.After(0.55f, () => { if (beam) Object.Destroy(beam.gameObject); });
        if (AmongUsClient.Instance.AmHost) HostBeam(n.Player, from, dir, length, size);
    }

    // ホスト: 光線の帯 (太さ size) の中の生きている人を倒す。キルを防ぐ役職には聞く
    private static void HostBeam(PlayerControl invoker, Vector2 from, Vector2 dir, float length, float size)
    {
        if (!PortKit.Alive(invoker)) return;
        float half = size * 0.5f + 0.2f; // 体の半径の分だけ太く
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.inVent || p.PlayerId == invoker.PlayerId) continue;
            Vector2 d = p.GetTruePosition() - from;
            float along = Vector2.Dot(d, dir);
            if (along < 0f || along > length) continue;
            float side = Mathf.Abs(d.x * dir.y - d.y * dir.x);
            if (side > half) continue;
            if (BeforeMurderEvent.Ask(invoker, p) != null) continue;
            invoker.RpcMurderPlayer(p, true);
        }
    }
}
