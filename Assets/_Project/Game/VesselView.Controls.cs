using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Подвижные аэродинамические поверхности и тормозной парашют (§4.6). Геометрию и смешивание задаёт ядро
    /// (WingDef.Surfaces / Aerodynamics.DefaultSurfaces, ControlSurface.Angle), вид только отклоняет створки на шарнирах
    /// по фактической доле хода Vessel.ControlDeflection, триммеру и щитку — с пределом скорости привода ControlSurface.RateDeg.
    /// Предел скорости — только визуальный: физика прикладывает команду сразу, иначе запаздывание привода раскачало бы
    /// контуры автопилотов, настроенные без него. Механизм общий для любого WingDef (орбитеры, конструктор, Starship).
    /// </summary>
    public sealed partial class VesselView
    {
        /// <summary>Створка на шарнире: вершины меша — от HingeA, поворот вокруг оси шарнира; Sign выбран так, чтобы
        /// плюс уводил заднюю кромку к ControlSurface.Up.</summary>
        sealed class SurfaceHinge
        {
            public Transform T;
            public ControlSurface S;
            public Vector3 Axis;
            public float Sign, Angle;
            public int Section;
        }

        /// <summary>Тормозной парашют секции: корень в точке выхода строп (SectionDef.DragChuteMount).</summary>
        sealed class DragChuteView
        {
            public Transform Root;
            public Transform[] Canopy;
            public LineRenderer[] Lines;
            public int Section;
        }

        readonly List<SurfaceHinge> surfaces = new List<SurfaceHinge>();
        readonly List<DragChuteView> dragChutes = new List<DragChuteView>();

        /// <summary>Длина строп тормозного парашюта в диаметрах купола: у «Шаттла» Ø12,2 м на ≈27 м фала (≈2,2 D) —
        /// купол за следом от крыла и киля. Пара с DragChuteSpread: у связки куполов стропы длиннее разводки.</summary>
        const float DragChuteRiserPerD = 2.2f;
        /// <summary>Разводка куполов связки от оси, град («Буран» — 3 купола).</summary>
        const float DragChuteSpread = 14;
        /// <summary>Сколько секунд купол вытягивается из контейнера до полной длины строп (наполнение — FlightPhysics.DragChuteFillTime).</summary>
        const float DragChuteExtract = 0.8f;
        /// <summary>Толщина задней кромки створки в долях толщины у шарнира (клин, как у настоящих рулей).</summary>
        const float SurfaceEdgeShare = 0.15f;

        /// <summary>Остекление кабины орбитеров (слот Glass в winged_parts.py): почти чёрное, гладкое — небо и Солнце
        /// видны бликом. Цвет — ключ слота в палитре (как отделки FinishOf), материал — GlassMaterial.</summary>
        static readonly Color GlassColor = new Color(0.02f, 0.025f, 0.03f);
        /// <summary>Гладкость стекла HDRP/Lit: 0,96 — зеркальный блик без «пластика» (у плиток ТЗП ≈ 0,3).</summary>
        const float GlassSmoothness = 0.96f;
        static Material glassMat;

        /// <summary>Материал стекла — копия bodyMat без карт (иначе текстура стрингеров и маска гасят гладкость);
        /// ключи карт снимаются руками: рантайм-материалы HDRP не валидируются (см. pitfalls-render).</summary>
        Material GlassMaterial()
        {
            if (glassMat != null || bodyMat == null) return glassMat != null ? glassMat : bodyMat;
            glassMat = new Material(bodyMat) { name = "Orbiter Glass" };
            foreach (var map in new[] { "_BaseColorMap", "_NormalMap", "_MaskMap", "_DetailMap" })
                if (glassMat.HasProperty(map)) glassMat.SetTexture(map, null);
            glassMat.DisableKeyword("_NORMALMAP");
            glassMat.DisableKeyword("_MASKMAP");
            glassMat.DisableKeyword("_DETAIL_MAP");
            if (glassMat.HasProperty("_Smoothness")) glassMat.SetFloat("_Smoothness", GlassSmoothness);
            if (glassMat.HasProperty("_Metallic")) glassMat.SetFloat("_Metallic", 0);
            return glassMat;
        }

        void ResetControls()
        {
            surfaces.Clear();
            dragChutes.Clear();
        }

        /// <summary>Створки всех плоскостей секции и её тормозной парашют. palette[0] — верх, palette[1] (если есть) —
        /// низ створок с DarkBelly (у орбитеров чёрная плитка снизу).</summary>
        void AddControls(GameObject go, Part part, int index, SectionDef s, Color[] palette)
        {
            if (s.Wings != null)
                foreach (var w in s.Wings)
                {
                    var list = w.Surfaces ?? Aerodynamics.DefaultSurfaces(w);
                    foreach (var cs in list) AddSurface(go, part, index, cs, palette);
                }
            if (s.DragChuteArea > 0) AddDragChute(go, index, s);
        }

        static Vector3 V(Vector3d v) => new Vector3((float)v.x, (float)v.y, (float)v.z);

        void AddSurface(GameObject go, Part part, int index, ControlSurface cs, Color[] palette)
        {
            Vector3 a = V(cs.HingeA), b = V(cs.HingeB);
            var axis = (b - a).normalized;
            if (axis == Vector3.zero) return;
            // Направление к задней кромке — Aft без составляющей вдоль шарнира; Up — перпендикуляр к створке.
            var aft = Vector3.ProjectOnPlane(V(cs.Aft), axis).normalized;
            var up = Vector3.ProjectOnPlane(V(cs.Up), axis);
            up = (up - Vector3.Dot(up, aft) * aft).normalized;
            var t = AddChild(go, cs.Name);
            t.localPosition = a;
            var mesh = SurfaceMesh(b - a, aft, up, (float)cs.ChordA, (float)cs.ChordB, (float)cs.Thickness);
            Color top = palette.Length > 0 ? palette[0] : WhiteColor;
            Color belly = cs.DarkBelly && palette.Length > 1 ? palette[1] : top;
            var pal = new[] { top, belly };
            part.AddBody(AddRenderer(t.gameObject, mesh, pal), pal);
            surfaces.Add(new SurfaceHinge
            {
                T = t, S = cs, Axis = axis, Section = index,
                // Поворот на +угол вокруг оси уводит aft к Cross(axis, aft): нужен знак, при котором это к Up.
                Sign = Vector3.Dot(Vector3.Cross(axis, aft), up) >= 0 ? 1 : -1,
            });
        }

        /// <summary>
        /// Клин створки: шарнир (от 0 до span) толщиной th, задняя кромка — на chord по aft, толщиной th·SurfaceEdgeShare.
        /// Субмеш 0 — верх (сторона Up), носок, торцы и кромка; 1 — низ. Вершины у каждой грани свои — жёсткие рёбра.
        /// </summary>
        static Mesh SurfaceMesh(Vector3 span, Vector3 aft, Vector3 up, float chordA, float chordB, float th)
        {
            float h = th * 0.5f, e = h * SurfaceEdgeShare;
            // Углы: 0/1 — шарнир у A верх/низ, 2/3 — у B, 4/5 — кромка у A, 6/7 — у B.
            var p = new[]
            {
                up * h, -up * h, span + up * h, span - up * h,
                aft * chordA + up * e, aft * chordA - up * e, span + aft * chordB + up * e, span + aft * chordB - up * e,
            };
            var verts = new List<Vector3>();
            var topTris = new List<int>();
            var botTris = new List<int>();
            var centre = (span + aft * (chordA + chordB) * 0.5f) * 0.5f;
            void Quad(List<int> tris, int i0, int i1, int i2, int i3)
            {
                int o = verts.Count;
                verts.Add(p[i0]); verts.Add(p[i1]); verts.Add(p[i2]); verts.Add(p[i3]);
                // Обход — так, чтобы нормаль Unity (по часовой при взгляде снаружи) смотрела от середины створки.
                var n = Vector3.Cross(p[i1] - p[i0], p[i2] - p[i0]);
                bool flip = Vector3.Dot(n, (p[i0] + p[i2]) * 0.5f - centre) < 0;
                if (flip) { tris.Add(o); tris.Add(o + 2); tris.Add(o + 1); tris.Add(o); tris.Add(o + 3); tris.Add(o + 2); }
                else { tris.Add(o); tris.Add(o + 1); tris.Add(o + 2); tris.Add(o); tris.Add(o + 2); tris.Add(o + 3); }
            }
            Quad(topTris, 0, 2, 6, 4);     // верх
            Quad(botTris, 1, 5, 7, 3);     // низ
            Quad(topTris, 0, 1, 3, 2);     // носок у шарнира
            Quad(topTris, 4, 6, 7, 5);     // задняя кромка
            Quad(topTris, 0, 4, 5, 1);     // торец A
            Quad(topTris, 2, 3, 7, 6);     // торец B
            var mesh = new Mesh { name = "Control Surface" };
            mesh.SetVertices(verts);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(topTris, 0);
            mesh.SetTriangles(botTris, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        void AddDragChute(GameObject go, int index, SectionDef s)
        {
            int n = Mathf.Max(1, s.DragChuteCount);
            var root = AddChild(go, "Drag Chute");
            root.localPosition = V(s.DragChuteMount);
            var view = new DragChuteView { Root = root, Section = index, Canopy = new Transform[n], Lines = new LineRenderer[n] };
            for (int k = 0; k < n; k++)
            {
                var c = new GameObject("Canopy " + k);
                c.transform.SetParent(root, false);
                c.AddComponent<MeshFilter>().sharedMesh = ProcMesh.Dome(ChuteDomeAngle, ChuteGores * 2, 8);
                var mr = c.AddComponent<MeshRenderer>();
                mr.sharedMaterial = bodyMat;
                mpb.Clear();
                mpb.SetColor("_BaseColor", Color.white);
                mpb.SetTexture("_BaseColorMap", GoreStripes());
                mr.SetPropertyBlock(mpb);
                // Стропы — отдельным узлом: у купола масштаб, а ломаная строп задаётся в метрах.
                var lgo = new GameObject("Lines " + k);
                lgo.transform.SetParent(root, false);
                var lines = lgo.AddComponent<LineRenderer>();
                lines.sharedMaterial = bodyMat;
                lines.useWorldSpace = false;
                lines.widthMultiplier = ChuteLineWidth;
                lines.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mpb.Clear();
                mpb.SetColor("_BaseColor", new Color(0.85f, 0.83f, 0.78f));
                lines.SetPropertyBlock(mpb);
                lines.positionCount = ChuteGores * 2;
                view.Canopy[k] = c.transform;
                view.Lines[k] = lines;
            }
            root.gameObject.SetActive(false);
            dragChutes.Add(view);
        }

        /// <summary>Каждый кадр: створки к целевому углу с пределом скорости привода, тормозной парашют по Vessel.DragChute.</summary>
        void UpdateControls(Vector3 airflow)
        {
            var v = Vessel;
            float dt = Time.deltaTime;
            // Ход рулей ограничен настройкой ControlLimit (PartInspector) — визуально так же, как бюджет момента в физике.
            var cmd = v.ControlDeflection * MathD.Clamp(v.ControlLimit, 0, 1);
            foreach (var h in surfaces)
            {
                if (h.T == null) continue;
                float target = v.Attached[h.Section] ? (float)h.S.Angle(cmd, v.PitchTrim, v.AirBrake) : 0;
                float step = (float)h.S.RateDeg * dt;
                h.Angle += Mathf.Clamp(target - h.Angle, -step, step);
                h.T.localRotation = Quaternion.AngleAxis(h.Sign * h.Angle, h.Axis);
            }

            foreach (var d in dragChutes)
            {
                if (d.Root == null) continue;
                var s = v.Design.Sections[d.Section];
                bool open = v.DragChute == DragChuteState.Open && v.Attached[d.Section];
                d.Root.gameObject.SetActive(open);
                if (!open) continue;
                int n = d.Canopy.Length;
                float fill = Mathf.Clamp01((float)(v.DragChuteTime / FlightPhysics.DragChuteFillTime));
                float full = Mathf.Sqrt((float)(s.DragChuteArea / n) / Mathf.PI);
                float r = Mathf.Max(0.3f, full * fill);
                float riser = 2 * full * DragChuteRiserPerD * Mathf.Clamp01((float)v.DragChuteTime / DragChuteExtract);
                // Купола за кормой по потоку: +Y корня — против набегающего потока, лёгкое рысканье.
                float t = Time.time;
                var sway = Quaternion.Euler(2 * Mathf.Sin(t * 1.3f), 0, 2 * Mathf.Sin(t * 1.1f));
                // Без потока (стоянка, читы) — просто за корму, иначе FromToRotation(0) оставит купола у носа.
                var downwind = airflow.sqrMagnitude > 1e-4f ? -airflow : -d.Root.parent.up;
                d.Root.rotation = Quaternion.FromToRotation(Vector3.up, downwind) * sway;
                for (int k = 0; k < n; k++)
                {
                    // Связка: купола веером вокруг оси потока (один — по оси).
                    var dir = n == 1 ? Vector3.up
                        : Quaternion.AngleAxis(360f * k / n, Vector3.up) * (Quaternion.AngleAxis(DragChuteSpread, Vector3.right) * Vector3.up);
                    var rot = Quaternion.FromToRotation(Vector3.up, dir);
                    var centre = dir * riser;
                    d.Canopy[k].localPosition = centre;
                    d.Canopy[k].localRotation = rot;
                    d.Canopy[k].localScale = new Vector3(r, r, r);
                    for (int g = 0; g < ChuteGores; g++)
                    {
                        float a = 2 * Mathf.PI * g / ChuteGores;
                        d.Lines[k].SetPosition(2 * g, Vector3.zero);
                        d.Lines[k].SetPosition(2 * g + 1, centre + rot * new Vector3(r * Mathf.Cos(a), 0, r * Mathf.Sin(a)));
                    }
                }
            }
        }
    }
}
