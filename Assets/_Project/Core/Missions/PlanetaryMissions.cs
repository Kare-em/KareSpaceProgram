using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Межпланетные миссии с гравманёврами (GDD §5.3, §6.11): «Вояджеры», «Кассини — Гюйгенс». Цели — пролёты по порядку; у каждого
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
            var dsm = new bool[legs.Count];
            for (int i = 0; i < legs.Count; i++)
            {
                bodies[i] = sys.Get(legs[i].Body);
                guess[i] = legs[i].Date;
                minPe[i] = bodies[i].Radius + legs[i].Min;
                // Возврат к той же планете (Венера → Венера у «Кассини») без двигателя не сходится по модулю v∞:
                // прямой Ламберт требовал C3 45,7 км²/с² вместо исторических 16,6 — на таком отрезке манёвр в дальнем
                // космосе, как 3.12.1998 (≈ 450 м/с).
                dsm[i] = i > 0 && legs[i].Body == legs[i - 1].Body;
            }
            var home = sys.Get("earth");
            return TourPlanner.Plan(home, departure, bodies, guess, parkRadius, minPe, dsm);
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

            // «Кассини — Гюйгенс»: тур VVEJGA (Венера, Венера, Земля, Юпитер) и выход на орбиту Сатурна, затем «Гюйгенс»
            // садится на Титан. Исторически: Венера 26.04.1998 (284 км) и 24.06.1999 (623 км), Земля 18.08.1999 (1171 км),
            // Юпитер 30.12.2000 (9,7 млн км), Сатурн 1.07.2004 (≈ 20 000 км над облаками, торможение 626 м/с),
            // посадка на Титан 14.01.2005. У Сатурна коридор пролёта — над облаками на высоте щели между кольцами F и G.
            var cassini = new MissionDef
            {
                Id = "cassini", Title = "Кассини–Гюйгенс", DesignId = "cassini", SiteId = "canaveral",
                Brief = "«Титан IVB — Центавр»: гравманёвры у Венеры (дважды), Земли и Юпитера, выход на орбиту Сатурна, " +
                        "посадка зонда «Гюйгенс» на Титан. Автопилот летит упрощённо: разгон C3 23 км²/с² (у настоящего 16,6), " +
                        "манёвр в дальнем космосе ≈ 400 м/с (450), пролёты Венеры выше (≈ 5400 и 2700 км против 284 и 623), " +
                        "Юпитер в феврале 2001-го, Сатурн — в мае 2006-го вместо 1.07.2004; перед Титаном перицентр поднят " +
                        "(как манёвр PRM), «Гюйгенс» отделяется уже в сфере Титана (у настоящего — за 20 сут), купол — на 15 км (160).",
                StartTime = GameCalendar.ToGameTime(1997, 10, 15, 8, 43, 0),
                Rival = "14 янв 2005",
            };
            cassini.Objectives.Add(Flyby("venus", 200e3, 8000e3, GameCalendar.ToGameTime(1998, 4, 26, 13, 45, 0)));
            cassini.Objectives.Add(Flyby("venus", 200e3, 3000e3, GameCalendar.ToGameTime(1999, 6, 24, 20, 30, 0)));
            cassini.Objectives.Add(Flyby("earth", 300e3, 5000e3, GameCalendar.ToGameTime(1999, 8, 18, 3, 28, 0)));
            cassini.Objectives.Add(Flyby("jupiter", 3e9, 15e9, GameCalendar.ToGameTime(2000, 12, 30, 10, 5, 0)));
            cassini.Objectives.Add(Flyby("saturn", 15e6, 25e6, GameCalendar.ToGameTime(2004, 7, 1, 2, 48, 0)));
            // Орбита Сатурна — сутки после торможения (виток ≈ 116 сут ждать незачем); перицентр — над атмосферой.
            cassini.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Body = "saturn", Min = 2e6, HoldSeconds = 86400 });
            cassini.Objectives.Add(new Objective { Type = ObjectiveType.Landing, Body = "titan" });
            list.Add(cassini);
        }
    }
}
