using System;
using System.Collections.Generic;
using MoreRolesPlus.Net;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 幽霊の観戦: 死んだ人 (観戦の人も) が、他の人の視点へカメラを移して見られる。
// - 右下に「次の人」(生きている人を番号順に回り、最後に自分へ戻る) と「戦いを見る」(立ち合いの組を順に) のボタン。キーは能力 1・2 と同じ
// - 立ち合いが新しく始まると、誰も見ていない・戦っていない人を見ている時は自動でその戦いへ寄る (MRP の設定で切れる)
// - 見ている人が戦っていれば立ち合いの HUD をその人の側で出す。部屋の名前と音の聞こえ方も見ている人の位置で
// カメラは本編の FollowerCamera の追う相手を差し替えるだけ (自分の幽霊はその場に残る)。この端末だけで、電文は送らない
internal static class Watch
{
    private const byte Self = 255;
    private const int CheckMs = 250;
    private const int AfterDeathMs = 1200; // 見ている人が倒れた後、倒した相手へ移るまで

    private static byte _target = Self;
    private static PlayerControl _targetPc; // 見ている人 (毎フレーム呼ばれる Ear で一覧を探さない)
    private static byte _lastHitBy = Self;  // 見ている人に最後に当てた人 (倒れた時に移る先)
    private static long _diedAt;
    private static long _nextCheck;
    private static bool _active;             // 幽霊として観戦できる場面か (最後に見た時)
    private static Lifespan _life;
    private static ModButton _next, _duel;
    private static TextMeshPro _label;
    private static bool _hooked;
    internal static int Switches, AutoJumps;

    // 見ている人 (自分を見ている時は 255)
    internal static byte Target => _target;

    // 視点の持ち主 (見ている人・いなければ自分)
    internal static byte ViewId
    {
        get
        {
            if (_target != Self) return _target;
            var lp = PlayerControl.LocalPlayer;
            return lp ? lp.PlayerId : Self;
        }
    }

