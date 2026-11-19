using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MoreRolesPlus.Flight;

// 夜の街の道路を走る車の反応。車は地面のシェーダ (FlightCity.shader の Cars) が時計から描くだけの絵なので、
// ここでは同じ式で「その時刻にその場所にどの車がいるか」を出し、出来事の時だけ車線の表を書き換えてシェーダへ渡す。
// - 弾が当たった車は燃料に火が回って爆ぜ、焼けた殻がそこに残って車線をふさぐ。後ろの車は前から順に詰めて止まる
// - ビルが崩れると、面した道の車はその前で止まる (抜けかけていた車はそのまま走り去る)
// - 近くで爆発があると防犯アラームが鳴って点滅灯が速く点滅する
// - 船が道の真上を低く抜けると、ブレーキ灯が点いてクラクションが鳴る
// ふさがった区間 (交差点と交差点の間) が埋まった後に来た車は、手前の交差点で曲がって来なくなる (渋滞が街じゅうへ伸びない)。
// 毎フレームの Unity の呼び出しは増やさない (時計と船の位置は光の配列の空いた所に入れて、毎フレーム渡している 1 回に乗せる)
internal static unsafe class FlightCars
{
    private const float P = 192f, Lc = 24f, L = 4.6f;
    private const int W = 8, Rows = 1536; // 1 車線 = 1 行 (区間の番号 2 つ + 区間ごとに 2 画素)
    private const float Empty = -1e6f;

    private static Texture2D _tex;
    private static float[] _data = Array.Empty<float>();
    private static bool _on, _dirty;
    private static float _clk, _hornCd, _alarmVol = -1f;
    private static Il2CppStructArray<Vector4> _alm;
    private static readonly float[] AlmX = new float[4], AlmZ = new float[4], AlmR = new float[4], AlmT = new float[4];
    private static int _almNext, _tsIdx = -1;
    private static int _texId, _onId, _almId, _jamId;
    // 渋滞 (ふさがった区間) の一覧。近い 8 つを車の立体の渋滞の枠へ載せる (FlightCarMesh・地面の絵はそれ以外を絵で描く)
    private const int JamSlots = 8;
    private static readonly System.Collections.Generic.List<(int Row, int Slot, float X, float Z)> Jams = new();
    private static readonly int[] JamRow = new int[JamSlots], JamSlot = new int[JamSlots];
    private static Il2CppStructArray<Vector4> _jam;
    private static float _jamCd;
    internal static int Burned { get; private set; }
    internal static Vector3 LastBurn { get; private set; }

    private struct Lane
    {
        public int Row;
        public float Hash, V, Dir, Lat; // 車線の種 (整数)・速さ・向き (+1 = 道に沿った位置が増える向き)・道の中心からの横の位置 (符号つき)
        public int Axis;                 // 0 = z に沿う道 (x = 線の番号 × 192)・1 = x に沿う道
        public float Line;               // 線の位置 (線の番号 × 192)
    }

    internal static void Begin()
    {
        _clk = 0f;
        _hornCd = 2f;
        _almNext = 0;
        Burned = 0;
        for (int i = 0; i < 4; i++) { AlmX[i] = AlmZ[i] = AlmR[i] = 0f; AlmT[i] = -1e6f; }
        _texId = Shader.PropertyToID("_MrpCarTex");
        _onId = Shader.PropertyToID("_MrpCarOn");
        _almId = Shader.PropertyToID("_MrpCarAlm");
        _jamId = Shader.PropertyToID("_MrpCarJam");
        _alm ??= new Il2CppStructArray<Vector4>((long)4);
        _jam ??= new Il2CppStructArray<Vector4>((long)JamSlots);
        PushAlarms();
        Jams.Clear();
        for (int i = 0; i < JamSlots; i++) JamRow[i] = -1;
        PushJams();
        _on = SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat);
        if (!_on)
        {
            // 浮動小数の表を持てない端末では車は止まらない (撃っても路面に当たるだけ)
            Shader.SetGlobalFloat(_onId, 0f);
            return;
        }
        if (_data.Length != W * Rows * 4) _data = new float[W * Rows * 4];
        Array.Clear(_data, 0, _data.Length);
        for (int r = 0; r < Rows; r++) { _data[r * W * 4] = Empty; _data[r * W * 4 + 1] = Empty; }
        if (!_tex)
        {
            _tex = new Texture2D(W, Rows, TextureFormat.RGBAFloat, false, true)
            {
                name = "MrpCarLanes",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
        }
        _dirty = true;
        Upload();
        Shader.SetGlobalTexture(_texId, _tex);
        Shader.SetGlobalFloat(_onId, 1f);
    }

