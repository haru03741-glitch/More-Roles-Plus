// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Spectrum.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Linq;
using System.Reflection;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// スペクトラム (インポスター): マッドメイトにできるクルーの役職を 1 つ無作為に持つインポスター (名前もその役職の名前)。
// その役職の能力と、自前のキルボタンを使う。本編のキルボタンは無い
public sealed class Spectrum : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => _inner != null ? _inner.Name : new Text("Spectrum.Name");
    public override string Reading => "すぺくとらむ";
    public override Text Blurb => _inner != null ? new Text("Spectrum.BlurbWith").With("role", _inner.Name.ToString()) : new Text("Spectrum.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Spectrum.Kill", "spectrum_kill") };

    public static readonly FloatOpt KillCooldown = new("Spectrum.KillCooldown", 25f, 2.5f, 60f, 2.5f, "s");

    private RoleBase _inner;
    internal RoleBase Inner => _inner;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Spectrum.Kill"), "spectrum_kill")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => KillCooldown,
            CanTarget = (_, t) => !PortKit.IsImpostor(t),
            OnUsed = (user, t) => { if (AmongUsClient.Instance.AmHost) UchuKit.HostAbilityKill(user, t); },
        });
        if (!AmongUsClient.Instance.AmHost) return;
        var pool = Registry.Roles.Where(r => r.CanBeMadmate && r.BaseTeam == Team.Crew && !r.MadeByOthers).ToList();
        if (pool.Count == 0) return;
        var pick = pool[UnityEngine.Random.Range(0, pool.Count)];
        int idx = Registry.Roles.IndexOf(pick);
        byte me = PlayerId;
        // 客の端末にこの役職が届いてから送る
        PortKit.After(0.5f, () =>
        {
            if (RoleState.Of(me) is not Spectrum s || s._inner != null) return;
            InnerCall.Send((me, idx));
            s.SetInner(idx);
        });
    }

    private static readonly RemoteCall<(byte id, int role)> InnerCall = new("Spectrum.Inner", Route.HostToAll,
        (w, m) => { w.Write(m.id); w.WritePacked(m.role); }, r => (r.ReadByte(), r.ReadPackedInt32()),
        (_, m) => { if (RoleState.Of(m.id) is Spectrum s) s.SetInner(m.role); });

    private void SetInner(int idx)
    {
        if (_inner != null || idx < 0 || idx >= Registry.Roles.Count) return;
        var proto = Registry.Roles[idx];
        var inner = (RoleBase)System.Activator.CreateInstance(proto.GetType());
        inner.Player = Player;
        inner.PlayerId = PlayerId;
        inner.Chance = proto.Chance;
        inner.Count = proto.Count;
        inner.Lifespan = Lifespan;
        _inner = inner;
        EventBinder.Bind(inner);
        try { inner.OnAssigned(); }
        catch (System.Exception e) { Plugin.Logger.LogError($"Spectrum inner {proto.Id}.OnAssigned: {e}"); }
        // 中の役職の能力を自分の能力として並べる (ホストの確かめは自分の能力の番号で引く)
        foreach (var a in inner.Abilities) AddAbility(a);
        inner.Abilities.Clear();
        _innerFrameLocalOnly = proto.GetType().GetMethod(nameof(OnFixedUpdate))?.GetCustomAttribute<HostOnlyAttribute>() == null;
        if (IsLocal)
        {
            MoreRolesPlus.Roles.Abilities.EnsureButtons();
            RoleDisplay.OnAssigned(this);
        }
    }

    private bool _innerFrameLocalOnly = true;

    public override void OnFixedUpdate()
    {
        if (IsLocal) UchuKit.HideKillButton();
        if (_inner == null) return;
        if (_innerFrameLocalOnly ? IsLocal : AmongUsClient.Instance.AmHost) _inner.OnFixedUpdate();
    }

    public override string NameMark(PlayerControl seen) => _inner?.NameMark(seen);

    public override void ModifyVoteWeight(ref int weight) => _inner?.ModifyVoteWeight(ref weight);

    // ホスト: 本編のキルボタンでのキルは通さない
    [HostOnly]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (e.Canceled || !e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        if (UchuKit.AbilityKill != PlayerId) e.Cancel("spectrum has no normal kill");
    }
}
