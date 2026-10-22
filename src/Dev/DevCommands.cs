using System;
using System.Globalization;
using System.Linq;
using AmongUs.GameOptions;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// 開発者コンソールとテスト用ブリッジで使う操作。どちらも同じ表 (TestBridge.Run) から呼ぶ。
// 他の人に効く操作はホストだけが行い、結果は本編の RPC か HostToAll の電文で全員に届ける
// (他の人の手元のビルドには開発者の名簿が無いので、送り主が開発者かを受け手は確かめられない)。
internal static class DevCommands
{
    private static readonly System.Collections.Generic.List<(string Kind, byte Id, int Value)> _tallyRules = new();
    private static Lifespan _tallyLife;
    private static readonly RemoteCall<byte> ReviveCall = new("Dev.Revive", Route.HostToAll,
        (w, pid) => w.Write(pid), r => r.ReadByte(), (_, pid) => ReviveLocal(pid));

    public static void Register()
    {
        RegisterPlayerOps();
        RegisterDummy();
        RegisterInspect();
        RegisterChatAndUi();
    }

    // 人の操作 (移動・役職・生死・勝ち)
    private static void RegisterPlayerOps()
    {
        TestBridge.Register("tp", "<x> <y> | <番号> 自分を座標かその人の所へ移動 (オンラインでは全員に届く)", (args, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp) { reply("ERR tp no local player"); return; }
            string[] p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Vector2 to;
            if (p.Length == 1 && byte.TryParse(p[0], out byte pid))
            {
                var t = Player(pid);
                if (!t) { reply($"ERR tp no player {pid}"); return; }
                to = t.GetTruePosition();
            }
            else if (p.Length >= 2 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                                   && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                to = new Vector2(x, y);
            else { reply("ERR tp needs <x> <y> or <番号>"); return; }

            var nt = lp.NetTransform;
            if (AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay)
                // 手元だけ動かす。同じ sequence id だと古い位置として捨てられるので進めておく
                nt.SnapTo(to, (ushort)(nt.lastSequenceId + 328));
            else
                nt.RpcSnapTo(to);
            reply($"OK tp {TestBridge.F(to.x)} {TestBridge.F(to.y)}");
        });

        TestBridge.Register("setrole", "<役職Id> [番号] その人 (省略で自分) に役職を付けて全員に配る (ホストのみ。土台の役職も合わせる)", GiveRole);
        TestBridge.Register("giverole", "setrole と同じ", GiveRole);
        TestBridge.Register("setaddon", "<アドオンId> [番号] その人 (省略で自分) にアドオンを足して全員に配る (ホストのみ。役職が無い人にはアドオンだけ付く)", GiveAddon);
        TestBridge.Register("clearaddons", "[番号] その人 (省略で自分) のアドオンを全部外す (ホストのみ。役職はそのまま)", ClearAddons);
        TestBridge.Register("assignsim", "<人数> [回数=1000] [種=1] [インポスター数=2] [番号:役職Id=固定指定] 今の設定で配り方を何度も回し、役職・アドオンごとの付いた回数を出す (本編の物は読まない)", (args, reply) =>
        {
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length < 1 || !int.TryParse(a[0], out int players)) { reply("ERR assignsim <players> [runs] [seed] [impostors]"); return; }
            int runs = a.Length > 1 && int.TryParse(a[1], out int r) ? Math.Clamp(r, 1, 100000) : 1000;
            int seed = a.Length > 2 && int.TryParse(a[2], out int sd) ? sd : 1;
            int imps = a.Length > 3 && int.TryParse(a[3], out int im) ? im : Math.Min(2, players);
            // 5 つ目 = 固定指定 "<番号>:<役職Id>" (例 3:Jester)
            System.Collections.Generic.Dictionary<byte, int> reserved = null;
            if (a.Length > 4)
            {
                var kv = a[4].Split(':');
                int ri = kv.Length == 2 ? Registry.Roles.FindIndex(x => string.Equals(x.Id, kv[1], StringComparison.OrdinalIgnoreCase)) : -1;
                if (ri < 0 || !byte.TryParse(kv[0], out byte rp)) { reply("ERR assignsim reserve needs <番号>:<役職Id>"); return; }
                reserved = new System.Collections.Generic.Dictionary<byte, int> { [rp] = ri };
            }
            foreach (var line in AssignPlan.Simulate(Math.Clamp(players, 1, Crowded.MaxPlayers), Math.Clamp(imps, 0, players), runs, seed, reserved).Split('\n')) reply("SIM " + line);
            reply("OK assignsim");
        });

        TestBridge.Register("kill", "<番号> その人を倒す (ホストのみ)", (args, reply) =>
        {
            if (!AmongUsClient.Instance.AmHost || !byte.TryParse(args.Trim(), out byte pid)) { reply("ERR kill needs host and number"); return; }
            var p = Player(pid);
            if (!p) { reply($"ERR kill no player {pid}"); return; }
            p.RpcMurderPlayer(p, true);
            reply($"OK kill {pid}");
        });

        TestBridge.Register("revive", "[番号] その人 (省略で自分) を生き返らせる (ホストのみ)", (args, reply) =>
        {
            if (!AmongUsClient.Instance.AmHost || !GameData.Instance) { reply("ERR revive needs host in game"); return; }
            byte pid = byte.TryParse(args.Trim(), out byte v) ? v : PlayerControl.LocalPlayer.PlayerId;
            var p = Player(pid);
            if (!p || p.Data == null) { reply($"ERR revive no player {pid}"); return; }
            if (!p.Data.IsDead) { reply($"ERR revive {pid} is alive"); return; }
            Revive(pid);
            reply($"OK revive {pid}");
        });

