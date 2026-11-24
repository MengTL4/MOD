using System;
using System.Collections.Generic;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using Process = System.Diagnostics.Process;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace SolverLogger
{
    /// <summary>
    /// In-game advisor: on each player turn it asks the offline solver (tools/Solve) for the best
    /// action sequence for the battle currently being recorded, and puts the answer on screen.
    /// Disabled unless advisor.json exists next to the mod, e.g.
    ///   { "enabled": true, "dotnet": "dotnet",
    ///     "solveDll": "F:\\LonestarSolver\\tools\\Solve\\bin\\Release\\net10.0\\Solve.dll",
    ///     "root": "F:\\LonestarSolver", "jobs": 6, "maxPlans": 32, "verify": 3 }
    /// The solver runs as a separate process because it needs the .NET 10 headless engine.
    /// </summary>
    internal static class Advisor
    {
        private static string modPath;
        private static bool enabled;
        private static string dotnet;
        private static string solveDll;
        private static string root;
        private static int jobs = 6;
        private static int maxPlans = 32;
        private static int verify = 3;
        private static bool dynamicSearch;
        private static int beam = 8;
        private static int depth = 5;
        private static int timeoutSeconds = 240;

        private static int running;
        private static int requestRound = -1;
        private static DateTime startedAt;

        public static string Status = "solver: idle";
        public static string Recommendation;
        public static string Prediction;
        private static string lastError;

        public static void Init(string path)
        {
            modPath = path;
            try
            {
                string configPath = Path.Combine(path, "advisor.json");
                if (!File.Exists(configPath))
                {
                    enabled = false;
                    Status = "solver: disabled (no advisor.json)";
                    return;
                }
                LitJson.JsonData cfg = LitJson.JsonMapper.ToObject(File.ReadAllText(configPath));
                enabled = ReadBool(cfg, "enabled", true);
                dotnet = ReadString(cfg, "dotnet", "dotnet");
                solveDll = ReadString(cfg, "solveDll", null);
                root = ReadString(cfg, "root", null);
                jobs = ReadInt(cfg, "jobs", jobs);
                maxPlans = ReadInt(cfg, "maxPlans", maxPlans);
                verify = ReadInt(cfg, "verify", verify);
                dynamicSearch = ReadBool(cfg, "dynamic", false);
                beam = ReadInt(cfg, "beam", beam);
                depth = ReadInt(cfg, "depth", depth);
                timeoutSeconds = ReadInt(cfg, "timeoutSeconds", timeoutSeconds);
                if (enabled && (string.IsNullOrEmpty(solveDll) || !File.Exists(solveDll)))
                {
                    enabled = false;
                    lastError = "solveDll not found: " + solveDll;
                }
                Status = enabled ? "solver: ready" : "solver: disabled";
                Debug.Log("[SolverLogger] advisor " + Status + (lastError == null ? "" : " (" + lastError + ")"));
            }
            catch (Exception ex)
            {
                enabled = false;
                lastError = ex.Message;
                Status = "solver: config error";
                Debug.LogWarning("[SolverLogger] advisor config failed: " + ex);
            }
        }

        public static void OnPlayerTurn()
        {
            try
            {
                if (!enabled)
                {
                    return;
                }
                BattleManager bm = Singleton<BattleManager>.Instance();
                if (bm == null || !bm.inBattle || bm.battleOver)
                {
                    return;
                }
                string battleFile = Log.BattleFilePath;
                if (string.IsNullOrEmpty(battleFile) || !File.Exists(battleFile))
                {
                    return;
                }
                int round = bm.round;
                if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
                {
                    // A solve for the previous turn is still running; drop this request.
                    return;
                }
                requestRound = round;
                startedAt = DateTime.Now;
                Recommendation = null;
                Prediction = null;
                Status = $"solver: thinking about turn {round}...";
                Directory.CreateDirectory(Path.Combine(modPath, "logs"));
                string snapshot = SnapshotBattleFile(battleFile);
                if (snapshot == null)
                {
                    Interlocked.Exchange(ref running, 0);
                    return;
                }
                string workFile = Path.Combine(modPath, "logs", "advisor_out.txt");
                string args = BuildArguments(snapshot, round, workFile);
                var thread = new Thread(() => RunSolver(args, workFile))
                {
                    IsBackground = true,
                    Name = "lonestar-advisor",
                };
                thread.Start();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SolverLogger] advisor turn hook failed: " + ex);
            }
        }

        /// <summary>
        /// Copies the live recording to a stable file, truncated to the last complete JSON line.
        /// The game keeps the original open for writing; a torn last line would break the reader.
        /// </summary>
        private static string SnapshotBattleFile(string battleFile)
        {
            try
            {
                string text;
                using (var fs = new FileStream(battleFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }
                int lastNewline = text.LastIndexOf('\n');
                if (lastNewline < 0)
                {
                    return null;
                }
                string path = Path.Combine(modPath, "logs", "advisor_battle.jsonl");
                File.WriteAllText(path, text.Substring(0, lastNewline + 1));
                return path;
            }
            catch (Exception ex)
            {
                lastError = "cannot snapshot recording: " + ex.Message;
                return null;
            }
        }

        private static string BuildArguments(string battleFile, int round, string workFile)
        {
            var sb = new StringBuilder();
            sb.Append('"').Append(solveDll).Append("\" \"").Append(battleFile).Append("\"");
            sb.Append(" --turn ").Append(round);
            sb.Append(" --root \"").Append(root ?? Path.GetDirectoryName(Path.GetDirectoryName(solveDll))).Append('"');
            sb.Append(" --search");
            if (dynamicSearch)
            {
                sb.Append(" --dynamic --beam ").Append(beam).Append(" --depth ").Append(depth);
                sb.Append(" --jobs ").Append(jobs);
            }
            else
            {
                sb.Append(" --jobs ").Append(jobs);
                sb.Append(" --max-plans ").Append(maxPlans);
                sb.Append(" --verify ").Append(verify);
            }
            sb.Append(" --quiet");
            return sb.ToString();
        }

        private static void RunSolver(string arguments, string outFile)
        {
            int elapsed = 0;
            try
            {
                var psi = new ProcessStartInfo(dotnet)
                {
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = root ?? Environment.CurrentDirectory,
                };
                using var process = new Process { StartInfo = psi };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (stdout) stdout.AppendLine(e.Data); } };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (stderr) stderr.AppendLine(e.Data);
                    // The solver narrates its progress on stderr; surface a compact version.
                    string line = e.Data.Trim();
                    if (line.Contains("preview ") || line.Contains("verifying") || line.Contains("state:"))
                    {
                        Status = "solver: " + line.TrimStart('[', ']', ' ');
                    }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var watch = Stopwatch.StartNew();
                while (!process.WaitForExit(500))
                {
                    elapsed = (int)watch.Elapsed.TotalSeconds;
                    if (elapsed > timeoutSeconds)
                    {
                        try { process.Kill(); } catch { }
                        lastError = "solver timed out";
                        Status = "solver: timed out";
                        return;
                    }
                }
                string text;
                lock (stdout) text = stdout.ToString();
                File.WriteAllText(outFile, text);
                ParseOutput(text);
                Status = "solver: turn " + requestRound + " ready (" + elapsed + "s)";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Status = "solver: failed";
                Debug.LogWarning("[SolverLogger] advisor solver failed: " + ex);
            }
            finally
            {
                requestRound = -1;
                Interlocked.Exchange(ref running, 0);
            }
        }

        private static void ParseOutput(string text)
        {
            Recommendation = null;
            Prediction = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("RECOMMENDED:", StringComparison.Ordinal))
                {
                    Recommendation = line.Substring("RECOMMENDED:".Length).Trim();
                }
                else if (line.StartsWith("projected:", StringComparison.Ordinal) || line.StartsWith("this turn:", StringComparison.Ordinal))
                {
                    Prediction = line;
                }
            }
            if (Recommendation != null)
            {
                Recommendation = Humanize(Recommendation);
            }
        }

        private static string Humanize(string sequence)
        {
            var parts = new List<string>();
            foreach (string token in sequence.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "end")
                {
                    parts.Add("结束回合");
                }
                else if (token.StartsWith("move:"))
                {
                    parts.Add(int.Parse(token.Substring(5)) > 0 ? "上移1行" : "下移1行");
                }
                else if (token.StartsWith("loadp:"))
                {
                    string body = token.Substring(6);
                    int at = body.IndexOf('@');
                    string card = body.Substring(0, at);
                    string cell = body.Substring(at + 1);
                    string[] pc = card.Split('/');
                    parts.Add($"装能量[{pc[0]}/{ColorName(pc[1])}]→({cell})");
                }
                else if (token.StartsWith("load:"))
                {
                    parts.Add(token);
                }
                else
                {
                    parts.Add(token);
                }
            }
            return string.Join("  ", parts);
        }

        private static string ColorName(string colors)
        {
            var names = colors.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c switch { "1" => "白", "2" => "蓝", "3" => "紫", _ => c });
            return string.Join("", names);
        }

        public static string HudText()
        {
            if (!enabled)
            {
                return null;
            }
            if (Recommendation == null && Status == "solver: idle" && lastError == null)
            {
                return null;
            }
            var sb = new StringBuilder();
            sb.AppendLine(Status);
            if (Recommendation != null)
            {
                sb.AppendLine("推荐: " + Recommendation);
            }
            if (Prediction != null)
            {
                sb.AppendLine(Prediction);
            }
            if (lastError != null && Recommendation == null)
            {
                sb.AppendLine(lastError);
            }
            return sb.ToString();
        }

        public static void Reset()
        {
            Status = enabled ? "solver: idle" : "solver: disabled";
            Recommendation = null;
            Prediction = null;
        }

        private static string ReadString(LitJson.JsonData data, string key, string fallback)
        {
            return data != null && data.Keys.Contains(key) && data[key] != null ? data[key].ToString() : fallback;
        }

        private static bool ReadBool(LitJson.JsonData data, string key, bool fallback)
        {
            if (data == null || !data.Keys.Contains(key) || data[key] == null) return fallback;
            return bool.TryParse(data[key].ToString(), out bool v) && v;
        }

        private static int ReadInt(LitJson.JsonData data, string key, int fallback)
        {
            if (data == null || !data.Keys.Contains(key) || data[key] == null) return fallback;
            return int.TryParse(data[key].ToString(), out int v) ? v : fallback;
        }
    }

    [HarmonyPatch(typeof(BattleManager), "PlayerTurn")]
    internal static class Patch_AdvisorPlayerTurn
    {
        private static void Postfix() => Advisor.OnPlayerTurn();
    }

    [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.BattleEnd))]
    internal static class Patch_AdvisorBattleEnd
    {
        private static void Postfix() => Advisor.Reset();
    }

    /// <summary>Small IMGUI overlay in the top-left corner showing the solver's recommendation.</summary>
    public sealed class AdvisorHud : MonoBehaviour
    {
        private GUIStyle style;

        private void OnGUI()
        {
            string text = Advisor.HudText();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15,
                    wordWrap = true,
                    richText = false,
                };
                style.normal.textColor = Color.white;
            }
            GUI.Box(new Rect(8f, 8f, 620f, 118f), GUIContent.none);
            GUI.Label(new Rect(16f, 12f, 600f, 110f), text, style);
        }
    }
}



