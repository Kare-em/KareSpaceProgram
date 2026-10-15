using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>Один участок бюджета Δv: что делаем и сколько это стоит, м/с.</summary>
    public struct BudgetLeg
    {
        public string Name;
        public double DeltaV;
        public BudgetLeg(string name, double dv) { Name = name; DeltaV = dv; }
    }

    /// <summary>Оценка Δv, нужного миссии (по вакуумному Δv ракеты, потери на выведении уже внутри).</summary>
    public sealed class DeltaVBudget
    {
        public string Mission;
        public readonly List<BudgetLeg> Legs = new List<BudgetLeg>();
        /// <summary>Тур с гравманёврами («Вояджеры»): прямой гомановский перелёт — оценка сверху, реальный путь дешевле.</summary>
        public bool Assists;
        public double Total
        {
            get
            {
                double s = 0;
                foreach (var l in Legs) s += l.DeltaV;
                return s;
            }
        }
    }

    /// <summary>
    /// Инженерный расчёт конструктора (GDD §5.4, как Kerbal Engineer у KSP): сколько Δv нужно выбранной миссии и где
    /// ракета не потянет. Чистая оценка гомановскими переходами по круговым орбитам — без планировщика и симуляции,
    /// чтобы считаться на каждую правку. Числа сверены с историческими ракетами тестом builder.
    /// </summary>
    public static class CraftAnalysis
    {
        /// <summary>Опорная низкая орбита, м: на неё выводят перед перелётом, если миссия не задаёт свою.</summary>
        public const double ParkingAltitude = 200e3;
        /// <summary>
        /// Потери выведения на НОО, м/с: гравитационные, аэродинамические, на управление и недобор тяги у Земли
        /// (Δv ракеты считаем по вакууму). Классика — 1,5–2 км/с; 1 800 дают «Восток» с запасом ~5 %, как у исторических.
        /// Пара: тест builder — каждая штатная ракета орбитальной миссии ≥ бюджета.
        /// </summary>
        public const double AscentLoss = 1800;
        /// <summary>Потери суборбитального подскока, м/с: короткий почти вертикальный прожиг в плотной атмосфере.</summary>
        public const double SuborbitalLoss = 600;
        /// <summary>Сход с низкой орбиты к входу в атмосферу, м/с (тормозной импульс «Востока» ≈ 130–150).</summary>
        public const double DeorbitDv = 150;
        /// <summary>Сближение со станцией и причаливание, м/с: фазировка и сброс скорости у цели (§6.6).</summary>
        public const double RendezvousDv = 250;
        /// <summary>Потери при посадке/взлёте без атмосферы, м/с: гравитационные на торможении и зависание перед касанием.</summary>
        public const double VacuumLandingLoss = 250;
        /// <summary>Посадочный импульс на тело с атмосферой, м/с: основное гасит торможение и парашют (Земля, Титан, Марс).</summary>
        public const double AtmoLandingDv = 200;
        /// <summary>Высота окололунной (и другой целевой) орбиты по умолчанию над радиусом тела — доля радиуса.</summary>
        const double DefaultOrbitFraction = 0.06;

        /// <summary>Общий Δv ракеты по вакууму — его и сравниваем с бюджетом (потери выведения в бюджете).</summary>
        public static double Available(List<StageStats> stats)
        {
            double s = 0;
            if (stats != null) foreach (var st in stats) s += st.DeltaVVac;
            return s;
        }

        static double Circ(double mu, double r) => Math.Sqrt(mu / r);
        static double Esc(double mu, double r) => Math.Sqrt(2 * mu / r);
        /// <summary>Полный Δv гомановского перехода между круговыми r1 и r2 (оба импульса).</summary>
        static double Hohmann(double mu, double r1, double r2)
        {
            double a = (r1 + r2) / 2;
            return Math.Abs(Math.Sqrt(mu * (2 / r1 - 1 / a)) - Circ(mu, r1)) + Math.Abs(Circ(mu, r2) - Math.Sqrt(mu * (2 / r2 - 1 / a)));
        }

        /// <summary>Импульс из круговой r на гиперболу с избытком vinf (и обратно — захват на ту же орбиту).</summary>
        static double Hyperbolic(double mu, double r, double vinf) => Math.Sqrt(vinf * vinf + 2 * mu / r) - Circ(mu, r);

        static double OrbitRadius(CelestialBody b, double t)
        {
            if (b.OrbitModel == OrbitModelType.Fixed) return 0;
            b.LocalStateAt(t, out var r, out _);
            return r.magnitude;
        }

        /// <summary>Предок, что обращается вокруг Солнца (у Титана — Сатурн, у Луны — Земля).</summary>
        static CelestialBody Planet(CelestialBody b)
        {
            while (b?.Parent != null && b.Parent.Parent != null) b = b.Parent;
            return b;
        }

        /// <summary>
        /// Бюджет миссии: выведение, перелёт, захват, посадка, возвращение — по целям def.Objectives по порядку.
        /// null — бюджет не нужен (старт в воздухе у полосы, ApproachStart).
        /// </summary>
        public static DeltaVBudget Budget(MissionDef def, SolarSystem sys)
        {
            if (def == null || sys == null || def.Approach != null) return null;
            var site = SolarSystem.GetSite(def.SiteId);
            var home = sys.Get(site?.BodyId ?? "earth") ?? sys.Get("earth");
            if (home == null) return null;
            double t = def.StartTime;
            var bud = new DeltaVBudget { Mission = def.Title, Assists = MissionCatalog.IsTour(def) };
            double muH = home.Mu, rH = home.Radius;

            // Самая дальняя цель: её планета дальше всех от Солнца; спутник чужой планеты — глубже самой планеты.
            CelestialBody target = null;
            double far = -1;
            foreach (var o in def.Objectives)
            {
                var b = sys.Get(o.Body);
                if (b == null || b == home) continue;
                var p = Planet(b);
                double d = p == Planet(home) ? OrbitRadius(b, t) * 1e-9 : OrbitRadius(p, t) + (b != p ? 1 : 0);
                if (d > far) { far = d; target = b; }
            }
            bool orbital = target != null || def.StationSection >= 0;
            double park = 0;
            foreach (var o in def.Objectives)
                if (o.Type == ObjectiveType.Orbit && sys.Get(o.Body) == home) { orbital = true; park = Math.Max(park, o.Min); }
                else if (o.Type == ObjectiveType.Dock) orbital = true;
            if (def.StationSection >= 0) park = Math.Max(park, def.StationAltitude);

            // Вращение Земли помогает пуску на восток: v·cos(широты) — у Байконура ≈ 320 м/с, у мыса Канаверал ≈ 410.
            double rot = Math.Abs(home.RotationRate) * rH * Math.Cos((site?.Latitude ?? 0) * Math.PI / 180);

            if (!orbital)
            {
                // Подскок: вертикально на высоту h — энергия mu(1/R − 1/(R+h)), плюс потери; дальше баллистика и спуск.
                double h = 100e3;
                foreach (var o in def.Objectives) if (o.Type == ObjectiveType.Altitude) h = Math.Max(h, o.Min);
                bud.Legs.Add(new BudgetLeg($"Подъём на {h / 1000:0} км", Math.Sqrt(2 * muH * (1 / rH - 1 / (rH + h))) + SuborbitalLoss));
                foreach (var o in def.Objectives)
                    if (o.Type == ObjectiveType.Landing && sys.Get(o.Body) == home) bud.Legs.Add(new BudgetLeg("Посадочный импульс", AtmoLandingDv));
                return bud;
            }

            if (park <= 0) park = ParkingAltitude;
            double low = Math.Min(park, ParkingAltitude), r1 = rH + park;
            bud.Legs.Add(new BudgetLeg($"Выведение на орбиту {low / 1000:0} км", Circ(muH, rH + low) + AscentLoss - rot));
            if (park > low + 1) bud.Legs.Add(new BudgetLeg($"Подъём орбиты до {park / 1000:0} км", Hohmann(muH, rH + low, r1)));
            if (def.StationSection >= 0 || def.Objectives.Exists(o => o.Type == ObjectiveType.Dock))
                bud.Legs.Add(new BudgetLeg("Сближение и стыковка", RendezvousDv));

            // Перелёт к цели: гиперболический избыток vinf на прилёте — от него считаются захват и прямой спуск.
            double vinf = 0, vinfBack = 0;
            CelestialBody arrival = null;
            if (target != null)
            {
                var planet = Planet(target);
                arrival = planet == Planet(home) ? target : planet;
                if (arrival.Parent == home)
                {
                    // Спутник Земли: гоман с НОО на радиус его орбиты, на прилёте — разность с его круговой скоростью.
                    double r2 = OrbitRadius(arrival, t), a = (r1 + r2) / 2;
                    bud.Legs.Add(new BudgetLeg($"Разгон к {arrival.Name}", Math.Sqrt(muH * (2 / r1 - 1 / a)) - Circ(muH, r1)));
                    vinf = Math.Abs(Circ(muH, r2) - Math.Sqrt(muH * (2 / r2 - 1 / a)));
                }
                else
                {
                    // Другая планета: гоман по солнечным орбитам, уход с НОО — гипербола с избытком vDep.
                    var sun = home.Parent;
                    double rE = OrbitRadius(home, t), rP = OrbitRadius(arrival, t), a = (rE + rP) / 2, mu = sun.Mu;
                    double vDep = Math.Abs(Math.Sqrt(mu * (2 / rE - 1 / a)) - Circ(mu, rE));
                    vinf = Math.Abs(Circ(mu, rP) - Math.Sqrt(mu * (2 / rP - 1 / a)));
                    bud.Legs.Add(new BudgetLeg($"Отлёт к {arrival.Name}" + (bud.Assists ? " (без гравманёвров)" : ""), Hyperbolic(muH, r1, vDep)));
                    vinfBack = vDep;
                }
                if (arrival.Parent == home) vinfBack = vinf;
            }

            // Цели по порядку: где корабль сейчас — на перелёте, на орбите тела или на поверхности.
            CelestialBody at = null;
            double atR = 0;
            bool landed = false, inOrbit = false, deorbited = false;
            foreach (var o in def.Objectives)
            {
                var b = sys.Get(o.Body) ?? home;
                if (b == home)
                {
                    if (o.Type == ObjectiveType.Return || o.Type == ObjectiveType.Runway || o.Type == ObjectiveType.Landing)
                    {
                        if (at != null)
                        {
                            // С поверхности цели — сначала на низкую орбиту над ней, затем уход домой (у Аполлона — ТЕI).
                            if (landed)
                            {
                                bud.Legs.Add(new BudgetLeg($"Взлёт с {at.Name}", AscentFrom(at, atR)));
                                landed = false;
                                inOrbit = true;
                            }
                            if (inOrbit) bud.Legs.Add(new BudgetLeg($"Отлёт с орбиты {at.Name} домой", Hyperbolic(at.Mu, atR, vinfBack)));
                            at = null;
                        }
                        else if (!deorbited)
                        {
                            bud.Legs.Add(new BudgetLeg("Сход с орбиты", DeorbitDv));
                            deorbited = true;
                        }
                        if (o.Type == ObjectiveType.Landing) bud.Legs.Add(new BudgetLeg("Посадочный импульс", AtmoLandingDv));
                    }
                    continue;
                }
                double r = b.Radius + (o.Type == ObjectiveType.Orbit && o.Min > 0 ? o.Min : b.Radius * DefaultOrbitFraction);
                switch (o.Type)
                {
                    case ObjectiveType.Orbit:
                        if (landed && at == b) bud.Legs.Add(new BudgetLeg($"Взлёт на орбиту {b.Name}", AscentFrom(b, r)));
                        else if (!inOrbit) bud.Legs.Add(new BudgetLeg($"Выход на орбиту {b.Name}", Capture(b, arrival, r, o.Max, vinf, t)));
                        at = b; atR = r; inOrbit = true; landed = false;
                        break;
                    case ObjectiveType.Landing:
                        if (b.HasAtmosphere) bud.Legs.Add(new BudgetLeg($"Посадка на {b.Name} (атмосфера, парашют)", AtmoLandingDv));
                        else if (inOrbit && at == b) bud.Legs.Add(new BudgetLeg($"Спуск с орбиты на {b.Name}", Circ(b.Mu, atR) + VacuumLandingLoss));
                        else if (def.LunarOrbit > 0 && b.Parent == home)
                        {
                            // Посадка через окололунную орбиту (MissionDef.LunarOrbit, Аполлон): торможение на орбиту, затем спуск.
                            atR = b.Radius + def.LunarOrbit;
                            bud.Legs.Add(new BudgetLeg($"Выход на орбиту {b.Name}", Capture(b, arrival, atR, 0, vinf, t)));
                            bud.Legs.Add(new BudgetLeg($"Спуск с орбиты на {b.Name}", Circ(b.Mu, atR) + VacuumLandingLoss));
                        }
                        else
                        {
                            // Прямой спуск с трассы (Луна-9, Сервейор): гасим всю скорость падения vinf² + v_esc².
                            double vi = b == arrival ? vinf : 0;
                            bud.Legs.Add(new BudgetLeg($"Посадка на {b.Name} с трассы", Math.Sqrt(vi * vi + Esc(b.Mu, b.Radius) * Esc(b.Mu, b.Radius)) + VacuumLandingLoss));
                        }
                        if (at != b && !(def.LunarOrbit > 0 && b.Parent == home)) atR = b.Radius * (1 + DefaultOrbitFraction);
                        at = b; landed = true; inOrbit = false;
                        break;
                    default:
                        // Пролёт, вход в сферу, съёмка, удар, езда — импульса сверх перелёта не нужно.
                        if (at == null) at = b;
                        break;
                }
            }
            return bud;
        }

        /// <summary>
        /// Захват у тела b с перицентром r. Апоцентр: maxAp над поверхностью, если цель его ограничивает; у газового гиганта —
        /// половина сферы влияния (вытянутая орбита, как у «Кассини»: круговая стоила бы ~10 км/с); иначе — круговая.
        /// Спутник чужой планеты: сперва захват планетой на радиус его орбиты.
        /// </summary>
        static double Capture(CelestialBody b, CelestialBody arrival, double r, double maxAp, double vinf, double t)
        {
            if (b != arrival && arrival != null) return Hyperbolic(arrival.Mu, OrbitRadius(b, t), vinf) + Hyperbolic(b.Mu, r, 0);
            double ra = maxAp > 0 ? b.Radius + maxAp
                : b.IsGasGiant && !double.IsInfinity(b.SoiRadius) ? GiantCaptureSoi * b.SoiRadius : r;
            ra = Math.Max(ra, r);
            double vp = Math.Sqrt(2 * b.Mu * ra / (r * (ra + r)));
            return Math.Sqrt(vinf * vinf + 2 * b.Mu / r) - vp;
        }

        /// <summary>Апоцентр захвата у газового гиганта — доля сферы влияния. Пара: Capture, бюджет «Кассини» ≈ 0,5 км/с на СОИ.</summary>
        const double GiantCaptureSoi = 0.5;

        /// <summary>Взлёт на низкую орбиту r без атмосферы: круговая скорость плюс гравпотери.</summary>
        static double AscentFrom(CelestialBody b, double r) =>
            b.HasAtmosphere ? Circ(b.Mu, r) + AscentLoss : Circ(b.Mu, r) + VacuumLandingLoss;

        /// <summary>Предупреждение о нехватке Δv для миссии (null — хватает). Оценка, поэтому не ошибка: запуск разрешён.</summary>
        public static string CheckBudget(DeltaVBudget bud, List<StageStats> stats)
        {
            if (bud == null) return null;
            double have = Available(stats), need = bud.Total;
            if (have >= need) return null;
            return $"Δv {have:0} м/с < ≈{need:0} м/с для «{bud.Mission}»" + (bud.Assists ? " (без гравманёвров — оценка сверху)" : "") + ": не хватит";
        }

        /// <summary>
        /// Перегрузка экипажа (§4.7): ступень, где тяга на пустых баках держит выше CrewGLimit дольше CrewGTime, убьёт
        /// экипаж без дросселирования (у РДТТ дросселя нет). Без экипажа и при выключенном пределе — молчим.
        /// </summary>
        public static void CheckGLoad(VesselDesign d, List<StageStats> stats, List<string> warnings)
        {
            int crew = 0;
            foreach (var s in d.Sections) crew += s.Crew;
            if (crew == 0 || stats == null) return;
            foreach (var st in stats)
                if (st.HighGTime > FlightPhysics.CrewGTime)
                    warnings.Add($"«{st.Name}»: к концу прожига до {st.MaxAccel / Constants.G0:0.#} g, выше {FlightPhysics.CrewGLimit:0} g — " +
                                 $"{st.HighGTime:0} с: без дросселя экипаж погибнет (§4.7)");
        }
    }
}
