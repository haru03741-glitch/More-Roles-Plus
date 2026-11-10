using System.Collections.Generic;
using PowerTools;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Fx;

// 倒れた姿のまま死体になる・着せ替えが体から外れて周りに落ちる (全身を描き替える連番 CrewFlip の「倒れる」流し方から使う)。
//   Shed   = 帽子・バイザー・服を今見えている絵のまま写し取り、体から跳ねて床へ落とす (体の絵が着せ替えを付けられない姿になる時)
//   Corpse = 死んだ瞬間の体の絵を写し取り、本編の死体が出たらその絵を隠して写した姿を死体として置く
// 写した物は本編の死体の子にするので、死体が消える時 (会議・食べる役職など) に一緒に消え、通報の当たりも本編の死体のまま。
// 死ななかった時 (会議で助かった等) は落ちた着せ替えを薄くして消す。誰も倒れていない間は Unity に触らない
internal static class FallenCorpse
{
    private const float Gravity = 6.5f;            // 落ちる物の重力 (単位/秒²)
    private const float Bounce = 0.28f;            // 床で 1 回だけ跳ね返る速さの割合
    private const float BodyWait = 2.5f;           // 死んでから本編の死体が出るのを待つ上限 (秒)
    // 死体は本編のキルの直後に出るので始めは細かく探す (写しと本編の死体が重なって見える間を短く)
    private const float BodyEager = 0.6f, BodySearchEager = 0.03f, BodySearchEvery = 0.1f;
    private const float FadeTime = 0.4f;           // 助かった時に落ちた物が消える長さ (秒)
    private const float AliveLinger = 0.6f;        // 連番が終わってから助かったと決めるまで (死の知らせの遅れを待つ)

    private sealed class Piece
    {
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer[] Srs;
        public float X, Y, Z, Vx, Vy, Ang, Spin, Rest, Ground;
        public int Hits;
        public bool Landed;
    }

    private sealed class Entry
    {
        public byte Id;
        public PlayerControl Pc;
        public readonly List<Piece> Pieces = new();
        public bool HatShed, SkinShed;
        public GameObject Doll;                     // 倒れた姿の写し (死体になる)
        public bool Dead, Adopted, Dressed;         // Dressed = 本編の死体の絵を倒れた姿に差し替えた
        public DeadBody Body;
        public float DiedAt, NextSearch, EndedAt = -1f, FadeAt = -1f;
    }

    private static readonly List<Entry> Live = new();
    private static readonly List<Entry> Kept = new(); // 死体に移し終えた物 (死体の絵を触る他の処理が尋ねる)
    private static int _gen = -1;
    internal static int Shed, Bodies, NoBody, Saved;

    // 体から着せ替えを外して落とす。hatVisor = 帽子とバイザー・skin = 服 (それぞれ 1 人 1 回)。
    // 見えていない人 (ベントの中・透明・隠れている) は写さない (落ちた物で居場所を漏らさない)
    // dying = 死んだ瞬間 (本編が体を幽霊に替えて見えなくした後なので、見えるかは問わない)
    internal static void ShedCosmetics(PlayerControl pc, bool hatVisor, bool skin, bool dying = false)
    {
        if (!pc || pc.cosmetics == null || pc.inVent) return;
        byte id = pc.PlayerId;
        if (!dying && (!pc.Visible || Roles.Cloak.AlphaOf(id) < 0.99f || Roles.UchuKit.Hidden.AlphaOf(id) < 0.99f)) return;
        var e = Get(pc);
        if (e == null) return;
        var cos = pc.cosmetics;
        float ground = pc.GetTruePosition().y;
        float face = cos.FlipX ? -1f : 1f;
        int color = cos.ColorId;
        int layer = pc.gameObject.layer;
        if (hatVisor && !e.HatShed)
        {
            e.HatShed = true;
            // 帽子は後ろへ高く・バイザーは前へ低く
            if (cos.hat) Launch(e, cos.hat.transform, false, layer, color, ground, -face, 0.55f, 1.55f, 90f);
            if (cos.visor) Launch(e, cos.visor.transform, false, layer, color, ground, face, 0.65f, 0.9f, 180f);
        }
        if (skin && !e.SkinShed && cos.skin)
        {
            e.SkinShed = true;
            // 服は体からずり落ちて横に広がる (隠れた後でも絵は残っているので、止めてある描画物から写す)
            Launch(e, cos.skin.transform, true, layer, color, ground, -face, 0.25f, 0.35f, 90f);
        }
    }

