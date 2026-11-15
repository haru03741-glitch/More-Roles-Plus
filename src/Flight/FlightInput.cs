using UnityEngine;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦の操作 (PC)。マウスの位置 = 機首の向け先 (画面の中心からのずれを仮想の操縦桿にする)・A/D = 横の回転・
// W/Shift = 加速・S = 減速・左クリック/Space = 撃つ (押している間)・Q/E = 横転の回避・R = 宙返りで向きを変える・Esc = 席を離れる。
// スマホの操作 (スティック + ボタン) は同じ Input を作る別の入口として足す
internal static class FlightInput
{
    private const float Dead = 0.06f;
    internal static bool InvertPitch = false;
    // 確かめ用: 外から入力を置く (bridge)。残り時間が 0 より大きい間はこちらを使う
    internal static FlightNative.Input Forced;
    internal static float ForcedT;

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
        i.Roll = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        i.Boost = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.LeftShift) ? 1 : 0;
        i.Brake = Input.GetKey(KeyCode.S) ? 1 : 0;
        i.Fire = Input.GetMouseButton(0) || Input.GetKey(KeyCode.Space) ? 1 : 0;
        i.Dodge = Input.GetKeyDown(KeyCode.Q) ? -1 : Input.GetKeyDown(KeyCode.E) ? 1 : 0;
        i.UTurn = Input.GetKeyDown(KeyCode.R) ? 1 : 0;
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
