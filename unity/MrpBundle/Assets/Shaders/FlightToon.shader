// 3D の艦隊戦の船。頂点の色 (a < 1 = 光る強さ) を 3 段の明暗で塗り、太い縁取りを付ける (本編の物と同じ「太い縁 + 段の陰」の様式)。
// Android の削られた Unity では Light の設定が使えないので、光の向きと色は global の値 (_MrpFltLight・_MrpFltSky) で受ける。
// 縁取りは面を裏返して外へ押し出す 2 つ目の描き方。押し出す向きは頂点の TEXCOORD1 (同じ位置の面の法線を足したなめらかな法線) で、
// 画面の上で同じ太さになるよう奥行き (w) を掛ける。遠いほど空の色へ寄せて奥行きを出す。
// 動く部品 (翼・舵・噴射口など): 頂点の TEXCOORD1.w が部品の番号 (0 = 船体)。部品の姿は global の配列 _MrpFltParts に
// 部品ごとに 2 つ (四元数・ずれ xyz と大きさ w) で入り、船ごとの始まりは _PartBase (-1 = 動かさない)。
// 頂点は 8 ビットに詰めてある: NORMAL = 陰の法線 xyz + 材質の番号 w (番号 / 127)・TEXCOORD1 = 縁取りを押し出す向き xyz
// (長さ = 太さの割合。細い部品の縁取りを細くする) + 部品の番号 w (番号 / 127)。
// 材質の番号: 0 = 無地 / 1 = 外板 (継ぎ目と点検口) / 2 = 細かい塗装 / 3 = 耐熱タイル / 4 = 放熱板 /
// 5 = 金色の断熱材 (しわの照り) / 6 = 太陽電池 / 7 = 金属 / 8 = 窓のガラス / 9 = 光る物。
// 模様は部品が動く前の位置 (形の座標) で描くので、動く部品に貼り付いたまま動く。模様の線は画面の上で 1 画素より細くなる距離で消す
// (ギザギザ消しが無いので、細かい線をそのまま描くとちらつく)。
Shader "MRP/FlightToon"
{
    Properties
    {
        _Outline ("Outline width", Float) = 0.0035
        _OutlineColor ("Outline color", Color) = (0.05, 0.05, 0.08, 1)
        _Flash ("Hit flash", Float) = 0
        _PartBase ("Part base", Float) = -1
    }

    CGINCLUDE
    float4 _MrpFltParts[192];
    float _PartBase;

    float3 QRot(float4 q, float3 v)
    {
        float3 t = 2.0 * cross(q.xyz, v);
        return v + q.w * t + cross(q.xyz, t);
    }

    // 部品の動きを位置と向き (法線 2 本) に掛ける。返り値 = 部品の大きさ (縁取りの太さにも掛ける)
    float ApplyPart(float part, inout float4 pos, inout float3 n1, inout float3 n2)
    {
        part = floor(part * 127.0 + 0.5);
        if (part < 0.5 || _PartBase < 0) return 1.0;
        int i = (int)(_PartBase + (part - 1.0) * 2.0 + 0.5);
        float4 q = _MrpFltParts[i];
        float4 t = _MrpFltParts[i + 1];
        pos.xyz = QRot(q, pos.xyz * t.w) + t.xyz;
        n1 = QRot(q, n1);
        n2 = QRot(q, n2);
        return t.w;
    }
    ENDCG

    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" "IgnoreProjector" = "True" }

        Pass
        {
            Cull Back
            ZWrite On
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float4 _MrpFltLight;   // xyz = 光の来る向き (世界)・w = 強さ
            float4 _MrpFltSky;     // rgb = 遠くの色・a = 霞む距離
            float4 _MrpFltRim;     // rgb = 縁の照り返しの色・a = 強さ
            float _Flash;

            struct appdata { float4 vertex : POSITION; float4 normal : NORMAL; fixed4 color : COLOR; float4 smooth : TEXCOORD1; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 n : TEXCOORD0;      // xyz = 世界の法線・w = 材質の番号
                float3 wp : TEXCOORD1;
                float3 lp : TEXCOORD2;     // 形の座標 (部品が動く前)
                float3 ln : TEXCOORD3;     // 形の座標の法線
                fixed4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.lp = v.vertex.xyz;
                o.ln = v.normal.xyz;
                float3 dummy = v.smooth.xyz, nrm = v.normal.xyz;
                ApplyPart(v.smooth.w, v.vertex, nrm, dummy);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.n = float4(UnityObjectToWorldNormal(nrm), floor(v.normal.w * 127.0 + 0.5));
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.color = v.color;
                return o;
            }

            float Hash12(float2 p)
            {
                float3 q = frac(p.xyx * 0.1031);
                q += dot(q, q.yzx + 33.33);
                return frac((q.x + q.y) * q.z);
            }

            float3 Hash33(float3 p)
            {
                p = frac(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yxz + 33.33);
                return frac((p.xxy + p.yxx) * p.zyx);
            }

            // 滑らかな乱れ (3 成分・0..1)
            float3 Noise3(float3 p)
            {
                float3 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float3 a = lerp(Hash33(i), Hash33(i + float3(1, 0, 0)), f.x);
                float3 b = lerp(Hash33(i + float3(0, 1, 0)), Hash33(i + float3(1, 1, 0)), f.x);
                float3 c = lerp(Hash33(i + float3(0, 0, 1)), Hash33(i + float3(1, 0, 1)), f.x);
                float3 d = lerp(Hash33(i + float3(0, 1, 1)), Hash33(i + float3(1, 1, 1)), f.x);
                return lerp(lerp(a, b, f.y), lerp(c, d, f.y), f.z);
            }

            // 升目の線 (uv の整数の所に幅 w の線)。fw = uv の 1 画素あたりの変化 (ずらした座標は飛ぶので、ずらす前の座標で測って渡す)。
            // 升が画面の上で小さくなると消える
            float GridLineFw(float2 uv, float2 w, float2 fw)
            {
                float2 d = abs(frac(uv + 0.5) - 0.5);
                float2 m = (1.0 - smoothstep(w, w + fw * 1.5, d)) * step(1e-4, w);
                float fade = 1.0 - smoothstep(0.12, 0.3, max(fw.x, fw.y));
                return max(m.x, m.y) * fade;
            }

            float GridLine(float2 uv, float2 w)
            {
                return GridLineFw(uv, w, max(fwidth(uv), 1e-5));
            }

            // 外板: 段ごとに継ぎ目の位置が違う板・板ごとの明るさの差・まれに点検口の枠。
            // 返り値 x = 明るさの掛け算・y = 継ぎ目の濃さ・z = 継ぎ目の下の縁の明るさ (彫った線の片側が光る)
            float3 Panels(float2 p, float2 size)
            {
                float2 uv0 = p / size;
                float2 fw = max(fwidth(uv0), 1e-5);
                float row = floor(uv0.y);
                float2 uv = float2(uv0.x + Hash12(float2(row, 3.7)), uv0.y);
                float h = Hash12(floor(uv));
                float2 w = float2(0.012, 0.02);
                float seam = GridLineFw(uv, w, fw);
                float fade = 1.0 - smoothstep(0.12, 0.3, max(fw.x, fw.y));
                // 横の継ぎ目の下側に細い光る縁
                float dy = frac(uv.y + 0.5) - 0.5;
                float lip = (1.0 - smoothstep(w.y * 1.6, w.y * 1.6 + fw.y * 1.5, abs(dy - w.y * 2.4))) * fade;
                float hatch = 0;
                if (h > 0.93)
                {
                    float2 f = frac(uv) - 0.5;
                    float2 hs = float2(0.16, 0.2);
                    float2 e = abs(abs(f) - hs);
                    float2 inside = step(abs(f), hs + 0.01);
                    float2 m = (1.0 - smoothstep(0.008, 0.008 + fw * 1.5, e)) * inside.yx;
                    hatch = max(m.x, m.y) * (1.0 - smoothstep(0.06, 0.15, max(fw.x, fw.y)));
                }
                // 遠目 (1 画素が形の 0.04〜0.1) では継ぎ目も板の濃淡も消して、1 枚のなめらかな外板に見せる
                float farFade = 1.0 - smoothstep(0.02, 0.05, max(fw.x, fw.y));
                return float3(1.0 + (h - 0.5) * 0.06 * farFade, max(seam, hatch * 0.7) * farFade, lip * farFade);
            }

            // 外板の凹凸の高さ (形の単位)。板の並びは Panels と同じ。板は縁を面取りして少し盛り上がり、1 枚ずつ傾きが違う。
            // 縁の内側にリベットが並び、半分ほどの板には小さな箱の出っ張りが乗る
            float PanelH(float2 p, float2 size)
            {
                float2 uv0 = p / size;
                float2 uv = float2(uv0.x + Hash12(float2(floor(uv0.y), 3.7)), uv0.y);
                float2 id = floor(uv), f = frac(uv);
                float h1 = Hash12(id), h2 = Hash12(id + 19.1), h3 = Hash12(id + 41.7);
                float2 fe = min(f, 1.0 - f) * size;
                float e = min(fe.x, fe.y);
                float plate = smoothstep(0.0, 0.1, e);
                float hgt = plate * (0.02 + 0.03 * h1 + dot(f - 0.5, float2(h2, h3) - 0.5) * 0.07);
                float along = fe.x < fe.y ? uv.y * size.y : uv.x * size.x;
                float rv = length(float2((frac(along / 0.32) - 0.5) * 0.32, e - 0.09));
                hgt += (1.0 - smoothstep(0.018, 0.032, rv)) * 0.012;
                if (h1 > 0.5 && e > 0.18)
                {
                    float2 g = float2(6.0, 3.0);
                    float2 sf = f * g;
                    float hs = Hash12(floor(sf) + id * 13.1);
                    float2 ce = min(frac(sf), 1.0 - frac(sf)) * size / g;
                    if (hs > 0.45) hgt += smoothstep(0.04, 0.07, min(ce.x, ce.y)) * (0.012 + 0.03 * hs);
                }
                return hgt;
            }

            // 耐熱タイルの凹凸: 目地で下がり、1 枚ずつ少し浮き沈みと傾きがある
            float TileH(float2 p)
            {
                float2 uv = p / 0.62;
                float2 id = floor(uv), f = frac(uv);
                float h1 = Hash12(id + 7.0), h2 = Hash12(id + 23.3), h3 = Hash12(id + 51.9);
                float2 fe = min(f, 1.0 - f) * 0.62;
                float plate = smoothstep(0.0, 0.05, min(fe.x, fe.y));
                return plate * (0.012 + 0.014 * h1 + dot(f - 0.5, float2(h2, h3) - 0.5) * 0.03);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.n.xyz);
                float3 l = normalize(_MrpFltLight.xyz);
                float3 vdir = normalize(_WorldSpaceCameraPos - i.wp);
                float mat = i.n.w;
                float3 base = i.color.rgb;
                float3 lp = i.lp;
                // 模様を貼る向き: 上か下を向いた面は真上から、横を向いた面は船の周りを巻く円筒で
                // (前後と左右の軸で貼り分けると、丸い角で向きが何度も切り替わって格子が乱れる)。継ぎ目は切り替わる所で途切れる
                bool topProj = abs(i.ln.y) >= 0.55 * length(i.ln);
                float2 uvp = topProj ? lp.xz : float2(atan2(lp.x, lp.z) * 14.0, lp.y);
                // 模様の平面の 2 本の向き (形の座標・凹凸の法線を作る)。円筒の横の向きは半径で長さを形の単位へ戻す
                float rr = max(length(lp.xz), 1.0);
                float3 tu = topProj ? float3(1, 0, 0) : float3(lp.z, 0, -lp.x) / rr * (14.0 / rr);
                float3 tv = topProj ? float3(0, 0, 1) : float3(0, 1, 0);
                // 凹凸は寄った時だけ (1 画素が形の 0.05 より大きくなると消える = 遠目はつるつる)
                float2 fwp = fwidth(uvp);
                float near = 1.0 - smoothstep(0.015, 0.05, max(fwp.x, fwp.y));
                float3 ns = n;   // 照りに使う法線 (しわは照りにだけ強く効かせる)
                float3 n0 = n;   // 凹凸を足す前の法線 (縁の照り返しと浅い角度の映り込み。凹凸の急な斜面に線が出ないように)
                float specAmt = 0.0, specLo = 0.955, specHi = 0.975, metal = 0.0, seamDark = 0.0, gloss = 0.0;

                if (mat < 0.5) { }
                else if (mat < 1.5)
                {
                    // 外板: 板の明るさ・継ぎ目・ごく低い周波数のくすみ
                    float3 pn = Panels(uvp, float2(3.4, 2.1));
                    float grime = Noise3(lp * 0.18).x;
                    base *= pn.x * (0.95 + grime * 0.1) * (1.0 + pn.z * 0.12);
                    seamDark = pn.y * 0.2;
                    if (near > 0.0)
                    {
                        const float eps = 0.012;
                        float h0 = PanelH(uvp, float2(3.4, 2.1));
                        float2 gr = float2(PanelH(uvp + float2(eps, 0), float2(3.4, 2.1)) - h0, PanelH(uvp + float2(0, eps), float2(3.4, 2.1)) - h0) / eps;
                        float3 dn = mul((float3x3)unity_ObjectToWorld, -(gr.x * tu + gr.y * tv)) * near;
                        n = normalize(n + dn);
                        ns = n;
                    }
                    gloss = 1.0;
                }
                else if (mat < 2.5)
                {
                    // 細かい塗装 (帯・小さな部品): くすみと細い照りだけ
                    float grime = Noise3(lp * 0.25).x;
                    base *= 0.96 + grime * 0.08;
                    specAmt = 0.12; specLo = 0.97; specHi = 0.985;
                }
                else if (mat < 3.5)
                {
                    // 耐熱タイル: 小さな四角・タイルごとの濃淡・目地
                    float2 uv = uvp / 0.62;
                    float h = Hash12(floor(uv) + 7.0);
                    base *= 0.86 + h * 0.26 - step(0.94, h) * 0.18;
                    float2 tfw = fwidth(uv);
                    float tFar = 1.0 - smoothstep(0.06, 0.16, max(tfw.x, tfw.y));
                    seamDark = GridLine(uv, float2(0.035, 0.035)) * 0.45 * tFar;
                    if (near > 0.0)
                    {
                        const float eps = 0.01;
                        float h0 = TileH(uvp);
                        float2 gr = float2(TileH(uvp + float2(eps, 0)) - h0, TileH(uvp + float2(0, eps)) - h0) / eps;
                        n = normalize(n + mul((float3x3)unity_ObjectToWorld, -(gr.x * tu + gr.y * tv)) * near);
                        ns = n;
                    }
                }
                else if (mat < 4.5)
                {
                        // 放熱板: 前後に走る細い筋
                    float rib = GridLine(float2(uvp.x / 0.32, 0.5), float2(0.09, 0.0));
                    base *= 1.0 - rib * 0.1;
                    specAmt = 0.25; specLo = 0.9; specHi = 0.95;
                    metal = 0.25;
                }
                else if (mat < 5.5)
                {
                    // 金色の断熱材: しわで法線を揺らし、細かい照りを散らす (遠くでは揺れを弱めてちらつかせない)
                    float3 q = lp * 5.5;
                    float fade = 1.0 - smoothstep(0.15, 0.45, length(fwidth(q)));
                    float3 wr = (Noise3(q) - 0.5) * 2.0 + (Noise3(q * 2.7 + 11.0) - 0.5) * 1.1;
                    float3 ww = mul((float3x3)unity_ObjectToWorld, wr) * fade;
                    ns = normalize(n + ww * 0.6);
                    n = normalize(n + ww * 0.15);
                    base *= 0.92 + wr.x * 0.08 * fade;
                    specAmt = 0.55; specLo = 0.86; specHi = 0.93;
                    metal = 0.5;
                }
                else if (mat < 6.5)
                {
                    // 太陽電池: 小さな升 (銀の線) と 4 升ごとの太い区切り
                    float2 uv = uvp / 0.42;
                    float cell = GridLine(uv, float2(0.04, 0.04));
                    float panel = GridLine(uv * 0.25, float2(0.02, 0.02));
                    base = lerp(base, float3(0.62, 0.66, 0.74), cell * 0.55);
                    base = lerp(base, float3(0.75, 0.62, 0.32), panel * 0.8);
                    specAmt = 0.45; specLo = 0.93; specHi = 0.97;
                    metal = 0.35;
                }
                else if (mat < 7.5)
                {
                    // 金属: 細い照り・映り込み
                    specAmt = 0.5; specLo = 0.965; specHi = 0.985;
                    metal = 0.8;
                }

                float ndl = dot(n, l);
                // 3 段: 明・中・暗 (段の境は少しだけぼかす)
                float lit = smoothstep(0.38, 0.5, ndl);
                float mid = smoothstep(-0.16, -0.04, ndl);
                float3 shade = base * float3(0.42, 0.45, 0.6);
                float3 c = lerp(shade, base * 0.8, mid);
                c = lerp(c, base * 1.06, lit);
                c *= 1.0 - seamDark;
                // 縁の照り返し (光の反対側の縁を空の色で)
                float ndv = saturate(dot(n0, vdir));
                float rim = pow(1.0 - ndv, 3.0);
                c += _MrpFltRim.rgb * _MrpFltRim.a * rim * (1.0 - lit * 0.6) * (1.0 + metal);
                // 金属の映り込み: 光の側を映す向きは明るく、反対は暗く (段に分けて絵の様式に合わせる)
                float3 r = reflect(-vdir, ns);
                float rl = dot(r, l);
                float env = smoothstep(-0.1, 0.1, rl) * 0.6 + smoothstep(0.6, 0.7, rl) * 0.4;
                c = lerp(c, c * (0.55 + env * 0.9), metal * 0.6);
                // 照り (金属・断熱材・電池は強く、広い塗装面は弱く細く)
                float3 h = normalize(l + vdir);
                c += smoothstep(specLo, specHi, dot(ns, h)) * lit * specAmt;
                // 塗装の光沢 (外板): 段に切らずになめらかに変わる広い照りと、浅い角度での映り込み。
                // 段で切った照りは広い面で斑に見えるので使わない。寄ると凹凸の法線で照りが板ごとに割れる
                if (gloss > 0.0)
                {
                    float nh = saturate(dot(ns, h));
                    float sheen = pow(nh, 40.0) * 0.35 + pow(nh, 6.0) * 0.06;
                    c += sheen * saturate(ndl * 2.0 + 0.2) * gloss * float3(1.0, 0.97, 0.92);
                    float fr = pow(1.0 - ndv, 5.0);
                    float sky = smoothstep(-0.2, 0.8, dot(r, l));
                    c += fr * gloss * lerp(_MrpFltRim.rgb * 0.25, float3(0.75, 0.8, 0.9), sky * 0.5) * 0.6;
                }
                // 光る所 (a < 1) は陰を付けず明るく
                float glow = 1.0 - i.color.a;
                c = lerp(c, base * (1.0 + glow * 0.9), saturate(glow * 3.0));
                if (mat > 7.5 && mat < 8.5)
                {
                    // 窓のガラス: 浅い角度ほど空を映し、斜めの照りの帯を流す
                    float fres = 0.12 + 0.6 * pow(1.0 - ndv, 4.0);
                    float sw = frac(dot(lp, float3(0.35, 0.6, 0.25)) * 0.9);
                    float streak = smoothstep(0.84, 0.88, sw) * (1.0 - smoothstep(0.9, 0.96, sw));
                    c = lerp(c, _MrpFltRim.rgb * 0.5 + 0.2, fres);
                    c += streak * 0.22 * (1.0 - smoothstep(0.1, 0.3, length(fwidth(lp))));
                    c *= 0.85 + 0.25 * saturate(i.ln.y * 0.5 + 0.5);
                }
                c = lerp(c, float3(1, 1, 1), _Flash);
                // 奥行きの霞
                float d = distance(_WorldSpaceCameraPos, i.wp);
                float fog = saturate(d / max(_MrpFltSky.a, 1.0));
                c = lerp(c, _MrpFltSky.rgb, fog * fog * 0.7 * (1.0 - glow));
                return fixed4(c, 1);
            }
            ENDCG
        }

        Pass
        {
            Cull Front
            ZWrite On
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float _Outline;
            fixed4 _OutlineColor;
            float4 _MrpFltSky;

            struct appdata { float4 vertex : POSITION; float4 smooth : TEXCOORD1; };
            struct v2f { float4 pos : SV_POSITION; float fog : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                float3 sn = v.smooth.xyz, dummy = sn;
                float wgt = length(sn);
                // 大きさ 0 に縮めた部品 (止まっている噴射の光) に縁取りの点を残さない
                float ps = ApplyPart(v.smooth.w, v.vertex, sn, dummy);
                float4 clip = UnityObjectToClipPos(v.vertex);
                float3 nw = UnityObjectToWorldNormal(sn);
                float2 nc = normalize(mul((float3x3)UNITY_MATRIX_VP, nw).xy + 1e-5);
                // 画面の縦横比を戻して、どの向きにも同じ太さ
                nc.x *= _ScreenParams.y / _ScreenParams.x;
                clip.xy += nc * _Outline * clip.w * 2.0 * saturate(ps) * wgt;
                o.pos = clip;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.fog = saturate(distance(_WorldSpaceCameraPos, wp) / max(_MrpFltSky.a, 1.0));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(lerp(_OutlineColor.rgb, _MrpFltSky.rgb, i.fog * i.fog * 0.7), 1);
            }
            ENDCG
        }
    }
}
