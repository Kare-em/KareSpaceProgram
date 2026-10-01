using System;

namespace Kare.Space.Core
{
    /// <summary>Модель атмосферы: давление, плотность, температура по высоте над уровнем «моря». См. GDD §4.</summary>
    public abstract class Atmosphere
    {
        public const double R = 8.31446261815324;

        /// <summary>Граница атмосферы, м. Выше — вакуум, и корабль можно класть на рельсы (GDD §3).</summary>
        public double Top;
        public double SeaLevelPressure;
        public double MolarMass;
        public double Gamma = 1.4;

        public abstract void Sample(double altitude, out double pressure, out double density, out double temperature);

        public double SpeedOfSound(double temperature) => Math.Sqrt(Gamma * R * temperature / MolarMass);

        public double Pressure(double altitude)
        {
            Sample(altitude, out double p, out _, out _);
            return p;
        }
    }

    /// <summary>
    /// US Standard Atmosphere 1976: семь слоёв до 86 км по аналитическим формулам, выше — таблица
    /// (логарифмическая интерполяция) до границы 140 км.
    /// </summary>
    public sealed class StandardAtmosphere1976 : Atmosphere
    {
        const double G0 = 9.80665;
        const double M = 0.0289644;
        const double EarthRadiusGeopotential = 6356766.0;

        // Слои по геопотенциальной высоте: основание, м; температура, К; градиент, К/м; давление, Па.
        static readonly double[] Hb = { 0, 11000, 20000, 32000, 47000, 51000, 71000 };
        static readonly double[] Tb = { 288.15, 216.65, 216.65, 228.65, 270.65, 270.65, 214.65 };
        static readonly double[] Lb = { -0.0065, 0, 0.001, 0.0028, 0, -0.0028, -0.002 };
        static readonly double[] Pb = { 101325, 22632.06, 5474.889, 868.0187, 110.9063, 66.93887, 3.956420 };

        // Термосфера, геометрическая высота: км → плотность, давление, температура.
        static readonly double[] UpZ = { 86, 90, 100, 110, 120, 130, 140 };
        static readonly double[] UpRho = { 6.958e-6, 3.416e-6, 5.604e-7, 9.708e-8, 2.222e-8, 8.152e-9, 3.831e-9 };
        static readonly double[] UpP = { 0.3734, 0.1836, 3.201e-2, 7.104e-3, 2.538e-3, 1.251e-3, 7.203e-4 };
        static readonly double[] UpT = { 186.87, 186.87, 195.08, 240.0, 360.0, 469.27, 559.63 };
        static readonly double[] UpLogRho, UpLogP;

        static StandardAtmosphere1976()
        {
            UpLogRho = Array.ConvertAll(UpRho, Math.Log);
            UpLogP = Array.ConvertAll(UpP, Math.Log);
        }

        public StandardAtmosphere1976()
        {
            Top = 140000;
            SeaLevelPressure = 101325;
            MolarMass = M;
            Gamma = 1.4;
        }

        public override void Sample(double z, out double pressure, out double density, out double temperature)
        {
            if (z >= Top)
            {
                pressure = density = 0;
                temperature = UpT[UpT.Length - 1];
                return;
            }
            if (z < 0) z = 0;
            if (z >= 86000)
            {
                double km = z / 1000;
                pressure = Math.Exp(MathD.Interp(UpZ, UpLogP, km));
                density = Math.Exp(MathD.Interp(UpZ, UpLogRho, km));
                temperature = MathD.Interp(UpZ, UpT, km);
                return;
            }
            double h = EarthRadiusGeopotential * z / (EarthRadiusGeopotential + z);
            int i = Hb.Length - 1;
            while (i > 0 && h < Hb[i]) i--;
            double dh = h - Hb[i];
            if (Lb[i] == 0)
            {
                temperature = Tb[i];
                pressure = Pb[i] * Math.Exp(-G0 * M * dh / (R * Tb[i]));
            }
            else
            {
                temperature = Tb[i] + Lb[i] * dh;
                pressure = Pb[i] * Math.Pow(Tb[i] / temperature, G0 * M / (R * Lb[i]));
            }
            density = pressure * M / (R * temperature);
        }
    }

    /// <summary>
    /// Экспоненциальная атмосфера с линейным падением температуры до минимума. Для Марса, Венеры,
    /// Титана и газовых гигантов: точнее для игры не нужно, а данных у этих тел всё равно меньше.
    /// </summary>
    public sealed class ExponentialAtmosphere : Atmosphere
    {
        public double ScaleHeight;
        public double SurfaceTemperature;
        public double LapseRate;
        public double MinTemperature;

        public ExponentialAtmosphere(double p0, double scaleHeight, double t0, double lapse, double tMin,
            double molarMass, double gamma, double top)
        {
            SeaLevelPressure = p0;
            ScaleHeight = scaleHeight;
            SurfaceTemperature = t0;
            LapseRate = lapse;
            MinTemperature = tMin;
            MolarMass = molarMass;
            Gamma = gamma;
            Top = top;
        }

        public override void Sample(double z, out double pressure, out double density, out double temperature)
        {
            if (z < 0) z = 0;
            temperature = Math.Max(MinTemperature, SurfaceTemperature - LapseRate * z);
            if (z >= Top)
            {
                pressure = density = 0;
                return;
            }
            pressure = SeaLevelPressure * Math.Exp(-z / ScaleHeight);
            density = pressure * MolarMass / (R * temperature);
        }
    }
}
