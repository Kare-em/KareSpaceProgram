using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Настройки яркости игрока (меню Esc, GDD §9.3): общая яркость кадра — компенсация экспозиции в EV — и отдельный
    /// множитель яркости факела. Лежат в PlayerPrefs и переживают перезапуск и перезагрузку сцены; значения читаются
    /// лениво, чтобы статик не зависел от порядка Awake. Применяют: SkyController (компенсация и пределы EV),
    /// VesselView (нит и сила света факела).
    /// </summary>
    public static class BrightnessSettings
    {
        /// <summary>Пределы ползунков. ±2 EV — вчетверо темнее/ярче: больше автоэкспозиция всё равно съедает, меньше
        /// не хватает на тёмный монитор. Факел 0,25…1,5 — ниже гасить смысла нет, выше пересвет (CoreNits уже белый).</summary>
        public const float EvMin = -2, EvMax = 2, PlumeMin = 0.25f, PlumeMax = 1.5f;
        const string EvKey = "kare.brightness.ev", PlumeKey = "kare.brightness.plume";

        static float ev = float.NaN, plume = float.NaN;

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

        public static void Save() => PlayerPrefs.Save();
    }
}
