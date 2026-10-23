using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using PowerTools;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 時戻しの逆再生で、全員の体を見ていたとおりに逆へ動かす。
// 各端末が自分の画面に映った全員の姿 (位置・向き・体と着せ替えのアニメの絵と時間・生き死に) を 0.1 秒ごとに輪へ控え、
// 逆再生の間は本編の歩きのアニメ (PlayerPhysics.HandleAnimation) と位置の同期 (CustomNetworkTransform) を止めて、控えを新しい順にたどって置く。
// 向きは控えた時のまま (後ろ向きに歩いて戻る)・アニメの時間は減る向きに進むので、歩いた映像を逆に流したように見える。
// 位置は各端末が自分の控えから置くので電文は使わない。終わりに持ち主が自分の位置を配り直して揃える (TimeRewind.Finish)。
// 控えるのは時戻しのいる試合だけ。輪の配列は人ごとに最初の 1 回だけ確保する
internal static class RewindPlayback
{
    internal const int StepMs = 100;
    private const int Slots = 260; // 26 秒分 (戻す秒数の上限 15 + 押してから依頼までの上限 8 + 余白)

    private const byte FSeen = 1, FFlip = 2, FDead = 4, FHold = 8;

    private enum Kind : byte { None, Run, Idle, Ghost, EnterVent, ExitVent, Spawn, ClimbUp, ClimbDown, Angel }

    private sealed class Track
    {
        public byte Id;
        public IntPtr PcPtr;
        public PlayerControl Pc;
        public Rigidbody2D Body;
        public CosmeticsLayer Cos;
        public PlayerAnimations Anims;
        public SpriteAnim Anim, Skin;
        public IntPtr PhysPtr, NtPtr;
        public int Refresh;
        public readonly float[] X = new float[Slots], Y = new float[Slots], T = new float[Slots];
        public readonly IntPtr[] Clip = new IntPtr[Slots];
        public readonly byte[] F = new byte[Slots];
        // 逆再生の間
        public bool Driving, Flip;
        public Kind Playing;
        public IntPtr PlayingClip;
    }

    private static readonly Track[] Tracks = new Track[256];
    private static readonly long[] SlotMs = new long[Slots];
    private static int _head, _count;

    // 逆再生の間 (本編のアニメと位置の同期を止めている間)
    private static bool _active;
    private static long _from;  // 逆再生を始めた時刻 (これより新しい控えは使わない)
    private static int _driven;

    internal static bool Active => _active;
    internal static int Count => _count;

    internal static void Clear()
    {
        if (_active) Stop();
        _head = _count = 0;
        foreach (var t in Tracks)
        {
            if (t == null) continue;
            t.PcPtr = IntPtr.Zero;
            t.Pc = null;
            t.Body = null;
            t.Cos = null;
            t.Anims = null;
            t.Anim = t.Skin = null;
        }
    }

    // ---- 控える ----

