using System;
using System.IO;
using System.Reflection;
using Mods;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// mod 入口。官方框架要求一个 DLL 里恰好有一个 UserMod 子类，
    /// 它在 ModManager.PatchAllMods() 里被实例化并调用 OnLoad()。
    /// </summary>
    public class PlannerMod : UserMod
    {
        public const string LanguageAuto = "Auto";
        public const string LanguageZh = "简体中文";
        public const string LanguageEn = "English";

        /// <summary>
        /// 用户设置。字段名必须叫 config（UserMod 用反射按名字找），官方 Mod 设置面板自动渲染、
        /// 存到 mod 目录的 config.json。标题是翻译键（"@" = modID），文字见 <see cref="SettingsText"/>。
        /// 设置面板"取消/重置"会整个换掉这个对象，所以各处都现读 PlannerMod.config，不要缓存引用。
        /// </summary>
        public static PlannerConfig config = new PlannerConfig();

        public static PlannerMod Instance { get; private set; }

        /// <summary>mod 自己的数据目录：存档目录/LonestarPlanner（日志、求解器缓存、摆位备份）。</summary>
        public static string DataRoot { get; private set; }

        public class PlannerConfig
        {
            [Label("@/opt/hdr_general")]
            public bool hdrGeneral;

            [BoolField("@/opt/enabled", "@/tip/enabled")]
            public bool enabled = true;

            [ChoiceField(new[] { LanguageAuto, LanguageZh, LanguageEn }, "@/opt/language", "@/tip/language")]
            public string language = LanguageAuto;

            [FloatField(0.6f, 2f, "@/opt/uiScale", "@/tip/uiScale")]
            public float uiScale = 1f;

            [BoolField("@/opt/visibleByDefault", "@/tip/visibleByDefault")]
            public bool visibleByDefault = true;

            [Label("@/opt/hdr_battle")]
            public bool hdrBattle;

            [FloatField(0f, 300f, "@/opt/battleSeconds", "@/tip/battleSeconds")]
            public float battleSeconds = 0f;

            [BoolField("@/opt/boardMarks", "@/tip/boardMarks")]
            public bool boardMarks = true;

            [BoolField("@/opt/autoPilotOnBattleStart", "@/tip/autoPilotOnBattleStart")]
            public bool autoPilotOnBattleStart = false;

            [FloatField(0.15f, 2f, "@/opt/autoActionInterval", "@/tip/autoActionInterval")]
            public float autoActionInterval = 0.45f;

            [Label("@/opt/hdr_dock")]
            public bool hdrDock;

            [FloatField(60f, 1800f, "@/opt/loadoutSeconds", "@/tip/loadoutSeconds")]
            public float loadoutSeconds = 600f;

            [Label("@/opt/hdr_solver")]
            public bool hdrSolver;

            [IntField(0, 64, "@/opt/brainJobs", "@/tip/brainJobs")]
            public int brainJobs = 0;

            [BoolField("@/opt/preferLocal", "@/tip/preferLocal")]
            public bool preferLocal = false;

            [BoolField("@/opt/autoStartService", "@/tip/autoStartService")]
            public bool autoStartService = true;

            [Label("@/opt/hdr_dev")]
            public bool hdrDev;

            [StringField("@/opt/solverRoot", "@/tip/solverRoot")]
            public string solverRoot = "";

            [BoolField("@/opt/enableDump", "@/tip/enableDump")]
            public bool enableDump = false;

            [BoolField("@/opt/enableGameMaster", "@/tip/enableGameMaster")]
            public bool enableGameMaster = false;
        }

        public override void OnLoad()
        {
            Instance = this;
            // 设置面板的文字走游戏自己的翻译表：在框架读 Translation/ 之前按游戏现有的语言写好
            SettingsText.Write(path);
            base.OnLoad();

            if (!config.enabled)
            {
                Debug.Log("[Planner] disabled in the mod settings");
                return;
            }

            if (config.enableGameMaster)
            {
                EnableGameMaster();
            }

            DataRoot = Path.Combine(Application.persistentDataPath, "LonestarPlanner");
            BrainService.ModPath = path;
            BrainService.DataRoot = DataRoot;
            BrainService.SystemMemoryMB = SystemInfo.systemMemorySize;
            Housekeeping.Run(path, DataRoot);

            // 战斗录制器：Brain 服务靠它写出的实时战斗文件同步局面
            try
            {
                SolverLogger.Log.Init(DataRoot);
                SolverLogger.Log.Write("session", "mod", modID, "version", info.version, "unity", Application.unityVersion,
                    "time", DateTime.Now.ToString("o"));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Planner] recorder init failed: " + ex);
            }

            BrainService.CheckGameVersion(Path.Combine(Application.dataPath, "Managed"));
            SolveBudget.Load(config);
            SingletonAutoMono<PlannerHud>.Instance();
            SingletonAutoMono<PlannerOverlay>.Instance();
            Debug.Log("[Planner] loaded " + info.version + ": battle F5 step / F7 auto / F8 panel; dock F6 apply; data in " + DataRoot);
        }

        /// <summary>保存设置（面板上的 -/+ 改了求解时间之后调用），和官方设置面板存同一个 config.json。</summary>
        public static void SaveConfig()
        {
            try
            {
                Instance?.SaveModSettings();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Planner] save settings failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 游戏本身带一个 GM 控制台（GameMasterManager），但被 Define.gameMaster 硬编码关掉了。
        /// 它是 static readonly 而不是 const，所以可以反射改掉；改完在游戏里按 Enter 就能开控制台。
        /// 注意：开启后成就相关逻辑会走 GM 分支。
        /// </summary>
        private static void EnableGameMaster()
        {
            try
            {
                var field = typeof(Define).GetField("gameMaster", BindingFlags.Public | BindingFlags.Static);
                if (field == null)
                {
                    Debug.LogWarning("[Planner] Define.gameMaster not found, GM console stays off");
                    return;
                }

                field.SetValue(null, true);
                Debug.Log("[Planner] GM console enabled: press Enter in game");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Planner] enabling the GM console failed: " + ex.Message);
            }
        }
    }
}
