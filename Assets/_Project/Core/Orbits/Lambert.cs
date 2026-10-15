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
        const int Iterations = 120;
        /// <summary>
        /// Удвоений нижней границы z: −4π²·2¹² ≈ −1,6·10⁵ — дальше cosh(√−z) в C(z) переполняется (√−z > 710).
        /// Пара: Iterations бисекции должно хватать на ширину отрезка до 1,6·10⁵ (2⁻¹²⁰ — с запасом).
        /// </summary>
        const int LowerBoundDoublings = 12;

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

            // Время перелёта монотонно растёт с z: z < 0 — гипербола, верхняя граница — полный виток (4π²).
            // Нижнюю границу только РАСШИРЯЕМ вниз, пока гипербола на ней ещё медленнее нужного. Раньше её «чинили»
            // делением пополам к hi при Y(lo) < 0 — с −4π² она прыгала сразу в 0, гиперболы отсекались, и решатель
            // молча возвращал параболу: у «Вояджера» Юпитер → Сатурн e = 1,000 и промах 100 млн км (замер 15.10.2026).
            // Участок Y < 0 (нефизичный) бисекция обходит сама: там lo = z.
            double lo = -4 * Math.PI * Math.PI, hi = 4 * Math.PI * Math.PI;
            for (int k = 0; k < LowerBoundDoublings; k++)
            {
                double yl = Y(lo, r1n, r2n, A);
                if (yl < 0 || TimeOfFlight(lo, yl, A, sqrtMu) < tof) break;
                lo *= 2;
            }
            double z = 0, y = 0;
            for (int i = 0; i < Iterations; i++)
            {
                z = (lo + hi) / 2;
                y = Y(z, r1n, r2n, A);
                if (y < 0) { lo = z; continue; }
                if (TimeOfFlight(z, y, A, sqrtMu) < tof) lo = z; else hi = z;
            }
            if (y <= 0) return false;
            double f = 1 - y / r1n, g = A * Math.Sqrt(y / mu), gdot = 1 - y / r2n;
            if (Math.Abs(g) < 1e-9) return false;
            v1 = (r2 - r1 * f) / g;
            v2 = (r2 * gdot - r1) / g;
            return !double.IsNaN(v1.x) && !double.IsNaN(v2.x);
        }

        static double TimeOfFlight(double z, double y, double A, double sqrtMu) =>
            (Math.Pow(y / C(z), 1.5) * S(z) + A * Math.Sqrt(y)) / sqrtMu;

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
