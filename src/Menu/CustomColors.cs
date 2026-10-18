// Based on the per-player palette of https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Modules/Cosmetics/DynamicPalette.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using InnerNet;
using MoreRolesPlus.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 自分で作った色 (#RRGGBB)。サーバーに見える色は本編の番号 (0〜17) のまま変えず、見た目の色だけを MRP の電文で全員に配る。
// 受け取った端末は本編の色の表 (Palette.PlayerColors / ShadowColors) の「その人の今の色番号」の所を書き換えて塗り直す。
// 本編の色番号は部屋の中で 1 人 1 つなので、番号の所を書き換えても他の人の色は変わらない。
// 表は接続の番号 (ClientId) で持ち、誰かの色番号が変わった・抜けた・色が届いた時に本編の色から作り直す。
// 試合が終わってロビーへ戻る時も同じ部屋なら表を残す (消すと配り直しが届くまで一瞬本編の色に戻る)。
// 自分の色は自分の人形が出てきた時にすぐ塗る (全員へ配るのは少し待ってから)
// 2 色目を決めた人は体を 1 色目 → 2 色目のグラデーションで塗る (人形の体のマテリアルのシェーダを MRP/PlayerGradient に差し替える)。
// 本編の色の表に入るのは 1 色目だけなので、帽子・会議の印・チャットの色など表から色を引く所は 1 色目になる
internal static class CustomColors
{
    // 作った色。B = 2 色目 (Grad の時だけ)・Dir = グラデーションの向き
    internal readonly struct Look
    {
        public readonly Color32 A, B;
        public readonly bool Grad;
        public readonly byte Dir;

        public Look(Color32 a, Color32 b, bool grad, byte dir)
        {
            A = a;
            B = grad ? b : default;
            Grad = grad;
            Dir = grad ? (byte)(dir % DirCount) : (byte)0;
        }

        public static Look Solid(Color32 a) => new(a, default, false, 0);

        public bool Same(Look o) => Eq(A, o.A) && Grad == o.Grad && (!Grad || (Eq(B, o.B) && Dir == o.Dir));
    }

    public static bool Eq(Color32 x, Color32 y) => x.r == y.r && x.g == y.g && x.b == y.b;

    // グラデーションの向き: 0 = 左→右・1 = 上→下・2 = 左上→右下・3 = 左下→右上
    public const int DirCount = 4;

    private static ConfigEntry<string> _mine;  // "" = 本編の色のまま / "RRGGBB"
    private static ConfigEntry<string> _mine2; // "" = 1 色だけ / "RRGGBB" = グラデーションの 2 色目
    private static ConfigEntry<int> _mineDir;
    private static Color32[] _body, _shadow;  // 本編の色の控え
    private static readonly Dictionary<int, Look> ByClient = new();
    private static int _room; // 表を作った部屋 (GameId)
    private static bool _applied; // 本編の色の表を書き換えている
    private static readonly Dictionary<int, Look> BySlot = new(); // 色番号 → 作った色 (Rebuild で作る)
    private static int _previewSlot = -1; // 決める前の見本を塗っている色番号
    private static Look _previewLook;
    private static readonly Dictionary<int, string> NameBySlot = new(); // 色番号 → 部屋の中で重ならない色の名前
    private static readonly Dictionary<string, int> NameCount = new();

    // A: 下位 24 ビット = 1 色目の RGB・25 ビット目 = 色あり (0 = 本編の色へ戻した)。
    // B: 下位 24 ビット = 2 色目の RGB・25 ビット目 = グラデーションあり・26〜27 ビット目 = 向き
    internal static readonly RemoteCall<(uint A, uint B)> Share = new("Color.Share", Route.Anyone,
        (w, v) => { w.Write(v.A); w.Write(v.B); }, r => (r.ReadUInt32(), r.ReadUInt32()), (sender, v) => Receive(sender, v));

    public static void Bind(ConfigFile cfg)
    {
        _mine = cfg.Bind("Client", "CustomColor", "", "Your own color as RRGGBB (empty = the game's color)");
        _mine2 = cfg.Bind("Client", "CustomColor2", "", "Second color of your gradient as RRGGBB (empty = one color)");
        _mineDir = cfg.Bind("Client", "CustomGradientDirection", 0, "Gradient direction: 0 = left to right, 1 = top to bottom, 2 = top left to bottom right, 3 = bottom left to top right");
    }

    public static bool TryParse(string s, out Color32 c)
    {
        c = default;
        s = (s ?? "").Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        c = new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
        return true;
    }

    public static string Hex(Color32 c) => $"{c.r:X2}{c.g:X2}{c.b:X2}";

    // 保存値を読み直すのは変わった時だけ (色のタブを開いている間は毎フレーム聞かれる)
    private static string _parsedFrom, _parsedFrom2;
    private static int _parsedDir;
    private static bool _parsedOk;
    private static Look _parsed;

    public static bool HasMine(out Color32 c)
    {
        bool ok = HasMineLook(out Look l);
        c = l.A;
        return ok;
    }

    public static bool HasMineLook(out Look l)
    {
        string v = _mine?.Value, v2 = _mine2?.Value;
        int dir = _mineDir?.Value ?? 0;
        if (!ReferenceEquals(v, _parsedFrom) || !ReferenceEquals(v2, _parsedFrom2) || dir != _parsedDir)
        {
            _parsedFrom = v;
            _parsedFrom2 = v2;
            _parsedDir = dir;
            _parsedOk = TryParse(v, out var a);
            bool grad = TryParse(v2, out var b);
            _parsed = new Look(a, b, grad, (byte)Math.Clamp(dir, 0, DirCount - 1));
        }
        l = _parsed;
        return _parsedOk;
    }

    // 自分の色を決める (null = 本編の色へ戻す)。保存して、部屋にいれば全員へ配る
    public static void SetMine(Color32? c) => SetLook(c.HasValue ? Look.Solid(c.Value) : null);

    public static void SetLook(Look? l)
    {
        if (_mine == null) return;
        _mine.Value = l.HasValue ? Hex(l.Value.A) : "";
        _mine2.Value = l.HasValue && l.Value.Grad ? Hex(l.Value.B) : "";
        if (l.HasValue) _mineDir.Value = l.Value.Dir;
        var me = PlayerControl.LocalPlayer;
        if (!me) return;
        if (l.HasValue) ByClient[me.OwnerId] = l.Value;
        else ByClient.Remove(me.OwnerId);
        Rebuild();
        Share.Send(Pack(l));
    }

    private static uint Rgb(Color32 c) => ((uint)c.r << 16) | ((uint)c.g << 8) | c.b;

    private static Color32 FromRgb(uint v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);

    private static (uint, uint) Pack(Look? l)
    {
        if (!l.HasValue) return (0u, 0u);
        var v = l.Value;
        return (0x1000000u | Rgb(v.A), v.Grad ? 0x1000000u | ((uint)v.Dir << 25) | Rgb(v.B) : 0u);
    }

    private static void Receive(PlayerControl sender, (uint A, uint B) v)
    {
        if (!sender || sender.AmOwner) return;
        if ((v.A & 0x1000000u) != 0)
        {
            var l = new Look(FromRgb(v.A), FromRgb(v.B), (v.B & 0x1000000u) != 0, (byte)((v.B >> 25) & 3));
            if (ByClient.TryGetValue(sender.OwnerId, out var old) && old.Same(l)) return;
            ByClient[sender.OwnerId] = l;
        }
        else if (!ByClient.Remove(sender.OwnerId)) return;
        _dirty = true;
    }

    // 影の色は本体の色を暗くして作る (本編の色の影とおおむね同じ暗さ)
    private static Color32 Shade(Color32 c) => new((byte)(c.r * 0.6f), (byte)(c.g * 0.6f), (byte)(c.b * 0.55f + 8), 255);

    private static bool Backup()
    {
        if (_body != null) return true;
        var pc = Palette.PlayerColors;
        var sc = Palette.ShadowColors;
        if (pc == null || sc == null) return false;
        _body = new Color32[pc.Length];
        _shadow = new Color32[sc.Length];
        for (int i = 0; i < _body.Length; i++) _body[i] = pc[i];
        for (int i = 0; i < _shadow.Length; i++) _shadow[i] = sc[i];
        return true;
    }

    // 本編の色に戻してから、色を持っている人の色番号の所だけ書き換え、全員を塗り直す
    public static void Rebuild()
    {
        if (ByClient.Count == 0 && !_applied) return; // 誰も色を作っていない部屋では何もしない
        if (!Backup()) return;
        _applied = ByClient.Count > 0;
        var pc = Palette.PlayerColors;
        var sc = Palette.ShadowColors;
        for (int i = 0; i < _body.Length && i < pc.Length; i++) pc[i] = _body[i];
        for (int i = 0; i < _shadow.Length && i < sc.Length; i++) sc[i] = _shadow[i];
        BySlot.Clear();
        _previewSlot = -1;
        NameBySlot.Clear();
        NameCount.Clear();
        var data = GameData.Instance;
        if (data && ByClient.Count > 0)
        {
            var all = data.AllPlayers;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p.Disconnected || !ByClient.TryGetValue(p.ClientId, out var l)) continue;
                int id = p.DefaultOutfit != null ? p.DefaultOutfit.ColorId : -1;
                if (id < 0 || id >= pc.Length || id >= sc.Length) continue;
                pc[id] = l.A;
                sc[id] = Shade(l.A);
                BySlot[id] = l;
                // 色覚の補助の文字として見分けられるよう、同じ名前の 2 人目からは番号を付ける (並びは全員の端末で同じ)
                string name = NameOf(l);
                int n = NameCount.TryGetValue(name, out int k) ? k + 1 : 1;
                NameCount[name] = n;
                NameBySlot[id] = n == 1 ? name : name + n;
            }
        }
        Repaint();
    }

    // その色番号を誰かが作った色で上書きしているか
    public static bool SlotColor(int id, out Color32 c)
    {
        bool ok = BySlot.TryGetValue(id, out var l);
        c = l.A;
        return ok;
    }

    // その色番号の色の名前 (作った色でなければ false)
    public static bool SlotName(int id, out string name) => NameBySlot.TryGetValue(id, out name);

    // 本編の色 (書き換える前の控え)
    public static Color32 VanillaBody(int id)
    {
        if (_body != null && id >= 0 && id < _body.Length) return _body[id];
        var pc = Palette.PlayerColors;
        return id >= 0 && id < pc.Length ? pc[id] : new Color32(0, 0, 0, 255);
    }

    // 決める前の見本: 色番号 id の所だけ表を書き換えて 1 体を塗る (戻すのは Rebuild)
    public static void PreviewSlot(int id, Look l, CosmeticsLayer layer)
    {
        if (!Backup()) return;
        var pc = Palette.PlayerColors;
        var sc = Palette.ShadowColors;
        if (id < 0 || id >= pc.Length || id >= sc.Length) return;
        pc[id] = l.A;
        sc[id] = Shade(l.A);
        _previewSlot = id;
        _previewLook = l;
        _applied = true;
        if (layer) layer.SetColor(id);
    }

    // ---- グラデーション ----

    // 差し替える前の本編のシェーダ: 歩いている人形など (Unlit/PlayerShader) と、会議の名札など枠で切り抜く人形 (Unlit/SimpleMaskedPlayer)
    private static Shader _vanillaShader, _vanillaMasked;
    private static string _lastRect = "-"; // 確認用: 最後に塗った体の絵の枠

    // 体の絵が取れない時の枠 (人形の自分の座標・立っている時の絵)
    private const float BodyX0 = -0.42f, BodyY0 = -0.52f, BodyW = 0.84f, BodyH = 1.04f;
    private const float BodyShare = 0.5f; // 体の絵の範囲のうち人形が占める割合 (歩く時の絵で 1.64×2.09 の中に 0.84×1.04)

    // 本編が人形 1 体を塗った後 (CosmeticsLayer.SetColor): その色番号がグラデーションなら体のシェーダを差し替える。
    // 本編はこの時に体のマテリアルを人形ごとに複製済みなので、sharedMaterial を書き換えても他の人形には移らない
    public static void ApplyGradient(CosmeticsLayer layer, int id)
    {
        var body = layer.currentBodySprite;
        var sr = body != null ? body.BodySprite : null;
        if (sr) ApplyGradient(sr, id);
    }

    public static void ApplyGradient(SpriteRenderer r, int id)
    {
        var grad = Fx.MrpBundle.PlayerGradientShader;
        if (!grad) return;
        Look look = default;
        bool want = id == _previewSlot ? (look = _previewLook).Grad : BySlot.TryGetValue(id, out look) && look.Grad;
        if (!want && !_vanillaShader && !_vanillaMasked) return; // まだ 1 度も差し替えていない
        var mat = r.sharedMaterial;
        if (!mat) return;
        var sh = mat.shader;
        bool isGrad = sh == grad;
        if (!want)
        {
            if (!isGrad) return;
            var back = mat.GetFloat("_MrpMasked") > 0.5f ? _vanillaMasked : _vanillaShader;
            if (back) mat.shader = back;
            return;
        }
        if (!isGrad)
        {
            // 本編の人形のシェーダの物だけ差し替える (着せ替えの店の見本などは別のシェーダ)
            bool masked;
            if (sh == _vanillaShader && _vanillaShader) masked = false;
            else if (sh == _vanillaMasked && _vanillaMasked) masked = true;
            else
            {
                string name = sh ? sh.name : "";
                if (name == "Unlit/PlayerShader") { _vanillaShader = sh; masked = false; }
                else if (name == "Unlit/SimpleMaskedPlayer") { _vanillaMasked = sh; masked = true; }
                else return;
                sh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            }
            mat.shader = grad;
            mat.SetFloat("_MrpMasked", masked ? 1f : 0f);
            mat.SetFloat("_MrpStencilComp", masked ? 3f : 7f); // Equal / GreaterEqual (本編の 2 つのシェーダと同じ)
        }
        // 色の向きの枠 = 今の体の絵の範囲 (人形の自分の座標) の真ん中の半分 (絵の周りには透明な余白がある)。
        // 会議の名札の絵は歩く時の絵と大きさが違う
        float x0 = BodyX0, y0 = BodyY0, w = BodyW, h = BodyH;
        var sp = r.sprite;
        if (sp)
        {
            var bd = sp.bounds;
            var c = bd.center;
            var sz = bd.size;
            if (sz.x > 0.01f && sz.y > 0.01f) { w = sz.x * BodyShare; h = sz.y * BodyShare; x0 = c.x - w / 2f; y0 = c.y - h / 2f; }
        }
        _lastRect = $"{x0:F2},{y0:F2} {w:F2}x{h:F2}";
        mat.SetColor("_BodyColor", look.A);
        mat.SetColor("_BackColor", Shade(look.A));
        mat.SetColor("_BodyColor2", look.B);
        mat.SetColor("_BackColor2", Shade(look.B));
        mat.SetFloat("_GradX0", x0);
        mat.SetFloat("_GradY0", y0);
        mat.SetFloat("_GradSX", 1f / w);
        mat.SetFloat("_GradSY", 1f / h);
        // t = u * wx + v * wy + w0 (u = 左 0 → 右 1・v = 下 0 → 上 1)
        var (wx, wy, w0) = look.Dir switch
        {
            1 => (0f, -1f, 1f),
            2 => (0.5f, -0.5f, 0.5f),
            3 => (0.5f, 0.5f, 0f),
            _ => (1f, 0f, 0f),
        };
        mat.SetFloat("_GradWX", wx);
        mat.SetFloat("_GradWY", wy);
        mat.SetFloat("_GradW0", w0);
    }

    // 色の名前 (いちばん近い名前の付いた色。近さは人の見え方に近い Lab の距離で測る)
    private static readonly (string Ja, string En, uint Rgb)[] Names =
    {
        ("赤", "Red", 0xE60012), ("朱色", "Vermilion", 0xEB6101), ("オレンジ", "Orange", 0xF39800), ("山吹色", "Amber", 0xF8B500),
        ("黄色", "Yellow", 0xF5EB3C), ("黄緑", "Yellow Green", 0xB8D200), ("ライム", "Lime", 0x7BE000), ("薄緑", "Pale Green", 0x98D98E),
        ("緑", "Green", 0x00A040), ("深緑", "Dark Green", 0x005A2E), ("ミント", "Mint", 0x7FE0C0), ("青緑", "Teal", 0x00A497),
        ("シアン", "Cyan", 0x20E0E8), ("水色", "Light Blue", 0x68C9F0), ("空色", "Sky Blue", 0x2CA9E1), ("青", "Blue", 0x1F4FE0),
        ("藍色", "Indigo", 0x1F3A8C), ("紺", "Navy", 0x0F1F4A), ("菫色", "Violet", 0x8A3CF0), ("紫", "Purple", 0x7A3EB1),
        ("藤色", "Lavender", 0xB9A2E0), ("マゼンタ", "Magenta", 0xE020D0), ("赤紫", "Crimson", 0xC2185B), ("桃色", "Pink", 0xF5A3C7),
        ("ローズ", "Rose", 0xE85298), ("えんじ", "Maroon", 0x8B1A2E), ("茶色", "Brown", 0x7B4A12), ("黄土色", "Ochre", 0xC39143),
        ("ベージュ", "Beige", 0xE8D3A9), ("白", "White", 0xF5F5F5), ("灰色", "Gray", 0x8C8C8C), ("濃い灰色", "Dark Gray", 0x4A4A4A),
        ("黒", "Black", 0x1A1A1A),
    };
    private static (float L, float A, float B)[] _namesLab;

    private static (float L, float A, float B) Lab(int r8, int g8, int b8)
    {
        static float Lin(int u)
        {
            float x = u / 255f;
            return x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
        }
        static float F(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
        float r = Lin(r8), g = Lin(g8), b = Lin(b8);
        float fx = F((0.4124f * r + 0.3576f * g + 0.1805f * b) / 0.95047f);
        float fy = F(0.2126f * r + 0.7152f * g + 0.0722f * b);
        float fz = F((0.0193f * r + 0.1192f * g + 0.9505f * b) / 1.08883f);
        return (116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }

    // グラデーションは 2 色の名前を並べる
    public static string NameOf(Look l) => l.Grad ? NameOf(l.A) + "/" + NameOf(l.B) : NameOf(l.A);

    public static string NameOf(Color32 c)
    {
        if (_namesLab == null)
        {
            _namesLab = new (float, float, float)[Names.Length];
            for (int i = 0; i < Names.Length; i++)
            {
                uint v = Names[i].Rgb;
                _namesLab[i] = Lab((int)(v >> 16), (int)(v >> 8) & 255, (int)v & 255);
            }
        }
        var (l, a, b) = Lab(c.r, c.g, c.b);
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < Names.Length; i++)
        {
            var n = _namesLab[i];
            float dl = l - n.L, da = a - n.A, db = b - n.B, d = dl * dl + da * da + db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        var (ja, en, _) = Names[best];
        var nb = _namesLab[best];
        // 色みのある名前には、名前の色より目立って明るい / 暗い時に言葉を足す (白・灰色・黒には足さない)
        if (nb.A * nb.A + nb.B * nb.B >= 225f)
        {
            if (l - nb.L >= 15f) return new Text("明るい" + ja, "Light " + en);
            if (l - nb.L <= -15f) return new Text("暗い" + ja, "Dark " + en);
        }
        return new Text(ja, en);
    }

    // 出ている人形を全部塗り直す (歩いている人・ロビーのホストの顔・会議の名札など。人形ごとの色番号のまま塗る)。
    // 色を変えた時と人の出入りの時だけ
    private static void Repaint()
    {
        var all = Object.FindObjectsOfType<CosmeticsLayer>();
        for (int i = 0; i < all.Length; i++)
        {
            var c = all[i];
            if (c) c.SetColor(c.ColorId);
        }
    }

    // ---- 部屋の出入り ----

    private static bool _sendPending, _dirty;
    private static long _sendAt;

    // 作り直しの予約 (人が出てくる時などに続けて来るので、次の tick で 1 回にまとめる)
    public static void MarkDirty()
    {
        if (ByClient.Count > 0 || _applied) _dirty = true;
    }

    // 誰か (自分も) が出てきた: 少し待ってから自分の色を全員へ配る (後から入った人に届けるため)
    public static void ScheduleSend()
    {
        _sendPending = true;
        _sendAt = Environment.TickCount64 + 1500;
    }

    public static void Tick()
    {
        if (_dirty)
        {
            _dirty = false;
            Rebuild();
        }
        if (!_sendPending || Environment.TickCount64 < _sendAt) return;
        _sendPending = false;
        if (!PlayerControl.LocalPlayer || !HasMineLook(out var l)) return;
        ByClient[PlayerControl.LocalPlayer.OwnerId] = l;
        Rebuild();
        Share.Send(Pack(l));
    }

    public static void OnJoined()
    {
        int room = AmongUsClient.Instance ? AmongUsClient.Instance.GameId : 0;
        if (room == _room) return; // 試合からロビーへ戻った: 同じ人の色はそのまま
        _room = room;
        ByClient.Clear();
        if (_body != null) Rebuild();
    }

    // 部屋を出た: 次に入る部屋では表を作り直す (LAN の部屋は番号が同じことがある)
    public static void OnExit() => _room = 0;

    // 自分の人形が出てきた: 作った色をすぐ塗る
    public static void OnLocalSpawned(PlayerControl me)
    {
        if (!HasMineLook(out var l)) return;
        if (ByClient.TryGetValue(me.OwnerId, out var old) && old.Same(l)) return;
        ByClient[me.OwnerId] = l;
        Rebuild();
    }

    public static void OnLeft(ClientData c)
    {
        if (c != null && ByClient.Remove(c.Id)) Rebuild();
    }

    // 確認用: 人の番号に色を届いた事にする (ダミーの見え方を見る)
    internal static string Fake(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !byte.TryParse(parts[0], out byte id) || !TryParse(parts[1], out var c)) return "ERR colors fake <番号> <#RRGGBB> [#RRGGBB [向き 0-3]]";
        var info = GameData.Instance ? GameData.Instance.GetPlayerById(id) : null;
        if (info == null) return "ERR colors fake: no player " + id;
        bool grad = TryParse(parts.Length >= 3 ? parts[2] : "", out var b);
        byte dir = parts.Length >= 4 && byte.TryParse(parts[3], out byte d) ? d : (byte)0;
        ByClient[info.ClientId] = new Look(c, b, grad, dir);
        Rebuild();
        return Describe();
    }

    // 確認用
    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder("OK colors mine=");
        sb.Append(_mine != null && _mine.Value.Length > 0 ? _mine.Value : "-");
        if (_mine2 != null && _mine2.Value.Length > 0) sb.Append('>').Append(_mine2.Value).Append('@').Append(_mineDir.Value);
        sb.Append(" grad=").Append(Fx.MrpBundle.PlayerGradientShader ? "ready" : "-").Append(" rect=").Append(_lastRect);
        foreach (var kv in ByClient)
        {
            sb.Append(' ').Append(kv.Key).Append('=').Append(Hex(kv.Value.A));
            if (kv.Value.Grad) sb.Append('>').Append(Hex(kv.Value.B)).Append('@').Append(kv.Value.Dir);
        }
        foreach (var kv in NameBySlot) sb.Append(" [").Append(kv.Key).Append(':').Append(kv.Value).Append(']');
        return sb.ToString();
    }
}

