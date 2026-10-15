using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Контракт карьеры (GDD §7.3): веха гонки поверх готовой миссии MissionId — условия успеха те же (MissionTracker),
    /// карьера добавляет награду, порядок (Requires) и дату соперника для престижа §6.1.
    /// </summary>
    public sealed class CareerContract
    {
        public string Id, Title, MissionId;
        public string[] Requires = new string[0];
        /// <summary>Награда §7.3: деньги M₢, наука S, престиж P (до множителя гонки).</summary>
        public double Funds, Science, Reputation;
        /// <summary>Дата достижения соперника (игровое время), NaN — соперника нет (первыми в мире).</summary>
        public double RivalDate = double.NaN;
        public string RivalName;
        /// <summary>Можно лететь историческим носителем (у миссий только карьеры пресета нет — своя ракета).</summary>
        public bool AllowHistoric = true;
    }

    public static class CareerContracts
    {
        public static readonly List<CareerContract> All = Build();

        public static CareerContract Get(string id) => All.Find(c => c.Id == id);
        public static CareerContract ForMission(string missionId) => All.Find(c => c.MissionId == missionId);

        static double D(int y, int m, int d) => GameCalendar.ToGameTime(y, m, d, 12);

        static CareerContract C(string id, string title, double p, double s, double f, params string[] req) =>
            new CareerContract { Id = id, MissionId = id, Title = title, Reputation = p, Science = s, Funds = f, Requires = req };

        static CareerContract Rival(this CareerContract c, double date, string who)
        {
            c.RivalDate = date;
            c.RivalName = who;
            return c;
        }

        /// <summary>
        /// Вехи по хронике §7.3, награды — из её таблицы (P/S/₢); у вех вне таблицы — по соседям того же класса.
        /// Соперник — достижение другой стороны (Эксплорер-1 к «Спутнику», «Френдшип-7» к «Востоку»): кто раньше — тот
        /// получает до ×2 престижа (§6.1).
        /// </summary>
        static List<CareerContract> Build() => new List<CareerContract>
        {
            C("karman", "Линия Кармана", 30, 5, 10),
            C("sputnik", "Первый спутник", 100, 5, 20).Rival(D(1958, 2, 1), "Эксплорер-1"),
            C("juno1", "Эксплорер-1: пояс радиации", 50, 10, 20, "sputnik"),
            C("mechta", "Вторая космическая: «Мечта»", 150, 15, 30, "sputnik").Rival(D(1959, 3, 4), "Пионер-4"),
            C("vympel", "Вымпел на Луне", 200, 20, 40, "mechta").Rival(D(1962, 4, 26), "Рейнджер-4"),
            C("farside", "Снимок обратной стороны Луны", 200, 20, 50, "mechta").Rival(D(1966, 8, 18), "Лунар орбитер-1"),
            C("freedom7", "Суборбитальный пилотируемый", 120, 10, 40, "sputnik"),
            C("vostok", "Человек на орбите", 300, 15, 60, "sputnik").Rival(D(1962, 2, 20), "Френдшип-7"),
            C("friendship7", "Френдшип-7: три витка", 120, 15, 50, "vostok"),
            C("ranger7", "Снимки Луны вблизи", 80, 15, 30, "vympel"),
            C("voskhod2", "Выход в открытый космос", 250, 15, 50, "vostok").Rival(D(1965, 6, 3), "Джемини-4"),
            C("gemini3", "Двухместный корабль, манёвр", 150, 20, 60, "vostok"),
            C("luna9", "Мягкая посадка на Луну", 300, 30, 100, "vympel").Rival(D(1966, 6, 2), "Сервейор-1"),
            C("surveyor1", "Сервейор-1: посадка на Луну", 150, 30, 80, "vympel"),
            C("apollo8", "Люди у Луны", 600, 60, 300, "gemini3", "farside"),
            C("apollo11", "Человек на Луне", 1000, 100, 500, "apollo8"),
            C("luna17", "Луноход", 250, 50, 120, "luna9").Rival(D(1997, 7, 4), "Соджорнер"),
            new CareerContract
            {
                Id = "venera7", MissionId = CareerMissions.Venera, Title = "Посадка на Венеру", Reputation = 400, Science = 60,
                Funds = 150, Requires = new[] { "mechta" }, AllowHistoric = false,
            },
            new CareerContract
            {
                Id = "mars3", MissionId = CareerMissions.Mars, Title = "Посадка на Марс", Reputation = 400, Science = 80,
                Funds = 200, Requires = new[] { "mechta" }, AllowHistoric = false,
            }.Rival(D(1976, 7, 20), "Викинг-1"),
            C("voyager1", "Юпитер и Сатурн", 400, 100, 200, "mechta"),
            C("voyager2", "Большой тур: Нептун", 800, 200, 400, "mechta"),
            C("sts1", "Многоразовый корабль", 300, 30, 150, "vostok").Rival(D(1988, 11, 15), "Буран"),
            C("buran", "Беспилотная посадка крылатого корабля", 200, 30, 120, "vostok"),
            C("soyuz_tm31", "Экипаж на станцию", 700, 150, 600, "gemini3"),
            C("cassini", "Посадка на Титан", 600, 250, 700, "voyager1"),
            C("crew_dragon", "Частный корабль к станции", 300, 20, 300, "soyuz_tm31"),
            C("ift5", "Возврат многоразовой ступени", 300, 0, 150, "sts1"),
        };
    }

    /// <summary>
    /// Миссии только карьеры (GDD §7.3 №9–10: Венера, Марс): своих пресетов у них нет — летит ракета игрока.
    /// Регистрируются в MissionCatalog.All во время работы (Register из игрового слоя): ядро и тесты без них не меняются.
    /// </summary>
    public static class CareerMissions
    {
        public const string Venera = "career_venera", Mars = "career_mars";

        public static bool IsCareerOnly(string missionId) => missionId == Venera || missionId == Mars;

        /// <summary>Добавить миссии карьеры в каталог (повторный вызов ничего не делает).</summary>
        public static void Register()
        {
            if (MissionCatalog.Get(Venera) != null) return;
            // «Венера-7»: старт 17.08.1970 05:38 UTC, посадка 15.12.1970 — первая мягкая посадка на другую планету.
            var venera = new MissionDef
            {
                Id = Venera, Title = "Венера: посадка", DesignId = "kara1", SiteId = "baikonur",
                Brief = "Своя ракета: перелёт к Венере в окне 1970 г. и посадка спускаемого аппарата (как «Венера-7»).",
                StartTime = GameCalendar.ToGameTime(1970, 8, 17, 5, 38, 22),
                Rival = "15 дек 1970",
            };
            venera.Objectives.Add(new Objective { Type = ObjectiveType.EnterSoi, Body = "venus" });
            venera.Objectives.Add(new Objective { Type = ObjectiveType.Landing, Body = "venus" });
            MissionCatalog.All.Add(venera);
            // «Марс-3»: старт 28.05.1971 15:26 UTC, посадка 2.12.1971 — первая мягкая посадка на Марс.
            var mars = new MissionDef
            {
                Id = Mars, Title = "Марс: посадка", DesignId = "kara1", SiteId = "baikonur",
                Brief = "Своя ракета: перелёт к Марсу в окне 1971 г. и мягкая посадка (как «Марс-3»).",
                StartTime = GameCalendar.ToGameTime(1971, 5, 28, 15, 26, 30),
                Rival = "2 дек 1971",
            };
            mars.Objectives.Add(new Objective { Type = ObjectiveType.EnterSoi, Body = "mars" });
            mars.Objectives.Add(new Objective { Type = ObjectiveType.Landing, Body = "mars" });
            MissionCatalog.All.Add(mars);
        }
    }
}
