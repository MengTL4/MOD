using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SolverLogger
{
    /// <summary>
    /// Exports the Resources/ assets the headless engine needs (TextAssets, ScriptableObject-style
    /// MonoBehaviour assets, prefabs) as JSON, following Unity's serialization rules.
    /// Runs at startup only when Mods/Dev/SolverLogger/export.request exists; its first line is the
    /// output directory. The request file is deleted when the export finishes.
    /// </summary>
    internal static class AssetExporter
    {
        private static readonly HashSet<string> ExportedTypes = new HashSet<string> { "TextAsset", "MonoBehaviour", "GameObject" };

        private static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
        private static Dictionary<int, string> assetPaths;

        public static void RunIfRequested(string modPath)
        {
            string request = Path.Combine(modPath, "export.request");
            if (!File.Exists(request))
            {
                return;
            }
            string outDir = File.ReadAllLines(request).FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(outDir))
            {
                outDir = Path.Combine(modPath, "export");
            }
            try
            {
                Export(Path.Combine(modPath, "resources_index.tsv"), outDir);
                File.Delete(request);
            }
            catch (Exception ex)
            {
                Log.Error("AssetExporter", ex);
            }
        }

        private static void Export(string indexFile, string outDir)
        {
            var started = DateTime.Now;
            var entries = File.ReadAllLines(indexFile)
                .Select(l => l.Split('\t'))
                .Where(p => p.Length == 2 && ExportedTypes.Contains(p[1]))
                .ToList();
            Directory.CreateDirectory(outDir);

            // Pass 1: load everything so cross-references can be written as Resources paths.
            var loaded = new List<(string path, string kind, Object asset)>();
            assetPaths = new Dictionary<int, string>();
            foreach (string[] e in entries)
            {
                Object asset = Resources.Load(e[0]);
                if (asset == null)
                {
                    Log.Write("export_missing", "path", e[0], "type", e[1]);
                    continue;
                }
                loaded.Add((e[0], e[1], asset));
                assetPaths[asset.GetInstanceID()] = e[0];
            }

            // Pass 2: serialize.
            int count = 0;
            foreach (var (path, kind, asset) in loaded)
            {
                try
                {
                    string file = Path.Combine(outDir, path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    switch (asset)
                    {
                        case TextAsset text:
                            File.WriteAllText(file + ".txt", text.text, new UTF8Encoding(false));
                            break;
                        case GameObject prefab:
                            File.WriteAllText(file + ".prefab.json", Prefab(prefab), new UTF8Encoding(false));
                            break;
                        default:
                            File.WriteAllText(file + ".asset.json", Asset(asset), new UTF8Encoding(false));
                            break;
                    }
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Error("export " + path, ex);
                }
            }
            File.WriteAllText(Path.Combine(outDir, "export_info.json"),
                $"{{\"count\":{count},\"unity\":\"{Application.unityVersion}\",\"time\":\"{DateTime.Now:o}\"}}");
            Log.Write("export_done", "count", count, "out", outDir, "seconds", (DateTime.Now - started).TotalSeconds);
            Resources.UnloadUnusedAssets();
        }

        // ---- documents -----------------------------------------------------------------------

        private static string Asset(Object asset)
        {
            var w = new JsonWriter();
            w.Begin();
            w.Key("type").Value(asset.GetType().FullName);
            w.Key("name").Value(asset.name);
            w.Key("fields");
            WriteFields(w, asset, null);
            w.End();
            return w.ToString();
        }

        private static string Prefab(GameObject root)
        {
            var nodes = new List<GameObject>();
            Collect(root.transform, nodes);
            var index = new Dictionary<int, (int node, int comp)>();
            for (int i = 0; i < nodes.Count; i++)
            {
                index[nodes[i].GetInstanceID()] = (i, -1);
                index[nodes[i].transform.GetInstanceID()] = (i, -2);
                Component[] comps = ExportedComponents(nodes[i]);
                for (int j = 0; j < comps.Length; j++)
                {
                    index[comps[j].GetInstanceID()] = (i, j);
                }
            }

            var w = new JsonWriter();
            w.Begin();
            w.Key("name").Value(root.name);
            w.Key("nodes").BeginArray();
            for (int i = 0; i < nodes.Count; i++)
            {
                GameObject go = nodes[i];
                Transform t = go.transform;
                w.Begin();
                w.Key("name").Value(go.name);
                w.Key("active").Value(go.activeSelf);
                w.Key("tag").Value(go.tag);
                w.Key("layer").Value(go.layer);
                w.Key("parent").Value(t.parent == null || i == 0 ? -1 : nodes.IndexOf(t.parent.gameObject));
                w.Key("rect").Value(t is RectTransform);
                w.Key("pos").Floats(t.localPosition.x, t.localPosition.y, t.localPosition.z);
                w.Key("rot").Floats(t.localRotation.x, t.localRotation.y, t.localRotation.z, t.localRotation.w);
                w.Key("scale").Floats(t.localScale.x, t.localScale.y, t.localScale.z);
                w.Key("comps").BeginArray();
                foreach (Component c in ExportedComponents(go))
                {
                    w.Begin();
                    if (c == null)
                    {
                        w.Key("type").Value("<missing script>");
                        w.End();
                        continue;
                    }
                    w.Key("type").Value(c.GetType().FullName);
                    w.Key("asm").Value(c.GetType().Assembly.GetName().Name);
                    if (c is Behaviour b)
                    {
                        w.Key("enabled").Value(b.enabled);
                    }
                    if (IsGameType(c.GetType()))
                    {
                        w.Key("fields");
                        WriteFields(w, c, index);
                    }
                    w.End();
                }
                w.EndArray();
                w.End();
            }
            w.EndArray();
            w.End();
            return w.ToString();
        }

        private static void Collect(Transform t, List<GameObject> nodes)
        {
            nodes.Add(t.gameObject);
            for (int i = 0; i < t.childCount; i++)
            {
                Collect(t.GetChild(i), nodes);
            }
        }

        /// <summary>All components except the Transform, which the node itself describes.</summary>
        private static Component[] ExportedComponents(GameObject go) =>
            go.GetComponents<Component>().Where(c => !(c is Transform)).ToArray();

        // ---- values --------------------------------------------------------------------------

        private static bool IsGameType(Type t)
        {
            string asm = t.Assembly.GetName().Name;
            return asm == "Assembly-CSharp" || asm == "Assembly-CSharp-firstpass";
        }

        private static FieldInfo[] SerializedFields(Type type)
        {
            if (fieldCache.TryGetValue(type, out FieldInfo[] fields))
            {
                return fields;
            }
            var list = new List<FieldInfo>();
            for (Type t = type; t != null && t != typeof(object) && IsGameType(t); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsInitOnly || f.IsNotSerialized || typeof(Delegate).IsAssignableFrom(f.FieldType))
                    {
                        continue;
                    }
                    if (f.IsPublic || f.IsDefined(typeof(SerializeField), true))
                    {
                        list.Add(f);
                    }
                }
            }
            fields = list.ToArray();
            fieldCache[type] = fields;
            return fields;
        }

        private static void WriteFields(JsonWriter w, object o, Dictionary<int, (int, int)> local)
        {
            w.Begin();
            foreach (FieldInfo f in SerializedFields(o.GetType()))
            {
                object v;
                try
                {
                    v = f.GetValue(o);
                }
                catch
                {
                    continue;
                }
                w.Key(f.Name);
                WriteValue(w, v, local, 0);
            }
            w.End();
        }

        private static void WriteValue(JsonWriter w, object v, Dictionary<int, (int node, int comp)> local, int depth)
        {
            switch (v)
            {
                case null:
                    w.Null();
                    return;
                case string s:
                    w.Value(s);
                    return;
                case bool b:
                    w.Value(b);
                    return;
                case float f:
                    w.Value(f);
                    return;
                case double d:
                    w.Value(d);
                    return;
                case Enum e:
                    w.Raw(Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                    return;
                case Object o:
                    if (o == null)
                    {
                        w.Null();
                    }
                    else if (local != null && local.TryGetValue(o.GetInstanceID(), out var r))
                    {
                        w.Begin().Key("$ref").Raw($"[{r.node},{r.comp}]").End();
                    }
                    else
                    {
                        w.Begin().Key("$asset").Value(o.GetType().Name).Key("name").Value(o.name);
                        if (assetPaths.TryGetValue(o.GetInstanceID(), out string path))
                        {
                            w.Key("path").Value(path);
                        }
                        w.End();
                    }
                    return;
                case Vector2 v2:
                    w.Floats(v2.x, v2.y);
                    return;
                case Vector3 v3:
                    w.Floats(v3.x, v3.y, v3.z);
                    return;
                case Vector4 v4:
                    w.Floats(v4.x, v4.y, v4.z, v4.w);
                    return;
                case Quaternion q:
                    w.Floats(q.x, q.y, q.z, q.w);
                    return;
                case Color c:
                    w.Floats(c.r, c.g, c.b, c.a);
                    return;
                case Vector2Int i2:
                    w.Raw($"[{i2.x},{i2.y}]");
                    return;
                case Vector3Int i3:
                    w.Raw($"[{i3.x},{i3.y},{i3.z}]");
                    return;
                case Rect rect:
                    w.Floats(rect.x, rect.y, rect.width, rect.height);
                    return;
            }
            Type t = v.GetType();
            if (t.IsPrimitive)
            {
                w.Raw(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (depth >= 10)
            {
                w.Null();
                return;
            }
            if (v is IList list)
            {
                w.BeginArray();
                foreach (object item in list)
                {
                    WriteValue(w, item, local, depth + 1);
                }
                w.EndArray();
                return;
            }
            if ((t.Attributes & TypeAttributes.Serializable) != 0 && !t.IsGenericType && IsGameType(t))
            {
                w.Begin();
                foreach (FieldInfo f in SerializedFields(t))
                {
                    w.Key(f.Name);
                    WriteValue(w, f.GetValue(v), local, depth + 1);
                }
                w.End();
                return;
            }
            w.Begin().Key("$unsupported").Value(t.FullName).End();
        }
    }

    /// <summary>Minimal streaming JSON writer (the game's LitJson cannot express these documents).</summary>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder sb = new StringBuilder(4096);
        private readonly Stack<bool> first = new Stack<bool>();
        private bool afterKey;

        public JsonWriter Begin()
        {
            Separator();
            sb.Append('{');
            first.Push(true);
            return this;
        }

        public JsonWriter End()
        {
            first.Pop();
            sb.Append('}');
            return this;
        }

        public JsonWriter BeginArray()
        {
            Separator();
            sb.Append('[');
            first.Push(true);
            return this;
        }

        public JsonWriter EndArray()
        {
            first.Pop();
            sb.Append(']');
            return this;
        }

        public JsonWriter Key(string key)
        {
            Separator();
            AppendString(key);
            sb.Append(':');
            afterKey = true;
            return this;
        }

        public JsonWriter Value(string s)
        {
            Separator();
            AppendString(s);
            return this;
        }

        public JsonWriter Value(bool b) => Raw(b ? "true" : "false");

        public JsonWriter Value(int i) => Raw(i.ToString(CultureInfo.InvariantCulture));

        public JsonWriter Value(double d) =>
            Raw(double.IsNaN(d) || double.IsInfinity(d) ? "\"" + d.ToString(CultureInfo.InvariantCulture) + "\"" : d.ToString("R", CultureInfo.InvariantCulture));

        public JsonWriter Value(float f) =>
            Raw(float.IsNaN(f) || float.IsInfinity(f) ? "\"" + f.ToString(CultureInfo.InvariantCulture) + "\"" : f.ToString("R", CultureInfo.InvariantCulture));

        public JsonWriter Floats(params float[] values)
        {
            BeginArray();
            foreach (float f in values)
            {
                Value(f);
            }
            return EndArray();
        }

        public JsonWriter Null() => Raw("null");

        public JsonWriter Raw(string json)
        {
            Separator();
            sb.Append(json);
            return this;
        }

        private void Separator()
        {
            if (afterKey)
            {
                afterKey = false;
                return;
            }
            if (first.Count == 0)
            {
                return;
            }
            if (first.Peek())
            {
                first.Pop();
                first.Push(false);
            }
            else
            {
                sb.Append(',');
            }
        }

        private void AppendString(string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        public override string ToString() => sb.ToString();
    }
}
