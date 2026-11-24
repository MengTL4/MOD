using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace LonestarPlanner.Tests
{
    /// <summary>极简测试框架：够用、零依赖、输出直观。</summary>
    public static class TestRunner
    {
        private sealed class Case
        {
            public string Name;
            public Action Body;
        }

        private static readonly List<Case> Cases = new List<Case>();

        public static void Test(string name, Action body)
        {
            Cases.Add(new Case { Name = name, Body = body });
        }

        public static int RunAll()
        {
            int passed = 0;
            var failures = new List<string>();
            var sw = Stopwatch.StartNew();

            foreach (var c in Cases)
            {
                try
                {
                    c.Body();
                    passed++;
                    Console.WriteLine("  PASS  " + c.Name);
                }
                catch (Exception ex)
                {
                    failures.Add(c.Name);
                    Console.WriteLine("  FAIL  " + c.Name);
                    Console.WriteLine("        " + ex.Message.Replace("\n", "\n        "));
                }
            }

            sw.Stop();
            Console.WriteLine();
            Console.WriteLine(passed + "/" + Cases.Count + " passed in " + sw.ElapsedMilliseconds + "ms");

            if (failures.Count > 0)
            {
                Console.WriteLine("failed: " + string.Join(", ", failures));
                return 1;
            }
            return 0;
        }

        // ---------------- 断言 ----------------

        public static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception(message + " (expected=" + expected + ", actual=" + actual + ")");
            }
        }

        public static void Near(double expected, double actual, double tolerance, string message)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new Exception(message + " (expected=" + expected + ", actual=" + actual + ")");
            }
        }

        public static void Greater(double actual, double than, string message)
        {
            if (!(actual > than))
            {
                throw new Exception(message + " (expected > " + than + ", actual=" + actual + ")");
            }
        }

        public static void Throws<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            throw new Exception(message + " (expected " + typeof(TException).Name + ")");
        }

        public static string Dump<T>(IEnumerable<T> items)
        {
            return string.Join(" | ", items.Select(x => x == null ? "null" : x.ToString()));
        }
    }
}
