using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 役職の小さな粒 (油のしずく・呪いのもや など)。飛ばして落として薄れて消える。
// 自分の画面だけに出す物なので通信はしない。Plugin の毎フレームから Tick を呼ぶ
internal static class RoleSparks
{
    private struct Bit
    {
        public SpriteRenderer R;
        public Vector3 Pos, Vel;
        public float Age, Life, Grav, Size0, Size1, Drag;
        public Color C;
    }

    private static readonly List<Bit> _live = new();
    private static readonly Stack<SpriteRenderer> _pool = new();
    private static GameObject _root;
    private static Sprite _dot;
    private const int Max = 400;
    // 絵の丸は芯が半分ほどなので、渡された大きさをこれだけ広げて見える大きさに合わせる
    internal const float SizeK = 2.5f;

    internal static Sprite Dot => _dot ??= FxSprites.Rect(32, 32, FxSprites.Dot, "MrpRoleSparkDot");

    // pos: 出る所・vel: 速さ (1 秒あたり)・grav: 下向きの重さ・size0→size1: 生まれた時と消える時の大きさ
    internal static void Spawn(Vector3 pos, Vector3 vel, Color c, float life, float size0, float size1 = -1f, float grav = 0f, float drag = 0f, float age0 = 0f)
    {
        // 途中まで飛んだ所から始める (呼ぶ間隔が空いた分を埋める時)
        if (age0 > 0f)
        {
            pos += new Vector3(vel.x * age0, vel.y * age0 - 0.5f * grav * age0 * age0, 0f);
            vel.y -= grav * age0;
        }
        if (_live.Count >= Max || !Root()) return;
        var r = _pool.Count > 0 ? _pool.Pop() : NewRenderer();
        if (!r) return;
        r.gameObject.SetActive(true);
        _live.Add(new Bit { R = r, Age = age0, Pos = pos, Vel = vel, Life = Mathf.Max(0.02f, life), Grav = grav, Drag = drag, Size0 = size0, Size1 = size1 < 0f ? size0 : size1, C = c });
        Place(_live[_live.Count - 1], age0 / Mathf.Max(0.02f, life));
    }

    private static bool Root()
    {
        if (_root) return true;
        if (!ShipStatus.Instance) return false;
        _root = new GameObject("MrpRoleSparks");
        _pool.Clear();
        return true;
    }

    private static SpriteRenderer NewRenderer()
    {
        var go = new GameObject("Spark");
        go.transform.SetParent(_root.transform, false);
        var r = go.AddComponent<SpriteRenderer>();
        r.sprite = Dot;
        return r;
    }

    private static void Place(Bit b, float k)
    {
        float s = Mathf.Lerp(b.Size0, b.Size1, k) * SizeK;
        var c = b.C;
        c.a *= 1f - k * k;
        b.R.color = c;
        // 足元の高さで奥行きを決める (その高さの人より少し手前)
        b.R.transform.position = new Vector3(b.Pos.x, b.Pos.y, b.Pos.z);
        b.R.transform.localScale = new Vector3(s, s, 1f);
    }

    // 人の足元の高さで決まる奥行き (その人より少し手前に描く)
    internal static float DepthAt(float footY) => footY / 1000f - 0.02f;

