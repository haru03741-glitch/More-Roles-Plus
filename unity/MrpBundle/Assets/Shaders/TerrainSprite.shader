// 船の部屋の絵 (SpriteRenderer) を描くシェーダ。描き方はゲームの Unlit/MaskShader と同じ
// (通常の半透明合成・深度書き込みあり・ステンシルへ _MaskLayer を書く → 影の板 Unlit/ShadowShader はその上にだけ影を落とす)。
// 加えて世界全体に敷いた「損傷マスク」(_MrpDamageTex) を世界座標で引き、壊れた所を描かずに抜き、焦げを乗せる。
//   マスクの R = 穴 (0..1 のなだらかな値。ノイズを足した閾値で切るので縁がぎざぎざになる)
//   マスクの G = 焦げの濃さ / B = 熾火 (割れ口の照り。爆発だけが書く)
// マスクの置き場所は _MrpDamageRect (xy = 世界座標の左下、zw = 1 / 幅と高さ) で全マテリアル共通。
Shader "MRP/TerrainSprite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _MaskLayer ("Stencil Ref", Float) = 1
        _MaskComp ("Stencil Comp", Float) = 8
        _StencilPass ("Stencil Pass", Float) = 2
        _Noise ("Edge Noise", 2D) = "gray" {}
        _Cells ("Break Cells", 2D) = "gray" {}
        _EdgeJag ("Edge jaggedness", Float) = 0.35
        _ScorchColor ("Scorch color", Color) = (0.08, 0.06, 0.05, 1)
        _EmberColor ("Ember rim color", Color) = (1.0, 0.45, 0.12, 1)
        _OutlineColor ("Break outline color", Color) = (0.086, 0.086, 0.094, 1)
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
            Pass [_StencilPass]
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
            sampler2D _Cells;
            float _EdgeJag;
            fixed4 _ScorchColor;
            fixed4 _EmberColor;
            float _UseDamage;
            fixed4 _OutlineColor;

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
                bool inMap = _MrpDamageRect.z > 0 && all(muv > 0) && all(muv < 1);

                // 2 = 穴の向こうの床: 抜けた所 (より少し広め) にだけ描き、割れ口の近くほど暗く落として深さを出す
                if (_UseDamage > 1.5)
                {
                    if (!inMap) discard;
                    float dr = tex2D(_MrpDamageTex, muv).r;
                    float cl = tex2D(_Cells, i.world * 0.4).r;
                    float nn = tex2D(_Noise, i.world * 3.1).r;
                    float hv = dr + (cl - 0.5) * _EdgeJag + (nn - 0.5) * 0.06;
                    clip(hv - 0.38);
                    c.rgb *= lerp(0.45, 1.0, smoothstep(0.45, 0.8, hv));
                    return c;
                }

                // 3 = ひびの板: 穴の中 (割れ口と同じ判定) と家具の上には描かない
                if (_UseDamage > 2.5)
                {
                    if (inMap)
                    {
                        fixed4 dk = tex2D(_MrpDamageTex, muv);
                        float ck = tex2D(_Cells, i.world * 0.4).r;
                        float nk = tex2D(_Noise, i.world * 3.1).r;
                        clip(0.5 - (dk.r + (ck - 0.5) * _EdgeJag + (nk - 0.5) * 0.06));
                        clip(0.5 - dk.a);
                    }
                    return c;
                }

                if (_UseDamage > 0.5 && inMap)
                {
                    fixed3 dmg = tex2D(_MrpDamageTex, muv).rgb;
                    // 細胞ごとに一定のずれ (角張った欠け) + ごく弱いノイズ (縁のガタつき)
                    float cell = tex2D(_Cells, i.world * 0.4).r;
                    float n = tex2D(_Noise, i.world * 3.1).r;

                    // 穴: なだらかな値を細胞でずらした閾値で切る → 割れた破片の形の縁
                    float hole = dmg.r + (cell - 0.5) * _EdgeJag + (n - 0.5) * 0.06;
                    clip(0.5 - hole);

                    // 焦げ: 本編の描き方に合わせて 2 段のベタ塗り (ぼかさない)
                    float scorch = dmg.g * (0.55 + cell * 0.7);
                    float level = scorch > 0.75 ? 0.75 : scorch > 0.4 ? 0.4 : 0.0;
                    c.rgb = lerp(c.rgb, _ScorchColor.rgb, level);

                    // 割れ口: 本編の絵と同じ濃い輪郭線 → そのすぐ外側にだけ熾火の照り
                    float edge = step(0.05, dmg.r);
                    float outline = step(0.41, hole) * edge;
                    float ember = step(0.33, hole) * edge * (1 - outline) * step(0.3, dmg.b);
                    c.rgb = lerp(c.rgb, _EmberColor.rgb, ember * 0.85);
                    c.rgb = lerp(c.rgb, _OutlineColor.rgb, outline);
                }

                return c;
            }
            ENDCG
        }
    }
}
