using System.Reflection;
using System.Runtime.CompilerServices;

namespace Brain;

/// <summary>
/// Leak hunting: how many objects each static field of every loaded assembly reaches (walking
/// everything, opaque framework types included). Compare two calls to see which root grows.
/// </summary>
public static class Diag
{
    public static Dictionary<string, long> ReachPerRoot(int cap = 3_000_000)
    {
        var result = new Dictionary<string, long>();
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }
            foreach (Type t in types)
            {
                if (t.ContainsGenericParameters)
                {
                    continue;
                }
                FieldInfo[] fields;
                try
                {
                    fields = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }
                foreach (FieldInfo f in fields)
                {
                    if (f.FieldType.IsPrimitive || f.FieldType.IsPointer || f.IsLiteral)
                    {
                        continue;
                    }
                    object v;
                    try
                    {
                        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack() || t.TypeInitializer != null && !Initialized(t))
                        {
                            continue;
                        }
                        v = f.GetValue(null);
                    }
                    catch
                    {
                        continue;
                    }
                    if (v == null)
                    {
                        continue;
                    }
                    result[$"{asm.GetName().Name}: {t.FullName}.{f.Name}"] = Count(v, cap);
                }
            }
        }
        return result;
    }

    // reading a static of a type whose cctor has not run would run it; only look at initialised ones
    private static bool Initialized(Type t)
    {
        try
        {
            return (bool)typeof(RuntimeHelpers).GetMethod("IsClassInitialized", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { t.TypeHandle }) ? true : false;
        }
        catch
        {
            return true;
        }
    }

    private static readonly Dictionary<Type, FieldInfo[]> fieldCache = new();

    private static FieldInfo[] Fields(Type t)
    {
        if (fieldCache.TryGetValue(t, out var fs))
        {
            return fs;
        }
        var list = new List<FieldInfo>();
        for (Type c = t; c != null; c = c.BaseType)
        {
            list.AddRange(c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => !f.FieldType.IsPrimitive && !f.FieldType.IsPointer && f.FieldType != typeof(IntPtr)));
        }
        return fieldCache[t] = list.ToArray();
    }

    private static long Count(object root, int cap)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        stack.Push(root);
        seen.Add(root);
        while (stack.Count > 0 && seen.Count < cap)
        {
            object o = stack.Pop();
            Type t = o.GetType();
            if (t == typeof(string) || t.IsPrimitive || o is Type || o is MemberInfo || o is Assembly || o is Module)
            {
                continue;
            }
            if (o is Array a)
            {
                Type e = t.GetElementType();
                if (e.IsPrimitive || e.IsEnum || e.IsPointer)
                {
                    continue;
                }
                foreach (object x in a)
                {
                    Visit(x);
                }
                continue;
            }
            foreach (FieldInfo f in Fields(t))
            {
                object x;
                try
                {
                    x = f.GetValue(o);
                }
                catch
                {
                    continue;
                }
                Visit(x);
            }
        }
        return seen.Count;

        void Visit(object x)
        {
            if (x == null || x is Pointer)
            {
                return;
            }
            if (x.GetType().IsValueType)
            {
                // boxed struct: walk its fields without counting the box
                foreach (FieldInfo f in Fields(x.GetType()))
                {
                    object y;
                    try
                    {
                        y = f.GetValue(x);
                    }
                    catch
                    {
                        continue;
                    }
                    if (y != null && !y.GetType().IsValueType && seen.Add(y))
                    {
                        stack.Push(y);
                    }
                }
                return;
            }
            if (seen.Add(x))
            {
                stack.Push(x);
            }
        }
    }
}
