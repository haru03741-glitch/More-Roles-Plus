using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// 見本のアドオン (起きた事を受ける): 誰かが倒された時に、自分の画面だけが一瞬赤く光る
public sealed class Sensitive : AddonBase
{
    public override string Color => "#E85D75";
    public override Text Name => new("敏感", "Sensitive");
    public override string Reading => "びんかん";
    public override Text Blurb => new("誰かが倒されると気配を感じる", "Sense it when someone falls");

    public static readonly IntOpt Strength = new("光の強さ", "Flash Strength", 30, 10, 80, 10, "%");

    // 自分の端末の表示だけ。倒されたのが自分なら本編の演出があるので何もしない
    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!Player || Player.Data == null || Player.Data.IsDead || !e.Target || e.Target.AmOwner) return;
        var hud = Vanilla.Hud;
        if (!hud) return;
        float a = Strength.Value / 100f;
        hud.StartCoroutine(hud.CoFadeFullScreen(new Color(1f, 0.1f, 0.15f, a), new Color(1f, 0.1f, 0.15f, 0f), 0.7f, false));
    }
}
