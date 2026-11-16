using System.Globalization;
using MoreRolesPlus.Bridge;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦の確かめ用の命令 (bridge)
internal static class FlightCommands
{
    internal static void Register()
    {
        TestBridge.Register("flight", "[enter [的の数] | exit | stat | set <名前> <値> | boom | params | view <yaw> <pitch> <距離> [見る点 x y z] | input <pitch> <yaw> <roll> <fire> <boost> <brake> <秒> | dodge <-1|1> | uturn | shoot [横のずれ] | tail [距離] | ship [weapon|hatch|shields|rpcweapon|rpchatch|rpcshields]] 3D の艦隊戦に一人で入る (開発用)", (line, reply) =>
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
                        // view <yaw> <pitch> <距離> [見る点 x y z (船の中の座標)] (距離 0 = 普段のカメラへ戻す)
                        float G(int k, float d) => args.Length > k && float.TryParse(args[k], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : d;
                        Arena.SetView(G(1, 0), G(2, 20), G(3, 0), G(4, 0), G(5, 0), G(6, 0));
                        reply($"OK flight view {G(1, 0)} {G(2, 20)} {G(3, 0)}");
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
                case "shoot":
                    {
                        float side = args.Length > 1 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float sd) ? sd : 6f;
                        reply(Arena.DebugShoot(side) ? $"OK flight shoot {side}" : "ERR flight not active");
                        return;
                    }
                case "tail":
                    {
                        float d = args.Length > 1 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float dd) ? dd : 90f;
                        reply(Arena.DebugTail(d) ? $"OK flight tail {d}" : "ERR flight no target");
                        return;
                    }
                case "ship":
                    {
                        // ship [weapon|hatch|shields|rpcweapon|rpchatch|rpcshields]: 本編の船の外の見た目 (砲台・ゴミの口・シールドの光) の位置を出し、指定があれば本編の動きを起こす
                        var ss = ShipStatus.Instance;
                        if (!ss) { reply("ERR flight no ship"); return; }
                        string what = args.Length > 1 ? args[1] : "";
                        if (what == "weapon") ss.FireWeapon();
                        else if (what == "hatch") ss.OpenHatch();
                        else if (what == "shields") ss.StartShields();
                        // rpc*: タスクを終えた時と同じ本編の電文で全員に見た目を出させる (他の窓の 3D で確かめる)
                        else if (what == "rpcweapon") PlayerControl.LocalPlayer.RpcPlayAnimation((byte)TaskTypes.ClearAsteroids);
                        else if (what == "rpchatch") PlayerControl.LocalPlayer.RpcPlayAnimation((byte)TaskTypes.EmptyGarbage);
                        else if (what == "rpcshields") PlayerControl.LocalPlayer.RpcPlayAnimation((byte)TaskTypes.PrimeShields);
                        var sb = new System.Text.StringBuilder("OK flight ship");
                        void Put(string name, UnityEngine.Component c)
                        {
                            if (!c) { sb.Append(' ').Append(name).Append("=none"); return; }
                            var p = c.transform.position;
                            sb.Append(' ').Append(name).Append('=').Append(p.x.ToString("F2", CultureInfo.InvariantCulture)).Append(',').Append(p.y.ToString("F2", CultureInfo.InvariantCulture));
                            var sr = c.GetComponent<UnityEngine.SpriteRenderer>();
                            if (sr && sr.sprite) { var b = sr.bounds; sb.Append("[").Append(b.min.x.ToString("F2", CultureInfo.InvariantCulture)).Append(',').Append(b.min.y.ToString("F2", CultureInfo.InvariantCulture)).Append("..").Append(b.max.x.ToString("F2", CultureInfo.InvariantCulture)).Append(',').Append(b.max.y.ToString("F2", CultureInfo.InvariantCulture)).Append(' ').Append(sr.sprite.name).Append(']'); }
                        }
                        Put("weapons", ss.WeaponsImage);
                        Put("hatch", ss.Hatch);
                        Put("hatchfx", ss.HatchParticles);
                        Put("border", ss.ShieldBorder);
                        var si = ss.ShieldsImages;
                        int sn = si == null ? 0 : si.Length;
                        sb.Append(" shields=").Append(sn);
                        for (int i = 0; i < sn; i++) Put("s" + i, si[i]);
                        reply(sb.ToString());
                        return;
                    }
            }
            reply("ERR flight unknown " + sub);
        });
    }
}
