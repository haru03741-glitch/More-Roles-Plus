using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 瀕死の人 (地形で崩れ落ちた人・外壁の穴の縁にしがみついている人) を助け出す。
// 近くの生きている人が長押しするか守り霊が「救う」と、その人を掴んで引きずって運ぶ。運んでいる間は瀕死の時計が止まり、
// 運べる秒には限りがある (瀕死 1 回につき合計)。火の外・足の着く水まで運ぶか、縁から内側へ引き戻せたら助かる。
// 守り霊は溺れかけ・焼けかけの人 (まだ瀕死でない) も宙に浮かせて運べる。浮かせた分だけ体が水から出るので、
// 浅い所なら運んでいる間も息が戻り、瀕死の人は息が戻り始めた所で瀕死から抜ける。火は熱が上へ昇るので、浮かせても火の外でしか戻らない。
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
    private const float Gap = 0.85f;      // 運ぶ人と引きずられる体の間 (這う姿は横に長い)・横に並べられない時 (間に壁) の紐の長さ
    private const float HoldX = 0.78f;    // 引きずられる体を置く所: 運ぶ人の両手が頭の前にかかる横の間
    private const float HoldY = 0.04f;    // 少し奥 (上) に置いて、運ぶ人の手が体の手前にかぶさるようにする
    private const float UnderGap = 0.0003f; // 運ぶ人の手の後ろの層を引きずる人の体よりこれだけ奥へ
    private const float HoldPull = 4f;    // 横の置き場へ寄せる速さの上限 (単位/秒・掴んだ瞬間に跳ばない)
    private const float Snap = 2.5f;      // これより離れたら (はしご・会議の後) 一気に寄せる
    private const float LetGo = 2.2f;     // ホスト: 壁に引っかかってこれより離れたら手が離れる
    private const float SafeHold = 0.4f;  // 安全な所にこの秒いたら助かる
    private const float WalkSpeed = 0.3f; // 運ぶ人がこれより速く動いていれば歩く姿 (単位/秒)
    private const float StillDelay = 0.15f; // 止まってからこの秒で止まった姿へ (歩きの切れ目でちらつかせない)
    private const float ClingBack = 0.8f; // 縁から内側へこれだけ引き戻せたら助かる
    private const float DriftEvery = 0.1f;
    private const float FloatLift = 0.35f;  // 守り霊に運ばれる体を浮かせる高さ (絵の単位)
    private const float FloatRise = 0.5f;   // 浮き上がるまでの秒
    private const string FloatClip = "floatcarried", DragClip = "dragged", SpiritClip = "ghostcarry";
    private const float SpiritHoldX = 0.6f;  // 守り霊は浮かせた人を胸の前で抱えるので、生きている人が引きずる時より近い
    // 浮かせた高さ (絵の単位 0.35 = 体の高さの約 3 分の 1) を水の深さに直した値。足の着かない深さよりこれだけ浅ければ頭が水から出る
    internal const float LiftDepth = 0.25f;

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
        public float StartMouth, SafeFor, OutFor, NextDrift, Born, LiftK = -1f; // LiftK = 絵の単位 1 が世界の何単位か (浮かせる体の光を合わせる)
        public Vector2 ByLast;
        public float StillFor, NextPose;
        public bool Walking, Posed, FaceLeft, HeadLeft;
        public sbyte Side;                      // 引きずられる体が運ぶ人のどちら側か (1 = 右・-1 = 左・0 = まだ)
    }
    private static readonly List<Haul> Hauls = new();
    private static readonly Dictionary<byte, float> Left = new(); // 運べる残りの秒 (瀕死 1 回につき)
    private static byte _localHaul = 255;
    internal static byte LocalCarried = 255; // 自分が運ばれている間 (瀕死から抜けても自分では動けない・PortFreezePatch が読む)
    private static int _shipGen = -1;

    internal static bool LocalHauling => _localHaul != 255;
    internal static bool AnyHaul => Hauls.Count > 0;

    // 助け出せる状態か (全員の端末で同じ答えになる物だけを見る)
    internal static bool Dying(byte id) => TerrainDeath.IsDying(id) || Terrain.CrewGrip.Clinging(id);

    // 守り霊が浮かせられる、まだ瀕死でない人: 足の着かない水の中 (泳ぎが得意な人は除く) か火の中にいる。
    // 水と火の広がりは全員の端末で同じなので、どの端末でも同じ答えになる
    internal static bool Endangered(byte id)
    {
        if (!TerrainDeath.Enabled || MeetingHud.Instance || ExileController.Instance) return false;
        bool fire = Terrain.FireSim.Ready && Terrain.FireSim.BurningCount > 0, water = Terrain.WaterSim.Running && Terrain.WaterSim.Ready;
        if (!fire && !water) return false;
        var p = PortKit.ById(id);
        if (!PortKit.Alive(p) || p.inVent || p.onLadder || p.inMovingPlat || TerrainDeath.IsDoomed(id)) return false;
        var pos = p.GetTruePosition();
        return (fire && TerrainDeath.OnFire(pos))
            || (water && !TerrainDeath.NoDrown(id) && Terrain.WaterSim.CurrentAt(pos, out _, out _) >= Terrain.CrewSwim.BraceMaxDepth);
    }

    // 守り霊に浮かせられている人の体が水面から上がった分 (水の深さの単位)。浮き上がる途中は少しずつ
    internal static float LiftDepthOf(byte id)
    {
        if (Hauls.Count == 0) return 0f;
        foreach (var h in Hauls)
        {
            if (h.Tid != id || !h.Spirit || h.Cling) continue;
            float u = (Time.time - h.Born) / FloatRise;
            return u >= 1f ? LiftDepth : u <= 0f ? 0f : u * LiftDepth;
        }
        return 0f;
    }

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

    // 守り霊に浮かせられた瀕死の人の頭が水から出た (火の外へ出た): 瀕死から抜けて運ばれ続ける
    private static readonly RemoteCall<byte> ReviveCall = new("Rescue.Revive", Route.HostToAll,
        (w, m) => w.Write(m), r => r.ReadByte(), (_, m) => TerrainDeath.Revive(m));

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

    // 守り霊が狙える瀕死の人・溺れかけ/焼けかけの人: まだ誰にも運ばれておらず、運べる秒が残っていて、守り霊が今は誰も運んでいない
    internal static bool SpiritCanTake(byte spirit, byte target) =>
        (Dying(target) || Endangered(target)) && !Carried(target) && !Hauling(spirit) && !(Left.TryGetValue(target, out float l) && l <= 0f);

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
        // 守り霊はホストが確かめてから呼ばれるので、瀕死でない (溺れかけの) 人もそのまま運ぶ
        if (!t || !b || t.Data == null || t.Data.IsDead || Carried(target) || Hauling(by) || (!spirit && !Dying(target))) return;
        bool cling = Terrain.CrewGrip.Clinging(target);
        var h = new Haul { T = t, By = b, Tid = target, ById = by, Spirit = spirit, Cling = cling, Born = Time.time };
        if (cling) h.StartMouth = Terrain.Decompression.MouthDist(t.GetTruePosition(), out _);
        if (Hauls.Count == 0) _shipGen = Terrain.GameClock.ShipGen;
        Hauls.Add(h);
        if (!Left.ContainsKey(target)) Left[target] = TerrainDeath.CarryTime;
        TerrainDeath.Hold(target, true);
        Terrain.CrewGrip.Hold(target, true);
        CrewFlip.Stop(target);
        // 守り霊は生きている人には見えないので、見えない手に抱えられて宙に浮き、力の抜けた姿で運ばれる
        CrewFlip.Play(t, spirit ? FloatClip : DragClip);
        if (t.AmOwner) LocalCarried = target;
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
            if (LocalCarried == target) LocalCarried = 255;
            TerrainDeath.Hold(target, false);
            Terrain.CrewGrip.Hold(target, false);
            CrewFlip.Stop(target);
            if (h.Posed) CrewFlip.Stop(h.ById);
            if (saved || !h.T || h.T.Data == null || h.T.Data.IsDead) continue;
            // 瀕死でない (浮かせている間に瀕死から抜けた) 人は放されるとその場に戻るだけ。運べる秒は次の瀕死のために数え直す
            if (!Dying(target)) { Left.Remove(target); Plugin.Logger.LogInfo($"[Rescue] release {target}"); continue; }
            if (h.Cling) Terrain.CrewGrip.Recling(target);
            else TerrainDeath.Recollapse(target);
            Plugin.Logger.LogInfo($"[Rescue] drop {target}");
        }
    }

    // 引きずられる体の連番が止まった (時戻しの再生・他の演出に断られた) 時に流し直す
    private static void KeepDragged()
    {
        if (RewindPlayback.Active) return;
        foreach (var h in Hauls) if (h.T && !CrewFlip.IsPlaying(h.Tid)) CrewFlip.Play(h.T, h.Spirit ? FloatClip : DragClip);
    }

    // 全員: 守り霊に運ばれる体はゆっくり浮き上がり、浮いたまま淡い光に包まれる
    private static void Float(Haul h, float now)
    {
        if (!h.T) return;
        float u = (now - h.Born) / FloatRise;
        float rise = u >= 1f ? 1f : u * u * (3f - 2f * u);
        // 浮いた高さは揺らさない: 守り霊の握る手は止まっているので、頭が手の中から動かないように
        float lift = rise * FloatLift;
        lift = (int)(lift * 200f + 0.5f) * 0.005f; // 0.5 px 刻み: 見えない差では体の位置を書き直さない
        CrewFlip.Lift(h.Tid, lift);
        // 頭を守り霊の方へ向ける (体は本編の同期で動くので、歩く向きに任せると逆を向くことがある)
        if (h.By)
        {
            float fx = h.By.GetTruePosition().x - h.T.GetTruePosition().x;
            if (fx * fx > 0.01f) h.HeadLeft = fx < 0f;
            CrewFlip.Face(h.Tid, h.HeadLeft);
        }
        if (h.LiftK < 0f)
        {
            var body = h.T.cosmetics && h.T.cosmetics.currentBodySprite != null ? h.T.cosmetics.currentBodySprite.BodySprite : null;
            if (body) h.LiftK = body.transform.lossyScale.y;
        }
        if (h.LiftK > 0f && h.T.Visible) WardSpiritFx.Hold(h.T, lift * h.LiftK);
    }

    // 全員: 守り霊は浮かせた人の方を向いて両手で抱える (幽霊の画面にだけ見える)。歩く/止まるで絵は変えない (幽霊は滑って動く)
    private static void PoseSpirit(Haul h, float now)
    {
        var b = h.By;
        if (!b || !h.T) return;
        float fx = h.T.GetTruePosition().x - b.GetTruePosition().x;
        if (fx * fx > 0.01f) h.FaceLeft = fx < 0f;
        if (!(h.Posed && CrewFlip.IsPlaying(h.ById)))
        {
            if (now < h.NextPose) return;
            h.NextPose = now + 0.25f;
            h.Posed = CrewFlip.Play(b, SpiritClip, ghost: true) || h.Posed;
        }
        Grip(h);
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
        if (playing && walk == h.Walking) { Grip(h); return; }
        if (!playing && now < h.NextPose) return; // 断られた (他の演出が体を動かしている) 時は少し待って決め直す
        h.NextPose = now + 0.25f;
        h.Walking = walk;
        h.Posed = CrewFlip.Play(b, walk ? "carry" : "carryidle") || h.Posed;
        Grip(h);
    }

    // 運ぶ人を引きずる人の方へ向け、頭の向こう側へ回った手 (後ろの層) を引きずる人の体のすぐ奥に置く
    private static void Grip(Haul h)
    {
        CrewFlip.Face(h.ById, h.FaceLeft);
        var cos = h.T ? h.T.cosmetics : null;
        var body = cos != null && cos.currentBodySprite != null ? cos.currentBodySprite.BodySprite : null;
        if (body) CrewFlip.UnderAt(h.ById, body.transform.position.z + UnderGap);
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
            // 守り霊が浮かせている人は瀕死でなくても運び続ける (溺れかけ・瀕死から抜けた後)
            bool gone = !h.T || h.T.Data == null || h.T.Data.IsDead || h.T.Data.Disconnected || (!(h.Spirit && !h.Cling) && !Dying(h.Tid));
            if (gone) { End(h.Tid, true); continue; }
            // 時戻しの再生中は体の連番が止められているので、流し直しも光も出さない (再生が終われば次の刻みで戻る)
            if (!RewindPlayback.Active)
            {
                if (!h.Spirit) PoseCarrier(h, dt, now);
                else { Float(h, now); PoseSpirit(h, now); }
            }
            if (!host) continue;
            bool byGone = !h.By || h.By.Data == null || h.By.Data.Disconnected || h.By.inVent || h.By.Data.IsDead != h.Spirit;
            Vector2 tp = h.T.GetTruePosition(), bp = h.By.GetTruePosition();
            float dx = tp.x - bp.x, dy = tp.y - bp.y;
            if (byGone || left <= 0f || dx * dx + dy * dy > LetGo * LetGo) { HostDrop(h.Tid); continue; }
            // 足の着かない水の上でも、浮かせた体の頭が水から出ていれば息が戻り始め、少し続けば瀕死から抜けて運ばれ続ける。
            // 足の着く所まで来ていれば下の助かる判定へ (起き上がって息が全部戻る)
            if (h.Spirit && !h.Cling && Dying(h.Tid) && TerrainDeath.InHazard(h.Tid, tp))
            {
                h.OutFor = TerrainDeath.InHazard(h.Tid, tp, LiftDepthOf(h.Tid)) ? 0f : h.OutFor + dt;
                if (h.OutFor >= SafeHold) { h.OutFor = 0f; ReviveCall.Send(h.Tid); TerrainDeath.Revive(h.Tid); }
                h.SafeFor = 0f;
                continue;
            }
            h.OutFor = 0f;
            bool safe = h.Cling
                ? !Terrain.Decompression.Pulling || Terrain.Decompression.MouthDist(tp, out _) >= h.StartMouth + ClingBack
                : h.Spirit && !Dying(h.Tid) ? TerrainDeath.SafeGround(tp) : !TerrainDeath.InHazard(h.Tid, tp);
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
        LocalCarried = 255;
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
            if (dx * dx > 0.04f || h.Side == 0) h.Side = (sbyte)(dx >= 0f ? 1 : -1);
            bool left = h.Side > 0;
            if (h.Flip != (left ? 1 : 0) && pc.cosmetics) { pc.cosmetics.SetFlipX(left); h.Flip = (sbyte)(left ? 1 : 0); }
            // 運ぶ人の手の先に頭が来るように横に並べる。間に壁があれば紐の長さで後ろを付いて来る (壁の向こうへは引けない)。
            // 縁から引き戻す時は気圧に逆らって引くので紐のまま
            Vector2 want;
            if (len > Snap) want = at;
            else if (h.Cling)
            {
                if (len <= Gap || Blocked(at, now, true)) continue;
                want = FxMath.V2(at.x + dx / len * Gap, at.y + dy / len * Gap);
            }
            else
            {
                want = FxMath.V2(at.x + h.Side * (h.Spirit ? SpiritHoldX : HoldX), at.y + HoldY);
                float wx = want.x - now.x, wy = want.y - now.y;
                if (wx * wx + wy * wy < 0.0004f) continue;
                if (Blocked(at, want, h.Cling))
                {
                    if (len <= Gap || Blocked(at, now, false)) continue;
                    want = FxMath.V2(at.x + dx / len * Gap, at.y + dy / len * Gap);
                }
                else
                {
                    float wl = System.MathF.Sqrt(wx * wx + wy * wy), step = HoldPull * Time.fixedDeltaTime;
                    if (wl > step) want = FxMath.V2(now.x + wx / wl * step, now.y + wy / wl * step);
                }
            }
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
            // 船が替わった (次の試合・ロビーへ戻った): 前の試合の組を捨てる (ロビーには船が無い)
            if (_shipGen != Terrain.GameClock.ShipGen || !ShipStatus.Instance) ClearHauls();
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
