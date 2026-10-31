using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 機関銃の弾を受ける。当たったかどうかは撃たれたサムライ本人の端末 (練習の相手はホスト) が決める:
// 刀の向き・振っている最中か・気は本人の手元でしか正確に分からないため。決めた数はまとめてホストへ送る (Impostor/TriggerHappy.cs)
//   振っている・突いている最中 = 弾を斬る (気は使わない)。段 2 以上の居合なら撃った人へ跳ね返す
//   抜いていて正面 (向いている方の ±50°) から来た弾 = 刀で受け流す (1 発ごとに気を使う・尽きたら体で受ける)
//   踏み込みの最中 = 素通り・それ以外 = 体に当たる (体力が削れる)
public sealed partial class Samurai
{
    internal enum BulletFate : byte { Hit, Parry, Cut, Reflect, Pass }

    internal const int BulletDamage = 2;     // 体に当たった 1 発で減る体力
    internal const int ReflectDamage = 3;    // 跳ね返した 1 発が撃った人に当たって減る体力
    internal const float ParryKi = 1.5f;     // 受け流した 1 発で使う気
    private const float ParryCos = 0.64f;    // 正面とみなす角 (cos 50°)

    private const float ReflectSlack = 0.8f; // 跳ね返しの申告を受ける、振り終わりからの余裕 (まとめて送る 0.2 秒と届くまでの遅れ)
    private float _hostReflectUntil = -10f;

    // ホスト: 今、跳ね返したという申告を受けてよいか (段 2 以上の居合を振った直後だけ)
    internal bool HostMayReflect => Time.time < _hostReflectUntil;

    // 歩いた向き (本人の端末)。止まっている間は最後に歩いた向き
    private float _faceX = 1f, _faceY;

    // この端末が弾の当たりを決める相手か
    internal bool JudgesBullets => IsLocal || Player && Player.isDummy && AmongUsClient.Instance && AmongUsClient.Instance.AmHost;

    // 弾が体に届いた (dx, dy = 弾の進む向き)
    internal BulletFate TakeBullet(float dx, float dy)
    {
        var p = Player;
        if (!p) return BulletFate.Hit;
        if (IsLocal && Dashing(out _)) return BulletFate.Pass;
        bool spent = IsLocal ? Spent : _blade.Spent;
        if (!Drawn || spent || _blade.Stunned) return BulletFate.Hit;
        if (_blade.Cutting(out int tier)) return tier >= 2 ? BulletFate.Reflect : BulletFate.Cut;
        // 弾が来る向き (弾の進む向きの逆) が、向いている方の正面か
        bool left = p.cosmetics && p.cosmetics.FlipX;
        float fx = _faceX, fy = _faceY;
        if (!IsLocal || fx < 0f != left) { fx = left ? -1f : 1f; fy = 0f; }
        if (-(dx * fx + dy * fy) < ParryCos) return BulletFate.Hit;
        if (DevFree(p)) return BulletFate.Parry;
        if (IsLocal)
        {
            if (_ki < ParryKi) return BulletFate.Hit;
            UseKi(ParryKi);
            return BulletFate.Parry;
        }
        if (HostKiNow() < ParryKi) return BulletFate.Hit;
        _hostKi -= ParryKi;
        return BulletFate.Parry;
    }

    // ホスト: 本人の端末が受け流した数 (気の見積もりを合わせる)
    internal void HostSpendParry(int n)
    {
        if (IsLocal || Player && Player.isDummy) return;
        HostKiNow();
        _hostKi -= ParryKi * n;
    }

    // ホスト: 跳ね返した弾が撃った人に当たった
    internal bool HostReflect(PlayerControl shooter, int damage) => HostApply(shooter, damage);

    // 刀の先 (弾を受けた火花を出す所): 体の中心から弾の来た方へ
    internal Vector2 BladeTip(float dx, float dy)
    {
        if (!Player) return FxMath.V2(0f, 0f);
        var c = Player.transform.position;
        return FxMath.V2(c.x - dx * 0.45f, c.y - dy * 0.45f + 0.05f);
    }
}
