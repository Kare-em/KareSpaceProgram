using System;
using System.Collections.Generic;
using Kare.Space.Core;

// Инженерный расчёт конструктора (CraftAnalysis, GDD §5.4): бюджет Δv миссий против штатных ракет и перегрузка экипажа.
static partial class Program
{
    static void TestBuilder()
    {
        var sys = SolarSystem.CreateReal();
        var need = new Dictionary<string, double>();
        foreach (var def in MissionCatalog.All)
        {
            var bud = CraftAnalysis.Budget(def, sys);
            if (bud == null) { Console.WriteLine($"   {def.Id,-16} без бюджета (старт в воздухе)"); continue; }
            need[def.Id] = bud.Total;
            VesselDesign d = null;
            try { d = VesselPresets.ById(def.DesignId); } catch (Exception) { }
            double have = d != null ? CraftAnalysis.Available(d.ComputeStats()) : double.NaN;
            Console.WriteLine($"   {def.Id,-16} нужно {bud.Total,6:F0}, у штатной {have,6:F0} м/с{(bud.Assists ? " (гравманёвры)" : "")}");
            foreach (var l in bud.Legs) Console.WriteLine($"        {l.Name,-40} {l.DeltaV,6:F0}");
        }
        double N(string id) => need.TryGetValue(id, out var v) ? v : double.NaN;
        // Порядки величин из учебников: НОО ≈ 9,3 км/с, подскок на 100 км ≈ 2 км/с, Аполлон целиком ≈ 17–18 км/с.
        Check("Бюджет: «Восток» — НОО 9,0–9,6 км/с", N("vostok") > 9000 && N("vostok") < 9600, $"{N("vostok"):F0}");
        Check("Бюджет: линия Кармана 1,8–2,3 км/с", N("karman") > 1800 && N("karman") < 2300, $"{N("karman"):F0}");
        Check("Бюджет: Луна-9 (прямой спуск) больше «Мечты» на ~2,5–3 км/с",
            N("luna9") - N("mechta") > 2400 && N("luna9") - N("mechta") < 3100, $"{N("luna9") - N("mechta"):F0}");
        Check("Бюджет: Аполлон-11 16–19 км/с", N("apollo11") > 16000 && N("apollo11") < 19000, $"{N("apollo11"):F0}");
        Check("Бюджет: Аполлон-8 (орбита и домой) между «Мечтой» и Аполлоном-11",
            N("apollo8") > N("mechta") + 1000 && N("apollo8") < N("apollo11") - 2500, $"{N("apollo8"):F0}");
        Check("Бюджет: стыковка дороже простой орбиты", N("soyuz_tm31") > N("vostok"), $"{N("soyuz_tm31"):F0} vs {N("vostok"):F0}");
        // Канаверал южнее Байконура: восточный пуск получает больше от вращения Земли.
        Check("Бюджет: «Эксплорер» с мыса дешевле «Спутника» с Байконура", N("juno1") < N("sputnik"), $"{N("juno1"):F0} vs {N("sputnik"):F0}");

        // Штатные ракеты околоземных и лунных миссий проходят бюджет: оценка не должна пугать там, где история летала.
        // Без Аполлона-11 (15,9 против 17,8 км/с): сумма Δv последовательных ступеней не описывает схему с расстыковкой
        // на окололунной орбите — взлётная ступень не везёт посадочную, а командный модуль ждёт на орбите. Станции
        // (soyuz_tm31, crew_dragon) — тоже нет: станция в проекте секцией, но летает отдельным бортом.
        foreach (var id in new[] { "sputnik", "vostok", "karman", "juno1", "freedom7", "friendship7", "gemini3", "voskhod2", "mechta", "luna9", "luna17", "surveyor1", "apollo8", "sts1", "buran" })
        {
            var def = MissionCatalog.All.Find(m => m.Id == id);
            if (def == null) continue;
            var d = VesselPresets.ById(def.DesignId);
            double have = CraftAnalysis.Available(d.ComputeStats());
            Check($"Бюджет: штатная ракета «{id}» не ниже оценки", have >= N(id), $"{have:F0} vs {N(id):F0}");
        }

        // Перегрузка: участок с тягой 1 МН и расходом 100 кг/с от 20 до 5 т — выше 9 g, пока m < 1e6/(9·g0) ≈ 11 330 кг.
        var st = new StageStats();
        st.AddAccel(1e6, 100, 20000, 5000);
        double mLim = 1e6 / (FlightPhysics.CrewGLimit * Constants.G0);
        Check("Перегрузка: время выше 9 g по расходу", Math.Abs(st.HighGTime - (mLim - 5000) / 100) < 1e-6, $"{st.HighGTime:F2} с");
        Check("Перегрузка: максимум — на пустых баках", Math.Abs(st.MaxAccel - 200) < 1e-9, $"{st.MaxAccel:F1} м/с²");
        var dCrew = new VesselDesign { Name = "т" };
        dCrew.Sections.Add(new SectionDef { Name = "капсула", Crew = 1 });
        var warns = new List<string>();
        CraftAnalysis.CheckGLoad(dCrew, new List<StageStats> { st }, warns);
        Check("Перегрузка: экипаж — предупреждение", warns.Count == 1, warns.Count > 0 ? warns[0] : "");
        dCrew.Sections[0].Crew = 0;
        warns.Clear();
        CraftAnalysis.CheckGLoad(dCrew, new List<StageStats> { st }, warns);
        Check("Перегрузка: без экипажа молчит", warns.Count == 0);

        // Компилятор: MaxAccel совпадает с TWR (тяга/масса на конце прожига), бюджет — в предупреждениях.
        var sb = CraftCompiler.Compile(CraftPresets.Semyorka());
        bool consistent = sb.Ok && sb.Stats.Count > 0;
        foreach (var s in sb.Stats) consistent &= s.MaxAccel >= s.TwrVac * Constants.G0 * 0.999 && s.MaxAccel > 0;
        Check("Семёрка: максимум ускорения ступеней не меньше стартового", consistent);
        foreach (var s in sb.Stats) Console.WriteLine($"      {s.Name,-28} {s.MaxAccel / Constants.G0:F1} g, выше 9 g {s.HighGTime:F0} с");
        var vostok = MissionCatalog.All.Find(m => m.Id == "vostok");
        var apollo = MissionCatalog.All.Find(m => m.Id == "apollo11");
        var bv = CraftCompiler.Compile(CraftPresets.Semyorka(), CraftAnalysis.Budget(vostok, sys));
        var ba = CraftCompiler.Compile(CraftPresets.Semyorka(), CraftAnalysis.Budget(apollo, sys));
        Console.WriteLine($"   Семёрка: Δv {CraftAnalysis.Available(bv.Stats):F0} м/с");
        Check("Семёрка → Восток: бюджет в сборке, Δv хватает", bv.Budget != null && !bv.Warnings.Exists(w => w.Contains("не хватит")), string.Join("; ", bv.Warnings));
        Check("Семёрка → Аполлон-11: «не хватит»", ba.Warnings.Exists(w => w.Contains("не хватит")) && ba.Ok, string.Join("; ", ba.Warnings));
    }
}
