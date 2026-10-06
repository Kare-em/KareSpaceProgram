using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Starship IFT-5 (GDD §6.9): выведение AscentAutopilot до горячего разделения (MECO Super Heavy — по его запасу на
    /// возврат, Vessel.HeldReserve; ускоритель дальше ведёт фоновый BoosterLandingAutopilot к башне), SECO корабля на
    /// трансатмосферной траектории (перигей SpaceXRockets.ShipPerigee), рельсы до верха атмосферы, затем тот же пилот
    /// посадки ведёт корабль: вход «брюхом», переворот, посадочный импульс на 3 Raptor и приводнение.
    /// </summary>
    public sealed partial class MissionAutopilot
    {
        /// <summary>
        /// Апогей выведения корабля, м (IFT-5 ≈ 210 км). AscentAutopilot сам вывел бы на круговую — SECO раньше, когда
        /// перигей дошёл до ShipPerigee. Пара: ShipPerigee и точка приводнения SpaceXRockets.ShipSplashLat/Lon.
        /// </summary>
        const double ShipApogee = 200e3;
        /// <summary>
        /// Предел перегрузки выведения, g (газ — AscentAutopilot.MaxG). Модель не дросселирует на max Q, и без предела
        /// MECO выходило на 2,2 км/с вместо ≈ 1,5: разворотный импульс съедал весь запас Super Heavy, а у корабля после
        /// SECO оставалось ≈ 150 т. Пара: SpaceXRockets.SuperHeavyReserve и топливо корабля.
        /// </summary>
        const double ShipMaxG = 2.5;
        /// <summary>Ниже этой высоты на нисходящей ветви, м, корабль передаётся пилоту посадки (полная физика).</summary>
        const double ShipDescentStart = 130e3;

        IEnumerable<object> Starship()
        {
            if (ShipCatch)
            {
                foreach (var x in StarshipOrbital()) yield return x;
                yield break;
            }
            var u = universe;
            BoosterLandingAutopilot ship = null;
            if (V.IsLanded || OnAscent())
            {
                Phase = "Выведение";
                u.Ascent = new AscentAutopilot { TargetAltitude = ShipApogee, MaxG = ShipMaxG };
                // SECO: перигей дошёл до заданного — дальше корабль летит по баллистике половину Земли. Проверка — на
                // каждом шаге физики: в конце разгона перигей растёт на ≈ 7 км на 1 м/с, а за кадр ×10 это сотни км.
                bool seco = false;
                step = (v, t, h) =>
                {
                    if (seco || !ShipAlone() || Perigee(v) - v.Body.Radius < SpaceXRockets.ShipPerigee) return AutopilotRequest.None;
                    seco = true;
                    u.Ascent = null;
                    FlightControl.Cutoff(v);
                    return AutopilotRequest.None;
                };
                foreach (var x in Await(() => seco || u.Ascent == null, () => "Выведение: " + (u.Ascent?.Status ?? ""))) yield return x;
                step = null;
                u.Ascent = null;
                FlightControl.Cutoff(V);
                if (!ShipAlone()) throw new Abort("разделение не состоялось");
                u.Post($"SECO: перигей {(Perigee(V) - V.Body.Radius) / 1000:F0} км, топливо {ShipFuel() / 1000:F0} т");
            }
            Phase = "Трансатмосферный полёт";
            foreach (var x in Await(() => V.Altitude < ShipDescentStart && V.VerticalSpeed < 0,
                                    () => $"Полёт к Индийскому океану: {V.Altitude / 1000:F0} км")) yield return x;
            ship = u.RecoveryOf(V) ?? BoosterLandingAutopilot.StartDescent(V);
            if (ship == null) throw new Abort("корабль не может сесть");
            if (!u.Recoveries.Contains(ship)) u.Recoveries.Add(ship);
            Phase = "Вход «брюхом» и приводнение";
            NeedsPhysics = true;
            foreach (var x in Await(() => !ship.Running, () => ship.Status)) yield return x;
            NeedsPhysics = false;
            if (ship.Phase == BoosterLandingAutopilot.PhaseType.Failed) throw new Abort(ship.Status);
        }

        // ---------------------------------------------------------------- орбитальный полёт и ловля корабля (starship_catch)

        /// <summary>
        /// Высота круговой орбиты корабля, м: 16 витков за звёздные сутки (P ≈ 5385 с) — трасса повторяется, и через сутки
        /// Starbase снова в вершине трассы (старт строго на восток: наклонение = широта старта, вершина над стартом).
        /// Пара: ShipCrossMax — без повтора ближайший проход над башней бывает в 40–80 км сбоку.
        /// </summary>
        const double ShipOrbitAltitude = 267e3;
        /// <summary>
        /// Перигей после тормозного импульса, м. Ниже — круче вход (больше перегрузка, точнее точка падения), выше — дольше
        /// пологий участок, где малая ошибка прогноза тянет точку на десятки км. Пара: SpaceXRockets.ShipCatchTolerance.
        /// </summary>
        const double ShipDeorbitPerigee = 40e3;
        /// <summary>Интерфейс входа, м: от него планировщик меряет дугу до точки падения (опорный прогноз с атмосферой).</summary>
        const double ShipEntryInterface = 120e3;
        /// <summary>Поиск момента схода: не раньше Lead, не позже Window, шаг Step, с.</summary>
        const double ShipDeorbitLead = 300, ShipDeorbitWindow = 36 * 3600, ShipDeorbitStep = 10;
        /// <summary>
        /// Боковое смещение башни от плоскости входа, м, которое берём без поиска дальше: его доправляет пилот возврата
        /// импульсом в вакууме (≈ 1 м/с на 0,8 км за четверть витка до цели).
        /// </summary>
        const double ShipCrossMax = 30e3;
        /// <summary>
        /// Доводка момента схода полным прогнозом, м по трассе: опорная дуга снята в другом месте орбиты (другая широта —
        /// другая скорость вращения Земли под входом), и по ней одной точка уходила на 58 км.
        /// </summary>
        const double ShipRefineAlong = 100;

        /// <summary>Миссия с ловлей корабля башней B: орбита, сход и возврат (а не суборбитальный IFT-5).</summary>
        bool ShipCatch => MissionCatalog.CatchesShip(Tracker.Def);

        /// <summary>
        /// starship_catch (§6.9): выведение на орбиту повторяющейся трассы (Super Heavy тем временем идёт к башне A),
        /// виток, ожидание прохода над Starbase, тормозной импульс по плану PlanShipDeorbit, затем пилот возврата:
        /// доправка в вакууме, вход «брюхом», переворот и ловля башней B.
        /// </summary>
        IEnumerable<object> StarshipOrbital()
        {
            var u = universe;
            var earth = V.Body;
            int iOrbit = IndexOf(ObjectiveType.Orbit);
            var ship = u.RecoveryOf(V);
            bool descending = !V.IsLanded && (iOrbit < 0 || Tracker.Done[iOrbit]) && Perigee(V) < earth.Radius + earth.AtmosphereTop;
            if (ship == null && !descending)
            {
                if (V.IsLanded || !OnOrbit(V))
                {
                    Phase = "Выведение";
                    u.Ascent = new AscentAutopilot { TargetAltitude = ShipOrbitAltitude, MaxG = ShipMaxG };
                    foreach (var x in Await(() => u.Ascent == null, () => "Выведение: " + (u.Ascent?.Status ?? ""))) yield return x;
                    if (!ShipAlone()) throw new Abort("разделение не состоялось");
                    if (V.IsLanded || !OnOrbit(V)) throw new Abort("орбита не получена");
                    u.Post($"Орбита: перигей {(Perigee(V) - earth.Radius) / 1000:F0} км, топливо {ShipFuel() / 1000:F0} т");
                }
                foreach (var x in HoldOrbit(iOrbit)) yield return x;

                Phase = "Сход с орбиты";
                var plan = BoosterLandingAutopilot.StartReturn(V, false);
                if (plan == null) throw new Abort("корабль не может сесть");
                double tb = PlanShipDeorbit(plan, out double dv, out double cross);
                if (double.IsNaN(tb)) throw new Abort("трасса не проходит над Starbase в ближайшие сутки");
                u.Post($"Сход с орбиты через {Clock(tb - T)}: {dv:F0} м/с, башня в {cross / 1000:F0} км сбоку от трассы");
                u.SetNode(tb, -dv, 0, 0);
                foreach (var x in Burn("Тормозной импульс")) yield return x;
                V.Throttle = 0;
            }
            ship = ship ?? BoosterLandingAutopilot.StartReturn(V);
            if (ship == null) throw new Abort("корабль не может сесть");
            if (!u.Recoveries.Contains(ship)) u.Recoveries.Add(ship);
            Phase = "Возврат к башне B";
            NeedsPhysics = true;
            foreach (var x in Await(() => !ship.Running, () => ship.Status)) yield return x;
            NeedsPhysics = false;
            if (ship.Phase == BoosterLandingAutopilot.PhaseType.Failed) throw new Abort(ship.Status);
        }

        /// <summary>
        /// Момент тормозного импульса к башне B (как PlanWingedDeorbit, но вход баллистический): дуга от EI до точки
        /// падения и время от EI до неё берутся из одного полного прогноза пилота (атмосфера, «брюхом»), дальше — поиск по
        /// Кеплеру, когда башня на момент падения окажется на этой дуге. Первый корень с боковым смещением ≤ ShipCrossMax,
        /// иначе лучший за окно. NaN — не нашлось.
        /// </summary>
        double PlanShipDeorbit(BoosterLandingAutopilot pilot, out double dv, out double cross)
        {
            var v = V;
            var body = v.Body;
            double mu = body.Mu;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, mu, T);
            double rp = body.Radius + ShipDeorbitPerigee, rEI = body.Radius + ShipEntryInterface;
            var tgtBf = pilot.TargetBodyFixed();
            v.MassProperties(out double mass, out _, out _, out _);
            dv = cross = 0;

            // Импульс назад по скорости: апоцентр — текущий радиус, перигей rp.
            bool Deorbit(double tb, out Vector3d r, out Vector3d vel, out double dvb)
            {
                orbit.GetState(tb, out r, out vel);
                dvb = 0;
                double r1 = r.magnitude;
                if (r1 <= rp) return false;
                double vNeed = Math.Sqrt(mu * 2 * rp / (r1 * (r1 + rp)));
                dvb = Vector3d.ProjectOnPlane(vel, r / r1).magnitude - vNeed;
                if (dvb <= 0) return false;
                vel -= vel.normalized * dvb;
                return true;
            }

            // Опорный вход: дуга EI → падение и время полёта от EI (с вращением Земли и сопротивлением).
            double tb0 = T + ShipDeorbitLead;
            if (!Deorbit(tb0, out var r0, out var u0, out _)) return double.NaN;
            var o0 = KeplerOrbit.FromState(r0, u0, mu, tb0);
            double tEI0 = o0.NextTimeAtRadius(rEI, tb0, false);
            if (double.IsNaN(tEI0)) return double.NaN;
            o0.GetState(tEI0, out var rE0, out _);
            var miss0 = pilot.Predict(tb0, r0, u0, mass, false, out double tI0);
            var impactBf = tgtBf + body.OrientationAt(tb0).Inverse * miss0;
            var impact = body.OrientationAt(tI0) * impactBf;
            double range = Vector3d.Angle(rE0, impact) * body.Radius, fly = tI0 - tEI0;

            bool Aim(double tb, out double f, out double cr, out double dvb)
            {
                f = cr = 0;
                if (!Deorbit(tb, out var r, out var vel, out dvb)) return false;
                var o2 = KeplerOrbit.FromState(r, vel, mu, tb);
                double tEI = o2.NextTimeAtRadius(rEI, tb, false);
                if (double.IsNaN(tEI)) return false;
                o2.GetState(tEI, out var rE, out var vE);
                var tgt = (body.OrientationAt(tEI + fly) * tgtBf).normalized;
                var n = Vector3d.Cross(rE, vE).normalized;
                cr = Math.Asin(MathD.Clamp(Vector3d.Dot(tgt, n), -1, 1)) * body.Radius;
                var inPlane = Vector3d.ProjectOnPlane(tgt, n).normalized;
                var rh = rE.normalized;
                double ang = Math.Atan2(Vector3d.Dot(Vector3d.Cross(rh, inPlane), n), Vector3d.Dot(rh, inPlane));
                if (ang < 0) ang += 2 * Math.PI;
                f = ang * body.Radius - range;
                return true;
            }

            double best = double.NaN, bestCross = double.PositiveInfinity, bestDv = 0;
            double prevF = double.NaN, prevT = 0, circ = 2 * Math.PI * body.Radius;
            for (double tb = T + ShipDeorbitLead; tb < T + ShipDeorbitWindow; tb += ShipDeorbitStep)
            {
                if (!Aim(tb, out double f, out _, out _)) { prevF = double.NaN; continue; }
                if (!double.IsNaN(prevF) && prevF > 0 && f <= 0 && prevF - f < 0.5 * circ)
                {
                    double tr = prevT + prevF / (prevF - f) * (tb - prevT);
                    if (Aim(tr, out _, out double cr, out double dvr) && Math.Abs(cr) < bestCross)
                    {
                        best = tr; bestCross = Math.Abs(cr); bestDv = dvr; cross = cr;
                        if (bestCross <= ShipCrossMax) break;
                    }
                }
                prevF = f;
                prevT = tb;
            }
            dv = bestDv;
            if (double.IsNaN(best)) return best;

            // Доводка секущей по промаху вдоль трассы из полного прогноза пилота от точки схода.
            double Along(double tb)
            {
                if (!Deorbit(tb, out var r, out var vel, out _)) return double.NaN;
                var m = pilot.Predict(tb, r, vel, mass, false, out _);
                pilot.TargetAxes(tb, r, vel, out var f1, out _);
                return Vector3d.Dot(m, f1);
            }
            double ta = best, fa = Along(ta), tc = best + 1, fc = Along(tc);
            for (int i = 0; i < 8 && !double.IsNaN(fa) && !double.IsNaN(fc) && Math.Abs(fc) > ShipRefineAlong && fc != fa; i++)
            {
                double tn = tc - fc * (tc - ta) / (fc - fa);
                ta = tc; fa = fc; tc = tn; fc = Along(tc);
            }
            if (!double.IsNaN(fc) && Aim(tc, out _, out double crc, out double dvc))
            {
                best = tc; dv = dvc; cross = crc;
            }
            return best;
        }

        /// <summary>Корабль уже отделился от Super Heavy (осталась одна секция).</summary>
        bool ShipAlone()
        {
            int n = 0;
            foreach (bool a in V.Attached) if (a) n++;
            return n == 1;
        }

        double ShipFuel()
        {
            double f = 0;
            for (int i = 0; i < V.Attached.Length; i++) if (V.Attached[i]) f += V.Propellant[i];
            return f;
        }

        /// <summary>Ещё на выведении: перигей под землёй и двигатели работают (Y нажали после старта).</summary>
        bool OnAscent() => V.AnyEngineRunning && Perigee(V) - V.Body.Radius < SpaceXRockets.ShipPerigee;
    }
}
