using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Громкость звука (меню Esc, GDD §9.3). Как BrightnessSettings: PlayerPrefs, ленивое чтение. Применяет FlightAudio
    /// через AudioListener.volume — общий множитель на весь звук сцены. Имя не AudioSettings: тот занят движком.
    /// </summary>
    public static class SoundSettings
    {
        const string VolumeKey = "kare.audio.volume";
        static float volume = float.NaN;

        /// <summary>Общая громкость 0…1; 0,8 по умолчанию — оставить запас до клиппинга при взрыве поверх рёва.</summary>
        public static float Volume
        {
            get
            {
                if (float.IsNaN(volume)) volume = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumeKey, 0.8f));
                return volume;
            }
            set
            {
                volume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(VolumeKey, volume);
            }
        }

        public static void Save() => PlayerPrefs.Save();
    }
}
