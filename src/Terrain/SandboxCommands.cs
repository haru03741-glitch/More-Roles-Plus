using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 練習の試合とフリープレイで、誰でもチャット欄から地形の破壊を試せるコマンド。
//   /hammer [強さ 0〜1]  いちばん近い壁を叩く (3 回で崩れる)
//   /blast [半径]        自分の所で爆発させる
// 依頼は武器と同じ TerrainSync.Request を通るので、同期とホストの検査もそのまま確かめられる
internal static class SandboxCommands
{
    private const int ShipLayer = 9;
    private const float DefaultForce = 0.5f;
    private const float DefaultRadius = 1.2f;
    private const float MaxRadius = 3f;
    private static ShipStatus _hinted;

    public static readonly Text Hint = new(
        "練習: チャット欄に /hammer で近くの壁を叩く (3 回で崩れる)・/blast で自分の所を爆破",
        "Practice: type /hammer to hit the nearest wall (breaks on the 3rd hit) or /blast to explode where you stand");

    // 試合ごとに 1 回だけ、チャット欄に使い方を出す
    public static void ShowHint(ChatController chat)
    {
        var ship = ShipStatus.Instance;
        if (!ship || _hinted == ship) return;
        _hinted = ship;
        chat.AddChatWarning(Hint);
    }

    // チャットの 1 行を受けたら true (結果は reply)。引数が 2 つ以上なら開発者向けの同名コマンドに任せる
    public static bool TryHandle(string name, string args, out string reply)
    {
        reply = null;
        if (name != "hammer" && name != "blast") return false;
        if (!Roles.PracticeMatch.Sandbox) return false;
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1) return false;
        float? num = parts.Length == 1 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : null;
        if (parts.Length == 1 && num == null) return false;

        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead) { reply = new Text("生きている時だけ使えます", "Only while alive"); return true; }
        Vector2 at = lp.GetTruePosition();
        ushort seed = (ushort)Environment.TickCount;

        if (name == "blast")
        {
            float r = Math.Clamp(num ?? DefaultRadius, 0.3f, MaxRadius);
            TerrainSync.Request(new DamageEvent(DamageKind.Explosion, at, Vector2.zero, r, 0f, seed));
            reply = new Text($"爆発 (半径 {r:0.0})", $"Blast (radius {r:0.0})");
            return true;
        }

        float reach = DamageProfile.Of(DamageKind.Blunt).Reach;
        if (!NearestWall(at, reach + 0.3f, out Vector2 dir)) { reply = new Text("近くに壊せる壁がありません", "No breakable wall nearby"); return true; }
        float force = Math.Clamp(num ?? DefaultForce, 0f, 1f);
        TerrainSync.Request(new DamageEvent(DamageKind.Blunt, at, dir, 0f, force, seed));
        reply = new Text("ハンマーで叩いた", "Hammer hit");
        return true;
    }

    // 8 方向でいちばん近い壊せる壁の向き
    private static bool NearestWall(Vector2 from, float range, out Vector2 bestDir)
    {
        float best = float.MaxValue;
        bestDir = default;
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.PI / 4f;
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            foreach (var h in Physics2D.CircleCastAll(from, 0.1f, dir, range, 1 << ShipLayer))
            {
                if (!h.collider || h.collider.isTrigger || h.distance >= best) continue;
                if (TerrainDamage.IsProtected(h.collider)) continue;
                best = h.distance;
                bestDir = dir;
            }
        }
        return best < float.MaxValue;
    }
}
