// Based on https://github.com/waffle-ful/Aeterna-End-K-not Modules/Il2Direct.cs (PinnedString / SetText, GPL-3.0)
using System;
using Il2CppInterop.Runtime;

namespace MoreRolesPlus;

// 毎フレーム書き直す字の欄へ、作り置きの文字列を渡す。tmp.text = s は 1 回ごとにゲーム側のヒープへ文字列を複製するので、
// 同じ文を何度も書く所はゲーム側の文字列を 1 回だけ作って握っておき、それを set_text へ直接渡す
internal static unsafe class PinnedText
{
    internal readonly struct Str
    {
        public readonly IntPtr Ptr;
        public readonly string Managed;

        public Str(string s)
        {
            Managed = s;
            Ptr = IL2CPP.ManagedStringToIl2Cpp(s);
            // ゲーム側の GC に回収させないため強参照のハンドルで握る (Boehm は動かさないのでポインタはそのまま使える)。放さない
            if (Ptr != IntPtr.Zero) IL2CPP.il2cpp_gchandle_new(Ptr, false);
        }
    }

    private static IntPtr _setText;
    private static int _state; // 0 = 未解決 / 1 = 使える / 2 = 使えない (tmp.text へ戻す)

    public static void Set(TMPro.TMP_Text tmp, in Str s)
    {
        if (s.Ptr == IntPtr.Zero || !Ready())
        {
            tmp.text = s.Managed;
            return;
        }
        IntPtr obj = tmp.Pointer;
        IntPtr method = IL2CPP.il2cpp_object_get_virtual_method(obj, _setText);
        void** args = stackalloc void*[1];
        args[0] = (void*)s.Ptr;
        IntPtr exc = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(method, obj, args, ref exc);
        Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(exc);
    }

    private static bool Ready()
    {
        if (_state != 0) return _state == 1;
        try
        {
            IntPtr klass = Il2CppClassPointerStore<TMPro.TMP_Text>.NativeClassPtr;
            _setText = klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_method_from_name(klass, "set_text", 1);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"PinnedText: set_text not found: {e.Message}"); }
        _state = _setText != IntPtr.Zero ? 1 : 2;
        return _state == 1;
    }
}
