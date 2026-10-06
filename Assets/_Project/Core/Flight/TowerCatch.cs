using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Ловля «палочками» башни Mechazilla (§6.9) — контактная модель, как шасси на полосе: борт без опор держат не
    /// грунт, а две балки на каретке башни. Корпус опускается между разведёнными палочками, башня сводит их вокруг
    /// корпуса (сходясь, балки толкают его к оси), и цапфы под носовой частью ложатся на балки. Мимо — ничего не держит:
    /// цапфы проходят в щель, борт падает на грунт на сложенные опоры (FlightPhysics.CheckContact).
    ///
    /// Оси — базис оси ловли (центр стола или круга ловли), как у стола в LaunchPadView: x — восток, y — зенит,
    /// z — север, начало — грунт на оси. Башня — к западу (−x), палочки идут вдоль x, щель между ними — по z.
    /// Геометрия — один источник для физики и вида: LaunchPadView рисует башню и палочки по этим числам.
    /// </summary>
    public static class TowerCatch
    {
        /// <summary>
        /// Башня: центр по x, м, сторона в плане, м, высота, м (≈ 146 м, ≈ 12 м, ≈ 30 м к западу от оси стола).
        /// Пара: Tools/blender/spacex_parts.py не моделирует башню — её рисует LaunchPadView по этим числам.
        /// </summary>
        public const double TowerX = -30, TowerSide = 12, TowerHeight = 146;
        /// <summary>Грань башни к оси, м: от неё растут палочки (корень каретки).</summary>
        public const double ArmRoot = TowerX + TowerSide * 0.5;
        /// <summary>
        /// Конец палочек по x, м (≈ 36–38 м от грани башни у настоящих). Пара: цапфы ложатся, если ось корпуса в
        /// [ArmRoot + радиус, ArmTip] — окно ≈ 33 м вдоль палочек, автопилот промахивается на 3–11 м.
        /// </summary>
        public const double ArmTip = 14;
        /// <summary>
        /// Полущель палочек по осям балок, м: сведены — радиус корпуса 4,5 + полутолщина балки 0,8 + 0,2 зазора;
        /// разведены — корпус проходит при боковом промахе до ArmGapOpen − ArmBarHalf − 4,5 ≈ 9,7 м.
        /// Пара: Ø Super Heavy и корабля 9 м — шире корпус, шире ArmGapClosed.
        /// </summary>
        public const double ArmGapClosed = 5.5, ArmGapOpen = 15;
        /// <summary>Полутолщина балки поперёк (по z) и полувысота, м.</summary>
        public const double ArmBarHalf = 0.8, ArmBarHalfHeight = 1.2;
        /// <summary>
        /// Сведение палочек, с: от разведённых до сведённых — ≈ 3,8 м/с хода балки. Пара: корпус проходит под цапфами
        /// (длина ниже цапф ≈ 67 м у Super Heavy) за ≈ 3 с посадочного профиля — свести надо быстрее.
        /// </summary>
        public const double ArmCloseTime = 2.5;
        /// <summary>Цапфы выступают за обвод корпуса, м: на сведённых палочках лежат при боковом смещении до
        /// ArmPinOut − 0,2 ≈ 1,3 м — его снимают сами сходящиеся балки.</summary>
        public const double ArmPinOut = 1.5;
        /// <summary>
        /// Удар балки о корпус сбоку, м/с: каретка на амортизаторах, балка толкает 200-тонный корпус к оси. Быстрее —
        /// «удар о палочки». Пара: ход сведения (ArmGapOpen − ArmGapClosed) / ArmCloseTime ≈ 3,8 м/с меньше порога.
        /// </summary>
        public const double ArmHitSpeed = 6;
        /// <summary>
        /// Посадка цапф на палочки: снижение, м/с (каретка гасит его амортизаторами, как подвеска опор — FlightPhysics.
        /// StepSuspension), скольжение вдоль палочек, м/с, и наклон оси от вертикали, градусы. Пара: автопилот касается на
        /// TouchdownRate 2,5 м/с (BoosterLandingAutopilot) — с запасом ниже CatchSinkLimit.
        /// </summary>
        public const double CatchSinkLimit = 6, CatchSlideLimit = 3, CatchMaxTilt = 10;
        /// <summary>
        /// Низ висящего корпуса над столом, м: сопла не достают до стартового стола (на него ускоритель потом опускают).
        /// Пара: RecoveryDef.ArmHeight = этот зазор + высота стола + (длина − PinsFromTop) — SpaceXRockets.
        /// </summary>
        public const double HangClearance = 6;
        /// <summary>
        /// Каретка палочек: ловит взведённой на CatchLift выше рабочей высоты (днище далеко над столом — промах по высоте
        /// не бьёт сопла о стол), потом опускает пойманный борт со скоростью CarriageSpeed, м/с (≈ 30 с хода).
        /// Пара: RecoveryDef.ArmHeight − ArmLowered = CatchLift (SpaceXRockets).
        /// </summary>
        public const double CatchLift = 30, CarriageSpeed = 1;
        /// <summary>Дальше этого от оси, м, башня борт не видит (палочки расходятся, контакта не считаем).</summary>
        const double Reach = 300;
        static readonly bool Trace = Environment.GetEnvironmentVariable("KSP_CATCH") != null;

        /// <summary>Высота палочек над грунтом на оси ловли, м: взведённые минус пройденный кареткой ход.</summary>
        public static double ArmY(RecoveryDef d, double lowered) => d.ArmHeight - lowered;

        /// <summary>
        /// Шаг каретки с пойманным бортом (FlightPhysics.StepLanded): опускает якорь ЦМ вместе с палочками до ArmLowered.
        /// </summary>
        public static void Lower(Vessel v, double dt)
        {
            var d = Def(v);
            if (d == null || d.ArmLowered <= 0) return;
            double drop = Math.Min(CarriageSpeed * dt, d.ArmHeight - d.ArmLowered - v.CatchLowered);
            if (drop <= 0) return;
            v.CatchLowered += drop;
            v.AnchorBodyFixed -= v.AnchorBodyFixed.normalized * drop;
        }

        /// <summary>Полущель палочек по осям балок, м, при сведении s (0 — разведены, 1 — сведены).</summary>
        public static double Gap(double s) => ArmGapOpen + (ArmGapClosed - ArmGapOpen) * MathD.Clamp(s, 0, 1);

        /// <summary>Башня, которая ловит этот борт: RecoveryDef.TowerCatch у единственной секции (ступень или корабль).</summary>
        public static RecoveryDef Def(Vessel v)
        {
            RecoveryDef r = null;
            int n = 0;
            for (int i = 0; i < v.Attached.Length; i++)
                if (v.Attached[i]) { n++; r = v.Design.Sections[i].Recovery; }
            return n == 1 && r != null && r.TowerCatch ? r : null;
        }

        /// <summary>Базис оси ловли в осях тела: начало — грунт на оси, x — восток, y — зенит, z — север.</summary>
        public static void Frame(CelestialBody body, RecoveryDef d, out Vector3d origin, out Vector3d ex, out Vector3d ey, out Vector3d ez)
        {
            ey = CelestialBody.LatLonToBodyFixed(d.TargetLat, d.TargetLon);
            origin = ey * (body.Radius + body.SurfaceHeight(ey));
            ex = Vector3d.Cross(Vector3d.forward, ey).normalized;
            ez = Vector3d.Cross(ey, ex);
        }

        /// <summary>
        /// Шаг контакта с башней на конце шага полёта (крючок FlightPhysics.StepFlying до CheckContact): сведение палочек,
        /// толчок балок, удары о балки и башню, посадка цапф. true — борт пойман или разбит (касание грунта не считать).
        /// com — ЦМ над днищем, len — длина, hull — радиус корпуса, м.
        /// </summary>
        public static bool Step(Vessel v, CelestialBody body, double t, double dt, double com, double len, double hull)
        {
            var d = Def(v);
            if (d == null || d.ArmHeight <= 0) return false;
            v.TowerCaught = false;
            Frame(body, d, out var origin, out var ex, out var ey, out var ez);
            var o = body.OrientationAt(t);
            var bf = o.Inverse * v.Position - origin;
            double x = Vector3d.Dot(bf, ex), y = Vector3d.Dot(bf, ey), z = Vector3d.Dot(bf, ez);
            if (Math.Abs(x) > Reach || Math.Abs(z) > Reach || y > TowerHeight + len)
            {
                v.CatchArms = 0;
                v.CatchPin = double.NaN;
                return false;
            }
            var spin = FlightPhysics.SpinAxis(body, t);
            var vs = o.Inverse * (v.Velocity - Vector3d.Cross(spin, v.Position));
            double vx = Vector3d.Dot(vs, ex), vy = Vector3d.Dot(vs, ey), vz = Vector3d.Dot(vs, ez);
            var noseBf = o.Inverse * v.NoseP;
            double nx = Vector3d.Dot(noseBf, ex), ny = Vector3d.Dot(noseBf, ey), nz = Vector3d.Dot(noseBf, ez);
            v.CatchLowered = 0;
            double armY = ArmY(d, 0);

            // Башня: корпус у её грани на высоте фермы — удар (сбоку проходит только мимо башни по z).
            double reachX = Math.Min(x, x + nx * (len - com)) - hull, reachZ = Math.Min(Math.Abs(z), Math.Abs(z + nz * (len - com)));
            if (reachX < ArmRoot && x > TowerX && reachZ < TowerSide * 0.5 + hull && y - com * Math.Abs(ny) < TowerHeight)
            {
                v.Destroy($"Удар о башню «{d.TargetName}»: {Math.Sqrt(vx * vx + vy * vy + vz * vz):F1} м/с");
                return true;
            }
            // Палочки работают только с почти вертикальным корпусом носом вверх.
            if (ny < 0.5)
            {
                v.CatchArms = Math.Max(0, v.CatchArms - dt / ArmCloseTime);
                v.CatchPin = double.NaN;
                return false;
            }

            // Цапфы — на оси корпуса ниже носа на PinsFromTop; низ — днище. Положение оси на высоте палочек — по наклону.
            double pinUp = len - com - d.PinsFromTop;
            double pinY = y + ny * pinUp, botY = y - ny * com;
            // Цапфы над палочками — с прошлого шага, а не по vy·dt: при наклоне корпуса цапфы ходят и от поворота
            // (замер starship_catch: Super Heavy под 9° прошёл плоскость поворотом при vy ≈ 0, пересечение не ловилось).
            double above = pinY - armY, prevAbove = double.IsNaN(v.CatchPin) ? above - vy * dt : v.CatchPin;
            v.CatchPin = above;
            double s = (armY - y) / ny;
            double hx = x + nx * s, hz = z + nz * s;
            bool spans = botY < armY && above > 0;
            bool overArms = hx > ArmRoot + hull && hx < ArmTip;

            double gapPrev = Gap(v.CatchArms);
            // Башня сводит палочки, когда корпус уже между ними: днище ниже балок, цапфы выше.
            if (spans && overArms) v.CatchArms = Math.Min(1, v.CatchArms + dt / ArmCloseTime);
            else if (!v.TowerCaught && above > 0) v.CatchArms = Math.Max(0, v.CatchArms - dt / ArmCloseTime);
            double gap = Gap(v.CatchArms);
            double closing = Math.Max(0, (gapPrev - gap) / dt);
            if (Trace && botY < armY + 150 && (t % 0.25) < dt)
                Console.WriteLine($"CATCH {v.Name} t {t:F2} x {x:F1} z {z:F1} hx {hx:F1} hz {hz:F1} bot {botY - armY:F1} pin {above:F1} vx {vx:F1} vy {vy:F1} vz {vz:F1} ny {ny:F3} arms {v.CatchArms:F2} gap {gap:F1}");

            // Посадка цапф: пересекли плоскость балок на этом шаге. Цапфы — по обе стороны корпуса поперёк щели (±z).
            if (above <= 0 && prevAbove > 0)
            {
                double px = x + nx * pinUp, pz = z + nz * pinUp;
                double pinR = hull + ArmPinOut, inner = gap - ArmBarHalf;
                bool plus = pz + pinR > inner, minus = pz - pinR < -inner;
                if (px > ArmRoot && px < ArmTip && (plus || minus))
                {
                    // Одна цапфа на балке, вторая в щели — корпус опрокидывается с палочек.
                    if (!(plus && minus))
                    {
                        v.Destroy($"Сорвалась с палочек: смещение {pz:F1} м поперёк, щель {2 * gap:F1} м");
                        return true;
                    }
                    // Поперёк держат балки (зажим ниже) — скользит корпус только вдоль палочек.
                    double sink = -vy, slide = Math.Abs(vx);
                    double tilt = Math.Acos(MathD.Clamp(ny, -1, 1)) * Constants.Rad2Deg;
                    if (sink > CatchSinkLimit) { v.Destroy($"Удар о палочки: снижение {sink:F1} м/с"); return true; }
                    if (slide > CatchSlideLimit) { v.Destroy($"Сорвалась с палочек: скольжение {slide:F1} м/с"); return true; }
                    if (tilt > CatchMaxTilt) { v.Destroy($"Сорвалась с палочек: наклон {tilt:F0}°"); return true; }
                    Hang(v, body, t, o, origin, ex, ey, px, armY - pinUp, sink, d.TargetName);
                    return true;
                }
                if (px > ArmRoot && px < ArmTip + pinR) v.Raise($"Цапфы прошли мимо палочек «{d.TargetName}»: щель {2 * gap:F1} м");
            }

            // Балки и корпус: корпус на высоте балок (днище ниже их верха, цапфы выше) над палочками по x.
            double barTop = armY + ArmBarHalfHeight;
            if (botY < barTop && above > 0 && hx > ArmRoot && hx < ArmTip + hull)
            {
                double az = Math.Abs(hz), sign = hz >= 0 ? 1 : -1;
                double inner = gap - ArmBarHalf, outer = gap + ArmBarHalf;
                bool inside = az < gap;
                double depth = inside ? az + hull - inner : outer - (az - hull);
                if (depth > 0)
                {
                    // Днище только что опустилось на верх балки — корпус сел на неё сверху.
                    if (botY - vy * dt >= barTop)
                    {
                        v.Destroy($"Удар о палочки: корпус на балке, {hz:F1} м от оси");
                        return true;
                    }
                    // Внутри щели сходящаяся балка толкает корпус к оси, снаружи — корпус сам влетел в балку.
                    double dir = inside ? -sign : sign;
                    double rel = inside ? closing + vz * sign : -vz * sign - closing;
                    if (rel > ArmHitSpeed)
                    {
                        v.Destroy($"Удар о палочки: {rel:F1} м/с сбоку");
                        return true;
                    }
                    // Балка выталкивает корпус всегда, а скорость гасит, только когда он идёт на неё: при наклоне ЦМ
                    // идёт к оси, а корпус на высоте балок — наружу (замер: корабль под 8° остался в балке на 2,3 м).
                    v.Position += o * (ez * (dir * depth));
                    // Каретка на амортизаторах ведёт корпус со скоростью балки (без отскока).
                    if (rel > 0) v.Velocity += o * (ez * (dir * rel));
                }
                // Сведённые палочки зажимают корпус поперёк (зазор 0,2 м): ход каретки кончился — поперечной скорости нет.
                // Без зажима толчок балки 3,8 м/с уносил Super Heavy к другой балке, автопилот клал его на 10° (замер).
                if (v.CatchArms >= 1 && az < gap)
                {
                    double vzNow = Vector3d.Dot(o.Inverse * (v.Velocity - Vector3d.Cross(spin, v.Position)), ez);
                    v.Velocity -= o * (ez * vzNow);
                }
            }
            return false;
        }

        /// <summary>
        /// Борт повис на палочках: Landed с якорем ЦМ под цапфами, ось — по местной вертикали, по центру щели (балки
        /// свели корпус), снижение уходит в амортизаторы каретки (подвеска, FlightPhysics.StepSuspension).
        /// </summary>
        static void Hang(Vessel v, CelestialBody body, double t, QuaternionD o, Vector3d origin, Vector3d ex, Vector3d ey,
            double px, double comY, double sink, string tower)
        {
            v.Situation = Situation.Landed;
            v.AnchorBodyFixed = origin + ex * px + ey * comY;
            v.Suspension = 0;
            v.SuspensionRate = -Math.Max(0, sink);
            v.AttitudeBodyFixed = o.SwapYZ.Inverse * v.Attitude;
            var nose = (v.AttitudeBodyFixed * Vector3d.up).SwapYZ.normalized;
            var up = v.AnchorBodyFixed.normalized;
            if (Vector3d.Dot(nose, up) < 1 - 1e-12)
                v.AttitudeBodyFixed = QuaternionD.FromToRotation(nose, up).SwapYZ * v.AttitudeBodyFixed;
            v.RollSpeed = 0;
            v.CatchArms = 1;
            v.TowerCaught = true;
            v.TouchdownSink = sink;
            FlightPhysics.UpdateLandedPose(v, t);
            v.Raise($"Поймана палочками «{tower}»: снижение {sink:F1} м/с, {px:+0;-0} м вдоль палочек");
        }
    }
}
