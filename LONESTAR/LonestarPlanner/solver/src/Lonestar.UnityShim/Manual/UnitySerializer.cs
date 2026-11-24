using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityEngine
{
    /// <summary>
    /// Unity's serialization contract, used by Instantiate and by prefab loading:
    /// only public or [SerializeField] instance fields of serializable types are copied; Lists,
    /// arrays and [Serializable] classes are copied by value; references to objects inside the
    /// cloned hierarchy are remapped to their clones, references outside it are kept.
    /// </summary>
    public static class UnitySerializer
    {
        private const int MaxDepth = 10;
        private static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();

        public static GameObject CloneHierarchy(GameObject source, Transform parent)
        {
            var map = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
            GameObject root = BuildShell(source, parent, map, isRoot: true);
            foreach (var pair in map)
            {
                if (pair.Key is Component src && pair.Key is not Transform)
                {
                    CopySerializedFields(src, pair.Value, map);
                }
            }
            UnityRuntime.AwakenHierarchy(root);
            return root;
        }

        private static GameObject BuildShell(GameObject source, Transform parent, Dictionary<object, object> map, bool isRoot)
        {
            var go = new GameObject(isRoot ? source.name + "(Clone)" : source.name, inScene: true, rectTransform: source.transform is RectTransform)
            {
                tag = source.tag,
                layer = source.layer,
            };
            // Stay inactive while building so nothing wakes up before its fields are deserialized.
            go.SetActiveSilently(false);
            map[source] = go;
            map[source.transform] = go.transform;
            go.transform.localPosition = source.transform.localPosition;
            go.transform.localRotation = source.transform.localRotation;
            go.transform.localScale = source.transform.localScale;
            if (parent is not null)
            {
                go.transform.parentTransform = parent;
                parent.children.Add(go.transform);
            }
            for (int i = 1; i < source.components.Count; i++)
            {
                Component src = source.components[i];
                var c = (Component)Activator.CreateInstance(src.GetType(), nonPublic: true);
                c.owner = go;
                go.components.Add(c);
                if (c is MonoBehaviour mb)
                {
                    UnityRuntime.RegisterBehaviour(mb);
                    if (src is Behaviour sb && !sb.enabled)
                    {
                        mb.SetEnabledSilently(false);
                    }
                }
                map[src] = c;
            }
            foreach (Transform child in source.transform.children)
            {
                GameObject childClone = BuildShell(child.gameObject, go.transform, map, isRoot: false);
                childClone.SetActiveSilently(child.gameObject.activeSelf);
            }
            if (isRoot)
            {
                go.SetActiveSilently(source.activeSelf);
            }
            return go;
        }

        /// <summary>
        /// How Unity's serializer creates a [Serializable] class instance: through the parameterless
        /// constructor when there is one (so field initializers run), otherwise uninitialized.
        /// </summary>
        public static object NewSerializable(Type t)
        {
            ConstructorInfo ctor = t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            return ctor != null || t.IsValueType
                ? Activator.CreateInstance(t, nonPublic: true)
                : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
        }

        /// <summary>
        /// What Unity's serializer does to a freshly created component: serialized fields of List,
        /// array and [Serializable] class types get empty instances instead of null.
        /// </summary>
        public static void InitializeSerializableDefaults(object target, int depth = 0)
        {
            if (depth > 3)
            {
                return;
            }
            foreach (FieldInfo f in SerializedFields(target.GetType()))
            {
                if (f.GetValue(target) != null)
                {
                    continue;
                }
                Type t = f.FieldType;
                if (t.IsArray)
                {
                    f.SetValue(target, Array.CreateInstance(t.GetElementType(), 0));
                }
                else if (t == typeof(string))
                {
                    f.SetValue(target, "");
                }
                else if (t == typeof(Gradient))
                {
                    // a Unity built-in serialized by value: never null on a component (Effect_BreathingLights
                    // evaluates it every frame); only colours lights, which nothing in the game reads back
                    f.SetValue(target, new Gradient());
                }
                else if (!t.IsValueType && !typeof(Object).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface
                    && (t.IsGenericType ? t.GetGenericTypeDefinition() == typeof(List<>) : (t.Attributes & TypeAttributes.Serializable) != 0))
                {
                    object value = t.IsGenericType ? Activator.CreateInstance(t) : NewSerializable(t);
                    if (!t.IsGenericType)
                    {
                        InitializeSerializableDefaults(value, depth + 1);
                    }
                    f.SetValue(target, value);
                }
            }
        }

        public static void CopySerializedFields(object src, object dst, Dictionary<object, object> map)
        {
            foreach (FieldInfo f in SerializedFields(src.GetType()))
            {
                object v = CopyValue(f.GetValue(src), f.FieldType, map, 0);
                f.SetValue(dst, v);
            }
        }

        public static FieldInfo[] SerializedFields(Type type)
        {
            if (fieldCache.TryGetValue(type, out FieldInfo[] fields))
            {
                return fields;
            }
            var list = new List<FieldInfo>();
            for (Type t = type; t != null && t != typeof(object) && t.Assembly != typeof(Object).Assembly; t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsInitOnly || f.IsNotSerialized)
                    {
                        continue;
                    }
                    bool marked = f.IsPublic || f.GetCustomAttributesData().Any(a => a.AttributeType.Name is "SerializeField" or "SerializeReference");
                    if (marked && IsSerializableType(f.FieldType, 0))
                    {
                        list.Add(f);
                    }
                }
            }
            fields = list.ToArray();
            fieldCache[type] = fields;
            return fields;
        }

        private static bool IsSerializableType(Type t, int depth)
        {
            if (depth > 2)
            {
                return false;
            }
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || typeof(Object).IsAssignableFrom(t) || t == typeof(Gradient))
            {
                return true;
            }
            if (t.IsArray)
            {
                return t.GetArrayRank() == 1 && IsSerializableType(t.GetElementType(), depth + 1);
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                return IsSerializableType(t.GetGenericArguments()[0], depth + 1);
            }
            if (t.IsGenericType || t.IsAbstract || t.IsInterface || typeof(Delegate).IsAssignableFrom(t))
            {
                return false;
            }
            return (t.Attributes & TypeAttributes.Serializable) != 0 || (t.IsValueType && t.Namespace == "UnityEngine");
        }

        private static object CopyValue(object v, Type declared, Dictionary<object, object> map, int depth)
        {
            switch (v)
            {
                case null:
                    return null;
                case string:
                    return v;
                case Object o:
                    return map.TryGetValue(o, out object mapped) ? mapped : o;
            }
            Type t = v.GetType();
            if (t.IsPrimitive || t.IsEnum)
            {
                return v;
            }
            if (depth >= MaxDepth)
            {
                return null;
            }
            if (v is Array array)
            {
                Type et = t.GetElementType();
                Array copy = Array.CreateInstance(et, array.Length);
                for (int i = 0; i < array.Length; i++)
                {
                    copy.SetValue(CopyValue(array.GetValue(i), et, map, depth + 1), i);
                }
                return copy;
            }
            if (v is IList list && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var copy = (IList)Activator.CreateInstance(t);
                Type et = t.GetGenericArguments()[0];
                foreach (object item in list)
                {
                    copy.Add(CopyValue(item, et, map, depth + 1));
                }
                return copy;
            }
            if (t.IsValueType && t.Namespace == "UnityEngine")
            {
                return v;
            }
            object clone = NewSerializable(t);
            foreach (FieldInfo f in SerializedFields(t))
            {
                f.SetValue(clone, CopyValue(f.GetValue(v), f.FieldType, map, depth + 1));
            }
            return clone;
        }
    }
}
