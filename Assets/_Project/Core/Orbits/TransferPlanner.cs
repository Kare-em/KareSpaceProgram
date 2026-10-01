using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>Узел разгона к спутнику и чем он кончится по расчёту планировщика.</summary>
    public sealed class TransferPlan
    {
        public double Time, Prograde, Normal, Radial;
        /// <summary>Наименьшее расстояние от центра цели без учёта её притяжения, м.</summary>
        public double Miss;
        public double ArrivalTime;
        public double DeltaV => Math.Sqrt(Prograde * Prograde + Normal * Normal + Radial * Radial);
        public double TransferTime => ArrivalTime - Time;
    }

    /// <summary>
    /// Программа бортового вычислителя «Перелёт к спутнику» (GDD §6.4, §6.11): ищет момент и импульс
    /// разгона с опорной орбиты к спутнику текущего тела (Земля → Луна, Марс → Фобос).
    /// Сначала перебор моментов с импульсом в плоскости орбиты (гомановским или под заданное время
    /// перелёта), затем покоординатное уточнение момента, разгона и бокового импульса — от нескольких
    /// лучших окон, а не от одного: локальный спуск из чужого окна не выберется. Притяжение цели не
    /// учитывается — точный перицентр у цели даёт прогноз склейки коник (PatchedConics.Predict).
    /// </summary>
    public static class TransferPlanner
    {
        const int Samples = 300;
        /// <summary>Цена бокового импульса в метрах промаха: окно в плоскости орбиты дешевле поворота плоскости.</summary>
        const double NormalPenalty = 2000;
        /// <summary>Цена ожидания, м промаха за секунду: сутки ≈ 43 м/с бокового импульса — раньше лучше, но не любой ценой.</summary>
        const double WaitPenalty = 1;
        /// <summary>Цена превышения заданного времени перелёта, м промаха за секунду.</summary>
        const double LatePenalty = 2000;
        /// <summary>Сколько лучших окон перебора уточнять спуском.</summary>
        const int Starts = 6;

        /// <param name="targetMiss">Желаемый промах от центра цели, м. Для попадания — меньше радиуса с учётом фокусировки.</param>
        /// <param name="window">Сколько секунд вперёд искать момент разгона.</param>
        /// <param name="maxTransfer">Предельное время перелёта, с: быстрый перелёт дороже по Δv (Луна-3 — 2,5 сут).</param>
        public static TransferPlan PlanIntercept(CelestialBody body, Vector3d r, Vector3d v, double t0,
            CelestialBody target, double targetMiss, double window, double maxTransfer = double.PositiveInfinity)
        {
            var orbit = KeplerOrbit.FromState(r, v, body.Mu, t0);
            if (!orbit.IsElliptic || target.Parent != body) return null;
            var ctx = new Context { Orbit = orbit, Target = target, TargetMiss = targetMiss, T0 = t0, MaxTransfer = maxTransfer };

            double dt = orbit.Period / 90;
            var scan = new List<(TransferPlan plan, double cost)>();
            // Запас 120 с — успеть развернуться на манёвр.
            for (double t = t0 + 120; t <= t0 + 120 + window; t += dt)
            {
                var p = new TransferPlan { Time = t, Prograde = PlaneDv(orbit, t, target, maxTransfer) };
                scan.Add((p, Cost(ctx, p)));
            }
            if (scan.Count == 0) return null;

            var starts = new List<(TransferPlan plan, double cost)>();
            for (int i = 0; i < scan.Count; i++)
            {
                double c = scan[i].cost;
                if ((i == 0 || c < scan[i - 1].cost) && (i == scan.Count - 1 || c <= scan[i + 1].cost))
                    starts.Add(scan[i]);
            }
            starts.Sort((a, b) => a.cost.CompareTo(b.cost));

            TransferPlan best = null;
            double bestCost = double.PositiveInfinity;
            for (int s = 0; s < Math.Min(Starts, starts.Count); s++)
            {
                var p = Descend(ctx, starts[s].plan, starts[s].cost, dt, out double c);
                if (c < bestCost)
                {
                    bestCost = c;
                    best = p;
                }
            }
            Cost(ctx, best);
            return best;
        }

        sealed class Context
        {
            public KeplerOrbit Orbit;
            public CelestialBody Target;
            public double TargetMiss, T0, MaxTransfer;
        }

        static TransferPlan Descend(Context ctx, TransferPlan start, double startCost, double dt, out double bestCost)
        {
            var best = start;
            bestCost = startCost;
            double[] step = { dt / 2, 20, 20 };
            for (int iter = 0; iter < 400 && (step[0] > 0.05 || step[1] > 0.01 || step[2] > 0.01); iter++)
            {
                bool improved = false;
                for (int k = 0; k < 3; k++)
                    for (int s = -1; s <= 1; s += 2)
                    {
                        var p = Shift(best, k, s * step[k]);
                        if (p.Time < ctx.T0 + 60) continue;
                        double c = Cost(ctx, p);
                        if (c < bestCost)
                        {
                            bestCost = c;
                            best = p;
                            improved = true;
                        }
                    }
                if (!improved)
                    for (int k = 0; k < 3; k++) step[k] *= 0.5;
            }
            return best;
        }

        static TransferPlan Shift(TransferPlan p, int k, double d) => new TransferPlan
        {
            Time = p.Time + (k == 0 ? d : 0),
            Prograde = p.Prograde + (k == 1 ? d : 0),
            Normal = p.Normal + (k == 2 ? d : 0),
            Radial = p.Radial,
        };

        /// <summary>
        /// Разгон в плоскости: гомановский (апоцентр на расстоянии цели) или, при заданном времени
        /// перелёта, такой, чтобы дойти от перицентра до расстояния цели за это время.
        /// </summary>
        static double PlaneDv(KeplerOrbit orbit, double t, CelestialBody target, double tof)
        {
            orbit.GetState(t, out var r, out var v);
            target.LocalStateAt(t, out var rt, out _);
            double r1 = r.magnitude, r2 = rt.magnitude;
            double rApo = r2;
            if (!double.IsInfinity(tof) && TimeToRadius(orbit.Mu, r1, r2, r2) > tof)
            {
                // Чем дальше апоцентр, тем быстрее проходим r2: бисекция по апоцентру (в логарифме).
                double lo = r2, hi = 100 * r2;
                if (TimeToRadius(orbit.Mu, r1, hi, r2) > tof) rApo = hi;
                else
                {
                    for (int i = 0; i < 60; i++)
                    {
                        double mid = Math.Sqrt(lo * hi);
                        if (TimeToRadius(orbit.Mu, r1, mid, r2) > tof) lo = mid;
                        else hi = mid;
                    }
                    rApo = hi;
                }
            }
            double a = 0.5 * (r1 + rApo);
            return Math.Sqrt(orbit.Mu * (2 / r1 - 1 / a)) - v.magnitude;
        }

        /// <summary>Время от перицентра r1 до радиуса r2 по эллипсу с апоцентром rApo, с.</summary>
        static double TimeToRadius(double mu, double r1, double rApo, double r2)
        {
            double a = 0.5 * (r1 + rApo), e = (rApo - r1) / (rApo + r1);
            double cosE = MathD.Clamp((1 - r2 / a) / e, -1, 1);
            double E = Math.Acos(cosE);
            return (E - e * Math.Sin(E)) / Math.Sqrt(mu / (a * a * a));
        }

        static double Cost(Context ctx, TransferPlan p)
        {
            var orbit = ctx.Orbit;
            var node = new ManeuverNode { Time = p.Time, Prograde = p.Prograde, Normal = p.Normal, Radial = p.Radial };
            orbit.GetState(p.Time, out var r, out var v);
            var tr = KeplerOrbit.FromState(r, v + node.WorldDeltaV(orbit), orbit.Mu, p.Time);
            p.Miss = ClosestApproach(tr, ctx.Target, p.Time, out p.ArrivalTime);
            double late = Math.Max(0, p.TransferTime - ctx.MaxTransfer);
            return Math.Abs(p.Miss - ctx.TargetMiss) + NormalPenalty * Math.Abs(p.Normal) +
                   WaitPenalty * (p.Time - ctx.T0) + LatePenalty * late;
        }

        /// <summary>Наименьшее расстояние до цели на первом полувитке перелёта: грубая сетка + золотое сечение.</summary>
        public static double ClosestApproach(KeplerOrbit tr, CelestialBody target, double t0, out double tMin)
        {
            const double day = 86400;
            double span = tr.IsElliptic ? Math.Min(0.6 * tr.Period, 20 * day) : 5 * day;
            double h = span / Samples;
            int bestI = 0;
            double best = double.PositiveInfinity;
            for (int i = 0; i <= Samples; i++)
            {
                double d = Distance(tr, target, t0 + i * h);
                if (d < best)
                {
                    best = d;
                    bestI = i;
                }
            }
            double lo = t0 + Math.Max(0, bestI - 1) * h, hi = t0 + Math.Min(Samples, bestI + 1) * h;
            const double g = 0.6180339887498949;
            double a = hi - g * (hi - lo), b = lo + g * (hi - lo);
            double fa = Distance(tr, target, a), fb = Distance(tr, target, b);
            for (int i = 0; i < 60 && hi - lo > 0.5; i++)
            {
                if (fa < fb)
                {
                    hi = b; b = a; fb = fa;
                    a = hi - g * (hi - lo);
                    fa = Distance(tr, target, a);
                }
                else
                {
                    lo = a; a = b; fa = fb;
                    b = lo + g * (hi - lo);
                    fb = Distance(tr, target, b);
                }
            }
            tMin = 0.5 * (lo + hi);
            return Math.Min(best, Distance(tr, target, tMin));
        }

        static double Distance(KeplerOrbit tr, CelestialBody target, double t)
        {
            target.LocalStateAt(t, out var rt, out _);
            return (tr.PositionAt(t) - rt).magnitude;
        }
    }
}
