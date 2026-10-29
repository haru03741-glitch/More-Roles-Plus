using AmongUs.GameOptions;
using System.Linq;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Bridge;

// 役職・設定の確認用コマンド
internal static class RoleCommands
{
    public static void Register()
    {
        TestBridge.Register("vroles", "本編の役職の名前・説明・能力ボタンの文と絵 (今の言語で)", (_, reply) =>
        {
            var rm = RoleManager.Instance;
            if (!rm || rm.AllRoles == null || !TranslationController.InstanceExists) { reply("ERR vroles not ready"); return; }
            var tc = TranslationController.Instance;
            var sb = new System.Text.StringBuilder("OK vroles");
            foreach (var r in rm.AllRoles)
            {
                if (!r) continue;
                var ab = r.Ability;
                sb.Append($"\n[{r.Role}] team={r.TeamType} vent={r.CanVent} kill={r.CanUseKillButton} name={r.NiceName} | med={tc.GetString(r.BlurbNameMed)} | long={tc.GetString(r.BlurbNameLong)}");
                if (ab != null) sb.Append($" | ability={tc.GetString(ab.Text)} img={(ab.Image ? ab.Image.name : "-")} img2={(ab.SecondImage ? ab.SecondImage.name : "-")}");
            }
            reply(sb.ToString());
        });

        TestBridge.Register("vstr", "<鍵の一部> 本編の文のうち鍵にその語を含む物を、今の言語と控えの言語 (英語) で", (args, reply) =>
        {
            if (!TranslationController.InstanceExists) { reply("ERR vstr not ready"); return; }
            var tc = TranslationController.Instance;
            string q = args.Trim();
            var sb = new System.Text.StringBuilder("OK vstr");
            foreach (var lang in new[] { tc.currentLanguage, tc.fallbackLanguage })
            {
                if (lang == null || lang.AllStrings == null) continue;
                sb.Append($"\n== {lang.languageID}");
                foreach (var kv in lang.AllStrings)
                    if (kv.Key.Contains(q, System.StringComparison.OrdinalIgnoreCase))
                        sb.Append('\n').Append(kv.Key).Append(" = ").Append(kv.Value.Replace("\n", "\\n"));
            }
            reply(sb.ToString());
        });

        TestBridge.Register("tmpsprite", "TMP の絵の既定の素材とシェーダがあるか", (_, reply) =>
        {
            var def = TMPro.TMP_Settings.defaultSpriteAsset;
            var sh = UnityEngine.Shader.Find("TextMeshPro/Sprite");
            string defInfo = def ? $"{def.name} mat={(def.material ? def.material.name + "/" + def.material.shader.name : "-")} tex={(def.spriteSheet ? def.spriteSheet.name : "-")}" : "-";
            reply($"OK tmpsprite default={defInfo} find={(sh ? sh.name : "-")}");
        });

        TestBridge.Register("roles", "登録された役職と、今の割り当て", (_, reply) =>
        {
            string reg = string.Join(" ", Registry.Roles.Select(r => $"{r.Id}({r.Team},{r.Chance.Value}%x{r.Count.Value})"));
            string now = string.Join(" ", RoleState.All.Select(r => $"{r.PlayerId}={r.Id}"));
            string addons = string.Join(" ", Registry.Addons.Select(a => $"{a.Id}({a.Chance.Value}%x{a.Count.Value})"));
            string addonNow = string.Join(" ", RoleState.AllAddons.Select(a => $"{a.PlayerId}=+{a.Id}"));
            string extra = "";
            var lp = PlayerControl.LocalPlayer;
            if (lp && ShipStatus.Instance && lp.Data != null)
                extra = $" vision={ShipStatus.Instance.CalculateLightRadius(lp.Data):0.###} killcd={GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown):0.#}/{GameManager.Instance.LogicOptions.GetKillCooldown():0.#}";
            reply($"OK roles reg=[{reg}] assigned=[{now}] addons=[{addons}] addonsNow=[{addonNow}] local={RoleState.Local?.Id ?? "-"} fp={Registry.Fingerprint:X8}{extra}");
        });

        TestBridge.Register("rewindfx", "[white | z <奥行き>] 時が戻った時の全員の画面の演出だけを出す (white = 全画面の発動の演出の直後の形) / 画面の板の奥行きを変える (HUD の中・既定 10)", (args, reply) =>
        {
            var p = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && p[0] == "z")
                RewindWorldFx.RootZ = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
            else RewindWorldFx.Play(p.Length >= 1 && p[0] == "white");
            var hud = Vanilla.Hud;
            string z = hud && hud.FullScreen ? hud.FullScreen.transform.localPosition.z.ToString("0.###") : "-";
            string kill = hud && hud.KillButton ? hud.KillButton.transform.localPosition.z.ToString("0.###") : "-";
            string use = hud && hud.UseButton ? hud.UseButton.transform.position.z.ToString("0.###") : "-";
            reply($"OK rewindfx playing={RewindWorldFx.Playing} rootZ={RewindWorldFx.RootZ} fullScreenZ={z} killZ={kill} useWorldZ={use}");
        });
        TestBridge.Register("rewindpose", "時戻しの逆再生で動かす全員の姿の状態 (控えの数・動かしている人・流している絵の種類・向き・アニメの時間)", (args, reply) =>
            reply($"OK rewindpose {RewindPlayback.Probe()}"));
        TestBridge.Register("rewindsnd", "時戻しの逆再生で鳴らす音の状態 (控えた数・並べた数・鳴っている声の pitch と再生位置。位置が呼ぶたびに減っていれば逆に鳴っている)", (args, reply) =>
            reply($"OK rewindsnd recorded={RewindSounds.Recorded} {RewindSounds.Last} voices=[{RewindSounds.Probe()}]"));
        TestBridge.Register("rewind", "[ask [押してからの ms] [戻す秒数 (0 = 設定の上限)] | force <秒> | doors <部屋 (SystemTypes)> | check <0|1> | print | charge <0〜1|off>] 時戻しの記録の状態 / 時戻しのボタンと同じ依頼を出す / ホストが役職を見ずに秒数だけ戻す / 部屋の扉を閉める (ホスト) / 地形の記録に指紋を付けて読み戻しの直後に比べる (自分の手元だけ) / 壁の今の姿の指紋 / 溜めの演出を値を決めて出す", (args, reply) =>
        {
            var p = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && p[0] == "charge")
            {
                RewindChargeFx.TestCharge = float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cv) ? cv : -1f;
                reply($"OK rewind charge {RewindChargeFx.TestCharge:0.00} showing={RewindChargeFx.Showing}");
                return;
            }
            if (p.Length >= 2 && p[0] == "check") Terrain.TerrainRewind.SelfCheck = p[1] == "1";
            if (p.Length >= 1 && p[0] == "print") { reply($"OK rewind print {Terrain.TerrainUndo.Print()}"); return; }
            if (p.Length >= 2 && p[0] == "doors")
            {
                if (!ShipStatus.Instance || !System.Enum.TryParse<SystemTypes>(p[1], out var room)) { reply("ERR rewind doors"); return; }
                ShipStatus.Instance.RpcCloseDoorsOfType(room);
            }
            if (p.Length >= 1 && p[0] == "ask" && !TimeRewind.Ask(p.Length >= 2 && int.TryParse(p[1], out int el) ? el : 0, p.Length >= 3 && int.TryParse(p[2], out int askSec) ? askSec : 0)) { reply($"ERR rewind ask {TimeRewind.Probe()}"); return; }
            if (p.Length >= 2 && p[0] == "force" && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sec))
            {
                if (!TimeRewind.HostForce(sec)) { reply($"ERR rewind force {TimeRewind.Probe()}"); return; }
            }
            int closed = 0;
            if (ShipStatus.Instance)
                foreach (var d in ShipStatus.Instance.AllDoors)
                    if (d && !d.IsOpen) closed++;
            reply($"OK rewind {TimeRewind.Probe()} closedDoors={closed} | terrain {Terrain.TerrainRewind.Probe()} | walls {Terrain.TerrainUndo.Probe()} digest={Terrain.TerrainDigest.Running:x8}");
        });

        TestBridge.Register("portal", "[x y | go <0|1> | use] 自分のポータルメーカーの状態 / (x, y) に置く (ボタンの待ち時間は使わない) / 装置の上へ移る / 本編の使用ボタンを押す", (args, reply) =>
        {
            if (RoleState.Local is not Roles.Crew.PortalMaker pm) { reply("ERR portal not PortalMaker"); return; }
            var p = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && p[0] == "go" && int.TryParse(p[1], out int gi))
            {
                if (!pm.GoForTest(gi)) { reply($"ERR portal go {pm.Describe()}"); return; }
            }
            else if (p.Length >= 1 && p[0] == "use")
            {
                var hud = Vanilla.Hud;
                if (!hud || !hud.UseButton) { reply("ERR portal no use button"); return; }
                hud.UseButton.DoClick();
            }
            else if (p.Length >= 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                              && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
            {
                if (!pm.PlaceForTest(x, y)) { reply($"ERR portal refused {pm.Describe()}"); return; }
            }
            reply($"OK portal {pm.Describe()} art={PortalArt.LiveCount}");
        });

        TestBridge.Register("samurai", "[draw | slash <dx> <dy> | of <番号>] 自分のサムライの状態 / 抜刀・納刀のボタンを押す / その向きへ斬る (ボタンの待ち時間は使う) / 番号の人のサムライの状態", (args, reply) =>
        {
            var p = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (p.Length >= 2 && p[0] == "of" && byte.TryParse(p[1], out byte oid))
            {
                if (RoleState.Of(oid) is not Roles.Neutral.Samurai os) { reply("ERR samurai not Samurai"); return; }
                reply($"OK samurai {os.Probe()}");
                return;
            }
            if (RoleState.Local is not Roles.Neutral.Samurai sm) { reply("ERR samurai not Samurai"); return; }
            if (p.Length >= 1 && p[0] == "draw") Abilities.PressSlot(0);
            else if (p.Length >= 3 && p[0] == "slash"
                     && float.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out float dx)
                     && float.TryParse(p[2], System.Globalization.NumberStyles.Float, inv, out float dy))
            {
                if (!sm.SlashForTest(new UnityEngine.Vector2(dx, dy))) { reply($"ERR samurai slash refused {sm.Probe()}"); return; }
            }
            reply($"OK samurai {sm.Probe()}");
        });

        TestBridge.Register("architect", "[use | of <番号> ... | place x y | simuse <番号> <台|255> | smash <台> <叩いた人> <回数> | x y | here | check x y | fly | visit x y | land | go <番号> | open | close | draw x1 y1 x2 y2 ... | pipe x y] 自分の構築士の状態 / 自分 (構築士でない人) が足元の設計台を使う / 番号の人の構築士を相手にする / この端末だけで台を置く / 番号の人が台を使った (255 = やめた) と届いた扱い / 台が叩かれた (3 回目で壊れる) と届いた扱い /(x, y) に設計台を置く (待ち時間は使わない) / 足元に能力ボタンと同じ決まりで置く / 床の点 (x, y) に置けるか / 設計図を開かずに自分の端末だけでドローンを飛ばす・行き先を足す・降ろす / 設計台の手前へ移る / 設計図を開く・閉じる / 点の列をなぞった扱いにする (ゲージを使い壁を立てる) / (x, y) を軽く押した扱いにする (近くの壁の配管を裂く)", (args, reply) =>
        {
            var p = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            // use = 自分 (構築士でない人) が足元の設計台を使う。of <番号> ... = ほかの人の構築士を相手にする
            if (p.Length >= 1 && p[0] == "use")
            {
                bool used = Roles.Impostor.Architect.UseForTest();
                reply($"{(used ? "OK" : "ERR")} architect use plan={ArchitectPlan.Describe()}");
                return;
            }
            Roles.Impostor.Architect ar;
            if (p.Length >= 2 && p[0] == "of" && byte.TryParse(p[1], out byte oid))
            {
                ar = RoleState.Of(oid) as Roles.Impostor.Architect;
                if (ar == null) { reply($"ERR architect {oid} not Architect"); return; }
                p = p[2..];
            }
            else if (RoleState.Local is Roles.Impostor.Architect mine) ar = mine;
            else { reply("ERR architect not Architect"); return; }
            if (p.Length >= 3 && p[0] == "place")
            {
                if (!ar.PlaceLocalForTest(float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)))
                { reply($"ERR architect place {ar.Describe()}"); return; }
            }
            else if (p.Length >= 3 && p[0] == "simuse" && byte.TryParse(p[1], out byte uid) && byte.TryParse(p[2], out byte ud))
            {
                if (!ar.SimUseForTest(uid, ud)) { reply($"ERR architect simuse {ar.Describe()}"); return; }
            }
            else if (p.Length >= 4 && p[0] == "smash" && byte.TryParse(p[1], out byte sd) && byte.TryParse(p[2], out byte sh) && byte.TryParse(p[3], out byte sn))
            {
                if (!ar.SmashForTest(sd, sh, sn)) { reply($"ERR architect smash {ar.Describe()}"); return; }
            }
            else if (p.Length >= 2 && p[0] == "go" && int.TryParse(p[1], out int gi))
            {
                if (!ar.GoForTest(gi)) { reply($"ERR architect go {ar.Describe()}"); return; }
            }
            else if (p.Length >= 3 && p[0] == "check")
            {
                string why = Roles.Impostor.Architect.CheckForTest(float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
                reply($"OK architect check {why ?? "ok"}");
                return;
            }
            else if (p.Length >= 1 && p[0] == "fly")
            {
                if (!ar.FlyForTest()) { reply($"ERR architect fly {ar.Describe()}"); return; }
            }
            else if (p.Length >= 3 && p[0] == "visit")
            {
                if (!ar.VisitForTest(float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)))
                { reply($"ERR architect visit {ar.Describe()}"); return; }
            }
            else if (p.Length >= 1 && p[0] == "land") ar.LandForTest();
            else if (p.Length >= 1 && p[0] == "here")
            {
                if (!ar.HereForTest()) { reply($"ERR architect here {ar.Describe()}"); return; }
            }
            else if (p.Length >= 1 && p[0] == "open")
            {
                if (!ar.OpenForTest()) { reply($"ERR architect open {ar.Describe()}"); return; }
            }
            else if (p.Length >= 1 && p[0] == "close") ArchitectPlan.Close();
            else if (p.Length >= 3 && p[0] == "pipe")
            {
                bool ok = ar.Burst(new UnityEngine.Vector2(float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)));
                reply($"{(ok ? "OK" : "ERR")} architect pipe {ar.Describe()}");
                return;
            }
            else if (p.Length >= 5 && p[0] == "draw")
            {
                var pts = new System.Collections.Generic.List<UnityEngine.Vector2>();
                for (int i = 1; i + 1 < p.Length; i += 2)
                    pts.Add(new UnityEngine.Vector2(float.Parse(p[i], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[i + 1], System.Globalization.CultureInfo.InvariantCulture)));
                float used = ar.DrawForTest(pts);
                reply($"OK architect drew {used:0.00} {ar.Describe()}");
                return;
            }
            else if (p.Length >= 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                              && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
            {
                if (!ar.PlaceForTest(x, y)) { reply($"ERR architect refused {ar.Describe()}"); return; }
            }
            reply($"OK architect {ar.Describe()} art={ArchitectDeskArt.LiveCount} desk0=[{ArchitectDeskArt.Probe()}]");
        });

        TestBridge.Register("endcheck", "[番号...] 生き残りの数で勝者が決まるかを見るだけ (終わらせない)。番号の人をキル役として数える", (args, reply) =>
        {
            if (!GameData.Instance) { reply("ERR endcheck not in game"); return; }
            GameEnd.TestKillers.Clear();
            foreach (var t in args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                if (byte.TryParse(t, out byte pid)) GameEnd.TestKillers.Add(pid);
            var r = GameEnd.Decide(out var reason, out string info, true);
            GameEnd.TestKillers.Clear();
            string res = r == null ? "none" : $"team={r.Team?.ToString() ?? "-"} role={r.Role?.Id ?? (r.Winners.Count > 0 && r.Team == null ? "test" : "-")} winners=[{string.Join(",", r.Winners)}] reason={reason}";
            reply($"OK endcheck {info} -> {res}");
        });

        TestBridge.Register("winner", "最後に配られた試合の結果", (_, reply) =>
        {
            var r = GameEnd.Last;
            reply(r == null ? "OK winner none" : $"OK winner team={r.Team?.ToString() ?? "-"} role={r.Role?.Id ?? "-"} winners=[{string.Join(",", r.Winners)}] localWon={GameEnd.LocalWon} cached={EndGameResult.CachedWinners?.Count ?? -1}");
        });

        TestBridge.Register("gameflags", "試合の終わりに関わる本編の状態と、止めた終わりの数", (_, reply) =>
        {
            var gm = GameManager.Instance;
            reply($"OK gameflags check={(gm ? gm.ShouldCheckForGameEnd : false)} started={(gm ? gm.GameHasStarted : false)} state={AmongUsClient.Instance.GameState} blocked={GameEnd.Blocked} total={GameData.Instance?.TotalTasks} done={GameData.Instance?.CompletedTasks}");
        });

        TestBridge.Register("meeting", "自分が緊急会議を開く", (_, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || !ShipStatus.Instance) { reply("ERR meeting not in game"); return; }
            lp.CmdReportDeadBody(null);
            reply("OK meeting");
        });

        TestBridge.Register("meetingtags", "会議の名札ごとの役職名の有無と位置", (_, reply) =>
        {
            var hud = MeetingHud.Instance;
            if (!hud) { reply("ERR meetingtags no meeting"); return; }
            var sb = new System.Text.StringBuilder();
            foreach (var pva in hud.playerStates)
            {
                var tag = pva.NameText.transform.parent.Find("MrpMeetingRole");
                var n = pva.NameText.transform;
                sb.Append($" [{pva.name} name={n.localPosition.x:0.##},{n.localPosition.y:0.##} s={n.localScale.x:0.##} tag={(tag ? $"{tag.localPosition.x:0.##},{tag.localPosition.y:0.##} act={tag.gameObject.activeInHierarchy}" : "-")}]");
            }
            reply($"OK meetingtags{sb}");
        });

        TestBridge.Register("vote", "<番号|skip> 会議で投票する", (args, reply) =>
        {
            var hud = MeetingHud.Instance;
            if (!hud) { reply("ERR vote no meeting"); return; }
            byte target = args.Trim() == "skip" ? (byte)253 : byte.Parse(args.Trim());
            hud.CmdCastVote(PlayerControl.LocalPlayer.PlayerId, Rules.MeetingVotes.SuspectIndex(hud, target));
            reply($"OK vote {target}");
        });

        TestBridge.Register("meetvote", "<番号|skip> 会議画面の確定ボタンを押したのと同じ (乗り移り中ならダミーの票になる)", (args, reply) =>
        {
            var hud = MeetingHud.Instance;
            if (!hud) { reply("ERR meetvote no meeting"); return; }
            byte target = args.Trim() == "skip" ? (byte)253 : byte.Parse(args.Trim());
            // 確定ボタンと同じ入口 (PlayerVoteArea.VoteForMe) から押す
            PlayerVoteArea area = target == 253 ? hud.SkipVoteButton : null;
            foreach (var pva in hud.playerStates) if (pva && pva.PlayerId.Value == target) area = pva;
            if (!area) { reply("ERR meetvote no such area"); return; }
            area.VoteForMe();
            reply($"OK meetvote {target}");
        });

        TestBridge.Register("proceed", "会議の投票結果の画面で「進む」を押す", (_, reply) =>
        {
            var hud = MeetingHud.Instance;
            if (!hud) { reply("ERR proceed no meeting"); return; }
            if (!hud.ProceedButton || !hud.ProceedButton.gameObject.activeInHierarchy) { reply("ERR proceed button not shown"); return; }
            hud.ProceedButton.OnClick.Invoke();
            reply("OK proceed");
        });

        TestBridge.Register("killbtn", "[0|1] キルボタンの状態 (表示・狙い・待ち時間)。1 で押す", (args, reply) =>
        {
            var hud = Vanilla.Hud;
            var lp = PlayerControl.LocalPlayer;
            if (!hud || !hud.KillButton || !lp) { reply("ERR killbtn no hud"); return; }
            var b = hud.KillButton;
            string state() => $"shown={b.isActiveAndEnabled} target={(b.currentTarget ? b.currentTarget.PlayerId : -1)} cooling={b.isCoolingDown} timer={lp.killTimer:0.#} canUse={lp.Data?.Role?.CanUseKillButton}";
            if (args.Trim() == "1")
            {
                string before = state();
                b.DoClick();
                reply($"OK killbtn pressed before=[{before}]");
            }
            else reply($"OK killbtn {state()}");
        });

        TestBridge.Register("exile", "<番号> その人に本編の追放の処理 (Exiled) を走らせる。自分の端末だけ", (args, reply) =>
        {
            var p = byte.TryParse(args.Trim(), out byte pid) && GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;
            if (!p) { reply($"ERR exile no player {args}"); return; }
            p.Exiled();
            reply($"OK exile {pid}");
        });

        TestBridge.Register("tasks", "自分のタスク欄の中身", (_, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || lp.myTasks == null) { reply("ERR tasks"); return; }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lp.myTasks.Count && i < 4; i++)
            {
                var t = lp.myTasks[i];
                sb.Append('[').Append(t ? t.name : "null");
                var it = t ? t.TryCast<ImportantTextTask>() : null;
                if (it != null) sb.Append(':').Append(it.Text.Replace('\n', '/'));
                sb.Append(']');
            }
            reply($"OK tasks n={lp.myTasks.Count} total={GameData.Instance?.TotalTasks} done={GameData.Instance?.CompletedTasks} {sb}");
        });

        TestBridge.Register("settingcode", "copy | paste | show  設定のコードをコピー / 貼ってある物を読む / 貼ってある物の中身", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "copy") reply($"OK settingcode {SettingCode.Copy()} len={UnityEngine.GUIUtility.systemCopyBuffer.Length}");
            else if (a == "paste") reply($"OK settingcode {SettingCode.Paste()}");
            else reply($"OK settingcode {(SettingCode.Decode(UnityEngine.GUIUtility.systemCopyBuffer) ?? "(not a code)").Replace('\n', '|')}");
        });

        TestBridge.Register("fresh", "[key...] ホストが直前に変えた印を付ける (受け取った時と同じ・key 無し = 外す)", (args, reply) =>
        {
            var list = new System.Collections.Generic.List<Opt>();
            foreach (var k in args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                if (Registry.TryGet(k, out var o)) list.Add(o);
            SettingNotice.MarkFresh(list);
            reply($"OK fresh {SettingNotice.FreshCount}");
        });

        TestBridge.Register("opt", "<key> [番号] [sync] 設定項目を見る / 番号で変える (sync = 全員へ送る・例 opt Lighter.Chance 10 sync)", (args, reply) =>
        {
            var a = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0)
            {
                reply("OK opts " + string.Join(" ", Registry.All.Select(o => $"{o.Key}={o.Display()}")));
                return;
            }
            if (!Registry.TryGet(a[0], out var opt)) { reply($"ERR opt unknown key {a[0]}"); return; }
            if (a.Length > 1 && int.TryParse(a[1], out int i)) opt.SetIndex(i);
            if (a.Length > 2 && a[2] == "sync")
            {
                PresetLabel.Recheck(); // 設定画面を閉じた時と同じく、プリセットのままかを見直してから送る
                Net.OptionSync.SendAll();
            }
            reply($"OK opt {opt.Key} index={opt.Index}/{opt.Count} value={opt.Display()}");
        });

        TestBridge.Register("editsettings", "ロビーでゲーム設定の画面を開く (ホストのみ)", (_, reply) =>
        {
            var gsm = Vanilla.StartManager;
            if (!gsm) { reply("ERR editsettings no lobby"); return; }
            gsm.ClickEdit();
            reply("OK editsettings");
        });

        TestBridge.Register("settab", "<番号> 開いている設定画面のタブを切り替える (1=本編のゲーム設定・3〜=MRP)", (args, reply) =>
        {
            if (!GameSettingMenu.Instance) { reply("ERR settab menu not open"); return; }
            if (!int.TryParse(args.Trim(), out int n)) { reply("ERR settab number"); return; }
            GameSettingMenu.Instance.ChangeTab(n, false);
            reply($"OK settab {n}");
        });


        TestBridge.Register("clickcat", "<番号> 設定画面の左の列のボタンを押す (0=プリセット 1=ゲーム 2=全般 3=クルー 4=インポスター 5=第三陣営 6=アドオン)", (args, reply) =>
        {
            if (!int.TryParse(args.Trim(), out int c)) { reply("ERR clickcat number"); return; }
            reply($"OK clickcat {SettingsMenu.ClickCat(c)}");
        });

        TestBridge.Register("clicktab", "<MRPタブ番号 0〜> MRP のタブのボタンを押す", (args, reply) =>
        {
            if (!int.TryParse(args.Trim(), out int p)) { reply("ERR clicktab number"); return; }
            reply($"OK clicktab {SettingsMenu.ClickTabButton(p)}");
        });

        TestBridge.Register("viewsettings", "[タブ 0=概要 1=役職 2=MRP] [件数] ロビーの設定を見る画面を開いてタブのボタンを押し、並んだ文字を返す (客でも可)", (args, reply) =>
        {
            var a = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            int tab = a.Length > 0 && int.TryParse(a[0], out int t) ? t : 2;
            int max = a.Length > 1 && int.TryParse(a[1], out int m) ? m : 12;
            reply($"OK viewsettings {LobbyView.Open(tab, max)}");
        });


        TestBridge.Register("genpage", "[jump <n> | reset | undo | rebuild] 設定画面の全般の頁 (節の目次へ飛ぶ・すべて初期値に戻す・元に戻す) を操作して、節の並びを返す (* = 初期値から変えた項目あり)", (args, reply) =>
            reply("OK genpage " + SettingsPages.Command(SettingsPages.General, args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))));
        TestBridge.Register("gamepage", "[map <MapId> | chip <行 0=バリエーション 1=モード> <札の番号 0〜> | imp | jump <n> | reset | undo | rebuild] 設定画面のゲームの頁を操作して、節の並びを返す (imp = インポスターと第三陣営の人数)", (args, reply) =>
            reply("OK gamepage " + SettingsPages.Command(SettingsPages.Game, args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))));
        TestBridge.Register("rolepage", "[q <語> | team <-1〜3> | only | hover <Id> | open <Id> | scroll <0〜1> | bulk <0|1|2 (2 = 元に戻す)> | reset (開いている役職を初期値へ) | rebuild] 設定画面の役職タブ (カードの頁) を操作して、並んでいるカードを返す", (args, reply) =>
            reply("OK rolepage " + RolePage.Command(args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))));

        TestBridge.Register("dict", "[open [役職Id] | close | q <語> | team <-1〜3> | only | hover <Id> | scroll <0〜1> | rebuild | button [click|ensure|look <高さ> <y>|state <Inactive|Active|Selected>]] 役職辞典の窓を開いて操作し、並んでいるカードを返す (button = 右上の辞典のボタン)", (args, reply) =>
            reply("OK dict " + Menu.RoleDictionary.Command(args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))));

        TestBridge.Register("uitree", "<chat|menu|page|gametab|cam|map|GameObject のパス> [深さ=3] 画面の部品の階層 (位置・大きさ・部品の種類・有効か)", (args, reply) =>
        {
            var a = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0) { reply("ERR uitree target"); return; }
            int depth = a.Length > 1 && int.TryParse(a[1], out int d) ? d : 3;
            var root = UiTarget(a[0]);
            if (!root) { reply($"ERR uitree not found: {a[0]}"); return; }
            var sb = new System.Text.StringBuilder();
            void Walk(UnityEngine.Transform t, int level)
            {
                var p = t.localPosition; var s = t.localScale;
                sb.Append('\n').Append(' ', level * 2).Append(t.gameObject.activeSelf ? "" : "(off) ").Append(t.name)
                  .Append($" pos=({p.x:0.###},{p.y:0.###},{p.z:0.###}) scale=({s.x:0.###},{s.y:0.###})");
                foreach (var c in t.GetComponents<UnityEngine.Component>())
                {
                    if (c == null || c.TryCast<UnityEngine.Transform>() != null) continue;
                    sb.Append(' ').Append(c.GetIl2CppType().Name);
                    var sr = c.TryCast<UnityEngine.SpriteRenderer>();
                    if (sr != null) sb.Append($"[size={sr.size.x:0.##}x{sr.size.y:0.##} sprite={(sr.sprite ? sr.sprite.name : "-")} order={sr.sortingOrder}]");
                    var tmp = c.TryCast<TMPro.TextMeshPro>();
                    if (tmp != null) sb.Append($"[\"{tmp.text}\" fs={tmp.fontSize:0.##}]");
                    var bc = c.TryCast<UnityEngine.BoxCollider2D>();
                    if (bc != null) sb.Append($"[box={bc.size.x:0.##}x{bc.size.y:0.##}]");
                }
                if (level >= depth) return;
                for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), level + 1);
            }
            Walk(root, 0);
            reply("OK uitree" + sb);
        });

        TestBridge.Register("uilook", "<A> <B> [深さ=3] 2 つの部品の見た目の違い (色・絵・字の大きさ/色/書体・大きさ・有効か。位置と文字は比べない)。A/B は uitree と同じ名前か gametab:header:<N> / gametab:row:<N> / <名前>:<子の名前>", (args, reply) =>
        {
            var a = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (a.Length < 2) { reply("ERR uilook <A> <B>"); return; }
            var ta = UiTarget(a[0]);
            var tb = UiTarget(a[1]);
            if (!ta || !tb) { reply($"ERR uilook not found: {(!ta ? a[0] : a[1])}"); return; }
            int depth = a.Length > 2 && int.TryParse(a[2], out int d) ? d : 3;
            var diffs = new System.Collections.Generic.List<string>();
            UiLook.Compare(ta, tb, "", depth, diffs);
            reply($"OK uilook diffs={diffs.Count}" + (diffs.Count > 0 ? "\n" + string.Join("\n", diffs) : ""));
        });

        TestBridge.Register("excl", "<役職Id> <n|all|none> 一緒に出ない役職を、候補の先頭から n 個だけ選んだ状態にする (相手の側も揃える)。値の欄の要約を返す", (args, reply) =>
        {
            var a = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            var me = a.Length > 0 ? Registry.Roles.Find(r => r.Id == a[0]) as RoleBase : null;
            if (me?.Excluded == null || a.Length < 2) { reply("ERR excl <役職Id> <n|all|none>"); return; }
            var cands = Registry.Roles.FindAll(r => r != me && r is RoleBase rb && rb.Excluded != null);
            int n = a[1] == "all" ? cands.Count : a[1] == "none" ? 0 : int.TryParse(a[1], out int v) ? v : 0;
            for (int i = 0; i < cands.Count; i++)
            {
                var other = (RoleBase)cands[i];
                me.Excluded.Set(other.Order, i < n);
                other.Excluded.Set(me.Order, i < n);
            }
            reply($"OK excl {me.Id} {System.Math.Min(n, cands.Count)}/{cands.Count} display={me.Excluded.Display()}");
        });

        TestBridge.Register("closesettings", "設定画面を閉じる", (_, reply) =>
        {
            if (!GameSettingMenu.Instance) { reply("ERR closesettings menu not open"); return; }
            GameSettingMenu.Instance.Close();
            reply("OK closesettings");
        });
    }

    // 画面の部品の名前 (uitree / uilook)。「名前:子の名前」は子を名前で、gametab:header:N / gametab:row:N はゲーム設定のタブの N 番目の見出し / 行
    private static UnityEngine.Transform UiTarget(string name)
    {
        int colon = name.IndexOf(':');
        if (colon > 0)
        {
            var parent = UiTarget(name[..colon]);
            if (!parent) return null;
            string rest = name[(colon + 1)..];
            if (name.StartsWith("gametab:header:") || name.StartsWith("gametab:row:"))
            {
                bool header = name.StartsWith("gametab:header:");
                var tab = GameSettingMenu.Instance.GameSettingsTab;
                if (!int.TryParse(rest[(rest.IndexOf(':') + 1)..], out int n) || !tab.settingsContainer) return null;
                var c = tab.settingsContainer;
                for (int i = 0; i < c.childCount; i++)
                {
                    var t = c.GetChild(i);
                    bool hit = header ? t.GetComponent<CategoryHeaderMasked>() != null : t.GetComponent<OptionBehaviour>() != null;
                    if (hit && n-- == 0) return t;
                }
                return null;
            }
            return UiLook.FindDeep(parent, rest);
        }
        return name switch
        {
            "chat" => Vanilla.Chat is { } chat ? chat.freeChatField?.transform : null,
            "menu" => GameSettingMenu.Instance ? GameSettingMenu.Instance.transform : null,
            "page" => SettingsMenu.OpenPage(),
            "gametab" => GameSettingMenu.Instance && GameSettingMenu.Instance.GameSettingsTab ? GameSettingMenu.Instance.GameSettingsTab.transform : null,
            "cam" => UnityEngine.Camera.main ? UnityEngine.Camera.main.transform : null,
            "map" => MapBehaviour.Instance ? MapBehaviour.Instance.transform : null,
            _ => UnityEngine.GameObject.Find(name)?.transform,
        };
    }
}
