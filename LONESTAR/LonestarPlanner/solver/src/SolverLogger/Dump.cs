using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SolverLogger
{
    /// <summary>
    /// Reflection dump of an object's plain-data fields (numbers, bools, strings, enums, coordinates,
    /// DynamicValue wrappers and lists of those). References to other game objects are skipped, and
    /// so are hover-preview fields, which mirror the real state only while the mouse is over something.
    /// Used for skills and buffs, whose internal counters differ per class.
    /// </summary>
    internal static class Dump
    {
        private static readonly object Skip = new object();
        private static readonly Dictionary<Type, FieldInfo[]> cache = new Dictionary<Type, FieldInfo[]>();

        public static Dictionary<string, object> Fields(object o)
        {
            if (o == null)
            {
                return null;
            }
            var result = new Dictionary<string, object>();
            foreach (FieldInfo f in FieldsOf(o.GetType()))
            {
                object v;
                try
                {
                    v = Simple(f.GetValue(o), 0);
                }
                catch
                {
                    continue;
                }
                if (v != Skip)
                {
                    result[CleanName(f.Name)] = v;
                }
            }
            return result;
        }

        private static FieldInfo[] FieldsOf(Type type)
        {
            if (cache.TryGetValue(type, out FieldInfo[] fields))
            {
                return fields;
            }
            var list = new List<FieldInfo>();
            for (Type t = type; t != null && t != typeof(object) && t != typeof(MonoBehaviour) && t != typeof(Component); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.Name.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }
                    list.Add(f);
                }
            }
            fields = list.ToArray();
            cache[type] = fields;
            return fields;
        }

        private static string CleanName(string name)
        {
            // Auto-property backing fields look like "<round>k__BackingField".
            if (name.Length > 0 && name[0] == '<')
            {
                int end = name.IndexOf('>');
                if (end > 1)
                {
                    return name.Substring(1, end - 1);
                }
            }
            return name;
        }

        private static object Simple(object v, int depth)
        {
            switch (v)
            {
                case null:
                    return null;
                case string or bool or int or long or float or double or short or byte or uint or ulong:
                    return v;
                case Enum e:
                    return Convert.ToInt64(e);
                case Vector2Int p:
                    return new[] { p.x, p.y };
                case UnityEngine.Object:
                case Delegate:
                    return Skip;
            }
            Type type = v.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DynamicValue<>))
            {
                return Simple(type.GetProperty("Value").GetValue(v), depth);
            }
            if (depth == 0 && v is IList list)
            {
                var items = new List<object>(list.Count);
                foreach (object item in list)
                {
                    object s = Simple(item, depth + 1);
                    if (s == Skip)
                    {
                        return Skip;
                    }
                    items.Add(s);
                }
                return items;
            }
            return Skip;
        }
    }
}
