namespace MoreRolesPlus;

// 本編の場面に付いた物 (HUD・ロビーの開始の画面・ロビーの情報の板・ストア) の読み方。
// 本編の X.Instance は、無い時 (場面が変わって消えた・まだ作られていない) に読むと中身の空な X を新しく作って返し、
// その更新が毎フレーム例外を出し続ける。`if (!X.Instance)` では防げないので、場面に付いた物は必ずここから読む (無ければ null)。
// 常にある物 (AmongUsClient・EOSManager・ModManager・RoleManager・ServerManager・TranslationController・AccountManager) は直接でよい。
internal static class Vanilla
{
    public static HudManager Hud => HudManager.InstanceExists ? HudManager.Instance : null;
    public static GameStartManager StartManager => GameStartManager.InstanceExists ? GameStartManager.Instance : null;
    public static LobbyInfoPane LobbyInfo => LobbyInfoPane.InstanceExists ? LobbyInfoPane.Instance : null;
    public static StoreMenu Store => StoreMenu.InstanceExists ? StoreMenu.Instance : null;

    // HUD のチャット欄 (HUD が無ければ null)
    public static ChatController Chat
    {
        get
        {
            var hud = Hud;
            return hud ? hud.Chat : null;
        }
    }
}
