using System;
using System.Text.RegularExpressions;
using Kare.Space.Core;

// Очередь манёвров (§6.11, 15.10.2026): узлы по очереди в прогнозе, оси узла очереди — по орбите после предыдущих,
// порядок и зажимы времени, исполнение по очереди, сохранение всех узлов и чтение старого формата с одним узлом.
static partial class Program
{
    static void TestNodes()
    {
        var (u, tr) = StartMission("sputnik");
        var earth = u.System.Get("earth");
        u.Teleport(earth, 200e3, true);
        var v = u.Active;
        double now = u.Time;

        // 1. Два узла: +100 м/с в ближайшем апоцентре, +100 м/с в апоцентре орбиты после первого (полвитка спустя).
        var a = NodePlanner.Add(v, now, NodeAnchor.Apoapsis);
        Check("nodes: первый узел поставлен", a != null && v.Node == a && v.Nodes.Count == 1);
        if (a == null) return;
        NodePlanner.Adjust(v, now, a, 100, 0, 0);
        var b = NodePlanner.Add(v, now, NodeAnchor.Apoapsis);
        Check("nodes: второй узел — после первого", b != null && v.Nodes.Count == 2 && v.Node == a && b.Time > a.Time + NodePlanner.MinGap,
            b != null ? $"Δt {(b.Time - a.Time) / 60:F1} мин" : "");
        if (b == null) return;
        NodePlanner.Adjust(v, now, b, 100, 0, 0);

        // Прогноз руками: орбита → +A → орбита 1 → +B → орбита 2. PredictActive обязан дать то же.
        var o0 = NodePlanner.CurrentOrbit(v, now);
        o0.GetState(a.Time, out var r1, out var v1);
        v1 += a.WorldDeltaV(o0);
        var o1 = KeplerOrbit.FromState(r1, v1, earth.Mu, a.Time);
        o1.GetState(b.Time, out var r2, out var v2);
        var bDv = b.WorldDeltaV(o1);
        v2 += bDv;
        var o2 = KeplerOrbit.FromState(r2, v2, earth.Mu, b.Time);
        var ps = u.PredictActive();
        var after1 = ps.Find(p => p.NodeIndex == 0);
        var after2 = ps.Find(p => p.NodeIndex == 1);
        Check("nodes: прогноз режет участки на обоих узлах", after1 != null && after2 != null && after1.AfterNode && after2.AfterNode &&
              after1.StartTime == a.Time && after2.StartTime == b.Time, $"{ps.Count} участков");
        if (after2 == null) return;
        double dPe = Math.Abs(after2.Orbit.PeriapsisRadius - o2.PeriapsisRadius), dAp = Math.Abs(after2.Orbit.ApoapsisRadius - o2.ApoapsisRadius);
        Check("nodes: орбита после двух узлов — как вручную", dPe < 1 && dAp < 1,
            $"Pe {(o2.PeriapsisRadius - earth.Radius) / 1000:F1} км, Ap {(o2.ApoapsisRadius - earth.Radius) / 1000:F1} км; расхождение {dPe:F3}/{dAp:F3} м");
        Check("nodes: остаток второго узла — в осях орбиты после первого", (b.Remaining - bDv).magnitude < 1e-6, $"{(b.Remaining - bDv).magnitude:E1} м/с");
        double pe1 = o1.PeriapsisRadius;
        Check("nodes: второй узел поднял перигей", o2.PeriapsisRadius > pe1 + 100e3,
            $"{(pe1 - earth.Radius) / 1000:F0} → {(o2.PeriapsisRadius - earth.Radius) / 1000:F0} км");

        // 2. Правка первого узла поворачивает оси второго: компоненты те же, остаток — по новой орбите.
        var bRemBefore = b.Remaining;
        NodePlanner.Adjust(v, now, a, 0, 50, 0);
        u.PredictActive();
        Check("nodes: правка первого — компоненты второго целы, остаток пересчитан",
            b.Prograde == 100 && b.Normal == 0 && (b.Remaining - bRemBefore).magnitude > 0.1, $"сдвиг {(b.Remaining - bRemBefore).magnitude:F1} м/с");
        NodePlanner.Adjust(v, now, a, 0, -50, 0);

        // 3. Зажимы времени: порядок узлов не меняется.
        double bt = b.Time, at = a.Time;
        NodePlanner.SetTime(v, now, b, a.Time - 1000);
        Check("nodes: второй не уходит раньше первого", Math.Abs(b.Time - (a.Time + NodePlanner.MinGap)) < 1e-6, $"{b.Time - a.Time:F1} с");
        NodePlanner.SetTime(v, now, b, bt);
        NodePlanner.SetTime(v, now, a, b.Time + 1000);
        Check("nodes: первый не уходит позже второго", Math.Abs(a.Time - (b.Time - NodePlanner.MinGap)) < 1e-6, $"{b.Time - a.Time:F1} с");
        NodePlanner.SetTime(v, now, a, at);
        NodePlanner.SetTime(v, now, a, now - 100);
        Check("nodes: узел не раньше MinLead", Math.Abs(a.Time - (now + NodePlanner.MinLead)) < 1e-6);
        NodePlanner.SetTime(v, now, a, at);

        // 4. Вставка между узлами и удаление.
        var mid = NodePlanner.InsertAt(v, now, 0.5 * (a.Time + b.Time));
        Check("nodes: вставка между узлами", mid != null && v.Nodes.Count == 3 && v.Nodes[1] == mid);
        Check("nodes: вставка вплотную к узлу отклонена", NodePlanner.InsertAt(v, now, a.Time + 1) == null);

        // 5. Сохранение: все узлы с компонентами; старый формат с одним полем Node читается.
        string text = SaveGame.Save(u, tr);
        var g = SaveGame.Load(text, SolarSystem.CreateReal());
        var gv = g.Universe.Active;
        bool same = gv.Nodes.Count == v.Nodes.Count;
        for (int i = 0; same && i < v.Nodes.Count; i++)
            same = gv.Nodes[i].Time == v.Nodes[i].Time && gv.Nodes[i].Prograde == v.Nodes[i].Prograde &&
                   gv.Nodes[i].Remaining.x == v.Nodes[i].Remaining.x;
        Check("nodes: сохранение — все узлы", same, $"{gv.Nodes.Count} из {v.Nodes.Count}");
        var re = new Regex("\"Nodes\":\\[(\\{\"\\$r\":\\d+\\})[^\\]]*\\]");
        string old = re.Replace(text, "\"Node\":$1", 1);
        bool converted = old != text;
        var go = SaveGame.Load(old, SolarSystem.CreateReal());
        var ov = go.Universe.Active;
        Check("nodes: старое сохранение с одним узлом", converted && ov.Nodes.Count == 1 && ov.Node.Time == a.Time && ov.Node.Prograde == a.Prograde,
            converted ? $"узлов {ov.Nodes.Count}" : "формат списка не распознан");

        // 6. Удаление текущего: следующий становится текущим, остаток — по текущей орбите.
        NodePlanner.Delete(v, now, mid);
        Check("nodes: удаление из середины", v.Nodes.Count == 2 && v.Node == a && v.Nodes[1] == b);

        // 7. Исполнение по очереди: NodeAutopilot отрабатывает первый, второй становится текущим, затем и он.
        // У РД-107/108 один запуск — второй прожиг без бесконечных запусков (игровой чит) не загорится.
        bool unlimited = Vessel.UnlimitedIgnitions;
        Vessel.UnlimitedIgnitions = true;
        try { ExecuteTwo(u, tr, v, b, o2, earth); }
        finally { Vessel.UnlimitedIgnitions = unlimited; }

        // 8. Старый вход: «v.Node = узел» (автопилоты) заменяет текущий и снимает узлы очереди не позже него.
        NodePlanner.Add(v, u.Time, NodeAnchor.Apoapsis);
        NodePlanner.Add(v, u.Time, NodeAnchor.NextOrbit);
        var last = v.Nodes[v.Nodes.Count - 1];
        v.Node = new ManeuverNode { Time = last.Time + 10 };
        Check("nodes: v.Node = … — один узел вместо очереди", v.Nodes.Count == 1, $"{v.Nodes.Count}");
        v.Node = null;
        Check("nodes: v.Node = null — очередь пуста", v.Nodes.Count == 0 && v.Node == null);
    }

