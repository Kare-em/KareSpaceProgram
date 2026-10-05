using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>Ступень, чья посадка отложена (§6.9): борт выведен из мира и стоит на паузе с момента FrozenAt.</summary>
    public sealed class DeferredRecovery
    {
        public readonly BoosterLandingAutopilot Pilot;
        public readonly double FrozenAt;
        public Vessel Vessel => Pilot.Vessel;

        public DeferredRecovery(BoosterLandingAutopilot pilot, double frozenAt)
        {
            Pilot = pilot;
            FrozenAt = frozenAt;
        }
    }

    /// <summary>
    /// «Сначала корабль, потом посадка» (§6.9). Возврат ступени и полёт корабля идут одновременно, а рельсы закрыты,
    /// пока ступень не села (RailsBlocker). Отложенная ступень из мира уходит целиком — не считается, не рисуется,
    /// рельсы открыты, — а вернувшись, продолжает с той же точки относительно Земли.
    /// Почему это честно: всё, что ведёт ступень, вращается вместе с телом — атмосфера, рельеф, цель посадки
    /// (широта/долгота), а тяготение осесимметрично. Сдвиг времени на Δt вместе с поворотом состояния на угол
    /// вращения тела за Δt — та же задача; меняется только освещение. Третьих тел физика не считает (FlightPhysics),
    /// поэтому сдвиг годится только для борта у своего тела, а не для перелёта (рандеву, Луна) — его и не откладываем.
    /// </summary>
    public sealed partial class Universe
    {
        public readonly List<DeferredRecovery> Deferred = new List<DeferredRecovery>();

        /// <summary>Первый борт на фоновом возврате, кроме активного (активный — это Starship на спуске, его не откладываем).</summary>
        public BoosterLandingAutopilot RunningRecovery()
        {
            foreach (var p in Recoveries)
                if (p.Running && p.Vessel != Active && p.Vessel.Alive) return p;
            return null;
        }

        /// <summary>Отложить посадку ступени: борт уходит из мира до ResumeRecovery.</summary>
        public bool DeferRecovery(BoosterLandingAutopilot p)
        {
            if (p == null || !p.Running || p.Vessel == Active || !p.Vessel.Alive || p.Vessel.IsLanded) return false;
            var v = p.Vessel;
            if (v.OnRails) LeaveRails(v);
            Recoveries.Remove(p);
            Vessels.Remove(v);
            Deferred.Add(new DeferredRecovery(p, Time));
            eventValid = false;
            Post($"{v.Name}: посадка отложена — вернуться к ней можно в любой момент");
            return true;
        }

        /// <summary>
        /// Вернуть отложенную ступень в мир на текущее время: положение, скорость и ориентацию поворачиваем на угол
        /// вращения тела за паузу, отметки времени борта и пилота сдвигаем на её длину. Ускорение — в ×1: посадку смотрят.
        /// </summary>
        public BoosterLandingAutopilot ResumeRecovery(DeferredRecovery d)
        {
            if (d == null || !Deferred.Remove(d)) return null;
            var v = d.Vessel;
            var p = d.Pilot;
            double dt = Time - d.FrozenAt;
            // Оси тела в момент заморозки → оси тела сейчас: в осях тела борт стоит там же, где стоял.
            var rot = v.Body.OrientationAt(Time) * v.Body.OrientationAt(d.FrozenAt).Inverse;
            v.Position = rot * v.Position;
            v.Velocity = rot * v.Velocity;
            // Attitude и удержание SAS — в осях Unity (SwapYZ), угловая скорость — в осях борта и не меняется.
            v.Attitude = rot.SwapYZ * v.Attitude;
            v.SasHold = rot.SwapYZ * v.SasHold;
            if (!double.IsNaN(v.LaunchTime)) v.LaunchTime += dt;
            if (!double.IsInfinity(v.NoCollideUntil)) v.NoCollideUntil += dt;
            for (int i = 0; i < v.ChuteOpenTime.Length; i++)
                if (v.ChuteDeployed[i]) v.ChuteOpenTime[i] += dt;
            v.DragChuteTime += dt;
            p.ShiftTime(dt);
            Vessels.Add(v);
            Recoveries.Add(p);
            FlightPhysics.UpdateTelemetry(v, Time);
            eventValid = false;
            SetWarp(0);
            Post($"{v.Name}: продолжение посадки (пауза {GameCalendar.FormatDuration(dt)})");
            return p;
        }
    }
}
