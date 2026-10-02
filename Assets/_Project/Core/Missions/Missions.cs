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
    }

    public sealed class Objective
    {
        public ObjectiveType Type;
        public string Body = "earth";
        public double Min, Max, HoldSeconds;
        public double MaxSpeed = FlightPhysics.CrashSpeed;

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
            u.Launch(VesselPresets.ById(def.DesignId), def.SiteId);
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
            var v = u.Active;
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
    public static class MissionCatalog
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

            return list;
        }
    }
}
