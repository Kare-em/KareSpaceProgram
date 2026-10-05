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
        /// <summary>Hidden/Kare/SpaceFogRestore — снятие ложного тумана HDRP с борта над атмосферой (SpaceFogFix).
        /// Ставит FlightSceneBuilder: шейдер без ссылки не попадёт в билд.</summary>
        public Shader FogRestoreShader;

        /// <summary>Пределы EV100 §9.3; на карте экспозиция фиксированная (§9.6). EvMin −6: ночью свет только луна и
        /// свечение неба (NightLight.SkyglowLux, 0,02 лк). При −5 безлунный грунт был ≈ 3 % белого (чёрная ночь), при −7
        /// стол ночью читался почти как в сумерки (замер 03.10.2026: средняя яркость кадра 35 из 255 — «пересвечено»).
        /// MapEv 15 — «солнечные 16» для освещённой планеты: Земля ρE/π ≈ 11 500 нит при Солнце 126 000 лк даёт 0,29
        /// белого, облака 0,78. При 13 было 1,17 (а с ползунком яркости +2 — ×4,7, белая дневная сторона, 03.10.2026).
        /// Пара: NightLight.MapFillLux — заполняющий свет ночной стороны под эту EV.</summary>
        public const float EvMin = -6, EvMax = 16, MapEv = 15;

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
            // Рантайм-копия. Instantiate(VolumeProfile) копирует только список — компоненты остаются общими с ассетом,
            // и правки экспозиции/неба в Play утекали в Settings/FlightVolume.asset (fixedExposure 14,9 на диске).
            // Поэтому каждый компонент копируется отдельно.
            var runtime = Instantiate(Volume.sharedProfile);
            for (int i = 0; i < runtime.components.Count; i++) runtime.components[i] = Instantiate(runtime.components[i]);
            Volume.profile = runtime;
            Volume.profile.TryGet(out env);
            Volume.profile.TryGet(out sky);
            Volume.profile.TryGet(out exposure);
            if (Volume.profile.TryGet(out bloom))
            {
                bloomFlight = bloom.intensity.value;
                bloom.scatter.Override(BloomScatter);
            }
            if (env == null || sky == null)
            {
                Debug.LogError("[Sky] В профиле нет VisualEnvironment/PhysicallyBasedSky — пересобери сцену (Kare/Build Flight Scene).");
                enabled = false;
                return;
            }
            if (Volume.profile.TryGet(out RayTracingSettings rt)) rt.distantRayBias.Override(DistantRayBias);
            earth = EarthAir.Capture(sky);
            env.centerMode.Override(VisualEnvironment.PlanetMode.Manual);
            env.renderingSpace.Override(RenderingSpace.World);
            SpaceFogFix.Create(FogRestoreShader, transform);
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (u?.Active == null) return;
            FloatingOrigin.Refresh();
            var b = u.Active.Body;
            if (b != profileBody) ApplyProfile(b);
            // Цвет земли PBSky — это и подсветка борта снизу (ambient). У поверхности под бортом местный грунт,
            // выше воздуха — весь диск планеты: тот же вес высоты, что у предела EV (DarkSkyAltitude).
            var look = BodyVisuals.Get(b.Id);
            var disc = look.Disc.a > 0 ? look.Disc : look.Low;
            sky.groundTint.Override(Color.Lerp(look.Low, disc, AirWeight(u.Active)));

            // HDRP ждёт центр и радиус планеты в КИЛОМЕТРАХ (VisualEnvironment), сцена — в метрах.
            var c = BodyRenderer.Project(b, out double k);
            env.planetCenter.Override(c / 1000f);
            env.planetRadius.Override((float)(b.Radius * k / 1000));
            SpaceFogFix.Update(Camera.main, c, (float)(b.Radius * k), SpaceFogFix.AtmosphereTop(sky), sky.atmosphericScattering.value);

            if (exposure != null)
            {
                exposure.mode.Override(MapView.IsOpen ? ExposureMode.Fixed : ExposureMode.AutomaticHistogram);
                exposure.adaptationSpeedDarkToLight.Override(AdaptDarkToLight);
                exposure.adaptationSpeedLightToDark.Override(AdaptLightToDark);
                // Яркость игрока (меню Esc): плюс — ярче. HDRP 17.6 вычитает компенсацию ДО зажима пределами
                // (HistogramExposure.compute: clamp(avgEV − comp, min, max)), поэтому пределы ниже считаются «без
                // ползунка» и сдвигаются на −comp целиком — иначе там, где EV стоит на пределе (ночь, факел, стол),
                // ползунок не работал или работал наоборот (+2 EV у факела ночью давали кадр вчетверо темнее).
                float comp = BrightnessSettings.Ev;
                exposure.compensation.Override(comp);
                exposure.fixedExposure.Override(MapEv - comp);
                float evMin = Mathf.Lerp(EvMin, SunlitEvMin, SunlitWeight(u.Active));
                // Экспозиция ведётся по факелу «как задуман» (без ползунка «Факел»), а реальная яркость — с ним:
                // так ползунок меняет факел относительно сцены, а не съедается экспозицией.
                float plume = VesselView.PlumePeakNits, plumeReal = plume * BrightnessSettings.Plume;
                float safeEv = EvMin - BrightnessSettings.EvMax;
                if (plume > 0)
                {
                    // Защита half-буфера от переполнения факелом (см. PreExposedMax) — по итоговому EV, без ползунка.
                    safeEv = Mathf.Log(plumeReal / (1.2f * PreExposedMax), 2);
                    // Ночью кадр тёмный, и автоэкспозиция
                    // по тёмному кадру уходит к EvMin и выжигает всё вокруг (замер 03.10.2026: «Спутник» на старте,
                    // 3,8 % кадра белые, дым квадратами). Держим экспозицию такой, чтобы ядро было в PlumeNightWhite раз
                    // белого: ярко, но не заливает. Днём предел EV и так выше — max ничего не меняет.
                    plumeNight = 1 - Mathf.Clamp01((float)SunLight.Visible);
                    plumeEv = Mathf.Log(plume / (1.2f * PlumeNightWhite), 2);
                }
                // Плавно: вспышка факела за 1,5 с поднимает экспозицию, после выключения она за 3 с возвращается к
                // автоматической. Гистограмма сама тянет EV вверх (факел яркий — «темнее»), поэтому верх тоже зажат.
                plumeK = Mathf.MoveTowards(plumeK, plume > 0 ? plumeNight : 0,
                    Time.unscaledDeltaTime / (plume > 0 && plumeNight > plumeK ? PlumeRampUp : PlumeRampDown));
                evMin = Mathf.Max(evMin, Mathf.Lerp(evMin, plumeEv, plumeK));
                // Прожекторы стола ночью: корпус ракеты в PadHullWhite раз белого, а не выжжен (LaunchPadView.LitNits).
                float pad = LaunchPadView.LitNits;
                if (pad > 0) evMin = Mathf.Max(evMin, Mathf.Log(pad / (1.2f * PadHullWhite), 2));
                float plasma = VesselView.PlasmaPeakNits;
                if (plasma > 0) evMin = Mathf.Max(evMin, Mathf.Log(plasma / (1.2f * PlasmaWhite), 2));
                float evMax = Mathf.Max(evMin, Mathf.Lerp(EvMax, plumeEv + PlumeEvSlack, plumeK));
                // Подсветка ландшафта ночью (NightLight) — под верх полосы факела: гистограмма с ярким факелом жмётся
                // к верхнему пределу. Без ползунка «Общая» — иначе он не менял бы яркость грунта.
                LandscapeEv = MapView.IsOpen ? EvMin : Mathf.Min(evMax, evMin + PlumeEvSlack * plumeK);
                float lo = Mathf.Max(evMin - comp, safeEv);
                exposure.limitMin.Override(lo);
                exposure.limitMax.Override(Mathf.Max(lo, evMax - comp));
                // Звёзды за экспозицией (§9.3): физичные звёзды видны только при EV ≲ −4 — на свету в космосе и при
                // факеле ночью небо было чёрным. Множитель держит их такими, как при EV StarsEv, но не тусклее
                // реальных. Днём в воздухе предел EV низкий — множитель 1, звёзд не видно, как и должно быть.
                if (sky != null) sky.spaceEmissionMultiplier.Override(MapView.IsOpen ? 1 : Mathf.Pow(2, Mathf.Max(0, evMin - StarsEv)));
            }
            // На карте bloom выключен: при фиксированной EV диск Солнца (≈1,6·10⁹ нит) с порогом 0 размазывался
            // на весь кадр серой пеленой, и линии орбит в ней тонули (замер 01.10.2026: без bloom — чёрный фон).
            // Ночью с факелом bloom приглушён: ядро в PlumeNightWhite раз белого растаскивалось в ореол на пол-кадра
            // (замер 03.10.2026, ночной старт: без bloom ореола нет, средняя яркость 14,5 → 6,2).
            if (bloom != null) bloom.intensity.Override(MapView.IsOpen ? 0 : bloomFlight * Mathf.Lerp(1, PlumeNightBloom, plumeK));
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

        /// <summary>
        /// Во сколько раз ядро факела ярче белого ночью (§9.5). Пара: VesselView.CoreNits (3·10³ нит) и VesselView.PlumeLight
        /// (сила света из той же яркости): при 16 экспозиция ≈ EV 7,3, ядро в 16 раз выше белого с ореолом, борт у среза
        /// не ярче ≈ ρ·16 белого. Замер 03.10 ниже — ещё при постоянных 2·10⁶ кд.
        /// Замер «Спутника» на старте: 8 → средняя яркость кадра 5, 16 → 8,6 (до правки 87,8 и 3,8 % белых). Больше —
        /// кадр снова заливает светом, меньше — окружение чернеет.
        /// </summary>
        const float PlumeNightWhite = 16f;
        /// <summary>Запас вверх от ночного EV факела, ступени, и время нарастания/спада зажима, с. Пара: PlumeNightWhite и
        /// AdaptDarkToLight/AdaptLightToDark — зажим не должен быть заметно медленнее самой адаптации (05.10.2026: было
        /// 1,5/3 с при адаптации 0,5/1,5 — «ISO переключается медленно», ускорено втрое вместе с ней).</summary>
        const float PlumeEvSlack = 1.5f, PlumeRampUp = 0.5f, PlumeRampDown = 1f;
        /// <summary>Скорость автоэкспозиции HDRP (§9.3), EV/с-подобный коэффициент: больше — быстрее. Ставится в рантайме,
        /// чтобы не требовать пересборки сцены; FlightSceneBuilder пишет те же значения в FlightVolume. Было 0,5/1,5 —
        /// выход из тени/включение факела тянулись секунды; ×4 и ×3,3 — адаптация за доли секунды, но без щелчка.</summary>
        public const float AdaptDarkToLight = 2f, AdaptLightToDark = 5f;
        /// <summary>Доля bloom ночью при факеле. Пара: PlumeNightWhite — чем ярче ядро относительно белого, тем меньше.</summary>
        const float PlumeNightBloom = 0.25f;

        /// <summary>
        /// Экспозиция, при которой звёзды показываются «как есть», EV100 (§9.3). Звёзды в нитах (StarFieldBaker:
        /// 0m ≈ 1 нит на тексель): при EV −6 звезда 0m — 53× белого, 4m — 0,6, Млечный Путь едва заметен. Замер ночного
        /// старта (EV 7,3): множитель 2500 (≈ EV −4) и 5000 (−5) — звёзды редкие и тусклые; приравнено к EvMin — небо как у стола
        /// безлунной ночью. Пара: EvMin.
        /// </summary>
        const float StarsEv = EvMin;
        float plumeK, plumeEv, plumeNight;

        /// <summary>EV100 сцены без ползунка «Общая» — по нему NightLight держит грунт ночью видимым (§9.3).</summary>
        public static float LandscapeEv { get; private set; } = EvMin;

        /// <summary>
        /// Ударный слой на входе — во столько раз ярче белого после экспозиции (§4.6, §9.3). Ночью предел EV — EvMin,
        /// гистограмма видит чёрный кадр и держит EV ≈ −3: плазма 1,2·10³ нит давала 8·10³ при белом 1 — весь
        /// кадр выжжен. Предел log2(нит / (1,2·2)) ≈ 9 при полном нагреве: ядро ореола чуть пересвечено, след
        /// и накал корпуса читаются. Днём предел и так 12. Пара: VesselView.PlasmaNits.
        /// </summary>
        const float PlasmaWhite = 2f;

        /// <summary>Корпус ракеты в свете прожекторов — во столько раз ярче белого (§7, §9.3). Меньше 1 — корпус серый,
        /// бетон и мачты тонут в черноте. Пара: LaunchPadView.FloodHullNits.</summary>
        const float PadHullWhite = 0.9f;

        /// <summary>Разлёт bloom. Штатные 0,7 растаскивали диск Солнца (≈1,9·10⁹ нит при пороге 0) в ореол
        /// ≈ 150 px на 1920 — серое пятно на орбите и оливковое на голубом небе; 0,3 — плотное белое пятно
        /// (замер 02.10.2026). Пара: SunLight.ColorTemperature.</summary>
        public const float BloomScatter = 0.3f;

        /// <summary>
        /// Смещение RT-луча тени вдали, м (§9.3). Начало луча HDRP восстанавливает из глубины, и на десятках
        /// километров ошибка — метры, а штатное смещение 0,001: луч стартует из-под грунта и упирается в сам патч.
        /// Признак — тёмно-синие пятна по всему дальнему грунту при Солнце 47° (замер 02.10.2026, камера 30 км);
        /// CPU-трассировка того же меша — 0 из 200 точек в тени. 5 м пятна не убрало, 50 — чисто. Ближнее смещение
        /// (rayBias) штатное — иначе пропадает тень ракеты на столе.
        /// </summary>
        const float DistantRayBias = 50f;

        static float SunlitWeight(Vessel v) => (float)SunLight.Visible * AirWeight(v);

        /// <summary>0 у поверхности, 1 — выше DarkSkyAltitude (небо уже чёрное); без атмосферы всегда 1.</summary>
        public static float AirWeight(Vessel v) =>
            v.Body.HasAtmosphere ? (float)System.Math.Min(1, System.Math.Max(0, v.Altitude / DarkSkyAltitude)) : 1;

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
        }
    }
}
