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
    }
}
