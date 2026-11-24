using System;
using System.Globalization;

namespace UnityEngine
{
    public partial struct Vector2
    {
        public float x;
        public float y;

        public Vector2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vector2 zero => new Vector2(0f, 0f);
        public static Vector2 one => new Vector2(1f, 1f);
        public static Vector2 up => new Vector2(0f, 1f);
        public static Vector2 down => new Vector2(0f, -1f);
        public static Vector2 left => new Vector2(-1f, 0f);
        public static Vector2 right => new Vector2(1f, 0f);

        public float magnitude => (float)Math.Sqrt(x * x + y * y);

        public static Vector2 Lerp(Vector2 a, Vector2 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
        }

        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator -(Vector2 a) => new Vector2(-a.x, -a.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator *(float d, Vector2 a) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.x / d, a.y / d);

        public static bool operator ==(Vector2 a, Vector2 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return dx * dx + dy * dy < 9.99999944E-11f;
        }

        public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
        public static implicit operator Vector2(Vector3 v) => new Vector2(v.x, v.y);
        public static implicit operator Vector3(Vector2 v) => new Vector3(v.x, v.y, 0f);

        public override bool Equals(object o) => o is Vector2 v && x.Equals(v.x) && y.Equals(v.y);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:F2}, {1:F2})", x, y);
    }

    public partial struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public Vector3(float x, float y)
        {
            this.x = x;
            this.y = y;
            z = 0f;
        }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 down => new Vector3(0f, -1f, 0f);
        public static Vector3 left => new Vector3(-1f, 0f, 0f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public static Vector3 back => new Vector3(0f, 0f, -1f);

        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized => Normalize(this);

        public static Vector3 Normalize(Vector3 v)
        {
            float m = v.magnitude;
            return m > 1E-05f ? v / m : zero;
        }

        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Scale(Vector3 a, Vector3 b) => new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);

        public static bool operator ==(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz < 9.99999944E-11f;
        }

        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);

        public override bool Equals(object o) => o is Vector3 v && x.Equals(v.x) && y.Equals(v.y) && z.Equals(v.z);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:F2}, {1:F2}, {2:F2})", x, y, z);
    }

    public partial struct Vector4
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public Vector4(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }
    }

    public partial struct Vector2Int : IEquatable<Vector2Int>
    {
        private int m_X;
        private int m_Y;

        public Vector2Int(int x, int y)
        {
            m_X = x;
            m_Y = y;
        }

        public int x
        {
            get => m_X;
            set => m_X = value;
        }

        public int y
        {
            get => m_Y;
            set => m_Y = value;
        }

        public static Vector2Int zero => new Vector2Int(0, 0);
        public static Vector2Int one => new Vector2Int(1, 1);
        public static Vector2Int up => new Vector2Int(0, 1);
        public static Vector2Int down => new Vector2Int(0, -1);
        public static Vector2Int left => new Vector2Int(-1, 0);
        public static Vector2Int right => new Vector2Int(1, 0);

        public static Vector2Int operator +(Vector2Int a, Vector2Int b) => new Vector2Int(a.m_X + b.m_X, a.m_Y + b.m_Y);
        public static Vector2Int operator -(Vector2Int a, Vector2Int b) => new Vector2Int(a.m_X - b.m_X, a.m_Y - b.m_Y);
        public static Vector2Int operator -(Vector2Int a) => new Vector2Int(-a.m_X, -a.m_Y);
        public static Vector2Int operator *(Vector2Int a, int d) => new Vector2Int(a.m_X * d, a.m_Y * d);
        public static Vector2Int operator *(int d, Vector2Int a) => new Vector2Int(a.m_X * d, a.m_Y * d);
        public static bool operator ==(Vector2Int a, Vector2Int b) => a.m_X == b.m_X && a.m_Y == b.m_Y;
        public static bool operator !=(Vector2Int a, Vector2Int b) => !(a == b);
        public static implicit operator Vector2(Vector2Int v) => new Vector2(v.m_X, v.m_Y);

        public bool Equals(Vector2Int other) => this == other;
        public override bool Equals(object o) => o is Vector2Int v && this == v;
        public override int GetHashCode() => m_X.GetHashCode() ^ (m_Y.GetHashCode() << 2);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0}, {1})", m_X, m_Y);
    }

    public partial struct Vector3Int
    {
        public int x { get; set; }
        public int y { get; set; }
        public int z { get; set; }
    }

    public partial struct Quaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);

        /// <summary>Stored Euler angles (degrees); the shim never needs true rotation math.</summary>
        public Vector3 eulerAngles => new Vector3(x, y, z);

        public static Quaternion Euler(float x, float y, float z) => new Quaternion(x, y, z, 1f);

        public static Quaternion Euler(Vector3 e) => new Quaternion(e.x, e.y, e.z, 1f);

        public static Vector3 operator *(Quaternion rotation, Vector3 point) => point;
    }

    public partial struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;

        public Color(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public Color(float r, float g, float b)
            : this(r, g, b, 1f)
        {
        }

        public static Color white => new Color(1f, 1f, 1f, 1f);
        public static Color black => new Color(0f, 0f, 0f, 1f);
        public static Color gray => new Color(0.5f, 0.5f, 0.5f, 1f);
        public static Color clear => new Color(0f, 0f, 0f, 0f);

        public static Color Lerp(Color a, Color b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);
        }

        public static Color HSVToRGB(float h, float s, float v) => new Color(v, v, v, 1f);

        public static Color operator +(Color a, Color b) => new Color(a.r + b.r, a.g + b.g, a.b + b.b, a.a + b.a);
        public static Color operator -(Color a, Color b) => new Color(a.r - b.r, a.g - b.g, a.b - b.b, a.a - b.a);
        public static Color operator *(Color a, float d) => new Color(a.r * d, a.g * d, a.b * d, a.a * d);
        public static bool operator ==(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
        public static bool operator !=(Color a, Color b) => !(a == b);
        public static implicit operator Vector4(Color c) => new Vector4(c.r, c.g, c.b, c.a);
        public static implicit operator Color(Vector4 v) => new Color(v.x, v.y, v.z, v.w);

        public override bool Equals(object o) => o is Color c && this == c;
        public override int GetHashCode() => r.GetHashCode() ^ g.GetHashCode() << 2 ^ b.GetHashCode() >> 2 ^ a.GetHashCode() >> 1;
    }

    public partial struct Mathf
    {
        public const float PI = 3.14159274f;
        public const float Infinity = float.PositiveInfinity;
        public const float NegativeInfinity = float.NegativeInfinity;
        public static readonly float Epsilon = float.Epsilon;

        public static int Abs(int v) => Math.Abs(v);
        public static float Abs(float v) => Math.Abs(v);
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Pow(float f, float p) => (float)Math.Pow(f, p);
        public static float Exp(float p) => (float)Math.Exp(p);
        public static float Log(float f) => (float)Math.Log(f);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Sin(float f) => (float)Math.Sin(f);
        public static float Cos(float f) => (float)Math.Cos(f);
        public static float Ceil(float f) => (float)Math.Ceiling(f);
        public static float Floor(float f) => (float)Math.Floor(f);
        public static float Round(float f) => (float)Math.Round(f);
        public static int CeilToInt(float f) => (int)Math.Ceiling(f);
        public static int FloorToInt(float f) => (int)Math.Floor(f);
        public static int RoundToInt(float f) => (int)Math.Round(f);
        public static float Sign(float f) => f >= 0f ? 1f : -1f;
        public static int Min(int a, int b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;

        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

        public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

        public static bool Approximately(float a, float b) =>
            Math.Abs(b - a) < Math.Max(1E-06f * Math.Max(Math.Abs(a), Math.Abs(b)), float.Epsilon * 8f);
    }

    /// <summary>UnityEngine.Random backed by the bit-exact port verified against the game (phase 0).</summary>
    public partial class Random
    {
        private static readonly Lonestar.Rng.UnityRandom rng = new Lonestar.Rng.UnityRandom((int)DateTime.Now.Ticks);

        public static void InitState(int seed) => rng.InitState(seed);

        public static int Range(int minInclusive, int maxExclusive) => rng.Range(minInclusive, maxExclusive);

        public static float Range(float minInclusive, float maxInclusive) => rng.Range(minInclusive, maxInclusive);

        public static float value => rng.Value();
    }
}
