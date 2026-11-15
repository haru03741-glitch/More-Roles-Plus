using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 瀕死の人 (地形で崩れ落ちた人・外壁の穴の縁にしがみついている人) を助け出す。
// 近くの生きている人が長押しするか守り霊が「救う」と、その人を掴んで引きずって運ぶ。運んでいる間は瀕死の時計が止まり、
// 運べる秒には限りがある (瀕死 1 回につき合計)。火の外・足の着く水まで運ぶか、縁から内側へ引き戻せたら助かる。
// 崩れた壁の下敷きは長押しでその場で助け出す。運ぶか・助かるか・放すかはホストが決めて全員へ配る
internal static class Rescue
{
    internal const byte ByLiving = 0, BySpirit = 1;
    private const float Near = 3f;        // ボタンを出す距離
    private const float Reach = 1f;       // 助け出せる距離
    private const float HoldTime = 1.5f;  // 長押しの秒
    private const int CheckEvery = 6;     // 何刻みごとに近くを見るか
    private const float HaulSpeed = 0.6f; // 運んでいる人の歩く速さの倍
    internal const float Brace = 0.3f;    // 運んでいる人が受ける吸い出しの流れの割合 (踏ん張る)
    private const float SpiritClingSpeed = 0.35f; // 守り霊が縁から引き戻す時の速さの倍 (気圧に逆らう)
    private const float Gap = 0.85f;      // 運ぶ人と引きずられる体の間 (這う姿は横に長い)
    private const float Snap = 2.5f;      // これより離れたら (はしご・会議の後) 一気に寄せる
    private const float LetGo = 2.2f;     // ホスト: 壁に引っかかってこれより離れたら手が離れる
    private const float SafeHold = 0.4f;  // 安全な所にこの秒いたら助かる
    private const float WalkSpeed = 0.3f; // 運ぶ人がこれより速く動いていれば歩く姿 (単位/秒)
    private const float StillDelay = 0.15f; // 止まってからこの秒で止まった姿へ (歩きの切れ目でちらつかせない)
    private const float ClingBack = 0.8f; // 縁から内側へこれだけ引き戻せたら助かる
    private const float DriftEvery = 0.1f;

    private static Lifespan _life;
    private static ModButton _button;
    private static int _check;

    // 運んでいる組 (全員の端末で同じ)
    private sealed class Haul
    {
        public PlayerControl T, By;
        public byte Tid, ById;
        public bool Spirit, Cling;
        public sbyte Flip = -1; // 最後に書いた引きずられる体の向き (-1 = まだ)
        public float StartMouth, SafeFor, NextDrift;
        public Vector2 ByLast;
        public float StillFor, NextPose;
        public bool Walking, Posed, FaceLeft;
    }
    private static readonly List<Haul> Hauls = new();
    private static readonly Dictionary<byte, float> Left = new(); // 運べる残りの秒 (瀕死 1 回につき)
    private static byte _localHaul = 255;
    private static int _shipGen = -1;

    internal static bool LocalHauling => _localHaul != 255;
    internal static bool AnyHaul => Hauls.Count > 0;

    // 助け出せる状態か (全員の端末で同じ答えになる物だけを見る)
    internal static bool Dying(byte id) => TerrainDeath.IsDying(id) || Terrain.CrewGrip.Clinging(id);

    internal static bool Carried(byte id)
    {
        foreach (var h in Hauls) if (h.Tid == id) return true;
        return false;
    }

    private static bool Hauling(byte id)
    {
        foreach (var h in Hauls) if (h.ById == id) return true;
        return false;
    }

    // ── ホストへの依頼と、全員への知らせ ────────────────────────────────
    private static readonly RemoteCall<byte> Request = new("Rescue.Request", Route.ToHost,
        (w, m) => w.Write(m), r => r.ReadByte(),
        (sender, target) =>
        {
            var t = PortKit.ById(target);
            string why = Refuse(sender, t);
            if (why != null) { Plugin.Logger.LogInfo($"[Rescue] refused {(sender ? sender.PlayerId : 255)}->{target}: {why}"); return; }
            HostBegin(sender, t);
        }, perSecond: 4);

