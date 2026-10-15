using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Программы вычислителя для возврата к планете от её спутника, коррекции перигея и перелёта к
    /// планетам (GDD §6.4, §6.11). Как и перехват спутника — перебор сетки, затем покоординатный спуск
    /// по (момент, разгон, боковой импульс); точный итог с притяжением цели даёт PatchedConics.Predict.
    /// </summary>
    public static partial class TransferPlanner
    {
        /// <summary>Сетка разгона к Земле с окололунной орбиты, м/с: 600 — едва уйти от Луны, 1600 — быстрый возврат.</summary>
        const double ReturnDvMin = 600, ReturnDvMax = 1600, ReturnDvStep = 25;
        const int ReturnTimeSamples = 72;
        /// <summary>Цена Δv в метрах ошибки перигея: 1 м/с = 1 км, точность доберёт коррекция на трассе.</summary>
        const double DvWeight = 1000;
        /// <summary>Цена времени перелёта, м за секунду: сутки ≈ 26 м/с Δv — экипажу быстрее, но не любой ценой.</summary>
        const double TimeWeight = 0.3;
        /// <summary>Штраф за траекторию, которая не уходит от спутника или не замыкается вокруг планеты.</summary>
        const double NoReturnCost = 1e12;
        /// <summary>Перебор коррекции перигея: до ±PerigeeFixMax м/с шагом PerigeeFixStep.</summary>
        const double PerigeeFixMax = 150, PerigeeFixStep = 2;

        /// <summary>
        /// Разгон от спутника (Луны) к его планете с заданным радиусом перигея — для входа в атмосферу
        /// (коридор «Аполлона» — 40–50 км над Землёй).
        /// </summary>
        public static TransferPlan PlanReturn(CelestialBody moon, Vector3d r, Vector3d v, double t0, double targetPeRadius)
        {
            var orbit = KeplerOrbit.FromState(r, v, moon.Mu, t0);
            if (!orbit.IsElliptic || moon.Parent == null) return null;
            double period = orbit.Period, dt = period / ReturnTimeSamples;
            Func<TransferPlan, double> cost = p => ReturnCost(orbit, moon, t0, targetPeRadius, p);

            var grid = new List<(TransferPlan plan, double cost)>();
            for (int i = 0; i < ReturnTimeSamples; i++)
                for (double dv = ReturnDvMin; dv <= ReturnDvMax; dv += ReturnDvStep)
                {
                    var p = new TransferPlan { Time = t0 + 120 + i * dt, Prograde = dv };
                    grid.Add((p, cost(p)));
                }
            grid.Sort((a, b) => a.cost.CompareTo(b.cost));
            if (grid.Count == 0 || grid[0].cost >= NoReturnCost) return null;

            TransferPlan best = null;
            double bestCost = double.PositiveInfinity;
            for (int s = 0; s < Math.Min(Starts, grid.Count); s++)
            {
                var p = DescendBy(cost, grid[s].plan, grid[s].cost, new[] { dt / 2, ReturnDvStep / 2, 20 }, t0 + 60, out double c);
                if (c < bestCost)
                {
                    bestCost = c;
                    best = p;
                }
            }
            cost(best);
            return bestCost < NoReturnCost ? best : null;
        }

        static double ReturnCost(KeplerOrbit orbit, CelestialBody moon, double t0, double targetPe, TransferPlan p)
        {
            var planet = moon.Parent;
            var node = new ManeuverNode { Time = p.Time, Prograde = p.Prograde, Normal = p.Normal, Radial = p.Radial };
            orbit.GetState(p.Time, out var r, out var v);
            var hy = KeplerOrbit.FromState(r, v + node.WorldDeltaV(orbit), orbit.Mu, p.Time);
            double tx = hy.NextTimeAtRadius(moon.SoiRadius, p.Time, true);
            if (double.IsNaN(tx)) return NoReturnCost + p.DeltaV;
            hy.GetState(tx, out var rs, out var vs);
            moon.LocalStateAt(tx, out var rm, out var vm);
            var back = KeplerOrbit.FromState(rm + rs, vm + vs, planet.Mu, tx);
            if (!back.IsElliptic) return NoReturnCost + p.DeltaV;
            p.Miss = back.PeriapsisRadius;
            p.ArrivalTime = tx + back.TimeToPeriapsis(tx);
            return Math.Abs(p.Miss - targetPe) + DvWeight * p.DeltaV + TimeWeight * (p.ArrivalTime - t0);
        }

        /// <summary>
        /// Коррекция перигея в момент tBurn наименьшим импульсом: сначала радиальным (на трассе возврата
        /// скорость почти по радиусу — перигей двигает поперечная составляющая), затем по скорости.
        /// </summary>
        public static TransferPlan PlanPerigee(CelestialBody body, Vector3d r, Vector3d v, double t, double targetPe, double tBurn)
        {
            var orbit = KeplerOrbit.FromState(r, v, body.Mu, t);
            for (int axis = 0; axis < 2; axis++)
            {
                Func<double, TransferPlan> make = d => new TransferPlan { Time = tBurn, Radial = axis == 0 ? d : 0, Prograde = axis == 1 ? d : 0 };
                Func<double, double> f = d => PerigeeAfter(orbit, make(d)) - targetPe;
                double f0 = f(0);
                if (Math.Abs(f0) < 1) return make(0);
                // Ближайшая к нулю смена знака по обе стороны.
                for (double d = PerigeeFixStep; d <= PerigeeFixMax; d += PerigeeFixStep)
                    for (int s = -1; s <= 1; s += 2)
                    {
                        double a = s * (d - PerigeeFixStep), b = s * d;
                        double fa = f(a), fb = f(b);
                        if (double.IsNaN(fa) || double.IsNaN(fb) || Math.Sign(fa) == Math.Sign(fb)) continue;
                        for (int i = 0; i < 50; i++)
                        {
                            double m = 0.5 * (a + b), fm = f(m);
                            if (Math.Sign(fm) == Math.Sign(fa)) { a = m; fa = fm; }
                            else b = m;
                        }
                        var plan = make(0.5 * (a + b));
                        plan.Miss = PerigeeAfter(orbit, plan);
                        return plan;
                    }
            }
            return null;
        }

        static double PerigeeAfter(KeplerOrbit orbit, TransferPlan p)
        {
            var node = new ManeuverNode { Time = p.Time, Prograde = p.Prograde, Normal = p.Normal, Radial = p.Radial };
            orbit.GetState(p.Time, out var r, out var v);
            var o = KeplerOrbit.FromState(r, v + node.WorldDeltaV(orbit), orbit.Mu, p.Time);
            if (o.IsElliptic) p.ArrivalTime = p.Time + o.TimeToPeriapsis(p.Time);
            return o.PeriapsisRadius;
        }

        // ------------------------------------------------------------- к планетам

        /// <summary>Сетка «свиной отбивной»: отрезков окна и шагов времени перелёта (0,6–1,4 гомановского).</summary>
        const int PorkDepartures = 120, PorkTofs = 17;
        const double PorkTofMin = 0.6, PorkTofMax = 1.4;
        /// <summary>Цена Δv в метрах промаха у планеты: 1 м/с ≈ 100 км — промах в тысячи км дешевле лишнего разгона.</summary>
        const double PlanetDvPenalty = 1e5;
        /// <summary>Захват в оценке окна — эллипс с апоцентром на этой доле SOI цели.
        /// Пара: MapView.CaptureApoSoi (та же оценка в меню перелётов).</summary>
        const double PorkCaptureApoSoi = 0.5;
        /// <summary>Горизонт поиска сближения для коррекции, лет — как прогноз вокруг Солнца (PatchedConics.HelioHorizonYears).</summary>
        const double HelioCorrectionYears = 40;

        /// <summary>Синодический период двух тел одного родителя, с: через столько повторяется окно старта.</summary>
        public static double SynodicPeriod(CelestialBody a, CelestialBody b, double t)
        {
            double pa = Period(a, t), pb = Period(b, t);
            return 1 / Math.Abs(1 / pa - 1 / pb);
        }

        static double Period(CelestialBody b, double t)
        {
            b.LocalStateAt(t, out var r, out var v);
            return KeplerOrbit.FromState(r, v, b.Parent.Mu, t).Period;
        }

        /// <summary>
        /// Разгон с опорной орбиты планеты к другой планете (Земля → Марс): окно по задаче Ламберта
        /// вокруг Солнца (наименьшая сумма гиперболических избытков на отлёте и прилёте), момент на витке —
        /// чтобы асимптота гиперболы ушла по нужному избытку, затем спуск по промаху у цели.
        /// </summary>
        /// <param name="targetMiss">Желаемое расстояние от центра цели без учёта её притяжения, м.</param>
        /// <param name="window">Сколько вперёд искать окно, с; по умолчанию — синодический период.</param>
        public static TransferPlan PlanInterplanetary(CelestialBody body, Vector3d r, Vector3d v, double t0,
            CelestialBody target, double targetMiss, double window = 0)
        {
            var sun = body.Parent;
            if (sun == null || target.Parent != sun || target == body) return null;
            var orbit = KeplerOrbit.FromState(r, v, body.Mu, t0);
            if (!orbit.IsElliptic) return null;
            if (window <= 0) window = SynodicPeriod(body, target, t0);

            body.LocalStateAt(t0, out var rb0, out var vb0);
            target.LocalStateAt(t0, out var rt0, out _);
            double aH = 0.5 * (rb0.magnitude + rt0.magnitude);
            double tH = Math.PI * Math.Sqrt(aH * aH * aH / sun.Mu);
            var pro = Vector3d.Cross(rb0, vb0).normalized;
            // Цена окна — Δv с ЭТОЙ опорной орбиты, а не сумма избытков: разгон у перицентра (Оберт) с поворотом
            // на угол β асимптоты к плоскости орбиты, плюс захват у цели. Сумма |v∞| выбирала к Сатурну переход на 160°
            // с асимптотой 48° к экваториальной орбите: узел 13,9 км/с вместо 8,1 (замер 15.10.2026, вылет с 300 км).
            var hPark = Vector3d.Cross(r, v).normalized;
            double rq = orbit.PeriapsisRadius, vq = Math.Sqrt(body.Mu * (2 / rq - 1 / orbit.A));
            double rqT = Math.Max(targetMiss, target.Radius * 1.05);
            double raT = Math.Max(rqT, PorkCaptureApoSoi * target.SoiRadius);
            double vqT = Math.Sqrt(2 * target.Mu * raT / (rqT * (rqT + raT)));

            double bestPork = double.PositiveInfinity, dep = double.NaN, tof = double.NaN;
            Vector3d vInf = Vector3d.zero;
            double depStep = window / PorkDepartures;
            for (int i = 0; i <= PorkDepartures; i++)
            {
                double td = t0 + orbit.Period + i * depStep;
                body.LocalStateAt(td, out var rb, out var vb);
                for (int j = 0; j < PorkTofs; j++)
                {
                    double tf = tH * (PorkTofMin + (PorkTofMax - PorkTofMin) * j / (PorkTofs - 1));
                    target.LocalStateAt(td + tf, out var rt, out var vt);
                    if (!Lambert.Solve(rb, rt, tf, sun.Mu, pro, out var v1, out var v2)) continue;
                    var vi = v1 - vb;
                    double vinf = vi.magnitude, va = (v2 - vt).magnitude;
                    double sinB = vinf > 0 ? Math.Min(1, Math.Abs(Vector3d.Dot(vi, hPark)) / vinf) : 0;
                    double vp = Math.Sqrt(vinf * vinf + 2 * body.Mu / rq);
                    double depart = Math.Sqrt(Math.Max(0, vq * vq + vp * vp - 2 * vq * vp * Math.Sqrt(1 - sinB * sinB)));
                    double capture = Math.Sqrt(va * va + 2 * target.Mu / rqT) - vqT;
                    double c = depart + capture;
                    if (c < bestPork)
                    {
                        bestPork = c;
                        dep = td;
                        tof = tf;
                        vInf = v1 - vb;
                    }
                }
            }
            if (double.IsNaN(dep)) return null;
            return PlanDeparture(body, r, v, t0, vInf, dep, target, targetMiss, tof);
        }

        /// <summary>
        /// Разгон с опорной орбиты к заданному гиперболическому избытку vInf, когда окно уже выбрано: dep — конец витка
        /// старта, tof — перелёт до цели. Момент на витке и импульс по асимптоте, затем спуск по промаху у цели.
        /// Отдельно от PlanInterplanetary: «Вояджеру» окно задаёт тур с гравманёврами (TourPlanner), а не минимум Δv.
        /// </summary>
        public static TransferPlan PlanDeparture(CelestialBody body, Vector3d r, Vector3d v, double t0, Vector3d vInf,
            double dep, CelestialBody target, double targetMiss, double tof)
        {
            var sun = body.Parent;
            var orbit = KeplerOrbit.FromState(r, v, body.Mu, t0);
            if (sun == null || !orbit.IsElliptic) return null;

            // Момент на витке: перебор за виток до окна, импульс по скорости до нужного избытка.
            double period = orbit.Period, dt = period / 180;
            Func<TransferPlan, double> aim = p =>
            {
                if (!Escape(orbit, body, p, out var tx, out var vh, out _)) return NoReturnCost;
                body.LocalStateAt(tx, out _, out var vb);
                return (vh - (vb + vInf)).magnitude;
            };
            TransferPlan start = null;
            double startCost = double.PositiveInfinity;
            for (double tb = dep - period; tb <= dep; tb += dt)
            {
                if (tb < t0 + 120) continue;
                orbit.GetState(tb, out var rp, out var vp);
                double need = Math.Sqrt(vInf.sqrMagnitude + 2 * body.Mu / rp.magnitude) - vp.magnitude;
                var p = new TransferPlan { Time = tb, Prograde = need };
                double c = aim(p);
                if (c < startCost)
                {
                    startCost = c;
                    start = p;
                }
            }
            if (start == null) return null;
            // Сначала — вектор избытка из решения Ламберта (гладкая цель, все четыре координаты, включая
            // радиальную: асимптота вне плоскости опорной орбиты), потом — промах у цели. Сразу по промаху спуск
            // застревал: боковой импульс 1,2 км/с, промах 420 тыс. км.
            start = DescendBy(aim, start, startCost, new[] { dt / 2, 20, 20, 20 }, t0 + 60, out _);

            double span = tof * 1.5;
            Func<TransferPlan, double> cost = p =>
            {
                if (!Escape(orbit, body, p, out var tx, out var vh, out var rh)) return NoReturnCost;
                var helio = KeplerOrbit.FromState(rh, vh, sun.Mu, tx);
                p.Miss = ClosestApproach(helio, target, tx, span, out p.ArrivalTime);
                return Math.Abs(p.Miss - targetMiss) + PlanetDvPenalty * p.DeltaV;
            };
            var best = DescendBy(cost, start, cost(start), new[] { dt / 8, 2, 2, 2 }, t0 + 60, out _);
            cost(best);
            return best;
        }

        /// <summary>Коррекция: импульс через столько секунд — успеть развернуться (пара: Universe.NodeWarpMargin).</summary>
        const double CorrectionLead = 3600;
        /// <summary>Перицентр у цели после коррекции, в радиусах: 3 R — снаружи колец Сатурна (2,3 R).</summary>
        const double CorrectionPeRadii = 3;
        const int CorrectionSamples = 200;

        /// <summary>
        /// Коррекция на перелёте (GDD §6.11): с орбиты вокруг родителя цели (Солнце → Сатурн) — импульс через
        /// CorrectionLead, Ламберт до точки прицела у цели с наименьшим Δv по времени прибытия (0,5–1,5 текущего).
        /// Без неё 8-минутный разгон Р-7 к Сатурну промахивался мимо сферы, а автоплан отвечал «с замкнутой орбиты».
        /// Прицел — прицельная дальность под перицентр CorrectionPeRadii с учётом фокусировки притяжением цели,
        /// с той же стороны, с какой корабль проходит сейчас.
        /// </summary>
        public static TransferPlan PlanCorrection(CelestialBody body, Vector3d r, Vector3d v, double t0, CelestialBody target) =>
            PlanCorrection(body, r, v, t0, target, (ta, rel, vrel, h) =>
            {
                // Сторона и скорость пролёта — по текущей траектории.
                var u = vrel.normalized;
                var side = rel - u * Vector3d.Dot(rel, u);
                if (side.sqrMagnitude < 1) side = Vector3d.Cross(u, h);
                double rp = CorrectionPeRadii * target.Radius, vinf2 = Math.Max(vrel.sqrMagnitude, 1);
                return side.normalized * (rp * Math.Sqrt(1 + 2 * target.Mu / (rp * vinf2)));
            }, CorrectionLead, 1);

        /// <summary>
        /// Точка прицела у цели в момент прибытия ta по прогнозу: rel — где корабль относительно цели,
        /// vrel — скорость относительно неё, h — нормаль орбиты корабля. Возвращает смещение от центра цели, м.
        /// </summary>
        public delegate Vector3d AimPoint(double ta, Vector3d rel, Vector3d vrel, Vector3d h);

        /// <summary>
        /// Коррекция к произвольной точке прицела (картинная плоскость гравманёвра — TourPlanner). passes > 1 —
        /// точка пересчитывается по траектории ПОСЛЕ коррекции: от неё зависит избыток у цели, а с ним и прицел.
        /// lead — через сколько импульс: прожиг зонда с ЖРД по 0,9 Н идёт десятки минут, его центр должен успеть.
        /// </summary>
        public static TransferPlan PlanCorrection(CelestialBody body, Vector3d r, Vector3d v, double t0, CelestialBody target,
            AimPoint aim, double lead, int passes)
        {
            if (target.Parent != body) return null;
            var orbit = KeplerOrbit.FromState(r, v, body.Mu, t0);
            double tc = t0 + lead;
            orbit.GetState(tc, out var rc, out var vc);
            double horizon = HelioCorrectionYears * 365.25 * 86400;
            double span = orbit.IsElliptic ? Math.Min(orbit.Period, horizon) : horizon;
            ClosestApproach(orbit, target, tc, span, out double ta0);
            double tof0 = ta0 - tc;
            if (tof0 < lead) return null;

            var h = Vector3d.Cross(rc, vc).normalized;
            target.LocalStateAt(ta0, out var rt0, out var vt0);
            orbit.GetState(ta0, out var rs0, out var vs0);
            var offset = aim(ta0, rs0 - rt0, vs0 - vt0, h);
            TransferPlan plan = null;
            for (int pass = 0; pass < passes; pass++)
            {
                plan = SolveCorrection(body, target, rc, vc, h, tc, tof0, offset, out double bestT, out var dv);
                if (plan == null || pass + 1 >= passes) break;
                var after = KeplerOrbit.FromState(rc, vc + dv, body.Mu, tc);
                target.LocalStateAt(bestT, out var rt, out var vt);
                after.GetState(bestT, out var rs, out var vs);
                offset = aim(bestT, rs - rt, vs - vt, h);
            }
            return plan;
        }

        /// <summary>Ламберт из (rc, vc) в точку «цель + offset» с наименьшим Δv по времени прибытия (0,5–1,5 tof0).</summary>
        static TransferPlan SolveCorrection(CelestialBody body, CelestialBody target, Vector3d rc, Vector3d vc, Vector3d h,
            double tc, double tof0, Vector3d offset, out double bestT, out Vector3d dvBest)
        {
            dvBest = Vector3d.zero;
            Func<double, Vector3d?> solve = ta =>
            {
                target.LocalStateAt(ta, out var rt, out _);
                if (!Lambert.Solve(rc, rt + offset, ta - tc, body.Mu, h, out var v1, out _)) return null;
                return v1 - vc;
            };
            double lo = tc + 0.5 * tof0, hi = tc + 1.5 * tof0, step = (hi - lo) / CorrectionSamples;
            bestT = double.NaN;
            double best = double.PositiveInfinity;
            for (int i = 0; i <= CorrectionSamples; i++)
            {
                double ta = lo + i * step;
                var dv = solve(ta);
                if (dv.HasValue && dv.Value.magnitude < best) { best = dv.Value.magnitude; bestT = ta; }
            }
            if (double.IsNaN(bestT)) return null;
            // Уточнение золотым сечением в соседних шагах сетки.
            double a = bestT - step, b = bestT + step;
            const double g = 0.6180339887498949;
            for (int i = 0; i < 50 && b - a > 1; i++)
            {
                double x1 = b - g * (b - a), x2 = a + g * (b - a);
                var d1 = solve(x1); var d2 = solve(x2);
                double f1 = d1.HasValue ? d1.Value.magnitude : double.PositiveInfinity;
                double f2 = d2.HasValue ? d2.Value.magnitude : double.PositiveInfinity;
                if (f1 < f2) b = x2; else a = x1;
                if (Math.Min(f1, f2) < best) { best = Math.Min(f1, f2); bestT = f1 < f2 ? x1 : x2; }
            }
            dvBest = solve(bestT).Value;
            var pro = vc.normalized;
            var rad = Vector3d.Cross(pro, h);
            var plan = new TransferPlan
            {
                Time = tc,
                Prograde = Vector3d.Dot(dvBest, pro),
                Normal = Vector3d.Dot(dvBest, h),
                Radial = Vector3d.Dot(dvBest, rad),
            };
            var after = KeplerOrbit.FromState(rc, vc + dvBest, body.Mu, tc);
            plan.Miss = ClosestApproach(after, target, tc, 1.5 * (bestT - tc), out plan.ArrivalTime);
            return plan;
        }

        /// <summary>Гипербола ухода после импульса: момент выхода из сферы влияния и состояние относительно родителя.</summary>
        static bool Escape(KeplerOrbit orbit, CelestialBody body, TransferPlan p, out double tx, out Vector3d vh, out Vector3d rh)
        {
            var node = new ManeuverNode { Time = p.Time, Prograde = p.Prograde, Normal = p.Normal, Radial = p.Radial };
            orbit.GetState(p.Time, out var r, out var v);
            var hy = KeplerOrbit.FromState(r, v + node.WorldDeltaV(orbit), orbit.Mu, p.Time);
            tx = hy.NextTimeAtRadius(body.SoiRadius, p.Time, true);
            vh = rh = Vector3d.zero;
            if (double.IsNaN(tx)) return false;
            hy.GetState(tx, out var rs, out var vs);
            body.LocalStateAt(tx, out var rb, out var vb);
            rh = rb + rs;
            vh = vb + vs;
            return true;
        }

        /// <summary>Покоординатный спуск по (момент, разгон, боковой, [радиальный]) — по длине step — с произвольной ценой.</summary>
        static TransferPlan DescendBy(Func<TransferPlan, double> cost, TransferPlan start, double startCost, double[] step,
            double earliest, out double bestCost)
        {
            var best = start;
            bestCost = startCost;
            step = (double[])step.Clone();
            int n = step.Length;
            for (int iter = 0; iter < 600 && (step[0] > 0.05 || step[1] > 0.01); iter++)
            {
                bool improved = false;
                for (int k = 0; k < n; k++)
                    for (int s = -1; s <= 1; s += 2)
                    {
                        var p = Shift4(best, k, s * step[k]);
                        if (p.Time < earliest) continue;
                        double c = cost(p);
                        if (c < bestCost)
                        {
                            bestCost = c;
                            best = p;
                            improved = true;
                        }
                    }
                if (!improved)
                    for (int k = 0; k < n; k++) step[k] *= 0.5;
            }
            return best;
        }

        static TransferPlan Shift4(TransferPlan p, int k, double d) => new TransferPlan
        {
            Time = p.Time + (k == 0 ? d : 0),
            Prograde = p.Prograde + (k == 1 ? d : 0),
            Normal = p.Normal + (k == 2 ? d : 0),
            Radial = p.Radial + (k == 3 ? d : 0),
        };
    }
}
