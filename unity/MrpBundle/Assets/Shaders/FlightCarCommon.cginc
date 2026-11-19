// 夜の街の車の立体 (FlightCar・FlightCarGlow) が共有する置き方。
// 車は地面のシェーダ (FlightCity の Cars) が時計から描く絵と同じ式で決まる。ここでは向きを逆にして
// 「車線と升の番号 → その車の今の位置と状態」を出し、カメラの周りの決まった数の枠へ頂点シェーダが置く。
// 枠 (頂点の TEXCOORD0.w = 番号) = 種類 × 4096 + 軸 × 2048 + 車線の枠 × 64 + 升の枠:
//   種類 0 = 近く (半径 64・道 2 本・細かい形)・1 = 遠く (半径 160・道 3 本・粗い形)・2 = 渋滞 (車線の枠 = 渋滞の番号 0..7・升の枠 = 並びの順 0..17 / 18 = ふさいだ殻)
// 道の番号・車線・向き・速さ・升の決まりは FlightCity.shader の Cars と FlightCars.cs と同じ (変えるなら 3 か所を揃える)。
#ifndef MRP_FLIGHT_CAR_COMMON
#define MRP_FLIGHT_CAR_COMMON

float4 _MrpFltSky;
float4 _MrpFltLights[8];
float4 _MrpCity;
float4 _MrpCityAlarm;
float4 _MrpCityOrg;
sampler2D _MrpCarTex;
float _MrpCarOn;
float4 _MrpCarAlm[4];
float4 _MrpCarJam[8]; // 渋滞の枠へ載せる車線の行 (x・無ければ負)・区間の枠 (y)。近い順 (0..2 は細かい形も持つ)

#define CAR_P 192.0
#define CAR_LC 24.0
#define CAR_L 4.6
#define CAR_NEAR_R 64.0
#define CAR_LOD_IN 56.0
#define CAR_FAR_R 160.0
#define CAR_FLAT 30.0

