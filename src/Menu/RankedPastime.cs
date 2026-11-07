using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// ランクマッチで人が揃うまでの間に遊ぶミニゲーム「スターラッシュ」(自分の画面だけ)。
//   - 周りに星が出る。歩いて拾うと点。続けて拾う (2.5 秒以内) とコンボで 1 個の点が増える (最大 ×5)
//   - 赤い印には隕石が落ちる。レベル 2 からは画面を横切るレーザー (細い線の予告の後に光る)、
//     レベル 4 からは自分を追いかける隕石も来る
//   - 当たるとハートが 1 つ減る (少しの間は当たらない)。青い盾を拾うと 1 回だけ守る。たまにハートも落ちている
//   - ハートが無くなると終わり。点と最高を出して少ししたら最初から
//   - 10 点ごとにレベルが上がり、落ちる間が短く・数が多くなる。最高点は試合を挟んでも覚えておく
// 着せ替え等の画面が開いている間は止まる (Paused)
internal static class RankedPastime
{
    public static bool Active { get; private set; }
    internal static bool Paused;

    private const int MaxHearts = 3;
    private static int _score, _best, _hearts, _combo;
    private static float _lastPick = -99f, _nextHazard, _nextStar, _nextPower, _invulnUntil, _overUntil, _hitUntil;
    private static bool _shield, _over;
    private static float _clock; // 止まっている間は進まない時計
    private static Transform _root;
    private static SpriteRenderer _shieldRing, _invulnRing;
    private static readonly SpriteRenderer[] HeartIcons = new SpriteRenderer[MaxHearts];
    private static Sprite _heart;
    private static TextMeshPro _font;

    private enum Kind { Meteor, Laser, Star, Shield, Heart, Popup }

    private sealed class Thing
    {
        public Kind Kind;
        public Vector2 At, Dir;
        public float Born, Due, Life, Radius;
        public bool Done, Vertical, Homing;
        public GameObject Go;
        public SpriteRenderer A, B, C;
        public TextMeshPro Text;
    }
    private static readonly List<Thing> Things = new();
    private static Sprite _ring, _disc, _square, _star;
    private static readonly System.Random Rand = new();

    private static int Level => 1 + _score / 10;

    public static void Toggle()
    {
        if (Active) Stop();
        else Begin();
    }

    private static void Begin()
    {
        if (!LobbyBehaviour.Instance || !PlayerControl.LocalPlayer) return;
        Active = true;
        _root = new GameObject("MrpPastime").transform;
        _root.SetParent(LobbyBehaviour.Instance.transform, false);
        _font = Ranked.FontFrom(Vanilla.Hud ? Vanilla.Hud.transform : null);
        _shieldRing = Make(_root, "Shield", Ring(), 1.1f, new Color(0.4f, 0.75f, 1f, 0.9f));
        _invulnRing = Make(_root, "Invuln", Ring(), 0.9f, new Color(1f, 1f, 1f, 0.8f));
        // 残りのハート (頭の上・名前のさらに上)
        for (int i = 0; i < MaxHearts; i++) HeartIcons[i] = Make(_root, "Heart" + i, Heart(), 0.22f, Color.white);
        Restart();
    }

    private static void Restart()
    {
        foreach (var t in Things) if (t.Go) UnityEngine.Object.Destroy(t.Go);
        Things.Clear();
        _score = 0;
        _hearts = MaxHearts;
        _combo = 0;
        _shield = false;
        _over = false;
        _clock = 0f;
        _lastPick = -99f;
        _invulnUntil = 0f;
        _hitUntil = -1f;
        _nextStar = 0.3f;
        _nextHazard = 1.5f;
        _nextPower = 12f;
    }

    public static void Stop()
    {
        if (!Active) return;
        Active = false;
        Things.Clear();
        if (_root) UnityEngine.Object.Destroy(_root.gameObject);
        _root = null;
    }

