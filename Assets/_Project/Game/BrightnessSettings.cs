using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Настройки яркости игрока (меню Esc, GDD §9.3): общая яркость кадра — компенсация экспозиции в EV, отдельный
    /// множитель яркости факела и подсветка ландшафта ночью. Лежат в PlayerPrefs и переживают перезапуск и перезагрузку
    /// сцены; значения читаются лениво, чтобы статик не зависел от порядка Awake. Применяют: SkyController (компенсация
    /// и пределы EV), VesselView (нит и сила света факела), NightLight (подсветка грунта).
    /// </summary>
    public static class BrightnessSettings
    {
        /// <summary>Пределы ползунков. ±5 EV — в 32 раза темнее/ярче: компенсация сдвигает и пределы EV
        /// (SkyController), поэтому работает и ночью с факелом. Факел ×0,05…4 (ползунок логарифмический): экспозиция под
        /// множитель не подстраивается — при 0,05 ядро ночью ≈ белого (PlumeNightWhite 16), при 4 — в 64 раза выше.
        /// Ночь — доля белого у грунта с альбедо 0,2 при текущей экспозиции (NightLight): 0 — только физичный свет.</summary>
        public const float EvMin = -5, EvMax = 5, PlumeMin = 0.05f, PlumeMax = 4f, NightMin = 0, NightMax = 0.4f;
        /// <summary>0,07 — столько даёт свечение неба (NightLight.SkyglowLux 0,02 лк) при EvMin −6: обычная ночь не
        /// меняется, а при факеле или прожекторах грунт остаётся таким же различимым. Пара: SkyController.EvMin.</summary>
        const float NightDefault = 0.07f;
        const string EvKey = "kare.brightness.ev", PlumeKey = "kare.brightness.plume", NightKey = "kare.brightness.night";

        static float ev = float.NaN, plume = float.NaN, night = float.NaN;

        /// <summary>Компенсация экспозиции, EV: плюс — ярче.</summary>
        public static float Ev
        {
            get
            {
                if (float.IsNaN(ev)) ev = Mathf.Clamp(PlayerPrefs.GetFloat(EvKey, 0), EvMin, EvMax);
                return ev;
            }
            set
            {
                ev = Mathf.Clamp(value, EvMin, EvMax);
                PlayerPrefs.SetFloat(EvKey, ev);
            }
        }

        /// <summary>Множитель яркости и силы света факела; 1 — как задумано.</summary>
        public static float Plume
        {
            get
            {
                if (float.IsNaN(plume)) plume = Mathf.Clamp(PlayerPrefs.GetFloat(PlumeKey, 1), PlumeMin, PlumeMax);
                return plume;
            }
            set
            {
                plume = Mathf.Clamp(value, PlumeMin, PlumeMax);
                PlayerPrefs.SetFloat(PlumeKey, plume);
            }
        }

        /// <summary>Подсветка ландшафта ночью: доля белого у грунта при текущей экспозиции.</summary>
        public static float Night
        {
            get
            {
                if (float.IsNaN(night)) night = Mathf.Clamp(PlayerPrefs.GetFloat(NightKey, NightDefault), NightMin, NightMax);
                return night;
            }
            set
            {
                night = Mathf.Clamp(value, NightMin, NightMax);
                PlayerPrefs.SetFloat(NightKey, night);
            }
        }

        public static void Save() => PlayerPrefs.Save();
    }
}
