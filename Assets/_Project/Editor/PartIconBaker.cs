using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kare.Space.Core;
using Kare.Space.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace Kare.Space.EditorTools
{
    /// <summary>
    /// Иконки деталей конструктора (§5.4): меню Kare/Bake Part Icons. Каждая деталь каталога — FBX из Models или
    /// процедурная геометрия PartShapes (как в предпросмотре ангара) — снимается одной ортокамерой «изометрии» с одним
    /// светом и вписывается по своим габаритам. Результат: PNG с прозрачным фоном в Textures/Icons/Parts/&lt;id&gt;.png и
    /// Resources/PartIcons.asset (PartIconSet). Перезапускается целиком: новые FBX подхватываются по ModelFile.
    ///
    /// Прозрачность: цветовой буфер HDRP — R11G11B10 без альфы, поэтому кадр снимается дважды — на чёрном и на
    /// пересвеченном фоне; пиксель, который поменялся, — фон. Снимок в Supersample раз крупнее без сглаживания: каждый
    /// отсчёт либо деталь, либо фон, а альфа края — доля отсчётов детали при уменьшении (docs/pitfalls-render.md).
    /// </summary>
    public static class PartIconBaker
    {
        const string OutDir = "Assets/_Project/Textures/Icons/Parts";
        /// <summary>Пара: PartIconSet.ResourceName.</summary>
        public const string SetPath = "Assets/_Project/Resources/" + PartIconSet.ResourceName + ".asset";
        const string ModelsDir = "Assets/_Project/Models/";
        const string MaterialPath = "Assets/_Project/Settings/VesselLit.mat";
        /// <summary>Профиль ангара (градиентное небо для окружающего света, Fixed EV 12, ACES) — только читается.</summary>
        const string VolumePath = "Assets/_Project/Settings/HangarVolume.asset";

        /// <summary>Сторона иконки, px. Пара: HangarController.CatalogIcon = 48 — запас ×2,7 на масштаб UI до 4K.</summary>
        const int IconSize = 128;
        /// <summary>Отсчётов на пиксель по стороне: 4×4 = 16 уровней альфы на краю.</summary>
        const int Supersample = 4;
        /// <summary>Свободный слой (30 и 31 пусты): камера, свет и объём иконок не видят и не светят сцене.</summary>
        const int IconLayer = 31;
        /// <summary>Ракурс: азимут от +X к +Z и возвышение камеры, °. Крыло (нормаль X) видно в плане на cos 35° = 0,82.</summary>
        const float Yaw = 35, Pitch = 20;
        /// <summary>Поле вокруг габарита — доля полуразмера кадра.</summary>
        const float Margin = 0.08f;
        /// <summary>Ключевой и заполняющий свет, лк, при FixedEv 12 профиля ангара. В ангаре 10000 (HangarSceneBuilder.LightLux),
        /// но на тёмной панели UI иконки при 10000/3500 читались серыми, а тёмные детали (переходники 0,12) — пятном.</summary>
        const float KeyLux = 18000, FillLux = 8000;
        /// <summary>Контровой свет сзади-сверху, лк: обводит силуэт тёмных деталей (сопла, переходники) на тёмном фоне UI.</summary>
        const float RimLux = 14000;
        /// <summary>Нижняя граница альбедо на иконке: переходники (0,12) и сопла (0,2) иначе сливаются с панелью UI.
        /// Пара: PartShapes.ColorOf — в ангаре и полёте цвета прежние.</summary>
        const float MinAlbedo = 0.24f;
        /// <summary>Цветовая температура света, К: нейтральнее ангарных 5200, чтобы белые баки не желтели.</summary>
        const float LightKelvin = 6000;
        /// <summary>Фон второго прохода, линейный HDR. ≈4 после экспозиции EV 12 — ACES уводит его в белый, а R11G11B10
        /// (максимум 65 000) не переполняется: при 1e5 был бы inf/NaN.</summary>
        const float WhiteBg = 20000;
        /// <summary>Отсчёт — деталь, если разница проходов меньше этой доли разницы на чистом фоне.</summary>
        const float MatteShare = 0.5f;
        /// <summary>Разница проходов на фоне ниже этого — второй проход не сработал: тёмная подложка без альфы.</summary>
        const float MatteMinDiff = 0.25f;
        /// <summary>Кадров на проход: HDRP досчитывает окружающий свет неба и тени за первые кадры.</summary>
        const int Frames = 3;

        /// <summary>Детали без SectionModel, у которых есть своя модель в Models (имя без .fbx).</summary>
        static readonly Dictionary<string, string> IdModels = new Dictionary<string, string>
        {
            { "cmd-vostok", "Vostok_Capsule" }, { "eng-rd107", "RD107_Engine" }, { "eng-rd0110", "RD0110_Engine" },
            { "pl-sputnik", "Sputnik_PS1" }, { "fins", "Sounding_Fins" }, { "legs", "Lander_Leg" },
        };

        [MenuItem("Kare/Bake Part Icons")]
        public static void BakeMenu() => Debug.Log("[PartIcons] " + Bake());

        /// <summary>Запекает все детали каталога (или одну по id); возвращает сводку для лога/MCP.</summary>
        public static string Bake(string only = null)
        {
            Directory.CreateDirectory(OutDir);
            Directory.CreateDirectory(Path.GetDirectoryName(SetPath));
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumePath);
            if (mat == null) return "нет " + MaterialPath;

            var scene = EditorSceneManager.NewPreviewScene();
            int px = IconSize * Supersample;
            var rt = new RenderTexture(px, px, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            var read = new Texture2D(px, px, TextureFormat.RGBA32, false);
            var report = new StringBuilder();
            var written = new List<(string id, string path)>();
            int plates = 0;
            try
            {
                var cam = Rig(scene, profile, rt, out var hd);
                foreach (var def in PartCatalog.All)
                {
                    if (only != null && def.Id != only) continue;
                    var p = PartCatalog.Resolve(new CraftPart(def.Id)) ?? def;
                    var root = new GameObject("Icon " + p.Id);
                    SceneManager.MoveGameObjectToScene(root, scene);
                    string model = ModelFile(p);
                    if (model != null) AddModel(root.transform, model, p, mat);
                    else AddShapes(root.transform, p, mat);
                    SetLayer(root.transform);
                    if (p.Wing != null) FaceCamera(root.transform, p.Wing.Vertical);

                    var b = Bounds(root);
                    Frame(cam, b);
                    var dark = Pass(cam, hd, rt, read, Color.black);
                    var bright = Pass(cam, hd, rt, read, new Color(WhiteBg, WhiteBg, WhiteBg));
                    Object.DestroyImmediate(root);

                    var icon = Matte(dark, bright, px, out float cover, out bool plate);
                    if (plate) plates++;
                    string path = $"{OutDir}/{p.Id}.png";
                    File.WriteAllBytes(path, icon.EncodeToPNG());
                    Object.DestroyImmediate(icon);
                    written.Add((p.Id, path));
                    report.Append($"{p.Id}{(model != null ? "[" + model + "]" : "")} {cover * 100:0}%; ");
                }
                Object.DestroyImmediate(cam.gameObject);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(read);
            }

            foreach (var (_, path) in written) Import(path);
            var set = AssetDatabase.LoadAssetAtPath<PartIconSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<PartIconSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }
            // Полный прогон переписывает набор (детали, ушедшие из каталога, выпадают); по одной — дописывает.
            var map = new Dictionary<string, Texture2D>();
            if (only != null) for (int i = 0; i < set.Ids.Length && i < set.Icons.Length; i++) map[set.Ids[i]] = set.Icons[i];
            foreach (var (id, path) in written) map[id] = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            set.Ids = map.Keys.ToArray();
            set.Icons = set.Ids.Select(id => map[id]).ToArray();
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            PartIconSet.Reload();
            return $"иконок {written.Count}, без альфы (подложка) {plates}: {report}";
        }

        // ---------------------------------------------------------------- сцена

        static Camera Rig(Scene scene, VolumeProfile profile, RenderTexture rt, out HDAdditionalCameraData hd)
        {
            var camGo = new GameObject("Icon Camera");
            SceneManager.MoveGameObjectToScene(camGo, scene);
            var cam = camGo.AddComponent<Camera>();
            hd = camGo.AddComponent<HDAdditionalCameraData>();
            cam.scene = scene;
            cam.enabled = false;
            cam.orthographic = true;
            cam.cullingMask = 1 << IconLayer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.allowMSAA = false;
            cam.allowHDR = true;
            cam.targetTexture = rt;
            hd.volumeLayerMask = 1 << IconLayer;
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            // Без сглаживания и апскейла: отсчёт должен быть строго «деталь/фон», сглаживает уменьшение.
            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
            hd.allowDynamicResolution = false;
            hd.allowDeepLearningSuperSampling = false;
            // Всё, что тащит фон на деталь или шумит от кадра к кадру, — выключено, иначе два прохода разойдутся на детали.
            hd.customRenderingSettings = true;
            var mask = hd.renderingPathCustomFrameSettingsOverrideMask;
            var fs = hd.renderingPathCustomFrameSettings;
            foreach (var f in new[]
            {
                FrameSettingsField.Bloom, FrameSettingsField.Vignette, FrameSettingsField.ChromaticAberration,
                FrameSettingsField.FilmGrain, FrameSettingsField.Dithering, FrameSettingsField.MotionBlur,
                FrameSettingsField.DepthOfField, FrameSettingsField.LensDistortion, FrameSettingsField.PaniniProjection,
                FrameSettingsField.SSR, FrameSettingsField.TransparentSSR, FrameSettingsField.Volumetrics,
                FrameSettingsField.AtmosphericScattering, FrameSettingsField.RayTracing, FrameSettingsField.LensFlareDataDriven,
            })
            {
                mask.mask[(uint)f] = true;
                fs.SetEnabled(f, false);
            }
            hd.renderingPathCustomFrameSettingsOverrideMask = mask;
            hd.renderingPathCustomFrameSettings = fs;

            if (profile != null)
            {
                var volGo = new GameObject("Icon Volume") { layer = IconLayer };
                SceneManager.MoveGameObjectToScene(volGo, scene);
                var vol = volGo.AddComponent<Volume>();
                vol.isGlobal = true;
                vol.sharedProfile = profile;
            }

            var view = Dir(Yaw, Pitch);
            // Ключ сверху-слева от камеры, заливка снизу-справа: объём читается, тени не чернят половину детали.
            Light(scene, "Icon Key", Dir(Yaw + 40, 50), KeyLux, true);
            Light(scene, "Icon Fill", Dir(Yaw - 80, 10), FillLux, false);
            Light(scene, "Icon Rim", Dir(Yaw + 160, 35), RimLux, false);
            camGo.transform.rotation = Quaternion.LookRotation(-view, Vector3.up);
            return cam;
        }

        static void Light(Scene scene, string name, Vector3 from, float lux, bool shadows)
        {
            var go = new GameObject(name) { layer = IconLayer };
            SceneManager.MoveGameObjectToScene(go, scene);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            go.AddComponent<HDAdditionalLightData>();
            light.lightUnit = LightUnit.Lux;
            light.intensity = lux;
            light.useColorTemperature = true;
            light.colorTemperature = LightKelvin;
            light.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            light.cullingMask = 1 << IconLayer;
            go.transform.rotation = Quaternion.LookRotation(-from, Vector3.up);
        }

        /// <summary>Направление «от объекта» по азимуту от +X к +Z и возвышению, °.</summary>
        static Vector3 Dir(float yaw, float pitch)
        {
            float y = yaw * Mathf.Deg2Rad, p = pitch * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Sin(p), Mathf.Cos(p) * Mathf.Sin(y));
        }

        static void Frame(Camera cam, Bounds b)
        {
            var t = cam.transform;
            float halfW = 0, halfH = 0, depth = 0;
            for (int i = 0; i < 8; i++)
            {
                var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) * 2 - 1, (i >> 1 & 1) * 2 - 1, (i >> 2 & 1) * 2 - 1)) - b.center;
                halfW = Mathf.Max(halfW, Mathf.Abs(Vector3.Dot(c, t.right)));
                halfH = Mathf.Max(halfH, Mathf.Abs(Vector3.Dot(c, t.up)));
                depth = Mathf.Max(depth, Mathf.Abs(Vector3.Dot(c, t.forward)));
            }
            cam.orthographicSize = Mathf.Max(halfW, halfH, 0.01f) * (1 + Margin);
            t.position = b.center - t.forward * (depth + 1);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 2 * depth + 2;
        }

        static Bounds Bounds(GameObject root)
        {
            var rs = root.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(Vector3.zero, Vector3.one);
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        static Color32[] Pass(Camera cam, HDAdditionalCameraData hd, RenderTexture rt, Texture2D read, Color bg)
        {
            hd.backgroundColorHDR = bg;
            for (int i = 0; i < Frames; i++) cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            read.Apply(false);
            RenderTexture.active = prev;
            return read.GetPixels32();
        }

        /// <summary>
        /// Альфа из двух проходов: отсчёт — деталь, если на чёрном и белом фоне он почти одинаков. Цвет — среднее
        /// отсчётов детали чёрного прохода (не премультиплицирован), альфа — их доля в пикселе.
        /// </summary>
        static Texture2D Matte(Color32[] dark, Color32[] bright, int px, out float cover, out bool plate)
        {
            // Разница на чистом фоне — по углу кадра (поле Margin там всегда пустое).
            int bg = Diff(dark[0], bright[0]);
            plate = bg < MatteMinDiff * 255;
            float limit = bg * MatteShare;
            var outPx = new Color32[IconSize * IconSize];
            int s = Supersample, n2 = s * s;
            long covered = 0;
            for (int y = 0; y < IconSize; y++)
                for (int x = 0; x < IconSize; x++)
                {
                    int r = 0, g = 0, bl = 0, n = 0;
                    for (int j = 0; j < s; j++)
                        for (int i = 0; i < s; i++)
                        {
                            int k = (y * s + j) * px + x * s + i;
                            if (!plate && Diff(dark[k], bright[k]) >= limit) continue;
                            r += dark[k].r; g += dark[k].g; bl += dark[k].b; n++;
                        }
                    covered += plate ? 0 : n;
                    outPx[y * IconSize + x] = n == 0 ? new Color32(0, 0, 0, 0)
                        : new Color32((byte)(r / n), (byte)(g / n), (byte)(bl / n), (byte)(255 * n / n2));
                }
            cover = (float)covered / (px * px);
            var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
            tex.SetPixels32(outPx);
            tex.Apply(false);
            return tex;
        }

        static int Diff(Color32 a, Color32 b) => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b));

        // ---------------------------------------------------------------- геометрия

        /// <summary>
        /// Модель детали в Models: Part_&lt;id&gt;.fbx (договорённость для новых FBX — подхватится без правки кода),
        /// затем IdModels, затем FBX аппарата по SectionModel (FlightSceneBuilder.CraftFiles), для шасси — любой *Gear*.fbx.
        /// </summary>
        static string ModelFile(PartDef p)
        {
            if (Exists("Part_" + p.Id)) return "Part_" + p.Id;
            if (IdModels.TryGetValue(p.Id, out var f) && Exists(f)) return f;
            if (p.Model != SectionModel.None)
                foreach (var (m, file) in FlightSceneBuilder.CraftFiles)
                    if (m == p.Model && Exists(file)) return file;
            if (p.GearHeight > 0 && Directory.Exists(ModelsDir))
            {
                var g = Directory.GetFiles(ModelsDir, "*Gear*.fbx").OrderBy(x => x).FirstOrDefault();
                if (g != null) return Path.GetFileNameWithoutExtension(g);
            }
            return null;
        }

        static bool Exists(string name) => File.Exists(ModelsDir + name + ".fbx");

        static void AddModel(Transform root, string file, PartDef p, Material mat)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelsDir + file + ".fbx");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.gameObject.scene);
            go.transform.SetParent(root, false);
            var hull = PartShapes.ColorOf(p);
            var mpb = new MaterialPropertyBlock();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                // Слоты FBX — отдельные субмеши: материал корпуса на каждый, цвет — по имени материала Blender.
                var src = r.sharedMaterials;
                var mats = new Material[src.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
                for (int i = 0; i < src.Length; i++)
                {
                    mpb.Clear();
                    mpb.SetColor("_BaseColor", Lift(SlotColor(src[i] != null ? src[i].name : "", hull)));
                    r.SetPropertyBlock(mpb, i);
                }
            }
        }

        /// <summary>Цвет слота по имени материала из parts.blend (Hull/Metal/Foil/Black/Nozzle/Shield…), как палитры VesselView.</summary>
        static Color SlotColor(string name, Color hull)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("shield") || n.Contains("ablat")) return new Color(0.32f, 0.24f, 0.17f);
            if (n.Contains("nozzle")) return PartShapes.NozzleColor;
            if (n.Contains("black")) return PartShapes.DarkColor;
            if (n.Contains("foil") || n.Contains("gold")) return new Color(0.8f, 0.6f, 0.25f);
            if (n.Contains("panel") || n.Contains("solar")) return PartShapes.PanelColor;
            if (n.Contains("polish") || n.Contains("steel") || n.Contains("chrome")) return new Color(0.8f, 0.8f, 0.82f);
            if (n.Contains("metal")) return PartShapes.MetalColor;
            if (n.Contains("white")) return new Color(0.9f, 0.9f, 0.88f);
            return hull;
        }

        static void AddShapes(Transform root, PartDef p, Material mat)
        {
            var mpb = new MaterialPropertyBlock();
            PartShapes.Icon(p, (name, mesh, col, pos, rot, scale) =>
            {
                var go = new GameObject(name);
                go.transform.SetParent(root, false);
                go.transform.localPosition = pos;
                go.transform.localRotation = rot;
                go.transform.localScale = scale;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                mpb.Clear();
                mpb.SetColor("_BaseColor", Lift(col));
                r.SetPropertyBlock(mpb);
            });
        }

        /// <summary>
        /// Плоскость крыла — к камере в плане (в ракурсе изометрии она видна почти с ребра). Киль сначала ставится
        /// размахом вверх (в осях секции он растёт к −X), хордой вбок — как хвостовое оперение на силуэте.
        /// </summary>
        static void FaceCamera(Transform root, bool vertical)
        {
            var stand = vertical ? Quaternion.Euler(0, 0, -90) : Quaternion.identity;
            var normal = vertical ? Vector3.forward : Vector3.right;
            root.rotation = Quaternion.FromToRotation(normal, Dir(Yaw, Pitch)) * stand;
        }

        static Color Lift(Color c)
        {
            float m = Mathf.Max(c.r, c.g, c.b);
            return m >= MinAlbedo ? c : Color.Lerp(c, new Color(MinAlbedo, MinAlbedo, MinAlbedo), 1 - m / MinAlbedo);
        }

        static void SetLayer(Transform t)
        {
            t.gameObject.layer = IconLayer;
            foreach (Transform c in t) SetLayer(c);
        }

        static void Import(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            bool changed = ti.textureType != TextureImporterType.Default || !ti.alphaIsTransparency || ti.mipmapEnabled
                || ti.wrapMode != TextureWrapMode.Clamp || ti.textureCompression != TextureImporterCompression.CompressedHQ;
            if (!changed) return;
            ti.textureType = TextureImporterType.Default;
            ti.alphaIsTransparency = true;   // без этого билинейка тянет чёрное с прозрачных соседей в кромку
            ti.mipmapEnabled = false;        // иконка рисуется ~1:2,5 — мипы только мылят
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
        }
    }
}
