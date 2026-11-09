using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 物の種類 (素材・大きさ・耐久・水に浸かる高さ)。ドローン 1 機ごとではなく「ドローン」という種類に 1 つ
internal sealed class BodyKind
{
    public readonly string Name;      // ログの名前 (英字)
    public readonly BodyMaterial Material;
    public readonly BodySize Size;
    public readonly float Hp;         // 耐久 (0 = 耐久を持たず、壊れずに当たった反応だけ)
    public readonly float Lift;       // 床からの高さ (水の深さ・Full = 1 単位)。これより深い水で浸かる。負 = 水を見ない
    public readonly bool Shared;      // 弾を止めるか (撃った人の端末にも同じ当たりがある物だけ。無いと撃った人の画面と当たりが食い違う)

    public BodyKind(string name, BodyMaterial material, BodySize size, float hp, float lift = -1f, bool shared = true)
    {
        Shared = shared;
        Name = name;
        Material = material;
        Size = size;
        Hp = hp;
        Lift = lift;
    }
}

// 場に置かれた物 1 つ。その物がある端末で登録する (本人にしか見えない物は本人の端末だけ)
internal sealed class Body
{
    public BodyKind Kind;
    public Transform Tf;           // 位置の読み元 (当たりを調べる時にだけ読む)
    public float OffX, OffY;       // Tf の位置から当たりの中心までのずれ
    public float Radius;           // 当たりの半径
    public float Hp;
    public Func<bool> Judges;      // この端末が結果を決める (耐久を減らし、壊す) か。null = 決めない (見た目だけ)
    // 結果を受ける。false を返すと「反応が無い」としてログに出す (反応の抜けを探すため)
    public Func<Body, StrikeResult, Strike, bool> OnStrike;
    public bool Fixed;             // Tf を持たず X, Y の位置に置いたまま (よそから届いた位置で置く当たり)
    public float Until;            // この時刻を過ぎたら消えた扱い (0 = 期限なし)
    public int Ignore;             // 反応しない属性 (1 << StrikeElement の和)。その物が自分で当たりを持つ属性はここで外す
    internal float X, Y;
    internal int SeenFrame = -1;
    internal float GuardUntil;     // 同じ物へ同じ斬りが毎フレーム重ならないように
    internal float PointUntil;     // 弾を数える間 (1 tick に 1 発)
    internal bool Gone;

    internal bool Skips(StrikeElement e) => (Ignore & (1 << (int)e)) != 0;
}

// 一撃 1 回。Edge = 切断力 (斬だけ・刀の段)
internal readonly struct Strike
{
    public readonly StrikeElement Element;
    public readonly float Power;
    public readonly int Edge;
    public readonly float X, Y, Dx, Dy;
    public readonly byte By;

    public Strike(StrikeElement element, float power, int edge, float x, float y, float dx, float dy, byte by)
    {
        Element = element;
        Power = power;
        Edge = edge;
        X = x;
        Y = y;
        Dx = dx;
        Dy = dy;
        By = by;
    }
}

// 場に置かれた物の一覧と、一撃をそこへ当てる口。武器の側は「どこへどの属性で当てたか」を渡すだけで、
// 何が起きるかは素材の表 (BodyRules) と物の OnStrike が決める。新しい物は登録すれば全部の武器と水に反応する
internal static class Bodies
{
    private static readonly List<Body> All = new();
    private static Lifespan _match;
    private static float _nextWater;
    private const float WaterEvery = 0.2f;
    private const float Regap = 0.25f;   // 同じ物へ同じ斬りを重ねない間
    private const float TickGap = 1f / 30f - 0.002f; // 弾を数える間 (1 tick)
    // 一撃の強さ (耐久を減らす量の基準)。爆発はたいていの物を 1 回で壊し、ハンマーは壁 1 枚分の耐久 3
    internal const float BulletPower = 1f, BluntPower = 3f, BlastPower = 10f;
    // 火の中に居る間 0.2 秒ごとに当てる熱 (耐久 15 のドローンは約 3 秒で焼け落ちる)
    internal const float HeatPower = 1f;
    private static readonly HashSet<int> Silent = new();
    internal static int Strikes, Silents;

    internal static int Count => All.Count;

    // 一つの属性だけに反応する物の Ignore
    internal static int AllBut(StrikeElement e) => ~(1 << (int)e);
    internal static int Mask(StrikeElement a) => 1 << (int)a;
    internal static int Mask(StrikeElement a, StrikeElement b) => (1 << (int)a) | (1 << (int)b);

    // 壊れた・斬れたと他の端末へ知らせるのはこの端末か: 刀と弾は振った・撃った人の端末 (練習の相手はホスト)・それ以外 (爆発・叩く・火・水) はホスト
    internal static bool Reports(in Strike s)
    {
        var c = AmongUsClient.Instance;
        bool host = c && c.AmHost;
        if (s.Element != StrikeElement.Slash && s.Element != StrikeElement.Bullet) return host;
        var by = PortKit.ById(s.By);
        if (!by) return s.By == 255 && host;
        return by.AmOwner || (by.isDummy && host);
    }

