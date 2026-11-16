using UnityEngine;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦の操縦席の操作 (PC)。覚える物は 4 つ: マウスの位置 = 機首の向け先 (画面の中心からのずれを仮想の操縦桿にする)・
// 左クリック = 撃つ (押している間・手を止めると溜まり、満ちてから押すと溜め撃ち)・右クリック = かわす (曲がっている向きへ横転・
// 後ろに付かれている時は宙返りで向き直る)・Shift = 加速。傾きは曲がりに合わせて自動。Esc = 席を離れる。
// 同じ働きの別のキー: W = 加速・Space = 撃つ・Q/E = 左右を決めてかわす。
// スマホの操作 (スティック + ボタン・はじきで回避) は同じ Input を作る別の入口として足す
internal static class FlightInput
{
    private const float Dead = 0.06f;
    internal static bool InvertPitch = false;
    // 確かめ用: 外から入力を置く (bridge)。残り時間が 0 より大きい間はこちらを使う
    internal static FlightNative.Input Forced;
    internal static float ForcedT;
    // 右クリックの回避の向き = 今曲がっている向き (まっすぐなら最後に曲がった向き)
    private static int _lastSide = 1;

    internal static FlightNative.Input Read(out bool leave)
    {
        leave = Input.GetKeyDown(KeyCode.Escape);
        if (ForcedT > 0)
        {
            ForcedT -= Time.deltaTime;
            var f = Forced;
            Forced.Dodge = 0;
            Forced.UTurn = 0;
            return f;
        }
        var i = new FlightNative.Input();
        float w = Screen.width, h = Screen.height;
        float r = Mathf.Min(w, h) * 0.32f;
        var m = Input.mousePosition;
        float x = Mathf.Clamp((m.x - w * 0.5f) / r, -1f, 1f);
        float y = Mathf.Clamp((m.y - h * 0.5f) / r, -1f, 1f);
        i.Yaw = Shape(x);
        i.Pitch = Shape(y) * (InvertPitch ? -1f : 1f);
        if (i.Yaw > 0.05f) _lastSide = 1;
        else if (i.Yaw < -0.05f) _lastSide = -1;
        i.Boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.W) ? 1 : 0;
        i.Fire = Input.GetMouseButton(0) || Input.GetKey(KeyCode.Space) ? 1 : 0;
        i.Dodge = Input.GetMouseButtonDown(1) ? _lastSide : Input.GetKeyDown(KeyCode.Q) ? -1 : Input.GetKeyDown(KeyCode.E) ? 1 : 0;
        return i;
    }

    // 中心の遊びを除き、端ほど強く (細かい狙いと大きな旋回を両立)
    private static float Shape(float v)
    {
        float a = Mathf.Abs(v);
        if (a < Dead) return 0f;
        a = (a - Dead) / (1f - Dead);
        return Mathf.Sign(v) * (a * 0.55f + a * a * 0.45f);
    }
}
