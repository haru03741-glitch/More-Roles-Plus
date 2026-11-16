using HarmonyLib;

namespace MoreRolesPlus.Flight;

// 本編のスケルドの船の外の動き (タスクの見た目: 武器庫の砲台が小惑星を撃つ・倉庫のゴミの口が開いてゴミを捨てる・シールドの光が点く) を
// 3D のスケルドにも出す。本編がそれぞれの動きを始める関数 (全員に届く出来事から呼ばれる) の後ろで受けるので、通信は足さない。
// 本編が動きを出さない時 (タスクの見た目の設定が切ってある時) は 3D でも出ない
internal static unsafe class ShipTasks
{
    // シールドの光は一度点くと試合の間は点いたまま (本編と同じ)
    internal static bool ShieldOn;

    private const int MaxShots = 6;
    private static readonly float[] ShotAt = new float[MaxShots];
    private static int _shots, _barrel;
    private static float _aimYaw, _aimPitch, _aimBack = -1f;
    private static float _garbageAt = -1f, _hatchCloseAt = -1f;
    private static bool _hatchOpen, _aimDirty;
    private static readonly System.Random Rnd = new();
    private static float _now;

    // 砲身が向きを変え終わってから撃つまで (部品の追う速さ 3.5 で 9 割ほど回る時間)・同じ砲台の次の 1 発までの最短
    private const float AimTime = 0.45f, ShotGap = 0.22f;
    // 小惑星までの距離 (光線が届いた所で砕ける)
    private const float RockRange = 150f;

    internal static void Begin()
    {
        _now = UnityEngine.Time.time;
        _shots = 0;
        _barrel = 0;
        _aimYaw = _aimPitch = 0f;
        _aimBack = _garbageAt = _hatchCloseAt = -1f;
        _hatchOpen = false;
        _aimDirty = true;
        var ss = ShipStatus.Instance;
        var si = ss ? ss.ShieldsImages : null;
        ShieldOn = si != null && si.Length > 0 && si[0] && si[0].Playing;
    }

    internal static void FireWeapon()
    {
        if (!Arena.Active || _shots >= MaxShots) return;
        // 撃つたびに別の小惑星へ向ける (横は前から船の外側へ・縦は前の上半分)
        _aimYaw = (float)(Rnd.NextDouble() * 1.3 - 0.3);
        _aimPitch = (float)(Rnd.NextDouble() * 0.9 + 0.1);
        _aimDirty = true;
        float at = _now + AimTime;
        if (_shots > 0 && ShotAt[_shots - 1] + ShotGap > at) at = ShotAt[_shots - 1] + ShotGap;
        ShotAt[_shots++] = at;
        _aimBack = -1f;
    }

    internal static void OpenHatch()
    {
        if (!Arena.Active) return;
        // 本編のゴミの口: 開く → 中身が飛び出す → 閉じる (約 1.5 秒)
        _hatchOpen = true;
        _aimDirty = true;
        _garbageAt = _now + 0.3f;
        _hatchCloseAt = _now + 1.4f;
    }

    internal static void StartShields() => ShieldOn = true;

    // フレームに 1 回 (C++ の刻みの前)。スケルド (形 0) の船だけへ
    internal static void Tick(System.IntPtr world, int shipN, int[] model, float now)
    {
        _now = now;
        bool garbage = false;
        int fire = -1;
        if (_shots > 0 && now >= ShotAt[0])
        {
            fire = _barrel;
            _barrel ^= 1;
            for (int i = 1; i < _shots; i++) ShotAt[i - 1] = ShotAt[i];
            _shots--;
            if (_shots == 0) _aimBack = now + 1.6f;
        }
        if (_aimBack >= 0 && now >= _aimBack)
        {
            _aimBack = -1f;
            _aimYaw = _aimPitch = 0f;
            _aimDirty = true;
        }
        if (_garbageAt >= 0 && now >= _garbageAt) { _garbageAt = -1f; garbage = true; }
        if (_hatchCloseAt >= 0 && now >= _hatchCloseAt) { _hatchCloseAt = -1f; _hatchOpen = false; _aimDirty = true; }
        if (!_aimDirty && fire < 0 && !garbage) return;
        for (int k = 0; k < shipN; k++)
        {
            if (model[k] != 0) continue;
            if (_aimDirty)
            {
                FlightNative.Aux(world, k, FlightNative.AuxAimYaw, _aimYaw);
                FlightNative.Aux(world, k, FlightNative.AuxAimPitch, _aimPitch);
                FlightNative.Aux(world, k, FlightNative.AuxHatch, _hatchOpen ? 1f : 0f);
            }
            if (fire >= 0) FlightNative.Turret(world, k, fire, RockRange);
            if (garbage) FlightNative.Garbage(world, k);
        }
        _aimDirty = false;
    }
}

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.FireWeapon))]
internal static class ShipFireWeaponPatch
{
    public static void Postfix() => ShipTasks.FireWeapon();
}

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.OpenHatch))]
internal static class ShipOpenHatchPatch
{
    public static void Postfix() => ShipTasks.OpenHatch();
}

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.StartShields))]
internal static class ShipStartShieldsPatch
{
    public static void Postfix() => ShipTasks.StartShields();
}
