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
                if (!v.Running[i] && !v.CanIgnite(i)) continue;
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

        /// <summary>Следующий шаг программы поджигает РДТТ (взведение или отделение с запуском следующей).</summary>
        public static bool NextIsSolidKick(Vessel v)
        {
            if (!v.HasNextStage) return false;
            var a = v.Design.Sequence[v.NextApplicable(v.NextStage)];
            int s = a.Type == StageActionType.Ignite ? a.Section
                  : a.Type == StageActionType.Separate && a.IgniteNext ? v.Design.NextCore(a.Section) : -1;
            var secs = v.Design.Sections;
            return s >= 0 && s < secs.Count && v.Attached[s] && secs[s].HasEngine && secs[s].Engine.Solid &&
                   v.Propellant[s] > 0;
        }

        public static bool SolidBurning(Vessel v)
        {
            var secs = v.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
                if (v.Attached[i] && v.Running[i] && secs[i].Engine.Solid) return true;
            return false;
        }

        /// <summary>Суммарное время работы оставшихся РДТТ, с (с секундной паузой на каждое разделение).</summary>
        public static double SolidBurnTime(Vessel v)
        {
            double t = 0;
            var secs = v.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                var s = secs[i];
                if (!v.Attached[i] || !s.HasEngine || !s.Engine.Solid || v.Propellant[i] <= 0) continue;
                t += v.Propellant[i] / (s.Engine.MassFlow * s.EngineCount) + 1;
            }
            return t;
        }

        /// <summary>
        /// Следующий шаг программы взводит двигатель с топливом. Нужен, когда у текущей ступени кончились
        /// запуски, а не топливо: правило «отсечка по выработке» такую ступень не сбросит.
        /// </summary>
        public static bool NextStageBringsEngine(Vessel v)
        {
            if (!v.HasNextStage) return false;
            var a = v.Design.Sequence[v.NextApplicable(v.NextStage)];
            int s = a.Type == StageActionType.Ignite ? a.Section
                  : a.Type == StageActionType.Separate && a.IgniteNext ? v.Design.NextCore(a.Section) : -1;
            var secs = v.Design.Sections;
            return s >= 0 && s < secs.Count && v.Attached[s] && secs[s].HasEngine && v.Propellant[s] > 0 &&
                   v.CanIgnite(s);
        }

        /// <summary>
        /// Следующий шаг — сброс радиальной группы с выработанным топливом: её сбрасывают сразу, пока ядро ещё работает
        /// (§5.4, боковые блоки «Союза» уходят на 118-й секунде) — иначе ядро тащит пустые блоки до своей выработки.
        /// </summary>
        public static bool NextDropsSpentRadial(Vessel v)
        {
            if (!v.HasNextStage) return false;
            var a = v.Design.Sequence[v.NextApplicable(v.NextStage)];
            if (a.Type != StageActionType.Separate) return false;
            var s = v.Design.Sections[a.Section];
            return s.IsRadial && s.HasEngine && v.Attached[a.Section] && v.Propellant[a.Section] <= 0;
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
            v.RcsTranslate = Vector3d.zero;
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
        /// <summary>
        /// Нормаль плоскости целевой орбиты (инерциальная), ноль — без цели. Наведение по горизонтальной скорости
        /// на Луне подхватывает её вращение (4,6 м/с на восток) и уводит с любого азимута: взлёт LM на запад дал
        /// ту же i 4,29°, что и на восток. С нормалью тяга гасит и боковую скорость.
        /// </summary>
        public Vector3d PlaneNormal;
        public bool AutoStage = true;
        public PhaseType Phase = PhaseType.Vertical;

        /// <summary>Скоростной напор, при котором допустимый угол атаки ещё 90°: q·sin α ≤ 2400 Па.</summary>
        const double QAlphaBudget = 2400;
        const double FairingAltitude = 110000;
        /// <summary>Замкнутое наведение — после максимального напора: q ниже GuidedQ и высота выше GuidedAltitude.</summary>
        public const double GuidedQ = 1500;
        public const double GuidedAltitude = 35000;
        /// <summary>Нижняя граница времени до орбиты в законе наведения, с: у отсечки закон вырождается.</summary>
        const double MinTimeToGo = 10;
        /// <summary>Недобор скорости, ниже которого направление по плоскости цели уже шумит, м/с.</summary>
        const double PlaneGapMin = 2;
        /// <summary>
        /// Постоянная времени набора вертикальной скорости перед твердотопливным «пинком», с. Закон до орбиты
        /// задирал «Редстоун» Juno до 72° (vz 1691 при vh 2095): вертикаль уходила в лишний подъём, а горизонтали
        /// РДТТ не хватало 130 м/с (Pe −174 км). Перед РДТТ жидкостная ступень набирает ровно ту vz, что донесёт
        /// до апоцентра TargetAltitude, остальное — в горизонталь.
        /// </summary>
        const double KickLoftTime = 10;
        double stageCooldown;

        public string Status { get; private set; } = "";

        /// <summary>Вертикальный подъём до этой поверхностной скорости или высоты — потом разворот.</summary>
        public const double VerticalSpeedEnd = 100, VerticalAltitudeEnd = 2000;
        /// <summary>
        /// Без атмосферы (взлёт LM с Луны) вертикальный участок — только отрыв от рельефа, дальше сразу замкнутое
        /// наведение: программа тангажа с крутым подъёмом до 35 км на Луне сожгла всю APS (Ap 66 км, Pe −337 км).
        /// Настоящий LM уходил с вертикали через ~10 с, на 15–20 м/с.
        /// </summary>
        public const double AirlessVerticalSpeedEnd = 20;

        /// <summary>Программный тангаж над горизонтом на высоте h, рад: 90°·(1 − √(h/H)). Им ведёт автопилот
        /// и его же подсказывает HUD ручному пилоту (FlightHud.Tutor) — один закон на двоих.</summary>
        public static double ProgramPitch(double altitude, double turnAltitude) =>
            Math.PI / 2 * (1 - Math.Sqrt(MathD.Clamp01(altitude / turnAltitude)));

        /// <summary>
        /// Азимут старта в плоскость орбиты цели, градусы от севера (рандеву, §6.6). Окололунная орбита «Аполлона»
        /// ретроградная (i ≈ 176°): взлёт LM на восток дал i 4° — плоскости разошлись на 172°, сближение невозможно.
        /// Направление — вдоль движения цели над точкой старта: h × up.
        /// </summary>
        public static double AzimuthToPlane(Vessel v, Vessel target, double t)
        {
            Universe.StateOf(target, t, out var rT, out var vT);
            FlightControl.LocalFrame(v, t, out var up, out var north, out var east);
            var dir = Vector3d.Cross(Vector3d.Cross(rT, vT), up);
            return Math.Atan2(Vector3d.Dot(dir, east), Vector3d.Dot(dir, north)) / Constants.Deg2Rad;
        }

        /// <summary>Взлёт в плоскость орбиты цели: азимут старта и нормаль, к которой наведение доворачивает скорость.</summary>
        public void AimAtPlane(Vessel v, Vessel target, double t)
        {
            Universe.StateOf(target, t, out var rT, out var vT);
            PlaneNormal = Vector3d.Cross(rT, vT).normalized;
            Azimuth = AzimuthToPlane(v, target, t);
        }

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
                // Juno I: жидкостная ступень выработана ниже орбиты, дальше — РДТТ. Их тягу не дросселировать и не
                // отложить, поэтому их поджигают в апоцентре пассивного участка, а не сразу (GDD §6.3).
                if (burning && Phase != PhaseType.Circularize && !v.AnyEngineRunning && FlightControl.NextIsSolidKick(v) &&
                    peAlt < v.Body.AtmosphereTop + 10000)
                {
                    Phase = PhaseType.Coast;
                    Raise(v, $"Выработка: апоцентр {apAlt / 1000:F0} км — твердотопливные ступени в апоцентре");
                }
                else if (FlightControl.NextDropsSpentRadial(v))
                    return RequestStage();
                // Отсечка по выработке: сбросить пустую ступень, следующую запустить.
                else if (burning && !v.AnyEngineRunning && v.HasNextStage &&
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
                    if (!v.Body.HasAtmosphere && v.SurfaceSpeed > AirlessVerticalSpeedEnd) Phase = PhaseType.Guided;
                    else if (v.SurfaceSpeed > VerticalSpeedEnd || v.Altitude > VerticalAltitudeEnd) Phase = PhaseType.PitchProgram;
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
                    double rT = v.Body.Radius + TargetAltitude;
                    double sinT = GuidedSin(v, up, heading, TargetAltitude, out double dvGo, out double T, out var hdir, PlaneNormal);
                    double a = FlightControl.AvailableThrust(v, out _) / v.Mass;
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
                    if (FlightControl.NextIsSolidKick(v) && double.IsInfinity(burn))
                    {
                        // Тяга РДТТ неуправляема — импульс центрируем на апоцентре по суммарному времени работы.
                        double kick = FlightControl.SolidBurnTime(v);
                        Status = $"Пассивный участок: до апоцентра {tAp:F0} с, РДТТ {kick:F0} с";
                        if (stageCooldown > 0 || !(tAp <= kick / 2 + 4 || v.VerticalSpeed < 0)) break;
                        Phase = PhaseType.Circularize;
                        v.Throttle = 1;
                        return RequestStage();
                    }
                    // Сопротивление в верхних слоях подъедает апоцентр — догоняем, пока ещё низко.
                    if (v.Body.HasAtmosphere && v.Altitude < v.Body.AtmosphereTop && apAlt < TargetAltitude - 2000)
                    {
                        Phase = PhaseType.Guided;
                        break;
                    }
                    if (double.IsInfinity(burn))
                    {
                        // Последний запуск ступени истрачен, а дальше по программе — ступень с двигателем: сбросить.
                        if (FlightControl.NextStageBringsEngine(v) && stageCooldown <= 0) return RequestStage();
                        Status = "Нечем довыводить: нет двигателя с топливом";
                        break;
                    }
                    if (tAp <= burn / 2 + 4 || v.VerticalSpeed < 0) Phase = PhaseType.Circularize;
                    break;
                }

                case PhaseType.Circularize:
                {
                    double sinT = CircularizeSin(v, up, out var hdir);
                    var dir = hdir * Math.Sqrt(1 - sinT * sinT) + up * sinT;
                    FlightControl.PointAt(v, dir);
                    bool aligned = Vector3d.Angle(v.NoseP, dir) < 5 * Constants.Deg2Rad;
                    if (aligned) FlightControl.Ignite(v, 1);
                    Status = $"Довыведение: перицентр {peAlt / 1000:F0} км";
                    double minPe = Math.Max(v.Body.AtmosphereTop + 10000, TargetAltitude - 2000);
                    // РДТТ не выключить: довыведение ими кончается, когда отгорят все (Explorer-1 — 358 × 2550 км).
                    bool solidLeft = FlightControl.SolidBurning(v) || FlightControl.NextIsSolidKick(v);
                    if (!solidLeft && (peAlt > minPe || orbit.E < 0.002 && peAlt > v.Body.AtmosphereTop))
                    {
                        FlightControl.Cutoff(v);
                        return Finish(v, peAlt, apAlt);
                    }
                    if (!double.IsInfinity(FlightControl.BurnTime(v, 1)) || solidLeft) break;
                    if (FlightControl.NextStageBringsEngine(v) && stageCooldown <= 0) return RequestStage();
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

        /// <summary>
        /// Синус тангажа замкнутого наведения над горизонтом (по горизонтальной скорости hdir). Линейный по времени
        /// закон: к моменту выхода высота цели и нулевая вертикальная скорость. Им ведёт автопилот и его же
        /// подсказывает HUD ручному пилоту (FlightHud.Tutor) — один закон на двоих, как ProgramPitch.
        /// </summary>
        public static double GuidedSin(Vessel v, Vector3d up, Vector3d heading, double targetAltitude,
                                       out double dvGo, out double timeToGo, out Vector3d hdir,
                                       Vector3d planeNormal = default)
        {
            double r = v.Position.magnitude;
            double rT = v.Body.Radius + targetAltitude;
            double g = v.Body.Mu / (r * r);
            double vz = Vector3d.Dot(v.Velocity, up);
            var hv = v.Velocity - up * vz;
            double vh = hv.magnitude;
            hdir = vh > 1 ? hv / vh : heading;
            double vCirc = Math.Sqrt(v.Body.Mu / rT);
            if (planeNormal.sqrMagnitude > 0)
            {
                // Горизонталь — по недобору скорости до круговой в плоскости цели (вдоль её движения: n × up).
                var gap = Vector3d.Cross(planeNormal, up).normalized * vCirc - hv;
                if (gap.magnitude > PlaneGapMin) hdir = gap.normalized;
            }
            dvGo = Math.Sqrt((vCirc - vh) * (vCirc - vh) + vz * vz);
            double T = timeToGo = Math.Max(MinTimeToGo, TimeToGo(v, dvGo));
            double azReq = 6 * (rT - r) / (T * T) - 4 * vz / T;
            if (FlightControl.NextIsSolidKick(v))
            {
                double gEff = Math.Max(0.5, g - vh * vh / r);
                double vzReq = Math.Sqrt(2 * gEff * Math.Max(0, rT - r));
                azReq = (vzReq - vz) / KickLoftTime;
            }
            double a = FlightControl.AvailableThrust(v, out _) / v.Mass;
            return a > 0 ? MathD.Clamp((azReq + g - vh * vh / r) / a, -0.6, 0.95) : 0;
        }

        /// <summary>Синус тангажа довыведения: горизонтально по скорости + поправка, держащая вертикальную скорость у нуля.</summary>
        public static double CircularizeSin(Vessel v, Vector3d up, out Vector3d hdir)
        {
            double r = v.Position.magnitude;
            double g = v.Body.Mu / (r * r);
            var hv = Vector3d.ProjectOnPlane(v.Velocity, up);
            double vh = hv.magnitude;
            hdir = hv.normalized;
            double a = FlightControl.AvailableThrust(v, out _) / v.Mass;
            return a > 0 ? MathD.Clamp((g - vh * vh / r - 0.05 * v.VerticalSpeed) / a, -0.5, 0.9) : 0;
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
        public static Vector3d LimitAoA(Vessel v, double t, Vector3d dir)
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
        public static double CircularizeDv(Vessel v, KeplerOrbit orbit, double t)
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
        /// <summary>
        /// Прожиг длиннее этой доли периода — «длинный» (разгон к Луне S-IVB/блоком Д, LOI): за время работы борт
        /// проходит десятки градусов дуги, и импульс по неподвижному инерциальному направлению теряет до 60 м/с
        /// (замер: апогей 227–323 тыс. км вместо 380). Такой прожиг ведётся по осям текущей орбиты и
        /// отсекается по энергии — так же разгон к Луне отсекала и настоящая система управления.
        /// Пара: короткие прожиги (сход «Востока», коррекции) остаются на старом законе — порог их не задевает.
        /// </summary>
        const double LongBurnPeriodShare = 0.02;
        /// <summary>Длинный прожиг без замкнутой орбиты (гипербола) — по времени, с.</summary>
        const double LongBurnSeconds = 60;
        /// <summary>Доля тангенциальной составляющей, при которой отсечка по энергии осмысленна.</summary>
        const double EnergyCutoffAlong = 0.9;

        public string Status { get; private set; } = "";
        bool started;
        ManeuverNode planned;
        bool energyMode;
        double targetEnergy, sign, cPro, cNrm, cRad;

        /// <summary>Запомнить цель длинного прожига: энергию орбиты после импульса и его компоненты в осях орбиты.</summary>
        void Plan(Vessel v, ManeuverNode node, double t)
        {
            planned = node;
            energyMode = false;
            var orbit = NodePlanner.CurrentOrbit(v, t);
            orbit.GetState(node.Time, out var r, out var vel);
            var dv = node.Remaining;
            double total = dv.magnitude;
            if (total < 1) return;
            var pro = vel.normalized;
            var nrm = Vector3d.Cross(r, vel).normalized;
            var rad = Vector3d.Cross(pro, nrm);
            cPro = Vector3d.Dot(dv, pro) / total;
            cNrm = Vector3d.Dot(dv, nrm) / total;
            cRad = Vector3d.Dot(dv, rad) / total;
            double burn = FlightControl.BurnTime(v, total);
            // Текущая ступень без запусков (блок И «Молнии» перед разгоном блоком Л): время бесконечно, и без
            // повторного плана после её сброса разгон шёл «коротким» законом — апоцентр 271 тыс. км, Луна мимо.
            if (double.IsInfinity(burn)) { planned = null; return; }
            double period = orbit.Period;
            bool longBurn = double.IsInfinity(period) ? burn > LongBurnSeconds : burn > LongBurnPeriodShare * period;
            if (!longBurn || Math.Abs(cPro) < EnergyCutoffAlong) return;
            var after = vel + dv;
            targetEnergy = after.sqrMagnitude / 2 - v.Body.Mu / r.magnitude;
            sign = Math.Sign(cPro);
            energyMode = true;
        }

        public AutopilotRequest Update(Vessel v, double t, double dt)
        {
            var node = v.Node;
            if (!v.Alive || node == null) return AutopilotRequest.Finished;
            v.Sas = SasMode.Off;
            // Флаг — по факту работы двигателя, а не по команде: запуск происходит уже в шаге физики,
            // и в окне осадки (до startAt) следующий вызов иначе заглушил бы его, сжигая попытку запуска.
            if (v.AnyEngineRunning) started = true;
            if (planned != node) Plan(v, node, t);
            double left = node.Total;
            Vector3d dir;
            if (energyMode)
            {
                // Остаток — недобор энергии, пересчитанный в м/с по текущей скорости: dE = v·dv.
                double e = v.Velocity.sqrMagnitude / 2 - v.Body.Mu / v.Position.magnitude;
                left = Math.Max(0, sign * (targetEnergy - e) / Math.Max(v.Velocity.magnitude, 1));
                var pro = v.Velocity.normalized;
                var nrm = Vector3d.Cross(v.Position, v.Velocity).normalized;
                var rad = Vector3d.Cross(pro, nrm);
                dir = (pro * cPro + nrm * cNrm + rad * cRad).normalized;
            }
            else dir = node.Remaining.normalized;
            if (left < 0.1 || !energyMode && started && Vector3d.Dot(node.Remaining, v.NoseP) < 0)
            {
                FlightControl.Cutoff(v);
                v.Node = null;
                v.Sas = SasMode.Stability;
                v.SasHoldValid = false;
                v.Raise($"Манёвр выполнен, остаток {left:F1} м/с");
                return AutopilotRequest.Finished;
            }
            FlightControl.PointAt(v, dir);
            double burn = FlightControl.BurnTime(v, left);
            if (double.IsInfinity(burn) && !v.AnyEngineRunning && FlightControl.NextStageBringsEngine(v))
            {
                // Ступень без запусков (S-IVB после разгона к Луне) — сбросить, дальше работает следующая.
                FlightControl.Cutoff(v);
                Status = "Сброс ступени без запусков";
                return AutopilotRequest.Stage;
            }
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
