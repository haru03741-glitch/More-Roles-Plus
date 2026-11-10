using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Fx;

// 全身を 1 コマずつ描き替える動き (本編の変身やキルの絵と同じ作り)。
// 本人の体の絵そのものを、本編のアニメが絵を書いた後 (LateUpdate) に連番の絵へ書き換える。24 コマ/秒。
// 体の描画物は 1 つのままなので、色・向き・大きさ・前後・帽子との重なりは本編のまま。やめれば次のフレームに本編の絵へ戻る。
// 絵は 2 通り: 本編が持っている絵を名前で読む (Vanilla)・同梱の格子の絵 (Own。本編の体の絵と同じ細かさで描く)。
// 帽子とバイザーは絵ごとの頭の動きに合わせて追わせる (足した分だけを差し替えるので、他の演出が覚えた位置を汚さない)。
// 服は立ち姿の形なので流している間は隠す。頭の無い絵 (NaN) では着せ替えを全部隠す。
// 出している人がいない間は Unity に触らない。着せ替えの位置は絵が変わるコマでだけ書く。
internal static partial class CrewFlip
{
    private const float Fps = 24f;
    private const float Ppu = 100f;               // 本編の体の絵と同じ (1 単位 = 100 px)
    private const float VanillaFeet = 103f;       // 本編の立ち絵 (idle) の原点から縁の下の端まで (px)

    private sealed class Clip
    {
        public string Name, Prefix;               // Prefix = 本編の絵の名前の頭 (null = 同梱の絵)
        public int W, H, Cols, Rows, Loop;
        public float FeetX, FeetY;                // 同梱の絵の足元 (升の中の px・上から)
        public ushort[] Nums;
        public byte[] Seq;
        public float[] Ax, Ay;                    // 絵ごとの頭の動き (右向きの時・単位)。null = 着せ替えを隠したまま
        public float Ox, Oy;                      // 本編の絵の原点を立ち絵の足元へ合わせるずらし (右向きの時・絵の単位)
        public Sprite[] Sprites;
        public bool Searched;

        // 本編の別の場面の絵 (キルの被害者など) は原点が立ち絵と違う所にあるので、そのまま差すと体が元の場所からずれる
        public static Clip Vanilla(string name, string prefix, ushort[] nums, float ox, float oy, int loop, byte[] seq, float[] ax, float[] ay) =>
            new() { Name = name, Prefix = prefix, Nums = nums, Ox = ox, Oy = oy, Loop = loop, Seq = seq, Ax = ax, Ay = ay };

        public static Clip Own(string name, int w, int h, int cols, int rows, float feetX, float feetY, int loop, byte[] seq) =>
            new() { Name = name, W = w, H = h, Cols = cols, Rows = rows, FeetX = feetX, FeetY = feetY, Loop = loop, Seq = seq };
    }

    private sealed class Show
    {
        public PlayerControl Pc;
        public byte Id;
        public Clip C;
        public float Start, Until;                // Until = この時刻で終える (0 = 連番が終われば終える・回る連番は Stop まで)
        public Transform Cos;
        public SpriteRenderer Body, Skin;
        public int Cur = -1, Last;
        public bool Flip, CosHidden, SkinHidden;
        public bool Fallen;                       // 倒れる流し方: 着せ替えは消さずに体から落とし、流している間に死んだら今の絵を死体にする
        public float K = 1f, Cx, Cy;              // 絵の 1 単位が着せ替えの親の何単位か・着せ替えに今足している分
        public float Bx, By;                      // 体の絵の位置に今足している分 (足元合わせ)
    }

    private static readonly List<Show> Live = new();
    private static int _shipGen = -1, _searchGen = -1;
    internal static int Played, Refused, NoArt;

    internal static bool IsPlaying(byte id)
    {
        for (int i = 0; i < Live.Count; i++) if (Live[i].Id == id) return true;
        return false;
    }

