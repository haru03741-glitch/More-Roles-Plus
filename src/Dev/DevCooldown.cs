using MoreRolesPlus.Bridge;

namespace MoreRolesPlus.Dev;

// 開発者の確かめ用: 自分のキルと MRP の能力の待ち時間を 0 にする (開発者コンソール・ブリッジの nocd)。
// ホストの確かめを飛ばすのは「依頼したのがホスト自身」の時だけ。客の依頼はいつもどおり確かめる
// (受け手の手元には開発者の名簿が無く、送り主が開発者かを確かめられないため)。
// なので効くのは開発者がホストの部屋とフリープレイ。本編の役職の能力 (シェイプシフトなど) の待ち時間は対象外
internal static class DevCooldown
{
    public static bool Zero { get; private set; }

    public static void Register()
    {
        TestBridge.Register("nocd", "[on|off] 自分のキルと MRP の能力の待ち時間を 0 にする (開発者だけ・ホストかフリープレイで効く)・省略で今の状態", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "on" || a == "off")
            {
                if (!DevUsers.AmDev) { reply("ERR nocd is for developers"); return; }
                bool was = Zero;
                Zero = a == "on";
                if (was && !Zero) Restart();
            }
            var client = AmongUsClient.Instance;
            bool host = client && client.AmHost;
            reply($"OK nocd {(Zero ? "on" : "off")}{(Zero && !host ? " (ホストでないので MRP の能力・キル役はホストに断られる)" : "")}");
        });
    }

    // 切った時は待ち時間を最初から数え直す (ホストは最後に使った時から数えているので、0 のままだと押しても断られる)
    private static void Restart()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !GameManager.Instance) return;
        var data = lp.Data;
        if (data != null && data.Role && data.Role.CanUseKillButton) lp.killTimer = GameManager.Instance.LogicOptions.GetKillCooldown();
        var role = Roles.RoleState.Of(lp.PlayerId);
        if (role == null) return;
        foreach (var ab in role.Abilities) ab.Button?.StartCooldown();
    }

    // ホストの確かめで待ち時間を見ないか
    public static bool Skips(PlayerControl user) => Zero && user && user.AmOwner;

    // 毎フレーム (旗が立っている時だけ先を見る)。本編のキルの待ち時間は本編が減らして表示するので、値を 0 にするだけでよい
    public static void Tick()
    {
        if (!Zero) return;
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.killTimer > 0f) lp.killTimer = 0f;
    }
}
