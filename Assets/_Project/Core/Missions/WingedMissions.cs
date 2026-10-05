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
        }
    }
}