[HarmonyPatch]
internal static class CustomColorsPatches
{
    // 本編の色を選び直した (誰でも): 書き換える番号が変わるので作り直す
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetColor)), HarmonyPostfix]
    public static void SetColor() => CustomColors.MarkDirty();

    // 色の名前 (色覚の補助の文字・着せ替え画面の名前) を作った色の名前にする
    [HarmonyPatch(typeof(Palette), nameof(Palette.GetColorName)), HarmonyPostfix]
    public static void ColorName([HarmonyArgument(0)] int colorId, ref string __result)
    {
        if (CustomColors.SlotName(colorId, out var name)) __result = name;
    }

    [HarmonyPatch(typeof(CosmeticsLayer), nameof(CosmeticsLayer.SetColor)), HarmonyPostfix]
    public static void LayerColor(CosmeticsLayer __instance, [HarmonyArgument(0)] int color)
    {
        try { CustomColors.ApplyGradient(__instance, color); }
        catch (Exception e) { Plugin.Logger.LogError($"custom colors gradient: {e.Message}"); }
        if (!CustomColors.SlotName(color, out var name)) return;
        var t = __instance.colorBlindText;
        if (t) t.text = name;
    }

    // 会議の名札などは色を塗った後で体のマテリアルを枠で切り抜く物に取り替える (本編の UpdateBodyMaterial)
    [HarmonyPatch(typeof(CosmeticsLayer), nameof(CosmeticsLayer.UpdateBodyMaterial)), HarmonyPostfix]
    public static void BodyMaterial(CosmeticsLayer __instance)
    {
        try { CustomColors.ApplyGradient(__instance, __instance.ColorId); }
        catch (Exception e) { Plugin.Logger.LogError($"custom colors gradient: {e.Message}"); }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Start)), HarmonyPostfix]
    public static void Spawned(PlayerControl __instance)
    {
        try { if (__instance && __instance.AmOwner) CustomColors.OnLocalSpawned(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"custom colors: {e.Message}"); }
        CustomColors.ScheduleSend();
    }

    [HarmonyPatch(typeof(AmongUsClient), "OnGameJoined"), HarmonyPrefix]
    public static void Joined()
    {
        try { CustomColors.OnJoined(); }
        catch (Exception e) { Plugin.Logger.LogError($"custom colors: {e.Message}"); }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame)), HarmonyPrefix]
    public static void Exit() => CustomColors.OnExit();

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft)), HarmonyPrefix]
    public static void Left([HarmonyArgument(0)] ClientData data)
    {
        try { CustomColors.OnLeft(data); }
        catch (Exception e) { Plugin.Logger.LogError($"custom colors: {e.Message}"); }
    }
}
