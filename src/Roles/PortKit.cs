using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 他の mod から移した役職が共通で使う小物: ホストでのキル・自決の依頼・陣営の見分け・自分だけへの知らせ
public static class PortKit
{
    // 陣営の判定で「インポスター」と出る役職 (狼憑き)。シェリフなどの判定が見る
    public interface IAppearsImpostor { }

    // 恋人の印 (ラバーズのアドオン)。シェリフなどの判定が見る
    public interface ILovers { }

    public static bool IsLover(PlayerControl p)
    {
        if (!p) return false;
        foreach (var a in RoleState.AddonsOf(p.PlayerId))
            if (a is ILovers) return true;
        return false;
    }

    private static bool AmHost => AmongUsClient.Instance && AmongUsClient.Instance.AmHost;

    // ホストの端末でだけ: killer が target を倒す (全員の画面で本編のキル)。
    // 能力の OnUsed は全員の端末で呼ばれるので、そこからそのまま呼んでよい。
    // 自決でなければキルを防ぐ役職 (BeforeMurderEvent) に先に聞く。倒せたら true
    public static bool HostKill(PlayerControl killer, PlayerControl target)
    {
        if (!AmHost || !killer || !target || target.Data == null || target.Data.IsDead) return false;
        if (killer.PlayerId != target.PlayerId && BeforeMurderEvent.Ask(killer, target) != null) return false;
        killer.RpcMurderPlayer(target, true);
        return true;
    }

    // ホストの端末でだけ: その人が自分で死ぬ
    public static void HostSuicide(PlayerControl p) => HostKill(p, p);

    // ホストの端末でだけ: killer が target を死体を残さずに倒す (吹き飛ばす・打ち上げるなど)。killer == target なら自決。
    // 全員の端末で本編の追放の死に方を借り、倒された出来事 (PlayerMurderedEvent) を起こす
    public static void HostVanish(PlayerControl killer, PlayerControl target)
    {
        if (!AmHost || !killer || !target || target.Data == null || target.Data.IsDead) return;
        VanishCall.Send((killer.PlayerId, target.PlayerId));
        OnVanish(killer.PlayerId, target.PlayerId);
    }

    // 死体を残さずに倒している最中の人 (追放の出来事を起こさない印)
    internal static int Vanishing = -1;

