using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Настройки рулей в окне секции (§4.6, как «Authority limiter» и триммер в KSP): ход рулей, триммер тангажа,
    /// воздушный тормоз и тормозной парашют пробега. Значения живут в Vessel (ControlLimit, PitchTrim, AirBrake,
    /// DragChute) — те же, что правят клавиши 1/2/3 и Alt+W/S (FlightInput) и читает физика.
    /// </summary>
    public sealed partial class PartInspector
    {
        /// <summary>Шаг «Хода рулей» и его нижний предел: ниже 25 % крылатый на посадке уже не парирует порыв.</summary>
        const double LimitStep = 0.25, LimitMin = 0.25;
        /// <summary>Шаг триммера кнопкой — десятая хода (пара с FlightInput.TrimRate: клавиша тот же шаг за 0,2 с).</summary>
        const double TrimStep = 0.1;

        static GUIStyle ctlText, ctlBtn;

        /// <summary>Есть что настраивать: подвижные поверхности у плоскостей или тормозной парашют.</summary>
        static bool HasControls(SectionDef s)
        {
            if (s.DragChuteArea > 0) return true;
            if (s.Wings == null) return false;
            foreach (var w in s.Wings)
                if (w.Surfaces != null || w.ControlFraction > 0 || w.BrakeArea > 0 || w.BodyFlapArea > 0) return true;
            return false;
        }

        static float ControlsGui(Rect r, Vessel v)
        {
            if (v == null) return 0;
            if (ctlText == null)
            {
                ctlText = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, alignment = TextAnchor.MiddleLeft };
                ctlBtn = new GUIStyle(GUI.skin.button) { fontSize = 13, richText = true, padding = new RectOffset(4, 4, 2, 2) };
            }
            float x = r.x, y = r.y, cw = r.width;
            const float Row = 26, B = 30;

            GUI.Label(new Rect(x, y, cw - 3 * (B + 4), 24), $"Ход рулей <b>{v.ControlLimit * 100:0}%</b>", ctlText);
            if (GUI.Button(new Rect(x + cw - 3 * (B + 4), y, B, 22), "−", ctlBtn)) v.ControlLimit = System.Math.Max(LimitMin, v.ControlLimit - LimitStep);
            if (GUI.Button(new Rect(x + cw - 2 * (B + 4), y, B, 22), "+", ctlBtn)) v.ControlLimit = System.Math.Min(1, v.ControlLimit + LimitStep);
            y += Row;

            GUI.Label(new Rect(x, y, cw - 3 * (B + 4), 24),
                $"Триммер <b>{v.PitchTrim * 100:+0;−0;0}%</b> <color=#9aa4ad>(Alt+W/S, 3 — ноль)</color>", ctlText);
            if (GUI.Button(new Rect(x + cw - 3 * (B + 4), y, B, 22), "−", ctlBtn)) v.PitchTrim = System.Math.Max(-1, v.PitchTrim - TrimStep);
            if (GUI.Button(new Rect(x + cw - 2 * (B + 4), y, B, 22), "+", ctlBtn)) v.PitchTrim = System.Math.Min(1, v.PitchTrim + TrimStep);
            if (GUI.Button(new Rect(x + cw - (B + 4), y, B + 4, 22), "0", ctlBtn)) v.PitchTrim = 0;
            y += Row;

            GUI.Label(new Rect(x, y, cw - 110, 24), $"Воздушный тормоз <b>{v.AirBrake * 100:0}%</b> <color=#9aa4ad>(1)</color>", ctlText);
            if (GUI.Button(new Rect(x + cw - 106, y, 106, 22), v.AirBrake > 0.5 ? "убрать" : "выпустить", ctlBtn))
                v.AirBrake = v.AirBrake > 0.5 ? 0 : 1;
            y += Row;

            if (v.DragChuteArea() > 0)
            {
                string st = v.DragChute == DragChuteState.Open ? "выпущен" : v.DragChute == DragChuteState.Jettisoned ? "сброшен" : "уложен";
                GUI.Label(new Rect(x, y, cw - 110, 24), $"Тормозной парашют: <b>{st}</b> <color=#9aa4ad>(2)</color>", ctlText);
                if (v.DragChute != DragChuteState.Jettisoned
                    && GUI.Button(new Rect(x + cw - 106, y, 106, 22), v.DragChute == DragChuteState.Open ? "сбросить" : "выпустить", ctlBtn))
                {
                    if (v.DragChute == DragChuteState.Open) v.JettisonDragChute();
                    else v.DeployDragChute();
                }
                y += Row;
                GUI.Label(new Rect(x, y, cw, 18),
                    $"<color=#9aa4ad>после касания, до {FlightPhysics.DragChuteMaxSpeed:0} м/с; сброс на {FlightPhysics.DragChuteJettisonSpeed:0} м/с</color>", ctlText);
                y += 20;
            }
            return y - r.y;
        }
    }
}
