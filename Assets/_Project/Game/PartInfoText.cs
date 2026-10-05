using System;
using System.Text;
using Kare.Space.Core;

namespace Kare.Space.Game
{
    /// <summary>
    /// Текст окна детали (PartInspector, §10.2): паспорт секции или детали конструктора — массы, размеры, двигатель,
    /// топливо, РСУ, парашют, теплозащита, экипаж. Rich text IMGUI; живые числа (остаток топлива, запуски) — из Vessel.
    /// </summary>
    public static class PartInfoText
    {
        const string Key = "#8cd8ff", Dim = "#9aa4ad", Bad = "#ff6050", Good = "#7dff9a";
        static readonly StringBuilder sb = new StringBuilder(1024);

        static void Line(string key, string value) => sb.Append("<color=").Append(Key).Append('>').Append(key).Append(":</color> ").Append(value).Append('\n');
        static void Sub(string text) => sb.Append("   <color=").Append(Dim).Append('>').Append(text).Append("</color>\n");

        static string T(double kg) => StageTable.Mass(kg);
        static string Kn(double n) => n >= 1e6 ? (n / 1e6).ToString("0.00") + " МН" : (n / 1000).ToString("0.#") + " кН";

        public static string Kind(SectionKind k)
        {
            switch (k)
            {
                case SectionKind.Stage: return "ступень";
                case SectionKind.Payload: return "полезная нагрузка";
                case SectionKind.Capsule: return "спускаемый аппарат";
                case SectionKind.Fairing: return "головной обтекатель";
                default: return k.ToString();
            }
        }

