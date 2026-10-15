using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Узел дерева технологий (GDD §8.2): {id, era, minDate, cost{science, funds}, requires[], unlocks[]}. Unlocks — id деталей
    /// PartCatalog; «*» в конце — префикс (tank-2-* — все баки Ø2). Время исследования (days) пока не моделируем: узел
    /// открывается сразу.
    /// </summary>
    public sealed class CareerTechNode
    {
        public string Id, Title, Brief;
        public int Era, Year;
        public double Science, Funds;
        public string[] Requires = new string[0];
        public string[] Parts = new string[0];

        public bool Unlocks(string partId)
        {
            foreach (var p in Parts)
                if (p.EndsWith("*") ? partId.StartsWith(p.Substring(0, p.Length - 1)) : partId == p) return true;
            return false;
        }
    }

    public static class CareerTech
    {
        /// <summary>Ранний доступ §8.2: не раньше чем за 3 года до исторического, цена ×(1 + 0,5·лет_раньше).</summary>
        public const int MaxYearsEarly = 3;
        public const double EarlyPenalty = 0.5;

        public static CareerTechNode Get(string id) => Nodes.Find(n => n.Id == id);

        static readonly Dictionary<string, CareerTechNode> byPart = new Dictionary<string, CareerTechNode>();

        /// <summary>Узел, открывающий деталь; null — детали нет в дереве (она открыта всегда).</summary>
        public static CareerTechNode NodeFor(string partId)
        {
            if (partId == null) return null;
            lock (byPart)
            {
                if (byPart.TryGetValue(partId, out var n)) return n;
                n = Nodes.Find(x => x.Unlocks(partId));
                byPart[partId] = n;
                return n;
            }
        }

        static CareerTechNode N(string id, string title, int era, int year, double s, double f, string[] req, params string[] parts) =>
            new CareerTechNode { Id = id, Title = title, Era = era, Year = year, Science = s, Funds = f, Requires = req, Parts = parts };

        static string[] R(params string[] ids) => ids;

        /// <summary>
        /// Дерево по эрам §8.2; цены по уровням 5–20 / 30–80 / 100–250 / 300–500 S. Деньги — разработка, M₢.
        /// Стартовый узел бесплатный: ракета-носитель «Семёрки» и спутник собираются из него; корабль «Восток» (СА и ТДУ) —
        /// узлы capsule и probes (CraftPresets.Semyorka целиком — к 1961 г.).
        /// Пара: сумма науки контрактов §7.3 (≈ 1400 S) и ситуаций (CareerObserver) должна покрывать всё дерево (≈ 1100 S).
        /// </summary>
        public static readonly List<CareerTechNode> Nodes = new List<CareerTechNode>
        {
            N("start", "Первые ракеты", 1, 1957, 0, 0, R(),
                "tank-1-*", "tank-2-*", "eng-rd107", "eng-rd0110", "dec-1", "dec-2", "dec-radial", "adp-1-2", "adp-2-1",
                "nose-1", "nose-2", "fins", "chute", "probe-core", "pl-sputnik", "antenna", "srb-1",
                "fairing-2", "fairing-3.7"),
            N("probes", "Автоматические станции", 1, 1959, 10, 10, R("start"),
                "eng-ktdu417", "solar", "pl-1t", "legs"),
            N("capsule", "Пилотируемая капсула", 1, 1960, 15, 20, R("start"),
                "cmd-vostok", "cmd-mercury"),
            N("medium", "Ракеты Ø3,7", 1, 1962, 30, 40, R("start"),
                "tank-3.7-*", "dec-3.7", "adp-2-3.7", "adp-3.7-2", "nose-3.7"),
            N("hydrogen", "Водородные ступени", 2, 1963, 40, 50, R("medium"),
                "eng-rl10", "eng-j2"),
            N("docking", "Стыковка и манёвр", 2, 1965, 40, 40, R("capsule"),
                "cmd-gemini", "dock-1", "rcs"),
            N("heavy", "Тяжёлые ракеты Ø5", 2, 1965, 60, 80, R("medium"),
                "tank-5-*", "dec-5", "adp-3.7-5", "adp-5-3.7", "fairing-5", "eng-f1"),
            N("lunar", "Лунный корабль", 2, 1967, 80, 100, R("docking", "hydrogen"),
                "cmd-apollo", "eng-sps"),
            N("rtg", "РИТЭГ", 2, 1969, 40, 30, R("probes"),
                "rtg"),
            N("superheavy", "Сверхтяжёлые Ø7,5–10", 2, 1968, 150, 200, R("heavy"),
                "tank-7.5-*", "tank-10-*", "dec-7.5", "dec-10", "adp-5-7.5", "adp-7.5-5", "adp-7.5-10", "adp-10-7.5",
                "fairing-7.5", "fairing-10", "eng-f1x5", "eng-j2x5", "pl-10t"),
            N("winged", "Крылатые корабли", 3, 1977, 120, 150, R("heavy"),
                "wing", "wing-tail", "wing-fin", "gear", "flaps", "srb-37"),
            N("kerolox", "Мощные керосиновые ЖРД", 3, 1985, 100, 120, R("heavy"),
                "eng-rd180", "eng-rd171x3"),
            N("reuse", "Многоразовые ступени", 5, 2010, 250, 300, R("kerolox"),
                "eng-k1x9", "eng-k2v", "gridfins"),
        };
    }
}
