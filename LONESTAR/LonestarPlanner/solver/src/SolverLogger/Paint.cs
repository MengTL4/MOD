using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SolverLogger
{
    /// <summary>
    /// The paint pots of the 多彩号 (ship skill SH_Paint): the ship carries colour points
    /// (<see cref="ShipData.colorSlotListBattle"/>, one entry per point: 1 white, 2 blue, 3 orange);
    /// dropping a hand card on a pot adds that colour to the card and spends the point. The pots are
    /// the <see cref="ColorSlotCell"/>s on the player ship's HP bar. With <c>clickPool</c> (a talent)
    /// clicking a pot instead creates a new 1-power card of that colour.
    ///
    /// Both actions go through the game's own code: a drop is the event sequence of
    /// <see cref="PowerController.OnEndDrag"/> with the pot as the hovered target, so everything that
    /// listens (treasures reacting to colorSlotListUse, previews, visuals) runs exactly as for a
    /// player. Shared by the in-game planner (to execute) and the headless engine (to search).
    /// </summary>
    public static class Paint
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static readonly FieldInfo Hovering = typeof(ColorSlotCell).GetField("currentHovering", Any);
        private static readonly FieldInfo Points = typeof(ColorSlotCell).GetField("current", Any);
        private static readonly MethodInfo ClickPool = typeof(ColorSlotCell).GetMethod("OnClickPool", Any);
        private static readonly MethodInfo PointerExit = typeof(ColorSlotCell).GetMethod("Exit", Any);

        /// <summary>The player's paint pots (empty for every other ship).</summary>
        public static List<ColorSlotCell> Pots()
        {
            List<ColorSlotCell> cells = Singleton<BattleManager>.Instance()?.playerShip?.shipVisual?.barPanel?.colorSlotCells;
            return cells == null ? new List<ColorSlotCell>() : cells.Where(c => c != null).ToList();
        }

        public static ColorSlotCell Pot(int rare) => Pots().FirstOrDefault(c => c.rare == rare);

        /// <summary>Points the pot can still spend (its own count; the shared pool's total with sharedPool).</summary>
        public static int PointsLeft(ColorSlotCell pot) => pot == null || Points == null ? 0 : (int)Points.GetValue(pot);

        public static bool PoolMode => Define.defineValue != null && Define.defineValue.clickPool;

        /// <summary>The conditions <c>ColorSlotCell.PowerAction</c> checks before painting.</summary>
        public static bool CanPaint(EnergyData energy, ColorSlotCell pot) =>
            energy?.powerController != null && pot != null && PointsLeft(pot) != 0
            && !energy.powerColors.colorList.Contains((PowerColor)pot.rare);

        /// <summary>Whether this pot is the one a drop in progress lands on (it acts in PowerAction).</summary>
        public static bool IsDropTarget(ColorSlotCell pot) => Hovering != null && ReferenceEquals(Hovering.GetValue(null), pot);

        /// <summary>Drops <paramref name="energy"/> on the pot of colour <paramref name="rare"/>; false if the game did not paint.</summary>
        public static bool Apply(EnergyData energy, int rare)
        {
            ColorSlotCell pot = Pot(rare);
            if (!CanPaint(energy, pot) || Hovering == null)
            {
                return false;
            }
            PowerController card = energy.powerController;
            EventCenter events = Singleton<EventCenter>.Instance();
            var stats = new DynamicValue<int>(0);
            Hovering.SetValue(null, pot);
            try
            {
                events.EventTrigger(EventName.powerActionCheck, stats, card);
                if (stats.Value == 0)
                {
                    return false;
                }
                if (Singleton<PreviewManager>.Instance().isPreviewing)
                {
                    Singleton<PreviewManager>.Instance().EndPreview();
                }
                events.EventTrigger(EventName.powerAction, stats, card);
                bool painted = stats.Value == 2;
                if (painted)
                {
                    events.EventTrigger(EventName.powerDragEnd);
                }
                events.EventTrigger(EventName.powerActionOver, stats, card, painted ? DragStatus.notDragging : card.dragStatus);
                return painted;
            }
            finally
            {
                Hovering.SetValue(null, null);
            }
        }

        /// <summary>clickPool: clicks the pot of colour <paramref name="rare"/> (a new 1-power card of that colour).</summary>
        public static bool ApplyPool(int rare)
        {
            ColorSlotCell pot = Pot(rare);
            if (!PoolMode || PointsLeft(pot) == 0 || PowerController.draggingPowerController != null || ClickPool == null)
            {
                return false;
            }
            ClickPool.Invoke(pot, null);
            PointerExit?.Invoke(pot, null); // the click leaves the pot hovered (preview on): the pointer moves on
            return true;
        }
    }
}
