using System;
using System.Diagnostics;
using Kare.Space.Core;

// Кометы Галлея и Хейла — Боппа (§2.3): эфемерида против известных дат, безопасность для поиска встреч и планировщика.
static partial class Program
{
    static double Jd(double jd) => (jd - Constants.JD_J2000) * Constants.Day;

    static void TestComets()
    {
        var s = SolarSystem.CreateReal();
        var halley = s.Get("halley");
        var hb = s.Get("halebopp");
        Check("comets: оба тела в системе", halley != null && hb != null);
        if (halley == null || hb == null) return;
        Check("comets: кометы — в конце списка (индексы планет не сдвинуты)", s.Get("charon").Index < halley.Index && halley.Index < hb.Index,
            $"charon {s.Get("charon").Index}, halley {halley.Index}, halebopp {hb.Index}");

        double au = Constants.AU;
        double Sun(CelestialBody b, double jd) => b.PositionAt(Jd(jd)).magnitude / au;
        double Earth(CelestialBody b, double jd) => (b.PositionAt(Jd(jd)) - s.Get("earth").PositionAt(Jd(jd))).magnitude / au;

        // Перигелии: Галлей 9.02.1986 — 0,587 а.е.; Хейл — Бопп 1.04.1997 — 0,914 а.е.
        double h0 = Sun(halley, 2446470.96), b0 = Sun(hb, 2450539.64);
        Check("comets: Галлей в перигелии 1986 — 0,587 а.е.", Math.Abs(h0 - 0.587) < 0.003, $"{h0:F4} а.е.");
        Check("comets: Хейл — Бопп в перигелии 1997 — 0,914 а.е.", Math.Abs(b0 - 0.914) < 0.002, $"{b0:F4} а.е.");
        // Ориентация орбиты (а не только форма): сближения с Землёй — Галлей 11.04.1986 на 0,42 а.е.,
        // Хейл — Бопп 22.03.1997 на 1,315 а.е.
        double he = Earth(halley, 2446531.5), be = Earth(hb, 2450529.5);
        Check("comets: Галлей у Земли 11.04.1986 — 0,42 а.е.", Math.Abs(he - 0.42) < 0.03, $"{he:F3} а.е.");
        Check("comets: Хейл — Бопп у Земли 22.03.1997 — 1,315 а.е.", Math.Abs(be - 1.315) < 0.03, $"{be:F3} а.е.");
        // Хейл — Бопп открыт 23.07.1995 на 7,2 а.е. от Солнца.
        double bDisc = Sun(hb, 2449921.5);
        Check("comets: Хейл — Бопп при открытии 1995 — 7,2 а.е.", Math.Abs(bDisc - 7.2) < 0.2, $"{bDisc:F2} а.е.");

        // Диапазон дат игры: миссии 1957–2030-е, проверяем с запасом 1950–2100 — конечные значения, r в [q, Q].
        double hq = halley.KeplerA * (1 - halley.KeplerE), hQ = halley.KeplerA * (1 + halley.KeplerE);
        bool finite = true, inRange = true;
        for (double y = 1950; y <= 2100; y += 0.25)
        {
            double t = (y - 2000) * 365.25 * Constants.Day;
            foreach (var b in new[] { halley, hb })
            {
                b.LocalStateAt(t, out var r, out var v);
                if (double.IsNaN(r.x + r.y + r.z + v.x + v.y + v.z) || double.IsInfinity(r.magnitude)) finite = false;
            }
            halley.LocalStateAt(t, out var rh, out _);
            if (rh.magnitude < hq * 0.999 || rh.magnitude > hQ * 1.001) inRange = false;
        }
        Check("comets: 1950–2100 — состояния конечны", finite);
        Check("comets: Галлей 1950–2100 между q и Q", inRange);
        // Следующий перигелий Галлея: реальный — 28.07.2061; без возмущений модель уходит на месяцы — фиксируем порядок.
        var ho = halley.OrbitAt(0);
        double next = Constants.JD_J2000 + (ho.TimeToPeriapsis(0)) / Constants.Day;
        double years = (next - 2446470.96) / 365.25;
        Check("comets: период Галлея ≈ 75 лет, перигелий 2061 ± 1 год", Math.Abs(next - 2474033.5) < 365, $"JD {next:F1} ({years:F2} лет), реальный JD 2474033.5");
        Check("comets: OrbitAt — копия с эпохой t", ho.Epoch == 0 && !ReferenceEquals(ho, halley.OrbitAt(0)));

        // Сфера влияния крошечная, но выше «пола рельсов»; атмосферы нет.
        foreach (var b in new[] { halley, hb })
        {
            double floor = PatchedConics.RailsFloorRadius(b);
            Check($"comets: {b.Name} — сфера {b.SoiRadius / 1000:F0} км > пола рельсов {floor / 1000:F1} км, нет атмосферы",
                b.SoiRadius > floor && b.SoiRadius < 1e6 && !b.HasAtmosphere && b.IsComet && !b.IsGasGiant);
        }

        // LocalStateAt зовётся на каждом шаге поиска встреч — не должен аллоцировать.
        halley.LocalStateAt(1e8, out _, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) hb.LocalStateAt(i * 1e5, out _, out _);
        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("comets: LocalStateAt без аллокаций", alloc == 0, $"{alloc} байт на 10000 вызовов");

        // Планировщик с кометой-целью: не падает и не виснет (ответ — сообщение или узел).
        foreach (var id in new[] { "halley", "halebopp" })
        {
            var (u, _) = StartMission("sputnik");
            u.Teleport(u.System.Get("earth"), 200e3, true);
            var sw = Stopwatch.StartNew();
            string msg = ManeuverAutoPlan.Plan(u, u.System.Get(id), out var plan);
            long ms = sw.ElapsedMilliseconds;
            Console.WriteLine($"   Земля → {id}: {msg} ({ms} мс)");
            Check($"comets: автоплан к {id} отвечает", msg != null && ms < 60000, $"{ms} мс, узел {(plan != null ? "есть" : "нет")}");
            // Прогноз сфер на 6 участков с кометами в списке детей Солнца: время в разумных пределах.
            var v = u.Active;
            sw.Restart();
            var ps = PatchedConics.Predict(v.Body, v.Position, v.Velocity, u.Time, v.Node, 6);
            Check($"comets: прогноз с узлом к {id} строится", ps != null && ps.Count > 0, $"{ps?.Count} участков, {sw.ElapsedMilliseconds} мс");
        }
    }
}
