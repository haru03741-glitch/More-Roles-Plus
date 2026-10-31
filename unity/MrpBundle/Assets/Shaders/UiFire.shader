// 立ち合いの HUD の炎 (体力のバーから立つ炎・回避の枠の炎の輪)。板 1 枚に式で炎を描く。
// 値は頂点の色で受け取る (Android の MaterialPropertyBlock に SetVector が無く、色なら SpriteRenderer.color の 1 回で済む):
//   r = 燃えている長さ (バー: 外側の端からの割合 / 輪: 上から時計回りの割合)・g = 熱さ (0〜1・高いほど背が高く白く明るい)
//   b = 形 (0 = バー・1 = 輪)・a = 全体の濃さ
// 炎の形: 上へ流れる 2 段のノイズの片方でもう片方を横へゆがめ、高さで削って舌にする。ノイズは HUD の座標で読むので、
//   板の大きさが違ってもきめが揃う。色は温度の連続した変化 (暗い赤 → 赤 → 橙 → 黄白) で、根元と芯ほど熱い。
// 合成は乗算済みアルファ (One / OneMinusSrcAlpha): 熱い所ほどアルファを下げて光として足す。
Shader "MRP/UiFire"
{
    Properties
    {
        _MainTex ("Sprite", 2D) = "white" {}
        _Noise ("Noise", 2D) = "gray" {}
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

            sampler2D _Noise;

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

            // 温度 (0〜1) → 炎の色
            float3 Ramp(float t)
            {
                float3 c = lerp(float3(0.22, 0.02, 0.0), float3(0.85, 0.12, 0.02), saturate(t * 3.0));
                c = lerp(c, float3(1.0, 0.48, 0.06), saturate(t * 3.0 - 1.0));
                return lerp(c, float3(1.0, 0.92, 0.62), saturate(t * 3.0 - 2.0));
            }

            // 上へ流れる炎のノイズ (0〜1)。下の段で上の段を横へゆがめ、ちぎれて揺らぐ形にする
            float FlameNoise(float2 w, float t)
            {
                float n1 = tex2D(_Noise, float2(w.x * 0.32, w.y * 0.45 - t * 0.5)).r;
                float n2 = tex2D(_Noise, float2(w.x * 0.95 + n1 * 0.55, w.y * 1.15 - t * 1.35)).r;
                float n3 = tex2D(_Noise, float2(w.x * 2.1 - n2 * 0.3, w.y * 2.4 - t * 2.2)).r;
                return n1 * 0.4 + n2 * 0.4 + n3 * 0.2;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float fill = i.color.r, heat = i.color.g, alpha = i.color.a;
                float t = _Time.y;
                float n = FlameNoise(i.world, t);
                float a, temp;
                if (i.color.b < 0.5)
                {
                    // バー: uv.x = 外側の端 (0) から内へ・uv.y = 根元 (0) から上へ。燃えているのは fill まで
                    float x = i.uv.x, y = i.uv.y;
                    // 燃えている端は箱に見えないよう広くぼかし、端へ向かって背も低くする
                    float inside = smoothstep(fill + 0.03, fill - 0.06, x) * smoothstep(0.0, 0.03, x);
                    float h = lerp(0.4, 0.95, heat) * (0.6 + 0.4 * inside);
                    float shape = n * 1.35 - y / h - 0.12;
                    float f = saturate(shape * 2.6) * inside;
                    // 根元のにじみ (バーそのものが熱を持って光る)。普段は弱く、バーの色を透かす
                    float base = exp(-y * 14.0) * inside * (0.1 + 0.6 * heat * heat);
                    temp = saturate(f * (0.5 + 0.65 * heat) + base * 0.6 - y * 0.35);
                    a = saturate(f * (0.7 + 0.3 * heat) + base * 0.5);
                }
                else
                {
                    // 輪: 板の中心から。炎は輪から立ち、上の側ほど背が高い (火は上へ昇る)
                    float2 d = i.uv - 0.5;
                    float r = length(d) * 2.0;
                    float u = frac(atan2(d.x, d.y) / 6.2831853);
                    float lit = fill >= 0.999 ? 1.0 : smoothstep(fill + 0.02, fill - 0.04, u);
                    float ring = 0.62;
                    float ro = r - ring;
                    float lift = saturate(d.y * 2.0 + 0.4);
                    float h = lerp(0.12, 0.42, heat) * (0.55 + 0.75 * lift);
                    float shape = n * 1.3 - max(ro, 0.0) / h - max(-ro, 0.0) * 9.0 - 0.15;
                    float f = saturate(shape * 2.4);
                    float edge = exp(-abs(ro) * 28.0);
                    // まだ溜まっていない側は燠火のように暗く燻るだけ
                    f *= lerp(0.18, 1.0, lit);
                    temp = saturate(f * (0.7 + 0.5 * heat) + edge * 0.5 * lit);
                    a = saturate(f + edge * (0.25 + 0.5 * lit));
                }
                float3 col = Ramp(temp);
                a *= alpha;
                // 乗算済み: 色は a を掛けて足し、熱い所ほど下を透かす (光として重なる)
                return fixed4(col * a * 1.25, a * (0.55 - 0.35 * temp));
            }
            ENDCG
        }
    }
}
