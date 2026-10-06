using System.Collections.Generic;
using UnityEngine;
using Kare.Space.Core;

namespace Kare.Space.Game
{
    /// <summary>
    /// Посадочная полоса крылатого борта (§6.4): бетонная лента с разметкой на ровной площадке рельефа. Только вид — ядро
    /// знает полосу как ось + длину + ширину (Runway, Runways.At). Одна лента на полосу на всю сцену.
    /// Локальный базис: X — вправо от оси посадки, Y — зенит, Z — вдоль курса посадки (как Runway.Locate: right = axis × up);
    /// начало — центр полосы на поверхности. Вся геометрия в одном меше на материал, а не коробками-детьми.
    /// </summary>
    public sealed class RunwayView : MonoBehaviour
    {
        /// <summary>
        /// Верх плиты над рельефом, м. Пара: нижняя грань плиты на SlabDepth ниже нуля, чтобы лента не «висела» над
        /// неровностью площадки; разметка выше плиты на MarkLift — иначе z-fighting с бетоном.
        /// </summary>
        const float SlabTop = 0.06f, SlabDepth = -1f, MarkLift = 0.02f;
        /// <summary>Концевая полоса безопасности за порогом (темнее бетона), м, и её верх над рельефом. Пара: FlatRadius у
        /// Runways.RunwayFlat (3500) ≥ Length/2 + OverrunLength — иначе она уходит в зону сглаживания рельефа.</summary>
        const float OverrunLength = (float)Runways.Overrun, OverrunTop = 0.04f;
        /// <summary>Осевая линия: штрих и просвет, м; ширина всех белых линий, м.</summary>
        const float DashLength = 36, DashGap = 24, LineWidth = 0.9f;
        /// <summary>Боковые линии отступают от кромки, м.</summary>
        const float EdgeInset = 1.5f;
        /// <summary>
        /// «Пианино» порога: длина полос вдоль оси, ширина, просвет, м; отступ от торца, м; свободный коридор по оси
        /// (до первой полосы от центра), м.
        /// </summary>
        const float KeyLength = 30, KeyWidth = 1.8f, KeyGap = 1.8f, KeyFromEnd = 6, KeyCenterClear = 3;
        /// <summary>Зона приземления: расстояния от порога до пар отметок, м; длина, ширина отметки и вынос от оси, м.</summary>
        static readonly float[] TouchdownAt = { 150, 300, 500, 650, 800 };
        const float TouchdownLength = 22, TouchdownWidth = 3, TouchdownOffset = 9;
        /// <summary>Точка прицеливания (толстые полосы) — расстояние от порога, длина, ширина, вынос от оси, м. Пара: ≈ точка
        /// касания шаттла в автопилоте посадки (§6.4), ~400 м от порога.</summary>
        const float AimAt = 400, AimLength = 45, AimWidth = 9, AimOffset = 12;
        /// <summary>Шаг, на который режем длинные коробки по Z, м: лента на сфере изгибается (стрела прогиба на 2,3 км ≈ 0,4 м
        /// у радиуса Земли), а коробка прямая — без нарезки концы полосы висели бы над рельефом.</summary>
        const float CurveStep = 50;
        /// <summary>Плотность UV бетона: метров на тайл (текстуры нет, но тангенты считаются по UV).</summary>
        const float UvTile = 10;
        /// <summary>Дальше этого полосу не рисуем — меньше пикселя (длина 4,6 км при 80 км — доли градуса).</summary>
        const float DrawDistance = 80000;

        static readonly Dictionary<string, RunwayView> Views = new Dictionary<string, RunwayView>();

        CelestialBody body;
        Vector3d anchorBf;
        QuaternionD frameBf;
        Renderer[] renderers;

        /// <summary>Видимые полосы всех площадок тела body (по Runways.All); существующие не пересоздаются.</summary>
        public static void EnsureAll(CelestialBody body, Material baseMat)
        {
            if (body == null) return;
            foreach (var r in Runways.All)
            {
                var s = r.Site;
                if (s == null || s.BodyId != body.Id) continue;
                Ensure(body, r, baseMat);
            }
        }

        /// <summary>Вид полосы r; уже есть (и жив — сцену могли перезагрузить) — ничего не делает.</summary>
        public static void Ensure(CelestialBody body, Runway r, Material baseMat)
        {
            if (body == null || r == null) return;
            if (Views.TryGetValue(r.Id, out var v) && v != null) return;
            var go = new GameObject("Runway " + r.Id);
            v = go.AddComponent<RunwayView>();
            v.Init(body, r, baseMat);
            Views[r.Id] = v;
        }

