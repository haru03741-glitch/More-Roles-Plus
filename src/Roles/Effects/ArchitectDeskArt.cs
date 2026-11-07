using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 構築士の設計台の絵: 斜め上から見た製図台 (立体で描いた絵・Resources/Icons/architect_desk.png)。
// 少し手前へ傾いた板に青図を貼り、平行定規と鉛筆受け、鉄管の脚 4 本と横木。台は少し斜めに振ってあり、左の側面と奥の脚が見える。
// 床の影は別の絵 (architect_desk_shadow.png) を台の奥に敷く (壊れた時の欠片に影が混ざらないように)
internal static class ArchitectDeskArt
{
    internal sealed class Handle
    {
        public float X, Y;     // 床の点 (台の真ん中の真下)
        public GameObject Root;
        public int Hits;       // ハンマーで叩かれた回数 (全員が控える)
        public bool Broken;    // 壊れた (番号は電文が指すので一覧からは外さない)
    }

    // 絵の範囲 (床の点が原点・単位は世界の長さ)。絵は 1 単位 200px で、この範囲ちょうどの大きさ
    private const float Left = -0.80f, Right = 0.80f, Bottom = -0.16f, Top = 1.14f;
    private const int Ppu = 200;
    private const int DeskLayer = 0;          // 仕掛け壁の絵と同じ層 (人と z で前後し、視界の外は家具と同じく暗く見える)
    private const float BodyLift = 0.3636f;   // 人の奥行きは体の y/1000 で、体は足元よりこれだけ上
    private const int ShipLayer = 9;
    // 当たり判定 (床の点からの四角)。人だけを止める (視界は遮らない・壁として削られない・歩ける所の地図に描かない)。
    // 斜めに振った 4 本の脚の足先を囲む (左右 ±0.55・手前 −0.06・奥 0.39)。奥は板の下の縁まで
    // (脚の下で止めると奥から来た人が板に隠れて足しか見えない)
    internal const float FootHalfW = 0.58f, FootFront = -0.08f, FootBack = 0.42f;

    private static Sprite _sprite, _shadow;
    private static GameObject _container;
    private static int _gen = -1;
    private static readonly List<Handle> Live = new();

    internal static int LiveCount => Live.Count;

    // 確かめ用: 最初の設計台の描画の状態と、焼いた絵の不透明な画素の数
    internal static string Probe()
    {
        if (Live.Count == 0 || !Live[0].Root) return "none";
        var sr = Live[0].Root.GetComponent<SpriteRenderer>();
        var t = Live[0].Root.transform;
        Vector3 p = t.position, s = t.lossyScale;
        string col = "-";
        var poly = Live[0].Root.GetComponentInChildren<PolygonCollider2D>();
        if (poly)
        {
            var pts = poly.points;
            Vector3 a = poly.transform.TransformPoint(pts[0]), b = poly.transform.TransformPoint(pts[2]);
            col = $"({a.x:0.00},{a.y:0.00})-({b.x:0.00},{b.y:0.00}) layer={poly.gameObject.layer} en={poly.enabled}";
        }
        return $"en={sr.enabled} active={Live[0].Root.activeInHierarchy} layer={Live[0].Root.layer} pos=({p.x:0.000},{p.y:0.000},{p.z:0.00000}) scale=({s.x:0.00},{s.y:0.00},{s.z:0.00}) tex={(sr.sprite ? sr.sprite.texture.width + "x" + sr.sprite.texture.height : "-")} col={col}";
    }

    // 役職が付いた時に読んでおく (初めて置いた時に PNG を開かないように)
    internal static void Prewarm() => EnsureSprite();

    internal static Handle Place(float x, float y)
    {
        var h = new Handle { X = x, Y = y };
        if (!Build(h)) return null;
        Live.Add(h);
        return h;
    }

    // 時戻しで壊れる前へ戻った台を作り直す (叩かれた回数と傾きも戻す)
    internal static void Unbreak(Handle h, int hits, Quaternion rot)
    {
        if (h == null || !h.Broken) return;
        if (!Build(h)) return;
        h.Broken = false;
        h.Hits = hits;
        h.Root.transform.rotation = rot;
        Live.Add(h);
    }

    // 時戻しで叩かれる前へ戻る
    internal static void Unhit(Handle h, int hits, Quaternion rot)
    {
        if (h == null || h.Broken || !h.Root) return;
        h.Hits = hits;
        h.Root.transform.rotation = rot;
    }

    private static bool Build(Handle h)
    {
        float x = h.X, y = h.Y;
        var parent = Container();
        var sp = EnsureSprite();
        if (!parent || !sp) return false;
        var root = new GameObject("MrpArchitectDesk") { layer = DeskLayer };
        root.transform.SetParent(parent.transform, false);
        root.transform.position = FxMath.V3(x, y, (y + BodyLift) / 1000f); // 船の根は z が 0 でない (Skeld は 8) ので世界の座標で置く
        var sr = root.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        FxRoot.TrackWorld(root); // 逆再生の間は控えの絵で出す
        if (_shadow)
        {
            var sh = new GameObject("Shadow") { layer = DeskLayer };
            sh.transform.SetParent(root.transform, false);
            sh.transform.localPosition = FxMath.V3(0f, 0f, 0.002f); // 床の影なので、台の奥 2 単位までにいる人より奥に敷く (影が人の体に重ならない)
            sh.AddComponent<SpriteRenderer>().sprite = _shadow;
        }
        // 入れ物は船の倍率を打ち消してあるので、局所の点は世界の差のまま
        var block = new GameObject(SolidMap.DeskBlockName) { layer = ShipLayer };
        block.transform.SetParent(root.transform, false);
        var poly = block.AddComponent<PolygonCollider2D>();
        poly.points = new[]
        {
            FxMath.V2(-FootHalfW, FootFront), FxMath.V2(FootHalfW, FootFront),
            FxMath.V2(FootHalfW, FootBack), FxMath.V2(-FootHalfW, FootBack),
        };
        h.Root = root;
        return true;
    }

