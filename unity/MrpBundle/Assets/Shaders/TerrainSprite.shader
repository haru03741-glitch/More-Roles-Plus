// 船の部屋の絵 (SpriteRenderer) を描くシェーダ。描き方はゲームの Unlit/MaskShader と同じ
// (通常の半透明合成・深度書き込みあり・ステンシルへ _MaskLayer を書く → 影の板 Unlit/ShadowShader はその上にだけ影を落とす)。
// 加えて世界全体に敷いた「損傷マスク」(_MrpDamageTex) を世界座標で引き、壊れた所を描かずに抜き、焦げを乗せる。
//   マスクの R = 穴 (0..1 のなだらかな値。ノイズを足した閾値で切るので縁がぎざぎざになる)
//   マスクの G = 焦げの濃さ
// マスクの置き場所は _MrpDamageRect (xy = 世界座標の左下、zw = 1 / 幅と高さ) で全マテリアル共通。
Shader "MRP/TerrainSprite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _MaskLayer ("Stencil Ref", Float) = 1
        _MaskComp ("Stencil Comp", Float) = 8
        _Noise ("Edge Noise", 2D) = "gray" {}
        _EdgeJag ("Edge jaggedness", Float) = 0.35
        _ScorchColor ("Scorch color", Color) = (0.08, 0.06, 0.05, 1)
        _EmberColor ("Ember rim color", Color) = (1.0, 0.45, 0.12, 1)
        _UseDamage ("Use damage mask", Float) = 1
        [HideInInspector] _AlphaTex ("External Alpha", 2D) = "white" {}
        [HideInInspector] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" "CanUseSpriteAtlas" = "True" }

        Stencil
        {
            Ref [_MaskLayer]
            Comp [_MaskComp]
            Pass Replace
        }

        Cull Off
        Lighting Off
        ZWrite On
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ ETC1_EXTERNAL_ALPHA
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _AlphaTex;
            float _EnableExternalAlpha;
            fixed4 _Color;
            sampler2D _Noise;
            float _EdgeJag;
            fixed4 _ScorchColor;
            fixed4 _EmberColor;
            float _UseDamage;

            sampler2D _MrpDamageTex;
            float4 _MrpDamageRect;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; float2 world : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                o.world = mul(unity_ObjectToWorld, v.vertex).xy;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
            #if ETC1_EXTERNAL_ALPHA
                fixed4 a = tex2D(_AlphaTex, i.uv);
                c.a = lerp(c.a, a.r, _EnableExternalAlpha);
            #endif
                c *= i.color;

                // 透明な画素はステンシルも書かない (書くと部屋の外の船体にまで影の板が掛かって明るく浮く)
                clip(c.a - 0.004);

                float2 muv = (i.world - _MrpDamageRect.xy) * _MrpDamageRect.zw;
                if (_UseDamage > 0.5 && _MrpDamageRect.z > 0 && all(muv > 0) && all(muv < 1))
                {
                    fixed2 dmg = tex2D(_MrpDamageTex, muv).rg;
                    float n = tex2D(_Noise, i.world * 0.9).r * 0.65 + tex2D(_Noise, i.world * 3.1).r * 0.35;

                    // 穴: なだらかな値をノイズでずらした閾値で切る → 割れたような縁
                    float hole = dmg.r + (n - 0.5) * _EdgeJag;
                    clip(0.5 - hole);

                    // 焦げ: 縁に近いほど黒く、ごく縁だけ熾火の色
                    float scorch = saturate(dmg.g * (0.75 + n * 0.5));
                    c.rgb = lerp(c.rgb, _ScorchColor.rgb, scorch * 0.85);
                    float rim = saturate(1 - abs(hole - 0.42) * 14) * step(0.05, dmg.r);
                    c.rgb += _EmberColor.rgb * rim * 0.6;
                }

                return c;
            }
            ENDCG
        }
    }
}