    // seconds = 出しておく長さ (0 = 連番が終わるまで。回る連番は Stop まで)。
    // fallen = 倒れる流し方 (服と、頭の無い絵へ移る時の帽子・バイザーを周りへ落とす。流している間に死んだら倒れた姿のまま死体になる)
    internal static bool Play(PlayerControl pc, string name, float seconds = 0f, bool fallen = false)
    {
        Clip c = Find(name);
        if (!pc || c == null || pc.Data == null || pc.Data.IsDead || pc.inVent || pc.cosmetics == null) { Refused++; return false; }
        if (!Load(c)) { NoArt++; return false; }
        byte id = pc.PlayerId;
        // 体や着せ替えを別の演出が動かしている・隠している間は始めない (隠す/出すの取り合いになる)
        if (EmoteArt.IsPlaying(id) || Terrain.CrewBrace.Posed(id) || Terrain.CrewGrip.Flying(id) || Roles.DuelTumble.Posing(id)
            || Roles.DuelShade.On(id) || Roles.ImpostorMaw.HidesCosmetics(id)) { Refused++; return false; }
        var cos = pc.cosmetics;
        var body = cos.currentBodySprite != null ? cos.currentBodySprite.BodySprite : null;
        if (!body) { Refused++; return false; }
        Stop(id);
        Roles.SamuraiBlade.ReleaseBody(id);
        if (Live.Count == 0) _shipGen = Terrain.GameClock.ShipGen;

        var s = new Show { Pc = pc, Id = id, C = c, Start = Time.time, Body = body, Cos = cos.transform, Fallen = fallen };
        s.Until = seconds > 0f ? s.Start + seconds : 0f;
        s.Skin = cos.skin ? cos.skin.layer : null;
        // 頭の動きは絵の単位 (100 px = 1) なので、着せ替えの親の単位へ直す (体の絵は縮めて置かれている)
        float ps = s.Cos.parent ? s.Cos.parent.lossyScale.x : 1f;
        s.K = ps > 0.001f ? body.transform.lossyScale.x / ps : 1f;
        s.Flip = body.flipX;
        Live.Add(s);
        Played++;
        Hide(s);
        Feet(s);
        Frame(s, 0);
        body.sprite = c.Sprites[s.Cur];
        return true;
    }

    internal static void Stop(byte id)
    {
        for (int i = Live.Count - 1; i >= 0; i--)
            if (Live[i].Id == id)
            {
                End(Live[i]);
                Live.RemoveAt(i);
            }
    }

    internal static void ClearAll()
    {
        for (int i = 0; i < Live.Count; i++) End(Live[i]);
        Live.Clear();
    }

