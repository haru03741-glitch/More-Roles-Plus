// 船の部屋の絵 (SpriteRenderer) を描くシェーダ。描き方はゲームの Unlit/MaskShader と同じ
// (通常の半透明合成・深度書き込みあり・ステンシルへ _MaskLayer を書く → 影の板 Unlit/ShadowShader はその上にだけ影を落とす)。
// 加えて世界全体に敷いた「損傷マスク」(_MrpDamageTex) を世界座標で引き、壊れた所を描かずに抜き、焦げを乗せる。
//   マスクの R = 穴 (0..1 のなだらかな値。ノイズを足した閾値で切るので縁がぎざぎざになる)
//   マスクの G = 焦げの濃さ (下の色に掛ける) / B = 切り口の印 (残った壁の端の断面。0.55 以上は爆発の熱い切り口)
// マスクの置き場所は _MrpDamageRect (xy = 世界座標の左下、zw = 1 / 幅と高さ) で全マテリアル共通。
// _MrpGenTex (同じ置き場所・点サンプリング) = その画素を最後に抜いた破壊の番号。割れた塊は自分の番号の所だけ描く。
// 合成の係数はマテリアルで変えられる (既定 = 通常の半透明合成。影の写しの型抜きだけが別の係数を使う)。
// _MrpPieceSites (行 0..255 = 破壊の番号・256..511 = 剥げかけの枠・列 = 種点) = 割れ目の種点 (放射状)。割れた塊は自分の種点がいちばん近い所だけ描く。
// 7 = 持ち上げた家具 / 8 = その跡の床: 部屋の絵に描き込まれた家具を、形 (_ShapeTex) と、その場所に本来ある床の模様
//   (_FloorPatch のきれいな床を繰り返した色) との違いで切り分ける。
Shader "MRP/TerrainSprite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _MaskLayer ("Stencil Ref", Float) = 1
        _MaskComp ("Stencil Comp", Float) = 8
        _StencilPass ("Stencil Pass", Float) = 2
        _Noise ("Edge Noise", 2D) = "gray" {}
        _Cells ("Break Cells", 2D) = "gray" {}
        _EdgeJag ("Edge jaggedness", Float) = 0.35
        _ScorchColor ("Scorch color", Color) = (0.08, 0.06, 0.05, 1)
        _EmberColor ("Ember rim color", Color) = (1.0, 0.45, 0.12, 1)
        _OutlineColor ("Break outline color", Color) = (0.086, 0.086, 0.094, 1)
        _UseDamage ("Use damage mask", Float) = 1
        _PieceMap ("Piece uv to world (xy scale, zw offset)", Vector) = (0,0,0,0)
        _PieceLine ("Piece outline width", Float) = 0.022
        _CrackLine ("Crack line half width", Float) = 0.0045
        _RecessColor ("Exposed wall inside color", Color) = (0.36, 0.35, 0.39, 1)
        _ShapeTex ("Lifted furniture shape", 2D) = "black" {}
        _ShapeRect ("Shape rect (xy min world, zw 1/size)", Vector) = (0,0,0,0)
        _FloorPatch ("Clean floor (xy min world, zw period x/y; 0 = same column/row)", Vector) = (0,0,1,1)
        _FloorTol ("Floor difference tolerance", Float) = 0.05
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 10
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendA ("Src blend alpha", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendA ("Dst blend alpha", Float) = 10
        [HideInInspector] _AlphaTex ("External Alpha", 2D) = "white" {}
        [HideInInspector] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" "CanUseSpriteAtlas" = "True" }

        Stencil
        {
            Ref [_MaskLayer]
            Comp [_MaskComp]
            Pass [_StencilPass]
        }

        Cull Off
        Lighting Off
        ZWrite On
        Blend [_SrcBlend] [_DstBlend], [_SrcBlendA] [_DstBlendA]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local _ ETC1_EXTERNAL_ALPHA
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            sampler2D _AlphaTex;
            float _EnableExternalAlpha;
            fixed4 _Color;
            sampler2D _Noise;
            sampler2D _Cells;
            float _EdgeJag;
            fixed4 _ScorchColor;
            fixed4 _EmberColor;
            float _UseDamage;
            fixed4 _OutlineColor;

            sampler2D _MrpDamageTex;
            sampler2D _MrpGenTex;
            float4 _MrpDamageRect;
            float4 _PieceMap;
            sampler2D_float _MrpPieceSites;
            float _PieceLine;
            float _CrackLine;
            fixed4 _RecessColor;
            sampler2D _ShapeTex;
            float4 _ShapeRect;
            float4 _FloorPatch;
            float _FloorTol;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; float2 world : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                o.world = mul(unity_ObjectToWorld, v.vertex).xy;
                return o;
            }

            // 種点 idx (破壊の番号 row の行) の世界座標。2 バイトずつの 1/256 単位 (損傷マスクの原点から)
            float2 SiteAt(float idx, float row)
            {
                float4 b = round(tex2Dlod(_MrpPieceSites, float4((idx + 0.5) / 64, (row + 0.5) / 512, 0, 0)) * 255);
                return _MrpDamageRect.xy + float2(b.r * 256 + b.g, b.b * 256 + b.a) / 256;
            }

            // 塊の種点 k の細胞からのはみ出し: 他の種点との二等分線への符号付き距離の最大 (0 以下 = 中・0 に近いほど縁)
            float CellExcess(float2 ow, float k, float row)
            {
                float2 sk = SiteAt(k, row);
                float m = -1000;
                [loop] for (int j = 0; j < 48; j++)
                {
                    if (abs(j - k) < 0.5) continue;
                    float2 dv = SiteAt(j, row) - sk;
                    m = max(m, (dot(ow - sk, dv) - dot(dv, dv) * 0.5) / max(length(dv), 1e-4));
                }
                return m;
            }

            // いちばん近い種点の番号
            float NearestSite(float2 ow, float row)
            {
                float best = 1e8, k = 0;
                [loop] for (int j = 0; j < 48; j++)
                {
                    float2 dv = ow - SiteAt(j, row);
                    float d = dot(dv, dv);
                    if (d < best) { best = d; k = j; }
                }
                return k;
            }

            // 割れた塊の中か: 元の場所 (ow) が「部屋の絵が抜いた所」(部屋と同じ判定) で、その画素を抜いたのが
            // この塊の破壊 (key.g) (細胞の中かは CellExcess)
            bool InPiece(float2 ow, float4 key)
            {
                float2 muv = (ow - _MrpDamageRect.xy) * _MrpDamageRect.zw;
                float dr = tex2D(_MrpDamageTex, muv).r;
                float cl = tex2D(_Cells, ow * 0.4).r;
                float nn = tex2D(_Noise, ow * 3.1).r;
                float hv = dr + (cl - 0.5) * _EdgeJag + (nn - 0.5) * 0.06;
                float gen = tex2D(_MrpGenTex, muv).r;
                return hv >= 0.5 && abs(gen - key.g) < 0.5 / 255;
            }

            // 焦げ: 下の色に掛けて暗くするだけ (暗い部屋では焦げも暗い・床の模様が透ける)。
            // 縁は大小 2 つのノイズで不規則に (細胞で段を付けると多角形の黒い穴の連なりに見えた)・段は 3 つ (一番濃い段 = 燃え尽きた炭)
            fixed ScorchMul(float g, float2 w)
            {
                float s = g + (tex2D(_Noise, w * 0.9).r - 0.5) * 0.35 + (tex2D(_Noise, w * 3.1).r - 0.5) * 0.1;
                return s > 0.92 ? 0.42 : s > 0.72 ? 0.64 : s > 0.45 ? 0.85 : 1.0;
            }

            // その場所に本来ある床 (きれいな床を模様の繰り返しの幅ごとに写した色)
            // (_FloorPatch.zw = 横と縦の繰り返しの幅。0 の向きは同じ列/行をそのまま引く = その向きには模様が変わらない床)
            fixed3 FloorAt(float2 ow)
            {
                float2 t = max(_FloorPatch.zw, 1e-5);
                float2 rep = _FloorPatch.xy + frac((ow - _FloorPatch.xy) / t) * t;
                float2 fw = float2(_FloorPatch.z > 0 ? rep.x : ow.x, _FloorPatch.w > 0 ? rep.y : ow.y);
                return tex2D(_MainTex, (fw - _PieceMap.zw) / _PieceMap.xy).rgb;
            }

            // 部屋の絵の uv の画素が、その場所の床のままか (家具・家具の落とす薄い影は床でない)
            bool IsFloorAt(float2 uv)
            {
                fixed4 c = tex2D(_MainTex, uv);
                if (c.a < 0.5) return true;
                float3 d = abs(c.rgb - FloorAt(uv * _PieceMap.xy + _PieceMap.zw));
                return max(d.r, max(d.g, d.b)) < _FloorTol;
            }

            // 持ち上げた家具の形の中か (元の場所の世界座標で)
            bool InShape(float2 ow)
            {
                float2 s = (ow - _ShapeRect.xy) * _ShapeRect.zw;
                return all(s > 0) && all(s < 1) && tex2D(_ShapeTex, s).r > 0.5;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 6 = 損傷の範囲の型抜き (影の写しを焼く時): 損傷マスクに何か書かれた所はアルファ 1・それ以外は 0。
                // マテリアル側の合成 (色はそのまま・アルファは掛け算) で、焼いた絵を損傷の範囲だけ残す
                if (_UseDamage > 5.5 && _UseDamage < 6.5)
                {
                    float2 mw = (i.world - _MrpDamageRect.xy) * _MrpDamageRect.zw;
                    if (_MrpDamageRect.z <= 0 || any(mw <= 0) || any(mw >= 1)) return fixed4(0, 0, 0, 0);
                    fixed4 dm = tex2D(_MrpDamageTex, mw);
                    return fixed4(0, 0, 0, max(dm.r, dm.g) > 0.01 ? 1 : 0);
                }

                fixed4 c = tex2D(_MainTex, i.uv);
            #if ETC1_EXTERNAL_ALPHA
                fixed4 a = tex2D(_AlphaTex, i.uv);
                c.a = lerp(c.a, a.r, _EnableExternalAlpha);
            #endif
                // 8 = 持ち上げた家具の跡の床: 元の場所で、家具 (床の色でない画素) の所にだけ、
                // 同じ部屋の絵のきれいな床を模様の繰り返しの幅ごとに写して描く
                // 家具の縁の中間色が輪に残らないように、1 画素隣が家具でも塗る
                if (_UseDamage > 7.5)
                {
                    float2 ow = i.uv * _PieceMap.xy + _PieceMap.zw;
                    if (c.a < 0.5 || !InShape(ow)) discard;
                    float2 t = _MainTex_TexelSize.xy;
                    if (IsFloorAt(i.uv) && IsFloorAt(i.uv + float2(t.x, 0)) && IsFloorAt(i.uv - float2(t.x, 0))
                        && IsFloorAt(i.uv + float2(0, t.y)) && IsFloorAt(i.uv - float2(0, t.y))) discard;
                    return fixed4(FloorAt(ow), 1);
                }
                // 7 = 持ち上げた家具: 部屋の絵を元の場所で引き、形の中で床の模様と違う画素だけ描く (落とす影ごと動く)
                if (_UseDamage > 6.5)
                {
                    float2 ow = i.uv * _PieceMap.xy + _PieceMap.zw;
                    if (c.a < 0.5 || !InShape(ow) || IsFloorAt(i.uv)) discard;
                    // 床の模様の手描きのずれ (ひし形の境の細い線) は家具でない: 上下左右の 3 つ以上も床でない画素だけ
                    float2 t = _MainTex_TexelSize.xy;
                    int n = (IsFloorAt(i.uv + float2(t.x, 0)) ? 0 : 1) + (IsFloorAt(i.uv - float2(t.x, 0)) ? 0 : 1)
                          + (IsFloorAt(i.uv + float2(0, t.y)) ? 0 : 1) + (IsFloorAt(i.uv - float2(0, t.y)) ? 0 : 1);
                    if (n < 3) discard;
                    return c;
                }
                // 5 = 剥げかけ (崩れる前の打撃)。頂点色 r = 細胞 (255 = ひびの線だけ)・g = 剥げかけの枠・
                // b, a = 浮いた表面のずれ (0.5 = ずれなし・b = 0 は欠けて断面だけ)。ひびの線は b = 半径
                if (_UseDamage > 4.5)
                {
                    float row = 256 + round(i.color.g * 255);
                    float2 ow = i.uv * _PieceMap.xy + _PieceMap.zw;
                    float kb = round(i.color.r * 255);
                    if (kb > 254.5)
                    {
                        // ひび: 中心の種点から楕円の中だけ、細胞の境に細い線。端はノイズでばらつかせる。
                        // a = 中心の種点の番号 + 64 × 横の伸びの段 (横の半径 = 縦の半径 b × (1 + 0.4 × 段))
                        clip(c.a - 0.5);
                        float av = round(i.color.a * 255);
                        float2 dc = ow - SiteAt(fmod(av, 64), row);
                        dc.x /= 1 + 0.4 * floor(av / 64);
                        float nr = tex2D(_Noise, ow * 2.3).r;
                        if (dot(dc, dc) > i.color.b * i.color.b * (0.55 + nr * 0.6)) discard;
                        float ek = CellExcess(ow, NearestSite(ow, row), row);
                        if (ek < -_CrackLine) discard;
                        return fixed4(_OutlineColor.rgb, 1);
                    }
                    float bx = round(i.color.b * 255);
                    bool missing = bx < 0.5;
                    float2 off = missing ? float2(0, 0) : (float2(bx, round(i.color.a * 255)) / 255 - 0.5) * 0.2;
                    // 浮いた表面: 細胞の絵をずれた所に描く (縁に輪郭線)
                    float el = missing ? 1 : CellExcess(ow - off, kb, row);
                    if (el <= 0)
                    {
                        fixed4 lc = tex2D(_MainTex, i.uv - off / _PieceMap.xy);
                        if (lc.a < 0.5) discard;
                        if (el > -_PieceLine) lc = fixed4(_OutlineColor.rgb, 1);
                        return lc;
                    }
                    // 表面がずれた/欠けた跡: 壁の中の断面。ベタ塗り + 一段の影 (浮いた表面の際・欠けた奥)
                    float eo = CellExcess(ow, kb, row);
                    if (eo > 0 || c.a < 0.5) discard;
                    fixed3 rc = _RecessColor.rgb;
                    bool shade = missing ? eo < -_PieceLine * 3.5 : CellExcess(ow - off * 0.5, kb, row) <= 0;
                    if (shade) rc *= 0.72;
                    if (eo > -_PieceLine) rc = _OutlineColor.rgb;
                    return fixed4(rc, 1);
                }

                // 4 = 割れた塊: 部屋の絵を元の場所で引いた損傷マスクで切り抜く。頂点色は色でなく塊の番号 (r = 種点・g = 破壊の番号)
                if (_UseDamage > 3.5)
                {
                    clip(c.a - 0.004);
                    if (_MrpDamageRect.z <= 0) discard;
                    float2 ow = i.uv * _PieceMap.xy + _PieceMap.zw;
                    float ex = CellExcess(ow, round(i.color.r * 255), round(i.color.g * 255));
                    if (ex > 0 || !InPiece(ow, i.color)) discard;
                    // 塊の縁 (隣の塊・残った壁との境) に本編と同じ濃い輪郭線
                    float d = _PieceLine;
                    bool rim = ex > -d || !InPiece(ow + float2(d, 0), i.color) || !InPiece(ow - float2(d, 0), i.color)
                            || !InPiece(ow + float2(0, d), i.color) || !InPiece(ow - float2(0, d), i.color);
                    // 焦げは部屋の絵と同じ掛け算
                    float2 pm = (ow - _MrpDamageRect.xy) * _MrpDamageRect.zw;
                    c.rgb *= ScorchMul(tex2D(_MrpDamageTex, pm).g, ow);
                    if (rim) c = fixed4(_OutlineColor.rgb, 1);
                    return c;
                }

                c *= i.color;

                // 透明な画素はステンシルも書かない (書くと部屋の外の船体にまで影の板が掛かって明るく浮く)
                clip(c.a - 0.004);

                float2 muv = (i.world - _MrpDamageRect.xy) * _MrpDamageRect.zw;
                bool inMap = _MrpDamageRect.z > 0 && all(muv > 0) && all(muv < 1);

                // 2 = 穴の向こうの床: 抜けた所 (より少し広め) にだけ描き、割れ口の近くほど暗く落として深さを出す
                if (_UseDamage > 1.5)
                {
                    if (!inMap) discard;
                    fixed4 dd = tex2D(_MrpDamageTex, muv);
                    // 宇宙へ抜けた喉 (R と A が両方立った所) は船体の中を描かず、後ろの星空を見せる
                    if (dd.a > 0.5 && dd.r > 0.5) discard;
                    float dr = dd.r;
                    float cl = tex2D(_Cells, i.world * 0.4).r;
                    float nn = tex2D(_Noise, i.world * 3.1).r;
                    float hv = dr + (cl - 0.5) * _EdgeJag + (nn - 0.5) * 0.06;
                    clip(hv - 0.38);
                    // 切り口のすぐ内側は奥まった暗がり (一段)。細胞のずれを入れると暗がりが多角形の斑になるので、マスクの値と弱いノイズだけ
                    c.rgb *= dr + (nn - 0.5) * 0.08 < 0.8 ? 0.62 : 1.0;
                    return c;
                }

                // 3 = ひびの板: 穴の中 (割れ口と同じ判定) と家具の上には描かない
                if (_UseDamage > 2.5)
                {
                    if (inMap)
                    {
                        fixed4 dk = tex2D(_MrpDamageTex, muv);
                        float ck = tex2D(_Cells, i.world * 0.4).r;
                        float nk = tex2D(_Noise, i.world * 3.1).r;
                        clip(0.5 - (dk.r + (ck - 0.5) * _EdgeJag + (nk - 0.5) * 0.06));
                        clip(0.5 - dk.a);
                    }
                    return c;
                }

                if (_UseDamage > 0.5 && inMap)
                {
                    fixed3 dmg = tex2D(_MrpDamageTex, muv).rgb;
                    // 細胞ごとに一定のずれ (角張った欠け) + ごく弱いノイズ (縁のガタつき)
                    float cell = tex2D(_Cells, i.world * 0.4).r;
                    float n = tex2D(_Noise, i.world * 3.1).r;

                    // 穴: なだらかな値を細胞でずらした閾値で切る → 割れた破片の形の縁
                    float hole = dmg.r + (cell - 0.5) * _EdgeJag + (n - 0.5) * 0.06;
                    clip(0.5 - hole);

                    c.rgb *= ScorchMul(dmg.g, i.world);

                    // 割れ口: 本編の絵と同じ濃い輪郭線。残った壁の端 (B = 切り口の印) にはその外側に断面 (壁の一段暗い面) と、
                    // 断面の外の縁にもう 1 本の輪郭線 → 壁に厚みがあるように見せる。爆発の切り口 (B ≥ 0.55) は断面の口側に橙を少し
                    float edge = step(0.05, dmg.r);
                    float outline = step(0.41, hole) * edge;
                    float face = step(0.22, hole) * edge * (1 - outline) * step(0.08, dmg.b);
                    c.rgb *= 1 - 0.42 * face;
                    c.rgb = lerp(c.rgb, _EmberColor.rgb, step(0.36, hole) * face * step(0.55, dmg.b) * 0.55);
                    c.rgb = lerp(c.rgb, _OutlineColor.rgb, max(outline, face * (1 - step(0.25, hole))));
                }

                return c;
            }
            ENDCG
        }
    }
}
