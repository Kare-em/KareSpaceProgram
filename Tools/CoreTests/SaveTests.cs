using System;
using System.Collections.Generic;
using System.Linq;
using Kare.Space.Core;

// Сохранение/загрузка полёта (§10.3): снимок → новая вселенная → оба мира шагают одинаково → траектории совпадают.
static partial class Program
{
    /// <summary>Допуск расхождения копии и оригинала, относительный. Ждём побитового совпадения; 1e-9 — запас на порядок обхода.</summary>
    const double SaveTolerance = 1e-9;

    static void TestSave()
    {
        // 1. «Восток» на подъёме: физика, автопилот выведения работает, идёт отделение боковушек.
        var (u, tr) = StartMission("vostok");
        u.Ascent = new AscentAutopilot { TargetAltitude = 200000 };
        u.SetWarp(2);
        Steps(u, tr, 60);
        var g = SaveRoundTrip("Восток на подъёме", u, tr, false);
        Check("сохранение: автопилот выведения восстановлен", g.Universe.Ascent != null, string.Join("; ", g.Dropped));
        Check("сохранение: ускорение сохранено", g.Universe.WarpIndex == u.WarpIndex, $"{g.Universe.WarpIndex}");
        // 250 с вперёд (кадр 0,1 с при ×10 — 1 с полёта) — через отделение блоков первой ступени и сброс обтекателя.
        Steps(u, tr, 250);
        Steps(g.Universe, g.Tracker, 250);
        CompareWorlds("Восток: +250 с после загрузки", u, g.Universe);
        // До орбиты (автопилот снимет себя сам) — дальше сравнение на рельсах.
        while (u.Ascent != null && u.Time < g.Universe.Time + 3000 && u.Active.Alive)
        {
            Steps(u, tr, 1);
            Steps(g.Universe, g.Tracker, 1);
        }
        CompareWorlds("Восток: выход на орбиту", u, g.Universe);
        Console.WriteLine($"   {OrbitText(u.Active, u.Time)}");

        // 2. На орбите: отделили блок «Е» (готова ТДУ), узел схода и автопилот узла — сохраняем уже копию с работающим
        // автопилотом: проверка и того, что загруженный мир снова сохраняется так же.
        foreach (var w in new[] { u, g.Universe })
        {
            w.Stage();
            w.SetNode(w.Time + 3000, -100, 0, 0);
            w.NodePilot = new NodeAutopilot();
        }
        var g2 = SaveRoundTrip("Восток на орбите с узлом", g.Universe, g.Tracker, false);
        Check("сохранение: узел манёвра и автопилот узла", g2.Universe.Active.Node != null && g2.Universe.Active.Node.Time == u.Active.Node.Time &&
              g2.Universe.NodePilot != null, string.Join("; ", g2.Dropped));
        foreach (var w in new[] { u, g2.Universe }) w.SetWarp(5);
        Steps(u, tr, 20);
        Steps(g2.Universe, g2.Tracker, 20);
        CompareWorlds("Восток: рельсы ×1000 после загрузки", u, g2.Universe);
        // Прожиг по узлу: автопилот сам сбросит ускорение, развернёт корабль и отработает импульс.
        for (int i = 0; i < 40000 && (u.NodePilot != null || g2.Universe.NodePilot != null); i++)
        {
            Steps(u, tr, 1);
            Steps(g2.Universe, g2.Tracker, 1);
        }
        CompareWorlds("Восток: прожиг по узлу", u, g2.Universe);
        Console.WriteLine($"   {OrbitText(u.Active, u.Time)}");

        // 3. Ракета из конструктора: проект пишется целиком, боковушки делят один проект и после загрузки.
        var b = CraftCompiler.Compile(CraftPresets.Semyorka());
        if (!b.Ok) { Check("сохранение: сборка Семёрки", false, string.Join("; ", b.Errors)); return; }
        var (cu, ctr) = StartMission("sputnik");
        cu.Vessels.Remove(cu.Active);
        cu.Launch(b.Design, MissionCatalog.Get("sputnik").SiteId);
        cu.Ascent = new AscentAutopilot { TargetAltitude = 200000 };
        cu.SetWarp(2);
        Steps(cu, ctr, 100);
        var cg = SaveRoundTrip("Семёрка из конструктора", cu, ctr, true);
        Check("сохранение: заголовок — своя ракета", cg.Header.CustomDesign && SaveGame.ReadHeader(SaveGame.Save(cu, ctr, true)).CustomDesign);
        Check("сохранение: проект Семёрки цел", cg.Universe.Active.Design.Sections.Count == b.Design.Sections.Count &&
              !ReferenceEquals(cg.Universe.Active.Design, b.Design), $"{cg.Universe.Active.Design.Sections.Count} секций");
        int designs = cu.Vessels.Select(v => v.Design).Distinct().Count();
        int loadedDesigns = cg.Universe.Vessels.Select(v => v.Design).Distinct().Count();
        Check("сохранение: общие проекты частей — по ссылке", designs == loadedDesigns, $"{designs} → {loadedDesigns} проектов на {cu.Vessels.Count} бортов");
        Steps(cu, ctr, 200);
        Steps(cg.Universe, cg.Tracker, 200);
        CompareWorlds("Семёрка: +200 с после загрузки", cu, cg.Universe);
    }