    public static string ScoreText()
    {
        if (_over)
            return $"<color=#FFD54F><b>{new Text("Ranked.PastimeOver")}</b></color>   " + new Text("Ranked.PastimeFinal").With("n", _score).With("best", _best);
        string s = new Text("Ranked.PastimeScore").With("n", _score).With("lv", Level).With("best", _best);
        if (_combo >= 2 && _clock - _lastPick < 2.5f) s += $"   <color=#FFD54F>x{Math.Min(_combo, 5)}</color>";
        if (_clock < _hitUntil) s = $"<color=#FF8A80>{new Text("Ranked.PastimeHit")}</color>   " + s;
        else if (_score == 0 && _clock < 6f) s = $"<size=85%>{new Text("Ranked.PastimeHint")}</size>";
        return s;
    }

    private static void Tick()
    {
        if (!Active) return;
        var lp = PlayerControl.LocalPlayer;
        if (!LobbyBehaviour.Instance || ShipStatus.Instance || !lp || !_root) { Stop(); return; }
        if (Paused) return;
        _clock += Time.deltaTime;
        float now = _clock;
        Vector2 me = lp.GetTruePosition();
        Vector3 mePos = lp.transform.position;

        if (_shieldRing)
        {
            _shieldRing.gameObject.SetActive(_shield);
            _shieldRing.transform.position = new Vector3(mePos.x, mePos.y + 0.1f, mePos.z - 0.05f);
            _shieldRing.transform.localScale = Vector3.one * (1.1f + 0.05f * Mathf.Sin(now * 6f));
        }
        if (_invulnRing)
        {
            bool inv = now < _invulnUntil;
            _invulnRing.gameObject.SetActive(inv && Mathf.Repeat(now * 8f, 1f) < 0.5f);
            _invulnRing.transform.position = new Vector3(mePos.x, mePos.y + 0.1f, mePos.z - 0.05f);
        }

        for (int i = 0; i < MaxHearts; i++)
        {
            var h = HeartIcons[i];
            if (!h) continue;
            bool full = i < _hearts;
            h.transform.position = new Vector3(mePos.x + (i - (MaxHearts - 1) * 0.5f) * 0.26f, mePos.y + 1.05f, mePos.z - 0.1f);
            h.color = full ? new Color(1f, 0.36f, 0.48f, 1f) : new Color(0.25f, 0.27f, 0.32f, 0.75f);
            // 当たった直後は減ったハートが点滅する
            h.enabled = !(i == _hearts && now < _invulnUntil && Mathf.Repeat(now * 8f, 1f) < 0.5f);
        }

        if (_over)
        {
            if (now >= _overUntil) Restart();
            UpdateThings(me, now);
            return;
        }

        // 星
        int stars = 0;
        foreach (var t in Things) if (t.Kind == Kind.Star && !t.Done) stars++;
        if (now >= _nextStar && stars < (Level >= 3 ? 3 : 2))
        {
            SpawnPickup(Kind.Star, me, now);
            _nextStar = now + 0.8f + (float)Rand.NextDouble() * 1.2f;
        }
        // 盾・ハート
        if (now >= _nextPower)
        {
            SpawnPickup(_hearts < MaxHearts && Rand.NextDouble() < 0.5 ? Kind.Heart : Kind.Shield, me, now);
            _nextPower = now + 14f + (float)Rand.NextDouble() * 8f;
        }
        // 危ない物
        if (now >= _nextHazard)
        {
            int lv = Level;
            int n = 1 + Math.Min(3, (lv - 1) / 2);
            for (int i = 0; i < n; i++) SpawnMeteor(me, now, i == 0, lv >= 4 && i == 1);
            if (lv >= 2 && Rand.NextDouble() < Math.Min(0.6, 0.2 + lv * 0.06)) SpawnLaser(me, now);
            _nextHazard = now + Mathf.Max(0.55f, 1.5f - lv * 0.11f);
        }
        UpdateThings(me, now);
    }

