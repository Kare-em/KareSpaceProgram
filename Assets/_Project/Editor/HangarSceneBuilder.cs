using Kare.Space.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.EditorTools
{
    /// <summary>
    /// Автосборка сцены Hangar — конструктор ракет (GDD §5.4). Идемпотентна, как FlightSceneBuilder:
    /// сцена создаётся заново, профиль переиспользуется. Материал деталей — VesselLit.mat из сборки Flight.
    /// </summary>
    public static class HangarSceneBuilder
    {
        const string ScenePath = "Assets/_Project/Scenes/Hangar.unity";
        const string SettingsDir = "Assets/_Project/Settings";
        const string ProfilePath = SettingsDir + "/HangarVolume.asset";
        const string VesselMatPath = SettingsDir + "/VesselLit.mat";

        /// <summary>Свет цеха, люкс. Пара: FixedEv — экспозиция подобрана под эту освещённость (≈ log2(lux / 2.5)).</summary>
        const float LightLux = 10000;
        const float FixedEv = 12;
        /// <summary>Яркость градиентного неба (EV, как у SkySettings.exposure): фон чуть темнее ракеты при FixedEv.</summary>
        const float SkyEv = 10.5f;

        [MenuItem("Kare/Build Hangar Scene")]
        public static void Build()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(VesselMatPath);
            if (mat == null)
            {
                Debug.LogError($"[Kare] Нет {VesselMatPath}: сначала Kare/Build Flight Scene (он создаёт материалы).");
                return;
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ScenePath));
            var profile = BuildProfile();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var volGo = new GameObject("Global Volume");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = profile;

            var lightGo = new GameObject("Hangar Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            lightGo.AddComponent<HDAdditionalLightData>().angularDiameter = 3; // мягкая тень, как от больших ламп цеха
            light.lightUnit = LightUnit.Lux;
            light.intensity = LightLux;
            light.useColorTemperature = true;
            light.colorTemperature = 5200;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50, -35, 0);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<HDAdditionalCameraData>();
            camGo.AddComponent<AudioListener>();
            // Ракета до 130 м (CraftCompiler.PadMaxHeight), камера отъезжает до 600 м.
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 5000;
            cam.fieldOfView = 45;
            camGo.transform.position = new Vector3(0, 20, -70);

            var game = new GameObject("Hangar");
            var hc = game.AddComponent<HangarController>();
            hc.Material = mat;
            hc.Camera = cam;

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddToBuildSettings(ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Kare] Сцена Hangar собрана: {ScenePath}");
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
            env.skyType.Override((int)SkyType.Gradient);
            env.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            var sky = Get<GradientSky>(profile);
            sky.top.Override(new Color(0.16f, 0.2f, 0.27f));
            sky.middle.Override(new Color(0.32f, 0.35f, 0.4f));
            sky.bottom.Override(new Color(0.1f, 0.1f, 0.11f));
            sky.gradientDiffusion.Override(1.5f);
            sky.exposure.Override(SkyEv);

            var exp = Get<Exposure>(profile);
            exp.mode.Override(ExposureMode.Fixed);
            exp.fixedExposure.Override(FixedEv);
            Get<Tonemapping>(profile).mode.Override(TonemappingMode.ACES);
            Get<Fog>(profile).enabled.Override(false);
            Get<HDShadowSettings>(profile).maxShadowDistance.Override(800);
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

        /// <summary>В конец списка: стартовая сцена остаётся Flight (её FlightSceneBuilder ставит первой).</summary>
        static void AddToBuildSettings(string path)
        {
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (list.Exists(s => s.path == path)) return;
            list.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = list.ToArray();
        }
    }
}
