using System.Text;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Текстовый HUD v1 (GDD §10.2) на IMGUI. Масштаб — от высоты экрана (база 1080), шрифт —
    /// встроенный динамический: кириллицу берёт из системных шрифтов.
    /// </summary>
    public sealed class FlightHud : MonoBehaviour
    {
        const float BaseHeight = 1080, PanelWidth = 360;

        readonly StringBuilder sb = new StringBuilder(1024);
        GUIStyle style, small;

        void OnGUI()
        {
            var boot = GameBootstrap.Instance;
            var u = GameBootstrap.U;
            var v = u?.Active;
            if (v == null) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 15, richText = true, wordWrap = true };
                style.normal.textColor = Color.white;
                small = new GUIStyle(style) { fontSize = 13 };
            }
            float s = Mathf.Max(0.75f, Screen.height / BaseHeight);
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));

            sb.Clear();
            sb.Append("<b>").Append(v.Name).Append("</b>  ").Append(v.Situation).Append('\n');
            sb.Append(GameCalendar.Format(u.Time)).Append("   T+").Append(GameCalendar.FormatDuration(v.MissionTime(u.Time))).Append('\n');
            sb.Append("Ускорение ×").Append(Universe.Warps[u.WarpIndex].ToString("0"))
              .Append(u.RailsActive ? " (рельсы)" : " (физика)");
            if (u.WarpIndex > 0 && !u.RailsActive)
            {
                var why = u.RailsBlocker(v);
                if (why != null) sb.Append(" — ").Append(why);
            }
            sb.Append("\n\n");

            sb.Append("Высота ").Append(Km(v.Altitude)).Append("   над рельефом ").Append(Km(v.TerrainAltitude)).Append('\n');
            sb.Append("Скорость орб. ").Append(v.Velocity.magnitude.ToString("0")).Append(" м/с   пов. ")
              .Append(v.SurfaceSpeed.ToString("0")).Append(" м/с\n");
            sb.Append("Верт. ").Append(v.VerticalSpeed.ToString("0.0")).Append("   гор. ").Append(v.HorizontalSpeed.ToString("0")).Append(" м/с\n");

            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, u.Time);
            double pe = orbit.PeriapsisRadius - v.Body.Radius;
            sb.Append("Pe ").Append(Km(pe));
            if (orbit.IsElliptic)
            {
                double ap = orbit.ApoapsisRadius - v.Body.Radius;
                sb.Append("   Ap ").Append(Km(ap)).Append(" (через ").Append(GameCalendar.FormatDuration(orbit.TimeToApoapsis(u.Time))).Append(')');
            }
            else sb.Append("   гипербола");
            sb.Append("\nТело: ").Append(v.Body.Name).Append("\n\n");

            var stats = v.RemainingStats();
            double dvStage = stats.Count > 0 ? stats[0].DeltaVVac : 0, dvTotal = 0;
            foreach (var st in stats) dvTotal += st.DeltaVVac;
            sb.Append("Δv ступени ").Append(dvStage.ToString("0")).Append("   всего ").Append(dvTotal.ToString("0")).Append(" м/с (вак.)\n");
            double r = v.Position.magnitude;
            double g = v.Body.Mu / (r * r);
            double twr = v.Mass > 0 ? v.CurrentThrust / (v.Mass * g) : 0;
            sb.Append("TWR ").Append(twr.ToString("0.00"));
            if (stats.Count > 0) sb.Append("   у земли ").Append(stats[0].TwrSL.ToString("0.00")).Append("   вак. ").Append(stats[0].TwrVac.ToString("0.00"));
            sb.Append('\n');
            double q = v.DynamicPressure / 1000;
            sb.Append("q ").Append(q.ToString("0.0")).Append(" кПа   Qα ").Append((q * v.AngleOfAttack * Constants.Rad2Deg).ToString("0")).Append(" кПа·°   M ").Append(v.Mach.ToString("0.00")).Append('\n');
            sb.Append("Перегрузка ").Append(v.GForce.ToString("0.0")).Append(" g\n\n");

            double fuel = 0;
            for (int i = 0; i < v.Attached.Length; i++) if (v.Attached[i]) fuel += v.Propellant[i];
            sb.Append("Дроссель ").Append((v.Throttle * 100).ToString("0")).Append(" %   SAS ").Append(v.Sas).Append('\n');
            sb.Append("Масса ").Append((v.Mass / 1000).ToString("0.0")).Append(" т   топливо ").Append((fuel / 1000).ToString("0.0")).Append(" т\n");
            sb.Append("След. ступень: ").Append(v.HasNextStage ? v.NextStageLabel : "—").Append('\n');
            if (SunLight.Visible < 0.999) sb.Append("Тень: Солнце ").Append((SunLight.Visible * 100).ToString("0")).Append(" %\n");
            if (u.Ascent != null) sb.Append("Автопилот выведения: ").Append(u.Ascent.Status).Append('\n');
            if (u.NodePilot != null) sb.Append("Автопилот манёвра: ").Append(u.NodePilot.Status).Append('\n');
            if (u.Landing != null) sb.Append("Автопилот посадки: ").Append(u.Landing.Phase).Append('\n');

            GUI.Box(new Rect(10, 10, PanelWidth, 520), sb.ToString(), style);

            // Миссия и сообщения
            sb.Clear();
            var tr = boot.Tracker;
            sb.Append("<b>").Append(boot.Mission.Title).Append("</b>  ").Append(tr.Status).Append('\n');
            for (int i = 0; i < boot.Mission.Objectives.Count; i++)
            {
                sb.Append(tr.Done[i] ? "✓ " : (i == tr.CurrentIndex ? "▶ " : "· "))
                  .Append(boot.Mission.Objectives[i].Describe(u.System)).Append('\n');
            }
            if (tr.FailReason != null) sb.Append("<color=#ff6060>").Append(tr.FailReason).Append("</color>\n");
            sb.Append('\n');
            foreach (var m in boot.Messages) sb.Append(m).Append('\n');
            float w = Screen.width / s;
            GUI.Box(new Rect(w - PanelWidth - 10, 10, PanelWidth, 360), sb.ToString(), small);

            GUI.Label(new Rect(10, Screen.height / s - 28, 900, 24),
                "Z/X дроссель 100/0 · Пробел ступень · WASDQE · T SAS · F режим SAS · G автопилот · ,/. ускорение · M карта · ПКМ+колесо камера", small);
        }

        static string Km(double m) =>
            System.Math.Abs(m) >= 10000 ? (m / 1000).ToString("0.0") + " км" : m.ToString("0") + " м";
    }
}