    private static void UpdateThings(Vector2 me, float now)
    {
        // 途中で拾う・当たる (字が増える・終わって片付く) ので写しで回す
        foreach (var t in Things.ToArray())
        {
            if (!Things.Contains(t)) continue;
            if (!t.Go) { Things.Remove(t); continue; }
            bool remove = t.Kind switch
            {
                Kind.Meteor => UpdateMeteor(t, me, now),
                Kind.Laser => UpdateLaser(t, me, now),
                Kind.Popup => UpdatePopup(t, now),
                _ => UpdatePickup(t, me, now),
            };
            if (remove)
            {
                UnityEngine.Object.Destroy(t.Go);
                Things.Remove(t);
            }
        }
    }

    // ── 当たった・拾った ──────────────────────────

    private static void Hurt(Vector2 at)
    {
        if (_over || _clock < _invulnUntil) return;
        _combo = 0;
        _invulnUntil = _clock + 1.3f;
        if (_shield)
        {
            _shield = false;
            Popup(at, "<color=#66C2FF>" + new Text("Ranked.PastimeBlock") + "</color>");
            return;
        }
        _hearts--;
        _hitUntil = _clock + 1.2f;
        Popup(at, "<color=#FF5C7A>-♥</color>");
        if (_hearts > 0) return;
        _over = true;
        _overUntil = _clock + 3f;
        if (_score > _best) _best = _score;
        // 残っている物は片付ける (浮かぶ字だけ残す)
        for (int i = Things.Count - 1; i >= 0; i--)
        {
            if (Things[i].Kind == Kind.Popup) continue;
            if (Things[i].Go) UnityEngine.Object.Destroy(Things[i].Go);
            Things.RemoveAt(i);
        }
    }

    private static void Pick(Thing t)
    {
        t.Done = true;
        switch (t.Kind)
        {
            case Kind.Star:
                _combo = _clock - _lastPick < 2.5f ? _combo + 1 : 1;
                _lastPick = _clock;
                int gain = Math.Min(_combo, 5);
                int before = Level;
                _score += gain;
                if (_score > _best) _best = _score;
                Popup(t.At, gain > 1 ? $"<color=#FFD54F>+{gain}</color> <size=70%>x{gain}</size>" : "<color=#FFD54F>+1</color>");
                if (Level > before) Popup(t.At + new Vector2(0f, 0.4f), "<color=#7CFFCB>LEVEL " + Level + "</color>");
                break;
            case Kind.Shield:
                _shield = true;
                Popup(t.At, "<color=#66C2FF>" + new Text("Ranked.PastimeShield") + "</color>");
                break;
            case Kind.Heart:
                _hearts = Math.Min(MaxHearts, _hearts + 1);
                Popup(t.At, "<color=#FF5C7A>+♥</color>");
                break;
        }
    }

    // ── 星・盾・ハート ─────────────────────────────

    private static void SpawnPickup(Kind kind, Vector2 me, float now)
    {
        var lp = PlayerControl.LocalPlayer;
        Vector2 at = me;
        bool ok = false;
        for (int tries = 0; tries < 12 && !ok; tries++)
        {
            float ang = (float)(Rand.NextDouble() * Math.PI * 2);
            float dist = 1.2f + (float)Rand.NextDouble() * 1.8f;
            at = me + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
            // 歩いて届く所 (間に壁や物が無い)
            ok = !PhysicsHelpers.AnythingBetween(me, at, Constants.ShipAndObjectsMask, false);
        }
        if (!ok) return;
        var go = Node("Pickup", at, 0.3f);
        var t = new Thing { Kind = kind, At = at, Born = now, Life = kind == Kind.Star ? 6f : 8f, Go = go };
        Color c = kind switch { Kind.Star => new Color(1f, 0.84f, 0.3f), Kind.Shield => new Color(0.4f, 0.75f, 1f), _ => new Color(1f, 0.36f, 0.48f) };
        t.A = Make(go.transform, "Glow", Disc(), 0.7f, new Color(c.r, c.g, c.b, 0.25f));
        t.B = Make(go.transform, "Body", kind == Kind.Star ? Star() : kind == Kind.Heart ? Heart() : Disc(), kind == Kind.Shield ? 0.3f : 0.42f, c);
        t.B.transform.localPosition = new Vector3(0f, 0f, -0.01f);
        Things.Add(t);
    }

