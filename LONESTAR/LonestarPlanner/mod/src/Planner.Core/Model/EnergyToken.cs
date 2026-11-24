using System;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 手上的一枚能量 token（游戏里的 EnergyData）。
    /// 游戏规则：一个 token 整块进入一个槽位，power 不做拆分；
    /// 颜色是一个集合，判定只看"最高颜色"。
    /// </summary>
    public sealed class EnergyToken
    {
        /// <summary>手牌内的稳定标识（0..n-1），用于规划结果回指。</summary>
        public int Id;

        /// <summary>能量数值 1..9（游戏 Define.powerLimit = 9）。</summary>
        public int Power;

        /// <summary>token 的最高颜色。</summary>
        public PowerColor Color;

        public EnergyToken()
        {
        }

        public EnergyToken(int id, int power, PowerColor color)
        {
            Id = id;
            Power = power;
            Color = color;
        }

        public EnergyToken Clone()
        {
            return new EnergyToken(Id, Power, Color);
        }

        public override string ToString()
        {
            return "#" + Id + " " + Color.ShortName() + Power;
        }
    }
}
