using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Крылатые миссии (GDD §6.4, §7.3): STS-1 «Колумбия» и «Буран». Выведение, виток, тормозной импульс, вход с большим
    /// углом атаки, заход и посадка на полосу (ObjectiveType.Runway). Ведёт их MissionAutopilot.Winged.cs.
    /// </summary>
    public static partial class MissionCatalog
    {
        /// <summary>Орбита выведения крылатой миссии: наклонение (°) и высота (м). Нужна автопилоту для азимута старта.</summary>
        public sealed class WingedOrbit
        {
            public double Inclination, Altitude;
        }

        /// <summary>
        /// STS-1: 40,3° и ~240 км (настоящая 246 × 248 км). «Буран»: 51,6° и ~250 км (247 × 256 км).
        /// Пара: Altitude ↔ MissionAutopilot.WingedDeorbitPerigee — с такой высоты до перигея 10 км импульс схода ≈ 70 м/с
        /// (замер зонда: STS-1 69 м/с, «Буран» 73,5 м/с).
        /// </summary>
        public static WingedOrbit WingedOrbitOf(string missionId)
        {
            switch (missionId)
            {
                case "sts1": return new WingedOrbit { Inclination = 40.3, Altitude = 240e3 };
                case "buran": return new WingedOrbit { Inclination = 51.6, Altitude = 250e3 };
                default: return null;
            }
        }

        static void AddWinged(List<MissionDef> list)
        {
            var sts1 = new MissionDef
            {
                Id = "sts1", Title = "STS-1 «Колумбия»", DesignId = "sts1", SiteId = "canaveral",
                Brief = "Первый «Шаттл»: выведение с LC-39A, сброс бака на орбите, вход с углом атаки 40°, планирование " +
                        "и посадка на дно озера Роджерс в Эдвардсе.",
                StartTime = GameCalendar.ToGameTime(1981, 4, 12, 12, 0, 4),
                Rival = "12 апр 1981",
            };
            sts1.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Min = 200000, HoldSeconds = -1 });
            sts1.Objectives.Add(new Objective { Type = ObjectiveType.Runway, Site = "edwards23" });
            list.Add(sts1);

            var buran = new MissionDef
            {
                Id = "buran", Title = "«Буран»", DesignId = "buran", SiteId = "baikonur",
                Brief = "Единственный полёт «Бурана»: «Энергия», два витка без экипажа и автоматическая посадка " +
                        "на аэродром «Юбилейный» в трёх километрах от старта.",
                StartTime = GameCalendar.ToGameTime(1988, 11, 15, 3, 0, 0),
                Rival = "15 ноя 1988",
            };
            buran.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Min = 200000, HoldSeconds = -1 });
            buran.Objectives.Add(new Objective { Type = ObjectiveType.Runway, Site = "yubileyny" });
            list.Add(buran);

            // Тренировка посадки (§10.2): только орбитер, сразу на рубеже TAEM — дозвуковой заход, конус HAC, выравнивание.
            // Пара: Speed < WingedGuidance.TaemSpeed (900) — автопилот начинает с TAEM, а не с гиперзвукового входа;
            // Range ≈ TaemRange (80 км) + запас на разворот по конусу.
            list.Add(WingedLanding("sts1_landing", "STS-1: заход и посадка", "sts1", "edwards", "edwards23",
                "«Колумбия» на высоте 25 км, в 85 км от озера Роджерс: заход по конусу, выпуск шасси (G), посадка " +
                "на полосу 23 и пробег с тормозами.", GameCalendar.ToGameTime(1981, 4, 14, 18, 15, 0), "14 апр 1981"));
            list.Add(WingedLanding("buran_landing", "«Буран»: заход и посадка", "buran", "yubileyny", "yubileyny",
                "«Буран» на высоте 25 км над степью: заход на «Юбилейный» с разворотом, выпуск шасси (G), посадка " +
                "и тормозной парашют (2).", GameCalendar.ToGameTime(1988, 11, 15, 6, 19, 0), "15 ноя 1988"));
        }

        /// <summary>Рубеж TAEM: 25 км, 800 м/с, траектория −6°, угол атаки 10° (STS: ~Mach 2,5 на 25 км).</summary>
        static MissionDef WingedLanding(string id, string title, string design, string site, string runway, string brief,
                                        double start, string rival)
        {
            var m = new MissionDef
            {
                Id = id, Title = title, DesignId = design, SiteId = site, Brief = brief, StartTime = start, Rival = rival,
                Approach = new ApproachStart { Altitude = 25000, Range = 85000, Speed = 800, PathDeg = -6, AlphaDeg = 10 },
            };
            m.Objectives.Add(new Objective { Type = ObjectiveType.Runway, Site = runway });
            return m;
        }
    }
}
