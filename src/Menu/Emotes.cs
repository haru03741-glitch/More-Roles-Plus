using System;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Roles;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 試合中のエモート (手を振る・跳ねる・拍手・手招き)。右下の「エモート」ボタンで 4 つのボタンを扇形に開いて選ぶ。
// PC は B キーで開閉・開いている間は 1〜4 キーでも選べる。生きている人だけが出せ、全員の画面に出る。
// 受け手は送り手が生きているか・間隔・種類の範囲を自分で確かめる (改造した客が連打しても画面を埋めない)
internal static class Emotes
{
    private const float Cooldown = 1.5f;     // 出した人のボタンの待ち時間
    private const long MinGapMs = 1200;      // 受け手が同じ人から受け付ける間隔 (待ち時間より短く・届く時刻の揺れの分)
    private const float RingRadius = 2.7f;   // 扇の半径 (ボタンの並びの単位)
    private const float RingScale = 0.8f;
    private static readonly float[] RingAngles = { 95f, 122f, 149f, 176f }; // 度 (右下のボタンから左上へ広げる)
    private static readonly string[] Names = { "Emote.Wave", "Emote.Jump", "Emote.Clap", "Emote.Beckon" };

    private static readonly RemoteCall<byte> PlayCall = new("Emote.Play", Route.Anyone,
        (w, kind) => w.Write(kind), r => r.ReadByte(), Receive, Delivery.Queued);

    private static readonly long[] LastMs = new long[256];
    private static Lifespan _life;
    private static ModButton _main;
    private static GameObject _ring;
    private static bool _retry, _iconDim;
    private static SpriteRenderer _mainIcon;
    private static int _frame;
    internal static int Dropped;

    private static bool Alive(PlayerControl p) => p && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

    private static bool InTasks => ShipStatus.Instance && !MeetingHud.Instance && !ExileController.Instance;

    private static void Receive(PlayerControl sender, byte kind)
    {
        long now = Environment.TickCount64;
        if (!sender || kind >= EmoteArt.Count || !Alive(sender) || !InTasks || now - LastMs[sender.PlayerId] < MinGapMs)
        {
            Dropped++;
            return;
        }
        LastMs[sender.PlayerId] = now;
        EmoteArt.Play(sender, kind);
    }

    // 自分がエモートを出す (手元に出して全員へ送る)
    internal static bool PlayLocal(byte kind)
    {
        var lp = PlayerControl.LocalPlayer;
        if (kind >= EmoteArt.Count || !Alive(lp) || !InTasks || lp.inVent || !lp.Visible) return false;
        if (!EmoteArt.Play(lp, kind)) return false;
        LastMs[lp.PlayerId] = Environment.TickCount64;
        PlayCall.Send(kind);
        return true;
    }

    // ── ボタンと扇 ───────────────────────────────────────────────

    // HUD が出た時に作る (試合中だけ・もう出ていれば何もしない)
    internal static void EnsureButtons()
    {
        // 船があれば試合中 (ロビーには船が無い・フリープレイは試合の状態が「開始」にならない)
        // 生きているかは自分で見る (HUD の出入りを数える ModButton の後置とこの後置の順は決まっていない)
        if (_life is { IsDead: false } || !Alive(PlayerControl.LocalPlayer) || !ShipStatus.Instance) return;
        _life = RoleState.Match.Child();
        _main = ModButton.Create(_life, null, new Text("Emote.Button"), Cooldown, () =>
        {
            SetRing(!RingOpen);
            return false;
        });
        if (_main == null || !_main.Transform)
        {
            _life.Release();
            _life = null;
            return;
        }
        _mainIcon = HandIcon(_main, EmoteArt.Wave);
        _iconDim = false;
        _ring = new GameObject("MrpEmoteRing");
        _ring.transform.SetParent(_main.Transform, false);
        _ring.layer = _main.Transform.gameObject.layer;
        _ring.SetActive(false);
        for (byte k = 0; k < EmoteArt.Count; k++)
        {
            byte kind = k;
            var b = ModButton.Create(_life, null, new Text(Names[kind]), 0f, () => Choose(kind), _ring.transform);
            if (b == null || !b.Transform) continue;
            // 扇のボタンは扇 (_ring) の出し入れで隠すので、自分は出したままにする
            b.Transform.gameObject.SetActive(true);
            HandIcon(b, kind);
            float a = RingAngles[kind] * FxMath.Deg2Rad;
            b.Transform.localPosition = new Vector3(FxMath.Cos(a) * RingRadius, FxMath.Sin(a) * RingRadius, -0.5f);
            b.Transform.localScale = new Vector3(RingScale, RingScale, 1f);
        }
        _life.OnRelease(() =>
        {
            _main = null;
            _ring = null;
            _mainIcon = null;
        });
    }

