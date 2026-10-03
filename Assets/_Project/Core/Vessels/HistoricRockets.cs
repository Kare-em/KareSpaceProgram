using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Исторические ракеты для миссий (GDD §5.3): «Протон-К» с «Луной-17» и американские носители до «Аполлона».
    /// Тяга — на один двигатель (умножается на EngineCount), массы и УИ — по открытым данным, округлённо.
    /// Допущения модели, общие для всех: со стола запускается одна секция, поэтому стартовые ускорители,
    /// работающие вместе с центральным блоком (Atlas), сведены в отдельную нижнюю секцию — центральный
    /// блок «поджигается» при её сбросе (docs/pitfalls-core.md).
    /// </summary>
    public static partial class VesselPresets
    {
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
                Model = SectionModel.Luna17KT,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Луноход-1", Kind = SectionKind.Payload, DryMass = 756, Length = 1.9, Diameter = 2.2, RcsTorque = 50,
                Rover = true, Model = SectionModel.Lunokhod,
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
            EngineCount = 1, Length = 7.5, Diameter = 3.9, RcsTorque = 4e3, MaxHeatFlux = 2e5, Model = SectionModel.ApolloSM,
        };

        static SectionDef ApolloCM() => new SectionDef
        {
            Name = "Командный модуль", Kind = SectionKind.Capsule, DryMass = 5560, Length = 3.2, Diameter = 3.9,
            RcsTorque = 2e3, ParachuteArea = 1520, DragScale = 1.3, MaxHeatFlux = 8e6, Crew = 3, Model = SectionModel.ApolloCM,
        };

        /// <summary>«Сатурн-5» «Аполлон-8» (21.12.1968): первый облёт Луны с экипажем, 10 витков, возврат.</summary>
        public static VesselDesign SaturnApollo8()
        {
            var d = new VesselDesign { Name = "Сатурн-5 «Аполлон-8»" };
            SaturnV(d);
            d.Sections.Add(ApolloSM());
            d.Sections.Add(ApolloCM());
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1, igniteNext: true));
            // Отделение S-IVB взводит SPS: тормозной у Луны и обратный разгон — без лишнего шага.
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 3));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 4));
            return d;
        }

        /// <summary>
        /// «Сатурн-5» «Аполлон-11» (16.07.1969). Стыковка не моделируется: пакет — стопка, поэтому лунный модуль
        /// стоит над командным, а КСМ уходит вместе с отделением («Колумбия» остаётся на орбите обломком).
        /// Возврат на Землю — за кадром миссии: цель — посадка и взлёт с Луны.
        /// </summary>
        public static VesselDesign SaturnApollo11()
        {
            var d = new VesselDesign { Name = "Сатурн-5 «Аполлон-11»" };
            SaturnV(d);
            d.Sections.Add(ApolloSM());
            d.Sections.Add(ApolloCM());
            d.Sections.Add(new SectionDef
            {
                Name = "LM / посадочная ступень", Kind = SectionKind.Stage, DryMass = 2034, Propellant = 8248,
                Engine = new EngineDef
                {
                    Name = "DPS", ThrustVac = 45.04e3, ThrustSL = 30e3, IspVac = 311, MinThrottle = 0.1, GimbalDeg = 6,
                    Ignitions = 3,
                },
                EngineCount = 1, Length = 3.2, Diameter = 4.2, RcsTorque = 2e3, MaxHeatFlux = 2e5, LandingLegs = true,
                Model = SectionModel.LMDescent,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "LM «Игл» / взлётная ступень", Kind = SectionKind.Stage, DryMass = 2445, Propellant = 2353,
                Engine = new EngineDef { Name = "APS", ThrustVac = 15.6e3, ThrustSL = 10e3, IspVac = 311, Ignitions = 2 },
                EngineCount = 1, Length = 2.8, Diameter = 4.0, RcsTorque = 1.5e3, MaxHeatFlux = 2e5, Crew = 2,
                Model = SectionModel.LMAscent,
            });
            int fairing = d.Sections.Count;
            d.Sections.Add(Fairing(2, 8.5, 6.6, 1800));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, fairing));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 4, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 5, igniteNext: true));
            return d;
        }
    }
}
