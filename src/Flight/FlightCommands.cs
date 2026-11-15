using System.Globalization;
using MoreRolesPlus.Bridge;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦の確かめ用の命令 (bridge)
internal static class FlightCommands
{
    internal static void Register()
    {
        TestBridge.Register("flight", "[enter [的の数] | exit | stat | set <名前> <値> | boom | params | view <yaw> <pitch> <距離> | input <pitch> <yaw> <roll> <fire> <boost> <brake> <秒> | dodge <-1|1> | uturn] 3D の艦隊戦に一人で入る (開発用)", (line, reply) =>
        {
            string[] args = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            string sub = args.Length > 0 ? args[0] : "stat";
            switch (sub)
            {
                case "enter":
                    {
                        int n = args.Length > 1 && int.TryParse(args[1], out int t) ? t : 3;
                        string r;
                        try { r = Arena.Enter(n); }
                        catch (System.Exception e) { Plugin.Logger.LogError($"flight enter: {e}"); Arena.Exit(); r = e.GetType().Name + ": " + e.Message; }
                        reply(r == "ok" ? "OK flight enter" : "ERR flight " + r);
                        return;
                    }
                case "exit":
                    Arena.Exit();
                    reply("OK flight exit");
                    return;
                case "stat":
                    reply($"OK flight active={Arena.Active} {Arena.Stats}");
                    return;
                case "view":
                    {
                        // view <yaw> <pitch> <距離> (距離 0 = 普段のカメラへ戻す)
                        float G(int k, float d) => args.Length > k && float.TryParse(args[k], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : d;
                        Arena.ViewYaw = G(1, 0); Arena.ViewPitch = G(2, 20); Arena.ViewDist = G(3, 0);
                        reply($"OK flight view {Arena.ViewYaw} {Arena.ViewPitch} {Arena.ViewDist}");
                        return;
                    }
                case "boom":
                    // 確かめ用: 自分の船の前に爆発を出す
                    reply(Arena.DebugExplode() ? "OK flight boom" : "ERR flight not active");
                    return;
                case "params":
                    reply("OK " + FlightParams.Dump());
                    return;
                case "set":
                    if (args.Length < 3 || !FlightParams.Set(args[1], args[2])) { reply("ERR flight set <name> <value>"); return; }
                    Arena.PushParams();
                    reply($"OK flight {args[1]}={args[2]}");
                    return;
                case "input":
                    {
                        if (args.Length < 8) { reply("ERR flight input <pitch> <yaw> <roll> <fire> <boost> <brake> <sec>"); return; }
                        float F(int k) => float.Parse(args[k], CultureInfo.InvariantCulture);
                        FlightInput.Forced = new FlightNative.Input { Pitch = F(1), Yaw = F(2), Roll = F(3), Fire = (int)F(4), Boost = (int)F(5), Brake = (int)F(6) };
                        FlightInput.ForcedT = F(7);
                        reply("OK flight input");
                        return;
                    }
                case "dodge":
                    FlightInput.Forced.Dodge = args.Length > 1 && args[1] == "-1" ? -1 : 1;
                    if (FlightInput.ForcedT <= 0) FlightInput.ForcedT = 0.05f;
                    reply("OK flight dodge");
                    return;
                case "uturn":
                    FlightInput.Forced.UTurn = 1;
                    if (FlightInput.ForcedT <= 0) FlightInput.ForcedT = 0.05f;
                    reply("OK flight uturn");
                    return;
            }
            reply("ERR flight unknown " + sub);
        });
    }
}