    // 死んだ瞬間の体の絵 (sprite) を from の描画物の位置と向きで写す。本編の死体が出たら入れ替える。
    // まだ付いている着せ替えも同時に落とす
    // flipX = 生きている間の最後の向き (死んだ瞬間の描画物は本編が幽霊へ替える時に向きが戻っていることがある)
    internal static void Corpse(PlayerControl pc, Sprite sprite, SpriteRenderer from, bool flipX)
    {
        if (!pc || !sprite || !from) return;
        var e = Get(pc);
        if (e == null || e.Doll) return;
        // 斬られて吹き飛ぶ人は飛ぶ人形 (着せ替えごと写した物) が、必殺技で斬られた人は斬れた形の片が体の代わりになる。
        // ここで写すと二重になる (帽子も 2 つ落ちる) ので、既に落ちた物だけを残す
        if (Roles.DuelTumble.KoActive(pc.PlayerId) || Roles.CutCorpse.Has(pc.PlayerId))
        {
            e.Dead = true;
            e.DiedAt = Time.time;
            return;
        }
        ShedCosmetics(pc, true, true, dying: true);
        var go = Roles.FxRoot.New("MrpFallenCorpse", pc.gameObject.layer);
        var ft = from.transform;
        go.transform.position = ft.position;
        go.transform.rotation = ft.rotation;
        WorldScale(go.transform, ft.lossyScale);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.flipX = flipX;
        sr.sortingOrder = from.sortingOrder;
        var mat = FxHands.PlayerMat();
        if (mat) sr.sharedMaterial = mat;
        int color = pc.cosmetics ? pc.cosmetics.ColorId : 0;
        // 色を塗ると材質がこの絵だけの物になるので、2 色塗りは塗った後に (共有の材質を書き換えない)
        if (color >= 0 && color < Palette.PlayerColors.Length)
        {
            PlayerMaterial.SetColors(color, sr);
            Menu.CustomColors.ApplyGradient(sr, color);
        }
        e.Doll = go;
        e.Dead = true;
        e.DiedAt = Time.time;
    }

    // 流していた連番が終わった (死んでいなければ、少し待って助かったと決める)
    internal static void Ended(byte id)
    {
        for (int i = 0; i < Live.Count; i++)
            if (Live[i].Id == id && Live[i].EndedAt < 0f) Live[i].EndedAt = Time.time;
    }

    private static Entry Get(PlayerControl pc)
    {
        if (!ShipStatus.Instance) return null;
        if (_gen != Terrain.GameClock.ShipGen) { ClearAll(); _gen = Terrain.GameClock.ShipGen; }
        byte id = pc.PlayerId;
        for (int i = 0; i < Live.Count; i++) if (Live[i].Id == id && !Live[i].Adopted) return Live[i];
        var e = new Entry { Id = id, Pc = pc };
        Live.Add(e);
        return e;
    }

