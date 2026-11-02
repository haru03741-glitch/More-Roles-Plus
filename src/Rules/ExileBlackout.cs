using UnityEngine;

namespace MoreRolesPlus.Rules;

// 本編の追放の締め (ExileController.WrapUp) は、その端末が「人数で試合が終わった」と判断すると、
// 操作と HUD を戻す処理 (ReEnableGameplay) を飛ばしてホストの終了を待つ。ホストと客で終わったかの判断が食い違うと
// (ホストだけが試合の終わりを止めている・役職の数え方の違いなど)、客は全画面の黒と HUD が消えたまま試合が続く
// (次の会議の画面まで黒に覆われる)。会議の画面と追放の演出が両方消えて少し経っても試合が続き黒が残っていたら、
// 本編の戻す処理を代わりに呼ぶ
internal static class ExileBlackout
{
    private const float CheckDelay = 2f; // 本当に試合が終わる時はホストの終了がこの間に届く

    private static ExileController _seen;

    // 追放の演出を見た時 (締めの後は Instance から取れないので控える)
    internal static void Seen(ExileController exile) => _seen = exile;

    internal static void OnMeetingEnd()
    {
        var exile = _seen;
        _seen = null;
        Roles.PortKit.After(CheckDelay, () => Check(exile));
    }

    private static void Check(ExileController exile)
    {
        var hud = Vanilla.Hud;
        var fs = hud ? hud.FullScreen : null;
        var client = AmongUsClient.Instance;
        if (!fs || !fs.gameObject.activeInHierarchy || !fs.enabled) return;
        if (!client || client.GameState != InnerNet.InnerNetClient.GameStates.Started) return;
        if (MeetingHud.Instance || ExileController.Instance || hud.IsIntroDisplayed) return;
        // 黒で不透明の時だけ (mod の演出が赤や白で借りている時は触らない)
        var c = fs.color;
        if (c.a < 0.95f || c.r > 0.05f || c.g > 0.05f || c.b > 0.05f) return;
        Plugin.Logger.LogInfo($"exile blackout: gameplay was not re-enabled after the meeting (controller={(exile is not null ? "kept" : "none")})");
        // ReEnableGameplay は this を使わない (演出の GameObject が消えた後でも呼べる)。黒を明ける・HUD を戻す・操作を戻すまで本編のまま
        // 破棄済みの Unity の物は == null が真になるので、参照があるかだけを見る
        if (exile is not null)
        {
            try { exile.ReEnableGameplay(); return; }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"exile blackout: {e.Message}"); }
        }
        hud.StartCoroutine(hud.CoFadeFullScreen(Color.black, Color.clear, 0.2f, false));
        hud.SetHudActive(true);
    }
}
