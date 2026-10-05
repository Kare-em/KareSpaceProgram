using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// HUD второго вида (§6.9, §10.3, FlightView): подсказка выбора порядка при отделении возвращаемой ступени,
    /// плашка «вид не на управляемом борту», отложенная посадка и панель правой половины сплит-скрина.
    /// </summary>
    public sealed partial class FlightHud
    {
        /// <summary>Ширина панели второго борта. Пара: TopCenter (460) — та же верхняя полоса, но в своей половине.</summary>
        const float SecondPanelWidth = 380;
        /// <summary>Ширина плашек вида; меньше половины экрана (960 при 1080p) — влезает и в сплит.</summary>
        const float BannerWidth = 700;

        /// <summary>Пилот возврата борта, в том числе закончивший работу: его Status — итог посадки.</summary>
        static BoosterLandingAutopilot PilotOf(Universe u, Vessel v)
        {
            BoosterLandingAutopilot last = null;
            foreach (var p in u.Recoveries) if (p.Vessel == v) last = p;
            return last;
        }

        /// <summary>Доля топлива ближайшей работающей (или первой с двигателем) ступени — ему и кончаться первым.</summary>
        static float FuelShare(Vessel v)
        {
            int idx = -1;
            for (int i = 0; i < v.Attached.Length; i++)
            {
                if (!v.Attached[i] || !v.Design.Sections[i].HasEngine) continue;
                if (idx < 0 || v.Running[i]) idx = i;
                if (v.Running[i]) break;
            }
            return idx >= 0 && v.Design.Sections[idx].Propellant > 0
                ? (float)(v.Propellant[idx] / v.Design.Sections[idx].Propellant) : 0;
        }

        /// <summary>Всё, что касается двух бортов. w — полная ширина экрана, wl — ширина главной половины.</summary>
        void ViewOverlay(Universe u, float w, float wl, float h)
        {
            var second = FlightView.Second;
            if (second != null)
            {
                Fill(new Rect(wl - 1, 0, 2, h), new Color(0, 0, 0, 0.8f));
                SecondPanel(u, second, wl, w - wl);
            }

            float y = 88; // под TopCenter (8 + 74)
            if (FlightView.Watching)
            {
                string who = u.Mission != null ? "автопилот миссии" : u.Ascent != null || u.NodePilot != null || u.Landing != null
                             || u.Docking != null || u.Lunar != null ? "автопилот" : "без автопилота";
                // Без имени корабля: оба имени не влезают в полосу (замер 05.10.2026, «Falcon 9 «Crew Dragon Demo-2»»).
                Banner(new Rect((wl - BannerWidth) / 2, y, BannerWidth, 26),
                    $"Вид: {FlightView.Main.Name} · корабль: {who} · F2 — назад", Accent);
                y += 30;
            }
            if (u.Deferred.Count > 0)
            {
                var d = u.Deferred[0];
                Banner(new Rect((wl - BannerWidth) / 2, y, BannerWidth, 26),
                    $"Посадка «{d.Vessel.Name}» отложена {GameCalendar.FormatDuration(u.Time - d.FrozenAt)} · F4 — продолжить", Warn);
                y += 30;
            }

            // Выбор порядка — сразу после отделения, пока игрок не нажал ни одну из трёх клавиш.
            var run = u.RunningRecovery();
            if (run != null && Time.unscaledTime < FlightView.PromptUntil && !FlightView.Watching)
            {
                var r = new Rect((wl - 560) / 2, y + 4, 560, 84);
                Fill(r, Panel);
                var c = new GUIStyle(label) { alignment = TextAnchor.UpperCenter, wordWrap = true };
                GUI.color = Accent;
                GUI.Label(new Rect(r.x + 10, r.y + 6, r.width - 20, 24), $"«{run.Vessel.Name}» идёт на посадку", c);
                GUI.color = Color.white;
                GUI.Label(new Rect(r.x + 10, r.y + 32, r.width - 20, 48),
                    "F2 — смотреть посадку (корабль летит сам) · F4 — сначала корабль, посадка потом · F3 — оба на экране", c);
            }
        }

        void Banner(Rect r, string text, Color col)
        {
            Fill(r, Panel);
            GUI.color = col;
            GUI.Label(r, text, new GUIStyle(small) { alignment = TextAnchor.MiddleCenter });
            GUI.color = Color.white;
        }

        /// <summary>Правая половина: кто там, кто его ведёт и главные числа посадки.</summary>
        void SecondPanel(Universe u, Vessel v, float x0, float width)
        {
            var r = new Rect(x0 + (width - SecondPanelWidth) / 2, 8, SecondPanelWidth, 172);
            Fill(r, Panel);
            float x = r.x + 12, y = r.y + 6, pw = r.width - 24;
            GUI.color = Accent;
            GUI.Label(new Rect(x, y, pw, 26), v.Name, mid);
            GUI.color = Dim;
            var p = PilotOf(u, v);
            string who = v == u.Active ? (u.Mission != null ? "автопилот миссии: " + u.Mission.Phase : "управляемый борт")
                       : p != null ? "возврат: " + (p.Status.Length > 0 ? p.Status : p.Phase.ToString())
                       : "без пилота";
            GUI.Label(new Rect(x, y + 30, pw, 36), who, new GUIStyle(small) { wordWrap = true });
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y + 66, pw, 30), (v.TerrainAltitude < 10000 ? "над грунтом " + Km(v.TerrainAltitude) : "высота " + Km(v.Altitude)), label);
            GUI.Label(new Rect(x, y + 92, pw, 30), $"{v.SurfaceSpeed:0} м/с · верт. {v.VerticalSpeed:0}", label);
            float fuel = FuelShare(v);
            Bar(new Rect(x, y + 126, pw, 22), "Топливо", fuel, fuel < 0.1f ? Danger : Warn);
            if (!v.Alive) { GUI.color = Danger; GUI.Label(new Rect(x, y + 146, pw, 20), "разрушен", small); GUI.color = Color.white; }
        }
    }
}
