using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Типы условий (GDD §7.2). Пока реализованы те, что нужны эре I; Crew, EVA, Dock, Data и прочие
    /// появятся вместе со своими системами.
    /// </summary>
    public enum ObjectiveType
    {
        /// <summary>Учебное: подняться выше Min над телом.</summary>
        Altitude,
        /// <summary>Орбита: Pe ≥ Min, Ap ≤ Max (0 — без предела), удержать HoldSeconds (−1 — один виток).</summary>
        Orbit,
        /// <summary>Войти в сферу влияния тела.</summary>
        EnterSoi,
        /// <summary>Пройти над телом не выше Max над поверхностью.</summary>
        Flyby,
        /// <summary>Над освещённой обратной стороной Луны не дальше Max от центра (Луна-3, GDD §7.4).</summary>
        FarSidePhoto,
        /// <summary>Достичь поверхности любой скоростью — гибель аппарата здесь и есть успех.</summary>
        Impact,
        /// <summary>Сесть на тело не быстрее MaxSpeed (физика разрушает выше 10 м/с).</summary>
        Landing,
        /// <summary>Вернуть капсулу на тело целой; с экипажем — живым (предел перегрузки §4.7 — в FlightPhysics).</summary>
        Return,
        /// <summary>Проехать по телу не меньше Min метров (луноход, GDD §6.12).</summary>
        Drive,
        /// <summary>Причалить к станции и пробыть в связке HoldSeconds (GDD §6.6, «Союз ТМ-31», Demo-2).</summary>
        Dock,
        /// <summary>Сесть на шасси на полосу Site (Runways) и остановиться, экипаж жив (STS-1, «Буран», GDD §6.4).</summary>
        Runway,
    }

    public sealed class Objective
    {
        public ObjectiveType Type;
        public string Body = "earth";
        public double Min, Max, HoldSeconds;
        public double MaxSpeed = FlightPhysics.CrashSpeed;
        /// <summary>Id полосы для ObjectiveType.Runway (Runways.Get).</summary>
        public string Site;

        public string Describe(SolarSystem sys)
        {
            string b = sys?.Get(Body)?.Name ?? Body;
            switch (Type)
            {
                case ObjectiveType.Altitude: return $"Подняться выше {Min / 1000:F0} км";
                case ObjectiveType.Orbit:
                    return $"Орбита {b}: перицентр ≥ {Min / 1000:F0} км" + (Max > 0 ? $", апоцентр ≤ {Max / 1000:F0} км" : "") +
                           (HoldSeconds < 0 ? ", один виток" : HoldSeconds > 0 ? $", {GameCalendar.FormatDuration(HoldSeconds)}" : "");
                case ObjectiveType.EnterSoi: return $"Войти в сферу влияния: {b}";
                case ObjectiveType.Flyby: return $"Пролёт {b} не выше {Max / 1000:F0} км";
                case ObjectiveType.FarSidePhoto: return $"Снять освещённую обратную сторону: {b}, не дальше {Max / 1000:F0} км";
                case ObjectiveType.Impact: return $"Достичь поверхности: {b}";
                case ObjectiveType.Landing: return $"Мягкая посадка: {b}, не быстрее {MaxSpeed:F0} м/с";
                case ObjectiveType.Drive: return $"Проехать {Min:F0} м: {b}";
                case ObjectiveType.Dock:
                    return "Стыковка со станцией" + (HoldSeconds > 0 ? $", в связке {GameCalendar.FormatDuration(HoldSeconds)}" : "");
                case ObjectiveType.Runway:
                    return $"Посадка на полосу: {Runways.Get(Site)?.Name ?? Site}, остановиться";
                case ObjectiveType.Return:
                    return $"Вернуть капсулу: {b}" + (FlightPhysics.GLoadLimit
                        ? $", экипаж не дольше {FlightPhysics.CrewGTime:F0} с выше {FlightPhysics.CrewGLimit:F0} g" : "");
            }
            return Type.ToString();
        }
    }

    public sealed class MissionDef
    {
        public string Id, Title, Brief, Rival;
        public string SiteId = "baikonur";
        public string DesignId;
        public double StartTime;
        /// <summary>Условия по порядку; иначе — в любом порядке (пролёт и съёмка у Луны-3).</summary>
        public bool Sequential = true;
        /// <summary>Посадка на Луну с окололунной орбиты (м над средним радиусом), 0 — прямой спуск с трассы перелёта,
        /// как у «Луны-9» и «Сервейера-1». LunarPerilune — перицентр, из которого начинается торможение.</summary>
        public double LunarOrbit, LunarPerilune;
        /// <summary>
        /// Станция-цель (GDD §6.6): секция проекта, которая на старте уже летает отдельным бортом (StationSetup.Place);
        /// −1 — станции нет. Орбита круговая: высота, наклонение, опережение по дуге над точкой выведения (°)
        /// на момент StartTime + StationLaunchDelay. Пара: StationLead ↔ ParkingAltitude автопилота (дрейф фаз).
        /// </summary>
        public int StationSection = -1;
        public double StationAltitude, StationInclination, StationLead, StationLaunchDelay = 120;
        public string StationName;
        public readonly List<Objective> Objectives = new List<Objective>();
    }

    public enum MissionStatus
    {
        Active,
        Success,
        Failed,
    }

    /// <summary>Следит за условиями миссии по активному кораблю. Вызывать после каждого шага вселенной.</summary>
    public sealed class MissionTracker
    {
        public readonly MissionDef Def;
        public MissionStatus Status { get; private set; } = MissionStatus.Active;
        public string FailReason { get; private set; }
        public readonly bool[] Done;
        readonly double[] holdStart;
        public event Action<string> Changed;

        public MissionTracker(MissionDef def)
        {
            Def = def;
            int n = def.Objectives.Count;
            Done = new bool[n];
            holdStart = new double[n];
            for (int i = 0; i < n; i++) holdStart[i] = double.NaN;
        }

        /// <summary>Вселенная на дату миссии с ракетой на столе.</summary>
        public static Universe CreateUniverse(MissionDef def, SolarSystem system)
        {
            var u = new Universe(system, def.StartTime);
            var v = u.Launch(VesselPresets.ById(def.DesignId), def.SiteId);
            if (def.StationSection >= 0) StationSetup.Place(u, v, def);
            return u;
        }

        public int CurrentIndex
        {
            get
            {
                for (int i = 0; i < Done.Length; i++)
                    if (!Done[i]) return i;
                return -1;
            }
        }

        public void Update(Universe u)
        {
            if (Status != MissionStatus.Active) return;
            // Игрок увёл управление на другой борт — задачи считаются по борту миссии (§6.13).
            var v = u.MissionVessel ?? u.Active;
            if (v == null) return;
            for (int i = 0; i < Done.Length; i++)
            {
                if (Done[i]) continue;
                var o = Def.Objectives[i];
                string fail = null;
                if (Check(o, i, v, u, ref fail))
                {
                    Done[i] = true;
                    Changed?.Invoke($"Выполнено: {o.Describe(u.System)}");
                }
                else if (fail != null)
                {
                    Fail(fail);
                    return;
                }
                if (Def.Sequential && !Done[i]) break;
            }
            if (CurrentIndex < 0)
            {
                Status = MissionStatus.Success;
                Changed?.Invoke($"Миссия выполнена: {Def.Title}");
                return;
            }
            if (!v.Alive) Fail(v.DestroyReason ?? "Аппарат потерян");
        }

        void Fail(string reason)
        {
            Status = MissionStatus.Failed;
            FailReason = reason;
            Changed?.Invoke($"Миссия провалена: {reason}");
        }

        bool Check(Objective o, int i, Vessel v, Universe u, ref string fail)
        {
            var body = u.System.Get(o.Body);
            if (o.Type == ObjectiveType.Impact)
                return !v.Alive && v.DestroyedOn == body;
            if (!v.Alive) return false;
            bool launched = !double.IsNaN(v.LaunchTime);
            bool onBody = v.Body == body;
            double t = u.Time;

            switch (o.Type)
            {
                case ObjectiveType.Altitude:
                    return onBody && v.Altitude >= o.Min;

                case ObjectiveType.Orbit:
                {
                    bool ok = false;
                    KeplerOrbit orbit = null;
                    if (onBody && !v.IsLanded)
                    {
                        orbit = KeplerOrbit.FromState(v.Position, v.Velocity, body.Mu, t);
                        double pe = orbit.PeriapsisRadius - body.Radius;
                        double ap = orbit.ApoapsisRadius - body.Radius;
                        ok = orbit.IsElliptic && orbit.ApoapsisRadius < body.SoiRadius &&
                             pe >= Math.Max(o.Min, body.AtmosphereTop) && (o.Max <= 0 || ap <= o.Max);
                    }
                    if (!ok)
                    {
                        holdStart[i] = double.NaN;
                        return false;
                    }
                    if (double.IsNaN(holdStart[i])) holdStart[i] = t;
                    double need = o.HoldSeconds < 0 ? orbit.Period : o.HoldSeconds;
                    return t - holdStart[i] >= need;
                }

                case ObjectiveType.EnterSoi:
                    return onBody;

                case ObjectiveType.Flyby:
                {
                    if (!onBody || v.IsLanded) return false;
                    var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, body.Mu, t);
                    // На ускорении момент перицентра можно перешагнуть — засчитываем и уже пройденный.
                    return v.Altitude <= o.Max || orbit.PeriapsisRadius - body.Radius <= o.Max && v.VerticalSpeed > 0;
                }

                case ObjectiveType.FarSidePhoto:
                {
                    if (!onBody || body.Parent == null) return false;
                    double dist = v.Position.magnitude;
                    if (dist > o.Max) return false;
                    // Обратная сторона смотрит от родителя: её центр — по направлению «родитель → тело».
                    var away = body.LocalPosition.normalized;
                    var toSun = (-body.Position).normalized;
                    return Vector3d.Dot(v.Position / dist, away) > 0.5 && Vector3d.Dot(toSun, away) > 0;
                }

                case ObjectiveType.Landing:
                    if (!launched || !onBody || v.Situation != Situation.Landed && v.Situation != Situation.Splashed) return false;
                    return true;

                case ObjectiveType.Drive:
                    return onBody && v.IsLanded && v.DriveDistance >= o.Min;

                case ObjectiveType.Dock:
                {
                    // Состыкован — к борту прицеплены перевёрнутые секции со стыковочным узлом (Vessel.Dock).
                    bool docked = false;
                    for (int k = 0; k < v.Attached.Length; k++)
                        if (v.Attached[k] && v.Flipped[k] && v.Design.Sections[k].DockingPort) docked = true;
                    if (!docked)
                    {
                        holdStart[i] = double.NaN;
                        return false;
                    }
                    if (double.IsNaN(holdStart[i])) holdStart[i] = t;
                    return t - holdStart[i] >= o.HoldSeconds;
                }

                case ObjectiveType.Runway:
                {
                    // Засчитывается после остановки: на пробеге борт ещё может уйти с полосы или взлететь.
                    if (!launched || !onBody || v.Situation != Situation.Landed || v.RollSpeed > 0) return false;
                    if (v.CrewLost)
                    {
                        fail = "Экипаж погиб от перегрузки";
                        return false;
                    }
                    var rw = Runways.At(body, v.AnchorBodyFixed);
                    if (rw == null || o.Site != null && rw.Id != o.Site)
                    {
                        fail = rw == null ? "Посадка вне полосы" : $"Посадка не на ту полосу: {rw.Name}";
                        return false;
                    }
                    return true;
                }

                case ObjectiveType.Return:
                {
                    if (!launched) return false;
                    if (!onBody || !v.IsLanded) return false;
                    if (!v.HasCapsule())
                    {
                        fail = "Капсулы нет на борту";
                        return false;
                    }
                    if (v.CrewLost)
                    {
                        fail = "Экипаж погиб от перегрузки";
                        return false;
                    }
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Миссии эры I (GDD §7.3) в датах реальных стартов — окна запуска и положение Луны настоящие.</summary>
    public static partial class MissionCatalog
    {
        public static readonly List<MissionDef> All = Build();

        public static MissionDef Get(string id) => All.Find(m => m.Id == id);

        static List<MissionDef> Build()
        {
            var list = new List<MissionDef>();

            // Учебная, до §7.3 №1: суборбитальный подскок учит ступеням и парашюту.
            var karman = new MissionDef
            {
                Id = "karman", Title = "Линия Кармана", DesignId = "sounding",
                Brief = "Геофизическая ракета: подняться за 100 км и вернуть капсулу с приборами на парашюте.",
                StartTime = GameCalendar.ToGameTime(1957, 8, 21, 9, 0, 0),
                Rival = "—",
            };
            karman.Objectives.Add(new Objective { Type = ObjectiveType.Altitude, Min = 100000 });
            karman.Objectives.Add(new Objective { Type = ObjectiveType.Return });
            list.Add(karman);

            var sputnik = new MissionDef
            {
                Id = "sputnik", Title = "Спутник", DesignId = "sputnik",
                Brief = "Первый искусственный спутник Земли. Окно — вечер 4 октября 1957 года.",
                StartTime = GameCalendar.ToGameTime(1957, 10, 4, 19, 28, 34),
                Rival = "4 окт 1957",
            };
            sputnik.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Min = 150000, Max = 1500000, HoldSeconds = -1 });
            list.Add(sputnik);

            var mechta = new MissionDef
            {
                Id = "mechta", Title = "Мечта", DesignId = "luna",
                Brief = "Достичь Луны: войти в её сферу влияния. Окно — 2 января 1959 года.",
                StartTime = GameCalendar.ToGameTime(1959, 1, 2, 16, 41, 21),
                Rival = "2 янв 1959 (Луна-1)",
            };
            mechta.Objectives.Add(new Objective { Type = ObjectiveType.EnterSoi, Body = "moon" });
            list.Add(mechta);

            var vympel = new MissionDef
            {
                Id = "vympel", Title = "Вымпел", DesignId = "luna",
                Brief = "Доставить вымпел на поверхность Луны — попасть в неё.",
                StartTime = GameCalendar.ToGameTime(1959, 9, 12, 6, 39, 42),
                Rival = "14 сен 1959 (Луна-2)",
            };
            vympel.Objectives.Add(new Objective { Type = ObjectiveType.Impact, Body = "moon" });
            list.Add(vympel);

            var luna3 = new MissionDef
            {
                Id = "farside", Title = "Пролёт Луны", DesignId = "luna", Sequential = false,
                Brief = "Облететь Луну и снять обратную сторону, пока она освещена Солнцем.",
                StartTime = GameCalendar.ToGameTime(1959, 10, 4, 0, 43, 40),
                Rival = "7 окт 1959 (Луна-3)",
            };
            luna3.Objectives.Add(new Objective { Type = ObjectiveType.Flyby, Body = "moon", Max = 10_000_000 });
            luna3.Objectives.Add(new Objective { Type = ObjectiveType.FarSidePhoto, Body = "moon", Max = 70_000_000 });
            list.Add(luna3);

            var vostok = new MissionDef
            {
                Id = "vostok", Title = "Восток", DesignId = "vostok",
                Brief = "Человек на орбите: один виток, торможение ТДУ, спуск капсулы на парашюте.",
                StartTime = GameCalendar.ToGameTime(1961, 4, 12, 6, 7, 0),
                Rival = "12 апр 1961",
            };
            vostok.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Min = 150000, HoldSeconds = -1 });
            vostok.Objectives.Add(new Objective { Type = ObjectiveType.Return });
            list.Add(vostok);

            var luna9 = new MissionDef
            {
                Id = "luna9", Title = "Луна-9", DesignId = "luna",
                Brief = "Мягкая посадка на Луну: перелёт, торможение с орбиты или прямое снижение.",
                StartTime = GameCalendar.ToGameTime(1966, 1, 31, 11, 41, 37),
                Rival = "3 фев 1966",
            };
            luna9.Objectives.Add(new Objective { Type = ObjectiveType.Landing, Body = "moon" });
            list.Add(luna9);

            var luna17 = new MissionDef
            {
                Id = "luna17", Title = "Луноход-1", DesignId = "luna17",
                Brief = "«Протон-К» с блоком Д: посадить ступень КТ на Луну, съехать по трапам и проехать 100 м.",
                StartTime = GameCalendar.ToGameTime(1970, 11, 10, 14, 44, 1),
                Rival = "17 ноя 1970",
                // Не прямой спуск: 15.11 круговая ~85 км, 16–17.11 КТДУ-417 опустила перицентр до 19 км, оттуда посадка
                // в Море Дождей 17.11 03:46:50 UTC (ru/en-wiki «Луна-17», orbitalfocus.uk).
                LunarOrbit = 85e3, LunarPerilune = 19e3,
            };
            luna17.Objectives.Add(new Objective { Type = ObjectiveType.Landing, Body = "moon" });
            luna17.Objectives.Add(new Objective { Type = ObjectiveType.Drive, Body = "moon", Min = 100 });
            list.Add(luna17);

            // Американские миссии до «Аполлона» включительно (Artemis вне эпохи) — со стартом с Канаверала.
            var juno1 = new MissionDef
            {
                Id = "juno1", Title = "Эксплорер-1", DesignId = "juno1", SiteId = "canaveral",
                Brief = "Juno I: жидкостная ступень выводит на пассивный участок, три связки РДТТ поджигаются в апоцентре.",
                StartTime = GameCalendar.ToGameTime(1958, 2, 1, 3, 48, 0),
                Rival = "1 фев 1958",
            };
            juno1.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Min = 150000, HoldSeconds = -1 });
            list.Add(juno1);

            var freedom7 = new MissionDef
            {
                Id = "freedom7", Title = "Фридом-7", DesignId = "mercury_redstone", SiteId = "canaveral",
                Brief = "Суборбитальный полёт Алана Шепарда: подняться за 100 км и приводниться на парашюте.",
                StartTime = GameCalendar.ToGameTime(1961, 5, 5, 14, 34, 13),
                Rival = "5 мая 1961",
            };
            freedom7.Objectives.Add(new Objective { Type = ObjectiveType.Altitude, Min = 100000 });
            freedom7.Objectives.Add(new Objective { Type = ObjectiveType.Return });
            list.Add(freedom7);

            var friendship7 = new MissionDef
            {
                Id = "friendship7", Title = "Френдшип-7", DesignId = "mercury_atlas", SiteId = "canaveral",
                Brief = "Джон Гленн на орбите: виток, торможение тремя РДТТ, спуск на парашюте.",
                StartTime = GameCalendar.ToGameTime(1962, 2, 20, 14, 47, 39),
                Rival = "20 фев 1962",
            };
            friendship7.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Min = 150000, HoldSeconds = -1 });
            friendship7.Objectives.Add(new Objective { Type = ObjectiveType.Return });
            list.Add(friendship7);

            var ranger7 = new MissionDef
            {
                Id = "ranger7", Title = "Рейнджер-7", DesignId = "ranger", SiteId = "canaveral",
                Brief = "«Атлас-Аджена»: перелёт к Луне и снимки до самого падения в Море Облаков.",
                StartTime = GameCalendar.ToGameTime(1964, 7, 28, 16, 50, 7),
                Rival = "31 июл 1964",
            };
            ranger7.Objectives.Add(new Objective { Type = ObjectiveType.Impact, Body = "moon" });
            list.Add(ranger7);

            var gemini3 = new MissionDef
            {
                Id = "gemini3", Title = "Джемини-3", DesignId = "gemini_titan", SiteId = "canaveral",
                Brief = "Гриссом и Янг: орбита на «Титане II», торможение агрегатным отсеком, спуск.",
                StartTime = GameCalendar.ToGameTime(1965, 3, 23, 14, 24, 0),
                Rival = "23 мар 1965",
            };
            gemini3.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Min = 150000, HoldSeconds = -1 });
            gemini3.Objectives.Add(new Objective { Type = ObjectiveType.Return });
            list.Add(gemini3);

            var surveyor1 = new MissionDef
            {
                Id = "surveyor1", Title = "Сервейор-1", DesignId = "surveyor", SiteId = "canaveral",
                Brief = "«Атлас-Центавр»: прямой перелёт и мягкая посадка в Океане Бурь.",
                StartTime = GameCalendar.ToGameTime(1966, 5, 30, 14, 41, 1),
                Rival = "2 июн 1966",
            };
            surveyor1.Objectives.Add(new Objective { Type = ObjectiveType.Landing, Body = "moon" });
            list.Add(surveyor1);

            var apollo8 = new MissionDef
            {
                Id = "apollo8", Title = "Аполлон-8", DesignId = "apollo8", SiteId = "canaveral",
                Brief = "«Сатурн-5»: разгон S-IVB к Луне, виток на окололунной орбите, возвращение и спуск.",
                StartTime = GameCalendar.ToGameTime(1968, 12, 21, 12, 51, 0),
                Rival = "24 дек 1968",
            };
            apollo8.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Body = "moon", Min = 50000, HoldSeconds = -1 });
            apollo8.Objectives.Add(new Objective { Type = ObjectiveType.Return });
            list.Add(apollo8);

            var apollo11 = new MissionDef
            {
                Id = "apollo11", Title = "Аполлон-11", DesignId = "apollo11", SiteId = "canaveral",
                Brief = "Посадка «Игла» в Море Спокойствия, взлёт и стыковка с «Колумбией», возвращение на Землю.",
                StartTime = GameCalendar.ToGameTime(1969, 7, 16, 13, 32, 0),
                Rival = "20 июл 1969",
            };
            apollo11.Objectives.Add(new Objective { Type = ObjectiveType.Landing, Body = "moon" });
            apollo11.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Body = "moon", Min = 15000, HoldSeconds = 60 });
            apollo11.Objectives.Add(new Objective { Type = ObjectiveType.Return });
            list.Add(apollo11);

            AddModern(list); // после «Аполлона» — StationMissions.cs
            AddWinged(list); // крылатые: STS-1, «Буран» — WingedMissions.cs
            AddSpaceX(list); // Starship IFT-5 — SpaceXMissions.cs
            return list;
        }
    }
}
