using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using HarmonyLib;

namespace DICEOMANCERCheatMenu
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class DICEOMANCERCheatMenu : BaseUnityPlugin
    {
        public const string PluginGuid = "me.mengtl.plugin.cheatmenu";
        public const string PluginName = "DICEOMANCERCheatMenu";
        public const string PluginVersion = "2.1.0";

        internal static ManualLogSource Log;

        private Harmony harmony;
        private CheatMenuWindow window;

        void Awake()
        {
            Log = Logger;
            CheatSettings.Bind(Config);

            harmony = new Harmony(PluginGuid);
            CheatPatches.ApplyAll(harmony, Logger);

            window = new CheatMenuWindow();
            Logger.LogInfo($"插件启动成功! 按 {CheatSettings.Hotkey.Value} 打开修改器");
        }

        void Update()
        {
            window.Update();
        }

        void OnGUI()
        {
            window.OnGUI();
        }

        void OnDestroy()
        {
            if (window != null) window.Shutdown();
            if (harmony != null) harmony.UnpatchSelf();
        }
    }
}
