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
                        // enter [的の数] [city|space|town] (town = 焼いた街・川と橋のある街並み)
                        if (args.Length > 2) Arena.Stage = args[2] == "space" ? 0 : (args[2] == "town" ? 2 : 1);
                        string r;
                        try { r = Arena.Enter(n); }
                        catch (System.Exception e) { Plugin.Logger.LogError($"flight enter: {e}"); Arena.Exit(); r = e.GetType().Name + ": " + e.Message; }
                        reply(r == "ok" ? "OK flight enter" : "ERR flight " + r);
                        return;
                    }
                case "place":
                    {
                        // place <x> <y> <z> [yaw] [pitch]: 自分の船を置き直す (街を近くから見る確かめ用)
                        float G(int k, float d) => args.Length > k && float.TryParse(args[k], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : d;
                        reply(Arena.DebugPlace(new UnityEngine.Vector3(G(1, 0), G(2, 0), G(3, 0)), G(4, 0), G(5, 0)) ? "OK flight place" : "ERR flight not active");
                        return;
                    }
                case "hold":
                    {
                        // hold <x> <y> <z> [yaw] [pitch] | hold off: 確かめ用。自分の船を毎フレームそこへ置き直す
                        float G(int k, float d) => args.Length > k && float.TryParse(args[k], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : d;
                        bool on = !(args.Length > 1 && args[1] == "off");
                        Arena.DebugHold(on, new UnityEngine.Vector3(G(1, 0), G(2, 0), G(3, 0)), G(4, 0), G(5, 0));
                        reply("OK flight hold " + (on ? "on" : "off"));
                        return;
                    }
                case "alarm":
                    // 確かめ用: 夜の街の警報 (明かりの明滅とサイレン) を今すぐ
                    FlightCity.StartAlarm(args.Length > 1 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float sec) ? sec : 5.5f);
                    reply(FlightCity.Active ? "OK flight alarm" : "ERR flight no city");
                    return;
                case "cars":
                    {
                        // cars mark [半径]: 船の真下の周りで、式が「いる」と見ている車に緑の印 / cars hit <x> <z> <横の位置> [前後の幅]: その辺りの車線の車を 1 台撃ったことにする (街の座標)
                        float G(int k, float d) => args.Length > k && float.TryParse(args[k], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : d;
                        string op = args.Length > 1 ? args[1] : "stat";
                        if (!FlightCity.Active) { reply("ERR flight no city"); return; }
                        if (op == "mark") { reply($"OK flight cars marked={FlightCars.Mark(G(2, 120f))}"); return; }
                        if (op == "hit") { reply(FlightCars.DebugHit(G(2, 0f), G(3, 0f), G(4, 0f), G(5, 30f)) ? $"OK flight cars hit at {FlightCars.LastBurn.x:0.0} {FlightCars.LastBurn.z:0.0}" : "ERR flight cars none"); return; }
                        reply($"OK flight cars burned={FlightCars.Burned}");
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
                case "bdmg":
                    {
                        // bdmg <削る量> [x y z]: 確かめ用。点 (省略は自分の船の 150 前) に一番近い立っている棟を削る
                        float G(int k, float d) => args.Length > k && float.TryParse(args[k], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : d;
                        var at = args.Length > 4 ? new UnityEngine.Vector3(G(2, 0), G(3, 0), G(4, 0)) : Arena.LocalShipPos + Arena.LocalShipForward * 150f;
                        int k = FlightCity.Nearest(at, 400f);
                        if (k < 0) { reply("ERR flight no building"); return; }
                        bool changed = FlightCity.Damage(k, G(1, 50), at);
                        reply($"OK flight bdmg bld={k} changed={changed} collapsed={FlightCity.Collapsed} box={FlightCity.BoxText(k)}");
                        return;
                    }
                case "cclock":
                    // cclock <速さ>: 確かめ用。崩れの時計の速さ (0.1 = 10 倍ゆっくり)
                    FlightCity.SetClockScale(args.Length > 1 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float ck) ? ck : 1f);
                    reply("OK flight cclock");
                    return;
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
