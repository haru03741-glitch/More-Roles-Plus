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

    public static bool HasMine(out Color32 c) => TryParse(_mine?.Value, out c);

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
            }
        }
        Repaint();
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

    // 確認用
    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder("OK colors mine=");
        sb.Append(_mine != null && _mine.Value.Length > 0 ? _mine.Value : "-");
        foreach (var kv in ByPlayer) sb.Append(' ').Append(kv.Key).Append('=').Append(Hex(kv.Value));
        return sb.ToString();
    }
}

[HarmonyPatch]
internal static class CustomColorsPatches
{
    // 本編の色を選び直した (誰でも): 書き換える番号が変わるので作り直す
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetColor)), HarmonyPostfix]
    public static void SetColor() => CustomColors.MarkDirty();

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
