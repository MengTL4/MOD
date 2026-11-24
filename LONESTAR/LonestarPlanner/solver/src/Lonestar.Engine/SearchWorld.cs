using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Lonestar.Engine
{
    /// <summary>
    /// A battle session whose states are addressed by action-token prefixes from a root decision
    /// point ("loadp:3/1@0,1;move:1;end;..."). Reaching a prefix restores the longest cached
    /// ancestor snapshot and replays the remaining tokens, so a search can jump between nodes in a
    /// few milliseconds. Everything is deterministic: the same prefix always yields the same state.
    /// </summary>
    public sealed class SearchWorld
    {
        private readonly Dictionary<string, (WorldSnapshot snap, long used)> cache = new Dictionary<string, (WorldSnapshot, long)>();
        private readonly int capacity;
        private long clock;
        private WorldSnapshot root;

        public BattleSession Session { get; }

        /// <summary>Real actions applied since the session's own start (tokens), i.e. the root's history.</summary>
        public List<string> History { get; } = new List<string>();

        public int Restores { get; private set; }

        public int TokensApplied { get; private set; }

        public SearchWorld(BattleSession session, int capacity = 16)
        {
            Session = session;
            this.capacity = Math.Max(4, capacity);
            root = WorldSnapshot.Capture();
        }

        public static string[] Split(string prefix) =>
            string.IsNullOrWhiteSpace(prefix)
                ? Array.Empty<string>()
                : prefix.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public static string Join(string prefix, string token) =>
            string.IsNullOrEmpty(prefix) ? token : prefix + ";" + token;

        /// <summary>
        /// A snapshot of exactly the current state when the last <see cref="GoTo"/> had one at hand
        /// (cache hit on the whole prefix, or the capture it just cached), else null.
        /// </summary>
        public WorldSnapshot Current { get; private set; }

        /// <summary>Puts the world in the state after <paramref name="prefix"/>; false if a token is illegal there.</summary>
        public bool GoTo(string prefix, bool cacheResult = true)
        {
            Current = null;
            string[] tokens = Split(prefix);
            int start = 0;
            WorldSnapshot from = root;
            // longest cached ancestor
            for (int k = tokens.Length; k > 0; k--)
            {
                string key = string.Join(";", tokens, 0, k);
                if (cache.TryGetValue(key, out var hit))
                {
                    from = hit.snap;
                    cache[key] = (hit.snap, ++clock);
                    start = k;
                    break;
                }
            }
            from.Restore();
            Restores++;
            if (start == tokens.Length)
            {
                Current = from;
            }
            for (int i = start; i < tokens.Length; i++)
            {
                TokensApplied++;
                if (!Session.ApplyToken(tokens[i]))
                {
                    return false;
                }
            }
            if (cacheResult && start < tokens.Length)
            {
                Current = WorldSnapshot.Capture();
                Put(string.Join(";", tokens), Current);
            }
            return true;
        }

        public void Put(string prefix, WorldSnapshot snap)
        {
            if (cache.Count >= capacity)
            {
                string oldest = cache.OrderBy(kv => kv.Value.used).First().Key;
                cache.Remove(oldest);
            }
            cache[prefix] = (snap, ++clock);
        }

        /// <summary>Makes the state after <paramref name="tokens"/> the new root (real actions taken in the game).</summary>
        public bool Advance(string tokens)
        {
            if (!GoTo(tokens, cacheResult: false))
            {
                return false;
            }
            root = WorldSnapshot.Capture();
            cache.Clear();
            History.AddRange(Split(tokens));
            return true;
        }

        /// <summary>
        /// Live mode: applies the actions the game recorded since the last sync on top of the root
        /// and makes that the new root. Returns the number of recorded actions applied.
        /// </summary>
        public int SyncRecording()
        {
            RestoreRoot();
            int n = Session.SyncRecording();
            root = WorldSnapshot.Capture();
            cache.Clear();
            return n;
        }

        public void RestoreRoot()
        {
            root.Restore();
            Restores++;
        }

        /// <summary>
        /// Short stable hash of the full recorder-schema state (used to merge transpositions and to
        /// check that a predicted state was reached). Frame counters are left out: the same state
        /// reached by a different path or frame timing (live sync vs. search) must hash the same.
        /// </summary>
        public static string StateHash()
        {
            string fp = FrameCounter.Replace(BattleSession.Fingerprint(), "");
            byte[] h = SHA1.HashData(Encoding.UTF8.GetBytes(fp));
            return Convert.ToHexString(h, 0, 8);
        }

        private static readonly System.Text.RegularExpressions.Regex FrameCounter =
            new System.Text.RegularExpressions.Regex("\"lastFrameExecuted\":-?\\d+,?", System.Text.RegularExpressions.RegexOptions.Compiled);
    }
}
