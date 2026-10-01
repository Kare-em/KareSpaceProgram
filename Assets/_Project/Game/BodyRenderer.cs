using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kare.Space.Game
{
    /// <summary>
    /// Меши тел (GDD §2.7, §2.8, этап 1): UV-сфера с процедурной текстурой на каждое тело каталога
    /// и локальный патч рельефа под бортом. Дальние тела сжимаются по расстоянию `d' = d0·(1+ln(d/d0))`
    /// с масштабом k = d'/d — угловой размер истинный. Детей создаёт сам: радиусы и рельеф — из Core.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class BodyRenderer : MonoBehaviour
    {
        public static BodyRenderer Instance { get; private set; }

        /// <summary>Порог сжатия §2.7, м. Пара: дальняя плоскость камеры 2e8 (FlightCamera.FarClip) —
        /// при d0 = 1e7 Нептун ложится на 1,4e8, внутрь неё.</summary>
        public const double CompressionStart = 1e7;

        public Material BaseMaterial;

        /// <summary>Карта (§9.6) рисует без сжатия.</summary>
        public static bool Compression = true;

        // Разрешение UV-сферы. Пара: стрела прогиба грани R·Δθ²/8 (у Земли при 384 — ≈ 210 м) —
        // настолько же сфера опущена под истинную поверхность, чтобы её закрывал патч и не было z-fighting.
        const int SegmentsDetailed = 384, SegmentsPlain = 96;
        const int TextureDetailed = 1024, TexturePlain = 256;

        // Патч под бортом: сетка PatchN², узлы сгущаются к центру (x = L·t·|t|), полуширина PatchHalf.
        // Пара: шаг в центре ≈ L·(2/N)² — при 80 км и 128 это ≈ 20 м.
        const int PatchN = 128;
        const double PatchHalf = 80000;
        /// <summary>Выше этой высоты над рельефом патч не нужен — горизонт дальше полуширины патча.</summary>
        const double PatchMaxAltitude = 40000;

        sealed class Entry
        {
            public CelestialBody Body;
            public Transform Tr;
            public Renderer Rend;
            public Material Mat;
            public double SphereRadius;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly Dictionary<CelestialBody, Entry> byBody = new Dictionary<CelestialBody, Entry>();

        Transform patchTr;
        MeshFilter patchMf;
        MeshRenderer patchMr;
        CelestialBody patchBody;
        Vector3d patchCenterBf;     // направление центра патча в осях тела (P)
        Vector3d patchCenterLocal;  // точка центра патча (с высотой) в осях тела, м

        void Awake() => Instance = this;

        void Start()
        {
            var u = GameBootstrap.U;
            if (u == null) { enabled = false; return; }
            if (BaseMaterial == null) BaseMaterial = new Material(Shader.Find("HDRP/Lit"));

            foreach (var b in u.System.Bodies)
            {
                if (b.Parent == null) continue; // Солнце рисует PBSky по углу Directional Light (§9.1)
                bool detailed = b.Id == "earth" || b.Id == "moon" || b.Id == "mars";
                var go = new GameObject(b.Name);
                go.transform.SetParent(transform, false);
                var e = new Entry { Body = b, Tr = go.transform };
                int seg = detailed ? SegmentsDetailed : SegmentsPlain;
                double dTheta = 2 * Mathf.PI / seg;
                double sag = b.Radius * dTheta * dTheta / 8;
                e.SphereRadius = b.Radius - (detailed ? sag * 1.5 : 0);
                go.AddComponent<MeshFilter>().sharedMesh = BuildSphere(b, seg, e.SphereRadius);
                var mr = go.AddComponent<MeshRenderer>();
                e.Mat = new Material(BaseMaterial) { name = $"Body {b.Id}" };
                e.Mat.SetTexture("_BaseColorMap", BuildTexture(b, detailed ? TextureDetailed : TexturePlain));
                e.Mat.SetColor("_BaseColor", Color.white);
                e.Mat.SetFloat("_Smoothness", 0.15f);
                mr.sharedMaterial = e.Mat;
                // Тени от планеты на планету рисовать бессмысленно (каскады 2 км), затмения — SunLight.
                mr.shadowCastingMode = ShadowCastingMode.Off;
                e.Rend = mr;
                entries.Add(e);
                byBody[b] = e;
            }

            var p = new GameObject("Terrain Patch");
            p.transform.SetParent(transform, false);
            patchTr = p.transform;
            patchMf = p.AddComponent<MeshFilter>();
            patchMr = p.AddComponent<MeshRenderer>();
            patchMr.shadowCastingMode = ShadowCastingMode.On;
            patchMf.sharedMesh = new Mesh { name = "Patch", indexFormat = IndexFormat.UInt32 };
            p.SetActive(false);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Положение и масштаб меша тела в Unity с учётом сжатия §2.7. scale — множитель к истинным
        /// размерам (1 вблизи). Нужен и PBSky (SkyController), чтобы небо совпадало с мешем.
        /// </summary>
        public static Vector3 Project(Vector3d worldP, out double scale)
        {
            var rel = (worldP - FloatingOrigin.OriginP).SwapYZ;
            double d = rel.magnitude;
            scale = 1;
            if (Compression && d > CompressionStart)
                scale = CompressionStart * (1 + System.Math.Log(d / CompressionStart)) / d;
            return FloatingOrigin.ToVector3(rel * scale);
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (u?.Active == null) return;
            FloatingOrigin.Refresh();
            foreach (var e in entries)
            {
                var pos = Project(e.Body.Position, out double k);
                e.Tr.SetPositionAndRotation(pos, FloatingOrigin.BodyRotation(e.Body));
                e.Tr.localScale = Vector3.one * (float)(e.SphereRadius * k);
            }
            UpdatePatch(u.Active);
        }

        // ---------------------------------------------------------------- патч под бортом

        void UpdatePatch(Vessel v)
        {
            var b = v.Body;
            bool want = Compression && b.Terrain != null && v.Alive && v.TerrainAltitude < PatchMaxAltitude;
            if (!want)
            {
                if (patchTr.gameObject.activeSelf) patchTr.gameObject.SetActive(false);
                return;
            }
            // Положение борта в осях тела: Orientation переводит оси тела в инерциальные P.
            var bf = (b.Orientation.Inverse * v.Position).normalized;
            double moved = Vector3d.Angle(bf, patchCenterBf) * b.Radius;
            double limit = System.Math.Max(300, v.TerrainAltitude * 0.3);
            if (patchBody != b || moved > limit || !patchTr.gameObject.activeSelf)
                RebuildPatch(b, bf);
            patchTr.SetPositionAndRotation(
                FloatingOrigin.ToUnity(b.Position + b.Orientation * patchCenterLocal),
                FloatingOrigin.BodyRotation(b));
        }

        void RebuildPatch(CelestialBody b, Vector3d centerBf)
        {
            patchBody = b;
            patchCenterBf = centerBf;
            patchCenterLocal = centerBf * (b.Radius + b.SurfaceHeight(centerBf));
            var e1 = Vector3d.AnyPerpendicular(centerBf).normalized;
            var e2 = Vector3d.Cross(centerBf, e1).normalized;

            int n = PatchN + 1;
            var verts = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double tx = 2.0 * i / PatchN - 1, ty = 2.0 * j / PatchN - 1;
                double x = PatchHalf * tx * System.Math.Abs(tx) / b.Radius;
                double y = PatchHalf * ty * System.Math.Abs(ty) / b.Radius;
                var dir = (centerBf + e1 * x + e2 * y).normalized;
                var p = dir * (b.Radius + b.SurfaceHeight(dir)) - patchCenterLocal;
                verts[j * n + i] = FloatingOrigin.ToVector3(p.SwapYZ);
                uvs[j * n + i] = LatLonUv(dir);
            }
            var tris = new int[PatchN * PatchN * 6];
            int t = 0;
            for (int j = 0; j < PatchN; j++)
            for (int i = 0; i < PatchN; i++)
            {
                int a = j * n + i, c = a + n;
                tris[t++] = a; tris[t++] = c; tris[t++] = a + 1;
                tris[t++] = a + 1; tris[t++] = c; tris[t++] = c + 1;
            }
            var mesh = patchMf.sharedMesh;
            mesh.Clear();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            FixWinding(mesh, (centerBf).SwapYZ);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            patchMr.sharedMaterial = byBody[b].Mat;
            patchTr.gameObject.SetActive(true);
        }

        // ---------------------------------------------------------------- генерация

        static Vector2 LatLonUv(Vector3d dirBf)
        {
            CelestialBody.BodyFixedToLatLon(dirBf, out double lat, out double lon);
            return new Vector2((float)((lon + 180) / 360), (float)((lat + 90) / 180));
        }

        /// <summary>UV-сфера в осях тела Unity (SwapYZ от осей тела P), единичного радиуса плюс рельеф.</summary>
        static Mesh BuildSphere(CelestialBody b, int seg, double radius)
        {
            int rings = seg / 2;
            int cols = seg + 1;
            var verts = new Vector3[cols * (rings + 1)];
            var uvs = new Vector2[verts.Length];
            for (int r = 0; r <= rings; r++)
            {
                double lat = -90 + 180.0 * r / rings;
                for (int s = 0; s <= seg; s++)
                {
                    double lon = -180 + 360.0 * s / seg;
                    var dir = CelestialBody.LatLonToBodyFixed(lat, lon);
                    double h = b.Terrain != null ? b.SurfaceHeight(dir) : 0;
                    verts[r * cols + s] = FloatingOrigin.ToVector3((dir * ((radius + h) / radius)).SwapYZ);
                    uvs[r * cols + s] = new Vector2((float)s / seg, (float)r / rings);
                }
            }
            var tris = new int[seg * rings * 6];
            int t = 0;
            for (int r = 0; r < rings; r++)
            for (int s = 0; s < seg; s++)
            {
                int a = r * cols + s, c = a + cols;
                tris[t++] = a; tris[t++] = c; tris[t++] = a + 1;
                tris[t++] = a + 1; tris[t++] = c; tris[t++] = c + 1;
            }
            var mesh = new Mesh { name = $"Sphere {b.Id}", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            FixWinding(mesh, Vector3d.zero);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// SwapYZ меняет правизну, поэтому порядок обхода не угадываем, а проверяем: нормаль первой
        /// невырожденной грани должна смотреть наружу (от центра тела). Иначе переворачиваем все.
        /// </summary>
        static void FixWinding(Mesh mesh, Vector3d outwardHint)
        {
            var v = mesh.vertices;
            var tri = mesh.triangles;
            for (int i = 0; i < tri.Length; i += 3)
            {
                var n = Vector3.Cross(v[tri[i + 1]] - v[tri[i]], v[tri[i + 2]] - v[tri[i]]);
                if (n.sqrMagnitude < 1e-12f) continue;
                var outward = outwardHint == Vector3d.zero ? v[tri[i]] : FloatingOrigin.ToVector3(outwardHint);
                if (Vector3.Dot(n, outward) >= 0) return;
                for (int k = 0; k < tri.Length; k += 3) (tri[k + 1], tri[k + 2]) = (tri[k + 2], tri[k + 1]);
                mesh.triangles = tri;
                return;
            }
        }

        /// <summary>Равнопромежуточная текстура-заглушка (§9.4 этап 1): цвет по высоте, океан, шапки, полосы.</summary>
        static Texture2D BuildTexture(CelestialBody b, int w)
        {
            int h = w / 2;
            var look = BodyVisuals.Get(b.Id);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = $"Tex {b.Id}", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            double amp = b.Terrain != null && b.Terrain.Amplitude > 0 ? b.Terrain.Amplitude : 1;
            bool ocean = b.Terrain != null && b.Terrain.Ocean;
            for (int y = 0; y < h; y++)
            {
                double lat = -90 + 180.0 * (y + 0.5) / h;
                for (int x = 0; x < w; x++)
                {
                    double lon = -180 + 360.0 * (x + 0.5) / w;
                    Color c;
                    if (look.Bands > 0)
                    {
                        float band = Mathf.Sin((float)(lat * Constants.Deg2Rad) * look.Bands * 1.7f + Mathf.Sin((float)(lon * Constants.Deg2Rad) * 3) * 0.15f);
                        c = Color.Lerp(look.Low, look.High, 0.5f + 0.5f * band * look.BandContrast);
                    }
                    else
                    {
                        var dir = CelestialBody.LatLonToBodyFixed(lat, lon);
                        double hh = b.Terrain != null ? b.SurfaceHeight(dir) : 0;
                        if (ocean && hh <= 0) c = look.Ocean;
                        else c = Color.Lerp(look.Low, look.High, Mathf.Clamp01((float)(0.5 + 0.5 * hh / amp)));
                        if (System.Math.Abs(lat) > look.IceLatitude) c = look.Ice;
                    }
                    px[y * w + x] = c;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }
    }
}
