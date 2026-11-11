using MoreRolesPlus.Fx;

namespace MoreRolesPlus.Roles;

// 止まると死ぬ・時間切れで死ぬ役職の、最後の数秒を本人にだけ刻む音 (札の数字だけでは見落とす)。
// 1 秒ごとに 1 回、0 に近いほど少し高く鳴らす
internal static class CountdownWarn
{
    internal static void Tick(ref int lastSec, float left, int from)
    {
        int sec = left <= 0f ? 0 : (int)System.Math.Ceiling(left);
        if (sec == lastSec) return;
        lastSec = sec;
        if (sec <= 0 || sec > from) return;
        Hearing.Self("timer_warn", 0.45f, 1f + 0.08f * (from - sec));
    }
}