        TestBridge.Register("forcewin", "<crew|imp|役職Id> その勝ちで試合を終わらせる (ホストのみ)", (args, reply) =>
        {
            if (!AmongUsClient.Instance.AmHost || !GameData.Instance) { reply("ERR forcewin needs host in game"); return; }
            string a = args.Trim();
            if (a == "crew") GameEnd.End(GameEnd.TeamResult(Team.Crew), GameOverReason.CrewmatesByVote);
            else if (a == "imp") GameEnd.End(GameEnd.TeamResult(Team.Impostor), GameOverReason.ImpostorsByKill);
            else
            {
                var role = RoleState.All.FirstOrDefault(r => string.Equals(r.Id, a, StringComparison.OrdinalIgnoreCase));
                if (role == null) { reply($"ERR forcewin nobody has {a}"); return; }
                GameEnd.Win(role);
            }
            reply($"OK forcewin {a}");
        });
    }

    // ダミー (出す・歩かせる・自動行動・乗り移り・行動)
    private static void RegisterDummy()
    {
        TestBridge.Register("dummy", "[人数=1] 自分の所にダミーを出す (ホストのみ・公式サーバーでは不可)", (args, reply) =>
        {
            var client = AmongUsClient.Instance;
            if (ServerKind.IsOfficial() || !client.AmHost || !GameData.Instance || !PlayerControl.LocalPlayer)
            { reply("ERR dummy needs host of a non-official game"); return; }
            int n = int.TryParse(args.Trim(), out int c) ? Math.Clamp(c, 1, Crowded.MaxPlayers - 1) : 1;
            int made = 0;
            for (int i = 0; i < n; i++)
            {
                if (GameData.Instance.GetAvailableId() < 0) break;
                Dummy.Spawn(made);
                made++;
            }
            reply($"OK dummy spawned={made}");
        });

        TestBridge.Register("dwalk", "<番号> <x> <y> ダミーをそこへ歩かせる (ホストのみ・まっすぐ進む)", (args, reply) =>
        {
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            byte pid = 0;
            var p = a.Length >= 3 && byte.TryParse(a[0], out pid) ? Player(pid) : null;
            if (!AmongUsClient.Instance.AmHost || !p || !p.isDummy
                || !float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            { reply("ERR dwalk <dummy id> <x> <y> (host only)"); return; }
            Dummy.WalkTo(p, x, y);
            reply($"OK dwalk {pid} -> {TestBridge.F(x)},{TestBridge.F(y)}");
        });

        TestBridge.Register("dauto", "[on|off|default] ダミーの自動行動 (既定 = オンラインの部屋だけ動く)", (args, reply) =>
        {
            switch (args.Trim())
            {
                case "on": DummyBrain.Forced = true; break;
                case "off": DummyBrain.Forced = false; break;
                case "default": DummyBrain.Forced = null; break;
            }
            reply($"OK dauto forced={(DummyBrain.Forced?.ToString() ?? "default")} {DummyBrain.Describe()}");
        });

        TestBridge.Register("dpossess", "[番号|off|stick <x> <y>] ホストがダミーに乗り移る (省略 = 今の状態・stick = 実の入力の代わりの値 (引数なしで戻す)) (歩く入力・カメラ・視界がダミーへ。PC は F6 で次のダミー・F7 キル・F8 通報)", (args, reply) =>
        {
            var a = args.Trim();
            if (a.Length == 0) { reply($"OK dpossess {DummyPilot.Describe()}"); return; }
            if (a == "off") { DummyPilot.Reset(); reply("OK dpossess off"); return; }
            if (a.StartsWith("stick", StringComparison.Ordinal))
            {
                var sp = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                DummyPilot.FakeStick = sp.Length == 3 && float.TryParse(sp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float sx)
                    && float.TryParse(sp[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float sy) ? new Vector2(sx, sy) : null;
                reply($"OK dpossess stick={(DummyPilot.FakeStick?.ToString() ?? "real")}");
                return;
            }
            var p = byte.TryParse(a, out byte pid) ? Player(pid) : null;
            string why = DummyPilot.Possess(p);
            reply(why == null ? $"OK dpossess {pid}" : $"ERR dpossess {why}");
        });

        TestBridge.Register("dact", "<番号> kill <相手> | report [死体の番号] | vote <相手|skip>  ダミーに行動させる (ホストのみ)", (args, reply) =>
        {
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var d = a.Length >= 2 && byte.TryParse(a[0], out byte did) ? Player(did) : null;
            if (!AmongUsClient.Instance.AmHost || !d || !d.isDummy) { reply("ERR dact <dummy id> <kill|report|vote> ... (host only)"); return; }
            switch (a[1])
            {
                case "kill":
                    var t = a.Length >= 3 && byte.TryParse(a[2], out byte tid) ? Player(tid) : null;
                    string why = t ? Dummy.Kill(d, t) : "target";
                    reply(why == null ? "OK dact kill sent" : $"ERR dact kill {why}");
                    return;
                case "report":
                    var body = a.Length >= 3 && byte.TryParse(a[2], out byte bid) ? GameData.Instance.GetPlayerById(bid) : null;
                    Dummy.Report(d, body);
                    reply("OK dact report");
                    return;
                case "vote":
                    byte v = a.Length >= 3 && a[2] != "skip" && byte.TryParse(a[2], out byte vid) ? vid : (byte)253;
                    Dummy.Vote(d, v);
                    reply($"OK dact vote {v}");
                    return;
            }
            reply("ERR dact unknown action");
        });
    }

    // 人と同期物の中身を見る
    private static void RegisterInspect()
    {
        TestBridge.Register("god", "[0|1] 全員の役職を名前の上と会議で見る (自分の画面だけ)", (args, reply) =>
        {
            string a = args.Trim();
            bool on = a == "1" || (a != "0" && !DevGod.On);
            DevGod.Set(on);
            reply($"OK god {(on ? 1 : 0)}");
        });

        TestBridge.Register("pinfo", "<番号> その人の表示の状態 (見えているか・体の絵・位置)", (args, reply) =>
        {
            var p = byte.TryParse(args.Trim(), out byte pid) ? Player(pid) : null;
            if (!p) { reply($"ERR pinfo no player {args}"); return; }
            var body = p.cosmetics ? p.cosmetics.currentBodySprite : null;
            var pos = p.transform.position;
            reply($"OK pinfo {pid} active={p.gameObject.activeInHierarchy} visible={p.Visible} body={(body != null && body.BodySprite ? body.BodySprite.enabled.ToString() : "-")} pos={TestBridge.F(pos.x)},{TestBridge.F(pos.y)},{TestBridge.F(pos.z)} layer={p.gameObject.layer} dummy={p.isDummy} netT={p.NetTransform.enabled} incomplete={(p.Data != null ? p.Data.IsIncomplete.ToString() : "nodata")} client={(p.Data != null ? p.Data.ClientId : -9)} name='{(p.cosmetics ? p.cosmetics.nameText.text : "-")}'");
        });

        TestBridge.Register("votecodes", "本編の投票の印の番号 (この番号の人は投票の的にできない) と会議などのページ送りの状態", (_, reply) =>
            reply($"OK votecodes hasNotVoted={PlayerVoteArea.HasNotVoted} missed={PlayerVoteArea.MissedVote} skipped={PlayerVoteArea.SkippedVote} dead={PlayerVoteArea.DeadVote} page={CrowdPages.Page + 1}/{CrowdPages.Pages}{(MeetingHud.Instance ? $" timer={MeetingHud.Instance.TimerText.transform.localPosition} skip={MeetingHud.Instance.SkipVoteButton.transform.localPosition} origin={MeetingHud.Instance.VoteOrigin} offs={MeetingHud.Instance.VoteButtonOffsets}" : "")}"));

        TestBridge.Register("openmg", "<vitals|shift|close> バイタルの画面を開く (船にある時) / 本編の能力ボタンを押す (変身役なら相手選びが開く)", (args, reply) =>
        {
            var a = args.Trim();
            if (a == "close")
            {
                if (Minigame.Instance) Minigame.Instance.Close();
                reply("OK openmg close");
                return;
            }
            if (a == "shift")
            {
                var hud = Vanilla.Hud;
                if (!hud || !hud.AbilityButton) { reply("ERR openmg no ability button"); return; }
                hud.AbilityButton.DoClick();
                reply("OK openmg shift");
                return;
            }
            foreach (var c in UnityEngine.Object.FindObjectsOfType<SystemConsole>())
            {
                if (!c.MinigamePrefab || !c.MinigamePrefab.TryCast<VitalsMinigame>()) continue;
                var mg = UnityEngine.Object.Instantiate(c.MinigamePrefab, Camera.main.transform, false);
                mg.transform.localPosition = new Vector3(0f, 0f, -50f);
                mg.Begin(null);
                reply("OK openmg vitals");
                return;
            }
            reply("ERR openmg no vitals console on this map");
        });

        TestBridge.Register("pageturn", "<+1|-1> [whisper] 会議の名札などのページを送る (whisper = ロビーの耳打ちの一覧)", (args, reply) =>
        {
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int d = a.Length > 0 && int.TryParse(a[0], out int v) ? v : 1;
            if (a.Length > 1 && a[1] == "whisper") { Commands.WhisperList.Turn(d); reply("OK pageturn whisper"); return; }
            CrowdPages.Turn(d);
            reply($"OK pageturn page={CrowdPages.Page + 1}/{CrowdPages.Pages}");
        });

        TestBridge.Register("netobjs", "通信で同期している物の一覧 (種類・NetId・持ち主・人の番号)", (args, reply) =>
        {
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var o in AmongUsClient.Instance.allObjects.allObjects)
            {
                if (!o) continue;
                n++;
                var info = o.TryCast<NetworkedPlayerInfo>();
                var pc = o.TryCast<PlayerControl>();
                sb.Append($" {o.GetIl2CppType().Name}#{o.NetId}/o{o.OwnerId}");
                if (info) sb.Append($"/p{info.PlayerId}/c{info.ClientId}");
                if (pc) sb.Append($"/p{pc.PlayerId}");
            }
            reply($"OK netobjs n={n} players={(GameData.Instance ? GameData.Instance.AllPlayers.Count : -1)}{sb}");
        });

        TestBridge.Register("poutfit", "<番号> その人の見た目のデータ", (args, reply) =>
        {
            var d = byte.TryParse(args.Trim(), out byte pid) && GameData.Instance ? GameData.Instance.GetPlayerById(pid) : null;
            if (d == null) { reply($"ERR poutfit no player {args}"); return; }
            foreach (var kv in d.Outfits)
            {
                var o = kv.Value;
                var sb = new System.Text.StringBuilder();
                foreach (var pr in o.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (pr.GetIndexParameters().Length > 0 || pr.Name is "Pointer" or "ObjectClass" or "WasCollected") continue;
                    object v;
                    try { v = pr.GetValue(o); } catch { v = "?"; }
                    sb.Append(' ').Append(pr.Name).Append('=').Append(v);
                }
                reply($"OUTFIT {kv.Key}{sb}");
            }
            var sd = new System.Text.StringBuilder();
            foreach (var pr in d.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (pr.GetIndexParameters().Length > 0 || pr.Name is "Pointer" or "ObjectClass" or "WasCollected") continue;
                object v;
                try { v = pr.GetValue(d); } catch { v = "?"; }
                if (v != null && pr.PropertyType != typeof(string) && !pr.PropertyType.IsPrimitive && !pr.PropertyType.IsEnum && !(v is string)) v = "obj";
                sd.Append(' ').Append(pr.Name).Append('=').Append(v);
            }
            reply($"DATA{sd}");
            reply($"OK poutfit {pid} incomplete={d.IsIncomplete}");
        });
    }

    // チャット・画面・機能の状態
    private static void RegisterChatAndUi()
    {
        TestBridge.Register("console", "<0|1|行> 開発者コンソールを開け閉めする / 行を打ち込む (画面の確認用)", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "0" || a == "1") DevConsole.BridgeOpen(a == "1");
            else DevConsole.BridgeType(a);
            reply($"OK console open={DevConsole.IsOpen}");
        });

        TestBridge.Register("chat", "[0|1] 試合中もチャット欄を出す / 隠す (自分の画面だけ。/ を付けたコマンドもここから打てる)", (args, reply) =>
        {
            var hud = Vanilla.Hud;
            if (!hud || !hud.Chat) { reply("ERR chat no hud"); return; }
            string a = args.Trim();
            bool on = a == "1" || (a != "0" && !hud.Chat.gameObject.activeSelf);
            DevChat.SetShown(on);
            reply($"OK chat {(on ? 1 : 0)}");
        });

        TestBridge.Register("chatsend", "<文> チャット欄に文を入れて送信を押したのと同じにする (チャットからのコマンドの確認用)", (args, reply) =>
        {
            var hud = Vanilla.Hud;
            if (!hud || !hud.Chat) { reply("ERR chatsend no hud"); return; }
            hud.Chat.freeChatField.textArea.SetText(args, "");
            hud.Chat.SendChat();
            reply($"OK chatsend left=[{hud.Chat.freeChatField.Text}]");
        });

        TestBridge.Register("chatraw", "<文> 自分の発言として全員へ普通のチャットを送る (コマンドとして拾わない・荒らし対策の確認用)", (args, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp) { reply("ERR chatraw no player"); return; }
            lp.RpcSendChat(args);
            reply("OK chatraw");
        });

        TestBridge.Register("presetui", "[name <名前> | <ボタンの番号>] 設定画面のプリセットのタブ (0 保存・1 取り込む・2 フォルダ・3 前・4 次・100+行*4+{0 読む,1 書き出す,2 消す})", (args, reply) => reply(Options.PresetPage.Simulate(args.Trim())));
        TestBridge.Register("endsound", "鳴っている音の一覧 (終わりの画面の曲の確認用)", (_, reply) =>
        {
            var sb = new System.Text.StringBuilder();
            foreach (var src in UnityEngine.Object.FindObjectsOfType<UnityEngine.AudioSource>())
                if (src && src.isPlaying && src.clip) sb.Append(' ').Append(src.clip.name).Append('@').Append(src.volume.ToString("0.00"));
            reply($"OK endsound playing:{sb}");
        });
        TestBridge.Register("spectate", "[self] 観戦の状態 (自分の選択・ホストが受けた人・この試合の観戦の人) / self = 試合中の自分を観戦の人にする", (args, reply) =>
        {
            if (args.Trim() == "self") Roles.Spectate.WatchSelfForTest();
            reply(Roles.Spectate.Describe());
        });
        TestBridge.Register("matchinfo", "[open | close | click | tab <n> | scroll <y> | dump] 試合の情報 (右上の「?」) を開く・閉じる・ボタンを押す・タブを選ぶ (0 = 人・1 = 設定・2 = 役職)・欄を送る・寸法 / 状態", (args, reply) => reply(Menu.MatchInfoExtension.Simulate(args.Trim())));

        TestBridge.Register("teamchat", "[0|1] 陣営チャットのボタンを押したのと同じ (0/1 で会議中の切り替えを直に決める) / 状態", (args, reply) =>
        {
            string a = args.Trim();
            if (a.Length > 0) Commands.TeamChat.BridgeSet(a == "1" ? true : a == "0" ? false : null);
            reply(Commands.TeamChat.Describe());
        });

        TestBridge.Register("whisper", "[<番号> | list] 耳打ちの相手を選ぶ (名札・一覧のボタンと同じ) / ロビーの一覧を開く / 状態", (args, reply) =>
        {
            args = args.Trim();
            if (args == "list") Commands.WhisperList.Toggle();
            else if (byte.TryParse(args, out byte id)) Commands.Whisper.Choose(id);
            reply(Commands.Whisper.Describe());
        });
        TestBridge.Register("rejoin", "自動で部屋へ戻る機能の状態 (終わりの画面の数え・入り直しの待ち)", (_, reply) => reply(Menu.AutoRejoin.Describe()));
        TestBridge.Register("f1", "試合中の設定の一覧 (F1) を出す / 消す", (_, reply) =>
        {
            Menu.SettingsOverlay.Toggle();
            reply($"OK f1 open={Menu.SettingsOverlay.IsOpen}");
        });
        TestBridge.Register("afk", "放置の数え (設定・止まっている秒数・知らせたか)", (_, reply) => reply(Commands.AfkGuard.Describe()));
        TestBridge.Register("colors", "[fake <番号> <#RRGGBB> [#RRGGBB [向き 0-3 [ぼかし 0-100 [位置 0-100]]]]] 自分で作った色 (#RRGGBB) の一覧 (自分の保存値・届いている人の番号=色・[色番号:色の名前]) / fake = その人の色が届いた事にする (ダミー用) / check <色番号> [人の番号] = 自分の色をその番号で頼む (ホストは番号の人に、その色を名乗らせてから頼ませる = 入ってきたばかりの人と同じ) (本編の色選びと同じ・空いていなければホストが替える)・人ごとの色番号と名前", (args, reply) =>
        {
            args = args.Trim();
            if (args.StartsWith("check "))
            {
                var cp = args[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var me = PlayerControl.LocalPlayer;
                if (!me || cp.Length == 0 || !byte.TryParse(cp[0], out byte want)) { reply("ERR colors check <色番号> [人の番号 (ホストの時・ダミー用)]"); return; }
                if (cp.Length >= 2 && byte.TryParse(cp[1], out byte pid) && AmongUsClient.Instance.AmHost)
                {
                    var who = GameData.Instance ? GameData.Instance.GetPlayerById(pid) : null;
                    if (who == null || !who.Object) { reply($"ERR colors check: no player {pid}"); return; }
                    // 入ってきたばかりの人と同じく、自分の色の空きを持たない状態から頼ませる
                    who.Object.SetColor(want);
                    who.Object.CheckColor(want);
                }
                else me.CmdCheckColor(want);
                reply($"OK colors check {want}");
                return;
            }
            reply(args.StartsWith("fake ") ? Menu.CustomColors.Fake(args[5..]) : Menu.CustomColors.Describe());
        });
        TestBridge.Register("picker", "[equip | type <文> | #RRGGBB | swatch1 | swatch2 | dir | soft <0-100> | pos <0-100>] 着せ替え画面の色のタブの「色を作る」(色を置く・装備を押す・#hex 欄に打つ) と今の色", (args, reply) => reply(Menu.ColorPicker.Simulate(args.Trim())));
        TestBridge.Register("fx", "<範囲 (self+team+target+nearby+dead / all)> [起こした人の番号=自分] [相手の番号=255] [meeting|comms] 演出の見える範囲を試す: 部屋の全員について見えるか (場所 = 自分の足元・半径 5)", (args, reply) =>
        {
            var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { reply("ERR fx <範囲> [actor] [target] [meeting|comms]"); return; }
            var a = Roles.Audience.None;
            foreach (var w in parts[0].Split('+'))
            {
                if (!Enum.TryParse<Roles.Audience>(w, true, out var bit)) { reply($"ERR fx unknown {w}"); return; }
                a |= bit;
            }
            byte actor = PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : (byte)0, target = 255;
            var hide = Roles.FxHide.None;
            if (parts.Length > 1) byte.TryParse(parts[1], out actor);
            if (parts.Length > 2) byte.TryParse(parts[2], out target);
            if (parts.Length > 3) Enum.TryParse(parts[3], true, out hide);
            reply(Roles.FxSignal.Matrix(a, hide, 5f, actor, target));
        });
        TestBridge.Register("custmenu", "[use | tab <番号> | chip <色番号> | close] 本編の着せ替え画面 (use = 右下の使うボタンを押す・先に tp でノートパソコンの横へ) を開く・タブを切り替える・本編の色を押す・閉じる / タブの一覧", (args, reply) => reply(Menu.ColorTab.Simulate(args.Trim())));
        TestBridge.Register("emote", "[ring | pick <0-3> | hold <0-3> <秒> | off | stats | <0-3> [番号]] ロビーと試合中のエモート (0=手を振る 1=跳ねる 2=拍手 3=手招き): 番号を付けるとその人から届いたのと同じ受け手の道・pick = 扇のボタンを本編の押下の道で押す・hold = その秒の姿で止める・off = 全部消す", (args, reply) => reply(Menu.Emotes.Command(args.Trim())));
        TestBridge.Register("stamp", "[menu | pick <0-7> | stats | <0-7> [番号]] 会議のスタンプ (0=怪しい 1=白 2=? 3=! 4=賛成 5=反対 6=待って 7=ごめん): 番号を付けるとその人から届いたのと同じ受け手の道・pick = 札のボタンを本編の押下の道で押す", (args, reply) => reply(Menu.MeetingStamps.Command(args.Trim())));
        TestBridge.Register("memo", "[meeting | open | line x1 y1 x2 y2 | mark <番号> x y | undo | clear] 会議の地図のメモ (自分だけ): 書いた線と印の数 / meeting = ホストの道で会議を始める・open = 会議中に地図を開く・座標はメモの層の中", (args, reply) => reply(Menu.MeetingMemo.Describe(args.Trim())));
        TestBridge.Register("update", "[state | check | fake <版> [url|file:<パス>] | press | popup | reset] メインメニューの更新ボタン: check = GitHub を見直す・fake = その版が出たことにする (file: なら手元の DLL で入れ替える)・press = ボタンを本編の押下の道で押す・reset = 知らせた版を忘れる", (args, reply) => reply(Menu.ModUpdate.Command(args.Trim())));
        TestBridge.Register("sabalert", "サボタージュの点滅と警報音 (端末ごとの設定) の状態・自前で回している数", (args, reply) => reply(Menu.SabotageAlert.Describe()));
        TestBridge.Register("client","[設定の名前 | menu | open | close] 端末ごとの設定 (Esc の「MRP の設定」) の一覧。名前を付けるとそれを切り替える・menu = 本編の設定画面を開く・open / close = MRP の設定の板を開く / 閉じる", (args, reply) => reply(Menu.ClientOptions.Describe(args.Trim())));
        TestBridge.Register("minigame", "[sys [名前の一部] | close] 自分の終わっていない最初のタスクの端末へ移って作業画面を開く / sys = 機器の端末 (カメラなど) を開く / close = 開いている作業画面を閉じる (待ち時間が作業中に進むかの確かめ用)", (args, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || !ShipStatus.Instance) { reply("ERR minigame not in game"); return; }
            string a = args.Trim();
            if (a == "close")
            {
                if (Minigame.Instance) Minigame.Instance.Close();
                reply("OK minigame closed");
                return;
            }
            if (a == "sys" || a.StartsWith("sys "))
            {
                string want = a.Length > 4 ? a[4..] : "";
                SystemConsole sc = null;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<SystemConsole>())
                    if (c && c.name.Contains(want, StringComparison.OrdinalIgnoreCase)) { sc = c; break; }
                if (!sc) { reply("ERR minigame no system console"); return; }
                lp.NetTransform.SnapTo(sc.transform.position);
                sc.Use();
                reply($"OK minigame console={sc.name}");
                return;
            }
            foreach (var task in lp.myTasks)
            {
                if (task == null || task.IsComplete) continue;
                var consoles = task.FindConsoles();
                if (consoles == null || consoles.Count == 0) continue;
                var c = consoles[0];
                lp.NetTransform.SnapTo(c.transform.position);
                c.Use();
                reply($"OK minigame task={task.TaskType} console={c.name} open={(Minigame.Instance ? Minigame.Instance.name : "-")}");
                return;
            }
            reply("ERR minigame no task with a console");
        });
        TestBridge.Register("shield", "[番号|255] 前の試合の最初の犠牲者の盾の状態 / その人に今すぐ盾を付ける (255 = 外す・ホストのみ全員へ)", (args, reply) => reply(Rules.VanillaTweaks.DevShield(args.Trim())));
        TestBridge.Register("nightcams", "停電中のカメラの暗視の状態", (_, reply) => reply(Rules.NightVisionCams.Describe()));
        TestBridge.Register("hudtweaks", "[scale | shot] 端末ごとの HUD の調整の状態。scale = ボタンの大きさを次へ・shot = スクリーンショットのキーと同じ", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "scale") Menu.HudTweaks.NextScale();
            else if (a == "shot") { reply("OK hudtweaks shot " + Menu.HudTweaks.Screenshot()); return; }
            reply(Menu.HudTweaks.Describe());
        });
        TestBridge.Register("chatin", "[tab | up | down | type <文> | paste <文>] チャットの入力欄の状態 (文字の制限・上限・今の文字・補う候補)。tab / paste は Tab・Ctrl+V と同じ処理 (paste の \n は改行)・type は打ったのと同じ", (args, reply) => reply(Commands.ChatInput.Simulate(args.Trim())));

        TestBridge.Register("chattoggle", "チャット欄の窓を開く / 閉じる (チャットのボタンを押したのと同じ)", (_, reply) =>
        {
            var hud = Vanilla.Hud;
            if (!hud || !hud.Chat) { reply("ERR chattoggle no hud"); return; }
            hud.Chat.Toggle();
            reply("OK chattoggle");
        });

        TestBridge.Register("gamemode", "[normal | hns] 本編のモードを替える (ホストのロビーのみ)・引数なしで今のモードと GameManager", (args, reply) =>
        {
            var gm = GameManager.Instance;
            string state = $"mode={Options.GameModeSwitch.Current} gm={(gm ? $"{gm.GetIl2CppType().Name}#{gm.NetId}" : "none")}";
            switch (args.Trim())
            {
                case "": reply($"OK gamemode {state}"); return;
                case "normal": reply($"OK gamemode {Options.GameModeSwitch.Switch(AmongUs.GameOptions.GameModes.Normal)}"); return;
                case "hns": reply($"OK gamemode {Options.GameModeSwitch.Switch(AmongUs.GameOptions.GameModes.HideNSeek)}"); return;
                default: reply("ERR gamemode [normal|hns]"); return;
            }
        });

        TestBridge.Register("modcheck", "[fakehost <版>] ホストから別の版の知らせが来た時と同じことをする (客のみ・版違いの文の確認用)・引数なしで /mods と同じ一覧", (args, reply) =>
        {
            var a = args.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (a.Length >= 2 && a[0] == "fakehost")
            {
                var client = AmongUsClient.Instance;
                PlayerControl host = null;
                foreach (var p in PlayerControl.AllPlayerControls)
                    if (p && client && p.OwnerId == client.HostId) host = p;
                if (!host || client.AmHost) { reply("ERR modcheck fakehost: guest only"); return; }
                Net.VersionCheck.OnHostHello(host, a[1], Options.Registry.Fingerprint, Native.MrpNative.Abi);
                reply("OK modcheck fakehost");
                return;
            }
            reply("OK modcheck " + Net.VersionCheck.ModsText("").Replace("\n", " | "));
        });

        TestBridge.Register("taskdone", "[数=1] 自分の終わっていないタスクを前から終わらせる (全員に送る)", (args, reply) =>
        {
            var me = PlayerControl.LocalPlayer;
            if (!me || me.myTasks == null) { reply("ERR taskdone no tasks"); return; }
            int want = int.TryParse(args.Trim(), out int n) ? n : 1, done = 0;
            foreach (var t in me.myTasks)
            {
                if (done >= want) break;
                if (!t || t.IsComplete || t.TryCast<NormalPlayerTask>() == null) continue;
                me.RpcCompleteTask(t.Id);
                done++;
            }
            reply($"OK taskdone {done}");
        });

        TestBridge.Register("airshipprobe", "[load | release] Airship の船を読み込んで湧き場所選びの画面の部品があるか・メモリがどれだけ増えるかを測る (確かめ用)", (args, reply) =>
        {
            static string Mem()
            {
                var pr = System.Diagnostics.Process.GetCurrentProcess();
                pr.Refresh();
                string s = $"ws={pr.WorkingSet64 >> 20}MB private={pr.PrivateMemorySize64 >> 20}MB boehm={Health.BoehmUsed() >> 20}MB";
#if !ANDROID
                s += $" unityAlloc={UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20}MB unityReserved={UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() >> 20}MB tex={Texture.currentTextureMemory >> 20}MB";
                var bundles = AssetBundle.GetAllLoadedAssetBundles_Native();
                int n = 0;
                var names = new System.Text.StringBuilder();
                foreach (var b in bundles)
                {
                    n++;
                    if (b && b.name.IndexOf("airship", StringComparison.OrdinalIgnoreCase) >= 0) names.Append(' ').Append(b.name);
                }
                s += $" bundles={n}{(names.Length > 0 ? " airship:" + names : "")}";
#endif
                return s;
            }
            var prefabs = AmongUsClient.Instance ? AmongUsClient.Instance.ShipPrefabs : null;
            if (prefabs == null || prefabs.Count <= 4) { reply("ERR airshipprobe no ship prefabs"); return; }
            var aref = prefabs[4];
            string before = Mem();
            if (args.Trim() == "bundles")
            {
#if !ANDROID
                foreach (var b in AssetBundle.GetAllLoadedAssetBundles_Native()) if (b) reply($"BUNDLE {b.name}");
#endif
                reply("OK airshipprobe bundles");
                return;
            }
            if (args.Trim() == "loadraw")
            {
                // 部品の包みを作らずに読み込んで、すぐ手放す (掴んでいるのが包みかどうかの切り分け)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var op = aref.LoadAssetAsync<GameObject>();
                op.WaitForCompletion();
                long ms = sw.ElapsedMilliseconds;
                aref.ReleaseAsset();
                reply($"OK airshipprobe loadraw {ms}ms before[{before}] after[{Mem()}]");
                return;
            }
            if (args.Trim() == "unloadgc")
            {
                // ゲーム側 (il2cpp) の GC も回してから掃除させる
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                Il2CppSystem.GC.Collect();
                UnityEngine.Resources.UnloadUnusedAssets();
                reply($"OK airshipprobe unloadgc requested before[{before}]");
                return;
            }
            if (args.Trim() == "unload")
            {
                // C# の包みの GC ハンドルを先に外してから、参照の無い物を掃除させる
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect();
                UnityEngine.Resources.UnloadUnusedAssets();
                reply($"OK airshipprobe unload requested before[{before}]");
                return;
            }
            if (args.Trim() == "release")
            {
                aref.ReleaseAsset();
                System.GC.Collect();
                UnityEngine.Resources.UnloadUnusedAssets();
                reply($"OK airshipprobe released before[{before}] after[{Mem()}]");
                return;
            }
            if (args.Trim() == "load")
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                GameObject go = aref.Asset ? aref.Asset.TryCast<GameObject>() : null;
                if (!go)
                {
                    var op = aref.LoadAssetAsync<GameObject>();
                    go = op.WaitForCompletion();
                }
                var ship = go ? go.GetComponent<AirshipStatus>() : null;
                var mg = ship ? ship.SpawnInGame : null;
                reply($"OK airshipprobe loaded in {sw.ElapsedMilliseconds}ms ship={(ship ? "yes" : "no")} minigame={(mg ? $"yes locations={mg.Locations.Length}" : "no")} before[{before}] after[{Mem()}]");
                return;
            }
            reply($"OK airshipprobe {Mem()} loaded={(aref.Asset ? "yes" : "no")}");
        });

        TestBridge.Register("spawnpick", "[press <左からの番号>] 湧く場所を選ぶ画面を今すぐ開く (湧く場所を選ぶ設定が入っていて Airship を読み込み済みの時) / 開いている画面のボタンを押す", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 2 && a[0] == "press" && int.TryParse(a[1], out int idx))
            {
                var mg = Minigame.Instance ? Minigame.Instance.TryCast<SpawnInMinigame>() : null;
                if (!mg) { reply("ERR spawnpick press: picker not open"); return; }
                var buttons = mg.LocationButtons.ToArray().Where(b => b).OrderBy(b => b.transform.localPosition.x).ToArray();
                if (idx < 0 || idx >= buttons.Length) { reply($"ERR spawnpick press: {buttons.Length} buttons"); return; }
                var t = buttons[idx].GetComponentInChildren<TMPro.TextMeshPro>();
                buttons[idx].OnClick.Invoke();
                reply($"OK spawnpick pressed '{(t ? t.text : "?")}'");
                return;
            }
            if (!Rules.SelectSpawn.Active) { reply("ERR spawnpick not active"); return; }
            Rules.SelectSpawn.Open();
            reply("OK spawnpick");
        });

        TestBridge.Register("meetinginfo", "会議の状態と時間 (投票の残り・結果の表示時間・進むボタン)", (_, reply) =>
        {
            var hud = MeetingHud.Instance;
            if (!hud) { reply("ERR meetinginfo no meeting"); return; }
            var opts = GameOptionsManager.Instance.CurrentGameOptions;
            reply($"OK meetinginfo state={hud.state} timer={hud.discussionTimer:0.0} discussion={opts.GetInt(Int32OptionNames.DiscussionTime)} voting={opts.GetInt(Int32OptionNames.VotingTime)} resultsTime={MeetingHud.ResultsTime:0.0} resultsStartedAt={hud.resultsStartedAt:0.0} now={Time.time:0.0} proceed={(hud.ProceedButton ? hud.ProceedButton.gameObject.activeInHierarchy.ToString() : "-")} overrules={hud.judgeOverrulesQueue?.Count ?? -1}");
        });

        TestBridge.Register("vtally", "[weight <番号> <票数> | proxy <番号> <相手|skip> | add <番号> <相手|skip> | clear] 次に票を数える時の書き換えを足す (ホストのみ)・省略で最後に数えた様子", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0) { reply($"OK vtally rules={_tallyRules.Count} last={Rules.VoteTally.Last}"); return; }
            if (a[0] == "clear")
            {
                _tallyRules.Clear();
                _tallyLife?.Release();
                _tallyLife = null;
                reply("OK vtally clear");
                return;
            }
            static byte Target(string s) => s == "skip" ? VoteTallyEvent.Skip : byte.TryParse(s, out byte b) ? b : (byte)255;
            if (a.Length != 3 || a[0] is not ("weight" or "proxy" or "add") || !byte.TryParse(a[1], out byte voter)
                || (a[0] == "weight" ? !int.TryParse(a[2], out _) : Target(a[2]) == 255))
            {
                reply("ERR vtally weight <id> <n> | proxy <id> <target|skip> | add <id> <target|skip> | clear");
                return;
            }
            if (!AmongUsClient.Instance.AmHost) { reply("ERR vtally host only"); return; }
            _tallyRules.Add((a[0], voter, a[0] == "weight" ? int.Parse(a[2]) : Target(a[2])));
            if (_tallyLife == null)
            {
                _tallyLife = new Lifespan();
                Events<VoteTallyEvent>.Subscribe(e =>
                {
                    foreach (var (kind, id, value) in _tallyRules)
                        switch (kind)
                        {
                            case "weight": e.SetWeight(id, value); break;
                            case "proxy": e.Redirect(id, (byte)value); break;
                            case "add": e.Add(id, (byte)value); break;
                        }
                }, _tallyLife, owner: "Dev.vtally");
            }
            reply($"OK vtally rules={_tallyRules.Count}");
        });

        TestBridge.Register("vset","<Int32OptionNames の名前> <値> 本編の数値の設定を変えて全員へ送る (ホストのみ・例 vset VotingTime 15)", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length != 2 || !Enum.TryParse(a[0], out Int32OptionNames key) || !int.TryParse(a[1], out int v)) { reply("ERR vset <name> <int>"); return; }
            if (!AmongUsClient.Instance.AmHost) { reply("ERR vset host only"); return; }
            GameOptionsManager.Instance.CurrentGameOptions.SetInt(key, v);
            if (GameManager.Instance) GameManager.Instance.LogicOptions.SyncOptions();
            reply($"OK vset {key}={GameOptionsManager.Instance.CurrentGameOptions.GetInt(key)}");
        });

        TestBridge.Register("votetimeout", "会議の投票の時間切れと同じこと (本編の ForceSkipAll) をする (ホストのみ)", (_, reply) =>
        {
            var hud = MeetingHud.Instance;
            if (!hud || !AmongUsClient.Instance.AmHost) { reply("ERR votetimeout host in meeting only"); return; }
            hud.ForceSkipAll();
            reply("OK votetimeout");
        });

        TestBridge.Register("players", "全員の番号・名前・生死・役職", (_, reply) =>
        {
            if (!GameData.Instance) { reply("ERR players no game"); return; }
            foreach (var d in GameData.Instance.AllPlayers)
            {
                if (d == null) continue;
                var role = RoleState.Of(d.PlayerId);
                reply($"PLAYER {d.PlayerId} {d.PlayerName} {(d.IsDead ? "dead" : "alive")} {(d.Role ? d.Role.Role.ToString() : "-")} {role?.Id ?? "-"}");
            }
            reply("OK players");
        });
    }

    private static void GiveRole(string args, Action<string> reply)
    {
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var proto = a.Length > 0 ? Registry.Roles.FirstOrDefault(r => string.Equals(r.Id, a[0], StringComparison.OrdinalIgnoreCase)) : null;
        if (proto == null) { reply($"ERR setrole unknown {args} (roles で一覧)"); return; }
        if (!PlayerControl.LocalPlayer || !AmongUsClient.Instance.AmHost) { reply("ERR setrole needs host in game"); return; }
        var target = a.Length > 1 && byte.TryParse(a[1], out byte pid) ? Player(pid) : PlayerControl.LocalPlayer;
        if (!target) { reply($"ERR setrole no player {a[1]}"); return; }
        RoleAssigner.Give(target, proto);
        reply($"OK setrole {proto.Id} to={target.PlayerId} base={target.Data.Role.Role} neutral={RoleState.AnyNeutral}");
    }

    private static void GiveAddon(string args, Action<string> reply)
    {
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var proto = a.Length > 0 ? Registry.Addons.FirstOrDefault(x => string.Equals(x.Id, a[0], StringComparison.OrdinalIgnoreCase)) : null;
        if (proto == null) { reply($"ERR setaddon unknown {args} (roles で一覧)"); return; }
        if (!PlayerControl.LocalPlayer || !AmongUsClient.Instance.AmHost || !GameData.Instance) { reply("ERR setaddon needs host in game"); return; }
        var target = a.Length > 1 && byte.TryParse(a[1], out byte pid) ? Player(pid) : PlayerControl.LocalPlayer;
        if (!target) { reply($"ERR setaddon no player {(a.Length > 1 ? a[1] : "")}"); return; }
        var list = RoleState.AddonsOf(target.PlayerId).Select(x => Registry.Addons.First(p => p.Id == x.Id)).ToList();
        if (!list.Contains(proto)) list.Add(proto);
        var role = RoleState.Of(target.PlayerId);
        var roleProto = role == null ? null : Registry.Roles.First(r => r.Id == role.Id);
        RoleAssigner.Give(target, roleProto, list);
        reply($"OK setaddon {string.Join("+", list.Select(x => x.Id))} to={target.PlayerId} role={role?.Id ?? "-"} fits={proto.CanAttach(role?.Team ?? (target.Data.Role && target.Data.Role.IsImpostor ? Team.Impostor : Team.Crew), role)}");
    }

    private static void ClearAddons(string args, Action<string> reply)
    {
        if (!PlayerControl.LocalPlayer || !AmongUsClient.Instance.AmHost || !GameData.Instance) { reply("ERR clearaddons needs host in game"); return; }
        var target = byte.TryParse(args.Trim(), out byte pid) ? Player(pid) : PlayerControl.LocalPlayer;
        if (!target) { reply($"ERR clearaddons no player {args}"); return; }
        var role = RoleState.Of(target.PlayerId);
        var roleProto = role == null ? null : Registry.Roles.First(r => r.Id == role.Id);
        RoleAssigner.Give(target, roleProto, new System.Collections.Generic.List<AddonBase>());
        reply($"OK clearaddons to={target.PlayerId} role={role?.Id ?? "-"}");
    }

    // 死んだ時に本編がタスク欄の先頭に足す「死亡した。…」の行を外す
    private static void RemoveGhostHint(PlayerControl p)
    {
        if (p.myTasks == null) return;
        var tc = TranslationController.Instance;
        string a = tc.GetString(StringNames.GhostDoTasks), b = tc.GetString(StringNames.GhostIgnoreTasks), c = tc.GetString(StringNames.GhostImpostor);
        for (int i = p.myTasks.Count - 1; i >= 0; i--)
        {
            var t = p.myTasks[i];
            var it = t ? t.TryCast<ImportantTextTask>() : null;
            if (it == null || it.Text == null) continue;
            if (it.Text.Contains(a) || it.Text.Contains(b) || it.Text.Contains(c))
            {
                p.myTasks.RemoveAt(i);
                UnityEngine.Object.Destroy(it.gameObject);
            }
        }
    }

    private static PlayerControl Player(byte pid)
        => GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;

    // ホスト: その人を生き返らせて全員に知らせる
    internal static void Revive(byte pid)
    {
        ReviveLocal(pid);
        ReviveCall.Send(pid);
    }

    // 生きていた時の役職 (NetworkedPlayerInfo.RoleWhenAlive)。interop の Nullable の読み出しは中身を取り違えるので、
    // 欄の中身 { bool hasValue; RoleTypes value; } を直接読む
    // 位置は初めて使う時に引く (本編の更新で名前が変わって引けなくても、役職を戻さないだけにする)
    private static IntPtr _roleWhenAliveField;
    private static int _hasValueAt = -1, _valueAt = -1;

    internal static unsafe RoleTypes? RoleWhenAlive(NetworkedPlayerInfo data)
    {
        if (_valueAt < 0)
        {
            _roleWhenAliveField = Il2CppInterop.Runtime.IL2CPP.GetIl2CppField(
                Il2CppInterop.Runtime.Il2CppClassPointerStore<NetworkedPlayerInfo>.NativeClassPtr, "RoleWhenAlive");
            if (_roleWhenAliveField == IntPtr.Zero) return null;
            var klass = Il2CppInterop.Runtime.Il2CppClassPointerStore<Il2CppSystem.Nullable<RoleTypes>>.NativeClassPtr;
            var hv = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, "hasValue");
            var v = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, "value");
            if (hv == IntPtr.Zero || v == IntPtr.Zero) return null;
            // 値型の欄の位置は箱の頭 (ポインタ 2 つ分) を含むので引く
            int header = IntPtr.Size * 2;
            _hasValueAt = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(hv) - header;
            _valueAt = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(v) - header;
        }
        byte* field = (byte*)Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtrNotNull(data)
                      + Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(_roleWhenAliveField);
        return field[_hasValueAt] != 0 ? (RoleTypes)(*(ushort*)(field + _valueAt)) : null;
    }

    // 本編の Revive は手元の状態だけ戻すので、全員がそれぞれ呼ぶ
    private static void ReviveLocal(byte pid)
    {
        var p = Player(pid);
        if (!p || p.Data == null || !p.Data.IsDead) return;
        p.Revive();
        // 死ぬと本編が幽霊の役職に替えるので、生きていた時の役職に戻す
        var alive = RoleWhenAlive(p.Data);
        // 読めない時や幽霊側の役職が入っている時 (フリープレイなど) は、陣営の基本の役職に戻す
        if (!alive.HasValue || RoleManager.IsGhostRole(alive.Value))
            alive = p.Data.Role && p.Data.Role.TeamType == RoleTeamTypes.Impostor ? RoleTypes.Impostor : RoleTypes.Crewmate;
        RoleManager.Instance.SetRole(p, alive.Value);
        Plugin.Logger.LogInfo($"revive {pid}: alive role={alive?.ToString() ?? "-"} now={p.Data.Role?.Role}");
        if (p.AmOwner) RemoveGhostHint(p);
        // 倒れた体が残ると通報できてしまうので片付ける
        foreach (var body in UnityEngine.Object.FindObjectsOfType<DeadBody>())
            if (body && body.ParentId == pid) UnityEngine.Object.Destroy(body.gameObject);
        if (DevGod.On) DevGod.Refresh();
    }
}
