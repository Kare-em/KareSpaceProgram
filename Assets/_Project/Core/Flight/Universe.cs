using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Вселенная одной партии (GDD §3): время, корабли, ускорение. Активный корабль летит по полной
    /// физике или на рельсах (кеплерова орбита), остальные — на рельсах или удаляются: считать
    /// физику обломка за тысячи километров незачем, а на ускорении — невозможно.
    /// </summary>
    public sealed class Universe
    {
        public static readonly double[] Warps = { 1, 5, 10, 50, 100, 1e3, 1e4, 1e5, 1e6, 1e7 };
        /// <summary>Потолок физического ускорения (атмосфера, работа двигателя, автопилот). Шаг интегратора
        /// остаётся MaxStep — ×10 только множит подшаги кадра (≈8 при 60 к/с), точность не теряется.
        /// Пара: индекс 2 в Warps = ×10, до него WarpUp пускает при закрытых рельсах.</summary>
        public const double MaxPhysicsWarp = 10;
        /// <summary>Ближе этого к активному кораблю обломки в атмосфере считаются физикой.</summary>
        public const double PassiveRange = 25000;
        /// <summary>За сколько секунд до запуска манёвра ускорение сбрасывается само.</summary>
        public const double NodeWarpMargin = 30;

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
            eventValid = false;
        }

        void OnVesselEvent(Vessel v, string msg)
        {
            if (v == Active) Post(msg);
            else if (!v.IsDebris && !v.Alive) Post($"{v.Name}: {msg}");
        }

        /// <summary>Следующая ступень активного корабля (пробел).</summary>
        public void Stage()
        {
            if (Active == null || !Active.Alive) return;
            if (Active.OnRails) LeaveRails(Active);
            foreach (var d in Active.Stage())
            {
                d.Event += OnVesselEvent;
                Vessels.Add(d);
            }
            eventValid = false;
        }

        // ---------------------------------------------------------------- ускорение времени

        /// <summary>Выше физического потолка — только если рельсы открыты: иначе HUD показывал бы ×1000,
        /// а время шло бы ×10.</summary>
        public void WarpUp()
        {
            int next = WarpIndex + 1;
            if (Active != null && RailsBlocker(Active) != null && next < Warps.Length && Warps[next] > MaxPhysicsWarp)
            {
                Post("Больше ×" + MaxPhysicsWarp + " нельзя: " + RailsBlocker(Active));
                return;
            }
            SetWarp(next);
        }
        public void WarpDown() => SetWarp(WarpIndex - 1);

        public void SetWarp(int index)
        {
            WarpIndex = Math.Max(0, Math.Min(Warps.Length - 1, index));
        }

        /// <summary>Почему нельзя на рельсы; null — можно.</summary>
        public string RailsBlocker(Vessel v)
        {
            if (v == null || !v.Alive) return null;
            if (v.AnyEngineRunning) return "работает двигатель";
            if (v.RcsForward > 0) return "работает РСУ";
            // Автопилот работает только в полной физике; на рельсах допустим лишь пассивный участок.
            if (v == Active && Ascent != null && Ascent.Phase != AscentAutopilot.PhaseType.Coast) return "идёт выведение";
            if (v == Active && Landing != null && Landing.Phase != LandingAutopilot.PhaseType.Coast) return "идёт посадка";
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
            double warp = Warps[WarpIndex];
            bool rails = WarpIndex > 0 && Active != null && RailsBlocker(Active) == null;
            if (rails && WarpLimitTime() <= Time + 1)
            {
                SetWarp(0);
                warp = 1;
                rails = false;
                Post("Ускорение сброшено: подходит время манёвра");
            }

            if (Active != null && Active.PilotInput.sqrMagnitude > 1e-6 && (Ascent != null || NodePilot != null || Landing != null))
            {
                Ascent = null;
                NodePilot = null;
                Landing = null;
                Post("Автопилот отключён: ручное управление");
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
            }
            System.Update(Time);
            Vessels.RemoveAll(v => !v.Alive && v != Active);
        }

        // ---------------------------------------------------------------- полная физика

        void AdvancePhysics(double dt)
        {
            if (Active != null && Active.OnRails) LeaveRails(Active);
            int n = Math.Max(1, (int)Math.Ceiling(dt / FlightPhysics.MaxStep - 1e-9));
            double h = dt / n;
            var physics = new List<Vessel>();
            for (int s = 0; s < n; s++)
            {
                physics.Clear();
                foreach (var v in Vessels)
                    if (v.Alive && (v == Active || NeedsPassivePhysics(v))) physics.Add(v);

                foreach (var v in physics)
                {
                    if (v.OnRails) LeaveRails(v);
                    FlightControl.Update(v, Time);
                }
                RunAutopilots(h);
                foreach (var v in physics) FlightPhysics.Step(v, Time, h);
                Time += h;
                foreach (var v in physics) CheckSoi(v);
            }
            foreach (var v in Vessels)
            {
                if (v == Active || !v.Alive) continue;
                if (v.IsLanded) FlightPhysics.UpdateLandedPose(v, Time);
                else if (!physics.Contains(v)) MovePassive(v, Time);
            }
            eventValid = false;
        }

        void RunAutopilots(double h)
        {
            if (Active == null) return;
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
            if (req == AutopilotRequest.Stage) Stage();
        }

        bool NeedsPassivePhysics(Vessel v)
        {
            if (v == Active || v.IsLanded || Active == null) return false;
            if (v.Position.magnitude >= PatchedConics.RailsFloorRadius(v.Body)) return false;
            return v.Body == Active.Body && Vector3d.Distance(v.Position, Active.Position) < PassiveRange;
        }

        // ---------------------------------------------------------------- рельсы

        void EnterRails(Vessel v)
        {
            if (v.OnRails) return;
            v.Orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, Time);
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

        void LeaveRails(Vessel v)
        {
            if (!v.OnRails) return;
            v.OnRails = false;
            // Вращение на рельсах заморожено: после ускорения корабль не должен кувыркаться.
            v.AngularVelocity = Vector3d.zero;
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
                    Post("Ускорение сброшено: подходит время манёвра");
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

        /// <summary>Неактивный корабль в полёте: рельсы, а провалившийся в атмосферу — потерян.</summary>
        void MovePassive(Vessel v, double t)
        {
            double floor = PatchedConics.RailsFloorRadius(v.Body);
            if (!v.OnRails)
            {
                if (v.Position.magnitude < floor)
                {
                    LoseVessel(v);
                    return;
                }
                EnterRails(v);
            }
            // Перицентр под «полом»: если за шаг прошли его — корабль вошёл в атмосферу вне зоны физики.
            if (v.Orbit.PeriapsisRadius < floor)
            {
                double tIn = v.Orbit.NextTimeAtRadius(floor, Time, false);
                if (!double.IsNaN(tIn) && tIn <= t)
                {
                    LoseVessel(v);
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
