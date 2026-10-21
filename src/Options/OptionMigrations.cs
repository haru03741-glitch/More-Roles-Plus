// The versioned key-rename table follows https://github.com/yukieiji/ExtremeRoles (Module/CustomOption/Migrator, GPL-3.0)
using System;
using System.Collections.Generic;

namespace MoreRolesPlus.Options;

// 設定の鍵 (「クラス名.フィールド名」) の名前が変わっても、前の版で保存した値・プリセットを引き継ぐ。
// 鍵を変える時はここに 1 段足して Current を上げる。保存したファイルの先頭に版を書き、それより新しい段だけを順に当てる
// (版の無いファイル = 0)。同じ鍵を後の段で何度変えても、古いファイルは途中の名前を順にたどって今の鍵へ着く
internal static class OptionMigrations
{
    internal const string VersionKey = "#version";

    private readonly record struct Step(int To, (string From, string To)[] Renames);

    private static readonly Step[] Steps =
    [
        // 例: new(1, [("OldRole.Chance", "NewRole.Chance")]),
    ];

    public static int Current => Steps.Length == 0 ? 0 : Steps[^1].To;

    // 読んだ値 (鍵 → 文字) を from の版から今の版へ。今の鍵が既にある時は上書きしない。直した数を返す
    public static int Apply(Dictionary<string, string> values, int from)
    {
        int n = 0;
        foreach (var step in Steps)
        {
            if (step.To <= from) continue;
            foreach (var (oldKey, newKey) in step.Renames)
            {
                if (!values.Remove(oldKey, out var v)) continue;
                if (values.TryAdd(newKey, v)) n++;
            }
        }
        return n;
    }

    // 先頭の「#version=N」の行から版を読む (無い・読めない = 0)
    public static bool TryReadVersion(string line, out int version)
    {
        version = 0;
        if (!line.StartsWith(VersionKey, StringComparison.Ordinal)) return false;
        int eq = line.IndexOf('=');
        return eq > 0 && int.TryParse(line.AsSpan(eq + 1).Trim(), out version);
    }
}
