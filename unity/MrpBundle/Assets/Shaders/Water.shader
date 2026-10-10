// 水 (床の水たまり・噴き出し) を描くシェーダ。CPU はスプライトの絵に「水の量」だけを書き、見た目はここで決める。
// 絵は双線形で引くので、粗い升の値でも縁はなめらかな線で切れる (fwidth で 1 画素ぶんぼかす)。
//
// _Mode = 0 (水たまり): R = 深さ (1 = _DeepAt)・G = 見せてよいか (壁の中・家具の上 = 0)・B = 濡れた升をならした値 (_WetLevel の線が水の縁)。_FloorMask = 床の画素 (0.5 の線が床の縁)。
//   深いほど濃く不透明・浅いほど床が透ける。縁の内側に細い濃い輪郭とその内に明るい縁 (表面張力の盛り上がり)。
//   水面の揺れ = ノイズ 2 枚を逆向きに流して、その傾きで光の照り返し (きらめき) と浅い所の明暗 (光の網) を出す。
// _Mode = 1 (噴き出し): R = 水の濃さ (粒が通った量)・G = 泡 (空気を含んで白い)。
//   _Flow (xy = 流れの向き) に沿って伸びる筋を流し、筋の明るい所と泡は白、それ以外は透けた水色。
Shader "MRP/Water"
{
    Properties
    {
        [PerRendererData] _MainTex ("Water data", 2D) = "black" {}
        _Noise ("Noise", 2D) = "gray" {}
        _ShadowOnly ("Only where the view shadow is (copy in front of the shadow)", Float) = 0
        _ShadowGain ("Opacity in the shadow", Float) = 1
        _Mode ("Mode (0 puddle / 1 spray)", Float) = 0
        _Edge ("Edge level", Float) = 0.05
        _WetLevel ("Puddle edge on the wet coverage (B)", Float) = 0.35
        [PerRendererData] _FloorMask ("Floor mask", 2D) = "white" {}
        [PerRendererData] _FloorOn ("Floor mask on", Float) = 0
        _Shallow ("Shallow color", Color) = (0.45, 0.72, 0.95, 0.38)
        _Deep ("Deep color", Color) = (0.16, 0.42, 0.78, 0.78)
        _DeepRange ("Depth to deep", Float) = 0.6
        _Outline ("Outline color", Color) = (0.11, 0.27, 0.5, 0.9)
        _OutlinePx ("Outline width (px)", Float) = 1.6
        _Rim ("Rim color", Color) = (0.86, 0.95, 1.0, 0.85)
        _RimPx ("Rim width (px)", Float) = 2.5
        _Flow ("Flow dir (spray)", Vector) = (1, 0, 0, 0)
        _SprayEdge ("Spray edge level", Float) = 0.3
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" "CanUseSpriteAtlas" = "False" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _Noise;
            sampler2D _FloorMask; // 部屋の絵の床の画素 (タイルと同じ範囲・1 = 床)
            float4 _MainTex_TexelSize, _FloorMask_TexelSize;
            float _FloorOn;
            sampler2D _MrpShadowTex; // 影のカメラの描き先 (視界の所はアルファ 0)
            float _ShadowOnly, _ShadowGain;
            float _MrpShadowOn; // 影の板が出ている間だけ 1
            float _Mode, _Edge, _WetLevel, _DeepRange, _OutlinePx, _RimPx, _SprayEdge;
            fixed4 _Shallow, _Deep, _Outline, _Rim;
            float4 _Flow;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 world : TEXCOORD1; fixed4 color : COLOR; float4 screen : TEXCOORD2; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.world = mul(unity_ObjectToWorld, v.vertex).xy;
                o.color = v.color;
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            // 水たまりのタイルの絵は端の画素がタイルの境目ちょうどの値 (隣のタイルの端と同じ値)。
            // 端の画素の真ん中から真ん中までを引くので、境目で隣と値がつながり縁に段が出ない
            float2 EdgeUv(float2 uv, float4 texel)
            {
                return uv * (1.0 - texel.xy) + texel.xy * 0.5;
            }

            // 水面の高さ (ノイズ 2 枚を逆向きに流す)
            float Surface(float2 w)
            {
                float t = _Time.y;
                float a = tex2D(_Noise, w * 0.22 + t * float2(0.018, 0.011)).r;
                float b = tex2D(_Noise, w * 0.5 - t * float2(0.014, 0.022)).r;
                return a * 0.65 + b * 0.35;
            }

            fixed4 Puddle(v2f i, float4 data)
            {
                float d = data.r;
                float wv = data.b - _WetLevel;                    // 濡れた升をならした値 (深さの低い線は升の形の階段になる)
                float inside = wv / max(fwidth(wv), 1e-5);       // 縁から内側へ何画素か
                float vis = smoothstep(0.35, 0.65, data.g);
                // 床マスクの縁 (家具・壁の絵の輪郭) から内側へ何画素か。マスクはならしてあるので、
                // 半分の値の線を画面の画素の幅で切ると拡大しても升目の段が出ない
                float wet = inside;
                if (_FloorOn > 0.5)
                {
                    float m = tex2D(_FloorMask, EdgeUv(i.uv, _FloorMask_TexelSize)).r - 0.5;
                    wet = min(inside, m / max(fwidth(m), 1e-4));
                }
                float body = saturate(wet + 0.5);                 // 縁を 1 画素でぼかす
                if (body * vis <= 0.001) return 0;

                float k = saturate((d - _Edge) / _DeepRange);
                fixed4 c = lerp(_Shallow, _Deep, k);

                // 水面の傾き → 照り返しと光の網
                float e = 0.12;
                float h0 = Surface(i.world);
                float hx = Surface(i.world + float2(e, 0)) - h0;
                float hy = Surface(i.world + float2(0, e)) - h0;
                float3 n = normalize(float3(-hx * 6.0, -hy * 6.0, 1.0));
                float3 hv = normalize(float3(-0.45, 0.55, 1.0));  // 左上からの光と真上の目の中間
                float spec = smoothstep(0.86, 0.97, pow(saturate(dot(n, hv)), 24.0));
                float caustic = saturate((h0 - 0.5) * 2.2) * (1.0 - k);
                c.rgb += caustic * 0.1;
                c.rgb = lerp(c.rgb, float3(1, 1, 1), spec * 0.75);
                c.a = saturate(c.a + spec * 0.35);

                // 縁: 外側に細い濃い輪郭・その内に明るい縁
                // 家具・壁に当たった所は絵の黒い輪郭があるので、濃い輪郭は描かず明るい縁だけ付ける
                float rim = saturate(1.0 - (inside - _OutlinePx) / _RimPx) * step(_OutlinePx, inside);
                rim = max(rim, saturate(1.0 - wet / _RimPx) * 0.7);
                c = lerp(c, _Rim, rim * _Rim.a * 0.8);
                if (inside < _OutlinePx) c = _Outline;
                c.a *= body * vis * i.color.a;
                return c;
            }

            fixed4 Spray(v2f i, float4 data)
            {
                float d = data.r;
                float foam = data.g;
                float fw = max(fwidth(d), 1e-5);
                float inside = (d - _SprayEdge) / fw;
                float body = saturate(inside + 0.5);
                if (body <= 0.001) return 0;

                float2 f = normalize(_Flow.xy + 1e-5);
                float2 p = float2(-f.y, f.x);
                float s = dot(i.world, f), t = dot(i.world, p);
                // 流れに沿って伸びた筋を流す (2 枚重ね)
                float n1 = tex2D(_Noise, float2(t * 5.0, s * 0.45 - _Time.y * 2.2)).r;
                float n2 = tex2D(_Noise, float2(t * 9.0 + 0.37, s * 0.8 - _Time.y * 3.1)).r;
                float streak = saturate((n1 * 0.6 + n2 * 0.4 - 0.5) * 3.5);
                float dense = saturate(d);
                // 薄い所は透けた水色・濃い所 (芯) は深い青。筋と泡だけ白
                float3 water = lerp(float3(0.55, 0.82, 1.0), float3(0.3, 0.6, 0.95), saturate(dense * 1.2 - 0.2));
                float white = saturate(foam * 0.85 + streak * 0.55);
                fixed4 c;
                c.rgb = lerp(water, float3(1, 1, 1), white);
                c.a = lerp(0.35, 0.85, saturate(dense * 0.9 + white * 0.3));
                // 縁: 薄い青の線 (水の膜の縁が光を曲げて暗く見える)
                float edge = saturate(1.0 - inside / 1.3);
                c.rgb = lerp(c.rgb, float3(0.3, 0.52, 0.82), edge * 0.8);
                c.a = max(c.a, edge * 0.85);
                c.a *= body * i.color.a;
                return c;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float4 data = tex2D(_MainTex, _Mode < 0.5 ? EdgeUv(i.uv, _MainTex_TexelSize) : i.uv);
                fixed4 c = _Mode < 0.5 ? Puddle(i, data) : Spray(i, data);
                // 影の手前の写し: 影の所だけに出す
                if (_ShadowOnly > 0.5) c.a *= tex2Dlod(_MrpShadowTex, float4(i.screen.xy / i.screen.w, 0, 0)).a * _ShadowGain * _MrpShadowOn;
                return c;
            }
            ENDCG
        }
    }
}
