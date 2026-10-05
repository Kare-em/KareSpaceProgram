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
        /// <summary>Нос на цель / от цели (Vessel.Target) — ручное сближение (§6.6).</summary>
        Target,
        AntiTarget,
        /// <summary>Соосно стыковочному узлу цели: нос против её носа — так причаливают руками.</summary>
        DockAlign,
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
        /// <summary>Обломок радиальной группы: азимут его копии в пакете, рад. Вид поворачивает блок на этот угол, чтобы
        /// косой конус бокового Р-7 и после отделения лежал носком к бывшей оси пакета. Физики не касается.</summary>
        public double RadialYaw;

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
        /// <summary>
        /// Высота ввода парашюта секции по барометру, м (GDD §4.8). Из SectionDef.ChuteAltitude, правится окном детали;
        /// писать только через SetChuteAltitude — он держит безопасный диапазон FlightPhysics.ChuteAltitudeMin..Max.
        /// </summary>
        public double[] ChuteAltitude;
        /// <summary>Попытка запуска уже была при этой «подаче газа»: повтор — только после сброса РУД в 0.</summary>
        bool[] ignitionLatch;
        public int NextStage;
        /// <summary>
        /// Порядок секций снизу вверх. У нового борта — по индексам; после стыковки секции второго борта идут сверху
        /// в обратном порядке (носом к носу, §6.6). Раскладка, верх и низ связки считаются по нему, а не по индексам.
        /// </summary>
        public int[] Order;
        /// <summary>Секция перевёрнута: пристыкована носом к носу. Двигатель её не взводится, рендер разворачивает её на 180°.</summary>
        public bool[] Flipped;
        /// <summary>Раскладное секции (SectionDef.Deploy, §6.12): сколько раскрыто, 0…1 (вид поворачивает опоры и трапы
        /// на шарнирах), и куда идёт привод — FlightPhysics.StepDeploy.</summary>
        public double[] Deployed;
        public bool[] DeployOn;

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
        /// <summary>Подвеска опор и колёс (FlightPhysics.StepSuspension): сдвиг корпуса по местной вертикали от точки
        /// AnchorBodyFixed, м (минус — просел), и его скорость, м/с. В полёте — нули.</summary>
        public double Suspension, SuspensionRate;
        /// <summary>Сглаженная нормаль грунта под колёсами лунохода в осях тела; ноль — ещё не задана (берётся радиус).</summary>
        public Vector3d GroundUp;
        /// <summary>Съезд с посадочной ступени по трапам (FlightPhysics.RampAbove): высота настила над грунтом, м
        /// (0 — уже на грунте), и пройденный путь по оси трапов от центра ступени, м (знак — в какую пару трапов).</summary>
        public double RampDeck, RampTravel;
        /// <summary>Настил ступени в осях тела: центр (низ лунохода при отделении), ось трапов и вертикаль ступени.
        /// Трапы — жёсткая деталь ступени: стоит она с креном — с тем же креном лежат и рельсы.</summary>
        public Vector3d RampOrigin, RampAxis, RampUp;
        public KeplerOrbit Orbit;
        public bool OnRails;
        public string DestroyReason;
        public CelestialBody DestroyedOn;
        /// <summary>Место гибели в осях тела (вращается вместе с ним); NaN — ещё не закреплено. См. Universe.PinWreck.</summary>
        public Vector3d WreckLocal = new Vector3d(double.NaN, 0, 0);
        public double LaunchTime = double.NaN;

        // Управление.
        public double Throttle = 1;
        /// <summary>Ручки пилота: x — тангаж (W +1), y — рысканье (D +1), z — крен (E +1).</summary>
        public Vector3d PilotInput;
        /// <summary>
        /// Момент рулей при полном отклонении, Н·м, по осям MaxTorque (x — рыскание, y — крен, z — тангаж): считает
        /// FlightPhysics.StepFlying по напору и Маху (Aerodynamics.ControlAuthority), на грунте и в пустоте — ноль (§4.6).
        /// </summary>
        public Vector3d AeroControlTorque;
        /// <summary>Щиток-тормоз 0…1 (руль направления «Шаттла» раскрывается веером): ставит автопилот посадки или пилот.</summary>
        public double AirBrake;
        /// <summary>Пробег по полосе на шасси, м/с вдоль курса (FlightPhysics.StepRollout); 0 — стоит или не на шасси.</summary>
        public double RollSpeed;
        /// <summary>Курс пробега в осях тела (единичный, касательный к грунту).</summary>
        public Vector3d RollDir;
        /// <summary>Последнее касание на шасси: снижение и путевая, м/с (NaN — не было). Для итога миссии и тестов.</summary>
        public double TouchdownSink = double.NaN, TouchdownSpeed = double.NaN;
        public SasMode Sas = SasMode.Stability;
        public QuaternionD SasHold;
        public bool SasHoldValid;
        /// <summary>Желаемый момент в связанных осях, Н·м — выход регулятора, физика обрезает по возможностям.</summary>
        public Vector3d TorqueCommand;
        /// <summary>Поступательная РСУ вдоль носа (0…1): осаживает топливо перед повторным запуском (GDD §6.3).</summary>
        public double RcsForward;
        /// <summary>Поступательная РСУ в связанных осях, −1…1 по каждой оси: сближение и причаливание (§6.6).</summary>
        public Vector3d RcsTranslate;
        /// <summary>Цель сближения и стыковки (§6.6): для SAS Target/DockAlign и прибора стыковки HUD.</summary>
        public Vessel Target;
        /// <summary>Расстыковка с переходом экипажа (ЛМ «Аполлона»): Universe делает этот борт активным и обнуляет поле.</summary>
        public Vessel ControlTransfer;
        /// <summary>Перезапуски двигателей без счёта (настройка меню Esc, §6.3). В ядре по умолчанию — честный счёт: тесты миссий.</summary>
        public static bool UnlimitedIgnitions;
        public ManeuverNode Node;

        // Телеметрия (обновляет физика).
        public double Altitude, TerrainAltitude, SurfaceSpeed, VerticalSpeed, HorizontalSpeed;
        public double StaticPressure, Density, DynamicPressure, Mach, AngleOfAttack, HeatFlux, GForce;
        public double SettledTimer, OverheatTimer;
        /// <summary>Секунды сверх предела перегрузки экипажа (§4.7) и итог: экипаж погиб, борт летит дальше.</summary>
        public double HighGTimer;
        public bool CrewLost;
        public double CurrentThrust;
        /// <summary>До этого момента борт не сталкивается: только что разделённые части стоят вплотную (Universe.CollisionGrace).</summary>
        public double NoCollideUntil = double.NegativeInfinity;

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
            ChuteAltitude = new double[n];
            for (int i = 0; i < n; i++) ChuteAltitude[i] = FlightPhysics.ClampChuteAltitude(design.Sections[i].ChuteAltitude);
            ignitionLatch = new bool[n];
            Order = new int[n];
            Flipped = new bool[n];
            Deployed = new double[n];
            DeployOn = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var s = design.Sections[i];
                Order[i] = i;
                Attached[i] = true;
                Propellant[i] = s.Propellant;
                IgnitionsLeft[i] = s.HasEngine ? s.Engine.Ignitions : 0;
            }
        }

        public bool Alive => Situation != Situation.Destroyed;
        /// <summary>Пройдено самоходным шасси по грунту, м (цель миссии «Проехать», ObjectiveType.Drive).</summary>
        public double DriveDistance;
        /// <summary>Путь колёс левого и правого борта со знаком, м (FlightPhysics.DriveRover): на развороте борта
        /// катятся навстречу друг другу. Вид крутит по нему колёса (VesselView, угол = путь / RoverWheelRadius).</summary>
        public double WheelPathLeft, WheelPathRight;

        /// <summary>Самоходное шасси стало нижней секцией: съехало с посадочной ступени и ездит (FlightPhysics.StepLanded).</summary>
        public bool IsRover
        {
            get
            {
                int b = BottomSection();
                return !IsDebris && b >= 0 && Design.Sections[b].Rover;
            }
        }
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
            // Радиальные блоки снаружи пакета: обтекатель их не закрывает (конструктор ставит его выше).
            if (secs[i].IsRadial) return -1;
            for (int f = i + 1; f < secs.Count; f++)
                if (Attached[f] && secs[f].Kind == SectionKind.Fairing && f - secs[f].EnclosesBelow <= i)
                    return f;
            return -1;
        }

        public bool IsEnclosed(int i) => EnclosingFairing(i) >= 0;

        /// <summary>Чит «бесконечное топливо» (меню Esc): баки присоединённых секций полны, запуски не меньше штатных.</summary>
        public void Refuel()
        {
            var secs = Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Attached[i]) continue;
                Propellant[i] = secs[i].Propellant;
                if (secs[i].HasEngine) IgnitionsLeft[i] = Math.Max(IgnitionsLeft[i], secs[i].Engine.Ignitions);
            }
        }

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
            double h = 0, coreBase = 0;
            foreach (int i in Order)
            {
                if (!Attached[i] || secs[i].IsRadial) continue;
                var s = secs[i];
                if (s.Beside)
                {
                    // Орбитер на баке: низ — у низа секции ядра под ним, длину пакета не наращивает (выше бака не торчит).
                    baseHeight[i] = coreBase;
                    h = Math.Max(h, coreBase + s.Length);
                    continue;
                }
                coreBase = h;
                if (s.Kind == SectionKind.Fairing)
                {
                    double b = h;
                    for (int j = i - s.EnclosesBelow; j < i; j++)
                        if (j >= 0 && Attached[j] && !secs[j].IsRadial) { b = baseHeight[j]; break; }
                    baseHeight[i] = b;
                    h = Math.Max(h, b + s.Length);
                }
                else
                {
                    baseHeight[i] = h;
                    h += s.Length;
                }
            }
            // Второй проход: радиальные блоки встают от низа родителя и длину пакета не добавляют (если не длиннее его).
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Attached[i] || !secs[i].IsRadial) continue;
                int p = secs[i].RadialParent;
                baseHeight[i] = (Attached[p] ? baseHeight[p] : 0) + secs[i].RadialLift;
                h = Math.Max(h, baseHeight[i] + secs[i].Length);
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
                moment += m * (layoutBuf[i] + secs[i].Length * secs[i].ComFraction);
                maxRadius = Math.Max(maxRadius, secs[i].Radius + secs[i].RadialOffset);
            }
            comHeight = mass > 0 ? moment / mass : 0;
        }

        /// <summary>Главные моменты инерции в связанных осях (X, Y — продольная, Z). Стержень + цилиндр.</summary>
        public Vector3d Inertia()
        {
            MassProperties(out double m, out _, out double len, out double r);
            double lateral = m * (len * len / 12 + r * r / 4);
            double roll = Math.Max(m * r * r / 2, 1);
            // Крылья разносят массу по размаху: крену и рысканию — не меньше m·(b/2)²/8 (у «Шаттла» ≈1,6e6 кг·м²).
            double span = WingSpan();
            if (span > 0)
            {
                double wing = m * span * span / 32;
                roll = Math.Max(roll, wing);
                lateral = Math.Max(lateral, wing);
            }
            return new Vector3d(lateral, roll, lateral);
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
                // Крен качанием возможен только у связки из нескольких камер; у радиальных блоков плечо — их вынос от оси.
                if (s.IsRadial) roll += side * s.RadialOffset;
                else if (s.EngineCount > 1) roll += side * s.Radius * 0.5;
            }
            // Рули (§4.6): бюджет считает физика по напору; бескрылым и на грунте — ноль.
            return new Vector3d(pitch + AeroControlTorque.x, roll + AeroControlTorque.y, pitch + AeroControlTorque.z);
        }

        /// <summary>Наибольший размах горизонтальных крыльев присоединённых секций, м; 0 — бескрылый.</summary>
        public double WingSpan()
        {
            double b = 0;
            var secs = Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Attached[i] || secs[i].Wings == null) continue;
                foreach (var w in secs[i].Wings) if (!w.Vertical) b = Math.Max(b, w.Span);
            }
            return b;
        }

        /// <summary>Шасси выпущено (DeployKind.Gear раскрыто): касание полосы — пробег, а не посадка на опоры.</summary>
        public bool GearDown
        {
            get
            {
                var secs = Design.Sections;
                for (int i = 0; i < secs.Count; i++)
                    if (Attached[i] && secs[i].Deploy == DeployKind.Gear && Deployed[i] >= 0.999) return true;
                return false;
            }
        }

        /// <summary>Высота шасси, м (наибольшая у присоединённых секций с Gear).</summary>
        public double GearHeight
        {
            get
            {
                double g = 0;
                var secs = Design.Sections;
                for (int i = 0; i < secs.Count; i++)
                    if (Attached[i] && secs[i].Deploy == DeployKind.Gear) g = Math.Max(g, secs[i].GearHeight);
                return g;
            }
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

        /// <summary>Верхняя присоединённая секция по порядку связки (после стыковки — секция второго борта). Радиальные блоки — не торцы.</summary>
        public int TopSection()
        {
            for (int k = Order.Length - 1; k >= 0; k--)
                if (Attached[Order[k]] && !Design.Sections[Order[k]].IsRadial) return Order[k];
            return -1;
        }

        public int BottomSection()
        {
            for (int k = 0; k < Order.Length; k++)
                if (Attached[Order[k]] && !Design.Sections[Order[k]].IsRadial) return Order[k];
            return -1;
        }

        /// <summary>Радиус корпуса пакета без радиальных блоков, м — для столкновений (Universe.Collide).</summary>
        public double HullRadius()
        {
            double r = 0.1;
            for (int i = 0; i < Attached.Length; i++)
                if (Attached[i] && !Design.Sections[i].IsRadial) r = Math.Max(r, Design.Sections[i].Radius);
            return r;
        }

        /// <summary>Есть стыковочный узел (свободный или занятый): такие пары не сталкиваются — их сводит стыковка.</summary>
        public bool HasPort()
        {
            for (int i = 0; i < Attached.Length; i++)
                if (Attached[i] && Design.Sections[i].DockingPort) return true;
            return false;
        }

        /// <summary>Стыковочный узел — верхний торец связки: его высота над центром масс, м (§6.6).</summary>
        public double PortHeight()
        {
            MassProperties(out _, out double com, out double length, out _);
            return length - com;
        }

        /// <summary>Узел свободен: верхняя секция связки несёт порт и не перевёрнута (у перевёрнутой порт смотрит вниз — занят).</summary>
        public bool HasFreePort
        {
            get
            {
                int t = TopSection();
                return t >= 0 && Design.Sections[t].DockingPort && !Flipped[t];
            }
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
            var e = Design.Sections[i].Engine;
            // РДТТ не дросселируется: горит на полной до выработки.
            if (e.Solid) return 1;
            return Math.Max(Throttle, e.MinThrottle);
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
                // Твердотопливный РУДа не слушает: взведён — горит до выработки.
                if (Throttle <= 0 && !secs[i].Engine.Solid)
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
                if (!CanIgnite(i))
                {
                    Raise($"{secs[i].Engine.Name}: запуски исчерпаны");
                    continue;
                }
                if (!UnlimitedIgnitions) IgnitionsLeft[i]--;
                string left = UnlimitedIgnitions ? "∞" : IgnitionsLeft[i].ToString();
                if (secs[i].Engine.NeedsUllage && !PropellantSettled)
                {
                    Raise($"{secs[i].Engine.Name}: запуск сорван — топливо не осело (осталось запусков: {left})");
                    continue;
                }
                Running[i] = true;
                Raise($"{secs[i].Engine.Name}: запуск (осталось запусков: {left})");
            }
        }

        /// <summary>Остался ли запуск у двигателя секции (с настройкой «бесконечные перезапуски» — всегда).</summary>
        public bool CanIgnite(int i) => UnlimitedIgnitions || IgnitionsLeft[i] > 0;

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

        /// <summary>
        /// Первый шаг программы начиная с from, который сейчас выполним. После стыковки и расстыковки состав связки
        /// меняется (§6.6): шаги, чьих секций в ней нет, пропускаются, а не съедают нажатие пробела впустую.
        /// </summary>
        public int NextApplicable(int from)
        {
            int k = Math.Max(0, from);
            while (k < Design.Sequence.Count && !Applicable(Design.Sequence[k])) k++;
            return k;
        }

        bool Applicable(StageAction a)
        {
            var secs = Design.Sections;
            int i = a.Section;
            switch (a.Type)
            {
                case StageActionType.Ignite:
                    return Attached[i] && secs[i].HasEngine && !Flipped[i];
                case StageActionType.Separate:
                {
                    if (secs[i].IsRadial) return Attached[i];
                    bool below = false, above = false;
                    for (int j = 0; j < secs.Count; j++)
                        if (Attached[j]) { if (Design.DetachedBy(j, i)) below = true; else above = true; }
                    if (below && above) return true;
                    int next = Design.NextCore(i);
                    return a.IgniteNext && next < secs.Count && Attached[next] && secs[next].HasEngine && !Armed[next] && !Flipped[next];
                }
                case StageActionType.JettisonFairing:
                    return Attached[i];
                case StageActionType.Undock:
                    for (int j = 0; j < secs.Count; j++)
                        if (Attached[j] && Flipped[j]) return true;
                    return false;
                default:
                    return Attached[i] && !ChuteDeployed[i] && !ChuteArmed[i];
            }
        }

        public bool HasNextStage => NextApplicable(NextStage) < Design.Sequence.Count;

        /// <summary>
        /// Δv оставшихся ступеней: сначала взведённые двигатели с топливом и запусками (заработают по газу,
        /// даже если сейчас заглушены — пассивный участок перед посадкой), затем последовательность.
        /// </summary>
        /// <summary>
        /// Высота ввода парашюта секции (окно детали, §4.8). Зажата в безопасный диапазон: ниже 1 км купол с рифлением
        /// (1 + 4 + 4 с) не успевает погасить скорость, выше 15 км напор и холод — не штатный режим.
        /// </summary>
        public void SetChuteAltitude(int section, double altitude)
        {
            if (section < 0 || section >= ChuteAltitude.Length) return;
            ChuteAltitude[section] = FlightPhysics.ClampChuteAltitude(altitude);
        }

        /// <summary>Самая высокая уставка ввода среди непрораскрытых парашютов на борту; 0 — парашютов нет.</summary>
        public double HighestChuteAltitude()
        {
            double h = 0;
            for (int i = 0; i < Attached.Length; i++)
                if (Attached[i] && !ChuteDeployed[i] && Design.Sections[i].ParachuteArea > 0) h = Math.Max(h, ChuteAltitude[i]);
            return h;
        }

        public List<StageStats> RemainingStats()
        {
            var ready = new bool[Attached.Length];
            for (int i = 0; i < ready.Length; i++)
                ready[i] = Attached[i] && Armed[i] && Propellant[i] > 0 && (Running[i] || CanIgnite(i));
            return Design.ComputeStats(Attached, Propellant, NextStage, ready);
        }

        // ---------------------------------------------------------------- опоры и трапы (§6.12)

        bool DeployDone(Func<DeployKind, bool> kind)
        {
            for (int i = 0; i < Attached.Length; i++)
                if (Attached[i] && kind(Design.Sections[i].Deploy) && Deployed[i] < 1) return false;
            return true;
        }

        static bool IsLegs(DeployKind k) => k == DeployKind.Legs || k == DeployKind.PyroLegs;
        /// <summary>Раскладных опор нет или все выпущены до конца: касание не ломает борт (FlightPhysics.CheckContact).</summary>
        public bool LegsDown => DeployDone(IsLegs);
        /// <summary>Трапов нет или они легли на грунт: луноход может съезжать.</summary>
        public bool RampsDown => DeployDone(k => k == DeployKind.Ramps);

        /// <summary>
        /// Выпустить раскладные опоры (автопилот посадки, §6.12: LM и «Сервейор» садились только на выпущенных).
        /// true — привод запущен сейчас, false — уже выпущены или выпускаются, либо опор нет.
        /// </summary>
        public bool ExtendGear()
        {
            bool started = false;
            for (int i = 0; i < Attached.Length; i++)
            {
                if (!Attached[i] || Design.Sections[i].Deploy != DeployKind.Gear || DeployOn[i]) continue;
                DeployOn[i] = started = true;
            }
            if (started) Raise("Шасси: выпуск");
            return started;
        }

        public bool ExtendLegs()
        {
            bool started = false;
            for (int i = 0; i < Attached.Length; i++)
            {
                if (!Attached[i] || !IsLegs(Design.Sections[i].Deploy) || DeployOn[i]) continue;
                DeployOn[i] = started = true;
            }
            if (started) Raise("Опоры: выпуск");
            return started;
        }

        /// <summary>
        /// Клавиша G (§6.12): трапы на грунте откидываются (обратно не складываются — механика «Луны-17» одноразовая),
        /// опоры выпускаются; повтор убирает только опоры на приводе (Legs) и только в полёте — на грунте борт стоит на них.
        /// Опоры на пирозамках (LM, «Сервейор») не убираются. После трапов G открывает и закрывает крышку лунохода —
        /// только на грунте. Возвращает строку для ленты или null.
        /// </summary>
        public string ToggleDeploy()
        {
            bool legs = false, legsOut = true, pyro = false, ramps = false, rampsOn = true, lid = false, lidOpen = true;
            bool gear = false, gearOut = true;
            for (int i = 0; i < Attached.Length; i++)
            {
                if (!Attached[i]) continue;
                switch (Design.Sections[i].Deploy)
                {
                    case DeployKind.Legs: legs = true; legsOut &= DeployOn[i]; break;
                    case DeployKind.PyroLegs: legs = pyro = true; legsOut &= DeployOn[i]; break;
                    case DeployKind.Ramps: ramps = true; rampsOn &= DeployOn[i]; break;
                    case DeployKind.Lid: lid = true; lidOpen &= DeployOn[i]; break;
                    case DeployKind.Gear: gear = true; gearOut &= DeployOn[i]; break;
                }
            }
            if (gear)
            {
                // Шасси орбитера (§4.6): выпуск в любой момент, уборка — только в полёте (на пробеге борт стоит на нём).
                if (gearOut && IsLanded) return "Шасси на грунте не убирается";
                for (int i = 0; i < Attached.Length; i++)
                    if (Attached[i] && Design.Sections[i].Deploy == DeployKind.Gear) DeployOn[i] = !gearOut;
                return gearOut ? "Шасси: уборка" : "Шасси: выпуск";
            }
            if (ramps && !rampsOn)
            {
                if (!IsLanded) return "Трапы откидываются только на грунте";
                for (int i = 0; i < Attached.Length; i++)
                    if (Attached[i] && Design.Sections[i].Deploy == DeployKind.Ramps) DeployOn[i] = true;
                return "Трапы: раскладка";
            }
            if (lid)
            {
                if (!IsLanded) return "Крышку открывают только на грунте";
                for (int i = 0; i < Attached.Length; i++)
                    if (Attached[i] && Design.Sections[i].Deploy == DeployKind.Lid) DeployOn[i] = !lidOpen;
                return lidOpen ? "Крышка: закрытие" : "Крышка: открытие";
            }
            if (!legs) return ramps ? "Трапы уже разложены" : "Раскладывать нечего";
            if (!legsOut) { ExtendLegs(); return null; }
            if (pyro) return "Опоры на пирозамках — не убираются";
            if (IsLanded) return "На грунте опоры не убрать";
            for (int i = 0; i < Attached.Length; i++)
                if (Attached[i] && Design.Sections[i].Deploy == DeployKind.Legs) DeployOn[i] = false;
            return "Опоры: уборка";
        }

        /// <summary>Почему пробел сейчас не сработает: сброс посадочной ступени с луноходом при сложенных трапах —
        /// луноход встал бы на настил без съезда. null — можно.</summary>
        public string StageBlock
        {
            get
            {
                int k = NextApplicable(NextStage);
                if (k >= Design.Sequence.Count || !IsLanded) return null;
                var a = Design.Sequence[k];
                if (a.Type != StageActionType.Separate || Design.Sections[a.Section].Deploy != DeployKind.Ramps) return null;
                return Deployed[a.Section] < 1 ? "Сначала трапы: G" : null;
            }
        }

        public string NextStageLabel
        {
            get
            {
                int k = NextApplicable(NextStage);
                if (k >= Design.Sequence.Count) return "—";
                var a = Design.Sequence[k];
                var s = Design.Sections[a.Section];
                switch (a.Type)
                {
                    case StageActionType.Ignite: return $"Запуск: {s.Engine.Name}";
                    case StageActionType.Separate: return $"Отделение: {s.Name}";
                    case StageActionType.JettisonFairing: return s.JettisonWhole ? $"Сброс: {s.Name}" : "Сброс обтекателя";
                    case StageActionType.Undock: return "Расстыковка";
                    default: return "Парашют";
                }
            }
        }

        /// <summary>Следующая ступень что-то отделяет (не запуск и не парашют) — на напоре это опасно (FlightInput).</summary>
        public bool NextStageSeparates
        {
            get
            {
                int k = NextApplicable(NextStage);
                if (k >= Design.Sequence.Count) return false;
                var t = Design.Sequence[k].Type;
                return t == StageActionType.Separate || t == StageActionType.JettisonFairing || t == StageActionType.Undock;
            }
        }

        /// <summary>Есть причаленное сверху (перевёрнутые секции) — его можно отстыковать (V).</summary>
        public bool IsDocked
        {
            get
            {
                for (int i = 0; i < Attached.Length; i++)
                    if (Attached[i] && Flipped[i]) return true;
                return false;
            }
        }

        /// <summary>
        /// Ручная расстыковка (V, §6.6): причаленное сверху уходит отдельным бортом на пружинах UndockPush. Это не
        /// обломок — у него свой SAS, его можно снова взять целью и причалить. Null — отстыковывать нечего.
        /// </summary>
        public Vessel Undock()
        {
            if (!IsDocked) return null;
            var mask = new bool[Design.Sections.Count];
            double m = 0, md = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = Attached[i] && Flipped[i];
                if (!Attached[i]) continue;
                m += SectionMass(i);
                if (mask[i]) md += SectionMass(i);
            }
            // Split толкает отходящую часть на dv, а остаток получает отдачу dv·md/mv — расхождение dv·m/mv. Пружины
            // узла дают расхождение UndockPush при любом раскладе масс (замер: полная S-IVB с ЛМ 120 т от КСМ 30 т без
            // поправки расходилась на 1,6 м/с вместо 0,3).
            double dv = m > md ? UndockPush * (m - md) / m : UndockPush;
            var d = Split(mask, dv, debris: false);
            d.Unflip();
            Raise($"Расстыковка: {d.Name}");
            return d;
        }

        /// <summary>
        /// Следующая ступень (пробел). Возвращает отделившиеся борта — их добавит Universe. Шаги с WithPrevious идут тем же
        /// нажатием (группа ступени конструктора: ядро и ускорители, отделение и запуск).
        /// </summary>
        public List<Vessel> Stage()
        {
            var debris = new List<Vessel>();
            if (!Alive) return debris;
            NextStage = NextApplicable(NextStage);
            if (NextStage >= Design.Sequence.Count) return debris;
            var seq = Design.Sequence;
            Execute(seq[NextStage++], debris);
            while (NextStage < seq.Count && seq[NextStage].WithPrevious)
            {
                var a = seq[NextStage++];
                if (Applicable(a)) Execute(a, debris);
            }
            UpdateEngines();
            return debris;
        }

        void Execute(StageAction a, List<Vessel> debris)
        {
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
                    if (secs[a.Section].IsRadial)
                    {
                        if (Attached[a.Section])
                        {
                            debris.AddRange(SplitRadial(a.Section));
                            Raise($"Отделение: {secs[a.Section].Name}");
                        }
                    }
                    else if (secs[a.Section].Kind == SectionKind.Fairing)
                    {
                        // Переходник (SLA «Аполлона», §6.6): ниже — ступень с грузом. Груз со стыковочным узлом (ЛМ) — не обломок:
                        // к нему ещё причаливают. Сам переходник раскрывается створками, как обтекатель.
                        bool port = false;
                        for (int i = 0; i < a.Section; i++)
                            if (Attached[i]) { mask[i] = true; any = true; port |= secs[i].DockingPort; }
                        if (any) debris.Add(Split(mask, AdapterPush, debris: !port));
                        if (Attached[a.Section])
                        {
                            var fm = new bool[secs.Count];
                            fm[a.Section] = true;
                            var h = Split(fm, FairingPush);
                            debris.Add(h);
                            debris.Add(h.SplitFairing());
                            h.Name = debris[debris.Count - 1].Name = secs[a.Section].Name;
                        }
                        Raise($"Отделение: {secs[a.Section].Name}");
                    }
                    else
                    {
                        for (int i = 0; i < secs.Count; i++)
                            if (Attached[i] && Design.DetachedBy(i, a.Section)) { mask[i] = true; any = true; }
                        if (any)
                        {
                            double push = secs[a.Section].DecouplerPush > 0 ? secs[a.Section].DecouplerPush : StagePush;
                            debris.Add(Split(mask, push));
                            Raise($"Отделение: {secs[a.Section].Name}");
                        }
                    }
                    if (a.IgniteNext) ArmNext(Design.NextCore(a.Section));
                    break;
                }

                case StageActionType.JettisonFairing:
                    if (Attached[a.Section])
                    {
                        var mask = new bool[secs.Count];
                        mask[a.Section] = true;
                        if (secs[a.Section].JettisonWhole)
                        {
                            // САС уходит целиком на своём РДТТ увода: толчок не от корабля, отдачи нет. Сопла скошены —
                            // башня уходит вбок и не проходит над кораблём (§6.6).
                            var d = Split(mask, 0);
                            d.Velocity += NoseP * LesPush + LocalToWorld(new Vector3d(1, 0, 0)) * LesSide;
                            d.Name = secs[a.Section].Name;
                            debris.Add(d);
                            Raise($"Сброс: {secs[a.Section].Name}");
                        }
                        else
                        {
                            var h = Split(mask, FairingPush);
                            debris.Add(h);
                            debris.Add(h.SplitFairing());
                            Raise("Сброс головного обтекателя");
                        }
                    }
                    break;

                case StageActionType.Undock:
                {
                    var mask = new bool[secs.Count];
                    for (int i = 0; i < secs.Count; i++) mask[i] = Attached[i] && Flipped[i];
                    var d = Split(mask, UndockPush, debris: !a.TransferControl);
                    d.Unflip();
                    if (a.TransferControl) ControlTransfer = d;
                    debris.Add(d);
                    Raise($"Расстыковка: {d.Name}");
                    break;
                }

                case StageActionType.DeployParachute:
                    // Ступень только взводит: раскрытие — по барометру (FlightPhysics.UpdateChutes), как у «Востока».
                    if (Attached[a.Section] && !ChuteDeployed[a.Section] && !ChuteArmed[a.Section])
                    {
                        ChuteArmed[a.Section] = true;
                        Raise($"Парашют взведён: раскроется ниже {ChuteAltitude[a.Section] / 1000:0.#} км");
                    }
                    break;
            }
        }

        void Arm(int i)
        {
            if (!Attached[i] || Flipped[i] || !Design.Sections[i].HasEngine) return;
            Armed[i] = true;
            ignitionLatch[i] = false;
        }

        /// <summary>Взвести новую нижнюю ступень после отделения; двигатели осадки работают ~3 с — успевает и разделение, и запуск.</summary>
        void ArmNext(int next)
        {
            var secs = Design.Sections;
            if (next >= secs.Count || !Attached[next] || Flipped[next]) return;
            if (secs[next].UllageMotors) SettledTimer = Math.Max(SettledTimer, 3);
            Arm(next);
        }

        /// <summary>Толчок пиропушителей при разделении ступеней, м/с (§5): обломок уходит назад, с отдачей.</summary>
        const double StagePush = 1.5;
        /// <summary>
        /// Толчки стыковочной программы, м/с (§6.6): ступень с ЛМ от переходника и расстыковка — пружинами, медленно,
        /// чтобы КСМ успел развернуться и причалить. Пара: DockingAutopilot.MaxApproach — сближение не быстрее.
        /// </summary>
        const double AdapterPush = 0.3, UndockPush = 0.3;
        /// <summary>САС «Аполлона»: РДТТ увода даёт ~30 м/с вперёд, скос сопел — ~8 м/с вбок (§6.6).</summary>
        const double LesPush = 30, LesSide = 8;

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
            Array.Copy(Order, t.Order, Order.Length);
            Array.Copy(Flipped, t.Flipped, Flipped.Length);
            Array.Copy(Deployed, t.Deployed, Deployed.Length);
            Array.Copy(DeployOn, t.DeployOn, DeployOn.Length);
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
        /// Направление толчка — от оставшейся части: нижнюю назад, верхнюю (обтекатель, отстыкованный модуль) вперёд.
        /// debris = false — отделяется управляемый борт (ЛМ, ступень с ЛМ): программа и взведённость сохраняются.
        /// </summary>
        Vessel Split(bool[] mask, double dv, bool debris = true)
        {
            MassProperties(out _, out double com0, out _, out _);
            var base0 = (double[])layoutBuf.Clone();
            var d = new Vessel(Design, $"{Name} — обломок")
            {
                IsDebris = debris,
                Body = Body,
                Position = Position,
                Velocity = Velocity,
                Attitude = Attitude,
                AngularVelocity = AngularVelocity,
                Situation = Situation,
                AnchorBodyFixed = AnchorBodyFixed,
                AttitudeBodyFixed = AttitudeBodyFixed,
                Throttle = 0,
                Sas = debris ? SasMode.Off : SasMode.Stability,
                LaunchTime = LaunchTime,
            };
            Array.Copy(Order, d.Order, Order.Length);
            Array.Copy(Flipped, d.Flipped, Flipped.Length);
            Array.Copy(Deployed, d.Deployed, Deployed.Length);
            Array.Copy(DeployOn, d.DeployOn, DeployOn.Length);
            bool burning = false;
            for (int i = 0; i < mask.Length; i++)
            {
                // Отделённая на ходу ступень не глохнет (§5, «разделение по-честному»): работающий двигатель уходит с ней
                // на прежнем газе и жжёт до выработки — догоняет и бьёт верхнюю ступень, если пилот поспешил.
                bool run = mask[i] && Running[i];
                burning |= run;
                d.Running[i] = run;
                d.ignitionLatch[i] = run;
                d.Armed[i] = mask[i] && Armed[i] && (!debris || run);
                d.Attached[i] = mask[i];
                d.Propellant[i] = Propellant[i];
                d.IgnitionsLeft[i] = IgnitionsLeft[i];
                d.ChuteDeployed[i] = ChuteDeployed[i];
                d.ChuteFailed[i] = ChuteFailed[i];
                d.ChuteArmed[i] = ChuteArmed[i];
                d.ChuteOpenTime[i] = ChuteOpenTime[i];
                d.ChuteAltitude[i] = ChuteAltitude[i];
                if (mask[i])
                {
                    Attached[i] = false;
                    Running[i] = false;
                }
            }
            if (burning) d.Throttle = Throttle;
            d.NextStage = debris ? Design.Sequence.Count : NextStage;
            if (debris)
            {
                for (int i = 0; i < mask.Length; i++)
                    if (mask[i]) { d.Name = $"{Design.Sections[i].Name} (обломок)"; break; }
            }
            else d.Name = d.CraftName();
            // Сдвиг ЦМ частей в связанных осях. Раскладка каждой части — жёсткий сдвиг старой (Layout считает
            // от нижней присоединённой секции), поэтому низ части в старых координатах — base0[нижней секции].
            var w = AngularVelocity;
            double Recenter(Vessel p)
            {
                int low = p.BottomSection();
                if (low < 0) return 0;
                p.MassProperties(out _, out double com, out _, out _);
                var r = new Vector3d(0, base0[low] + com - com0, 0);
                p.Position += LocalToWorld(r);
                // На грунте положение задаёт якорь в осях тела: без сдвига якоря часть, оставшаяся стоять
                // (луноход, съехавший с посадочной ступени), прыгнула бы обратно на старый общий ЦМ.
                if (p.IsLanded) p.AnchorBodyFixed += (p.AttitudeBodyFixed * r).SwapYZ;
                // Луноход стоит на настиле ступени: дальше он не прыгает на грунт, а съезжает по трапам.
                if (p.IsLanded && p.IsRover)
                {
                    double R = p.AnchorBodyFixed.magnitude;
                    p.MassProperties(out _, out double pc, out _, out _);
                    double h = R - (p.Body.Radius + p.Body.SurfaceHeight(p.AnchorBodyFixed / R) + pc);
                    p.RampDeck = h > FlightPhysics.RampMinDeck ? h : 0;
                    p.RampTravel = 0;
                    p.RampUp = (p.AttitudeBodyFixed * Vector3d.up).SwapYZ.normalized;
                    p.RampAxis = (p.AttitudeBodyFixed * Vector3d.forward).SwapYZ.normalized;
                    p.RampOrigin = p.AnchorBodyFixed - p.RampUp * pc;
                    p.GroundUp = p.RampUp;
                }
                // Точка жёсткого тела на плече r летит со скоростью v + ω × r.
                p.Velocity += LocalToWorld(Vector3d.Cross(w, r));
                return r.y;
            }
            double rd = Recenter(d), rv = Recenter(this);
            d.MassProperties(out double md, out _, out _, out _);
            MassProperties(out double mv, out _, out _, out _);
            if (rd < rv) dv = -dv;
            d.Velocity += NoseP * dv;
            if (mv > 0) Velocity -= NoseP * (dv * md / mv);
            SasHoldValid = false;
            return d;
        }

        /// <summary>
        /// Толчок радиальных разделителей, м/с, и закрутка блока верхом наружу, рад/с (§5, как сброс ускорителей «Союза»
        /// и KSP). Пара: низ блока уходит наружу со скоростью Push − Tumble·L/2 — должна остаться > 0 (L = 20 м → 2 − 0,1·10 = 1),
        /// иначе низ блока качнётся внутрь, на ядро.
        /// </summary>
        public const double RadialPush = 2, RadialTumble = 0.1;

        /// <summary>
        /// Отделить радиальную группу j: каждый из N блоков — свой борт-обломок на своём месте вокруг оси, со скоростью
        /// точки жёсткого тела (v + ω × r), толчком наружу и закруткой «вершина наружу». Толчки симметричны — суммарный
        /// импульс сохраняется без отдачи ядру; ядро только встаёт на свой новый ЦМ.
        /// </summary>
        List<Vessel> SplitRadial(int j)
        {
            var secs = Design.Sections;
            var s = secs[j];
            int n = s.RadialCount;
            MassProperties(out _, out double com0, out _, out _);
            double mid = layoutBuf[j] + s.Length * 0.5 - com0;
            var piece = Design.RadialPiece(j);
            double push = s.DecouplerPush > 0 ? s.DecouplerPush : RadialPush;
            var w = AngularVelocity;
            var res = new List<Vessel>();
            for (int c = 0; c < n; c++)
            {
                double ang = 2 * Math.PI * c / n + s.RadialPhase;
                // Блок c стоит по направлению (cos, 0, sin) в связанных осях — так же его рисует VesselView.
                var dir = new Vector3d(Math.Cos(ang), 0, Math.Sin(ang));
                var r = dir * s.RadialOffset + new Vector3d(0, mid, 0);
                var p = new Vessel(piece, $"{s.Name} №{c + 1} (обломок)")
                {
                    IsDebris = true,
                    Body = Body,
                    Position = Position + LocalToWorld(r),
                    Velocity = Velocity + LocalToWorld(Vector3d.Cross(w, r) + dir * push),
                    Attitude = Attitude,
                    // ω × (0, h, 0) = dir при ω = up × dir: верх блока уходит наружу.
                    AngularVelocity = w + Vector3d.Cross(Vector3d.up, dir) * RadialTumble,
                    RadialYaw = ang,
                    Situation = Situation,
                    AnchorBodyFixed = AnchorBodyFixed + (AttitudeBodyFixed * r).SwapYZ,
                    AttitudeBodyFixed = AttitudeBodyFixed,
                    Throttle = Running[j] ? Throttle : 0,
                    Sas = SasMode.Off,
                    LaunchTime = LaunchTime,
                };
                p.Propellant[0] = Propellant[j] / n;
                p.IgnitionsLeft[0] = IgnitionsLeft[j];
                p.Running[0] = p.Armed[0] = p.ignitionLatch[0] = Running[j];
                res.Add(p);
            }
            Attached[j] = false;
            Running[j] = false;
            MassProperties(out _, out double com1, out _, out _);
            var rc = new Vector3d(0, com1 - com0, 0);
            Position += LocalToWorld(rc);
            if (IsLanded) AnchorBodyFixed += (AttitudeBodyFixed * rc).SwapYZ;
            Velocity += LocalToWorld(Vector3d.Cross(w, rc));
            SasHoldValid = false;
            return res;
        }

        /// <summary>Имя отделившегося управляемого борта: по обитаемому модулю (до двоеточия), с ведущей ступенью снизу.</summary>
        string CraftName()
        {
            var secs = Design.Sections;
            string core = null;
            foreach (int i in Order)
            {
                if (!Attached[i] || (secs[i].Crew <= 0 && !secs[i].DockingPort)) continue;
                core = secs[i].Name;
                if (secs[i].Crew > 0) break;
            }
            if (core == null) return secs[TopSection()].Name;
            int c = core.IndexOf(':');
            if (c > 0) core = core.Substring(0, c);
            string low = secs[BottomSection()].Name;
            return low.StartsWith(core) ? core : $"{low} + {core}";
        }

        // ---------------------------------------------------------------- стыковка (§6.6)

        /// <summary>
        /// Развернуть борт после расстыковки: секции, что стояли в связке перевёрнутыми, снова идут снизу вверх
        /// в своём порядке, а нос смотрит туда, где был их верх. В пространстве ничего не сдвигается: тот же ЦМ,
        /// те же секции — меняются только связанные оси (поворот на 180° вокруг X).
        /// </summary>
        void Unflip()
        {
            var at = new List<int>();
            for (int k = 0; k < Order.Length; k++)
                if (Attached[Order[k]]) at.Add(k);
            var ids = new int[at.Count];
            for (int m = 0; m < at.Count; m++) ids[m] = Order[at[m]];
            for (int m = 0; m < at.Count; m++)
            {
                int i = ids[at.Count - 1 - m];
                Order[at[m]] = i;
                Flipped[i] = !Flipped[i];
            }
            var flip = QuaternionD.AngleAxis(Math.PI, new Vector3d(1, 0, 0));
            Attitude = Attitude * flip;
            AttitudeBodyFixed = AttitudeBodyFixed * flip;
            // Угловая скорость в новых осях: поворот на π вокруг X меняет знак Y и Z.
            AngularVelocity = new Vector3d(AngularVelocity.x, -AngularVelocity.y, -AngularVelocity.z);
            SasHoldValid = false;
        }

        /// <summary>
        /// Причалить борт t (§6.6): его секции встают сверху носом к носу — в обратном порядке и перевёрнутыми.
        /// Импульс сохраняется; ось связки — ось этого борта, t подтягивается на неё (захват ≤ 1 м, DockCaptureRange).
        /// Борт t после этого лишний — его убирает Universe.
        /// </summary>
        public void Dock(Vessel t)
        {
            MassProperties(out double m0, out double com0, out _, out _);
            t.MassProperties(out double mt, out _, out _, out _);
            var bottom = Position - NoseP * com0;
            if (m0 + mt > 0) Velocity = (Velocity * m0 + t.Velocity * mt) / (m0 + mt);
            var order = new List<int>();
            foreach (int i in Order)
                if (Attached[i]) order.Add(i);
            for (int k = t.Order.Length - 1; k >= 0; k--)
            {
                int i = t.Order[k];
                if (!t.Attached[i] || Attached[i]) continue;
                order.Add(i);
                Attached[i] = true;
                Flipped[i] = !t.Flipped[i];
                Armed[i] = Running[i] = ignitionLatch[i] = false;
                Propellant[i] = t.Propellant[i];
                IgnitionsLeft[i] = t.IgnitionsLeft[i];
                ChuteDeployed[i] = t.ChuteDeployed[i];
                ChuteFailed[i] = t.ChuteFailed[i];
                ChuteArmed[i] = t.ChuteArmed[i];
                ChuteOpenTime[i] = t.ChuteOpenTime[i];
                ChuteAltitude[i] = t.ChuteAltitude[i];
                Deployed[i] = t.Deployed[i];
                DeployOn[i] = t.DeployOn[i];
            }
            foreach (int i in Order)
                if (!order.Contains(i)) order.Add(i);
            Order = order.ToArray();
            NextStage = NextApplicable(Math.Min(NextStage, t.NextStage));
            IsDebris = false;
            MassProperties(out _, out double com1, out _, out _);
            Position = bottom + NoseP * com1;
            SasHoldValid = false;
            Raise($"Стыковка: {t.Name}");
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
