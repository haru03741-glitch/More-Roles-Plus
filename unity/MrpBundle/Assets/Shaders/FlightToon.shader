// 3D の艦隊戦の船。頂点の色 (a < 1 = 光る強さ) を 3 段の明暗で塗り、太い縁取りを付ける (本編の物と同じ「太い縁 + 段の陰」の様式)。
// Android の削られた Unity では Light の設定が使えないので、光の向きと色は global の値 (_MrpFltLight・_MrpFltSky) で受ける。
// 縁取りは面を裏返して外へ押し出す 2 つ目の描き方。押し出す向きは頂点の TEXCOORD1 (同じ位置の面の法線を足したなめらかな法線) で、
// 画面の上で同じ太さになるよう奥行き (w) を掛ける。遠いほど空の色へ寄せて奥行きを出す。
Shader "MRP/FlightToon"
{
    Properties
    {
        _Outline ("Outline width", Float) = 0.0035
        _OutlineColor ("Outline color", Color) = (0.05, 0.05, 0.08, 1)
        _Flash ("Hit flash", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" "IgnoreProjector" = "True" }

        Pass
        {
            Cull Back
            ZWrite On
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float4 _MrpFltLight;   // xyz = 光の来る向き (世界)・w = 強さ
            float4 _MrpFltSky;     // rgb = 遠くの色・a = 霞む距離
            float4 _MrpFltRim;     // rgb = 縁の照り返しの色・a = 強さ
            float _Flash;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float3 n : TEXCOORD0; float3 wp : TEXCOORD1; fixed4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.n = UnityObjectToWorldNormal(v.normal);
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.n);
                float3 l = normalize(_MrpFltLight.xyz);
                float3 vdir = normalize(_WorldSpaceCameraPos - i.wp);
                float ndl = dot(n, l);
                // 3 段: 明・中・暗 (段の境は少しだけぼかす)
                float lit = smoothstep(0.45, 0.52, ndl);
                float mid = smoothstep(-0.12, -0.05, ndl);
                float3 base = i.color.rgb;
                float3 shade = base * float3(0.42, 0.45, 0.6);
                float3 c = lerp(shade, base * 0.8, mid);
                c = lerp(c, base * 1.06, lit);
                // 縁の照り返し (光の反対側の縁を空の色で)
                float rim = pow(1.0 - saturate(dot(n, vdir)), 3.0);
                c += _MrpFltRim.rgb * _MrpFltRim.a * rim * (1.0 - lit * 0.6);
                // 照り (金属の光沢を段で)
                float3 h = normalize(l + vdir);
                c += step(0.94, dot(n, h)) * lit * 0.22;
                // 光る所 (a < 1) は陰を付けず明るく
                float glow = 1.0 - i.color.a;
                c = lerp(c, base * (1.4 + glow * 1.6), saturate(glow * 2.0));
                c = lerp(c, float3(1, 1, 1), _Flash);
                // 奥行きの霞
                float d = distance(_WorldSpaceCameraPos, i.wp);
                float fog = saturate(d / max(_MrpFltSky.a, 1.0));
                c = lerp(c, _MrpFltSky.rgb, fog * fog * 0.7 * (1.0 - glow));
                return fixed4(c, 1);
            }
            ENDCG
        }

        Pass
        {
            Cull Front
            ZWrite On
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float _Outline;
            fixed4 _OutlineColor;
            float4 _MrpFltSky;

            struct appdata { float4 vertex : POSITION; float3 smooth : TEXCOORD1; };
            struct v2f { float4 pos : SV_POSITION; float fog : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                float4 clip = UnityObjectToClipPos(v.vertex);
                float3 nw = UnityObjectToWorldNormal(v.smooth);
                float2 nc = normalize(mul((float3x3)UNITY_MATRIX_VP, nw).xy + 1e-5);
                // 画面の縦横比を戻して、どの向きにも同じ太さ
                nc.x *= _ScreenParams.y / _ScreenParams.x;
                clip.xy += nc * _Outline * clip.w * 2.0;
                o.pos = clip;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.fog = saturate(distance(_WorldSpaceCameraPos, wp) / max(_MrpFltSky.a, 1.0));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(lerp(_OutlineColor.rgb, _MrpFltSky.rgb, i.fog * i.fog * 0.7), 1);
            }
            ENDCG
        }
    }
}
