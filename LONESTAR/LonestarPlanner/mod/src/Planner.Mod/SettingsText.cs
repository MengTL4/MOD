using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Lean.Localization;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 官方 Mod 设置面板的文字。面板把每个设置的标题/提示当翻译键查（"@" 换成 modID），
    /// 翻译来自 mod 目录下 Translation/&lt;语言&gt;/*.csv。这里在框架读取之前，按游戏里实际存在的
    /// 语言写好这些文件：中文（简/繁）用中文，其他语言用英文。只为已有的语言写 —— 目录名如果
    /// 不是游戏已有的语言，框架会把它当成新语言加进游戏的语言列表。
    /// </summary>
    internal static class SettingsText
    {
        private static readonly string[,] Texts =
        {
            { "opt/hdr_general", "通用", "General" },
            { "opt/enabled", "启用战术求解器（重启游戏生效）", "Enable Tactics Solver (restart the game)" },
            { "tip/enabled", "关掉后本 mod 什么都不做。", "When off, the mod does nothing." },
            { "opt/language", "界面语言", "Interface language" },
            { "tip/language", "Auto = 跟随游戏语言（中文显示中文，其他语言显示英文）。", "Auto follows the game language (Chinese for Chinese, English otherwise)." },
            { "opt/uiScale", "界面缩放", "Panel scale" },
            { "tip/uiScale", "求解器面板的大小（以 1080p 为基准）。", "Size of the solver panel, relative to 1080p." },
            { "opt/visibleByDefault", "默认显示面板", "Show the panels by default" },
            { "tip/visibleByDefault", "关掉后面板默认收起，按 F8 或点屏幕左侧的小标签打开。", "When off, the panels start hidden. Press F8 or click the tab on the left edge to show them." },

            { "opt/hdr_battle", "战斗", "Battle" },
            { "opt/battleSeconds", "每回合求解时间（秒，0 = 不限时）", "Seconds per turn (0 = no limit)" },
            { "tip/battleSeconds", "0 = 跑完整的深度搜索（同一局面结果固定，推荐）。设了时间则到点就用目前最好的方案。战斗面板上也能调。", "0 runs the full deep search (the same position always gets the same answer, recommended). With a limit, the best plan found so far is used when time is up. Also adjustable on the battle panel." },
            { "opt/boardMarks", "在棋盘上标出接下来的操作", "Mark the next actions on the board" },
            { "tip/boardMarks", "用方框、连线和序号把接下来几步直接画在船上。", "Draws boxes, lines and step numbers for the next few actions right on the ships." },
            { "opt/autoPilotOnBattleStart", "每场战斗开始时自动接管", "Auto-play every battle from the start" },
            { "tip/autoPilotOnBattleStart", "开启后每场战斗都自动按方案操作，随时按 F7 停下。", "Every battle is played automatically. Press F7 to stop at any time." },
            { "opt/autoActionInterval", "自动操作间隔（秒）", "Delay between automatic actions (s)" },
            { "tip/autoActionInterval", "自动执行时两步之间等多久。", "How long to wait between two automatic actions." },

            { "opt/hdr_dock", "船坞", "Dock" },
            { "opt/loadoutSeconds", "摆位求解时间上限（秒）", "Loadout search time cap (s)" },
            { "tip/loadoutSeconds", "真实模拟摆位最多算这么久，找不到更好的摆法会提前停。船坞面板上也能调。", "The real-simulation loadout search stops at this cap at the latest, and earlier once it stops finding better layouts. Also adjustable on the dock panel." },

            { "opt/hdr_solver", "求解器", "Solver" },
            { "opt/brainJobs", "求解进程数（0 = 自动）", "Solver processes (0 = automatic)" },
            { "tip/brainJobs", "自动 = 每个 CPU 线程一个，内存小于 16GB 时按内存减少（每个约 350MB）。求解器以较低优先级在后台运行，不会抢游戏的 CPU。电脑吃力可以调小。重启游戏后生效。", "Automatic = one per CPU thread, fewer on PCs with less than 16 GB of RAM (each takes about 350 MB). The solver runs at below-normal priority, so the game stays smooth. Lower this if your PC struggles. Takes effect after restarting the game." },
            { "opt/preferLocal", "只用本地估算（不启动求解器）", "Local estimate only (no solver)" },
            { "tip/preferLocal", "只看本回合的快速估算：比完整求解弱很多，但几乎不占 CPU。", "A quick one-turn estimate: much weaker than the full solver, but uses almost no CPU." },
            { "opt/autoStartService", "自动启动求解服务", "Start the solver service automatically" },
            { "tip/autoStartService", "关掉后需要自己运行求解器（开发用）。", "When off, you have to run the solver yourself (for development)." },

            { "opt/hdr_dev", "开发者选项", "Developer options" },
            { "opt/solverRoot", "求解器源码目录（留空 = 用 mod 自带的）", "Solver source folder (empty = the bundled one)" },
            { "tip/solverRoot", "开发用：填仓库里的 solver 目录，就改用那里编译出的求解器。", "For development: the solver folder of a source checkout, whose build is used instead of the bundled one." },
            { "opt/enableDump", "F9 导出船坞局面（调试）", "F9 dumps the dock state (debug)" },
            { "tip/enableDump", "把当前船坞写成文本文件，反馈问题时用。", "Writes the current dock to a text file, for bug reports." },
            { "opt/enableGameMaster", "开启游戏内置 GM 控制台（作弊，影响成就）", "Enable the game's GM console (cheat, affects achievements)" },
            { "tip/enableGameMaster", "游戏中按回车打开控制台。重启游戏生效。", "Press Enter in game to open it. Restart the game to apply." },
        };

        public static void Write(string modPath)
        {
            try
            {
                string zh = Csv(1), en = Csv(2);
                foreach (string lang in new List<string>(LeanLocalization.CurrentLanguages.Keys))
                {
                    string dir = Path.Combine(modPath, "Translation", lang);
                    string file = Path.Combine(dir, "planner.csv");
                    string text = lang.StartsWith("Chinese", StringComparison.Ordinal) ? zh : en;
                    if (File.Exists(file) && File.ReadAllText(file) == text) continue;
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(file, text, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Planner] writing the settings texts failed: " + ex.Message);
            }
        }

        private static string Csv(int column)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Texts.GetLength(0); i++)
            {
                sb.Append('@').Append('/').Append(Texts[i, 0]).Append(";\"").Append(Texts[i, column]).Append("\"\n");
            }
            return sb.ToString();
        }
    }
}