        void Init(CelestialBody b, Runway r, Material baseMat)
        {
            body = b;
            r.Frame(out var up, out var axis);
            // Тот же right, что в Runway.Locate: cross > 0 — справа от оси; right × axis = up, базис правый.
            var right = Vector3d.Cross(axis, up);
            // Площадка плоская (LaunchSite.FlatRadius), поэтому высота в центре — высота всей ленты.
            anchorBf = up * (b.Radius + b.SurfaceHeight(up));
            frameBf = QuaternionD.FromBasis(right.SwapYZ, up.SwapYZ, axis.SwapYZ);

            var shader = baseMat != null ? baseMat.shader : Shader.Find("HDRP/Lit");
            // Размеры — из ядра: те же Length/Width, по которым Runways.At решает «борт на полосе».
            float hl = (float)r.HalfLength, hw = (float)(r.Width * 0.5);
            float curve = 1f / (2f * (float)b.Radius);

            var slab = new Strip(curve);
            slab.Box(new Vector3(-hw, SlabDepth, -hl), new Vector3(hw, SlabTop, hl));
            var over = new Strip(curve);
            over.Box(new Vector3(-hw, SlabDepth, hl), new Vector3(hw, OverrunTop, hl + OverrunLength));
            over.Box(new Vector3(-hw, SlabDepth, -hl - OverrunLength), new Vector3(hw, OverrunTop, -hl));

            var marks = new Strip(curve);
            float y0 = SlabTop, y1 = SlabTop + MarkLift;
            // Боковые линии на всю длину.
            float ex = hw - EdgeInset;
            marks.Box(new Vector3(-ex - LineWidth, y0, -hl), new Vector3(-ex, y1, hl));
            marks.Box(new Vector3(ex, y0, -hl), new Vector3(ex + LineWidth, y1, hl));
            // Осевая линия: штрихи симметрично между порогами, концы отступают на длину «пианино».
            float usable = 2 * (hl - KeyFromEnd - KeyLength - DashGap);
            int dashes = Mathf.Max(1, Mathf.FloorToInt((usable + DashGap) / (DashLength + DashGap)));
            float zs = -(dashes * (DashLength + DashGap) - DashGap) * 0.5f;
            for (int i = 0; i < dashes; i++)
            {
                float z = zs + i * (DashLength + DashGap);
                marks.Box(new Vector3(-LineWidth * 0.5f, y0, z), new Vector3(LineWidth * 0.5f, y1, z + DashLength));
            }
            // Оба торца полосы: «пианино» порога, зона приземления, точка прицеливания (разметка симметрична —
            // борт может сесть с любого конца, курс Runway.Heading лишь выбирает основной).
            foreach (float sg in new[] { 1f, -1f })
            {
                float zEnd = sg * (hl - KeyFromEnd);
                for (float x = KeyCenterClear; x + KeyWidth <= ex - 1.5f; x += KeyWidth + KeyGap)
                {
                    KeyBar(marks, sg, zEnd, x, y0, y1);
                    KeyBar(marks, sg, zEnd, -x - KeyWidth, y0, y1);
                }
                foreach (float d in TouchdownAt) Pair(marks, sg * (hl - d), TouchdownOffset, TouchdownWidth, TouchdownLength, y0, y1);
                Pair(marks, sg * (hl - AimAt), AimOffset, AimWidth, AimLength, y0, y1);
            }

            Part("Slab", slab, new Color(0.30f, 0.30f, 0.31f), shader, 0.2f);
            Part("Overrun", over, new Color(0.19f, 0.19f, 0.19f), shader, 0.15f);
            Part("Marks", marks, new Color(0.86f, 0.86f, 0.84f), shader, 0.3f);
            renderers = GetComponentsInChildren<Renderer>();
            LateUpdate();
        }

        /// <summary>Полоса «пианино»: от торца в глубь ленты на KeyLength (sg — знак конца), x — левая кромка полосы.</summary>
        static void KeyBar(Strip m, float sg, float zEnd, float x, float y0, float y1)
        {
            float za = zEnd, zb = zEnd - sg * KeyLength;
            m.Box(new Vector3(x, y0, Mathf.Min(za, zb)), new Vector3(x + KeyWidth, y1, Mathf.Max(za, zb)));
        }

