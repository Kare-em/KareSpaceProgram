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
        GUIStyle title, label, toggle, button, small, small2;

        void OnDestroy() => IsOpen = false; // статик переживает перезагрузку сцены

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) IsOpen = !IsOpen;
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

            int missionRows = (MissionCatalog.All.Count + MissionCols - 1) / MissionCols;
            float h = 70 + Row * 5 + 30 + 3 * (Row + 8) + 20 + 30 + missionRows * (Row + 4) + 16 + 30 + 2 * Row + 20 + 30 + 2 * Row + 2 * (Row + 4) + 10 + BrightnessBlock + DetailBlock;
            var r = new Rect((Screen.width - Width) / 2, (Screen.height - h) / 2, Width, h);
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
            y += Row + 20;

            // Выбор миссии: перезапуск сцены с другой миссией (MissionCatalog), например «Луна-9» для посадки.
            GUI.Label(new Rect(x, y, w, 24), "Миссия (перезапуск)", label); y += 30;
            float bw = (w - (MissionCols - 1) * 4) / MissionCols;
            for (int i = 0; i < MissionCatalog.All.Count; i++)
            {
                var m = MissionCatalog.All[i];
                var br = new Rect(x + (i % MissionCols) * (bw + 4), y + (i / MissionCols) * (Row + 4), bw, Row);
                bool cur = boot.Mission != null && boot.Mission.Id == m.Id;
                GUI.color = cur ? new Color(1f, 0.8f, 0.4f) : Color.white;
                if (GUI.Button(br, m.Title, small) && !cur)
                {
                    GameBootstrap.NextMissionId = m.Id;
                    GameBootstrap.NextDesign = null; // миссия со своим историческим носителем
                    IsOpen = false;
                    Reload();
                }
                GUI.color = Color.white;
            }
            y += missionRows * (Row + 4) + 16;

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
            BrightnessSettings.Plume = Slider(x, y, w, "Факел", BrightnessSettings.Plume,
                BrightnessSettings.PlumeMin, BrightnessSettings.PlumeMax, "×{0:0.00}"); y += Row + 6 + 20;
            if (Event.current.type == EventType.MouseUp) BrightnessSettings.Save();

            // Читы для тестов: телепорт и бесконечное топливо. Миссия при этом не засчитывается честно — это отладка.
            GUI.Label(new Rect(x, y, w, 24), "Читы (для тестов)", label); y += 30;
            boot.InfiniteFuel = GUI.Toggle(new Rect(x, y, w, Row), boot.InfiniteFuel, "  Бесконечное топливо", toggle); y += Row;
            // Зажигания у исторических двигателей считанные (Vessel.IgnitionsLeft) — тумблер снимает лимит для свободного полёта.
            Vessel.UnlimitedIgnitions = GUI.Toggle(new Rect(x, y, w, Row), Vessel.UnlimitedIgnitions,
                "  Бесконечные перезапуски двигателей", toggle); y += Row;
            var u = GameBootstrap.U;
            var earth = u?.System.Get("earth");
            var moon = u?.System.Get("moon");
            var mars = u?.System.Get("mars");
            float hw = (w - 4) / 2;
            if (GUI.Button(new Rect(x, y, hw, Row), "Орбита Земли 200 км", small)) Jump(u, earth, 200e3, true);
            if (GUI.Button(new Rect(x + hw + 4, y, hw, Row), "Орбита Луны 100 км", small)) Jump(u, moon, 100e3, true);
            y += Row + 4;
            if (GUI.Button(new Rect(x, y, hw, Row), "Над Луной 15 км (G — посадка)", small)) Jump(u, moon, 15e3, false);
            if (GUI.Button(new Rect(x + hw + 4, y, hw, Row), "Орбита Марса 300 км", small)) Jump(u, mars, 300e3, true);
        }

        /// <summary>Высота блока «Яркость»: заголовок, два ползунка, отступ. Пара: код блока в OnGUI.</summary>
        const float BrightnessBlock = 30 + 2 * (Row + 6) + 20;
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

        void Quality(float x, float y, float w, ref bool value, bool supported, string text)
        {
            if (supported) { value = GUI.Toggle(new Rect(x, y, w, Row), value, text, toggle); return; }
            GUI.enabled = false;
            GUI.Toggle(new Rect(x, y, w, Row), false, text + " — нет на этой видеокарте", toggle);
            GUI.enabled = true;
        }

        /// <summary>Сколько кнопок миссий в ряду. Пара: Width — подписи «Пролёт Луны» должны влезать.</summary>
        const int MissionCols = 4;

        void Jump(Universe u, CelestialBody body, double alt, bool orbital)
        {
            if (u == null || body == null) return;
            u.Teleport(body, alt, orbital);
            IsOpen = false;
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
        }
    }
}
