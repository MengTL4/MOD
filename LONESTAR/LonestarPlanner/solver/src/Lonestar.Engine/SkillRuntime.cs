extern alias RealHarmony;

using System;
using System.Collections.Generic;
using System.Linq;
using RealHarmony::HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace Lonestar.Engine
{
    /// <summary>A unit's clickable active skill at the current decision point.</summary>
    public sealed class SkillOption
    {
        public int UnitIndex;
        public int X;
        public int Y;
        public int UnitId;
        public string Skill;
        public bool TwoStep;
    }

    /// <summary>One choice offered by a two-step skill (a target unit or a target cell).</summary>
    public sealed class TargetOption
    {
        public int Index;
        public bool IsCell;
        public int X;
        public int Y;
        public int UnitId;
    }

    /// <summary>
    /// Captures the callbacks a two-step skill hands to the ship-direction panel, so the solver can
    /// enumerate and choose a target without driving the UI with a mouse.
    /// </summary>
    internal static class SkillTargetCapture
    {
        public static List<ShipUnitController> UnitTargets;
        public static UnityAction<ShipUnitController> UnitAction;
        public static List<ShipCell> CellTargets;
        public static UnityAction<ShipCell> CellAction;
        private static bool patched;

        public static void EnsurePatched()
        {
            if (patched)
            {
                return;
            }
            var harmony = new Harmony("lonestar.headless.skilltarget");
            harmony.PatchAll(typeof(SkillTargetCapture).Assembly);
            patched = true;
        }

        public static void Reset()
        {
            UnitTargets = null;
            UnitAction = null;
            CellTargets = null;
            CellAction = null;
        }
    }

    [HarmonyPatch(typeof(ShipDirPanel), nameof(ShipDirPanel.Init),
        typeof(List<ShipUnitController>), typeof(UnityAction<ShipUnitController>), typeof(UnityAction<ShipUnitController>))]
    internal static class CaptureUnitTargets
    {
        private static void Prefix(List<ShipUnitController> shipUnitControllers, ref UnityAction<ShipUnitController> action)
        {
            SkillTargetCapture.UnitTargets = shipUnitControllers?.Where(u => u != null).ToList();
            SkillTargetCapture.UnitAction = action;
        }
    }

    [HarmonyPatch(typeof(ShipDirPanel), nameof(ShipDirPanel.Init),
        typeof(List<ShipCell>), typeof(UnityAction<ShipCell>), typeof(UnityAction<ShipCell>))]
    internal static class CaptureCellTargets
    {
        private static void Prefix(List<ShipCell> shipCells, ref UnityAction<ShipCell> action)
        {
            SkillTargetCapture.CellTargets = shipCells?.Where(c => c != null).ToList();
            SkillTargetCapture.CellAction = action;
        }
    }
}