        /// <summary>Пара отметок с центром на zc, симметричная относительно оси: ближняя кромка — на offset от оси.</summary>
        static void Pair(Strip m, float zc, float offset, float width, float length, float y0, float y1)
        {
            float h = length * 0.5f;
            m.Box(new Vector3(offset, y0, zc - h), new Vector3(offset + width, y1, zc + h));
            m.Box(new Vector3(-offset - width, y0, zc - h), new Vector3(-offset, y1, zc + h));
        }

        void Part(string name, Strip strip, Color color, Shader shader, float smooth)
        {
            var mat = new Material(shader) { name = "Runway " + name };
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smooth);
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = strip.Build(name);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            // Плоская лента у земли: тень от неё не нужна, а на ровной поверхности даёт акне.
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void LateUpdate()
        {
            if (body == null) return;
            var o = body.Orientation;
            var pos = FloatingOrigin.ToUnity(body.Position + o * anchorBf);
            bool show = !MapView.IsOpen && FlightView.Near(pos, DrawDistance);
            foreach (var r in renderers) r.enabled = show;
            if (show) transform.SetPositionAndRotation(pos, FloatingOrigin.ToQuaternion(o.SwapYZ * frameBf));
        }

        /// <summary>
        /// Коробки в один меш. Каждая режется по Z шагом CurveStep, вершины опускаются на z²/(2R) — лента повторяет
        /// кривизну тела и не отрывается от рельефа на концах. Нижняя грань не строится (не видна).
        /// </summary>
        sealed class Strip
        {
            readonly float curve;
            readonly List<Vector3> v = new List<Vector3>();
            readonly List<Vector3> n = new List<Vector3>();
            readonly List<Vector2> uv = new List<Vector2>();
            readonly List<int> t = new List<int>();

            public Strip(float curve) { this.curve = curve; }

            public void Box(Vector3 min, Vector3 max)
            {
                int pieces = Mathf.Max(1, Mathf.CeilToInt((max.z - min.z) / CurveStep));
                float dz = (max.z - min.z) / pieces;
                for (int i = 0; i < pieces; i++)
                {
                    float za = min.z + i * dz, zb = za + dz;
                    // Верх; обход (x0,z0),(x0,z1),(x1,z1),(x1,z0) — лицом вверх (Cross(p1 − p0, p2 − p0) = +Y).
                    Quad(Vector3.up, P(min.x, max.y, za), P(min.x, max.y, zb), P(max.x, max.y, zb), P(max.x, max.y, za));
                    Quad(Vector3.right, P(max.x, min.y, za), P(max.x, max.y, za), P(max.x, max.y, zb), P(max.x, min.y, zb));
                    Quad(Vector3.left, P(min.x, min.y, za), P(min.x, min.y, zb), P(min.x, max.y, zb), P(min.x, max.y, za));
                    if (i == pieces - 1)
                        Quad(Vector3.forward, P(min.x, min.y, zb), P(max.x, min.y, zb), P(max.x, max.y, zb), P(min.x, max.y, zb));
                    if (i == 0)
                        Quad(Vector3.back, P(min.x, min.y, za), P(min.x, max.y, za), P(max.x, max.y, za), P(max.x, min.y, za));
                }
            }

            Vector3 P(float x, float y, float z) => new Vector3(x, y - z * z * curve, z);

            void Quad(Vector3 nrm, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int i0 = v.Count;
                foreach (var p in new[] { a, b, c, d })
                {
                    v.Add(p);
                    n.Add(nrm);
                    uv.Add(new Vector2(nrm.x != 0 ? p.z : p.x, nrm.y != 0 ? p.z : p.y) / UvTile);
                }
                t.Add(i0); t.Add(i0 + 1); t.Add(i0 + 2);
                t.Add(i0); t.Add(i0 + 2); t.Add(i0 + 3);
            }

            public Mesh Build(string name)
            {
                // Вершин у 4,6 км ленты с разметкой больше 65 тыс. не бывает, но формат 32 бита безопаснее на запас.
                var m = new Mesh { name = "Runway " + name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                m.SetVertices(v);
                m.SetNormals(n);
                m.SetUVs(0, uv);
                m.SetTriangles(t, 0);
                m.RecalculateTangents();
                m.RecalculateBounds();
                return m;
            }
        }
    }
}
