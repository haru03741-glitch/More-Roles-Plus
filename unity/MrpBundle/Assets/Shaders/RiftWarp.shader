// 空間の裂け目: メインカメラが描き上げた画面 (視界の影まで含む) を、斬った線を境に線に沿ってせん断して描き直す。
// 線の左右は逆向きに半分ずつすべり、ずれは線の上だけで切れる。線から離れるほど、線の両端へ近づくほど、ずれは滑らかに 0 へ戻るので、
// 切れ目は斬った線の 1 本だけになる。画面の画素をずらすだけなので、物が二つに写ることはなく、線をまたいだ物だけが分かれて見える。
// 線の絵も一緒にずれるが、線に沿う向きに動くだけなので形は変わらない (_Gap は線の真上をずらさない幅・既定 0)。
// 網は裂け目ごとの四角 (線に沿って 0..長さ、線から左右に幅) で、頂点に次を持たせる:
//   uv0 = (線に沿う位置 t, 線からの符号付きの離れ d・左が正)
//   uv1 = 左側のずれ (線の向き × ずれ幅の半分 × 開き具合・世界の単位)
//   uv2 = (線の長さ, 影響の幅)
// 描く順番は Overlay: メインカメラの他の物 (視界の影の板を含む) を全部描いた後に画面を写す。HUD は別のカメラなのでずれない。
Shader "MRP/RiftWarp"
{
    Properties
    {
        _Gap ("Unshifted half width on the line (world)", Float) = 0
        _TaperMax ("Max end taper (world)", Float) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "IgnoreProjector" = "True" "RenderType" = "Overlay" "PreviewType" = "Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always

        // 名前付き: 裂け目がいくつあっても 1 フレームに 1 回だけ写す
        GrabPass { "_MrpRiftGrab" }

        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MrpRiftGrab;
            float _Gap, _TaperMax;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float2 uv1 : TEXCOORD1; float2 uv2 : TEXCOORD2; };
            struct v2f { float4 pos : SV_POSITION; float2 td : TEXCOORD0; float2 slip : TEXCOORD1; float2 lw : TEXCOORD2; float3 world : TEXCOORD3; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.td = v.uv;
                o.slip = v.uv1;
                o.lw = v.uv2;
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float t = i.td.x, d = i.td.y, len = i.lw.x, w = i.lw.y;
                float ad = abs(d);
                float tp = min(len * 0.3, _TaperMax);
                float taper = smoothstep(0.0, tp, t) * smoothstep(0.0, tp, len - t);
                // 線のそばは塊のまま動き (傾き 0)、幅の外で 0
                float fall = 1.0 - smoothstep(_Gap, w, ad);
                float k = (ad < _Gap ? 0.0 : 1.0) * (d < 0.0 ? -1.0 : 1.0) * taper * fall;
                // 物を +ずれ だけ動かして見せる = その画素には -ずれ の所の景色を描く
                float3 q = i.world - float3(i.slip * k, 0.0);
                float4 clip = mul(UNITY_MATRIX_VP, float4(q, 1.0));
                float4 g = ComputeGrabScreenPos(clip);
                fixed4 c = tex2D(_MrpRiftGrab, g.xy / g.w);
                c.a = 1.0;
                return c;
            }
            ENDCG
        }
    }
}
