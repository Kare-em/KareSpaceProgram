using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Полётная физика активного корабля (GDD §4): RK4 по положению и скорости в невращающейся системе
    /// центра тела, тяга с учётом давления, сопротивление по Маху и углу атаки, нагрев, qα, посадка.
    /// Гравитация — только тела текущей сферы влияния: иначе рельсы и физика разойдутся.
    /// </summary>
    public static class FlightPhysics
    {
        public const double MaxStep = 0.02;

        /// <summary>Доля раскрытой площади купола через t секунд после ввода.</summary>
        public static double ChuteFraction(double t)
        {
            if (t < 1) return ChuteReefFraction * t;
            if (t < 1 + ChuteReefTime) return ChuteReefFraction;
            double k = Math.Min(1, (t - 1 - ChuteReefTime) / ChuteFillTime);
            return ChuteReefFraction + (1 - ChuteReefFraction) * k * k;
        }
        /// <summary>Быстрее — разбился (GDD §4.8).</summary>
        public const double CrashSpeed = 10;
        /// <summary>Порог разрушения от поперечной нагрузки: q·sin α, Па (GDD §4.7).</summary>
        public const double QAlphaLimit = 4000;
        public const double ChuteMaxQ = 25000;
        /// <summary>
        /// Автоматика ввода (GDD §4.8): взведённый парашют раскрывается ниже ChuteDeployAltitude, когда напор
        /// не выше ChuteSafeQ. Ручной ввод в нужное окно игрок пропускал: на 4× от 7 км до земли ≈ 15 с.
        /// Пара: ChuteSafeQ &lt; ChuteMaxQ — иначе купол рвётся в момент раскрытия; 7 км — высота ввода у «Востока».
        /// </summary>
        public const double ChuteDeployAltitude = 7000, ChuteSafeQ = 20000;
        /// <summary>
        /// Допустимая уставка высоты ввода (окно детали, §4.8). Низ: от ввода до полного купола 9 с рифления, на 1 км
        /// при ~100 м/с под тормозным куполом это ещё впритык; верх: выше 15 км напор у капсулы на баллистике ещё
        /// за ChuteSafeQ, ввод всё равно ждал бы напора. Пара: ChuteAltitudeMin ≤ ChuteDeployAltitude ≤ ChuteAltitudeMax.
        /// </summary>
        public const double ChuteAltitudeMin = 1000, ChuteAltitudeMax = 15000;

        /// <summary>Уставка ввода в безопасном диапазоне; 0 и меньше — штатная ChuteDeployAltitude.</summary>
        public static double ClampChuteAltitude(double altitude)
        {
            if (!(altitude > 0)) return ChuteDeployAltitude;
            return Math.Max(ChuteAltitudeMin, Math.Min(ChuteAltitudeMax, altitude));
        }
        /// <summary>
        /// Раскрытие купола с рифлением (GDD §4.8): за 1 с — 3% площади, так висит 4 с, гася скорость,
        /// затем за 4 с дораскрывается (рост площади ~t²). Мгновенный купол 600 м² на 170 м/с давал
        /// бы сотни g; с рифлением пик ~6 g (оценка интегрированием: 7 км, 170 м/с, 2,5 т).
        /// </summary>
        const double ChuteReefFraction = 0.03, ChuteReefTime = 4, ChuteFillTime = 4;
        /// <summary>Константа Саттона — Грейвса для воздуха, кг^0.5/м.</summary>
        const double SuttonGraves = 1.7415e-4;
        /// <summary>Сколько секунд суммарного перегрева выдерживает обшивка.</summary>
        public const double OverheatTolerance = 3;

        /// <summary>
        /// Правила повреждений (GDD §4.7). По умолчанию выключены: игрок учится разворачивать ракету, а не
        /// теряет её на первом же развороте. qα и нагрев считаются всегда — HUD их показывает; при выключенном
        /// правиле борт просто не разрушается. Тесты ядра включают оба: автопилоты обязаны проходить и по
        /// строгим правилам. Задаются из игры (GameBootstrap) — статика, т.к. правило одно на всю симуляцию.
        /// </summary>
        /// <summary>
        /// Продольное ускорение, которое осаживает топливо, м/с². У S-IVB и «Блока Д» двигатели осадки давали
        /// ≈ 0,01–0,1 м/с². Было 0,05: РСУ полного пакета (≈ 13 кН на 500 т = 0,026 м/с²) не осаживала никогда,
        /// и после чита «Над Луной» автопилот посадки ждал осадки до удара. Пара: Vessel.RcsThrust.
        /// </summary>
        const double SettleAccel = 0.01;

        public static bool AeroBreakup, HeatDamage;
        /// <summary>Гибель экипажа от перегрузки (§4.7): выключено — таймер упирается в предел, экипаж жив.</summary>
        public static bool GLoadLimit;

        /// <summary>
        /// Предел экипажа эры I (§4.7): дольше CrewGTime секунд выше CrewGLimit — гибель. Таймер копится только
        /// сверх предела и тает вдвое медленнее (короткий провал ниже 9 g не обнуляет накопленное).
        /// Пара: баллистический спуск «Востока» — пик ≈ 8–9 g, т.е. штатный вход проходит впритык.
        /// </summary>
        public const double CrewGLimit = 9, CrewGTime = 10;

        /// <summary>
        /// Аэродинамический момент пакета (§4.6, M2). Подъёмная сила носа по теории тонкого тела — CNα = 2 /рад
        /// на площадь миделя — приложена у торца, впереди ЦМ: ракета без стабилизаторов статически неустойчива
        /// и без управления кувыркается. Поперечное обтекание (Cd 1,2) — в середине длины, при больших углах
        /// разворачивает борт поперёк потока. Пара: StackCrossflowCd и боковая площадь — те же, что в Drag.
        /// </summary>
        public const double StackNormalSlope = 2, StackCrossflowCd = 1.2;
        /// <summary>Центр давления носа — столько радиусов от торца (≈ 2/3 конуса длиной 3R).</summary>
        const double NoseCpRadii = 2;
        /// <summary>
        /// Демпфирование по тангажу |Cmq|: момент −q·S·L·(L/2V)·Cmq·ω. Оценка для гладкого корпуса; на
        /// «Востоке» в max-Q даёт затухание ≈ 0,03 /с против раскачки ≈ 0,3 /с — неустойчивость остаётся.
        /// </summary>
        const double StackPitchDamping = 4;
        /// <summary>
        /// CNα стабилизаторов малого удлинения, 1/рад на их площадь; ЦД — FinCpHeight над низом секции.
        /// Пара: «Кара-Г» FinArea 3 м² — запас устойчивости ≈ 1 калибр против носа (CNα 2 на мидель 2,1 м²).
        /// </summary>
        public const double FinNormalSlope = 3, FinCpHeight = 0.6;

        /// <summary>Превышен ли предел поперечной нагрузки — и для разрушения, и для предупреждения HUD.
        /// Шар капсулы (длина ≤ 4 радиусов) ей не подвержен; ниже 2 кПа не ломается ничего.</summary>
        /// <summary>С какого угла атаки ступень на решётчатых рулях считается летящей хвостом вперёд, град.</summary>
        public const double GridFinStableAlpha = 170;

        public static bool QAlphaExceeded(double q, double sinA, double length, double radius) =>
            length > 4 * radius && q > 2000 && q * sinA > QAlphaLimit;

        static readonly double[] CdMach = { 0, 0.6, 0.85, 1.05, 1.2, 2, 4, 10 };
        static readonly double[] CdValue = { 0.30, 0.30, 0.45, 0.80, 0.70, 0.50, 0.35, 0.30 };

        /// <summary>Cd корпуса по Маху (та же таблица, что в Drag) — для прогнозов автопилотов (BoosterLandingAutopilot).</summary>
        public static double CdAt(double mach) => MathD.Interp(CdMach, CdValue, mach);

        /// <summary>Вектор угловой скорости вращения тела в P, рад/с.</summary>
        public static Vector3d SpinAxis(CelestialBody body, double t) =>
            (body.OrientationAt(t) * Vector3d.forward) * body.RotationRate;

        public static void Step(Vessel v, double t, double dt)
        {
            if (!v.Alive) return;
            StepDeploy(v, dt);
            if (v.IsLanded) StepLanded(v, t, dt);
            else StepFlying(v, t, dt);
        }

        static void StepDeploy(Vessel v, double dt)
        {
            var secs = v.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                var k = secs[i].Deploy;
                double target = v.DeployOn[i] ? 1 : 0, was = v.Deployed[i];
                if (k == DeployKind.None || was == target) continue;
                double step = dt / (k == DeployKind.Ramps ? RampDeployTime : k == DeployKind.Lid ? LidDeployTime
                    : k == DeployKind.Gear ? GearDeployTime : k == DeployKind.Nose ? NoseDeployTime
                    : k == DeployKind.Panels ? PanelDeployTime : LegDeployTime);
                v.Deployed[i] = target > was ? Math.Min(1, was + step) : Math.Max(0, was - step);
                if (!v.Attached[i]) continue;
                if (v.Deployed[i] == 1)
                    v.Raise(k == DeployKind.Ramps ? "Трапы на грунте" : k == DeployKind.Lid ? "Крышка открыта"
                        : k == DeployKind.Gear ? "Шасси выпущено" : k == DeployKind.Nose ? "Стыковочный узел открыт"
                        : k == DeployKind.Panels ? "Солнечные батареи раскрыты" : "Опоры выпущены");
                else if (v.Deployed[i] == 0 && k == DeployKind.Lid) v.Raise("Крышка закрыта");
                else if (v.Deployed[i] == 0 && k == DeployKind.Nose) v.Raise("Носовой обтекатель закрыт");
            }
        }

        /// <summary>Телеметрия без шага физики — для корабля на рельсах.</summary>
        public static void UpdateTelemetry(Vessel v, double t)
        {
            if (!v.Alive) return;
            UpdateAir(v, v.Body, v.Position, v.Velocity, SpinAxis(v.Body, t));
            var bf = v.Body.OrientationAt(t).Inverse * v.Position;
            v.TerrainAltitude = v.Altitude - v.Body.SurfaceHeight(bf);
            v.GForce = 0;
            v.CurrentThrust = 0;
        }

        // ---------------------------------------------------------------- на поверхности

        /// <summary>Поставить корабль на поверхность носом в зенит (стартовый стол).</summary>
        public static void PlaceOnSurface(Vessel v, CelestialBody body, double latDeg, double lonDeg, double t,
            double padHeight = 0)
        {
            v.Body = body;
            v.MassProperties(out _, out double com, out _, out _);
            var up = CelestialBody.LatLonToBodyFixed(latDeg, lonDeg);
            double h = body.SurfaceHeight(up);
            var east = Vector3d.Cross(Vector3d.forward, up).normalized;
            var north = Vector3d.Cross(up, east);
            v.AnchorBodyFixed = up * (body.Radius + h + padHeight + com);
            // Связанные оси: X — север, Y — зенит, Z — запад (в координатах U тела). Крен выбран под клавиши
            // FlightControl (D — нос к −Z): по умолчанию D уводит на восток, как в KSP (§10.1). Пара — FlightCamera
            // ставит камеру на столе лицом на север, чтобы восток был справа.
            v.AttitudeBodyFixed = QuaternionD.FromBasis(north.SwapYZ, up.SwapYZ, (-east).SwapYZ);
            v.Situation = Situation.Landed;
            UpdateLandedPose(v, t);
        }

        public static void UpdateLandedPose(Vessel v, double t)
        {
            var o = v.Body.OrientationAt(t);
            var up = v.AnchorBodyFixed.normalized;
            v.Position = o * (v.AnchorBodyFixed + up * v.Suspension);
            v.Velocity = Vector3d.Cross(SpinAxis(v.Body, t), v.Position) + o * up * v.SuspensionRate;
            v.Attitude = o.SwapYZ * v.AttitudeBodyFixed;
            v.AngularVelocity = Vector3d.zero;
        }

        /// <summary>
        /// Скорость лунохода, м/с: вторая передача «Лунохода-1» ≈ 2 км/ч. Поворот на месте — RoverTurnRate, рад/с.
        /// Пара: за шаг 0,02 с шасси сдвигается на 1 см — рельеф патча (шаг сетки ~1 м) проходится плавно.
        /// </summary>
        public const double RoverSpeed = 0.55, RoverTurnRate = 0.25;

        /// <summary>
        /// Подвеска опор и колёс (§6.3, §6.12) — пружина с демпфером по вертикали: 1,5 Гц, ζ = 0,6 (почти без отскока,
        /// как сминаемые соты опор LM и торсионы «Лунохода»). Касание на 2 м/с проседает на ≈ 0,1 м, на 6 м/с («Луна-9») —
        /// на ≈ 0,32 м и успокаивается за ≈ 0,5 с. Пара: ход SuspensionStroke 0,5 м больше просадки при CrashSpeed-касаниях
        /// посадочных скоростей; шаг физики 0,02 с ≪ 1/ω ≈ 0,1 с — явная схема устойчива (при ω·dt &gt; 0,5 подвеска замирает).
        /// </summary>
        const double SuspensionFreq = 1.5, SuspensionDamping = 0.6, SuspensionStroke = 0.5;
        /// <summary>Шасси «Лунохода-1»: полубаза 1,705/2 м и полуколея 1,60/2 м — точки, по которым берётся наклон грунта.
        /// Постоянная сглаживания наклона 0,3 с — колёса на балансирах гасят кочки, корпус не дёргается на каждом метре сетки.</summary>
        const double RoverHalfBase = 0.85, RoverHalfTrack = 0.8, RoverTiltTime = 0.3;
        /// <summary>Радиус колеса «Лунохода-1» (⌀510 мм), м. Пара: Tools/blender/lunokhod_wheels.py.</summary>
        public const double RoverWheelRadius = 0.255;
        /// <summary>
        /// Трапы «Луны-17» (§6.3): две пары рельсов по колее лунохода 1,60 м — вперёд и назад от настила. Край настила —
        /// 1,2 м от оси ступени, уклон 30° (tg = 0,577), конец рельса — RampEnd: с настила 1,9 м трап уходит на 3,3 м.
        /// Пара: модель Luna17_Ramps (Tools/blender/parts.blend, рельсы от |z| = 1,17 до 4,49 м, верх 1,94 → 0,04) —
        /// меняешь одно, правь второе. Ниже RampMinDeck луноход считается стоящим на грунте (съезжать не с чего).
        /// </summary>
        public const double RampDeckEdge = 1.2, RampSlope = 0.577, RampEnd = 4.5, RampMinDeck = 0.3;
        /// <summary>
        /// Привод раскладного (§6.12), с: опоры — пружинами после пирозамков, ≈ 2 с; трапы КТ — 4 с (длиннее и тяжелее);
        /// крышка лунохода — электроприводом, 6 с.
        /// Сложенные опоры держат касание не быстрее StowedCrashSpeed — борт садится на сопло и баки.
        /// Пара: время раскладки видно в VesselView (поворот на шарнире идёт по Vessel.Deployed).
        /// </summary>
        public const double LegDeployTime = 2, RampDeployTime = 4, LidDeployTime = 6, StowedCrashSpeed = 2;
        /// <summary>Выпуск шасси орбитера, с (у «Шаттла» ≈10 с пневмоприводом; автопилот выпускает за 100+ м до касания).</summary>
        public const double GearDeployTime = 4;
        /// <summary>Носовой обтекатель Crew Dragon (электропривод, ≈ 10 с у настоящего — у нас 6, чтобы стыковка не ждала)
        /// и раскрытие солнечных батарей на пружинах-демпферах. Пара: углы на шарнирах — VesselView.FoldHinge.</summary>
        public const double NoseDeployTime = 6, PanelDeployTime = 5;

        /// <summary>
        /// Высота рельса над грунтом под точкой d (единичный радиус в осях тела), м; 0 — рельса над грунтом нет.
        /// Настил и трапы — жёсткая геометрия в осях ступени (Vessel.RampOrigin/Axis/Up), а не профиль по грунту:
        /// колёса идут по тем рельсам, что видны в модели, даже если ступень стоит на склоне (обе пары симметричны).
        /// </summary>
        static double RampAbove(Vessel v, Vector3d d)
        {
            double ground = v.Body.Radius + v.Body.SurfaceHeight(d);
            var side = Vector3d.Cross(v.RampUp, v.RampAxis);
            var rel = d * ground - v.RampOrigin;
            double z = Vector3d.Dot(rel, v.RampAxis), x = Vector3d.Dot(rel, side), az = Math.Abs(z);
            if (az > RampEnd) return 0;
            double y = az <= RampDeckEdge ? 0 : -(az - RampDeckEdge) * RampSlope;
            var s = v.RampOrigin + v.RampAxis * z + side * x + v.RampUp * y;
            return Math.Max(0, s.magnitude - ground);
        }

        /// <summary>
        /// Посадочная ступень на выпущенных опорах ложится на грунт (§6.12): корпус встаёт нормалью плоскости под
        /// стопами, днище — на их средней высоте. Без этого борт стоял, как коснулся: КТ «Луны-17» — с креном 16° на
        /// кромке одной опоры, днище в 0,49 м над грунтом, трапы висели и луноход съезжал по воздуху (03.10.2026).
        /// Пара: LegSettleTime — порядка RoverTiltTime; перепад высоты уходит в подвеску (ход SuspensionStroke).
        /// </summary>
        const double LegSettleTime = 0.3;

        static void SettleOnLegs(Vessel v, double dt)
        {
            int b = v.BottomSection();
            if (b < 0 || v.Situation != Situation.Landed || !v.Design.Sections[b].LandingLegs || !v.LegsDown) return;
            var body = v.Body;
            v.MassProperties(out _, out double com, out _, out _);
            double R = v.AnchorBodyFixed.magnitude, foot = v.Design.Sections[b].Radius;
            var u = v.AnchorBodyFixed / R;
            var e1 = Vector3d.AnyPerpendicular(u).normalized;
            var e2 = Vector3d.Cross(u, e1);
            double h1 = body.SurfaceHeight((u + e1 * (foot / R)).normalized), h1n = body.SurfaceHeight((u - e1 * (foot / R)).normalized);
            double h2 = body.SurfaceHeight((u + e2 * (foot / R)).normalized), h2n = body.SurfaceHeight((u - e2 * (foot / R)).normalized);
            var n = (u - e1 * ((h1 - h1n) / (2 * foot)) - e2 * ((h2 - h2n) / (2 * foot))).normalized;
            var nose = (v.AttitudeBodyFixed * Vector3d.up).SwapYZ.normalized;
            var step = (nose + (n - nose) * Math.Min(1, dt / LegSettleTime)).normalized;
            if (Vector3d.Dot(step, nose) < 1 - 1e-12)
                v.AttitudeBodyFixed = QuaternionD.FromToRotation(nose, step).SwapYZ * v.AttitudeBodyFixed;
            double newR = body.Radius + (h1 + h1n + h2 + h2n) * 0.25 + com;
            if (Math.Abs(R - newR) < 1e-4) return;
            v.Suspension = MathD.Clamp(v.Suspension + R - newR, -SuspensionStroke, SuspensionStroke);
            v.AnchorBodyFixed = u * newR;
        }

        /// <summary>Шаг пружины-демпфера подвески: корпус догоняет точку AnchorBodyFixed без рывка.</summary>
        static void StepSuspension(Vessel v, double dt)
        {
            if (v.Suspension == 0 && v.SuspensionRate == 0) return;
            double w = 2 * Math.PI * SuspensionFreq;
            if (w * dt > 0.5) { v.Suspension = v.SuspensionRate = 0; return; }
            v.SuspensionRate += (-w * w * v.Suspension - 2 * SuspensionDamping * w * v.SuspensionRate) * dt;
            v.Suspension += v.SuspensionRate * dt;
            if (Math.Abs(v.Suspension) > SuspensionStroke)
            {
                // Упор хода: дальше корпус идёт жёстко вместе с грунтом.
                v.Suspension = Math.Sign(v.Suspension) * SuspensionStroke;
                if (v.SuspensionRate * v.Suspension > 0) v.SuspensionRate = 0;
            }
            if (Math.Abs(v.Suspension) < 1e-4 && Math.Abs(v.SuspensionRate) < 1e-3) v.Suspension = v.SuspensionRate = 0;
        }

        /// <summary>
        /// Езда самоходного шасси (GDD §6.3): W/S — вперёд/назад вдоль связанной оси Z, A/D — разворот вокруг местной
        /// вертикали. Шасси всегда стоит по радиусу на высоте рельефа: так же встаёт борт на столе (PlaceOnSurface).
        /// </summary>
        static void DriveRover(Vessel v, double dt)
        {
            var body = v.Body;
            v.MassProperties(out _, out double com, out _, out _);
            double R = v.AnchorBodyFixed.magnitude;
            var u = v.AnchorBodyFixed / R;
            // Курс — нос корпуса, возвращённый с наклона (GroundUp) на местную вертикаль тем же поворотом, каким его
            // наклонили. Простая проекция наклонённого носа при крене и тангаже сразу даёт рыскание ~sin·sin за шаг,
            // и на трапе (30° + крен) луноход разворачивало поперёк рельсов за 0,5 с (03.10.2026).
            if (v.GroundUp.sqrMagnitude < 0.5) v.GroundUp = u;
            var f = Vector3d.ProjectOnPlane(QuaternionD.FromToRotation(v.GroundUp, u) * (v.AttitudeBodyFixed * Vector3d.forward).SwapYZ, u);
            if (f.sqrMagnitude < 1e-12) f = Vector3d.ProjectOnPlane(Vector3d.forward, u);
            if (f.sqrMagnitude < 1e-12) f = Vector3d.ProjectOnPlane(Vector3d.right, u);
            f = f.normalized;
            // На трапах колёса идут по рельсам: поворот закрыт до съезда обеих осей на грунт.
            bool onRamp = v.RampDeck > 0;
            double turn = onRamp ? 0 : MathD.Clamp(v.PilotInput.y, -1, 1) * RoverTurnRate * dt;
            // D (y > 0) — направо, то есть по часовой при взгляде сверху: отрицательный угол вокруг зенита.
            if (turn != 0) f = (QuaternionD.AngleAxis(-turn, u) * f).normalized;
            double ds = MathD.Clamp(v.PilotInput.x, -1, 1) * RoverSpeed * dt;
            var dir = (u + f * (ds / R)).normalized;
            f = Vector3d.ProjectOnPlane(f, dir).normalized;
            // Точки колёс: передняя и задняя оси, левый и правый борт.
            var side = Vector3d.Cross(dir, f);
            var pF = (dir + f * (RoverHalfBase / R)).normalized;
            var pB = (dir - f * (RoverHalfBase / R)).normalized;
            var pS = (dir + side * (RoverHalfTrack / R)).normalized;
            var pN = (dir - side * (RoverHalfTrack / R)).normalized;
            // Съезд: колёса стоят на рельсах ступени, ЦМ — на средней высоте осей, наклон — по разнице высот (нос вниз).
            double rampF = 0, rampB = 0, rampS = 0, rampN = 0;
            if (onRamp)
            {
                rampF = RampAbove(v, pF);
                rampB = RampAbove(v, pB);
                rampS = RampAbove(v, pS);
                rampN = RampAbove(v, pN);
                v.RampTravel = Vector3d.Dot(dir * R - v.RampOrigin, v.RampAxis);
                if (rampF <= 0 && rampB <= 0) v.RampDeck = 0;
            }
            double hF = body.SurfaceHeight(pF) + rampF, hB = body.SurfaceHeight(pB) + rampB;
            double hS = body.SurfaceHeight(pS) + rampS, hN = body.SurfaceHeight(pN) + rampN;
            double newR = body.Radius + (onRamp ? (hF + hB) * 0.5 : body.SurfaceHeight(dir)) + com;
            // Перепад рельефа уходит в подвеску, а не в корпус: точка опоры прыгает, корпус догоняет её демпфером.
            // На трапе профиль гладкий — подвеска не нужна.
            if (ds != 0 && !onRamp) v.Suspension = MathD.Clamp(v.Suspension + R - newR, -SuspensionStroke, SuspensionStroke);
            v.AnchorBodyFixed = dir * newR;
            // Наклон корпуса — по четырём точкам колёс, сглаженный: на склоне кратера луноход стоит вдоль грунта.
            var n = (dir - f * ((hF - hB) / (2 * RoverHalfBase)) - side * ((hS - hN) / (2 * RoverHalfTrack))).normalized;
            // Сначала перенос за точкой опоры по сфере (иначе на пробеге копится крен к старому радиусу), потом сглаживание.
            var carried = QuaternionD.FromToRotation(u, dir) * v.GroundUp;
            var tilt = v.GroundUp = (carried + (n - carried) * Math.Min(1, dt / RoverTiltTime)).normalized;
            var ft = (QuaternionD.FromToRotation(dir, tilt) * f).normalized;
            v.AttitudeBodyFixed = QuaternionD.FromBasis(Vector3d.Cross(ft, tilt).SwapYZ, tilt.SwapYZ, ft.SwapYZ);
            v.DriveDistance += Math.Abs(ds);
            // Разворот на месте: D (turn > 0) — направо, левый борт катится вперёд, правый назад.
            v.WheelPathLeft += ds + turn * RoverHalfTrack;
            v.WheelPathRight += ds - turn * RoverHalfTrack;
        }

        /// <summary>Отрыв с пробега — подъёмная с запасом над весом: без запаса борт «подпрыгивает» в момент касания.</summary>
        const double RolloutLiftMargin = 1.05;

        /// <summary>Подъёмная крыльев на пробеге вдоль местной вертикали, Н.</summary>
        static double RolloutLift(Vessel v, CelestialBody body, double t, double r)
        {
            v.MassProperties(out _, out _, out _, out _);
            Aerodynamics.CollectPanels(v, panelBuf);
            if (panelBuf.Count == 0 || v.Density <= 0) return 0;
            var spin = SpinAxis(body, t);
            var vAir = v.Velocity - Vector3d.Cross(spin, v.Position);
            var fl = Aerodynamics.Force(panelBuf, v.WorldToLocal(vAir), v.Density, v.Mach);
            return Vector3d.Dot(v.LocalToWorld(fl), v.Position / r);
        }

        static void StepLanded(Vessel v, double t, double dt)
        {
            var body = v.Body;
            v.AeroControlTorque = Vector3d.zero;
            v.ControlDeflection = Vector3d.zero;
            bool rolling = v.RollSpeed > 0 && v.Situation == Situation.Landed;
            if (rolling) StepRollout(v, t, dt);
            else if (v.IsRover && v.Situation == Situation.Landed) DriveRover(v, dt);
            else SettleOnLegs(v, dt);
            StepSuspension(v, dt);
            UpdateLandedPose(v, t + dt);
            if (rolling) v.Velocity += body.OrientationAt(t + dt) * (v.RollDir * v.RollSpeed);
            v.UpdateEngines();
            UpdateAir(v, body, v.Position, v.Velocity, SpinAxis(body, t + dt));
            double thrust = v.Thrust(v.StaticPressure, out _);
            v.CurrentThrust = thrust;
            double r = v.Position.magnitude;
            double weight = v.Mass * body.Mu / (r * r);
            double lift = rolling ? RolloutLift(v, body, t + dt, r) : 0;
            if (thrust * Vector3d.Dot(v.NoseP, v.Position / r) + lift > weight * (rolling ? RolloutLiftMargin : 1))
            {
                v.RollSpeed = 0;
                bool fromWater = v.Situation == Situation.Splashed;
                v.Situation = Situation.Flying;
                v.Suspension = v.SuspensionRate = 0;
                v.GroundUp = Vector3d.zero;
                v.RampDeck = 0;
                if (double.IsNaN(v.LaunchTime)) v.LaunchTime = t;
                v.Raise(fromWater ? "Взлёт с воды" : "Есть отрыв!");
            }
            v.BurnPropellant(dt);
            v.SettledTimer = 0;
            v.TerrainAltitude = 0;
            v.GForce = 1;
        }

        // ---------------------------------------------------------------- в полёте

        struct Geometry
        {
            public double Mass, Com, Length, Radius, DragScale, FrontRadius;
            /// <summary>
            /// Корпус без боковых блоков (Vessel.HullRadius) и площади обтекания: лобовая и поперечная. Radius (maxR)
            /// с боковыми блоками — только для касания грунта и осадки; в аэродинамике пакет Р-7 цилиндром Ø8,6 м
            /// давал вдвое лишнее сопротивление и нос-плечо 4,3 м — «Молния» ломалась на T+61 (q 30 кПа, α 8°).
            /// </summary>
            public double Hull, FrontArea, SideArea;
            public double ChuteCdA;
            /// <summary>Крылья (§4.6): плоскости в связанных осях, ориентация на шаг, щиток. null — бескрылый борт.</summary>
            public List<WingPanel> Panels;
            public QuaternionD Att;
            public double BrakeCdA;
            /// <summary>
            /// «Планер»: крылья не меньше половины боковой площади корпуса. Его корпус обтекается как фюзеляж под крылом —
            /// поперечное обтекание только с долей GliderSideFactor (остальное уже в площади крыла), момент даёт крыло,
            /// а не нос пакета (StackAeroTorque), и qα-разрушение не применяется: крыло несёт нагрузку по построению.
            /// </summary>
            public bool Glider;
            public double SideFactor;
        }

        /// <summary>
        /// Доля поперечного обтекания корпуса «планера»: площадь крыла 249,9 м² у «Шаттла» уже включает подкрыльевую часть
        /// фюзеляжа; поперёк обтекаются только нос и хвост. Пара: с 0,2 L/D орбитера на α 40° ≈ 1,1, на дозвуке ≈ 4,5
        /// (замер пробой Aerodynamics в тесте sts1).
        /// </summary>
        public const double GliderSideFactor = 0.2;
        /// <summary>Порог «планера»: площадь крыльев к боковой площади корпуса.</summary>
        const double GliderWingShare = 0.5;
        static readonly List<WingPanel> panelBuf = new List<WingPanel>();

        static void StepFlying(Vessel v, double t, double dt)
        {
            var body = v.Body;
            var spin = SpinAxis(body, t);
            v.UpdateEngines();

            v.MassProperties(out double mass, out double com, out double len, out double maxR);
            var nose = v.NoseP;
            UpdateAir(v, body, v.Position, v.Velocity, spin);
            UpdateChutes(v);
            double thrust = v.Thrust(v.StaticPressure, out _);
            v.CurrentThrust = thrust;

            var g = new Geometry { Mass = mass, Com = com, Length = len, Radius = maxR, Att = v.Attitude };
            AeroAreas(v, ref g);
            var vAir0 = v.Velocity - Vector3d.Cross(spin, v.Position);
            bool noseFirst = Vector3d.Dot(nose, vAir0) >= 0;
            int lead = noseFirst ? v.TopSection() : v.BottomSection();
            var leadDef = v.Design.Sections[lead];
            g.DragScale = leadDef.DragScale;
            g.FrontRadius = Math.Max(0.3, leadDef.Radius);
            for (int i = 0; i < v.Attached.Length; i++)
                if (v.Attached[i] && v.ChuteDeployed[i] && !v.ChuteFailed[i])
                    g.ChuteCdA += 1.5 * v.Design.Sections[i].ParachuteArea * ChuteFraction(v.ChuteOpenTime[i]);

            var thrustAcc = nose * ((thrust + v.RcsForward * v.RcsThrust) / mass)
                          // Поступательная РСУ — сближение при стыковке (§6.6), в связанных осях.
                          + v.LocalToWorld(v.RcsTranslate) * (v.RcsThrust / mass);

            // RK4: тяга и масса постоянны на шаге, гравитация и сопротивление — от состояния.
            var r0 = v.Position;
            var u0 = v.Velocity;
            var a1 = Accel(body, spin, nose, g, thrustAcc, r0, u0);
            var r1 = r0 + u0 * (dt * 0.5);
            var u1 = u0 + a1 * (dt * 0.5);
            var a2 = Accel(body, spin, nose, g, thrustAcc, r1, u1);
            var r2 = r0 + u1 * (dt * 0.5);
            var u2 = u0 + a2 * (dt * 0.5);
            var a3 = Accel(body, spin, nose, g, thrustAcc, r2, u2);
            var r3 = r0 + u2 * dt;
            var u3 = u0 + a3 * dt;
            var a4 = Accel(body, spin, nose, g, thrustAcc, r3, u3);
            v.Position = r0 + (u0 + u1 * 2 + u2 * 2 + u3) * (dt / 6);
            v.Velocity = u0 + (a1 + a2 * 2 + a3 * 2 + a4) * (dt / 6);

            // Вращение: момент регулятора, обрезанный по возможностям, + аэродинамика (капсула или пакет).
            var inertia = v.Inertia();
            var flowLocal = v.WorldToLocal(vAir0);
            // Рули (§4.6): бюджет момента по напору и Маху на начало шага — MaxTorque отдаёт его регулятору.
            v.AeroControlTorque = g.Panels != null && v.DynamicPressure > 0
                ? Aerodynamics.ControlAuthority(g.Panels, flowLocal, com, v.Density, v.Mach) : Vector3d.zero;
            var tmax = v.MaxTorque(v.StaticPressure);
            var cmd = v.TorqueCommand;
            // Триммер (§4.6): есть балансировочный щиток — свой момент сверх рулей; нет — смещение команды элевонов в их ходе.
            double trim = MathD.Clamp(v.PitchTrim, -1, 1), trimFlap = 0;
            if (trim != 0 && g.Panels != null && v.DynamicPressure > 0)
            {
                if (Aerodynamics.HasTrimFlap(g.Panels))
                    trimFlap = trim * Aerodynamics.TrimAuthority(g.Panels, flowLocal, com, v.Density, v.Mach);
                else cmd.z += trim * v.AeroControlTorque.z;
            }
            var torque = new Vector3d(
                MathD.Clamp(cmd.x, -tmax.x, tmax.x),
                MathD.Clamp(cmd.y, -tmax.y, tmax.y),
                MathD.Clamp(cmd.z, -tmax.z, tmax.z));
            // Фактическая доля хода рулей — то, что физика приложила, к пределу (вид отклоняет по ней поверхности).
            v.ControlDeflection = new Vector3d(
                tmax.x > 0 ? torque.x / tmax.x : 0, tmax.y > 0 ? torque.y / tmax.y : 0, tmax.z > 0 ? torque.z / tmax.z : 0);
            torque.z += trimFlap;
            if (!g.Glider) torque += CapsuleAeroTorque(v, vAir0, g, inertia) + StackAeroTorque(v, vAir0, g);
            var w = v.AngularVelocity;
            // Крылья: каждая плоскость в своём потоке v + ω×r — момент и демпфирование вместе.
            if (g.Panels != null && v.DynamicPressure > 0)
                torque += Aerodynamics.Torque(g.Panels, flowLocal, w, com, v.Density, v.Mach);
            w = new Vector3d(w.x + torque.x / inertia.x * dt, w.y + torque.y / inertia.y * dt, w.z + torque.z / inertia.z * dt);
            v.AngularVelocity = w;
            v.Attitude = v.Attitude.IntegrateBody(w, dt);

            v.BurnPropellant(dt);
            if (v.Node != null && thrust > 0) v.Node.Remaining -= nose * (thrust / mass * dt);

            // Телеметрия на конец шага.
            UpdateAir(v, body, v.Position, v.Velocity, spin);
            var drag = Drag(body, spin, nose, g, v.Position, v.Velocity, out _, out _, out double sinA);
            var nonGrav = thrustAcc + drag / mass;
            v.GForce = nonGrav.magnitude / Constants.G0;
            CheckCrew(v, dt);

            // Осадка топлива: продольное ускорение вперёд прижимает топливо к заборникам (GDD §6.3).
            if (Vector3d.Dot(nonGrav, nose) > SettleAccel) v.SettledTimer = 3;
            else v.SettledTimer = Math.Max(0, v.SettledTimer - dt);

            CheckStructure(v, g, sinA, leadDef, dt);
            if (v.Alive) CheckContact(v, body, spin, g, t + dt);
        }

        static Vector3d Accel(CelestialBody body, Vector3d spin, Vector3d nose, Geometry g, Vector3d thrustAcc,
            Vector3d r, Vector3d u)
        {
            double rm = r.magnitude;
            var a = r * (-body.Mu / (rm * rm * rm)) + thrustAcc;
            if (body.Atmosphere != null)
                a += Drag(body, spin, nose, g, r, u, out _, out _, out _) / g.Mass;
            return a;
        }

        /// <summary>Сила сопротивления, Н. Площадь смешивается между лобовой и боковой по углу атаки.</summary>
        static Vector3d Drag(CelestialBody body, Vector3d spin, Vector3d nose, Geometry g, Vector3d r, Vector3d u,
            out double q, out double mach, out double sinA)
        {
            q = mach = sinA = 0;
            var atm = body.Atmosphere;
            if (atm == null) return Vector3d.zero;
            double alt = r.magnitude - body.Radius;
            if (alt >= atm.Top) return Vector3d.zero;
            atm.Sample(alt, out _, out double rho, out double temp);
            var vAir = u - Vector3d.Cross(spin, r);
            double sp = vAir.magnitude;
            if (sp < 1e-6 || rho <= 0) return Vector3d.zero;
            q = 0.5 * rho * sp * sp;
            mach = sp / atm.SpeedOfSound(temp);
            double cosA = Vector3d.Dot(nose, vAir) / sp;
            sinA = Math.Sqrt(Math.Max(0, 1 - cosA * cosA));
            double cd = MathD.Interp(CdMach, CdValue, mach) * g.DragScale;
            double cdA = cd * g.FrontArea * cosA * cosA + 1.2 * g.SideArea * g.SideFactor * sinA * sinA + g.ChuteCdA + g.BrakeCdA;
            var f = vAir * (-q * cdA / sp);
            if (g.Panels != null)
            {
                // Крылья — подъёмная и сопротивление плоскостей в связанных осях (ориентация постоянна на шаге RK4).
                var local = g.Att.Inverse * vAir.SwapYZ;
                f += (g.Att * Aerodynamics.Force(g.Panels, local, rho, mach)).SwapYZ;
            }
            return f;
        }

        /// <summary>Давление, плотность, скорость звука, нагрев и скорости относительно поверхности.</summary>
        static void UpdateChutes(Vessel v)
        {
            // Высота — своя у каждой секции (окно детали); напор — общий порог прочности купола.
            if (v.DynamicPressure > ChuteSafeQ) return;
            for (int i = 0; i < v.Attached.Length; i++)
            {
                if (!v.Attached[i] || !v.ChuteArmed[i] || v.ChuteDeployed[i]) continue;
                if (v.Altitude > v.ChuteAltitude[i]) continue;
                v.ChuteArmed[i] = false;
                v.ChuteDeployed[i] = true;
                v.Raise("Парашют раскрыт");
            }
        }

        static void UpdateAir(Vessel v, CelestialBody body, Vector3d r, Vector3d u, Vector3d spin)
        {
            double rm = r.magnitude;
            var up = r / rm;
            v.Altitude = rm - body.Radius;
            var vSurf = u - Vector3d.Cross(spin, r);
            v.SurfaceSpeed = vSurf.magnitude;
            v.VerticalSpeed = Vector3d.Dot(u, up);
            v.HorizontalSpeed = Vector3d.ProjectOnPlane(vSurf, up).magnitude;
            v.StaticPressure = v.Density = v.DynamicPressure = v.Mach = v.HeatFlux = 0;
            v.AngleOfAttack = 0;
            var atm = body.Atmosphere;
            if (atm == null || v.Altitude >= atm.Top) return;
            atm.Sample(v.Altitude, out double p, out double rho, out double temp);
            v.StaticPressure = p;
            v.Density = rho;
            double sp = v.SurfaceSpeed;
            v.DynamicPressure = 0.5 * rho * sp * sp;
            v.Mach = sp / atm.SpeedOfSound(temp);
            if (sp > 1e-3) v.AngleOfAttack = Vector3d.Angle(v.NoseP, vSurf) * Constants.Rad2Deg;
        }

        /// <summary>Капсула со смещённым центром масс сама разворачивается теплозащитой вперёд.</summary>
        static Vector3d CapsuleAeroTorque(Vessel v, Vector3d vAirP, Geometry g, Vector3d inertia)
        {
            if (v.DynamicPressure <= 0) return Vector3d.zero;
            var bottom = v.Design.Sections[v.BottomSection()];
            if (bottom.Kind != SectionKind.Capsule) return Vector3d.zero;
            var flowLocal = v.WorldToLocal(vAirP);
            if (flowLocal.sqrMagnitude < 1e-6) return Vector3d.zero;
            // Нос (+Y) тянется против набегающего потока: днище вперёд.
            var target = -flowLocal.normalized;
            var axis = Vector3d.Cross(Vector3d.up, target);
            double s = axis.magnitude;
            double ang = Math.Atan2(s, Vector3d.Dot(Vector3d.up, target));
            double area = Math.PI * g.Radius * g.Radius;
            double k = 0.05 * v.DynamicPressure * area * 2 * g.Radius;
            var restoring = s > 1e-9 ? axis / s * (k * Math.Sin(ang * 0.5) * 2) : Vector3d.zero;
            // Аэродемпфирование, иначе капсула раскачивается вечно.
            var w = v.AngularVelocity;
            double damp = 4 * Math.Sqrt(k * inertia.x);
            return restoring - new Vector3d(w.x * damp, 0, w.z * damp);
        }

        /// <summary>
        /// Момент пакета (не капсулы): подъёмная сила носа у ведущего торца + поперечное обтекание в середине
        /// длины + демпфирование. Высоты — от низа пакета, как g.Com.
        /// </summary>
        static Vector3d StackAeroTorque(Vessel v, Vector3d vAirP, Geometry g)
        {
            double q = v.DynamicPressure;
            if (q <= 0) return Vector3d.zero;
            int bottom = v.BottomSection();
            if (bottom < 0 || v.Design.Sections[bottom].Kind == SectionKind.Capsule) return Vector3d.zero;
            // Скорость борта относительно воздуха в связанных осях; поток набегает навстречу ей.
            var flow = v.WorldToLocal(vAirP);
            double sp = flow.magnitude;
            if (sp < 1) return Vector3d.zero;
            var dir = flow / sp;
            var cross = new Vector3d(dir.x, 0, dir.z);
            double sinA = cross.magnitude, cosA = dir.y;
            // Подъёмная сила носа — по корпусу ядра; конусы боковых блоков ниже и ближе к ЦМ, их вклад — в поперечном.
            double front = Math.PI * g.Hull * g.Hull;
            double side = g.SideArea;
            // Ведущий торец: носом вперёд — верх, хвостом — низ (ступень после отделения летит как попало).
            double cp = Math.Min(NoseCpRadii * g.Hull, g.Length * 0.5);
            double leadY = cosA >= 0 ? g.Length - cp : cp;
            var fNose = cross * (-q * front * StackNormalSlope * Math.Abs(cosA));
            var fCross = cross * (-q * StackCrossflowCd * side * sinA);
            var torque = Vector3d.Cross(new Vector3d(0, leadY - g.Com, 0), fNose)
                       + Vector3d.Cross(new Vector3d(0, g.Length * 0.5 - g.Com, 0), fCross);
            // Стабилизаторы: та же сила по направлению, но за ЦМ — момент возвращает нос к потоку.
            var secs = v.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!v.Attached[i] || secs[i].FinArea <= 0) continue;
                var fFin = cross * (-q * secs[i].FinArea * FinNormalSlope * Math.Abs(cosA));
                torque += Vector3d.Cross(new Vector3d(0, v.SectionBottom(i) + FinCpHeight - g.Com, 0), fFin);
            }
            var w = v.AngularVelocity;
            double damp = q * front * g.Length * g.Length / (2 * sp) * StackPitchDamping;
            return torque - new Vector3d(w.x * damp, 0, w.z * damp);
        }

        static void CheckCrew(Vessel v, double dt)
        {
            if (v.CrewLost || !v.HasCrew()) return;
            if (v.GForce > CrewGLimit) v.HighGTimer += dt;
            else v.HighGTimer = Math.Max(0, v.HighGTimer - dt * 0.5);
            // Без правила таймер упирается в предел: HUD показывает «перегрузка 100 %», экипаж жив.
            if (!GLoadLimit) v.HighGTimer = Math.Min(v.HighGTimer, CrewGTime);
            else if (v.HighGTimer > CrewGTime)
            {
                v.CrewLost = true;
                v.Raise($"Экипаж погиб: перегрузка {v.GForce:F1} g дольше {CrewGTime:F0} с");
            }
        }

        /// <summary>
        /// Площади обтекания: корпус — цилиндр радиуса ядра, каждый боковой блок — свой торец; сбоку видны
        /// не больше двух блоков группы (остальные заслонены ядром). Без радиальных групп — как прежде (π·R², L·2R·0,8).
        /// </summary>
        static void AeroAreas(Vessel v, ref Geometry g)
        {
            g.Hull = v.HullRadius();
            g.FrontArea = Math.PI * g.Hull * g.Hull;
            g.SideArea = g.Length * 2 * g.Hull * 0.8;
            var secs = v.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                var s = secs[i];
                if (!v.Attached[i] || !s.IsRadial) continue;
                g.FrontArea += s.RadialCount * Math.PI * s.Radius * s.Radius;
                g.SideArea += Math.Min(s.RadialCount, 2) * s.Length * 2 * s.Radius * 0.8;
            }
            g.SideFactor = 1;
            g.BrakeCdA = v.GridFinCdA();
            Aerodynamics.CollectPanels(v, panelBuf);
            if (panelBuf.Count == 0) return;
            g.Panels = panelBuf;
            g.Glider = Aerodynamics.WingArea(panelBuf) >= GliderWingShare * g.SideArea;
            if (g.Glider) g.SideFactor = GliderSideFactor;
            // Щиток-тормоз: Cd 1 на раскрытую площадь, по команде Vessel.AirBrake.
            g.BrakeCdA += Aerodynamics.BrakeArea(panelBuf) * MathD.Clamp(v.AirBrake, 0, 1);
        }

        static void CheckStructure(Vessel v, Geometry g, double sinA, SectionDef lead, double dt)
        {
            double q = v.DynamicPressure;
            if (q <= 0) return;

            // Поперечная нагрузка ломает только длинный пакет; шар капсулы ей не подвержен.
            // Возвращаемая ступень хвостом вперёд (α > 170°) на раскрытых решётчатых рулях устойчива как флюгер: на спуске
            // напор доходит до 200+ кПа, где лимит q·α ломал бы уже за 1° рассогласования (BoosterLandingAutopilot).
            bool finStable = v.AngleOfAttack > GridFinStableAlpha && g.BrakeCdA > 0 && v.GridFinCdA() > 0;
            // Starship «брюхом» (§6.9): поперёк потока его держат закрылки — это штатный режим, а не потеря устойчивости.
            if (AeroBreakup && !g.Glider && !finStable && !BoosterLandingAutopilot.BellyEntry(v) && QAlphaExceeded(q, sinA, g.Length, g.Hull))
            {
                v.Destroy($"Разрушение от аэродинамической нагрузки: q = {q / 1000:F1} кПа, α = {v.AngleOfAttack:F0}°");
                return;
            }

            for (int i = 0; i < v.Attached.Length; i++)
            {
                if (!v.Attached[i] || !v.ChuteDeployed[i] || v.ChuteFailed[i]) continue;
                v.ChuteOpenTime[i] += dt;
                if (q > ChuteMaxQ)
                {
                    v.ChuteFailed[i] = true;
                    v.Raise($"Парашют сорван: скоростной напор {q / 1000:F1} кПа");
                }
            }

            double sp = v.SurfaceSpeed;
            v.HeatFlux = SuttonGraves * Math.Sqrt(v.Density / Math.Max(0.5, lead.Radius)) * sp * sp * sp;
            if (v.HeatFlux > lead.MaxHeatFlux) v.OverheatTimer += dt * v.HeatFlux / lead.MaxHeatFlux;
            else v.OverheatTimer = Math.Max(0, v.OverheatTimer - dt * 0.5);
            // Без правила таймер упирается в предел: HUD показывает «перегрев 100 %», борт цел.
            if (!HeatDamage) v.OverheatTimer = Math.Min(v.OverheatTimer, OverheatTolerance);
            else if (v.OverheatTimer > OverheatTolerance)
                v.Destroy($"Сгорел в атмосфере: тепловой поток {v.HeatFlux / 1e6:F2} МВт/м² ({lead.Name})");
        }

        /// <summary>Плотность морской воды, кг/м³.</summary>
        const double SeaWaterDensity = 1025;
        /// <summary>Осадку не глубже этой доли диаметра: тяжёлый корпус (ступень) иначе «тонет» целиком под гладь.</summary>
        const double MaxDraftFraction = 0.8;

        /// <summary>
        /// Осадка на воде (§6.4): борт плавает погружённым на долю объёма m/(ρV), а не лежит на глади —
        /// капсула стояла на воде «бильярдным шаром» (01.10.2026). Объём — шар радиуса корпуса; для шара доля
        /// объёма ≈ доля диаметра (точно при ½). «Восток» 2,4 т, ⌀2,3 м → 0,38 → осадка ≈ 0,9 м.
        /// Касание и взлёт с воды считаются от той же ватерлинии — порог отрыва не меняется.
        /// </summary>
        static double Draft(Geometry g)
        {
            double vol = 4.0 / 3.0 * Math.PI * g.Radius * g.Radius * g.Radius;
            double f = Math.Min(MaxDraftFraction, g.Mass / (SeaWaterDensity * Math.Max(vol, 1e-3)));
            return 2 * g.Radius * f;
        }

        /// <summary>
        /// Касание на шасси (§6.4): |cos| угла носа к вертикали меньше GearMaxNoseUp (борт почти горизонтален),
        /// снижение не быстрее GearSinkLimit, путевая не выше GearMaxSpeed. «Шаттл» касался на 95–105 м/с со
        /// снижением ≈ 1 м/с, стойки держали до ≈ 3 м/с. Пара: GearMaxSpeed выше посадочной скорости автопилота (≈ 100 м/с).
        /// </summary>
        public const double GearMaxNoseUp = 0.35, GearSinkLimit = 3, GearMaxSpeed = 130;
        /// <summary>
        /// Пробег (§6.4): торможение колёсами ≈ 2,5 м/с² (у «Шаттла» с парашютом ≈ 2,4 при 2,7 км пробега от 100 м/с);
        /// нос опускается на переднюю стойку за NoseDownTime; руление A/D — RolloutTurnRate при полном отклонении.
        /// </summary>
        public const double RolloutBrake = 2.5, NoseDownTime = 3, RolloutTurnRate = 3 * Constants.Deg2Rad;
        /// <summary>
        /// Тормозной парашют пробега (§4.6): ленточный купол Cd ≈ 0,55 на площадь, наполнение DragChuteFillTime с;
        /// сброс ниже DragChuteJettisonSpeed (у «Шаттла» 60 уз ≈ 31 м/с — дальше купол ложится на полосу и рвёт сопла),
        /// выше DragChuteMaxSpeed купол рвёт (выпуск «Шаттла» — до 230 уз ≈ 118 м/с). Пара: JettisonSpeed &lt; GearMaxSpeed.
        /// С куполом 117 м² от 100 м/с — ≈ 2 м/с² сверх колёс на первых секундах.
        /// </summary>
        public const double DragChuteCd = 0.55, DragChuteFillTime = 2.5, DragChuteJettisonSpeed = 30, DragChuteMaxSpeed = 120;

        static bool TouchdownOnGear(Vessel v, CelestialBody body, QuaternionD o, Vector3d up, Vector3d vSurf, double height, double t)
        {
            double sink = -Vector3d.Dot(vSurf, up);
            var horiz = Vector3d.ProjectOnPlane(vSurf, up);
            double hs = horiz.magnitude;
            if (sink > GearSinkLimit)
            {
                v.Destroy($"Шасси сломано: снижение {sink:F1} м/с при касании");
                return true;
            }
            if (hs > GearMaxSpeed)
            {
                v.Destroy($"Шасси сломано: касание на {hs:F0} м/с");
                return true;
            }
            var snapped = up * (body.Radius + height);
            v.Situation = Situation.Landed;
            v.AnchorBodyFixed = o.Inverse * snapped;
            v.Suspension = 0;
            v.SuspensionRate = -Math.Max(0, sink);
            v.AttitudeBodyFixed = o.SwapYZ.Inverse * v.Attitude;
            v.TerrainAltitude = 0;
            v.TouchdownSink = sink;
            v.TouchdownSpeed = hs;
            // Курс пробега — путевая скорость (или нос, если борт почти стоит), в осях тела.
            var dirP = hs > 0.5 ? horiz / hs : Vector3d.ProjectOnPlane(v.NoseP, up).normalized;
            v.RollDir = o.Inverse * dirP;
            v.RollSpeed = Math.Max(hs, 0.01);
            UpdateLandedPose(v, t);
            var rw = Runways.At(body, v.AnchorBodyFixed);
            v.Raise(rw != null ? $"Касание: {rw.Name}, {hs:F0} м/с, снижение {sink:F1} м/с"
                               : $"Касание вне полосы: {hs:F0} м/с, снижение {sink:F1} м/с");
            return true;
        }

        /// <summary>
        /// Пробег на шасси: борт катится вдоль RollDir по рельефу, нос опускается, колёса тормозят; газ помогает,
        /// подъёмная больше веса — снова в полёт (уход на второй круг). Остановка — RollSpeed = 0, дальше стоит.
        /// </summary>
        static void StepRollout(Vessel v, double t, double dt)
        {
            var body = v.Body;
            double R = v.AnchorBodyFixed.magnitude;
            var u = v.AnchorBodyFixed / R;
            double above = R - body.Radius - body.SurfaceHeight(u);
            var f = Vector3d.ProjectOnPlane(v.RollDir, u);
            if (f.sqrMagnitude < 1e-12) { v.RollSpeed = 0; return; }
            f = f.normalized;
            double turn = MathD.Clamp(v.PilotInput.y, -1, 1) * RolloutTurnRate * Math.Min(1, v.RollSpeed / 20) * dt;
            if (turn != 0) f = (QuaternionD.AngleAxis(-turn, u) * f).normalized;
            v.MassProperties(out double mass, out _, out _, out _);
            double thrust = v.Thrust(v.StaticPressure, out _);
            var o = body.OrientationAt(t);
            var noseB = o.Inverse * v.NoseP;
            // Сопротивление щитка на пробеге (Cd 1 на раскрытую площадь), как в полёте.
            Aerodynamics.CollectPanels(v, panelBuf);
            double drag = 0.5 * v.Density * v.RollSpeed * v.RollSpeed * Aerodynamics.BrakeArea(panelBuf) * MathD.Clamp(v.AirBrake, 0, 1);
            if (v.DragChute == DragChuteState.Open)
            {
                v.DragChuteTime += dt;
                double fill = Math.Min(1, v.DragChuteTime / DragChuteFillTime);
                drag += 0.5 * v.Density * v.RollSpeed * v.RollSpeed * DragChuteCd * v.DragChuteArea() * fill * fill;
                if (v.RollSpeed < DragChuteJettisonSpeed) v.JettisonDragChute();
            }
            // На пробеге руль направления ходит вместе с передней стойкой — для вида (ControlSurface).
            // Руль направления за педалями: D (PilotInput.y > 0) — вокруг −X, как в FlightControl.
            v.ControlDeflection = new Vector3d(-MathD.Clamp(v.PilotInput.y, -1, 1), 0, 0);
            double acc = (thrust * Vector3d.Dot(noseB, f) - drag) / Math.Max(1, mass) - RolloutBrake;
            v.RollSpeed = Math.Max(0, v.RollSpeed + acc * dt);
            double ds = v.RollSpeed * dt;
            var dir = (u + f * (ds / R)).normalized;
            f = Vector3d.ProjectOnPlane(f, dir).normalized;
            v.AnchorBodyFixed = dir * (body.Radius + body.SurfaceHeight(dir) + above);
            v.RollDir = f;
            v.DriveDistance += ds;
            // Нос — к горизонту по курсу, брюхо (+X) — к грунту (сборка базиса — как в PlaceOnSurface).
            var X = -dir;
            var Y = f;
            var Z = -Vector3d.Cross(X, Y);
            var target = QuaternionD.FromBasis(X.SwapYZ, Y.SwapYZ, Z.SwapYZ);
            v.AttitudeBodyFixed = QuaternionD.Slerp(v.AttitudeBodyFixed, target, Math.Min(1, dt / NoseDownTime));
            if (v.RollSpeed <= 0) v.Raise("Остановка на полосе");
        }

        static void CheckContact(Vessel v, CelestialBody body, Vector3d spin, Geometry g, double t)
        {
            var r = v.Position;
            double rm = r.magnitude;
            var up = r / rm;
            // Грубый отсев: выше любого рельефа — не считаем шум.
            double maxTerrain = body.Terrain != null ? body.Terrain.Amplitude * 1.5 : 0;
            if (rm - body.Radius > maxTerrain + g.Length + 100)
            {
                v.TerrainAltitude = rm - body.Radius;
                return;
            }
            var o = body.OrientationAt(t);
            var bf = o.Inverse * r;
            double h = body.SurfaceHeight(bf);
            double cosUp = Math.Abs(Vector3d.Dot(v.NoseP, up));
            double offset = g.Com * cosUp + g.Radius * Math.Sqrt(Math.Max(0, 1 - cosUp * cosUp));
            // Крылатый борт на выпущенном шасси, лёжа горизонтально: брюхо выше грунта на высоту стоек.
            bool onGear = v.GearDown && cosUp < GearMaxNoseUp;
            if (onGear) offset = g.Radius + v.GearHeight;
            bool water = body.Terrain != null && body.Terrain.Ocean && h <= 0 &&
                         Terrain.RawHeight(body.Terrain, bf.normalized) < 0;
            // Баржа (§6.9): над палубой — твёрдый настил, а не вода.
            if (water && BoosterLandingAutopilot.DeckUnder(v, body, bf, out double deck))
            {
                water = false;
                h = deck;
            }
            if (water) offset -= Draft(g);
            v.TerrainAltitude = rm - body.Radius - h - offset;
            if (v.TerrainAltitude > 0) return;

            if (body.IsGasGiant || body.Parent == null)
            {
                v.Destroy($"Раздавлен в глубинах атмосферы: {body.Name}");
                return;
            }
            var vSurf = v.Velocity - Vector3d.Cross(spin, r);
            double speed = vSurf.magnitude;
            if (onGear && !water && TouchdownOnGear(v, body, o, up, vSurf, h + offset, t)) return;
            if (speed > CrashSpeed)
            {
                v.Destroy($"Удар о поверхность: {body.Name}, {speed:F0} м/с");
                return;
            }
            // Super Heavy без опор: у башни касание держат её «палочки» (RecoveryDef.TowerCatch).
            if (!water && speed > StowedCrashSpeed && !v.LegsDown && !BoosterLandingAutopilot.CaughtByTower(v, body, bf))
            {
                v.Destroy($"Посадка на сложенные опоры: {body.Name}, {speed:F1} м/с");
                return;
            }
            var snapped = up * (body.Radius + h + offset);
            v.Situation = water ? Situation.Splashed : Situation.Landed;
            v.AnchorBodyFixed = o.Inverse * snapped;
            // Удар принимает подвеска: корпус проседает со скоростью касания и выходит на опоры демпфером.
            v.Suspension = 0;
            v.SuspensionRate = Math.Min(0, Vector3d.Dot(vSurf, up));
            v.AttitudeBodyFixed = o.SwapYZ.Inverse * v.Attitude;
            v.TerrainAltitude = 0;
            UpdateLandedPose(v, t);
            for (int i = 0; i < v.Attached.Length; i++)
                if (v.ChuteDeployed[i]) v.ChuteFailed[i] = true; // купол лёг на землю
            v.Raise(water ? $"Приводнение: {speed:F1} м/с" : $"Посадка: {body.Name}, {speed:F1} м/с");
        }
    }
}
