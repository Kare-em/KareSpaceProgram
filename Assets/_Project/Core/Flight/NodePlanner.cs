using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>Куда ставить новый узел манёвра.</summary>
    public enum NodeAnchor
    {
        Apoapsis,
        Periapsis,
        Ahead,
        /// <summary>Через виток после последнего узла (или от «сейчас»). В круг N (Cycle, % 3) не входит.</summary>
        NextOrbit,
    }

    /// <summary>
    /// Орбита, на которой стоит узел, — до его импульса: участок прогноза с узлами, идущими раньше него.
    /// У первого узла в текущей сфере это ровно <see cref="NodePlanner.CurrentOrbit"/>.
    /// </summary>
    public struct NodeFrame
    {
        public KeplerOrbit Orbit;
        public CelestialBody Body;
        /// <summary>Границы участка прогноза, на котором лежит узел (конец — смена сферы, вход в атмосферу или следующий узел).</summary>
        public double Start, End;
        public bool Valid => Orbit != null;
    }

    /// <summary>
    /// Планирование манёвров игроком (GDD §6.11, бортовой вычислитель): поставить узел на апсиде, двигать его
    /// по времени, менять компоненты импульса, скруглить орбиту. Компоненты задаются в осях орбиты на момент
    /// узла (как в KSP), а остаток <see cref="ManeuverNode.Remaining"/> пересчитывается из них.
    /// Узлов может быть несколько (<see cref="Vessel.Nodes"/>, по времени): у второго и дальше оси — по орбите
    /// ПОСЛЕ предыдущих узлов (<see cref="NodeFrame"/>), поэтому правка первого узла поворачивает и импульсы
    /// следующих — ровно как в KSP. Порядок узлов неизменен: время узла зажато между соседями (MinGap).
    /// </summary>
    public static class NodePlanner
    {
        /// <summary>Узел не раньше, чем через столько секунд: развернуться и осадить топливо нужно успеть.
        /// Пара: NodeAutopilot — ullage 4 с и разворот на 3°.</summary>
        public const double MinLead = 30;
        /// <summary>«Узел впереди» для гиперболы и как запасной вариант, с.</summary>
        public const double AheadTime = 600;
        /// <summary>Наименьший промежуток между соседними узлами, с: порядок узлов не меняется, а импульс второго
        /// всегда ставится на орбиту после первого. Пара: MinLead (столько же нужно на разворот между прожигами).</summary>
        public const double MinGap = MinLead;
        /// <summary>Узел на эллипсе — не дальше стольких витков от нижней границы: дальше прогноз и варп не заглядывают.</summary>
        public const int MaxRevs = 10;
        /// <summary>Участков прогноза до узла, сверх числа узлов перед ним (смены сфер: Земля → Луна → Земля).</summary>
        const int FramePatches = 4;

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
                case NodeAnchor.NextOrbit: t = o.IsElliptic ? now + o.Period : double.NaN; break;
                default: t = now + AheadTime; break;
            }
            if (double.IsNaN(t) || double.IsInfinity(t)) return double.NaN;
            // Апсида через секунды — берём следующую (через виток): иначе узел уже позади, пока разворачиваемся.
            if (t < now + MinLead) t = o.IsElliptic ? t + o.Period : double.NaN;
            return t;
        }

        // ------------------------------------------------------------------ орбита узла

        /// <summary>
        /// Прогноз с первыми count узлами: на нём лежат узлы с индексом count и дальше. Остатки узлов очереди
        /// при этом пересчитываются (refresh) — после правки предыдущих их оси повернулись.
        /// </summary>
        public static List<OrbitPatch> PatchesBefore(Vessel v, double now, int count)
        {
            count = Math.Max(0, Math.Min(count, v.Nodes.Count));
            var nodes = v.Nodes.GetRange(0, count);
            return PatchedConics.PredictNodes(v.Body, v.Position, v.Velocity, now, nodes, FramePatches + count, true);
        }

        /// <summary>Участок прогноза, на котором лежит момент t (последний начавшийся не позже t).</summary>
        public static NodeFrame Pick(List<OrbitPatch> patches, double t)
        {
            OrbitPatch best = null;
            if (patches != null)
                foreach (var p in patches)
                    if (p.StartTime <= t || best == null) best = p;
            if (best == null) return default;
            return new NodeFrame { Orbit = best.Orbit, Body = best.Body, Start = best.StartTime, End = best.EndTime };
        }

        /// <summary>Орбита узла n (до его импульса). Узел вне списка — как новый узел в момент n.Time.</summary>
        public static NodeFrame FrameOf(Vessel v, double now, ManeuverNode n)
        {
            int i = v.Nodes.IndexOf(n);
            return FrameAt(v, now, n.Time, i >= 0 ? i : CountBefore(v, n.Time));
        }

        /// <summary>Орбита в момент t с учётом первых count узлов.</summary>
        public static NodeFrame FrameAt(Vessel v, double now, double t, int count)
        {
            if (count == 0 && v.Body != null)
            {
                // Первый узел в текущей сфере — орбита из состояния, без поиска событий (дёшево, как было до очереди).
                var o = CurrentOrbit(v, now);
                if (InsideSoi(o, v.Body, now, t)) return new NodeFrame { Orbit = o, Body = v.Body, Start = now, End = double.PositiveInfinity };
            }
            var f = Pick(PatchesBefore(v, now, count), t);
            return f.Valid ? f : new NodeFrame { Orbit = CurrentOrbit(v, now), Body = v.Body, Start = now, End = double.PositiveInfinity };
        }

        /// <summary>Орбита наверняка в сфере тела до момента t: эллипс без выхода за SOI и без спутников на пути.</summary>
        static bool InsideSoi(KeplerOrbit o, CelestialBody body, double now, double t)
        {
            if (!o.IsElliptic) return false;
            if (body.Parent != null && !double.IsInfinity(body.SoiRadius) && o.ApoapsisRadius > body.SoiRadius) return false;
            if (o.PeriapsisRadius < PatchedConics.RailsFloorRadius(body)) return false;
            foreach (var c in body.Children)
            {
                var co = c.OrbitAt(now);
                double cMin = co != null ? co.PeriapsisRadius : 0;
                if (o.ApoapsisRadius >= cMin - c.SoiRadius) return false;
            }
            return true;
        }

        /// <summary>Сколько узлов строго раньше t (индекс, под которым встанет новый узел).</summary>
        public static int CountBefore(Vessel v, double t)
        {
            int k = 0;
            while (k < v.Nodes.Count && v.Nodes[k].Time < t) k++;
            return k;
        }

        /// <summary>
        /// Допустимое время узла: не раньше now + MinLead и предыдущего узла + MinGap, не позже следующего − MinGap,
        /// конца прогноза (атмосфера) и MaxRevs витков. patches — прогноз с узлами до этого (PatchesBefore).
        /// </summary>
        public static void TimeRange(Vessel v, double now, int index, List<OrbitPatch> patches, out double lo, out double hi)
        {
            lo = now + MinLead;
            hi = double.PositiveInfinity;
            if (index > 0 && index - 1 < v.Nodes.Count) lo = Math.Max(lo, v.Nodes[index - 1].Time + MinGap);
            int next = index + 1;
            if (index >= 0 && index < v.Nodes.Count && next < v.Nodes.Count) hi = v.Nodes[next].Time - MinGap;
            if (patches != null && patches.Count > 0)
            {
                var last = patches[patches.Count - 1];
                double end = last.EndTime;
                if (double.IsInfinity(end) || double.IsNaN(end))
                    end = last.Orbit.IsElliptic ? Math.Max(lo, last.StartTime) + MaxRevs * last.Orbit.Period
                        : last.StartTime + PatchedConics.DefaultHorizon(last.Orbit, last.Body);
                else if (last.EndType == TransitionType.None && last.Orbit.IsElliptic)
                    // Обрыв по числу участков, а не по событию: дальше та же орбита, считаем витками.
                    end = Math.Max(end, Math.Max(lo, last.StartTime) + MaxRevs * last.Orbit.Period);
                hi = Math.Min(hi, end - 1);
            }
            if (hi < lo) hi = lo;
        }

        // ------------------------------------------------------------------ правки одного узла

        /// <summary>Остаток импульса по орбите узла и компонентам.</summary>
        public static void Recompute(Vessel v, double now, ManeuverNode n, NodeFrame f = default)
        {
            if (n == null) return;
            if (!f.Valid) f = FrameOf(v, now, n);
            n.Remaining = n.WorldDeltaV(f.Orbit);
        }

        /// <summary>Остаток текущего узла по текущей орбите и компонентам.</summary>
        public static void Recompute(Vessel v, double now) => Recompute(v, now, v.Node);

        /// <summary>
        /// Компоненты — из остатка. После частичного прожига (руками или прерванным автопилотом) остаток
        /// меньше исходного импульса, а пересчёт по старым компонентам вернул бы его целиком — и борт
        /// прожёг бы манёвр второй раз. Поэтому каждая правка начинается с проекции остатка на оси узла.
        /// Только для первого узла: у узлов очереди истина — компоненты, а остаток мог устареть после правки
        /// предыдущих (его обновляет прогноз), и проекция устаревшего остатка исказила бы компоненты.
        /// </summary>
        public static void SyncFromRemaining(Vessel v, double now, ManeuverNode n, NodeFrame f = default)
        {
            if (n == null || n != v.Node) return;
            if (!f.Valid) f = FrameOf(v, now, n);
            f.Orbit.GetState(n.Time, out var r, out var vel);
            var pro = vel.normalized;
            var nrm = Vector3d.Cross(r, vel).normalized;
            var rad = Vector3d.Cross(pro, nrm);
            n.Prograde = Vector3d.Dot(n.Remaining, pro);
            n.Normal = Vector3d.Dot(n.Remaining, nrm);
            n.Radial = Vector3d.Dot(n.Remaining, rad);
        }

        public static void SyncFromRemaining(Vessel v, double now) => SyncFromRemaining(v, now, v.Node);

        /// <summary>Прибавить к компонентам узла (м/с). f — закэшированная орбита узла (при перетаскивании ручки).</summary>
        public static void Adjust(Vessel v, double now, ManeuverNode n, double prograde, double normal, double radial,
            NodeFrame f = default)
        {
            if (n == null) return;
            if (!f.Valid) f = FrameOf(v, now, n);
            SyncFromRemaining(v, now, n, f);
            n.Prograde += prograde;
            n.Normal += normal;
            n.Radial += radial;
            Recompute(v, now, n, f);
        }

        public static void Adjust(Vessel v, double now, double prograde, double normal, double radial) =>
            Adjust(v, now, v.Node, prograde, normal, radial);

        /// <summary>Задать компоненты целиком (числовые поля окна узла).</summary>
        public static void SetComponents(Vessel v, double now, ManeuverNode n, double prograde, double normal, double radial)
        {
            if (n == null) return;
            var f = FrameOf(v, now, n);
            n.Prograde = prograde;
            n.Normal = normal;
            n.Radial = radial;
            Recompute(v, now, n, f);
        }

        /// <summary>
        /// Поставить узел на время t (зажатое в <see cref="TimeRange"/>). Компоненты сохраняются: игрок двигает
        /// уже настроенный импульс. patches — прогноз до узла, если уже посчитан (перетаскивание по орбите).
        /// </summary>
        public static void SetTime(Vessel v, double now, ManeuverNode n, double t, List<OrbitPatch> patches = null)
        {
            if (n == null) return;
            int i = v.Nodes.IndexOf(n);
            if (i < 0) return;
            // Проекция остатка — по СТАРОМУ месту узла: на нём остаток и копился.
            SyncFromRemaining(v, now, n);
            patches ??= PatchesBefore(v, now, i);
            TimeRange(v, now, i, patches, out double lo, out double hi);
            n.Time = Math.Max(lo, Math.Min(hi, t));
            Recompute(v, now, n, Pick(patches, n.Time));
        }

        /// <summary>Сдвиг узла по времени на dt.</summary>
        public static void Shift(Vessel v, double now, ManeuverNode n, double dt)
        {
            if (n != null) SetTime(v, now, n, n.Time + dt);
        }

        public static void Shift(Vessel v, double now, double dt) => Shift(v, now, v.Node, dt);

        /// <summary>
        /// Скругление в точке узла: скорость после импульса — круговая √(μ/r), горизонтально в плоскости орбиты.
        /// Нормальная составляющая обнуляется — наклон не меняем. Компоненты раскладываются в те же оси, что
        /// <see cref="ManeuverNode.WorldDeltaV"/>, поэтому пересчёт даёт тот же вектор.
        /// </summary>
        public static void Circularize(Vessel v, double now, ManeuverNode n)
        {
            if (n == null) return;
            var f = FrameOf(v, now, n);
            var o = f.Orbit;
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
            Recompute(v, now, n, f);
        }

        public static void Circularize(Vessel v, double now) => Circularize(v, now, v.Node);

        // ------------------------------------------------------------------ очередь узлов

        /// <summary>Поставить пустой узел вместо всех (как до очереди: клавиша N без узлов); false — привязка недоступна.</summary>
        public static bool Create(Vessel v, double now, NodeAnchor where)
        {
            if (!CanPlan(v)) return false;
            var o = CurrentOrbit(v, now);
            double t = AnchorTime(o, now, where);
            if (double.IsNaN(t)) return false;
            v.Nodes.Clear();
            v.Node = new ManeuverNode { Time = t };
            Recompute(v, now);
            return true;
        }

        /// <summary>
        /// Добавить пустой узел ПОСЛЕ последнего — на орбите, которая получится после всех узлов
        /// (окно узлов: «на Ap», «на Pe», «+виток»). Вернуть узел или null, если привязки нет.
        /// </summary>
        public static ManeuverNode Add(Vessel v, double now, NodeAnchor where)
        {
            if (!CanPlan(v)) return null;
            int count = v.Nodes.Count;
            double from = now;
            if (count > 0) from = Math.Max(now, v.Nodes[count - 1].Time);
            var patches = PatchesBefore(v, now, count);
            var f = Pick(patches, from);
            if (!f.Valid) return null;
            // Минимальный отступ от предыдущего узла — MinGap, от «сейчас» — MinLead (AnchorTime отсчитывает от from).
            double t = AnchorTime(f.Orbit, from, where);
            if (double.IsNaN(t)) return null;
            TimeRange(v, now, count, patches, out double lo, out double hi);
            if (t < lo || t > hi) return null;
            var n = new ManeuverNode { Time = t };
            v.Nodes.Add(n);
            Recompute(v, now, n, Pick(patches, t));
            return n;
        }

        /// <summary>
        /// Вставить пустой узел в момент t (клик по траектории на карте). Узлы позже t остаются в очереди:
        /// их компоненты — в осях своей орбиты, прогноз пересчитает им остаток. Вставка раньше текущего узла
        /// делает новый узел текущим (исполняемый узел защищает слой игры). null — t вне допустимого.
        /// </summary>
        public static ManeuverNode InsertAt(Vessel v, double now, double t)
        {
            if (!CanPlan(v) || double.IsNaN(t)) return null;
            int i = CountBefore(v, t);
            if (t < now + MinLead) return null;
            if (i > 0 && t < v.Nodes[i - 1].Time + MinGap) return null;
            if (i < v.Nodes.Count && t > v.Nodes[i].Time - MinGap) return null;
            // Прежний текущий узел уходит в очередь: его частичный прожиг — в компоненты, пока остаток в силе.
            if (i == 0 && v.Nodes.Count > 0) SyncFromRemaining(v, now);
            var patches = PatchesBefore(v, now, i);
            var f = Pick(patches, t);
            if (!f.Valid || t > f.End) return null;
            var n = new ManeuverNode { Time = t };
            v.Nodes.Insert(i, n);
            Recompute(v, now, n, f);
            return n;
        }

        /// <summary>
        /// Удалить узел; если это текущий — следующий становится текущим, его остаток считается заново
        /// по реальной орбите (до этого он считался по прогнозу, а после прожига орбита чуть другая).
        /// </summary>
        public static void Delete(Vessel v, double now, ManeuverNode n)
        {
            if (n == null) return;
            bool first = n == v.Node;
            v.RemoveNode(n);
            if (first) Promote(v, now);
        }

        /// <summary>Пересчитать остаток нового текущего узла (после исполнения или удаления предыдущего).</summary>
        public static void Promote(Vessel v, double now)
        {
            var n = v.Node;
            if (n == null || v.Body == null || !v.Alive) return;
            Recompute(v, now, n);
        }

        /// <summary>Пересчитать остатки узлов очереди (оси по орбите после предыдущих узлов).</summary>
        public static void Refresh(Vessel v, double now)
        {
            if (v.Nodes.Count < 2 || v.Body == null) return;
            PatchedConics.PredictNodes(v.Body, v.Position, v.Velocity, now, v.Nodes, FramePatches + v.Nodes.Count, true);
        }

        /// <summary>
        /// Следующая привязка по кругу Ap → Pe → «впереди» (N) для узла n, недоступные пропускаются.
        /// Отсчёт — от нижней границы узла (сейчас или предыдущий узел), компоненты сохраняются.
        /// </summary>
        public static NodeAnchor Cycle(Vessel v, double now, ManeuverNode n, NodeAnchor current)
        {
            int i = v.Nodes.IndexOf(n);
            if (i < 0) return current;
            var patches = PatchesBefore(v, now, i);
            TimeRange(v, now, i, patches, out double lo, out double hi);
            double from = i > 0 ? Math.Max(now, v.Nodes[i - 1].Time) : now;
            var f = Pick(patches, Math.Max(from, lo));
            if (!f.Valid) return current;
            for (int k = 1; k <= 3; k++)
            {
                var next = (NodeAnchor)(((int)current + k) % 3);
                double t = AnchorTime(f.Orbit, from, next);
                if (double.IsNaN(t) || t < lo || t > hi) continue;
                SetTime(v, now, n, t, patches);
                return next;
            }
            return current;
        }

        /// <summary>Круг привязок для текущего узла (старый вход: клавиша N).</summary>
        public static NodeAnchor Cycle(Vessel v, double now, NodeAnchor current) => Cycle(v, now, v.Node, current);

        /// <summary>Сумма Δv узла по компонентам (у узлов очереди остаток может быть ещё не пересчитан).</summary>
        public static double PlannedTotal(ManeuverNode n) =>
            n == null ? 0 : Math.Sqrt(n.Prograde * n.Prograde + n.Normal * n.Normal + n.Radial * n.Radial);
    }
}
