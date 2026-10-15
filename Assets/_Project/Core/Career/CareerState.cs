using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kare.Space.Core
{
    /// <summary>
    /// Вход в карьеру для чужого кода (конструктор, HUD): null в Current — песочница, всё открыто (GDD §8.5).
    /// Игровой слой (CareerStore) кладёт сюда загруженное состояние, когда игрок выбрал режим «Карьера».
    /// </summary>
    public static class Career
    {
        public static CareerState Current;
        public static bool Active => Current != null;

        public static bool IsPartUnlocked(string partId) => Current == null || Current.IsPartUnlocked(partId);
        public static bool IsPartUnlocked(PartDef part) => part == null || IsPartUnlocked(part.Id);
    }

    /// <summary>Что дал выполненный контракт (§7.3), для окна результата.</summary>
    public sealed class CareerReward
    {
        public CareerContract Contract;
        public double Funds, Science, Reputation;
        /// <summary>Множитель гонки §6.1 и его пояснение («раньше соперника на 120 сут»).</summary>
        public double RaceFactor = 1;
        public string RaceNote;
    }

    /// <summary>
    /// Сохранение карьеры (GDD §8.3): казна M₢, наука S, престиж P, дата, выполненные контракты, изученные узлы,
    /// собранная наука по телам/ситуациям. Отдельно от сохранений полёта (.ksav): карьера переживает любой полёт.
    /// </summary>
    public sealed class CareerState
    {
        /// <summary>Казна 1957 года и дата старта карьеры (§8.3: «стартовая казна 150 M₢», бюджет с 1 января).</summary>
        public const double StartFunds = 150;
        public const int StartYear = 1957;
        /// <summary>Дефицит на 1 января режет бюджет года на эту долю (§8.3: «бюджет урезается, игра не кончается»).</summary>
        public const double DeficitCut = 0.25;
        /// <summary>Масштаб престижа в множителе бюджета B·(1 + 0,5·tanh(P/500)) (§8.3). Пара: PrestigeBoost.</summary>
        public const double PrestigeScale = 500, PrestigeBoost = 0.5;
        /// <summary>Журнал событий карьеры — последние N строк (окно карьеры показывает хвост).</summary>
        public const int LogSize = 40;

        public double Funds = StartFunds, Science, Reputation;
        /// <summary>Дата карьеры, игровое время (с J2000; 1957 — отрицательное). Идёт только вперёд.</summary>
        public double Date = GameCalendar.ToGameTime(StartYear, 1, 1);
        public int Flights;
        public readonly HashSet<string> Completed = new HashSet<string>();
        public readonly HashSet<string> Tech = new HashSet<string>();
        /// <summary>Ключи «тело:ситуация» собранной науки (CareerObserver): каждая — один раз (§8.3, повтор ×0,5^n у нас — 0).</summary>
        public readonly HashSet<string> ScienceDone = new HashSet<string>();
        public readonly List<string> Log = new List<string>();

        public CareerState()
        {
            foreach (var n in CareerTech.Nodes)
                if (n.Science <= 0 && n.Funds <= 0) Tech.Add(n.Id);
        }

        public int Year
        {
            get
            {
                GameCalendar.ToDate(Date, out int y, out _, out _, out _, out _, out _);
                return y;
            }
        }

        public void Say(string text)
        {
            Log.Add(text);
            while (Log.Count > LogSize) Log.RemoveAt(0);
        }

        // ---------------------------------------------------------------- детали и узлы (§8.2)

        /// <summary>Деталь открыта, если её узел изучен; деталь вне дерева (новая в каталоге) открыта всегда.</summary>
        public bool IsPartUnlocked(string partId)
        {
            var node = CareerTech.NodeFor(partId);
            return node == null || Tech.Contains(node.Id);
        }

        /// <summary>Цена узла сейчас (§8.2: раньше исторического года — ×(1 + 0,5·лет_раньше), не раньше чем за 3 года).</summary>
        public void NodePrice(CareerTechNode n, out double science, out double funds)
        {
            double k = 1 + CareerTech.EarlyPenalty * Math.Max(0, n.Year - Year);
            science = Math.Ceiling(n.Science * k);
            funds = Math.Ceiling(n.Funds * k);
        }

        /// <summary>Почему узел нельзя изучить сейчас; null — можно.</summary>
        public string NodeBlock(CareerTechNode n)
        {
            if (Tech.Contains(n.Id)) return "изучено";
            foreach (var r in n.Requires)
                if (!Tech.Contains(r)) return "нужно: " + (CareerTech.Get(r)?.Title ?? r);
            if (n.Year - Year > CareerTech.MaxYearsEarly) return $"не раньше {n.Year - CareerTech.MaxYearsEarly} г.";
            NodePrice(n, out double s, out double f);
            if (Science < s) return $"мало науки ({Science:F0} из {s:F0} S)";
            if (Funds < f) return $"мало денег ({Funds:F0} из {f:F0} M₢)";
            return null;
        }

        public bool Buy(CareerTechNode n)
        {
            if (NodeBlock(n) != null) return false;
            NodePrice(n, out double s, out double f);
            Science -= s;
            Funds -= f;
            Tech.Add(n.Id);
            Say($"Изучено: {n.Title} (−{s:F0} S, −{f:F0} M₢)");
            return true;
        }

        // ---------------------------------------------------------------- контракты (§7.3)

        /// <summary>Почему контракт закрыт; null — доступен. Выполненный не повторяется.</summary>
        public string ContractBlock(CareerContract c)
        {
            if (Completed.Contains(c.Id)) return "выполнен";
            foreach (var r in c.Requires)
                if (!Completed.Contains(r)) return "сначала: " + (CareerContracts.Get(r)?.Title ?? r);
            return null;
        }

        /// <summary>
        /// Засчитать контракт: деньги и наука — по таблице §7.3, престиж — с множителем гонки §6.1 против RivalDate.
        /// Дата достижения — не раньше даты карьеры: история не отматывается, «Восход-2» после «Аполлона» — поздно.
        /// </summary>
        public CareerReward Complete(CareerContract c, double time)
        {
            if (c == null || ContractBlock(c) != null) return null;
            double when = Math.Max(Date, time);
            var r = new CareerReward { Contract = c, Funds = c.Funds, Science = c.Science };
            if (!double.IsNaN(c.RivalDate))
            {
                double days = (c.RivalDate - when) / Constants.Day;
                r.RaceFactor = RaceFactor(days);
                r.RaceNote = days >= 0 ? $"раньше соперника на {days:F0} сут" : $"позже соперника на {-days:F0} сут";
            }
            r.Reputation = Math.Round(c.Reputation * r.RaceFactor);
            Completed.Add(c.Id);
            AdvanceDate(when);
            Funds += r.Funds;
            Science += r.Science;
            Reputation += r.Reputation;
            Say($"Контракт «{c.Title}»: +{r.Funds:F0} M₢, +{r.Science:F0} S, +{r.Reputation:F0} P");
            return r;
        }

        /// <summary>
        /// Гонка §6.1: раньше соперника — P·(1 + min(1, дней/365)), позже — P·max(0,1; 0,5·e^(−лет/2)).
        /// </summary>
        public static double RaceFactor(double daysEarlier)
        {
            if (daysEarlier >= 0) return 1 + Math.Min(1, daysEarlier / 365.25);
            double years = -daysEarlier / 365.25;
            return Math.Max(0.1, 0.5 * Math.Exp(-years / 2));
        }

        // ---------------------------------------------------------------- деньги и время (§8.1, §8.3)

        /// <summary>Базовый бюджет года по эрам §8.1, M₢ (линейно внутри эры).</summary>
        public static double BaseBudget(int year)
        {
            if (year < 1965) return Lerp(300, 1500, (year - 1957) / 8.0);
            if (year < 1975) return Lerp(3000, 2000, (year - 1965) / 10.0);
            if (year < 1990) return Lerp(1500, 2500, (year - 1975) / 15.0);
            return 3000;
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * Math.Max(0, Math.Min(1, t));

        /// <summary>Бюджет года с престижем: B·(1 + 0,5·tanh(P/500)) (§8.3).</summary>
        public double Budget(int year) => BaseBudget(year) * (1 + PrestigeBoost * Math.Tanh(Reputation / PrestigeScale));

        /// <summary>
        /// Сдвинуть дату карьеры вперёд (полёт закончился позже): за каждое пройденное 1 января — годовой бюджет;
        /// при минусе в казне год урезан на DeficitCut. Назад дата не идёт.
        /// </summary>
        public void AdvanceDate(double time)
        {
            if (double.IsNaN(time) || time <= Date) return;
            int from = Year;
            Date = time;
            int to = Year;
            for (int y = from + 1; y <= to; y++)
            {
                double b = Budget(y);
                if (Funds < 0) b *= 1 - DeficitCut;
                b = Math.Round(b);
                Funds += b;
                Say($"1 января {y}: бюджет +{b:F0} M₢");
            }
        }

        /// <summary>Пропустить время до ближайшего 1 января (ждать бюджета, когда казна пуста).</summary>
        public void WaitNextYear() => AdvanceDate(GameCalendar.ToGameTime(Year + 1, 1, 1));

        /// <summary>Оплата пуска (§8.4). Уход в минус разрешён: дефицит режет следующий бюджет.</summary>
        public void Charge(double price, string what)
        {
            Funds -= price;
            Flights++;
            Say($"Пуск: {what} −{price:F0} M₢");
        }

        public void AddFunds(double amount, string why)
        {
            Funds += amount;
            Say($"{why}: +{amount:F0} M₢");
        }

        /// <summary>Наука за ситуацию один раз; true — новая.</summary>
        public bool AddScience(string key, double amount, string why)
        {
            if (!ScienceDone.Add(key)) return false;
            Science += amount;
            Say($"Наука: {why} +{amount:F0} S");
            return true;
        }

        // ---------------------------------------------------------------- JSON

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"version\": 1");
            sb.Append(",\n  \"funds\": ").Append(Funds.ToString("R", Inv));
            sb.Append(",\n  \"science\": ").Append(Science.ToString("R", Inv));
            sb.Append(",\n  \"reputation\": ").Append(Reputation.ToString("R", Inv));
            sb.Append(",\n  \"date\": ").Append(Date.ToString("R", Inv));
            sb.Append(",\n  \"flights\": ").Append(Flights.ToString(Inv));
            List("completed", Completed, sb);
            List("tech", Tech, sb);
            List("scienceDone", ScienceDone, sb);
            List("log", Log, sb);
            sb.Append("\n}\n");
            return sb.ToString();
        }

        static void List(string key, IEnumerable<string> items, StringBuilder sb)
        {
            sb.Append(",\n  ").Append(Json.Quote(key)).Append(": [");
            bool first = true;
            foreach (var s in items)
            {
                sb.Append(first ? "" : ", ").Append(Json.Quote(s));
                first = false;
            }
            sb.Append(']');
        }

        public static CareerState FromJson(string text)
        {
            var o = Json.Parse(text) as Dictionary<string, object>;
            if (o == null) throw new FormatException("career: не объект JSON");
            var s = new CareerState
            {
                Funds = Json.Num(o, "funds", StartFunds),
                Science = Json.Num(o, "science", 0),
                Reputation = Json.Num(o, "reputation", 0),
                Date = Json.Num(o, "date", GameCalendar.ToGameTime(StartYear, 1, 1)),
                Flights = (int)Json.Num(o, "flights", 0),
            };
            Read(o, "completed", s.Completed.Add);
            Read(o, "tech", t => s.Tech.Add(t));
            Read(o, "scienceDone", s.ScienceDone.Add);
            Read(o, "log", t => { s.Log.Add(t); return true; });
            return s;
        }

        static void Read(Dictionary<string, object> o, string key, Func<string, bool> add)
        {
            if (!o.TryGetValue(key, out var v) || !(v is List<object> list)) return;
            foreach (var item in list)
                if (item is string str) add(str);
        }
    }
}
