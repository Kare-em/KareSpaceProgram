using System;
using Kare.Space.Core;
using UnityEngine;
using Terrain = Kare.Space.Core.Terrain;

namespace Kare.Space.Game
{
    /// <summary>
    /// Земля с высоты (GDD §9.4, этап 1): биомы вместо «низина → вершина». Прежняя палитра красила всё
    /// выше середины амплитуды в бежевый, и материк с орбиты читался одной песочной шапкой.
    /// Климат — две величины: температура (широта + высота) и влажность (пояса циркуляции + шум).
    /// Функции чистые и потокобезопасные — текстуры строятся параллельно (BodyRenderer.BuildTexture).
    /// </summary>
    public static class EarthSurface
    {
        // Палитра — sRGB, как видны биомы на снимках с орбиты (приглушённые, темнее «картинных»).
        static readonly Color DeepOcean = new Color(0.02f, 0.06f, 0.17f), ShallowOcean = new Color(0.06f, 0.25f, 0.35f);
        static readonly Color SeaIce = new Color(0.82f, 0.86f, 0.90f), Snow = new Color(0.93f, 0.95f, 0.97f);
        static readonly Color Rainforest = new Color(0.10f, 0.24f, 0.08f), Temperate = new Color(0.20f, 0.32f, 0.14f);
        static readonly Color Taiga = new Color(0.15f, 0.23f, 0.14f), Tundra = new Color(0.45f, 0.42f, 0.35f);
        static readonly Color HotDesert = new Color(0.80f, 0.66f, 0.46f), ColdDesert = new Color(0.60f, 0.55f, 0.46f);
        static readonly Color Rock = new Color(0.45f, 0.42f, 0.38f);

        /// <summary>Температура у моря, °C: EquatorTemp на экваторе, на EquatorTemp − PoleDrop ниже на полюсе;
        /// с высотой падает на LapseRate °C/м (СА-1976). Пара: пороги льда −6…−12 °C дают шапку от ≈72°,
        /// как прежний BodyLook.IceLatitude, а снег на горах — выше ≈2,5 км в умеренных широтах.</summary>
        const double EquatorTemp = 27, PoleDrop = 50, LapseRate = 0.0065;
        const float IceStart = -6, IceFull = -12;
        /// <summary>Глубина, м, где цвет мелководья сменяется открытым океаном.</summary>
        const double ShelfDepth = 2500;
        /// <summary>Скальник на высотах, м. Пара: амплитуда рельефа Земли 5 км, 99 % суши ниже 2,4 км.</summary>
        const double RockStart = 1800, RockFull = 3000;
        /// <summary>Гладкость для маски HDRP: океан даёт блик Солнца с орбиты, суша матовая.</summary>
        public const float OceanSmoothness = 0.85f, LandSmoothness = 0.15f, IceSmoothness = 0.4f;
        const int ClimateSeed = 911, CloudSeed = 4242;

        /// <summary>Цвет поверхности (sRGB) и гладкость в точке.</summary>
        public static Color Sample(CelestialBody b, double lat, double lon, out float smoothness)
        {
            var dir = CelestialBody.LatLonToBodyFixed(lat, lon);
            double h = b.SurfaceHeight(dir);
            double a = Math.Abs(lat);
            var perm = Terrain.Perm(ClimateSeed);
            double tn = Terrain.Fbm(perm, dir * 3, 5, 0.55);
            float temp = (float)(EquatorTemp - PoleDrop * Math.Pow(a / 90, 1.6) + 6 * tn - LapseRate * Math.Max(h, 0));
            float ice = Smooth(IceStart, IceFull, temp);

            if (b.Terrain.Ocean && h <= 0)
            {
                var water = OceanColor(-Terrain.RawHeight(b.Terrain, dir));
                smoothness = Mathf.Lerp(OceanSmoothness, IceSmoothness, ice);
                return Color.Lerp(water, SeaIce, ice);
            }

            // Влажность: ВКЗ у экватора, сухие субтропики ≈25°, шторм-треки 50–60°; шум рвёт пояса на пятна.
            double moist = 0.2 + 0.75 * Math.Exp(-Sq(a / 10)) + 0.5 * Math.Exp(-Sq((a - 55) / 14))
                         + 0.6 * Terrain.Fbm(perm, dir * 5 + new Vector3d(31.7, -12.4, 7.9), 4, 0.5);
            var wet = temp < 12 ? Color.Lerp(Taiga, Temperate, temp / 12) : Color.Lerp(Temperate, Rainforest, (temp - 12) / 13);
            var dry = Color.Lerp(ColdDesert, HotDesert, temp / 25);
            var c = Color.Lerp(dry, wet, Smooth(0.2f, 0.6f, (float)moist));
            c = Color.Lerp(c, Tundra, Smooth(3, -5, temp));
            c = Color.Lerp(c, Rock, Smooth((float)RockStart, (float)RockFull, (float)h));
            smoothness = Mathf.Lerp(LandSmoothness, IceSmoothness, ice);
            return Color.Lerp(c, Snow, ice);
        }

        /// <summary>Цвет воды по глубине, м: им же красится вода патча вблизи, чтобы не было шва.</summary>
        public static Color OceanColor(double depth) =>
            Color.Lerp(ShallowOcean, DeepOcean, Mathf.Sqrt(Mathf.Clamp01((float)(depth / ShelfDepth))));

        /// <summary>
        /// Альфа облаков: доля покрытия по широте (экватор и 50–60° пасмурнее, субтропики ясные),
        /// шум сжат по полюсу — облака вытянуты вдоль параллелей, как у зональных ветров; искажение
        /// координат даёт завихрения.
        /// </summary>
        public static float Cloud(double lat, double lon)
        {
            var d = CelestialBody.LatLonToBodyFixed(lat, lon);
            var perm = Terrain.Perm(CloudSeed);
            double a = Math.Abs(lat);
            double cover = 0.45 + 0.25 * Math.Exp(-Sq(a / 8)) - 0.2 * Math.Exp(-Sq((a - 25) / 9)) + 0.2 * Math.Exp(-Sq((a - 55) / 12));
            var p = new Vector3d(d.x, d.y, d.z * 2.2) * 4;
            var warp = new Vector3d(Terrain.Fbm(perm, p * 0.5 + new Vector3d(5.2, 1.3, -7.7), 3, 0.5),
                                    Terrain.Fbm(perm, p * 0.5 + new Vector3d(-2.8, 9.1, 3.3), 3, 0.5), 0);
            double n = Terrain.Fbm(perm, p + warp * 1.5, 7, 0.55);
            // Fbm сосредоточен около 0 (σ ≈ 0,2): порог (0,5 − cover)/2 даёт долю покрытия ≈ cover.
            float thr = (float)((0.5 - cover) * 0.5);
            return 0.92f * Smooth(thr, thr + 0.18f, (float)n);
        }

        static double Sq(double x) => x * x;

        /// <summary>GLSL smoothstep (Mathf.SmoothStep — другое: интерполяция между from и to).</summary>
        static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3 - 2 * t);
        }
    }
}
