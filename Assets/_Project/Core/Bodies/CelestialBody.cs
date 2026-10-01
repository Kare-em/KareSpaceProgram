using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    public enum OrbitModelType
    {
        /// <summary>Неподвижно (Солнце — начало гелиоцентрической системы).</summary>
        Fixed,
        /// <summary>JPL «Approximate Positions of the Planets» (Standish): элементы J2000 + вековые скорости.</summary>
        Standish,
        /// <summary>Луна по элементам Шлайтера с прецессией узла и перигея.</summary>
        Schlyter,
        /// <summary>Круговая орбита в плоскости экватора родителя — для спутников внешних планет.</summary>
        Circular,
    }

    /// <summary>
    /// Небесное тело на кеплеровых «рельсах» (GDD §2.3). Определение (масса, радиус, орбита, вращение)
    /// плюс состояние на текущий момент, которое пересчитывает <see cref="SolarSystem.Update"/>.
    /// Все векторы — в системе P (эклиптика J2000, правая), единицы СИ.
    /// </summary>
    public sealed class CelestialBody
    {
        public int Index;
        public string Id;
        public string Name;
        public CelestialBody Parent;
        public readonly List<CelestialBody> Children = new List<CelestialBody>();

        public double Mu;
        public double Radius;
        public double SoiRadius = double.PositiveInfinity;
        public Atmosphere Atmosphere;
        public TerrainSettings Terrain;
        /// <summary>Газовый гигант: твёрдой поверхности нет, на «радиусе» (1 бар) корабль гибнет.</summary>
        public bool IsGasGiant;

        // Вращение по IAU: полюс α0, δ0 (град + град/столетие), меридиан W (град + град/сутки).
        public double PoleRa0, PoleRaRate, PoleDec0, PoleDecRate, W0, WRate;
        /// <summary>Приливный захват: нулевой меридиан всегда смотрит на родителя (для круговых спутников).</summary>
        public bool TidallyLocked;

        public OrbitModelType OrbitModel;
        /// <summary>Standish: a (а.е.), e, I, L, ϖ, Ω (град) и скорости их изменения за юлианское столетие.</summary>
        public double[] Elements, Rates;
        public double CircularRadius, CircularPhase0;
        public bool Retrograde;
        /// <summary>
        /// Спутник, с которым тело образует заметный барицентр (Земля — Луна). Эфемерида Standish даёт
        /// барицентр, а Земля смещена от него на ~4700 км — учитываем, иначе Луна «плавает».
        /// </summary>
        public CelestialBody BarycenterSatellite;

        // Состояние на момент StateTime.
        public double StateTime = double.NaN;
        public Vector3d Position, Velocity;
        public Vector3d LocalPosition, LocalVelocity;
        public QuaternionD Orientation = QuaternionD.identity;
        public Vector3d AngularVelocity;
        public KeplerOrbit Orbit;

        public bool HasAtmosphere => Atmosphere != null;
        public double AtmosphereTop => Atmosphere?.Top ?? 0;

        public override string ToString() => Name;

        // ---------------------------------------------------------------- орбита

        /// <summary>Орбита относительно родителя на момент t (для Standish элементы дрейфуют со временем).</summary>
        public KeplerOrbit OrbitAt(double t)
        {
            switch (OrbitModel)
            {
                case OrbitModelType.Standish:
                {
                    double T = t / Constants.JulianCentury;
                    double a = (Elements[0] + Rates[0] * T) * Constants.AU;
                    double e = Elements[1] + Rates[1] * T;
                    double inc = Elements[2] + Rates[2] * T;
                    double L = Elements[3] + Rates[3] * T;
                    double peri = Elements[4] + Rates[4] * T;
                    double node = Elements[5] + Rates[5] * T;
                    double d2r = Constants.Deg2Rad;
                    return KeplerOrbit.FromElements(Parent.Mu + Mu, a, e, inc * d2r, node * d2r, (peri - node) * d2r,
                        MathD.WrapPi((L - peri) * d2r), t);
                }
                case OrbitModelType.Schlyter:
                {
                    // У Шлайтера d = 0 в 2000-01-00 0h, это на 1,5 суток раньше J2000.
                    double d = t / Constants.Day + 1.5;
                    double node = 125.1228 - 0.0529538083 * d;
                    double w = 318.0634 + 0.1643573223 * d;
                    double m = 115.3654 + 13.0649929509 * d;
                    double d2r = Constants.Deg2Rad;
                    return KeplerOrbit.FromElements(Parent.Mu + Mu, 384399e3, 0.0549, 5.1454 * d2r, node * d2r,
                        w * d2r, MathD.WrapPi(m * d2r), t);
                }
                case OrbitModelType.Circular:
                {
                    CircularState(t, out var r, out var v);
                    return KeplerOrbit.FromState(r, v, Parent.Mu + Mu, t);
                }
                default:
                    return null;
            }
        }

        /// <summary>Положение и скорость относительно родителя на момент t. Чистая функция — не трогает состояние.</summary>
        public void LocalStateAt(double t, out Vector3d r, out Vector3d v)
        {
            if (OrbitModel == OrbitModelType.Fixed)
            {
                r = v = Vector3d.zero;
                return;
            }
            if (OrbitModel == OrbitModelType.Circular)
            {
                CircularState(t, out r, out v);
                return;
            }
            OrbitAt(t).GetState(t, out r, out v);
            if (BarycenterSatellite != null)
            {
                BarycenterSatellite.LocalStateAt(t, out var rs, out var vs);
                double k = BarycenterSatellite.Mu / (Mu + BarycenterSatellite.Mu);
                r -= rs * k;
                v -= vs * k;
            }
        }

        void CircularState(double t, out Vector3d r, out Vector3d v)
        {
            var pole = Parent.PoleAt(t);
            var node = EquatorNode(pole);
            var side = Vector3d.Cross(pole, node);
            double n = Math.Sqrt((Parent.Mu + Mu) / (CircularRadius * CircularRadius * CircularRadius));
            if (Retrograde) n = -n;
            double th = CircularPhase0 + n * t;
            double c = Math.Cos(th), s = Math.Sin(th);
            r = (node * c + side * s) * CircularRadius;
            v = (node * -s + side * c) * (CircularRadius * n);
        }

        /// <summary>Восходящий узел экватора на эклиптике — опорное направление в плоскости экватора.</summary>
        static Vector3d EquatorNode(Vector3d pole)
        {
            var node = Vector3d.Cross(Vector3d.forward, pole);
            return node.sqrMagnitude < 1e-12 ? Vector3d.right : node.normalized;
        }

        /// <summary>Гелиоцентрическое положение на момент t (сумма по цепочке родителей).</summary>
        public Vector3d PositionAt(double t)
        {
            if (Parent == null) return Vector3d.zero;
            LocalStateAt(t, out var r, out _);
            return Parent.PositionAt(t) + r;
        }

        public void StateAt(double t, out Vector3d r, out Vector3d v)
        {
            if (Parent == null)
            {
                r = v = Vector3d.zero;
                return;
            }
            LocalStateAt(t, out r, out v);
            Parent.StateAt(t, out var pr, out var pv);
            r += pr;
            v += pv;
        }

        // ---------------------------------------------------------------- вращение

        /// <summary>Северный полюс вращения в системе P на момент t.</summary>
        public Vector3d PoleAt(double t)
        {
            double T = t / Constants.JulianCentury;
            double ra = (PoleRa0 + PoleRaRate * T) * Constants.Deg2Rad;
            double dec = (PoleDec0 + PoleDecRate * T) * Constants.Deg2Rad;
            var eq = new Vector3d(Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
            return EquatorialToEcliptic(eq);
        }

        public static Vector3d EquatorialToEcliptic(Vector3d v)
        {
            double c = Math.Cos(Constants.Obliquity), s = Math.Sin(Constants.Obliquity);
            return new Vector3d(v.x, v.y * c + v.z * s, -v.y * s + v.z * c);
        }

        /// <summary>Скорость вращения вокруг полюса, рад/с (отрицательная у Венеры и Урана).</summary>
        public double RotationRate
        {
            get
            {
                if (TidallyLocked && Parent != null)
                {
                    double n = Math.Sqrt((Parent.Mu + Mu) / Math.Pow(CircularRadius, 3));
                    return Retrograde ? -n : n;
                }
                return WRate * Constants.Deg2Rad / Constants.Day;
            }
        }

        /// <summary>
        /// Ориентация «тело → P» на момент t. Оси тела: z — северный полюс, x — нулевой меридиан.
        /// IAU: R = Rx(−ε) · Rz(α0 + 90°) · Rx(90° − δ0) · Rz(W).
        /// </summary>
        public QuaternionD OrientationAt(double t)
        {
            if (TidallyLocked && OrbitModel == OrbitModelType.Circular)
            {
                CircularState(t, out var r, out _);
                var z = Parent.PoleAt(t);
                var x = (-r).normalized;
                return QuaternionD.FromBasis(x, Vector3d.Cross(z, x), z);
            }
            double T = t / Constants.JulianCentury;
            double d = t / Constants.Day;
            double d2r = Constants.Deg2Rad;
            double ra = (PoleRa0 + PoleRaRate * T) * d2r;
            double dec = (PoleDec0 + PoleDecRate * T) * d2r;
            double w = MathD.Wrap2Pi((W0 + WRate * d) * d2r);
            return QuaternionD.AngleAxis(-Constants.Obliquity, Vector3d.right)
                   * QuaternionD.AngleAxis(ra + Math.PI / 2, Vector3d.forward)
                   * QuaternionD.AngleAxis(Math.PI / 2 - dec, Vector3d.right)
                   * QuaternionD.AngleAxis(w, Vector3d.forward);
        }

        // ---------------------------------------------------------------- поверхность

        /// <summary>Скорость точки поверхности (или атмосферы), вращающейся вместе с телом.</summary>
        public Vector3d SurfaceVelocity(Vector3d localPos) => Vector3d.Cross(AngularVelocity, localPos);

        public static Vector3d LatLonToBodyFixed(double latDeg, double lonDeg)
        {
            double la = latDeg * Constants.Deg2Rad, lo = lonDeg * Constants.Deg2Rad;
            return new Vector3d(Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la));
        }

        public static void BodyFixedToLatLon(Vector3d dir, out double latDeg, out double lonDeg)
        {
            var n = dir.normalized;
            latDeg = Math.Asin(MathD.Clamp(n.z, -1, 1)) * Constants.Rad2Deg;
            lonDeg = Math.Atan2(n.y, n.x) * Constants.Rad2Deg;
        }

        /// <summary>Высота рельефа над радиусом в направлении dir (система тела), м. Над океаном — 0.</summary>
        public double SurfaceHeight(Vector3d dirBodyFixed) => Terrain == null ? 0 : Kare.Space.Core.Terrain.Height(this, dirBodyFixed);

        /// <summary>Высота над рельефом для точки localPos (P, от центра тела) при текущей ориентации.</summary>
        public double AltitudeAboveTerrain(Vector3d localPos)
        {
            var bf = Orientation.Inverse * localPos;
            return localPos.magnitude - Radius - SurfaceHeight(bf);
        }
    }
}
