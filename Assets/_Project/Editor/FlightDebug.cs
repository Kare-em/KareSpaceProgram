using System;
using Kare.Space.Core;
using Kare.Space.Game;

namespace Kare.Space.EditorTools
{
    /// <summary>
    /// Отладка сценариев в Play из MCP (execute_code зовёт статические методы рефлексией: codedom не знает
    /// dynamic). Не игровой код — только ускоряет цикл «поставил → посмотрел → поправил».
    /// </summary>
    public static class FlightDebug
    {
        /// <summary>Сводка полёта одной строкой.</summary>
        public static string Status()
        {
            var u = GameBootstrap.U;
            if (u == null) return "нет Universe (не Play?)";
            var v = u.Active;
            int chutes = 0, failed = 0;
            for (int i = 0; i < v.ChuteDeployed.Length; i++)
            {
                if (v.Attached[i] && v.ChuteDeployed[i]) chutes++;
                if (v.Attached[i] && v.ChuteFailed[i]) failed++;
            }
            return $"t={u.Time:F0} {v.Situation} {v.Body.Name} h={v.Altitude:F0} vs={v.VerticalSpeed:F1} v={v.SurfaceSpeed:F0} " +
                   $"q={v.DynamicPressure:F0} g={v.GForce:F1} heat={v.HeatFlux / 1e3:F0}кВт/м² chute={chutes}/{failed} " +
                   $"next='{v.NextStageLabel}' warp={u.EffectiveWarp} {v.DestroyReason}";
        }

        /// <summary>
        /// Сбросить ступени до парашюта и поставить спускаемый аппарат на вход в атмосферу над дневной стороной:
        /// высота, инерциальная скорость, угол входа (отрицательный — вниз). Как у «Востока»: 100 км, 7,6 км/с, −1,5°.
        /// </summary>
        public static string Reentry(double altitude, double speed, double gammaDeg)
        {
            var u = GameBootstrap.U;
            if (u == null) return "нет Universe";
            u.Active.Throttle = 0; // по умолчанию газ 1: запуск ТДУ на столе сразу дал бы тягу
            for (int guard = 0; guard < 20; guard++)
            {
                var a = u.Active;
                if (!a.HasNextStage || a.Design.Sequence[a.NextStage].Type == StageActionType.DeployParachute) break;
                u.Stage();
            }
            var v = u.Active;
            var b = v.Body;
            var s = (u.System.Sun.Position - b.Position).normalized;
            var pole = b.Orientation * new Vector3d(0, 0, 1);
            // Чуть до подсолнечной точки: спуск занимает ≈ 1/8 витка, сядет днём.
            var dir = (s * 0.85 - Vector3d.Cross(pole, s) * 0.5).normalized;
            var east = Vector3d.Cross(pole, dir).normalized;
            double g = gammaDeg * Math.PI / 180;
            v.Situation = Situation.Flying;
            // Отсчёт T+ ставит только отрыв от стола; без него HUD после телепорта показывал T+00:00.
            if (double.IsNaN(v.LaunchTime)) v.LaunchTime = u.Time;
            v.Throttle = 0;
            v.Sas = SasMode.Off;
            v.Position = dir * (b.Radius + altitude);
            v.Velocity = east * (speed * Math.Cos(g)) + dir * (speed * Math.Sin(g));
            return "ok " + Status();
        }

        /// <summary>
        /// Поднять весь пакет со стола на высоту altitude вертикально со скоростью up, м/с, — проверка разделения
        /// и сброса обтекателя без минут выведения. Дальше — Stage() вручную.
        /// </summary>
        public static string Lift(double altitude, double up)
        {
            var u = GameBootstrap.U;
            if (u == null) return "нет Universe";
            var v = u.Active;
            var dir = v.Position.normalized;
            v.Situation = Situation.Flying;
            if (double.IsNaN(v.LaunchTime)) v.LaunchTime = u.Time;
            v.Position = dir * (v.Body.Radius + altitude);
            v.Velocity = dir * up;
            return "ok " + Status();
        }

        public static string Stage()
        {
            GameBootstrap.U?.Stage();
            return Status();
        }

        public static string Warp(int index)
        {
            GameBootstrap.U?.SetWarp(index);
            return Status();
        }
    }
}
