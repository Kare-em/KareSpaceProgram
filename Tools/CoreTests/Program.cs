using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Kare.Space.Core;

// Прогон ядра без Unity: математика, эфемериды, атмосфера и полные полёты миссий автопилотом.
// Запуск: dotnet run --project Tools/CoreTests [имя теста]
static class Program
{
    static int passed, failed;

    static int Main(string[] args)
    {
        string only = args.Length > 0 ? args[0] : null;
        // В игре повреждения по умолчанию выключены; автопилоты проверяем по строгим правилам.
        FlightPhysics.AeroBreakup = FlightPhysics.HeatDamage = FlightPhysics.GLoadLimit = true;
        // Та же карта суши, что в игре: посадки и приводнения проверяются на реальной географии.
        string land = null;
        for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null && land == null; dir = dir.Parent)
        {
            var f = System.IO.Path.Combine(dir.FullName, "Assets/_Project/Data/EarthLand.bytes");
            if (System.IO.File.Exists(f)) land = f;
        }
        // Копия тестов вне репозитория (параллельные прогоны): корень — из KARE_ROOT или по умолчанию.
        if (land == null)
        {
            var root = Environment.GetEnvironmentVariable("KARE_ROOT") ?? "C:/CocosGames/KareSpaceProgram";
            var f = System.IO.Path.Combine(root, "Assets/_Project/Data/EarthLand.bytes");
            if (System.IO.File.Exists(f)) land = f;
        }
        if (land != null) SolarSystem.EarthLand = new LandMap(System.IO.File.ReadAllBytes(land));
        else Console.WriteLine("   карта суши не найдена — процедурные материки");
        // Реальные карты высот тел (§2.8, Tools/bake-dem.py): посадки идут по настоящему рельефу. KARE_NO_DEM=1 —
        // прежний процедурный рельеф (сравнение).
        if (land != null && Environment.GetEnvironmentVariable("KARE_NO_DEM") == null)
            foreach (var (id, name) in new[] { ("earth", "Earth"), ("moon", "Moon"), ("mars", "Mars"), ("mercury", "Mercury"), ("venus", "Venus") })
            {
                var f = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(land), name + "Height.bytes");
                if (System.IO.File.Exists(f)) SolarSystem.HeightMaps[id] = new HeightMap(System.IO.File.ReadAllBytes(f));
            }
        // Подробные вставки DEM у земных площадок (bake-dem.py patches): нет файла — как раньше, только 0,1°.
        if (land != null && SolarSystem.HeightMaps.TryGetValue("earth", out var earthMap))
        {
            var f = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(land), "EarthPatches.bytes");
            if (System.IO.File.Exists(f)) earthMap.AddPatches(System.IO.File.ReadAllBytes(f));
        }
        Run("math", TestMath, only);
        Run("orbit", TestOrbit, only);
        Run("moon", TestEphemeris, only);
        Run("atmo", TestAtmosphere, only);
        Run("stats", TestStats, only);
        Run("stability", TestStability, only);
        Run("separation", TestSeparation, only);
        Run("craft", TestCraft, only);
        Run("craftparams", TestCraftParams, only);
        Run("collide", TestCollide, only);
        Run("undock", TestUndock, only);
        Run("craft_fly", TestCraftFly, only);
        Run("karman", TestKarman, only);
        Run("chutealt", TestChuteAltitude, only);
        Run("sputnik", TestSputnik, only);
        Run("mechta", () => TestLunar("mechta", 30e6), only);
        Run("vympel", () => TestLunar("vympel", 1000e3), only);
        Run("farside", () => TestLunar("farside", 8000e3, 2.6 * 86400), only);
        Run("vostok", TestVostok, only);
        Run("luna9", () => TestLunar("luna9", 1000e3, land: true), only);
        Run("moondrop", TestMoonDrop, only);
        Run("juno1", () => TestOrbitReturn("juno1", JunoTarget), only);
        Run("freedom7", TestFreedom7, only);
        Run("friendship7", () => TestOrbitReturn("friendship7", 200000), only);
        Run("gemini3", () => TestOrbitReturn("gemini3", 200000), only);
        Run("ranger7", () => TestLunar("ranger7", 1000e3), only);
        Run("surveyor1", () => TestLunar("surveyor1", 1000e3, land: true), only);
        Run("luna17", () => TestLunar("luna17", 1000e3, land: true, afterLanding: DriveLunokhod), only);
        Run("apollo8", () => TestApollo("apollo8"), only);
        Run("apollo11", () => TestApollo("apollo11"), only);
        Run("planets", TestPlanets, only);
        Run("autoplan", TestAutoPlan, only);
        Run("patches", TestPatches, only);
        Run("booster", TestBooster, only);
        Run("booster_defer", TestBoosterDefer, only);
        Run("starship", TestStarship, only);
        Run("starship_catch", TestStarshipCatch, only);
        // Миссии целиком автопилотом Y без рук и с автоускорением: «auto» — все, «auto_<id>» — одна.
        foreach (var id in AutoMissions)
            Run("auto_" + id, () => TestAuto(id), only == "auto" ? "auto_" + id : only);
        Console.WriteLine($"\nИтого: {passed} ok, {failed} fail");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test, string only)
    {
        if (only != null && only != name) return;
        Console.WriteLine($"\n== {name}");
        var sw = Stopwatch.StartNew();
        try { test(); }
        catch (Exception e) { Check($"{name}: исключение", false, e.ToString()); }
        Console.WriteLine($"   ({sw.Elapsed.TotalSeconds:F1} с)");
    }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passed++;
        else failed++;
        Console.WriteLine($"{(ok ? "[OK]  " : "[FAIL]")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    // ------------------------------------------------------------------ базовое

    static Vector3d RandomVector(Random rnd) => new Vector3d(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5);

    static void TestMath()
    {
        var rnd = new Random(1);
        double err = 0, errRot = 0;
        for (int i = 0; i < 1000; i++)
        {
            var q = QuaternionD.AngleAxis(rnd.NextDouble() * 2 * Math.PI, RandomVector(rnd).normalized);
            var v = RandomVector(rnd) * 10;
            err = Math.Max(err, ((q * v).SwapYZ - q.SwapYZ * v.SwapYZ).magnitude);
            var w = RandomVector(rnd);
            errRot = Math.Max(errRot, (QuaternionD.FromToRotation(v.normalized, w.normalized) * v.normalized - w.normalized).magnitude);
        }
        Check("SwapYZ кватерниона согласован с векторами", err < 1e-12, $"{err:E1}");
        Check("FromToRotation", errRot < 1e-9, $"{errRot:E1}");
    }

    static void TestOrbit()
    {
        const double mu = 3.986004418e14;
        foreach (var (name, r0, v0) in new[]
                 {
                     ("эллипс", new Vector3d(6.9e6, 1e5, 2e5), new Vector3d(100, 7600, 1200)),
                     ("гипербола", new Vector3d(7e6, 0, 0), new Vector3d(0, 11500, 3000)),
                 })
        {
            var o = KeplerOrbit.FromState(r0, v0, mu, 100);
            o.GetState(100, out var r1, out var v1);
            // Сверка с численным RK4 за 3000 с.
            Vector3d r = r0, v = v0;
            const double h = 0.5;
            Func<Vector3d, Vector3d> acc = p => p * (-mu / Math.Pow(p.magnitude, 3));
            for (int i = 0; i < 6000; i++)
            {
                var k1v = acc(r); var k1r = v;
                var k2v = acc(r + k1r * (h / 2)); var k2r = v + k1v * (h / 2);
                var k3v = acc(r + k2r * (h / 2)); var k3r = v + k2v * (h / 2);
                var k4v = acc(r + k3r * h); var k4r = v + k3v * h;
                r += (k1r + k2r * 2 + k3r * 2 + k4r) * (h / 6);
                v += (k1v + k2v * 2 + k3v * 2 + k4v) * (h / 6);
            }
            o.GetState(3100, out var r2, out _);
            Check($"Кеплер {name}: туда-обратно", (r1 - r0).magnitude < 1e-3 && (v1 - v0).magnitude < 1e-6,
                $"{(r1 - r0).magnitude:E1} м");
            Check($"Кеплер {name}: против RK4 за 3000 с", (r2 - r).magnitude < 1, $"{(r2 - r).magnitude:F3} м");
        }
    }

    static void TestEphemeris()
    {
        var sys = SolarSystem.CreateReal();
        var moon = sys.Get("moon");
        double min = double.MaxValue, max = 0;
        double t0 = GameCalendar.ToGameTime(1959, 1, 1);
        for (double t = t0; t < t0 + 30 * 86400; t += 3600)
        {
            moon.LocalStateAt(t, out var r, out _);
            min = Math.Min(min, r.magnitude);
            max = Math.Max(max, r.magnitude);
        }
        Check("Луна: перигей января 1959", min > 355e6 && min < 372e6, $"{min / 1e3:F0} км");
        Check("Луна: апогей января 1959", max > 400e6 && max < 407e6, $"{max / 1e3:F0} км");
        sys.Update(GameCalendar.ToGameTime(2000, 1, 1, 12));
        var earth = sys.Get("earth");
        double au = earth.Position.magnitude / 1.495978707e11;
        Check("Земля: ~1 а.е. от Солнца на J2000", au > 0.98 && au < 0.99, $"{au:F4} а.е.");
    }

    static void TestAtmosphere()
    {
        var atm = SolarSystem.CreateReal().Get("earth").Atmosphere;
        atm.Sample(0, out var p0, out var rho0, out var t0);
        atm.Sample(11019.1, out var p11, out _, out var t11);
        atm.Sample(50000, out var p50, out _, out _);
        Check("СА-1976 на уровне моря", Math.Abs(p0 - 101325) < 1 && Math.Abs(rho0 - 1.225) < 0.001, $"{p0:F0} Па, {rho0:F3} кг/м³");
        Check("СА-1976 на 11 км геопотенциальных", Math.Abs(p11 - 22632) < 20 && Math.Abs(t11 - 216.65) < 0.5, $"{p11:F0} Па, {t11:F1} К");
        Check("СА-1976 на 50 км", Math.Abs(p50 - 79.8) < 2, $"{p50:F1} Па");
    }

    static void TestStats()
    {
        foreach (var id in new[] { "heavy", "sputnik", "vostok", "luna", "sounding", "luna17", "juno1", "mercury_redstone", "mercury_atlas",
                                    "gemini_titan", "ranger", "surveyor", "apollo8", "apollo11" })
        {
            var d = VesselPresets.ById(id);
            Console.WriteLine($"   {d.Name}: {d.TotalMass / 1000:F2} т, Δv {d.TotalDeltaVVac:F0} м/с");
            foreach (var s in d.ComputeStats())
                Console.WriteLine($"      {s.Name,-28} Δv {s.DeltaVVac,6:F0} (у Земли {s.DeltaVSL,6:F0}) TWR {s.TwrSL:F2}/{s.TwrVac:F2} {s.BurnTime,5:F0} с");
        }
        var heavy = VesselPresets.Kara1Heavy();
        var st = heavy.ComputeStats();
        Check("Кара-1: стартовая масса", Math.Abs(heavy.TotalMass - 529770) < 100, $"{heavy.TotalMass / 1000:F2} т");
        Check("Кара-1: Δv хватает на НОО с запасом", heavy.TotalDeltaVVac > 9500, $"{heavy.TotalDeltaVVac:F0} м/с");
        Check("Кара-1: TWR на старте 1.3–1.6", st[0].TwrSL > 1.3 && st[0].TwrSL < 1.6, $"{st[0].TwrSL:F2}");
    }

    // ------------------------------------------------------------------ полёты

    static (Universe u, MissionTracker tr) StartMission(string id)
    {
        var def = MissionCatalog.Get(id);
        var u = MissionTracker.CreateUniverse(def, SolarSystem.CreateReal());
        var tr = new MissionTracker(def);
        double t0 = u.Time;
        u.Message += m => Console.WriteLine($"   [{Clock(u.Time - t0)}] {m}");
        tr.Changed += m => Console.WriteLine($"   [{Clock(u.Time - t0)}] ★ {m}");
        Console.WriteLine($"   {def.Title}: {GameCalendar.Format(def.StartTime)}, {u.Active.Design.Name}, {u.Active.Mass / 1000:F1} т");
        return (u, tr);
    }

    static readonly string[] AutoMissions =
    {
        "karman", "sputnik", "vostok", "freedom7", "juno1", "friendship7", "gemini3", "mechta", "vympel", "farside",
        "ranger7", "luna9", "surveyor1", "luna17", "apollo8", "apollo11",
        "voskhod2", "soyuz_tm31", "crew_dragon", "sts1", "buran", "ift5", "starship_catch",
    };

    /// <summary>Миссия от стола до успеха одним автопилотом миссии: ни клавиш, ни ускорения из сценария.</summary>
    static void TestAuto(string id)
    {
        var (u, tr) = StartMission(id);
        u.AutoWarp = true;
        u.Mission = new MissionAutopilot(u, tr);
        Console.WriteLine($"   профиль: {u.Mission.Profile}");
        // Кадры по 0,1 с реального времени; предел — 30 суток полёта или 200 тыс. кадров (≈ 5,5 ч в игре).
        const int MaxFrames = 200000;
        double end = u.Time + 30 * 86400, maxWarp = 1;
        int frames = 0;
        string phase = null;
        bool inAir = false, wasHigh = false;
        double maxG = 0, maxGTimer = 0;
        while (tr.Status == MissionStatus.Active && u.Mission != null && u.Time < end && frames < MaxFrames)
        {
            u.Advance(0.1);
            tr.Update(u);
            frames++;
            maxWarp = Math.Max(maxWarp, u.EffectiveWarp);
            var av = u.Active;
            // Замер входа — только при возврате с высокой орбиты (выведение тоже идёт сквозь атмосферу).
            if (av != null && av.Alive && av.Body.HasAtmosphere && av.Altitude > 1e6) wasHigh = true;
            // С низкой орбиты — после тормозного импульса (сценарий «Сход с орбиты»).
            if (u.Mission != null && u.Mission.Phase == "Сход с орбиты") wasHigh = true;
            if (wasHigh && av.Alive && av.Body.HasAtmosphere && !av.IsLanded && av.Altitude < av.Body.AtmosphereTop)
            {
                if (!inAir)
                {
                    var o = KeplerOrbit.FromState(av.Position, av.Velocity, av.Body.Mu, u.Time);
                    double gamma = Math.Asin(Vector3d.Dot(av.Position.normalized, av.Velocity.normalized)) * 180 / Math.PI;
                    Console.WriteLine($"   вход: {av.Altitude / 1000:F0} км, v {av.Velocity.magnitude:F0} м/с (пов. {av.SurfaceSpeed:F0}), " +
                                      $"угол {gamma:F2}°, перигей {(o.PeriapsisRadius - av.Body.Radius) / 1000:F1} км, {OrbitText(av, u.Time)}");
                }
                inAir = true;
                maxG = Math.Max(maxG, av.GForce);
                maxGTimer = Math.Max(maxGTimer, av.HighGTimer);
            }
            if (u.Mission != null && u.Mission.Phase != phase)
            {
                phase = u.Mission.Phase;
                // Тутор ручного полёта на том же шаге: что он подсказал бы игроку без автопилота.
                string tip = MissionGuide.Next(u, tr, out var step, out var hint) ? $"   · тутор: {step} — {hint}" : "";
                Console.WriteLine($"   ▶ {phase}{tip}");
            }
        }
        // Успех засчитывается раньше, чем «Аполлон-11» допристыкуется; дождаться конца сценария.
        while (tr.Status == MissionStatus.Success && u.Mission != null && frames < MaxFrames)
        {
            u.Advance(0.1);
            tr.Update(u);
            frames++;
        }
        var v = u.Active;
        if (inAir) Console.WriteLine($"   спуск: пик {maxG:F1} g, выше {FlightPhysics.CrewGLimit:F0} g — {maxGTimer:F1} с");
        // Крылатые: касание полосы (предел снижения — FlightPhysics.GearMaxSink).
        if (v != null && !double.IsNaN(v.TouchdownSink))
            Console.WriteLine($"   касание: {v.TouchdownSpeed:F0} м/с, снижение {v.TouchdownSink:F1} м/с");
        Console.WriteLine($"   итог: {(v != null ? OrbitText(v, u.Time) : "нет борта")}; статус миссии: {u.Mission?.Status}");
        Check($"auto_{id}: миссия выполнена без рук", tr.Status == MissionStatus.Success,
            $"{tr.Status} {tr.FailReason}; кадров {frames} ({frames * 0.1 / 60:F1} мин реального времени), макс. ×{maxWarp:G}");
    }

    /// <summary>
    /// Возврат I ступени Falcon 9 (Demo-2, §6.9): миссия crew_dragon автопилотом Y, ступень после отделения ведёт фоновый
    /// BoosterLandingAutopilot на баржу OCISLY. Критерий: касание медленнее 6 м/с, не дальше 50 м от центра палубы.
    /// </summary>
    static void TestBooster()
    {
        var (u, tr) = StartMission("crew_dragon");
        u.AutoWarp = true;
        u.Mission = new MissionAutopilot(u, tr);
        BoosterLandingAutopilot p = null;
        string phase = null;
        double tSep = 0;
        for (int f = 0; f < 60000; f++)
        {
            u.Advance(0.1);
            tr.Update(u);
            if (p == null && u.Recoveries.Count > 0)
            {
                p = u.Recoveries[0];
                var b = p.Vessel;
                tSep = u.Time;
                b.MassProperties(out double m, out _, out _, out _);
                var miss = p.Predict(u.Time, b.Position, b.Velocity, m, true, out double ti);
                var bf = b.Body.OrientationAt(u.Time).Inverse * (b.Body.OrientationAt(u.Time) * p.TargetBodyFixed() + miss);
                CelestialBody.BodyFixedToLatLon(bf.normalized, out double lat, out double lon);
                var pad = CelestialBody.LatLonToBodyFixed(28.6082, -80.6041);
                double down = Math.Acos(Vector3d.Dot(bf.normalized, pad)) * b.Body.Radius / 1000;
                Console.WriteLine($"   отделение T+{u.Time - b.LaunchTime:F0} с: {b.Altitude / 1000:F1} км, v пов. {b.SurfaceSpeed:F0} м/с, " +
                                  $"топливо {b.Propellant[0] / 1000:F1} т; прогноз с входным: {lat:F3}°, {lon:F3}° ({down:F0} км от LC-39A), " +
                                  $"промах до баржи {miss.magnitude:F0} м, падение через {ti - u.Time:F0} с");
            }
            if (p != null && p.Phase.ToString() != phase)
            {
                phase = p.Phase.ToString();
                Console.WriteLine($"   ▶ ступень: {phase} T+{u.Time - tSep:F0} с после отделения, h {p.Vessel.Altitude / 1000:F1} км, " +
                                  $"v {p.Vessel.SurfaceSpeed:F0} м/с, топливо {p.Vessel.Propellant[0] / 1000:F1} т — {p.Status}");
            }
            if (p != null && p.Phase == BoosterLandingAutopilot.PhaseType.Landing && f % 20 == 0)
                Console.WriteLine($"     · h {p.Vessel.Altitude:F0} м, v {p.Vessel.SurfaceSpeed:F0} м/с, газ {p.Vessel.Throttle:F2} × {p.Vessel.EngineLimit} дв., " +
                                  $"α {p.Vessel.AngleOfAttack:F0}°, q {p.Vessel.DynamicPressure / 1000:F0} кПа — {p.Status}");
            if (p != null && !p.Running) break;
        }
        Check("booster: ступень отделилась с автопилотом возврата", p != null, "нет пилота");
        if (p == null) return;
        Console.WriteLine($"   итог: {p.Phase} — {p.Status}");
        Check("booster: касание медленнее 6 м/с", p.TouchdownSpeed < 6, $"{p.TouchdownSpeed:F1} м/с");
        Check("booster: промах не больше 50 м", p.Miss <= 50, $"{p.Miss:F0} м");
    }

    /// <summary>
    /// «Сначала корабль, потом посадка» (§6.9): ступень Falcon 9 откладывается сразу после отделения, корабль летит
    /// дальше (рельсы открыты), через 40 мин ступень возвращается со сдвигом времени и садится на баржу так же, как
    /// без паузы: Земля за паузу повернулась на 10°, и без поворота состояния ступень промахнулась бы на сотни км.
    /// </summary>
    static void TestBoosterDefer()
    {
        var (u, tr) = StartMission("crew_dragon");
        u.AutoWarp = true;
        u.Mission = new MissionAutopilot(u, tr);
        BoosterLandingAutopilot p = null;
        double tSep = double.NaN, maxWarp = 0;
        const double pause = 2400;
        for (int f = 0; f < 200000; f++)
        {
            u.Advance(0.1);
            tr.Update(u);
            if (double.IsNaN(tSep) && u.RunningRecovery() is BoosterLandingAutopilot r && u.Time - r.Vessel.LaunchTime > 0)
            {
                tSep = u.Time;
                p = r;
            }
            if (p != null && u.Deferred.Count == 0 && u.Recoveries.Contains(p) && u.Time - tSep > 3 && u.Time - tSep < pause)
            {
                Check("booster_defer: посадка откладывается", u.DeferRecovery(p), "отказ");
                Check("booster_defer: ступень ушла из мира", !u.Vessels.Contains(p.Vessel) && u.RunningRecovery() == null, "осталась");
            }
            if (u.Deferred.Count > 0)
            {
                maxWarp = Math.Max(maxWarp, u.EffectiveWarp);
                if (u.Time - u.Deferred[0].FrozenAt >= pause)
                {
                    var b = p.Vessel;
                    double h0 = b.Altitude;
                    u.ResumeRecovery(u.Deferred[0]);
                    Console.WriteLine($"   пауза {pause:F0} с, корабль: h {u.Active.Altitude / 1000:F0} км; ступень вернулась: h {h0 / 1000:F1} → {b.Altitude / 1000:F1} км, " +
                                      $"v пов. {b.SurfaceSpeed:F0} м/с, наибольшее ускорение в паузе ×{maxWarp:F0}");
                    Check("booster_defer: высота после возврата та же", Math.Abs(b.Altitude - h0) < 1, $"{b.Altitude - h0:F1} м");
                }
            }
            if (p != null && u.Deferred.Count == 0 && !p.Running) break;
        }
        Check("booster_defer: ступень отделилась", p != null, "нет пилота");
        if (p == null) return;
        Console.WriteLine($"   итог: {p.Phase} — {p.Status}; миссия: {tr.Status}");
        Check("booster_defer: рельсы открыты, пока посадка отложена", maxWarp > Universe.MaxPhysicsWarp, $"×{maxWarp:F0}");
        Check("booster_defer: касание медленнее 6 м/с", p.TouchdownSpeed < 6, $"{p.TouchdownSpeed:F1} м/с");
        Check("booster_defer: промах не больше 50 м", p.Miss <= 50, $"{p.Miss:F0} м");
    }

    /// <summary>
    /// Starship IFT-5 (§6.9): автопилот миссии ведёт корабль, фоновый пилот — Super Heavy к башне. Ускоритель: касание
    /// медленнее 6 м/с и висит на палочках (TowerCatch); корабль: вход «брюхом», переворот, приводнение медленнее 6 м/с; миссия выполнена.
    /// </summary>
    static void TestStarship()
    {
        var (u, tr) = StartMission("ift5");
        u.AutoWarp = true;
        u.Mission = new MissionAutopilot(u, tr);
        Check("starship: профиль Starship", u.Mission.Profile == MissionAutopilot.ProfileType.Starship, u.Mission.Profile.ToString());
        BoosterLandingAutopilot booster = null, ship = null;
        string bPhase = null, sPhase = null, mPhase = null;
        double maxHeat = 0, maxQ = 0;
        for (int f = 0; f < 200000 && tr.Status == MissionStatus.Active; f++)
        {
            u.Advance(0.1);
            tr.Update(u);
            foreach (var p in u.Recoveries)
            {
                if (p.Vessel == u.Active) ship = p;
                else booster = p;
            }
            if (u.Mission != null && u.Mission.Phase != mPhase)
            {
                mPhase = u.Mission.Phase;
                var a = u.Active;
                Console.WriteLine($"   ◆ миссия: {mPhase}: h {a.Altitude / 1000:F1} км, v {a.Velocity.magnitude:F0} м/с, {OrbitText(a, u.Time)}");
            }
            if (booster != null && booster.Phase.ToString() != bPhase)
            {
                bPhase = booster.Phase.ToString();
                var b = booster.Vessel;
                CelestialBody.BodyFixedToLatLon((b.Body.OrientationAt(u.Time).Inverse * b.Position).normalized, out double la, out double lo);
                Console.WriteLine($"   ▶ Super Heavy: {bPhase}: h {b.Altitude / 1000:F1} км, v {b.SurfaceSpeed:F0} м/с, {la:F3}°, {lo:F3}°, " +
                                  $"топливо {b.Propellant[0] / 1000:F0} т — {booster.Status}");
            }
            if (ship != null && ship.Phase.ToString() != sPhase)
            {
                sPhase = ship.Phase.ToString();
                var s = ship.Vessel;
                CelestialBody.BodyFixedToLatLon((s.Body.OrientationAt(u.Time).Inverse * s.Position).normalized, out double la, out double lo);
                Console.WriteLine($"   ▶ корабль: {sPhase}: h {s.Altitude / 1000:F1} км, v {s.SurfaceSpeed:F0} м/с, {la:F2}°, {lo:F2}°, " +
                                  $"топливо {s.Propellant[1] / 1000:F1} т — {ship.Status}");
            }
            if (ship != null && ship.Running)
            {
                maxHeat = Math.Max(maxHeat, ship.Vessel.HeatFlux);
                maxQ = Math.Max(maxQ, ship.Vessel.DynamicPressure);
            }
            if (ship != null && ship.Phase == BoosterLandingAutopilot.PhaseType.Landing && f % 20 == 0)
                Console.WriteLine($"     · h {ship.Vessel.Altitude:F0} м, v {ship.Vessel.SurfaceSpeed:F0} м/с, газ {ship.Vessel.Throttle:F2} × {ship.Vessel.EngineLimit}, α {ship.Vessel.AngleOfAttack:F0}°");
        }
        Console.WriteLine($"   корабль: тепловой поток до {maxHeat / 1e6:F2} МВт/м², напор до {maxQ / 1000:F1} кПа");
        Check("starship: Super Heavy вернулся с пилотом", booster != null, "нет пилота");
        if (booster != null)
        {
            Console.WriteLine($"   Super Heavy: {booster.Phase} — {booster.Status}");
            Check("starship: Super Heavy пойман медленнее 6 м/с", booster.TouchdownSpeed < 6, $"{booster.TouchdownSpeed:F1} м/с");
            Check("starship: Super Heavy висит на палочках", booster.Vessel.TowerCaught, $"{booster.Vessel.Situation}, {booster.Miss:F0} м от оси");
        }
        Check("starship: корабль передан пилоту посадки", ship != null, "нет пилота");
        if (ship != null)
        {
            var s = ship.Vessel;
            CelestialBody.BodyFixedToLatLon((s.Body.OrientationAt(u.Time).Inverse * s.Position).normalized, out double la, out double lo);
            Console.WriteLine($"   корабль: {ship.Phase} — {ship.Status}; точка {la:F2}°, {lo:F2}°, {s.Situation}");
            Check("starship: корабль приводнился медленнее 6 м/с", ship.TouchdownSpeed < 6 && s.Situation == Situation.Splashed,
                  $"{ship.TouchdownSpeed:F1} м/с, {s.Situation}");
        }
        Check("starship: миссия выполнена", tr.Status == MissionStatus.Success, tr.Status.ToString());
    }

    /// <summary>
    /// starship_catch (§6.9): обе ступени ловят башни Starbase. Super Heavy — к башне A, как в IFT-5; корабль — орбита
    /// повторяющейся трассы, сход через сутки, доправка в вакууме, вход «брюхом» и ловля башней B. Обе: касание медленнее
    /// 6 м/с в круге 50 м своей башни; миссия выполнена.
    /// </summary>
    static void TestStarshipCatch()
    {
        var (u, tr) = StartMission("starship_catch");
        u.AutoWarp = true;
        u.Mission = new MissionAutopilot(u, tr);
        Check("starship_catch: профиль Starship", u.Mission.Profile == MissionAutopilot.ProfileType.Starship, u.Mission.Profile.ToString());
        BoosterLandingAutopilot booster = null, ship = null;
        string bPhase = null, sPhase = null, mPhase = null;
        double maxHeat = 0, maxQ = 0, maxG = 0, end = u.Time + 3 * 86400;
        for (int f = 0; f < 400000 && tr.Status == MissionStatus.Active && u.Time < end; f++)
        {
            u.Advance(0.1);
            tr.Update(u);
            foreach (var p in u.Recoveries)
            {
                if (p.Vessel == u.Active) ship = p;
                else booster = p;
            }
            if (u.Mission != null && u.Mission.Phase != mPhase)
            {
                mPhase = u.Mission.Phase;
                var a = u.Active;
                Console.WriteLine($"   ◆ миссия: {mPhase}: h {a.Altitude / 1000:F1} км, v {a.Velocity.magnitude:F0} м/с, {OrbitText(a, u.Time)}, " +
                                  $"топливо {a.Propellant[1] / 1000:F0} т");
            }
            if (booster != null && booster.Phase.ToString() != bPhase)
            {
                bPhase = booster.Phase.ToString();
                var b = booster.Vessel;
                Console.WriteLine($"   ▶ Super Heavy: {bPhase}: h {b.Altitude / 1000:F1} км, v {b.SurfaceSpeed:F0} м/с, " +
                                  $"топливо {b.Propellant[0] / 1000:F0} т — {booster.Status}");
            }
            if (ship != null && ship.Phase.ToString() != sPhase)
            {
                sPhase = ship.Phase.ToString();
                var s = ship.Vessel;
                var miss = ship.Predict(u.Time, s.Position, s.Velocity, s.Mass, false, out double tI);
                Console.WriteLine($"   ▶ корабль: {sPhase}: h {s.Altitude / 1000:F1} км, v {s.SurfaceSpeed:F0} м/с, топливо {s.Propellant[1] / 1000:F1} т, " +
                                  $"прогноз промаха {miss.magnitude / 1000:F2} км через {tI - u.Time:F0} с — {ship.Status}");
            }
            if (ship != null && ship.Running)
            {
                maxHeat = Math.Max(maxHeat, ship.Vessel.HeatFlux);
                maxQ = Math.Max(maxQ, ship.Vessel.DynamicPressure);
                maxG = Math.Max(maxG, ship.Vessel.GForce);
                if (ship.Phase == BoosterLandingAutopilot.PhaseType.Aero && f % 100 == 0)
                {
                    var s = ship.Vessel;
                    var miss = ship.Predict(u.Time, s.Position, s.Velocity, s.Mass, false, out _);
                    Console.WriteLine($"     · h {s.Altitude / 1000:F1} км, v {s.SurfaceSpeed:F0} м/с, {s.GForce:F1} g, AoA {s.AngleOfAttack:F0}°, q {s.DynamicPressure:F0}, прогноз промаха {miss.magnitude:F0} м — {ship.Status}");
                }
                if (ship.Phase == BoosterLandingAutopilot.PhaseType.Landing && f % 20 == 0)
                    Console.WriteLine($"     · h {ship.Vessel.Altitude:F0} м, v {ship.Vessel.SurfaceSpeed:F0} м/с, газ {ship.Vessel.Throttle:F2} × {ship.Vessel.EngineLimit} — {ship.Status}");
            }
        }
        Console.WriteLine($"   корабль: тепловой поток до {maxHeat / 1e6:F2} МВт/м², напор до {maxQ / 1000:F1} кПа, до {maxG:F1} g");
        Check("starship_catch: Super Heavy вернулся с пилотом", booster != null, "нет пилота");
        if (booster != null)
        {
            Console.WriteLine($"   Super Heavy: {booster.Phase} — {booster.Status}");
            Check("starship_catch: Super Heavy пойман медленнее 6 м/с", booster.TouchdownSpeed < 6, $"{booster.TouchdownSpeed:F1} м/с");
            Check("starship_catch: Super Heavy висит на палочках", booster.Vessel.TowerCaught, $"{booster.Vessel.Situation}, {booster.Miss:F0} м от оси");
        }
        Check("starship_catch: корабль передан пилоту возврата", ship != null, "нет пилота");
        if (ship != null)
        {
            Console.WriteLine($"   корабль: {ship.Phase} — {ship.Status}; {ship.Vessel.Situation}");
            Check("starship_catch: корабль пойман медленнее 6 м/с", ship.TouchdownSpeed < 6, $"{ship.TouchdownSpeed:F1} м/с");
            Check("starship_catch: корабль висит на палочках башни B", ship.Vessel.TowerCaught, $"{ship.Vessel.Situation}, {ship.Miss:F0} м от оси");
        }
        Check("starship_catch: миссия выполнена", tr.Status == MissionStatus.Success, tr.Status.ToString());
    }

    /// <summary>Вставки DEM 15″ у площадок (bake-dem.py patches): мыс Канаверал — суша, океан к востоку — вода. В 0,1° LC-39A
    /// билинейно −4 м, и площадка торчала островом в море (05.10.2026). Нет файла — пропуск.</summary>
    static void TestPatches()
    {
        var earth = SolarSystem.CreateReal().Get("earth");
        var map = earth.Terrain.Map;
        if (map == null || map.Patches.Count == 0) { Console.WriteLine("   EarthPatches.bytes нет — пропуск"); return; }
        double kmDeg = Math.PI * earth.Radius / 180 / 1000;
        double At(double lat, double lon, double north, double east, bool raw = false)
        {
            var d = CelestialBody.LatLonToBodyFixed(lat + north / kmDeg, lon + east / (kmDeg * Math.Cos(lat * Constants.Deg2Rad)));
            return raw ? Terrain.RawHeight(earth.Terrain, d) : Terrain.Height(earth, d);
        }
        var c = SolarSystem.GetSite("canaveral");
        var slf = SolarSystem.GetSite("slf");
        // Таблица сырого рельефа (без выравнивания площадок) вокруг LC-39A: строки — север, км; столбцы — восток, км.
        var xs = new[] { -10.0, -8, -6, -4, -2, -1, 0, 1, 2, 3, 4, 5 };
        Console.WriteLine("   сырой рельеф у LC-39A, м (строки — к северу, км; столбцы — к востоку, км):");
        Console.WriteLine("        " + string.Concat(Array.ConvertAll(xs, x => $"{x,6:F0}")));
        foreach (var y in new[] { 4.0, 2, 1, 0, -1, -2, -4 })
            Console.WriteLine($"   {y,4:F0} " + string.Concat(Array.ConvertAll(xs, x => $"{At(c.Latitude, c.Longitude, y, x, true),6:F1}")));
        Console.WriteLine("   итоговый рельеф (с выравниванием площадки, < 0 — вода):");
        foreach (var y in new[] { 4.0, 2, 1, 0, -1, -2, -4 })
            Console.WriteLine($"   {y,4:F0} " + string.Concat(Array.ConvertAll(xs, x => $"{At(c.Latitude, c.Longitude, y, x),6:F1}")));
        double pad = At(c.Latitude, c.Longitude, 0, 0, true), padSlf = At(slf.Latitude, slf.Longitude, 0, 0, true);
        Check("patches: LC-39A на суше (сырой рельеф > 0)", pad > 0, $"{pad:F1} м");
        Check("patches: SLF на суше (сырой рельеф > 0)", padSlf > 0, $"{padSlf:F1} м");
        // Суша вокруг — по итоговому рельефу, который видит игрок: к западу, юго-западу, югу от 39A (океан — к востоку
        // и северо-востоку в 0,5–1 км, как в жизни); по оси полосы SLF (курс 150°) ±2 км. Сырой ETOPO 15″ местами даёт
        // −1 м у берега (лагуны, артефакт сшивки суша/батиметрия) — их держит выравнивание площадки (PatchSeaCut = −2 м).
        double minLand = double.MaxValue;
        foreach (var (n, e) in new[] { (0.0, -1.0), (0, -2), (-1, -1), (-1.4, -1.4), (-1, 0), (-2, 0), (1, -2), (2, -2) })
            minLand = Math.Min(minLand, At(c.Latitude, c.Longitude, n, e));
        for (double a = -2; a <= 2; a += 0.5)
            minLand = Math.Min(minLand, At(slf.Latitude, slf.Longitude, a * Math.Cos(150 * Constants.Deg2Rad), a * Math.Sin(150 * Constants.Deg2Rad)));
        Check("patches: на 1–2 км к З/Ю от 39A и вдоль SLF — суша", minLand > 0, $"мин {minLand:F1} м");
        double sea = Math.Max(At(c.Latitude, c.Longitude, 0, 4), At(c.Latitude, c.Longitude, 0, 5));
        double seaRaw = Math.Max(At(c.Latitude, c.Longitude, 0, 4, true), At(c.Latitude, c.Longitude, 0, 5, true));
        Check("patches: океан в 4–5 км к востоку от 39A — вода (выравнивание площадки его не поднимает)", seaRaw < 0 && sea <= 0,
            $"сырой {seaRaw:F1} м, с площадкой {sea:F1} м");
        double padH = At(c.Latitude, c.Longitude, 0, 0);
        Check("patches: стол 39A на отметке", Math.Abs(padH - c.Elevation) < 0.01, $"{padH:F2} м");
        // Край вставки: шаг 200 м поперёк полосы смешивания — без ступеньки (перепад порядка соседних точек вне вставки).
        var pt = map.Patches.Find(p => p.LatS < c.Latitude && c.Latitude < p.LatN && p.LonW < c.Longitude && c.Longitude < p.LonE);
        double maxStep = 0, prev = double.NaN;
        for (double lat = pt.LatN - 2 * DemPatch.FadeDeg; lat < pt.LatN + DemPatch.FadeDeg; lat += 0.2 / kmDeg)
        {
            double hh = Terrain.RawHeight(earth.Terrain, CelestialBody.LatLonToBodyFixed(lat, -81.2));
            if (!double.IsNaN(prev)) maxStep = Math.Max(maxStep, Math.Abs(hh - prev));
            prev = hh;
        }
        Check("patches: край вставки без ступеньки (шаг 200 м)", maxStep < 5, $"макс. перепад {maxStep:F2} м");
        foreach (var id in new[] { "baikonur", "kourou", "plesetsk", "vostochny", "edwards", "yubileyny" })
        {
            var s = SolarSystem.GetSite(id);
            double r = At(s.Latitude, s.Longitude, 0, 0, true);
            bool inside = map.PatchAt(s.Latitude, s.Longitude, out double cw) != null && cw >= 1;
            Check($"patches: {s.Name} — во вставке, сырой рельеф у отметки", inside && Math.Abs(r - s.Elevation) < 60,
                $"{r:F0} м при отметке {s.Elevation:F0}");
        }
    }

    /// <summary>План перелёта к планетам с опорной орбиты Земли: прогноз склейки коник доходит до цели.</summary>
    static void TestPlanets()
    {
        foreach (var target in new[] { "mars", "venus" })
        {
            var (u, tr) = StartMission("sputnik");
            var earth = u.System.Get("earth");
            var planet = u.System.Get(target);
            u.Teleport(earth, 200e3, true);
            var v = u.Active;
            var sw = Stopwatch.StartNew();
            double miss = Math.Max(0.1 * planet.SoiRadius, 3 * planet.Radius);
            var plan = TransferPlanner.PlanInterplanetary(earth, v.Position, v.Velocity, u.Time, planet, miss);
            long ms = sw.ElapsedMilliseconds;
            Check($"planets: план к {planet.Name} найден", plan != null, $"{ms} мс");
            if (plan == null) continue;
            u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
            var ps = PatchedConics.Predict(v.Body, v.Position, v.Velocity, u.Time, v.Node, 6);
            var at = ps.Find(p => p.Body == planet);
            string route = string.Join(" → ", ps.ConvertAll(p => p.Body.Name));
            Check($"planets: {planet.Name} — прогноз входит в сферу влияния", at != null,
                $"старт через {(plan.Time - u.Time) / 86400:F0} сут, Δv {plan.DeltaV:F0} м/с (норм. {plan.Normal:F0}), перелёт {plan.TransferTime / 86400:F0} сут, " +
                $"промах без притяжения {plan.Miss / 1000:F0} км (цель {miss / 1000:F0}); {route}; {ms} мс" +
                (at != null ? $"; перицентр {(at.Orbit.PeriapsisRadius - planet.Radius) / 1000:F0} км" : ""));
        }
    }

    /// <summary>Клавиша P: узел к Луне с орбиты Земли, к Земле с орбиты Луны, к Марсу — через ManeuverAutoPlan.</summary>
    static void TestAutoPlan()
    {
        foreach (var (from, to, alt) in new[] { ("earth", "moon", 200e3), ("moon", "earth", 100e3), ("earth", "mars", 200e3) })
        {
            var (u, tr) = StartMission("sputnik");
            var a = u.System.Get(from);
            var b = u.System.Get(to);
            u.Teleport(a, alt, true);
            var v = u.Active;
            var sw = Stopwatch.StartNew();
            string msg = ManeuverAutoPlan.Plan(u, b, out var plan);
            long ms = sw.ElapsedMilliseconds;
            Console.WriteLine($"   {from} → {to}: {msg} ({ms} мс)");
            Check($"autoplan: {from} → {to} — узел поставлен", plan != null && v.Node != null, $"{ms} мс");
            if (plan == null) continue;
            var at = PatchedConics.Predict(v.Body, v.Position, v.Velocity, u.Time, v.Node, 6).Find(p => p.Body == b);
            double pe = at != null ? at.Orbit.PeriapsisRadius - b.Radius : double.NaN;
            double want = to == "moon" ? ManeuverAutoPlan.MoonPeriapsis : to == "earth" ? MissionAutopilot.ReturnPerigee : double.NaN;
            bool ok = at != null && (double.IsNaN(want) || Math.Abs(pe - want) < (to == "moon" ? 200e3 : 30e3));
            Check($"autoplan: {from} → {to} — прогноз у цели", ok, $"перицентр {pe / 1000:F0} км" + (double.IsNaN(want) ? "" : $" (цель {want / 1000:F0})"));
        }
    }

    static string Clock(double s)
    {
        if (s < 3600) return $"{(int)(s / 60):00}:{s % 60:00.0}";
        if (s < 86400) return $"{(int)(s / 3600)}ч{(int)(s % 3600 / 60):00}м";
        return $"{s / 86400:F2} сут";
    }

    /// <summary>Шагать вселенную, пока cond истинно. Ускорение задаётся один раз — дальше его сбрасывает сама вселенная.</summary>
    static void Fly(Universe u, MissionTracker tr, int warp, double maxTime, Func<bool> cond, Action perStep = null)
    {
        u.SetWarp(warp);
        double end = u.Time + maxTime;
        while (u.Time < end && cond())
        {
            u.Advance(0.1);
            tr?.Update(u);
            perStep?.Invoke();
        }
    }

    static string OrbitText(Vessel v, double t)
    {
        if (v.IsLanded) return $"на поверхности ({v.Body.Name})";
        var o = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, t);
        double pe = (o.PeriapsisRadius - v.Body.Radius) / 1000, ap = (o.ApoapsisRadius - v.Body.Radius) / 1000;
        double inc = o.InclinationTo(v.Body.PoleAt(t)) / Constants.Deg2Rad;
        return $"{v.Body.Name}: Pe {pe:F1} км, Ap {(double.IsInfinity(ap) ? "∞" : ap.ToString("F1"))} км, i {inc:F2}°, e {o.E:F4}";
    }

    /// <summary>Вывод на опорную орбиту автопилотом с телеметрией каждые 30 с.</summary>
    static bool Ascend(Universe u, MissionTracker tr, double target, Vessel planeOf = null)
    {
        var v = u.Active;
        u.Ascent = new AscentAutopilot { TargetAltitude = target };
        if (planeOf != null) u.Ascent.AimAtPlane(v, planeOf, u.Time);
        double t0 = u.Time, maxQ = 0, maxG = 0, nextLog = 0;
        var phase = u.Ascent.Phase;
        Fly(u, tr, 2, 3000, () => u.Ascent != null && u.Active.Alive, () =>
        {
            v = u.Active;
            maxQ = Math.Max(maxQ, v.DynamicPressure);
            maxG = Math.Max(maxG, v.GForce);
            double mt = u.Time - t0;
            if (u.Ascent != null && (mt >= nextLog || u.Ascent.Phase != phase))
            {
                nextLog = Math.Floor(mt / 30) * 30 + 30;
                phase = u.Ascent.Phase;
                double pitch = 90 - Vector3d.Angle(v.NoseP, v.Position.normalized) / Constants.Deg2Rad;
                Console.WriteLine($"      T+{mt,5:F0} {phase,-12} h {v.Altitude / 1000,6:F1} км  vs {v.VerticalSpeed,6:F0}  vh {v.HorizontalSpeed,6:F0}  " +
                                  $"q {v.DynamicPressure / 1000,5:F1} кПа  α {v.AngleOfAttack,4:F1}°  тангаж {pitch,5:F1}°  {v.Mass / 1000,6:F1} т  {u.Ascent.Status}");
            }
        });
        v = u.Active;
        Console.WriteLine($"   итог: {OrbitText(v, u.Time)}, масса {v.Mass / 1000:F2} т, max q {maxQ / 1000:F1} кПа, max {maxG:F1} g");
        return v.Alive && u.Ascent == null && !v.IsLanded;
    }

    /// <summary>§4.6: пакет без стабилизаторов в плотных слоях неустойчив — без управления угол атаки растёт.</summary>
    static void TestStability()
    {
        double Run(SasMode sas)
        {
            var (u, tr) = StartMission("vostok");
            u.Ascent = new AscentAutopilot { TargetAltitude = 220000 };
            Fly(u, tr, 2, 200, () => u.Active.Alive && u.Active.Altitude < 9000);
            var v = u.Active;
            u.Ascent = null;
            v.Sas = sas;
            double maxA = 0;
            Fly(u, tr, 2, 20, () => v.Alive, () => maxA = Math.Max(maxA, v.AngleOfAttack));
            if (!v.Alive) maxA = Math.Max(maxA, 90);
            Console.WriteLine($"   SAS {sas}: max α {maxA:F1}° за 20 с, q {v.DynamicPressure / 1000:F1} кПа, {(v.Alive ? "цел" : v.DestroyReason)}");
            return maxA;
        }
        double free = Run(SasMode.Off), held = Run(SasMode.Prograde);
        Check("Без управления пакет кувыркается (§4.6)", free > 10, $"{free:F1}°");
        Check("SAS по вектору скорости держит пакет", held < 5, $"{held:F1}°");
    }

    /// <summary>
    /// §5: после разделения каждая часть стоит там, где была в пакете (Position — ЦМ части), импульс сохранён.
    /// До исправления обломок и борт вставали в старый общий ЦМ — I ступень оказывалась внутри II.
    /// </summary>
    /// <summary>Конструктор (§5.4): JSON туда-обратно, компиляция пресетов, группы ступеней и радиальное отделение.</summary>
    static void TestCraft()
    {
        foreach (var c in CraftPresets.All())
        {
            var json = c.ToJson();
            Check($"Конструктор: JSON туда-обратно «{c.Name}»", Craft.FromJson(json).ToJson() == json);
            var b = CraftCompiler.Compile(c);
            foreach (var e in b.Errors) Console.WriteLine($"   ошибка: {e}");
            foreach (var w in b.Warnings) Console.WriteLine($"   предупреждение: {w}");
            Console.WriteLine($"   {c.Name}: {b.Mass / 1000:F1} т, высота {b.Height:F1} м, ширина {b.Width:F1} м, ЦМ {b.ComHeight:F1} м");
            if (b.Design != null)
            {
                foreach (var sec in b.Design.Sections)
                    Console.WriteLine($"      {sec.Name,-28} {sec.Kind,-8} {(sec.IsRadial ? $"×{sec.RadialCount} на {sec.RadialOffset:F2} м" : "")}");
                foreach (var st in b.Stats)
                    Console.WriteLine($"      {st.Name,-28} Δv {st.DeltaVVac,6:F0} (у Земли {st.DeltaVSL,6:F0}) TWR {st.TwrSL:F2}/{st.TwrVac:F2}");
                int i = 0;
                foreach (var a in b.Design.Sequence)
                    Console.WriteLine($"      {i++,2}. {a.Type} {b.Design.Sections[a.Section].Name}{(a.IgniteNext ? " +запуск" : "")}{(a.WithPrevious ? " (вместе)" : "")}");
            }
            Check($"Конструктор: «{c.Name}» собирается", b.Ok, string.Join("; ", b.Errors));
        }

        var sb = CraftCompiler.Compile(CraftPresets.Semyorka());
        if (!sb.Ok) return;
        var d = sb.Design;
        Check("Семёрка: TWR на старте 1,15–1,6", sb.Stats[0].TwrSL > 1.15 && sb.Stats[0].TwrSL < 1.6, $"{sb.Stats[0].TwrSL:F2}");
        var q = d.Sequence;
        Check("Семёрка: старт — ядро и боковушки одним нажатием",
            q.Count > 1 && q[0].Type == StageActionType.Ignite && q[1].Type == StageActionType.Ignite && q[1].WithPrevious
            && d.Sections[q[1].Section].IsRadial);

        // Отделение боковушек на ходу: 4 обломка вокруг оси, импульс сохраняется, уходят наружу и продолжают жечь.
        var (u, _) = StartMission("sputnik");
        u.Vessels.Remove(u.Active);
        var v = u.Launch(d, MissionCatalog.Get("sputnik").SiteId);
        v.Situation = Situation.Flying;
        v.Velocity += v.NoseP * 500;
        v.AngularVelocity = new Vector3d(0.01, 0, 0.005);
        v.Stage();
        int rj = Array.FindIndex(d.Sections.ToArray(), x => x.IsRadial);
        v.Propellant[rj] *= 0.5;
        v.MassProperties(out double m0, out _, out _, out _);
        var mom0 = v.Velocity * m0;
        int sepIdx = q.FindIndex(a => a.Type == StageActionType.Separate && d.Sections[a.Section].IsRadial);
        while (v.NextApplicable(v.NextStage) < sepIdx) v.NextStage++;
        var list = v.Stage();
        var mom = v.Velocity * v.Mass;
        double minOut = double.MaxValue;
        int burning = 0;
        foreach (var w in list)
        {
            w.MassProperties(out double m, out _, out _, out _);
            mom += w.Velocity * m;
            var radial = w.Position - v.Position;
            radial -= v.NoseP * Vector3d.Dot(radial, v.NoseP);
            minOut = Math.Min(minOut, Vector3d.Dot(w.Velocity - v.Velocity, radial.normalized));
            if (w.Running[0] && w.Throttle > 0) burning++;
        }
        Check("Боковушки: 4 обломка", list.Count == 4, $"{list.Count}");
        Check("Боковушки: импульс сохраняется", (mom - mom0).magnitude / m0 < 1e-6, $"{(mom - mom0).magnitude / m0:E1} м/с");
        Check("Боковушки: уходят наружу", minOut > 1, $"{minOut:F2} м/с");
        Check("Боковушки: двигатели работают дальше", burning == 4, $"{burning}");
    }

    /// <summary>
    /// Параметрические детали (§5.4): пересчёт топлива по размерам, JSON с размерами и без, предупреждение о стыке,
    /// крылья в физике — площадь, размах и угол установки меняют аэродинамическую силу (§4.6).
    /// </summary>
    static void TestCraftParams()
    {
        var baseTank = PartCatalog.Get("tank-3.7-2");
        var longTank = PartCatalog.Resolve(new CraftPart("tank-3.7-2") { Length = baseTank.Length * 2 });
        var wideTank = PartCatalog.Resolve(new CraftPart("tank-3.7-2") { Diameter = 7.4 });
        Check("Параметры: бак вдвое длиннее — топлива вдвое", Math.Abs(longTank.Propellant - 2 * baseTank.Propellant) <= 10,
            $"{baseTank.Propellant} → {longTank.Propellant}");
        Check("Параметры: бак вдвое шире — топлива вчетверо", Math.Abs(wideTank.Propellant - 4 * baseTank.Propellant) <= 20,
            $"{wideTank.Propellant}");
        Check("Параметры: пределы обрезают", PartCatalog.Resolve(new CraftPart("tank-1-1") { Length = 500 }).Length == 60);
        Check("Параметры: двигатель не меняется", PartCatalog.Resolve(new CraftPart("eng-rd107") { Length = 10 }).Length == PartCatalog.Get("eng-rd107").Length);

        // Старое сохранение (без ключей размеров) читается, новое — с ними и туда-обратно.
        var old = Craft.FromJson("{ \"name\": \"Старая\", \"stack\": [ { \"part\": \"tank-2-1\", \"stage\": -1 } ], \"radials\": [] }");
        Check("Параметры: старый JSON без размеров", old.Stack.Count == 1 && !old.Stack[0].HasParams);

        // Высота ввода парашюта (§4.8): необязательный ключ chuteAlt у детали стека и у боковой группы; старый JSON — штатная.
        Check("Парашют: старый JSON без высоты — штатная", double.IsNaN(old.Stack[0].ChuteAltitude));
        var chuted = new Craft { Name = "С парашютами" };
        chuted.Stack.Add(new CraftPart("eng-rd107"));
        chuted.Stack.Add(new CraftPart("tank-2-2"));
        chuted.Stack.Add(new CraftPart("dec-2"));
        chuted.Stack.Add(new CraftPart("cmd-mercury") { ChuteAltitude = 4000 });
        chuted.Radials.Add(new CraftRadial { Parent = 1, Symmetry = 2, Parts = { "srb-1", "chute" }, ChuteAltitude = 3000 });
        var chutedJson = chuted.ToJson();
        var chutedBack = Craft.FromJson(chutedJson);
        Check("Парашют: высота ввода сохраняется в JSON", chutedBack.ToJson() == chutedJson && chutedBack.Stack[3].ChuteAltitude == 4000
            && chutedBack.Radials[0].ChuteAltitude == 3000 && double.IsNaN(chutedBack.Stack[0].ChuteAltitude), chutedJson.Replace('\n', ' '));
        Check("Парашют: незаданная высота в JSON не пишется", new Regex(CraftPart.ChuteKey).Matches(chutedJson).Count == 2);
        var cb = CraftCompiler.Compile(chutedBack);
        Check("Парашют: компилятор переносит высоту в секции", cb.Design != null
            && cb.Design.Sections[cb.PartSection[3]].ChuteAltitude == 4000 && cb.Design.Sections[cb.RadialSection[0]].ChuteAltitude == 3000
            && cb.Design.Sections[cb.PartSection[0]].ChuteAltitude == 0, string.Join("; ", cb.Errors));

        Craft Plane(double span, double incidence)
        {
            var c = new Craft { Name = "Ракетоплан" };
            c.Stack.Add(new CraftPart("eng-rd107"));
            c.Stack.Add(new CraftPart("wing") { Span = span, Chord = 3, Incidence = incidence, Sweep = 30 });
            c.Stack.Add(new CraftPart("wing-fin"));
            c.Stack.Add(new CraftPart("gear"));
            c.Stack.Add(new CraftPart("tank-2-4") { Length = 10 });
            c.Stack.Add(new CraftPart("probe-core") );
            return c;
        }
        var plane = Plane(14, 2);
        var json = plane.ToJson();
        var back = Craft.FromJson(json);
        Check("Параметры: JSON с размерами туда-обратно", back.ToJson() == json && back.Stack[1].Span == 14 && back.Stack[4].Length == 10);
        var pb = CraftCompiler.Compile(plane);
        foreach (var w in pb.Warnings) Console.WriteLine($"   предупреждение: {w}");
        Check("Параметры: ракетоплан собирается", pb.Ok, string.Join("; ", pb.Errors));
        if (!pb.Ok) return;
        var sec = pb.Design.Sections[0];
        Check("Параметры: крыло и киль в секции", sec.Wings != null && sec.Wings.Count == 2, $"{sec.Wings?.Count}");
        Check("Параметры: площадь крыла = размах × хорда", Math.Abs(sec.Wings[0].Area - 42) < 1e-9, $"{sec.Wings[0].Area}");
        Check("Параметры: шасси", sec.Deploy == DeployKind.Gear && sec.GearHeight > 1);
        Check("Параметры: топливо бака по длине", Math.Abs(pb.Design.Sections[0].Propellant - PartCatalog.Resolve(plane.Stack[4]).Propellant) < 1);

        // Физика: нормальная сила при α = 5°, 100 м/с у земли — больше размах, больше сила; угол установки даёт силу на α = 0.
        double Normal(Craft c, double alphaDeg)
        {
            var b = CraftCompiler.Compile(c);
            var v = new Vessel(b.Design, c.Name);
            v.MassProperties(out _, out _, out _, out _); // SectionBottom — после раскладки
            var panels = new List<WingPanel>();
            Aerodynamics.CollectPanels(v, panels);
            double a = alphaDeg * Constants.Deg2Rad;
            var f = Aerodynamics.Force(panels, new Vector3d(100 * Math.Sin(a), 100 * Math.Cos(a), 0), 1.225, 0.3);
            return Math.Abs(f.x);
        }
        double f14 = Normal(Plane(14, 0), 5), f20 = Normal(Plane(20, 0), 5);
        double f0 = Normal(Plane(14, 0), 0), fInc = Normal(Plane(14, 4), 0);
        Console.WriteLine($"   N(α 5°): размах 14 — {f14 / 1000:F1} кН, 20 — {f20 / 1000:F1} кН; α 0: без установки {f0 / 1000:F2}, установка 4° — {fInc / 1000:F1} кН");
        Check("Параметры: размах 20 > 14 по силе", f20 > f14 * 1.2, $"{f14:F0} / {f20:F0}");
        Check("Параметры: угол установки даёт силу на α 0", fInc > 10 * Math.Max(f0, 1), $"{f0:F0} / {fInc:F0}");

        // Стык без переходника — предупреждение.
        var bad = new Craft { Name = "Ступенька" };
        bad.Stack.Add(new CraftPart("eng-rd180"));
        bad.Stack.Add(new CraftPart("tank-3.7-4"));
        bad.Stack.Add(new CraftPart("tank-2-2"));
        bad.Stack.Add(new CraftPart("probe-core"));
        var bb = CraftCompiler.Compile(bad);
        Check("Параметры: стык Ø3,7 → Ø2 — предупреждение", bb.Warnings.Exists(w => w.StartsWith("Стык")), string.Join("; ", bb.Warnings));
        int presetJoints = 0;
        foreach (var c in CraftPresets.All())
            foreach (var w in CraftCompiler.Compile(c).Warnings)
                if (w.StartsWith("Стык")) { presetJoints++; Console.WriteLine($"   {c.Name}: {w}"); }
        Check("Параметры: готовые корабли без ступенек", presetJoints == 0, $"{presetJoints}");
    }

    /// <summary>Столкновения бортов: медленный удар — отскок с сохранением импульса, быстрый — гибель обоих.</summary>
    static void TestCollide()
    {
        foreach (double speed in new[] { 1.5, 20.0 })
        {
            var (u, _) = StartMission("sputnik");
            var a = u.Active;
            a.Situation = Situation.Flying;
            a.Position += a.NoseP * 5000;
            var b = new Vessel(VesselPresets.Kara1Heavy(), "Мишень")
            {
                Body = a.Body,
                Attitude = a.Attitude,
                Situation = Situation.Flying,
            };
            // Бок о бок: оси параллельны, между осями 3 м — корпуса перекрываются, сближение по боку.
            var side = a.LocalToWorld(new Vector3d(1, 0, 0));
            b.Position = a.Position + side * 3;
            b.Velocity = a.Velocity - side * speed;
            u.Add(b);
            a.MassProperties(out double ma, out _, out _, out _);
            b.MassProperties(out double mb, out _, out _, out _);
            var mom0 = a.Velocity * ma + b.Velocity * mb;
            u.Collide(new List<Vessel> { a, b });
            if (speed < 5)
            {
                var mom = a.Velocity * a.Mass + b.Velocity * b.Mass;
                double vn = Vector3d.Dot(b.Velocity - a.Velocity, side);
                Check("Удар 1,5 м/с: оба целы", a.Alive && b.Alive);
                Check("Удар 1,5 м/с: импульс сохраняется", (mom - mom0).magnitude / (ma + mb) < 1e-6, $"{(mom - mom0).magnitude / (ma + mb):E1} м/с");
                Check("Удар 1,5 м/с: расходятся", vn > 0, $"{vn:F2} м/с");
            }
            else
                Check("Удар 20 м/с: оба разрушены", !a.Alive && !b.Alive, $"{a.DestroyReason}");
        }
    }

    /// <summary>
    /// Ручная расстыковка (V, §6.6): отошедший борт не причаливает обратно сам (пара «свежая» до расхождения
    /// на SeparationClear), импульс сохраняется, пружины UndockPush; после расхождения связка снова собирается.
    /// </summary>
    static void TestUndock()
    {
        var (u, _) = StartMission("apollo11");
        var v = u.Active;
        v.Situation = Situation.Flying;
        double R = v.Body.Radius + 300e3;
        var up = v.Position.normalized;
        v.Position = up * R;
        v.Velocity = Vector3d.Cross(new Vector3d(0, 0, 1), up).normalized * Math.Sqrt(v.Body.Mu / R);
        Vessel lm = null;
        for (int k = 0; k < 30 && lm == null && u.Active == v && v.HasNextStage; k++)
        {
            u.Stage();
            FlightControl.Cutoff(v);
            foreach (var x in u.Vessels) if (x != v && Universe.CanDock(v, x)) lm = x;
        }
        if (lm == null) { Check("Расстыковка: ЛМ на переходнике", false); return; }
        u.Vessels.Remove(lm);
        v.Dock(lm);
        Check("Расстыковка: связка собрана", v.IsDocked);
        v.MassProperties(out double m0, out _, out _, out _);
        var mom0 = v.Velocity * m0;
        int n0 = u.Vessels.Count;
        u.Undock();
        var t = v.Target;
        bool ok = t != null && !v.IsDocked && u.Vessels.Count == n0 + 1;
        Check("Расстыковка: отдельный борт, он же цель", ok, $"{u.Vessels.Count - n0} новых");
        if (!ok) return;
        v.MassProperties(out double m1, out _, out _, out _);
        t.MassProperties(out double m2, out _, out _, out _);
        double dm = ((v.Velocity * m1 + t.Velocity * m2) - mom0).magnitude / m0;
        Check("Расстыковка: импульс сохраняется", dm < 1e-6, $"{dm:E1} м/с");
        double push = (t.Velocity - v.Velocity).magnitude;
        Check("Расстыковка: пружины разводят на 0,3 м/с", Math.Abs(push - 0.3) < 0.02, $"{push:F3} м/с");
        for (int i = 0; i < 100; i++) u.Advance(0.1);
        Universe.StateOf(t, u.Time, out var rt, out _);
        Universe.StateOf(v, u.Time, out var rv, out _);
        double gap = (rt - rv).magnitude;
        Check("Расстыковка: не причалил обратно сам, цель не прыгает", !v.IsDocked && u.Vessels.Contains(t) && gap < 40, $"центры в {gap:F1} м");
        u.Docking = new DockingAutopilot(u, t);
        double t0 = u.Time;
        while (u.Time - t0 < 1800 && !u.Active.IsDocked && u.Active.Alive) u.Advance(0.1);
        Check("Расстыковка: автопилот снова собирает связку", u.Active.IsDocked, $"{u.Time - t0:F0} с");
    }

    /// <summary>Ракета из конструктора летит: «Семёрка» сама выходит на орбиту 200 км и сбрасывает боковушки.</summary>
    static void TestCraftFly()
    {
        var b = CraftCompiler.Compile(CraftPresets.Semyorka());
        if (!b.Ok) { Check("Семёрка из конструктора: сборка", false, string.Join("; ", b.Errors)); return; }
        var (u, tr) = StartMission("sputnik");
        u.Vessels.Remove(u.Active);
        u.Launch(b.Design, MissionCatalog.Get("sputnik").SiteId);
        bool ok = Ascend(u, tr, 200000);
        var v = u.Active;
        int debris = u.Vessels.Count(x => x != v);
        Console.WriteLine($"   {OrbitText(v, u.Time)}, обломков {debris}");
        Check("Семёрка из конструктора: на орбите", ok && v.Alive, v.DestroyReason ?? "");
        Check("Семёрка из конструктора: боковушки сброшены", !v.Attached[Array.FindIndex(b.Design.Sections.ToArray(), x => x.IsRadial)]);
    }

    static void TestSeparation()
    {
        var (u, _) = StartMission("vostok");
        var v = u.Active;
        v.Situation = Situation.Flying;
        v.AngularVelocity = new Vector3d(0.02, 0, -0.01);
        var secs = v.Design.Sections;
        double worstPos = 0, worstMom = 0, worstOut = double.MaxValue;
        int parts = 0, halves = 0;
        while (v.HasNextStage)
        {
            var h0 = new double[secs.Count];
            v.Layout(h0);
            v.MassProperties(out double m0, out double com0, out _, out _);
            var bottom0 = v.Position - v.NoseP * com0;
            var nose = v.NoseP;
            var mom0 = v.Velocity * m0;
            var mom = Vector3d.zero;
            var list = v.Stage();
            if (list.Count == 0) continue;
            list.Add(v);
            foreach (var w in list)
            {
                int low = Array.IndexOf(w.Attached, true);
                // Боковой блок (VesselDesign.RadialPiece) — свой борт из одной секции: низ — по своей группе,
                // а от оси пакета он отстоит на RadialOffset, поэтому сверяем осевую и боковую части отдельно.
                int radial = w == v ? -1 : secs.FindIndex(x => x.IsRadial && x.Name == w.Design.Name);
                if (radial >= 0) low = radial;
                w.MassProperties(out double m, out double com, out _, out _);
                var diff = w.Position - nose * com - (bottom0 + nose * h0[low]);
                double axial = Vector3d.Dot(diff, nose);
                double side = (diff - nose * axial).magnitude;
                double err = Math.Abs(axial) + Math.Abs(side - (radial >= 0 ? secs[radial].RadialOffset : 0));
                Console.WriteLine($"   {w.Name}: ЦМ {com:F1} м от своего низа, низ в пакете {h0[low]:F1} м, ошибка {err:F4} м");
                worstPos = Math.Max(worstPos, err);
                if (w.FairingHalf != 0)
                {
                    // Низ створки относительно ракеты должен уходить наружу, иначе ступень пройдёт сквозь неё.
                    var outward = v.LocalToWorld(new Vector3d(w.FairingHalf, 0, 0));
                    var rel = w.Velocity - v.Velocity
                              + w.LocalToWorld(Vector3d.Cross(w.AngularVelocity - v.AngularVelocity, new Vector3d(0, -com, 0)));
                    worstOut = Math.Min(worstOut, Vector3d.Dot(rel, outward));
                    halves++;
                }
                mom += w.Velocity * m;
                parts++;
            }
            // Вращение тоже меняет импульс частей (ω × r), но в сумме по ЦМ — ноль; остаётся только погрешность.
            worstMom = Math.Max(worstMom, (mom - mom0).magnitude / m0);
        }
        Check("Отделение: части остаются на своих местах в пакете", parts >= 6 && worstPos < 1e-3, $"{parts} частей, {worstPos:F4} м");
        Check("Отделение: импульс сохраняется", worstMom < 1e-6, $"{worstMom:E1} м/с");
        Check("Обтекатель раскрывается на две створки, низ уходит наружу", halves == 2 && worstOut > 0.3,
            $"{halves} створки, низ наружу {worstOut:F2} м/с");
    }

    static void TestKarman()
    {
        var (u, tr) = StartMission("karman");
        var v = u.Active;
        u.Stage(); // зажигание
        double apex = 0;
        bool separated = false, chute = false;
        Fly(u, tr, 2, 3000, () => tr.Status == MissionStatus.Active, () =>
        {
            v = u.Active;
            apex = Math.Max(apex, v.Altitude);
            if (!separated && v.VerticalSpeed < 0 && v.Altitude > 50000)
            {
                u.Stage();
                separated = true;
            }
            if (separated && !chute && v.Altitude < 6000 && v.DynamicPressure < 20000)
            {
                u.Stage();
                chute = true;
            }
        });
        Console.WriteLine($"   апогей {apex / 1000:F1} км, итог: {v.Situation}, {v.DestroyReason}");
        Check("Линия Кармана выполнена", tr.Status == MissionStatus.Success, tr.FailReason ?? "");
    }

    /// <summary>
    /// Уставка высоты ввода парашюта (§4.8, окно детали): «Карман» с куполом на 4 км вместо штатных 7 — взводим сразу
    /// после отделения, раскрыться он должен у 4 км, а капсула — сесть целой. Плюс зажим уставки и баки в строках ступеней.
    /// </summary>
    static void TestChuteAltitude()
    {
        Check("Парашют: уставка 0 — штатные 7 км", FlightPhysics.ClampChuteAltitude(0) == FlightPhysics.ChuteDeployAltitude);
        Check("Парашют: уставка зажата сверху", FlightPhysics.ClampChuteAltitude(50000) == FlightPhysics.ChuteAltitudeMax);
        Check("Парашют: уставка зажата снизу", FlightPhysics.ClampChuteAltitude(10) == FlightPhysics.ChuteAltitudeMin);

        var (u, tr) = StartMission("karman");
        var v = u.Active;
        var st = v.RemainingStats();
        bool secs = st.Count > 0;
        foreach (var x in st) secs &= x.Sections.Count > 0 && x.Propellant > 0;
        Check("Строки ступеней знают свои секции и топливо", secs, $"{st.Count} строк");

        const double Target = 4000;
        u.Stage(); // зажигание
        bool separated = false, armed = false;
        double deployAlt = double.NaN;
        Fly(u, tr, 2, 3000, () => tr.Status == MissionStatus.Active, () =>
        {
            v = u.Active;
            if (!separated && v.VerticalSpeed < 0 && v.Altitude > 50000)
            {
                u.Stage();
                separated = true;
                v = u.Active;
                for (int i = 0; i < v.Attached.Length; i++)
                    if (v.Attached[i] && v.Design.Sections[i].ParachuteArea > 0) v.SetChuteAltitude(i, Target);
            }
            // Взвод сразу после отделения (на 50+ км): штатный купол раскрылся бы на 7 км, этот — должен ждать 4 км.
            if (separated && !armed && v == u.Active) { u.Stage(); armed = true; }
            if (armed && double.IsNaN(deployAlt))
                for (int i = 0; i < v.Attached.Length; i++)
                    if (v.Attached[i] && v.ChuteDeployed[i]) { deployAlt = v.Altitude; break; }
        });
        Console.WriteLine($"   купол раскрыт на {deployAlt:F0} м, итог: {v.Situation}, {v.DestroyReason}");
        // Шаг физики на спуске ~0,02 с × 200 м/с — купол раскрывается в пределах пары сотен метров под уставкой.
        Check("Парашют раскрылся у уставки 4 км", deployAlt <= Target && deployAlt > Target - 300, $"{deployAlt:F0} м");
        Check("Капсула с низким вводом села целой", tr.Status == MissionStatus.Success, tr.FailReason ?? "");
    }

    static void TestSputnik()
    {
        var (u, tr) = StartMission("sputnik");
        bool orbit = Ascend(u, tr, 220000);
        Check("Спутник: автопилот вывел на орбиту", orbit);
        if (!orbit) return;
        Fly(u, tr, 5, 4 * 3600, () => tr.Status == MissionStatus.Active);
        Check("Спутник: миссия выполнена (виток на орбите)", tr.Status == MissionStatus.Success, tr.FailReason ?? OrbitText(u.Active, u.Time));
    }

    /// <summary>Промах по перицентру у Луны, после которого нужна коррекция на трассе, м.</summary>
    const double MidcourseTolerance = 300e3;

    static void TestLunar(string id, double miss, double maxTransfer = double.PositiveInfinity, bool land = false,
                          Action<Universe, MissionTracker> afterLanding = null, bool lunarOrbit = false)
    {
        var (u, tr) = StartMission(id);
        var earth = u.Active.Body;
        var moon = u.System.Get("moon");
        // Окно старта: плоскость опорной орбиты проходит через Луну к прибытию (GDD §6.2).
        double flight = 3300 + (double.IsInfinity(maxTransfer) ? 5 * 86400 : maxTransfer);
        double tL = LaunchWindow.NextPlaneWindow(earth, SolarSystem.GetSite(tr.Def.SiteId), 90, moon, u.Time + 60, flight);
        Check($"{id}: окно старта найдено", !double.IsNaN(tL));
        Console.WriteLine($"   окно старта: через {Clock(tL - u.Time)}");
        if (tL - u.Time > 300) Fly(u, tr, 5, tL - u.Time - 200, () => true);
        Fly(u, tr, 3, tL - u.Time, () => true);
        if (!Ascend(u, tr, 200000)) { Check($"{id}: опорная орбита", false); return; }
        var v = u.Active;
        var sw = Stopwatch.StartNew();
        var plan = TransferPlanner.PlanIntercept(earth, v.Position, v.Velocity, u.Time, moon, miss, 12 * 86400, maxTransfer);
        Console.WriteLine($"   план: через {Clock(plan.Time - u.Time)}, Δv {plan.Prograde:F0}/{plan.Normal:F0} м/с, " +
                          $"промах {plan.Miss / 1000:F0} км, перелёт {Clock(plan.ArrivalTime - plan.Time)} ({sw.ElapsedMilliseconds} мс)");
        Check($"{id}: планировщик нашёл перелёт", plan != null && Math.Abs(plan.Miss - miss) < 2000e3);

        u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
        var patches = u.PredictActive();
        foreach (var p in patches)
            Console.WriteLine($"      участок {p.Body.Name}: до {Clock(p.EndTime - u.Time)} → {p.EndType} {p.NextBody?.Name}" +
                              (p.Body == moon ? $", Pe {(p.Orbit.PeriapsisRadius - moon.Radius) / 1000:F0} км" : ""));
        Check($"{id}: прогноз видит вход в SOI Луны", patches.Exists(p => p.EndType == TransitionType.Encounter && p.NextBody == moon));

        u.NodePilot = new NodeAutopilot();
        Fly(u, tr, 6, plan.Time - u.Time + 86400, () => u.NodePilot != null && u.Active.Alive);
        v = u.Active;
        Console.WriteLine($"   после разгона: {OrbitText(v, u.Time)}, масса {v.Mass / 1000:F2} т");
        patches = u.PredictActive();
        var lunar = patches.Find(p => p.Body == moon);
        Console.WriteLine(lunar != null ? $"   прогноз у Луны: Pe {(lunar.Orbit.PeriapsisRadius - moon.Radius) / 1000:F0} км" : "   прогноз: Луна не достигается");
        // Коррекция на трассе (как у всех настоящих лунных станций): промах разгона добирается малым импульсом
        // через несколько часов, пока Луна далеко и цена поправки — единицы-десятки м/с.
        if (lunar == null || Math.Abs(lunar.Orbit.PeriapsisRadius - miss) > MidcourseTolerance)
        {
            var fix = TransferPlanner.PlanIntercept(earth, v.Position, v.Velocity, u.Time + 2 * 3600, moon, miss, 86400, maxTransfer);
            if (fix != null)
            {
                Console.WriteLine($"   коррекция: через {Clock(fix.Time - u.Time)}, Δv {fix.Prograde:F1}/{fix.Normal:F1}/{fix.Radial:F1} м/с, промах {fix.Miss / 1000:F0} км");
                u.SetNode(fix.Time, fix.Prograde, fix.Normal, fix.Radial);
                u.NodePilot = new NodeAutopilot();
                Fly(u, tr, 6, fix.Time - u.Time + 3600, () => u.NodePilot != null && u.Active.Alive);
                v = u.Active;
                lunar = u.PredictActive().Find(p => p.Body == moon);
                Console.WriteLine(lunar != null ? $"   после коррекции: Pe {(lunar.Orbit.PeriapsisRadius - moon.Radius) / 1000:F0} км" : "   после коррекции: Луна не достигается");
            }
            else Console.WriteLine("   коррекция: план не найден");
        }

        Action landLog = null;
        if (land)
        {
            // Программа «Посадка» включается сразу: до сферы влияния Луны она ждёт, ускорение сбросит сама.
            u.Landing = new LandingAutopilot(moon);
            var lp = u.Landing.Phase;
            double nextLog = 0;
            landLog = () =>
            {
                var a = u.Landing;
                if (a == null || a.Phase == LandingAutopilot.PhaseType.Coast && lp == a.Phase) return;
                if (a.Phase != lp || u.Time >= nextLog)
                {
                    lp = a.Phase;
                    nextLog = u.Time + (a.Phase == LandingAutopilot.PhaseType.Terminal ? 2 : 10);
                    var lv = u.Active;
                    Console.WriteLine($"      {a.Phase,-9} h {moon.AltitudeAboveTerrain(lv.Position),8:F0} м  vs {lv.VerticalSpeed,7:F1}  vh {lv.HorizontalSpeed,7:F1}  " +
                                      $"{lv.Mass,7:F0} кг  {a.Status}");
                }
            };
        }
        if (lunarOrbit)
        {
            // Выход на окололунную орбиту: торможение в перицентре до круговой (как LOI «Аполлона-8»).
            Fly(u, tr, 7, 6 * 86400, () => u.Active.Body != moon && u.Active.Alive);
            v = u.Active;
            if (v.Body == moon)
            {
                var o = KeplerOrbit.FromState(v.Position, v.Velocity, moon.Mu, u.Time);
                double rp = o.PeriapsisRadius, tp = o.TimeToPeriapsis(u.Time);
                double vp = Math.Sqrt(moon.Mu * (2 / rp - 1 / o.A)), vc = Math.Sqrt(moon.Mu / rp);
                Console.WriteLine($"   у Луны: Pe {(rp - moon.Radius) / 1000:F0} км через {Clock(tp)}, торможение {vp - vc:F0} м/с");
                u.SetNode(u.Time + tp, -(vp - vc), 0, 0);
                u.NodePilot = new NodeAutopilot();
                Fly(u, tr, 7, tp + 3600, () => u.NodePilot != null && u.Active.Alive);
                Console.WriteLine($"   после LOI: {OrbitText(u.Active, u.Time)}");
            }
            Fly(u, tr, 5, 6 * 3600, () => !tr.Done[0] && u.Active.Alive);
            Check($"{id}: виток вокруг Луны", tr.Done[0], OrbitText(u.Active, u.Time));
            return;
        }
        Fly(u, tr, 7, 20 * 86400, () => tr.Status == MissionStatus.Active && u.Active.Alive &&
                                         !(land && u.Active.IsLanded && u.Active.Body == moon), landLog);
        if (land)
        {
            var lv = u.Active;
            double left = 0;
            for (int i = 0; i < lv.Attached.Length; i++) if (lv.Attached[i]) left += lv.Propellant[i];
            Console.WriteLine($"   посадка: {lv.Situation}, остаток топлива {left:F0} кг");
            if (lv.IsLanded && afterLanding != null) afterLanding(u, tr);
        }
        // Падение на Луну доводит физика: ускорение сброшено у рельефа, дальше шагаем в реальном темпе.
        if (tr.Status == MissionStatus.Active && u.Active.Body == moon)
            Fly(u, tr, 2, 3600, () => tr.Status == MissionStatus.Active);
        Console.WriteLine($"   итог: {(u.Active.Alive ? OrbitText(u.Active, u.Time) : u.Active.DestroyReason)}");
        Check($"{id}: миссия выполнена", tr.Status == MissionStatus.Success, tr.FailReason ?? "");
    }

    /// <summary>
    /// «Аполлон» целиком на автопилотах (§6.4, §6.6, §6.11): окно старта, опорная орбита, LunarAutopilot (разгон,
    /// перестроение с причаливанием к ЛМ, отброс S-IVB, коррекция, торможение до 110 км). У «Аполлона-11» дальше —
    /// расстыковка (управление ЛМ), посадка, взлёт взлётной ступени и сближение со стыковкой к КСМ.
    /// </summary>
    static void TestApollo(string id)
    {
        var (u, tr) = StartMission(id);
        var earth = u.Active.Body;
        var moon = u.System.Get("moon");
        double tL = LaunchWindow.NextPlaneWindow(earth, SolarSystem.GetSite(tr.Def.SiteId), 90, moon, u.Time + 60, 3300 + 5 * 86400);
        Check($"{id}: окно старта найдено", !double.IsNaN(tL));
        if (tL - u.Time > 300) Fly(u, tr, 5, tL - u.Time - 200, () => true);
        Fly(u, tr, 3, tL - u.Time, () => true);
        if (!Ascend(u, tr, 200000)) { Check($"{id}: опорная орбита", false); return; }

        u.Lunar = new LunarAutopilot(u, moon);
        var lp = u.Lunar.Phase;
        string ls = null;
        double nextLog = 0;
        // «Аполлон-8» летит на автоускорении (§6.11: ускорение ведёт автопилот, игрок ничего не жмёт), «Аполлон-11» —
        // на ручном ×1e4: автопилот обязан работать и под ускорением, выставленным игроком.
        bool auto = id == "apollo8";
        u.AutoWarp = auto;
        int frames = 0;
        double maxWarp = 0;
        Fly(u, tr, auto ? 0 : 6, 8 * 86400, () => u.Lunar != null && u.Active.Alive, () =>
        {
            frames++;
            maxWarp = Math.Max(maxWarp, u.EffectiveWarp);
            var a = u.Lunar;
            if (a == null) return;
            bool docking = a.Phase == LunarAutopilot.PhaseType.Docking;
            if (a.Phase != lp || docking && u.Time >= nextLog && a.Status != ls)
            {
                lp = a.Phase;
                ls = a.Status;
                nextLog = u.Time + 20;
                var lp2 = u.PredictActive().Find(p => p.Body == moon);
                Console.WriteLine($"      {a.Phase,-13} {u.Active.Name}, {u.Active.Mass / 1000:F1} т  {a.Status}  прогноз Pe {(lp2 != null ? ((lp2.Orbit.PeriapsisRadius - moon.Radius) / 1000).ToString("F0") : "—")} км");
            }
        });
        var v = u.Active;
        Console.WriteLine($"   после автопилота: {OrbitText(v, u.Time)}, {v.Name}, {v.Mass / 1000:F2} т; кадров {frames} ({frames / 600.0:F1} мин при 60 к/с), макс. ×{maxWarp:0}, в конце ×{Universe.Warps[u.WarpIndex]:0}");
        if (auto) Check($"{id}: автоускорение — перелёт меньше 10 мин реального времени, в конце ×1", frames < 6000 && u.WarpIndex == 0, $"{frames} кадров");
        u.AutoWarp = false;
        var o = v.Body == moon ? KeplerOrbit.FromState(v.Position, v.Velocity, moon.Mu, u.Time) : null;
        Check($"{id}: окололунная орбита автопилотом", o != null && o.PeriapsisRadius - moon.Radius > 60e3 &&
                                                     o.ApoapsisRadius - moon.Radius < 200e3, OrbitText(v, u.Time));
        if (o == null) return;

        if (id == "apollo8")
        {
            Fly(u, tr, 5, 6 * 3600, () => !tr.Done[0] && u.Active.Alive);
            Check($"{id}: виток вокруг Луны", tr.Done[0], OrbitText(u.Active, u.Time));
            return;
        }

        // Перестроение: ЛМ (секции 3–4) в связке с КСМ (7), S-IVB (2) отброшена.
        Check($"{id}: ЛМ пристыкован, S-IVB отброшена", v.Attached[3] && v.Attached[4] && v.Attached[7] && !v.Attached[2],
              string.Join("", Array.ConvertAll(v.Attached, b => b ? "1" : "0")));
        var csm = v;
        while (u.Active == csm && csm.NextStageLabel != null) u.Stage(); // расстыковка с переходом экипажа
        var lm = u.Active;
        Check($"{id}: управление перешло на ЛМ", lm != csm && lm.Attached[3] && !lm.Attached[7], lm.Name);
        if (lm == csm) return;
        Fly(u, tr, 0, 30, () => true); // отход на безопасное расстояние

        u.Landing = new LandingAutopilot(moon);
        var lph = u.Landing.Phase;
        Fly(u, tr, 6, 86400, () => u.Active.Alive && !u.Active.IsLanded, () =>
        {
            var a = u.Landing;
            if (a != null && a.Phase != lph)
            {
                lph = a.Phase;
                Console.WriteLine($"      {a.Phase,-9} h {moon.AltitudeAboveTerrain(u.Active.Position),8:F0} м  {u.Active.Mass,7:F0} кг  {a.Status}");
            }
        });
        lm = u.Active;
        Console.WriteLine($"   посадка: {lm.Situation}, {lm.DestroyReason}");
        Check($"{id}: ЛМ сел", lm.IsLanded && lm.Body == moon && tr.Done[0], lm.Situation.ToString());
        if (!lm.IsLanded) return;
        Fly(u, tr, 0, 10, () => true);

        u.Stage(); // отделение взлётной ступени с зажиганием
        Console.WriteLine($"   азимут в плоскость КСМ: {AscentAutopilot.AzimuthToPlane(u.Active, csm, u.Time):F1}°");
        bool orbit = Ascend(u, tr, 30000, csm);
        lm = u.Active;
        Check($"{id}: взлётная ступень на орбите", orbit, OrbitText(lm, u.Time));
        Console.WriteLine($"   КСМ: {OrbitText(csm, u.Time)}");
        if (!orbit) return;

        u.Docking = new DockingAutopilot(u, csm);
        var dp = u.Docking.Phase;
        string ds = null;
        double dLog = 0, minD = double.PositiveInfinity;
        Fly(u, tr, 6, 2 * 86400, () => u.Docking != null && u.Active.Alive, () =>
        {
            var a = u.Docking;
            if (a == null) return;
            Universe.StateOf(csm, u.Time, out var rc, out _);
            double d = Vector3d.Distance(rc, u.Active.Position);
            minD = Math.Min(minD, d);
            if (a.Phase != dp || a.Phase == DockingAutopilot.PhaseType.Rcs && u.Time >= dLog && a.Status != ds)
            {
                dp = a.Phase;
                ds = a.Status;
                dLog = u.Time + 30;
                Console.WriteLine($"      {a.Phase,-7} d {d,9:F0} м  {u.Active.Mass,6:F0} кг  {a.Status}");
            }
        });
        v = u.Active;
        bool docked = !u.Vessels.Contains(csm) || !u.Vessels.Contains(lm);
        Console.WriteLine($"   сближение: min {minD:F1} м, активный {v.Name}, {OrbitText(v, u.Time)}");
        Check($"{id}: взлётная ступень причалила к КСМ", docked && v.Attached[4] && v.Attached[7],
              string.Join("", Array.ConvertAll(v.Attached, b => b ? "1" : "0")));

        Fly(u, tr, 5, 4 * 3600, () => !tr.Done[1] && tr.Status == MissionStatus.Active && u.Active.Alive);
        Console.WriteLine($"   итог: {(u.Active.Alive ? OrbitText(u.Active, u.Time) : u.Active.DestroyReason)}");
        Check($"{id}: посадка и орбита ЛМ засчитаны", tr.Done[0] && tr.Done[1] && tr.Status != MissionStatus.Failed, tr.FailReason ?? "");

        // Экипаж — в «Колумбию»: взлётная ступень отбрасывается, SPS остаётся взведённым на разгон к Земле.
        // Сам возврат проверяет auto_apollo11.
        for (int k = 0; k < 4 && u.Active.Attached.Where((b, i) => b && u.Active.Flipped[i]).Any(); k++) u.Stage();
        v = u.Active;
        Check($"{id}: экипаж в КСМ, SPS взведён", v.Attached[7] && !v.Attached[4] && v.Armed[6] && u.Vessels.Count(o => o.Attached[4]) == 1,
              string.Join("", Array.ConvertAll(v.Attached, b => b ? "1" : "0")) + $" armed6={v.Armed[6]}");
    }

    /// <summary>«Луноход-1»: сброс посадочной ступени (съезд по трапам) и 100 м своим ходом (GDD §6.12).</summary>
    static void DriveLunokhod(Universe u, MissionTracker tr)
    {
        // Трапы сложены (§6.12): пробел не сбрасывает ступень, G раскладывает за FlightPhysics.RampDeployTime.
        var lander = u.Active;
        u.Stage();
        Check("luna17: сложенные трапы держат луноход", !u.Active.IsRover && lander.StageBlock != null, lander.StageBlock ?? "сброшено");
        u.ToggleDeploy();
        double tr0 = u.Time;
        Fly(u, tr, 0, 30, () => !lander.RampsDown && lander.Alive);
        Check("luna17: трапы разложены", lander.RampsDown && lander.StageBlock == null, $"{u.Time - tr0:F1} с");
        {
            var up = lander.Position.normalized;
            double tilt = Math.Acos(Math.Min(1, Vector3d.Dot(lander.NoseP, up))) * 180 / Math.PI;
            double R = lander.AnchorBodyFixed.magnitude, hb = R - lander.Body.Radius - lander.Body.SurfaceHeight(lander.AnchorBodyFixed / R);
            lander.MassProperties(out _, out double lc, out _, out _);
            Console.WriteLine($"   КТ: наклон {tilt:F1}°, низ над грунтом {hb - lc:F2} м, подвеска {lander.Suspension:F3}");
        }
        for (int n = 0; n < 8 && !u.Active.IsRover && u.Active.HasNextStage; n++) u.Stage();
        var v = u.Active;
        Check("luna17: луноход отделился на настиле", v.IsRover && v.RampDeck > 1, $"{v.Situation} настил {v.RampDeck:F2} м");
        // Крышка (§6.12): G открывает за FlightPhysics.LidDeployTime, повтор закрывает.
        int lid = v.Design.Sections.FindIndex(s => s.Deploy == DeployKind.Lid);
        string said = v.ToggleDeploy();
        Fly(u, tr, 0, 10, () => lid >= 0 && v.Deployed[lid] < 1 && v.Alive);
        bool opened = lid >= 0 && v.Deployed[lid] == 1;
        v.ToggleDeploy();
        Fly(u, tr, 0, 10, () => lid >= 0 && v.Deployed[lid] > 0 && v.Alive);
        Check("luna17: крышка открывается и закрывается", opened && v.Deployed[lid] == 0, said ?? "—");
        // Съезд по трапу: высота спадает плавно, без прыжка на грунт; на рельсах поворот закрыт.
        v.PilotInput = new Vector3d(1, 0.3, 0);
        double deck = v.RampDeck, maxJump = 0, prevH = double.NaN, t0 = u.Time;
        Fly(u, tr, 0, 60, () => v.RampDeck > 0 && v.Alive, () =>
        {
            double R = v.AnchorBodyFixed.magnitude, h = R - v.Body.Radius - v.Body.SurfaceHeight(v.AnchorBodyFixed / R);
            if (!double.IsNaN(prevH)) maxJump = Math.Max(maxJump, Math.Abs(h - prevH));
            prevH = h;
        });
        Console.WriteLine($"   съезд с настила {deck:F2} м за {u.Time - t0:F1} с, путь {v.RampTravel:F1} м, макс. шаг высоты {maxJump * 100:F1} см");
        Check("luna17: съехал по трапу без прыжка", v.RampDeck == 0 && maxJump < 0.05, $"{maxJump:F3} м");
        Fly(u, tr, 0, 600, () => tr.Status == MissionStatus.Active && v.Alive);
        v.PilotInput = Vector3d.zero;
        Console.WriteLine($"   проехал {v.DriveDistance:F0} м, {v.Situation}");
    }

    /// <summary>Орбита, виток, торможение (жидкостное по SAS-ретро или РДТТ) и спуск капсулы на парашюте.</summary>
    static void TestOrbitReturn(string id, double target)
    {
        var (u, tr) = StartMission(id);
        if (!Ascend(u, tr, target)) { Check($"{id}: орбита", false); return; }
        Fly(u, tr, 5, 4 * 3600, () => !tr.Done[0] && u.Active.Alive);
        Check($"{id}: виток выполнен", tr.Done[0], OrbitText(u.Active, u.Time));
        if (tr.Def.Objectives.Count < 2 || !tr.Done[0]) return;
        while (u.Active.NextStageLabel != null && u.Active.Design.Sequence[u.Active.NextStage].Type != StageActionType.Ignite)
            u.Stage();
        var v = u.Active;
        v.Sas = SasMode.Retrograde;
        Fly(u, tr, 2, 120, () => true);
        v.Throttle = 1;
        u.Stage(); // тормозные двигатели
        var earth = v.Body;
        Fly(u, tr, 2, 900, () => v.Alive && KeplerOrbit.FromState(v.Position, v.Velocity, earth.Mu, u.Time).PeriapsisRadius - earth.Radius > 40000);
        v.Throttle = 0;
        Console.WriteLine($"   после торможения: {OrbitText(v, u.Time)}");
        while (u.Active.NextStageLabel != null && u.Active.Design.Sequence[u.Active.NextStage].Type != StageActionType.DeployParachute)
            u.Stage();
        v = u.Active;
        v.Sas = SasMode.Off;
        u.Stage(); // парашют взводится, раскрывает автоматика
        double maxG = 0;
        Fly(u, tr, 2, 6 * 3600, () => tr.Status == MissionStatus.Active, () => { if (v.Situation == Situation.Flying) maxG = Math.Max(maxG, v.GForce); });
        Console.WriteLine($"   спуск: max {maxG:F1} g, итог {v.Situation} {v.DestroyReason}");
        Check($"{id}: миссия выполнена", tr.Status == MissionStatus.Success, tr.FailReason ?? "");
    }

    /// <summary>
    /// Программный наклон «Фридом-7», град; дальше нос ведёт ограничитель угла атаки (гравитационный разворот).
    /// Строго вертикальный подъём на 385 км давал на спуске 14 g дольше 10 с. Перебор: 15° без отсечки —
    /// 147 км и 10,8 g, 20° — 88 км: без отсечки апогей слишком чувствителен к наклону.
    /// </summary>
    const double FreedomTilt = 10;
    /// <summary>
    /// Отсечка «Редстоуна» по баллистическому апоцентру, м. С наклоном 10°: апогей 181 км, пик 11,1 g
    /// (у Шепарда — 187 км и 11 g). Пара: CrewGLimit/CrewGTime — при 150 км пик 9,7 g, при 187 км 11,8 g.
    /// </summary>
    const double FreedomApex = 180000;
    /// <summary>
    /// Цель апоцентра Juno I, м. Настоящий «Эксплорер-1» ушёл на 358 × 2550 км, но в модели связки РДТТ дают
    /// 5,18 км/с: подъём к 237 км оставлял 80 м/с недобора до круговой. Ниже апоцентр — больше горизонтальной
    /// скорости после I ступени. Пара: цель миссии — перицентр выше 150 км.
    /// </summary>
    const double JunoTarget = 200000;
    /// <summary>Скорость наклона после вертикального участка, град/с; пара к FreedomTilt (наклон за ~20 с).</summary>
    const double FreedomPitchRate = 0.5;
    /// <summary>Длительность вертикального участка, с.</summary>
    const double FreedomVertical = 12;

    /// <summary>«Фридом-7»: суборбитальный прыжок Шепарда — как «Линия Кармана», но с капсулой.</summary>
    static void TestFreedom7()
    {
        var (u, tr) = StartMission("freedom7");
        var v = u.Active;
        u.Stage();
        double apex = 0, maxG = 0, t0 = u.Time, touch = 0;
        bool separated = false, chute = false, opened = false;
        Fly(u, tr, 2, 3000, () => tr.Status == MissionStatus.Active, () =>
        {
            v = u.Active;
            apex = Math.Max(apex, v.Altitude);
            if (v.Situation == Situation.Flying) maxG = Math.Max(maxG, v.GForce);
            if (!separated && v.AnyEngineRunning)
            {
                // Программа тангажа «Редстоуна»: вертикально, затем плавный наклон на восток (в океан).
                FlightControl.LocalFrame(v, u.Time, out var up, out _, out var east);
                double pitch = (90 - Math.Min(FreedomTilt, Math.Max(0, u.Time - t0 - FreedomVertical) * FreedomPitchRate)) * Math.PI / 180;
                // FlightControl.Update обнуляет момент каждый шаг — программа идёт через удержание SAS.
                var dir = AscentAutopilot.LimitAoA(v, u.Time, up * Math.Sin(pitch) + east * Math.Cos(pitch));
                v.Sas = SasMode.Stability;
                v.SasHold = QuaternionD.FromToRotation(v.NoseP.SwapYZ, dir.SwapYZ) * v.Attitude;
                v.SasHoldValid = true;
                // Отсечка по баллистическому апоцентру: модельный «Редстоун» с полной выработкой забрасывал на 300+ км.
                var o = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, u.Time);
                if (o.ApoapsisRadius - v.Body.Radius > FreedomApex) v.Throttle = 0;
            }
            if (!separated && v.VerticalSpeed < 0 && v.Altitude > 50000) { u.Stage(); separated = true; }
            else if (separated && !chute) { u.Stage(); chute = true; }
            // Регрессия «Шепард гибнет при касании»: скорость последнего кадра в полёте ≈ скорость касания.
            if (v.Situation == Situation.Flying) touch = v.SurfaceSpeed;
            for (int i = 0; i < v.ChuteDeployed.Length; i++) opened |= v.Attached[i] && v.ChuteDeployed[i] && !v.ChuteFailed[i];
        });
        Console.WriteLine($"   апогей {apex / 1000:F1} км, max {maxG:F1} g, итог: {v.Situation}, касание {touch:F1} м/с, {v.DestroyReason}");
        Check("Фридом-7 выполнен", tr.Status == MissionStatus.Success, tr.FailReason ?? "");
        // Настоящий «Меркурий» приводнялся на ≈ 9 м/с; порог гибели — FlightPhysics.CrashSpeed (10 м/с).
        Check("Фридом-7: капсула цела, купол раскрыт, приводнение мягче порога удара",
            v.Alive && !v.CrewLost && v.Situation == Situation.Splashed && opened && touch < FlightPhysics.CrashSpeed,
            $"{v.Situation} купол {opened} касание {touch:F1} м/с {v.DestroyReason}");
    }

    /// <summary>
    /// Чит меню Esc «Над Луной 15 км» + автопилот посадки: падение из покоя. Пакет Р-7 на Луну не сажается —
    /// РД-107/108 и РД-0110 не дросселируются и не перезапускаются, тяга в 5–10 лунных весов (на терминальном
    /// участке глохнут и падают с 400 м). Поэтому всё под станцией Е-6 сбрасывается, как после разгона блоком Л.
    /// </summary>
    static void TestMoonDrop()
    {
        foreach (var id in new[] { "luna9" })
        {
            var (u, tr) = StartMission(id);
            var moon = u.System.Get("moon");
            u.Teleport(moon, 15e3, false);
            int station = u.Active.Design.Sections.FindIndex(s => s.Name == "Станция Е-6");
            while (u.Active.BottomSection() < station && u.Active.HasNextStage) u.Stage();
            FlightControl.Cutoff(u.Active);
            u.Landing = new LandingAutopilot(moon);
            var lp = (LandingAutopilot.PhaseType)(-1);
            double nextLog = 0;
            Fly(u, null, 0, 1200, () => u.Active.Alive && !u.Active.IsLanded, () =>
            {
                var a = u.Landing;
                var lv = u.Active;
                if (a == null || a.Phase == lp && u.Time < nextLog) return;
                lp = a.Phase;
                nextLog = u.Time + 10;
                Console.WriteLine($"      {a.Phase,-9} h {moon.AltitudeAboveTerrain(lv.Position),8:F0} м  vs {lv.VerticalSpeed,7:F1}  {a.Status}");
            });
            var v = u.Active;
            Console.WriteLine($"   {id}: {v.Situation} {v.DestroyReason}");
            Check($"moondrop {id}: сел", v.Alive && v.IsLanded, v.DestroyReason ?? "");
        }
    }

    static void TestVostok()
    {
        var (u, tr) = StartMission("vostok");
        if (!Ascend(u, tr, 220000)) { Check("Восток: орбита", false); return; }
        Fly(u, tr, 5, 4 * 3600, () => !tr.Done[0]);
        Check("Восток: виток выполнен", tr.Done[0]);
        // Сброс II ступени и подготовка ТДУ.
        while (u.Active.NextStageLabel != null && u.Active.Design.Sequence[u.Active.NextStage].Type != StageActionType.Ignite)
            u.Stage();
        u.Stage();
        var v = u.Active;
        u.SetNode(u.Time + 300, -95, 0, 0);
        u.NodePilot = new NodeAutopilot();
        Fly(u, tr, 4, 3600, () => u.NodePilot != null && u.Active.Alive);
        Console.WriteLine($"   после ТДУ: {OrbitText(v, u.Time)}");
        u.Stage(); // отделение приборного отсека
        v = u.Active;
        v.Sas = SasMode.Off;
        u.Stage(); // парашют взводится в космосе — раскрывает автоматика (FlightPhysics.ChuteDeployAltitude)
        double maxG = 0, maxHeat = 0, maxEntryG = 0, entryAlt = 0;
        bool chute = false;
        Fly(u, tr, 2, 6 * 3600, () => tr.Status == MissionStatus.Active, () =>
        {
            if (v.Situation == Situation.Flying)
            {
                if (!chute && v.GForce > maxEntryG) { maxEntryG = v.GForce; entryAlt = v.Altitude; }
                maxG = Math.Max(maxG, v.GForce);
                maxHeat = Math.Max(maxHeat, v.HeatFlux);
            }
            if (!chute && v.ChuteDeployed[v.Design.Sections.Count - 1])
            {
                chute = true;
                Console.WriteLine($"      парашют на {v.Altitude / 1000:F1} км, {v.SurfaceSpeed:F0} м/с");
            }
        });
        Console.WriteLine($"   вход: max {maxEntryG:F1} g на {entryAlt / 1000:F0} км");
        Console.WriteLine($"   спуск: max {maxG:F1} g, max тепловой поток {maxHeat / 1e3:F0} кВт/м², итог {v.Situation} {v.DestroyReason}");
        Check("Восток: миссия выполнена", tr.Status == MissionStatus.Success, tr.FailReason ?? "");
    }
}
