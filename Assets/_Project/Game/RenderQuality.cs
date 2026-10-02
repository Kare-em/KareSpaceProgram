using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.Game
{
    /// <summary>
    /// Трассировка лучей и DLSS (GDD §9, «реалистичная графика»; тот же приём, что в Car_Train/HdrpRuntime).
    /// Пайплайн включает обе возможности (FlightSceneBuilder.EnsurePipeline), здесь — выбор игрока из меню Esc:
    /// RT — тени Солнца лучами (резкие у опор и ферм, без лесенки каскадов на 2 км) и отражения Mixed
    /// (сперва луч по экрану, настоящий — где экран не знает ответа); DLSS — апскейл кадра тензорными ядрами
    /// вместо TAA. Флаги статические — переживают перезапуск миссии (перезагрузку сцены).
    /// </summary>
    [DefaultExecutionOrder(60)] // после SkyController (50): он подменяет профиль копией в Start
    public sealed class RenderQuality : MonoBehaviour
    {
        /// <summary>Выбор игрока. По умолчанию включено всё — действует, только если железо умеет (свойства ниже).</summary>
        public static bool RayTracing = true, Dlss = true;
        public static bool RayTracingSupported => SystemInfo.supportsRayTracing
            && GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset hd
            && hd.currentPlatformRenderPipelineSettings.supportRayTracing;
        /// <summary>Детект HDRP при создании пайплайна (DLSSPass.SetupFeature) — потому читаем каждый раз, а не в Awake:
        /// в сборке Awake сцены идёт раньше первого кадра пайплайна.</summary>
        public static bool DlssSupported => HDDynamicResolutionPlatformCapabilities.DLSSDetected;

        HDAdditionalCameraData cam;
        HDAdditionalLightData sun;
        Volume volume;
        VolumeProfile appliedProfile;
        bool appliedRt, appliedDlss, applied;

        void Awake() => cam = GetComponent<HDAdditionalCameraData>();

        void Start()
        {
            var sky = FindAnyObjectByType<SkyController>();
            volume = sky != null ? sky.Volume : null;
            var sl = FindAnyObjectByType<SunLight>();
            sun = sl != null ? sl.GetComponent<HDAdditionalLightData>() : null;
        }

        void LateUpdate()
        {
            bool rt = RayTracing && RayTracingSupported;
            bool dlss = Dlss && DlssSupported;
            var profile = volume != null ? volume.profile : null;
            if (applied && rt == appliedRt && dlss == appliedDlss && profile == appliedProfile) return;
            applied = true; appliedRt = rt; appliedDlss = dlss; appliedProfile = profile;

            if (cam != null)
            {
                // DLSS сам сглаживает (временной апскейл); TAA нужен ему как источник векторов движения и джиттера.
                cam.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                cam.allowDynamicResolution = dlss;
                cam.allowDeepLearningSuperSampling = dlss;
                cam.deepLearningSuperSamplingUseCustomQualitySettings = true;
#if KARE_NVIDIA_MODULE
                cam.deepLearningSuperSamplingQuality = (uint)UnityEngine.NVIDIA.DLSSQuality.MaximumQuality;
#endif
            }
            if (sun != null)
            {
                // Направленному свету HDRP трассирует тени только через экранный буфер теней (HDRaytracingManager:
                // ShadowsEnabled && useScreenSpaceShadows && useRayTracedShadows) — без первого флага RT молча не работает.
                sun.useScreenSpaceShadows = rt;
                sun.useRayTracedShadows = rt;
            }
            if (profile != null && profile.TryGet(out ScreenSpaceReflection ssr))
                ssr.tracing.Override(rt ? RayCastingMode.Mixed : RayCastingMode.RayMarching);
        }
    }
}
