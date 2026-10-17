using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// クルーと同じ奥行きに置かれた、歩ける床の絵 (ドロップシップの坂・床)。本編ではクルーより奥・部屋の絵より手前にあり、
// 水のタイル (部屋の絵のすぐ手前) がこの絵の下に隠れる。床は常にクルーより奥に描かれるので、
// 試合の始めに絵を水のタイルのすぐ奥 (後ろの部屋の絵のすぐ手前) へ移し、掛かるタイルはこの絵より手前に置く。
// 床マスクもこの絵を床の層として作る (tools/make-floor-mask.py の FLOOR_COVERS と同じ表)
internal static class FloorCovers
{
    // 後ろの部屋の絵からの奥行きのずらし。掛かる水のタイルはここから 0.0004 手前 (Front)
    private const float Behind = 0.0002f, Step = 0.00001f;
    private const int Probe = 5; // 後ろの部屋の絵を引く点の数 (縦横)

    private static int _gen = -1;
    private static readonly List<(float X0, float Y0, float X1, float Y1, float Z)> Rects = new();
    private static readonly Dictionary<int, float> Orig = new(); // 移す前の z (同じ絵をもう一度移しても元から数える)
    private static int _origShip;
    internal static string Stats { get; private set; } = "-";

    // 床に置いた平たい物 (医務室のスキャン台・原子炉の床の光る枠・排水溝・床のタオル・浜の貝殻と枝) も同じく床として水の下に沈める。
    // コマ送りの絵は名前の後ろに番号が付くので頭で見る
    internal static bool Is(string ship, string sprite) => ship switch
    {
        "SkeldShip" => sprite is "MedPlatform" || sprite.StartsWith("reactorfloor", StringComparison.Ordinal),
        "MiraShip" => sprite is "med-scanner",
        "PolusShip" => sprite is "ramp" or "watergrate" || sprite.StartsWith("water1", StringComparison.Ordinal) || sprite.StartsWith("waterDrip", StringComparison.Ordinal),
        "Airship" => sprite is "task_towelflat" or "task_towelwet" or "task_towelbunched" or "panel_flashdrive",
        "FungleShip" => sprite is "dropship_floorandramp" or "rampwithsand2_nobox" or "console_collectSeashell" or "findstick_console" or "console_throwFrisbee",
        _ => false,
    };

    // 船ごとに 1 回。部屋の絵の一覧ができるまでは毎回やり直す
    internal static void Ensure()
    {
        int gen = GameClock.ShipGen;
        if (gen == _gen) return;
        var ship = ShipStatus.Instance;
        if (!GameClock.ShipAlive || !ship) { _gen = gen; Rects.Clear(); Orig.Clear(); Stats = "no ship"; return; }
        if (!DamageMap.Ready()) return;
        _gen = gen;
        Rects.Clear();
        int shipId = ship.GetInstanceID();
        if (shipId != _origShip) { Orig.Clear(); _origShip = shipId; }
        string name = ship.name.Replace("(Clone)", "");
        var sb = new StringBuilder();
        // 元の奥行きの奥から順に移し、移した先でも前後を保つ (順位ごとに Step ずつ手前)
        var found = new List<(SpriteRenderer Sr, float Z)>();
        foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (!sr || !sr.sprite || !Is(name, sr.sprite.name)) continue;
            int id = sr.GetInstanceID();
            if (!Orig.TryGetValue(id, out float oz)) Orig[id] = oz = sr.transform.position.z;
            found.Add((sr, oz));
        }
        found.Sort((a, b) => b.Z.CompareTo(a.Z));
        Renderer[] all = found.Count > 0 ? ship.GetComponentsInChildren<Renderer>(false) : null;
        for (int rank = 0; rank < found.Count; rank++)
        {
            var sr = found[rank].Sr;
            var tr = sr.transform;
            var p = tr.position;
            p.z = found[rank].Z;
            var b = sr.bounds;
            Vector3 mn = b.min, mx = b.max;
            float back = float.MaxValue;
            for (int i = 0; i < Probe; i++)
                for (int k = 0; k < Probe; k++)
                {
                    float x = mn.x + (i + 0.5f) / Probe * (mx.x - mn.x), y = mn.y + (k + 0.5f) / Probe * (mx.y - mn.y);
                    back = Math.Min(back, DamageMap.FrontZ(new Vector2(x, y)));
                }
            // 元の奥行きと後ろの部屋の絵の間にある絵は、移すとこの絵より手前に出る。確かめられるようにログへ出す
            // (Fungle の地面の飾りの網目 BaseOverlays は、坂の絵の透明な隅に小さな三角形があるだけ)
            foreach (var r in all)
            {
                if (!r || r == sr) continue;
                float rz = r.transform.position.z;
                if (rz <= p.z || rz >= back) continue;
                var rb = r.bounds;
                if (rb.max.x <= mn.x || rb.min.x >= mx.x || rb.max.y <= mn.y || rb.min.y >= mx.y || r.name.StartsWith("Mrp", StringComparison.Ordinal)) continue;
                sb.Append(" [under ").Append(r.name).Append(" z=").Append(rz.ToString("0.####")).Append(" box=").Append(rb.min.x.ToString("0.##")).Append(',').Append(rb.min.y.ToString("0.##"))
                  .Append("..").Append(rb.max.x.ToString("0.##")).Append(',').Append(rb.max.y.ToString("0.##")).Append(']');
            }
            // 後ろの部屋の絵が自分より手前 (引けない) なら動かさない
            if (back <= p.z) { sb.Append(' ').Append(sr.sprite.name).Append(" kept z=").Append(p.z.ToString("0.###")); continue; }
            float z = back - Behind - rank * Step;
            tr.position = new Vector3(p.x, p.y, z);
            Rects.Add((mn.x, mn.y, mx.x, mx.y, z));
            sb.Append(' ').Append(sr.sprite.name).Append(' ').Append(p.z.ToString("0.####")).Append("->").Append(z.ToString("0.####"));
        }
        Stats = Rects.Count == 0 && sb.Length == 0 ? "none" : sb.ToString().TrimStart();
        if (sb.Length > 0) Plugin.Logger.LogInfo($"[FloorCovers] {Stats}");
    }

    // 水のタイル (x, y から size 四方) がこの絵に掛かるなら、絵より手前の奥行き
    internal static float Front(float x, float y, float size, float front)
    {
        Ensure();
        foreach (var r in Rects)
            if (r.X1 > x && r.X0 < x + size && r.Y1 > y && r.Y0 < y + size) front = Math.Min(front, r.Z);
        return front;
    }
}
