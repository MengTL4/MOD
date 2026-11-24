using System;
using System.Linq;
using System.Reflection;

namespace HarmonyLib
{
    /// <summary>
    /// The game resolves skill, treasure, battle-event and AI classes by name through Harmony
    /// 2.2.2's AccessTools.TypeByName. Newer Harmony throws on "" where 2.2.2 returns null (and the
    /// game then falls back to defaults), so the game is given this 2.2.2-equivalent instead.
    /// </summary>
    [UnityEngine.ShimLenient]
    public static partial class AccessTools
    {
        public static Type TypeByName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            Type local = Type.GetType(name, false);
            if (local != null)
            {
                return local;
            }
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly asm in assemblies)
            {
                Type t = asm.GetType(name, false);
                if (t != null)
                {
                    return t;
                }
            }
            Type[] all = assemblies.SelectMany(SafeTypes).ToArray();
            return all.FirstOrDefault(t => t.FullName == name) ?? all.FirstOrDefault(t => t.Name == name);
        }

        private static Type[] SafeTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null).ToArray();
            }
        }
    }
}
