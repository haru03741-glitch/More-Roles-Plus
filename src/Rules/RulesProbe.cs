using System.Text;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Rules;

// 試合のルールの確認用コマンド
internal static class RulesProbe
{
    public static void Register()
    {
        TestBridge.Register("shiptasks", "今のマップのタスクの種類 (共通 / 長い / 短い) を本編の船の表から出す", (_, reply) =>
        {
            var ship = ShipStatus.Instance;
            if (!ship) { reply("ERR shiptasks no ship"); return; }
            var sb = new StringBuilder($"OK shiptasks map={MapRules.MapId}");
            void List(string name, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<NormalPlayerTask> tasks)
            {
                sb.Append('\n').Append(name).Append(':');
                if (tasks == null) return;
                foreach (var t in tasks) if (t) sb.Append(' ').Append(t.TaskType);
            }
            List("common", ship.CommonTasks);
            List("long", ship.LongTasks);
            List("short", ship.ShortTasks);
            reply(sb.ToString());
        });

        TestBridge.Register("mytasks", "自分に配られたタスクの種類 (全部)", (_, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || lp.myTasks == null) { reply("ERR mytasks"); return; }
            var sb = new StringBuilder("OK mytasks");
            foreach (var t in lp.myTasks)
                if (t && t.TaskType != TaskTypes.None) sb.Append(' ').Append(t.TaskType);
            reply(sb.ToString());
        });

        TestBridge.Register("consoles", "今のマップの機器の端末 (アドミンの机・カメラ・バイタル・ドアログ・ジップライン) の名前・位置・止めたか・自分が使えるか", (_, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!ShipStatus.Instance || !lp) { reply("ERR consoles no ship"); return; }
            var sb = new StringBuilder("OK consoles");
            foreach (var c in Object.FindObjectsOfType<MapConsole>())
            {
                c.CanUse(lp.Data, out bool can, out bool could);
                sb.Append($"\n map '{c.name}' ({c.transform.position.x:0.##},{c.transform.position.y:0.##}) blocked={Devices.Blocked(c.Pointer)} could={could}");
            }
            foreach (var c in Object.FindObjectsOfType<SystemConsole>())
            {
                var mg = c.MinigamePrefab;
                c.CanUse(lp.Data, out bool can, out bool could);
                sb.Append($"\n sys '{c.name}' ({c.transform.position.x:0.##},{c.transform.position.y:0.##}) game={(mg ? mg.GetIl2CppType().Name : "-")} blocked={Devices.Blocked(c.Pointer)} could={could}");
            }
            foreach (var z in Object.FindObjectsOfType<ZiplineConsole>())
            {
                z.CanUse(lp.Data, out bool can, out bool could);
                sb.Append($"\n zip top={z.atTop} ({z.transform.position.x:0.##},{z.transform.position.y:0.##}) could={could}");
            }
            reply(sb.ToString());
        });

        TestBridge.Register("rules", "試合のルールの今の状態 (サボタージュの長さ・止めた端末・ベント・マップ)", (_, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            var role = lp && lp.Data != null ? lp.Data.Role : null;
            var o = GameOptionsManager.Instance?.CurrentGameOptions;
            reply($"OK rules map={MapRules.MapId} speed={(o != null ? o.GetFloat(AmongUs.GameOptions.FloatOptionNames.PlayerSpeedMod) : -1)} devices {Devices.Describe()} sabotage{SabotageRules.Describe()} canVent={(role ? role.CanVent : false)} extraKill={MapRules.ExtraKillSeconds()} pos=({(lp ? lp.transform.position.x : 0):0.##},{(lp ? lp.transform.position.y : 0):0.##}) platform={PlatformPatch.Off}");
        });

        TestBridge.Register("sab", "<SystemTypes の名前 (Comms など)> | map  サボタージュを本編の道で起こす (ホストが受けて判定する) / サボタージュの地図を開く", (args, reply) =>
        {
            var hud = Vanilla.Hud;
            if (!ShipStatus.Instance || !hud) { reply("ERR sab"); return; }
            string a = args.Trim();
            if (a == "map")
            {
                hud.ToggleMapVisible(new MapOptions { Mode = MapOptions.Modes.Sabotage });
                reply("OK sab map");
                return;
            }
            if (!System.Enum.TryParse(a, out SystemTypes t)) { reply($"ERR sab {a}"); return; }
            ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Sabotage, (byte)t);
            reply($"OK sab {t}");
        });

        TestBridge.Register("aftermeeting", "会議が終わった時と同じ処理を通す (会議の画面が無いので 0.2 秒後に走る)", (_, reply) =>
        {
            RulesHooks.OnMeeting();
            reply("OK aftermeeting");
        });

        TestBridge.Register("decon", "除染室の時間 (Mira HQ・Polus)", (_, reply) =>
        {
            var sb = new StringBuilder("OK decon");
            foreach (var d in Object.FindObjectsOfType<DeconSystem>()) sb.Append($" [{d.name} decon={d.DeconTime} door={d.DoorOpenTime}]");
            reply(sb.ToString());
        });

        TestBridge.Register("ladder", "いちばん近いはしごを本編の道で使う (下りなら落下の判定に入る)", (_, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || !ShipStatus.Instance) { reply("ERR ladder"); return; }
            Ladder best = null;
            float bd = float.MaxValue;
            var me = lp.transform.position;
            foreach (var l in Object.FindObjectsOfType<Ladder>())
            {
                float d = (l.transform.position - me).sqrMagnitude;
                if (d < bd) { bd = d; best = l; }
            }
            if (!best) { reply("ERR ladder none"); return; }
            lp.MyPhysics.RpcClimbLadder(best);
            reply($"OK ladder top={best.IsTop} at=({best.transform.position.x:0.##},{best.transform.position.y:0.##}) to=({best.Destination.transform.position.x:0.##},{best.Destination.transform.position.y:0.##})");
        });
    }
}
