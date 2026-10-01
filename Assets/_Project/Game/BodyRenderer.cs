using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.Game
{
    /// <summary>
    /// Меши тел (GDD §2.7, §2.8, этап 1): UV-сфера с процедурной текстурой на каждое тело каталога
    /// и локальный патч рельефа под бортом. Дальние тела кладутся в «оболочку» перед дальней плоскостью
    /// (аналог ScaledSpace KSP одной камерой) с масштабом k = d'/d — угловой размер истинный.
    /// Детей создаёт сам: радиусы и рельеф — из Core.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class BodyRenderer : MonoBehaviour
    {
        public static BodyRenderer Instance { get; private set; }

        /// <summary>
        /// Оболочка сжатия §2.7, м: тело, у которого расстояние до горизонта H больше ShellStart,
        /// масштабируется так, что H ложится в ShellStart + ShellWidth·x/(x + ShellSoftness), x = ln(H/ShellStart).
        /// Почему не 2e8, как в GDD: при дальней плоскости ≥ 3e6 (near 0,1) и 2e8 (near 2/20) HDRP теряет
        /// тени Солнца целиком; 1e7 при near 1 и 1e6 при near 0,1 — тени есть (замер 01.10.2026).
        /// Пара: ShellStart + ShellWidth = 9,5e6 &lt; FlightCamera.FarClip = 1e7.
        /// Пара: ShellStart = 5e6 больше горизонта с потолка патча (40 км → 715 км) — патч всегда без сжатия.
        /// </summary>
        public const double ShellStart = 5e6, ShellWidth = 4.5e6;
        /// <summary>Мягкость оболочки (в единицах ln): Луна с Земли ложится на ≈6,7e6, Нептун — на ≈8e6.</summary>
        const double ShellSoftness = 7;

        public Material BaseMaterial;

        [Header("Грунт вблизи (§2.8): цвет тайлом в метрах + крупная вариация детальной картой")]
        public Texture2D EarthGround, EarthMacro, MoonGround, MarsGround;
        [Tooltip("Нормали грунта Земли и ряби воды (из Car_Train), тайл в метрах — только в патче вблизи")]
        public Texture2D EarthGroundNormal, WaterNormal;
        /// <summary>Тайл ряби, м, и скорость её сноса, м/с. Пара: UV0 патча — в тайлах GroundTile, поэтому
        /// масштаб воды задаётся через _BaseColorMap_ST = GroundTile / WaterTile.</summary>
        const double WaterTile = 40, WaterDrift = 0.7;
        /// <summary>Сила нормалей: рябь мягкая (с высоты стола крупная рябь читается «пластиком»), грунт — заметнее.</summary>
        const float WaterNormalScale = 0.25f, GroundNormalScale = 0.6f;
        /// <summary>Вода вблизи: гладкость как у океана на сфере (EarthSurface.OceanSmoothness), но рябь ещё и рассеивает блик.</summary>
        const float WaterSmoothness = 0.92f;
        /// <summary>Облака Земли: высота слоя, м. Пара: ниже потолка патча PatchMaxAltitude (40 км) — с борта
        /// слой виден сверху; снизу (камера внутри сферы) отсекается задними гранями.</summary>
        const double CloudAltitude = 8000;
        /// <summary>Сетка сферы облаков. Пара: стрела прогиба R·Δθ²/8 при 256 ≈ 480 м — много меньше CloudAltitude.</summary>
        const int CloudSegments = 256;
        /// <summary>Повтор тайла грунта и крупной вариации, м. Пара: шаг патча в центре ≈ 20 м — крупный
        /// масштаб много больше шага, иначе вариация не читается; мелкий — меньше камеры у стола (≈ 60 м).</summary>
        const double GroundTile = 6, MacroTile = 350;
        /// <summary>Контраст крупной вариации: доля отклонения яркости макро-текстуры от средней.</summary>
        const float MacroContrast = 1.6f;

        /// <summary>Карта (§9.6) рисует без сжатия.</summary>
        public static bool Compression = true;

        // Разрешение UV-сферы. Пара: стрела прогиба грани R·Δθ²/8 (у Земли при 384 — ≈ 210 м) —
        // настолько же сфера опущена под истинную поверхность, чтобы её закрывал патч и не было z-fighting.
        const int SegmentsDetailed = 384, SegmentsPlain = 96;
        const int TextureDetailed = 1024, TexturePlain = 256;
        /// <summary>Земля — 2048: тексель 20 км, береговая линия с орбиты не «ступеньками». Огни остаются 1024.</summary>
        const int TextureEarth = 2048;

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
            /// <summary>Тот же материал без ночных огней: вблизи тексель 40 км светился бы пятном под столом.</summary>
            public Material PatchMat;
            public double SphereRadius;
            /// <summary>Есть свой грунт: UV патча — метры, цвет подгоняется к цвету тела в точке.</summary>
            public bool Ground;
            public Color GroundMean;
        }

        Material waterMat;
        Vector2 waterOffset;

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
                bool earth = b.Id == "earth";
                e.Mat.SetTexture("_BaseColorMap", BuildTexture(b, earth ? TextureEarth : detailed ? TextureDetailed : TexturePlain, out var mask));
                e.Mat.SetColor("_BaseColor", Color.white);
                e.Mat.SetFloat("_Smoothness", 0.15f);
                e.PatchMat = new Material(e.Mat) { name = $"Patch {b.Id}" };
                if (earth)
                {
                    AddNightLights(e.Mat, b, TextureDetailed);
                    // Блик Солнца на океане (§9.4): гладкость из маски, только у сферы — у патча своя вода.
                    e.Mat.SetTexture("_MaskMap", mask);
                    e.Mat.SetFloat("_MetallicRemapMin", 0);
                    e.Mat.SetFloat("_MetallicRemapMax", 0);
                    e.Mat.SetFloat("_AORemapMin", 1);
                    e.Mat.SetFloat("_AORemapMax", 1);
                    e.Mat.SetFloat("_SmoothnessRemapMin", 0);
                    e.Mat.SetFloat("_SmoothnessRemapMax", 1);
                    // Текстуры лежат на материале — Validate ставит _MASKMAP и _EMISSIVE_COLOR_MAP сам.
                    HDMaterial.ValidateMaterial(e.Mat);
                }
                SetupGround(e);
                mr.sharedMaterial = e.Mat;
                // Тени от планеты на планету рисовать бессмысленно (каскады 2 км), затмения — SunLight.
                mr.shadowCastingMode = ShadowCastingMode.Off;
                e.Rend = mr;
                entries.Add(e);
                byBody[b] = e;
                if (earth) AddClouds(e);
            }

            var p = new GameObject("Terrain Patch");
            p.transform.SetParent(transform, false);
            patchTr = p.transform;
            patchMf = p.AddComponent<MeshFilter>();
            patchMr = p.AddComponent<MeshRenderer>();
            patchMr.shadowCastingMode = ShadowCastingMode.On;
            patchMf.sharedMesh = new Mesh { name = "Patch", indexFormat = IndexFormat.UInt32 };
            p.SetActive(false);
            waterMat = BuildWaterMaterial();
        }

        /// <summary>
        /// Вода патча (§2.8): отдельная субмеш на треугольниках океана. По мотивам воды Car_Train — гладкий Lit
        /// с картой ряби, которую сносит по UV; цвет — тот же, что у океана на сфере в этой точке.
        /// </summary>
        Material BuildWaterMaterial()
        {
            var m = new Material(BaseMaterial) { name = "Patch Water" };
            m.SetColor("_BaseColor", EarthSurface.OceanColor(100));
            m.SetFloat("_Smoothness", WaterSmoothness);
            m.SetFloat("_Metallic", 0);
            if (WaterNormal != null)
            {
                m.SetTexture("_NormalMap", WaterNormal);
                m.SetFloat("_NormalScale", WaterNormalScale);
            }
            HDMaterial.ValidateMaterial(m);
            return m;
        }

        /// <summary>
        /// Слой облаков Земли (§9.4): прозрачная сфера-ребёнок тела — сжатие оболочки и вращение берёт от него.
        /// Lit, а не Unlit: ночью облака гаснут вместе с поверхностью, а терминатор проходит и по ним.
        /// </summary>
        void AddClouds(Entry e)
        {
            var go = new GameObject("Clouds");
            go.transform.SetParent(e.Tr, false);
            go.transform.localScale = Vector3.one * (float)((e.Body.Radius + CloudAltitude) / e.SphereRadius);
            go.AddComponent<MeshFilter>().sharedMesh = BuildSphere(e.Body, CloudSegments, 1, false);
            var mr = go.AddComponent<MeshRenderer>();
            var m = new Material(BaseMaterial) { name = "Clouds" };
            m.SetTexture("_BaseColorMap", BuildClouds(TextureEarth));
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0);
            m.SetFloat("_Metallic", 0);
            HDMaterial.SetSurfaceType(m, true); // внутри — ValidateMaterial
            mr.sharedMaterial = m;
            mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        static Texture2D BuildClouds(int w)
        {
            int h = w / 2;
            var px = new Color32[w * h];
            System.Threading.Tasks.Parallel.For(0, h, y =>
            {
                double lat = -90 + 180.0 * (y + 0.5) / h;
                for (int x = 0; x < w; x++)
                {
                    double lon = -180 + 360.0 * (x + 0.5) / w;
                    px[y * w + x] = new Color32(245, 247, 250, (byte)(255 * EarthSurface.Cloud(lat, lon)));
                }
            });
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "Clouds earth", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, anisoLevel = 4 };
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Положение и масштаб меша тела в Unity с учётом сжатия §2.7. scale — множитель к истинным
        /// размерам (1 вблизи). Нужен и PBSky (SkyController), чтобы небо совпадало с мешем.
        /// Мерило — расстояние до горизонта, а не до центра: вся видимая часть сферы ближе горизонта,
        /// поэтому Земля под стартовым столом (центр 6,4e6) не сжимается и совпадает с патчем.
        /// </summary>
        public static Vector3 Project(CelestialBody b, out double scale)
        {
            var rel = (b.Position - FloatingOrigin.OriginP).SwapYZ;
            double d = rel.magnitude;
            double r = b.Radius;
            double horizon = d > r ? System.Math.Sqrt(d * d - r * r) : 0;
            scale = 1;
            if (Compression && horizon > ShellStart)
            {
                double x = System.Math.Log(horizon / ShellStart);
                scale = (ShellStart + ShellWidth * x / (x + ShellSoftness)) / horizon;
            }
            return FloatingOrigin.ToVector3(rel * scale);
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (u?.Active == null) return;
            FloatingOrigin.Refresh();
            foreach (var e in entries)
            {
                var pos = Project(e.Body, out double k);
                e.Tr.SetPositionAndRotation(pos, FloatingOrigin.BodyRotation(e.Body));
                e.Tr.localScale = Vector3.one * (float)(e.SphereRadius * k);
            }
            UpdatePatch(u.Active);
            // Снос ряби: только дробная часть, чтобы смещение не копило ошибку float.
            float k = (float)(GroundTile / WaterTile);
            waterOffset.x = Frac(waterOffset.x + Time.deltaTime * WaterDrift / WaterTile);
            waterOffset.y = Frac(waterOffset.y + Time.deltaTime * WaterDrift * 0.6 / WaterTile);
            waterMat.SetVector("_BaseColorMap_ST", new Vector4(k, k, waterOffset.x, waterOffset.y));
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
            var e = byBody[b];
            var macro = e.Ground ? new Vector2[n * n] : null;
            bool ocean = b.Terrain.Ocean;
            var wet = ocean ? new bool[n * n] : null;
            // Метрические UV от широты/долготы, а не от осей патча: оси патча меняются при каждой
            // перестройке, и текстура прыгала бы под ракетой. Опорная точка округлена до градуса, а её
            // доля тайла добавлена отдельно — в float остаются только метры внутри патча.
            double lat0 = 0, lon0 = 0, kx = 0, ky = b.Radius * Constants.Deg2Rad;
            Vector2 g0 = default, m0 = default;
            if (e.Ground)
            {
                CelestialBody.BodyFixedToLatLon(centerBf, out double clat, out double clon);
                lat0 = System.Math.Round(clat);
                lon0 = System.Math.Round(clon);
                kx = ky * System.Math.Max(0.1, System.Math.Cos(lat0 * Constants.Deg2Rad));
                g0 = new Vector2(Frac(lon0 * kx / GroundTile), Frac(lat0 * ky / GroundTile));
                m0 = new Vector2(Frac(lon0 * kx / MacroTile), Frac(lat0 * ky / MacroTile));
                var c = SurfaceColor(b, BodyVisuals.Get(b.Id), clat, clon);
                if (b.Id == "earth") waterMat.SetColor("_BaseColor", EarthSurface.OceanColor(System.Math.Max(50, -Kare.Space.Core.Terrain.RawHeight(b.Terrain, centerBf))));
                // Средний цвет тайла → цвет тела в точке: с высоты патч не выделяется квадратом.
                e.PatchMat.SetColor("_BaseColor", new Color(c.r / e.GroundMean.r, c.g / e.GroundMean.g, c.b / e.GroundMean.b, 1));
            }
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double tx = 2.0 * i / PatchN - 1, ty = 2.0 * j / PatchN - 1;
                double x = PatchHalf * tx * System.Math.Abs(tx) / b.Radius;
                double y = PatchHalf * ty * System.Math.Abs(ty) / b.Radius;
                var dir = (centerBf + e1 * x + e2 * y).normalized;
                double hgt = b.SurfaceHeight(dir);
                if (ocean) wet[j * n + i] = hgt <= 0;
                var p = dir * (b.Radius + hgt) - patchCenterLocal;
                verts[j * n + i] = FloatingOrigin.ToVector3(p.SwapYZ);
                if (e.Ground)
                {
                    CelestialBody.BodyFixedToLatLon(dir, out double lat, out double lon);
                    double dl = lon - lon0;
                    if (dl > 180) dl -= 360; else if (dl < -180) dl += 360;
                    double mx = dl * kx, my = (lat - lat0) * ky;
                    uvs[j * n + i] = new Vector2((float)(mx / GroundTile), (float)(my / GroundTile)) + g0;
                    macro[j * n + i] = new Vector2((float)(mx / MacroTile), (float)(my / MacroTile)) + m0;
                }
                else uvs[j * n + i] = LatLonUv(dir);
            }
            // Суша и вода — разные субмеши: вода — треугольник, у которого все три узла на уровне моря.
            var land = new List<int>(PatchN * PatchN * 6);
            var water = new List<int>();
            for (int j = 0; j < PatchN; j++)
            for (int i = 0; i < PatchN; i++)
            {
                int a = j * n + i, c = a + n;
                AddTri(ocean && wet[a] && wet[c] && wet[a + 1] ? water : land, a, c, a + 1);
                AddTri(ocean && wet[a + 1] && wet[c] && wet[c + 1] ? water : land, a + 1, c, c + 1);
            }
            var mesh = patchMf.sharedMesh;
            mesh.Clear();
            mesh.vertices = verts;
            mesh.uv = uvs;
            if (macro != null) mesh.uv2 = macro;
            mesh.subMeshCount = ocean ? 2 : 1;
            mesh.SetTriangles(land, 0);
            if (ocean) mesh.SetTriangles(water, 1);
            FixWinding(mesh, (centerBf).SwapYZ);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents(); // нормал-карты грунта и ряби
            mesh.RecalculateBounds();
            patchMr.sharedMaterials = ocean ? new[] { e.PatchMat, waterMat } : new[] { e.PatchMat };
            patchTr.gameObject.SetActive(true);
        }

        static float Frac(double x) => (float)(x - System.Math.Floor(x));

        static void AddTri(List<int> list, int a, int b, int c)
        {
            list.Add(a); list.Add(b); list.Add(c);
        }

        /// <summary>
        /// Свой грунт патча: цвет — тайл в метрах (UV0), крупные пятна — детальная карта HDRP по UV1
        /// (только альбедо, R; G/A — плоская нормаль, B — нейтральная гладкость). С высоты мипы
        /// усредняют обе в среднее — патч сходится к цвету тела без шва.
        /// </summary>
        void SetupGround(Entry e)
        {
            var id = e.Body.Id;
            var albedo = id == "earth" ? EarthGround : id == "moon" ? MoonGround : id == "mars" ? MarsGround : null;
            if (albedo == null || !albedo.isReadable) return;
            var src = id == "earth" && EarthMacro != null && EarthMacro.isReadable ? EarthMacro : albedo;
            e.Ground = true;
            e.GroundMean = MeanColor(albedo);
            var m = e.PatchMat;
            m.SetTexture("_BaseColorMap", albedo);
            m.SetTexture("_DetailMap", BuildDetail(src));
            // ValidateMaterial в рантайме не вызывается — ключевое слово и канал UV ставим сами.
            m.EnableKeyword("_DETAIL_MAP");
            m.SetFloat("_UVDetail", 1);
            m.SetVector("_UVDetailsMappingMask", new Vector4(0, 1, 0, 0));
            m.SetFloat("_DetailAlbedoScale", 1);
            m.SetFloat("_DetailNormalScale", 0);
            m.SetFloat("_DetailSmoothnessScale", 0);
            if (id == "earth" && EarthGroundNormal != null)
            {
                m.SetTexture("_NormalMap", EarthGroundNormal);
                m.SetFloat("_NormalScale", GroundNormalScale);
                m.EnableKeyword("_NORMALMAP");
                m.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
        }

        static Color MeanColor(Texture2D t)
        {
            int mip = Mathf.Max(0, t.mipmapCount - 5);
            var px = t.GetPixels(mip);
            Color s = Color.black;
            foreach (var c in px) s += c;
            s /= px.Length;
            return new Color(Mathf.Max(s.r, 0.02f), Mathf.Max(s.g, 0.02f), Mathf.Max(s.b, 0.02f), 1);
        }

        static Texture2D BuildDetail(Texture2D src)
        {
            const int w = 256;
            int mip = 0;
            while (mip + 1 < src.mipmapCount && (src.width >> (mip + 1)) >= w) mip++;
            int sw = Mathf.Max(1, src.width >> mip), sh = Mathf.Max(1, src.height >> mip);
            var px = src.GetPixels(mip);
            var lum = new float[w * w];
            float mean = 0;
            for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                var c = px[(y * sh / w) * sw + x * sw / w];
                float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                lum[y * w + x] = l;
                mean += l;
            }
            mean /= lum.Length;
            var tex = new Texture2D(w, w, TextureFormat.RGBA32, true, true) { name = $"Detail {src.name}", wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            var outPx = new Color32[w * w];
            for (int i = 0; i < lum.Length; i++)
            {
                float d = 0.5f + (lum[i] - mean) / Mathf.Max(mean, 0.05f) * 0.5f * MacroContrast;
                byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(255 * d), 0, 255);
                outPx[i] = new Color32(r, 128, 128, 128);
            }
            tex.SetPixels32(outPx);
            tex.Apply(true, true);
            return tex;
        }

        // ---------------------------------------------------------------- генерация

        static Vector2 LatLonUv(Vector3d dirBf)
        {
            CelestialBody.BodyFixedToLatLon(dirBf, out double lat, out double lon);
            return new Vector2((float)((lon + 180) / 360), (float)((lat + 90) / 180));
        }

        /// <summary>UV-сфера в осях тела Unity (SwapYZ от осей тела P), единичного радиуса плюс рельеф.</summary>
        static Mesh BuildSphere(CelestialBody b, int seg, double radius, bool relief = true)
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
                    double h = relief && b.Terrain != null ? b.SurfaceHeight(dir) : 0;
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

        /// <summary>
        /// Ночные огни (§9.4, этап 1): эмиссия в нитах на суше вне льдов, гуще в умеренных широтах севера,
        /// пятнами по шуму + точки-города. Днём (EV 12+) их не видно, ночью (EV −5) — видно; экспозиция
        /// решает сама, переключать по терминатору не нужно.
        /// </summary>
        static void AddNightLights(Material m, CelestialBody b, int w)
        {
            int h = w / 2;
            var look = BodyVisuals.Get(b.Id);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = $"Lights {b.Id}", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            var rnd = new System.Random(4);
            for (int y = 0; y < h; y++)
            {
                double lat = -90 + 180.0 * (y + 0.5) / h;
                // Населённость по широте: пик 25–55° с. ш., юг втрое реже.
                double belt = System.Math.Exp(-System.Math.Pow((lat - 38) / 18, 2)) + 0.35 * System.Math.Exp(-System.Math.Pow((lat + 25) / 15, 2));
                for (int x = 0; x < w; x++)
                {
                    double lon = -180 + 360.0 * (x + 0.5) / w;
                    if (System.Math.Abs(lat) > look.IceLatitude - 8) continue;
                    var dir = CelestialBody.LatLonToBodyFixed(lat, lon);
                    if (b.SurfaceHeight(dir) <= 0) continue;
                    float n = Mathf.PerlinNoise((float)lon * 0.15f + 300, (float)lat * 0.15f + 300);
                    float n2 = Mathf.PerlinNoise((float)lon * 0.9f + 700, (float)lat * 0.9f + 700);
                    double v = belt * Mathf.Clamp01((n - 0.45f) * 3) * Mathf.Clamp01((n2 - 0.35f) * 2);
                    if (rnd.NextDouble() < belt * 0.03) v = System.Math.Max(v, 0.6 + 0.4 * rnd.NextDouble()); // город
                    byte c = (byte)(255 * System.Math.Min(1, v));
                    px[y * w + x] = new Color32(c, c, c, 255);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            m.SetTexture("_EmissiveColorMap", tex);
            m.SetColor("_EmissiveColor", NightLightsColor * NightLightsNits);
            m.EnableKeyword("_EMISSIVE_COLOR_MAP");
        }

        /// <summary>Яркость огней в пике, нит (порядок VIIRS для города, ≈1e-4 Вт/м²/ср). Пара: ночная экспозиция
        /// SkyController.EvMin = −5 — серое 18 % ≈ 0,004 нит, огни в 5 раз ярче.</summary>
        const float NightLightsNits = 0.02f;
        static readonly Color NightLightsColor = new Color(1f, 0.72f, 0.38f); // натрий

        /// <summary>Равнопромежуточная текстура-заглушка (§9.4 этап 1): цвет по высоте, океан, шапки, полосы.</summary>
        /// <summary>mask — маска HDRP (A — гладкость), только у Земли: блик на океане.</summary>
        static Texture2D BuildTexture(CelestialBody b, int w, out Texture2D mask)
        {
            int h = w / 2;
            var look = BodyVisuals.Get(b.Id);
            bool earth = b.Id == "earth";
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = $"Tex {b.Id}", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            var mk = earth ? new Color32[w * h] : null;
            // Строки независимы, рельеф и шум — чистые функции: параллельно (у Земли 2 млн отсчётов рельефа).
            System.Threading.Tasks.Parallel.For(0, h, y =>
            {
                double lat = -90 + 180.0 * (y + 0.5) / h;
                for (int x = 0; x < w; x++)
                {
                    double lon = -180 + 360.0 * (x + 0.5) / w;
                    if (earth)
                    {
                        px[y * w + x] = EarthSurface.Sample(b, lat, lon, out float s);
                        mk[y * w + x] = new Color32(0, 255, 0, (byte)(255 * s));
                    }
                    else px[y * w + x] = SurfaceColor(b, look, lat, lon);
                }
            });
            tex.SetPixels32(px);
            tex.Apply(true, true);
            mask = null;
            if (earth)
            {
                mask = new Texture2D(w, h, TextureFormat.RGBA32, true, true) { name = $"Mask {b.Id}", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };
                mask.SetPixels32(mk);
                mask.Apply(true, true);
            }
            return tex;
        }

        /// <summary>Цвет тела в точке — им же рисуется сфера (BuildTexture) и тонируется грунт патча.</summary>
        static Color SurfaceColor(CelestialBody b, BodyLook look, double lat, double lon)
        {
            if (b.Id == "earth") return EarthSurface.Sample(b, lat, lon, out _);
            if (look.Bands > 0)
            {
                float band = Mathf.Sin((float)(lat * Constants.Deg2Rad) * look.Bands * 1.7f + Mathf.Sin((float)(lon * Constants.Deg2Rad) * 3) * 0.15f);
                return Color.Lerp(look.Low, look.High, 0.5f + 0.5f * band * look.BandContrast);
            }
            double amp = b.Terrain != null && b.Terrain.Amplitude > 0 ? b.Terrain.Amplitude : 1;
            bool ocean = b.Terrain != null && b.Terrain.Ocean;
            var dir = CelestialBody.LatLonToBodyFixed(lat, lon);
            double hh = b.Terrain != null ? b.SurfaceHeight(dir) : 0;
            Color c = ocean && hh <= 0 ? look.Ocean
                    : Color.Lerp(look.Low, look.High, Mathf.Clamp01((float)(0.5 + 0.5 * hh / amp)));
            if (System.Math.Abs(lat) > look.IceLatitude) c = look.Ice;
            return c;
        }
    }
}
