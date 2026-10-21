// 水 (床の水たまり・噴き出し) を描くシェーダ。CPU はスプライトの絵に「水の量」だけを書き、見た目はここで決める。
// 絵は双線形で引くので、粗い升の値でも縁はなめらかな線で切れる (fwidth で 1 画素ぶんぼかす)。
//
// _Mode = 0 (水たまり): R = 深さ (1 = _RUnits 単位)・G = 見せてよいか (壁の中・家具の上 = 0)・B = 濡れた升をならした値 (_WetLevel の線が水の縁)。_FloorMask = 床の画素 (0.5 の線が床の縁)。
//   深いほど濃く不透明・浅いほど床が透ける。縁の内側に細い濃い輪郭とその内に明るい縁 (表面張力の盛り上がり)。
//   水面の揺れ = ノイズ 2 枚を逆向きに流して、その傾きで光の照り返し (きらめき) と浅い所の明暗 (光の網) を出す。
//   _FlowTex = 升ごとの流れ (RG = 128 + 速さ / _FlowMax × 127・周りに隣のタイルの升を 1 列)。流れの向きに伸びた明るい筋をその速さで流す (2 つの位相を交互に
//   重ねるので、筋は流れが曲がっても伸び続けずに入れ替わる)。速い所ほど筋が濃く、白く泡立つ。
//   A = 壁の面の上の水の厚さ (単位 × _FaceScale / 255 + 0.5・壁の面でない所 = 0)。斜め上から見た絵では深い水は奥の壁を這い上がって見えるので、
//   床マスクの外 (壁の面) は A の 0.5 の線を水面の線にして、その下を水で覆う。
// _Mode = 2 (体の水面): クルー・小物の手前に置く板。板の原点 = 足元。_Depth = 足元の水の深さ (単位)・_Body = (半幅, 体の高さ, 沈んだ時に足す頭の上, 水面の輪の縦の半径)。
//   板は _Quad (幅, 高さ, 足元の UV の高さ)。体の形は上の角を丸めた縦長の四角で近似し、その中の水面の線より下を周りと同じ水の色で覆う (覆いの厚さ = 線からの深さ)。
//   頭まで沈んだら覆いを _SubMax までにして体を水越しの影として残し、頭の上から泡を昇らせる。
// _Mode = 3 (家具の水面): 家具の絵の手前に、家具と同じ絵 (持ち上げた家具は形の白黒) を型にした板。_SilR = 型を R で見る (1) か A で見る (0)。
//   斜めに見下ろした絵では高さ h の所が床から画面で h × _Rise だけ上に描かれる (本編の絵は高さを縮めて描く)。絵の下の縁を床とし、
//   画素の高さ h = (下からの距離 / _Rise) を天板の高さ _FaceH で頭打ちにする。_FaceH × _Rise より下が前面 (脚・側面)・その上が天板。
//   _Lvl = 床から測った水位で、h < _Lvl の画素を水の厚さ (_Lvl - h) の色で覆う。
//   床の線は列ごと (_BaseTex・_BaseOn) で、無い時は絵全体の一番下 (_VBase)。
//   _Cover = 天板に乗った水がまだらに広がる具合 (0..1)。_VPerUnit = 世界の 1 単位が型の v でいくつか (上下反転なら負)。
//   _RoomOn = 1: 持ち上げた家具の型 (当たり判定を広げた形) のうち、家具の画素だけに描く。床の画素と、当たり判定 (型の G) の外で
//   家具が床に落とす影の画素は床の水が描く (切り抜きの絵と同じ見分け方。型の uv = 元の場所の四角 _ShX.._ShH・床の模様 = _FlX.._FlTy)。
//   _FlatTop = 1: 奥行きのある低い机。絵の縦の広がりは高さでなく奥行きなので、床の画素以外は全部天板の高さ (_FaceH) とし、
//   水が天板を越えるまで描かず、越えたら家具ごとまだらに沈める (下から水面の線を這い上がらせない)。
//   _SilCut > 0: 型をその不透明度で 0/1 に切る (床マスクが床の水を抜いた画素と同じ所だけに描く)。
//   _LvlCols = 1: 水位も列ごと (_BaseTex の G × _LvlMax)・床の線は R と B の 16 ビット。幅の広い手前の絵 (機関・壁の端末の並び) は片側だけ水に浸かる。G = 0 の列は描かない。
// _Mode = 1 (噴き出し・滝): R = 水の濃さ (粒が通った量)・G = 泡 (空気を含んで白い)・B = 水の深さ (1 = 2 単位)・A = 1 - 水たまりの色の割合。
//   水たまりの色の割合の所 (縁を越えたばかりの水) は B の深さの水たまりと同じ色の作りで塗り、割合が下がるほど噴き出しの透けた水色へ移る。その所は縁の線・筋・泡を引かない (水たまりとつながる)。
//   落ちた後も地の色は B の深さの水たまりの色を主にする (水たまりと滝の色がそろう)。噴き出し (A = 1・B = 0) は割合 0。
// 影の中の写し (_ShadowOnly = 1) は影の板の手前にあって家具より手前になるので、_FrontOn の時は _FrontMask (水より手前の家具の絵の形) の所を抜く。
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
        [PerRendererData] _FrontMask ("Furniture in front of the water (shadow copy)", 2D) = "black" {}
        [PerRendererData] _FrontOn ("Front mask on", Float) = 0
        _Shallow ("Shallow color", Color) = (0.45, 0.72, 0.95, 0.38)
        _Deep ("Deep color", Color) = (0.16, 0.42, 0.78, 0.78)
        _Outline ("Outline color", Color) = (0.11, 0.27, 0.5, 0.9)
        _OutlinePx ("Outline width (px)", Float) = 1.6
        _Rim ("Rim color", Color) = (0.86, 0.95, 1.0, 0.85)
        _RimPx ("Rim width (px)", Float) = 2.5
        _Flow ("Flow dir (spray)", Vector) = (1, 0, 0, 0)
        [PerRendererData] _FlowTex ("Current per cell (puddle)", 2D) = "gray" {}
        _FlowMax ("Speed (units/s) at full scale of the current", Float) = 4
        _FlowCycle ("Streak refresh period (s)", Float) = 1.6
        _FlowStreak ("Streak strength", Float) = 0.55
        _FlowFoam ("Foam where fast", Float) = 0.35
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
        [PerRendererData] _Lvl ("Water level above the feet (units, furniture)", Float) = -1
        [PerRendererData] _Cover ("Water spread over the top (furniture)", Float) = 0
        [PerRendererData] _FaceH ("Top height (units, furniture)", Float) = 0.35
        [PerRendererData] _VPerUnit ("Silhouette v per world unit (furniture)", Float) = 0.01
        [PerRendererData] _VBase ("Silhouette v of the floor line (furniture)", Float) = 0
        [PerRendererData] _BaseTex ("Floor line per column (furniture)", 2D) = "black" {}
        [PerRendererData] _BaseU0 ("Floor line columns: u0 (furniture)", Float) = 0
        [PerRendererData] _BaseUS ("Floor line columns: 1/du (furniture)", Float) = 1
        [PerRendererData] _BaseV0 ("Floor line columns: v0 (furniture)", Float) = 0
        [PerRendererData] _BaseVS ("Floor line columns: dv (furniture)", Float) = 1
        [PerRendererData] _BaseOn ("Floor line per column on (furniture)", Float) = 0
        _Rise ("Screen rise per unit of height (furniture)", Float) = 0.6
        [PerRendererData] _SilR ("Silhouette from R instead of A (furniture)", Float) = 0
        [PerRendererData] _FlatTop ("Deep low table: whole art at the top height (furniture)", Float) = 0
        [PerRendererData] _LvlCols ("Water level per column in the floor line G (furniture)", Float) = 0
        [PerRendererData] _LvlMax ("Water level of G = 1 (units, furniture)", Float) = 4
        [PerRendererData] _SilCut ("Silhouette as drawn / not drawn at this alpha (furniture)", Float) = 0
        [PerRendererData] _RoomTex ("Room art under lifted furniture (furniture)", 2D) = "black" {}
        [PerRendererData] _RoomOn ("Draw only on furniture pixels of the room art (furniture)", Float) = 0
        [PerRendererData] _RoomSx ("Room uv to world: scale x", Float) = 1
        [PerRendererData] _RoomSy ("Room uv to world: scale y", Float) = 1
        [PerRendererData] _RoomOx ("Room uv to world: offset x", Float) = 0
        [PerRendererData] _RoomOy ("Room uv to world: offset y", Float) = 0
        [PerRendererData] _RoomDx ("Room pixel size x (world)", Float) = 0.01
        [PerRendererData] _RoomDy ("Room pixel size y (world)", Float) = 0.01
        [PerRendererData] _FlX ("Clean floor min x (world)", Float) = 0
        [PerRendererData] _FlY ("Clean floor min y (world)", Float) = 0
        [PerRendererData] _FlTx ("Clean floor period x (0 = same column)", Float) = 0
        [PerRendererData] _FlTy ("Clean floor period y (0 = same row)", Float) = 0
        [PerRendererData] _ShX ("Shape rect min x (world, original place)", Float) = 0
        [PerRendererData] _ShY ("Shape rect min y (world, original place)", Float) = 0
        [PerRendererData] _ShW ("Shape rect width", Float) = 1
        [PerRendererData] _ShH ("Shape rect height", Float) = 1
        _FloorTol ("Floor difference tolerance (furniture)", Float) = 0.05
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
            sampler2D _BaseTex;   // 家具の水面: 列ごとの床の線 (型の u の列ごとに、絵の一番下の v を _BaseV0・_BaseVS の範囲で 0..1)
            float _BaseU0, _BaseUS, _BaseV0, _BaseVS;   // 列の範囲 (u0・1/du) と床の線の範囲 (v0・dv)
            float _BaseOn, _LvlCols, _LvlMax, _SilCut;
            sampler2D _FloorMask; // 部屋の絵の床の画素 (タイルと同じ範囲・1 = 床)
            float4 _MainTex_TexelSize, _FloorMask_TexelSize;
            float _FloorOn;
            sampler2D _FrontMask; // 影の中の写しだけ: 水より手前の家具の絵の形 (1 = 家具。視界の中では家具が水の上に描かれるので、写しもそこを抜く)
            float _FrontOn;
            sampler2D _MrpShadowTex; // 影のカメラの描き先 (視界の所はアルファ 0)
            float _ShadowOnly, _ShadowGain;
            float _MrpShadowOn; // 影の板が出ている間だけ 1
            // 確認用 (ブリッジの viewcheck だけが立てる): 1 = 描く画素を不透明の単色に・2 = 半透明の単色 (重ね塗りの数を読む)・3 = 描かない
            float _MrpDebugFlat;
            float _Mode, _WetLevel, _OutlinePx, _RimPx, _SprayEdge, _FaceScale;
            float _RUnits, _ShallowDepth, _Fog, _Caustic, _LineWave, _FaceView, _FoamW, _Shaft;
            fixed4 _Shallow, _Deep, _Outline, _Rim, _Abyss;
            float4 _Flow;
            sampler2D _FlowTex;
            float4 _FlowTex_TexelSize;
            float _FlowMax, _FlowCycle, _FlowStreak, _FlowFoam;
            float _Depth, _SubMax, _Lvl, _Cover, _SilR, _FaceH, _VPerUnit, _VBase, _Rise, _FlatTop;
            float4 _Body, _Quad;
            sampler2D _RoomTex;   // 家具の水面: 持ち上げた家具を描き込んだ部屋の絵 (_RoomOn = 1 の時だけ)
            float _RoomOn, _RoomSx, _RoomSy, _RoomOx, _RoomOy, _RoomDx, _RoomDy;
            float _FlX, _FlY, _FlTx, _FlTy, _ShX, _ShY, _ShW, _ShH, _FloorTol;

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

            // 流れ: 流れの向きに伸びた筋を速さで流す。筋の座標は位相ごとに 0 から流れの分ずらし、位相の切り替わりは重みを 0 にして隠す
            float Streak(float2 w, float2 dir, float2 off, float seed)
            {
                float2 p = w - off;
                float2 q = float2(dot(p, dir), dot(p, float2(-dir.y, dir.x)));
                // 流れの向きに長く (約 2.5 単位)・横に細い (約 0.3 単位) 筋。2 枚目で筋の途中を切って、途切れ途切れの泡の線にする
                float a = tex2D(_Noise, q * float2(0.18, 1.5) + seed).r;
                float b = tex2D(_Noise, q * float2(0.9, 0.9) + seed * 1.7 + 0.31).r;
                return smoothstep(0.6, 0.78, a) * smoothstep(0.35, 0.65, b);
            }

            void Current(inout fixed4 c, v2f i, float du)
            {
                // 流れの絵は周りに隣のタイルの升を 1 列ずつ持つ (n = タイルの升 + 2)
                float n = 1.0 / _FlowTex_TexelSize.x;
                float2 v = (tex2D(_FlowTex, (i.uv * (n - 2.0) + 1.0) / n).rg - 0.5) * (2.0 * _FlowMax);
                float sp = length(v);
                if (sp < 0.03) return;
                float2 dir = v / sp;
                float t = _Time.y / _FlowCycle;
                float p0 = frac(t), p1 = frac(t + 0.5);
                float w0 = 1.0 - abs(p0 * 2.0 - 1.0);
                float s0 = Streak(i.world, dir, v * (p0 * _FlowCycle), floor(t) * 0.37);
                float s1 = Streak(i.world, dir, v * (p1 * _FlowCycle), floor(t + 0.5) * 0.37 + 0.5);
                float s = s0 * w0 + s1 * (1.0 - w0);
                float k = saturate(sp / (_FlowMax * 0.5));
                float lane = s * k * smoothstep(0.02, 0.1, du);
                // 速い所は筋が太り白く泡立つ
                float foam = s * saturate((sp - _FlowMax * 0.4) / (_FlowMax * 0.5));
                c.rgb = lerp(c.rgb, float3(0.86, 0.95, 1.0), saturate(lane * _FlowStreak + foam * _FlowFoam));
                c.a = saturate(c.a + lane * _FlowStreak * 0.3 + foam * _FlowFoam * 0.5);
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
                // 床の縁と壁の面は足し合わせてつなぐ (max だと床の縁ちょうどで両方 0.5 になり、半分透けた線が出る)
                float body = saturate(saturate(wet + 0.5) + face); // 縁を 1 画素でぼかす
                if (body * vis <= 0.001) return 0;

                float murk, t = _Time.y;
                fixed4 c = Tint(i.world, du, murk);
                Current(c, i, du);

                // 縁: 外側に細い濃い輪郭・その内に明るい縁
                // 家具・壁に当たった所は絵の黒い輪郭があるので、濃い輪郭は描かず明るい縁だけ付ける
                float rim = saturate(1.0 - (inside - _OutlinePx) / _RimPx) * step(_OutlinePx, inside);
                // 家具・壁の縁の明るい線は浅い水が縁に当たる所だけ。深くなると縁は水の下に沈み、線が輪郭をなぞって浮いて見える
                rim = max(rim, saturate(1.0 - wet / _RimPx) * 0.7 * (1.0 - smoothstep(0.05, 0.18, du)));
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

            static const float FallTint = 0.8;   // 落ち切った滝の地の色のうち水たまりの色の割合

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
                // 深さのある水は水たまりと同じ色
                float du = data.b * 2.0;
                float pool = 1.0 - data.a;
                float murk;
                fixed4 tint = Tint(i.world, du, murk);
                // 縁から落ちた水 (深さがある) の地の色は落ちた後も元の水たまりの色。落ちるほど少しだけ噴き出しの水色へ寄せる
                water = lerp(water, tint.rgb, du > 0.001 ? lerp(FallTint, 1.0, pool) : pool);
                float white = saturate((foam * 0.85 + streak * 0.55) * (1.0 - pool));
                fixed4 c;
                c.rgb = lerp(water, float3(1, 1, 1), white);
                c.a = lerp(lerp(0.35, 0.85, saturate(dense * 0.9 + white * 0.3)), saturate(tint.a + white * 0.3), pool);
                // 縁: 薄い青の線 (水の膜の縁が光を曲げて暗く見える)。水たまりとつながる所 (深さのある所) は引かない
                float edge = saturate(1.0 - inside / 1.3) * (1.0 - pool);
                c.rgb = lerp(c.rgb, float3(0.3, 0.52, 0.82), edge * 0.8);
                c.a = max(c.a, edge * 0.85);
                c.a *= body * i.color.a;
                return c;
            }

            float Silhouette(float4 s) { return _SilR > 0.5 ? s.r * s.a : s.a; }

            // 持ち上げた家具の見分け (TerrainSprite の _UseDamage = 7 と同じ式): 部屋の絵がその場所のきれいな床の色のままなら床
            float2 RoomUv(float2 ow) { return (ow - float2(_RoomOx, _RoomOy)) / float2(_RoomSx, _RoomSy); }

            float3 RoomFloorAt(float2 ow)
            {
                float2 o = float2(_FlX, _FlY), t = max(float2(_FlTx, _FlTy), 1e-5);
                float2 rep = o + frac((ow - o) / t) * t;
                float2 fw = float2(_FlTx > 0 ? rep.x : ow.x, _FlTy > 0 ? rep.y : ow.y);
                return tex2Dlod(_RoomTex, float4(RoomUv(fw), 0, 0)).rgb;
            }

            bool RoomIsFloor(float2 ow)
            {
                fixed4 c = tex2Dlod(_RoomTex, float4(RoomUv(ow), 0, 0));
                if (c.a < 0.5) return true;
                float3 d = abs(c.rgb - RoomFloorAt(ow));
                return max(d.r, max(d.g, d.b)) < _FloorTol;
            }

            // 家具が床に落とす影か: その場所の床の色を一様に暗くした色 (床の色との比が 3 色でそろっている)
            bool RoomIsShadow(float2 ow)
            {
                float3 c = tex2Dlod(_RoomTex, float4(RoomUv(ow), 0, 0)).rgb, f = RoomFloorAt(ow);
                float k = dot(c, f) / max(dot(f, f), 1e-4);
                float3 d = abs(c - f * k);
                return k > 0.25 && k < 0.97 && max(d.r, max(d.g, d.b)) < _FloorTol;
            }

            // 型 (広げた四角の uv) の画素が家具の絵か。床の模様の手描きのずれ (細い線) は上下左右の 3 つ以上も床でない画素だけ家具
            bool OnFurniture(float2 uv)
            {
                float2 ow = float2(_ShX, _ShY) + uv * float2(_ShW, _ShH);
                if (RoomIsFloor(ow)) return false;
                float2 dx = float2(_RoomDx, 0), dy = float2(0, _RoomDy);
                int n = (RoomIsFloor(ow + dx) ? 0 : 1) + (RoomIsFloor(ow - dx) ? 0 : 1)
                      + (RoomIsFloor(ow + dy) ? 0 : 1) + (RoomIsFloor(ow - dy) ? 0 : 1);
                return n >= 3;
            }

            fixed4 Submerge(v2f i)
            {
                float4 shape = tex2D(_MainTex, i.uv);
                float sil = Silhouette(shape);
                if (sil <= 0.003) return 0;
                if (_SilCut > 0.0) sil = step(_SilCut, sil); // 床マスクと同じ閾値で「絵がある」画素だけ (半透明の金網の隙間は床の水が描く)
                // 型の中の床の画素と、当たり判定の外で家具が床に落とす影の画素は床の水が描く (切り抜きも描かない) ので板は描かない
                if (_RoomOn > 0.5)
                {
                    if (!OnFurniture(i.uv)) return 0;
                    if (shape.g < 0.5 && RoomIsShadow(float2(_ShX, _ShY) + i.uv * float2(_ShW, _ShH))) return 0;
                }
                // 床の線: 列ごと (斜めに置いた箱や形の崩れた家具は、列ごとに絵の下の縁が違う)。無ければ絵全体の一番下
                float vb = _VBase;
                float lvl = _Lvl;
                if (_BaseOn > 0.5)
                {
                    float4 bc = tex2Dlod(_BaseTex, float4(saturate((i.uv.x - _BaseU0) * _BaseUS), 0.5, 0, 0));
                    vb = _BaseV0 + bc.r * _BaseVS;
                    if (_LvlCols > 0.5)
                    {
                        vb = _BaseV0 + (bc.r * (65280.0 / 65535.0) + bc.b * (255.0 / 65535.0)) * _BaseVS; // 床の線は R (上位) と B (下位) の 16 ビット
                        lvl = bc.g * _LvlMax;
                        if (lvl < 0.004) return 0;
                    }
                }
                float h = clamp((i.uv.y - vb) / (_VPerUnit * _Rise), 0.0, _FaceH); // この画素の高さ (単位)
                if (_FlatTop > 0.5) h = _FaceH;
                float t = _Time.y, sub = _Cover;
                float onTop = step(_FaceH - 1e-4, h);
                float px = max(fwidth(i.world.y), 1e-4);
                float wave = (tex2D(_Noise, float2(i.world.x * 0.9 + t * 0.05, t * 0.03)).r - 0.5) * _LineWave * 2.0;
                float ex = lvl + wave * (1.0 - onTop) - h;   // この画素の上の水の厚さ
                float under = saturate(ex / px + 0.5);
                float murk;
                // 前面は斜めに見るので壁の面と同じく厚みを足す。天板は真上から見るので水の厚さだけ
                float face = 1.0 - onTop;
                fixed4 c = Tint(i.world, max(ex, 0.0) + _FaceView * face, murk);
                c.a = min(c.a, _SubMax);
                // 前面に当たる水面の線の明るい縁と、その下のちぎれた泡の帯
                float lip = saturate(1.0 - abs(ex) / (px * _RimPx)) * face;
                float fn = tex2D(_Noise, float2(i.world.x * 2.2 + t * 0.12, ex * 6.0 - t * 0.05)).r;
                float foam = saturate(1.0 - ex / _FoamW) * smoothstep(0.42, 0.62, fn) * face * under;
                c.rgb = lerp(c.rgb, float3(0.92, 0.97, 1.0), foam * 0.75);
                c.a = max(c.a, foam * 0.9);
                c = lerp(c, _Rim, lip * _Rim.a);
                c.a *= sil * max(under, lip);
                // 天板に乗り始めた水は一面に一度には広がらない: まだらに濡れた所が沈むにつれてつながる
                float n = tex2D(_Noise, i.world.xy * 0.7).r;
                c.a *= lerp(1.0, smoothstep(n - 0.06, n + 0.06, sub * 1.12 - 0.06), onTop);
                return c;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c;
                if (_Mode > 2.5) c = Submerge(i);
                else if (_Mode > 1.5) c = Wade(i);
                else
                {
                    float4 data = tex2D(_MainTex, _Mode < 0.5 ? EdgeUv(i.uv, _MainTex_TexelSize) : i.uv);
                    c = _Mode < 0.5 ? Puddle(i, data) : Spray(i, data);
                }
                // 影の手前の写し: 影の所だけに出す
                if (_ShadowOnly > 0.5)
                {
                    c.a *= tex2Dlod(_MrpShadowTex, float4(i.screen.xy / i.screen.w, 0, 0)).a * _ShadowGain * _MrpShadowOn;
                    // 家具の水面の板は絵の uv の範囲 (床の線と同じ) を型の 0..1 にしてある
                    float2 fuv = _Mode > 2.5 ? float2((i.uv.x - _BaseU0) * _BaseUS, (i.uv.y - _BaseV0) / _BaseVS) : i.uv;
                    if (_FrontOn > 0.5) c.a *= 1.0 - tex2D(_FrontMask, fuv).r;
                }
                if (_MrpDebugFlat > 0.5)
                {
                    if (_MrpDebugFlat > 2.5 || c.a < 0.03) return 0;
                    return fixed4(1, 0, 1, _MrpDebugFlat > 1.5 ? 0.5 : 1.0);
                }
                return c;
            }
            ENDCG
        }
    }
}
