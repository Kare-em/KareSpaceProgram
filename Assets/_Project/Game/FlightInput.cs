using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Клавиши GDD §10.1 → ручное управление, дроссель, ступени, SAS, warp, карта.
    /// Пока на старом Input Manager: пакета Input System в проекте нет (activeInputHandler = 0),
    /// весь ввод собран здесь, чтобы переезд затронул один файл.
    /// </summary>
    [DefaultExecutionOrder(-110)] // до GameBootstrap: ввод кадра попадает в тот же шаг симуляции
    public sealed class FlightInput : MonoBehaviour
    {
        /// <summary>Скорость дросселя Shift/Ctrl, доля в секунду.</summary>
        public const double ThrottleRate = 0.5;
        /// <summary>Правка импульса удержанием (§6.11): база, м/с за секунду, и разгон — скорость растёт как
        /// 1 + (t·NodeAccel)² от времени удержания: тонко в начале, сотни м/с за пару секунд. Alt — ×NodeFine.</summary>
        public const double NodeDvRate = 2, NodeAccel = 1.5, NodeFine = 0.1;
        /// <summary>Сдвиг узла [ ] — доля периода в секунду (у гиперболы — от NodePlanner.AheadTime).</summary>
        public const double NodeTimeRate = 0.02;
        /// <summary>Орбита взлёта к цели стыковки, м: у LM ~18 км × 83 км под КСМ на 110 км. Пара: DockingAutopilot.MinClearance 15 км.</summary>
        const double LmAscentAltitude = 30000;

        NodeAnchor anchor;
        float nodeHeld;

        void Update()
        {
            var u = GameBootstrap.U;
            var v = u?.Active;
            if (v == null || PauseMenu.IsOpen) return;

            if (Input.GetKeyDown(KeyCode.M)) MapView.Toggle();
            if (Input.GetKeyDown(KeyCode.Period)) u.WarpUp();
            if (Input.GetKeyDown(KeyCode.Comma)) u.WarpDown();
            // «/» — сброс ускорения сразу в ×1, как в KSP.
            if (Input.GetKeyDown(KeyCode.Slash) || Input.GetKeyDown(KeyCode.KeypadDivide)) u.SetWarp(0);
            if (!v.Alive) return;
            Maneuver(u, v);

            // Ось: x тангаж (W = +1), y рыскание (D = +1), z крен (E = +1) — соглашение Core.
            var pilot = new Vector3d(
                Axis(KeyCode.W, KeyCode.S),
                Axis(KeyCode.D, KeyCode.A),
                Axis(KeyCode.E, KeyCode.Q));
            v.PilotInput = pilot;
            if (pilot.sqrMagnitude > 0) DropAutopilots(u);

            double thr = v.Throttle;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) thr += ThrottleRate * Time.deltaTime;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) thr -= ThrottleRate * Time.deltaTime;
            if (Input.GetKeyDown(KeyCode.Z)) thr = 1;
            if (Input.GetKeyDown(KeyCode.X)) thr = 0;
            thr = System.Math.Max(0, System.Math.Min(1, thr));
            if (thr != v.Throttle) { v.Throttle = thr; DropAutopilots(u); }

            if (Input.GetKeyDown(KeyCode.Space)) u.Stage();
            if (Input.GetKeyDown(KeyCode.T))
            {
                v.Sas = v.Sas == SasMode.Off ? SasMode.Stability : SasMode.Off;
                v.SasHoldValid = false;
            }
            if (Input.GetKeyDown(KeyCode.F))
            {
                // Режимы по кругу §4.9, Off пропускаем — для него есть T.
                int n = System.Enum.GetValues(typeof(SasMode)).Length;
                v.Sas = (SasMode)((int)v.Sas % (n - 1) + 1);
                v.SasHoldValid = false;
            }
            // Предложение §10.1, не зафиксировано: G — автопилот выведения на 200 км; у тела без атмосферы в полёте —
            // автопилот посадки (§6.12): сам сводит с орбиты, тормозит по прогнозу и садит. Повтор — снять.
            if (Input.GetKeyDown(KeyCode.G))
            {
                if (!v.Body.HasAtmosphere && !v.IsLanded)
                    u.Landing = u.Landing == null ? new LandingAutopilot(v.Body) : null;
                else if (u.Ascent == null)
                {
                    u.Ascent = new AscentAutopilot { TargetAltitude = 200000 };
                    // Взлёт с безатмосферного тела при цели стыковки (LM к КСМ, §6.6) — в её плоскость и ниже неё:
                    // догоняющий на нижней орбите сам подходит к цели по фазе.
                    var dock = v.IsLanded && !v.Body.HasAtmosphere ? u.NearestDockTarget() : null;
                    if (dock != null && dock.Body == v.Body)
                    {
                        u.Ascent.AimAtPlane(v, dock, u.Time);
                        u.Ascent.TargetAltitude = LmAscentAltitude;
                    }
                }
            }
            // Y — автопилот «к Луне» (§6.4, §6.11): опорная орбита, разгон, перестроение «Аполлона», коррекция, LOI. Повтор — снять.
            if (Input.GetKeyDown(KeyCode.Y))
            {
                if (u.Lunar != null) { u.Lunar = null; FlightControl.Cutoff(v); u.Post("Автопилот к Луне снят"); }
                else
                {
                    var moon = u.System.Get("moon");
                    if (moon != null) { u.Lunar = new LunarAutopilot(u, moon); u.Post("Автопилот к Луне включён"); }
                }
            }
            // V — сближение и стыковка с ближайшим совместимым бортом (§6.6): Ламберт, торможение, причаливание на РСУ.
            if (Input.GetKeyDown(KeyCode.V))
            {
                if (u.Docking != null) { u.Docking = null; FlightControl.Cutoff(v); u.Post("Стыковка прервана"); }
                else
                {
                    var t = u.NearestDockTarget();
                    if (t == null) u.Post("Нет борта для стыковки");
                    else { u.Docking = new DockingAutopilot(u, t); u.Post($"Стыковка: цель {t.Name}"); }
                }
            }
        }

        /// <summary>
        /// Узел манёвра (§6.11): N — поставить / перенести (Ap → Pe → +10 мин), I/K — по скорости, L/J — нормаль,
        /// O/U — радиально, [ ] — время, C — скруглить, B — выполнить / прервать, Backspace — удалить.
        /// Пока узел исполняет автопилот, правки закрыты: остаток уже тает, а B прерывает исполнение.
        /// </summary>
        void Maneuver(Universe u, Vessel v)
        {
            double now = u.Time;
            if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
            {
                v.Node = null;
                u.NodePilot = null;
                return;
            }
            if (Input.GetKeyDown(KeyCode.N) && u.NodePilot == null)
            {
                if (v.Node == null)
                {
                    anchor = NodeAnchor.Apoapsis;
                    for (int k = 0; k < 3 && !NodePlanner.Create(v, now, anchor); k++) anchor = (NodeAnchor)(((int)anchor + 1) % 3);
                    if (v.Node == null) u.Post("Манёвр можно планировать только в полёте");
                }
                else anchor = NodePlanner.Cycle(v, now, anchor);
            }
            if (v.Node == null) return;
            if (Input.GetKeyDown(KeyCode.B))
            {
                if (u.NodePilot != null)
                {
                    u.NodePilot = null;
                    FlightControl.Cutoff(v);
                    // Прерванный прожиг: компоненты — из остатка, иначе следующая правка вернёт импульс целиком.
                    NodePlanner.SyncFromRemaining(v, now);
                    u.Post("Исполнение манёвра прервано");
                }
                else if (v.Node.Total > 0.1)
                {
                    u.Ascent = null;
                    u.Landing = null;
                    u.NodePilot = new NodeAutopilot();
                }
            }
            if (u.NodePilot != null) return;
            if (Input.GetKeyDown(KeyCode.C)) NodePlanner.Circularize(v, now);

            var dv = new Vector3d(
                Axis(KeyCode.I, KeyCode.K),
                Axis(KeyCode.L, KeyCode.J),
                Axis(KeyCode.O, KeyCode.U));
            double shift = Axis(KeyCode.RightBracket, KeyCode.LeftBracket);
            if (dv.sqrMagnitude == 0 && shift == 0) { nodeHeld = 0; return; }
            nodeHeld += Time.unscaledDeltaTime;
            double accel = 1 + (nodeHeld * NodeAccel) * (nodeHeld * NodeAccel);
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) accel *= NodeFine;
            double step = accel * Time.unscaledDeltaTime;
            if (dv.sqrMagnitude > 0)
                NodePlanner.Adjust(v, now, dv.x * NodeDvRate * step, dv.y * NodeDvRate * step, dv.z * NodeDvRate * step);
            if (shift != 0)
            {
                var o = NodePlanner.CurrentOrbit(v, now);
                double span = o.IsElliptic ? o.Period : NodePlanner.AheadTime;
                NodePlanner.Shift(v, now, shift * span * NodeTimeRate * step);
            }
        }

        static double Axis(KeyCode plus, KeyCode minus) => (Input.GetKey(plus) ? 1 : 0) - (Input.GetKey(minus) ? 1 : 0);

        /// <summary>Любой ручной ввод выключает автопилоты (Core делает это сам, дублируем для HUD).</summary>
        static void DropAutopilots(Universe u)
        {
            u.Ascent = null;
            u.NodePilot = null;
            u.Landing = null;
            u.Docking = null;
            u.Lunar = null;
        }
    }
}
