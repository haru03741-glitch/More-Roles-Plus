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
// 表は人の番号 (PlayerId) で持ち、誰かの色番号が変わった・抜けた・色が届いた時に本編の色から作り直す
internal static class CustomColors
{
    private static ConfigEntry<string> _mine; // "" = 本編の色のまま / "RRGGBB"
    private static Color32[] _body, _shadow;  // 本編の色の控え
    private static readonly Dictionary<byte, Color32> ByPlayer = new();
    private static bool _applied; // 本編の色の表を書き換えている
    private static readonly Dictionary<int, Color32> BySlot = new(); // 色番号 → 作った色 (Rebuild で作る)
    private static readonly Dictionary<int, string> NameBySlot = new(); // 色番号 → 部屋の中で重ならない色の名前
    private static readonly Dictionary<string, int> NameCount = new();

    // 下位 24 ビット = RGB・25 ビット目 = 色あり (0 = 本編の色へ戻した)
    internal static readonly RemoteCall<uint> Share = new("Color.Share", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadUInt32(), (sender, v) => Receive(sender, v));

    public static void Bind(ConfigFile cfg) => _mine = cfg.Bind("Client", "CustomColor", "", "Your own color as RRGGBB (empty = the game's color)");

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
    private static string _parsedFrom;
    private static bool _parsedOk;
    private static Color32 _parsed;

    public static bool HasMine(out Color32 c)
    {
        string v = _mine?.Value;
        if (!ReferenceEquals(v, _parsedFrom))
        {
            _parsedFrom = v;
            _parsedOk = TryParse(v, out _parsed);
        }
        c = _parsed;
        return _parsedOk;
    }

    // 自分の色を決める (null = 本編の色へ戻す)。保存して、部屋にいれば全員へ配る
    public static void SetMine(Color32? c)
    {
        if (_mine == null) return;
        _mine.Value = c.HasValue ? Hex(c.Value) : "";
        var me = PlayerControl.LocalPlayer;
        if (!me) return;
        if (c.HasValue) ByPlayer[me.PlayerId] = c.Value;
        else ByPlayer.Remove(me.PlayerId);
        Rebuild();
        Share.Send(Pack(c));
    }

    private static uint Pack(Color32? c) => c.HasValue ? 0x1000000u | ((uint)c.Value.r << 16) | ((uint)c.Value.g << 8) | c.Value.b : 0u;

    private static void Receive(PlayerControl sender, uint v)
    {
        if (!sender || sender.AmOwner) return;
        if ((v & 0x1000000u) != 0)
        {
            var c = new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
            if (ByPlayer.TryGetValue(sender.PlayerId, out var old) && old.r == c.r && old.g == c.g && old.b == c.b) return;
            ByPlayer[sender.PlayerId] = c;
        }
        else if (!ByPlayer.Remove(sender.PlayerId)) return;
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
        if (ByPlayer.Count == 0 && !_applied) return; // 誰も色を作っていない部屋では何もしない
        if (!Backup()) return;
        _applied = ByPlayer.Count > 0;
        var pc = Palette.PlayerColors;
        var sc = Palette.ShadowColors;
        for (int i = 0; i < _body.Length && i < pc.Length; i++) pc[i] = _body[i];
        for (int i = 0; i < _shadow.Length && i < sc.Length; i++) sc[i] = _shadow[i];
        BySlot.Clear();
        NameBySlot.Clear();
        NameCount.Clear();
        var data = GameData.Instance;
        if (data && ByPlayer.Count > 0)
        {
            var all = data.AllPlayers;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p.Disconnected || !ByPlayer.TryGetValue(p.PlayerId, out var c)) continue;
                int id = p.DefaultOutfit != null ? p.DefaultOutfit.ColorId : -1;
                if (id < 0 || id >= pc.Length || id >= sc.Length) continue;
                pc[id] = c;
                sc[id] = Shade(c);
                BySlot[id] = c;
                // 色覚の補助の文字として見分けられるよう、同じ名前の 2 人目からは番号を付ける (並びは全員の端末で同じ)
                string name = NameOf(c);
                int n = NameCount.TryGetValue(name, out int k) ? k + 1 : 1;
                NameCount[name] = n;
                NameBySlot[id] = n == 1 ? name : name + n;
            }
        }
        Repaint();
    }

    // その色番号を誰かが作った色で上書きしているか
    public static bool SlotColor(int id, out Color32 c) => BySlot.TryGetValue(id, out c);

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
    public static void PreviewSlot(int id, Color32 c, CosmeticsLayer layer)
    {
        if (!Backup()) return;
        var pc = Palette.PlayerColors;
        var sc = Palette.ShadowColors;
        if (id < 0 || id >= pc.Length || id >= sc.Length) return;
        pc[id] = c;
        sc[id] = Shade(c);
        _applied = true;
        if (layer) layer.SetColor(id);
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
        if (ByPlayer.Count > 0 || _applied) _dirty = true;
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
        if (!PlayerControl.LocalPlayer || !HasMine(out var c)) return;
        ByPlayer[PlayerControl.LocalPlayer.PlayerId] = c;
        Rebuild();
        Share.Send(Pack(c));
    }

    public static void OnJoined()
    {
        ByPlayer.Clear();
        if (_body != null) Rebuild();
    }

    public static void OnLeft(ClientData c)
    {
        var ch = c != null ? c.Character : null;
        if (ch && ByPlayer.Remove(ch.PlayerId)) Rebuild();
    }

    // 確認用: 人の番号に色を届いた事にする (ダミーの見え方を見る)
    internal static string Fake(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !byte.TryParse(parts[0], out byte id) || !TryParse(parts[1], out var c)) return "ERR colors fake <番号> <#RRGGBB>";
        ByPlayer[id] = c;
        Rebuild();
        return Describe();
    }

    // 確認用
    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder("OK colors mine=");
        sb.Append(_mine != null && _mine.Value.Length > 0 ? _mine.Value : "-");
        foreach (var kv in ByPlayer) sb.Append(' ').Append(kv.Key).Append('=').Append(Hex(kv.Value));
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
        if (!CustomColors.SlotName(color, out var name)) return;
        var t = __instance.colorBlindText;
        if (t) t.text = name;
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Start)), HarmonyPostfix]
    public static void Spawned() => CustomColors.ScheduleSend();

    [HarmonyPatch(typeof(AmongUsClient), "OnGameJoined"), HarmonyPrefix]
    public static void Joined()
    {
        try { CustomColors.OnJoined(); }
        catch (Exception e) { Plugin.Logger.LogError($"custom colors: {e.Message}"); }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft)), HarmonyPrefix]
    public static void Left([HarmonyArgument(0)] ClientData data)
    {
        try { CustomColors.OnLeft(data); }
        catch (Exception e) { Plugin.Logger.LogError($"custom colors: {e.Message}"); }
    }
}
