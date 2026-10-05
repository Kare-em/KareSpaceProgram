using System.Collections.Generic;
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
    public sealed partial class FlightHud : MonoBehaviour
    {
        const float BaseHeight = 1080, PanelWidth = 360;
        /// <summary>Сообщения ядра висят столько секунд после последнего нового.</summary>
        const float MessageShowTime = 7;
        /// <summary>Порог жёлтого «нагрев»: доля предельного потока самого слабого борта (2·10⁵ Вт/м²).
        /// Пара: FlightPhysics.OverheatTolerance — красное «перегрев» по таймеру, когда поток выше предела.</summary>
        const double HeatWarnFlux = 1e5, GWarn = 5;
        /// <summary>Жёлтое «угол атаки» — с этой доли предела FlightPhysics.QAlphaLimit, красное — с предела.</summary>
        const double QAlphaWarnShare = 0.6;
        /// <summary>Подсказка по углу (§10.2): цель по апоцентру, м. Пара: FlightInput, H — автопилот на 200 км.
        /// Запас перицентра над атмосферой — как у довыведения AscentAutopilot (верх атмосферы + 10 км и выше).</summary>
        const double TutorApoapsis = 200000, TutorPeriapsisMargin = 40000;
        /// <summary>Масштаб программы тангажа подсказки, м. Пара: AscentAutopilot.TurnAltitude.</summary>
        const double TutorTurnAltitude = 100000;
        /// <summary>Отклонение носа от цели, при котором подсказка говорит «так держать», градусы.</summary>
        const double TutorOkAngle = 3;

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
            if (Input.GetKeyDown(KeyCode.F1)) details = !details;
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
            if (!MapView.IsOpen) NavBall(u, v, w, h - 24); // 24 — строка подсказки клавиш внизу
            Messages(boot, w, h);
            NodePanel(u, v, h);
            DockPanel(u, v, w, h);
            if (details) Details(u, v, boot);
            else if (boot.AscentTutor && !Tutor(u, v)) Guide(u, boot);

            GUI.color = Dim;
            GUI.Label(new Rect(10, h - 24, 1400, 22),
                "Y автопилот миссии · P манёвр к цели · Пробел ступень · Z/X газ · WASDQE руль · T/F SAS · H взлёт/посадка · G опоры/трапы · R к Луне · V стыковка/расстыковка · Tab ручная стыковка · PgUp/PgDn аппарат · N/B манёвр · ,/. время · M карта · F1 детали · Esc", small);
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
            if (u.WarpIndex > 0 || u.AutoWarpManual)
            {
                // Фактический множитель, а не выбранный: в физике выше MaxPhysicsWarp время не идёт.
                // «авто»/«вручную» — кто правит ускорением при работающем автопилоте («.» перехватывает, «/» на ×1 отдаёт).
                string warp = "×" + u.EffectiveWarp.ToString("0") + (u.RailsActive ? " рельсы" : "")
                            + (u.AutoWarpDriving ? " авто" : u.AutoWarpManual ? " вручную" : "");
                GUI.color = Accent;
                GUI.Label(new Rect(r.xMax + 10, r.y + 6, 300, 30), warp, mid);
                GUI.color = Color.white;
            }
        }

        // ---------------------------------------------------------------- предупреждения

        void Warnings(Universe u, Vessel v, float w)
        {
            float y = 12, x = w - 330;
            if (v.OverheatTimer > 0)
                Warning(ref y, x, Icon.Heat, Danger, $"ПЕРЕГРЕВ {v.OverheatTimer / FlightPhysics.OverheatTolerance * 100:0} %"
                                                     + (FlightPhysics.HeatDamage ? "" : " (выкл.)"));
            else if (v.HeatFlux > HeatWarnFlux)
                Warning(ref y, x, Icon.Heat, Warn, $"Нагрев {v.HeatFlux / 1e6:0.00} МВт/м²");
            // Поперечная нагрузка — главная причина разрушения при ручном развороте (§4.7).
            double qa = v.DynamicPressure * System.Math.Sin(v.AngleOfAttack * Constants.Deg2Rad);
            v.MassProperties(out _, out _, out double len, out double rad);
            if (FlightPhysics.QAlphaExceeded(v.DynamicPressure, 1, len, rad) && qa > QAlphaWarnShare * FlightPhysics.QAlphaLimit)
            {
                bool over = qa > FlightPhysics.QAlphaLimit;
                string tail = over && !FlightPhysics.AeroBreakup ? " (разрушение выкл.)" : "";
                Warning(ref y, x, Icon.Stability, over ? Danger : Warn, $"Угол атаки {v.AngleOfAttack:0}°{tail}");
            }
            if (v.CrewLost)
                Warning(ref y, x, Icon.GLoad, Danger, "Экипаж погиб от перегрузки");
            else if (v.HighGTimer > 0)
            {
                // Счётчик §4.7: сколько секунд из 10 уже набрано сверх 9 g — игрок видит запас до гибели.
                string tail = FlightPhysics.GLoadLimit ? "" : " (гибель выкл.)";
                Warning(ref y, x, Icon.GLoad, Danger,
                    $"Перегрузка {v.GForce:0.0} g · экипаж {v.HighGTimer:0}/{FlightPhysics.CrewGTime:0} с{tail}");
            }
            else if (v.GForce > GWarn)
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
            // Группы ступеней (§5.4): действия с WithPrevious идут тем же пробелом — одна ячейка, иконки рядом.
            var groups = new List<(int from, int to)>();
            for (int i = v.NextStage; i < seq.Count; i++)
            {
                if (groups.Count > 0 && seq[i].WithPrevious) groups[groups.Count - 1] = (groups[groups.Count - 1].from, i);
                else groups.Add((i, i));
            }
            int count = Mathf.Min(groups.Count, 6);
            const float cell = 50;
            float y = 110;
            if (count <= 0) return;
            // Снизу вверх, как ступени на ракете: ближайшее действие — нижнее.
            for (int k = 0; k < count; k++)
            {
                var (g0, g1) = groups[k];
                int n = g1 - g0 + 1;
                float cw = cell + (n - 1) * (cell - 14);
                var r = new Rect(w - cw - 12, y + (count - 1 - k) * (cell + 6), cw, cell);
                bool next = k == 0;
                Fill(r, next ? new Color(0.10f, 0.30f, 0.40f, 0.85f) : Panel);
                GUI.color = next ? Accent : Dim;
                for (int j = 0; j < n; j++)
                    DrawIcon(new Rect(r.x + 8 + j * (cell - 14), r.y + 8, cell - 16, cell - 16), StageIcon(seq[g0 + j].Type));
                GUI.color = Color.white;
                GUI.Label(new Rect(r.x - 22, r.y, 20, cell), (groups.Count - k).ToString(), small);
            }
            float by = y + count * (cell + 6);
            var tip = new GUIStyle(small) { alignment = TextAnchor.UpperRight, wordWrap = true };
            GUI.Label(new Rect(w - 230, by, 218, 40), "Пробел: " + v.NextStageLabel, tip);
            StagePanel(v, w, h, by + 44);
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

        /// <summary>
        /// Система отсчёта скорости (§10.2, как переключатель над навболом в KSP): орбитальная — в невращающихся осях
        /// тела, поверхностная — относительно вращающегося грунта. С ней меняется и высота: у поверхностной — над
        /// грунтом под бортом (радиовысотомер), у орбитальной — над уровнем моря. Авто — поверхностная ниже
        /// AutoSurfaceBelow. Щелчок по строке «СКОРОСТЬ» перебирает режимы, выбор живёт в PlayerPrefs.
        /// </summary>
        enum SpeedFrame { Auto, Orbit, Surface }
        const string SpeedFrameKey = "hud.speedFrame";
        /// <summary>Граница авто-режима, м — та же, что была у HUD до переключателя: ниже неё важны воздух и грунт.</summary>
        const double AutoSurfaceBelow = 30000;
        SpeedFrame speedFrame = (SpeedFrame)(-1);

        /// <summary>Левая колонка снизу вверх: газ и топливо (ThrustFuel), узел манёвра (NodePanel).
        /// Пара: меняешь высоту одной — колонка сдвигается через NodePanelTop.</summary>
        const float FlightPanelHeight = 150, ThrustFuelTop = 138, NodePanelHeight = 166;
        static float NodePanelTop(float h) => h - ThrustFuelTop - 6 - NodePanelHeight;
        /// <summary>
        /// Высота и скорость — сверху по центру, сразу под временем миссии (TopCenter: y 8, высота 74): главные цифры
        /// полёта там, куда игрок и так смотрит. Пара: сообщения ядра (Messages) — под ней, с MessageTop.
        /// </summary>
        const float FlightPanelTopY = 8 + 74 + 6, MessageTop = FlightPanelTopY + FlightPanelHeight + 8;

        void Bottom(Universe u, Vessel v, float w, float h)
        {
            if ((int)speedFrame < 0) speedFrame = (SpeedFrame)Mathf.Clamp(PlayerPrefs.GetInt(SpeedFrameKey, 0), 0, 2);
            bool surface = speedFrame == SpeedFrame.Surface || speedFrame == SpeedFrame.Auto && v.Altitude < AutoSurfaceBelow;

            const float pw = 250;
            // Перетаскивается за любое место, кроме кнопки системы отсчёта (её прямоугольник ниже — та же пара).
            var r = Draggable(flightDrag, new Rect((w - pw) / 2, FlightPanelTopY, pw, FlightPanelHeight), w, h,
                new Rect(6, 78, pw - 12, 22));
            Fill(r, Panel);
            float x = r.x + 12, y = r.y;
            GUI.color = Dim; GUI.Label(new Rect(x, y + 4, pw - 24, 22), surface ? "ВЫСОТА НАД ГРУНТОМ" : "ВЫСОТА (ур. моря)", small); GUI.color = Color.white;
            GUI.Label(new Rect(x, y + 22, pw - 24, 34), Km(surface ? v.TerrainAltitude : v.Altitude), mid);
            GUI.color = Dim;
            if (surface) GUI.Label(new Rect(x, y + 54, pw - 24, 20), "над ур. моря " + Km(v.Altitude), small);
            else if (v.Situation == Situation.Flying)
            {
                var o = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, u.Time);
                string ap = o.IsElliptic ? Km(o.ApoapsisRadius - v.Body.Radius) : "∞";
                GUI.Label(new Rect(x, y + 54, pw - 24, 20), $"Ap {ap}  ·  Pe {Km(o.PeriapsisRadius - v.Body.Radius)}", small);
            }
            GUI.color = Color.white;

            // Переключатель — кнопкой на всю строку подписи: так его видно и в него легко попасть.
            string frame = speedFrame == SpeedFrame.Auto ? (surface ? "АВТО: ПОВ." : "АВТО: ОРБ.")
                         : speedFrame == SpeedFrame.Orbit ? "ОРБИТАЛЬНАЯ" : "ОТН. ПОВЕРХНОСТИ";
            var bs = new GUIStyle(small) { alignment = TextAnchor.MiddleLeft };
            var btn = new Rect(x - 6, y + 78, pw - 12, 22);
            Fill(btn, new Color(0.10f, 0.30f, 0.40f, btn.Contains(Event.current.mousePosition) ? 0.9f : 0.5f));
            if (GUI.Button(btn, "  СКОРОСТЬ · " + frame + "  ›", bs))
            {
                speedFrame = (SpeedFrame)(((int)speedFrame + 1) % 3);
                PlayerPrefs.SetInt(SpeedFrameKey, (int)speedFrame);
            }
            double sp = surface ? v.SurfaceSpeed : v.Velocity.magnitude;
            GUI.Label(new Rect(x, y + 100, pw - 24, 34), sp.ToString("0") + " м/с", mid);
            GUI.color = Dim;
            GUI.Label(new Rect(x, y + 128, pw - 24, 20),
                surface ? $"верт. {v.VerticalSpeed:0} · гор. {v.HorizontalSpeed:0} м/с" : $"верт. {v.VerticalSpeed:0} м/с", small);
            GUI.color = Color.white;

            ThrustFuel(v, h);
            // Режим — справа внизу, под стеком ступеней и списком Δv (они кончаются выше 700 px из 1080).
            var mode = new Rect(w - 262, h - 150, 250, 116);
            Fill(mode, new Color(0.05f, 0.12f, 0.18f, 0.82f));
            Mode(u, v, mode);
        }

        /// <summary>Что сейчас ведёт ракету — главный вопрос игрока: автопилот, SAS или руки.</summary>
        void Mode(Universe u, Vessel v, Rect r)
        {
            Icon icon; string title, status; Color col;
            // Автопилот миссии сам запускает частные — показываем его шаг, а их статус — строкой ниже.
            if (u.Mission != null) { icon = Icon.Autopilot; title = "МИССИЯ"; status = u.Mission.Phase + (u.Mission.Status.Length > 0 ? ": " + u.Mission.Status : ""); col = Accent; }
            else if (u.Ascent != null) { icon = Icon.Autopilot; title = "АВТОПИЛОТ"; status = u.Ascent.Status; col = Accent; }
            else if (u.NodePilot != null) { icon = Icon.Maneuver; title = "МАНЁВР"; status = u.NodePilot.Status; col = Accent; }
            else if (u.Landing != null) { icon = Icon.Autopilot; title = "ПОСАДКА"; status = u.Landing.Phase.ToString(); col = Accent; }
            else if (u.Docking != null) { icon = Icon.Maneuver; title = "СТЫКОВКА"; status = u.Docking.Status; col = Accent; }
            else if (u.Lunar != null) { icon = Icon.Autopilot; title = "К ЛУНЕ"; status = u.Lunar.Status; col = Accent; }
            else if (v.Sas != SasMode.Off) { icon = SasIcon(v.Sas); title = "SAS"; status = SasName(v.Sas); col = Color.white; }
            else { icon = Icon.Stability; title = "РУЧНОЕ"; status = "Y — вся миссия, H — взлёт, T — SAS"; col = Warn; }
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
                case SasMode.Target: return Icon.Prograde;
                case SasMode.AntiTarget: return Icon.Retrograde;
                case SasMode.DockAlign: return Icon.Autopilot;
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
                case SasMode.Target: return "на цель";
                case SasMode.AntiTarget: return "от цели";
                case SasMode.DockAlign: return "соосно с узлом цели";
                default: return "удержание";
            }
        }

        // ---------------------------------------------------------------- узел манёвра (§6.11)

        /// <summary>Прогноз после узла пересчитывается не каждый кадр: склейка коник — тысячи шагов.</summary>
        const float NodePredictPeriod = 0.25f;
        System.Collections.Generic.List<OrbitPatch> nodePatches;
        float nodePredictAt = -1;

        void NodePanel(Universe u, Vessel v, float h)
        {
            var n = v.Node;
            if (n == null) { nodePatches = null; return; }
            if (Time.unscaledTime >= nodePredictAt)
            {
                nodePredictAt = Time.unscaledTime + NodePredictPeriod;
                nodePatches = u.PredictActive();
            }
            var r = new Rect(12, NodePanelTop(h), 250, NodePanelHeight);
            Fill(r, Panel);
            float y = r.y + 6, x = r.x + 12;
            GUI.color = Accent;
            DrawIcon(new Rect(x, y, 24, 24), Icon.Maneuver);
            GUI.Label(new Rect(x + 30, y, 210, 24), "<b>МАНЁВР</b>", label);
            GUI.color = Color.white;
            y += 26;
            double burn = FlightControl.BurnTime(v, n.Total);
            string burnText = double.IsInfinity(burn) || double.IsNaN(burn) ? "<color=#ff6050>нет тяги</color>" : $"{burn:0} с работы";
            GUI.Label(new Rect(x, y, 230, 22), $"Δv <b>{n.Total:0.0}</b> м/с · {burnText}", label);
            y += 22;
            double tLeft = n.Time - u.Time;
            string when = tLeft >= 0 ? "через " + GameCalendar.FormatDuration(tLeft) : "<color=#ffc840>узел пропущен — Bksp или N</color>";
            GUI.Label(new Rect(x, y, 230, 22), when, label);
            y += 22;
            GUI.color = Dim;
            GUI.Label(new Rect(x, y, 236, 20), $"скор. {n.Prograde:0.0} · норм. {n.Normal:0.0} · рад. {n.Radial:0.0}", small);
            GUI.color = Color.white;
            y += 22;
            GUI.Label(new Rect(x, y, 236, 22), AfterNode(), small);
            y += 24;
            GUI.color = Dim;
            GUI.Label(new Rect(x, y, 236, 20), "N апсида · I/K J/L O/U Δv · [ ] время · C круг", small);
            GUI.Label(new Rect(x, y + 18, 236, 20), "B выполнить · Bksp удалить · Alt точно", small);
            GUI.color = Color.white;
        }

        /// <summary>Орбита после узла — ради неё узел и ставят: Ap/Pe или переход в другую сферу влияния.</summary>
        string AfterNode()
        {
            if (nodePatches == null) return "";
            for (int i = 0; i < nodePatches.Count; i++)
            {
                var p = nodePatches[i];
                if (!p.AfterNode) continue;
                var o = p.Orbit;
                string pe = Km(o.PeriapsisRadius - p.Body.Radius);
                string tail = p.EndType == TransitionType.Atmosphere ? " · <color=#ffc840>вход в атм.</color>"
                            : p.NextBody != null ? $" · → {p.NextBody.Name}" : "";
                string ap = o.IsElliptic ? Km(o.ApoapsisRadius - p.Body.Radius) : "∞";
                return $"После: Ap {ap} · Pe {pe}{tail}";
            }
            return "";
        }

        void ThrustFuel(Vessel v, float h)
        {
            var r = new Rect(12, h - ThrustFuelTop, 250, 104);
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

            // Тяговооружённость при текущей тяге: меньше 1 — ракета не разгоняется вверх, а теряет скорость.
            double r0 = v.Position.magnitude, g = v.Body.Mu / (r0 * r0);
            double twr = v.Mass > 0 ? v.CurrentThrust / (v.Mass * g) : 0;
            double full;
            if (v.CurrentThrust > 0 && v.Throttle > 0.05) full = twr / v.Throttle;
            else
            {
                // Двигатель молчит — полный газ по таблице ступени при текущем давлении.
                var stats = v.RemainingStats();
                double p = v.Body.HasAtmosphere ? System.Math.Min(1, v.StaticPressure / 101325) : 0;
                full = stats.Count > 0 ? stats[0].TwrVac + (stats[0].TwrSL - stats[0].TwrVac) * p : 0;
            }
            GUI.Label(new Rect(r.x + 12, r.y + 72, 70, 22), "TWR", small);
            GUI.color = v.CurrentThrust > 0 && twr < 1 && v.Situation != Situation.Landed ? Warn : Color.white;
            GUI.Label(new Rect(r.x + 72, r.y + 68, 80, 28), twr.ToString("0.00"), mid);
            GUI.color = Dim;
            GUI.Label(new Rect(r.x + 140, r.y + 72, 110, 22), "газ 100%: " + full.ToString("0.00"), small);
            GUI.color = Color.white;
        }

        // ---------------------------------------------------------------- подсказка по углу (выведение)

        /// <summary>
        /// Учебная подсказка ручного выведения (§10.2): какой тангаж держать сейчас — по тому же закону, что у
        /// автопилота, — где нос сейчас и какую клавишу жать. Видна, пока не ведёт автопилот и орбита не набрана.
        /// </summary>
        bool Tutor(Universe u, Vessel v)
        {
            if (u.Ascent != null || u.NodePilot != null || u.Landing != null || u.Lunar != null || u.Docking != null || u.Mission != null || !v.Body.HasAtmosphere) return false;
            // На воде полёт окончен: Splashed не Landed, и без этой проверки после приводнения (vs = 0, двигатель
            // молчит, ниже атмосферы) снова вылезала «2. Вертикальный подъём» (замер 01.10.2026).
            if (v.Situation == Situation.Splashed) return false;
            bool landed = v.Situation == Situation.Landed;
            if (landed && v.Site == null) return false;
            double atm = v.Body.AtmosphereTop;
            double apAlt = 0, peAlt = 0, toAp = double.NaN;
            KeplerOrbit o = null;
            if (!landed)
            {
                o = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, u.Time);
                apAlt = o.IsElliptic ? o.ApoapsisRadius - v.Body.Radius : double.PositiveInfinity;
                peAlt = o.PeriapsisRadius - v.Body.Radius;
                toAp = o.TimeToApoapsis(u.Time);
                // Орбита набрана или борт уже падает без тяги (спуск) — подсказка не нужна.
                if (peAlt > atm) return false;
                if (!v.AnyEngineRunning && v.VerticalSpeed < 0 && v.Altitude < atm) return false;
            }

            FlightControl.LocalFrame(v, u.Time, out var up, out _, out var east);
            string step, hint;
            double target = double.NaN; // тангаж над горизонтом, градусы; NaN — угол сейчас не важен
            bool burning = v.AnyEngineRunning;
            // Довыводить в апоцентре можно, только если работающий двигатель запустится снова. У Блока Е «Востока»
            // и большинства исторических ступеней запуск один: старая подсказка «X — отсечка, в апоцентре Z»
            // оставляла борт на суборбите без тяги (§10.2). Такие ступени ведём прямым выведением до орбиты.
            bool restart = false;
            for (int i = 0; i < v.Attached.Length; i++)
                if (v.Running[i] && v.CanIgnite(i)) restart = true;
            bool energy = o != null && o.IsElliptic && o.A >= v.Body.Radius + TutorApoapsis - 500;
            double circDv = o != null ? AscentAutopilot.CircularizeDv(v, o, u.Time) : 0;
            double circBurn = FlightControl.BurnTime(v, circDv);
            if (landed)
            {
                step = "1. Старт";
                hint = "Z — полный газ, Пробел — зажигание. Нос строго вверх.";
            }
            else if (!burning && FlightControl.NextStageBringsEngine(v) && double.IsInfinity(FlightControl.BurnTime(v, 1)))
            {
                step = "Ступень";
                hint = "Двигатель молчит — Пробел: следующая ступень.";
            }
            else if (v.SurfaceSpeed < AscentAutopilot.VerticalSpeedEnd && v.Altitude < AscentAutopilot.VerticalAltitudeEnd)
            {
                step = "2. Вертикальный подъём";
                hint = $"Вверх до {AscentAutopilot.VerticalSpeedEnd:0} м/с, потом плавно на восток.";
                target = 90;
            }
            else if (burning && (v.Altitude < AscentAutopilot.GuidedAltitude || v.DynamicPressure > AscentAutopilot.GuidedQ))
            {
                step = "3. Разворот на восток";
                hint = "Нос по голубой линии. Плавно: резкий угол атаки ломает ракету.";
                target = AscentAutopilot.ProgramPitch(v.Altitude, TutorTurnAltitude) * Constants.Rad2Deg;
            }
            else if (burning && !energy)
            {
                double sinT = AscentAutopilot.GuidedSin(v, up, east, TutorApoapsis, out double dvGo, out double tGo, out _);
                step = "4. Выведение";
                hint = $"Полный газ, нос по линии. До орбиты ~{tGo:0} с, Δv {dvGo:0} м/с. Двигатель не глушить.";
                target = System.Math.Asin(sinT) * Constants.Rad2Deg;
            }
            else if (burning && restart && v.VerticalSpeed > 0 && toAp > circBurn / 2 + 10)
            {
                step = "5. Отсечка";
                hint = $"Апоцентр {Km(apAlt)} набран — X: выключить. Довыведение — в апоцентре.";
            }
            else if (burning)
            {
                double sinT = AscentAutopilot.CircularizeSin(v, up, out _);
                step = "6. Довыведение";
                hint = $"Нос по линии, газ не сбрасывать, пока перицентр не выше {Km(atm + TutorPeriapsisMargin)} (сейчас {Km(peAlt)}).";
                target = System.Math.Asin(sinT) * Constants.Rad2Deg;
            }
            else if (double.IsInfinity(circBurn))
            {
                step = "Нет запуска";
                hint = "Двигатель больше не запустится — орбиту не довести. Esc → Читы: бесконечные перезапуски.";
            }
            else
            {
                double lead = circBurn / 2;
                bool now = toAp <= lead + 4 || v.VerticalSpeed < 0;
                step = "5. Полёт к апоцентру";
                hint = now ? $"Сейчас: Z — полный газ! Импульс {circDv:0} м/с, ~{circBurn:0} с."
                           : $"Импульс {circDv:0} м/с, ~{circBurn:0} с работы. Z через {GameCalendar.FormatDuration(toAp - lead)}. Нос — на горизонт.";
                target = 0;
            }

            var r = new Rect(12, 12, 340, double.IsNaN(target) ? 92 : 214);
            Fill(r, Panel);
            GUI.color = Accent;
            GUI.Label(new Rect(r.x + 12, r.y + 6, r.width - 24, 22), "ПОДСКАЗКА · " + step, label);
            GUI.color = Color.white;
            var wrap = new GUIStyle(small) { wordWrap = true, alignment = TextAnchor.UpperLeft };
            GUI.Label(new Rect(r.x + 12, r.y + 30, r.width - 24, 58), hint, wrap);
            if (double.IsNaN(target)) return true;

            // Угол носа в вертикальной плоскости «восток — зенит»: 0° — горизонт на восток, 90° — вверх.
            var nose = v.NoseP;
            double noseDeg = System.Math.Atan2(Vector3d.Dot(nose, up), Vector3d.Dot(nose, east)) * Constants.Rad2Deg;
            double tRad = target * Constants.Deg2Rad;
            var want = up * System.Math.Sin(tRad) + east * System.Math.Cos(tRad);
            double err = Vector3d.Angle(nose, want) * Constants.Rad2Deg;

            // Шкала-четверть: горизонт и вертикаль, цель — акцентом, нос — белым.
            var pivot = new Vector2(r.x + 24, r.y + 200);
            const float len = 100;
            Line(pivot, 0, len, Dim, 1);
            Line(pivot, 90, len, Dim, 1);
            Line(pivot, (float)target, len, Accent, 4);
            Line(pivot, (float)noseDeg, len * 0.85f, Color.white, 3);

            float tx = r.x + 140;
            GUI.color = Dim; GUI.Label(new Rect(tx, r.y + 94, 60, 20), "ЦЕЛЬ", small);
            GUI.Label(new Rect(tx + 90, r.y + 94, 60, 20), "НОС", small);
            GUI.color = Accent; GUI.Label(new Rect(tx, r.y + 112, 90, 30), target.ToString("0") + "°", mid);
            GUI.color = Color.white; GUI.Label(new Rect(tx + 90, r.y + 112, 90, 30), noseDeg.ToString("0") + "°", mid);
            string key;
            if (err < TutorOkAngle) { GUI.color = new Color(0.45f, 1f, 0.55f); key = "✓ так держать"; }
            else { GUI.color = Warn; key = "жми " + KeyToward(v, want - nose); }
            GUI.Label(new Rect(tx, r.y + 158, 190, 30), key, mid);
            GUI.color = Color.white;
            return true;
        }

        /// <summary>Тутор миссии (§10.2) после выведения: шаг и клавиши из MissionGuide — ручной полёт до Луны и обратно.</summary>
        void Guide(Universe u, GameBootstrap boot)
        {
            if (u.Ascent != null || u.NodePilot != null || u.Landing != null || u.Lunar != null || u.Docking != null || u.Mission != null) return;
            if (!MissionGuide.Next(u, boot.Tracker, out var step, out var hint)) return;
            var r = new Rect(12, 12, 340, 104);
            Fill(r, Panel);
            GUI.color = Accent;
            GUI.Label(new Rect(r.x + 12, r.y + 6, r.width - 24, 22), "ПОДСКАЗКА · " + step, label);
            GUI.color = Color.white;
            var wrap = new GUIStyle(small) { wordWrap = true, alignment = TextAnchor.UpperLeft };
            GUI.Label(new Rect(r.x + 12, r.y + 30, r.width - 24, 72), hint, wrap);
        }

        /// <summary>Клавиша, которая ведёт нос в сторону delta (P). Соглашение FlightControl.Update:
        /// W — нос к +X связанных осей, S — к −X, D — к −Z, A — к +Z.</summary>
        static string KeyToward(Vessel v, Vector3d delta)
        {
            var l = v.WorldToLocal(delta);
            if (System.Math.Abs(l.x) >= System.Math.Abs(l.z)) return l.x > 0 ? "W" : "S";
            return l.z < 0 ? "D" : "A";
        }

        /// <summary>Отрезок из pivot под углом deg над горизонталью (вправо — восток), в координатах GUI.</summary>
        static void Line(Vector2 pivot, float deg, float length, Color c, float width)
        {
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(-deg, pivot);
            Fill(new Rect(pivot.x, pivot.y - width / 2, length, width), c);
            GUI.matrix = m;
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
            GUI.Label(new Rect((w - 640) / 2, MessageTop, 640, 80), sb.ToString(), st);
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
            sb.Append("q ").Append(q.ToString("0.0")).Append(" кПа   Qα ").Append((q * v.AngleOfAttack).ToString("0")).Append(" кПа·°   M ").Append(v.Mach.ToString("0.00")).Append('\n');
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
            HudHits.Add(r); // панель HUD: щелчок по ней не выбирает деталь за ней (PartInspector)
        }

        static string Km(double m) =>
            System.Math.Abs(m) >= 10000 ? (m / 1000).ToString("0.0") + " км" : m.ToString("0") + " м";
    }
}
