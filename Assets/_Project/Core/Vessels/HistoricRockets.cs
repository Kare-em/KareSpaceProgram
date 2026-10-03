using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Исторические ракеты для миссий (GDD §5.3): семейство Р-7, «Протон-К» с «Луной-17» и американские носители
    /// до «Аполлона». Тяга — на один двигатель (умножается на EngineCount), массы и УИ — по открытым данным, округлённо.
    /// Боковые блоки Р-7 — радиальная группа (SectionDef.RadialCount), работают вместе с блоком А со стола.
    /// Ускорители Atlas сведены в отдельную нижнюю секцию — маршевый блок «поджигается» при её сбросе
    /// (docs/pitfalls-core.md).
    /// </summary>
    public static partial class VesselPresets
    {
        // ------------------------------------------------------------------ Р-7: «Спутник», «Восток», «Молния-М»

        /// <summary>РД-107: четырёхкамерный двигатель бокового блока (рулевые камеры учтены в тяге).</summary>
        static EngineDef RD107() => new EngineDef { Name = "РД-107", ThrustVac = 1000e3, ThrustSL = 813e3, IspVac = 314, GimbalDeg = 4 };

        /// <summary>РД-108: центральный блок А; у земли слабее РД-107, зато работает ~300 с — до конца II ступени.</summary>
        static EngineDef RD108() => new EngineDef { Name = "РД-108", ThrustVac = 941e3, ThrustSL = 745e3, IspVac = 315, GimbalDeg = 4 };

        /// <summary>
        /// Блок А (центральный): Ø2,95 × 28 м. Топливо — на ~300 с РД-108 (941 кН / 315 с → 305 кг/с).
        /// У «Спутника» блок А сам вышел на орбиту (сухой 7,5 т), у «Востока» и «Молнии» над ним третья ступень.
        /// </summary>
        static SectionDef R7BlockA(double dry, double propellant) => new SectionDef
        {
            Name = "Р-7 / блок А", Kind = SectionKind.Stage, DryMass = dry, Propellant = propellant,
            Engine = RD108(), EngineCount = 1, Length = 28, Diameter = 2.95, RcsTorque = 2e4, MaxHeatFlux = 2e5,
        };

        /// <summary>
        /// Боковые блоки Б, В, Г, Д — радиальная группа ×4 у блока А (секция 0): по 3,45 т сухих и 39,6 т топлива,
        /// РД-107 работает ~120 с (1000 кН / 314 с → 325 кг/с). Ø2,68 × 19,8 м, низ вровень с блоком А.
        /// RadialOffset — касание корпусов (2,95 / 2 + 2,68 / 2) плюс зазор, как Craft.RadialGap = 0,15.
        /// </summary>
        static SectionDef R7Boosters() => new SectionDef
        {
            Name = "Р-7 / блоки Б, В, Г, Д", Kind = SectionKind.Stage, DryMass = 4 * 3450, Propellant = 4 * 39600,
            Engine = RD107(), EngineCount = 4, Length = 19.8, Diameter = 2.68, MaxHeatFlux = 2e5,
            RadialCount = 4, RadialParent = 0, RadialOffset = 2.95 / 2 + 2.68 / 2 + 0.15,
        };

        /// <summary>Пакет Р-7: блок А (0), боковые (1), выше — верхние ступени и ПН. Пуск — все пять блоков разом.</summary>
        static VesselDesign R7(string name, SectionDef blockA, params SectionDef[] upper)
        {
            var d = new VesselDesign { Name = name };
            d.Sections.Add(blockA);
            d.Sections.Add(R7Boosters());
            d.Sections.AddRange(upper);
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 1));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0, withPrevious: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1));
            return d;
        }

        /// <summary>
        /// «Спутник» (8К71ПС, 04.10.1957): Р-7 без третьей ступени — блок А довёл ПС-1 до орбиты и сам остался
        /// на ней. Старт ~267 т.
        /// </summary>
        public static VesselDesign R7Sputnik()
        {
            var d = R7("Р-7 «Спутник»", R7BlockA(7500, 86500),
                new SectionDef
                {
                    Name = "ПС-1", Kind = SectionKind.Payload, DryMass = 83.6, Length = 0.58, Diameter = 0.58, RcsTorque = 5,
                    Model = SectionModel.Sputnik,
                },
                Fairing(1, 2.2, 1.2, 150));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, 3));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0));
            return d;
        }

        /// <summary>
        /// «Восток» (8К72К, 12.04.1961): блок А + боковые + блок Е (РД-0109) + корабль 4,73 т, старт ~287 т.
        /// ТДУ — на тормозной импульс, затем отстрел приборного отсека и парашют.
        /// </summary>
        public static VesselDesign R7Vostok()
        {
            var d = R7("Р-7 «Восток»", R7BlockA(6500, 94000),
                new SectionDef
                {
                    Name = "Блок Е", Kind = SectionKind.Stage, DryMass = 1440, Propellant = 6400,
                    Engine = new EngineDef { Name = "РД-0109", ThrustVac = 54.5e3, ThrustSL = 40e3, IspVac = 323.5, GimbalDeg = 3 },
                    EngineCount = 1, Length = 3.1, Diameter = 2.56, RcsTorque = 3e3, MaxHeatFlux = 2e5,
                },
                new SectionDef
                {
                    Name = "Приборный отсек", Kind = SectionKind.Stage, DryMass = 2000, Propellant = 275,
                    Engine = new EngineDef { Name = "ТДУ-1", ThrustVac = 15.8e3, ThrustSL = 12e3, IspVac = 266, Ignitions = 1 },
                    EngineCount = 1, Length = 2.3, Diameter = 2.4, RcsTorque = 1.5e3, Model = SectionModel.VostokService,
                },
                new SectionDef
                {
                    Name = "СА «Восток»", Kind = SectionKind.Capsule, DryMass = 2460, Length = 2.3, Diameter = 2.3,
                    RcsTorque = 300, ParachuteArea = 600, DragScale = 2.6, MaxHeatFlux = 3e6, Sphere = true, Crew = 1,
                },
                Fairing(2, 5.6, 2.7, 800));
            // Блок Е запускается на разделении (у настоящей — «горячее», ещё до сброса блока А).
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, 5));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 3));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 3));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 4));
            return d;
        }

        /// <summary>
        /// «Молния-М» (8К78М) со станцией Е-6 («Луна-9», 1966): блок И (РД-0110) выводит на опорную орбиту,
        /// блок Л разгоняет к Луне, станция КТДУ-5 корректирует и садится (Δv станции ≈ 3 км/с, GDD §6.11).
        /// Блоку Л даны 2 запуска: если блоку И не хватило, Л довыводит на опорную и потом разгоняет.
        /// </summary>
        public static VesselDesign R7Luna()
        {
            var d = R7("Р-7 «Молния-М»", R7BlockA(6500, 94000),
                new SectionDef
                {
                    Name = "Блок И", Kind = SectionKind.Stage, DryMass = 2400, Propellant = 22500,
                    Engine = new EngineDef
                    {
                        Name = "РД-0110", ThrustVac = 298e3, ThrustSL = 230e3, IspVac = 326, GimbalDeg = 3, NeedsUllage = true,
                    },
                    EngineCount = 1, Length = 6.7, Diameter = 2.66, RcsTorque = 8e3, MaxHeatFlux = 2e5,
                },
                new SectionDef
                {
                    Name = "Блок Л", Kind = SectionKind.Stage, DryMass = 1100, Propellant = 5400,
                    Engine = new EngineDef
                    {
                        Name = "С1.5400", ThrustVac = 66.7e3, ThrustSL = 40e3, IspVac = 340, GimbalDeg = 3, Ignitions = 2,
                    },
                    EngineCount = 1, UllageMotors = true, Length = 2.6, Diameter = 2.6, RcsTorque = 3e3, MaxHeatFlux = 2e5,
                },
                new SectionDef
                {
                    Name = "Станция Е-6", Kind = SectionKind.Stage, DryMass = 500, Propellant = 1000,
                    Engine = new EngineDef
                    {
                        Name = "КТДУ-5", ThrustVac = 16e3, ThrustSL = 12e3, IspVac = 277, MinThrottle = 0.25, Ignitions = 4,
                    },
                    EngineCount = 1, Length = 2.7, Diameter = 2.0, RcsTorque = 800, MaxHeatFlux = 2e5, LandingLegs = true,
                    Deploy = DeployKind.Legs,
                    Model = SectionModel.Luna9,
                },
                Fairing(2, 6.6, 2.7, 700));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, 5));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2, igniteNext: true));
            // КТДУ взводится отделением блока Л: на торможении у Луны ступень сменяется без лишнего шага (GDD §6.11).
            d.Sequence.Add(new StageAction(StageActionType.Separate, 3, igniteNext: true));
            return d;
        }

        // ------------------------------------------------------------------ «Протон-К» / «Луна-17»

        /// <summary>
        /// «Луна-17» (17.11.1970): «Протон-К» + разгонный блок Д + посадочная ступень КТ + «Луноход-1».
        /// Блок Д и довыводит на опорную орбиту, и разгоняет к Луне — III ступень своих запусков не повторяет.
        /// </summary>
        public static VesselDesign ProtonLuna17()
        {
            var d = new VesselDesign { Name = "Протон-К «Луна-17»" };
            d.Sections.Add(new SectionDef
            {
                Name = "Протон / I ступень", Kind = SectionKind.Stage, DryMass = 31000, Propellant = 419000,
                Engine = new EngineDef { Name = "РД-253", ThrustVac = 1635e3, ThrustSL = 1474e3, IspVac = 316, GimbalDeg = 7 },
                EngineCount = 6, Length = 21, Diameter = 7.4, MaxHeatFlux = 2e5, Model = SectionModel.ProtonStage1,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Протон / II ступень", Kind = SectionKind.Stage, DryMass = 11000, Propellant = 156000,
                Engine = new EngineDef { Name = "РД-0210", ThrustVac = 582e3, ThrustSL = 480e3, IspVac = 327, GimbalDeg = 3 },
                EngineCount = 4, Length = 17, Diameter = 4.1, RcsTorque = 4e4, MaxHeatFlux = 2e5, Model = SectionModel.ProtonStage2,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Протон / III ступень", Kind = SectionKind.Stage, DryMass = 4200, Propellant = 46500,
                Engine = new EngineDef { Name = "РД-0212", ThrustVac = 613e3, ThrustSL = 480e3, IspVac = 325, GimbalDeg = 3 },
                EngineCount = 1, Length = 4.1, Diameter = 4.1, RcsTorque = 2e4, MaxHeatFlux = 2e5, Model = SectionModel.ProtonStage3,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Блок Д", Kind = SectionKind.Stage, DryMass = 3300, Propellant = 15000,
                Engine = new EngineDef
                {
                    Name = "РД-58", ThrustVac = 83.4e3, ThrustSL = 40e3, IspVac = 349, GimbalDeg = 3, Ignitions = 5,
                    NeedsUllage = true,
                },
                EngineCount = 1, UllageMotors = true, Length = 5.5, Diameter = 3.7, RcsTorque = 3e3, MaxHeatFlux = 2e5, Model = SectionModel.BlokD,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Посадочная ступень КТ", Kind = SectionKind.Stage, DryMass = 1100, Propellant = 3700,
                Engine = new EngineDef
                {
                    Name = "КТДУ-417", ThrustVac = 18.8e3, ThrustSL = 14e3, IspVac = 313, MinThrottle = 0.25, Ignitions = 6,
                },
                EngineCount = 1, Length = 1.9, Diameter = 4.0, RcsTorque = 1500, MaxHeatFlux = 2e5, LandingLegs = true,
                Deploy = DeployKind.Ramps,
                Model = SectionModel.Luna17KT,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Луноход-1", Kind = SectionKind.Payload, DryMass = 756, Length = 1.9, Diameter = 2.2, RcsTorque = 50,
                Rover = true, Model = SectionModel.Lunokhod, Deploy = DeployKind.Lid,
            });
            int fairing = d.Sections.Count;
            d.Sections.Add(Fairing(3, 12, 4.1, 2000));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, fairing));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1, igniteNext: true));
            // Отделение III ступени взводит блок Д, отделение блока Д — КТДУ: на перелёте ступени сменяются
            // без лишнего шага, как у «Кары-1 Луна» (GDD §6.11).
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 3, igniteNext: true));
            // Луноход съезжает с КТ по трапам — в модели это отделение ступени на грунте.
            d.Sequence.Add(new StageAction(StageActionType.Separate, 4));
            return d;
        }

        // ------------------------------------------------------------------ Juno I / Explorer 1

        /// <summary>«Бэби Сержант» — РДТТ верхних ступеней Juno I (масштабная копия «Сержанта»).</summary>
        static EngineDef BabySergeant() => new EngineDef
        {
            Name = "Baby Sergeant", ThrustVac = 7.3e3, ThrustSL = 6.5e3, IspVac = 225, Solid = true,
        };

        /// <summary>
        /// Juno I (1.02.1958): удлинённый «Редстоун» + три твердотопливные ступени из связок «Бэби Сержантов».
        /// Верхние ступени поджигаются в апоцентре пассивного участка — так их и запускали (AscentAutopilot).
        /// Δv верхних ступеней ≈ 5,2 км/с, I ступени ≈ 4,3 км/с.
        /// </summary>
        public static VesselDesign JunoExplorer1()
        {
            var d = new VesselDesign { Name = "Juno I «Эксплорер-1»" };
            d.Sections.Add(new SectionDef
            {
                Name = "Juno I / I ступень", Kind = SectionKind.Stage, DryMass = 5400, Propellant = 24800,
                Engine = new EngineDef { Name = "A-7", ThrustVac = 416e3, ThrustSL = 369e3, IspVac = 265, GimbalDeg = 5 },
                EngineCount = 1, Length = 21, Diameter = 1.78, RcsTorque = 1e4, FinArea = 2, MaxHeatFlux = 2e5, Model = SectionModel.JunoStage1,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Связка 11 РДТТ", Kind = SectionKind.Stage, DryMass = 102.7, Propellant = 236.5,
                Engine = BabySergeant(), EngineCount = 11, Length = 1.3, Diameter = 0.76, RcsTorque = 200, MaxHeatFlux = 2e5, Model = SectionModel.JunoCluster11,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Связка 3 РДТТ", Kind = SectionKind.Stage, DryMass = 27, Propellant = 64.5,
                Engine = BabySergeant(), EngineCount = 3, Length = 1.0, Diameter = 0.42, RcsTorque = 200, MaxHeatFlux = 2e5, Model = SectionModel.JunoCluster3,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Эксплорер-1", Kind = SectionKind.Stage, DryMass = 14, Propellant = 21.5,
                Engine = BabySergeant(), EngineCount = 1, Length = 2.0, Diameter = 0.16, RcsTorque = 200, MaxHeatFlux = 2e5,
                Model = SectionModel.Explorer1,
            });
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2, igniteNext: true));
            return d;
        }

        // ------------------------------------------------------------------ «Меркурий»

        static SectionDef MercuryCapsule() => new SectionDef
        {
            Name = "Капсула «Меркурий»", Kind = SectionKind.Capsule, DryMass = 1300, Length = 2.9, Diameter = 1.89,
            RcsTorque = 200, ParachuteArea = 290, DragScale = 1.6, MaxHeatFlux = 3e6, Crew = 1, Model = SectionModel.Mercury,
        };

        /// <summary>«Меркурий-Редстоун» (5.05.1961, «Фридом-7»): суборбитальный прыжок Шепарда, апогей ≈ 187 км.</summary>
        public static VesselDesign MercuryRedstone()
        {
            var d = new VesselDesign { Name = "Меркурий-Редстоун «Фридом-7»" };
            d.Sections.Add(new SectionDef
            {
                Name = "Редстоун", Kind = SectionKind.Stage, DryMass = 3900, Propellant = 25400,
                Engine = new EngineDef { Name = "A-7", ThrustVac = 395e3, ThrustSL = 350e3, IspVac = 235, GimbalDeg = 5 },
                EngineCount = 1, Length = 17.7, Diameter = 1.78, RcsTorque = 1e4, FinArea = 2, MaxHeatFlux = 2e5, Model = SectionModel.Redstone,
            });
            d.Sections.Add(MercuryCapsule());
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 1));
            return d;
        }

        /// <summary>Стартовые ускорители Atlas (2 × LR-89): в модели — нижняя секция, сбрасываемая на ~130 с.</summary>
        static SectionDef AtlasBooster() => new SectionDef
        {
            Name = "Atlas / ускорители", Kind = SectionKind.Stage, DryMass = 3200, Propellant = 86000,
            Engine = new EngineDef { Name = "LR-89", ThrustVac = 960e3, ThrustSL = 845e3, IspVac = 290, GimbalDeg = 5 },
            EngineCount = 2, Length = 4, Diameter = 3.05, MaxHeatFlux = 2e5, Model = SectionModel.AtlasBooster,
        };

        /// <param name="top">Корпус по верхнему переходнику: под капсулу, под «Аджену» или под «Центавр».</param>
        static SectionDef AtlasSustainer(SectionModel top = SectionModel.AtlasSustainer) => new SectionDef
        {
            Name = "Atlas / маршевый блок", Kind = SectionKind.Stage, DryMass = 2400, Propellant = 27000,
            Engine = new EngineDef { Name = "LR-105", ThrustVac = 386e3, ThrustSL = 290e3, IspVac = 309, GimbalDeg = 3 },
            EngineCount = 1, Length = 20, Diameter = 3.05, RcsTorque = 1e4, MaxHeatFlux = 2e5, Model = top,
        };

        /// <summary>
        /// «Меркурий-Атлас» (20.02.1962, «Френдшип-7»): Гленн, три витка. Тормозной блок — три РДТТ по 4,5 кН,
        /// Δv ≈ 160 м/с, как у настоящего (3 × ~1 т·с).
        /// </summary>
        public static VesselDesign MercuryAtlas()
        {
            var d = new VesselDesign { Name = "Меркурий-Атлас «Френдшип-7»" };
            d.Sections.Add(AtlasBooster());
            d.Sections.Add(AtlasSustainer());
            d.Sections.Add(new SectionDef
            {
                Name = "Тормозной блок", Kind = SectionKind.Stage, DryMass = 40, Propellant = 100,
                Engine = new EngineDef { Name = "Тормозные РДТТ", ThrustVac = 4.5e3, ThrustSL = 4e3, IspVac = 230, Solid = true },
                EngineCount = 3, Length = 0.4, Diameter = 0.9, RcsTorque = 100, MaxHeatFlux = 3e6,
            });
            d.Sections.Add(MercuryCapsule());
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 2));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 3));
            return d;
        }

        // ------------------------------------------------------------------ «Джемини»

        /// <summary>«Джемини-Титан II» (23.03.1965, «Джемини-3»): Гриссом и Янг, первый манёвр с экипажем на орбите.</summary>
        public static VesselDesign GeminiTitan()
        {
            var d = new VesselDesign { Name = "Джемини-Титан II «Джемини-3»" };
            d.Sections.Add(new SectionDef
            {
                Name = "Титан II / I ступень", Kind = SectionKind.Stage, DryMass = 4400, Propellant = 113000,
                Engine = new EngineDef { Name = "LR-87", ThrustVac = 1045e3, ThrustSL = 956e3, IspVac = 296, GimbalDeg = 5 },
                EngineCount = 2, Length = 21, Diameter = 3.05, MaxHeatFlux = 2e5, Model = SectionModel.TitanStage1,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Титан II / II ступень", Kind = SectionKind.Stage, DryMass = 2300, Propellant = 27100,
                Engine = new EngineDef { Name = "LR-91", ThrustVac = 445e3, ThrustSL = 300e3, IspVac = 316, GimbalDeg = 3 },
                EngineCount = 1, Length = 8.5, Diameter = 3.05, RcsTorque = 1e4, MaxHeatFlux = 2e5, Model = SectionModel.TitanStage2,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Агрегатный отсек", Kind = SectionKind.Stage, DryMass = 1000, Propellant = 200,
                Engine = new EngineDef { Name = "Тормозные ДУ", ThrustVac = 10.5e3, ThrustSL = 8e3, IspVac = 240, Ignitions = 4 },
                EngineCount = 4, Length = 2.3, Diameter = 3.05, RcsTorque = 600, MaxHeatFlux = 2e5,
                Model = SectionModel.GeminiAdapter,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Капсула «Джемини»", Kind = SectionKind.Capsule, DryMass = 1983, Length = 3.4, Diameter = 2.34,
                RcsTorque = 400, ParachuteArea = 515, DragScale = 1.6, MaxHeatFlux = 3e6, Crew = 2, Model = SectionModel.Gemini,
            });
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 2));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 3));
            return d;
        }

        // ------------------------------------------------------------------ «Рейнджер», «Сервейор»

        /// <summary>«Атлас-Аджена» (28.07.1964, «Рейнджер-7»): первые крупные снимки Луны перед падением.</summary>
        public static VesselDesign AtlasAgenaRanger()
        {
            var d = new VesselDesign { Name = "Атлас-Аджена «Рейнджер-7»" };
            d.Sections.Add(AtlasBooster());
            d.Sections.Add(AtlasSustainer(SectionModel.AtlasSustainerAgena));
            d.Sections.Add(new SectionDef
            {
                Name = "Аджена", Kind = SectionKind.Stage, DryMass = 870, Propellant = 6300,
                Engine = new EngineDef
                {
                    Name = "Bell 8096", ThrustVac = 71.2e3, ThrustSL = 40e3, IspVac = 290, GimbalDeg = 3, Ignitions = 2,
                    NeedsUllage = true,
                },
                EngineCount = 1, UllageMotors = true, Length = 7.6, Diameter = 1.52, RcsTorque = 2e3, MaxHeatFlux = 2e5, Model = SectionModel.Agena,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Рейнджер-7", Kind = SectionKind.Stage, DryMass = 366, Propellant = 10,
                Engine = new EngineDef { Name = "Корректирующая ДУ", ThrustVac = 224, ThrustSL = 150, IspVac = 230, Ignitions = 2 },
                EngineCount = 1, Length = 3.1, Diameter = 1.5, RcsTorque = 30, MaxHeatFlux = 2e5, Model = SectionModel.Ranger,
            });
            int fairing = d.Sections.Count;
            d.Sections.Add(Fairing(1, 4.8, 1.65, 300));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, fairing));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2, igniteNext: true));
            return d;
        }

        /// <summary>
        /// «Атлас-Центавр» (30.05.1966, «Сервейор-1»): первая мягкая посадка США. Основная тормозная РДТТ и верньеры
        /// «Сервейора» сведены в одну дросселируемую ДУ — посадочному автопилоту нужна одна ступень (docs/pitfalls-core.md).
        /// </summary>
        public static VesselDesign AtlasCentaurSurveyor()
        {
            var d = new VesselDesign { Name = "Атлас-Центавр «Сервейор-1»" };
            d.Sections.Add(AtlasBooster());
            d.Sections.Add(AtlasSustainer(SectionModel.AtlasSustainerCentaur));
            d.Sections.Add(new SectionDef
            {
                Name = "Центавр", Kind = SectionKind.Stage, DryMass = 2000, Propellant = 13600,
                Engine = new EngineDef
                {
                    Name = "RL10", ThrustVac = 66.7e3, ThrustSL = 30e3, IspVac = 444, GimbalDeg = 4, Ignitions = 2,
                    NeedsUllage = true,
                },
                EngineCount = 2, UllageMotors = true, Length = 9.1, Diameter = 3.05, RcsTorque = 3e3, MaxHeatFlux = 2e5, Model = SectionModel.Centaur,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Сервейор-1", Kind = SectionKind.Stage, DryMass = 355, Propellant = 642,
                Engine = new EngineDef
                {
                    Name = "Посадочная ДУ", ThrustVac = 12e3, ThrustSL = 9e3, IspVac = 289, MinThrottle = 0.1, Ignitions = 4,
                },
                EngineCount = 1, Length = 3.0, Diameter = 2.0, RcsTorque = 300, MaxHeatFlux = 2e5, LandingLegs = true,
                Deploy = DeployKind.PyroLegs,
                Model = SectionModel.Surveyor,
            });
            int fairing = d.Sections.Count;
            d.Sections.Add(Fairing(1, 10, 3.05, 1000));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, fairing));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2, igniteNext: true));
            return d;
        }

        // ------------------------------------------------------------------ «Сатурн-5» / «Аполлон»

        static void SaturnV(VesselDesign d)
        {
            d.Sections.Add(new SectionDef
            {
                Name = "S-IC", Kind = SectionKind.Stage, DryMass = 131000, Propellant = 2149000,
                Engine = new EngineDef { Name = "F-1", ThrustVac = 7770e3, ThrustSL = 6770e3, IspVac = 304, GimbalDeg = 6 },
                EngineCount = 5, Length = 42, Diameter = 10.1, MaxHeatFlux = 2e5, Model = SectionModel.SaturnSIC,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "S-II", Kind = SectionKind.Stage, DryMass = 36000, Propellant = 444000,
                Engine = new EngineDef { Name = "J-2", ThrustVac = 1033e3, ThrustSL = 486e3, IspVac = 421, GimbalDeg = 7 },
                EngineCount = 5, UllageMotors = true, Length = 24.9, Diameter = 10.1, RcsTorque = 2e5, MaxHeatFlux = 2e5, Model = SectionModel.SaturnSII,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "S-IVB", Kind = SectionKind.Stage, DryMass = 11300, Propellant = 107000,
                Engine = new EngineDef
                {
                    Name = "J-2", ThrustVac = 1033e3, ThrustSL = 486e3, IspVac = 421, GimbalDeg = 7, Ignitions = 2,
                    NeedsUllage = true,
                },
                EngineCount = 1, UllageMotors = true, Length = 17.8, Diameter = 6.6, RcsTorque = 5e4, MaxHeatFlux = 2e5, Model = SectionModel.SaturnSIVB,
            });
        }

        static SectionDef ApolloSM() => new SectionDef
        {
            Name = "Служебный модуль", Kind = SectionKind.Stage, DryMass = 6110, Propellant = 18400,
            Engine = new EngineDef { Name = "SPS", ThrustVac = 91.2e3, ThrustSL = 50e3, IspVac = 314, GimbalDeg = 6, Ignitions = 36 },
            // Длина — только корпус (3,9 м + стык). Сопло SPS (ещё ~3 м) висит ниже, внутри переходника SLA, как у
            // настоящего «Аполлона»; при длине 7,5 м с соплом корпус SM висел над SLA на 3,5 м. Пара: модель Apollo_SM
            // (начало сверху, корпус y −3,9..0,08) и SLA 8,5 м − LM 6,0 м = 2,5 м под сопло.
            EngineCount = 1, Length = 4.0, Diameter = 3.9, RcsTorque = 4e3, MaxHeatFlux = 2e5, Model = SectionModel.ApolloSM,
        };

        static SectionDef ApolloCM() => new SectionDef
        {
            Name = "Командный модуль", Kind = SectionKind.Capsule, DryMass = 5560, Length = 3.2, Diameter = 3.9,
            RcsTorque = 2e3, ParachuteArea = 1520, DragScale = 1.3, MaxHeatFlux = 8e6, Crew = 3, Model = SectionModel.ApolloCM,
            DockingPort = true,
        };

        /// <summary>
        /// Система аварийного спасения: башня с РДТТ и защитный колпак КМ (BPC). Обтекатель над КМ (encloses 1) —
        /// уходит целиком после запуска S-II (§6.6). Начало модели — низ КМ, высота 13,1 м (apollo_fairings.py).
        /// </summary>
        static SectionDef ApolloLES()
        {
            var f = Fairing(1, 13.1, 3.96, 4170);
            f.Name = "САС";
            f.JettisonWhole = true;
            f.Model = SectionModel.ApolloLES;
            return f;
        }

        /// <summary>
        /// Переходник SLA: четыре панели вокруг ЛМ между S-IVB и СМ, 8,5 м, низ 6,6 м → верх 3,9 м. Раскрывается
        /// при отделении КСМ (Separate по самому переходнику — см. Vessel.Stage).
        /// </summary>
        static SectionDef ApolloSLA(int encloses)
        {
            var f = Fairing(encloses, 8.5, 6.6, 1800);
            f.Name = "Переходник SLA";
            f.Model = SectionModel.ApolloSLA;
            return f;
        }

        /// <summary>
        /// «Сатурн-5» «Аполлон-8» (21.12.1968): первый облёт Луны с экипажем, 10 витков, возврат. Вместо ЛМ —
        /// макет LTA-B 9 т: остаётся на S-IVB, КСМ уходит от переходника и сам тормозит у Луны.
        /// </summary>
        public static VesselDesign SaturnApollo8()
        {
            var d = new VesselDesign { Name = "Сатурн-5 «Аполлон-8»" };
            SaturnV(d);
            d.Sections.Add(new SectionDef
            {
                Name = "Макет LTA-B", Kind = SectionKind.Payload, DryMass = 9026, Length = 5.5, Diameter = 4.2, MaxHeatFlux = 2e5,
            });
            d.Sections.Add(ApolloSLA(1));
            d.Sections.Add(ApolloSM());
            d.Sections.Add(ApolloCM());
            d.Sections.Add(ApolloLES());
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, 7));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1, igniteNext: true));
            // Отделение КСМ от переходника взводит SPS: тормозной у Луны и обратный разгон — без лишнего шага.
            d.Sequence.Add(new StageAction(StageActionType.Separate, 4, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 5));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 6));
            return d;
        }

        /// <summary>
        /// «Сатурн-5» «Аполлон-11» (16.07.1969) со стыковочной программой (§6.6): ЛМ едет под переходником SLA на S-IVB.
        /// После отлёта к Луне КСМ отходит от переходника, разворачивается и причаливает к ЛМ (клавиша V), ЛМ
        /// вытаскивается с S-IVB. У Луны «Игл» отстыковывается с экипажем, садится, взлетает и причаливает к «Колумбии».
        /// </summary>
        public static VesselDesign SaturnApollo11()
        {
            var d = new VesselDesign { Name = "Сатурн-5 «Аполлон-11»" };
            SaturnV(d);
            d.Sections.Add(new SectionDef
            {
                Name = "LM «Игл»: посадочная ступень", Kind = SectionKind.Stage, DryMass = 2034, Propellant = 8248,
                Engine = new EngineDef
                {
                    Name = "DPS", ThrustVac = 45.04e3, ThrustSL = 30e3, IspVac = 311, MinThrottle = 0.1, GimbalDeg = 6,
                    Ignitions = 3,
                },
                EngineCount = 1, Length = 3.2, Diameter = 4.2, RcsTorque = 2e3, MaxHeatFlux = 2e5, LandingLegs = true,
                Deploy = DeployKind.PyroLegs,
                Model = SectionModel.LMDescent,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "LM «Игл»: взлётная ступень", Kind = SectionKind.Stage, DryMass = 2445, Propellant = 2353,
                // APS рассчитан на 35 запусков: взлёт, довыведение и импульсы сближения с КСМ (§6.6).
                Engine = new EngineDef { Name = "APS", ThrustVac = 15.6e3, ThrustSL = 10e3, IspVac = 311, Ignitions = 35 },
                EngineCount = 1, Length = 2.8, Diameter = 4.0, RcsTorque = 1.5e3, MaxHeatFlux = 2e5, Crew = 2,
                DockingPort = true, Model = SectionModel.LMAscent,
            });
            d.Sections.Add(ApolloSLA(2));  // 5
            d.Sections.Add(ApolloSM());    // 6
            d.Sections.Add(ApolloCM());    // 7
            d.Sections.Add(ApolloLES());   // 8
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, 8));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1, igniteNext: true));
            // Отлёт к Луне — вторым запуском J-2 (S-IVB). Дальше КСМ уходит от переходника; ступень с ЛМ остаётся бортом.
            d.Sequence.Add(new StageAction(StageActionType.Separate, 5, igniteNext: true));
            // После причаливания к ЛМ: отбросить S-IVB (ЛМ уже в связке перевёрнутым).
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2));
            // На окололунной орбите экипаж переходит в ЛМ: он отстыковывается и становится активным.
            d.Sequence.Add(new StageAction(StageActionType.Undock, 3, transferControl: true));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 3));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 3, igniteNext: true));
            // Взлётная ступень причалила к «Колумбии» — экипаж вернулся, ступень отбрасывается.
            d.Sequence.Add(new StageAction(StageActionType.Undock, 4));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 6));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 7));
            return d;
        }
    }
}
