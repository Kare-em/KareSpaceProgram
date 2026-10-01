using System;

namespace Kare.Space.Core
{
    /// <summary>Решение уравнения Кеплера для эллипса и гиперболы. Ньютон с запасным делением пополам.</summary>
    public static class KeplerSolver
    {
        /// <summary>Эксцентрическая аномалия E из средней M (эллипс, e &lt; 1).</summary>
        public static double SolveElliptic(double meanAnomaly, double e)
        {
            double m = MathD.WrapPi(meanAnomaly);
            // Стартовое приближение: для больших e лучше начинать с ±π.
            double ea = e < 0.8 ? m + e * Math.Sin(m) : (m >= 0 ? Math.PI : -Math.PI);
            for (int i = 0; i < 50; i++)
            {
                double f = ea - e * Math.Sin(ea) - m;
                double fp = 1 - e * Math.Cos(ea);
                double d = f / fp;
                ea -= d;
                if (Math.Abs(d) < 1e-14) return ea;
            }
            // Ньютон не сошёлся (e → 1): делим пополам, функция монотонна на [−π, π].
            double lo = -Math.PI, hi = Math.PI;
            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (mid - e * Math.Sin(mid) - m > 0) hi = mid;
                else lo = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>Гиперболическая аномалия H из средней M (e &gt; 1): e·sh H − H = M.</summary>
        public static double SolveHyperbolic(double meanAnomaly, double e)
        {
            double m = meanAnomaly;
            double h = Math.Abs(m) < 6 * e
                ? Asinh(m / e)
                : Math.Sign(m) * Math.Log(2 * Math.Abs(m) / e + 1.8);
            for (int i = 0; i < 100; i++)
            {
                double f = e * Math.Sinh(h) - h - m;
                double fp = e * Math.Cosh(h) - 1;
                double d = f / fp;
                // Ограничиваем шаг: при плохом старте Ньютон на sh улетает.
                if (d > 1) d = 1;
                else if (d < -1) d = -1;
                h -= d;
                if (Math.Abs(d) < 1e-14 * Math.Max(1, Math.Abs(h))) return h;
            }
            return h;
        }

        public static double Asinh(double x) => Math.Log(x + Math.Sqrt(x * x + 1));
        public static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));

        /// <summary>Истинная аномалия → средняя (эллипс или гипербола).</summary>
        public static double TrueToMean(double trueAnomaly, double e)
        {
            if (e < 1)
            {
                double ea = Math.Atan2(Math.Sqrt(1 - e * e) * Math.Sin(trueAnomaly), e + Math.Cos(trueAnomaly));
                return ea - e * Math.Sin(ea);
            }
            double sh = Math.Sqrt(e * e - 1) * Math.Sin(trueAnomaly) / (1 + e * Math.Cos(trueAnomaly));
            double h = Asinh(sh);
            return e * Math.Sinh(h) - h;
        }

        /// <summary>Средняя аномалия → истинная.</summary>
        public static double MeanToTrue(double meanAnomaly, double e)
        {
            if (e < 1)
            {
                double ea = SolveElliptic(meanAnomaly, e);
                return Math.Atan2(Math.Sqrt(1 - e * e) * Math.Sin(ea), Math.Cos(ea) - e);
            }
            double h = SolveHyperbolic(meanAnomaly, e);
            return Math.Atan2(Math.Sqrt(e * e - 1) * Math.Sinh(h), e - Math.Cosh(h));
        }
    }
}
