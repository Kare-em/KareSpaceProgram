using System.Collections.Generic;
using Kare.Space.Core;
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
        /// <summary>Порог отбора в RTAS, градусы телесного угла (как в Car_Train). Пара: борт 40 м виден под 4° с ≈ 600 м —
        /// дальше его отражение и RT-тень всё равно в пиксель; ближний патч рельефа под камерой всегда крупнее.</summary>
        const float RtasMinSolidAngle = 4;

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
            camGo.AddComponent<RenderQuality>();

            var game = new GameObject("Game");
            var boot = game.AddComponent<GameBootstrap>();
            boot.Camera = cam;
            boot.Sun = sun;
            boot.Volume = vol;
            boot.VesselMaterial = vesselMat;
            boot.PlumeMaterial = plumeMat;
            boot.SmokeMaterial = smokeMat;
            // Пак Vefects Free Fire HDRP (Asset Store); нет пака — поля пустые, горения нет.
            boot.WreckFirePrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Vefects/Free Fire HDRP/Particles/VFX_Fire_Floor_01_Smoke.prefab");
            boot.DebrisFirePrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Vefects/Free Fire HDRP/Particles/VFX_Fire_01_Small_Smoke.prefab");
            // Пак JMO WarFX: шейдеры built-in — сначала перевести материалы на HDRP (идемпотентно).
            if (AssetDatabase.IsValidFolder("Assets/JMO Assets/WarFX")) WarFxConverter.Convert();
            boot.BlastPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/JMO Assets/WarFX/_Effects/Explosions/WFX_Explosion.prefab");
            boot.BigBlastPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/JMO Assets/WarFX/_Effects/Explosions/WFX_Nuke.prefab");
            boot.EarthLand = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/_Project/Data/EarthLand.bytes");
            boot.PadTexture = GroundTexture("Concrete", false);
            // Лоу-поли детали из Blender (Tools/blender); нет файла — вид берёт процедурный меш.
            boot.CapsuleMesh = ModelMesh("Vostok_Capsule");
            boot.EngineMesh = ModelMesh("RD107_Engine");
            boot.LegMesh = ModelMesh("Lander_Leg");
            boot.TrussMesh = ModelMesh("Pad_Truss_Arm");
            boot.SputnikMesh = ModelMesh("Sputnik_PS1");
            boot.VostokServiceMesh = ModelMesh("Vostok_Service");
            boot.Luna9Mesh = ModelMesh("Luna9_Station");
            boot.UpperEngineMesh = ModelMesh("RD0110_Engine");
            boot.InterstageMesh = ModelMesh("Interstage_Truss");
            boot.FairingHalfMesh = ModelMesh("Fairing_Half");
            boot.FinsMesh = ModelMesh("Sounding_Fins");
            var crafts = new List<GameBootstrap.CraftMesh>();
            foreach (var (model, file) in CraftFiles)
            {
                var mesh = ModelMesh(file);
                if (mesh != null) crafts.Add(new GameBootstrap.CraftMesh
                {
                    Model = model, Mesh = mesh, Deploy = DeployParts(model, file, DeployFiles, false),
                    Wheels = DeployParts(model, file, WheelFiles, true),
                });
            }
            boot.CraftMeshes = crafts.ToArray();
            // Стартовые комплексы: Р-7, мачта, Протон, Редстоун, Атлас, Титан, Сатурн (ML + LUT), стрелы башен.
            var pads = new List<GameBootstrap.PadMesh>();
            foreach (var file in PadFiles)
            {
                var mesh = ModelMesh(file);
                if (mesh != null) pads.Add(new GameBootstrap.PadMesh { Name = file, Mesh = mesh });
            }
            boot.PadMeshes = pads.ToArray();
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
            // Карты тел (§9.4): Луна — LRO, остальное — Solar System Scope (CC BY 4.0), Tools/textures/sss_convert.py.
            bodyRenderer.Maps = new[]
            {
                new BodyRenderer.BodyMapSet { Id = "moon", Map = BodyMap("MoonLroc8k.png", 8192, false), Small = BodyMap("MoonLrocSmall.png", 1024, true) },
                new BodyRenderer.BodyMapSet
                {
                    Id = "earth", Map = BodyMap("EarthDay.jpg", 8192, false), Small = BodyMap("EarthSmall.png", 2048, true),
                    Night = BodyMap("EarthNight.jpg", 8192, false), Clouds = BodyMap("EarthClouds.jpg", 8192, false, true),
                },
                new BodyRenderer.BodyMapSet { Id = "mercury", Map = BodyMap("MercuryMap.jpg", 8192, false) },
                new BodyRenderer.BodyMapSet { Id = "venus", Map = BodyMap("VenusMap.jpg", 4096, false) },
                new BodyRenderer.BodyMapSet { Id = "mars", Map = BodyMap("MarsMap.jpg", 8192, false), Small = BodyMap("MarsSmall.png", 1024, true) },
                new BodyRenderer.BodyMapSet { Id = "jupiter", Map = BodyMap("JupiterMap.jpg", 4096, false) },
                new BodyRenderer.BodyMapSet { Id = "saturn", Map = BodyMap("SaturnMap.jpg", 4096, false) },
                new BodyRenderer.BodyMapSet { Id = "uranus", Map = BodyMap("UranusMap.jpg", 2048, false) },
                new BodyRenderer.BodyMapSet { Id = "neptune", Map = BodyMap("NeptuneMap.jpg", 2048, false) },
            };
            bodyRenderer.SaturnRing = RingMap("SaturnRing.png");
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
            // RT и DLSS (тот же набор, что в Car_Train/HdrpSetup): возможности пайплайна, включает их игрок
            // (RenderQuality, меню Esc). Без поддержки в ассете тумблеры ничего не делают.
            s.supportSSR = true;
            s.supportRayTracing = true;
            s.supportedRayTracingMode = RenderPipelineSettings.SupportedRayTracingMode.Both;
            // Тени Солнца лучами идут через экранный буфер теней — слоты под Солнце и ночной свет (NightLight).
            s.hdShadowInitParams.supportScreenSpaceShadows = true;
            s.hdShadowInitParams.maxScreenSpaceShadowSlots = Mathf.Max(4, s.hdShadowInitParams.maxScreenSpaceShadowSlots);
            var drs = s.dynamicResolutionSettings;
            drs.enabled = true;
            drs.dynResType = DynamicResolutionType.Hardware; // D3D12 — без лишней копии кадра
            drs.DLSSUseOptimalSettings = true;
            // Приоритет: DLSS на RTX, иначе STP (апскейлер Unity) — камера разрешает его только флагом DRS.
            drs.advancedUpscalerNames = new System.Collections.Generic.List<string> { "DLSS", "STP" };
            s.dynamicResolutionSettings = drs;
            asset.currentPlatformRenderPipelineSettings = s;
            EditorUtility.SetDirty(asset);
            if (GraphicsSettings.defaultRenderPipeline != asset) GraphicsSettings.defaultRenderPipeline = asset;
            if (QualitySettings.renderPipeline != null) QualitySettings.renderPipeline = null;
            // HDRP в Gamma не рендерит вовсе (ошибка в консоли каждый кадр).
            if (PlayerSettings.colorSpace != ColorSpace.Linear) PlayerSettings.colorSpace = ColorSpace.Linear;
            SetHighLightmapEncoding();
            // Трассировка лучей есть только в D3D12 — ставим его первым явно, а не надеемся на «по умолчанию».
            foreach (var t in new[] { BuildTarget.StandaloneWindows64, BuildTarget.StandaloneWindows })
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(t, false);
                PlayerSettings.SetGraphicsAPIs(t, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
            }
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

            // Отражения: экранные всегда, лучами (Mixed) — когда RT включён (RenderQuality меняет tracing).
            var ssr = Get<ScreenSpaceReflection>(profile);
            ssr.enabled.Override(true);
            ssr.mode.Override(RayTracingMode.Performance);
            ssr.tracing.Override(RayCastingMode.RayMarching);
            // Отбор в ускоряющую структуру по телесному углу: далёкие тела в «оболочке» (BodyRenderer) и мелочь
            // на горизонте в RTAS не попадают — иначе структура пересобирается из гигантских сфер каждый кадр.
            var rts = Get<RayTracingSettings>(profile);
            rts.cullingMode.Override(RTASCullingMode.SolidAngle);
            rts.minSolidAngle.Override(RtasMinSolidAngle);

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

        /// <summary>Аппараты в натуральную величину из Tools/blender/parts.blend → Models/*.fbx.</summary>
        /// <summary>FBX комплексов Models/Pad_*.fbx; имена — ключи GameBootstrap.PadMeshFor (пара: LaunchPadView).</summary>
        static readonly string[] PadFiles =
        {
            "Pad_R7", "Pad_Mast", "Pad_Proton", "Pad_Redstone", "Pad_Atlas", "Pad_Titan",
            "Pad_Saturn_ML", "Pad_Saturn_LUT", "Pad_Arm_Light", "Pad_Arm_Heavy",
        };

        static readonly (SectionModel, string)[] CraftFiles =
        {
            (SectionModel.Lunokhod, "Lunokhod"), (SectionModel.Luna17KT, "Luna17_KT"),
            (SectionModel.LMDescent, "LM_Descent"), (SectionModel.LMAscent, "LM_Ascent"),
            (SectionModel.ApolloCM, "Apollo_CM"), (SectionModel.ApolloSM, "Apollo_SM"),
            (SectionModel.ApolloLES, "Apollo_LES"), (SectionModel.ApolloSLA, "Apollo_SLA_Half"),
            (SectionModel.Mercury, "Mercury_Capsule"), (SectionModel.Gemini, "Gemini_Capsule"),
            (SectionModel.GeminiAdapter, "Gemini_Adapter"), (SectionModel.Surveyor, "Surveyor"),
            (SectionModel.Ranger, "Ranger"), (SectionModel.Explorer1, "Explorer1"),
            (SectionModel.Redstone, "Redstone"), (SectionModel.JunoStage1, "Juno_Stage1"),
            (SectionModel.JunoCluster11, "Juno_Cluster11"), (SectionModel.JunoCluster3, "Juno_Cluster3"),
            (SectionModel.AtlasBooster, "Atlas_Booster"), (SectionModel.AtlasSustainer, "Atlas_Sustainer"),
            (SectionModel.AtlasSustainerAgena, "Atlas_SustainerAgena"), (SectionModel.AtlasSustainerCentaur, "Atlas_SustainerCentaur"),
            (SectionModel.Agena, "Agena"), (SectionModel.Centaur, "Centaur"),
            (SectionModel.TitanStage1, "Titan_Stage1"), (SectionModel.TitanStage2, "Titan_Stage2"),
            (SectionModel.SaturnSIC, "Saturn_SIC"), (SectionModel.SaturnSII, "Saturn_SII"), (SectionModel.SaturnSIVB, "Saturn_SIVB"),
            (SectionModel.ProtonStage1, "Proton_Stage1"), (SectionModel.ProtonStage2, "Proton_Stage2"),
            (SectionModel.ProtonStage3, "Proton_Stage3"), (SectionModel.BlokD, "BlokD"),
        };

        /// <summary>Раскладное (§6.12): опоры и трапы — отдельные FBX, по объекту на опору (Tools/blender, split_deploy).</summary>
        static readonly (SectionModel, string)[] DeployFiles =
        {
            (SectionModel.Surveyor, "Surveyor_Legs"), (SectionModel.LMDescent, "LM_Legs"), (SectionModel.Luna17KT, "Luna17_Ramps"),
            (SectionModel.Lunokhod, "Lunokhod_Lid"),
        };

        /// <summary>Колёса — отдельный FBX, по объекту на колесо (Tools/blender/lunokhod_wheels.py), вид крутит их по пути.</summary>
        static readonly (SectionModel, string)[] WheelFiles = { (SectionModel.Lunokhod, "Lunokhod_Wheels") };

        /// <summary>
        /// Детали раскладного модели: меш, направление наружу (центр нижних вершин — стопа опоры, конец трапа: по
        /// центру масс детали азимут уводят подкосы) и слоты материалов в палитре корпуса — по именам материалов FBX,
        /// у детали слотов меньше, чем у корпуса.
        /// </summary>
        static GameBootstrap.DeployPart[] DeployParts(SectionModel model, string bodyFile, (SectionModel, string)[] files, bool wheels)
        {
            string file = null;
            foreach (var (m, f) in files) if (m == model) file = f;
            if (file == null) return null;
            var body = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Models/" + bodyFile + ".fbx");
            var root = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Models/" + file + ".fbx");
            if (body == null || root == null) return null;
            var bodyMats = new List<string>();
            foreach (var mat in body.GetComponentInChildren<MeshRenderer>().sharedMaterials) bodyMats.Add(mat != null ? mat.name : "");
            var list = new List<GameBootstrap.DeployPart>();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = mf.sharedMesh;
                var mats = mf.GetComponent<MeshRenderer>().sharedMaterials;
                var slots = new int[mats.Length];
                for (int i = 0; i < mats.Length; i++) slots[i] = Mathf.Max(0, bodyMats.IndexOf(mats[i] != null ? mats[i].name : ""));
                if (wheels)
                {
                    list.Add(new GameBootstrap.DeployPart { Mesh = mesh, Dir = mesh.bounds.center, Slots = slots });
                    continue;
                }
                float low = mesh.bounds.min.y + DeployFootBand;
                Vector3 c = Vector3.zero;
                foreach (var p in mesh.vertices) if (p.y < low) c += new Vector3(p.x, 0, p.z);
                if (c.sqrMagnitude < 1e-6f) c = new Vector3(mesh.bounds.center.x, 0, mesh.bounds.center.z);
                list.Add(new GameBootstrap.DeployPart { Mesh = mesh, Dir = c.normalized, Slots = slots });
            }
            return list.ToArray();
        }

        /// <summary>Полоса «стопы» над низом детали, м: тоньше лап опор (0,2 у LM) и толще рельса трапа (0,07).</summary>
        const float DeployFootBand = 0.3f;

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
            // Без «Preserve Specular Lighting»: блик не умножается на альфу, и под факелом 2·10⁶ кд весь квадрат клуба
            // светился целиком с прямыми краями, как бы мягко ни гасла текстура (замер 03.10.2026, ночной старт).
            m.SetFloat("_EnableBlendModePreserveSpecularLighting", 0);
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

        /// <summary>Карты тел (Tools/textures: NASA SVS CGI Moon Kit, Solar System Scope): равнопромежуточные, повтор
        /// по долготе, у полюсов — край. grayAlpha — облака: альфа из яркости, иначе слой был бы сплошным.</summary>
        static Texture2D BodyMap(string name, int size, bool readable, bool grayAlpha = false)
        {
            string path = $"Assets/_Project/Textures/Bodies/{name}";
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = true;
                ti.alphaSource = grayAlpha ? TextureImporterAlphaSource.FromGrayScale : TextureImporterAlphaSource.None;
                ti.alphaIsTransparency = grayAlpha;
                ti.mipmapEnabled = true;
                ti.wrapModeU = TextureWrapMode.Repeat;
                ti.wrapModeV = TextureWrapMode.Clamp;
                ti.anisoLevel = 4;
                ti.maxTextureSize = size;
                ti.isReadable = readable;
                // DXT1, не BC7: 8k в BC7 — 85 МБ видеопамяти против 21 МБ, а разница на сером реголите не видна.
                // Облака с альфой — DXT5 (Compressed сам выбирает его при alphaSource).
                ti.textureCompression = readable ? TextureImporterCompression.Uncompressed : TextureImporterCompression.Compressed;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Полоса колец: x — радиус, края не повторяются (иначе внутренний край C подхватил бы внешний A).</summary>
        static Texture2D RingMap(string name)
        {
            string path = $"Assets/_Project/Textures/Bodies/{name}";
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = true;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = true;
                ti.mipmapEnabled = true;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.npotScale = TextureImporterNPOTScale.None;
                ti.anisoLevel = 8;
                ti.maxTextureSize = 8192;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
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

        /// <summary>Процедурные карты обшивки — Tools/gen-hull-textures.py (швы, заклёпки, стрингеры, подтёки, зерно).</summary>
        const string HullDir = "Assets/_Project/Textures/Hull";

        /// <summary>Карта обшивки без стороннего пака: повтор, мипы, 1024; srgb — только цветовая карта.</summary>
        static Texture2D HullMap(string name, TextureImporterType type, bool srgb)
        {
            string path = $"{HullDir}/{name}.png";
            if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) return null;
            ti.textureType = type;
            ti.sRGBTexture = srgb;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.anisoLevel = 8;
            ti.maxTextureSize = 1024;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>
        /// Обшивка бортов и стола (§9.5): процедурный атлас Hull (albedo + нормаль + Mask Map + Detail Map) трипланаром
        /// в пространстве объекта. У процедурных Frustum/Bell нет UV, а трипланар их не требует; объектное пространство —
        /// чтобы рисунок не «плыл» при сдвиге плавающего начала. Цвет ступеней по-прежнему даёт _BaseColor из MPB —
        /// он умножается на карту, поэтому карта почти белая. Металличность и гладкость берутся из Mask Map (слайдеры
        /// _Metallic/_Smoothness при ней игнорируются), поэтому масштабируются через Remap. Нет карт — старый Metal_30.
        /// </summary>
        static void HullTexturing(Material m)
        {
            var albedo = HullMap("HullAlbedo", TextureImporterType.Default, true);
            var normal = HullMap("HullNormal", TextureImporterType.NormalMap, false);
            var mask = HullMap("HullMask", TextureImporterType.Default, false);
            var detail = HullMap("HullDetail", TextureImporterType.Default, false);
            if (albedo == null)
            {
                albedo = PackTexture(HullMetal + ".tga", false, false);
                normal = PackTexture(HullMetal + "_N.tga", false, true);
                mask = detail = null;
            }
            if (albedo == null) return;
            m.SetTexture("_BaseColorMap", albedo);
            m.SetTexture("_NormalMap", normal);
            m.SetFloat("_NormalScale", 0.8f);
            m.SetTexture("_MaskMap", mask);
            // Пара: HullMask.R (металл ≈ 0,5–0,75) и HullMask.A (гладкость ≈ 0,3–0,6) из gen-hull-textures.py. Покрашенная
            // обшивка — не зеркало: потолок металла 0,45, гладкость 0,15…0,75.
            m.SetFloat("_MetallicRemapMin", 0f); m.SetFloat("_MetallicRemapMax", 0.45f);
            m.SetFloat("_SmoothnessRemapMin", 0.15f); m.SetFloat("_SmoothnessRemapMax", 0.75f);
            m.SetFloat("_AORemapMin", 0f); m.SetFloat("_AORemapMax", 1f);
            m.SetTexture("_DetailMap", detail);
            m.SetFloat("_LinkDetailsWithBase", 1);    // деталь берёт трипланар базы
            m.SetTextureScale("_DetailMap", new Vector2(6, 6)); // зерно в 6 раз мельче швов
            m.SetFloat("_DetailAlbedoScale", 0.6f);
            m.SetFloat("_DetailNormalScale", 0.7f);
            m.SetFloat("_DetailSmoothnessScale", 0.5f);
            m.SetFloat("_UVBase", 5);                 // Triplanar
            m.SetFloat("_ObjectSpaceUVMapping", 1);  // ObjectSpace
            m.SetFloat("_TexWorldScale", 1f / HullTileMeters);
            HDMaterial.ValidateMaterial(m); // ставит _MAPPING_TRIPLANAR, _NORMALMAP, _MASKMAP, _DETAIL_MAP по свойствам
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
