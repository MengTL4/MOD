using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Lonestar.Engine
{
    /// <summary>Structural diff of two recorder snapshots (in-game vs headless), as used by tools/Replay.</summary>
    public static class StateDiff
    {
        /// <summary>
        /// Never comparable: record numbering, tags, frame counters (headless frames are not the
        /// game's) and activeSprite (a skill's sprite reference, null in the game's recordings).
        /// </summary>
        private static readonly HashSet<string> Ignored = new() { "n", "t", "tag", "lastFrameExecuted", "activeSprite" };

        public static readonly HashSet<string> TransientKeys = new() { "lines", "previewing", "container" };

        public static void Compare(JsonElement x, JsonElement y, string path, List<string> diffs, int max, HashSet<string> alsoIgnored = null)
        {
            if (diffs.Count >= max)
            {
                return;
            }
            if (x.ValueKind != y.ValueKind)
            {
                diffs.Add($"{path}: {Short(x)} != {Short(y)}");
                return;
            }
            switch (x.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty p in x.EnumerateObject())
                    {
                        if (Ignored.Contains(p.Name) || alsoIgnored?.Contains(p.Name) == true)
                        {
                            continue;
                        }
                        if (!y.TryGetProperty(p.Name, out JsonElement yv))
                        {
                            diffs.Add($"{path}.{p.Name}: missing in headless");
                            continue;
                        }
                        Compare(p.Value, yv, $"{path}.{p.Name}", diffs, max, alsoIgnored);
                    }
                    foreach (JsonProperty p in y.EnumerateObject())
                    {
                        if (!Ignored.Contains(p.Name) && alsoIgnored?.Contains(p.Name) != true && !x.TryGetProperty(p.Name, out _))
                        {
                            diffs.Add($"{path}.{p.Name}: only in headless");
                        }
                    }
                    return;
                case JsonValueKind.Array:
                    int lx = x.GetArrayLength(), ly = y.GetArrayLength();
                    if (lx != ly)
                    {
                        diffs.Add($"{path}: length {lx} != {ly}");
                    }
                    for (int i = 0; i < Math.Min(lx, ly); i++)
                    {
                        Compare(x[i], y[i], $"{path}[{i}]", diffs, max, alsoIgnored);
                    }
                    return;
                default:
                    if (x.GetRawText() != y.GetRawText())
                    {
                        diffs.Add($"{path}: {Short(x)} != {Short(y)}");
                    }
                    return;
            }
        }

        private static string Short(JsonElement e)
        {
            string s = e.GetRawText();
            return s.Length > 80 ? s.Substring(0, 77) + "..." : s;
        }
    }
}
