using System.Text;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// HUD v2 (GDD §10.2) на IMGUI: на экране только то, по чему игрок действует сейчас — время миссии,
    /// задача, высота и скорость, режим автопилота/SAS, стек ступеней и предупреждения. Остальная
    /// телеметрия — панель «Детали» по H (бывший HUD v1). Иконки — атлас 4×4 (Textures/UI/HudIcons).
    /// Масштаб — от высоты экрана (база 1080).
    /// </summary>
    public sealed class FlightHud : MonoBehaviour
    {
        const float BaseHeight = 1080, PanelWidth = 360;
        /// <summary>Сообщения ядра висят столько секунд после последнего нового.</summary>
        const float MessageShowTime = 7;
        /// <summary>Порог жёлтого «нагрев»: доля предельного потока самого слабого борта (2·10⁵ Вт/м²).
        /// Пара: FlightPhysics.OverheatTolerance — красное «перегрев» по таймеру, когда поток выше предела.</summary>
        const double HeatWarnFlux = 1e5, GWarn = 5;

        public Texture2D Icons;

        // Порядок клеток атласа (Tools/slice-atlas.py, строки сверху вниз).
        enum Icon
        {
            Ignite, Separate, Fairing, Chute,
            Capsule, Heat, GLoad, Stability,
            Prograde, Retrograde, Normal, AntiNormal,
            RadialOut, RadialIn, Maneuver, Autopilot,
        }

        static readonly Color Accent = new Color(0.45f, 0.85f, 1f);
        static readonly Color Dim = new Color(1, 1, 1, 0.55f);
        static readonly Color Warn = new Color(1f, 0.78f, 0.25f);
        static readonly Color Danger = new Color(1f, 0.32f, 0.28f);
        static readonly Color Panel = new Color(0.03f, 0.06f, 0.10f, 0.72f);

        readonly StringBuilder sb = new StringBuilder(1024);
        GUIStyle big, mid, label, small, box;
        bool details;
        int seenMessages;
        float messageTime = -100;

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.H)) details = !details;
        }

        void Styles()
        {
            if (big != null) return;
            label = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = true, alignment = TextAnchor.MiddleLeft, wordWrap = false };
            label.normal.textColor = Color.white;
            small = new GUIStyle(label) { fontSize = 13 };
            mid = new GUIStyle(label) { fontSize = 22, fontStyle = FontStyle.Bold };
            big = new GUIStyle(label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            box = new GUIStyle(GUI.skin.box) { fontSize = 14, richText = true, alignment = TextAnchor.UpperLeft, wordWrap = true };
            box.normal.textColor = Color.white;
        }

        void OnGUI()
        {
            var boot = GameBootstrap.Instance;
            var u = GameBootstrap.U;
            var v = u?.Active;
            if (v == null) return;
            Styles();
            float s = Mathf.Max(0.75f, Screen.height / BaseHeight);
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
            float w = Screen.width / s, h = Screen.height / s;

            TopCenter(u, v, boot, w);
            Warnings(u, v, w);
            StageStack(v, w, h);
            Bottom(u, v, w, h);
            Messages(boot, w, h);
            if (details) Details(u, v, boot);

            GUI.color = Dim;
            GUI.Label(new Rect(10, h - 24, 1100, 22),
                "Пробел ступень · Z/X газ · WASDQE руль · T/F SAS · G автопилот · ,/. время · M карта · H детали", small);
            GUI.color = Color.white;
        }

        // ---------------------------------------------------------------- верх: время и задача

        void TopCenter(Universe u, Vessel v, GameBootstrap boot, float w)
        {
            const float pw = 460;
            var r = new Rect((w - pw) / 2, 8, pw, 74);
            Fill(r, Panel);
            GUI.Label(new Rect(r.x, r.y + 2, pw, 38), "T+ " + GameCalendar.FormatDuration(v.MissionTime(u.Time)), big);
            var tr = boot.Tracker;
            string task = tr.FailReason != null ? $"<color=#ff6050>{tr.FailReason}</color>"
                        : tr.CurrentIndex >= 0 ? "Задача: " + boot.Mission.Objectives[tr.CurrentIndex].Describe(u.System)
                        : "Миссия выполнена";
            var c = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(r.x + 8, r.y + 40, pw - 16, 26), task, c);
            if (u.WarpIndex > 0)
            {
                string warp = "×" + Universe.Warps[u.WarpIndex].ToString("0") + (u.RailsActive ? " рельсы" : "");
                GUI.color = Accent;
                GUI.Label(new Rect(r.xMax + 10, r.y + 6, 160, 30), warp, mid);
                GUI.color = Color.white;
            }
        }

        // ---------------------------------------------------------------- предупреждения

        void Warnings(Universe u, Vessel v, float w)
        {
            float y = 12, x = w - 330;
            if (v.OverheatTimer > 0)
                Warning(ref y, x, Icon.Heat, Danger, $"ПЕРЕГРЕВ {v.OverheatTimer / FlightPhysics.OverheatTolerance * 100:0} %");
            else if (v.HeatFlux > HeatWarnFlux)
                Warning(ref y, x, Icon.Heat, Warn, $"Нагрев {v.HeatFlux / 1e6:0.00} МВт/м²");
            if (v.GForce > GWarn)
                Warning(ref y, x, Icon.GLoad, v.GForce > 2 * GWarn ? Danger : Warn, $"Перегрузка {v.GForce:0.0} g");
            if (u.WarpIndex > 0 && !u.RailsActive)
            {
                var why = u.RailsBlocker(v);
                if (why != null) Warning(ref y, x, Icon.Stability, Dim, why);
            }
        }

        void Warning(ref float y, float x, Icon icon, Color col, string text)
        {
            GUI.color = col;
            DrawIcon(new Rect(x, y, 30, 30), icon);
            GUI.Label(new Rect(x + 36, y, 250, 30), text, label);
            GUI.color = Color.white;
            y += 34;
        }

        // ---------------------------------------------------------------- стек ступеней (как в KSP)

        void StageStack(Vessel v, float w, float h)
        {
            var seq = v.Design.Sequence;
            int from = v.NextStage, count = Mathf.Min(seq.Count - from, 6);
            const float cell = 50;
            float x = w - cell - 12, y = 110;
            if (count <= 0) return;
            // Снизу вверх, как ступени на ракете: ближайшее действие — нижнее.
            for (int k = 0; k < count; k++)
            {
                var a = seq[from + k];
                var r = new Rect(x, y + (count - 1 - k) * (cell + 6), cell, cell);
                bool next = k == 0;
                Fill(r, next ? new Color(0.10f, 0.30f, 0.40f, 0.85f) : Panel);
                GUI.color = next ? Accent : Dim;
                DrawIcon(new Rect(r.x + 8, r.y + 8, cell - 16, cell - 16), StageIcon(a.Type));
                GUI.color = Color.white;
                GUI.Label(new Rect(r.x - 22, r.y, 20, cell), (seq.Count - (from + k)).ToString(), small);
            }
            float by = y + count * (cell + 6);
            var tip = new GUIStyle(small) { alignment = TextAnchor.UpperRight, wordWrap = true };
            GUI.Label(new Rect(w - 230, by, 218, 40), "Пробел: " + v.NextStageLabel, tip);
        }

        static Icon StageIcon(StageActionType t)
        {
            switch (t)
            {
                case StageActionType.Ignite: return Icon.Ignite;
                case StageActionType.Separate: return Icon.Separate;
                case StageActionType.JettisonFairing: return Icon.Fairing;
                default: return Icon.Chute;
            }
        }

        // ---------------------------------------------------------------- низ: высота, скорость, режим

        void Bottom(Universe u, Vessel v, float w, float h)
        {
            const float side = 230, centre = 240, ph = 92;
            float x0 = (w - centre) / 2 - side, y = h - ph - 32;

            var left = new Rect(x0, y, side, ph);
            Fill(left, Panel);
            GUI.color = Dim; GUI.Label(new Rect(left.x + 14, y + 6, side, 22), "ВЫСОТА", small); GUI.color = Color.white;
            GUI.Label(new Rect(left.x + 14, y + 26, side, 34), Km(v.Altitude), mid);
            if (v.Situation == Situation.Flying && v.Altitude > 20000)
            {
                var o = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, u.Time);
                string ap = o.IsElliptic ? Km(o.ApoapsisRadius - v.Body.Radius) : "∞";
                GUI.color = Dim;
                GUI.Label(new Rect(left.x + 14, y + 62, side, 22), $"Ap {ap}  ·  Pe {Km(o.PeriapsisRadius - v.Body.Radius)}", small);
                GUI.color = Color.white;
            }

            var mid0 = new Rect(x0 + side, y - 14, centre, ph + 14);
            Fill(mid0, new Color(0.05f, 0.12f, 0.18f, 0.82f));
            Mode(u, v, mid0);

            var right = new Rect(x0 + side + centre, y, side, ph);
            Fill(right, Panel);
            GUI.color = Dim; GUI.Label(new Rect(right.x + 14, y + 6, side, 22), v.Altitude < 30000 ? "СКОРОСТЬ (пов.)" : "СКОРОСТЬ (орб.)", small); GUI.color = Color.white;
            double sp = v.Altitude < 30000 ? v.SurfaceSpeed : v.Velocity.magnitude;
            GUI.Label(new Rect(right.x + 14, y + 26, side, 34), sp.ToString("0") + " м/с", mid);
            GUI.color = Dim;
            GUI.Label(new Rect(right.x + 14, y + 62, side, 22), $"верт. {v.VerticalSpeed:0} м/с", small);
            GUI.color = Color.white;

            ThrustFuel(v, h);
        }

        /// <summary>Что сейчас ведёт ракету — главный вопрос игрока: автопилот, SAS или руки.</summary>
        void Mode(Universe u, Vessel v, Rect r)
        {
            Icon icon; string title, status; Color col;
            if (u.Ascent != null) { icon = Icon.Autopilot; title = "АВТОПИЛОТ"; status = u.Ascent.Status; col = Accent; }
            else if (u.NodePilot != null) { icon = Icon.Maneuver; title = "МАНЁВР"; status = u.NodePilot.Status; col = Accent; }
            else if (u.Landing != null) { icon = Icon.Autopilot; title = "ПОСАДКА"; status = u.Landing.Phase.ToString(); col = Accent; }
            else if (v.Sas != SasMode.Off) { icon = SasIcon(v.Sas); title = "SAS"; status = SasName(v.Sas); col = Color.white; }
            else { icon = Icon.Stability; title = "РУЧНОЕ"; status = "G — автопилот, T — SAS"; col = Warn; }
            GUI.color = col;
            DrawIcon(new Rect(r.center.x - 22, r.y + 8, 44, 44), icon);
            var c = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            GUI.Label(new Rect(r.x, r.y + 52, r.width, 22), title, c);
            GUI.color = Color.white;
            var cs = new GUIStyle(small) { alignment = TextAnchor.UpperCenter, wordWrap = true };
            GUI.Label(new Rect(r.x + 6, r.y + 74, r.width - 12, 34), status, cs);
        }

        static Icon SasIcon(SasMode m)
        {
            switch (m)
            {
                case SasMode.Prograde: return Icon.Prograde;
                case SasMode.Retrograde: return Icon.Retrograde;
                case SasMode.Normal: return Icon.Normal;
                case SasMode.AntiNormal: return Icon.AntiNormal;
                case SasMode.RadialOut: return Icon.RadialOut;
                case SasMode.RadialIn: return Icon.RadialIn;
                case SasMode.Maneuver: return Icon.Maneuver;
                default: return Icon.Stability;
            }
        }

        static string SasName(SasMode m)
        {
            switch (m)
            {
                case SasMode.Prograde: return "по скорости";
                case SasMode.Retrograde: return "против скорости";
                case SasMode.Normal: return "нормаль";
                case SasMode.AntiNormal: return "антинормаль";
                case SasMode.RadialOut: return "радиально наружу";
                case SasMode.RadialIn: return "радиально внутрь";
                case SasMode.Maneuver: return "на манёвр";
                default: return "удержание";
            }
        }

        void ThrustFuel(Vessel v, float h)
        {
            var r = new Rect(12, h - 112, 250, 78);
            Fill(r, Panel);
            Bar(new Rect(r.x + 12, r.y + 12, 226, 22), "Газ", (float)v.Throttle, Accent);
            // Топливо ближайшей работающей (или первой с двигателем) ступени — ему и кончаться первым.
            int idx = -1;
            for (int i = 0; i < v.Attached.Length; i++)
            {
                if (!v.Attached[i] || !v.Design.Sections[i].HasEngine) continue;
                if (idx < 0 || v.Running[i]) idx = i;
                if (v.Running[i]) break;
            }
            float fuel = 0;
            if (idx >= 0 && v.Design.Sections[idx].Propellant > 0)
                fuel = (float)(v.Propellant[idx] / v.Design.Sections[idx].Propellant);
            Bar(new Rect(r.x + 12, r.y + 44, 226, 22), "Топливо", fuel, fuel < 0.1f ? Danger : Warn);
        }

        void Bar(Rect r, string name, float f, Color col)
        {
            GUI.Label(new Rect(r.x, r.y, 70, r.height), name, small);
            var b = new Rect(r.x + 72, r.y + 6, r.width - 120, r.height - 12);
            Fill(b, new Color(1, 1, 1, 0.12f));
            Fill(new Rect(b.x, b.y, b.width * Mathf.Clamp01(f), b.height), col);
            GUI.Label(new Rect(b.xMax + 6, r.y, 44, r.height), (f * 100).ToString("0") + "%", small);
        }

        // ---------------------------------------------------------------- сообщения

        void Messages(GameBootstrap boot, float w, float h)
        {
            var list = boot.Messages;
            // Сообщения в списке — скользящее окно из 8; новое = изменился последний элемент или их число.
            int sig = list.Count == 0 ? 0 : list.Count * 397 ^ list[list.Count - 1].GetHashCode();
            if (sig != seenMessages) { seenMessages = sig; messageTime = Time.unscaledTime; }
            float age = Time.unscaledTime - messageTime;
            if (age > MessageShowTime || list.Count == 0) return;
            float a = Mathf.Clamp01((MessageShowTime - age) / 1.5f);
            sb.Clear();
            for (int i = Mathf.Max(0, list.Count - 3); i < list.Count; i++) sb.Append(list[i]).Append('\n');
            var st = new GUIStyle(label) { alignment = TextAnchor.UpperCenter, wordWrap = true };
            GUI.color = new Color(1, 1, 1, a);
            GUI.Label(new Rect((w - 640) / 2, 96, 640, 80), sb.ToString(), st);
            GUI.color = Color.white;
        }

        // ---------------------------------------------------------------- детали (H) — бывший HUD v1

        void Details(Universe u, Vessel v, GameBootstrap boot)
        {
            sb.Clear();
            sb.Append("<b>").Append(v.Name).Append("</b>  ").Append(v.Situation).Append('\n');
            sb.Append(GameCalendar.Format(u.Time)).Append('\n');
            sb.Append("Над рельефом ").Append(Km(v.TerrainAltitude)).Append("   гор. ").Append(v.HorizontalSpeed.ToString("0")).Append(" м/с\n");
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, u.Time);
            if (orbit.IsElliptic)
                sb.Append("До Ap ").Append(GameCalendar.FormatDuration(orbit.TimeToApoapsis(u.Time))).Append('\n');
            sb.Append("Тело: ").Append(v.Body.Name).Append("\n\n");

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
            sb.Append("Тепловой поток ").Append((v.HeatFlux / 1e6).ToString("0.000")).Append(" МВт/м²\n");
            sb.Append("Масса ").Append((v.Mass / 1000).ToString("0.0")).Append(" т\n");
            if (SunLight.Visible < 0.999) sb.Append("Тень: Солнце ").Append((SunLight.Visible * 100).ToString("0")).Append(" %\n");
            sb.Append('\n');
            var tr = boot.Tracker;
            sb.Append("<b>").Append(boot.Mission.Title).Append("</b>  ").Append(tr.Status).Append('\n');
            for (int i = 0; i < boot.Mission.Objectives.Count; i++)
                sb.Append(tr.Done[i] ? "✓ " : (i == tr.CurrentIndex ? "▶ " : "· "))
                  .Append(boot.Mission.Objectives[i].Describe(u.System)).Append('\n');
            sb.Append('\n');
            foreach (var m in boot.Messages) sb.Append(m).Append('\n');
            GUI.Box(new Rect(10, 10, PanelWidth, 560), sb.ToString(), box);
        }

        // ---------------------------------------------------------------- примитивы

        void DrawIcon(Rect r, Icon i)
        {
            if (Icons == null) return;
            int k = (int)i, col = k % 4, row = k / 4;
            // UV снизу вверх, строки атласа — сверху вниз.
            GUI.DrawTextureWithTexCoords(r, Icons, new Rect(col * 0.25f, 1 - (row + 1) * 0.25f, 0.25f, 0.25f));
        }

        static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        static string Km(double m) =>
            System.Math.Abs(m) >= 10000 ? (m / 1000).ToString("0.0") + " км" : m.ToString("0") + " м";
    }
}
