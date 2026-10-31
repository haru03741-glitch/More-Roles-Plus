using System;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Bridge;

// 立ち合いの体の崩れ方 (DuelTumble) の確認用コマンド
internal static class DuelCommands
{
    public static void Register()
    {
        TestBridge.Register("tumble", "[<番号> [weak|strong|ko|tech] [当てた人の番号]] 当たった人の崩れ方: のけぞり / 吹っ飛び / 倒れる時の人形 (どれもこの端末だけ・ko の後に kill で死体を移す) / 受け身 (吹っ飛んでいる自分) / 空 = 状態", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length >= 1 && byte.TryParse(a[0], out byte id))
            {
                string kind = a.Length >= 2 ? a[1] : "strong";
                byte from = a.Length >= 3 && byte.TryParse(a[2], out byte f) ? f : (byte)255;
                if (kind == "weak") DuelTumble.Recoil(id, from);
                else if (kind == "ko") DuelTumble.KoLaunch(id, from);
                else if (kind == "tech")
                {
                    var lp = PlayerControl.LocalPlayer;
                    if (lp) DuelTumble.TryTech(lp);
                }
                else DuelTumble.Launch(id, from);
            }
            reply("OK " + DuelTumble.Describe() + " " + DuelTumble.DescribeKo());
        });
        TestBridge.Register("cutbody", "[<番号> [weak|strong]] 斬られた形の死体: 番号の人の姿を写して自分が倒す (ホスト・フリープレイ向け・weak/strong = 立ち合いで斬った扱いで片が飛ぶ) / 空 = 状態", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length > 0 && byte.TryParse(a[0], out byte id))
            {
                var lp = PlayerControl.LocalPlayer;
                var t = GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;
                if (!AmongUsClient.Instance.AmHost || !lp || !t) { reply("ERR cutbody <id> (host only)"); return; }
                bool ok = a.Length >= 2 ? CutCorpse.CaptureHit(id, lp.PlayerId, a[1] == "strong") : CutCorpse.Capture(id);
                lp.RpcMurderPlayer(t, true);
                reply($"OK captured={ok} {CutCorpse.Describe()}");
                return;
            }
            reply("OK " + CutCorpse.Describe());
        });
    }
}