    internal static void End()
    {
        if (_alarmVol > 0f) FlightSound.CarAlarm(0f);
        _alarmVol = -1f;
        if (_onId != 0) Shader.SetGlobalFloat(_onId, 0f);
        _on = false;
    }

    private static void Upload()
    {
        if (!_dirty || !_tex) return;
        _dirty = false;
        fixed (float* p = _data) _tex.LoadRawTextureData((IntPtr)p, _data.Length * 4);
        _tex.Apply(false, false);
    }

    // 毎フレーム (Arena が光の配列を渡す直前): 奇数番の w に場面の時計と自分の船の位置 (街の xz・地面からの高さ) を入れる。
    // 光は 4 つ × 8 個 (FlightNative.OutLights) の並びが前提 (シェーダの _MrpFltLights[1/3/5/7].w と揃える)
    internal static void WriteSlots(float* lights, Vector3 ship)
    {
        lights[7] = _clk;
        lights[15] = ship.x;
        lights[23] = ship.z;
        lights[31] = ship.y - FlightCity.Ground;
    }

    // 毎フレーム (街の場面の間だけ)
    internal static void Tick(float dt)
    {
        // 確かめ用の車の時計の速さ (carTime・0 で止めて描いた車と式の車を見比べる)
        if (_tsIdx < 0) _tsIdx = FlightParams.Find("carTime");
        _clk += dt * (_tsIdx >= 0 ? FlightParams.Values[_tsIdx] : 1f);
        Upload();
        Vector3 ship = Arena.LocalShipPos;
        FlightCarMesh.Tick(ship.y - FlightCity.Ground);
        _jamCd -= dt;
        if (_jamCd <= 0f && Jams.Count > 0)
        {
            _jamCd = 0.25f;
            PickJams(ship);
        }
        TickHorn(dt, ship);
        TickAlarmSound(ship);
    }

    // ---- 車の位置 (FlightCity.shader の Cars と同じ式) ----

    private static float Frac(float x) => x - MathF.Floor(x);

    private static float Hash12(float px, float py)
    {
        float qx = Frac(px * 0.1031f), qy = Frac(py * 0.1031f), qz = qx;
        float d = qx * (qy + 33.33f) + qy * (qz + 33.33f) + qz * (qx + 33.33f);
        qx += d; qy += d; qz += d;
        return Frac((qx + qy) * qz);
    }

    // 街の点 (qx, qz) を通る axis の道の車線。車道の外なら false
    private static bool LaneAt(float qx, float qz, int axis, out Lane ln, out float p, out float x)
    {
        ln = default; p = 0f; x = 0f;
        float c = axis == 0 ? qx : qz, a = axis == 0 ? qz : qx;
        float li = MathF.Floor(c / P + 0.5f);
        float lat = c - li * P;
        bool wide = Math.Abs(li) % 4f < 0.5f;
        float d = Math.Abs(lat);
        if (d > (wide ? 39f : 9.5f)) return false;
        float dir = lat > 0f ? -1f : 1f;
        float k = wide ? Math.Clamp(MathF.Floor((d - 5f) / 5.6f), 0f, 5f) : 0f;
        float center = wide ? 5f + 5.6f * (k + 0.5f) : 4.75f;
        x = d - center;
        if (Math.Abs(x) > 2.4f || li < -32f || li > 31f) return false;
        float id = axis == 0 ? li : li + 517f;
        ln.Hash = id * 13f + k + (dir > 0f ? 0f : 7f) + (wide ? 8000f : 0f);
        ln.V = (wide ? 14f : 9f) + 6f * Frac(ln.Hash * 0.618034f);
        ln.Dir = dir;
        ln.Lat = lat > 0f ? center : -center;
        ln.Axis = axis;
        ln.Line = li * P;
        ln.Row = (int)(((axis * 64 + (int)li + 32) * 2 + (dir > 0f ? 0 : 1)) * 6 + k);
        p = a * dir;
        return true;
    }

