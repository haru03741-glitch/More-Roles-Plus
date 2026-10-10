// 水 (床の水たまり・噴き出し) を描くシェーダ。CPU はスプライトの絵に「水の量」だけを書き、見た目はここで決める。
// 絵は双線形で引くので、粗い升の値でも縁はなめらかな線で切れる (fwidth で 1 画素ぶんぼかす)。
//
// _Mode = 0 (水たまり): R = 深さ (1 = _RUnits 単位)・G = 見せてよいか (壁の中・家具の上 = 0)・B = 濡れた升をならした値 (_WetLevel の線が水の縁)。_FloorMask = 床の画素 (0.5 の線が床の縁)。
//   深いほど濃く不透明・浅いほど床が透ける。縁の内側に細い濃い輪郭とその内に明るい縁 (表面張力の盛り上がり)。
//   水面の揺れ = ノイズ 2 枚を逆向きに流して、その傾きで光の照り返し (きらめき) と浅い所の明暗 (光の網) を出す。
//   A = 壁の面の上の水の厚さ (単位 × _FaceScale / 255 + 0.5・壁の面でない所 = 0)。斜め上から見た絵では深い水は奥の壁を這い上がって見えるので、
//   床マスクの外 (壁の面) は A の 0.5 の線を水面の線にして、その下を水で覆う。
// _Mode = 2 (体の水面): クルー・小物の手前に置く板。板の原点 = 足元。_Depth = 足元の水の深さ (単位)・_Body = (半幅, 体の高さ, 沈んだ時に足す頭の上, 水面の輪の縦の半径)。
//   板は _Quad (幅, 高さ, 足元の UV の高さ)。体の形は上の角を丸めた縦長の四角で近似し、その中の水面の線より下を周りと同じ水の色で覆う (覆いの厚さ = 線からの深さ)。
//   頭まで沈んだら覆いを _SubMax までにして体を水越しの影として残し、頭の上から泡を昇らせる。
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
        _Mode ("Mode (0 puddle / 1 spray / 2 wade)", Float) = 0
        _WetLevel ("Puddle edge on the wet coverage (B)", Float) = 0.35
        [PerRendererData] _FloorMask ("Floor mask", 2D) = "white" {}
        [PerRendererData] _FloorOn ("Floor mask on", Float) = 0
        _Shallow ("Shallow color", Color) = (0.45, 0.72, 0.95, 0.38)
        _Deep ("Deep color", Color) = (0.16, 0.42, 0.78, 0.78)
        _Outline ("Outline color", Color) = (0.11, 0.27, 0.5, 0.9)
        _OutlinePx ("Outline width (px)", Float) = 1.6
        _Rim ("Rim color", Color) = (0.86, 0.95, 1.0, 0.85)
        _RimPx ("Rim width (px)", Float) = 2.5
        _Flow ("Flow dir (spray)", Vector) = (1, 0, 0, 0)
        _SprayEdge ("Spray edge level", Float) = 0.3
        _FaceScale ("Face water thickness scale (A)", Float) = 96
        _RUnits ("Depth (units) at R = 1", Float) = 1.5625
        _ShallowDepth ("Depth (units) to the deep color", Float) = 0.35
        _Fog ("Murk per unit of depth", Float) = 1.3
        _Abyss ("Deep murk color", Color) = (0.05, 0.22, 0.38, 0.95)
        _Caustic ("Caustic net strength", Float) = 0.18
        _LineWave ("Wall waterline wave (units)", Float) = 0.05
        _FaceView ("Extra thickness seen on the wall face (units)", Float) = 0.1
        _FoamW ("Foam band under the wall waterline (units)", Float) = 0.06
        _Shaft ("Light shafts when deep", Float) = 1.0
        [PerRendererData] _Depth ("Water depth at the feet (units, wade)", Float) = 0
        _Body ("Half width, height, extra top when sunk, ring radius (wade)", Vector) = (0.25, 0.7, 0, 0.1)
        _SubMax ("Max cover over a sunk body (wade)", Float) = 0.72
        _Quad ("Board width, height, feet from the bottom (0..1) (wade)", Vector) = (0.84, 1.75, 0.0857, 0)
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
            float _Mode, _WetLevel, _OutlinePx, _RimPx, _SprayEdge, _FaceScale;
            float _RUnits, _ShallowDepth, _Fog, _Caustic, _LineWave, _FaceView, _FoamW, _Shaft;
            fixed4 _Shallow, _Deep, _Outline, _Rim, _Abyss;
            float4 _Flow;
            float _Depth, _SubMax;
            float4 _Body, _Quad;

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

            // 深さ du (単位) の水の色: 深いほど濃く底がかすむ・水面の照り返し・光の網・深い時の光の筋
            fixed4 Tint(float2 w, float du, out float murk)
            {
                float k = saturate((du - 0.03) / _ShallowDepth);
                fixed4 c = lerp(_Shallow, _Deep, k);
                // 深い水は底がかすむ: 暗い青緑へ寄せて不透明にする (浅い水たまりは変えない)
                murk = 1.0 - exp(-max(du - 0.12, 0.0) * _Fog);
                c.rgb = lerp(c.rgb, _Abyss.rgb, murk * 0.85);
                c.a = lerp(c.a, _Abyss.a, murk);

                // 水面の傾き → 照り返しと光の網
                float e = 0.12;
                float h0 = Surface(w);
                float hx = Surface(w + float2(e, 0)) - h0;
                float hy = Surface(w + float2(0, e)) - h0;
                float3 n = normalize(float3(-hx * 6.0, -hy * 6.0, 1.0));
                float3 hv = normalize(float3(-0.45, 0.55, 1.0));  // 左上からの光と真上の目の中間
                float spec = smoothstep(0.86, 0.97, pow(saturate(dot(n, hv)), 24.0));
                float caustic = saturate((h0 - 0.5) * 2.2) * (1.0 - k);
                c.rgb += caustic * 0.1;
                // 光の網: 逆向きに流れる 2 枚の模様の差が 0 に近い所を細い筋にする。深くなると底まで届かず薄れる
                float t = _Time.y;
                float c1 = tex2D(_Noise, w * 0.45 + t * float2(0.031, 0.019)).r;
                float c2 = tex2D(_Noise, w * 0.7 - t * float2(0.022, 0.037)).r;
                float net = pow(saturate(1.0 - abs(c1 - c2) * 7.0), 3.0);
                c.rgb += net * _Caustic * smoothstep(0.06, 0.25, du) * (1.0 - murk * 0.7) * float3(0.6, 0.88, 1.0);
                // 頭まで沈む深さ: 水面から斜めに差し込む光の筋
                float sp = dot(w, float2(0.94, -0.33));
                float ray = tex2D(_Noise, float2(sp * 0.6 + t * 0.015, t * 0.008)).r;
                c.rgb += smoothstep(0.55, 0.85, ray) * smoothstep(0.7, 1.2, du) * _Shaft * 0.14 * float3(0.7, 0.95, 1.0);
                c.rgb = lerp(c.rgb, float3(1, 1, 1), spec * 0.75);
                c.a = saturate(c.a + spec * 0.35);
                return c;
            }

            fixed4 Puddle(v2f i, float4 data)
            {
                float du = data.r * _RUnits;                      // 深さ (単位)
                float wv = data.b - _WetLevel;                    // 濡れた升をならした値 (深さの低い線は升の形の階段になる)
                float inside = wv / max(fwidth(wv), 1e-5);       // 縁から内側へ何画素か
                float vis = smoothstep(0.35, 0.65, data.g);
                // 床マスクの縁 (家具・壁の絵の輪郭) から内側へ何画素か。マスクはならしてあるので、
                // 半分の値の線を画面の画素の幅で切ると拡大しても升目の段が出ない
                float wet = inside;
                float faceIn = -1.0, face = 0.0, ex = 0.0;
                if (_FloorOn > 0.5)
                {
                    float m = tex2D(_FloorMask, EdgeUv(i.uv, _FloorMask_TexelSize)).r - 0.5;
                    float mIn = m / max(fwidth(m), 1e-4);
                    wet = min(inside, mIn);
                    // 壁の面: 水面の線 (A の 0.5) より下で、床の外
                    // 水面は揺れているので、壁に当たる線も上下に波打つ
                    float wave = tex2D(_Noise, float2(i.world.x * 0.35 + _Time.y * 0.04, _Time.y * 0.025)).r - 0.5;
                    ex = (data.a - 0.5) * 255.0 / _FaceScale + wave * _LineWave * 2.0;
                    faceIn = ex / max(fwidth(ex), 1e-4);
                    face = data.a > 0.0 ? saturate(faceIn + 0.5) * saturate(0.5 - mIn) : 0.0;
                    // 壁の手前の水は斜めに見通すので、同じ深さの床より厚く見える
                    du = lerp(du, ex + _FaceView, face);
                }
                float body = max(saturate(wet + 0.5), face);      // 縁を 1 画素でぼかす
                if (body * vis <= 0.001) return 0;

                float murk, t = _Time.y;
                fixed4 c = Tint(i.world, du, murk);

                // 縁: 外側に細い濃い輪郭・その内に明るい縁
                // 家具・壁に当たった所は絵の黒い輪郭があるので、濃い輪郭は描かず明るい縁だけ付ける
                float rim = saturate(1.0 - (inside - _OutlinePx) / _RimPx) * step(_OutlinePx, inside);
                rim = max(rim, saturate(1.0 - wet / _RimPx) * 0.7);
                rim *= 1.0 - face;                                // 壁の面の上は床の縁の線を引かない
                c = lerp(c, _Rim, rim * _Rim.a * 0.8);
                if (inside < _OutlinePx && face < 0.5) c = _Outline;
                // 壁に這い上がった水面の線: 明るい縁と、その下のちぎれた泡の帯
                float lip = saturate(1.0 - faceIn / _RimPx) * face;
                float fn = tex2D(_Noise, float2(i.world.x * 2.2 + t * 0.12, ex * 6.0 - t * 0.05)).r;
                float foam = saturate(1.0 - ex / _FoamW) * smoothstep(0.42, 0.62, fn) * face;
                c.rgb = lerp(c.rgb, float3(0.92, 0.97, 1.0), foam * 0.75);
                c.a = max(c.a, foam * 0.9);
                c = lerp(c, _Rim, lip * _Rim.a);
                c.a *= body * vis * i.color.a;
                return c;
            }

            fixed4 Wade(v2f i)
            {
                // 絵は束ねて描かれると物体の行列が無くなるので、足元からの位置は板の UV から出す
                float2 p = (i.uv - float2(0.5, _Quad.z)) * _Quad.xy;
                float d = _Depth, t = _Time.y;
                float W = _Body.x, top = _Body.y, ry = _Body.w;
                float sub = saturate((d - top) / 0.08);          // 頭まで沈んだか
                float H = top + _Body.z * sub;
                // 体の形: 上の角を大きく丸めた縦長の四角 (足元 0〜H・幅 ±W)。足元の角は小さく丸める
                float r = p.y > H * 0.5 ? W * 0.9 : 0.05;
                float2 e = abs(p - float2(0.0, H * 0.5)) - (float2(W, H * 0.5) - r);
                float sd = length(max(e, 0.0)) + min(max(e.x, e.y), 0.0) - r;
                float px = max(fwidth(p.y), 1e-4);
                float sil = saturate(0.5 - sd / (px * 1.5));
                // 形は体の近似なので、外周は少しずつ薄くして体の外に出た所が水の二重の四角に見えないようにする
                float fade = saturate(-sd / 0.05);
                // 体に当たる水面の線: 体の手前ほど画面で下に来るので、真ん中が輪の縦の半径ぶん下がる
                float xr = saturate(abs(p.x) / (W + 0.08));
                float wave = (tex2D(_Noise, float2(i.world.x * 0.9 + t * 0.05, t * 0.03)).r - 0.5) * _LineWave * 2.0;
                float ex = d - ry * sqrt(1.0 - xr * xr) + wave - p.y;   // 水面の線から下へ何単位か
                float under = saturate(ex / px + 0.5);
                float murk;
                fixed4 c = Tint(i.world, max(ex, 0.0) + _FaceView, murk);
                c.a = min(c.a, _SubMax);
                // 水面の線の明るい縁と、その下のちぎれた泡の帯
                float lip = saturate(1.0 - abs(ex) / (px * _RimPx)) * (1.0 - sub);
                float fn = tex2D(_Noise, float2(i.world.x * 2.2 + t * 0.12, ex * 6.0 - t * 0.05)).r;
                float foam = saturate(1.0 - ex / _FoamW) * smoothstep(0.42, 0.62, fn) * (1.0 - sub) * under;
                c.rgb = lerp(c.rgb, float3(0.92, 0.97, 1.0), foam * 0.75);
                c.a = max(c.a, foam * 0.9);
                c = lerp(c, _Rim, lip * _Rim.a);
                c.a *= fade * max(under, lip);
                // 体のまわりの水面の輪 (後ろ半分は体に隠れる)。泡でちぎれ、ゆっくり回る
                float2 rr = (p - float2(0.0, d)) / float2(W + 0.1, ry + 0.04);
                float re = length(rr) - 1.0;
                float ring = saturate(1.0 - abs(re) / max(fwidth(re), 1e-4) / 1.6);
                float rn = tex2D(_Noise, float2(atan2(rr.y, rr.x) * 0.5 + t * 0.06, t * 0.05 + d)).r;
                ring *= smoothstep(0.32, 0.55, rn) * (1.0 - sub) * step(0.03, d) * (rr.y < 0.0 ? 1.0 : 1.0 - sil);
                c.rgb = lerp(c.rgb, float3(0.92, 0.97, 1.0), ring);
                c.a = max(c.a, ring * 0.85);
                // 沈んだ体から昇る泡
                float bub = 0.0;
                for (int k = 0; k < 3; k++)
                {
                    float sp = 0.45 + k * 0.13, ph = t * sp + k * 0.37, f = frac(ph);
                    float rnd = frac(sin(floor(ph) * 7.13 + k * 12.9) * 43758.5);
                    float2 bc = float2((rnd - 0.5) * W * 1.2 + sin(t * 3.0 + k * 2.1) * 0.03, top * 0.85 + f * (d - top * 0.85));
                    float br = 0.022 + 0.012 * k;
                    float bd = length(p - bc);
                    float o = saturate(1.0 - abs(bd - br) / (px * 1.3)) + saturate(1.0 - length(p - bc - br * float2(-0.35, 0.35)) / (px * 1.5));
                    bub = max(bub, saturate(o) * (1.0 - f * 0.5));
                }
                bub *= sub;
                c.rgb = lerp(c.rgb, float3(0.9, 0.97, 1.0), bub);
                c.a = max(c.a, bub * 0.8);
                c.a *= i.color.a;
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
                fixed4 c;
                if (_Mode > 1.5) c = Wade(i);
                else
                {
                    float4 data = tex2D(_MainTex, _Mode < 0.5 ? EdgeUv(i.uv, _MainTex_TexelSize) : i.uv);
                    c = _Mode < 0.5 ? Puddle(i, data) : Spray(i, data);
                }
                // 影の手前の写し: 影の所だけに出す
                if (_ShadowOnly > 0.5) c.a *= tex2Dlod(_MrpShadowTex, float4(i.screen.xy / i.screen.w, 0, 0)).a * _ShadowGain * _MrpShadowOn;
                return c;
            }
            ENDCG
        }
    }
}