float Hash12(float2 p)
{
    float3 q = frac(float3(p.xyx) * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return frac((q.x + q.y) * q.z);
}

float Ramp2(float x) { return x <= 0.0 ? 0.0 : (x < 3.0 ? x * x / 6.0 : x - 1.5); }

float4 CarTexel(float row, float k)
{
    return tex2Dlod(_MrpCarTex, float4((k + 0.5) / 8.0, (row + 0.5) / 1536.0, 0, 0));
}

float Bit(float m, float i) { return fmod(floor(m / exp2(i)), 2.0); }

struct CarLane
{
    float axis;  // 0 = z に沿う道 (x = 線)・1 = x に沿う道
    float baseLine; // 線の位置 (番号 × 192)
    float latc;  // 車線の中心の線からの横の位置 (符号つき)
    float dir;   // +1 = 道に沿った位置が増える向きへ走る
    float hash;  // 車線の種 (整数)
    float v;     // 速さ
    float row;   // 止まりの表の行
    float wide;
};

CarLane MakeLane(float axis, float n, float k, float dirbit)
{
    CarLane ln;
    ln.axis = axis;
    ln.wide = step(fmod(abs(n), 4.0), 0.5);
    ln.dir = dirbit < 0.5 ? 1.0 : -1.0;
    float center = ln.wide > 0.5 ? 5.0 + 5.6 * (k + 0.5) : 4.75;
    ln.latc = ln.dir > 0.0 ? -center : center; // 左側通行: 線より横の位置が負の側は道に沿って増える向き
    ln.baseLine = n * CAR_P;
    float id = axis < 0.5 ? n : n + 517.0;
    ln.hash = id * 13.0 + k + (ln.dir > 0.0 ? 0.0 : 7.0) + ln.wide * 8000.0;
    ln.v = (ln.wide > 0.5 ? 14.0 : 9.0) + 6.0 * frac(ln.hash * 0.618034);
    ln.row = ((axis * 64.0 + n + 32.0) * 2.0 + dirbit) * 6.0 + k;
    return ln;
}

// 車線の道に沿った位置 p (走る向きの座標) の、街の xz
float2 LaneCity(CarLane ln, float p)
{
    float a = p * ln.dir;
    float lat = ln.baseLine + ln.latc;
    return ln.axis < 0.5 ? float2(lat, a) : float2(a, lat);
}

// その場所の車の時計 (停電で止まっていた分を引く) と、止まっている度合い
float CarClock(float2 cityXZ, out float stopped)
{
    float2 wxz = cityXZ + _MrpCityOrg.xy;
    float s = _MrpCityAlarm.x;
    float cut = min(length(wxz - _MrpCityAlarm.zw) / 900.0, 2.0);
    float B = _MrpCityAlarm.y - 1.0;
    stopped = saturate((s - cut) / 3.0) * (1.0 - saturate((s - cut - B) / 3.0));
    return _MrpFltLights[1].w - _MrpCity.w - (Ramp2(s - cut) - Ramp2(s - cut - B));
}

struct CarInfo
{
    float ok;
    float rear;    // 後ろの端 (走る向きの座標)
    float cell;    // 識別 (升の番号)
    float tone;
    float brake;
    float burn;    // 0 = 普通・それ以外 = 焼けた殻 (大きいほどまだ燃えている)
    float stopped; // 停電で止まっている度合い
};

CarInfo NoCar()
{
    CarInfo c;
    c.ok = 0; c.rear = 0; c.cell = 0; c.tone = 0; c.brake = 0; c.burn = 0; c.stopped = 0;
    return c;
}

// 走っている車: 窓の真ん中 (走る向きの座標 pc) から後ろへ R 手前の升から数えて j 番目の升の車
CarInfo FreeCar(CarLane ln, float j, float pc, float R)
{
    CarInfo c = NoCar();
    float st;
    float T0 = CarClock(LaneCity(ln, pc), st);
    float cell = floor((pc - R - CAR_L - ln.v * T0) / CAR_LC) - 1.0 + j;
    if (Hash12(float2(cell, ln.hash)) > 0.42) return c;
    float r = cell * CAR_LC + 1.0 + Hash12(float2(cell + 1291.0, ln.hash)) * 16.0;
    // 時計は車のいる所で測り直す (停電の前線は場所でずれる)
    float T = CarClock(LaneCity(ln, r + ln.v * T0 + 2.3), st);
    float rear = r + ln.v * T;
    if (_MrpCarOn > 0.5)
    {
        float4 hd = CarTexel(ln.row, 0);
        float seg = floor(rear / CAR_P);
        float slot = hd.x == seg ? 0.0 : (hd.y == seg ? 1.0 : -1.0);
        if (slot >= 0.0)
        {
            float4 A = CarTexel(ln.row, 1.0 + slot * 2.0);
            // ふさいだ所の手前へ入った車は渋滞の枠が描く (並べなかった車は交差点で曲がって来ない)
            if (rear < A.x) return c;
            float u0 = A.x - ln.v * A.z;
            float lo = A.w > 0.5 ? u0 + A.y : u0 - CAR_L;
            if (r <= lo) return c;
        }
    }
    c.ok = 1;
    c.rear = rear;
    c.cell = cell;
    c.tone = Hash12(float2(cell + 2477.0, ln.hash));
    c.stopped = st;
    return c;
}

// 渋滞の車: 枠 J の車線の区間で、ふさいだ所から後ろへ j 番目 (0..17) の升の車・18 = ふさいだ殻
CarInfo JamCar(float4 J, float j, out CarLane ln)
{
    CarInfo c = NoCar();
    float row = max(J.x, 0.0);
    float k = fmod(row, 6.0);
    float t = floor(row / 6.0);
    float dirbit = fmod(t, 2.0);
    float t2 = floor(t / 2.0);
    float axis = t2 >= 64.0 ? 1.0 : 0.0;
    ln = MakeLane(axis, t2 - axis * 64.0 - 32.0, k, dirbit);
    if (J.x < 0.0 || _MrpCarOn < 0.5) return c;
    float slot = J.y;
    float4 hd = CarTexel(row, 0);
    float seg = slot < 0.5 ? hd.x : hd.y;
    float4 A = CarTexel(row, 1.0 + slot * 2.0);
    float4 Bk = CarTexel(row, 2.0 + slot * 2.0);
    float pb = A.x, Tb = A.z, crush = A.w;
    float u0 = pb - ln.v * Tb;
    float st;
    float T = CarClock(LaneCity(ln, pb), st);
    c.stopped = st;
    float clk = _MrpFltLights[1].w;
    if (j > 17.5)
    {
        if (crush < 0.5) return c;
        c.ok = 1;
        c.rear = pb;
        c.cell = floor(u0 / CAR_LC);
        c.tone = Hash12(float2(c.cell + 2477.0, ln.hash));
        c.burn = saturate(1.0 - (clk - Bk.z) / 40.0) + 0.02;
        return c;
    }
    float dT = max(T - Tb, 0.0);
    float K = max(ln.v * ln.v / 20.0, 1.0);
    float gap0 = crush > 0.5 ? 2.5 : 4.0;
    float cf = floor((u0 - CAR_L) / CAR_LC);
    float edge = seg * CAR_P + (fmod(abs(seg), 4.0) < 0.5 ? 48.0 : 15.0) + 1.0;
    float stp = 0, idx = 0;
    int jt = (int)(j + 0.5);
    [loop] for (int jj = 0; jj < 18; jj++)
    {
        float cc = cf - jj;
        bool exists = Hash12(float2(cc, ln.hash)) <= 0.42;
        float r = cc * CAR_LC + 1.0 + Hash12(float2(cc + 1291.0, ln.hash)) * 16.0;
        if (exists && r <= u0 - CAR_L)
        {
            float xtb = pb + r - u0;
            stp = idx < 0.5 ? pb - CAR_L - min(gap0, max(pb - xtb - CAR_L, 0.0)) : stp - CAR_L - (1.5 + 1.5 * Hash12(float2(cc + 3637.0, ln.hash)));
            if (stp < edge) return c;
            idx += 1.0;
            if (jj == jt)
            {
                float kk = max(min(min(K, stp - xtb), stp - seg * CAR_P + 0.5), 0.05);
                float f = xtb + ln.v * dT;
                // まだ区間に入っていない車は、走っている車の枠が描く
                if (f < seg * CAR_P) return c;
                float z = f - stp;
                c.ok = 1;
                c.rear = f - (z <= -kk ? 0.0 : (z < kk ? (z + kk) * (z + kk) / (4.0 * kk) : z));
                c.cell = cc;
                c.tone = Hash12(float2(cc + 2477.0, ln.hash));
                c.brake = saturate((z + kk) / (2.0 * kk));
                float bi = cc - (floor(u0 / CAR_LC) - 18.0);
                if (bi >= 0.0 && bi < 24.0 && Bit(Bk.x, bi) > 0.5) c.burn = saturate(1.0 - (clk - Bk.y) / 40.0) + 0.02;
                return c;
            }
        }
        else if (jj == jt) return c;
    }
    return c;
}

// 枠の番号から、その枠の車線と車を決める。center = 車の真ん中 (街の xz)・fwd = 走る向き (xz)
CarInfo PlaceCar(float entry, out CarLane ln, out float kind)
{
    ln = MakeLane(0.0, 0.0, 0.0, 0.0);
    kind = floor(entry / 4096.0);
    float rest = entry - kind * 4096.0;
    float axis = floor(rest / 2048.0);
    rest -= axis * 2048.0;
    float ls = floor(rest / 64.0);
    float j = rest - ls * 64.0;
    if (kind > 1.5)
    {
        int ji = (int)(ls + 0.5);
        return JamCar(_MrpCarJam[ji], j, ln);
    }
    float2 cq = _WorldSpaceCameraPos.xz - _MrpCityOrg.xy;
    float c = axis < 0.5 ? cq.x : cq.y;
    float along = axis < 0.5 ? cq.y : cq.x;
    float n0 = floor(c / CAR_P + 0.5);
    // 近くは近い道 2 本・遠くは 3 本
    float slots = kind < 0.5 ? 2.0 : 3.0;
    float other = c - n0 * CAR_P >= 0.0 ? 1.0 : -1.0;
    float roads[3];
    if (kind < 0.5) { roads[0] = n0; roads[1] = n0 + other; roads[2] = n0 + other; }
    else { roads[0] = n0 - 1.0; roads[1] = n0; roads[2] = n0 + 1.0; }
    float n, k, dirbit;
    bool skip = false;
    if (ls < slots * 2.0)
    {
        int ri = (int)floor(ls / 2.0);
        n = roads[ri];
        dirbit = fmod(ls, 2.0);
        k = 0.0;
        skip = fmod(abs(n), 4.0) < 0.5; // 広い通りは下の枠が描く
    }
    else
    {
        float w = ls - slots * 2.0;
        k = fmod(w, 6.0);
        dirbit = floor(w / 6.0);
        n = 1e6;
        for (int i = 0; i < 3; i++)
            if (i < (int)slots && fmod(abs(roads[i]), 4.0) < 0.5) n = roads[i];
        skip = n > 1e5;
    }
    // 街の外の道 (止まりの表の行が無い) は描かない
    skip = skip || n < -32.0 || n > 31.0;
    ln = MakeLane(axis, skip ? 0.0 : n, k, dirbit);
    if (skip) return NoCar();
    return FreeCar(ln, j, along * ln.dir, kind < 0.5 ? CAR_NEAR_R : CAR_FAR_R);
}

// 車の向きと真ん中 (世界)
void CarFrame(CarLane ln, CarInfo car, out float3 center, out float2 fwd)
{
    float2 q = LaneCity(ln, car.rear + CAR_L * 0.5);
    center = float3(q.x + _MrpCityOrg.x, _MrpCity.y, q.y + _MrpCityOrg.y);
    fwd = ln.axis < 0.5 ? float2(0.0, ln.dir) : float2(ln.dir, 0.0);
}

// 点滅灯 (停電で止まった時 1.5Hz・防犯アラームで 2.4Hz) と、船が真上を低く抜ける時のブレーキ
void CarSignals(inout CarInfo car, float3 center, out float blink)
{
    float clk = _MrpFltLights[1].w;
    float2 lq = center.xz - _MrpCityOrg.xy;
    float alarm = 0;
    [unroll] for (int m = 0; m < 4; m++)
    {
        float2 dd = lq - _MrpCarAlm[m].xy;
        float age = clk - _MrpCarAlm[m].w;
        alarm = max(alarm, step(dot(dd, dd), _MrpCarAlm[m].z * _MrpCarAlm[m].z) * step(0.0, age) * step(age, 38.0) * step(Hash12(float2(car.tone * 57.0, m)), 0.7));
    }
    float2 sd = lq - float2(_MrpFltLights[3].w, _MrpFltLights[5].w);
    car.brake = max(car.brake, saturate(1.0 - length(sd) / 60.0) * saturate(1.0 - _MrpFltLights[7].w / 90.0));
    blink = step(frac(clk * 1.5 + car.tone * 7.0), 0.5) * saturate(car.stopped * 3.0 - 2.0);
    blink = max(blink, step(frac(clk * 2.4 + car.tone * 3.0), 0.5) * alarm);
    if (car.burn > 0.0) { car.brake = 0; blink = 0; }
}

// 車の形 (0 = セダン・1 = ミニバン) と、タクシーか
void CarKind(CarInfo car, float hash, out float morph, out float taxi)
{
    float th = Hash12(float2(car.cell + 4831.0, hash));
    morph = th < 0.64 ? 0.0 : 1.0;
    taxi = (morph < 0.5 && Hash12(float2(car.cell + 5521.0, hash)) < 0.2) ? 1.0 : 0.0;
}

// 地面を這う霧 (FlightCity の GroundMist と同じ式)
float CarMist(float3 wp, float dist)
{
    float h = max(wp.y - _MrpCity.y, 0.0);
    float2 m = wp.xz * 0.004 + _Time.y * float2(0.011, 0.006);
    float drift = 0.7 + 0.3 * sin(m.x * 6.3 + sin(m.y * 4.1)) * sin(m.y * 5.3 + 1.7);
    return (1.0 - exp(-dist / 900.0)) * exp(-h / 70.0) * 0.75 * drift;
}

#endif