    // 車線の道に沿った位置 p にいる車の場所 (街の xz)
    private static Vector3 World(in Lane ln, float pAlong)
    {
        float a = pAlong * ln.Dir;
        float lat = ln.Line + ln.Lat;
        return ln.Axis == 0 ? new Vector3(lat, FlightCity.Ground, a) : new Vector3(a, FlightCity.Ground, lat);
    }

    private static float Clock(Vector3 q)
    {
        Vector3 o = Arena.OriginPos;
        return _clk - FlightCity.CarLostAt(q.x + o.x, q.z + o.z);
    }

    private enum Kind { None, Free, Queued, Shell }

    private struct Car
    {
        public Kind Kind;
        public float Cell, Rear; // 識別 (升の番号)・今の後ろの端 (道に沿った位置)
        public int Slot;          // ふさがった区間の表の枠 (-1 = 無い)
    }

    private static int Idx(int row, int texel) => (row * W + texel) * 4;

    // 車線 ln の位置 p を覆う車 (前後に tol だけ広く見る)
    private static Car CarAt(in Lane ln, float p, float T, float tol)
    {
        var car = new Car { Kind = Kind.None, Slot = -1 };
        float lo = -1e9f;
        if (_on)
        {
            int h = Idx(ln.Row, 0);
            float seg = MathF.Floor(p / P);
            int slot = _data[h] == seg ? 0 : (_data[h + 1] == seg ? 1 : -1);
            if (slot >= 0)
            {
                int a = Idx(ln.Row, 1 + slot * 2), b = Idx(ln.Row, 2 + slot * 2);
                float pb = _data[a], len = _data[a + 1], tb = _data[a + 2];
                bool crush = _data[a + 3] > 0.5f;
                float u0 = pb - ln.V * tb;
                car.Slot = slot;
                if (p < pb)
                {
                    float dT = Math.Max(T - tb, 0f);
                    float K = Math.Max(ln.V * ln.V / 20f, 1f);
                    float gap0 = crush ? 2.5f : 4f;
                    float cf = MathF.Floor((u0 - L) / Lc);
                    float edge = seg * P + (Math.Abs(seg) % 4f < 0.5f ? 48f : 15f) + 1f;
                    float st = 0f;
                    int idx = 0;
                    for (int j = 0; j < 18; j++)
                    {
                        float c = cf - j;
                        if (Hash12(c, ln.Hash) > 0.42f) continue;
                        float r = c * Lc + 1f + Hash12(c + 1291f, ln.Hash) * 16f;
                        if (r > u0 - L) continue;
                        float xtb = pb + r - u0;
                        st = idx == 0 ? pb - L - Math.Min(gap0, Math.Max(pb - xtb - L, 0f)) : st - L - (1.5f + 1.5f * Hash12(c + 3637f, ln.Hash));
                        if (st < edge) break;
                        idx++;
                        float kk = Math.Max(Math.Min(Math.Min(K, st - xtb), st - seg * P + 0.5f), 0.05f);
                        float f = xtb + ln.V * dT;
                        float z = f - st;
                        float xc = f - (z <= -kk ? 0f : (z < kk ? (z + kk) * (z + kk) / (4f * kk) : z));
                        float yy = p - xc;
                        if (yy >= -tol && yy <= L + tol)
                        {
                            car.Cell = c;
                            car.Rear = xc;
                            float bi = c - (MathF.Floor(u0 / Lc) - 18f);
                            bool dead = bi >= 0f && bi < 24f && ((int)_data[b] >> (int)bi & 1) != 0;
                            car.Kind = dead ? Kind.Shell : Kind.Queued;
                            return car;
                        }
                        if (xc + L < p - 2f) break;
                    }
                    return car;
                }
                lo = crush ? u0 + len : u0 - L;
                if (crush && p < pb + L + tol)
                {
                    car.Kind = Kind.Shell;
                    car.Rear = pb;
                    car.Cell = MathF.Floor(u0 / Lc);
                    return car;
                }
            }
        }
        float u = p - ln.V * T;
        // 当たりを広げた分、隣の升の車も見る
        for (int dc = 0; dc <= 1; dc++)
        {
            float cell = MathF.Floor((u + tol) / Lc) - dc;
            if (Hash12(cell, ln.Hash) > 0.42f) continue;
            float off = 1f + Hash12(cell + 1291f, ln.Hash) * 16f;
            float r = cell * Lc + off;
            if (r <= lo) continue;
            float y = u - r;
            if (y < -tol || y > L + tol) continue;
            car.Kind = Kind.Free;
            car.Cell = cell;
            car.Rear = r + ln.V * T;
            return car;
        }
        return car;
    }