    /// <summary>Falcon 9: «миссия целиком» (Y) не сохраняется, фоновый автопилот посадки ступени (§6.9) — сохраняется.</summary>
    static void TestSaveBooster()
    {
        var (u, tr) = StartMission("crew_dragon");
        u.AutoWarp = true;
        u.Mission = new MissionAutopilot(u, tr);
        for (int i = 0; i < 20000 && !u.Recoveries.Any(p => p.Running); i++) Steps(u, tr, 1);
        Steps(u, tr, 100);
        if (!u.Recoveries.Any(p => p.Running)) { Check("сохранение: ступень на возврате", false); return; }
        var g = SaveRoundTrip("Falcon 9 после отделения", u, tr, false);
        Check("сохранение: «миссия целиком» выключена с пометкой", g.Universe.Mission == null && g.Dropped.Any(d => d.StartsWith("Mission")));
        Check("сохранение: автопилот посадки ступени восстановлен", g.Universe.Recoveries.Count == u.Recoveries.Count &&
              g.Universe.Recoveries.All(p => g.Universe.Vessels.Contains(p.Vessel)), $"{g.Universe.Recoveries.Count} из {u.Recoveries.Count}");
        // Оригинал — без «миссии целиком», как копия: дальше оба мира ведут одни частные автопилоты.
        u.Mission = null;
        for (int i = 0; i < 6000 && u.Recoveries.Any(p => p.Running); i++)
        {
            Steps(u, tr, 1);
            Steps(g.Universe, g.Tracker, 1);
        }
        var booster = u.Recoveries.FirstOrDefault()?.Vessel;
        Console.WriteLine($"   ступень: {booster?.Name}, {(booster != null && booster.IsLanded ? "села" : booster?.DestroyReason ?? "летит")}");
        CompareWorlds("Falcon 9: посадка ступени после загрузки", u, g.Universe);
    }

    static void Steps(Universe u, MissionTracker tr, int n)
    {
        for (int i = 0; i < n; i++)
        {
            u.Advance(0.1);
            tr?.Update(u);
        }
    }

