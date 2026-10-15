using System;
using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kare.Space.Game
{
    /// <summary>
    /// Окно карьеры (GDD §8, §10.2) из меню Esc: казна/наука/престиж и дата, контракты-вехи с наградами (§7.3),
    /// дерево технологий (§8.2) и журнал. Рисуется поверх затемнения паузы, как MissionPicker.
    /// </summary>
    public static class CareerWindow
    {
        public static bool Open;

        const float MaxWidth = 1180, MaxHeight = 860, RowC = 58, RowT = 78, BtnW = 150;
        /// <summary>Строк журнала в окне. Пара: CareerState.LogSize — больше журнал не хранит.</summary>
        const int LogLines = 40;

        enum Tab { Contracts, Tech, Log }
        static Tab tab;
        static Vector2 scroll;
        static bool confirmNew;
        static readonly Dictionary<string, string> partNames = new Dictionary<string, string>();
        static GUIStyle title, stats, tabOn, tabOff, name, sub, ok, warn, btn, close;

        public static void Draw(Action<string> flyHistoric)
        {
            Styles();
            var s = Career.Current;
            float w = Mathf.Min(Screen.width - 40, MaxWidth), h = Mathf.Min(Screen.height - 40, MaxHeight);
            var r = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
            Fill(r, new Color(0.06f, 0.08f, 0.12f, 0.97f));
            float x = r.x + 16, y = r.y + 12, iw = w - 32;
            GUI.Label(new Rect(x, y, iw, 30), "КАРЬЕРА", title);
            if (GUI.Button(new Rect(r.xMax - 16 - 120, y, 120, 30), "Назад (Esc)", close)) Open = false;
            y += 38;

            if (s == null)
            {
                GUI.Label(new Rect(x, y, iw, 60), "Сейчас режим песочницы: все детали и миссии открыты, денег и науки нет.\n" +
                    "Карьера — гонка с 1957 года: бюджет, контракты-вехи, наука и дерево технологий.", sub);
                y += 70;
                if (GUI.Button(new Rect(x, y, 320, 36), CareerStore.HasSave ? "Продолжить карьеру" : "Начать карьеру (1957)", btn))
                    CareerStore.Enter(false);
                if (CareerStore.HasSave && GUI.Button(new Rect(x + 330, y, 320, 36), "Новая карьера с 1957 года", btn))
                    CareerStore.Enter(true);
                return;
            }

            // Счёт и календарь.
            GameCalendar.ToDate(s.Date, out int yy, out int mo, out int dd, out _, out _, out _);
            GUI.Label(new Rect(x, y, iw, 28),
                $"Казна {CareerStore.Money(s.Funds)}    Наука {s.Science:F0} S    Престиж {s.Reputation:F0} P    " +
                $"Дата {dd:00}.{mo:00}.{yy}    Пусков {s.Flights}    Бюджет {yy + 1} г. ≈ {CareerStore.Money(s.Budget(yy + 1))}", stats);
            y += 34;

            // Вкладки и действия.
            float tw = 150;
            if (GUI.Button(new Rect(x, y, tw, 32), "Контракты", tab == Tab.Contracts ? tabOn : tabOff)) { tab = Tab.Contracts; scroll = Vector2.zero; }
            if (GUI.Button(new Rect(x + tw + 6, y, tw, 32), "Технологии", tab == Tab.Tech ? tabOn : tabOff)) { tab = Tab.Tech; scroll = Vector2.zero; }
            if (GUI.Button(new Rect(x + 2 * (tw + 6), y, tw, 32), "Журнал", tab == Tab.Log ? tabOn : tabOff)) { tab = Tab.Log; scroll = Vector2.zero; }
            float rx = x + iw;
            if (confirmNew)
            {
                if (GUI.Button(new Rect(rx - 120, y, 120, 32), "Отмена", btn)) confirmNew = false;
                if (GUI.Button(new Rect(rx - 120 - 6 - 230, y, 230, 32), "Да, стереть и начать", btn)) { confirmNew = false; CareerStore.Enter(true); }
            }
            else
            {
                if (GUI.Button(new Rect(rx - 170, y, 170, 32), "Новая карьера…", btn)) confirmNew = true;
                // §8.1: бюджет приходит 1 января — без полётов время двигается только явно.
                if (GUI.Button(new Rect(rx - 170 - 6 - 220, y, 220, 32), $"Ждать до 1 января {yy + 1}", btn))
                {
                    s.WaitNextYear();
                    CareerStore.Save();
                }
            }
            y += 40;

            var view = new Rect(x, y, iw, r.yMax - 12 - y);
            switch (tab)
            {
                case Tab.Contracts: Contracts(s, view, flyHistoric); break;
                case Tab.Tech: TechTree(s, view); break;
                default: Log(s, view); break;
            }
        }

        static void Contracts(CareerState s, Rect view, Action<string> flyHistoric)
        {
            var list = CareerContracts.All;
            float cw = view.width - 18;
            scroll = GUI.BeginScrollView(view, scroll, new Rect(0, 0, cw, list.Count * RowC));
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                var m = MissionCatalog.Get(c.MissionId);
                string block = s.ContractBlock(c);
                bool done = s.Completed.Contains(c.Id);
                var row = new Rect(0, i * RowC, cw, RowC - 4);
                Fill(row, done ? new Color(0.2f, 0.45f, 0.25f, 0.25f) : block == null ? new Color(0.25f, 0.4f, 0.65f, 0.22f) : new Color(1, 1, 1, 0.03f));
                float tx = row.x + 10, txw = cw - 2 * (BtnW + 8) - 20;
                string date = m != null ? $"  ·  старт {Short(m.StartTime)}" : "";
                GUI.Label(new Rect(tx, row.y + 3, txw, 26), (done ? "✓ " : "") + c.Title + date, name);
                string rival = double.IsNaN(c.RivalDate) ? "соперника нет — первыми в мире" : $"соперник: {c.RivalName}, {Short(c.RivalDate)}";
                string state = done ? "выполнен" : block ?? "доступен";
                GUI.Label(new Rect(tx, row.y + 28, txw, 24),
                    $"+{c.Funds:F0} M₢  +{c.Science:F0} S  +{c.Reputation:F0} P   ·   {rival}   ·   {state}", done || block == null ? ok : warn);

                if (done || block != null || m == null) continue;
                var b2 = new Rect(row.xMax - BtnW - 8, row.y + 10, BtnW, 34);
                var b1 = new Rect(b2.x - BtnW - 8, b2.y, BtnW, 34);
                GUI.enabled = c.AllowHistoric;
                if (GUI.Button(b1, c.AllowHistoric ? "Лететь (ист.)" : "только своей", btn))
                {
                    Open = false;
                    flyHistoric(c.MissionId);
                }
                GUI.enabled = true;
                if (GUI.Button(b2, "Своя ракета…", btn))
                {
                    // Конструктор (§5.4) стартует выбранную миссию своей ракетой; оплата — по деталям при пуске.
                    Open = false;
                    HangarController.ReturnMissionId = c.MissionId;
                    GameBootstrap.NextMissionId = c.MissionId;
                    SceneManager.LoadScene(HangarController.SceneName);
                }
            }
            GUI.EndScrollView();
        }

        static void TechTree(CareerState s, Rect view)
        {
            var list = CareerTech.Nodes;
            float cw = view.width - 18;
            scroll = GUI.BeginScrollView(view, scroll, new Rect(0, 0, cw, list.Count * RowT));
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                string block = s.NodeBlock(n);
                bool have = s.Tech.Contains(n.Id);
                var row = new Rect(0, i * RowT, cw, RowT - 4);
                Fill(row, have ? new Color(0.2f, 0.45f, 0.25f, 0.25f) : block == null ? new Color(0.25f, 0.4f, 0.65f, 0.22f) : new Color(1, 1, 1, 0.03f));
                float tx = row.x + 10, txw = cw - BtnW - 30;
                s.NodePrice(n, out double ps, out double pf);
                string price = have ? "изучено" : ps <= 0 && pf <= 0 ? "бесплатно" : $"{ps:F0} S + {pf:F0} M₢" + (ps > n.Science ? " (ранний доступ)" : "");
                GUI.Label(new Rect(tx, row.y + 3, txw, 24), $"{(have ? "✓ " : "")}{n.Title}  ·  {n.Year} г.  ·  {price}", name);
                GUI.Label(new Rect(tx, row.y + 27, txw, 22), Parts(n), sub);
                string req = n.Requires.Length == 0 ? "" : "требует: " + string.Join(", ", Array.ConvertAll(n.Requires, q => CareerTech.Get(q)?.Title ?? q)) + "   ·   ";
                GUI.Label(new Rect(tx, row.y + 49, txw, 22), req + (have ? n.Brief : block ?? "можно изучить"), have || block == null ? ok : warn);
                if (have) continue;
                GUI.enabled = block == null;
                if (GUI.Button(new Rect(row.xMax - BtnW - 8, row.y + 20, BtnW, 34), "Изучить", btn) && s.Buy(n))
                {
                    CareerStore.Save();
                    HangarController.CatalogChanged();
                }
                GUI.enabled = true;
            }
            GUI.EndScrollView();
        }

        static void Log(CareerState s, Rect view)
        {
            float cw = view.width - 18;
            int n = Math.Min(LogLines, s.Log.Count);
            scroll = GUI.BeginScrollView(view, scroll, new Rect(0, 0, cw, Math.Max(1, n) * 24));
            if (n == 0) GUI.Label(new Rect(6, 0, cw, 24), "Журнал пуст.", sub);
            for (int i = 0; i < n; i++)
                GUI.Label(new Rect(6, i * 24, cw - 6, 24), s.Log[s.Log.Count - 1 - i], sub); // свежее сверху
            GUI.EndScrollView();
        }

        /// <summary>Детали узла по каталогу (префиксы «*» раскрыты), кешируются — список не меняется в игре.</summary>
        static string Parts(CareerTechNode n)
        {
            if (partNames.TryGetValue(n.Id, out var text)) return text;
            var names = new List<string>();
            foreach (var p in PartCatalog.All)
                if (n.Unlocks(p.Id)) names.Add(p.Name);
            text = names.Count == 0 ? "—" : string.Join(", ", names);
            partNames[n.Id] = text;
            return text;
        }

        static string Short(double t)
        {
            GameCalendar.ToDate(t, out int y, out int mo, out int d, out _, out _, out _);
            return $"{d:00}.{mo:00}.{y}";
        }

        static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        static void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
            stats = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold };
            stats.normal.textColor = new Color(1f, 0.85f, 0.5f);
            tabOff = new GUIStyle(GUI.skin.button) { fontSize = 15 };
            tabOn = new GUIStyle(tabOff) { fontStyle = FontStyle.Bold };
            tabOn.normal.textColor = tabOn.hover.textColor = new Color(1f, 0.8f, 0.4f);
            name = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip };
            name.normal.textColor = Color.white;
            sub = new GUIStyle(GUI.skin.label) { fontSize = 13, clipping = TextClipping.Clip };
            sub.normal.textColor = new Color(0.75f, 0.8f, 0.9f);
            ok = new GUIStyle(sub);
            ok.normal.textColor = new Color(0.7f, 0.9f, 0.75f);
            warn = new GUIStyle(sub);
            warn.normal.textColor = new Color(0.95f, 0.7f, 0.45f);
            btn = new GUIStyle(GUI.skin.button) { fontSize = 15 };
            close = new GUIStyle(GUI.skin.button) { fontSize = 15 };
        }
    }
}
