using System;
using System.Collections.Generic;
using System.IO;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kare.Space.Game
{
    /// <summary>
    /// Сохранения полёта (GDD §10.3): быстрые клавиши F9/F10 (§10.1, KeyBinds.QuickSave/QuickLoad), слоты в
    /// persistentDataPath/saves/*.ksav и тост «Сохранено»/«Загружено». Формат и что восстанавливается — SaveGame (ядро).
    /// Загрузка = текст сохранения в статик Pending + перезагрузка сцены Flight: GameBootstrap.Awake строит вселенную
    /// из него (TakePending) вместо старта миссии — виды, звук, площадка заводятся тем же путём, что при обычном старте.
    /// Компонент добавляет GameBootstrap; меню слотов рисует Esc-меню через DrawMenu.
    /// </summary>
    public sealed class SaveController : MonoBehaviour
    {
        public const string Extension = ".ksav";
        /// <summary>Файл быстрого сохранения F9 — один, перезаписывается (как в KSP); «Сохранить» в меню — новый слот.</summary>
        const string QuickName = "quicksave";
        /// <summary>Тост держится столько секунд реального времени (меню Esc ставит симуляцию на паузу, а тост — нет).</summary>
        const float ToastSeconds = 2.5f;

        // Раскладка DrawMenu. Пара: MenuHeight = Row + Gap + VisibleRows * SlotRow — меняешь строку, проверь высоту.
        const float Row = 34, Gap = 6, SlotRow = 46, LoadW = 100, DeleteW = 92;
        const int VisibleRows = 4;
        /// <summary>Высота блока DrawMenu — столько места выделить под него в меню Esc.</summary>
        public const float MenuHeight = Row + Gap + VisibleRows * SlotRow;

        /// <summary>Текст сохранения, ждущий перезагрузки сцены. Статик переживает LoadScene, как GameBootstrap.NextMissionId.</summary>
        static string pending;
        /// <summary>Сцена уже перезагружается: второй F10 или сохранение посреди перехода записали бы полумир.</summary>
        static bool loading;

        static string toast;
        static float toastUntil;

        sealed class Slot
        {
            public string Path;
            public SaveHeader Header;
            public DateTime Written;
        }
        static readonly List<Slot> slots = new List<Slot>();
        static bool slotsDirty = true;
        static Vector2 scroll;
        /// <summary>Слот, у которого нажали «Удалить» один раз: второе нажатие («Точно?») удаляет.</summary>
        static string confirmDelete;
        static GUIStyle button, small, info, toastStyle;

        public static string Directory => System.IO.Path.Combine(Application.persistentDataPath, "saves");

        void Awake()
        {
            loading = false;
            slotsDirty = true;
            confirmDelete = null;
        }

        void Update()
        {
            if (KeyBinds.Down(Bind.QuickSave)) Save(QuickName);
            else if (KeyBinds.Down(Bind.QuickLoad))
            {
                var newest = Newest();
                if (newest != null) Load(newest);
                else Toast("Сохранений нет");
            }
        }

        // ---------------------------------------------------------------- запись и чтение

        /// <summary>Записать полёт в слот name (без расширения); null — новый слот с датой в имени. false — не записано.</summary>
        public static bool Save(string name = null)
        {
            var boot = GameBootstrap.Instance;
            var u = GameBootstrap.U;
            if (loading || boot == null || u == null || u.Active == null)
            {
                Toast("Сейчас сохранить нельзя");
                return false;
            }
            try
            {
                string text = SaveGame.Save(u, boot.Tracker, GameBootstrap.NextDesign != null);
                System.IO.Directory.CreateDirectory(Directory);
                name = name ?? "save_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = System.IO.Path.Combine(Directory, name + Extension);
                // Через временный файл: падение посреди записи не портит прежний слот с тем же именем.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                slotsDirty = true;
                Toast(name == QuickName ? "Сохранено (F9)" : "Сохранено");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Toast("Не удалось сохранить: " + e.Message);
                return false;
            }
        }

        /// <summary>Загрузить слот: проверить заголовок, запомнить текст и перезапустить сцену Flight.</summary>
        public static void Load(string path)
        {
            if (loading) return;
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                Toast("Не удалось прочитать: " + e.Message);
                return;
            }
            var h = SaveGame.ReadHeader(text);
            if (h == null || MissionCatalog.Get(h.MissionId ?? "") == null)
            {
                Toast("Файл повреждён или миссия не найдена");
                return;
            }
            if (h.Version > SaveGame.Version)
            {
                Toast("Сохранение из более новой версии игры");
                return;
            }
            pending = text;
            loading = true;
            // Миссия сохранения — её же вернёт «Начать заново» после загрузки.
            GameBootstrap.NextMissionId = h.MissionId;
            var scene = SceneManager.GetActiveScene();
            // Как PauseMenu.Reload: сцена вне списка сборки (Play в редакторе) — индекс 0.
            SceneManager.LoadScene(scene.buildIndex >= 0 ? scene.buildIndex : 0);
        }

        /// <summary>
        /// Для GameBootstrap.Awake: вселенная из ждущего сохранения или null (обычный старт миссии). Ускорение — ×1:
        /// после загрузки игрок сначала смотрит, где он. Ошибка разбора — тоже null с тостом: лучше новый старт, чем пустая сцена.
        /// </summary>
        public static LoadedGame TakePending(SolarSystem system)
        {
            if (pending == null) return null;
            string text = pending;
            pending = null;
            try
            {
                var g = SaveGame.Load(text, system);
                if (g.Mission == null) throw new FormatException($"нет миссии {g.Header.MissionId}");
                if (g.Tracker == null) g.Tracker = new MissionTracker(g.Mission);
                g.Universe.SetWarp(0);
                // Своя ракета из конструктора: «Начать заново» после загрузки должен ставить её, а не исторический носитель.
                // Проект общий у всех частей ракеты — у активного борта он целый, со всеми секциями.
                if (!g.Header.CustomDesign) GameBootstrap.NextDesign = null;
                else if (GameBootstrap.NextDesign == null) GameBootstrap.NextDesign = g.Universe.Active?.Design;
                Toast(g.Dropped.Count > 0 ? "Загружено. Автопилот выключен — включите заново" : "Загружено");
                foreach (var d in g.Dropped) Debug.Log("[Kare] Не восстановлено из сохранения: " + d);
                return g;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Toast("Не удалось загрузить: " + e.Message);
                return null;
            }
        }

        static void Toast(string text)
        {
            toast = text;
            toastUntil = Time.realtimeSinceStartup + ToastSeconds;
        }

        static string Newest()
        {
            Refresh();
            return slots.Count > 0 ? slots[0].Path : null;
        }

        /// <summary>Список слотов, новые сверху. Заголовок — только первая строка файла: тело с графом не читаем.</summary>
        static void Refresh()
        {
            if (!slotsDirty) return;
            slotsDirty = false;
            slots.Clear();
            if (!System.IO.Directory.Exists(Directory)) return;
            foreach (var path in System.IO.Directory.GetFiles(Directory, "*" + Extension))
            {
                SaveHeader h = null;
                try
                {
                    using (var r = new StreamReader(path)) h = SaveGame.ReadHeader(r.ReadLine());
                }
                catch (IOException) { }
                if (h != null) slots.Add(new Slot { Path = path, Header = h, Written = File.GetLastWriteTime(path) });
            }
            slots.Sort((a, b) => b.Written.CompareTo(a.Written));
        }

        static void Delete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException e)
            {
                Toast("Не удалось удалить: " + e.Message);
            }
            slotsDirty = true;
            confirmDelete = null;
        }

        // ---------------------------------------------------------------- IMGUI

        /// <summary>
        /// Блок «Сохранения» для меню Esc (вызывать из OnGUI): кнопка «Сохранить» (новый слот), под ней — слоты, новые
        /// сверху, у каждого «Загрузить» и «Удалить». Высота блока — MenuHeight; список прокручивается.
        /// </summary>
        public static void DrawMenu(Rect area)
        {
            Styles();
            Refresh();
            float x = area.x, y = area.y, w = area.width;
            if (GUI.Button(new Rect(x, y, w, Row), "Сохранить", button)) Save();
            y += Row + Gap;
            var view = new Rect(x, y, w, area.yMax - y);
            if (slots.Count == 0)
            {
                GUI.Label(view, "Сохранений нет. F9 — быстрое сохранение, F10 — загрузить последнее.", small);
                return;
            }
            var content = new Rect(0, 0, w - 18, slots.Count * SlotRow);
            scroll = GUI.BeginScrollView(view, scroll, content);
            string load = null, del = null;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                var h = s.Header;
                float ry = i * SlotRow, tw = content.width - LoadW - DeleteW - 12;
                string name = System.IO.Path.GetFileNameWithoutExtension(s.Path) == QuickName ? "Быстрое · " : "";
                GUI.Label(new Rect(0, ry, tw, 22), $"{name}{s.Written:dd.MM HH:mm} · {h.MissionTitle}", info);
                GUI.Label(new Rect(0, ry + 20, tw, 20),
                          $"{h.Vessel}, {h.Body}, {h.Altitude / 1000:F0} км · {GameCalendar.Format(h.Time)}", small);
                if (GUI.Button(new Rect(content.width - LoadW - DeleteW - 6, ry + 4, LoadW, Row), "Загрузить", button)) load = s.Path;
                bool confirm = confirmDelete == s.Path;
                if (GUI.Button(new Rect(content.width - DeleteW, ry + 4, DeleteW, Row), confirm ? "Точно?" : "Удалить", button))
                {
                    if (confirm) del = s.Path;
                    else confirmDelete = s.Path;
                }
            }
            GUI.EndScrollView();
            // Действия — после EndScrollView: список не меняется посреди его же отрисовки.
            if (del != null) Delete(del);
            if (load != null) Load(load);
        }

        static void Styles()
        {
            if (button != null) return;
            button = new GUIStyle(GUI.skin.button) { fontSize = 15 };
            info = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip };
            small = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, clipping = TextClipping.Clip };
            small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            toastStyle = new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
        }

        void OnGUI()
        {
            if (toast == null || Time.realtimeSinceStartup > toastUntil || FlightHud.UiHidden) return;
            Styles();
            // Поверх HUD и меню: тост — ответ на нажатие клавиши, его должно быть видно в любом режиме.
            GUI.depth = -100;
            var size = toastStyle.CalcSize(new GUIContent(toast));
            float w = Mathf.Max(220, size.x + 40);
            GUI.Box(new Rect((Screen.width - w) / 2, 60, w, 44), toast, toastStyle);
        }
    }
}
