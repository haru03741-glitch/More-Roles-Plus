// Based on https://github.com/Gurge44/EndlessHostRoles Patches/ControlPatch.cs (GPL-3.0)
using System;
using UnityEngine;

namespace MoreRolesPlus;

// Ctrl を押している間だけ壁を通り抜ける (PC)。使えるのはロビーとフリープレイ・練習の試合、開発者は試合中も。
// 自分の足元の当たり判定を切って壁に触れなくする (位置の同期はそのままなので他の人にも普通に動いて見える)。
// 当たり判定を遠くへずらす方法だと、足元の位置 (タスク・キルの距離・水の深さが使う) まで一緒にずれるので使わない。
// 毎フレームの問い合わせは Ctrl の 2 つだけで、切る・戻すは切り替わった時だけ書く
internal static class NoClip
{
    private static readonly bool Enabled = !OperatingSystem.IsAndroid();
    private static PlayerControl _player;
    private static bool _on;
    private static int _frames;

    // 試験用: Ctrl を押しっぱなしにしたのと同じ
    public static bool Hold;
    public static bool On => _on;

    public static void Tick()
    {
        if (!Enabled) return;
        bool want = Hold || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (!want && !_on) return;
        // 自分の人が消えた (場面の切り替え) = 切った当たり判定も一緒に消えている
        if (_on && !_player) { _player = null; _on = false; }
        if (!want) { Restore(); return; }
        if (_on)
        {
            // 押しっぱなしのままロビーから試合へ移った時に止める (確かめるのは時々だけ)
            if (++_frames % 30 == 0 && !Allowed()) Restore();
            return;
        }
        if (!Allowed()) return;
        var lp = PlayerControl.LocalPlayer;
        var col = lp ? lp.Collider : null;
        // はしご・昇降床の途中など本編が切っている間は触らない (戻す時に勝手に入れてしまうため)
        if (!col || !col.enabled) return;
        _player = lp;
        col.enabled = false;
        _on = true;
    }

    private static void Restore()
    {
        if (_player)
        {
            var col = _player.Collider;
            if (col) col.enabled = true;
        }
        _player = null;
        _on = false;
    }

    private static bool Allowed()
    {
        var client = AmongUsClient.Instance;
        if (!client) return false;
        if (LobbyBehaviour.Instance && !ShipStatus.Instance) return true;
        if (!ShipStatus.Instance) return false;
        return Roles.PracticeMatch.Sandbox || Dev.DevUsers.AmDev;
    }
}
