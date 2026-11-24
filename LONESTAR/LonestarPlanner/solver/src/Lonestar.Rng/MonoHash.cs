namespace Lonestar.Rng
{
    /// <summary>
    /// Bit-exact port of the game's Mono <c>string.GetHashCode()</c>
    /// (<c>String.GetLegacyNonRandomizedHashCode</c> in LONESTAR_Data/Managed/mscorlib.dll).
    /// The original walks a NUL-terminated buffer, so an embedded '\0' ends the hash early.
    /// </summary>
    public static class MonoHash
    {
        public static int Hash(string s)
        {
            unchecked
            {
                int h1 = 5381;
                int h2 = h1;
                int i = 0;
                while (true)
                {
                    int c = i < s.Length ? s[i] : 0;
                    if (c == 0)
                    {
                        break;
                    }
                    h1 = ((h1 << 5) + h1) ^ c;
                    c = i + 1 < s.Length ? s[i + 1] : 0;
                    if (c == 0)
                    {
                        break;
                    }
                    h2 = ((h2 << 5) + h2) ^ c;
                    i += 2;
                }
                return h1 + h2 * 1566083941;
            }
        }
    }
}
