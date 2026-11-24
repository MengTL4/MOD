using System;
using System.Linq;
using LonestarPlanner.Core;

namespace LonestarPlanner.Tests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "demo")
            {
                return Demo(args.Length > 1 ? args[1] : null);
            }

            Console.WriteLine("Lonestar Planner — core tests");
            Console.WriteLine();
            PlannerTests.Register();
            PlacementTests.Register();
            MovementTests.Register();
            return TestRunner.RunAll();
        }

        /// <summary>
        /// 演示：造一个局面，打印快照和求解结果。
        /// 用法: dotnet run -- demo [seed]
        /// </summary>
        private static int Demo(string seedArg)
        {
            int seed = 2026;
            if (seedArg != null) int.TryParse(seedArg, out seed);

            var state = StateFactory.RandomBattle(new Random(seed), new StateGenOptions
            {
                MinRows = 3,
                MaxRows = 3,
                MinUnits = 3,
                MaxUnits = 4,
                MinSlotsPerUnit = 1,
                MaxSlotsPerUnit = 3,
                MinHand = 4,
                MaxHand = 5,
            });

            Console.WriteLine("=== 局面 (seed=" + seed + ") ===");
            Console.WriteLine("敌方行战力: " + string.Join(", ", state.EnemyRowPower));
            Console.WriteLine("手牌: " + string.Join(", ", state.Hand.Select(t => t.ToString())));
            Console.WriteLine("部件:");
            foreach (var u in state.Units)
            {
                Console.WriteLine("  " + u + " slots=" + string.Join("", u.Slots.Select(s => s.ToString())) +
                                  "  fullValue=" + u.FullLoadValue.ToString("0.0"));
            }

            var solver = new EnergySolver(new ApproxScorer());
            var plan = solver.Solve(state);

            Console.WriteLine();
            Console.WriteLine("=== 方案 ===");
            Console.WriteLine(plan.Describe());

            Console.WriteLine("=== 快照 JSON 前 400 字符 ===");
            var json = StateJson.Write(state);
            Console.WriteLine(json.Length > 400 ? json.Substring(0, 400) + "..." : json);
            return 0;
        }
    }
}
