using System;
using System.Collections.Generic;

namespace MoreRolesPlus.Dev;

// 開発者の名簿。中身は DevUsers.Local.cs (リポジトリに入れない) が PopulateCodes で詰める。
// 無いビルドでは名簿は空で、誰も開発者にならない。
// 開発者かどうかは必ず「自分のフレンドコードが名簿にあるか」で決める (名簿が空でないこと自体では開かない)。
internal static partial class DevUsers
{
    static partial void PopulateCodes(HashSet<string> codes);

    private static readonly HashSet<string> Codes;
    private static bool _resolved, _amDev;

    static DevUsers()
    {
        Codes = new(StringComparer.OrdinalIgnoreCase);
        PopulateCodes(Codes);
    }

    // 名簿が空のビルドで毎フレームの確認をすぐ終えるためだけに使う (開発者かどうかの判定には使わない)
    public static bool HasList => Codes.Count > 0;
    public static bool IsResolved => _resolved;

    public static bool IsDev(string friendCode)
        => !string.IsNullOrEmpty(friendCode) && Codes.Contains(friendCode.Replace(':', '#'));

    public static string LocalFriendCode
        => EOSManager.InstanceExists ? EOSManager.Instance.FriendCode : null;

    // 自分が開発者か。フレンドコードはログイン後に決まるので、空の間は毎回引き、決まったら覚える
    // (読むたびにゲーム側の文字列が複製されるので、決まった後は読まない)
    public static bool AmDev
    {
        get
        {
            if (_resolved || Codes.Count == 0) return _amDev;
            string fc = LocalFriendCode;
            if (string.IsNullOrEmpty(fc)) return false;
            _resolved = true;
            _amDev = IsDev(fc);
            Plugin.Logger.LogInfo($"dev: {(_amDev ? "on" : "off")}");
            return _amDev;
        }
    }
}
