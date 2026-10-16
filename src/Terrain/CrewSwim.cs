using System;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水の中の自分の体: 浅い水では足を取られて遅くなり少し流され、腰より深い水では泳ぐ (遅く・勢いが残り・流れに運ばれる)。
// 本編の歩行 (入力 → 速さ) の後で速さを書き換える (CrewPull から)。当たり判定は物理が見るので壁で止まる。
// 水の深さと流れは WaterSim の確定の場を読む (見せる側なので端末ごとの小数でよい・位置は本編の同期で配られる)
internal static class CrewSwim
{
    private const float WadeDepth = 0.12f;  // これより深いと足を取られる
    private const float SwimDepth = 0.35f;  // これより深いと泳ぐ
    private const float WadeSlow = 0.4f;    // 泳ぐ手前の深さで歩く速さから引く割合
    private const float WadeCarry = 0.5f;   // 泳ぐ手前の深さで流れに運ばれる割合
    private const float SwimMul = 0.6f;     // 泳ぐ速さ (歩く速さの倍)
    private const float SwimCarry = 1.0f;
    private const float SwimEase = 0.08f;   // 泳いでいる時に 1 物理刻みで狙いの速さへ寄る割合 (勢いが残る)
    private const float MaxMul = 3f;        // 速さの上限 (歩く速さの倍)
    // 踏ん張り: 強い流れ (歩く速さの BraceCur 倍以上) に逆らって歩くと、流されずに少しずつ上流へ進む。
    // 進む速さは逆らう入力の BraceGain 倍を平均に、足を踏み出す度に強まり踏みしめる間は弱まる (CrewBrace の足取りと同じ時計)
    internal const float BraceCur = 0.35f;
    internal const float BraceMaxDepth = 0.6f;   // これより深いと足が着かない
    private const float BraceGain = 0.3f;
    private const float BracePulse = 0.6f;
    private const float BraceAgainst = 0.5f;     // 入力の向きが上流側へこれ以上 (cos) 向いている時
    private const long BraceHoldMs = 250;

    private static float _vx, _vy;
    private static bool _swim;
    private static long _braceUntil;
    internal static float Depth { get; private set; }
    internal static bool Swimming => _swim;
    internal static bool Bracing => Environment.TickCount64 < _braceUntil;

    // 確認用: 自分の足元の水の深さと流れをこの値に固定する (Forced の間は水が無くても入口を開ける)
    internal static bool Forced;
    internal static float ForceDepth, ForceCx, ForceCy;

    // 水がある間だけ CrewPull の入口を開ける
    internal static bool Wet => Forced || (WaterSim.Running && WaterSim.Ready);

    internal static float FlowAt(Vector2 pos, out float cx, out float cy)
    {
        if (Forced) { cx = ForceCx; cy = ForceCy; return ForceDepth; }
        return WaterSim.CurrentAt(pos, out cx, out cy);
    }

    internal static void Clear()
    {
        _swim = false;
        Depth = 0f;
        _vx = _vy = 0f;
        _braceUntil = 0;
    }

    internal static void Physics(Rigidbody2D body, Vector2 pos, float speed)
    {
        float d = FlowAt(pos, out float cx, out float cy);
        Depth = d;
        var v = body.velocity;
        if (d < WadeDepth)
        {
            // 水から出た: 泳いでいた勢いは歩きに戻す
            _swim = false;
            _vx = v.x; _vy = v.y;
            return;
        }
        float lim = speed * MaxMul;
        if (d < SwimDepth)
        {
            float t = (d - WadeDepth) / (SwimDepth - WadeDepth);
            float slow = 1f - WadeSlow * t, carry = WadeCarry * t;
            float nx = v.x * slow + cx * carry, ny = v.y * slow + cy * carry;
            Brace(d, v.x, v.y, cx, cy, speed, ref nx, ref ny);
            _swim = false;
            _vx = nx; _vy = ny;
            body.velocity = FxMath.V2(nx, ny);
            return;
        }
        if (!_swim) { _swim = true; _vx = v.x; _vy = v.y; }
        float tx = v.x * SwimMul + cx * SwimCarry, ty = v.y * SwimMul + cy * SwimCarry;
        _vx += (tx - _vx) * SwimEase;
        _vy += (ty - _vy) * SwimEase;
        float s2 = _vx * _vx + _vy * _vy;
        if (s2 > lim * lim) { float k = lim / MathF.Sqrt(s2); _vx *= k; _vy *= k; }
        Brace(d, v.x, v.y, cx, cy, speed, ref _vx, ref _vy);
        body.velocity = FxMath.V2(_vx, _vy);
    }

    // 強い流れに逆らう入力 (ix, iy) の時、流れの向きの速さを「上流へ少し進む」より下流側にさせない。横の動きはそのまま
    private static bool Brace(float d, float ix, float iy, float cx, float cy, float speed, ref float nx, ref float ny)
    {
        if (d >= BraceMaxDepth) return false;
        float c2 = cx * cx + cy * cy, lim = BraceCur * speed;
        if (c2 < lim * lim) return false;
        float c = MathF.Sqrt(c2), ux = cx / c, uy = cy / c;
        float i2 = ix * ix + iy * iy;
        if (i2 < 0.01f * speed * speed) return false;
        float against = -(ix * ux + iy * uy);
        if (against < BraceAgainst * MathF.Sqrt(i2)) return false;
        _braceUntil = Environment.TickCount64 + BraceHoldMs;
        float floor = -BraceGain * against * (1f + BracePulse * CrewBrace.StepPush());
        float along = nx * ux + ny * uy;
        if (along > floor) { nx += (floor - along) * ux; ny += (floor - along) * uy; }
        return true;
    }

    internal static void Register()
    {
        TestBridge.Register("swim", "自分の足元の水の深さ・流れ・泳いでいるか", (args, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp) { reply("ERR swim: no player"); return; }
            float d = FlowAt(lp.GetTruePosition(), out float cx, out float cy);
            reply($"OK swim depth={d:0.000} current=({cx:0.00},{cy:0.00}) swim={_swim} brace={Bracing} v=({_vx:0.00},{_vy:0.00}) walk={CrewPull.Speed:0.00}");
        });
    }
}
