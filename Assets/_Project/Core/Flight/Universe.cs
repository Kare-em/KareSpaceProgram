using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Вселенная одной партии (GDD §3): время, корабли, ускорение. Активный корабль летит по полной
    /// физике или на рельсах (кеплерова орбита), остальные — на рельсах или удаляются: считать
    /// физику обломка за тысячи километров незачем, а на ускорении — невозможно.
    /// </summary>
    public sealed partial class Universe
    {
        public static readonly double[] Warps = { 1, 5, 10, 50, 100, 1e3, 1e4, 1e5, 1e6, 1e7 };
        /// <summary>Потолок физического ускорения (атмосфера, работа двигателя, автопилот). Шаг интегратора
        /// остаётся MaxStep — ×10 только множит подшаги кадра (≈8 при 60 к/с), точность не теряется.
        /// Пара: индекс 2 в Warps = ×10, до него WarpUp пускает при закрытых рельсах.</summary>
        public const double MaxPhysicsWarp = 10;
        /// <summary>
        /// Сколько неактивных бортов ниже «пола» рельсов (атмосфера, низкий пролёт над безатмосферным телом) считается полной
        /// физикой — ближайшие к активному, на любом расстоянии (§5: обломок падает, сгорает или ложится на грунт сам, а не
        /// исчезает за 25 км). Остальные теряются, как раньше. Пара: шаг FlightPhysics.MaxStep × число бортов — цена кадра.
        /// </summary>
        public const int MaxPassivePhysics = 32;
        /// <summary>Столкновения бортов (§5): выключатель для отладки.</summary>
        public bool Collisions = true;
        /// <summary>
        /// Скорость сближения, м/с, выше которой столкновение разрушает оба борта (тонкостенные баки), и коэффициент
        /// восстановления ниже неё. Пара: Vessel.StagePush 1,5 м/с и RadialPush 2 м/с — штатное разделение не крушит.
        /// </summary>
        public const double CrashSpeed = 12, Restitution = 0.2;
        /// <summary>Секунды после разделения, пока новые части не сталкиваются ни с кем; с родителем их дальше разводит Fresh.</summary>
        public const double CollisionGrace = 1.5;
        /// <summary>За сколько секунд до запуска манёвра ускорение сбрасывается само.</summary>
        public const double NodeWarpMargin = 30;
        /// <summary>
        /// Автоускорение (§6.11): сколько РЕАЛЬНЫХ секунд должно оставаться до ближайшего события (манёвр, смена сферы,
        /// апоцентр взлёта) на выбранной ступени. Ступень выбирается наибольшей, у которой запас не меньше, — к событию
        /// ускорение само спускается по лестнице Warps, не перескакивая его за кадр (кадр ≤ 0,1 с, т. е. ≤ 1/20 запаса).
        /// Само событие рельсы не проскочат (AdvanceRails останавливается на нём), запас — только на плавность: при 4 с
        /// перелёт к Луне шёл 16,6 мин реального времени, половина — спуск по лестнице.
        /// </summary>
        public const double AutoWarpLead = 2;
        /// <summary>Остаток прожига в вакууме, с, выше которого автопилот ускоряет физику до MaxPhysicsWarp: разгон к Луне
        /// идёт ≈ 5,5 мин, на ×10 — полминуты. Шаг интегратора тот же (см. MaxPhysicsWarp), точность импульса не теряется:
        /// apollo8 при 60 и при 15 вышел на одну и ту же орбиту 109,9 × 110,3 км, а кадров стало 5512 → 4693.</summary>
        public const double AutoBurnWarpMin = 15;

        public readonly SolarSystem System;
        public double Time { get; private set; }
        public readonly List<Vessel> Vessels = new List<Vessel>();
        public Vessel Active { get; private set; }
        public int WarpIndex { get; private set; }
        /// <summary>Фактический множитель последнего кадра и был ли он на рельсах — для HUD.</summary>
        public double EffectiveWarp { get; private set; } = 1;
        public bool RailsActive { get; private set; }

        public AscentAutopilot Ascent;
        public NodeAutopilot NodePilot;
        public LandingAutopilot Landing;
        public DockingAutopilot Docking;
        public LunarAutopilot Lunar;
        /// <summary>Автопилот «миссия целиком» (Y): сценарий поверх частных автопилотов, сам ведёт ускорение.</summary>
        public MissionAutopilot Mission;
        /// <summary>
        /// Фоновые автопилоты возвращаемых ступеней (§6.9): работают для любого борта, не только активного. Пока хоть
        /// один ведёт ступень, она под полной физикой, а рельсы закрыты (RailsBlocker). Завершённые остаются — итог посадки.
        /// </summary>
        public readonly List<BoosterLandingAutopilot> Recoveries = new List<BoosterLandingAutopilot>();

        /// <summary>Пилот возврата этого борта, если он ещё ведёт его.</summary>
        public BoosterLandingAutopilot RecoveryOf(Vessel v)
        {
            foreach (var p in Recoveries) if (p.Vessel == v && p.Running) return p;
            return null;
        }

        bool AnyRecovery
        {
            get
            {
                foreach (var p in Recoveries) if (p.Running) return true;
                return false;
            }
        }

        /// <summary>Автопилот сам ведёт ускорение времени (настройка игрока; в тестах ядра по умолчанию выключено —
        /// там ускорение задаёт сценарий). Ручные «.» и «,» ставят его на паузу до конца работы автопилотов, «/» на ×1 — снимает паузу.</summary>
        public bool AutoWarp;
        public bool AutoWarpPaused;
        bool autoDriving;
        /// <summary>Ступень выбрал игрок клавишами (WarpUp/WarpDown), а не сценарий/сброс через SetWarp. Только такая
        /// ×5…×10 у манёвра остаётся физикой, а не сбрасывается в ×1: тесты ядра задают ускорение SetWarp и ждут сброса.</summary>
        bool playerWarp;
        /// <summary>Ускорением правит игрок поверх работающего автопилота (автоускорение на паузе) — для HUD.</summary>
        public bool AutoWarpManual => AutoWarp && AutoWarpPaused && AutopilotActive;
        /// <summary>Автоускорение выбрало физическую ступень (≤ MaxPhysicsWarp) при свободных рельсах: окно перед прожигом.</summary>
        bool autoPhysics;
        /// <summary>«Миссия целиком» на паузе, пока игрок ведёт другой борт (MissionVessel): иначе сценарий взялся бы за чужой.</summary>
        public bool AutopilotActive => Ascent != null || NodePilot != null || Landing != null || Docking != null || Lunar != null || Mission != null && MissionVessel == null;
        /// <summary>Ускорением сейчас управляет автопилот — для HUD.</summary>
        public bool AutoWarpDriving => autoDriving;

        public readonly List<(double time, string text)> Log = new List<(double, string)>();
        public event Action<string> Message;

        Transition nextEvent = Transition.None;
        bool eventValid;

        public Universe(SolarSystem system, double time)
        {
            System = system;
            Time = time;
            System.Update(time);
        }

        public void Post(string text)
        {
            Log.Add((Time, text));
            if (Log.Count > 200) Log.RemoveAt(0);
            Message?.Invoke(text);
        }

        // ---------------------------------------------------------------- корабли

        public Vessel Launch(VesselDesign design, string siteId)
        {
            var site = SolarSystem.GetSite(siteId) ?? throw new ArgumentException($"Нет космодрома {siteId}");
            var body = System.Get(site.BodyId);
            var v = new Vessel(design);
            FlightPhysics.PlaceOnSurface(v, body, site.Latitude, site.Longitude, Time, site.PadHeight);
            v.Site = site;
            FlightPhysics.UpdateTelemetry(v, Time);
            Add(v);
            SetActive(v);
            Post($"{design.Name} на старте: {site.Name}");
            return v;
        }

        public void Add(Vessel v)
        {
            Vessels.Add(v);
            v.Event += OnVesselEvent;
        }

        public void SetActive(Vessel v)
        {
            Active = v;
            Ascent = null;
            NodePilot = null;
            Landing = null;
            Docking = null;
            Lunar = null;
            eventValid = false;
        }

        /// <summary>
        /// Борт миссии, пока игрок управляет другим (переключение §6.13): по нему считаются задачи, на него ждёт
        /// автопилот «миссия целиком». null — борт миссии и есть активный (так всегда без ручного переключения:
        /// переход экипажа в ЛМ через ControlTransfer миссию не покидает).
        /// </summary>
        public Vessel MissionVessel { get; private set; }

        /// <summary>
        /// Переключиться на другой борт (Shift+[ / Shift+], как [ ] в KSP). Нельзя с работающим двигателем: брошенный
        /// борт остался бы с газом без пилота. Ускорение — в ×1: новый борт может оказаться в физике (в воздухе, у грунта).
        /// </summary>
        public bool SwitchTo(Vessel v)
        {
            if (v == null || v == Active || !v.Alive || !Vessels.Contains(v)) return false;
            if (Active != null && Active.Alive && Active.AnyEngineRunning)
            {
                Post("Сначала заглушите двигатель");
                return false;
            }
            if (Active != null && Active.Alive) FlightControl.Cutoff(Active);
            if (MissionVessel == null && Active != null && Active.Alive) MissionVessel = Active;
            if (v == MissionVessel) MissionVessel = null;
            SetWarp(0);
            SetActive(v);
            Post($"Управление: {v.Name}");
            return true;
        }

        /// <summary>Следующий (dir = +1) или предыдущий живой борт по порядку списка — порядок не прыгает от расстояний.</summary>
        public Vessel NextVessel(int dir)
        {
            int n = Vessels.Count, i0 = Vessels.IndexOf(Active);
            for (int k = 1; k <= n; k++)
            {
                var c = Vessels[((i0 + dir * k) % n + n) % n];
                if (c != Active && c.Alive) return c;
            }
            return null;
        }

        void OnVesselEvent(Vessel v, string msg)
        {
            if (v == Active) Post(msg);
            else if (!v.IsDebris && !v.Alive) Post($"{v.Name}: {msg}");
        }

        /// <summary>G (§6.12): опоры и трапы активного корабля. Привод крутится в физике — с рельсов снимаем.</summary>
        public void ToggleDeploy()
        {
            if (Active == null || !Active.Alive) return;
            if (Active.OnRails) LeaveRails(Active);
            var msg = Active.ToggleDeploy();
            if (msg != null) Post(msg);
        }

        /// <summary>Расстыковка вручную (V на состыкованном борту): отошедший борт сразу цель — можно причалить снова.</summary>
        public void Undock()
        {
            if (Active == null || !Active.Alive) return;
            if (Active.OnRails) LeaveRails(Active);
            var d = Active.Undock();
            if (d == null) { Post("Нет состыкованного борта"); return; }
            Docking = null;
            AddSeparated(Active, new List<Vessel> { d });
            Active.Target = d;
        }

        /// <summary>
        /// Отделившиеся борта — в мир. Каждая пара из родителя и частей «свежая»: не сталкивается и не стыкуется,
        /// пока корпуса не разойдутся на SeparationClear (Fresh). Раньше был общий таймер CollisionGrace на весь
        /// активный борт: на напоре он то гас, пока части ещё вплотную, то глушил удары и с чужими бортами.
        /// </summary>
        void AddSeparated(Vessel parent, List<Vessel> parts)
        {
            if (parts.Count == 0) return;
            fresh.RemoveAll(f => Time > f.until || !f.a.Alive || !f.b.Alive);
            foreach (var d in parts)
            {
                d.NoCollideUntil = Time + CollisionGrace;
                d.Event += OnVesselEvent;
                Vessels.Add(d);
                var pilot = BoosterLandingAutopilot.TryStart(d);
                if (pilot != null) Recoveries.Add(pilot);
            }
            for (int i = -1; i < parts.Count; i++)
                for (int j = i + 1; j < parts.Count; j++)
                    fresh.Add((i < 0 ? parent : parts[i], parts[j], Time + FreshMaxTime));
            eventValid = false;
        }

        /// <summary>
        /// Зазор между корпусами, м, после которого пара разделения снова «обычная» (удары, стыковка). Пара:
        /// DockCaptureRange 1 м — меньше нельзя, иначе только что отстыкованный борт тут же причалит обратно.
        /// </summary>
        public const double SeparationClear = 1.5;
        /// <summary>Сколько пара может оставаться «свежей», с: части, идущие вплотную дольше, — уже не разделение.</summary>
        const double FreshMaxTime = 60;
        readonly List<(Vessel a, Vessel b, double until)> fresh = new List<(Vessel, Vessel, double)>();

        /// <summary>Пара a–b ещё не разошлась после разделения. Разошлась (или истёк срок) — вычёркивается навсегда.</summary>
        bool Fresh(Vessel a, Vessel b)
        {
            for (int k = fresh.Count - 1; k >= 0; k--)
            {
                var f = fresh[k];
                if (!(f.a == a && f.b == b || f.a == b && f.b == a)) continue;
                bool gone = !a.Alive || !b.Alive || a.Body != b.Body || Time > f.until;
                if (!gone)
                {
                    Hull(a, out var a0, out var a1, out double ra, out _);
                    Hull(b, out var b0, out var b1, out double rb, out _);
                    ClosestPoints(a0, a1, b0, b1, out var ca, out var cb);
                    gone = (cb - ca).magnitude - ra - rb > SeparationClear;
                }
                if (gone) { fresh.RemoveAt(k); return false; }
                return true;
            }
            return false;
        }

        /// <summary>Следующая ступень активного корабля (пробел).</summary>
        public void Stage()
        {
            if (Active == null || !Active.Alive) return;
            var block = Active.StageBlock;
            if (block != null) { Post(block); return; }
            if (Active.OnRails) LeaveRails(Active);
            AddSeparated(Active, Active.Stage());
            // Расстыковка с переходом экипажа (§6.6): управление — отошедшему борту (ЛМ уходит на посадку).
            if (Active.ControlTransfer != null)
            {
                var c = Active.ControlTransfer;
                Active.ControlTransfer = null;
                SetActive(c);
                Post($"Управление: {c.Name}");
            }
        }

        // ---------------------------------------------------------------- ускорение времени

        /// <summary>Выше физического потолка — только если рельсы открыты: иначе HUD показывал бы ×1000,
        /// а время шло бы ×10.</summary>
        public void WarpUp()
        {
            TakeManualWarp();
            int next = WarpIndex + 1;
            if (Active != null && next < Warps.Length && Warps[next] > MaxPhysicsWarp)
            {
                // Манёвр (§6.11): рельсы закрыты и на прожиге, и в окне перед ним — иначе ускорение проскочило бы
                // старт и автопилот начал бы прожиг с опозданием. Физика до ×MaxPhysicsWarp остаётся.
                string why = RailsBlocker(Active) ?? (WarpLimitTime() <= Time + 1 ? "подходит манёвр" : null);
                if (why != null)
                {
                    Post("Больше ×" + MaxPhysicsWarp + " нельзя: " + why);
                    return;
                }
            }
            SetWarp(next);
            playerWarp = true;
        }
        public void WarpDown()
        {
            TakeManualWarp();
            SetWarp(WarpIndex - 1);
            playerWarp = true;
        }

        /// <summary>«/»: ×1. Если ускорением уже правил игрок поверх автопилота и время идёт ×1 — вернуть его автопилоту.</summary>
        public void WarpReset()
        {
            if (AutoWarpManual && WarpIndex == 0)
            {
                AutoWarpPaused = false;
                Post("Ускорением снова правит автопилот");
                return;
            }
            TakeManualWarp();
            SetWarp(0);
        }

        /// <summary>Клавиша ускорения при работающем автоускорении: управление переходит игроку до конца автопилотов,
        /// иначе DriveWarp перезаписывал бы ступень каждый кадр и «.» не действовала.</summary>
        void TakeManualWarp()
        {
            if (!autoDriving) return;
            autoDriving = false;
            autoPhysics = false;
            AutoWarpPaused = true;
            Post("Ускорение вручную до конца автопилота (/ на ×1 — вернуть автопилоту)");
        }

        public void SetWarp(int index)
        {
            WarpIndex = Math.Max(0, Math.Min(Warps.Length - 1, index));
            playerWarp = false;
        }

        /// <summary>Почему нельзя на рельсы; null — можно.</summary>
        public string RailsBlocker(Vessel v)
        {
            if (v == null || !v.Alive) return null;
            if (v.AnyEngineRunning) return "работает двигатель";
            // Ступень на возврате (§6.9) живёт только в физике — рельсы закрыты для всех, пока она не села.
            if (AnyRecovery) return "посадка ступени";
            if (v.RcsForward > 0 || v.RcsTranslate.sqrMagnitude > 0) return "работает РСУ";
            if (v == Active && (Docking != null && Docking.Close || Lunar != null && Lunar.Close)) return "идёт стыковка";
            // Без узла автопилот стыковки ещё не спланировал перелёт, а планирует он только в физике: на рельсах
            // (тест apollo11, ×6) он не вызывался ни разу — двое суток ожидания впустую.
            if (v == Active && Docking != null && Docking.Phase == DockingAutopilot.PhaseType.Plan) return "планируется сближение";
            // Перестроение «Аполлона» решается в Update (физика): на автоускорении до сферы Луны оно бы не наступило.
            if (v == Active && Lunar != null && Lunar.NeedsPhysics) return "перестроение";
            // Автопилот работает только в полной физике; на рельсах допустим лишь пассивный участок.
            if (v == Active && Ascent != null && Ascent.Phase != AscentAutopilot.PhaseType.Coast) return "идёт выведение";
            if (v == Active && Landing != null && Landing.Phase != LandingAutopilot.PhaseType.Coast) return "идёт посадка";
            // До проверки посадки: луноход едет, стоя на грунте, а на рельсах борт на поверхности неподвижен.
            if (v == Active && Mission != null && Mission.NeedsPhysics) return "программа миссии";
            if (v.IsLanded) return null;
            if (v.Position.magnitude < PatchedConics.RailsFloorRadius(v.Body))
                return v.Body.HasAtmosphere ? "в атмосфере" : "слишком низко над поверхностью";
            if (v.PilotInput.sqrMagnitude > 1e-6) return "идёт управление";
            return null;
        }

        /// <summary>Шаг вселенной на realDt секунд реального времени.</summary>
        public void Advance(double realDt)
        {
            realDt = Math.Min(realDt, 0.1);
            if (realDt <= 0) return;
            // Планирование перелёта — раз в кадр и на рельсах тоже (вход в сферу Луны случается под ускорением), и ДО
            // выбора ускорения: иначе первый кадр после Y улетал на ×1e7 раньше, чем автопилот поставил узел разгона.
            // Сценарий миссии — первым: он запускает частные автопилоты, и те должны увидеть кадр, в котором созданы.
            if (Mission != null && Mission.Tick(this)) Mission = null;
            if (Lunar != null && Lunar.Tick(this)) Lunar = null;
            DriveWarp();
            double warp = Warps[WarpIndex];
            bool rails = WarpIndex > 0 && Active != null && RailsBlocker(Active) == null && !(autoDriving && autoPhysics);
            if (rails && WarpLimitTime() <= Time + 1)
            {
                rails = false;
                // У манёвра рельсы закрыты, но ×5…×10, выбранные игроком, — это физика: автопилоты в ней работают.
                if (!(playerWarp && warp <= MaxPhysicsWarp))
                {
                    SetWarp(0);
                    warp = 1;
                    if (!autoDriving) Post("Ускорение сброшено: подходит время манёвра");
                }
            }

            // Автопилот миссии ручной ввод не снимает — только Y (игрок задевал руль и терял многочасовой полёт).
            // Ввод лунохода задаёт сам автопилот миссии, так что при Mission сюда не попадаем вовсе.
            if (Active != null && Active.PilotInput.sqrMagnitude > 1e-6 && AutopilotActive && Mission == null)
            {
                Ascent = null;
                NodePilot = null;
                Landing = null;
                Docking = null;
                Lunar = null;
                Mission = null;
                Post("Автопилот отключён: ручное управление");
            }
            if (Active != null && Active.PilotInput.sqrMagnitude > 1e-6 && RecoveryOf(Active) is BoosterLandingAutopilot rp)
            {
                rp.Abort("ручное управление");
                Active.EngineLimit = 0;
                Post("Автопилот посадки ступени отключён: ручное управление");
            }

            if (rails)
            {
                EffectiveWarp = warp;
                RailsActive = true;
                AdvanceRails(realDt * warp);
            }
            else
            {
                EffectiveWarp = Math.Min(warp, MaxPhysicsWarp);
                RailsActive = false;
                AdvancePhysics(realDt * EffectiveWarp);
                CheckDocking();
            }
            System.Update(Time);
            PinWreck(Active);
            Vessels.RemoveAll(v => !v.Alive && v != Active);
        }

        /// <summary>
        /// Погибший активный борт физика больше не двигает, и он замирал в невращающихся осях, а взрыв и обломки
        /// (BlastEffects) стоят в осях тела — Земля уезжала из-под камеры на ≈ 400 м/с (§9.5). Место гибели
        /// крепим к телу в тот же момент, что и взрыв (по Body.Orientation текущего кадра), и дальше ведём вместе с ним.
        /// </summary>
        void PinWreck(Vessel v)
        {
            if (v == null || v.Alive || v.DestroyedOn == null) return;
            var body = v.DestroyedOn;
            if (v.Body != body) return;
            if (double.IsNaN(v.WreckLocal.x)) v.WreckLocal = body.Orientation.Inverse * v.Position;
            v.Position = body.Orientation * v.WreckLocal;
            v.Velocity = Vector3d.Cross(FlightPhysics.SpinAxis(body, Time), v.Position);
        }

        // ---------------------------------------------------------------- полная физика

        void AdvancePhysics(double dt)
        {
            if (Active != null && Active.OnRails) LeaveRails(Active);
            double t0 = Time; // положения бортов вне физики относятся к началу кадра
            int n = Math.Max(1, (int)Math.Ceiling(dt / FlightPhysics.MaxStep - 1e-9));
            double h = dt / n;
            var physics = new List<Vessel>();
            var passive = PassivePhysicsSet();
            // Погибшая ступень из физики выпадает (v.Alive) — её пилот сам не узнает, а рельсы держал бы закрытыми вечно.
            foreach (var p in Recoveries)
                if (p.Running && !p.Vessel.Alive) p.Abort(p.Vessel.DestroyReason ?? "потеряна");
            for (int s = 0; s < n; s++)
            {
                physics.Clear();
                foreach (var v in Vessels)
                    if (v.Alive && (v == Active || passive.Contains(v) && NeedsPassivePhysics(v) || RecoveryOf(v) != null)) physics.Add(v);

                foreach (var v in physics)
                {
                    if (v.OnRails) LeaveRails(v);
                    FlightControl.Update(v, Time);
                }
                RunAutopilots(h);
                foreach (var p in Recoveries)
                    if (p.Running && physics.Contains(p.Vessel)) p.Update(Time, h);
                foreach (var v in physics) FlightPhysics.Step(v, Time, h);
                Time += h;
                foreach (var v in physics) CheckSoi(v);
                if (Collisions) Collide(physics);
            }
            foreach (var v in Vessels)
            {
                if (v == Active || !v.Alive) continue;
                if (v.IsLanded) FlightPhysics.UpdateLandedPose(v, Time);
                else if (!physics.Contains(v)) MovePassive(v, Time, passive.Contains(v) || passive.Count < MaxPassivePhysics, t0);
            }
            eventValid = false;
        }

        void RunAutopilots(double h)
        {
            if (Active == null) return;
            // Программа миссии (тангаж «Редстоуна») идёт до частных автопилотов и не исключает их.
            bool missionStage = Mission != null && MissionVessel == null && Mission.Update(Active, Time, h) == AutopilotRequest.Stage;
            AutopilotRequest req = AutopilotRequest.None;
            if (Ascent != null)
            {
                req = Ascent.Update(Active, Time, h);
                if (req == AutopilotRequest.Finished) Ascent = null;
            }
            else if (NodePilot != null)
            {
                req = NodePilot.Update(Active, Time, h);
                if (req == AutopilotRequest.Finished) NodePilot = null;
            }
            else if (Landing != null)
            {
                req = Landing.Update(Active, Time, h);
                if (req == AutopilotRequest.Finished) Landing = null;
            }
            else if (Docking != null)
            {
                req = Docking.Update(Active, Time, h);
                if (req == AutopilotRequest.Finished) Docking = null;
            }
            else if (Lunar != null)
            {
                req = Lunar.Update(Active, Time, h);
                if (req == AutopilotRequest.Finished) Lunar = null;
            }
            if (req == AutopilotRequest.Stage || missionStage) Stage();
        }

        bool NeedsPassivePhysics(Vessel v)
        {
            if (v == Active || v.IsLanded || Active == null) return false;
            return v.Position.magnitude < PatchedConics.RailsFloorRadius(v.Body);
        }

        /// <summary>Неактивные борта под полной физикой на этот кадр: ниже «пола», ближайшие к активному, не больше MaxPassivePhysics.</summary>
        HashSet<Vessel> PassivePhysicsSet()
        {
            var set = new HashSet<Vessel>();
            if (Active == null) return set;
            var cand = new List<(double d, Vessel v)>();
            var at = Active.Body.Position + Active.Position;
            foreach (var v in Vessels)
            {
                if (!v.Alive || v == Active) continue;
                bool below = NeedsPassivePhysics(v);
                // Падающий на рельсах в этот кадр пересечёт «пол» — место под него держим заранее.
                if (!below && v.OnRails && !v.IsLanded && v.Orbit.PeriapsisRadius < PatchedConics.RailsFloorRadius(v.Body)) below = true;
                if (below) cand.Add(((v.Body.Position + v.Position - at).sqrMagnitude, v));
            }
            cand.Sort((x, y) => x.d.CompareTo(y.d));
            for (int i = 0; i < cand.Count && i < MaxPassivePhysics; i++) set.Add(cand[i].v);
            return set;
        }

        // ---------------------------------------------------------------- столкновения (§5)

        /// <summary>
        /// Столкновения бортов под физикой: каждый — капсула по оси (отрезок от низа до верха, ужатый на радиус корпуса).
        /// При сближении точек контакта — импульс с восстановлением Restitution, с плечом (закручивает); быстрее CrashSpeed —
        /// оба разрушены. Расходящиеся пары не трогаются: так отстрел вплотную и толчки разделения не дают ложных ударов.
        /// </summary>
        internal void Collide(List<Vessel> list)
        {
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    if (!CanCollide(a, b)) continue;
                    var dp = b.Position - a.Position;
                    if (dp.sqrMagnitude > 1e8) continue; // дальше 10 км — заведомо мимо
                    Hull(a, out var a0, out var a1, out double ra, out double la);
                    Hull(b, out var b0, out var b1, out double rb, out double lb);
                    if (dp.magnitude > (la + lb) * 0.5 + ra + rb) continue;
                    ClosestPoints(a0, a1, b0, b1, out var ca, out var cb);
                    var d = cb - ca;
                    double dist = d.magnitude;
                    double pen = ra + rb - dist;
                    if (pen <= 0) continue;
                    var nrm = dist > 1e-6 ? d / dist : Vector3d.AnyPerpendicular(a.NoseP).normalized;
                    var contact = ca + nrm * (ra - pen * 0.5);
                    var rA = contact - a.Position;
                    var rB = contact - b.Position;
                    var vA = a.Velocity + a.LocalToWorld(Vector3d.Cross(a.AngularVelocity, a.WorldToLocal(rA)));
                    var vB = b.Velocity + b.LocalToWorld(Vector3d.Cross(b.AngularVelocity, b.WorldToLocal(rB)));
                    double vn = Vector3d.Dot(vB - vA, nrm);
                    if (vn >= 0) continue;
                    if (-vn > CrashSpeed)
                    {
                        a.Destroy($"Столкновение с «{b.Name}» на {-vn:F0} м/с");
                        b.Destroy($"Столкновение с «{a.Name}» на {-vn:F0} м/с");
                        continue;
                    }
                    double ma = a.Mass, mb = b.Mass;
                    var Ia = a.Inertia();
                    var Ib = b.Inertia();
                    // Угловой вклад в эффективную массу: n · ((I⁻¹ (r × n)) × r), всё в связанных осях своего борта.
                    double Ang(Vessel v, Vector3d I, Vector3d r)
                    {
                        var rl = v.WorldToLocal(r);
                        var nl = v.WorldToLocal(nrm);
                        var t = Vector3d.Cross(rl, nl);
                        var w = new Vector3d(t.x / I.x, t.y / I.y, t.z / I.z);
                        return Vector3d.Dot(Vector3d.Cross(w, rl), nl);
                    }
                    double k = 1 / ma + 1 / mb + Ang(a, Ia, rA) + Ang(b, Ib, rB);
                    double J = -(1 + Restitution) * vn / k;
                    var imp = nrm * J;
                    a.Velocity -= imp / ma;
                    b.Velocity += imp / mb;
                    void Spin(Vessel v, Vector3d I, Vector3d r, Vector3d p)
                    {
                        var t = Vector3d.Cross(v.WorldToLocal(r), v.WorldToLocal(p));
                        v.AngularVelocity += new Vector3d(t.x / I.x, t.y / I.y, t.z / I.z);
                        v.SasHoldValid = false;
                    }
                    Spin(a, Ia, rA, -imp);
                    Spin(b, Ib, rB, imp);
                    // Разводим перекрытие по массам, чтобы следующий шаг не начинался внутри.
                    a.Position -= nrm * (pen * mb / (ma + mb));
                    b.Position += nrm * (pen * ma / (ma + mb));
                    if (a == Active || b == Active) Post($"Удар: {(a == Active ? b : a).Name}, {-vn:F1} м/с");
                }
        }

        bool CanCollide(Vessel a, Vessel b)
        {
            if (!a.Alive || !b.Alive || a.IsLanded || b.IsLanded || a.Body != b.Body) return false;
            if (a.NoCollideUntil > Time || b.NoCollideUntil > Time || Fresh(a, b)) return false;
            // Створки обтекателя раскрываются вокруг груза вплотную — их разводит сам сброс (Vessel.SplitFairing).
            if (a.FairingHalf != 0 || b.FairingHalf != 0) return false;
            // Сближение двух бортов с узлами — дело стыковки (§6.6), её захват мягче удара.
            return !(a.HasPort() && b.HasPort());
        }

        /// <summary>Ось борта как капсула: концы отрезка в P (от центра тела), радиус и длина.</summary>
        static void Hull(Vessel v, out Vector3d p0, out Vector3d p1, out double r, out double len)
        {
            v.MassProperties(out _, out double com, out len, out _);
            r = v.HullRadius();
            var nose = v.NoseP;
            var bottom = v.Position - nose * com;
            double lo = Math.Min(r, len * 0.5), hi = Math.Max(len - r, len * 0.5);
            p0 = bottom + nose * lo;
            p1 = bottom + nose * hi;
        }

        /// <summary>Ближайшие точки двух отрезков (Ericson, Real-Time Collision Detection, §5.1.9).</summary>
        static void ClosestPoints(Vector3d p1, Vector3d q1, Vector3d p2, Vector3d q2, out Vector3d c1, out Vector3d c2)
        {
            var d1 = q1 - p1;
            var d2 = q2 - p2;
            var r = p1 - p2;
            double a = Vector3d.Dot(d1, d1), e = Vector3d.Dot(d2, d2), f = Vector3d.Dot(d2, r);
            double s, t;
            if (a <= 1e-12 && e <= 1e-12) { s = t = 0; }
            else if (a <= 1e-12) { s = 0; t = MathD.Clamp(f / e, 0, 1); }
            else
            {
                double c = Vector3d.Dot(d1, r);
                if (e <= 1e-12) { t = 0; s = MathD.Clamp(-c / a, 0, 1); }
                else
                {
                    double b = Vector3d.Dot(d1, d2), den = a * e - b * b;
                    if (den > 1e-9 * a * e) s = MathD.Clamp((b * f - c * e) / den, 0, 1);
                    else
                    {
                        // Параллельные корпуса (бок о бок): ближайших точек целый отрезок, Эриксон берёт его край. Удар в торец
                        // уводит импульс во вращение, и ЦМ продолжают сближаться — берём середину перекрытия проекций.
                        double s0 = -c / a, s1 = (b - c) / a;
                        double lo = Math.Max(0, Math.Min(s0, s1)), hi = Math.Min(1, Math.Max(s0, s1));
                        s = lo <= hi ? (lo + hi) * 0.5 : MathD.Clamp(s0, 0, 1);
                    }
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = MathD.Clamp(-c / a, 0, 1); }
                    else if (t > 1) { t = 1; s = MathD.Clamp((b - c) / a, 0, 1); }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }

        // ---------------------------------------------------------------- рельсы

        void EnterRails(Vessel v) => EnterRails(v, Time);

        /// <summary>epoch — момент, к которому относится v.Position (не обязательно Time: см. AdvancePhysics).</summary>
        void EnterRails(Vessel v, double epoch)
        {
            if (v.OnRails) return;
            v.Orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, epoch);
            v.OnRails = true;
            if (v == Active) eventValid = false;
        }

        /// <summary>
        /// Чит для тестов (меню Esc): перенести активный борт к телу body на высоту altitude над освещённой стороной.
        /// orbital — круговая орбита на восток, иначе покой относительно вращающейся поверхности (падение / посадка).
        /// Автопилоты, узел и ускорение сбрасываются: прежние расчёты относились к другой траектории.
        /// </summary>
        public void Teleport(CelestialBody body, double altitude, bool orbital)
        {
            var v = Active;
            if (v == null || !v.Alive || body == null) return;
            LeaveRails(v);
            SetWarp(0);
            Ascent = null;
            NodePilot = null;
            Landing = null;
            Docking = null;
            Lunar = null;
            Mission = null;
            v.Node = null;
            // Над дневной стороной, чуть к утреннему терминатору — как FlightDebug.Reentry: и свет есть, и рельеф с тенями.
            var s = (System.Sun.Position - body.Position).normalized;
            var pole = body.Orientation * Vector3d.forward;
            var dir = (s * 0.85 - Vector3d.Cross(pole, s) * 0.5).normalized;
            var east = Vector3d.Cross(pole, dir);
            if (east.sqrMagnitude < 1e-12) east = Vector3d.Cross(new Vector3d(1, 0, 0), dir);
            east = east.normalized;
            double r = body.Radius + altitude;
            v.Body = body;
            v.Site = null; // стол и подсказки взлёта к новому месту не относятся
            v.Situation = Situation.Flying;
            if (double.IsNaN(v.LaunchTime)) v.LaunchTime = Time;
            v.Throttle = 0;
            v.AngularVelocity = Vector3d.zero;
            v.Position = dir * r;
            v.Velocity = orbital ? east * Math.Sqrt(body.Mu / r) : Vector3d.Cross(body.AngularVelocity, v.Position);
            eventValid = false;
            Post($"Чит: {body.Name}, {altitude / 1000:0} км{(orbital ? ", круговая орбита" : "")}");
        }

        // ---------------------------------------------------------------- стыковка

        /// <summary>
        /// Захват (§6.6): узлы ближе DockCaptureRange, сближение не быстрее DockMaxSpeed, оси навстречу с точностью
        /// DockMaxAngleDeg. Пара: DockingAutopilot.FinalSpeed &lt; DockMaxSpeed, DockingAutopilot.AlignAngleDeg &lt; DockMaxAngleDeg.
        /// </summary>
        public const double DockCaptureRange = 1, DockMaxSpeed = 0.5, DockMaxAngleDeg = 10;

        void CheckDocking()
        {
            var a = Active;
            // Узел под носовым обтекателем (Crew Dragon, DeployKind.Nose) не стыкуется, пока обтекатель не откинут.
            if (a == null || !a.Alive || a.IsLanded || !a.HasFreePort || !a.PortOpen) return;
            var pa = a.Position + a.NoseP * a.PortHeight();
            foreach (var t in Vessels)
            {
                if (!CanDock(a, t) || Fresh(a, t) || !t.PortOpen) continue;
                var pt = t.Position + t.NoseP * t.PortHeight();
                if (Vector3d.Distance(pa, pt) > DockCaptureRange) continue;
                if ((a.Velocity - t.Velocity).magnitude > DockMaxSpeed) continue;
                if (Vector3d.Angle(a.NoseP, -t.NoseP) > DockMaxAngleDeg * Constants.Deg2Rad) continue;
                // Основа связки — борт со спускаемым аппаратом: после причаливания ЛМ к «Колумбии» управлять дальше КСМ.
                var host = a.HasCapsule() || !t.HasCapsule() ? a : t;
                var guest = host == a ? t : a;
                LeaveRails(a);
                LeaveRails(t);
                if (host != Active) SetActive(host);
                // Причалили к борту миссии (или он сам пришёл к нам) — дальше он и есть активный.
                if (MissionVessel == a || MissionVessel == t) MissionVessel = null;
                host.Dock(guest);
                // Тяга РСУ связки — сумма по секциям (с МКС 2·10^5 Н): невыключенная команда сближения за 10 мин
                // разгоняла связку на ~120 м/с (420×420 → 419×862 км, гибель на 9,3 g). Захват — конец манёвра.
                host.RcsTranslate = Vector3d.zero;
                host.RcsForward = 0;
                guest.RcsTranslate = Vector3d.zero;
                guest.RcsForward = 0;
                host.Target = null;
                if (host.Sas >= SasMode.Target) host.Sas = SasMode.Stability; // цели больше нет — держим, что есть
                guest.Event -= OnVesselEvent;
                Vessels.Remove(guest);
                Docking = null;
                eventValid = false;
                return;
            }
        }

        /// <summary>Можно ли причалить a к t: один проект (секции общие), разные секции, оба узла свободны, оба в полёте у одного тела.</summary>
        public static bool CanDock(Vessel a, Vessel t)
        {
            if (a == null || t == null || a == t || !a.Alive || !t.Alive || a.IsLanded || t.IsLanded) return false;
            if (a.Body != t.Body || a.Design != t.Design || !a.HasFreePort || !t.HasFreePort) return false;
            for (int i = 0; i < a.Attached.Length; i++)
                if (a.Attached[i] && t.Attached[i]) return false;
            return true;
        }

        /// <summary>Ближайший борт, к которому можно причалить активному (клавиша V).</summary>
        public Vessel NearestDockTarget()
        {
            Vessel best = null;
            double bd = double.PositiveInfinity;
            foreach (var t in Vessels)
            {
                if (!CanDock(Active, t)) continue;
                double d = Vector3d.Distance(t.Position, Active.Position);
                if (d < bd) { bd = d; best = t; }
            }
            return best;
        }

        /// <summary>Состояние борта на момент time: внутри шага физики неактивный борт на рельсах ещё не сдвинут.</summary>
        public static void StateOf(Vessel v, double time, out Vector3d r, out Vector3d vel)
        {
            if (v.OnRails && !v.IsLanded && v.Orbit != null) v.Orbit.GetState(time, out r, out vel);
            else
            {
                r = v.Position;
                vel = v.Velocity;
            }
        }

        void LeaveRails(Vessel v)
        {
            if (!v.OnRails) return;
            v.OnRails = false;
            // Вращение на рельсах заморожено: после ускорения корабль не должен кувыркаться.
            v.AngularVelocity = Vector3d.zero;
        }

        /// <summary>
        /// Автоускорение (§6.11): пока работает автопилот, ступень Warps выбирает он — на рельсах наибольшая, у которой до
        /// ближайшего события (WarpLimitTime, смена сферы влияния, вход в атмосферу) остаётся AutoWarpLead реальных секунд;
        /// в физике — ×1, а на долгом прожиге в вакууме ×MaxPhysicsWarp. Когда автопилоты закончились, ускорение — в ×1:
        /// иначе после выхода на орбиту Луны время продолжало бы нестись на последней ступени.
        /// </summary>
        void DriveWarp()
        {
            bool active = AutopilotActive && Active != null && Active.Alive;
            if (!active)
            {
                if (autoDriving) SetWarp(0);
                autoDriving = false;
                autoPhysics = false;
                AutoWarpPaused = false;
                return;
            }
            autoDriving = AutoWarp && !AutoWarpPaused;
            if (autoDriving) SetWarp(AutoWarpIndex());
        }

        int AutoWarpIndex()
        {
            var v = Active;
            autoPhysics = false;
            if (RailsBlocker(v) != null)
            {
                bool air = v.Body.HasAtmosphere && v.Altitude < v.Body.AtmosphereTop;
                if (v.AnyEngineRunning && v.Node != null && !air && FlightControl.BurnTime(v, v.Node.Remaining.magnitude) > AutoBurnWarpMin)
                    return Array.IndexOf(Warps, MaxPhysicsWarp);
                // Миссия целиком: выведение, спуск в атмосфере и посадка проверены тестами ядра на ×10 физики
                // (шаг интегратора от ускорения не зависит, растёт лишь число шагов в кадре).
                if (Mission != null && Mission.PhysicsWarpOk(this)) return Array.IndexOf(Warps, MaxPhysicsWarp);
                return 0;
            }
            double next = NextEventTime();
            int i = Ladder(Math.Min(WarpLimitTime(), next) - Time, Warps.Length - 1);
            if (i > 0) return i;
            // Рельсы кончились (окно NodeWarpMargin + полпрожига перед узлом): автопилоты работают и в ускоренной
            // физике, так что ждём старта прожига на ×5…×10, а не на ×1 — иначе каждое окно стоило ≈ 100 с реального времени.
            double start = WarpLimitTime();
            if (v.Node != null)
            {
                double burn = FlightControl.BurnTime(v, v.Node.Total);
                start = v.Node.Time - (double.IsInfinity(burn) ? 0 : burn / 2);
            }
            i = Ladder(Math.Min(start, next) - Time, Array.IndexOf(Warps, MaxPhysicsWarp));
            autoPhysics = i > 0;
            return i;
        }

        /// <summary>Наибольшая ступень не выше max, у которой до события остаётся AutoWarpLead реальных секунд.</summary>
        static int Ladder(double horizon, int max)
        {
            int i = 0;
            while (i < max && Warps[i + 1] * AutoWarpLead <= horizon) i++;
            return i;
        }

        /// <summary>Ближайшая смена участка траектории активного борта (тот же кеш, что у AdvanceRails).</summary>
        double NextEventTime()
        {
            var a = Active;
            if (a.IsLanded) return double.PositiveInfinity;
            if (!eventValid)
            {
                var orbit = a.OnRails ? a.Orbit : KeplerOrbit.FromState(a.Position, a.Velocity, a.Body.Mu, Time);
                nextEvent = PatchedConics.FindNext(orbit, a.Body, Time, PatchedConics.DefaultHorizon(orbit));
                eventValid = true;
            }
            return nextEvent.Time;
        }

        /// <summary>Момент, раньше которого ускорение надо прервать (манёвр, довыведение).</summary>
        double WarpLimitTime()
        {
            double limit = double.PositiveInfinity;
            var v = Active;
            if (v?.Node != null)
            {
                double burn = FlightControl.BurnTime(v, v.Node.Total);
                if (double.IsInfinity(burn)) burn = 0;
                limit = Math.Min(limit, v.Node.Time - burn / 2 - NodeWarpMargin);
            }
            if (Ascent != null && Ascent.Phase == AscentAutopilot.PhaseType.Coast && v != null && !v.IsLanded)
            {
                // Орбита с рельсов здесь ещё не посчитана (вызов — до входа на рельсы), берём из состояния.
                var orbit = v.OnRails ? v.Orbit : KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, Time);
                if (orbit.IsElliptic) limit = Math.Min(limit, Time + orbit.TimeToApoapsis(Time) - 60);
            }
            if (Landing != null && v != null && v.Alive && !v.IsLanded)
            {
                var orbit = v.OnRails ? v.Orbit : KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, Time);
                limit = Math.Min(limit, Landing.WarpLimit(v, orbit, Time));
            }
            if (Mission != null) limit = Math.Min(limit, Mission.WarpLimit);
            return limit;
        }

        void AdvanceRails(double dt)
        {
            var a = Active;
            double target = Time + dt;
            double limit = WarpLimitTime();

            for (int guard = 0; guard < 8 && Time < target; guard++)
            {
                if (a.Alive && !a.IsLanded)
                {
                    EnterRails(a);
                    if (!eventValid)
                    {
                        nextEvent = PatchedConics.FindNext(a.Orbit, a.Body, Time, PatchedConics.DefaultHorizon(a.Orbit));
                        eventValid = true;
                    }
                }
                else nextEvent = Transition.None;

                double stop = Math.Min(target, Math.Min(nextEvent.Time, limit));
                MoveAll(stop);
                Time = stop;

                if (nextEvent.Type != TransitionType.None && stop >= nextEvent.Time)
                {
                    HandleTransition(a, nextEvent);
                    eventValid = false;
                    // После смены тела предел другой: посадку на Луну планируют уже в её сфере влияния.
                    limit = WarpLimitTime();
                    if (nextEvent.Type == TransitionType.Atmosphere) break;
                    continue;
                }
                if (stop >= limit)
                {
                    SetWarp(0);
                    if (!autoDriving) Post("Ускорение сброшено: подходит время манёвра");
                    break;
                }
            }
        }

        void MoveAll(double t)
        {
            foreach (var v in Vessels)
            {
                if (!v.Alive) continue;
                if (v.IsLanded) FlightPhysics.UpdateLandedPose(v, t);
                else if (v == Active)
                {
                    v.Orbit.GetState(t, out v.Position, out v.Velocity);
                    FlightPhysics.UpdateTelemetry(v, t);
                }
                else MovePassive(v, t);
            }
        }

        /// <summary>
        /// Неактивный корабль в полёте: рельсы. Провалившийся под «пол» в полной физике (keep) сходит с рельсов и со
        /// следующего кадра падает по физике; на ускорении (или сверх MaxPassivePhysics) — потерян.
        /// Борт, сходящий на рельсы, стоит там, где был в момент since (по умолчанию Time). В AdvancePhysics Time к этому
        /// моменту уже ушло на кадр вперёд: орбита с эпохой Time переносила отошедший борт на кадр по орбите — замер
        /// расстыковки на 300 км, кадр 0,1 с: цель прыгала на 773 м (7,73 км/с × 0,1 с) и «уходила» на 1,2 м/с.
        /// </summary>
        void MovePassive(Vessel v, double t, bool keep = false, double since = double.NaN)
        {
            double floor = PatchedConics.RailsFloorRadius(v.Body);
            if (!v.OnRails)
            {
                if (v.Position.magnitude < floor)
                {
                    LoseVessel(v);
                    return;
                }
                EnterRails(v, double.IsNaN(since) ? Time : since);
            }
            // Перицентр под «полом»: если за шаг прошли его — корабль вошёл в атмосферу вне зоны физики.
            if (v.Orbit.PeriapsisRadius < floor)
            {
                double tIn = v.Orbit.NextTimeAtRadius(floor, Time, false);
                if (!double.IsNaN(tIn) && tIn <= t)
                {
                    if (keep)
                    {
                        v.Orbit.GetState(t, out v.Position, out v.Velocity);
                        LeaveRails(v);
                    }
                    else LoseVessel(v);
                    return;
                }
            }
            v.Orbit.GetState(t, out v.Position, out v.Velocity);
            var before = v.Body;
            CheckSoi(v);
            if (v.Body != before) v.Orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, t);
        }

        void LoseVessel(Vessel v)
        {
            v.Destroy(v.Body.HasAtmosphere ? $"Сгорел в атмосфере: {v.Body.Name}" : $"Упал на {v.Body.Name}");
        }

        void HandleTransition(Vessel v, Transition tr)
        {
            switch (tr.Type)
            {
                case TransitionType.Escape:
                case TransitionType.Encounter:
                {
                    var r = v.Position;
                    var vel = v.Velocity;
                    PatchedConics.ChangeFrame(v.Body, tr.NewBody, Time, ref r, ref vel);
                    v.Body = tr.NewBody;
                    v.Position = r;
                    v.Velocity = vel;
                    v.Orbit = KeplerOrbit.FromState(r, vel, v.Body.Mu, Time);
                    Post(tr.Type == TransitionType.Escape
                        ? $"Выход из сферы влияния → {v.Body.Name}"
                        : $"Вход в сферу влияния: {v.Body.Name}");
                    break;
                }
                case TransitionType.Atmosphere:
                    LeaveRails(v);
                    SetWarp(0);
                    Post(v.Body.HasAtmosphere ? $"Вход в атмосферу: {v.Body.Name} — ускорение сброшено"
                        : $"Снижение к поверхности: {v.Body.Name} — ускорение сброшено");
                    break;
            }
        }

        /// <summary>Проверка смены сферы влияния по расстоянию — для физики и неактивных кораблей.</summary>
        void CheckSoi(Vessel v)
        {
            if (!v.Alive || v.IsLanded) return;
            var body = v.Body;
            if (body.Parent != null && v.Position.magnitude > body.SoiRadius)
            {
                Switch(v, body.Parent);
                return;
            }
            foreach (var c in body.Children)
            {
                c.LocalStateAt(Time, out var rc, out _);
                if ((v.Position - rc).magnitude < c.SoiRadius)
                {
                    Switch(v, c);
                    return;
                }
            }
        }

        void Switch(Vessel v, CelestialBody to)
        {
            var r = v.Position;
            var vel = v.Velocity;
            PatchedConics.ChangeFrame(v.Body, to, Time, ref r, ref vel);
            var from = v.Body;
            v.Body = to;
            v.Position = r;
            v.Velocity = vel;
            if (v.OnRails) v.Orbit = KeplerOrbit.FromState(r, vel, to.Mu, Time);
            if (v == Active)
            {
                eventValid = false;
                Post(to == from.Parent ? $"Выход из сферы влияния → {to.Name}" : $"Вход в сферу влияния: {to.Name}");
            }
        }

        // ---------------------------------------------------------------- прогноз

        public List<OrbitPatch> PredictActive(int maxPatches = 4)
        {
            var v = Active;
            if (v == null || !v.Alive || v.IsLanded) return new List<OrbitPatch>();
            return PatchedConics.Predict(v.Body, v.Position, v.Velocity, Time, v.Node, maxPatches);
        }

        /// <summary>Поставить узел манёвра на орбите активного корабля (компоненты — в осях орбиты на момент узла).</summary>
        public void SetNode(double time, double prograde, double normal, double radial)
        {
            var v = Active;
            if (v == null || v.IsLanded) return;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, Time);
            var node = new ManeuverNode { Time = time, Prograde = prograde, Normal = normal, Radial = radial };
            node.Remaining = node.WorldDeltaV(orbit);
            v.Node = node;
        }
    }
}
