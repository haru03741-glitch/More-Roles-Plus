// 立ち合いの HUD の体力のバー・中央の鍔・顔の額縁。板 1 枚に式で描く (斜めに切った黒漆の枠に金の縁・朱と金の中身)。
// 値は頂点の色で受け取る (Android の MaterialPropertyBlock に SetVector が無く、色なら SpriteRenderer.color の 1 回で済む):
//   r = 体力 (0〜1)・g = 削られた分の影の端 (0〜1)・b = 形 (0 = バー・0.5 = 鍔・1 = 額縁)・a = 全体の濃さ
// 板の縦横比は画面上の微分から求める (スプライトは束ねて描かれ、オブジェクトの行列から大きさを読めない)。
// 長さは板の高さを 1 とした単位で測るので、大きなバーと小さなバーで縁の太さの割合が揃う。
// バーの uv.x = 0 は外側の端 (顔の側)。右のバーは板を左右に返して置く。合成は乗算済みアルファ
Shader "MRP/UiBar"
{
    Properties
    {
        _MainTex ("Sprite", 2D) = "white" {}
        _Noise ("Noise", 2D) = "gray" {}
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" "CanUseSpriteAtlas" = "False" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _Noise;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 world : TEXCOORD1; float4 color : COLOR; };

            static const float Slant = 0.45;   // 端の傾き (高さ 1 あたりの横のずれ)
            static const float Rim = 0.1;      // 金の縁
            static const float Gap = 0.09;     // 黒漆の溝

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.world = mul(unity_ObjectToWorld, v.vertex).xy;
                o.color = v.color;
                return o;
            }

            // 金の縁: k = 縁の中の位置 (0 = 外・1 = 内)・lit = 光の当たり (-1〜1)・glint = 流れる光
            float3 Gold(float k, float lit, float glint)
            {
                float ridge = 1.0 - abs(k - 0.4) * 2.2;
                float3 dark = float3(0.42, 0.25, 0.05), light = float3(1.0, 0.85, 0.42);
                float3 c = lerp(dark, light, saturate(0.45 + 0.4 * lit + 0.35 * ridge));
                return c + float3(1.0, 0.95, 0.8) * (saturate(ridge) * saturate(lit) * 0.35 + glint);
            }

            // 平行四辺形 (左の辺 x = Slant * y・右の辺 x = A - Slant + Slant * y・y = 0〜1) までの符号付きの距離 (内が負)
            float Para(float2 p, float a)
            {
                float n = rsqrt(1.0 + Slant * Slant);
                float xl = Slant * p.y, xr = a - Slant + Slant * p.y;
                float dx = max(xl - p.x, p.x - xr) * n;
                float dy = max(-p.y, p.y - 1.0);
                return max(dx, dy);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float t = _Time.y;
                // 板の縦横比 (画面上の微分から)。p = 高さ 1 の単位の位置
                float sx = abs(ddx(i.world.x) / max(abs(ddx(i.uv.x)), 1e-6));
                float sy = abs(ddy(i.world.y) / max(abs(ddy(i.uv.y)), 1e-6));
                float a = sx / max(sy, 1e-6);
                float2 p = float2(i.uv.x * a, i.uv.y);
                float mode = i.color.b;
                float3 col;
                float d, ds;

                if (mode > 0.25 && mode < 0.75)
                {
                    // 鍔: 四つの葉の木瓜形。中は黒漆で、中心に朱の灯り
                    float2 c = p - float2(a * 0.5, 0.5);
                    float r = length(c) * 2.0;
                    float th = atan2(c.y, c.x);
                    float rb = 0.8 + 0.16 * pow(abs(cos(2.0 * th + 0.785)), 0.7);
                    d = (r - rb) * 0.5;
                    float2 cs = c - float2(0.03, -0.05);
                    ds = (length(cs) * 2.0 - rb) * 0.5;
                    float rim = Rim * 0.9;
                    float2 dir = c / max(length(c), 1e-4);
                    float lit = dot(dir, float2(-0.45, 0.9));
                    float glint = pow(saturate(cos(th - t * 1.3)), 40.0) * 0.5;
                    if (d > -rim) col = Gold(saturate(-d / rim), lit, glint * step(-rim, d));
                    else
                    {
                        float glow = exp(-r * r * 3.0) * (0.75 + 0.25 * sin(t * 3.0));
                        col = lerp(float3(0.05, 0.02, 0.02), float3(0.75, 0.14, 0.05), glow * 0.8);
                        // 内側の細い金の輪
                        col += float3(0.9, 0.7, 0.3) * exp(-abs(r - 0.52) * 60.0) * 0.6;
                    }
                }
                else
                {
                    bool plate = mode >= 0.75;
                    d = Para(p, a);
                    ds = Para(p - float2(0.06, -0.1), a);
                    float xl = Slant * p.y, xr = a - Slant + Slant * p.y;
                    float lit = (p.y - 0.5) * 2.0;
                    // 縁を流れる光 (4 秒に 1 回・外から内へ)
                    float sweep = frac(t * 0.25) * (a + 3.0) - 1.5;
                    float q = (p.x - p.y * Slant - sweep) * 2.5;
                    float glint = exp(-q * q) * 0.55;
                    if (d > -Rim) col = Gold(saturate(-d / Rim), lit, glint);
                    else if (d > -Rim - Gap) col = float3(0.04, 0.015, 0.015) + float3(0.25, 0.06, 0.03) * saturate(lit) * 0.4;
                    else
                    {
                        float inner = Rim + Gap;
                        float qy = saturate((p.y - inner) / (1.0 - 2.0 * inner));
                        if (plate)
                        {
                            // 額縁の中: 顔の後ろの朱のスポットライトと斜めの集中線
                            float2 c = p - float2(a * 0.5, 0.45);
                            float spot = exp(-dot(c, c) * 3.5);
                            col = lerp(float3(0.05, 0.015, 0.02), float3(0.7, 0.12, 0.05), spot);
                            col += 0.05 * step(0.5, frac((p.x - p.y * 0.6) * 6.0));
                        }
                        else
                        {
                            float fx = saturate((p.x - xl) / max(xr - xl, 1e-4));
                            float hp = i.color.r, ghost = i.color.g;
                            // 地: 暗い紅に細い斜めの筋
                            col = float3(0.08, 0.02, 0.03) + float3(0.05, 0.015, 0.015) * step(0.5, frac((p.x + p.y * 0.6) * 6.0));
                            if (fx < ghost) col = lerp(float3(0.45, 0.03, 0.04), float3(0.85, 0.12, 0.08), qy);
                            if (fx < hp)
                            {
                                // 多い = 金→朱・半分を切る = 朱→紅・少ない = 紅が脈打つ。上半分は明るく、真ん中で段が付く
                                float k1 = smoothstep(0.2, 0.4, hp), k2 = smoothstep(0.45, 0.65, hp);
                                float3 top = lerp(lerp(float3(1.0, 0.55, 0.45), float3(1.0, 0.8, 0.4), k1), float3(1.0, 0.95, 0.62), k2);
                                float3 mid = lerp(lerp(float3(0.85, 0.06, 0.08), float3(0.98, 0.32, 0.06), k1), float3(1.0, 0.55, 0.08), k2);
                                float3 bot = lerp(lerp(float3(0.35, 0.0, 0.03), float3(0.55, 0.06, 0.03), k1), float3(0.62, 0.12, 0.02), k2);
                                float step50 = smoothstep(0.47, 0.53, qy);
                                col = lerp(lerp(bot, mid * 0.9, qy / 0.5), lerp(mid, top, (qy - 0.5) / 0.5), step50);
                                // 筆の擦れのむら (ゆっくり流れる)
                                float n = tex2Dlod(_Noise, float4(p.x * 0.35 - t * 0.05, qy * 0.25 + p.x * 0.02, 0.0, 0.0)).r;
                                col *= 0.86 + 0.28 * n;
                                // 上の艶の帯
                                col += float3(1.0, 0.97, 0.9) * smoothstep(0.7, 0.8, qy) * (1.0 - smoothstep(0.86, 0.94, qy)) * 0.45;
                                // 減る端の光る線
                                float edge = exp(-abs(fx - hp) * (xr - xl) * 14.0);
                                col += float3(1.0, 0.9, 0.6) * edge * 0.7;
                                if (hp < 0.35) col *= 0.82 + 0.28 * (0.5 + 0.5 * sin(t * 14.0));
                            }
                            // 縁の下の影 (中身が枠にはまって見える)
                            col *= 0.6 + 0.4 * (1.0 - smoothstep(0.82, 1.0, qy));
                        }
                    }
                }

                float w = max(fwidth(d), 1e-4);
                float cover = saturate(0.5 - d / w);
                float shadow = saturate(0.5 - ds / 0.12) * 0.45;
                float alpha = i.color.a;
                float ao = (cover + shadow * (1.0 - cover)) * alpha;
                return fixed4(col * cover * alpha, ao);
            }
            ENDCG
        }
    }
}
