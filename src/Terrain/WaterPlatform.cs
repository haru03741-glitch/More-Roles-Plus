using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 動く床 (エアシップの昇降機の台): 台が居る升は発着の床と同じ高さの床になり、載った水は台と一緒に運ばれる。
// 台が通る帯の台が居ない所は空 (縁を越えた水は帯へ落ちて消える)。
// 台の位置はホストが記録した「向きが変わった刻み」から全員が整数で出す (本編の台は各自が再生するので、見た目の位置は人によって先後がある)。
// 本編の台は向きが変わった瞬間から PlatformTicks 刻みでなめらかに (始めと終わりがゆっくり) 動く (実測 2.5 秒)
internal static partial class WaterSim
{
    private const int PlatformTicks = 75;
    // 水の刻みは Delay 刻み遅れて進むので、台の位置はこの刻みだけ先を使う (見た目の台に水を近づける)。
    // 客に記録が届くまでの余裕 = Delay − PlatformLead 刻み (これより遅れて届くと客の水がずれる)
    private const int PlatformLead = 14;
    internal static int LatePlatform;
    private const int PlatformPoll = 15; // ホストが向き以外の動き (会議などでの移し替え) を見る間隔
    // 台の縁の枠の高さ (水の量)。これより薄い水は台に残り、台が動き出す・止まる時の揺れで寄った水が越えてこぼれる
    private const int PlatformRim = 96;
    // 揺れの強さ: 台の加速度 (1/1024 升/刻み²) × 深さ × これ / 1024 の勢いを、加速と逆向きに足す
    private const int SloshGain = 24;
    private const int PosOne = 1024; // 台の細かい位置の 1 升

    // 記録: 刻み・種類 (0 = 左へ動き始めた・1 = 右へ・2 = その位置へ移った)・位置 (升の数)
    private static readonly List<(int Tick, byte Kind, byte Off)> PlatLog = new();
    private static int _platShipGen = -1, _platNextPoll;
    private const byte FarRight = 255; // 記録の位置で「右の端」(升の数は升を作るまで分からない)
    private static bool _platLeftSeen, _platSeen, _pfNone;
    private static int _platLook, _platSettle;
    private const int PlatformSettle = 60;
    private static MovingPlatformBehaviour _pfObj;

    private static void NewShip()
    {
        _platShipGen = GameClock.ShipGen;
        PlatLog.Clear();
        _platSeen = false;
        _pfObj = null;
        _pfNone = false;
        _platLook = 0;
        _platSettle = 0;
        _platNextPoll = 0;
        _pfD = 0;
        _pfOff = -1;
        LatePlatform = 0;
    }

    // 台の升の形 (作った時): 左に居る時の左下の升・幅・奥行き・左右の動く升の数・床の高さ
    private static bool _plat;
    private static int _pfX0, _pfY0, _pfW, _pfH, _pfD, _pfOff = -1;
    private static sbyte _pfLvl;
    private static float _pfZ;   // 台の絵の奥行き
    private static short _pfBed;
    private static byte[] _platFloor;
    private static sbyte[] _pfBaseLvl;  // 帯の升の元の高さ
    private static short[] _pfBaseBed;

    private static MovingPlatformBehaviour FindPlatform()
    {
        var ship = ShipStatus.Instance;
        var air = ship ? ship.TryCast<AirshipStatus>() : null;
        return air ? air.GapPlatform : null;
    }

    // 台の左右の位置を升の数で (本編の台の今の位置)。左 = 0・右 = _pfD
    private static int PlatformCellsNow(MovingPlatformBehaviour pf)
    {
        float span = pf.RightPosition.x - pf.LeftPosition.x;
        if (span == 0f) return 0;
        float f = (pf.transform.localPosition.x - pf.LeftPosition.x) / span;
        return Math.Clamp((int)MathF.Round(f * _pfD), 0, _pfD);
    }

    // ── ホスト: 記録して配る (毎フレーム・水が出ていなくても) ─────────────────