    private static bool UpdatePickup(Thing t, Vector2 me, float now)
    {
        if (t.Done) return true;
        float age = now - t.Born;
        float left = t.Life - age;
        if (left <= 0f) return true;
        float pop = Mathf.Clamp01(age / 0.2f);
        float bob = Mathf.Sin(now * 4f + t.Born) * 0.04f;
        if (t.B)
        {
            t.B.transform.localPosition = new Vector3(0f, 0.12f + bob, -0.01f);
            t.B.transform.localRotation = Quaternion.Euler(0f, 0f, t.Kind == Kind.Star ? now * 90f : 0f);
            bool blink = left < 1.5f && Mathf.Repeat(now * 6f, 1f) < 0.4f;
            t.B.enabled = !blink;
        }
        if (t.A) t.A.transform.localScale = Vector3.one * (0.7f * pop + 0.08f * Mathf.Sin(now * 5f));
        if ((me - t.At).magnitude < 0.45f) Pick(t);
        return false;
    }

    // ── 隕石 ────────────────────────────────

    private static void SpawnMeteor(Vector2 me, float now, bool aim, bool homing)
    {
        float ang = (float)(Rand.NextDouble() * Math.PI * 2);
        float dist = aim ? (float)Rand.NextDouble() * 0.4f : 0.6f + (float)Rand.NextDouble() * 1.8f;
        var at = homing ? me : me + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
        float radius = 0.5f + (float)Rand.NextDouble() * 0.2f;
        float fall = Mathf.Max(0.75f, 1.1f - Level * 0.04f);
        var go = Node("Meteor", at, 0.3f);
        var t = new Thing { Kind = Kind.Meteor, At = at, Born = now, Due = now + fall + (homing ? 0.5f : 0f), Radius = radius, Homing = homing, Go = go };
        var col = homing ? new Color(1f, 0.55f, 0.15f) : new Color(1f, 0.3f, 0.25f);
        t.A = Make(go.transform, "Fill", Disc(), 0f, new Color(col.r, col.g, col.b, 0.25f));
        t.B = Make(go.transform, "Ring", Ring(), radius * 2f, new Color(col.r, col.g, col.b, 0.9f));
        t.C = Make(go.transform, "Flash", Disc(), radius * 2f, new Color(1f, 0.85f, 0.5f, 0.9f));
        t.C.transform.localPosition = new Vector3(0f, 0f, -0.6f);
        t.C.gameObject.SetActive(false);
        Things.Add(t);
    }

    private static bool UpdateMeteor(Thing t, Vector2 me, float now)
    {
        float left = t.Due - now;
        if (!t.Done)
        {
            // 追いかける隕石は落ちる 0.6 秒前まで自分の方へ寄ってくる
            if (t.Homing && left > 0.6f)
            {
                t.At = Vector2.MoveTowards(t.At, me, 1.6f * Time.deltaTime);
                t.Go.transform.position = new Vector3(t.At.x, t.At.y, t.At.y / 1000f + 0.3f);
            }
            float span = t.Due - t.Born;
            float k = 1f - Mathf.Clamp01(left / span);
            if (t.A) t.A.transform.localScale = Vector3.one * (t.Radius * 2f * k);
            if (t.B) t.B.color = new Color(t.B.color.r, t.B.color.g, t.B.color.b, 0.6f + 0.4f * Mathf.Abs(Mathf.Sin(now * 12f)));
            if (left > 0f) return false;
            t.Done = true;
            if (!_over && (me - t.At).magnitude < t.Radius + 0.12f) Hurt(t.At);
            if (t.A) t.A.gameObject.SetActive(false);
            if (t.B) t.B.gameObject.SetActive(false);
            if (t.C) t.C.gameObject.SetActive(true);
            return false;
        }
        float e = Mathf.Clamp01(-left / 0.35f);
        if (t.C)
        {
            t.C.transform.localScale = Vector3.one * (t.Radius * 2f * (1f + 0.5f * e));
            t.C.color = new Color(1f, 0.85f, 0.5f, 0.9f * (1f - e));
        }
        return e >= 1f;
    }

