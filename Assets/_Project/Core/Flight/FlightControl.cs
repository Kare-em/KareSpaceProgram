using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Управление ориентацией (GDD §4.9): ручки пилота и SAS. Вся математика — в связанных осях:
    /// перевод угловых векторов между P и U меняет их знак (отражение осей), а в связанных осях
    /// ошибка ориентации, угловая скорость и момент живут в одной системе.
    /// </summary>
    public static class FlightControl
    {
        /// <summary>Доля доступного углового ускорения, которую закладываем на торможение вращения.</summary>
        const double BrakeShare = 0.6;
        /// <summary>Постоянная времени выхода на заданную угловую скорость, с.</summary>
        const double RateTau = 0.15;
        /// <summary>Линейный участок закона у нуля, рад/с на рад: без него корень даёт дребезг.</summary>
        const double LinearGain = 2.0;

        public static void Update(Vessel v, double t)
        {
            v.TorqueCommand = Vector3d.zero;
            if (!v.Alive || v.IsLanded) return;

            var tmax = v.MaxTorque(v.StaticPressure);
            var input = v.PilotInput;
            if (input.sqrMagnitude > 1e-6)
            {
                // Ось вращения в связанных осях: W → нос к «брюху» (+X) = поворот вокруг −Z,
                // D → нос к −Z = вокруг −X, E → крен вокруг −Y.
                v.TorqueCommand = new Vector3d(-input.y * tmax.x, -input.z * tmax.y, -input.x * tmax.z);
                v.SasHoldValid = false;
                return;
            }

            if (v.Sas == SasMode.Off) return;
            if (v.Sas != SasMode.Stability && TryGetSasDirection(v, v.Sas, t, out var dir))
            {
                PointAt(v, dir);
                return;
            }

            // Стабилизация: сначала гасим вращение, потом запоминаем ориентацию и держим её.
            if (!v.SasHoldValid)
            {
                HoldRates(v, Vector3d.zero);
                if (v.AngularVelocity.magnitude < 0.005)
                {
                    v.SasHold = v.Attitude;
                    v.SasHoldValid = true;
                }
                return;
            }
            var err = (v.Attitude.Inverse * v.SasHold).ToRotationVector();
            Regulate(v, err);
        }

        /// <summary>Направление режима SAS в P. Ложь — режиму не на что опереться (нет узла, нет скорости).</summary>
        public static bool TryGetSasDirection(Vessel v, SasMode mode, double t, out Vector3d dir)
        {
            dir = Vector3d.zero;
            var r = v.Position;
            // В атмосфере — относительно воздуха: так «прогрейд» совпадает с набегающим потоком.
            var vel = InAtmosphere(v) ? v.Velocity - Vector3d.Cross(FlightPhysics.SpinAxis(v.Body, t), r) : v.Velocity;
            if (mode == SasMode.Maneuver)
            {
                if (v.Node == null || v.Node.Total < 1e-3) return false;
                dir = v.Node.Remaining.normalized;
                return true;
            }
            if (vel.sqrMagnitude < 1e-4) return false;
            var pro = vel.normalized;
            var nrm = Vector3d.Cross(r, vel).normalized;
            var rad = Vector3d.Cross(pro, nrm);
            switch (mode)
            {
                case SasMode.Prograde: dir = pro; break;
                case SasMode.Retrograde: dir = -pro; break;
                case SasMode.Normal: dir = nrm; break;
                case SasMode.AntiNormal: dir = -nrm; break;
                case SasMode.RadialOut: dir = rad; break;
                case SasMode.RadialIn: dir = -rad; break;
                default: return false;
            }
            return true;
        }

        static bool InAtmosphere(Vessel v) => v.Body.HasAtmosphere && v.Altitude < v.Body.AtmosphereTop;

        /// <summary>Довернуть нос на направление dirP (система P), крен — гасить.</summary>
        public static void PointAt(Vessel v, Vector3d dirP)
        {
            var target = v.WorldToLocal(dirP.normalized);
            var err = QuaternionD.FromToRotation(Vector3d.up, target).ToRotationVector();
            err.y = 0;
            Regulate(v, err);
        }

        /// <summary>Закон «корня»: желаемая скорость такая, чтобы успеть затормозить к нулю ошибки.</summary>
        static void Regulate(Vessel v, Vector3d err)
        {
            var inertia = v.Inertia();
            var tmax = v.MaxTorque(v.StaticPressure);
            var wDes = new Vector3d(
                DesiredRate(err.x, tmax.x / inertia.x),
                DesiredRate(err.y, tmax.y / inertia.y),
                DesiredRate(err.z, tmax.z / inertia.z));
            HoldRates(v, wDes);
        }

        static double DesiredRate(double e, double alphaMax)
        {
            if (alphaMax <= 0) return 0;
            double a = Math.Abs(e);
            return Math.Sign(e) * Math.Min(Math.Sqrt(2 * BrakeShare * alphaMax * a), LinearGain * a);
        }

        static void HoldRates(Vessel v, Vector3d wDes)
        {
            var inertia = v.Inertia();
            var tmax = v.MaxTorque(v.StaticPressure);
            var w = v.AngularVelocity;
            v.TorqueCommand = new Vector3d(
                MathD.Clamp(inertia.x * (wDes.x - w.x) / RateTau, -tmax.x, tmax.x),
                MathD.Clamp(inertia.y * (wDes.y - w.y) / RateTau, -tmax.y, tmax.y),
                MathD.Clamp(inertia.z * (wDes.z - w.z) / RateTau, -tmax.z, tmax.z));
        }

        // ---------------------------------------------------------------- общие расчёты для автопилотов

        /// <summary>Местные оси в P: зенит, север, восток.</summary>
        public static void LocalFrame(Vessel v, double t, out Vector3d up, out Vector3d north, out Vector3d east)
        {
            up = v.Position.normalized;
            var pole = v.Body.OrientationAt(t) * Vector3d.forward;
            north = Vector3d.ProjectOnPlane(pole, up);
            north = north.sqrMagnitude < 1e-12 ? Vector3d.AnyPerpendicular(up) : north.normalized;
            east = Vector3d.Cross(north, up);
        }

        /// <summary>
        /// Тяга (вакуум) и расход двигателей, которые запустятся при подаче газа: уже работающие плюс
        /// взведённые с топливом и запасом запусков. Нужна для оценки времени манёвра.
        /// </summary>
        public static double AvailableThrust(Vessel v, out double massFlow)
        {
            double t = 0;
            massFlow = 0;
            var secs = v.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                var s = secs[i];
                if (!v.Attached[i] || !s.HasEngine || !v.Armed[i] || v.Propellant[i] <= 0) continue;
                if (!v.Running[i] && v.IgnitionsLeft[i] <= 0) continue;
                t += s.Engine.ThrustVac * s.EngineCount;
                massFlow += s.Engine.MassFlow * s.EngineCount;
            }
            return t;
        }

        /// <summary>Время набора dv по формуле Циолковского при текущей массе, с (∞ — нечем).</summary>
        public static double BurnTime(Vessel v, double dv)
        {
            double thrust = AvailableThrust(v, out double mdot);
            if (thrust <= 0 || mdot <= 0) return double.PositiveInfinity;
            double ve = thrust / mdot;
            double m0 = v.Mass;
            double m1 = m0 * Math.Exp(-dv / ve);
            return (m0 - m1) / mdot;
        }

        public static bool NeedsUllageForStart(Vessel v)
        {
            var secs = v.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
                if (v.Attached[i] && v.Armed[i] && !v.Running[i] && secs[i].HasEngine && secs[i].Engine.NeedsUllage &&
                    v.Propellant[i] > 0)
                    return true;
            return false;
        }

        /// <summary>
        /// Подать газ с учётом осадки: если двигателю нужна осадка, сначала РСУ вперёд, газ — когда
        /// топливо осело. Иначе запуск сорвётся и сгорит одна из попыток (GDD §6.3).
        /// </summary>
        public static void Ignite(Vessel v, double throttle)
        {
            if (v.AnyEngineRunning || !NeedsUllageForStart(v) || v.PropellantSettled)
            {
                // Таймер осадки ещё ~3 с держит топливо у заборников — РСУ можно гасить сразу.
                v.Throttle = throttle;
                v.RcsForward = 0;
                return;
            }
            v.Throttle = 0;
            v.RcsForward = 1;
        }

        public static void Cutoff(Vessel v)
        {
            v.Throttle = 0;
            v.RcsForward = 0;
        }
    }

    /// <summary>Что автопилот хочет от вселенной после шага.</summary>
    public enum AutopilotRequest
    {
        None,
        Stage,
        Finished,
    }

    /// <summary>
    /// Автопилот выведения — первая программа бортового вычислителя (GDD §6.11): вертикальный подъём,
    /// программа тангажа в плотных слоях с ограничением угла атаки по q·sin α, затем замкнутое
    /// наведение до круговой орбиты (линейный закон вертикального ускорения, время до орбиты — по
    /// остатку Δv всех ступеней). Если отсечка вышла с низким перицентром — пассивный участок и
    /// довыведение в апоцентре.
    /// </summary>
    public sealed class AscentAutopilot
    {
        public enum PhaseType
        {
            Vertical,
            PitchProgram,
            Guided,
            Coast,
            Circularize,
            Done,
        }

        public double TargetAltitude = 200000;
        /// <summary>
        /// Масштаб программы тангажа, м: тангаж = 90°·(1 − √(h/H)). Большое H — крутой подъём: у «Кары-1»
        /// тяговооружённость II ступени ~0,9, ей нужны высота и вертикальная скорость на разделении.
        /// </summary>
        public double TurnAltitude = 100000;
        /// <summary>Предел перегрузки при разгоне, g: под конец работы лёгкая ступень дросселируется.</summary>
        public double MaxG = 4.5;
        /// <summary>Азимут, градусы от севера (90 — восток, максимум выигрыша от вращения Земли).</summary>
        public double Azimuth = 90;
        public bool AutoStage = true;
        public PhaseType Phase = PhaseType.Vertical;

        /// <summary>Скоростной напор, при котором допустимый угол атаки ещё 90°: q·sin α ≤ 2400 Па.</summary>
        const double QAlphaBudget = 2400;
        const double FairingAltitude = 110000;
        /// <summary>Замкнутое наведение — после максимального напора: q ниже GuidedQ и высота выше GuidedAltitude.</summary>
        const double GuidedQ = 1500;
        const double GuidedAltitude = 35000;
        /// <summary>Нижняя граница времени до орбиты в законе наведения, с: у отсечки закон вырождается.</summary>
        const double MinTimeToGo = 10;
        double stageCooldown;

        public string Status { get; private set; } = "";

        /// <summary>Вертикальный подъём до этой поверхностной скорости или высоты — потом разворот.</summary>
        public const double VerticalSpeedEnd = 100, VerticalAltitudeEnd = 2000;

        /// <summary>Программный тангаж над горизонтом на высоте h, рад: 90°·(1 − √(h/H)). Им ведёт автопилот
        /// и его же подсказывает HUD ручному пилоту (FlightHud.Tutor) — один закон на двоих.</summary>
        public static double ProgramPitch(double altitude, double turnAltitude) =>
            Math.PI / 2 * (1 - Math.Sqrt(MathD.Clamp01(altitude / turnAltitude)));

        public AutopilotRequest Update(Vessel v, double t, double dt)
        {
            if (!v.Alive) return AutopilotRequest.Finished;
            stageCooldown -= dt;
            v.Sas = SasMode.Off;
            v.PilotInput = Vector3d.zero;
            FlightControl.LocalFrame(v, t, out var up, out var north, out var east);
            var az = Azimuth * Constants.Deg2Rad;
            var heading = east * Math.Sin(az) + north * Math.Cos(az);
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, t);
            double apAlt = orbit.ApoapsisRadius - v.Body.Radius;
            double peAlt = orbit.PeriapsisRadius - v.Body.Radius;

            // На столе — первая ступень.
            if (v.IsLanded)
            {
                v.Throttle = 1;
                if (!v.AnyEngineRunning && v.HasNextStage && stageCooldown <= 0) return RequestStage();
                return AutopilotRequest.None;
            }

            if (AutoStage && stageCooldown <= 0)
            {
                var next = v.HasNextStage ? v.Design.Sequence[v.NextStage] : default;
                bool burning = Phase == PhaseType.Vertical || Phase == PhaseType.PitchProgram || Phase == PhaseType.Guided ||
                               (Phase == PhaseType.Circularize && v.Throttle > 0);
                if (v.HasNextStage && next.Type == StageActionType.JettisonFairing &&
                    (v.Altitude > FairingAltitude || v.DynamicPressure < 10 && v.Altitude > 80000))
                    return RequestStage();
                // Отсечка по выработке: сбросить пустую ступень, следующую запустить.
                if (burning && !v.AnyEngineRunning && v.HasNextStage &&
                    (next.Type == StageActionType.Ignite ||
                     next.Type == StageActionType.Separate && v.Propellant[next.Section] <= 0))
                    return RequestStage();
            }

            switch (Phase)
            {
                case PhaseType.Vertical:
                    v.Throttle = 1;
                    FlightControl.PointAt(v, up);
                    Status = "Вертикальный подъём";
                    if (v.SurfaceSpeed > VerticalSpeedEnd || v.Altitude > VerticalAltitudeEnd) Phase = PhaseType.PitchProgram;
                    break;

                case PhaseType.PitchProgram:
                {
                    v.Throttle = 1;
                    double pitch = ProgramPitch(v.Altitude, TurnAltitude);
                    var dir = up * Math.Sin(pitch) + heading * Math.Cos(pitch);
                    FlightControl.PointAt(v, LimitAoA(v, t, dir));
                    Status = $"Разворот по тангажу: {pitch * Constants.Rad2Deg:F0}°";
                    if (v.Altitude > GuidedAltitude && v.DynamicPressure < GuidedQ) Phase = PhaseType.Guided;
                    break;
                }

                case PhaseType.Guided:
                {
                    double r = v.Position.magnitude;
                    double rT = v.Body.Radius + TargetAltitude;
                    double g = v.Body.Mu / (r * r);
                    double vz = Vector3d.Dot(v.Velocity, up);
                    var hv = v.Velocity - up * vz;
                    double vh = hv.magnitude;
                    var hdir = vh > 1 ? hv / vh : heading;
                    double vCirc = Math.Sqrt(v.Body.Mu / rT);
                    double dvGo = Math.Sqrt((vCirc - vh) * (vCirc - vh) + vz * vz);
                    double T = Math.Max(MinTimeToGo, TimeToGo(v, dvGo));
                    // Линейный по времени закон: к моменту T высота rT и нулевая вертикальная скорость.
                    double azReq = 6 * (rT - r) / (T * T) - 4 * vz / T;
                    double thrust = FlightControl.AvailableThrust(v, out _);
                    double a = thrust / v.Mass;
                    double sinT = a > 0 ? MathD.Clamp((azReq + g - vh * vh / r) / a, -0.6, 0.95) : 0;
                    var dir = hdir * Math.Sqrt(1 - sinT * sinT) + up * sinT;
                    FlightControl.PointAt(v, LimitAoA(v, t, dir));
                    v.Throttle = a > 0 ? MathD.Clamp(MaxG * Constants.G0 / a, 0, 1) : 1;
                    Status = $"Наведение: до орбиты {T:F0} с, Δv {dvGo:F0} м/с, тангаж {Math.Asin(sinT) * Constants.Rad2Deg:F0}°";

                    bool energyReached = orbit.IsElliptic && orbit.A >= rT - 500;
                    if (energyReached || peAlt >= TargetAltitude - 3000)
                    {
                        FlightControl.Cutoff(v);
                        if (peAlt > v.Body.AtmosphereTop + 10000) return Finish(v, peAlt, apAlt);
                        Phase = PhaseType.Coast;
                        Raise(v, $"Отсечка: апоцентр {apAlt / 1000:F0} км, перицентр {peAlt / 1000:F0} км — довыведение");
                    }
                    else if (!v.AnyEngineRunning && !v.HasNextStage && double.IsInfinity(FlightControl.BurnTime(v, 1)))
                    {
                        Raise(v, "Автопилот: топливо кончилось до выхода на орбиту");
                        Phase = PhaseType.Done;
                        return AutopilotRequest.Finished;
                    }
                    break;
                }

                case PhaseType.Coast:
                {
                    FlightControl.Cutoff(v);
                    var dirH = HorizontalPrograde(v, up);
                    FlightControl.PointAt(v, dirH);
                    double dv = CircularizeDv(v, orbit, t);
                    double burn = FlightControl.BurnTime(v, dv);
                    double tAp = orbit.TimeToApoapsis(t);
                    Status = $"Пассивный участок: до апоцентра {tAp:F0} с, импульс {dv:F0} м/с";
                    // Сопротивление в верхних слоях подъедает апоцентр — догоняем, пока ещё низко.
                    if (v.Body.HasAtmosphere && v.Altitude < v.Body.AtmosphereTop && apAlt < TargetAltitude - 2000)
                    {
                        Phase = PhaseType.Guided;
                        break;
                    }
                    if (double.IsInfinity(burn))
                    {
                        Status = "Нечем довыводить: нет двигателя с топливом";
                        break;
                    }
                    if (tAp <= burn / 2 + 4 || v.VerticalSpeed < 0) Phase = PhaseType.Circularize;
                    break;
                }

                case PhaseType.Circularize:
                {
                    double r = v.Position.magnitude;
                    double g = v.Body.Mu / (r * r);
                    var hv = Vector3d.ProjectOnPlane(v.Velocity, up);
                    double vh = hv.magnitude;
                    double thrust = FlightControl.AvailableThrust(v, out _);
                    double a = thrust / v.Mass;
                    // Горизонтально по скорости + вертикальная поправка: держим вертикальную скорость у нуля.
                    double sinT = a > 0 ? MathD.Clamp((g - vh * vh / r - 0.05 * v.VerticalSpeed) / a, -0.5, 0.9) : 0;
                    var dir = hv.normalized * Math.Sqrt(1 - sinT * sinT) + up * sinT;
                    FlightControl.PointAt(v, dir);
                    bool aligned = Vector3d.Angle(v.NoseP, dir) < 5 * Constants.Deg2Rad;
                    if (aligned) FlightControl.Ignite(v, 1);
                    Status = $"Довыведение: перицентр {peAlt / 1000:F0} км";
                    double minPe = Math.Max(v.Body.AtmosphereTop + 10000, TargetAltitude - 2000);
                    if (peAlt > minPe || orbit.E < 0.002 && peAlt > v.Body.AtmosphereTop)
                    {
                        FlightControl.Cutoff(v);
                        return Finish(v, peAlt, apAlt);
                    }
                    if (!double.IsInfinity(FlightControl.BurnTime(v, 1))) break;
                    if (!v.AnyEngineRunning && !v.HasNextStage)
                    {
                        Raise(v, "Автопилот: топливо кончилось до выхода на орбиту");
                        Phase = PhaseType.Done;
                        return AutopilotRequest.Finished;
                    }
                    break;
                }

                case PhaseType.Done:
                    return AutopilotRequest.Finished;
            }
            return AutopilotRequest.None;
        }

        AutopilotRequest RequestStage()
        {
            stageCooldown = 1.0;
            return AutopilotRequest.Stage;
        }

        static void Raise(Vessel v, string msg) => v.Raise(msg);

        AutopilotRequest Finish(Vessel v, double peAlt, double apAlt)
        {
            Phase = PhaseType.Done;
            v.Sas = SasMode.Stability;
            v.SasHoldValid = false;
            Raise(v, $"Орбита: {peAlt / 1000:F0} × {apAlt / 1000:F0} км");
            return AutopilotRequest.Finished;
        }

        /// <summary>Время набора dv по оставшимся ступеням (вакуумные характеристики, с паузой на разделение).</summary>
        static double TimeToGo(Vessel v, double dv)
        {
            const double stagingPause = 2;
            double t = 0;
            foreach (var s in v.RemainingStats())
            {
                if (s.DeltaVVac <= 0 || s.BurnTime <= 0) continue;
                if (dv <= s.DeltaVVac)
                {
                    double mdot = (s.StartMass - s.EndMass) / s.BurnTime;
                    double ve = s.DeltaVVac / Math.Log(s.StartMass / s.EndMass);
                    return t + s.StartMass / mdot * (1 - Math.Exp(-dv / ve));
                }
                dv -= s.DeltaVVac;
                t += s.BurnTime + stagingPause;
            }
            return t + 60;
        }

        /// <summary>В плотных слоях угол атаки не больше допустимого по q·sin α — иначе пакет сломается (GDD §4.7).</summary>
        static Vector3d LimitAoA(Vessel v, double t, Vector3d dir)
        {
            if (v.DynamicPressure < 100) return dir;
            var spin = FlightPhysics.SpinAxis(v.Body, t);
            var vSurf = v.Velocity - Vector3d.Cross(spin, v.Position);
            if (vSurf.sqrMagnitude < 1) return dir;
            double aLim = v.DynamicPressure > QAlphaBudget ? Math.Asin(QAlphaBudget / v.DynamicPressure) : Math.PI / 2;
            return LimitAngle(vSurf.normalized, dir, Math.Min(aLim, 30 * Constants.Deg2Rad));
        }

        static Vector3d HorizontalPrograde(Vessel v, Vector3d up)
        {
            var h = Vector3d.ProjectOnPlane(v.Velocity, up);
            return h.sqrMagnitude > 1e-6 ? h.normalized : v.Velocity.normalized;
        }

        /// <summary>Импульс, чтобы в апоцентре орбита стала круговой, м/с.</summary>
        static double CircularizeDv(Vessel v, KeplerOrbit orbit, double t)
        {
            double rAp = orbit.ApoapsisRadius;
            if (double.IsInfinity(rAp)) return 0;
            double vAp = Math.Sqrt(orbit.Mu * (2 / rAp - 1 / orbit.A));
            return Math.Max(0, Math.Sqrt(orbit.Mu / rAp) - vAp);
        }

        /// <summary>Повернуть want к reference так, чтобы угол между ними не превышал limit.</summary>
        static Vector3d LimitAngle(Vector3d reference, Vector3d want, double limit)
        {
            double ang = Vector3d.Angle(reference, want);
            if (ang <= limit || ang < 1e-9) return want;
            var axis = Vector3d.Cross(reference, want);
            if (axis.sqrMagnitude < 1e-18) return reference;
            return QuaternionD.AngleAxis(limit, axis) * reference;
        }
    }

    /// <summary>Исполнение манёвра (GDD §6.11): разворот на импульс, запуск за полвремени до узла, отсечка.</summary>
    public sealed class NodeAutopilot
    {
        public string Status { get; private set; } = "";
        bool started;

        public AutopilotRequest Update(Vessel v, double t, double dt)
        {
            var node = v.Node;
            if (!v.Alive || node == null) return AutopilotRequest.Finished;
            v.Sas = SasMode.Off;
            // Флаг — по факту работы двигателя, а не по команде: запуск происходит уже в шаге физики,
            // и в окне осадки (до startAt) следующий вызов иначе заглушил бы его, сжигая попытку запуска.
            if (v.AnyEngineRunning) started = true;
            double left = node.Total;
            if (left < 0.1 || started && Vector3d.Dot(node.Remaining, v.NoseP) < 0)
            {
                FlightControl.Cutoff(v);
                v.Node = null;
                v.Sas = SasMode.Stability;
                v.SasHoldValid = false;
                v.Raise($"Манёвр выполнен, остаток {left:F1} м/с");
                return AutopilotRequest.Finished;
            }
            var dir = node.Remaining.normalized;
            FlightControl.PointAt(v, dir);
            double burn = FlightControl.BurnTime(v, left);
            if (double.IsInfinity(burn))
            {
                Status = "Нечем выполнять манёвр";
                FlightControl.Cutoff(v);
                return AutopilotRequest.None;
            }
            double startAt = node.Time - burn / 2;
            bool aligned = Vector3d.Angle(v.NoseP, dir) < 3 * Constants.Deg2Rad;
            if (t < startAt - (FlightControl.NeedsUllageForStart(v) ? 4 : 0) && !started)
            {
                FlightControl.Cutoff(v);
                Status = $"Разворот на манёвр, запуск через {startAt - t:F0} с ({burn:F0} с работы)";
                return AutopilotRequest.None;
            }
            if (!aligned && !started)
            {
                Status = "Ждём ориентацию";
                return AutopilotRequest.None;
            }
            // Под конец — дросселируем, чтобы не перелететь остаток.
            double thrust = FlightControl.AvailableThrust(v, out _);
            double throttle = thrust > 0 ? MathD.Clamp(left * v.Mass / (thrust * 1.5), 0.05, 1) : 1;
            FlightControl.Ignite(v, throttle);
            Status = $"Манёвр: осталось {left:F1} м/с";
            return AutopilotRequest.None;
        }
    }
}
