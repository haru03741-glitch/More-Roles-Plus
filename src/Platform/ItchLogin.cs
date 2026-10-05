// Ported from https://github.com/waffle-ful/Aeterna-End-K-not Modules/Android/ItchLogin.cs (GPL-3.0)
#if ANDROID
using System;
using System.IO;
using BepInEx;
using Epic.OnlineServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Connect = Epic.OnlineServices.Connect;

namespace MoreRolesPlus.Platform;

// Android 版はランチャーの中で動くので、本編の Google ログインが使えずゲスト (フレンドコード無し) になる。
// ランチャーが受け取った itch.io のトークンで Epic にログインし、PC と同じ正式なアカウントにする。
internal static class ItchLogin
{
    // ランチャーとの約束の名前 (変えるとランチャー側と食い違う)
    private const string TokenEnv = "ENDKNOT_ITCH_TOKEN";
    private const string KeyFileName = "EndKnot.ItchApiKey.txt";
    private const string FailedMarkerName = "EndKnot.ItchApiKey.failed";

    // 本編はログインに失敗するたびに LoginWithCorrectPlatformImpl を呼び直す → 3 回で本編の流れ (Google → ゲスト) に譲る
    private const int MaxAttempts = 3;
    private static int attempts;
    private static bool keyFromEnvironment;

    // ランチャーはトークンを暗号化して持ち、起動の直前に環境変数で渡す。平文のファイルは古いランチャーか
    // 開発で手で置いた時だけ読む (ここからは書かない・複製しない)。
    private static string PrivateDir => Environment.GetEnvironmentVariable("FUSION_APP_DATA_DIR");

    private static string ResolvePlaintextPath()
    {
        var privateDir = PrivateDir;
        var privatePath = string.IsNullOrEmpty(privateDir) ? null : Path.Combine(privateDir, KeyFileName);
        if (privatePath != null && File.Exists(privatePath)) return privatePath;
        var legacyPath = Path.Combine(Paths.ConfigPath, KeyFileName);
        return File.Exists(legacyPath) ? legacyPath : privatePath ?? legacyPath;
    }

    // 保存されたトークンが弾かれ続けたことをランチャーに知らせる (ランチャーはサインアウトして、ゲームへ直行しなくなる)
    private static void MarkKeyFailed()
    {
        try
        {
            if (keyFromEnvironment)
            {
                var privateDir = PrivateDir;
                if (string.IsNullOrEmpty(privateDir)) { Plugin.Logger.LogWarning("itch: token rejected but FUSION_APP_DATA_DIR is unset"); return; }
                Directory.CreateDirectory(privateDir);
                File.WriteAllText(Path.Combine(privateDir, FailedMarkerName), "rejected" + Environment.NewLine);
                Environment.SetEnvironmentVariable(TokenEnv, null);
                Plugin.Logger.LogWarning("itch: token rejected; failure marker written for the launcher");
                return;
            }
            var path = ResolvePlaintextPath();
            if (path == null || !File.Exists(path)) return;
            var failed = path + ".failed";
            if (File.Exists(failed)) File.Delete(failed);
            File.Move(path, failed);
            Plugin.Logger.LogWarning("itch: key file set aside as " + Path.GetFileName(failed));
        }
        catch (Exception e) { Plugin.Logger.LogWarning("itch: could not report the rejected key: " + e.Message); }
    }

    private static string ReadKey()
    {
        try
        {
            var fromEnv = Environment.GetEnvironmentVariable(TokenEnv);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                keyFromEnvironment = true;
                return fromEnv.Trim();
            }
            keyFromEnvironment = false;
            var path = ResolvePlaintextPath();
            if (path == null || !File.Exists(path)) return null;
            var key = File.ReadAllText(path).Trim().Trim((char)0xFEFF);
            return key.Length == 0 ? null : key;
        }
        catch (Exception e) { Plugin.Logger.LogError("itch: key read failed: " + e.Message); return null; }
    }

    // interop の ConnectInterface.Login(ref LoginOptions) は箱詰めの構造体を開かずにヘッダのポインタを渡し、
    // 実機で落ちる。開いた値のポインタで直接呼ぶ。
    private static unsafe void InvokeConnectLogin(Connect.ConnectInterface connect, Connect.LoginOptions opts, Connect.OnLoginCallback cb)
    {
        var fi = typeof(Connect.ConnectInterface).GetField("NativeMethodInfoPtr_Login_Public_Void_byref_LoginOptions_Object_OnLoginCallback_0",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (fi == null) throw new InvalidOperationException("Connect.Login native method pointer not found in interop");
        var method = (IntPtr)fi.GetValue(null);
        IntPtr* args = stackalloc IntPtr[3];
        args[0] = IL2CPP.il2cpp_object_unbox(IL2CPP.Il2CppObjectBaseToPtrNotNull(opts));
        args[1] = IntPtr.Zero;
        args[2] = IL2CPP.Il2CppObjectBaseToPtr(cb);
        IntPtr exc = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(method, IL2CPP.Il2CppObjectBaseToPtrNotNull(connect), (void**)args, ref exc);
        Il2CppException.RaiseExceptionIfNecessary(exc);
    }

    [HarmonyPatch(typeof(EOSManager), nameof(EOSManager.LoginWithCorrectPlatformImpl))]
    private static class LoginPatch
    {
        public static bool Prefix(EOSManager __instance, [HarmonyArgument(0)] Connect.OnLoginCallback successCallbackIn)
        {
            var key = ReadKey();
            if (key == null) { Plugin.Logger.LogWarning("itch: no token → vanilla login"); return true; }
            if (++attempts > MaxAttempts) { Plugin.Logger.LogWarning($"itch: login failed {MaxAttempts} times → vanilla login (guest)"); MarkKeyFailed(); return true; }
            try
            {
                // Il2CppSystem.Nullable<T>(T) のコンストラクタは箱詰めの値型を参照として扱って中身を壊すので、
                // value / hasValue を直接書く。value の getter も壊れた物を返すので読み戻さない。
                var cred = new Connect.Credentials();
                cred._Token_k__BackingField = new Utf8String(key);
                cred._Type_k__BackingField = ExternalCredentialType.ItchioKey;
                var nullable = new Il2CppSystem.Nullable<Connect.Credentials>();
                nullable.value = cred;
                nullable.hasValue = true;
                var opts = new Connect.LoginOptions();
                opts._Credentials_k__BackingField = nullable;
                InvokeConnectLogin(__instance.PlatformInterface.GetConnectInterface(), opts, successCallbackIn);
                Plugin.Logger.LogInfo("itch: Connect.Login issued");
                return false;
            }
            catch (Exception e) { Plugin.Logger.LogError("itch: Connect.Login threw, falling back: " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(EOSManager), nameof(EOSManager.EndFinalPartsOfLoginFlowFullAccount))]
    private static class FullAccountPatch
    {
        public static void Postfix() { attempts = 0; Plugin.Logger.LogInfo("itch: full account login"); }
    }

    [HarmonyPatch(typeof(EOSManager), nameof(EOSManager.EndFinalPartsOfLoginFlowTempAccount))]
    private static class TempAccountPatch
    {
        public static void Postfix() => Plugin.Logger.LogWarning("itch: guest account login");
    }
}
#endif
