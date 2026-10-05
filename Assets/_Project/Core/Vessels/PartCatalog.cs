using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    public enum PartCategory
    {
        Command,
        Tank,
        Engine,
        /// <summary>Разделители, переходники, стыковочные узлы.</summary>
        Coupling,
        /// <summary>Обтекатели, носовые конусы, стабилизаторы.</summary>
        Aero,
        /// <summary>Парашюты, опоры, RCS, панели, антенны, РИТЭГ.</summary>
        Utility,
        Payload,
    }

    /// <summary>
    /// Размеры, которые игрок меняет у детали в конструкторе (§5.4, §4.6): значения живут в CraftPart, пересчёт масс,
    /// топлива и площадей — PartCatalog.Resolve. Флаги — какие из них есть у детали.
    /// </summary>
    [Flags]
    public enum PartParam
    {
        None = 0,
        Length = 1,
        Diameter = 2,
        /// <summary>Диаметр верха (переходник).</summary>
        Top = 4,
        /// <summary>Размах (у киля и стабилизаторов — высота плоскости).</summary>
        Span = 8,
        Chord = 16,
        Sweep = 32,
        Incidence = 64,
        Dihedral = 128,
    }

    /// <summary>Пределы и шаг параметра в конструкторе.</summary>
    public struct ParamRange
    {
        public double Min, Max, Step;
        public ParamRange(double min, double max, double step) { Min = min; Max = max; Step = step; }
        public double Clamp(double v) => Math.Max(Min, Math.Min(Max, v));
    }

    /// <summary>
    /// Деталь конструктора (§5.4). Секция ядра (SectionDef) собирается из деталей блока между разделителями: массы и
    /// топливо складываются, длины — тоже, диаметр — наибольший. Детали длиной 0 — навесные (опоры, RCS, панели).
    /// </summary>
    public sealed class PartDef
    {
        /// <summary>Что меняется в конструкторе; None — деталь фиксированная (двигатели, капсулы — реальные изделия).</summary>
        public PartParam Params;
        /// <summary>Размах и хорда плоскости, м (крыло, оперение, стабилизаторы); у киля Span — высота.</summary>
        public double Span, Chord;
        /// <summary>
        /// Несущая плоскость (§4.6, Aerodynamics): шаблон с рулями и углами. Площадь — Span·Chord, место на корпусе ставит
        /// CraftCompiler: задняя кромка корня — на стыке с деталью ниже в стеке.
        /// </summary>
        public WingDef Wing;
        /// <summary>Шасси (DeployKind.Gear): высота стоек, м — на столько борт выше полосы на пробеге.</summary>
        public double GearHeight;
        public PartDef Clone()
        {
            var c = (PartDef)MemberwiseClone();
            if (Wing != null) c.Wing = Wing.Clone();
            return c;
        }

        public string Id, Name, Description;
        public PartCategory Category;
        /// <summary>Диаметр низа; TopDiameter > 0 — у переходника диаметр верха другой.</summary>
        public double Diameter, TopDiameter;
        public double Length, DryMass, Propellant;
        public Func<EngineDef> Engine;
        /// <summary>Камер (двигателей) в детали: связка 9×, 5× считается одной деталью.</summary>
        public int EngineCount = 1;
        public bool UllageMotors;
        public int Crew;
        public double RcsTorque, ParachuteArea, FinArea;
        /// <summary>Допустимый тепловой поток, Вт/м²; 0 — по умолчанию секции (обшивка ступени).</summary>
        public double MaxHeatFlux;
        public double DragScale = 1;
        public bool Decoupler, RadialDecoupler, Fairing, NoseCone, LandingLegs, DockingPort, Sphere;
        public SectionModel Model;

        public bool HasEngine => Engine != null;
        public double Top => TopDiameter > 0 ? TopDiameter : Diameter;
        public double MaxDiameter => Math.Max(Diameter, Top);
        /// <summary>Занимает строку в списке ступеней: двигатель, разделитель, обтекатель, парашют.</summary>
        public bool Stageable => HasEngine || Decoupler || Fairing || ParachuteArea > 0;
        public double Mass => DryMass + Propellant;
    }

    /// <summary>
    /// Каталог деталей §5.4: сетка диаметров 1; 2; 3,7; 5; 7,5; 10 м, переходники между соседними. Двигатели — по открытым
    /// данным реальных ЖРД/РДТТ (как и исторические пресеты, HistoricRockets.cs).
    /// </summary>
    public static class PartCatalog
    {
        public static readonly double[] Diameters = { 1, 2, 3.7, 5, 7.5, 10 };

        /// <summary>
        /// Насыпная плотность пары керосин–кислород, кг/м³, и заполнение объёма бака (днища, газовая подушка). Проверка:
        /// бак Ø3,7 × 41 м даёт 386 т — у первой ступени «Кара-1» (класс Falcon 9) 396 т.
        /// </summary>
        const double KeroloxDensity = 1030, TankFill = 0.85;
        /// <summary>Сухая масса бака в долях топлива: у первой ступени класса Falcon 9 ≈ 5,5 % без двигателей.</summary>
        const double TankDryFraction = 0.055;
        /// <summary>Погонная масса оболочки обтекателя, кг/м² боковой поверхности: Ø5,2 × 13 м → 1,9 т, как у «Кара-1».</summary>
        public const double FairingAreal = 9;

        static List<PartDef> all;
        static Dictionary<string, PartDef> byId;

        public static IReadOnlyList<PartDef> All
        {
            get
            {
                if (all == null) Build();
                return all;
            }
        }

        public static PartDef Get(string id)
        {
            if (all == null) Build();
            return id != null && byId.TryGetValue(id, out var p) ? p : null;
        }

        // ---------------------------------------------------------------- параметры деталей (§5.4)

        /// <summary>Порядок параметров в окне конструктора.</summary>
        public static readonly PartParam[] ParamOrder =
        {
            PartParam.Length, PartParam.Diameter, PartParam.Top, PartParam.Span, PartParam.Chord,
            PartParam.Sweep, PartParam.Incidence, PartParam.Dihedral,
        };

        /// <summary>
        /// Пределы и шаг кнопок ±. Длина ≤ 60 м и диаметр ≤ 10 м — пара с CraftCompiler.PadMaxHeight/PadMaxWidth (130 × 30 м):
        /// один бак не должен сам по себе выходить за площадку. Размах ≤ 40 м — тоже под PadMaxWidth с запасом на ошибку
        /// (проверка всё равно в Check). Стреловидность ≤ 70° — дальше Aerodynamics.CLα уходит в ноль (cos Λ).
        /// </summary>
        public static ParamRange Range(PartParam p)
        {
            switch (p)
            {
                case PartParam.Length: return new ParamRange(0.5, 60, 0.5);
                case PartParam.Diameter:
                case PartParam.Top: return new ParamRange(0.5, 10, 0.1);
                case PartParam.Span: return new ParamRange(0.5, 40, 0.5);
                case PartParam.Chord: return new ParamRange(0.25, 15, 0.25);
                case PartParam.Sweep: return new ParamRange(0, 70, 5);
                case PartParam.Incidence: return new ParamRange(-5, 10, 0.5);
                case PartParam.Dihedral: return new ParamRange(-10, 15, 1);
                default: return new ParamRange(0, 0, 0);
            }
        }

        public static string ParamName(PartParam p)
        {
            switch (p)
            {
                case PartParam.Length: return "Длина, м";
                case PartParam.Diameter: return "Диаметр, м";
                case PartParam.Top: return "Диаметр верха, м";
                case PartParam.Span: return "Размах, м";
                case PartParam.Chord: return "Хорда, м";
                case PartParam.Sweep: return "Стреловидность, °";
                case PartParam.Incidence: return "Угол установки, °";
                case PartParam.Dihedral: return "Поперечное V, °";
                default: return p.ToString();
            }
        }

        /// <summary>Текущее значение параметра у детали (PartDef — уже из Resolve).</summary>
        public static double Value(PartDef p, PartParam param)
        {
            switch (param)
            {
                case PartParam.Length: return p.Length;
                case PartParam.Diameter: return p.Diameter;
                case PartParam.Top: return p.Top;
                case PartParam.Span: return p.Span;
                case PartParam.Chord: return p.Chord;
                case PartParam.Sweep: return p.Wing?.Sweep ?? 0;
                case PartParam.Incidence: return p.Wing?.Incidence ?? 0;
                case PartParam.Dihedral: return p.Wing?.Dihedral ?? 0;
                default: return 0;
            }
        }

        /// <summary>
        /// Погонные массы плоскостей, кг/м²: крыло с элевонами и силовым набором ≈40 (у лёгких самолётов 15–25, у орбитера с
        /// ТЗП — до 100+), киль и оперение ≈30, стабилизаторы ракеты ≈75 (каталожные 4 м² → 300 кг).
        /// </summary>
        const double WingAreal = 40, FinPlaneAreal = 30, FinsAreal = 75;
        /// <summary>Носовой конус: масса 20·d·L (при L = 1,5·d — прежние 30·d²).</summary>
        const double NoseAreal = 20;

        static readonly Dictionary<string, PartDef> resolved = new Dictionary<string, PartDef>();

        /// <summary>
        /// Деталь стека с размерами игрока: клон каталожной с пересчитанными массой, топливом, площадью (§5.4). Без
        /// параметров — сама каталожная деталь. Значения обрезаются по Range. Кешируется по ключу (Id + значения).
        /// </summary>
        public static PartDef Resolve(CraftPart cp)
        {
            var b = Get(cp?.Id);
            if (b == null || b.Params == PartParam.None || !cp.HasParams) return b;
            string key = cp.ParamKey();
            if (resolved.TryGetValue(key, out var r)) return r;
            r = b.Clone();
            double P(PartParam p, double def)
            {
                if ((b.Params & p) == 0) return def;
                double v = cp.Get(p);
                return double.IsNaN(v) ? def : Range(p).Clamp(v);
            }
            r.Length = P(PartParam.Length, b.Length);
            r.Diameter = P(PartParam.Diameter, b.Diameter);
            if ((b.Params & PartParam.Top) != 0) r.TopDiameter = P(PartParam.Top, b.Top);
            r.Span = P(PartParam.Span, b.Span);
            r.Chord = P(PartParam.Chord, b.Chord);
            if (r.Wing != null)
            {
                r.Wing.Sweep = P(PartParam.Sweep, b.Wing.Sweep);
                r.Wing.Incidence = P(PartParam.Incidence, b.Wing.Incidence);
                r.Wing.Dihedral = P(PartParam.Dihedral, b.Wing.Dihedral);
            }
            Recompute(r);
            resolved[key] = r;
            return r;
        }

        /// <summary>Массы, топливо, площади и имя по размерам детали. Те же формулы, что строят каталог (Build).</summary>
        static void Recompute(PartDef r)
        {
            double d = r.Diameter, len = r.Length;
            if (r.Wing != null)
            {
                double area = r.Span * r.Chord;
                r.Wing.Area = area;
                r.Wing.Span = r.Span;
                r.DryMass = Math.Round((r.Wing.Vertical ? FinPlaneAreal : WingAreal) * area);
                r.Description = $"S = {area:F1} м², удлинение {r.Wing.AspectRatio:F1}";
            }
            else if (r.FinArea > 0)
            {
                // 4 стабилизатора: площадь — сумма, Span — высота одного над обшивкой.
                r.FinArea = 4 * r.Span * r.Chord;
                r.DryMass = Math.Round(FinsAreal * r.FinArea);
                r.Description = $"4 × {r.Span:0.##} × {r.Chord:0.##} м, всего {r.FinArea:F1} м²";
            }
            else if (r.Category == PartCategory.Tank)
            {
                r.Propellant = TankPropellant(d, len);
                r.DryMass = Math.Round(r.Propellant * TankDryFraction);
                r.Name = $"Бак Ø{D(d)} × {D(len)} м";
                r.Description = $"{r.Propellant / 1000:F1} т топлива";
            }
            else if (r.Decoupler)
            {
                r.DryMass = Math.Round(50 * d * d);
                r.Name = $"Разделитель Ø{D(d)} м";
            }
            else if (r.Fairing)
            {
                r.DryMass = Math.Round(40 * d);
                r.Name = $"Обтекатель Ø{D(d)} м";
            }
            else if (r.NoseCone)
            {
                r.DryMass = Math.Round(NoseAreal * d * len);
                // Тупой конус тормозит сильнее: каталожный (L = 1,5·d) — 0,6, полусфера (L = 0,5·d) — 0,9, игла — 0,4.
                r.DragScale = Math.Max(0.4, Math.Min(1, 0.6 + 0.3 * (1.5 - len / d)));
                r.Name = $"Носовой конус Ø{D(d)} × {D(len)} м";
            }
            else if (r.TopDiameter > 0)
            {
                double t = r.TopDiameter;
                r.DryMass = AdapterMass(d, t, len);
                r.Name = $"Переходник Ø{D(d)} → {D(t)} м";
                r.Description = $"Длина {D(len)} м";
            }
        }

        /// <summary>Топливо бака: TankFill·π·d²/4·L·ρ, с округлением до 10 кг.</summary>
        static double TankPropellant(double d, double len) =>
            Math.Round(TankFill * Math.PI * d * d * 0.25 * len * KeroloxDensity / 10) * 10;

        /// <summary>
        /// Масса переходника: 60·((a + b)/2)² у каталожного длиной |b − a|, пропорционально длине (не короче 0,5 м в расчёте
        /// — у переходника между равными диаметрами разницы нет).
        /// </summary>
        static double AdapterMass(double a, double b, double len) =>
            Math.Round(60 * (a + b) * (a + b) / 4 * Math.Max(0.3, len / Math.Max(0.5, Math.Abs(b - a))));

        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        /// <summary>Число для имени — с запятой; для Id — с точкой (Id живут в JSON сохранений, не зависят от локали).</summary>
        static string D(double d) => d.ToString("0.#", Inv).Replace('.', ',');
        static string I(double d) => d.ToString("0.#", Inv);

        static void Build()
        {
            all = new List<PartDef>();
            byId = new Dictionary<string, PartDef>();

            // ---------------------------------------------------------------- командные модули
            Add(new PartDef
            {
                Id = "cmd-vostok", Name = "СА «Восток»", Category = PartCategory.Command, Diameter = 2.3, Length = 2.3,
                DryMass = 2460, Crew = 1, RcsTorque = 300, ParachuteArea = 600, DragScale = 2.6, MaxHeatFlux = 3e6, Sphere = true,
                Description = "Шар на 1 человека, баллистический спуск, парашют",
            });
            Add(new PartDef
            {
                Id = "cmd-mercury", Name = "Капсула «Меркурий»", Category = PartCategory.Command, Diameter = 1.89, Length = 2.9,
                DryMass = 1300, Crew = 1, RcsTorque = 200, ParachuteArea = 290, DragScale = 1.6, MaxHeatFlux = 3e6,
                Model = SectionModel.Mercury, Description = "1 человек, лёгкая",
            });
            Add(new PartDef
            {
                Id = "cmd-gemini", Name = "Капсула «Джемини»", Category = PartCategory.Command, Diameter = 2.34, Length = 3.4,
                DryMass = 1983, Crew = 2, RcsTorque = 400, ParachuteArea = 515, DragScale = 1.6, MaxHeatFlux = 3e6,
                Model = SectionModel.Gemini, Description = "2 человека",
            });
            Add(new PartDef
            {
                Id = "cmd-apollo", Name = "Командный модуль «Аполлон»", Category = PartCategory.Command, Diameter = 3.9, Length = 3.2,
                DryMass = 5560, Crew = 3, RcsTorque = 2e3, ParachuteArea = 1520, DragScale = 1.3, MaxHeatFlux = 8e6,
                Model = SectionModel.ApolloCM, Description = "3 человека, теплозащита для возвращения от Луны",
            });
            Add(new PartDef
            {
                Id = "probe-core", Name = "Блок управления зонда", Category = PartCategory.Command, Diameter = 1, Length = 0.5,
                DryMass = 150, RcsTorque = 300, Description = "Беспилотное управление и маховики",
            });

            // ---------------------------------------------------------------- баки (керосин + кислород)
            foreach (double d in Diameters)
                foreach (double k in new[] { 1.0, 2.0, 4.0 })
                {
                    double len = d * k;
                    double prop = TankPropellant(d, len);
                    Add(new PartDef
                    {
                        Id = $"tank-{I(d)}-{I(k)}", Name = $"Бак Ø{D(d)} × {D(len)} м", Category = PartCategory.Tank, Diameter = d,
                        Length = len, Propellant = prop, DryMass = Math.Round(prop * TankDryFraction), MaxHeatFlux = 2e5,
                        Params = PartParam.Length | PartParam.Diameter,
                        Description = $"{prop / 1000:F1} т топлива",
                    });
                }

            // ---------------------------------------------------------------- двигатели
            Engine("eng-ktdu417", "КТДУ-417", 1, 0.8, 120, () => new EngineDef
            {
                Name = "КТДУ-417", ThrustVac = 18.8e3, ThrustSL = 14e3, IspVac = 313, MinThrottle = 0.25, Ignitions = 6,
            }, "Маневровый, 6 запусков, дросселируется");
            Engine("eng-rl10", "RL10", 1, 2.3, 170, () => new EngineDef
            {
                Name = "RL10", ThrustVac = 66.7e3, ThrustSL = 30e3, IspVac = 444, GimbalDeg = 4, Ignitions = 2, NeedsUllage = true,
            }, "Вакуумный, высокий УИ", ullage: true);
            Engine("eng-sps", "SPS (AJ10)", 2, 2.5, 300, () => new EngineDef
            {
                Name = "SPS", ThrustVac = 91.2e3, ThrustSL = 50e3, IspVac = 314, GimbalDeg = 6, Ignitions = 36,
            }, "Маршевый корабля, 36 запусков");
            Engine("eng-rd0110", "РД-0110", 2, 1.6, 408, () => new EngineDef
            {
                Name = "РД-0110", ThrustVac = 298e3, ThrustSL = 230e3, IspVac = 326, GimbalDeg = 3, NeedsUllage = true,
            }, "Верхняя ступень класса «Союз»", ullage: true);
            Engine("eng-rd107", "РД-107", 2, 2.9, 1155, () => new EngineDef
            {
                Name = "РД-107", ThrustVac = 1000e3, ThrustSL = 813e3, IspVac = 314, GimbalDeg = 4,
            }, "Боковой блок / ядро класса «Союз»");
            Engine("eng-k2v", "РД-К2В", 3.7, 3, 470, VesselPresets.K2Engine, "Вакуумный, 4 запуска", ullage: true);
            Engine("eng-k1x9", "Связка 9 × РД-К1", 3.7, 3, 9 * 470, VesselPresets.K1Engine, "Первая ступень класса Falcon 9", count: 9);
            Engine("eng-rd180", "РД-180", 3.7, 3.6, 5480, () => new EngineDef
            {
                Name = "РД-180", ThrustVac = 4152e3, ThrustSL = 3830e3, IspVac = 338, MinThrottle = 0.47, GimbalDeg = 8,
            }, "Тяжёлый первой ступени");
            Engine("eng-j2", "J-2", 5, 3.4, 1440, () => new EngineDef
            {
                Name = "J-2", ThrustVac = 1033e3, ThrustSL = 486e3, IspVac = 421, GimbalDeg = 7, Ignitions = 2, NeedsUllage = true,
            }, "Водородный верхней ступени, 2 запуска", ullage: true);
            Engine("eng-f1", "F-1", 5, 5.8, 8400, () => new EngineDef
            {
                Name = "F-1", ThrustVac = 7770e3, ThrustSL = 6770e3, IspVac = 304, GimbalDeg = 6,
            }, "Самый мощный однокамерный ЖРД");
            Engine("eng-rd171x3", "Связка 3 × РД-171", 7.5, 4, 3 * 9750, () => new EngineDef
            {
                Name = "РД-171", ThrustVac = 7904e3, ThrustSL = 7255e3, IspVac = 337, MinThrottle = 0.5, GimbalDeg = 6,
            }, "Сверхтяжёлая первая ступень", count: 3);
            Engine("eng-j2x5", "Связка 5 × J-2", 10, 3.4, 5 * 1440, () => new EngineDef
            {
                Name = "J-2", ThrustVac = 1033e3, ThrustSL = 486e3, IspVac = 421, GimbalDeg = 7, NeedsUllage = true,
            }, "Вторая ступень класса S-II", count: 5, ullage: true);
            Engine("eng-f1x5", "Связка 5 × F-1", 10, 5.8, 5 * 8400, () => new EngineDef
            {
                Name = "F-1", ThrustVac = 7770e3, ThrustSL = 6770e3, IspVac = 304, GimbalDeg = 6,
            }, "Первая ступень класса S-IC", count: 5);

            // РДТТ: топливо внутри детали, не глушится (EngineDef.Solid).
            Add(new PartDef
            {
                Id = "srb-1", Name = "РДТТ Ø1 × 9 м", Category = PartCategory.Engine, Diameter = 1, Length = 9, DryMass = 1500,
                Propellant = 10000, MaxHeatFlux = 2e5, Description = "Твердотопливный ускоритель класса Castor 4A",
                Engine = () => new EngineDef { Name = "РДТТ-1", ThrustVac = 480e3, ThrustSL = 430e3, IspVac = 266, Solid = true },
            });
            Add(new PartDef
            {
                Id = "srb-37", Name = "РДТТ Ø3,7 × 45 м", Category = PartCategory.Engine, Diameter = 3.7, Length = 45, DryMass = 87000,
                Propellant = 500000, MaxHeatFlux = 2e5, Description = "Твердотопливный ускоритель класса SRB «Шаттла»",
                Engine = () => new EngineDef { Name = "РДТТ-37", ThrustVac = 13800e3, ThrustSL = 12500e3, IspVac = 268, Solid = true },
            });

            // ---------------------------------------------------------------- разделители и переходники
            foreach (double d in Diameters)
                Add(new PartDef
                {
                    Id = $"dec-{I(d)}", Name = $"Разделитель Ø{D(d)} м", Category = PartCategory.Coupling, Diameter = d, Length = 0.4,
                    DryMass = Math.Round(50 * d * d), Decoupler = true, MaxHeatFlux = 2e5, Params = PartParam.Diameter,
                    Description = "Отделяет всё, что ниже, пиротолкателями",
                });
            Add(new PartDef
            {
                Id = "dec-radial", Name = "Радиальный разделитель", Category = PartCategory.Coupling, Diameter = 0.5, Length = 0,
                DryMass = 100, RadialDecoupler = true, Description = "Крепит боковой блок; толчок наружу при отделении",
            });
            for (int i = 0; i + 1 < Diameters.Length; i++)
            {
                double a = Diameters[i], b = Diameters[i + 1];
                double mass = Math.Round(60 * (a + b) * (a + b) / 4);
                Add(new PartDef
                {
                    Id = $"adp-{I(a)}-{I(b)}", Name = $"Переходник Ø{D(a)} → {D(b)} м", Category = PartCategory.Coupling,
                    Diameter = a, TopDiameter = b, Length = b - a, DryMass = mass, MaxHeatFlux = 2e5,
                    Params = PartParam.Length | PartParam.Diameter | PartParam.Top,
                });
                Add(new PartDef
                {
                    Id = $"adp-{I(b)}-{I(a)}", Name = $"Переходник Ø{D(b)} → {D(a)} м", Category = PartCategory.Coupling,
                    Diameter = b, TopDiameter = a, Length = b - a, DryMass = mass, MaxHeatFlux = 2e5,
                    Params = PartParam.Length | PartParam.Diameter | PartParam.Top,
                });
            }
            Add(new PartDef
            {
                Id = "dock-1", Name = "Стыковочный узел", Category = PartCategory.Coupling, Diameter = 1, Length = 0.4, DryMass = 300,
                DockingPort = true, Description = "Стыковка (V), §6.6",
            });

            // ---------------------------------------------------------------- аэродинамика
            foreach (double d in Diameters)
                if (d >= 2)
                    Add(new PartDef
                    {
                        Id = $"fairing-{I(d)}", Name = $"Обтекатель Ø{D(d)} м", Category = PartCategory.Aero, Diameter = d, Length = 0.3,
                        DryMass = Math.Round(40 * d), Fairing = true, MaxHeatFlux = 2e5, Params = PartParam.Diameter,
                        Description = "Основание: всё выше — под створками; оболочка считается по грузу",
                    });
            foreach (double d in Diameters)
                if (d <= 3.7)
                    Add(new PartDef
                    {
                        Id = $"nose-{I(d)}", Name = $"Носовой конус Ø{D(d)} м", Category = PartCategory.Aero, Diameter = d,
                        TopDiameter = 0.01, Length = 1.5 * d, DryMass = Math.Round(30 * d * d), NoseCone = true, DragScale = 0.6,
                        MaxHeatFlux = 2e5, Params = PartParam.Length | PartParam.Diameter,
                    });
            Add(new PartDef
            {
                Id = "fins", Name = "Стабилизаторы (4 шт)", Category = PartCategory.Aero, Diameter = 1, Length = 0, DryMass = 300,
                FinArea = 4, Span = 1, Chord = 1, Params = PartParam.Span | PartParam.Chord,
                Description = "Сдвигают центр давления назад — статическая устойчивость",
            });
            // Несущие плоскости (§4.6): считаются Aerodynamics по WingDef, а не FinArea. Площадь = размах × хорда,
            // масса — PartCatalog.WingAreal/FinPlaneAreal на м². Крыло и оперение — пара консолей, киль — одна плоскость.
            const PartParam wingParams = PartParam.Span | PartParam.Chord | PartParam.Sweep | PartParam.Incidence | PartParam.Dihedral;
            Add(new PartDef
            {
                Id = "wing", Name = "Крыло", Category = PartCategory.Aero, Diameter = 1, Length = 0, Span = 10, Chord = 3,
                DryMass = Math.Round(WingAreal * 30), Params = wingParams, Description = "S = 30 м², с элевонами",
                // Cd0 0,012 — профиль средней толщины без ТЗП (у орбитера с плитками 0,02). Элевоны 15 % площади.
                Wing = new WingDef { Name = "Крыло", Sweep = 30, Incidence = 1, Dihedral = 3, Cd0 = 0.012, ControlFraction = 0.15, ControlMaxDeg = 20 },
            });
            Add(new PartDef
            {
                Id = "wing-tail", Name = "Горизонтальное оперение", Category = PartCategory.Aero, Diameter = 1, Length = 0,
                Span = 5, Chord = 1.5, DryMass = Math.Round(FinPlaneAreal * 7.5), Params = wingParams,
                Description = "S = 7,5 м², руль высоты",
                // Цельноповоротное — руль на трети площади: оперение и держит, и рулит тангажом.
                Wing = new WingDef { Name = "Стабилизатор", Sweep = 30, Cd0 = 0.01, ControlFraction = 0.35, ControlMaxDeg = 25 },
            });
            Add(new PartDef
            {
                Id = "wing-fin", Name = "Киль", Category = PartCategory.Aero, Diameter = 1, Length = 0, Span = 4, Chord = 3,
                DryMass = Math.Round(FinPlaneAreal * 12), Params = PartParam.Span | PartParam.Chord | PartParam.Sweep,
                Description = "S = 12 м², руль направления; Span — высота над обшивкой",
                Wing = new WingDef { Name = "Киль", Vertical = true, Sweep = 40, Cd0 = 0.01, ControlFraction = 0.25, ControlMaxDeg = 25 },
            });
            Add(new PartDef
            {
                Id = "gear", Name = "Шасси", Category = PartCategory.Utility, Diameter = 1, Length = 0, DryMass = 1500,
                // Стойки 1,8 м — как у орбитера (WingedRockets.Orbiter): хвост не чиркает о полосу при α ≈ 14° выравнивания.
                GearHeight = 1.8, Description = "Посадка на полосу с пробегом; G — выпуск",
            });

            // ---------------------------------------------------------------- оборудование
            Add(new PartDef
            {
                Id = "chute", Name = "Парашют", Category = PartCategory.Utility, Diameter = 1, Length = 0.4, DryMass = 120,
                ParachuteArea = 400, Description = "Купол 400 м²",
            });
            Add(new PartDef
            {
                Id = "legs", Name = "Посадочные опоры", Category = PartCategory.Utility, Diameter = 1, Length = 0, DryMass = 250,
                LandingLegs = true, Description = "Мягкая посадка на тело без атмосферы; G — выпуск и уборка",
            });
            Add(new PartDef
            {
                Id = "rcs", Name = "Блок RCS", Category = PartCategory.Utility, Diameter = 1, Length = 0, DryMass = 60,
                RcsTorque = 2e3, Description = "Ориентация: +2 кН·м",
            });
            Add(new PartDef
            {
                Id = "solar", Name = "Солнечные панели", Category = PartCategory.Utility, Diameter = 1, Length = 0, DryMass = 45,
            });
            Add(new PartDef
            {
                Id = "antenna", Name = "Антенна", Category = PartCategory.Utility, Diameter = 1, Length = 0, DryMass = 15,
            });
            Add(new PartDef
            {
                Id = "rtg", Name = "РИТЭГ", Category = PartCategory.Utility, Diameter = 1, Length = 0, DryMass = 45,
            });

            // ---------------------------------------------------------------- полезная нагрузка
            Add(new PartDef
            {
                Id = "pl-sputnik", Name = "ПС-1", Category = PartCategory.Payload, Diameter = 0.58, Length = 0.58, DryMass = 83.6,
                RcsTorque = 5, Model = SectionModel.Sputnik,
            });
            Add(new PartDef
            {
                Id = "pl-1t", Name = "Макет ПН 1 т", Category = PartCategory.Payload, Diameter = 1, Length = 1.5, DryMass = 1000,
            });
            Add(new PartDef
            {
                Id = "pl-10t", Name = "Макет ПН 10 т", Category = PartCategory.Payload, Diameter = 3.7, Length = 6, DryMass = 10000,
                RcsTorque = 2e3,
            });
        }

        static void Engine(string id, string name, double d, double len, double mass, Func<EngineDef> eng, string desc,
            int count = 1, bool ullage = false)
        {
            var e = eng();
            Add(new PartDef
            {
                Id = id, Name = name, Category = PartCategory.Engine, Diameter = d, Length = len, DryMass = mass, Engine = eng,
                EngineCount = count, UllageMotors = ullage, MaxHeatFlux = 2e5,
                Description = $"{desc}. Тяга {e.ThrustSL * count / 1000:F0}/{e.ThrustVac * count / 1000:F0} кН, УИ {e.IspSL:F0}/{e.IspVac:F0} с",
            });
        }

        static void Add(PartDef p)
        {
            if (p.Wing != null)
            {
                p.Wing.Area = p.Span * p.Chord;
                p.Wing.Span = p.Span;
            }
            all.Add(p);
            byId[p.Id] = p;
        }
    }
}
