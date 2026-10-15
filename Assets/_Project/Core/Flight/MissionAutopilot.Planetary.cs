using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// «Вояджеры» (GDD §5.3, §6.11): тур с гравманёврами. План тура — TourPlanner от исторических дат; старт в окно,
    /// когда плоскость опорной орбиты содержит асимптоту ухода; разгон «Центавром» с добором твердотопливным модулем
    /// (NodeAutopilot сам отсекает «Центавр» на доле модуля); коррекции на трассе двигателями зонда — к точке прицела,
    /// которая поворачивает v∞ к следующей планете тура.
    /// </summary>
    public sealed partial class MissionAutopilot
    {
        /// <summary>Отлёт с опорной орбиты — не раньше чем через столько после выхода на неё (разворот «Центавра»).</summary>
        const double DepartLead = 900;
        /// <summary>
        /// Поздняя коррекция — за столько до пролёта: снаружи сферы влияния (Юпитер 48 млн км при v∞ 8 км/с — 70 сут,
        /// Нептун 87 млн км при 17 км/с — 60 сут). Пара: сфера / v∞ каждой планеты тура должна быть меньше.
        /// </summary>
        const double LateCorrection = 75 * 86400;
        /// <summary>Ранняя коррекция — через столько после выхода из сферы влияния: промах разгона/пролёта ещё дёшев.</summary>
        const double EarlyCorrection = 5 * 86400;
        /// <summary>
        /// Окно поиска сближения на перелёте тура — столько плановых перелётов (до tour.Times[i]), но не меньше
        /// ArrivalWindowMin. ClosestApproach берёт глобальный минимум окна: при 40 годах «Кассини» после манёвра в дальнем
        /// космосе находил сближение с Венерой виток спустя (5124 сут вместо 613) — поздняя коррекция и подправка пролёта
        /// пропадали, пролёт уходил мимо коридора (15.10.2026, от сдвига такта кадров окном разделения). Пара: опоздание
        /// против плана меньше (Factor − 1) перелёта, иначе настоящее сближение выпадет из окна.
        /// </summary>
        const double ArrivalWindowFactor = 1.5, ArrivalWindowMin = 60 * 86400;
        /// <summary>Коррекцию меньше этого не делаем, м/с: 0,1 м/с у Юпитера — сотни километров у перицентра.</summary>
        const double MinCorrection = 0.05;
        /// <summary>Запас перед серединой прожига зонда (тяга 3,6 Н на 825 кг: 10 м/с — 40 мин), с.</summary>
        const double CorrectionTurn = 1200;
        /// <summary>Пересчётов точки прицела по траектории после коррекции (от неё зависит v∞, а с ним прицел).</summary>
        const int AimPasses = 3;
        /// <summary>
        /// Запас наклонения над склонением асимптоты, градусы. Асимптота «Вояджера-2» лежит выше широты мыса (28,5°):
        /// плоскость пуска на восток её не содержит, окна нет, и разгон с поворотом плоскости вышел 7836 м/с вместо
        /// 7276 — «Центавру» с модулем не хватило 92 м/с, остаток пошёл на гидразин (3,6 Н), прожиг начался за
        /// 50 мин до узла, и зонд сгорел. Пара: наклонение = склонение + запас, азимут — из cos i = cos φ · sin A.
        /// </summary>
        const double DeclinationMargin = 0.5;
        /// <summary>
        /// Уточнений перицентра у последней планеты по коническим сечениям и допуск (доля коридора): ошибка
        /// картинной плоскости почти постоянна, сдвиг прицела снимает её за 1–2 шага.
        /// </summary>
        const int FinalAimPasses = 4;
        const double FinalAimTolerance = 0.05;

        IEnumerable<object> Planetary()
        {
            var u = universe;
            var def = Tracker.Def;
            var earth = u.System.Get("earth");
            var sun = earth.Parent;
            double park = earth.Radius + ParkingAltitude;

            // Тур — от отлёта примерно через полтора часа (выведение и полвитка); для окна старта точности хватает.
            var tour = MissionCatalog.PlanTour(def, u.System, T + AscentToTransfer, park);
            if (tour == null) throw new Abort("тур не найден");

            double azimuth = 90;
            Vector3d plane = default;
            if (V.IsLanded && V.Site != null)
            {
                double tL = double.NaN;
                foreach (double az in LaunchAzimuths(V.Body, V.Site, tour.VInfDepart, T))
                {
                    double tw = LaunchWindow.NextPlaneWindow(V.Body, V.Site, az, tour.VInfDepart, T + 60);
                    if (double.IsNaN(tw) || !double.IsNaN(tL) && tw >= tL) continue;
                    tL = tw;
                    azimuth = az;
                }
                if (!double.IsNaN(tL))
                {
                    // Вращение Земли сдвигает плоскость при азимуте ≠ 90° — наведение доворачивает к плоскости окна.
                    plane = LaunchWindow.PlaneNormal(V.Body, V.Site, azimuth, tL);
                    u.Post($"Окно старта к {tour.Bodies[0].Name} через {Clock(tL - T)}, азимут {azimuth:F1}°");
                    Phase = "Ожидание окна старта";
                    foreach (var x in WaitUntil(tL, () => $"Окно старта через {Clock(tL - T)}")) yield return x;
                }
            }
            if (V.Body == earth)
            {
                foreach (var x in Ascend(ParkingAltitude, null, azimuth, plane)) yield return x;

                // Тур заново — от настоящего момента отлёта (окно сдвинуло его на часы).
                var v = V;
                var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, earth.Mu, T);
                double dep = T + DepartLead + orbit.Period;
                tour = MissionCatalog.PlanTour(def, u.System, dep - orbit.Period / 2, orbit.A) ?? tour;
                var first = tour.Bodies[0];
                double b = FlybyTour.AimVector(first, tour.VIn[0], tour.VOut[0].sqrMagnitude > 0 ? tour.VOut[0] : tour.VIn[0],
                    FirstPeriapsis(tour, def)).magnitude;
                var plan = TransferPlanner.PlanDeparture(earth, v.Position, v.Velocity, T, tour.VInfDepart, dep, first, b,
                    tour.Times[0] - dep, TourAim(tour, 0));
                if (plan == null) throw new Abort($"отлёт к {first.Name} не найден");
                Phase = $"Разгон к {first.Name}";
                u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
                // Наведение пинка по асимптоте — к избытку импульсного плана (под него подобран промах у цели).
                orbit.GetState(plan.Time, out var rn, out var vn);
                var want = KeplerOrbit.FromState(rn, vn + V.Node.WorldDeltaV(orbit), earth.Mu, plan.Time).Excess;
                V.Node.TargetExcess = want;
                u.Post($"К {first.Name}: разгон через {Clock(plan.Time - T)}, Δv {plan.DeltaV:F0} м/с, C3 {tour.VInfDepart.sqrMagnitude / 1e6:F0} км²/с²");
                foreach (var x in Burn($"Разгон к {first.Name}")) yield return x;
                // Модуль выгорел — сброс, двигатели зонда (Separate igniteNext). Только с зажиганием следующего: у «Кассини»
                // следом идёт отделение «Гюйгенса» (без igniteNext) — его сбросило бы сразу после разгона.
                for (int n = 0; n < 4 && V.HasNextStage && V.Design.Sequence[V.NextStage].Type == StageActionType.Separate &&
                                V.Design.Sequence[V.NextStage].IgniteNext; n++)
                    u.Stage();
            }

            for (int i = 0; i < tour.Bodies.Length; i++)
            {
                var target = tour.Bodies[i];
                int leg = IndexOf(ObjectiveType.Flyby) + i;
                if (leg < Tracker.Done.Length && Tracker.Done[leg]) continue;
                Phase = $"Перелёт к {target.Name}";
                foreach (var x in Await(() => V.Body == sun, CoastText)) yield return x;

                bool dsmLeg = tour.DsmTime != null && !double.IsNaN(tour.DsmTime[i]);
                bool capture = i == tour.Bodies.Length - 1 && IndexOf(ObjectiveType.Orbit) >= 0;
                if (dsmLeg && tour.DsmTime[i] > T)
                {
                    // Манёвр в дальнем космосе (TourPlanner, «Кассини» 3.12.1998): он же и коррекция — Ламберт к прицелу
                    // у тела i из точки манёвра; середина долгого прожига — в плановый момент.
                    double half = FlightControl.BurnTime(V, tour.DsmDv[i].magnitude) / 2;
                    double start = tour.DsmTime[i] - (double.IsInfinity(half) ? 0 : half) - CorrectionTurn;
                    if (start > T)
                        foreach (var x in WaitUntil(start, () => $"Перелёт к {target.Name}: манёвр в дальнем космосе через {Clock(start - T)}"))
                            yield return x;
                    foreach (var x in DeepSpaceManeuver(tour, i)) yield return x;
                }
                else
                {
                    // Ранняя коррекция — когда выходной промах ещё дёшев; поздняя — снаружи сферы цели.
                    double early = T + EarlyCorrection;
                    foreach (var x in WaitUntil(early, () => $"Перелёт к {target.Name}: коррекция через {Clock(early - T)}")) yield return x;
                    foreach (var x in Correct(tour, i, "Коррекция")) yield return x;
                }
                double arrival = Arrival(target,
                    Math.Max(ArrivalWindowMin, ArrivalWindowFactor * (tour.Times[i] - T)));
                // У планеты захвата сфера больше: Сатурн 54,8 млн км при v∞ 5,4 км/с — 118 сут > LateCorrection.
                double lateLead = capture
                    ? Math.Max(LateCorrection, CaptureLateFactor * target.SoiRadius / Math.Max(1, tour.VIn[i].magnitude))
                    : LateCorrection;
                if (!double.IsNaN(arrival) && arrival - lateLead > T + EarlyCorrection)
                {
                    double late = arrival - lateLead;
                    foreach (var x in WaitUntil(late, () => $"Перелёт к {target.Name}: {Clock(Math.Max(0, arrival - T))}")) yield return x;
                    foreach (var x in Correct(tour, i, "Поздняя коррекция")) yield return x;
                }
                // Захват у последней планеты (орбита Сатурна) — своя фаза: торможение идёт поперёк пролёта.
                if (capture) break;
                Phase = $"Пролёт {target.Name}";
                int goal = leg;
                foreach (var x in Await(() => goal < Tracker.Done.Length && Tracker.Done[goal] || V.Body == target,
                             () => CoastText())) yield return x;
                // Перицентр по входу в сферу — для разбора промаха (без него «мимо коридора» не говорит, выше или ниже).
                if (V.Body == target && HasDsm(tour) && i + 1 < tour.Bodies.Length)
                    foreach (var x in FlybyTrim(tour, i)) yield return x;
                double peAlt = V.Body == target
                    ? KeplerOrbit.FromState(V.Position, V.Velocity, target.Mu, T).PeriapsisRadius - target.Radius
                    : double.NaN;
                foreach (var x in Await(() => goal >= Tracker.Done.Length || Tracker.Done[goal] || V.Body != target,
                             () => $"У {target.Name}: {V.Altitude / 1e6:F0} тыс. км")) yield return x;
                if (goal < Tracker.Done.Length && !Tracker.Done[goal])
                    throw new Abort($"пролёт {target.Name} мимо коридора (перицентр {peAlt / 1e3:F0} км)");
            }
            if (IndexOf(ObjectiveType.Orbit) >= 0)
                foreach (var x in SaturnTitan()) yield return x;
        }

        /// <summary>
        /// Цель пролёта тела i тура — по порядку задач, а не по имени тела: у «Кассини» Венера дважды, и Find по телу
        /// отдавал второму пролёту коридор первого.
        /// </summary>
        Objective TourLeg(int i, CelestialBody body)
        {
            var list = Tracker.Def.Objectives;
            int k = IndexOf(ObjectiveType.Flyby) + i;
            if (k >= 0 && k < list.Count && list[k].Type == ObjectiveType.Flyby && list[k].Body == body.Id) return list[k];
            return list.Find(x => x.Type == ObjectiveType.Flyby && x.Body == body.Id);
        }

        /// <summary>В туре есть манёвр в дальнем космосе (TourPlanner) — включает подправки пролётов в сфере (FlybyTrim).</summary>
        static bool HasDsm(FlybyTour tour)
        {
            if (tour.DsmTime == null) return false;
            foreach (double t in tour.DsmTime) if (!double.IsNaN(t)) return true;
            return false;
        }

        /// <summary>
        /// Подправка пролёта на входе в сферу тела i: асимптота ухода — на плановую (VOut тура перед манёвром в
        /// дальнем космосе, иначе Ламберт к следующему телу на его плановую дату). Коническая стыковка по Солнцу
        /// ошибается в перицентре на сотни км (Венера 840 и 370 км, Земля 370 км у «Кассини», 15.10.2026) — после
        /// пролёта это 200–300 м/с, а здесь, за сутки до перицентра, единицы м/с. Только для тура с манёвром в
        /// дальнем космосе: «Вояджеры» летают без неё, их прицел уже выверен.
        /// </summary>
        IEnumerable<object> FlybyTrim(FlybyTour tour, int i)
        {
            var u = universe;
            var v = V;
            var body = tour.Bodies[i];
            var o = TourLeg(i, body);
            double floor = body.Radius + (o?.Min ?? 0), top = body.Radius + (o?.Max ?? double.PositiveInfinity);
            var now = KeplerOrbit.FromState(v.Position, v.Velocity, body.Mu, T);
            if (now.IsElliptic) yield break;
            double tPe = T + now.TimeToPeriapsis(T);
            if (tPe - T < 2 * CorrectionTurn) yield break;
            Vector3d want;
            if (DsmAfter(tour, i)) want = tour.VOut[i];
            else
            {
                // Пролёт без двигателя сохраняет |v∞| — дата у следующего тела «плавает» (AimedExit). На плановую
                // дату с чужим модулем подправка у Земли увела к Юпитеру с промахом на 519 м/с (auto_cassini, 15.10.2026).
                var vinf = Math.Sqrt(Math.Max(0, now.Energy * 2));
                if (!(AimedExit(tour, i, tPe, vinf) is Vector3d found)) yield break;
                want = found;
            }
            var s = want.normalized;
            var e1 = Vector3d.Cross(s, Math.Abs(s.z) < 0.9 ? new Vector3d(0, 0, 1) : new Vector3d(1, 0, 0)).normalized;
            var e2 = Vector3d.Cross(s, e1);
            double tc = T + CorrectionTurn;
            now.GetState(tc, out var rc, out var vc);
            // Невязка — проекции асимптоты ухода на оси поперёк нужной; Гаусс–Ньютон с наименьшей нормой Δv.
            Func<Vector3d, (double, double, double)> f = d =>
            {
                var k = KeplerOrbit.FromState(rc, vc + d, body.Mu, tc);
                var x = k.Excess.normalized;
                return (Vector3d.Dot(x, e1), Vector3d.Dot(x, e2), k.PeriapsisRadius);
            };
            var dv = Vector3d.zero;
            for (int it = 0; it < 6; it++)
            {
                var (a0, b0, _) = f(dv);
                if (Math.Abs(a0) + Math.Abs(b0) < 1e-6) break;
                const double step = 0.05;
                var axes = new[] { new Vector3d(step, 0, 0), new Vector3d(0, step, 0), new Vector3d(0, 0, step) };
                var ja = new double[3];
                var jb = new double[3];
                for (int c = 0; c < 3; c++)
                {
                    var (a1, b1, _) = f(dv + axes[c]);
                    ja[c] = (a1 - a0) / step;
                    jb[c] = (b1 - b0) / step;
                }
                // dv = Jᵀ (J Jᵀ)⁻¹ (−r): у 2×3 матрицы JJᵀ — 2×2.
                double m11 = ja[0] * ja[0] + ja[1] * ja[1] + ja[2] * ja[2];
                double m12 = ja[0] * jb[0] + ja[1] * jb[1] + ja[2] * jb[2];
                double m22 = jb[0] * jb[0] + jb[1] * jb[1] + jb[2] * jb[2];
                double det = m11 * m22 - m12 * m12;
                if (Math.Abs(det) < 1e-30) yield break;
                double y1 = (-a0 * m22 + b0 * m12) / det, y2 = (-b0 * m11 + a0 * m12) / det;
                dv += new Vector3d(ja[0] * y1 + jb[0] * y2, ja[1] * y1 + jb[1] * y2, ja[2] * y1 + jb[2] * y2);
            }
            var (ra, rb2, rp) = f(dv);
            if (Math.Abs(ra) + Math.Abs(rb2) > 1e-4 || rp < floor || rp > top || dv.magnitude > FlybyTrimMax)
            {
                u.Post($"Подправка пролёта {body.Name} не нужна или вне коридора: перицентр {(rp - body.Radius) / 1e3:F0} км, {dv.magnitude:F1} м/с");
                yield break;
            }
            if (dv.magnitude < MinCorrection) yield break;
            var pro = vc.normalized;
            var h = Vector3d.Cross(rc, vc).normalized;
            var rad = Vector3d.Cross(pro, h);
            Phase = $"Подправка пролёта {body.Name}";
            u.SetNode(tc, Vector3d.Dot(dv, pro), Vector3d.Dot(dv, h), Vector3d.Dot(dv, rad));
            u.Post($"Подправка пролёта {body.Name}: {dv.magnitude:F1} м/с, перицентр {(now.PeriapsisRadius - body.Radius) / 1e3:F0} → {(rp - body.Radius) / 1e3:F0} км");
            foreach (var x in Burn($"Подправка пролёта {body.Name}")) yield return x;
        }

        /// <summary>
        /// Выход из пролёта тела i к точке прицела у тела i+1 без двигателя: |v∞| сохраняется, дата у i+1 — та, где
        /// Ламберт даёт этот модуль (ближайшая к плановой). Модуль — на границе сферы (√(v∞² + 2μ/R_SOI)): дальше
        /// конические сечения притяжение планеты уже не считают — у Юпитера это +300 м/с к v∞ (8589 → 8890).
        /// Точка прицела у i+1 — из TourAim (у Юпитера она в 10 млн км от центра — мимо неё коррекция стоила 365 м/с).
        /// </summary>
        /// <remarks>
        /// Добавка 2μ/R_SOI нужна и к относительной скорости солнечного Ламберта: без неё у Юпитера прогноз выхода
        /// 7967 м/с против фактических 8290, Сатурн уходил на 2006 г. с коррекцией 1791 м/с (auto_cassini, 15.10.2026).
        /// </remarks>
        Vector3d? AimedExit(FlybyTour tour, int i, double ta, double vinf)
        {
            var body = tour.Bodies[i];
            var next = tour.Bodies[i + 1];
            double veff = Math.Sqrt(vinf * vinf + 2 * body.Mu / body.SoiRadius);
            var nextAim = TourAim(tour, i + 1);
            var offset = Vector3d.zero;
            Vector3d? vout = null;
            for (int k = 0; k < 3; k++)
            {
                if (!FloatingExit(body, ta, veff, next, tour.Times[i + 1], offset, out var vo, out var varr, out double tArr)) return vout;
                vout = vo;
                body.LocalStateAt(ta, out var rb, out var vb);
                var h = Vector3d.Cross(rb, vb + vo).normalized;
                offset = nextAim(tArr, offset, varr, h);
            }
            return vout;
        }

        /// <summary>Ламберт от тела в момент ta к точке у next с |v∞ ухода| = vinf; дата — корень, ближайший к плановой.</summary>
        static bool FloatingExit(CelestialBody body, double ta, double vinf, CelestialBody next, double tPlanned, Vector3d offset,
            out Vector3d vout, out Vector3d varr, out double tArr)
        {
            vout = varr = Vector3d.zero;
            tArr = double.NaN;
            double tof0 = tPlanned - ta;
            if (tof0 <= 0) return false;
            body.LocalStateAt(ta, out var rb, out var vb);
            double mu = body.Parent.Mu;
            Func<double, double> g = tof =>
            {
                next.LocalStateAt(ta + tof, out var rn, out _);
                return Lambert.Solve(rb, rn + offset, tof, mu, new Vector3d(0, 0, 1), out var v1, out _) ? (v1 - vb).magnitude - vinf : double.NaN;
            };
            double best = double.NaN;
            const int scan = 40;
            const double lo = 0.6, span = 1.0; // окно дат: 0,6–1,6 плановой длительности перелёта
            double ga = g(tof0 * lo);
            for (int k = 0; k < scan; k++)
            {
                double a = tof0 * (lo + span * k / scan), b = tof0 * (lo + span * (k + 1) / scan);
                double gb = g(b), gaKeep = ga;
                ga = gb;
                if (double.IsNaN(gaKeep) || double.IsNaN(gb) || Math.Sign(gaKeep) == Math.Sign(gb)) continue;
                double fa = gaKeep;
                for (int n = 0; n < 40; n++)
                {
                    double m = 0.5 * (a + b), gm = g(m);
                    if (double.IsNaN(gm)) break;
                    if (Math.Sign(gm) == Math.Sign(fa)) { a = m; fa = gm; } else b = m;
                }
                double root = 0.5 * (a + b);
                if (double.IsNaN(best) || Math.Abs(root - tof0) < Math.Abs(best - tof0)) best = root;
            }
            if (double.IsNaN(best)) return false;
            next.LocalStateAt(ta + best, out var rN, out var vN);
            if (!Lambert.Solve(rb, rN + offset, best, mu, new Vector3d(0, 0, 1), out var w1, out var w2)) return false;
            vout = w1 - vb;
            varr = w2 - vN;
            tArr = ta + best;
            return true;
        }

        /// <summary>Потолок подправки пролёта, м/с: дороже — значит прицел не тот, лучше коррекция после пролёта.</summary>
        const double FlybyTrimMax = 60;

        /// <summary>За пролётом тела i — манёвр в дальнем космосе (TourPlanner): прицел у i — на плановый выход v∞.</summary>
        static bool DsmAfter(FlybyTour tour, int i) =>
            tour.DsmTime != null && i + 1 < tour.Bodies.Length && !double.IsNaN(tour.DsmTime[i + 1]);

        /// <summary>
        /// Азимуты старта, плоскость которых может содержать асимптоту ухода: 90° (вся скорость вращения Земли), если
        /// склонение асимптоты не выше широты площадки; иначе — пара азимутов на наклонение «склонение + запас»
        /// (на северо-восток и на юго-восток), из них берётся то, чьё окно раньше.
        /// </summary>
        static double[] LaunchAzimuths(CelestialBody body, LaunchSite site, Vector3d asymptote, double t)
        {
            var pole = body.OrientationAt(t) * Vector3d.forward;
            double decl = Math.Asin(MathD.Clamp(Vector3d.Dot(asymptote.normalized, pole), -1, 1)) / Constants.Deg2Rad;
            double lat = Math.Abs(site.Latitude);
            double incl = Math.Abs(decl) + DeclinationMargin;
            if (incl <= lat || incl >= 90) return new double[] { 90 };
            double sinA = Math.Cos(incl * Constants.Deg2Rad) / Math.Cos(lat * Constants.Deg2Rad);
            double a = Math.Asin(MathD.Clamp(sinA, -1, 1)) / Constants.Deg2Rad;
            return new[] { a, 180 - a };
        }

        /// <summary>Перицентр у первой планеты по туру, но не ниже нижней границы её коридора.</summary>
        static double FirstPeriapsis(FlybyTour tour, MissionDef def)
        {
            var o = def.Objectives.Find(x => x.Type == ObjectiveType.Flyby && x.Body == tour.Bodies[0].Id);
            double floor = tour.Bodies[0].Radius + (o?.Min ?? 0);
            double rp = tour.Periapsis[0];
            return double.IsNaN(rp) ? floor + (o != null ? 0.5 * (o.Max - o.Min) : 0) : Math.Max(rp, floor);
        }

        /// <summary>Сближение с целью по текущей траектории вокруг Солнца (без её притяжения), NaN — не сходимся.</summary>
        /// <param name="window">Окно поиска, с; по умолчанию 40 лет. У спутника — один виток: иначе ClosestApproach
        /// находит более тесное сближение через десяток витков («Кассини» ждал его и вошёл в Титан с «Гюйгенсом»).</param>
        double Arrival(CelestialBody target, double window = 40 * 365.25 * 86400)
        {
            var v = V;
            if (target.Parent != v.Body) return double.NaN;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, T);
            TransferPlanner.ClosestApproach(orbit, target, T, window, out double ta);
            return ta;
        }

        /// <summary>
        /// Точка прицела у планеты i тура: у промежуточной — поворот v∞ к следующей планете на её плановую дату
        /// (Ламберт от планеты в момент сближения), перицентр — из угла поворота, не ниже коридора; у последней —
        /// середина коридора высот с той стороны, с какой зонд идёт сейчас.
        /// </summary>
        /// <param name="rpLast">Перицентр прицела у последней планеты вместо середины коридора (поправка под
        /// конические сечения — Correct); NaN — середина.</param>
        TransferPlanner.AimPoint TourAim(FlybyTour tour, int i, double rpLast = double.NaN)
        {
            var body = tour.Bodies[i];
            var o = TourLeg(i, body);
            double floor = body.Radius + (o?.Min ?? 0), top = body.Radius + (o?.Max ?? 0);
            if (DsmAfter(tour, i))
            {
                // Следующее тело — после манёвра в дальнем космосе: Ламберт прямо к нему не нужен, выход — плановый v∞
                // тура (его подобрал TourPlanner вместе с манёвром), перицентр — из угла поворота к нему.
                var vout = tour.VOut[i];
                return (ta, rel, vrel, h) =>
                {
                    double rp = double.IsNaN(rpLast)
                        ? MathD.Clamp(FlybyTour.TurnPeriapsis(body, vrel.magnitude, Vector3d.Angle(vrel, vout)), floor, top)
                        : rpLast;
                    return FlybyTour.AimVector(body, vrel, vout, rp);
                };
            }
            if (i == tour.Bodies.Length - 1)
            {
                // Планета захвата: перицентр — с той стороны, где орбита ляжет ближе всего к экватору (там Титан):
                // нормаль h ∥ b × v∞, ближайшая к полюсу допустимая — проекция полюса на плоскость поперёк v∞.
                bool equatorial = IndexOf(ObjectiveType.Orbit) >= 0;
                return (ta, rel, vrel, h) =>
                {
                    var uv = vrel.normalized;
                    var side = rel - uv * Vector3d.Dot(rel, uv);
                    if (equatorial)
                    {
                        var pole = body.PoleAt(ta);
                        var hh = pole - uv * Vector3d.Dot(pole, uv);
                        if (hh.sqrMagnitude > 1e-6) side = Vector3d.Cross(uv, hh.normalized);
                    }
                    if (side.sqrMagnitude < 1e-6) side = Vector3d.Cross(uv, h);
                    double rp = double.IsNaN(rpLast) ? 0.5 * (floor + top) : rpLast, v2 = Math.Max(vrel.sqrMagnitude, 1);
                    // Кольца проверяем по перицентру ЦЕЛИ (середина коридора), а не прицела: rpLast — лишь поправка на
                    // ошибку конических сечений (у Сатурна 58 тыс. км при цели 80), по нему находился «узел внутри
                    // кольца C», а реальный перицентр 79 тыс. клал узел на 86 тыс. — в кольцо C (auto_cassini, 15.10.2026).
                    double rpRing = 0.5 * (floor + top);
                    // Захват — эллипс с тем же перицентром: его узлы ближе гиперболических, проверяем по нему.
                    double e = equatorial
                        ? (CaptureApoapsis - rpRing) / (CaptureApoapsis + rpRing)
                        : 1 + rpRing * v2 / body.Mu;
                    side = RingSafeSide(body, body.PoleAt(ta), uv, side.normalized, rpRing, v2, e);
                    return side * (rp * Math.Sqrt(1 + 2 * body.Mu / (rp * v2)));
                };
            }
            var next = tour.Bodies[i + 1];
            double tn = tour.Times[i + 1];
            bool floating = HasDsm(tour);
            return (ta, rel, vrel, h) =>
            {
                // Тур с манёвром в дальнем космосе («Кассини»): дата у следующего тела плавает под |v∞| (AimedExit) —
                // иначе опоздание к Юпитеру на 2 мес. требовало от пролёта невозможного поворота к Сатурну.
                if (floating && AimedExit(tour, i, ta, vrel.magnitude) is Vector3d fv)
                {
                    double rpf = MathD.Clamp(FlybyTour.TurnPeriapsis(body, vrel.magnitude, Vector3d.Angle(vrel, fv)), floor, top);
                    return FlybyTour.AimVector(body, vrel, fv, rpf);
                }
                body.LocalStateAt(ta, out var rb, out var vb);
                next.LocalStateAt(tn, out var rn, out _);
                if (!Lambert.Solve(rb, rn, tn - ta, body.Parent.Mu, new Vector3d(0, 0, 1), out var v1, out _))
                    return FlybyTour.AimVector(body, vrel, vrel, floor);
                var vout = v1 - vb;
                double rp = FlybyTour.TurnPeriapsis(body, vrel.magnitude, Vector3d.Angle(vrel, vout));
                rp = MathD.Clamp(rp, floor, top);
                return FlybyTour.AimVector(body, vrel, vout, rp);
            };
        }

        /// <summary>
        /// Запас узла орбиты от края плотных колец, м. Пара: при перицентре 80 тыс. км и захвате до 9 млн км узел
        /// при ω = 90° — на p ≈ 159 тыс. км, край F — 140 тыс.; запас 5 тыс. км оставляет ω ± 5,6°.
        /// </summary>
        const double RingNodeMargin = 5e6;

        /// <summary>
        /// Сторона прицела (единичная, поперёк v∞) у планеты с плотными кольцами: плоскость подлёта поворачивается
        /// вокруг v∞ от предпочтительной side0 ровно настолько, чтобы оба узла (пересечения экватора) легли вне колец.
        /// При наименьшем наклонении узел приходился в ≈ 70° от перицентра — на 115 тыс. км, в кольцо B: «Кассини» гиб
        /// на подлёте (auto_cassini, 15.10.2026). Настоящий подходил с перицентром над кольцами (ω ≈ 90°), плоскость
        /// пересекал в щели F–G. Граница допустимого угла уточняется делением пополам — прицел непрерывен по v∞,
        /// иначе коррекции (итерации по прицелу) прыгали бы на шаг перебора.
        /// </summary>
        /// <param name="e">Эксцентриситет проверяемой орбиты с перицентром rp (захватный эллипс или сама гипербола).</param>
        static Vector3d RingSafeSide(CelestialBody body, Vector3d pole, Vector3d uv, Vector3d side0, double rp, double v2, double e)
        {
            if (!Rings.DenseSpan(body, out double inner, out double outer)) return side0;
            var ortho = Vector3d.Cross(uv, side0);
            double eh = 1 + rp * v2 / body.Mu; // эксцентриситет гиперболы — для направления на перицентр
            double cosInf = 1 / eh, sinInf = Math.Sqrt(Math.Max(0, 1 - cosInf * cosInf));
            Vector3d Side(double phi) => side0 * Math.Cos(phi) + ortho * Math.Sin(phi);
            bool Clear(double phi)
            {
                var s = Side(phi);
                var hh = Vector3d.Cross(s, uv); // нормаль орбиты: b × v∞
                var node = Vector3d.Cross(pole, hh);
                if (node.sqrMagnitude < 1e-6) return false; // плоскость колец
                node = node.normalized;
                var peri = uv * cosInf + s * sinInf; // перицентр гиперболы: от −v∞ на угол асимптоты
                double c = Vector3d.Dot(peri, node);
                return NodeClear(c) && NodeClear(-c);
            }
            bool NodeClear(double cosNu)
            {
                double den = 1 + e * cosNu;
                if (den <= 1e-9) return true; // ветвь гиперболы до узла не доходит
                double r = rp * (1 + e) / den;
                return r > outer + RingNodeMargin || r < inner - RingNodeMargin;
            }
            if (Clear(0)) return side0;
            const double step = Math.PI / 180;
            // Граница a ↔ b (Clear(a) ≠ Clear(b)) — делением пополам, к допустимой стороне.
            double Edge(double bad, double good)
            {
                for (int it = 0; it < 30; it++)
                {
                    double mid = 0.5 * (bad + good);
                    if (Clear(mid)) good = mid; else bad = mid;
                }
                return good;
            }
            for (int k = 1; k <= 180; k++)
                for (int sgn = 1; sgn >= -1; sgn -= 2)
                {
                    double hi = sgn * k * step;
                    if (!Clear(hi)) continue;
                    // Окно допустимых плоскостей узкое (ω = 90° ± 5,7°), а ошибка сшивки конических сечений — градусы:
                    // с прицелом на самый край окна узел ложился в кольцо A. Целимся в середину окна.
                    double near = Edge(sgn * (k - 1) * step, hi);
                    int j = k;
                    while (j < 180 && Clear(sgn * (j + 1) * step)) j++;
                    double far = j < 180 ? Edge(sgn * (j + 1) * step, sgn * j * step) : sgn * Math.PI;
                    double phi = 0.5 * (near + far);
                    return Side(phi);
                }
            return side0; // безопасной плоскости нет (v∞ в самой плоскости колец) — как было
        }

        /// <summary>
        /// Манёвр в дальнем космосе к телу i: Ламберт к точке прицела на ПЛАНОВУЮ дату тура, затем уточнение сквозь
        /// пролёт (PlanThroughFlyby). Correct выбирал дату прибытия по наименьшему Δv — «Кассини» пришёл ко второй
        /// Венере на 15 сут позже, к Земле на 20, и коррекция к ней вышла 1503 м/с (auto_cassini, 15.10.2026).
        /// </summary>
        IEnumerable<object> DeepSpaceManeuver(FlybyTour tour, int i)
        {
            var u = universe;
            var v = V;
            var target = tour.Bodies[i];
            double tA = tour.Times[i];
            var aim = TourAim(tour, i);
            double half = FlightControl.BurnTime(v, tour.DsmDv[i].magnitude) / 2;
            double lead = CorrectionTurn + (double.IsInfinity(half) ? 0 : half);
            TransferPlan plan = null;
            for (int pass = 0; pass < 3; pass++)
            {
                var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, T);
                double tc = T + lead;
                orbit.GetState(tc, out var rc, out var vc);
                var h = Vector3d.Cross(rc, vc).normalized;
                target.LocalStateAt(tA, out var rt, out var vt);
                var offset = Vector3d.zero;
                Vector3d dv = Vector3d.zero;
                bool ok = false;
                // Прицел зависит от v∞ прибытия, а она — от прицела: два-три пересчёта сходятся.
                for (int k = 0; k < 3; k++)
                {
                    if (!Lambert.Solve(rc, rt + offset, tA - tc, v.Body.Mu, h, out var v1, out var v2)) break;
                    dv = v1 - vc;
                    ok = true;
                    offset = aim(tA, offset, v2 - vt, h);
                }
                if (!ok) break;
                var pro = vc.normalized;
                var rad = Vector3d.Cross(pro, h);
                plan = new TransferPlan
                {
                    Time = tc, ArrivalTime = tA,
                    Prograde = Vector3d.Dot(dv, pro), Normal = Vector3d.Dot(dv, h), Radial = Vector3d.Dot(dv, rad),
                };
                if (i + 1 < tour.Bodies.Length && !DsmAfter(tour, i))
                {
                    var o = TourLeg(i, target);
                    double floor = target.Radius + (o?.Min ?? 0), top = target.Radius + (o?.Max ?? 0);
                    var tp = TransferPlanner.PlanThroughFlyby(v.Body, v.Position, v.Velocity, T, target, tour.Bodies[i + 1],
                        tour.Times[i + 1], TourAim(tour, i + 1), lead, plan, floor, top);
                    if (tp != null) plan = tp;
                }
                double h2 = FlightControl.BurnTime(v, plan.DeltaV) / 2;
                if (double.IsInfinity(h2) || Math.Abs(h2 + CorrectionTurn - lead) < 60) break;
                lead = Math.Max(CorrectionTurn, h2 + CorrectionTurn);
            }
            if (plan == null)
            {
                // Ламберт не сошёлся — обычная коррекция лучше, чем ничего.
                foreach (var x in Correct(tour, i, "Манёвр в дальнем космосе")) yield return x;
                yield break;
            }
            Phase = "Манёвр в дальнем космосе";
            u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
            u.Post($"Манёвр в дальнем космосе к {target.Name}: {plan.DeltaV:F1} м/с (план тура {tour.DsmDv[i].magnitude:F0}), " +
                   $"пролёт {GameCalendar.Format(tA)}");
            foreach (var x in Burn($"Манёвр в дальнем космосе к {target.Name}")) yield return x;
        }

        /// <summary>Коррекция к планете i тура двигателями зонда; lead — чтобы середина долгого прожига успела.</summary>
        IEnumerable<object> Correct(FlybyTour tour, int i, string what)
        {
            var u = universe;
            var target = tour.Bodies[i];
            var aim = TourAim(tour, i);
            var v = V;
            // Сквозь гравманёвр — к прицелу у следующей планеты; коридор высот — из цели пролёта, как в TourAim.
            // Перед манёвром в дальнем космосе сквозного прицела нет: следующее тело достаёт сам манёвр.
            bool dsmNext = DsmAfter(tour, i);
            bool through = i + 1 < tour.Bodies.Length && !dsmNext;
            var o = TourLeg(i, target);
            double floor = target.Radius + (o?.Min ?? 0), top = target.Radius + (o?.Max ?? 0);
            var nextAim = through ? TourAim(tour, i + 1) : null;
            double lead = CorrectionTurn;
            TransferPlan plan = null, thru = null;
            double rpGoal = dsmNext && !double.IsNaN(tour.Periapsis[i]) ? MathD.Clamp(tour.Periapsis[i], floor, top) : 0.5 * (floor + top);
            double rpAim = rpGoal;
            for (int pass = 0; pass < 3; pass++)
            {
                plan = TransferPlanner.PlanCorrection(v.Body, v.Position, v.Velocity, T, target, aim, lead, AimPasses);
                if (plan == null) break;
                // У последней планеты сквозного прицела нет: картинная плоскость ошибается на тысячи км перицентра
                // (у Нептуна 12,7 тыс. при коридоре 17 тыс.) — сдвигаем перицентр прицела на ошибку прогноза.
                for (int k = 0; !through && k < FinalAimPasses; k++)
                {
                    double rp = TransferPlanner.PeriapsisAfter(v.Body, v.Position, v.Velocity, T, plan, target);
                    if (double.IsNaN(rp) || Math.Abs(rp - rpGoal) < FinalAimTolerance * (top - floor)) break;
                    rpAim = Math.Max(target.Radius, rpAim + rpGoal - rp);
                    var p2 = TransferPlanner.PlanCorrection(v.Body, v.Position, v.Velocity, T, target,
                        TourAim(tour, i, rpAim), lead, AimPasses);
                    if (p2 == null) break;
                    plan = p2;
                }
                if (through)
                {
                    // Следующий проход (длиннее lead под долгий прожиг) стартует от прошлого сквозного решения; не сошёлся —
                    // остаётся прошлое: момент чуть не тот, но прицел у следующей планеты лучше картинной плоскости.
                    var tp = TransferPlanner.PlanThroughFlyby(v.Body, v.Position, v.Velocity, T, target, tour.Bodies[i + 1],
                        tour.Times[i + 1], nextAim, lead, plan, floor, top, thru);
                    if (tp != null) thru = tp;
                    plan = thru ?? plan;
                }
                double half = FlightControl.BurnTime(v, plan.DeltaV) / 2;
                if (double.IsInfinity(half) || half + CorrectionTurn <= lead) break;
                lead = half + CorrectionTurn;
            }
            if (plan == null || plan.DeltaV < MinCorrection) yield break;
            Phase = what;
            u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
            u.Post($"{what} к {target.Name}: {plan.DeltaV:F2} м/с, пролёт {GameCalendar.Format(plan.ArrivalTime)}");
            foreach (var x in Burn($"{what} к {target.Name}")) yield return x;
        }
    }
}
