using System.Collections.Generic;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Выбор секции щелчком (§10.2, окно детали — PartInspector). Коллайдеров у вида нет и заводить их ради щелчка
    /// незачем (физика у нас своя, в ядре), поэтому луч проверяется по локальным границам рендереров детали —
    /// в осях самого рендерера, так что повёрнутые радиальные блоки и наклонённый борт ловятся плотно, без мировых AABB.
    /// </summary>
    public sealed partial class VesselView
    {
        static readonly List<Renderer> pickBuf = new List<Renderer>(32);

        /// <summary>Индекс секции под лучом (ближайшее попадание) или −1; dist — расстояние по лучу, м сцены.</summary>
        public int PickSection(Ray ray, out float dist)
        {
            dist = float.PositiveInfinity;
            int best = -1;
            if (Vessel == null) return -1;
            foreach (var p in parts)
            {
                if (p.Tr == null || !p.Tr.gameObject.activeInHierarchy) continue;
                if (p.Index < 0 || p.Index >= Vessel.Attached.Length || !Vessel.Attached[p.Index]) continue;
                pickBuf.Clear();
                p.Tr.GetComponentsInChildren(false, pickBuf);
                foreach (var r in pickBuf)
                {
                    if (!r.enabled || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                    // Факел и свечение сопла — не корпус: по ним щелчок проходит насквозь.
                    if (Under(r.transform, p.Plume) || Under(r.transform, p.Glow)) continue;
                    if (!Hit(r, ray, out float d) || d >= dist) continue;
                    dist = d;
                    best = p.Index;
                }
            }
            return best;
        }

        static bool Under(Transform t, Transform root) => root != null && (t == root || t.IsChildOf(root));

        static bool Hit(Renderer r, Ray ray, out float dist)
        {
            dist = 0;
            var t = r.transform;
            // InverseTransformVector, а не Direction: секции масштабированы неравномерно (радиус ≠ длина), и луч в
            // осях рендерера должен идти туда же, куда в мире; точку попадания потом возвращаем обратно.
            var ld0 = t.InverseTransformVector(ray.direction);
            if (ld0.sqrMagnitude < 1e-12f) return false;
            var lr = new Ray(t.InverseTransformPoint(ray.origin), ld0);
            if (!r.localBounds.IntersectRay(lr, out float ld)) return false;
            // Расстояние в осях рендерера с масштабом — возвращаем в мир, чтобы сравнивать детали между собой.
            var world = t.TransformPoint(lr.GetPoint(ld));
            dist = Vector3.Distance(ray.origin, world);
            return true;
        }
    }
}
