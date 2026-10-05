using System;
using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kare.Space.Game
{
    /// <summary>Что показывает окно детали: заголовок и текст живые — вызываются каждый кадр.</summary>
    public sealed class PartInfo
    {
        /// <summary>Тот же Key — то же окно: повторный щелчок по детали не плодит копий.</summary>
        public object Key;
        public Func<string> Title, Text;
        /// <summary>Уставка высоты ввода парашюта, м (§4.8); null — у детали парашюта нет.</summary>
        public Func<double> ChuteGet;
        public Action<double> ChuteSet;
        /// <summary>Своя отрисовка под текстом (таблица ступеней): прямоугольник с доступной высотой → занятая высота.</summary>
        public Func<Rect, float> Custom;
        public float Width = 380;
    }

    /// <summary>
    /// Окна деталей (§10.2, как правый щелчок по детали в KSP, но левой — правая у нас крутит камеру): щелчок по
    /// секции ракеты в полёте или по детали в конструкторе открывает паспорт. Окно таскается за заголовок; незакреплённое
    /// одно и заменяется следующим щелчком, закреплённые остаются (несколько сразу) и обновляются вживую.
    /// Сам себя добавляет в сцены полёта и конструктора — сборщики сцен не трогаем.
    /// </summary>
    public sealed partial class PartInspector : MonoBehaviour
    {
        public static PartInspector Instance { get; private set; }

        /// <summary>Щелчок, а не перетаскивание камеры: смещение мыши меньше этого, px экрана, и короче ClickTime, с.</summary>
        const float ClickSlop = 6, ClickTime = 0.6f;
        /// <summary>Шаг кнопок уставки парашюта, м. Пара: FlightPhysics.ChuteAltitudeMin/Max — края диапазона.</summary>
        const double ChuteStep = 500;
        const int MaxWindows = 8;
        const float TitleH = 26;

        sealed class Win
        {
            public PartInfo Info;
            public Vector2 Pos;
            public bool Pinned, Drag;
            public Vector2 Grab;
            public float H = 160;
        }

        readonly List<Win> wins = new List<Win>();
        HangarController hangar;
        Vector3 downAt;
        float downTime;
        bool downOk;
        GUIStyle title, text, btn;

        static readonly Color Bg = new Color(0.03f, 0.06f, 0.10f, 0.88f);
        static readonly Color Bar = new Color(0.10f, 0.30f, 0.40f, 0.95f);
        static readonly Color BarPinned = new Color(0.36f, 0.26f, 0.06f, 0.95f);

        // ---------------------------------------------------------------- появление в сцене

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Hook()
        {
            SceneManager.sceneLoaded -= Loaded;
            SceneManager.sceneLoaded += Loaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void First() => Ensure();

        static void Loaded(Scene s, LoadSceneMode m) => Ensure();

        static void Ensure()
        {
            if (Instance != null) return;
            if (FindAnyObjectByType<GameBootstrap>() == null && FindAnyObjectByType<HangarController>() == null) return;
            new GameObject("Part Inspector").AddComponent<PartInspector>();
        }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        void Start() => hangar = FindAnyObjectByType<HangarController>();

        /// <summary>Масштаб IMGUI — как у HUD полёта и у конструктора (база 1080 по высоте).</summary>
        float Scale => hangar != null ? Screen.height / 1080f : Mathf.Max(0.75f, Screen.height / 1080f);

        // ---------------------------------------------------------------- выбор щелчком

        void Update()
        {
            var m = Input.mousePosition;
            if (Input.GetMouseButtonDown(0))
            {
                downAt = m;
                downTime = Time.unscaledTime;
                downOk = !Blocked(m);
            }
            // Нажатие поймал контрол IMGUI (кнопка, поле) — это щелчок по интерфейсу.
            if (downOk && GUIUtility.hotControl != 0) downOk = false;
            if (Input.GetMouseButtonUp(0))
            {
                if (downOk && (m - downAt).magnitude < ClickSlop && Time.unscaledTime - downTime < ClickTime) Pick(m);
                downOk = false;
            }
        }

        bool Blocked(Vector3 m)
        {
            if (PauseMenu.IsOpen || MapView.IsOpen || OverWindow(m) || HudHits.Contains(m)) return true;
            return hangar != null && !hangar.CanInspect(m);
        }

        /// <summary>Мышь (Input.mousePosition) над окном детали: конструктор не крутит камеру, полёт не выбирает за окном.</summary>
        public static bool OverWindow(Vector3 mouse)
        {
            var me = Instance;
            if (me == null || me.wins.Count == 0) return false;
            float s = me.Scale;
            var p = new Vector2(mouse.x, Screen.height - mouse.y) / s;
            foreach (var w in me.wins)
                if (new Rect(w.Pos, new Vector2(w.Info.Width, w.H)).Contains(p)) return true;
            return false;
        }

        void Pick(Vector3 m)
        {
            PartInfo info = hangar != null ? hangar.InspectAt(m) : FlightPick(m);
            if (info != null) Show(info, new Vector2(m.x, Screen.height - m.y) / Scale);
        }

        static PartInfo FlightPick(Vector3 m)
        {
            var boot = GameBootstrap.Instance;
            var cam = boot != null && boot.Camera != null ? boot.Camera : Camera.main;
            if (cam == null) return null;
            var ray = cam.ScreenPointToRay(m);
            VesselView best = null;
            int bi = -1;
            float bd = float.PositiveInfinity;
            foreach (var view in FindObjectsByType<VesselView>(FindObjectsSortMode.None))
            {
                if (view.Vessel == null) continue;
                int i = view.PickSection(ray, out float d);
                if (i >= 0 && d < bd) { best = view; bi = i; bd = d; }
            }
            return best != null ? FlightSection(best.Vessel, bi) : null;
        }

        /// <summary>
        /// Секция в полёте. Борт ищется заново каждый кадр: после разделения секция живёт в обломке с тем же проектом
        /// (Vessel.Split копирует Design), и закреплённое окно продолжает показывать её топливо и парашют.
        /// </summary>
        public static PartInfo FlightSection(Vessel v, int idx)
        {
            var design = v.Design;
            var s = design.Sections[idx];
            Vessel holder = v;
            Vessel Where()
            {
                var u = GameBootstrap.U;
                if (u == null) return null;
                if (holder != null && u.Vessels.Contains(holder) && holder.Attached[idx]) return holder;
                holder = null;
                foreach (var x in u.Vessels)
                    if (x.Design == design && idx < x.Attached.Length && x.Attached[idx]) return holder = x;
                return null;
            }
            var info = new PartInfo
            {
                Key = Tuple.Create<object, int>(design, idx),
                Title = () => s.Name,
                Text = () =>
                {
                    var x = Where();
                    return x != null ? PartInfoText.Section(s, x, idx)
                                     : PartInfoText.Section(s, null, idx) + "\n<color=#9aa4ad>секции больше нет в полёте</color>";
                },
            };
            if (s.ParachuteArea > 0)
            {
                info.ChuteGet = () =>
                {
                    var x = Where();
                    return x != null ? x.ChuteAltitude[idx] : FlightPhysics.ClampChuteAltitude(s.ChuteAltitude);
                };
                info.ChuteSet = a => Where()?.SetChuteAltitude(idx, a);
            }
            // Рули и тормозной парашют крылатых (§4.6) — PartInspector.Controls.
            if (HasControls(s)) info.Custom = rect => ControlsGui(rect, Where());
            return info;
        }

        // ---------------------------------------------------------------- окна

        /// <summary>Открыть окно: незакреплённое одно — его содержимое и меняется; закреплённые не трогаем.</summary>
        public static void Show(PartInfo info, Vector2 at, bool pinned = false)
        {
            var me = Instance;
            if (me == null) return;
            var wins = me.wins;
            foreach (var w in wins)
                if (Equals(w.Info.Key, info.Key)) { if (!w.Pinned) w.Info = info; me.Front(w); return; }
            foreach (var w in wins)
                if (!w.Pinned && !pinned) { w.Info = info; me.Front(w); return; }
            if (wins.Count >= MaxWindows) wins.RemoveAt(0);
            wins.Add(new Win { Info = info, Pos = at + new Vector2(24, 16), Pinned = pinned });
        }

        public static bool IsOpen(object key)
        {
            var me = Instance;
            if (me == null) return false;
            foreach (var w in me.wins) if (Equals(w.Info.Key, key)) return true;
            return false;
        }

        /// <summary>Закрыть окно с этим ключом; true — было открыто (кнопка-переключатель конструктора).</summary>
        public static bool Close(object key)
        {
            var me = Instance;
            if (me == null) return false;
            return me.wins.RemoveAll(w => Equals(w.Info.Key, key)) > 0;
        }

        void Front(Win w)
        {
            wins.Remove(w);
            wins.Add(w);
        }

        void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = true, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            title.normal.textColor = Color.white;
            text = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, alignment = TextAnchor.UpperLeft, wordWrap = true };
            text.normal.textColor = Color.white;
            btn = new GUIStyle(GUI.skin.button) { fontSize = 13, richText = true, padding = new RectOffset(4, 4, 2, 2) };
        }

        void OnGUI()
        {
            if (wins.Count == 0 && hangar == null) return;
            if (PauseMenu.IsOpen) return;
            GUI.depth = -10; // поверх HUD: окно получает щелчок раньше перетаскиваемых панелей под ним
            Styles();
            float s = Scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
            float sw = Screen.width / s, sh = Screen.height / s;
            if (hangar != null) hangar.InspectorButtons(btn);

            Win close = null, front = null;
            for (int k = 0; k < wins.Count; k++)
            {
                var w = wins[k];
                switch (Draw(w, sw, sh))
                {
                    case 1: close = w; break;
                    case 2: front = w; break;
                }
            }
            if (close != null) wins.Remove(close);
            if (front != null && wins.Contains(front)) Front(front);
        }

        /// <summary>0 — ничего, 1 — закрыть, 2 — поднять наверх.</summary>
        int Draw(Win w, float sw, float sh)
        {
            var e = Event.current;
            var info = w.Info;
            int result = 0;
            w.Pos.x = Mathf.Clamp(w.Pos.x, 0, Mathf.Max(0, sw - info.Width));
            w.Pos.y = Mathf.Clamp(w.Pos.y, 0, Mathf.Max(0, sh - TitleH));
            var r = new Rect(w.Pos, new Vector2(info.Width, w.H));
            Fill(r, Bg);
            HudHits.Add(r);
            Fill(new Rect(r.x, r.y, r.width, TitleH), w.Pinned ? BarPinned : Bar);

            var closeR = new Rect(r.xMax - 28, r.y + 2, 26, TitleH - 4);
            var pinR = new Rect(closeR.x - 98, r.y + 2, 96, TitleH - 4);
            if (GUI.Button(pinR, w.Pinned ? "закреплено" : "закрепить", btn)) { w.Pinned = !w.Pinned; result = 2; }
            if (GUI.Button(closeR, "×", btn)) result = 1;
            GUI.Label(new Rect(r.x + 8, r.y, pinR.x - r.x - 12, TitleH), "<b>" + (info.Title?.Invoke() ?? "") + "</b>", title);

            // Перетаскивание за заголовок (кроме кнопок) — как панели HUD (FlightHud.Draggable).
            var grip = new Rect(r.x, r.y, pinR.x - r.x, TitleH);
            if (w.Drag && !Input.GetMouseButton(0)) w.Drag = false;
            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition))
            {
                w.Drag = true;
                w.Grab = e.mousePosition - w.Pos;
                result = 2;
                e.Use();
            }
            else if (w.Drag && e.type == EventType.MouseDrag)
            {
                w.Pos = e.mousePosition - w.Grab;
                e.Use();
            }

            float y = r.y + TitleH + 6, x = r.x + 10, cw = r.width - 20;
            string body = info.Text?.Invoke();
            if (!string.IsNullOrEmpty(body))
            {
                var gc = new GUIContent(body);
                float th = text.CalcHeight(gc, cw);
                GUI.Label(new Rect(x, y, cw, th), gc, text);
                y += th + 4;
            }
            if (info.ChuteGet != null)
            {
                double a = info.ChuteGet();
                GUI.Label(new Rect(x, y, cw - 150, 24), $"Ввод парашюта ниже <b>{a / 1000:0.0#} км</b>", text);
                if (GUI.Button(new Rect(x + cw - 146, y, 30, 22), "−", btn)) info.ChuteSet?.Invoke(a - ChuteStep);
                if (GUI.Button(new Rect(x + cw - 112, y, 30, 22), "+", btn)) info.ChuteSet?.Invoke(a + ChuteStep);
                if (GUI.Button(new Rect(x + cw - 78, y, 78, 22), "штатно", btn)) info.ChuteSet?.Invoke(FlightPhysics.ChuteDeployAltitude);
                y += 26;
                GUI.Label(new Rect(x, y, cw, 18),
                    $"<color=#9aa4ad>от {FlightPhysics.ChuteAltitudeMin / 1000:0} до {FlightPhysics.ChuteAltitudeMax / 1000:0} км, штатно {FlightPhysics.ChuteDeployAltitude / 1000:0} км</color>", text);
                y += 20;
            }
            if (info.Custom != null) y += info.Custom(new Rect(x, y, cw, Mathf.Max(60, sh - y - 10))) + 4;
            w.H = y - r.y + 6;

            // Щелчок по телу окна не должен уйти панелям и камере под ним.
            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                if (result == 0) result = 2;
                e.Use();
            }
            return result;
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
