using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 把 BattleState 写成 JSON 快照。
    ///
    /// 用途：游戏内按热键把真实局面导出到磁盘，再丢给离线测试/调参流程重放，
    /// 这样"游戏里遇到的具体难题"可以变成一条固定的回归用例。
    /// 只做写出，不做解析 —— 离线端用 System.Text.Json 读即可。
    /// </summary>
    public static class StateJson
    {
        public const int SchemaVersion = 1;

        public static string Write(BattleState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var sb = new StringBuilder(2048);
            sb.Append("{\n");
            sb.Append("  \"schema\": ").Append(SchemaVersion).Append(",\n");
            sb.Append("  \"tag\": ").Append(Str(state.Tag)).Append(",\n");
            sb.Append("  \"rowCount\": ").Append(state.RowCount).Append(",\n");

            sb.Append("  \"enemyRowPower\": ");
            WriteIntArray(sb, state.EnemyRowPower);
            sb.Append(",\n");

            sb.Append("  \"playerRowFlatBonus\": ");
            WriteIntArray(sb, state.PlayerRowFlatBonus);
            sb.Append(",\n");

            sb.Append("  \"units\": [\n");
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                sb.Append("    { \"id\": ").Append(u.Id)
                  .Append(", \"name\": ").Append(Str(u.Name))
                  .Append(", \"row\": ").Append(u.Row)
                  .Append(", \"col\": ").Append(u.Col)
                  .Append(", \"fullLoadValue\": ").Append(Num(u.FullLoadValue))
                  .Append(", \"slots\": [");

                for (int s = 0; s < u.Slots.Count; s++)
                {
                    var slot = u.Slots[s];
                    if (s > 0) sb.Append(", ");
                    sb.Append("{ \"index\": ").Append(slot.Index)
                      .Append(", \"req\": ").Append((int)slot.Requirement)
                      .Append(", \"disturbed\": ").Append(slot.Disturbed ? "true" : "false");
                    if (slot.Loaded != null)
                    {
                        sb.Append(", \"loaded\": { \"power\": ").Append(slot.Loaded.Power)
                          .Append(", \"color\": ").Append((int)slot.Loaded.Color)
                          .Append(", \"id\": ").Append(slot.Loaded.Id).Append(" }");
                    }
                    sb.Append(" }");
                }

                sb.Append("] }");
                if (i < state.Units.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ],\n");

            sb.Append("  \"hand\": [\n");
            for (int i = 0; i < state.Hand.Count; i++)
            {
                var t = state.Hand[i];
                sb.Append("    { \"id\": ").Append(t.Id)
                  .Append(", \"power\": ").Append(t.Power)
                  .Append(", \"color\": ").Append((int)t.Color)
                  .Append(" }");
                if (i < state.Hand.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static void WriteIntArray(StringBuilder sb, int[] values)
        {
            sb.Append('[');
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(values[i]);
                }
            }
            sb.Append(']');
        }

        private static string Num(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Str(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            var sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
