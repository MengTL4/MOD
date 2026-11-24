using System;
using System.Collections.Generic;
using System.Text;

namespace LonestarPlanner.Core
{
    /// <summary>规划方案中的一步：把哪枚 token 打到哪个部件（落到哪个槽由规则决定）。</summary>
    public sealed class PlanStep
    {
        public int TokenId;
        public int TokenPower;
        public PowerColor TokenColor;
        public int UnitId;
        public string UnitName = string.Empty;
        public int UnitRow;
        public int SlotIndex;
        public bool CompletesUnit;

        public override string ToString()
        {
            return TokenColor.ShortName() + TokenPower + " → " + UnitName + "(R" + UnitRow + " Slot" + SlotIndex + ")"
                + (CompletesUnit ? " [装满]" : string.Empty);
        }
    }

    /// <summary>一行的战斗结果（与游戏 BattleResultInfo.result 的口径一致：正数=我方赢该行）。</summary>
    public sealed class RowOutcome
    {
        public int Row;
        public int PlayerPower;
        public int EnemyPower;
        public int Diff;
        public bool IsWin;
        public double Value;
    }

    /// <summary>方案执行后的局面推演结果，用来给 UI 显示和给测试断言。</summary>
    public sealed class BattleOutcome
    {
        public RowOutcome[] Rows;
        public List<int> CompletedUnitIds = new List<int>();
        public int TotalPlayerPower;

        public int Wins
        {
            get
            {
                int n = 0;
                if (Rows != null)
                {
                    for (int i = 0; i < Rows.Length; i++)
                    {
                        if (Rows[i].IsWin) n++;
                    }
                }
                return n;
            }
        }

        public int StrongestDiff
        {
            get
            {
                int best = 0;
                if (Rows != null)
                {
                    for (int i = 0; i < Rows.Length; i++)
                    {
                        if (Rows[i].Diff > best) best = Rows[i].Diff;
                    }
                }
                return best;
            }
        }
    }

    /// <summary>求解器输出。Steps 的顺序就是玩家应该执行的顺序（顺序在这里是有意义的）。</summary>
    public sealed class Plan
    {
        public List<PlanStep> Steps = new List<PlanStep>();
        public List<int> UnusedTokenIds = new List<int>();
        public double Score;
        public BattleOutcome Outcome;
        public bool IsExact;
        public bool IsOptimisticBound;
        public int NodesEvaluated;

        public string Explain()
        {
            var sb = new StringBuilder();
            sb.Append("score=").Append(Score.ToString("0.##"));
            if (Outcome != null && Outcome.Rows != null)
            {
                for (int i = 0; i < Outcome.Rows.Length; i++)
                {
                    var r = Outcome.Rows[i];
                    sb.Append(" | R").Append(r.Row).Append(':')
                      .Append(r.PlayerPower).Append("v").Append(r.EnemyPower)
                      .Append(r.Diff > 0 ? " 赢" : (r.Diff < 0 ? " 输" : " 平"));
                }
                if (Outcome.CompletedUnitIds.Count > 0)
                {
                    sb.Append(" | 装满:").Append(string.Join(",", Outcome.CompletedUnitIds));
                }
            }
            return sb.ToString();
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Explain());
            for (int i = 0; i < Steps.Count; i++)
            {
                sb.Append(i + 1).Append(". ").AppendLine(Steps[i].ToString());
            }
            if (Steps.Count == 0)
            {
                sb.AppendLine("（无需/无法装载）");
            }
            if (UnusedTokenIds.Count > 0)
            {
                sb.Append("剩余: ").AppendLine(string.Join(",", UnusedTokenIds));
            }
            return sb.ToString();
        }
    }
}
