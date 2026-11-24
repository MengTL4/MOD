using System;

// Just enough of the mod and of Unity for BrainClient.cs / BrainService.cs to run in plain .NET.

namespace UnityEngine
{
    public static class Debug
    {
        public static void Log(object m) => Console.WriteLine("  [log] " + m);

        public static void LogWarning(object m) => Console.WriteLine("  [warn] " + m);
    }
}

namespace LonestarPlanner.Mod
{
    public static class PlannerMod
    {
        public static Config config = new Config();

        public sealed class Config
        {
            public string solverRoot = "";
            public bool autoStartService = true;
            public int brainJobs = 8;
        }
    }

    internal static class L
    {
        public static bool Zh = true;

        public static string T(string zh, string en) => Zh ? zh : en;
    }
}