    // ---- 弾が地面に当たった (q = 街の点) ----

    // 弾が路面に落ちた所 q と撃った所 (fx, fy, fz)。車体の高さ (1.5) より下を通った区間を、撃った側から 3.5 刻みで見る
    // (車の長さ 4.6 と当たりの前後の広げ 1.2 で隙間が出ない刻み)
    internal static bool Hit(Vector3 q, float fx, float fy, float fz)
    {
        float up = fy - q.y;
        float hx = fx - q.x, hz = fz - q.z;
        float hd = MathF.Sqrt(hx * hx + hz * hz);
        if (up <= 0.5f || hd < 1f) return Hit(q);
        float back = Math.Min(1.5f * hd / up, 60f);
        int n = Math.Min((int)MathF.Ceiling(back / 3.5f), 18);
        for (int i = n; i >= 0; i--)
        {
            float k = back * i / Math.Max(n, 1) / hd;
            if (Hit(new Vector3(q.x + hx * k, q.y, q.z + hz * k))) return true;
        }
        return false;
    }

    // 車に当たれば燃やして true
    internal static bool Hit(Vector3 q)
    {
        // 表を持てない端末では殻もふさがりも描けないので、路面に当たっただけにする
        if (!FlightCity.Active || !_on) return false;
        float T = Clock(q);
        for (int axis = 0; axis < 2; axis++)
            for (int s = 0; s < 3; s++)
            {
                // 車線の横へも少し広く (光弾は太いので、車の脇をかすめても当てる)
                float side = s == 0 ? 0f : (s == 1 ? 1.4f : -1.4f);
                float qx = axis == 0 ? q.x + side : q.x, qz = axis == 0 ? q.z : q.z + side;
                if (!LaneAt(qx, qz, axis, out var ln, out float p, out _)) continue;
                var car = CarAt(ln, p, T, 1.2f);
                if (car.Kind == Kind.None || car.Kind == Kind.Shell) continue;
                if (!Kill(ln, car, T)) continue;
                Vector3 at = World(ln, car.Rear + L * 0.5f);
                Burst(at, 70f);
                Burned++;
                LastBurn = at;
                float* c = stackalloc float[3];
                c[0] = at.x; c[1] = at.y; c[2] = at.z;
                IntPtr w = FlightCity.WorldPtr;
                if (w != IntPtr.Zero) FlightNative.Car(w, c, 1);
                FlightSound.CarBoom(Volume(at, 1400f));
                return true;
            }
        return false;
    }

