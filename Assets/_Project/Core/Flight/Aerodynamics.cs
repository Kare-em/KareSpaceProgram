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

        /// <summary>Средняя хорда, м.</summary>
        public double MeanChord => Span > 0 ? Area / Span : 0;
        /// <summary>Удлинение: у крыла b²/S; киль стоит на корпусе как на экране — вдвое больше (2h²/S).</summary>
        public double AspectRatio => Span <= 0 || Area <= 0 ? 0 : Vertical ? 2 * Span * Span / Area : Span * Span / Area;
        public WingDef Clone() => (WingDef)MemberwiseClone();
    }

    /// <summary>Одна плоскость в связанных осях пакета: точка приложения (y — от низа пакета), нижняя нормаль, хорда.</summary>
    public struct WingPanel
    {
        public Vector3d Pos, Normal, Chord;
        public double Area, AspectRatio, Sweep, Cd0, ControlArea, ControlMax, BrakeArea;
        /// <summary>От центра давления до шарнира руля, м (полхорды): плечо руля длиннее плеча самой плоскости.</summary>
        public double HingeAft;
        public bool Vertical;
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
                foreach (var w in s.Wings) AddPanels(w, bottom, s.Length, flip, buf);
            }
        }

        static void AddPanels(WingDef w, double bottom, double length, bool flip, List<WingPanel> buf)
        {
            if (w.Area <= 0 || w.Span <= 0) return;
            double sweep = w.Sweep * Constants.Deg2Rad, inc = w.Incidence * Constants.Deg2Rad;
            double ar = w.AspectRatio;
            double ctl = w.ControlMaxDeg * Constants.Deg2Rad;
            var nose = flip ? -Vector3d.up : Vector3d.up;
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
                if (p.ControlArea <= 0) continue;
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
