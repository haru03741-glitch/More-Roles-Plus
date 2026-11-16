// 3D の艦隊戦の火花・炎・煙・宇宙の塵。C++ が 1 枚 4 頂点の板を書く。
// uv.x < 1.5 = 丸いにじみ (uv 0〜1 の中心からの距離で形)・uv.x >= 2 = 速さの向きに伸びる筋 (uv.x - 2 = 後ろ 0 → 前 1・uv.y = 幅)・
// uv.x >= 4.5 = 噴射口の炎 (uv.x - 5 = 根元 0 → 先 1・uv.y の小数 = 横・uv.y の 2 ごとが出力の段 0..7)・
// uv.x < -1.5 = 固い欠片 (岩・ゴミ。uv.x + 4 と uv.y の小数が板の中・uv.y の 2 ごとが形の番号。縁はくっきり・左上から光が当たった濃淡)。
// 色は乗算済み: rgb = 足す光・a = 後ろを隠す量。炎は光りながら少し隠し、黒煙は光らずに隠す (One / OneMinusSrcAlpha)。
// 丸い板はノイズで縁を崩して、きれいな円に見せない
Shader "MRP/FlightFx"
{
    Properties
    {
        _Noise ("Noise", 2D) = "gray" {}
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+1" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
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

            sampler2D _Noise;

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float2 wn : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wn = wp.xz * 0.03 + wp.y * 0.02;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float m;
                if (i.uv.x < -1.5)
                {
                    float k = floor(i.uv.y * 0.5);
                    float2 d = float2(i.uv.x + 4.0, i.uv.y - k * 2.0) * 2.0 - 1.0;
                    float a = atan2(d.y, d.x);
                    // いびつな輪郭 (形ごとに位相を変えた小さな揺らぎを重ねる。揺らぎを大きくすると星や花の記号に見える)。0 は板の切れ端らしく四角寄り
                    float r = k < 0.5 ? max(abs(d.x) * 1.15, abs(d.y) * 1.6)
                                      : length(d) / (0.8 + 0.06 * sin(a * 2.0 + k * 1.7) + 0.045 * sin(a * 3.0 + k * 4.1) + 0.03 * sin(a * 5.0 + k * 2.3));
                    float aa = fwidth(r) * 1.5 + 1e-4;
                    m = 1.0 - smoothstep(0.92 - aa, 0.92 + aa, r);
                    float lit = 0.62 + 0.38 * saturate(dot(d, float2(-0.55, 0.65)) * 0.8 + 0.5);
                    lit *= 1.0 - 0.25 * smoothstep(0.55, 0.92, r);
                    return fixed4(i.color.rgb * lit * m, i.color.a * m);
                }
                if (i.uv.x >= 4.5)
                {
                    // 噴射口の炎: a = 根元 0 → 先 1・y = 軸からの横 (-1..1)・h = 出力の段 (0..1)。
                    // 根元の白い芯 (出力で伸びる)・外へ広がって先で消える炎・加速の時だけ芯の後ろに並ぶ菱形の節。ノイズが軸に沿って流れて揺らす
                    float a = saturate(i.uv.x - 5.0);
                    float lv = floor(i.uv.y * 0.5);
                    float h = lv / 7.0;
                    float y = abs((i.uv.y - lv * 2.0) * 2.0 - 1.0);
                    float n = tex2D(_Noise, float2(a * 1.3 - _Time.y * 5.0, y * 0.35 + lv * 0.13 + i.wn.x)).r;
                    float env = (0.55 + 0.45 * pow(a, 0.7)) * (0.9 + 0.2 * n);
                    float body = pow(saturate(1.0 - y / env), 1.4) * pow(saturate(1.0 - a), 0.7 + 1.2 * (1.0 - h)) * smoothstep(0.0, 0.04, a);
                    float glow = pow(saturate(1.0 - y), 3.0) * pow(saturate(1.0 - a), 1.6) * 0.22 * smoothstep(0.0, 0.04, a);
                    float coreL = 0.14 + 0.18 * h;
                    float coreW = 0.42 * saturate(1.0 - a / coreL);
                    float core = pow(saturate(1.0 - y / max(coreW, 1e-3)), 1.4) * step(a, coreL) * smoothstep(0.0, 0.03, a);
                    float d = abs(frac((a - coreL) * (3.5 + 2.0 * h)) - 0.5) * 2.0;
                    float dia = saturate(1.0 - (d / 0.8 + y / (0.4 * env)));
                    dia = pow(dia, 1.3) * smoothstep(0.5, 0.9, h) * saturate(1.0 - a * 1.3) * step(coreL, a);
                    // 芯と節は白へ寄せる (足す白も頂点の明るさに比例させる: 軸を正面から見て薄めた板が白く残らない)
                    float3 hot = i.color.rgb + 0.35 * max(i.color.r, max(i.color.g, i.color.b));
                    float3 c = i.color.rgb * ((body * 0.5 + glow) * (0.85 + 0.3 * n)) + core * 0.75 * hot + dia * 0.9 * hot;
                    return fixed4(c, 0);
                }
                if (i.uv.x >= 1.5)
                {
                    float x = i.uv.x - 2.0;
                    float y = abs(i.uv.y * 2.0 - 1.0);
                    m = saturate(1.0 - y) * smoothstep(0.0, 0.5, x);
                    m *= m;
                }
                else
                {
                    float2 d = i.uv * 2.0 - 1.0;
                    float n = tex2D(_Noise, i.uv * 0.6 + i.wn).r;
                    float r = length(d) * (0.85 + n * 0.35);
                    m = saturate(1.0 - r);
                    m = m * m * (3.0 - 2.0 * m);
                }
                return fixed4(i.color.rgb * m, i.color.a * m);
            }
            ENDCG
        }
    }
}
