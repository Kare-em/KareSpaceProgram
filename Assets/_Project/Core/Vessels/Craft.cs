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

        public CraftPart() { }
        public CraftPart(string id, int stage = -1) { Id = id; Stage = stage; }
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
                    .Append(", \"stage\": ").Append(Stack[i].Stage).Append(" }");
            sb.Append(Stack.Count > 0 ? "\n  ],\n  \"radials\": [" : "],\n  \"radials\": [");
            for (int i = 0; i < Radials.Count; i++)
            {
                var r = Radials[i];
                sb.Append(i == 0 ? "\n" : ",\n").Append("    { \"parent\": ").Append(r.Parent).Append(", \"symmetry\": ").Append(r.Symmetry)
                    .Append(", \"lift\": ").Append(r.Lift.ToString("R", CultureInfo.InvariantCulture))
                    .Append(", \"engineStage\": ").Append(r.EngineStage).Append(", \"sepStage\": ").Append(r.SepStage).Append(", \"parts\": [");
                for (int j = 0; j < r.Parts.Count; j++) sb.Append(j == 0 ? "" : ", ").Append(Json.Quote(r.Parts[j]));
                sb.Append("] }");
            }
            sb.Append(Radials.Count > 0 ? "\n  ]\n}\n" : "]\n}\n");
            return sb.ToString();
        }

        public static Craft FromJson(string text)
        {
            var o = Json.Parse(text) as Dictionary<string, object> ?? throw new FormatException("Ожидался объект корабля");
            var c = new Craft { Name = Json.Str(o, "name") ?? "Без имени" };
            if (o.TryGetValue("stack", out var st) && st is List<object> sl)
                foreach (var e in sl)
                    if (e is Dictionary<string, object> p)
                        c.Stack.Add(new CraftPart(Json.Str(p, "part"), (int)Json.Num(p, "stage", -1)));
            if (o.TryGetValue("radials", out var rt) && rt is List<object> rl)
                foreach (var e in rl)
                    if (e is Dictionary<string, object> p)
                    {
                        var r = new CraftRadial
                        {
                            Parent = (int)Json.Num(p, "parent", 0), Symmetry = (int)Json.Num(p, "symmetry", 2), Lift = Json.Num(p, "lift", 0),
                            EngineStage = (int)Json.Num(p, "engineStage", -1), SepStage = (int)Json.Num(p, "sepStage", -1),
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
                var p = PartCatalog.Get(c.Stack[i].Id);
                if (p == null) continue;
                bool cut = p.Decoupler;
                if (p.Fairing && fairingPart < 0)
                {
                    fairingPart = i;
                    // Разделитель прямо над основанием — груз отделяется от ступени под створками: режем по нему, а
                    // основание остаётся на ступени (как у «Кара-1»: обтекатель на II ступени, ПН — на своём разделителе).
                    var next = i + 1 < c.Stack.Count ? PartCatalog.Get(c.Stack[i + 1].Id) : null;
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
                if (PartCatalog.Get(c.Stack[i].Id) == null) b.Errors.Add($"Неизвестная деталь «{c.Stack[i].Id}»");
            foreach (var r in c.Radials)
                foreach (var id in r.Parts)
                    if (PartCatalog.Get(id) == null) b.Errors.Add($"Неизвестная деталь «{id}»");
            if (c.Stack.Count == 0) b.Errors.Add("Пустой стек: поставьте хотя бы одну деталь");
            if (b.Errors.Count > 0) return b;
            if (HasUnset(c)) AutoStage(c, onlyUnset: true);

            var blocks = Blocks(c, out int fairingPart, out int firstEnclosed);
            int fairings = 0;
            foreach (var cp in c.Stack)
                if (PartCatalog.Get(cp.Id).Fairing) fairings++;
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
                    var p = PartCatalog.Get(c.Stack[i].Id);
                    parts.Add(p);
                    h += p.Length;
                }
                var s = Section(parts, b, $"блок {k + 1}");
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
                var fp = PartCatalog.Get(c.Stack[fairingPart].Id);
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
                    var p = PartCatalog.Get(c.Stack[i].Id);
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

            b.Design = d;
            Check(c, b, blocks);
            return b;
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
                s.ParachuteArea += p.ParachuteArea;
                s.Crew += p.Crew;
                s.LandingLegs |= p.LandingLegs;
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
            var s = one.Clone();
            s.Name = one.HasEngine ? $"Боковые блоки ×{n}" : $"Боковые детали ×{n}";
            if (one.Kind == SectionKind.Capsule) b.Errors.Add("Командный модуль сбоку не ставится");
            s.Kind = one.HasEngine ? SectionKind.Stage : SectionKind.Payload;
            s.DryMass = one.DryMass * n;
            s.Propellant = one.Propellant * n;
            s.EngineCount = one.EngineCount * n;
            s.RcsTorque = one.RcsTorque * n;
            s.FinArea = one.FinArea * n;
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
                var top = PartCatalog.Get(c.Stack[blocks[k].To].Id);
                if (top.Decoupler && c.Stack[blocks[k].To].Stage < 0) b.Errors.Add($"Разделитель «{top.Name}» не стоит в ступенях");
            }
            bool control = false;
            foreach (var s in d.Sections) control |= s.RcsTorque > 0 || s.HasEngine && s.Engine.GimbalDeg > 0;
            if (!control) b.Warnings.Add("Нечем управлять ориентацией: нет RCS и качающихся двигателей");
            bool crewOrProbe = false;
            foreach (var cp in c.Stack) crewOrProbe |= PartCatalog.Get(cp.Id).Category == PartCategory.Command;
            if (!crewOrProbe) b.Warnings.Add("Нет командного модуля или блока управления");

            var v = new Vessel(d, d.Name);
            v.MassProperties(out b.Mass, out b.ComHeight, out b.Height, out double maxR);
            b.Width = 2 * maxR;
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
                    if (PartCatalog.Get(c.Stack[i].Id)?.HasEngine == true) firstEngine = k;

            var launch = NewGroup();
            if (firstEngine >= 0)
            {
                engineDone[firstEngine] = true;
                for (int i = blocks[firstEngine].From; i <= blocks[firstEngine].To; i++)
                    if (PartCatalog.Get(c.Stack[i].Id).HasEngine) SetPart(launch, i);
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
                var tp = PartCatalog.Get(c.Stack[top].Id);
                if (tp == null || !tp.Decoupler || k + 1 >= blocks.Count) continue;
                var g = NewGroup();
                SetPart(g, top);
                if (!engineDone[k + 1])
                {
                    engineDone[k + 1] = true;
                    for (int i = blocks[k + 1].From; i <= blocks[k + 1].To; i++)
                        if (PartCatalog.Get(c.Stack[i].Id).HasEngine) SetPart(g, i);
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
                        if (PartCatalog.Get(c.Stack[i].Id)?.HasEngine == true) SetPart(rest, i);
            var chutes = NewGroup();
            for (int i = 0; i < c.Stack.Count; i++)
                if (PartCatalog.Get(c.Stack[i].Id)?.ParachuteArea > 0) SetPart(chutes, i);

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
