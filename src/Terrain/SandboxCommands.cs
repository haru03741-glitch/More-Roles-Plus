using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 練習の試合とフリープレイで、誰でもチャット欄から地形の破壊を試せるコマンド。
//   /hammer [強さ 0〜1]  向いている方向の壁をハンマーで叩く (3 回で崩れる)
//   /blast [半径]        足元に爆弾を置く (導火線の後に爆発)
//   /fire [半径]         足元の床に火を付ける (燃え広がるかは床の材質次第)
//   /water               足元から向いている方向へ水を噴き出す (しばらく出続ける)
//   /flood [秒]          足元へ水を入れ続ける (部屋が水で満ちていく)
//   /wall [角度]         向いている方向の先に仕掛け壁を立てる (省略 = 向きと直角・0 = 横・90 = 縦)
// 役職の能力と同じ TerrainApi を通るので、同期とホストの検査もそのまま確かめられる
internal static class SandboxCommands
{
    private const float DefaultForce = 0.5f;
    private const float DefaultRadius = 1.2f;
    private const float HammerCooldown = 0.5f;
    private const float BlastCooldown = 3f;
    private const float FireRadius = 0.8f;
    private const float FireCooldown = 1f;
    private const float WaterCooldown = 2f;
    private const float FloodSeconds = 30f;
    private const float FloodCooldown = 5f;
    private const float WallCooldown = 1f;
    private const float WallGap = 0.45f; // 自分の足元から壁の手前の面までの間
    private static ShipStatus _hinted;

    public static readonly Text Hint = new(
        "練習: 右下のボタンかチャット欄の /hammer で向いている方向の壁を叩く (3 回で崩れる)・爆破ボタンか /blast で足元に爆弾を置く・点火ボタンか /fire で足元に火を付ける・放水ボタンか /water で向いている方向へ水を噴き出す・浸水ボタンか /flood で足元へ水を入れ続ける・壁ボタンか /wall で向いている方向の先に壁を立てる (/wall 0 = 横・/wall 90 = 縦)",
        "Practice: use the buttons or type /hammer to hit the wall you are facing (breaks on the 3rd hit), /blast to drop a bomb at your feet, /fire to set the floor at your feet on fire, /water to spray water the way you are facing, /flood to keep pouring water at your feet, /wall to put up a wall in front of you (/wall 0 = horizontal, /wall 90 = vertical)");

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
        if (name != "hammer" && name != "blast" && name != "fire" && name != "water" && name != "flood" && name != "wall") return false;
        if (!Roles.PracticeMatch.Sandbox) return false;
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1) return false;
        float? num = parts.Length == 1 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : null;
        if (parts.Length == 1 && num == null) return false;

        reply = (name switch
        {
            "blast" => TerrainApi.Bomb(num ?? DefaultRadius),
            "fire" => IgniteAtFeet(num ?? FireRadius),
            "water" => SprayAtFeet(),
            "flood" => FloodAtFeet(num ?? FloodSeconds),
            "wall" => WallAhead(num),
            _ => TerrainApi.Hammer(num ?? DefaultForce),
        }).Why;
        return true;
    }

    private static TerrainResult IgniteAtFeet(float radius)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return TerrainResult.Fail(new Text("試合の中だけ使えます", "Only during a game"));
        return TerrainApi.Ignite(lp.GetTruePosition(), radius);
    }

    // 向いている左右 (歩いている時はその向き) へ足元から水を噴き出す
    private static TerrainResult SprayAtFeet()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return TerrainResult.Fail(new Text("試合の中だけ使えます", "Only during a game"));
        return TerrainApi.Water(lp.GetTruePosition(), HammerSwing.Aim(lp, out _));
    }

    private static TerrainResult FloodAtFeet(float seconds)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return TerrainResult.Fail(new Text("試合の中だけ使えます", "Only during a game"));
        return TerrainApi.Flood(lp.GetTruePosition(), seconds);
    }

    // 向いている方向の先に壁を立てる。deg = 壁の向き (null = 向いている方向と直角)。壁の手前の面が足元から WallGap 先に来る
    internal static TerrainResult WallAhead(float? deg)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return TerrainResult.Fail(new Text("試合の中だけ使えます", "Only during a game"));
        Vector2 aim = HammerSwing.Aim(lp, out _);
        Vector2 t = deg is float d ? new Vector2(MathF.Cos(d * MathF.PI / 180f), MathF.Sin(d * MathF.PI / 180f)) : new Vector2(-aim.y, aim.x);
        Vector2 n = new(-t.y, t.x);
        // 向いている方向に沿った壁の半分の広がり
        float extent = MathF.Abs(t.x * aim.x + t.y * aim.y) * TrickWall.DefaultLength * 0.5f + MathF.Abs(n.x * aim.x + n.y * aim.y) * TrickWall.Thickness * 0.5f;
        return TerrainApi.Wall(lp.GetTruePosition() + aim * (extent + WallGap), t);
    }

    // 練習とフリープレイの試合ごとに、ハンマー・爆破・点火・放水・浸水のボタンを出す (もう出ていれば何もしない)
    private static Lifespan _buttons;

    public static void EnsureButtons()
    {
        if (_buttons is { IsDead: false } || !Roles.PracticeMatch.Sandbox) return;
        _buttons = Roles.RoleState.Match.Child();
        var hammer = Roles.ModButton.Create(_buttons, Roles.ButtonIcons.Get("hammer"), new Text("ハンマー", "Hammer"), HammerCooldown,
            () => Report(TerrainApi.Hammer(DefaultForce)));
        var bomb = Roles.ModButton.Create(_buttons, Roles.ButtonIcons.Get("bomb"), new Text("爆破", "Blast"), BlastCooldown,
            () => Report(TerrainApi.Bomb(DefaultRadius)));
        var fire = Roles.ModButton.Create(_buttons, Roles.ButtonIcons.Get("fire"), new Text("点火", "Ignite"), FireCooldown,
            () => Report(IgniteAtFeet(FireRadius)));
        var water = Roles.ModButton.Create(_buttons, Roles.ButtonIcons.Get("water"), new Text("放水", "Water"), WaterCooldown,
            () => Report(SprayAtFeet()));
        var flood = Roles.ModButton.Create(_buttons, Roles.ButtonIcons.Get("water"), new Text("浸水", "Flood"), FloodCooldown,
            () => Report(FloodAtFeet(FloodSeconds)));
        var wall = Roles.ModButton.Create(_buttons, Roles.ButtonIcons.Get("hammer"), new Text("壁", "Wall"), WallCooldown,
            () => Report(WallAhead(null)));
        // HUD がまだ無くて作れなかった時は、次に HUD が出た時に作り直す
        if (hammer == null || bomb == null || fire == null || water == null || flood == null || wall == null) { _buttons.Release(); _buttons = null; }
    }

    // ボタンで使えなかった時だけ理由をチャット欄に出す (使えた時は画面の破壊で分かる)
    private static bool Report(TerrainResult result)
    {
        if (!result.Ok && Vanilla.Chat is { } chat) chat.AddChatWarning(result.Why);
        return result.Ok;
    }
}
