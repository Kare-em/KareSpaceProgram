using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Что показывать, а не чем управлять (§6.9, §10.3). Управление всегда у u.Active (корабль под автопилотом миссии),
    /// а вид может уйти на ступень, которую ведёт фоновый BoosterLandingAutopilot: переключать сам борт (PgUp) нельзя —
    /// автопилот миссии ведёт только активный, и корабль остался бы без пилота.
    /// Три порядка после отделения возвращаемой ступени:
    ///  F2 — «сначала посадка»: вид на ступень, корабль летит сам; после посадки вид сам возвращается на корабль;
    ///  F4 — «сначала корабль»: посадка отложена (Universe.DeferRecovery), рельсы открыты; продолжить — F4 в любой
    ///       момент или само, когда миссия корабля кончилась (ResumeRecovery сдвигает ступень во времени);
    ///  F3 — сплит-скрин: оба борта одновременно, слева главный вид, справа второй.
    /// Плавающий ноль, патч рельефа, небо и звук идут за главным видом (Main). Второй вид — та же сцена из второй
    /// камеры: до ~1000 км от нуля дрожание float ≈ 6 см (меньше пикселя на полэкрана), рельеф под ним без патча.
    /// </summary>
    [DefaultExecutionOrder(90)] // до FlightCamera (100): прямоугольники камер выставлены до их позы
    public sealed class FlightView : MonoBehaviour
    {
        /// <summary>Сколько секунд после посадки ступень ещё в кадре (касание, осадка), прежде чем вид вернётся на корабль.</summary>
        const float HoldAfterLanding = 8;
        /// <summary>Пауза между концом миссии корабля и возвратом к отложенной посадке — успеть прочесть итог.</summary>
        const float ResumeDelay = 4;
        /// <summary>Сколько секунд после отделения висит подсказка выбора порядка.</summary>
        const float PromptTime = 20;

        static Vessel focus, companion;
        static float doneAt = -1, finishedAt = -1;
        public static bool Split { get; private set; }
        public static float PromptUntil { get; private set; } = -1;

        Camera mainCam, secondCam;

        /// <summary>Борт главного вида: ступень в режиме «сначала посадка», иначе активный.</summary>
        public static Vessel Main
        {
            get
            {
                var u = GameBootstrap.U;
                if (u == null) return null;
                return focus != null && focus != u.Active && focus.Alive && u.Vessels.Contains(focus) ? focus : u.Active;
            }
        }

        /// <summary>Второй борт пары «корабль — ступень»: для главного вида на ступени это корабль, и наоборот.</summary>
        public static Vessel Other
        {
            get
            {
                var u = GameBootstrap.U;
                if (u == null) return null;
                return Main != u.Active ? u.Active : companion;
            }
        }

        /// <summary>Борт правой половины экрана; null — экран не делится.</summary>
        public static Vessel Second => Split && !MapView.IsOpen ? Other : null;

        public static bool Watching => GameBootstrap.U != null && Main != GameBootstrap.U.Active;

        /// <summary>Точка Unity в пределах dist от одного из видов: виды площадок и бортов прячутся только вдали от обоих.</summary>
        public static bool Near(Vector3 pos, float dist)
        {
            if (pos.magnitude < dist) return true;
            var s = Second;
            return s != null && (pos - FloatingOrigin.ToUnity(FloatingOrigin.WorldP(s))).magnitude < dist;
        }

        // ---------------------------------------------------------------- команды (FlightInput)

        /// <summary>F2: вид на ступень и обратно. Отложенная посадка при этом продолжается.</summary>
        public static void ToggleFocus(Universe u)
        {
            if (Watching) { focus = null; u.Post($"Вид: {u.Active.Name}"); return; }
            if (companion == null && u.Deferred.Count > 0) { Resume(u); return; }
            if (companion == null) { u.Post("Нет ступени на возврате"); return; }
            focus = companion;
            PromptUntil = -1;
            u.Post($"Вид: {focus.Name} — {u.Active.Name} летит сам. F2 — вернуться");
        }

        /// <summary>F3: сплит-скрин.</summary>
        public static void ToggleSplit(Universe u)
        {
            Split = !Split;
            PromptUntil = -1;
            if (Split && Other == null) u.Post("Сплит-скрин включён: второй борт появится при отделении возвращаемой ступени");
        }

        /// <summary>F4: отложить идущую посадку или продолжить отложенную.</summary>
        public static void ToggleDefer(Universe u)
        {
            PromptUntil = -1;
            var run = u.RunningRecovery();
            if (run != null)
            {
                if (u.DeferRecovery(run))
                {
                    if (focus == run.Vessel) focus = null;
                    if (companion == run.Vessel) companion = null;
                }
                return;
            }
            if (u.Deferred.Count > 0) Resume(u);
            else u.Post("Нет ступени на возврате");
        }

        static void Resume(Universe u)
        {
            var p = u.ResumeRecovery(u.Deferred[0]);
            if (p == null) return;
            companion = p.Vessel;
            focus = p.Vessel;
            doneAt = -1;
        }

        // ---------------------------------------------------------------- кадр

        void Awake()
        {
            focus = companion = null;
            doneAt = finishedAt = PromptUntil = -1;
            var boot = GameBootstrap.Instance;
            mainCam = boot != null ? boot.Camera : Camera.main;
            if (mainCam == null) return;
            // Копия главной камеры со всеми настройками HDRP; слушатель звука и тег MainCamera — только у главной.
            var go = Instantiate(mainCam.gameObject, mainCam.transform.parent);
            go.name = "Second Camera";
            go.tag = "Untagged";
            var al = go.GetComponent<AudioListener>();
            if (al != null) DestroyImmediate(al);
            secondCam = go.GetComponent<Camera>();
            go.GetComponent<FlightCamera>().Secondary = true;
            secondCam.enabled = false;
        }

        /// <summary>После шага вселенной: кто пара главному виду, возврат вида после посадки, продолжение отложенной.</summary>
        public static void Tick(Universe u, MissionTracker tracker)
        {
            if (u?.Active == null) return;
            var run = u.RunningRecovery();
            if (run != null && run.Vessel != companion)
            {
                companion = run.Vessel;
                doneAt = -1;
                // Подсказка — только у свежего отделения, а не у продолженной посадки (её выбрал сам игрок).
                if (focus != companion) PromptUntil = Time.unscaledTime + PromptTime;
            }
            if (companion != null && (companion == u.Active || !u.Vessels.Contains(companion))) companion = null;
            if (companion != null && u.RecoveryOf(companion) == null)
            {
                // Села или разбилась: ещё HoldAfterLanding секунд в кадре, потом вид — на корабль.
                if (doneAt < 0) doneAt = Time.unscaledTime;
                else if (Time.unscaledTime - doneAt > HoldAfterLanding)
                {
                    if (focus == companion) u.Post($"Вид: {u.Active.Name}");
                    if (focus == companion) focus = null;
                    companion = null;
                    doneAt = -1;
                }
            }
            if (focus != null && (focus == u.Active || !u.Vessels.Contains(focus))) focus = null;

            // «Сначала корабль»: миссия кончилась (итог или гибель) — возвращаемся к отложенной посадке.
            bool finished = tracker.Status != MissionStatus.Active || !u.Active.Alive;
            if (u.Deferred.Count > 0 && run == null && finished && u.Mission == null)
            {
                if (finishedAt < 0) finishedAt = Time.unscaledTime;
                else if (Time.unscaledTime - finishedAt > ResumeDelay)
                {
                    finishedAt = -1;
                    u.Post("Миссия корабля завершена — возвращаемся к посадке ступени");
                    Resume(u);
                }
            }
            else finishedAt = -1;
        }

        void LateUpdate()
        {
            if (mainCam == null || secondCam == null) return;
            bool split = Second != null;
            mainCam.rect = split ? new Rect(0, 0, 0.5f, 1) : new Rect(0, 0, 1, 1);
            secondCam.rect = new Rect(0.5f, 0, 0.5f, 1);
            if (secondCam.enabled != split) secondCam.enabled = split;
        }
    }
}
