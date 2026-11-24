using System;
using System.Collections.Concurrent;

namespace UnityEngine
{
    /// <summary>
    /// Marks a hand-written type whose remaining (generated) members should behave like inert
    /// null objects instead of throwing. Used for API surfaces that are mostly cosmetic.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class ShimLenientAttribute : Attribute
    {
    }

    /// <summary>
    /// A static field that configures or measures the engine rather than belonging to the game world:
    /// world snapshots leave it alone (a restore must not switch an engine setting back).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class NotWorldStateAttribute : Attribute
    {
    }

    /// <summary>Thrown by generated members of hand-written types that have no real implementation yet.</summary>
    public sealed class ShimNotImplementedException : NotImplementedException
    {
        public ShimNotImplementedException(string member)
            : base("Shim member not implemented: " + member)
        {
        }
    }

    /// <summary>Return values for generated stubs.</summary>
    public static class Shim
    {
        /// <summary>
        /// A harmless value of type <typeparamref name="T"/>: default for value types, "" for strings,
        /// empty arrays, and one shared dummy instance for other concrete classes so chained UI calls
        /// such as <c>panel.GetComponent&lt;Image&gt;().color = c</c> do not throw.
        /// </summary>
        public static T Ret<T>() => NullObject<T>.Value;

        private static class NullObject<T>
        {
            public static readonly T Value = (T)Create(typeof(T));
        }

        private static readonly ConcurrentDictionary<Type, object> dummies = new ConcurrentDictionary<Type, object>();

        internal static object Create(Type t)
        {
            if (t == typeof(string))
            {
                return "";
            }
            if (t.IsValueType)
            {
                return Activator.CreateInstance(t);
            }
            if (t.IsArray)
            {
                return Array.CreateInstance(t.GetElementType(), 0);
            }
            if (t.IsAbstract || t.IsInterface || typeof(Delegate).IsAssignableFrom(t) || t.ContainsGenericParameters)
            {
                return null;
            }
            return dummies.GetOrAdd(t, type =>
            {
                try
                {
                    return Activator.CreateInstance(type, nonPublic: true);
                }
                catch
                {
                    return null;
                }
            });
        }
    }
}
