using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Межпланетные станции (GDD §5.3): «Титан IIIE — Центавр» с «Вояджерами» (1977). Тяга — на один двигатель
    /// (умножается на EngineCount), массы и УИ — по открытым данным, округлённо.
    /// </summary>
    public static partial class VesselPresets
    {
        /// <summary>Ось РДТТ UA1205 от оси ядра: радиус ядра 1,525 + радиус РДТТ 1,525 + зазор 0,3 м.</summary>
        const double TitanSrbOffset = 1.525 + 1.525 + 0.3;

        /// <summary>
        /// «Титан IIIE — Центавр» с «Вояджером» (20.08 и 05.09.1977). Со стола работают только два пятисегментных РДТТ
        /// UA1205; ядро (I ступень «Титана III», LR87-AJ-11) запускается в воздухе на их выгорании — так и было:
        /// T+108 с запуск ядра, T+120 с сброс РДТТ. Обтекатель 17,7 м закрывает «Центавр» целиком.
        /// Разгон к Юпитеру — «Центавром» (второе включение) и твердотопливным модулем TE-M-364-4: РДТТ не отсекается,
        /// поэтому «Центавр» отрабатывает Δv без доли модуля (MissionAutopilot.Planetary.cs).
        /// Пара: стартовая масса ≈ 633 т (2 × 226 т РДТТ + 123 т ядро + 32 т II ступень + 15,6 т «Центавр» + 2 т ПН
        /// + 3,4 т обтекатель), тяговооружённость у земли ≈ 1,34 — тяга РДТТ и их топливо меняются только вместе.
        /// </summary>
        public static VesselDesign TitanCentaurVoyager(int number)
        {
            var d = new VesselDesign { Name = $"Титан IIIE — Центавр «Вояджер-{number}»" };
            d.Sections.Add(new SectionDef
            {
                Name = "Титан IIIE / ядро", Kind = SectionKind.Stage, DryMass = 5900, Propellant = 116000,
                Engine = new EngineDef { Name = "LR87-AJ-11", ThrustVac = 1170e3, ThrustSL = 1010e3, IspVac = 302, GimbalDeg = 5 },
                EngineCount = 2, Length = 22.2, Diameter = 3.05, MaxHeatFlux = 2e5, Model = SectionModel.Titan3Core,
            });
            d.Sections.Add(new SectionDef
            {
                // Топливо на ≈ 108 с: 192 т × 265 с × g / 4,6 МН (средняя тяга; пиковая у настоящих — до 5,9 МН).
                Name = "РДТТ UA1205", Kind = SectionKind.Stage, DryMass = 2 * 33800, Propellant = 2 * 192000,
                Engine = new EngineDef { Name = "UA1205", ThrustVac = 4.6e6, ThrustSL = 4.15e6, IspVac = 265, GimbalDeg = 5, Solid = true },
                EngineCount = 2, Length = 25.9, Diameter = 3.05, MaxHeatFlux = 2e5,
                RadialCount = 2, RadialParent = 0, RadialOffset = TitanSrbOffset, RadialPhase = Math.PI / 2,
                Model = SectionModel.TitanSRB,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Титан IIIE / II ступень", Kind = SectionKind.Stage, DryMass = 2900, Propellant = 29200,
                Engine = new EngineDef { Name = "LR91-AJ-11", ThrustVac = 454e3, ThrustSL = 300e3, IspVac = 316, GimbalDeg = 3 },
                EngineCount = 1, Length = 7.1, Diameter = 3.05, RcsTorque = 1e4, MaxHeatFlux = 2e5, Model = SectionModel.TitanStage2,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Центавр D-1T", Kind = SectionKind.Stage, DryMass = 2000, Propellant = 13600,
                Engine = new EngineDef
                {
                    Name = "RL10A-3-3", ThrustVac = 66.7e3, ThrustSL = 30e3, IspVac = 444, GimbalDeg = 4, Ignitions = 2,
                    NeedsUllage = true,
                },
                EngineCount = 2, UllageMotors = true, Length = 9.1, Diameter = 3.05, RcsTorque = 3e3, MaxHeatFlux = 2e5, Model = SectionModel.Centaur,
            });
            d.Sections.Add(new SectionDef
            {
                // TE-M-364-4: 1039 кг топлива, 68 кН, 286 с → работает ≈ 43 с и даёт ≈ 2 км/с зонду с модулем.
                Name = "Разгонный модуль", Kind = SectionKind.Stage, DryMass = 181, Propellant = 1039,
                Engine = new EngineDef { Name = "TE-M-364-4", ThrustVac = 68e3, ThrustSL = 60e3, IspVac = 286, Solid = true },
                EngineCount = 1, Length = 2.0, Diameter = 1.8, RcsTorque = 400, MaxHeatFlux = 2e5, Model = SectionModel.VoyagerPM,
            });
            d.Sections.Add(new SectionDef
            {
                // 825 кг со 105 кг гидразина; коррекции — четырьмя кормовыми ЖРД по 0,89 Н (Δv ≈ 290 м/с на всю миссию).
                Name = $"Вояджер-{number}", Kind = SectionKind.Stage, DryMass = 720, Propellant = 105,
                Engine = new EngineDef { Name = "Гидразиновые ЖРД", ThrustVac = 0.89, ThrustSL = 0.5, IspVac = 220, Ignitions = 1000 },
                EngineCount = 4, Length = 3.4, Diameter = 3.66, RcsTorque = 40, MaxHeatFlux = 2e5, Model = SectionModel.Voyager,
            });
            int fairing = d.Sections.Count;
            d.Sections.Add(Fairing(3, 17.7, 4.27, 3400));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 1));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, fairing));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 3, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 4, igniteNext: true));
            return d;
        }

        /// <summary>Ось SRMU от оси ядра «Титана IVB»: радиус ядра 1,525 + радиус SRMU 1,6 + зазор 0,3 м.</summary>
        const double Titan4SrmuOffset = 1.525 + 1.6 + 0.3;

        /// <summary>
        /// «Титан IVB — Центавр» с «Кассини — Гюйгенсом» (15.10.1997, SLC-40). Как у «Титана IIIE», со стола работают
        /// только два трёхсегментных SRMU, ядро (LR87-AJ-11) запускается в воздухе на их выгорании (T+131 с).
        /// «Центавр T» (⌀ 4,3 м) — довыведение на опорную и разгон к Венере (C3 ≈ 16,6 км²/с²), дальше всё — двигателем
        /// орбитера 445 Н: коррекции, манёвр в дальнем космосе, торможение у Сатурна (≈ 626 м/с за 96 мин), наведение
        /// на Титан. «Гюйгенс» — три капсулы сверху: лобовой экран, задняя крышка с основным куполом 8,3 м и спускаемый
        /// модуль с тормозным 3,03 м (GDD §5.3, §4.8). В раскладке и ЦМ зонд на носу, а висит, как у настоящего, сбоку
        /// орбитера экраном наружу (SideMounted) — оттуда же и отделяется (Vessel.Split, VesselView.Cassini.cs).
        /// Пара: стартовая масса ≈ 940 т (2 × 350 т SRMU + 165 т ядро + 40 т II ступень + 23,9 т «Центавр» + 5,6 т КА
        /// + 4,5 т обтекатель), тяговооружённость у земли ≈ 1,25 — тяга SRMU и их топливо меняются только вместе.
        /// </summary>
        public static VesselDesign TitanCentaurCassini()
        {
            var d = new VesselDesign { Name = "Титан IVB — Центавр «Кассини»" };
            d.Sections.Add(new SectionDef
            {
                Name = "Титан IVB / ядро", Kind = SectionKind.Stage, DryMass = 10000, Propellant = 155000,
                Engine = new EngineDef { Name = "LR87-AJ-11", ThrustVac = 1220e3, ThrustSL = 1050e3, IspVac = 302, GimbalDeg = 5 },
                EngineCount = 2, Length = 26.4, Diameter = 3.05, MaxHeatFlux = 2e5, Model = SectionModel.Titan4Core,
            });
            d.Sections.Add(new SectionDef
            {
                // Топливо на ≈ 140 с: 313,6 т × 286 с × g / 6,28 МН (средняя тяга в пустоте).
                Name = "РДТТ SRMU", Kind = SectionKind.Stage, DryMass = 2 * 36700, Propellant = 2 * 313600,
                Engine = new EngineDef { Name = "SRMU", ThrustVac = 6.28e6, ThrustSL = 5.75e6, IspVac = 286, GimbalDeg = 6, Solid = true },
                EngineCount = 2, Length = 34.3, Diameter = 3.2, MaxHeatFlux = 2e5,
                RadialCount = 2, RadialParent = 0, RadialOffset = Titan4SrmuOffset, RadialPhase = Math.PI / 2,
                Model = SectionModel.Titan4SRMU,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Титан IVB / II ступень", Kind = SectionKind.Stage, DryMass = 4700, Propellant = 35000,
                Engine = new EngineDef { Name = "LR91-AJ-11", ThrustVac = 467e3, ThrustSL = 300e3, IspVac = 316, GimbalDeg = 3 },
                EngineCount = 1, Length = 10, Diameter = 3.05, RcsTorque = 1.5e4, MaxHeatFlux = 2e5, Model = SectionModel.TitanStage2,
            });
            d.Sections.Add(new SectionDef
            {
                // «Центавр G-prime» под «Титан IV»: два включения — довыведение на опорную и разгон к Венере.
                Name = "Центавр T", Kind = SectionKind.Stage, DryMass = 2900, Propellant = 20950,
                Engine = new EngineDef
                {
                    Name = "RL10A-3-3A", ThrustVac = 73.4e3, ThrustSL = 30e3, IspVac = 444.4, GimbalDeg = 4, Ignitions = 2,
                    NeedsUllage = true,
                },
                EngineCount = 2, UllageMotors = true, Length = 9.0, Diameter = 4.3, RcsTorque = 4e3, MaxHeatFlux = 2e5, Model = SectionModel.Centaur,
            });
            d.Sections.Add(new SectionDef
            {
                // Сухая 2125 кг, 3130 кг ММГ/АТ на 445-ньютонный ЖРД (Isp 308 с): ≈ 2,5 км/с на всю миссию с зондом.
                // Толчок отделения «Гюйгенса» — 0,35 м/с, как пружины SED (25.12.2004).
                Name = "Кассини", Kind = SectionKind.Stage, DryMass = 2125, Propellant = 3130,
                Engine = new EngineDef { Name = "ЖРД 445 Н", ThrustVac = 445, ThrustSL = 300, IspVac = 308, Ignitions = 1000 },
                // Подвес «Гюйгенса» сбоку: ось на 2,9 м над днищем, верх спускаемого модуля в 0,62 м от оси (контейнер
                // купола утоплен в обечайку r 0,8), закрутка SED 7,5 об/мин. Пара: cassini_parts.py HUY_Z, HUY_R.
                EngineCount = 1, Length = 6.8, Diameter = 4.0, RcsTorque = 150, MaxHeatFlux = 2e5, DecouplerPush = 0.35,
                SideMountHeight = 2.9, SideMountRadius = 0.62, SideMountSpin = 7.5 * 2 * Math.PI / 60,
                Model = SectionModel.Cassini,
            });
            d.Sections.Add(new SectionDef
            {
                // Лобовой экран 2,7 м (79 кг, плитки AQ60): держит вход в атмосферу Титана на ≈ 6 км/с.
                Name = "Гюйгенс / лобовой экран", Kind = SectionKind.Capsule, DryMass = 79, Length = 0.6, Diameter = 2.7,
                DragScale = 1.3, MaxHeatFlux = 2e6, Model = SectionModel.HuygensShield, SideMounted = true,
            });
            d.Sections.Add(new SectionDef
            {
                // Основной купол 8,3 м (54 м²). Настоящий раскрылся на 160 км, у нас ввод не выше 15 км
                // (FlightPhysics.ChuteAltitudeMax) — верхнюю атмосферу зонд проходит под одним экраном (≈ 6 м/с у 15 км).
                Name = "Гюйгенс / задняя крышка", Kind = SectionKind.Capsule, DryMass = 38, Length = 0.5, Diameter = 2.6,
                ParachuteArea = 54, ChuteAltitude = 15000, MaxHeatFlux = 2e5, Model = SectionModel.HuygensCover, SideMounted = true,
            });
            d.Sections.Add(new SectionDef
            {
                // Спускаемый модуль 201 кг с тормозным куполом 3,03 м (7,2 м²): у грунта ≈ 4–5 м/с, как 14.01.2005.
                // RcsTorque — замена закрутке 7 об/мин: держит днище против потока до входа.
                Name = "Гюйгенс", Kind = SectionKind.Capsule, DryMass = 201, Length = 0.9, Diameter = 1.3,
                ParachuteArea = 7.2, ChuteAltitude = 15000, RcsTorque = 30, MaxHeatFlux = 2e5, Model = SectionModel.Huygens, SideMounted = true,
            });
            int fairing = d.Sections.Count;
            // Обтекатель 66 футов (20,1 м, ⌀ 5,08) закрывает «Центавр» и КА целиком.
            d.Sections.Add(Fairing(5, 20.1, 5.08, 4500));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 1));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, fairing));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 3, igniteNext: true));
            // Отделение «Гюйгенса» от орбитера, затем спуск: основной купол, сброс экрана, сброс крышки с куполом, тормозной.
            d.Sequence.Add(new StageAction(StageActionType.Separate, CassiniOrbiter));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 6));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 5));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 6));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 7));
            return d;
        }

        /// <summary>Индекс орбитера в пакете «Кассини» — его отделение и есть отделение «Гюйгенса» (MissionAutopilot.Titan.cs).</summary>
        public const int CassiniOrbiter = 4;
    }
}
