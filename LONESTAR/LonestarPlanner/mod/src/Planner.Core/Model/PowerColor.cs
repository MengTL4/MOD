using System;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 对应游戏里的 PowerColor 枚举，顺序必须一致：
    /// 槽位判定用的是 (int)token.GetHighestColor() &gt;= slot.rare，
    /// 所以这里的数值大小就是"颜色强度"。
    /// </summary>
    public enum PowerColor
    {
        None = 0,
        White = 1,
        Blue = 2,
        Purple = 3,
    }

    public static class PowerColorExtensions
    {
        public static string ShortName(this PowerColor color)
        {
            switch (color)
            {
                case PowerColor.Purple: return "紫";
                case PowerColor.Blue: return "蓝";
                case PowerColor.White: return "白";
                default: return "无";
            }
        }

        public static bool Satisfies(this PowerColor tokenColor, PowerColor slotRequirement)
        {
            return (int)tokenColor >= (int)slotRequirement;
        }
    }
}
