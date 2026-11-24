using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ShimGen;

/// <summary>
/// Finds every symbol from a non-BCL external assembly (Unity, DOTween, TMP, Lean, ...) that the
/// game source depends on: direct references, operators, conversions, foreach patterns, base types,
/// overridden members, and all members of implemented external interfaces.
/// </summary>
internal sealed class UsageCollector
{
    /// <summary>The .NET base libraries, which the game uses for real; everything else is stubbed.</summary>
    private static readonly HashSet<string> Bcl = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib", "netstandard", "System", "System.Core", "Mono.Security",
    };

    private readonly Compilation compilation;

    public HashSet<INamedTypeSymbol> Types { get; } = new(SymbolEqualityComparer.Default);

    public HashSet<ISymbol> Members { get; } = new(SymbolEqualityComparer.Default);

    public UsageCollector(Compilation compilation)
    {
        this.compilation = compilation;
    }

    public static bool IsBcl(IAssemblySymbol asm) =>
        asm != null && (Bcl.Contains(asm.Name) || asm.Name.StartsWith("System.", StringComparison.OrdinalIgnoreCase));

    public bool IsExternal(ISymbol s)
    {
        IAssemblySymbol asm = s?.ContainingAssembly;
        return asm != null && !SymbolEqualityComparer.Default.Equals(asm, compilation.Assembly) && !IsBcl(asm);
    }

    public void CollectAll()
    {
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
            {
                Visit(model, node);
            }
        }
    }

    private void Visit(SemanticModel model, SyntaxNode node)
    {
        switch (node)
        {
            case BaseTypeDeclarationSyntax decl:
                if (model.GetDeclaredSymbol(decl) is INamedTypeSymbol type)
                {
                    AddType(type.BaseType);
                    foreach (INamedTypeSymbol iface in type.AllInterfaces)
                    {
                        AddType(iface);
                        if (IsExternal(iface))
                        {
                            foreach (ISymbol m in iface.GetMembers())
                            {
                                AddSymbol(m);
                            }
                        }
                    }
                }
                return;
            case MemberDeclarationSyntax member:
                foreach (ISymbol declared in DeclaredSymbols(model, member))
                {
                    AddOverridden(declared);
                }
                return;
            case ForEachStatementSyntax fe:
                ForEachStatementInfo info = model.GetForEachStatementInfo(fe);
                AddSymbol(info.GetEnumeratorMethod);
                AddSymbol(info.MoveNextMethod);
                AddSymbol(info.CurrentProperty);
                AddSymbol(info.DisposeMethod);
                AddType(info.ElementType);
                break;
        }

        if (node is ExpressionSyntax or TypeSyntax or ConstructorInitializerSyntax or AttributeSyntax or PrimaryConstructorBaseTypeSyntax)
        {
            SymbolInfo si = model.GetSymbolInfo(node);
            AddSymbol(si.Symbol);
            foreach (ISymbol c in si.CandidateSymbols)
            {
                AddSymbol(c);
            }
            TypeInfo ti = model.GetTypeInfo(node);
            AddType(ti.Type);
            AddType(ti.ConvertedType);
            if (node is ExpressionSyntax expr)
            {
                var conv = model.GetConversion(expr);
                if (conv.IsUserDefined)
                {
                    AddSymbol(conv.MethodSymbol);
                }
            }
        }
        if (node is InitializerExpressionSyntax init && init.IsKind(SyntaxKind.CollectionInitializerExpression))
        {
            foreach (ExpressionSyntax e in init.Expressions)
            {
                AddSymbol(model.GetCollectionInitializerSymbolInfo(e).Symbol);
            }
        }
    }

    private static IEnumerable<ISymbol> DeclaredSymbols(SemanticModel model, MemberDeclarationSyntax member)
    {
        if (member is BaseFieldDeclarationSyntax field)
        {
            foreach (var v in field.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(v) is ISymbol s)
                {
                    yield return s;
                }
            }
        }
        else if (model.GetDeclaredSymbol(member) is ISymbol s)
        {
            yield return s;
        }
    }

    private void AddOverridden(ISymbol declared)
    {
        switch (declared)
        {
            case IMethodSymbol m:
                for (IMethodSymbol o = m.OverriddenMethod; o != null; o = o.OverriddenMethod)
                {
                    AddSymbol(o);
                }
                foreach (IMethodSymbol e in m.ExplicitInterfaceImplementations)
                {
                    AddSymbol(e);
                }
                break;
            case IPropertySymbol p:
                for (IPropertySymbol o = p.OverriddenProperty; o != null; o = o.OverriddenProperty)
                {
                    AddSymbol(o);
                }
                foreach (IPropertySymbol e in p.ExplicitInterfaceImplementations)
                {
                    AddSymbol(e);
                }
                break;
            case IEventSymbol ev:
                for (IEventSymbol o = ev.OverriddenEvent; o != null; o = o.OverriddenEvent)
                {
                    AddSymbol(o);
                }
                break;
        }
    }

    public void AddSymbol(ISymbol s)
    {
        switch (s)
        {
            case null:
                return;
            case ITypeSymbol t:
                AddType(t);
                return;
            case IAliasSymbol alias:
                AddType(alias.Target as ITypeSymbol);
                return;
            case IMethodSymbol m:
                if (m.ReducedFrom != null)
                {
                    m = m.ReducedFrom;
                }
                foreach (ITypeSymbol ta in m.TypeArguments)
                {
                    AddType(ta);
                }
                m = m.OriginalDefinition;
                if (m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet)
                {
                    AddSymbol(m.AssociatedSymbol);
                    return;
                }
                if (m.MethodKind is MethodKind.EventAdd or MethodKind.EventRemove)
                {
                    AddSymbol(m.AssociatedSymbol);
                    return;
                }
                if (!IsExternal(m) || !Members.Add(m))
                {
                    return;
                }
                AddType(m.ContainingType);
                AddType(m.ReturnType);
                foreach (IParameterSymbol p in m.Parameters)
                {
                    AddType(p.Type);
                }
                foreach (ITypeParameterSymbol tp in m.TypeParameters)
                {
                    foreach (ITypeSymbol c in tp.ConstraintTypes)
                    {
                        AddType(c);
                    }
                }
                return;
            case IPropertySymbol p:
                p = p.OriginalDefinition;
                if (!IsExternal(p) || !Members.Add(p))
                {
                    return;
                }
                AddType(p.ContainingType);
                AddType(p.Type);
                foreach (IParameterSymbol ip in p.Parameters)
                {
                    AddType(ip.Type);
                }
                return;
            case IFieldSymbol f:
                f = f.OriginalDefinition;
                if (!IsExternal(f) || !Members.Add(f))
                {
                    return;
                }
                AddType(f.ContainingType);
                AddType(f.Type);
                return;
            case IEventSymbol e:
                e = e.OriginalDefinition;
                if (!IsExternal(e) || !Members.Add(e))
                {
                    return;
                }
                AddType(e.ContainingType);
                AddType(e.Type);
                return;
        }
    }

    public void AddType(ITypeSymbol t)
    {
        switch (t)
        {
            case null:
                return;
            case IArrayTypeSymbol a:
                AddType(a.ElementType);
                return;
            case IPointerTypeSymbol p:
                AddType(p.PointedAtType);
                return;
            case INamedTypeSymbol n:
                foreach (ITypeSymbol ta in n.TypeArguments)
                {
                    AddType(ta);
                }
                n = n.OriginalDefinition;
                if (!IsExternal(n) || !Types.Add(n))
                {
                    return;
                }
                AddType(n.ContainingType);
                AddType(n.BaseType);
                foreach (INamedTypeSymbol i in n.Interfaces)
                {
                    AddType(i);
                }
                foreach (ITypeParameterSymbol tp in n.TypeParameters)
                {
                    foreach (ITypeSymbol c in tp.ConstraintTypes)
                    {
                        AddType(c);
                    }
                }
                if (n.TypeKind == TypeKind.Delegate && n.DelegateInvokeMethod != null)
                {
                    AddType(n.DelegateInvokeMethod.ReturnType);
                    foreach (IParameterSymbol p in n.DelegateInvokeMethod.Parameters)
                    {
                        AddType(p.Type);
                    }
                }
                return;
        }
    }
}
