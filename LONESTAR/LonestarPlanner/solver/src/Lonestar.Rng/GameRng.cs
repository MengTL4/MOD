using System;
using System.Collections.Generic;

namespace Lonestar.Rng
{
    /// <summary>
    /// Ports of the game's seeded random helpers (Utils.UtilsClass and WantedProcess.GetStateByKey).
    /// Every helper re-seeds UnityEngine.Random from a per-key stream state before drawing, so each
    /// call is a pure function of that state.
    /// </summary>
    public static class GameRng
    {
        public const string SeedCharset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        /// <summary>Configuration applied to every UnityRandom this class creates.</summary>
        public static FloatMode FloatMode = FloatMode.Mantissa23Div;
        public static LerpMode LerpMode = LerpMode.Reversed;

        public static UnityRandom Seeded(int state) =>
            new UnityRandom(state) { FloatMode = FloatMode, LerpMode = LerpMode };

        // ---- Stream states (WantedProcess.GetStateByKey) ------------------------------------

        /// <summary>First state of an out-of-battle stream; each later call on the key adds 1.</summary>
        public static int NormalStreamBase(string key, string gameSeed) =>
            MonoHash.Hash("MathTide_Normal_" + key + "_" + gameSeed);

        /// <summary>First state of an in-battle stream; the counters reset at every BattleTempData.TempSave.</summary>
        public static int BattleStreamBase(string key, string gameSeed, int smallPhase, int bigPhase) =>
            MonoHash.Hash($"MathTide_Battle_{key}_{gameSeed}_{smallPhase + 100 * bigPhase}");

        /// <summary>First state of an in-battle stream during a successive boss fight.</summary>
        public static int BossStreamBase(string key, string gameSeed, int bossIndex) =>
            MonoHash.Hash($"MathTide_Boss_{key}_{gameSeed}_{bossIndex}");

        // ---- UtilsClass helpers ------------------------------------------------------------

        /// <summary>UtilsClass.RandomRange(float, float, key).</summary>
        public static float RandomRange(float minInclusive, float maxInclusive, int state) =>
            Seeded(state).Range(minInclusive, maxInclusive);

        /// <summary>UtilsClass.RandomRange(int, int, key): floor of a float draw, clamped below max.</summary>
        public static int RandomRange(int minInclusive, int maxExclusive, int state)
        {
            float f = Seeded(state).Range(0f, 1f);
            float scaled = f * (float)(maxExclusive - minInclusive) + (float)minInclusive;
            int num = (int)Math.Floor(scaled);
            if (num >= maxExclusive && num > minInclusive)
            {
                num = maxExclusive - 1;
            }
            return num;
        }

        /// <summary>UtilsClass.ShuffleListPowerSystem: n swaps of a random index with slot 0.</summary>
        public static void ShuffleListPowerSystem<T>(List<T> list, int state)
        {
            UnityRandom r = Seeded(state);
            for (int i = 0; i < list.Count; i++)
            {
                int index = r.Range(0, list.Count);
                T value = list[index];
                list[index] = list[0];
                list[0] = value;
            }
        }

        /// <summary>UtilsClass.ShuffleList: partial Fisher-Yates variant that always swaps with slot 0.</summary>
        public static void ShuffleList<T>(List<T> list, int state)
        {
            UnityRandom r = Seeded(state);
            int count = list.Count;
            for (int i = 0; i < count - 1; i++)
            {
                int index = r.Range(i, count);
                T value = list[index];
                list[index] = list[0];
                list[0] = value;
            }
        }

        /// <summary>UtilsClass.CreateGameSeedFromInt.</summary>
        public static string CreateGameSeedFromInt(int seed, int length = 8)
        {
            UnityRandom r = Seeded(seed);
            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                chars[i] = SeedCharset[r.Range(0, SeedCharset.Length)];
            }
            return new string(chars);
        }
    }
}
