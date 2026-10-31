// 必殺技の演出で、事前に描いたコマの透明な所 (試合の画面の板と破片) に生の画面を貼る。
// 網の頂点の uv = その点が写す生の画面の位置 (0〜1・左下が 0)。板の暗さと色抜きは溜めの間だけ変わる
Shader "MRP/KatanaPlate"
{
    Properties
    {
        _Dim ("Brightness", Float) = 1
        _Sat ("Saturation", Float) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "IgnoreProjector" = "True" "RenderType" = "Overlay" "PreviewType" = "Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always

        // 名前付き: 板と破片がいくつあっても 1 フレームに 1 回だけ写す
        GrabPass { "_MrpKatanaGrab" }

        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            sampler2D _MrpKatanaGrab;
            float _Dim, _Sat;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                #if UNITY_UV_STARTS_AT_TOP
                if (_ProjectionParams.x >= 0) o.uv.y = 1.0 - o.uv.y;
                #endif
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed3 c = tex2D(_MrpKatanaGrab, i.uv).rgb;
                fixed g = dot(c, fixed3(0.299, 0.587, 0.114));
                return fixed4(lerp(g.xxx, c, _Sat) * _Dim, 1.0);
            }
            ENDCG
        }
    }
}
