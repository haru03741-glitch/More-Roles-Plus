// 必殺技の演出の事前に描いたコマ。色 (_MainTex・不透明度を掛けた後の色) と不透明度 (_AlphaTex の R) を別のテクスチャで持つ。
// 普段は premultiplied で重ねる (One, OneMinusSrcAlpha)。_UseMask = 1 の時は不透明度の代わりに _MaskTex (被害者より手前の物の割合) を使い、
// 被害者の絵の上へ手前の物だけを描き直す (SrcAlpha, OneMinusSrcAlpha)
Shader "MRP/KatanaFrame"
{
    Properties
    {
        _MainTex ("Color", 2D) = "black" {}
        _AlphaTex ("Alpha", 2D) = "white" {}
        _MaskTex ("Front mask", 2D) = "black" {}
        _UseMask ("Use mask", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst", Float) = 10
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "IgnoreProjector" = "True" "RenderType" = "Overlay" "PreviewType" = "Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend [_SrcBlend] [_DstBlend]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            sampler2D _MainTex, _AlphaTex, _MaskTex;
            float _UseMask;

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
                fixed3 c = tex2D(_MainTex, i.uv).rgb;
                fixed a = _UseMask > 0.5 ? tex2D(_MaskTex, i.uv).r : tex2D(_AlphaTex, i.uv).r;
                return fixed4(c, a);
            }
            ENDCG
        }
    }
}
