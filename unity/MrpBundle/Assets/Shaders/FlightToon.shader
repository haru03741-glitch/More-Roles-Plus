// 3D の艦隊戦の船。頂点の色 (a < 1 = 光る強さ) を 3 段の明暗で塗り、太い縁取りを付ける (本編の物と同じ「太い縁 + 段の陰」の様式)。
// Android の削られた Unity では Light の設定が使えないので、光の向きと色は global の値 (_MrpFltLight・_MrpFltSky) で受ける。
// 縁取りは面を裏返して外へ押し出す 2 つ目の描き方。押し出す向きは頂点の TEXCOORD1 (同じ位置の面の法線を足したなめらかな法線) で、
// 画面の上で同じ太さになるよう奥行き (w) を掛ける。遠いほど空の色へ寄せて奥行きを出す。
// 動く部品 (翼・舵・噴射口など): 頂点の TEXCOORD1.w が部品の番号 (0 = 船体)。部品の姿は global の配列 _MrpFltParts に
// 部品ごとに 2 つ (四元数・ずれ xyz と大きさ w) で入り、船ごとの始まりは _PartBase (-1 = 動かさない)。
// 頂点は 8 ビットに詰めてある: NORMAL = 陰の法線 xyz + 材質の番号 w (番号 / 127)・TEXCOORD1 = 縁取りを押し出す向き xyz
// (長さ = 太さの割合。細い部品の縁取りを細くする) + 部品の番号 w (番号 / 127)。
// 材質の番号: 0 = 無地 / 1 = 外板 (継ぎ目と点検口) / 2 = 細かい塗装 / 3 = 耐熱タイル / 4 = 放熱板 /
// 5 = 金色の断熱材 (しわの照り) / 6 = 太陽電池 / 7 = 金属 / 8 = 窓のガラス / 9 = 光る物 /
// 10 = 装甲帯 (縦のヘアラインの厚い板・継ぎ目を押さえる帯金とボルト・横に長く伸びる照り)。
// 番号に 16 を足した面は、投影でなく頂点の TEXCOORD2 (模様の座標 × 1/128) で模様を描く (船体の丸い肩: 縁に沿って巻く座標)。
// その座標は一周 U_PERIOD で戻るので、板やタイルの番号は一周の個数で折り返して、戻った所で模様をつなげる。
// 模様は部品が動く前の位置 (形の座標) で描くので、動く部品に貼り付いたまま動く。
// 細かい物 (線・板の濃淡・凹凸) は画面の上で 1 画素より細かくなる距離で、消すのでなく平均の見え方へ寄せる
// (線は面積の割合の暗さ・板の濃淡は 1・凹凸は照りの広がり)。遠目でも表面の粗さは同じで、見分けられなくなるだけにする。
// ギザギザ消しが無いので、細かい物をそのまま描くとちらつく。
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

            #define UV_FLAG 16.0
            #define U_PERIOD 105.4
            #define PANEL_W 3.4
            #define PANEL_H 2.1
            #define TILE 0.62
            #define BELT_L 1.7     // 装甲帯の板の長さ (外板の板の半分・一周 62 枚)
            #define BELT_C 2.4     // 装甲帯の段の高さ (船体の帯の壁では、段の継ぎ目が窓のすぐ下とすぐ上に来る)
            #define STRAP 0.13     // 段の継ぎ目を押さえる帯金の幅の半分
            #define BOLT 0.425     // 帯金のボルトの間隔 (一周 248 本)

            struct appdata { float4 vertex : POSITION; float4 normal : NORMAL; fixed4 color : COLOR; float4 smooth : TEXCOORD1; float2 uv : TEXCOORD2; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 n : TEXCOORD0;      // xyz = 世界の法線・w = 材質の番号
                float3 wp : TEXCOORD1;
                float3 lp : TEXCOORD2;     // 形の座標 (部品が動く前)
                float3 ln : TEXCOORD3;     // 形の座標の法線
                float2 uv : TEXCOORD4;     // 書き出した模様の座標 (形の単位)
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
                o.uv = v.uv * 128.0;
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

            // 一周 U_PERIOD の座標で、横の番号を一周の個数 n で折り返す (戻った所で同じ番号になる)
            float2 WrapId(float2 id, float n) { return float2(id.x - n * floor(id.x / n), id.y); }

            // 升目の線 (uv の整数の所に幅 w の線)。fw = uv の 1 画素あたりの変化 (ずらした座標は飛ぶので、ずらす前の座標で測って渡す)。
            // 線が 1 画素より細くなると、線が占める面積の割合 (平均の濃さ) へ寄せる
            float GridLineFw(float2 uv, float2 w, float2 fw)
            {
                float2 d = abs(frac(uv + 0.5) - 0.5);
                float2 on = step(1e-4, w);
                float2 m = (1.0 - smoothstep(w, w + fw * 1.5, d)) * on;
                float2 ww = max(w, 1e-4);
                m = lerp(m, min(2.0 * w, 1.0) * on, smoothstep(ww, ww * 4.0, fw));
                return 1.0 - (1.0 - m.x) * (1.0 - m.y);
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
                float h = Hash12(WrapId(floor(uv), U_PERIOD / PANEL_W));
                float2 w = float2(0.012, 0.02);
                float seam = GridLineFw(uv, w, fw);
                float fade = 1.0 - smoothstep(0.12, 0.3, max(fw.x, fw.y));
                // 横の継ぎ目の下側に細い光る縁 (細くなったら面積の割合へ)
                float dy = frac(uv.y + 0.5) - 0.5;
                float lip = 1.0 - smoothstep(w.y * 1.6, w.y * 1.6 + fw.y * 1.5, abs(dy - w.y * 2.4));
                lip = lerp(lip, w.y * 3.2, smoothstep(w.y * 1.6, w.y * 6.4, fw.y));
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
                // 板の濃淡は、板が数画素より小さくなってから平均 (1) へ
                float plateFade = 1.0 - smoothstep(0.15, 0.4, max(fw.x, fw.y));
                return float3(1.0 + (h - 0.5) * 0.06 * plateFade, max(seam, hatch * 0.7), lip);
            }

            // 外板の凹凸の高さ (形の単位)。板の並びは Panels と同じ。板は縁を面取りして少し盛り上がり、1 枚ずつ傾きが違う。
            // 縁の内側にリベットが並び、半分ほどの板には小さな箱の出っ張りが乗る
            float PanelH(float2 p, float2 size)
            {
                float2 uv0 = p / size;
                float2 uv = float2(uv0.x + Hash12(float2(floor(uv0.y), 3.7)), uv0.y);
                float2 id = WrapId(floor(uv), U_PERIOD / PANEL_W), f = frac(uv);
                float h1 = Hash12(id), h2 = Hash12(id + 19.1), h3 = Hash12(id + 41.7);
                float2 fe = min(f, 1.0 - f) * size;
                float e = min(fe.x, fe.y);
                float plate = smoothstep(0.0, 0.1, e);
                float hgt = plate * (0.02 + 0.03 * h1 + dot(f - 0.5, float2(h2, h3) - 0.5) * 0.07);
                float along = fe.x < fe.y ? uv.y * size.y : uv.x * size.x;
                const float rs = PANEL_W / 11.0;   // リベットの間隔 (板の幅も一周も割り切る)
                float rv = length(float2((frac(along / rs) - 0.5) * rs, e - 0.09));
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
                float2 uv = p / TILE;
                float2 id = WrapId(floor(uv), U_PERIOD / TILE), f = frac(uv);
                float h1 = Hash12(id + 7.0), h2 = Hash12(id + 23.3), h3 = Hash12(id + 51.9);
                float2 fe = min(f, 1.0 - f) * TILE;
                float plate = smoothstep(0.0, 0.05, min(fe.x, fe.y));
                return plate * (0.012 + 0.014 * h1 + dot(f - 0.5, float2(h2, h3) - 0.5) * 0.03);
            }

            // 装甲帯の板: 段ごとに半枚ずらす。xy = 板の番号 (一周で折り返し済み)・zw = 板の中の位置 (0..1)・
            // 段の継ぎ目からの縦の距離は BeltSeam で
            float4 BeltCell(float2 p)
            {
                float2 uv = p / float2(BELT_L, BELT_C);
                uv.x += frac(floor(uv.y) * 0.5);
                return float4(WrapId(floor(uv), U_PERIOD / BELT_L), frac(uv));
            }

            float BeltSeam(float y) { return (frac(y / BELT_C + 0.5) - 0.5) * BELT_C; }

            // 装甲帯の凹凸の高さ (形の単位): 板は縁を面取りして浮き、1 枚ずつ少し傾く。
            // 段の継ぎ目の上に厚い帯金がかぶさり、その上下の縁寄りにボルトが互い違いに 2 列並ぶ
            float ArmorH(float2 p)
            {
                float4 c = BeltCell(p);
                float2 fe = min(c.zw, 1.0 - c.zw) * float2(BELT_L, BELT_C);
                float h1 = Hash12(c.xy + 3.1), h2 = Hash12(c.xy + 17.7);
                float hgt = smoothstep(0.0, 0.05, min(fe.x, fe.y)) * (0.025 + 0.01 * h1 + (c.z - 0.5) * (h2 - 0.5) * 0.03);
                float dv = BeltSeam(p.y);
                float se = STRAP - abs(dv);
                if (se > -0.01)
                {
                    float strap = 0.05 + smoothstep(-0.01, 0.025, se) * 0.03;
                    float bx = (frac(p.x / BOLT + (dv > 0.0 ? 0.5 : 0.0)) - 0.5) * BOLT;
                    float rb = length(float2(bx, abs(dv) - 0.07)) / 0.03;
                    strap += sqrt(saturate(1.0 - rb * rb)) * 0.016;
                    hgt = se > 0.0 ? strap : max(hgt, strap * saturate(1.0 + se / 0.01));
                }
                return hgt;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.n.xyz);
                float3 l = normalize(_MrpFltLight.xyz);
                float3 vdir = normalize(_WorldSpaceCameraPos - i.wp);
                float mat = i.n.w;
                bool baked = mat > UV_FLAG - 0.5;
                if (baked) mat -= UV_FLAG;
                float3 base = i.color.rgb;
                float3 lp = i.lp;
                // 模様を貼る向き: 上か下を向いた面は真上から、横を向いた面は船の周りを巻く円筒で
                // (前後と左右の軸で貼り分けると、丸い角で向きが何度も切り替わって格子が乱れる)。継ぎ目は切り替わる所で途切れる
                // 船体の丸い肩は書き出した座標 (縁に沿って巻く) で貼る
                bool topProj = abs(i.ln.y) >= 0.55 * length(i.ln);
                float2 uvp = baked ? i.uv : (topProj ? lp.xz : float2(atan2(lp.x, lp.z) * 14.0, lp.y));
                // 模様の座標の 2 本の向き (形の座標の面の上での u と v の勾配・凹凸の法線を作る)。
                // 画面の上の微分から出すので、どの貼り方でも面に乗った向きになる (2x2 画素ごとに一定)
                float3 dp1 = ddx(lp), dp2 = ddy(lp);
                float2 du1 = ddx(uvp), du2 = ddy(uvp);
                float3 nl = normalize(i.ln);
                float3 c2 = cross(dp2, nl), c1 = cross(nl, dp1);
                float det = dot(dp1, c2);
                float3 tu = 0, tv = 0;
                if (abs(det) > 1e-12)
                {
                    tu = (c2 * du1.x + c1 * du2.x) / det;
                    tv = (c2 * du1.y + c1 * du2.y) / det;
                }
                // 凹凸は寄った時だけ (1 画素が形の 0.05 より大きくなると消える = 遠目はつるつる)
                float2 fwp = fwidth(uvp);
                float near = 1.0 - smoothstep(0.015, 0.05, max(fwp.x, fwp.y));
                // 板ごとの傾きは板が数画素になるまで残す (1 画素が形の 0.2〜0.6)
                float tiltAmt = 1.0 - smoothstep(0.2, 0.6, max(fwp.x, fwp.y));
                // 見分けられなくなった凹凸の分だけ表面を粗く扱う (0 = 凹凸を描いている・1 = 平均だけ)
                float rough = 1.0 - near;
                float3 ns = n;   // 照りに使う法線 (しわは照りにだけ強く効かせる)
                float3 n0 = n;   // 凹凸を足す前の法線 (縁の照り返しと浅い角度の映り込み。凹凸の急な斜面に線が出ないように)
                float specAmt = 0.0, specLo = 0.955, specHi = 0.975, metal = 0.0, seamDark = 0.0, gloss = 0.0;
                float aniso = 0.0;   // 縦のヘアラインの照り (横に長く伸びる) の強さ

                if (mat < 0.5) { }
                else if (mat < 1.5)
                {
                    // 外板: 板の明るさ・継ぎ目・ごく低い周波数のくすみ
                    float3 pn = Panels(uvp, float2(PANEL_W, PANEL_H));
                    float grime = Noise3(lp * 0.18).x;
                    base *= pn.x * (0.95 + grime * 0.1) * (1.0 + pn.z * 0.12);
                    seamDark = pn.y * 0.2;
                    if (tiltAmt > 0.0)
                    {
                        // 板の傾き (板の中で一定なので高さの関数を呼ばずに出す・PanelH の傾きの項と同じ)
                        float2 uv0 = uvp / float2(PANEL_W, PANEL_H);
                        float2 pid = WrapId(floor(float2(uv0.x + Hash12(float2(floor(uv0.y), 3.7)), uv0.y)), U_PERIOD / PANEL_W);
                        float2 gr = (float2(Hash12(pid + 19.1), Hash12(pid + 41.7)) - 0.5) * 0.07 / float2(PANEL_W, PANEL_H);
                        if (near > 0.0)
                        {
                            const float eps = 0.012;
                            float h0 = PanelH(uvp, float2(PANEL_W, PANEL_H));
                            float2 grF = float2(PanelH(uvp + float2(eps, 0), float2(PANEL_W, PANEL_H)) - h0, PanelH(uvp + float2(0, eps), float2(PANEL_W, PANEL_H)) - h0) / eps;
                            gr = lerp(gr, grF, near);
                        }
                        float3 dn = mul((float3x3)unity_ObjectToWorld, -(gr.x * tu + gr.y * tv) * tiltAmt);
                        n = normalize(n + dn);
                        // 塗装の細かいざらつき (寄った時だけ見える)。照りにだけ効かせる (陰の段を揺らすと斑の迷彩になる)
                        float3 grain = (Noise3(lp * 9.0) - 0.5) * 0.12 * near;
                        ns = normalize(n + mul((float3x3)unity_ObjectToWorld, grain));
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
                    float2 uv = uvp / TILE;
                    float h = Hash12(WrapId(floor(uv), U_PERIOD / TILE) + 7.0);
                    float2 tfw = fwidth(uv);
                    // 1 枚ずつの濃淡はタイルが数画素より小さくなると平均 (0.98) へ・目地は線の面積の割合の暗さへ
                    float tFar = 1.0 - smoothstep(0.15, 0.4, max(tfw.x, tfw.y));
                    base *= lerp(0.98, 0.86 + h * 0.26 - step(0.94, h) * 0.18, tFar);
                    seamDark = GridLine(uv, float2(0.035, 0.035)) * 0.45;
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
                    float rib = GridLine(float2(uvp.x / (U_PERIOD / 330.0), 0.5), float2(0.09, 0.0));
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
                else if (mat > 9.5 && mat < 10.5)
                {
                    // 装甲帯: 段ごとにずらした厚い板 (縦の継ぎ目)・段の継ぎ目にかぶさる帯金 (縁は塗装が擦れて地の金属が出る)・
                    // 帯金のボルト・縦のヘアライン。線と濃淡は 1 画素より細くなると面積の割合へ寄せる
                    float2 uv0 = uvp / float2(BELT_L, BELT_C);
                    float2 bfw = max(fwidth(uv0), 1e-5);   // ずらす前の座標で測る (段の境で飛ばない)
                    float4 cell = BeltCell(uvp);
                    float h = Hash12(cell.xy + 3.1);
                    float plateFar = 1.0 - smoothstep(0.15, 0.4, max(bfw.x, bfw.y));
                    base *= 1.0 + (h - 0.5) * 0.08 * plateFar;
                    float dv = BeltSeam(uvp.y);
                    float fwv = max(fwp.y, 1e-5);
                    // 帯金の面 (少し明るい・金属寄り)
                    float onStrap = 1.0 - smoothstep(STRAP - fwv * 0.75, STRAP + fwv * 0.75, abs(dv));
                    onStrap = lerp(onStrap, saturate(STRAP * 2.0 / BELT_C), smoothstep(STRAP, STRAP * 4.0, fwv));
                    // 板の縦の継ぎ目 (帯金の下には出ない)
                    float joint = GridLineFw(float2(uv0.x + frac(floor(uv0.y) * 0.5), 0.5), float2(0.006, 0.0), bfw) * (1.0 - onStrap);
                    // 帯金の縁の擦れ (細い明るい線・遠くでは面積の割合)
                    const float wl = 0.014;
                    float edge = 1.0 - smoothstep(wl, wl + fwv * 1.5, abs(abs(dv) - STRAP + wl));
                    edge = lerp(edge, wl * 2.0 * 2.0 / BELT_C, smoothstep(wl, wl * 4.0, fwv));
                    // ボルトの頭 (鋼の灰・寄った時だけ)
                    float bx = (frac(uvp.x / BOLT + (dv > 0.0 ? 0.5 : 0.0)) - 0.5) * BOLT;
                    float bolt = (1.0 - smoothstep(0.024, 0.024 + max(fwp.x, fwv) * 1.5, length(float2(bx, abs(dv) - 0.07)))) * onStrap;
                    bolt *= 1.0 - smoothstep(0.02, 0.06, max(fwp.x, fwv));
                    // 縦のヘアライン (1 本ずつ明るさが違う・線が 1 画素より細くなると平均へ)
                    float hx = uvp.x * 70.0;
                    float hair = (Hash12(float2(floor(hx), floor(uvp.y / BELT_C) + 5.3)) - 0.5) * (1.0 - smoothstep(0.3, 0.9, fwidth(hx)));
                    base *= (1.0 + hair * 0.07) * (1.0 + onStrap * 0.1);
                    base = lerp(base, float3(0.55, 0.6, 0.7), edge * 0.55 + bolt * 0.6);
                    seamDark = joint * 0.5;
                    if (tiltAmt > 0.0)
                    {
                        // 板の傾き (ArmorH の傾きの項と同じ) と、寄った時は面取り・帯金・ボルトの凹凸
                        float2 gr = float2((Hash12(cell.xy + 17.7) - 0.5) * 0.03 / BELT_L, 0.0) * (1.0 - onStrap);
                        if (near > 0.0)
                        {
                            const float eps = 0.008;
                            float h0 = ArmorH(uvp);
                            float2 grF = float2(ArmorH(uvp + float2(eps, 0)) - h0, ArmorH(uvp + float2(0, eps)) - h0) / eps;
                            gr = lerp(gr, grF, near);
                        }
                        n = normalize(n + mul((float3x3)unity_ObjectToWorld, -(gr.x * tu + gr.y * tv) * tiltAmt));
                        ns = n;
                    }
                    metal = 0.3 + onStrap * 0.2;
                    aniso = 1.0 - bolt;
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
                    // 寄った時は凹凸とざらつきの法線が照りを割る。遠くでそれが見分けられなくなった分は、
                    // 照りの山を低く広げて同じ量の光を返す (粗い面を遠くから見た平均の照り)
                    float nh = saturate(dot(ns, h));
                    float sp = lerp(32.0, 12.0, rough);
                    float sheen = pow(nh, sp) * 0.3 * (sp + 2.0) / 34.0 + pow(nh, 6.0) * 0.06;
                    c += sheen * saturate(ndl * 2.0 + 0.2) * gloss * float3(1.0, 0.97, 0.92);
                    // 浅い角度の映り込みは粗い面なのでぼやけて弱い
                    float fr = pow(1.0 - ndv, 5.0);
                    float sky = smoothstep(-0.5, 1.0, dot(r, l));
                    c += fr * gloss * lerp(_MrpFltRim.rgb * 0.25, float3(0.75, 0.8, 0.9), sky * 0.5) * 0.35;
                }
                // 縦のヘアラインの照り: 溝と直角 (横) へは広く、溝に沿って (縦) は狭く散るので、横に長い光の帯になる。
                // 帯は光と目の向きで決まり、船が回ると帯の上を滑る。遠くでも面の性質なので消えない
                if (aniso > 0.0)
                {
                    float3 tw = mul((float3x3)unity_ObjectToWorld, tv);
                    tw -= ns * dot(tw, ns);
                    if (dot(tw, tw) > 1e-12)
                    {
                        tw = normalize(tw);
                        float3 bw = cross(ns, tw);
                        float ht = dot(h, tw), hb = dot(h, bw), hn = saturate(dot(h, ns));
                        float ex = (ht * ht * 500.0 + hb * hb * 10.0) / max(1.0 - hn * hn, 1e-4);
                        float glint = pow(hn, min(ex, 2000.0)) * 0.45 + pow(hn, min(ex * 0.15, 400.0)) * 0.08;
                        c += glint * aniso * saturate(ndl * 2.0 + 0.2) * float3(0.82, 0.88, 1.0);
                    }
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
