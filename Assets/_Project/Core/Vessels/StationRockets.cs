namespace Kare.Space.Core
{
    /// <summary>
    /// Корабли после «Аполлона» и станции-цели (GDD §6.6, §7.3): «Восход-2», «Союз ТМ-31» к МКС, Crew Dragon Demo-2.
    /// Станция — верхняя секция того же проекта: на старте она уже летает отдельным бортом (StationSetup), поэтому
    /// стыковка идёт штатно — Universe.CanDock требует общий проект. Массы, тяги и УИ — по открытым данным
    /// (энциклопедия «Космонавтика», spaceflight101, NASA press kits); где данных нет, — оценка с пометкой.
    /// </summary>
    public static partial class VesselPresets
    {
        /// <summary>Сопоставление id → проект для новых кораблей. Пара: VesselPresets.ById (default) и MissionCatalog.AddModern.</summary>
        public static VesselDesign ModernById(string id)
        {
            switch (id)
            {
                case "voskhod2": return R7Voskhod2();
                case "soyuz_tm31": return SoyuzTM31();
                case "crew_dragon": return FalconCrewDragon();
                default: return null;
            }
        }

        // ------------------------------------------------------------------ «Восход-2»

        /// <summary>
        /// Блок И «семёрки» (третья ступень «Восхода», «Молнии», «Союза»): РД-0108/РД-0110, 298 кН, УИ 326 с.
        /// Запускается «горячим» разделением, ещё на работающем блоке А.
        /// </summary>
        static SectionDef BlockI(string engine) => new SectionDef
        {
            Name = "Блок И", Kind = SectionKind.Stage, DryMass = 2400, Propellant = 22500,
            Engine = new EngineDef { Name = engine, ThrustVac = 298e3, ThrustSL = 230e3, IspVac = 326, GimbalDeg = 3, NeedsUllage = true },
            EngineCount = 1, Length = 6.7, Diameter = 2.66, RcsTorque = 8e3, MaxHeatFlux = 2e5, Model = SectionModel.R7BlockI,
        };

        /// <summary>
        /// «Восход» (11А57) с «Восходом-2» (18.03.1965): Р-7 + блок И + корабль 5,68 т — приборный отсек «Востока» с ТДУ-1,
        /// СА на двоих и надувной шлюз «Волга», через который Леонов вышел в открытый космос. Шлюз отстреливается до схода.
        /// Посадку «Восход» делал в СА (ДМП у земли) — здесь вместо ДМП купол чуть больше «востоковского»: 2,9 т на 700 м²
        /// дают у земли ~6,6 м/с, как 2,46 т на 600 м² у «Востока» (auto_vostok). Пара: ParachuteArea ↔ DryMass СА.
        /// </summary>
        public static VesselDesign R7Voskhod2()
        {
            var d = R7("Р-7 «Восход»", R7BlockA(6500, 94000),
                BlockI("РД-0108"),
                new SectionDef
                {
                    Name = "Приборный отсек", Kind = SectionKind.Stage, DryMass = 2250, Propellant = 275,
                    Engine = new EngineDef { Name = "ТДУ-1", ThrustVac = 15.8e3, ThrustSL = 12e3, IspVac = 266, Ignitions = 1 },
                    EngineCount = 1, Length = 2.3, Diameter = 2.4, RcsTorque = 1.5e3, Model = SectionModel.VostokService,
                },
                new SectionDef
                {
                    Name = "Восход-2: спускаемый аппарат", Kind = SectionKind.Capsule, DryMass = 2900, Length = 2.3, Diameter = 2.3,
                    RcsTorque = 300, ParachuteArea = 700, DragScale = 2.6, MaxHeatFlux = 3e6, Sphere = true, Crew = 2,
                },
                new SectionDef
                {
                    // Надутый шлюз — 2,5 м × Ø1,2 м, 250 кг; в полёте стоял сбоку СА, здесь — сверху (пакет соосный).
                    Name = "Шлюз «Волга»", Kind = SectionKind.Payload, DryMass = 250, Length = 2.5, Diameter = 1.2,
                    JettisonWhole = true, MaxHeatFlux = 2e5, Model = SectionModel.VoskhodAirlock,
                },
                Fairing(3, 8.4, 2.7, 900));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, 6));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, 5)); // шлюз — после выхода, до схода
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 3));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 3));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 4));
            return d;
        }

        // ------------------------------------------------------------------ «Союз ТМ-31» → МКС

        /// <summary>
        /// «Союз-У» с «Союзом ТМ-31» (31.10.2000, первая экспедиция МКС) и сама МКС ноября 2000 г. (секция 7).
        /// Корабль 7,1 т: ПАО 2,95 т (СКД 3,09 кН, УИ 302 с, ~880 кг топлива — Δv ≈ 390 м/с), СА 2,85 т на троих,
        /// БО 1,3 т со стыковочным узлом. Головной обтекатель с САС — одной секцией.
        /// МКС: «Заря» + «Юнити» + «Звезда» + Z1 + «Прогресс М1-3» ≈ 60 т, ~43 м по оси — соосный цилиндр.
        /// Порядок спуска как у настоящего: тормозной импульс СКД, затем разделение отсеков и парашют.
        /// </summary>
        public static VesselDesign SoyuzTM31()
        {
            var d = R7("Союз-У «Союз ТМ-31»", R7BlockA(6550, 95400),
                BlockI("РД-0110"),
                new SectionDef
                {
                    Name = "ПАО «Союза»", Kind = SectionKind.Stage, DryMass = 2070, Propellant = 880,
                    // СКД перезапускается десятки раз: импульсы сближения, коррекции, тормозной (§6.6).
                    Engine = new EngineDef { Name = "СКД", ThrustVac = 3.09e3, ThrustSL = 2e3, IspVac = 302, Ignitions = 40 },
                    EngineCount = 1, Length = 2.26, Diameter = 2.72, RcsTorque = 1.5e3, MaxHeatFlux = 2e5, Model = SectionModel.SoyuzPAO,
                    // Две панели СБ (по 4 створки) сложены вдоль корабля под обтекателем, раскрываются после отделения от блока И.
                    Deploy = DeployKind.Panels,
                },
                new SectionDef
                {
                    // Купол 1000 м²: 2,85 т садятся ~5,5 м/с (ДМП в последнюю секунду — за кадром). Пара: ParachuteArea ↔ DryMass.
                    Name = "Союз ТМ-31: спускаемый аппарат", Kind = SectionKind.Capsule, DryMass = 2850, Length = 2.24, Diameter = 2.17,
                    RcsTorque = 400, ParachuteArea = 1000, DragScale = 1.5, MaxHeatFlux = 3e6, Crew = 3, Model = SectionModel.SoyuzSA,
                },
                new SectionDef
                {
                    // БО со стыковочным агрегатом «штырь» сверху; сбрасывается целиком перед входом.
                    Name = "Бытовой отсек", Kind = SectionKind.Payload, DryMass = 1300, Length = 2.98, Diameter = 2.26,
                    RcsTorque = 100, DockingPort = true, JettisonWhole = true, MaxHeatFlux = 2e5, Model = SectionModel.SoyuzBO,
                },
                SoyuzShroud(),
                new SectionDef
                {
                    Name = "МКС", Kind = SectionKind.Payload, DryMass = 60000, Length = 43, Diameter = 4.15,
                    RcsTorque = 5e4, DockingPort = true, MaxHeatFlux = 2e5, Model = SectionModel.ISS2000,
                });
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, 6));
            // Отделение блока И взводит СКД: дальше все манёвры сближения — ПАО.
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Undock, 7));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, 5)); // БО
            d.Sequence.Add(new StageAction(StageActionType.Separate, 3));        // ПАО
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 4));
            return d;
        }

        /// <summary>Головной обтекатель «Союза» с башней САС: ~15 м, Ø3 м, ~5 т (обтекатель + РДТТ САС); уходит одним сбросом.</summary>
        static SectionDef SoyuzShroud()
        {
            var f = Fairing(3, 15, 3.0, 5000);
            f.Name = "Обтекатель с САС";
            // Модель — половина (−X), вторую VesselView рисует поворотом на 180°: створки расходятся при сбросе.
            f.Model = SectionModel.SoyuzShroud;
            return f;
        }

        // ------------------------------------------------------------------ Crew Dragon Demo-2 → МКС

        /// <summary>
        /// Falcon 9 Block 5 с Crew Dragon «Индевор» (Demo-2, 30.05.2020) и МКС 2020 г. (секция 4).
        /// I ступень: 9 × Merlin 1D (845/914 кН, УИ 282/311 с), 25,6 т сухих, 395,7 т топлива.
        /// II ступень: Merlin 1D Vacuum 981 кН, УИ 348 с, 3,9 т + 92,7 т. Dragon ~12 т: капсула 7,7 т + 2,56 т топлива
        /// (Draco 8 × 400 Н на манёвры; SuperDraco САС здесь не нужны), негерметичный «багажник» 1,8 т.
        /// I ступень возвращается (§6.9): MECO с запасом SpaceXRockets.Falcon9Reserve, после отделения её ведёт фоновый
        /// BoosterLandingAutopilot на баржу OCISLY (SpaceXRockets.Ocisly). Азотная РСУ — RcsTorque: переворот ≈ 30 с
        /// при инерции пустой ступени ≈ 9·10⁶ кг·м². Merlin — 4 запуска: старт, (разворотный), входной, посадочный.
        /// </summary>
        public static VesselDesign FalconCrewDragon()
        {
            var d = new VesselDesign { Name = "Falcon 9 «Crew Dragon Demo-2»" };
            d.Sections.Add(new SectionDef
            {
                Name = "Falcon 9: I ступень", Kind = SectionKind.Stage, DryMass = 25600, Propellant = 395700,
                Engine = new EngineDef { Name = "Merlin 1D", ThrustVac = 914e3, ThrustSL = 845e3, IspVac = 311, MinThrottle = 0.4, GimbalDeg = 5, Ignitions = 4 },
                // MaxHeatFlux выше «голого бака» 2e5: днище с теплозащитой, вход двигателями вперёд (Falcon9Reserve).
                EngineCount = 9, Length = 41.2, Diameter = 3.66, MaxHeatFlux = 4e5, Model = SectionModel.Falcon9S1,
                RcsTorque = SpaceXRockets.Falcon9RcsTorque, Deploy = DeployKind.Legs, Recovery = SpaceXRockets.Ocisly(),
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Falcon 9: II ступень", Kind = SectionKind.Stage, DryMass = 3900, Propellant = 92670,
                Engine = new EngineDef { Name = "Merlin 1D Vacuum", ThrustVac = 981e3, ThrustSL = 500e3, IspVac = 348, MinThrottle = 0.4, GimbalDeg = 5, Ignitions = 4 },
                EngineCount = 1, Length = 13.8, Diameter = 3.66, RcsTorque = 2e4, MaxHeatFlux = 2e5, Model = SectionModel.Falcon9S2,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Багажник Dragon", Kind = SectionKind.Payload, DryMass = 1800, Length = 3.7, Diameter = 3.7, MaxHeatFlux = 2e5,
                Model = SectionModel.DragonTrunk,
            });
            d.Sections.Add(new SectionDef
            {
                // Купола 4 × Ø35 м; 2000 м² — приводнение ~7 м/с при ~9,6 т (как у настоящего, 16 миль/ч). Пара: ParachuteArea ↔ масса.
                // Стропы выходят из отсека сбоку под носовым обтекателем (ChuteOffset 0,9 м — между шарниром и осью): капсула
                // висит наклонно. Узел — под откидным обтекателем (DeployKind.Nose): открывают перед стыковкой.
                Name = "Crew Dragon «Индевор»", Kind = SectionKind.Capsule, DryMass = 7700, Propellant = 2560,
                Engine = new EngineDef { Name = "Draco", ThrustVac = 400, ThrustSL = 300, IspVac = 300, Ignitions = 100 },
                EngineCount = 8, Length = 4.4, Diameter = 4.0, RcsTorque = 2e3, ParachuteArea = 2000, DragScale = 1.3,
                MaxHeatFlux = 8e6, Crew = 2, DockingPort = true, Model = SectionModel.CrewDragon,
                Deploy = DeployKind.Nose, ChuteCount = 4, ChuteOffset = 0.9,
            });
            d.Sections.Add(new SectionDef
            {
                // МКС 2020 г.: ~420 т, ось «Гармония» — «Звезда» ~51 м. Соосным цилиндром; фермы и панели — только масса.
                Name = "МКС", Kind = SectionKind.Payload, DryMass = 420000, Length = 51, Diameter = 4.4,
                RcsTorque = 1e6, DockingPort = true, MaxHeatFlux = 2e5, Model = SectionModel.ISS2020,
            });
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 3, withPrevious: true));
            d.Sequence.Add(new StageAction(StageActionType.Undock, 4));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2)); // багажник — перед входом
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 3));
            return d;
        }
    }
}
