// 3D の艦隊戦の火花・炎・煙・宇宙の塵。C++ が 1 枚 4 頂点の板を書く。
// uv.x < 1.5 = 丸いにじみ (uv 0〜1 の中心からの距離で形)・uv.x >= 2 = 速さの向きに伸びる筋 (uv.x - 2 = 後ろ 0 → 前 1・uv.y = 幅)。
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
