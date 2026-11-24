using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lonestar.Engine
{
    /// <summary>
    /// Serves Resources.Load from data/export (written by the in-game AssetExporter). Paths are
    /// case-insensitive like Unity's. Assets that were not exported but exist in the game (sprites,
    /// audio...) resolve to inert named placeholders so visual code keeps running.
    /// </summary>
    public sealed class AssetStore
    {
        private readonly Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> knownPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Object> cache = new Dictionary<string, Object>(StringComparer.Ordinal);
        private readonly Dictionary<(string, Type), Object> placeholders = new Dictionary<(string, Type), Object>();

        public AssetStore(string exportDir, string resourcesIndex)
        {
            // Resolve exported type names against the game and shim assemblies explicitly; scanning
            // the AppDomain misses Lonestar.Game until something has touched it.
            var gameAssembly = typeof(BattleManager).Assembly;
            var shimAssembly = typeof(GameObject).Assembly;
            var resolved = new Dictionary<string, Type>(StringComparer.Ordinal);
            HeadlessAssets.TypeResolver = name =>
            {
                if (name == null)
                {
                    return null;
                }
                if (!resolved.TryGetValue(name, out Type t))
                {
                    t = gameAssembly.GetType(name, false) ?? shimAssembly.GetType(name, false);
                    resolved[name] = t;
                }
                return t;
            };

            foreach (string file in Directory.EnumerateFiles(exportDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(exportDir, file).Replace('\\', '/');
                foreach (string suffix in new[] { ".prefab.json", ".asset.json", ".txt" })
                {
                    if (rel.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        files[rel.Substring(0, rel.Length - suffix.Length).ToLowerInvariant()] = file;
                        break;
                    }
                }
            }
            if (File.Exists(resourcesIndex))
            {
                foreach (string line in File.ReadLines(resourcesIndex))
                {
                    int tab = line.IndexOf('\t');
                    knownPaths.Add((tab < 0 ? line : line.Substring(0, tab)).ToLowerInvariant());
                }
            }
        }

        public int Count => files.Count;

        public Object Load(string path, Type type)
        {
            if (path == null)
            {
                return null;
            }
            string key = path.Replace('\\', '/').ToLowerInvariant();
            if (!cache.TryGetValue(key, out Object asset))
            {
                asset = LoadUncached(key);
                cache[key] = asset;
            }
            if (asset is not null && type.IsInstanceOfType(asset))
            {
                return asset;
            }
            if (asset is null && knownPaths.Contains(key) && !type.IsAbstract && type != typeof(GameObject)
                && !typeof(Component).IsAssignableFrom(type) && typeof(Object).IsAssignableFrom(type))
            {
                if (!placeholders.TryGetValue((key, type), out Object placeholder))
                {
                    placeholder = (Object)Activator.CreateInstance(type, nonPublic: true);
                    placeholder.name = Path.GetFileName(key);
                    placeholders[(key, type)] = placeholder;
                }
                return placeholder;
            }
            return null;
        }

        private Object LoadUncached(string key)
        {
            if (!files.TryGetValue(key, out string file))
            {
                return null;
            }
            if (file.EndsWith(".txt", StringComparison.Ordinal))
            {
                return new TextAsset(File.ReadAllText(file)) { name = Path.GetFileName(key) };
            }
            string json = File.ReadAllText(file);
            return file.EndsWith(".prefab.json", StringComparison.Ordinal)
                ? HeadlessAssets.LoadPrefab(json)
                : HeadlessAssets.LoadAsset(json);
        }
    }
}
