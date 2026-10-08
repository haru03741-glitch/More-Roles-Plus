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

    private enum State : byte { Free, Grabbed, Pushing, Lost }

    private static State _state;
    private static float _t;                // 物理の刻みで進む自分の時計
    private static float _dt;
    private static float _lockEnd, _pushEnd, _immuneEnd, _lostAt;
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
    private static bool _shown, _ringOn, _dotOn, _grabOn;
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
    }
    private static readonly List<Flight> Flights = new();
    // 吸い出しで本編の追放を走らせている間の人 (追放の出来事を起こさない)
    internal static int Spacing = -1;

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
        }
        if (Flights.Count > 0) TickFlights();
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
                _gauge += presses * Mash - Drain * (mul / EdgeMul) * _dt;
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
                    Plugin.Logger.LogInfo("[CrewGrip] lost grip");
                    Let();
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
        }

        // Free
        // 口に触れたら外へ。エアシップの風は弱い (歩く速さの 0.4 倍) ので、風があれば落ちる
        if (on && mul >= (Decompression.Wind ? 0.01f : DangerMul) && dist <= OutDist && Decompression.MouthDist(pos, out _) <= OutReach)
        {
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
            if (_hasTarget && _de <= Window) Grab(pos);
            else _lockEnd = _t + Lockout;
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

    private static void Grab(Vector2 pos)
    {
        _state = State.Grabbed;
        // 体を角の手前 (来た側) HoldGap へ寄せる。来た側は歩けた所なので壁の中には入らない
        float dx = pos.x - _target.x, dy = pos.y - _target.y, len = MathF.Sqrt(dx * dx + dy * dy);
        _hold = len > HoldGap ? FxMath.V2(_target.x + dx / len * HoldGap, _target.y + dy / len * HoldGap) : pos;
        _gauge = _de <= Perfect ? PerfectGauge : StartGauge;
        Plugin.Logger.LogInfo($"[CrewGrip] grab at {_target.x:0.00},{_target.y:0.00} de={_de:0.00} gauge={_gauge:0.0}");
    }

    private static void Let()
    {
        _state = State.Free;
        _hasTarget = false;
        RestoreBody();
    }

    internal static void Cancel()
    {
        if (_state == State.Free && !_shown) return;
        _state = State.Free;
        _hasTarget = false;
        _presses = 0;
        RestoreBody();
        if (_shown) Hide();
    }

    // ── 吸い出し ───────────────────────────────────────────────────────

    private static void Out(PlayerControl pc)
    {
        _state = State.Lost;
        _lostAt = _t;
        _hasTarget = false;
        Plugin.Logger.LogInfo($"[CrewGrip] out at {pc.GetTruePosition().x:0.00},{pc.GetTruePosition().y:0.00}");
        OutRequest.Send(pc.PlayerId);
    }

    private static readonly RemoteCall<byte> OutRequest = new("Decomp.Out", Route.ToHost,
        (w, pid) => w.Write(pid), r => r.ReadByte(),
        (sender, pid) =>
        {
            if (!sender || sender.PlayerId != pid) return;
            string why = Refuse(sender);
            if (why != null)
            {
                Plugin.Logger.LogWarning($"[CrewGrip] out refused {pid}: {why}");
                return;
            }
            Spaced.Send(pid);
            StartFlight(pid);
        });

    private static readonly RemoteCall<byte> Spaced = new("Decomp.Spaced", Route.HostToAll,
        (w, pid) => w.Write(pid), r => r.ReadByte(), (_, pid) => StartFlight(pid));

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

    private static void StartFlight(byte pid)
    {
        if (Interrupted) return;
        var p = GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;
        if (!p || p.Data == null || p.Data.IsDead) return;
        foreach (var f in Flights) if (f.Pc && f.Pc.PlayerId == pid) return;
        if (p.AmOwner) { RestoreBody(); if (_shown) Hide(); _state = State.Lost; _lostAt = _t; }
        var pos = p.GetTruePosition();
        float md = Decompression.MouthDist(pos, out var m);
        float mx = 0f, my = 0.3f;
        if (md < 5f) { mx = m.x - pos.x; my = m.y - pos.y; }
        float len = MathF.Sqrt(mx * mx + my * my);
        float dx = len > 0.05f ? mx / len : 0f, dy = len > 0.05f ? my / len : 1f;
        if (len <= 0.05f && Decompression.PullAt(pos, out var dir, out _, out _)) { dx = dir.x; dy = dir.y; }
        var pose = new Pose(p);
        pose.HideName();
        Flights.Add(new Flight { Pc = p, Pose = pose, Mx = mx, My = my, Dx = dx, Dy = dy });
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
        try { p.Exiled(); }
        finally { Spacing = -1; }
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
        bool ring = _state == State.Free && _hasTarget;
        bool grab = _state == State.Grabbed;

        // 縮む輪: 角を中心に半径 = 体の縁から角までの距離。角の点に重なった時が押し時
        if (ring != _ringOn) { _ringOn = ring; _ringTf.gameObject.SetActive(ring); _ringGlowTf.gameObject.SetActive(ring); }
        if ((ring || grab) != _dotOn) { _dotOn = ring || grab; _dotTf.gameObject.SetActive(_dotOn); }
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
        if (ring || grab)
        {
            float pulse = 0.85f + 0.15f * FxMath.Sin(_anim * 12f);
            _dotTf.position = FxMath.V3(_target.x, _target.y, _target.y / 1000f - 0.021f);
            _dotTf.localScale = FxMath.V3(0.36f * pulse, 0.36f * pulse, 1f);
            _dot.color = grab ? FxMath.Rgba(1f, 1f, 1f, 0.5f) : FxMath.Rgba(1f, 0.95f, 0.75f, 0.9f);
        }

        // つかんでいる間: 角に手・体を穴へ伸ばして震わせる・頭の上にゲージ
        if (grab != _grabOn)
        {
            _grabOn = grab;
            _handTf.gameObject.SetActive(grab);
            _barBgTf.gameObject.SetActive(grab);
            _barTf.gameObject.SetActive(grab);
        }
        if (grab)
        {
            // 手は角から体の方へ少し寄せる (角の点は壁の中の升の中心)
            _handTf.position = FxMath.V3(_target.x + (pos.x - _target.x) * 0.3f, _target.y + (pos.y - _target.y) * 0.3f, pos.y / 1000f - 0.003f);
            float ang = FxMath.Atan2(_dir.y, _dir.x) * 57.29578f;
            _handTf.localRotation = FxMath.RotZ(ang + 90f);
            BendBody(lp);
            float g = FxMath.Clamp01(_gauge);
            float bx = pos.x - 0.45f, by = pos.y + 1.35f, bz = pos.y / 1000f - 0.03f;
            _barBgTf.position = FxMath.V3(pos.x, by, bz + 0.001f);
            _barBgTf.localScale = FxMath.V3(0.98f, 0.17f, 1f);
            _barTf.position = FxMath.V3(bx, by, bz);
            _barTf.localScale = FxMath.V3(0.9f * g, 0.1f, 1f);
            _bar.color = g < 0.35f ? FxMath.Rgba(1f, 0.3f, 0.25f, 1f) : g < 0.7f ? FxMath.Rgba(1f, 0.8f, 0.25f, 1f) : FxMath.Rgba(0.4f, 1f, 0.45f, 1f);
        }
        else RestoreBody();

        int kind = grab ? 2 : ring ? 1 : 0;
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

    // つかんだ角を軸に穴の方へ 1.15 倍伸ばし ±4° で震わせる (体の絵だけ・位置の同期は触らない)
    private static void BendBody(PlayerControl lp)
    {
        _bend ??= new Pose(lp);
        float shake = 4f * FxMath.Sin(_anim * 88f) * (0.6f + 0.4f * FxMath.Sin(_anim * 13f));
        float lean = -_dir.x * 30f;
        bool vert = FxMath.Abs(_dir.y) > FxMath.Abs(_dir.x);
        float sx = vert ? 0.94f : 1.15f, sy = vert ? 1.15f : 0.94f;
        _bend.Set(_dir.x * 0.12f, _dir.y * 0.12f, lean + shake, sx, sy);
    }

    private static void RestoreBody()
    {
        if (_bend == null) return;
        if (_bend.Alive) _bend.Restore();
        _bend = null;
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
        bool ja = Lang.IsJapanese;
        if (_keyTf)
        {
            // つかむ時は名前の上・連打の間はゲージ (頭の上 1.35) のさらに上
            _keyTf.localPosition = FxMath.V3(0f, kind == 1 ? 1.5f : 1.8f, -800f);
            string key = Touch
                ? (kind == 1 ? (ja ? "右上をタップ" : "TAP upper right") : (ja ? "右上を連打" : "MASH upper right"))
                : (kind == 1 ? "SPACE" : (ja ? "SPACE 連打" : "MASH SPACE"));
            float w = Touch ? 3.0f : kind == 1 ? 1.7f : 2.6f;
            _keyText.text = key;
            _keyText.rectTransform.sizeDelta = FxMath.V2(w, 0.6f);
            _keyFill.size = FxMath.V2(w, 0.6f);
            _keyRim.size = FxMath.V2(w + 0.12f, 0.72f);
        }
        if (_tapText) _tapText.text = kind == 1 ? (ja ? "つかむ" : "GRAB") : (ja ? "連打" : "MASH");
        _prompt.text = kind == 1
            ? (Touch ? (ja ? "輪が角に重なったら画面の右上をタップ!" : "Tap the upper right when the ring meets the corner!") : (ja ? "輪が角に重なったら スペース/クリック!" : "Space/Click when the ring meets the corner!"))
            : (Touch ? (ja ? "画面の右上を連打して戻れ!" : "Mash the upper right to climb back!") : (ja ? "スペース/クリック連打で戻れ!" : "Mash Space/Click to climb back!"));
    }

    private static bool EnsureArt(PlayerControl lp)
    {
        if (_root) return true;
        if (!ShipStatus.Instance) return false;
        _root = new GameObject("MrpCrewGrip") { layer = 0 };
        _root.transform.SetParent(ShipStatus.Instance.transform, false);
        GameClock.Ship.Bind(_root);
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
            _prompt.gameObject.SetActive(false);

            var key = new GameObject("MrpGripKey") { layer = hud.gameObject.layer };
            _keyTf = key.transform;
            _keyTf.SetParent(hud.transform, false);
            _keyRim = KeyPlate(_keyTf, "Rim", 0.18f, 0.002f, FxMath.Rgba(0.45f, 0.85f, 1f, 1f));
            _keyFill = KeyPlate(_keyTf, "Fill", 0.12f, 0.001f, FxMath.Rgba(0.06f, 0.06f, 0.09f, 0.9f));
            _keyText = MakeText(src, _keyTf, "Label", FxMath.V3(0f, 0f, 0f), 1.7f, 2.2f);
            GameClock.Ship.Bind(key);
            key.SetActive(false);
            if (Touch && hud.UICamera)
            {
                var cam = hud.UICamera;
                var b = new GameObject("MrpGripTap") { layer = hud.gameObject.layer };
                b.transform.SetParent(hud.transform, false);
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
        TestBridge.Register("grip", "[press [n] | auto [perfect|window|off] | list | out] 吸い出しのつかみ: 状態・ゲージ・狙っている角・つかめる角の数 (press = 押した事にする・n 回 / auto = 輪が角に重なったら押し・つかまったら毎秒 5 回押す / list = 近いつかめる角 / out = 自分を吸い出す依頼を出す)", (args, reply) =>
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
