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

        void Update()
        {
            var u = GameBootstrap.U;
            var v = u?.Active;
            if (v == null || PauseMenu.IsOpen) return;

            if (Input.GetKeyDown(KeyCode.M)) MapView.Toggle();
            if (Input.GetKeyDown(KeyCode.Period)) u.WarpUp();
            if (Input.GetKeyDown(KeyCode.Comma)) u.WarpDown();
            if (!v.Alive) return;

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
            // Предложение §10.1, не зафиксировано: G — автопилот выведения на 200 км.
            if (Input.GetKeyDown(KeyCode.G) && u.Ascent == null)
                u.Ascent = new AscentAutopilot { TargetAltitude = 200000 };
        }

        static double Axis(KeyCode plus, KeyCode minus) => (Input.GetKey(plus) ? 1 : 0) - (Input.GetKey(minus) ? 1 : 0);

        /// <summary>Любой ручной ввод выключает автопилоты (Core делает это сам, дублируем для HUD).</summary>
        static void DropAutopilots(Universe u)
        {
            u.Ascent = null;
            u.NodePilot = null;
            u.Landing = null;
        }
    }
}
