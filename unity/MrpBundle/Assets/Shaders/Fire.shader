// 火 (床の照り・油の膜・立ち上がる炎) を描くシェーダ。CPU はタイルの絵に升ごとの値だけを書き、見た目はここで決める。
// 絵の 1 画素 = 火の升 1 つ。タイルは自分の升の周りに 1 升の縁 (隣のタイルの値) と、上に炎が立ち上がる分の升を持つ。
// 隣のタイルと二重に描かないよう、照りと油は自分の升の範囲 (_Own) の画素だけ、炎は自分の升から立ち上がる物だけを描く。
//   R = 炎の大きさ・G = 熱 (床の照り)・B = 油の量・A = 開いた升 (歩ける床)
// _Mode = 0 (床): 油の膜 (黒い膜に虹色の照り・縁に細い線) と熱の照り (橙の光をにじませる)。
// _Mode = 1 (炎): 燃えている所に 2 升幅で 1 本の舌 (下が丸く上で細る涙形) を立て、高さを脈打たせて横へゆらす。
//   舌の列は行ごとにずらし、大小 2 層を重ねて、炎の塊が格子や柵に見えないようにする。
//   色は平塗りの段で重ねる: 暗い赤の縁 → 赤橙 → 橙 → 黄 → 白い芯。
Shader "MRP/Fire"
{
    Properties
    {
        [PerRendererData] _MainTex ("Fire data", 2D) = "black" {}
        _Noise ("Noise", 2D) = "gray" {}
        _Mode ("Mode (0 floor / 1 flame)", Float) = 0
        _Own ("Own cells (uv min xy, max xy)", Vector) = (0, 0, 1, 1)
        _Rise ("Flame height (uv)", Float) = 0.2
        _CellUV ("One cell (uv xy)", Vector) = (0.05, 0.05, 0, 0)
        _Strip ("Rows per flame strip (uv)", Float) = 0.2
        _Glow ("Glow color", Color) = (1.0, 0.42, 0.08, 0.5)
        _Outline ("Flame outline", Color) = (0.42, 0.06, 0.04, 0.95)
        _Red ("Flame red", Color) = (0.88, 0.22, 0.06, 1)
        _Orange ("Flame orange", Color) = (1.0, 0.52, 0.1, 1)
        _Yellow ("Flame yellow", Color) = (1.0, 0.84, 0.25, 1)
        _Core ("Flame core", Color) = (1.0, 0.98, 0.82, 1)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" "CanUseSpriteAtlas" = "False" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _Noise;
            float _Mode, _Rise, _Strip;
            float4 _Own, _CellUV;
            fixed4 _Glow, _Outline, _Red, _Orange, _Yellow, _Core;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 world : TEXCOORD1; fixed4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.world = mul(unity_ObjectToWorld, v.vertex).xy;
                o.color = v.color;
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

                // 油: 黒い膜。照りは流れるノイズで虹色にずらし、縁に細い濃い線
                float oil = d.b;
                float fw = max(fwidth(oil), 1e-5);
                float inside = (oil - 0.08) / fw;
                float body = saturate(inside + 0.5) * smoothstep(0.3, 0.7, d.a);
                if (body > 0.001)
                {
                    float n = tex2D(_Noise, i.world * 0.6 + t * float2(0.01, 0.006)).r;
                    float m = tex2D(_Noise, i.world * 1.7 - t * float2(0.008, 0.012)).r;
                    float hue = frac(n * 2.3 + m * 0.7);
                    float3 rainbow = saturate(abs(frac(hue + float3(0.0, 0.33, 0.67)) * 6.0 - 3.0) - 1.0);
                    float sheen = smoothstep(0.55, 0.8, m) * 0.35;
                    fixed4 o;
                    o.rgb = lerp(float3(0.07, 0.06, 0.08), rainbow * 0.6 + 0.15, sheen);
                    o.a = 0.78;
                    if (inside < 1.4) { o.rgb = float3(0.03, 0.02, 0.03); o.a = 0.9; }
                    o.a *= body;
                    c = o;
                }

                // 熱の照り: 熱い所ほど橙の光が床に乗る (炎のゆらぎに合わせて明滅)
                float heat = d.g;
                if (heat > 0.01)
                {
                    float flick = 0.85 + 0.15 * tex2D(_Noise, float2(t * 0.9, i.world.x * 0.3)).r;
                    float a = saturate(heat * 1.4) * _Glow.a * flick;
                    // 熱い芯 (燃えている所の床) は赤く焼けた色を濃く
                    float3 g = lerp(_Glow.rgb, float3(1.0, 0.75, 0.35), saturate(heat * 2.0 - 1.0));
                    c.rgb = lerp(c.rgb, g, a / max(c.a + a, 1e-4));
                    c.a = saturate(c.a + a * (1.0 - c.a));
                }
                c.a *= i.color.a;
                return c;
            }

            // 炎の板は横長の帯に分けて置く (帯ごとに奥行きを変えてクルーと正しく前後させる)。
            // 帯の番号は頂点色の緑 (番号 / 16) で受け、その帯の行から立つ舌だけを描く
            float InStrip(float2 uv, float strip)
            {
                float y0 = _Own.y + strip * _Strip;
                return step(_Own.x, uv.x) * step(uv.x, _Own.z) * step(y0, uv.y) * step(uv.y, y0 + _Strip);
            }

            float Hash(float n) { return frac(sin(n * 12.9898) * 43758.5453); }

            // 1 層分の舌の強さ (0 = 外)。舌は 2 升幅の列に 1 本ずつ立ち、列の位置は行ごとにずらして格子に見せない。
            // scale = 舌の大きさ・seed = 層ごとのずらし
            float Tongues(float2 cellPos, float scale, float seed, float t, float strip)
            {
                const float W = 2.0;
                float best = 0;
                float up = _Rise / _CellUV.y;
                [unroll]
                for (int j = 0; j <= 5; j++)
                {
                    float row = floor(cellPos.y) - j;
                    float shift = Hash(row * 1.91 + seed) * W;
                    float cx = (cellPos.x + shift) / W;
                    float col = floor(cx);
                    float fx = (frac(cx) - 0.5) * W;          // 列の中心からの横のずれ (升)
                    float2 baseUV = float2(((col + 0.5) * W - shift) * _CellUV.x, (row + 0.5) * _CellUV.y);
                    float f = tex2Dlod(_MainTex, float4(baseUV, 0, 0)).r * InStrip(baseUV, strip);
                    if (f <= 0.02) continue;
                    float ph = Hash(col * 3.1 + row * 7.7 + seed * 13.0);
                    // 舌の高さ = 燃え方 × 舌ごとの背丈 × ゆらぐ脈
                    float pulse = 0.65 + 0.55 * tex2Dlod(_Noise, float4(ph * 0.7 + seed, t * 0.8 + ph, 0, 0)).r;
                    float hgt = up * scale * f * (0.55 + 0.45 * ph) * pulse;
                    float y = cellPos.y - row - 0.5;            // 升の真ん中から上へ (升)
                    float rel = y / max(hgt, 0.3);
                    if (rel >= 1) continue;
                    float r = saturate(rel);
                    float sway = (sin(t * (2.3 + ph * 1.4) + ph * 6.28 - r * 3.0) * 0.32 + sin(t * 5.1 + ph * 3.0 - r * 5.5) * 0.12) * r * W * 0.5;
                    float base = 0.5 * W * scale * (0.65 + 0.35 * f);
                    float hw = base * sqrt(1 - r) * (1 - 0.3 * r);
                    float edge = (tex2Dlod(_Noise, float4(cellPos.x * 0.35 + ph, cellPos.y * 0.3 - t * 1.5, 0, 0)).r - 0.5) * 0.4;
                    float dx = (fx - sway) / max(hw, 1e-3);
                    // 下は丸く閉じる (升の真ん中より下は楕円)
                    float dy = rel < 0 ? -y / (0.45 * base) : 0;
                    float d = sqrt(dx * dx + dy * dy) + edge;
                    if (d >= 1) continue;
                    best = max(best, (1 - d) * (1 - r * 0.5) * (0.7 + 0.3 * f));
                }
                return best;
            }

            fixed4 Flame(v2f i)
            {
                float t = _Time.y;
                float2 cellPos = i.uv / _CellUV.xy;
                float strip = round(i.color.g * 16.0);
                // 大きな舌と小さな舌の 2 層を重ねて、てっぺんを不揃いにする
                float v = Tongues(cellPos, 1.0, 0.0, t, strip);
                v = max(v, Tongues(cellPos, 0.7, 5.3, t + 1.7, strip));
                if (v <= 0.001) return 0;

                fixed4 c;
                if (v > 0.72) c = _Core;
                else if (v > 0.52) c = _Yellow;
                else if (v > 0.32) c = _Orange;
                else if (v > 0.12) c = _Red;
                else c = _Outline;
                float fw = max(fwidth(v), 1e-4);
                c.a *= saturate(v / fw);
                c.a *= i.color.a;
                return c;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _Mode < 0.5 ? Floor(i) : Flame(i);
            }
            ENDCG
        }
    }
}
