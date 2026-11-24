using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ShimGen;

/// <summary>Loads the decompiled game scripts as one Roslyn compilation.</summary>
internal static class GameSource
{
    public static readonly string[] GameAssemblies = { "Assembly-CSharp", "Assembly-CSharp-firstpass" };

    public static List<SyntaxTree> Parse(string decompiledRoot)
    {
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>();
        foreach (string asm in GameAssemblies)
        {
            string dir = Path.Combine(decompiledRoot, asm);
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(dir, file);
                if (rel.StartsWith("obj" + Path.DirectorySeparatorChar) || rel.StartsWith("Properties" + Path.DirectorySeparatorChar))
                {
                    continue;
                }
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, path: Path.Combine(asm, rel)));
            }
        }
        return trees;
    }

    /// <summary>
    /// Compiles against the game's real Managed/*.dll (Mono BCL included), i.e. the environment the
    /// decompiled code originally compiled in, so symbol resolution matches the shipped game.
    /// </summary>
    public static CSharpCompilation CompileAgainstGame(List<SyntaxTree> trees, string managedDir)
    {
        var refs = Directory.EnumerateFiles(managedDir, "*.dll")
            .Where(f => !GameAssemblies.Contains(Path.GetFileNameWithoutExtension(f)))
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(f))
            .ToList();
        return CSharpCompilation.Create("Game", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Disable));
    }
}
