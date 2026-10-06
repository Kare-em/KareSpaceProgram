using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Несущая поверхность секции (GDD §4.6): крыло (пара консолей), киль или стабилизатор. Геометрия — в осях
    /// секции: +Y — нос, «верх» борта — −X (на положительном угле атаки поток набегает на брюхо +X), Z — размах.
    /// Рули — доля площади поверхности; их отклонение физика не рисует, а даёт управляющий момент (Vessel.AeroControlTorque).
    /// </summary>
    public sealed class WingDef
    {
        public string Name = "Крыло";
        /// <summary>Площадь, м²: у крыла — обе консоли вместе с подфюзеляжной частью (как в справочниках:
        /// «Шаттл» 249,9 м², «Буран» 250 м²), у киля — одна плоскость.</summary>
        public double Area;
        /// <summary>Размах обеих консолей, м; у киля — высота над корнем.</summary>
        public double Span;
        /// <summary>Стреловидность по линии 1/4 хорд, °. Сдвигает фокус консоли назад и затягивает срыв.</summary>
        public double Sweep;
        /// <summary>Угол установки к оси борта, °: плюс — передняя кромка вверх (к −X).</summary>
        public double Incidence;
        /// <summary>Поперечное V, °: плюс — концы вверх. Даёт поперечную устойчивость (крен от скольжения).</summary>
        public double Dihedral;
        /// <summary>Фокус корневой хорды (1/4 хорды) над низом секции, м.</summary>
        public double Height;
        /// <summary>Корень над осью секции, м (к −X): низкоплан — минус; у киля — от оси вверх.</summary>
        public double Offset;
        /// <summary>Киль (плоскость XY): одна плоскость, нормаль — Z.</summary>
        public bool Vertical;
        /// <summary>Доля площади на рулях (элевоны, руль высоты, руль направления) и их предельное отклонение, °.</summary>
        public double ControlFraction = 0.2, ControlMaxDeg = 20;
        /// <summary>Щиток-тормоз, м² (расщепляемый руль направления «Шаттла»): Cd 1 при Vessel.AirBrake = 1.</summary>
        public double BrakeArea;
        /// <summary>Сопротивление трения плоскости на её площадь.</summary>
        public double Cd0 = 0.008;
        /// <summary>
        /// Скорость привода рулей, °/с (вид, VesselView.Controls): «Шаттл» — элевоны ~20°/с, руль ~14°/с. Ядро отдаёт
        /// команду сразу (ControlAuthority — бюджет момента на шаг), привод догоняет её на картинке.
        /// </summary>
        public double ControlRateDeg = 20;
        /// <summary>
        /// Раскрытие створок руля-тормоза на сторону при Vessel.AirBrake = 1, ° («Шаттл»: створки до 87° вместе — по ~44°).
        /// Пара: BrakeArea — площадь, которая при этом раскрытии даёт Cd 1.
        /// </summary>
        public double BrakeMaxDeg = 44;
        /// <summary>
        /// Балансировочный щиток (body flap) под соплами, м²: ход задаёт триммер тангажа Vessel.PitchTrim (§4.6).
        /// «Шаттл» 6,1 × 2,1 м ≈ 12,6 м², ход −11,7°…+22,5° (модель симметричная, BodyFlapMaxDeg). Только у крыла.
        /// </summary>
        public double BodyFlapArea, BodyFlapMaxDeg = 20;
        /// <summary>
        /// Подвижные поверхности для вида: шарниры, хорды, смешение команд. null — Aerodynamics.DefaultSurfaces строит их
        /// по самой плоскости (конструктор, крылья без модели). Физику не меняют: момент рулей — ControlFraction/ControlMaxDeg.
        /// </summary>
        public List<ControlSurface> Surfaces;

        /// <summary>Средняя хорда, м.</summary>
        public double MeanChord => Span > 0 ? Area / Span : 0;
        /// <summary>Удлинение: у крыла b²/S; киль стоит на корпусе как на экране — вдвое больше (2h²/S).</summary>
        public double AspectRatio => Span <= 0 || Area <= 0 ? 0 : Vertical ? 2 * Span * Span / Area : Span * Span / Area;
        public WingDef Clone()
        {
            var c = (WingDef)MemberwiseClone();
            if (Surfaces != null) c.Surfaces = Surfaces.ConvertAll(s => s.Clone());
            return c;
        }
    }

    /// <summary>
    /// Подвижная поверхность крыла (GDD §4.6): элевон, руль высоты/направления, половина расщепляемого руля-тормоза,
    /// балансировочный щиток, закрылок Starship. Чистые данные для вида — общий механизм для любых WingDef.
    /// Оси секции, как у WingDef: +Y — нос, +X — брюхо, Z — размах; начало — низ секции.
    /// Угол, °: MaxDeg·clamp(MixYaw·рыскание + MixRoll·крен + MixPitch·тангаж + MixTrim·триммер, −1…1) + BrakeDeg·тормоз;
    /// плюс — задняя кромка уходит к Up. Команды — доли хода Vessel.ControlDeflection (то же, что крутит борт физика).
    /// </summary>
    public sealed class ControlSurface
    {
        public string Name = "Руль";
        /// <summary>Шарнир от A (корень) до B (конец), м.</summary>
        public Vector3d HingeA, HingeB;
        /// <summary>Направление хорды от шарнира к задней кромке (обычно −Y).</summary>
        public Vector3d Aft = new Vector3d(0, -1, 0);
        /// <summary>Куда уходит задняя кромка на положительном угле: горизонтальные — −X (вверх), киль — +Z.</summary>
        public Vector3d Up = new Vector3d(-1, 0, 0);
        /// <summary>Хорда у A и у B и толщина у шарнира, м.</summary>
        public double ChordA, ChordB, Thickness = 0.3;
        public double MixYaw, MixRoll, MixPitch, MixTrim, BrakeDeg;
        /// <summary>Предельный угол по командам рулей, °, и скорость привода, °/с.</summary>
        public double MaxDeg = 20, RateDeg = 20;
        /// <summary>Нижняя сторона из чёрных плиток ТЗП (элевоны, щиток): палитра вида.</summary>
        public bool DarkBelly;
        /// <summary>
        /// Сдвиг внешних углов задней кромки вдоль шарнира (от A к B, м) у A и у B — стреловидность и скос боковой кромки:
        /// 0 — хорда строго поперёк шарнира (прямоугольник/трапеция). Только форма вида, аэродинамику не меняет.
        /// </summary>
        public double TipShiftA, TipShiftB;
        /// <summary>
        /// Положение «убрано», °: если больше 0, вид складывает створку на этот угол (к Up), пока борт не в потоке
        /// (закрылки Starship на подъёме, в вакууме и на посадочном импульсе). Только вид — физика не знает про складывание.
        /// </summary>
        public double StowDeg;
        /// <summary>Визуальное усиление команды (только вид): доля хода рулей Starship мала — момент делит большой РСУ,
        /// а не аэродинамика. Пара: MaxDeg — итог всё равно режется пределом хода.</summary>
        public double VisualGain = 1;

        /// <summary>Заданный угол, °. cmd — доли хода (x рыскание, y крен, z тангаж), trim −1…1, brake 0…1.</summary>
        public double Angle(Vector3d cmd, double trim, double brake)
        {
            double u = MixYaw * cmd.x + MixRoll * cmd.y + MixPitch * cmd.z + MixTrim * trim;
            u = u < -1 ? -1 : u > 1 ? 1 : u;
            return MaxDeg * u + BrakeDeg * (brake < 0 ? 0 : brake > 1 ? 1 : brake);
        }

        public ControlSurface Clone() => (ControlSurface)MemberwiseClone();
    }

    /// <summary>Одна плоскость в связанных осях пакета: точка приложения (y — от низа пакета), нижняя нормаль, хорда.</summary>
    public struct WingPanel
    {
        public Vector3d Pos, Normal, Chord;
        public double Area, AspectRatio, Sweep, Cd0, ControlArea, ControlMax, BrakeArea;
        /// <summary>От центра давления до шарнира руля, м (полхорды): плечо руля длиннее плеча самой плоскости.</summary>
        public double HingeAft;
        public bool Vertical;
        /// <summary>Балансировочный щиток: площадь 0 (обтекание уже в корпусе), только момент триммера — TrimAuthority.</summary>
        public bool Trim;
    }

    /// <summary>
    /// Аэродинамика крыльев (GDD §4.6). Каждая консоль — плоская пластина в своём местном потоке (скорость борта плюс ω×r,
    /// отсюда демпфирование), коэффициент нормальной силы:
    ///   CN = f·(CLα·sin α·|cos α| + Kv·sin α·|sin α|) + (1 − f)·CN90·sin α,
    /// CLα — Хелмболд/DATCOM на дозвуке (с поправкой Прандтля — Глауэрта), 4/√(M² − 1) на сверхзвуке; Kv·sin² — вихревая
    /// (нелинейная) добавка малых удлинений, на гиперзвуке Kv → 2 — ньютоновская пластина 2·sin²α; f — доля
    /// безотрывного обтекания: срыв на дозвуке после αs = 15° + до 15° за малое удлинение + до 10° за стреловидность.
    /// Сопротивление: трение Cd0 + индуктивное CL²/(π·e·A) при подсосе передней кромки (дозвук, без срыва), иначе
    /// проекция нормальной силы CN·sin α.
    /// </summary>
    public static class Aerodynamics
    {
        /// <summary>Центр давления консоли — доля полуразмаха от корня (трапеция между треугольником 1/3 и прямоугольником 1/2).</summary>
        public const double SpanCentroid = 0.42;
        /// <summary>Коэффициент Освальда.</summary>
        public const double Oswald = 0.8;
        /// <summary>Отношение наклона профиля к 2π (DATCOM).</summary>
        const double Kappa = 0.95;
        /// <summary>Нормальная сила пластины поперёк потока: дозвук 1,17 (Хёрнер), ньютоновский предел 2.</summary>
        const double Cn90Sub = 1.17, Cn90Hyper = 2;
        /// <summary>Вихревая добавка Kv: дозвук 1,2 (Полхамус для малых удлинений), гиперзвук 2 (Ньютон).</summary>
        const double KvSub = 1.2, KvHyper = 2;
        /// <summary>Срыв: базовый угол, добавки за удлинение (A &lt; 3) и стреловидность, ширина перехода, °.</summary>
        const double StallBase = 15, StallLowAr = 15, StallSweep = 10, StallWidth = 10;
        /// <summary>Эффективность руля: доля CLα при отклонении части хорды (τ для руля ~25 % хорды).</summary>
        const double FlapEffect = 0.6;

        static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

        /// <summary>Доля гиперзвукового (ньютоновского) поведения: 0 до М 1, 1 от М 5.</summary>
        public static double Hypersonic(double mach) => Clamp01((mach - 1) / 4);

        /// <summary>Наклон CL(α), 1/рад, плоскости удлинения ar и стреловидности sweep (рад).</summary>
        public static double LiftSlope(double ar, double sweep, double mach)
        {
            if (ar <= 0) return 0;
            double m = Math.Min(mach, 0.9);
            double beta2 = 1 - m * m;
            double t = Math.Tan(sweep);
            double sub = 2 * Math.PI * ar / (2 + Math.Sqrt(4 + ar * ar * beta2 / (Kappa * Kappa) * (1 + t * t / beta2)));
            if (mach <= 1) return sub;
            return Math.Min(sub, 4 / Math.Sqrt(mach * mach - 1));
        }

        /// <summary>Угол начала срыва, рад.</summary>
        public static double StallAngle(double ar, double sweep) =>
            (StallBase + StallLowAr * Clamp01((3 - ar) / 2) + StallSweep * Clamp01(Math.Abs(sweep) * Constants.Rad2Deg / 60)) * Constants.Deg2Rad;

        /// <summary>CN(α) плоскости, α — угол к хорде (−π…π, хвостом вперёд — так же). attached — доля безотрывного.</summary>
        public static double NormalCoef(double alpha, double mach, double ar, double sweep, out double attached)
        {
            double s = Math.Sin(alpha), c = Math.Cos(alpha);
            double hyper = Hypersonic(mach);
            double a = Math.Abs(alpha);
            if (a > Math.PI / 2) a = Math.PI - a;
            // Срыв — только на дозвуке; к М 1,2 плавно уходит (скачки, а не отрыв).
            double fSub = Clamp01(1 - (a - StallAngle(ar, sweep)) / (StallWidth * Constants.Deg2Rad));
            double subWeight = Clamp01((1.2 - mach) / 0.4);
            double f = 1 - subWeight * (1 - fSub);
            attached = f;
            double kv = KvSub + (KvHyper - KvSub) * hyper;
            double cn90 = Cn90Sub + (Cn90Hyper - Cn90Sub) * hyper;
            return f * (LiftSlope(ar, sweep, mach) * s * Math.Abs(c) + kv * s * Math.Abs(s)) + (1 - f) * cn90 * s;
        }

        /// <summary>Сила на плоскости, Н, в связанных осях; vLocal — скорость плоскости относительно воздуха.</summary>
        public static Vector3d PanelForce(in WingPanel p, Vector3d vLocal, double rho, double mach)
        {
            double sp2 = vLocal.sqrMagnitude;
            if (sp2 < 1e-6 || rho <= 0) return Vector3d.zero;
            double sp = Math.Sqrt(sp2);
            var vh = vLocal / sp;
            double vn = Vector3d.Dot(vLocal, p.Normal), va = Vector3d.Dot(vLocal, p.Chord);
            double inPlane2 = vn * vn + va * va;
            if (inPlane2 < 1e-9) return vh * (-0.5 * rho * sp2 * p.Area * p.Cd0);
            // Скольжение вдоль размаха нормальной силы не даёт: напор — по скорости в плоскости хорда-нормаль.
            double alpha = Math.Atan2(vn, va);
            double cn = NormalCoef(alpha, mach, p.AspectRatio, p.Sweep, out double f);
            double qn = 0.5 * rho * inPlane2;
            double sinA = vn / Math.Sqrt(inPlane2);
            double cosA = Math.Sqrt(Math.Max(0, 1 - sinA * sinA));
            // Подсос передней кромки: на безотрывном дозвуке проекция CN·sin α на поток заменяется индуктивным CL²/(πeA).
            double suctionShare = f * Clamp01((1.2 - mach) / 0.4);
            double cl = cn * cosA;
            double cdi = p.AspectRatio > 0 ? cl * cl / (Math.PI * Oswald * p.AspectRatio) : 0;
            double suction = Math.Max(0, cn * sinA - cdi) * suctionShare;
            return p.Normal * (-qn * p.Area * cn) + vh * (qn * p.Area * suction - 0.5 * rho * sp2 * p.Area * p.Cd0);
        }

        /// <summary>Плоскости присоединённых секций с крыльями — в buf; верно после Vessel.MassProperties того же шага.</summary>
        public static void CollectPanels(Vessel v, List<WingPanel> buf)
        {
            buf.Clear();
            var secs = v.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                var s = secs[i];
                if (!v.Attached[i] || s.Wings == null || s.IsRadial) continue;
                double bottom = v.SectionBottom(i);
                // Перевёрнутая (пристыкованная носом к носу) секция: высоты от верха, хорда назад.
                bool flip = v.Flipped[i];
                foreach (var w in s.Wings) AddPanels(w, bottom, s.Length, flip, v.ControlLimit, buf);
            }
        }

        /// <summary>limit — доля хода рулей 0…1 (Vessel.ControlLimit, окно детали): режет и угол, и момент.</summary>
        static void AddPanels(WingDef w, double bottom, double length, bool flip, double limit, List<WingPanel> buf)
        {
            if (w.Area <= 0 || w.Span <= 0) return;
            double sweep = w.Sweep * Constants.Deg2Rad, inc = w.Incidence * Constants.Deg2Rad;
            double ar = w.AspectRatio;
            double ctl = w.ControlMaxDeg * Constants.Deg2Rad * (limit < 0 ? 0 : limit > 1 ? 1 : limit);
            var nose = flip ? -Vector3d.up : Vector3d.up;
            if (!w.Vertical && w.BodyFlapArea > 0)
            {
                // Щиток — у самого низа секции под брюхом; удлинение ~3 (6,1 × 2,1 м у «Шаттла»), без стреловидности.
                buf.Add(new WingPanel
                {
                    Pos = new Vector3d(-w.Offset, flip ? bottom + length : bottom, 0),
                    Normal = new Vector3d(1, 0, 0), Chord = nose, AspectRatio = BodyFlapAspect,
                    ControlArea = w.BodyFlapArea, ControlMax = w.BodyFlapMaxDeg * Constants.Deg2Rad, Trim = true,
                });
            }
            if (w.Vertical)
            {
                double reach = SpanCentroid * w.Span;
                double y = w.Height - reach * Math.Tan(sweep);
                var n = new Vector3d(0, 0, 1);
                buf.Add(new WingPanel
                {
                    Pos = new Vector3d(-(w.Offset + reach), flip ? bottom + length - y : bottom + y, 0),
                    Normal = n * Math.Cos(inc) + nose * Math.Sin(inc),
                    Chord = nose * Math.Cos(inc) - n * Math.Sin(inc),
                    Area = w.Area, AspectRatio = ar, Sweep = sweep, Cd0 = w.Cd0,
                    ControlArea = w.Area * w.ControlFraction, ControlMax = ctl, BrakeArea = w.BrakeArea, Vertical = true,
                    HingeAft = 0.5 * w.MeanChord,
                });
                return;
            }
            double g = w.Dihedral * Constants.Deg2Rad;
            double half = w.Span * 0.5, reachH = SpanCentroid * half;
            double yc = w.Height - reachH * Math.Tan(sweep);
            for (int side = -1; side <= 1; side += 2)
            {
                var spanDir = new Vector3d(-Math.Sin(g), 0, side * Math.Cos(g));
                // Нижняя нормаль консоли: брюхо (+X), у поднятой консоли наклонена наружу.
                var n = new Vector3d(Math.Cos(g), 0, side * Math.Sin(g));
                var pos = new Vector3d(-w.Offset, flip ? bottom + length - yc : bottom + yc, 0) + spanDir * reachH;
                buf.Add(new WingPanel
                {
                    Pos = pos,
                    // Угол установки: передняя кромка вверх — хорда отклонена от нижней нормали.
                    Normal = n * Math.Cos(inc) + nose * Math.Sin(inc),
                    Chord = nose * Math.Cos(inc) - n * Math.Sin(inc),
                    Area = w.Area * 0.5, AspectRatio = ar, Sweep = sweep, Cd0 = w.Cd0,
                    ControlArea = w.Area * 0.5 * w.ControlFraction, ControlMax = ctl, BrakeArea = w.BrakeArea * 0.5,
                    HingeAft = 0.5 * w.MeanChord,
                });
            }
        }

        /// <summary>Сумма сил плоскостей, Н, в связанных осях (без вращения — для интегратора поступательного движения).</summary>
        public static Vector3d Force(List<WingPanel> panels, Vector3d vLocal, double rho, double mach)
        {
            var f = Vector3d.zero;
            for (int i = 0; i < panels.Count; i++) f += PanelForce(panels[i], vLocal, rho, mach);
            return f;
        }

        /// <summary>
        /// Момент плоскостей относительно ЦМ (высота com от низа пакета), Н·м: каждая — в своём потоке v + ω×r,
        /// поэтому вращение само себя гасит (демпфирование крыльев и киля).
        /// </summary>
        public static Vector3d Torque(List<WingPanel> panels, Vector3d vLocal, Vector3d omega, double com, double rho, double mach)
        {
            var t = Vector3d.zero;
            var c = new Vector3d(0, com, 0);
            for (int i = 0; i < panels.Count; i++)
            {
                var r = panels[i].Pos - c;
                var f = PanelForce(panels[i], vLocal + Vector3d.Cross(omega, r), rho, mach);
                t += Vector3d.Cross(r, f);
            }
            return t;
        }

        /// <summary>
        /// Управляющий момент рулей по осям (X — рыскание, Y — крен, Z — тангаж), Н·м: прирост нормальной силы рулевой
        /// части при полном отклонении на плечо до ЦМ. Эффективность — FlapEffect·CLα на дозвуке, ньютоновская
        /// 2·sin 2θ (θ — угол руля к потоку, до 45°) на гиперзвуке: на входе с α 40° элевоны «Шаттла» работают, а при α 0 — нет.
        /// </summary>
        public static Vector3d ControlAuthority(List<WingPanel> panels, Vector3d vLocal, double com, double rho, double mach)
        {
            double sp2 = vLocal.sqrMagnitude;
            if (sp2 < 1 || rho <= 0) return Vector3d.zero;
            double hyper = Hypersonic(mach);
            double yaw = 0, roll = 0, pitch = 0;
            for (int i = 0; i < panels.Count; i++)
            {
                var p = panels[i];
                if (p.ControlArea <= 0 || p.Trim) continue;
                double vn = Vector3d.Dot(vLocal, p.Normal), va = Vector3d.Dot(vLocal, p.Chord);
                double inPlane2 = vn * vn + va * va;
                if (inPlane2 < 1) continue;
                double a = Math.Abs(Math.Atan2(vn, va));
                if (a > Math.PI / 2) a = Math.PI - a;
                double theta = Math.Min(a + p.ControlMax * 0.5, Math.PI / 4);
                double slope = (1 - hyper) * FlapEffect * LiftSlope(p.AspectRatio, p.Sweep, mach) + hyper * 2 * Math.Sin(2 * theta);
                double dN = 0.5 * rho * inPlane2 * p.ControlArea * slope * p.ControlMax;
                // Руль у задней кромки: плечо до ЦМ — от шарнира (за ЦД плоскости), а не от ЦД.
                double arm = Math.Abs(p.Pos.y - p.HingeAft - com);
                if (p.Vertical)
                {
                    yaw += dN * arm;
                    roll += dN * Math.Abs(p.Pos.x) * 0.5;
                }
                else
                {
                    pitch += dN * arm;
                    roll += dN * Math.Abs(p.Pos.z);
                }
            }
            return new Vector3d(yaw, roll, pitch);
        }

        /// <summary>Удлинение балансировочного щитка (≈ 6,1 / 2,1 у «Шаттла»).</summary>
        const double BodyFlapAspect = 3;

        /// <summary>
        /// Момент тангажа балансировочного щитка при полном ходе, Н·м (как ControlAuthority по рулевой части); без щитка —
        /// 0, и триммер работает смещением команды элевонов (FlightPhysics.StepFlying).
        /// </summary>
        public static double TrimAuthority(List<WingPanel> panels, Vector3d vLocal, double com, double rho, double mach)
        {
            double sp2 = vLocal.sqrMagnitude;
            if (sp2 < 1 || rho <= 0) return 0;
            double hyper = Hypersonic(mach), pitch = 0;
            for (int i = 0; i < panels.Count; i++)
            {
                var p = panels[i];
                if (!p.Trim) continue;
                double vn = Vector3d.Dot(vLocal, p.Normal), va = Vector3d.Dot(vLocal, p.Chord);
                double inPlane2 = vn * vn + va * va;
                if (inPlane2 < 1) continue;
                double a = Math.Abs(Math.Atan2(vn, va));
                if (a > Math.PI / 2) a = Math.PI - a;
                double theta = Math.Min(a + p.ControlMax * 0.5, Math.PI / 4);
                double slope = (1 - hyper) * FlapEffect * LiftSlope(p.AspectRatio, 0, mach) + hyper * 2 * Math.Sin(2 * theta);
                pitch += 0.5 * rho * inPlane2 * p.ControlArea * slope * p.ControlMax * Math.Abs(p.Pos.y - com);
            }
            return pitch;
        }

        /// <summary>Есть ли среди плоскостей балансировочный щиток.</summary>
        public static bool HasTrimFlap(List<WingPanel> panels)
        {
            for (int i = 0; i < panels.Count; i++) if (panels[i].Trim) return true;
            return false;
        }

        /// <summary>
        /// Подвижные поверхности плоскости для вида, когда у WingDef нет своих Surfaces (конструктор, крыло без модели):
        /// полоса рулей вдоль задней кромки пластины WingMesh на ControlFraction хорды, от SurfaceInboard до SurfaceOutboard
        /// полуразмаха. Крыло — элевоны (тангаж + крен), киль — руль направления; с BrakeArea — две створки-тормоза.
        /// Геометрия — как в WingMesh.Add: пара при правке одного — проверить второе.
        /// </summary>
        public static List<ControlSurface> DefaultSurfaces(WingDef w)
        {
            var list = new List<ControlSurface>();
            double c = w.MeanChord, b = w.Span;
            if (c <= 0 || b <= 0 || (w.ControlFraction <= 0 && w.BrakeArea <= 0)) return list;
            double sweep = Math.Tan(w.Sweep * Constants.Deg2Rad);
            double cf = Math.Max(0.15, w.ControlFraction) * c, th = Math.Max(0.04, 0.08 * c) * 0.6;
            var rootQ = new Vector3d(-w.Offset, w.Height, 0);
            if (w.Vertical)
            {
                // Киль: шарнир на cf перед задней кромкой (3/4 хорды за линией 1/4), от корня вверх (−X).
                Vector3d a = rootQ + new Vector3d(-b * SurfaceInboard, -b * SurfaceInboard * sweep - (0.75 * c - cf), 0);
                Vector3d e = rootQ + new Vector3d(-b * SurfaceOutboard, -b * SurfaceOutboard * sweep - (0.75 * c - cf), 0);
                int halves = w.BrakeArea > 0 ? 2 : 1;
                for (int k = 0; k < halves; k++)
                    list.Add(new ControlSurface
                    {
                        Name = halves == 1 ? "Руль направления" : k == 0 ? "Руль-тормоз +Z" : "Руль-тормоз −Z",
                        HingeA = a, HingeB = e, ChordA = cf, ChordB = cf, Thickness = th, Up = new Vector3d(0, 0, 1),
                        MixYaw = 1, MaxDeg = w.ControlMaxDeg, RateDeg = w.ControlRateDeg,
                        BrakeDeg = halves == 1 ? 0 : k == 0 ? w.BrakeMaxDeg : -w.BrakeMaxDeg,
                    });
                return list;
            }
            if (w.ControlFraction <= 0) return list;
            double inc = w.Incidence * Constants.Deg2Rad, dih = w.Dihedral * Constants.Deg2Rad, half = b * 0.5;
            var ch = new Vector3d(-Math.Sin(inc), Math.Cos(inc), 0);
            for (int side = -1; side <= 1; side += 2)
            {
                var span = new Vector3d(-Math.Sin(dih), 0, side * Math.Cos(dih));
                Vector3d At(double f) => rootQ + span * (half * f) + new Vector3d(0, -half * f * sweep, 0) - ch * (0.75 * c - cf);
                list.Add(new ControlSurface
                {
                    Name = side > 0 ? "Элевон +Z" : "Элевон −Z",
                    HingeA = At(SurfaceInboard), HingeB = At(SurfaceOutboard), ChordA = cf, ChordB = cf, Thickness = th,
                    Aft = -ch, Up = new Vector3d(-Math.Cos(inc), -Math.Sin(inc), 0),
                    // Крен: τy > 0 — сила к брюху на консоли +Z, т. е. задняя кромка вверх (FlightPhysics, правило r × F).
                    MixPitch = 1, MixRoll = side, MixTrim = 1, MaxDeg = w.ControlMaxDeg, RateDeg = w.ControlRateDeg,
                });
            }
            return list;
        }

        /// <summary>Полоса рулей по размаху по умолчанию: от 15 до 95 % полуразмаха (у киля — высоты).</summary>
        const double SurfaceInboard = 0.15, SurfaceOutboard = 0.95;

        /// <summary>Площадь щитков-тормозов, м².</summary>
        public static double BrakeArea(List<WingPanel> panels)
        {
            double a = 0;
            for (int i = 0; i < panels.Count; i++) a += panels[i].BrakeArea;
            return a;
        }

        /// <summary>Площадь горизонтальных плоскостей, м² (опорная площадь для коэффициентов и проверка «планера»).</summary>
        public static double WingArea(List<WingPanel> panels)
        {
            double a = 0;
            for (int i = 0; i < panels.Count; i++) if (!panels[i].Vertical) a += panels[i].Area;
            return a;
        }
    }
}
