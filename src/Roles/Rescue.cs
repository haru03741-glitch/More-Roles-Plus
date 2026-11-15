using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 瀕死の人 (地形で崩れ落ちた人・外壁の穴の縁にしがみついている人) を、近くの生きている人が長押しで助け出す。
// ボタンは近くに瀕死の人がいる間だけ出す。助けるかはホストが確かめて全員へ配る (守り霊が救う時も同じ配り方)
internal static class Rescue
{
    internal const byte ByLiving = 0, BySpirit = 1;
    private const float Near = 3f;        // ボタンを出す距離
    private const float Reach = 1f;       // 助け出せる距離
    private const float HoldTime = 1.5f;  // 長押しの秒
    private const int CheckEvery = 6;     // 何刻みごとに近くを見るか

    private static Lifespan _life;
    private static ModButton _button;
    private static int _check;

    // 助け出せる状態か (全員の端末で同じ答えになる物だけを見る)
    internal static bool Dying(byte id) => TerrainDeath.IsDying(id) || Terrain.CrewGrip.Clinging(id);

    // ── ホストへの依頼と、全員への知らせ ────────────────────────────────
    private static readonly RemoteCall<byte> Request = new("Rescue.Request", Route.ToHost,
        (w, m) => w.Write(m), r => r.ReadByte(),
        (sender, target) =>
        {
            var t = PortKit.ById(target);
            string why = Refuse(sender, t);
            if (why != null) { Plugin.Logger.LogInfo($"[Rescue] refused {(sender ? sender.PlayerId : 255)}->{target}: {why}"); return; }
            HostSave(sender, t, ByLiving);
        }, perSecond: 4);

    private static readonly RemoteCall<(byte target, byte by, byte kind)> Saved = new("Rescue.Saved", Route.HostToAll,
        (w, m) => { w.Write(m.target); w.Write(m.by); w.Write(m.kind); }, r => (r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (_, m) => Apply(m.target, m.by, m.kind));

    private static string Refuse(PlayerControl user, PlayerControl target)
    {
        if (!user || !target || user == target) return "who";
        if (MeetingHud.Instance || ExileController.Instance || !ShipStatus.Instance) return "not now";
        if (user.Data == null || user.Data.IsDead || user.inVent || Dying(user.PlayerId)) return "user";
        if (target.Data == null || target.Data.IsDead || !Dying(target.PlayerId)) return "not dying";
        return Targeting.Unreachable(user, target, Reach + 0.5f);
    }

    // ホスト (確かめ用): by が target を長押しで助け出したのと同じ道で確かめて配る。断った理由 (通れば null)
    internal static string HostTry(PlayerControl by, PlayerControl target)
    {
        string why = Refuse(by, target);
        if (why == null) HostSave(by, target, ByLiving);
        return why;
    }

    // ホスト: 認めた助け出しを全員へ (守り霊の能力もここを通す)
    internal static void HostSave(PlayerControl by, PlayerControl target, byte kind)
    {
        if (!by || !target) return;
        Saved.Send((target.PlayerId, by.PlayerId, kind));
        Apply(target.PlayerId, by.PlayerId, kind);
    }

    internal static void Apply(byte target, byte by, byte kind)
    {
        Plugin.Logger.LogInfo($"[Rescue] saved {target} by {by} kind={kind}");
        TerrainDeath.OnRescued(target);
        Terrain.CrewGrip.OnRescued(target);
        if (kind == BySpirit) Addons.WardSpirit.OnSaved(target, by);
    }

    // ── 自分の端末: 近くに瀕死の人がいる間だけボタンを出す (RoleFrame.Tick から) ──────
    internal static void Tick()
    {
        if (++_check < CheckEvery) return;
        _check = 0;
        bool show = Nearest(Near) != null;
        if (!show)
        {
            if (_life != null) { _life.Release(); _life = null; _button = null; }
            return;
        }
        if (_life == null || _life.IsDead)
        {
            if (!Vanilla.Hud) return;
            _life = RoleState.Match.Child();
            _button = ModButton.Create(_life, ButtonIcons.Get("rescue_pull"), new Text("Rescue.Pull"), 0f, () => Nearest(Reach) != null);
            _button?.SetHold(() => 0f, () => HoldTime, Released, new Text("Ability.Hold"));
        }
        _button?.SetUsable(Nearest(Reach) != null);
    }

    private static bool Released(float held)
    {
        if (held < HoldTime - 0.05f) return false;
        var t = Nearest(Reach);
        if (!t) return false;
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost)
        {
            string why = Refuse(PlayerControl.LocalPlayer, t);
            if (why != null) return false;
            HostSave(PlayerControl.LocalPlayer, t, ByLiving);
        }
        else Request.Send(t.PlayerId);
        return true;
    }

    // 自分から r 以内の一番近い瀕死の人 (自分が生きていて動ける時だけ)
    private static PlayerControl Nearest(float r)
    {
        if (!TerrainDeath.AnyDoomed && !Terrain.CrewGrip.AnyClinging) return null; // 誰も瀕死でない間は Unity に触らない
        if (!ShipStatus.Instance || MeetingHud.Instance) return null;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead || lp.inVent || Dying(lp.PlayerId)) return null;
        Vector2 me = lp.GetTruePosition();
        PlayerControl best = null;
        float bd = r * r;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p == lp || p.Data == null || p.Data.IsDead || !Dying(p.PlayerId)) continue;
            Vector2 q = p.GetTruePosition();
            float dx = q.x - me.x, dy = q.y - me.y, d = dx * dx + dy * dy;
            if (d < bd) { bd = d; best = p; }
        }
        return best;
    }
}
