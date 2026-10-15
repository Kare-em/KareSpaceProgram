using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Esc → «Управление» (§10.1): все действия по группам, клик по клавише — ждём новую, Esc — отмена.
    /// Хранение и подписи — KeyBinds; подсказки HUD и карты берут подписи оттуда же, поэтому меняются сразу.
    /// Клавишу ловим в Update перебором KeyCode (а не Event.current в OnGUI): IMGUI на Windows не присылает
    /// KeyDown для одиночных Shift/Ctrl/Alt, а газ по умолчанию висит именно на них.
    /// </summary>
    public static class ControlsMenu
    {
        public static bool Open;
        static Bind? waiting;
        static Vector2 scroll;
        static GUIStyle title, head, row, key, keyWait, note, button;

        const float Width = 980, RowH = 28, HeadH = 34, KeyW = 150, ColGap = 30;

        /// <summary>Клавиши, которые можно назначить: всё до мыши (Mouse0 = 323), без None. Джойстики — после мыши.</summary>
        static readonly KeyCode[] Assignable = System.Enum.GetValues(typeof(KeyCode)).Cast<KeyCode>()
            .Where(k => k != KeyCode.None && k < KeyCode.Mouse0).Distinct().ToArray();

        /// <summary>Из PauseMenu.Update: true — кадр занят ожиданием клавиши (Esc меню при этом не закрывает).</summary>
        public static bool Poll()
        {
            if (waiting == null) return false;
            if (Input.GetKeyDown(KeyCode.Escape)) { waiting = null; return true; }
            foreach (var k in Assignable)
                if (k != KeyCode.Escape && Input.GetKeyDown(k)) { KeyBinds.Set(waiting.Value, k); waiting = null; break; }
            return true;
        }

        public static void Close()
        {
            Open = false;
            waiting = null;
        }

        public static void Draw()
        {
            Styles();
            var groups = new List<KeyValuePair<string, List<KeyBinds.Entry>>>();
            foreach (var e in KeyBinds.All)
            {
                if (groups.Count == 0 || groups[groups.Count - 1].Key != e.Group)
                {
                    var found = groups.FindIndex(g => g.Key == e.Group);
                    if (found < 0) groups.Add(new KeyValuePair<string, List<KeyBinds.Entry>>(e.Group, new List<KeyBinds.Entry>()));
                }
                groups.First(g => g.Key == e.Group).Value.Add(e);
            }

            // Раскладка в две колонки: группа целиком — в ту, что сейчас короче (группы не рвём посередине).
            float colW = (Width - 40 - ColGap) / 2;
            var col = new float[2];
            var place = new List<(int col, float y, KeyValuePair<string, List<KeyBinds.Entry>> g)>();
            foreach (var g in groups)
            {
                int c = col[0] <= col[1] ? 0 : 1;
                place.Add((c, col[c], g));
                col[c] += HeadH + g.Value.Count * RowH + 10;
            }
            float content = Mathf.Max(col[0], col[1]);

            float footer = 110;
            float h = Mathf.Min(Screen.height - 40, 70 + content + footer);
            var r = new Rect((Screen.width - Width) / 2, (Screen.height - h) / 2, Width, h);
            GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.96f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUI.Label(new Rect(r.x, r.y + 14, Width, 32), "УПРАВЛЕНИЕ", title);
            var view = new Rect(r.x + 20, r.y + 60, Width - 40, h - 60 - footer);
            scroll = GUI.BeginScrollView(view, scroll, new Rect(0, 0, Width - 60, content));
            foreach (var p in place)
            {
                float x = p.col * (colW + ColGap), y = p.y;
                GUI.Label(new Rect(x, y, colW, HeadH), p.g.Key, head);
                y += HeadH;
                foreach (var e in p.g.Value)
                {
                    DrawRow(new Rect(x, y, colW, RowH), e);
                    y += RowH;
                }
            }
            GUI.EndScrollView();

            float fy = r.yMax - footer + 8;
            GUI.Label(new Rect(r.x + 20, fy, Width - 40, 40),
                "Клик по клавише — нажмите новую (Esc — отмена). Оранжевая — занята другим действием. " +
                "Shift — «назад» у камеры и фокуса карты, Alt — триммер (с тангажом) и точная правка манёвра, стрелки — поворот в ручной стыковке.", note);
            fy += 50;
            float bw = 220;
            if (GUI.Button(new Rect(r.x + 20, fy, bw, 34), "Сбросить всё", button)) { KeyBinds.ResetAll(); waiting = null; }
            if (GUI.Button(new Rect(r.xMax - 20 - bw, fy, bw, 34), "Назад (Esc)", button)) Close();
        }

        static void DrawRow(Rect r, KeyBinds.Entry e)
        {
            var conflicts = KeyBinds.Conflicts(e.Bind).ToList();
            string text = e.Title;
            if (conflicts.Count > 0) text += $"  <color=#ffa040>· также: {string.Join(", ", conflicts.Select(KeyBinds.Title))}</color>";
            GUI.Label(new Rect(r.x, r.y, r.width - KeyW - 8, r.height), text, row);

            bool wait = waiting == e.Bind;
            string label = wait ? "нажмите…" : KeyBinds.Label(e.Bind);
            if (!wait && e.Alt != KeyCode.None) label += " / " + KeyBinds.Label(e.Alt);
            GUI.color = wait ? new Color(1f, 0.85f, 0.4f) : conflicts.Count > 0 ? new Color(1f, 0.62f, 0.3f) : Color.white;
            if (GUI.Button(new Rect(r.xMax - KeyW, r.y + 2, KeyW, r.height - 4), label, wait ? keyWait : key))
                waiting = wait ? (Bind?)null : e.Bind;
            GUI.color = Color.white;
        }

        static void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            head = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.LowerLeft };
            head.normal.textColor = new Color(0.6f, 0.75f, 1f);
            row = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleLeft, richText = true, clipping = TextClipping.Clip };
            row.normal.textColor = Color.white;
            key = new GUIStyle(GUI.skin.button) { fontSize = 14 };
            keyWait = new GUIStyle(key) { fontStyle = FontStyle.Italic };
            note = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            note.normal.textColor = new Color(0.65f, 0.7f, 0.8f);
            button = new GUIStyle(GUI.skin.button) { fontSize = 16 };
        }
    }
}
