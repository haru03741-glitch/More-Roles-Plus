using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 壊れる瞬間の動き (崩れ落ちる塊・飛ぶ破片・土煙・火花・爆発の閃光)。
// 塊と破片は止まったらそのまま瓦礫として残し、土煙・火花・閃光は消える。
// 乱数は損傷イベントの種から作るので、全員が同じ散らばり方になる。
internal static class TerrainFx
{
    private enum Kind { Fall, Fly, Puff, Spark, Flash }

    private sealed class Item
    {
        public Kind Kind;
        public Transform Tr;
        public SpriteRenderer Sr;
        public float T, Life;
        public Vector2 Pos, Vel;   // 床の上の位置と速さ
        public float Height, VH;   // 床からの高さ (3/4 視点なので画面では上にずれて見える)
        public float Rot, VRot;
        public float S0, S1, Z;
    }

    private static readonly List<Item> Items = new();
    private static Sprite _flash;
    private static bool _flashSearched;
    private static long _lastMs;

    private const float Gravity = 9f;

    // ── 発生 ───────────────────────────────────────────────────────────

    // 打撃で壁が崩れる: 壁の区間に沿って塊が上から落ちて山になり、根元に土煙。
    // axis = 抜けた向き (振った向き)・force = 振りの強さ。強いほど山が向こう側へ押し出される
    public static void Crumble(Vector2 center, Vector2 tangent, Vector2 normal, Vector2 axis, float force, float length, ushort seed)
    {
        var rnd = new System.Random(seed ^ Hash(center));
        center += axis * (force * 0.3f);
        int chunks = 16;
        for (int k = 0; k < chunks; k++)
        {
            float along = ((float)rnd.NextDouble() - 0.5f) * length;
            float across = ((float)rnd.NextDouble() - 0.4f) * 0.45f; // 壁の線に寄せて山にする (叩いた側の反対へ少し多め)
            Vector2 rest = center + tangent * along - normal * across;
            float size = 0.24f + (float)rnd.NextDouble() * 0.22f;
            var it = Spawn(Kind.Fall, DebrisArt.Chunk(rnd.Next()), rest, size, keep: true);
            it.Height = 0.35f + (float)rnd.NextDouble() * 0.45f; // 壁の高さから落ちる
            it.T = -(float)rnd.NextDouble() * 0.25f;            // 崩れ始めをずらす
            it.Rot = (float)rnd.NextDouble() * 360f;
            it.VRot = ((float)rnd.NextDouble() - 0.5f) * 200f;
        }
        for (int k = 0; k < 22; k++)
        {
            Vector2 rest = center + tangent * (((float)rnd.NextDouble() - 0.5f) * length * 1.2f) - normal * (((float)rnd.NextDouble() - 0.4f) * 0.9f);
            var it = Spawn(Kind.Fall, DebrisArt.Pebble, rest, 0.06f + (float)rnd.NextDouble() * 0.07f, keep: true);
            it.Height = 0.2f + (float)rnd.NextDouble() * 0.5f;
            it.T = -(float)rnd.NextDouble() * 0.35f;
        }
        for (int k = 0; k < 7; k++)
            Dust(center + tangent * (((float)rnd.NextDouble() - 0.5f) * length), rnd, 0.55f, 1.3f);
    }

    // 打撃が当たったが崩れない: 小さな土煙と、欠けた小石が少しこぼれる
    public static void Chip(Vector2 at, Vector2 dir, ushort seed)
    {
        var rnd = new System.Random(seed ^ Hash(at));
        for (int k = 0; k < 4; k++)
        {
            Vector2 rest = at - dir * (0.05f + (float)rnd.NextDouble() * 0.3f) + new Vector2(0f, ((float)rnd.NextDouble() - 0.5f) * 0.3f);
            var it = Spawn(Kind.Fall, DebrisArt.Pebble, rest, 0.06f + (float)rnd.NextDouble() * 0.05f, keep: true);
            it.Height = 0.15f + (float)rnd.NextDouble() * 0.3f;
        }
        Dust(at - dir * 0.1f, rnd, 0.35f, 0.8f);
    }

