using System;

namespace Lonestar.Engine
{
    /// <summary>
    /// Weights of the cheap one-card-at-a-time playout policy (<see cref="BattleSession.GreedyLoads(System.Collections.Generic.List{string}, RolloutPolicy)"/>).
    /// A card's score on a unit: min(power, line deficit)·Deficit + power·Power (+Match when the
    /// colour exactly fits the slot) − Overkill·surplus on lines already won − EmptyLine·power on
    /// lines without an opponent. The search scores plans by the best of several policies.
    /// </summary>
    public sealed class RolloutPolicy
    {
        public string Name = "default";
        public double Deficit = 3.0;
        public double Power = 0.5;
        public double Match = 1.5;
        public double Overkill = 0.25;
        public double EmptyLine;
        public bool TryMoves;

        public static readonly RolloutPolicy Default = new RolloutPolicy();

        public static readonly RolloutPolicy Mover = new RolloutPolicy { Name = "mover", TryMoves = true, EmptyLine = 0.4 };

        public static readonly RolloutPolicy Defensive = new RolloutPolicy { Name = "defensive", Deficit = 5.0, Power = 0.2, Overkill = 1.0, EmptyLine = 0.4 };

        public static readonly RolloutPolicy Aggressive = new RolloutPolicy { Name = "aggressive", Deficit = 1.5, Power = 2.0, Overkill = 0.0, EmptyLine = 1.0, TryMoves = true };

        public static RolloutPolicy ByName(string name)
        {
            switch ((name ?? "default").ToLowerInvariant())
            {
                case "mover": return Mover;
                case "defensive": return Defensive;
                case "aggressive": return Aggressive;
                default: return Default;
            }
        }
    }
}