    // 音の聞こえる位置 (見ている人の足元・いなければ自分)
    internal static Vector2 Ear(PlayerControl lp)
    {
        return _target != Self && _targetPc ? _targetPc.GetTruePosition() : lp.GetTruePosition();
    }

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Duel.OnJoin += OnDuelJoin;
        Duel.OnHit += OnDuelHit;
    }

    // 毎フレーム (キーを読むため)。重い確かめは 0.25 秒ごと
    internal static void Tick()
    {
        long now = Environment.TickCount64;
        if (now >= _nextCheck)
        {
            _nextCheck = now + CheckMs;
            Check(now);
        }
        if (!_active || !ModButton.GhostShown) return;
        // 上に画面が出ているか (チャット・作業など) は押された時だけ見る
        var b = Menu.Keybinds.Down(Menu.Keybinds.Slot.Ability1) ? _next : Menu.Keybinds.Down(Menu.Keybinds.Slot.Ability2) ? _duel : null;
        if (b != null && !Menu.Emotes.Covered()) b.Press(KeyCode.None);
    }

    private static bool CanWatch()
    {
        var lp = PlayerControl.LocalPlayer;
        return lp && lp.Data != null && lp.Data.IsDead && ShipStatus.Instance && Vanilla.Hud
            && !MeetingHud.Instance && !ExileController.Instance && GameEnd.Last == null;
    }

    private static void Check(long now)
    {
        bool can = CanWatch();
        if (!can)
        {
            if (_active) Stop();
            return;
        }
        bool began = !_active;
        _active = true;
        Hook();
        EnsureButtons();
        // 幽霊になった時に戦いが続いていれば、そこへ寄る (新しく始まった戦いは OnDuelJoin で)
        if (began && _target == Self && Duel.Any && Menu.ClientOptions.WatchDuels.Value)
        {
            byte t = NextFighter();
            if (t != Self) SetTarget(t, auto: true);
        }
        if (_target == Self)
        {
            _duel?.SetUsable(Duel.Any);
            return;
        }
        var p = Player(_target);
        if (!p || p.Data == null || p.Data.Disconnected)
        {
            SetTarget(Self);
        }
        else if (p.Data.IsDead)
        {
            // 倒れた: 少し見せてから倒した相手 (まだ生きていれば) へ・いなければ次の人へ
            if (_diedAt == 0) _diedAt = now;
            else if (now - _diedAt >= AfterDeathMs)
            {
                var killer = Player(_lastHitBy);
                if (killer && killer.Data != null && !killer.Data.IsDead) SetTarget(_lastHitBy);
                else SetTarget(NextAlive(_target));
            }
        }
        _duel?.SetUsable(Duel.Any);
        WriteLabel();
    }

    private static void EnsureButtons()
    {
        if (_life is { IsDead: false } && _next != null && _next.Transform) return;
        _life?.Release();
        _life = RoleState.Match.Child();
        var icon = HauntIcon();
        _next = ModButton.Create(_life, icon, new Text("Watch.Next"), 0f, () => { SetTarget(NextAlive(_target)); return false; }, ghost: true);
        _duel = ModButton.Create(_life, icon, new Text("Watch.Duel"), 0f, () => { var t = NextFighter(); if (t != Self) SetTarget(t); return false; }, ghost: true);
        _duel?.SetUsable(Duel.Any);
    }

    private static Sprite _haunt;

    // 本編の幽霊の憑依のボタンの絵 (読み込まれていなければ絵なし)
    private static Sprite HauntIcon()
    {
        if (_haunt) return _haunt;
        var role = RoleManager.Instance ? RoleManager.Instance.GetRole(AmongUs.GameOptions.RoleTypes.CrewmateGhost) : null;
        var ability = role ? role.Ability : null;
        _haunt = ability ? ability.Image : null;
        return _haunt;
    }

    private static void Stop()
    {
        _active = false;
        SetTarget(Self);
        _life?.Release();
        _life = null;
        _next = _duel = null;
        if (_label) UnityEngine.Object.Destroy(_label.gameObject);
        _label = null;
        _labelText = null;
    }

    internal static void SetTarget(byte id, bool auto = false)
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp && id == lp.PlayerId) id = Self;
        _diedAt = 0;
        if (id == _target) return;
        _target = id;
        _lastHitBy = Self;
        if (auto) AutoJumps++;
        else Switches++;
        var hud = Vanilla.Hud;
        _targetPc = Player(id);
        var follow = _targetPc ?? lp;
        if (hud && hud.PlayerCam && follow)
        {
            hud.PlayerCam.Target = follow;
            hud.PlayerCam.SnapToTarget();
        }
        ShowVanillaRoom(hud, id == Self);
        UI.DuelHud.OnViewChanged();
        _labelText = null;
        WriteLabel();
    }

    // 本編の部屋の名前は自分の幽霊の居場所を追う (判定が自分の人に決め打ち)。見ている間は隠し、見ている人の部屋をラベルに出す
    // 隠す前に本編が出していたか (本編が隠していた物を戻す時に出さない)
    private static bool _roomWasOn, _roomHidden;

    private static void ShowVanillaRoom(HudManager hud, bool on)
    {
        var rt = hud ? hud.roomTracker : null;
        if (!rt) { _roomHidden = false; return; }
        var go = rt.gameObject;
        if (!on)
        {
            if (_roomHidden) return;
            _roomWasOn = go.activeSelf;
            _roomHidden = true;
            if (_roomWasOn) go.SetActive(false);
            return;
        }
        if (!_roomHidden) return;
        _roomHidden = false;
        if (_roomWasOn && !go.activeSelf) go.SetActive(true);
    }

    private static string _labelText;

    // 本編の部屋の名前の所に「観戦中: 名前」と、見ている人のいる部屋 (本編の部屋の名前の字を写す)。字が変わった時だけ書く
    private static void WriteLabel()
    {
        var p = _targetPc;
        if (_target == Self || !p || p.Data == null)
        {
            if (_label) _label.gameObject.SetActive(false);
            return;
        }
        var hud = Vanilla.Hud;
        var rt = hud ? hud.roomTracker : null;
        if (!_label)
        {
            var src = rt ? rt.text : null;
            if (!src) return;
            // 部屋の名前の字は RoomTracker と同じ GameObject にある。写しから外さないと写しも自分の部屋を追う
            _label = UnityEngine.Object.Instantiate(src, src.transform.parent);
            _label.name = "MrpWatchLabel";
            var copy = _label.GetComponent<RoomTracker>();
            if (copy) UnityEngine.Object.DestroyImmediate(copy);
            var tr = _label.GetComponent<TextTranslatorTMP>();
            if (tr) UnityEngine.Object.DestroyImmediate(tr);
            // 2 行にすると自動の大きさで縮むので、本編の 1 行の大きさに止める
            _label.enableAutoSizing = false;
            _label.fontSize = src.fontSize;
            var at = src.transform.localPosition;
            _label.transform.localPosition = new Vector3(at.x, rt.SourceY, at.z); // SourceY = 部屋の名前が出ている時の高さ
        }
        string room = rt ? rt.GetRoomForPlayer(p.Data) : null;
        string who = new Text("Watch.Watching").With("name", p.Data.PlayerName);
        string text = string.IsNullOrEmpty(room) ? who : $"<size=70%>{who}</size>\n{room}";
        if (text != _labelText)
        {
            _labelText = text;
            _label.text = text;
        }
        if (!_label.gameObject.activeSelf) _label.gameObject.SetActive(true);
    }

    // ---- 次に見る人 ----

    private static readonly List<byte> Ids = new();

    // 生きている人を番号順に回り、最後の人の次は自分
    private static byte NextAlive(byte from)
    {
        var lp = PlayerControl.LocalPlayer;
        Ids.Clear();
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p && p != lp && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected) Ids.Add(p.PlayerId);
        Ids.Sort();
        foreach (byte id in Ids)
            if (from == Self || id > from) return id;
        return Self;
    }

    // 今見ている組の次の組 (組の中ではサムライを先に)
    private static byte NextFighter()
    {
        int cur = _target != Self ? Duel.GroupOf(_target) : 0;
        // cur の次の組から 1 周 (最後に cur 自身 = 組が 1 つだけなら見続ける)
        for (int i = 1; i <= Duel.MaxGroups; i++)
        {
            byte g = (byte)((cur + i - 1) % Duel.MaxGroups + 1);
            if (Duel.CountOf(g) > 0) return g == cur ? _target : Lead(g);
        }
        return Self;
    }

    private static byte Lead(byte g)
    {
        int n = Duel.CountOf(g);
        for (int i = 0; i < n; i++)
        {
            byte id = Duel.MemberOf(g, i);
            if (RoleState.Of(id) is Neutral.Samurai) return id;
        }
        return n > 0 ? Duel.MemberOf(g, 0) : Self;
    }

    // ---- 立ち合い ----

    private static void OnDuelJoin(byte id, byte g, bool fresh)
    {
        if (!fresh || !_active || !Menu.ClientOptions.WatchDuels.Value) return;
        // 戦いを見ている間は奪わない
        if (_target != Self && Duel.InDuel(_target)) return;
        var lp = PlayerControl.LocalPlayer;
        if (lp && id == lp.PlayerId) return;
        SetTarget(Lead(g), auto: true);
    }

    private static void OnDuelHit(byte a, byte t, int hp)
    {
        if (t == _target && a != t) _lastHitBy = a;
    }

    private static PlayerControl Player(byte id)
    {
        if (id == Self) return null;
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p && p.PlayerId == id) return p;
        return null;
    }

    private static string RoomDump()
    {
        var hud = Vanilla.Hud;
        var rt = hud ? hud.roomTracker : null;
        return rt && rt.text ? $"y={rt.text.transform.localPosition.y:0.00} src={rt.SourceY:0.00} tgt={rt.TargetY:0.00} pc={(rt.playerCollider ? rt.playerCollider.gameObject.name : "null")}" : "-";
    }

    internal static ModButton NextButton => _next;
    internal static ModButton DuelButton => _duel;

    internal static string Describe()
    {
        var lp = PlayerControl.LocalPlayer;
        string why = !lp || lp.Data == null ? "nolocal" : !lp.Data.IsDead ? "alive" : !ShipStatus.Instance ? "noship" : !Vanilla.Hud ? "nohud"
            : MeetingHud.Instance ? "meeting" : ExileController.Instance ? "exile" : GameEnd.Last != null ? "ended" : "ok";
        return $"OK watch can={why} active={_active} target={_target} view={ViewId} switches={Switches} auto={AutoJumps} next={(_next != null)} duelBtn={(_duel != null)} label={(_label ? _label.text : "-")} labelY={(_label ? _label.transform.localPosition.y : 0f):0.00} room={RoomDump()}";
    }
}
