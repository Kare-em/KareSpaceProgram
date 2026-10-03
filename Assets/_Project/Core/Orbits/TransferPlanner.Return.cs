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
                    double c = (v1 - vb).magnitude + (v2 - vt).magnitude;
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
