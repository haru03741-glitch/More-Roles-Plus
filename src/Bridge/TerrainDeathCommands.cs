using System;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Bridge;

// 地形による死 (焼死・溺死・下敷き) の確認用コマンド
internal static class TerrainDeathCommands
{
    public static void Register()
    {
        TestBridge.Register("tdeath", "[<番号> burn|drown|crush] 地形による死: 皆の足元の水の深さ・火・数えた秒 / その人をその死に方で死なせる (ホスト)", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length < 2) { reply("OK " + TerrainDeath.Describe()); return; }
            if (!byte.TryParse(a[0], out byte id)) { reply("ERR id"); return; }
            var cause = a[1] switch { "burn" => TerrainDeath.Cause.Burn, "drown" => TerrainDeath.Cause.Drown, "crush" => TerrainDeath.Cause.Crush, _ => TerrainDeath.Cause.None };
            if (cause == TerrainDeath.Cause.None) { reply("ERR cause"); return; }
            reply("OK tdeath " + TerrainDeath.ForceDoom(id, cause));
        });
    }
}