    // 爆発: 本編の爆発の絵が一瞬 → 塊が外へ飛んで散らばる → 火花と煙。
    // 向きに偏った爆発 (force > 0) は塊と火花も向きの先へ多く飛ぶ
    public static void Explosion(Vector2 c, float radius, Vector2 dir, float force, ushort seed)
    {
        var rnd = new System.Random(seed ^ Hash(c));
        var flash = FlashSprite();
        if (flash)
        {
            var f = Spawn(Kind.Flash, flash, c, radius * 1.6f, keep: false);
            f.Life = 0.45f;
            f.Z -= 0.01f;
        }
        for (int k = 0; k < 12; k++)
        {
            float ang = (float)(rnd.NextDouble() * Math.PI * 2);
            float sp = radius * (2.5f + (float)rnd.NextDouble() * 3f);
            var it = Spawn(Kind.Fly, DebrisArt.Chunk(rnd.Next()), c, 0.12f + (float)rnd.NextDouble() * 0.14f, keep: true);
            it.Vel = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * sp + dir * (sp * force);
            it.VH = 1.5f + (float)rnd.NextDouble() * 2f;
            it.VRot = ((float)rnd.NextDouble() - 0.5f) * 720f;
        }
        for (int k = 0; k < 18; k++)
        {
            float ang = (float)(rnd.NextDouble() * Math.PI * 2);
            float sp = 3f + (float)rnd.NextDouble() * 4f;
            var it = Spawn(Kind.Spark, DebrisArt.Spark, c, 0.08f + (float)rnd.NextDouble() * 0.06f, keep: false);
            it.Vel = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * sp + dir * (sp * force);
            it.VH = 1f + (float)rnd.NextDouble() * 2f;
            it.Life = 0.35f + (float)rnd.NextDouble() * 0.5f;
        }
        for (int k = 0; k < 6; k++)
        {
            float ang = (float)(rnd.NextDouble() * Math.PI * 2);
            Dust(c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * radius * 0.5f * (float)rnd.NextDouble(), rnd, radius * 0.6f, 1.3f);
        }
    }

    private static void Dust(Vector2 at, System.Random rnd, float size, float life)
    {
        var it = Spawn(Kind.Puff, DebrisArt.Puff, at, size, keep: false);
        it.S0 = size * 0.4f;
        it.S1 = size * (1f + (float)rnd.NextDouble() * 0.4f);
        it.Life = life * (0.8f + (float)rnd.NextDouble() * 0.4f);
        it.VH = 0.4f + (float)rnd.NextDouble() * 0.4f;
        it.Vel = new Vector2(((float)rnd.NextDouble() - 0.5f) * 0.6f, 0f);
        it.Z -= 0.005f; // 塊より手前
    }

    private static Item Spawn(Kind kind, Sprite sprite, Vector2 pos, float worldSize, bool keep)
    {
        var go = new GameObject("MrpDebris");
        var tr = go.transform;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        if (DamageMap.PropMaterial) sr.sharedMaterial = DamageMap.PropMaterial;
        float z = DamageMap.FrontZ(pos) - 0.004f;
        tr.position = new Vector3(pos.x, pos.y, z);
        float s = worldSize / Math.Max(0.0001f, sprite.bounds.size.x);
        tr.localScale = new Vector3(s, s, 1f);
        if (keep) DamageMap.Track(go);
        var it = new Item { Kind = kind, Tr = tr, Sr = sr, Pos = pos, S0 = s, S1 = s, Z = z, Life = 1f };
        Items.Add(it);
        return it;
    }

