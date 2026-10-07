using System;
using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Конструктор «как в KSP» (GDD §5.4): деталь тащится мышью из каталога в предпросмотр и прилипает к узлу — снизу,
    /// сверху или между деталями ядра, сбоку с симметрией ×2…×8 или на верх бокового блока. Деталь сборки поднимается
    /// мышью и переставляется; сброшенная на каталог — удаляется. Плюс отмена/повтор, горячие клавиши, вид «ракета /
    /// самолёт» с ракурсами и метки центров масс, давления и тяги. Данные — тот же Craft (стек + боковые группы):
    /// перетаскивание лишь выбирает индекс вставки, родителя и подъём, проверки остаются в CraftCompiler.
    /// </summary>
    public sealed partial class HangarController
    {
        /// <summary>Сдвиг мыши до подъёма детали, px. Больше PartInspector.ClickSlop (6): короткий щелчок — окно детали.</summary>
        const float DragSlop = 8;
        /// <summary>Узел стека ближе NodeSnap px перебивает боковое крепление; дальше NodeReach px узлы не ловятся.</summary>
        const float NodeSnap = 26, NodeReach = 120;
        /// <summary>Шаг подъёма бокового блока над низом родителя, м (как кнопки ±1 в RadialEditor, только мельче).</summary>
        const double LiftStep = 0.5;
        const int UndoMax = 100;
        /// <summary>Строка инструментов над предпросмотром, px IMGUI. Пара: подсказка (GUI.tooltip) рисуется ниже неё.</summary>
        const float ToolH = 32;

        enum ViewMode { Rocket, Plane }
        enum SnapKind { None, Stack, Side, OnBlock }

        /// <summary>Что «в руке»: деталь ядра (с размерами, ступенью и боковыми группами на ней), целая боковая группа
        /// или верх бокового блока / новая деталь из каталога (Ids).</summary>
        sealed class Drag
        {
            public CraftPart Part;
            public List<CraftRadial> Riders;
            public CraftRadial Group;
            public List<string> Ids;
            public bool FromCatalog;
            /// <summary>Сборка до подъёма: отпустили мимо узла — вернуть как было.</summary>
            public string Before;
            public readonly List<PartDef> Defs = new List<PartDef>();
            public double Length;

            public IList<string> PartIds => Part != null ? new[] { Part.Id } : Group != null ? Group.Parts : (IList<string>)Ids;
        }

        struct Snap
        {
            public SnapKind Kind;
            public int Index;
            public double Lift;
        }

        /// <summary>Самолёт лежит: нос (+Y сборки) — вперёд по +Z, брюхо (+X, см. WingMesh) — вниз, размах — по X.</summary>
        static readonly Quaternion PlaneRot = Quaternion.LookRotation(Vector3.left, Vector3.forward);
        static readonly Color GhostOk = new Color(0.45f, 1f, 0.55f), GhostBad = new Color(1f, 0.35f, 0.3f);
        static readonly Color ComColor = new Color(1f, 0.85f, 0.2f), ColColor = new Color(0.3f, 0.8f, 1f), CotColor = new Color(1f, 0.4f, 0.9f);
        static readonly string[] ViewNames = { "3/4", "Сбоку", "Спереди", "Сверху" };

        ViewMode viewMode;
        bool showCenters = true;
        int symmetry = 4;
        readonly List<string> undo = new List<string>(), redo = new List<string>();
        string lastJson;

        Drag drag;
        Snap snap;
        bool pressing, lmbOrbit, catalogDragged;
        int pressStack = -1, pressRadial = -1, pressRadialPart = -1;
        Vector3 pressAt;
        string catalogPress;
        readonly List<Vector3> nodes = new List<Vector3>();
        int nodeHot = -1;

        Transform ghost;
        readonly List<Renderer> ghostR = new List<Renderer>();
        int ghostCopies = -1;
        Bounds craftBounds;
        Texture2D dot;

        // ---------------------------------------------------------------- раскладка

        double[] Layout()
        {
            var d = build?.Design;
            if (d == null || build.PartSection == null) return null;
            var layout = new double[d.Sections.Count];
            new Vessel(d).Layout(layout);
            return layout;
        }

        /// <summary>Низ каждой детали стека — как в BuildParts (без проекта или у обтекателя — подряд).</summary>
        double[] StackBases(double[] layout, out double top)
        {
            var b = new double[craft.Stack.Count];
            double y = 0;
            top = 0;
            for (int i = 0; i < craft.Stack.Count; i++)
            {
                var p = PartCatalog.Resolve(craft.Stack[i]);
                if (p == null) { b[i] = y; continue; }
                b[i] = layout != null && !p.Fairing && i < build.PartSection.Length ? layout[build.PartSection[i]] + build.PartOffset[i] : y;
                y = b[i] + p.Length;
                top = Math.Max(top, y);
            }
            return b;
        }

        Ray LocalRay(Vector3 m)
        {
            var wr = Camera.ScreenPointToRay(m);
            return new Ray(root.InverseTransformPoint(wr.origin), root.InverseTransformDirection(wr.direction));
        }

        // ---------------------------------------------------------------- предпросмотр: пол и кадр

        void RebuildPreview()
        {
            // Destroy срабатывает в конце кадра: без отцепки старые детали попали бы в габарит и копились при
            // нескольких перестройках за кадр (подъём детали + отпускание).
            var old = new List<Transform>();
            foreach (Transform ch in root) old.Add(ch);
            foreach (var ch in old) { ch.SetParent(null, false); Destroy(ch.gameObject); }
            BuildParts();
            FitToFloor();
        }

        /// <summary>
        /// Сборка стоит на полу: низ габарита — на y = 0. Ядро считает высоты от низа стека, а стреловидное крыло, опоры
        /// и боковые блоки с отрицательным подъёмом уходят ниже — так первая деталь «проваливалась» под пол
        /// (крыло: −2,9 м). Самолёт ещё и центруется над полом. Выбор лучом (Hit) идёт в осях root — сдвиг ему не мешает.
        /// </summary>
        void FitToFloor()
        {
            root.SetPositionAndRotation(Vector3.zero, viewMode == ViewMode.Plane ? PlaneRot : Quaternion.identity);
            bool any = false;
            var b = new Bounds();
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!any) { craftBounds = new Bounds(new Vector3(0, 5, 0), new Vector3(10, 10, 10)); return; }
            var shift = new Vector3(viewMode == ViewMode.Plane ? -b.center.x : 0, -b.min.y, viewMode == ViewMode.Plane ? -b.center.z : 0);
            root.position = shift;
            craftBounds = new Bounds(b.center + shift, b.size);
        }

        /// <summary>Камера на всю сборку: дистанция по наибольшему габариту (fov 45° — запас 1,35 и 8 м).</summary>
        void Frame()
        {
            var s = craftBounds.size;
            target = craftBounds.center;
            dist = Mathf.Clamp(Mathf.Max(5, Mathf.Max(s.x, Mathf.Max(s.y, s.z))) * 1.35f + 8, 3, 600);
        }

        void SetView(int preset)
        {
            bool plane = viewMode == ViewMode.Plane;
            // Самолёт носом на +Z: «сбоку» — взгляд по +X, «спереди» — по −Z; сверху с yaw 0 нос смотрит вверх экрана.
            switch (preset)
            {
                case 1: yaw = plane ? 90 : 0; pitch = 0; break;
                case 2: yaw = plane ? 180 : 90; pitch = 0; break;
                case 3: yaw = 0; pitch = 89; break;
                default: yaw = plane ? 215 : 35; pitch = plane ? 22 : 12; break;
            }
            Frame();
        }

        void ToggleView()
        {
            viewMode = viewMode == ViewMode.Rocket ? ViewMode.Plane : ViewMode.Rocket;
            FitToFloor();
            SetView(0);
            Say(viewMode == ViewMode.Plane ? "Вид «самолёт»: нос вперёд, брюхо вниз (V — обратно)" : "Вид «ракета»: нос вверх");
        }

        // ---------------------------------------------------------------- отмена

        /// <summary>Снимок после каждой правки: сравнение JSON ловит любую (кнопки, размеры, ступени, перенос) без
        /// ручных вызовов в каждом месте. Пока деталь в руке — не пишем: перенос — одна запись.</summary>
        void TrackUndo()
        {
            if (drag != null) return;
            var j = craft.ToJson();
            if (lastJson != null && j != lastJson)
            {
                undo.Add(lastJson);
                if (undo.Count > UndoMax) undo.RemoveAt(0);
                redo.Clear();
            }
            lastJson = j;
        }

        void Undo() => Step(undo, redo, "Отменено (Ctrl+Y — вернуть)");
        void Redo() => Step(redo, undo, "Возвращено");

        void Step(List<string> from, List<string> to, string what)
        {
            if (drag != null || from.Count == 0) return;
            to.Add(craft.ToJson());
            craft = current = Craft.FromJson(from[from.Count - 1]);
            from.RemoveAt(from.Count - 1);
            lastJson = craft.ToJson();
            Recompile();
            Say(what);
        }

        // ---------------------------------------------------------------- ввод

        void BuildInput()
        {
            var m = Input.mousePosition;
            if (GUIUtility.keyboardControl == 0) Hotkeys();
            if (drag != null) { DragUpdate(m); return; }
            if (Input.GetMouseButtonDown(0))
            {
                pressing = lmbOrbit = false;
                if (InPreview(m) && !HudHits.Contains(m))
                {
                    pressing = Hit(m, out pressStack, out pressRadial, out pressRadialPart, out _);
                    lmbOrbit = !pressing;
                    pressAt = m;
                }
            }
            if (!Input.GetMouseButton(0)) pressing = lmbOrbit = false;
            if (pressing && (m - pressAt).magnitude > DragSlop * scale) { pressing = false; PickUp(); }
            if (catalogPress != null)
            {
                if (!Input.GetMouseButton(0)) catalogPress = null;
                else if (InPreview(m))
                {
                    // Деталь из каталога вытащили в предпросмотр: клик по кнопке каталога уже не считается.
                    StartDrag(new Drag { Ids = new List<string> { catalogPress }, FromCatalog = true });
                    catalogPress = null;
                    catalogDragged = true;
                }
            }
        }

        void Hotkeys()
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (ctrl && Input.GetKeyDown(KeyCode.Z)) { if (shift) Redo(); else Undo(); }
            if (ctrl && Input.GetKeyDown(KeyCode.Y)) Redo();
            if (ctrl) return;
            if (Input.GetKeyDown(KeyCode.X)) CycleSymmetry(shift ? -1 : 1);
            if (drag != null) return;
            if (Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace)) DeleteSelected();
            if (Input.GetKeyDown(KeyCode.V)) ToggleView();
            if (Input.GetKeyDown(KeyCode.F)) Frame();
            if (Input.GetKeyDown(KeyCode.C)) showCenters = !showCenters;
            for (int i = 0; i < ViewNames.Length; i++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) SetView(i);
        }

        /// <summary>X — симметрия следующей боковой группы; выбранная группа (или группа в руке) меняется сразу.</summary>
        void CycleSymmetry(int dir)
        {
            var s = Craft.Symmetries;
            int k = Math.Max(0, Array.IndexOf(s, symmetry));
            symmetry = s[(k + dir + s.Length) % s.Length];
            if (drag?.Group != null) drag.Group.Symmetry = symmetry;
            else if (drag == null && selRadial >= 0 && selRadial < craft.Radials.Count) { craft.Radials[selRadial].Symmetry = symmetry; dirty = true; }
            Say($"Симметрия ×{symmetry}");
        }

        void DeleteSelected()
        {
            if (selRadial >= 0 && selRadial < craft.Radials.Count) { craft.Radials.RemoveAt(selRadial); selRadial = -1; }
            else if (selStack >= 0 && selStack < craft.Stack.Count) { craft.RemoveAt(selStack); selStack = Math.Min(selStack, craft.Stack.Count - 1); }
            else return;
            dirty = true;
        }

        // ---------------------------------------------------------------- перенос

        void PickUp()
        {
            var d = new Drag { Before = craft.ToJson() };
            if (pressRadial >= 0 && pressRadial < craft.Radials.Count)
            {
                var rad = craft.Radials[pressRadial];
                int j = pressRadialPart, n = rad.Parts.Count;
                if (j > 0 && j < n)
                {
                    // Схватили не за низ: снимаем блок от этой детали и выше (как в KSP — деталь с тем, что на ней).
                    d.Ids = rad.Parts.GetRange(j, n - j);
                    rad.Parts.RemoveRange(j, n - j);
                }
                else
                {
                    d.Group = rad;
                    craft.Radials.RemoveAt(pressRadial);
                    symmetry = rad.Symmetry;
                }
            }
            else if (pressStack >= 0 && pressStack < craft.Stack.Count)
            {
                d.Part = craft.Stack[pressStack];
                d.Riders = craft.Radials.FindAll(r => r.Parent == pressStack);
                craft.RemoveAt(pressStack);
            }
            else return;
            selStack = selRadial = -1;
            StartDrag(d);
        }

        void StartDrag(Drag d)
        {
            drag = d;
            if (d.Part != null) d.Defs.Add(PartCatalog.Resolve(d.Part));
            else foreach (var id in d.PartIds) d.Defs.Add(PartCatalog.Get(id));
            d.Defs.RemoveAll(p => p == null);
            foreach (var p in d.Defs) d.Length += p.Length;
            if (ghost == null) ghost = new GameObject("Drag Ghost").transform;
            ghost.gameObject.SetActive(true);
            ghostCopies = -1;
            // Узлы считаются по сборке без поднятой детали.
            Recompile();
        }

        void DragUpdate(Vector3 m)
        {
            ComputeSnap(m);
            PlaceGhost(m);
            if (Input.GetKeyDown(KeyCode.Escape)) { CancelDrag(); return; }
            if (Input.GetMouseButton(0)) return;
            // Отпустили над каталогом — деталь выброшена (из сборки она уже снята).
            if (m.x / scale < LeftW + Pad && !showFiles)
            {
                bool fromCatalog = drag.FromCatalog;
                EndDrag();
                if (!fromCatalog) Say("Деталь убрана (Ctrl+Z — вернуть)");
                return;
            }
            if (snap.Kind == SnapKind.None) { CancelDrag(); return; }
            Apply();
            EndDrag();
        }

        void CancelDrag()
        {
            if (!drag.FromCatalog) craft = current = Craft.FromJson(drag.Before);
            EndDrag();
        }

        void EndDrag()
        {
            drag = null;
            snap = default;
            nodes.Clear();
            if (ghost != null) ghost.gameObject.SetActive(false);
            dirty = true;
        }

        void Apply()
        {
            var ids = drag.PartIds;
            switch (snap.Kind)
            {
                case SnapKind.Stack:
                    int k = snap.Index;
                    if (drag.Part != null)
                    {
                        craft.Insert(k, drag.Part.Id);
                        craft.Stack[k] = drag.Part; // свои размеры, ступень и парашют
                        foreach (var r in drag.Riders) { r.Parent = k; craft.Radials.Add(r); }
                        selStack = k;
                    }
                    else
                    {
                        for (int n = 0; n < ids.Count; n++) craft.Insert(k + n, ids[n]);
                        selStack = k + ids.Count - 1;
                    }
                    selRadial = -1;
                    break;
                case SnapKind.Side:
                    var g = drag.Group ?? new CraftRadial { Symmetry = symmetry, Parts = new List<string>(ids) };
                    g.Parent = snap.Index;
                    g.Lift = snap.Lift;
                    craft.Radials.Add(g);
                    selRadial = craft.Radials.Count - 1;
                    selStack = snap.Index;
                    if (drag.Riders != null && drag.Riders.Count > 0) Say("Боковые группы детали сняты: на боковом блоке их не бывает");
                    break;
                case SnapKind.OnBlock:
                    var rad = craft.Radials[snap.Index];
                    rad.Parts.AddRange(ids);
                    selRadial = snap.Index;
                    selStack = rad.Parent;
                    break;
            }
        }

        /// <summary>
        /// Куда встанет деталь: на боковой блок (луч попал в него) → на бок детали ядра (луч в боковую стенку, узел стека
        /// не ближе NodeSnap) → в ближайший узел стека (стыки деталей, низ и верх). Пустая сборка — первая деталь в 0.
        /// </summary>
        void ComputeSnap(Vector3 m)
        {
            snap = default;
            nodes.Clear();
            nodeHot = -1;
            if (craft.Stack.Count == 0)
            {
                nodes.Add(Vector3.zero);
                nodeHot = 0;
                snap = new Snap { Kind = SnapKind.Stack, Index = 0 };
                return;
            }
            var layout = Layout();
            var bases = StackBases(layout, out double top);
            for (int k = 0; k <= craft.Stack.Count; k++)
                nodes.Add(new Vector3(0, (float)(k < craft.Stack.Count ? bases[k] : top), 0));

            float best = NodeReach * scale;
            for (int k = 0; k < nodes.Count; k++)
            {
                var sp = Camera.WorldToScreenPoint(root.TransformPoint(nodes[k]));
                if (sp.z <= 0) continue;
                float dd = Vector2.Distance(sp, m);
                if (dd < best) { best = dd; nodeHot = k; }
            }

            Hit(m, out int hs, out int hr, out _, out float t);
            if (hr >= 0 && layout != null)
            {
                snap = new Snap { Kind = SnapKind.OnBlock, Index = hr };
                nodeHot = -1;
                return;
            }
            if (hs >= 0 && layout != null && !(nodeHot >= 0 && best < NodeSnap * scale))
            {
                var p = PartCatalog.Resolve(craft.Stack[hs]);
                var ray = LocalRay(m);
                var hp = ray.origin + ray.direction * t;
                float r = (float)Math.Max(p.Diameter, p.Top) * 0.5f;
                // Боковая стенка, а не торец: крепим только к «бочке» деталей с длиной (не к крылу, обтекателю, опоре).
                bool wall = new Vector2(hp.x, hp.z).magnitude > r * 0.85f;
                if (wall && p.Length > 0.3 && !p.Fairing && p.Wing == null && p.GearHeight <= 0)
                {
                    double lift = Math.Round((hp.y - bases[hs] - drag.Length * 0.5) / LiftStep) * LiftStep;
                    snap = new Snap { Kind = SnapKind.Side, Index = hs, Lift = Math.Max(0, Math.Min(p.Length, lift)) };
                    nodeHot = -1;
                    return;
                }
            }
            // Целая боковая группа в стек не встаёт как группа — её детали встанут в ядро подряд.
            if (nodeHot >= 0) snap = new Snap { Kind = SnapKind.Stack, Index = nodeHot };
        }

        // ---------------------------------------------------------------- призрак

        void PlaceGhost(Vector3 m)
        {
            ghost.SetPositionAndRotation(root.position, root.rotation);
            int copies = 1;
            float radius = 0;
            float baseY = 0;
            Vector3 at = Vector3.zero;
            var layout = Layout();
            switch (snap.Kind)
            {
                case SnapKind.Stack:
                {
                    // Внизу — деталь под сборкой, вверху — над ней, между — по центру стыка (вставка раздвинет стек).
                    float y = nodes[snap.Index].y, len = (float)drag.Length;
                    baseY = snap.Index == 0 && craft.Stack.Count > 0 ? y - len : snap.Index >= craft.Stack.Count ? y : y - len * 0.5f;
                    break;
                }
                case SnapKind.Side:
                {
                    var parent = PartCatalog.Resolve(craft.Stack[snap.Index]);
                    double gr = 0;
                    foreach (var p in drag.Defs) gr = Math.Max(gr, Math.Max(p.Diameter, p.Top) * 0.5);
                    // Как CraftCompiler.RadialSection: RadialOffset = радиус родителя + радиус блока + RadialGap.
                    radius = (float)(Math.Max(parent.Diameter, parent.Top) * 0.5 + gr + CraftCompiler.RadialGap);
                    copies = drag.Group?.Symmetry ?? symmetry;
                    baseY = (float)(StackBases(layout, out _)[snap.Index] + snap.Lift);
                    break;
                }
                case SnapKind.OnBlock:
                {
                    var rad = craft.Radials[snap.Index];
                    var s = build.Design.Sections[build.RadialSection[snap.Index]];
                    radius = (float)s.RadialOffset;
                    copies = s.RadialCount;
                    double h = layout[build.RadialSection[snap.Index]];
                    foreach (var id in rad.Parts) h += PartCatalog.Get(id)?.Length ?? 0;
                    baseY = (float)h;
                    break;
                }
                default:
                {
                    // Мимо узлов — висит под мышью на глубине цели камеры.
                    var ray = LocalRay(m);
                    float depth = Mathf.Max(3, dist * 0.8f);
                    at = ray.origin + ray.direction * depth;
                    baseY = at.y - (float)drag.Length * 0.5f;
                    at.y = 0;
                    break;
                }
            }
            if (copies != ghostCopies) BuildGhost(copies);
            for (int c = 0; c < copies; c++)
            {
                var tr = ghost.GetChild(c);
                float ang = 2 * Mathf.PI * c / copies;
                var dir = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
                tr.localPosition = at + dir * radius + Vector3.up * baseY;
                tr.localRotation = radius > 0 ? Quaternion.Euler(0, -ang * Mathf.Rad2Deg, 0) : Quaternion.identity;
            }
            mpb.Clear();
            mpb.SetColor("_BaseColor", snap.Kind == SnapKind.None ? GhostBad : GhostOk);
            foreach (var r in ghostR) r.SetPropertyBlock(mpb);
        }

        void BuildGhost(int copies)
        {
            var old = new List<Transform>();
            foreach (Transform ch in ghost) old.Add(ch);
            foreach (var ch in old) { ch.SetParent(null, false); Destroy(ch.gameObject); }
            ghostR.Clear();
            for (int c = 0; c < copies; c++)
            {
                var copy = new GameObject("Copy " + c).transform;
                copy.SetParent(ghost, false);
                float y = 0;
                foreach (var p in drag.Defs)
                {
                    var holder = new GameObject(p.Name).transform;
                    holder.SetParent(copy, false);
                    holder.localPosition = Vector3.up * y;
                    PartShapes.Preview(p, (name, mesh, col, pos, q, sc) =>
                    {
                        var t = Add(name, mesh, col, false, holder);
                        t.localPosition = pos;
                        t.localRotation = q;
                        t.localScale = sc;
                    });
                    y += (float)p.Length;
                }
                ghostR.AddRange(copy.GetComponentsInChildren<Renderer>());
            }
            foreach (var r in ghostR) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ghostCopies = copies;
        }

        // ---------------------------------------------------------------- IMGUI

        /// <summary>Строка над предпросмотром: вид, ракурсы, центры, симметрия, отмена.</summary>
        void ViewBar()
        {
            float x = LeftW + 2 * Pad, y = TopH + 4, h = 26;
            bool plane = viewMode == ViewMode.Plane;
            if (Tool(ref x, y, 150, h, new GUIContent(plane ? "Вид: самолёт (V)" : "Вид: ракета (V)",
                    "Самолёт: сборка лежит носом вперёд, брюхом вниз — удобно ставить крылья, кили и шасси"), plane)) ToggleView();
            x += 6;
            for (int i = 0; i < ViewNames.Length; i++)
                if (Tool(ref x, y, 66, h, new GUIContent(ViewNames[i], $"Ракурс ({i + 1}); F — показать всю сборку"), false)) SetView(i);
            x += 6;
            if (Tool(ref x, y, 104, h, new GUIContent("Центры (C)",
                    "Жёлтый — центр масс, голубой — центр давления крыльев, розовый — тяга первой ступени. ЦД позади ЦМ — устойчиво"), showCenters))
                showCenters = !showCenters;
            if (Tool(ref x, y, 132, h, new GUIContent($"Симметрия ×{symmetry} (X)", "Сколько боковых блоков ставит перетаскивание на бок детали"), false))
                CycleSymmetry(1);
            x += 6;
            GUI.enabled = undo.Count > 0 && drag == null;
            if (Tool(ref x, y, 84, h, new GUIContent("Отменить", "Ctrl+Z"), false)) Undo();
            GUI.enabled = redo.Count > 0 && drag == null;
            if (Tool(ref x, y, 84, h, new GUIContent("Вернуть", "Ctrl+Y"), false)) Redo();
            GUI.enabled = true;
        }

        bool Tool(ref float x, float y, float w, float h, GUIContent c, bool on)
        {
            var r = new Rect(x, y, w, h);
            x += w + 4;
            HudHits.Add(r);
            GUI.color = on ? SelColor : Color.white;
            bool hit = GUI.Button(r, c, btnSmall);
            GUI.color = Color.white;
            return hit;
        }

        /// <summary>Подсказка внизу предпросмотра: что произойдёт при отпускании или какие есть приёмы.</summary>
        void HelpLine()
        {
            string text;
            if (drag != null)
            {
                string what = drag.Defs.Count > 0 ? drag.Defs[0].Name : "деталь";
                switch (snap.Kind)
                {
                    case SnapKind.Stack:
                        text = craft.Stack.Count == 0 ? $"«{what}» — первая деталь сборки"
                            : snap.Index == 0 ? $"«{what}» встанет под сборку"
                            : snap.Index >= craft.Stack.Count ? $"«{what}» встанет наверх"
                            : $"«{what}» встанет между деталями";
                        break;
                    case SnapKind.Side:
                        text = $"«{what}» — боковые ×{drag.Group?.Symmetry ?? symmetry} (X — сменить), подъём {snap.Lift:0.0} м";
                        break;
                    case SnapKind.OnBlock: text = $"«{what}» — на верх бокового блока"; break;
                    default: text = "Ведите к сборке: зелёный — встанет; над каталогом — удалить; Esc — отмена"; break;
                }
            }
            else text = "Тащите детали из каталога в сборку · ЛКМ по детали — перенести · Del — удалить · Ctrl+Z / Ctrl+Y · ПКМ — вращать, СКМ — сдвиг";
            float l = LeftW + 2 * Pad, w = vw - RightW - Pad - l;
            var r = new Rect(l, vh - StatsH - Pad - 30, w, 26);
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(r.x + (r.width - Mathf.Min(r.width, small.CalcSize(new GUIContent(text)).x + 24)) * 0.5f, r.y,
                Mathf.Min(r.width, small.CalcSize(new GUIContent(text)).x + 24), r.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            var st = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(r, text, st);
        }

        /// <summary>Узлы стека во время переноса: точки на стыках, выбранный — крупный зелёный.</summary>
        void NodeMarks()
        {
            if (drag == null) return;
            for (int k = 0; k < nodes.Count; k++)
            {
                bool hot = snap.Kind == SnapKind.Stack && snap.Index == k;
                Mark(nodes[k], hot ? GhostOk : new Color(1, 1, 1, 0.8f), hot ? 18 : 10, null);
            }
        }

        /// <summary>
        /// Метки центров (§5.4): ЦМ — Vessel.MassProperties; ЦД — крылья и кили по площади, у каждой плоскости — 1/4
        /// средней хорды на середине консоли (стреловидность сдвигает её назад на b/4·tg χ, у киля — b/2·tg χ), как
        /// в Aerodynamics; корпус не считаем. ЦТ — двигатели первого пуска, по тяге.
        /// </summary>
        void Centers()
        {
            if (!showCenters || build?.Design == null || drag != null) return;
            var d = build.Design;
            var layout = Layout();
            if (layout == null) return;
            Mark(new Vector3(0, (float)build.ComHeight, 0), ComColor, 20, "ЦМ");

            double area = 0, ay = 0;
            for (int i = 0; i < d.Sections.Count; i++)
            {
                var s = d.Sections[i];
                if (s.Wings == null) continue;
                int n = s.IsRadial ? Math.Max(1, s.RadialCount) : 1;
                foreach (var w in s.Wings)
                {
                    double tg = Math.Tan(w.Sweep * Math.PI / 180);
                    double y = layout[i] + w.Height - (w.Vertical ? 0.5 : 0.25) * w.Span * tg;
                    area += w.Area * n;
                    ay += w.Area * n * y;
                }
            }
            if (area > 0) Mark(new Vector3(0, (float)(ay / area), 0), ColColor, 16, "ЦД");

            int first = int.MaxValue;
            foreach (var cp in craft.Stack) if (PartCatalog.Resolve(cp)?.HasEngine == true && cp.Stage >= 0) first = Math.Min(first, cp.Stage);
            foreach (var rad in craft.Radials)
                if (rad.EngineStage >= 0 && rad.Parts.Exists(id => PartCatalog.Get(id)?.HasEngine == true)) first = Math.Min(first, rad.EngineStage);
            if (first == int.MaxValue) return;
            double thrust = 0, ty = 0;
            var bases = StackBases(layout, out _);
            for (int i = 0; i < craft.Stack.Count; i++)
            {
                var p = PartCatalog.Resolve(craft.Stack[i]);
                if (p == null || !p.HasEngine || craft.Stack[i].Stage != first) continue;
                double f = p.Engine().ThrustVac * p.EngineCount;
                thrust += f;
                ty += f * bases[i];
            }
            for (int g = 0; g < craft.Radials.Count; g++)
            {
                var rad = craft.Radials[g];
                if (rad.EngineStage != first || g >= build.RadialSection.Length) continue;
                double h = layout[build.RadialSection[g]];
                foreach (var id in rad.Parts)
                {
                    var p = PartCatalog.Get(id);
                    if (p == null) continue;
                    if (p.HasEngine)
                    {
                        double f = p.Engine().ThrustVac * p.EngineCount * rad.Symmetry;
                        thrust += f;
                        ty += f * h;
                    }
                    h += p.Length;
                }
            }
            if (thrust > 0) Mark(new Vector3(0, (float)(ty / thrust), 0), CotColor, 14, "ЦТ");
        }

        void Mark(Vector3 local, Color c, float size, string text)
        {
            var sp = Camera.WorldToScreenPoint(root.TransformPoint(local));
            if (sp.z <= 0) return;
            var p = new Vector2(sp.x / scale, (Screen.height - sp.y) / scale);
            if (dot == null) dot = Dot();
            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(new Rect(p.x - size * 0.5f - 2, p.y - size * 0.5f - 2, size + 4, size + 4), dot);
            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - size * 0.5f, p.y - size * 0.5f, size, size), dot);
            GUI.color = Color.white;
            if (text == null) return;
            var st = new GUIStyle(small) { fontStyle = FontStyle.Bold };
            st.normal.textColor = c;
            GUI.Label(new Rect(p.x + size * 0.5f + 4, p.y - 11, 60, 22), text, st);
        }

        static Texture2D Dot()
        {
            const int n = 32;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float r = new Vector2(x - n * 0.5f + 0.5f, y - n * 0.5f + 0.5f).magnitude;
                    t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(n * 0.5f - r)));
                }
            t.Apply();
            return t;
        }
    }
}
