using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Fx;

// 焼けて焦げる体: 炎の中で崩れている間、体の絵がまだらに煤の色へ変わり、焦げの境目が燠火で赤く光る。
// 死ぬと焦げたまま死体になり (FallenCorpse が死体の絵へ移す)、燠火は冷えて消え、煙が少しの間昇る。
// 焦げはクルーの体を塗るシェーダ (2 色塗りと同じ物) の値で描くので、本編の絵・向き・2 色塗りの上にそのまま掛かる。
// 誰も焼けていない間は Unity に触らない。値は 1/15 秒ごとにだけ書く
internal static class BodyChar
{
    internal const float Max = 0.42f;          // 焦げきった量 (模様の半分ほどが炭・残りに元の色が見える)
    private const float WriteEvery = 1f / 15f;
    private const float CoolTime = 9f;         // 死体の燠火が冷えきるまで
    private const float SmokeLife = 7f;        // 死体から煙が昇る長さ

    private static readonly int CharId = Shader.PropertyToID("_Char");
    private static readonly int EmberId = Shader.PropertyToID("_Ember");
    private static readonly int SeedId = Shader.PropertyToID("_CharSeed");

    private sealed class Burn
    {
        public byte Id;
        public PlayerControl Pc;
        public SpriteRenderer Sr;
        public float Start, Ramp, Next, Seed;
        public bool Corpse;
    }

    private static readonly List<Burn> Live = new();
    private static int _gen = -1;

    internal static float SeedOf(byte id) => 17.3f * id + 4.1f;

    // 炎の中で崩れ始めた (全員の端末で)。ramp 秒で焦げきる
    internal static void Start(PlayerControl pc, float ramp)
    {
        var body = pc && pc.cosmetics && pc.cosmetics.currentBodySprite != null ? pc.cosmetics.currentBodySprite.BodySprite : null;
        if (!body) return;
        Gen();
        Stop(pc.PlayerId);
        var b = new Burn { Id = pc.PlayerId, Pc = pc, Sr = body, Start = Time.time, Ramp = ramp, Seed = SeedOf(pc.PlayerId) };
        if (!Set(b.Sr, pc.cosmetics.ColorId, 0f, 1f, b.Seed)) return;
        Live.Add(b);
    }

    // 焼けて死んだ人の死体の絵を焦げたままにする (燠火はここから冷える)
    internal static void Corpse(SpriteRenderer sr, int color, byte id, Vector3 at)
    {
        if (!Set(sr, color, Max, 1f, SeedOf(id))) return;
        Gen();
        Live.Add(new Burn { Id = id, Sr = sr, Start = Time.time, Seed = SeedOf(id), Corpse = true });
        Terrain.FireFx.AddSmolder(at.x, at.y, 1f, 0f, 0.22f, SmokeLife, true);
    }

    // 死体の色が塗り直された (暗視カメラなど): 焦げを掛け直す
    internal static void Reapply(SpriteRenderer sr, int color, byte id)
    {
        float ember = 0f;
        for (int i = 0; i < Live.Count; i++) if (Live[i].Sr == sr) ember = Ember(Live[i], Time.time);
        Set(sr, color, Max, ember, SeedOf(id));
    }

    internal static void Stop(byte id)
    {
        for (int i = Live.Count - 1; i >= 0; i--)
            if (!Live[i].Corpse && Live[i].Id == id) { Unset(Live[i].Sr); Live.RemoveAt(i); }
    }

    internal static void Tick()
    {
        if (Live.Count == 0) return;
        if (_gen != Terrain.GameClock.ShipGen) { ClearAll(); return; }
        if (Roles.RewindPlayback.Active) return;
        float now = Time.time;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var b = Live[i];
            if (!b.Sr) { Live.RemoveAt(i); continue; }
            if (now < b.Next) continue;
            b.Next = now + WriteEvery;
            if (b.Corpse)
            {
                float e = Ember(b, now);
                Write(b.Sr, Max, e);
                if (e <= 0f) Live.RemoveAt(i);
                continue;
            }
            // 死んだ体は本編が幽霊に替える (焦げは死体の絵へ移した)・助かった人は元の色へ
            bool dead = !b.Pc || b.Pc.Data == null || b.Pc.Data.IsDead || b.Pc.Data.Disconnected;
            if (dead || !Roles.TerrainDeath.IsDoomed(b.Id)) { Unset(b.Sr); Live.RemoveAt(i); continue; }
            float t = FxMath.Clamp01((now - b.Start) / b.Ramp);
            float ch = Max * t * t * (3f - 2f * t);
            // 色の塗り直し (本編の SetColor) で人形の材質が本編のシェーダへ戻ったら掛け直す
            var mat = b.Sr.sharedMaterial;
            if (mat && mat.shader != MrpBundle.PlayerGradientShader) Set(b.Sr, b.Pc.cosmetics.ColorId, ch, 1f, b.Seed);
            else Write(b.Sr, ch, 1f);
        }
    }

    private static float Ember(Burn b, float now) => b.Corpse ? FxMath.Clamp01(1f - (now - b.Start) / CoolTime) : 1f;

    private static bool Set(SpriteRenderer sr, int color, float ch, float ember, float seed)
    {
        if (!sr || !MrpBundle.PlayerGradientShader) return false;
        Menu.CustomColors.ApplyGradient(sr, color, solid: true);
        var mat = sr.sharedMaterial;
        if (!mat || mat.shader != MrpBundle.PlayerGradientShader) return false;
        mat.SetFloat(SeedId, seed);
        mat.SetFloat(CharId, ch);
        mat.SetFloat(EmberId, ember);
        return true;
    }

    private static void Write(SpriteRenderer sr, float ch, float ember)
    {
        var mat = sr.sharedMaterial;
        if (!mat) return;
        mat.SetFloat(CharId, ch);
        mat.SetFloat(EmberId, ember);
    }

    private static void Unset(SpriteRenderer sr)
    {
        if (sr) Write(sr, 0f, 0f);
    }

    private static void Gen()
    {
        if (_gen == Terrain.GameClock.ShipGen) return;
        _gen = Terrain.GameClock.ShipGen;
        ClearAll();
    }

    internal static void ClearAll()
    {
        foreach (var b in Live) if (!b.Corpse) Unset(b.Sr);
        Live.Clear();
    }

    internal static string Describe() => $"char live={Live.Count}";
}
