using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Lonestar.Engine
{
    /// <summary>A battle file written by the recorder (in-game or headless).</summary>
    public sealed class Recording
    {
        public string Path { get; }

        public List<JsonElement> Records { get; }

        public Recording(string path)
        {
            Path = path;
            Records = ReadRecords(path);
        }

        public JsonElement BattleBegin => Records.First(r => Type(r) == "battle_begin");

        public JsonElement BattleInit => Records.First(r => Type(r) == "battle_init");

        public int EnemyId => BattleInit.GetProperty("enemy").GetInt32();

        public int BattleEventId => BattleInit.GetProperty("battle_event").GetInt32();

        /// <summary>Save files (path relative to persistentDataPath → encrypted contents).</summary>
        public Dictionary<string, string> SaveFiles =>
            BattleBegin.TryGetProperty("save_files", out JsonElement files)
                ? files.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString())
                : new Dictionary<string, string>();

        /// <summary>Player decisions to re-apply, in order (act_result records excluded).</summary>
        public IEnumerable<JsonElement> Actions => Records.Where(r => Type(r).StartsWith("act_") && Type(r) != "act_result");

        public IEnumerable<JsonElement> Snaps => Records.Where(r => Type(r) == "snap");

        public static string Type(JsonElement r) => r.GetProperty("t").GetString();

        /// <summary>Consecutive top-level JSON values (early recorder builds split some records across lines).</summary>
        public static List<JsonElement> ReadRecords(string file)
        {
            byte[] bytes;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            // A live battle file may end in a half-written line: only read up to the last newline.
            int end = Array.LastIndexOf(bytes, (byte)10);
            if (end < 0)
            {
                return new List<JsonElement>();
            }
            var list = new List<JsonElement>();
            var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(bytes, 0, end + 1), new JsonReaderOptions { AllowMultipleValues = true });
            while (reader.Read())
            {
                list.Add(JsonElement.ParseValue(ref reader));
            }
            return list;
        }
    }
}