    // 壊れない物 (壊れると役職の働きが止まる物・飾り) を登録する。刀・弾・叩く・爆発で火花と音だけ出す。
    // 絵が消えると当たりも消える (tf が無くなった物は数えない)
    internal static Body Prop(BodyKind kind, Transform tf, float offY, float radius) => Add(new Body
    {
        Kind = kind,
        Tf = tf,
        OffY = offY,
        Radius = radius,
        OnStrike = PropStrike,
    });

    private static bool PropStrike(Body b, StrikeResult r, Strike s)
    {
        if (r == StrikeResult.Pass || r == StrikeResult.None) return false;
        if (s.Element == StrikeElement.Heat || s.Element == StrikeElement.Water) return true; // 火と水はそれぞれの絵が出ている
        Spark(b.X, b.Y, s, s.Element != StrikeElement.Blast);
        return true;
    }

    // 客から届いてよい壊れた知らせの属性 (刀と弾だけ。火・爆発・叩く・水はホストが自分で決める)
    internal static bool ClientReport(StrikeElement e) => e == StrikeElement.Slash || e == StrikeElement.Bullet;

    // 弾で物を撃てる役職か (場の物に弾を当てるのは機関銃だけ)
    internal static bool Shoots(PlayerControl p) => RoleState.Of(p) is Impostor.TriggerHappy;

    // ホストの手元の確かめの一撃 (Test・当てた人が無い) か。刀と弾の一撃は必ず振った・撃った人を持つので、人の無い刀と弾はこれだけ
    internal static bool FromTest(PlayerControl by) => !by && AmongUsClient.Instance && AmongUsClient.Instance.AmHost;

