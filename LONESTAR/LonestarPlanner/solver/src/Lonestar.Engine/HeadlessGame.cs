using System;
using System.Collections.Generic;
using System.IO;
using LitJson;
using Tool.Database;
using UnityEngine;
using Utils;

namespace Lonestar.Engine
{
    /// <summary>
    /// Boots the game's own code headlessly: the parts of Launcher.Awake that matter for battles,
    /// then SaveManager.Load() on a restored save, then WantedManager.EnterTheBattle — the same path
    /// as pressing "Continue" and picking the battle in the real game.
    /// </summary>
    public static class HeadlessGame
    {
        /// <summary>Boot steps that threw. Some UI-only failures are tolerable; logic ones are not.</summary>
        public static readonly List<string> BootErrors = new List<string>();

        private static AssetStore store;

        /// <summary>Whether idle frames are merged unless LONESTAR_MERGE_FRAMES says otherwise.</summary>
        public static bool MergeIdleFramesByDefault = true;

        public static void Boot(string exportDir, string resourcesIndex, string persistentDir, IReadOnlyDictionary<string, string> saveFiles, Action<string> log)
        {
            UnityRuntime.Reset();
            BootErrors.Clear();
            Debug.LogSink = log;
            UnityRuntime.UpdateTypes.Add("MonoController");
            // LONESTAR_MERGE_FRAMES=1/0 overrides the default (see UnityRuntime.MergeIdleFrames)
            string merge = Environment.GetEnvironmentVariable("LONESTAR_MERGE_FRAMES");
            UnityRuntime.MergeIdleFrames = merge == null ? MergeIdleFramesByDefault : merge == "1";

            if (Directory.Exists(persistentDir))
            {
                Directory.Delete(persistentDir, recursive: true);
            }
            Directory.CreateDirectory(persistentDir);
            Application.PersistentDataPath = persistentDir.Replace('\\', '/');
            foreach (KeyValuePair<string, string> f in saveFiles)
            {
                string path = Path.Combine(persistentDir, f.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, f.Value);
            }

            store ??= new AssetStore(exportDir, resourcesIndex);
            Resources.Provider = store.Load;

            // Objects the Launcher takes from its scene (level0), which is not exported.
            Define.volume = new GameObject("Volume").AddComponent<UnityEngine.Rendering.Volume>();
            Define.globalLight = new GameObject("GlobalLight").AddComponent<UnityEngine.Rendering.Universal.Light2D>();

            // Launcher.Awake, minus Steam, mods, localization and graphics settings.
            Run("UtilsClass.Init", UtilsClass.Init);
            Run("JsonManager.Init", () => Singleton<JsonManager>.Instance().Init());
            Run("ScreenManager.Init", () => Singleton<ScreenManager>.Instance().Init());
            Run("UnityTypeBindings.Register", UnityTypeBindings.Register);
            Run("GameMasterManager", () => SingletonAutoMono<GameMasterManager>.Instance());
            Run("SaveManager.Init", () => Singleton<SaveManager>.Instance().Init());
            Run("SaveManager.LoadSystemData", () => Singleton<SaveManager>.Instance().LoadSystemData());
            Run("SaveManager.LoadPermanentData", () => Singleton<SaveManager>.Instance().LoadPermanentData());
            Run("UIControllerManager.Init", () => Singleton<UIControllerManager>.Instance().Init());
            Run("UIManager", () => Singleton<UIManager>.Instance());
            Run("ShipDatasManager.InitPlayerShipDatas", () => Singleton<ShipDatasManager>.Instance().InitPlayerShipDatas());
            Run("AudioManager.Init", () => Singleton<AudioManager>.Instance().Init());
            Run("BackgroundManager.Init", () => Singleton<BackgroundManager>.Instance().Init());
            Run("DragManager.Init", () => Singleton<DragManager>.Instance().Init());
            Settle(120);

            // "Continue": restore the run exactly as saved right before the battle.
            Run("SaveManager.Load", () => Singleton<SaveManager>.Instance().Load());
            Settle(600);
        }

        public static void EnterBattle(int enemyId, int battleEventId)
        {
            DataEnemyShip enemy = DataEnemyShipManager.Instance().GetDataByID(enemyId);
            Run("WantedManager.EnterTheBattle", () => Singleton<WantedManager>.Instance().EnterTheBattle(enemy, battleEventId));
        }

        /// <summary>Frames always stepped after an action (a player never acts twice in one frame).</summary>
        public static int MinSettleFrames = 2;

        /// <summary>
        /// Steps until nothing that can change game state is pending (<see cref="UnityRuntime.Quiescent"/>),
        /// at least <see cref="MinSettleFrames"/> and at most <paramref name="maxFrames"/> frames.
        /// </summary>
        public static int Settle(int maxFrames)
        {
            int n = Math.Min(MinSettleFrames, maxFrames);
            for (int i = 0; i < n; i++)
            {
                UnityRuntime.Step();
            }
            int more = UnityRuntime.RunUntil(() => UnityRuntime.Quiescent, maxFrames - n);
            return more < 0 ? -1 : n + more;
        }

        private static void Run(string step, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                BootErrors.Add($"{step}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
