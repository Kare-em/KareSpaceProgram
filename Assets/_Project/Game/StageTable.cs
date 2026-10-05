using System.Collections.Generic;
using System.Text;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Таблица ступеней подробно (§5.4, §10.2): одна строка — одно нажатие пробела (StageStats), в ней топливо
    /// баков ступени полосой и в тоннах, Δv, TWR, время работы, двигатели и масса до/после. Общая для HUD полёта
    /// (остаток баков сейчас) и конструктора (полные баки) — чтобы цифры в ангаре и в полёте читались одинаково.
    /// </summary>
    public static class StageTable
    {
        /// <summary>Высоты строк, px HUD (база 1080). Пара: FlightHud.StagePanel считает по ним, сколько строк влезает.</summary>
        public const float RowH = 60, CompactH = 20, MoreH = 20;

        static readonly Color Accent = new Color(0.45f, 0.85f, 1f);
        static readonly Color Dim = new Color(1, 1, 1, 0.55f);
        static readonly Color FuelColor = new Color(1f, 0.70f, 0.25f);
        static readonly Color Danger = new Color(1f, 0.32f, 0.28f);
        /// <summary>Ниже этой доли топливо ступени краснеет — как полоса «Топливо» в HUD.</summary>
        const float LowFuel = 0.1f;
        /// <summary>Колонки подробной строки справа, px: «Δv 3545 (2868) м/с» и «252,4 т / 252,4 т · 100%» шрифтом 14.</summary>
        const float DvW = 176, FuelW = 196;

        static GUIStyle name, right, small;
        static readonly StringBuilder sb = new StringBuilder(128);

        static void Styles()
        {
            if (name != null) return;
            name = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            name.normal.textColor = Color.white;
            right = new GUIStyle(name) { alignment = TextAnchor.MiddleRight };
            small = new GUIStyle(name) { fontSize = 12 };
        }

        /// <summary>Сколько топлива в баках строки сейчас и сколько было при заправке, кг.</summary>
        public static void Fuel(StageStats st, VesselDesign d, double[] propNow, out double now, out double full)
        {
            now = full = 0;
            foreach (int i in st.Sections)
            {
                if (i < 0 || i >= d.Sections.Count) continue;
                full += d.Sections[i].Propellant;
                now += propNow != null ? propNow[i] : d.Sections[i].Propellant;
            }
        }

        /// <summary>Двигатели строки: «РД-107 ×4 + РД-108» — одноимённые сложены.</summary>
        public static string Engines(StageStats st, VesselDesign d)
        {
            var names = new List<string>();
            var counts = new List<int>();
            foreach (int i in st.Sections)
            {
                if (i < 0 || i >= d.Sections.Count || !d.Sections[i].HasEngine) continue;
                var s = d.Sections[i];
                int k = names.IndexOf(s.Engine.Name);
                if (k < 0) { names.Add(s.Engine.Name); counts.Add(s.EngineCount); }
                else counts[k] += s.EngineCount;
            }
            sb.Clear();
            for (int k = 0; k < names.Count; k++)
            {
                if (k > 0) sb.Append(" + ");
                sb.Append(names[k]);
                if (counts[k] > 1) sb.Append(" ×").Append(counts[k]);
            }
            return sb.ToString();
        }

        public static string Mass(double kg) => kg >= 10000 ? (kg / 1000).ToString("0.0") + " т" : kg.ToString("0") + " кг";

        public static string Time(double s)
        {
            if (double.IsInfinity(s) || double.IsNaN(s)) return "—";
            return s >= 90 ? $"{(int)(s / 60)}:{(int)(s % 60):00}" : s.ToString("0") + " с";
        }

        /// <summary>
        /// Рисует строки в <paramref name="area"/>: первые <paramref name="detailed"/> — подробно, дальше — кратко, пока
        /// влезает, остаток — строкой «ещё N». <paramref name="air"/> — показывать цифры у земли рядом с вакуумными.
        /// Возвращает занятую высоту.
        /// </summary>
        public static float Draw(Rect area, List<StageStats> stats, VesselDesign d, double[] propNow, bool air, int detailed, bool current)
        {
            Styles();
            float y = area.y;
            for (int i = 0; i < stats.Count; i++)
            {
                bool full = i < detailed;
                float need = full ? RowH : CompactH;
                bool last = i == stats.Count - 1;
                if (y + need + (last ? 0 : MoreH) > area.yMax && !(last && y + need <= area.yMax))
                {
                    GUI.color = Dim;
                    GUI.Label(new Rect(area.x, y, area.width, MoreH), $"… ещё ступеней: {stats.Count - i}", small);
                    GUI.color = Color.white;
                    return y + MoreH - area.y;
                }
                var r = new Rect(area.x, y, area.width, need);
                if (full) Row(r, stats[i], d, propNow, air, current && i == 0, i + 1);
                else Compact(r, stats[i], d, propNow, air, current && i == 0, i + 1);
                y += need;
            }
            return y - area.y;
        }

        static void Row(Rect r, StageStats st, VesselDesign d, double[] propNow, bool air, bool now, int n)
        {
            float w = r.width;
            GUI.color = now ? Accent : Color.white;
            GUI.Label(new Rect(r.x, r.y, w - DvW - 4, 20), $"<b>{n}. {st.Name}</b>", name);
            string dv = air ? $"{st.DeltaVVac:0} ({st.DeltaVSL:0})" : st.DeltaVVac.ToString("0");
            GUI.Label(new Rect(r.xMax - DvW, r.y, DvW, 20), $"Δv <b>{dv}</b> м/с", right);
            GUI.color = Color.white;

            Fuel(st, d, propNow, out double fnow, out double ffull);
            float f = ffull > 0 ? Mathf.Clamp01((float)(fnow / ffull)) : 0;
            var bar = new Rect(r.x, r.y + 24, w - FuelW - 6, 10);
            Fill(bar, new Color(1, 1, 1, 0.12f));
            Fill(new Rect(bar.x, bar.y, bar.width * f, bar.height), f < LowFuel ? Danger : FuelColor);
            GUI.Label(new Rect(r.xMax - FuelW, r.y + 19, FuelW, 20), $"{Mass(fnow)} / {Mass(ffull)} · {f * 100:0}%", right);

            string eng = Engines(st, d); // до sb.Clear: Engines сама пишет в общий sb
            sb.Clear();
            // В атмосфере — TWR у земли (вакуумный в строку не влезает рядом с двигателями и массой).
            sb.Append(air ? "TWR у земли " : "TWR ").Append((air ? st.TwrSL : st.TwrVac).ToString("0.00"));
            sb.Append(" · ").Append(Time(st.BurnTime));
            if (eng.Length > 0) sb.Append(" · ").Append(eng);
            sb.Append(" · ").Append(Mass(st.StartMass)).Append(" → ").Append(Mass(st.EndMass));
            GUI.color = Dim;
            GUI.Label(new Rect(r.x, r.y + 38, w, 18), sb.ToString(), small);
            GUI.color = Color.white;
        }

        static void Compact(Rect r, StageStats st, VesselDesign d, double[] propNow, bool air, bool now, int n)
        {
            float w = r.width;
            GUI.color = now ? Accent : Color.white;
            string nm = st.Name.Length > 18 ? st.Name.Substring(0, 17) + "…" : st.Name;
            GUI.Label(new Rect(r.x, r.y, w * 0.42f, r.height), $"{n}. {nm}", small);
            string dv = air ? $"{st.DeltaVVac:0} ({st.DeltaVSL:0})" : st.DeltaVVac.ToString("0");
            GUI.Label(new Rect(r.x + w * 0.42f, r.y, w * 0.30f, r.height), dv + " м/с", small);
            GUI.color = Color.white;
            Fuel(st, d, propNow, out double fnow, out double ffull);
            float f = ffull > 0 ? Mathf.Clamp01((float)(fnow / ffull)) : 0;
            var bar = new Rect(r.x + w * 0.73f, r.y + 7, w * 0.12f, 6);
            Fill(bar, new Color(1, 1, 1, 0.12f));
            Fill(new Rect(bar.x, bar.y, bar.width * f, bar.height), f < LowFuel ? Danger : FuelColor);
            GUI.Label(new Rect(r.x + w * 0.86f, r.y, w * 0.14f, r.height), Time(st.BurnTime), small);
        }

        static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }
    }
}
