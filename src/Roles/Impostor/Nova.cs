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
    internal static float? TestAngle; // 試験用: 撃つ向きを固定する (bridge の nova aim)

    private static float Aim(PlayerControl lp)
    {
        if (TestAngle is float fixedAngle) return fixedAngle;
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

    // 全員の端末で: 溜め (光の球と狙いの線) → 光線。倒すのと地形を焼くのはホスト
    private static void OnFire(Nova n, float angle)
    {
        var p = n.Player;
        if (!p) return;
        float charge = Charge;
        RangedKillSnapPatch.Mark(n.PlayerId, charge + 2f);
        Vector2 from0 = p.GetTruePosition();
        PortSound.Play("nova_charge", () => PortSound.Fallback("nova_charge"), 0.8f, from0, 15f);
        NovaFx.BeginCharge(p, angle, charge, Size, Reach(from0, angle, out _));
        PortKit.After(charge, () =>
        {
            if (MeetingHud.Instance || !ShipStatus.Instance || !PortKit.Alive(p)) { NovaFx.CancelCharge(p); return; }
            Beam(n, p.GetTruePosition(), angle);
        });
    }

    // 光線の届く長さ (壁で止まる)
    private static float Reach(Vector2 from, float angle, out bool wall)
    {
        float r = angle * (System.MathF.PI / 180f);
        var hit = Physics2D.Raycast(from, FxMath.V2(FxMath.Cos(r), FxMath.Sin(r)), MaxLength, Constants.ShipOnlyMask);
        wall = hit.collider;
        return wall ? hit.distance : MaxLength;
    }

    private static void Beam(Nova n, Vector2 from, float angle)
    {
        float r = angle * (System.MathF.PI / 180f);
        Vector2 dir = FxMath.V2(FxMath.Cos(r), FxMath.Sin(r));
        float length = Reach(from, angle, out bool wall);
        float size = Size;
        PortSound.Play("nova_beam", () => PortSound.Fallback("nova_beam"), 1f, from, 25f);
        NovaFx.Fire(n.Player, from, angle, length, size, wall);
        if (wall) PortSound.Play("nova_impact", null, 1f, from + dir * length, 25f);
        if (AmongUsClient.Instance.AmHost) HostBeam(n.Player, from, dir, length, size, wall);
    }

    // ホスト: 光線の帯 (太さ size) の中の生きている人を倒す。キルを防ぐ役職には聞く
    private static void HostBeam(PlayerControl invoker, Vector2 from, Vector2 dir, float length, float size, bool wall)
    {
        if (!PortKit.Alive(invoker)) return;
        HostScorch(from, dir, length, size, wall);
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

    // ホスト: 当たった壁をプラズマの爆発で抜き (光線の向きへ伸びた形)、通った床に熱を足す (水は湯気になり、燃える物には火が付く)。
    // 熱を足す所は長さに応じて最大 5 か所 (電文を増やしすぎない)
    private const int MaxHeatPoints = 5;

    private static void HostScorch(Vector2 from, Vector2 dir, float length, float size, bool wall)
    {
        if (wall)
        {
            float r = System.Math.Clamp(0.6f + 0.5f * size, 0.6f, Terrain.TerrainPermit.MaxRadius);
            var res = Terrain.TerrainApi.WorldBlast(from + dir * length, r, dir, 0.7f);
            if (!res.Ok) Plugin.Logger.LogInfo($"nova: terrain blast refused ({res.Why})");
        }
        int n = System.Math.Clamp((int)(length / 2f), 1, MaxHeatPoints);
        float rad = 0.3f + 0.2f * size;
        for (int i = 0; i < n; i++)
            Terrain.TerrainApi.WorldIgnite(from + dir * (length * (i + 0.5f) / n), rad, 0.6f);
    }
}
