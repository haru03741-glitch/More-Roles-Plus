// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Modifiers/Lovers.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// ラバーズ (アドオン・2 人 1 組): 相方が誰か・どの役職か分かり、相方の位置の矢印が出る。相方が死ぬと後を追って死ぬ (設定)。
// 勝ち方は 3 択 (通常 / 共勝利 / 完全単独)
public sealed class Lovers : AddonBase, PortKit.ILovers
{
    public const string Pink = "#FF66CC";

    public override string Color => Pink;
    public override Text Name => new("Lovers.Name");
    public override string Reading => "らばーず";
    public override Text Blurb => new("Lovers.Blurb");
    public override string Origin => "LCM, SNR";
    public override int GroupSize => 2;

    // 付く陣営 (原作の「抽選にインポスター / 第三陣営を含める」) は既定で含めない。設定画面の付く陣営でそのまま変えられる
    protected internal override bool DefaultOnImpostor => false;
    protected internal override bool DefaultOnNeutral => false;

    public const int Normal = 0, Shared = 1, Single = 2;

    public static readonly BoolOpt FollowDeath = new("Lovers.FollowDeath", true);

    // 勝ち方 (原作の LoversWinType): 通常 = 2 人とも生きたまま残り 3 人以下になれば 2 人だけの勝ち・陣営の勝ちには陣営として加わる
    // 共勝利 = 勝った陣営に相方がいれば、生きている相方も勝つ / 完全単独 = 勝った陣営にラバーズは入れない
    public static readonly ChoiceOpt WinType = new("Lovers.WinType", Normal,
        new Text("Lovers.WinType.Normal"), new Text("Lovers.WinType.Shared"), new Text("Lovers.WinType.Single"));
    public static readonly BoolOpt KnowPartnerRole = new("Lovers.KnowPartnerRole", true);
    public static readonly BoolOpt KnowPartnerPosition = new("Lovers.KnowPartnerPosition", true);

    private byte Partner => AddonGroups.PartnerOf(PlayerId);

    public override bool AffectsWin => true;
    internal override bool NeedsMrpEnd => true;

    // 共勝利: 勝った陣営に相方がいれば、生きている自分も勝つ
    public override bool AlsoWins(GameResult result) =>
        WinType.Value == Shared && PortKit.Alive(Player) && result.Winners.Contains(Partner);

    // 完全単独: 2 人だけの勝ちでなければ、ほかの人が勝つ試合からラバーズは外れる
    public override bool LosesWin(GameResult result)
    {
        if (WinType.Value != Single || IsSolo(result)) return false;
        foreach (byte id in result.Winners)
            if (!IsLoverId(id)) return true;
        return false;
    }

    private static bool IsLoverId(byte id)
    {
        foreach (var a in RoleState.AddonsOf(id))
            if (a is Lovers) return true;
        return false;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameResult, object> SoloMarks = new();
    private static bool IsSolo(GameResult r) => r != null && SoloMarks.TryGetValue(r, out _);

    // 原作の CanSoloWin: 2 人とも生きていて、生き残りが 3 人以下なら 2 人だけの勝ちで試合を終える (GameEnd.Decide から)。
    // どの勝ち方でも起きる。決まらなければ null
    internal static GameResult SoloResult()
    {
        var gd = GameData.Instance;
        if (!gd) return null;
        Lovers first = null;
        foreach (var a in RoleState.AllAddons)
        {
            if (a is not Lovers l || !PortKit.Alive(l.Player)) continue;
            var mate = PortKit.ById(l.Partner);
            if (mate && PortKit.Alive(mate)) { first = l; break; }
        }
        if (first == null) return null;
        var all = gd.AllPlayers;
        int alive = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p != null && !p.Disconnected && !p.IsDead) alive++;
        }
        if (alive > 3) return null;
        var r = new GameResult { Role = RoleState.Of(first.PlayerId) };
        r.Winners.Add(first.PlayerId);
        r.Winners.Add(first.Partner);
        SoloMarks.Add(r, null);
        return r;
    }

    // 原作: 通常・完全単独では、ラバーズが混ざる陣営の人数での勝ち (残りが 2 人以上) は取り消す。
    // インポスターの人数での勝ち / 第三陣営のキル役の陣営 (faction) の人数での勝ちを止めるか
    internal static bool BlocksImpostorCount(int impostors) =>
        impostors > 1 && AliveLoverWhere(p => p.Data != null && p.Data.Role && p.Data.Role.IsImpostor);

    internal static bool BlocksFactionCount(string faction, int members) =>
        members > 1 && AliveLoverWhere(p => RoleState.Of(p.PlayerId)?.Faction == faction);

    private static bool AliveLoverWhere(System.Func<PlayerControl, bool> inTeam)
    {
        if (WinType.Value == Shared) return false;
        foreach (var a in RoleState.AllAddons)
        {
            if (a is not Lovers l || !PortKit.Alive(l.Player)) continue;
            try { if (inTeam(l.Player)) return true; }
            catch (System.Exception) { }
        }
        return false;
    }

    private WorldArrow _arrow;

    // 相方の位置の矢印 (自分の端末だけ)。自分が生きている間は相方が生きていれば出し、死んだ後は相方の居場所を出し続ける
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        var mate = PortKit.ById(Partner);
        bool on = KnowPartnerPosition && mate && !MeetingHud.Instance && (!PortKit.Alive(Player) || PortKit.Alive(mate));
        if (!on)
        {
            _arrow?.SetActive(false);
            return;
        }
        _arrow ??= new WorldArrow(Lifespan, RoleDisplay.ParseColor(Pink));
        _arrow.SetActive(true);
        _arrow.Update(mate.transform.position);
    }

    public override string NameMark(PlayerControl seen)
    {
        if (seen.PlayerId == PlayerId) return $"<color={Pink}>♥</color>";
        if (seen.PlayerId != Partner) return null;
        var role = KnowPartnerRole ? RoleState.Of(seen) : null;
        return $"<color={Pink}>♥</color>" + (role != null ? $" <size=70%>{role.ColoredName}</size>" : "");
    }

    // 相方が倒された / 追放された: ホストが後を追わせる
    [HostOnly]
    private void OnMurdered(PlayerMurderedEvent e) => Follow(e.Target, false);

    [HostOnly]
    private void OnExiled(PlayerExiledEvent e) => Follow(e.Player, true);

    private void Follow(PlayerControl dead, bool exiled)
    {
        if (!FollowDeath || !dead || dead.PlayerId != Partner || !PortKit.Alive(Player)) return;
        // 倒された処理の途中で次の死を起こさないよう、少し後で
        PortKit.After(0.05f, () =>
        {
            if (!PortKit.Alive(Player)) return;
            if (exiled || MeetingHud.Instance || ExileController.Instance) PortKit.HostVanish(Player, Player, PortKit.Harm.Fated);
            else PortKit.HostSuicide(Player);
        });
    }
}
