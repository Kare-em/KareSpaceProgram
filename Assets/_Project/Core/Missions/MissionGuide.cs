using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Тутор ручного полёта (GDD §10.2) после выведения: по текущей цели миссии и состоянию борта — что делать
    /// сейчас и какой клавишей. Выведение в атмосфере ведёт отдельная подсказка по углу (FlightHud.Tutor); этот —
    /// всё остальное: витки, сход, разгон к Луне, коррекции, торможение, посадка, возврат. Шаги повторяют
    /// сценарии MissionAutopilot — те же действия, только руками игрока.
    /// </summary>
    public static class MissionGuide
    {
        /// <summary>Коридор входа при возврате от Луны, м над поверхностью. Пара: MissionAutopilot.ReturnPerigee (45 км).</summary>
        public const double EntryMin = 30e3, EntryMax = 60e3;
        /// <summary>Перицентр схода с околоземной орбиты, м. Пара: MissionAutopilot.DeorbitPerigee.</summary>
        public const double DeorbitPerigee = 40e3;
        /// <summary>Высота раскрытия парашюта, м. Пара: MissionAutopilot.ChuteAltitude.</summary>
        public const double ChuteAltitude = 6000;
        /// <summary>За сколько до узла звать к прожигу, с, сверх половины его длительности.</summary>
        const double BurnLead = 30;
        /// <summary>Орбита с апоцентром ниже стольких радиусов тела — «низкая»: с неё сходят, а не корректируют вход.</summary>
        const double LowOrbitRadii = 3;

        public static bool Next(Universe u, MissionTracker tr, out string step, out string hint)
        {
            step = hint = null;
            var v = u.Active;
            if (v == null || !v.Alive || tr.Status != MissionStatus.Active || tr.CurrentIndex < 0) return false;
            var obj = tr.Def.Objectives[tr.CurrentIndex];
            var goal = u.System.Get(obj.Body) ?? v.Body;
            var body = v.Body;
            double t = u.Time;

            // Узел уже стоит — главное его исполнить, что бы ни было дальше по миссии.
            if (v.Node != null && u.NodePilot == null && v.Node.Total > 0.1)
            {
                double burn = FlightControl.BurnTime(v, v.Node.Total);
                double toStart = v.Node.Time - (double.IsInfinity(burn) ? 0 : burn / 2) - t;
                step = "Манёвр";
                hint = toStart > BurnLead
                    ? $"Импульс {v.Node.Total:F0} м/с через {GameCalendar.FormatDuration(toStart)}. B — автопилот исполнит. Вручную: F до «манёвр», . — ускорить время до узла."
                    : $"Сейчас: нос на синий маркер (F → «манёвр»), Z — полный газ, X — когда остаток 0. Или B — автопилот.";
                return true;
            }

            if (v.IsLanded || v.Situation == Situation.Splashed)
            {
                if (obj.Type == ObjectiveType.Drive)
                {
                    step = "Луноход";
                    hint = $"Пробел — съехать с посадочной ступени, W/S — вперёд/назад, A/D — поворот. Проехать {obj.Min:F0} м.";
                }
                else if (body.HasAtmosphere) return false; // старт со стола — подсказка по углу
                else
                {
                    step = "Взлёт";
                    hint = "H — автопилот взлёта (на стыковку — в плоскость корабля). Вручную: Z, Пробел, нос на восток после 1 км.";
                }
                return true;
            }

            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, body.Mu, t);
            double peAlt = orbit.PeriapsisRadius - body.Radius;
            double atm = body.AtmosphereTop;
            bool inOrbit = orbit.IsElliptic && peAlt > atm;
            bool air = body.HasAtmosphere && v.Altitude < atm;

            // Спуск в атмосфере — для любой цели последний шаг.
            if (air && !v.AnyEngineRunning && v.VerticalSpeed < 0)
            {
                step = "Спуск";
                hint = v.Altitude > ChuteAltitude
                    ? $"Двигатели не нужны. Пробел — отделить спускаемый аппарат. Парашют — Пробел ниже {ChuteAltitude / 1000:F0} км."
                    : "Пробел — парашют (если ещё не раскрыт). Дальше — ждать касания.";
                return true;
            }

            if (goal == body)
            {
                switch (obj.Type)
                {
                    case ObjectiveType.Orbit:
                        if (!orbit.IsElliptic)
                        {
                            step = "Торможение";
                            hint = $"Пролёт по гиперболе. N → узел в перицентре, K (ретроград) до замкнутой орбиты, B — выполнить.";
                        }
                        else if (peAlt < obj.Min)
                        {
                            step = "Поднять перицентр";
                            hint = $"Перицентр {peAlt / 1000:F0} км, нужно ≥ {obj.Min / 1000:F0}. N — узел в апоцентре, I — прибавить, B — выполнить.";
                        }
                        else
                        {
                            step = "На орбите";
                            hint = "Орбита есть — держать. . (точка) — ускорить время, Y — автопилот доведёт миссию.";
                        }
                        return true;
                    case ObjectiveType.Return:
                        // Низкая орбита — сход; вытянутая (идём от Луны) — коррекция перицентра в коридор.
                        if (inOrbit && orbit.ApoapsisRadius < LowOrbitRadii * body.Radius)
                        {
                            step = "Сход с орбиты";
                            hint = $"N — узел, K (ретроград) пока перицентр не станет {DeorbitPerigee / 1000:F0} км, B — выполнить. Перед этим Пробел — отделить лишнее.";
                        }
                        else if (orbit.IsElliptic && peAlt > EntryMax)
                        {
                            step = "Коррекция входа";
                            hint = $"Перицентр {peAlt / 1000:F0} км — мимо атмосферы. N, K — опустить до {EntryMin / 1000:F0}–{EntryMax / 1000:F0} км, B.";
                        }
                        else
                        {
                            step = "Вход в атмосферу";
                            hint = "Пробел — отделить двигательный отсек, F → ретроград: тепловой щит вперёд. Парашют ниже 6 км.";
                        }
                        return true;
                    case ObjectiveType.Landing:
                    case ObjectiveType.Impact:
                        step = obj.Type == ObjectiveType.Landing ? "Посадка" : "Попадание";
                        hint = obj.Type == ObjectiveType.Landing
                            ? "H — автопилот посадки: сам сведёт с орбиты, выпустит опоры и затормозит. Вручную: F → ретроград, газ так, чтобы у поверхности было < 5 м/с."
                            : "Траектория уже ведёт в поверхность — . (точка) ускорить время.";
                        return true;
                }
            }

            // Цель — спутник своего тела (Луна с Земли).
            if (goal.Parent == body)
            {
                if (!inOrbit)
                {
                    step = "Опорная орбита";
                    hint = "Сначала орбита: H — автопилот выведения, или по подсказке угла. Потом — к Луне.";
                    return true;
                }
                var enc = u.PredictActive(4).Find(p => p.Body == goal);
                if (enc == null)
                {
                    step = $"Разгон · {goal.Name}";
                    hint = $"P — рассчитать разгон (или M → Tab → {goal.Name} → P), затем B. Целиком без рук — Y.";
                    return true;
                }
                double pe = enc.Orbit.PeriapsisRadius - goal.Radius;
                bool hit = obj.Type == ObjectiveType.Impact || obj.Type == ObjectiveType.Landing;
                step = "Перелёт";
                hint = $"Встреча через {GameCalendar.FormatDuration(enc.StartTime - t)}, перицентр {pe / 1000:F0} км. . — ускорить время" +
                       (hit && pe > 0 ? "; для посадки H уже у Луны." : ".");
                return true;
            }

            // Цель — родитель (Земля с орбиты Луны).
            if (body.Parent == goal)
            {
                step = $"Возврат · {goal.Name}";
                hint = orbit.IsElliptic
                    ? $"P — рассчитать разгон домой, затем B. Перицентр выйдет в коридоре входа {EntryMin / 1000:F0}–{EntryMax / 1000:F0} км."
                    : ". — ускорить время до выхода из сферы влияния.";
                return true;
            }

            step = "Задача";
            hint = obj.Describe(u.System) + ". Y — автопилот миссии.";
            return true;
        }
    }
}
