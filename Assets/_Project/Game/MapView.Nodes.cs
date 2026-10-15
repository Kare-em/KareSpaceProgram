using System.Collections.Generic;
using System.Globalization;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Ручные манёвры на карте (GDD §6.11, карта §9.6) — как в KSP. Щелчок ЛКМ по траектории — «Добавить манёвр»;
    /// у выбранного узла шесть ручек (по скорости / против, нормаль / антинормаль, радиально наружу / внутрь):
    /// тянешь ручку — Δv по её оси растёт тем быстрее, чем дальше увёл руку; тянешь сам ромб — узел скользит
    /// по орбите (меняется время). Окно манёвров (кнопка на карте, клавиша NodeWindow) — очередь узлов с T−, Δv,
    /// длительностью прожига и РЕАЛЬНЫМ стартом (середина прожига — на узле), числовые поля выбранного узла.
    /// Узел, который исполняет автопилот (первый), закрыт для правок — как и у клавиш (FlightInput.Maneuver).
    /// </summary>
    public sealed partial class MapView
    {
        // ---------------------------------------------------------------- константы

        /// <summary>Длина ручки от ромба, px — постоянная на любом масштабе (карта от 1 км до 1e13 м).
        /// Пара: HandleHitPx меньше половины расстояния между концами соседних ручек, иначе щелчок ловит не ту.</summary>
        const float HandlePx = 58, HandleHitPx = 11, HandleDotPx = 16;
        /// <summary>Ручка, смотрящая почти в камеру, не короче этой доли HandlePx — иначе прячется под ромбом.</summary>
        const float HandleMinFrac = 0.35f;
        /// <summary>Ромб узла, px, и радиус попадания по нему. Пара: MarkerHitPx &lt; HandlePx·HandleMinFrac − HandleHitPx,
        /// чтобы короткая ручка не тонула в ромбе.</summary>
        const float MarkerPx = 16, MarkerHitPx = 9;
        /// <summary>Набор Δv при перетаскивании ручки: DragRate·(d / DragScalePx)² м/с за секунду, d — вынос руки
        /// вдоль ручки, px. 50 px → 10 м/с за секунду (доводка), 200 px → 160 м/с за секунду (разгон к Луне ≈ 20 с).
        /// Пара: DragDeadPx — дрожь руки у самой ручки Δv не набирает; DragMaxPullPx — только рисунок ручки.</summary>
        const double DragRate = 10;
        const float DragScalePx = 50, DragDeadPx = 3, DragMaxPullPx = 240;
        /// <summary>Alt — точная доводка, как у клавиш (FlightInput.NodeFine).</summary>
        const double DragFine = 0.1;
        /// <summary>Прогноз во время правки, с: траектория меняется вслед за рукой. Обычный период — PredictPeriod.</summary>
        const float DragPredictPeriod = 0.05f;
        /// <summary>Щелчок по линии: допуск до траектории, px; сдвиг мыши, после которого это уже не щелчок, px.</summary>
        const float LinePickPx = 12, ClickSlopPx = 5;
        /// <summary>Поиск ближайшей к мыши точки участка: точек по истинной аномалии (как рисуются линии) и шагов уточнения.</summary>
        const int PickSamples = 256, PickRefine = 20;
        /// <summary>Строк узлов в окне: больше — «ещё N» (окно встаёт под списком фокуса, 1080 по высоте).</summary>
        const int WindowRows = 8;
        /// <summary>Шаги кнопок числовых полей, м/с (и секунды у времени).</summary>
        static readonly double[] FieldSteps = { 0.1, 1, 10, 100 };

        static readonly Color ProColor = new Color(0.85f, 0.95f, 0.2f), NrmColor = new Color(0.9f, 0.35f, 0.95f),
            RadColor = new Color(0.3f, 0.9f, 1f), SelectedNodeColor = new Color(0.55f, 0.8f, 1f);
        static readonly string[] HandleNames = { "по скорости", "против скорости", "нормаль", "антинормаль", "радиально наружу", "радиально внутрь" };

        // ---------------------------------------------------------------- состояние

        /// <summary>Выбранный узел (правки ручками, полями и клавишами). Не из очереди активного борта — значит первый.</summary>
        static ManeuverNode selected;
        bool nodeWindowOpen;

        struct NodeView
        {
            public ManeuverNode Node;
            public Vector2 Gui;
            public bool Visible;
        }
        readonly List<NodeView> nodeViews = new List<NodeView>();
        /// <summary>Смещения ручек выбранного узла от ромба, px GUI (y вниз): pro, retro, nrm, anti, rad out, rad in.</summary>
        readonly Vector2[] handleOff = new Vector2[6];
        int selView = -1;

        enum NodeDrag { None, Handle, Marker, Click }
        NodeDrag drag;
        ManeuverNode dragNode;
        int dragAxis;
        Vector2 dragRest, dragDir, dragStart, lastMouse;
        float dragPull;
        NodeFrame dragFrame;
        List<OrbitPatch> dragPatches;
        double dragLo, dragHi;

        bool popupOpen;
        double popupTime;
        Vector2 popupAt;
        Rect nodeWindowRect, popupRect;
        readonly string[] fieldBuf = new string[4];
        Texture2D discTex, ringTex;
        GUIStyle nodeCell, nodeCellLeft, nodeBtn, nodeField, nodeRow;

        // ---------------------------------------------------------------- выбор (FlightInput, окно)

        /// <summary>Узел, к которому идут правки: выбранный, если он ещё в очереди борта, иначе первый.</summary>
        public static ManeuverNode SelectedNode(Vessel v)
        {
            if (v == null) return null;
            if (selected != null && v.Nodes.Contains(selected)) return selected;
            return v.Node;
        }

        public static void SelectNode(ManeuverNode n) => selected = n;

        /// <summary>Следующий узел очереди по кругу (клавиша NodeSelectNext).</summary>
        public static void SelectNextNode(Vessel v)
        {
            if (v == null || v.Nodes.Count == 0) return;
            int i = v.Nodes.IndexOf(SelectedNode(v));
            selected = v.Nodes[(i + 1) % v.Nodes.Count];
        }

        /// <summary>Окно манёвров: открыть вместе с картой или закрыть. С меню перелётов не совмещается — одно место.</summary>
        public static void ToggleNodeWindow()
        {
            if (instance == null) return;
            if (!IsOpen)
            {
                Toggle();
                instance.nodeWindowOpen = true;
            }
            else instance.nodeWindowOpen = !instance.nodeWindowOpen;
            if (instance.nodeWindowOpen) instance.transfersOpen = false;
            else GUIUtility.keyboardControl = 0;
        }

        /// <summary>Игрок печатает в поле окна манёвров — клавиши полёта (цифры, I/K…) молчат.</summary>
        public static bool Typing => IsOpen && instance != null && instance.nodeWindowOpen && GUIUtility.keyboardControl != 0;

        static bool Locked(Universe u, Vessel v, ManeuverNode n) => u.NodePilot != null && n == v.Node;

        static bool AltHeld => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        static Vector2 MouseGui()
        {
            var m = Input.mousePosition;
            return new Vector2(m.x, Screen.height - m.y);
        }

        // ---------------------------------------------------------------- кадр: перетаскивание (LateUpdate)

        /// <summary>Перетаскивание ручки или ромба — раз в кадр (OnGUI зовётся по нескольку раз). Нажатие ловит OnGUI.</summary>
        void NodesUpdate(Universe u, Vessel v)
        {
            if (drag == NodeDrag.None) return;
            double now = u.Time;
            if (drag != NodeDrag.Click && (dragNode == null || !v.Nodes.Contains(dragNode) || Locked(u, v, dragNode)))
            {
                drag = NodeDrag.None;
                return;
            }
            if (!Input.GetMouseButton(0))
            {
                if (drag == NodeDrag.Click && (MouseGui() - dragStart).magnitude < ClickSlopPx) PickLine(u, MouseGui());
                drag = NodeDrag.None;
                return;
            }
            bool changed = drag == NodeDrag.Handle ? DragHandle(v, now) : drag == NodeDrag.Marker && DragMarker(v, now);
            if (changed) nextPredict = Mathf.Min(nextPredict, Time.unscaledTime + DragPredictPeriod);
        }

        /// <summary>Ручка: вынос руки вдоль её экранной оси → скорость набора Δv (квадратично, как разгон клавиш).</summary>
        bool DragHandle(Vessel v, double now)
        {
            float d = Vector2.Dot(MouseGui() - dragRest, dragDir);
            dragPull = Mathf.Clamp(d, -DragMaxPullPx, DragMaxPullPx);
            float a = Mathf.Abs(d) - DragDeadPx;
            if (a <= 0) return false;
            double x = a / DragScalePx;
            double dv = DragRate * x * x * (AltHeld ? DragFine : 1) * Mathf.Sign(d) * Time.unscaledDeltaTime;
            if (dragAxis % 2 == 1) dv = -dv;
            int c = dragAxis / 2;
            // Орбита узла — закэширована на нажатие: у узла очереди это прогноз через предыдущие (поиск встреч).
            NodePlanner.Adjust(v, now, dragNode, c == 0 ? dv : 0, c == 1 ? dv : 0, c == 2 ? dv : 0, dragFrame);
            return true;
        }

        /// <summary>Ромб: ближайшая к мыши точка орбиты узла в пределах ±полвитка от его времени — узел едет за рукой
        /// и может уйти на следующие витки. Прогноз до узла посчитан на нажатие (его импульс на него не влияет).</summary>
        bool DragMarker(Vessel v, double now)
        {
            var m = MouseGui();
            if ((m - lastMouse).sqrMagnitude < 0.25f) return false;
            lastMouse = m;
            double lo = dragLo, hi = dragHi;
            var f = NodePlanner.Pick(dragPatches, dragNode.Time);
            if (f.Valid && f.Orbit.IsElliptic)
            {
                double half = 0.5 * f.Orbit.Period;
                lo = System.Math.Max(lo, dragNode.Time - half);
                hi = System.Math.Min(hi, dragNode.Time + half);
            }
            double best = double.NaN;
            float bestPx = float.MaxValue;
            foreach (var p in dragPatches)
            {
                double a = System.Math.Max(lo, p.StartTime), b = System.Math.Min(hi, p.EndTime);
                if (Closest(p, a, b, m, out double t, out float px) && px < bestPx) { bestPx = px; best = t; }
            }
            if (double.IsNaN(best)) return false;
            NodePlanner.SetTime(v, now, dragNode, best, dragPatches);
            return true;
        }

        /// <summary>Щелчок по траектории — меню «Добавить манёвр» в ближайшей точке (участки прогноза с узлами).</summary>
        void PickLine(Universe u, Vector2 m)
        {
            popupOpen = false;
            if (patches == null) return;
            double best = double.NaN;
            float bestPx = LinePickPx;
            foreach (var p in patches)
            {
                double a = System.Math.Max(p.StartTime, u.Time);
                double b = p.EndType == TransitionType.Atmosphere ? p.EndTime : EndOf(p, u.Time);
                if (Closest(p, a, b, m, out double t, out float px) && px < bestPx) { bestPx = px; best = t; }
            }
            if (double.IsNaN(best)) return;
            popupOpen = true;
            popupTime = best;
            popupAt = m;
        }

        /// <summary>
        /// Ближайшая к точке экрана m точка участка p на отрезке времени [a, b]: перебор по истинной аномалии (у вытянутой
        /// орбиты равные по времени точки сбились бы к апоцентру) и уточнение тернарным поиском между соседями.
        /// </summary>
        bool Closest(OrbitPatch p, double a, double b, Vector2 m, out double t, out float px)
        {
            t = double.NaN;
            px = float.MaxValue;
            var o = p.Orbit;
            if (double.IsInfinity(b) || double.IsNaN(b)) b = EndOf(p, a);
            if (o.IsElliptic) b = System.Math.Min(b, a + o.Period);
            if (!(b > a)) return false;
            double nu0 = UnwrappedTrueAnomaly(o, a), nu1 = UnwrappedTrueAnomaly(o, b);
            int bi = -1;
            for (int i = 0; i < PickSamples; i++)
            {
                float d = ScreenDist(p, nu0 + (nu1 - nu0) * i / (PickSamples - 1), m);
                if (d < px) { px = d; bi = i; }
            }
            if (bi < 0) return false;
            double l = nu0 + (nu1 - nu0) * System.Math.Max(bi - 1, 0) / (PickSamples - 1);
            double r = nu0 + (nu1 - nu0) * System.Math.Min(bi + 1, PickSamples - 1) / (PickSamples - 1);
            for (int k = 0; k < PickRefine; k++)
            {
                double m1 = l + (r - l) / 3, m2 = r - (r - l) / 3;
                if (ScreenDist(p, m1, m) < ScreenDist(p, m2, m)) r = m2;
                else l = m1;
            }
            double nu = 0.5 * (l + r);
            px = ScreenDist(p, nu, m);
            t = TimeAtAnomaly(o, nu, a);
            if (double.IsNaN(t)) return false;
            t = System.Math.Max(a, System.Math.Min(b, t));
            return true;
        }

        float ScreenDist(OrbitPatch p, double nu, Vector2 m)
        {
            var sp = Camera.WorldToScreenPoint(FloatingOrigin.ToUnity(p.Body.Position + p.Orbit.PositionAtTrueAnomaly(nu)));
            if (sp.z <= 0) return float.MaxValue;
            return Vector2.Distance(new Vector2(sp.x, Screen.height - sp.y), m);
        }

        /// <summary>Время по развёрнутой аномалии (участок не длиннее витка): первое не раньше after.</summary>
        static double TimeAtAnomaly(KeplerOrbit o, double nu, double after)
        {
            if (!o.IsElliptic) return o.TimeOfTrueAnomaly(nu, double.NegativeInfinity);
            double w = System.Math.IEEERemainder(nu, 2 * System.Math.PI);
            return o.TimeOfTrueAnomaly(w, after - 1e-6);
        }

        // ---------------------------------------------------------------- метки узлов (Draw, после прогноза)

        /// <summary>Положение узлов на экране и ручки выбранного — после прогноза и камеры этого кадра.
        /// Узел стоит на участке ДО своего импульса: там его оси (как в NodePlanner.SyncFromRemaining).</summary>
        void NodeMarks(Universe u, Vessel v)
        {
            nodeViews.Clear();
            selView = -1;
            if (patches == null || v.Nodes.Count == 0) return;
            var sel = SelectedNode(v);
            var cam = Camera.transform;
            for (int k = 0; k < v.Nodes.Count; k++)
            {
                var n = v.Nodes[k];
                var f = NodePlanner.Pick(patches, n.Time - 1e-3);
                if (!f.Valid) continue;
                f.Orbit.GetState(n.Time, out var r, out var vel);
                var world = FloatingOrigin.ToUnity(f.Body.Position + r);
                var sp = Camera.WorldToScreenPoint(world);
                string dvText = n.Total > 0.05 ? $"{n.Total:0.0} м/с" : "Δv 0";
                marks.Add(new Mark { World = world, Text = $"   ◆ {k + 1}: {dvText}", Color = n == sel ? SelectedNodeColor : NodeColor });
                if (n == sel)
                {
                    selView = nodeViews.Count;
                    var pro = vel.normalized;
                    var nrm = Vector3d.Cross(r, vel).normalized;
                    var rad = Vector3d.Cross(pro, nrm);
                    var axes = new[] { pro, -pro, nrm, -nrm, rad, -rad };
                    for (int i = 0; i < 6; i++)
                    {
                        // Экранная длина постоянна: ось в осях камеры, z (на зрителя) отбрасываем — ручка укорачивается
                        // в перспективе, как в KSP, но не меньше HandleMinFrac.
                        var dc = cam.InverseTransformDirection(FloatingOrigin.DirToUnity(axes[i]));
                        var off = new Vector2(dc.x, -dc.y) * HandlePx;
                        float len = off.magnitude, min = HandleMinFrac * HandlePx;
                        if (len < min)
                            off = len > 1e-3f ? off / len * min : (Vector2)(Quaternion.Euler(0, 0, 60 * i) * Vector2.up) * min;
                        handleOff[i] = off;
                    }
                }
                nodeViews.Add(new NodeView { Node = n, Gui = new Vector2(sp.x, Screen.height - sp.y), Visible = sp.z > 0 });
            }
        }

        // ---------------------------------------------------------------- OnGUI: ромбы, ручки, меню щелчка

        void DrawNodes(Universe u, Vessel v)
        {
            EnsureNodeTextures();
            var sel = SelectedNode(v);
            bool locked = sel != null && Locked(u, v, sel);
            for (int k = 0; k < nodeViews.Count; k++)
            {
                var nv = nodeViews[k];
                if (!nv.Visible) continue;
                if (k == selView && !locked)
                    for (int i = 0; i < 6; i++)
                    {
                        var col = i < 2 ? ProColor : i < 4 ? NrmColor : RadColor;
                        var h = nv.Gui + handleOff[i];
                        if (drag == NodeDrag.Handle && dragAxis == i) h = dragRest + dragDir * dragPull;
                        DrawLine(nv.Gui, h, 2, new Color(col.r, col.g, col.b, 0.7f));
                        GUI.color = col;
                        // Сплошной кружок — «плюс» оси (по скорости, нормаль, наружу), кольцо — противоположная.
                        GUI.DrawTexture(new Rect(h.x - HandleDotPx * 0.5f, h.y - HandleDotPx * 0.5f, HandleDotPx, HandleDotPx), i % 2 == 0 ? discTex : ringTex);
                    }
                // Ромб — квадрат, повёрнутый на 45°.
                var m = GUI.matrix;
                GUIUtility.RotateAroundPivot(45, nv.Gui);
                float s = k == selView ? MarkerPx : MarkerPx * 0.8f;
                GUI.color = Color.black;
                GUI.DrawTexture(new Rect(nv.Gui.x - s * 0.5f - 1.5f, nv.Gui.y - s * 0.5f - 1.5f, s + 3, s + 3), Texture2D.whiteTexture);
                GUI.color = k == selView ? SelectedNodeColor : NodeColor;
                GUI.DrawTexture(new Rect(nv.Gui.x - s * 0.5f, nv.Gui.y - s * 0.5f, s, s), Texture2D.whiteTexture);
                GUI.matrix = m;
            }
            GUI.color = Color.white;

            // Подсказка у руки во время перетаскивания: что меняем и сколько уже.
            if (drag == NodeDrag.Handle && dragNode != null || drag == NodeDrag.Marker && dragNode != null)
            {
                string text;
                if (drag == NodeDrag.Handle)
                {
                    double c = dragAxis / 2 == 0 ? dragNode.Prograde : dragAxis / 2 == 1 ? dragNode.Normal : dragNode.Radial;
                    text = $"{HandleNames[dragAxis]}: {c:0.0} м/с · Δv {dragNode.Total:0.0} м/с" + (AltHeld ? " · точно" : "");
                }
                else text = $"узел через {GameCalendar.FormatDuration(dragNode.Time - u.Time)}";
                var mg = MouseGui();
                var size = markStyle.CalcSize(new GUIContent(text));
                GUI.Label(new Rect(mg.x + 18, mg.y + 14, size.x, size.y), text, markStyle);
            }

        }

        /// <summary>Мышь над окном манёвров или меню щелчка — подписи тел под ними не перехватывают нажатие.</summary>
        bool NodeUiContains(Vector2 m) => nodeWindowOpen && nodeWindowRect.Contains(m) || popupOpen && popupRect.Contains(m);

        /// <summary>Меню «Добавить манёвр» у точки щелчка — поверх панелей (рисуется после них).</summary>
        void NodePopup(Universe u, Vessel v)
        {
            if (!popupOpen) { popupRect = default; return; }
            NodeStyles();
            if (popupTime < u.Time + NodePlanner.MinLead) { popupOpen = false; return; }
            float w = 210, h = 58;
            popupRect = new Rect(Mathf.Min(popupAt.x + 14, Screen.width - w - 4), Mathf.Clamp(popupAt.y - 10, 4, Screen.height - h - 4), w, h);
            GUI.color = NodeColor;
            GUI.DrawTexture(new Rect(popupAt.x - 4, popupAt.y - 4, 8, 8), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Box(popupRect, GUIContent.none);
            GUI.Box(popupRect, GUIContent.none);
            HudHits.Add(popupRect);
            GUI.Label(new Rect(popupRect.x + 8, popupRect.y + 4, w - 40, 20), $"через {GameCalendar.FormatDuration(popupTime - u.Time)}", nodeCellLeft);
            if (GUI.Button(new Rect(popupRect.xMax - 28, popupRect.y + 4, 24, 20), "✕", nodeBtn)) popupOpen = false;
            if (GUI.Button(new Rect(popupRect.x + 8, popupRect.y + 28, w - 16, 24), "Добавить манёвр", nodeBtn))
            {
                var n = NodePlanner.InsertAt(v, u.Time, popupTime);
                if (n != null) { selected = n; nextPredict = 0; }
                else u.Post(NodePlanner.CanPlan(v) ? $"Здесь узел не встанет: ближе {NodePlanner.MinGap:0} с к другому узлу или {NodePlanner.MinLead:0} с к «сейчас»" : "Манёвр можно планировать только в полёте");
                popupOpen = false;
            }
        }

        /// <summary>
        /// Нажатие ЛКМ по карте — после всех панелей OnGUI: кнопки уже забрали событие (тип Used), фон панелей
        /// отсекают HudHits и рамка окна. Порядок: ручки выбранного узла → ромбы → щелчок по линии (на отпускании).
        /// </summary>
        void NodesMouse(Universe u, Vessel v)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0) return;
            var m = e.mousePosition;
            if (popupOpen && popupRect.Contains(m)) return;
            popupOpen = false;
            if (nodeWindowOpen && nodeWindowRect.Contains(m)) return;
            if (HudHits.Contains(new Vector3(m.x, Screen.height - m.y))) return;
            GUIUtility.keyboardControl = 0; // щелчок мимо окна снимает фокус с поля — клавиши полёта снова живы
            var sel = SelectedNode(v);
            if (selView >= 0 && selView < nodeViews.Count && nodeViews[selView].Visible && !Locked(u, v, sel))
            {
                var c = nodeViews[selView].Gui;
                for (int i = 0; i < 6; i++)
                    if (Vector2.Distance(m, c + handleOff[i]) < HandleHitPx)
                    {
                        drag = NodeDrag.Handle;
                        dragNode = sel;
                        dragAxis = i;
                        dragRest = c + handleOff[i];
                        dragDir = handleOff[i].normalized;
                        dragPull = 0;
                        dragFrame = NodePlanner.FrameOf(v, u.Time, sel);
                        e.Use();
                        return;
                    }
            }
            for (int k = 0; k < nodeViews.Count; k++)
            {
                var nv = nodeViews[k];
                if (!nv.Visible || Vector2.Distance(m, nv.Gui) > MarkerHitPx) continue;
                selected = nv.Node;
                e.Use();
                if (Locked(u, v, nv.Node)) return;
                int idx = v.Nodes.IndexOf(nv.Node);
                drag = NodeDrag.Marker;
                dragNode = nv.Node;
                lastMouse = m;
                dragPatches = NodePlanner.PatchesBefore(v, u.Time, idx);
                NodePlanner.TimeRange(v, u.Time, idx, dragPatches, out dragLo, out dragHi);
                return;
            }
            drag = NodeDrag.Click;
            dragStart = m;
        }

        static void DrawLine(Vector2 a, Vector2 b, float w, Color c)
        {
            var d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            var m = GUI.matrix;
            GUI.color = c;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            GUI.DrawTexture(new Rect(a.x, a.y - w * 0.5f, len, w), Texture2D.whiteTexture);
            GUI.matrix = m;
        }

        void EnsureNodeTextures()
        {
            if (discTex != null) return;
            discTex = Circle(false);
            ringTex = Circle(true);
        }

        /// <summary>Кружок 32 px со сглаженным краем; ring — кольцо (противоположные ручки).</summary>
        static Texture2D Circle(bool ring)
        {
            const int n = 32;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float r = new Vector2(x + 0.5f - n * 0.5f, y + 0.5f - n * 0.5f).magnitude;
                    float a = Mathf.Clamp01(n * 0.5f - r);
                    if (ring) a *= Mathf.Clamp01(r - (n * 0.5f - 6));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        // ---------------------------------------------------------------- окно манёвров

        void NodeStyles()
        {
            if (nodeCell != null) return;
            nodeCell = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleRight, richText = true };
            nodeCellLeft = new GUIStyle(nodeCell) { alignment = TextAnchor.MiddleLeft };
            nodeBtn = new GUIStyle(GUI.skin.button) { fontSize = 13, padding = new RectOffset(2, 2, 1, 1) };
            nodeField = new GUIStyle(GUI.skin.textField) { fontSize = 14, alignment = TextAnchor.MiddleRight };
            nodeRow = new GUIStyle(GUI.skin.button) { padding = new RectOffset(0, 0, 0, 0) };
        }

        /// <summary>Колонки списка, px: №, T−, Δv, прожиг, старт прожига.</summary>
        static readonly float[] NodeCols = { 34, 130, 100, 110, 156 };

        void NodeWindow(Universe u, Vessel v)
        {
            if (!nodeWindowOpen) { nodeWindowRect = default; return; }
            NodeStyles();
            var e = Event.current;
            // Enter/Esc в поле — снять фокус: иначе клавиши полёта молчат (Typing).
            if (e.type == EventType.KeyDown && GUIUtility.keyboardControl != 0 &&
                (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.keyCode == KeyCode.Escape))
            {
                GUIUtility.keyboardControl = 0;
                e.Use();
            }
            double now = u.Time;
            var sel = SelectedNode(v);
            int shown = Mathf.Min(v.Nodes.Count, WindowRows);
            float pad = 10, rowH = 24, w = pad * 2;
            foreach (var c in NodeCols) w += c;
            float h = pad * 2 + rowH * 2 + rowH * Mathf.Max(1, shown) + (v.Nodes.Count > WindowRows ? rowH : 0) + 6 + rowH;
            if (sel != null) h += 10 + rowH * 9 + 18;
            // Место меню перелётов (справа от списка фокуса): окна не совмещаются (ToggleNodeWindow).
            var box = new Rect(12 + 230 + 16, 230, w, h);
            nodeWindowRect = box;
            GUI.color = Color.white;
            GUI.Box(box, GUIContent.none);
            GUI.Box(box, GUIContent.none);
            HudHits.Add(box);
            float x0 = box.x + pad, y = box.y + pad, iw = w - pad * 2;

            GUI.Label(new Rect(x0, y, iw - 30, rowH), $"<b>Манёвры</b> · {KeyBinds.Label(Bind.NodeWindow)} — окно, ЛКМ по орбите — новый узел, Alt — точно", nodeCellLeft);
            if (GUI.Button(new Rect(box.xMax - pad - 26, y, 26, rowH - 2), "✕", nodeBtn)) { nodeWindowOpen = false; GUIUtility.keyboardControl = 0; }
            y += rowH;
            string[] heads = { "№", "T−", "Δv, м/с", "Прожиг", "Старт прожига" };
            float cx = x0;
            for (int i = 0; i < heads.Length; i++) { GUI.Label(new Rect(cx, y, NodeCols[i] - 6, rowH), $"<b>{heads[i]}</b>", nodeCell); cx += NodeCols[i]; }
            y += rowH;
            if (v.Nodes.Count == 0)
            {
                GUI.Label(new Rect(x0, y, iw, rowH), "Узлов нет — добавьте кнопкой ниже или щелчком по орбите", nodeCellLeft);
                y += rowH;
            }
            for (int k = 0; k < shown; k++)
            {
                var n = v.Nodes[k];
                bool on = n == sel;
                GUI.color = on ? new Color(0.45f, 0.85f, 1f) : new Color(1, 1, 1, 0.5f);
                if (GUI.Button(new Rect(x0, y, iw, rowH - 2), GUIContent.none, nodeRow)) selected = n;
                GUI.color = Color.white;
                double tLeft = n.Time - now, burn = FlightControl.StagedBurnTime(v, n.Total);
                string lockMark = Locked(u, v, n) ? " ▶" : "";
                string[] cells = { $"{k + 1}{lockMark}", GameCalendar.FormatDuration(tLeft), $"{n.Total:0.0}", BurnText(burn), StartText(tLeft, burn) };
                cx = x0;
                for (int i = 0; i < cells.Length; i++) { GUI.Label(new Rect(cx, y, NodeCols[i] - 6, rowH), cells[i], nodeCell); cx += NodeCols[i]; }
                y += rowH;
            }
            if (v.Nodes.Count > WindowRows)
            {
                GUI.Label(new Rect(x0, y, iw, rowH), $"ещё {v.Nodes.Count - WindowRows} — {KeyBinds.Label(Bind.NodeSelectNext)} перебирает все", nodeCellLeft);
                y += rowH;
            }
            y += 6;

            // Добавление — после последнего узла, на орбите после всех импульсов (NodePlanner.Add).
            float bw = (iw - 3 * 6) / 4;
            if (GUI.Button(new Rect(x0, y, bw, rowH), "+ на Ap", nodeBtn)) AddNode(u, v, NodeAnchor.Apoapsis);
            if (GUI.Button(new Rect(x0 + (bw + 6), y, bw, rowH), "+ на Pe", nodeBtn)) AddNode(u, v, NodeAnchor.Periapsis);
            if (GUI.Button(new Rect(x0 + (bw + 6) * 2, y, bw, rowH), "+ через виток", nodeBtn)) AddNode(u, v, NodeAnchor.NextOrbit);
            GUI.enabled = sel != null;
            if (GUI.Button(new Rect(x0 + (bw + 6) * 3, y, bw, rowH), $"Удалить ({KeyBinds.Label(Bind.NodeDelete)})", nodeBtn) && sel != null)
            {
                if (sel == v.Node) u.NodePilot = null; // как Backspace: исполняемый узел удаляется вместе с автопилотом
                NodePlanner.Delete(v, now, sel);
                nextPredict = 0;
            }
            GUI.enabled = true;
            y += rowH;
            if (sel != null) SelectedEditor(u, v, sel, x0, y + 10, iw, rowH);
            GUI.color = Color.white;
        }

        void AddNode(Universe u, Vessel v, NodeAnchor where)
        {
            var n = NodePlanner.Add(v, u.Time, where);
            if (n != null) { selected = n; nextPredict = 0; }
            else u.Post(NodePlanner.CanPlan(v) ? "Такой точки нет после последнего узла (гипербола или конец прогноза)" : "Манёвр можно планировать только в полёте");
        }

        /// <summary>Числовые поля выбранного узла: компоненты Δv и время, кнопки ±0,1/1/10/100, витки, итог.</summary>
        void SelectedEditor(Universe u, Vessel v, ManeuverNode sel, float x0, float y, float iw, float rowH)
        {
            double now = u.Time;
            int idx = v.Nodes.IndexOf(sel);
            bool locked = Locked(u, v, sel);
            GUI.Label(new Rect(x0, y, iw, rowH), $"<b>Узел {idx + 1}</b>" + (locked ? " — исполняется автопилотом, правки закрыты" : ""), nodeCellLeft);
            y += rowH;
            GUI.enabled = !locked;
            double p = NumRow(x0, y, 0, "По скорости", sel.Prograde, ProColor, rowH);
            double nr = NumRow(x0, y + rowH, 1, "Нормаль", sel.Normal, NrmColor, rowH);
            double r = NumRow(x0, y + rowH * 2, 2, "Радиально", sel.Radial, RadColor, rowH);
            if (!locked && (p != sel.Prograde || nr != sel.Normal || r != sel.Radial))
            {
                NodePlanner.SetComponents(v, now, sel, p, nr, r);
                nextPredict = 0;
            }
            double tLeft = sel.Time - now;
            double t = NumRow(x0, y + rowH * 3, 3, "T−, с", tLeft, Color.white, rowH);
            if (!locked && t != tLeft)
            {
                NodePlanner.SetTime(v, now, sel, now + t);
                nextPredict = 0;
            }
            y += rowH * 4 + 4;
            float bw = (iw - 2 * 6) / 3;
            if (GUI.Button(new Rect(x0, y, bw, rowH), "Виток −1", nodeBtn)) ShiftOrbit(u, v, sel, -1);
            if (GUI.Button(new Rect(x0 + bw + 6, y, bw, rowH), "Виток +1", nodeBtn)) ShiftOrbit(u, v, sel, 1);
            if (GUI.Button(new Rect(x0 + (bw + 6) * 2, y, bw, rowH), $"Скруглить ({KeyBinds.Label(Bind.Circularize)})", nodeBtn))
            {
                NodePlanner.Circularize(v, now, sel);
                nextPredict = 0;
            }
            GUI.enabled = true;
            y += rowH + 4;
            double burn = FlightControl.StagedBurnTime(v, sel.Total);
            GUI.Label(new Rect(x0, y, iw, rowH),
                $"Δv <b>{sel.Total:0.0}</b> м/с · прожиг {BurnText(burn)} · старт прожига {StartText(tLeft, burn)}", nodeCellLeft);
            y += rowH;
            GUI.Label(new Rect(x0, y, iw, rowH * 2 + 4), AfterText(idx), nodeCellLeft);
        }

        /// <summary>Строка поля: подпись, −100…−0,1, поле ввода, +0,1…+100. Возвращает новое значение (или прежнее).</summary>
        double NumRow(float x, float y, int slot, string label, double value, Color col, float rowH)
        {
            GUI.color = col;
            GUI.Label(new Rect(x, y, 96, rowH), label, nodeCellLeft);
            GUI.color = Color.white;
            double nv = value;
            // Пара: 100 + 4·bw + 104 + 4·bw не шире строки окна (сумма NodeCols = 530).
            float bx = x + 100, bw = 40;
            for (int i = FieldSteps.Length - 1; i >= 0; i--)
            {
                if (GUI.Button(new Rect(bx, y, bw - 2, rowH - 2), "−" + StepText(FieldSteps[i]), nodeBtn)) nv = value - FieldSteps[i];
                bx += bw;
            }
            string name = "nodeField" + slot;
            GUI.SetNextControlName(name);
            bool focused = GUI.GetNameOfFocusedControl() == name;
            string shownText = focused && fieldBuf[slot] != null ? fieldBuf[slot] : value.ToString("0.0", CultureInfo.InvariantCulture);
            string s = GUI.TextField(new Rect(bx + 2, y, 96, rowH - 2), shownText, nodeField);
            if (focused)
            {
                // Первый кадр фокуса — только запомнить текст: округлённое «12.3» не должно затирать 12.345.
                if (fieldBuf[slot] == null) fieldBuf[slot] = s;
                else if (s != fieldBuf[slot])
                {
                    fieldBuf[slot] = s;
                    if (double.TryParse(s.Replace(',', '.').Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out double x1) &&
                        !double.IsNaN(x1) && !double.IsInfinity(x1))
                        nv = x1;
                }
            }
            else fieldBuf[slot] = null;
            bx += 104;
            for (int i = 0; i < FieldSteps.Length; i++)
            {
                if (GUI.Button(new Rect(bx, y, bw - 2, rowH - 2), "+" + StepText(FieldSteps[i]), nodeBtn)) nv = value + FieldSteps[i];
                bx += bw;
            }
            return nv;
        }

        static string StepText(double s) => s.ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>Виток ±1: время узла сдвигается на период его орбиты (у гиперболы витка нет).</summary>
        void ShiftOrbit(Universe u, Vessel v, ManeuverNode n, int dir)
        {
            var f = NodePlanner.FrameOf(v, u.Time, n);
            if (!f.Valid || !f.Orbit.IsElliptic) { u.Post("У незамкнутой орбиты витков нет"); return; }
            double before = n.Time;
            NodePlanner.SetTime(v, u.Time, n, n.Time + dir * f.Orbit.Period);
            if (System.Math.Abs(n.Time - (before + dir * f.Orbit.Period)) > 1)
                u.Post(dir > 0 ? "Дальше нельзя: следующий узел или конец прогноза" : "Раньше нельзя: «сейчас» или предыдущий узел");
            nextPredict = 0;
        }

        static string BurnText(double burn) =>
            double.IsInfinity(burn) || double.IsNaN(burn) ? "<color=#ff6050>нет тяги</color>"
            : burn >= 60 ? GameCalendar.FormatDuration(burn) : $"{burn:0} с";

        /// <summary>Реальный старт прожига: середина прожига — на узле, значит старт за полпрожига до него
        /// (FlightControl.StagedBurnTime — с учётом сброса ступеней, как у NodeAutopilot). У узлов очереди
        /// длительность — по сегодняшней массе (топливо предыдущих узлов не вычитается): оценка.</summary>
        static string StartText(double tLeft, double burn)
        {
            if (double.IsInfinity(burn) || double.IsNaN(burn)) return "—";
            double s = tLeft - burn * 0.5;
            if (s >= 0) return "через " + GameCalendar.FormatDuration(s);
            return tLeft >= 0 ? "<color=#ffc840>сейчас!</color>" : "<color=#ffc840>пропущен</color>";
        }

        /// <summary>Орбита после узла idx: Ap/Pe и куда ведёт (встреча, выход из сферы, атмосфера) — до следующего узла.</summary>
        string AfterText(int idx)
        {
            if (patches == null) return "";
            int first = -1;
            for (int i = 0; i < patches.Count; i++)
                if (patches[i].AfterNode && patches[i].NodeIndex == idx) { first = i; break; }
            if (first < 0) return "<color=#a0a0a0>Δv 0 — орбита не меняется</color>";
            var p = patches[first];
            var o = p.Orbit;
            string ap = o.IsElliptic ? NodeDist(o.ApoapsisRadius - p.Body.Radius) : "∞";
            string s = $"После: Ap {ap} · Pe {NodeDist(o.PeriapsisRadius - p.Body.Radius)} ({p.Body.Name})";
            for (int i = first; i < patches.Count && patches[i].NodeIndex == idx; i++)
            {
                var q = patches[i];
                if (q.EndType == TransitionType.Atmosphere) { s += $"\n<color=#ffc840>вход в атмосферу: {q.Body.Name}</color>"; break; }
                if (i + 1 < patches.Count && patches[i + 1].NodeIndex == idx && patches[i + 1].Body != q.Body)
                {
                    var nx = patches[i + 1];
                    s += nx.Body == q.Body.Parent ? $"\n→ выход из сферы {q.Body.Name}"
                        : $"\n→ встреча: {nx.Body.Name}, Pe {NodeDist(nx.Orbit.PeriapsisRadius - nx.Body.Radius)}";
                    break;
                }
            }
            return s;
        }

        static string NodeDist(double m)
        {
            double a = System.Math.Abs(m);
            if (a >= 0.1 * Constants.AU) return (m / Constants.AU).ToString("0.00", CultureInfo.InvariantCulture) + " а.е.";
            if (a >= 1e9) return (m / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + " млн км";
            return (m / 1000).ToString(a >= 1e6 ? "0" : "0.0", CultureInfo.InvariantCulture) + " км";
        }
    }
}
