using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Кеплерова орбита (эллипс или гипербола), заданная перифокальным базисом: P — единичный вектор
    /// к перицентру, Q — на 90° вперёд по движению. Классические углы Ω и ω вырождаются на круговых
    /// и экваториальных орбитах, а у нас это норма (круговая опорная орбита, старт с экватора), поэтому
    /// храним базис, а углы считаем только для показа. Все векторы — в системе P (эклиптика J2000).
    /// </summary>
    public sealed class KeplerOrbit
    {
        /// <summary>Отступ от параболы: при e ровно 1 большая полуось уходит в бесконечность.</summary>
        const double ParabolicGuard = 1e-7;

        public double Mu;
        /// <summary>Большая полуось, м. У гиперболы отрицательна.</summary>
        public double A;
        public double E;
        public Vector3d P, Q;
        public double MeanAnomalyAtEpoch;
        public double Epoch;

        public Vector3d Normal => Vector3d.Cross(P, Q);
        /// <summary>Фокальный параметр p = a(1 − e²) &gt; 0 для обоих типов.</summary>
        public double SemiLatusRectum => A * (1 - E * E);
        public double PeriapsisRadius => A * (1 - E);
        public double ApoapsisRadius => E < 1 ? A * (1 + E) : double.PositiveInfinity;
        public double MeanMotion => Math.Sqrt(Mu / Math.Abs(A * A * A));
        public double Period => E < 1 ? Constants.TwoPi / MeanMotion : double.PositiveInfinity;
        public bool IsElliptic => E < 1;
        public double Energy => -Mu / (2 * A);

        public KeplerOrbit Clone() => (KeplerOrbit)MemberwiseClone();

        /// <summary>Орбита по вектору состояния (r, v) относительно центра тела в момент t.</summary>
        public static KeplerOrbit FromState(Vector3d r, Vector3d v, double mu, double t)
        {
            var h = Vector3d.Cross(r, v);
            double rm = r.magnitude;
            // Чисто радиальное движение (h = 0) не задаёт плоскость. Добавляем исчезающе малую
            // поперечную скорость — на траектории это не видно, а формулы остаются конечными.
            if (h.magnitude < 1e-9 * rm * Math.Max(v.magnitude, 1e-3))
            {
                v += Vector3d.AnyPerpendicular(r) * 1e-4;
                h = Vector3d.Cross(r, v);
            }
            var hn = h.normalized;
            double p = h.sqrMagnitude / mu;
            var ev = Vector3d.Cross(v, h) / mu - r / rm;
            double e = ev.magnitude;

            Vector3d pv;
            double nu;
            if (e < 1e-11)
            {
                // Круговая: перицентр не определён, ставим его в текущую точку.
                pv = r / rm;
                nu = 0;
                e = 0;
            }
            else
            {
                pv = ev / e;
                var qv0 = Vector3d.Cross(hn, pv);
                nu = Math.Atan2(Vector3d.Dot(r, qv0), Vector3d.Dot(r, pv));
            }
            if (Math.Abs(e - 1) < ParabolicGuard) e = e < 1 ? 1 - ParabolicGuard : 1 + ParabolicGuard;

            var o = new KeplerOrbit
            {
                Mu = mu,
                E = e,
                A = p / (1 - e * e),
                P = pv,
                Q = Vector3d.Cross(hn, pv),
                Epoch = t,
            };
            o.MeanAnomalyAtEpoch = KeplerSolver.TrueToMean(nu, e);
            return o;
        }

        /// <summary>Орбита по классическим элементам (углы в радианах), M — средняя аномалия на epoch.</summary>
        public static KeplerOrbit FromElements(double mu, double a, double e, double inc, double lan, double argPe,
            double meanAnomaly, double epoch)
        {
            double cO = Math.Cos(lan), sO = Math.Sin(lan);
            double cw = Math.Cos(argPe), sw = Math.Sin(argPe);
            double ci = Math.Cos(inc), si = Math.Sin(inc);
            return new KeplerOrbit
            {
                Mu = mu,
                A = a,
                E = e,
                P = new Vector3d(cO * cw - sO * sw * ci, sO * cw + cO * sw * ci, sw * si),
                Q = new Vector3d(-cO * sw - sO * cw * ci, -sO * sw + cO * cw * ci, cw * si),
                MeanAnomalyAtEpoch = meanAnomaly,
                Epoch = epoch,
            };
        }

        public double MeanAnomalyAt(double t)
        {
            double m = MeanAnomalyAtEpoch + MeanMotion * (t - Epoch);
            return E < 1 ? MathD.WrapPi(m) : m;
        }

        public double TrueAnomalyAt(double t) => KeplerSolver.MeanToTrue(MeanAnomalyAt(t), E);

        public double RadiusAtTrueAnomaly(double nu) => SemiLatusRectum / (1 + E * Math.Cos(nu));

        public Vector3d PositionAtTrueAnomaly(double nu)
        {
            double r = RadiusAtTrueAnomaly(nu);
            return P * (r * Math.Cos(nu)) + Q * (r * Math.Sin(nu));
        }

        public void StateAtTrueAnomaly(double nu, out Vector3d r, out Vector3d v)
        {
            double c = Math.Cos(nu), s = Math.Sin(nu);
            double p = SemiLatusRectum;
            double rr = p / (1 + E * c);
            r = P * (rr * c) + Q * (rr * s);
            double k = Math.Sqrt(Mu / p);
            v = P * (-k * s) + Q * (k * (E + c));
        }

        public void GetState(double t, out Vector3d r, out Vector3d v) => StateAtTrueAnomaly(TrueAnomalyAt(t), out r, out v);

        public Vector3d PositionAt(double t) => PositionAtTrueAnomaly(TrueAnomalyAt(t));

        /// <summary>
        /// Ближайший момент не раньше after, когда тело проходит истинную аномалию nu.
        /// Для гиперболы — NaN, если эта точка уже пройдена или недостижима.
        /// </summary>
        public double TimeOfTrueAnomaly(double nu, double after)
        {
            if (E >= 1)
            {
                double nuMax = Math.Acos(-1 / E);
                if (Math.Abs(nu) >= nuMax) return double.NaN;
                double t = Epoch + (KeplerSolver.TrueToMean(nu, E) - MeanAnomalyAtEpoch) / MeanMotion;
                return t >= after ? t : double.NaN;
            }
            double dm = MathD.Wrap2Pi(KeplerSolver.TrueToMean(nu, E) - MeanAnomalyAt(after));
            return after + dm / MeanMotion;
        }

        /// <summary>Истинная аномалия (≥ 0, восходящая ветвь), на которой радиус равен r; NaN, если не достигается.</summary>
        public double TrueAnomalyAtRadius(double r)
        {
            if (E < 1e-12) return double.NaN;
            double c = (SemiLatusRectum / r - 1) / E;
            if (c < -1 || c > 1) return double.NaN;
            return Math.Acos(c);
        }

        /// <summary>Ближайший момент после after, когда радиус пересекает r: outward — на удалении, иначе на сближении.</summary>
        public double NextTimeAtRadius(double r, double after, bool outward)
        {
            double nu = TrueAnomalyAtRadius(r);
            if (double.IsNaN(nu)) return double.NaN;
            return TimeOfTrueAnomaly(outward ? nu : -nu, after);
        }

        public double TimeToPeriapsis(double now) => TimeOfTrueAnomaly(0, now) - now;
        public double TimeToApoapsis(double now) => E < 1 ? TimeOfTrueAnomaly(Math.PI, now) - now : double.NaN;

        /// <summary>Наклонение относительно плоскости с нормалью pole (например, экватора тела), рад.</summary>
        public double InclinationTo(Vector3d pole) => Vector3d.Angle(Normal, pole);

        public override string ToString() =>
            $"a={A / 1000:F1} км e={E:F5} Pe={PeriapsisRadius / 1000:F1} км Ap={ApoapsisRadius / 1000:F1} км";
    }
}
