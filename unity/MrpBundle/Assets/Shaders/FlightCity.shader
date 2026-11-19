// 3D の艦隊戦の夜の街 (ビル・地面・格子の塔・高架の道路)。窓・道・街灯の光は絵を持たず、位置と頂点の値から描く。
// 頂点: NORMAL = 法線 xyz + w (材質 + 8 × 壁の種類) / 127・COLOR = r 建物の種・g 壁の作り × 16 + 8・b 窓の点く割合・a ちらつきの組・
// TEXCOORD0 = 壁に沿った長さ・地面からの高さ (単位)。
// 遠くで窓や道の線が画素より細くなったら、線と窓の平均の明るさへ寄せる (ちらつきとモアレを出さない)。
// _MrpCity = x 停電の暗さ (街全体・0..1)・y 地面の高さ・z 街の明るさ・w 停電で車が止まっていた秒の合計
// _MrpFltLights の奇数番の w = 場面の時計 (秒)・自分の船の街の x・z・地面からの高さ
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
                float3 rel : TEXCOORD6;  // カメラから見た位置 (場面の原点が遠いので、画素ごとの差はこちらで取る)
                float wcls : TEXCOORD7;  // 壁の種類 (0 表・1 隣と接する窓の無い壁・2 妻)
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
                o.wp = mul(unity_ObjectToWorld, pos).xyz;
                // 焼いた街の地面の面 (色 g = 区分 + 1) は区分の順に少しだけカメラ側へ寄せる。
                // 同じ高さに近い面どうしが遠くで深さの細かさを超えてちらつかないように (距離に比例して寄せる)
                float gcls = floor(v.color.g * 255.0 + 0.5);
                // 法線の w = 材質 + 8 × 壁の種類 (0 表・1 隣と接する窓の無い壁・2 妻)
                float code = floor(v.normal.w * 127.0 + 0.5);
                float wcls = floor(code / 8.0);
                float mat = code - wcls * 8.0;
                float3 wp = o.wp;
                if (mat == 3.0 && gcls > 0.5) wp += (_WorldSpaceCameraPos - wp) * (gcls * 4e-5);
                o.pos = UnityWorldToClipPos(wp);
                o.rel = o.wp - _WorldSpaceCameraPos;
                o.n = float4(normalize(mul((float3x3)unity_ObjectToWorld, v.normal.xyz)), mat);
                o.wcls = wcls;
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

            // 帯 [a, b] の中か (縁は e でぼかす)
            float Span(float x, float a, float b, float e) { return smoothstep(a - e, a + e, x) * (1.0 - smoothstep(b - e, b + e, x)); }

            // 窓の奥の部屋。窓の並びの奥に箱の部屋 (幅・高さ・奥行きを 0..1 にした升) があるとして、目からの線が最初に当たる面
            // (手前の家具・天井・床・横の壁・奥の壁) の照らされ方を返す (部屋の明かり 1 あたり・平均は 0.5 前後)。
            // p = 部屋の手前の面での位置・d = 部屋の升で測った視線 (z が奥)・nw = 部屋の横に並ぶ窓の数・
            // kind = 0 事務所 / 1 住まい / 2 倉庫・rh = 部屋の種・e = 模様の縁のぼかし (遠いほど広い)
            float Room(float2 p, float3 d, float nw, float kind, float rh, float e)
            {
                d.xy = (d.xy >= 0.0 ? 1.0 : -1.0) * max(abs(d.xy), 1e-4);
                float3 o = float3(p, 0.0);
                float3 tb = (step(0.0, d) - o) / d;
                float tm = min(min(tb.x, tb.y), tb.z);
                float3 q = o + d * tm;
                float wx = q.x * nw; // 窓 1 つを 1 とした横の位置 (灯や机は窓ごとに並ぶ)
                float v;
                if (tm == tb.y && d.y > 0.0)
                {
                    // 天井: 灯の近くほど明るい。事務所 = 窓ごとの蛍光灯の列・住まい = 真ん中の吊り下げの灯・倉庫 = 裸の電球
                    float cx = frac(wx) - 0.5, cz = q.z - 0.5;
                    v = 0.3 + 0.45 * exp(-(cx * cx + cz * cz) * 6.0);
                    if (kind < 0.5) v += 2.2 * Span(abs(cx), -1.0, 0.07, e) * Span(q.z, 0.2, 0.8, e);
                    else v += (kind < 1.5 ? 2.6 : 1.6) * exp(-(cx * cx * nw * nw + cz * cz) / (0.006 + e * e));
                }
                else if (tm == tb.y)
                {
                    // 床: 灯の下に光が溜まる。住まいは敷物の濃い四角
                    float cx = frac(wx) - 0.5, cz = q.z - 0.55;
                    v = 0.09 + 0.3 * exp(-(cx * cx + cz * cz) * 5.0);
                    if (kind > 0.5 && kind < 1.5) v *= 1.0 - 0.45 * Span(q.x, 0.2, 0.8, e) * Span(q.z, 0.3, 0.8, e);
                }
                else if (tm == tb.x)
                {
                    // 横の壁: 天井に近いほど明るく、奥へ行くほど灯から離れて暗い
                    v = (0.16 + 0.2 * q.y) * (1.05 - 0.4 * abs(q.z - 0.5));
                    // 住まい・事務所の横の壁の棚や扉の暗い影
                    float sh = Hash12(float2(rh, q.x * 3.0));
                    v *= 1.0 - 0.5 * step(0.55, sh) * Span(q.z, 0.35 + 0.3 * sh, 0.6 + 0.3 * sh, e) * Span(q.y, -1.0, 0.75, e);
                }
                else
                {
                    // 奥の壁: 上ほど明るい。扉 (どこへ続くか分からない暗い戸口)・事務所は掲示板、住まいは額と棚
                    v = 0.44 * (0.55 + 0.45 * q.y);
                    float dx = 0.1 + 0.7 * frac(rh * 7.3);
                    float door = Span(q.x, dx, dx + 0.2 / nw, e) * Span(q.y, -1.0, 0.72, e);
                    float open = step(0.5, frac(rh * 3.1));
                    v *= 1.0 - door * (open > 0.5 ? 0.92 : 0.45);
                    float fx = frac(wx + rh * 5.0);
                    float art = Span(fx, 0.3, 0.7, e) * Span(q.y, 0.45, 0.72, e) * (1.0 - door);
                    v += art * (kind < 0.5 ? 0.12 : (kind < 1.5 ? -0.12 : 0.0));
                }
                // 隅は光が届かず暗い (面の縁ほど暗くする)
                float3 m = min(q, 1.0 - q);
                float edge = tm == tb.x ? min(m.y, m.z) : (tm == tb.y ? min(m.x, m.z) : min(m.x * nw, m.y));
                v *= 0.6 + 0.4 * smoothstep(0.0, 0.15, edge);
                // 手前の家具 (机・仕切り・ソファの背・棚)。奥の面より手前で当たれば、上から照らされた暗い影の形
                float zm = kind < 0.5 ? 0.38 : (kind < 1.5 ? 0.55 : 0.5);
                float tf = zm / d.z;
                if (tf < tm)
                {
                    float2 f = o.xy + d.xy * tf;
                    float fx = frac(f.x * nw + rh * 3.0);
                    float top = kind < 0.5 ? 0.3 + 0.12 * step(0.5, frac(fx * 2.0 + rh)) : (kind < 1.5 ? 0.27 : 0.78);
                    float body = Span(f.y, -1.0, top, e);
                    if (kind > 0.5 && kind < 1.5) body *= Span(fx, 0.15, 0.85, e); // ソファは窓の幅より短い
                    if (kind > 1.5) body *= max(Span(frac(f.y * 4.0), 0.0, 0.1, e * 4.0), Span(frac(fx * 2.0), 0.0, 0.12, e * 2.0)); // 棚の板と柱
                    v = lerp(v, 0.07 + 0.25 * Span(f.y, top - 0.03, top, e), body);
                }
                return v;
            }

            // 幅 w の帯 (x = 帯の中心からの距離) を 1 画素の幅 f で平均した覆う割合。画素より細い線は薄く広がり、ちらつかない
            float Band(float x, float w, float f)
            {
                return saturate((min(x + f * 0.5, w * 0.5) - max(x - f * 0.5, -w * 0.5)) / f);
            }

            // 間隔 p で並ぶ長さ L の帯 (破線・縞)。画素が間隔より粗くなると平均 (L / p) へ寄せる
            float Dashes(float a, float L, float p, float f)
            {
                float x = a - floor(a / p + 0.5) * p;
                return lerp(Band(x, L, f), L / p, saturate(f / p * 2.0 - 0.5));
            }

            // 壁の作りごとの窓の並び (壁の作りの番号は flight_city.cpp の Fac と同じ)。uv の 1 階 = 3.8。
            // 東京の下町の実物の壁に合わせた: 全面ガラスは高い事務所だけ・大きな商業は窓のほとんど無い壁・
            // 雑居や住まいは壁の間に小さな窓・マンションはベランダ側が手すりの帯で廊下側は夜通し灯る共用廊下。
            // bw = 窓の升の横の幅 (uv)・r = 升の中の窓の範囲 (x0 x1 y0 y1)・pr = 窓のある升の割合・nw = 部屋が横にまたぐ升の数・
            // kind = 部屋の種類 (0 事務所 / 1 住まい / 2 倉庫)・ln = 窓枠の線の強さ (ガラスの壁だけ)
            void FacLayout(float fac, out float bw, out float4 r, out float pr, out float nw, out float kind, out float ln)
            {
                bw = 3.0; r = float4(0.18, 0.82, 0.25, 0.78); pr = 0.85; nw = 1.0; kind = 0.0; ln = 0.0; // 雑居・小さな事務所 (5)
                if (fac < 0.5) { bw = 3.4; r = float4(0.05, 0.95, 0.24, 0.93); pr = 1.0; nw = 2.0; ln = 0.6; }              // 事務所 (横長の窓と腰壁)
                else if (fac < 1.5) { bw = 2.1; r = float4(0.05, 0.95, 0.04, 0.96); pr = 1.0; nw = 3.0; ln = 0.55; }       // 高い事務所の全面ガラス
                else if (fac < 2.5) { bw = 4.4; r = float4(0.06, 0.94, 0.03, 0.8); pr = 1.0; kind = 1.0; }                  // マンション
                else if (fac < 3.5) { bw = 3.0; r = float4(0.26, 0.74, 0.32, 0.74); pr = 0.4; kind = 1.0; }                 // 戸建て
                else if (fac < 4.5) { kind = 1.0; }                                                                        // 店と住まい (上の階)
                else if (fac < 5.5) { }
                else if (fac < 6.5) { bw = 4.0; r = float4(0.0, 1.0, 0.36, 0.7); pr = 1.0; nw = 3.0; }                     // 大きな商業 (窓は一部の階の帯だけ)
                else if (fac < 7.5) { bw = 4.0; r = float4(0.1, 0.9, 0.7, 0.88); pr = 0.3; nw = 4.0; kind = 2.0; }          // 工場・倉庫
                else if (fac < 8.5) { bw = 3.4; r = float4(0.0, 1.0, 0.3, 0.8); pr = 1.0; nw = 3.0; ln = 0.25; }            // 学校・役所 (横に続く窓)
                else { bw = 2.6; r = float4(0.22, 0.78, 0.3, 0.75); pr = 1.0; kind = 1.0; }                                // 宿
            }

            // 窓の無い壁の面 (打ち放し・タイル・板)。床の縁の帯は街明かりを受けて明るく、縦の継ぎ目は暗い。
            // g = 升の位置 (横は窓の升・縦は階)・fw = 画素の幅 (升の単位)・tone = 棟ごとの明るさ
            // 窓の無い壁 (blank = 1) は床の縁が外に出ないので、目地は淡い打ち継ぎの線だけ
            float WallFace(float2 g, float2 fw, float tone, float blank)
            {
                float joint = 1.0 + lerp(0.8, 0.18, blank) * Band(frac(g.y + 0.5) - 0.5, 0.07, fw.y) - 0.25 * Band(frac(g.x * 0.5 + 0.5) - 0.5, 0.04, fw.x * 0.5);
                return tone * joint;
            }

            float3 Facade(v2f i, float fac, float wcls, float seed, float litRatio, float isDeck)
            {
                float bw; float4 r; float pr, nw, kind, ln;
                FacLayout(fac, bw, r, pr, nw, kind, ln);
                float3 nn = normalize(i.n.xyz);
                float hb = Hash12(float2(seed * 3.7, 11.3));
                // マンションの面の向き: ベランダは南寄り (棟ごとに ±40°)・反対が共用廊下・残りの面は小さな窓
                float side = 0.0; // 1 = ベランダ・-1 = 共用廊下
                if (fac > 1.5 && fac < 2.5 && isDeck < 0.5)
                {
                    float a = (hb - 0.5) * 1.4;
                    float b = dot(nn.xz, float2(sin(a), -cos(a)));
                    side = b > 0.5 ? 1.0 : (b < -0.5 ? -1.0 : 0.0);
                    if (side == 0.0) { bw = 3.0; r = float4(0.3, 0.7, 0.34, 0.72); pr = 0.7; }
                    if (side < 0.0) { r = float4(0.58, 0.86, 0.46, 0.82); }
                }
                // 妻: 小さな窓が縦に少しだけ並ぶ
                if (wcls > 1.5) { bw = 3.0; r = float4(0.36, 0.64, 0.4, 0.7); pr = 0.22; nw = 1.0; ln = 0.0; }
                float2 cell = float2(bw, 3.8);
                if (isDeck > 0.5) { cell = float2(2.3, 3.2); r = float4(0.05, 0.95, 0.06, 0.94); pr = 1.0; nw = 3.0; kind = 1.0; ln = 0.55; }
                float2 g = i.uv / cell;
                float2 fw = max(fwidth(g), 1e-4);
                float2 id = floor(g);
                float2 f = frac(g);
                float far = saturate(max(fw.x, fw.y) * 1.6 - 0.35);
                float blank = (wcls > 0.5 && wcls < 1.5) ? 1.0 : 0.0;
                // 1 階: 店の間口・入口 (表の面だけ)
                float ground = (id.y < 0.5 && blank < 0.5 && wcls < 1.5 && isDeck < 0.5) ? 1.0 : 0.0;
                float shopRow = ground * ((fac < 1.5 || (fac > 3.5 && fac < 6.5) || fac > 8.5) ? 1.0 : 0.0);
                if (fac > 4.5 && fac < 5.5) shopRow *= step(hb, 0.55);
                // 1 階を店や入口に使う棟は、1 階に上の階と同じ窓を並べない
                float groundUse = max(shopRow, ground * ((fac > 1.5 && fac < 3.5) ? 1.0 : 0.0));
                // 窓のある升か (升ごと・大きな商業は階ごとの帯)
                float hc = Hash12(float2(id.x * 1.31 + 7.0, id.y * 0.73) + seed * 0.913);
                float has = step(hc, pr);
                if (fac > 5.5 && fac < 6.5 && isDeck < 0.5) has = step(Hash12(float2(id.y, seed * 5.1)), 0.16);
                if (wcls > 1.5) has = step(Hash12(float2(id.x * 2.1, seed * 0.37)), pr * 1.6) * step(0.25, Hash12(id + seed * 0.21));
                has *= (1.0 - blank) * (1.0 - groundUse);
                float bright = 0.6 + 0.6 * Hash12(float2(seed * 37.1, 4.2));
                // 部屋は横に nw 個の升をまたぐ。点くか・色は部屋ごと。時々 1 つの階が丸ごと点いている
                float2 rid = float2(floor(g.x / nw), id.y);
                float h = Hash12(rid + seed * 113.7);
                float h2 = Hash12(rid * 1.37 + seed * 17.3);
                // 壁に沿った向き t と、uv の 1 が世界のいくつか (ku・kv)。壁の uv は位置の一次式なので、隣の画素との差の比から出る
                // (格子の街と焼いた街で u の進む向きが逆でも ku の符号がそれを持つ)
                float3 t = float3(-nn.z, 0.0, nn.x);
                float3 rx = ddx(i.rel), ry = ddy(i.rel);
                float2 at = float2(dot(rx, t), dot(ry, t)), ay = float2(rx.y, ry.y);
                float ku = dot(float2(ddx(i.uv.x), ddy(i.uv.x)), at) / max(dot(at, at), 1e-10);
                float kv = dot(float2(ddx(i.uv.y), ddy(i.uv.y)), ay) / max(dot(ay, ay), 1e-10);
                float floorLit = step(Hash12(float2(id.y, seed * 71.3)), 0.05);
                float lit = max(step(h, litRatio), floorLit);
                float3 red = float3(1.0, 0.1, 0.07), warm = float3(1.0, 0.6, 0.34), pale = float3(1.0, 0.78, 0.82);
                float3 pane = h2 < 0.68 ? red : (h2 < 0.9 ? warm : pale);
                if (isDeck > 0.5) { lit = 1.0; pane = lerp(warm, red, 0.35); }
                                float paneMask = Span(f.x, r.x, r.y, fw.x) * Span(f.y, r.z, r.w, fw.y) * has;
                float3 V = normalize(i.rel);
                float Vn = max(-dot(V, nn), 0.02);
                // 部屋の照らされ方 (遠くで窓が画素に近いほど小さくなったら平均の 0.5 のまま)。
                // 消えている部屋はほぼ黒なので描かない (壁が画面を覆う時の重さを点いた窓の分だけにする)
                float rv = 0.5;
                [branch] if (far < 0.999 && paneMask > 0.001 && lit > 0.5)
                {
                    float depth = (0.9 + 0.8 * Hash12(rid * 0.53 + seed * 2.9)) * (nw > 1.5 ? 1.6 : 1.0); // 奥行き (窓 1 つの幅を 1)
                    float3 d = float3(dot(V, t) * ku / (cell.x * nw), V.y * kv / cell.y, Vn * abs(ku) / (cell.x * depth));
                    float e = max(fw.x, fw.y) * 1.5 + 0.01;
                    float2 pr2 = float2(frac(g.x / nw), f.y);
                    rv = Room(pr2, d, nw, kind, h2 * 17.0 + h, e);
                    // 窓の内側のブラインドとカーテン (後ろから照らされた布)。窓の中の位置で測る
                    float2 pp = float2((f.x - r.x) / max(r.y - r.x, 0.01), (f.y - r.z) / max(r.w - r.z, 0.01));
                    float cb = Hash12(rid * 2.71 + seed * 0.37);
                    float cov = 0.0, cloth = 0.55;
                    if (kind < 0.5 && cb < 0.2)
                    {
                        float yb = 1.0 - (0.2 + 0.8 * frac(cb * 13.0));
                        cov = smoothstep(yb - e, yb + e, pp.y);
                        cloth = 0.45 + 0.2 * lerp(smoothstep(0.3, 0.7, abs(frac(pp.y * 14.0) - 0.5) * 2.0), 0.5, saturate(fw.y * 28.0 - 0.5));
                    }
                    else if (kind > 0.5 && kind < 1.5 && cb < 0.45)
                    {
                        float cwid = 0.1 + 0.25 * frac(cb * 7.0);
                        cov = cb < 0.12 ? 0.8 : 1.0 - Span(pp.x, cwid, 1.0 - cwid, e);
                        cloth = 0.5 + 0.12 * sin(pp.x * 45.0 + cb * 20.0) * (1.0 - saturate(fw.x * 10.0));
                    }
                    rv = lerp(rv, cloth, cov);
                }
                float3 near = pane * lit * paneMask * (0.45 + 0.6 * frac(h2 * 7.31)) * rv * 1.7;
                // 消えている部屋も外の赤い街明かりで天井や壁がわずかに見える。ガラスには赤黒い空が浅い角度ほど映る
                near += (float3(0.045, 0.007, 0.01) * rv * (1.0 - lit) * (1.0 - 0.75 * _MrpCity.x) + _MrpFltSky.rgb * (0.05 + 0.6 * pow(1.0 - Vn, 5.0))) * paneMask;
                // マンションのベランダ: 窓の下は手すりの板 (中の明かりを遮る)・上の床の縁は街明かりを受けて淡く光る
                float rail = 0.0;
                if (side > 0.5 && isDeck < 0.5)
                {
                    rail = Span(f.y, -1.0, 0.36, fw.y) * (1.0 - groundUse);
                    near *= 1.0 - 0.85 * rail;
                }
                // 停電の間も非常灯の窓がわずかに残る (暗い赤のまま点きっぱなし)
                float emergency = step(frac(h2 * 13.7), 0.07) * lit;
                float pw = i.power;
                near *= max(pw, emergency * 0.75);
                // 共用廊下: 腰の高さの壁の上に、夜通し点く天井の灯の列と玄関の扉
                float3 extra = 0.0;
                float extraAvg = 0.0;
                if (side < -0.5 && isDeck < 0.5 && ground < 0.5)
                {
                    float open = Span(f.y, 0.4, 0.97, fw.y);
                    float lamp = exp(-((f.x - 0.3) * (f.x - 0.3) * 14.0 + (f.y - 0.95) * (f.y - 0.95) * 30.0));
                    float door = Span(f.x, 0.1, 0.34, fw.x) * Span(f.y, 0.4, 0.92, fw.y);
                    float3 hall = pale * (0.06 + 0.22 * lamp) * (1.0 - 0.7 * door) + pale * 1.4 * Band(f.y - 0.955, 0.035, fw.y) * Band(f.x - 0.3, 0.12, fw.x);
                    extra = lerp(hall * open, pale * 0.07, far) * max(pw, 0.12 * step(Hash12(float2(seed, 9.1)), 0.5));
                }
                // 1 階の店: 開いている店は明るいガラスと看板・閉まった店はシャッター
                if (shopRow > 0.5)
                {
                    float sid = floor(g.x / 2.0);
                    float hs = Hash12(float2(sid, seed * 3.3));
                    float openShop = step(hs, 0.45);
                    float sf = frac(g.x / 2.0);
                    float glass = Span(sf, 0.03, 0.97, fw.x * 0.5) * Span(f.y, 0.05, 0.8, fw.y);
                    float hasSign = step(frac(hs * 7.7), 0.6);
                    float signI = Span(sf, 0.05, 0.95, fw.x * 0.5) * Span(f.y, 0.84, 0.96, fw.y) * hasSign;
                    float3 sc = frac(hs * 13.1) < 0.55 ? red : (frac(hs * 13.1) < 0.8 ? warm : pale);
                    float shelves = 0.75 + 0.25 * Band(frac(sf * 6.0) - 0.5, 0.12, fw.x * 3.0);
                    float shutter = 0.55 + 0.45 * Band(frac(f.y * 14.0) - 0.5, 0.35, fw.y * 14.0);
                    float3 shop = glass * (openShop * sc * 0.75 * shelves + (1.0 - openShop) * float3(0.05, 0.03, 0.035) * shutter) + signI * sc * 0.9;
                    // 遠くの平均: 開いた店のガラス (面積 0.71 × 明るさ 0.75 × 棚) + 看板 (面積 0.11)
                    extra += lerp(shop, sc * (openShop * 0.47 + hasSign * 0.1), far) * max(pw, 0.05);
                }
                else if (ground > 0.5 && (fac > 1.5 && fac < 3.5))
                {
                    // マンション・戸建ての 1 階: 入口だけ灯る
                    float ent = step(Hash12(float2(id.x, seed * 2.7)), fac < 2.5 ? 0.18 : 0.3) * Span(f.x, 0.3, 0.7, fw.x) * Span(f.y, 0.05, 0.75, fw.y);
                    extra += lerp(pale * 0.35 * ent, pale * 0.03, far) * max(pw, 0.1);
                }
                // 遠くの平均: 点く割合 × 平均の色 × 窓の面積 (窓の無い壁は 0)
                float area = (r.y - r.x) * (r.w - r.z) * pr * (1.0 - blank);
                if (fac > 5.5 && fac < 6.5 && isDeck < 0.5) area = (r.y - r.x) * (r.w - r.z) * 0.16;
                if (wcls > 1.5) area = 0.08 * 0.3 * 0.75;
                area *= 1.0 - groundUse;
                if (side > 0.5) area *= 0.7;
                float3 avg = (red * 0.68 + warm * 0.22 + pale * 0.1) * (litRatio + 0.05) * area * 0.67 * 0.85;
                if (isDeck > 0.5) avg = pane * 0.55 * 0.85;
                avg *= max(pw, 0.07 * 0.75);
                float3 win = lerp(near, avg, far) + extra;
                // 窓枠の線 (ガラスの壁だけ)。窓の無い壁と小さな窓の壁には描かない
                float2 lw = fac < 0.5 ? float2(0.07, 0.11) : float2(0.1, 0.05);
                if (fac > 7.5 && fac < 8.5) lw = float2(0.05, 0.0);
                float lineCov = 0.0;
                [branch] if (ln > 0.0 && blank < 0.5) lineCov = Lines(g, lw, fw) * step(0.5, has + step(fac, 1.5));
                float lineI = ln * bright * (1.0 - groundUse);
                float3 lineCol = float3(1.0, 0.5, 0.56);
                float flags = floor(i.col.a * 255.0 + 0.5);
                float fl = Flicker(seed, fmod(flags, 8.0));
                // 窓の無い所の壁の面: 棟ごとの明るさの違いと目地
                float tone = 0.8 + 0.6 * Hash12(float2(seed * 1.9, 2.6));
                float wallI = WallFace(g, fw, tone, blank);
                // ベランダの床の縁 (帯) は少し明るい
                if (side > 0.5) wallI *= 1.0 + 0.5 * Span(f.y, 0.8, 1.0, fw.y) * (1.0 - far * 0.6);
                // 窓のある壁は床の縁の帯が淡く光る (窓の並びの階が読める)
                float slab = Band(frac(g.y + 0.5) - 0.5, 0.05, fw.y) * (1.0 - blank) * (1.0 - step(0.5, ln)) * 0.09 * (1.0 - far * 0.5);
                // 壁の面: 赤黒い空と街明かりの照り返し (停電で暗くなる)
                float3 wallC = float3(0.032, 0.011, 0.015) + float3(0.03, 0.003, 0.006) * (1.0 - 0.75 * _MrpCity.x);
                float3 c = wallC * wallI + (win + lineCol * (lineCov * lineI + slab) * pw) * fl * _MrpCity.z;
                // 燃えている棟: 一部の窓の奥で火が揺れ、枠は煤けて暗い
                if (flags >= 64.0 && isDeck < 0.5)
                {
                    float fire = step(Hash12(id * 0.71 + seed * 5.3), 0.16) * max(has, ground);
                    float flick = 0.55 + 0.45 * sin(_Time.y * (9.0 + 7.0 * h) + h2 * 40.0) * sin(_Time.y * 4.3 + h * 17.0);
                    float3 fireC = float3(1.0, 0.42, 0.1) * flick;
                    float fm = max(paneMask, ground * Span(f.y, 0.05, 0.8, fw.y));
                    c = lerp(c, c * 0.45, 0.6) + lerp(fireC * fire * fm, fireC * 0.16 * 0.55 * saturate(area * 2.0 + 0.2), far) * (1.0 - i.ruin.x);
                }
                // 下の街明かりで足元ほど赤く照らされる
                float hgt = i.wp.y - _MrpCity.y;
                c += float3(0.05, 0.006, 0.008) * exp(-hgt / 90.0) * (1.0 - 0.75 * _MrpCity.x) * (0.8 + 0.4 * tone);
                return c;
            }

            // 車線の標示 (lat = 道の中心からの横の距離・a = 道に沿った位置・rw = 車道の半分の幅)。
            // 細い道 = 中央の破線と外側線。広い通り = 中央分離帯の縁・片側 6 車線の破線・外側線
            float RoadMarks(float lat, float a, float fl, float fa, float wide, float rw)
            {
                float d = abs(lat);
                float edge = Band(d - (rw - 0.7), 0.25, fl);
                if (wide < 0.5) return max(edge, Band(lat, 0.3, fl) * Dashes(a, 6.0, 12.0, fa));
                float t = (d - 5.0) / 5.6;
                float k = clamp(floor(t + 0.5), 1.0, 5.0);
                float lane = Band((t - k) * 5.6, 0.3, fl) * Dashes(a, 8.0, 20.0, fa);
                return max(max(edge, lane), Band(d - 5.6, 0.25, fl));
            }

            // 0 から x 秒までに ramp (0→1 を 3 秒) が積もる量。車が止まっていた秒の計算に使う (FlightCity.cs の CarLost と同じ式)
            float Ramp2(float x) { return x <= 0.0 ? 0.0 : (x < 3.0 ? x * x / 6.0 : x - 1.5); }

            // 小さな灯 (半径 r)。画素より小さくなったら画素の大きさへ広げて同じだけの光にする (遠くでちらつかない)
            float Dot(float2 p, float r, float f)
            {
                float g = max(r, f * 0.8);
                return exp(-dot(p, p) / (g * g)) * (r * r) / (g * g);
            }

            // 車線ごとの止まりの表 (FlightCars.cs が書く・8 × 1536 を点で引く)。1 車線に 1 行:
            // 0 = 止まりのある区間の番号 2 つ (道に沿った位置 / 192 の切り捨て・無ければ大きな負)、1〜2 と 3〜4 = 区間ごとの
            // (止まる所 pb・長さ・始まった車の時計 Tb・1 = 燃えた車が道をふさぐ / 0 = ビルの前で止まるだけ)
            // (撃たれた車の印 24 個 = ふさいだ車の 18 升後ろから 5 升先まで・最後に撃たれた時計・ふさいだ車が撃たれた時計・0)
            sampler2D _MrpCarTex;
            float _MrpCarOn;
            float4 _MrpCarAlm[4]; // 防犯アラームが鳴っている所 (街の xz・半径・鳴り始めた時計)
            float4 _MrpCarJam[8]; // 車の立体が描く渋滞 (車線の行・区間の枠)。ここに無い渋滞の車は近くでも絵で描く
            float _MrpCar3D;      // 車の立体が出ている (0 = 高く飛んでいる間・作れなかった時は近くも絵で描く)

            float JamIn3D(float row, float slot)
            {
                float hit = 0;
                [unroll] for (int i = 0; i < 8; i++) hit = max(hit, step(abs(_MrpCarJam[i].x - row) + abs(_MrpCarJam[i].y - slot), 0.1));
                return hit;
            }

            float4 CarTexel(float lane, float k)
            {
                return tex2Dlod(_MrpCarTex, float4((k + 0.5) / 8.0, (lane + 0.5) / 1536.0, 0, 0));
            }

            // 印 m の i 番目 (0..23) が立っているか
            float Bit(float m, float i) { return fmod(floor(m / exp2(i)), 2.0); }

            // 1 台を描く。y = 車の後ろから前へ (0..4.6 が車体)・x = 車線の中心からの横・tone = 色の種・brake = ブレーキ灯 (0..1)・
            // haz = 非常点滅灯 (0..1)・burn = 燃えた殻 (0 = 普通の車・1 = 燃えている・小さいほど冷めた)・
            // w = 絵で描く割合 (カメラの近くは車の立体 (FlightCar) が描くので 0。路面には車の下の影と前照灯が照らす光だけを残す)
            float3 CarLook(float y, float x, float tone, float brake, float haz, float burn, float w, float fl, float fa, inout float alb)
            {
                float body = Band(y - 2.3, 4.6, fa) * Band(x, 1.85, fl);
                // 車の下の影 (車体より少し広くぼかす)
                float shade = saturate(1.0 - abs(y - 2.3) / 2.75) * saturate(1.0 - abs(x) / 1.15);
                alb *= 1.0 - (1.0 - w) * 0.8 * saturate(shade * 2.5);
                body *= w;
                if (burn > 0.0)
                {
                    // 焼けた殻: 煤けた黒と、まだ燃えている所の揺らぐ火
                    alb = lerp(alb, 0.012, body);
                    float2 g = floor(float2(y * 1.6, x * 2.2));
                    float f = Hash12(g + tone * 31.0);
                    float flick = 0.55 + 0.45 * sin(_MrpFltLights[1].w * (7.0 + 6.0 * f) + f * 6.3);
                    float heat = step(1.0 - (0.25 + 0.6 * burn), f) * flick * (0.08 + 1.8 * burn * burn);
                    return float3(1.0, 0.32, 0.06) * heat * body;
                }
                alb = lerp(alb, tone < 0.35 ? 0.03 : (tone < 0.7 ? 0.32 : (tone < 0.9 ? 0.18 : 0.09)), body);
                float ax = abs(x) - 0.65;
                float3 e = float3(1.0, 0.92, 0.8) * 3.0 * Dot(float2(ax, y - 4.5), 0.28, max(fl, fa));
                e += float3(1.0, 0.05, 0.03) * 1.6 * (1.0 + 2.2 * brake) * Dot(float2(ax, y - 0.1), 0.24 + 0.06 * brake, max(fl, fa));
                float ox = abs(x) - 0.85;
                e += float3(1.0, 0.45, 0.05) * 2.2 * haz * (Dot(float2(ox, y - 4.4), 0.2, max(fl, fa)) + Dot(float2(ox, y - 0.2), 0.2, max(fl, fa)));
                e *= w;
                // 前照灯が前の路面を照らす (前へ 18・先ほど広がる)
                float ahead = y - 4.6;
                float bw = 1.1 + ahead * 0.12;
                e += float3(1.0, 0.9, 0.75) * 0.07 * step(0.0, ahead) * saturate(1.0 - ahead / 18.0) * exp(-x * x / (bw * bw));
                return e;
            }

            // 車線を走る車 (左側通行)。lat = 道の中心からの横の距離・a = 道に沿った位置・id = 道の番号。
            // 車体は地面の色を差し替え (alb)、前照灯・尾灯・前照灯が路面を照らす光は自ら光る分として返す。
            // 時計は _MrpFltLights[1].w (場面の秒)。停電: 前線が届くと 3 秒で止まって非常点滅灯を点け、明かりが戻る頃に走り出す。
            // 止まっていた分は警報の後に _MrpCity.w が引き継ぐので、走りが飛ばない。
            // 燃えた車やビルの崩れで道がふさがると、その区間では後ろの車が前から順に詰めて止まり (車間 1.5〜3)、
            // 区間が埋まった後に来た車は手前の交差点で曲がって来なくなる。式は FlightCars.cs と同じ
            float3 Cars(float lat, float a, float fl, float fa, float wide, float id, float2 wxz, inout float alb)
            {
                float d = abs(lat);
                float dir = lat > 0.0 ? -1.0 : 1.0;
                float k = wide > 0.5 ? clamp(floor((d - 5.0) / 5.6), 0.0, 5.0) : 0.0;
                float x = d - (wide > 0.5 ? 5.0 + 5.6 * (k + 0.5) : 4.75);
                if (abs(x) > 2.4) return 0;
                // 車線の種は整数 (どの端末でも同じ値)。速さは時刻で何百倍にもなるので、端数の計算の誤差が増えない式で出す
                float lane = id * 13.0 + k + (dir > 0.0 ? 0.0 : 7.0) + wide * 8000.0;
                float v = (wide > 0.5 ? 14.0 : 9.0) + 6.0 * frac(lane * 0.618034);
                float clk = _MrpFltLights[1].w;
                float s = _MrpCityAlarm.x;
                float cut = min(length(wxz - _MrpCityAlarm.zw) / 900.0, 2.0);
                float B = _MrpCityAlarm.y - 1.0;
                float stopped = saturate((s - cut) / 3.0) * (1.0 - saturate((s - cut - B) / 3.0));
                float T = clk - _MrpCity.w - (Ramp2(s - cut) - Ramp2(s - cut - B));
                const float Lc = 24.0;
                const float L = 4.6;
                float p = a * dir;
                float tone = 0, y = -100, brake = 0, burn = 0;
                float lo = -1e9; // 描いてよい車の識別 (時刻 0 の道に沿った位置) の下限
                bool free = true;
                float jamPaint = 0; // 立体の渋滞の枠に載っていない渋滞の車 (近くでも絵で描く)
                if (_MrpCarOn > 0.5)
                {
                    float axis = id > 300.0 ? 1.0 : 0.0;
                    float li = id - axis * 517.0;
                    float row = ((axis * 64.0 + li + 32.0) * 2.0 + (dir > 0.0 ? 0.0 : 1.0)) * 6.0 + k;
                    float4 hd = CarTexel(row, 0);
                    float seg = floor(p / 192.0);
                    float slot = hd.x == seg ? 0.0 : (hd.y == seg ? 1.0 : -1.0);
                    if (slot >= 0.0)
                    {
                        float4 A = CarTexel(row, 1.0 + slot * 2.0);
                        float4 Bk = CarTexel(row, 2.0 + slot * 2.0);
                        float pb = A.x, len = A.y, Tb = A.z, crush = A.w;
                        float u0 = pb - v * Tb;
                        float c0 = floor(u0 / Lc) - 18.0;
                        jamPaint = 1.0 - JamIn3D(row, slot);
                        if (p < pb)
                        {
                            free = false;
                            // 区間の中で止まる車: 前から順に、詰めて止まる所 st へ向けてブレーキを踏む
                            float dT = max(T - Tb, 0.0);
                            float K = max(v * v / 20.0, 1.0);
                            float gap0 = crush > 0.5 ? 2.5 : 4.0;
                            float cf = floor((u0 - L) / Lc);
                            // 並ぶのは手前の交差点の横断歩道より先まで (交差点はふさがない)
                            float edge = seg * 192.0 + (fmod(abs(seg), 4.0) < 0.5 ? 48.0 : 15.0) + 1.0;
                            float st = 0, idx = 0;
                            // 後ろから来た車も、区間が埋まるまで (印 18 升 = 平均 7 台ほど) は交差点を抜けて並ぶ
                            [loop] for (int j = 0; j < 18; j++)
                            {
                                float c = cf - j;
                                if (Hash12(float2(c, lane)) > 0.42) continue;
                                float r = c * Lc + 1.0 + Hash12(float2(c + 1291.0, lane)) * 16.0;
                                if (r > u0 - L) continue;
                                float xtb = pb + r - u0;
                                st = idx < 0.5 ? pb - L - min(gap0, max(pb - xtb - L, 0.0)) : st - L - (1.5 + 1.5 * Hash12(float2(c + 3637.0, lane)));
                                if (st < edge) break;
                                idx += 1.0;
                                float kk = max(min(min(K, st - xtb), st - seg * 192.0 + 0.5), 0.05); // 交差点の手前ではまだ踏まない (絵が途切れない)
                                float f = xtb + v * dT;
                                float z = f - st;
                                float xc = f - (z <= -kk ? 0.0 : (z < kk ? (z + kk) * (z + kk) / (4.0 * kk) : z));
                                float yy = p - xc;
                                if (yy >= -1.0 && yy <= L + 1.0)
                                {
                                    y = yy;
                                    tone = Hash12(float2(c + 2477.0, lane));
                                    brake = saturate((z + kk) / (2.0 * kk));
                                    float bi = c - c0;
                                    if (bi >= 0.0 && bi < 24.0 && Bit(Bk.x, bi) > 0.5) burn = saturate(1.0 - (clk - Bk.y) / 40.0) + 0.02;
                                    break;
                                }
                                if (xc + L < p - 2.0) break;
                            }
                        }
                        else
                        {
                            // ふさいだ所より先: 抜けていった車だけ (燃えた車がふさいだ時はその車と重なっていた車は描かない)
                            lo = crush > 0.5 ? u0 + len : u0 - L;
                            if (crush > 0.5 && p < pb + L + 1.0)
                            {
                                free = false;
                                y = p - pb;
                                tone = Hash12(float2(floor(u0 / Lc) + 2477.0, lane));
                                burn = saturate(1.0 - (clk - Bk.z) / 40.0) + 0.02;
                            }
                        }
                    }
                }
                if (free)
                {
                    float u = p - v * T;
                    float cell = floor(u / Lc);
                    if (Hash12(float2(cell, lane)) > 0.42) return 0;
                    float off = 1.0 + Hash12(float2(cell + 1291.0, lane)) * 16.0;
                    if (cell * Lc + off <= lo) return 0;
                    y = u - cell * Lc - off;
                    tone = Hash12(float2(cell + 2477.0, lane));
                }
                if (y < -1.0 || y > L + 1.0) return 0;
                // 防犯アラーム: 近くで爆発があると、止まっている車も走っている車も 7 割が非常点滅灯を速く点滅させる
                float2 lq = wxz - _MrpCityOrg.xy;
                float alarm = 0;
                [unroll] for (int m = 0; m < 4; m++)
                {
                    float2 dd = lq - _MrpCarAlm[m].xy;
                    float age = clk - _MrpCarAlm[m].w;
                    alarm = max(alarm, step(dot(dd, dd), _MrpCarAlm[m].z * _MrpCarAlm[m].z) * step(0.0, age) * step(age, 38.0) * step(Hash12(float2(tone * 57.0, m)), 0.7));
                }
                // 船が真上を低く抜けると、運転手がブレーキを踏む
                float2 sd = lq - float2(_MrpFltLights[3].w, _MrpFltLights[5].w);
                brake = max(brake, saturate(1.0 - length(sd) / 60.0) * saturate(1.0 - _MrpFltLights[7].w / 90.0));
                float blink = step(frac(clk * 1.5 + tone * 7.0), 0.5) * saturate(stopped * 3.0 - 2.0);
                blink = max(blink, step(frac(clk * 2.4 + tone * 3.0), 0.5) * alarm);
                // カメラの近くの車は立体 (FlightCar) が描く。遠くへ向けて同じ距離で絵へ渡す (FlightCarCommon の CAR_FAR_R・CAR_FLAT)
                float w = saturate((distance(_WorldSpaceCameraPos, float3(wxz.x, _MrpCity.y + 0.7, wxz.y)) - 130.0) / 30.0);
                if (!free) w = max(w, jamPaint);
                w = max(w, 1.0 - _MrpCar3D);
                float3 e = CarLook(y, x, tone, burn > 0.0 ? 0.0 : brake, burn > 0.0 ? 0.0 : blink, burn, w, fl, fa, alb);
                return e * _MrpCity.z;
            }

            // 地面 = 車道 (アスファルト・車線・横断歩道・停止線)・歩道 (敷石と縁石)・敷地 (舗装)。
            // 色は反射の割合で決め、空の照り返しと街灯の溜まりに照らされた分だけ見える (標示は自ら光らないので停電で消える)。
            // alb = 砲口の閃光や爆発に照らされる時の反射の割合
            float3 Ground(float3 wp, float dist, out float alb)
            {
                // 区画の間隔と道の半分の幅 (flight_city.cpp の Pitch・RoadW と同じ値)
                const float P = 192.0;
                float2 q = wp.xz - _MrpCityOrg.xy;
                float2 li = floor(q / P + 0.5);
                float2 s = q - li * P;              // 道の中心からの距離 (x = z に沿う道・y = x に沿う道)
                float2 d = abs(s);
                float2 wide = step(fmod(abs(li), 4.0), 0.5);
                float2 hw = lerp(15.0, 48.0, wide); // 歩道の外まで
                float2 rw = lerp(9.5, 39.0, wide);  // 車道
                float2 fw = max(fwidth(q), 1e-3);
                float fm = max(fw.x, fw.y);
                float2 band = 1.0 - smoothstep(hw - fw, hw + fw, d);
                float2 road = 1.0 - smoothstep(rw - fw, rw + fw, d);
                float onBand = max(band.x, band.y);
                float onRoad = max(road.x, road.y);
                float walk = onBand * (1.0 - onRoad);
                // 地面は画面の多くを占めるので、道の外 (敷地) では標示を計算しない
                float med = 0.0;
                float3 emis = 0;
                if (onBand <= 0.0) alb = 0.12 * (1.0 - 0.3 * Lines(q / 4.0, 0.03, fw / 4.0));
                else
                {
                    // 車線は交差点の外 (相手の道の歩道より外) だけ
                    float outA = smoothstep(hw.y + 0.5, hw.y + 1.5, d.y);
                    float outB = smoothstep(hw.x + 0.5, hw.x + 1.5, d.x);
                    float marks = max(road.x * outA * RoadMarks(s.x, q.y, fw.x, fw.y, wide.x, rw.x),
                                      road.y * outB * RoadMarks(s.y, q.x, fw.y, fw.x, wide.y, rw.y));
                    // 横断歩道: 相手の道の歩道の続き。縞は道に沿う向き (幅 0.9・間隔 1.8)
                    float cwA = Band(d.y - (rw.y + hw.y) * 0.5, hw.y - rw.y - 1.6, fw.y) * Band(s.x, 2.0 * rw.x - 2.0, fw.x) * Dashes(s.x, 0.9, 1.8, fw.x);
                    float cwB = Band(d.x - (rw.x + hw.x) * 0.5, hw.x - rw.x - 1.6, fw.x) * Band(s.y, 2.0 * rw.y - 2.0, fw.y) * Dashes(s.y, 0.9, 1.8, fw.y);
                    // 停止線: 横断歩道の手前・交差点へ向かう側の車線だけ (左側通行)
                    float stA = Band(d.y - (hw.y + 2.0), 0.6, fw.y) * road.x * step(lerp(0.3, 5.6, wide.x), d.x) * step(0.0, s.x * s.y);
                    float stB = Band(d.x - (hw.x + 2.0), 0.6, fw.x) * road.y * step(lerp(0.3, 5.6, wide.y), d.y) * step(s.x * s.y, 0.0);
                    marks = max(max(marks, max(cwA, cwB)), max(stA, stB));
                    // 広い通りの中央分離帯 (高架の橋脚が立つ)
                    med = max(wide.x * (1.0 - smoothstep(5.0 - fw.x, 5.0 + fw.x, d.x)) * outA,
                              wide.y * (1.0 - smoothstep(5.0 - fw.y, 5.0 + fw.y, d.y)) * outB);
                    // 縁石: 車道と歩道の境 (相手の車道の中では切れる)
                    float curb = max(Band(d.x - rw.x, 0.4, fw.x) * step(rw.y, d.y), Band(d.y - rw.y, 0.4, fw.y) * step(rw.x, d.x));
                    // 素材: アスファルトは 3 の升ごとのむらと補修の跡・歩道は 1.5 の敷石
                    float grain = lerp(Hash12(floor(q / 3.0)), 0.5, saturate(fm / 3.0 * 2.0 - 0.5));
                    float patch = step(Hash12(floor(q / 11.0) + 7.3), 0.12);
                    float asph = 0.065 * (0.85 + 0.3 * grain) * (1.0 - 0.25 * patch);
                    float walkA = 0.17 * (1.0 - 0.35 * Lines(q / 1.5, 0.06, fw / 1.5));
                    alb = lerp(lerp(0.12, walkA, walk), asph, onRoad);
                    alb = lerp(alb, 0.1, med);
                    alb = lerp(alb, 0.3, curb * (1.0 - med));
                    alb = lerp(alb, 0.55, marks * onRoad * (1.0 - med));
                    if (road.x > 0.0) emis += Cars(s.x, q.y, fw.x, fw.y, wide.x, li.x, wp.xz, alb);
                    if (road.y > 0.0) emis += Cars(s.y, q.x, fw.y, fw.x, wide.y, li.y + 517.0, wp.xz, alb);
                }
                // 街灯: 縁石の 1.2 外に 32 ごと (交差点の中には立たない)
                float2 lc = floor(q.yx / 32.0 + 0.5) * 32.0;          // 一番近い灯の道に沿った位置 (x = z に沿う道の灯の z)
                float2 lo = q.yx - lc;
                float2 e = d - (rw + 1.2);
                float2 ok = step(hw.yx, abs(lc - li.yx * P));
                float pool = max(exp(-(e.x * e.x + lo.x * lo.x) / 110.0) * ok.x, exp(-(e.y * e.y + lo.y * lo.y) / 110.0) * ok.y);
                // 広い通りは中央分離帯にも背の高い灯 (光の輪が広い)
                pool = max(pool, max(wide.x * ok.x * exp(-(d.x * d.x + lo.x * lo.x) / 300.0), wide.y * ok.y * exp(-(d.y * d.y + lo.y * lo.y) / 300.0)));
                // 遠くでは溜まりの点が見分けられないので、道の上の平均の明るさに寄せる
                float far = saturate(fm / 8.0 - 0.3);
                pool = lerp(pool, onBand * 0.15, far);
                // 停電: 街灯は消える。ごく一部だけ電気が残り、切れかけて不規則にまたたく
                float2 lamp = floor(q / 32.0 + 0.5);
                float pw = Power(wp.xz, Hash12(lamp) * 97.0);
                float alive = step(Hash12(lamp * 1.91 + 3.7), 0.04) * step(Hash12(lamp + floor(_Time.y * 9.0)), 0.7);
                float3 lampL = float3(1.0, 0.16, 0.07) * 2.2 * pool * _MrpCity.z * Flicker(li.x * 7.0 + li.y * 3.0, 0) * max(pw, lerp(alive * 0.8, 0.02, far));
                // 空の照り返し (停電で暗くなる)
                float3 amb = float3(0.24, 0.11, 0.12) * (1.0 - 0.6 * _MrpCity.x) * _MrpCity.z;
                // 濡れた車道は灯の光を鈍く照り返す
                return alb * (amb + lampL) + lampL * onRoad * (1.0 - med) * 0.05 + emis;
            }

            // 焼いた街の地面 (区分ごとの面)。cls = 焼く道具の区分の番号・q = 街の中心からの位置 (ゲームの単位)。
            // 色は反射の割合で決め、空の照り返しと街灯の溜まりに照らされた分だけ見える (Ground と同じ光)
            float3 GroundReal(float3 wp, float cls, float2 q, float seed, float dist, out float alb)
            {
                float2 fw = max(fwidth(q), 1e-3);
                float fm = max(fw.x, fw.y);
                float grain = lerp(Hash12(floor(q / 3.0)), 0.5, saturate(fm / 3.0 * 2.0 - 0.5));
                float isRoad = 0.0, isWalk = 0.0, water = 0.0;
                if (cls > 8.5)                      // 車道・交差点の中 (細かい車線の面がある所)
                {
                    float patch = step(Hash12(floor(q / 11.0) + 7.3), 0.12);
                    alb = (cls > 9.5 ? 0.075 : 0.065) * (0.85 + 0.3 * grain) * (1.0 - 0.25 * patch);
                    isRoad = 1.0;
                }
                else if (cls > 7.5)                 // 中央分離帯・島: 植え込み
                {
                    float shrub = Hash12(floor(q / 2.2)) * 0.5 + Hash12(floor(q / 0.9) + 3.1) * 0.5;
                    alb = 0.05 + 0.05 * lerp(shrub, 0.5, saturate(fm / 2.0 - 0.3));
                }
                else if (cls > 6.5)                 // 歩道: 敷石の目地
                {
                    alb = 0.17 * (0.9 + 0.2 * grain) * (1.0 - 0.35 * Lines(q / 1.8, 0.06, fw / 1.8));
                    isWalk = 1.0;
                }
                else if (cls > 5.5)                 // 車線の面の無い所の道路全体
                {
                    alb = 0.07 * (0.85 + 0.3 * grain);
                    isRoad = 1.0;
                }
                else if (cls > 4.5)                 // 水面
                {
                    alb = 0.02;
                    water = 1.0;
                }
                else if (cls > 2.5)                 // 平面駐車場: アスファルトに区画の白線 (7.5 ごと・奥行 15 で区切り)
                {
                    float stall = Band(abs(frac(q.x / 7.5 + 0.5) - 0.5) * 7.5, 0.35, fw.x) * step(frac(q.y / 15.0), 0.85);
                    alb = 0.07 * (0.85 + 0.3 * grain);
                    alb = lerp(alb, 0.4, stall * (1.0 - saturate(fm / 2.0)));
                    isRoad = 0.5;
                }
                else if (cls > 1.5)                 // 公園・緑地: 芝と木立の濃淡
                {
                    float canopy = Hash12(floor(q / 9.0)) * 0.6 + Hash12(floor(q / 3.3) + 5.7) * 0.4;
                    alb = lerp(0.045, 0.085, lerp(canopy, 0.5, saturate(fm / 6.0 - 0.3)));
                }
                else if (cls > 0.5)                 // 空き地: 砂利
                    alb = 0.13 * (0.75 + 0.5 * lerp(Hash12(floor(q / 0.8)), 0.5, saturate(fm - 0.5)));
                else                                // 敷地の地面 (建物の間)
                    alb = 0.1 * (0.85 + 0.3 * grain);
                // 街灯: 道と歩道の上だけ、32 ごとの升に 1 つ (升の中で少しずれる)。灯の無い所もある
                float2 cell = floor(q / 32.0);
                float2 lp = (cell + 0.5 + (float2(Hash12(cell + 1.7), Hash12(cell + 9.3)) - 0.5) * 0.6) * 32.0;
                float2 dl = q - lp;
                float has = step(Hash12(cell * 1.37 + 2.1), 0.6);
                float pool = exp(-dot(dl, dl) / 160.0) * has;
                float far = saturate(fm / 8.0 - 0.3);
                pool = lerp(pool, 0.12, far) * max(isRoad, isWalk);
                float pw = Power(wp.xz, Hash12(cell) * 97.0);
                float alive = step(Hash12(cell * 1.91 + 3.7), 0.04) * step(Hash12(cell + floor(_Time.y * 9.0)), 0.7);
                float3 lampL = float3(1.0, 0.16, 0.07) * 2.2 * pool * _MrpCity.z * max(pw, lerp(alive * 0.8, 0.02, far));
                float3 amb = float3(0.24, 0.11, 0.12) * (1.0 - 0.6 * _MrpCity.x) * _MrpCity.z;
                float3 c = alb * (amb + lampL) + lampL * isRoad * 0.05;
                if (water > 0.5)
                {
                    // 川面: さざ波に揺れる空の照り返しと、岸の明かりの縦に伸びた映り (遠いほど鏡に近い)
                    float3 V = normalize(_WorldSpaceCameraPos - wp);
                    float2 rp = q * 0.05 + _Time.y * float2(0.03, 0.017);
                    float rip = sin(rp.x * 7.1 + sin(rp.y * 5.3)) * sin(rp.y * 6.7 + 1.3) + 0.5 * sin(q.x * 0.9 + _Time.y * 1.7) * sin(q.y * 0.7 - _Time.y * 1.1);
                    float fres = pow(1.0 - saturate(V.y), 4.0);
                    float glint = step(0.92, Hash12(floor(float2(q.x / 6.0, q.y / 1.2 + _Time.y * 0.6)))) * (0.6 + 0.4 * rip);
                    c = _MrpFltSky.rgb * (0.4 + 1.8 * fres) * (0.85 + 0.15 * rip) + float3(1.0, 0.25, 0.12) * glint * 0.09 * _MrpCity.z * (1.0 - far);
                    alb = 0.04;
                }
                return c;
            }

            // 高架の上の標示 (中央の黄色い 2 本・片側 3 車線の破線・外側線)。広い通りの真上にあるので、同じ格子から横の距離を出す
            float DeckMarks(float3 wp, out float yellow)
            {
                const float P = 192.0;
                float2 q = wp.xz - _MrpCityOrg.xy;
                float2 li = floor(q / P + 0.5);
                float2 s = q - li * P;
                float2 fw = max(fwidth(q), 1e-3);
                bool alongX = fmod(abs(li.y), 4.0) < 0.5 && abs(s.y) < 23.0;
                float lat = alongX ? s.y : s.x, a = alongX ? q.x : q.y;
                float fl = alongX ? fw.y : fw.x, fa = alongX ? fw.x : fw.y;
                float d = abs(lat);
                yellow = Band(d - 0.45, 0.3, fl);
                float t = d / 6.8;
                float k = clamp(floor(t + 0.5), 1.0, 2.0);
                float lane = Band((t - k) * 6.8, 0.3, fl) * Dashes(a, 8.0, 20.0, fa);
                return max(max(lane, Band(d - 20.6, 0.25, fl)), yellow);
            }

            // 地面を這う霧: 地面に近く・遠いほど濃い。ゆっくり流れて濃淡が動く (FlightLamp と同じ式)
            float GroundMist(float3 wp, float dist)
            {
                float h = max(wp.y - _MrpCity.y, 0.0);
                float2 m = wp.xz * 0.004 + _Time.y * float2(0.011, 0.006);
                float drift = 0.7 + 0.3 * sin(m.x * 6.3 + sin(m.y * 4.1)) * sin(m.y * 5.3 + 1.7);
                return (1.0 - exp(-dist / 900.0)) * exp(-h / 70.0) * 0.75 * drift;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float mat = i.n.w;
                float3 n = normalize(i.n.xyz);
                // 補間された色は画素ごとにわずかにずれるので、種は整数へ丸めてから乱数に入れる (丸めないと窓が画素ごとの点々になる)
                float seed = floor(i.col.r * 255.0 + 0.5);
                float fac = floor(floor(i.col.g * 255.0 + 0.5) / 16.0); // 壁の作り (色 g = 作り × 16 + 8)
                float litRatio = i.col.b;
                float dist = distance(_WorldSpaceCameraPos, i.wp);
                float3 c;
                float albedo = 0.35;
                if (mat < 0.5 || (mat > 3.5 && mat < 4.5)) c = Facade(i, fac, i.wcls, seed, litRatio, mat > 3.5 ? 1.0 : 0.0);
                else if (mat < 1.5)
                {
                    // 屋上: 暗いコンクリート + 空の赤い照り返し
                    c = float3(0.03, 0.018, 0.022) + float3(0.04, 0.004, 0.008) * (0.6 + 0.4 * Hash12(float2(seed, 2.0)));
                }
                else if (mat < 2.5)
                {
                    // 塔の鉄骨: 下から照らされて自ら光って見える (上へ行くほど少し暗い)
                    float hgt = i.wp.y - _MrpCity.y;
                    // a = 1 は背の高い塔 (634 m の塔・上まで照らされる)
                    float span = i.col.a > 0.002 ? 1900.0 : 780.0;
                    c = i.col.rgb * (1.25 - hgt / span) * (0.75 + 0.25 * n.y * n.y);
                    // 停電の間は下からの光が消え、てっぺんと展望台の障害灯の脈にだけ赤く浮かぶ
                    float p = frac(max(_MrpCityAlarm.x, 0.0) / 2.2) * 2.2;
                    float beat = smoothstep(0.0, 0.08, p) * exp(-p / 0.8);
                    c *= lerp(0.08 + 0.4 * beat * saturate(hgt / 300.0), 1.0, i.power);
                    albedo = 0.1;
                }
                else if (mat < 3.5)
                {
                    float gcls = floor(i.col.g * 255.0 + 0.5) - 1.0;
                    if (gcls >= 0.0) c = GroundReal(i.wp, gcls, i.uv, seed, dist, albedo);
                    else c = Ground(i.wp, dist, albedo);
                    albedo *= 2.0; // 閃光は近くの強い光なので、暗い路面でも照らされた形が分かる程度に
                }
                else
                {
                    // 高架の道路のコンクリート (縁の灯に下から照らされる)
                    c = float3(0.05, 0.035, 0.04) + float3(0.12, 0.03, 0.015) * saturate(n.y) * i.power;
                    if (n.y > 0.9)
                    {
                        float yellow;
                        float m = DeckMarks(i.wp, yellow);
                        float3 paint = lerp(float3(1.0, 1.0, 1.0), float3(1.0, 0.75, 0.2), yellow);
                        c += paint * m * (float3(0.06, 0.04, 0.045) + float3(0.3, 0.07, 0.035) * i.power);
                    }
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
                // 霞: 遠いほど赤黒い霞へ。地面の近くは街の明かりを受けて少し明るい霧が這う
                float fog = 1.0 - exp(-dist / max(_MrpFltSky.a, 1.0));
                // 霧は地面から 320 より上ではほぼ 0 なので計算しない (ビルの上の方の画素の分を省く)
                if (i.wp.y - _MrpCity.y < 320.0) c =lerp(c, _MrpFltSky.rgb * 1.35, GroundMist(i.wp, dist));
                c = lerp(c, _MrpFltSky.rgb, fog);
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
