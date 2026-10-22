using System.Collections.Generic;
using System.IO;
using AmongUs.Data;
using AmongUs.Data.Player;
using AmongUs.Data.Settings;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// Based on https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Patches/CopyGameCodePatch.cs (GPL-3.0)
// オンラインの部屋を立てたら、部屋のコードをクリップボードに入れる (端末ごとの設定)。同じ部屋では試合の後に入れ直さない
internal static class RoomCodeCopy
{
    private static int _copiedFor;

    public static void OnLobby()
    {
        var client = AmongUsClient.Instance;
        if (!ClientOptions.CopyRoomCode.Value || !client || !client.AmHost || client.NetworkMode != NetworkModes.OnlineGame) return;
        int id = client.GameId;
        if (id == 0 || id == _copiedFor) return;
        _copiedFor = id;
        string code = InnerNet.GameCode.IntToGameName(id);
        GUIUtility.systemCopyBuffer = code;
        bool streamer = DataManager.Settings != null && DataManager.Settings.Gameplay != null && DataManager.Settings.Gameplay.StreamerMode;
        ChatNotes.Post(new Text("Convenience.CodeCopied").With("code", streamer ? "******" : code));
    }
}

// Based on https://github.com/AU-Avengers/TOU-Mira TownOfUs/Patches/Misc/SaveFilePatches.cs (GPL-3.0)
// 本編のプレイヤーデータと設定を MRP 用の別のファイルへ保存する。MRP は色の番号などを本編の範囲の外まで使うので、
// mod 無しの本編がそれを読んで壊れないように。MRP 用のファイルがまだ無い時は本編のファイルを写してから使う (名前や設定を引き継ぐ)
[HarmonyPatch]
internal static class SaveFileSplit
{
    private const string Suffix = "_MRP";
    private static readonly Dictionary<string, string> Names = new(); // 本編の名前 → MRP の名前 (写しを確かめた物)

    [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.FileName), MethodType.Getter)]
    [HarmonyPatch(typeof(SettingsData), nameof(SettingsData.FileName), MethodType.Getter)]
    [HarmonyPostfix]
    public static void Postfix(ref string __result)
    {
        string vanilla = __result;
        if (string.IsNullOrEmpty(vanilla)) return;
        if (Names.TryGetValue(vanilla, out string known)) { __result = known; return; }
        if (vanilla.Contains(Suffix)) return;
        // 本編の名前は拡張子まで入っている (player.amogus)。印は拡張子の前に入れる
        string mine = Path.GetFileNameWithoutExtension(vanilla) + Suffix + Path.GetExtension(vanilla);
        __result = mine;
        try
        {
            string dir = Application.persistentDataPath;
            string src = Path.Combine(dir, vanilla), dst = Path.Combine(dir, mine);
            if (File.Exists(src) && !File.Exists(dst))
            {
                File.Copy(src, dst);
                Plugin.Logger.LogInfo($"save files: copied {vanilla} -> {mine}");
            }
            Names[vanilla] = mine; // 写せた (または写す必要が無かった) 時だけ覚える。失敗したら次に呼ばれた時にもう一度
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"save files: {e.Message}"); }
    }
}

// 初めて部屋に入った時に 1 回だけ、MRP の操作のヒントをチャットに出す (自分にだけ見える)
internal static class FirstHints
{
    public static void OnLobby()
    {
        if (ClientOptions.HintsShown.Value) return;
        ClientOptions.HintsShown.Value = true;
        ChatNotes.Post(new Text("Convenience.Hints"));
    }
}

[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class ConvenienceLobbyPatch
{
    public static void Postfix()
    {
        try { RoomCodeCopy.OnLobby(); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"room code copy: {e.Message}"); }
        FirstHints.OnLobby();
    }
}
