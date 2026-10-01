using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Реальная Солнечная система 1:1 (GDD §2). Список тел упорядочен «родитель раньше детей» —
    /// на этом держится <see cref="Update"/>.
    /// </summary>
    public sealed class SolarSystem
    {
        public readonly List<CelestialBody> Bodies = new List<CelestialBody>();
        readonly Dictionary<string, CelestialBody> byId = new Dictionary<string, CelestialBody>();

        public CelestialBody Sun => Bodies[0];
        public CelestialBody Get(string id) => byId.TryGetValue(id, out var b) ? b : null;
        public double Time { get; private set; } = double.NaN;

        /// <summary>Пересчитать положения, скорости и ориентации всех тел на момент t.</summary>
        public void Update(double t)
        {
            Time = t;
            foreach (var b in Bodies)
            {
                b.StateTime = t;
                if (b.Parent == null)
                {
                    b.Position = b.Velocity = b.LocalPosition = b.LocalVelocity = Vector3d.zero;
                }
                else
                {
                    b.LocalStateAt(t, out var r, out var v);
                    b.LocalPosition = r;
                    b.LocalVelocity = v;
                    b.Position = b.Parent.Position + r;
                    b.Velocity = b.Parent.Velocity + v;
                    b.Orbit = b.OrbitAt(t);
                }
                b.Orientation = b.OrientationAt(t);
                b.AngularVelocity = (b.Orientation * Vector3d.forward) * b.RotationRate;
            }
        }

        CelestialBody Add(CelestialBody b, CelestialBody parent)
        {
            b.Index = Bodies.Count;
            b.Parent = parent;
            parent?.Children.Add(b);
            Bodies.Add(b);
            byId[b.Id] = b;
            return b;
        }

        // ---------------------------------------------------------------- данные

        static CelestialBody Body(string id, string name, double mu, double radiusKm) =>
            new CelestialBody { Id = id, Name = name, Mu = mu, Radius = radiusKm * 1000 };

        static CelestialBody Iau(CelestialBody b, double ra, double raRate, double dec, double decRate, double w0, double wRate)
        {
            b.PoleRa0 = ra;
            b.PoleRaRate = raRate;
            b.PoleDec0 = dec;
            b.PoleDecRate = decRate;
            b.W0 = w0;
            b.WRate = wRate;
            return b;
        }

        static CelestialBody Standish(CelestialBody b, double[] el, double[] rates)
        {
            b.OrbitModel = OrbitModelType.Standish;
            b.Elements = el;
            b.Rates = rates;
            return b;
        }

        /// <summary>Круговой спутник в плоскости экватора родителя, в приливном захвате.</summary>
        static CelestialBody Moonlet(CelestialBody b, double aKm, double phaseDeg, bool retrograde = false)
        {
            b.OrbitModel = OrbitModelType.Circular;
            b.CircularRadius = aKm * 1000;
            b.CircularPhase0 = phaseDeg * Constants.Deg2Rad;
            b.Retrograde = retrograde;
            b.TidallyLocked = true;
            return b;
        }

        static TerrainSettings Ter(double amp, int seed, bool ocean = false, double landBias = 0, double craters = 0) =>
            new TerrainSettings { Amplitude = amp, Seed = seed, Ocean = ocean, LandBias = landBias, Craters = craters };

        static ExponentialAtmosphere Exp(double p0, double h, double t0, double lapse, double tMin, double m, double gamma, double top) =>
            new ExponentialAtmosphere(p0, h, t0, lapse, tMin, m, gamma, top);

        /// <summary>
        /// Собрать систему. Источники: GM и радиусы — NASA/JPL; орбиты планет — JPL Standish (таблица
        /// 1800–2050 гг.); Луна — Шлайтер; полюса и вращение — IAU WGCCRE 2015 (без малых гармоник).
        /// </summary>
        public static SolarSystem CreateReal()
        {
            var s = new SolarSystem();

            var sun = s.Add(Iau(Body("sun", "Солнце", 1.32712440018e20, 695700), 286.13, 0, 63.87, 0, 84.176, 14.1844), null);
            sun.OrbitModel = OrbitModelType.Fixed;
            sun.IsGasGiant = true;

            var mercury = s.Add(Standish(Iau(Body("mercury", "Меркурий", 2.2031868551e13, 2439.7), 281.0103, -0.0328, 61.4155, -0.0049, 329.5988, 6.1385108),
                new[] { 0.38709927, 0.20563593, 7.00497902, 252.25032350, 77.45779628, 48.33076593 },
                new[] { 0.00000037, 0.00001906, -0.00594749, 149472.67411175, 0.16047689, -0.12534081 }), sun);
            mercury.Terrain = Ter(3000, 11, craters: 0.6);

            var venus = s.Add(Standish(Iau(Body("venus", "Венера", 3.24858592e14, 6051.8), 272.76, 0, 67.16, 0, 160.20, -1.4813688),
                new[] { 0.72333566, 0.00677672, 3.39467605, 181.97909950, 131.60246718, 76.67984255 },
                new[] { 0.00000390, -0.00004107, -0.00078890, 58517.81538729, 0.00268329, -0.27769418 }), sun);
            venus.Terrain = Ter(3000, 12);
            venus.Atmosphere = Exp(9.2e6, 15900, 737, 0.0078, 170, 0.04345, 1.29, 250000);

            var earth = s.Add(Standish(Iau(Body("earth", "Земля", 3.986004418e14, 6371.0), 0, -0.641, 90, -0.557, 190.147, 360.9856235),
                new[] { 1.00000261, 0.01671123, -0.00001531, 100.46457166, 102.93768193, 0.0 },
                new[] { 0.00000562, -0.00004392, -0.01294668, 35999.37244981, 0.32327364, 0.0 }), sun);
            earth.Terrain = Ter(5000, 1, ocean: true, landBias: -0.06);
            earth.Atmosphere = new StandardAtmosphere1976();

            var moon = s.Add(Iau(Body("moon", "Луна", 4.9028e12, 1737.4), 269.9949, 0.0031, 66.5392, 0.0130, 38.3213, 13.17635815), earth);
            moon.OrbitModel = OrbitModelType.Schlyter;
            moon.Terrain = Ter(4000, 2, craters: 0.5);
            earth.BarycenterSatellite = moon;

            var mars = s.Add(Standish(Iau(Body("mars", "Марс", 4.282837362e13, 3389.5), 317.269202, -0.10927547, 54.432516, -0.05827105, 176.049863, 350.891982443297),
                new[] { 1.52371034, 0.09339410, 1.84969142, -4.55343205, -23.94362959, 49.55953891 },
                new[] { 0.00001847, 0.00007882, -0.00813131, 19140.30268499, 0.44441088, -0.29257343 }), sun);
            mars.Terrain = Ter(7000, 5, craters: 0.2);
            mars.Atmosphere = Exp(610, 11100, 210, 0.0025, 130, 0.04334, 1.29, 125000);
            s.Add(Moonlet(Body("phobos", "Фобос", 7.11e5, 11.27), 9376, 40), mars).Terrain = Ter(1500, 21, craters: 0.6);
            s.Add(Moonlet(Body("deimos", "Деймос", 9.8e4, 6.2), 23463.2, 200), mars).Terrain = Ter(900, 22, craters: 0.6);

            var jupiter = s.Add(Standish(Iau(Body("jupiter", "Юпитер", 1.26686534e17, 69911), 268.056595, -0.006499, 64.495303, 0.002413, 284.95, 870.536),
                new[] { 5.20288700, 0.04838624, 1.30439695, 34.39644051, 14.72847983, 100.47390909 },
                new[] { -0.00011607, -0.00013253, -0.00183714, 3034.74612775, 0.21252668, 0.20469106 }), sun);
            jupiter.IsGasGiant = true;
            jupiter.Atmosphere = Exp(1e5, 27000, 165, 0, 110, 0.00227, 1.43, 1000000);
            s.Add(Moonlet(Body("io", "Ио", 5.959916e12, 1821.6), 421700, 10), jupiter).Terrain = Ter(3000, 31);
            s.Add(Moonlet(Body("europa", "Европа", 3.202739e12, 1560.8), 671034, 100), jupiter).Terrain = Ter(500, 32);
            s.Add(Moonlet(Body("ganymede", "Ганимед", 9.887834e12, 2634.1), 1070412, 190), jupiter).Terrain = Ter(2000, 33, craters: 0.4);
            s.Add(Moonlet(Body("callisto", "Каллисто", 7.179289e12, 2410.3), 1882709, 280), jupiter).Terrain = Ter(2500, 34, craters: 0.7);

            var saturn = s.Add(Standish(Iau(Body("saturn", "Сатурн", 3.7931187e16, 58232), 40.589, -0.036, 83.537, -0.004, 38.90, 810.7939024),
                new[] { 9.53667594, 0.05386179, 2.48599187, 49.95424423, 92.59887831, 113.66242448 },
                new[] { -0.00125060, -0.00050991, 0.00193609, 1222.49362201, -0.41897216, -0.28867794 }), sun);
            saturn.IsGasGiant = true;
            saturn.Atmosphere = Exp(1e5, 59500, 134, 0, 85, 0.00207, 1.43, 1500000);
            s.Add(Moonlet(Body("enceladus", "Энцелад", 7.2027e9, 252.1), 237948, 60), saturn).Terrain = Ter(800, 41, craters: 0.2);
            var titan = s.Add(Moonlet(Body("titan", "Титан", 8.978138e12, 2574.7), 1221870, 150), saturn);
            titan.Terrain = Ter(800, 42, ocean: true, landBias: 0.15);
            titan.Atmosphere = Exp(146700, 21000, 94, 0, 70, 0.0280, 1.4, 600000);

            var uranus = s.Add(Standish(Iau(Body("uranus", "Уран", 5.793939e15, 25362), 257.311, 0, -15.175, 0, 203.81, -501.1600928),
                new[] { 19.18916464, 0.04725744, 0.77263783, 313.23810451, 170.95427630, 74.01692503 },
                new[] { -0.00196176, -0.00004397, -0.00242939, 428.48202785, 0.40805281, 0.04240589 }), sun);
            uranus.IsGasGiant = true;
            uranus.Atmosphere = Exp(1e5, 27700, 76, 0, 53, 0.00264, 1.45, 800000);

            var neptune = s.Add(Standish(Iau(Body("neptune", "Нептун", 6.836529e15, 24622), 299.36, 0, 43.46, 0, 249.978, 541.1397757),
                new[] { 30.06992276, 0.00859048, 1.77004347, -55.12002969, 44.96476227, 131.78422574 },
                new[] { 0.00026291, 0.00005105, 0.00035372, 218.45945325, -0.32241464, -0.00508664 }), sun);
            neptune.IsGasGiant = true;
            neptune.Atmosphere = Exp(1e5, 19700, 72, 0, 50, 0.00265, 1.45, 600000);
            s.Add(Moonlet(Body("triton", "Тритон", 1.4276e12, 1353.4), 354759, 30, retrograde: true), neptune).Terrain = Ter(1000, 51);

            var pluto = s.Add(Standish(Iau(Body("pluto", "Плутон", 8.696e11, 1188.3), 132.993, 0, -6.163, 0, 302.695, 56.3625225),
                new[] { 39.48211675, 0.24882730, 17.14001206, 238.92903833, 224.06891629, 110.30393684 },
                new[] { -0.00031596, 0.00005170, 0.00004818, 145.20780515, -0.04062942, -0.01183482 }), sun);
            pluto.Terrain = Ter(3000, 61, craters: 0.3);
            s.Add(Moonlet(Body("charon", "Харон", 1.058e11, 606), 19591, 0), pluto).Terrain = Ter(2500, 62, craters: 0.4);

            // Сферы влияния по Лапласу: r = a·(m/M)^0.4, a — на J2000.
            foreach (var b in s.Bodies)
            {
                if (b.Parent == null) continue;
                double a = b.OrbitModel == OrbitModelType.Circular ? b.CircularRadius : b.OrbitAt(0).A;
                b.SoiRadius = a * Math.Pow(b.Mu / b.Parent.Mu, 0.4);
            }

            if (Terrain.Sites.Count == 0)
            {
                Terrain.Sites.Add(new LaunchSite("baikonur", "Байконур, Гагаринский старт", "earth", 45.920, 63.342, 90));
                Terrain.Sites.Add(new LaunchSite("canaveral", "Канаверал, LC-39A", "earth", 28.608, -80.604, 3));
                Terrain.Sites.Add(new LaunchSite("kourou", "Куру, ELA-3", "earth", 5.239, -52.768, 15));
                Terrain.Sites.Add(new LaunchSite("plesetsk", "Плесецк", "earth", 62.927, 40.575, 120));
                Terrain.Sites.Add(new LaunchSite("vostochny", "Восточный", "earth", 51.884, 128.334, 230));
            }
            return s;
        }

        public static LaunchSite GetSite(string id) => Terrain.Sites.Find(x => x.Id == id);
    }
}
