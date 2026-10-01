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

        /// <summary>Доля незатенённого Солнца, 0..1 — для HUD и экспозиции.</summary>
        public static double Visible { get; private set; } = 1;

        Light sun;

        void Awake() => sun = GetComponent<Light>();

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
            sun.enabled = Visible > 1e-4;
        }

        /// <summary>
        /// Видимая доля диска Солнца из точки me. Перекрытие дисков приближено плавной ступенью по
        /// расстоянию между центрами: полная тень при θ ≤ ρb − ρs, свет при θ ≥ ρb + ρs (полутень между).
        /// </summary>
        public static double SunFraction(SolarSystem sys, Vector3d me, Vector3d dirSun, double distSun)
        {
            double rs = System.Math.Asin(System.Math.Min(1, sys.Sun.Radius / distSun));
            double vis = 1;
            foreach (var b in sys.Bodies)
            {
                if (b.Parent == null) continue;
                var tb = b.Position - me;
                double d = tb.magnitude;
                if (d >= distSun || d < b.Radius) continue;
                double rb = System.Math.Asin(b.Radius / d);
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
