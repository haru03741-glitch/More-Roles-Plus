using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;

namespace MoreRolesPlus.Boot;

// Based on https://github.com/waffle-ful/Aeterna-End-K-not (Modules/PatchPhases.cs DelegateTypeCache, GPL-3.0)
// 本編の関数にパッチを当てるたびに、HarmonyX はデリゲート型 1 個のためにアセンブリを丸ごと作って読み込む
// (1 本あたり十数 ms・起動時のパッチ適用の大半)。デリゲート型は引数と戻り値の形だけで決まるので、
// 同じ形なら 1 つの動的モジュールに作った型を使い回す。失敗したらその呼び出しだけ本来の工場に任せる。
internal static class DelegateTypeCache
{
    private static readonly Dictionary<string, Type> _types = new();
    private static ModuleBuilder _module;
    private static int _counter;

    internal static bool Installed { get; private set; }
    internal static int Hits { get; private set; }
    internal static int Misses { get; private set; }
    internal static int Fallbacks { get; private set; }

    internal static void Install(Harmony harmony)
    {
        if (Installed) return;

        try
        {
            MethodInfo target = AccessTools.Method(typeof(DelegateTypeFactory), nameof(DelegateTypeFactory.CreateDelegateType), new[] { typeof(Type), typeof(Type[]), typeof(CallingConvention?) });
            if (target == null) throw new MissingMethodException("DelegateTypeFactory.CreateDelegateType(Type, Type[], CallingConvention?)");

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(DelegateTypeCache), nameof(Prefix)) { priority = Priority.First });
            Installed = true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"delegate type cache: install failed, using HarmonyX factory: {e}");
        }
    }

    internal static string Summary() => $"dtc={(Installed ? 1 : 0)}/{Hits}h/{Misses}m/{Fallbacks}f";

    private static bool Prefix(Type returnType, Type[] argTypes, CallingConvention? convention, ref Type __result)
    {
        try
        {
            var key = new StringBuilder(returnType.AssemblyQualifiedName).Append('|');
            foreach (Type t in argTypes) key.Append(t.AssemblyQualifiedName).Append(',');
            key.Append('|').Append(convention.HasValue ? ((int)convention.Value).ToString() : "-");
            string k = key.ToString();

            lock (_types)
            {
                if (_types.TryGetValue(k, out Type cached))
                {
                    Hits++;
                    __result = cached;
                    return false;
                }

                _module ??= AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MoreRolesPlusDelegateTypes"), AssemblyBuilderAccess.Run).DefineDynamicModule("MoreRolesPlusDelegateTypes");

                TypeBuilder tb = _module.DefineType($"MrpDelegate{++_counter}", TypeAttributes.Sealed | TypeAttributes.Public, typeof(MulticastDelegate));

                if (convention.HasValue)
                {
                    ConstructorInfo attrCtor = typeof(UnmanagedFunctionPointerAttribute).GetConstructor(new[] { typeof(CallingConvention) });
                    tb.SetCustomAttribute(new CustomAttributeBuilder(attrCtor, new object[] { convention.Value }));
                }

                ConstructorBuilder ctor = tb.DefineConstructor(MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(object), typeof(IntPtr) });
                ctor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

                MethodBuilder invoke = tb.DefineMethod("Invoke", MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Public, returnType, argTypes);
                invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

                Type created = tb.CreateType();
                _types[k] = created;
                Misses++;
                __result = created;
                return false;
            }
        }
        catch (Exception e)
        {
            Fallbacks++;
            if (Fallbacks <= 3) Plugin.Logger.LogError($"delegate type cache: miss path failed, falling back: {e}");
            return true;
        }
    }
}