    private static void PollPlatform()
    {
        if (GameClock.ShipGen != _platShipGen) NewShip();
        int now = GameClock.Now;
        if (_pfObj == null)
        {
            // 台を探すのは船ごとに 1 回 (船の準備が済むまでは間を空けて)
            if (_pfNone || now < _platLook) return;
            _platLook = now + PlatformPoll;
            if (!ShipStatus.Instance) return;
            _pfObj = FindPlatform();
            if (_pfObj == null) { _pfNone = true; return; }
        }
        if (TerrainSync.IsGuest()) return;
        var pf = _pfObj;
        if (!_platSeen)
        {
            // 最初の位置: 記録が無ければ全員が左とみなす。右に居る時だけ、客の船が出そろうのを待って (PlatformSettle 刻み後) 記録する
            // (客の船が出る前に届いた記録は、客が船の切り替わりで消してしまう)
            if (_platSettle == 0) { _platSettle = now + PlatformSettle; return; }
            if (now < _platSettle) return;
            if (!pf) { _pfObj = null; _pfNone = true; return; }
            _platSeen = true;
            _platLeftSeen = pf.IsLeft;
            if (!_platLeftSeen) AddPlatform(now, 2, FarRight, true);
            _platNextPoll = now + PlatformPoll;
            return;
        }
        bool left = pf.IsLeft;
        if (left != _platLeftSeen)
        {
            _platLeftSeen = left;
            AddPlatform(now, (byte)(left ? 0 : 1), 0, true);
            _platNextPoll = now + PlatformTicks + PlatformPoll;
            return;
        }
        if (now < _platNextPoll) return;
        _platNextPoll = now + PlatformPoll;
        if (!pf) { _pfObj = null; _pfNone = true; return; }
        if (_pfD <= 0) return;
        int at = PlatformCellsNow(pf);
        if (at != OffsetAt(now)) AddPlatform(now, 2, (byte)at, true);
    }

    private static void AddPlatform(int tick, byte kind, byte off, bool send)
    {
        int i = PlatLog.Count;
        while (i > 0 && PlatLog[i - 1].Tick > tick) i--;
        PlatLog.Insert(i, (tick, kind, off));
        if (send) TerrainSync.BroadcastPlatform(unchecked((ushort)tick), kind, off);
    }

    // 客: ホストから台の記録が届いた
    internal static void OnPlatform(int tick, byte kind, byte off)
    {
        if (GameClock.ShipGen != _platShipGen) NewShip();
        if (_ready && tick < _step + PlatformLead) LatePlatform++;
        AddPlatform(tick, kind, off, false);
    }

    // 刻み s の台の位置 (升の数)。動いている間は動く向きへ 1/4 升早く次の升へ移る (水の升が台の絵の後ろに残って見えないよう)
    private static int OffsetAt(int s)
    {
        int pos = PosAt(s), v = PosAt(s + 1) - pos;
        return (pos + (v > 0 ? PosOne * 3 / 4 : v < 0 ? PosOne / 4 : PosOne / 2)) / PosOne;
    }

    // 刻み s の台の細かい位置 (1/PosOne 升)。記録を頭から辿る (動きの途中で次の記録が来たら、その時の位置から動き直す)
    private static int PosAt(int s)
    {
        int pos = 0;
        for (int i = 0; i < PlatLog.Count && PlatLog[i].Tick <= s; i++)
        {
            var e = PlatLog[i];
            if (e.Kind == 2) { pos = Math.Min(e.Off, _pfD) * PosOne; continue; }
            int from = pos, to = e.Kind == 0 ? 0 : _pfD * PosOne;
            int end = i + 1 < PlatLog.Count && PlatLog[i + 1].Tick <= s ? PlatLog[i + 1].Tick : s;
            pos = from + (int)((long)(to - from) * Ease(end - e.Tick) / EaseOne);
        }
        return pos;
    }

    // なめらかな動き 3t² − 2t³ を整数で (t = 刻み / PlatformTicks・返り値は EaseOne 分の)
    private const int EaseOne = PlatformTicks * PlatformTicks * PlatformTicks;
    private static int Ease(int t)
    {
        t = Math.Clamp(t, 0, PlatformTicks);
        return 3 * t * t * PlatformTicks - 2 * t * t * t;
    }

    // ── 升 ─────────────────────────────────────────────────────────────

