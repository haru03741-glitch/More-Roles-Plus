using System;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Terrain;

namespace MoreRolesPlus.Bridge;

// 地形による死 (焼死・溺死・下敷き) の確認用コマンド
internal static class TerrainDeathCommands
{
    public static void Register()
    {
        TestBridge.Register("tdeath", "[<番号> burn|drown|crush [<起こした人>] | crumble <x> <y> [長さ] | rescue <助ける人> <瀕死の人>] 地形による死: 皆の足元の水の深さ・火・数えた秒 / その人をその死に方で死なせる (ホスト)", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // crumble <x> <y> [長さ]: そこの仕掛け壁を今すぐ崩す (時間切れと同じ・ホスト)
            if (a.Length >= 3 && a[0] == "crumble" && float.TryParse(a[1], out float cx) && float.TryParse(a[2], out float cy))
            {
                float len = a.Length >= 4 && float.TryParse(a[3], out float l) ? l : Terrain.TrickWall.DefaultLength;
                var res = Terrain.TerrainApi.WorldCrumble(Fx.FxMath.V2(cx, cy), len);
                reply((res.Ok ? "OK tdeath crumble " : "ERR tdeath crumble ") + res.Why);
                return;
            }
            // rescue <助ける人> <瀕死の人>: その人が長押しで助け出したのと同じ道で (ホスト)
            if (a.Length >= 3 && a[0] == "rescue" && byte.TryParse(a[1], out byte rb) && byte.TryParse(a[2], out byte rt))
            {
                var helper = PortKit.ById(rb);
                // 幽霊が助ける人なら守り霊の「救う」と同じ動き (回数は数えない・この端末だけ)
                if (helper && helper.Data != null && helper.Data.IsDead)
                {
                    var tp = PortKit.ById(rt);
                    if (!tp || !Rescue.SpiritCanTake(rb, rt)) { reply("ERR tdeath rescue spirit"); return; }
                    Rescue.SpiritUse(helper, tp);
                    reply("OK tdeath rescue spirit");
                    return;
                }
                string why = Rescue.HostTry(helper, PortKit.ById(rt));
                reply(why == null ? "OK tdeath rescue" : "ERR tdeath rescue " + why);
                return;
            }
            if (a.Length < 2) { reply("OK " + TerrainDeath.Describe()); return; }
            if (!byte.TryParse(a[0], out byte id)) { reply("ERR id"); return; }
            var cause = a[1] switch { "burn" => TerrainDeath.Cause.Burn, "drown" => TerrainDeath.Cause.Drown, "crush" => TerrainDeath.Cause.Crush, _ => TerrainDeath.Cause.None };
            if (cause == TerrainDeath.Cause.None) { reply("ERR cause"); return; }
            byte by = a.Length >= 3 && byte.TryParse(a[2], out byte b) ? b : ResolvedDamage.NoActor;
            reply("OK tdeath " + TerrainDeath.ForceDoom(id, cause, by));
        });
    }
}
