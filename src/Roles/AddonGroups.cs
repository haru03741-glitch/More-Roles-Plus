using System.Collections.Generic;
using MoreRolesPlus.Net;

namespace MoreRolesPlus.Roles;

// 何人 1 組で付くアドオン (AddonBase.GroupSize。ラバーズなど) の組。ホストが配った後に全員の端末で同じになる
public static class AddonGroups
{
    private static readonly List<byte[]> Groups = new();

    // その人と同じ組の人たち (本人を除く)。組に入っていなければ空
    public static IEnumerable<byte> MatesOf(byte playerId)
    {
        foreach (var g in Groups)
        {
            if (System.Array.IndexOf(g, playerId) < 0) continue;
            foreach (var id in g)
                if (id != playerId) yield return id;
        }
    }

    // 1 人の相方 (2 人組の時)。いなければ 255
    public static byte PartnerOf(byte playerId)
    {
        foreach (var id in MatesOf(playerId)) return id;
        return 255;
    }

    internal static void Clear() => Groups.Clear();

    // ホスト: 配布案の後に送る (役職の電文と同じ束に入れて、配られた後に届くようにする)
    internal static void HostSend(List<byte[]> groups)
    {
        var copy = new List<byte[]>(groups);
        Call.Send(copy);
        Set(copy);
    }

    // ホスト: 試合の途中で組を 1 つ足して全員に配る (アドオンを付けた後に呼ぶ)
    public static void HostAdd(params byte[] group)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        var all = new List<byte[]>(Groups) { group };
        Call.Send(all);
        Set(all);
    }

    private static void Set(List<byte[]> groups)
    {
        Groups.Clear();
        Groups.AddRange(groups);
    }

    private static readonly RemoteCall<List<byte[]>> Call = new("AddonGroups.Set", Route.HostToAll,
        (w, gs) =>
        {
            w.Write((byte)gs.Count);
            foreach (var g in gs)
            {
                w.Write((byte)g.Length);
                foreach (var id in g) w.Write(id);
            }
        },
        r =>
        {
            var gs = new List<byte[]>();
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                var g = new byte[r.ReadByte()];
                for (int k = 0; k < g.Length; k++) g[k] = r.ReadByte();
                gs.Add(g);
            }
            return gs;
        },
        (_, gs) => Set(gs));
}
