using System;
using System.Collections.Generic;
using System.Linq;
using Kare.Space.Core;

// Карьера (GDD §8): цены пуска, дерево технологий, контракты, бюджет, сохранение и наблюдатель полёта.
static partial class Program
{
    static void TestCareer()
    {
        // ---- цены (§8.4)
        double kara1 = CareerPricing.CraftPrice(CraftPresets.Kara1());
        double kara1Sec = CareerPricing.DesignPrice(VesselPresets.Kara1Heavy());
        double r7 = CareerPricing.DesignPrice(VesselPresets.ById("vostok"));
        double semyorka = CareerPricing.CraftPrice(CraftPresets.Semyorka());
        double saturn = CareerPricing.DesignPrice(VesselPresets.ById("apollo11"));
        Console.WriteLine($"   цены, M₢: «Кара-1» из деталей {kara1:F1}, по секциям {kara1Sec:F1}; Р-7 «Восток» {r7:F1}, " +
                          $"«Семёрка» из деталей {semyorka:F1}; «Сатурн V» {saturn:F1}");
        Check("«Кара-1» из деталей ≈ 60 M₢ (§8.4)", kara1 > 45 && kara1 < 75, $"{kara1:F1}");
        Check("Р-7 дешевле «Кара-1», дороже 20", r7 > 20 && r7 < kara1Sec, $"{r7:F1}");
        Check("«Сатурн V» 200–450 M₢", saturn > 200 && saturn < 450, $"{saturn:F1}");
        Check("своя «Семёрка» в пределах ×1,6 от пресета", semyorka / r7 < 1.6 && r7 / semyorka < 1.6, $"{semyorka:F1} / {r7:F1}");

        // ---- дерево технологий (§8.2)
        Career.Current = null;
        Check("песочница: всё открыто", Career.IsPartUnlocked("cmd-apollo") && Career.IsPartUnlocked(PartCatalog.Get("eng-f1x5")));
        var s = new CareerState();
        Check("старт: бак Ø2 и РД-107 открыты", s.IsPartUnlocked("tank-2-4") && s.IsPartUnlocked("eng-rd107"));
        Check("старт: «Аполлон» и баки Ø10 закрыты", !s.IsPartUnlocked("cmd-apollo") && !s.IsPartUnlocked("tank-10-4"));
        Check("деталь вне дерева открыта", s.IsPartUnlocked("no-such-part"));
        int inTree = PartCatalog.All.Count(p => CareerTech.NodeFor(p.Id) != null);
        var outside = PartCatalog.All.Where(p => CareerTech.NodeFor(p.Id) == null).Select(p => p.Id).ToList();
        Check("все детали каталога в дереве", outside.Count == 0, $"{inTree} в дереве; вне: {string.Join(", ", outside)}");
        var locked = CareerPricing.LockedParts(CraftPresets.Semyorka(), s);
        Check("«Семёрка»: закрыты только корабль «Восток» и ТДУ", locked.Count == 2 && locked.Contains("cmd-vostok") && locked.Contains("eng-ktdu417"),
            string.Join(", ", locked));
        foreach (var n in CareerTech.Nodes)
            foreach (var r in n.Requires)
                if (CareerTech.Get(r) == null) Check($"узел {n.Id}: требование {r} есть", false);
        var heavy = CareerTech.Get("heavy");
        Check("узел без требований закрыт", s.NodeBlock(heavy)?.StartsWith("нужно") == true, s.NodeBlock(heavy));
        var sh = new CareerState { Science = 1000, Funds = 1000 };
        sh.Tech.Add("medium");
        Check("узел 1965 г. в 1957 закрыт по дате (ранний доступ — 3 года)", sh.NodeBlock(heavy)?.StartsWith("не раньше") == true, sh.NodeBlock(heavy));
        var probes = CareerTech.Get("probes");
        Check("без науки узел не купить", !s.Buy(probes), s.NodeBlock(probes));
        s.Science = 100;
        s.NodePrice(probes, out double ps, out _);
        Check("ранний доступ: 1959 в 1957 — цена ×2", Math.Abs(ps - 2 * probes.Science) < 1e-9, $"{ps} S");
        Check("купить «Автоматические станции»", s.Buy(probes) && s.IsPartUnlocked("eng-ktdu417") && Math.Abs(s.Science - (100 - ps)) < 1e-9);

        // ---- контракты (§7.3) и гонка (§6.1)
        CareerMissions.Register();
        CareerMissions.Register();
        Check("миссии карьеры добавлены один раз", MissionCatalog.All.Count(m => m.Id == CareerMissions.Venera) == 1);
        foreach (var c in CareerContracts.All)
        {
            if (MissionCatalog.Get(c.MissionId) == null) Check($"контракт {c.Id}: миссия {c.MissionId} есть", false);
            foreach (var r in c.Requires)
                if (CareerContracts.Get(r) == null) Check($"контракт {c.Id}: требование {r} есть", false);
        }
        Check($"контрактов {CareerContracts.All.Count}, миссии и требования на месте", true);
        var st = new CareerState();
        var apollo = CareerContracts.Get("apollo11");
        Check("«Аполлон-11» закрыт в начале", st.ContractBlock(apollo) != null && st.Complete(apollo, 0) == null, st.ContractBlock(apollo));
        var sp = CareerContracts.Get("sputnik");
        double funds0 = st.Funds;
        var rw = st.Complete(sp, MissionCatalog.Get("sputnik").StartTime + 3600);
        Check("«Спутник» засчитан: деньги, наука, престиж ×(1 + 120/365)", rw != null && st.Funds == funds0 + sp.Funds && st.Science == sp.Science
            && rw.Reputation > sp.Reputation * 1.3 && rw.Reputation < sp.Reputation * 1.4, $"P {rw?.Reputation}, {rw?.RaceNote}");
        Check("повтор контракта не засчитывается", st.Complete(sp, 0) == null);
        Check("гонка: на 2 года позже — ×0,5·e^−1", Math.Abs(CareerState.RaceFactor(-2 * 365.25) - 0.5 * Math.Exp(-1)) < 1e-9);

        // ---- бюджет (§8.1, §8.3)
        var b = new CareerState { Funds = 0 };
        b.AdvanceDate(GameCalendar.ToGameTime(1960, 6, 1));
        double expect = Math.Round(CareerState.BaseBudget(1958)) + Math.Round(CareerState.BaseBudget(1959)) + Math.Round(CareerState.BaseBudget(1960));
        Check("бюджет за 1958–1960 начислен", Math.Abs(b.Funds - expect) < 1e-9, $"{b.Funds} из {expect}");
        var d = new CareerState { Funds = -10 };
        d.WaitNextYear();
        Check("дефицит режет бюджет на 25 %", Math.Abs(d.Funds - (-10 + Math.Round(0.75 * CareerState.BaseBudget(1958)))) < 1e-9, $"{d.Funds}");
        d.AdvanceDate(GameCalendar.ToGameTime(1957, 3, 1));
        Check("назад дата не идёт", d.Year == 1958, $"{d.Year}");

        // ---- сохранение
        st.ScienceDone.Add("moon:orbit");
        st.Tech.Add("capsule");
        var back = CareerState.FromJson(st.ToJson());
        Check("JSON карьеры туда-обратно", back.Funds == st.Funds && back.Science == st.Science && back.Reputation == st.Reputation
            && back.Date == st.Date && back.Completed.SetEquals(st.Completed) && back.Tech.SetEquals(st.Tech)
            && back.ScienceDone.SetEquals(st.ScienceDone) && back.Log.Count == st.Log.Count);

        // ---- полёт: наука и возврат (karman — космос и возврат капсулы, sputnik — орбита)
        var cs = new CareerState();
        FlyCareer("karman", cs);
        Check("«Линия Кармана»: наука «космос» и «возвращение»", cs.ScienceDone.Contains("earth:space") && cs.ScienceDone.Contains("earth:return"),
            string.Join(", ", cs.ScienceDone));
        Check("«Линия Кармана»: возврат денег за капсулу", cs.Log.Any(l => l.StartsWith("Возврат")), $"{cs.Funds:F1} M₢");
        Check("«Линия Кармана»: контракт засчитан", cs.Completed.Contains("karman"));
        FlyCareer("sputnik", cs);
        Check("«Спутник»: наука «орбита Земли»", cs.ScienceDone.Contains("earth:orbit"), string.Join(", ", cs.ScienceDone));
        Check("«Спутник»: контракт засчитан", cs.Completed.Contains("sputnik"));
        Console.WriteLine($"   итог: {cs.Funds:F1} M₢, {cs.Science:F0} S, {cs.Reputation:F0} P");
    }

    /// <summary>Пуск миссии автопилотом с наблюдателем карьеры: оплата по секциям, наука, контракт по успеху.</summary>
    static void FlyCareer(string id, CareerState state)
    {
        var (u, tr) = StartMission(id);
        state.Charge(CareerPricing.DesignPrice(u.Active.Design), u.Active.Design.Name);
        var obs = new CareerObserver(state);
        obs.Message += m => Console.WriteLine($"   ☆ {m}");
        u.AutoWarp = true;
        u.Mission = new MissionAutopilot(u, tr);
        for (int f = 0; f < 60000 && tr.Status == MissionStatus.Active; f++)
        {
            u.Advance(0.1);
            tr.Update(u);
            obs.Update(u);
        }
        // Капсула «Кармана» садится уже после успеха — досмотреть посадку.
        for (int f = 0; f < 20000 && u.Vessels.Any(v => v.Alive && !v.IsLanded && !v.IsDebris); f++)
        {
            u.Advance(0.1);
            obs.Update(u);
        }
        if (tr.Status == MissionStatus.Success) state.Complete(CareerContracts.ForMission(id), u.Time);
        Console.WriteLine($"   {id}: {tr.Status}, {state.Funds:F1} M₢, {state.Science:F0} S");
    }
}
