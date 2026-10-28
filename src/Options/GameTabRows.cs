using System.Collections.Generic;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Rules;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 設定画面のゲームの頁に、本編の設定の節と一緒に並べる MRP の項目の組。
// - マップ選びの下 (インポスターの節の前): マップのルール
// - インポスター: 本編のインポスター数の行を「最多人数」と呼び替え、その上に最少人数の行
// - インポスターの節の後: 第三陣営 (数とキル役の設定)
// - クルー・会議・タスクの節の終わり: 全員ベント・会議を無くす・タスクで勝てなくする の行
// - 頁の終わり: タスクの無効化・機器の無効化・サボタージュ・配り方・人数の帯 (帯ごとに見出し 1 つ)
internal static class GameTabRows
{
    internal readonly struct Group
    {
        public readonly Text Title;
        public readonly Opt[] Opts;

        public Group(Text title, params Opt[] opts)
        {
            Title = title;
            Opts = opts;
        }
    }

    public static readonly Group Neutral = new(new Text("NeutralKillSettings.Title"),
        NeutralKillSettings.MinNeutrals, NeutralKillSettings.MaxNeutrals, NeutralKillSettings.MaxKillers,
        NeutralKillSettings.KillCooldown, NeutralKillSettings.Vision, NeutralKillSettings.KillDistance);

    // マップ選びの下に並べる組
    public static IEnumerable<Group> Top()
    {
        yield return new Group(new Text("MapRules.Title"), MapRules.Rows);
    }

    // タブの終わりに並べる組
    public static IEnumerable<Group> Tail()
    {
        yield return new Group(new Text("TaskFilter.Title"), TaskFilter.Rows);
        yield return new Group(new Text("Devices.Title"), Devices.Rows);
        yield return new Group(new Text("SabotageRules.Title"), SabotageRules.Rows);
        yield return new Group(new Text("VanillaTweaks.Title"), VanillaTweaks.Rows);
        yield return new Group(new Text("AssignSettings.Title"), AssignSettings.MaxAddonsPerPlayer, AssignSettings.Fill, AssignSettings.Fairness);
        yield return new Group(new Text("TeamSizes.Title"), TeamSizes.Enabled);
        var bands = TeamSizes.Bands;
        for (int i = 0; i < bands.Length; i++)
        {
            var b = bands[i];
            yield return new Group(new Text("TeamSizes.Band" + (i + 1)), b.Players, b.MinImpostors, b.MaxImpostors, b.MinNeutrals, b.MaxNeutrals, b.MaxNeutralKillers);
        }
    }

    // 本編の項目の終わりに足す行 (その項目の目印の行・足す行)
    internal static readonly (StringNames Marker, Opt[] Opts)[] SectionRows =
    {
        (StringNames.GameCrewLight, new Opt[] { GameRules.EveryoneVents }),
        (StringNames.GameNumMeetings, new Opt[] { GameRules.NoMeetings, GameRules.NoSkip, GameRules.SelfVoteIfNone }),
        (StringNames.GameCommonTasks, new Opt[] { GameRules.TaskWin }),
    };

    // 検索の組 (タブに並べる物全部)
    public static IEnumerable<Group> All()
    {
        foreach (var g in Top()) yield return g;
        yield return Neutral;
        yield return new Group(new Text("GameRules.Title"), GameRules.EveryoneVents, GameRules.NoMeetings, GameRules.NoSkip, GameRules.SelfVoteIfNone, GameRules.TaskWin);
        foreach (var g in Tail()) yield return g;
    }
}
