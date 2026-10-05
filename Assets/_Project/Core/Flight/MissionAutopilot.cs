using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Автопилот «Миссия целиком» (GDD §6.11): проводит текущую миссию от стола до последней цели без рук —
    /// окно старта, выведение, витки, разгоны и коррекции, посадка, сход с орбиты, парашют, у «Аполлона» —
    /// расстыковка, посадка ЛМ, взлёт и стыковка или возврат к Земле. Сам он импульсов не считает: ставит узлы
    /// и запускает частные автопилоты вселенной (Ascent, NodePilot, Landing, Docking, Lunar) — те же, что жмёт
    /// игрок. Сценарии — порт полётов из тестов ядра (Tools/CoreTests), где каждый проверен целиком.
    /// Сценарий — корутина: Tick раз в кадр (и на рельсах) продвигает его на шаг; Update в физике исполняет то,
    /// чему нужен каждый шаг интегратора (программа тангажа «Редстоуна», езда лунохода).
    /// </summary>
    public sealed partial class MissionAutopilot
    {
        /// <summary>Опорная орбита, м. Пара: LunarAutopilot.ParkingAltitude и клавиша H — то же выведение на 200 км.</summary>
        const double ParkingAltitude = 200e3;
        /// <summary>Сколько от старта до разгона к Луне закладывать в окно старта, с (выведение + полвитка; TestLunar).</summary>
        const double AscentToTransfer = 3300;
        /// <summary>Перелёт к Луне без ограничения по времени — для окна старта, с.</summary>
        const double DefaultTransfer = 5 * 86400;
        /// <summary>Сколько вперёд искать момент разгона, с (как TestLunar и LunarAutopilot).</summary>
        const double TransferWindow = 12 * 86400;
        /// <summary>Промах по перицентру у Луны, после которого нужна коррекция на трассе, м. Пара: TestLunar.MidcourseTolerance.</summary>
        const double MidcourseTolerance = 300e3;
        /// <summary>Коррекция — через столько после разгона: ошибка уже видна, а поправка ещё дешёвая.</summary>
        const double MidcourseDelay = 2 * 3600;
        /// <summary>Прицел для «Мечты»: войти в сферу влияния Луны (66 тыс. км) с запасом.</summary>
        const double SoiMiss = 30e6;
        /// <summary>Прицел для попадания и посадки, м: с фокусировкой притяжением Луны 1000 км дают перицентр под рельефом.</summary>
        const double ImpactMiss = 1000e3;
        /// <summary>Прицел «Луны-3», м, и её предельный перелёт: облёт за обратную сторону к Земле (TestLunar farside).</summary>
        const double FarSideMiss = 8000e3, FarSideTransfer = 2.6 * 86400;
        /// <summary>Разворот на торможение перед РДТТ, с: SAS успевает с запасом.</summary>
        const double DeorbitTurn = 120;
        /// <summary>Тормозить РДТТ/газом до такого перицентра, м: спуск в атмосфере за виток, перегрузка в норме (TestOrbitReturn).</summary>
        const double DeorbitPerigee = 40e3;
        /// <summary>Тормозной импульс жидкостной ТДУ «Востока», м/с, и через сколько после взведения (TestVostok).</summary>
        const double DeorbitDv = 95, DeorbitLead = 300;
        /// <summary>Отделение головной части на нисходящей ветви выше этой высоты, м (суборбитальные прыжки).</summary>
        const double SeparateAltitude = 50e3;
        /// <summary>«Линия Кармана»: парашют ниже этой высоты и при скоростном напоре меньше этого, м и Па.</summary>
        const double ChuteAltitude = 6000, ChuteMaxQ = 20000;
        /// <summary>
        /// Программа «Редстоуна» (TestFreedom7): вертикально FreedomVertical с, затем наклон FreedomPitchRate °/с до
        /// FreedomTilt° и отсечка по баллистическому апоцентру FreedomApex. Пара: CrewGLimit — при 187 км пик 11,8 g.
        /// </summary>
        const double FreedomTilt = 10, FreedomApex = 180e3, FreedomPitchRate = 0.5, FreedomVertical = 12;
        /// <summary>ЛМ после расстыковки отходит столько секунд, прежде чем начать сход (TestApollo).</summary>
        const double UndockCoast = 30;
        /// <summary>Пауза на поверхности перед взлётом, с.</summary>
        const double SurfaceStay = 10;
        /// <summary>Орбита взлётной ступени, м. Пара: FlightInput.LmAscentAltitude (H на Луне у ЛМ).</summary>
        const double LmAscentAltitude = 30e3;
        /// <summary>Езда лунохода: ход и руль (как DriveLunokhod), и предел времени, с.</summary>
        const double RoverThrottle = 1, RoverSteer = 0.3, RoverTime = 600;
        /// <summary>
        /// Условный перигей возвращения от Луны, м над поверхностью: коридор входа «Аполлона» — 40–50 км. Выше —
        /// рикошет от атмосферы, ниже — перегрузка экипажа. Пара: MaxHeatFlux капсулы CM (8 МВт/м²).
        /// </summary>
        public const double ReturnPerigee = 45e3;
        /// <summary>Допуск перигея на трассе возврата, м; коррекций не больше MaxReturnFixes.</summary>
        /// Коридор узкий: замер auto — перигей 45,9 км → вход −6,85°, пик 8,0 g; 41,1 км → −7,02°, 9,2 g дольше 10 с,
        /// экипаж погиб. Допуск 5 км пропускал второй случай без коррекции.
        const double ReturnTolerance = 1.5e3;
        const int MaxReturnFixes = 3;
        /// <summary>Поздняя коррекция возврата — за столько секунд до перигея. Пара: больше EntryPrep + 3·TurnMargin,
        /// иначе FixPerigee откажется; на ~3 ч до перигея борт в ~80 тыс. км, 1 км перигея — ~0,1–0,3 м/с.</summary>
        const double LateFixLead = 3 * 3600;
        /// <summary>Меньше — NodeAutopilot закрывает манёвр сразу (порог остатка 0,1 м/с в FlightControl).</summary>
        const double MinFixDv = 0.1;
        /// <summary>Запас на разворот перед коррекцией, с.</summary>
        const double TurnMargin = 120;
        /// <summary>За сколько до перигея сбросить служебный модуль и взвести парашют, с: вход в атмосферу ≈ за 5–10 мин.</summary>
        const double EntryPrep = 1800;
        /// <summary>Ускорение кончается чуть позже расчётного конца витка — трекер засчитывает цель по кадрам.</summary>
        const double HoldMargin = 2;
        /// <summary>Прожиги короче этого — на ×1 (точность отсечки и смотреть есть на что). Пара: Universe.AutoBurnWarpMin.</summary>
        const double ShortBurn = 15;

        public enum ProfileType { Suborbital, Orbital, LunarProbe, Apollo, Station, Winged, Starship }

        public readonly MissionTracker Tracker;
        public readonly ProfileType Profile;
        public string Status { get; private set; } = "";
        /// <summary>Шаг сценария словами — для HUD и подсказки.</summary>
        public string Phase { get; private set; } = "";
        /// <summary>Раньше этого момента ускорение надо прервать (окно старта, конец витка, подготовка ко входу).</summary>
        public double WarpLimit { get; private set; } = double.PositiveInfinity;
        /// <summary>Шагу нужна полная физика: разворот по SAS, езда лунохода.</summary>
        public bool NeedsPhysics { get; private set; }
        /// <summary>Автопилот сам задаёт PilotInput (луноход) — ручным управлением это не считать.</summary>
        public bool OwnsPilotInput { get; private set; }

        readonly Universe universe;
        readonly CelestialBody moon;
        readonly IEnumerator<object> script;
        Func<Vessel, double, double, AutopilotRequest> step;

        sealed class Abort : Exception
        {
            public Abort(string reason) : base(reason) { }
        }

        public MissionAutopilot(Universe u, MissionTracker tracker)
        {
            universe = u;
            Tracker = tracker;
            moon = u.System.Get("moon");
            Profile = Classify(tracker.Def);
            script = Script().GetEnumerator();
        }

        /// <summary>Какой сценарий у миссии — по её целям. Общий с подсказкой (MissionGuide).</summary>
        public static ProfileType Classify(MissionDef def)
        {
            if (MissionCatalog.IsStarship(def)) return ProfileType.Starship; // MissionAutopilot.Starship.cs
            if (def.StationSection >= 0) return ProfileType.Station; // сближение со станцией — MissionAutopilot.Station.cs
            if (def.Objectives.Exists(o => o.Type == ObjectiveType.Runway)) return ProfileType.Winged; // посадка на полосу — MissionAutopilot.Winged.cs
            bool moonOrbit = false, moonGoal = false, earthOrbit = false;
            foreach (var o in def.Objectives)
            {
                if (o.Body == "moon")
                {
                    moonGoal = true;
                    if (o.Type == ObjectiveType.Orbit) moonOrbit = true;
                }
                else if (o.Type == ObjectiveType.Orbit) earthOrbit = true;
            }
            if (moonOrbit) return ProfileType.Apollo;
            if (moonGoal) return ProfileType.LunarProbe;
            return earthOrbit ? ProfileType.Orbital : ProfileType.Suborbital;
        }

        /// <summary>Продвинуть сценарий, раз в кадр. true — автопилот закончил (миссия решена или он сдался).</summary>
        public bool Tick(Universe u)
        {
            var v = u.Active;
            if (v == null || Tracker.Status != MissionStatus.Active)
            {
                Finish(u);
                return true;
            }
            try
            {
                // СБ раскрываются сами после отделения от носителя (Vessel.DeployPanels: вне обтекателя и атмосферы).
                v.DeployPanels(true);
                if (script.MoveNext()) return false;
            }
            catch (Abort e)
            {
                u.Post("Автопилот миссии: " + e.Message);
            }
            Finish(u);
            return true;
        }

        void Finish(Universe u)
        {
            if (OwnsPilotInput && u.Active != null) u.Active.PilotInput = Vector3d.zero;
            OwnsPilotInput = NeedsPhysics = false;
            step = null;
            WarpLimit = double.PositiveInfinity;
        }

        /// <summary>Исполнение в физике, каждый шаг интегратора (до частных автопилотов).</summary>
        public AutopilotRequest Update(Vessel v, double t, double h) => step != null && v.Alive ? step(v, t, h) : AutopilotRequest.None;

        /// <summary>Можно ли сейчас ускорять полную физику до ×MaxPhysicsWarp (все фазы тестов ядра пройдены на ×10).</summary>
        public bool PhysicsWarpOk(Universe u)
        {
            var v = u.Active;
            if (u.Docking != null && u.Docking.Close || u.Lunar != null && u.Lunar.Close) return false;
            if (u.Landing != null && u.Landing.Phase == LandingAutopilot.PhaseType.Terminal) return false;
            if (v.AnyEngineRunning && v.Node != null && FlightControl.BurnTime(v, v.Node.Total) < ShortBurn) return false;
            // Заход и посадка планера: крен и α меняются за секунды, шаг ×10 раскачивает наведение.
            if (Profile == ProfileType.Winged && NeedsPhysics && v.Altitude < WingedNoWarpAltitude) return false;
            return true;
        }

        // ---------------------------------------------------------------- сценарии

        IEnumerable<object> Script()
        {
            IEnumerable<object> s;
            switch (Profile)
            {
                case ProfileType.Suborbital: s = Suborbital(); break;
                case ProfileType.Orbital: s = Orbital(); break;
                case ProfileType.LunarProbe: s = LunarProbe(); break;
                case ProfileType.Station: s = Station(); break;
                case ProfileType.Winged: s = Winged(); break;
                case ProfileType.Starship: s = Starship(); break;
                default: s = Apollo(); break;
            }
            foreach (var x in s) yield return x;
            foreach (var x in Await(() => false, "Ожидание цели миссии")) yield return x;
        }

        Vessel V => universe.Active;
        double T => universe.Time;

        /// <summary>«Линия Кармана», «Фридом-7»: прыжок вверх, отделение на нисходящей ветви, парашют.</summary>
        IEnumerable<object> Suborbital()
        {
            var u = universe;
            if (V.IsLanded && double.IsNaN(V.LaunchTime))
            {
                Phase = "Старт";
                u.Stage(); // зажигание
            }
            if (V.HasCrew()) step = FreedomProgram(T);
            Phase = "Подъём";
            foreach (var x in Await(() => V.VerticalSpeed < 0 && V.Altitude > SeparateAltitude,
                                    () => $"Подъём: {V.Altitude / 1000:F0} км")) yield return x;
            step = null;
            Phase = "Спуск";
            u.Stage(); // отделение головной части
            yield return null;
            // Капсула с автоматикой: парашют взводится сразу, раскрывает FlightPhysics по высоте. Взводим не ниже уставки
            // игрока (окно детали, §4.8) — иначе поднятая уставка молча съезжала бы до 6 км; опущенную FlightPhysics выдержит сам.
            double chuteArm = Math.Max(ChuteAltitude, V.HighestChuteAltitude());
            if (!V.HasCrew())
                foreach (var x in Await(() => V.Altitude < chuteArm && V.DynamicPressure < ChuteMaxQ,
                                        () => $"Спуск: {V.Altitude / 1000:F0} км, парашют на {chuteArm / 1000:0.#} км")) yield return x;
            u.Stage();
            foreach (var x in Await(() => false, () => $"Спуск на парашюте: {V.Altitude / 1000:F1} км")) yield return x;
        }

        /// <summary>Программа тангажа «Редстоуна» (TestFreedom7): иначе вертикальный подъём даёт 14 g на спуске.</summary>
        Func<Vessel, double, double, AutopilotRequest> FreedomProgram(double t0) => (v, t, h) =>
        {
            if (!v.AnyEngineRunning) return AutopilotRequest.None;
            FlightControl.LocalFrame(v, t, out var up, out _, out var east);
            double pitch = (90 - Math.Min(FreedomTilt, Math.Max(0, t - t0 - FreedomVertical) * FreedomPitchRate)) * Constants.Deg2Rad;
            // FlightControl.Update обнуляет момент каждый шаг — программа идёт через удержание SAS.
            var dir = AscentAutopilot.LimitAoA(v, t, up * Math.Sin(pitch) + east * Math.Cos(pitch));
            v.Sas = SasMode.Stability;
            v.SasHold = QuaternionD.FromToRotation(v.NoseP.SwapYZ, dir.SwapYZ) * v.Attitude;
            v.SasHoldValid = true;
            var o = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, t);
            if (o.ApoapsisRadius - v.Body.Radius > FreedomApex) v.Throttle = 0;
            return AutopilotRequest.None;
        };

        /// <summary>Спутник, «Восток», «Меркурий», «Джемини»: орбита, виток, торможение и спуск капсулы.</summary>
        IEnumerable<object> Orbital()
        {
            var earth = V.Body;
            bool deorbited = !V.IsLanded && Perigee(V) < earth.AtmosphereTop && Tracker.Done[0];
            if (!deorbited)
            {
                foreach (var x in Ascend(ParkingAltitude, null)) yield return x;
                foreach (var x in HoldOrbit(IndexOf(ObjectiveType.Orbit))) yield return x;
                if (IndexOf(ObjectiveType.Return) < 0) yield break;
                foreach (var x in Deorbit()) yield return x;
            }
            foreach (var x in Descend()) yield return x;
        }

        /// <summary>Сход с орбиты: РДТТ — газом по SAS-ретро до перигея DeorbitPerigee, жидкостная ТДУ — узлом.</summary>
        IEnumerable<object> Deorbit()
        {
            var u = universe;
            Phase = "Сход с орбиты";
            StageUntil(StageActionType.Ignite);
            if (FlightControl.NextIsSolidKick(V))
            {
                V.Sas = SasMode.Retrograde;
                NeedsPhysics = true;
                double end = T + DeorbitTurn;
                foreach (var x in Await(() => T >= end, () => $"Разворот на торможение: {end - T:F0} с")) yield return x;
                V.Throttle = 1;
                u.Stage(); // тормозные двигатели
                var v = V;
                foreach (var x in Await(() => !v.Alive || Perigee(v) < v.Body.Radius + DeorbitPerigee,
                                        () => $"Торможение: перигей {(Perigee(v) - v.Body.Radius) / 1000:F0} км")) yield return x;
                v.Throttle = 0;
                NeedsPhysics = false;
            }
            else
            {
                u.Stage(); // взведение ТДУ
                u.SetNode(T + DeorbitLead, -DeorbitDv, 0, 0);
                foreach (var x in Burn("Тормозной импульс")) yield return x;
            }
        }

        /// <summary>Сброс всего лишнего до парашюта, SAS выкл., парашют взводится — раскрывает автоматика по высоте.</summary>
        IEnumerable<object> Descend()
        {
            PrepareEntry();
            Phase = "Спуск";
            foreach (var x in Await(() => false, () => V.Situation == Situation.Flying
                ? $"Спуск: {V.Altitude / 1000:F1} км, {V.SurfaceSpeed:F0} м/с"
                : "Посадка")) yield return x;
        }

        void PrepareEntry()
        {
            StageUntil(StageActionType.DeployParachute);
            V.Sas = SasMode.Off;
            if (V.NextStageLabel != null) universe.Stage();
        }

        /// <summary>Лунные станции: окно, опорная орбита, разгон, коррекция, посадка или попадание, луноход.</summary>
        IEnumerable<object> LunarProbe()
        {
            var u = universe;
            var def = Tracker.Def;
            bool land = IndexOf(ObjectiveType.Landing) >= 0, impact = IndexOf(ObjectiveType.Impact) >= 0;
            double miss = land || impact ? ImpactMiss : IndexOf(ObjectiveType.Flyby) >= 0 ? FarSideMiss : SoiMiss;
            double maxTransfer = IndexOf(ObjectiveType.Flyby) >= 0 ? FarSideTransfer : double.PositiveInfinity;
            bool viaOrbit = land && def.LunarOrbit > 0;

            if (!viaOrbit && V.Body != moon && !Encounters(V))
            {
                foreach (var x in WaitWindow(maxTransfer)) yield return x;
                foreach (var x in Ascend(ParkingAltitude, null)) yield return x;
                Phase = "Разгон к Луне";
                var v = V;
                var plan = TransferPlanner.PlanIntercept(v.Body, v.Position, v.Velocity, T, moon, miss, TransferWindow, maxTransfer);
                if (plan == null) throw new Abort("перелёт к Луне не найден");
                u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
                u.Post($"К Луне: разгон через {Clock(plan.Time - T)}, Δv {plan.DeltaV:F0} м/с, перелёт {Clock(plan.TransferTime)}");
                foreach (var x in Burn("Разгон к Луне")) yield return x;
            }
            if (!viaOrbit && V.Body != moon)
            {
                // Коррекция на трассе (как у настоящих станций): промах разгона добирается малым импульсом.
                var lunar = PatchedConics.Predict(V.Body, V.Position, V.Velocity, T, null, 4).Find(p => p.Body == moon);
                if (lunar == null || Math.Abs(lunar.Orbit.PeriapsisRadius - miss) > MidcourseTolerance)
                {
                    var v = V;
                    var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, T);
                    double t0 = T + MidcourseDelay;
                    if (orbit.IsElliptic)
                    {
                        orbit.GetState(t0, out var r0, out var v0);
                        var fix = TransferPlanner.PlanIntercept(v.Body, r0, v0, t0, moon, miss, 86400, maxTransfer);
                        if (fix != null)
                        {
                            Phase = "Коррекция";
                            u.SetNode(fix.Time, fix.Prograde, fix.Normal, fix.Radial);
                            u.Post($"Коррекция через {Clock(fix.Time - T)}: {fix.DeltaV:F1} м/с");
                            foreach (var x in Burn("Коррекция на трассе")) yield return x;
                        }
                    }
                }
            }
            if (viaOrbit && !(V.IsLanded && V.Body == moon)) foreach (var x in LunarOrbitFirst(def)) yield return x;
            if (land && !V.IsLanded) u.Landing = new LandingAutopilot(moon);
            Phase = land ? "Перелёт и посадка" : "Перелёт";
            foreach (var x in Await(() => land && V.IsLanded && V.Body == moon || land && u.Landing == null && !V.IsLanded, () =>
                     u.Landing != null && u.Landing.Phase != LandingAutopilot.PhaseType.Coast ? u.Landing.Status
                     : V.Body == moon ? $"У Луны: {V.Altitude / 1000:F0} км" : CoastText())) yield return x;
            if (land && !V.IsLanded) throw new Abort("посадка не удалась");
            if (IndexOf(ObjectiveType.Drive) < 0) yield break;

            // «Луноход-1»: съезд по трапам (сброс посадочной ступени) и езда своим ходом (GDD §6.12).
            Phase = "Луноход";
            // Трапы откидываются только на грунте, и пока они не легли, пробел не сбросит ступень (Vessel.StageBlock).
            if (!V.RampsDown)
            {
                NeedsPhysics = true;
                u.ToggleDeploy();
                var lander = V;
                foreach (var x in Await(() => lander.RampsDown || !lander.Alive, "Трапы: раскладка")) yield return x;
                NeedsPhysics = false;
            }
            for (int n = 0; n < 8 && !V.IsRover && V.HasNextStage; n++) u.Stage();
            if (!V.IsRover) throw new Abort("луноход не съехал");
            OwnsPilotInput = NeedsPhysics = true;
            double until = T + RoverTime;
            var rover = V;
            foreach (var x in Await(() => T >= until || !rover.Alive, () =>
                     {
                         rover.PilotInput = new Vector3d(RoverThrottle, RoverSteer, 0);
                         return $"Луноход: проехал {rover.DriveDistance:F0} м";
                     })) yield return x;
            rover.PilotInput = Vector3d.zero;
            OwnsPilotInput = NeedsPhysics = false;
        }

        /// <summary>
        /// «Луна-17» (как «Луна-16»): не прямой спуск «Луны-9» и «Сервейера», а сначала круговая окололунная орбита
        /// def.LunarOrbit, затем перицентр def.LunarPerilune торможением в апоцентре — и посадка из него.
        /// Пара: LandingAutopilot.LowEnough (DeorbitPeriapsis 15 км + 5 км) — 19 км уже низко, второго схода нет.
        /// </summary>
        IEnumerable<object> LunarOrbitFirst(MissionDef def)
        {
            var u = universe;
            if (V.Body != moon || !OnOrbit(V))
            {
                if (V.Body != moon && !Encounters(V))
                {
                    if (V.IsLanded) foreach (var x in WaitWindow(double.PositiveInfinity)) yield return x;
                    foreach (var x in Ascend(ParkingAltitude, null)) yield return x;
                }
                u.Lunar = new LunarAutopilot(u, moon, def.LunarOrbit);
                Phase = "Полёт к Луне";
                foreach (var x in Await(() => u.Lunar == null, () => u.Lunar?.Status ?? "")) yield return x;
            }
            if (V.Body != moon || !OnOrbit(V)) throw new Abort("окололунная орбита не получена");

            var v = V;
            var o = KeplerOrbit.FromState(v.Position, v.Velocity, moon.Mu, T);
            double rp = moon.Radius + def.LunarPerilune;
            if (o.PeriapsisRadius < rp + 2000) yield break;
            double ra = o.ApoapsisRadius, t = T + o.TimeToApoapsis(T);
            if (t - T < TurnMargin) t += o.Period;
            double va = Math.Sqrt(moon.Mu * (2 / ra - 1 / o.A)), vn = Math.Sqrt(moon.Mu * (2 / ra - 2 / (ra + rp)));
            Phase = "Снижение орбиты";
            u.SetNode(t, vn - va, 0, 0);
            u.Post($"Снижение перицентра до {def.LunarPerilune / 1000:F0} км: через {Clock(t - T)}, Δv {va - vn:F0} м/с");
            foreach (var x in Burn("Снижение перицентра")) yield return x;
        }

        /// <summary>«Аполлон»: окно, опорная орбита, LunarAutopilot до окололунной; дальше посадка ЛМ или виток и возврат.</summary>
        IEnumerable<object> Apollo()
        {
            var u = universe;
            int orbitIdx = IndexOf(ObjectiveType.Orbit);
            bool returning = V.Body != moon && !V.IsLanded && orbitIdx >= 0 && Tracker.Done[orbitIdx];
            if (!returning)
            {
                if (V.Body != moon)
                {
                    if (V.IsLanded) foreach (var x in WaitWindow(double.PositiveInfinity)) yield return x;
                    foreach (var x in Ascend(ParkingAltitude, null)) yield return x;
                }
                if (V.Body != moon || !OnOrbit(V))
                {
                    u.Lunar = new LunarAutopilot(u, moon);
                    Phase = "Полёт к Луне";
                    foreach (var x in Await(() => u.Lunar == null, () => u.Lunar?.Status ?? "")) yield return x;
                }
                if (V.Body != moon || !OnOrbit(V)) throw new Abort("окололунная орбита не получена");

                if (IndexOf(ObjectiveType.Landing) >= 0)
                {
                    foreach (var x in LunarLanding()) yield return x;
                }
                else foreach (var x in HoldOrbit(orbitIdx)) yield return x;
                if (IndexOf(ObjectiveType.Return) < 0) yield break;
                foreach (var x in TransEarth()) yield return x;
            }
            foreach (var x in EarthReturn()) yield return x;
            foreach (var x in Descend()) yield return x;
        }

        /// <summary>«Аполлон-11»: расстыковка с переходом в ЛМ, посадка, взлёт в плоскость КСМ и стыковка (TestApollo).</summary>
        IEnumerable<object> LunarLanding()
        {
            var u = universe;
            var csm = V;
            Phase = "Расстыковка";
            while (u.Active == csm && csm.NextStageLabel != null) u.Stage();
            if (u.Active == csm) throw new Abort("ЛМ не отделился");
            double end = T + UndockCoast;
            // WaitUntil, а не Await: без события впереди (круговая орбита, грунт) автоускорение уходило на ×1e7
            // и проскакивало ~11,6 сут за кадр — замер auto_apollo11: отстыковка 4,87 сут, сход 16,45 сут.
            foreach (var x in WaitUntil(end, () => $"Отход от КСМ: {end - T:F0} с")) yield return x;

            Phase = "Посадка ЛМ";
            u.Landing = new LandingAutopilot(moon);
            foreach (var x in Await(() => V.IsLanded || u.Landing == null, () => u.Landing?.Status ?? "")) yield return x;
            if (!V.IsLanded) throw new Abort("посадка не удалась");
            end = T + SurfaceStay;
            Phase = "На Луне";
            foreach (var x in WaitUntil(end, () => $"На поверхности, взлёт через {end - T:F0} с")) yield return x;

            u.Stage(); // отделение взлётной ступени с зажиганием
            foreach (var x in Ascend(LmAscentAltitude, csm)) yield return x;
            Phase = "Стыковка";
            var lm = V;
            u.Docking = new DockingAutopilot(u, csm);
            foreach (var x in Await(() => u.Docking == null, () => u.Docking?.Status ?? "")) yield return x;
            // Связку забирает КСМ (ЛМ в ней перевёрнут), активным становится он — пропал один из двух бортов.
            if (u.Vessels.Contains(csm) && u.Vessels.Contains(lm)) throw new Abort("стыковка с КСМ не удалась");

            // Экипаж переходит в «Колумбию», взлётная ступень остаётся на орбите, SPS взводится на разгон к Земле.
            Phase = "Переход в КСМ";
            StageUntil(StageActionType.Separate);
            for (int i = 0; i < V.Attached.Length; i++)
                if (V.Attached[i] && V.Flipped[i]) throw new Abort("КСМ не отделился от взлётной ступени");
        }

        /// <summary>Разгон к Земле с окололунной орбиты (TEI): узел по TransferPlanner.PlanReturn.</summary>
        IEnumerable<object> TransEarth()
        {
            var u = universe;
            var v = V;
            Phase = "Разгон к Земле";
            var plan = TransferPlanner.PlanReturn(moon, v.Position, v.Velocity, T, moon.Parent.Radius + ReturnPerigee);
            if (plan == null) throw new Abort("траектория возврата не найдена");
            u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
            u.Post($"К Земле: разгон через {Clock(plan.Time - T)}, Δv {plan.DeltaV:F0} м/с, перелёт {Clock(plan.TransferTime)}");
            foreach (var x in Burn("Разгон к Земле")) yield return x;
        }

        /// <summary>Трасса возврата: коррекции перигея в коридор входа, сброс СМ и парашют перед входом.</summary>
        IEnumerable<object> EarthReturn()
        {
            var u = universe;
            var earth = moon.Parent;
            Phase = "Возврат";
            foreach (var x in Await(() => V.Body == earth, CoastText)) yield return x;
            double target = earth.Radius + ReturnPerigee;
            foreach (var x in FixPerigee(earth, target)) yield return x;
            {
                // Вторая коррекция ближе к Земле, как MCC-7 «Аполлонов» (§9): у границы сферы Луны 2 км перигея
                // стоят сотые доли м/с — меньше порога NodeAutopilot (0,1 м/с), манёвр закрывается не начавшись,
                // а к входу перигей уплывает. Замер auto_apollo11: 43 км на выходе из сферы → 41,1 км на входе, 9,2 g.
                var v = V;
                var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, earth.Mu, T);
                double late = T + orbit.TimeToPeriapsis(T) - LateFixLead;
                if (late > T + TurnMargin)
                {
                    Phase = "Возврат";
                    foreach (var x in WaitUntil(late, CoastText)) yield return x;
                    foreach (var x in FixPerigee(earth, target)) yield return x;
                }
            }
            {
                var v = V;
                var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, earth.Mu, T);
                double prep = T + orbit.TimeToPeriapsis(T) - EntryPrep;
                Phase = "Возврат";
                foreach (var x in WaitUntil(prep, () => $"До входа в атмосферу {Clock(prep + EntryPrep - T)}, перигей {(Perigee(V) - earth.Radius) / 1000:F0} км")) yield return x;
            }
        }

        /// <summary>Коррекции перигея возврата в коридор входа, не больше MaxReturnFixes.</summary>
        IEnumerable<object> FixPerigee(CelestialBody earth, double target)
        {
            var u = universe;
            for (int k = 0; k < MaxReturnFixes; k++)
            {
                var v = V;
                if (Math.Abs(Perigee(v) - target) <= ReturnTolerance) break;
                var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, earth.Mu, T);
                if (orbit.TimeToPeriapsis(T) < EntryPrep + 3 * TurnMargin) break;
                var fix = TransferPlanner.PlanPerigee(earth, v.Position, v.Velocity, T, target, T + TurnMargin);
                // Импульс меньше порога NodeAutopilot всё равно не исполнится — ждать поздней коррекции.
                if (fix == null || fix.DeltaV < MinFixDv) break;
                Phase = "Коррекция возврата";
                u.SetNode(fix.Time, fix.Prograde, fix.Normal, fix.Radial);
                u.Post($"Коррекция перигея: {(Perigee(v) - earth.Radius) / 1000:F0} → {ReturnPerigee / 1000:F0} км, {fix.DeltaV:F1} м/с");
                foreach (var x in Burn("Коррекция возврата")) yield return x;
            }
        }

        // ---------------------------------------------------------------- шаги

        IEnumerable<object> Await(Func<bool> done, string status) => Await(done, () => status);

        IEnumerable<object> Await(Func<bool> done, Func<string> status)
        {
            while (true)
            {
                if (!V.Alive) throw new Abort(V.DestroyReason ?? "аппарат потерян");
                if (done()) yield break;
                Status = status();
                yield return null;
            }
        }

        /// <summary>Ждать момента time: рельсы остановятся на нём (WarpLimit), последние секунды — в физике.</summary>
        IEnumerable<object> WaitUntil(double time, Func<string> status)
        {
            WarpLimit = time;
            foreach (var x in Await(() => T >= time - 0.5, status)) yield return x;
            WarpLimit = double.PositiveInfinity;
        }

        /// <summary>Окно старта к Луне: плоскость опорной орбиты проходит через Луну к прибытию (GDD §6.2).</summary>
        IEnumerable<object> WaitWindow(double maxTransfer)
        {
            if (!V.IsLanded || V.Site == null) yield break;
            double flight = AscentToTransfer + (double.IsInfinity(maxTransfer) ? DefaultTransfer : maxTransfer);
            double tL = LaunchWindow.NextPlaneWindow(V.Body, V.Site, 90, moon, T + 60, flight);
            if (double.IsNaN(tL)) yield break;
            universe.Post($"Окно старта к Луне через {Clock(tL - T)}");
            Phase = "Ожидание окна старта";
            foreach (var x in WaitUntil(tL, () => $"Окно старта через {Clock(tL - T)}")) yield return x;
        }

        IEnumerable<object> Ascend(double altitude, Vessel planeOf)
        {
            var u = universe;
            if (!V.IsLanded && OnOrbit(V)) yield break;
            Phase = "Выведение";
            u.Ascent = new AscentAutopilot { TargetAltitude = altitude };
            if (planeOf != null) u.Ascent.AimAtPlane(V, planeOf, T);
            foreach (var x in Await(() => u.Ascent == null, () => "Выведение: " + (u.Ascent?.Status ?? ""))) yield return x;
            if (V.IsLanded || !OnOrbit(V)) throw new Abort("орбита не получена");
        }

        /// <summary>Виток на орбите: ускорение до расчётного конца удержания цели трекером.</summary>
        IEnumerable<object> HoldOrbit(int i)
        {
            if (i < 0 || Tracker.Done[i]) yield break;
            var o = Tracker.Def.Objectives[i];
            var v = V;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, T);
            double need = o.HoldSeconds < 0 ? orbit.Period : o.HoldSeconds;
            double end = T + need + HoldMargin;
            Phase = o.HoldSeconds < 0 ? "Виток на орбите" : "На орбите";
            WarpLimit = end;
            foreach (var x in Await(() => Tracker.Done[i], () =>
                     {
                         // Трекер мог начать отсчёт позже (перицентр ниже цели) — тогда даём ему дойти в физике.
                         if (T >= WarpLimit) WarpLimit = T + 60;
                         return $"{Phase}: осталось {Clock(Math.Max(0, end - T))}";
                     })) yield return x;
            WarpLimit = double.PositiveInfinity;
        }

        IEnumerable<object> Burn(string what)
        {
            var u = universe;
            u.NodePilot = new NodeAutopilot();
            foreach (var x in Await(() => u.NodePilot == null, () =>
                     {
                         var n = V.Node;
                         if (n == null) return what;
                         double left = n.Time - T;
                         return left > 0 ? $"{what}: через {Clock(left)}, {n.Total:F1} м/с" : $"{what}: остаток {n.Total:F1} м/с";
                     })) yield return x;
        }

        void StageUntil(StageActionType type)
        {
            var u = universe;
            for (int guard = 0; guard < 32; guard++)
            {
                var v = V;
                if (v.NextStageLabel == null || v.NextStage >= v.Design.Sequence.Count || v.Design.Sequence[v.NextStage].Type == type) return;
                u.Stage();
            }
        }

        int IndexOf(ObjectiveType type) => Tracker.Def.Objectives.FindIndex(o => o.Type == type);

        string CoastText()
        {
            var v = V;
            var ps = universe.PredictActive();
            var next = ps.Count > 0 ? ps[0] : null;
            if (next != null && next.EndType == TransitionType.Encounter && next.NextBody != null)
                return $"Перелёт: до {next.NextBody.Name} {Clock(Math.Max(0, next.EndTime - T))}";
            if (next != null && next.EndType == TransitionType.Escape && next.NextBody != null)
                return $"Перелёт: выход к {next.NextBody.Name} через {Clock(Math.Max(0, next.EndTime - T))}";
            return $"Перелёт: {v.Altitude / 1000:F0} км над {v.Body.Name}";
        }

        bool Encounters(Vessel v) =>
            !v.IsLanded && PatchedConics.Predict(v.Body, v.Position, v.Velocity, T, null, 3).Exists(p => p.Body == moon);

        double Perigee(Vessel v) => KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, T).PeriapsisRadius;

        /// <summary>Замкнутая орбита выше атмосферы (или рельефа у безатмосферного тела).</summary>
        bool OnOrbit(Vessel v)
        {
            if (v.IsLanded) return false;
            var o = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, T);
            double floor = v.Body.Radius + (v.Body.HasAtmosphere ? v.Body.AtmosphereTop : 5000);
            return o.IsElliptic && o.ApoapsisRadius < v.Body.SoiRadius && o.PeriapsisRadius > floor;
        }

        public static string Clock(double s)
        {
            if (s < 0) s = 0;
            if (s < 3600) return $"{(int)(s / 60):00}:{(int)(s % 60):00}";
            if (s < 86400) return $"{(int)(s / 3600)} ч {(int)(s % 3600 / 60):00} мин";
            return $"{s / 86400:F1} сут";
        }
    }
}