    private static bool Kill(in Lane ln, Car car, float T)
    {
        int h = Idx(ln.Row, 0);
        if (car.Kind == Kind.Queued)
        {
            int b = Idx(ln.Row, 2 + car.Slot * 2);
            int a = Idx(ln.Row, 1 + car.Slot * 2);
            float u0 = _data[a] - ln.V * _data[a + 2];
            int bi = (int)(car.Cell - (MathF.Floor(u0 / Lc) - 18f));
            if (bi < 0 || bi >= 24) return false;
            _data[b] = (float)((int)_data[b] | (1 << bi));
            _data[b + 1] = _clk;
            _dirty = true;
            return true;
        }
        // 走っている車: その区間にまだふさがりが無ければ、焼けた殻がふさぐ
        float seg = MathF.Floor(car.Rear / P);
        if (_data[h] == seg || _data[h + 1] == seg) return false; // 同じ区間の先へ抜けていく車 (ふさぎは 1 区間に 1 つ)
        int slot = _data[h] == Empty ? 0 : (_data[h + 1] == Empty ? 1 : -1);
        if (slot < 0) return false;
        Block(ln, slot, seg, car.Rear, L, T, true);
        return true;
    }

    private static void Block(in Lane ln, int slot, float seg, float pb, float len, float T, bool crush)
    {
        int h = Idx(ln.Row, 0), a = Idx(ln.Row, 1 + slot * 2), b = Idx(ln.Row, 2 + slot * 2);
        _data[h + slot] = seg;
        _data[a] = pb; _data[a + 1] = len; _data[a + 2] = T; _data[a + 3] = crush ? 1f : 0f;
        _data[b] = 0f; _data[b + 1] = _clk; _data[b + 2] = _clk; _data[b + 3] = 0f;
        _dirty = true;
        Vector3 at = World(ln, pb);
        Jams.Add((ln.Row, slot, at.x, at.z));
        _jamCd = 0f;
    }

    // 船に近い渋滞から 8 つを車の立体へ (変わった時だけ渡す)
    private static void PickJams(Vector3 ship)
    {
        Span<int> best = stackalloc int[JamSlots];
        Span<float> bd = stackalloc float[JamSlots];
        int n = 0;
        for (int i = 0; i < Jams.Count; i++)
        {
            float dx = Jams[i].X - ship.x, dz = Jams[i].Z - ship.z;
            float d = dx * dx + dz * dz;
            int k = n < JamSlots ? n++ : JamSlots;
            if (k == JamSlots)
            {
                if (d >= bd[JamSlots - 1]) continue;
                k = JamSlots - 1;
            }
            while (k > 0 && bd[k - 1] > d) { bd[k] = bd[k - 1]; best[k] = best[k - 1]; k--; }
            bd[k] = d;
            best[k] = i;
        }
        // 前と同じ渋滞は同じ枠に残す (枠が入れ替わると細かい形と粗い形が切り替わってちらつく)
        Span<bool> used = stackalloc bool[JamSlots];
        Span<int> next = stackalloc int[JamSlots];
        for (int s = 0; s < JamSlots; s++) next[s] = -1;
        for (int r = 0; r < n; r++)
        {
            var j = Jams[best[r]];
            for (int s = 0; s < JamSlots; s++)
                if (JamRow[s] == j.Row && JamSlot[s] == j.Slot) { next[s] = best[r]; used[r] = true; break; }
        }
        for (int r = 0; r < n; r++)
        {
            if (used[r]) continue;
            for (int s = 0; s < JamSlots; s++)
                if (next[s] < 0) { next[s] = best[r]; break; }
        }
        bool changed = false;
        for (int s = 0; s < JamSlots; s++)
        {
            int row = next[s] < 0 ? -1 : Jams[next[s]].Row, slot = next[s] < 0 ? 0 : Jams[next[s]].Slot;
            if (row == JamRow[s] && slot == JamSlot[s]) continue;
            JamRow[s] = row;
            JamSlot[s] = slot;
            changed = true;
        }
        if (changed) PushJams();
    }

    private static void PushJams()
    {
        if (_jam == null) return;
        for (int i = 0; i < JamSlots; i++) _jam[i] = new Vector4(JamRow[i], JamSlot[i], 0f, 0f);
        Shader.SetGlobalVectorArray(_jamId, _jam);
    }

    // ---- ビルが崩れた (足元の x0 z0 x1 z1・街の座標) ----

