// クルーの体を 2 色のグラデーションで塗るシェーダ。描き方はゲームの Unlit/PlayerShader と同じ
// (絵の R = 体の色・G = バイザーの色・B = 影の色の重み / 灰色の画素は元の色のまま / 透明な所の周り 1〜3 画素に輪郭線 /
//  半透明の合成・深度は書かない・ステンシルは _MaskLayer 以上の所だけ)。
// 会議の名札などの枠で切り抜く人形 (Unlit/SimpleMaskedPlayer) にも使う: その時は _MrpMasked = 1 で
// 輪郭線を描かず、ステンシルが _MaskLayer と同じ所だけ描く (本編のそのシェーダと同じ)。
// 違うのは体の色と影の色を、人形の自分の座標の位置で 1 色目 → 2 色目へ移すことだけ。
//   r = u * _GradWX + v * _GradWY + _GradW0  (u, v = 人形の絵の左下 0 → 右上 1)
//   t = saturate((r - _GradPos) / _GradSoft + 0.5)  (境目の位置と、ぼかしの幅。幅 0 でも 1 画素ぶんは滑らかにする)
// 自分の座標を使うので、動きのコマが変わっても色の位置は体に付いたまま。まとめて描く (batching) と自分の座標が
// 世界の座標に変わるので止める。
// 左を向いた人形は絵の頂点ごと左右が入れ替わる (自分の座標の x と絵の u が逆に進む) ので、その時は u を裏返して
// 色を体 (背中側・顔側) に付けたままにする。
Shader "MRP/PlayerGradient"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _BackColor ("Shadow Color", Color) = (1,0,0,1)
        _BodyColor ("Body Color", Color) = (1,1,0,1)
        _VisorColor ("VisorColor", Color) = (0,1,1,1)
        _Outline ("Outline", Range(0, 1)) = 0.0
        _OutlineColor ("OutlineColor", Color) = (1,1,1,1)
        _Desaturate ("Desaturate", Range(0, 1)) = 0.0
        _MaskLayer ("Mask Layer", Float) = 1.0
        _BackColor2 ("Shadow Color 2", Color) = (1,0,0,1)
        _BodyColor2 ("Body Color 2", Color) = (1,1,0,1)
        _GradX0 ("Body left (object x)", Float) = -0.5
        _GradY0 ("Body bottom (object y)", Float) = -0.5
        _GradSX ("1 / body width", Float) = 1
        _GradSY ("1 / body height", Float) = 1
        _GradWX ("Weight of u", Float) = 1
        _GradWY ("Weight of v", Float) = 0
        _GradW0 ("Offset", Float) = 0
        _GradPos ("Boundary position", Float) = 0.5
        _GradSoft ("Boundary softness", Float) = 1
        _MrpMasked ("Masked player (no outline)", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _MrpStencilComp ("Stencil comp", Float) = 7
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "DisableBatching" = "True" }

        Pass
        {
            Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
            Stencil
            {
                Ref [_MaskLayer]
                Comp [_MrpStencilComp]
                Pass Keep
            }
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _BackColor, _BodyColor, _VisorColor, _OutlineColor, _BackColor2, _BodyColor2;
            float _Outline, _MrpMasked;
            float _GradX0, _GradY0, _GradSX, _GradSY, _GradWX, _GradWY, _GradW0, _GradPos, _GradSoft;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; float2 body : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                o.body = v.vertex.xy;
                return o;
            }

            // 上下左右に d 画素ずらした 4 点の不透明さの和
            float Ring(float2 uv, float2 d)
            {
                return tex2D(_MainTex, uv + float2(0, d.y)).a + tex2D(_MainTex, uv + float2(d.x, 0)).a
                     + tex2D(_MainTex, uv - float2(0, d.y)).a + tex2D(_MainTex, uv - float2(d.x, 0)).a;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);

                float3 n = normalize(c.rgb);
                float gray = floor(saturate(dot(n, float3(0.57735, 0.57735, 0.57735)) + 0.01));
                float mixed = ceil(1 - saturate(dot(n.zxy, n.xyz) * 0.5));

                // 絵の u と自分の座標の x が逆に進む = 左を向いている (頂点の x が裏返っているので戻してから測る)
                float x = ddx(i.uv.x) * ddx(i.body.x) < 0 ? -i.body.x : i.body.x;
                float u = (x - _GradX0) * _GradSX;
                float v = (i.body.y - _GradY0) * _GradSY;
                float r = u * _GradWX + v * _GradWY + _GradW0;
                float soft = max(_GradSoft, fwidth(r) * 1.5);
                float t = saturate((r - _GradPos) / soft + 0.5);
                fixed3 body = lerp(_BodyColor.rgb, _BodyColor2.rgb, t);
                fixed3 back = lerp(_BackColor.rgb, _BackColor2.rgb, t);
                fixed3 col = c.r * body + c.g * _VisorColor.rgb + c.b * back;
                col = lerp(col, c.rgb, gray);
                fixed3 painted = c.rgb + mixed * (col - c.rgb);
                if (_MrpMasked > 0.5) return fixed4(painted, c.a) * i.color;

                float2 px = _MainTex_TexelSize.xy;
                float around = Ring(i.uv, px) + Ring(i.uv, px * 2) + Ring(i.uv, px * 3);

                fixed4 outline = fixed4(_OutlineColor.rgb, saturate(_Outline) * (around + c.a));
                fixed4 res = lerp(outline, fixed4(painted, c.a), round(c.a + 0.05));
                return res * i.color;
            }
            ENDCG
        }
    }
}
