using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Автопилот сближения и стыковки (GDD §6.6). Издалека — перелёт по Ламберту: перебор момента старта и времени
    /// перелёта, импульс, коррекция на середине, торможение у цели. Ближе RcsRange — поступательная РСУ:
    /// выход на ось узла цели на StandoffDistance, разворот носом к узлу, причаливание. Захват делает Universe.CheckDocking.
    /// </summary>
    public sealed class DockingAutopilot
    {
        /// <summary>Ближе — сближение на РСУ, м. Пара: AimStandoff &lt; RcsRange — торможение приводит в зону РСУ.</summary>
        const double RcsRange = 2000;
        /// <summary>Перелёт целится не в узел, а в точку на его оси впереди: двигатель тормозит, не проходя сквозь цель.</summary>
        const double AimStandoff = 300;
        /// <summary>Точка ожидания на оси узла, пока борт не выровнен, м.</summary>
        const double StandoffDistance = 20;
        /// <summary>Выровнен для причаливания. Пара: Universe.DockMaxAngleDeg = 10 — запас на качание по ходу.</summary>
        const double AlignAngleDeg = 5, AlignLateral = 0.5;
        /// <summary>Предел скорости сближения на РСУ, м/с. Пара: Vessel.UndockPush 0,3 — отход после расстыковки.</summary>
        public const double MaxApproach = 3;
        /// <summary>Скорость последних метров. Пара: Universe.DockMaxSpeed = 0,5 — захват не отбрасывает.</summary>
        const double FinalSpeed = 0.25, FinalZone = 8;
        /// <summary>Постоянная времени контура скорости РСУ, с.</summary>
        const double Tau = 2;
        /// <summary>Доля ускорения РСУ в законе «корня»: остальное — запас на отработку ошибки.</summary>
        const double BrakeShare = 0.5;
        /// <summary>Коррекция меньше этого не выполняется, м/с.</summary>
        const double MinCorrection = 0.5;
        /// <summary>Сколько раз заново планировать перелёт, если торможение не привело в зону РСУ.</summary>
        const int MaxPlans = 4;
        /// <summary>Цена ожидания старта, м/с за секунду: сутки ≈ 17 м/с — раньше лучше, но не любой ценой.</summary>
        const double WaitPenalty = 2e-4;
        /// <summary>Запас перицентра перелёта над телом, м (горы Луны — до 10 км).</summary>
        const double MinClearance = 15000;

        public enum PhaseType { Plan, Depart, Correct, Brake, Rcs }

        public readonly Vessel Target;
        readonly Universe universe;
        public PhaseType Phase { get; private set; } = PhaseType.Plan;
        public string Status { get; private set; } = "";
        /// <summary>Идёт сближение на РСУ — ускорение времени запрещено.</summary>
        public bool Close => Phase == PhaseType.Rcs;
        /// <summary>Причалили: цели больше нет во вселенной.</summary>
        public bool Docked => !universe.Vessels.Contains(Target);

        NodeAutopilot node;
        double arrival;
        int plans;

        public DockingAutopilot(Universe u, Vessel target)
        {
            universe = u;
            Target = target;
        }

        public AutopilotRequest Update(Vessel v, double t, double dt)
        {
            if (Docked) return AutopilotRequest.Finished;
            if (!Universe.CanDock(v, Target))
            {
                Stop(v);
                v.Raise("Стыковка отменена: цель недоступна");
                return AutopilotRequest.Finished;
            }
            Universe.StateOf(Target, t, out var rT, out var vT);
            var nT = Target.NoseP;
            var portT = rT + nT * Target.PortHeight();
            var port = v.Position + v.NoseP * v.PortHeight();
            double dist = (port - portT).magnitude;
            var vrel = v.Velocity - vT;

            if (dist < RcsRange && node == null) Phase = PhaseType.Rcs;
            else if (Phase == PhaseType.Rcs) Phase = PhaseType.Plan; // унесло — заново перелётом

            if (Phase == PhaseType.Rcs) return Approach(v, port, portT, nT, vrel, dist);

            v.RcsTranslate = Vector3d.zero;
            if (node != null)
            {
                var req = node.Update(v, t, dt);
                Status = $"{PhaseName()}: {node.Status}";
                if (req != AutopilotRequest.Finished) return req;
                node = null;
                if (Phase == PhaseType.Depart) PlanCorrection(v, t);
                else if (Phase == PhaseType.Correct) PlanBrake(v, t);
                else Phase = PhaseType.Plan;
                return AutopilotRequest.None;
            }
            // Узел сняли руками (Backspace) — перепланировать.
            if (Phase != PhaseType.Plan) Phase = PhaseType.Plan;
            if (plans >= MaxPlans)
            {
                Stop(v);
                v.Raise("Стыковка: не удалось выйти к цели");
                return AutopilotRequest.Finished;
            }
            plans++;
            if (!PlanTransfer(v, t))
            {
                Stop(v);
                v.Raise("Стыковка: перелёт к цели не найден");
                return AutopilotRequest.Finished;
            }
            return AutopilotRequest.None;
        }

        string PhaseName() => Phase == PhaseType.Depart ? "Перелёт к цели" : Phase == PhaseType.Correct ? "Коррекция" : "Торможение у цели";

        void Stop(Vessel v)
        {
            FlightControl.Cutoff(v);
            v.Sas = SasMode.Stability;
            v.SasHoldValid = false;
        }

        // ---------------------------------------------------------------- РСУ

        AutopilotRequest Approach(Vessel v, Vector3d port, Vector3d portT, Vector3d nT, Vector3d vrel, double dist)
        {
            v.Throttle = 0;
            v.RcsForward = 0;
            v.Sas = SasMode.Off;
            FlightControl.PointAt(v, -nT);
            double aRcs = v.RcsThrust / Math.Max(v.Mass, 1);
            if (aRcs <= 0)
            {
                Stop(v);
                v.Raise("Стыковка: нет РСУ");
                return AutopilotRequest.Finished;
            }
            var d = port - portT;
            double axial = Vector3d.Dot(d, nT);
            var lat = d - nT * axial;
            bool aligned = axial > 0 && lat.magnitude < AlignLateral &&
                           Vector3d.Angle(v.NoseP, -nT) < AlignAngleDeg * Constants.Deg2Rad;
            double goal = aligned ? 0 : StandoffDistance;
            double err = axial - goal;
            double ax = -Math.Sign(err) * Speed(Math.Abs(err), aRcs);
            if (aligned && axial < FinalZone) ax = -Math.Min(FinalSpeed, Math.Max(0.05, Math.Abs(ax)));
            var vdes = nT * ax;
            double lm = lat.magnitude;
            if (lm > 1e-6) vdes -= lat / lm * Math.Min(aligned ? 0.3 : MaxApproach, Speed(lm, aRcs));
            // Вдали от оси сначала к точке ожидания, иначе борт режет угол через цель.
            if (!aligned && dist > 3 * StandoffDistance)
            {
                var e = portT + nT * StandoffDistance - port;
                vdes = e.normalized * Speed(e.magnitude, aRcs);
            }
            var a = (vdes - vrel) / Tau;
            var cmd = v.WorldToLocal(a) / aRcs;
            v.RcsTranslate = new Vector3d(MathD.Clamp(cmd.x, -1, 1), MathD.Clamp(cmd.y, -1, 1), MathD.Clamp(cmd.z, -1, 1));
            Status = aligned ? $"Причаливание: {dist:F1} м, {-Vector3d.Dot(vrel, nT):F2} м/с"
                             : $"Выход на ось узла: {dist:F0} м, отклонение {lm:F1} м";
            return AutopilotRequest.None;
        }

        /// <summary>Закон «корня»: успеть остановиться на половине ускорения РСУ.</summary>
        static double Speed(double distance, double aRcs) => Math.Min(MaxApproach, Math.Sqrt(2 * BrakeShare * aRcs * distance));

        // ---------------------------------------------------------------- перелёт

        Vector3d AimAt(double time)
        {
            Universe.StateOf(Target, universe.Time, out var r0, out var v0);
            var o = KeplerOrbit.FromState(r0, v0, Target.Body.Mu, universe.Time);
            o.GetState(time, out var r, out _);
            return r + Target.NoseP * (Target.PortHeight() + AimStandoff);
        }

        void TargetVelocity(double time, out Vector3d vel)
        {
            Universe.StateOf(Target, universe.Time, out var r0, out var v0);
            KeplerOrbit.FromState(r0, v0, Target.Body.Mu, universe.Time).GetState(time, out _, out vel);
        }

        bool PlanTransfer(Vessel v, double t)
        {
            double mu = v.Body.Mu;
            var oc = KeplerOrbit.FromState(v.Position, v.Velocity, mu, t);
            Universe.StateOf(Target, t, out var rT, out var vT);
            var ot = KeplerOrbit.FromState(rT, vT, mu, t);
            if (!oc.IsElliptic || !ot.IsElliptic) return false;
            double P = oc.Period, Pt = ot.Period;
            // Окно старта — до синодического периода (фаза цели повторяется), но не больше двух суток.
            double syn = Math.Abs(1 / P - 1 / Pt) > 1e-12 ? 1 / Math.Abs(1 / P - 1 / Pt) : double.PositiveInfinity;
            double window = Math.Min(Math.Max(2 * P, syn), 2 * 86400);
            double minPe = Math.Min(v.Body.Radius + MinClearance, oc.PeriapsisRadius);
            var h = Vector3d.Cross(v.Position, v.Velocity);
            double best = double.PositiveInfinity, bt = 0, btof = 0;
            Vector3d bdv = Vector3d.zero;
            for (double wait = 120; wait <= window; wait += P / 24)
            {
                double td = t + wait;
                oc.GetState(td, out var r1, out var vc1);
                for (double f = 0.3; f <= 1.0 + 1e-9; f += 1.0 / 24)
                {
                    double tof = f * Pt;
                    var r2 = AimAt(td + tof);
                    if (!Lambert.Solve(r1, r2, tof, mu, h, out var v1, out var v2)) continue;
                    TargetVelocity(td + tof, out var vt2);
                    double cost = (v1 - vc1).magnitude + (vt2 - v2).magnitude + wait * WaitPenalty;
                    if (cost >= best) continue;
                    if (KeplerOrbit.FromState(r1, v1, mu, td).PeriapsisRadius < minPe) continue;
                    best = cost;
                    bt = td;
                    btof = tof;
                    bdv = v1 - vc1;
                }
            }
            if (double.IsInfinity(best)) return false;
            arrival = bt + btof;
            SetNode(v, t, bt, bdv);
            Phase = PhaseType.Depart;
            v.Raise($"Стыковка: старт через {bt - t:F0} с, перелёт {btof / 60:F0} мин, Δv ≈ {best:F0} м/с");
            return true;
        }

        /// <summary>Коррекция на середине перелёта: Ламберт из текущей орбиты в ту же точку к тому же сроку.</summary>
        void PlanCorrection(Vessel v, double t)
        {
            double mu = v.Body.Mu;
            double tc = t + Math.Max(120, 0.4 * (arrival - t));
            if (arrival - tc < 300) { PlanBrake(v, t); return; }
            var oc = KeplerOrbit.FromState(v.Position, v.Velocity, mu, t);
            oc.GetState(tc, out var r1, out var vc1);
            if (!Lambert.Solve(r1, AimAt(arrival), arrival - tc, mu, Vector3d.Cross(v.Position, v.Velocity), out var v1, out _) ||
                (v1 - vc1).magnitude < MinCorrection)
            {
                PlanBrake(v, t);
                return;
            }
            SetNode(v, t, tc, v1 - vc1);
            Phase = PhaseType.Correct;
        }

        /// <summary>Торможение в точке встречи: уравнять скорость с целью.</summary>
        void PlanBrake(Vessel v, double t)
        {
            var oc = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, t);
            double tb = Math.Max(arrival, t + 60);
            oc.GetState(tb, out _, out var vc);
            TargetVelocity(tb, out var vt);
            SetNode(v, t, tb, vt - vc);
            Phase = PhaseType.Brake;
        }

        void SetNode(Vessel v, double t, double time, Vector3d dv)
        {
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, t);
            orbit.GetState(time, out var r, out var vel);
            var pro = vel.normalized;
            var nrm = Vector3d.Cross(r, vel).normalized;
            var rad = Vector3d.Cross(pro, nrm);
            v.Node = new ManeuverNode
            {
                Time = time,
                Prograde = Vector3d.Dot(dv, pro),
                Normal = Vector3d.Dot(dv, nrm),
                Radial = Vector3d.Dot(dv, rad),
                Remaining = dv,
            };
            node = new NodeAutopilot();
        }
    }
}