    // ── レーザー ───────────────────────────────

    private static void SpawnLaser(Vector2 me, float now)
    {
        bool vertical = Rand.NextDouble() < 0.5;
        float off = ((float)Rand.NextDouble() - 0.5f) * 1.2f;
        var at = vertical ? new Vector2(me.x + off, me.y) : new Vector2(me.x, me.y + off);
        var go = Node("Laser", at, 0.29f);
        var t = new Thing { Kind = Kind.Laser, At = at, Born = now, Due = now + 1.25f, Vertical = vertical, Go = go };
        var size = vertical ? new Vector2(0.06f, 14f) : new Vector2(14f, 0.06f);
        t.A = Make(go.transform, "Warn", Square(), 1f, new Color(1f, 0.3f, 0.6f, 0.5f));
        t.A.transform.localScale = new Vector3(size.x, size.y, 1f);
        t.B = Make(go.transform, "Beam", Square(), 1f, new Color(1f, 0.6f, 0.9f, 0.95f));
        t.B.transform.localScale = new Vector3(vertical ? 0.55f : 14f, vertical ? 14f : 0.55f, 1f);
        t.B.transform.localPosition = new Vector3(0f, 0f, -0.06f);
        t.B.gameObject.SetActive(false);
        Things.Add(t);
    }

    private static bool UpdateLaser(Thing t, Vector2 me, float now)
    {
        float left = t.Due - now;
        if (left > 0f)
        {
            if (t.A) t.A.color = new Color(1f, 0.3f, 0.6f, 0.35f + 0.4f * Mathf.Abs(Mathf.Sin(now * (8f + (1.25f - left) * 10f))));
            return false;
        }
        if (!t.Done)
        {
            t.Done = true;
            if (t.A) t.A.gameObject.SetActive(false);
            if (t.B) t.B.gameObject.SetActive(true);
        }
        float d = t.Vertical ? Mathf.Abs(me.x - t.At.x) : Mathf.Abs(me.y - t.At.y);
        if (-left < 0.3f && d < 0.32f) Hurt(me);
        float e = Mathf.Clamp01(-left / 0.4f);
        if (t.B) t.B.color = new Color(1f, 0.6f, 0.9f, 0.95f * (1f - e));
        return e >= 1f;
    }

    // ── 浮かぶ字 ──────────────────────────────

    private static void Popup(Vector2 at, string text)
    {
        if (!_root) return;
        var go = Node("Popup", at + new Vector2(0f, 0.5f), -0.5f);
        var tmp = go.AddComponent<TextMeshPro>();
        if (_font) { tmp.font = _font.font; tmp.fontSharedMaterial = _font.fontSharedMaterial; }
        tmp.fontSize = 2.2f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = new Vector2(3f, 0.6f);
        tmp.outlineWidth = 0.2f;
        tmp.text = "<b>" + text + "</b>";
        tmp.sortingOrder = 50;
        Things.Add(new Thing { Kind = Kind.Popup, At = at, Born = _clock, Go = go, Text = tmp });
    }

