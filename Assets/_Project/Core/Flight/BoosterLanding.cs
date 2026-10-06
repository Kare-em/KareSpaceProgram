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
        /// Ловля башней (Super Heavy, корабль; Starbase): опор нет — держат «палочки» башни на оси цели. Контактная модель
        /// (TowerCatch.Step): корпус опускается между разведёнными палочками, башня их сводит, цапфы ложатся на балки с
        /// проверкой снижения, скольжения и наклона. Мимо палочек — падение на сложенные опоры (FlightPhysics.CheckContact).
        /// </summary>
        public bool TowerCatch;
        /// <summary>
        /// Высота палочек над грунтом на оси ловли, м (цапфы ложатся на неё), и цапфы ниже носа, м.
        /// Палочки ждут взведёнными на ArmHeight и после ловли каретка опускает борт до ArmLowered (TowerCatch.Lower).
        /// Пара: опущенный корпус — днище на ArmLowered − (длина − PinsFromTop) — над столом на TowerCatch.HangClearance.
        /// </summary>
        public double ArmHeight, ArmLowered, PinsFromTop;
        /// <summary>Δv посадочного импульса с запасом, м/с: столько топлива входной импульс обязан оставить.</summary>
        public double LandingDv = 600;
        /// <summary>Где цель: Fixed — TargetLat/Lon (пресеты); LaunchSite и Downrange — найдёт пилот после отделения.</summary>
        public RecoveryTarget Mode = RecoveryTarget.Fixed;

        public RecoveryDef Clone() => (RecoveryDef)MemberwiseClone();

        /// <summary>
        /// Возврат ступени своего корабля (§6.9, выбор в ангаре — CraftPart.Landing): секция с двигателем и опорами.
        /// Двигателей на импульсы — треть связки, на посадку — девятая часть (Falcon 9: 3 и 1 из 9, Super Heavy: 11 и 3
        /// из 33). Закрылки — вход «брюхом». Запас топлива — по Циолковскому на посадку, вход и (к старту) разворот.
        /// </summary>
        public static RecoveryTarget DefaultFor(SectionDef s, bool booster) =>
            // По умолчанию — под трассу, но только нижняя ступень и боковые блоки без экипажа: верхняя с опорами —
            // обычно посадочная, и её запас (HeldReserve) держался бы весь полёт до Луны.
            booster && s.HasEngine && s.Kind != SectionKind.Capsule && s.Crew == 0 ? RecoveryTarget.Downrange : RecoveryTarget.None;

        public static RecoveryDef ForCraft(SectionDef s, RecoveryTarget mode)
        {
            int n = Math.Max(1, s.EngineCount);
            var r = new RecoveryDef
            {
                StageName = s.Name, Mode = mode,
                TargetName = mode == RecoveryTarget.LaunchSite ? "площадка у старта" : "точка под трассой",
                BurnEngines = Math.Max(1, n / 3), LandingEngines = Math.Max(1, n / 9),
                EntryDv = CraftEntryDv, BellyFlop = s.FlapArea > 0,
            };
            double dv = r.LandingDv + r.EntryDv + (mode == RecoveryTarget.LaunchSite ? CraftBoostbackDv : 0);
            r.Reserve = s.DryMass * (Math.Exp(dv / (s.Engine.IspVac * Constants.G0)) - 1);
            return r;
        }

        /// <summary>
        /// Δv на вход и разворот к старту для своего корабля, м/с (у пресетов — по миссии). Пара: Falcon 9 гасит входом
        /// ≈ 1000 м/с с 2,25 км/с; разворот RTLS — ≈ 1,5 км/с при отделении на ≈ 2 км/с. Больше — ступень несёт лишнее.
        /// </summary>
        public const double CraftEntryDv = 600, CraftBoostbackDv = 1500;
        /// <summary>Площадка посадки у старта — севернее стола, м: не в башню и не в газоотвод (LZ-1 у Канаверала ≈ 9 км).</summary>
        public const double LandingZoneOffset = 600;
    }

    /// <summary>Цель возврата ступени: Fixed — задана пресетом, LaunchSite — у старта (RTLS), Downrange — куда падает.</summary>
    public enum RecoveryTarget { None = 0, LaunchSite = 1, Downrange = 2, Fixed = 3 }

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
        /// <summary>
        /// Хвост входного импульса, м перелёта: ближе — на LandingEngines и прогноз каждый шаг физики. 13 Raptor Super
        /// Heavy двигают точку на ≈ 3,3 км/с, прогноз раз в 0,1 с проскакивал отсечку на 349 м — и посадку с таким
        /// промахом боковой увод (MaxTilt) уже не вытягивал. Пара: FlightPhysics.MaxStep.
        /// </summary>
        const double EntryFineAlong = 3000;
        /// <summary>
        /// Вход «брюхом»: доля поперечного сопротивления sin²α. Прогнозы до аэроучастка (разворот, планировщик схода) берут
        /// номинал, а на аэроучастке она — руль дальности: меньше α — меньше торможение, дальше полёт (§6.9). Импульс
        /// двигателем с орбиты точнее ≈ 0,3 м/с не отсекается, а это километры по трассе — их добирает этот руль.
        /// Пара: номинал внутри [BellyShareMin, 1] — запас в обе стороны.
        /// </summary>
        const double BellyShareNominal = 0.8, BellyShareMin = 0.45;
        /// <summary>Пересчёт угла атаки на входе «брюхом», с (два прогноза на шаг).</summary>
        const double BellyGuidePeriod = 0.5;
        /// <summary>
        /// Напор, Па, до которого угол атаки «брюхом» держится по команде, и выше которого щитки сами кладут корпус поперёк
        /// потока. Замер starship_catch: α 60° держится до q ≈ 4,7 кПа (64 км), при 6,5 кПа уже 74°, при 12,8 — 84°,
        /// выше 18 кПа — 86–87°. Прогноз без этого считал команду до земли и недолёт копился до 36 км. Между порогами —
        /// линейный переход к доле 1. Пара: Panels корабля (StarshipShip) — другие щитки, другие пороги.
        /// </summary>
        const double BellyHoldQ = 4500, BellyLostQ = 10000;
        /// <summary>
        /// Заход к палочкам (§6.9): днище уходит ниже балок за (длина − PinsFromTop) до касания цапф, а щель разведённых
        /// палочек пропускает корпус лишь при смещении поперёк до ≈ 9,7 м (TowerCatch.ArmGapOpen). ZEM/ZEV с временем до
        /// касания 2–3 с такой промах не выбирает (Play 06.10.2026: корабль пришёл на −18,2 м поперёк — «Сорвалась»).
        /// Поэтому: над воротами (днище на CatchGateMargin выше балок) — снижение не быстрее CatchSlowRate, в полосе
        /// CatchHoldBand над воротами — зависание, пока смещение больше CatchAlignTol (поперёк) / CatchAlongTol (вдоль)
        /// и боковая скорость больше CatchDriftTol; ниже ворот — TouchdownRate (балки сходятся за 2,5 с).
        /// Боковой увод тут — ПД-регулятор CatchKp/CatchKd (ω ≈ 0,3 рад/с, ζ ≈ 0,9). Пара: TowerCatch.CatchSinkLimit 6 >
        /// CatchSlowRate; ArmPinOut 1,5 − 0,2 > CatchAlignTol − сходящиеся балки дотягивают остальное.
        /// </summary>
        const double CatchGateMargin = 12, CatchSlowBand = 150, CatchHoldBand = 10, CatchSlowRate = 5;
        /// <summary>Гасить двигатель на заходе, когда нужная тяга ниже этой доли минимальной (запас регулятору вниз).</summary>
        const double CatchEngineMargin = 1.1, CatchEngineReserve = 1.4;
        /// <summary>Ниже высоты зависания — подъём к ней, 1/с: √(2k·h) с k Super Heavy подбрасывал его на 3 м/с.</summary>
        const double CatchHoldGain = 0.4;
        const double CatchAlignTol = 2, CatchAlongTol = 6, CatchDriftTol = 0.6, CatchKp = 0.09, CatchKd = 0.55;

        double nextPredict, entryAlong = double.NaN, entryCross;
        double bellyShare = BellyShareNominal;
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

        /// <summary>Отложенная посадка вернулась (Universe.ResumeRecovery): отметки времени пилота — на длину паузы.</summary>
        internal void ShiftTime(double dt) => nextPredict += dt;

        /// <summary>Отделился борт из одной секции с RecoveryDef и топливом — взвести и начать возврат.</summary>
        public static BoosterLandingAutopilot TryStart(Vessel d, Vessel parent = null)
        {
            int idx = -1, n = 0;
            for (int i = 0; i < d.Attached.Length; i++)
                if (d.Attached[i]) { n++; idx = i; }
            if (n != 1) return null;
            var s = d.Design.Sections[idx];
            if (s.Recovery == null || s.Recovery.Mode == RecoveryTarget.None || !s.HasEngine || d.Propellant[idx] <= 0 || d.IsLanded) return null;
            // Split пометил её обломком и разоружил (§5): это управляемая ступень, а не мусор.
            d.IsDebris = false;
            d.Armed[idx] = true;
            d.Sas = SasMode.Off;
            d.Name = s.Recovery.StageName ?? s.Name;
            var def = s.Recovery;
            if (def.Mode != RecoveryTarget.Fixed)
            {
                // Своя ступень (§6.9): цель — у старта (площадка севернее стола) или там, куда падает (найдёт Update).
                def = def.Clone();
                var site = d.Site ?? parent?.Site;
                if (def.Mode == RecoveryTarget.LaunchSite && site != null)
                {
                    def.TargetLat = site.Latitude + LandingZoneDeg(d.Body);
                    def.TargetLon = site.Longitude;
                }
                else def.Mode = RecoveryTarget.Downrange;
            }
            d.Raise($"{d.Name}: возврат на «{def.TargetName}», запас {d.Propellant[idx] / 1000:F1} т");
            return new BoosterLandingAutopilot(d, def, idx);
        }

        static double LandingZoneDeg(CelestialBody b) => RecoveryDef.LandingZoneOffset / b.Radius * Constants.Rad2Deg;

        /// <summary>
        /// Цель «куда падает» (RecoveryTarget.Downrange): точка падения по прогнозу со входным импульсом. На орбите (перицентр
        /// выше атмосферы, у безвоздушного — выше поверхности) падать некуда — пилот снимается.
        /// </summary>
        bool ResolveDownrange(double t)
        {
            var v = Vessel;
            var body = v.Body;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, body.Mu, t);
            if (orbit.E < 1 && orbit.PeriapsisRadius > body.Radius + (body.Atmosphere?.Top ?? 0) + 1000)
            {
                Abort("на орбите: точки падения нет");
                v.Raise($"{v.Name}: возврат отменён — ступень на орбите");
                return false;
            }
            var bf = body.OrientationAt(t).Inverse * v.Position;
            CelestialBody.BodyFixedToLatLon(bf.normalized, out Def.TargetLat, out Def.TargetLon);
            v.MassProperties(out double mass, out _, out _, out _);
            for (int k = 0; k < 2; k++)
            {
                var miss = Predict(t, v.Position, v.Velocity, mass, Def.EntryDv > 0, out _);
                var tgt = TargetBodyFixed() + body.OrientationAt(t).Inverse * miss;
                CelestialBody.BodyFixedToLatLon(tgt.normalized, out Def.TargetLat, out Def.TargetLon);
            }
            Def.Mode = RecoveryTarget.Fixed;
            return true;
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
        /// Возврат с орбиты (корабль starship_catch, §6.9): после тормозного импульса автопилота миссии пилот начинает с
        /// переворота — промах прогноза больше BoostbackTolerance доправляется импульсом в вакууме (боковое смещение плоскости
        /// и дальность). engage = false — только прогноз (планирование схода): борт не трогаем, пилота в мир не отдаём.
        /// </summary>
        public static BoosterLandingAutopilot StartReturn(Vessel v, bool engage = true)
        {
            int idx = -1, n = 0;
            for (int i = 0; i < v.Attached.Length; i++)
                if (v.Attached[i]) { n++; idx = i; }
            if (n != 1) return null;
            var s = v.Design.Sections[idx];
            if (s.Recovery == null || !s.HasEngine) return null;
            if (engage)
            {
                v.Armed[idx] = true;
                v.Sas = SasMode.Off;
                v.Raise($"{v.Name}: возврат к «{s.Recovery.TargetName}», топливо {v.Propellant[idx] / 1000:F1} т");
            }
            return new BoosterLandingAutopilot(v, s.Recovery, idx);
        }

        /// <summary>
        /// Одиночная секция с входом «брюхом» (RecoveryDef.BellyFlop) или с закрылками (SectionDef.FlapArea): закрылки держат
        /// её поперёк потока, поэтому лимит q·α длинного пакета (FlightPhysics.CheckStructure) к ней не применяется — иначе
        /// ломалась бы на α 90° сразу.
        /// </summary>
        public static bool BellyEntry(Vessel v)
        {
            int n = 0;
            SectionDef s = null;
            for (int i = 0; i < v.Attached.Length; i++)
                if (v.Attached[i]) { n++; s = v.Design.Sections[i]; }
            return n == 1 && (s.FlapArea > 0 || s.Recovery != null && s.Recovery.BellyFlop);
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

        /// <summary>
        /// Центр настила в осях тела, м от центра. У ловли башней «настил» — уровень днища, когда цапфы ложатся на
        /// палочки: пилот касается его на TouchdownRate, и цапфы садятся на балки с той же скоростью (TowerCatch.Step).
        /// </summary>
        public Vector3d TargetBodyFixed()
        {
            var body = Vessel.Body;
            var dir = CelestialBody.LatLonToBodyFixed(Def.TargetLat, Def.TargetLon);
            double ground = Def.AtSea ? 0 : body.SurfaceHeight(dir);
            double deck = Def.DeckHeight;
            if (Def.TowerCatch && Def.ArmHeight > 0)
            {
                Vessel.MassProperties(out _, out _, out double len, out _);
                deck = Def.ArmHeight - (len - Def.PinsFromTop);
            }
            return dir * (body.Radius + ground + deck);
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
            if (Def.Mode == RecoveryTarget.Downrange && !ResolveDownrange(t)) return;
            var tgtBf = TargetBodyFixed();
            if (v.IsLanded)
            {
                FlightControl.Cutoff(v);
                v.EngineLimit = 0;
                TouchdownSpeed = lastSpeed;
                var bf = o.Inverse * v.Position;
                Miss = (bf.normalized - tgtBf.normalized).magnitude * tgtBf.magnitude;
                Phase = PhaseType.Done;
                Status = v.TowerCaught
                    ? $"поймана палочками: {TouchdownSpeed:F1} м/с, {Miss:F0} м от оси «{Def.TargetName}»"
                    : $"села: {TouchdownSpeed:F1} м/с, {Miss:F0} м от центра «{Def.TargetName}»";
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
                // Промах раскладывается в горизонтали цели (TargetAxes): с орбиты цель в четверти витка, и оси борта к её
                // горизонтали почти поперёк — матрица вырождалась, импульс кончался с промахом 44 км.
                TargetAxes(t, r, v.Velocity, out var f1, out var f2);
                const double probe = 5;
                var m0 = Predict(t, r, v.Velocity, mass, true, out _);
                var m1 = Predict(t, r, v.Velocity + e1 * probe, mass, true, out _);
                var m2 = Predict(t, r, v.Velocity + e2 * probe, mass, true, out _);
                double a = Vector3d.Dot(m1 - m0, f1) / probe, b = Vector3d.Dot(m2 - m0, f1) / probe;
                double c = Vector3d.Dot(m1 - m0, f2) / probe, d = Vector3d.Dot(m2 - m0, f2) / probe;
                double det = a * d - b * c;
                double x = -Vector3d.Dot(m0, f1), y = -Vector3d.Dot(m0, f2);
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

        /// <summary>
        /// Горизонталь цели в момент t (оси, в которых Predict отдаёт промах): f1 — по трассе (поперёк f2), f2 — проекция
        /// нормали орбиты (r × u).
        /// </summary>
        public void TargetAxes(double t, Vector3d r, Vector3d u, out Vector3d f1, out Vector3d f2)
        {
            var up = (Vessel.Body.OrientationAt(t) * TargetBodyFixed()).normalized;
            f2 = Vector3d.ProjectOnPlane(Vector3d.Cross(r, u), up).normalized;
            f1 = Vector3d.Cross(f2, up);
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
                nextPredict = t + (entryAlong < EntryFineAlong ? 0 : 0.1);
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
            if (entryAlong < EntryFineAlong && Def.LandingEngines < Def.BurnEngines) v.EngineLimit = Def.LandingEngines;
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
                // Вход «брюхом»: ось почти поперёк потока, нос вперёд по трассе. Угол атаки — руль дальности (GuideBelly):
                // доля боковой площади в сопротивлении — sin²α (FlightPhysics.Drag).
                if (t >= nextPredict)
                {
                    nextPredict = t + BellyGuidePeriod;
                    GuideBelly(t, r, mass);
                }
                Frame(r, vs, out var e1, out _);
                var vh = vs.normalized;
                var belly = (e1 - vh * Vector3d.Dot(e1, vh)).normalized;
                if (belly.sqrMagnitude < 0.5) belly = e1;
                // Брюхо (+X) — навстречу потоку, иначе α 90° набегает на бок (крен прежним PointAt не держался).
                FlightControl.PointAt(v, (belly * Math.Sqrt(bellyShare) + vh * Math.Sqrt(1 - bellyShare)).normalized, -vh);
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
        /// Угол атаки на входе «брюхом»: секущая по доле sin²α на промах по трассе (два прогноза), в [BellyShareMin, 1].
        /// Вбок рулить нечем — поперёк промах снимает разворотный импульс в вакууме.
        /// </summary>
        void GuideBelly(double t, Vector3d r, double mass)
        {
            var u = Vessel.Velocity;
            TargetAxes(t, r, u, out var f1, out _);
            double s0 = bellyShare;
            var m0 = Predict(t, r, u, mass, false, out _);
            double a0 = Vector3d.Dot(m0, f1);
            double s1 = s0 > BellyShareMin + 0.1 ? s0 - 0.1 : s0 + 0.1;
            bellyShare = s1;
            double a1 = Vector3d.Dot(Predict(t, r, u, mass, false, out _), f1);
            bellyShare = s0;
            double k = (a1 - a0) / (s1 - s0);
            if (Math.Abs(k) > 1) bellyShare = MathD.Clamp(s0 - a0 / k, BellyShareMin, 1);
            Status = $"вход брюхом: α {Math.Asin(Math.Sqrt(bellyShare)) * Constants.Rad2Deg:F0}°, промах {m0.magnitude:F0} м, по трассе {a0:F0} м";
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
                var a = PredictAccel(body, atm, spin, r, u, mass, area, 0, cdFixed, finCdA, null, 0, out _);
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
            var tgt = o * tgtBf;
            var dx = r - tgt;
            dx -= up * Vector3d.Dot(dx, up);
            var vh = vs - up * Vector3d.Dot(vs, up);
            // Заход к палочкам: снижение придерживается, пока корпус не встал над щелью (CatchGateMargin).
            bool catchZone = false, onCurve = true;
            double across = 0, along = 0;
            if (Def.TowerCatch && Def.ArmHeight > 0)
            {
                v.MassProperties(out _, out _, out double len, out _);
                double gate = len - Def.PinsFromTop + CatchGateMargin;
                if (hh < gate + CatchSlowBand)
                {
                    catchZone = true;
                    TowerCatch.Frame(v.Body, Def, out _, out var ex, out _, out var ez);
                    across = Vector3d.Dot(dx, o * ez);
                    along = Vector3d.Dot(dx, o * ex);
                    bool aligned = Math.Abs(across) < CatchAlignTol && Math.Abs(along) < CatchAlongTol && vh.magnitude < CatchDriftTol;
                    // Не над щелью — тормозить по тому же профилю к высоте зависания над воротами (ступенька vt = 0 в полосе
                    // проскакивалась: корабль вошёл в неё на 10,8 м/с и прошёл ворота со смещением 12 м — «удар о палочки»).
                    // Ниже ворот профиль сам сводит снижение к TouchdownRate у цапф: зависать там — жечь остаток топлива.
                    // Над воротами — тот же профиль √(v² + 2k·h), но к CatchSlowRate на воротах: ступенька «не быстрее
                    // CatchSlowRate» тормозила Super Heavy с 44 м/с за 2 с и перекидывала вверх (замер 06.10.2026).
                    double dg = hh - gate;
                    if (dg >= 0)
                    {
                        double dh = dg - CatchHoldBand * 0.5;
                        vt = aligned ? Math.Min(vt, Math.Sqrt(CatchSlowRate * CatchSlowRate + 2 * k * dg))
                            : Math.Min(vt, dh > 0 ? Math.Sqrt(2 * k * dh) : dh * CatchHoldGain);
                        onCurve = aligned || dh > 0;
                    }
                    else
                    {
                        onCurve = vt < CatchSlowRate;
                        vt = Math.Min(vt, CatchSlowRate);
                    }
                }
            }
            double aDrag = DragAccel(r, vs, mass);
            // Упреждение — торможение, которого требует сама цель: на профиле √(c + 2kh) это k · vDown / vt (= k на профиле),
            // на постоянной цели — ноль. Полное k у палочек гасило команду на спуск: k ≈ 8 м/с² = RateGain · vt, ступень
            // висела при av = g 60 с до сухих баков (starship_catch); k · vDown / vt на постоянных 5 м/с ниже ворот давало
            // равновесие 1,7 м/с, и Super Heavy с 79 т топлива не дотягивал до цапф (starship, замеры 06.10.2026).
            double kff = !catchZone ? k : onCurve ? k * MathD.Clamp(vDown / Math.Max(1, vt), 0, 1) : 0;
            double av = g + kff + RateGain * (vDown - vt) - aDrag * Math.Max(0, vDown) / Math.Max(1, vs.magnitude);

            // Боковой увод к центру палубы (ZEM/ZEV): время до касания — по средней скорости снижения.
            double tgo = 2 * hh / Math.Max(1, vDown + TouchdownRate);
            double lim = Math.Tan(MaxTilt) * Math.Max(av, g * 0.5);
            // Цель уже не догнать — гасить горизонталь: иначе ZEM разгоняет вбок и ступень касается с 40+ м/с сбоку
            // (замер: 44 м/с горизонтали на 20 м при промахе 1 км).
            double tStop = vh.magnitude / Math.Max(0.1, lim);
            Vector3d ah = catchZone ? -dx * CatchKp - vh * CatchKd
                : tgo > 2.5 + 1.5 * tStop ? (-dx - vh * tgo) * (6 / (tgo * tgo)) + vh * (2 / tgo) : -vh * 1.5;
            if (ah.magnitude > lim) ah = ah.normalized * lim;
            // Наклон тяги — не больше MaxTilt и при малой вертикальной команде: на зависании у палочек av падал до 0,1,
            // и ah ≈ 1 м/с² клал Super Heavy на 40° (замер starship_catch 06.10.2026).
            // Вертикаль для наклона — не ниже минимального газа: двигатель всё равно даст столько, и ось, наклонённая под
            // крошечную команду, превращала избыток тяги в боковую — Super Heavy с 79 т уходил на 25° и 50 м вбок от щели
            // (starship, замер 06.10.2026). Пара: EngineLimit · MinThrottle — та же нижняя граница, что у газа ниже.
            double avTilt = av;
            if (catchZone)
            {
                var eng = v.Design.Sections[section].Engine;
                avTilt = Math.Max(av, v.EngineLimit * eng.MinThrottle * eng.Thrust(v.StaticPressure) / mass);
                ah = ah.normalized * Math.Min(ah.magnitude, Math.Tan(MaxTilt) * avTilt);
            }
            // Команда без нижней границы газа — по ней решается, гасить ли лишний двигатель у палочек.
            double accWant = (up * Math.Max(av, 0.1) + ah).magnitude;
            var acc = up * Math.Max(avTilt, 0.1) + ah;
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
            // Напор ниже 0,5 · QAlphaLimit корпус не ломает при любом угле — предела нет. Asin(Min(1, …)) давал там 90°, и при
            // медленном подъёме (поток сверху) тяга укладывалась горизонтально: Super Heavy у палочек опрокидывался
            // (starship, замер 06.10.2026: ось к вертикали 0,12 при q ≈ 15 Па).
            double maxOff = q > 0.5 * FlightPhysics.QAlphaLimit ? Math.Asin(0.5 * FlightPhysics.QAlphaLimit / q) : Math.PI;
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
            // Заход к палочкам требует висеть, а 3 Raptor на MinThrottle (3 × 0,4 × 2,26 МН ≈ 2,7 МН) тянут больше пустого
            // Super Heavy (≈ 240 т, 2,35 МН): ступень уходила вверх и кренилась (замер starship_catch 06.10.2026) — гасим по одному.
            if (catchZone && v.EngineLimit > 1)
            {
                var eng = v.Design.Sections[section].Engine;
                // Оставшиеся на полном газе обязаны держать вес с запасом CatchEngineReserve: один Raptor (2,26 МН) пустой
                // Super Heavy (2,35 МН) не держит — гасили до одного, и ступень проваливалась на палочки на 9 м/с (замер).
                double each = eng.Thrust(v.StaticPressure);
                if (accWant * mass < CatchEngineMargin * eng.MinThrottle * each * v.EngineLimit
                    && (v.EngineLimit - 1) * each > CatchEngineReserve * mass * g)
                {
                    v.EngineLimit--;
                    v.Raise($"{v.Name}: заход к палочкам на {v.EngineLimit} дв.");
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
            Status = catchZone
                ? $"заход к палочкам: h {hh:F0} м, снижение {vDown:F1} м/с, поперёк {across:F1} м, вдоль {along:F1} м"
                : $"посадка: h {hh:F0} м, снижение {vDown:F1} м/с, до цели {dx.magnitude:F0} м";
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
            // Брюхом на большом напоре корпус ложится поперёк сам (BellyHoldQ) — там площадь боковая, а не по команде.
            double areaBroad = cdFixed > 0 ? s.Length * s.Diameter * 0.8 : 0;
            double dvLeft = entryBurn ? Def.EntryDv : 0;
            double finCdA = v.GridFinCdA();
            int lit = Math.Min(Def.BurnEngines, s.EngineCount);
            double mdot = s.Engine.MassFlow * lit;
            double t = t0;
            tImpact = t0;
            // Вакуумную дугу — по Кеплеру сразу до верха атмосферы: физика считает то же точечное тяготение, а с орбиты
            // численно это ≈ 3000 шагов на прогноз, и разворотный импульс (3 прогноза за 0,02 с) тормозил кадр.
            // Только без входного импульса: он начинается ниже EntryAltitude, но привязан к нисходящей ветви.
            double top = body.Radius + (atm?.Top ?? 0);
            if (dvLeft <= 0 && r.magnitude > top + VacuumJump)
            {
                var o = KeplerOrbit.FromState(r, u, body.Mu, t0);
                double tTop = o.NextTimeAtRadius(top + VacuumJump, t0, false);
                if (!double.IsNaN(tTop))
                {
                    o.GetState(tTop, out r, out u);
                    t = tTop;
                }
            }
            for (int i = 0; i < 20000; i++)
            {
                double alt = r.magnitude - body.Radius;
                double dt = alt > 40000 ? 1.0 : alt > 8000 ? 0.5 : 0.25;
                bool burn = dvLeft > 0 && alt < Def.EntryAltitude && Vector3d.Dot(u, r) < 0;
                var spin = FlightPhysics.SpinAxis(body, t);
                var a = PredictAccel(body, atm, spin, r, u, mass, area, areaBroad, cdFixed, finCdA, burn ? s.Engine : null, lit, out double thrustAcc);
                var rNext = r + u * dt + a * (0.5 * dt * dt);
                var a2 = PredictAccel(body, atm, spin, rNext, u + a * dt, mass, area, areaBroad, cdFixed, finCdA, burn ? s.Engine : null, lit, out _);
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

        /// <summary>Над верхом атмосферы, м, где кончается кеплеров прыжок прогноза (численно — последние километры).</summary>
        const double VacuumJump = 1000;

        /// <summary>
        /// Обтекание для прогноза — как в FlightPhysics.Drag: хвостом вперёд cd(Mach)·πR², «брюхом» (α = 90°)
        /// 1,2·боковая площадь L·D·0,8 без зависимости от Маха.
        /// </summary>
        void Aero(SectionDef s, out double area, out double cdFixed)
        {
            bool belly = Def.BellyFlop && Phase != PhaseType.Landing && Phase != PhaseType.Flop;
            // Брюхом — смесь боковой и лобовой площади по sin²α (как FlightPhysics.Drag; лобовую — с тем же cd, она мала).
            area = belly ? s.Length * s.Diameter * 0.8 * bellyShare + Math.PI * s.Radius * s.Radius * (1 - bellyShare)
                         : Math.PI * s.Radius * s.Radius;
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
            double area, double areaBroad, double cdFixed, double finCdA, EngineDef engine, int lit, out double thrustAcc)
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
                double ar = area;
                if (areaBroad > 0)
                    ar += (areaBroad - area) * MathD.Clamp((0.5 * rho * sp * sp - BellyHoldQ) / (BellyLostQ - BellyHoldQ), 0, 1);
                a -= vAir * (0.5 * rho * sp * (cd * ar + finCdA) / mass);
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
