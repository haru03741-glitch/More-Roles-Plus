using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// ノヴァの光線を刀で受ける (どうなるかはホストが決める・Impostor/Nova.Samurai.cs)。
// 押し合いは鍔迫り合いと同じ連打とカメラ・HUD を使い、刀は光線の来る方へ構えたまま、光線に少しずつ押し下げられる
public sealed partial class Samurai
{
    private const float BeamFacing = -0.2f; // 光線の来る方と向いている左右の内積がこれより小さい = 背を向けている
    private const float BeamPush = 0.35f;   // 押し合いの間に光線に押し下げられる速さ

    private bool _beamLock;

    // ホスト: 光線を刀で受けられる形か (抜いている・気がある・よろけていない・背を向けていない・ほかと押し合っていない)
    internal bool HostGuardsBeam(Vector2 from, float kiNeed)
    {
        var p = Player;
        if (!p || !Drawn || Locked || _blade.Stunned) return false;
        if (IsLocal ? Spent : _blade.Spent) return false;
        Vector2 me = p.GetTruePosition();
        float dx = from.x - me.x, dy = from.y - me.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        bool left = p.cosmetics && p.cosmetics.FlipX;
        if (d > 0.01f && dx / d * (left ? -1f : 1f) < BeamFacing) return false;
        if (DevFree(p)) return true;
        return (IsLocal ? _ki : HostKiNow()) >= kiNeed;
    }

    // ホスト: 弾いた分の気を見積もりから引く (本人の端末は OnBeamDeflect で引く)
    internal void HostSpendBeam(float ki)
    {
        if (IsLocal) return;
        HostKiNow();
        _hostKi -= ki;
    }

    // 刀で光線を弾いた (全員の端末で): 刀が跳ね上がり、本人は少し下がって気を使う
    internal void OnBeamDeflect(byte nova)
    {
        _blade.Recoil(AngleTo(nova), 0.2f);
        if (!IsLocal) return;
        Knock(nova);
        UseKi(Impostor.Nova.DeflectKi);
    }

    // 満タンの光線を刀で受けた (全員の端末で): 刀ごと弾き飛ばされ、気が尽きる (吹き飛ぶのは体力の知らせの側)
    internal void OnBeamFatal(byte nova)
    {
        _blade.Recoil(AngleTo(nova), 0.4f);
        if (!IsLocal || DevFree(Player)) return;
        _ki = 0f;
        _spentUntil = Time.time + SpentTime;
        _blade.SetSpent(true);
        SpentCall.Send(true);
    }

    // 押し合いの始め (全員の端末で)。相手へは寄らず、刀を光線の来る方へ構えて受ける
    internal void BeginBeamLock(byte nova)
    {
        if (!Drawn) SetDrawn(true, iai: true);
        _beamLock = true;
        _lockWith = nova;
        _lockOpp = GameData.Instance ? GameData.Instance.GetPlayerById(nova)?.Object : null;
        _lockUntil = Time.time + LockTime + 1.5f;
        _blade.SetBeamLock(AngleTo(nova));
        if (!IsLocal) return;
        _mash = _mashSent = 0;
        _mashNext = Time.time + 0.2f;
        SwipeInput.Arm(null);
        UI.DuelHud.ShowLock(true);
        ZoomIn();
    }

    // 押し合いの終わり。lost = 押し負けた (刀が下がり気が尽きる)・knock = 後ろへ弾かれる
    internal void EndBeamLock(bool lost, bool knock = true)
    {
        if (!_beamLock) return;
        _beamLock = false;
        EndLock(lost: lost, other: knock ? _lockWith : (byte)255);
    }

    // 押し合いの間の本人の動き: 光線に押されて少しずつ下がる
    private Vector2 BeamPull(Vector3 me)
    {
        var o = _lockOpp;
        if (!o) return default;
        Vector3 b = o.transform.position;
        float dx = me.x - b.x, dy = me.y - b.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.001f) return default;
        return FxMath.V2(dx / d * BeamPush, dy / d * BeamPush);
    }

    // ボット: 光線と押し合っている間の動き
    private static Vector2 BotBeamPull(byte id, PlayerControl self) =>
        RoleState.Of(id) is Samurai s && s._beamLock ? s.BeamPull(self.transform.position) : default;
}
