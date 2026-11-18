// 3D の艦隊戦の夜の街の空。カメラに付いた内向きの球に向きだけから描く: ほぼ黒い空・地平の街明かりの赤いにじみ・
// 下から赤く照らされた薄い雲・まばらで暗い星。地平より下は霞の色 (街の遠くが溶ける色) にそろえる
Shader "MRP/FlightNight"
{
    Properties
    {
        _Noise ("Noise", 2D) = "gray" {}
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
            float4 _MrpFltSky;
            float4 _MrpCity;

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

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float e = d.y;
                float3 fogC = _MrpFltSky.rgb;
                float3 c = float3(0.004, 0.0015, 0.003);
                // 地平の赤いにじみ (街の明かりが低い空気を照らす)
                c += fogC * exp(-max(e + 0.02, 0.0) * 9.0) * 1.05;
                // 下から照らされた薄い雲 (地平に近いほど明るい)
                if (e > 0.0)
                {
                    float2 uv = d.xz / (e + 0.12) * 0.11 + float2(_Time.y * 0.002, 0.0);
                    float n = tex2D(_Noise, uv).r * 0.65 + tex2D(_Noise, uv * 2.3 + 0.31).r * 0.35;
                    float cloud = smoothstep(0.52, 0.8, n) * exp(-e * 2.5);
                    c += float3(0.28, 0.03, 0.035) * cloud * 0.45 * (1.0 - _MrpCity.x * 0.7); // 停電で下からの照り返しが消える
                    // まばらな暗い星 (雲の切れ間だけ)
                    float3 a = abs(d);
                    float2 suv = a.y >= max(a.x, a.z) ? d.xz / a.y : (a.x >= a.z ? d.yz / a.x : d.xy / a.z);
                    float2 g = suv * 70.0;
                    float h = hash(float3(floor(g), 3.0));
                    float2 pos = float2(hash(float3(floor(g), 9.0)), hash(float3(floor(g), 21.0)));
                    float s = step(0.86, h) * saturate(1.0 - length(frac(g) - pos) / 0.07);
                    c += s * 0.35 * smoothstep(0.08, 0.35, e) * (1.0 - cloud);
                }
                else c = lerp(c, fogC, saturate(-e * 20.0));
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
