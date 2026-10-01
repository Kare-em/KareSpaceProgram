using UnityEditor;
using UnityEngine;

namespace Kare.Space.EditorTools
{
    /// <summary>
    /// Процедурное звёздное небо (GDD §9.2): равнопромежуточный EXR в эклиптике J2000, импорт как кубмап
    /// (Latitude-Longitude) — эмиссия космоса PBSky. Яркость физическая, в нитах: звезда 0m = 2,54e-6 лк,
    /// размазанная по телесному углу текселя. Ориентацию граней кубмапа делает импортёр — без швов.
    /// Позже заменяется реальной картой (Hipparcos) той же раскладки.
    /// </summary>
    public static class StarFieldBaker
    {
        public const string Path = "Assets/_Project/Settings/StarField.exr";

        /// <summary>Пара: импорт делит ширину на 4 — грань кубмапа 1024, тексель ≈ 0,09° (как у эквиректа на экваторе).</summary>
        const int W = 4096, H = W / 2;
        /// <summary>Освещённость от звезды 0m, лк.</summary>
        const double Lux0 = 2.54e-6;
        /// <summary>Подсчёт звёзд всего неба: lg N(&lt;m) = CountSlope·m + CountZero (≈4600 ярче 6m).</summary>
        const double CountSlope = 0.51, CountZero = 0.6;
        /// <summary>Самая яркая и самая слабая звезда, m. Слабее 8m при EV −5 уже не видно.</summary>
        const double MagMin = -1.5, MagMax = 8;
        /// <summary>Яркость Млечного Пути в ядре полосы, нит (≈20,5 m/угл.сек²). Пара: при EV −5 серое 18 %
        /// ≈ 0,004 нит — полоса видна тусклой, днём (EV 12+) пропадает, как и в жизни.</summary>
        const float MilkyWayNits = 1.2e-3f;
        /// <summary>Полуширина полосы по галактической широте и ширина балджа по долготе, градусы.</summary>
        const double BandSigma = 7, BulgeSigma = 35;
        /// <summary>Фон неба (зодиакальный свет, далёкие звёзды), нит.</summary>
        const float BackgroundNits = 1e-4f;
        /// <summary>Наклон эклиптики J2000, градусы.</summary>
        const double Obliquity = 23.4392911;
        const int Seed = 1957;

        public static Cubemap Ensure(bool force = false)
        {
            if (!force && AssetDatabase.LoadAssetAtPath<Cubemap>(Path) != null)
                return AssetDatabase.LoadAssetAtPath<Cubemap>(Path);

            var px = new Color[W * H];
            var rnd = new System.Random(Seed);
            // Галактика в эклиптике: полюс и центр из экваториальных координат (IAU 1958).
            var pole = EquatorialToEcliptic(192.85948, 27.12825);
            var center = EquatorialToEcliptic(266.40500, -28.93617);
            var side = Cross(pole, center);

            for (int y = 0; y < H; y++)
            {
                double lat = -System.Math.PI / 2 + System.Math.PI * (y + 0.5) / H;
                for (int x = 0; x < W; x++)
                {
                    double lon = 2 * System.Math.PI * (x + 0.5) / W;
                    var d = Dir(lon, lat);
                    double b = System.Math.Asin(Dot(d, pole)) * 180 / System.Math.PI;
                    double l = System.Math.Atan2(Dot(d, side), Dot(d, center)) * 180 / System.Math.PI;
                    px[y * W + x] = MilkyWay(b, l);
                }
            }

            // Звёзды: величина по закону подсчёта, лёгкое сгущение слабых к плоскости Галактики.
            double a = System.Math.Pow(10, CountSlope * MagMin), c = System.Math.Pow(10, CountSlope * MagMax);
            int count = (int)System.Math.Pow(10, CountSlope * MagMax + CountZero);
            for (int i = 0; i < count; i++)
            {
                double m = System.Math.Log10(a + rnd.NextDouble() * (c - a)) / CountSlope;
                double[] d;
                do
                {
                    double z = 2 * rnd.NextDouble() - 1, phi = 2 * System.Math.PI * rnd.NextDouble();
                    double s = System.Math.Sqrt(1 - z * z);
                    d = new[] { s * System.Math.Cos(phi), s * System.Math.Sin(phi), z };
                    double gb = System.Math.Asin(Dot(d, pole)) * 180 / System.Math.PI;
                    double keep = m < 4 ? 1 : 0.35 + 0.65 * System.Math.Exp(-gb * gb / (2 * 20 * 20));
                    if (rnd.NextDouble() < keep) break;
                } while (true);
                Splat(px, d, Lux0 * System.Math.Pow(10, -0.4 * m), StarTint(rnd.NextDouble()));
            }

            var tex = new Texture2D(W, H, TextureFormat.RGBAHalf, false, true);
            tex.SetPixels(px);
            tex.Apply(false);
            System.IO.File.WriteAllBytes(Path, tex.EncodeToEXR(Texture2D.EXRFlags.CompressZIP));
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(Path, ImportAssetOptions.ForceUpdate);

            var imp = (TextureImporter)AssetImporter.GetAtPath(Path);
            imp.textureShape = TextureImporterShape.TextureCube;
            imp.generateCubemap = TextureImporterGenerateCubemap.Cylindrical;
            imp.sRGBTexture = false;
            imp.mipmapEnabled = false;
            imp.maxTextureSize = W / 4; // размер грани; по умолчанию импортёр берёт W/2 — 24 МБ вместо 6
            imp.textureCompression = TextureImporterCompression.CompressedHQ; // BC6H
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Cubemap>(Path);
        }

