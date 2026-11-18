// 3D の艦隊戦の夜の街の灯 (航空障害灯・高架の灯)。灯ごとに 4 頂点の板を置き、頂点で目の前へ向けて広げる。
// 警報の間は航空障害灯だけがゆっくり脈打ち、ほかの灯は停電で消える。
// 頂点: 位置 = 灯の中心・NORMAL xy = 板の角 (-1/1)・COLOR = 色 + a 点滅の組 (0 点いたまま・1 揃って点滅・2 ゆっくり明滅)・TEXCOORD0 = (芯の大きさ, 位相)。
// 遠くの灯は画素より小さくならないよう広げ、霞の中でも点で残す (光は霞より強い)。足す光 (One / OneMinusSrcAlpha で a = 0)
Shader "MRP/FlightLamp"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
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

            float4 _MrpFltSky;
            float4 _MrpCity;
            float4 _MrpCityAlarm;

            float Hash12(float2 p)
            {
                float3 q = frac(float3(p.xyx) * 0.1031);
                q += dot(q, q.yzx + 33.33);
                return frac((q.x + q.y) * q.z);
            }

            struct appdata { float4 vertex : POSITION; float4 normal : NORMAL; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 q : TEXCOORD0; fixed3 col : TEXCOORD1; float k : TEXCOORD2; };

            v2f vert(appdata v)
            {
                v2f o;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                float dist = distance(_WorldSpaceCameraPos, wp);
                // 芯は少なくとも約 2 画素 (縦の視野 62° の 1080 画素で 1 画素 ≒ 距離 × 0.0011)
                float core = max(v.uv.x, dist * 0.0024);
                float4 vp = mul(UNITY_MATRIX_V, float4(wp, 1.0));
                vp.xy += v.normal.xy * core * 3.0;
                o.pos = mul(UNITY_MATRIX_P, vp);
                o.q = v.normal.xy * 3.0;
                o.col = v.color.rgb;
                float cls = floor(v.color.a * 255.0 + 0.5);
                float t = _Time.y;
                float on = 1.0;
                if (cls > 0.5 && cls < 1.5) on = smoothstep(0.0, 0.04, frac(t / 1.5)) * (1.0 - smoothstep(0.36, 0.46, frac(t / 1.5))); // 1.5 秒ごとに 0.4 秒
                else if (cls > 1.5) on = 0.55 + 0.45 * sin(t * 1.9 + v.uv.y);
                // 警報の間: 電池で動く航空障害灯 (組 1) だけが揃ってゆっくり脈打ち、ほかの灯は停電の前線が来ると消える (街の壁と同じ決まり)
                float s = _MrpCityAlarm.x;
                float p = frac(max(s, 0.0) / 2.2) * 2.2;
                float beat = smoothstep(0.0, 0.08, p) * exp(-p / 0.8);
                if (cls > 0.5 && cls < 1.5) on = lerp(on, 0.22 + 1.4 * beat, _MrpCity.x);
                else
                {
                    float seed = frac(v.uv.y * 7.13 + wp.x * 0.013) * 97.0;
                    float cut = s - length(wp.xz - _MrpCityAlarm.zw) / 900.0 - Hash12(float2(seed, 5.1)) * 0.3;
                    float back = s - _MrpCityAlarm.y - Hash12(float2(seed, 8.7)) * 2.5;
                    float blink = step(Hash12(float2(seed * 3.3, floor(t * 15.0))), 0.5);
                    on *= max(cut < 0.0 ? 1.0 : (cut < 0.45 ? blink : 0.0), back < 0.0 ? 0.0 : (back < 0.35 ? blink : 1.0));
                }
                // 遠くは霞で弱まるが、点の光は霞より残る。大きさを広げた分だけ明るさを落として、遠くの灯が大きな玉に見えないように
                float fog = 1.0 - exp(-dist / max(_MrpFltSky.a * 1.8, 1.0));
                o.k = on * (1.0 - fog * 0.8) * _MrpCity.z * saturate(v.uv.x / core * 1.6 + 0.25);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float r2 = dot(i.q, i.q);
                float core = exp(-r2 * 4.0);
                float halo = exp(-r2 * 0.55) * 0.28;
                float3 c = i.col * (core * 1.6 + halo) + core * core * 0.6;
                return fixed4(c * i.k, 0);
            }
            ENDCG
        }
    }
}