    internal static void Tick()
    {
        if (_live.Count == 0) return;
        if (!_root || MeetingHud.Instance)
        {
            Clear();
            return;
        }
        float dt = Time.deltaTime;
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var b = _live[i];
            b.Age += dt;
            if (b.Age >= b.Life || !b.R)
            {
                if (b.R) { b.R.gameObject.SetActive(false); _pool.Push(b.R); }
                _live.RemoveAt(i);
                continue;
            }
            b.Vel.y -= b.Grav * dt;
            if (b.Drag > 0f) b.Vel *= Mathf.Max(0f, 1f - b.Drag * dt);
            b.Pos += new Vector3(b.Vel.x, b.Vel.y, 0f) * dt;
            _live[i] = b;
            Place(b, b.Age / b.Life);
        }
    }

    // 人の上で紙吹雪が弾けて舞い落ちる (贈り物など)
    internal static void Confetti(PlayerControl p, Color[] palette, int count = 36)
    {
        if (!p || p.inVent || !p.Visible || palette == null || palette.Length == 0) return;
        var c = p.transform.position + new Vector3(0f, 0.35f, 0f);
        c.z = DepthAt(p.GetTruePosition().y) - 0.01f;
        for (int i = 0; i < count; i++)
        {
            float a = Random.Range(0.15f, Mathf.PI - 0.15f);
            float v = Random.Range(1.4f, 3f);
            var vel = new Vector3(Mathf.Cos(a) * v * 0.8f, Mathf.Sin(a) * v, 0f);
            Spawn(c, vel, palette[i % palette.Length], Random.Range(0.9f, 1.4f), Random.Range(0.07f, 0.11f), 0.05f, 5.5f, 1.8f);
        }
        // きらめき
        for (int i = 0; i < 10; i++)
            Spawn(c + new Vector3(Random.Range(-0.35f, 0.35f), Random.Range(-0.3f, 0.3f), 0f), new Vector3(0f, 0.4f, 0f),
                new Color(1f, 1f, 0.85f, 1f), Random.Range(0.4f, 0.7f), 0.1f, 0f);
    }

    internal static void Clear()
    {
        foreach (var b in _live)
            if (b.R) { b.R.gameObject.SetActive(false); _pool.Push(b.R); }
        _live.Clear();
    }
}

// 決まった数の丸を並べ直して使う (輪・線)。毎フレーム置き直し、使わない分は隠す
internal sealed class DotSet
{
    private readonly List<SpriteRenderer> _dots = new();
    private GameObject _root;
    private readonly string _name;

    internal DotSet(string name) { _name = name; }

    private bool Ensure(int n)
    {
        if (!_root)
        {
            if (!ShipStatus.Instance) return false;
            _root = new GameObject(_name);
            _dots.Clear();
        }
        while (_dots.Count < n)
        {
            var go = new GameObject("Dot");
            go.transform.SetParent(_root.transform, false);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = RoleSparks.Dot;
            _dots.Add(r);
        }
        return true;
    }

    // n 個を f(i) の場所・色・大きさで置く。残りは隠す
    internal void Show(int n, System.Func<int, (Vector3 pos, Color c, float size)> f)
    {
        if (!Ensure(n)) return;
        _root.SetActive(true);
        for (int i = 0; i < _dots.Count; i++)
        {
            var d = _dots[i];
            if (i >= n) { if (d.gameObject.activeSelf) d.gameObject.SetActive(false); continue; }
            var (pos, c, size) = f(i);
            if (!d.gameObject.activeSelf) d.gameObject.SetActive(true);
            d.transform.position = pos;
            d.transform.localScale = new Vector3(size * RoleSparks.SizeK, size * RoleSparks.SizeK, 1f);
            d.color = c;
        }
    }

    internal void Hide()
    {
        if (_root && _root.activeSelf) _root.SetActive(false);
    }

    // 足元を中心にした輪 (frac の分だけ丸を出す。上から時計回り)
    internal void Ring(Vector2 center, float radius, float frac, Color c, int count = 20, float size = 0.07f)
    {
        int n = Mathf.Clamp(Mathf.CeilToInt(count * Mathf.Clamp01(frac)), 0, count);
        float z = RoleSparks.DepthAt(center.y) + 0.015f;
        Show(n, i =>
        {
            float a = Mathf.PI * 0.5f - i * Mathf.PI * 2f / count;
            float pulse = i == n - 1 ? 1.5f : 1f;
            return (new Vector3(center.x + Mathf.Cos(a) * radius, center.y + Mathf.Sin(a) * radius * 0.55f, z), c, size * pulse);
        });
    }
}
