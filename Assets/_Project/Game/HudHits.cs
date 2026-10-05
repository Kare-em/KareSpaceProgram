using System.Collections.Generic;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Где на экране лежат панели IMGUI (§10.2): щелчок по панели — это щелчок по интерфейсу, а не по детали
    /// ракеты за ней (PartInspector). Панели HUD без кнопок событие мыши не съедают, поэтому их прямоугольники
    /// собираются при отрисовке (Repaint) и проверяются в Update следующего кадра. Координаты — экранные пиксели
    /// IMGUI (y вниз), с учётом масштаба GUI.matrix.
    /// </summary>
    public static class HudHits
    {
        static List<Rect> cur = new List<Rect>(64), prev = new List<Rect>(64);
        static int frame = -1;

        /// <summary>Прямоугольник в текущих координатах GUI (до GUI.matrix). Повёрнутые (линии подсказки) пропускаем.</summary>
        public static void Add(Rect r)
        {
            var e = Event.current;
            if (e == null || e.type != EventType.Repaint) return;
            if (frame != Time.frameCount)
            {
                // Прошлый кадр остаётся в prev: Update идёт раньше OnGUI, и проверяет он то, что нарисовано кадром раньше.
                var t = prev; prev = cur; cur = t;
                cur.Clear();
                frame = Time.frameCount;
            }
            var m = GUI.matrix;
            if (Mathf.Abs(m.m01) > 1e-4f || Mathf.Abs(m.m10) > 1e-4f) return;
            Vector2 a = m.MultiplyPoint3x4(r.min), b = m.MultiplyPoint3x4(r.max);
            cur.Add(Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y)));
        }

        /// <summary>Мышь (Input.mousePosition, y вверх) над какой-нибудь панелью последних двух кадров.</summary>
        public static bool Contains(Vector3 mouse)
        {
            // Старше двух кадров — панели уже нет (закрыли меню): не держим «призраков».
            if (Time.frameCount - frame > 2) return false;
            var p = new Vector2(mouse.x, Screen.height - mouse.y);
            foreach (var r in cur) if (r.Contains(p)) return true;
            foreach (var r in prev) if (r.Contains(p)) return true;
            return false;
        }
    }
}