        static Color MilkyWay(double b, double l)
        {
            double band = System.Math.Exp(-b * b / (2 * BandSigma * BandSigma));
            double bulge = 1 + 2.5 * System.Math.Exp(-l * l / (2 * BulgeSigma * BulgeSigma)) * System.Math.Exp(-b * b / (2 * 12 * 12));
            // Пылевые прожилки: шум по (l, b), шов l = ±180 — в антицентре, где полоса слабая.
            float nx = (float)(l / 6 + 500), ny = (float)(b / 3 + 500);
            float n = 0.6f * Mathf.PerlinNoise(nx, ny) + 0.4f * Mathf.PerlinNoise(nx * 3.1f, ny * 3.1f);
            float dust = Mathf.Lerp(0.25f, 1.3f, n);
            float lum = (float)(band * bulge) * dust * MilkyWayNits + BackgroundNits;
            // Балдж желтее, диск — нейтральный.
            var tint = Color.Lerp(new Color(0.85f, 0.9f, 1f), new Color(1f, 0.85f, 0.65f), (float)(bulge - 1) / 2.5f);
            return tint * lum;
        }

        /// <summary>Энергия звезды гауссом ≈1,5 текселя (по долготе шире на 1/cos β), чтобы при перекладке
        /// эквиректа в кубмап звёзды не терялись у полюсов. Яркость текселя = E·w/Ω.</summary>
        static void Splat(Color[] px, double[] d, double lux, Color tint)
        {
            double lat = System.Math.Asin(d[2]);
            double lon = System.Math.Atan2(d[1], d[0]);
            if (lon < 0) lon += 2 * System.Math.PI;
            double fx = lon / (2 * System.Math.PI) * W - 0.5, fy = (lat + System.Math.PI / 2) / System.Math.PI * H - 0.5;
            double cos = System.Math.Max(System.Math.Cos(lat), 0.02);
            double sy = 0.7, sx = 0.7 / cos;
            int rx = (int)System.Math.Ceiling(sx * 2.5), ry = 2;
            int x0 = (int)System.Math.Round(fx), y0 = (int)System.Math.Round(fy);
            double pixelSa = (2 * System.Math.PI / W) * (System.Math.PI / H);
            double sum = 0;
            for (int pass = 0; pass < 2; pass++)
            for (int j = -ry; j <= ry; j++)
            {
                int y = y0 + j;
                if (y < 0 || y >= H) continue;
                for (int i = -rx; i <= rx; i++)
                {
                    double dx = (x0 + i - fx) / sx, dy = (y - fy) / sy;
                    double w = System.Math.Exp(-0.5 * (dx * dx + dy * dy));
                    if (pass == 0) { sum += w; continue; }
                    int x = ((x0 + i) % W + W) % W;
                    double rowLat = -System.Math.PI / 2 + System.Math.PI * (y + 0.5) / H;
                    double sa = pixelSa * System.Math.Max(System.Math.Cos(rowLat), 1e-3);
                    px[y * W + x] += tint * (float)(lux * w / sum / sa);
                }
            }
        }

        /// <summary>Цвет по спектральному классу: доли примерно как у звёзд, видимых глазом.</summary>
        static Color StarTint(double r)
        {
            if (r < 0.15) return new Color(0.75f, 0.85f, 1.25f);  // B–A
            if (r < 0.55) return new Color(1f, 1f, 1f);           // A–F
            if (r < 0.85) return new Color(1.1f, 0.98f, 0.82f);   // G–K
            return new Color(1.2f, 0.85f, 0.6f);                  // K–M
        }

        static double[] EquatorialToEcliptic(double raDeg, double decDeg)
        {
            double ra = raDeg * System.Math.PI / 180, dec = decDeg * System.Math.PI / 180, e = Obliquity * System.Math.PI / 180;
            double x = System.Math.Cos(dec) * System.Math.Cos(ra), y = System.Math.Cos(dec) * System.Math.Sin(ra), z = System.Math.Sin(dec);
            return new[] { x, y * System.Math.Cos(e) + z * System.Math.Sin(e), -y * System.Math.Sin(e) + z * System.Math.Cos(e) };
        }

        static double[] Dir(double lon, double lat) =>
            new[] { System.Math.Cos(lat) * System.Math.Cos(lon), System.Math.Cos(lat) * System.Math.Sin(lon), System.Math.Sin(lat) };

        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    }
}
