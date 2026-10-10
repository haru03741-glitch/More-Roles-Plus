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

    private static float _vx, _vy;
    private static bool _swim;
    internal static float Depth { get; private set; }
    internal static bool Swimming => _swim;

    // 水がある間だけ CrewPull の入口を開ける
    internal static bool Wet => WaterSim.Running && WaterSim.Ready;

    internal static void Clear()
    {
        _swim = false;
        Depth = 0f;
        _vx = _vy = 0f;
    }

    internal static void Physics(Rigidbody2D body, Vector2 pos, float speed)
    {
        float d = WaterSim.CurrentAt(pos, out float cx, out float cy);
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
        body.velocity = FxMath.V2(_vx, _vy);
    }

    internal static void Register()
    {
        TestBridge.Register("swim", "自分の足元の水の深さ・流れ・泳いでいるか", (args, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp) { reply("ERR swim: no player"); return; }
            float d = WaterSim.CurrentAt(lp.GetTruePosition(), out float cx, out float cy);
            reply($"OK swim depth={d:0.000} current=({cx:0.00},{cy:0.00}) swim={_swim} v=({_vx:0.00},{_vy:0.00}) walk={CrewPull.Speed:0.00}");
        });
    }
}
