using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Lonestar.Engine
{
    /// <summary>
    /// In-process snapshot/restore of the whole headless game world.
    ///
    /// Everything the game can see lives in managed memory reachable from static fields of the game,
    /// shim and RNG assemblies (there is no native Unity underneath). <see cref="Capture"/> walks that
    /// object graph and keeps a shallow copy ("shadow") of every mutable object; <see cref="Restore"/>
    /// writes each shadow back into its original object and resets the static fields. Objects keep
    /// their identity, so identity-keyed dictionaries, hash sets and delegates stay valid, and objects
    /// created after the capture simply become unreachable.
    ///
    /// Field copies are emitted as IL (ldfld/stfld, stobj for structs) so GC write barriers are kept.
    /// The world is single-threaded: never capture or restore while game code is running.
    /// </summary>
    public sealed class WorldSnapshot
    {
        private static readonly string[] RootAssemblies = { "Lonestar.Game", "Lonestar.UnityShim", "Lonestar.Rng" };

        /// <summary>Namespaces whose objects are treated as opaque values (neither walked nor copied).</summary>
        private static readonly string[] OpaqueNamespaces =
        {
            "System.Reflection", "System.Threading", "System.IO", "System.Globalization", "System.Runtime",
            "System.Diagnostics", "System.Security", "System.Net", "System.Text.Json", "Lonestar.Engine",
            "Microsoft.", "HarmonyLib", "MonoMod", "Mono.",
        };

        private readonly object[] originals;
        private readonly object[] shadows;
        private readonly object[] staticValues;

        public int ObjectCount => originals.Length;

        public double CaptureMs { get; private set; }

        private WorldSnapshot(object[] originals, object[] shadows, object[] staticValues)
        {
            this.originals = originals;
            this.shadows = shadows;
            this.staticValues = staticValues;
        }

        // ---- public API ------------------------------------------------------------------------

        public static WorldSnapshot Capture()
        {
            var sw = Stopwatch.StartNew();
            Statics roots = Statics.Get();
            object[] values = roots.Read();
            // the world is single-threaded: reuse one walker's big buffers (fresh ones are large-object-heap
            // allocations on every capture, which under 16 parallel workers cost more than the walk itself)
            Walker walker = pooledWalker ??= new Walker();
            walker.Reset();
            foreach (object v in values)
            {
                walker.Push(v);
            }
            walker.Run();
            var snap = new WorldSnapshot(walker.Originals.ToArray(), walker.Shadows.ToArray(), values);
            snap.CaptureMs = sw.Elapsed.TotalMilliseconds;
            return snap;
        }

        /// <summary>Per object: its field copier, or null for an array. Built on the first restore (a
        /// snapshot is usually restored many times, e.g. once per child in an expand).</summary>
        private Action<object, object>[] copiers;

        public void Restore()
        {
            Action<object, object>[] cp = copiers ??= originals.Select(o => o is Array ? null : TypeInfo.Of(o.GetType()).Copy).ToArray();
            object[] orig = originals, shad = shadows;
            for (int i = 0; i < orig.Length; i++)
            {
                Action<object, object> c = cp[i];
                if (c == null)
                {
                    var a = (Array)orig[i];
                    Array.Copy((Array)shad[i], a, a.Length);
                }
                else
                {
                    c(shad[i], orig[i]);
                }
            }
            Statics.Get().Write(staticValues);
        }

        /// <summary>Diagnostics: objects whose restored fields still differ from their shadow.</summary>
        public List<string> Verify(int max = 20)
        {
            var bad = new List<string>();
            for (int i = 0; i < originals.Length && bad.Count < max; i++)
            {
                object o = originals[i];
                object s = shadows[i];
                if (o is Array a)
                {
                    var b = (Array)s;
                    if (a.Length != b.Length) { bad.Add($"{o.GetType()} length {a.Length} vs {b.Length}"); continue; }
                    int k = 0;
                    foreach (object x in a)
                    {
                        object y = b.GetValue(Flat(b, k++));
                        if (!Same(x, y)) { bad.Add($"{o.GetType()}[{k - 1}]"); break; }
                    }
                    continue;
                }
                foreach (FieldInfo f in InstanceFields(o.GetType()))
                {
                    if (!Same(f.GetValue(o), f.GetValue(s)))
                    {
                        bad.Add($"{o.GetType()}.{f.Name}");
                    }
                }
            }
            return bad;
        }

        /// <summary>Diagnostics: how many captured objects currently differ from their shadow, by type.</summary>
        public Dictionary<string, int> ChangedByType()
        {
            var result = new Dictionary<string, int>();
            for (int i = 0; i < originals.Length; i++)
            {
                object o = originals[i];
                object s = shadows[i];
                bool changed = false;
                if (o is Array a)
                {
                    var b = (Array)s;
                    if (a.Length != b.Length)
                    {
                        changed = true;
                    }
                    else
                    {
                        int k = 0;
                        foreach (object x in a)
                        {
                            if (!Same(x, b.GetValue(Flat(b, k++))))
                            {
                                changed = true;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    foreach (FieldInfo f in InstanceFields(o.GetType()))
                    {
                        if (!Same(f.GetValue(o), f.GetValue(s)))
                        {
                            changed = true;
                            break;
                        }
                    }
                }
                if (changed)
                {
                    string t = o.GetType().Name;
                    result.TryGetValue(t, out int c);
                    result[t] = c + 1;
                }
            }
            return result;
        }

        /// <summary>Diagnostics: List&lt;T&gt; objects whose _size exceeds their backing array.</summary>
        public List<string> BrokenLists()
        {
            var bad = new List<string>();
            for (int i = 0; i < originals.Length; i++)
            {
                object o = originals[i];
                Type t = o.GetType();
                if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(List<>))
                {
                    continue;
                }
                var items = (Array)t.GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
                int size = (int)t.GetField("_size", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
                var sItems = (Array)t.GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shadows[i]);
                int sSize = (int)t.GetField("_size", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shadows[i]);
                if (size > items.Length || sSize > sItems.Length)
                {
                    bad.Add($"{t} size={size} items={items.Length} shadow size={sSize} items={sItems.Length} sameArr={ReferenceEquals(items, sItems)} idx={i}");
                }
            }
            return bad;
        }

        private static int[] Flat(Array a, int k)
        {
            var idx = new int[a.Rank];
            for (int d = a.Rank - 1; d >= 0; d--)
            {
                int len = a.GetLength(d);
                idx[d] = k % len + a.GetLowerBound(d);
                k /= len;
            }
            return idx;
        }

        private static bool Same(object x, object y) =>
            x == null ? y == null : x.GetType().IsValueType ? x.Equals(y) : ReferenceEquals(x, y);

        /// <summary>Diagnostics: object count and approximate shadow bytes per type, largest first.</summary>
        public List<(string Type, int Count, long Bytes)> SizeByType()
        {
            var acc = new Dictionary<Type, (int n, long b)>();
            foreach (object o in originals)
            {
                Type t = o.GetType();
                long b = o is Array a ? 24 + (long)a.Length * ElementSize(t.GetElementType()) : 16 + 8L * InstanceFields(t).Count();
                acc.TryGetValue(t, out var cur);
                acc[t] = (cur.n + 1, cur.b + b);
            }
            return acc.OrderByDescending(kv => kv.Value.b).Select(kv => (kv.Key.ToString(), kv.Value.n, kv.Value.b)).ToList();
        }

        private static int ElementSize(Type e)
        {
            if (!e.IsValueType)
            {
                return 8;
            }
            if (e.IsEnum)
            {
                e = Enum.GetUnderlyingType(e);
            }
            try
            {
                return (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf)).MakeGenericMethod(e).Invoke(null, null);
            }
            catch
            {
                return 8;
            }
        }

        /// <summary>Number of root static fields (diagnostics).</summary>
        public static int RootCount => Statics.Get().Count;

        // ---- graph walk ------------------------------------------------------------------------

        /// <summary>Collects every reachable mutable object and its shallow copy.</summary>
        [ThreadStatic]
        private static Walker pooledWalker;

        public sealed class Walker
        {
            internal void Reset()
            {
                seen.Clear();
                stack.Clear();
                Originals.Clear();
                Shadows.Clear();
            }

            private readonly HashSet<object> seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            private readonly Stack<object> stack = new Stack<object>(1 << 16);
            internal readonly List<object> Originals = new List<object>(1 << 16);
            internal readonly List<object> Shadows = new List<object>(1 << 16);

            public void Push(object o)
            {
                if (o == null || !seen.Add(o))
                {
                    return;
                }
                stack.Push(o);
            }

            internal void Run()
            {
                while (stack.Count > 0)
                {
                    object o = stack.Pop();
                    TypeInfo info = TypeInfo.Of(o.GetType());
                    switch (info.Kind)
                    {
                        case Kind.Opaque:
                            break;
                        case Kind.Delegate:
                            var d = (Delegate)o;
                            Delegate[] list = d.GetInvocationList();
                            if (list.Length == 1)
                            {
                                Push(d.Target);
                            }
                            else
                            {
                                foreach (Delegate part in list)
                                {
                                    Push(part);
                                }
                            }
                            break;
                        case Kind.Object:
                            Originals.Add(o);
                            Shadows.Add(MemberwiseCloneFn(o));
                            info.Visit?.Invoke(o, this);
                            break;
                        case Kind.ArrayNoRefs:
                            Originals.Add(o);
                            Shadows.Add(((Array)o).Clone());
                            break;
                        case Kind.ArrayOfRefs:
                            Originals.Add(o);
                            Shadows.Add(((Array)o).Clone());
                            if (o is object[] sz)
                            {
                                foreach (object e in sz)
                                {
                                    Push(e);
                                }
                            }
                            else
                            {
                                foreach (object e in (Array)o)
                                {
                                    Push(e);
                                }
                            }
                            break;
                        case Kind.ArrayOfStructs:
                            Originals.Add(o);
                            Shadows.Add(((Array)o).Clone());
                            if (info.VisitElements != null)
                            {
                                info.VisitElements((Array)o, this);
                            }
                            else
                            {
                                // Multi-dimensional struct arrays: rare, box each element.
                                TypeInfo elem = TypeInfo.Of(o.GetType().GetElementType());
                                foreach (object e in (Array)o)
                                {
                                    elem.Visit?.Invoke(e, this);
                                }
                            }
                            break;
                    }
                }
            }
        }

        private static readonly Func<object, object> MemberwiseCloneFn = (Func<object, object>)Delegate.CreateDelegate(
            typeof(Func<object, object>),
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic));

        private static readonly MethodInfo PushMethod = typeof(Walker).GetMethod(nameof(Walker.Push));

        private enum Kind
        {
            Opaque,
            Delegate,
            Object,
            ArrayNoRefs,
            ArrayOfRefs,
            ArrayOfStructs,
        }

        /// <summary>Per-type plan: how to walk it and how to copy it back.</summary>
        private sealed class TypeInfo
        {
            private static readonly Dictionary<Type, TypeInfo> cache = new Dictionary<Type, TypeInfo>();

            public Kind Kind;
            public Action<object, Walker> Visit;
            public Action<Array, Walker> VisitElements;
            public Action<object, object> Copy;

            public static TypeInfo Of(Type t)
            {
                if (!cache.TryGetValue(t, out TypeInfo info))
                {
                    info = Build(t);
                    cache[t] = info;
                }
                return info;
            }

            private static TypeInfo Build(Type t)
            {
                var info = new TypeInfo();
                if (t.IsArray)
                {
                    Type e = t.GetElementType();
                    if (e.IsPointer || !ContainsRefs(e))
                    {
                        info.Kind = Kind.ArrayNoRefs;
                    }
                    else if (!e.IsValueType)
                    {
                        info.Kind = Kind.ArrayOfRefs;
                    }
                    else
                    {
                        info.Kind = Kind.ArrayOfStructs;
                        if (t.IsSZArray)
                        {
                            info.VisitElements = EmitElementVisitor(t, e);
                        }
                    }
                    return info;
                }
                if (typeof(Delegate).IsAssignableFrom(t))
                {
                    info.Kind = Kind.Delegate;
                    return info;
                }
                if (IsOpaque(t))
                {
                    info.Kind = Kind.Opaque;
                    return info;
                }
                List<FieldInfo> fields = InstanceFields(t);
                if (fields.Count == 0)
                {
                    info.Kind = Kind.Opaque;
                    return info;
                }
                info.Kind = Kind.Object;
                info.Visit = EmitVisitor(t, fields);
                info.Copy = EmitCopier(t, fields);
                return info;
            }

            private static bool IsOpaque(Type t)
            {
                if (t == typeof(string) || t.IsPrimitive || t.IsEnum || t.IsPointer || typeof(Type).IsAssignableFrom(t) ||
                    typeof(MemberInfo).IsAssignableFrom(t) || t == typeof(decimal) || t == typeof(DateTime) ||
                    t == typeof(Guid) || t == typeof(object))
                {
                    return true;
                }
                string ns = t.Namespace ?? string.Empty;
                if (IsAsyncMachinery(t))
                {
                    return false;
                }
                foreach (string prefix in OpaqueNamespaces)
                {
                    if (ns.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                if (t.Assembly.GetName().Name == "Lonestar.Engine")
                {
                    return true;
                }
                if (t.Namespace == "System.Text" && t != typeof(System.Text.StringBuilder))
                {
                    return true; // encodings
                }
                return false;
            }
        }

        /// <summary>
        /// Pending awaits of the game's async UI effects: tasks, async state machine boxes and the
        /// continuations queued on the frame context. Walked and restored like game objects.
        /// </summary>
        private static bool IsAsyncMachinery(Type t)
        {
            string ns = t.Namespace ?? string.Empty;
            if (ns == "System.Threading.Tasks")
            {
                return t.Name.StartsWith("Task", StringComparison.Ordinal) || t.Name.Contains("Continuation", StringComparison.Ordinal);
            }
            if (ns == "System.Runtime.CompilerServices" || ns == "System.Threading")
            {
                return t.Name.Contains("AsyncStateMachineBox", StringComparison.Ordinal) ||
                       t.Name.Contains("MethodBuilder", StringComparison.Ordinal) ||
                       t.Name.Contains("Continuation", StringComparison.Ordinal);
            }
            return false;
        }

        internal static List<FieldInfo> InstanceFields(Type t)
        {
            var list = new List<FieldInfo>();
            for (Type c = t; c != null && c != typeof(object) && c != typeof(ValueType); c = c.BaseType)
            {
                foreach (FieldInfo f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    list.Add(f);
                }
            }
            return list;
        }

        private static readonly Dictionary<Type, bool> containsRefs = new Dictionary<Type, bool>();

        internal static bool ContainsRefs(Type t)
        {
            if (t.IsPointer || t.IsByRef)
            {
                return false;
            }
            if (!t.IsValueType)
            {
                return true;
            }
            if (t.IsPrimitive || t.IsEnum)
            {
                return false;
            }
            if (containsRefs.TryGetValue(t, out bool r))
            {
                return r;
            }
            containsRefs[t] = false; // recursion guard (structs cannot really contain themselves)
            r = InstanceFields(t).Any(f => ContainsRefs(f.FieldType));
            containsRefs[t] = r;
            return r;
        }

        // ---- IL emission -----------------------------------------------------------------------

        /// <summary>void Visit(object o, Walker w): pushes every reference reachable through o's fields.</summary>
        private static Action<object, Walker> EmitVisitor(Type t, List<FieldInfo> fields)
        {
            if (!fields.Any(f => ContainsRefs(f.FieldType)))
            {
                return null;
            }
            var dm = new DynamicMethod("visit_" + t.Name, null, new[] { typeof(object), typeof(Walker) }, typeof(WorldSnapshot).Module, true);
            ILGenerator il = dm.GetILGenerator();
            foreach (FieldInfo f in fields)
            {
                EmitPushField(il, f, il2 =>
                {
                    il2.Emit(OpCodes.Ldarg_0);
                    if (t.IsValueType)
                    {
                        il2.Emit(OpCodes.Unbox, t);
                    }
                    else
                    {
                        il2.Emit(OpCodes.Castclass, t);
                    }
                }, 0);
            }
            il.Emit(OpCodes.Ret);
            return (Action<object, Walker>)dm.CreateDelegate(typeof(Action<object, Walker>));
        }

        /// <summary>void Visit(T[] a, Walker w): pushes references inside struct elements.</summary>
        private static Action<Array, Walker> EmitElementVisitor(Type arrayType, Type elem)
        {
            var dm = new DynamicMethod("visitarr_" + elem.Name, null, new[] { typeof(Array), typeof(Walker) }, typeof(WorldSnapshot).Module, true);
            ILGenerator il = dm.GetILGenerator();
            LocalBuilder arr = il.DeclareLocal(arrayType);
            LocalBuilder i = il.DeclareLocal(typeof(int));
            Label loop = il.DefineLabel();
            Label check = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, arrayType);
            il.Emit(OpCodes.Stloc, arr);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, i);
            il.Emit(OpCodes.Br, check);
            il.MarkLabel(loop);
            foreach (FieldInfo f in InstanceFields(elem))
            {
                EmitPushField(il, f, il2 =>
                {
                    il2.Emit(OpCodes.Ldloc, arr);
                    il2.Emit(OpCodes.Ldloc, i);
                    il2.Emit(OpCodes.Ldelema, elem);
                }, 0);
            }
            il.Emit(OpCodes.Ldloc, i);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, i);
            il.MarkLabel(check);
            il.Emit(OpCodes.Ldloc, i);
            il.Emit(OpCodes.Ldloc, arr);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Blt, loop);
            il.Emit(OpCodes.Ret);
            return (Action<Array, Walker>)dm.CreateDelegate(typeof(Action<Array, Walker>));
        }

        /// <summary>Emits pushes for field <paramref name="f"/> of the instance whose address/ref <paramref name="loadOwner"/> leaves on the stack.</summary>
        private static void EmitPushField(ILGenerator il, FieldInfo f, Action<ILGenerator> loadOwner, int depth)
        {
            Type ft = f.FieldType;
            if (!ContainsRefs(ft) || depth > 8)
            {
                return;
            }
            if (!ft.IsValueType)
            {
                il.Emit(OpCodes.Ldarg_1);
                loadOwner(il);
                il.Emit(OpCodes.Ldfld, f);
                il.Emit(OpCodes.Call, PushMethod);
                return;
            }
            foreach (FieldInfo inner in InstanceFields(ft))
            {
                EmitPushField(il, inner, il2 =>
                {
                    loadOwner(il2);
                    il2.Emit(OpCodes.Ldflda, f);
                }, depth + 1);
            }
        }

        /// <summary>
        /// void Copy(object src, object dst): dst.f = src.f for every instance field. Reference and
        /// primitive fields are compared first and only written when they differ: after a restore
        /// most objects are unchanged, and skipping the store skips its GC write barrier and keeps
        /// the cache line clean (16 workers restoring at once are memory bound).
        /// </summary>
        private static Action<object, object> EmitCopier(Type t, List<FieldInfo> fields)
        {
            var dm = new DynamicMethod("copy_" + t.Name, null, new[] { typeof(object), typeof(object) }, typeof(WorldSnapshot).Module, true);
            ILGenerator il = dm.GetILGenerator();
            if (t.IsValueType)
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Unbox, t);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Unbox, t);
                il.Emit(OpCodes.Ldobj, t);
                il.Emit(OpCodes.Stobj, t);
            }
            else
            {
                LocalBuilder dst = il.DeclareLocal(t);
                LocalBuilder src = il.DeclareLocal(t);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Castclass, t);
                il.Emit(OpCodes.Stloc, dst);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, t);
                il.Emit(OpCodes.Stloc, src);
                foreach (FieldInfo f in fields)
                {
                    Type ft = f.FieldType;
                    bool comparable = !ft.IsValueType || ft.IsPrimitive || ft.IsEnum;
                    Label skip = il.DefineLabel();
                    if (comparable)
                    {
                        il.Emit(OpCodes.Ldloc, dst);
                        il.Emit(OpCodes.Ldfld, f);
                        il.Emit(OpCodes.Ldloc, src);
                        il.Emit(OpCodes.Ldfld, f);
                        il.Emit(OpCodes.Beq, skip); // NaN never compares equal: it is simply copied
                    }
                    il.Emit(OpCodes.Ldloc, dst);
                    il.Emit(OpCodes.Ldloc, src);
                    il.Emit(OpCodes.Ldfld, f);
                    il.Emit(OpCodes.Stfld, f);
                    il.MarkLabel(skip);
                }
            }
            il.Emit(OpCodes.Ret);
            return (Action<object, object>)dm.CreateDelegate(typeof(Action<object, object>));
        }

        // ---- static roots ----------------------------------------------------------------------

        /// <summary>All static fields of the world assemblies, including closed generic types (Singleton&lt;T&gt;).</summary>
        private sealed class Statics
        {
            private static Statics instance;

            private readonly Func<object[]> read;
            private readonly Action<object[]> write;

            public int Count { get; }

            public IReadOnlyList<FieldInfo> Fields { get; }

            private Statics(List<FieldInfo> fields)
            {
                Fields = fields;
                Count = fields.Count;
                read = EmitRead(fields);
                write = EmitWrite(fields);
            }

            public static Statics Get() => instance ??= new Statics(Discover());

            public object[] Read() => read();

            public void Write(object[] values) => write(values);

            private static List<FieldInfo> Discover()
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => RootAssemblies.Contains(a.GetName().Name))
                    .ToList();
                var types = new HashSet<Type>();
                var all = new List<Type>();
                foreach (Assembly a in assemblies)
                {
                    Type[] ts;
                    try
                    {
                        ts = a.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        ts = ex.Types.Where(x => x != null).ToArray();
                    }
                    all.AddRange(ts);
                    foreach (Type t in ts)
                    {
                        if (!t.ContainsGenericParameters)
                        {
                            types.Add(t);
                        }
                    }
                    // Closed generic instantiations the code refers to (TypeSpec table).
                    foreach (Module m in a.GetModules())
                    {
                        for (int row = 1; row < 1 << 20; row++)
                        {
                            Type spec;
                            try
                            {
                                spec = m.ResolveType(0x1B000000 | row);
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                break;
                            }
                            catch (Exception)
                            {
                                continue;
                            }
                            AddClosed(spec, types, assemblies);
                        }
                    }
                }
                // Singleton-like generic types can be instantiated from generic methods; add every candidate argument.
                // Only the game's Singleton* types: reading a static field runs the type initializer, and other
                // generic holders (e.g. the shim's NullObject<T>) would construct an instance per argument.
                foreach (Type def in all.Where(t => t.IsGenericTypeDefinition && t.Assembly.GetName().Name == "Lonestar.Game" &&
                                                    t.Name.StartsWith("Singleton", StringComparison.Ordinal) &&
                                                    t.GetGenericArguments().Length == 1 && HasStaticOfParamType(t)))
                {
                    Type param = def.GetGenericArguments()[0];
                    foreach (Type arg in all)
                    {
                        if (arg.ContainsGenericParameters || arg.IsAbstract && arg.IsSealed || !SatisfiesConstraints(param, arg))
                        {
                            continue;
                        }
                        try
                        {
                            types.Add(def.MakeGenericType(arg));
                        }
                        catch (ArgumentException)
                        {
                        }
                    }
                }
                var fields = new List<FieldInfo>();
                foreach (Type t in types)
                {
                    if (t.IsInterface)
                    {
                        continue;
                    }
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.IsLiteral || f.FieldType.IsPointer || f.FieldType.IsByRefLike || f.IsDefined(typeof(ThreadStaticAttribute)) ||
                            f.IsDefined(typeof(UnityEngine.NotWorldStateAttribute)))
                        {
                            continue;
                        }
                        fields.Add(f);
                    }
                }
                return fields;
            }

            private static void AddClosed(Type t, HashSet<Type> types, List<Assembly> assemblies)
            {
                if (t == null || t.ContainsGenericParameters)
                {
                    return;
                }
                if (t.IsArray || t.IsPointer || t.IsByRef)
                {
                    AddClosed(t.GetElementType(), types, assemblies);
                    return;
                }
                if (!t.IsConstructedGenericType)
                {
                    return;
                }
                if (assemblies.Contains(t.GetGenericTypeDefinition().Assembly))
                {
                    types.Add(t);
                }
                foreach (Type arg in t.GetGenericArguments())
                {
                    AddClosed(arg, types, assemblies);
                }
            }

            private static bool HasStaticOfParamType(Type def)
            {
                Type p = def.GetGenericArguments()[0];
                return def.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Any(f => !f.IsLiteral && f.FieldType == p);
            }

            private static bool SatisfiesConstraints(Type param, Type arg)
            {
                GenericParameterAttributes a = param.GenericParameterAttributes;
                if ((a & GenericParameterAttributes.DefaultConstructorConstraint) != 0 &&
                    !arg.IsValueType && (arg.IsAbstract || arg.GetConstructor(Type.EmptyTypes) == null))
                {
                    return false;
                }
                if ((a & GenericParameterAttributes.ReferenceTypeConstraint) != 0 && arg.IsValueType)
                {
                    return false;
                }
                if ((a & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0 && !arg.IsValueType)
                {
                    return false;
                }
                foreach (Type c in param.GetGenericParameterConstraints())
                {
                    if (c.ContainsGenericParameters || !c.IsAssignableFrom(arg))
                    {
                        return false;
                    }
                }
                return true;
            }

            private static Func<object[]> EmitRead(List<FieldInfo> fields)
            {
                var dm = new DynamicMethod("statics_read", typeof(object[]), Type.EmptyTypes, typeof(WorldSnapshot).Module, true);
                ILGenerator il = dm.GetILGenerator();
                LocalBuilder arr = il.DeclareLocal(typeof(object[]));
                il.Emit(OpCodes.Ldc_I4, fields.Count);
                il.Emit(OpCodes.Newarr, typeof(object));
                il.Emit(OpCodes.Stloc, arr);
                for (int i = 0; i < fields.Count; i++)
                {
                    il.Emit(OpCodes.Ldloc, arr);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldsfld, fields[i]);
                    if (fields[i].FieldType.IsValueType)
                    {
                        il.Emit(OpCodes.Box, fields[i].FieldType);
                    }
                    il.Emit(OpCodes.Stelem_Ref);
                }
                il.Emit(OpCodes.Ldloc, arr);
                il.Emit(OpCodes.Ret);
                return (Func<object[]>)dm.CreateDelegate(typeof(Func<object[]>));
            }

            private static Action<object[]> EmitWrite(List<FieldInfo> fields)
            {
                var dm = new DynamicMethod("statics_write", null, new[] { typeof(object[]) }, typeof(WorldSnapshot).Module, true);
                ILGenerator il = dm.GetILGenerator();
                for (int i = 0; i < fields.Count; i++)
                {
                    FieldInfo f = fields[i];
                    if (f.IsInitOnly)
                    {
                        continue; // the reference never changes; the object itself is restored by the graph walk
                    }
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldelem_Ref);
                    if (f.FieldType.IsValueType)
                    {
                        il.Emit(OpCodes.Unbox_Any, f.FieldType);
                    }
                    else
                    {
                        il.Emit(OpCodes.Castclass, f.FieldType);
                    }
                    il.Emit(OpCodes.Stsfld, f);
                }
                il.Emit(OpCodes.Ret);
                return (Action<object[]>)dm.CreateDelegate(typeof(Action<object[]>));
            }
        }
    }
}
