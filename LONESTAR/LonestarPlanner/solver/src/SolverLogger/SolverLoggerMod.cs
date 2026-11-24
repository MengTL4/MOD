using System;
using Mods;
using UnityEngine;

namespace SolverLogger
{
    /// <summary>Entry point discovered by the game's ModManager (one UserMod subclass per mod).</summary>
    public class SolverLoggerMod : UserMod
    {
        public override void OnLoad()
        {
            try
            {
                Log.Init(path);
                Log.Write("session", "mod", modID, "version", info.version, "unity", Application.unityVersion,
                    "time", DateTime.Now.ToString("o"));
                SelfTest.Run();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SolverLogger] init failed: " + ex);
            }
            // Applies every [HarmonyPatch] in this assembly.
            base.OnLoad();
            Log.Write("patched");
            Debug.Log("[SolverLogger] logging to " + Log.FilePath);

            // Optional in-game advisor overlay (enabled by advisor.json next to the mod).
            try
            {
                Advisor.Init(path);
                var hud = new GameObject("LonestarSolverHud");
                UnityEngine.Object.DontDestroyOnLoad(hud);
                hud.AddComponent<AdvisorHud>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SolverLogger] advisor init failed: " + ex);
            }

            AssetExporter.RunIfRequested(path);
        }
    }
}
