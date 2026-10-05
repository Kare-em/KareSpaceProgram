using System;
using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Таблица выбора готовых миссий (GDD §7.3, §10.2): открывается из меню Esc, строки по дате старта.
    /// Щелчок по строке — перезапуск сцены с миссией (как прежняя сетка кнопок), наведение на «?» — подсказка
    /// с подробным описанием (MissionInfoCatalog) и схемой траектории не в масштабе (MissionSketch).
    /// </summary>
    public static class MissionPicker
    {
        public static bool Open;
        /// <summary>Отладка и скриншоты: показать подсказку этой миссии без мыши (курсор в Game view через MCP не двигается).</summary>
        public static string DebugTipId;

        /// <summary>Ширины колонок, пикс.; последняя («Кратко») забирает остаток. Пара: MaxWidth — на 1080p
        /// «Кратко» должно оставаться ≥ 400 пикс., иначе описание режется на полуслове.</summary>
        const float QCol = 40, DateCol = 104, NameCol = 200, AgencyCol = 200, CraftCol = 260, GoalCol = 230;
        const float MaxWidth = 1560, RowH = 34, HeaderH = 30, TipW = 480;

        static Vector2 scroll;
        static List<MissionDef> sorted;
        static GUIStyle title, hint, head, cell, cellCur, q, tipTitle, tipSub, tipText, tipObj, sketchLabel, close;

        /// <summary>Миссии по дате старта: так таблица читается как хроника гонки (§6.1), независимо от порядка в каталоге.</summary>
        static List<MissionDef> Sorted()
        {
            if (sorted != null && sorted.Count == MissionCatalog.All.Count) return sorted;
            sorted = new List<MissionDef>(MissionCatalog.All);
            sorted.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            return sorted;
        }

        public static void Draw(MissionDef current, Action<string> select)
        {
            Styles();
            var list = Sorted();
            var sys = GameBootstrap.U?.System;
            float w = Mathf.Min(Screen.width - 40, MaxWidth);
            float h = Mathf.Min(Screen.height - 40, 64 + HeaderH + list.Count * RowH + 56);
            var r = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
            Fill(r, new Color(0.06f, 0.08f, 0.12f, 0.97f));

            float x = r.x + 16, y = r.y + 12, iw = w - 32;
            GUI.Label(new Rect(x, y, iw, 30), "ВЫБОР МИССИИ", title);
            if (GUI.Button(new Rect(r.xMax - 16 - 120, y, 120, 30), "Назад (Esc)", close)) Open = false;
            y += 34;
            GUI.Label(new Rect(x, y, iw, 20), "Щелчок по строке — старт миссии с начала.  Наведите на «?» — подробности и схема полёта.", hint);
            y += 26;

            // Шапка.
            float[] cols = Columns(iw);
            string[] names = { "", "Дата", "Миссия", "Страна · агентство", "Аппарат / носитель", "Цель", "Кратко" };
            Fill(new Rect(x, y, iw, HeaderH), new Color(0.12f, 0.16f, 0.24f));
            float cx = x;
            for (int c = 0; c < cols.Length; c++) { GUI.Label(new Rect(cx + 6, y, cols[c] - 8, HeaderH), names[c], head); cx += cols[c]; }
            y += HeaderH;

            var outerMouse = Event.current.mousePosition;
            var view = new Rect(x, y, iw, r.yMax - 12 - y);
            float contentH = list.Count * RowH;
            bool hasBar = contentH > view.height;
            scroll = GUI.BeginScrollView(view, scroll, new Rect(0, 0, iw - (hasBar ? 18 : 0), contentH));
            bool inView = view.Contains(outerMouse);
            var mouse = Event.current.mousePosition;
            MissionDef tip = null;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                var info = MissionInfoCatalog.Get(m);
                var row = new Rect(0, i * RowH, iw - (hasBar ? 18 : 0), RowH);
                bool hover = inView && row.Contains(mouse);
                bool cur = current != null && current.Id == m.Id;
                var qr = new Rect(row.x + 8, row.y + 5, QCol - 16, RowH - 10);
                bool qHover = hover && qr.Contains(mouse);
                if (qHover) tip = m;
                else if (tip == null && m.Id == DebugTipId) { tip = m; outerMouse = new Vector2(view.x + qr.center.x, view.y + qr.center.y - scroll.y); }

                Fill(row, cur ? new Color(0.35f, 0.25f, 0.08f, 0.55f)
                    : hover && !qHover ? new Color(0.25f, 0.4f, 0.65f, 0.45f)
                    : i % 2 == 0 ? new Color(1, 1, 1, 0.03f) : new Color(0, 0, 0, 0));

                GUI.color = qHover ? new Color(1f, 0.8f, 0.4f) : Color.white;
                GUI.Box(qr, "?", q);
                GUI.color = Color.white;

                var st = cur ? cellCur : cell;
                cx = row.x + cols[0];
                GameCalendar.ToDate(m.StartTime, out int yy, out int mo, out int dd, out _, out _, out _);
                GUI.Label(new Rect(cx + 6, row.y, cols[1] - 8, RowH), $"{dd:00}.{mo:00}.{yy}", st); cx += cols[1];
                GUI.Label(new Rect(cx + 6, row.y, cols[2] - 8, RowH), cur ? m.Title + "  ◄" : m.Title, st); cx += cols[2];
                Fill(new Rect(cx + 6, row.y + RowH / 2 - 5, 10, 10), CountryColor(info.Agency));
                GUI.Label(new Rect(cx + 22, row.y, cols[3] - 24, RowH), info.Agency, st); cx += cols[3];
                GUI.Label(new Rect(cx + 6, row.y, cols[4] - 8, RowH), info.Craft, st); cx += cols[4];
                GUI.Label(new Rect(cx + 6, row.y, cols[5] - 8, RowH), MissionProfile.Of(m).Goal(sys), st); cx += cols[5];
                GUI.Label(new Rect(cx + 6, row.y, cols[6] - 8, RowH), info.Short, st);

                var e = Event.current;
                if (hover && !qHover && e.type == EventType.MouseDown && e.button == 0)
                {
                    e.Use();
                    if (!cur) select(m.Id);
                }
            }
            GUI.EndScrollView();

            if (tip != null) Tooltip(tip, outerMouse, sys);
        }

        static float[] Columns(float iw)
        {
            float rest = iw - (QCol + DateCol + NameCol + AgencyCol + CraftCol + GoalCol);
            return new[] { QCol, DateCol, NameCol, AgencyCol, CraftCol, GoalCol, Mathf.Max(120, rest) };
        }

        /// <summary>Подсказка справа от «?»: заголовок, страна и дата, абзац, схема, условия миссии.</summary>
        static void Tooltip(MissionDef m, Vector2 mouse, SolarSystem sys)
        {
            var info = MissionInfoCatalog.Get(m);
            var sketch = MissionSketch.Get(m);
            float iw = TipW - 24;
            string sub = $"{info.Agency}  ·  {info.Craft}  ·  старт {GameCalendar.Format(m.StartTime)} UTC";
            var objectives = new List<string>();
            for (int i = 0; i < m.Objectives.Count; i++) objectives.Add($"{i + 1}. {m.Objectives[i].Describe(sys)}");
            string obj = string.Join("\n", objectives);

            float hSub = tipSub.CalcHeight(new GUIContent(sub), iw);
            float hText = tipText.CalcHeight(new GUIContent(info.Long), iw);
            float hObj = tipObj.CalcHeight(new GUIContent(obj), iw);
            float sw = Mathf.Min(MissionSketch.W, iw), sh = sw * MissionSketch.H / MissionSketch.W;
            float h = 12 + 28 + hSub + 6 + hText + 10 + sh + 20 + hObj + 14;

            float x = mouse.x + 24, y = mouse.y - 40;
            if (x + TipW > Screen.width - 8) x = mouse.x - 24 - TipW;
            y = Mathf.Clamp(y, 8, Mathf.Max(8, Screen.height - h - 8));
            var r = new Rect(x, y, TipW, h);
            Fill(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), new Color(0.5f, 0.65f, 0.9f, 0.8f));
            Fill(r, new Color(0.04f, 0.055f, 0.09f, 0.98f));

            float cx = x + 12, cy = y + 12;
            GUI.Label(new Rect(cx, cy, iw, 28), m.Title, tipTitle); cy += 28;
            GUI.Label(new Rect(cx, cy, iw, hSub), sub, tipSub); cy += hSub + 6;
            GUI.Label(new Rect(cx, cy, iw, hText), info.Long, tipText); cy += hText + 10;

            var sr = new Rect(cx + (iw - sw) / 2, cy, sw, sh);
            GUI.DrawTexture(sr, sketch.Texture);
            float k = sw / MissionSketch.W;
            foreach (var (pos, text) in sketch.Labels)
                GUI.Label(new Rect(sr.x + pos.x * k, sr.y + pos.y * k, 200, 18), text, sketchLabel);
            cy += sh + 2;
            GUI.Label(new Rect(cx, cy, iw, 18), "Схема не в масштабе", tipSub); cy += 18;
            GUI.Label(new Rect(cx, cy, iw, hObj), obj, tipObj);
        }

        /// <summary>Метка страны: СССР/Россия — красная, США — синяя, учебная — серая.</summary>
        static Color CountryColor(string agency)
        {
            if (agency.StartsWith("СССР") || agency.StartsWith("Россия")) return new Color(0.85f, 0.2f, 0.2f);
            if (agency.StartsWith("США")) return new Color(0.3f, 0.5f, 0.95f);
            return new Color(0.55f, 0.55f, 0.6f);
        }

        static void Fill(Rect r, Color c)
        {
            if (c.a <= 0) return;
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        static void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
            hint = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            hint.normal.textColor = new Color(0.6f, 0.68f, 0.8f);
            head = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            head.normal.textColor = new Color(0.6f, 0.75f, 1f);
            cell = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            cell.normal.textColor = Color.white;
            cellCur = new GUIStyle(cell) { fontStyle = FontStyle.Bold };
            cellCur.normal.textColor = new Color(1f, 0.82f, 0.45f);
            q = new GUIStyle(GUI.skin.box) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            q.normal.textColor = Color.white;
            close = new GUIStyle(GUI.skin.button) { fontSize = 15 };
            tipTitle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
            tipTitle.normal.textColor = new Color(1f, 0.85f, 0.5f);
            tipSub = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            tipSub.normal.textColor = new Color(0.6f, 0.68f, 0.8f);
            tipText = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
            tipText.normal.textColor = new Color(0.92f, 0.94f, 0.98f);
            tipObj = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            tipObj.normal.textColor = new Color(0.7f, 0.9f, 0.75f);
            sketchLabel = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = false };
            sketchLabel.normal.textColor = new Color(0.88f, 0.9f, 0.95f);
        }
    }
}
