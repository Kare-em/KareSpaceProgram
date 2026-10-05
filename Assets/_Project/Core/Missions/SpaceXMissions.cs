using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Миссии SpaceX (GDD §6.9): Starship IFT-5. Выведение, горячее разделение, возврат Super Heavy к башне (фоновый
    /// BoosterLandingAutopilot), трансатмосферная траектория корабля, вход «брюхом» и приводнение. Ведёт их
    /// MissionAutopilot.Starship.cs.
    /// </summary>
    public static partial class MissionCatalog
    {
        /// <summary>Миссия Starship — по проекту (его ведёт MissionAutopilot.Starship, а не обычный суборбитальный сценарий).</summary>
        public static bool IsStarship(MissionDef def) => def.DesignId == "ift5";

        static void AddSpaceX(List<MissionDef> list)
        {
            var ift5 = new MissionDef
            {
                Id = "ift5", Title = "Starship IFT-5", DesignId = "ift5", SiteId = "starbase",
                Brief = "Super Heavy и Starship с Starbase: горячее разделение, ускоритель возвращается к башне, корабль " +
                        "проходит полмира, входит брюхом и приводняется в Индийском океане.",
                StartTime = GameCalendar.ToGameTime(2024, 10, 13, 12, 25, 0),
                Rival = "13 окт 2024",
            };
            // Апогей IFT-5 ≈ 210 км; цель — почти орбитальная высота, затем мягкое приводнение корабля.
            ift5.Objectives.Add(new Objective { Type = ObjectiveType.Altitude, Min = 150000 });
            ift5.Objectives.Add(new Objective { Type = ObjectiveType.Landing });
            list.Add(ift5);
        }
    }
}
