using System.Reflection;
using HarmonyLib;
using Impostor.Api.Games;
using Impostor.Api.Games.Managers;
using Impostor.Api.Innersloth;
using Impostor.Api.Innersloth.GameOptions;
using Impostor.Hazel.Abstractions;
using Microsoft.Extensions.Logging;

namespace MrpServer;

// MRP はロビーでクラシックとかくれんぼを切り替えられる (ホストが GameManager を出し直して、新しいモードの設定を送る)。
// Impostor は部屋を作った時のモードの設定の入れ物 (Game.Options) を持ち続け、届いた設定をその入れ物の形に読むので、
// 別のモードの設定が届くと形が合わずに例外になり、ホストが切断される (「サーバー接続が切れました」)。
// 設定を読む所 (GameOptionsFactory.DeserializeInto) の前で形が違うと分かったら、部屋の入れ物を届いたモードの物に差し替える
internal static class GameModeFix
{
    private const byte ModularOptionsDataVersion = 7;
    private static IGameManager? _games;
    private static ILogger? _log;
    private static FieldInfo? _optionsField;

    // 直しが入ったか (/mrp/status の modeSwitch。ゲームはこれを見てからロビーでの切り替えを許す)
    public static bool Installed { get; private set; }

    public static void Install(IGameManager games, ILogger log)
    {
        _games = games;
        _log = log;
        var target = AccessTools.Method(typeof(GameOptionsFactory), nameof(GameOptionsFactory.DeserializeInto));
        if (target == null) { log.LogError("game mode fix: DeserializeInto not found (mode switch in the lobby will disconnect the host)"); return; }
        new Harmony("mrp.server.gamemode").Patch(target, prefix: new HarmonyMethod(typeof(GameModeFix), nameof(Prefix)));
        Installed = true;
        log.LogInformation("game mode fix installed");
    }

    private static bool IsHideNSeek(GameModes m) => m is GameModes.HideNSeek or GameModes.SeekFools;

    // false = 元の処理を飛ばす (差し替えた入れ物へ読み終えた)
    private static bool Prefix(IMessageReader reader, IGameOptions gameOptions)
    {
        try
        {
            if (gameOptions is not (NormalGameOptions or HideNSeekGameOptions)) return true;
            // 読み進めないよう写しで頭だけ覗く (大きさ・版・中の電文のモード)
            var peek = reader.Copy();
            peek.ReadPackedInt32();
            if (peek.ReadByte() < ModularOptionsDataVersion) return true;
            var mode = (GameModes)peek.ReadMessage().ReadByte();
            if (IsHideNSeek(mode) == gameOptions is HideNSeekGameOptions) return true;

            var game = _games?.Games.FirstOrDefault(g => ReferenceEquals(g.Options, gameOptions));
            if (game == null) return true;
            var field = _optionsField ??= OptionsField(game.GetType());
            if (field == null) { _log?.LogError("game mode fix: Game.Options field not found"); return true; }
            var fresh = GameOptionsFactory.Deserialize(reader);
            field.SetValue(game, fresh);
            _log?.LogInformation("game {Code}: options switched to {Mode}", game.Code, mode);
            return false;
        }
        catch (Exception e)
        {
            _log?.LogError(e, "game mode fix failed");
            return true;
        }
    }

    // 読み取り専用の自動プロパティ Options の裏の欄
    private static FieldInfo? OptionsField(Type? t)
    {
        for (; t != null; t = t.BaseType)
        {
            var f = t.GetField("<Options>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null) return f;
        }
        return null;
    }
}
