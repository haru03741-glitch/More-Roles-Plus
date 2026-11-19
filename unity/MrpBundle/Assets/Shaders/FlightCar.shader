// 夜の街の車の立体。網目は枠ごとに同じ車の形を並べた物で、頂点シェーダが枠の番号から車を決めて置く (FlightCarCommon)。
// 頂点: POSITION = セダンの形の位置 (車の真ん中の地面が原点・x 右・y 上・z 前)・NORMAL = セダンの法線 + w 部品 (0 車体・1 タイヤ・2 ミラー・3 行灯)・
// TEXCOORD0 = ミニバンへの差 xyz + 枠の番号・TEXCOORD1 = ミニバンの法線 xyz + w 細かさ (0 近く・1 遠く)。
// 色は絵を持たず、車の中の位置から描く (窓・柱・扉の継ぎ目・灯・ナンバー・ホイール)。光は地面と同じ空の照り返し・
// 街灯 (縁石の外に 32 ごと)・砲口の閃光で、塗装は街灯と空の照り返しを映す。遠くは平たく潰して地面の絵へ渡す
Shader "MRP/FlightCar"
{
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" "IgnoreProjector" = "True" }
        Cull Back
        ZWrite On
        ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "FlightCarCommon.cginc"

            struct appdata { float4 vertex : POSITION; float4 normal : NORMAL; float4 d : TEXCOORD0; float4 n2 : TEXCOORD1; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 wp : TEXCOORD0;    // xyz = 世界・w = 細かさの切り替えのにじみ (1 = 描く)
                float4 n : TEXCOORD1;     // xyz = 法線 (世界)・w = 部品
                float4 lp : TEXCOORD2;    // xyz = 車の中の位置・w = 形 (0 セダン・1 ミニバン)
                float4 paint : TEXCOORD3; // rgb = 塗装・a = タクシー
                float4 st : TEXCOORD4;    // ブレーキ・点滅灯・焼けた殻・車輪の回った角度
                float4 road : TEXCOORD5;  // 軸・線の位置・広い通り・金属の塗装
                float3 ln : TEXCOORD6;    // 法線 (車の中で)
            };

            // 塗装の色。遠くの地面の絵の明るさ (黒 0.03・白 0.32・銀 0.18・色 0.09) と同じ組に分ける
            float3 Paint(float tone, float cell, float hash, float taxi, out float metal)
            {
                metal = 0;
                float h = Hash12(float2(cell + 7919.0, hash));
                if (taxi > 0.5)
                {
                    if (tone < 0.35) return float3(0.012, 0.012, 0.014);
                    if (tone < 0.7) return float3(0.55, 0.55, 0.52);
                    if (tone < 0.9) return float3(0.6, 0.42, 0.06);
                    return float3(0.05, 0.22, 0.09);
                }
                if (tone < 0.35) return float3(0.012, 0.012, 0.015);
                if (tone < 0.7) return lerp(float3(0.56, 0.56, 0.54), float3(0.5, 0.5, 0.5), h);
                if (tone < 0.9) { metal = 1; return lerp(float3(0.28, 0.29, 0.31), float3(0.2, 0.2, 0.21), h); }
                return h < 0.4 ? float3(0.24, 0.025, 0.03) : (h < 0.75 ? float3(0.025, 0.045, 0.14) : float3(0.035, 0.1, 0.06));
            }

            v2f vert(appdata v)
            {
                v2f o = (v2f)0;
                CarLane ln;
                float kind;
                CarInfo car = PlaceCar(v.d.w, ln, kind);
                float lod = v.n2.w > 0.0 ? 1.0 : 0.0;
                float part = floor(v.normal.w * 127.0 + 0.5);
                float morph, taxi;
                CarKind(car, ln.hash, morph, taxi);
                float3 center; float2 fwd;
                CarFrame(ln, car, center, fwd);
                float dist = distance(_WorldSpaceCameraPos, center + float3(0, 0.7, 0));
                // 細かい形は近くだけ・粗い形は少し重ねて遠くまで。境目はにじませる
                float show = lod < 0.5 ? step(dist, CAR_NEAR_R) : step(CAR_LOD_IN, dist) * step(dist, CAR_FAR_R);
                float fade = lod < 0.5 ? saturate((CAR_NEAR_R - dist) / (CAR_NEAR_R - CAR_LOD_IN)) : saturate((dist - CAR_LOD_IN) / (CAR_NEAR_R - CAR_LOD_IN));
                // 渋滞の 4 番目から先の枠は粗い形だけを持つので、近くでも粗い形で描く
                if (kind > 1.5 && fmod(floor(v.d.w / 64.0), 32.0) > 2.5) { show = step(dist, CAR_FAR_R); fade = 1.0; }
                // 行灯はタクシーだけ・殻はミラーと行灯が落ちている
                if (part > 2.5 && taxi < 0.5) show = 0;
                if (car.burn > 0.0 && part > 1.5) show = 0;
                if (car.ok < 0.5 || show < 0.5)
                {
                    o.pos = float4(0, 0, -10, 1);
                    return o;
                }
                float3 p = v.vertex.xyz + v.d.xyz * morph;
                float3 nn = normalize(lerp(v.normal.xyz, v.n2.xyz, morph));
                if (car.burn > 0.0)
                {
                    // 焼けた殻: タイヤが燃えて車体が沈み、天井が少したわむ
                    p.y -= 0.13;
                    p.y -= 0.12 * saturate((p.y - 0.9) / 0.5) * (1.0 - abs(p.z) / 2.3);
                    if (part > 0.5) p.xz *= 0.92;
                }
                // 遠くで地面の絵へ渡す前に平たく潰す (地面の絵は同じ距離で濃くなる)
                p.y *= saturate((CAR_FAR_R - dist) / CAR_FLAT);
                float2 right = float2(fwd.y, -fwd.x);
                float3 wp = center + float3(right.x * p.x + fwd.x * p.z, p.y, right.y * p.x + fwd.y * p.z);
                float3 wn = float3(right.x * nn.x + fwd.x * nn.z, nn.y, right.y * nn.x + fwd.y * nn.z);
                float blink;
                CarSignals(car, center, blink);
                float metal;
                o.paint = float4(Paint(car.tone, car.cell, ln.hash, taxi, metal), taxi);
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));
                o.wp = float4(wp, fade);
                o.n = float4(wn, part);
                o.lp = float4(p, morph);
                o.st = float4(car.brake, blink, car.burn, (car.rear + 2.3) / 0.31);
                o.road = float4(ln.axis, ln.baseLine, ln.wide, metal);
                o.ln = nn;
                return o;
            }

            // 警報の間の電気 (FlightCity の Power と同じ式)
            float Power(float2 xz, float seed)
            {
                float s = _MrpCityAlarm.x;
                float cut = s - length(xz - _MrpCityAlarm.zw) / 900.0 - Hash12(float2(seed, 5.1)) * 0.3;
                float back = s - _MrpCityAlarm.y - Hash12(float2(seed, 8.7)) * 2.5;
                float blink = step(Hash12(float2(seed * 3.3, floor(_Time.y * 15.0))), 0.5);
                float on = cut < 0.0 ? 1.0 : (cut < 0.45 ? blink : 0.0);
                float re = back < 0.0 ? 0.0 : (back < 0.35 ? blink : 1.0);
                return max(on, re);
            }

            // 街灯 1 本の光 (lq = 灯の街の xz・高さ h・広がり s2 は地面の溜まりと同じ)。拡散と照り
            void Lamp(float2 lq, float h, float s2, float3 wp, float3 n, float3 V, float gloss, inout float3 dif, inout float3 spe)
            {
                float2 lamp = floor(lq / 32.0 + 0.5);
                float2 lw = lq + _MrpCityOrg.xy;
                float pw = Power(lw, Hash12(lamp) * 97.0);
                float alive = step(Hash12(lamp * 1.91 + 3.7), 0.04) * step(Hash12(lamp + floor(_Time.y * 9.0)), 0.7);
                float k = max(pw, alive * 0.8);
                if (k <= 0.0) return;
                float3 L = float3(lw.x, _MrpCity.y + h, lw.y) - wp;
                float d2 = dot(L, L);
                float2 dxz = L.xz;
                float3 l = L * rsqrt(d2 + 1e-4);
                float3 col = float3(1.0, 0.16, 0.07) * 2.2 * k;
                dif += col * exp(-dot(dxz, dxz) / s2) * (saturate(dot(n, l)) * 1.2 + 0.08);
                float3 hv = normalize(l + V);
                spe += col * exp(-d2 / (s2 * 7.0)) * pow(saturate(dot(n, hv)), gloss) * (gloss + 8.0) * 0.04;
            }

            // 映り込み: 上は赤黒い空・水平の辺りはビルの窓の赤い粒・下は暗い路面
            float3 Env(float3 r, float dist)
            {
                float day = (1.0 - 0.6 * _MrpCity.x) * _MrpCity.z;
                float3 sky = lerp(float3(0.30, 0.05, 0.06), float3(0.03, 0.006, 0.01), saturate(r.y * 1.6)) * day;
                float2 g = floor(float2(atan2(r.x, r.z) * 38.0, r.y * 70.0));
                float win = step(Hash12(g), 0.28) * saturate(1.0 - r.y * 2.2) * step(0.0, r.y);
                sky += float3(1.0, 0.12, 0.08) * win * 0.35 * saturate(1.0 - dist / 90.0) * (1.0 - 0.85 * _MrpCity.x);
                float3 road = float3(0.02, 0.006, 0.007) * day;
                return lerp(road, sky, smoothstep(-0.08, 0.06, r.y));
            }

            float BeltY(float z, float m)
            {
                float s = lerp(0.93 + 0.05 * smoothstep(0.4, -1.8, z), 1.0 + 0.03 * smoothstep(0.6, -1.8, z), m);
                return s;
            }

            float Box(float2 p, float2 c, float2 hs, float f)
            {
                float2 d = abs(p - c) - hs;
                return saturate(0.5 - max(d.x, d.y) / f);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 細かさの切り替えは画面の網点でにじませる
                float2 sp = floor(i.pos.xy);
                float dither = frac(52.9829189 * frac(dot(sp, float2(0.06711056, 0.00583715))));
                clip(i.wp.w - dither - 0.001);
                float3 wp = i.wp.xyz;
                float3 n = normalize(i.n.xyz);
                float part = i.n.w;
                float3 lp = i.lp.xyz;
                float m = i.lp.w;
                float dist = distance(_WorldSpaceCameraPos, wp);
                float3 V = (_WorldSpaceCameraPos - wp) / max(dist, 1e-3);
                float f = max(fwidth(lp.y), 1e-3) * 1.2;
                float brake = i.st.x, blink = i.st.y, burn = i.st.z;
                float3 paint = i.paint.rgb;
                float taxi = i.paint.a;
                float metal = i.road.w;

                // 面の向き (車の中で)
                float3 ln = normalize(i.ln);
                float side = abs(ln.x), front = ln.z, up = ln.y;
                float belt = BeltY(lp.z, m);
                float3 alb = paint;
                float gloss = 60.0, coat = 0.5, glass = 0;
                float3 emis = 0;

                if (part < 0.5)
                {
                    // 窓: 窓の下の線より上の傾いた面と、天井の縁より下
                    float roofY = lerp(1.44, 1.80, m);
                    float isGlass = step(belt + 0.045, lp.y) * step(up, 0.93) * step(lp.y, roofY - 0.05);
                    // ミニバンの後ろの扉は尾灯より上だけが窓
                    isGlass *= front < -0.45 ? step(lerp(1.0, 1.18, m), lp.y) : 1.0;
                    // 柱 (前の窓の両脇・真ん中・後ろ) は塗装のまま黒い枠
                    float bz = lerp(0.0, 0.32, m);
                    float pillar = Box(float2(lp.z, 0), float2(bz, 0), float2(0.065, 1), max(fwidth(lp.z), 1e-3)) * step(0.5, side);
                    float cz = lerp(-0.62, -1.15, m);
                    pillar = max(pillar, Box(float2(lp.z, 0), float2(cz, 0), float2(0.07, 1), max(fwidth(lp.z), 1e-3)) * step(0.5, side) * m);
                    glass = isGlass * (1.0 - pillar);
                    // 扉の継ぎ目と取っ手 (横の面の窓の下)
                    float fz = max(fwidth(lp.z), 1e-3);
                    float below = step(lp.y, belt) * step(0.6, side);
                    float seams = 0;
                    float s0 = lerp(1.16, 1.42, m), s1 = lerp(0.03, 0.3, m), s2 = lerp(-1.05, -1.25, m);
                    seams = max(seams, saturate(1.0 - abs(lp.z - s0) / max(0.012, fz)));
                    seams = max(seams, saturate(1.0 - abs(lp.z - s1) / max(0.012, fz)));
                    seams = max(seams, saturate(1.0 - abs(lp.z - s2) / max(0.012, fz)));
                    seams *= below * step(0.32, lp.y);
                    float handle = (Box(float2(lp.z, lp.y), float2(s1 + 0.32, belt - 0.1), float2(0.09, 0.018), max(fz, f)) +
                                    Box(float2(lp.z, lp.y), float2(s2 + 0.32, belt - 0.1), float2(0.09, 0.018), max(fz, f))) * below;
                    alb = lerp(alb, alb * 0.25, saturate(seams * saturate(1.0 - dist / 60.0)));
                    alb = lerp(alb, float3(0.03, 0.03, 0.03), handle * 0.6);
                    // 下の縁とバンパーの下は黒い樹脂・底は暗い
                    float trim = step(lp.y, 0.34) + step(up, -0.4);
                    alb = lerp(alb, float3(0.02, 0.02, 0.022), saturate(trim));
                    coat *= 1.0 - saturate(trim);
                    // 前: 前照灯・格子・ナンバー
                    float fx = abs(lp.x);
                    float ff = max(fwidth(lp.x), f);
                    if (front > 0.45)
                    {
                        float hy = lerp(0.74, 0.86, m);
                        float head = Box(float2(fx, lp.y), float2(0.6, hy), float2(0.17, 0.065), ff);
                        float grille = Box(float2(fx, lp.y), float2(0.0, hy - 0.08), float2(0.36, 0.09), ff);
                        float slats = 0.5 + 0.5 * step(0.5, frac(lp.y * 32.0));
                        float plate = Box(float2(fx, lp.y), float2(0.0, 0.45), float2(0.17, 0.06), ff);
                        alb = lerp(alb, float3(0.015, 0.015, 0.017) * slats, grille);
                        alb = lerp(alb, float3(0.5, 0.5, 0.45), plate);
                        alb = lerp(alb, float3(0.3, 0.3, 0.32), head);
                        coat = lerp(coat, 1.0, head);
                        float amber = Box(float2(fx, lp.y), float2(0.8, hy), float2(0.05, 0.05), ff);
                        emis += float3(1.0, 0.92, 0.8) * 2.6 * head * (1.0 - step(0.001, burn));
                        emis += float3(1.0, 0.45, 0.05) * 2.2 * blink * amber;
                    }
                    else if (front < -0.45)
                    {
                        float ty = lerp(0.88, 1.05, m);
                        float tail = Box(float2(fx, lp.y), float2(0.62, ty), float2(0.19, 0.06), ff);
                        float inner = Box(float2(fx, lp.y), float2(0.5, ty), float2(0.06, 0.05), ff);
                        float outer = Box(float2(fx, lp.y), float2(0.76, ty), float2(0.05, 0.05), ff);
                        float plate = Box(float2(fx, lp.y), float2(0.0, lerp(0.6, 0.62, m)), float2(0.17, 0.06), ff);
                        alb = lerp(alb, float3(0.5, 0.5, 0.45), plate);
                        alb = lerp(alb, float3(0.12, 0.01, 0.01), tail);
                        float on = 1.0 - step(0.001, burn);
                        emis += float3(1.0, 0.05, 0.03) * 1.1 * (1.0 + 2.6 * brake) * tail * (1.0 - inner) * on;
                        emis += float3(1.0, 0.95, 0.9) * 0.12 * inner * on;
                        emis += float3(1.0, 0.45, 0.05) * 2.2 * blink * outer;
                        emis += float3(1.0, 0.9, 0.75) * 0.25 * plate * on;
                    }
                }
                else if (part < 1.5)
                {
                    // タイヤ: 黒いゴム。外の面にホイール (5 本の腕が走った距離で回る)
                    alb = float3(0.018, 0.018, 0.02);
                    gloss = 12.0; coat = 0.05;
                    if (side > 0.7)
                    {
                        float wz = lp.z > 0.0 ? 1.35 : -1.35;
                        float2 q = float2(lp.z - wz, lp.y - 0.31);
                        float r = length(q);
                        float ang = atan2(q.y, q.x) + i.st.w * sign(lp.x);
                        float spoke = step(0.5, frac(ang * 5.0 / 6.2832 + 0.25));
                        float rim = step(r, 0.21);
                        float hub = step(r, 0.05);
                        float3 rimC = lerp(float3(0.06, 0.06, 0.065), float3(0.32, 0.32, 0.34), max(spoke * step(0.07, r), hub));
                        alb = lerp(alb, rimC, rim * (1.0 - step(0.001, burn)));
                        coat = lerp(coat, 0.8, rim);
                        gloss = lerp(gloss, 90.0, rim);
                    }
                }
                else if (part < 2.5)
                {
                    // ミラー: 後ろを向く面は鏡
                    if (front < -0.5) glass = 1;
                }
                else
                {
                    // タクシーの行灯 (中から光る)
                    alb = float3(0.7, 0.62, 0.45);
                    emis += float3(1.0, 0.78, 0.45) * 0.9 * (0.6 + 0.4 * step(0.3, up));
                }

                // 焼けた殻: 煤けた黒・窓は抜けて中で火がくすぶる
                if (burn > 0.0)
                {
                    float2 g = floor(float2(lp.z * 4.0, lp.y * 5.0 + lp.x * 3.0));
                    float h = Hash12(g + i.st.w * 0.01);
                    float flick = 0.55 + 0.45 * sin(_MrpFltLights[1].w * (7.0 + 6.0 * h) + h * 6.3);
                    float hot = step(1.0 - (0.2 + 0.55 * burn), h) * flick * (0.05 + 1.6 * burn * burn);
                    alb = lerp(float3(0.014, 0.012, 0.012), float3(0.05, 0.025, 0.015), h * 0.5);
                    emis += float3(1.0, 0.32, 0.06) * hot * (glass > 0.5 ? 1.6 : 0.5);
                    glass = 0;
                    coat = 0.02;
                    gloss = 6.0;
                }

                // 光: 空の照り返し・街灯・砲口の閃光
                float3 dif = 0, spe = 0;
                float g = glass > 0.5 ? 220.0 : (metal > 0.5 ? 70.0 : gloss);
                float2 q = wp.xz - _MrpCityOrg.xy;
                float lat = (i.road.x < 0.5 ? q.x : q.y) - i.road.y;
                float a = i.road.x < 0.5 ? q.y : q.x;
                float rw = lerp(9.5, 39.0, i.road.z);
                float lc = floor(a / 32.0 + 0.5) * 32.0;
                float lc2 = lc + (a > lc ? 32.0 : -32.0);
                // 交差点の中には立たない (相手の道の歩道より外だけ)
                float crossN = floor(a / CAR_P + 0.5);
                float chw = fmod(abs(crossN), 4.0) < 0.5 ? 48.0 : 15.0;
                [unroll] for (int s = 0; s < 2; s++)
                {
                    float L0 = s == 0 ? lc : lc2;
                    if (abs(L0 - crossN * CAR_P) < chw) continue;
                    [unroll] for (int e = -1; e <= 1; e += 2)
                    {
                        float ll = i.road.y + e * (rw + 1.2);
                        float2 lq = i.road.x < 0.5 ? float2(ll, L0) : float2(L0, ll);
                        Lamp(lq, 8.0, 110.0, wp, n, V, g, dif, spe);
                    }
                    if (i.road.z > 0.5)
                    {
                        float2 lq = i.road.x < 0.5 ? float2(i.road.y, L0) : float2(L0, i.road.y);
                        Lamp(lq, 12.0, 300.0, wp, n, V, g, dif, spe);
                    }
                }
                float day = (1.0 - 0.6 * _MrpCity.x) * _MrpCity.z;
                float3 amb = float3(0.24, 0.11, 0.12) * day * (0.35 + 0.65 * saturate(n.y * 0.5 + 0.5));
                float3 fl = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float3 L = _MrpFltLights[k * 2].xyz - wp;
                    float r = _MrpFltLights[k * 2].w;
                    float d2 = dot(L, L);
                    float att = saturate(1.0 - d2 / (r * r));
                    fl += _MrpFltLights[k * 2 + 1].rgb * (att * att * saturate(dot(n, L * rsqrt(d2 + 1e-4)) * 0.85 + 0.15));
                }
                dif *= _MrpCity.z;
                spe *= _MrpCity.z;
                float nv = saturate(dot(n, V));
                float fres = 0.04 + 0.96 * pow(1.0 - nv, 5.0);
                float3 R = reflect(-V, n);
                float3 env = Env(R, dist);
                float3 c;
                if (glass > 0.5)
                {
                    // 暗い車内 + 映り込み + 灯の照り
                    c = float3(0.006, 0.005, 0.006) + env * (0.1 + 0.9 * fres) + spe * 1.4;
                }
                else
                {
                    float3 tint = metal > 0.5 ? alb * 2.2 + 0.05 : float3(1, 1, 1);
                    c = alb * (amb + dif + fl) + (env * fres * coat * (metal > 0.5 ? 1.6 : 1.0) + spe * (0.25 + coat)) * tint;
                }
                c += emis * _MrpCity.z;
                float fog = 1.0 - exp(-dist / max(_MrpFltSky.a, 1.0));
                c = lerp(c, _MrpFltSky.rgb * 1.35, CarMist(wp, dist));
                c = lerp(c, _MrpFltSky.rgb, fog);
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
