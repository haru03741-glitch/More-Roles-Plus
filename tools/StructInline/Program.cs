// ビルドの後に MoreRolesPlus.dll の中の Unity の値型の小さなメソッド (Vector2 の足し算・Rect.xMin など) の呼び出しを、
// 同じ計算を mod の中でする MoreRolesPlus.Fx.UnityStructs へ差し替える。interop 経由だと 1 回ごとに
// il2cpp_runtime_invoke と戻り値の箱 (ゲーム側のごみ) になる。
//   dotnet StructInline.dll <MoreRolesPlus.dll> [参照の一覧のファイル]   (同じ場所に .pdb があれば行番号も保つ)
// 参照の一覧 = 1 行 1 ファイルの参照アセンブリ (書き出しで定数の enum の型を引くのに要る)
// 置き換えは下の表の物だけ (式が分かっている物)。表の名前は型・メソッド名・引数の型で文字列一致させる (型の解決はしない)。
// 差し替え先は 3 つの形: newobj → 値を返す New*、call .ctor (アドレスへの初期化) → Init*(ref T, ...)、
// インスタンスのメソッド → 第 1 引数が ref T の静的メソッド。callvirt / constrained. は触らない。
// [NoStructInline] の付いた型 (と、その中の入れ子の型) は飛ばす。2 回掛けても何も起きない。
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 1) { Console.Error.WriteLine("usage: StructInline <assembly> [references.txt]"); return 2; }
string path = args[0];
bool symbols = File.Exists(Path.ChangeExtension(path, ".pdb"));

const string V2 = "UnityEngine.Vector2", V3 = "UnityEngine.Vector3", V4 = "UnityEngine.Vector4", R = "UnityEngine.Rect", RI = "UnityEngine.RectInt",
    B = "UnityEngine.Bounds", C = "UnityEngine.Color", C32 = "UnityEngine.Color32", H = "UnityEngine.RaycastHit2D", M = "UnityEngine.Mathf",
    F = "System.Single", I = "System.Int32", U8 = "System.Byte";
static string K(string type, string name, params string[] ps) => $"{type}::{name}({string.Join(",", ps)})";

