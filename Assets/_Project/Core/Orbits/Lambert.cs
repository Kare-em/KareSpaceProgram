using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Задача Ламберта (GDD §6.6, §6.11): орбита, проходящая через r1 и r2 за время tof. Универсальная переменная z
    /// (алгоритм Кертиса 5.2) с бисекцией — медленнее Ньютона, зато не расходится на почти полном витке.
    /// Один виток, направление движения — по нормали prograde (у сближения это нормаль орбиты догоняющего).
    /// </summary>
    public static class Lambert
    {
        const int Iterations = 80;

        public static bool Solve(Vector3d r1, Vector3d r2, double tof, double mu, Vector3d prograde,
            out Vector3d v1, out Vector3d v2)
        {
            v1 = v2 = Vector3d.zero;
            double r1n = r1.magnitude, r2n = r2.magnitude;
            if (tof <= 0 || r1n <= 0 || r2n <= 0) return false;
            double cosT = MathD.Clamp(Vector3d.Dot(r1, r2) / (r1n * r2n), -1, 1);
            double theta = Math.Acos(cosT);
            if (Vector3d.Dot(Vector3d.Cross(r1, r2), prograde) < 0) theta = Constants.TwoPi - theta;
            if (1 - Math.Cos(theta) < 1e-12) return false; // вырожденный случай: точки на одной прямой с фокусом
            double A = Math.Sin(theta) * Math.Sqrt(r1n * r2n / (1 - Math.Cos(theta)));
            double sqrtMu = Math.Sqrt(mu);

            // Время перелёта монотонно растёт с z: нижняя граница — гипербола, верхняя — полный виток (4π²).
            double lo = -4 * Math.PI * Math.PI, hi = 4 * Math.PI * Math.PI;
            for (int k = 0; k < 60 && Y(lo, r1n, r2n, A) < 0; k++) lo = (lo + hi) / 2;
            double z = 0, y = 0;
            for (int i = 0; i < Iterations; i++)
            {
                z = (lo + hi) / 2;
                y = Y(z, r1n, r2n, A);
                if (y < 0) { lo = z; continue; }
                double c = C(z), s = S(z);
                double t = (Math.Pow(y / c, 1.5) * s + A * Math.Sqrt(y)) / sqrtMu;
                if (t < tof) lo = z; else hi = z;
            }
            if (y <= 0) return false;
            double f = 1 - y / r1n, g = A * Math.Sqrt(y / mu), gdot = 1 - y / r2n;
            if (Math.Abs(g) < 1e-9) return false;
            v1 = (r2 - r1 * f) / g;
            v2 = (r2 * gdot - r1) / g;
            return !double.IsNaN(v1.x) && !double.IsNaN(v2.x);
        }

        static double Y(double z, double r1, double r2, double A) => r1 + r2 + A * (z * S(z) - 1) / Math.Sqrt(C(z));

        /// <summary>Функции Штумпфа; у нуля — ряд, иначе деление на малое z теряет точность.</summary>
        static double C(double z)
        {
            if (z > 1e-6) return (1 - Math.Cos(Math.Sqrt(z))) / z;
            if (z < -1e-6) return (Math.Cosh(Math.Sqrt(-z)) - 1) / -z;
            return 0.5 - z / 24;
        }

        static double S(double z)
        {
            if (z > 1e-6)
            {
                double sz = Math.Sqrt(z);
                return (sz - Math.Sin(sz)) / (sz * sz * sz);
            }
            if (z < -1e-6)
            {
                double sz = Math.Sqrt(-z);
                return (Math.Sinh(sz) - sz) / (sz * sz * sz);
            }
            return 1.0 / 6 - z / 120;
        }
    }
}
