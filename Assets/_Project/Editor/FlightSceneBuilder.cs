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

        /// <summary>Пара: SunLight.IlluminanceAt1Au — стартовое значение до первого кадра.</summary>
        const float SunLux = 127000;
        const float SunTemperature = 5778;
        /// <summary>Угловой диаметр Солнца с 1 а.е., градусы (§9.3: мягкость тени).</summary>
        const float SunAngularDiameter = 0.53f;
        /// <summary>Пара: дальность теней ↔ размер борта/патча рельефа вблизи камеры.</summary>
        const float ShadowDistance = 2000;
        const float PlumeNits = 2e5f;

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
            var plumeMat = PlumeMaterial(SettingsDir + "/Plume.mat");
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
            game.AddComponent<FloatingOrigin>();
            game.AddComponent<FlightInput>();
            game.AddComponent<FlightHud>();
            var map = game.AddComponent<MapView>();
            map.Camera = cam;
            map.LineMaterial = lineMat;

            var bodies = new GameObject("Bodies");
            bodies.AddComponent<BodyRenderer>().BaseMaterial = bodyMat;

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

            var exp = Get<Exposure>(profile);
            exp.mode.Override(ExposureMode.AutomaticHistogram);
            exp.limitMin.Override(SkyController.EvMin);
            exp.limitMax.Override(SkyController.EvMax);
            exp.adaptationSpeedDarkToLight.Override(0.5f);
            exp.adaptationSpeedLightToDark.Override(1.5f);

            Get<Tonemapping>(profile).mode.Override(TonemappingMode.ACES);
            Get<Bloom>(profile).intensity.Override(0.2f);
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

        static Material PlumeMaterial(string path)
        {
            var m = LoadOrCreate(path, "HDRP/Lit");
            m.SetColor("_BaseColor", new Color(1f, 0.75f, 0.45f));
            HDMaterial.SetEmissiveColor(m, new Color(1f, 0.7f, 0.4f));
            HDMaterial.SetEmissiveIntensity(m, PlumeNits, EmissiveIntensityUnit.Nits);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
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
