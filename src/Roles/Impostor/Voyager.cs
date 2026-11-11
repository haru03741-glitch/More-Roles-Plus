// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Voyager.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ボイジャー (インポスター): 「潜伏」でインポスター以外から見えなくなり、速く動ける。潜伏の間は画面が紫になり、
// 他の生きている人の見た目が何組か入れ替わって見える (自分の画面だけ)。仲間もキルできる。
// 「離脱」で戻る。決めた時間の内に離脱しないと死ぬ (パラドックス)。会議で解ける
public sealed class Voyager : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Voyager.Name");
    public override string Reading => "ぼいじゃー";
    public override Text Blurb => new("Voyager.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Voyager.Dive", "voyager_dive"), new("Voyager.AllyKill", "voyager_allykill") };

    public static readonly FloatOpt DiveCooldown = new("Voyager.DiveCooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt DiveDuration = new("Voyager.DiveDuration", 15f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt DiveSpeed = new("Voyager.DiveSpeed", 1.5f, 1f, 3f, 0.25f, "x");
    public static readonly IntOpt SwapPairs = new("Voyager.SwapPairs", 4, 0, 7);

    private bool _diving;   // 全員の端末で同じ
    private bool _left;     // 自分の端末: 離脱で終えた
    // 入れ替えた人と、入れ替える前の姿 (本編の変身・カモフラージュ中ならその姿に戻す)
    private readonly List<(PlayerControl P, PlayerOutfitType Type, NetworkedPlayerInfo.PlayerOutfit Was)> _swapped = new();

    public override void OnAssigned()
    {
        UchuKit.Hidden.Set(PlayerId, () => _diving);
        // 潜伏の間も体はあるので水と粉に跡が残る。その代わり水の流れと外壁の穴の吸い出しに押されない
        CrewTraits.Set(PlayerId, CrewTraits.Trait.Trace, () => _diving);
        CrewTraits.Set(PlayerId, CrewTraits.Trait.Steady, () => _diving);
        AddAbility(new Ability(new Text("Voyager.Dive"), "voyager_dive")
        {
            Cooldown = () => DiveCooldown,
            Duration = () => DiveDuration,
            Cancellable = true,
            CancelLabel = new Text("Voyager.Leave"),
            OnUse = () =>
            {
                _left = false;
                SetDive(true);
                DiveCall.Send(true);
                MoveSpeed.Add("VoyagerDive", DiveSpeed, DiveDuration);
                SwapLooks();
                return true;
            },
            OnCancel = () => _left = true,
            OnEnd = () =>
            {
                bool paradox = !_left && !MeetingHud.Instance && PortKit.Alive(Player);
                EndDive();
                if (paradox)
                {
                    PortKit.Notice(new Text("Voyager.Paradox").ToString());
                    PortKit.Suicide();
                }
            },
        });
        // 潜伏の間だけ、仲間のインポスターも倒せる (本編のキルボタンは仲間を狙えないので別のボタン)
        AddAbility(new Ability(new Text("Voyager.AllyKill"), "voyager_allykill")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => GameManager.Instance.LogicOptions.GetKillCooldown(),
            CooldownAtStart = false,
            CanTarget = (_, t) => _diving && PortKit.IsImpostor(t),
            OnUsed = (user, t) =>
            {
                // 倒された仲間の画面は、潜伏の側の紫の暗がりに一気に閉じる (何に倒されたかを見え方で伝える)
                if (t && t.AmOwner) SenseFx.Pulled();
                if (AmongUsClient.Instance.AmHost) UchuKit.HostAbilityKill(user, t, PortKit.Harm.Slash);
            },
        });
    }

    // 消える所と現れる所は周りにも見えるので、音もその場所から (人に結ぶと透明の間は鳴らない)
    private void SetDive(bool on)
    {
        if (_diving == on) return;
        _diving = on;
        if (!Player || MeetingHud.Instance) return;
        var at = Player.GetTruePosition();
        MoreRolesPlus.Fx.Hearing.At(on ? "voyager_dive" : "voyager_surface", at.x, at.y, 8f, 0.65f, sightOnly: true);
    }

    private static readonly RemoteCall<bool> DiveCall = new("Voyager.Dive", Route.Anyone,
        (w, on) => w.Write(on), r => r.ReadBoolean(),
        (sender, on) => { if (RoleState.Of(sender) is Voyager v) v.SetDive(on); }, perSecond: 4);

    private void EndDive()
    {
        if (!_diving) return;
        SetDive(false);
        DiveCall.Send(false);
        MoveSpeed.Remove("VoyagerDive");
        RestoreLooks();
        Tint(false);
    }

    // 自分の画面だけ: 他の生きている人の見た目を何組か入れ替える
    private void SwapLooks()
    {
        Tint(true);
        var pool = new List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls)
            if (PortKit.Alive(p) && !p.AmOwner && p.Data != null) pool.Add(p);
        for (int i = pool.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); (pool[i], pool[j]) = (pool[j], pool[i]); }
        int pairs = Mathf.Min(SwapPairs, pool.Count / 2);
        for (int k = 0; k < pairs; k++)
        {
            var a = pool[2 * k];
            var b = pool[2 * k + 1];
            // 今見えている姿どうしを入れ替える
            var ta = a.CurrentOutfitType;
            var tb = b.CurrentOutfitType;
            var oa = a.CurrentOutfit ?? a.Data.DefaultOutfit;
            var ob = b.CurrentOutfit ?? b.Data.DefaultOutfit;
            try
            {
                _swapped.Add((a, ta, oa));
                _swapped.Add((b, tb, ob));
                a.RawSetOutfit(ob, PlayerOutfitType.Shapeshifted);
                b.RawSetOutfit(oa, PlayerOutfitType.Shapeshifted);
            }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"voyager swap: {e.Message}"); }
        }
    }

    private void RestoreLooks()
    {
        foreach (var (p, type, was) in _swapped)
        {
            if (!p || p.Data == null) continue;
            try
            {
                // 潜伏の間に別の物 (変身の解除など) が姿を戻した人はそのまま
                if (p.CurrentOutfitType != PlayerOutfitType.Shapeshifted) continue;
                if (type == PlayerOutfitType.Default || was == null) p.RawSetOutfit(p.Data.DefaultOutfit, PlayerOutfitType.Default);
                else p.RawSetOutfit(was, type);
            }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"voyager restore: {e.Message}"); }
        }
        _swapped.Clear();
    }

    // 潜伏の間の紫の画面
    private static SpriteRenderer _tint;

    private static void Tint(bool on)
    {
        var hud = Vanilla.Hud;
        if (on)
        {
            if (!_tint && hud && hud.FullScreen)
            {
                _tint = Object.Instantiate(hud.FullScreen, hud.FullScreen.transform.parent);
                _tint.name = "MrpVoyagerTint";
            }
            if (!_tint) return;
            _tint.color = new Color(0.55f, 0.1f, 0.85f, 0.22f);
            _tint.gameObject.SetActive(true);
        }
        else if (_tint) _tint.gameObject.SetActive(false);
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        _diving = false;
        if (IsLocal) { RestoreLooks(); Tint(false); MoveSpeed.Remove("VoyagerDive"); }
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (_diving && !PortKit.Alive(Player)) EndDive();
    }
}
