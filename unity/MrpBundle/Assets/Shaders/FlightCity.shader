// 3D の艦隊戦の夜の街 (ビル・地面・格子の塔・高架の道路)。窓・道・街灯の光は絵を持たず、位置と頂点の値から描く。
// 頂点: NORMAL = 法線 xyz + 材質 w (番号 / 127)・COLOR = r 建物の種・g 様式 × 64 + 8・b 窓の点く割合・a ちらつきの組・
// TEXCOORD0 = 壁に沿った長さ・地面からの高さ (単位)。
// 遠くで窓や道の線が画素より細くなったら、線と窓の平均の明るさへ寄せる (ちらつきとモアレを出さない)。
// _MrpCity = x 停電の暗さ (街全体・0..1)・y 地面の高さ・z 街の明るさ
// _MrpCityAlarm = x 警報が鳴ってからの秒 (鳴っていない間は大きな負)・y 明かりが戻り始める秒・zw 停電が広がり始める点 (世界の xz)
// 警報の間は電気が止まる: 塔から外へ停電が広がり、建物ごとに数回またたいて消える。残るのは非常灯の窓がわずかと、
// ゆっくり脈打つ航空障害灯だけ。戻る時は建物ごとにばらばらに点き直す
Shader "MRP/FlightCity"
{
    Properties
    {
        _Noise ("Noise", 2D) = "gray" {}
    }

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

            sampler2D _Noise;
            float4 _MrpFltSky;       // rgb = 霞の色・a = 霞む距離
            float4 _MrpFltLights[8];
            float4 _MrpCity;
            float4 _MrpCityAlarm;
            float4 _MrpCityOrg;      // xy = 街の原点の世界の xz (区画の格子はここから測る)
            float _MrpCityClock;     // 崩れの時計 (秒・崩れている棟がある間だけ進む)

            // k = 崩れ (崩れ始めた時刻 (0 = 立っている)・棟の中心 x・z・棟の高さ)
            struct appdata { float4 vertex : POSITION; float4 normal : NORMAL; fixed4 color : COLOR; float2 uv : TEXCOORD0; float4 k : TEXCOORD1; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 n : TEXCOORD0;   // xyz = 法線・w = 材質
                float3 wp : TEXCOORD1;
                float4 col : TEXCOORD2;
                float2 uv : TEXCOORD3;
                float power : TEXCOORD4; // 建物の電気 (1 = 点いている・0 = 停電)
                float2 ruin : TEXCOORD5; // x = 電気が落ちた (崩れ始め 0.6 秒で 1)・y = 崩れ落ちた (落ちきって 1 秒で 1)
            };

            float Hash12(float2 p)
            {
                float3 q = frac(float3(p.xyx) * 0.1031);
                q += dot(q, q.yzx + 33.33);
                return frac((q.x + q.y) * q.z);
            }

            // 警報の間の電気 (seed = 建物や灯の種)。停電の前線が届くと 0.45 秒またたいて消え、戻る時も 0.35 秒またたいて点く
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

            // 崩れ: 一瞬震え → 足元から沈み込みながら種の向きへ少し傾く → 地面より下へ行った分は低い瓦礫の山に潰れて外へ広がる。
            // 落ちる速さと瓦礫の高さは土煙を出す側 (flight_sim の StepRuins・flight_city の RubbleTop) と同じ式
            float4 Collapse(float4 pos, float4 k, float seed, out float2 ruin)
            {
                ruin = 0.0;
                float s = _MrpCityClock - k.x;
                if (k.x <= 0.0 || s <= 0.0) return pos;
                float G = _MrpCity.y;
                float H = max(k.w, 1.0);
                float h = pos.y - G;
                float jt = floor(s * 30.0);
                float sh = saturate(s / 0.05) * (1.0 - saturate((s - 0.25) / 0.3));
                pos.xz += (float2(Hash12(float2(seed, jt)), Hash12(float2(seed + 7.0, jt))) - 0.5) * 1.6 * sh;
                float tf = max(s - 0.35, 0.0);
                float fall = min(30.0 * tf * tf, H);
                float R = min(0.08 * H + 4.0, 28.0);
                float hn = h - fall;
                float y = hn > R ? hn : min(R * exp((hn - R) / (0.5 * H)), h); // 元の高さより上へは持ち上げない (足元が浮かない)
                float spread = saturate((R - hn) / H) * 0.3;
                float ang = Hash12(float2(seed, 3.3)) * 6.2832;
                float lean = 0.12 * saturate(tf / 2.0) * saturate((hn - R) * 4.0 / H);
                float2 rel = (pos.xz - k.yz) * (1.0 + spread);
                pos.xz = k.yz + rel + float2(cos(ang), sin(ang)) * (y * lean);
                pos.y = G + y;
                float fallT = sqrt(2.0 * H / 60.0) + 0.35;
                ruin = float2(saturate(s / 0.6), saturate(s - fallT));
                return pos;
            }

            v2f vert(appdata v)
            {
                v2f o;
                float2 ruin;
                float4 pos = Collapse(v.vertex, v.k, v.color.r * 255.0, ruin);
                o.ruin = ruin;
                o.pos = UnityObjectToClipPos(pos);
                o.wp = mul(unity_ObjectToWorld, pos).xyz;
                o.n = float4(normalize(mul((float3x3)unity_ObjectToWorld, v.normal.xyz)), floor(v.normal.w * 127.0 + 0.5));
                o.col = v.color;
                o.uv = v.uv;
                o.power = Power(o.wp.xz, v.color.r * 255.0) * (1.0 - ruin.x);
                return o;
            }

            // 升目の線の覆う割合 (幅 w・升 1 つの単位)。画素が升より粗くなると線の平均 (w) へ寄せる
            float Lines(float2 g, float2 w, float2 fw)
            {
                float2 d = abs(frac(g + 0.5) - 0.5);
                float2 m = 1.0 - smoothstep(w * 0.5 - fw, w * 0.5 + fw, d);
                m = lerp(m, w, saturate(fw * 2.0 - 0.4));
                return 1.0 - (1.0 - m.x) * (1.0 - m.y);
            }

            // ふだんの蛍光灯の小さなちらつき
            float Flicker(float seed, float cls)
            {
                return Hash12(float2(seed * 91.7, floor(_Time.y * 14.0 + cls * 3.1))) < 0.012 ? 0.35 : 1.0;
            }

            float3 Facade(v2f i, float style, float seed, float litRatio, float isDeck)
            {
                float2 cell = style < 0.5 ? float2(1.7, 1.9) : (style < 1.5 ? float2(1.05, 1.9) : (style < 2.5 ? float2(1.35, 1.45) : float2(1.7, 1.9)));
                if (isDeck > 0.5) cell = float2(1.15, 1.6);
                float2 g = i.uv / (cell * 2.0); // 窓 1 つの大きさ (1 階 = 3.8)
                float2 fw = max(fwidth(g), 1e-4);
                float2 lw = style < 0.5 ? float2(0.07, 0.11) : (style < 1.5 ? float2(0.1, 0.05) : (style < 2.5 ? float2(0.035, 0.2) : float2(0.05, 0.08)));
                float bright = 0.6 + 0.6 * Hash12(float2(seed * 37.1, 4.2));
                float lineI = (style < 0.5 ? 0.7 : (style < 1.5 ? 0.55 : (style < 2.5 ? 0.22 : 0.04))) * bright;
                float lineCov = Lines(g, lw, fw);
                // 窓: 升ごとに点くか・色。時々 1 つの階が丸ごと点いている
                float2 id = floor(g);
                float h = Hash12(id + seed * 113.7);
                float h2 = Hash12(id * 1.37 + seed * 17.3);
                float floorLit = step(Hash12(float2(id.y, seed * 71.3)), 0.05);
                float lit = max(step(h, litRatio), floorLit);
                float3 red = float3(1.0, 0.1, 0.07), warm = float3(1.0, 0.6, 0.34), pale = float3(1.0, 0.78, 0.82);
                float3 pane = h2 < 0.68 ? red : (h2 < 0.9 ? warm : pale);
                if (isDeck > 0.5) { lit = 1.0; pane = lerp(warm, red, 0.35); }
                float2 f = abs(frac(g) - 0.5);
                float2 inner = 0.5 - lw * 0.5 - 0.06;
                float2 pm = 1.0 - smoothstep(inner - fw, inner + fw, f);
                float paneMask = pm.x * pm.y;
                float3 near = pane * lit * paneMask * (0.45 + 0.6 * frac(h2 * 7.31));
                // 遠くの平均: 点く割合 × 平均の色 × 窓の面積
                float3 avg = (red * 0.68 + warm * 0.22 + pale * 0.1) * (litRatio + 0.05) * 0.55 * 0.85;
                if (isDeck > 0.5) avg = pane * 0.55 * 0.85;
                float far = saturate(max(fw.x, fw.y) * 1.6 - 0.35);
                // 停電の間も非常灯の窓がわずかに残る (暗い赤のまま点きっぱなし)
                float emergency = step(frac(h2 * 13.7), 0.07) * lit;
                float pw = i.power;
                near *= max(pw, emergency * 0.75);
                avg *= max(pw, 0.07 * 0.75);
                float3 win = lerp(near, avg, far);
                float3 lineCol = float3(1.0, 0.5, 0.56);
                float flags = floor(i.col.a * 255.0 + 0.5);
                float fl = Flicker(seed, fmod(flags, 8.0));
                float3 c = float3(0.018, 0.011, 0.016) + (win + lineCol * lineCov * lineI * pw) * fl * _MrpCity.z;
                // 燃えている棟: 一部の窓の奥で火が揺れ、枠は煤けて暗い
                if (flags >= 64.0 && isDeck < 0.5)
                {
                    float fire = step(Hash12(id * 0.71 + seed * 5.3), 0.16);
                    float flick = 0.55 + 0.45 * sin(_Time.y * (9.0 + 7.0 * h) + h2 * 40.0) * sin(_Time.y * 4.3 + h * 17.0);
                    float3 fireC = float3(1.0, 0.42, 0.1) * flick;
                    c = lerp(c, c * 0.45, 0.6) + lerp(fireC * fire * paneMask, fireC * 0.16 * 0.55, far) * (1.0 - i.ruin.x);
                }
                // 下の街明かりで足元ほど赤く照らされる
                float hgt = i.wp.y - _MrpCity.y;
                c += float3(0.05, 0.006, 0.008) * exp(-hgt / 90.0) * (1.0 - 0.75 * _MrpCity.x);
                return c;
            }

            float3 Ground(float3 wp, float dist)
            {
                // 区画の間隔と道の半分の幅 (flight_city.cpp の Pitch・RoadW と同じ値)
                const float P = 192.0;
                float2 q = wp.xz - _MrpCityOrg.xy;
                float2 li = floor(q / P + 0.5);
                float2 d = abs(q - li * P);
                float2 hw = float2(fmod(abs(li.x), 4.0) < 0.5 ? 48.0 : 15.0, fmod(abs(li.y), 4.0) < 0.5 ? 48.0 : 15.0);
                float2 fw = max(fwidth(q), 1e-3);
                float2 onR = 1.0 - smoothstep(hw - fw, hw + fw, d);
                float road = max(onR.x, onR.y);
                float3 c = lerp(float3(0.026, 0.02, 0.024), float3(0.012, 0.01, 0.013), road);
                // 街灯の光の溜まり: 道の両脇に 32 ごと (縦の道 = x の線・横の道 = z の線)
                float lz = abs(q.y - floor(q.y / 32.0 + 0.5) * 32.0);
                float lx = abs(q.x - floor(q.x / 32.0 + 0.5) * 32.0);
                float ex = abs(d.x - hw.x - 2.0), ez = abs(d.y - hw.y - 2.0);
                float poolA = exp(-(ex * ex + lz * lz) / 28.0) * step(d.x, hw.x + 6.0);
                float poolB = exp(-(ez * ez + lx * lx) / 28.0) * step(d.y, hw.y + 6.0);
                float pool = max(poolA, poolB);
                // 遠くでは溜まりの点が見分けられないので、道の上の平均の明るさに寄せる
                float far = saturate(max(fw.x, fw.y) / 8.0 - 0.3);
                pool = lerp(pool, road * 0.1, far);
                // 停電: 街灯は消える。ごく一部だけ電気が残り、切れかけて不規則にまたたく
                float2 lamp = floor(q / 32.0 + 0.5);
                float pw = Power(wp.xz, Hash12(lamp) * 97.0);
                float alive = step(Hash12(lamp * 1.91 + 3.7), 0.04) * step(Hash12(lamp + floor(_Time.y * 9.0)), 0.7);
                c += float3(1.0, 0.16, 0.07) * pool * 0.5 * _MrpCity.z * Flicker(li.x * 7.0 + li.y * 3.0, 0) * max(pw, lerp(alive * 0.8, 0.02, far));
                return c;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float mat = i.n.w;
                float3 n = normalize(i.n.xyz);
                float seed = i.col.r * 255.0;
                float style = floor(i.col.g * 255.0 / 64.0);
                float litRatio = i.col.b;
                float dist = distance(_WorldSpaceCameraPos, i.wp);
                float3 c;
                float albedo = 0.35;
                if (mat < 0.5 || (mat > 3.5 && mat < 4.5)) c = Facade(i, style, seed, litRatio, mat > 3.5 ? 1.0 : 0.0);
                else if (mat < 1.5)
                {
                    // 屋上: 暗いコンクリート + 空の赤い照り返し
                    c = float3(0.03, 0.018, 0.022) + float3(0.04, 0.004, 0.008) * (0.6 + 0.4 * Hash12(float2(seed, 2.0)));
                }
                else if (mat < 2.5)
                {
                    // 塔の鉄骨: 下から照らされて自ら光って見える (上へ行くほど少し暗い)
                    float hgt = i.wp.y - _MrpCity.y;
                    c = i.col.rgb * (1.25 - hgt / 780.0) * (0.75 + 0.25 * n.y * n.y);
                    // 停電の間は下からの光が消え、てっぺんと展望台の障害灯の脈にだけ赤く浮かぶ
                    float p = frac(max(_MrpCityAlarm.x, 0.0) / 2.2) * 2.2;
                    float beat = smoothstep(0.0, 0.08, p) * exp(-p / 0.8);
                    c *= lerp(0.08 + 0.4 * beat * saturate(hgt / 300.0), 1.0, i.power);
                    albedo = 0.1;
                }
                else if (mat < 3.5) c = Ground(i.wp, dist);
                else
                {
                    // 高架の道路のコンクリート (縁の灯に下から照らされる)
                    c = float3(0.05, 0.035, 0.04) + float3(0.12, 0.03, 0.015) * saturate(n.y) * i.power;
                }
                // 砲口の閃光・爆発が照らす
                float3 fl = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float3 L = _MrpFltLights[k * 2].xyz - i.wp;
                    float r = _MrpFltLights[k * 2].w;
                    float d2 = dot(L, L);
                    float att = saturate(1.0 - d2 / (r * r));
                    fl += _MrpFltLights[k * 2 + 1].rgb * (att * att * saturate(dot(n, L * rsqrt(d2 + 1e-4)) * 0.85 + 0.15));
                }
                c += fl * albedo;
                // 崩れた瓦礫は粉をかぶって暗く鈍い色
                c = lerp(c, c * 0.35 + float3(0.03, 0.022, 0.024), i.ruin.y * (mat < 1.5 ? 1.0 : 0.0));
                // 瓦礫の中でくすぶる火 (崩れ終わってから点き、ゆっくり明滅する)
                if (i.ruin.y > 0.0 && mat < 1.5)
                {
                    float2 cell = floor(i.wp.xz / 2.5) + floor(i.wp.y / 2.5) * 17.0;
                    float hot = step(Hash12(cell), 0.07) * (0.5 + 0.5 * sin(_Time.y * (1.3 + 2.0 * Hash12(cell + 3.1)) + Hash12(cell + 9.2) * 6.3));
                    c += float3(1.0, 0.3, 0.06) * hot * 0.55 * i.ruin.y * saturate(1.0 - dist / 1500.0);
                }
                // 霞: 遠いほど赤黒い霞へ・地面に近いほど濃い
                float fog = 1.0 - exp(-dist / max(_MrpFltSky.a, 1.0));
                float low = exp(-max(i.wp.y - _MrpCity.y, 0.0) / 150.0) * saturate(dist / 1800.0) * 0.2;
                c = lerp(c, _MrpFltSky.rgb, saturate(fog + low));
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
