using System;
using MoreRolesPlus.Fx;

namespace MoreRolesPlus.Bridge;

// 全身を描き替える連番 (CrewFlip) の確認用コマンド
internal static class CrewFlipCommands
{
    public static void Register()
    {
        TestBridge.Register("crewflip", "[<名前> [番号] [秒] [fallen]|off|bodies] 全身を描き替える連番を番号の人 (省くと自分) に流す (この端末だけ・fallen = 着せ替えを落とし、死んだら最後の絵を死体にする)・off = 全部やめる・bodies = 本編の死体の中身・引数なし = 状態と名前の一覧", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0) { reply("OK " + CrewFlip.Describe() + " / " + FallenCorpse.Describe()); return; }
            if (a[0] == "off") { CrewFlip.ClearAll(); reply("OK " + CrewFlip.Describe()); return; }
            if (a[0] == "bodies")
            {
                foreach (var b in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                {
                    if (!b) continue;
                    var root = b.transform;
                    reply($"BODY {b.name} parent={b.ParentId} pos=({root.position.x:0.00},{root.position.y:0.00},{root.position.z:0.000}) layer={b.gameObject.layer} children={root.childCount}");
                    foreach (var t in b.GetComponentsInChildren<UnityEngine.Transform>(true))
                    {
                        string comps = "";
                        foreach (var c in t.GetComponents<UnityEngine.Component>()) if (c && c is not UnityEngine.Transform) comps += c.GetIl2CppType().Name + ",";
                        var sr = t.GetComponent<UnityEngine.SpriteRenderer>();
                        if (sr) comps += $"order={sr.sortingOrder}{(sr.enabled ? "" : "(off)")} flip={sr.flipX} sprite={(sr.sprite ? sr.sprite.name : "-")} mat={(sr.sharedMaterial ? sr.sharedMaterial.name : "-")}";
                        reply($"  {t.name}{(t.gameObject.activeSelf ? "" : "(inactive)")} lp=({t.localPosition.x:0.00},{t.localPosition.y:0.00}) ls={t.localScale.x:0.00} [{comps.TrimEnd(',')}]");
                    }
                }
                reply("OK bodies");
                return;
            }
            PlayerControl pc = PlayerControl.LocalPlayer;
            if (a.Length >= 2 && byte.TryParse(a[1], out byte id))
                pc = GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;
            float sec = a.Length >= 3 && float.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
            bool ok = pc && CrewFlip.Play(pc, a[0], sec, a.Length >= 4 && a[3] == "fallen");
            reply((ok ? "OK " : "ERR refused ") + CrewFlip.Describe());
        });
    }
}
