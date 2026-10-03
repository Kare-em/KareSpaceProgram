using System;
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
        if (land != null) SolarSystem.EarthLand = new LandMap(System.IO.File.ReadAllBytes(land));
        else Console.WriteLine("   карта суши не найдена — процедурные материки");
        Run("math", TestMath, only);
        Run("orbit", TestOrbit, only);
        Run("moon", TestEphemeris, only);
        Run("atmo", TestAtmosphere, only);
        Run("stats", TestStats, only);
        Run("stability", TestStability, only);
        Run("separation", TestSeparation, only);
        Run("karman", TestKarman, only);
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
        Run("apollo8", () => TestLunar("apollo8", 1737.4e3 + 110e3, lunarOrbit: true), only);
        Run("apollo11", () => TestLunar("apollo11", 1000e3, land: true, afterLanding: LunarLiftoff), only);
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
    static bool Ascend(Universe u, MissionTracker tr, double target)
    {
        var v = u.Active;
        u.Ascent = new AscentAutopilot { TargetAltitude = target };
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
                w.MassProperties(out double m, out double com, out _, out _);
                double err = (w.Position - nose * com - (bottom0 + nose * h0[low])).magnitude;
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

    /// <summary>«Луноход-1»: сброс посадочной ступени (съезд по трапам) и 100 м своим ходом (GDD §6.12).</summary>
    static void DriveLunokhod(Universe u, MissionTracker tr)
    {
        while (!u.Active.IsRover && u.Active.NextStageLabel != null) u.Stage();
        var v = u.Active;
        Check("luna17: луноход съехал", v.IsRover, v.Situation.ToString());
        v.PilotInput = new Vector3d(1, 0.3, 0);
        Fly(u, tr, 0, 600, () => tr.Status == MissionStatus.Active && v.Alive);
        v.PilotInput = Vector3d.zero;
        Console.WriteLine($"   проехал {v.DriveDistance:F0} м, {v.Situation}");
    }

    /// <summary>«Аполлон-11»: взлётная ступень уходит с посадочной на окололунную орбиту.</summary>
    static void LunarLiftoff(Universe u, MissionTracker tr)
    {
        u.Stage(); // отделение взлётной ступени с зажиганием
        bool orbit = Ascend(u, tr, 30000);
        var lm = u.Active;
        double pe = KeplerOrbit.FromState(lm.Position, lm.Velocity, lm.Body.Mu, u.Time).PeriapsisRadius - lm.Body.Radius;
        Check("apollo11: взлётная ступень на орбите", orbit && pe > 10000, OrbitText(lm, u.Time));
        Fly(u, tr, 2, 3600, () => tr.Status == MissionStatus.Active && u.Active.Alive);
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
        double apex = 0, maxG = 0, t0 = u.Time;
        bool separated = false, chute = false;
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
        });
        Console.WriteLine($"   апогей {apex / 1000:F1} км, max {maxG:F1} g, итог: {v.Situation}, {v.DestroyReason}");
        Check("Фридом-7 выполнен", tr.Status == MissionStatus.Success, tr.FailReason ?? "");
    }

    /// <summary>Чит меню Esc «Над Луной 15 км» + G: полный пакет со стола, падение из покоя, автопилот посадки.</summary>
    static void TestMoonDrop()
    {
        foreach (var id in new[] { "luna9", "vostok" })
        {
            var (u, tr) = StartMission(id);
            var moon = u.System.Get("moon");
            u.Teleport(moon, 15e3, false);
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
