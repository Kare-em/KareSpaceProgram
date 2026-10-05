using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>Параметры рельефа тела (GDD §2.8, §9.4): реальная карта высот (Map), если её подал хост, иначе
    /// процедурный шум (тела без карты, headless-тесты без файлов).</summary>
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
        /// <summary>Октав шума деталей поверх карты высот (Terrain.DetailOctaves: от текселя до ~30 м).</summary>
        public int DetailOctaves = 8;
        /// <summary>Кратерированность для безатмосферных тел (0…1).</summary>
        public double Craters;
        /// <summary>Реальная карта суши (Земля): если задана, материки и горные пояса берутся из неё, шум только
        /// дорисовывает детали. Без неё — процедурные материки (тела без карты, headless-тесты без файла).</summary>
        public LandMap Land;
        /// <summary>Реальная карта высот (DEM): если задана, рельеф — она плюс шум мельче её текселя
        /// (Terrain.DemHeight), Land и процедурные материки не используются. Ставит SolarSystem.CreateReal.</summary>
        public HeightMap Map;
    }

    /// <summary>
    /// Реальная карта высот тела (Tools/bake-dem.py → Data/&lt;Body&gt;Height.bytes; GDD §2.8): ETOPO 2022 (Земля,
    /// с батиметрией), LRO LOLA, MGS MOLA, MESSENGER, Magellan. Равнопромежуточная, раскладка как у LandMap:
    /// строка 0 — северный полюс, x = 0 — 180° з. д., центры текселей; билинейно, по долготе по кругу.
    /// Формат: 'KDEM', int32 W, int32 H, float32 scale, float32 offset, W·H int16 (LE); высота = offset + scale·v,
    /// м над радиусом тела в ядре. Поверх — подробные вставки вокруг площадок (Patches, AddPatches).
    /// </summary>
    public sealed class HeightMap
    {
        public readonly int Width, Height;
        /// <summary>Высоты карты и вставок по текселям, м: самая низкая и самая высокая.</summary>
        public double MinHeight { get; private set; }
        public double MaxHeight { get; private set; }
        /// <summary>Верхняя граница рельефа вместе с шумом деталей, м — для отсевов «выше любых гор».</summary>
        public double MaxWithDetail { get; private set; }
        /// <summary>Подробные вставки (Data/EarthPatches.bytes): мыс Канаверал в 0,1° — море (−4 м в LC-39A).</summary>
        public readonly List<DemPatch> Patches = new List<DemPatch>();
        readonly short[] h;
        readonly double scale, offset;
        // Шероховатость: СКО перепада между соседними текселями, м, по блокам RoughBlock² текселей.
        readonly float[] rough;
        readonly int rw, rh;

        /// <summary>Блок сетки шероховатости, текселей. Пара: 8 × 0,1° ≈ 24 км на Луне — шум деталей меняет силу
        /// плавно, но моря и материк различает (моря ≈ 20–50 м на тексель, материк 200–400 м).</summary>
        internal const int RoughBlock = 8;
        /// <summary>Предел шероховатости, м: на обрывах (стены Долин Маринер — 1–2 км на тексель) шум силой в
        /// перепад рисовал бы километровые зубья, которых в данных нет.</summary>
        public const double RoughCap = 400;

        public HeightMap(byte[] data)
        {
            if (data.Length < 20 || data[0] != 'K' || data[1] != 'D' || data[2] != 'E' || data[3] != 'M')
                throw new ArgumentException("Не карта высот KDEM");
            Width = BitConverter.ToInt32(data, 4);
            Height = BitConverter.ToInt32(data, 8);
            scale = BitConverter.ToSingle(data, 12);
            offset = BitConverter.ToSingle(data, 16);
            int n = Width * Height;
            if (Width <= 0 || Height <= 0 || data.Length < 20 + 2 * n) throw new ArgumentException("Повреждённая карта высот");
            h = new short[n];
            Buffer.BlockCopy(data, 20, h, 0, 2 * n);
            MinMax(h, scale, offset, out double lo, out double hi);
            MinHeight = lo;
            MaxHeight = hi;
            rough = RoughGrid(h, Width, Height, scale, true, out rw, out rh, out double maxRough);
            // Билинейная смесь не выходит за максимум узлов, |Fbm| ≤ 1 — граница с запасом.
            MaxWithDetail = MaxHeight + Terrain.DetailGain * maxRough;
        }

        /// <summary>Вставки из Data/EarthPatches.bytes (Tools/bake-dem.py patches). Формат: 'KDPT', int32 N; на вставку
        /// float64 lat_s, lat_n, lon_w, lon_e (края, °), int32 W, H, float32 scale, offset, W·H int16 (LE), строка 0 —
        /// север. Ставит хост до CreateReal: SolarSystem.ApplyHeightMap берёт MaxWithDetail уже со вставками.</summary>
        public void AddPatches(byte[] data)
        {
            if (data.Length < 8 || data[0] != 'K' || data[1] != 'D' || data[2] != 'P' || data[3] != 'T')
                throw new ArgumentException("Не вставки высот KDPT");
            int count = BitConverter.ToInt32(data, 4), at = 8;
            for (int i = 0; i < count; i++)
            {
                var p = new DemPatch(data, ref at);
                Patches.Add(p);
                MinHeight = Math.Min(MinHeight, p.MinHeight);
                MaxHeight = Math.Max(MaxHeight, p.MaxHeight);
                MaxWithDetail = Math.Max(MaxWithDetail, p.MaxWithDetail);
            }
        }

        internal static void MinMax(short[] h, double scale, double offset, out double min, out double max)
        {
            short lo = short.MaxValue, hi = short.MinValue;
            foreach (var v in h)
            {
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            double a = offset + scale * lo, b = offset + scale * hi;
            min = Math.Min(a, b);
            max = Math.Max(a, b);
        }

        /// <summary>Сетка шероховатости по блокам RoughBlock² (СКО перепада соседних текселей, м, не больше RoughCap).</summary>
        internal static float[] RoughGrid(short[] h, int w, int hgt, double scale, bool wrapX, out int rw, out int rh, out double maxRough)
        {
            rw = (w + RoughBlock - 1) / RoughBlock;
            rh = (hgt + RoughBlock - 1) / RoughBlock;
            var rough = new float[rw * rh];
            maxRough = 0;
            for (int by = 0; by < rh; by++)
            for (int bx = 0; bx < rw; bx++)
            {
                double sum = 0;
                int cnt = 0;
                for (int y = by * RoughBlock; y < Math.Min(hgt, (by + 1) * RoughBlock); y++)
                for (int x = bx * RoughBlock; x < Math.Min(w, (bx + 1) * RoughBlock); x++)
                {
                    int i = y * w + x;
                    if (wrapX || x + 1 < w)
                    {
                        double dx = h[y * w + (x + 1) % w] - h[i];
                        sum += dx * dx;
                        cnt++;
                    }
                    if (y + 1 >= hgt) continue;
                    double dy = h[i + w] - h[i];
                    sum += dy * dy;
                    cnt++;
                }
                double r = Math.Min(RoughCap, Math.Abs(scale) * Math.Sqrt(sum / Math.Max(1, cnt)));
                rough[by * rw + bx] = (float)r;
                maxRough = Math.Max(maxRough, r);
            }
            return rough;
        }

        /// <summary>Шероховатость — билинейно по центрам блоков (центр блока k — тексель k·B + (B−1)/2): без ступенек
        /// силы шума на границах блоков. fx, fy — координаты в текселях карты (центр текселя 0 — 0).</summary>
        internal static double RoughAt(float[] rough, int rw, int rh, double fx, double fy, bool wrapX)
        {
            double gx = (fx - (RoughBlock - 1) * 0.5) / RoughBlock, gy = (fy - (RoughBlock - 1) * 0.5) / RoughBlock;
            int rx0 = (int)Math.Floor(gx), ry0 = (int)Math.Floor(gy);
            double sx = gx - rx0, sy = gy - ry0;
            int ra, rb;
            if (wrapX) { ra = ((rx0 % rw) + rw) % rw; rb = (ra + 1) % rw; }
            else { ra = Math.Max(0, Math.Min(rw - 1, rx0)); rb = Math.Max(0, Math.Min(rw - 1, rx0 + 1)); }
            int rya = Math.Max(0, Math.Min(rh - 1, ry0)), ryb = Math.Max(0, Math.Min(rh - 1, ry0 + 1));
            double r0 = rough[rya * rw + ra] + (rough[rya * rw + rb] - rough[rya * rw + ra]) * sx;
            double r1 = rough[ryb * rw + ra] + (rough[ryb * rw + rb] - rough[ryb * rw + ra]) * sx;
            return r0 + (r1 - r0) * sy;
        }

        /// <summary>Размер текселя на экваторе, м, для тела радиуса radius.</summary>
        public double CellSize(double radius) => 2 * Math.PI * radius / Width;

        /// <summary>Высота глобальной карты (м) и шероховатость (СКО перепада на тексель, м) в направлении в осях тела.</summary>
        public double Sample(Vector3d dirBodyFixed, out double roughness)
        {
            CelestialBody.BodyFixedToLatLon(dirBodyFixed, out double lat, out double lon);
            return Sample(lat, lon, out roughness);
        }

        /// <summary>Только глобальная карта, без вставок (Terrain.DemHeight смешивает их сам).</summary>
        public double Sample(double lat, double lon, out double roughness)
        {
            double fx = (lon + 180) / 360 * Width - 0.5, fy = (90 - lat) / 180 * Height - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            int xa = ((x0 % Width) + Width) % Width, xb = (xa + 1) % Width;
            int ya = Math.Max(0, Math.Min(Height - 1, y0)), yb = Math.Max(0, Math.Min(Height - 1, y0 + 1));
            double top = h[ya * Width + xa] + (h[ya * Width + xb] - h[ya * Width + xa]) * tx;
            double bot = h[yb * Width + xa] + (h[yb * Width + xb] - h[yb * Width + xa]) * tx;
            roughness = RoughAt(rough, rw, rh, fx, fy, true);
            return offset + scale * (top + (bot - top) * ty);
        }

        /// <summary>Вставка, накрывающая точку, и её вес 0…1 (0 — вне всех вставок). Вставки не перекрываются
        /// (bake-dem.py сливает близкие площадки в одно окно), берётся первая с весом &gt; 0.</summary>
        public DemPatch PatchAt(double lat, double lon, out double weight)
        {
            for (int i = 0; i < Patches.Count; i++)
            {
                weight = Patches[i].Weight(lat, lon);
                if (weight > 0) return Patches[i];
            }
            weight = 0;
            return null;
        }
    }

    /// <summary>
    /// Подробная вставка карты высот (ETOPO 2022 15″ ≈ 460 м вокруг земных площадок; Tools/bake-dem.py patches).
    /// Глобальные 0,1° (11 км) топят узкие косы: мыс Канаверал билинейно −4 м в LC-39A. Окно lat/lon,
    /// строка 0 — север, центры текселей, билинейно; у краёв — плавный вес к глобальной карте (Weight).
    /// </summary>
    public sealed class DemPatch
    {
        public readonly double LatS, LatN, LonW, LonE;
        public readonly int Width, Height;
        public readonly double MinHeight, MaxHeight, MaxWithDetail;
        /// <summary>Текселей вставки на полный оборот по долготе: первая волна шума деталей — её тексель.</summary>
        public readonly double CellsPerTurn;
        /// <summary>Октав шума деталей: от текселя вставки до Terrain.DetailFinest; ставит SolarSystem.ApplyHeightMap.</summary>
        public int DetailOctaves = 4;
        readonly short[] h;
        readonly double scale, offset;
        readonly float[] rough;
        readonly int rw, rh;

        /// <summary>Полоса смешивания с глобальной картой у края вставки, °. Пара: окно 1,5° (bake-dem.py PATCH_DEG) —
        /// ядро без смешивания 1,1°; 0,2° — два текселя глобальной карты, ступеньки по краю нет.</summary>
        public const double FadeDeg = 0.2;

        internal DemPatch(byte[] d, ref int at)
        {
            LatS = BitConverter.ToDouble(d, at);
            LatN = BitConverter.ToDouble(d, at + 8);
            LonW = BitConverter.ToDouble(d, at + 16);
            LonE = BitConverter.ToDouble(d, at + 24);
            Width = BitConverter.ToInt32(d, at + 32);
            Height = BitConverter.ToInt32(d, at + 36);
            scale = BitConverter.ToSingle(d, at + 40);
            offset = BitConverter.ToSingle(d, at + 44);
            at += 48;
            int n = Width * Height;
            if (Width < 2 || Height < 2 || d.Length < at + 2 * n || LatN <= LatS || LonE <= LonW)
                throw new ArgumentException("Повреждённая вставка высот");
            h = new short[n];
            Buffer.BlockCopy(d, at, h, 0, 2 * n);
            at += 2 * n;
            HeightMap.MinMax(h, scale, offset, out double lo, out double hi);
            MinHeight = lo;
            MaxHeight = hi;
            rough = HeightMap.RoughGrid(h, Width, Height, scale, false, out rw, out rh, out double maxRough);
            MaxWithDetail = MaxHeight + Terrain.DetailGain * maxRough;
            CellsPerTurn = Width * 360.0 / (LonE - LonW);
        }

        /// <summary>Размер текселя по долготе на экваторе, м, для тела радиуса radius.</summary>
        public double CellSize(double radius) => 2 * Math.PI * radius / CellsPerTurn;

        static double Wrap180(double a) => a - 360 * Math.Floor((a + 180) / 360);

        /// <summary>Вес вставки: 1 внутри, плавно к 0 в полосе FadeDeg у края, 0 снаружи.</summary>
        public double Weight(double lat, double lon)
        {
            double dy = Math.Min(lat - LatS, LatN - lat);
            if (dy <= 0) return 0;
            double x = Wrap180(lon - LonW);
            double dx = Math.Min(x, LonE - LonW - x);
            if (dx <= 0) return 0;
            return MathD.Smoothstep(0, FadeDeg, dx) * MathD.Smoothstep(0, FadeDeg, dy);
        }

        /// <summary>Высота вставки (м) и шероховатость (м на её тексель); у краёв — крайние тексели.</summary>
        public double Sample(double lat, double lon, out double roughness)
        {
            double fx = Wrap180(lon - LonW) / (LonE - LonW) * Width - 0.5, fy = (LatN - lat) / (LatN - LatS) * Height - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = MathD.Clamp(fx - x0, 0, 1), ty = MathD.Clamp(fy - y0, 0, 1);
            int xa = Math.Max(0, Math.Min(Width - 1, x0)), xb = Math.Max(0, Math.Min(Width - 1, x0 + 1));
            int ya = Math.Max(0, Math.Min(Height - 1, y0)), yb = Math.Max(0, Math.Min(Height - 1, y0 + 1));
            double top = h[ya * Width + xa] + (h[ya * Width + xb] - h[ya * Width + xa]) * tx;
            double bot = h[yb * Width + xa] + (h[yb * Width + xb] - h[yb * Width + xa]) * tx;
            roughness = HeightMap.RoughAt(rough, rw, rh, fx, fy, false);
            return offset + scale * (top + (bot - top) * ty);
        }
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
        /// <summary>Радиус котловины, м: на нём сырой рельеф плавно опускается к отметке космодрома (Terrain.Height).
        /// Карта суши в 20 км ставит Байконур на 1130 м при реальных 90, и площадка в 9 км стояла в котловане
        /// с километровой стеной над горизонтом (замер 02.10.2026). 150 км дают уклон ≤ 0,7° на перепад 1 км.
        /// Пара: BlendRadius — внутри него рельеф уже выровнен, котловина должна быть много шире.</summary>
        public double BasinRadius = 150000;
        /// <summary>Сырой рельеф в центре минус отметка, м (NaN — не посчитан). Детерминирован, гонка потоков безвредна.</summary>
        internal double BasinDrop = double.NaN;
        /// <summary>Вес подробной вставки DEM в центре площадки 0…1 (NaN — не посчитан; Terrain.Height).</summary>
        internal double PatchCover = double.NaN;

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
    /// или в ней). Реальная карта высот с шумом деталей или процедурный градиентный шум; детерминировано, double.
    /// </summary>
    public static class Terrain
    {
        public static readonly List<LaunchSite> Sites = new List<LaunchSite>();

        public static double Height(CelestialBody body, Vector3d dirBodyFixed)
        {
            var ts = body.Terrain;
            var d = dirBodyFixed.normalized;
            double h = RawHeight(ts, d), raw = h;

            foreach (var site in Sites)
            {
                if (site.BodyId != body.Id) continue;
                double dist = Vector3d.Angle(d, site.DirectionBodyFixed) * body.Radius;
                if (dist >= Math.Max(site.BlendRadius * 6, site.BasinRadius)) continue;
                // Котловина: суша вокруг космодрома, поднятая картой выше его отметки, плавно опускается на
                // ту же разницу — мелкий рельеф сохраняется, а ступени у края площадки нет.
                if (double.IsNaN(site.BasinDrop))
                    site.BasinDrop = Math.Max(0, RawHeight(ts, site.DirectionBodyFixed.normalized) - site.Elevation);
                // Опускаем не ниже отметки: вычитание полного перепада уводило низины в 50–150 км под ноль, и
                // вокруг Байконура выступали «озёра» на текстуре сферы (02.10.2026). Выше Elevation+перепад рельеф
                // сдвинут целиком, между — сжат к отметке, ниже отметки не тронут; по h монотонно (наклон 1−w ≥ 0).
                double bw = 1 - MathD.Smoothstep(site.BlendRadius, site.BasinRadius, dist);
                h -= bw * Math.Min(site.BasinDrop, Math.Max(0, h - site.Elevation));
                if (dist >= site.BlendRadius * 6) continue;
                if (double.IsNaN(site.PatchCover))
                {
                    double cw = 0;
                    if (ts.Map != null && ts.Map.Patches.Count > 0) ts.Map.PatchAt(site.Latitude, site.Longitude, out cw);
                    site.PatchCover = cw;
                }
                double pc = site.PatchCover;
                // Ближняя зона — ровная площадка на отметке космодрома; дальше суша поднимается не ниже
                // площадки, чтобы шум не утопил космодром в океане. Во вставке DEM берег настоящий: подъём
                // выключен (иначе море в 9 км вокруг LC-39A — суша на 3 м), а море за ядром стола не выравнивается.
                double w = 1 - MathD.Smoothstep(site.FlatRadius, site.BlendRadius, dist);
                double landW = (1 - pc) * (1 - MathD.Smoothstep(site.BlendRadius, site.BlendRadius * 6, dist));
                if (pc > 0)
                    w *= 1 - pc * (1 - MathD.Smoothstep(PatchSeaCut, 0, raw)) * MathD.Smoothstep(PatchPadCore, 2 * PatchPadCore, dist);
                h = Math.Max(h, MathD.Lerp(h, site.Elevation, landW));
                h = MathD.Lerp(h, site.Elevation, w);
            }

            if (ts.Ocean && h < 0) h = 0;
            return h;
        }

        /// <summary>Ядро площадки во вставке DEM, м: внутри стол выравнивается всегда, даже над водой. Пара: плита стола
        /// ±22 м + перрон (LaunchPadView) много меньше; берег у LC-39A по 15″ в 0,5–1 км — ядро в него не лезет.</summary>
        const double PatchPadCore = 300;
        /// <summary>Сырая высота, м, ниже которой тексель вставки — вода (не выравнивается к отметке). Пара: лагуны
        /// Банана-Ривер/Индиан-Ривер в ETOPO 15″ −1…−2 м, океан в 1 км от мыса −8…−10 м; 0 и выше — суша.</summary>
        const double PatchSeaCut = -2;

        /// <summary>Рельеф без океана и площадок: отрицательное — дно.</summary>
        public static double RawHeight(TerrainSettings ts, Vector3d d)
        {
            var perm = Perm(ts.Seed);
            if (ts.Map != null) return DemHeight(ts, perm, d);
            var p = d * ts.BaseFrequency;
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

        /// <summary>Сила шума деталей в долях шероховатости карты. Fbm ≈ ±1 (СКО ≈ 0,25): мелочь ≈ ¼ перепада между
        /// соседними текселями — фрактальное продолжение рельефа ниже разрешения карты; крупные формы не трогает.
        /// Пара: HeightMap.MaxWithDetail считает границу с этим же множителем.</summary>
        public const double DetailGain = 1.0;
        /// <summary>Самая короткая волна шума деталей, м. Пара: узлы патча BodyRenderer у борта ≈ 20 м — мельче
        /// сетка не покажет, только наложение.</summary>
        public const double DetailFinest = 30;
        /// <summary>Шум береговой линии Земли, м. Пара: шельф ≈ 2 м/км, приморская равнина 1–5 м/км — 12 м сдвигают
        /// берег на 3–6 км, порядка текселя ETOPO (11 км): берег изрезан, а не ломаной по текселям.</summary>
        const double CoastAmp = 12;

        /// <summary>Октав шума деталей для карты на теле радиуса radius: от текселя до DetailFinest.</summary>
        public static int DetailOctaves(HeightMap map, double radius) => DetailOctaves(map.CellSize(radius));

        /// <summary>Октав шума деталей для текселя cellSize, м: от текселя до DetailFinest.</summary>
        public static int DetailOctaves(double cellSize) =>
            Math.Max(1, Math.Min(10, (int)Math.Ceiling(Math.Log(cellSize / DetailFinest) / Math.Log(2.03))));

        /// <summary>
        /// Рельеф по реальной карте высот (§2.8): билинейная высота DEM + шум мельче текселя, сила которого — местная
        /// шероховатость карты (моря гладкие, материки и горы — изрезанные). Первая октава шума — размер текселя:
        /// p = d·W/2π, единица шума = 2πR/W. У тела с океаном шум не меняет знак высоты (не роет ложных озёр на
        /// низменностях и не насыпает островов в море); берег изрезан отдельным слабым шумом только у берега по
        /// карте суши (EarthLand), а не везде, где суша низкая (Прикаспий поднят до +1 м — там были бы «соты» озёр).
        /// </summary>
        static double DemHeight(TerrainSettings ts, int[] perm, Vector3d d)
        {
            var map = ts.Map;
            CelestialBody.BodyFixedToLatLon(d, out double lat, out double lon);
            double h = map.Sample(lat, lon, out double rough);
            var p = d * (map.Width / (2 * Math.PI));
            double det = DetailGain * rough * Fbm(perm, p + new Vector3d(31.7, -12.9, 5.3), ts.DetailOctaves, 0.5);
            // Подробная вставка у площадки (15″ ≈ 460 м против 11 км): её высота и её шум — первая октава размером с
            // её тексель, сила — её шероховатость (приморская равнина 1–3 м, а не 11-км-ные перепады шельфа).
            double pw = 0;
            var patch = map.Patches.Count > 0 ? map.PatchAt(lat, lon, out pw) : null;
            if (patch != null)
            {
                double hp = patch.Sample(lat, lon, out double rp);
                var pp = d * (patch.CellsPerTurn / (2 * Math.PI));
                double detP = DetailGain * rp * Fbm(perm, pp + new Vector3d(-21.3, 7.7, 40.1), patch.DetailOctaves, 0.5);
                h = MathD.Lerp(h, hp, pw);
                det = MathD.Lerp(det, detP, pw);
            }
            if (!ts.Ocean) return h + det;
            // Знак сохраняется: |h| уходит к нулю не дальше чем на 90 %, на самом берегу шум гаснет.
            h = h >= 0 ? h + Math.Max(det, -0.9 * h) : h + Math.Min(det, -0.9 * h);
            // Во вставке берег уже настоящий (460 м): шум берега в 12 м сдвинул бы его на километры и снова утопил
            // косу шириной в пару километров. Глушим его весом вставки.
            if (ts.Land == null || pw >= 1) return h;
            ts.Land.Sample(d, out double f, out _);
            double near = 1 - MathD.Smoothstep(0.1, 0.25, Math.Abs(f - 0.5));
            if (near <= 0) return h;
            double band = (1 - pw) * (1 - MathD.Smoothstep(CoastAmp, 3 * CoastAmp, Math.Abs(h)));
            // ×2: типичный |Fbm| ≈ 0,5, берег гуляет на ±CoastAmp.
            return h + near * band * CoastAmp * Fbm(perm, p * 3 + new Vector3d(-8.1, 4.4, 19.2), 6, 0.55) * 2;
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
