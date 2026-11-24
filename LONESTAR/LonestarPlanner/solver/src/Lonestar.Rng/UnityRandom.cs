namespace Lonestar.Rng
{
    /// <summary>How a raw 32-bit draw becomes a float in [0, 1]. Pinned down by the in-game self-test.</summary>
    public enum FloatMode
    {
        /// <summary>(x &amp; 0x7FFFFF) / 8388607f</summary>
        Mantissa23Div,
        /// <summary>(x &amp; 0x7FFFFF) * (1f / 8388607f)</summary>
        Mantissa23Mul,
        /// <summary>1 - (x &amp; 0x7FFFFF) / 8388607f</summary>
        Mantissa23DivInv,
    }

    /// <summary>How Range(float min, float max) interpolates. Pinned down by the in-game self-test.</summary>
    public enum LerpMode
    {
        /// <summary>min * t + (1 - t) * max</summary>
        Reversed,
        /// <summary>min + (max - min) * t</summary>
        Forward,
        /// <summary>min * (1 - t) + max * t</summary>
        Weighted,
    }

    /// <summary>
    /// Re-implementation of UnityEngine.Random (Xorshift128). Unity's version is native code, so
    /// the float conversions are configurable until the self-test log identifies the exact one.
    /// </summary>
    public sealed class UnityRandom
    {
        public uint S0, S1, S2, S3;

        public FloatMode FloatMode = FloatMode.Mantissa23Div;
        public LerpMode LerpMode = LerpMode.Reversed;

        public UnityRandom(int seed)
        {
            InitState(seed);
        }

        public void InitState(int seed)
        {
            unchecked
            {
                S0 = (uint)seed;
                S1 = S0 * 1812433253u + 1u;
                S2 = S1 * 1812433253u + 1u;
                S3 = S2 * 1812433253u + 1u;
            }
        }

        public uint NextUInt()
        {
            unchecked
            {
                uint t = S0 ^ (S0 << 11);
                S0 = S1;
                S1 = S2;
                S2 = S3;
                S3 = S3 ^ (S3 >> 19) ^ t ^ (t >> 8);
                return S3;
            }
        }

        /// <summary>Equivalent of Random.value.</summary>
        public float Value() => ToFloat(NextUInt());

        public float ToFloat(uint x)
        {
            float m = x & 0x7FFFFFu;
            switch (FloatMode)
            {
                case FloatMode.Mantissa23Mul:
                    return m * (1f / 8388607f);
                case FloatMode.Mantissa23DivInv:
                    return 1f - m / 8388607f;
                default:
                    return m / 8388607f;
            }
        }

        /// <summary>Equivalent of Random.Range(float, float); both ends inclusive.</summary>
        public float Range(float min, float max)
        {
            float t = ToFloat(NextUInt());
            switch (LerpMode)
            {
                case LerpMode.Forward:
                    return min + (max - min) * t;
                case LerpMode.Weighted:
                    return min * (1f - t) + max * t;
                default:
                    return min * t + (1f - t) * max;
            }
        }

        /// <summary>Equivalent of Random.Range(int, int); max exclusive, arguments may be reversed.</summary>
        public int Range(int min, int max)
        {
            unchecked
            {
                if (min < max)
                {
                    return min + (int)(NextUInt() % (uint)(max - min));
                }
                if (min > max)
                {
                    return min - (int)(NextUInt() % (uint)(min - max));
                }
                return min;
            }
        }
    }
}
