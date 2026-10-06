using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kare.Space.Core
{
    /// <summary>Деталь в стеке ядра. Stage — номер ступени (0 — первая по пробелу), −1 — не задан (авто).</summary>
    public sealed class CraftPart
    {
        public string Id;
        public int Stage = -1;
        /// <summary>
        /// Размеры игрока (§5.4, PartParam): NaN — каталожное значение. В JSON — необязательными ключами, старые сохранения
        /// открываются как есть. Пересчёт масс и площадей — PartCatalog.Resolve.
        /// </summary>
        public double Length = double.NaN, Diameter = double.NaN, Top = double.NaN, Span = double.NaN, Chord = double.NaN,
            Sweep = double.NaN, Incidence = double.NaN, Dihedral = double.NaN;

        /// <summary>
        /// Высота ввода парашюта, м (§4.8, окно детали в ангаре): NaN — штатная FlightPhysics.ChuteDeployAltitude. Не размер —
        /// в HasParams/ParamKey не входит (кеш Resolve от неё не зависит); в JSON — необязательный ключ ChuteKey.
        /// </summary>
        public double ChuteAltitude = double.NaN;

        /// <summary>JSON-ключ высоты ввода парашюта у детали стека и у боковой группы; пишется только заданный.</summary>
        public const string ChuteKey = "chuteAlt";

        /// <summary>
        /// Возврат ступени (§6.9, окно опор в ангаре): null — по умолчанию (RecoveryDef.DefaultFor), иначе выбор игрока.
        /// Смысл имеет у детали с опорами в секции с двигателем; в JSON — необязательный ключ LandingKey.
        /// </summary>
        public RecoveryTarget? Landing;

        /// <summary>JSON-ключ возврата ступени у детали стека и у боковой группы; пишется только заданный.</summary>
        public const string LandingKey = "landing";

        public CraftPart() { }
        public CraftPart(string id, int stage = -1) { Id = id; Stage = stage; }

        /// <summary>JSON-ключи параметров, в порядке PartCatalog.ParamOrder.</summary>
        public static readonly string[] ParamKeys = { "length", "diameter", "top", "span", "chord", "sweep", "incidence", "dihedral" };

        public double Get(PartParam p)
        {
            switch (p)
            {
                case PartParam.Length: return Length;
                case PartParam.Diameter: return Diameter;
                case PartParam.Top: return Top;
                case PartParam.Span: return Span;
                case PartParam.Chord: return Chord;
                case PartParam.Sweep: return Sweep;
                case PartParam.Incidence: return Incidence;
                case PartParam.Dihedral: return Dihedral;
                default: return double.NaN;
            }
        }

        public void Set(PartParam p, double v)
        {
            switch (p)
            {
                case PartParam.Length: Length = v; break;
                case PartParam.Diameter: Diameter = v; break;
                case PartParam.Top: Top = v; break;
                case PartParam.Span: Span = v; break;
                case PartParam.Chord: Chord = v; break;
                case PartParam.Sweep: Sweep = v; break;
                case PartParam.Incidence: Incidence = v; break;
                case PartParam.Dihedral: Dihedral = v; break;
            }
        }

        public bool HasParams
        {
            get
            {
                foreach (var p in PartCatalog.ParamOrder)
                    if (!double.IsNaN(Get(p))) return true;
                return false;
            }
        }

        /// <summary>Ключ кеша PartCatalog.Resolve: Id и заданные значения.</summary>
        public string ParamKey()
        {
            var sb = new StringBuilder(Id);
            foreach (var p in PartCatalog.ParamOrder)
            {
                double v = Get(p);
                sb.Append('|');
                if (!double.IsNaN(v)) sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Радиальная группа (§5.4, симметрия 2/3/4/6/8): N одинаковых боковых блоков вокруг детали Parent ядра. Parts — стек
    /// блока снизу вверх; Lift — подъём низа блока над низом детали-родителя, м.
    /// </summary>
    public sealed class CraftRadial
    {
        public int Parent;
        public int Symmetry = 4;
        public double Lift;
        public List<string> Parts = new List<string>();
        public int EngineStage = -1, SepStage = -1;
        /// <summary>Высота ввода парашютов блоков, м; NaN — штатная (как CraftPart.ChuteAltitude).</summary>
        public double ChuteAltitude = double.NaN;
        /// <summary>Возврат блоков с опорами (как CraftPart.Landing); null — по умолчанию.</summary>
        public RecoveryTarget? Landing;
    }

    /// <summary>
    /// Корабль из деталей (§5.4): стек ядра снизу вверх и радиальные группы. Сохраняется JSON (ToJson/FromJson) — тем же
    /// форматом записаны и готовые корабли CraftPresets. В полёт идёт скомпилированный VesselDesign (CraftCompiler).
    /// </summary>
    public sealed class Craft
    {
        public static readonly int[] Symmetries = { 2, 3, 4, 6, 8 };

        public string Name = "Новая ракета";
        public List<CraftPart> Stack = new List<CraftPart>();
        public List<CraftRadial> Radials = new List<CraftRadial>();

        public Craft Clone() => FromJson(ToJson());

        /// <summary>Вставить деталь в стек на место index (детали выше сдвигаются; ссылки радиальных групп — тоже).</summary>
        public void Insert(int index, string id)
        {
            index = Math.Max(0, Math.Min(index, Stack.Count));
            Stack.Insert(index, new CraftPart(id));
            foreach (var r in Radials)
                if (r.Parent >= index) r.Parent++;
        }

        /// <summary>Убрать деталь стека; радиальные группы на ней уходят вместе с ней.</summary>
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= Stack.Count) return;
            Stack.RemoveAt(index);
            Radials.RemoveAll(r => r.Parent == index);
            foreach (var r in Radials)
                if (r.Parent > index) r.Parent--;
        }

        /// <summary>Поменять детали стека местами (кнопки ▲▼ конструктора).</summary>
        public void Swap(int a, int b)
        {
            if (a < 0 || b < 0 || a >= Stack.Count || b >= Stack.Count || a == b) return;
            (Stack[a], Stack[b]) = (Stack[b], Stack[a]);
            foreach (var r in Radials)
                if (r.Parent == a) r.Parent = b;
                else if (r.Parent == b) r.Parent = a;
        }

        // ---------------------------------------------------------------- JSON

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"name\": ").Append(Json.Quote(Name)).Append(",\n  \"stack\": [");
            for (int i = 0; i < Stack.Count; i++)
                sb.Append(i == 0 ? "\n" : ",\n").Append("    { \"part\": ").Append(Json.Quote(Stack[i].Id))
                    .Append(", \"stage\": ").Append(Stack[i].Stage).Append(ParamsJson(Stack[i])).Append(" }");
            sb.Append(Stack.Count > 0 ? "\n  ],\n  \"radials\": [" : "],\n  \"radials\": [");
            for (int i = 0; i < Radials.Count; i++)
            {
                var r = Radials[i];
                sb.Append(i == 0 ? "\n" : ",\n").Append("    { \"parent\": ").Append(r.Parent).Append(", \"symmetry\": ").Append(r.Symmetry)
                    .Append(", \"lift\": ").Append(r.Lift.ToString("R", CultureInfo.InvariantCulture))
                    .Append(", \"engineStage\": ").Append(r.EngineStage).Append(", \"sepStage\": ").Append(r.SepStage).Append(ChuteJson(r.ChuteAltitude)).Append(LandingJson(r.Landing)).Append(", \"parts\": [");
                for (int j = 0; j < r.Parts.Count; j++) sb.Append(j == 0 ? "" : ", ").Append(Json.Quote(r.Parts[j]));
                sb.Append("] }");
            }
            sb.Append(Radials.Count > 0 ? "\n  ]\n}\n" : "]\n}\n");
            return sb.ToString();
        }

        /// <summary>Размеры детали — только заданные: сохранение без них читается и прежним кодом.</summary>
        static string ParamsJson(CraftPart cp)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < PartCatalog.ParamOrder.Length; k++)
            {
                double v = cp.Get(PartCatalog.ParamOrder[k]);
                if (!double.IsNaN(v))
                    sb.Append(", ").Append(Json.Quote(CraftPart.ParamKeys[k])).Append(": ").Append(v.ToString("R", CultureInfo.InvariantCulture));
            }
            sb.Append(ChuteJson(cp.ChuteAltitude)).Append(LandingJson(cp.Landing));
            return sb.ToString();
        }

        /// <summary>Возврат ступени — тоже только заданный, словом: "none", "site", "downrange".</summary>
        static string LandingJson(RecoveryTarget? m) => m == null ? ""
            : ", " + Json.Quote(CraftPart.LandingKey) + ": " + Json.Quote(LandingWord(m.Value));

        static string LandingWord(RecoveryTarget m) => m == RecoveryTarget.LaunchSite ? "site" : m == RecoveryTarget.Downrange ? "downrange" : "none";

        static RecoveryTarget? ParseLanding(Dictionary<string, object> p)
        {
            switch (Json.Str(p, CraftPart.LandingKey))
            {
                case "none": return RecoveryTarget.None;
                case "site": return RecoveryTarget.LaunchSite;
                case "downrange": return RecoveryTarget.Downrange;
                default: return null;
            }
        }

        /// <summary>Высота ввода парашюта — тоже только заданная: файлы без ключа открываются со штатной высотой.</summary>
        static string ChuteJson(double alt) => double.IsNaN(alt) ? ""
            : ", " + Json.Quote(CraftPart.ChuteKey) + ": " + alt.ToString("R", CultureInfo.InvariantCulture);

        public static Craft FromJson(string text)
        {
            var o = Json.Parse(text) as Dictionary<string, object> ?? throw new FormatException("Ожидался объект корабля");
            var c = new Craft { Name = Json.Str(o, "name") ?? "Без имени" };
            if (o.TryGetValue("stack", out var st) && st is List<object> sl)
                foreach (var e in sl)
                    if (e is Dictionary<string, object> p)
                    {
                        var cp = new CraftPart(Json.Str(p, "part"), (int)Json.Num(p, "stage", -1));
                        for (int k = 0; k < PartCatalog.ParamOrder.Length; k++)
                            cp.Set(PartCatalog.ParamOrder[k], Json.Num(p, CraftPart.ParamKeys[k], double.NaN));
                        cp.ChuteAltitude = Json.Num(p, CraftPart.ChuteKey, double.NaN);
                        cp.Landing = ParseLanding(p);
                        c.Stack.Add(cp);
                    }
            if (o.TryGetValue("radials", out var rt) && rt is List<object> rl)
                foreach (var e in rl)
                    if (e is Dictionary<string, object> p)
                    {
                        var r = new CraftRadial
                        {
                            Parent = (int)Json.Num(p, "parent", 0), Symmetry = (int)Json.Num(p, "symmetry", 2), Lift = Json.Num(p, "lift", 0),
                            EngineStage = (int)Json.Num(p, "engineStage", -1), SepStage = (int)Json.Num(p, "sepStage", -1),
                            ChuteAltitude = Json.Num(p, CraftPart.ChuteKey, double.NaN),
                            Landing = ParseLanding(p),
                        };
                        if (p.TryGetValue("parts", out var pp) && pp is List<object> pl)
                            foreach (var id in pl)
                                if (id is string s) r.Parts.Add(s);
                        c.Radials.Add(r);
                    }
            return c;
        }
    }

    /// <summary>Итог компиляции: проект для полёта, ошибки (не пускают на старт, §5.4), предупреждения и статистика.</summary>
    public sealed class CraftBuild
    {
        public VesselDesign Design;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public List<StageStats> Stats = new List<StageStats>();
        /// <summary>Секция проекта для каждой детали стека; низ детали над низом её секции, м.</summary>
        public int[] PartSection;
        public double[] PartOffset;
        /// <summary>Секция проекта для каждой радиальной группы.</summary>
        public int[] RadialSection;
        public double Mass, Height, Width, ComHeight;
        public bool Ok => Errors.Count == 0 && Design != null;
    }

    /// <summary>
    /// Craft → VesselDesign (§5.4). Стек режется на блоки разделителями (разделитель уходит с нижним блоком — как
    /// межступенчатый отсек) и основанием обтекателя; блок — секция ядра. Радиальные группы встают сразу после секции
    /// родителя. Ступени — группы шагов с WithPrevious: всё одного номера идёт одним нажатием пробела.
    /// </summary>
    public static class CraftCompiler
    {
        /// <summary>
        /// Площадка (§5.4 «помещается на площадку»): высота и размах, м. Пара: LaunchPadView — фермы стола рассчитаны на
        /// пакет не выше «Сатурна-5» (111 м) с запасом.
        /// </summary>
        public const double PadMaxHeight = 130, PadMaxWidth = 30;
        /// <summary>Зазор между ядром и боковым блоком, м (радиальный разделитель).</summary>
        public const double RadialGap = 0.15;
        /// <summary>Нос обтекателя сверх груза, в диаметрах: оживальный конус.</summary>
        const double FairingNose = 0.8, FairingClearance = 0.3;

        struct Block
        {
            public int From, To; // детали стека [From, To]
        }

        static List<Block> Blocks(Craft c, out int fairingPart, out int firstEnclosed)
        {
            var res = new List<Block>();
            fairingPart = -1;
            firstEnclosed = -1;
            int from = 0;
            bool enclosePending = false;
            for (int i = 0; i < c.Stack.Count; i++)
            {
                var p = PartCatalog.Resolve(c.Stack[i]);
                if (p == null) continue;
                bool cut = p.Decoupler;
                if (p.Fairing && fairingPart < 0)
                {
                    fairingPart = i;
                    // Разделитель прямо над основанием — груз отделяется от ступени под створками: режем по нему, а
                    // основание остаётся на ступени (как у «Кара-1»: обтекатель на II ступени, ПН — на своём разделителе).
                    var next = i + 1 < c.Stack.Count ? PartCatalog.Resolve(c.Stack[i + 1]) : null;
                    if (next != null && next.Decoupler) enclosePending = true;
                    else cut = true;
                }
                if (cut || i == c.Stack.Count - 1)
                {
                    res.Add(new Block { From = from, To = i });
                    from = i + 1;
                    if (fairingPart == i || enclosePending && p.Decoupler)
                    {
                        firstEnclosed = res.Count;
                        enclosePending = false;
                    }
                }
            }
            return res;
        }

        public static CraftBuild Compile(Craft craft)
        {
            var b = new CraftBuild();
            var c = craft.Clone();
            for (int i = 0; i < c.Stack.Count; i++)
                if (PartCatalog.Resolve(c.Stack[i]) == null) b.Errors.Add($"Неизвестная деталь «{c.Stack[i].Id}»");
            foreach (var r in c.Radials)
                foreach (var id in r.Parts)
                    if (PartCatalog.Get(id) == null) b.Errors.Add($"Неизвестная деталь «{id}»");
            if (c.Stack.Count == 0) b.Errors.Add("Пустой стек: поставьте хотя бы одну деталь");
            if (b.Errors.Count > 0) return b;
            if (HasUnset(c)) AutoStage(c, onlyUnset: true);

            var blocks = Blocks(c, out int fairingPart, out int firstEnclosed);
            int fairings = 0;
            foreach (var cp in c.Stack)
                if (PartCatalog.Resolve(cp).Fairing) fairings++;
            if (fairings > 1) b.Errors.Add("Обтекатель может быть только один");
            if (fairingPart >= 0 && firstEnclosed >= blocks.Count) b.Errors.Add("Над основанием обтекателя пусто — нечего укрывать");

            var d = new VesselDesign { Name = c.Name };
            b.PartSection = new int[c.Stack.Count];
            b.PartOffset = new double[c.Stack.Count];
            b.RadialSection = new int[c.Radials.Count];
            var blockSection = new int[blocks.Count];
            var partBlock = new int[c.Stack.Count];
            int stageNo = 0;
            for (int k = 0; k < blocks.Count; k++)
            {
                var bl = blocks[k];
                var parts = new List<PartDef>();
                double h = 0;
                for (int i = bl.From; i <= bl.To; i++)
                {
                    partBlock[i] = k;
                    b.PartOffset[i] = h;
                    var p = PartCatalog.Resolve(c.Stack[i]);
                    parts.Add(p);
                    h += p.Length;
                }
                var s = Section(parts, b, $"блок {k + 1}");
                AddWings(c, bl, s, b);
                if (s.HasEngine && s.Kind == SectionKind.Stage) s.Name = $"Ступень {++stageNo}";
                blockSection[k] = d.Sections.Count;
                for (int i = bl.From; i <= bl.To; i++) b.PartSection[i] = d.Sections.Count;
                d.Sections.Add(s);
                // Радиальные группы — сразу за родителем (VesselDesign.DetachedBy считает по RadialParent).
                for (int r = 0; r < c.Radials.Count; r++)
                {
                    var rad = c.Radials[r];
                    if (rad.Parent < 0 || rad.Parent >= c.Stack.Count || partBlock[rad.Parent] != k || rad.Parent < bl.From || rad.Parent > bl.To)
                        continue;
                    b.RadialSection[r] = d.Sections.Count;
                    d.Sections.Add(RadialSection(c, rad, r, s, blockSection[k], b.PartOffset[rad.Parent], b));
                }
            }
            for (int r = 0; r < c.Radials.Count; r++)
                if (c.Radials[r].Parent < 0 || c.Radials[r].Parent >= c.Stack.Count) b.Errors.Add($"Боковая группа {r + 1}: нет детали-родителя");
                else if (fairingPart >= 0 && c.Radials[r].Parent > fairingPart) b.Errors.Add("Боковые блоки под обтекателем не помещаются");

            int fairingSection = -1;
            if (fairingPart >= 0 && firstEnclosed < blocks.Count)
            {
                double encD = 0, encL = 0;
                for (int k = firstEnclosed; k < blocks.Count; k++)
                {
                    var s = d.Sections[blockSection[k]];
                    encD = Math.Max(encD, s.Diameter);
                    encL += s.Length;
                }
                var fp = PartCatalog.Resolve(c.Stack[fairingPart]);
                double fd = Math.Max(fp.Diameter, encD + FairingClearance);
                double fl = encL + FairingNose * fd;
                fairingSection = d.Sections.Count;
                d.Sections.Add(new SectionDef
                {
                    Name = "Головной обтекатель", Kind = SectionKind.Fairing, Diameter = fd, Length = fl,
                    DryMass = Math.Round(PartCatalog.FairingAreal * Math.PI * fd * fl), EnclosesBelow = blocks.Count - firstEnclosed,
                    MaxHeatFlux = 2e5,
                });
                if (encD > fp.Diameter + 0.01) b.Warnings.Add($"Груз шире основания обтекателя: створки Ø{fd:F1} м");
            }

            // ---------------------------------------------------------------- ступени
            var items = new List<(int stage, int order, StageAction a)>();
            for (int k = 0; k < blocks.Count; k++)
            {
                var bl = blocks[k];
                int sec = blockSection[k];
                int ign = int.MaxValue, chute = int.MaxValue;
                for (int i = bl.From; i <= bl.To; i++)
                {
                    var p = PartCatalog.Resolve(c.Stack[i]);
                    int st = c.Stack[i].Stage;
                    if (p.HasEngine) ign = Math.Min(ign, st);
                    if (p.ParachuteArea > 0) chute = Math.Min(chute, st);
                    if (p.Decoupler && i == bl.To)
                    {
                        if (k + 1 < blocks.Count) items.Add((st, 0, new StageAction(StageActionType.Separate, sec)));
                        else b.Warnings.Add("Разделитель на самом верху ничего не отделяет");
                    }
                    if (p.Fairing && fairingSection >= 0) items.Add((st, 2, new StageAction(StageActionType.JettisonFairing, fairingSection)));
                }
                if (ign != int.MaxValue && d.Sections[sec].HasEngine) items.Add((ign, 3, new StageAction(StageActionType.Ignite, sec)));
                if (chute != int.MaxValue) items.Add((chute, 4, new StageAction(StageActionType.DeployParachute, sec)));
            }
            for (int r = 0; r < c.Radials.Count; r++)
            {
                if (c.Radials[r].Parent < 0 || c.Radials[r].Parent >= c.Stack.Count) continue;
                int sec = b.RadialSection[r];
                items.Add((c.Radials[r].SepStage, 1, new StageAction(StageActionType.Separate, sec)));
                if (d.Sections[sec].HasEngine) items.Add((c.Radials[r].EngineStage, 3, new StageAction(StageActionType.Ignite, sec)));
            }
            items.Sort((x, y) => x.stage != y.stage ? x.stage.CompareTo(y.stage) : x.order != y.order ? x.order.CompareTo(y.order) : x.a.Section.CompareTo(y.a.Section));
            for (int i = 0; i < items.Count;)
            {
                int j = i;
                var group = new List<StageAction>();
                while (j < items.Count && items[j].stage == items[i].stage) group.Add(items[j++].a);
                // Отделение ядра и запуск следующей ступени одним нажатием — это Separate(igniteNext): с осадкой топлива (§6.3).
                for (int g = 0; g < group.Count; g++)
                {
                    var a = group[g];
                    if (a.Type != StageActionType.Separate || d.Sections[a.Section].IsRadial) continue;
                    int next = d.NextCore(a.Section);
                    int ig = group.FindIndex(x => x.Type == StageActionType.Ignite && x.Section == next);
                    if (ig < 0) continue;
                    group[g] = new StageAction(StageActionType.Separate, a.Section, igniteNext: true);
                    group.RemoveAt(ig);
                    if (ig < g) g--;
                }
                for (int g = 0; g < group.Count; g++)
                {
                    var a = group[g];
                    d.Sequence.Add(new StageAction(a.Type, a.Section, a.IgniteNext, false, withPrevious: g > 0));
                }
                i = j;
            }

            // Высота ввода парашюта (§4.8) — в секцию детали; несколько куполов в блоке раскрываются вместе (по последней).
            for (int i = 0; i < c.Stack.Count; i++)
                if (!double.IsNaN(c.Stack[i].ChuteAltitude) && PartCatalog.Resolve(c.Stack[i]).ParachuteArea > 0)
                    d.Sections[b.PartSection[i]].ChuteAltitude = FlightPhysics.ClampChuteAltitude(c.Stack[i].ChuteAltitude);
            for (int r = 0; r < c.Radials.Count; r++)
                if (!double.IsNaN(c.Radials[r].ChuteAltitude) && b.RadialSection[r] < d.Sections.Count && d.Sections[b.RadialSection[r]].ParachuteArea > 0)
                    d.Sections[b.RadialSection[r]].ChuteAltitude = FlightPhysics.ClampChuteAltitude(c.Radials[r].ChuteAltitude);

            // Возврат ступеней с опорами (§6.9): выбор игрока у детали опор, иначе RecoveryDef.DefaultFor.
            for (int i = 0; i < c.Stack.Count; i++)
                if (PartCatalog.Resolve(c.Stack[i]).LandingLegs)
                    SetLanding(d.Sections[b.PartSection[i]], c.Stack[i].Landing, b.PartSection[i] == 0);
            for (int r = 0; r < c.Radials.Count; r++)
                if (b.RadialSection[r] < d.Sections.Count && d.Sections[b.RadialSection[r]].LandingLegs)
                    SetLanding(d.Sections[b.RadialSection[r]], c.Radials[r].Landing, true);

            CheckJoints(c, fairingPart, b);
            b.Design = d;
            Check(c, b, blocks);
            return b;
        }

        static void SetLanding(SectionDef s, RecoveryTarget? choice, bool booster)
        {
            var mode = choice ?? RecoveryDef.DefaultFor(s, booster);
            s.Recovery = mode == RecoveryTarget.None || !s.HasEngine ? null : RecoveryDef.ForCraft(s, mode);
        }

        static SectionDef Section(List<PartDef> parts, CraftBuild b, string where)
        {
            var s = new SectionDef { Name = where, Kind = SectionKind.Payload, MaxHeatFlux = 0 };
            PartDef engine = null, command = null, main = null;
            double minFlux = double.MaxValue, maxFlux = 0;
            int lengthy = 0;
            foreach (var p in parts)
            {
                s.DryMass += p.DryMass;
                s.Propellant += p.Propellant;
                s.Length += p.Length;
                s.Diameter = Math.Max(s.Diameter, p.MaxDiameter);
                s.RcsTorque += p.RcsTorque;
                s.FinArea += p.FinArea;
                s.FlapArea += p.FlapArea;
                s.GridFinArea += p.GridFinArea;
                s.ParachuteArea += p.ParachuteArea;
                s.Crew += p.Crew;
                s.LandingLegs |= p.LandingLegs;
                if (p.LandingLegs) s.Deploy = DeployKind.Legs;
                s.DockingPort |= p.DockingPort;
                s.UllageMotors |= p.UllageMotors;
                if (p.MaxHeatFlux > 0)
                {
                    minFlux = Math.Min(minFlux, p.MaxHeatFlux);
                    maxFlux = Math.Max(maxFlux, p.MaxHeatFlux);
                }
                if (p.Category == PartCategory.Command && (command == null || p.Crew > command.Crew)) command = p;
                if (p.Length > 0)
                {
                    lengthy++;
                    main = p;
                }
                if (p.HasEngine)
                {
                    if (engine != null && engine.Id != p.Id)
                        b.Errors.Add($"{where}: разные двигатели ({engine.Name}, {p.Name}) в одном блоке — разделите их разделителем");
                    if (engine == null) s.Engine = p.Engine();
                    engine = p;
                    s.EngineCount += p.EngineCount;
                }
            }
            // Шасси главнее опор: секция с колёсами садится на полосу пробегом (Vessel.GearOut), а не на опоры.
            foreach (var p in parts)
                if (p.GearHeight > 0)
                {
                    s.Deploy = DeployKind.Gear;
                    s.GearHeight = Math.Max(s.GearHeight, p.GearHeight);
                }
            s.Length = Math.Max(s.Length, 0.3);
            if (s.Diameter <= 0) s.Diameter = 1;
            bool shield = command != null && command.MaxHeatFlux >= 1e6;
            if (shield)
            {
                s.Kind = SectionKind.Capsule;
                s.Name = command.Name;
                s.DragScale = command.DragScale;
                s.MaxHeatFlux = maxFlux;
            }
            else
            {
                if (engine != null) s.Kind = SectionKind.Stage;
                s.MaxHeatFlux = minFlux < double.MaxValue ? minFlux : 3e5;
                if (command != null) s.Name = command.Name;
                else if (engine == null && main != null) s.Name = main.Name;
                bool nose = parts.Count > 0 && parts[parts.Count - 1].NoseCone;
                if (nose) s.DragScale = parts[parts.Count - 1].DragScale;
            }
            // Готовая модель секции (FBX, §SectionModel) — только когда блок и есть эта одна деталь, иначе рисуем корпус.
            if (lengthy == 1 && main != null)
            {
                s.Model = main.Model;
                s.Sphere = main.Sphere;
            }
            return s;
        }

        /// <summary>
        /// Плоскости блока (§4.6) — из деталей с PartDef.Wing. Корень: задняя кромка — на стыке с деталью ниже в стеке, хорда
        /// идёт вверх по детали выше, так что фокус корневой хорды (WingDef.Height) — на 3/4 хорды выше стыка. Крыло и
        /// оперение — среднеплан (корень на оси), киль — на обшивке детали, к которой прижат (к −X, «верх» пакета).
        /// </summary>
        static void AddWings(Craft c, Block bl, SectionDef s, CraftBuild b)
        {
            for (int i = bl.From; i <= bl.To; i++)
            {
                var p = PartCatalog.Resolve(c.Stack[i]);
                if (p?.Wing == null) continue;
                // Несущая деталь: ближайшая с длиной выше в блоке, иначе ниже.
                double r = -1;
                for (int j = i + 1; j <= bl.To && r < 0; j++)
                {
                    var q = PartCatalog.Resolve(c.Stack[j]);
                    if (q.Length > 0) r = q.Diameter * 0.5;
                }
                for (int j = i - 1; j >= bl.From && r < 0; j--)
                {
                    var q = PartCatalog.Resolve(c.Stack[j]);
                    if (q.Length > 0) r = q.Top * 0.5;
                }
                if (r < 0) r = s.Radius;
                var w = p.Wing.Clone();
                w.Height = b.PartOffset[i] + 0.75 * p.Chord;
                w.Offset = w.Vertical ? r : 0;
                if (b.PartOffset[i] + p.Chord > s.Length + 1e-6)
                    b.Warnings.Add($"«{p.Name}»: хорда {p.Chord:0.##} м длиннее корпуса над стыком — сдвиньте ниже в стеке");
                (s.Wings ??= new List<WingDef>()).Add(w);
            }
        }

        /// <summary>
        /// Стыки стека: верх детали и низ следующей (детали длиной 0 — навесные, пропускаются) должны совпасть по диаметру,
        /// иначе ступенька — это лишнее сопротивление и неправильная картинка. Груз под обтекателем и само основание
        /// обтекателя не проверяются.
        /// Допуск 0,05 м — меньше шага диаметра в конструкторе (PartCatalog.Range, 0,1 м).
        /// </summary>
        static void CheckJoints(Craft c, int fairingPart, CraftBuild b)
        {
            PartDef prev = null;
            for (int i = 0; i < c.Stack.Count; i++)
            {
                if (fairingPart >= 0 && i > fairingPart) break;
                var p = PartCatalog.Resolve(c.Stack[i]);
                if (p == null || p.Length <= 0) continue;
                // Основание обтекателя шире ступени — штатная «молотоголовая» компоновка (Ø5,2 на Ø3,7 у «Кара-1»), не ступенька.
                if (prev != null && !p.Fairing && Math.Abs(prev.Top - p.Diameter) > 0.05)
                    b.Warnings.Add($"Стык Ø{prev.Top:0.##} → Ø{p.Diameter:0.##} м между «{prev.Name}» и «{p.Name}»: поставьте переходник");
                prev = p;
            }
        }

        static SectionDef RadialSection(Craft c, CraftRadial rad, int index, SectionDef parent, int parentSection, double parentOffset, CraftBuild b)
        {
            int n = rad.Symmetry;
            if (Array.IndexOf(Craft.Symmetries, n) < 0)
            {
                b.Errors.Add($"Боковая группа {index + 1}: симметрия {n} — допустимо 2, 3, 4, 6, 8");
                n = Math.Max(2, n);
            }
            var parts = new List<PartDef> { PartCatalog.Get("dec-radial") };
            foreach (var id in rad.Parts) parts.Add(PartCatalog.Get(id));
            if (rad.Parts.Count == 0) b.Errors.Add($"Боковая группа {index + 1}: пустая");
            var one = Section(parts, b, $"боковая группа {index + 1}");
            foreach (var p in parts)
                if (p?.Wing != null)
                {
                    // Aerodynamics.Collect пропускает радиальные секции: крыло на боковом блоке было бы мёртвым весом.
                    b.Warnings.Add($"Боковая группа {index + 1}: «{p.Name}» сбоку не работает — ставьте плоскости в стек ядра");
                    break;
                }
            var s = one.Clone();
            s.Name = one.HasEngine ? $"Боковые блоки ×{n}" : $"Боковые детали ×{n}";
            if (one.Kind == SectionKind.Capsule) b.Errors.Add("Командный модуль сбоку не ставится");
            s.Kind = one.HasEngine ? SectionKind.Stage : SectionKind.Payload;
            s.DryMass = one.DryMass * n;
            s.Propellant = one.Propellant * n;
            s.EngineCount = one.EngineCount * n;
            s.RcsTorque = one.RcsTorque * n;
            s.FinArea = one.FinArea * n;
            s.FlapArea = one.FlapArea * n;
            s.GridFinArea = one.GridFinArea * n;
            s.ParachuteArea = one.ParachuteArea * n;
            s.RadialCount = n;
            s.RadialParent = parentSection;
            s.RadialOffset = parent.Radius + one.Radius + RadialGap;
            s.RadialLift = parentOffset + rad.Lift;
            s.Model = SectionModel.None;
            if (s.RadialLift < -1e-6 && parentSection == 0) b.Errors.Add($"Боковая группа {index + 1} ниже днища ядра — не встанет на стол");
            // Боковые блоки не должны задевать соседа: хорда между центрами ≥ диаметра блока.
            double chord = 2 * s.RadialOffset * Math.Sin(Math.PI / n);
            if (chord < one.Diameter - 1e-6) b.Errors.Add($"Боковая группа {index + 1}: ×{n} блоков Ø{one.Diameter:F1} м не помещаются вокруг ядра");
            return s;
        }

        static void Check(Craft c, CraftBuild b, List<Block> blocks)
        {
            var d = b.Design;
            bool engine = false;
            foreach (var s in d.Sections) engine |= s.HasEngine;
            if (!engine) b.Errors.Add("Нет ни одного двигателя");
            if (engine && (d.Sequence.Count == 0 || d.Sequence[0].Type != StageActionType.Ignite))
                b.Warnings.Add("Первая ступень по пробелу — не запуск двигателя");
            try
            {
                b.Stats = d.ComputeStats();
            }
            catch (Exception e)
            {
                b.Errors.Add("Расчёт Δv: " + e.Message);
            }
            if (engine && b.Stats.Count > 0 && b.Stats[0].TwrSL < 1)
                b.Errors.Add($"TWR первой ступени у земли {b.Stats[0].TwrSL:F2} < 1 — не оторвётся");
            // Каждый разделитель с грузом над ним — в списке ступеней.
            for (int k = 0; k + 1 < blocks.Count; k++)
            {
                var top = PartCatalog.Resolve(c.Stack[blocks[k].To]);
                if (top.Decoupler && c.Stack[blocks[k].To].Stage < 0) b.Errors.Add($"Разделитель «{top.Name}» не стоит в ступенях");
            }
            bool control = false;
            foreach (var s in d.Sections) control |= s.RcsTorque > 0 || s.HasEngine && s.Engine.GimbalDeg > 0;
            if (!control) b.Warnings.Add("Нечем управлять ориентацией: нет RCS и качающихся двигателей");
            bool crewOrProbe = false;
            foreach (var cp in c.Stack) crewOrProbe |= PartCatalog.Resolve(cp).Category == PartCategory.Command;
            if (!crewOrProbe) b.Warnings.Add("Нет командного модуля или блока управления");

            var v = new Vessel(d, d.Name);
            v.MassProperties(out b.Mass, out b.ComHeight, out b.Height, out double maxR);
            b.Width = 2 * maxR;
            // Размах крыльев тоже должен пройти между фермами стола (PadMaxWidth).
            foreach (var s in d.Sections)
                if (s.Wings != null && !s.IsRadial)
                    foreach (var w in s.Wings)
                        b.Width = Math.Max(b.Width, w.Vertical ? maxR + w.Offset + w.Span : w.Span);
            if (b.Height > PadMaxHeight) b.Errors.Add($"Высота {b.Height:F0} м — не помещается на площадку (≤ {PadMaxHeight:F0} м)");
            if (b.Width > PadMaxWidth) b.Errors.Add($"Размах {b.Width:F0} м — не помещается на площадку (≤ {PadMaxWidth:F0} м)");
        }

        static bool HasUnset(Craft c)
        {
            foreach (var p in c.Stack)
                if (p.Stage < 0 && PartCatalog.Get(p.Id).Stageable) return true;
            foreach (var r in c.Radials)
                if (r.SepStage < 0 || r.EngineStage < 0) return true;
            return false;
        }

        /// <summary>
        /// Авторасстановка ступеней, как в KSP: [ядро + боковые запускаются] → [сброс боковых] → [отделение и запуск
        /// следующей] → [обтекатель] → … → [запуск двигателей, что ниже не взводились] → [парашюты].
        /// onlyUnset — заполнить только неназначенные (−1), сохранив ручную расстановку как есть.
        /// </summary>
        public static void AutoStage(Craft c, bool onlyUnset = false)
        {
            var blocks = Blocks(c, out int fairingPart, out _);
            var groups = new List<List<Action<int>>>();
            List<Action<int>> NewGroup()
            {
                var g = new List<Action<int>>();
                groups.Add(g);
                return g;
            }
            void SetPart(List<Action<int>> g, int i)
            {
                if (!onlyUnset || c.Stack[i].Stage < 0) g.Add(n => c.Stack[i].Stage = n);
            }
            bool[] engineDone = new bool[blocks.Count];
            int firstEngine = -1;
            for (int k = 0; k < blocks.Count && firstEngine < 0; k++)
                for (int i = blocks[k].From; i <= blocks[k].To; i++)
                    if (PartCatalog.Resolve(c.Stack[i])?.HasEngine == true) firstEngine = k;

            var launch = NewGroup();
            if (firstEngine >= 0)
            {
                engineDone[firstEngine] = true;
                for (int i = blocks[firstEngine].From; i <= blocks[firstEngine].To; i++)
                    if (PartCatalog.Resolve(c.Stack[i]).HasEngine) SetPart(launch, i);
            }
            foreach (var r in c.Radials)
            {
                var rr = r;
                if (!onlyUnset || rr.EngineStage < 0) launch.Add(n => rr.EngineStage = n);
            }
            if (c.Radials.Count > 0)
            {
                var sep = NewGroup();
                foreach (var r in c.Radials)
                {
                    var rr = r;
                    if (!onlyUnset || rr.SepStage < 0) sep.Add(n => rr.SepStage = n);
                }
            }
            bool fairingDone = fairingPart < 0;
            for (int k = 0; k < blocks.Count; k++)
            {
                int top = blocks[k].To;
                var tp = PartCatalog.Resolve(c.Stack[top]);
                if (tp == null || !tp.Decoupler || k + 1 >= blocks.Count) continue;
                var g = NewGroup();
                SetPart(g, top);
                if (!engineDone[k + 1])
                {
                    engineDone[k + 1] = true;
                    for (int i = blocks[k + 1].From; i <= blocks[k + 1].To; i++)
                        if (PartCatalog.Resolve(c.Stack[i]).HasEngine) SetPart(g, i);
                }
                if (!fairingDone)
                {
                    fairingDone = true;
                    SetPart(NewGroup(), fairingPart);
                }
            }
            if (!fairingDone) SetPart(NewGroup(), fairingPart);
            var rest = NewGroup();
            for (int k = 0; k < blocks.Count; k++)
                if (!engineDone[k])
                    for (int i = blocks[k].From; i <= blocks[k].To; i++)
                        if (PartCatalog.Resolve(c.Stack[i])?.HasEngine == true) SetPart(rest, i);
            var chutes = NewGroup();
            for (int i = 0; i < c.Stack.Count; i++)
                if (PartCatalog.Resolve(c.Stack[i])?.ParachuteArea > 0) SetPart(chutes, i);

            int n = 0;
            if (onlyUnset)
            {
                // Новые ступени — после уже назначенных, чтобы не перемешать ручную расстановку.
                foreach (var p in c.Stack) n = Math.Max(n, p.Stage + 1);
                foreach (var r in c.Radials) n = Math.Max(n, Math.Max(r.SepStage, r.EngineStage) + 1);
                if (n == 0) onlyUnset = false;
            }
            foreach (var g in groups)
            {
                if (g.Count == 0) continue;
                foreach (var set in g) set(n);
                n++;
            }
            // Нестадийные детали (баки, опоры) номера не держат.
            foreach (var p in c.Stack)
                if (PartCatalog.Get(p.Id)?.Stageable != true) p.Stage = -1;
        }

        /// <summary>Сжать номера ступеней до 0..N−1 без пропусков (после ручной правки в списке).</summary>
        public static void Normalize(Craft c)
        {
            var used = new SortedSet<int>();
            foreach (var p in c.Stack)
                if (p.Stage >= 0) used.Add(p.Stage);
            foreach (var r in c.Radials)
            {
                if (r.SepStage >= 0) used.Add(r.SepStage);
                if (r.EngineStage >= 0) used.Add(r.EngineStage);
            }
            var map = new Dictionary<int, int>();
            foreach (int u in used) map[u] = map.Count;
            foreach (var p in c.Stack)
                if (p.Stage >= 0) p.Stage = map[p.Stage];
            foreach (var r in c.Radials)
            {
                if (r.SepStage >= 0) r.SepStage = map[r.SepStage];
                if (r.EngineStage >= 0) r.EngineStage = map[r.EngineStage];
            }
        }
    }

    /// <summary>Готовые корабли конструктора — тем же форматом, что сохранения игрока (§5.4).</summary>
    public static class CraftPresets
    {
        /// <summary>
        /// Класс «Восток»: ядро и 4 боковых блока на РД-107, блок Е на РД-0110, под обтекателем — тормозная ДУ (КТДУ-417)
        /// и СА «Восток» на своих разделителях.
        /// </summary>
        public static Craft Semyorka()
        {
            var c = new Craft { Name = "Семёрка" };
            foreach (var id in new[] { "eng-rd107", "tank-2-4", "tank-2-4", "tank-2-4", "tank-2-4", "dec-2", "eng-rd0110", "tank-2-4", "fairing-3.7", "dec-2",
                "eng-ktdu417", "tank-1-1", "dec-1", "cmd-vostok" })
                c.Stack.Add(new CraftPart(id));
            c.Radials.Add(new CraftRadial { Parent = 1, Symmetry = 4, Parts = { "eng-rd107", "tank-2-4", "tank-2-4", "nose-2" } });
            CraftCompiler.AutoStage(c);
            return c;
        }

        /// <summary>Двухступенчатая на 9 × РД-К1 с макетом 10 т — эталон «Кара-1» из деталей.</summary>
        public static Craft Kara1()
        {
            var c = new Craft { Name = "Кара-1 из деталей" };
            foreach (var id in new[] { "eng-k1x9", "tank-3.7-4", "tank-3.7-4", "tank-3.7-2", "dec-3.7", "eng-k2v", "tank-3.7-2", "rcs", "fairing-5", "dec-3.7", "pl-10t" })
                c.Stack.Add(new CraftPart(id));
            CraftCompiler.AutoStage(c);
            return c;
        }

        public static IEnumerable<Craft> All()
        {
            yield return Semyorka();
            yield return Kara1();
        }
    }

    /// <summary>Минимальный JSON без зависимостей (ядро — чистый .NET без Unity и пакетов).</summary>
    public static class Json
    {
        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            return sb.Append('"').ToString();
        }

        public static string Str(Dictionary<string, object> o, string key) => o.TryGetValue(key, out var v) ? v as string : null;

        public static double Num(Dictionary<string, object> o, string key, double def) =>
            o.TryGetValue(key, out var v) && v is double d ? d : def;

        public static object Parse(string text)
        {
            int i = 0;
            var v = Value(text, ref i);
            Skip(text, ref i);
            if (i != text.Length) throw new FormatException($"JSON: лишнее в позиции {i}");
            return v;
        }

        static void Skip(string t, ref int i)
        {
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
        }

        static object Value(string t, ref int i)
        {
            Skip(t, ref i);
            if (i >= t.Length) throw new FormatException("JSON: неожиданный конец");
            char ch = t[i];
            if (ch == '{')
            {
                var o = new Dictionary<string, object>();
                i++;
                Skip(t, ref i);
                if (t[i] == '}') { i++; return o; }
                while (true)
                {
                    Skip(t, ref i);
                    string key = (string)Value(t, ref i);
                    Skip(t, ref i);
                    if (t[i++] != ':') throw new FormatException($"JSON: ожидалось ':' в позиции {i - 1}");
                    o[key] = Value(t, ref i);
                    Skip(t, ref i);
                    if (t[i] == ',') { i++; continue; }
                    if (t[i] == '}') { i++; return o; }
                    throw new FormatException($"JSON: ожидалось ',' или '}}' в позиции {i}");
                }
            }
            if (ch == '[')
            {
                var l = new List<object>();
                i++;
                Skip(t, ref i);
                if (t[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(t, ref i));
                    Skip(t, ref i);
                    if (t[i] == ',') { i++; continue; }
                    if (t[i] == ']') { i++; return l; }
                    throw new FormatException($"JSON: ожидалось ',' или ']' в позиции {i}");
                }
            }
            if (ch == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (t[i] != '"')
                {
                    if (t[i] == '\\')
                    {
                        i++;
                        char e = t[i++];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                sb.Append((char)Convert.ToInt32(t.Substring(i, 4), 16));
                                i += 4;
                                break;
                            default: sb.Append(e); break;
                        }
                    }
                    else sb.Append(t[i++]);
                }
                i++;
                return sb.ToString();
            }
            if (t.Length - i >= 4 && string.CompareOrdinal(t, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (t.Length - i >= 5 && string.CompareOrdinal(t, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (t.Length - i >= 4 && string.CompareOrdinal(t, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < t.Length && "+-0123456789.eE".IndexOf(t[i]) >= 0) i++;
            if (start == i) throw new FormatException($"JSON: неожиданный символ '{ch}' в позиции {i}");
            return double.Parse(t.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
