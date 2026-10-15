using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// «Кассини–Гюйгенс» (GDD §5.3, §6.11): после тура — торможение у Сатурна (SOI 1.07.2004), виток, наведение
    /// на Титан из апоцентра, отделение «Гюйгенса» и спуск на парашютах с подменой купола (14.01.2005).
    /// </summary>
    public sealed partial class MissionAutopilot
    {
        /// <summary>
        /// Апоцентр первого витка у Сатурна, м: виток ≈ 114 сут (у «Кассини» — 116), орбита Титана (1,22 млн км)
        /// внутри. Пара: торможение ≈ 600 м/с при перицентре 20 тыс. км — по силам орбитеру (2,5 км/с на всю миссию).
        /// </summary>
        const double CaptureApoapsis = 9e9;
        /// <summary>
        /// Поздняя коррекция к планете захвата — снаружи её сферы с запасом: у Сатурна 54,8 млн км при v∞ 5,4 км/с —
        /// 118 сут, а LateCorrection (75 сут) приходилась уже внутрь сферы, где PlanCorrection по Солнцу не работает.
        /// </summary>
        const double CaptureLateFactor = 1.15;
        /// <summary>Подправить перицентр в сфере Сатурна, если он ушёл от цели дальше этого, м.</summary>
        const double CapturePeTolerance = 2e6;
        /// <summary>
        /// Ньютон коррекции у колец: итераций, проба импульса (м/с) и точность прицела (м). Пара: 50 км прицела — доля
        /// окна без колец (ω ± 5,7° у перицентра 80 тыс. км ≈ ± 8 тыс. км по картинной плоскости).
        /// </summary>
        const int RingTrimIterations = 12;
        const double RingTrimProbe = 0.01, RingTrimTolerance = 5e4;
        /// <summary>
        /// Угол входа «Гюйгенса» к горизонту на верхней границе атмосферы Титана, градусы (у настоящего ≈ 65°).
        /// Крутой вход — широкий коридор: прицельная дальность 0–3,3 тыс. км попадает, середина ≈ 1,4 тыс. км.
        /// </summary>
        const double TitanEntryAngle = 65;
        /// <summary>Коррекции к Титану: из апоцентра, затем за столько до встречи, с.</summary>
        static readonly double[] TitanCorrections = { 10 * 86400, 4 * 86400 };
        /// <summary>
        /// Потолок v∞ у Титана при наведении, м/с (у «Гюйгенса» ≈ 5,5 км/с — вход 6,1 км/с). С перицентром 20 тыс. км
        /// после захвата v∞ выходил 7,85 км/с: вход 8,2 км/с, 2,39 МВт/м² — экран сгорал (auto_cassini, 15.10.2026).
        /// Пара: поднять перицентр до ≈ 8 Rs стоит ≈ 390 м/с — как настоящий манёвр подъёма перицентра (PRM, 23.08.2004).
        /// </summary>
        const double TitanVinfMax = 5500;
        /// <summary>Штраф наведения за каждый м/с v∞ сверх TitanVinfMax, в м/с Δv.</summary>
        const double TitanVinfPenalty = 5;
        /// <summary>Шаг перебора даты встречи с Титаном, с.</summary>
        const double TitanScanStep = 6 * 3600;
        /// <summary>Экран сбрасывается через столько после раскрытия основного купола, с (у настоящего — 30 с).</summary>
        const double ShieldDropDelay = 30;
        /// <summary>Основной купол меняется на тормозной через столько после раскрытия, с (как у настоящего — 15 мин).</summary>
        const double MainChuteTime = 15 * 60;

        IEnumerable<object> SaturnTitan()
        {
            var u = universe;
            var def = Tracker.Def;
            int orbitGoal = IndexOf(ObjectiveType.Orbit), landGoal = IndexOf(ObjectiveType.Landing);
            var planet = u.System.Get(def.Objectives[orbitGoal].Body);
            var moon = landGoal >= 0 ? u.System.Get(def.Objectives[landGoal].Body) : null;
            bool released = V.Design.Sections.Count > VesselPresets.CassiniOrbiter && !V.Attached[VesselPresets.CassiniOrbiter];

            if (!released && !Tracker.Done[orbitGoal])
            {
                Phase = $"Подлёт к {planet.Name}";
                foreach (var x in Await(() => V.Body == planet, CoastText)) yield return x;
                foreach (var x in CaptureTrim(planet)) yield return x;
                foreach (var x in Capture(planet)) yield return x;
                foreach (var x in HoldOrbit(orbitGoal)) yield return x;
            }
            if (moon == null) yield break;

            if (!released)
            {
                // Наведение из апоцентра: там скорость ≈ 300 м/с, поворот плоскости и фазировка к Титану дешевле всего.
                var orbit = KeplerOrbit.FromState(V.Position, V.Velocity, planet.Mu, T);
                double tAp = orbit.IsElliptic ? orbit.TimeOfTrueAnomaly(Math.PI, T) : T;
                double start = tAp - CorrectionTurn - 2 * 3600;
                if (start > T)
                    foreach (var x in WaitUntil(start, () => $"Виток у {planet.Name}: до апоцентра {Clock(Math.Max(0, tAp - T))}")) yield return x;
                foreach (var x in MoonApproach(moon)) yield return x;
                foreach (double before in TitanCorrections)
                {
                    double arrival = Arrival(moon, OneRevolution());
                    if (double.IsNaN(arrival) || arrival - before <= T + CorrectionTurn) continue;
                    foreach (var x in WaitUntil(arrival - before, () => $"К {moon.Name}: {Clock(Math.Max(0, arrival - T))}")) yield return x;
                    foreach (var x in MoonCorrection(moon, "Коррекция к " + moon.Name)) yield return x;
                }
                double meet = Arrival(moon, OneRevolution());
                if (double.IsNaN(meet)) throw new Abort($"встречи с {moon.Name} нет");
                // Отделение — уже в сфере Титана, после подправки по точной гиперболе: коническая стыковка по Сатурну
                // промахивалась на ≈ 600 км прицельной дальности (вход −76° вместо −65°). У настоящего — за 20 сут.
                Phase = $"Перелёт к {moon.Name}";
                foreach (var x in Await(() => V.Body == moon, CoastText)) yield return x;
                foreach (var x in EntryTrim(moon)) yield return x;
                Phase = "Отделение зонда";
                StageUntil(StageActionType.Separate);
                u.Stage();
                // Основной купол взводится сразу: раскроет автоматика по барометру (FlightPhysics.UpdateChutes).
                StageUntil(StageActionType.DeployParachute);
                if (V.NextStageLabel != null) u.Stage();
                u.Post($"«{V.Name}»: зонд отделён, вход в атмосферу {moon.Name} через {Clock(Math.Max(0, meet - T))}");
            }

            foreach (var x in TitanDescent(moon)) yield return x;
        }

        /// <summary>Перицентр у планеты захвата — к середине коридора пролёта, пока до него далеко (импульс мал).</summary>
        IEnumerable<object> CaptureTrim(CelestialBody planet)
        {
            var u = universe;
            var o = Tracker.Def.Objectives.Find(x => x.Type == ObjectiveType.Flyby && x.Body == planet.Id);
            if (o == null) yield break;
            double rpGoal = planet.Radius + 0.5 * (o.Min + o.Max);
            var v = V;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, planet.Mu, T);
            if (orbit.TimeToPeriapsis(T) < 10 * 86400) yield break;
            // У планеты с кольцами правим не только перицентр, но и плоскость: прицел из-за сферы промахивался по углу
            // картинной плоскости на 3–4° (≈ 5 тыс. км), окно без колец — ω = 90° ± 5,7°, и узел ложился в кольцо A.
            // Здесь гипербола точная, а до перицентра 4 мес.: те же 5 тыс. км стоят ≈ 0,5 м/с.
            var fix = Rings.HasDense(planet)
                ? PlanRingTrim(planet, v.Position, v.Velocity, rpGoal, T + CorrectionTurn)
                : Math.Abs(orbit.PeriapsisRadius - rpGoal) < CapturePeTolerance ? null
                : TransferPlanner.PlanPerigee(planet, v.Position, v.Velocity, T, rpGoal, T + CorrectionTurn);
            if (fix == null || fix.DeltaV < MinCorrection) yield break;
            u.SetNode(fix.Time, fix.Prograde, fix.Normal, fix.Radial);
            u.Post($"Коррекция перицентра у {planet.Name}: {(orbit.PeriapsisRadius - planet.Radius) / 1e6:F0} → {(rpGoal - planet.Radius) / 1e6:F0} тыс. км, {fix.DeltaV:F1} м/с");
            foreach (var x in Burn($"Коррекция перицентра у {planet.Name}")) yield return x;
        }

        /// <summary>
        /// Прицел гиперболы по вектору состояния: смещение входящей асимптоты от центра (b, м) и её направление uv.
        /// Входящая скорость — P/e + Q·√(1 − 1/e²) (P — к перицентру, Q — по движению в нём); h ∥ b × v∞.
        /// </summary>
        static Vector3d AsymptoteAim(double mu, Vector3d r, Vector3d v, out Vector3d uv)
        {
            var h = Vector3d.Cross(r, v);
            var ev = Vector3d.Cross(v, h) / mu - r.normalized;
            double e = Math.Max(ev.magnitude, 1 + 1e-9);
            var hn = h.normalized;
            var P = ev / ev.magnitude;
            var Q = Vector3d.Cross(hn, P);
            uv = P / e + Q * Math.Sqrt(1 - 1 / (e * e));
            double vinf = Math.Sqrt(Math.Max(1, v.sqrMagnitude - 2 * mu / r.magnitude));
            return Vector3d.Cross(uv, hn) * (h.magnitude / vinf);
        }

        /// <summary>
        /// Коррекция в сфере планеты с кольцами: перицентр rpGoal и плоскость, у которой оба узла захватного эллипса
        /// вне колец (RingSafeSide — тот же выбор, что у прицела TourAim). Импульс поперёк скорости, Ньютон по двум
        /// осям картинной плоскости. null — гипербола не та (уже эллипс) или Ньютон не сошёлся.
        /// </summary>
        TransferPlan PlanRingTrim(CelestialBody planet, Vector3d r, Vector3d v, double rpGoal, double tc)
        {
            double mu = planet.Mu;
            var orbit = KeplerOrbit.FromState(r, v, mu, T);
            if (orbit.IsElliptic) return null;
            orbit.GetState(tc, out var rc, out var vc);
            AsymptoteAim(mu, rc, vc, out var uv);
            double v2 = Math.Max(vc.sqrMagnitude - 2 * mu / rc.magnitude, 1);
            var pole = planet.PoleAt(tc + orbit.TimeToPeriapsis(tc));
            var hh = pole - uv * Vector3d.Dot(pole, uv);
            if (hh.sqrMagnitude < 1e-6) return null;
            var side0 = Vector3d.Cross(uv, hh.normalized);
            double e = (CaptureApoapsis - rpGoal) / (CaptureApoapsis + rpGoal);
            var side = RingSafeSide(planet, pole, uv, side0, rpGoal, v2, e);
            var bGoal = side * (rpGoal * Math.Sqrt(1 + 2 * mu / (rpGoal * v2)));
            var pro = vc.normalized;
            var nrm = Vector3d.Cross(rc, vc).normalized;
            var rad = Vector3d.Cross(pro, nrm);
            var e1 = Vector3d.Cross(uv, nrm).normalized;
            var e2 = Vector3d.Cross(uv, e1);
            double dn = 0, dr = 0;
            Vector3d Err(double n, double d) => AsymptoteAim(mu, rc, vc + nrm * n + rad * d, out _) - bGoal;
            for (int it = 0; it < RingTrimIterations; it++)
            {
                var f = Err(dn, dr);
                double f1 = Vector3d.Dot(f, e1), f2 = Vector3d.Dot(f, e2);
                if (Math.Sqrt(f1 * f1 + f2 * f2) < RingTrimTolerance)
                {
                    var plan = new TransferPlan { Time = tc, Normal = dn, Radial = dr };
                    plan.ArrivalTime = tc + orbit.TimeToPeriapsis(tc);
                    return plan;
                }
                var fn = Err(dn + RingTrimProbe, dr) - f;
                var fr = Err(dn, dr + RingTrimProbe) - f;
                double a = Vector3d.Dot(fn, e1) / RingTrimProbe, b = Vector3d.Dot(fr, e1) / RingTrimProbe;
                double c = Vector3d.Dot(fn, e2) / RingTrimProbe, d = Vector3d.Dot(fr, e2) / RingTrimProbe;
                double det = a * d - b * c;
                if (Math.Abs(det) < 1e-12) return null;
                dn -= (d * f1 - b * f2) / det;
                dr -= (-c * f1 + a * f2) / det;
            }
            return null;
        }

        /// <summary>
        /// Торможение в перицентре до апоцентра CaptureApoapsis. Узел — в самом перицентре: NodeAutopilot начинает
        /// прожиг за половину его длины (у «Кассини» 96 мин на 445 Н), импульс ложится симметрично.
        /// </summary>
        IEnumerable<object> Capture(CelestialBody planet)
        {
            var u = universe;
            var orbit = KeplerOrbit.FromState(V.Position, V.Velocity, planet.Mu, T);
            if (orbit.IsElliptic && orbit.ApoapsisRadius < planet.SoiRadius) yield break;
            double tPe = T + orbit.TimeToPeriapsis(T);
            orbit.GetState(tPe, out var rPe, out var vPe);
            double rp = rPe.magnitude;
            double vNew = Math.Sqrt(planet.Mu * (2 / rp - 2 / (rp + CaptureApoapsis)));
            double dv = vNew - vPe.magnitude;
            u.SetNode(tPe, dv, 0, 0);
            u.Post($"Выход на орбиту {planet.Name}: перицентр {(rp - planet.Radius) / 1e6:F1} тыс. км, Δv {-dv:F0} м/с, " +
                   $"прожиг {Clock(FlightControl.BurnTime(V, -dv))}");
            foreach (var x in Burn($"Выход на орбиту {planet.Name}")) yield return x;
            var after = KeplerOrbit.FromState(V.Position, V.Velocity, planet.Mu, T);
            if (!after.IsElliptic || after.ApoapsisRadius > planet.SoiRadius) throw new Abort($"захват у {planet.Name} не удался");
            u.Post($"Орбита {planet.Name}: {(after.PeriapsisRadius - planet.Radius) / 1e6:F1} × {(after.ApoapsisRadius - planet.Radius) / 1e6:F0} тыс. км, " +
                   $"виток {Clock(after.Period)}");
        }

        /// <summary>
        /// Коррекция к спутнику по прицелу входа: прицельная дальность b = r·v·cos γ / v∞ на верхней границе атмосферы
        /// (γ — TitanEntryAngle), с той стороны, с какой борт идёт сейчас. lead растёт под длинный прожиг, как в Correct.
        /// </summary>
        IEnumerable<object> MoonCorrection(CelestialBody moon, string what)
        {
            var u = universe;
            var v = V;
            var aim = EntryAim(moon);
            double lead = CorrectionTurn;
            TransferPlan plan = null;
            for (int pass = 0; pass < 3; pass++)
            {
                plan = TransferPlanner.PlanCorrection(v.Body, v.Position, v.Velocity, T, moon, aim, lead, AimPasses);
                if (plan == null) break;
                double half = FlightControl.BurnTime(v, plan.DeltaV) / 2;
                if (double.IsInfinity(half) || half + CorrectionTurn <= lead) break;
                lead = half + CorrectionTurn;
            }
            if (plan == null) throw new Abort($"наведение на {moon.Name} не найдено");
            if (plan.DeltaV < MinCorrection) yield break;
            Phase = what;
            u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
            u.Post($"{what}: {plan.DeltaV:F1} м/с, встреча {GameCalendar.Format(plan.ArrivalTime)}");
            foreach (var x in Burn(what)) yield return x;
        }

        /// <summary>
        /// Наведение на Титан из апоцентра: перебор даты встречи на витке, Ламберт к точке прицела входа; цена — Δv
        /// плюс штраф за v∞ выше TitanVinfMax. PlanCorrection брал самую дешёвую встречу (206 м/с) с v∞ 7,85 км/с.
        /// </summary>
        IEnumerable<object> MoonApproach(CelestialBody moon)
        {
            var u = universe;
            var v = V;
            var aim = EntryAim(moon);
            double lead = CorrectionTurn;
            TransferPlan plan = null;
            double vinfBest = double.NaN;
            for (int pass = 0; pass < 3; pass++)
            {
                var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, T);
                double tc = T + lead;
                orbit.GetState(tc, out var rc, out var vc);
                var h = Vector3d.Cross(rc, vc).normalized;
                double window = orbit.IsElliptic ? orbit.Period : 365.25 * 86400;
                double bestCost = double.PositiveInfinity, bestT = double.NaN;
                for (double tA = tc + 2 * 86400; tA < tc + window; tA += TitanScanStep)
                {
                    moon.LocalStateAt(tA, out var rt, out var vt);
                    if (!Lambert.Solve(rc, rt, tA - tc, v.Body.Mu, h, out var v1, out var v2)) continue;
                    double vinf = (v2 - vt).magnitude;
                    double cost = (v1 - vc).magnitude + TitanVinfPenalty * Math.Max(0, vinf - TitanVinfMax);
                    if (cost < bestCost) { bestCost = cost; bestT = tA; }
                }
                if (double.IsNaN(bestT)) break;
                moon.LocalStateAt(bestT, out var rm, out var vm);
                var offset = Vector3d.zero;
                Vector3d dv = Vector3d.zero;
                bool ok = false;
                for (int k = 0; k < 3; k++)
                {
                    if (!Lambert.Solve(rc, rm + offset, bestT - tc, v.Body.Mu, h, out var v1, out var v2)) break;
                    ok = true;
                    dv = v1 - vc;
                    vinfBest = (v2 - vm).magnitude;
                    offset = aim(bestT, offset, v2 - vm, h);
                }
                if (!ok) break;
                var pro = vc.normalized;
                var rad = Vector3d.Cross(pro, h);
                plan = new TransferPlan
                {
                    Time = tc, ArrivalTime = bestT,
                    Prograde = Vector3d.Dot(dv, pro), Normal = Vector3d.Dot(dv, h), Radial = Vector3d.Dot(dv, rad),
                };
                double half = FlightControl.BurnTime(v, plan.DeltaV) / 2;
                if (double.IsInfinity(half) || Math.Abs(half + CorrectionTurn - lead) < 60) break;
                lead = half + CorrectionTurn;
            }
            if (plan == null)
            {
                foreach (var x in MoonCorrection(moon, "Наведение на " + moon.Name)) yield return x;
                yield break;
            }
            string what = "Наведение на " + moon.Name;
            Phase = what;
            u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
            u.Post($"{what}: {plan.DeltaV:F1} м/с, встреча {GameCalendar.Format(plan.ArrivalTime)}, v∞ {vinfBest:F0} м/с");
            foreach (var x in Burn(what)) yield return x;
        }

        /// <summary>Прицел входа: b = r·v·cos γ / v∞ на верхней границе атмосферы, с той стороны, с какой борт идёт.</summary>
        static TransferPlanner.AimPoint EntryAim(CelestialBody moon)
        {
            double re = moon.Radius + (moon.HasAtmosphere ? moon.AtmosphereTop : 0);
            return (ta, rel, vrel, h) =>
            {
                var uv = vrel.normalized;
                var side = rel - uv * Vector3d.Dot(rel, uv);
                if (side.sqrMagnitude < 1) side = Vector3d.Cross(uv, h);
                double vinf = Math.Max(vrel.magnitude, 1);
                double ve = Math.Sqrt(vinf * vinf + 2 * moon.Mu / re);
                return side.normalized * (re * ve * Math.Cos(TitanEntryAngle * Constants.Deg2Rad) / vinf);
            };
        }

        /// <summary>
        /// Подправка в сфере Титана: перицентр гиперболы — под угол входа TitanEntryAngle на верхней границе атмосферы
        /// (h = r·v·cos γ, rp = p / (1 + e)). Гипербола в сфере точная — промах конической стыковки снимается.
        /// </summary>
        IEnumerable<object> EntryTrim(CelestialBody moon)
        {
            var u = universe;
            var v = V;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, moon.Mu, T);
            if (orbit.IsElliptic) yield break;
            double re = moon.Radius + (moon.HasAtmosphere ? moon.AtmosphereTop : 0);
            double v2inf = 2 * orbit.Energy;
            double ve = Math.Sqrt(v2inf + 2 * moon.Mu / re);
            double hh = re * ve * Math.Cos(TitanEntryAngle * Constants.Deg2Rad);
            double p = hh * hh / moon.Mu;
            double e = Math.Sqrt(1 + v2inf * hh * hh / (moon.Mu * moon.Mu));
            double rpGoal = p / (1 + e);
            if (Math.Abs(orbit.PeriapsisRadius - rpGoal) < EntryTrimTolerance) yield break;
            var fix = TransferPlanner.PlanPerigee(moon, v.Position, v.Velocity, T, rpGoal, T + CorrectionTurn);
            if (fix == null || fix.DeltaV < MinCorrection) yield break;
            string what = $"Подправка входа в {moon.Name}";
            Phase = what;
            u.SetNode(fix.Time, fix.Prograde, fix.Normal, fix.Radial);
            u.Post($"Подправка входа в атмосферу {moon.Name}: перицентр {(orbit.PeriapsisRadius - moon.Radius) / 1e3:F0} → " +
                   $"{(rpGoal - moon.Radius) / 1e3:F0} км, {fix.DeltaV:F1} м/с");
            foreach (var x in Burn(what)) yield return x;
        }

        /// <summary>Допуск перицентра у Титана перед отделением, м: ±50 км ≈ ±1° угла входа.</summary>
        const double EntryTrimTolerance = 50e3;

        /// <summary>
        /// Спуск «Гюйгенса»: днищем к потоку (лидирует экран — FlightPhysics по направлению носа), основной купол по
        /// барометру, сброс экрана, через 15 мин — сброс крышки с основным куполом и тормозной купол до грунта.
        /// Ввод на 15 км, а не на 160 как у настоящего: выше FlightPhysics.ChuteAltitudeMax уставку не поднять.
        /// </summary>
        IEnumerable<object> TitanDescent(CelestialBody moon)
        {
            var u = universe;
            Phase = $"Перелёт к {moon.Name}";
            foreach (var x in Await(() => V.Body == moon, CoastText)) yield return x;
            var v = V;
            // Нос (+Y) — против скорости: впереди нижняя секция, экран 2,7 м (FlightPhysics: lead = BottomSection).
            v.Sas = SasMode.Retrograde;
            Phase = $"Вход в атмосферу {moon.Name}";
            foreach (var x in Await(() => AnyChuteDeployed(v) || v.IsLanded,
                         () => $"Вход: {v.Altitude / 1000:F0} км, {v.SurfaceSpeed:F0} м/с, {v.GForce:F1} g")) yield return x;
            v.Sas = SasMode.Off;
            if (v.IsLanded) yield break;
            double opened = T;
            Phase = "Спуск на основном куполе";
            foreach (var x in Await(() => T >= opened + ShieldDropDelay || v.IsLanded, () => $"Основной купол: {v.Altitude / 1000:F1} км")) yield return x;
            if (StageNextIs(v, StageActionType.Separate)) u.Stage(); // лобовой экран
            foreach (var x in Await(() => T >= opened + MainChuteTime || v.IsLanded,
                         () => $"Основной купол: {v.Altitude / 1000:F1} км, {-v.VerticalSpeed:F1} м/с")) yield return x;
            if (!v.IsLanded)
            {
                if (StageNextIs(v, StageActionType.Separate)) u.Stage(); // задняя крышка с основным куполом
                if (StageNextIs(v, StageActionType.DeployParachute)) u.Stage(); // тормозной купол 3,03 м
            }
            Phase = "Спуск на тормозном куполе";
            foreach (var x in Await(() => v.IsLanded,
                         () => $"Тормозной купол: {v.Altitude / 1000:F1} км, {-v.VerticalSpeed:F1} м/с")) yield return x;
        }

        /// <summary>Окно поиска встречи со спутником — один виток вокруг планеты (у гиперболы — год).</summary>
        double OneRevolution()
        {
            var o = KeplerOrbit.FromState(V.Position, V.Velocity, V.Body.Mu, T);
            return o.IsElliptic ? o.Period : 365.25 * 86400;
        }

        static bool AnyChuteDeployed(Vessel v)
        {
            for (int i = 0; i < v.Attached.Length; i++)
                if (v.Attached[i] && v.ChuteDeployed[i]) return true;
            return false;
        }

        static bool StageNextIs(Vessel v, StageActionType type) =>
            v.HasNextStage && v.NextStage < v.Design.Sequence.Count && v.Design.Sequence[v.NextStage].Type == type;
    }
}
