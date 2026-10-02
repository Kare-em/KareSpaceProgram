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
        GUIStyle title, label, toggle, button;

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

            float h = 70 + Row * 5 + 30 + 2 * (Row + 8) + 20;
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
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex >= 0
                    ? SceneManager.GetActiveScene().buildIndex : 0);
            }
        }

        void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            label = new GUIStyle(GUI.skin.label) { fontSize = 15 };
            label.normal.textColor = new Color(0.6f, 0.75f, 1f);
            toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 16 };
            toggle.normal.textColor = toggle.onNormal.textColor = toggle.hover.textColor = toggle.onHover.textColor = Color.white;
            button = new GUIStyle(GUI.skin.button) { fontSize = 16 };
        }
    }
}