        public static string Category(PartCategory c)
        {
            switch (c)
            {
                case PartCategory.Command: return "командный отсек";
                case PartCategory.Tank: return "бак";
                case PartCategory.Engine: return "двигатель";
                case PartCategory.Coupling: return "разделитель";
                case PartCategory.Payload: return "полезная нагрузка";
                default: return c.ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// Топливная пара по двигателю: в ядре компонентов нет (§4.4 считает только массу и УИ), а игроку важно, что
        /// в баках. Известные двигатели — по имени, остальные — по УИ: выше 400 с бывает только водород.
        /// </summary>
        public static string PropellantKind(EngineDef e)
        {
            if (e == null) return "—";
            if (e.Solid) return "твёрдое смесевое";
            string n = e.Name ?? "";
            if (n.StartsWith("A-7")) return "этанол + жидкий кислород";
            if (n.StartsWith("J-2") || n.StartsWith("RL") || n.StartsWith("РД-0120")) return "жидкий водород + кислород";
            if (n.StartsWith("РД-107") || n.StartsWith("РД-108") || n.StartsWith("РД-0109") || n.StartsWith("РД-0110")
                || n.StartsWith("РД-0124") || n.StartsWith("РД-58") || n.StartsWith("РД-170") || n.StartsWith("F-1")
                || n.StartsWith("LR-89") || n.StartsWith("LR-105") || n.StartsWith("Merlin") || n.StartsWith("РД-Г"))
                return "керосин + жидкий кислород";
            if (n.StartsWith("РД-253") || n.StartsWith("РД-0210") || n.StartsWith("РД-0212") || n.StartsWith("LR-87")
                || n.StartsWith("LR-91") || n.StartsWith("SPS") || n.StartsWith("APS") || n.StartsWith("DPS") || n.StartsWith("Draco")
                || n.StartsWith("AJ10") || n.StartsWith("ТДУ") || n.StartsWith("СКД") || n.StartsWith("КТДУ")
                || n.StartsWith("Тормозные") || n.StartsWith("Корректирующая"))
                return "самовоспламеняющееся (НДМГ / АТ)";
            return e.IspVac >= 400 ? "жидкий водород + кислород" : "жидкое двухкомпонентное";
        }

        /// <summary>
        /// Паспорт секции. <paramref name="v"/> — борт, в котором секция сейчас (null — конструктор или отделена):
        /// с ним остаток топлива, запуски и состояние парашюта живые.
        /// </summary>
        public static string Section(SectionDef s, Vessel v, int i)
        {
            sb.Clear();
            string kind = Kind(s.Kind);
            if (s.IsRadial) kind += $" · боковые блоки ×{s.RadialCount} (цифры на группу)";
            if (s.Beside) kind += " · сбоку от ядра";
            Line("Тип", kind);
            if (v != null) Line("Борт", v.Name + (v.Situation == Situation.Destroyed ? $" <color={Bad}>разрушен</color>" : ""));
            double prop = v != null ? v.Propellant[i] : s.Propellant;
            string mass = $"сухая {T(s.DryMass)}";
            if (s.Propellant > 0) mass += $" · заправленная {T(s.Mass)} · сейчас {T(s.DryMass + prop)}";
            Line("Масса", mass);
            Line("Размеры", s.Sphere ? $"шар Ø {s.Diameter:0.##} м" : $"длина {s.Length:0.##} м · Ø {s.Diameter:0.##} м");
            if (s.Propellant > 0)
                Line("Топливо", $"<b>{T(prop)}</b> / {T(s.Propellant)} ({prop / s.Propellant * 100:0}%)"
                              + (s.HasEngine ? " · " + PropellantKind(s.Engine) : ""));
            if (s.HasEngine) Engine(s, v, i, prop);
            if (s.UllageMotors) Line("Осадка", "свои РДТТ осадки при разделении");
            if (s.RcsTorque > 0) Line("РСУ", $"момент {s.RcsTorque / 1000:0.##} кН·м");
            if (s.ParachuteArea > 0)
            {
                string st = "сложен";
                if (v != null)
                {
                    if (v.ChuteFailed[i]) st = $"<color={Bad}>оборван</color>";
                    else if (v.ChuteDeployed[i]) st = $"<color={Good}>раскрыт</color> ({FlightPhysics.ChuteFraction(v.ChuteOpenTime[i]) * 100:0}% площади)";
                    else if (v.ChuteArmed[i]) st = "взведён, ждёт высоты";
                }
                Line("Парашют", $"купол {s.ParachuteArea:0} м² · {st}");
                Sub($"ввод ниже уставки и при напоре ≤ {FlightPhysics.ChuteSafeQ / 1000:0} кПа, рвётся выше {FlightPhysics.ChuteMaxQ / 1000:0} кПа");
            }
            if (s.Kind == SectionKind.Capsule || s.MaxHeatFlux > 1e6)
                Line("Теплозащита", $"предельный поток {s.MaxHeatFlux / 1e6:0.0#} МВт/м²" + (v != null ? $" · сейчас {v.HeatFlux / 1e6:0.00}" : ""));
            else Line("Нагрев", $"предел {s.MaxHeatFlux / 1e6:0.0#} МВт/м²");
            if (s.Crew > 0) Line("Экипаж", $"{s.Crew} чел.");
            if (s.FinArea > 0) Line("Стабилизаторы", $"{s.FinArea:0.#} м²");
            if (s.Wings != null && s.Wings.Count > 0) Line("Крылья", $"{s.Wings.Count} поверхн.");
            if (s.DockingPort) Line("Стыковка", "узел на верхнем торце");
            if (s.Deploy != DeployKind.None) Line("Раскладное (G)", Deploy(s.Deploy));
            if (s.Rover) Line("Шасси", "самоходное (луноход)");
            if (s.Kind == SectionKind.Fairing) Line("Закрывает", $"{s.EnclosesBelow} секц. под собой");
            if (v != null && !v.Attached[i]) sb.Append($"<color={Dim}>отделена</color>\n");
            return sb.ToString().TrimEnd('\n');
        }

        static string Deploy(DeployKind k)
        {
            switch (k)
            {
                case DeployKind.Legs: return "посадочные опоры";
                case DeployKind.PyroLegs: return "опоры на пирозамках (только выпуск)";
                case DeployKind.Ramps: return "трапы схода";
                case DeployKind.Lid: return "крышка с солнечной батареей";
                case DeployKind.Gear: return "шасси";
                default: return k.ToString();
            }
        }

        static void Engine(SectionDef s, Vessel v, int i, double prop)
        {
            var e = s.Engine;
            string state = "";
            if (v != null && v.Attached[i])
                state = v.Running[i] ? $" · <color={Good}>работает</color>" : v.Armed[i] ? " · взведён" : " · молчит";
            Line("Двигатель", $"<b>{e.Name}</b>" + (s.EngineCount > 1 ? $" ×{s.EngineCount}" : "") + (e.Solid ? " (РДТТ)" : "") + state);
            int n = Math.Max(1, s.EngineCount);
            Sub($"тяга вак. {Kn(e.ThrustVac * n)} · у земли {Kn(e.Thrust(101325) * n)}" + (n > 1 ? $" (на {n})" : ""));
            Sub($"УИ вак. {e.IspVac:0} с · у земли {e.IspSL:0} с · расход {e.MassFlow * n:0.#} кг/с");
            double flow = e.MassFlow * n;
            if (s.Propellant > 0 && flow > 0)
                Sub($"работа: полная {StageTable.Time(s.Propellant / flow)} · осталось {StageTable.Time(prop / flow)}");
            string ign = e.Ignitions > 50 ? "запусков без ограничения" : v != null ? $"запусков осталось {v.IgnitionsLeft[i]} из {e.Ignitions}" : $"запусков {e.Ignitions}";
            string extra = ign;
            if (e.NeedsUllage) extra += " · нужна осадка";
            if (e.MinThrottle < 1) extra += $" · дросселирование до {e.MinThrottle * 100:0}%";
            if (e.GimbalDeg > 0) extra += $" · качание ±{e.GimbalDeg:0.#}°";
            Sub(extra);
        }

        /// <summary>Деталь конструктора (§5.4): паспорт из каталога и секция, в которую она скомпилировалась.</summary>
        public static string Part(PartDef p, SectionDef section, int stage)
        {
            sb.Clear();
            if (!string.IsNullOrEmpty(p.Description)) sb.Append("<i>").Append(p.Description).Append("</i>\n");
            Line("Тип", Category(p.Category) + (stage >= 0 ? $" · ступень {stage}" : ""));
            if (section != null) Line("Секция полёта", section.Name);
            string mass = $"сухая {T(p.DryMass)}";
            if (p.Propellant > 0) mass += $" · заправленная {T(p.Mass)}";
            Line("Масса", mass);
            string dia = Math.Abs(p.Top - p.Diameter) > 1e-3 ? $"Ø {p.Diameter:0.##} → {p.Top:0.##} м" : $"Ø {p.Diameter:0.##} м";
            Line("Размеры", p.Length > 0 ? $"длина {p.Length:0.##} м · {dia}" : $"навесная · {dia}");
            EngineDef e = p.HasEngine ? p.Engine() : null;
            if (p.Propellant > 0) Line("Топливо", $"{T(p.Propellant)}" + (e != null ? " · " + PropellantKind(e) : ""));
            if (e != null)
            {
                int n = Math.Max(1, p.EngineCount);
                Line("Двигатель", $"<b>{e.Name}</b>" + (n > 1 ? $" ×{n}" : "") + (e.Solid ? " (РДТТ)" : ""));
                Sub($"тяга вак. {Kn(e.ThrustVac * n)} · у земли {Kn(e.Thrust(101325) * n)}");
                Sub($"УИ вак. {e.IspVac:0} с · у земли {e.IspSL:0} с · расход {e.MassFlow * n:0.#} кг/с");
                if (p.Propellant > 0) Sub($"работа на своём топливе {StageTable.Time(p.Propellant / (e.MassFlow * n))}");
                string extra = e.Ignitions > 50 ? "запусков без ограничения" : $"запусков {e.Ignitions}";
                if (e.NeedsUllage) extra += " · нужна осадка";
                if (e.MinThrottle < 1) extra += $" · дросселирование до {e.MinThrottle * 100:0}%";
                if (e.GimbalDeg > 0) extra += $" · качание ±{e.GimbalDeg:0.#}°";
                Sub(extra);
            }
            if (p.UllageMotors) Line("Осадка", "РДТТ осадки при разделении");
            if (p.RcsTorque > 0) Line("РСУ", $"момент {p.RcsTorque / 1000:0.##} кН·м");
            if (p.ParachuteArea > 0) Line("Парашют", $"купол {p.ParachuteArea:0} м²");
            if (p.MaxHeatFlux > 0) Line("Теплозащита", $"предельный поток {p.MaxHeatFlux / 1e6:0.0#} МВт/м²");
            if (p.Crew > 0) Line("Экипаж", $"{p.Crew} чел.");
            if (p.FinArea > 0) Line("Стабилизаторы", $"{p.FinArea:0.#} м²");
            if (p.Decoupler) Line("Разделитель", "отделяет всё ниже себя");
            if (p.RadialDecoupler) Line("Разделитель", "радиальный: держит боковые блоки");
            if (p.Fairing) Line("Обтекатель", "закрывает нагрузку над верхней ступенью");
            if (p.DockingPort) Line("Стыковка", "узел на верхнем торце");
            if (p.LandingLegs) Line("Опоры", "посадочные (G)");
            return sb.ToString().TrimEnd('\n');
        }
    }
}
