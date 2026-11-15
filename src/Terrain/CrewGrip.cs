using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 船外への吸い出しで流される自分の体: 流れの先の角にタイミングよくつかまり、連打で中へ戻る。
// - つかむ: 流れの強さが歩く速さの半分以上の所で、流れの先 Reach 以内・横 Lateral 以内の角に縮む輪を出す。角の真横に来た (Window 以内) 時に押すとつかまる
//   (Perfect 以内ならゲージ 80% から・それ以外は 60%)。早押しは Lockout の間押せない。
// - 連打: ゲージはその場の引く強さに比例して減る (口の際の満タンで 25%/秒)・1 押し +10%。100% で中へ 1.8 戻り Immune の間は流されない。
//   0% で手が離れて流される (次の角でもう一度つかめる)。口がふさがる・気圧が抜ければ引く力が消えて助かる。
// - 口に触れたら (つかまっていない時) 吸い出し (エアシップは空へ落ちる): 本人の端末が決めてホストへ 1 通 → ホストが確かめて全員へ配り、全員が同じ飛び方を見せてから
//   本編の追放と同じ死に方 (体を残さない) にする。追放の出来事 (道化の勝ち等) は起こさない。
// - 口の際: 口に触れた時、口の端に残った壁が近ければ最後にもう一度だけ輪を出す (つかめなければ吸い出し)。
//   風が強いので窓は短く、つかんでもゲージは低い所から速く減り 1 押しの伸びも小さい。手が離れたらそのまま吸い出し。
// 押すのは PC = スペース/クリック・スマホ = 画面の右側のタップ (左の移動の指は数えない)。入力は毎フレーム、体の速さは物理の刻みで
internal static class CrewGrip
{
    private const float DangerMul = 0.5f;   // 引く強さ (歩く速さの倍) がこれ以上の所だけ危ない
    private const float Reach = 1.5f;       // 角に輪を出す距離 (体の縁から)
    private const float Window = 0.4f;      // 押せばつかまる距離
    private const float Perfect = 0.12f;
    private const float Lateral = 0.9f;     // 流れの向きに対して横にこの距離までの角に手が届く
    private const float Lockout = 0.4f;
    private const float HoldGap = 0.45f;    // つかんでいる間の体の中心と角の距離
    private const float StartGauge = 0.6f, PerfectGauge = 0.8f;
    private const float Drain = 0.25f;      // 口の際の満タン (引く強さ 1.6) での毎秒の減り
    private const float EdgeMul = 1.6f;
    private const float Mash = 0.10f;
    private const float PushDist = 1.8f, PushTime = 0.3f, Immune = 1.5f;
    // 口の際: 足元から口の線まで OutReach 以内で、流れの道のりも OutDist 以内 (体が止まる所は喉の形で 0.3〜0.9 と違う)
    private const float OutReach = 1.0f;
    private const int OutDist = Decompression.UnitDist * 3 / 4;
    private const float HostReach = 2.0f;   // ホストが確かめる距離 (位置の同期の遅れの分の余裕)
    private const float LostTimeout = 3f;   // ホストが断った時に動けるように戻すまで
    private const float FlyTime = 1.1f;
    // 口の際の最後のつかみ: 体から RimReach 以内の口の端。輪は RimStart から RimSpeed で縮み、角を Window 過ぎたら吸い出し
    private const float RimReach = 1.8f, RimStart = 1.2f, RimSpeed = 3.2f, RimCreep = 0.6f;
    private const float RimGauge = 0.45f, RimPerfectGauge = 0.6f, RimDrain = 0.45f, RimMash = 0.08f;

    private enum State : byte { Free, Grabbed, Pushing, Lost, Rim, Shoved, Cling }
    // 突き飛ばされた (突き落と師): 口への道を ShoveSpeed で運ばれ、流れが ShoveMul 以上か口の際まで来たら手を離す (そこからは普通の流れ・つかめる)
    private const float ShoveSpeed = 7f, ShoveMax = 1.2f, ShoveMul = 0.8f, ShoveRim = 1.3f;
    private const float ShoveMemory = ShoveMax + 4f; // ホスト: 突き飛ばした人を死因の相手にする間 (秒)
    private static float _shoveEnd;
    private static readonly Dictionary<byte, (byte By, float Until)> ShovedBy = new(); // ホストだけ

    private static State _state;
    private static float _t;                // 物理の刻みで進む自分の時計
    private static float _dt;
    private static float _lockEnd, _pushEnd, _immuneEnd, _lostAt, _rimEnd;
    private static bool _rim;               // 口の際でつかんでいる (厳しい方の数値)
    private static bool _clingGrip;         // 自分が縁の角を握ってしがみついている (角に手の印とゲージを出す)
    private static int _presses;            // 毎フレーム数えて物理の刻みで使う
    private static bool _hasTarget;
    private static Vector2 _target, _hold, _dir, _pushDir;
    private static float _de, _gauge, _mul;
    private static int _shipGen = -1;
    private static float _auto = -1f;       // 確認用: 輪がこの距離に入ったら押し、つかまったら毎秒 AutoRate 回押す (負 = 切)
    private static float _autoNext;
    private const float AutoRate = 5f;

    // 見た目 (自分だけ)
    private static GameObject _root;
    private static SpriteRenderer _ringGlow, _ring, _dot, _hand, _barBg, _bar;
    private static Transform _ringTf, _ringGlowTf, _dotTf, _handTf, _barBgTf, _barTf;
    private static TextMeshPro _prompt;
    private static SpriteRenderer _tapBtn;
    private static int _promptKind = -1;
    // 押すキーの札 (HUD の上に置く。カメラは自分を追うので HUD の中心の少し上が頭の上になる)
    private static Transform _keyTf;
    private static SpriteRenderer _keyRim, _keyFill;
    private static TextMeshPro _keyText, _tapText;
    private static int _keyLook = -1;
    private static float _popAt = -1f;      // 最後に押した時刻 (_anim)
    private static float _keyScale = -1f;   // 最後に書いた札の大きさ (同じなら書かない)
    private static Transform _tapTf;
    private static bool _shown, _ringOn, _dotOn, _grabOn, _afterRewind;
    private static Pose _bend;
    private static float _anim;
    private static Sprite _ringSprite, _dotSprite, _whiteSprite;

    // 体の見た目 (体の絵 BodyForms と帽子・服の Cosmetics は同じ原点の兄弟)。動かすのは見た目だけで位置の同期は触らない。
    // 名前は回すと体から外れるので、飛ぶ間は隠す
    private sealed class Pose
    {
        private readonly Transform _body, _cos, _names;
        private bool _nameHidden;
        private readonly Vector3 _bp, _bs, _cp, _cs;
        private readonly Quaternion _br, _cr;
        private readonly float _inv;   // ワールドの長さ → 体の根の中の長さ

        public Pose(PlayerControl pc)
        {
            var root = pc.transform;
            Roles.SamuraiBlade.ReleaseBody(pc.PlayerId);
            _body = root.Find("BodyForms");
            _cos = pc.cosmetics ? pc.cosmetics.transform : null;
            _names = root.Find("Names");
            float s = root.localScale.x;
            _inv = s > 0.01f ? 1f / s : 1f;
            if (_body) { _bp = _body.localPosition; _br = _body.localRotation; _bs = _body.localScale; }
            if (_cos) { _cp = _cos.localPosition; _cr = _cos.localRotation; _cs = _cos.localScale; }
        }

        public bool Alive => _body || _cos;

        public void Set(float wx, float wy, float deg, float sx, float sy)
        {
            var rot = FxMath.RotZ(deg);
            float ox = wx * _inv, oy = wy * _inv;
            if (_body)
            {
                _body.localPosition = FxMath.V3(_bp.x + ox, _bp.y + oy, _bp.z);
                _body.localRotation = rot;
                _body.localScale = FxMath.V3(_bs.x * sx, _bs.y * sy, _bs.z);
            }
            if (_cos)
            {
                _cos.localPosition = FxMath.V3(_cp.x + ox, _cp.y + oy, _cp.z);
                _cos.localRotation = rot;
                _cos.localScale = FxMath.V3(_cs.x * sx, _cs.y * sy, _cs.z);
            }
        }

