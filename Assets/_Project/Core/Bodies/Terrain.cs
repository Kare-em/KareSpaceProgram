using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>Параметры процедурного рельефа тела. Пока нет реальных карт высот (GDD §2.8, §9.4) — шум.</summary>
    public sealed class TerrainSettings
    {
        /// <summary>Размах гор, м.</summary>
        public double Amplitude;
        public int Seed;
        /// <summary>Есть океан: всё ниже нуля — водная гладь на нулевой высоте.</summary>
        public bool Ocean;
        /// <summary>Сдвиг «суши» в долях амплитуды: больше — меньше океана.</summary>
        public double LandBias;
        /// <summary>Частота крупного рельефа (на единичной сфере).</summary>
        public double BaseFrequency = 2.0;
        public int Octaves = 9;
        /// <summary>Кратерированность для безатмосферных тел (0…1).</summary>
        public double Craters;
        /// <summary>Реальная карта суши (Земля): если задана, материки и горные пояса берутся из неё, шум только
        /// дорисовывает детали. Без неё — процедурные материки (тела без карты, headless-тесты без файла).</summary>
        public LandMap Land;
    }

    /// <summary>
    /// Карта суши в равнопромежуточной проекции (Tools/bake-earth-land.py → Data/EarthLand.bytes): поле суши
    /// (0,5 — берег, к 1 — глубь материка, к 0 — открытый океан) и «горность» 0…1. Байты, билинейно, по долготе
    /// по кругу. Формат: int32 W, int32 H, W·H байт поля, W·H байт горности; строка 0 — северный полюс, x = 0 — 180° з. д.
    /// </summary>
    public sealed class LandMap
    {
        public readonly int Width, Height;
        readonly byte[] field, rough;

        public LandMap(byte[] data)
        {
            Width = BitConverter.ToInt32(data, 0);
            Height = BitConverter.ToInt32(data, 4);
            int n = Width * Height;
            if (Width <= 0 || Height <= 0 || data.Length < 8 + 2 * n) throw new ArgumentException("Повреждённая карта суши");
            field = new byte[n];
            rough = new byte[n];
            Buffer.BlockCopy(data, 8, field, 0, n);
            Buffer.BlockCopy(data, 8 + n, rough, 0, n);
        }

        public void Sample(Vector3d dirBodyFixed, out double landField, out double mountains)
        {
            CelestialBody.BodyFixedToLatLon(dirBodyFixed, out double lat, out double lon);
            double fx = (lon + 180) / 360 * Width - 0.5, fy = (90 - lat) / 180 * Height - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            int xa = ((x0 % Width) + Width) % Width, xb = (xa + 1) % Width;
            int ya = Math.Max(0, Math.Min(Height - 1, y0)), yb = Math.Max(0, Math.Min(Height - 1, y0 + 1));
            landField = Bilinear(field, xa, xb, ya, yb, tx, ty);
            mountains = Bilinear(rough, xa, xb, ya, yb, tx, ty);
        }

        double Bilinear(byte[] a, int xa, int xb, int ya, int yb, double tx, double ty)
        {
            double top = a[ya * Width + xa] + (a[ya * Width + xb] - a[ya * Width + xa]) * tx;
            double bot = a[yb * Width + xa] + (a[yb * Width + xb] - a[yb * Width + xa]) * tx;
            return (top + (bot - top) * ty) / 255.0;
        }
    }

    /// <summary>Космодром: точка на теле, вокруг которой рельеф выровнен (GDD §6, реальные космодромы).</summary>
    public sealed class LaunchSite
    {
        public string Id, Name, BodyId;
        public double Latitude, Longitude, Elevation;
        /// <summary>Радиус ровной площадки и зоны плавного перехода, м.</summary>
        public double FlatRadius = 1500, BlendRadius = 9000;
        /// <summary>Высота стартового стола над грунтом, м: ракета висит на опорах над газоотводом, сопла
        /// не уходят в землю (§7). Пара: LaunchPadView.PadHeight рисует бетон на этой же высоте.</summary>
        public double PadHeight = 6;

        public LaunchSite(string id, string name, string bodyId, double lat, double lon, double elevation)
        {
            Id = id;
            Name = name;
            BodyId = bodyId;
            Latitude = lat;
            Longitude = lon;
            Elevation = elevation;
        }

        public Vector3d DirectionBodyFixed => CelestialBody.LatLonToBodyFixed(Latitude, Longitude);
    }

    /// <summary>
    /// Высота рельефа — одна функция и для физики посадки, и для меша (иначе корабль стоит над землёй
    /// или в ней). Детерминированный градиентный шум, только double.
    /// </summary>
    public static class Terrain
    {
        public static readonly List<LaunchSite> Sites = new List<LaunchSite>();

        public static double Height(CelestialBody body, Vector3d dirBodyFixed)
        {
            var ts = body.Terrain;
            var d = dirBodyFixed.normalized;
            double h = RawHeight(ts, d);

            foreach (var site in Sites)
            {
                if (site.BodyId != body.Id) continue;
                double dist = Vector3d.Angle(d, site.DirectionBodyFixed) * body.Radius;
                if (dist >= site.BlendRadius * 6) continue;
                // Ближняя зона — ровная площадка на отметке космодрома; дальше суша поднимается не ниже
                // площадки, чтобы шум не утопил космодром в океане.
                double w = 1 - MathD.Smoothstep(site.FlatRadius, site.BlendRadius, dist);
                double landW = 1 - MathD.Smoothstep(site.BlendRadius, site.BlendRadius * 6, dist);
                h = Math.Max(h, MathD.Lerp(h, site.Elevation, landW));
                h = MathD.Lerp(h, site.Elevation, w);
            }

            if (ts.Ocean && h < 0) h = 0;
            return h;
        }

        /// <summary>Рельеф без океана и площадок: отрицательное — дно.</summary>
        public static double RawHeight(TerrainSettings ts, Vector3d d)
        {
            var p = d * ts.BaseFrequency;
            var perm = Perm(ts.Seed);
            if (ts.Land != null) return MappedHeight(ts, perm, d, p);
            // Континенты — низкая частота, горы — гребневый шум, растущий к центру материков.
            double continent = Fbm(perm, p * 0.6, 4, 0.5) + ts.LandBias;
            double detail = Fbm(perm, p * 3 + new Vector3d(17.3, -5.1, 9.7), ts.Octaves, 0.5);
            double ridge = 1 - Math.Abs(Noise(perm, p * 5 + new Vector3d(-3.3, 8.8, 1.1)));
            ridge *= ridge;
            double land = MathD.Smoothstep(-0.05, 0.25, continent);
            double h = continent * 0.6 + detail * 0.25 + ridge * land * 0.35;
            if (ts.Craters > 0) h += ts.Craters * Craters(perm, d * 8);
            return h * ts.Amplitude;
        }

        /// <summary>Сила шума берега в долях поля суши. Пара: шаг карты ≈ 20 км (bake-earth-land.py) — шум
        /// 0,06 двигает берег примерно на тексель, мельче карта берег не знает.</summary>
        const double CoastNoise = 0.06;
        /// <summary>Глубина открытого океана при поле 0 (поле −0,5 от берега), м — средняя реальная ≈ 3,7 км.</summary>
        const double OceanDepth = 7400;

        /// <summary>
        /// Рельеф по реальной карте суши (§2.8): знак c — суша/океан, высота суши — равнинная ступень + плато и
        /// хребты, растущие с «горностью» карты (Тибет, Анды); шум на суше только вверх, иначе у берега равнины
        /// изрыты ложными озёрами (шум ±375 м при равнине в 100 м).
        /// </summary>
        static double MappedHeight(TerrainSettings ts, int[] perm, Vector3d d, Vector3d p)
        {
            ts.Land.Sample(d, out double f, out double m);
            double c = f - 0.5 + CoastNoise * Fbm(perm, p * 20 + new Vector3d(4.1, -7.7, 2.9), 5, 0.55);
            double detail = Fbm(perm, p * 3 + new Vector3d(17.3, -5.1, 9.7), ts.Octaves, 0.5);
            if (c < 0) return c * OceanDepth + detail * 300 * MathD.Smoothstep(0, 0.1, -c);
            double ridge = 1 - Math.Abs(Noise(perm, p * 5 + new Vector3d(-3.3, 8.8, 1.1)));
            ridge *= ridge;
            double coast = MathD.Smoothstep(0, 0.04, c);
            double h = MathD.Smoothstep(0, 0.3, c) * 0.1 + m * 0.45
                + coast * (Math.Abs(detail) * 0.15 * (0.4 + m) + ridge * m * 0.5);
            return h * ts.Amplitude;
        }

        // ---------------------------------------------------------------- шум

        // Чтение без блокировки: высоты и цвета текстур считаются в Parallel.For, и lock на каждый
        // вызов RawHeight выстраивал потоки в очередь.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int[]> PermCache =
            new System.Collections.Concurrent.ConcurrentDictionary<int, int[]>();

        public static int[] Perm(int seed) => PermCache.GetOrAdd(seed, BuildPerm);

        static int[] BuildPerm(int seed)
        {
            var p = new int[512];
            var src = new int[256];
            for (int i = 0; i < 256; i++) src[i] = i;
            var rng = new Random(seed);
            for (int i = 255; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (src[i], src[j]) = (src[j], src[i]);
            }
            for (int i = 0; i < 512; i++) p[i] = src[i & 255];
            return p;
        }

        public static double Fbm(int[] perm, Vector3d p, int octaves, double gain)
        {
            double sum = 0, amp = 1, norm = 0;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Noise(perm, p);
                norm += amp;
                amp *= gain;
                p = p * 2.03 + new Vector3d(1.7, 9.2, 3.4);
            }
            return sum / norm;
        }

        /// <summary>Улучшенный шум Перлина (2002), диапазон примерно [−1, 1].</summary>
        public static double Noise(int[] perm, Vector3d p)
        {
            double fx = Math.Floor(p.x), fy = Math.Floor(p.y), fz = Math.Floor(p.z);
            int X = (int)fx & 255, Y = (int)fy & 255, Z = (int)fz & 255;
            double x = p.x - fx, y = p.y - fy, z = p.z - fz;
            double u = Fade(x), v = Fade(y), w = Fade(z);
            int A = perm[X] + Y, AA = perm[A] + Z, AB = perm[A + 1] + Z;
            int B = perm[X + 1] + Y, BA = perm[B] + Z, BB = perm[B + 1] + Z;
            return Lerp(w,
                Lerp(v, Lerp(u, Grad(perm[AA], x, y, z), Grad(perm[BA], x - 1, y, z)),
                    Lerp(u, Grad(perm[AB], x, y - 1, z), Grad(perm[BB], x - 1, y - 1, z))),
                Lerp(v, Lerp(u, Grad(perm[AA + 1], x, y, z - 1), Grad(perm[BA + 1], x - 1, y, z - 1)),
                    Lerp(u, Grad(perm[AB + 1], x, y - 1, z - 1), Grad(perm[BB + 1], x - 1, y - 1, z - 1))));
        }

        /// <summary>Кратеры: ячеистый шум, чаша с валом. Возвращает примерно [−1, 0.3].</summary>
        static double Craters(int[] perm, Vector3d p)
        {
            double fx = Math.Floor(p.x), fy = Math.Floor(p.y), fz = Math.Floor(p.z);
            double best = 10;
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            for (int k = -1; k <= 1; k++)
            {
                int cx = (int)fx + i, cy = (int)fy + j, cz = (int)fz + k;
                int hsh = perm[(perm[(perm[cx & 255] + cy) & 255] + cz) & 255];
                var c = new Vector3d(cx + (hsh & 15) / 15.0, cy + ((hsh >> 4) & 15) / 15.0, cz + perm[hsh] / 255.0);
                double dist = (p - c).magnitude;
                if (dist < best) best = dist;
            }
            double r = best / 0.45;
            if (r > 1.3) return 0;
            return r < 1 ? r * r - 1 : 0.3 * (1 - (r - 1) / 0.3);
        }

        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        static double Lerp(double t, double a, double b) => a + t * (b - a);

        static double Grad(int hash, double x, double y, double z)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
    }
}
