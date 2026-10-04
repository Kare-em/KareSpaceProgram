using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Сценарий «к станции» (GDD §6.6): «Союз ТМ-31», Crew Dragon Demo-2. Окно — пролёт площадки под плоскостью
    /// станции, выведение в её плоскость, перелёт и причаливание DockingAutopilot, время в связке, расстыковка,
    /// тормозной импульс двигателем корабля и спуск капсулы. Станцию заранее ставит StationSetup.Place.
    /// </summary>
    public sealed partial class MissionAutopilot
    {
        /// <summary>Шаг и глубина поиска окна в плоскость станции, с: площадка проходит под плоскостью дважды в сутки.</summary>
        const double PlaneScanStep = 30, PlaneScanSpan = 86400;
        /// <summary>После расстыковки отойти столько, с, прежде чем тормозить (0,3 м/с толчка — ~200 м от станции).</summary>
        const double UndockAway = 600;
        /// <summary>
        /// Перигей после тормозного импульса, м. Капсула в ядре баллистическая (без подъёмной силы «Союза»):
        /// пологий вход растягивает торможение, крутой поднимает пик. Пара: FlightPhysics.CrewGLimit (9 g дольше 10 с).
        /// </summary>
        const double StationEntryPerigee = 50e3;

        IEnumerable<object> Station()
        {
            var u = universe;
            var def = Tracker.Def;
            int dockIdx = IndexOf(ObjectiveType.Dock);
            int undockIdx = V.Design.Sequence.FindIndex(a => a.Type == StageActionType.Undock);
            bool dockDone = dockIdx >= 0 && Tracker.Done[dockIdx];

            if (!dockDone && !IsDocked(V))
            {
                var station = FindStation(def);
                if (station == null) throw new Abort("станция не найдена");
                if (V.IsLanded) foreach (var x in PlaneWindow(station)) yield return x;
                foreach (var x in Ascend(ParkingAltitude, station)) yield return x;
                // Последняя ступень уходит — сближение ведёт двигатель корабля (СКД «Союза», Draco «Дракона»).
                StageBefore(undockIdx);
                Phase = "Сближение";
                u.Docking = new DockingAutopilot(u, station);
                foreach (var x in Await(() => u.Docking == null, () => "Сближение: " + (u.Docking?.Status ?? ""))) yield return x;
                if (!IsDocked(V)) throw new Abort("стыковка не удалась");
            }

            if (dockIdx >= 0 && !Tracker.Done[dockIdx])
            {
                double end = T + def.Objectives[dockIdx].HoldSeconds + HoldMargin;
                Phase = "На станции";
                WarpLimit = end;
                foreach (var x in Await(() => Tracker.Done[dockIdx], () =>
                         {
                             if (T >= WarpLimit) WarpLimit = T + 60;
                             return $"На станции: {Clock(Math.Max(0, end - T))}";
                         })) yield return x;
                WarpLimit = double.PositiveInfinity;
            }
            if (IndexOf(ObjectiveType.Return) < 0) yield break;

            if (IsDocked(V))
            {
                Phase = "Расстыковка";
                StageBefore(undockIdx);
                var before = new HashSet<Vessel>(u.Vessels);
                u.Stage();
                // Отошедшая станция — снова станция, а не «обломок» (Vessel.Split называет так всё без управления).
                foreach (var w in u.Vessels)
                    if (!before.Contains(w) && w.Attached[def.StationSection])
                    {
                        w.IsDebris = false;
                        w.Name = def.StationName ?? w.Name;
                    }
                if (IsDocked(V)) throw new Abort("расстыковка не удалась");
                double away = T + UndockAway;
                foreach (var x in WaitUntil(away, () => $"Отход от станции: {Clock(away - T)}")) yield return x;
            }

            if (!V.IsLanded && Perigee(V) > V.Body.Radius + V.Body.AtmosphereTop)
            {
                Phase = "Сход с орбиты";
                if (!HasArmedEngine(V)) StageUntil(StageActionType.Ignite);
                if (!HasArmedEngine(V) && V.NextStageLabel != null) u.Stage();
                var v = V;
                double tn = T + DeorbitLead;
                var o = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, T);
                o.GetState(tn, out var r, out var vel);
                double rn = r.magnitude, rp = v.Body.Radius + StationEntryPerigee;
                // Импульс по горизонту: из точки rn на эллипс с перицентром rp (орбита почти круговая).
                double vh = Vector3d.Cross(r, vel).magnitude / rn;
                double need = Math.Sqrt(v.Body.Mu * 2 * rp / (rn * (rn + rp)));
                u.SetNode(tn, need - vh, 0, 0);
                double prop = 0;
                for (int k = 0; k < v.Attached.Length; k++)
                    if (v.Attached[k] && v.Armed[k]) prop += v.Propellant[k];
                u.Post($"Сход: импульс {vh - need:F0} м/с, топлива корабля {prop:F0} кг; орбита {(o.PeriapsisRadius - v.Body.Radius) / 1000:F0}×{(o.ApoapsisRadius - v.Body.Radius) / 1000:F0} км");
                foreach (var x in Burn("Тормозной импульс")) yield return x;
            }
            foreach (var x in Descend()) yield return x;
        }

        /// <summary>Отработать ступени до действия idx (расстыковки): Undock до стыковки неприменим, и NextStage
        /// его перешагнул бы — StageUntil здесь не годится, он смотрит сырой индекс.</summary>
        void StageBefore(int idx)
        {
            for (int guard = 0; guard < 32 && idx >= 0; guard++)
            {
                var v = V;
                if (!v.HasNextStage || v.NextApplicable(v.NextStage) >= idx) return;
                universe.Stage();
            }
        }

        Vessel FindStation(MissionDef def)
        {
            foreach (var w in universe.Vessels)
                if (w != V && w.Alive && w.Attached[def.StationSection]) return w;
            return null;
        }

        /// <summary>К борту прицеплены перевёрнутые секции с узлом — станция (Vessel.Dock).</summary>
        static bool IsDocked(Vessel v)
        {
            for (int k = 0; k < v.Attached.Length; k++)
                if (v.Attached[k] && v.Flipped[k] && v.Design.Sections[k].DockingPort) return true;
            return false;
        }

        static bool HasArmedEngine(Vessel v)
        {
            for (int k = 0; k < v.Attached.Length; k++)
                if (v.Attached[k] && v.Armed[k] && v.Propellant[k] > 0 && v.Design.Sections[k].HasEngine) return true;
            return false;
        }

        /// <summary>
        /// Окно старта к станции: площадка под плоскостью её орбиты на восходящем витке (курс к северу) —
        /// тогда выведение идёт прямо в плоскость, без бокового манёвра (Δv корабля на него не рассчитан).
        /// </summary>
        IEnumerable<object> PlaneWindow(Vessel station)
        {
            if (!V.IsLanded || V.Site == null) yield break;
            double tL = StationPlaneWindow(V.Body, V.Site, station, T + PlaneScanStep);
            if (double.IsNaN(tL)) yield break;
            universe.Post($"Окно старта в плоскость станции через {Clock(tL - T)}");
            Phase = "Ожидание окна старта";
            foreach (var x in WaitUntil(tL, () => $"Окно старта через {Clock(tL - T)}")) yield return x;
        }

        double StationPlaneWindow(CelestialBody body, LaunchSite site, Vessel station, double t0)
        {
            Universe.StateOf(station, T, out var rS, out var vS);
            var n = Vector3d.Cross(rS, vS).normalized;
            double F(double t) => Vector3d.Dot(body.OrientationAt(t) * site.DirectionBodyFixed, n);
            double tPrev = t0, fPrev = F(t0);
            for (double t = t0 + PlaneScanStep; t <= t0 + PlaneScanSpan; t += PlaneScanStep)
            {
                double f = F(t);
                if (Math.Sign(f) != Math.Sign(fPrev))
                {
                    double lo = tPrev, hi = t, flo = fPrev;
                    for (int i = 0; i < 40 && hi - lo > 0.1; i++)
                    {
                        double mid = 0.5 * (lo + hi), fm = F(mid);
                        if (Math.Sign(fm) == Math.Sign(flo)) { lo = mid; flo = fm; }
                        else hi = mid;
                    }
                    double tc = 0.5 * (lo + hi);
                    var q = body.OrientationAt(tc);
                    var up = q * site.DirectionBodyFixed;
                    var north = Vector3d.ProjectOnPlane(q * Vector3d.forward, up);
                    if (Vector3d.Dot(Vector3d.Cross(n, up), north) > 0) return tc;
                }
                tPrev = t;
                fPrev = f;
            }
            return double.NaN;
        }
    }
}
