// ノヴァの光線。板 1 枚に式で描く: 砲口の前の頂点 1 点から先 (uv.x = 1) へ、尖った先から急に開いて、先へ行くほど一定の太さに近づく太い柱。
// 中身は光をよく吸う濃い暗いガスで、背景を隠す。光っているのは、長さ方向に流れて伸びる筋 (太い筋と細い髪のような筋) と、まだらに明るい芯の帯だけ。
// ガスの中には濃い煙のかたまりが流れ、そこでは筋も沈む。色は 5 段の値 (_R0r〜_R4b・材質に書く) で受け、明るさをその並びへ写す。
// 立体: 断面を円柱として、奥行き (弦の長さ) の分だけ濃く・左上からの光が当たる側の縁を明るく・反対側を暗くする。筋は円柱の表面に巻いて読む (縁ほど詰まる)。
// 大きさは絵ごとの値 (MaterialPropertyBlock の SetFloat) で受け取る: _Len = 板の長さ・_Back = 板のうち頂点より後ろの長さ (頂点の光のにじみの分)・_Half = 太くなりきった時の半分の幅・_Core = 芯の半分の幅 (世界の単位)・
//   _Lit = 光線の横向き (板の +y) と画面の左上の光の向きの内積 (-1〜1)。板の縦は _Half の 1.3 倍まで取ってあり、外の煙のにじみはそこまで。
// 時間で変わる値は頂点の色: r = 太り具合 (0 = 芯だけ・1 = 太くなりきる)・g = 熱さ (細って消える時に下げる)・b = 1 なら床に落ちた影と照り返し・a = 全体の濃さ。
// 合成は乗算済みアルファ (One / OneMinusSrcAlpha): 暗いガスは背景を隠し、明るい筋ほどアルファを下げて光として足す。
Shader "MRP/NovaBeam"
{
    Properties
    {
        _MainTex ("Sprite", 2D) = "white" {}
        _Noise ("Noise", 2D) = "gray" {}
        _Len ("Length", Float) = 10
        _Half ("Half width", Float) = 1
        _Core ("Core half width", Float) = 0.2
        _Back ("Behind the apex", Float) = 0.2
        _Lit ("Light side", Float) = 0.9
        _R0r ("Ramp 0 r", Float) = 0.006
        _R0g ("Ramp 0 g", Float) = 0.006
        _R0b ("Ramp 0 b", Float) = 0.008
        _R1r ("Ramp 1 r", Float) = 0.07
        _R1g ("Ramp 1 g", Float) = 0.07
        _R1b ("Ramp 1 b", Float) = 0.075
        _R2r ("Ramp 2 r", Float) = 0.38
        _R2g ("Ramp 2 g", Float) = 0.38
        _R2b ("Ramp 2 b", Float) = 0.4
        _R3r ("Ramp 3 r", Float) = 0.8
        _R3g ("Ramp 3 g", Float) = 0.8
        _R3b ("Ramp 3 b", Float) = 0.82
        _R4r ("Ramp 4 r", Float) = 1
        _R4g ("Ramp 4 g", Float) = 1
        _R4b ("Ramp 4 b", Float) = 1
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
            float _Len, _Half, _Core, _Back, _Lit;
            float _R0r, _R0g, _R0b, _R1r, _R1g, _R1b, _R2r, _R2g, _R2b, _R3r, _R3g, _R3b, _R4r, _R4g, _R4b;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            // 明るさ (0〜1) → 色。最も暗い地 → 暗いにじみ → 本体 → 明るい筋 → 芯
            float3 Ramp(float t)
            {
                float3 c = lerp(float3(_R0r, _R0g, _R0b), float3(_R1r, _R1g, _R1b), saturate(t * 4.0));
                c = lerp(c, float3(_R2r, _R2g, _R2b), saturate(t * 4.0 - 1.0));
                c = lerp(c, float3(_R3r, _R3g, _R3b), saturate(t * 4.0 - 2.0));
                return lerp(c, float3(_R4r, _R4g, _R4b), saturate(t * 4.0 - 3.0));
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float grow = i.color.r, heat = i.color.g, floorShadow = i.color.b, alpha = i.color.a;
                float t = _Time.y;
                float u = i.uv.x;
                float x = u * _Len - _Back;                      // 頂点からの距離 (世界)。板は頂点の少し後ろから始まる
                float y = (i.uv.y - 0.5) * 2.0 * 1.3 * _Half;    // 芯の線からの横の距離 (世界)
                float core = max(_Core, 0.03);

                // 輪郭: 頂点 (x = 0) の 1 点から弾丸形に開く。幅 = 太さ × tanh(x / (0.85 × 太さ)) で、先へ行くほど一定に近づく。縁はノイズで揺らぐ
                float wnow = core + (_Half - core) * grow;
                float hw = wnow * tanh(max(x, 0.0) / (0.85 * wnow));
                hw = max(hw, 0.001);
                float wob = tex2D(_Noise, float2(x * 0.18 - t * 2.6, (y > 0 ? 0.25 : 0.75) + t * 0.2)).r - 0.5;
                hw *= 1.0 + wob * 0.3;
                float a1 = clamp(y / hw, -1.0, 1.0);
                float d = abs(y) / hw;                           // 0 = 芯の線・1 = 縁

                // 円柱の断面: 弦の長さ (奥行き) と、表面に巻いた横の位置 (縁ほど詰まる)
                float chord = sqrt(saturate(1.0 - a1 * a1));
                float wrap = asin(a1) * 0.6366;                  // -1〜1

                // 縁は煙のようにちぎれて溶ける
                float er = tex2D(_Noise, float2(x * 0.35 - t * 4.5, wrap * 0.7 + 0.4)).r;
                float body = 1.0 - smoothstep(0.7 + 0.18 * er, 1.0, d);

                // 長さ方向に長く伸びて流れる筋 (横は表面に巻いた位置で読む)。太い筋がゆっくり・細い筋が速く
                float s1 = tex2D(_Noise, float2(x * 0.035 - t * 1.6, wrap * 0.55 + 0.13)).r;
                float s2 = tex2D(_Noise, float2(x * 0.12 - t * 4.8 + s1 * 0.4, wrap * 1.5 + 0.71)).r;
                float fil = saturate(s1 * 0.6 + s2 * 0.7 - 0.43);
                fil = fil * fil * 5.0;
                // 細い髪のような筋: 速く流れ、太い筋の明るい所に多い
                float s3 = tex2D(_Noise, float2(x * 0.32 - t * 9.0 + s2 * 0.6, wrap * 3.2 + 0.27)).r;
                float hair = saturate(s3 * 2.2 - 1.12);
                hair = hair * hair * 6.0 * (0.3 + 0.7 * saturate(s2 * 2.0 - 0.6));
                // 濃い煙のかたまり: ここでは筋も地も沈む
                float veil = tex2D(_Noise, float2(x * 0.06 - t * 2.2 + s1 * 0.3, wrap * 0.9 + 0.52)).r;
                float murk = 0.35 + 0.65 * smoothstep(0.3, 0.75, veil);
                // 芯の帯: 真ん中ほど明るいが、ちぎれてまだら
                float band = exp(-a1 * a1 * 7.0) * (0.35 + 0.65 * s1);

                // 左上からの光: 当たる側の縁が明るく、反対側は暗く沈む
                float lam = saturate(a1 * _Lit * 0.75 + chord * 0.55);
                float rim = pow(saturate(a1 * _Lit), 3.0) * body;
                float flick = 0.72 + 0.28 * tex2D(_Noise, float2(t * 9.0, 0.37)).r;

                float glow = ((fil + hair) * (0.45 + 0.55 * chord) * murk + band * 0.45 * murk) * (0.55 + 0.6 * lam) + rim * 0.4;
                float lev = saturate(0.03 * chord * (0.6 + 0.8 * lam) * murk + glow * 0.72) * heat * flick;
                // 頂点の光 (ここから開く)
                float tip = exp(-(x * x + y * y) / (core * core * 1.2));
                lev = saturate(lev + tip * heat);

                // 濃さ: 奥行きのある真ん中ほど濃いガス + 外の薄い煙
                float edge = 1.0 - smoothstep(0.78, 0.98, abs(i.uv.y - 0.5) * 2.0);
                float smoke = exp(-max(abs(y) - hw, 0.0) / (0.12 * wnow)) * (1.0 - body) * 0.45 * edge;
                float dens = saturate(body * (0.92 + 0.08 * chord) + smoke + tip);
                float a = dens * alpha;
                // 先はすぐ壁 (着弾の光が隠す)。板の始まり (頂点の後ろ) では頂点の光も必ず 0 まで落とす
                a *= 1.0 - smoothstep(0.985, 1.0, u);
                a *= smoothstep(-_Back, -_Back * 0.2, x);

                float3 col = Ramp(lev);
                // 床: 柱の真下に落ちる暗い影と、筋の光の淡い照り返し
                if (floorShadow > 0.5)
                    return fixed4(col * a * lev * 0.8, a * 0.55);
                return fixed4(col * a * (1.0 + lev * 0.6), a * (1.0 - 0.4 * lev));
            }
            ENDCG
        }
    }
}
