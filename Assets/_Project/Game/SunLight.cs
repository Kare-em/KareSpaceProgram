using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Солнце (GDD §9.1, §9.3): Directional Light вдоль луча Солнце → борт, освещённость
    /// 127 000 лк на 1 а.е. по обратному квадрату, затмения по перекрытию угловых дисков в истинной
    /// геометрии (без сжатия §2.7).
    /// </summary>
    [RequireComponent(typeof(Light))]
    public sealed class SunLight : MonoBehaviour
    {
        /// <summary>Освещённость на 1 а.е., лк (§9.1).</summary>
        public const double IlluminanceAt1Au = 127000;
        /// <summary>
        /// Цветовая температура света, К. Не 5778 (фотосфера): в HDRP белая точка — D65, и 5778 К даёт желтоватый
        /// диск, а вместе с голубым небом PBSky ореол bloom вокруг него выходит оливковым — G ≥ R > B
        /// (замер 02.10.2026 у стола: (169,173,166) в 120 px от центра). 6500 К — белый диск, ореол сразу
        /// уходит в голубое: (180,177,172) → (119,122,128) → (98,103,115). Солнце вне атмосферы и есть белое.
        /// Пара: SkyController.BloomScatter — ширина того же ореола.
        /// </summary>
        public const float ColorTemperature = 6500;

        /// <summary>Доля незатенённого Солнца, 0..1 — для HUD и экспозиции.</summary>
        public static double Visible { get; private set; } = 1;
        /// <summary>Солнечный свет включён. Пока он горит, каскадные тени заняты им (NightLight).</summary>
        public static bool Shining => Visible > 1e-4;

        Light sun;
        UnityEngine.Rendering.LensFlareComponentSRP flare;

        void Awake()
        {
            sun = GetComponent<Light>();
            sun.useColorTemperature = true;
            sun.colorTemperature = ColorTemperature;
            // Ореол (flare) PBSky — имитация рассеяния в воздухе, в вакууме его нет (§9.3). Его яркость берётся от
            // диска (≈1,9·10⁹ нит), и bloom растаскивал 2° ореола на весь кадр: на орбите при Солнце у края кадра
            // небо серо-белое R≈240, без ореола — R=0, а сам диск с bloom 0,2 — ровное пятно (замер 01.10.2026).
            var hd = GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
            if (hd != null) { hd.flareSize = 0; hd.flareMultiplier = 0; }
            // Вместо ореола — блик объектива, гаснущий к краю кадра (SunFlare); и ночной свет (NightLight).
            flare = SunFlare.Attach(gameObject);
            NightLight.Create();
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (u?.Active == null) return;
            var s = u.System.Sun;
            var me = FloatingOrigin.WorldP(u.Active);
            var toSun = s.Position - me;
            double r = toSun.magnitude;
            transform.rotation = Quaternion.LookRotation(FloatingOrigin.DirToUnity(-toSun / r));

            Visible = SunFraction(u.System, me, toSun / r, r);
            double au = Constants.AU / r;
            sun.intensity = (float)(IlluminanceAt1Au * au * au * Visible);
            sun.enabled = Shining;
            // На карте блика нет — там своя фиксированная экспозиция и Солнце-иконка (§9.6).
            flare.intensity = MapView.IsOpen ? 0 : (float)Visible;
        }

        /// <summary>
        /// Видимая доля диска Солнца из точки me. Перекрытие дисков приближено плавной ступенью по
        /// расстоянию между центрами: полная тень при θ ≤ ρb − ρs, свет при θ ≥ ρb + ρs (полутень между).
        /// </summary>
        public static double SunFraction(SolarSystem sys, Vector3d me, Vector3d dirSun, double distSun) =>
            DiscFraction(sys, me, dirSun, distSun, sys.Sun.Radius);

        /// <summary>Видимая доля диска любого источника радиуса srcRadius (Солнце, Луна для NightLight).
        /// Сам источник отсекается условием d ≥ distSun — его расстояние совпадает до бита.</summary>
        public static double DiscFraction(SolarSystem sys, Vector3d me, Vector3d dirSun, double distSun, double srcRadius)
        {
            double rs = System.Math.Asin(System.Math.Min(1, srcRadius / distSun));
            double vis = 1;
            foreach (var b in sys.Bodies)
            {
                if (b.Parent == null) continue;
                var tb = b.Position - me;
                double d = tb.magnitude;
                if (d >= distSun) continue;
                // Внутри сферы (приводнение с осадкой, суша ниже уровня моря) тело — полнеба, а не «нет тела»:
                // раньше оно пропускалось, ночью Солнце светило сквозь Землю и мигало при качке на воде.
                double rb = System.Math.Asin(System.Math.Min(1, b.Radius / d));
                double theta = Vector3d.Angle(tb / d, dirSun);
                if (theta >= rb + rs) continue;
                double area = rb >= rs ? 1 : (rb * rb) / (rs * rs); // кольцевое затмение не гасит полностью
                double t = (theta - System.Math.Abs(rb - rs)) / (2 * System.Math.Min(rb, rs));
                double cover = theta <= System.Math.Abs(rb - rs) ? area : area * (1 - MathSmooth(t));
                vis = System.Math.Min(vis, 1 - cover);
            }
            return System.Math.Max(0, vis);
        }

        static double MathSmooth(double t)
        {
            t = System.Math.Max(0, System.Math.Min(1, t));
            return t * t * (3 - 2 * t);
        }
    }
}
