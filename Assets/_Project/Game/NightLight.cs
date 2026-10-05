using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.Game
{
    /// <summary>
    /// Ночной свет (GDD §9.1, §9.3): кроме Солнца в сцене не было ни одного источника, и ночью грунт, стол и борт
    /// были чёрными при любой экспозиции (замер 02.10.2026: «Спутник», Солнце −48°, Луна +23° — кадр (0,0,0)
    /// кроме звёзд). Два направленных света:
    /// 1) отражённый — самое яркое после Солнца тело (у Земли — Луна, на Луне — Земля): освещённость
    ///    E = E☉(тело) · p · (R/d)² · Φ(α), p — геометрическое альбедо, Φ — фаза ламбертовой сферы;
    ///    полная Луна ≈ 0,31 лк (реально 0,25…0,3), полная Земля с Луны ≈ 15 лк; затенение — как у Солнца;
    /// 2) свечение ночного неба (собственное свечение верхней атмосферы + звёзды, реально ≈ 0,002 лк) из зенита —
    ///    только в воздухе, иначе безлунная ночь остаётся чёрной.
    /// Обоим выключено взаимодействие с небом PBSky: диск отражённого света рисовался бы полным кругом поверх
    /// фазы меша Луны.
    /// </summary>
    public sealed class NightLight : MonoBehaviour
    {
        /// <summary>Свечение неба, лк. Реальные 0,002 на экране неотличимы от чёрного; ×10 — поправка на
        /// ночное (скотопическое) зрение, которого монитор не даёт. Пара: SkyController.EvMin — при −7 грунт
        /// с альбедо 0,2 выходит ≈ 14 % белого.</summary>
        const float SkyglowLux = 0.02f;
        /// <summary>Минимум света на ночной стороне вне воздуха, лк (§9.3). Реально там только звёзды, свечение
        /// атмосферы и огни городов; без Луны кадр на орбите был чёрным (замер 03.10.2026: среднее 9/255, борт и
        /// Земля неразличимы). 0,04 лк — вдвое слабее свечения неба у земли и в 5 раз слабее полной Луны: ночь остаётся
        /// ночью, но контур борта и материки читаются. Пара: SkyController.EvMin (−7) — при нём это ≈ 0,1 белого.</summary>
        const float SpaceGlowLux = 0.04f;
        /// <summary>Альбедо грунта, под которое считается подсветка ландшафта ночью. Пара: BrightnessSettings.Night.</summary>
        const float LandscapeAlbedo = 0.2f;
        /// <summary>Заполняющий свет карты, лк (§9.6). Без него ночная сторона ровно чёрная, и орбита уходила в чёрный диск.
        /// Замер при EV 13: 2500 лк — средний кадр 1,0 из 255 (диск едва виден), 8000 — тёмно-серая ночь. Карта теперь на
        /// EV 15 (кадр вчетверо темнее) — свет ×4, ≈ 25 % Солнца: ночь та же тёмно-серая. Пара: SkyController.MapEv.</summary>
        const float MapFillLux = 32000f;
        const float MapFillTemperature = 8000;
        /// <summary>Цветовые температуры, К. Лунный свет физически чуть краснее солнечного, но ночью глаз видит
        /// мир синеватым (эффект Пуркинье) — это и передаём. Свет Земли с Луны — голубой от океанов и облаков.</summary>
        const float MoonTemperature = 8500, EarthshineTemperature = 9000, SkyglowTemperature = 11000;
        /// <summary>Ниже этого, лк, свет выключен — экономия на теневой карте.</summary>
        const float MinLux = 1e-4f;

        Light reflected, skyglow, mapFill;

        public static NightLight Create()
        {
            var go = new GameObject("Night Light");
            return go.AddComponent<NightLight>();
        }

        void Awake()
        {
            reflected = MakeLight("Reflected", LightShadows.Soft);
            skyglow = MakeLight("Skyglow", LightShadows.None);
            skyglow.colorTemperature = SkyglowTemperature;
            mapFill = MakeLight("Map Fill", LightShadows.None);
            mapFill.colorTemperature = MapFillTemperature;
        }

        Light MakeLight(string name, LightShadows shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            var hd = go.AddComponent<HDAdditionalLightData>();
            l.lightUnit = UnityEngine.Rendering.LightUnit.Lux;
            l.useColorTemperature = true;
            l.shadows = shadows;
            l.intensity = 0;
            hd.interactsWithSky = false;
            hd.flareSize = 0;
            hd.flareMultiplier = 0;
            return l;
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (u?.Active == null) return;
            var v = u.Active;
            var sys = u.System;
            var me = FloatingOrigin.WorldP(v);

            // Отражённый свет: перебор тел, самое яркое кроме Солнца и тела, на котором (над которым) стоим,
            // — оно светит ночной стороной вниз, его вклад — подсветка борта снизу (groundTint PBSky).
            CelestialBody best = null;
            double bestLux = 0;
            foreach (var b in sys.Bodies)
            {
                if (b.Parent == null || b == v.Body) continue;
                double lux = ReflectedLux(sys, b, me);
                if (lux > bestLux) { bestLux = lux; best = b; }
            }
            if (best != null)
            {
                var to = best.Position - me;
                double d = to.magnitude;
                bestLux *= SunLight.DiscFraction(sys, me, to / d, d, best.Radius);
                reflected.transform.rotation = Quaternion.LookRotation(FloatingOrigin.DirToUnity(-to / d));
                reflected.colorTemperature = best.Id == "earth" ? EarthshineTemperature : MoonTemperature;
            }
            Set(reflected, MapView.IsOpen ? 0 : (float)bestLux);
            // HDRP строит каскадные тени только одному направленному свету: днём Луна над горизонтом, и её
            // тени сыпали в консоль «Cascade Shadow atlasing has failed» каждый кадр (02.10.2026). Днём лунные
            // тени всё равно невидимы на фоне солнечных (0,3 лк против 10⁵), поэтому отдаём их Солнцу.
            reflected.shadows = SunLight.Shining ? LightShadows.None : LightShadows.Soft;

            // Свечение неба: только в воздухе, гаснет к DarkSkyAltitude, как небо PBSky.
            var up = v.Position.normalized;
            skyglow.transform.rotation = Quaternion.LookRotation(FloatingOrigin.DirToUnity(-up));
            float air = v.Body.HasAtmosphere && !MapView.IsOpen ? 1 - SkyController.AirWeight(v) : 0;
            // Вне воздуха — минимум SpaceGlowLux в тени (Солнце закрыто телом), иначе орбитальная ночь чёрная.
            float space = MapView.IsOpen ? 0 : SpaceGlowLux * (1 - air) * (1 - Mathf.Clamp01((float)SunLight.Visible));
            // Подсветка ландшафта (ползунок «Ночь»): при факеле или прожекторах экспозиция поднимается на 10+ ступеней,
            // и физичный свет неба (0,02 лк) уходит в чёрное — кадр «пламя в пустоте». Держим грунт с альбедо
            // LandscapeAlbedo на доле Night от белого при текущей экспозиции: E = W·1,2·2^EV·π/ρ. Без факела EV у пола
            // EvMin, и при W 0,07 это те же 0,02 лк — обычная ночь не меняется. Петли нет: EV берётся от пределов, а не
            // от гистограммы.
            float night = 1 - Mathf.Clamp01((float)SunLight.Visible);
            float fill = BrightnessSettings.Night * 1.2f * Mathf.Pow(2, SkyController.LandscapeEv) * Mathf.PI / LandscapeAlbedo;
            Set(skyglow, Mathf.Max(SkyglowLux, fill * night) * air + space);

            // Карта: свет из камеры чуть сверху-сбоку, чтобы шар не был плоским диском.
            var cam = Camera.main;
            if (MapView.IsOpen && cam != null)
            {
                mapFill.transform.rotation = cam.transform.rotation * Quaternion.Euler(25, 20, 0);
                Set(mapFill, MapFillLux);
            }
            else Set(mapFill, 0);
        }

        static void Set(Light l, float lux)
        {
            l.intensity = lux;
            l.enabled = lux > MinLux;
        }

        /// <summary>Освещённость от тела b в точке me, лк, без затенения.</summary>
        static double ReflectedLux(SolarSystem sys, CelestialBody b, Vector3d me)
        {
            var toSun = sys.Sun.Position - b.Position;
            var toMe = me - b.Position;
            double ds = toSun.magnitude, d = toMe.magnitude;
            if (d <= b.Radius) return 0;
            double au = Constants.AU / ds;
            double alpha = Vector3d.Angle(toSun / ds, toMe / d); // фазовый угол: 0 — полная фаза
            double phase = (System.Math.Sin(alpha) + (System.Math.PI - alpha) * System.Math.Cos(alpha)) / System.Math.PI;
            double k = b.Radius / d;
            return SunLight.IlluminanceAt1Au * au * au * GeometricAlbedo(b.Id) * k * k * phase;
        }

        /// <summary>Геометрическое альбедо (NASA fact sheets). Визуальное, в игровом слое — в Core цветов нет.</summary>
        static double GeometricAlbedo(string id)
        {
            switch (id)
            {
                case "moon": return 0.12;
                case "earth": return 0.434;
                case "mars": return 0.17;
                case "venus": return 0.69;
                case "mercury": return 0.142;
                case "jupiter": return 0.538;
                case "saturn": return 0.499;
                case "europa": return 0.67;
                case "enceladus": return 1.0;
                default: return 0.15;
            }
        }
    }
}
