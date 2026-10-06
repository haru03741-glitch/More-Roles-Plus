using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 練習の試合とフリープレイで、誰でもチャット欄から地形の破壊を試せるコマンド。
//   /hammer [強さ 0〜1]  向いている方向の壁をハンマーで叩く (3 回で崩れる)
//   /blast [半径]        足元に爆弾を置く (導火線の後に爆発)
// 依頼は武器と同じ TerrainSync.Request を通るので、同期とホストの検査もそのまま確かめられる
internal static class SandboxCommands
{
    private const float DefaultForce = 0.5f;
    private const float DefaultRadius = 1.2f;
    private const float HammerCooldown = 0.5f;
    private const float BlastCooldown = 3f;
    private static ShipStatus _hinted;

    public static readonly Text Hint = new(
        "練習: 右下のボタンかチャット欄の /hammer で向いている方向の壁を叩く (3 回で崩れる)・爆破ボタンか /blast で足元に爆弾を置く",
        "Practice: use the buttons or type /hammer to hit the wall you are facing (breaks on the 3rd hit), /blast to drop a bomb at your feet");

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

        reply = name == "blast" ? Blast(num ?? DefaultRadius, out _) : Hammer(num ?? DefaultForce, out _);
        return true;
    }

    // 足元に爆弾を置く (導火線の後に爆発)。返り値は結果の説明
    public static string Blast(float radius, out bool ok) => BombFuse.Place(radius, out ok);

    // 向いている方向の壁をハンマーで叩く (振る動きの後に当たる)
    public static string Hammer(float force, out bool ok) => HammerSwing.Swing(force, out ok);

    // 練習とフリープレイの試合ごとに、ハンマーと爆破のボタンを出す (もう出ていれば何もしない)
    private static Lifespan _buttons;

    public static void EnsureButtons()
    {
        if (_buttons is { IsDead: false } || !Roles.PracticeMatch.Sandbox) return;
        _buttons = Roles.RoleState.Match.Child();
        var hammer = Roles.ModButton.Create(_buttons, Roles.ButtonIcons.Get("hammer"), new Text("ハンマー", "Hammer"), HammerCooldown,
            () => Report(Hammer(DefaultForce, out bool ok), ok));
        var bomb = Roles.ModButton.Create(_buttons, Roles.ButtonIcons.Get("bomb"), new Text("爆破", "Blast"), BlastCooldown,
            () => Report(Blast(DefaultRadius, out bool ok), ok));
        // HUD がまだ無くて作れなかった時は、次に HUD が出た時に作り直す
        if (hammer == null || bomb == null) { _buttons.Release(); _buttons = null; }
    }

    // ボタンで使えなかった時だけ理由をチャット欄に出す (使えた時は画面の破壊で分かる)
    private static bool Report(string result, bool ok)
    {
        if (!ok && HudManager.InstanceExists && HudManager.Instance.Chat) HudManager.Instance.Chat.AddChatWarning(result);
        return ok;
    }
}
