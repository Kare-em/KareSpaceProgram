using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Вектор двойной точности. Вся симуляция ведётся в double: float даёт шаг ~0,5 м на радиусе
    /// Земли и ~10 км на 1 а.е. — для масштаба 1:1 этого мало (см. GDD §2).
    /// </summary>
    [Serializable]
    public struct Vector3d : IEquatable<Vector3d>
    {
        public double x, y, z;

        public Vector3d(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static readonly Vector3d zero = new Vector3d(0, 0, 0);
        public static readonly Vector3d one = new Vector3d(1, 1, 1);
        public static readonly Vector3d right = new Vector3d(1, 0, 0);
        public static readonly Vector3d up = new Vector3d(0, 1, 0);
        public static readonly Vector3d forward = new Vector3d(0, 0, 1);

        public double sqrMagnitude => x * x + y * y + z * z;
        public double magnitude => Math.Sqrt(x * x + y * y + z * z);

        public Vector3d normalized
        {
            get
            {
                double m = magnitude;
                return m > 1e-300 ? new Vector3d(x / m, y / m, z / m) : zero;
            }
        }

        /// <summary>
        /// Перестановка y↔z: переход между физической системой (правая, эклиптика J2000, z — северный
        /// полюс эклиптики) и системой Unity (левая, y — вверх). Операция сама себе обратна.
        /// </summary>
        public Vector3d SwapYZ => new Vector3d(x, z, y);

        public bool IsFinite => !(double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z) ||
                                  double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z));

        public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3d operator -(Vector3d a) => new Vector3d(-a.x, -a.y, -a.z);
        public static Vector3d operator *(Vector3d a, double d) => new Vector3d(a.x * d, a.y * d, a.z * d);
        public static Vector3d operator *(double d, Vector3d a) => new Vector3d(a.x * d, a.y * d, a.z * d);
        public static Vector3d operator /(Vector3d a, double d) => new Vector3d(a.x / d, a.y / d, a.z / d);
        public static bool operator ==(Vector3d a, Vector3d b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public static bool operator !=(Vector3d a, Vector3d b) => !(a == b);

        public static double Dot(Vector3d a, Vector3d b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3d Cross(Vector3d a, Vector3d b) =>
            new Vector3d(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

        public static double Distance(Vector3d a, Vector3d b) => (a - b).magnitude;

        /// <summary>Угол между векторами в радианах. Через atan2 — точен и у 0, и у π, в отличие от acos.</summary>
        public static double Angle(Vector3d a, Vector3d b) => Math.Atan2(Cross(a, b).magnitude, Dot(a, b));

        public static Vector3d Lerp(Vector3d a, Vector3d b, double t) => a + (b - a) * t;

        public static Vector3d Project(Vector3d v, Vector3d onNormal)
        {
            double sq = onNormal.sqrMagnitude;
            return sq < 1e-300 ? zero : onNormal * (Dot(v, onNormal) / sq);
        }

        public static Vector3d ProjectOnPlane(Vector3d v, Vector3d planeNormal) => v - Project(v, planeNormal);

        /// <summary>Любой единичный вектор, перпендикулярный данному.</summary>
        public static Vector3d AnyPerpendicular(Vector3d v)
        {
            var n = v.normalized;
            var a = Math.Abs(n.x) < 0.9 ? right : up;
            return Cross(n, a).normalized;
        }

        public bool Equals(Vector3d other) => this == other;
        public override bool Equals(object obj) => obj is Vector3d v && this == v;
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
        public override string ToString() => $"({x:G6}, {y:G6}, {z:G6})";
        public string ToString(string format) => $"({x.ToString(format)}, {y.ToString(format)}, {z.ToString(format)})";
    }
}
