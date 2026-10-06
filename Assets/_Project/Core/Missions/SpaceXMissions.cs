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
        public static bool IsStarship(MissionDef def) => def.DesignId == "ift5" || CatchesShip(def);

        /// <summary>Корабль возвращается с орбиты к башне B (MissionAutopilot.StarshipOrbital), а не приводняется.</summary>
        public static bool CatchesShip(MissionDef def) => def.DesignId == "starship_catch";

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

            var cat = new MissionDef
            {
                Id = "starship_catch", Title = "Starship: ловля обеих ступеней", DesignId = "starship_catch", SiteId = "starbase",
                Brief = "Полный многоразовый полёт: Super Heavy возвращается к башне A, корабль выходит на орбиту, через " +
                        "сутки сходит с неё над Техасом, входит брюхом и его ловит башня B. F3 — оба борта на экране.",
                // Старт днём по Техасу (17:00 UTC): через звёздные сутки проход над Starbase — тоже днём (§6.9).
                StartTime = GameCalendar.ToGameTime(2026, 6, 15, 17, 0, 0),
                Rival = "—",
            };
            // Орбита: виток (HoldSeconds −1), затем ловля корабля — Landing у башни (касание < 6 м/с в круге 50 м).
            cat.Objectives.Add(new Objective { Type = ObjectiveType.Orbit, Min = 200000, HoldSeconds = -1 });
            cat.Objectives.Add(new Objective { Type = ObjectiveType.Landing, Site = Objective.TowerSite });
            list.Add(cat);

            // Тренировка ловли (§10.2): только корабль, уже «брюхом» над заливом — переворот, посадочное зажигание, палочки.
            // Пара: Altitude/Speed ↔ установившееся падение плашмя (сопротивление sin²α), Range — на запад от башни B.
            var land = new MissionDef
            {
                Id = "starship_landing", Title = "Starship: ловля башней", DesignId = "starship_catch", SiteId = "starbase",
                Brief = "Корабль падает брюхом с 15 км в километре от Starbase: удержать курс, на 2,5 км перевернуться, " +
                        "зажечь Raptor и повиснуть на «палочках» башни B.",
                StartTime = GameCalendar.ToGameTime(2026, 6, 16, 18, 0, 0),
                Rival = "—",
                Approach = new ApproachStart { Altitude = 15000, Range = 1000, Speed = 200, PathDeg = -75, AlphaDeg = 90,
                                               Propellant = 100000 },
            };
            land.Objectives.Add(new Objective { Type = ObjectiveType.Landing, Site = Objective.TowerSite });
            list.Add(land);
        }
    }
}
