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
        GUIStyle title, label, toggle, button, small;

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
            float h = 70 + Row * 5 + 30 + 2 * (Row + 8) + 20 + 30 + missionRows * (Row + 4) + 16 + 30 + Row + 2 * (Row + 4) + 10;
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
            boot.AscentTutor = GUI.Toggle(new Rect(x, y, w, Row), boot.AscentTutor, "  Подсказка по углу на взлёте", toggle); y += Row + 20;

            if (GUI.Button(new Rect(x, y, w, Row), "Продолжить (Esc)", button)) IsOpen = false;
            y += Row + 8;
            if (GUI.Button(new Rect(x, y, w, Row), "Начать миссию заново", button))
            {
                IsOpen = false;
                Reload();
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
                    IsOpen = false;
                    Reload();
                }
                GUI.color = Color.white;
            }
            y += missionRows * (Row + 4) + 16;

            // Читы для тестов: телепорт и бесконечное топливо. Миссия при этом не засчитывается честно — это отладка.
            GUI.Label(new Rect(x, y, w, 24), "Читы (для тестов)", label); y += 30;
            boot.InfiniteFuel = GUI.Toggle(new Rect(x, y, w, Row), boot.InfiniteFuel, "  Бесконечное топливо", toggle); y += Row;
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
        }
    }
}
