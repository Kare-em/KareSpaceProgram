using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Качание сопел — управление вектором тяги (§4.5, §9.5). Физика считает момент качания в Vessel.MaxTorque
    /// (EngineDef.GimbalDeg) и отдаёт фактическую долю хода Vessel.ControlDeflection; вид наклоняет сопла на шарнире у
    /// горловины на ту же долю предельного угла. Направление — из той же механики: сопло под центром масс, боковая сила
    /// тяги против отклонения струи, момент r × F; значит струя отклоняется в (−dz, 0, dx) связанных осей, а для крена
    /// у связки и боковых блоков — по касательной к кольцу. Факел качается вместе с соплом.
    /// Сопла моделей (FBX, слот цвета NozzleColor) вырезаются из меша по связным кускам (NozzleSplit): у каждого свой
    /// шарнир, корпус рисуется без них. Предел скорости привода — только визуальный, как у рулей (VesselView.Controls).
    /// </summary>
    public sealed partial class VesselView
    {
        sealed class Gimbal
        {
            public Transform T;
            public int Section;
            /// <summary>Шарнир (горловина) и покой в осях родителя T.</summary>
            public Vector3 Pivot, Rest;
            public Quaternion Base;
            /// <summary>Крен качанием: сопло связки или бокового блока. Факел связки — только тангаж и рыскание.</summary>
            public bool Roll;
            /// <summary>Текущее боковое отклонение струи в связанных осях, доля 1 = sin предельного угла.</summary>
            public Vector3 Off;
        }

        readonly List<Gimbal> gimbals = new List<Gimbal>();

        /// <summary>Скорость привода, град/с: у F-1 и РД-171 — около 5…10, у верхних ступеней быстрее.
        /// Пара: GimbalDeg каталога — полный ход проходится за полсекунды-секунду.</summary>
        const float GimbalRateDeg = 10f;
        /// <summary>Ближе к оси, м — сопло центральное, крена качанием не даёт (как в Vessel.MaxTorque).</summary>
        const float GimbalRollMinOffset = 0.05f;

        void AddGimbal(Transform t, int section, Vector3 pivot, bool roll)
        {
            var s = Vessel.Design.Sections[section];
            if (!s.HasEngine || s.Engine == null || s.Engine.GimbalDeg <= 0) return;
            gimbals.Add(new Gimbal
            {
                T = t, Section = section, Pivot = pivot, Rest = t.localPosition, Base = t.localRotation, Roll = roll,
            });
        }

        void UpdateGimbals()
        {
            if (gimbals.Count == 0) return;
            var v = Vessel;
            var d = v.ControlDeflection * MathD.Clamp(v.ControlLimit, 0, 1);
            float dx = Mathf.Clamp((float)d.x, -1, 1), dy = Mathf.Clamp((float)d.y, -1, 1), dz = Mathf.Clamp((float)d.z, -1, 1);
            float dt = Time.deltaTime;
            var down = transform.TransformDirection(Vector3.down);
            foreach (var g in gimbals)
            {
                if (g.T == null) continue;
                var s = v.Design.Sections[g.Section];
                float max = Mathf.Sin((float)s.Engine.GimbalDeg * Mathf.Deg2Rad);
                var target = Vector3.zero;
                if (v.Attached[g.Section] && v.Running[g.Section] && !v.IsLanded)
                {
                    target = new Vector3(-dz, 0, dx);
                    if (g.Roll && (s.IsRadial || s.EngineCount > 1))
                    {
                        // Касательная к кольцу: у сопла на смещении o от оси положительный крен даёт струя по (−oz, 0, ox).
                        var o = transform.InverseTransformPoint(g.T.parent.TransformPoint(g.Pivot));
                        var o2 = new Vector2(o.x, o.z);
                        if (o2.magnitude > GimbalRollMinOffset) target += dy * new Vector3(-o2.y, 0, o2.x) / o2.magnitude;
                    }
                    target = Vector3.ClampMagnitude(target, 1);
                }
                g.Off = Vector3.MoveTowards(g.Off, target, GimbalRateDeg / Mathf.Max(0.1f, (float)s.Engine.GimbalDeg) * dt);
                // Струя в связанных осях → мировые → оси родителя шарнира: так же для боковых блоков с их поворотом.
                var dirLocal = (Vector3.down + g.Off * max).normalized;
                var p = g.T.parent;
                var q = Quaternion.FromToRotation(p.InverseTransformDirection(down),
                                                  p.InverseTransformDirection(transform.TransformDirection(dirLocal)));
                g.T.localRotation = q * g.Base;
                g.T.localPosition = g.Pivot + q * (g.Rest - g.Pivot);
            }
        }

        /// <summary>
        /// Сопла модели: связные куски субмеша-сопла у низа модели, сгруппированные по оси двигателя. Результат на меш
        /// и слот кешируется — модели общие у всех бортов, а перестройка вида идёт на каждом отделении ступени.
        /// </summary>
        sealed class NozzleSplit
        {
            /// <summary>Модель без вырезанных сопел (тот же порядок слотов — палитра совпадает); null — сопел не нашли.</summary>
            public Mesh Body;
            public readonly List<(Mesh mesh, Vector3 pivot)> Nozzles = new List<(Mesh, Vector3)>();
        }

        static readonly Dictionary<(Mesh, int), NozzleSplit> nozzleSplits = new Dictionary<(Mesh, int), NozzleSplit>();

        /// <summary>Сопло — кусок, чей низ в нижней доле высоты модели; меньше доли крупнейшего — арматура, не качается.
        /// Пара: NozzleGroupShare — куски одного сопла (стенки, насадок) сливаются по близости осей.</summary>
        const float NozzleBottomShare = 0.25f, NozzleMinShare = 0.25f, NozzleGroupShare = 0.5f;
        /// <summary>Сварка вершин по положению, м: у FBX стенки сопла — раздельные вершины ради жёстких нормалей.</summary>
        const float NozzleWeld = 1e-3f;

        static NozzleSplit SplitNozzles(Mesh mesh, int slot)
        {
            if (nozzleSplits.TryGetValue((mesh, slot), out var cached)) return cached;
            var res = new NozzleSplit(); // куски: начала треугольников в tris слота
            nozzleSplits[(mesh, slot)] = res;
            if (mesh == null || slot < 0 || slot >= mesh.subMeshCount || !mesh.isReadable) return res;

            var verts = mesh.vertices;
            var normals = mesh.normals;
            var uv = mesh.uv;
            var tris = mesh.GetTriangles(slot);
            if (tris.Length == 0) return res;

            // Связность по сваренным положениям: union-find по вершинам, равные положения — один корень.
            var parent = new int[verts.Length];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
            var weld = new Dictionary<Vector3Int, int>();
            foreach (int i in tris)
            {
                var k = Vector3Int.RoundToInt(verts[i] / NozzleWeld);
                if (weld.TryGetValue(k, out int j)) Union(i, j); else weld[k] = i;
            }
            for (int t = 0; t < tris.Length; t += 3) { Union(tris[t], tris[t + 1]); Union(tris[t + 1], tris[t + 2]); }

            var islands = new Dictionary<int, (List<int> tris, Bounds b)>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                int root = Find(tris[t]);
                if (!islands.TryGetValue(root, out var isl)) isl = (new List<int>(), new Bounds(verts[tris[t]], Vector3.zero));
                isl.tris.Add(t); // начало треугольника в tris
                for (int c = 0; c < 3; c++) isl.b.Encapsulate(verts[tris[t + c]]);
                islands[root] = isl;
            }

            var mb = mesh.bounds;
            float bottom = mb.min.y + NozzleBottomShare * mb.size.y;
            float biggest = 0;
            foreach (var isl in islands.Values) biggest = Mathf.Max(biggest, Mathf.Max(isl.b.size.x, isl.b.size.z));
            // Группы по оси двигателя: центр куска в плане ближе доли его ширины — то же сопло.
            var groups = new List<(List<int> tris, Bounds b)>();
            foreach (var isl in islands.Values)
            {
                float width = Mathf.Max(isl.b.size.x, isl.b.size.z);
                if (isl.b.min.y > bottom || width < NozzleMinShare * biggest) continue;
                // Кусок шире половины модели — юбка или днище, а не сопло.
                if (width > 0.5f * Mathf.Max(mb.size.x, mb.size.z)) continue;
                var c = new Vector2(isl.b.center.x, isl.b.center.z);
                int gi = groups.FindIndex(g => (new Vector2(g.b.center.x, g.b.center.z) - c).magnitude
                                                 < NozzleGroupShare * Mathf.Max(g.b.size.x, g.b.size.z, width));
                if (gi < 0) groups.Add((new List<int>(isl.tris), isl.b));
                else { var g = groups[gi]; g.tris.AddRange(isl.tris); g.b.Encapsulate(isl.b); groups[gi] = g; }
            }
            if (groups.Count == 0) return res;

            // Корпус без сопел: слот-сопло оставляем с тем, что не вырезали (арматура, опоры).
            var cut = new HashSet<int>();
            foreach (var g in groups) cut.UnionWith(g.tris);
            var keep = new List<int>();
            for (int t = 0; t < tris.Length; t += 3)
                if (!cut.Contains(t)) { keep.Add(tris[t]); keep.Add(tris[t + 1]); keep.Add(tris[t + 2]); }
            var body = Object.Instantiate(mesh);
            body.name = mesh.name + " (без сопел)";
            body.SetTriangles(keep, slot);
            res.Body = body;

            foreach (var g in groups)
            {
                // Шарнир — верх сопла по его оси; вершины куска — в осях шарнира.
                var pivot = new Vector3(g.b.center.x, g.b.max.y, g.b.center.z);
                var map = new Dictionary<int, int>();
                var nv = new List<Vector3>();
                var nn = new List<Vector3>();
                var nu = new List<Vector2>();
                var nt = new List<int>();
                foreach (int t in g.tris)
                    for (int c = 0; c < 3; c++)
                    {
                        int i = tris[t + c];
                        if (!map.TryGetValue(i, out int j))
                        {
                            j = map[i] = nv.Count;
                            nv.Add(verts[i] - pivot);
                            if (normals.Length == verts.Length) nn.Add(normals[i]);
                            if (uv.Length == verts.Length) nu.Add(uv[i]);
                        }
                        nt.Add(j);
                    }
                var m = new Mesh { name = mesh.name + " сопло" };
                if (nv.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.SetVertices(nv);
                if (nn.Count == nv.Count) m.SetNormals(nn);
                if (nu.Count == nv.Count) m.SetUVs(0, nu);
                m.SetTriangles(nt, 0);
                if (nn.Count != nv.Count) m.RecalculateNormals();
                m.RecalculateBounds();
                res.Nozzles.Add((m, pivot));
            }
            return res;
        }

        /// <summary>
        /// Модель с двигателем: сопла слота NozzleColor — отдельными рендерерами на шарнирах. Возвращает меш корпуса
        /// (без сопел или исходный, если качать нечего).
        /// </summary>
        Mesh GimbalModelNozzles(Transform modelTr, Part part, int section, Mesh model, Color[] palette)
        {
            var s = Vessel.Design.Sections[section];
            if (!s.HasEngine || s.Engine == null || s.Engine.GimbalDeg <= 0) return model;
            int slot = System.Array.IndexOf(palette, NozzleColor);
            var split = SplitNozzles(model, slot);
            if (split.Body == null) return model;
            var tint = new[] { NozzleColor };
            int n = 0;
            foreach (var (mesh, pivot) in split.Nozzles)
            {
                var t = AddChild(modelTr.gameObject, "Gimbal Nozzle " + n++);
                t.localPosition = pivot;
                part.AddNozzle(AddRenderer(t.gameObject, mesh, tint), tint);
                AddGimbal(t, section, pivot, true);
            }
            return split.Body;
        }
    }
}
