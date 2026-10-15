using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.Game
{
    /// <summary>
    /// Солнце (GDD §9.1, §9.3): Directional Light вдоль луча Солнце → борт, освещённость
    /// 127 000 лк на 1 а.е. по обратному квадрату, затмения по перекрытию угловых дисков в истинной
    /// геометрии (без сжатия §2.7).
    /// </summary>
    [RequireComponent(typeof(Light))]
    public sealed class SunLight : MonoBehaviour
    {
        /// <summary>Освещённость на 1 а.е., лк (§9.1).</summary>
        public const double IlluminanceAt1Au = 127000;
        /// <summary>
        /// Цветовая температура света, К. Не 5778 (фотосфера): в HDRP белая точка — D65, и 5778 К даёт желтоватый
        /// диск, а вместе с голубым небом PBSky ореол bloom вокруг него выходит оливковым — G ≥ R > B
        /// (замер 02.10.2026 у стола: (169,173,166) в 120 px от центра). 6500 К — белый диск, ореол сразу
        /// уходит в голубое: (180,177,172) → (119,122,128) → (98,103,115). Солнце вне атмосферы и есть белое.
        /// Пара: SkyController.BloomScatter — ширина того же ореола.
        /// </summary>
        public const float ColorTemperature = 6500;

        /// <summary>Доля незатенённого Солнца, 0..1 — для HUD и экспозиции.</summary>
        public static double Visible { get; private set; } = 1;
        /// <summary>Расстояние камеры-борта до Солнца, а. е. — для поправки экспозиции вдали от Солнца (SkyController).</summary>
        public static double DistanceAu { get; private set; } = 1;
        /// <summary>Солнечный свет включён. Пока он горит, каскадные тени заняты им (NightLight).</summary>
        public static bool Shining => Visible > 1e-4;

        Light sun;
        UnityEngine.Rendering.LensFlareComponentSRP flare;

        void Awake()
        {
            sun = GetComponent<Light>();
            sun.useColorTemperature = true;
            sun.colorTemperature = ColorTemperature;
            // Ореол (flare) PBSky — имитация рассеяния в воздухе, в вакууме его нет (§9.3). Его яркость берётся от
            // диска (≈1,9·10⁹ нит), и bloom растаскивал 2° ореола на весь кадр: на орбите при Солнце у края кадра
            // небо серо-белое R≈240, без ореола — R=0, а сам диск с bloom 0,2 — ровное пятно (замер 01.10.2026).
            var hd = GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
            if (hd != null) { hd.flareSize = 0; hd.flareMultiplier = 0; }
            // Вместо ореола — блик объектива, гаснущий к краю кадра (SunFlare); и ночной свет (NightLight).
            flare = SunFlare.Attach(gameObject);
            NightLight.Create();
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (u?.Active == null) return;
            var s = u.System.Sun;
            var me = FloatingOrigin.WorldP(FlightView.Main);
            var toSun = s.Position - me;
            double r = toSun.magnitude;
            transform.rotation = Quaternion.LookRotation(FloatingOrigin.DirToUnity(-toSun / r));

            Visible = SunFraction(u.System, me, toSun / r, r);
            double au = Constants.AU / r;
            DistanceAu = r / Constants.AU;
            sun.intensity = (float)(IlluminanceAt1Au * au * au * Visible);
            sun.enabled = Shining;
            SetDiscSize(r, u.System.Sun.Radius);
            LimitDisc();
            // На карте блика нет — там своя фиксированная экспозиция и Солнце-иконка (§9.6).
            // Блик выключается игроком (клавиша Bind.SunGlare, меню Esc): у дальних зондов Солнце почти всегда в кадре.
            if (KeyBinds.Down(Bind.SunGlare))
            {
                BrightnessSettings.SunGlare = !BrightnessSettings.SunGlare;
                BrightnessSettings.Save();
                u.Post(BrightnessSettings.SunGlare ? "Блик Солнца включён" : "Блик Солнца выключен");
            }
            float far = FlareDistanceFade();
            flare.intensity = MapView.IsOpen || !BrightnessSettings.SunGlare ? 0 : (float)Visible * SunFlare.Intensity * far;
            flare.scale = SunFlare.Scale * Mathf.Max(FlareMinScale, Mathf.Sqrt(far));
        }

        /// <summary>
        /// Блик вдали от Солнца (§9.3, 15.10.2026: «Солнце бликует у Нептуна»). Освещённость падает как 1/r², а блик
        /// объектива рисовался одинаково на любом расстоянии — у Нептуна (30 а. е., Солнце в 900 раз слабее) такой же
        /// ореол, как у Земли. Сила блика — 1/r (мягче, чем 1/r²: у Юпитера ещё виден, 0,19; у Нептуна 0,03 — почти нет),
        /// размер — √ от неё, не меньше FlareMinScale. Ближе 1 а. е. — без усиления (Меркурий не должен слепить сильнее).
        /// </summary>
        static float FlareDistanceFade() => Mathf.Clamp01(1f / (float)System.Math.Max(DistanceAu, 1e-3));
        /// <summary>Нижний предел масштаба блика вдали, доля от SunFlare.Scale.</summary>
        const float FlareMinScale = 0.3f;

        /// <summary>
        /// Минимальный видимый диаметр диска, градусы. Настоящий у Нептуна ≈ 0,018° — треть пикселя при FOV 60° на 1080p:
        /// TAA-джиттер то ловил, то терял такой диск, и он мерцал. 0,06° ≈ пиксель. Пара: SunLight.DiscPreExposedMax —
        /// чем меньше диск, тем ярче его пиксели, потолок их держит.
        /// </summary>
        const double DiscMinDegrees = 0.06;

        /// <summary>
        /// Угловой размер диска по расстоянию (§9.3). Был постоянный 0,53° (как с Земли), и у Сатурна/Нептуна по небу
        /// ходил земной диск с земным bloom-ореолом — «бликует». Заодно тени вдали становятся резче, как и должно быть.
        /// </summary>
        void SetDiscSize(double distance, double radius)
        {
            if (hdSun == null && !TryGetComponent(out hdSun)) return;
            double deg = 2 * System.Math.Asin(System.Math.Min(1, radius / distance)) * Mathf.Rad2Deg;
            hdSun.angularDiameter = (float)System.Math.Max(DiscMinDegrees, deg);
        }

        /// <summary>
        /// Потолок яркости диска Солнца после предэкспозиции (§9.3). Диск PBSky — освещённость / телесный угол:
        /// ≈7·10⁷ нит у Юпитера, ≈2·10⁹ у Земли; в буфере ×1/(1,2·2^EV) — до 10⁶, выше потолка R11G11B10 (≈65 000) → Inf.
        /// TAA-джиттер (8 кадров) переставлял эти пиксели, и bloom-ореол пульсировал: замер 15.10.2026, «Вояджер» у
        /// Юпитера, без блика, камера плывёт 0,1°/кадр — энергия ореола 1500…2040 (разброс 31 %, «мерцает»); с диском
        /// ≤3·10⁴ — 285…288 (1 %). Запас ×2 до потолка — на 2–3 кадра задержки чтения экспозиции (адаптация ≤ 5 EV/с).
        /// Пара: SkyController.PreExposedMax (тот же потолок для факела) и AdaptLightToDark (скорость, которую покрывает запас).
        /// </summary>
        const double DiscPreExposedMax = 3e4;

        static readonly int ExposureTextureId = Shader.PropertyToID("_ExposureTexture");
        HDAdditionalLightData hdSun;
        /// <summary>Множитель экспозиции кадра из HDRP (R — 1/(1,2·2^EV)); 0 — ещё не прочитан.</summary>
        float exposure;
        bool reading;

        /// <summary>
        /// Приглушает диск (surfaceTint), если при текущей экспозиции он переполнил бы буфер; освещение сцены не трогает.
        /// Экспозиция — фактическая, из текстуры HDRP (асинхронно, 1 px): по нижнему пределу EV нельзя — днём у стола
        /// предел −7,3, а кадр на EV 13,4, и Солнце вышло бы чёрным.
        /// </summary>
        void LimitDisc()
        {
            if (hdSun == null && !TryGetComponent(out hdSun)) return;
            if (!reading && Shader.GetGlobalTexture(ExposureTextureId) is Texture tex)
            {
                reading = true;
                AsyncGPUReadback.Request(tex, 0, 0, 1, 0, 1, 0, 1, r =>
                {
                    reading = false;
                    if (!r.hasError) exposure = r.GetData<float>()[0];
                });
            }
            double half = hdSun.angularDiameter * 0.5 * Mathf.Deg2Rad;
            double solid = 2 * System.Math.PI * (1 - System.Math.Cos(half));
            double pre = sun.intensity / System.Math.Max(solid, 1e-12) * exposure;
            float k = pre > DiscPreExposedMax ? (float)(DiscPreExposedMax / pre) : 1;
            hdSun.surfaceTint = new Color(k, k, k, 1);
        }

        /// <summary>
        /// Видимая доля диска Солнца из точки me. Перекрытие дисков приближено плавной ступенью по
        /// расстоянию между центрами: полная тень при θ ≤ ρb − ρs, свет при θ ≥ ρb + ρs (полутень между).
        /// </summary>
        public static double SunFraction(SolarSystem sys, Vector3d me, Vector3d dirSun, double distSun) =>
            DiscFraction(sys, me, dirSun, distSun, sys.Sun.Radius);

        /// <summary>Видимая доля диска любого источника радиуса srcRadius (Солнце, Луна для NightLight).
        /// Сам источник отсекается условием d ≥ distSun — его расстояние совпадает до бита.</summary>
        public static double DiscFraction(SolarSystem sys, Vector3d me, Vector3d dirSun, double distSun, double srcRadius)
        {
            double rs = System.Math.Asin(System.Math.Min(1, srcRadius / distSun));
            double vis = 1;
            foreach (var b in sys.Bodies)
            {
                if (b.Parent == null) continue;
                var tb = b.Position - me;
                double d = tb.magnitude;
                if (d >= distSun) continue;
                // Внутри сферы (приводнение с осадкой, суша ниже уровня моря) тело — полнеба, а не «нет тела»:
                // раньше оно пропускалось, ночью Солнце светило сквозь Землю и мигало при качке на воде.
                double rb = System.Math.Asin(System.Math.Min(1, b.Radius / d));
                double theta = Vector3d.Angle(tb / d, dirSun);
                if (theta >= rb + rs) continue;
                double area = rb >= rs ? 1 : (rb * rb) / (rs * rs); // кольцевое затмение не гасит полностью
                double t = (theta - System.Math.Abs(rb - rs)) / (2 * System.Math.Min(rb, rs));
                double cover = theta <= System.Math.Abs(rb - rs) ? area : area * (1 - MathSmooth(t));
                vis = System.Math.Min(vis, 1 - cover);
            }
            return System.Math.Max(0, vis);
        }

        static double MathSmooth(double t)
        {
            t = System.Math.Max(0, System.Math.Min(1, t));
            return t * t * (3 - 2 * t);
        }
    }
}
