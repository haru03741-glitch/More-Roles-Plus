// 必殺技の演出で、攻撃者の着せ替え (帽子・バイザー・スキン) を事前に描いたコマに重ねる。
// _MainTex = 着せ替えだけを別のカメラで描いた絵 (透明の黒に重ねたので色は不透明度を掛けた後)・_MaskTex = 見える割合 (刀や拳など手前の物の所は 0)。
// premultiplied で重ねる (One, OneMinusSrcAlpha)
Shader "MRP/KatanaAttire"
{
    Properties
    {
        _MainTex ("Attire", 2D) = "black" {}
        _MaskTex ("Visible", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "IgnoreProjector" = "True" "RenderType" = "Overlay" "PreviewType" = "Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            sampler2D _MainTex, _MaskTex;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv) * tex2D(_MaskTex, i.uv).r;
            }
            ENDCG
        }
    }
}
