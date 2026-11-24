// ShimGen: generates the headless "fake Unity" layer the game scripts compile against.
//
//   ShimGen analyze   -> compile the decompiled game against the real game DLLs and report which
//                        external APIs it uses.
//   ShimGen generate  -> write src/Lonestar.UnityShim/Generated/Shim.g.cs for exactly that API,
//                        skipping whatever src/Lonestar.UnityShim/Manual already implements.
//   ShimGen rewrite   -> write src/Lonestar.Game/Rewritten (Mono-vs-.NET behaviour fixes, see Rewriter).
//   ShimGen members T -> list the members of external type T that the game uses.

using Microsoft.CodeAnalysis;
using ShimGen;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string decompiled = Path.Combine(root, "decompiled");
string managed = Environment.GetEnvironmentVariable("LONESTAR_MANAGED")
    ?? @"F:\SteamLibrary\steamapps\common\LONESTAR\LONESTAR_Data\Managed";
string mode = args.Length > 0 ? args[0] : "analyze";

var sw = System.Diagnostics.Stopwatch.StartNew();
var trees = GameSource.Parse(decompiled);
var compilation = GameSource.CompileAgainstGame(trees, managed);
var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
Console.WriteLine($"Parsed {trees.Count} files in {sw.Elapsed.TotalSeconds:F1}s; {errors.Count} compile errors against the real game DLLs");
foreach (var g in errors.GroupBy(e => e.Id).OrderByDescending(g => g.Count()).Take(10))
{
    Console.WriteLine($"  {g.Key} x{g.Count()}: {g.First().GetMessage()}  @ {g.First().Location.GetLineSpan()}");
}

var usage = new UsageCollector(compilation);
usage.CollectAll();
Console.WriteLine($"Collected in {sw.Elapsed.TotalSeconds:F1}s: {usage.Types.Count} external types, {usage.Members.Count} external members");
Console.WriteLine();
Console.WriteLine($"{"assembly",-45} {"types",6} {"members",8}");
var byAsm = usage.Types.GroupBy(t => t.ContainingAssembly.Name)
    .Select(g => (asm: g.Key, types: g.Count(), members: usage.Members.Count(m => m.ContainingAssembly.Name == g.Key)))
    .OrderByDescending(x => x.members);
foreach (var (asm, types, members) in byAsm)
{
    Console.WriteLine($"{asm,-45} {types,6} {members,8}");
}

if (mode == "generate")
{
    string shimDir = Path.Combine(root, "src", "Lonestar.UnityShim");
    var manual = ManualParts.Load(Path.Combine(shimDir, "Manual"));
    var emitter = new Emitter(usage, manual);
    string code = emitter.Emit();
    string outFile = Path.Combine(shimDir, "Generated", "Shim.g.cs");
    Directory.CreateDirectory(Path.GetDirectoryName(outFile));
    File.WriteAllText(outFile, code);
    Console.WriteLine($"Wrote {outFile}: {emitter.EmittedMembers} members ({emitter.ThrowingMembers} throwing gaps in hand-written types)");
}

if (mode == "rewrite")
{
    Rewriter.Run(compilation, decompiled, Path.Combine(root, "src", "Lonestar.Game"));
}

if (mode == "members")
{
    // ShimGen members UnityEngine.Vector3 UnityEngine.Transform ...
    foreach (string typeName in args.Skip(1))
    {
        Console.WriteLine($"== {typeName}");
        foreach (var m in usage.Members.Where(m => Emitter.FullName(m.ContainingType) == typeName).OrderBy(m => m.Name))
        {
            Console.WriteLine($"   {m.Kind,-9} {m.ToDisplayString()}");
        }
    }
}

if (mode == "analyze" && args.Contains("--list"))
{
    foreach (var t in usage.Types.OrderBy(t => t.ToDisplayString()))
    {
        int n = usage.Members.Count(m => SymbolEqualityComparer.Default.Equals(m.ContainingType, t));
        Console.WriteLine($"  {t.ContainingAssembly.Name,-35} {t.ToDisplayString(),-70} {n}");
    }
}