// (呼び出しの名前) → (差し替え先, 形)。形: N = newobj の ctor・I = call の ctor・S = 静的・T = インスタンス (ref this)
// ctor の名前の末尾は N/I、op_Implicit は戻り値の型を ">" で付けて区別する
var map = new Dictionary<string, (string Helper, char Form)>
{
    [K(V2, ".ctor", F, F) + "N"] = ("NewVector2", 'N'), [K(V2, ".ctor", F, F) + "I"] = ("InitVector2", 'I'),
    [K(V2, "op_Addition", V2, V2)] = ("V2Add", 'S'), [K(V2, "op_Subtraction", V2, V2)] = ("V2Sub", 'S'),
    [K(V2, "op_Multiply", V2, F)] = ("V2MulVS", 'S'), [K(V2, "op_Multiply", F, V2)] = ("V2MulSV", 'S'),
    [K(V2, "op_Division", V2, F)] = ("V2Div", 'S'), [K(V2, "op_UnaryNegation", V2)] = ("V2Neg", 'S'),
    [K(V2, "op_Implicit", V3) + ">" + V2] = ("V2FromV3", 'S'), [K(V2, "op_Implicit", V2) + ">" + V3] = ("V3FromV2", 'S'),
    [K(V2, "get_sqrMagnitude")] = ("V2SqrMagnitude", 'T'), [K(V2, "get_magnitude")] = ("V2Magnitude", 'T'),
    [K(V2, "get_normalized")] = ("V2Normalized", 'T'),
    [K(V2, "op_Equality", V2, V2)] = ("V2Equal", 'S'), [K(V2, "op_Inequality", V2, V2)] = ("V2NotEqual", 'S'),
    [K(V2, "Dot", V2, V2)] = ("V2Dot", 'S'), [K(V2, "Lerp", V2, V2, F)] = ("V2Lerp", 'S'),
    [K(V2, "get_zero")] = ("V2Zero", 'S'), [K(V2, "get_one")] = ("V2One", 'S'), [K(V2, "get_right")] = ("V2Right", 'S'),
    [K(V2, "get_left")] = ("V2Left", 'S'), [K(V2, "get_up")] = ("V2Up", 'S'), [K(V2, "get_down")] = ("V2Down", 'S'),

    [K(V3, ".ctor", F, F, F) + "N"] = ("NewVector3", 'N'), [K(V3, ".ctor", F, F, F) + "I"] = ("InitVector3", 'I'),
    [K(V3, ".ctor", F, F) + "N"] = ("NewVector3XY", 'N'), [K(V3, ".ctor", F, F) + "I"] = ("InitVector3XY", 'I'),
    [K(V3, "op_Addition", V3, V3)] = ("V3Add", 'S'), [K(V3, "op_Subtraction", V3, V3)] = ("V3Sub", 'S'),
    [K(V3, "op_Multiply", V3, F)] = ("V3MulVS", 'S'), [K(V3, "op_Multiply", F, V3)] = ("V3MulSV", 'S'),
    [K(V3, "op_Division", V3, F)] = ("V3Div", 'S'), [K(V3, "op_UnaryNegation", V3)] = ("V3Neg", 'S'),
    [K(V3, "get_magnitude")] = ("V3Magnitude", 'T'), [K(V3, "get_sqrMagnitude")] = ("V3SqrMagnitude", 'T'),
    [K(V3, "get_zero")] = ("V3Zero", 'S'), [K(V3, "get_one")] = ("V3One", 'S'),

    [K(V4, ".ctor", F, F, F, F) + "N"] = ("NewVector4", 'N'), [K(V4, ".ctor", F, F, F, F) + "I"] = ("InitVector4", 'I'),

    [K(R, ".ctor", F, F, F, F) + "N"] = ("NewRect", 'N'), [K(R, ".ctor", F, F, F, F) + "I"] = ("InitRect", 'I'),
    [K(R, "MinMaxRect", F, F, F, F)] = ("RectMinMax", 'S'),
    [K(R, "get_xMin")] = ("RectXMin", 'T'), [K(R, "get_yMin")] = ("RectYMin", 'T'), [K(R, "get_x")] = ("RectXMin", 'T'), [K(R, "get_y")] = ("RectYMin", 'T'),
    [K(R, "get_xMax")] = ("RectXMax", 'T'), [K(R, "get_yMax")] = ("RectYMax", 'T'),
    [K(R, "get_width")] = ("RectWidth", 'T'), [K(R, "get_height")] = ("RectHeight", 'T'),
    [K(R, "get_center")] = ("RectCenter", 'T'), [K(R, "get_position")] = ("RectPosition", 'T'), [K(R, "get_size")] = ("RectSize", 'T'),
    [K(R, "Contains", V2)] = ("RectContains2", 'T'), [K(R, "Contains", V3)] = ("RectContains3", 'T'), [K(R, "Overlaps", R)] = ("RectOverlaps", 'T'),

    [K(RI, ".ctor", I, I, I, I) + "N"] = ("NewRectInt", 'N'), [K(RI, ".ctor", I, I, I, I) + "I"] = ("InitRectInt", 'I'),
    [K(RI, "get_xMin")] = ("RectIntXMin", 'T'), [K(RI, "get_yMin")] = ("RectIntYMin", 'T'),
    [K(RI, "get_xMax")] = ("RectIntXMax", 'T'), [K(RI, "get_yMax")] = ("RectIntYMax", 'T'),
    [K(RI, "get_width")] = ("RectIntWidth", 'T'), [K(RI, "get_height")] = ("RectIntHeight", 'T'),

    [K(B, "get_center")] = ("BoundsCenter", 'T'), [K(B, "get_extents")] = ("BoundsExtents", 'T'), [K(B, "get_size")] = ("BoundsSize", 'T'),
    [K(B, "get_min")] = ("BoundsMin", 'T'), [K(B, "get_max")] = ("BoundsMax", 'T'),

    [K(C, ".ctor", F, F, F, F) + "N"] = ("NewColor", 'N'), [K(C, ".ctor", F, F, F, F) + "I"] = ("InitColor", 'I'),
    [K(C, ".ctor", F, F, F) + "N"] = ("NewColorRgb", 'N'), [K(C, ".ctor", F, F, F) + "I"] = ("InitColorRgb", 'I'),
    [K(C, "get_white")] = ("ColorWhite", 'S'), [K(C, "get_black")] = ("ColorBlack", 'S'), [K(C, "get_gray")] = ("ColorGray", 'S'), [K(C, "get_clear")] = ("ColorClear", 'S'),
    [K(C32, ".ctor", U8, U8, U8, U8) + "N"] = ("NewColor32", 'N'), [K(C32, ".ctor", U8, U8, U8, U8) + "I"] = ("InitColor32", 'I'),

    [K(H, "get_distance")] = ("HitDistance", 'T'), [K(H, "get_point")] = ("HitPoint", 'T'), [K(H, "get_normal")] = ("HitNormal", 'T'),

    [K(M, "CeilToInt", F)] = ("CeilToInt", 'S'), [K(M, "FloorToInt", F)] = ("FloorToInt", 'S'), [K(M, "RoundToInt", F)] = ("RoundToInt", 'S'),
    [K(M, "Max", F, F)] = ("MaxF", 'S'), [K(M, "Min", F, F)] = ("MinF", 'S'), [K(M, "Max", I, I)] = ("MaxI", 'S'), [K(M, "Min", I, I)] = ("MinI", 'S'),
    [K(M, "Abs", F)] = ("AbsF", 'S'), [K(M, "Clamp01", F)] = ("Clamp01", 'S'),
};

