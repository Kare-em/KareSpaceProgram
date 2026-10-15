using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// «Вояджеры» (GDD §5.3, §6.11): тур с гравманёврами. План тура — TourPlanner от исторических дат; старт в окно,
    /// когда плоскость опорной орбиты содержит асимптоту ухода; разгон «Центавром» с добором твердотопливным модулем
    /// (NodeAutopilot сам отсекает «Центавр» на доле модуля); коррекции на трассе двигателями зонда — к точке прицела,
    /// которая поворачивает v∞ к следующей планете тура.
    /// </summary>
    public sealed partial class MissionAutopilot
    {
        /// <summary>Отлёт с опорной орбиты — не раньше чем через столько после выхода на неё (разворот «Центавра»).</summary>
        const double DepartLead = 900;
        /// <summary>
        /// Поздняя коррекция — за столько до пролёта: снаружи сферы влияния (Юпитер 48 млн км при v∞ 8 км/с — 70 сут,
        /// Нептун 87 млн км при 17 км/с — 60 сут). Пара: сфера / v∞ каждой планеты тура должна быть меньше.
        /// </summary>
        const double LateCorrection = 75 * 86400;
        /// <summary>Ранняя коррекция — через столько после выхода из сферы влияния: промах разгона/пролёта ещё дёшев.</summary>
        const double EarlyCorrection = 5 * 86400;
        /// <summary>Коррекцию меньше этого не делаем, м/с: 0,1 м/с у Юпитера — сотни километров у перицентра.</summary>
        const double MinCorrection = 0.05;
        /// <summary>Запас перед серединой прожига зонда (тяга 3,6 Н на 825 кг: 10 м/с — 40 мин), с.</summary>
        const double CorrectionTurn = 1200;
        /// <summary>Пересчётов точки прицела по траектории после коррекции (от неё зависит v∞, а с ним прицел).</summary>
        const int AimPasses = 3;

        IEnumerable<object> Planetary()
        {
            var u = universe;
            var def = Tracker.Def;
            var earth = u.System.Get("earth");
            var sun = earth.Parent;
            double park = earth.Radius + ParkingAltitude;

            // Тур — от отлёта примерно через полтора часа (выведение и полвитка); для окна старта точности хватает.
            var tour = MissionCatalog.PlanTour(def, u.System, T + AscentToTransfer, park);
            if (tour == null) throw new Abort("тур не найден");

            if (V.IsLanded && V.Site != null)
            {
                double tL = LaunchWindow.NextPlaneWindow(V.Body, V.Site, 90, tour.VInfDepart, T + 60);
                if (!double.IsNaN(tL))
                {
                    u.Post($"Окно старта к {tour.Bodies[0].Name} через {Clock(tL - T)}");
                    Phase = "Ожидание окна старта";
                    foreach (var x in WaitUntil(tL, () => $"Окно старта через {Clock(tL - T)}")) yield return x;
                }
            }
            if (V.Body == earth)
            {
                foreach (var x in Ascend(ParkingAltitude, null)) yield return x;

                // Тур заново — от настоящего момента отлёта (окно сдвинуло его на часы).
                var v = V;
                var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, earth.Mu, T);
                double dep = T + DepartLead + orbit.Period;
                tour = MissionCatalog.PlanTour(def, u.System, dep - orbit.Period / 2, orbit.A) ?? tour;
                var first = tour.Bodies[0];
                double b = FlybyTour.AimVector(first, tour.VIn[0], tour.VOut[0].sqrMagnitude > 0 ? tour.VOut[0] : tour.VIn[0],
                    FirstPeriapsis(tour, def)).magnitude;
                var plan = TransferPlanner.PlanDeparture(earth, v.Position, v.Velocity, T, tour.VInfDepart, dep, first, b,
                    tour.Times[0] - dep);
                if (plan == null) throw new Abort($"отлёт к {first.Name} не найден");
                Phase = $"Разгон к {first.Name}";
                u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
                // Наведение пинка по асимптоте — к избытку импульсного плана (под него подобран промах у цели).
                orbit.GetState(plan.Time, out var rn, out var vn);
                var want = KeplerOrbit.FromState(rn, vn + V.Node.WorldDeltaV(orbit), earth.Mu, plan.Time).Excess;
                V.Node.TargetExcess = want;
                u.Post($"К {first.Name}: разгон через {Clock(plan.Time - T)}, Δv {plan.DeltaV:F0} м/с, C3 {tour.VInfDepart.sqrMagnitude / 1e6:F0} км²/с²");
                foreach (var x in Burn($"Разгон к {first.Name}")) yield return x;
                // Модуль выгорел — сброс, двигатели зонда (Separate igniteNext).
                for (int n = 0; n < 4 && V.HasNextStage && V.Design.Sequence[V.NextStage].Type == StageActionType.Separate; n++)
                    u.Stage();
                {
                    var o = KeplerOrbit.FromState(V.Position, V.Velocity, earth.Mu, T);
                    var vx = o.Excess;
                    u.Post($"DIAG v∞ {vx.magnitude:F0} vs {want.magnitude:F0}, угол {Vector3d.Angle(vx, want) * 180 / Math.PI:F2}°, e {o.E:F3}");
                }
            }

            for (int i = 0; i < tour.Bodies.Length; i++)
            {
                var target = tour.Bodies[i];
                int leg = IndexOf(ObjectiveType.Flyby) + i;
                if (leg < Tracker.Done.Length && Tracker.Done[leg]) continue;
                Phase = $"Перелёт к {target.Name}";
                foreach (var x in Await(() => V.Body == sun, CoastText)) yield return x;

                // Ранняя коррекция — когда выходной промах ещё дёшев; поздняя — снаружи сферы цели.
                double early = T + EarlyCorrection;
                foreach (var x in WaitUntil(early, () => $"Перелёт к {target.Name}: коррекция через {Clock(early - T)}")) yield return x;
                foreach (var x in Correct(tour, i, "Коррекция")) yield return x;
                double arrival = Arrival(target);
                if (!double.IsNaN(arrival) && arrival - LateCorrection > T + EarlyCorrection)
                {
                    double late = arrival - LateCorrection;
                    foreach (var x in WaitUntil(late, () => $"Перелёт к {target.Name}: {Clock(Math.Max(0, arrival - T))}")) yield return x;
                    foreach (var x in Correct(tour, i, "Поздняя коррекция")) yield return x;
                }
                Phase = $"Пролёт {target.Name}";
                int goal = leg;
                foreach (var x in Await(() => goal < Tracker.Done.Length && Tracker.Done[goal] || V.Body == target,
                             () => CoastText())) yield return x;
                foreach (var x in Await(() => goal >= Tracker.Done.Length || Tracker.Done[goal] || V.Body != target,
                             () => $"У {target.Name}: {V.Altitude / 1e6:F0} тыс. км")) yield return x;
                if (goal < Tracker.Done.Length && !Tracker.Done[goal]) throw new Abort($"пролёт {target.Name} мимо коридора");
            }
        }

        /// <summary>Перицентр у первой планеты по туру, но не ниже нижней границы её коридора.</summary>
        static double FirstPeriapsis(FlybyTour tour, MissionDef def)
        {
            var o = def.Objectives.Find(x => x.Type == ObjectiveType.Flyby && x.Body == tour.Bodies[0].Id);
            double floor = tour.Bodies[0].Radius + (o?.Min ?? 0);
            double rp = tour.Periapsis[0];
            return double.IsNaN(rp) ? floor + (o != null ? 0.5 * (o.Max - o.Min) : 0) : Math.Max(rp, floor);
        }

        /// <summary>Сближение с целью по текущей траектории вокруг Солнца (без её притяжения), NaN — не сходимся.</summary>
        double Arrival(CelestialBody target)
        {
            var v = V;
            if (target.Parent != v.Body) return double.NaN;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, T);
            TransferPlanner.ClosestApproach(orbit, target, T, 40 * 365.25 * 86400, out double ta);
            return ta;
        }

        /// <summary>
        /// Точка прицела у планеты i тура: у промежуточной — поворот v∞ к следующей планете на её плановую дату
        /// (Ламберт от планеты в момент сближения), перицентр — из угла поворота, не ниже коридора; у последней —
        /// середина коридора высот с той стороны, с какой зонд идёт сейчас.
        /// </summary>
        TransferPlanner.AimPoint TourAim(FlybyTour tour, int i)
        {
            var body = tour.Bodies[i];
            var o = Tracker.Def.Objectives.Find(x => x.Type == ObjectiveType.Flyby && x.Body == body.Id);
            double floor = body.Radius + (o?.Min ?? 0), top = body.Radius + (o?.Max ?? 0);
            if (i == tour.Bodies.Length - 1)
                return (ta, rel, vrel, h) =>
                {
                    var uv = vrel.normalized;
                    var side = rel - uv * Vector3d.Dot(rel, uv);
                    if (side.sqrMagnitude < 1) side = Vector3d.Cross(uv, h);
                    double rp = 0.5 * (floor + top), v2 = Math.Max(vrel.sqrMagnitude, 1);
                    return side.normalized * (rp * Math.Sqrt(1 + 2 * body.Mu / (rp * v2)));
                };
            var next = tour.Bodies[i + 1];
            double tn = tour.Times[i + 1];
            return (ta, rel, vrel, h) =>
            {
                body.LocalStateAt(ta, out var rb, out var vb);
                next.LocalStateAt(tn, out var rn, out _);
                if (!Lambert.Solve(rb, rn, tn - ta, body.Parent.Mu, new Vector3d(0, 0, 1), out var v1, out _))
                    return FlybyTour.AimVector(body, vrel, vrel, floor);
                var vout = v1 - vb;
                double rp = FlybyTour.TurnPeriapsis(body, vrel.magnitude, Vector3d.Angle(vrel, vout));
                rp = MathD.Clamp(rp, floor, top);
                return FlybyTour.AimVector(body, vrel, vout, rp);
            };
        }

        /// <summary>Коррекция к планете i тура двигателями зонда; lead — чтобы середина долгого прожига успела.</summary>
        IEnumerable<object> Correct(FlybyTour tour, int i, string what)
        {
            var u = universe;
            var target = tour.Bodies[i];
            var aim = TourAim(tour, i);
            var v = V;
            double lead = CorrectionTurn;
            TransferPlan plan = null;
            for (int pass = 0; pass < 3; pass++)
            {
                plan = TransferPlanner.PlanCorrection(v.Body, v.Position, v.Velocity, T, target, aim, lead, AimPasses);
                if (plan == null) break;
                double half = FlightControl.BurnTime(v, plan.DeltaV) / 2;
                if (double.IsInfinity(half) || half + CorrectionTurn <= lead) break;
                lead = half + CorrectionTurn;
            }
            if (plan == null || plan.DeltaV < MinCorrection) yield break;
            Phase = what;
            u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
            u.Post($"{what} к {target.Name}: {plan.DeltaV:F2} м/с, пролёт {GameCalendar.Format(plan.ArrivalTime)}");
            foreach (var x in Burn($"{what} к {target.Name}")) yield return x;
        }
    }
}