    internal static void OnCollapse(float x0, float z0, float x1, float z1)
    {
        if (!FlightCity.Active) return;
        float cx = (x0 + x1) * 0.5f, cz = (z0 + z1) * 0.5f;
        Alarm(cx, cz, Math.Max(x1 - x0, z1 - z0) * 0.5f + 90f);
        if (!_on) return;
        // 面した道の全車線で、ビルの前 (前後に 12 ずつ広く) の手前で止まる
        float ix = MathF.Floor(cx / P), iz = MathF.Floor(cz / P);
        for (int axis = 0; axis < 2; axis++)
            for (int e = 0; e < 2; e++)
            {
                float line = ((axis == 0 ? ix : iz) + e) * P;
                float a0 = (axis == 0 ? z0 : x0) - 12f, a1 = (axis == 0 ? z1 : x1) + 12f;
                bool wide = Math.Abs(MathF.Round(line / P)) % 4f < 0.5f;
                // 間に別の敷地を挟む道 (ビルの面から歩道の端まで 35 より遠い) は止めない
                float n0 = axis == 0 ? x0 : z0, n1 = axis == 0 ? x1 : z1;
                if (Math.Min(Math.Abs(n0 - line), Math.Abs(n1 - line)) - (wide ? 48f : 15f) > 35f) continue;
                int lanes = wide ? 6 : 1;
                for (int side = 0; side < 2; side++)
                    for (int k = 0; k < lanes; k++)
                    {
                        float center = wide ? 5f + 5.6f * (k + 0.5f) : 4.75f;
                        float lat = line + (side == 0 ? -center : center); // side 0 = 横の位置が負 = 道に沿って増える向き
                        float am = (a0 + a1) * 0.5f;
                        float qx = axis == 0 ? lat : am, qz = axis == 0 ? am : lat;
                        if (!LaneAt(qx, qz, axis, out var ln, out _, out _)) continue;
                        float pb = ln.Dir > 0f ? a0 : -a1;
                        float seg = MathF.Floor(pb / P);
                        int h = Idx(ln.Row, 0);
                        if (_data[h] == seg || _data[h + 1] == seg) continue;
                        int slot = _data[h] == Empty ? 0 : (_data[h + 1] == Empty ? 1 : -1);
                        if (slot < 0) continue;
                        float len = Math.Min(a1 - a0, (seg + 1f) * P - pb - 1f);
                        Block(ln, slot, seg, pb, len, Clock(World(ln, pb)), false);
                    }
            }
    }

    // ---- 防犯アラーム ----

    // 爆発の周り (半径 r) の車のアラームが鳴る (古い物から 4 つまで)
    internal static void Burst(Vector3 q, float r) => Alarm(q.x, q.z, r);

    private static void Alarm(float x, float z, float r)
    {
        int i = _almNext;
        _almNext = (_almNext + 1) % 4;
        AlmX[i] = x; AlmZ[i] = z; AlmR[i] = r; AlmT[i] = _clk;
        PushAlarms();
    }

    private static void PushAlarms()
    {
        if (_alm == null) return;
        for (int i = 0; i < 4; i++) _alm[i] = new Vector4(AlmX[i], AlmZ[i], AlmR[i], AlmT[i]);
        Shader.SetGlobalVectorArray(_almId, _alm);
    }

    // アラームの音: 一番近くで鳴っている所の距離で大きさを決める (38 秒で止む・値が変わった時だけ書く)
    private static void TickAlarmSound(Vector3 ship)
    {
        float best = 0f;
        for (int i = 0; i < 4; i++)
        {
            float age = _clk - AlmT[i];
            if (age < 0f || age > 38f) continue;
            float dx = ship.x - AlmX[i], dz = ship.z - AlmZ[i];
            float d = MathF.Sqrt(dx * dx + dz * dz + (ship.y - FlightCity.Ground) * (ship.y - FlightCity.Ground));
            float v = Math.Clamp(1f - (d - AlmR[i]) / 700f, 0f, 1f) * Math.Clamp((38f - age) / 4f, 0f, 1f) * Math.Clamp(age / 0.6f, 0f, 1f);
            if (v > best) best = v;
        }
        float vol = 0.32f * best;
        if (vol <= 0.005f) vol = 0f;
        if (Math.Abs(vol - _alarmVol) < 0.01f && (vol == 0f) == (_alarmVol <= 0f)) return;
        _alarmVol = vol;
        FlightSound.CarAlarm(vol);
    }

