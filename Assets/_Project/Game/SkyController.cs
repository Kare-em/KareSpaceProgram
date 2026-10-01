using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.Game
{
    /// <summary>
    /// Physically Based Sky в «космическом» режиме (GDD §9.2, §9.3): центр и радиус планеты каждый кадр
    /// (там же, где меш тела — с учётом сжатия §2.7), профиль рассеяния по SOI, пределы экспозиции.
    /// Пишет в копию профиля, чтобы Play не портил ассет.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public sealed class SkyController : MonoBehaviour
    {
        public Volume Volume;

        /// <summary>Пределы EV100 §9.3; на карте экспозиция фиксированная (§9.6).</summary>
        public const float EvMin = -5, EvMax = 16, MapEv = 13;

        VisualEnvironment env;
        PhysicallyBasedSky sky;
        Exposure exposure;
        Bloom bloom;
        float bloomFlight;
        CelestialBody profileBody;
        EarthAir earth;

        /// <summary>Земные плотности из профиля — чтобы вернуть их после Луны/Марса.</summary>
        struct EarthAir
        {
            float r, g, b, aerosol, ozone, top;
            Color tint;

            public static EarthAir Capture(PhysicallyBasedSky s) => new EarthAir
            {
                r = s.airDensityR.value, g = s.airDensityG.value, b = s.airDensityB.value,
                aerosol = s.aerosolDensity.value, ozone = s.ozoneDensityDimmer.value,
                top = s.airMaximumAltitude.value, tint = s.aerosolTint.value,
            };

            public void Restore(PhysicallyBasedSky s)
            {
                s.airDensityR.Override(r); s.airDensityG.Override(g); s.airDensityB.Override(b);
                s.aerosolDensity.Override(aerosol); s.ozoneDensityDimmer.Override(ozone);
                s.airMaximumAltitude.Override(top); s.aerosolTint.Override(tint);
            }
        }

        void Start()
        {
            if (Volume == null) Volume = GetComponent<Volume>();
            if (Volume == null || Volume.sharedProfile == null) { enabled = false; return; }
            Volume.profile = Instantiate(Volume.sharedProfile); // рантайм-копия
            Volume.profile.TryGet(out env);
            Volume.profile.TryGet(out sky);
            Volume.profile.TryGet(out exposure);
            if (Volume.profile.TryGet(out bloom)) bloomFlight = bloom.intensity.value;
            if (env == null || sky == null)
            {
                Debug.LogError("[Sky] В профиле нет VisualEnvironment/PhysicallyBasedSky — пересобери сцену (Kare/Build Flight Scene).");
                enabled = false;
                return;
            }
            earth = EarthAir.Capture(sky);
            env.centerMode.Override(VisualEnvironment.PlanetMode.Manual);
            env.renderingSpace.Override(RenderingSpace.World);
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (u?.Active == null) return;
            FloatingOrigin.Refresh();
            var b = u.Active.Body;
            if (b != profileBody) ApplyProfile(b);

            // HDRP ждёт центр и радиус планеты в КИЛОМЕТРАХ (VisualEnvironment), сцена — в метрах.
            var c = BodyRenderer.Project(b, out double k);
            env.planetCenter.Override(c / 1000f);
            env.planetRadius.Override((float)(b.Radius * k / 1000));

            if (exposure != null)
            {
                exposure.mode.Override(MapView.IsOpen ? ExposureMode.Fixed : ExposureMode.AutomaticHistogram);
                exposure.fixedExposure.Override(MapEv);
                float evMin = Mathf.Lerp(EvMin, SunlitEvMin, SunlitWeight(u.Active));
                float plume = VesselView.PlumePeakNits;
                if (plume > 0) evMin = Mathf.Max(evMin, Mathf.Log(plume / (1.2f * PreExposedMax), 2));
                exposure.limitMin.Override(evMin);
            }
            // На карте bloom выключен: при фиксированной EV диск Солнца (≈1,6·10⁹ нит) с порогом 0 размазывался
            // на весь кадр серой пеленой, и линии орбит в ней тонули (замер 01.10.2026: без bloom — чёрный фон).
            if (bloom != null) bloom.intensity.Override(MapView.IsOpen ? 0 : bloomFlight);
        }

        /// <summary>
        /// Нижний предел EV на свету (§9.3, «орбита на свету»). Без него гистограмма в космосе видит
        /// почти чёрный кадр и уходит к EvMin — освещённые Земля и борт выбеливаются целиком
        /// (замер 01.10.2026: белый кадр на 124 км, при пределе 12 — нормальный). Ночью предел — EvMin.
        /// </summary>
        public const float SunlitEvMin = 12;
        /// <summary>Высота, к которой небо уже чёрное и предел выходит на SunlitEvMin, м.
        /// Пара: кадр на 25 км — небо почти чёрное; у поверхности гистограмме не мешаем (закаты).</summary>
        const double DarkSkyAltitude = 30000;

        /// <summary>
        /// Потолок яркости после предэкспозиции для факела (§9.5). Множитель кадра 1/(1,2·2^EV): при EV −5
        /// ядро 3·10³ нит даёт 8·10⁴ — выше максимума half (65 504) → Inf в буфере. Отсюда
        /// EV ≥ log2(нит / (1,2·PreExposedMax)): для ядра 3·10³ это ≈ −2. Запас ×6 до half — на наложение слоёв
        /// аддитива (две стенки ядра + две свечения). Пара: VesselView.CoreNits.
        /// </summary>
        const float PreExposedMax = 1e4f;

        static float SunlitWeight(Vessel v)
        {
            double air = v.Body.HasAtmosphere ? System.Math.Min(1, System.Math.Max(0, v.Altitude / DarkSkyAltitude)) : 1;
            return (float)(SunLight.Visible * air);
        }

        /// <summary>Смена профиля по SOI (§9.2): Земля, Марс, остальное — без атмосферы.</summary>
        void ApplyProfile(CelestialBody b)
        {
            profileBody = b;
            switch (b.Id)
            {
                case "earth":
                    sky.atmosphericScattering.Override(true);
                    sky.type.Override(PhysicallyBasedSkyModel.EarthAdvanced);
                    earth.Restore(sky);
                    break;
                case "mars":
                    // M6 — свои параметры рассеяния; пока тонкая рыжая дымка.
                    sky.atmosphericScattering.Override(true);
                    sky.type.Override(PhysicallyBasedSkyModel.Custom);
                    sky.airDensityR.Override(0.002f);
                    sky.airDensityG.Override(0.004f);
                    sky.airDensityB.Override(0.008f);
                    sky.aerosolDensity.Override(0.05f);
                    sky.ozoneDensityDimmer.Override(0);
                    sky.aerosolTint.Override(new Color(0.85f, 0.55f, 0.35f));
                    sky.airMaximumAltitude.Override(60000);
                    break;
                default:
                    // Без атмосферы (§9.2): только Солнце и эмиссия космоса — плотности в ноль.
                    sky.atmosphericScattering.Override(false);
                    sky.type.Override(PhysicallyBasedSkyModel.Custom);
                    sky.airDensityR.Override(0);
                    sky.airDensityG.Override(0);
                    sky.airDensityB.Override(0);
                    sky.aerosolDensity.Override(0);
                    sky.ozoneDensityDimmer.Override(0);
                    break;
            }
            sky.groundTint.Override(BodyVisuals.Get(b.Id).Low);
        }
    }
}
