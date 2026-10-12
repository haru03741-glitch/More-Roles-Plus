namespace MoreRolesPlus;

// il2cpp の配列 (Il2CppStructArray など) の中身を直に読み書きする時の先頭。
// 添字で 1 要素ずつ読むと interop を通って遅いので、頂点などの大きな配列はここからポインタで読む
internal static class Il2Array
{
    // 頭 = 型・監視・範囲・長さの 4 語
    internal static System.IntPtr Data(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase a) => a.Pointer + 4 * System.IntPtr.Size;
}
