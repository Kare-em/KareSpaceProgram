using Kare.Space.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kare.Space.Game
{
    /// <summary>
    /// Меню по Esc (GDD §4.7): пауза и правила полёта — разрушение от аэронагрузки и перегрева, предел перегрузок.
    /// Правила живут в GameBootstrap и каждый кадр передаются в ядро; меню их только переключает.
    /// Пока меню открыто, GameBootstrap не двигает время, а FlightInput не читает клавиши.
    /// </summary>
    public sealed class PauseMenu : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }

        const float Width = 420, Row = 34;
        GUIStyle title, label, toggle, button, small, small2, credit;

        void OnDestroy() => IsOpen = false; // статик переживает перезагрузку сцены

        // Карьера (GDD §8): оплата пуска, наука и контракты полёта — компонент рядом с меню, GameBootstrap не трогаем.
        void Awake() { if (GetComponent<CareerFlight>() == null) gameObject.AddComponent<CareerFlight>(); }

        void Update()
        {
            // Ожидание новой клавиши в «Управлении» забирает кадр целиком: Esc там — отмена, а не выход из меню.
            if (ControlsMenu.Poll()) return;
            if (!Input.GetKeyDown(KeyCode.Escape)) return;
            // Esc из таблицы миссий и из «Управления» возвращает в меню, а не в полёт.
            if (IsOpen && MissionPicker.Open) MissionPicker.Open = false;
            else if (IsOpen && CareerWindow.Open) CareerWindow.Open = false;
            else if (IsOpen && ControlsMenu.Open) ControlsMenu.Close();
            else if (IsOpen && savesOpen) savesOpen = false;
            else if (IsOpen && OrbitSpawnMenu.Open) OrbitSpawnMenu.Open = false;
            else { IsOpen = !IsOpen; MissionPicker.Open = false; CareerWindow.Open = false; ControlsMenu.Close(); savesOpen = false; OrbitSpawnMenu.Open = false; }
        }

        static bool savesOpen;

        /// <summary>Окно сохранений: список слотов SaveController (F9 — быстрое, F10 — загрузить последнее).</summary>
        void DrawSaves()
        {
            float h = 50 + SaveController.MenuHeight + 16 + Row + 16;
            var r = new Rect((Screen.width - Width) / 2, (Screen.height - h) / 2, Width, h);
            GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.95f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
            float x = r.x + 20, y = r.y + 12, w = Width - 40;
            GUI.Label(new Rect(x, y, w, 30), "СОХРАНЕНИЯ", title); y += 38;
            SaveController.DrawMenu(new Rect(x, y, w, SaveController.MenuHeight));
            y += SaveController.MenuHeight + 16;
            if (GUI.Button(new Rect(x, y, w, Row), $"Назад (Esc) · {KeyBinds.Label(Bind.QuickSave)} — быстрое, {KeyBinds.Label(Bind.QuickLoad)} — загрузить", button)) savesOpen = false;
        }

        void OnGUI()
        {
            if (!IsOpen) return;
            var boot = GameBootstrap.Instance;
            if (boot == null) return;
            Styles();

            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Выбор миссии — отдельная таблица (MissionPicker) поверх затемнения вместо панели паузы.
            if (MissionPicker.Open)
            {
                MissionPicker.Draw(boot.Mission, SelectMission);
                return;
            }
            if (CareerWindow.Open)
            {
                CareerWindow.Draw(SelectMission);
                return;
            }
            if (ControlsMenu.Open)
            {
                ControlsMenu.Draw();
                return;
            }
            if (savesOpen)
            {
                DrawSaves();
                return;
            }
            if (OrbitSpawnMenu.Open)
            {
                OrbitSpawnMenu.Draw(GameBootstrap.U, () => IsOpen = false);
                return;
            }

            float h = 70 + Row * 5 + 30 + 5 * (Row + 8) + 20 + 30 + (Row + 4) + 16 + 30 + 2 * Row + 20 + CareerBlock + BrightnessBlock + SoundBlock + DetailBlock + CreditsBlock;
            // Панель выше окна (1250 px было при 1080) — низ с читами уходил за экран и кнопку «На орбиту…» не находили.
            // Не влезает — ужимаем всю панель матрицей GUI: мышь пересчитывается ею же, раскладку трогать не нужно.
            float scale = Mathf.Min(1, (Screen.height - 2 * ScreenMargin) / h);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            var r = new Rect((Screen.width / scale - Width) / 2, (Screen.height / scale - h) / 2, Width, h);
            GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.95f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float x = r.x + 20, y = r.y + 16, w = Width - 40;
            GUI.Label(new Rect(x, y, w, 30), "ПАУЗА", title); y += 40;
            GUI.Label(new Rect(x, y, w, 24), "Правила полёта", label); y += 30;
            boot.AeroBreakup = GUI.Toggle(new Rect(x, y, w, Row), boot.AeroBreakup, "  Разрушение от аэронагрузки (Qα)", toggle); y += Row;
            boot.HeatDamage = GUI.Toggle(new Rect(x, y, w, Row), boot.HeatDamage, "  Разрушение от перегрева", toggle); y += Row;
            boot.GLoadLimit = GUI.Toggle(new Rect(x, y, w, Row), boot.GLoadLimit, "  Гибель экипажа от перегрузки (> 9 g, 10 с)", toggle); y += Row;
            boot.AscentTutor = GUI.Toggle(new Rect(x, y, w, Row), boot.AscentTutor, "  Подсказки по полёту (тутор)", toggle); y += Row;
            boot.AutoWarp = GUI.Toggle(new Rect(x, y, w, Row), boot.AutoWarp, "  Автопилот сам управляет ускорением", toggle); y += Row + 20;

            if (GUI.Button(new Rect(x, y, w, Row), "Продолжить (Esc)", button)) IsOpen = false;
            y += Row + 8;
            if (GUI.Button(new Rect(x, y, w, Row), "Начать миссию заново", button))
            {
                IsOpen = false;
                Reload();
            }
            y += Row + 8;
            // Конструктор (§5.4) — своя сцена; миссия и выбранная ракета запоминаются в статиках.
            if (GUI.Button(new Rect(x, y, w, Row), GameBootstrap.NextDesign != null ? "В конструктор (своя ракета)" : "В конструктор ракет", button))
            {
                IsOpen = false;
                HangarController.ReturnMissionId = boot.Mission?.Id;
                SceneManager.LoadScene(HangarController.SceneName);
            }
            y += Row + 8;
            // Клавиши (§10.1): переназначение, подсказки HUD берут подписи из KeyBinds.
            // Сохранения — своим окном поверх (как «Управление»): панель паузы и так почти во весь экран.
            float half = (w - 8) / 2;
            if (GUI.Button(new Rect(x, y, half, Row), "Управление (клавиши)…", button)) ControlsMenu.Open = true;
            if (GUI.Button(new Rect(x + half + 8, y, half, Row), "Сохранения…", button)) savesOpen = true;
            y += Row + 8;
            // Читы и телепорт (§4.7) — своим окном (OrbitSpawnMenu): быстрые орбиты, любое тело, топливо и перезапуски.
            if (GUI.Button(new Rect(x, y, w, Row), "Читы и телепорт: орбита любого тела…", button)) OrbitSpawnMenu.Open = true;
            y += Row + 20;

            // Выбор миссии: таблица с описаниями и схемами полёта (MissionPicker), перезапуск сцены с другой миссией.
            GUI.Label(new Rect(x, y, w, 24), "Миссия (перезапуск)", label); y += 30;
            if (GUI.Button(new Rect(x, y, w, Row), $"{boot.Mission?.Title ?? "—"}  —  выбрать из таблицы…", button))
                MissionPicker.Open = true;
            y += Row + 4 + 16;

            // Режим (§8.5): песочница по умолчанию, карьера — явный выбор; засчитывается с ближайшего пуска.
            GUI.Label(new Rect(x, y, w, 24), CareerStore.Active ? "Режим: карьера (с ближайшего пуска)" : "Режим: песочница", label); y += 30;
            float third = (w - 16) / 3;
            GUI.color = CareerStore.Active ? Color.white : new Color(1f, 0.8f, 0.4f);
            if (GUI.Button(new Rect(x, y, third, Row), "Песочница", button) && CareerStore.Active) CareerStore.Leave();
            GUI.color = CareerStore.Active ? new Color(1f, 0.8f, 0.4f) : Color.white;
            if (GUI.Button(new Rect(x + third + 8, y, third, Row), "Карьера", button) && !CareerStore.Active) CareerStore.Enter(false);
            GUI.color = Color.white;
            if (GUI.Button(new Rect(x + 2 * (third + 8), y, third, Row), "Карьера…", button)) CareerWindow.Open = true;
            y += Row + 20;

            // Графика (GDD §9): RT и DLSS — RenderQuality применяет на лету, без перезапуска.
            GUI.Label(new Rect(x, y, w, 24), "Графика", label); y += 30;
            Quality(x, y, w, ref RenderQuality.RayTracing, RenderQuality.RayTracingSupported,
                "  Трассировка лучей (тени Солнца, отражения)"); y += Row;
            Quality(x, y, w, ref RenderQuality.Dlss, RenderQuality.DlssSupported, "  DLSS (апскейл NVIDIA)"); y += Row + 4;
            // Детализация планет, облаков и орбит карты (§9.4, §9.6): тела перестраиваются сразу, миссия не сбрасывается.
            GUI.Label(new Rect(x, y, DetailLabel, Row), "Детализация", toggle);
            float dw = (w - DetailLabel - 3 * 4) / DetailSettings.Names.Length;
            for (int i = 0; i < DetailSettings.Names.Length; i++)
            {
                bool cur = DetailSettings.Level == i;
                GUI.color = cur ? new Color(1f, 0.8f, 0.4f) : Color.white;
                if (GUI.Button(new Rect(x + DetailLabel + i * (dw + 4), y, dw, Row), DetailSettings.Names[i], small) && !cur)
                {
                    DetailSettings.Level = i;
                    if (BodyRenderer.Instance != null) BodyRenderer.Instance.ApplyDetail();
                }
                GUI.color = Color.white;
            }
            y += Row + 20;

            // Яркость (§9.3): общая — компенсация экспозиции, факел — отдельно (ночью его легко пересветить).
            // Значения в PlayerPrefs, применяют SkyController и VesselView; сохраняем при отпускании кнопки мыши.
            GUI.Label(new Rect(x, y, w, 24), "Яркость", label); y += 30;
            BrightnessSettings.Ev = Slider(x, y, w, "Общая", BrightnessSettings.Ev,
                BrightnessSettings.EvMin, BrightnessSettings.EvMax, "{0:+0.0;-0.0;0} EV"); y += Row + 6;
            // Факел — по логарифму: ×0,05…4 — это 6 ступеней EV, линейно весь нижний край ушёл бы в пару пикселей.
            BrightnessSettings.Plume = SliderLog(x, y, w, "Факел", BrightnessSettings.Plume,
                BrightnessSettings.PlumeMin, BrightnessSettings.PlumeMax, "×{0:0.00}"); y += Row + 6;
            // Ночь — подсветка ландшафта (NightLight): грунт виден и при факеле, и у прожекторов стола.
            BrightnessSettings.Night = Slider(x, y, w, "Ночь", BrightnessSettings.Night,
                BrightnessSettings.NightMin, BrightnessSettings.NightMax, "{0:P0}"); y += Row + 6;
            // Блик Солнца (SunFlare) — то же, что клавиша Bind.SunGlare.
            bool glare = GUI.Toggle(new Rect(x, y, w, Row), BrightnessSettings.SunGlare,
                $"  Блик Солнца в объективе ({KeyBinds.Label(Bind.SunGlare)})", toggle); y += Row + 20;
            if (glare != BrightnessSettings.SunGlare) { BrightnessSettings.SunGlare = glare; BrightnessSettings.Save(); }
            if (Event.current.type == EventType.MouseUp) BrightnessSettings.Save();

            // Звук (§9.3): общая громкость синтезированного звука полёта, применяет FlightAudio через AudioListener.
            GUI.Label(new Rect(x, y, w, 24), "Звук", label); y += 30;
            SoundSettings.Volume = Slider(x, y, w, "Громкость", SoundSettings.Volume, 0, 1, "{0:P0}"); y += Row + 6 + 20;
            if (Event.current.type == EventType.MouseUp) SoundSettings.Save();

            // Атрибуция карт тел — условие лицензии CC BY 4.0 (BodyRenderer.Maps).
            GUI.Label(new Rect(x, y, w, CreditsBlock), Credits, credit);
            GUI.matrix = Matrix4x4.identity;
        }

        /// <summary>Поля над и под панелью паузы, когда её ужимает под высоту окна, px.</summary>
        const float ScreenMargin = 10;

        /// <summary>Строка атрибуции карт тел и её высота. Пара: код строки в OnGUI.</summary>
        const string Credits = "Карты планет: Solar System Scope (solarsystemscope.com), CC BY 4.0. Луна: NASA SVS / LRO. Карты спутников: NASA/JPL/USGS (public domain).";
        const float CreditsBlock = 36;
        /// <summary>Блок «Режим»: подпись, ряд кнопок, отступ. Пара: код блока в OnGUI.</summary>
        const float CareerBlock = 30 + Row + 20;

        /// <summary>Высота блока «Яркость»: заголовок, два ползунка, отступ. Пара: код блока в OnGUI.</summary>
        const float BrightnessBlock = 30 + 3 * (Row + 6) + Row + 20;
        const float SoundBlock = 30 + (Row + 6) + 20;
        /// <summary>Строка «Детализация» в блоке «Графика» и ширина её подписи. Пара: код строки в OnGUI.</summary>
        const float DetailBlock = Row + 4, DetailLabel = 110;

        /// <summary>Подпись слева, ползунок посередине, значение справа.</summary>
        float Slider(float x, float y, float w, string text, float value, float min, float max, string format)
        {
            GUI.Label(new Rect(x, y, 90, Row), text, toggle);
            value = GUI.HorizontalSlider(new Rect(x + 95, y + Row * 0.5f - 6, w - 95 - 80, 20), value, min, max);
            GUI.Label(new Rect(x + w - 75, y, 75, Row), string.Format(format, value), small2);
            return value;
        }

        /// <summary>Логарифмический ползунок: положение — log2 значения, подпись и результат — само значение.</summary>
        float SliderLog(float x, float y, float w, string text, float value, float min, float max, string format)
        {
            GUI.Label(new Rect(x, y, 90, Row), text, toggle);
            float v = Mathf.Pow(2, GUI.HorizontalSlider(new Rect(x + 95, y + Row * 0.5f - 6, w - 95 - 80, 20),
                Mathf.Log(value, 2), Mathf.Log(min, 2), Mathf.Log(max, 2)));
            GUI.Label(new Rect(x + w - 75, y, 75, Row), string.Format(format, v), small2);
            return v;
        }

        void Quality(float x, float y, float w, ref bool value, bool supported, string text)
        {
            if (supported) { value = GUI.Toggle(new Rect(x, y, w, Row), value, text, toggle); return; }
            GUI.enabled = false;
            GUI.Toggle(new Rect(x, y, w, Row), false, text + " — нет на этой видеокарте", toggle);
            GUI.enabled = true;
        }

        /// <summary>Перезапуск сцены с выбранной миссией и её историческим носителем.</summary>
        static void SelectMission(string id)
        {
            GameBootstrap.NextMissionId = id;
            GameBootstrap.NextDesign = null; // миссия со своим историческим носителем
            MissionPicker.Open = false;
            IsOpen = false;
            Reload();
        }

        static void Reload() =>
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex >= 0 ? SceneManager.GetActiveScene().buildIndex : 0);

        void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            label = new GUIStyle(GUI.skin.label) { fontSize = 15 };
            label.normal.textColor = new Color(0.6f, 0.75f, 1f);
            toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 16 };
            toggle.normal.textColor = toggle.onNormal.textColor = toggle.hover.textColor = toggle.onHover.textColor = Color.white;
            button = new GUIStyle(GUI.skin.button) { fontSize = 16 };
            small = new GUIStyle(GUI.skin.button) { fontSize = 13, wordWrap = true };
            small2 = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleRight };
            small2.normal.textColor = Color.white;
            credit = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            credit.normal.textColor = new Color(0.6f, 0.65f, 0.75f);
        }
    }
}
