// 火 (床の照り・油の膜・立ち上がる炎) を描くシェーダ。CPU はタイルの絵に升ごとの値だけを書き、見た目はここで決める。
// 絵の 1 画素 = 火の升 1 つ。タイルは自分の升の周りに 1 升の縁 (隣のタイルの値) と、上に炎が立ち上がる分の升を持つ。
// 隣のタイルと二重に描かないよう、照りと油は自分の升の範囲 (_Own) の画素だけ、炎は自分の升から立ち上がる物だけを描く。
//   R = 炎の大きさ・G = 熱 (床の照り)・B = 油の量・A = 開いた升 (0 = 閉じた升・0.376 から上は燃やした量 = 焦げ)
// _Mode = 0 (床): 焦げ (茶の焼け → 黒い炭・炭には灰の斑) の上に、油の膜 (黒い膜に虹色の照り・縁に細い線) と熱の照り (橙の光をにじませる)。
// _Mode = 1 (炎): 燃えている行の上に、燃え方 (横になめらかに読む) の背丈で連続した炎を立てる。
//   上へ流れるノイズで横へゆがめ (上ほど大きく) 先を削って、ちぎれて揺らぐ舌にする。升の格子には揃えない。
//   色は温度の連続した変化 (暗い赤 → 赤橙 → 橙 → 黄 → 白) で、根元ほど熱い。先は煤で暗く透ける。
// 合成は乗算済みアルファ (One / OneMinusSrcAlpha): 熱い所ほどアルファを下げて光として足し、炎同士の重なりが明るくなる。
Shader "MRP/Fire"
{
    Properties
    {
        [PerRendererData] _MainTex ("Fire data", 2D) = "black" {}
        _Noise ("Noise", 2D) = "gray" {}
        _ShadowOnly ("Only where the view shadow is (copy in front of the shadow)", Float) = 0
        _ShadowGain ("Brightness in the shadow", Float) = 1
        _Mode ("Mode (0 floor / 1 flame)", Float) = 0
        _Own ("Own cells (uv min xy, max xy)", Vector) = (0, 0, 1, 1)
        _Rise ("Flame height (uv)", Float) = 0.2
        _CellUV ("One cell (uv xy)", Vector) = (0.05, 0.05, 0, 0)
        _Strip ("Rows per flame strip (uv)", Float) = 0.2
        _Glow ("Glow color", Color) = (1.0, 0.42, 0.08, 0.5)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" "CanUseSpriteAtlas" = "False" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _Noise;
            sampler2D _MrpShadowTex; // 影のカメラの描き先 (視界の所はアルファ 0)
            float _ShadowOnly, _ShadowGain;
            float _MrpShadowOn; // 影の板が出ている間だけ 1
            float _Mode, _Rise, _Strip;
            float4 _Own, _CellUV;
            fixed4 _Glow;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 world : TEXCOORD1; fixed4 color : COLOR; float4 screen : TEXCOORD2; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.world = mul(unity_ObjectToWorld, v.vertex).xy;
                o.color = v.color;
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            float InOwn(float2 uv)
            {
                return step(_Own.x, uv.x) * step(uv.x, _Own.z) * step(_Own.y, uv.y) * step(uv.y, _Own.w);
            }

            fixed4 Floor(v2f i)
            {
                if (InOwn(i.uv) < 0.5) return 0;
                float4 d = tex2D(_MainTex, i.uv);
                float t = _Time.y;
                fixed4 c = 0;
                // 油と照りは升目を見せない: 斜め 4 点を足してぼかす (縁の 1 升の中に収まる距離)
                float2 o = _CellUV.xy * 0.7;
                float4 blur = d * 0.36 + (tex2D(_MainTex, i.uv + o) + tex2D(_MainTex, i.uv - o)
                    + tex2D(_MainTex, i.uv + float2(o.x, -o.y)) + tex2D(_MainTex, i.uv + float2(-o.x, o.y))) * 0.16;

                // 開いた升 (A > 0) と燃やした量 (A の 0.376 から上)
                float open = saturate(blur.a * 5.0 - 0.6);
                float burnt = saturate((blur.a - 0.376) / 0.624) * open;

                // 焦げ: 外から 焼け (薄い茶) → 焦げ (濃い茶) → 炭 (黒) の 3 段。段の境はノイズで不規則にずらしてくっきり切る。
                // 炭は木目のようなむらと、斜めの座標で取った灰の斑 (軸に揃った座標だとノイズの絵の繰り返しが点の格子に見えた)。
                // 縁の幅 (fwidth) とノイズは分岐の外で取る (分岐の境の画素で微分が乱れて縁に筋が出ないように)
                float n1 = tex2D(_Noise, i.world * 0.8).r, n2 = tex2D(_Noise, i.world * 3.7).r;
                float ch = burnt + (n1 - 0.5) * 0.4 + (n2 - 0.5) * 0.15;
                float aa = fwidth(ch) * 1.5 + 1e-4;
                float2 r = float2(i.world.x * 0.8 - i.world.y * 0.6, i.world.x * 0.6 + i.world.y * 0.8);
                float grain = tex2D(_Noise, r * float2(2.2, 6.5)).r;
                float fl = tex2D(_Noise, r * 4.3 + 0.37).r * tex2D(_Noise, r * 1.3 + 0.71).r;
                if (burnt > 0.01)
                {
                    float singe = smoothstep(0.14 - aa, 0.14 + aa, ch);
                    float dark = smoothstep(0.42 - aa, 0.42 + aa, ch);
                    float coal = smoothstep(0.72 - aa, 0.72 + aa, ch);
                    float3 col = float3(0.30, 0.18, 0.08);
                    float a = 0.42 * singe;
                    col = lerp(col, float3(0.11, 0.07, 0.045), dark);
                    a = lerp(a, 0.8, dark);
                    col = lerp(col, float3(0.03, 0.025, 0.024) * (0.75 + 0.5 * grain), coal);
                    a = lerp(a, 0.93, coal);
                    float ash = smoothstep(0.4, 0.48, fl) * coal;
                    col = lerp(col, float3(0.46, 0.44, 0.41), ash * 0.6);
                    c = fixed4(col * a, a);
                }

                // 油: 黒い膜。照りは流れるノイズで虹色にずらし、縁に細い濃い線。縁はノイズで少し波打たせる
                float oil = blur.b + (tex2D(_Noise, i.world * 2.3).r - 0.5) * 0.08 * saturate(blur.b * 8.0);
                float fw = max(fwidth(oil), 1e-5);
                float inside = (oil - 0.08) / fw;
                float body = saturate(inside + 0.5) * open;
                if (body > 0.001)
                {
                    float n = tex2D(_Noise, i.world * 0.6 + t * float2(0.01, 0.006)).r;
                    float m = tex2D(_Noise, i.world * 1.7 - t * float2(0.008, 0.012)).r;
                    float hue = frac(n * 2.3 + m * 0.7);
                    float3 rainbow = saturate(abs(frac(hue + float3(0.0, 0.33, 0.67)) * 6.0 - 3.0) - 1.0);
                    float sheen = smoothstep(0.55, 0.8, m) * 0.35;
                    fixed4 f;
                    f.rgb = lerp(float3(0.07, 0.06, 0.08), rainbow * 0.6 + 0.15, sheen);
                    f.a = 0.78;
                    if (inside < 1.4) { f.rgb = float3(0.03, 0.02, 0.03); f.a = 0.9; }
                    f.a *= body;
                    c = fixed4(f.rgb * f.a + c.rgb * (1.0 - f.a), f.a + c.a * (1.0 - f.a));
                }

                // 熱の照り: 熱い所ほど橙の光が床に乗る (炎のゆらぎに合わせて明滅)
                float heat = blur.g;
                if (heat > 0.01)
                {
                    float flick = 0.85 + 0.15 * tex2D(_Noise, float2(t * 0.9, i.world.x * 0.3)).r;
                    float a = saturate(heat * 1.4) * _Glow.a * flick;
                    // 熱い芯 (燃えている所の床) は赤く焼けた色を濃く
                    float3 g = lerp(_Glow.rgb, float3(1.0, 0.75, 0.35), saturate(heat * 2.0 - 1.0));
                    // 照りは光として足す (床の絵を塗りつぶさず明るくする)。少しだけ覆って暗い床でも色が乗るように
                    c.rgb = c.rgb + g * a;
                    c.a = saturate(c.a + a * 0.35 * (1.0 - c.a));
                }
                return c * i.color.a;
            }

            // 炎の板は横長の帯に分けて置く (帯ごとに奥行きを変えてクルーと正しく前後させる)。
            // 帯の番号は頂点色の緑 (番号 / 16) で受け、その帯の行から立つ炎だけを描く
            float N(float2 p) { return tex2Dlod(_Noise, float4(p, 0, 0)).r; }

            // 温度 (0..1) → 炎の色。黒体の放射のように暗い赤から白へ連続して変わる
            float3 Ramp(float k)
            {
                float3 c = lerp(float3(0.88, 0.24, 0.04), float3(0.96, 0.34, 0.05), smoothstep(0.0, 0.22, k));
                c = lerp(c, float3(1.0, 0.45, 0.07), smoothstep(0.18, 0.45, k));
                c = lerp(c, float3(1.0, 0.76, 0.26), smoothstep(0.40, 0.70, k));
                return lerp(c, float3(1.0, 0.93, 0.66), smoothstep(0.86, 1.0, k));
            }

            fixed4 Flame(v2f i)
            {
                float t = _Time.y;
                float2 cell = i.uv / _CellUV.xy;
                float strip = round(i.color.g * 16.0);
                float row = round((_Own.y + strip * _Strip) / _CellUV.y);
                float up = _Rise / _CellUV.y;
                float y = cell.y - row - 0.5;                  // 行の真ん中から上へ (升)
                if (y < -1.4) return 0;
                float2 w = i.world;
                // 横は世界の升 (タイルの継ぎ目で揃う)。行ごとにノイズをずらして、重なった行の炎が同じ形で塗り重ならないようにする
                float cx = w.x * 4.0;
                // 行の真ん中の世界の高さ (帯の中では一定)。floor で番号にすると境目に乗った行で画素ごとに番号が入れ替わり細い横線になる
                float ro = (w.y - y * 0.25) * 4.0 * 0.618;

                // 上へ流れる 2 つのノイズ: 横のゆがみ (上ほど大きい) と、先を削る細かい揺らぎ
                float n1 = N(float2(cx * 0.14 + ro, y * 0.11 - t * 0.55));
                float n2 = N(float2(cx * 0.33 + ro * 1.7 + 0.37, y * 0.26 - t * 1.25));
                float lift = saturate(y / up);
                // ゆがみは最大でも横の縁 (3 升) に収める (超えると板の端で炎が縦にまっすぐ切れる)
                float warp = ((n1 - 0.5) * 2.2 + (n2 - 0.5) * 0.9) * (0.5 + lift * 1.2);
                float sx = cell.x + warp;
                float2 suv = float2(sx * _CellUV.x, (row + 0.5) * _CellUV.y);
                float f = tex2Dlod(_MainTex, float4(suv, 0, 0)).r * step(_Own.x, suv.x) * step(suv.x, _Own.z);
                if (f <= 0.01) return 0;

                // 背丈は燃え方とゆっくり変わる脈で決める (隣と揃わないよう横の位置でずらす)
                float pulse = N(float2(cx * 0.09 + ro * 0.5 + 0.71, t * 0.35));
                float H = up * (0.35 + 0.65 * f) * (0.7 + 0.6 * pulse);
                // 根元の高さを横の位置ごとに下へずらす (揃うと行ごとに根元が一直線に並んで横縞になる。上へずらすと浮いて見える)
                float yb = y + N(float2(cx * 0.55 + ro * 1.3, t * 0.6 + ro)) * 0.5;
                float r = yb / H;
                if (r > 1.3) return 0;
                float bottom = saturate(1.0 + yb / 0.8);       // 根元の下は板の下の端 (1.5 升下) より上で消し切る

                // 舌: 縦に伸びた塊のノイズが上へ流れ、そこだけ炎が立つ (1 行の 4 割ほど)。ゆがみに沿って曲がる
                float nt = N(float2((cx + warp) * 0.16 + ro * 2.3, y * 0.07 - t * 0.32));
                float tongue = smoothstep(0.44, 0.66, nt + (1.0 - saturate(r)) * 0.06);
                // 体: 舌の中は根元ほど濃く、上へ行くほど細かいノイズで削られてちぎれる
                float n3 = N(float2(cx * 0.6 + ro * 3.1 + 0.13, y * 0.45 - t * 2.1));
                float dens = tongue * (1.0 - r) * 1.45 - (n2 * 0.5 + n3 * 0.5) * (0.2 + 0.7 * saturate(r) + 0.4 * saturate(-yb * 2.0));
                // 燃えている所の縁は燃料の読みのなめらかさで細らせ、四角く切らない
                dens *= bottom * smoothstep(0.05, 0.7, f);
                // 温度: 中にも細かい明暗を入れて平らな塗りにしない。縁は濃い色の線にせず透けて消える
                float k = saturate(dens * 0.8 + 0.08 + (n3 - 0.5) * 0.3);
                float cover = smoothstep(0.04, 0.24, dens);

                fixed4 c;
                // 本体: ほぼ覆って、熱い芯だけ少し光として足す (重なりで白く飛ばさない・明るい床で芯が透けて濁らない)
                // 舌の先だけ暗い赤へ冷える (縁を全部赤くすると重なった舌の縁が網目に見える)
                float3 col = lerp(Ramp(k), float3(0.55, 0.07, 0.02), smoothstep(0.6, 1.1, r) * (1.0 - k));
                c.rgb = col * cover;
                c.a = cover * (0.92 - 0.14 * k);
                // にじみ: 体の周りの薄い赤い光
                float halo = saturate(1.1 - r) * bottom * saturate(f * 2.5) * (1.0 - cover) * (0.04 + 0.12 * tongue);
                c.rgb += float3(0.95, 0.30, 0.06) * halo;
                c.a += halo * 0.15;
                // 煤: 先の上の暗く透けた煙
                float soot = smoothstep(0.55, 1.05, r) * smoothstep(1.3, 1.0, r) * (1.0 - cover) * saturate(f * 2.0) * n1 * 0.15;
                c.a += soot;
                return c * i.color.a;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = _Mode < 0.5 ? Floor(i) : Flame(i);
                // 影の手前の写し: 影の所だけに出す (乗算済みなので全体に掛ける)
                if (_ShadowOnly > 0.5) c *= tex2Dlod(_MrpShadowTex, float4(i.screen.xy / i.screen.w, 0, 0)).a * _ShadowGain * _MrpShadowOn;
                return c;
            }
            ENDCG
        }
    }
}
