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
    public sealed class HangarController : MonoBehaviour
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
        const float LeftW = 360, RightW = 440, TopH = 52, StatsH = 230, Pad = 8, Row = 30;

        Craft craft;
        CraftBuild build;
        bool dirty = true;
        int selStack = -1, selRadial = -1;
        PartCategory category = PartCategory.Tank;
        Vector2 partsScroll, stackScroll, stageScroll, filesScroll, statsScroll;
        int missionIdx;
        bool showFiles;
        string[] files = Array.Empty<string>();
        string status;
        float statusUntil;
        float scale = 1, vw = 1920, vh = 1080;

        Transform root, fairing;
        Material fairingMat;
        MaterialPropertyBlock mpb;
        float yaw = 35, pitch = 12, dist = 70;
        Vector3 target = new Vector3(0, 20, 0);
        Vector3 lastMouse;

        GUIStyle head, label, small, btn, btnSmall, tab, rowBtn, err, warn, box;

        static readonly string[] CategoryNames = { "Командные", "Баки", "Двигатели", "Разделители", "Аэро", "Разное", "Нагрузка" };
        static readonly Color SelColor = new Color(1f, 0.55f, 0.15f);

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
            build = CraftCompiler.Compile(craft);
            if (selStack >= craft.Stack.Count) selStack = craft.Stack.Count - 1;
            if (selRadial >= craft.Radials.Count) selRadial = -1;
            RebuildPreview();
            if (frame)
            {
                float h = Mathf.Max(5, (float)(build.Height > 0 ? build.Height : 20));
                target = new Vector3(0, h * 0.5f, 0);
                dist = Mathf.Max(h, (float)build.Width) * 1.35f + 8;
            }
            dirty = false;
        }

        void Update()
        {
            if (dirty) Recompile();
            OrbitCamera();
            if (statusUntil > 0 && Time.time > statusUntil) status = null;
        }

        // ---------------------------------------------------------------- камера

        /// <summary>Зона предпросмотра в экранных пикселях (между панелями): мышь здесь крутит камеру, а не IMGUI.</summary>
        bool InPreview(Vector3 m)
        {
            float x = m.x / scale, y = (Screen.height - m.y) / scale;
            return x > LeftW + Pad && x < vw - RightW - Pad && y > TopH && y < vh - StatsH - Pad && !showFiles;
        }

        void OrbitCamera()
        {
            if (Camera == null) return;
            var m = Input.mousePosition;
            if (InPreview(m))
            {
                // ЛКМ/ПКМ — вращение, СКМ — сдвиг по высоте, колесо — приближение (как в ангаре KSP).
                if (Input.GetMouseButton(1) || Input.GetMouseButton(0) && !Input.GetMouseButtonDown(0))
                {
                    var d = m - lastMouse;
                    yaw += d.x * 0.3f;
                    pitch = Mathf.Clamp(pitch - d.y * 0.3f, -10, 80);
                }
                if (Input.GetMouseButton(2)) target.y = Mathf.Max(0, target.y - (m.y - lastMouse.y) * dist * 0.002f);
                float wheel = Input.mouseScrollDelta.y;
                if (wheel != 0) dist = Mathf.Clamp(dist * Mathf.Pow(0.88f, wheel), 3, 600);
            }
            lastMouse = m;
            var rot = Quaternion.Euler(pitch, yaw, 0);
            Camera.transform.SetPositionAndRotation(target - rot * Vector3.forward * dist, rot);
            // Створка (половина +X) всегда на дальней от камеры стороне: Euler(0, θ) несёт +X в (cos θ, 0, −sin θ),
            // а взгляд камеры по горизонтали — (sin yaw, 0, cos yaw) → θ = yaw − 90°.
            if (fairing != null) fairing.localRotation = Quaternion.Euler(0, yaw - 90, 0);
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

        void RebuildPreview()
        {
            foreach (Transform ch in root) Destroy(ch.gameObject);
            // Высоты деталей — из скомпилированного проекта (Vessel.Layout), как их поставит полёт; без проекта — подряд.
            double[] layout = null;
            var d = build.Design;
            if (d != null && build.PartSection != null)
            {
                layout = new double[d.Sections.Count];
                new Vessel(d).Layout(layout);
            }
            double y = 0;
            for (int i = 0; i < craft.Stack.Count; i++)
            {
                var p = PartCatalog.Get(craft.Stack[i].Id);
                if (p == null) continue;
                double baseY = layout != null && !p.Fairing ? layout[build.PartSection[i]] + build.PartOffset[i] : y;
                bool sel = selStack == i && selRadial < 0;
                if (p.Fairing && d != null)
                {
                    int fs = d.Sections.FindIndex(s => s.Kind == SectionKind.Fairing);
                    if (fs >= 0) { Fairing(d.Sections[fs], (float)layout[fs], sel); continue; }
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

        static Color PartColor(PartDef p)
        {
            switch (p.Category)
            {
                case PartCategory.Command: return new Color(0.3f, 0.29f, 0.27f);
                case PartCategory.Engine: return p.Propellant > 0 ? new Color(0.85f, 0.85f, 0.82f) : new Color(0.25f, 0.24f, 0.23f);
                case PartCategory.Coupling: return new Color(0.12f, 0.12f, 0.12f);
                case PartCategory.Payload: return new Color(0.8f, 0.62f, 0.28f);
                case PartCategory.Utility: return new Color(0.5f, 0.5f, 0.5f);
                default: return new Color(0.88f, 0.88f, 0.85f);
            }
        }

        void Part(PartDef p, Vector3 at, Quaternion rot, bool sel)
        {
            float r0 = (float)p.Diameter * 0.5f, r1 = (float)p.Top * 0.5f, len = (float)p.Length;
            var col = PartColor(p);
            var holder = new GameObject(p.Name).transform;
            holder.SetParent(root, false);
            holder.localPosition = at;
            holder.localRotation = rot;
            if (len <= 0.01f)
            {
                // Навесные детали (опоры, RCS, стабилизаторы) места в стеке не занимают — метка-кольцо по месту.
                var ring = Add("Mark", ProcMesh.Frustum(r0 + 0.08f, r0 + 0.08f, 0.12f, 24, true), new Color(0.9f, 0.75f, 0.2f), sel, holder);
                ring.localPosition = Vector3.zero;
                return;
            }
            if (p.HasEngine && p.Propellant <= 0)
            {
                // Двигатель: рама сверху, сопла снизу; связка — кольцом (+ центральное от 5 штук), как в полёте.
                float frame = len * 0.3f, bellH = len - frame;
                Add("Frame", ProcMesh.Frustum(r0 * 0.85f, r1, frame, 24, true), col, sel, holder).localPosition = Vector3.up * bellH;
                int n = p.EngineCount, ring = n >= 5 ? n - 1 : n == 1 ? 0 : n;
                float rr = ring > 0 ? r0 * 0.62f : 0;
                float nr = ring > 0 ? Mathf.Min(rr * Mathf.Sin(Mathf.PI / Mathf.Max(ring, 2)) * 0.95f, r0 * 0.35f) : r0 * 0.9f;
                for (int k = 0; k < n; k++)
                {
                    float a = 2 * Mathf.PI * k / Mathf.Max(ring, 1);
                    var b = Add("Bell", ProcMesh.Bell(nr, nr * 0.4f, bellH, 16), new Color(0.2f, 0.18f, 0.17f), sel, holder);
                    b.localPosition = k < ring ? new Vector3(rr * Mathf.Cos(a), 0, rr * Mathf.Sin(a)) : Vector3.zero;
                }
                return;
            }
            if (p.HasEngine)
            {
                // РДТТ: корпус с топливом и короткое сопло.
                float noz = Mathf.Min(len * 0.1f, r0 * 1.2f);
                Add("Bell", ProcMesh.Bell(r0 * 0.6f, r0 * 0.3f, noz, 16), new Color(0.2f, 0.18f, 0.17f), sel, holder);
                Add("Case", ProcMesh.Frustum(r0, r1, len - noz, 24, true), col, sel, holder).localPosition = Vector3.up * noz;
                return;
            }
            Mesh mesh;
            if (p.Sphere) mesh = ProcMesh.Sphere(r0, len * 0.5f, 24, 12);
            else if (p.NoseCone) mesh = ProcMesh.Frustum(r0, r0 * 0.12f, len, 24, true);
            else if (p.Category == PartCategory.Command) mesh = ProcMesh.Frustum(r0, Mathf.Max(r1 * 0.35f, 0.2f), len, 24, true);
            else mesh = ProcMesh.Frustum(r0, r1, len, 24, true);
            Add("Body", mesh, col, sel, holder);
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
            return go.transform;
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

            TopBar();
            Catalog(new Rect(Pad, TopH, LeftW - Pad, vh - TopH - Pad));
            float rx = vw - RightW;
            float stackH = (vh - TopH - Pad) * 0.56f;
            Assembly(new Rect(rx, TopH, RightW - Pad, stackH));
            Staging(new Rect(rx, TopH + stackH + Pad, RightW - Pad, vh - TopH - stackH - 2 * Pad));
            Stats(new Rect(LeftW + Pad, vh - StatsH, vw - LeftW - RightW - 2 * Pad, StatsH - Pad));
            if (showFiles) Files(new Rect(vw * 0.5f - 260, 120, 520, 520));
            if (!string.IsNullOrEmpty(GUI.tooltip))
            {
                var tip = new Rect(LeftW + 2 * Pad, TopH + Pad, 420, 64);
                Panel(tip);
                GUI.Label(new Rect(tip.x + 10, tip.y + 6, tip.width - 20, tip.height - 12), GUI.tooltip, small);
            }
        }

        void TopBar()
        {
            Panel(new Rect(0, 0, vw, TopH - 6));
            float x = Pad, y = 8, h = TopH - 22;
            GUI.Label(new Rect(x, y, 120, h), "Конструктор", head); x += 130;
            craft.Name = GUI.TextField(new Rect(x, y, 240, h), craft.Name ?? "", btn); x += 248;
            if (GUI.Button(new Rect(x, y, 80, h), "Новая", btn))
            {
                Load(new Craft(), "Новая ракета: начните с командного модуля или двигателя");
            }
            x += 86;
            foreach (var p in CraftPresets.All())
            {
                var preset = p;
                if (GUI.Button(new Rect(x, y, 130, h), preset.Name, btnSmall)) Load(preset, $"Пресет «{preset.Name}»");
                x += 136;
            }
            if (GUI.Button(new Rect(x, y, 110, h), "Сохранить", btn)) Save();
            x += 116;
            if (GUI.Button(new Rect(x, y, 110, h), "Открыть…", btn))
            {
                showFiles = !showFiles;
                files = Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.json") : Array.Empty<string>();
            }
            x += 116;

            // Справа: миссия (цель, дата, стол) и запуск.
            var all = MissionCatalog.All;
            float rx = vw - 640;
            if (GUI.Button(new Rect(rx, y, 32, h), "◄", btn)) missionIdx = (missionIdx + all.Count - 1) % all.Count;
            GUI.Label(new Rect(rx + 36, y, 230, h), "Миссия: " + all[missionIdx].Title, label);
            if (GUI.Button(new Rect(rx + 270, y, 32, h), "►", btn)) missionIdx = (missionIdx + 1) % all.Count;
            if (GUI.Button(new Rect(rx + 310, y, 150, h), new GUIContent("Исторический", "Лететь на штатной ракете миссии"), btnSmall))
            {
                GameBootstrap.NextDesign = null;
                GameBootstrap.NextMissionId = all[missionIdx].Id;
                SceneManager.LoadScene(FlightScene);
            }
            GUI.enabled = build.Ok;
            GUI.color = build.Ok ? new Color(0.5f, 1f, 0.6f) : Color.white;
            if (GUI.Button(new Rect(rx + 468, y, 160, h), build.Ok ? "ЗАПУСК ▶" : "Есть ошибки", btn)) Launch();
            GUI.color = Color.white;
            GUI.enabled = true;
            if (status != null) GUI.Label(new Rect(x, y, rx - x - 8, h), status, small);
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
            y += 2 * (Row + 4) + 6;
            string into = selRadial >= 0 ? $"в боковую группу {selRadial + 1}" : selStack >= 0 ? "над выбранной деталью" : "наверх стека";
            GUI.Label(new Rect(x, y, w, 22), "Клик — добавить " + into, small);
            y += 26;
            var list = new List<PartDef>();
            foreach (var p in PartCatalog.All) if (p.Category == category) list.Add(p);
            const float item = 52;
            var view = new Rect(x, y, w, r.yMax - y - 8);
            partsScroll = GUI.BeginScrollView(view, partsScroll, new Rect(0, 0, w - 20, list.Count * (item + 4)));
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                string line = $"{p.Name}\n<size=12>Ø {p.Diameter:0.##} м · {p.Length:0.##} м · {p.Mass / 1000:0.###} т{EngineText(p)}</size>";
                if (GUI.Button(new Rect(0, i * (item + 4), w - 20, item), new GUIContent(line, p.Description), rowBtn)) AddPart(p.Id);
            }
            GUI.EndScrollView();
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

            float editorH = selRadial >= 0 ? 150 : 0;
            var view = new Rect(x, y, w, r.yMax - y - 8 - editorH);
            int rows = craft.Stack.Count + craft.Radials.Count;
            stackScroll = GUI.BeginScrollView(view, stackScroll, new Rect(0, 0, w - 20, rows * (Row + 2)));
            float ry = 0, iw = w - 20;
            for (int i = craft.Stack.Count - 1; i >= 0; i--)
            {
                var cp = craft.Stack[i];
                var p = PartCatalog.Get(cp.Id);
                GUI.color = selStack == i && selRadial < 0 ? SelColor : Color.white;
                string st = p != null && p.Stageable && cp.Stage >= 0 ? $"[{cp.Stage + 1}] " : "";
                if (GUI.Button(new Rect(0, ry, iw - 96, Row), st + (p?.Name ?? cp.Id), rowBtn)) { selStack = i; selRadial = -1; dirty = true; }
                GUI.color = Color.white;
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
            GUI.Label(new Rect(x, y, w - 110, 24), "Ступени (по пробелу)", label);
            if (GUI.Button(new Rect(x + w - 100, y, 100, 26), new GUIContent("Авто", "Расставить ступени заново"), btnSmall))
            {
                CraftCompiler.AutoStage(craft);
                dirty = true;
            }
            y += 32;
            var items = new List<StageItem>();
            for (int i = 0; i < craft.Stack.Count; i++)
            {
                var cp = craft.Stack[i];
                var p = PartCatalog.Get(cp.Id);
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

        void Stats(Rect r)
        {
            Panel(r);
            float x = r.x + 10, y = r.y + 8, w = r.width - 20;
            double dv = 0;
            foreach (var s in build.Stats) dv += s.DeltaVVac;
            string twr = build.Stats.Count > 0 ? $"{build.Stats[0].TwrSL:0.00}" : "—";
            GUI.Label(new Rect(x, y, w, 24),
                $"Масса {build.Mass / 1000:0.0} т · высота {build.Height:0.0} м · ширина {build.Width:0.0} м · Δv {dv:0} м/с · TWR старта {twr}", label);
            y += 28;
            float colW = w * 0.58f;
            // Слева — ступени по пробелам (как VesselDesign.ComputeStats: параллельная работа боковушек — одним прожигом).
            var view = new Rect(x, y, colW, r.yMax - y - 6);
            statsScroll = GUI.BeginScrollView(view, statsScroll, new Rect(0, 0, colW - 20, (build.Stats.Count + 1) * 22));
            GUI.Label(new Rect(0, 0, colW, 22), "Ступень                      Δv вак (у Земли)   TWR у Земли / вак   время", small);
            for (int i = 0; i < build.Stats.Count; i++)
            {
                var s = build.Stats[i];
                GUI.Label(new Rect(0, (i + 1) * 22, colW, 22),
                    $"{s.Name}", small);
                GUI.Label(new Rect(colW * 0.42f, (i + 1) * 22, colW, 22),
                    $"{s.DeltaVVac:0} ({s.DeltaVSL:0}) м/с     {s.TwrSL:0.00} / {s.TwrVac:0.00}     {s.BurnTime:0} с", small);
            }
            GUI.EndScrollView();
            // Справа — проверки перед стартом: ошибки запрещают запуск, предупреждения — нет.
            float ex = x + colW + 10, ew = w - colW - 10, ey = y;
            if (build.Errors.Count == 0 && build.Warnings.Count == 0)
                GUI.Label(new Rect(ex, ey, ew, 22), "✔ Проверки пройдены", label);
            foreach (var e in build.Errors) { GUI.Label(new Rect(ex, ey, ew, 40), "✖ " + e, err); ey += err.CalcHeight(new GUIContent("✖ " + e), ew) + 2; }
            foreach (var e in build.Warnings) { GUI.Label(new Rect(ex, ey, ew, 40), "⚠ " + e, warn); ey += warn.CalcHeight(new GUIContent("⚠ " + e), ew) + 2; }
        }

        void Files(Rect r)
        {
            Panel(r);
            Panel(r);
            float x = r.x + 10, y = r.y + 10, w = r.width - 20;
            GUI.Label(new Rect(x, y, w - 40, 24), "Сохранённые ракеты", label);
            if (GUI.Button(new Rect(r.xMax - 40, y, 30, 26), "✕", btnSmall)) showFiles = false;
            y += 32;
            GUI.Label(new Rect(x, y, w, 20), Dir, small);
            y += 24;
            var view = new Rect(x, y, w, r.yMax - y - 10);
            filesScroll = GUI.BeginScrollView(view, filesScroll, new Rect(0, 0, w - 20, files.Length * (Row + 4)));
            for (int i = 0; i < files.Length; i++)
            {
                if (!GUI.Button(new Rect(0, i * (Row + 4), w - 20, Row), Path.GetFileNameWithoutExtension(files[i]), rowBtn)) continue;
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

        void Panel(Rect r)
        {
            GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.9f);
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
            err = new GUIStyle(small);
            err.normal.textColor = new Color(1f, 0.45f, 0.4f);
            warn = new GUIStyle(small);
            warn.normal.textColor = new Color(1f, 0.85f, 0.4f);
        }
    }
}
