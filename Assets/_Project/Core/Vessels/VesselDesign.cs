using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Двигатель (связка однотипных камер считается одним кластером). Тяга падает с давлением как
    /// T(p) = Tvac − p·Ae при постоянном расходе — так ведут себя реальные ЖРД (GDD §4.4).
    /// </summary>
    public sealed class EngineDef
    {
        public string Name;
        public double ThrustVac;
        public double ThrustSL;
        public double IspVac;
        /// <summary>Минимальная тяга в долях от полной. 1 — двигатель не дросселируется.</summary>
        public double MinThrottle = 1;
        public double GimbalDeg;
        /// <summary>Число запусков на весь полёт (GDD §6.3 «Ограниченные запуски»).</summary>
        public int Ignitions = 1;
        /// <summary>Нужна осадка топлива перед запуском в невесомости (GDD §6.3).</summary>
        public bool NeedsUllage;
        /// <summary>
        /// Твердотопливный: запускается при взведении сам, без РУД, и не глушится до выработки (GDD §6.3).
        /// Тяга ступени неуправляема — автопилот поджигает её в апоцентре (AscentAutopilot.NextIsSolidKick).
        /// </summary>
        public bool Solid;

        public double MassFlow => ThrustVac / (IspVac * Constants.G0);
        public double NozzleArea => (ThrustVac - ThrustSL) / 101325.0;
        public double Thrust(double pressure) => Math.Max(0, ThrustVac - NozzleArea * pressure);
        public double Isp(double pressure) => Thrust(pressure) / (MassFlow * Constants.G0);
        public double IspSL => Isp(101325);
    }

    /// <summary>Деталь вида секции из Tools/blender. Физики не касается: ядро о мешах не знает, только об имени детали.</summary>
    public enum SectionModel
    {
        None, Sputnik, VostokService, Luna9,
        Lunokhod, Luna17KT, LMDescent, LMAscent, ApolloCM, ApolloSM, Mercury, Gemini, GeminiAdapter, Surveyor, Ranger, Explorer1,
        // Корпуса ступеней (Tools/blender/hulls_models.py): сопла в модели, начало — днище секции.
        Redstone, JunoStage1, JunoCluster11, JunoCluster3, AtlasBooster, AtlasSustainer, AtlasSustainerAgena, AtlasSustainerCentaur,
        Agena, Centaur, TitanStage1, TitanStage2, SaturnSIC, SaturnSII, SaturnSIVB, ProtonStage1, ProtonStage2, ProtonStage3, BlokD,
    }

    public enum SectionKind
    {
        Stage,
        Payload,
        /// <summary>Спускаемый аппарат: теплозащита, парашют, выдерживает посадку.</summary>
        Capsule,
        Fairing,
    }

    /// <summary>Секция пакета (ступень, ПН, капсула, обтекатель). Пакет — стопка секций снизу вверх.</summary>
    public sealed class SectionDef
    {
        public string Name;
        public SectionKind Kind;
        public double DryMass;
        public double Propellant;
        public EngineDef Engine;
        public int EngineCount;
        /// <summary>Двигатели осадки: при разделении сами осаживают топливо следующей ступени.</summary>
        public bool UllageMotors;
        public double Length, Diameter;
        /// <summary>Момент от РСУ/маховиков, Н·м — управляет ориентацией без тяги.</summary>
        public double RcsTorque;
        /// <summary>
        /// Стабилизаторы у низа секции, м² (сумма плоскостей). Неуправляемой ракете (§4.6) только они
        /// переносят центр давления за ЦМ; пара — FlightPhysics.FinNormalSlope.
        /// </summary>
        public double FinArea;
        /// <summary>Площадь купола, м² (0 — парашюта нет).</summary>
        public double ParachuteArea;
        /// <summary>Множитель Cd: тупое тело капсулы тормозит сильнее ракеты.</summary>
        public double DragScale = 1;
        /// <summary>Предельный тепловой поток, Вт/м²: выше секция разрушается (GDD §4.6).</summary>
        public double MaxHeatFlux = 3e5;
        /// <summary>Только обтекатель: сколько секций под ним он закрывает.</summary>
        public int EnclosesBelow;
        /// <summary>Только вид: шар диаметром Diameter (СА «Восток»), а не конус капсулы. Физику не меняет.</summary>
        public bool Sphere;
        /// <summary>Только вид: четыре посадочные опоры по кромке днища (станция Е-6К). Физику не меняет.</summary>
        public bool LandingLegs;
        /// <summary>Только вид: деталь из Models/*.fbx вместо процедурного корпуса (VesselView.ModelFor).</summary>
        public SectionModel Model;
        /// <summary>Мест экипажа: перегрузка (§4.7) убивает только живых, приборные капсулы её терпят.</summary>
        public int Crew;
        /// <summary>
        /// Самоходное шасси («Луноход»): став нижней секцией на грунте, ездит от W/S и A/D (FlightPhysics.StepLanded).
        /// </summary>
        public bool Rover;

        public double Mass => DryMass + Propellant;
        public double Radius => Diameter * 0.5;
        public bool HasEngine => Engine != null && EngineCount > 0;
    }

    public enum StageActionType
    {
        Ignite,
        /// <summary>Отделить секцию и всё, что под ней.</summary>
        Separate,
        JettisonFairing,
        DeployParachute,
    }

    public struct StageAction
    {
        public StageActionType Type;
        public int Section;
        /// <summary>Только Separate: сразу запустить двигатели новой нижней ступени (с осадкой).</summary>
        public bool IgniteNext;

        public StageAction(StageActionType type, int section, bool igniteNext = false)
        {
            Type = type;
            Section = section;
            IgniteNext = igniteNext;
        }
    }

    public sealed class StageStats
    {
        public string Name;
        public double DeltaVVac, DeltaVSL, TwrSL, TwrVac, BurnTime, StartMass, EndMass;
    }

    /// <summary>Проект ракеты: секции снизу вверх и последовательность ступеней (пробел).</summary>
    public sealed class VesselDesign
    {
        public string Name;
        public readonly List<SectionDef> Sections = new List<SectionDef>();
        public readonly List<StageAction> Sequence = new List<StageAction>();

        public double TotalMass
        {
            get
            {
                double m = 0;
                foreach (var s in Sections) m += s.Mass;
                return m;
            }
        }

        /// <summary>Δv и тяговооружённость по ступеням в порядке работы (по g Земли).</summary>
        public List<StageStats> ComputeStats() => ComputeStats(null, null, 0, null);

        /// <summary>
        /// Остаток по ступеням для летящего корабля: сначала уже работающие двигатели, затем
        /// последовательность с шага fromStage. Нужен автопилоту для оценки времени до орбиты.
        /// </summary>
        public List<StageStats> ComputeStats(bool[] attachedNow, double[] propellantNow, int fromStage, bool[] runningNow)
        {
            var res = new List<StageStats>();
            int n = Sections.Count;
            var attached = new bool[n];
            var prop = new double[n];
            for (int i = 0; i < n; i++)
            {
                attached[i] = attachedNow == null || attachedNow[i];
                prop[i] = propellantNow != null ? propellantNow[i] : Sections[i].Propellant;
            }

            double AttachedMass()
            {
                double m = 0;
                for (int i = 0; i < n; i++)
                    if (attached[i]) m += Sections[i].DryMass + prop[i];
                return m;
            }

            void Burn(int idx)
            {
                var s = Sections[idx];
                if (!attached[idx] || !s.HasEngine || prop[idx] <= 0) return;
                double m0 = AttachedMass(), m1 = m0 - prop[idx];
                double tv = s.Engine.ThrustVac * s.EngineCount, ts = s.Engine.Thrust(101325) * s.EngineCount;
                double ln = Math.Log(m0 / m1);
                res.Add(new StageStats
                {
                    Name = s.Name,
                    StartMass = m0,
                    EndMass = m1,
                    DeltaVVac = s.Engine.IspVac * Constants.G0 * ln,
                    DeltaVSL = s.Engine.IspSL * Constants.G0 * ln,
                    TwrVac = tv / (m0 * Constants.G0),
                    TwrSL = ts / (m0 * Constants.G0),
                    BurnTime = prop[idx] / (s.Engine.MassFlow * s.EngineCount),
                });
                prop[idx] = 0;
            }

            if (runningNow != null)
                for (int i = 0; i < n; i++)
                    if (runningNow[i]) Burn(i);
            for (int k = fromStage; k < Sequence.Count; k++)
            {
                var a = Sequence[k];
                switch (a.Type)
                {
                    case StageActionType.Ignite:
                        Burn(a.Section);
                        break;
                    case StageActionType.Separate:
                        for (int i = 0; i <= a.Section; i++) attached[i] = false;
                        if (a.IgniteNext && a.Section + 1 < Sections.Count) Burn(a.Section + 1);
                        break;
                    case StageActionType.JettisonFairing:
                        attached[a.Section] = false;
                        break;
                }
            }
            return res;
        }

        public double TotalDeltaVVac
        {
            get
            {
                double dv = 0;
                foreach (var s in ComputeStats()) dv += s.DeltaVVac;
                return dv;
            }
        }
    }

    /// <summary>Готовые ракеты стартового набора (GDD §5.3).</summary>
    public static partial class VesselPresets
    {
        // Двигатель первой ступени «Кара-1» — класс Merlin 1D: тяга и УИ — по открытым данным.
        public static EngineDef K1Engine() => new EngineDef
        {
            Name = "РД-К1", ThrustVac = 914.1e3, ThrustSL = 845.2e3, IspVac = 311,
            MinThrottle = 0.4, GimbalDeg = 5, Ignitions = 3, NeedsUllage = true,
        };

        public static EngineDef K2Engine() => new EngineDef
        {
            Name = "РД-К2В", ThrustVac = 981e3, ThrustSL = 420e3, IspVac = 348,
            MinThrottle = 0.4, GimbalDeg = 5, Ignitions = 4, NeedsUllage = true,
        };

        static SectionDef S1() => new SectionDef
        {
            Name = "Кара-1 / I ступень", Kind = SectionKind.Stage, DryMass = 25600, Propellant = 395700,
            Engine = K1Engine(), EngineCount = 9, Length = 41, Diameter = 3.7, MaxHeatFlux = 2e5,
        };

        static SectionDef S2() => new SectionDef
        {
            Name = "Кара-1 / II ступень", Kind = SectionKind.Stage, DryMass = 3900, Propellant = 92670,
            Engine = K2Engine(), EngineCount = 1, UllageMotors = true, Length = 13.8, Diameter = 3.7,
            RcsTorque = 6e4, MaxHeatFlux = 2e5,
        };

        static SectionDef Fairing(int encloses) => new SectionDef
        {
            Name = "Головной обтекатель", Kind = SectionKind.Fairing, DryMass = 1900, Length = 13, Diameter = 5.2,
            EnclosesBelow = encloses, MaxHeatFlux = 2e5,
        };

        static SectionDef Fairing(int encloses, double length, double diameter, double dry) => new SectionDef
        {
            Name = "Головной обтекатель", Kind = SectionKind.Fairing, DryMass = dry, Length = length, Diameter = diameter,
            EnclosesBelow = encloses, MaxHeatFlux = 2e5,
        };

        /// <summary>Двухступенчатая «Кара-1» с произвольной полезной нагрузкой (секции ПН снизу вверх).</summary>
        static VesselDesign Kara1(string name, params SectionDef[] payload)
        {
            var d = new VesselDesign { Name = name };
            d.Sections.Add(S1());
            d.Sections.Add(S2());
            d.Sections.AddRange(payload);
            int fairing = d.Sections.Count;
            d.Sections.Add(Fairing(payload.Length));
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true));
            d.Sequence.Add(new StageAction(StageActionType.JettisonFairing, fairing));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 1));
            return d;
        }

        /// <summary>Эталон: 10 т на опорную орбиту, Δv ≈ 11,1 км/с.</summary>
        public static VesselDesign Kara1Heavy() => Kara1("Кара-1 (10 т)", new SectionDef
        {
            Name = "Макет ПН 10 т", Kind = SectionKind.Payload, DryMass = 10000, Length = 6, Diameter = 3.7, RcsTorque = 2e3,
        });

        public static VesselDesign Kara1Sputnik() => Kara1("Кара-1 «Спутник»", new SectionDef
        {
            Name = "ПС-1", Kind = SectionKind.Payload, DryMass = 83.6, Length = 0.58, Diameter = 0.58, RcsTorque = 5,
            Model = SectionModel.Sputnik,
        });

        /// <summary>Пилотируемый «Восток»: приборный отсек с ТДУ + шар спускаемого аппарата.</summary>
        public static VesselDesign Kara1Vostok()
        {
            var d = Kara1("Кара-1 «Восток»",
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
                });
            // ТДУ — на тормозной импульс, затем отстрел приборного отсека и парашют.
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 2));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 2));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 3));
            return d;
        }

        /// <summary>Лунная станция класса Е-6: 1,5 т, своя посадочная ДУ (Δv ≈ 3 км/с).</summary>
        public static VesselDesign Kara1Luna()
        {
            var d = Kara1("Кара-1 «Луна»", new SectionDef
            {
                Name = "Станция Е-6К", Kind = SectionKind.Stage, DryMass = 500, Propellant = 1000,
                Engine = new EngineDef
                {
                    Name = "КТДУ-5К", ThrustVac = 16e3, ThrustSL = 12e3, IspVac = 277, MinThrottle = 0.25, Ignitions = 4,
                },
                EngineCount = 1, Length = 2.7, Diameter = 2.0, RcsTorque = 800, MaxHeatFlux = 2e5, LandingLegs = true,
                Model = SectionModel.Luna9,
            });
            // КТДУ взводится отделением II ступени: на торможении у Луны ступень сменяется без лишнего шага (GDD §6.11).
            d.Sequence[d.Sequence.Count - 1] = new StageAction(StageActionType.Separate, 1, igniteNext: true);
            return d;
        }

        /// <summary>Геофизическая ракета для первого прыжка за линию Кармана (класс Р-1/В-2).</summary>
        public static VesselDesign SoundingRocket()
        {
            var d = new VesselDesign { Name = "Кара-Г (геофизическая)" };
            d.Sections.Add(new SectionDef
            {
                Name = "Ступень Г-1", Kind = SectionKind.Stage, DryMass = 4000, Propellant = 8800,
                Engine = new EngineDef { Name = "РД-Г", ThrustVac = 310e3, ThrustSL = 270e3, IspVac = 245, Ignitions = 1 },
                EngineCount = 1, Length = 13, Diameter = 1.65, MaxHeatFlux = 2e5, FinArea = 3,
            });
            d.Sections.Add(new SectionDef
            {
                Name = "Контейнер с приборами", Kind = SectionKind.Capsule, DryMass = 500, Length = 1.6, Diameter = 1.0,
                RcsTorque = 50, ParachuteArea = 60, DragScale = 2.0, MaxHeatFlux = 1.5e6,
            });
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0));
            d.Sequence.Add(new StageAction(StageActionType.DeployParachute, 1));
            return d;
        }

        public static VesselDesign ById(string id)
        {
            switch (id)
            {
                case "sounding": return SoundingRocket();
                case "sputnik": return Kara1Sputnik();
                case "vostok": return Kara1Vostok();
                case "luna": return Kara1Luna();
                case "luna17": return ProtonLuna17();
                case "juno1": return JunoExplorer1();
                case "mercury_redstone": return MercuryRedstone();
                case "mercury_atlas": return MercuryAtlas();
                case "gemini_titan": return GeminiTitan();
                case "ranger": return AtlasAgenaRanger();
                case "surveyor": return AtlasCentaurSurveyor();
                case "apollo8": return SaturnApollo8();
                case "apollo11": return SaturnApollo11();
                default: return Kara1Heavy();
            }
        }
    }
}
