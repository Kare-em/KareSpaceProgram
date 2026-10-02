using System;

namespace Kare.Space.Core
{
    /// <summary>Куда ставить новый узел манёвра.</summary>
    public enum NodeAnchor
    {
        Apoapsis,
        Periapsis,
        Ahead,
    }

    /// <summary>
    /// Планирование манёвра игроком (GDD §6.11, бортовой вычислитель): поставить узел на апсиде, двигать его
    /// по времени, менять компоненты импульса, скруглить орбиту. Компоненты задаются в осях орбиты на момент
    /// узла (как в KSP), а остаток <see cref="ManeuverNode.Remaining"/> пересчитывается из них по текущей орбите.
    /// Узел планируется в сфере текущего тела: за сменой SOI орбита другая, импульс там не ставим.
    /// </summary>
    public static class NodePlanner
    {
        /// <summary>Узел не раньше, чем через столько секунд: развернуться и осадить топливо нужно успеть.
        /// Пара: NodeAutopilot — ullage 4 с и разворот на 3°.</summary>
        public const double MinLead = 30;
        /// <summary>«Узел впереди» для гиперболы и как запасной вариант, с.</summary>
        public const double AheadTime = 600;

        public static KeplerOrbit CurrentOrbit(Vessel v, double now) =>
            KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, now);

        public static bool CanPlan(Vessel v) => v != null && v.Alive && v.Situation == Situation.Flying;

        /// <summary>Время ближайшей точки привязки; NaN — такой нет (апоцентр у гиперболы, перицентр позади).</summary>
        public static double AnchorTime(KeplerOrbit o, double now, NodeAnchor where)
        {
            double t;
            switch (where)
            {
                case NodeAnchor.Apoapsis: t = o.IsElliptic ? now + o.TimeToApoapsis(now) : double.NaN; break;
                case NodeAnchor.Periapsis: t = now + o.TimeToPeriapsis(now); break;
                default: t = now + AheadTime; break;
            }
            if (double.IsNaN(t) || double.IsInfinity(t)) return double.NaN;
            // Апсида через секунды — берём следующую (через виток): иначе узел уже позади, пока разворачиваемся.
            if (t < now + MinLead) t = o.IsElliptic ? t + o.Period : double.NaN;
            return t;
        }

        /// <summary>Поставить пустой узел; вернуть false, если привязка недоступна.</summary>
        public static bool Create(Vessel v, double now, NodeAnchor where)
        {
            if (!CanPlan(v)) return false;
            var o = CurrentOrbit(v, now);
            double t = AnchorTime(o, now, where);
            if (double.IsNaN(t)) return false;
            v.Node = new ManeuverNode { Time = t };
            Recompute(v, now);
            return true;
        }

        /// <summary>Следующая привязка по кругу Ap → Pe → «впереди» (N), недоступные пропускаются.</summary>
        public static NodeAnchor Cycle(Vessel v, double now, NodeAnchor current)
        {
            for (int k = 1; k <= 3; k++)
            {
                var next = (NodeAnchor)(((int)current + k) % 3);
                var keep = v.Node;
                if (keep != null) SyncFromRemaining(v, now);
                if (Create(v, now, next))
                {
                    // Компоненты сохраняем: игрок двигает уже настроенный импульс к другой апсиде.
                    if (keep != null)
                    {
                        v.Node.Prograde = keep.Prograde;
                        v.Node.Normal = keep.Normal;
                        v.Node.Radial = keep.Radial;
                        Recompute(v, now);
                    }
                    return next;
                }
                v.Node = keep;
            }
            return current;
        }

        /// <summary>Остаток импульса по текущей орбите и компонентам.</summary>
        public static void Recompute(Vessel v, double now)
        {
            if (v.Node == null) return;
            v.Node.Remaining = v.Node.WorldDeltaV(CurrentOrbit(v, now));
        }

        /// <summary>
        /// Компоненты — из остатка. После частичного прожига (руками или прерванным автопилотом) остаток
        /// меньше исходного импульса, а пересчёт по старым компонентам вернул бы его целиком — и борт
        /// прожёг бы манёвр второй раз. Поэтому каждая правка начинается с проекции остатка на оси узла.
        /// </summary>
        public static void SyncFromRemaining(Vessel v, double now)
        {
            var n = v.Node;
            if (n == null) return;
            CurrentOrbit(v, now).GetState(n.Time, out var r, out var vel);
            var pro = vel.normalized;
            var nrm = Vector3d.Cross(r, vel).normalized;
            var rad = Vector3d.Cross(pro, nrm);
            n.Prograde = Vector3d.Dot(n.Remaining, pro);
            n.Normal = Vector3d.Dot(n.Remaining, nrm);
            n.Radial = Vector3d.Dot(n.Remaining, rad);
        }

        public static void Adjust(Vessel v, double now, double prograde, double normal, double radial)
        {
            var n = v.Node;
            if (n == null) return;
            SyncFromRemaining(v, now);
            n.Prograde += prograde;
            n.Normal += normal;
            n.Radial += radial;
            Recompute(v, now);
        }

        /// <summary>Сдвиг узла по времени; у эллипса — в пределах одного витка вперёд.</summary>
        public static void Shift(Vessel v, double now, double dt)
        {
            var n = v.Node;
            if (n == null) return;
            SyncFromRemaining(v, now);
            var o = CurrentOrbit(v, now);
            double t = Math.Max(now + MinLead, n.Time + dt);
            if (o.IsElliptic) t = Math.Min(t, now + o.Period + MinLead);
            n.Time = t;
            Recompute(v, now);
        }

        /// <summary>
        /// Скругление в точке узла: скорость после импульса — круговая √(μ/r), горизонтально в плоскости орбиты.
        /// Нормальная составляющая обнуляется — наклон не меняем. Компоненты раскладываются в те же оси, что
        /// <see cref="ManeuverNode.WorldDeltaV"/>, поэтому пересчёт даёт тот же вектор.
        /// </summary>
        public static void Circularize(Vessel v, double now)
        {
            var n = v.Node;
            if (n == null) return;
            var o = CurrentOrbit(v, now);
            o.GetState(n.Time, out var r, out var vel);
            var up = r.normalized;
            var h = Vector3d.Cross(r, vel).normalized;
            var horizontal = Vector3d.Cross(h, up).normalized;
            var dv = horizontal * Math.Sqrt(o.Mu / r.magnitude) - vel;
            var pro = vel.normalized;
            var rad = Vector3d.Cross(pro, h);
            n.Prograde = Vector3d.Dot(dv, pro);
            n.Normal = 0;
            n.Radial = Vector3d.Dot(dv, rad);
            Recompute(v, now);
        }
    }
}
