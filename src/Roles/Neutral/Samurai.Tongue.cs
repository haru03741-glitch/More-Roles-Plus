using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 立ち合いの相手のインポスター (舌の一撃・DuelTongue) とのつなぎ。
// ホストはこのサムライから立ち合いの相手のインポスターへの斬りを GuardHold 秒だけ保留し、その間にインポスターがキルボタンを押せば
// 口で刀を受けた (斬りは当たらず、サムライは StunTime 秒振れない)。押されなければ保留が切れた所で当てる (HostClashTick)
public sealed partial class Samurai
{
    private const float GuardHold = 0.15f;
    private const float StunTime = 0.35f;
    private float _hostStunUntil = -10f;

    // ホスト: 同じ組のインポスターへの斬りなら保留する。刀が向いていない (近すぎて触れただけの) 当たりは口に届いていないので受けられない
    private bool HostHoldForGuard(PlayerControl target, int damage, bool aimed)
    {
        if (!aimed || !Duel.SameGroup(target.PlayerId, PlayerId) || !DuelTongue.Engages(target, PlayerId)) return false;
        HostHeld.Add(new Held { A = PlayerId, T = target.PlayerId, Dmg = damage, At = Time.time, Iai = _hostStrokeIai, Aimed = aimed, Guard = true, Smash = _hostStrokeSmash });
        DuelBot.OnHeldGuard(target.PlayerId, PlayerId, GuardHold);
        return true;
    }

    // ホスト: このサムライが imp へ振って保留中の斬りがあれば取り除いて true (受けられた)
    internal bool HostTakeHeldOn(byte imp)
    {
        float now = Time.time;
        for (int i = 0; i < HostHeld.Count; i++)
        {
            var h = HostHeld[i];
            if (!h.Guard || h.A != PlayerId || h.T != imp || now - h.At > GuardHold) continue;
            HostHeld.RemoveAt(i);
            return true;
        }
        return false;
    }

    // ホスト: 口で受けられた。少しの間このサムライの振りを通さない
    internal void HostStun() => _hostStunUntil = Time.time + StunTime;

    // 全員の端末: 刀が弾かれた姿 (本人は少し押し戻される。気は減らさない = 受けられ続けて気切れに追い込まれない)
    internal void OnGuarded(byte imp)
    {
        _blade.Recoil(AngleTo(imp));
        DuelTumble.Recoil(PlayerId, imp);
        if (IsLocal) Knock(imp);
    }
}
