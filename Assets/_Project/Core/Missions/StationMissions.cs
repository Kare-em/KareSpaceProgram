using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Станция-цель на орбите к началу миссии (GDD §6.6). Станция — секция MissionDef.StationSection проекта ракеты:
    /// стыковка в ядре допускается только между бортами одного проекта (Universe.CanDock), поэтому МКС не отдельный
    /// проект, а «верхняя ступень», которая уже летает сама. С ракеты на столе её секция снимается.
    /// </summary>
    public static class StationSetup
    {
        public static Vessel Place(Universe u, Vessel launch, MissionDef def)
        {
            int s = def.StationSection;
            var design = launch.Design;
            if (s < 0 || s >= design.Sections.Count) throw new ArgumentException($"Нет секции станции {s}");
            var site = launch.Site;
            var body = launch.Body;

            // Ракета на столе — без станции: центр масс ниже, ставим заново.
            launch.Attached[s] = false;
            FlightPhysics.PlaceOnSurface(launch, body, site.Latitude, site.Longitude, u.Time, site.PadHeight);
            FlightPhysics.UpdateTelemetry(launch, u.Time);

            // Плоскость орбиты проходит через площадку в момент StartTime + StationLaunchDelay на восходящем витке —
            // окно старта (MissionAutopilot.PlaneWindow) приходится ровно на него. Кадр — как LaunchWindow.PlaneError.
            double tL = u.Time + def.StationLaunchDelay;
            var q = body.OrientationAt(tL);
            var up = q * site.DirectionBodyFixed;
            var north = Vector3d.ProjectOnPlane(q * Vector3d.forward, up).normalized;
            var east = Vector3d.Cross(north, up);
            double lat = site.Latitude * Constants.Deg2Rad, inc = def.StationInclination * Constants.Deg2Rad;
            // Инерциальный азимут пролёта площадки: sin A = cos i / cos φ (51,6° с Байконура — 63°, с Канаверала — 45°).
            double sinA = Math.Max(-1, Math.Min(1, Math.Cos(inc) / Math.Cos(lat)));
            double A = Math.Asin(sinA);
            var heading = north * Math.Cos(A) + east * Math.Sin(A);
            double r = body.Radius + def.StationAltitude, lead = def.StationLead * Constants.Deg2Rad;
            double vc = Math.Sqrt(body.Mu / r);
            // Станция на момент tL опережает площадку на lead по дуге; к u.Time — откатываем по круговой орбите.
            double back = lead - vc / r * def.StationLaunchDelay;
            var pos = (up * Math.Cos(back) + heading * Math.Sin(back)) * r;
            var vel = (-up * Math.Sin(back) + heading * Math.Cos(back)) * vc;

            var st = new Vessel(design, def.StationName ?? design.Sections[s].Name);
            for (int i = 0; i < st.Attached.Length; i++) st.Attached[i] = i == s;
            st.NextStage = design.Sequence.Count;
            st.Body = body;
            st.Situation = Situation.Flying;
            st.Position = pos;
            st.Velocity = vel;
            // Узлом назад по курсу: корабль догоняет снизу и сзади, подход по оси без облёта (как «Союз» к ПрК «Звезды»).
            st.Attitude = QuaternionD.FromToRotation(Vector3d.up, (-vel.normalized).SwapYZ);
            st.AngularVelocity = Vector3d.zero;
            FlightPhysics.UpdateTelemetry(st, u.Time);
            u.Add(st);
            return st;
        }
    }

    /// <summary>Миссии после «Аполлона» (GDD §7.3): выход в открытый космос и станции.</summary>
    public static partial class MissionCatalog
    {
        /// <summary>Станция на 380 км (МКС осенью 2000 г.) и 420 км (май 2020 г.), наклонение 51,6°.</summary>
        const double Iss2000Altitude = 380e3, Iss2020Altitude = 420e3, IssInclination = 51.6;
        /// <summary>
        /// Опережение станции над площадкой в момент окна, °. Выведение ~9 мин (≈ 18° по дуге) против ~34° станции,
        /// дрейф фаз 200 → 380 км ≈ 13°/ч: 0° даёт перелёт в пределах первых витков, а не суток.
        /// Пара: StationLead ↔ MissionAutopilot.ParkingAltitude.
        /// </summary>
        const double IssLead = 0;
        /// <summary>В связке со станцией, с: цель «Стыковка» засчитывается после этого (настоящие — месяцы).</summary>
        const double DockStay = 600;

        static void AddModern(List<MissionDef> list)
        {
            var voskhod2 = new MissionDef
            {
                Id = "voskhod2", Title = "Восход-2", DesignId = "voskhod2",
                Brief = "Первый выход в открытый космос: шлюз «Волга», виток на орбите и возвращение экипажа из двух человек.",
                StartTime = GameCalendar.ToGameTime(1965, 3, 18, 7, 0, 0),
                Rival = "18 мар 1965",
            };
            voskhod2.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Min = 150000, HoldSeconds = -1 });
            voskhod2.Objectives.Add(new Objective { Type = ObjectiveType.Return });
            list.Add(voskhod2);

            var tm31 = new MissionDef
            {
                Id = "soyuz_tm31", Title = "Союз ТМ-31 — МКС", DesignId = "soyuz_tm31",
                Brief = "Первая экспедиция на МКС: «Союз-У» в плоскость станции, сближение, стыковка, расстыковка и спуск СА.",
                StartTime = GameCalendar.ToGameTime(2000, 10, 31, 7, 52, 47),
                Rival = "2 ноя 2000",
                StationSection = 7, StationAltitude = Iss2000Altitude, StationInclination = IssInclination,
                StationLead = IssLead, StationName = "МКС",
            };
            tm31.Objectives.Add(new Objective { Type = ObjectiveType.Dock, HoldSeconds = DockStay });
            tm31.Objectives.Add(new Objective { Type = ObjectiveType.Return });
            list.Add(tm31);

            var demo2 = new MissionDef
            {
                Id = "crew_dragon", Title = "Crew Dragon Demo-2", DesignId = "crew_dragon", SiteId = "canaveral",
                Brief = "Falcon 9 и «Индевор» с LC-39A: выведение в плоскость МКС, стыковка, расстыковка и приводнение.",
                StartTime = GameCalendar.ToGameTime(2020, 5, 30, 19, 22, 45),
                Rival = "31 мая 2020",
                StationSection = 4, StationAltitude = Iss2020Altitude, StationInclination = IssInclination,
                StationLead = IssLead, StationName = "МКС",
            };
            demo2.Objectives.Add(new Objective { Type = ObjectiveType.Dock, HoldSeconds = DockStay });
            demo2.Objectives.Add(new Objective { Type = ObjectiveType.Return });
            list.Add(demo2);
        }
    }
}
