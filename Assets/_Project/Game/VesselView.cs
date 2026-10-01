using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Вид борта (GDD §5, §9.5): процедурный меш из Design.Sections снизу вверх, начало ноды — центр масс,
    /// ориентация — Vessel.Attitude (уже в осях Unity, +Y = нос), факел под работающими двигателями.
    /// Отделённые ступени — отдельные Vessel со своим видом (их заводит GameBootstrap).
    /// </summary>
    public sealed class VesselView : MonoBehaviour
    {
        /// <summary>Дальше этого от активного борта обломки не рисуем — на таком расстоянии они меньше пикселя.</summary>
        const float DrawDistance = 50000;
        /// <summary>Длина факела в калибрах сопла у земли и рост в вакууме (§9.5: расширение струи).</summary>
        const float PlumeLengthSL = 6, PlumeVacuumGrowth = 3;
        const double SeaLevelPressure = 101325;

        public Vessel Vessel { get; private set; }

        sealed class Part
        {
            public int Index;
            public Transform Tr;
            public Transform Plume;
            public float PlumeRadius;
        }

        readonly List<Part> parts = new List<Part>();
        Material bodyMat, plumeMat;
        string builtSignature;
        double[] baseHeight;
        MaterialPropertyBlock mpb;

        static readonly Color StageColor = new Color(0.82f, 0.83f, 0.80f);
        static readonly Color PayloadColor = new Color(0.65f, 0.62f, 0.55f);
        static readonly Color CapsuleColor = new Color(0.30f, 0.28f, 0.26f);
        static readonly Color FairingColor = new Color(0.92f, 0.92f, 0.90f);
        static readonly Color NozzleColor = new Color(0.20f, 0.18f, 0.17f);

        public void Init(Vessel v, Material mat, Material plume)
        {
            Vessel = v;
            bodyMat = mat != null ? mat : new Material(Shader.Find("HDRP/Lit"));
            plumeMat = plume != null ? plume : bodyMat;
            baseHeight = new double[v.Design.Sections.Count];
            mpb = new MaterialPropertyBlock();
            Rebuild();
            LateUpdate();
        }

        string Signature()
        {
            var a = Vessel.Attached;
            var c = new char[a.Length];
            for (int i = 0; i < a.Length; i++) c[i] = a[i] ? (Vessel.IsEnclosed(i) ? 'e' : '1') : '0';
            return new string(c);
        }

        void Rebuild()
        {
            foreach (Transform ch in transform) Destroy(ch.gameObject);
            parts.Clear();
            builtSignature = Signature();
            var secs = Vessel.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Vessel.Attached[i] || Vessel.IsEnclosed(i)) continue;
                var s = secs[i];
                var go = new GameObject(s.Name);
                go.transform.SetParent(transform, false);
                float len = (float)s.Length, r = (float)s.Radius;
                Mesh mesh; Color col;
                switch (s.Kind)
                {
                    case SectionKind.Capsule: mesh = ProcMesh.Frustum(r, r * 0.35f, len, 24, true); col = CapsuleColor; break;
                    case SectionKind.Fairing: mesh = ProcMesh.Fairing(r, len, 24); col = FairingColor; break;
                    case SectionKind.Payload: mesh = ProcMesh.Frustum(r, r, len, 24, true); col = PayloadColor; break;
                    default: mesh = ProcMesh.Frustum(r, r, len, 24, true); col = StageColor; break;
                }
                AddRenderer(go, mesh, col);
                var part = new Part { Index = i, Tr = go.transform };

                if (s.HasEngine)
                {
                    // Сопло и факел — под днищем секции; калибр сопла от диаметра секции.
                    float nr = Mathf.Min(r * 0.45f, 1.2f);
                    var nozzle = new GameObject("Nozzle");
                    nozzle.transform.SetParent(go.transform, false);
                    nozzle.transform.localPosition = new Vector3(0, -nr * 1.4f, 0);
                    AddRenderer(nozzle, ProcMesh.Frustum(nr, nr * 0.45f, nr * 1.4f, 16, false), NozzleColor);

                    var plume = new GameObject("Plume");
                    plume.transform.SetParent(nozzle.transform, false);
                    var pr = plume.AddComponent<MeshRenderer>();
                    plume.AddComponent<MeshFilter>().sharedMesh = ProcMesh.Frustum(1, 0.25f, -1, 16, false);
                    pr.sharedMaterial = plumeMat;
                    pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    part.Plume = plume.transform;
                    part.PlumeRadius = nr;
                    plume.SetActive(false);
                }
                parts.Add(part);
            }
        }

        void AddRenderer(GameObject go, Mesh mesh, Color col)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = bodyMat;
            mpb.SetColor("_BaseColor", col);
            mr.SetPropertyBlock(mpb);
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (Vessel == null || u?.Active == null) return;
            bool show = Vessel.Alive && !MapView.IsOpen;
            var pos = FloatingOrigin.ToUnity(FloatingOrigin.WorldP(Vessel));
            if (Vessel != u.Active && pos.magnitude > DrawDistance) show = false;
            SetVisible(show);
            if (!show) return;

            if (Signature() != builtSignature) Rebuild();
            Vessel.MassProperties(out _, out double com, out _, out _);
            Vessel.Layout(baseHeight);
            transform.SetPositionAndRotation(pos, FloatingOrigin.ToQuaternion(Vessel.Attitude));

            float pressure = (float)(Vessel.StaticPressure / SeaLevelPressure);
            foreach (var p in parts)
            {
                p.Tr.localPosition = new Vector3(0, (float)(baseHeight[p.Index] - com), 0);
                if (p.Plume == null) continue;
                bool on = Vessel.Running[p.Index];
                float thr = on ? (float)Vessel.EffectiveThrottle(p.Index) : 0;
                p.Plume.gameObject.SetActive(thr > 0.01f);
                if (thr <= 0.01f) continue;
                // В вакууме струя раздувается и удлиняется; яркость/длина — от дросселя (§9.5).
                float spread = 1 + (1 - Mathf.Clamp01(pressure)) * PlumeVacuumGrowth;
                float len = p.PlumeRadius * PlumeLengthSL * thr * spread;
                float flicker = 1 + 0.05f * Mathf.Sin(Time.time * 53f + p.Index);
                p.Plume.localScale = new Vector3(p.PlumeRadius * spread, len * flicker, p.PlumeRadius * spread);
            }
        }

        bool visible = true;

        void SetVisible(bool on)
        {
            if (on == visible) return;
            visible = on;
            foreach (Transform ch in transform) ch.gameObject.SetActive(on);
        }
    }

    /// <summary>Процедурные тела вращения вокруг +Y для бортов.</summary>
    public static class ProcMesh
    {
        /// <summary>Усечённый конус от y=0 (радиус r0) до y=h (радиус r1); h &lt; 0 — вниз. caps — торцы.</summary>
        public static Mesh Frustum(float r0, float r1, float h, int seg, bool caps)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                verts.Add(d * r0);
                verts.Add(d * r1 + Vector3.up * h);
            }
            for (int i = 0; i < seg; i++)
            {
                int a = i * 2;
                Quad(tris, a, a + 2, a + 3, a + 1, h > 0);
            }
            if (caps)
            {
                Cap(verts, tris, r0, 0, seg, false);
                Cap(verts, tris, r1, h, seg, true);
            }
            return Finish(verts, tris, "Frustum");
        }

        /// <summary>Обтекатель: цилиндр на 55 % длины + оживальный нос.</summary>
        public static Mesh Fairing(float r, float len, int seg)
        {
            const int rings = 10;
            const float cyl = 0.55f;
            var prof = new List<Vector2> { new Vector2(r, 0), new Vector2(r, len * cyl) };
            for (int k = 1; k <= rings; k++)
            {
                float t = (float)k / rings;
                prof.Add(new Vector2(r * Mathf.Sqrt(Mathf.Max(0, 1 - t * t)), len * (cyl + (1 - cyl) * t)));
            }
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int pc = prof.Count;
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                foreach (var p in prof) verts.Add(d * p.x + Vector3.up * p.y);
            }
            for (int i = 0; i < seg; i++)
            for (int k = 0; k < pc - 1; k++)
            {
                int a = i * pc + k, b = (i + 1) * pc + k;
                Quad(tris, a, b, b + 1, a + 1, true);
            }
            Cap(verts, tris, r, 0, seg, false);
            return Finish(verts, tris, "Fairing");
        }

        static void Quad(List<int> t, int a, int b, int c, int d, bool outward)
        {
            // Порядок обхода Unity — по часовой с лицевой стороны; при h < 0 профиль идёт вниз и нормаль переворачивается.
            if (outward) { t.Add(a); t.Add(d); t.Add(c); t.Add(a); t.Add(c); t.Add(b); }
            else { t.Add(a); t.Add(c); t.Add(d); t.Add(a); t.Add(b); t.Add(c); }
        }

        static void Cap(List<Vector3> v, List<int> t, float r, float y, int seg, bool up)
        {
            if (r <= 0) return;
            int c = v.Count;
            v.Add(new Vector3(0, y, 0));
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                v.Add(new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r));
            }
            for (int i = 0; i < seg; i++)
            {
                if (up) { t.Add(c); t.Add(c + i + 2); t.Add(c + i + 1); }
                else { t.Add(c); t.Add(c + i + 1); t.Add(c + i + 2); }
            }
        }

        static Mesh Finish(List<Vector3> v, List<int> t, string name)
        {
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
