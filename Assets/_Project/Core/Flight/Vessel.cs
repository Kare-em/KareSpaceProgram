using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    public enum Situation
    {
        Landed,
        Splashed,
        Flying,
        Destroyed,
    }

    public enum SasMode
    {
        Off,
        Stability,
        Prograde,
        Retrograde,
        Normal,
        AntiNormal,
        RadialOut,
        RadialIn,
        Maneuver,
    }

    /// <summary>Манёвр: импульс в осях орбиты на момент Time (GDD §6.11, бортовой вычислитель).</summary>
    public sealed class ManeuverNode
    {
        public double Time;
        public double Prograde, Normal, Radial;
        /// <summary>Остаток импульса в системе P. Задаётся при планировании, тает по мере работы двигателя.</summary>
        public Vector3d Remaining;
        public double Total => Remaining.magnitude;

        /// <summary>Импульс в P по орбите корабля в момент узла.</summary>
        public Vector3d WorldDeltaV(KeplerOrbit orbit)
        {
            orbit.GetState(Time, out var r, out var v);
            var pro = v.normalized;
            var nrm = Vector3d.Cross(r, v).normalized;
            var rad = Vector3d.Cross(pro, nrm);
            return pro * Prograde + nrm * Normal + rad * Radial;
        }
    }

    /// <summary>
    /// Корабль: проект + состояние секций + вектор состояния. Положение и скорость — в P относительно
    /// центра текущего тела (невращающиеся оси). Ориентация — в системе Unity (U), связанные оси:
    /// +Y — нос, +X — «брюхо» (на старте восток), +Z — на старте север. Угловая скорость — в связанных осях.
    /// </summary>
    public sealed class Vessel
    {
        static int nextId = 1;

        public int Id;
        public string Name;
        public VesselDesign Design;
        public bool IsDebris;
        /// <summary>Створка сброшенного обтекателя: +1 — половина со стороны +X связанных осей, −1 — со стороны −X, 0 — целый.</summary>
        public int FairingHalf;

        // Секции.
        public bool[] Attached;
        public double[] Propellant;
        public bool[] Armed;
        public bool[] Running;
        public int[] IgnitionsLeft;
        public bool[] ChuteDeployed, ChuteFailed;
        /// <summary>Парашют взведён ступенью, но ещё ждёт высоты и безопасного напора (FlightPhysics.ChuteDeployAltitude).</summary>
        public bool[] ChuteArmed;
        /// <summary>Секунды с ввода парашюта секции — для раскрытия с рифлением.</summary>
        public double[] ChuteOpenTime;
        /// <summary>Попытка запуска уже была при этой «подаче газа»: повтор — только после сброса РУД в 0.</summary>
        bool[] ignitionLatch;
        public int NextStage;

        // Состояние.
        public CelestialBody Body;
        public Vector3d Position, Velocity;
        public QuaternionD Attitude = QuaternionD.identity;
        public Vector3d AngularVelocity;
        public Situation Situation = Situation.Landed;
        public Vector3d AnchorBodyFixed;
        /// <summary>Космодром старта (для стола и креплений в рендере); null — борт создан не на старте.</summary>
        public LaunchSite Site;
        public QuaternionD AttitudeBodyFixed = QuaternionD.identity;
        public KeplerOrbit Orbit;
        public bool OnRails;
        public string DestroyReason;
        public CelestialBody DestroyedOn;
        public double LaunchTime = double.NaN;

        // Управление.
        public double Throttle = 1;
        /// <summary>Ручки пилота: x — тангаж (W +1), y — рысканье (D +1), z — крен (E +1).</summary>
        public Vector3d PilotInput;
        public SasMode Sas = SasMode.Stability;
        public QuaternionD SasHold;
        public bool SasHoldValid;
        /// <summary>Желаемый момент в связанных осях, Н·м — выход регулятора, физика обрезает по возможностям.</summary>
        public Vector3d TorqueCommand;
        /// <summary>Поступательная РСУ вдоль носа (0…1): осаживает топливо перед повторным запуском (GDD §6.3).</summary>
        public double RcsForward;
        public ManeuverNode Node;

        // Телеметрия (обновляет физика).
        public double Altitude, TerrainAltitude, SurfaceSpeed, VerticalSpeed, HorizontalSpeed;
        public double StaticPressure, Density, DynamicPressure, Mach, AngleOfAttack, HeatFlux, GForce;
        public double SettledTimer, OverheatTimer;
        /// <summary>Секунды сверх предела перегрузки экипажа (§4.7) и итог: экипаж погиб, борт летит дальше.</summary>
        public double HighGTimer;
        public bool CrewLost;
        public double CurrentThrust;

        public event Action<Vessel, string> Event;

        public Vessel(VesselDesign design, string name = null)
        {
            Id = nextId++;
            Design = design;
            Name = name ?? design.Name;
            int n = design.Sections.Count;
            Attached = new bool[n];
            Propellant = new double[n];
            Armed = new bool[n];
            Running = new bool[n];
            IgnitionsLeft = new int[n];
            ChuteDeployed = new bool[n];
            ChuteFailed = new bool[n];
            ChuteArmed = new bool[n];
            ChuteOpenTime = new double[n];
            ignitionLatch = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var s = design.Sections[i];
                Attached[i] = true;
                Propellant[i] = s.Propellant;
                IgnitionsLeft[i] = s.HasEngine ? s.Engine.Ignitions : 0;
            }
        }

        public bool Alive => Situation != Situation.Destroyed;
        public bool IsLanded => Situation == Situation.Landed || Situation == Situation.Splashed;
        public bool PropellantSettled => IsLanded || SettledTimer > 0;
        public double MissionTime(double now) => double.IsNaN(LaunchTime) ? 0 : now - LaunchTime;

        public void Raise(string message) => Event?.Invoke(this, message);

        // ---------------------------------------------------------------- геометрия и масса

        public double Mass
        {
            get
            {
                double m = 0;
                for (int i = 0; i < Attached.Length; i++)
                    if (Attached[i]) m += SectionMass(i);
                return m;
            }
        }

        int EnclosingFairing(int i)
        {
            var secs = Design.Sections;
            for (int f = i + 1; f < secs.Count; f++)
                if (Attached[f] && secs[f].Kind == SectionKind.Fairing && f - secs[f].EnclosesBelow <= i)
                    return f;
            return -1;
        }

        public bool IsEnclosed(int i) => EnclosingFairing(i) >= 0;

        /// <summary>Масса секции, кг; у створки — половина обтекателя.</summary>
        double SectionMass(int i)
        {
            var s = Design.Sections[i];
            double m = s.DryMass + Propellant[i];
            return FairingHalf != 0 && s.Kind == SectionKind.Fairing ? m * 0.5 : m;
        }

        /// <summary>
        /// Раскладка пакета по высоте от низа нижней присоединённой секции. Секции под обтекателем
        /// длину не добавляют — их закрывает обтекатель. Нужна и физике (центр масс, плечо), и рендеру.
        /// </summary>
        public double Layout(double[] baseHeight)
        {
            var secs = Design.Sections;
            double h = 0;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Attached[i]) continue;
                var s = secs[i];
                if (s.Kind == SectionKind.Fairing)
                {
                    double b = h;
                    for (int j = i - s.EnclosesBelow; j < i; j++)
                        if (j >= 0 && Attached[j]) { b = baseHeight[j]; break; }
                    baseHeight[i] = b;
                    h = Math.Max(h, b + s.Length);
                }
                else
                {
                    baseHeight[i] = h;
                    h += s.Length;
                }
            }
            return h;
        }

        double[] layoutBuf;

        public void MassProperties(out double mass, out double comHeight, out double length, out double maxRadius)
        {
            if (layoutBuf == null) layoutBuf = new double[Attached.Length];
            length = Layout(layoutBuf);
            mass = 0;
            double moment = 0;
            maxRadius = 0.1;
            var secs = Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Attached[i]) continue;
                double m = SectionMass(i);
                mass += m;
                moment += m * (layoutBuf[i] + secs[i].Length * 0.5);
                maxRadius = Math.Max(maxRadius, secs[i].Radius);
            }
            comHeight = mass > 0 ? moment / mass : 0;
        }

        /// <summary>Главные моменты инерции в связанных осях (X, Y — продольная, Z). Стержень + цилиндр.</summary>
        public Vector3d Inertia()
        {
            MassProperties(out double m, out _, out double len, out double r);
            double lateral = m * (len * len / 12 + r * r / 4);
            return new Vector3d(lateral, Math.Max(m * r * r / 2, 1), lateral);
        }

        /// <summary>Низ секции от низа пакета, м; верно после MassProperties того же кадра.</summary>
        public double SectionBottom(int i) => layoutBuf[i];

        /// <summary>Доступный управляющий момент по осям (X, Y, Z), Н·м: качание двигателей + РСУ.</summary>
        public Vector3d MaxTorque(double pressure)
        {
            MassProperties(out _, out double com, out _, out _);
            double pitch = 0, roll = 0;
            var secs = Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Attached[i]) continue;
                var s = secs[i];
                pitch += s.RcsTorque;
                roll += s.RcsTorque;
                if (!Running[i] || s.Engine.GimbalDeg <= 0) continue;
                double t = s.Engine.Thrust(pressure) * s.EngineCount * EffectiveThrottle(i);
                double side = t * Math.Sin(s.Engine.GimbalDeg * Constants.Deg2Rad);
                pitch += side * Math.Max(0.5, com - layoutBuf[i]);
                // Крен качанием возможен только у связки из нескольких камер.
                if (s.EngineCount > 1) roll += side * s.Radius * 0.5;
            }
            return new Vector3d(pitch, roll, pitch);
        }

        /// <summary>
        /// Тяга РСУ вдоль оси, Н. Отдельного параметра у секций нет — берём долю от управляющего момента:
        /// сопла те же, плечо ~5 м. Расход рабочего тела РСУ пока не считаем.
        /// </summary>
        public double RcsThrust
        {
            get
            {
                double t = 0;
                for (int i = 0; i < Attached.Length; i++)
                    if (Attached[i]) t += Design.Sections[i].RcsTorque * 0.2;
                return t;
            }
        }

        public int TopSection()
        {
            for (int i = Attached.Length - 1; i >= 0; i--)
                if (Attached[i]) return i;
            return -1;
        }

        public int BottomSection()
        {
            for (int i = 0; i < Attached.Length; i++)
                if (Attached[i]) return i;
            return -1;
        }

        public bool HasCrew()
        {
            for (int i = 0; i < Attached.Length; i++)
                if (Attached[i] && Design.Sections[i].Crew > 0) return true;
            return false;
        }

        public bool HasCapsule()
        {
            for (int i = 0; i < Attached.Length; i++)
                if (Attached[i] && Design.Sections[i].Kind == SectionKind.Capsule) return true;
            return false;
        }

        // ---------------------------------------------------------------- двигатели

        public double EffectiveThrottle(int i)
        {
            if (!Running[i]) return 0;
            return Math.Max(Throttle, Design.Sections[i].Engine.MinThrottle);
        }

        /// <summary>Тяга (Н) и расход (кг/с) работающих двигателей при давлении p.</summary>
        public double Thrust(double pressure, out double massFlow)
        {
            double t = 0;
            massFlow = 0;
            var secs = Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Attached[i] || !Running[i]) continue;
                var s = secs[i];
                double k = EffectiveThrottle(i) * s.EngineCount;
                t += s.Engine.Thrust(pressure) * k;
                massFlow += s.Engine.MassFlow * k;
            }
            return t;
        }

        /// <summary>
        /// Логика запуска/останова (GDD §6.3): РУД в 0 глушит двигатель, повторный запуск тратит
        /// попытку, без осадки топлива запуск срывается.
        /// </summary>
        public void UpdateEngines()
        {
            var secs = Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Attached[i] || !secs[i].HasEngine || !Armed[i]) continue;
                if (Throttle <= 0)
                {
                    ignitionLatch[i] = false;
                    if (Running[i])
                    {
                        Running[i] = false;
                        Raise($"{secs[i].Engine.Name}: выключен");
                    }
                    continue;
                }
                if (Running[i] || ignitionLatch[i]) continue;
                ignitionLatch[i] = true;
                if (Propellant[i] <= 0)
                {
                    Raise($"{secs[i].Engine.Name}: нет топлива");
                    continue;
                }
                if (IgnitionsLeft[i] <= 0)
                {
                    Raise($"{secs[i].Engine.Name}: запуски исчерпаны");
                    continue;
                }
                IgnitionsLeft[i]--;
                if (secs[i].Engine.NeedsUllage && !PropellantSettled)
                {
                    Raise($"{secs[i].Engine.Name}: запуск сорван — топливо не осело (осталось запусков: {IgnitionsLeft[i]})");
                    continue;
                }
                Running[i] = true;
                Raise($"{secs[i].Engine.Name}: запуск (осталось запусков: {IgnitionsLeft[i]})");
            }
        }

        /// <summary>Списать топливо за dt; при выработке — останов.</summary>
        public void BurnPropellant(double dt)
        {
            var secs = Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Attached[i] || !Running[i]) continue;
                var s = secs[i];
                Propellant[i] -= s.Engine.MassFlow * s.EngineCount * EffectiveThrottle(i) * dt;
                if (Propellant[i] <= 0)
                {
                    Propellant[i] = 0;
                    Running[i] = false;
                    Raise($"{s.Engine.Name}: топливо выработано");
                }
            }
        }

        public bool AnyEngineRunning
        {
            get
            {
                for (int i = 0; i < Running.Length; i++)
                    if (Attached[i] && Running[i]) return true;
                return false;
            }
        }

        // ---------------------------------------------------------------- ступени

        public bool HasNextStage => NextStage < Design.Sequence.Count;

        /// <summary>Оставшиеся ступени с текущим топливом — для автопилота и HUD.</summary>
        /// <summary>
        /// Δv оставшихся ступеней: сначала взведённые двигатели с топливом и запусками (заработают по газу,
        /// даже если сейчас заглушены — пассивный участок перед посадкой), затем последовательность.
        /// </summary>
        public List<StageStats> RemainingStats()
        {
            var ready = new bool[Attached.Length];
            for (int i = 0; i < ready.Length; i++)
                ready[i] = Attached[i] && Armed[i] && Propellant[i] > 0 && (Running[i] || IgnitionsLeft[i] > 0);
            return Design.ComputeStats(Attached, Propellant, NextStage, ready);
        }

        public string NextStageLabel
        {
            get
            {
                if (!HasNextStage) return "—";
                var a = Design.Sequence[NextStage];
                var s = Design.Sections[a.Section];
                switch (a.Type)
                {
                    case StageActionType.Ignite: return $"Запуск: {s.Engine.Name}";
                    case StageActionType.Separate: return $"Отделение: {s.Name}";
                    case StageActionType.JettisonFairing: return "Сброс обтекателя";
                    default: return "Парашют";
                }
            }
        }

        /// <summary>Следующая ступень (пробел). Возвращает отделившиеся обломки — их добавит Universe.</summary>
        public List<Vessel> Stage()
        {
            var debris = new List<Vessel>();
            if (!HasNextStage || !Alive) return debris;
            var a = Design.Sequence[NextStage++];
            var secs = Design.Sections;
            switch (a.Type)
            {
                case StageActionType.Ignite:
                    Arm(a.Section);
                    break;

                case StageActionType.Separate:
                {
                    var mask = new bool[secs.Count];
                    bool any = false;
                    for (int i = 0; i <= a.Section; i++)
                        if (Attached[i]) { mask[i] = true; any = true; }
                    if (any)
                    {
                        var d = Split(mask, -1.5);
                        debris.Add(d);
                        Raise($"Отделение: {secs[a.Section].Name}");
                    }
                    int next = a.Section + 1;
                    if (a.IgniteNext && next < secs.Count && Attached[next])
                    {
                        if (secs[next].UllageMotors)
                        {
                            // Двигатели осадки работают ~3 с — успевает и разделение, и запуск.
                            SettledTimer = Math.Max(SettledTimer, 3);
                        }
                        Arm(next);
                    }
                    break;
                }

                case StageActionType.JettisonFairing:
                    if (Attached[a.Section])
                    {
                        var mask = new bool[secs.Count];
                        mask[a.Section] = true;
                        var h = Split(mask, FairingPush);
                        debris.Add(h);
                        debris.Add(h.SplitFairing());
                        Raise("Сброс головного обтекателя");
                    }
                    break;

                case StageActionType.DeployParachute:
                    // Ступень только взводит: раскрытие — по барометру (FlightPhysics.UpdateChutes), как у «Востока».
                    if (Attached[a.Section] && !ChuteDeployed[a.Section] && !ChuteArmed[a.Section])
                    {
                        ChuteArmed[a.Section] = true;
                        Raise($"Парашют взведён: раскроется ниже {FlightPhysics.ChuteDeployAltitude / 1000:F0} км");
                    }
                    break;
            }
            UpdateEngines();
            return debris;
        }

        void Arm(int i)
        {
            if (!Attached[i] || !Design.Sections[i].HasEngine) return;
            Armed[i] = true;
            ignitionLatch[i] = false;
        }

        /// <summary>
        /// Сброс обтекателя (§5): толчок вперёд, м/с. Он меньше, чем ракета набирает за долю секунды, поэтому
        /// цельная оболочка оказалась бы пролётной — ракета проходила сквозь неё. Отсюда раскрытие на створки.
        /// </summary>
        const double FairingPush = 0.5;
        /// <summary>
        /// Створки расходятся вбок FairingSideSpeed, м/с, и откидываются верхом наружу FairingTumble, рад/с.
        /// Пара: низ створки идёт наружу со скоростью Side − Tumble·L/2 (L = 13 м → 2 − 0,2·6,5 = 0,7 м/с) — должна
        /// остаться > 0, иначе низ створки качнётся внутрь, на ступень.
        /// </summary>
        const double FairingSideSpeed = 2, FairingTumble = 0.2;

        /// <summary>
        /// Раскрыть сброшенный обтекатель на две створки: этот борт становится створкой +X, возвращается створка −X.
        /// Импульс сохраняется: боковые толчки равны и противоположны, масса делится пополам.
        /// </summary>
        Vessel SplitFairing()
        {
            var t = new Vessel(Design, Name)
            {
                IsDebris = true,
                Body = Body,
                Position = Position,
                Velocity = Velocity,
                Attitude = Attitude,
                AngularVelocity = AngularVelocity,
                Situation = Situation,
                AnchorBodyFixed = AnchorBodyFixed,
                AttitudeBodyFixed = AttitudeBodyFixed,
                Throttle = 0,
                Sas = SasMode.Off,
                LaunchTime = LaunchTime,
                NextStage = NextStage,
            };
            Array.Copy(Attached, t.Attached, Attached.Length);
            Array.Copy(Propellant, t.Propellant, Propellant.Length);
            FairingHalf = 1;
            t.FairingHalf = -1;
            Name = t.Name = "Створка обтекателя";
            var side = LocalToWorld(new Vector3d(1, 0, 0));
            Velocity += side * FairingSideSpeed;
            t.Velocity -= side * FairingSideSpeed;
            // ω × (0, h, 0) по Z даёт −ω·h по X: створке +X нужен ω_z < 0, чтобы верх уходил наружу.
            AngularVelocity += new Vector3d(0, 0, -FairingTumble);
            t.AngularVelocity += new Vector3d(0, 0, FairingTumble);
            return t;
        }

        /// <summary>
        /// Отделить секции по маске в новый корабль-обломок, толкнув его вдоль оси на dv, м/с (§5).
        /// Position борта — его центр масс, а вид рисует секции вокруг своего ЦМ. Поэтому обе части после
        /// разделения встают каждая на свой новый ЦМ, а не на общий старый — иначе меши налезали друг на друга
        /// (первая ступень оказывалась внутри второй, 01.10.2026). Толчок — с отдачей: импульс сохраняется.
        /// </summary>
        Vessel Split(bool[] mask, double dv)
        {
            MassProperties(out _, out double com0, out _, out _);
            var base0 = (double[])layoutBuf.Clone();
            var d = new Vessel(Design, $"{Name} — обломок")
            {
                IsDebris = true,
                Body = Body,
                Position = Position,
                Velocity = Velocity,
                Attitude = Attitude,
                AngularVelocity = AngularVelocity,
                Situation = Situation,
                AnchorBodyFixed = AnchorBodyFixed,
                AttitudeBodyFixed = AttitudeBodyFixed,
                Throttle = 0,
                Sas = SasMode.Off,
                LaunchTime = LaunchTime,
            };
            for (int i = 0; i < mask.Length; i++)
            {
                d.Attached[i] = mask[i];
                d.Propellant[i] = Propellant[i];
                d.IgnitionsLeft[i] = IgnitionsLeft[i];
                d.ChuteDeployed[i] = ChuteDeployed[i];
                d.ChuteFailed[i] = ChuteFailed[i];
                d.ChuteArmed[i] = ChuteArmed[i];
                d.ChuteOpenTime[i] = ChuteOpenTime[i];
                if (mask[i])
                {
                    Attached[i] = false;
                    Running[i] = false;
                }
            }
            d.NextStage = Design.Sequence.Count;
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) { d.Name = $"{Design.Sections[i].Name} (обломок)"; break; }
            // Сдвиг ЦМ частей в связанных осях. Раскладка каждой части — жёсткий сдвиг старой (Layout считает
            // от нижней присоединённой секции), поэтому низ части в старых координатах — base0[нижней секции].
            var w = AngularVelocity;
            void Recenter(Vessel p)
            {
                int low = Array.IndexOf(p.Attached, true);
                if (low < 0) return;
                p.MassProperties(out _, out double com, out _, out _);
                var r = new Vector3d(0, base0[low] + com - com0, 0);
                p.Position += LocalToWorld(r);
                // Точка жёсткого тела на плече r летит со скоростью v + ω × r.
                p.Velocity += LocalToWorld(Vector3d.Cross(w, r));
            }
            Recenter(d);
            Recenter(this);
            d.MassProperties(out double md, out _, out _, out _);
            MassProperties(out double mv, out _, out _, out _);
            d.Velocity += NoseP * dv;
            if (mv > 0) Velocity -= NoseP * (dv * md / mv);
            SasHoldValid = false;
            return d;
        }

        // ---------------------------------------------------------------- оси

        /// <summary>Направление носа в системе P.</summary>
        public Vector3d NoseP => (Attitude * Vector3d.up).SwapYZ;

        public Vector3d WorldToLocal(Vector3d vP) => Attitude.Inverse * vP.SwapYZ;
        public Vector3d LocalToWorld(Vector3d local) => (Attitude * local).SwapYZ;

        public void Destroy(string reason)
        {
            if (!Alive) return;
            Situation = Situation.Destroyed;
            DestroyReason = reason;
            DestroyedOn = Body;
            for (int i = 0; i < Running.Length; i++) Running[i] = false;
            Raise(reason);
        }
    }
}
