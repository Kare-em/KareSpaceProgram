using System;
using System.Collections.Generic;
using System.IO;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kare.Space.Game
{
    /// <summary>
    /// Конструктор ракет (GDD §5.4), сцена Hangar: каталог деталей, стек ядра и боковые блоки с симметрией 2/3/4/6/8,
    /// ступени, Δv/TWR, проверки перед стартом, JSON-сохранения и запуск в Flight. Сборка и проверки — в ядре
    /// (Craft, CraftCompiler, те же, что гоняют тесты); здесь только IMGUI и предпросмотр из примитивов.
    /// </summary>
    public sealed partial class HangarController : MonoBehaviour
    {
        public const string SceneName = "Hangar", FlightScene = "Flight";
        /// <summary>Миссия, из которой пришли (Esc → «В конструктор»): цель и стол для запуска.</summary>
        public static string ReturnMissionId;
        /// <summary>Текущая ракета: переживает уход в полёт и возврат (и сохраняется в _last.json).</summary>
        static Craft current;

        [Tooltip("HDRP/Lit для деталей; цвет — через MaterialPropertyBlock (VesselLit.mat).")]
        public Material Material;
        public Camera Camera;

        /// <summary>Опорное разрешение IMGUI: всё рисуется в 1920 × 1080 и масштабируется по высоте экрана.</summary>
        const float RefHeight = 1080;
        /// <summary>Ширина каталога слева и колонки сборки справа. Пара: зона мыши камеры (PreviewArea).</summary>
        const float LeftW = 360, RightW = 440, TopH = 52, Pad = 8, Row = 30;
        /// <summary>Высота сводки внизу: развёрнутая (таблица ступеней + проверки) и свёрнутая в одну строку.
        /// Пара: зона мыши камеры и строка подсказки (InPreview, HelpLine) берут текущую — StatsH.</summary>
        const float StatsOpenH = 268, StatsClosedH = 44, StagingClosedH = 40;
        /// <summary>Выпадающий список: высота строки и предел высоты (дальше — прокрутка).</summary>
        const float PopupRow = 30, PopupMaxH = 620;
        /// <summary>Иконки деталей (PartIconSet), px IMGUI: в каталоге — в строку высотой 52, в стеке — в строку Row = 30.</summary>
        const float CatalogIcon = 48, StackIcon = 26, IconPad = 3;

        /// <summary>
        /// Единая точка фильтра каталога для режима карьеры: false — деталь ещё не открыта (не показана в каталоге, а в
        /// сборке — ошибка «не открыта», запуск закрыт). По умолчанию открыто всё. Сменили правило — CatalogChanged().
        /// </summary>
        public static Func<PartDef, bool> PartAvailable = _ => true;
        /// <summary>Сбросить кеш каталога и перепроверить сборку после смены PartAvailable (открыта новая деталь).</summary>
        public static void CatalogChanged() => catalogVersion++;
        static int catalogVersion;
        int catListVersion = -1, checkedVersion = -1;

        Craft craft;
        CraftBuild build;
        /// <summary>Бюджет Δv выбранной миссии (CraftAnalysis.Budget) и для какой миссии он посчитан: смена цели — пересборка.</summary>
        DeltaVBudget budget;
        int budgetFor = -1;
        /// <summary>Система тел для бюджета: эфемериды нужны только радиусам орбит, одна на сцену.</summary>
        static SolarSystem budgetSystem;
        /// <summary>Подсвеченная ступень сводки (строка под мышью или закреплённая кликом), −1 — нет; секции — из StageStats.Sections.</summary>
        int hoverStage = -1, pinnedStage = -1, shownStage = -1;
        bool dirty = true;
        int selStack = -1, selRadial = -1;
        PartCategory category = PartCategory.Tank;
        Vector2 partsScroll, stackScroll, stageScroll, filesScroll, statsScroll;
        int missionIdx;
        bool showFiles;
        string[] files = Array.Empty<string>();
        /// <summary>Файл, ждущий подтверждения удаления (-1 — нет): удаление необратимо, поэтому в два клика.</summary>
        int confirmDelete = -1;
        /// <summary>Свёрнутые панели: место отдаётся предпросмотру (сводка) и стеку сборки (ступени).</summary>
        bool statsOpen = true, stagingOpen = true;
        float StatsH => statsOpen ? StatsOpenH : StatsClosedH;

        /// <summary>Открытый выпадающий список: GUI.Window — он рисуется поверх и получает клики раньше панелей.</summary>
        enum Popup { None, Presets, Missions }
        Popup popup;
        Rect popupRect;
        Vector2 popupScroll;
        /// <summary>Имена пресетов — один раз; сами пресеты строятся заново по клику, чтобы правка не портила образец.</summary>
        string[] presetNames;

        /// <summary>Поиск по всем категориям каталога и кеш списка: пересобирается при смене категории или запроса.</summary>
        string search = "";
        readonly List<PartDef> catList = new List<PartDef>();
        PartCategory catListCat = (PartCategory)(-1);
        string catListQuery;
        string status;
        float statusUntil;
        float scale = 1, vw = 1920, vh = 1080;

        Transform root, fairing;
        Material fairingMat;
        MaterialPropertyBlock mpb;
        float yaw = 35, pitch = 12, dist = 70;
        Vector3 target = new Vector3(0, 20, 0);
        Vector3 lastMouse;

        GUIStyle head, label, small, btn, btnSmall, tab, rowBtn, rowIconBtn, stackIconBtn, err, warn, box, helpStyle, markStyle, toastStyle, hintStyle;

        static readonly string[] CategoryNames = { "Командные", "Баки", "Двигатели", "Разделители", "Аэро", "Разное", "Нагрузка" };
        static readonly Color SelColor = new Color(1f, 0.55f, 0.15f);
        /// <summary>Подсветка ступени из сводки (и её полоса на шкале Δv) — холодный цвет, не путается с выбором SelColor.</summary>
        static readonly Color StageColor = new Color(0.25f, 0.8f, 1f);

        static string Dir => Path.Combine(Application.persistentDataPath, "Crafts");
        static string LastPath => Path.Combine(Dir, "_last.json");

        // ---------------------------------------------------------------- жизненный цикл

        void Start()
        {
            if (current == null)
            {
                try { if (File.Exists(LastPath)) current = Craft.FromJson(File.ReadAllText(LastPath)); }
                catch (Exception e) { Debug.LogWarning($"[Hangar] _last.json не читается: {e.Message}"); }
            }
            craft = current ?? CraftPresets.Semyorka();
            current = craft;
            var all = MissionCatalog.All;
            string mid = ReturnMissionId ?? GameBootstrap.NextMissionId ?? "sputnik";
            for (int i = 0; i < all.Count; i++) if (all[i].Id == mid) missionIdx = i;
            mpb = new MaterialPropertyBlock();
            fairingMat = new Material(Material != null ? Material : new Material(Shader.Find("HDRP/Lit"))) { name = "Fairing (cutaway)" };
            // Створка в разрезе видна изнутри: HDRP-материалы в рантайме не валидируются — двусторонность ставим ключами.
            fairingMat.SetFloat("_DoubleSidedEnable", 1);
            fairingMat.EnableKeyword("_DOUBLESIDED_ON");
            fairingMat.SetFloat("_CullMode", 0);
            fairingMat.SetFloat("_CullModeForward", 0);
            root = new GameObject("Craft Preview").transform;
            Floor();
            Recompile(frame: true);
        }

        void Recompile(bool frame = false)
        {
            CraftCompiler.AutoStage(craft, onlyUnset: true);
            CraftCompiler.Normalize(craft);
            if (budgetFor != missionIdx)
            {
                budgetFor = missionIdx;
                if (budgetSystem == null) budgetSystem = SolarSystem.CreateReal();
                try { budget = CraftAnalysis.Budget(MissionCatalog.All[missionIdx], budgetSystem); }
                catch (Exception e) { budget = null; Debug.LogWarning($"[Hangar] бюджет Δv: {e.Message}"); }
            }
            build = CraftCompiler.Compile(craft, budget);
            CheckAvailable();
            checkedVersion = catalogVersion;
            if (selStack >= craft.Stack.Count) selStack = craft.Stack.Count - 1;
            if (selRadial >= craft.Radials.Count) selRadial = -1;
            TrackUndo();
            RebuildPreview();
            if (frame) Frame();
            dirty = false;
        }

        void Update()
        {
            // Сменили миссию (◄ ►, список) или карьера открыла деталь — пересчитать бюджет и проверки.
            if (budgetFor != missionIdx || checkedVersion != catalogVersion) dirty = true;
            if (dirty) Recompile();
            BuildInput();
            if (dirty) Recompile();
            OrbitCamera();
            if (statusUntil > 0 && Time.time > statusUntil) status = null;
        }

        // ---------------------------------------------------------------- камера

        /// <summary>Зона предпросмотра в экранных пикселях (между панелями): мышь здесь крутит камеру, а не IMGUI.</summary>
        bool InPreview(Vector3 m)
        {
            float x = m.x / scale, y = (Screen.height - m.y) / scale;
            return x > LeftW + Pad && x < vw - RightW - Pad && y > TopH && y < vh - StatsH - Pad && !showFiles && popup == Popup.None
                && !PartInspector.OverWindow(m); // окно детали над предпросмотром: тащим окно, а не крутим камеру
        }

        void OrbitCamera()
        {
            if (Camera == null) return;
            var m = Input.mousePosition;
            if (InPreview(m))
            {
                // ПКМ (и ЛКМ по пустому месту) — вращение, СКМ — сдвиг в плоскости экрана, колесо — приближение.
                // ЛКМ по детали — перенос (BuildInput), поэтому камеру она не крутит.
                if (Input.GetMouseButton(1) || lmbOrbit && Input.GetMouseButton(0) && !Input.GetMouseButtonDown(0))
                {
                    var d = m - lastMouse;
                    yaw += d.x * 0.3f;
                    pitch = Mathf.Clamp(pitch - d.y * 0.3f, -10, 89);
                }
                if (Input.GetMouseButton(2))
                {
                    var d = m - lastMouse;
                    var ct = Camera.transform;
                    target -= (ct.right * d.x + ct.up * d.y) * dist * 0.0015f;
                    target.y = Mathf.Max(0, target.y);
                }
                float wheel = Input.mouseScrollDelta.y;
                if (wheel != 0) dist = Mathf.Clamp(dist * Mathf.Pow(0.88f, wheel), 3, 600);
            }
            lastMouse = m;
            var rot = Quaternion.Euler(pitch, yaw, 0);
            Camera.transform.SetPositionAndRotation(target - rot * Vector3.forward * dist, rot);
            // Створка (половина +X) всегда на дальней от камеры стороне: Euler(0, θ) несёт +X в (cos θ, 0, −sin θ) —
            // совмещаем с взглядом камеры в осях сборки (в виде «самолёт» сборка повёрнута, yaw уже не годится).
            if (fairing != null)
            {
                var f = root.InverseTransformDirection(Camera.transform.forward);
                fairing.localRotation = Quaternion.Euler(0, Mathf.Atan2(-f.z, f.x) * Mathf.Rad2Deg, 0);
            }
        }

        // ---------------------------------------------------------------- предпросмотр

        void Floor()
        {
            var go = new GameObject("Floor");
            var f = go.AddComponent<MeshFilter>();
            f.sharedMesh = ProcMesh.Frustum(60, 60, 0.3f, 64, true);
            go.transform.position = new Vector3(0, -0.3f, 0);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Material;
            var b = new MaterialPropertyBlock();
            b.SetColor("_BaseColor", new Color(0.18f, 0.19f, 0.2f));
            r.SetPropertyBlock(b);
        }

        void BuildParts()
        {
            fairing = null;
            // Высоты деталей — из скомпилированного проекта (Vessel.Layout), как их поставит полёт; без проекта — подряд.
            double[] layout = null;
            var d = build.Design;
            if (d != null && build.PartSection != null)
            {
                layout = new double[d.Sections.Count];
                new Vessel(d).Layout(layout);
            }
            double y = 0;
            // Крылья рисуются по WingDef, собранным компилятором (CraftCompiler.AddWings) — в той же точке, где их считает
            // аэродинамика; k-я крыльевая деталь секции — k-я плоскость в SectionDef.Wings.
            var wingIdx = new Dictionary<int, int>();
            for (int i = 0; i < craft.Stack.Count; i++)
            {
                var p = PartCatalog.Resolve(craft.Stack[i]);
                if (p == null) continue;
                double baseY = layout != null && !p.Fairing ? layout[build.PartSection[i]] + build.PartOffset[i] : y;
                bool sel = selStack == i && selRadial < 0;
                curSec = build.PartSection != null && i < build.PartSection.Length ? build.PartSection[i] : -1;
                if (p.Wing != null && layout != null)
                {
                    int sec = build.PartSection[i];
                    wingIdx.TryGetValue(sec, out int k);
                    wingIdx[sec] = k + 1;
                    var wings = d.Sections[sec].Wings;
                    var wm = wings != null && k < wings.Count ? WingMesh.Build(wings[k]) : null;
                    if (wm != null)
                    {
                        Add(p.Name, wm, PartColor(p), sel).localPosition = Vector3.up * (float)layout[sec];
                        continue;
                    }
                }
                if (p.Fairing && d != null)
                {
                    int fs = d.Sections.FindIndex(s => s.Kind == SectionKind.Fairing);
                    if (fs >= 0) { curSec = fs; Fairing(d.Sections[fs], (float)layout[fs], sel); continue; }
                }
                Part(p, Vector3.up * (float)baseY, Quaternion.identity, sel);
                y = baseY + p.Length;
            }
            if (layout == null) return;
            for (int g = 0; g < craft.Radials.Count; g++)
            {
                var rad = craft.Radials[g];
                var s = d.Sections[build.RadialSection[g]];
                bool sel = selRadial == g;
                curSec = build.RadialSection[g];
                for (int c = 0; c < s.RadialCount; c++)
                {
                    float ang = 2 * Mathf.PI * c / s.RadialCount;
                    var dir = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
                    var yawQ = Quaternion.Euler(0, -ang * Mathf.Rad2Deg, 0);
                    // Первая деталь блока — радиальный разделитель (CraftCompiler.RadialSection), длина 0.
                    double h = layout[build.RadialSection[g]];
                    foreach (var id in rad.Parts)
                    {
                        var p = PartCatalog.Get(id);
                        if (p == null) continue;
                        Part(p, dir * (float)s.RadialOffset + Vector3.up * (float)h, yawQ, sel);
                        h += p.Length;
                    }
                    // Пилон радиального разделителя от оси ядра к блоку: видно, к чему пристёгнута группа.
                    float inner = (float)(s.RadialOffset - s.Radius);
                    var strut = Add("Strut", ProcMesh.Frustum(0.12f, 0.12f, inner, 8, true), Color.gray, sel);
                    strut.localPosition = Vector3.up * (float)(layout[build.RadialSection[g]] + Math.Min(s.Length * 0.25, 3));
                    strut.localRotation = Quaternion.FromToRotation(Vector3.up, dir);
                }
            }
        }

        static Color PartColor(PartDef p) => PartShapes.ColorOf(p);

        void Part(PartDef p, Vector3 at, Quaternion rot, bool sel)
        {
            var holder = new GameObject(p.Name).transform;
            holder.SetParent(root, false);
            holder.localPosition = at;
            holder.localRotation = rot;
            // Геометрия общая с иконками каталога (PartShapes, меню Kare/Bake Part Icons): иконка = деталь в сборке.
            PartShapes.Preview(p, (name, mesh, col, pos, q, scale) =>
            {
                var t = Add(name, mesh, col, sel, holder);
                t.localPosition = pos;
                t.localRotation = q;
                t.localScale = scale;
            });
        }

        void Fairing(SectionDef s, float baseY, bool sel)
        {
            // Половина обтекателя в разрезе: видно, что под ним, — как «прозрачные» створки в ангаре KSP.
            var t = Add("Fairing", ProcMesh.Fairing((float)s.Radius, (float)s.Length, 32, 1), new Color(0.93f, 0.93f, 0.9f), sel);
            t.GetComponent<MeshRenderer>().sharedMaterial = fairingMat;
            t.localPosition = Vector3.up * baseY;
            fairing = t;
        }

        Transform Add(string name, Mesh mesh, Color col, bool sel, Transform parent = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent : root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Material;
            mpb.Clear();
            mpb.SetColor("_BaseColor", sel ? Color.Lerp(col, SelColor, 0.6f) : col);
            r.SetPropertyBlock(mpb);
            tints.Add(new Tint { R = r, Col = col, Sel = sel, Sec = curSec });
            return go.transform;
        }

        /// <summary>Деталь предпросмотра: исходный цвет и секция проекта — по ним красится ступень под мышью в сводке.</summary>
        struct Tint
        {
            public MeshRenderer R;
            public Color Col;
            public bool Sel;
            public int Sec;
        }
        readonly List<Tint> tints = new List<Tint>();
        int curSec = -1;

        /// <summary>
        /// Перекрасить предпросмотр под подсвеченную ступень: её секции — StageColor, остальное — как было. Только
        /// MaterialPropertyBlock, без перестройки мешей: наведение мыши меняет строку каждый кадр.
        /// </summary>
        void ApplyTint()
        {
            int k = pinnedStage >= 0 ? pinnedStage : hoverStage;
            if (k == shownStage) return;
            shownStage = k;
            var secs = k >= 0 && build != null && k < build.Stats.Count ? build.Stats[k].Sections : null;
            foreach (var t in tints)
            {
                if (t.R == null) continue;
                var c = t.Sel ? Color.Lerp(t.Col, SelColor, 0.6f) : t.Col;
                if (secs != null && secs.Contains(t.Sec)) c = Color.Lerp(c, StageColor, 0.7f);
                mpb.Clear();
                mpb.SetColor("_BaseColor", c);
                t.R.SetPropertyBlock(mpb);
            }
        }

        // ---------------------------------------------------------------- правка

        void AddPart(string id)
        {
            if (selRadial >= 0) craft.Radials[selRadial].Parts.Add(id);
            else
            {
                int at = selStack >= 0 ? selStack + 1 : craft.Stack.Count;
                craft.Insert(at, id);
                selStack = at;
            }
            dirty = true;
        }

        void Load(Craft c, string what)
        {
            craft = current = c;
            selStack = selRadial = -1;
            Recompile(frame: true);
            Say(what);
        }

        void Say(string text)
        {
            status = text;
            statusUntil = Time.time + 4;
        }

        static string FileName(string name)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return string.IsNullOrWhiteSpace(name) ? "ракета" : name.Trim();
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var path = Path.Combine(Dir, FileName(craft.Name) + ".json");
                File.WriteAllText(path, craft.ToJson());
                Say($"Сохранено: {path}");
            }
            catch (Exception e) { Say($"Не сохранилось: {e.Message}"); }
        }

        void Launch()
        {
            if (build == null || !build.Ok) return;
            try { Directory.CreateDirectory(Dir); File.WriteAllText(LastPath, craft.ToJson()); }
            catch (Exception e) { Debug.LogWarning($"[Hangar] _last.json не записан: {e.Message}"); }
            GameBootstrap.NextDesign = build.Design;
            GameBootstrap.NextMissionId = MissionCatalog.All[missionIdx].Id;
            SceneManager.LoadScene(FlightScene);
        }

        // ---------------------------------------------------------------- IMGUI

        void OnGUI()
        {
            Styles();
            scale = Screen.height / RefHeight;
            vw = Screen.width / scale;
            vh = RefHeight;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            if (build == null) return;
            var ev = Event.current;
            // Клик мимо выпадающего списка закрывает его и дальше не идёт: не нажмёт кнопку под ним случайно.
            if (popup != Popup.None && ev.type == EventType.MouseDown && !popupRect.Contains(ev.mousePosition))
            {
                popup = Popup.None;
                ev.Use();
            }
            // Esc — сначала закрыть список/окно файлов (деталь в руке Esc отменяет в DragUpdate).
            if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape && (popup != Popup.None || showFiles))
            {
                popup = Popup.None;
                showFiles = false;
                ev.Use();
            }

            TopBar();
            Catalog(new Rect(Pad, TopH, LeftW - Pad, vh - TopH - Pad));
            float rx = vw - RightW, colH = vh - TopH - Pad;
            float stageH = stagingOpen ? colH * 0.44f : StagingClosedH;
            Assembly(new Rect(rx, TopH, RightW - Pad, colH - stageH - Pad));
            Staging(new Rect(rx, vh - Pad - stageH, RightW - Pad, stageH));
            Stats(new Rect(LeftW + Pad, vh - StatsH, vw - LeftW - RightW - 2 * Pad, StatsH - Pad));
            if (!showFiles)
            {
                Centers();
                NodeMarks();
                HelpLine();
                ViewBar();
            }
            Toast();
            if (showFiles) Files(new Rect(vw * 0.5f - 280, 120, 560, 560));
            if (popup != Popup.None) popupRect = GUI.Window(77, popupRect, PopupWindow, GUIContent.none, box);
            if (!string.IsNullOrEmpty(GUI.tooltip))
            {
                var tip = new Rect(LeftW + 2 * Pad, TopH + ToolH + Pad, 420, 64);
                Panel(tip);
                GUI.Label(new Rect(tip.x + 10, tip.y + 6, tip.width - 20, tip.height - 12), GUI.tooltip, small);
            }
        }

        void TopBar()
        {
            Panel(new Rect(0, 0, vw, TopH - 6));
            float x = Pad, y = 8, h = TopH - 22;
            GUI.Label(new Rect(x, y, 120, h), "Конструктор", head); x += 130;
            // Enter/Esc в поле имени — снять фокус, иначе горячие клавиши (V, F, Del…) молчат: они ждут keyboardControl == 0.
            var ev = Event.current;
            if (ev.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == "craftName"
                && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter || ev.keyCode == KeyCode.Escape))
            {
                GUIUtility.keyboardControl = 0;
                ev.Use();
            }
            GUI.SetNextControlName("craftName");
            craft.Name = GUI.TextField(new Rect(x, y, 240, h), craft.Name ?? "", btn); x += 248;
            if (GUI.Button(new Rect(x, y, 80, h), new GUIContent("Новая", "Пустая сборка (Ctrl+Z — вернуть прежнюю)"), btn))
                Load(new Craft(), "Новая ракета: начните с командного модуля или двигателя");
            x += 86;
            if (GUI.Button(new Rect(x, y, 120, h), "Пресеты ▾", btn))
            {
                if (presetNames == null)
                {
                    var names = new List<string>();
                    foreach (var p in CraftPresets.All()) names.Add(p.Name);
                    presetNames = names.ToArray();
                }
                OpenPopup(Popup.Presets, new Rect(x, TopH - 4, 260, presetNames.Length * PopupRow + 12));
            }
            x += 126;
            if (GUI.Button(new Rect(x, y, 110, h), "Сохранить", btn)) Save();
            x += 116;
            if (GUI.Button(new Rect(x, y, 110, h), "Открыть…", btn))
            {
                showFiles = !showFiles;
                confirmDelete = -1;
                RefreshFiles();
            }

            // Справа: миссия (цель, дата, стол) и запуск. Название — кнопка со списком всех миссий.
            var all = MissionCatalog.All;
            float rx = vw - 660;
            if (GUI.Button(new Rect(rx, y, 32, h), "◄", btn)) missionIdx = (missionIdx + all.Count - 1) % all.Count;
            if (GUI.Button(new Rect(rx + 36, y, 284, h), new GUIContent(all[missionIdx].Title + " ▾", "Миссия: цель, дата и стартовый стол"), btnSmall))
                OpenPopup(Popup.Missions, new Rect(rx + 36, TopH - 4, 380, Mathf.Min(all.Count * PopupRow + 12, PopupMaxH)));
            if (GUI.Button(new Rect(rx + 324, y, 32, h), "►", btn)) missionIdx = (missionIdx + 1) % all.Count;
            if (GUI.Button(new Rect(rx + 364, y, 130, h), new GUIContent("Исторический", "Лететь на штатной ракете миссии"), btnSmall))
            {
                GameBootstrap.NextDesign = null;
                GameBootstrap.NextMissionId = all[missionIdx].Id;
                SceneManager.LoadScene(FlightScene);
            }
            GUI.enabled = build.Ok;
            GUI.color = build.Ok ? new Color(0.5f, 1f, 0.6f) : Color.white;
            if (GUI.Button(new Rect(rx + 502, y, 150, h), build.Ok ? "ЗАПУСК ▶" : "Есть ошибки", btn)) Launch();
            GUI.color = Color.white;
            GUI.enabled = true;
        }

        void OpenPopup(Popup which, Rect r)
        {
            popup = popup == which ? Popup.None : which;
            popupRect = r;
            popupScroll = which == Popup.Missions ? new Vector2(0, Mathf.Max(0, missionIdx * PopupRow - PopupRow * 4)) : Vector2.zero;
        }

        void PopupWindow(int id)
        {
            float w = popupRect.width - 12;
            if (popup == Popup.Presets)
            {
                for (int i = 0; i < presetNames.Length; i++)
                {
                    if (!GUI.Button(new Rect(6, 6 + i * PopupRow, w, PopupRow - 2), presetNames[i], rowBtn)) continue;
                    popup = Popup.None;
                    int k = 0;
                    foreach (var p in CraftPresets.All())
                        if (k++ == i) { Load(p, $"Пресет «{p.Name}» (Ctrl+Z — вернуть прежнюю)"); break; }
                }
                return;
            }
            var all = MissionCatalog.All;
            var view = new Rect(6, 6, w, popupRect.height - 12);
            popupScroll = GUI.BeginScrollView(view, popupScroll, new Rect(0, 0, w - 20, all.Count * PopupRow));
            for (int i = 0; i < all.Count; i++)
            {
                GUI.color = i == missionIdx ? SelColor : Color.white;
                if (GUI.Button(new Rect(0, i * PopupRow, w - 20, PopupRow - 2), all[i].Title, rowBtn)) { missionIdx = i; popup = Popup.None; }
            }
            GUI.color = Color.white;
            GUI.EndScrollView();
        }

        /// <summary>Сообщение над строкой подсказки: в верхней панели его обрезало место между кнопками.</summary>
        void Toast()
        {
            if (string.IsNullOrEmpty(status)) return;
            float l = LeftW + 2 * Pad, w = vw - RightW - Pad - l;
            float tw = Mathf.Min(w, toastStyle.CalcSize(new GUIContent(status)).x + 32);
            var r = new Rect(l + (w - tw) * 0.5f, vh - StatsH - Pad - 70, tw, 32);
            // Последние полсекунды — растворение, а не обрыв.
            float a = Mathf.Clamp01((statusUntil - Time.time) * 2);
            GUI.color = new Color(0.1f, 0.18f, 0.3f, 0.92f * a);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = new Color(1, 1, 1, a);
            GUI.Label(r, status, toastStyle);
            GUI.color = Color.white;
        }

        void Catalog(Rect r)
        {
            Panel(r);
            float x = r.x + 8, y = r.y + 8, w = r.width - 16;
            float cw = (w - 3 * 4) / 4;
            for (int i = 0; i < CategoryNames.Length; i++)
            {
                var br = new Rect(x + (i % 4) * (cw + 4), y + (i / 4) * (Row + 4), cw, Row);
                GUI.color = (int)category == i ? SelColor : Color.white;
                if (GUI.Button(br, CategoryNames[i], tab)) category = (PartCategory)i;
                GUI.color = Color.white;
            }
            y += 2 * (Row + 4) + 4;
            // Поиск — по всем категориям сразу: имя, id и описание («стык», «РСУ», «парашют»).
            var ev0 = Event.current;
            if (ev0.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == "partSearch"
                && (ev0.keyCode == KeyCode.Escape || ev0.keyCode == KeyCode.Return || ev0.keyCode == KeyCode.KeypadEnter))
            {
                if (ev0.keyCode == KeyCode.Escape) search = "";
                GUIUtility.keyboardControl = 0;
                ev0.Use();
            }
            var sr = new Rect(x, y, w - (search.Length > 0 ? 34 : 0), Row - 2);
            GUI.SetNextControlName("partSearch");
            search = GUI.TextField(sr, search ?? "", btnSmall);
            if (search.Length == 0 && GUI.GetNameOfFocusedControl() != "partSearch")
                GUI.Label(new Rect(sr.x + 8, sr.y, sr.width - 8, sr.height), "Поиск по всем деталям…", hintStyle);
            if (search.Length > 0 && GUI.Button(new Rect(x + w - 30, y, 30, Row - 2), "✕", btnSmall)) { search = ""; GUIUtility.keyboardControl = 0; }
            y += Row + 2;
            string into = selRadial >= 0 ? $"в боковую группу {selRadial + 1}" : selStack >= 0 ? "над выбранной деталью" : "наверх стека";
            GUI.Label(new Rect(x, y, w, 22), "Клик — добавить " + into + " · или тащите в сборку", small);
            y += 26;
            var list = CatalogList();
            if (list.Count == 0) GUI.Label(new Rect(x, y, w, 24), "Ничего не нашлось", small);
            const float item = 52;
            var view = new Rect(x, y, w, r.yMax - y - 8);
            partsScroll = GUI.BeginScrollView(view, partsScroll, new Rect(0, 0, w - 20, list.Count * (item + 4)));
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                string line = $"{p.Name}\n<size=12>Ø {p.Diameter:0.##} м · {p.Length:0.##} м · {p.Mass / 1000:0.###} т{EngineText(p)}</size>";
                var icon = PartIconSet.Get(p.Id);
                var row = new Rect(0, i * (item + 4), w - 20, item);
                // Нажатие запоминаем до кнопки: её MouseDown съедается, а перенос в предпросмотр ловит BuildInput.
                var ev = Event.current;
                if (ev.type == EventType.MouseDown && ev.button == 0 && row.Contains(ev.mousePosition)) { catalogPress = p.Id; catalogDragged = false; }
                if (GUI.Button(row, new GUIContent(line, p.Description), icon != null ? rowIconBtn : rowBtn) && !catalogDragged) AddPart(p.Id);
                if (icon != null) GUI.DrawTexture(new Rect(row.x + IconPad, row.y + (item - CatalogIcon) * 0.5f, CatalogIcon, CatalogIcon), icon, ScaleMode.ScaleToFit);
            }
            GUI.EndScrollView();
        }

        List<PartDef> CatalogList()
        {
            string q = (search ?? "").Trim().ToLowerInvariant();
            if (catListCat == category && catListQuery == q && catListVersion == catalogVersion) return catList;
            catListCat = category;
            catListQuery = q;
            catListVersion = catalogVersion;
            catList.Clear();
            foreach (var p in PartCatalog.All)
            {
                if (!Available(p)) continue;
                if (q.Length == 0 ? p.Category != category
                    : !((p.Name ?? "").ToLowerInvariant().Contains(q) || p.Id.ToLowerInvariant().Contains(q)
                        || (p.Description ?? "").ToLowerInvariant().Contains(q))) continue;
                catList.Add(p);
            }
            partsScroll = Vector2.zero;
            return catList;
        }

        static bool Available(PartDef p)
        {
            try { return PartAvailable == null || PartAvailable(p); }
            catch (Exception e) { Debug.LogWarning($"[Hangar] PartAvailable({p.Id}): {e.Message}"); return true; }
        }

        /// <summary>Детали, закрытые фильтром карьеры (пресет, файл, старая сборка), — ошибка сборки: запуск недоступен.</summary>
        void CheckAvailable()
        {
            var seen = new HashSet<string>();
            void One(string id)
            {
                var p = PartCatalog.Get(id);
                if (p != null && seen.Add(id) && !Available(p)) build.Errors.Add($"«{p.Name}» ещё не открыта");
            }
            foreach (var cp in craft.Stack) One(cp.Id);
            foreach (var r in craft.Radials) foreach (var id in r.Parts) One(id);
        }

        static string EngineText(PartDef p)
        {
            if (!p.HasEngine) return p.Propellant > 0 ? $" · топливо {p.Propellant / 1000:0.#} т" : "";
            var e = p.Engine();
            return $" · {e.ThrustVac * p.EngineCount / 1000:0} кН · Isp {e.IspVac:0} с";
        }

        void Assembly(Rect r)
        {
            Panel(r);
            float x = r.x + 8, y = r.y + 8, w = r.width - 16;
            GUI.Label(new Rect(x, y, w, 24), $"Сборка (снизу вверх) — {craft.Stack.Count} дет.", label);
            y += 28;
            float bw = (w - 8) / 3;
            GUI.enabled = selStack >= 0 && selRadial < 0;
            if (GUI.Button(new Rect(x, y, bw, Row), new GUIContent("+ Боковые ×4", "Группа боковых блоков у выбранной детали ядра"), btnSmall))
            {
                craft.Radials.Add(new CraftRadial { Parent = selStack, Symmetry = 4 });
                selRadial = craft.Radials.Count - 1;
                dirty = true;
            }
            GUI.enabled = selStack >= 0 || selRadial >= 0;
            if (GUI.Button(new Rect(x + bw + 4, y, bw, Row), "Снять выбор", btnSmall)) { selStack = selRadial = -1; dirty = true; }
            GUI.enabled = true;
            GUI.enabled = craft.Stack.Count > 0;
            if (GUI.Button(new Rect(x + 2 * (bw + 4), y, bw, Row), "Очистить", btnSmall)) Load(new Craft { Name = craft.Name }, "Стек очищен");
            GUI.enabled = true;
            y += Row + 6;

            // Под списком — либо редактор боковой группы, либо размеры выбранной детали ядра (§5.4).
            var selPart = selStack >= 0 && selRadial < 0 && selStack < craft.Stack.Count ? PartCatalog.Resolve(craft.Stack[selStack]) : null;
            int paramRows = selPart != null ? ParamCount(selPart.Params) : 0;
            float editorH = selRadial >= 0 ? 150 : paramRows > 0 ? 28 + (paramRows + 1) * (Row + 2) : 0;
            var view = new Rect(x, y, w, r.yMax - y - 8 - editorH);
            int rows = craft.Stack.Count + craft.Radials.Count;
            stackScroll = GUI.BeginScrollView(view, stackScroll, new Rect(0, 0, w - 20, rows * (Row + 2)));
            float ry = 0, iw = w - 20;
            for (int i = craft.Stack.Count - 1; i >= 0; i--)
            {
                var cp = craft.Stack[i];
                var p = PartCatalog.Resolve(cp);
                GUI.color = selStack == i && selRadial < 0 ? SelColor : Color.white;
                string st = p != null && p.Stageable && cp.Stage >= 0 ? $"[{cp.Stage + 1}] " : "";
                var icon = PartIconSet.Get(cp.Id);
                if (GUI.Button(new Rect(0, ry, iw - 96, Row), st + (p?.Name ?? cp.Id), icon != null ? stackIconBtn : rowBtn)) { selStack = i; selRadial = -1; dirty = true; }
                GUI.color = Color.white;
                if (icon != null) GUI.DrawTexture(new Rect(IconPad, ry + (Row - StackIcon) * 0.5f, StackIcon, StackIcon), icon, ScaleMode.ScaleToFit);
                if (GUI.Button(new Rect(iw - 94, ry, 30, Row), "▲", btnSmall) && i + 1 < craft.Stack.Count) { craft.Swap(i, i + 1); selStack = i + 1; dirty = true; }
                if (GUI.Button(new Rect(iw - 62, ry, 30, Row), "▼", btnSmall) && i > 0) { craft.Swap(i, i - 1); selStack = i - 1; dirty = true; }
                if (GUI.Button(new Rect(iw - 30, ry, 30, Row), "✕", btnSmall)) { craft.RemoveAt(i); selRadial = -1; selStack = Mathf.Min(i, craft.Stack.Count - 1); dirty = true; }
                ry += Row + 2;
                for (int g = 0; g < craft.Radials.Count; g++)
                {
                    var rad = craft.Radials[g];
                    if (rad.Parent != i) continue;
                    GUI.color = selRadial == g ? SelColor : new Color(0.75f, 0.85f, 1f);
                    string names = rad.Parts.Count == 0 ? "пусто — выберите детали слева" : string.Join(", ", rad.Parts.ConvertAll(id => PartCatalog.Get(id)?.Name ?? id));
                    if (GUI.Button(new Rect(24, ry, iw - 56, Row), $"↳ ×{rad.Symmetry}: {names}", rowBtn)) { selRadial = g; selStack = i; dirty = true; }
                    GUI.color = Color.white;
                    if (GUI.Button(new Rect(iw - 30, ry, 30, Row), "✕", btnSmall)) { craft.Radials.RemoveAt(g); selRadial = -1; dirty = true; }
                    ry += Row + 2;
                }
            }
            GUI.EndScrollView();
            if (selRadial >= 0) RadialEditor(new Rect(x, r.yMax - editorH - 4, w, editorH));
            else if (paramRows > 0) ParamEditor(new Rect(x, r.yMax - editorH - 4, w, editorH), craft.Stack[selStack], selPart);
        }

        static int ParamCount(PartParam f)
        {
            int n = 0;
            foreach (var q in PartCatalog.ParamOrder) if ((f & q) != 0) n++;
            return n;
        }

        /// <summary>
        /// Размеры детали (§5.4): длина/диаметр баков, переходников, конусов; у крыльев — размах, хорда, стреловидность,
        /// установка и V. Значения хранит CraftPart (в JSON — только заданные), масса, топливо и площади пересчитываются
        /// в PartCatalog.Resolve, а dirty перекомпилирует проект — Δv, TWR и аэродинамика обновляются сразу.
        /// Кнопки: шаг PartCatalog.Range(p).Step и десять шагов; пределы — там же.
        /// </summary>
        void ParamEditor(Rect r, CraftPart cp, PartDef p)
        {
            float x = r.x, y = r.y, w = r.width;
            GUI.Label(new Rect(x, y, w, 24), $"Размеры: {p.Name}", label);
            y += 28;
            const float bw = 40, vw = 80;
            float lw = w - 4 * (bw + 4) - vw - 4;
            foreach (var q in PartCatalog.ParamOrder)
            {
                if ((p.Params & q) == 0) continue;
                var range = PartCatalog.Range(q);
                double v = PartCatalog.Value(p, q);
                GUI.Label(new Rect(x, y, lw, Row), PartCatalog.ParamName(q), small);
                float bx = x + lw;
                double step = range.Step;
                if (GUI.Button(new Rect(bx, y, bw, Row), new GUIContent("−−", $"−{step * 10:0.##}"), btnSmall)) SetParam(cp, q, v - 10 * step);
                bx += bw + 4;
                if (GUI.Button(new Rect(bx, y, bw, Row), new GUIContent("−", $"−{step:0.##}"), btnSmall)) SetParam(cp, q, v - step);
                bx += bw + 4;
                GUI.Label(new Rect(bx, y, vw, Row), v.ToString(step < 1 ? "0.00" : "0.0"), label);
                bx += vw + 4;
                if (GUI.Button(new Rect(bx, y, bw, Row), new GUIContent("+", $"+{step:0.##}"), btnSmall)) SetParam(cp, q, v + step);
                bx += bw + 4;
                if (GUI.Button(new Rect(bx, y, bw, Row), new GUIContent("++", $"+{step * 10:0.##}"), btnSmall)) SetParam(cp, q, v + 10 * step);
                y += Row + 2;
            }
            GUI.enabled = cp.HasParams;
            if (GUI.Button(new Rect(x, y, 160, Row), new GUIContent("Как в каталоге", "Сбросить размеры детали"), btnSmall))
            {
                foreach (var q in PartCatalog.ParamOrder) cp.Set(q, double.NaN);
                dirty = true;
            }
            GUI.enabled = true;
        }

        void SetParam(CraftPart cp, PartParam q, double v)
        {
            var range = PartCatalog.Range(q);
            // Округление к сетке шага: 0,1 + 0,2 в double даёт 0,30000000000000004 и плодит записи в кеше Resolve.
            v = Math.Round(v / range.Step) * range.Step;
            cp.Set(q, range.Clamp(v));
            dirty = true;
        }

        void RadialEditor(Rect r)
        {
            var rad = craft.Radials[selRadial];
            float x = r.x, y = r.y;
            GUI.Label(new Rect(x, y, 120, Row), "Симметрия", small);
            for (int k = 0; k < Craft.Symmetries.Length; k++)
            {
                int n = Craft.Symmetries[k];
                GUI.color = rad.Symmetry == n ? SelColor : Color.white;
                if (GUI.Button(new Rect(x + 110 + k * 46, y, 42, Row), "×" + n, btnSmall)) { rad.Symmetry = n; dirty = true; }
                GUI.color = Color.white;
            }
            y += Row + 4;
            GUI.Label(new Rect(x, y, 200, Row), $"Подъём над деталью: {rad.Lift:0.0} м", small);
            if (GUI.Button(new Rect(x + 210, y, 50, Row), "−1", btnSmall)) { rad.Lift = Math.Max(0, rad.Lift - 1); dirty = true; }
            if (GUI.Button(new Rect(x + 264, y, 50, Row), "+1", btnSmall)) { rad.Lift += 1; dirty = true; }
            y += Row + 4;
            // Детали блока сверху вниз; ✕ убирает одну.
            float bx = x;
            for (int j = rad.Parts.Count - 1; j >= 0; j--)
            {
                var name = PartCatalog.Get(rad.Parts[j])?.Name ?? rad.Parts[j];
                float bw = Mathf.Min(140, r.width - (bx - x));
                if (bw < 60) break;
                if (GUI.Button(new Rect(bx, y, bw, Row), new GUIContent("✕ " + name, "Убрать деталь из бокового блока"), btnSmall))
                {
                    rad.Parts.RemoveAt(j);
                    dirty = true;
                    break;
                }
                bx += bw + 4;
            }
            y += Row + 4;
            if (GUI.Button(new Rect(x, y, 160, Row), "Готово", btnSmall)) { selRadial = -1; dirty = true; }
        }

        /// <summary>Строка списка ступеней: что делает и как сдвинуть по номеру пробела.</summary>
        struct StageItem
        {
            public string Text;
            public int Stage;
            public Action<int> Set;
        }

        void Staging(Rect r)
        {
            Panel(r);
            float x = r.x + 8, y = r.y + 8, w = r.width - 16;
            if (GUI.Button(new Rect(x, y, w - 110, 24), (stagingOpen ? "▾ " : "▸ ") + "Ступени (по пробелу)", label)) stagingOpen = !stagingOpen;
            if (GUI.Button(new Rect(x + w - 100, y, 100, 26), new GUIContent("Авто", "Расставить ступени заново"), btnSmall))
            {
                CraftCompiler.AutoStage(craft);
                dirty = true;
            }
            if (!stagingOpen) return;
            y += 32;
            var items = new List<StageItem>();
            for (int i = 0; i < craft.Stack.Count; i++)
            {
                var cp = craft.Stack[i];
                var p = PartCatalog.Resolve(cp);
                if (p == null || !p.Stageable) continue;
                string verb = p.HasEngine ? "Запуск" : p.Decoupler ? "Отделение" : p.Fairing ? "Сброс" : "Парашют";
                items.Add(new StageItem { Text = $"{verb}: {p.Name}", Stage = cp.Stage, Set = s => cp.Stage = s });
            }
            for (int g = 0; g < craft.Radials.Count; g++)
            {
                var rad = craft.Radials[g];
                bool engine = rad.Parts.Exists(id => PartCatalog.Get(id)?.HasEngine == true);
                if (engine) items.Add(new StageItem { Text = $"Запуск: боковые ×{rad.Symmetry}", Stage = rad.EngineStage, Set = s => rad.EngineStage = s });
                items.Add(new StageItem { Text = $"Отделение: боковые ×{rad.Symmetry}", Stage = rad.SepStage, Set = s => rad.SepStage = s });
            }
            int max = -1;
            foreach (var it in items) max = Mathf.Max(max, it.Stage);
            int lines = 0;
            for (int s = 0; s <= max; s++) { lines++; foreach (var it in items) if (it.Stage == s) lines++; }
            var view = new Rect(x, y, w, r.yMax - y - 8);
            stageScroll = GUI.BeginScrollView(view, stageScroll, new Rect(0, 0, w - 20, lines * (Row + 2)));
            float ry = 0, iw = w - 20;
            for (int s = 0; s <= max; s++)
            {
                GUI.color = new Color(0.6f, 0.75f, 1f);
                GUI.Label(new Rect(0, ry, iw, Row), $"Пробел {s + 1}", label);
                GUI.color = Color.white;
                ry += Row + 2;
                foreach (var it in items)
                {
                    if (it.Stage != s) continue;
                    GUI.Label(new Rect(14, ry, iw - 84, Row), it.Text, small);
                    // ◄ — раньше, ► — позже; номер за последним создаёт новую ступень, Normalize сожмёт пропуски.
                    if (GUI.Button(new Rect(iw - 66, ry, 32, Row - 2), "◄", btnSmall) && s > 0) { it.Set(s - 1); dirty = true; }
                    if (GUI.Button(new Rect(iw - 32, ry, 32, Row - 2), "►", btnSmall)) { it.Set(s + 1); dirty = true; }
                    ry += Row + 2;
                }
            }
            GUI.EndScrollView();
        }

        /// <summary>Шкала Δv: высота полосы и строки с участками бюджета под ней. Пара: StatsOpenH — в него входят обе.</summary>
        const float BarH = 18, BarLegsH = 18;
        /// <summary>Колонки таблицы ступеней — доли ширины левой колонки сводки (как Kerbal Engineer: Δv, TWR, перегрузка, время, масса).</summary>
        static readonly float[] StageCols = { 0, 0.33f, 0.44f, 0.55f, 0.70f, 0.79f, 0.89f };

        void Stats(Rect r)
        {
            Panel(r);
            float x = r.x + 10, y = r.y + 8, w = r.width - 20;
            double dv = CraftAnalysis.Available(build.Stats);
            string twr = build.Stats.Count > 0 ? $"{build.Stats[0].TwrSL:0.00}" : "—";
            if (GUI.Button(new Rect(r.xMax - 36, y, 28, 24), new GUIContent(statsOpen ? "▾" : "▴", statsOpen ? "Свернуть сводку" : "Развернуть сводку"), btnSmall))
                statsOpen = !statsOpen;
            string goal = budget != null ? $" из ≈{budget.Total:0}" : "";
            string sum = $"Масса {build.Mass / 1000:0.0} т · высота {build.Height:0.0} м · Δv {dv:0}{goal} м/с · TWR старта {twr}";
            var ev = Event.current;
            if (!statsOpen)
            {
                if (ev.type == EventType.Repaint) { hoverStage = -1; ApplyTint(); }
                // Свёрнуто: сводка + счётчик проверок; первая ошибка — красным, чтобы не искать, почему запуск серый.
                float sw = label.CalcSize(new GUIContent(sum)).x;
                GUI.Label(new Rect(x, y, sw, 24), sum, label);
                float ex0 = x + sw + 16, ew0 = r.xMax - 44 - ex0;
                // В одну строку с «…»: перенос длинного предупреждения вылезал за полосу высотой StatsClosedH.
                if (build.Errors.Count > 0) GUI.Label(new Rect(ex0, y, ew0, 24), Fit(err, $"✖ {build.Errors.Count}: {build.Errors[0]}", ew0), err);
                else if (build.Warnings.Count > 0) GUI.Label(new Rect(ex0, y, ew0, 24), Fit(warn, $"⚠ {build.Warnings.Count}: {build.Warnings[0]}", ew0), warn);
                else GUI.Label(new Rect(ex0, y, ew0, 24), "✔ Проверки пройдены", label);
                return;
            }
            GUI.Label(new Rect(x, y, w - 40, 24), sum + $" · ширина {build.Width:0.0} м", label);
            y += 28;
            int hov = -1;
            DeltaVBar(new Rect(x, y, w - 40, BarH), dv, ref hov);
            y += BarH + BarLegsH + 6;

            float colW = w * 0.6f;
            int crew = 0;
            if (build.Design != null) foreach (var s in build.Design.Sections) crew += s.Crew;
            // Слева — ступени по пробелам (как VesselDesign.ComputeStats: параллельная работа боковушек — одним прожигом).
            // Наведение подсвечивает ступень в предпросмотре, клик закрепляет (ещё клик — снять).
            var view = new Rect(x, y, colW, r.yMax - y - 6);
            int rows = build.Stats.Count + 2;
            // Мышь — до BeginScrollView: внутри координаты локальные, и строки за краем прокрутки тоже «содержали» бы её.
            bool inView = view.Contains(ev.mousePosition);
            statsScroll = GUI.BeginScrollView(view, statsScroll, new Rect(0, 0, colW - 20, rows * 22));
            float cw = colW - 20;
            string[] headTxt = { "Ступень", "Δv вак", "у Земли", "TWR Зем/вак", "макс g", "время", "масса" };
            for (int c = 0; c < headTxt.Length; c++) GUI.Label(new Rect(cw * StageCols[c], 0, cw * 0.2f, 22), headTxt[c], hintStyle);
            double sumVac = 0, sumSL = 0, sumT = 0;
            for (int i = 0; i < build.Stats.Count; i++)
            {
                var s = build.Stats[i];
                var row = new Rect(0, (i + 1) * 22, cw, 22);
                if (inView && row.Contains(ev.mousePosition)) hov = i;
                bool lit = (pinnedStage >= 0 ? pinnedStage : hoverStage) == i;
                if (lit)
                {
                    GUI.color = new Color(StageColor.r, StageColor.g, StageColor.b, 0.22f);
                    GUI.DrawTexture(row, Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
                if (inView && ev.type == EventType.MouseDown && ev.button == 0 && row.Contains(ev.mousePosition))
                {
                    pinnedStage = pinnedStage == i ? -1 : i;
                    ev.Use();
                }
                sumVac += s.DeltaVVac;
                sumSL += s.DeltaVSL;
                sumT += s.BurnTime;
                double g = s.MaxAccel / Kare.Space.Core.Constants.G0;
                bool gBad = crew > 0 && s.HighGTime > FlightPhysics.CrewGTime;
                Cell(row, 0, Fit(small, $"{i + 1}. {s.Name}", cw * (StageCols[1] - 0.01f)), small);
                Cell(row, 1, $"{s.DeltaVVac:0}", small);
                Cell(row, 2, $"{s.DeltaVSL:0}", small);
                Cell(row, 3, $"{s.TwrSL:0.00} / {s.TwrVac:0.00}", i == 0 && s.TwrSL < 1 ? err : small);
                Cell(row, 4, $"{g:0.0}", gBad ? warn : small);
                Cell(row, 5, Duration(s.BurnTime), small);
                Cell(row, 6, $"{s.StartMass / 1000:0.#} т", small);
            }
            var tot = new Rect(0, (build.Stats.Count + 1) * 22, cw, 22);
            Cell(tot, 0, "Итого", label);
            Cell(tot, 1, $"{sumVac:0}", label);
            Cell(tot, 2, $"{sumSL:0}", label);
            Cell(tot, 5, Duration(sumT), label);
            GUI.EndScrollView();
            if (ev.type == EventType.Repaint)
            {
                hoverStage = hov;
                ApplyTint();
            }
            // Справа — проверки перед стартом: ошибки запрещают запуск, предупреждения — нет.
            float ex = x + colW + 10, ew = w - colW - 10, ey = y;
            if (build.Errors.Count == 0 && build.Warnings.Count == 0)
                GUI.Label(new Rect(ex, ey, ew, 22), "✔ Проверки пройдены", label);
            foreach (var e in build.Errors) { GUI.Label(new Rect(ex, ey, ew, 40), "✖ " + e, err); ey += err.CalcHeight(new GUIContent("✖ " + e), ew) + 2; }
            foreach (var e in build.Warnings) { GUI.Label(new Rect(ex, ey, ew, 40), "⚠ " + e, warn); ey += warn.CalcHeight(new GUIContent("⚠ " + e), ew) + 2; }
        }

        void Cell(Rect row, int col, string text, GUIStyle st)
        {
            float cw = row.width;
            float x0 = cw * StageCols[col], x1 = col + 1 < StageCols.Length ? cw * StageCols[col + 1] : cw;
            GUI.Label(new Rect(row.x + x0, row.y, x1 - x0, row.height), text, st);
        }

        static string Duration(double s) => s >= 120 ? $"{(int)(s / 60)} мин {s % 60:00} с" : $"{s:0} с";

        /// <summary>
        /// Шкала Δv как карта KSP: ступени — отрезками по их вакуумному Δv, сверху — засечки участков бюджета миссии
        /// (выведение, разгон, захват…) и итоговая черта: зелёная — хватает, красная — нет. Под шкалой — сами участки.
        /// </summary>
        void DeltaVBar(Rect r, double have, ref int hov)
        {
            var ev = Event.current;
            double need = budget?.Total ?? 0;
            double full = Math.Max(have, need) * 1.04;
            GUI.color = new Color(1, 1, 1, 0.08f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
            if (full <= 0) return;
            float px = r.x;
            int lit = pinnedStage >= 0 ? pinnedStage : hoverStage;
            for (int i = 0; i < build.Stats.Count; i++)
            {
                float sw = (float)(build.Stats[i].DeltaVVac / full * r.width);
                var seg = new Rect(px, r.y + 2, Mathf.Max(1, sw - 1), r.height - 4);
                GUI.color = i == lit ? StageColor : i % 2 == 0 ? new Color(0.45f, 0.62f, 0.85f, 0.9f) : new Color(0.32f, 0.45f, 0.65f, 0.9f);
                GUI.DrawTexture(seg, Texture2D.whiteTexture);
                GUI.color = Color.white;
                if (sw > 16) GUI.Label(seg, $"{i + 1}", helpStyle);
                if (seg.Contains(ev.mousePosition)) hov = i;
                if (ev.type == EventType.MouseDown && ev.button == 0 && seg.Contains(ev.mousePosition))
                {
                    pinnedStage = pinnedStage == i ? -1 : i;
                    ev.Use();
                }
                px += sw;
            }
            var legsRect = new Rect(r.x, r.yMax + 2, r.width, BarLegsH);
            if (budget == null)
            {
                GUI.Label(legsRect, "Бюджет миссии не считается: старт в воздухе у цели", hintStyle);
                return;
            }
            // Засечки по нарастающей: где кончается выведение, разгон к цели и т.д.
            double cum = 0;
            var legs = new List<string>();
            foreach (var l in budget.Legs)
            {
                cum += l.DeltaV;
                float mx = r.x + (float)(cum / full * r.width);
                GUI.color = new Color(1, 1, 1, 0.75f);
                GUI.DrawTexture(new Rect(mx - 1, r.y - 3, 2, 6), Texture2D.whiteTexture);
                GUI.color = Color.white;
                legs.Add($"{l.Name} {l.DeltaV:0}");
            }
            bool ok = have >= need;
            float nx = r.x + (float)(need / full * r.width);
            GUI.color = ok ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.4f, 0.35f);
            GUI.DrawTexture(new Rect(nx - 1.5f, r.y - 3, 3, r.height + 6), Texture2D.whiteTexture);
            GUI.color = Color.white;
            string verdict = ok ? $"запас {have - need:0} м/с" : $"не хватает {need - have:0} м/с";
            string text = $"{MissionCatalog.All[missionIdx].Title}: ≈{need:0} м/с, {verdict}" + (budget.Assists ? " (без гравманёвров)" : "") +
                          "  ·  " + string.Join(" → ", legs);
            GUI.Label(legsRect, new GUIContent(Fit(ok ? small : warn, text, legsRect.width),
                "Оценка по круговым орбитам и гомановским перелётам; потери выведения внутри. Δv ракеты — по вакууму."), ok ? small : warn);
        }

        /// <summary>Текст, обрезанный с «…» под ширину w в стиле s (CalcSize меряет без переноса).</summary>
        static string Fit(GUIStyle s, string text, float w)
        {
            if (s.CalcSize(new GUIContent(text)).x <= w) return text;
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (s.CalcSize(new GUIContent(text.Substring(0, mid) + "…")).x <= w) lo = mid; else hi = mid - 1;
            }
            return text.Substring(0, lo) + "…";
        }

        void Files(Rect r)
        {
            Panel(r, 0.97f);
            float x = r.x + 10, y = r.y + 10, w = r.width - 20;
            GUI.Label(new Rect(x, y, w - 40, 24), "Сохранённые ракеты (Esc — закрыть)", label);
            if (GUI.Button(new Rect(r.xMax - 40, y, 30, 26), "✕", btnSmall)) showFiles = false;
            y += 32;
            GUI.Label(new Rect(x, y, w, 20), Dir, small);
            y += 24;
            var view = new Rect(x, y, w, r.yMax - y - 10);
            filesScroll = GUI.BeginScrollView(view, filesScroll, new Rect(0, 0, w - 20, files.Length * (Row + 4)));
            for (int i = 0; i < files.Length; i++)
            {
                float ry = i * (Row + 4), iw = w - 20;
                if (confirmDelete == i)
                {
                    GUI.Label(new Rect(8, ry, iw - 150, Row), $"Удалить «{Path.GetFileNameWithoutExtension(files[i])}»?", err);
                    if (GUI.Button(new Rect(iw - 140, ry, 66, Row), "Да", btnSmall))
                    {
                        try { File.Delete(files[i]); Say($"Удалено: {Path.GetFileName(files[i])}"); }
                        catch (Exception e) { Say($"Не удалилось: {e.Message}"); }
                        confirmDelete = -1;
                        RefreshFiles();
                        break;
                    }
                    if (GUI.Button(new Rect(iw - 70, ry, 70, Row), "Нет", btnSmall)) confirmDelete = -1;
                    continue;
                }
                if (GUI.Button(new Rect(iw - 34, ry, 34, Row), new GUIContent("✕", "Удалить файл"), btnSmall)) { confirmDelete = i; continue; }
                if (!GUI.Button(new Rect(0, ry, iw - 40, Row), Path.GetFileNameWithoutExtension(files[i]), rowBtn)) continue;
                try
                {
                    Load(Craft.FromJson(File.ReadAllText(files[i])), $"Открыта «{Path.GetFileName(files[i])}»");
                    showFiles = false;
                }
                catch (Exception e) { Say($"Не читается: {e.Message}"); }
            }
            if (files.Length == 0) GUI.Label(new Rect(0, 0, w, 24), "Пока ничего не сохранено", small);
            GUI.EndScrollView();
        }

        /// <summary>Сохранения без автосохранения: _last.json — это текущая сборка, открывать её незачем.</summary>
        void RefreshFiles()
        {
            var list = new List<string>();
            if (Directory.Exists(Dir))
                foreach (var f in Directory.GetFiles(Dir, "*.json"))
                    if (!string.Equals(f, LastPath, StringComparison.OrdinalIgnoreCase)) list.Add(f);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            files = list.ToArray();
        }

        void Panel(Rect r, float alpha = 0.9f)
        {
            GUI.color = new Color(0.06f, 0.08f, 0.12f, alpha);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        void Styles()
        {
            if (head != null) return;
            head = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            label = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
            label.normal.textColor = new Color(0.75f, 0.85f, 1f);
            small = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, alignment = TextAnchor.MiddleLeft };
            small.normal.textColor = Color.white;
            btn = new GUIStyle(GUI.skin.button) { fontSize = 15 };
            btnSmall = new GUIStyle(GUI.skin.button) { fontSize = 13 };
            tab = new GUIStyle(btnSmall) { fontSize = 12, padding = new RectOffset(2, 2, 2, 2) };
            rowBtn = new GUIStyle(GUI.skin.button) { fontSize = 14, alignment = TextAnchor.MiddleLeft, richText = true, wordWrap = false };
            rowBtn.padding.left = 10;
            // Строки с иконкой: текст сдвинут за иконку (IconPad + размер + зазор).
            rowIconBtn = new GUIStyle(rowBtn);
            rowIconBtn.padding.left = (int)(IconPad + CatalogIcon + 8);
            stackIconBtn = new GUIStyle(rowBtn);
            stackIconBtn.padding.left = (int)(IconPad + StackIcon + 6);
            err = new GUIStyle(small);
            err.normal.textColor = new Color(1f, 0.45f, 0.4f);
            warn = new GUIStyle(small);
            warn.normal.textColor = new Color(1f, 0.85f, 0.4f);
            helpStyle = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter };
            markStyle = new GUIStyle(small) { fontStyle = FontStyle.Bold };
            toastStyle = new GUIStyle(small) { fontSize = 15, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            hintStyle = new GUIStyle(small);
            hintStyle.normal.textColor = new Color(1, 1, 1, 0.4f);
            // Подложка выпадающего списка: непрозрачная, иначе строки читаются поверх деталей.
            var bg = new Texture2D(1, 1);
            bg.SetPixel(0, 0, new Color(0.08f, 0.1f, 0.15f, 0.98f));
            bg.Apply();
            box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(0, 0, 0, 0) };
            box.normal.background = bg;
            box.onNormal.background = bg;
        }
    }
}
