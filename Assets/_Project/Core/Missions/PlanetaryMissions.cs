using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Межпланетные миссии с гравманёврами (GDD §5.3, §6.11): «Вояджеры». Цели — пролёты по порядку; у каждого
    /// пролёта историческая дата (Objective.Date) — от неё TourPlanner ищет тур, — и коридор высот Min..Max.
    /// </summary>
    public static partial class MissionCatalog
    {
        /// <summary>Миссия с туром: хотя бы один пролёт с плановой датой.</summary>
        public static bool IsTour(MissionDef def) =>
            def.Objectives.Exists(o => o.Type == ObjectiveType.Flyby && !double.IsNaN(o.Date));

        /// <summary>
        /// Тур миссии от момента отлёта с опорной орбиты радиуса parkRadius: тела и даты — из целей Flyby,
        /// нижняя граница перицентра — R + Min.
        /// </summary>
        public static FlybyTour PlanTour(MissionDef def, SolarSystem sys, double departure, double parkRadius)
        {
            var legs = def.Objectives.FindAll(o => o.Type == ObjectiveType.Flyby && !double.IsNaN(o.Date));
            if (legs.Count == 0) return null;
            var bodies = new CelestialBody[legs.Count];
            var guess = new double[legs.Count];
            var minPe = new double[legs.Count];
            for (int i = 0; i < legs.Count; i++)
            {
                bodies[i] = sys.Get(legs[i].Body);
                guess[i] = legs[i].Date;
                minPe[i] = bodies[i].Radius + legs[i].Min;
            }
            var home = sys.Get("earth");
            return TourPlanner.Plan(home, departure, bodies, guess, parkRadius, minPe);
        }

        static Objective Flyby(string body, double minAlt, double maxAlt, double date) =>
            new Objective { Type = ObjectiveType.Flyby, Body = body, Min = minAlt, Max = maxAlt, Date = date };

        static void AddPlanetary(List<MissionDef> list)
        {
            // Коридоры высот: Max — с запасом над историческим пролётом (зачёт цели), Min — у Сатурна снаружи кольца A
            // (внешний край 136 775 км от центра = 76 500 км над облаками), у Урана — снаружи кольца ε (≈ 25 600 км).
            var v2 = new MissionDef
            {
                Id = "voyager2", Title = "Вояджер-2", DesignId = "voyager2", SiteId = "canaveral",
                Brief = "«Титан IIIE — Центавр»: Большой тур — гравманёвры у Юпитера, Сатурна и Урана, пролёт Нептуна.",
                StartTime = GameCalendar.ToGameTime(1977, 8, 20, 14, 29, 44),
                Rival = "25 авг 1989",
            };
            // Исторически: Юпитер 9.07.1979 (перицентр 721 670 км), Сатурн 26.08.1981 (161 000 км),
            // Уран 24.01.1986 (107 000 км), Нептун 25.08.1989 (29 240 км от центра).
            v2.Objectives.Add(Flyby("jupiter", 300e6, 1000e6, GameCalendar.ToGameTime(1979, 7, 9, 22, 29, 0)));
            v2.Objectives.Add(Flyby("saturn", 80e6, 200e6, GameCalendar.ToGameTime(1981, 8, 26, 3, 24, 0)));
            v2.Objectives.Add(Flyby("uranus", 30e6, 150e6, GameCalendar.ToGameTime(1986, 1, 24, 17, 59, 0)));
            v2.Objectives.Add(Flyby("neptune", 3e6, 20e6, GameCalendar.ToGameTime(1989, 8, 25, 3, 56, 0)));
            list.Add(v2);

            var v1 = new MissionDef
            {
                Id = "voyager1", Title = "Вояджер-1", DesignId = "voyager1", SiteId = "canaveral",
                Brief = "«Титан IIIE — Центавр»: быстрый перелёт к Юпитеру, гравманёвр к Сатурну.",
                StartTime = GameCalendar.ToGameTime(1977, 9, 5, 12, 56, 0),
                Rival = "12 ноя 1980",
            };
            // Исторически: Юпитер 5.03.1979 (348 890 км), Сатурн 12.11.1980 (184 300 км; пролёт Титана в игре не ставим).
            v1.Objectives.Add(Flyby("jupiter", 200e6, 450e6, GameCalendar.ToGameTime(1979, 3, 5, 12, 5, 0)));
            v1.Objectives.Add(Flyby("saturn", 80e6, 200e6, GameCalendar.ToGameTime(1980, 11, 12, 23, 46, 0)));
            list.Add(v1);
        }
    }
}
