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
    public static partial class TransferPlanner
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
        /// <param name="periapsis">Желаемый перицентр у цели от центра, м (NaN — нет): доводка пересчитывает b по v∞ из
        /// прогноза. targetMiss по прикидке v∞ 1000 м/с при настоящих ≈ 800 м/с — это перицентр под поверхностью, доводка
        /// вела в удар, и перелёт к Луне на 100 км приходил на 499 км (autoplan, 15.10.2026).</param>
        public static TransferPlan PlanIntercept(CelestialBody body, Vector3d r, Vector3d v, double t0,
            CelestialBody target, double targetMiss, double window, double maxTransfer = double.PositiveInfinity,
            double periapsis = double.NaN)
        {
            var orbit = KeplerOrbit.FromState(r, v, body.Mu, t0);
            if (!orbit.IsElliptic || target.Parent != body) return null;
            var ctx = new Context { Orbit = orbit, Target = target, TargetMiss = targetMiss, T0 = t0, MaxTransfer = maxTransfer, Periapsis = periapsis };

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
                Cost(ctx, p);
                // Окончательный выбор — по склейке коник: прямая без притяжения цели у апогея (Луна догоняет
                // медленный аппарат) путает кандидатов. «Луна-9»: план «промах 1000 км», прогноз — Pe 13 400 км,
                // посадка мимо; соседний кандидат 60 м/с давал попадание (замер 15.10.2026).
                c += PredictedMissError(ctx, body, r, v, t0, p);
                if (c < bestCost)
                {
                    bestCost = c;
                    best = p;
                }
            }
            if (best != null) best = Polish(ctx, body, r, v, t0, best, dt);
            if (best != null) Cost(ctx, best);
            return best;
        }

        /// <summary>
        /// Расхождение прогноза склейки коник с желаемым промахом, м: перицентр у цели переводится в прицельную
        /// дальность b = rp·√(1 + 2μ/(rp·v∞²)) — в тех же единицах, что TargetMiss (без притяжения цели). Нет
        /// входа в сферу — штраф NoEncounterPenalty.
        /// </summary>
        static double PredictedMissError(Context ctx, CelestialBody body, Vector3d r, Vector3d v, double t0, TransferPlan p)
        {
            var node = new ManeuverNode { Time = p.Time, Prograde = p.Prograde, Normal = p.Normal, Radial = p.Radial };
            // Predict прибавляет Remaining (вектор в P), а не компоненты: без него прогноз шёл БЕЗ импульса, и доводка
            // крутила только штрафы ожидания и бокового импульса — «Луна-17» пришла с перицентром 13 562 км (15.10.2026).
            node.Remaining = node.WorldDeltaV(ctx.Orbit);
            var patch = PatchedConics.Predict(body, r, v, t0, node, 3).Find(x => x.Body == ctx.Target);
            if (patch == null) return NoEncounterPenalty;
            double rp = patch.Orbit.PeriapsisRadius, mu = ctx.Target.Mu;
            double vinf2 = patch.Orbit.IsElliptic ? 0 : -mu / patch.Orbit.A;
            double b = vinf2 > 0 ? rp * Math.Sqrt(1 + 2 * mu / (rp * vinf2)) : rp;
            double want = ctx.TargetMiss;
            if (!double.IsNaN(ctx.Periapsis))
                want = vinf2 > 0 ? ctx.Periapsis * Math.Sqrt(1 + 2 * mu / (ctx.Periapsis * vinf2)) : ctx.Periapsis;
            return Math.Abs(b - want) - Math.Abs(p.Miss - ctx.TargetMiss);
        }

        /// <summary>
        /// Доводка лучшего кандидата по прогнозу склейки коник (цена — та же, но промах — из прогноза):
        /// короткий покоординатный спуск с малыми шагами, PolishIters вызовов Predict на координату.
        /// </summary>
        static TransferPlan Polish(Context ctx, CelestialBody body, Vector3d r, Vector3d v, double t0, TransferPlan start, double dt)
        {
            double Score(TransferPlan p) => Cost(ctx, p) + PredictedMissError(ctx, body, r, v, t0, p);
            var best = start;
            double bestCost = Score(best);
            double[] step = { dt / 8, 2, 2 };
            for (int iter = 0; iter < PolishIters && (step[0] > 0.5 || step[1] > 0.02 || step[2] > 0.02); iter++)
            {
                bool improved = false;
                for (int k = 0; k < 3; k++)
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        var p = Shift(best, k, sgn * step[k]);
                        if (p.Time < ctx.T0 + 60) continue;
                        double c = Score(p);
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

        /// <summary>Итераций доводки по прогнозу: по 6 прогнозов на итерацию.</summary>
        const int PolishIters = 60;

        /// <summary>Штраф кандидату, у которого прогноз не входит в сферу цели, м промаха.</summary>
        const double NoEncounterPenalty = 1e8;

        sealed class Context
        {
            public KeplerOrbit Orbit;
            public CelestialBody Target;
            public double TargetMiss, T0, MaxTransfer, Periapsis;
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
            return ClosestApproach(tr, target, t0, span, out tMin);
        }

        /// <summary>То же на заданном отрезке span, с (межпланетный перелёт — сотни суток).</summary>
        public static double ClosestApproach(KeplerOrbit tr, CelestialBody target, double t0, double span, out double tMin)
        {
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
