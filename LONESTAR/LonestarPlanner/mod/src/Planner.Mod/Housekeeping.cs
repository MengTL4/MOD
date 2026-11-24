using System;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 数据目录的整理：
    ///   · 旧版把日志写在 mod 目录里（创意工坊更新会删掉、开发版上传会带上），搬到数据目录；
    ///   · 战斗记录每场一个文件，只留最近的一批，不让它无限长。
    /// </summary>
    internal static class Housekeeping
    {
        private const int KeepSessions = 20;
        private const int KeepBattles = 60;
        private const int KeepLoadouts = 20;
        private const int KeepBackups = 30;

        public static void Run(string modPath, string dataRoot)
        {
            try
            {
                Directory.CreateDirectory(dataRoot);
                Migrate(Path.Combine(modPath, "logs"), Path.Combine(dataRoot, "logs"));
                Migrate(Path.Combine(Application.persistentDataPath, "PlannerDumps"), Path.Combine(dataRoot, "backups"));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Planner] moving old logs failed: " + ex.Message);
            }
            new Thread(() =>
            {
                try
                {
                    string logs = Path.Combine(dataRoot, "logs");
                    Trim(logs, "session_*.jsonl", KeepSessions);
                    Trim(Path.Combine(logs, "battles"), "*.jsonl", KeepBattles);
                    Trim(Path.Combine(logs, "loadout"), "*.jsonl", KeepLoadouts);
                    Trim(Path.Combine(dataRoot, "backups"), "*.txt", KeepBackups);
                }
                catch (Exception ex)
                {
                    Debug.Log("[Planner] log cleanup: " + ex.Message);
                }
            }) { IsBackground = true, Name = "PlannerHousekeeping", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
        }

        /// <summary>把 from 下的文件（含子目录）挪到 to 下，已有同名的跳过，搬完删掉 from。</summary>
        private static void Migrate(string from, string to)
        {
            if (!Directory.Exists(from)) return;
            foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, f.Substring(from.Length).TrimStart('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (!File.Exists(target)) File.Move(f, target);
            }
            Directory.Delete(from, true);
            Debug.Log("[Planner] moved " + from + " -> " + to);
        }

        private static void Trim(string dir, string pattern, int keep)
        {
            if (!Directory.Exists(dir)) return;
            foreach (FileInfo f in new DirectoryInfo(dir).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
            {
                try
                {
                    f.Delete();
                }
                catch
                {
                    // 正在写的文件（当前战斗）
                }
            }
        }
    }
}