    static void ExecuteTwo(Universe u, MissionTracker tr, Vessel v, ManeuverNode b, KeplerOrbit o2, CelestialBody earth)
    {
        u.SetWarp(5);
        u.NodePilot = new NodeAutopilot();
        for (int i = 0; i < 80000 && u.NodePilot != null; i++) Steps(u, tr, 1);
        Check("nodes: после первого прожига текущий — второй узел", u.NodePilot == null && v.Nodes.Count == 1 && v.Node == b,
            $"узлов {v.Nodes.Count}; {OrbitText(v, u.Time)}");
        var expect = NodePlanner.CurrentOrbit(v, u.Time);
        u.SetWarp(5);
        u.NodePilot = new NodeAutopilot();
        for (int i = 0; i < 80000 && u.NodePilot != null; i++) Steps(u, tr, 1);
        var fin = NodePlanner.CurrentOrbit(v, u.Time);
        double pe = fin.PeriapsisRadius - earth.Radius, want = o2.PeriapsisRadius - earth.Radius;
        Check("nodes: оба прожига исполнены, перигей поднят как в плане", v.Nodes.Count == 0 && Math.Abs(pe - want) < 0.05 * want,
            $"Pe {pe / 1000:F0} км (план {want / 1000:F0}), до второго прожига {(expect.PeriapsisRadius - earth.Radius) / 1000:F0} км; {OrbitText(v, u.Time)}");
    }
}
