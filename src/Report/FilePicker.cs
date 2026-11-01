using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MoreRolesPlus.Report;

// Windows の「開く」の窓で画像・動画を選ぶ。窓を開いている間もゲームが止まらないよう別のスレッドで開く
// (ゲームの窓を持ち主にするので、閉じるまでゲームの窓は押せない)
internal static class FilePicker
{
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".mp4", ".mov", ".webm" };

    public static bool IsVideo(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".webm";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    private const int OFN_ALLOWMULTISELECT = 0x200, OFN_EXPLORER = 0x80000, OFN_FILEMUSTEXIST = 0x1000, OFN_PATHMUSTEXIST = 0x800, OFN_NOCHANGEDIR = 0x8;
    private const int BufChars = 32 * 1024;

    private static string _lastDir;

    // メインスレッドから呼ぶ。選ばなければ空の配列
    public static Task<string[]> Pick(string title, string filterName, bool multi)
    {
        IntPtr owner = IntPtr.Zero;
        try { owner = GetActiveWindow(); } catch { }
        string initial = _lastDir ?? DefaultDir();
        var done = new TaskCompletionSource<string[]>();
        var t = new Thread(() =>
        {
            try { done.SetResult(Run(owner, title, filterName, multi, initial)); }
            catch (Exception e) { done.SetException(e); }
        }) { IsBackground = true, Name = "MrpFilePicker" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    // スクリーンショットのある所 (Windows の「スクリーンショット」) から始める
    private static string DefaultDir()
    {
        try
        {
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string shots = Path.Combine(pics, "Screenshots");
            return Directory.Exists(shots) ? shots : pics;
        }
        catch { return null; }
    }

    private static string[] Run(IntPtr owner, string title, string filterName, bool multi, string initial)
    {
        string pattern = string.Join(";", Array.ConvertAll(Extensions, e => "*" + e));
        IntPtr buf = Marshal.AllocHGlobal(BufChars * 2);
        try
        {
            Marshal.WriteInt16(buf, 0);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = $"{filterName}\0{pattern}\0\0",
                nFilterIndex = 1,
                lpstrFile = buf,
                nMaxFile = BufChars,
                lpstrInitialDir = initial,
                lpstrTitle = title,
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | (multi ? OFN_ALLOWMULTISELECT : 0),
            };
            if (!GetOpenFileNameW(ref ofn)) return Array.Empty<string>();
            // 1 つなら「全部の道筋\0\0」、いくつもなら「フォルダー\0名前\0名前\0\0」
            var parts = new List<string>();
            int offset = 0;
            while (true)
            {
                string s = Marshal.PtrToStringUni(IntPtr.Add(buf, offset * 2));
                if (string.IsNullOrEmpty(s)) break;
                parts.Add(s);
                offset += s.Length + 1;
                if (offset >= BufChars) break;
            }
            if (parts.Count == 0) return Array.Empty<string>();
            string[] result;
            if (parts.Count == 1) result = new[] { parts[0] };
            else
            {
                result = new string[parts.Count - 1];
                for (int i = 1; i < parts.Count; i++) result[i - 1] = Path.Combine(parts[0], parts[i]);
            }
            try { _lastDir = Path.GetDirectoryName(result[0]); } catch { }
            return result;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