var resolver = new DefaultAssemblyResolver();
if (args.Length > 1)
    foreach (var dir in File.ReadAllLines(args[1]).Where(l => l.Length > 0).Select(Path.GetDirectoryName).Distinct())
        resolver.AddSearchDirectory(dir);
var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = symbols, InMemory = true, AssemblyResolver = resolver });
var module = asm.MainModule;
var helperType = module.GetType("MoreRolesPlus.Fx.UnityStructs");
if (helperType == null) { Console.Error.WriteLine("StructInline: MoreRolesPlus.Fx.UnityStructs not found"); return 1; }
var helpers = new Dictionary<string, MethodDefinition>();
foreach (var (helper, _) in map.Values)
{
    if (helpers.ContainsKey(helper)) continue;
    var md = helperType.Methods.SingleOrDefault(m => m.Name == helper && m.IsStatic);
    if (md == null) { Console.Error.WriteLine($"StructInline: helper {helper} missing"); return 1; }
    helpers[helper] = md;
}

bool Skipped(TypeDefinition t)
{
    for (; t != null; t = t.DeclaringType)
        if (t == helperType || t.CustomAttributes.Any(a => a.AttributeType.Name == "NoStructInlineAttribute")) return true;
    return false;
}

var counts = new SortedDictionary<string, int>();
int total = 0;
foreach (var t in module.GetTypes())
{
    if (Skipped(t)) continue;
    foreach (var m in t.Methods)
    {
        if (!m.HasBody) continue;
        foreach (var ins in m.Body.Instructions)
        {
            if (ins.Operand is not MethodReference mr || mr.DeclaringType.Namespace != "UnityEngine") continue;
            if (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Newobj) continue;
            if (ins.Previous?.OpCode == OpCodes.Constrained) continue;
            string key = K(mr.DeclaringType.FullName, mr.Name, mr.Parameters.Select(p => p.ParameterType.FullName).ToArray());
            if (mr.Name == ".ctor") key += ins.OpCode == OpCodes.Newobj ? "N" : "I";
            else if (mr.Name == "op_Implicit") key += ">" + mr.ReturnType.FullName;
            if (!map.TryGetValue(key, out var hit)) continue;
            var h = helpers[hit.Helper];
            // 形の確かめ: インスタンスと call の ctor は第 1 引数が ref の this、ほかは引数の数が同じ
            bool withThis = hit.Form is 'T' or 'I';
            int want = mr.Parameters.Count + (withThis ? 1 : 0);
            if (h.Parameters.Count != want || (withThis && !h.Parameters[0].ParameterType.IsByReference))
            {
                Console.Error.WriteLine($"StructInline: {hit.Helper} does not match {key}");
                return 1;
            }
            ins.OpCode = OpCodes.Call;
            ins.Operand = h;
            counts[hit.Helper] = counts.GetValueOrDefault(hit.Helper) + 1;
            total++;
        }
    }
}

// 一時ファイルへ書いてから置き換える (途中で落ちても元の出力を壊さない)
string tmp = path + ".structinline.tmp";
asm.Write(tmp, new WriterParameters { WriteSymbols = symbols });
asm.Dispose();
File.Move(tmp, path, true);
if (symbols) File.Move(Path.ChangeExtension(tmp, ".pdb"), Path.ChangeExtension(path, ".pdb"), true);
Console.WriteLine($"StructInline: {total} calls -> UnityStructs ({counts.Count} kinds){(symbols ? " +pdb" : "")}");
return 0;