    // ---- 低く抜けた時のクラクション ----

    private static void TickHorn(float dt, Vector3 ship)
    {
        _hornCd -= dt;
        if (_hornCd > 0f) return;
        float h = ship.y - FlightCity.Ground;
        if (h > 75f) return;
        bool over = LaneAt(ship.x, ship.z, 0, out _, out _, out _) || LaneAt(ship.x, ship.z, 1, out _, out _, out _);
        if (!over)
        {
            // 車道の真上でなくても、すぐ横に車道があれば鳴る
            over = LaneAt(ship.x + 12f, ship.z, 0, out _, out _, out _) || LaneAt(ship.x - 12f, ship.z, 0, out _, out _, out _)
                || LaneAt(ship.x, ship.z + 12f, 1, out _, out _, out _) || LaneAt(ship.x, ship.z - 12f, 1, out _, out _, out _);
            if (!over) return;
        }
        float k = Math.Clamp(1f - h / 75f, 0f, 1f);
        FlightSound.Horn(0.25f + 0.45f * k, h < 40f);
        _hornCd = 1.6f + 2.2f * UnityEngine.Random.value;
    }

    private static float Volume(Vector3 q, float range)
    {
        Vector3 s = Arena.LocalShipPos;
        float dx = s.x - q.x, dy = s.y - q.y, dz = s.z - q.z;
        return Math.Clamp(1f - MathF.Sqrt(dx * dx + dy * dy + dz * dz) / range, 0f, 1f);
    }

    // ---- 確かめ用 ----

    // 船の真下の周り (半径 r) で、ここが「いる」と見ている車の真ん中に緑の印を出す (描かれた車と重なれば式が揃っている)
    internal static int Mark(float r)
    {
        IntPtr w = FlightCity.WorldPtr;
        if (w == IntPtr.Zero) return 0;
        Vector3 s = Arena.LocalShipPos;
        int n = 0;
        float* c = stackalloc float[3];
        for (int axis = 0; axis < 2; axis++)
            for (float t = -r; t <= r; t += 2f)
                for (float latOff = -40f; latOff <= 40f; latOff += 0.5f)
                {
                    float qx = axis == 0 ? MathF.Floor(s.x / P + 0.5f) * P + latOff : s.x + t;
                    float qz = axis == 0 ? s.z + t : MathF.Floor(s.z / P + 0.5f) * P + latOff;
                    if (!LaneAt(qx, qz, axis, out var ln, out float p, out float x) || Math.Abs(x) > 0.25f) continue;
                    var car = CarAt(ln, p, Clock(new Vector3(qx, 0f, qz)), 0f);
                    if (car.Kind == Kind.None || Math.Abs(p - (car.Rear + L * 0.5f)) > 1f) continue;
                    Vector3 at = World(ln, car.Rear + L * 0.5f);
                    c[0] = at.x; c[1] = at.y + 0.5f; c[2] = at.z;
                    FlightNative.Car(w, c, 0);
                    n++;
                }
        return n;
    }

    // 点 (街の xz) の周り (前後 r) で、横の位置 lat (道の中心から・符号つき) の車線の車を 1 台撃ったことにする
    internal static bool DebugHit(float x, float z, float lat, float r)
    {
        int axis = Math.Abs(x - MathF.Floor(x / P + 0.5f) * P) < Math.Abs(z - MathF.Floor(z / P + 0.5f) * P) ? 0 : 1;
        for (float t = 0f; t <= r; t += 1f)
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                float qx = axis == 0 ? MathF.Floor(x / P + 0.5f) * P + lat : x + t * sgn;
                float qz = axis == 0 ? z + t * sgn : MathF.Floor(z / P + 0.5f) * P + lat;
                if (Hit(new Vector3(qx, FlightCity.Ground, qz))) return true;
            }
        return false;
    }
}
