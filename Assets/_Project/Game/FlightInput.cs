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
            if (Input.GetKeyDown(KeyCode.Slash) || Input.GetKeyDown(KeyCode.KeypadDivide)) u.WarpReset();
            // Порядок «ступень — корабль» после отделения (§6.9, FlightView): F2 — вид на посадку ступени, F3 — сплит-скрин,
            // F4 — отложить посадку / продолжить отложенную. Это вид, а не управление: открыто и под автопилотом миссии.
            if (Input.GetKeyDown(KeyCode.F2)) FlightView.ToggleFocus(u);
            if (Input.GetKeyDown(KeyCode.F3)) FlightView.ToggleSplit(u);
            if (Input.GetKeyDown(KeyCode.F4)) FlightView.ToggleDefer(u);
            // PageUp / PageDown — переключение между бортами (§6.13, как [ ] в KSP: скобки здесь двигают время узла,
            // Shift/Ctrl — газ). Home — назад к борту миссии. Работает и после гибели активного — увести управление на живой.
            if (Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.PageDown))
            {
                var next = u.NextVessel(Input.GetKeyDown(KeyCode.PageDown) ? 1 : -1);
                if (next == null) u.Post("Других аппаратов нет");
                else u.SwitchTo(next);
                return;
            }
            if (Input.GetKeyDown(KeyCode.Home) && u.MissionVessel != null) { u.SwitchTo(u.MissionVessel); return; }
            if (!v.Alive) return;
            // Y — автопилот всей миссии (§6.11): от стола до последней цели без рук, ускорением правит сам.
            // Снимается только повторным Y: руль, газ и прочие клавиши на время автопилота закрыты.
            if (Input.GetKeyDown(KeyCode.Y))
            {
                if (u.Mission != null) { DropAutopilots(u); FlightControl.Cutoff(v); u.SetWarp(0); u.Post("Автопилот миссии снят"); return; }
                var boot = GameBootstrap.Instance;
                u.Mission = new MissionAutopilot(u, boot.Tracker);
                u.Post($"Автопилот миссии: «{boot.Mission.Title}» без рук. Снять — Y");
            }
            if (u.Mission != null)
            {
                if (!u.Mission.OwnsPilotInput) v.PilotInput = default;
                foreach (var k in LockedKeys)
                    if (Input.GetKeyDown(k)) { u.Post("Ведёт автопилот миссии — снять: Y"); break; }
                return;
            }
            Maneuver(u, v);

            // Ось: x тангаж (W = +1), y рыскание (D = +1), z крен (E = +1) — соглашение Core.
            // Tab — режим стыковки (§6.6, как в KSP): WASD двигают борт РСУ вбок (W — к «верху» навбола, D — вправо),
            // Shift/Ctrl — вперёд/назад, повороты — стрелками, Q/E — крен. Цель — ближайший совместимый борт.
            if (Input.GetKeyDown(KeyCode.Tab) && !MapView.IsOpen) ToggleDockMode(u, v);
            if (DockMode && (v.Target == null || !v.Target.Alive)) ToggleDockMode(u, v);

            Vector3d pilot;
            if (DockMode)
            {
                pilot = new Vector3d(
                    Axis(KeyCode.UpArrow, KeyCode.DownArrow),
                    Axis(KeyCode.RightArrow, KeyCode.LeftArrow),
                    Axis(KeyCode.E, KeyCode.Q));
                // Связанные оси: верх навбола — −X, право — −Z, нос — +Y (см. FlightHud.NavBall).
                var tr = new Vector3d(
                    Axis(KeyCode.S, KeyCode.W),
                    (Shift ? 1 : 0) - (Ctrl ? 1 : 0),
                    Axis(KeyCode.A, KeyCode.D));
                if (tr.sqrMagnitude > 0) DropAutopilots(u);
                if (u.Docking == null) v.RcsTranslate = tr; // автопилот V сам правит РСУ — не перебивать нулём
            }
            else if (Alt)
            {
                // Alt+W/S — триммер тангажа (§4.6, как в KSP): балансировочный щиток или смещение элевонов; W/S не тангаж.
                // Знак как у W/S (FlightControl: W — нос к брюху = −Z); PitchTrim > 0 — нос вверх, поэтому S — плюс.
                double trim = v.PitchTrim + Axis(KeyCode.S, KeyCode.W) * TrimRate * Time.deltaTime;
                v.PitchTrim = System.Math.Max(-1, System.Math.Min(1, trim));
                pilot = new Vector3d(0, Axis(KeyCode.D, KeyCode.A), Axis(KeyCode.E, KeyCode.Q));
            }
            else
                pilot = new Vector3d(
                    Axis(KeyCode.W, KeyCode.S),
                    Axis(KeyCode.D, KeyCode.A),
                    Axis(KeyCode.E, KeyCode.Q));
            v.PilotInput = pilot;
            // Рули крылатых (§4.6): 1 — воздушный тормоз (расщеп руля), 2 — тормозной парашют на пробеге, 3 — триммер в ноль.
            if (Input.GetKeyDown(KeyCode.Alpha1))
            {
                v.AirBrake = v.AirBrake > 0.5 ? 0 : 1;
                u.Post(v.AirBrake > 0 ? "Воздушный тормоз выпущен" : "Воздушный тормоз убран");
            }
            if (Input.GetKeyDown(KeyCode.Alpha2) && v.DragChuteArea() > 0)
            {
                if (v.DragChute == DragChuteState.Open) v.JettisonDragChute();
                else if (v.DragChute == DragChuteState.Stowed) v.DeployDragChute();
                else u.Post("Тормозной парашют уже сброшен");
            }
            if (Input.GetKeyDown(KeyCode.Alpha3)) { v.PitchTrim = 0; u.Post("Триммер — 0"); }
            if (pilot.sqrMagnitude > 0) DropAutopilots(u);

            double thr = v.Throttle;
            if (!DockMode && Shift) thr += ThrottleRate * Time.deltaTime;
            if (!DockMode && Ctrl) thr -= ThrottleRate * Time.deltaTime;
            if (Input.GetKeyDown(KeyCode.Z)) thr = 1;
            if (Input.GetKeyDown(KeyCode.X)) thr = 0;
            thr = System.Math.Max(0, System.Math.Min(1, thr));
            if (thr != v.Throttle) { v.Throttle = thr; DropAutopilots(u); }

            if (Input.GetKeyDown(KeyCode.Space)) StageKey(u, v);
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
            // G — раскладное (§6.12, как в KSP): опоры выпустить/убрать, трапы КТ откинуть на грунте.
            if (Input.GetKeyDown(KeyCode.G)) u.ToggleDeploy();
            // Предложение §10.1, не зафиксировано: H — автопилот выведения на 200 км; у тела без атмосферы в полёте —
            // автопилот посадки (§6.12): сам сводит с орбиты, тормозит по прогнозу и садит. Повтор — снять.
            if (Input.GetKeyDown(KeyCode.H))
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
            // P — узел манёвра к цели с автоматическим расчётом (§6.11): тело, выбранное на карте (Tab), иначе
            // Луна с орбиты Земли / Земля с орбиты Луны. Выполнить — B.
            if (Input.GetKeyDown(KeyCode.P) && u.NodePilot == null)
            {
                var target = MapView.Focus != null && MapView.Focus != v.Body ? MapView.Focus : ManeuverAutoPlan.DefaultTarget(v);
                u.Post(ManeuverAutoPlan.Plan(u, target, out _));
            }
            // R — автопилот «к Луне» (§6.4, §6.11): опорная орбита, разгон, перестроение «Аполлона», коррекция, LOI. Повтор — снять.
            if (Input.GetKeyDown(KeyCode.R))
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
                else if (v.IsDocked) u.Undock();
                else
                {
                    var t = u.NearestDockTarget();
                    if (t == null) u.Post("Нет борта для стыковки");
                    else { v.Target = t; u.Docking = new DockingAutopilot(u, t); u.Post($"Стыковка: цель {t.Name}"); }
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

        /// <summary>Управление, закрытое при автопилоте миссии: нажатие — подсказка «снять: Y».</summary>
        static readonly KeyCode[] LockedKeys =
        {
            KeyCode.W, KeyCode.S, KeyCode.A, KeyCode.D, KeyCode.Q, KeyCode.E, KeyCode.Z, KeyCode.X, KeyCode.LeftShift,
            KeyCode.LeftControl, KeyCode.Space, KeyCode.T, KeyCode.F, KeyCode.G, KeyCode.H, KeyCode.R, KeyCode.V, KeyCode.P, KeyCode.B, KeyCode.N, KeyCode.Tab,
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3,
        };

        /// <summary>
        /// Напор, Па, выше которого отделение просит второе нажатие пробела за StageConfirmWindow с. Замер 04.10.2026
        /// (sepx, ручное отделение на 55-й с): «Джемини» на 26 кПа и «Атлас» на 36 кПа — голая верхняя ступень
        /// статически неустойчива, руль упирается в предел (τ 110 кН·м у LR-91) и за 1–2 с рвётся при α 9–12°.
        /// Пара: автопилот миссии отделяет при q ≤ 1 кПа, и его это окно не касается.
        /// </summary>
        const double StageConfirmQ = 10e3;
        const float StageConfirmWindow = 3;
        static float stageAsked = -100;

        static void StageKey(Universe u, Vessel v)
        {
            if (v.NextStageSeparates && v.DynamicPressure > StageConfirmQ && Time.unscaledTime - stageAsked > StageConfirmWindow)
            {
                stageAsked = Time.unscaledTime;
                u.Post($"Напор {v.DynamicPressure / 1000:F0} кПа: после отделения ступень может сорваться в кувырок. Пробел ещё раз — отделить");
                return;
            }
            stageAsked = -100;
            u.Stage();
        }

        /// <summary>Ручной режим стыковки (Tab): клавиши — поступательная РСУ, HUD — прибор сближения.</summary>
        public static bool DockMode { get; private set; }

        /// <summary>Скорость триммера, доля хода в секунду: весь ход за 2 с — тонко подстроить успеваешь, держать долго не надо.</summary>
        const double TrimRate = 0.5;
        static bool Alt => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        static bool Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        static bool Ctrl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        static void ToggleDockMode(Universe u, Vessel v)
        {
            if (DockMode)
            {
                DockMode = false;
                v.RcsTranslate = Vector3d.zero;
                u.Post("Режим стыковки выключен");
                return;
            }
            if (v.Target == null || !v.Target.Alive) v.Target = u.NearestDockTarget();
            if (v.Target == null) { u.Post("Нет борта для стыковки"); return; }
            DockMode = true;
            v.SetNose(true); // узел Crew Dragon под обтекателем — откинуть, иначе причаливание не захватит (§6.6)
            u.Post($"Режим стыковки: цель {v.Target.Name}. WASD — сдвиг, Shift/Ctrl — вперёд/назад, стрелки — поворот"
                + (v.RcsThrust > 0 ? "" : ". Нет РСУ — сдвиг не работает"));
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
            u.Mission = null;
        }
    }
}
