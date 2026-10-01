using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Программа бортового вычислителя «Посадка» на тело без атмосферы (GDD §6.11, §4.8).
    /// Пассивный участок длится до момента, когда торможение всеми оставшимися ступенями погасит скорость
    /// ровно на «воротах» терминального участка: момент ищет бисекция по траектории с прогнозом торможения.
    /// Торможение — против поверхностной скорости (гравитационный разворот), газ текущей ступени подбирает
    /// тот же прогноз, отработавшая ступень отделяется. Терминальный участок — постоянное замедление до
    /// FinalSpeed на FinalHeight над рельефом и отсечка: малый газ КТДУ больше лунного веса, зависнуть
    /// нельзя, поэтому последние метры — свободное падение (у «Луны-9» — щуп 5 м и отстрел станции).
    /// Если траектория не задевает поверхность — сначала сход: перицентр на DeorbitPeriapsis.
    /// </summary>
    public sealed class LandingAutopilot
    {
        public enum PhaseType
        {
            Deorbit,
            Coast,
            Braking,
            Terminal,
            Done,
        }

        public readonly CelestialBody Target;
        public PhaseType Phase = PhaseType.Coast;
        public bool AutoStage = true;
        public string Status { get; private set; } = "";
        /// <summary>Момент запуска торможения по последнему расчёту, NaN — ещё не считали.</summary>
        public double IgnitionTime { get; private set; } = double.NaN;

        /// <summary>Скорость на воротах терминального участка, м/с.</summary>
        const double GateSpeed = 40;
        /// <summary>
        /// Чистое замедление терминального участка, м/с². Пара с малым газом: у пустеющей станции малый газ
        /// КТДУ (0,25 × 16 кН / ~700 кг ≈ 5,7 м/с²) должен оставаться ниже TerminalDecel + g.
        /// </summary>
        const double TerminalDecel = 6;
        /// <summary>Отсечка: FinalSpeed на FinalHeight над рельефом — падение даёт у Луны ~3,5 м/с.</summary>
        const double FinalHeight = 3, FinalSpeed = 1.5;
        /// <summary>Доля тяги, закладываемая в прогноз на будущие ступени, — запас на ошибки и рельеф.</summary>
        const double PlanThrottle = 0.85;
        /// <summary>Перицентр схода, м над средним радиусом: ниже — уже торможение по прогнозу.</summary>
        const double DeorbitPeriapsis = 15000;
        const double PredictStep = 0.5;
        const int MaxPredictSteps = 20000;
        /// <summary>Пауза на разделение в прогнозе, с.</summary>
        const double StagingPause = 3;
        /// <summary>Пересчёт газа на торможении, с.</summary>
        const double ReplanPeriod = 0.5;
        /// <summary>За сколько секунд до запуска торможения ускорение времени сбрасывается: успеть развернуться.</summary>
        const double IgnitionWarpMargin = 60;

        /// <summary>Высота ворот над рельефом: с неё постоянное замедление гасит GateSpeed до FinalSpeed.</summary>
        public static double GateAltitude => (GateSpeed * GateSpeed - FinalSpeed * FinalSpeed) / (2 * TerminalDecel) + FinalHeight;

        double stageCooldown, replanTimer, throttleCmd = PlanThrottle, planTime = double.NaN;
        CelestialBody planBody;

        public LandingAutopilot(CelestialBody target)
        {
            Target = target;
        }

        public AutopilotRequest Update(Vessel v, double t, double dt)
        {
            if (!v.Alive) return AutopilotRequest.Finished;
            stageCooldown -= dt;
            replanTimer -= dt;
            v.PilotInput = Vector3d.zero;
            if (v.IsLanded)
            {
                FlightControl.Cutoff(v);
                Phase = PhaseType.Done;
                v.Sas = SasMode.Stability;
                v.SasHoldValid = false;
                Status = "Посадка выполнена";
                return AutopilotRequest.Finished;
            }
            v.Sas = SasMode.Off;
            if (v.Body != Target)
            {
                FlightControl.Cutoff(v);
                Status = $"Ждём сферу влияния: {Target.Name}";
                return AutopilotRequest.None;
            }

            var r = v.Position;
            var spin = FlightPhysics.SpinAxis(Target, t);
            var vSurf = v.Velocity - Vector3d.Cross(spin, r);
            double speed = vSurf.magnitude;
            var retro = speed > 0.5 ? -vSurf / speed : r.normalized;
            double h = Target.AltitudeAboveTerrain(r);

            switch (Phase)
            {
                case PhaseType.Deorbit:
                {
                    var orbit = KeplerOrbit.FromState(r, v.Velocity, Target.Mu, t);
                    double peAlt = orbit.PeriapsisRadius - Target.Radius;
                    var dir = -v.Velocity.normalized;
                    FlightControl.PointAt(v, dir);
                    if (peAlt <= DeorbitPeriapsis)
                    {
                        FlightControl.Cutoff(v);
                        Phase = PhaseType.Coast;
                        v.Raise($"Сход выполнен: перицентр {peAlt / 1000:F0} км");
                        break;
                    }
                    if (TryStage(v, out var req)) return req;
                    bool aligned = Vector3d.Angle(v.NoseP, dir) < 3 * Constants.Deg2Rad;
                    if (aligned || v.AnyEngineRunning) FlightControl.Ignite(v, 1);
                    else FlightControl.Cutoff(v);
                    Status = $"Сход: перицентр {peAlt / 1000:F0} км";
                    break;
                }

                case PhaseType.Coast:
                {
                    FlightControl.Cutoff(v);
                    FlightControl.PointAt(v, retro);
                    var orbit = KeplerOrbit.FromState(r, v.Velocity, Target.Mu, t);
                    if (!LowEnough(orbit))
                    {
                        Phase = PhaseType.Deorbit;
                        v.Raise("Посадка: траектория не задевает поверхность — сход");
                        break;
                    }
                    double tIgn = CachedIgnition(v, orbit, t);
                    double lead = FlightControl.NeedsUllageForStart(v) ? 4 : 0;
                    Status = $"Пассивный участок: торможение через {tIgn - t:F0} с";
                    if (t >= tIgn - lead)
                    {
                        Phase = PhaseType.Braking;
                        throttleCmd = PlanThrottle;
                        replanTimer = 0;
                        v.Raise($"Торможение: {h / 1000:F1} км над рельефом, {speed:F0} м/с");
                    }
                    break;
                }

                case PhaseType.Braking:
                {
                    FlightControl.PointAt(v, retro);
                    if (TryStage(v, out var req)) return req;
                    if (replanTimer <= 0)
                    {
                        replanTimer = ReplanPeriod;
                        throttleCmd = ChooseThrottle(v, t);
                    }
                    FlightControl.Ignite(v, throttleCmd);
                    Status = $"Торможение: {h / 1000:F1} км, {speed:F0} м/с, газ {throttleCmd * 100:F0}%";
                    if (speed <= GateSpeed + 5 || h < GateAltitude && speed < 150)
                    {
                        Phase = PhaseType.Terminal;
                        v.Raise($"Терминальный участок: {h:F0} м, {speed:F0} м/с");
                    }
                    break;
                }

                case PhaseType.Terminal:
                    return Terminal(v, vSurf);
            }
            return AutopilotRequest.None;
        }

        /// <summary>Отработавшую ступень — долой; кончилось всё — программа завершена.</summary>
        bool TryStage(Vessel v, out AutopilotRequest req)
        {
            req = AutopilotRequest.None;
            if (FlightControl.AvailableThrust(v, out _) > 0) return false;
            if (AutoStage && v.HasNextStage)
            {
                if (stageCooldown <= 0)
                {
                    stageCooldown = 1;
                    req = AutopilotRequest.Stage;
                }
                return true;
            }
            FlightControl.Cutoff(v);
            Phase = PhaseType.Done;
            v.Raise("Посадка: топливо кончилось");
            req = AutopilotRequest.Finished;
            return true;
        }

        /// <summary>
        /// Постоянное замедление к FinalSpeed на FinalHeight: вертикально — по формуле пути торможения,
        /// по горизонтали — гасим остаток за то же время. Малый газ сильнее нужного — отсечка и падение,
        /// перезапуск, когда потребная тяга снова выше малого газа.
        /// </summary>
        AutopilotRequest Terminal(Vessel v, Vector3d vSurf)
        {
            if (TryStage(v, out var req)) return req;
            var r = v.Position;
            double rm = r.magnitude;
            var up = r / rm;
            v.MassProperties(out double mass, out double com, out _, out _);
            double h = Target.AltitudeAboveTerrain(r) - com;
            double vz = Vector3d.Dot(vSurf, up);
            var vh = vSurf - up * vz;
            double down = -vz;
            double g = Target.Mu / (rm * rm);
            double d = h - FinalHeight;
            double aMax = FlightControl.AvailableThrust(v, out _) / mass;
            double kMin = MinThrottle(v);

            double aVert = down > FinalSpeed && d > 0
                ? (down * down - FinalSpeed * FinalSpeed) / (2 * d) + g - vh.sqrMagnitude / rm
                : 0;
            double tGo = d > 0 ? 2 * d / Math.Max(down + FinalSpeed, 0.5) : 0;
            var aVec = up * Math.Max(0, aVert) - vh / Math.Max(tGo, 2);
            double cmd = aVec.magnitude / aMax;
            FlightControl.PointAt(v, aVec.magnitude > 0.05 ? aVec : up);

            bool running = v.AnyEngineRunning;
            if (d <= 0 || running && cmd < kMin && down < 2 * FinalSpeed)
                FlightControl.Cutoff(v);
            else if (running || cmd >= Math.Max(1.3 * kMin, 0.1))
                FlightControl.Ignite(v, MathD.Clamp(cmd, 0.01, 1));
            else
                FlightControl.Cutoff(v);
            Status = $"Посадка: {h:F0} м, {vz:F1} м/с, гориз. {vh.magnitude:F1} м/с, газ {(running ? cmd * 100 : 0):F0}%";
            return AutopilotRequest.None;
        }

        /// <summary>Перицентр достаточно низок, чтобы тормозить прямо с траектории.</summary>
        bool LowEnough(KeplerOrbit orbit) => orbit.PeriapsisRadius < Target.Radius + DeorbitPeriapsis + 5000;

        /// <summary>До какого момента можно ускорять время (Universe.WarpLimitTime): на рельсах программа стоит.</summary>
        public double WarpLimit(Vessel v, KeplerOrbit orbit, double t)
        {
            if (Phase != PhaseType.Coast || v.Body != Target) return double.PositiveInfinity;
            if (!LowEnough(orbit)) return t; // сход считается в полной физике
            return CachedIgnition(v, orbit, t) - IgnitionWarpMargin;
        }

        double CachedIgnition(Vessel v, KeplerOrbit orbit, double t)
        {
            double age = Math.Abs(t - planTime);
            bool near = IgnitionTime - t < 2 * IgnitionWarpMargin;
            if (planBody != v.Body || double.IsNaN(planTime) || age > (near ? 1 : 10))
            {
                IgnitionTime = PlanIgnition(v, orbit, t);
                planTime = t;
                planBody = v.Body;
            }
            return IgnitionTime;
        }

        /// <summary>
        /// Последний момент на траектории, когда торможение (будущие ступени на PlanThrottle) ещё выводит на
        /// ворота над рельефом. Бисекция: чем позже запуск, тем ниже точка, где погашена скорость.
        /// </summary>
        double PlanIgnition(Vessel v, KeplerOrbit orbit, double t)
        {
            double tHit = orbit.PeriapsisRadius < Target.Radius
                ? orbit.NextTimeAtRadius(Target.Radius, t, false)
                : t + orbit.TimeToPeriapsis(t);
            if (double.IsNaN(tHit)) return double.PositiveInfinity;
            var stages = v.RemainingStats();
            double Margin(double tt)
            {
                orbit.GetState(tt, out var rr, out var vv);
                return PredictGate(Target, rr, vv, tt, stages, PlanThrottle) - GateAltitude;
            }
            if (Margin(t) <= 0) return t;
            double lo = t, hi = tHit;
            if (Margin(hi) > 0) return hi;
            for (int i = 0; i < 60 && hi - lo > 0.2; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Margin(mid) > 0) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>Газ текущей ступени, при котором прогноз выводит ровно на ворота (будущие — на PlanThrottle).</summary>
        double ChooseThrottle(Vessel v, double t)
        {
            var stages = v.RemainingStats();
            var r = v.Position;
            var vel = v.Velocity;
            double kMin = Math.Max(MinThrottle(v), 0.05);
            double Margin(double k) => PredictGate(Target, r, vel, t, stages, k) - GateAltitude;
            if (Margin(1) <= 0) return 1;
            if (Margin(kMin) >= 0) return kMin;
            double lo = kMin, hi = 1;
            for (int i = 0; i < 10; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Margin(mid) > 0) hi = mid;
                else lo = mid;
            }
            return hi;
        }

        /// <summary>
        /// Прогноз торможения против поверхностной скорости: высота над рельефом (м), на которой скорость
        /// упадёт до GateSpeed. Отрицательная — удар или топлива не хватает; величина тогда неважна.
        /// </summary>
        /// <param name="firstThrottle">Газ первой (текущей) ступени; остальные — на PlanThrottle.</param>
        public static double PredictGate(CelestialBody body, Vector3d r, Vector3d vel, double t,
            List<StageStats> stages, double firstThrottle)
        {
            var spin = FlightPhysics.SpinAxis(body, t);
            double mu = body.Mu, radius = body.Radius;
            double terrainBand = (body.Terrain?.Amplitude ?? 0) * 1.5 + 100;
            int si = 0;
            double m = stages.Count > 0 ? stages[0].StartMass : 1, pause = 0;
            double prevSpeed = (vel - Vector3d.Cross(spin, r)).magnitude, prevAlt = double.NaN;
            const double dt = PredictStep;

            for (int i = 0; i < MaxPredictSteps; i++)
            {
                double acc = 0, mdot = 0;
                if (pause > 0) pause -= dt;
                else
                {
                    while (si < stages.Count && (stages[si].BurnTime <= 0 || m <= stages[si].EndMass + 1e-6))
                    {
                        si++;
                        if (si < stages.Count)
                        {
                            m = stages[si].StartMass;
                            pause = StagingPause;
                        }
                    }
                    if (si >= stages.Count) return -prevSpeed; // нечем гасить остаток
                    if (pause <= 0)
                    {
                        var s = stages[si];
                        double k = si == 0 ? firstThrottle : PlanThrottle;
                        double mdotFull = (s.StartMass - s.EndMass) / s.BurnTime;
                        double ve = s.DeltaVVac / Math.Log(s.StartMass / s.EndMass);
                        mdot = k * mdotFull;
                        acc = mdot * ve / m;
                    }
                }

                // Средняя точка: тяга против поверхностной скорости, центральное поле.
                var a1 = Accel(r, vel, spin, mu, acc);
                var rMid = r + vel * (dt / 2);
                var vMid = vel + a1 * (dt / 2);
                var a2 = Accel(rMid, vMid, spin, mu, acc);
                r += vMid * dt;
                vel += a2 * dt;
                m -= mdot * dt;
                t += dt;

                double speed = (vel - Vector3d.Cross(spin, r)).magnitude;
                double alt = r.magnitude - radius;
                if (alt < terrainBand) alt -= body.SurfaceHeight(body.OrientationAt(t).Inverse * r);
                if (alt <= 0) return -speed;
                if (speed <= GateSpeed)
                {
                    if (double.IsNaN(prevAlt)) return alt;
                    double f = (prevSpeed - GateSpeed) / Math.Max(prevSpeed - speed, 1e-9);
                    return prevAlt + f * (alt - prevAlt);
                }
                prevSpeed = speed;
                prevAlt = alt;
            }
            return -prevSpeed;
        }

        static Vector3d Accel(Vector3d r, Vector3d vel, Vector3d spin, double mu, double thrustAcc)
        {
            double rm = r.magnitude;
            var a = r * (-mu / (rm * rm * rm));
            if (thrustAcc > 0)
            {
                var vs = vel - Vector3d.Cross(spin, r);
                double s = vs.magnitude;
                a += s > 1e-6 ? vs * (-thrustAcc / s) : r * (thrustAcc / rm);
            }
            return a;
        }

        /// <summary>Малый газ взведённых двигателей с топливом.</summary>
        static double MinThrottle(Vessel v)
        {
            double k = 0;
            var secs = v.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
                if (v.Attached[i] && v.Armed[i] && secs[i].HasEngine && v.Propellant[i] > 0)
                    k = Math.Max(k, secs[i].Engine.MinThrottle);
            return k;
        }
    }
}