    internal static void Remove(Handle h)
    {
        if (h == null) return;
        Live.Remove(h);
        if (h.Root) UnityEngine.Object.Destroy(h.Root);
    }

    // ハンマーで叩かれた (まだ壊れない): 叩かれた向きへ傾き (回数ごとに深く)、金属の音と火花
    internal static void Crack(Handle h, Vector2 face, Vector2 toward, ushort seed)
    {
        if (h == null || h.Broken || !h.Root) return;
        float lean = (toward.x >= 0f ? -1f : 1f) * TiltPerHit * h.Hits;
        h.Root.transform.rotation = Quaternion.Euler(0f, 0f, lean);
        BreakNoise.PlayAt("noise_hit_metal", h.X, h.Y + 0.5f, 9f, 5f, 0.8f, FxMath.Range(1.05f, 1.15f));
        TerrainFx.PropChip(face, toward, false, seed);
    }

    // 壊れた: 絵を升に割った欠片が叩かれた向きへ飛んで床に散らばり (当たり判定なし)、土煙と崩れる音。当たり判定は消える
    internal static void Break(Handle h, Vector2 dir, Vector2 home, ushort seed)
    {
        if (h == null || h.Broken) return;
        h.Broken = true;
        Live.Remove(h);
        if (!h.Root) return;
        UnityEngine.Object.Destroy(h.Root);
        h.Root = null;
        var parts = new List<(Sprite Sp, Vector2 At, float FloorY)>(Cols * Rows);
        var pieces = Pieces();
        for (int i = 0; i < pieces.Length; i++)
        {
            var (sp, lx, ly) = pieces[i];
            if (sp) parts.Add((sp, FxMath.V2(h.X + lx, h.Y + ly), h.Y));
        }
        var center = FxMath.V2(h.X, h.Y + 0.4f);
        TerrainFx.Shatter(parts, 1f, 1f, center, dir, false, BreakSparks, Color.white, seed, WallSegments.Snapshot(center, 4f), home);
        DustCloud.Kick(FxMath.V2(h.X, h.Y + 0.1f), 0.8f, seed);
        BreakNoise.PlayAt("noise_crumble_metal", h.X, h.Y + 0.4f, 9f, 5f, 1f, FxMath.Range(0.95f, 1.05f));
        BreakNoise.PlayAt("noise_hit_metal", h.X, h.Y + 0.4f, 9f, 5f, 0.9f, FxMath.Range(0.8f, 0.9f));
    }

    private const float TiltPerHit = 2.5f;    // 叩かれた回数ごとに傾く角度
    private const int Cols = 3, Rows = 3, BreakSparks = 8;
    private static (Sprite Sp, float Lx, float Ly)[] _pieces;

    // 絵を Cols×Rows の升に切った欠片 (升の真ん中の床の点からの位置)。1 回だけ作る
    private static (Sprite, float, float)[] Pieces()
    {
        if (_pieces != null) return _pieces;
        var sp = EnsureSprite();
        var pieces = new (Sprite, float, float)[Cols * Rows];
        if (!sp) return pieces;
        // どの升も台の一部が 2 割以上入っている (斜めに振った台が絵いっぱいに広がる) ので、全部の升を欠片にする
        int w = sp.texture.width, hgt = sp.texture.height;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                int x0 = c * w / Cols, x1 = (c + 1) * w / Cols, y0 = r * hgt / Rows, y1 = (r + 1) * hgt / Rows;
                var piece = Sprite.Create(sp.texture, new Rect(x0, y0, x1 - x0, y1 - y0), FxMath.V2(0.5f, 0.5f), Ppu, 0, SpriteMeshType.FullRect);
                piece.name = $"MrpArchitectDesk{r}{c}";
                piece.hideFlags |= HideFlags.HideAndDontSave;
                pieces[r * Cols + c] = (piece, Left + (x0 + x1) * 0.5f / Ppu, Bottom + (y0 + y1) * 0.5f / Ppu);
            }
        return _pieces = pieces;
    }

    // 船ごとに 1 つの入れ物 (船と一緒に消える)。船の倍率と反転は打ち消して世界の座標で置く
    private static GameObject Container()
    {
        var ship = ShipStatus.Instance;
        if (!ship || !GameClock.ShipAlive) return null;
        if (_gen == GameClock.ShipGen && _container) return _container;
        Live.Clear();
        _container = new GameObject("MrpArchitectDesks");
        _container.transform.SetParent(ship.transform, false);
        Vector3 ls = ship.transform.lossyScale;
        _container.transform.localScale = FxMath.V3(1f / ls.x, 1f / ls.y, 1f / ls.z);
        _gen = GameClock.ShipGen;
        return _container;
    }

    private static Sprite EnsureSprite()
    {
        if (_sprite) return _sprite;
        var pivot = FxMath.V2(-Left / (Right - Left), -Bottom / (Top - Bottom));
        _sprite = ButtonIcons.Load("architect_desk", Ppu, pivot);
        _shadow = ButtonIcons.Load("architect_desk_shadow", Ppu, pivot);
        return _sprite;
    }
}
