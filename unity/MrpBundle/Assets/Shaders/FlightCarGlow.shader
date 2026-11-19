// 夜の街の車の灯 (前照灯・尾灯・点滅灯) の光の板。車の立体と同じ枠の番号で同じ車へ置き、目の前へ向けて広げる。
// 頂点: POSITION = セダンの灯の位置 (車の中)・NORMAL xy = 板の角 (-1/1)・z = 0 前照灯 / 1 尾灯・TEXCOORD0 = ミニバンへの差 xyz + 枠の番号。
// 前照灯は前から・尾灯は後ろから見た時に強い。遠くでは地面の絵の灯 (FlightCity の Dot) へ同じ距離で渡す。足す光 (a = 0)
Shader "MRP/FlightCarGlow"
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
            #include "FlightCarCommon.cginc"

            struct appdata { float4 vertex : POSITION; float4 normal : NORMAL; float4 d : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 q : TEXCOORD0; float3 col : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o = (v2f)0;
                CarLane ln;
                float kind;
                CarInfo car = PlaceCar(v.d.w, ln, kind);
                float morph, taxi;
                CarKind(car, ln.hash, morph, taxi);
                float3 center; float2 fwd;
                CarFrame(ln, car, center, fwd);
                float dist = distance(_WorldSpaceCameraPos, center + float3(0, 0.7, 0));
                if (car.ok < 0.5 || car.burn > 0.0 || dist > CAR_FAR_R)
                {
                    o.pos = float4(0, 0, -10, 1);
                    return o;
                }
                float blink;
                CarSignals(car, center, blink);
                float3 p = v.vertex.xyz + v.d.xyz * morph;
                p.y *= saturate((CAR_FAR_R - dist) / CAR_FLAT);
                float2 right = float2(fwd.y, -fwd.x);
                float3 wp = center + float3(right.x * p.x + fwd.x * p.z, p.y, right.y * p.x + fwd.y * p.z);
                float3 toCam = normalize(_WorldSpaceCameraPos - wp);
                float tail = v.normal.z > 0.5 ? 1.0 : 0.0;
                float face = dot(float3(fwd.x, 0, fwd.y), toCam) * (tail > 0.5 ? -1.0 : 1.0);
                float aim = saturate(face * 0.8 + 0.2);
                aim *= aim;
                float3 col = tail > 0.5 ? float3(1.0, 0.05, 0.03) * 1.6 * (1.0 + 2.2 * car.brake) : float3(1.0, 0.92, 0.8) * 3.0;
                col += float3(1.0, 0.45, 0.05) * 2.2 * blink * saturate(face * 2.0 + 1.0);
                // 芯は少なくとも約 2 画素。広げた分だけ暗くする (地面の Dot と同じ考え)
                float r = tail > 0.5 ? 0.16 : 0.2;
                float core = max(r, dist * 0.0024);
                float4 vp = mul(UNITY_MATRIX_V, float4(wp, 1.0));
                // 板は車の面の少し手前に出して、車体に半分埋もれないように
                vp.xyz += mul((float3x3)UNITY_MATRIX_V, toCam) * 0.12;
                vp.xy += v.normal.xy * core * 3.0;
                o.pos = mul(UNITY_MATRIX_P, vp);
                o.q = v.normal.xy * 3.0;
                float fog = 1.0 - exp(-dist / max(_MrpFltSky.a * 1.8, 1.0));
                float mist = CarMist(wp, dist);
                float flatK = saturate((CAR_FAR_R - dist) / CAR_FLAT);
                o.col = col * aim * (r * r) / (core * core) * (1.0 - fog * 0.8) * (1.0 - mist * 0.85) * _MrpCity.z * flatK * 0.55;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float r2 = dot(i.q, i.q);
                float core = exp(-r2 * 4.0);
                float halo = exp(-r2 * 0.6) * 0.22;
                float3 c = i.col * (core * 1.6 + halo) + i.col * core * core * 0.3;
                return fixed4(c, 0);
            }
            ENDCG
        }
    }
}