    private static readonly RemoteCall<(byte killer, byte target)> VanishCall = new("PortKit.Vanish", Route.HostToAll,
        (w, m) => { w.Write(m.killer); w.Write(m.target); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => OnVanish(m.killer, m.target));

    private static void OnVanish(byte killerId, byte targetId)
    {
        var killer = ById(killerId);
        var target = ById(targetId);
        if (!target || target.Data == null || target.Data.IsDead) return;
        Vanishing = target.PlayerId;
        try { target.Exiled(); }
        finally { Vanishing = -1; }
        if (MeetingHud.Instance) MeetingDeath(target);
        MatchLog.OnMurder(killer, target);
        if (RoleState.All.Count > 0)
            Events<PlayerMurderedEvent>.Run(new PlayerMurderedEvent { Killer = killer ? killer : target, Target = target });
    }

    // 会議中に死んだ人を名札に反映する: 死んだ印を付け、その人への票とその人の票を外す。ホストは投票が揃ったかを見直す
    private static void MeetingDeath(PlayerControl target)
    {
        var mh = MeetingHud.Instance;
        foreach (var pva in mh.playerStates)
        {
            if (!pva) continue;
            if (pva.PlayerId.Value == target.PlayerId)
            {
                pva.SetDead(true);
                if (pva.Overlay) pva.Overlay.gameObject.SetActive(true);
                if (pva.DidVote) pva.UnsetVote();
            }
            else if (pva.DidVote && pva.VotedForId.Value == target.PlayerId)
            {
                pva.UnsetVote();
                // 自分の票だったら選び直せるように戻す
                var lp = PlayerControl.LocalPlayer;
                if (lp && pva.PlayerId.Value == lp.PlayerId) mh.ClearVote(pva.PlayerId, false);
            }
        }
        if (AmHost) mh.CheckForEndVoting();
    }

    // 自分の端末から: 自分を死なせる (ホストへ頼む)。body = false なら死体を残さない
    public static void Suicide(bool body = true) => SuicideCall.Send(body ? (byte)0 : (byte)1);

    private static readonly RemoteCall<byte> SuicideCall = new("PortKit.Suicide", Route.ToHost,
        (w, kind) => w.Write(kind), r => r.ReadByte(),
        (sender, kind) =>
        {
            if (kind == 1) HostVanish(sender, sender);
            else HostSuicide(sender);
        });

    // ホストの端末でだけ: キルを防いだことを全員へ知らせる。キルした人の待ち時間を戻し、キルした人と幽霊の画面で
    // 狙われた人に守りの演出を出す (守った本人は気付かない)
    public static void HostBlocked(PlayerControl killer, PlayerControl target)
    {
        if (!AmHost || !killer || !target) return;
        BlockedCall.Send((killer.PlayerId, target.PlayerId));
        OnBlocked(killer.PlayerId, target.PlayerId);
    }

    private static readonly RemoteCall<(byte killer, byte target)> BlockedCall = new("PortKit.Blocked", Route.HostToAll,
        (w, m) => { w.Write(m.killer); w.Write(m.target); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => OnBlocked(m.killer, m.target));

    private static void OnBlocked(byte killerId, byte targetId)
    {
        var killer = ById(killerId);
        var target = ById(targetId);
        if (!killer || !target) return;
        var lp = PlayerControl.LocalPlayer;
        if (killer.AmOwner) killer.SetKillTimer(GameManager.Instance.LogicOptions.GetKillCooldown());
        if (killer.AmOwner || (lp && lp.Data != null && lp.Data.IsDead)) target.ShowFailedMurder();
    }

    public static PlayerControl ById(byte id) => GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;

    // ---- 陣営の見分け ----

    public static bool IsImpostor(PlayerControl p) => p && p.Data != null && p.Data.Role && p.Data.Role.IsImpostor;

    // インポスター陣営と勝つクルーの役職 (マッドメイトなど)
    public static bool IsMad(PlayerControl p) => RoleState.Of(p)?.WinsWithTeam == Team.Impostor;

    public static bool IsNeutral(PlayerControl p) => RoleState.Of(p)?.Team == Team.Neutral;

    // 第三陣営と勝つクルーの役職 (ジャッカルフレンズなど)
    public static bool IsFriend(PlayerControl p) => RoleState.Of(p)?.WinsWithFaction != null;

    // クルーとして勝つ人 (クルーの役職か本編のクルーで、別の陣営と勝たない)
    public static bool IsCrewSide(PlayerControl p) =>
        p && !IsImpostor(p) && !IsNeutral(p) && !IsMad(p) && !IsFriend(p);

    // キルできる人 (インポスター・キルできる役職)
    public static bool CanKillAnyone(PlayerControl p) => IsImpostor(p) || RoleState.Of(p) is { CanKill: true };

    // 判定の役職 (シェリフなど) から見てインポスターに見えるか
    public static bool LooksImpostor(PlayerControl p)
    {
        if (IsImpostor(p) || RoleState.Of(p) is IAppearsImpostor) return true;
        if (!p) return false;
        foreach (var a in RoleState.AddonsOf(p.PlayerId))
            if (a is IAppearsImpostor) return true;
        return false;
    }

    // ---- 表示 ----

    // 自分のチャット欄にだけ知らせを出す
    public static void Notice(string text) => Dev.DevChat.Notice(Vanilla.Chat, text);

    // 自分の画面を一瞬その色で光らせる
    public static void Flash(Color color, float seconds = 0.7f)
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        var clear = new Color(color.r, color.g, color.b, 0f);
        hud.StartCoroutine(hud.CoFadeFullScreen(color, clear, seconds, false));
    }

    // ---- 船の様子 ----

    // 停電中か
    public static bool LightsOut()
    {
        var ship = ShipStatus.Instance;
        if (!ship || !ship.Systems.TryGetValue(SystemTypes.Electrical, out var s)) return false;
        var sw = s.TryCast<SwitchSystem>();
        return sw != null && sw.ActualSwitches != sw.ExpectedSwitches;
    }

    // 何かのサボタージュが起きているか (自分のタスク欄にサボタージュの直しがあるか)
    public static bool AnySabotage()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.myTasks == null) return false;
        foreach (var t in lp.myTasks)
            if (t && t.TryCast<SabotageTask>() != null) return true;
        return false;
    }

    // インポスターと同じ視界 (停電で狭まらない・部屋の設定のインポスターの視界)
    public static void ImpostorVision(ref float radius)
    {
        var ship = ShipStatus.Instance;
        if (!ship) return;
        radius = ship.MaxLightRadius * GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.ImpostorLightMod);
    }

    // その人が終えたタスクの数
    public static int TasksDone(PlayerControl p)
    {
        if (!p || p.Data == null || p.Data.Tasks == null) return 0;
        int n = 0;
        foreach (var t in p.Data.Tasks)
            if (t != null && t.Complete) n++;
        return n;
    }

    // その人のタスクが全部終わっているか (タスクが無い人は false)
    public static bool AllTasksDone(PlayerControl p)
    {
        if (!p || p.Data == null || p.Data.Tasks == null || p.Data.Tasks.Count == 0) return false;
        return TasksDone(p) >= p.Data.Tasks.Count;
    }

    // 自分の画面にだけ、その場所へ絵 (Resources/Icons) を置く。寿命が切れると消える
    public static SpriteRenderer Marker(Lifespan life, Vector2 pos, string icon, float size = 0.6f, Color? tint = null)
    {
        var go = new GameObject("MrpPortMarker");
        go.transform.position = new Vector3(pos.x, pos.y, pos.y / 1000f + 0.01f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Roles.ButtonIcons.Get(icon);
        if (tint is Color c) sr.color = c;
        go.transform.localScale = Vector3.one * (size / 1.28f);
        life.Bind(go);
        return sr;
    }

    // 生きている人の数
    public static int AliveCount()
    {
        int n = 0;
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected) n++;
        return n;
    }

    // ---- 生き返る ----

    // ホストの端末でだけ: その人を生き返らせ、死体を消して死体のあった所 (無ければ今の所) に立たせる
    public static void HostRevive(PlayerControl p)
    {
        if (!AmHost || !p || p.Data == null || !p.Data.IsDead) return;
        ReviveCall.Send(p.PlayerId);
        OnRevive(p.PlayerId);
    }

    private static readonly RemoteCall<byte> ReviveCall = new("PortKit.Revive", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => OnRevive(id));

    private static void OnRevive(byte id)
    {
        var p = ById(id);
        if (!p || p.Data == null) return;
        Vector2? at = null;
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
        {
            if (!b || b.ParentId != id) continue;
            at = b.TruePosition;
            Object.Destroy(b.gameObject);
        }
        try { p.Revive(); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"PortKit.Revive: {e.Message}"); }
        if (p.AmOwner && at is Vector2 to) Net.Teleport.Self(to);
    }

    // ---- 姿を変える ----

    // 自分の端末から: 自分を into の姿にする (null で元に戻す)。全員の端末で本編の変身 (Shapeshift) を手元で起こす
    public static void Morph(PlayerControl into)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        byte id = into ? into.PlayerId : (byte)255;
        MorphCall.Send(id);
        ApplyMorph(lp, id);
    }

    private static readonly RemoteCall<byte> MorphCall = new("PortKit.Morph", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(), (sender, id) => ApplyMorph(sender, id));

    private static void ApplyMorph(PlayerControl who, byte intoId)
    {
        if (!who || who.Data == null) return;
        var into = intoId == 255 ? who : ById(intoId);
        if (!into) return;
        try { who.Shapeshift(into, true); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"PortKit.Morph: {e.Message}"); }
    }

    // ---- 見た目 ----

    // その人の体・帽子・服・ペット・バイザーの濃さを全員の端末の手元で変える (0 = 見えない・1 = 普通)。ほぼ見えない時は名前も隠す
    public static void SetOpacity(PlayerControl p, float opacity)
    {
        if (!p || !p.cosmetics) return;
        var color = Color.Lerp(Palette.ClearWhite, Palette.White, opacity);
        try
        {
            var c = p.cosmetics;
            if (c.currentBodySprite != null && c.currentBodySprite.BodySprite) c.currentBodySprite.BodySprite.color = color;
            if (c.skin && c.skin.layer) c.skin.layer.color = color;
            if (c.hat) c.hat.SpriteColor = color;
            var pet = c.GetPet();
            if (pet) pet.ForEachRenderer(true, (Il2CppSystem.Action<SpriteRenderer>)(r => r.color = color));
            if (c.visor) c.visor.Image.color = color;
            if (c.colorBlindText) c.colorBlindText.color = opacity < 0.1f ? Palette.ClearWhite : Palette.White;
            if (c.nameText) c.nameText.enabled = opacity >= 0.1f;
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"PortKit.SetOpacity: {e.Message}"); }
    }

    // ---- 近くの物 ----

    // その場所から range 以内でいちばん近い、まだ通報されていない死体 (無ければ null)
    public static DeadBody NearestBody(Vector2 from, float range)
    {
        DeadBody best = null;
        float bestD = range;
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
        {
            if (!b || b.Reported) continue;
            float d = Vector2.Distance(from, b.TruePosition);
            if (d <= bestD) { best = b; bestD = d; }
        }
        return best;
    }

    // その場所から range 以内でいちばん近いベント (無ければ null)
    public static Vent NearestVent(Vector2 from, float range)
    {
        var ship = ShipStatus.Instance;
        if (!ship || ship.AllVents == null) return null;
        Vent best = null;
        float bestD = range;
        foreach (var v in ship.AllVents)
        {
            if (!v) continue;
            float d = Vector2.Distance(from, v.transform.position);
            if (d <= bestD) { best = v; bestD = d; }
        }
        return best;
    }

    // ---- 少し後で ----

    private static readonly System.Collections.Generic.List<(float left, System.Action act)> Later = new();
    private static object _laterMatch; // Later を空にする後始末を足した試合 (1 試合に 1 回だけ足す)

    // 自分の端末で seconds 秒後に呼ぶ。試合が終わると呼ばずに捨てる
    public static void After(float seconds, System.Action act)
    {
        if (!ReferenceEquals(_laterMatch, RoleState.Match))
        {
            _laterMatch = RoleState.Match;
            RoleState.Match.OnRelease(() => { Later.Clear(); _laterMatch = null; });
        }
        Later.Add((seconds, act));
    }

    // 毎 FixedUpdate (RoleFrame.Tick から)
    internal static void TickLater()
    {
        if (Later.Count == 0) return;
        float dt = Time.fixedDeltaTime;
        for (int i = Later.Count - 1; i >= 0; i--)
        {
            var (left, act) = Later[i];
            left -= dt;
            if (left > 0f) { Later[i] = (left, act); continue; }
            Later.RemoveAt(i);
            try { act(); }
            catch (System.Exception e) { Plugin.Logger.LogError($"PortKit.After: {e}"); }
        }
    }

    public static bool Alive(PlayerControl p) => p && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
}

// 自分では動けない人 (ロケットランチャーに掴まれた・リモコンで操作されている/している・ユビキタスでドローンを操作している)。
// CanMove は 1 フレームに何度も読まれるので、パッチは 1 つにまとめ、誰も止まっていない時は Unity の物に触らずに返す
[HarmonyLib.HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CanMove), HarmonyLib.MethodType.Getter)]
internal static class PortFreezePatch
{
    public static void Postfix(PlayerControl __instance, ref bool __result)
    {
        if (!__result) return;
        var local = RoleState.Local;
        bool held = Impostor.RocketLauncher.Held.Count > 0, controlled = Impostor.RemoteController.Controlled.Count > 0;
        if (!held && !controlled && local is not Impostor.RemoteController && local is not Crew.Ubiquitous) return;
        if (!__instance.AmOwner) return;
        byte id = __instance.PlayerId;
        if ((held && Impostor.RocketLauncher.Held.Contains(id))
            || (controlled && Impostor.RemoteController.Controlled.Contains(id))
            || (local is Impostor.RemoteController && Impostor.RemoteController.Operating(__instance))
            || (local is Crew.Ubiquitous && Crew.Ubiquitous.Operating(__instance)))
            __result = false;
    }
}
