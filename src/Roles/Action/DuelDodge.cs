using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 立ち合いの相手の手 = 回避。立ち合いの間だけ、サムライでない側が短く飛び退ける (PC = 回避のキー (既定 Shift・Space は本編の「使う」と重なる)・スマホ = 右の縁のボタン)。
// 飛び退く向きは歩いている向き (止まっていれば相手から離れる向き)。立ち合いごとに数回まで。
// ホストは回避を受け取ってから少しの間、その人への斬りを当てない (無敵)。飛び退いた跡には全員の画面で残像が残る
internal static class DuelDodge
{
    internal const int PerDuel = 3;
    internal const float Speed = 13f;
    private const float MoveTime = 0.18f;
    private const float Safe = 0.35f;       // ホストが受け取ってから当てない秒
    private const float TrailTime = 0.26f;  // 残像を置く秒
    private const byte None = 255;

    // 自分の端末
    private static byte _opp = None;
    private static int _left;
    private static float _moveUntil = -10f;
    private static bool _moveOn;
    private static Vector2 _dir;
    private static bool _usable;
    private static int _shownLeft = -1;
    private static KeyCode _shownKey;
    internal static int Used, Refused;
    internal static float TestSafe; // 確かめ用: 無敵の秒を伸ばす (0 = 既定)
    // ホスト
    private static readonly byte[] HostLeft = new byte[256];
    private static readonly float[] SafeUntil = new float[256];
    // 残像を置いている人 (全員の端末)
    private static readonly byte[] TrailId = new byte[8];
    private static readonly float[] TrailUntil = new float[8];
    private static readonly float[] TrailLast = new float[8];
    private static int _trails;
    private static bool _hooked;

