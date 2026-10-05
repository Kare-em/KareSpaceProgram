using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Ступени подробно под стеком (§5.4, §10.2) — бывший список Δv: на каждую оставшуюся ступень топливо баков
    /// полосой и в тоннах, Δv, TWR, время работы, двигатели и масса (StageTable). Щелчок по заголовку сворачивает
    /// в краткие строки; выбор живёт в PlayerPrefs. Не влезающие по высоте строки сами ужимаются до кратких.
    /// </summary>
    public sealed partial class FlightHud
    {
        const string StagesDetailedKey = "hud.stagesDetailed";
        /// <summary>Ширина панели ступеней, px HUD. Пара: стек ступеней над ней прижат к тому же правому краю (w − 12).</summary>
        const float StagePanelWidth = 340, StageHeaderH = 26;
        /// <summary>Низ колонки — над панелью режима (Bottom: прямоугольник w − 262, h − 150).</summary>
        const float StageBottomGap = 150 + 6;
        int stagesDetailed = -1;

        void StagePanel(Vessel v, float w, float h, float y)
        {
            var stats = v.RemainingStats();
            if (stats.Count == 0) return;
            if (stagesDetailed < 0) stagesDetailed = PlayerPrefs.GetInt(StagesDetailedKey, 1);
            float pw = StagePanelWidth;
            float avail = h - StageBottomGap - y - StageHeaderH - 8;
            if (avail < StageTable.CompactH) return;
            int n = stats.Count, k = stagesDetailed > 0 ? n : 0;
            while (k > 0 && k * StageTable.RowH + (n - k) * StageTable.CompactH > avail) k--;
            float body = Mathf.Min(avail, k * StageTable.RowH + (n - k) * StageTable.CompactH);
            var r = new Rect(w - pw - 12, y, pw, StageHeaderH + body + 8);
            Fill(r, Panel);

            bool air = v.Body.HasAtmosphere && v.Altitude < v.Body.Atmosphere.Top;
            double total = 0;
            foreach (var s in stats) total += s.DeltaVVac;
            // Заголовок — кнопка: сворачивает подробные строки, когда ступеней много и они закрывают обзор.
            var head = new Rect(r.x, r.y, pw, StageHeaderH);
            Fill(head, new Color(0.10f, 0.30f, 0.40f, head.Contains(Event.current.mousePosition) ? 0.7f : 0.35f));
            string mark = stagesDetailed > 0 ? "▾" : "▸";
            string sub = air ? " вак. (у земли)" : "";
            if (GUI.Button(head, $"  {mark} СТУПЕНИ · Δv{sub} всего <b>{total:0}</b> м/с", small))
            {
                stagesDetailed = stagesDetailed > 0 ? 0 : 1;
                PlayerPrefs.SetInt(StagesDetailedKey, stagesDetailed);
            }
            StageTable.Draw(new Rect(r.x + 10, r.y + StageHeaderH + 4, pw - 20, body), stats, v.Design, v.Propellant, air, k, true);
        }
    }
}
