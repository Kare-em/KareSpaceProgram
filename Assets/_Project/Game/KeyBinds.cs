using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>Действия управления (§10.1). Новые — только в конец: индекс хранится в PlayerPrefs по имени, но порядок
    /// списка в настройках идёт по enum.</summary>
    public enum Bind
    {
        Map, WarpUp, WarpDown, WarpReset, StageFocus, SplitScreen, DeferLanding, CameraMode, Transfers,
        PrevVessel, NextVessel, MissionVessel, MissionAutopilot, DockMode,
        PitchDown, PitchUp, YawLeft, YawRight, RollLeft, RollRight,
        ThrottleUp, ThrottleDown, ThrottleFull, ThrottleCut, Stage, Sas, SasMode, Deploy,
        AirBrake, DragChute, TrimReset,
        AutoTakeoffLanding, ToMoon, ManeuverToTarget, Dock, NodeNew, NodeDelete, NodeExecute, Circularize,
        NodePrograde, NodeRetrograde, NodeNormal, NodeAntiNormal, NodeRadialOut, NodeRadialIn, NodeLater, NodeEarlier,
        CinematicCamera, LookAtBody, MapResetView, QuickSave, QuickLoad, MapFocus, HudDetails,
    }

    /// <summary>
    /// Переназначаемые клавиши (§10.1): одно место, откуда берут и ввод, и подсказки HUD — сменил клавишу в настройках,
    /// сменилась и строка подсказки. Хранение — PlayerPrefs "key.&lt;Bind&gt;" (имя, не индекс: enum можно дополнять).
    /// У действия основная клавиша и запасная (цифровой блок и т. п.); переназначается основная.
    /// </summary>
    public static class KeyBinds
    {
        public struct Entry
        {
            public Bind Bind;
            public string Group, Title;
            public KeyCode Default, Alt;
        }

        static readonly List<Entry> entries = new List<Entry>();
        static readonly Dictionary<Bind, KeyCode> current = new Dictionary<Bind, KeyCode>();
        static readonly Dictionary<Bind, Entry> byBind = new Dictionary<Bind, Entry>();

        public static IReadOnlyList<Entry> All => entries;

        static void Add(Bind b, string group, string title, KeyCode key, KeyCode alt = KeyCode.None)
        {
            var e = new Entry { Bind = b, Group = group, Title = title, Default = key, Alt = alt };
            entries.Add(e);
            byBind[b] = e;
        }

        static KeyBinds()
        {
            const string G = "Общее", F = "Полёт", A = "Автопилоты", N = "Манёвры", C = "Камера и карта";
            Add(Bind.Map, C, "Карта", KeyCode.M);
            Add(Bind.CameraMode, C, "Режим камеры (Shift — назад)", KeyCode.F5);
            Add(Bind.CinematicCamera, C, "Кинокамера: смена ракурса", KeyCode.F7);
            Add(Bind.LookAtBody, C, "Взгляд на планету", KeyCode.F8);
            Add(Bind.MapResetView, C, "Карта: к аппарату, сброс масштаба", KeyCode.Home);
            Add(Bind.MapFocus, C, "Карта: следующее тело (Shift — назад)", KeyCode.Tab);
            Add(Bind.Transfers, C, "Меню перелётов", KeyCode.F6);
            Add(Bind.HudDetails, C, "Подробности на экране", KeyCode.F1);
            Add(Bind.StageFocus, C, "Вид на ступень", KeyCode.F2);
            Add(Bind.SplitScreen, C, "Сплит-скрин", KeyCode.F3);
            Add(Bind.DeferLanding, C, "Отложить посадку ступени", KeyCode.F4);

            Add(Bind.QuickSave, G, "Быстрое сохранение", KeyCode.F9);
            Add(Bind.QuickLoad, G, "Быстрая загрузка", KeyCode.F10);
            Add(Bind.WarpUp, G, "Ускорение времени +", KeyCode.Period);
            Add(Bind.WarpDown, G, "Ускорение времени −", KeyCode.Comma);
            Add(Bind.WarpReset, G, "Ускорение ×1", KeyCode.Slash, KeyCode.KeypadDivide);
            Add(Bind.PrevVessel, G, "Предыдущий аппарат", KeyCode.PageUp);
            Add(Bind.NextVessel, G, "Следующий аппарат", KeyCode.PageDown);
            Add(Bind.MissionVessel, G, "К аппарату миссии", KeyCode.Home);

            Add(Bind.PitchDown, F, "Тангаж: нос вниз", KeyCode.W);
            Add(Bind.PitchUp, F, "Тангаж: нос вверх", KeyCode.S);
            Add(Bind.YawLeft, F, "Рыскание влево", KeyCode.A);
            Add(Bind.YawRight, F, "Рыскание вправо", KeyCode.D);
            Add(Bind.RollLeft, F, "Крен влево", KeyCode.Q);
            Add(Bind.RollRight, F, "Крен вправо", KeyCode.E);
            Add(Bind.ThrottleUp, F, "Газ +", KeyCode.LeftShift);
            Add(Bind.ThrottleDown, F, "Газ −", KeyCode.LeftControl);
            Add(Bind.ThrottleFull, F, "Полный газ", KeyCode.Z);
            Add(Bind.ThrottleCut, F, "Выключить двигатель", KeyCode.X);
            Add(Bind.Stage, F, "Ступень / разделение", KeyCode.Space);
            Add(Bind.Sas, F, "SAS вкл / выкл", KeyCode.T);
            Add(Bind.SasMode, F, "Режим SAS по кругу (прогрейд, нормаль…)", KeyCode.F);
            Add(Bind.Deploy, F, "Опоры, трапы, раскладное", KeyCode.G);
            Add(Bind.AirBrake, F, "Воздушный тормоз", KeyCode.Alpha1);
            Add(Bind.DragChute, F, "Тормозной парашют (выпуск / сброс)", KeyCode.Alpha2);
            Add(Bind.TrimReset, F, "Триммер в ноль", KeyCode.Alpha3);
            Add(Bind.DockMode, F, "Ручная стыковка", KeyCode.Tab);

            Add(Bind.MissionAutopilot, A, "Автопилот миссии", KeyCode.Y);
            Add(Bind.AutoTakeoffLanding, A, "Выведение / посадка", KeyCode.H);
            Add(Bind.ToMoon, A, "К Луне", KeyCode.R);
            Add(Bind.ManeuverToTarget, A, "Манёвр к цели", KeyCode.P);
            Add(Bind.Dock, A, "Автостыковка / расстыковка", KeyCode.V);

            Add(Bind.NodeNew, N, "Новый манёвр / перенести (Ap → Pe)", KeyCode.N);
            Add(Bind.NodeDelete, N, "Удалить манёвр", KeyCode.Backspace, KeyCode.Delete);
            Add(Bind.NodeExecute, N, "Выполнить / прервать манёвр", KeyCode.B);
            Add(Bind.Circularize, N, "Скруглить орбиту", KeyCode.C);
            Add(Bind.NodePrograde, N, "Манёвр: прогрейд", KeyCode.I);
            Add(Bind.NodeRetrograde, N, "Манёвр: ретрогрейд", KeyCode.K);
            Add(Bind.NodeNormal, N, "Манёвр: нормаль", KeyCode.L);
            Add(Bind.NodeAntiNormal, N, "Манёвр: антинормаль", KeyCode.J);
            Add(Bind.NodeRadialOut, N, "Манёвр: радиально наружу", KeyCode.O);
            Add(Bind.NodeRadialIn, N, "Манёвр: радиально внутрь", KeyCode.U);
            Add(Bind.NodeLater, N, "Манёвр позже", KeyCode.RightBracket);
            Add(Bind.NodeEarlier, N, "Манёвр раньше", KeyCode.LeftBracket);
            Load();
        }

        static string Pref(Bind b) => "key." + b;

        static void Load()
        {
            current.Clear();
            foreach (var e in entries)
            {
                KeyCode k = e.Default;
                try
                {
                    string s = PlayerPrefs.GetString(Pref(e.Bind), "");
                    if (s.Length > 0 && Enum.TryParse(s, out KeyCode parsed)) k = parsed;
                }
                catch (Exception) { } // PlayerPrefs до старта движка недоступны — остаётся клавиша по умолчанию
                current[e.Bind] = k;
            }
        }

        public static KeyCode Key(Bind b) => current.TryGetValue(b, out var k) ? k : KeyCode.None;
        static KeyCode Alt(Bind b) => byBind.TryGetValue(b, out var e) ? e.Alt : KeyCode.None;

        public static bool Down(Bind b) => Input.GetKeyDown(Key(b)) || Alt(b) != KeyCode.None && Input.GetKeyDown(Alt(b));
        public static bool Held(Bind b) => Input.GetKey(Key(b)) || Alt(b) != KeyCode.None && Input.GetKey(Alt(b));
        /// <summary>Ось из пары действий: +1 / −1 / 0.</summary>
        public static double Axis(Bind plus, Bind minus) => (Held(plus) ? 1 : 0) - (Held(minus) ? 1 : 0);

        /// <summary>Растёт при каждой смене клавиш — кеши подсказок (FlightHud.KeysHint) по нему пересобираются.</summary>
        public static int Version { get; private set; }

        public static void Set(Bind b, KeyCode k)
        {
            Version++;
            current[b] = k;
            PlayerPrefs.SetString(Pref(b), k.ToString());
            PlayerPrefs.Save();
        }

        public static void ResetAll()
        {
            foreach (var e in entries) PlayerPrefs.DeleteKey(Pref(e.Bind));
            PlayerPrefs.Save();
            Version++;
            Load();
        }

        /// <summary>Другие действия на этой клавише — для предупреждения в настройках (контекст у них может различаться:
        /// Home на карте и в полёте).</summary>
        public static IEnumerable<Bind> Conflicts(Bind b)
        {
            var k = Key(b);
            foreach (var e in entries)
                if (e.Bind != b && Key(e.Bind) == k && !SplitByContext(b, e.Bind)) yield return e.Bind;
        }

        /// <summary>Пары с общей клавишей по задумке: на открытой карте работает первое, в полёте — второе
        /// (FlightInput пропускает полётное, пока карта открыта и клавиши совпадают).</summary>
        static readonly (Bind map, Bind flight)[] ContextPairs =
        {
            (Bind.MapResetView, Bind.MissionVessel),
            (Bind.MapFocus, Bind.DockMode),
        };

        static bool SplitByContext(Bind a, Bind b)
        {
            foreach (var p in ContextPairs)
                if (p.map == a && p.flight == b || p.map == b && p.flight == a) return true;
            return false;
        }

        public static string Title(Bind b) => byBind.TryGetValue(b, out var e) ? e.Title : b.ToString();

        /// <summary>Подпись клавиши для подсказок: «F5», «Пробел», «.».</summary>
        public static string Label(Bind b) => Label(Key(b));

        public static string Label(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.None: return "—";
                case KeyCode.Space: return "Пробел";
                case KeyCode.Period: return ".";
                case KeyCode.Comma: return ",";
                case KeyCode.Slash: return "/";
                case KeyCode.LeftBracket: return "[";
                case KeyCode.RightBracket: return "]";
                case KeyCode.LeftShift: case KeyCode.RightShift: return "Shift";
                case KeyCode.LeftControl: case KeyCode.RightControl: return "Ctrl";
                case KeyCode.LeftAlt: case KeyCode.RightAlt: return "Alt";
                case KeyCode.Backspace: return "Bksp";
                case KeyCode.Delete: return "Del";
                case KeyCode.Insert: return "Ins";
                case KeyCode.KeypadDivide: return "Num /";
                case KeyCode.KeypadMultiply: return "Num *";
                case KeyCode.KeypadMinus: return "Num -";
                case KeyCode.KeypadPlus: return "Num +";
                case KeyCode.KeypadPeriod: return "Num .";
                case KeyCode.KeypadEnter: return "Num Enter";
                case KeyCode.Return: return "Enter";
                case KeyCode.Escape: return "Esc";
                case KeyCode.BackQuote: return "`";
                case KeyCode.Minus: return "-";
                case KeyCode.Equals: return "=";
                case KeyCode.Semicolon: return ";";
                case KeyCode.Quote: return "'";
                case KeyCode.Backslash: return "\\";
                case KeyCode.UpArrow: return "↑";
                case KeyCode.DownArrow: return "↓";
                case KeyCode.LeftArrow: return "←";
                case KeyCode.RightArrow: return "→";
                case KeyCode.PageUp: return "PgUp";
                case KeyCode.PageDown: return "PgDn";
            }
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return ((int)(k - KeyCode.Alpha0)).ToString();
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return "Num" + (int)(k - KeyCode.Keypad0);
            return k.ToString();
        }
    }
}