    internal static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Duel.OnStart += OnStart;
        Duel.OnEnd += OnEnd;
    }

    private static byte LocalId => PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : None;

    // 回避を使えるのはサムライでない側 (サムライは踏み込みを持つ)
    private static bool CanHave(byte id) => RoleState.Of(id) is not Neutral.Samurai;

    private static void OnStart(byte a, byte t)
    {
        HostLeft[a] = HostLeft[t] = PerDuel;
        SafeUntil[a] = SafeUntil[t] = 0f;
        byte me = LocalId;
        if (me != a && me != t || !CanHave(me)) return;
        _opp = me == a ? t : a;
        _left = PerDuel;
        _shownLeft = -1;
    }

    private static void OnEnd(byte a, byte t)
    {
        byte me = LocalId;
        if (me != a && me != t) return;
        _opp = None;
        _usable = false;
        DodgePad.Hide();
    }

    // ホスト: この人は今、斬りを避けているか
    internal static bool HostSafe(byte id) => Time.time < SafeUntil[id];

    // 毎フレーム。立ち合い中でも残像も無ければ比較だけで帰る
    internal static void Tick()
    {
        if (_trails > 0) TickTrails();
        if (_opp == None) return;
        var p = PlayerControl.LocalPlayer;
        if (!p || p.Data == null || p.Data.IsDead || !Duel.InDuel(p.PlayerId))
        {
            _opp = None;
            DodgePad.Hide();
            return;
        }
        // 動けるか (毎フレームの読みを減らすため 4 フレームに 1 回)
        if (Time.frameCount % 4 == 0) _usable = p.CanMove && !p.inVent && !MeetingHud.Instance;
        bool pad = DodgePad.Tick(_usable && _left > 0);
        Hint();
        if (!_usable || _left <= 0) return;
        bool press = pad && DodgePad.Pressed();
        if (!press && !System.OperatingSystem.IsAndroid() && Menu.Keybinds.Down(Menu.Keybinds.Slot.Dodge))
        {
            var hud = Vanilla.Hud;
            press = !(hud && hud.Chat && hud.Chat.IsOpenOrOpening) && !Dev.DevConsole.IsOpen;
        }
        if (press) Dodge(p);
    }

    private static void Hint()
    {
        var key = Menu.Keybinds.Key(Menu.Keybinds.Slot.Dodge);
        if (_left == _shownLeft && key == _shownKey) return;
        string line = new Text("Duel.DodgeHint").With("n", _left).ToString();
        if (!System.OperatingSystem.IsAndroid() && key != KeyCode.None) line = $"[{(key is KeyCode.LeftShift or KeyCode.RightShift ? "Shift" : key.ToString())}] {line}";
        // HUD がまだ出ていなければ次のフレームに書き直す
        if (!UI.DuelHud.SetHint(line)) return;
        _shownLeft = _left;
        _shownKey = key;
    }

    private static void Dodge(PlayerControl p)
    {
        var v = p.MyPhysics ? p.MyPhysics.body.velocity : default;
        float dx = v.x, dy = v.y;
        if (dx * dx + dy * dy < 0.01f)
        {
            // 止まっていれば相手から離れる向き
            var opp = GameData.Instance ? GameData.Instance.GetPlayerById(_opp)?.Object : null;
            Vector3 me = p.transform.position;
            Vector3 o = opp ? opp.transform.position : me;
            dx = me.x - o.x;
            dy = me.y - o.y;
            if (dx * dx + dy * dy < 1e-4f) { dx = p.cosmetics && p.cosmetics.FlipX ? 1f : -1f; dy = 0f; }
        }
        float l = FxMath.Sqrt(dx * dx + dy * dy);
        _dir = FxMath.V2(dx / l, dy / l);
        _moveUntil = Time.time + MoveTime;
        _moveOn = true;
        _left--;
        Used++;
        DodgeCall.Send(0);
        Apply(p.PlayerId);
    }

    // ホスト: 練習の相手 (ダミー) が回避した。使えたら true (動かすのは呼んだ側)
    internal static bool BotDodge(byte id)
    {
        if (!Duel.InDuel(id) || HostLeft[id] == 0 || !CanHave(id)) return false;
        Apply(id);
        BotDodgeCall.Send(id);
        return true;
    }

    internal static float BotDodgeTime => MoveTime;

    private static readonly RemoteCall<byte> BotDodgeCall = new("Duel.BotDodge", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadByte(), (_, id) => StartTrail(id));

    // 回避した (全員の端末で・送った本人とホスト自身は手元で)
    private static readonly RemoteCall<byte> DodgeCall = new("Duel.Dodge", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadByte(), (sender, _) => { if (sender) Apply(sender.PlayerId); }, perSecond: 6);

    private static void Apply(byte id)
    {
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost)
        {
            if (Duel.InDuel(id) && HostLeft[id] > 0 && CanHave(id))
            {
                HostLeft[id]--;
                SafeUntil[id] = Time.time + (TestSafe > 0f ? TestSafe : Safe);
            }
            else Refused++;
        }
        StartTrail(id);
    }

    private static void StartTrail(byte id)
    {
        int slot = -1;
        for (int i = 0; i < _trails; i++)
            if (TrailId[i] == id) slot = i;
        if (slot < 0)
        {
            if (_trails >= TrailId.Length) return;
            slot = _trails++;
        }
        TrailId[slot] = id;
        TrailUntil[slot] = Time.time + TrailTime;
        SamuraiSound.Dodge(GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null);
        TrailLast[slot] = -10f;
    }

    private static void TickTrails()
    {
        float now = Time.time;
        var gd = GameData.Instance;
        for (int i = _trails - 1; i >= 0; i--)
        {
            if (now >= TrailUntil[i] || !gd)
            {
                _trails--;
                TrailId[i] = TrailId[_trails];
                TrailUntil[i] = TrailUntil[_trails];
                TrailLast[i] = TrailLast[_trails];
                continue;
            }
            if (now - TrailLast[i] < 1f / 30f) continue;
            var info = gd.GetPlayerById(TrailId[i]);
            var pc = info != null ? info.Object : null;
            if (!pc || !pc.Visible) continue;
            int color = pc.CurrentOutfit != null ? pc.CurrentOutfit.ColorId : info.DefaultOutfit.ColorId;
            if (SamuraiBlade.GhostOf(pc, color, false, now)) TrailLast[i] = now;
        }
    }

    internal static bool Moving(out Vector2 dir)
    {
        dir = _dir;
        if (!_moveOn) return false;
        if (Time.time < _moveUntil) return true;
        _moveOn = false;
        return false;
    }

    // 確かめ用
    internal static bool ForTest()
    {
        var p = PlayerControl.LocalPlayer;
        if (_opp == None || !p || _left <= 0) return false;
        Dodge(p);
        return true;
    }

    internal static string Describe() =>
        $"opp={_opp} left={_left} usable={_usable} used={Used} refused={Refused} pad={DodgePad.Shown} trails={_trails}";
}

// 回避: 本編の歩行の後で、短い間だけ体の速さを飛び退く向きへ書き換える (壁は物理が止める)
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
internal static class DuelDodgeMovePatch
{
    public static void Postfix(PlayerPhysics __instance)
    {
        if (!DuelDodge.Moving(out var dir) || !__instance.AmOwner) return;
        var pc = __instance.myPlayer;
        if (!pc || !pc.CanMove || pc.inVent) return;
        __instance.body.velocity = FxMath.V2(dir.x * DuelDodge.Speed, dir.y * DuelDodge.Speed);
    }
}