    // 当たった所に火花と金属の跳ねる音を出す (向きは一撃の向き、無ければ当てた所から物へ)
    internal static void Spark(float x, float y, in Strike s, bool sound = true)
    {
        float dx = s.Dx, dy = s.Dy;
        if (dx == 0f && dy == 0f) { dx = x - s.X; dy = y - s.Y; }
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.0001f) { dx = 0f; dy = -1f; } else { dx /= d; dy /= d; }
        GatlingFx.Deflect(x, y, dx, dy);
        if (sound) GunSound.Deflect(x, y, false);
    }
    internal static bool Contains(Body b) => All.Contains(b);

    internal static Body Add(Body b)
    {
        Use();
        b.Hp = b.Kind.Hp;
        b.Gone = false;
        b.SeenFrame = -1;
        All.Add(b);
        return b;
    }

    internal static void Remove(Body b)
    {
        if (b == null) return;
        b.Gone = true;
        All.Remove(b);
    }

    private static void Use()
    {
        var match = RoleState.Match;
        if (_match == match && !match.IsDead) return;
        _match = match;
        All.Clear();
        Silent.Clear();
        Strikes = Silents = 0;
        Events<TerrainBrokenEvent>.Subscribe(OnTerrainBroken, match, owner: "Bodies");
        match.OnRelease(() => All.Clear());
    }

    private static void Refresh(Body b)
    {
        int f = Time.frameCount;
        if (b.SeenFrame == f) return;
        b.SeenFrame = f;
        if (b.Until > 0f && Time.time > b.Until) { b.Gone = true; return; }
        if (b.Fixed) return;
        if (!b.Tf) { b.Gone = true; return; }
        Vector3 p = b.Tf.position;
        b.X = p.x + b.OffX;
        b.Y = p.y + b.OffY;
    }

    // 弾のような小さな点 (x, y, 半径 r) が当たった最初の物。true = 弾はそこで止まる (素通り・反応なしの物・本人にしか無い物では止まらない)
    internal static bool Point(in Strike s, float r)
    {
        for (int i = All.Count - 1; i >= 0; i--)
        {
            if (i >= All.Count) continue; // 反応の中で別の物が外れる (装置は 2 つとも閉じる)
            var b = All[i];
            Refresh(b);
            if (b.Gone) { All.RemoveAt(i); continue; }
            float dx = b.X - s.X, dy = b.Y - s.Y, rr = b.Radius + r;
            if (dx * dx + dy * dy > rr * rr) continue;
            // 人と同じく 1 tick に 1 発だけ数える (機関銃は 1 秒に 60 発)。全員にある物は数えない弾もそこで止める
            float now = Time.time;
            if (b.Skips(StrikeElement.Bullet)) continue;
            if (now < b.PointUntil)
            {
                if (b.Kind.Shared) return true;
                continue;
            }
            b.PointUntil = now + TickGap;
            var res = Apply(b, s);
            if (b.Kind.Shared && res != StrikeResult.Pass && res != StrikeResult.None) return true;
        }
        return false;
    }

    // 叩く向きの線 (at から dir へ reach まで) に、物の半径 + pad で掛かるいちばん近い物。face = 線が縁に入る所
    internal static bool Probe(Vector2 at, Vector2 dir, float reach, float pad, out Vector2 face, out Vector2 center)
    {
        face = center = default;
        float best = reach;
        bool found = false;
        for (int i = All.Count - 1; i >= 0; i--)
        {
            if (i >= All.Count) continue; // 反応の中で別の物が外れる (装置は 2 つとも閉じる)
            var b = All[i];
            if (b.Skips(StrikeElement.Blunt)) continue;
            Refresh(b);
            if (b.Gone) { All.RemoveAt(i); continue; }
            float px = b.X - at.x, py = b.Y - at.y;
            float t = px * dir.x + py * dir.y;
            if (t < 0f) continue;
            float rr = b.Radius + pad;
            float qx = px - dir.x * t, qy = py - dir.y * t, q2 = qx * qx + qy * qy, r2 = rr * rr;
            if (q2 > r2) continue;
            float enter = t - FxMath.Sqrt(r2 - q2);
            if (enter < 0f) enter = 0f;
            if (enter >= best) continue;
            best = enter;
            found = true;
            face = FxMath.V2(at.x + dir.x * enter, at.y + dir.y * enter);
            center = FxMath.V2(b.X, b.Y);
        }
        return found;
    }

    // 円の範囲 (爆発・叩いた所) の物すべて
    internal static void Circle(in Strike s, float r)
    {
        for (int i = All.Count - 1; i >= 0; i--)
        {
            if (i >= All.Count) continue; // 反応の中で別の物が外れる (装置は 2 つとも閉じる)
            var b = All[i];
            Refresh(b);
            if (b.Gone) { All.RemoveAt(i); continue; }
            float dx = b.X - s.X, dy = b.Y - s.Y, rr = b.Radius + r;
            if (dx * dx + dy * dy > rr * rr) continue;
            Apply(b, s);
        }
    }

    // 刀の刃 (根元 (x, y) から向き (co, si) に長さ len) が掛かった物。振っている間の毎フレーム呼ばれる
    internal static void Blade(byte by, int edge, float x, float y, float co, float si, float len)
    {
        if (All.Count == 0) return;
        float now = Time.time;
        for (int i = All.Count - 1; i >= 0; i--)
        {
            if (i >= All.Count) continue; // 反応の中で別の物が外れる (装置は 2 つとも閉じる)
            var b = All[i];
            if (now < b.GuardUntil || b.Skips(StrikeElement.Slash)) continue;
            Refresh(b);
            if (b.Gone) { All.RemoveAt(i); continue; }
            // 刃の線分と物の中心の距離
            float px = b.X - x, py = b.Y - y;
            float t = px * co + py * si;
            if (t < 0f) t = 0f; else if (t > len) t = len;
            float qx = px - co * t, qy = py - si * t;
            if (qx * qx + qy * qy > b.Radius * b.Radius) continue;
            b.GuardUntil = now + Regap;
            Apply(b, new Strike(StrikeElement.Slash, 1f, edge, x + co * t, y + si * t, -si, co, by));
        }
    }

    private static StrikeResult Apply(Body b, in Strike s)
    {
        if (b.Skips(s.Element)) return StrikeResult.None;
        var k = b.Kind;
        float hp = b.Hp;
        var res = BodyRules.Judge(s.Element, s.Power, s.Edge, k.Material, k.Size, ref hp);
        if (res == StrikeResult.None) return res;
        // 耐久を持たない物は壊れず、当たった反応だけ出す
        if (k.Hp <= 0f && (res == StrikeResult.Break || res == StrikeResult.Cut || res == StrikeResult.Douse)) res = StrikeResult.Glance;
        bool judge = b.Judges != null && b.Judges();
        // 決めない端末は耐久を減らさない (壊れる・斬れるはその物を決める端末から届く)。当たった見た目だけ出す
        if (!judge && (res == StrikeResult.Break || res == StrikeResult.Cut || res == StrikeResult.Douse)) res = StrikeResult.Damage;
        if (judge) b.Hp = hp;
        Strikes++;
        bool shown = b.OnStrike != null && b.OnStrike(b, res, s);
        if (!shown && res != StrikeResult.Pass) ReportSilent(k, s.Element, res);
        return res;
    }

    private static void ReportSilent(BodyKind k, StrikeElement e, StrikeResult r)
    {
        Silents++;
        int key = k.Name.GetHashCode() ^ (int)e << 8 ^ (int)r << 16;
        if (!Silent.Add(key)) return;
        Plugin.Logger.LogWarning($"SILENT {k.Name} x {e} -> {r}");
    }

    // 地形の爆発の知らせを待たずに、全員の端末で同じ時に爆を当てる (隕石など、地形の爆発より広い・強い物)。
    // 同じ場所から後で届く地形の爆発の知らせは二重に当てない
    internal static void Blast(in Strike s, float r)
    {
        _blastX = s.X;
        _blastY = s.Y;
        _blastUntil = Time.time + BlastDedupe;
        if (All.Count > 0) Circle(s, r);
    }
    private static float _blastX, _blastY, _blastUntil;
    private const float BlastDedupe = 3f;

    // 爆発と叩いた所 (地形の知らせは全員の端末に届く)
    private static void OnTerrainBroken(TerrainBrokenEvent e)
    {
        if (All.Count == 0) return;
        var p = e.Position;
        if (e.Kind == DamageKind.Explosion && Time.time < _blastUntil)
        {
            float dx = p.x - _blastX, dy = p.y - _blastY;
            if (dx * dx + dy * dy < 0.25f) { _blastUntil = 0f; return; }
        }
        if (e.Kind == DamageKind.Explosion)
            Circle(new Strike(StrikeElement.Blast, BlastPower, 0, p.x, p.y, 0f, 0f, e.PlayerId), e.Radius);
        else if (e.Kind == DamageKind.Blunt)
            Circle(new Strike(StrikeElement.Blunt, BluntPower, 0, p.x, p.y, 0f, 0f, e.PlayerId), 0.35f);
    }

    // 水位と火 (0.2 秒ごと)。決める端末だけが見る
    internal static void Tick()
    {
        if (All.Count == 0) return;
        float now = Time.time;
        if (now < _nextWater) return;
        _nextWater = now + WaterEvery;
        bool fire = FireSim.Ready && FireSim.BurningCount > 0;
        for (int i = All.Count - 1; i >= 0; i--)
        {
            if (i >= All.Count) continue; // 反応の中で別の物が外れる (装置は 2 つとも閉じる)
            var b = All[i];
            bool water = b.Kind.Lift >= 0f;
            if (!water && !fire) continue;
            if (b.Judges == null || !b.Judges()) continue;
            Refresh(b);
            if (b.Gone) { All.RemoveAt(i); continue; }
            if (fire && FireTouches(b.X, b.Y, b.Radius))
            {
                Apply(b, new Strike(StrikeElement.Heat, HeatPower, 0, b.X, b.Y, 0f, 0f, 255));
                if (b.Gone) continue;
            }
            if (water && WaterSim.DepthAt(FxMath.V2(b.X, b.Y)) >= (int)(WaterSim.Full * b.Kind.Lift))
                Apply(b, new Strike(StrikeElement.Water, 1f, 0, b.X, b.Y, 0f, 0f, 255));
        }
    }

    // 燃えている升が (x, y) から半径 r に掛かるか
    private static bool FireTouches(float x, float y, float r)
    {
        var org = FireSim.Origin;
        float cell = FireSim.Cell;
        int w = FireSim.W, h = FireSim.H;
        int x0 = FxMath.Max(0, (int)FxMath.Floor((x - r - org.x) / cell)), x1 = FxMath.Min(w - 1, (int)FxMath.Floor((x + r - org.x) / cell));
        int y0 = FxMath.Max(0, (int)FxMath.Floor((y - r - org.y) / cell)), y1 = FxMath.Min(h - 1, (int)FxMath.Floor((y + r - org.y) / cell));
        for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
                if (FireSim.Burning(cy * w + cx)) return true;
        return false;
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder($"bodies n={All.Count} strikes={Strikes} silent={Silents}");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var b in All)
        {
            Refresh(b);
            if (b.Gone) continue;
            sb.Append($" | {b.Kind.Name} at={b.X.ToString("F2", inv)},{b.Y.ToString("F2", inv)} hp={b.Hp.ToString("F1", inv)} judge={(b.Judges != null && b.Judges() ? 1 : 0)}");
        }
        return sb.ToString();
    }

    // 確かめ用: その場所に属性の一撃を当てる (斬は edge の切断力で、横向きの刃を r の長さで振った扱い)
    internal static void Test(StrikeElement e, float x, float y, float r, float power, int edge)
    {
        if (e == StrikeElement.Slash) { Blade(255, edge, x - r * 0.5f, y, 1f, 0f, r); return; }
        if (e == StrikeElement.Water)
        {
            foreach (var b in All.ToArray()) { Refresh(b); float dx = b.X - x, dy = b.Y - y; if (dx * dx + dy * dy <= (b.Radius + r) * (b.Radius + r)) Apply(b, new Strike(e, power, 0, b.X, b.Y, 0f, 0f, 255)); }
            return;
        }
        Circle(new Strike(e, power, edge, x, y, 0f, 0f, 255), r);
    }
}