    private static Sprite FlashSprite()
    {
        if (_flashSearched) return _flash;
        _flashSearched = true;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var sp = o.TryCast<Sprite>();
            if (sp && sp.name == "weapons_explosion") { _flash = sp; break; }
        }
        return _flash;
    }

    // ── 更新 ───────────────────────────────────────────────────────────

    public static void Tick()
    {
        if (Items.Count == 0) { _lastMs = 0; return; }
        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.05f, (now - _lastMs) / 1000f);
        _lastMs = now;

        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var it = Items[i];
            if (!it.Tr) { Items.RemoveAt(i); continue; }
            it.T += dt;
            if (it.T < 0f) { it.Sr.enabled = false; continue; }
            it.Sr.enabled = true;

            bool done = it.Kind switch
            {
                Kind.Fall => StepFall(it, dt),
                Kind.Fly => StepFly(it, dt),
                Kind.Puff => StepPuff(it, dt),
                Kind.Spark => StepSpark(it, dt),
                _ => StepFlash(it),
            };

            it.Tr.position = new Vector3(it.Pos.x, it.Pos.y + it.Height, it.Z);
            if (it.VRot != 0f) it.Tr.rotation = Quaternion.Euler(0f, 0f, it.Rot);

            if (done)
            {
                Items.RemoveAt(i);
                if (it.Kind is Kind.Puff or Kind.Spark or Kind.Flash) UnityEngine.Object.Destroy(it.Tr.gameObject);
            }
        }
    }

    private static bool StepFall(Item it, float dt)
    {
        it.VH -= Gravity * dt;
        it.Height += it.VH * dt;
        it.Rot += it.VRot * dt;
        if (it.Height > 0f) return false;
        // 床に着いたら 1 回だけ小さく跳ねて止まる
        if (it.VH < -1.2f) { it.Height = 0f; it.VH = -it.VH * 0.25f; it.VRot *= 0.4f; return false; }
        it.Height = 0f; it.VRot = 0f;
        return true;
    }

    private static bool StepFly(Item it, float dt)
    {
        it.Pos += it.Vel * dt;
        it.Vel *= MathF.Max(0f, 1f - 3.5f * dt); // 床をこすって減速
        it.VH -= Gravity * dt;
        it.Height = MathF.Max(0f, it.Height + it.VH * dt);
        it.Rot += it.VRot * dt;
        it.VRot *= MathF.Max(0f, 1f - 3f * dt);
        if (it.Vel.sqrMagnitude > 0.01f || it.Height > 0f) return false;
        it.VRot = 0f;
        return true;
    }

    private static bool StepPuff(Item it, float dt)
    {
        float k = it.T / it.Life;
        float s = it.S0 + (it.S1 - it.S0) * (1f - (1f - k) * (1f - k));
        it.Tr.localScale = new Vector3(s / Math.Max(0.0001f, it.Sr.sprite.bounds.size.x), s / Math.Max(0.0001f, it.Sr.sprite.bounds.size.x), 1f);
        it.Height += it.VH * dt;
        it.Pos += it.Vel * dt;
        var c = it.Sr.color; c.a = k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f; it.Sr.color = c;
        return k >= 1f;
    }

    private static bool StepSpark(Item it, float dt)
    {
        it.Pos += it.Vel * dt;
        it.Vel *= MathF.Max(0f, 1f - 2f * dt);
        it.VH -= Gravity * dt;
        it.Height = MathF.Max(0f, it.Height + it.VH * dt);
        var c = it.Sr.color; c.a = 1f - it.T / it.Life; it.Sr.color = c;
        return it.T >= it.Life;
    }

    private static bool StepFlash(Item it)
    {
        float k = it.T / it.Life;
        float grow = k < 0.25f ? 0.6f + k / 0.25f * 0.5f : 1.1f + (k - 0.25f) * 0.2f;
        it.Tr.localScale = new Vector3(it.S0 * grow, it.S0 * grow, 1f);
        var c = it.Sr.color; c.a = k < 0.5f ? 1f : 1f - (k - 0.5f) / 0.5f; it.Sr.color = c;
        return k >= 1f;
    }

    private static int Hash(Vector2 p) => (int)(p.x * 73856.093f) ^ (int)(p.y * 19349.663f);

    public static void Clear() => Items.Clear();
}