// 回避のスマホのボタン (画面の右の縁)。立ち合いの間だけ出す。確かめ用に PC でも出せる (ForceShow)
internal static class DodgePad
{
    private const float Z = -490f, Size = 1.05f, Edge = 0.78f, Y = 0.5f;

    internal static bool ForceShow;
    internal static bool Shown => _root && _root.activeSelf;

    private static GameObject _root;
    private static Transform _btn;
    private static SpriteRenderer _bg, _icon;
    private static int _painted = -1;
    private static Sprite _chevrons;

    private static bool Wanted => ForceShow || System.OperatingSystem.IsAndroid();

    // 出すか・塗り。出していれば true
    internal static bool Tick(bool usable)
    {
        if (!Wanted) return false;
        if (!Build()) return false;
        if (!_root.activeSelf) _root.SetActive(true);
        int key = usable ? 1 : 0;
        if (key != _painted)
        {
            _painted = key;
            _bg.color = usable ? FxMath.Rgba(0.10f, 0.12f, 0.25f, 0.78f) : FxMath.Rgba(0.08f, 0.08f, 0.12f, 0.45f);
            _icon.color = usable ? FxMath.Rgba(1f, 1f, 1f, 1f) : FxMath.Rgba(1f, 1f, 1f, 0.3f);
        }
        return true;
    }

    // 押した瞬間か (スマホ = 指・PC で出している時 = 左クリック)
    internal static bool Pressed()
    {
        if (System.OperatingSystem.IsAndroid())
        {
            int n = Input.touchCount;
            for (int i = 0; i < n; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began && Hit(t.position)) return true;
            }
            return false;
        }
        if (!Input.GetMouseButtonDown(0)) return false;
        Vector3 m = Input.mousePosition;
        return Hit(FxMath.V2(m.x, m.y));
    }

    private static bool Hit(Vector2 screen)
    {
        var hud = Vanilla.Hud;
        var cam = hud ? hud.UICamera : null;
        if (!cam || !_btn) return false;
        Vector3 w = cam.ScreenToWorldPoint(FxMath.V3(screen.x, screen.y, 0f));
        Vector3 c = _btn.position;
        float dx = w.x - c.x, dy = w.y - c.y, r = Size * 0.5f;
        return dx * dx + dy * dy <= r * r;
    }

    internal static void Hide()
    {
        if (_root && _root.activeSelf) _root.SetActive(false);
    }

    private static bool Build()
    {
        if (_root) return true;
        var hud = Vanilla.Hud;
        var cam = hud ? hud.UICamera : null;
        if (!hud || !cam) return false;
        float halfW = cam.orthographicSize * cam.aspect;
        int layer = hud.gameObject.layer;
        _root = new GameObject("MrpDodgePad") { layer = layer };
        var tf = _root.transform;
        tf.SetParent(hud.transform, false);
        tf.localPosition = FxMath.V3(0f, 0f, Z);
        var go = new GameObject("Dodge") { layer = layer };
        _btn = go.transform;
        _btn.SetParent(tf, false);
        _btn.localPosition = FxMath.V3(halfW - Edge, Y, 0f);
        _btn.localScale = FxMath.V3(Size, Size, 1f);
        _bg = go.AddComponent<SpriteRenderer>();
        _bg.sprite = UI.DuelHud.Circle();
        _chevrons ??= FxSprites.Rect(64, 64, Chevrons, "MrpDodgeIcon");
        var igo = new GameObject("Icon") { layer = layer };
        igo.transform.SetParent(_btn, false);
        igo.transform.localPosition = FxMath.V3(0f, 0f, -0.1f);
        igo.transform.localScale = FxMath.V3(0.62f, 0.62f, 1f);
        _icon = igo.AddComponent<SpriteRenderer>();
        _icon.sprite = _chevrons;
        _painted = -1;
        return true;
    }

    // 2 つ重ねた «  (飛び退く)
    private static float Chevrons(float x, float y)
    {
        float best = 0f;
        for (int k = 0; k < 2; k++)
        {
            float cx = x + 0.12f - 0.24f * k;
            float d = FxMath.Abs(cx - (FxMath.Abs(y) * 0.6f - 0.1f));
            if (FxMath.Abs(y) > 0.3f) continue;
            best = FxMath.Max(best, FxMath.Clamp01((0.06f - d) / 0.025f));
        }
        return best;
    }
}