        public void HideName()
        {
            if (!_names || !_names.gameObject.activeSelf) return;
            _names.gameObject.SetActive(false);
            _nameHidden = true;
        }

        public void Restore()
        {
            if (_body) { _body.localPosition = _bp; _body.localRotation = _br; _body.localScale = _bs; }
            if (_cos) { _cos.localPosition = _cp; _cos.localRotation = _cr; _cos.localScale = _cs; }
            if (_nameHidden && _names) _names.gameObject.SetActive(true);
            _nameHidden = false;
        }
    }

    // 吸い出されて飛んでいく人 (全員の端末で同じように)
    private sealed class Flight
    {
        public PlayerControl Pc;
        public Pose Pose;
        public float Mx, My, Dx, Dy;   // 体から見た口と、口の先の向き
        public float T;
        public byte By;                // 穴を開けた人 (死因の相手)
    }
    private static readonly List<Flight> Flights = new();

    // 口まで引き込まれ、縁にしがみついている人 (全員の端末で)。瀕死の秒が切れると手が離れて吸い出される。
    // その間に近くの人か守り霊が助け出せば中へ戻る。口がふさがる・会議になれば助かる
    private sealed class Cling
    {
        public PlayerControl Pc;
        public byte Id, By;
        public float At;
        public float HeldAt;   // 引き戻されている間 (0 = いない)。その間は手が離れるまでの時計が止まる
        public bool Has;       // 握っている角がある
        public Vector2 Grip, Out; // 握っている角 (口の端に残った壁) と、引かれる向き (口の先)
        public Vector2 Axis;      // 体を伸ばす向き (角から口の線を越えて外へ)
        public Pose Pose;      // 体を角から口の先へ向けて倒し、震わせる (全員の端末で)
    }
    private static readonly List<Cling> Clings = new();
    private const float SaveImmune = 3f;   // 助け出された人がこの秒は流されない

    internal static bool AnyClinging => Clings.Count > 0;

    internal static bool Clinging(byte id)
    {
        foreach (var c in Clings) if (c.Id == id) return true;
        return false;
    }
    // 全員: 縁から引き戻されている間は手が離れるまでの時計を止め、放されたら止まっていた分だけ後ろへずらす
    internal static void Hold(byte id, bool on)
    {
        foreach (var c in Clings)
        {
            if (c.Id != id) continue;
            if (on) { if (c.HeldAt <= 0f) c.HeldAt = Time.time; Unlean(c); if (c.Pc && c.Pc.AmOwner) _clingGrip = false; }
            else if (c.HeldAt > 0f) { c.At += Time.time - c.HeldAt; c.HeldAt = 0f; }
        }
    }

    // 全員: 引き戻す手を放された人は、また縁にしがみつく
    internal static void Recling(byte id)
    {
        foreach (var c in Clings)
            if (c.Id == id && c.Pc)
            {
                if (c.Pc.AmOwner) _clingGrip = c.Has;
                Lean(c);
                return;
            }
    }

    // 吸い出しで本編の追放を走らせている間の人 (追放の出来事を起こさない)
    internal static int Spacing = -1;
    internal static int SpacedBy = ResolvedDamage.NoActor; // その人を吸い出した穴を開けた人

    internal static bool Active => _state != State.Free || _shown || Flights.Count > 0;
    internal static string StateName => _state.ToString();

    private static readonly bool Touch = OperatingSystem.IsAndroid();

