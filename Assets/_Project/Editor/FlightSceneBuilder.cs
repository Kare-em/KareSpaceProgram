using Kare.Space.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.EditorTools
{
    /// <summary>
    /// Автосборка сцены Flight (docs/SCENE_SETUP.md §Б, повторяет §А кодом). Идемпотентна:
    /// сцена создаётся заново, профиль и материалы переиспользуются и перезаписываются значениями отсюда.
    /// </summary>
    public static class FlightSceneBuilder
    {
        const string ScenePath = "Assets/_Project/Scenes/Flight.unity";
        const string SettingsDir = "Assets/_Project/Settings";
        const string ProfilePath = SettingsDir + "/FlightVolume.asset";
        const string PipelinePath = SettingsDir + "/HDRP.asset";
        /// <summary>Текстуры из атласов генератора (Tools/gen-texture.mjs → Tools/slice-atlas.py).</summary>
        const string GroundDir = "Assets/_Project/Textures/Ground";
        const string HudIconsPath = "Assets/_Project/Textures/UI/HudIcons.png";

        /// <summary>Пара: SunLight.IlluminanceAt1Au — стартовое значение до первого кадра.</summary>
        const float SunLux = 127000;
        const float SunTemperature = SunLight.ColorTemperature;
        /// <summary>Угловой диаметр Солнца с 1 а.е., градусы (§9.3: мягкость тени).</summary>
        const float SunAngularDiameter = 0.53f;
        /// <summary>Пара: дальность теней ↔ размер борта/патча рельефа вблизи камеры.</summary>
        const float ShadowDistance = 2000;

        [MenuItem("Kare/Build Flight Scene")]
        public static void Build()
        {
            System.IO.Directory.CreateDirectory(SettingsDir);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ScenePath));

            EnsurePipeline();
            var profile = BuildProfile();
            var bodyMat = LitMaterial(SettingsDir + "/BodyLit.mat", Color.white, 0.15f);
            var vesselMat = LitMaterial(SettingsDir + "/VesselLit.mat", Color.white, 0.45f);
            vesselMat.SetFloat("_Metallic", 0.3f);
            HullTexturing(vesselMat);
            var plumeMat = PlumeMaterial(SettingsDir + "/Plume.mat");
            var smokeMat = SmokeMaterial(SettingsDir + "/Smoke.mat");
            var lineMat = UnlitMaterial(SettingsDir + "/MapLine.mat");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var volGo = new GameObject("Global Volume");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = profile;
            volGo.AddComponent<SkyController>().Volume = vol;

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            var hdSun = sunGo.AddComponent<HDAdditionalLightData>();
            sun.lightUnit = LightUnit.Lux;
            sun.intensity = SunLux;
            sun.useColorTemperature = true;
            sun.colorTemperature = SunTemperature;
            sun.shadows = LightShadows.Soft;
            hdSun.angularDiameter = SunAngularDiameter;
            sunGo.AddComponent<SunLight>();

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<HDAdditionalCameraData>();
            camGo.AddComponent<AudioListener>();
            cam.nearClipPlane = FlightCamera.NearClip;
            cam.farClipPlane = FlightCamera.FarClip;
            cam.fieldOfView = 60;
            camGo.AddComponent<FlightCamera>();

            var game = new GameObject("Game");
            var boot = game.AddComponent<GameBootstrap>();
            boot.Camera = cam;
            boot.Sun = sun;
            boot.Volume = vol;
            boot.VesselMaterial = vesselMat;
            boot.PlumeMaterial = plumeMat;
            boot.SmokeMaterial = smokeMat;
            boot.EarthLand = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/_Project/Data/EarthLand.bytes");
            boot.PadTexture = GroundTexture("Concrete", false);
            // Лоу-поли детали из Blender (Tools/blender); нет файла — вид берёт процедурный меш.
            boot.CapsuleMesh = ModelMesh("Vostok_Capsule");
            boot.EngineMesh = ModelMesh("RD107_Engine");
            boot.LegMesh = ModelMesh("Lander_Leg");
            boot.TrussMesh = ModelMesh("Pad_Truss_Arm");
            game.AddComponent<FloatingOrigin>();
            game.AddComponent<FlightInput>();
            game.AddComponent<FlightHud>().Icons = IconTexture(HudIconsPath);
            var map = game.AddComponent<MapView>();
            map.Camera = cam;
            map.LineMaterial = lineMat;

            var bodies = new GameObject("Bodies");
            var bodyRenderer = bodies.AddComponent<BodyRenderer>();
            bodyRenderer.BaseMaterial = bodyMat;
            // Грунт из паков ADG (§2.8): ground12 — сухая степь Байконура, ground13 — красный камень Марса.
            // Пака нет — откат на свои тайлы, сцена собирается и без него.
            bodyRenderer.EarthGround = PackTexture(Ground12 + "_Diffuse.tga", true, false) ?? GroundTexture("SteppeDetail", true);
            bodyRenderer.EarthMacro = GroundTexture("SteppeMacro", true);
            bodyRenderer.MoonGround = GroundTexture("Regolith", true);
            bodyRenderer.MarsGround = PackTexture(Ground13 + "_Diffuse.tga", true, false) ?? GroundTexture("MarsSoil", true);
            bodyRenderer.EarthGroundNormal = PackTexture(Ground12 + "_Normal.tga", false, true) ?? NormalTexture("GroundNormal");
            bodyRenderer.WaterNormal = NormalTexture("WaterNormal");

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddToBuildSettings(ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Kare] Сцена Flight собрана: {ScenePath}");
        }

        /// <summary>
        /// Пакет HDRP стоит, но без ассета пайплайна проект рендерит встроенным конвейером —
        /// все HDRP/Lit становятся пурпурными. Ассет — по умолчанию в Graphics, уровни качества не переопределяют.
        /// </summary>
        static void EnsurePipeline()
        {
            var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(PipelinePath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                AssetDatabase.CreateAsset(asset, PipelinePath);
            }
            var s = asset.currentPlatformRenderPipelineSettings;
            s.supportSSAO = true;
            asset.currentPlatformRenderPipelineSettings = s;
            EditorUtility.SetDirty(asset);
            if (GraphicsSettings.defaultRenderPipeline != asset) GraphicsSettings.defaultRenderPipeline = asset;
            if (QualitySettings.renderPipeline != null) QualitySettings.renderPipeline = null;
            // HDRP в Gamma не рендерит вовсе (ошибка в консоли каждый кадр).
            if (PlayerSettings.colorSpace != ColorSpace.Linear) PlayerSettings.colorSpace = ColorSpace.Linear;
            SetHighLightmapEncoding();
        }

        /// <summary>
        /// HDRP Wizard требует High Quality кодирование лайтмапов. Публичного сеттера нет —
        /// внутренний PlayerSettings.SetLightmapEncodingQualityForPlatform (тот же путь в Car_Train).
        /// </summary>
        static void SetHighLightmapEncoding()
        {
            var method = typeof(PlayerSettings).GetMethod("SetLightmapEncodingQualityForPlatform",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (method == null) { Debug.LogWarning("[Kare] Нет SetLightmapEncodingQualityForPlatform — выставь High вручную."); return; }
            var high = System.Enum.Parse(method.GetParameters()[1].ParameterType, "High");
            foreach (var t in new[] { BuildTarget.StandaloneWindows64, BuildTarget.StandaloneWindows,
                                      BuildTarget.StandaloneOSX, BuildTarget.StandaloneLinux64 })
                method.Invoke(null, new object[] { t, high });
        }

        static VolumeProfile BuildProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }

            var env = Get<VisualEnvironment>(profile);
            env.skyType.Override((int)SkyType.PhysicallyBased);
            env.skyAmbientMode.Override(SkyAmbientMode.Dynamic);

            var sky = Get<PhysicallyBasedSky>(profile);
            sky.type.Override(PhysicallyBasedSkyModel.EarthAdvanced);
            env.planetRadius.Override(6378.137f); // км; рантайм перезаписывает из BodyRenderer (§9.2)
            // Звёзды и Млечный Путь в нитах (§9.2): множитель 1, видимость решает экспозиция §9.3.
            sky.spaceEmissionTexture.Override(StarFieldBaker.Ensure());
            sky.spaceEmissionMultiplier.Override(1);

            var exp = Get<Exposure>(profile);
            exp.mode.Override(ExposureMode.AutomaticHistogram);
            exp.limitMin.Override(SkyController.EvMin);
            exp.limitMax.Override(SkyController.EvMax);
            exp.adaptationSpeedDarkToLight.Override(0.5f);
            exp.adaptationSpeedLightToDark.Override(1.5f);

            Get<Tonemapping>(profile).mode.Override(TonemappingMode.ACES);
            Get<Bloom>(profile).intensity.Override(0.2f);
            Get<Bloom>(profile).scatter.Override(SkyController.BloomScatter);
            Get<Fog>(profile).enabled.Override(false);

            var sh = Get<HDShadowSettings>(profile);
            sh.maxShadowDistance.Override(ShadowDistance);
            sh.cascadeShadowSplitCount.Override(4);

            EditorUtility.SetDirty(profile);
            return profile;
        }

        static T Get<T>(VolumeProfile p) where T : VolumeComponent
        {
            if (!p.TryGet(out T c))
            {
                c = p.Add<T>(false);
                c.name = typeof(T).Name;
                AssetDatabase.AddObjectToAsset(c, p);
            }
            return c;
        }

        static Material LoadOrCreate(string path, string shader)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find(shader));
                AssetDatabase.CreateAsset(m, path);
            }
            return m;
        }

        static Material LitMaterial(string path, Color c, float smooth)
        {
            var m = LoadOrCreate(path, "HDRP/Lit");
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Первый меш FBX из Models; null — файла нет (детали не обязательны).</summary>
        static Mesh ModelMesh(string name)
        {
            return AssetDatabase.LoadAssetAtPath<Mesh>("Assets/_Project/Models/" + name + ".fbx");
        }

        /// <summary>Дым (§9.5): Lit, прозрачный, альфа-смешение, двусторонний — освещён Солнцем, ночью тёмный.
        /// Туман оставлен: дальний шлейф должен тонуть в дымке, как и грунт. Текстуру и цвет ставит ExhaustTrail
        /// на рантайм-копии.</summary>
        static Material SmokeMaterial(string path)
        {
            var m = LoadOrCreate(path, "HDRP/Lit");
            m.SetFloat("_SurfaceType", 1);   // Transparent
            m.SetFloat("_BlendMode", 0);     // Alpha
            m.SetFloat("_DoubleSidedEnable", 1);
            m.SetFloat("_TransparentZWrite", 0);
            m.SetFloat("_Smoothness", 0);
            m.SetFloat("_Metallic", 0);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Факел (§9.5): Unlit, прозрачный, аддитивный, двусторонний, без записи глубины.
        /// Цвет в нитах и градиент по длине ставит VesselView через MaterialPropertyBlock.</summary>
        static Material PlumeMaterial(string path)
        {
            var m = LoadOrCreate(path, "HDRP/Unlit");
            m.shader = Shader.Find("HDRP/Unlit"); // старый Plume.mat был на HDRP/Lit
            m.SetFloat("_SurfaceType", 1);   // Transparent
            m.SetFloat("_BlendMode", 1);     // Additive
            m.SetFloat("_DoubleSidedEnable", 1);
            m.SetFloat("_TransparentZWrite", 0);
            // Цвет Unlit идёт мимо экспозиции — светим только эмиссией (её HDRP экспонирует).
            m.SetColor("_UnlitColor", Color.black);
            m.SetColor("_EmissiveColor", Color.white);
            // Туман на прозрачных добавлял бы рассеяние неба в каждый аддитивный слой — голубой налёт.
            m.SetFloat("_EnableFogOnTransparent", 0);
            HDMaterial.ValidateMaterial(m);
            // Ключ на ассете не держится (Validate снимает его без текстуры в материале) — VesselView
            // делает рантайм-копию с градиентом и ключом. Здесь — чтобы вариант шейдера попал в сборку.
            m.EnableKeyword("_EMISSIVE_COLOR_MAP");
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Тайл грунта: повтор, анизотропия, мипы. readable — BodyRenderer считает средний цвет
        /// и строит детальную карту; без сжатия, чтобы GetPixels работал на любом формате.</summary>
        static Texture2D GroundTexture(string name, bool readable)
        {
            string path = $"{GroundDir}/{name}.png";
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = true;
                ti.mipmapEnabled = true;
                ti.wrapMode = TextureWrapMode.Repeat;
                ti.anisoLevel = 8;
                ti.maxTextureSize = 1024;
                ti.isReadable = readable;
                ti.textureCompression = readable ? TextureImporterCompression.Uncompressed : TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        const string Ground12 = "Assets/ADG_Textures/ground_vol1/ground12/ground12";
        const string Ground13 = "Assets/ADG_Textures/ground_vol1/ground13/ground13";
        const string HullMetal = "Assets/Realistic Metal Texture/Texture/Metal_30/Metal_30";
        /// <summary>Тайл обшивки в метрах (§9.5): панели Metal_30 ≈ 2 м — на баке Ø 3 м видно 4–5 полос по кругу.</summary>
        const float HullTileMeters = 2f;

        /// <summary>Текстура из стороннего пака по пути: повтор, мипы, 1024. readable — для GetPixels в BodyRenderer
        /// (тогда без сжатия: 4 МБ на 1024²). null, если пака нет.</summary>
        static Texture2D PackTexture(string path, bool readable, bool normal)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) return null;
            ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (!normal) ti.sRGBTexture = true;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.anisoLevel = 8;
            ti.maxTextureSize = 1024;
            ti.isReadable = readable;
            ti.textureCompression = readable ? TextureImporterCompression.Uncompressed : TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>
        /// Обшивка бортов и стола (§9.5): Metal_30 с нормалью трипланаром в пространстве объекта. У процедурных
        /// Frustum/Bell нет UV, а трипланар их не требует; объектное пространство — чтобы рисунок не «плыл»
        /// при сдвиге плавающего начала. Цвет ступеней по-прежнему даёт _BaseColor из MPB — он умножается на карту,
        /// поэтому карта почти белая.
        /// </summary>
        static void HullTexturing(Material m)
        {
            var albedo = PackTexture(HullMetal + ".tga", false, false);
            var normal = PackTexture(HullMetal + "_N.tga", false, true);
            if (albedo == null) return;
            m.SetTexture("_BaseColorMap", albedo);
            if (normal != null) { m.SetTexture("_NormalMap", normal); m.SetFloat("_NormalScale", 0.6f); }
            m.SetFloat("_UVBase", 5);                 // Triplanar
            m.SetFloat("_ObjectSpaceUVMapping", 1);  // ObjectSpace
            m.SetFloat("_TexWorldScale", 1f / HullTileMeters);
            HDMaterial.ValidateMaterial(m); // ставит _MAPPING_TRIPLANAR, _NORMALMAP по свойствам
            EditorUtility.SetDirty(m);
        }

        /// <summary>Нормаль-карта тайла (из Car_Train, GL-формат — как ждёт Unity), повтор и мипы.</summary>
        static Texture2D NormalTexture(string name)
        {
            string path = $"{GroundDir}/{name}.png";
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.mipmapEnabled = true;
                ti.wrapMode = TextureWrapMode.Repeat;
                ti.anisoLevel = 8;
                ti.maxTextureSize = 1024;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Атлас иконок HUD 4×4: белое по альфе, красится GUI.color; без мипов и повтора.</summary>
        static Texture2D IconTexture(string path)
        {
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.textureType = TextureImporterType.Default;
                ti.alphaIsTransparency = true;
                ti.mipmapEnabled = true;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Material UnlitMaterial(string path)
        {
            var m = LoadOrCreate(path, "HDRP/Unlit");
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static void AddToBuildSettings(string path)
        {
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            list.RemoveAll(s => s.path == path);
            list.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = list.ToArray();
        }
    }
}
