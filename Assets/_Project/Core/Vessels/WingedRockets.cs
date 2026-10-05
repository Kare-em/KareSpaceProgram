using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Крылатые многоразовые корабли (GDD §4.6, §6.4, §7.3): Space Shuttle (STS-1 «Колумбия») и «Энергия» — «Буран».
    /// Пакет: центральный бак/блок (секция 0) с маршевыми двигателями, радиальные ускорители (секция 1), орбитер сбоку
    /// (секция 2, SectionDef.Beside). Маршевые RS-25 физически стоят на орбитере, но работают только от бака — здесь они
    /// числятся за баком и уходят с ним: на орбите у орбитера остаётся только OMS/ОДУ. Массы и тяги — по открытым данным
    /// (NASA STS-1 press kit, энциклопедия «Энергия — Буран»); где данных нет — оценка с пометкой.
    /// </summary>
    public static partial class VesselPresets
    {
        /// <summary>Сопоставление id → проект для крылатых. Пара: VesselPresets.ById (default) и MissionCatalog.AddWinged.</summary>
        public static VesselDesign WingedById(string id)
        {
            switch (id)
            {
                case "sts1": return ShuttleColumbia();
                case "buran": return EnergiaBuran();
                default: return null;
            }
        }

        /// <summary>
        /// Запас устойчивости орбитера, м: центр давления крыла позади ЦМ на столько. Меньше — борт теряет устойчивость по
        /// тангажу при выгорании OMS, больше — рулям не хватает хода на α 40° входа. Пара: OrbiterComFraction ↔ WingHeight.
        /// </summary>
        const double OrbiterStaticMargin = 0.8;
        /// <summary>ЦМ орбитера — доля длины от хвоста: маршевые, OMS и крыло в корме («Шаттл» — 0,65 длины от носа).</summary>
        const double OrbiterComFraction = 0.35;

        /// <summary>
        /// Высота фокуса корневой хорды над хвостом (WingDef.Height), при которой центр давления консоли
        /// (Aerodynamics.SpanCentroid полуразмаха по стреловидной линии) стоит на OrbiterStaticMargin позади ЦМ.
        /// </summary>
        static double WingHeight(double length, double span, double sweepDeg) =>
            length * OrbiterComFraction - OrbiterStaticMargin
            + Aerodynamics.SpanCentroid * span * 0.5 * Math.Tan(sweepDeg * Constants.Deg2Rad);

        // ------------------------------------------------------------------ Space Shuttle, STS-1

        /// <summary>
        /// Space Shuttle «Колумбия» (STS-1, 12.04.1981, LC-39A). ~2030 т на старте.
        /// Внешний бак ET (первый, крашеный): 35 т сухой, 719 т кислорода и водорода, Ø8,4 × 47 м; на нём — 3 × RS-25
        /// (SSME): 2,09 МН в вакууме, 1,67 МН у Земли, УИ 452/366 с. Два РДТТ SRB: по 87 т сухих и 502 т топлива, Ø3,7 × 45,5 м,
        /// тяга ≈12 МН в вакууме, УИ 268/242 с, ~110 с работы (настоящие 123 с с «провалом» тяги на max q — здесь ровно).
        /// Орбитер: 37,24 м, размах 23,79, крыло 249,9 м², стреловидность 81°/45° (двойная дельта; по 1/4 хорд ≈40°),
        /// посадочная масса STS-1 ≈ 89 т. OMS: 2 × 26,7 кН, УИ 316 с, ~11 т топлива (Δv ≈ 360 м/с).
        /// </summary>
        public static VesselDesign ShuttleColumbia()
        {
            const double orbLen = 37.24, orbSpan = 23.79, orbSweep = 40;
            var d = new VesselDesign { Name = "Space Shuttle «Колумбия»" };
            d.Sections.Add(new SectionDef
            {
                Name = "Внешний бак ET", Kind = SectionKind.Stage, DryMass = 35000, Propellant = 719000,
                Engine = new EngineDef
                {
                    Name = "RS-25 (SSME)", ThrustVac = 2.09e6, ThrustSL = 1.67e6, IspVac = 452.3, GimbalDeg = 10.5,
                    MinThrottle = 0.65,
                },
                EngineCount = 3, Length = 47, Diameter = 8.4, MaxHeatFlux = 2e5, RcsTorque = 0, Model = SectionModel.ShuttleET,
            });
            d.Sections.Add(new SectionDef
            {
                // Ось SRB — в 6,05 м от оси бака (4,2 + 1,85), по бокам: орбитер на −X, ускорители на ±Z.
                Name = "Твердотопливные ускорители SRB", Kind = SectionKind.Stage, DryMass = 2 * 87000, Propellant = 2 * 502000,
                Engine = new EngineDef { Name = "SRB", ThrustVac = 12.0e6, ThrustSL = 10.84e6, IspVac = 268, GimbalDeg = 8, Solid = true },
                EngineCount = 2, Length = 45.5, Diameter = 3.7, MaxHeatFlux = 2e5,
                RadialCount = 2, RadialParent = 0, RadialOffset = 6.05, RadialPhase = Math.PI / 2, ParachuteArea = 0,
                Model = SectionModel.ShuttleSRB,
            });
            d.Sections.Add(Orbiter("Орбитер «Колумбия»", orbLen, 5.2, orbSpan, 249.9, orbSweep, 38.4, 8.0,
                dry: 88000, propellant: 11000,
                new EngineDef { Name = "OMS", ThrustVac = 26.7e3, ThrustSL = 15e3, IspVac = 316, Ignitions = 10 },
                // Ось орбитера над осью бака: 4,2 (радиус ET) + 2,6 (радиус фюзеляжа) + ~0,1 зазора.
                besideOffset: 6.9, crew: 2, model: SectionModel.ShuttleOrbiter));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 1));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0, withPrevious: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1));
            // Сброс бака взводит OMS орбитера (NextCore(0) = 2): довыведение и тормозной импульс.
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            return d;
        }

        // ------------------------------------------------------------------ «Энергия» — «Буран»

        /// <summary>
        /// «Энергия» (11К25) с «Бураном» 1К1 (15.11.1988, Байконур, пл. 110), ~2400 т.
        /// Центральный блок Ц: Ø7,75 × 58,8 м, ~70 т сухой, ~700 т кислорода и водорода, 4 × РД-0120: 1,96 МН в вакууме,
        /// 1,52 МН у Земли, УИ 455/353 с. Четыре блока А (Ø3,92 × 38,3 м, 35 т + 320 т керосина и кислорода), на каждом РД-170:
        /// 7,9/7,26 МН, УИ 337/309 с, ~140 с работы. В отличие от «Шаттла», маршевые блока Ц на «Буране» не стоят — у орбитера
        /// только ОДУ (2 × 88 кН, УИ 362 с); здесь это совпадает с моделью «двигатели за баком».
        /// «Буран»: 36,37 м, размах 23,92, крыло 250 м² (двойная дельта 78°/45°), стартовая масса в полёте 1988 г. ≈ 80 т,
        /// посадочная ≈ 74 т (ОДУ довыводил на орбиту). Экипажа не было: посадка — автоматом.
        /// </summary>
        public static VesselDesign EnergiaBuran()
        {
            const double orbLen = 36.37, orbSpan = 23.92, orbSweep = 45;
            var d = new VesselDesign { Name = "«Энергия» — «Буран»" };
            d.Sections.Add(new SectionDef
            {
                Name = "Центральный блок Ц", Kind = SectionKind.Stage, DryMass = 70000, Propellant = 700000,
                Engine = new EngineDef
                {
                    Name = "РД-0120", ThrustVac = 1.96e6, ThrustSL = 1.52e6, IspVac = 455, GimbalDeg = 11, MinThrottle = 0.45,
                },
                EngineCount = 4, Length = 58.8, Diameter = 7.75, MaxHeatFlux = 2e5, Model = SectionModel.EnergiaCore,
            });
            d.Sections.Add(new SectionDef
            {
                // Оси блоков А — в 5,9 м от оси Ц (3,875 + 1,96 + зазор), по диагоналям: орбитер стоит на −X между ними.
                Name = "Блоки А", Kind = SectionKind.Stage, DryMass = 4 * 35000, Propellant = 4 * 320000,
                Engine = new EngineDef { Name = "РД-170", ThrustVac = 7.9e6, ThrustSL = 7.26e6, IspVac = 337, GimbalDeg = 8 },
                EngineCount = 4, Length = 38.3, Diameter = 3.92, MaxHeatFlux = 2e5,
                RadialCount = 4, RadialParent = 0, RadialOffset = 5.9, RadialPhase = Math.PI / 4, Model = SectionModel.EnergiaBlockA,
            });
            d.Sections.Add(Orbiter("«Буран»", orbLen, 5.6, orbSpan, 250, orbSweep, 38.5, 8.0,
                dry: 72000, propellant: 8000,
                new EngineDef { Name = "ОДУ", ThrustVac = 88e3, ThrustSL = 60e3, IspVac = 362, Ignitions = 10 },
                // 3,875 (радиус Ц) + 2,8 (радиус фюзеляжа) + ~0,5 на узлы крепления.
                besideOffset: 7.2, crew: 0, model: SectionModel.Buran));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 1));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0, withPrevious: true));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            return d;
        }

        /// <summary>
        /// Орбитер (крылатый планер с теплозащитой). Крыло — низкоплан с поперечным V 7° («Шаттл» 3,5°, «Буран» 6° — 7° даёт
        /// запас поперечной устойчивости модели, где фюзеляж её не добавляет); киль с расщепляемым рулём — щиток-тормоз.
        /// Cd0 крыла 0,02 — толстый профиль с плитками ТЗП, отсюда L/D ≈ 4,5 на дозвуке (у «Шаттла» 4,5–5).
        /// </summary>
        static SectionDef Orbiter(string name, double len, double diam, double span, double area, double sweep,
            double finArea, double finHeight, double dry, double propellant, EngineDef oms, double besideOffset, int crew,
            SectionModel model = SectionModel.None) => new SectionDef
        {
            Name = name, Kind = SectionKind.Stage, DryMass = dry, Propellant = propellant, Engine = oms, EngineCount = 2,
            // Вид — FBX из winged_parts.py; крыло и киль физики остаются по WingDef ниже, пластины WingMesh не рисуются.
            Model = model,
            Length = len, Diameter = diam, Crew = crew,
            // РСУ орбитера: 38 основных двигателей по 3,9 кН, плечо ~15 м — по оси ~2·10⁵ Н·м.
            RcsTorque = 2e5,
            // Плитки ТЗП держат ~1,6 МВт/м² (1650 °C на кромках); по Саттону — Грейвзу на радиусе фюзеляжа вход даёт ~0,3.
            MaxHeatFlux = 1.6e6,
            Beside = true, BesideOffset = besideOffset, ComFraction = OrbiterComFraction,
            Deploy = DeployKind.Gear, GearHeight = 1.8,
            Wings = new List<WingDef>
            {
                new WingDef
                {
                    Name = "Крыло", Area = area, Span = span, Sweep = sweep, Incidence = 0.5, Dihedral = 7,
                    Height = WingHeight(len, span, sweep), Offset = -diam * 0.3, Cd0 = 0.02,
                    // Элевоны ≈ 19 % площади, ход −35°…+20° — модель симметричная.
                    ControlFraction = 0.19, ControlMaxDeg = 25,
                },
                new WingDef
                {
                    // Киль: корень ≈ 1/4 хорды в 6,5 м от хвоста; расщепляемый руль направления — щиток-тормоз ≈ 10 м².
                    Name = "Киль", Vertical = true, Area = finArea, Span = finHeight, Sweep = 45, Height = 6.5,
                    Offset = diam * 0.5, Cd0 = 0.01, ControlFraction = 0.25, ControlMaxDeg = 25, BrakeArea = 10,
                },
            },
        };
    }
}
