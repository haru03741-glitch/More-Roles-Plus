// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Camouflager.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// カモフラージャー (インポスター): 「カモフラージュ」でしばらく全員の見た目を自分と同じにする
public sealed class Camouflager : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Camouflager.Name");
    public override string Reading => "かもふらーじゃー";
    public override Text Blurb => new("Camouflager.Blurb");
    public override string Origin => "TOR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Camouflager.Camo", "snr_camo") };
    public static readonly FloatOpt CamoCooldown = new("Camouflager.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt CamoDuration = new("Camouflager.Duration", 10f, 2.5f, 60f, 2.5f, "s");

    private static byte _active = 255; // 今カモフラージュしている人

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Camouflager.Camo"), "snr_camo")
        {
            Cooldown = () => CamoCooldown,
            Duration = () => CamoDuration,
            OnUse = () => { CamoCall.Send(true); Apply(Player, true); return true; },
            OnEnd = () => { CamoCall.Send(false); Apply(Player, false); },
        });
        Lifespan.OnRelease(() => { if (_active == PlayerId) Apply(Player, false); });
    }

    private static readonly RemoteCall<bool> CamoCall = new("Camouflager.Camo", Route.Anyone,
        (w, on) => w.Write(on), r => r.ReadBoolean(),
        (sender, on) => { if (RoleState.Of(sender) is Camouflager) Apply(sender, on); });

    // 全員の端末で: 生きている全員を camo の姿にする / 戻す
    private static void Apply(PlayerControl camo, bool on)
    {
        if (!camo) return;
        bool fresh = on && _active == 255;
        _active = on ? camo.PlayerId : (byte)255;
        if (fresh) Before.Clear();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            // 掛ける時は生きている人だけ、戻す時は途中で死んだ人も戻す
            if (!p || p.Data == null || (on && p.Data.IsDead) || p.PlayerId == camo.PlayerId) continue;
            try
            {
                if (on)
                {
                    if (fresh) Before[p.PlayerId] = p.shapeshiftTargetPlayerId;
                    p.Shapeshift(camo, true);
                    continue;
                }
                // 最中に本編の変身が姿を替えた人はそのまま。替わっていなければ掛ける前の姿 (本編の変身中ならその姿) へ
                if (p.shapeshiftTargetPlayerId != camo.PlayerId) continue;
                PlayerControl back = p;
                if (Before.TryGetValue(p.PlayerId, out int was) && was >= 0 && was != p.PlayerId)
                {
                    var t = PortKit.ById((byte)was);
                    if (t && t.Data != null) back = t;
                }
                p.Shapeshift(back, true);
            }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"camouflage: {e.Message}"); }
        }
        if (!on) Before.Clear();
    }

    // 掛ける前の本編の変身先 (人の番号 → 変身先の番号・-1 = 変身していない)
    private static readonly System.Collections.Generic.Dictionary<byte, int> Before = new();
}
