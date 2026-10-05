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