    private static readonly RemoteCall<(byte target, byte by, byte kind)> Saved = new("Rescue.Saved", Route.HostToAll,
        (w, m) => { w.Write(m.target); w.Write(m.by); w.Write(m.kind); }, r => (r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (_, m) => Apply(m.target, m.by, m.kind));

    private static readonly RemoteCall<(byte target, byte by)> CarryCall = new("Rescue.Carry", Route.HostToAll,
        (w, m) => { w.Write(m.target); w.Write(m.by); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => Begin(m.target, m.by, false));

    private static readonly RemoteCall<byte> DropCall = new("Rescue.Drop", Route.HostToAll,
        (w, m) => w.Write(m), r => r.ReadByte(), (_, m) => End(m, false));

    private static string Refuse(PlayerControl user, PlayerControl target)
    {
        if (!user || !target || user == target) return "who";
        if (MeetingHud.Instance || ExileController.Instance || !ShipStatus.Instance) return "not now";
        if (user.Data == null || user.Data.IsDead || user.inVent || Dying(user.PlayerId) || Hauling(user.PlayerId)) return "user";
        if (target.Data == null || target.Data.IsDead || !Dying(target.PlayerId)) return "not dying";
        if (Carried(target.PlayerId)) return "carried";
        if (Left.TryGetValue(target.PlayerId, out float left) && left <= 0f) return "spent";
        string why = Targeting.Unreachable(user, target, Reach + 0.5f);
        if (why != "wall" || !Terrain.CrewGrip.Clinging(target.PlayerId)) return why;
        return Blocked(user.GetTruePosition(), target.GetTruePosition(), true) ? "wall" : null;
    }

    private const float HandReach = 0.6f; // しがみついている人の手の先 (足元から口と逆の向きへ)

    // from から体まで壁があるか。縁にしがみついている体の足元は口の際 (壁の線の上) にあるので、
    // 体まで通らなければ、口と逆の向きへ伸ばした手の先までを見る
    private static bool Blocked(Vector2 from, Vector2 body, bool cling)
    {
        if (!PhysicsHelpers.AnythingBetween(from, body, WallMask, false)) return false;
        if (!cling) return true;
        Terrain.Decompression.MouthDist(body, out var m);
        float ix = body.x - m.x, iy = body.y - m.y, il = System.MathF.Sqrt(ix * ix + iy * iy);
        if (il < 0.01f) return true;
        var hand = FxMath.V2(body.x + ix / il * HandReach, body.y + iy / il * HandReach);
        return PhysicsHelpers.AnythingBetween(from, hand, WallMask, false);
    }

    // 壁と家具 (家具の層 12 は ShipAndObjectsMask に入っていない)
    private static readonly int WallMask = Constants.ShipAndObjectsMask | (1 << 12);

    // ホスト (確かめ用): by が target を長押しで助け出したのと同じ道で確かめて配る。断った理由 (通れば null)
    internal static string HostTry(PlayerControl by, PlayerControl target)
    {
        string why = Refuse(by, target);
        if (why == null) HostBegin(by, target);
        return why;
    }

    // ホスト: 認めた長押し。下敷きはその場で助け出し、火・水・縁は掴んで運び始める
    private static void HostBegin(PlayerControl by, PlayerControl target)
    {
        if (Pinned(target.PlayerId)) { HostSave(by, target, ByLiving); return; }
        CarryCall.Send((target.PlayerId, by.PlayerId));
        Begin(target.PlayerId, by.PlayerId, false);
    }

    // 守り霊が狙える瀕死の人: まだ誰にも運ばれておらず、運べる秒が残っていて、守り霊が今は誰も運んでいない
    internal static bool SpiritCanTake(byte spirit, byte target) =>
        Dying(target) && !Carried(target) && !Hauling(spirit) && !(Left.TryGetValue(target, out float l) && l <= 0f);

    // 崩れた壁の下敷き (縁にしがみついてはいない)。瓦礫の下からその場で助け出す
    private static bool Pinned(byte id) => TerrainDeath.CauseOf(id) == TerrainDeath.Cause.Crush && !Terrain.CrewGrip.Clinging(id);

    // 全員 (守り霊の「救う」はホストが確かめてから全員の端末で呼ばれる): 下敷きはその場で、それ以外は掴んで運ぶ
    internal static void SpiritUse(PlayerControl spirit, PlayerControl target)
    {
        if (!spirit || !target) return;
        if (Pinned(target.PlayerId)) Apply(target.PlayerId, spirit.PlayerId, BySpirit);
        else Begin(target.PlayerId, spirit.PlayerId, true);
    }

    // ホスト: 認めた助け出しを全員へ (守り霊の能力もここを通す)
    internal static void HostSave(PlayerControl by, PlayerControl target, byte kind)
    {
        if (!by || !target) return;
        Saved.Send((target.PlayerId, by.PlayerId, kind));
        Apply(target.PlayerId, by.PlayerId, kind);
    }

    internal static void Apply(byte target, byte by, byte kind)
    {
        Plugin.Logger.LogInfo($"[Rescue] saved {target} by {by} kind={kind}");
        End(target, true);
        Left.Remove(target);
        TerrainDeath.OnRescued(target);
        Terrain.CrewGrip.OnRescued(target);
        if (kind == BySpirit) Addons.WardSpirit.OnSaved(target, by);
    }

    // ── 運ぶ ─────────────────────────────────────────────────
    // 全員: by が target を掴んだ。瀕死の時計を止め、体は這う姿で運ぶ人の後ろを引きずられる
    private static void Begin(byte target, byte by, bool spirit)
    {
        var t = PortKit.ById(target);
        var b = PortKit.ById(by);
        if (!t || !b || t.Data == null || t.Data.IsDead || Carried(target) || Hauling(by) || !Dying(target)) return;
        bool cling = Terrain.CrewGrip.Clinging(target);
        var h = new Haul { T = t, By = b, Tid = target, ById = by, Spirit = spirit, Cling = cling };
        if (cling) h.StartMouth = Terrain.Decompression.MouthDist(t.GetTruePosition(), out _);
        if (Hauls.Count == 0) _shipGen = Terrain.GameClock.ShipGen;
        Hauls.Add(h);
        if (!Left.ContainsKey(target)) Left[target] = TerrainDeath.CarryTime;
        TerrainDeath.Hold(target, true);
        Terrain.CrewGrip.Hold(target, true);
        CrewFlip.Stop(target);
        CrewFlip.Play(t, "dragged");
        if (b.AmOwner)
        {
            _localHaul = target;
            if (!spirit) Terrain.CrewGrip.Cancel();
        }
        // 掴む前へ時間を戻すと、手は離れて元の瀕死に戻る
        Rewind.Undo(_ => End(target, false));
        Plugin.Logger.LogInfo($"[Rescue] carry {target} by {by} spirit={spirit} cling={cling} left={Left[target]:0.0}");
    }

    // 全員: 運ぶのをやめる。saved でなければ、その場でもう一度崩れ落ちる (縁ならまたしがみつく) と瀕死の時計が残りから進む
    private static void End(byte target, bool saved)
    {
        for (int i = Hauls.Count - 1; i >= 0; i--)
        {
            var h = Hauls[i];
            if (h.Tid != target) continue;
            Hauls.RemoveAt(i);
            if (_localHaul == target) _localHaul = 255;
            TerrainDeath.Hold(target, false);
            Terrain.CrewGrip.Hold(target, false);
            CrewFlip.Stop(target);
            if (h.Posed) CrewFlip.Stop(h.ById);
            if (saved || !h.T || h.T.Data == null || h.T.Data.IsDead) continue;
            if (h.Cling) Terrain.CrewGrip.Recling(target);
            else TerrainDeath.Recollapse(target);
            Plugin.Logger.LogInfo($"[Rescue] drop {target}");
        }
    }

    // 引きずられる体の連番が止まった (時戻しの再生・他の演出に断られた) 時に流し直す
    private static void KeepDragged()
    {
        if (RewindPlayback.Active) return;
        foreach (var h in Hauls) if (h.T && !CrewFlip.IsPlaying(h.Tid)) CrewFlip.Play(h.T, "dragged");
    }

    // 全員: 生きている運ぶ人は、引きずる人の方を向いて両手で掴み、後ろ向きに下がる姿 (歩いている間は歩く連番・止まると踏ん張る 2 枚)。
    // 他人の体は本編の同期で動くので、速さは位置の変わり方から測る
    private static void PoseCarrier(Haul h, float dt, float now)
    {
        var b = h.By;
        if (!b || b.inVent || dt <= 0f) return;
        Vector2 p = b.GetTruePosition();
        if (h.T)
        {
            float fx = h.T.GetTruePosition().x - p.x;
            if (fx * fx > 0.01f) h.FaceLeft = fx < 0f; // 縦に並んだ時は前の向きのまま
        }
        float mx = p.x - h.ByLast.x, my = p.y - h.ByLast.y;
        bool first = h.NextPose == 0f;
        h.ByLast = p;
        bool moving = !first && mx * mx + my * my > WalkSpeed * WalkSpeed * dt * dt;
        h.StillFor = moving ? 0f : h.StillFor + dt;
        bool walk = moving || (h.Walking && h.StillFor < StillDelay);
        bool playing = h.Posed && CrewFlip.IsPlaying(h.ById);
        if (playing && walk == h.Walking) { CrewFlip.Face(h.ById, h.FaceLeft); return; }
        if (!playing && now < h.NextPose) return; // 断られた (他の演出が体を動かしている) 時は少し待って決め直す
        h.NextPose = now + 0.25f;
        h.Walking = walk;
        h.Posed = CrewFlip.Play(b, walk ? "carry" : "carryidle") || h.Posed;
        CrewFlip.Face(h.ById, h.FaceLeft);
    }

    private static void HostDrop(byte target)
    {
        DropCall.Send(target);
        End(target, false);
    }

    // 全員: 運べる秒を数え、ホストは手が離れたか・助かったかを決める (RoleFrame.Tick から)
    private static void TickHauls()
    {
        if (MeetingHud.Instance || ExileController.Instance) { ClearHauls(); return; }
        bool host = AmongUsClient.Instance && AmongUsClient.Instance.AmHost;
        float dt = Time.deltaTime, now = Time.time;
        for (int i = Hauls.Count - 1; i >= 0; i--)
        {
            if (i >= Hauls.Count) continue;
            var h = Hauls[i];
            float left = Left.TryGetValue(h.Tid, out float l) ? l - dt : 0f;
            Left[h.Tid] = left;
            if (h.Spirit && h.T && now >= h.NextDrift) { h.NextDrift = now + DriftEvery; WardSpiritFx.Drift(h.T); }
            bool gone = !h.T || h.T.Data == null || h.T.Data.IsDead || h.T.Data.Disconnected || !Dying(h.Tid);
            if (gone) { End(h.Tid, true); continue; }
            if (!h.Spirit) PoseCarrier(h, dt, now);
            if (!host) continue;
            bool byGone = !h.By || h.By.Data == null || h.By.Data.Disconnected || h.By.inVent || h.By.Data.IsDead != h.Spirit;
            Vector2 tp = h.T.GetTruePosition(), bp = h.By.GetTruePosition();
            float dx = tp.x - bp.x, dy = tp.y - bp.y;
            if (byGone || left <= 0f || dx * dx + dy * dy > LetGo * LetGo) { HostDrop(h.Tid); continue; }
            bool safe = h.Cling
                ? !Terrain.Decompression.Pulling || Terrain.Decompression.MouthDist(tp, out _) >= h.StartMouth + ClingBack
                : !TerrainDeath.InHazard(h.Tid, tp);
            h.SafeFor = safe ? h.SafeFor + dt : 0f;
            if (h.SafeFor >= SafeHold) HostSave(h.By, h.T, h.Spirit ? BySpirit : ByLiving);
        }
    }

    private static void ClearHauls()
    {
        for (int i = Hauls.Count - 1; i >= 0; i--) { CrewFlip.Stop(Hauls[i].Tid); if (Hauls[i].Posed) CrewFlip.Stop(Hauls[i].ById); TerrainDeath.Hold(Hauls[i].Tid, false); Terrain.CrewGrip.Hold(Hauls[i].Tid, false); }
        Hauls.Clear();
        Left.Clear();
        _localHaul = 255;
    }

    // 物理の刻み (PlayerPhysics.FixedUpdate の後): 運ぶ人は遅く歩き、運ばれる体は自分の端末 (ダミーはホスト) が運ぶ人の後ろへ寄せる。
    // 位置は本編の同期で皆に届く。間に壁があれば寄せない (手が離れる距離までは引っかかったまま)
    internal static void Physics(PlayerPhysics pp)
    {
        var pc = pp.myPlayer;
        if (!pc) return;
        byte id = pc.PlayerId;
        for (int i = 0; i < Hauls.Count; i++)
        {
            var h = Hauls[i];
            if (h.ById == id && pc.AmOwner && pp.body)
            {
                var v = pp.body.velocity;
                float k = h.Spirit && h.Cling ? SpiritClingSpeed : HaulSpeed;
                pp.body.velocity = FxMath.V2(v.x * k, v.y * k);
            }
            if (h.Tid != id || !(pc.AmOwner || (pc.isDummy && AmongUsClient.Instance.AmHost))) continue;
            if (!h.By || pc.inVent || pc.inMovingPlat) continue;
            Vector2 at = h.By.GetTruePosition(), now = pc.GetTruePosition();
            float dx = now.x - at.x, dy = now.y - at.y, len = System.MathF.Sqrt(dx * dx + dy * dy);
            // 運ぶ人の方へ手を伸ばす向き (左右が分かれる程離れている時だけ書く)
            if (dx * dx > 0.01f)
            {
                bool left = dx > 0f;
                if (h.Flip != (left ? 1 : 0) && pc.cosmetics) { pc.cosmetics.SetFlipX(left); h.Flip = (sbyte)(left ? 1 : 0); }
            }
            if (len <= Gap) continue;
            Vector2 want = len > Snap ? at : FxMath.V2(at.x + dx / len * Gap, at.y + dy / len * Gap);
            // 壁の向こうへは引けない (縁にしがみついている体は手の先まで)
            if (len <= Snap && Blocked(at, now, h.Cling)) continue;
            if (pp.body) pp.body.velocity = FxMath.V2(0f, 0f);
            var tp = pc.transform.position;
            pc.NetTransform.SnapTo(FxMath.V2(want.x + tp.x - now.x, want.y + tp.y - now.y));
        }
    }

    // ── 自分の端末: 近くに瀕死の人がいる間だけボタンを出す (RoleFrame.Tick から) ──────
    internal static void Tick()
    {
        if (Hauls.Count > 0)
        {
            // 船が替わった (次の試合・ロビーへ戻った): 前の試合の組を捨てる
            if (_shipGen != Terrain.GameClock.ShipGen) ClearHauls();
            else TickHauls();
        }
        if (++_check < CheckEvery) return;
        _check = 0;
        if (Left.Count > 0) ForgetSpent();
        if (Hauls.Count > 0) KeepDragged();
        var lp = PlayerControl.LocalPlayer;
        bool alive = lp && lp.Data != null && !lp.Data.IsDead; // 守り霊は能力のボタンで運ぶので、生きている人のボタンは出さない
        bool show = alive && (LocalHauling || Nearest(Near) != null);
        if (!show)
        {
            if (_life != null) { _life.Release(); _life = null; _button = null; }
            return;
        }
        if (_life == null || _life.IsDead)
        {
            if (!Vanilla.Hud) return;
            _life = RoleState.Match.Child();
            _button = ModButton.Create(_life, ButtonIcons.Get("rescue_pull"), new Text("Rescue.Pull"), 0f, () => Nearest(Reach) != null);
            _button?.SetHold(() => 0f, () => HoldTime, Released, new Text("Ability.Hold"));
        }
        if (_button == null) return;
        _button.SetUsable(!LocalHauling && Nearest(Reach) != null);
        // 運んでいる間は運べる残りの秒を見せる
        float left = LocalHauling && Left.TryGetValue(_localHaul, out float l) ? FxMath.Max(0f, l) : -1f;
        if (left >= 0f) _button.ShowGauge(left, TerrainDeath.CarryTime, left.ToString("0.0"));
        else _button.ShowGauge(0f, 0f, null);
    }

    // 瀕死が終わった人 (助かった・死んだ・会議) の運べる残りの秒を消す (次に瀕死になった時はまた満タンから)
    private static readonly List<byte> Stale = new();
    private static void ForgetSpent()
    {
        Stale.Clear();
        foreach (var id in Left.Keys) if (!Dying(id) && !Carried(id)) Stale.Add(id);
        foreach (var id in Stale) Left.Remove(id);
    }

    private static bool Released(float held)
    {
        if (held < HoldTime - 0.05f || LocalHauling) return false;
        var t = Nearest(Reach);
        if (!t) return false;
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost)
        {
            string why = Refuse(PlayerControl.LocalPlayer, t);
            if (why != null) return false;
            HostBegin(PlayerControl.LocalPlayer, t);
        }
        else Request.Send(t.PlayerId);
        return true;
    }

    // 自分から r 以内の一番近い、まだ誰にも運ばれていない瀕死の人 (自分が生きていて動ける時だけ)
    private static PlayerControl Nearest(float r)
    {
        if (!TerrainDeath.AnyDoomed && !Terrain.CrewGrip.AnyClinging) return null; // 誰も瀕死でない間は Unity に触らない
        if (!ShipStatus.Instance || MeetingHud.Instance) return null;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead || lp.inVent || Dying(lp.PlayerId)) return null;
        Vector2 me = lp.GetTruePosition();
        PlayerControl best = null;
        float bd = r * r;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p == lp || p.Data == null || p.Data.IsDead || !Dying(p.PlayerId) || Carried(p.PlayerId)) continue;
            if (Left.TryGetValue(p.PlayerId, out float left) && left <= 0f) continue;
            Vector2 q = p.GetTruePosition();
            float dx = q.x - me.x, dy = q.y - me.y, d = dx * dx + dy * dy;
            if (d < bd) { bd = d; best = p; }
        }
        return best;
    }
}

// 運ぶ人の速さと引きずられる体の位置は、吸い出しの流れ (CrewPull) を足す前に決める
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
[HarmonyPriority(Priority.High)]
internal static class RescueHaulPatch
{
    public static void Postfix(PlayerPhysics __instance)
    {
        if (!Rescue.AnyHaul) return;
        Rescue.Physics(__instance);
    }
}
