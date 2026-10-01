using System;

namespace Kare.Space.Core
{
    /// <summary>Физические и астрономические константы. Единицы СИ: м, с, кг.</summary>
    public static class Constants
    {
        public const double G = 6.67430e-11;
        /// <summary>Стандартное ускорение свободного падения — для удельного импульса.</summary>
        public const double G0 = 9.80665;
        public const double AU = 149597870700.0;
        public const double Day = 86400.0;
        public const double JulianCentury = 36525.0 * Day;
        /// <summary>Юлианская дата эпохи J2000.0 (2000-01-01 12:00 TT). Игровое время отсчитывается от неё.</summary>
        public const double JD_J2000 = 2451545.0;
        /// <summary>Наклон эклиптики к экватору на J2000.</summary>
        public const double Obliquity = 23.4392911 * Deg2Rad;
        public const double Deg2Rad = Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / Math.PI;
        public const double TwoPi = Math.PI * 2;
        /// <summary>Солнечная постоянная на 1 а.е., Вт/м², и освещённость, лк — для света и нагрева.</summary>
        public const double SolarConstant = 1361.0;
        public const double SolarIlluminance1AU = 128000.0;
        public const double SpeedOfLight = 299792458.0;
    }

    public static class MathD
    {
        public static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
        public static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        public static double Lerp(double a, double b, double t) => a + (b - a) * t;
        public static double InverseLerp(double a, double b, double v) => Math.Abs(b - a) < 1e-300 ? 0 : (v - a) / (b - a);

        /// <summary>Угол в диапазон [0, 2π).</summary>
        public static double Wrap2Pi(double a)
        {
            a %= Constants.TwoPi;
            return a < 0 ? a + Constants.TwoPi : a;
        }

        /// <summary>Угол в диапазон (−π, π].</summary>
        public static double WrapPi(double a)
        {
            a = Wrap2Pi(a);
            return a > Math.PI ? a - Constants.TwoPi : a;
        }

        public static double Smoothstep(double e0, double e1, double x)
        {
            double t = Clamp01((x - e0) / (e1 - e0));
            return t * t * (3 - 2 * t);
        }

        /// <summary>Кусочно-линейная интерполяция по таблице (xs возрастают).</summary>
        public static double Interp(double[] xs, double[] ys, double x)
        {
            if (x <= xs[0]) return ys[0];
            int n = xs.Length;
            if (x >= xs[n - 1]) return ys[n - 1];
            int i = 1;
            while (xs[i] < x) i++;
            double t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
            return ys[i - 1] + (ys[i] - ys[i - 1]) * t;
        }
    }
}
