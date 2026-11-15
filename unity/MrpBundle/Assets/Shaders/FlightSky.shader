// 3D の艦隊戦の宇宙の背景。カメラの位置に置いた内向きの球に、向きだけから星と星雲を描く (画像を持たない)。
// 星 = 立方体の面に分けた格子ごとに 1 個の点 (明るさ・色は格子の番号の hash)。星雲 = ノイズの絵を向きの 2 つの面で読み重ねる。
// 遠い光源の向き (_MrpFltLight) に恒星の光のにじみ
Shader "MRP/FlightSky"
{
    Properties
    {
        _Noise ("Noise", 2D) = "gray" {}
        _NebulaA ("Nebula color A", Color) = (0.18, 0.08, 0.32, 1)
        _NebulaB ("Nebula color B", Color) = (0.04, 0.16, 0.3, 1)
        _Base ("Base color", Color) = (0.01, 0.012, 0.03, 1)
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _Noise;
            fixed4 _NebulaA, _NebulaB, _Base;
            float4 _MrpFltLight;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            float hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float Stars(float3 d, float cells)
            {
                float3 a = abs(d);
                float2 uv; float face;
                if (a.x >= a.y && a.x >= a.z) { uv = d.yz / a.x; face = sign(d.x); }
                else if (a.y >= a.z) { uv = d.xz / a.y; face = 2.0 + sign(d.y); }
                else { uv = d.xy / a.z; face = 4.0 + sign(d.z); }
                float2 g = uv * cells;
                float2 cell = floor(g);
                float h = hash(float3(cell, face));
                float2 pos = float2(hash(float3(cell, face + 7.0)), hash(float3(cell, face + 13.0)));
                float dd = length(frac(g) - pos);
                float size = 0.04 + h * 0.06;
                return step(0.72, h) * saturate(1.0 - dd / size) * (0.4 + h);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float n1 = tex2D(_Noise, d.xz * 0.35 + d.y * 0.2).r;
                float n2 = tex2D(_Noise, d.yx * 0.6 + 0.37).r;
                float neb = saturate((n1 * 0.65 + n2 * 0.35 - 0.42) * 2.4);
                float band = saturate(1.0 - abs(d.y + 0.15 * sin(d.x * 3.0)) * 2.2);
                float3 c = _Base.rgb + lerp(_NebulaB.rgb, _NebulaA.rgb, n2) * neb * (0.35 + band * 0.9);
                float s = Stars(d, 90.0) + Stars(d.zxy, 37.0) * 1.3;
                c += s * lerp(float3(0.7, 0.8, 1.0), float3(1.0, 0.9, 0.75), n1);
                float sun = saturate(dot(d, normalize(_MrpFltLight.xyz)));
                c += float3(1.0, 0.86, 0.66) * (pow(sun, 900.0) * 3.0 + pow(sun, 24.0) * 0.18);
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
