// 3D の艦隊戦の光弾。C++ が 1 発 4 頂点の板 (カメラへ向けた細長い四角) を書く。uv.x = 後ろ 0 → 前 1・uv.y = 幅。
// 色は頂点の色。中心に白い芯・外へ色のにじみ。足し算で重ねる (暗い背景の宇宙なので足し算でもつぶれない)
Shader "MRP/FlightBolt"
{
    Properties { }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float y = abs(i.uv.y * 2.0 - 1.0);
                float x = i.uv.x;
                float along = smoothstep(0.0, 0.35, x) * (1.0 - smoothstep(0.92, 1.0, x) * 0.5);
                float core = saturate(1.0 - y * 2.6);
                float halo = pow(saturate(1.0 - y), 2.0);
                float3 c = i.color.rgb * halo * 0.9 + core * core * 1.1;
                return fixed4(c * along, 1);
            }
            ENDCG
        }
    }
}
