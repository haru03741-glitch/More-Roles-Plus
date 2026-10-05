using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MoreRolesPlus.Bridge;

// ブリッジの基本コマンド (どの機能にも依存しない観測・操作)
internal static class BuiltinCommands
{
    public static void RegisterAll()
    {
        TestBridge.Register("ping", "生存確認", (_, reply) => reply("OK ping"));

        TestBridge.Register("state", "bridge-state.json を今すぐ書き直す", (_, reply) =>
        {
            TestBridge.WriteState();
            reply($"OK state (phase={TestBridge.Phase()})");
        });

        TestBridge.Register("patches", "この mod が当てた Harmony パッチの一覧 (無音で外れていないかの確認)", (_, reply) =>
        {
            int n = 0;
            foreach (var m in Plugin.Harmony.GetPatchedMethods())
            {
                reply($"PATCH {m.DeclaringType?.Name}.{m.Name}");
                n++;
            }
            reply($"OK patches n={n}");
        });

        TestBridge.Register("remote", "MRP の電文の一覧 (番号・名前・送った/受けた/捨てた数) と指紋", (_, reply) =>
        {
            foreach (var c in Net.Remote.All) reply($"CALL {c.Id} {c.Name} {c.Route} sent={c.Sent} recv={c.Received} drop={c.Dropped}");
            reply($"OK remote n={Net.Remote.All.Count} fp={Options.Registry.Fingerprint:X8}");
        });

        TestBridge.Register("events", "試合のイベントごとの受け手の数 (寿命が生きている物) と起きた回数", (_, reply) =>
        {
            foreach (var s in Roles.EventStats.All) reply($"EVENT {s.Name} live={s.Live} fired={s.Fired}");
            reply($"OK events n={Roles.EventStats.All.Count} roles={Roles.RoleState.All.Count}");
        });

        TestBridge.Register("mark","<文字列> ログに目印を書く (wait marker の起点用)", (args, reply) =>
        {
            Plugin.Logger.LogInfo($"MARK {args}");
            reply($"OK mark {args}");
        });

        TestBridge.Register("shot", "[名前] スクショを Screens/ へ保存", (args, reply) =>
        {
            string name = string.IsNullOrWhiteSpace(args) ? "shot" : Regex.Replace(args, @"[^A-Za-z0-9_-]", "_");
            string path = Path.Combine(TestBridge.ScreensDir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{name}.png");
            ScreenCapture.CaptureScreenshot(path);
            reply($"OK shot {path}");
        });

        TestBridge.Register("errors", "[件数] 直近のエラー", (args, reply) =>
        {
            int n = int.TryParse(args, out int v) ? Math.Clamp(v, 1, 200) : 20;
            foreach (string e in BridgeLog.RecentErrors(n)) reply($"ERRLOG {e}");
            reply($"OK errors total={BridgeLog.ErrorsTotal}");
        });

        TestBridge.Register("grep", "<正規表現> [件数] 直近のログを検索", (args, reply) =>
        {
            string[] parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { reply("ERR grep needs a pattern"); return; }
            int n = parts.Length > 1 && int.TryParse(parts[1], out int v) ? Math.Clamp(v, 1, 200) : 30;
            var hits = BridgeLog.Grep(new Regex(parts[0]), n);
            foreach (string h in hits) reply($"HIT {h}");
            reply($"OK grep hits={hits.Count}");
        });

        TestBridge.Register("tp", "<x> <y> 自分を指定座標へ移動", (args, reply) =>
        {
            string[] p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                              || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            { reply("ERR tp needs <x> <y>"); return; }

            var lp = PlayerControl.LocalPlayer;
            if (!lp) { reply("ERR tp no local player"); return; }
            // 手元だけ動かす (通信しない)。同じ sequence id だと古い位置として捨てられるので進めておく
            var nt = lp.NetTransform;
            nt.SnapTo(new Vector2(x, y), (ushort)(nt.lastSequenceId + 328));
            reply($"OK tp {TestBridge.F(x)} {TestBridge.F(y)}");
        });

        TestBridge.Register("walk", "<x> <y> [秒=4] 自分を物理で歩かせる (壁に当たれば止まる)。時間切れか到着で最後の位置を出す", (args, reply) =>
        {
            string[] p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                              || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            { reply("ERR walk needs <x> <y> [sec]"); return; }
            float sec = p.Length > 2 && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float s) ? s : 4f;
            if (!PlayerControl.LocalPlayer) { reply("ERR walk no local player"); return; }
            WalkPatch.Start(new Vector2(x, y), sec);
            reply($"OK walk start -> {TestBridge.F(x)} {TestBridge.F(y)}");
        });

        TestBridge.Register("freeplay", "[マップ番号 0=Skeld 1=Mira 2=Polus 4=Airship 5=Fungle] メニューからフリープレイを始める", (args, reply) =>
        {
            byte map = byte.TryParse(args, out byte m) ? m : (byte)0;
            if (TestBridge.Phase() != "Menu") { reply($"ERR freeplay menu not ready (phase={TestBridge.Phase()}, follow: wait phase=Menu)"); return; }

            var btn = UnityEngine.Object.FindObjectOfType<HostLocalGameButton>(true);
            if (!btn) { reply("ERR freeplay HostLocalGameButton not found"); return; }

            // OnClick は自分の GameObject でコルーチンを回すので、閉じたままのフリープレイ画面を開いておく
            for (var t = btn.transform; t; t = t.parent)
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);

            AmongUsClient.Instance.TutorialMapId = map;
            btn.NetworkMode = NetworkModes.FreePlay;
            btn.OnClick();
            reply($"OK freeplay map={map} requested (follow with: wait phase=InGame 60)");
        });

        TestBridge.Register("quitgame", "フリープレイ/試合を抜けてメニューへ", (_, reply) =>
        {
            if (TestBridge.Phase() is "Menu" or "Boot") { reply("ERR quitgame already at Menu"); return; }
            AmongUsClient.Instance.ExitGame(DisconnectReasons.ExitGame);
            reply("OK quitgame requested (follow with: wait phase=Menu 30)");
        });
    }
}

// テスト用の歩行: 自分の物理更新の後で歩く向きを上書きする (本編の歩行と同じく当たり判定に止められる)
[HarmonyLib.HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
internal static class WalkPatch
{
    private static bool _active;
    private static Vector2 _target;
    private static float _left;

    public static void Start(Vector2 target, float sec)
    {
        _target = target;
        _left = sec;
        _active = true;
    }

    public static void Postfix(PlayerPhysics __instance)
    {
        if (!_active) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || __instance.myPlayer != lp) return;
        Vector2 pos = lp.GetTruePosition();
        Vector2 d = _target - pos;
        _left -= Time.fixedDeltaTime;
        bool arrived = d.sqrMagnitude < 0.02f;
        if (arrived || _left <= 0f)
        {
            _active = false;
            __instance.SetNormalizedVelocity(Vector2.zero);
            TestBridge.Out($"OK walk {(arrived ? "arrived" : "timeout")} at {TestBridge.F(pos.x)} {TestBridge.F(pos.y)}");
            return;
        }
        __instance.SetNormalizedVelocity(d.normalized);
    }
}
