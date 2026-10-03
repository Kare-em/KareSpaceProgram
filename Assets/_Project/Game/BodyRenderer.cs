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
        /// <summary>
        /// Настоящая карта тела (§9.4) вместо процедурной: Луна — LRO (NASA SVS CGI Moon Kit), остальные — Solar System
        /// Scope (CC BY 4.0, атрибуция в меню Esc). Яркость приведена к геометрическому альбедо (Tools/textures/sss_convert.py).
        /// Small — читаемая копия для цвета патча вблизи, Night и Clouds — только у Земли (огни, слой облаков).
        /// </summary>
        [System.Serializable]
        public struct BodyMapSet
        {
            public string Id;
            public Texture2D Map, Small, Night, Clouds;
        }

        public BodyMapSet[] Maps;
        [Tooltip("Кольца Сатурна (Solar System Scope): полоса, x — радиус от RingInner до RingOuter, альфа — плотность")]
        public Texture2D SaturnRing;

        /// <summary>Пиксели Small для SurfaceColor: тот вызывается из Parallel.For, Texture2D там трогать нельзя.</summary>
        static readonly Dictionary<string, (Color32[] Px, int W, int H)> smallPx = new Dictionary<string, (Color32[], int, int)>();

        /// <summary>
        /// Кольца Сатурна по полосе SSS, м от центра: край C на ≈ 290-м столбце из 8192, щель Кассини
        /// (117 580–122 170 км) — на 5450–5850, внешний край A (136 775 км) — на 7650, щель Энке — на 7296.
        /// Линейная подгонка по этим трём меткам даёт 8,44 км на столбец. Пара: SaturnRing.png — меняешь одно, правь второе.
        /// </summary>
        const double RingInner = 72.2e6, RingOuter = 141.35e6;
        const int RingSegments = 256;

        BodyMapSet MapFor(string id)
        {
            if (Maps != null) foreach (var m in Maps) if (m.Id == id) return m;
            return default;
        }
        /// <summary>Тайл ряби, м, и скорость её сноса, м/с. Пара: UV0 патча — в тайлах GroundTile, поэтому
        /// масштаб воды задаётся через _BaseColorMap_ST = GroundTile / WaterTile.</summary>
        const double WaterTile = 40, WaterDrift = 0.7;
        /// <summary>Сила нормалей: рябь мягкая (с высоты стола крупная рябь читается «пластиком»), грунт — заметнее.</summary>
        const float WaterNormalScale = 0.25f, GroundNormalScale = 0.6f;
        /// <summary>Вода вблизи: гладкость как у океана на сфере (EarthSurface.OceanSmoothness), но рябь ещё и рассеивает блик.</summary>
        const float WaterSmoothness = 0.92f;
        /// <summary>Дно под водой патча, м: узлы ниже уровня моря опускаются до RawHeight, но не глубже —
        /// плоскость воды (h = 0) накрывает дно, и берег — пересечение склона с водой, а не ступенька сетки
        /// (у треугольника «весь в воде / нет» берег шёл зубцами по 600 м). Пара: шаг патча у края ≈ 1,2 км —
        /// склон 200 м на ячейку не даёт z-конфликта воды с дном на дальности патча.</summary>
        const double ShoreDepth = 200;
        /// <summary>Облака Земли: высота слоя, м. Пара: ниже потолка патча PatchMaxAltitude (40 км) — с борта
        /// слой виден сверху; снизу (камера внутри сферы) отсекается задними гранями.</summary>
        const double CloudAltitude = 8000;
        /// <summary>Повтор тайла грунта и крупной вариации, м. Пара: шаг патча в центре ≈ 20 м — крупный
        /// масштаб много больше шага, иначе вариация не читается; мелкий — меньше камеры у стола (≈ 60 м).</summary>
        const double GroundTile = 6, MacroTile = 350;
        /// <summary>Контраст крупной вариации: доля отклонения яркости макро-текстуры от средней.</summary>
        const float MacroContrast = 1.6f;

        /// <summary>Карта (§9.6) рисует без сжатия.</summary>
        public static bool Compression = true;

        // Разрешение сфер, текстур и облаков — по уровню DetailSettings (меню Esc), см. BuildLod.

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
            /// <summary>Земля, Луна, Марс — густая сетка и крупные текстуры, сфера опущена под патч.</summary>
            public bool Detailed;
            public MeshFilter Mf;
            public MeshFilter CloudMf;
            public Material CloudMat;
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
            smallPx.Clear();
            if (Maps != null)
                foreach (var m in Maps)
                    if (m.Map != null && m.Small != null && m.Small.isReadable)
                        smallPx[m.Id] = (m.Small.GetPixels32(), m.Small.width, m.Small.height);

            foreach (var b in u.System.Bodies)
            {
                if (b.Parent == null) continue; // Солнце рисует PBSky по углу Directional Light (§9.1)
                var go = new GameObject(b.Name);
                go.transform.SetParent(transform, false);
                var e = new Entry { Body = b, Tr = go.transform, Detailed = b.Id == "earth" || b.Id == "moon" || b.Id == "mars" };
                e.Mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                e.Mat = new Material(BaseMaterial) { name = $"Body {b.Id}" };
                bool earth = b.Id == "earth";
                e.Mat.SetColor("_BaseColor", Color.white);
                e.Mat.SetFloat("_Smoothness", 0.15f);
                // Копия до текстур: огни и маска блика — только у сферы (у патча своя вода, тексель огней светился бы пятном).
                e.PatchMat = new Material(e.Mat) { name = $"Patch {b.Id}" };
                if (earth)
                {
                    AddClouds(e);
                    // Блик Солнца на океане (§9.4): гладкость из маски (BuildLod), только у сферы — у патча своя вода.
                    e.Mat.SetFloat("_MetallicRemapMin", 0);
                    e.Mat.SetFloat("_MetallicRemapMax", 0);
                    e.Mat.SetFloat("_AORemapMin", 1);
                    e.Mat.SetFloat("_AORemapMax", 1);
                    e.Mat.SetFloat("_SmoothnessRemapMin", 0);
                    e.Mat.SetFloat("_SmoothnessRemapMax", 1);
                }
                BuildLod(e);
                SetupGround(e);
                if (b.Id == "saturn" && SaturnRing != null) AddRing(e);
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
            waterMat = BuildWaterMaterial();
        }

        /// <summary>
        /// Всё, что зависит от DetailSettings: сетка сферы и её радиус, текстура тела (у Земли ещё маска блика и огни),
        /// сетка и текстура облаков. Старое освобождается: текстура Земли на Ультра — 170 МБ, утечка при каждой смене
        /// уровня копилась бы. Грунт патча не трогаем — он от уровня не зависит.
        /// </summary>
        void BuildLod(Entry e)
        {
            var b = e.Body;
            bool earth = b.Id == "earth";
            int seg = e.Detailed ? DetailSettings.SegmentsDetailed : DetailSettings.SegmentsPlain;
            // Стрела прогиба грани R·Δθ²/8: на полторы стрелы сфера опущена под истинную поверхность, чтобы её
            // закрывал патч и не было z-fighting. Радиус меняется с уровнем — масштаб в LateUpdate берёт его отсюда.
            double dTheta = 2 * Mathf.PI / seg;
            double sag = b.Radius * dTheta * dTheta / 8;
            e.SphereRadius = b.Radius - (e.Detailed ? sag * 1.5 : 0);
            Free(e.Mf.sharedMesh);
            e.Mf.sharedMesh = Own(BuildSphere(b, seg, e.SphereRadius));

            // Настоящая карта (§9.4): процедурная Луна не совпадала с морями и была вдвое светлее, у планет — полосы
            // вместо облачных поясов. Ассет, не своё — Own/Free не трогают его; уровень детали на него не влияет
            // (8k DXT1 ≈ 21 МБ с мипами). Земле процедурный проход всё равно нужен — маска блика океана.
            var set = MapFor(b.Id);
            if (set.Map != null && !earth)
            {
                Free(e.Mat.GetTexture("_BaseColorMap"));
                e.Mat.SetTexture("_BaseColorMap", set.Map);
                if (!e.Ground) e.PatchMat.SetTexture("_BaseColorMap", set.Map);
                return;
            }

            // Текстуры — из дискового кеша (TextureCache), генерация только при первом запуске уровня.
            int w = earth ? DetailSettings.TextureEarth : e.Detailed ? DetailSettings.TextureDetailed : DetailSettings.TexturePlain;
            string colorKey = $"{b.Id}_color_{w}", maskKey = $"{b.Id}_mask_{w}";
            var tex = TextureCache.Load(colorKey, false);
            var mask = earth ? TextureCache.Load(maskKey, true) : null;
            if (tex == null || earth && mask == null)
            {
                if (tex != null) Destroy(tex);
                if (mask != null) Destroy(mask);
                tex = TextureCache.Store(colorKey, BuildTexture(b, w, out mask));
                if (mask != null) mask = TextureCache.Store(maskKey, mask);
            }
            Free(e.Mat.GetTexture("_BaseColorMap"));
            if (set.Map != null)
            {
                Destroy(tex);
                tex = set.Map;
            }
            else Own(tex);
            e.Mat.SetTexture("_BaseColorMap", tex);
            if (!e.Ground) e.PatchMat.SetTexture("_BaseColorMap", tex);
            if (!earth) return;

            Free(e.Mat.GetTexture("_MaskMap"));
            e.Mat.SetTexture("_MaskMap", Own(mask));
            int lw = DetailSettings.TextureDetailed;
            string lightsKey = $"{b.Id}_lights_{lw}";
            // Огни: карта Black Marble уже в цвете натрия — тон не накладываем, только яркость пика.
            var lights = set.Night != null ? set.Night
                : Own(TextureCache.Load(lightsKey, false) ?? TextureCache.Store(lightsKey, BuildNightLights(b, lw)));
            Free(e.Mat.GetTexture("_EmissiveColorMap"));
            e.Mat.SetTexture("_EmissiveColorMap", lights);
            e.Mat.SetColor("_EmissiveColor", (set.Night != null ? Color.white : NightLightsColor) * NightLightsNits);
            // Текстуры лежат на материале — Validate ставит _MASKMAP и _EMISSIVE_COLOR_MAP сам.
            HDMaterial.ValidateMaterial(e.Mat);

            e.CloudMf.transform.localScale = Vector3.one * (float)((b.Radius + CloudAltitude) / e.SphereRadius);
            Free(e.CloudMf.sharedMesh);
            e.CloudMf.sharedMesh = Own(BuildSphere(b, DetailSettings.CloudSegments, 1, false));
            Free(e.CloudMat.GetTexture("_BaseColorMap"));
            int cw = DetailSettings.TextureClouds;
            string cloudKey = $"clouds_{cw}";
            // Облака SSS — серые, альфа из яркости (импорт FlightSceneBuilder.BodyMap, alphaSource = FromGrayScale).
            var clouds = set.Clouds != null ? set.Clouds
                : Own(TextureCache.Load(cloudKey, false, CloudAniso) ?? TextureCache.Store(cloudKey, BuildClouds(cw), CloudAniso));
            e.CloudMat.SetTexture("_BaseColorMap", clouds);
        }

        /// <summary>Созданное BuildLod. Освобождаем только своё: на материалах бывают и ассеты (BaseMaterial, заглушки),
        /// а Destroy ассета в Play — ошибка «not allowed».</summary>
        readonly HashSet<Object> lod = new HashSet<Object>();

        T Own<T>(T o) where T : Object
        {
            lod.Add(o);
            return o;
        }

        /// <summary>
        /// Кольца Сатурна (§9.4): плоское кольцо в экваторе — ребёнок сферы, поворот и сжатие оболочки берёт от неё
        /// (единица сетки — SphereRadius). Lit, двусторонний: с теневой стороны колец свет проходит слабее — темнее.
        /// Тень планеты на кольцах не рисуется (тела теней не отбрасывают, см. Start).
        /// </summary>
        void AddRing(Entry e)
        {
            var go = new GameObject("Rings");
            go.transform.SetParent(e.Tr, false);
            float r0 = (float)(RingInner / e.SphereRadius), r1 = (float)(RingOuter / e.SphereRadius);
            int n = RingSegments;
            var verts = new Vector3[(n + 1) * 2];
            var uvs = new Vector2[verts.Length];
            var tris = new int[n * 6];
            for (int i = 0; i <= n; i++)
            {
                float a = 2 * Mathf.PI * i / n, c = Mathf.Cos(a), s = Mathf.Sin(a);
                verts[i * 2] = new Vector3(c * r0, 0, s * r0);
                verts[i * 2 + 1] = new Vector3(c * r1, 0, s * r1);
                uvs[i * 2] = new Vector2(0, (float)i / n);
                uvs[i * 2 + 1] = new Vector2(1, (float)i / n);
                if (i == n) continue;
                int k = i * 6, v = i * 2;
                tris[k] = v; tris[k + 1] = v + 1; tris[k + 2] = v + 2;
                tris[k + 3] = v + 2; tris[k + 4] = v + 1; tris[k + 5] = v + 3;
            }
            var mesh = new Mesh { name = "Rings" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.normals = System.Array.ConvertAll(verts, _ => Vector3.up);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            var m = new Material(BaseMaterial) { name = "Rings" };
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0);
            m.SetFloat("_Metallic", 0);
            m.SetFloat("_EnableFogOnTransparent", 0);
            m.SetFloat("_DoubleSidedEnable", 1);
            m.SetTexture("_BaseColorMap", SaturnRing);
            HDMaterial.SetSurfaceType(m, true); // внутри — ValidateMaterial (ставит и двусторонность)
            mr.sharedMaterial = m;
            mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        /// <summary>Анизотропия облаков: слой с орбиты виден под скользящим углом у горизонта.</summary>
        const int CloudAniso = 8;

        void Free(Object o)
        {
            if (o != null && lod.Remove(o)) Destroy(o);
        }

        /// <summary>Перестроить тела под новый уровень детализации (меню Esc). Блокирует кадр на время генерации
        /// текстур — секунды на Ультра; миссия при этом не перезапускается.</summary>
        public void ApplyDetail()
        {
            foreach (var e in entries) BuildLod(e);
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
            e.CloudMf = go.AddComponent<MeshFilter>(); // сетку, масштаб и текстуру ставит BuildLod
            var mr = go.AddComponent<MeshRenderer>();
            var m = e.CloudMat = new Material(BaseMaterial) { name = "Clouds" };
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0);
            m.SetFloat("_Metallic", 0);
            // Туман на прозрачных добавляет рассеяние атмосферы и там, где альфа 0: вся сфера облаков с орбиты
            // была сплошь бирюзовой (замер 01.10.2026).
            m.SetFloat("_EnableFogOnTransparent", 0);
            m.SetTexture("_BaseColorMap", Texture2D.whiteTexture); // заглушка до BuildLod: SetSurfaceType валидирует с картой
            HDMaterial.SetSurfaceType(m, true); // внутри — ValidateMaterial
            mr.sharedMaterial = m;
            mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        static Texture2D BuildClouds(int w)
        {
            int h = w / 2;
            // Октава шума на каждое удвоение текстуры: мельчайшая октава ≈ 2 текселя. Пара: CloudBaseOctaves при 2048.
            int octaves = EarthSurface.CloudBaseOctaves + Mathf.RoundToInt(Mathf.Log(w / 2048f, 2));
            var px = new Color32[w * h];
            System.Threading.Tasks.Parallel.For(0, h, y =>
            {
                double lat = -90 + 180.0 * (y + 0.5) / h;
                for (int x = 0; x < w; x++)
                {
                    double lon = -180 + 360.0 * (x + 0.5) / w;
                    px[y * w + x] = new Color32(245, 247, 250, (byte)(255 * EarthSurface.Cloud(lat, lon, octaves)));
                }
            });
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "Clouds earth", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, anisoLevel = CloudAniso };
            tex.SetPixels32(px);
            tex.Apply(true, false);
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
            float wk = (float)(GroundTile / WaterTile);
            waterOffset.x = Frac(waterOffset.x + Time.deltaTime * WaterDrift / WaterTile);
            waterOffset.y = Frac(waterOffset.y + Time.deltaTime * WaterDrift * 0.6 / WaterTile);
            waterMat.SetVector("_BaseColorMap_ST", new Vector4(wk, wk, waterOffset.x, waterOffset.y));
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
            bool ocean = b.Terrain.Ocean;
            // У тел с океаном вторая половина вершин — плоскость воды на уровне моря.
            int nv = ocean ? 2 * n * n : n * n, wb = n * n;
            var verts = new Vector3[nv];
            var uvs = new Vector2[nv];
            var e = byBody[b];
            var macro = e.Ground ? new Vector2[nv] : null;
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
                if (ocean) verts[wb + j * n + i] = FloatingOrigin.ToVector3((dir * b.Radius - patchCenterLocal).SwapYZ);
                if (ocean && hgt <= 0)
                {
                    wet[j * n + i] = true;
                    hgt = System.Math.Max(Kare.Space.Core.Terrain.RawHeight(b.Terrain, dir), -ShoreDepth);
                }
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
            if (ocean)
            {
                System.Array.Copy(uvs, 0, uvs, wb, wb);
                if (macro != null) System.Array.Copy(macro, 0, macro, wb, wb);
            }
            // Суша и вода — разные субмеши. Суша — треугольники с сухим узлом, вода — с мокрым (на своих
            // вершинах уровня моря); на берегу рисуются оба, видимую кромку режет глубина.
            var land = new List<int>(PatchN * PatchN * 6);
            var water = new List<int>();
            for (int j = 0; j < PatchN; j++)
            for (int i = 0; i < PatchN; i++)
            {
                int a = j * n + i, c = a + n;
                Classify(land, water, wet, wb, a, c, a + 1);
                Classify(land, water, wet, wb, a + 1, c, c + 1);
            }
            // Новый меш, а не Clear() старого: RTAS держит BLAS по объекту Mesh и правку вершин на месте не видит —
            // RT-тени падали от рельефа прежнего места патча (замер 03.10.2026, Луна: чёрные зоны с прямыми краями
            // при честном горизонте 1,5° против Солнца 8,2°; с DynamicGeometry — чисто). Перестройка редкая, так дешевле.
            var oldMesh = patchMf.sharedMesh;
            var mesh = new Mesh { name = "Patch", indexFormat = IndexFormat.UInt32 };
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
            patchMf.sharedMesh = mesh;
            if (oldMesh != null) Destroy(oldMesh);
            patchMr.sharedMaterials = ocean ? new[] { e.PatchMat, waterMat } : new[] { e.PatchMat };
            patchTr.gameObject.SetActive(true);
        }

        static float Frac(double x) => (float)(x - System.Math.Floor(x));

        static void Classify(List<int> land, List<int> water, bool[] wet, int wb, int a, int b, int c)
        {
            if (wet == null || !(wet[a] && wet[b] && wet[c])) { land.Add(a); land.Add(b); land.Add(c); }
            if (wet != null && (wet[a] || wet[b] || wet[c])) { water.Add(wb + a); water.Add(wb + b); water.Add(wb + c); }
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
            var heights = relief && b.Terrain != null ? ConservativeHeights(b, seg) : null;
            for (int r = 0; r <= rings; r++)
            {
                double lat = -90 + 180.0 * r / rings;
                for (int s = 0; s <= seg; s++)
                {
                    double lon = -180 + 360.0 * s / seg;
                    var dir = CelestialBody.LatLonToBodyFixed(lat, lon);
                    double h = heights != null ? heights[r * cols + s] : 0;
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
        /// Высоты вершин дальней сферы «снизу»: грань сферы — плоский треугольник между вершинами, и если
        /// между ними рельеф проседает (котлован площадки космодрома ≈ 9 км против грани ≈ 104 км), грань
        /// вылезает над патчем — на 12 км над Байконуром было +128 м, и сквозь этот слой пролетали.
        /// Поэтому: (1) рельеф берётся на сетке вдвое гуще и вершина получает минимум по своей
        /// окрестности в полграни; (2) вершины рядом с космодромом не выше его отметки.
        /// </summary>
        static double[] ConservativeHeights(CelestialBody b, int seg)
        {
            int rings = seg / 2, cols = seg + 1;
            int fr = rings * 2, fs = seg * 2, fcols = fs + 1;
            var fine = new double[fcols * (fr + 1)];
            // Строки независимы, рельеф — чистая функция: параллельно (при 768 — 2,4 млн отсчётов, в один поток 1,5 с).
            System.Threading.Tasks.Parallel.For(0, fr + 1, r =>
            {
                double lat = -90 + 180.0 * r / fr;
                for (int s = 0; s <= fs; s++)
                    fine[r * fcols + s] = b.SurfaceHeight(CelestialBody.LatLonToBodyFixed(lat, -180 + 360.0 * s / fs));
            });
            // Радиус влияния площадки на вершину: зона выравнивания плюс диагональ грани на экваторе.
            double faceDiag = b.Radius * (2 * System.Math.PI / seg) * System.Math.Sqrt(2);
            var h = new double[cols * (rings + 1)];
            for (int r = 0; r <= rings; r++)
            for (int s = 0; s <= seg; s++)
            {
                double m = double.MaxValue;
                for (int dr = -1; dr <= 1; dr++)
                for (int ds = -1; ds <= 1; ds++)
                {
                    int rr = 2 * r + dr, ss = 2 * s + ds;
                    if (rr < 0 || rr > fr) continue;
                    if (ss < 0) ss += fs; else if (ss > fs) ss -= fs; // шов долготы ±180
                    m = System.Math.Min(m, fine[rr * fcols + ss]);
                }
                var dir = CelestialBody.LatLonToBodyFixed(-90 + 180.0 * r / rings, -180 + 360.0 * s / seg);
                foreach (var site in Kare.Space.Core.Terrain.Sites)
                {
                    if (site.BodyId != b.Id) continue;
                    if (Vector3d.Angle(dir, site.DirectionBodyFixed) * b.Radius < site.BlendRadius + faceDiag)
                        m = System.Math.Min(m, site.Elevation);
                }
                h[r * cols + s] = m;
            }
            return h;
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
                // По субмешам: присваивание mesh.triangles склеило бы сушу и воду в одну субмеш.
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var st = mesh.GetTriangles(s);
                    for (int k = 0; k < st.Length; k += 3) (st[k + 1], st[k + 2]) = (st[k + 2], st[k + 1]);
                    mesh.SetTriangles(st, s);
                }
                return;
            }
        }

        /// <summary>
        /// Ночные огни (§9.4, этап 1): эмиссия в нитах на суше вне льдов, гуще в умеренных широтах севера,
        /// пятнами по шуму + точки-города. Днём (EV 12+) их не видно, ночью (EV −5) — видно; экспозиция
        /// решает сама, переключать по терминатору не нужно.
        /// </summary>
        static Texture2D BuildNightLights(CelestialBody b, int w)
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
            tex.Apply(true, false); // читаемой: TextureCache сожмёт и выгрузит
            return tex;
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
            tex.Apply(true, false);
            mask = null;
            if (earth)
            {
                mask = new Texture2D(w, h, TextureFormat.RGBA32, true, true) { name = $"Mask {b.Id}", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };
                mask.SetPixels32(mk);
                mask.Apply(true, false);
            }
            return tex;
        }

        /// <summary>Билинейная выборка равнопромежуточной карты: строка 0 — юг, столбец 0 — долгота −180°, как в BuildTexture.</summary>
        static Color SampleMap(Color32[] px, int w, int h, double lat, double lon)
        {
            double fx = (lon + 180) / 360 * w - 0.5, fy = (lat + 90) / 180 * h - 0.5;
            int x0 = (int)System.Math.Floor(fx), y0 = (int)System.Math.Floor(fy);
            float tx = (float)(fx - x0), ty = (float)(fy - y0);
            Color P(int x, int y) => px[Mathf.Clamp(y, 0, h - 1) * w + ((x % w) + w) % w];
            return Color.Lerp(Color.Lerp(P(x0, y0), P(x0 + 1, y0), tx), Color.Lerp(P(x0, y0 + 1), P(x0 + 1, y0 + 1), tx), ty);
        }

        /// <summary>Цвет тела в точке — им же рисуется сфера (BuildTexture) и тонируется грунт патча.</summary>
        static Color SurfaceColor(CelestialBody b, BodyLook look, double lat, double lon)
        {
            bool map = smallPx.TryGetValue(b.Id, out var sm);
            if (b.Id == "earth")
            {
                // Суша — по снимку (его же видно на сфере), вода — своя: патч красит океан по глубине (OceanColor).
                var c0 = EarthSurface.Sample(b, lat, lon, out _);
                if (!map || b.SurfaceHeight(CelestialBody.LatLonToBodyFixed(lat, lon)) <= 0) return c0;
                return SampleMap(sm.Px, sm.W, sm.H, lat, lon);
            }
            if (map) return SampleMap(sm.Px, sm.W, sm.H, lat, lon);
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
