// Il2CppInterop.Runtime の barrier 無しの参照書き込み 3 か所を il2cpp_gc_wbarrier_set_field 経由に書き換える
// (LGPL-3.0 の Il2CppInterop の改変版を作る道具)。
// Ported from https://github.com/waffle-ful/Aeterna-End-K-not tools/InteropWbPatch/Program.cs (GPL-3.0)
//   1) IL2CPP.FieldWriteWbarrierStub(obj, target, value) の本体 = il2cpp_gc_wbarrier_set_field(obj, target, value)
//      (ScanMethodRefs=false で生成した interop の参照型フィールド setter はここを呼ぶ)
//   2) Il2CppReferenceArray<T>.StoreValue の参照型分岐の stind.i
//   3) Il2CppStringArray の indexer setter の stind.i
//   4) 古い版だけ: Il2CppReferenceField<T>.Set / Il2CppStringField.Set の stind.i (注入クラスのフィールド)
// 2) 3) 4) は (target, value) がスタックに積まれているので、新設の IL2CPP.WbStore(target, value) の呼び出しへ置換する。
// ゲーム本体の il2cpp_gc_wbarrier_set_field は第 1 引数を使わない (*target = value; GC_dirty(target)) ので obj = 0 でよい。
// incremental GC が ON の間、barrier の無い書き込みは書いた先の参照が回収されうる (生きているのに解放される)。
// 3 か所を直すと interop を作り直さずに incremental GC を ON にできる。mod 側は IL2CPP.WbStore の有無で
// 書き換え済みかどうかを判定する (src/Boot/IncrementalGcInvalidator.cs)。
//
// 使い方: dotnet run --project tools/InteropWbPatch -- <in> <out>
//   <in> が .dll ならそのまま書き換える (PC の BepInEx-MRP/core/Il2CppInterop.Runtime.dll)。
//   <in> が .zip なら中の core/Il2CppInterop.Runtime.dll を書き換えて zip ごと書き出す
//   (Android/Launcher/fusionApp/src/main/assets/BepInEx-arm64.zip・in と out は同じでよい)。
//   書き換え済みなら何もしない。
using System.IO.Compression;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2) { Console.Error.WriteLine("usage: InteropWbPatch <in .dll|.zip> <out>"); return 2; }
var inPath = Path.GetFullPath(args[0]);
var outPath = Path.GetFullPath(args[1]);

if (!inPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
{
    var r = Patch(File.ReadAllBytes(inPath), out var bytes);
    if (r == 0) File.WriteAllBytes(outPath, bytes);
    else if (r == 3 && !string.Equals(inPath, outPath, StringComparison.OrdinalIgnoreCase)) File.Copy(inPath, outPath, overwrite: true);
    return r == 3 ? 0 : r;
}

const string Entry = "core/Il2CppInterop.Runtime.dll";
var entries = new List<(string Name, DateTimeOffset Time, byte[] Data)>();
using (var zip = ZipFile.OpenRead(inPath))
{
    foreach (var e in zip.Entries)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        entries.Add((e.FullName, e.LastWriteTime, ms.ToArray()));
    }
}
var idx = entries.FindIndex(e => e.Name == Entry);
if (idx < 0) { Console.Error.WriteLine($"{Entry} not found in {inPath}"); return 1; }
var rc = Patch(entries[idx].Data, out var patched);
if (rc == 3)
{
    if (!string.Equals(inPath, outPath, StringComparison.OrdinalIgnoreCase)) File.Copy(inPath, outPath, overwrite: true);
    return 0;
}
if (rc != 0) return rc;
entries[idx] = (Entry, entries[idx].Time, patched);
var tmp = outPath + ".tmp";
using (var fs = File.Create(tmp))
using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
{
    foreach (var (name, time, data) in entries)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        e.LastWriteTime = time;
        using var s = e.Open();
        s.Write(data);
    }
}
File.Move(tmp, outPath, overwrite: true);
Console.WriteLine($"wrote {outPath}");
return 0;

// 0 = 書き換えた / 3 = 書き換え済み / 4 = 想定の数と合わない
static int Patch(byte[] input, out byte[] output)
{
    output = null;
    using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(input), new ReaderParameters { ReadWrite = false, InMemory = true });
    var mod = asm.MainModule;
    var il2cpp = mod.GetType("Il2CppInterop.Runtime.IL2CPP");
    if (il2cpp.Methods.Any(m => m.Name == "WbStore")) { Console.WriteLine("already patched"); return 3; }
    var setField = il2cpp.Methods.Single(m => m.Name == "il2cpp_gc_wbarrier_set_field");
    var stub = il2cpp.Methods.Single(m => m.Name == "FieldWriteWbarrierStub");

    // 1)
    stub.Body.Instructions.Clear();
    stub.Body.Variables.Clear();
    stub.Body.ExceptionHandlers.Clear();
    var p = stub.Body.GetILProcessor();
    p.Emit(OpCodes.Ldarg_0); p.Emit(OpCodes.Ldarg_1); p.Emit(OpCodes.Ldarg_2);
    p.Emit(OpCodes.Call, setField); p.Emit(OpCodes.Ret);

    // WbStore(target, value)
    var intPtr = mod.TypeSystem.IntPtr;
    var wb = new MethodDefinition("WbStore", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, mod.TypeSystem.Void);
    wb.Parameters.Add(new ParameterDefinition("targetAddress", ParameterAttributes.None, intPtr));
    wb.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, intPtr));
    var w = wb.Body.GetILProcessor();
    w.Emit(OpCodes.Ldc_I4_0); w.Emit(OpCodes.Conv_I); w.Emit(OpCodes.Ldarg_0); w.Emit(OpCodes.Ldarg_1);
    w.Emit(OpCodes.Call, setField); w.Emit(OpCodes.Ret);
    il2cpp.Methods.Add(wb);

    int Replace(MethodDefinition m)
    {
        var stores = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Stind_I).ToList();
        foreach (var s in stores) { s.OpCode = OpCodes.Call; s.Operand = wb; }
        return stores.Count;
    }
    // 2)
    var refArr = mod.GetType("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1");
    int n2 = Replace(refArr.Methods.Single(m => m.Name == "StoreValue"));
    // 3)
    var strArr = mod.GetType("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray");
    int n3 = Replace(strArr.Methods.Single(m => m.Name == "set_Item"));
    // 4) 古い版 (Android 同梱) だけ: 注入クラスの参照型フィールドの Set も生書き。新しい版は barrier 付きで 0 件
    int n4 = 0;
    foreach (var name in new[] { "Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField`1", "Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppStringField" })
        if (mod.GetType(name) is { } ft)
            foreach (var m in ft.Methods.Where(m => m.Name == "Set" && m.HasBody)) n4 += Replace(m);

    // 残りの stind.i / stind.ref を一覧に出す (型登録・メタデータ構造体への書き込みは GC 管理外なので対象外)
    foreach (var t in mod.GetTypes())
        foreach (var m in t.Methods.Where(m => m.HasBody))
            foreach (var i in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Stind_I || i.OpCode == OpCodes.Stind_Ref))
                Console.WriteLine($"remaining {i.OpCode} in {t.FullName}::{m.Name}");

    Console.WriteLine($"patched: stub=1 refArray={n2} stringArray={n3} injectedFields={n4}");
    if (n2 != 1 || n3 != 1) return 4;
    var ms = new MemoryStream();
    asm.Write(ms);
    output = ms.ToArray();
    return 0;
}
