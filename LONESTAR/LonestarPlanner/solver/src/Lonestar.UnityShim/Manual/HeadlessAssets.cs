using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace UnityEngine
{
    /// <summary>
    /// Rebuilds assets exported by the in-game AssetExporter (SolverLogger):
    /// prefabs become template hierarchies (outside the scene, never awakened) and ScriptableObject
    /// assets become instances, with serialized fields decoded per their declared types.
    /// </summary>
    public static class HeadlessAssets
    {
        /// <summary>Resolves a component/asset type by full name; null for types the shim does not model.</summary>
        public static Func<string, Type> TypeResolver = DefaultTypeResolver;

        private static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

        private static readonly HashSet<string> UiComponentPrefixes = new HashSet<string>
        {
            "UnityEngine.Canvas", "UnityEngine.CanvasRenderer", "UnityEngine.CanvasGroup",
        };

        public static GameObject LoadPrefab(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement[] nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
            var gos = new GameObject[nodes.Length];
            var comps = new List<Component>[nodes.Length];
            bool[] rect = RectFlags(nodes);

            for (int i = 0; i < nodes.Length; i++)
            {
                JsonElement n = nodes[i];
                var go = new GameObject(n.GetProperty("name").GetString(), inScene: false, rectTransform: rect[i])
                {
                    tag = n.GetProperty("tag").GetString(),
                    layer = n.GetProperty("layer").GetInt32(),
                };
                go.SetActiveSilently(n.GetProperty("active").GetBoolean());
                Transform t = go.transform;
                t.localPosition = Vec3(n.GetProperty("pos"));
                JsonElement r = n.GetProperty("rot");
                t.localRotation = new Quaternion(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle());
                t.localScale = Vec3(n.GetProperty("scale"));
                int parent = n.GetProperty("parent").GetInt32();
                if (parent >= 0)
                {
                    t.parentTransform = gos[parent].transform;
                    gos[parent].transform.children.Add(t);
                }
                gos[i] = go;
                comps[i] = new List<Component>();
                foreach (JsonElement c in n.GetProperty("comps").EnumerateArray())
                {
                    Type type = TypeResolver(c.GetProperty("type").GetString());
                    if (type == null || !typeof(Component).IsAssignableFrom(type) || type.IsAbstract || typeof(Transform).IsAssignableFrom(type))
                    {
                        comps[i].Add(null);
                        continue;
                    }
                    var component = (Component)Activator.CreateInstance(type, nonPublic: true);
                    component.owner = go;
                    go.components.Add(component);
                    if (c.TryGetProperty("enabled", out JsonElement en) && component is Behaviour b)
                    {
                        b.SetEnabledSilently(en.GetBoolean());
                    }
                    comps[i].Add(component);
                }
            }

            var ctx = new Context(gos, comps);
            for (int i = 0; i < nodes.Length; i++)
            {
                int j = 0;
                foreach (JsonElement c in nodes[i].GetProperty("comps").EnumerateArray())
                {
                    Component component = comps[i][j++];
                    if (component != null && c.TryGetProperty("fields", out JsonElement fields))
                    {
                        ReadFields(component, fields, ctx);
                    }
                }
            }
            return gos[0];
        }

        public static Object LoadAsset(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            Type type = TypeResolver(root.GetProperty("type").GetString());
            if (type == null || type.IsAbstract)
            {
                return null;
            }
            var asset = (Object)Activator.CreateInstance(type, nonPublic: true);
            asset.name = root.GetProperty("name").GetString();
            ReadFields(asset, root.GetProperty("fields"), new Context(null, null));
            return asset;
        }

        /// <summary>
        /// The export does not say which nodes carry a RectTransform; Unity gives one to every node
        /// inside a Canvas and to every node with UI components, so infer it the same way.
        /// </summary>
        private static bool[] RectFlags(JsonElement[] nodes)
        {
            var flags = new bool[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].TryGetProperty("rect", out JsonElement explicitFlag))
                {
                    flags[i] = explicitFlag.GetBoolean();
                    continue;
                }
                int parent = nodes[i].GetProperty("parent").GetInt32();
                bool ui = nodes[i].GetProperty("comps").EnumerateArray().Any(c =>
                {
                    string t = c.GetProperty("type").GetString() ?? "";
                    return UiComponentPrefixes.Contains(t) || t.StartsWith("UnityEngine.UI.") || t.StartsWith("TMPro.");
                });
                flags[i] = ui || (parent >= 0 && flags[parent]);
            }
            return flags;
        }

        // ---- field decoding ---------------------------------------------------------------------

        private sealed class Context
        {
            public readonly GameObject[] Nodes;
            public readonly List<Component>[] Comps;

            public Context(GameObject[] nodes, List<Component>[] comps)
            {
                Nodes = nodes;
                Comps = comps;
            }
        }

        private static void ReadFields(object target, JsonElement fields, Context ctx)
        {
            if (fields.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            foreach (FieldInfo f in UnitySerializer.SerializedFields(target.GetType()))
            {
                if (!fields.TryGetProperty(f.Name, out JsonElement v))
                {
                    continue;
                }
                if (TryDecode(v, f.FieldType, ctx, out object value))
                {
                    f.SetValue(target, value);
                }
            }
        }

        private static bool TryDecode(JsonElement v, Type t, Context ctx, out object value)
        {
            value = null;
            if (v.ValueKind == JsonValueKind.Null)
            {
                return !t.IsValueType;
            }
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("$unsupported", out _))
            {
                if (t == typeof(Gradient))
                {
                    // never null in Unity; Effect_BreathingLights evaluates it every frame (colours only)
                    value = new Gradient();
                    return true;
                }
                return false;
            }
            try
            {
                if (t == typeof(string))
                {
                    value = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
                    return true;
                }
                if (t == typeof(bool))
                {
                    value = v.GetBoolean();
                    return true;
                }
                if (t.IsEnum)
                {
                    value = Enum.ToObject(t, v.GetInt64());
                    return true;
                }
                if (t == typeof(float))
                {
                    value = v.ValueKind == JsonValueKind.String ? float.Parse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture) : v.GetSingle();
                    return true;
                }
                if (t == typeof(double))
                {
                    value = v.ValueKind == JsonValueKind.String ? double.Parse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture) : v.GetDouble();
                    return true;
                }
                if (t.IsPrimitive)
                {
                    value = Convert.ChangeType(v.GetDecimal(), t, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                if (typeof(Object).IsAssignableFrom(t))
                {
                    value = DecodeReference(v, t, ctx);
                    return true;
                }
                if (t.IsValueType && t.Namespace == "UnityEngine")
                {
                    return TryDecodeUnityStruct(v, t, out value);
                }
                if (t.IsArray)
                {
                    Type et = t.GetElementType();
                    var items = v.EnumerateArray().ToArray();
                    Array array = Array.CreateInstance(et, items.Length);
                    for (int i = 0; i < items.Length; i++)
                    {
                        if (TryDecode(items[i], et, ctx, out object item))
                        {
                            array.SetValue(item, i);
                        }
                    }
                    value = array;
                    return true;
                }
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                {
                    Type et = t.GetGenericArguments()[0];
                    var list = (IList)Activator.CreateInstance(t);
                    foreach (JsonElement item in v.EnumerateArray())
                    {
                        list.Add(TryDecode(item, et, ctx, out object decoded) ? decoded : (et.IsValueType ? Activator.CreateInstance(et) : null));
                    }
                    value = list;
                    return true;
                }
                if (v.ValueKind == JsonValueKind.Object)
                {
                    object obj = UnitySerializer.NewSerializable(t);
                    ReadFields(obj, v, ctx);
                    value = obj;
                    return true;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or MissingMethodException)
            {
                return false;
            }
            return false;
        }

        private static object DecodeReference(JsonElement v, Type t, Context ctx)
        {
            if (v.TryGetProperty("$ref", out JsonElement r))
            {
                if (ctx.Nodes == null)
                {
                    return null;
                }
                int node = r[0].GetInt32();
                int comp = r[1].GetInt32();
                object target = comp switch
                {
                    -1 => ctx.Nodes[node],
                    -2 => ctx.Nodes[node].transform,
                    _ => comp < ctx.Comps[node].Count ? ctx.Comps[node][comp] : null,
                };
                return t.IsInstanceOfType(target) ? target : null;
            }
            if (v.TryGetProperty("$asset", out JsonElement assetType))
            {
                if (v.TryGetProperty("path", out JsonElement path))
                {
                    Object loaded = Resources.Load(path.GetString(), t);
                    if (t.IsInstanceOfType(loaded))
                    {
                        return loaded;
                    }
                }
                // Sprites, materials, audio...: an inert named placeholder keeps visual code from failing.
                if (!t.IsAbstract && t != typeof(GameObject) && !typeof(Component).IsAssignableFrom(t))
                {
                    var placeholder = (Object)Activator.CreateInstance(t, nonPublic: true);
                    placeholder.name = v.TryGetProperty("name", out JsonElement nm) ? nm.GetString() : "";
                    return placeholder;
                }
            }
            return null;
        }

        private static bool TryDecodeUnityStruct(JsonElement v, Type t, out object value)
        {
            value = null;
            if (v.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            float F(int i) => v[i].ValueKind == JsonValueKind.String ? float.Parse(v[i].GetString(), System.Globalization.CultureInfo.InvariantCulture) : v[i].GetSingle();
            if (t == typeof(Vector2)) value = new Vector2(F(0), F(1));
            else if (t == typeof(Vector3)) value = new Vector3(F(0), F(1), F(2));
            else if (t == typeof(Vector4)) value = new Vector4(F(0), F(1), F(2), F(3));
            else if (t == typeof(Quaternion)) value = new Quaternion(F(0), F(1), F(2), F(3));
            else if (t == typeof(Color)) value = new Color(F(0), F(1), F(2), F(3));
            else if (t == typeof(Vector2Int)) value = new Vector2Int(v[0].GetInt32(), v[1].GetInt32());
            else if (t == typeof(Vector3Int))
            {
                var p = new Vector3Int { x = v[0].GetInt32(), y = v[1].GetInt32(), z = v[2].GetInt32() };
                value = p;
            }
            return value != null;
        }

        private static Vector3 Vec3(JsonElement a) => new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());

        private static Type DefaultTypeResolver(string fullName)
        {
            if (fullName == null)
            {
                return null;
            }
            lock (typeCache)
            {
                if (typeCache.TryGetValue(fullName, out Type cached))
                {
                    return cached;
                }
                Type found = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    found = asm.GetType(fullName, throwOnError: false);
                    if (found != null)
                    {
                        break;
                    }
                }
                typeCache[fullName] = found;
                return found;
            }
        }
    }
}
