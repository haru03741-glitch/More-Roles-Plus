// 3D の艦隊戦の火花・炎・煙・宇宙の塵。C++ が 1 枚 4 頂点の板を書く。
// uv.x < 1.5 = 丸いにじみ (uv 0〜1 の中心からの距離で形)・uv.x >= 2 = 速さの向きに伸びる筋 (uv.x - 2 = 後ろ 0 → 前 1・uv.y = 幅)・
// uv.x < -1.5 = 固い欠片 (岩・ゴミ。uv.x + 4 と uv.y の小数が板の中・uv.y の 2 ごとが形の番号。縁はくっきり・左上から光が当たった濃淡)。
// 色は乗算済み: rgb = 足す光・a = 後ろを隠す量。炎は光りながら少し隠し、黒煙は光らずに隠す (One / OneMinusSrcAlpha)。
// 丸い板はノイズで縁を崩して、きれいな円に見せない
Shader "MRP/FlightFx"
{
    Properties
    {
        _Noise ("Noise", 2D) = "gray" {}
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+1" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _Noise;

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float2 wn : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wn = wp.xz * 0.03 + wp.y * 0.02;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float m;
                if (i.uv.x < -1.5)
                {
                    float k = floor(i.uv.y * 0.5);
                    float2 d = float2(i.uv.x + 4.0, i.uv.y - k * 2.0) * 2.0 - 1.0;
                    float a = atan2(d.y, d.x);
                    // いびつな輪郭 (形ごとに位相を変えた小さな揺らぎを重ねる。揺らぎを大きくすると星や花の記号に見える)。0 は板の切れ端らしく四角寄り
                    float r = k < 0.5 ? max(abs(d.x) * 1.15, abs(d.y) * 1.6)
                                      : length(d) / (0.8 + 0.06 * sin(a * 2.0 + k * 1.7) + 0.045 * sin(a * 3.0 + k * 4.1) + 0.03 * sin(a * 5.0 + k * 2.3));
                    float aa = fwidth(r) * 1.5 + 1e-4;
                    m = 1.0 - smoothstep(0.92 - aa, 0.92 + aa, r);
                    float lit = 0.62 + 0.38 * saturate(dot(d, float2(-0.55, 0.65)) * 0.8 + 0.5);
                    lit *= 1.0 - 0.25 * smoothstep(0.55, 0.92, r);
                    return fixed4(i.color.rgb * lit * m, i.color.a * m);
                }
                if (i.uv.x >= 1.5)
                {
                    float x = i.uv.x - 2.0;
                    float y = abs(i.uv.y * 2.0 - 1.0);
                    m = saturate(1.0 - y) * smoothstep(0.0, 0.5, x);
                    m *= m;
                }
                else
                {
                    float2 d = i.uv * 2.0 - 1.0;
                    float n = tex2D(_Noise, i.uv * 0.6 + i.wn).r;
                    float r = length(d) * (0.85 + n * 0.35);
                    m = saturate(1.0 - r);
                    m = m * m * (3.0 - 2.0 * m);
                }
                return fixed4(i.color.rgb * m, i.color.a * m);
            }
            ENDCG
        }
    }
}
