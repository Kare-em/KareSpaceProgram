using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Крылатые миссии (GDD §6.4, §7.3): STS-1 и «Буран» — выведение по азимуту наклонения, сброс бака, виток, тормозной
    /// импульс в момент, когда трасса входа проходит над полосой, вход с α 40° и управлением дальностью креном
    /// (равновесное планирование, как у «Шаттла»), заход по конусу выравнивания курса (HAC), глиссада 20°, выравнивание,
    /// касание на шасси и пробег. Импульсов и прожигов сам не считает — узел ставит NodeAutopilot, как у капсул.
    /// </summary>
    public sealed partial class MissionAutopilot
    {
        /// <summary>
        /// Вакуумный перигей после тормозного импульса, м: с 240–250 км это ≈ 70–80 м/с и угол входа ≈ −1,1° на 120 км (у «Шаттла» −1,2°).
        /// Выше — вход положе и дольше (риск рикошета при α 40° и крене 0), ниже — круче и горячее.
        /// Пара: MissionCatalog.WingedOrbitOf (высота орбиты) и WingedRangeFromEI (дальность от входа до полосы).
        /// </summary>
        const double WingedDeorbitPerigee = 10e3;
        /// <summary>Ниже этой высоты, м, крылатому — только реальное время (заход, глиссада, выравнивание).</summary>
        const double WingedNoWarpAltitude = 40e3;
        /// <summary>Граница входа в атмосферу (EI), м — 400 тыс. футов, как у NASA.</summary>
        const double WingedEntryInterface = 120e3;
        /// <summary>
        /// Дальность от EI до полосы по дуге, м, на которую целится тормозной импульс. Середина диапазона управления креном
        /// (cos σ ≈ 0,5 при L/D ≈ 1): у «Шаттла» вход начинали в ~8000 км от полосы.
        /// </summary>
        const double WingedRangeFromEI = 7000e3;
        /// <summary>Боковая дальность, м, которую вход берёт без натяжки; у «Шаттла» до ~1500 км, здесь с запасом.</summary>
        const double WingedCrossMax = 700e3;
        /// <summary>Поиск момента схода: шаг, с, и окно вперёд, с (полтора суток — «Буран» садился на 3-м витке, STS-1 на 37-м).</summary>
        const double WingedDeorbitStep = 10, WingedDeorbitWindow = 36 * 3600;
        /// <summary>Не раньше этого до импульса, с: разворот на торможение. Пара: TurnMargin.</summary>
        const double WingedDeorbitLead = 600;
        /// <summary>За сколько до EI включить физику и встать на α 40°, с.</summary>
        const double WingedEntryPrep = 300;

        /// <summary>Азимут старта на наклонение incDeg с широты latDeg, ° от севера, с поправкой на вращение Земли (v орбиты ≈ 7,8 км/с).</summary>
        static double LaunchAzimuth(CelestialBody body, double latDeg, double incDeg, double orbitSpeed)
        {
            double lat = latDeg * Constants.Deg2Rad, inc = incDeg * Constants.Deg2Rad;
            double sinA = MathD.Clamp(Math.Cos(inc) / Math.Max(1e-6, Math.Cos(lat)), -1, 1);
            double cosA = Math.Sqrt(1 - sinA * sinA);
            double vRot = body.RotationRate * body.Radius * Math.Cos(lat);
            return Math.Atan2(orbitSpeed * sinA - vRot, orbitSpeed * cosA) * Constants.Rad2Deg;
        }

        IEnumerable<object> Winged()
        {
            var u = universe;
            var def = Tracker.Def;
            int iRunway = IndexOf(ObjectiveType.Runway), iOrbit = IndexOf(ObjectiveType.Orbit);
            var runway = Runways.Get(def.Objectives[iRunway].Site);
            if (runway == null || runway.Site == null) throw new Abort("полоса посадки не найдена");
            var earth = V.Body;
            bool descending = !V.IsLanded && (iOrbit < 0 || Tracker.Done[iOrbit]) &&
                              Perigee(V) < earth.Radius + earth.AtmosphereTop;
            if (!descending)
            {
                if (V.IsLanded || !OnOrbit(V))
                {
                    var orbit = MissionCatalog.WingedOrbitOf(def.Id);
                    double alt = orbit?.Altitude ?? ParkingAltitude;
                    Phase = "Выведение";
                    u.Ascent = new AscentAutopilot { TargetAltitude = alt };
                    if (orbit != null && V.Site != null)
                    {
                        double vOrb = Math.Sqrt(earth.Mu / (earth.Radius + alt));
                        u.Ascent.Azimuth = LaunchAzimuth(earth, V.Site.Latitude, orbit.Inclination, vOrb);
                    }
                    foreach (var x in Await(() => u.Ascent == null, () => "Выведение: " + (u.Ascent?.Status ?? ""))) yield return x;
                    if (V.IsLanded || !OnOrbit(V)) throw new Abort("орбита не получена");
                }
                // Бак/блок Ц, если довыводить не понадобилось, ещё на борту: сбросить — дальше только OMS/ОДУ.
                // Без yield: кадр без WarpLimit между выведением и витком автоускорение проскакивает на ×10⁷ (≈ 277 ч за кадр).
                if (V.HasNextStage && V.Design.Sequence[V.NextStage].Type == StageActionType.Separate) u.Stage();
                foreach (var x in HoldOrbit(iOrbit)) yield return x;

                Phase = "Сход с орбиты";
                double tb = PlanWingedDeorbit(runway, out double dv, out double cross);
                if (double.IsNaN(tb)) throw new Abort("трасса не проходит над полосой в ближайшие сутки");
                u.Post($"Сход с орбиты через {Clock(tb - T)}: {dv:F0} м/с, боковая дальность {cross / 1000:F0} км");
                u.SetNode(tb, -dv, 0, 0);
                foreach (var x in Burn("Тормозной импульс")) yield return x;
                V.Throttle = 0;
                var o = KeplerOrbit.FromState(V.Position, V.Velocity, earth.Mu, T);
                double tEI = o.NextTimeAtRadius(earth.Radius + WingedEntryInterface, T, false);
                if (!double.IsNaN(tEI) && tEI - WingedEntryPrep > T)
                    foreach (var x in WaitUntil(tEI - WingedEntryPrep, () => $"До входа в атмосферу {Clock(tEI - T)}")) yield return x;
            }

            var guide = new WingedGuidance(this, runway);
            step = guide.Step;
            NeedsPhysics = OwnsPilotInput = true;
            Phase = "Вход в атмосферу";
            foreach (var x in Await(() => false, () => guide.Text)) yield return x;
        }

        /// <summary>
        /// Момент тормозного импульса: самый ранний, после которого дуга от EI до полосы равна WingedRangeFromEI, а боковое
        /// отклонение полосы от плоскости входа не больше WingedCrossMax. Полоса — на момент EI (она вращается с Землёй, а
        /// планирование идёт в воздухе, который вращается с ней же). NaN — не нашлось.
        /// </summary>
        double PlanWingedDeorbit(Runway runway, out double dv, out double cross)
        {
            var v = V;
            var body = v.Body;
            double mu = body.Mu;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, mu, T);
            double rp = body.Radius + WingedDeorbitPerigee, rEI = body.Radius + WingedEntryInterface;
            runway.Frame(out var rwUp, out _);
            double best = double.NaN, bestCross = double.PositiveInfinity, bestDv = 0;
            double prevF = double.NaN, prevT = 0;
            double circ = 2 * Math.PI * body.Radius;
            dv = cross = 0;

            bool Predict(double tb, out double f, out double cr, out double dvb)
            {
                f = cr = dvb = 0;
                orbit.GetState(tb, out var r, out var vel);
                double r1 = r.magnitude;
                if (r1 <= rp) return false;
                // Импульс назад по скорости: новая орбита с апоцентром r1 и перигеем rp (на почти круговой — точно до долей м/с).
                double vNeed = Math.Sqrt(mu * 2 * rp / (r1 * (r1 + rp)));
                double vh = Vector3d.ProjectOnPlane(vel, r / r1).magnitude;
                dvb = vh - vNeed;
                if (dvb <= 0) return false;
                var vel2 = vel - vel.normalized * dvb;
                var o2 = KeplerOrbit.FromState(r, vel2, mu, tb);
                double tEI = o2.NextTimeAtRadius(rEI, tb, false);
                if (double.IsNaN(tEI)) return false;
                o2.GetState(tEI, out var rE, out var vE);
                var vRel = vE - Vector3d.Cross(FlightPhysics.SpinAxis(body, tEI), rE);
                var rw = body.OrientationAt(tEI) * rwUp;
                var n = Vector3d.Cross(rE, vRel).normalized;
                cr = Math.Asin(MathD.Clamp(Vector3d.Dot(rw, n), -1, 1)) * body.Radius;
                var inPlane = Vector3d.ProjectOnPlane(rw, n).normalized;
                var rh = rE.normalized;
                double ang = Math.Atan2(Vector3d.Dot(Vector3d.Cross(rh, inPlane), n), Vector3d.Dot(rh, inPlane));
                if (ang < 0) ang += 2 * Math.PI;
                f = ang * body.Radius - WingedRangeFromEI;
                return true;
            }

            for (double tb = T + WingedDeorbitLead; tb < T + WingedDeorbitWindow; tb += WingedDeorbitStep)
            {
                if (!Predict(tb, out double f, out _, out _)) { prevF = double.NaN; continue; }
                // Чем позже импульс, тем ближе EI к полосе: дуга убывает. Скачок вверх на полный круг — не корень.
                if (!double.IsNaN(prevF) && prevF > 0 && f <= 0 && prevF - f < 0.5 * circ)
                {
                    double tr = prevT + prevF / (prevF - f) * (tb - prevT);
                    if (Predict(tr, out _, out double cr, out double dvr) && Math.Abs(cr) < bestCross)
                    {
                        best = tr; bestCross = Math.Abs(cr); bestDv = dvr; cross = cr;
                        if (bestCross <= WingedCrossMax) break;
                    }
                }
                prevF = f;
                prevT = tb;
            }
            dv = bestDv;
            return bestCross <= 2 * WingedCrossMax ? best : double.NaN;
        }

        /// <summary>
        /// Наведение планера от EI до остановки на полосе (каждый шаг физики). Вход: α по скорости (40° → 15°), крен по
        /// равновесному планированию — дальность до полосы против располагаемой при текущем L/D, плюс демпфер фугоиды по
        /// вертикальной скорости; знак крена — к полосе, перекладка за мёртвой зоной ошибки курса. Заход (TAEM, ниже
        /// WingedTaemSpeed): поле курса к конусу HAC и на ось, высота — по профилю, тангаж — α с ПИ по вертикальной
        /// скорости, щиток — по скоростному напору. Выравнивание — экспонента по высоте, касание ~1 м/с.
        /// </summary>
        sealed class WingedGuidance
        {
            /// <summary>Шкала высот атмосферы, м (у Земли 7–8 км): связывает ошибку торможения с вертикальной скоростью.</summary>
            const double ScaleHeight = 7200;
            /// <summary>
            /// Слежение за торможением (как у «Шаттла»): опорное D = (V² − Vк²) / 2(S − TaemRange) — постоянное торможение,
            /// которое ровно гасит скорость к началу TAEM. Пределы, м/с²: снизу — чтобы не уходить в «прыжок», сверху — 2 g
            /// торможения (≈ 2,2 g перегрузки при L/D 1). Закон равновесного планирования (дальность ∝ ln(1/(1 − V²/gr)))
            /// здесь проигрывал: борт отставал от равновесия на 3–4 км по высоте, приходил к полосе на 3,7 км/с и нырял до 5,3 g.
            /// Верх 12 (≈ 1,2 g): с 20 к концу входа опорное D взлетало (дальность → TaemRange), борт нырял креном 84° и на
            /// передаче в TAEM подхватывал с 3,5 g; излишек энергии на 12 гасит TAEM щитком и профилем.
            /// </summary>
            const double MinRefDrag = 1, MaxRefDrag = 12;
            /// <summary>
            /// Контур по торможению: собственная частота, рад/с (период ~200 с — медленнее короткопериодики, быстрее фугоиды),
            /// и демпфирование. Пара: ωn ↔ DragFloor — при малом D усиление H/D² растёт квадратично.
            /// </summary>
            const double DragOmega = 0.03, DragZeta = 0.7, DragFloor = 2;
            /// <summary>Наименьший cos крена: 84° — дальше подъёмная тянет почти вбок, а не держит высоту.</summary>
            const double MinCosBank = 0.1;
            /// <summary>Мёртвая зона ошибки курса для перекладки крена, °: на гиперзвуке узкая, к TAEM шире (как у «Шаттла»).</summary>
            const double DeadbandHigh = 10, DeadbandLow = 18, DeadbandHighSpeed = 5000, DeadbandLowSpeed = 1500;
            /// <summary>Расписание α, °: 40° до EntryAlphaSpeed, линейно до TaemAlpha к WingedTaemSpeed.</summary>
            const double EntryAlpha = 40, EntryAlphaSpeed = 3500, TaemAlpha = 12;
            /// <summary>Конец входа, м/с (≈ М 2,7). Пара: TaemRange — сколько остаётся планировать после.</summary>
            const double TaemSpeed = 900;
            /// <summary>Дальность, которую закрывает TAEM, м: из располагаемой дальности входа её вычесть.</summary>
            const double TaemRange = 80e3;
            /// <summary>L/D до первого замера (гиперзвук, α 40°).</summary>
            const double LdGuess = 1.0;
            /// <summary>Сглаживание замеров торможения и L/D, с.</summary>
            const double LdFilter = 3;

            /// <summary>
            /// Конус выравнивания курса: радиус, м (крен 45° на 240 м/с — 5,9 км), и плавность захода на окружность, м.
            /// Пара: HacRadius ↔ TaemMaxBank и скорость на конусе.
            /// </summary>
            const double HacRadius = 6000, HacLead = 2500;
            /// <summary>Прямая: от точки выхода (FAP) до точки прицеливания, м; прицел — за столько до порога, м.</summary>
            const double FinalLength = 12000, AimBefore = 1000;
            /// <summary>Глиссада 20° (крутая, как у «Шаттла») и наклон профиля TAEM до неё, °.</summary>
            const double GlideSlope = 20, TaemSlope = 12;
            /// <summary>Подтяжка к профилю высоты, 1/с, и вертикальная скорость → ускорение, 1/с.</summary>
            const double HeightGain = 0.08, SinkGain = 0.5;
            /// <summary>Предел добавочной перегрузки на заходе, g: подхват после входа не круче ~2,5 g (у «Шаттла» до 2,5).</summary>
            const double MaxPullUp = 1.5;
            /// <summary>
            /// Предел поправки к профилю, м/с, и напор, выше которого снижение запрещается, Па (ширина перехода — QBand).
            /// Без них «Буран», отдан TAEM на 6 км выше профиля, пикировал на −345 м/с до 47 кПа и выходил с 4,5 g.
            /// Пара: QLimit ↔ MaxPullUp — чем выше допустимый напор, тем круче вывод из пикирования.
            /// </summary>
            const double MaxHeightCorr = 150, QLimit = 30e3, QBand = 10e3;
            /// <summary>Интегратор α, 1/с, и пределы α на заходе, °. 18° — под срывом (StallAngle крыла «Шаттла» ≈ 27°).</summary>
            const double AlphaIntegral = 0.3, MinAlpha = -2, MaxAlpha = 18;
            /// <summary>
            /// Предел α на заходе до выравнивания, °: чуть выше наивыгоднейшего (у крыла AR 2,3 и Cd0 0,02 — ≈ 8°). На 18°
            /// L/D падает с ~5 до ~3, и борт, оказавшийся ниже профиля, тянул α к упору и проваливался ещё сильнее.
            /// </summary>
            const double TaemMaxAlpha = 11;
            /// <summary>Крен: усиление по ошибке курса и пределы на TAEM / прямой / у земли, °.</summary>
            const double BankGain = 2, TaemMaxBank = 50, FinalMaxBank = 30, FlareMaxBank = 5;
            /// <summary>Захват оси на прямой: упреждение, м, и наибольший угол подхода к оси, °.</summary>
            const double FinalLead = 1500, FinalIntercept = 30;
            /// <summary>Приборная скорость захода, м/с (≈300 узлов у «Шаттла»), и ширина регулятора щитка, м/с.</summary>
            const double EasRef = 150, EasBand = 25;
            /// <summary>
            /// Приборная скорость, к которой щиток гасит борт в выравнивании, м/с: «Шаттл» касается на ~100 м/с (195 узлов).
            /// Пара: FlareEas ↔ MaxAlpha — на 100 м/с орбитеру 97 т нужен CL ≈ 0,6, это α ≈ 14° при пределе 18°.
            /// </summary>
            const double FlareEas = 105;
            /// <summary>Усиление по вертикальной скорости в выравнивании, 1/с: на 0,5 борт опаздывал и касался на 3,9 м/с.</summary>
            const double FlareSinkGain = 1.0;
            /// <summary>
            /// Выравнивание: с этой высоты над полосой, м, вертикальная скорость ∝ высоте (FlareRate, 1/с), не меньше TouchSink.
            /// Пара: TouchSink ↔ FlightPhysics.GearMaxSink (3 м/с) — с запасом втрое.
            /// </summary>
            const double FlareAltitude = 450, FlareRate = 0.2, TouchSink = 1.0;
            /// <summary>Выпуск шасси, м (выпуск — Vessel.GearDeployTime 4 с).</summary>
            const double GearAltitude = 300;
            /// <summary>Руление на пробеге: упреждение до оси, м, и усиление руля на радиан ошибки курса.</summary>
            const double RollLead = 300, RollSteerGain = 4;

            readonly MissionAutopilot ap;
            readonly Runway rw;
            readonly Vector3d upBf, axisBf, rightBf;
            readonly double elevation;
            Vector3d prevVel;
            double prevT = double.NaN, dragAcc, ld = LdGuess;
            int bankSign;
            bool taem, final;
            int hacSide;
            double alphaI = double.NaN, lastAlpha = EntryAlpha;
            double peakG;
            public string Text = "";

            public WingedGuidance(MissionAutopilot ap, Runway rw)
            {
                this.ap = ap;
                this.rw = rw;
                rw.Frame(out upBf, out axisBf);
                rightBf = Vector3d.Cross(axisBf, upBf);
                elevation = rw.Site.Elevation;
            }

            static double Lerp(double a, double b, double k) => a + (b - a) * MathD.Clamp(k, 0, 1);

            public AutopilotRequest Step(Vessel v, double t, double dt)
            {
                var body = v.Body;
                var o = body.OrientationAt(t);
                if (v.IsLanded) return Rollout(v, o);

                var pos = v.Position;
                double r = pos.magnitude;
                var up = pos / r;
                var vRel = v.Velocity - Vector3d.Cross(FlightPhysics.SpinAxis(body, t), pos);
                double V = vRel.magnitude;
                if (V < 1) return AutopilotRequest.None;
                var vh = vRel / V;
                double g = body.Mu / (r * r);
                double hdot = Vector3d.Dot(vRel, up);
                double h = r - body.Radius - elevation;
                peakG = Math.Max(peakG, v.GForce);

                // Замер аэродинамики: приращение скорости минус гравитация (тяги на входе нет).
                if (!double.IsNaN(prevT) && t - prevT > 1e-4)
                {
                    var aAero = (v.Velocity - prevVel) / (t - prevT) + pos * (body.Mu / (r * r * r));
                    double d = -Vector3d.Dot(aAero, vh);
                    double l = (aAero + vh * d).magnitude;
                    double k = Math.Min(1, (t - prevT) / LdFilter);
                    dragAcc += (Math.Max(0, d) - dragAcc) * k;
                    if (d > 0.5) ld += (MathD.Clamp(l / d, 0.2, 6) - ld) * k;
                }
                prevVel = v.Velocity;
                prevT = t;

                var rwUp = o * upBf;
                var axis = o * axisBf;
                var right = o * rightBf;
                double alpha, bank;
                if (!taem && V > TaemSpeed)
                {
                    alpha = V >= EntryAlphaSpeed ? EntryAlpha : Lerp(TaemAlpha, EntryAlpha, (V - TaemSpeed) / (EntryAlphaSpeed - TaemSpeed));
                    // Ошибка курса на полосу по большому кругу: плюс — полоса правее.
                    var hor = Vector3d.ProjectOnPlane(vh, up).normalized;
                    var toRw = Vector3d.ProjectOnPlane(rwUp, up).normalized;
                    var side = Vector3d.Cross(hor, up);
                    double err = Math.Atan2(Vector3d.Dot(toRw, side), Vector3d.Dot(toRw, hor));
                    double range = Vector3d.Angle(up, rwUp) * body.Radius;
                    // «Невесомость» даёт инерциальная скорость: к воздушной прибавить вращение Земли вдоль трассы (на восток
                    // с 40° широты ≈ +350 м/с). С одной воздушной борт считал себя ниже равновесия и уходил вверх.
                    double vc = V + Vector3d.Dot(Vector3d.Cross(FlightPhysics.SpinAxis(body, t), pos), hor);
                    double togo = Math.Max(1, range - TaemRange);
                    double dRef = MathD.Clamp((V * V - TaemSpeed * TaemSpeed) / (2 * togo), MinRefDrag, MaxRefDrag);
                    double hdotRef = -2 * ScaleHeight * dRef / V;
                    double dm = Math.Max(DragFloor, dragAcc);
                    // Нужная вертикальная доля L/D: равновесие при опорном D плюс ПД по ошибке торможения и вертикальной скорости.
                    double u = (g - vc * vc / r) / dRef
                               + ScaleHeight / (dm * dm) * DragOmega * DragOmega * (dragAcc - dRef)
                               - 2 * DragZeta * DragOmega * (hdot - hdotRef) / dm;
                    double cosB = u / Math.Max(0.3, ld);
                    cosB = MathD.Clamp(cosB, MinCosBank, 1);
                    double db = Lerp(DeadbandLow, DeadbandHigh, (V - DeadbandLowSpeed) / (DeadbandHighSpeed - DeadbandLowSpeed)) * Constants.Deg2Rad;
                    if (bankSign == 0) bankSign = err >= 0 ? 1 : -1;
                    else if (bankSign * err < -db) bankSign = -bankSign;
                    bank = bankSign * Math.Acos(cosB);
                    lastAlpha = alpha;
                    ap.Phase = "Вход в атмосферу";
                    Text = $"Вход: {h / 1000:F0} км, {V:F0} м/с, до полосы {range / 1000:F0} км, крен {bank * Constants.Rad2Deg:F0}°, " +
                           $"D {dragAcc:F1}/{dRef:F1} м/с², L/D {ld:F2}, {v.GForce:F1} g";
                    v.AirBrake = 0;
                    v.PitchTrim = 0;   // ручной триммер сбивал бы балансировку α-профиля входа — автопилот ведёт сам

                }
                else
                {
                    taem = true;
                    TerminalArea(v, o, pos, vRel, V, hdot, h, g, axis, right, dt, out alpha, out bank);
                }
                Hold(v, vh, up, alpha * Constants.Deg2Rad, bank);
                return AutopilotRequest.None;
            }

            /// <summary>TAEM, прямая и выравнивание. α — в градусах, крен — в радианах.</summary>
            void TerminalArea(Vessel v, QuaternionD o, Vector3d pos, Vector3d vRel, double V, double hdot, double h, double g,
                Vector3d axis, Vector3d right, double dt, out double alpha, out double bank)
            {
                rw.Locate(v.Body, o.Inverse * pos, out double along, out double cross);
                double vx = Vector3d.Dot(vRel, axis), vy = Vector3d.Dot(vRel, right);
                double vhz = Math.Sqrt(vx * vx + vy * vy);
                double xA = -rw.HalfLength - AimBefore, xF = xA - FinalLength;
                double ux, uy, sTogo, maxBank;
                if (!final)
                {
                    if (hacSide == 0) hacSide = cross >= 0 ? 1 : -1;
                    double px = along - xF, py = cross - hacSide * HacRadius;
                    double d = Math.Sqrt(px * px + py * py);
                    px /= d; py /= d;
                    // Касательная по ходу облёта (к FAP курсом полосы) и наклон к центру по удалению от окружности.
                    double tx = -hacSide * py, ty = hacSide * px;
                    double beta = MathD.Clamp(Math.Atan((d - HacRadius) / HacLead), -60 * Constants.Deg2Rad, 85 * Constants.Deg2Rad);
                    ux = tx * Math.Cos(beta) - px * Math.Sin(beta);
                    uy = ty * Math.Cos(beta) - py * Math.Sin(beta);
                    double angF = Math.Atan2(-hacSide, 0), angP = Math.Atan2(py, px);
                    double rem = hacSide * (angF - angP);
                    rem -= 2 * Math.PI * Math.Floor(rem / (2 * Math.PI));
                    sTogo = Math.Max(0, d - HacRadius) + HacRadius * rem + FinalLength;
                    maxBank = TaemMaxBank;
                    bool atFap = (rem < 20 * Constants.Deg2Rad || rem > 350 * Constants.Deg2Rad) && Math.Abs(d - HacRadius) < 3000;
                    bool straightIn = along < xF + 2000 && Math.Abs(cross) < 2000 && vx > 0 && Math.Abs(vy) < 0.35 * vx;
                    if ((atFap && vx > 0) || straightIn) final = true;
                    ap.Phase = "Заход на посадку";
                }
                else
                {
                    double psi = -MathD.Clamp(Math.Atan(cross / FinalLead), -FinalIntercept * Constants.Deg2Rad, FinalIntercept * Constants.Deg2Rad);
                    ux = Math.Cos(psi);
                    uy = Math.Sin(psi);
                    sTogo = Math.Max(0, xA - along);
                    maxBank = FinalMaxBank;
                    ap.Phase = "Посадка";
                }
                // Ошибка курса в плоскости полосы: плюс — нужно правее (от оси к +cross).
                double err = vhz > 1 ? Math.Atan2((vx * uy - vy * ux) / vhz, (vx * ux + vy * uy) / vhz) : 0;

                double hT = v.TerrainAltitude;
                bool low = final && hT < FlareAltitude;
                if (hT < 100) maxBank = Math.Min(maxBank, FlareMaxBank);
                bank = MathD.Clamp(BankGain * err, -maxBank * Constants.Deg2Rad, maxBank * Constants.Deg2Rad);

                double gamma = (sTogo <= FinalLength ? GlideSlope : TaemSlope) * Constants.Deg2Rad;
                double hRef = sTogo <= FinalLength
                    ? sTogo * Math.Tan(GlideSlope * Constants.Deg2Rad)
                    : FinalLength * Math.Tan(GlideSlope * Constants.Deg2Rad) + (sTogo - FinalLength) * Math.Tan(TaemSlope * Constants.Deg2Rad);
                double q = v.DynamicPressure;
                double hdotDes = -vhz * Math.Tan(gamma) + MathD.Clamp(HeightGain * (hRef - h), -MaxHeightCorr, MaxHeightCorr);
                double diveMax = vhz * Math.Tan(35 * Constants.Deg2Rad) * MathD.Clamp((QLimit - q) / QBand, 0, 1);
                hdotDes = MathD.Clamp(hdotDes, -diveMax, 30);
                if (low) hdotDes = -Math.Max(TouchSink, Math.Min(vhz * Math.Tan(GlideSlope * Constants.Deg2Rad), FlareRate * Math.Max(0, hT)));

                // α: нужное вертикальное ускорение → прирост подъёмной силы по наклону CL(α) крыла при текущем напоре.
                double aCmd = (low ? FlareSinkGain : SinkGain) * (hdotDes - hdot) + g * (1 / Math.Max(0.5, Math.Cos(bank)) - 1);
                aCmd = MathD.Clamp(aCmd, -g, MaxPullUp * g);
                v.MassProperties(out double mass, out _, out _, out _);
                double slope = WingSlope(v);
                double perRad = q * slope / Math.Max(1, mass);
                double dAlpha = aCmd / Math.Max(0.3, perRad) * Constants.Rad2Deg;
                if (double.IsNaN(alphaI)) alphaI = lastAlpha;
                double maxA = low ? MaxAlpha : TaemMaxAlpha;
                alphaI = MathD.Clamp(alphaI + AlphaIntegral * dAlpha * dt, MinAlpha, maxA);
                alpha = MathD.Clamp(alphaI + dAlpha, MinAlpha, maxA);

                double eas = Math.Sqrt(2 * q / 1.225);
                v.AirBrake = low
                    ? MathD.Clamp((eas - FlareEas) / EasBand, 0, 1)
                    : MathD.Clamp((eas - EasRef) / EasBand + (h - hRef) / 2000, 0, 1);
                if (hT < GearAltitude && !v.GearDown) v.ExtendGear();
                Text = $"{ap.Phase}: {h / 1000:F1} км (профиль {hRef / 1000:F1}), {V:F0} м/с, до точки {sTogo / 1000:F1} км, " +
                       $"бок {cross:F0} м, α {alpha:F0}°, крен {bank * Constants.Rad2Deg:F0}°, щиток {v.AirBrake * 100:F0}%";
            }

            /// <summary>Наклон CL(α) крыльев борта × площадь, м²/рад — для пересчёта нужного ускорения в α.</summary>
            static double WingSlope(Vessel v)
            {
                double s = 0;
                var secs = v.Design.Sections;
                for (int i = 0; i < secs.Count; i++)
                {
                    if (!v.Attached[i] || secs[i].Wings == null) continue;
                    foreach (var w in secs[i].Wings)
                        if (!w.Vertical)
                            s += w.Area * Aerodynamics.LiftSlope(w.AspectRatio, w.Sweep * Constants.Deg2Rad, v.Mach);
                }
                return s;
            }

            /// <summary>Ориентация по α и крену относительно скорости: нос = v·cos α + l·sin α, брюхо (+X) — навстречу потоку.</summary>
            static void Hold(Vessel v, Vector3d vh, Vector3d up, double alpha, double bank)
            {
                var upPerp = Vector3d.ProjectOnPlane(up, vh);
                if (upPerp.sqrMagnitude < 1e-9) return;
                upPerp = upPerp.normalized;
                var side = Vector3d.Cross(vh, upPerp);
                var lift = upPerp * Math.Cos(bank) + side * Math.Sin(bank);
                var Y = vh * Math.Cos(alpha) + lift * Math.Sin(alpha);
                var X = vh * Math.Sin(alpha) - lift * Math.Cos(alpha);
                var Z = -Vector3d.Cross(X, Y);
                v.Sas = SasMode.Stability;
                v.SasHold = QuaternionD.FromBasis(X.SwapYZ, Y.SwapYZ, Z.SwapYZ);
                v.SasHoldValid = true;
            }

            /// <summary>Пробег: щиток настежь, руль к оси полосы.</summary>
            AutopilotRequest Rollout(Vessel v, QuaternionD o)
            {
                v.AirBrake = 1;
                // Тормозной парашют — сразу после касания основных стоек (§4.6; у «Шаттла» с STS-49 — до опускания носа),
                // сбрасывает его само ядро на FlightPhysics.DragChuteJettisonSpeed.
                if (v.DragChute == DragChuteState.Stowed && v.DragChuteArea() > 0) v.DeployDragChute();
                rw.Locate(v.Body, v.AnchorBodyFixed, out double along, out double cross);
                double fx = Vector3d.Dot(v.RollDir, axisBf), fy = Vector3d.Dot(v.RollDir, rightBf);
                double want = -Math.Atan(cross / RollLead);
                double err = want - Math.Atan2(fy, fx);
                v.PilotInput = new Vector3d(0, MathD.Clamp(RollSteerGain * err, -1, 1), 0);
                ap.Phase = "Пробег";
                Text = $"Пробег: {v.RollSpeed:F0} м/с, от порога {along + rw.HalfLength:F0} м, от оси {cross:F0} м" +
                       (double.IsNaN(v.TouchdownSink) ? "" : $"; касание {v.TouchdownSpeed:F0} м/с, снижение {v.TouchdownSink:F1} м/с") +
                       (v.DragChute == DragChuteState.Open ? "; тормозной парашют" : "");
                return AutopilotRequest.None;
            }
        }
    }
}
