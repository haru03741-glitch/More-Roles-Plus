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
    // 見た目の揃え方: 全員を自分の姿に / 全員をばらばらに他の人の姿に / 全員を灰色に (帽子・バイザー・スキン・ペット・名前を消す)
    public static readonly ChoiceOpt Look = new("Camouflager.Look", 0,
        new Text("Camouflager.Look.Self"), new Text("Camouflager.Look.Random"), new Text("Camouflager.Look.Gray"));

    private const byte LookSelf = 0, LookRandom = 1, LookGray = 2;
    private static byte _active = 255; // 今カモフラージュしている人
    private static byte _mode;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Camouflager.Camo"), "snr_camo")
        {
            Cooldown = () => CamoCooldown,
            Duration = () => CamoDuration,
            OnUse = () => { var plan = Plan(Player); CamoCall.Send(plan); Apply(Player, plan); return true; },
            OnEnd = () => { var off = new byte[] { 0, _mode }; CamoCall.Send(off); Apply(Player, off); },
        });
        Lifespan.OnRelease(() => { if (_active == PlayerId) Apply(Player, new byte[] { 0, _mode }); });
    }

    // 掛ける側が決める: [1, 揃え方, (人の番号, 見せる人の番号)...]。ばらばらの割り当ては全員の画面で同じになるよう送る
    private static byte[] Plan(PlayerControl camo)
    {
        byte mode = (byte)Look.Value;
        var data = new System.Collections.Generic.List<byte> { 1, mode };
        if (mode == LookRandom)
        {
            var who = new System.Collections.Generic.List<byte>();
            foreach (var p in PlayerControl.AllPlayerControls)
                if (p && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected) who.Add(p.PlayerId);
            // 自分以外の姿になるよう、並べ替えて 1 つずらす (2 人以上なら誰も自分の姿のままにならない)
            for (int i = who.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); (who[i], who[j]) = (who[j], who[i]); }
            for (int i = 0; i < who.Count; i++)
            {
                data.Add(who[i]);
                data.Add(who[(i + 1) % who.Count]);
            }
        }
        return data.ToArray();
    }

    private static readonly RemoteCall<byte[]> CamoCall = new("Camouflager.Camo", Route.Anyone,
        (w, a) => { w.Write((byte)a.Length); foreach (var b in a) w.Write(b); },
        r => { int n = r.ReadByte(); var a = new byte[n]; for (int i = 0; i < n; i++) a[i] = r.ReadByte(); return a; },
        (sender, a) => { if (RoleState.Of(sender) is Camouflager && a.Length >= 2) Apply(sender, a); });

    // 全員の端末で: 生きている全員の見た目を揃える / 戻す
    private static void Apply(PlayerControl camo, byte[] plan)
    {
        if (!camo || plan.Length < 2) return;
        bool on = plan[0] != 0;
        byte mode = plan[1];
        bool fresh = on && _active == 255;
        if (!on && _active == 255) return;
        _active = on ? camo.PlayerId : (byte)255;
        if (on) _mode = mode;
        if (fresh) Before.Clear();
        var map = new System.Collections.Generic.Dictionary<byte, byte>();
        for (int i = 2; i + 1 < plan.Length; i += 2) map[plan[i]] = plan[i + 1];
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            // 掛ける時は生きている人だけ、戻す時は途中で死んだ人も戻す。自分の姿に揃える時は自分はそのまま
            if (!p || p.Data == null || (on && p.Data.IsDead)) continue;
            if (_mode == LookSelf && p.PlayerId == camo.PlayerId) continue;
            try
            {
                if (on)
                {
                    if (fresh) Before[p.PlayerId] = p.shapeshiftTargetPlayerId;
                    if (mode == LookSelf) p.Shapeshift(camo, true);
                    else if (mode == LookGray) p.RawSetOutfit(GrayOutfit(), PlayerOutfitType.Shapeshifted);
                    else if (map.TryGetValue(p.PlayerId, out byte src) && PortKit.ById(src) is { Data: not null } t)
                        p.RawSetOutfit(t.Data.DefaultOutfit, PlayerOutfitType.Shapeshifted);
                    continue;
                }
                // 自分の姿に揃えた時: 最中に本編の変身が姿を替えた人はそのまま
                if (_mode == LookSelf && p.shapeshiftTargetPlayerId != camo.PlayerId) continue;
                Restore(p);
            }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"camouflage: {e.Message}"); }
        }
        if (!on) Before.Clear();
        Menu.MrpCosmetics.HideAll = on && _mode == LookGray;
    }

    // 掛ける前の姿へ (本編の変身中だった人はその変身先の姿)
    private static void Restore(PlayerControl p)
    {
        PlayerControl back = p;
        if (Before.TryGetValue(p.PlayerId, out int was) && was >= 0 && was != p.PlayerId)
        {
            var t = PortKit.ById((byte)was);
            if (t && t.Data != null) back = t;
        }
        if (_mode == LookSelf) { p.Shapeshift(back, true); return; }
        if (back == p) p.RawSetOutfit(p.Data.DefaultOutfit, PlayerOutfitType.Default);
        else p.RawSetOutfit(back.Data.DefaultOutfit, PlayerOutfitType.Shapeshifted);
    }

    // 灰色で何も着けていない・名前の無い姿
    private static NetworkedPlayerInfo.PlayerOutfit GrayOutfit()
    {
        var o = new NetworkedPlayerInfo.PlayerOutfit
        {
            PlayerName = "",
            ColorId = GrayColor(),
            HatId = CosmeticsLayer.EMPTY_HAT_ID,
            VisorId = CosmeticsLayer.EMPTY_VISOR_ID,
            SkinId = CosmeticsLayer.EMPTY_SKIN_ID,
            PetId = CosmeticsLayer.EMPTY_PET_ID,
            NamePlateId = "",
        };
        return o;
    }

    private static int GrayColor()
    {
        var names = Palette.ColorNames;
        if (names != null)
            for (int i = 0; i < names.Length; i++)
                if (names[i] == StringNames.ColorGray) return i;
        return 15;
    }

    // 掛ける前の本編の変身先 (人の番号 → 変身先の番号・-1 = 変身していない)
    private static readonly System.Collections.Generic.Dictionary<byte, int> Before = new();
}