    // root の下の見えている絵をまとめて 1 つの落ちる物にする (着せ替えの絵は本人の色で塗り直す)
    private static void Launch(Entry e, Transform root, bool evenIfHidden, int layer, int color, float ground, float side, float speed, float up, float rest)
    {
        var srcs = root.GetComponentsInChildren<SpriteRenderer>(true);
        var go = Roles.FxRoot.New("MrpFallenCosmetic", layer);
        var tf = go.transform;
        int n = 0;
        float minX = 0f, maxX = 0f, minY = 0f, maxY = 0f, z = 0f;
        foreach (var s in srcs)
        {
            if (!s || !s.sprite || !s.gameObject.activeInHierarchy || !s.enabled && !evenIfHidden) continue;
            var b = s.bounds;
            if (n == 0) { minX = b.min.x; maxX = b.max.x; minY = b.min.y; maxY = b.max.y; z = s.transform.position.z; }
            else
            {
                minX = FxMath.Min(minX, b.min.x); maxX = FxMath.Max(maxX, b.max.x);
                minY = FxMath.Min(minY, b.min.y); maxY = FxMath.Max(maxY, b.max.y);
            }
            n++;
        }
        if (n == 0) { Object.Destroy(go); return; }
        // 物の中心を回る軸にする
        float cx = (minX + maxX) * 0.5f, cy = (minY + maxY) * 0.5f;
        tf.position = FxMath.V3(cx, cy, z);
        var list = new List<SpriteRenderer>();
        foreach (var s in srcs)
        {
            if (!s || !s.sprite || !s.gameObject.activeInHierarchy || !s.enabled && !evenIfHidden) continue;
            var part = Roles.FxRoot.New("Part", layer, tf);
            var pt = part.transform;
            var st = s.transform;
            pt.position = st.position;
            pt.rotation = st.rotation;
            WorldScale(pt, st.lossyScale);
            var sr = part.AddComponent<SpriteRenderer>();
            sr.sprite = s.sprite;
            sr.flipX = s.flipX;
            sr.flipY = s.flipY;
            sr.sortingOrder = s.sortingOrder;
            sr.sharedMaterial = s.sharedMaterial;
            sr.color = s.color;
            if (color >= 0 && color < Palette.PlayerColors.Length) PlayerMaterial.SetColors(color, sr);
            list.Add(sr);
        }
        float jitter = 0.75f + 0.5f * Random.value;
        e.Pieces.Add(new Piece
        {
            Go = go, Tf = tf, Srs = list.ToArray(),
            X = cx, Y = cy, Z = z,
            Vx = side * speed * jitter,
            Vy = up * (0.85f + 0.3f * Random.value),
            Spin = -side * (380f + 220f * Random.value) * (up > 0.5f ? 1f : 0.3f),
            // 床に着いた時の向き (横倒し)。少しばらす
            Rest = -side * (rest + (Random.value - 0.5f) * 30f),
            // 足元の線の少し前後に散らす
            Ground = ground - 0.05f + (Random.value - 0.5f) * 0.18f,
        });
        Shed++;
    }

