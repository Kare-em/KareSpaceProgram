using System;
using System.IO;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Режим и файл карьеры (GDD §8): career.json рядом с сохранениями полёта, режим — в PlayerPrefs. Песочница — по
    /// умолчанию (§8.5): карьера включается только явным выбором в меню Esc. Включённая карьера кладёт состояние в
    /// Career.Current — по нему конструктор прячет неизученные детали (HangarController.PartAvailable).
    /// </summary>
    public static class CareerStore
    {
        const string ModeKey = "kare.career.mode";

        public static string Path => System.IO.Path.Combine(Application.persistentDataPath, "career.json");
        public static bool Active => Career.Current != null;
        public static CareerState State => Career.Current;
        public static bool HasSave => File.Exists(Path);

        /// <summary>До первой сцены: миссии карьеры — в каталог (их ищут и конструктор, и загрузка сохранений).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            CareerMissions.Register();
            HangarController.PartAvailable = Career.IsPartUnlocked;
            Career.Current = null;
            if (PlayerPrefs.GetInt(ModeKey, 0) == 1) Career.Current = Load() ?? new CareerState();
        }

        static CareerState Load()
        {
            try { return File.Exists(Path) ? CareerState.FromJson(File.ReadAllText(Path)) : null; }
            catch (Exception e) { Debug.LogWarning($"[Career] career.json не читается: {e.Message}"); return null; }
        }

        public static void Save()
        {
            if (Career.Current == null) return;
            try { File.WriteAllText(Path, Career.Current.ToJson()); }
            catch (Exception e) { Debug.LogWarning($"[Career] career.json не записан: {e.Message}"); }
        }

        /// <summary>Включить карьеру: продолжить сохранённую или начать новую с 1957 года.</summary>
        public static void Enter(bool fresh)
        {
            Career.Current = (fresh ? null : Load()) ?? new CareerState();
            if (fresh) Career.Current.Say("Новая карьера: 1 января 1957, казна 150 M₢");
            PlayerPrefs.SetInt(ModeKey, 1);
            PlayerPrefs.Save();
            Save();
            HangarController.CatalogChanged();
        }

        /// <summary>В песочницу: файл карьеры остаётся, к нему можно вернуться.</summary>
        public static void Leave()
        {
            Save();
            Career.Current = null;
            PlayerPrefs.SetInt(ModeKey, 0);
            PlayerPrefs.Save();
            HangarController.CatalogChanged();
        }

        public static string Money(double m) => $"{m:N0} M₢".Replace(',', ' ');
    }
}