    internal static void Tick()
    {
        if (Live.Count == 0) return;
        if (_shipGen != Terrain.GameClock.ShipGen || MeetingHud.Instance || Roles.RewindPlayback.Active)
        {
            ClearAll();
            return;
        }
        float now = Time.time;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var s = Live[i];
            int tick = (int)((now - s.Start) * Fps);
            bool done = s.Until > 0f ? now >= s.Until : s.C.Loop < 0 && tick >= s.C.Seq.Length;
            if (done || !s.Pc || !s.Body || s.Pc.Data == null || s.Pc.Data.IsDead || s.Pc.Data.Disconnected || s.Pc.inVent)
            {
                End(s);
                Live.RemoveAt(i);
                continue;
            }
            if (!s.Pc.Visible)
            {
                // 見えない所から出てくる時に本編が着せ替えを出し直すので、次のコマで決め直す
                s.CosHidden = false;
                s.Cur = -1;
                continue;
            }
            Hide(s);
            // 倒れる流し方は始めた時の向きのまま (動けない人でも他人の画面では位置の補正で本編が向きを変えることがあり、
            // 伏せた姿と死体が倒れた時と逆を向く)。LateTick で体の向きを書き戻す
            bool flip = s.Body.flipX;
            if (!s.Fallen && flip != s.Flip)
            {
                s.Flip = flip;
                s.Cur = -1;
                Feet(s);
            }
            Frame(s, tick);
        }
    }

    // 本編のアニメが体の絵を書いた後に上書きする (毎フレーム書き直されるので毎フレーム)
    internal static void LateTick()
    {
        for (int i = 0; i < Live.Count; i++)
        {
            var s = Live[i];
            if (s.Cur < 0 || !s.Body) continue;
            s.Body.sprite = s.C.Sprites[s.Cur];
            if (s.Fallen && s.Body.flipX != s.Flip) s.Body.flipX = s.Flip;
        }
    }

    private static void Frame(Show s, int tick)
    {
        var seq = s.C.Seq;
        int n = seq.Length;
        if (tick >= n) tick = s.C.Loop >= 0 && s.C.Loop < n ? s.C.Loop + (tick - n) % (n - s.C.Loop) : n - 1;
        int f = seq[tick];
        if (f == s.Cur) return;
        s.Cur = s.Last = f;
        Head(s, f);
    }

    // 帽子とバイザーを絵の頭に追わせる。頭の無い絵では着せ替えを隠す
    private static void Head(Show s, int f)
    {
        var c = s.C;
        float ax = c.Ax != null ? c.Ax[f] : float.NaN;
        bool hide = float.IsNaN(ax);
        if (hide != s.CosHidden)
        {
            // 倒れる流し方では、消す前に今の場所から帽子とバイザーを落とす
            if (hide && s.Fallen) FallenCorpse.ShedCosmetics(s.Pc, true, false);
            s.CosHidden = hide;
            s.Pc.cosmetics.SetBodyCosmeticsVisible(!hide);
        }
        if (hide || !s.Cos) return;
        float tx = (s.Flip ? -ax : ax) * s.K, ty = c.Ay[f] * s.K;
        if (tx == s.Cx && ty == s.Cy) return;
        var p = s.Cos.localPosition;
        s.Cos.localPosition = FxMath.V3(p.x + tx - s.Cx, p.y + ty - s.Cy, p.z);
        s.Cx = tx;
        s.Cy = ty;
    }

    // 体の絵を足元合わせの分だけ動かす (左右が反転すると原点の左右も反転する)。
    // 足した分だけを差し替えるので、同時に体を動かす他の演出 (チキンの震えなど) の分は消さない
    private static void Feet(Show s)
    {
        var c = s.C;
        if (!s.Body || (c.Ox == 0f && c.Oy == 0f)) return;
        var t = s.Body.transform;
        var l = t.localScale;
        float tx = (s.Flip ? -c.Ox : c.Ox) * l.x, ty = c.Oy * l.y;
        if (tx == s.Bx && ty == s.By) return;
        var p = t.localPosition;
        t.localPosition = FxMath.V3(p.x + tx - s.Bx, p.y + ty - s.By, p.z);
        s.Bx = tx;
        s.By = ty;
    }

    // 服は立ち姿の形なので隠す。本編が出し直したら隠し直す (歩きの絵への切り替え・見えない所から出てきた時)
    private static void Hide(Show s)
    {
        if (s.Skin && s.Skin.enabled)
        {
            // 倒れる流し方では、服は体から外れて落ちる
            if (s.Fallen && !s.SkinHidden) FallenCorpse.ShedCosmetics(s.Pc, false, true);
            s.Skin.enabled = false;
            s.SkinHidden = true;
        }
    }

    private static void End(Show s)
    {
        if (s.Fallen)
        {
            // 流している間に死んだ: 今の姿のまま死体になる。生きていれば落ちた物は少し待って消える
            if (s.Pc && s.Pc.Data != null && s.Pc.Data.IsDead && s.Body) FallenCorpse.Corpse(s.Pc, s.C.Sprites[s.Last], s.Body, s.Flip);
            else FallenCorpse.Ended(s.Id);
        }
        if (s.Cos && (s.Cx != 0f || s.Cy != 0f))
        {
            var p = s.Cos.localPosition;
            s.Cos.localPosition = FxMath.V3(p.x - s.Cx, p.y - s.Cy, p.z);
            s.Cx = s.Cy = 0f;
        }
        // 死体を写した後で戻す (死体は倒れた絵を差していた時の位置に置く)
        if (s.Body && (s.Bx != 0f || s.By != 0f))
        {
            var t = s.Body.transform;
            var p = t.localPosition;
            t.localPosition = FxMath.V3(p.x - s.Bx, p.y - s.By, p.z);
            s.Bx = s.By = 0f;
        }
        if (!s.Pc || s.Pc.cosmetics == null) return;
        // 向きを止めていた体を本編の向きへ戻す
        if (s.Fallen && s.Body) s.Body.flipX = s.Pc.cosmetics.FlipX;
        // ベントの中などで本編が隠している人と、別の演出が着せ替えを隠している人は出さない (それぞれ終わる時に戻る)
        bool show = s.Pc.Visible;
        if (s.SkinHidden && s.Skin) s.Skin.enabled = show;
        if (s.CosHidden && show && !Roles.ImpostorMaw.HidesCosmetics(s.Id) && !Roles.DuelShade.On(s.Id)) s.Pc.cosmetics.SetBodyCosmeticsVisible(true);
    }

    private static Clip Find(string name)
    {
        for (int i = 0; i < Clips.Length; i++) if (Clips[i].Name == name) return Clips[i];
        return null;
    }

    private static bool Load(Clip c)
    {
        if (c.Sprites != null && c.Sprites.Length > 0 && c.Sprites[0]) return true;
        if (c.Prefix == null)
        {
            // 絵の原点を本編の立ち絵と同じ所 (足元の VanillaFeet 上・左右は足元の点) に置いて切り分ける
            var sheet = Roles.ButtonIcons.Load("crewflip_" + c.Name, Ppu, FxMath.V2(0.5f, 0.5f));
            if (!sheet) return false;
            var tex = sheet.texture;
            var pivot = FxMath.V2(c.FeetX / c.W, (c.H - c.FeetY + VanillaFeet) / c.H);
            var list = new Sprite[c.Cols * c.Rows];
            for (int i = 0; i < list.Length; i++)
            {
                list[i] = Sprite.Create(tex, new Rect(i % c.Cols * c.W, (c.Rows - 1 - i / c.Cols) * c.H, c.W, c.H), pivot, Ppu, 0, SpriteMeshType.FullRect);
                list[i].hideFlags = HideFlags.HideAndDontSave;
            }
            c.Sprites = list;
            return true;
        }
        // 本編の絵は読み込まれている物の中から名前で探す (試合ごとに 1 回まで。見つからなければ次の試合で探し直す)
        int gen = Terrain.GameClock.ShipGen;
        if (_searchGen != gen)
        {
            _searchGen = gen;
            for (int i = 0; i < Clips.Length; i++) Clips[i].Searched = false;
        }
        if (c.Searched) return false;
        c.Searched = true;
        var found = new Sprite[c.Nums.Length];
        int n = 0, len = c.Prefix.Length + 4;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var sp = o.TryCast<Sprite>();
            if (!sp) continue;
            string nm = sp.name;
            if (nm == null || nm.Length != len || !nm.StartsWith(c.Prefix, StringComparison.Ordinal)) continue;
            if (!int.TryParse(nm.AsSpan(c.Prefix.Length), out int k)) continue;
            int at = Array.IndexOf(c.Nums, (ushort)k);
            if (at < 0 || found[at]) continue;
            found[at] = sp;
            n++;
        }
        if (n < found.Length)
        {
            Plugin.Logger.LogWarning($"CrewFlip: {c.Name} art not found ({n}/{found.Length})");
            return false;
        }
        c.Sprites = found;
        return true;
    }

    internal static string Names()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Clips.Length; i++) sb.Append(i > 0 ? "," : "").Append(Clips[i].Name);
        return sb.ToString();
    }

    internal static string Describe() => $"live={Live.Count} played={Played} refused={Refused} noart={NoArt} clips={Names()}";
}

[HarmonyLib.HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate))]
internal static class CrewFlipLatePatch
{
    public static void Postfix() => CrewFlip.LateTick();
}