    // 台の升の形を本編の台の絵から決める (ホストの最初の記録の前にも呼ぶ)
    private static bool PrepPlatform(MovingPlatformBehaviour pf)
    {
        var sr = pf.GetComponent<SpriteRenderer>();
        if (!sr || _w == 0) return false;
        var b = sr.bounds;
        float scale = pf.transform.lossyScale.x;
        float shift = (pf.transform.localPosition.x - pf.LeftPosition.x) * scale;   // 今の位置から左の位置へ
        float span = (pf.RightPosition.x - pf.LeftPosition.x) * scale;
        // 真ん中が絵の中にある升 (境は升の真ん中から 1/2 升離れているので端末の丸めで割れない)
        int x0 = (int)MathF.Ceiling((b.min.x - shift - _org.x) / _cell - 0.5f);
        int x1 = (int)MathF.Floor((b.max.x - shift - _org.x) / _cell - 0.5f);
        int y0 = (int)MathF.Ceiling((b.min.y - _org.y) / _cell - 0.5f);
        int y1 = (int)MathF.Floor((b.max.y - _org.y) / _cell - 0.5f);
        int r0 = (int)MathF.Ceiling((b.min.x - shift + span - _org.x) / _cell - 0.5f);
        if (x1 < x0 || y1 < y0 || r0 < x0) return false;
        _pfX0 = x0; _pfY0 = y0; _pfW = x1 - x0 + 1; _pfH = y1 - y0 + 1; _pfD = r0 - x0;
        _pfZ = sr.transform.position.z;
        return true;
    }

    // 升作りの時: 台の帯を空にし、今の刻みの位置に台の床を置く (Levels の後・Links の前)
    private static void LoadPlatform()
    {
        _plat = false;
        _pfOff = -1;
        var pf = FindPlatform();
        if (!pf || !PrepPlatform(pf)) return;
        int bx1 = _pfX0 + _pfW - 1 + _pfD, by1 = _pfY0 + _pfH - 1;
        if (_pfX0 < 2 || _pfY0 < 2 || bx1 >= _w - 2 || by1 >= _h - 2) return;
        // 床の高さ = 左の発着の床 (台の左の縁の真ん中の行の 1 升左)
        int dock = (_pfY0 + _pfH / 2) * _w + _pfX0 - 1;
        if (_open[dock] == 0) return;
        _pfLvl = _lvl[dock];
        _pfBed = _bed[dock];
        _pfBaseLvl = new sbyte[(bx1 - _pfX0 + 1) * _pfH];
        _pfBaseBed = new short[_pfBaseLvl.Length];
        for (int y = _pfY0; y <= by1; y++)
        for (int x = _pfX0; x <= bx1; x++)
        {
            int k = y * _w + x, i = (y - _pfY0) * (bx1 - _pfX0 + 1) + x - _pfX0;
            _pfBaseLvl[i] = _lvl[k];
            _pfBaseBed[i] = _bed[k];
            _air[k] = 1;
        }
        _plat = true;
        MovePlatform(OffsetAt(_step + PlatformLead), false);
    }

    // 刻みの中: 台が升を移ったら、載った水を一緒に動かして升を作り直す
    private static void StepPlatform()
    {
        if (!_plat) return;
        int s = _step + PlatformLead;
        int off = OffsetAt(s);
        if (off != _pfOff) MovePlatform(off, true);
        // 揺れ: 台が速くなる・遅くなる時、載った水は置いていかれる (台から見ると加速と逆向きに押される)
        int acc = PosAt(s + 1) - 2 * PosAt(s) + PosAt(s - 1);
        if (acc == 0) return;
        int d = acc > 0 ? 1 : 0, mag = Math.Abs(acc);
        for (int y = 0; y < _pfH; y++)
        for (int x = 0; x < _pfW; x++)
        {
            int k = (_pfY0 + y) * _w + _pfX0 + _pfOff + x;
            int h = _hgt[k];
            if (h <= 0) continue;
            _flux[k * 4 + d] += (int)((long)h * mag * SloshGain / 1024);
            Wake(k);
        }
    }

    // 縁の升 k の向き d が台の縁 (台の外の空へこぼれる所) なら枠の高さ
    private static bool PlatformRimAt(int k, int to) => to == -1 && _platFloor != null && _platFloor[k] != 0;