    // アイコン = 本人の色に塗った手 (本編のボタンの絵の材質では色のひな形が塗られないので、隠して手の絵を重ねる)
    private static SpriteRenderer HandIcon(ModButton b, byte kind)
    {
        var g = b.Graphic;
        var mat = FxHands.PlayerMat();
        var sprite = EmoteArt.Icon(kind);
        var lp = PlayerControl.LocalPlayer;
        if (!g || !mat || !sprite || !lp || lp.Data == null) return null;
        g.enabled = false;
        var go = new GameObject("MrpEmoteIcon") { layer = g.gameObject.layer };
        go.transform.SetParent(g.transform, false);
        go.transform.localPosition = new Vector3(0f, 0f, -0.01f);
        go.transform.localRotation = FxMath.RotZ(EmoteArt.IconRotation(kind));
        go.transform.localScale = new Vector3(0.85f, 0.85f, 1f);
        // 本編の手の絵は原点が絵の外にあるので、中心をボタンの中心へ寄せる
        var art = new GameObject("Art") { layer = go.layer };
        art.transform.SetParent(go.transform, false);
        Vector2 c = EmoteArt.IconCenter(kind);
        art.transform.localPosition = new Vector3(c.x, c.y, 0f);
        var sr = art.AddComponent<SpriteRenderer>();
        sr.sharedMaterial = mat;
        sr.sprite = sprite;
        sr.sortingLayerID = g.sortingLayerID;
        sr.sortingOrder = g.sortingOrder + 1;
        PlayerMaterial.SetColors(lp.Data.DefaultOutfit.ColorId, sr);
        return sr;
    }

    private static bool RingOpen => _ring && _ring.activeSelf;

    private static void SetRing(bool open)
    {
        if (!_ring) return;
        if (open && _main != null && _main.CoolingDown) return;
        _ring.SetActive(open);
    }

    private static bool Choose(byte kind)
    {
        SetRing(false);
        if (_main != null && _main.CoolingDown) return false;
        if (PlayLocal(kind)) _main?.StartCooldown();
        return false;
    }

    // 毎フレーム: PC のキー (B で開閉・開いている間は 1〜4)
    internal static void Tick()
    {
        if (_main == null)
        {
            // HUD が出た時に船がまだ無かった試合は、ここで 0.5 秒ごとに作り直す
            if (_retry && ++_frame % 30 == 0) EnsureButtons();
            return;
        }
        // 待ち時間の間はアイコンを暗く (本編のボタンの暗くする処理は隠した本編の絵にしか掛からない)
        if (_main.CoolingDown != _iconDim && _mainIcon)
        {
            _iconDim = _main.CoolingDown;
            _mainIcon.color = _iconDim ? new Color(0.45f, 0.45f, 0.45f, 1f) : Color.white;
        }
        if (!ModButton.HudShown) return;
        if (Input.GetKeyDown(KeyCode.B))
        {
            var chat = Vanilla.Chat;
            if (!(chat && chat.IsOpenOrOpening)) SetRing(!RingOpen);
            return;
        }
        if (!RingOpen) return;
        for (byte k = 0; k < EmoteArt.Count; k++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + k))
            {
                Choose(k);
                return;
            }
    }

    internal static void OnHudShown(bool shown)
    {
        _retry = shown;
        if (shown) EnsureButtons();
        else SetRing(false);
    }

    // 確認用: dev `emote`
    internal static string Command(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string head = parts.Length > 0 ? parts[0] : "stats";
        switch (head)
        {
            case "ring":
                EnsureButtons();
                SetRing(!RingOpen);
                break;
            case "pick" when parts.Length > 1 && byte.TryParse(parts[1], out byte pk):
                // 扇のボタンを本編の押下の道で押す (開いていなければ開いてから)
                if (!RingOpen) SetRing(true);
                return $"{(_ring && pk < _ring.transform.childCount ? Press(pk) : "no ring")} {Stats()}";
            case "stats":
                break;
            default:
                if (!byte.TryParse(head, out byte kind)) return "ERR emote [ring | pick <0-3> | stats | <0-3> [番号]]";
                if (parts.Length > 1 && byte.TryParse(parts[1], out byte pid))
                {
                    // 他人 (ダミー) が送ってきたのと同じ受け手の道を通す
                    var p = GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;
                    if (!p) return $"ERR no player {pid}";
                    Receive(p, kind);
                }
                else if (!PlayLocal(kind)) return $"ERR emote refused {Stats()}";
                break;
        }
        return $"OK emote {Stats()}";
    }

    private static string Press(byte i)
    {
        var b = _ring.transform.GetChild(i).GetComponent<PassiveButton>();
        if (!b) return "no button";
        b.OnClick.Invoke();
        return $"pressed={i}";
    }

    private static string Stats() =>
        $"{EmoteArt.Describe()} dropped={Dropped} sent={PlayCall.Sent} recv={PlayCall.Received} main={(_main != null ? _main.Describe() : "none")} ring={(RingOpen ? "open" : "closed")}";
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.SetHudActive), typeof(PlayerControl), typeof(RoleBehaviour), typeof(bool))]
internal static class EmoteHudPatch
{
    public static void Postfix(bool isActive) => Emotes.OnHudShown(isActive);
}

// 会議が始まったら出ているエモートを全部止める (跳ねた体を元の高さへ)
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class EmoteMeetingPatch
{
    public static void Postfix() => EmoteArt.ClearAll();
}