    private static bool UpdatePopup(Thing t, float now)
    {
        float age = now - t.Born;
        if (age > 0.9f) return true;
        t.Go.transform.position = new Vector3(t.At.x, t.At.y + 0.5f + age * 0.6f, t.Go.transform.position.z);
        if (t.Text) t.Text.alpha = 1f - Mathf.Clamp01((age - 0.5f) / 0.4f);
        return false;
    }

    // ── 絵 ─────────────────────────────────

    private static GameObject Node(string name, Vector2 at, float zBack)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root, false);
        go.transform.position = new Vector3(at.x, at.y, at.y / 1000f + zBack); // 床の上 (人より奥)。字は手前
        return go;
    }

    private static SpriteRenderer Make(Transform parent, string name, Sprite sp, float scale, Color c)
    {
        var sr = new GameObject(name).AddComponent<SpriteRenderer>();
        sr.transform.SetParent(parent, false);
        sr.transform.localScale = Vector3.one * scale;
        sr.sprite = sp;
        sr.color = c;
        return sr;
    }

    private static Sprite Disc() => _disc ??= Draw((x, y) => Mathf.Clamp01(0.5f * 64f - Mathf.Sqrt(x * x + y * y)));
    private static Sprite Ring() => _ring ??= Draw((x, y) =>
    {
        float d = Mathf.Sqrt(x * x + y * y);
        return Mathf.Clamp01(32f - d) * Mathf.Clamp01(d - 28f);
    });
    private static Sprite Square() => _square ??= Draw((x, y) => 1f);

    // ハート ((x²+y²-1)³ - x²y³ ≤ 0 の形を 4×4 で数えてなめらかに)
    private static Sprite Heart() => _heart ??= Draw((x, y) =>
    {
        int inside = 0;
        for (int sy = 0; sy < 4; sy++)
        for (int sx = 0; sx < 4; sx++)
        {
            float u = (x + (sx - 1.5f) * 0.25f) / 26f * 1.25f;
            float v = (y + (sy - 1.5f) * 0.25f) / 26f * 1.25f + 0.15f;
            float a = u * u + v * v - 1f;
            if (a * a * a - u * u * v * v * v <= 0f) inside++;
        }
        return inside / 16f;
    });

    // 5 つの角の星 (多角形までの距離で縁をなめらかに)
    private static Sprite Star() => _star ??= MakeStar();

    private static Sprite MakeStar()
    {
        var pts = new Vector2[10];
        for (int k = 0; k < 10; k++)
        {
            float ang = Mathf.PI / 2f + k * Mathf.PI / 5f;
            float r = k % 2 == 0 ? 31f : 13f;
            pts[k] = new Vector2(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r - 2f);
        }
        return Draw((x, y) =>
        {
            var p = new Vector2(x, y);
            float d = float.MaxValue;
            bool inside = false;
            for (int a = 0, b = pts.Length - 1; a < pts.Length; b = a++)
            {
                Vector2 pa = pts[b], pb = pts[a], ab = pb - pa;
                float t = Mathf.Clamp01(Vector2.Dot(p - pa, ab) / Vector2.Dot(ab, ab));
                d = Mathf.Min(d, (p - (pa + ab * t)).magnitude);
                if ((pb.y > y) != (pa.y > y) && x < (pa.x - pb.x) * (y - pb.y) / (pa.y - pb.y) + pb.x) inside = !inside;
            }
            return inside ? Mathf.Clamp01(0.5f + d) : Mathf.Clamp01(0.5f - d);
        });
    }

    // 直径 1 (64 画素)。f(中心からの x, y) = 濃さ
    private static Sprite Draw(Func<float, float, float> f)
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
            px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(f(x + 0.5f - n / 2f, y + 0.5f - n / 2f)) * 255));
        tex.SetPixels32(px);
        tex.Apply(false, false);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate))]
    private static class FramePatch
    {
        public static void Postfix()
        {
            try { Tick(); }
            catch (Exception e) { Plugin.Logger.LogError($"ranked pastime: {e}"); Stop(); }
        }
    }
}