    // ── 毎フレーム (入力と見た目) ───────────────────────────────────────

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[CrewGrip] tick: {e}");
            Cancel();
        }
    }

    private static void TickCore()
    {
        if (GameClock.ShipGen != _shipGen)
        {
            _shipGen = GameClock.ShipGen;
            // 人の体は船より長生きする (ロビーへ戻っても残る) ので、動かしていた見た目を戻してから捨てる
            foreach (var f in Flights) if (f.Pose.Alive) f.Pose.Restore();
            Flights.Clear();
            foreach (var c in Clings) Unlean(c);
            Clings.Clear();
            RestoreBody();
            _root = null;
            _prompt = null;
            _tapBtn = null;
            _keyTf = _tapTf = null;
            _keyRim = _keyFill = null;
            _keyText = _tapText = null;
            _popAt = -1f;
            _state = State.Free;
            _shown = false;
            _strandTried = false;
            ShovedBy.Clear();
        }
        if (Roles.RewindPlayback.Active)
        {
            // 逆再生の間はつかまりをやめ、流されている体の見た目も戻す (体の位置は逆再生が置き、吸い出しの計算は時戻しが巻き戻す)
            foreach (var f in Flights) if (f.Pose.Alive) f.Pose.Restore();
            Flights.Clear();
            foreach (var c in Clings) Unlean(c);
            Clings.Clear(); // 体の連番は逆再生の間 CrewFlip が止める
            Cancel();
            _afterRewind = true;
            return;
        }
        if (_afterRewind)
        {
            // 控え (TrackWorld) は戻し終わりに入れ物を出し直すので、つかまっていなければ隠し直す
            _afterRewind = false;
            if (!_shown && _root) _root.SetActive(false);
        }
        if (Clings.Count > 0) TickClings();
        if (Flights.Count > 0) TickFlights();
        if (SolidMap.AnySealed && ++_strandFrame >= StrandEvery) { _strandFrame = 0; CheckStranded(); }
        bool danger = _state != State.Free || (Decompression.Pulling && _hasTarget);
        if (!danger)
        {
            if (_shown) Hide();
            return;
        }
        if (Pressed()) { _presses++; _popAt = _anim; }
        Draw();
    }

    private static bool Pressed()
    {
        if (Touch)
        {
            int n = Input.touchCount;
            // 右半分の上 7 割 (左下の移動の指と右下の本編のボタンは数えない)
            float minX = Screen.width * 0.5f, minY = Screen.height * 0.3f;
            for (int i = 0; i < n; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began && t.position.x > minX && t.position.y > minY) return true;
            }
            return false;
        }
        return Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0);
    }

    // ── 物理の刻み (CrewPull から・自分の体だけ) ────────────────────────
    // 返り値 true = こちらで体の速さを決めた (流れは足さない)

    internal static bool Physics(PlayerControl pc, Rigidbody2D body, Vector2 pos, bool on, Vector2 dir, float mul, int dist)
    {
        if (_dt <= 0f) _dt = Time.fixedDeltaTime;
        _t += _dt;
        int presses = _presses;
        _presses = 0;
        _mul = on ? mul : 0f;
        switch (_state)
        {
            case State.Cling:
                body.velocity = _clingGrip ? FxMath.V2((_hold.x - pos.x) * 8f, (_hold.y - pos.y) * 8f) : FxMath.V2(0f, 0f);
                return true;

            case State.Lost:
                body.velocity = FxMath.V2(0f, 0f);
                if (_t - _lostAt > LostTimeout) { Plugin.Logger.LogWarning("[CrewGrip] host did not confirm"); Cancel(); }
                return true;

            case State.Grabbed:
                if (!on || mul < 0.05f)
                {
                    Plugin.Logger.LogInfo("[CrewGrip] pull gone while holding");
                    Let();
                    return false;
                }
                _dir = dir;
                if (_auto >= 0f && _t >= _autoNext) { presses++; _autoNext = _t + 1f / AutoRate; }
                _gauge += _rim ? presses * RimMash - RimDrain * _dt : presses * Mash - Drain * (mul / EdgeMul) * _dt;
                if (_gauge >= 1f)
                {
                    Plugin.Logger.LogInfo("[CrewGrip] climbed back");
                    Let();
                    _state = State.Pushing;
                    _pushDir = FxMath.V2(-dir.x, -dir.y);
                    _pushEnd = _t + PushTime;
                    _immuneEnd = _t + Immune;
                    float ps = PushDist / PushTime;
                    body.velocity = FxMath.V2(_pushDir.x * ps, _pushDir.y * ps);
                    return true;
                }
                if (_gauge <= 0f)
                {
                    Plugin.Logger.LogInfo($"[CrewGrip] lost grip{(_rim ? " at rim" : "")}");
                    bool rim = _rim;
                    Let();
                    if (rim)
                    {
                        Out(pc);
                        body.velocity = FxMath.V2(0f, 0f);
                        return true;
                    }
                    _lockEnd = _t + Lockout;
                    return false;
                }
                // つかんだ所へ戻す (引く力は足さない)
                body.velocity = FxMath.V2((_hold.x - pos.x) * 8f, (_hold.y - pos.y) * 8f);
                return true;

            case State.Pushing:
                if (_t < _pushEnd)
                {
                    float s = PushDist / PushTime;
                    body.velocity = FxMath.V2(_pushDir.x * s, _pushDir.y * s);
                    return true;
                }
                if (_t < _immuneEnd) return true; // 歩きは本編のまま・流れだけ足さない
                _state = State.Free;
                break;

            case State.Rim:
            {
                // 輪の残り (正 = まだ角の手前・負 = 過ぎた)。体は口へ少しずつ寄る
                float left = (_rimEnd - _t) * RimSpeed;
                _de = MathF.Abs(left);
                if (_auto >= 0f && _de <= _auto) presses++;
                if (presses > 0)
                {
                    if (_t >= _lockEnd && _de <= Window)
                    {
                        Grab(pos, true);
                        body.velocity = FxMath.V2(0f, 0f);
                        return true;
                    }
                    if (_t >= _lockEnd) { _lockEnd = _t + Lockout; Plugin.Logger.LogInfo($"[CrewGrip] early press at rim de={_de:0.00}"); }
                }
                if (!on || left < -Window)
                {
                    _state = State.Free;
                    _hasTarget = false;
                    if (!on) return false;
                    Plugin.Logger.LogInfo("[CrewGrip] rim missed");
                    Out(pc);
                    body.velocity = FxMath.V2(0f, 0f);
                    return true;
                }
                body.velocity = FxMath.V2(dir.x * RimCreep, dir.y * RimCreep);
                return true;
            }
        }

        if (_state == State.Shoved)
        {
            bool reached = (on && mul >= ShoveMul) || Decompression.MouthDist(pos, out _) <= ShoveRim;
            if (!reached && _t < _shoveEnd && Decompression.PathAt(pos, out var sd))
            {
                body.velocity = FxMath.V2(sd.x * ShoveSpeed, sd.y * ShoveSpeed);
                return true;
            }
            Plugin.Logger.LogInfo($"[CrewGrip] shove end at {pos.x:0.00},{pos.y:0.00} mul={mul:0.00} reached={reached}");
            _state = State.Free;
            _lockEnd = _t; // 運ばれている間の押しは持ち越さない
        }

        // Free
        // 口に触れたら外へ。エアシップの風は弱い (歩く速さの 0.4 倍) ので、風があれば落ちる
        if (on && mul >= (Decompression.Wind ? 0.01f : DangerMul) && dist <= OutDist && Decompression.MouthDist(pos, out _) <= OutReach)
        {
            // エアシップの縁は風が弱いので最後の輪は出さない
            if (!Decompression.Wind && RimStub(pos, out var stub))
            {
                _state = State.Rim;
                _rimEnd = _t + RimStart / RimSpeed;
                _lockEnd = _t; // 手前の空振りの分は持ち越さない
                _target = stub;
                _hasTarget = true;
                _de = RimStart;
                _dir = dir;
                Plugin.Logger.LogInfo($"[CrewGrip] rim at {stub.x:0.00},{stub.y:0.00} from {pos.x:0.00},{pos.y:0.00}");
                body.velocity = FxMath.V2(0f, 0f);
                return true;
            }
            Out(pc);
            body.velocity = FxMath.V2(0f, 0f);
            return true;
        }
        if (!on || mul < DangerMul)
        {
            _hasTarget = false;
            return false;
        }
        _dir = dir;
        PickTarget(pos, dir);
        if (_auto >= 0f && _hasTarget && _de <= _auto) presses++;
        if (presses > 0 && _t >= _lockEnd)
        {
            if (_hasTarget && _de <= Window) Grab(pos, false);
            else
            {
                _lockEnd = _t + Lockout;
                if (_hasTarget) Plugin.Logger.LogInfo($"[CrewGrip] early press de={_de:0.00}");
            }
        }
        return false;
    }

    // 流れの向きに沿って Reach 先までで、横の離れが Lateral 以内の角のうち、次に真横を通る物。
    // 押し時 = 角の真横 (流れの向きの残りの距離が Window 以内)。通り過ぎて Window を越えたら次の角
    private static void PickTarget(Vector2 p, Vector2 dir)
    {
        _hasTarget = false;
        float best = float.MaxValue;
        var grips = Decompression.GripPoints;
        for (int i = 0; i < grips.Count; i++)
        {
            var g = grips[i];
            float rx = g.x - p.x, ry = g.y - p.y;
            float along = rx * dir.x + ry * dir.y;
            if (along < -Window || along > Reach) continue;
            float side = rx * dir.y - ry * dir.x;
            if (side < -Lateral || side > Lateral) continue;
            if (along < best) { best = along; _target = g; }
        }
        if (best == float.MaxValue) return;
        _hasTarget = true;
        _de = MathF.Abs(best);
    }

    // 口の端に残った壁 (口の線の端のうち、ほかの開いた口の線と繋がっていない物) のうち体にいちばん近い物。
    // 曲がった口の折れ目や隣り合う口の継ぎ目は壁が残っていないので数えない。点は線の外へ少し (壁の中へ) ずらす
    private static bool RimStub(Vector2 p, out Vector2 stub)
    {
        stub = default;
        float best = RimReach * RimReach;
        var list = Decompression.OpenedBreaches;
        for (int i = 0; i < list.Count; i++)
        {
            var br = list[i];
            if (br.Sealed) continue;
            for (int l = 0; l < br.Lines.Count; l++)
            {
                var (a, b) = br.Lines[l];
                for (int e = 0; e < 2; e++)
                {
                    Vector2 q = e == 0 ? a : b, o = e == 0 ? b : a;
                    float d = (q.x - p.x) * (q.x - p.x) + (q.y - p.y) * (q.y - p.y);
                    if (d >= best || Joined(q, br, l)) continue;
                    float ux = q.x - o.x, uy = q.y - o.y, len = MathF.Sqrt(ux * ux + uy * uy);
                    if (len < 0.05f) continue;
                    best = d;
                    stub = FxMath.V2(q.x + ux / len * 0.12f, q.y + uy / len * 0.12f);
                }
            }
        }
        return best < RimReach * RimReach;
    }

    private static bool Joined(Vector2 q, Decompression.Breach self, int line)
    {
        var list = Decompression.OpenedBreaches;
        for (int i = 0; i < list.Count; i++)
        {
            var br = list[i];
            if (br.Sealed) continue;
            for (int l = 0; l < br.Lines.Count; l++)
            {
                if (br == self && l == line) continue;
                var (a, b) = br.Lines[l];
                float abx = b.x - a.x, aby = b.y - a.y, len = abx * abx + aby * aby;
                float t = len > 1e-6f ? Math.Clamp(((q.x - a.x) * abx + (q.y - a.y) * aby) / len, 0f, 1f) : 0f;
                float cx = a.x + abx * t - q.x, cy = a.y + aby * t - q.y;
                if (cx * cx + cy * cy < 0.3f * 0.3f) return true;
            }
        }
        return false;
    }

    private static void Grab(Vector2 pos, bool rim)
    {
        _state = State.Grabbed;
        // つかんだ姿は体の絵を曲げて見せるので、突き飛ばされた連番が残っていれば止める
        var lp = PlayerControl.LocalPlayer;
        if (lp) CrewFlip.Stop(lp.PlayerId);
        _rim = rim;
        // 体を角の手前 (来た側) HoldGap へ寄せる。来た側は歩けた所なので壁の中には入らない
        // 口の端は体の真横にあるので、流れの逆向き (船の中) へ寄せる
        float dx = rim ? -_dir.x : pos.x - _target.x, dy = rim ? -_dir.y : pos.y - _target.y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (rim && len > 1e-4f) { _hold = FxMath.V2(_target.x + dx / len * HoldGap, _target.y + dy / len * HoldGap); }
        else _hold = len > HoldGap ? FxMath.V2(_target.x + dx / len * HoldGap, _target.y + dy / len * HoldGap) : pos;
        _gauge = rim ? (_de <= Perfect ? RimPerfectGauge : RimGauge) : _de <= Perfect ? PerfectGauge : StartGauge;
        Plugin.Logger.LogInfo($"[CrewGrip] grab{(rim ? " rim" : "")} at {_target.x:0.00},{_target.y:0.00} de={_de:0.00} gauge={_gauge:0.00}");
    }

    private static void Let()
    {
        _state = State.Free;
        _hasTarget = false;
        _rim = false;
        RestoreBody();
    }

    internal static void Cancel()
    {
        if (_state == State.Free && !_shown) return;
        _state = State.Free;
        _hasTarget = false;
        _rim = false;
        _presses = 0;
        RestoreBody();
        if (_shown) Hide();
    }

    // 自分が突き飛ばされた (受ける人の端末で)。つかまっていても手が離れる。吸い出されている途中は何もしない
    internal static void Shove()
    {
        if (_state == State.Lost) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || Flying(lp.PlayerId)) return;
        if (_state != State.Free) Let();
        _state = State.Shoved;
        _shoveEnd = _t + ShoveMax;
        _immuneEnd = 0f;
        _presses = 0;
        Plugin.Logger.LogInfo("[CrewGrip] shoved");
    }

    // ホスト: 突き飛ばした人を控える (その後しばらくの吸い出しは、その人を死因の相手にする)
    internal static void NoteShove(byte target, byte by) => ShovedBy[target] = (by, Time.time + ShoveMemory);

    // ── 口の外側に取り残された自分 ─────────────────────────────────────
    // 口の線は切れた壁の線から引くので、掘った穴の中に立っていると口の外側 (船の外にした升) に残ることがある。
    // そこは流れの範囲の外なので引かれず、口の線で船の中へも戻れない。開いた口が近ければ吸い出し、
    // ふさがった後やホストが断った時は船の中のいちばん近い床へ戻す
    private const int StrandEvery = 15;      // 何フレームおきに見るか
    private const float StrandOut = 1.5f;    // 開いた口がこれ以内なら吸い出す (ホストが確かめる HostReach より内側)
    private const float StrandSearch = 3f;   // 戻す床を探す距離
    private const float StrandClear = 0.2f;  // 戻す所で体が壁に掛からない余白
    private static int _strandFrame;
    private static bool _strandTried;        // 取り残されて吸い出しを頼んだ (断られたら床へ戻す)

    private static void CheckStranded()
    {
        if (_state != State.Free) return;
        var pc = PlayerControl.LocalPlayer;
        if (!pc || pc.Data == null || pc.Data.IsDead || pc.inVent || !pc.CanMove) return;
        var pos = pc.GetTruePosition();
        if (!SolidMap.SealedAt(pos.x, pos.y)) { _strandTried = false; return; }
        if (!_strandTried && Decompression.MouthDist(pos, out _) <= StrandOut)
        {
            _strandTried = true;
            Plugin.Logger.LogInfo($"[CrewGrip] stranded behind mouth at {pos.x:0.00},{pos.y:0.00}");
            Out(pc);
            return;
        }
        if (!SolidMap.NearestFloor(pos.x, pos.y, StrandSearch, StrandClear, out var floor))
        {
            Plugin.Logger.LogWarning($"[CrewGrip] stranded at {pos.x:0.00},{pos.y:0.00} with no floor nearby");
            return;
        }
        _strandTried = false;
        // SnapTo は体の根の位置なので、足元 (GetTruePosition) との差を引く
        var root = pc.transform.position;
        var to = FxMath.V2(floor.x - (pos.x - root.x), floor.y - (pos.y - root.y));
        Plugin.Logger.LogInfo($"[CrewGrip] stranded at {pos.x:0.00},{pos.y:0.00} -> back to {floor.x:0.00},{floor.y:0.00}");
        Teleport.Self(to);
    }

    // ── 吸い出し ───────────────────────────────────────────────────────

    private static void Out(PlayerControl pc)
    {
        _state = State.Lost;
        _lostAt = _t;
        _hasTarget = false;
        var pos = pc.GetTruePosition();
        // 最後にしがみつく所 = 口の端に残った壁の角 (縁の輪が出ていた所)。無ければ近くのつかめる角
        bool has = RimStub(pos, out var grip) || NearestGrip(pos, out grip);
        Vector2 dir = Decompression.PullAt(pos, out var d, out _, out _) ? d : _dir;
        Plugin.Logger.LogInfo($"[CrewGrip] out at {pos.x:0.00},{pos.y:0.00} grip={(has ? $"{grip.x:0.00},{grip.y:0.00}" : "-")}");
        OutRequest.Send(new OutMsg(pc.PlayerId, has, grip.x, grip.y, dir.x, dir.y));
    }

    private static bool NearestGrip(Vector2 p, out Vector2 grip)
    {
        grip = default;
        float best = RimReach * RimReach;
        var grips = Decompression.GripPoints;
        for (int i = 0; i < grips.Count; i++)
        {
            var g = grips[i];
            float d = (g.x - p.x) * (g.x - p.x) + (g.y - p.y) * (g.y - p.y);
            if (d < best) { best = d; grip = g; }
        }
        return best < RimReach * RimReach;
    }

    // しがみつく人と、握る角と引かれる向き (角が無ければ Has = false)
    internal readonly record struct OutMsg(byte Pid, bool Has, float Gx, float Gy, float Dx, float Dy);

    private static void WriteOut(Hazel.MessageWriter w, OutMsg m)
    {
        w.Write(m.Pid);
        w.Write(m.Has);
        if (!m.Has) return;
        w.Write(m.Gx); w.Write(m.Gy); w.Write(m.Dx); w.Write(m.Dy);
    }

    private static OutMsg ReadOut(Hazel.MessageReader r)
    {
        byte pid = r.ReadByte();
        return r.ReadBoolean() ? new OutMsg(pid, true, r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) : new OutMsg(pid, false, 0f, 0f, 0f, 0f);
    }

    private static readonly RemoteCall<OutMsg> OutRequest = new("Decomp.Out", Route.ToHost,
        WriteOut, ReadOut,
        (sender, om) =>
        {
            byte pid = om.Pid;
            if (!sender || sender.PlayerId != pid) return;
            string why = Refuse(sender);
            if (why != null)
            {
                Plugin.Logger.LogWarning($"[CrewGrip] out refused {pid}: {why}");
                return;
            }
            byte by = ShovedBy.TryGetValue(pid, out var sh) && Time.time <= sh.Until ? sh.By : Decompression.MouthActor(sender.GetTruePosition());
            ShovedBy.Remove(pid);
            // 握る角は送った人の言い値なので、体から手の届く所かだけ確かめる (遠ければ角なしでしがみつく)
            var sp = sender.GetTruePosition();
            float gdx = om.Gx - sp.x, gdy = om.Gy - sp.y;
            bool finite = float.IsFinite(om.Gx) && float.IsFinite(om.Gy) && float.IsFinite(om.Dx) && float.IsFinite(om.Dy);
            if (om.Has && (!finite || !(gdx * gdx + gdy * gdy <= (RimReach + 0.5f) * (RimReach + 0.5f)))) om = om with { Has = false };
            ClingCall.Send((om, by));
            StartCling(om, by);
        });

    private static readonly RemoteCall<(OutMsg m, byte by)> ClingCall = new("Decomp.Cling", Route.HostToAll,
        (w, c) => { WriteOut(w, c.m); w.Write(c.by); }, r => (ReadOut(r), r.ReadByte()), (_, c) => StartCling(c.m, c.by));

    private static void StartCling(OutMsg om, byte by)
    {
        byte pid = om.Pid;
        if (Interrupted) return;
        var p = GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;
        if (!p || p.Data == null || p.Data.IsDead || Clinging(pid) || Flying(pid)) return;
        var c = new Cling { Pc = p, Id = pid, By = by, At = Time.time, Has = om.Has, Grip = FxMath.V2(om.Gx, om.Gy) };
        float ol = MathF.Sqrt(om.Dx * om.Dx + om.Dy * om.Dy);
        c.Out = ol > 0.01f ? FxMath.V2(om.Dx / ol, om.Dy / ol) : FxMath.V2(0f, 1f);
        c.Axis = c.Out;
        if (c.Has && Decompression.MouthDist(p.GetTruePosition(), out var mouth) < 5f)
        {
            // 体は口の真ん中寄りから外へ吸われる: 角 → (体に近い口の点 + 口の先へ少し)
            float ax = mouth.x + c.Out.x * 0.8f - c.Grip.x, ay = mouth.y + c.Out.y * 0.8f - c.Grip.y, al = MathF.Sqrt(ax * ax + ay * ay);
            if (al > 0.05f) c.Axis = FxMath.V2(ax / al, ay / al);
        }
        Clings.Add(c);
        if (p.AmOwner)
        {
            RestoreBody();
            if (_shown) Hide();
            _state = State.Cling;
            _clingGrip = c.Has;
            _target = c.Grip;
            _dir = c.Out;
            // 握った角の手前 (流れの逆向き) へ寄る。縁の掴みと同じく速さで寄せるので壁で止まる
            _hold = FxMath.V2(c.Grip.x - c.Out.x * HoldGap, c.Grip.y - c.Out.y * HoldGap);
        }
        Lean(c);
        Plugin.Logger.LogInfo($"[CrewGrip] cling {pid} by={by} grip={(c.Has ? $"{c.Grip.x:0.00},{c.Grip.y:0.00}" : "-")}");
    }

    // 縁の角を両手で握り、体は口の先へ引っぱられて倒れる (這う絵の手の側を角へ・背を口の先へ)。角が無ければ倒さない
    private static void Lean(Cling c)
    {
        CrewFlip.Stop(c.Id);
        // 体の控えは連番より先に作る (控えを作る時に他の演出の体を戻させ、その中で連番も止まる)。
        // 這う絵を流せない時 (体を別の演出が動かしている) は立ち姿を倒さない
        if (c.Has && c.Pose == null) c.Pose = new Pose(c.Pc);
        if (!CrewFlip.Play(c.Pc, c.Has ? GripClip : "cling")) Unlean(c);
    }

    // 角を両手で握って体が流れの先へ引っぱられる絵 (拳 = 絵の足元・体は絵の右へ伸びる)
    private const string GripClip = "clinggrip";

    private static void Unlean(Cling c)
    {
        if (c.Pose != null && c.Pose.Alive) c.Pose.Restore();
        c.Pose = null;
    }

    // 体を倒す向きと震え (毎フレーム・つかまっている人だけ)。体 (絵の右) を axis へ伸ばし、
    // 拳 (絵の足元・体の根から HandDrop 下) が角に来るよう体ごとずらす
    private const float HandDrop = 0.38f, LeanReach = 0.8f;
    // flip = 本編が体を左右反転して描いている (寄る・押される時の動きで本編が決める)。横の倍率を負にして打ち消す
    private static void Aim(Pose pose, Vector2 root, Vector2 grip, Vector2 o, float t, float k, bool flip)
    {
        float shake = (4f + 4f * k) * FxMath.Sin(t * 78f) * (0.6f + 0.4f * FxMath.Sin(t * 11f));
        float deg = FxMath.Atan2(o.y, o.x) * 57.29578f + shake;
        float rad = deg * 0.0174533f, cs = FxMath.Cos(rad), sn = FxMath.Sin(rad);
        // 絵の下 (0, -HandDrop) を回した所
        float hx = HandDrop * sn, hy = -HandDrop * cs;
        float tug = 0.03f * FxMath.Sin(t * 23f);
        float wx = grip.x - (root.x + hx) + o.x * tug, wy = grip.y - (root.y + hy) + o.y * tug;
        float wl = MathF.Sqrt(wx * wx + wy * wy);
        if (wl > LeanReach) { wx *= LeanReach / wl; wy *= LeanReach / wl; }
        float st = 1.1f + 0.05f * k + 0.03f * FxMath.Sin(t * 31f);
        pose.Set(wx, wy, deg, flip ? -st : st, 2f - st);
    }

    // 全員: しがみついている人を見る。手を離すか (ホストが決める)・会議で助かるか
    private static void TickClings()
    {
        bool host = AmongUsClient.Instance && AmongUsClient.Instance.AmHost;
        float dying = Roles.TerrainDeath.DyingTime;
        for (int i = Clings.Count - 1; i >= 0; i--)
        {
            var c = Clings[i];
            if (!c.Pc || c.Pc.Data == null || c.Pc.Data.IsDead || c.Pc.Data.Disconnected) { Unlean(c); Clings.RemoveAt(i); CrewFlip.Stop(c.Id); continue; }
            if (Interrupted) { Release(i, c); continue; }
            if (c.HeldAt > 0f) continue; // 引き戻されている間は手が離れない
            // 握る絵を別の演出が止めた (体を返させた・ベント・見えない所から戻った): 立ち絵を倒し続けない
            if (c.Pose != null && !CrewFlip.IsPlaying(c.Id)) Unlean(c);
            if (c.Pose != null)
            {
                // 手が離れる時が近いほど強く震える
                float k = FxMath.Clamp01((Time.time - c.At) / FxMath.Max(1f, dying));
                var rt = c.Pc.transform.position;
                Aim(c.Pose, FxMath.V2(rt.x, rt.y), c.Grip, c.Axis, Time.time + c.Id * 1.7f, k, c.Pc.cosmetics && c.Pc.cosmetics.FlipX);
            }
            // ホストの知らせ (手が離れた・助かった) が届かなかった時の安全弁
            if (!host) { if (Time.time - c.At > dying + 5f) Release(i, c); continue; }
            // 口がふさがった・気圧が抜けた: 引く力が消えたので助かる
            if (!Decompression.Pulling) { Roles.Rescue.HostSave(c.Pc, c.Pc, Roles.Rescue.ByLiving); continue; }
            if (Time.time - c.At < dying) continue;
            Unlean(c);
            Clings.RemoveAt(i);
            Spaced.Send((c.Id, c.By));
            StartFlight(c.Id, c.By);
        }
    }

    private static void Release(int i, Cling c)
    {
        Unlean(c);
        Clings.RemoveAt(i);
        CrewFlip.Stop(c.Id);
        if (c.Pc && c.Pc.AmOwner && _state == State.Cling) _state = State.Free;
    }

    // 全員 (ホストが認めた助け出しの知らせで): 縁から中へ引き戻され、少しの間は流されない
    internal static void OnRescued(byte id)
    {
        for (int i = Clings.Count - 1; i >= 0; i--)
        {
            var c = Clings[i];
            if (c.Id != id) continue;
            Release(i, c);
            if (!c.Pc || !c.Pc.AmOwner) continue;
            var pos = c.Pc.GetTruePosition();
            float md = Decompression.MouthDist(pos, out var m);
            float dx = pos.x - m.x, dy = pos.y - m.y, len = MathF.Sqrt(dx * dx + dy * dy);
            _state = State.Pushing;
            _pushDir = md < 5f && len > 0.05f ? FxMath.V2(dx / len, dy / len) : FxMath.V2(0f, 0f);
            _pushEnd = _t + PushTime;
            _immuneEnd = _t + SaveImmune;
        }
    }

    // 死因の相手 (穴を開けた人・剥がした人・突き飛ばした人) はホストが決めて配る (端末ごとに割れない)
    private static readonly RemoteCall<(byte pid, byte by)> Spaced = new("Decomp.Spaced", Route.HostToAll,
        (w, m) => { w.Write(m.pid); w.Write(m.by); }, r => (r.ReadByte(), r.ReadByte()), (_, m) => StartFlight(m.pid, m.by));

    private static string Refuse(PlayerControl p)
    {
        if (AmongUsClient.Instance.IsGameOver || !ShipStatus.Instance || MeetingHud.Instance || ExileController.Instance) return "not now";
        if (p.Data == null || p.Data.IsDead || p.Data.Disconnected) return "dead";
        if (p.inVent || p.inMovingPlat) return "busy";
        if (!Decompression.Running) return "no breach";
        foreach (var f in Flights) if (f.Pc && f.Pc.PlayerId == p.PlayerId) return "already";
        float d = Decompression.MouthDist(p.GetTruePosition(), out _);
        if (d > HostReach) return $"far {d:0.00}";
        return null;
    }

    private static bool Interrupted => AmongUsClient.Instance.IsGameOver || MeetingHud.Instance || ExileController.Instance;

    internal static bool Flying(byte pid)
    {
        foreach (var f in Flights) if (f.Pc && f.Pc.PlayerId == pid) return true;
        return false;
    }

    // 時戻し: 飛んでいる途中の人は死なせずに見た目を戻し、自分のつかまりも離す
    internal static void Rewound()
    {
        foreach (var f in Flights) if (f.Pose.Alive) f.Pose.Restore();
        Flights.Clear();
        foreach (var c in Clings) { Unlean(c); CrewFlip.Stop(c.Id); }
        Clings.Clear();
        _strandTried = false;
        Cancel();
    }

    private static void StartFlight(byte pid, byte by)
    {
        if (Interrupted) return;
        var p = GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;
        if (!p || p.Data == null || p.Data.IsDead) return;
        foreach (var f in Flights) if (f.Pc && f.Pc.PlayerId == pid) return;
        for (int i = Clings.Count - 1; i >= 0; i--) if (Clings[i].Id == pid) { Unlean(Clings[i]); Clings.RemoveAt(i); }
        if (p.AmOwner) { RestoreBody(); if (_shown) Hide(); _state = State.Lost; _lostAt = _t; }
        var pos = p.GetTruePosition();
        float md = Decompression.MouthDist(pos, out var m);
        float mx = 0f, my = 0.3f;
        if (md < 5f) { mx = m.x - pos.x; my = m.y - pos.y; }
        float len = MathF.Sqrt(mx * mx + my * my);
        float dx = len > 0.05f ? mx / len : 0f, dy = len > 0.05f ? my / len : 1f;
        if (len <= 0.05f && Decompression.PullAt(pos, out var dir, out _, out _)) { dx = dir.x; dy = dir.y; }
        CrewFlip.Stop(pid);
        Hearing.At("spaced_suck", pos.x, pos.y, 10f, 0.8f);
        var pose = new Pose(p);
        pose.HideName();
        Flights.Add(new Flight { Pc = p, Pose = pose, Mx = mx, My = my, Dx = dx, Dy = dy, By = by });
        Plugin.Logger.LogInfo($"[CrewGrip] spaced {pid} mouth={md:0.00}");
    }

    // 口まで 0.25 秒で吸われ、口の先へ 3 単位・回りながら小さく消える。終わったら追放と同じ死に方
    private static void TickFlights()
    {
        // 飛んでいる途中に会議・試合の終わりが来たら死なせずに戻す (会議の間や終わった後に追放の処理を走らせない)
        if (Interrupted)
        {
            foreach (var f in Flights)
            {
                if (f.Pose.Alive) f.Pose.Restore();
                if (f.Pc && f.Pc.AmOwner) { _state = State.Free; _hasTarget = false; }
            }
            Flights.Clear();
            return;
        }
        float dt = Time.deltaTime;
        for (int i = Flights.Count - 1; i >= 0; i--)
        {
            var f = Flights[i];
            f.T += dt;
            if (!f.Pc || !f.Pose.Alive)
            {
                Flights.RemoveAt(i);
                continue;
            }
            float u = f.T / FlyTime;
            if (u >= 1f)
            {
                Flights.RemoveAt(i);
                Finish(f);
                continue;
            }
            float ox, oy;
            if (u < 0.22f)
            {
                float a = u / 0.22f;
                a *= a;
                ox = f.Mx * a; oy = f.My * a;
            }
            else
            {
                float a = (u - 0.22f) / 0.78f;
                float far = (Decompression.Wind ? 1.2f : 3f) * (1f - (1f - a) * (1f - a));
                ox = f.Mx + f.Dx * far; oy = f.My + f.Dy * far;
            }
            // エアシップは空へ落ちる (回りは少なく、遠ざかるほど小さく)
            float spin = (Decompression.Wind ? 200f : 720f) * u * u;
            float sc = Decompression.Wind ? 1f - 0.9f * u * u : 1f - 0.65f * u;
            f.Pose.Set(ox, oy, spin, sc, sc);
        }
    }

    private static void Finish(Flight f)
    {
        f.Pose.Restore();
        var p = f.Pc;
        if (p.Data == null || p.Data.IsDead) return;
        Spacing = p.PlayerId;
        SpacedBy = f.By;
        try { p.Exiled(); }
        finally { Spacing = -1; SpacedBy = ResolvedDamage.NoActor; }
        if (p.AmOwner) { _state = State.Free; _hasTarget = false; }
    }

    // ── 見た目 (自分だけ) ──────────────────────────────────────────────

    private static void Draw()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !EnsureArt(lp)) return;
        float dt = Time.deltaTime;
        _anim += dt;
        if (!_shown) { _root.SetActive(true); _shown = true; }
        var pos = lp.GetTruePosition();
        bool ring = (_state == State.Free && _hasTarget) || _state == State.Rim;
        bool grab = _state == State.Grabbed;
        bool cling = _state == State.Cling && _clingGrip;
        bool hold = grab || cling;

        // 縮む輪: 角を中心に半径 = 体の縁から角までの距離。角の点に重なった時が押し時
        if (ring != _ringOn) { _ringOn = ring; _ringTf.gameObject.SetActive(ring); _ringGlowTf.gameObject.SetActive(ring); }
        if ((ring || hold) != _dotOn) { _dotOn = ring || hold; _dotTf.gameObject.SetActive(_dotOn); }
        if (ring)
        {
            float r = _de + 0.1f, z = _target.y / 1000f - 0.02f;
            bool inWin = _de <= Window, locked = _t < _lockEnd;
            var col = locked ? FxMath.Rgba(0.55f, 0.55f, 0.6f, 0.6f)
                : _de <= Perfect ? FxMath.Rgba(1f, 1f, 1f, 1f)
                : inWin ? FxMath.Rgba(1f, 0.85f, 0.3f, 1f)
                : FxMath.Rgba(0.45f, 0.85f, 1f, 0.85f);
            _ringTf.position = FxMath.V3(_target.x, _target.y, z);
            _ringTf.localScale = FxMath.V3(r * 2f, r * 2f, 1f);
            _ring.color = col;
            _ringGlowTf.position = FxMath.V3(_target.x, _target.y, z + 0.001f);
            _ringGlowTf.localScale = FxMath.V3(r * 2.25f, r * 2.25f, 1f);
            _ringGlow.color = FxMath.Rgba(0f, 0f, 0f, 0.45f);
        }
        if (ring || hold)
        {
            float pulse = 0.85f + 0.15f * FxMath.Sin(_anim * 12f);
            _dotTf.position = FxMath.V3(_target.x, _target.y, _target.y / 1000f - 0.021f);
            _dotTf.localScale = FxMath.V3(0.36f * pulse, 0.36f * pulse, 1f);
            _dot.color = hold ? FxMath.Rgba(1f, 1f, 1f, 0.5f) : FxMath.Rgba(1f, 0.95f, 0.75f, 0.9f);
        }

        // つかんでいる間: 角に手・体を穴へ伸ばして震わせる・頭の上にゲージ
        if (hold != _grabOn)
        {
            _grabOn = hold;
            _handTf.gameObject.SetActive(hold);
            _barBgTf.gameObject.SetActive(hold);
            _barTf.gameObject.SetActive(hold);
        }
        if (hold)
        {
            // 手は角から体の方へ少し寄せる (角の点は壁の中の升の中心)
            _handTf.position = FxMath.V3(_target.x + (pos.x - _target.x) * 0.3f, _target.y + (pos.y - _target.y) * 0.3f, pos.y / 1000f - 0.003f);
            float ang = FxMath.Atan2(_dir.y, _dir.x) * 57.29578f;
            _handTf.localRotation = FxMath.RotZ(ang + 90f);
            // 縁にしがみついている間の体は全員の端末で倒す (TickClings)。ゲージは手が離れるまでの残り
            float g;
            if (grab) { BendBody(lp); g = FxMath.Clamp01(_gauge); }
            else g = 1f - FxMath.Clamp01(ClingAge(lp.PlayerId) / FxMath.Max(1f, Roles.TerrainDeath.DyingTime));
            float bx = pos.x - 0.45f, by = pos.y + 1.35f, bz = pos.y / 1000f - 0.03f;
            _barBgTf.position = FxMath.V3(pos.x, by, bz + 0.001f);
            _barBgTf.localScale = FxMath.V3(0.98f, 0.17f, 1f);
            _barTf.position = FxMath.V3(bx, by, bz);
            _barTf.localScale = FxMath.V3(0.9f * g, 0.1f, 1f);
            _bar.color = g < 0.35f ? FxMath.Rgba(1f, 0.3f, 0.25f, 1f) : g < 0.7f ? FxMath.Rgba(1f, 0.8f, 0.25f, 1f) : FxMath.Rgba(0.4f, 1f, 0.45f, 1f);
        }
        if (!grab) RestoreBody();

        bool rim = _state == State.Rim || (grab && _rim);
        int kind = grab ? (rim ? 4 : 2) : ring ? (rim ? 3 : 1) : 0;
        if (kind != _promptKind) SetPrompt(kind);
        if (kind != 0) DrawKey(grab);
    }

    // 札の色 = 輪と同じ (灰 = まだ押せない・水色 = 待つ・黄 = 今・白 = ぴったり)。今の間は脈打ち、連打は押すたびに跳ねる
    private static void DrawKey(bool grab)
    {
        if (_keyTf is null) return; // 船が替わると null に戻す (毎フレームの Unity の生存確認を避ける)
        int look = grab ? 4 : _t < _lockEnd ? 0 : _de <= Perfect ? 3 : _de <= Window ? 2 : 1;
        if (look != _keyLook)
        {
            _keyLook = look;
            var col = look switch
            {
                0 => FxMath.Rgba(0.55f, 0.55f, 0.6f, 0.8f),
                1 => FxMath.Rgba(0.45f, 0.85f, 1f, 1f),
                3 => FxMath.Rgba(1f, 1f, 1f, 1f),
                _ => FxMath.Rgba(1f, 0.85f, 0.3f, 1f),
            };
            _keyRim.color = col;
            _keyText.color = col;
            if (_tapBtn) _tapBtn.color = FxMath.Rgba(col.r, col.g, col.b, look >= 2 ? 0.75f : 0.45f);
        }
        float sc = 1f;
        float since = _anim - _popAt;
        if (grab) { if (_popAt >= 0f && since < KeyPop) sc = 1f + 0.25f * (1f - since / KeyPop); }
        else if (look >= 2) sc = 1.12f + 0.06f * FxMath.Sin(_anim * 20f);
        if (sc == _keyScale) return;
        _keyScale = sc;
        _keyTf.localScale = FxMath.V3(sc, sc, 1f);
        if (_tapTf != null) _tapTf.localScale = FxMath.V3(sc, sc, 1f);
    }

    private const float KeyPop = 0.15f;

    // 角を両手で握り、体は流れの先へ引っぱられて倒れて震える (縁にしがみつく姿と同じ這う絵)。
    // 本人の画面だけ・位置の同期は触らない。ゲージが減るほど強く震える
    private static void BendBody(PlayerControl lp)
    {
        if (_bendNo) return;
        if (_bend != null && !CrewFlip.IsPlaying(lp.PlayerId)) { _bend.Restore(); _bend = null; _bendNo = true; return; }
        if (_bend == null)
        {
            _bend = new Pose(lp); // 連番より先に (縁にしがみつく姿と同じ理由)
            if (!CrewFlip.Play(lp, GripClip)) { _bend.Restore(); _bend = null; _bendNo = true; return; }
        }
        var rt = lp.transform.position;
        float k = _rim ? 1f : 1f - FxMath.Clamp01(_gauge);
        Aim(_bend, FxMath.V2(rt.x, rt.y), _target, _dir, _anim, k, lp.cosmetics && lp.cosmetics.FlipX);
    }

    private static bool _bendNo; // このつかまりの間は這う絵を流せなかった (立ち姿のまま)

    private static void RestoreBody()
    {
        _bendNo = false;
        if (_bend == null) return;
        if (_bend.Alive) _bend.Restore();
        _bend = null;
        var lp = PlayerControl.LocalPlayer;
        if (lp && _state != State.Cling) CrewFlip.Stop(lp.PlayerId);
    }

    private static float ClingAge(byte id)
    {
        foreach (var c in Clings)
            if (c.Id == id) return (c.HeldAt > 0f ? c.HeldAt : Time.time) - c.At;
        return 0f;
    }

    private static void Hide()
    {
        _shown = false;
        RestoreBody();
        if (_root) _root.SetActive(false);
        if (_promptKind != 0) SetPrompt(0);
    }

    private static void SetPrompt(int kind)
    {
        _promptKind = kind;
        _keyLook = -1;
        _keyScale = -1f;
        if (_prompt) _prompt.gameObject.SetActive(kind != 0);
        if (_tapBtn) _tapBtn.gameObject.SetActive(kind != 0);
        if (_keyTf) _keyTf.gameObject.SetActive(kind != 0);
        if (kind == 0 || !_prompt) return;
        // 3 = 口の際の輪・4 = 口の際の連打 (札は 1/2 と同じ・案内文だけ変える)
        bool rim = kind >= 3;
        if (rim) kind -= 2;
        if (_keyTf)
        {
            // つかむ時は名前の上・連打の間はゲージ (頭の上 1.35) のさらに上
            _keyTf.localPosition = FxMath.V3(0f, kind == 1 ? 1.5f : 1.8f, -800f);
            string key = Touch
                ? (kind == 1 ? Tr.Get("CrewGrip.TapKeyTouch") : Tr.Get("CrewGrip.MashKeyTouch"))
                : (kind == 1 ? "SPACE" : Tr.Get("CrewGrip.MashKeyPc"));
            float w = Touch ? 3.0f : kind == 1 ? 1.7f : 2.6f;
            _keyText.text = key;
            _keyText.rectTransform.sizeDelta = FxMath.V2(w, 0.6f);
            _keyFill.size = FxMath.V2(w, 0.6f);
            _keyRim.size = FxMath.V2(w + 0.12f, 0.72f);
        }
        if (_tapText) _tapText.text = kind == 1 ? Tr.Get("CrewGrip.Grab") : Tr.Get("CrewGrip.Mash");
        if (rim)
        {
            _prompt.text = kind == 1
                ? (Touch ? Tr.Get("CrewGrip.LastWallTouch") : Tr.Get("CrewGrip.LastWallPc"))
                : (Touch ? Tr.Get("CrewGrip.WindTouch") : Tr.Get("CrewGrip.WindPc"));
            return;
        }
        _prompt.text = kind == 1
            ? (Touch ? Tr.Get("CrewGrip.RingTouch") : Tr.Get("CrewGrip.RingPc"))
            : (Touch ? Tr.Get("CrewGrip.ClimbTouch") : Tr.Get("CrewGrip.ClimbPc"));
    }

    private static bool EnsureArt(PlayerControl lp)
    {
        if (_root) return true;
        if (!ShipStatus.Instance) return false;
        _root = new GameObject("MrpCrewGrip") { layer = 0 };
        _root.transform.SetParent(ShipStatus.Instance.transform, false);
        // 左右反転した船の中でも手の向きと絵・伸びる棒の向きが裏返らないように、入れ物の横の倍率で反転を打ち消す
        if (ShipStatus.Instance.transform.lossyScale.x < 0f) _root.transform.localScale = FxMath.V3(-1f, 1f, 1f);
        GameClock.Ship.Bind(_root);
        Roles.FxRoot.TrackWorld(_root); // 逆再生の間は見えていたとおりに逆へ流す (画面の案内は TrackHud)
        _ringOn = _dotOn = _grabOn = false;
        _ringSprite ??= Bake(128, RingAlpha, "MrpGripRing");
        _dotSprite ??= Bake(64, DotAlpha, "MrpGripDot");
        _whiteSprite ??= Bake(8, (_, _) => 1f, "MrpGripWhite", new Vector2(0f, 0.5f));
        _ringGlow = Part("Glow", _ringSprite, out _ringGlowTf);
        _ring = Part("Ring", _ringSprite, out _ringTf);
        _dot = Part("Dot", _dotSprite, out _dotTf);
        _barBg = Part("BarBg", Options.MenuArt.Round(0.06f), out _barBgTf);
        _barBg.drawMode = SpriteDrawMode.Sliced;
        _barBg.size = FxMath.V2(1f, 1f);
        _barBg.color = FxMath.Rgba(0f, 0f, 0f, 0.7f);
        _bar = Part("Bar", _whiteSprite, out _barTf);
        _hand = Part("Hand", FxHands.GripSprite(), out _handTf);
        var mat = FxHands.PlayerMat();
        if (mat) { _hand.sharedMaterial = mat; PlayerMaterial.SetColors(lp.Data.DefaultOutfit.ColorId, _hand); }
        _handTf.localScale = FxMath.V3(0.3f, 0.3f, 1f);

        var hud = Vanilla.Hud;
        var src = hud && hud.TaskPanel ? hud.TaskPanel.taskText : null;
        if (src && !_prompt)
        {
            _prompt = MakeText(src, hud.transform, "MrpGripPrompt", FxMath.V3(0f, -1.55f, -800f), 7f, 2.6f);
            _prompt.color = FxMath.Rgba(1f, 0.95f, 0.8f, 1f);
            GameClock.Ship.Bind(_prompt.gameObject);
            Roles.FxRoot.TrackHud(_prompt.transform);
            _prompt.gameObject.SetActive(false);

            var key = Roles.FxRoot.NewHud("MrpGripKey");
            _keyTf = key.transform;
            _keyRim = KeyPlate(_keyTf, "Rim", 0.18f, 0.002f, FxMath.Rgba(0.45f, 0.85f, 1f, 1f));
            _keyFill = KeyPlate(_keyTf, "Fill", 0.12f, 0.001f, FxMath.Rgba(0.06f, 0.06f, 0.09f, 0.9f));
            _keyText = MakeText(src, _keyTf, "Label", FxMath.V3(0f, 0f, 0f), 1.7f, 2.2f);
            GameClock.Ship.Bind(key);
            key.SetActive(false);
            if (Touch && hud.UICamera)
            {
                var cam = hud.UICamera;
                var b = Roles.FxRoot.NewHud("MrpGripTap");
                b.transform.localPosition = FxMath.V3(cam.orthographicSize * cam.aspect - 1.3f, 0.4f, -800f);
                _tapBtn = b.AddComponent<SpriteRenderer>();
                _tapBtn.sprite = Options.MenuArt.Round(0.6f);
                _tapBtn.drawMode = SpriteDrawMode.Sliced;
                _tapBtn.size = FxMath.V2(1.6f, 1.6f);
                _tapBtn.color = FxMath.Rgba(1f, 0.85f, 0.3f, 0.45f);
                _tapTf = b.transform;
                _tapText = MakeText(src, b.transform, "Label", FxMath.V3(0f, 0f, -0.001f), 1.6f, 2.4f);
                GameClock.Ship.Bind(b);
                b.SetActive(false);
            }
            _promptKind = -1;
        }
        _root.SetActive(false);
        return true;
    }

    // HUD のタスク欄の文字を複製して文字だけの部品にする (言語表の書き戻しや当たり判定の部品を外す)
    private static TextMeshPro MakeText(TextMeshPro src, Transform parent, string name, Vector3 pos, float width, float size)
    {
        var go = UnityEngine.Object.Instantiate(src.gameObject, parent);
        go.name = name;
        for (int i = go.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
        foreach (var c in go.GetComponents<Component>())
            if (!c.TryCast<Transform>() && !c.TryCast<TextMeshPro>() && !c.TryCast<Renderer>() && !c.TryCast<MeshFilter>()) UnityEngine.Object.Destroy(c);
        go.transform.localPosition = pos;
        go.transform.localScale = Vector3.one;
        var t = go.GetComponent<TextMeshPro>();
        t.rectTransform.sizeDelta = FxMath.V2(width, 0.6f);
        t.alignment = TextAlignmentOptions.Center;
        t.enableAutoSizing = false;
        t.fontSize = size;
        t.enableWordWrapping = false;
        t.outlineWidth = 0.2f;
        t.outlineColor = new Color32(0, 0, 0, 255);
        return t;
    }

    private static SpriteRenderer KeyPlate(Transform parent, string name, float radius, float z, Color col)
    {
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = FxMath.V3(0f, 0f, z);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Options.MenuArt.Round(radius);
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = FxMath.V2(1.7f, 0.6f);
        sr.color = col;
        return sr;
    }

    private static SpriteRenderer Part(string name, Sprite sp, out Transform tf)
    {
        var go = new GameObject(name) { layer = 0 };
        go.transform.SetParent(_root.transform, false);
        tf = go.transform;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        go.SetActive(false);
        return sr;
    }

    // 輪: 半径 0.86 の細い線 + 外側へのにじみ (1 単位の絵・色は SpriteRenderer で)
    private static float RingAlpha(float x, float y)
    {
        float r = MathF.Sqrt(x * x + y * y) * 2f, d = MathF.Abs(r - 0.86f);
        float line = MathF.Max(0f, 1f - d / 0.035f);
        float glow = MathF.Exp(-d * d / (2f * 0.07f * 0.07f)) * 0.45f;
        return MathF.Min(1f, line + glow);
    }

    // 点: 白い核 + にじみ
    private static float DotAlpha(float x, float y)
    {
        float r = MathF.Sqrt(x * x + y * y) * 2f;
        float core = r < 0.22f ? 1f : MathF.Max(0f, 1f - (r - 0.22f) / 0.06f);
        float glow = MathF.Exp(-r * r / (2f * 0.3f * 0.3f)) * 0.6f;
        return MathF.Min(1f, core + glow);
    }

    // 白い絵 (n×n・1 単位)。f(x, y) は中心からの位置 (-0.5〜0.5) → 不透明度
    private static unsafe Sprite Bake(int n, Func<float, float, float> f, string name, Vector2? pivot = null)
    {
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float a = Math.Clamp(f((x + 0.5f) / n - 0.5f, (y + 0.5f) / n - 0.5f), 0f, 1f);
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(a * 255f + 0.5f);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = name };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), pivot ?? new Vector2(0.5f, 0.5f), n);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // ── 確認用 ─────────────────────────────────────────────────────────

    internal static void Register()
    {
        TestBridge.Register("grip", "[press [n] | auto [perfect|window|off] | list | out | shove] 吸い出しのつかみ: 状態・ゲージ・狙っている角・つかめる角の数 (press = 押した事にする・n 回 / auto = 輪が角に重なったら押し・つかまったら毎秒 5 回押す / list = 近いつかめる角 / out = 自分を吸い出す依頼を出す / shove = 自分が突き飛ばされた事にする)", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string cmd = a.Length > 0 ? a[0] : "";
            if (cmd == "press")
            {
                int n = a.Length > 1 && int.TryParse(a[1], out int v) ? v : 1;
                _presses += n;
                reply($"OK grip press {n} state={_state}");
                return;
            }
            if (cmd == "auto")
            {
                _auto = a.Length < 2 || a[1] == "perfect" ? Perfect : a[1] == "window" ? Window : -1f;
                reply($"OK grip auto={_auto:0.00}");
                return;
            }
            if (cmd == "list")
            {
                var me0 = PlayerControl.LocalPlayer;
                var p0 = me0 ? me0.GetTruePosition() : Vector2.zero;
                var sb = new System.Text.StringBuilder();
                var g = new List<Vector2>(Decompression.GripPoints);
                g.Sort((u, v) => ((u - p0).sqrMagnitude).CompareTo((v - p0).sqrMagnitude));
                for (int i = 0; i < g.Count && i < 12; i++) sb.Append($" {g[i].x:0.00},{g[i].y:0.00}");
                reply($"OK grip list n={g.Count}{sb}");
                return;
            }
            if (cmd == "shove")
            {
                Shove();
                reply($"OK grip shove state={_state}");
                return;
            }
            if (cmd == "out")
            {
                var lp = PlayerControl.LocalPlayer;
                if (!lp) { reply("ERR grip no player"); return; }
                Out(lp);
                reply("OK grip out requested");
                return;
            }
            var me = PlayerControl.LocalPlayer;
            float md = me ? Decompression.MouthDist(me.GetTruePosition(), out _) : float.MaxValue;
            reply($"OK grip state={_state} t={_t:0.00} mul={_mul:0.00} target={(_hasTarget ? $"{_target.x:0.00},{_target.y:0.00} de={_de:0.00}" : "none")} gauge={_gauge:0.00} lock={MathF.Max(0f, _lockEnd - _t):0.00} immune={MathF.Max(0f, _immuneEnd - _t):0.00} mouth={(md == float.MaxValue ? "none" : md.ToString("0.00"))} grips={Decompression.GripPoints.Count} flights={Flights.Count} shown={_shown}");
        });
    }
}
