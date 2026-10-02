using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Стартовый стол (GDD §7): бетонный пьедестал с проёмом газоотвода и четыре опорные фермы,
    /// которые держат ракету за корпус и отводятся «тюльпаном» при отрыве. Привязан к телу в точке
    /// космодрома, физики не несёт: высоту стола ядро задаёт само (LaunchSite.PadHeight).
    /// </summary>
    public sealed class LaunchPadView : MonoBehaviour
    {
        /// <summary>Полуширина бетона и проёма над газоотводом, м. Пара: связка сопел (≈ радиус днища)
        /// должна пролезать в проём — HoleHalf больше радиуса первой ступени.</summary>
        const float SlabHalf = 22, HoleHalf = 5.5f;
        /// <summary>Повтор текстуры бетона, м (на тайле 2×2 плиты — плита 4 м).</summary>
        const float ConcreteTile = 8;
        /// <summary>Где ферма касается корпуса над столом, сечение фермы, м.</summary>
        const float ArmReach = 9, ArmWidth = 0.9f;
        /// <summary>Длина фермы в FBX, м (Tools/blender). Пара: ArmWidth — сечение модели 0,9 м, тянем только по длине.</summary>
        const float TrussModelLength = 8.2f;
        /// <summary>Отвод ферм: на сколько градусов наружу и за сколько секунд.</summary>
        const float ReleaseAngle = 70, ReleaseTime = 1.6f;
        /// <summary>Дальше этого стол не рисуем — меньше пикселя.</summary>
        const float DrawDistance = 60000;

        static readonly Color ArmColor = new Color(0.42f, 0.40f, 0.36f);
        static readonly Color PitColor = new Color(0.05f, 0.05f, 0.05f);

        Vessel vessel;
        CelestialBody body;
        Vector3d anchorBf;
        QuaternionD frameBf;
        readonly List<Transform> arms = new List<Transform>();
        float armTilt, release;
        Renderer[] renderers;

        public void Init(Vessel v, Texture2D concrete, Material baseMat, Mesh truss = null)
        {
            vessel = v;
            body = v.Body;
            var site = v.Site;
            // Базис стола: X — восток, Y — зенит, Z — север (борт на нём повёрнут креном, см. PlaceOnSurface; стол симметричен).
            var up = CelestialBody.LatLonToBodyFixed(site.Latitude, site.Longitude);
            anchorBf = up * (body.Radius + body.SurfaceHeight(up));
            var east = Vector3d.Cross(Vector3d.forward, up).normalized;
            var north = Vector3d.Cross(up, east);
            frameBf = QuaternionD.FromBasis(east.SwapYZ, up.SwapYZ, north.SwapYZ);

            var shader = baseMat != null ? baseMat.shader : Shader.Find("HDRP/Lit");
            var concreteMat = new Material(shader) { name = "Pad Concrete" };
            concreteMat.SetTexture("_BaseColorMap", concrete != null ? concrete : Texture2D.grayTexture);
            concreteMat.SetFloat("_Smoothness", 0.2f);
            var steelMat = new Material(shader) { name = "Pad Steel" };
            steelMat.SetColor("_BaseColor", ArmColor);
            steelMat.SetFloat("_Metallic", 0.5f);
            steelMat.SetFloat("_Smoothness", 0.35f);
            var pitMat = new Material(shader) { name = "Pad Pit" };
            pitMat.SetColor("_BaseColor", PitColor);

            float top = (float)site.PadHeight;
            // Пьедестал: кольцо из четырёх плит вокруг проёма; снизу на метр в грунт — под рельеф.
            var slab = new MeshBuilder(ConcreteTile);
            slab.Box(new Vector3(-SlabHalf, -1, HoleHalf), new Vector3(SlabHalf, top, SlabHalf));
            slab.Box(new Vector3(-SlabHalf, -1, -SlabHalf), new Vector3(SlabHalf, top, -HoleHalf));
            slab.Box(new Vector3(HoleHalf, -1, -HoleHalf), new Vector3(SlabHalf, top, HoleHalf));
            slab.Box(new Vector3(-SlabHalf, -1, -HoleHalf), new Vector3(-HoleHalf, top, HoleHalf));
            AddPart("Pad", slab.Build(), concreteMat, transform);
            // Дно газоотвода — тёмное, чтобы проём читался ямой, а не травой под ракетой.
            var pit = new MeshBuilder(ConcreteTile);
            pit.Box(new Vector3(-HoleHalf, -1, -HoleHalf), new Vector3(HoleHalf, 0.05f, HoleHalf));
            AddPart("Flame Pit", pit.Build(), pitMat, transform);

            // Фермы: шарнир на кромке проёма, верх упирается в корпус на ArmReach над столом.
            float hull = (float)v.Design.Sections[0].Radius;
            armTilt = Mathf.Atan2(HoleHalf - hull, ArmReach) * Mathf.Rad2Deg;
            float len = Mathf.Sqrt(ArmReach * ArmReach + (HoleHalf - hull) * (HoleHalf - hull));
            var arm = new MeshBuilder(ConcreteTile);
            // Решётчатая ферма из Blender (Models/Pad_Truss_Arm, сечение = ArmWidth, длина TrussModelLength) или брус.
            if (truss == null)
                arm.Box(new Vector3(-ArmWidth / 2, 0, -ArmWidth / 2), new Vector3(ArmWidth / 2, len, ArmWidth / 2));
            // Противовес под шарниром — им ферма и откидывается, когда ракета уходит.
            arm.Box(new Vector3(-ArmWidth, -2.5f, -ArmWidth), new Vector3(ArmWidth, 0, ArmWidth));
            var armMesh = arm.Build();
            for (int k = 0; k < 4; k++)
            {
                var pivot = new GameObject($"Arm {k}").transform;
                pivot.SetParent(transform, false);
                // Ферма смотрит на ось ракеты: локальный −Z шарнира — к центру.
                float yaw = 90 * k;
                pivot.localPosition = Quaternion.Euler(0, yaw, 0) * new Vector3(0, top, HoleHalf);
                pivot.localRotation = Quaternion.Euler(0, yaw, 0);
                AddPart(truss == null ? "Truss" : "Counterweight", armMesh, steelMat, pivot);
                if (truss != null)
                    AddPart("Truss", truss, steelMat, pivot).transform.localScale = new Vector3(1, len / TrussModelLength, 1);
                arms.Add(pivot);
            }
            renderers = GetComponentsInChildren<Renderer>();
            Pose();
        }

        static GameObject AddPart(string name, Mesh mesh, Material mat, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            // У FBX слоты материалов — отдельные субмеши: с одним материалом рисуется только первый.
            var mats = new Material[Mathf.Max(1, mesh.subMeshCount)];
            for (int i = 0; i < mats.Length; i++) mats[i] = mat;
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            return go;
        }

        void LateUpdate()
        {
            if (vessel == null) return;
            // Фермы отходят по факту отрыва — ядро переводит борт из Landed в Flying (StepLanded).
            if (release < 1 && (vessel.Situation != Situation.Landed || !vessel.Alive))
                release = Mathf.Min(1, release + Time.deltaTime / ReleaseTime);
            Pose();
        }

        void Pose()
        {
            var o = body.Orientation;
            var pos = FloatingOrigin.ToUnity(body.Position + o * anchorBf);
            bool show = !MapView.IsOpen && pos.magnitude < DrawDistance;
            foreach (var r in renderers) r.enabled = show;
            if (!show) return;
            transform.SetPositionAndRotation(pos, FloatingOrigin.ToQuaternion(o.SwapYZ * frameBf));
            // Плавный старт и мягкая остановка — ферма тяжёлая, рывком не ходит.
            float a = armTilt - ReleaseAngle * Mathf.SmoothStep(0, 1, release);
            for (int k = 0; k < arms.Count; k++) arms[k].localRotation = Quaternion.Euler(-a, 90 * k, 0);
        }

        /// <summary>Коробки с UV в метрах / tile по проекции на грань — бетон не тянется по длинной плите.</summary>
        sealed class MeshBuilder
        {
            readonly float tile;
            readonly List<Vector3> v = new List<Vector3>();
            readonly List<Vector3> n = new List<Vector3>();
            readonly List<Vector2> uv = new List<Vector2>();
            readonly List<int> t = new List<int>();

            public MeshBuilder(float tile) { this.tile = tile; }

            public void Box(Vector3 min, Vector3 max)
            {
                var c = (min + max) * 0.5f;
                var e = (max - min) * 0.5f;
                Face(c, e, Vector3.right); Face(c, e, Vector3.left);
                Face(c, e, Vector3.up); Face(c, e, Vector3.down);
                Face(c, e, Vector3.forward); Face(c, e, Vector3.back);
            }

            void Face(Vector3 c, Vector3 e, Vector3 nrm)
            {
                // Две оси грани, Cross(a, b) — наружу: в Unity лицевая у треугольника (p0, p1, p2) та,
                // куда смотрит Cross(p1 − p0, p2 − p0).
                Vector3 a = nrm.y != 0 ? Vector3.right : Vector3.up;
                Vector3 b = Vector3.Cross(nrm, a);
                if (Vector3.Dot(Vector3.Cross(a, b), nrm) < 0) b = -b;
                var center = c + Vector3.Scale(nrm, e);
                var ea = Vector3.Scale(a, e);
                var eb = Vector3.Scale(b, e);
                int i0 = v.Count;
                var corners = new[] { center - ea - eb, center + ea - eb, center + ea + eb, center - ea + eb };
                foreach (var p in corners)
                {
                    v.Add(p);
                    n.Add(nrm);
                    // Проекция на плоскость грани: у верха — XZ, у боков — горизонталь × высота.
                    uv.Add(nrm.y != 0 ? new Vector2(p.x, p.z) / tile
                         : new Vector2(nrm.x != 0 ? p.z : p.x, p.y) / tile);
                }
                t.Add(i0); t.Add(i0 + 1); t.Add(i0 + 2);
                t.Add(i0); t.Add(i0 + 2); t.Add(i0 + 3);
            }

            public Mesh Build()
            {
                var m = new Mesh { name = "Pad" };
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
