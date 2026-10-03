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
    /// Деталь конструктора (§5.4). Секция ядра (SectionDef) собирается из деталей блока между разделителями: массы и
    /// топливо складываются, длины — тоже, диаметр — наибольший. Детали длиной 0 — навесные (опоры, RCS, панели).
    /// </summary>
    public sealed class PartDef
    {
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
                    double prop = Math.Round(TankFill * Math.PI * d * d * 0.25 * len * KeroloxDensity / 10) * 10;
                    Add(new PartDef
                    {
                        Id = $"tank-{I(d)}-{I(k)}", Name = $"Бак Ø{D(d)} × {D(len)} м", Category = PartCategory.Tank, Diameter = d,
                        Length = len, Propellant = prop, DryMass = Math.Round(prop * TankDryFraction), MaxHeatFlux = 2e5,
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
                    DryMass = Math.Round(50 * d * d), Decoupler = true, MaxHeatFlux = 2e5,
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
                });
                Add(new PartDef
                {
                    Id = $"adp-{I(b)}-{I(a)}", Name = $"Переходник Ø{D(b)} → {D(a)} м", Category = PartCategory.Coupling,
                    Diameter = b, TopDiameter = a, Length = b - a, DryMass = mass, MaxHeatFlux = 2e5,
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
                        DryMass = Math.Round(40 * d), Fairing = true, MaxHeatFlux = 2e5,
                        Description = "Основание: всё выше — под створками; оболочка считается по грузу",
                    });
            foreach (double d in Diameters)
                if (d <= 3.7)
                    Add(new PartDef
                    {
                        Id = $"nose-{I(d)}", Name = $"Носовой конус Ø{D(d)} м", Category = PartCategory.Aero, Diameter = d,
                        TopDiameter = 0.01, Length = 1.5 * d, DryMass = Math.Round(30 * d * d), NoseCone = true, DragScale = 0.6,
                        MaxHeatFlux = 2e5,
                    });
            Add(new PartDef
            {
                Id = "fins", Name = "Стабилизаторы (4 шт)", Category = PartCategory.Aero, Diameter = 1, Length = 0, DryMass = 300,
                FinArea = 4, Description = "Сдвигают центр давления назад — статическая устойчивость",
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
            all.Add(p);
            byId[p.Id] = p;
        }
    }
}