    internal static void Capture(long now)
    {
        int s = _head;
        SlotMs[s] = now;
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < Tracks.Length; i++)
            if (Tracks[i] != null) Tracks[i].F[s] = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc) continue;
            var t = TrackOf(pc);
            if (t == null) continue;
            try { CaptureOne(t, s); }
            catch (Exception e)
            {
                t.F[s] = 0;
                t.Refresh = 0;
                Plugin.Logger.LogWarning($"RewindPlayback capture {t.Id}: {e.Message}");
            }
        }
        _head = (_head + 1) % Slots;
        if (_count < Slots) _count++;
    }

    private static Track TrackOf(PlayerControl pc)
    {
        byte id = pc.PlayerId;
        var t = Tracks[id] ??= new Track { Id = id, Refresh = id % 10 }; // 引き直しを人ごとにずらす
        if (t.PcPtr != pc.Pointer)
        {
            // 別の人がこの番号に入った: 前の人の控えは使わない
            Array.Clear(t.F, 0, Slots);
            t.PcPtr = pc.Pointer;
            t.Pc = pc;
            t.Refresh = 0;
        }
        if (--t.Refresh <= 0 && !Resolve(t)) { t.Refresh = 0; return null; }
        return t.Body && t.Anim ? t : null;
    }

    // 体・アニメ・着せ替えの包みを引き直す (体の絵は姿が替わると別の物になるので 1 秒ごと)
    private static bool Resolve(Track t)
    {
        t.Refresh = 1000 / StepMs;
        var pc = t.Pc;
        var phys = pc.MyPhysics;
        if (!phys) return false;
        t.Body = phys.body;
        t.Anims = phys.Animations;
        t.PhysPtr = phys.Pointer;
        t.NtPtr = pc.NetTransform ? pc.NetTransform.Pointer : IntPtr.Zero;
        t.Cos = pc.cosmetics;
        var bodySprite = t.Cos ? t.Cos.currentBodySprite : null;
        var bsr = bodySprite != null ? bodySprite.BodySprite : null;
        t.Anim = bsr ? bsr.GetComponent<SpriteAnim>() : null;
        var skin = t.Cos ? t.Cos.skin : null;
        t.Skin = skin ? skin.animator : null;
        return t.Body && t.Anim;
    }

    private static void CaptureOne(Track t, int s)
    {
        var pc = t.Pc;
        var p = t.Body.position;
        t.X[s] = p.x;
        t.Y[s] = p.y;
        t.Clip[s] = CurrClip(t.Anim);
        t.T[s] = t.Anim.GetNormalisedTime();
        byte f = FSeen;
        if (t.Cos && t.Cos.FlipX) f |= FFlip;
        var data = pc.Data;
        if (data != null && data.IsDead) f |= FDead;
        if (pc.inVent || pc.onLadder || pc.inMovingPlat) f |= FHold;
        t.F[s] = f;
    }

    // 体のアニメが今流している絵 (SpriteAnim の m_currAnim)。包みを作らずに欄を直に読む
    private static int _currAt = -2;

    private static unsafe IntPtr CurrClip(SpriteAnim a)
    {
        if (_currAt == -2)
        {
            _currAt = -1;
            IntPtr fi = IL2CPP.il2cpp_class_get_field_from_name(IL2CPP.il2cpp_object_get_class(a.Pointer), "m_currAnim");
            if (fi != IntPtr.Zero) _currAt = (int)IL2CPP.il2cpp_field_get_offset(fi);
            else Plugin.Logger.LogWarning("RewindPlayback: no m_currAnim on SpriteAnim");
        }
        return _currAt < 0 ? IntPtr.Zero : *(IntPtr*)((byte*)a.Pointer + _currAt);
    }

    // ---- 逆にたどる ----

    // 逆再生の始め: 今の姿を最後の控えにして、今はしご・昇降機・ベントの中にいない人を止める
    internal static void Begin(long now)
    {
        if (_active) Stop();
        Capture(now);
        _from = now;
        _active = true;
        _driven = 0;
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc) continue;
            var t = Tracks[pc.PlayerId];
            if (t == null || t.PcPtr != pc.Pointer || !t.Body || !t.Anim) continue;
            if (pc.inVent || pc.onLadder || pc.inMovingPlat) continue;
            t.Driving = true;
            t.Flip = t.Cos && t.Cos.FlipX;
            t.PlayingClip = IntPtr.Zero; // 最初の控えの絵を流し直してから時間を置く
            t.Playing = Kind.None;
            t.Anim.Paused = true;
            if (t.Skin) t.Skin.Paused = true;
            _driven++;
        }
    }

    // 毎 FixedUpdate: 記録の時刻 at の姿へ全員を置く
    internal static void Drive(long at)
    {
        if (!_active) return;
        Bracket(at, out int a, out int b, out float u);
        if (a < 0) return;
        foreach (var t in Tracks)
        {
            if (t == null || !t.Driving) continue;
            try { DriveOne(t, a, b, u); }
            catch (Exception e)
            {
                Release(t);
                Plugin.Logger.LogWarning($"RewindPlayback drive {t.Id}: {e.Message}");
            }
        }
    }

    private static void DriveOne(Track t, int a, int b, float u)
    {
        if (!t.Pc || !t.Body) { t.Driving = false; return; }
        // 生き返った時などに体の絵が作り直されたら引き直して止め直す
        if (!t.Anim)
        {
            if (!Resolve(t)) return;
            t.Anim.Paused = true;
            if (t.Skin) t.Skin.Paused = true;
            t.PlayingClip = IntPtr.Zero;
        }
        byte fa = t.F[a], fb = t.F[b];
        if ((fa & FSeen) == 0 || (fb & FSeen) == 0) return;
        // ベント・はしご・昇降機の途中だった所は本編の動きが位置を持っていたので、その間は動かさない
        if (((fa | fb) & FHold) != 0) return;
        float x = t.X[a] + (t.X[b] - t.X[a]) * u, y = t.Y[a] + (t.Y[b] - t.Y[a]) * u;
        t.Body.velocity = Fx.FxMath.V2(0f, 0f);
        t.Body.position = Fx.FxMath.V2(x, y);
        // 向きと絵は近い方の控えのもの
        int n = u < 0.5f ? a : b;
        bool flip = (t.F[n] & FFlip) != 0;
        if (flip != t.Flip && t.Cos)
        {
            t.Flip = flip;
            t.Cos.SetFlipX(flip);
        }
        IntPtr clip = t.Clip[n];
        if (clip != t.PlayingClip) Switch(t, clip, flip);
        if (t.Playing == Kind.None) return;
        float time = AnimTime(t, a, b, u);
        t.Anim.SetNormalizedTime(time);
        if (t.Skin) t.Skin.SetNormalizedTime(time);
    }

    // 2 つの控えの間のアニメの時間 (同じ絵の間だけ補う。輪になる絵は 1 を越えて巻き戻った分を足して進める)
    private static float AnimTime(Track t, int a, int b, float u)
    {
        float ta = t.T[a], tb = t.T[b];
        if (t.Clip[a] != t.Clip[b]) return u < 0.5f ? ta - MathF.Floor(ta) : tb - MathF.Floor(tb);
        float d = tb - ta;
        if (d < 0f) d -= MathF.Floor(d);
        float v = ta + d * u;
        return v > 1f ? v - MathF.Floor(v) : v;
    }

    // 控えた絵へ切り替える。本人の今の体の型の絵のどれかに当たる時だけ流す (当たらない絵は消えた物かもしれないので触らない)
    private static void Switch(Track t, IntPtr clip, bool flip)
    {
        t.PlayingClip = clip;
        t.Playing = Kind.None;
        if (clip == IntPtr.Zero || !t.Anims) return;
        var g = t.Anims.group;
        if (g == null) return;
        AnimationClip hit = null;
        Kind kind = Kind.None;
        Match(g.RunAnim, Kind.Run, clip, ref hit, ref kind);
        Match(g.IdleAnim, Kind.Idle, clip, ref hit, ref kind);
        Match(g.GhostIdleAnim, Kind.Ghost, clip, ref hit, ref kind);
        Match(g.EnterVentAnim, Kind.EnterVent, clip, ref hit, ref kind);
        Match(g.ExitVentAnim, Kind.ExitVent, clip, ref hit, ref kind);
        Match(g.SpawnAnim, Kind.Spawn, clip, ref hit, ref kind);
        Match(g.ClimbUpAnim, Kind.ClimbUp, clip, ref hit, ref kind);
        Match(g.ClimbDownAnim, Kind.ClimbDown, clip, ref hit, ref kind);
        Match(g.GhostGuardianAngelAnim, Kind.Angel, clip, ref hit, ref kind);
        if (hit == null) return;
        t.Anim.Play(hit, 1f);
        t.Anim.Paused = true;
        t.Playing = kind;
        var skin = t.Cos ? t.Cos.skin : null;
        if (!skin) return;
        switch (kind)
        {
            case Kind.Run: skin.SetRun(flip); break;
            case Kind.Idle: skin.SetIdle(flip); break;
            case Kind.Ghost: case Kind.Angel: skin.SetGhost(); break;
            case Kind.EnterVent: skin.SetEnterVent(flip); break;
            case Kind.ExitVent: skin.SetExitVent(flip); break;
            case Kind.Spawn: skin.SetSpawn(flip, 0f); break;
            case Kind.ClimbUp: skin.SetClimb(false); break;
            case Kind.ClimbDown: skin.SetClimb(true); break;
        }
        if (t.Skin) t.Skin.Paused = true;
    }

    private static void Match(AnimationClip c, Kind k, IntPtr want, ref AnimationClip hit, ref Kind kind)
    {
        if (hit != null || c == null || c.Pointer != want) return;
        hit = c;
        kind = k;
    }

    // at の前後の控え (a が古い側・u は a→b の割合)。逆再生を始めた後の控えは使わない。無ければ a = -1
    private static void Bracket(long at, out int a, out int b, out float u)
    {
        a = b = -1;
        u = 0f;
        int newer = -1;
        for (int k = 1; k <= _count; k++)
        {
            int s = (_head - k + Slots) % Slots;
            if (SlotMs[s] > _from) continue;
            if (SlotMs[s] <= at)
            {
                a = s;
                b = newer >= 0 ? newer : s;
                long span = SlotMs[b] - SlotMs[a];
                u = span > 0 ? (at - SlotMs[a]) / (float)span : 0f;
                return;
            }
            newer = s;
        }
        if (newer >= 0) a = b = newer; // 控えより前: 一番古い姿
    }

    // その人の生き死にを記録の時刻 at で (控えが無ければ null)
    internal static bool? DeadAt(byte id, long at)
    {
        var t = Tracks[id];
        if (t == null) return null;
        Bracket(at, out int a, out _, out _);
        if (a < 0 || (t.F[a] & FSeen) == 0) return null;
        return (t.F[a] & FDead) != 0;
    }

    // 逆再生の終わり: 止めていた本編のアニメと位置の同期を戻す (位置の揃え直しは持ち主の Teleport)
    internal static void Stop()
    {
        _active = false;
        foreach (var t in Tracks)
            if (t != null && t.Driving) Release(t);
    }

    private static void Release(Track t)
    {
        t.Driving = false;
        try
        {
            if (t.Anim) t.Anim.Paused = false;
            if (t.Skin) t.Skin.Paused = false;
            // 止めていた間に届いた位置を捨てる (届いた順に歩き直さない)
            if (t.Pc && !t.Pc.AmOwner && t.Pc.NetTransform) t.Pc.NetTransform.ClearPositionQueues();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"RewindPlayback release {t.Id}: {e.Message}"); }
    }

    // 戻した時刻より後の控えは無かったことになる
    internal static void Forget(long afterMs)
    {
        while (_count > 0)
        {
            int s = (_head - 1 + Slots) % Slots;
            if (SlotMs[s] <= afterMs) break;
            _head = s;
            _count--;
        }
    }

    // その体を今こちらが動かしているか (本編の処理を止める判定)
    internal static int AnimHits, NetHits; // 確かめ用: 本編の処理を止めた回数

    internal static bool Holds(IntPtr ptr, bool physics)
    {
        foreach (var t in Tracks)
            if (t != null && t.Driving && (physics ? t.PhysPtr : t.NtPtr) == ptr) return true;
        return false;
    }

    internal static string Probe()
    {
        var sb = new System.Text.StringBuilder($"active={_active} samples={_count} driven={_driven} animSkips={AnimHits} netSkips={NetHits}");
        foreach (var t in Tracks)
        {
            if (t == null || !t.Pc) continue;
            sb.Append($" | {t.Id} drive={t.Driving} kind={t.Playing} flip={t.Flip} now={(t.Cos ? t.Cos.FlipX : false)}");
            if (t.Body) sb.Append($" v={t.Body.velocity.x:0.00},{t.Body.velocity.y:0.00}");
            if (t.Anim) sb.Append($" t={t.Anim.GetNormalisedTime():0.00} paused={t.Anim.Paused}");
        }
        return sb.ToString();
    }
}

// 逆再生の間は本編が速さから歩き/止まりと向きを決め直さないようにする
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.HandleAnimation))]
internal static class RewindPlaybackAnim
{
    public static bool Prefix(PlayerPhysics __instance)
    {
        if (!RewindPlayback.Active || !RewindPlayback.Holds(__instance.Pointer, true)) return true;
        RewindPlayback.AnimHits++;
        return false;
    }
}

// 逆再生の間は位置の同期で体を動かさない (持ち主は送らない・他の人の体は届いた位置へ寄せない)
[HarmonyPatch(typeof(CustomNetworkTransform), nameof(CustomNetworkTransform.FixedUpdate))]
internal static class RewindPlaybackNet
{
    public static bool Prefix(CustomNetworkTransform __instance)
    {
        if (!RewindPlayback.Active || !RewindPlayback.Holds(__instance.Pointer, false)) return true;
        RewindPlayback.NetHits++;
        return false;
    }
}