    // 毎フレーム (Plugin の Update から・CrewFlip の後)
    internal static void Tick()
    {
        if (Live.Count == 0) return;
        if (_gen != Terrain.GameClock.ShipGen || Roles.RewindPlayback.Active) { ClearAll(); return; }
        float now = Time.time, dt = Time.deltaTime;
        bool meeting = MeetingHud.Instance || ExileController.Instance;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var e = Live[i];
            // 会議で消えるのは本編の死体と同じ (子にした物は死体と一緒に消える)
            if (meeting && !e.Adopted) { Drop(e); Live.RemoveAt(i); continue; }
            bool settled = true;
            for (int k = 0; k < e.Pieces.Count; k++) if (!Fly(e.Pieces[k], dt)) settled = false;

            // 連番を流していない所で死んだ (別の死に方): 落ちた物は死体と一緒に残す
            if (!e.Dead && e.Pc && e.Pc.Data != null && e.Pc.Data.IsDead) { e.Dead = true; e.DiedAt = now; }
            if (e.Dead && !e.Adopted)
            {
                // 吹き飛んでいる間は待つ (死体は飛んだ先へ動くので、落ちた物を先に死体の子にすると一緒に動いてしまう)
                if (Roles.DuelTumble.KoActive(e.Id)) { e.DiedAt = now; continue; }
                float t = now - e.DiedAt;
                if (now >= e.NextSearch && t < BodyWait)
                {
                    e.NextSearch = now + (t < BodyEager ? BodySearchEager : BodySearchEvery);
                    e.Body = FindBody(e.Id);
                    if (e.Body) Adopt(e);
                }
                if (!e.Adopted && t >= BodyWait) { NoBody++; Drop(e); Live.RemoveAt(i); continue; }
            }
            if (e.Adopted)
            {
                // 死体が消えた (食べられた・会議) か、落ちきったら見送る (死体の子として残る)
                if (!e.Body || settled)
                {
                    Live.RemoveAt(i);
                    if (e.Body) Kept.Add(e);
                }
                continue;
            }
            if (!e.Dead)
            {
                // 助かった: 落ちた物を薄くして消す
                if (e.EndedAt >= 0f && e.FadeAt < 0f && now - e.EndedAt >= AliveLinger) { e.FadeAt = now; Saved++; }
                if (e.FadeAt >= 0f)
                {
                    float a = 1f - (now - e.FadeAt) / FadeTime;
                    if (a <= 0f) { Drop(e); Live.RemoveAt(i); continue; }
                    foreach (var p in e.Pieces)
                        foreach (var sr in p.Srs) if (sr) { var c = sr.color; sr.color = FxMath.Rgba(c.r, c.g, c.b, a); }
                }
            }
        }
    }

    // 1 つ動かす。床に着いて止まったら true
    private static bool Fly(Piece p, float dt)
    {
        if (p.Landed || !p.Tf) return true;
        p.Vy -= Gravity * dt;
        p.X += p.Vx * dt;
        p.Y += p.Vy * dt;
        p.Ang += p.Spin * dt;
        if (p.Y <= p.Ground && p.Vy < 0f)
        {
            p.Y = p.Ground;
            if (p.Hits++ == 0 && -p.Vy > 0.6f)
            {
                p.Vy = -p.Vy * Bounce;
                p.Vx *= 0.45f;
                p.Spin *= 0.35f;
            }
            else
            {
                p.Landed = true;
                p.Ang = p.Rest;
            }
        }
        // 床に近づくほど床に寝た向きへ寄せる
        float near = FxMath.Clamp01(1f - (p.Y - p.Ground) / 0.25f);
        float ang = p.Landed ? p.Rest : p.Ang + (p.Rest - p.Ang) * near * near * 0.5f;
        // 前後は今いる高さではなく落ちる床の高さで決める (人と同じ y/1000)
        p.Tf.position = FxMath.V3(p.X, p.Y, p.Ground / 1000f - 0.0005f);
        p.Tf.rotation = FxMath.RotZ(ang);
        return p.Landed;
    }

    // 本編の死体の絵を倒れた姿に差し替え (アニメを止めて写しと同じ所・向き・大きさに置く)、落ちた物を死体の子にする。
    // 死体の描画物そのものを使うので、本編の死体の絵を触る他の処理 (色の塗り替え・刺さる物・消える演出) もそのまま効く
    private static void Adopt(Entry e)
    {
        var body = e.Body;
        var bt = body.transform;
        int layer = body.gameObject.layer;
        var rs = body.bodyRenderers;
        var main = rs != null && rs.Length > 0 ? rs[0] : null;
        if (e.Doll && main)
        {
            var anim = main.GetComponent<Animator>();
            if (anim) anim.enabled = false;
            var sa = main.GetComponent<SpriteAnim>();
            if (sa) sa.enabled = false;
            var dt = e.Doll.transform;
            var ds = e.Doll.GetComponent<SpriteRenderer>();
            main.sprite = ds.sprite;
            main.flipX = ds.flipX;
            var mt = main.transform;
            mt.position = dt.position;
            mt.rotation = dt.rotation;
            var ps = mt.parent ? mt.parent.lossyScale : FxMath.V3(1f, 1f, 1f);
            var ws = dt.lossyScale;
            mt.localScale = FxMath.V3(ws.x / ps.x, ws.y / ps.y, 1f);
            // 生きている体と同じ 2 色塗りに (倒れた姿のまま色だけ変わらないように)
            if (e.Pc && e.Pc.cosmetics) Menu.CustomColors.ApplyGradient(main, e.Pc.cosmetics.ColorId);
            for (int i = 1; i < rs.Length; i++) if (rs[i]) rs[i].enabled = false;
            if (body.bloodSplatter) body.bloodSplatter.enabled = false;
            Object.Destroy(e.Doll);
            e.Doll = null;
            e.Dressed = true;
            Bodies++;
        }
        foreach (var p in e.Pieces) if (p.Tf) Reparent(p.Tf, bt, layer);
        e.Adopted = true;
    }

    // ---- 本編の死体の絵を触る他の処理から ----

    private static Entry Of(DeadBody body)
    {
        if (!body || Live.Count == 0 && Kept.Count == 0) return null;
        if (_gen != Terrain.GameClock.ShipGen) { ClearAll(); _gen = Terrain.GameClock.ShipGen; return null; }
        var ptr = body.Pointer;
        for (int i = 0; i < Live.Count; i++) if (Live[i].Adopted && Live[i].Body && Live[i].Body.Pointer == ptr) return Live[i];
        for (int i = Kept.Count - 1; i >= 0; i--)
        {
            var k = Kept[i];
            if (!k.Body) { Kept.RemoveAt(i); continue; }
            if (k.Body.Pointer == ptr) return k;
        }
        return null;
    }

    // この死体の絵が倒れた姿に差し替えてある (絵の範囲が本編の死体と違い、2 色塗りも掛かっている)
    internal static bool Dressed(DeadBody body) => Of(body)?.Dressed == true;

    // 死体を誰か分からない姿にする (暗視カメラ)。on = 落ちた着せ替えを隠し 2 色塗りを color の塗り方へ・off = 戻す。
    // 体の色そのものは呼ぶ側が塗る
    internal static void Disguise(DeadBody body, int color, bool on)
    {
        var e = Of(body);
        if (e == null) return;
        var rs = body.bodyRenderers;
        if (e.Dressed && rs != null && rs.Length > 0 && rs[0]) Menu.CustomColors.ApplyGradient(rs[0], color);
        foreach (var p in e.Pieces) if (p.Go && p.Go.activeSelf == on) p.Go.SetActive(!on);
    }

    // 親の倍率 (船の倍率など) を打ち消して世界の大きさを合わせる
    private static void WorldScale(Transform t, Vector3 world)
    {
        var p = t.parent ? t.parent.lossyScale : FxMath.V3(1f, 1f, 1f);
        t.localScale = FxMath.V3(world.x / p.x, world.y / p.y, world.z / p.z);
    }

    private static void Reparent(Transform t, Transform parent, int layer)
    {
        t.SetParent(parent, true);
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) t.GetChild(i).gameObject.layer = layer;
    }

    private static DeadBody FindBody(byte id)
    {
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
            if (b && b.ParentId == id) return b;
        return null;
    }

    private static void Drop(Entry e)
    {
        if (e.Adopted) return;
        foreach (var p in e.Pieces) if (p.Go) Object.Destroy(p.Go);
        e.Pieces.Clear();
        if (e.Doll) Object.Destroy(e.Doll);
        e.Doll = null;
    }

    internal static void ClearAll()
    {
        foreach (var e in Live) Drop(e);
        Live.Clear();
        // 死体に移した物は死体と一緒に残る (時戻しで残った死体もある) ので、試合が変わった時だけ忘れる
        if (_gen != Terrain.GameClock.ShipGen) Kept.Clear();
    }

    internal static string Describe() => $"fallen live={Live.Count} kept={Kept.Count} shed={Shed} bodies={Bodies} noBody={NoBody} saved={Saved}";
}
