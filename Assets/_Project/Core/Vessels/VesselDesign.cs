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

    /// <summary>
    /// Раскладное на секции (§6.12), клавиша G — Vessel.ToggleDeploy. Legs — опоры на приводе, убираются обратно
    /// (деталь конструктора, как в KSP); PyroLegs — опоры на пирозамках, только выпуск: LM выпускал их на окололунной
    /// орбите, «Сервейор» — после отделения от «Центавра» (под обтекателем сложены); Ramps — трапы схода лунохода
    /// с КТ «Луны-17», откидываются только на грунте; Lid — крышка лунохода с солнечной батареей: на грунте
    /// открывается и закрывается (луноход закрывал её на лунную ночь). Опоры КТ и Е-6 неподвижные — None.
    /// </summary>
    public enum DeployKind { None, Legs, PyroLegs, Ramps, Lid }

    /// <summary>Деталь вида секции из Tools/blender. Физики не касается: ядро о мешах не знает, только об имени детали.</summary>
    public enum SectionModel
    {
        None, Sputnik, VostokService, Luna9,
        Lunokhod, Luna17KT, LMDescent, LMAscent, ApolloCM, ApolloSM, Mercury, Gemini, GeminiAdapter, Surveyor, Ranger, Explorer1,
        // Корпуса ступеней (Tools/blender/hulls_models.py): сопла в модели, начало — днище секции.
        Redstone, JunoStage1, JunoCluster11, JunoCluster3, AtlasBooster, AtlasSustainer, AtlasSustainerAgena, AtlasSustainerCentaur,
        Agena, Centaur, TitanStage1, TitanStage2, SaturnSIC, SaturnSII, SaturnSIVB, ProtonStage1, ProtonStage2, ProtonStage3, BlokD,
        // Сбрасываемые оболочки «Аполлона» (Tools/blender/apollo_fairings.py): САС целиком и половина переходника SLA.
        ApolloLES, ApolloSLA,
        // Процедурные тела вращения «семёрки» по профилю VesselPresets.R7*RadiusAt: блок А с «талией», конусы боковых.
        R7BlockA, R7Booster,
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
        /// <summary>Что раскладывает G (DeployKind). Сложенные опоры (Vessel.LegsDown) ломаются при касании быстрее
        /// FlightPhysics.StowedCrashSpeed; сложенные трапы не дают отделить ступень с луноходом (Vessel.StageBlock).</summary>
        public DeployKind Deploy;
        /// <summary>Только вид: деталь из Models/*.fbx вместо процедурного корпуса (VesselView.ModelFor).</summary>
        public SectionModel Model;
        /// <summary>Мест экипажа: перегрузка (§4.7) убивает только живых, приборные капсулы её терпят.</summary>
        public int Crew;
        /// <summary>
        /// Самоходное шасси («Луноход»): став нижней секцией на грунте, ездит от W/S и A/D (FlightPhysics.StepLanded).
        /// </summary>
        public bool Rover;
        /// <summary>Стыковочный узел на верхнем торце секции (§6.6): к нему причаливает другой борт носом к носу.</summary>
        public bool DockingPort;
        /// <summary>Только обтекатель: уходит целиком на своём двигателе увода, без створок (САС «Аполлона»).</summary>
        public bool JettisonWhole;
        /// <summary>
        /// Радиальная группа (конструктор, §5.4): RadialCount ≥ 2 одинаковых блоков по кругу у секции RadialParent, ось блока —
        /// в RadialOffset от оси пакета. Масса, топливо, EngineCount и площади — на всю группу; блоку достаётся 1/N
        /// (VesselDesign.RadialPiece), поэтому EngineCount кратен RadialCount. В Sections группа стоит после родителя.
        /// </summary>
        public int RadialCount;
        public int RadialParent;
        public double RadialOffset;
        /// <summary>Подъём низа блока над низом родителя, м.</summary>
        public double RadialLift;
        /// <summary>Толчок разделителя под этой секцией, м/с; 0 — штатный (Vessel.StagePush, у радиальных — Vessel.RadialPush).</summary>
        public double DecouplerPush;

        public double Mass => DryMass + Propellant;
        public double Radius => Diameter * 0.5;
        public bool HasEngine => Engine != null && EngineCount > 0;
        public bool IsRadial => RadialCount >= 2;
        public SectionDef Clone() => (SectionDef)MemberwiseClone();
    }

    public enum StageActionType
    {
        Ignite,
        /// <summary>Отделить секцию и всё, что под ней.</summary>
        Separate,
        JettisonFairing,
        DeployParachute,
        /// <summary>Отстыковать пристыкованный (перевёрнутый) модуль, §6.6.</summary>
        Undock,
    }

    public struct StageAction
    {
        public StageActionType Type;
        public int Section;
        /// <summary>Только Separate: сразу запустить двигатели новой нижней ступени (с осадкой).</summary>
        public bool IgniteNext;
        /// <summary>Только Undock: экипаж уходит в отстыкованный модуль — он становится активным бортом (ЛМ «Аполлона»).</summary>
        public bool TransferControl;
        /// <summary>Выполняется тем же нажатием пробела, что и предыдущий шаг (группа ступени KSP: старт ядра и ускорителей).</summary>
        public bool WithPrevious;

        public StageAction(StageActionType type, int section, bool igniteNext = false, bool transferControl = false, bool withPrevious = false)
        {
            Type = type;
            Section = section;
            IgniteNext = igniteNext;
            TransferControl = transferControl;
            WithPrevious = withPrevious;
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

        /// <summary>
        /// Уходит ли секция i при отделении Separate(k). Отделение радиальной группы снимает только её; отделение секции
        /// пакета — всё, что ниже, вместе с радиальными блоками на этих секциях (они крепятся к родителю, а не к индексу).
        /// </summary>
        public bool DetachedBy(int i, int k)
        {
            if (Sections[k].IsRadial) return i == k;
            var s = Sections[i];
            return s.IsRadial ? s.RadialParent <= k : i <= k;
        }

        /// <summary>Следующая секция пакета над k (радиальные группы пропускаются): её взводит Separate с IgniteNext.</summary>
        public int NextCore(int k)
        {
            int i = k + 1;
            while (i < Sections.Count && Sections[i].IsRadial) i++;
            return i;
        }

        /// <summary>Пакет с параллельной работой двигателей: Δv считается совместным прожигом, а не по одной секции.</summary>
        public bool Parallel
        {
            get
            {
                foreach (var s in Sections) if (s.IsRadial) return true;
                foreach (var a in Sequence) if (a.WithPrevious) return true;
                return false;
            }
        }

        /// <summary>
        /// Проект одного блока радиальной группы i — отдельного борта после отделения: 1/N массы, топлива, двигателей.
        /// Общий для всех N блоков: состояние у каждого своё (Vessel), проект только описывает.
        /// </summary>
        public VesselDesign RadialPiece(int i)
        {
            var s = Sections[i];
            int n = Math.Max(1, s.RadialCount);
            var p = s.Clone();
            p.RadialCount = 0;
            p.RadialParent = 0;
            p.RadialOffset = p.RadialLift = 0;
            p.DryMass /= n;
            p.Propellant /= n;
            p.EngineCount /= n;
            p.RcsTorque /= n;
            p.FinArea /= n;
            p.ParachuteArea /= n;
            var d = new VesselDesign { Name = s.Name };
            d.Sections.Add(p);
            return d;
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

            // Исторические пакеты — строго последовательные: их цифры (и тесты) считаются по-старому, секция за секцией.
            if (Parallel) return ParallelStats(attached, prop, fromStage, runningNow);
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

        /// <summary>
        /// Совместный прожиг (ускорители + ядро, как в KSP): все работающие двигатели жгут одновременно, участок кончается
        /// на первой выработке. Пилот, по допущению, жмёт пробел сразу на выработке — следующая группа шагов идёт тут же,
        /// а оставшиеся двигатели продолжают. Строка статистики — одна группа (одно нажатие пробела).
        /// </summary>
        List<StageStats> ParallelStats(bool[] attached, double[] prop, int fromStage, bool[] runningNow)
        {
            var res = new List<StageStats>();
            int n = Sections.Count;
            var running = new bool[n];
            if (runningNow != null)
                for (int i = 0; i < n; i++) running[i] = runningNow[i] && attached[i];

            double AttachedMass()
            {
                double m = 0;
                for (int i = 0; i < n; i++)
                    if (attached[i]) m += Sections[i].DryMass + prop[i];
                return m;
            }

            void Burn(bool toEnd)
            {
                StageStats st = null;
                var names = new List<string>();
                while (true)
                {
                    double tv = 0, ts = 0, flow = 0, dt = double.PositiveInfinity;
                    for (int i = 0; i < n; i++)
                    {
                        if (!running[i] || !attached[i] || prop[i] <= 0) { running[i] = false; continue; }
                        var s = Sections[i];
                        double f = s.Engine.MassFlow * s.EngineCount;
                        tv += s.Engine.ThrustVac * s.EngineCount;
                        ts += s.Engine.Thrust(101325) * s.EngineCount;
                        flow += f;
                        dt = Math.Min(dt, prop[i] / f);
                        if (!names.Contains(s.Name)) names.Add(s.Name);
                    }
                    if (flow <= 0) break;
                    double m0 = AttachedMass(), m1 = m0 - flow * dt;
                    double ln = Math.Log(m0 / m1);
                    if (st == null)
                        st = new StageStats
                        {
                            StartMass = m0,
                            TwrVac = tv / (m0 * Constants.G0),
                            TwrSL = ts / (m0 * Constants.G0),
                        };
                    // Эффективный УИ связки — суммарная тяга на суммарный расход.
                    st.DeltaVVac += tv / flow * ln;
                    st.DeltaVSL += ts / flow * ln;
                    st.BurnTime += dt;
                    st.EndMass = m1;
                    for (int i = 0; i < n; i++)
                    {
                        if (!running[i]) continue;
                        var s = Sections[i];
                        prop[i] -= s.Engine.MassFlow * s.EngineCount * dt;
                        if (prop[i] <= 1e-6 * Math.Max(1, s.Propellant)) { prop[i] = 0; running[i] = false; }
                    }
                    if (!toEnd) break;
                }
                if (st == null) return;
                st.Name = string.Join(" + ", names);
                res.Add(st);
            }

            void Ignite(int i)
            {
                if (i < n && attached[i] && Sections[i].HasEngine && prop[i] > 0) running[i] = true;
            }

            Burn(fromStage >= Sequence.Count);
            int k = fromStage;
            while (k < Sequence.Count)
            {
                do
                {
                    var a = Sequence[k++];
                    switch (a.Type)
                    {
                        case StageActionType.Ignite:
                            Ignite(a.Section);
                            break;
                        case StageActionType.Separate:
                            for (int i = 0; i < n; i++)
                                if (DetachedBy(i, a.Section)) attached[i] = running[i] = false;
                            if (a.IgniteNext) Ignite(NextCore(a.Section));
                            break;
                        case StageActionType.JettisonFairing:
                            attached[a.Section] = running[a.Section] = false;
                            break;
                    }
                } while (k < Sequence.Count && Sequence[k].WithPrevious);
                Burn(k >= Sequence.Count);
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
                case "sputnik": return R7Sputnik();
                case "vostok": return R7Vostok();
                case "luna": return R7Luna();
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
