using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Возвращаемая ступень (GDD §6.9): запас топлива на посадку, цель (баржа или площадка) и параметры манёвров.
    /// Задаётся у секции (SectionDef.Recovery); отделившись, такая ступень получает фоновый автопилот посадки
    /// (BoosterLandingAutopilot) — он ведёт её и тогда, когда игрок управляет верхней ступенью.
    /// </summary>
    public sealed class RecoveryDef
    {
        /// <summary>Имя борта после отделения и название цели (HUD, события).</summary>
        public string StageName, TargetName;
        public double TargetLat, TargetLon;
        /// <summary>Цель на воде (баржа): настил на DeckHeight над уровнем моря. Иначе — над рельефом.</summary>
        public bool AtSea;
        /// <summary>Высота настила, м, и его полуразмер, м: в этом круге касание воды — посадка на палубу (FlightPhysics.CheckContact).</summary>
        public double DeckHeight, DeckRadius = 50;
        /// <summary>
        /// Запас топлива на возврат, кг (§6.9: «резерв на три манёвра»). В связке ступень его не жжёт — MECO по запасу
        /// (Vessel.HeldReserve). Пара: должен покрыть BoostbackDv + EntryDv + посадку (LandingDv) по Циолковскому.
        /// </summary>
        public double Reserve;
        /// <summary>Двигателей на разворотный и входной импульсы и на посадку (Falcon 9: 3 и 1; Super Heavy: 13 и 3).</summary>
        public int BurnEngines = 3, LandingEngines = 1;
        /// <summary>Входной импульс: высота начала на нисходящей ветви, м, и номинальный Δv для прогноза, м/с (0 — без него).</summary>
        public double EntryAltitude = 70000, EntryDv;
        /// <summary>Промах прогноза, м, выше которого перед входом нужен разворотный (boostback) импульс.</summary>
        public double BoostbackTolerance = 3000;
        /// <summary>Решётчатые рули, м² (сумма): управляющий момент на напоре (Vessel.GridFinTorque), раскрыты после отделения.</summary>
        public double GridFinArea;
        /// <summary>Вход «брюхом» (Starship): на аэроучастке поперёк потока, перед посадкой — переворот (belly flop).</summary>
        public bool BellyFlop;
        /// <summary>
        /// Высота переворота из положения «брюхом» в вертикаль, м (IFT: ≈ 1–1,5 км). Пара: переворот моментом
        /// RcsTorque занимает ≈ 10 с, на скорости ≈ 100 м/с это ≈ 1 км — ниже не успевает погасить снижение.
        /// </summary>
        public double FlopAltitude = 1300;
        /// <summary>
        /// Ловля башней (Super Heavy, Starbase): опор нет — в круге DeckRadius у цели касание держат «палочки» башни
        /// (FlightPhysics.CheckContact не требует опор). Упрощение: ловля = мягкая посадка у башни.
        /// </summary>
        public bool TowerCatch;
        /// <summary>Δv посадочного импульса с запасом, м/с: столько топлива входной импульс обязан оставить.</summary>
        public double LandingDv = 600;
    }

    /// <summary>
    /// Фоновый автопилот посадки ступени (§6.9). Профиль Falcon 9 Demo-2: переворот азотной РСУ хвостом вперёд →
    /// (при большом промахе — разворотный импульс BurnEngines двигателями) → входной импульс на нисходящей ветви →
    /// аэроучасток хвостом вперёд на решётчатых рулях → «hoverslam» LandingEngines двигателями (зависнуть нельзя:
    /// минимальная тяга Merlin 338 кН больше веса пустой ступени ≈ 280 кН) → опоры за секунды до касания.
    /// Подъёмной силы у корпуса в модели нет (FlightPhysics.Drag — только вдоль потока), поэтому боковое наведение —
    /// тягой: на входном импульсе и на посадке. Работает только в полной физике: Universe держит такой борт под ней.
    /// </summary>
    public sealed class BoosterLandingAutopilot
    {
        public enum PhaseType { Flip, Boostback, Coast, Entry, Aero, Flop, Landing, Done, Failed }

        public PhaseType Phase { get; private set; } = PhaseType.Flip;
        public readonly Vessel Vessel;
        public readonly RecoveryDef Def;
        readonly int section;
        /// <summary>Итог: скорость касания, м/с, и промах от центра цели, м (NaN — ещё летит или погиб).</summary>
        public double TouchdownSpeed = double.NaN, Miss = double.NaN;
        public string Status = "";

        /// <summary>
        /// Посадочный закон: целевая скорость снижения v(h) = √(v0² + 2·k·h). k = доля LandingDecelShare от запаса
        /// ускорения (aMax − g) — так у регулятора есть ход газа в обе стороны. Касание — на TouchdownRate м/с
        /// (пара: FlightPhysics.CrashSpeed 10 и требование «< 6 м/с»).
        /// </summary>
        const double LandingDecelShare = 0.65, TouchdownRate = 2.5, RateGain = 1.5;
        /// <summary>Наибольший наклон тяги от вертикали на посадке, рад (боковой увод к цели).</summary>
        const double MaxTilt = 0.2;
        /// <summary>Опоры (FlightPhysics.LegDeployTime 2 с) — когда до касания меньше LegLeadTime секунд.</summary>
        const double LegLeadTime = 5;
        /// <summary>Наклон тяги входного импульса на боковой промах: рад на метр, предел — EntryMaxTilt.</summary>
        const double EntryTiltGain = 1.0 / 20000, EntryMaxTilt = 0.12;

        double nextPredict, entryAlong = double.NaN, entryCross;
        Vector3d boostDv;
        bool legs;
        double lastSpeed;

        BoosterLandingAutopilot(Vessel v, RecoveryDef def, int sec)
        {
            Vessel = v;
            Def = def;
            section = sec;
        }

        public bool Running => Phase != PhaseType.Done && Phase != PhaseType.Failed;

        /// <summary>Отделился борт из одной секции с RecoveryDef и топливом — взвести и начать возврат.</summary>
        public static BoosterLandingAutopilot TryStart(Vessel d)
        {
            int idx = -1, n = 0;
            for (int i = 0; i < d.Attached.Length; i++)
                if (d.Attached[i]) { n++; idx = i; }
            if (n != 1) return null;
            var s = d.Design.Sections[idx];
            if (s.Recovery == null || !s.HasEngine || d.Propellant[idx] <= 0) return null;
            // Split пометил её обломком и разоружил (§5): это управляемая ступень, а не мусор.
            d.IsDebris = false;
            d.Armed[idx] = true;
            d.Sas = SasMode.Off;
            d.Name = s.Recovery.StageName ?? s.Name;
            d.Raise($"{d.Name}: возврат на «{s.Recovery.TargetName}», запас {d.Propellant[idx] / 1000:F1} т");
            return new BoosterLandingAutopilot(d, s.Recovery, idx);
        }

        /// <summary>
        /// Посадка борта, который не отделялся, а летит сам (корабль Starship после SECO, §6.9): пилот начинает с
        /// пассивного участка — переворот и разворотный импульс ему не нужны. Имя борта не меняется (это активный борт).
        /// </summary>
        public static BoosterLandingAutopilot StartDescent(Vessel v)
        {
            int idx = -1, n = 0;
            for (int i = 0; i < v.Attached.Length; i++)
                if (v.Attached[i]) { n++; idx = i; }
            if (n != 1) return null;
            var s = v.Design.Sections[idx];
            if (s.Recovery == null || !s.HasEngine) return null;
            v.Armed[idx] = true;
            v.Sas = SasMode.Off;
            v.Raise($"{v.Name}: спуск к «{s.Recovery.TargetName}», топливо {v.Propellant[idx] / 1000:F1} т");
            return new BoosterLandingAutopilot(v, s.Recovery, idx) { Phase = PhaseType.Coast };
        }

        /// <summary>
        /// Одиночная секция с входом «брюхом» (RecoveryDef.BellyFlop): закрылки держат её поперёк потока, поэтому лимит
        /// q·α длинного пакета (FlightPhysics.CheckStructure) к ней не применяется — иначе ломалась бы на α 90° сразу.
        /// </summary>
        public static bool BellyEntry(Vessel v)
        {
            int n = 0;
            RecoveryDef r = null;
            for (int i = 0; i < v.Attached.Length; i++)
                if (v.Attached[i]) { n++; r = v.Design.Sections[i].Recovery; }
            return n == 1 && r != null && r.BellyFlop;
        }

        /// <summary>Касание в круге ловли у башни (RecoveryDef.TowerCatch): держат «палочки», опоры не нужны.</summary>
        public static bool CaughtByTower(Vessel v, CelestialBody body, Vector3d bodyFixed)
        {
            for (int i = 0; i < v.Attached.Length; i++)
            {
                var r = v.Attached[i] ? v.Design.Sections[i].Recovery : null;
                if (r == null || !r.TowerCatch || body != v.Body) continue;
                var c = CelestialBody.LatLonToBodyFixed(r.TargetLat, r.TargetLon);
                if ((bodyFixed.normalized - c).magnitude * body.Radius <= r.DeckRadius) return true;
            }
            return false;
        }

        /// <summary>Снять пилота (ручное управление): двигатели остаются как есть.</summary>
        public void Abort(string why)
        {
            if (!Running) return;
            Phase = PhaseType.Failed;
            Status = why;
            Vessel.EngineLimit = 0;
        }

        // ---------------------------------------------------------------- цель

        /// <summary>Центр настила в осях тела, м от центра.</summary>
        public Vector3d TargetBodyFixed()
        {
            var body = Vessel.Body;
            var dir = CelestialBody.LatLonToBodyFixed(Def.TargetLat, Def.TargetLon);
            double ground = Def.AtSea ? 0 : body.SurfaceHeight(dir);
            return dir * (body.Radius + ground + Def.DeckHeight);
        }

        /// <summary>Касание воды в круге палубы — посадка на настил (крючок FlightPhysics.CheckContact).</summary>
        public static bool DeckUnder(Vessel v, CelestialBody body, Vector3d bodyFixed, out double deckHeight)
        {
            deckHeight = 0;
            for (int i = 0; i < v.Attached.Length; i++)
            {
                var r = v.Attached[i] ? v.Design.Sections[i].Recovery : null;
                if (r == null || !r.AtSea || r.DeckHeight <= 0 || body != v.Body) continue;
                var c = CelestialBody.LatLonToBodyFixed(r.TargetLat, r.TargetLon);
                if ((bodyFixed.normalized - c).magnitude * body.Radius > r.DeckRadius) continue;
                deckHeight = r.DeckHeight;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- шаг

        public void Update(double t, double h)
        {
            var v = Vessel;
            if (!Running) return;
            if (!v.Alive)
            {
                Phase = PhaseType.Failed;
                Status = v.DestroyReason ?? "потерян";
                return;
            }
            var body = v.Body;
            var o = body.OrientationAt(t);
            var tgtBf = TargetBodyFixed();
            if (v.IsLanded)
            {
                FlightControl.Cutoff(v);
                v.EngineLimit = 0;
                TouchdownSpeed = lastSpeed;
                var bf = o.Inverse * v.Position;
                Miss = (bf.normalized - tgtBf.normalized).magnitude * tgtBf.magnitude;
                Phase = PhaseType.Done;
                Status = $"села: {TouchdownSpeed:F1} м/с, {Miss:F0} м от центра «{Def.TargetName}»";
                v.Raise($"{v.Name} {Status}");
                return;
            }

            var spin = FlightPhysics.SpinAxis(body, t);
            var r = v.Position;
            var up = r.normalized;
            var vs = v.Velocity - Vector3d.Cross(spin, r);
            double vDown = -Vector3d.Dot(vs, up);
            lastSpeed = vs.magnitude;
            v.MassProperties(out double mass, out double com, out _, out _);
            double hBottom = r.magnitude - tgtBf.magnitude - com * Math.Abs(Vector3d.Dot(v.NoseP, up));
            var retro = vs.sqrMagnitude > 1 ? -vs.normalized : up;

            switch (Phase)
            {
                case PhaseType.Flip:
                {
                    // Переворот хвостом вперёд азотной РСУ (на вакууме двигателю нечем помочь, кроме качания).
                    FlightControl.PointAt(v, retro);
                    if (t < nextPredict) break;
                    nextPredict = t + 1;
                    var miss = Predict(t, r, v.Velocity, mass, true, out _);
                    if (miss.magnitude > Def.BoostbackTolerance && Vector3d.Dot(vs, up) > -200)
                    {
                        Phase = PhaseType.Boostback;
                        Status = $"разворотный импульс: промах {miss.magnitude / 1000:F1} км";
                        v.Raise($"{v.Name}: {Status}");
                    }
                    else if (Angle(v.NoseP, retro) < 10)
                    {
                        Phase = PhaseType.Coast;
                        Status = $"пассивный участок, промах прогноза {miss.magnitude:F0} м";
                    }
                    break;
                }
                case PhaseType.Boostback:
                    Boostback(t, r, vs, mass);
                    break;
                case PhaseType.Coast:
                    FlightControl.PointAt(v, retro);
                    if (vDown > 0 && r.magnitude - body.Radius < Def.EntryAltitude && Def.EntryDv > 0)
                    {
                        Phase = PhaseType.Entry;
                        v.EngineLimit = Def.BurnEngines;
                        FlightControl.Ignite(v, 1);
                        Status = "входной импульс";
                        v.Raise($"{v.Name}: входной импульс ({Def.BurnEngines} дв.)");
                    }
                    else if (vDown > 0 && Def.EntryDv <= 0 && r.magnitude - body.Radius < Def.EntryAltitude)
                        Phase = PhaseType.Aero;
                    break;
                case PhaseType.Entry:
                    Entry(t, r, vs, retro, mass, hBottom);
                    break;
                case PhaseType.Aero:
                    AeroAndIgnition(t, r, up, vs, vDown, hBottom, mass);
                    break;
                case PhaseType.Flop:
                    Flop(t, r, up, vs, vDown, hBottom, mass);
                    break;
                case PhaseType.Landing:
                    Landing(t, r, up, vs, vDown, hBottom, mass, o, tgtBf);
                    break;
            }
        }

        static double Angle(Vector3d a, Vector3d b) =>
            Math.Acos(MathD.Clamp(Vector3d.Dot(a.normalized, b.normalized), -1, 1)) * Constants.Rad2Deg;

        // ---------------------------------------------------------------- разворотный импульс

        void Boostback(double t, Vector3d r, Vector3d vs, double mass)
        {
            var v = Vessel;
            if (t >= nextPredict)
            {
                // Чувствительность точки падения к Δv по двум горизонтальным осям — численно, по прогнозу (матрица 2×2).
                nextPredict = t + (boostDv.magnitude < 40 ? 0.02 : 0.25);
                Frame(r, vs, out var e1, out var e2);
                const double probe = 5;
                var m0 = Predict(t, r, v.Velocity, mass, true, out _);
                var m1 = Predict(t, r, v.Velocity + e1 * probe, mass, true, out _);
                var m2 = Predict(t, r, v.Velocity + e2 * probe, mass, true, out _);
                double a = Vector3d.Dot(m1 - m0, e1) / probe, b = Vector3d.Dot(m2 - m0, e1) / probe;
                double c = Vector3d.Dot(m1 - m0, e2) / probe, d = Vector3d.Dot(m2 - m0, e2) / probe;
                double det = a * d - b * c;
                double x = -Vector3d.Dot(m0, e1), y = -Vector3d.Dot(m0, e2);
                boostDv = Math.Abs(det) < 1e-9 ? Vector3d.zero : e1 * ((d * x - b * y) / det) + e2 * ((a * y - c * x) / det);
                Status = $"разворотный импульс: промах {m0.magnitude:F0} м, Δv {boostDv.magnitude:F0} м/с";
                if (m0.magnitude < 150 || boostDv.magnitude < 0.3)
                {
                    FlightControl.Cutoff(v);
                    v.EngineLimit = 0;
                    Phase = PhaseType.Coast;
                    v.Raise($"{v.Name}: разворотный импульс закончен, промах прогноза {m0.magnitude:F0} м");
                    return;
                }
            }
            var dir = boostDv.normalized;
            FlightControl.PointAt(v, dir);
            if (!v.AnyEngineRunning && Angle(v.NoseP, dir) > 5) return;
            // Хвост грубого Δv — одним двигателем: минимальная тяга трёх (≈ 18 м/с² у пустой ступени) перелетает цель за шаг.
            v.EngineLimit = boostDv.magnitude > 60 ? Def.BurnEngines : 1;
            double thrust = v.EngineThrustLimit(v.StaticPressure, section);
            double thr = thrust > 0 ? boostDv.magnitude * mass / (thrust * 2) : 1;
            FlightControl.Ignite(v, MathD.Clamp(thr, 0.05, 1));
        }

        /// <summary>Горизонтальные оси: e1 — по трассе (горизонталь скорости), e2 — поперёк.</summary>
        static void Frame(Vector3d r, Vector3d vs, out Vector3d e1, out Vector3d e2)
        {
            var up = r.normalized;
            var hz = vs - up * Vector3d.Dot(vs, up);
            e1 = hz.sqrMagnitude > 1 ? hz.normalized : Vector3d.Cross(Vector3d.forward, up).normalized;
            e2 = Vector3d.Cross(up, e1);
        }

        // ---------------------------------------------------------------- входной импульс

        void Entry(double t, Vector3d r, Vector3d vs, Vector3d retro, double mass, double hBottom)
        {
            var v = Vessel;
            if (t >= nextPredict)
            {
                nextPredict = t + 0.1;
                Frame(r, vs, out var e1, out var e2);
                // Цель — точка остановки после посадочного импульса, а не баллистическое падение: импульс тягой против
                // скорости гасит и горизонталь, разница — километр-полтора (замер: 1,3 км недолёта).
                PlanLanding(t, r, v.Velocity, mass, hBottom, false);
                var miss = landMiss;
                entryAlong = Vector3d.Dot(miss, e1);
                entryCross = Vector3d.Dot(miss, e2);
                Status = $"входной импульс: перелёт {entryAlong:F0} м, вбок {entryCross:F0} м";
            }
            Frame(r, vs, out _, out var side);
            double tilt = MathD.Clamp(entryCross * EntryTiltGain, -EntryMaxTilt, EntryMaxTilt);
            var dir = (retro - side * tilt).normalized;
            FlightControl.PointAt(v, dir);
            FlightControl.Ignite(v, 1);
            // Отсечка: точка падения дошла до цели или топлива осталось только на посадку.
            double dry = mass - v.Propellant[section];
            double ve = v.Design.Sections[section].Engine.IspVac * Constants.G0 * 0.9;
            double landingFuel = dry * (Math.Exp(Def.LandingDv / ve) - 1);
            if (entryAlong <= 0 || v.Propellant[section] <= landingFuel)
            {
                FlightControl.Cutoff(v);
                v.EngineLimit = 0;
                Phase = PhaseType.Aero;
                Status = $"аэроучасток: перелёт {entryAlong:F0} м, вбок {entryCross:F0} м";
                v.Raise($"{v.Name}: входной импульс закончен, {Status}");
            }
        }

        // ---------------------------------------------------------------- аэроучасток и посадка

        /// <summary>Тяга посадочных двигателей и местное g, м/с².</summary>
        void LandingAccel(Vector3d r, double mass, out double aMax, out double g)
        {
            var s = Vessel.Design.Sections[section];
            double p = Vessel.StaticPressure;
            aMax = s.Engine.Thrust(p) * Math.Min(Def.LandingEngines, s.EngineCount) / mass;
            g = Vessel.Body.Mu / r.sqrMagnitude;
        }

        double IgnitionHeight(double vDown, double aMax, double g, out double k)
        {
            k = Math.Max(0.5, LandingDecelShare * (aMax - g));
            // Запаздывание на шаг и разворот сопла: 0,5 с полёта на текущей скорости.
            return (vDown * vDown - TouchdownRate * TouchdownRate) / (2 * k) + vDown * 0.5;
        }

        void AeroAndIgnition(double t, Vector3d r, Vector3d up, Vector3d vs, double vDown, double hBottom, double mass)
        {
            var v = Vessel;
            if (Def.BellyFlop)
            {
                // Вход «брюхом»: ось поперёк потока, нос по горизонту трассы — сопротивление всей боковой площадью.
                Frame(r, vs, out var e1, out _);
                var belly = (e1 - vs.normalized * Vector3d.Dot(e1, vs.normalized)).normalized;
                FlightControl.PointAt(v, belly.sqrMagnitude > 0.5 ? belly : e1);
                if (hBottom < Def.FlopAltitude)
                {
                    Phase = PhaseType.Flop;
                    v.Raise($"{v.Name}: переворот (belly flop)");
                }
                return;
            }
            FlightControl.PointAt(v, vs.sqrMagnitude > 1 ? -vs.normalized : up);
            if (vDown <= 0) return;
            if (t >= nextPredict)
            {
                nextPredict = t + 0.25;
                PlanLanding(t, r, v.Velocity, mass, hBottom, false);
                Status = $"аэроучасток: зажигание на {ignitionHeight:F0} м ({landingLit} дв.), промах {landMiss.magnitude:F0} м";
            }
            LandingAccel(r, mass, out double aMax, out double g);
            bool late = ignitionHeight < 0 && hBottom < LateIgnitionHeight && hBottom <= IgnitionHeight(vDown, aMax, g, out _);
            if (hBottom <= ignitionHeight + vDown * 0.3 || late) StartLanding();
        }

        /// <summary>
        /// Ниже этой высоты, м, зажигание по мгновенному профилю, даже если прогноз точки не нашёл. Пара: выше — ступень
        /// ещё тормозится воздухом и мгновенный профиль зажигал бы на десятках километров (PlanIgnition).
        /// </summary>
        const double LateIgnitionHeight = 4000;
        /// <summary>
        /// Выше этой высоты, м, посадочный импульс не зажигается: на 40–50 км ступень ещё разгоняется под профиль, но воздух
        /// ниже её затормозит, а импульс оттуда сжёг бы всё топливо (замер: зажигание на 43 км — удар 53 м/с без топлива).
        /// </summary>
        const double IgnitionCeiling = 12000;
        /// <summary>Выше этой скорости, м/с, посадка ведётся по прогнозу точки остановки (SimulateLanding), ниже — ZEM/ZEV.</summary>
        const double PredictiveSpeed = 60;

        double ignitionHeight = -1;
        int landingLit = 1;
        Vector3d landMiss;
        double landTgo;

        /// <summary>
        /// Выбор числа посадочных двигателей и прогноз посадки (SimulateLanding): сначала на LandingEngines, а если одним
        /// не успеть погасить скорость до настила — на BurnEngines (тяжёлые возвраты Falcon 9 садятся «на трёх» и
        /// глушат два в конце).
        /// </summary>
        void PlanLanding(double t, Vector3d r, Vector3d u, double mass, double hNow, bool burning)
        {
            int one = Def.LandingEngines;
            landMiss = SimulateLanding(t, r, u, mass, hNow, one, burning, out double hIgn, out bool ok, out landTgo);
            landingLit = one;
            if (!ok && Def.BurnEngines > one)
            {
                landMiss = SimulateLanding(t, r, u, mass, hNow, Def.BurnEngines, burning, out hIgn, out ok, out landTgo);
                landingLit = Def.BurnEngines;
            }
            ignitionHeight = hIgn;
        }

        /// <summary>
        /// Прогон посадки до остановки над настилом (тем же законом, что Landing, без бокового увода): промах точки
        /// остановки от цели (инерциальный горизонтальный вектор, м), высота зажигания hIgn и успел ли импульс
        /// погасить снижение (ok). Зажигание — там, где траектория, уже побывав «под профилем» v(h) (IgnitionHeight),
        /// снова его догоняет: сверху скорость тоже бывает «за профилем» (781 м/с на 58 км), но воздух её ещё затормозит
        /// до ≈ 300 м/с — сравнение «сейчас» зажигало на 58 км и ломало ступень. Импульс тормозит и по горизонтали
        /// (тяга против скорости), поэтому баллистическая точка падения (Predict) для посадки врёт на километры.
        /// </summary>
        Vector3d SimulateLanding(double t, Vector3d r, Vector3d u, double mass, double hNow, int lit, bool burning,
            out double hIgn, out bool ok, out double tgo)
        {
            var body = Vessel.Body;
            var atm = body.Atmosphere;
            var s = Vessel.Design.Sections[section];
            Aero(s, out double area, out double cdFixed);
            double finCdA = Vessel.GridFinCdA();
            var tgtBf = TargetBodyFixed();
            double floor = tgtBf.magnitude, offset = hNow - (r.magnitude - floor);
            int n = Math.Min(lit, s.EngineCount);
            bool slow = true, burn = burning, saved = false; // старт «под профилем»: ниже IgnitionCeiling и «за ним» — зажигать сейчас
            double dry = mass - Vessel.Propellant[section];
            hIgn = burning ? hNow : -1;
            ok = false;
            double t0 = t;
            // Пересечений профиля сверху бывает несколько: на 40–50 км ступень ещё разгоняется, ниже воздух её тормозит.
            // Зажигание — в последнем: баллистика гонится до настила, точка запоминается и прогон продолжается оттуда.
            Vector3d sr = r, su = u;
            double st = t, sm = mass, sh = 0;
            for (int i = 0; i < 12000; i++)
            {
                double h = r.magnitude - floor + offset;
                var up = r.normalized;
                var spin = FlightPhysics.SpinAxis(body, t);
                var vAir = u - Vector3d.Cross(spin, r);
                double sp = vAir.magnitude, vDown = -Vector3d.Dot(vAir, up);
                double p = atm != null ? atm.Pressure(Math.Max(0, r.magnitude - body.Radius)) : 0;
                double g = body.Mu / r.sqrMagnitude;
                double aMax = s.Engine.Thrust(p) * n / mass;
                double hProfile = IgnitionHeight(vDown, aMax, g, out double k);
                if (!burn)
                {
                    if (h > hProfile) slow = true;
                    else if ((slow && h < IgnitionCeiling) || (h < LateIgnitionHeight && !saved))
                    {
                        slow = false;
                        saved = true;
                        sr = r; su = u; st = t; sm = mass; sh = h;
                    }
                    if (h <= 0)
                    {
                        if (!saved) break;
                        r = sr; u = su; t = st; mass = sm;
                        burn = true;
                        hIgn = sh;
                        continue;
                    }
                }
                if (burn && vDown < TouchdownRate + 0.5) { ok = h > -5; break; }
                if (h <= 0) break;
                double dt = h > 5000 ? 0.5 : 0.1;
                var a = PredictAccel(body, atm, spin, r, u, mass, area, cdFixed, finCdA, null, 0, out _);
                if (burn && sp > 1e-3)
                {
                    double vt = Math.Sqrt(TouchdownRate * TouchdownRate + 2 * k * Math.Max(0, h));
                    double aDragUp = Vector3d.Dot(a + up * g, up);
                    double av = g + k + RateGain * (vDown - vt) - aDragUp;
                    double cos = Math.Max(0.3, vDown / sp);
                    double at = mass > dry ? MathD.Clamp(av / cos, 0, aMax) : 0;
                    a -= vAir * (at / sp);
                    mass -= s.Engine.MassFlow * n * (at / aMax) * dt;
                }
                r += u * dt + a * (0.5 * dt * dt);
                u += a * dt;
                t += dt;
            }
            tgo = t - t0;
            var bf = body.OrientationAt(t).Inverse * r;
            var d = bf.normalized * tgtBf.magnitude - tgtBf;
            return body.OrientationAt(t0) * d;
        }

        void Flop(double t, Vector3d r, Vector3d up, Vector3d vs, double vDown, double hBottom, double mass)
        {
            var v = Vessel;
            // Двигатели зажигаются в начале переворота и сами помогают довернуть в вертикаль (качанием). Прогноза посадки
            // «брюхом» не было (PlanLanding) — сразу все тормозные (Starship: 3 Raptor), а у воды Landing глушит лишние
            // до LandingEngines: три Raptor даже на MinThrottle поднимают пустой корабль (замер: ушёл с 2,3 на 4,1 км).
            landingLit = Math.Max(landingLit, Math.Max(Def.BurnEngines, Def.LandingEngines));
            if (!v.AnyEngineRunning) StartLanding();
            Phase = PhaseType.Landing;
        }

        void StartLanding()
        {
            var v = Vessel;
            v.EngineLimit = landingLit;
            FlightControl.Ignite(v, 0.7);
            Phase = PhaseType.Landing;
            Status = "посадочный импульс";
            v.Raise($"{v.Name}: посадочный импульс ({landingLit} дв.)");
        }

        void Landing(double t, Vector3d r, Vector3d up, Vector3d vs, double vDown, double hBottom, double mass,
            QuaternionD o, Vector3d tgtBf)
        {
            var v = Vessel;
            LandingAccel(r, mass, out double aMax, out double g);
            double k = Math.Max(0.5, LandingDecelShare * (aMax - g));
            double hh = Math.Max(0, hBottom);
            double vt = Math.Sqrt(TouchdownRate * TouchdownRate + 2 * k * hh);
            double aDrag = DragAccel(r, vs, mass);
            double av = g + k + RateGain * (vDown - vt) - aDrag * Math.Max(0, vDown) / Math.Max(1, vs.magnitude);

            // Боковой увод к центру палубы (ZEM/ZEV): время до касания — по средней скорости снижения.
            var tgt = o * tgtBf;
            var dx = r - tgt;
            dx -= up * Vector3d.Dot(dx, up);
            var vh = vs - up * Vector3d.Dot(vs, up);
            double tgo = 2 * hh / Math.Max(1, vDown + TouchdownRate);
            double lim = Math.Tan(MaxTilt) * Math.Max(av, g * 0.5);
            // Цель уже не догнать — гасить горизонталь: иначе ZEM разгоняет вбок и ступень касается с 40+ м/с сбоку
            // (замер: 44 м/с горизонтали на 20 м при промахе 1 км).
            double tStop = vh.magnitude / Math.Max(0.1, lim);
            Vector3d ah = tgo > 2.5 + 1.5 * tStop ? (-dx - vh * tgo) * (6 / (tgo * tgo)) + vh * (2 / tgo) : -vh * 1.5;
            if (ah.magnitude > lim) ah = ah.normalized * lim;
            var acc = up * Math.Max(av, 0.1) + ah;
            double sp = vs.magnitude;
            if (sp > PredictiveSpeed && vDown > 0)
            {
                // Быстрый участок: тяга против скорости (гасит и горизонталь), увод — по прогнозу точки остановки.
                if (t >= nextPredict)
                {
                    nextPredict = t + 0.2;
                    PlanLanding(t, r, v.Velocity, mass, hBottom, true);
                }
                double cosV = Math.Max(0.3, vDown / sp);
                var corr = -landMiss * (6 / Math.Max(4, landTgo * landTgo));
                corr -= up * Vector3d.Dot(corr, up);
                acc = -vs.normalized * (Math.Max(av, 0.1) / cosV) + corr;
            }
            // На напоре ось держится у потока: поперечная нагрузка q·sin α ломает длинный корпус (FlightPhysics.QAlphaLimit).
            var retro = vs.sqrMagnitude > 1 ? -vs.normalized : up;
            double q = v.DynamicPressure;
            double maxOff = q > 1 ? Math.Asin(Math.Min(1, 0.5 * FlightPhysics.QAlphaLimit / q)) : Math.PI;
            double off = Math.Acos(MathD.Clamp(Vector3d.Dot(acc.normalized, retro), -1, 1));
            if (off > maxOff)
            {
                var perp = acc.normalized - retro * Math.Cos(off);
                acc = (retro * Math.Cos(maxOff) + perp.normalized * Math.Sin(maxOff)) * acc.magnitude;
            }
            FlightControl.PointAt(v, acc.normalized);
            // Тяжёлый возврат садится «на трёх»: когда одного хватает с запасом, два глушатся (как у Falcon 9).
            if (v.EngineLimit > Def.LandingEngines && sp < PredictiveSpeed)
            {
                double one = v.Design.Sections[section].Engine.Thrust(v.StaticPressure) * Def.LandingEngines;
                if (acc.magnitude * mass < 0.8 * one)
                {
                    v.EngineLimit = Def.LandingEngines;
                    v.Raise($"{v.Name}: посадка на {Def.LandingEngines} дв.");
                }
            }
            double thrust = v.EngineThrustLimit(v.StaticPressure, section);
            double cos = Math.Max(0.5, Vector3d.Dot(v.NoseP, acc.normalized));
            double thr = thrust > 0 ? acc.magnitude * mass / (thrust * cos) : 1;
            FlightControl.Ignite(v, MathD.Clamp(thr, 0.01, 1));
            if (!legs && hh < LegLeadTime * Math.Max(vDown, TouchdownRate))
            {
                legs = true;
                v.ExtendLegs();
            }
            Status = $"посадка: h {hh:F0} м, снижение {vDown:F1} м/с, до цели {dx.magnitude:F0} м";
        }

        // ---------------------------------------------------------------- прогноз точки падения

        /// <summary>
        /// Промах точки падения на настил (инерциальный вектор в местной горизонтали цели, м): точечная масса,
        /// центральное поле и сопротивление хвостом вперёд по той же cd(Mach), что FlightPhysics.Drag, во вращающейся
        /// атмосфере. entryBurn — с номинальным входным импульсом EntryDv (для решения о разворотном импульсе).
        /// Посадочный импульс сдвигает точку на десятки метров — их добирает боковой увод на посадке.
        /// </summary>
        public Vector3d Predict(double t0, Vector3d r, Vector3d u, double mass, bool entryBurn, out double tImpact)
        {
            var v = Vessel;
            var body = v.Body;
            var s = v.Design.Sections[section];
            var atm = body.Atmosphere;
            var tgtBf = TargetBodyFixed();
            double floor = tgtBf.magnitude + s.Length * s.ComFraction * 0.5;
            Aero(s, out double area, out double cdFixed);
            double dvLeft = entryBurn ? Def.EntryDv : 0;
            double finCdA = v.GridFinCdA();
            int lit = Math.Min(Def.BurnEngines, s.EngineCount);
            double mdot = s.Engine.MassFlow * lit;
            double t = t0;
            tImpact = t0;
            for (int i = 0; i < 20000; i++)
            {
                double alt = r.magnitude - body.Radius;
                double dt = alt > 40000 ? 1.0 : alt > 8000 ? 0.5 : 0.25;
                bool burn = dvLeft > 0 && alt < Def.EntryAltitude && Vector3d.Dot(u, r) < 0;
                var spin = FlightPhysics.SpinAxis(body, t);
                var a = PredictAccel(body, atm, spin, r, u, mass, area, cdFixed, finCdA, burn ? s.Engine : null, lit, out double thrustAcc);
                var rNext = r + u * dt + a * (0.5 * dt * dt);
                var a2 = PredictAccel(body, atm, spin, rNext, u + a * dt, mass, area, cdFixed, finCdA, burn ? s.Engine : null, lit, out _);
                var uNext = u + (a + a2) * (0.5 * dt);
                if (burn)
                {
                    dvLeft -= thrustAcc * dt;
                    mass -= mdot * dt;
                }
                if (rNext.magnitude <= floor)
                {
                    double f = (r.magnitude - floor) / Math.Max(1e-6, r.magnitude - rNext.magnitude);
                    r += (rNext - r) * f;
                    t += dt * f;
                    break;
                }
                r = rNext;
                u = uNext;
                t += dt;
            }
            tImpact = t;
            var bf = body.OrientationAt(t).Inverse * r;
            var d = bf.normalized * tgtBf.magnitude - tgtBf;
            return body.OrientationAt(t0) * d;
        }

        /// <summary>
        /// Обтекание для прогноза — как в FlightPhysics.Drag: хвостом вперёд cd(Mach)·πR², «брюхом» (α = 90°)
        /// 1,2·боковая площадь L·D·0,8 без зависимости от Маха.
        /// </summary>
        void Aero(SectionDef s, out double area, out double cdFixed)
        {
            bool belly = Def.BellyFlop && Phase != PhaseType.Landing && Phase != PhaseType.Flop;
            area = belly ? s.Length * s.Diameter * 0.8 : Math.PI * s.Radius * s.Radius;
            cdFixed = belly ? 1.2 : 0;
        }

        /// <summary>Торможение воздухом сейчас, м/с² (для посадочного закона).</summary>
        double DragAccel(Vector3d r, Vector3d vs, double mass)
        {
            var body = Vessel.Body;
            var atm = body.Atmosphere;
            double alt = r.magnitude - body.Radius, sp = vs.magnitude;
            if (atm == null || alt >= atm.Top || sp < 1e-3) return 0;
            atm.Sample(alt, out _, out double rho, out double temp);
            Aero(Vessel.Design.Sections[section], out double area, out double cdFixed);
            double cd = cdFixed > 0 ? cdFixed : FlightPhysics.CdAt(sp / atm.SpeedOfSound(temp));
            return 0.5 * rho * sp * sp * (cd * area + Vessel.GridFinCdA()) / mass;
        }

        static Vector3d PredictAccel(CelestialBody body, Atmosphere atm, Vector3d spin, Vector3d r, Vector3d u, double mass,
            double area, double cdFixed, double finCdA, EngineDef engine, int lit, out double thrustAcc)
        {
            double rm = r.magnitude;
            var a = r * (-body.Mu / (rm * rm * rm));
            thrustAcc = 0;
            double alt = rm - body.Radius;
            var vAir = u - Vector3d.Cross(spin, r);
            double sp = vAir.magnitude;
            double p = 0;
            if (atm != null && alt < atm.Top && sp > 1e-3)
            {
                atm.Sample(alt, out p, out double rho, out double temp);
                double cd = cdFixed > 0 ? cdFixed : FlightPhysics.CdAt(sp / atm.SpeedOfSound(temp));
                a -= vAir * (0.5 * rho * sp * (cd * area + finCdA) / mass);
            }
            if (engine != null && sp > 1e-3)
            {
                thrustAcc = engine.Thrust(p) * lit / mass;
                a -= vAir * (thrustAcc / sp);
            }
            return a;
        }
    }
}
