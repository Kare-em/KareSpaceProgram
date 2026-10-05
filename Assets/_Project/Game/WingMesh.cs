using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Процедурные плоскости крыльев (GDD §4.6, §5.4): пластина постоянной хорды со стреловидностью, поперечным V и углом
    /// установки — по тем же WingDef, что считает Aerodynamics.CollectPanels, чтобы картинка не врала физике.
    /// Оси секции в Unity: +Y — нос, +X — брюхо (верх — −X, туда же киль), размах крыла — по Z; начало — низ секции.
    /// Общая для ангара (HangarController) и полёта (VesselView), пока у детали нет своей FBX-модели.
    /// </summary>
    public static class WingMesh
    {
        /// <summary>Толщина пластины — доля хорды (профиль 8 %), но не тоньше ThinMin м, иначе издали мерцает.</summary>
        const float Thickness = 0.08f, ThinMin = 0.06f;

        /// <summary>Все плоскости секции одним мешем (двусторонним: тонкая пластина видна с обеих сторон).</summary>
        public static Mesh Build(IList<WingDef> wings)
        {
            if (wings == null || wings.Count == 0) return null;
            var v = new List<Vector3>();
            var t = new List<int>();
            foreach (var w in wings) Add(w, v, t);
            if (v.Count == 0) return null;
            var m = new Mesh { name = "Wings" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        public static Mesh Build(WingDef w) => Build(new[] { w });

        static void Add(WingDef w, List<Vector3> v, List<int> t)
        {
            float c = (float)w.MeanChord, b = (float)w.Span;
            if (c <= 0 || b <= 0) return;
            float sweep = Mathf.Tan((float)w.Sweep * Mathf.Deg2Rad);
            float th = Mathf.Max(ThinMin, Thickness * c);
            // Корень по линии 1/4 хорд — на Height над низом секции, над осью на Offset (к −X).
            var rootQ = new Vector3(-(float)w.Offset, (float)w.Height, 0);
            if (w.Vertical)
            {
                // Киль: от корня вверх (−X) на высоту Span, концевая хорда сдвинута назад стреловидностью.
                var chord = Vector3.up * c;
                var tipQ = rootQ + new Vector3(-b, -b * sweep, 0);
                Plate(rootQ, tipQ, chord, Vector3.forward, th, v, t);
                return;
            }
            // Хорда от задней кромки к передней; плюс установки — передняя кромка к −X (как в WingDef.Incidence).
            float inc = (float)w.Incidence * Mathf.Deg2Rad, dih = (float)w.Dihedral * Mathf.Deg2Rad;
            var ch = new Vector3(-Mathf.Sin(inc), Mathf.Cos(inc), 0) * c;
            float half = b * 0.5f;
            for (int side = -1; side <= 1; side += 2)
            {
                // Поперечное V: плюс — концы вверх (к −X).
                var span = new Vector3(-Mathf.Sin(dih), 0, side * Mathf.Cos(dih));
                var tipQ = rootQ + span * half + Vector3.down * (half * sweep);
                var n = Vector3.Cross(span, ch).normalized;
                Plate(rootQ, tipQ, ch, n, th, v, t);
            }
        }

        /// <summary>Пластина по линии 1/4 хорд от корня до конца: передняя кромка на 1/4 хорды впереди, задняя — на 3/4 позади.</summary>
        static void Plate(Vector3 rootQ, Vector3 tipQ, Vector3 chord, Vector3 n, float th, List<Vector3> v, List<int> t)
        {
            Vector3 rLE = rootQ + chord * 0.25f, rTE = rootQ - chord * 0.75f;
            Vector3 tLE = tipQ + chord * 0.25f, tTE = tipQ - chord * 0.75f;
            var h = n * (th * 0.5f);
            Quad(rTE + h, rLE + h, tLE + h, tTE + h, v, t);       // верх
            Quad(rTE - h, rLE - h, tLE - h, tTE - h, v, t);       // низ
            Quad(rLE + h, rLE - h, tLE - h, tLE + h, v, t);       // передняя кромка
            Quad(rTE + h, rTE - h, tTE - h, tTE + h, v, t);       // задняя кромка
            Quad(tTE + h, tLE + h, tLE - h, tTE - h, v, t);       // торец
        }

        /// <summary>Четырёхугольник в обе стороны: порядок обхода граней не важен, нормали у каждой стороны свои.</summary>
        static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, List<Vector3> v, List<int> t)
        {
            for (int s = 0; s < 2; s++)
            {
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(d);
                if (s == 0) t.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
                else t.AddRange(new[] { i, i + 2, i + 1, i, i + 3, i + 2 });
            }
        }
    }
}