    /// <summary>Снимок → загрузка → повторный снимок копии: тела файлов (без заголовка с часами) обязаны совпасть.</summary>
    static LoadedGame SaveRoundTrip(string what, Universe u, MissionTracker tr, bool custom)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string text = SaveGame.Save(u, tr, custom);
        double saveMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var g = SaveGame.Load(text, SolarSystem.CreateReal());
        double loadMs = sw.Elapsed.TotalMilliseconds;
        string again = SaveGame.Save(g.Universe, g.Tracker, custom);
        // Без заголовка (часы записи) и без списка «не сохранено»: у копии выключенного там уже нет.
        static string Body(string s)
        {
            int from = s.IndexOf('\n') + 1, to = s.LastIndexOf("\"dropped\"", StringComparison.Ordinal);
            return s.Substring(from, (to > from ? to : s.Length) - from);
        }
        var h = g.Header;
        Console.WriteLine($"   {what}: {text.Length / 1024.0:F0} КБ, запись {saveMs:F0} мс, чтение {loadMs:F0} мс; " +
                          $"«{h.MissionTitle}», {h.Vessel}, {h.Body}, h {h.Altitude / 1000:F1} км, бортов {u.Vessels.Count}" +
                          (g.Dropped.Count > 0 ? $"; без автопилотов: {string.Join("; ", g.Dropped)}" : ""));
        Check($"сохранение: {what} — повторный снимок копии тот же", Body(text) == Body(again), FirstDiff(Body(text), Body(again)));
        CompareWorlds($"{what}: сразу после загрузки", u, g.Universe);
        return g;
    }

    static string FirstDiff(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && a[i] == b[i]) i++;
        if (i == n && a.Length == b.Length) return "";
        int s = Math.Max(0, i - 120);
        return $"позиция {i}: «{a.Substring(s, Math.Min(240, a.Length - s))}» / «{b.Substring(s, Math.Min(240, b.Length - s))}»";
    }

    /// <summary>Борта сравниваются по порядку в списке: id у копии те же, но новые после загрузки получают разные.</summary>
    static void CompareWorlds(string what, Universe a, Universe b)
    {
        double worst = 0;
        long bitDiffs = 0, values = 0;
        string where = "";
        void Cmp(string name, double x, double y)
        {
            values++;
            if (BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y)) return;
            bitDiffs++;
            double rel = Math.Abs(x - y) / Math.Max(1e-300, Math.Max(Math.Abs(x), Math.Abs(y)));
            if (double.IsNaN(rel)) rel = double.PositiveInfinity;
            if (rel > worst) { worst = rel; where = $"{name}: {x:R} / {y:R}"; }
        }
        void Vec(string name, Vector3d x, Vector3d y)
        {
            Cmp(name + ".x", x.x, y.x);
            Cmp(name + ".y", x.y, y.y);
            Cmp(name + ".z", x.z, y.z);
        }
        Cmp("Time", a.Time, b.Time);
        bool same = a.Vessels.Count == b.Vessels.Count && a.Vessels.IndexOf(a.Active) == b.Vessels.IndexOf(b.Active);
        for (int i = 0; same && i < a.Vessels.Count; i++)
        {
            Vessel va = a.Vessels[i], vb = b.Vessels[i];
            string n = $"{va.Name}#{i}";
            same &= va.Body == vb.Body || va.Body.Id == vb.Body.Id;
            same &= va.Alive == vb.Alive && va.Situation == vb.Situation && va.Attached.SequenceEqual(vb.Attached);
            Vec(n + ".Position", va.Position, vb.Position);
            Vec(n + ".Velocity", va.Velocity, vb.Velocity);
            Vec(n + ".AngularVelocity", va.AngularVelocity, vb.AngularVelocity);
            Cmp(n + ".Attitude.x", va.Attitude.x, vb.Attitude.x);
            Cmp(n + ".Attitude.y", va.Attitude.y, vb.Attitude.y);
            Cmp(n + ".Attitude.z", va.Attitude.z, vb.Attitude.z);
            Cmp(n + ".Attitude.w", va.Attitude.w, vb.Attitude.w);
            for (int k = 0; k < va.Propellant.Length; k++) Cmp($"{n}.Propellant[{k}]", va.Propellant[k], vb.Propellant[k]);
        }
        bool ok = same && worst <= SaveTolerance;
        Check($"сохранение: {what}", ok,
              $"бортов {a.Vessels.Count}/{b.Vessels.Count}, значений {values}, " +
              (bitDiffs == 0 ? "побитово равны" : $"отличаются {bitDiffs}, худшее отн. {worst:E2} ({where})") + (same ? "" : ", состав/состояние бортов разошлись"));
    }
}
