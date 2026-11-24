using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ShimGen;

/// <summary>
/// Index of the hand-written shim sources: which types they implement and which members they
/// declare (by name and parameter count), so the generator neither duplicates nor shadows them.
/// </summary>
internal sealed class ManualParts
{
    private readonly HashSet<string> types = new(StringComparer.Ordinal);
    private readonly HashSet<string> members = new(StringComparer.Ordinal);
    private readonly HashSet<string> withOperators = new(StringComparer.Ordinal);
    private readonly HashSet<string> lenient = new(StringComparer.Ordinal);

    public static ManualParts Load(string dir)
    {
        var parts = new ManualParts();
        if (!Directory.Exists(dir))
        {
            return parts;
        }
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            foreach (BaseTypeDeclarationSyntax decl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                parts.AddType(decl);
            }
            foreach (DelegateDeclarationSyntax del in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
            {
                parts.types.Add(FullName(del));
            }
        }
        return parts;
    }

    public bool HasType(string fullName) => types.Contains(fullName);

    /// <summary><paramref name="paramCount"/> of -1 matches a field, property or event of that name.</summary>
    public bool HasMember(string typeFullName, string name, int paramCount) =>
        members.Contains(Key(typeFullName, name, paramCount)) || members.Contains(Key(typeFullName, name, -1));

    public bool HasOperators(string typeFullName) => withOperators.Contains(typeFullName);

    /// <summary>Manual types marked [ShimLenient]: their leftover generated members are inert, not throwing.</summary>
    public bool IsLenient(string typeFullName) => lenient.Contains(typeFullName);

    private void AddType(BaseTypeDeclarationSyntax decl)
    {
        string full = FullName(decl);
        types.Add(full);
        if (decl.AttributeLists.SelectMany(l => l.Attributes).Any(a => a.Name.ToString() is "ShimLenient" or "ShimLenientAttribute"))
        {
            lenient.Add(full);
        }
        if (decl is not TypeDeclarationSyntax td)
        {
            return;
        }
        foreach (MemberDeclarationSyntax m in td.Members)
        {
            switch (m)
            {
                case MethodDeclarationSyntax md:
                    members.Add(Key(full, md.Identifier.Text, md.ParameterList.Parameters.Count));
                    break;
                case ConstructorDeclarationSyntax cd when !cd.Modifiers.Any(SyntaxKind.StaticKeyword):
                    members.Add(Key(full, ".ctor", cd.ParameterList.Parameters.Count));
                    break;
                case PropertyDeclarationSyntax pd:
                    members.Add(Key(full, pd.Identifier.Text, -1));
                    members.Add(Key(full, pd.Identifier.Text, 0));
                    break;
                case IndexerDeclarationSyntax id:
                    members.Add(Key(full, "this", id.ParameterList.Parameters.Count));
                    break;
                case FieldDeclarationSyntax fd:
                    foreach (var v in fd.Declaration.Variables)
                    {
                        members.Add(Key(full, v.Identifier.Text, -1));
                    }
                    break;
                case EventFieldDeclarationSyntax ef:
                    foreach (var v in ef.Declaration.Variables)
                    {
                        members.Add(Key(full, v.Identifier.Text, -1));
                    }
                    break;
                case EventDeclarationSyntax ed:
                    members.Add(Key(full, ed.Identifier.Text, -1));
                    break;
                case OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax:
                    withOperators.Add(full);
                    break;
            }
        }
    }

    private static string Key(string type, string name, int count) => $"{type}::{name}/{count}";

    private static string FullName(MemberDeclarationSyntax decl)
    {
        var names = new Stack<string>();
        for (SyntaxNode n = decl; n != null; n = n.Parent)
        {
            switch (n)
            {
                case BaseTypeDeclarationSyntax t:
                    names.Push(t.Identifier.Text);
                    break;
                case DelegateDeclarationSyntax d:
                    names.Push(d.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    names.Push(ns.Name.ToString());
                    break;
            }
        }
        return string.Join(".", names);
    }
}
