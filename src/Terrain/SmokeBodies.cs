using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 煙の中の体: 粉の煙と火の煙の粒を、人の体のまわりから押しのける。体は物としてそこにあるので、
// 止まっている人のまわりは煙が回り込み、歩くと体の幅に押し分けられて後ろに筋が残る。
// 役職で見えない人も、体の跡を残す役職なら同じく押す (見えるのは煙の形だけ)。
// 位置は 0.1 秒ごとに控えて、粒を動かす所では控えた位置を歩く速さで先へ進めて使う (毎フレームの interop を増やさない)
internal static class SmokeBodies
{
    private const int Max = 20;
    private const long SampleMs = 100;
    private const float CenterUp = 0.35f;   // 足元から体の真ん中まで (煙は体の高さで押される)
    private const float BodyR = 0.3f;       // 体の半分の幅
    private const float StillPush = 0.35f;  // 止まっている体が回り込ませる強さ (速さの倍率に足す)
    private const float Push = 4f;          // 押しのける加速 (1/秒)
    private const float Along = 0.35f;      // 歩く向きへ引きずる割合

    private static readonly float[] X = new float[Max], Y = new float[Max], Vx = new float[Max], Vy = new float[Max];
    private static readonly float[] LastX = new float[256], LastY = new float[256];
    private static readonly bool[] Had = new bool[256];
    private static int _n;
    private static long _nextMs, _sampledMs;
    private static float _ahead;              // 控えてからの秒 (このフレームの Tick で決める)
    private static long _smokeAtMs;           // InSmoke の答えを覚えた時
    private static float _smokeX, _smokeY;
    private static bool _smokeWas;
    private static int _shipGen = -1;

    internal static int Count => _n;

    // 煙の粒が出ている間、毎フレーム (煙の Tick から・何か所から呼んでも 0.1 秒に 1 回だけ控える)
    internal static void Tick()
    {
        long now = Environment.TickCount64;
        _ahead = _sampledMs == 0 ? 0f : Math.Min(0.2f, (now - _sampledMs) / 1000f);
        if (now < _nextMs) return;
        // 煙が無かった間・時戻しの逆再生の間は控えていないので、前の控えとの差を速さにしない
        bool stale = _sampledMs == 0 || now - _sampledMs > 500;
        float span = stale ? 0f : (now - _sampledMs) / 1000f;
        if (stale) Array.Clear(Had, 0, Had.Length);
        _nextMs = now + SampleMs;
        _sampledMs = now;
        _ahead = 0f;
        if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; Array.Clear(Had, 0, Had.Length); }
        _n = 0;
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count && _n < Max; i++)
        {
            var pc = all[i];
            if (!pc) continue;
            var data = pc.Data;
            byte id = pc.PlayerId;
            if (data == null || data.IsDead || data.Disconnected || pc.inVent || !Roles.CrewTraits.LeavesTrace(pc)) { Had[id] = false; continue; }
            Vector2 pos = pc.GetTruePosition();
            float vx = 0f, vy = 0f;
            if (Had[id] && span > 0f)
            {
                vx = (pos.x - LastX[id]) / span; vy = (pos.y - LastY[id]) / span;
                if (vx * vx + vy * vy > 100f) vx = vy = 0f; // 飛んだ (通気口・転送)
            }
            LastX[id] = pos.x; LastY[id] = pos.y; Had[id] = true;
            X[_n] = pos.x; Y[_n] = pos.y + CenterUp; Vx[_n] = vx; Vy[_n] = vy;
            _n++;
        }
    }

    // (x, y) にある半径 r の煙の粒が dt 秒のあいだに受ける速さの変わり。押す体が無ければ false
    internal static bool PushAt(float x, float y, float r, float dt, out float dvx, out float dvy)
    {
        dvx = dvy = 0f;
        if (_n == 0) return false;
        float ahead = _ahead;
        bool any = false;
        float reach = r + BodyR;
        for (int i = 0; i < _n; i++)
        {
            float bx = X[i] + Vx[i] * ahead, by = Y[i] + Vy[i] * ahead;
            float dx = x - bx, dy = y - by;
            float d2 = dx * dx + dy * dy;
            if (d2 >= reach * reach) continue;
            float d = MathF.Sqrt(d2);
            float sp = MathF.Sqrt(Vx[i] * Vx[i] + Vy[i] * Vy[i]);
            float k = (1f - d / reach) * (StillPush + sp) * Push * dt;
            float ux, uy;
            if (d > 0.0001f) { ux = dx / d; uy = dy / d; }
            else if (sp > 0.0001f) { ux = -Vy[i] / sp; uy = Vx[i] / sp; }
            else { ux = 0f; uy = 1f; }
            dvx += ux * k + Vx[i] * Along * k / (StillPush + sp);
            dvy += uy * k + Vy[i] * Along * k / (StillPush + sp);
            any = true;
        }
        return any;
    }

    // 足元 (pos) が煙の中か (粉の煙が濃い所・火の煙の粒が集まっている所)
    // 動いている間は毎物理刻みで聞かれるので、0.1 秒・0.2 マスの内は前の答えを使う
    internal static bool InSmoke(Vector2 pos)
    {
        long now = Environment.TickCount64;
        float dx = pos.x - _smokeX, dy = pos.y - _smokeY;
        if (now - _smokeAtMs < SampleMs && dx * dx + dy * dy < 0.04f) return _smokeWas;
        _smokeAtMs = now; _smokeX = pos.x; _smokeY = pos.y;
        _smokeWas = DustCloud.DensityAt(pos.x, pos.y) >= DustCloud.InMin || FireFx.SmokeNear(pos.x, pos.y + CenterUp, 0.7f) >= 3;
        return _smokeWas;
    }
}