    // 水のタイル (x, y から size 四方) が台の通る帯に掛かるなら、台の絵より手前の奥行き (台は部屋の絵より手前にある)
    internal static float PlatformFront(float x, float y, float size, float front)
    {
        if (!_plat) return front;
        float x0 = _org.x + _pfX0 * _cell, x1 = x0 + (_pfW + _pfD) * _cell;
        float y0 = _org.y + _pfY0 * _cell, y1 = y0 + _pfH * _cell;
        if (x1 <= x || x0 >= x + size || y1 <= y || y0 >= y + size) return front;
        return Math.Min(front, _pfZ);
    }

    // 床のマスクに台の今の所 (水の升の位置) を床として足す。画素 k の真ん中 = x + k × step
    internal static void StampPlatform(float x, float y, float step, int n, byte[] into)
    {
        if (!_plat || _pfOff < 0) return;
        float x0 = _org.x + (_pfX0 + _pfOff) * _cell, x1 = x0 + _pfW * _cell;
        float y0 = _org.y + _pfY0 * _cell, y1 = y0 + _pfH * _cell;
        float size = step * (n - 1);
        if (x1 <= x || x0 >= x + size || y1 <= y || y0 >= y + size) return;
        int px0 = Math.Max(0, (int)MathF.Ceiling((x0 - x) / step)), px1 = Math.Min(n - 1, (int)MathF.Floor((x1 - x) / step));
        int py0 = Math.Max(0, (int)MathF.Ceiling((y0 - y) / step)), py1 = Math.Min(n - 1, (int)MathF.Floor((y1 - y) / step));
        for (int py = py0; py <= py1; py++)
        for (int px = px0; px <= px1; px++) into[py * n + px] = 255;
    }

    private static void MovePlatform(int off, bool links)
    {
        int old = _pfOff;
        int bw = _pfW + _pfD;
        // 載っている水を先に移す (先に升を作り直すと、閉じる升の水が発着の床へこぼれる)
        int[] carry = null;
        if (old >= 0)
        {
            carry = new int[_pfW * _pfH * 5];
            for (int y = 0; y < _pfH; y++)
            for (int x = 0; x < _pfW; x++)
            {
                int k = (_pfY0 + y) * _w + _pfX0 + old + x;
                if (_platFloor[k] == 0) continue;
                int c = (y * _pfW + x) * 5;
                carry[c] = _hgt[k];
                _hgt[k] = 0;
                for (int d = 0; d < 4; d++) { carry[c + 1 + d] = _flux[k * 4 + d]; _flux[k * 4 + d] = 0; }
                Mark(k);
                _platFloor[k] = 0;
                int i = y * bw + old + x;
                _lvl[k] = _pfBaseLvl[i];
                _bed[k] = _pfBaseBed[i];
            }
        }
        for (int y = 0; y < _pfH; y++)
        for (int x = 0; x < _pfW; x++)
        {
            int k = (_pfY0 + y) * _w + _pfX0 + off + x;
            _platFloor[k] = 1;
            _lvl[k] = _pfLvl;
            _bed[k] = _pfBed;
            int c = (y * _pfW + x) * 5;
            if (carry == null || carry[c] == 0) continue;
            _hgt[k] += carry[c];
            for (int d = 0; d < 4; d++) _flux[k * 4 + d] = carry[c + 1 + d];
            Wake(k);
            Mark(k);
        }
        _pfOff = off;
        int x0 = _pfX0 + Math.Min(Math.Max(old, 0), off), x1 = _pfX0 + Math.Max(old, off) + _pfW - 1;
        Rebuild(x0 - 1, _pfY0 - 1, x1 + 1, _pfY0 + _pfH);
        if (!links) return;
        int reach = FallScan[3] + 1;
        Links(x0 - reach, _pfY0 - reach, x1 + reach, _pfY0 + _pfH - 1 + reach);
        WaterArt.FloorChanged(Rect.MinMaxRect(_org.x + (x0 - 1) * _cell, _org.y + (_pfY0 - 1) * _cell,
                                              _org.x + (x1 + 2) * _cell, _org.y + (_pfY0 + _pfH + 1) * _cell));
    }
}
