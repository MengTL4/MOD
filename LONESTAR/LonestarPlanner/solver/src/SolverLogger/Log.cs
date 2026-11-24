using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SolverLogger
{
    /// <summary>A value that is already serialized JSON and is embedded verbatim.</summary>
    internal sealed class RawJson
    {
        public readonly string Json;

        public RawJson(string json)
        {
            // The game's LitJson pretty-prints; JSON strings cannot hold raw line breaks, so
            // dropping them keeps one record per line without changing the value.
            Json = json?.Replace("\r", "").Replace("\n", "");
        }
    }

    /// <summary>
    /// Append-only JSON-lines logs. Every record goes to the session log; while a battle is being
    /// recorded it is also mirrored into that battle's own file. Each record carries a "t" (type)
    /// and a monotonically increasing "n" so the offline tools can order events exactly.
    /// </summary>
    internal static class Log
    {
        private static StreamWriter session;
        private static StreamWriter battle;
        private static long seq;
        private static readonly object gate = new object();
        private static string logDir;

        public static string FilePath { get; private set; }

        public static string BattleFilePath { get; private set; }

        public static void Init(string modPath)
        {
            logDir = Path.Combine(modPath, "logs");
            Directory.CreateDirectory(logDir);
            FilePath = Path.Combine(logDir, "session_" + Stamp() + ".jsonl");
            session = Open(FilePath);
        }

        public static void BeginBattle(string label)
        {
            EndBattle();
            string dir = Path.Combine(logDir, "battles");
            Directory.CreateDirectory(dir);
            BattleFilePath = Path.Combine(dir, Stamp() + "_" + label + ".jsonl");
            battle = Open(BattleFilePath);
        }

        public static void EndBattle()
        {
            lock (gate)
            {
                battle?.Dispose();
                battle = null;
            }
        }

        /// <summary>Writes one record. <paramref name="kv"/> alternates field name and value.</summary>
        public static void Write(string type, params object[] kv)
        {
            if (session == null)
            {
                return;
            }
            try
            {
                var sb = new StringBuilder(256);
                lock (gate)
                {
                    sb.Append("{\"n\":").Append(seq++).Append(",\"t\":");
                    AppendString(sb, type);
                    for (int i = 0; i + 1 < kv.Length; i += 2)
                    {
                        sb.Append(',');
                        AppendString(sb, (string)kv[i]);
                        sb.Append(':');
                        AppendValue(sb, kv[i + 1]);
                    }
                    sb.Append('}');
                    string line = sb.ToString();
                    session.WriteLine(line);
                    battle?.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[SolverLogger] log write failed: " + ex);
            }
        }

        /// <summary>Serializes alternating key/value pairs the same way <see cref="Write"/> does.</summary>
        public static string ToJson(IList<object> kv)
        {
            var sb = new StringBuilder(4096);
            sb.Append('{');
            for (int i = 0; i + 1 < kv.Count; i += 2)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                AppendString(sb, (string)kv[i]);
                sb.Append(':');
                AppendValue(sb, kv[i + 1]);
            }
            sb.Append('}');
            return sb.ToString();
        }

        public static void Error(string where, Exception ex)
        {
            Write("error", "where", where, "ex", ex.ToString());
        }

        private static string Stamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);

        private static StreamWriter Open(string path)
        {
            // Shared read so the offline tools can run while the game is still open.
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        private static void AppendValue(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null:
                    sb.Append("null");
                    break;
                case RawJson raw:
                    sb.Append(string.IsNullOrEmpty(raw.Json) ? "null" : raw.Json);
                    break;
                case string s:
                    AppendString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case int or long or uint or ulong or short or byte:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                    break;
                case Enum e:
                    sb.Append(Convert.ToInt64(e, CultureInfo.InvariantCulture));
                    break;
                case float f:
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case IDictionary dict:
                    sb.Append('{');
                    bool firstKey = true;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (!firstKey)
                        {
                            sb.Append(',');
                        }
                        firstKey = false;
                        AppendString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        AppendValue(sb, entry.Value);
                    }
                    sb.Append('}');
                    break;
                case IEnumerable list:
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in list)
                    {
                        if (!first)
                        {
                            sb.Append(',');
                        }
                        first = false;
                        AppendValue(sb, item);
                    }
                    sb.Append(']');
                    break;
                default:
                    AppendString(sb, v.ToString());
                    break;
            }
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
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
    }
}
