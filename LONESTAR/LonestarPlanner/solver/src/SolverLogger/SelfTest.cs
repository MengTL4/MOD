using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Utils;
using UnityRandom = UnityEngine.Random;

namespace SolverLogger
{
    /// <summary>
    /// Captures ground truth from the game's own runtime for the two primitives the solver must
    /// reproduce bit-exactly: Mono's string.GetHashCode and UnityEngine.Random (native code).
    /// Floats are logged as raw IEEE-754 bits so nothing is lost to formatting.
    /// </summary>
    internal static class SelfTest
    {
        private static readonly string[] HashSamples =
        {
            "", "a", "ab", "abc", "abcd", "abcde",
            "Shuffle_Deck",
            "MathTide_Normal_Shuffle_Deck_ABCDEFGH",
            "MathTide_Battle_Shuffle_Deck_X1Y2Z3W4_101",
            "MathTide_Boss_key_PowerCore_0123ABCD_3",
            "中文测试字符串",
            "The quick brown fox jumps over the lazy dog 0123456789",
        };

        private static readonly int[] Seeds =
        {
            0, 1, 2, 3, 42, -1, -2, 1283, 123456789, -987654321, int.MaxValue, int.MinValue,
        };

        private static readonly int[] IntBounds = { 1, 2, 3, 7, 10, 36, 1000 };

        private const int Count = 12;

        public static void Run()
        {
            UnityRandom.State saved = UnityRandom.state;
            try
            {
                foreach (string s in HashSamples)
                {
                    Log.Write("selftest_hash", "s", s, "h", s.GetHashCode());
                }

                foreach (int seed in Seeds)
                {
                    UnityRandom.InitState(seed);
                    Log.Write("selftest_state", "seed", seed, "s", StateWords(UnityRandom.state));

                    UnityRandom.InitState(seed);
                    Log.Write("selftest_rng", "seed", seed, "kind", "value", "bits", Repeat(() => Bits(UnityRandom.value)));

                    UnityRandom.InitState(seed);
                    Log.Write("selftest_rng", "seed", seed, "kind", "range_f_0_1", "bits", Repeat(() => Bits(UnityRandom.Range(0f, 1f))));

                    UnityRandom.InitState(seed);
                    Log.Write("selftest_rng", "seed", seed, "kind", "range_f_-2.5_7.25", "bits", Repeat(() => Bits(UnityRandom.Range(-2.5f, 7.25f))));

                    foreach (int n in IntBounds)
                    {
                        UnityRandom.InitState(seed);
                        Log.Write("selftest_rng", "seed", seed, "kind", "range_i_0_" + n, "vals", Repeat(() => UnityRandom.Range(0, n)));
                    }

                    UnityRandom.InitState(seed);
                    Log.Write("selftest_rng", "seed", seed, "kind", "range_i_-5_4", "vals", Repeat(() => UnityRandom.Range(-5, 4)));

                    UnityRandom.InitState(seed);
                    Log.Write("selftest_rng", "seed", seed, "kind", "range_i_10_3", "vals", Repeat(() => UnityRandom.Range(10, 3)));

                    // Interleaved call types, to confirm they all advance one shared stream.
                    UnityRandom.InitState(seed);
                    var mixed = new List<object>();
                    for (int i = 0; i < 4; i++)
                    {
                        mixed.Add(UnityRandom.Range(0, 10));
                        mixed.Add(Bits(UnityRandom.value));
                        mixed.Add(Bits(UnityRandom.Range(0f, 1f)));
                    }
                    Log.Write("selftest_rng", "seed", seed, "kind", "mixed_i10_value_f01", "vals", mixed);
                }

                foreach (int i in new[] { 0, 1, 1283, 4242, 10006 })
                {
                    Log.Write("selftest_gameseed", "i", i, "seed", UtilsClass.CreateGameSeedFromInt(i));
                }
            }
            catch (Exception ex)
            {
                Log.Error("SelfTest", ex);
            }
            finally
            {
                UnityRandom.state = saved;
            }
        }

        private static List<T> Repeat<T>(Func<T> f)
        {
            var list = new List<T>(Count);
            for (int i = 0; i < Count; i++)
            {
                list.Add(f());
            }
            return list;
        }

        private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

        private static List<int> StateWords(UnityRandom.State state)
        {
            object boxed = state;
            return typeof(UnityRandom.State)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => Convert.ToInt32(f.GetValue(boxed)))
                .ToList();
        }
    }
}
