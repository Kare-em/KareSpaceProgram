using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Тур с гравманёврами (GDD §6.11, «Вояджеры»): цепочка перелётов Ламберта вокруг Солнца
    /// «дом → тело 1 → тело 2 → …» с заданным отлётом. Даты пролётов подбираются так, чтобы у каждого
    /// промежуточного тела избыток на входе и выходе совпадал по модулю (пролёт без двигателя меняет только
    /// направление v∞), а поворот укладывался в перицентр не ниже допустимого. Остаток — Δv с опорной орбиты.
    /// </summary>
    public sealed class FlybyTour
    {
        public CelestialBody Home;
        public double Departure;
        public CelestialBody[] Bodies;
        /// <summary>Моменты пролёта (у последнего тела — прибытие).</summary>
        public double[] Times;
        /// <summary>Избыток на отлёте относительно дома, м/с (вектор в осях P).</summary>
        public Vector3d VInfDepart;
        /// <summary>v∞ на входе и выходе у каждого тела относительно него; у последнего VOut — ноль.</summary>
        public Vector3d[] VIn, VOut;
        /// <summary>Радиус перицентра, нужный для поворота VIn → VOut, м; у последнего — NaN.</summary>
        public double[] Periapsis;
        /// <summary>Разгон с опорной орбиты (Оберт), м/с.</summary>
        public double DepartDv;
        /// <summary>Сумма рассогласований |v∞вых| − |v∞вх| и недоворотов — то, что пришлось бы добирать двигателем, м/с.</summary>
        public double Mismatch;
        public double Cost;

        /// <summary>
        /// Прицельный вектор (b) пролёта тела i для поворота vin к направлению vout: смещение от центра тела
        /// поперёк vin, м. Притяжение тянет к телу, значит поперечная часть vout смотрит ОТ точки прицела.
        /// rp — радиус перицентра; |b| = rp·√(1 + 2μ/(rp·v∞²)) — фокусировка притяжением.
        /// </summary>
        public static Vector3d AimVector(CelestialBody body, Vector3d vin, Vector3d vout, double rp)
        {
            var u = vin.normalized;
            var side = -(vout.normalized - u * Vector3d.Dot(u, vout.normalized));
            if (side.sqrMagnitude < 1e-18) side = Vector3d.AnyPerpendicular(u);
            double v2 = Math.Max(vin.sqrMagnitude, 1);
            return side.normalized * (rp * Math.Sqrt(1 + 2 * body.Mu / (rp * v2)));
        }

        /// <summary>Перицентр гиперболы с избытком vinf, поворачивающей на угол delta: e = 1/sin(δ/2).</summary>
        public static double TurnPeriapsis(CelestialBody body, double vinf, double delta)
        {
            double s = Math.Sin(delta / 2);
            if (s < 1e-9) return double.PositiveInfinity;
            return body.Mu / (vinf * vinf) * (1 / s - 1);
        }
    }

    public static class TourPlanner
    {
        /// <summary>Нормаль эклиптики: перелёты прямые (все планеты обращаются против часовой с её севера).</summary>
        static readonly Vector3d EclipticNorth = new Vector3d(0, 0, 1);
        /// <summary>Начальный шаг симплекса по датам, с (20 сут) и точность остановки (≈ 1 мин).</summary>
        const double SimplexStep = 20 * 86400, SimplexTol = 60;
        const int SimplexIterations = 4000;
        /// <summary>Перелёт короче этого не рассматриваем — Ламберт вырождается, с (30 сут).</summary>
        const double MinLeg = 30 * 86400;
        /// <summary>
        /// Привязка к историческим датам: штраф (Δt / HistoryScale)² м/с. Без неё симплекс уходил по долине
        /// нулевых рассогласований к дешёвому отлёту: «Вояджер-1» — Юпитер на 235 сут позже, C3 89 вместо ≈ 100,
        /// у «Вояджера-2» Нептун — через месяц после Урана (замер 15.10.2026). Наклон цены отлёта вдоль долины
        /// ≈ 2,5 м/с за сутки — при шкале 2 сут равновесие в пределах нескольких суток от истории.
        /// </summary>
        const double HistoryScale = 2 * 86400;

        /// <summary>
        /// Подобрать даты пролётов возле guess (исторические даты миссии — их окрестность, а не глобальный поиск:
        /// у тура много локальных минимумов, и нам нужен именно тот, которым летел «Вояджер»).
        /// </summary>
        /// <param name="parkRadius">Радиус опорной орбиты у дома, м — для цены отлёта с учётом Оберта.</param>
        /// <param name="minPeriapsis">Наименьший допустимый радиус перицентра у каждого тела, м.</param>
        public static FlybyTour Plan(CelestialBody home, double departure, CelestialBody[] bodies, double[] guess,
            double parkRadius, double[] minPeriapsis)
        {
            int n = bodies.Length;
            Func<double[], double> f = x =>
            {
                var t = Evaluate(home, departure, bodies, x, parkRadius, minPeriapsis);
                if (t == null) return double.PositiveInfinity;
                double pull = 0;
                for (int i = 0; i < n; i++) pull += Math.Pow((x[i] - guess[i]) / HistoryScale, 2);
                return t.Cost + pull;
            };
            var best = (double[])guess.Clone();
            // Перезапуски симплекса: он сжимается в долину рассогласований, новый старт выводит его дальше по ней.
            for (int restart = 0; restart < 4; restart++)
                best = NelderMead(f, best, restart == 0 ? SimplexStep : SimplexStep / 4);
            return Evaluate(home, departure, bodies, best, parkRadius, minPeriapsis);
        }

        /// <summary>Тур с заданными датами: перелёты Ламберта и цена. null — какой-то отрезок не решается.</summary>
        public static FlybyTour Evaluate(CelestialBody home, double departure, CelestialBody[] bodies, double[] times,
            double parkRadius, double[] minPeriapsis)
        {
            int n = bodies.Length;
            var sun = home.Parent;
            var tour = new FlybyTour
            {
                Home = home, Departure = departure, Bodies = bodies, Times = (double[])times.Clone(),
                VIn = new Vector3d[n], VOut = new Vector3d[n], Periapsis = new double[n],
            };
            double tPrev = departure;
            home.LocalStateAt(departure, out var rPrev, out var vPrevBody);
            var vDepart = Vector3d.zero;
            for (int i = 0; i < n; i++)
            {
                double tof = times[i] - tPrev;
                if (tof < MinLeg) return null;
                bodies[i].LocalStateAt(times[i], out var rb, out var vb);
                if (!Lambert.Solve(rPrev, rb, tof, sun.Mu, EclipticNorth, out var v1, out var v2)) return null;
                if (i == 0) vDepart = v1 - vPrevBody;
                else tour.VOut[i - 1] = v1 - vPrevBody;
                tour.VIn[i] = v2 - vb;
                rPrev = rb;
                vPrevBody = vb;
                tPrev = times[i];
            }
            tour.VInfDepart = vDepart;
            double vinf = vDepart.magnitude, vc = Math.Sqrt(home.Mu / parkRadius);
            tour.DepartDv = Math.Sqrt(vinf * vinf + 2 * home.Mu / parkRadius) - vc;

            double mismatch = 0;
            for (int i = 0; i < n; i++)
            {
                if (i == n - 1)
                {
                    tour.Periapsis[i] = double.NaN;
                    break;
                }
                var vin = tour.VIn[i];
                var vout = tour.VOut[i];
                double a = vin.magnitude, b = vout.magnitude;
                mismatch += Math.Abs(b - a);
                double delta = Vector3d.Angle(vin, vout);
                double v = 0.5 * (a + b);
                double rp = FlybyTour.TurnPeriapsis(bodies[i], v, delta);
                tour.Periapsis[i] = rp;
                if (rp < minPeriapsis[i])
                {
                    // Недоворот: сколько угла не даёт пролёт на минимальной высоте, — его Δv = 2v·sin(Δδ/2).
                    double e = 1 + minPeriapsis[i] * v * v / bodies[i].Mu;
                    double deltaMax = 2 * Math.Asin(1 / e);
                    mismatch += 2 * v * Math.Sin((delta - deltaMax) / 2);
                }
            }
            tour.Mismatch = mismatch;
            tour.Cost = tour.DepartDv + mismatch;
            return tour;
        }

        /// <summary>Симплекс Нелдера — Мида: рассогласования образуют узкие долины, покоординатный спуск в них застревает.</summary>
        static double[] NelderMead(Func<double[], double> f, double[] x0, double step)
        {
            int n = x0.Length;
            var pts = new double[n + 1][];
            var val = new double[n + 1];
            for (int i = 0; i <= n; i++)
            {
                pts[i] = (double[])x0.Clone();
                if (i > 0) pts[i][i - 1] += step;
                val[i] = f(pts[i]);
            }
            for (int iter = 0; iter < SimplexIterations; iter++)
            {
                Array.Sort(val, pts);
                double size = 0;
                for (int i = 1; i <= n; i++)
                    for (int k = 0; k < n; k++) size = Math.Max(size, Math.Abs(pts[i][k] - pts[0][k]));
                if (size < SimplexTol) break;
                var c = new double[n];
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < n; k++) c[k] += pts[i][k] / n;
                Func<double, double[]> along = s =>
                {
                    var p = new double[n];
                    for (int k = 0; k < n; k++) p[k] = c[k] + s * (pts[n][k] - c[k]);
                    return p;
                };
                var xr = along(-1);
                double fr = f(xr);
                if (fr < val[0])
                {
                    var xe = along(-2);
                    double fe = f(xe);
                    if (fe < fr) { pts[n] = xe; val[n] = fe; }
                    else { pts[n] = xr; val[n] = fr; }
                }
                else if (fr < val[n - 1]) { pts[n] = xr; val[n] = fr; }
                else
                {
                    var xc = fr < val[n] ? along(-0.5) : along(0.5);
                    double fc = f(xc);
                    if (fc < Math.Min(fr, val[n])) { pts[n] = xc; val[n] = fc; }
                    else
                        for (int i = 1; i <= n; i++)
                        {
                            for (int k = 0; k < n; k++) pts[i][k] = pts[0][k] + 0.5 * (pts[i][k] - pts[0][k]);
                            val[i] = f(pts[i]);
                        }
                }
            }
            Array.Sort(val, pts);
            return pts[0];
        }
    }
}
