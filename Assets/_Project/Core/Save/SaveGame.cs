using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Kare.Space.Core
{
    /// <summary>Заголовок сохранения — первая строка файла: список слотов читает только её (§10.3).</summary>
    public sealed class SaveHeader
    {
        public int Version;
        /// <summary>Местное время записи «yyyy-MM-dd HH:mm:ss» — для списка слотов.</summary>
        public string Saved;
        public string MissionId, MissionTitle, Vessel, Body;
        /// <summary>Высота активного борта, м, и время вселенной, с от J2000.</summary>
        public double Altitude, Time;
        /// <summary>Миссия летела на ракете из конструктора (GameBootstrap.NextDesign), а не на историческом носителе.</summary>
        public bool CustomDesign;
    }

    /// <summary>Результат загрузки: вселенная, трекер миссии и что восстановить не удалось (автопилоты).</summary>
    public sealed class LoadedGame
    {
        public SaveHeader Header;
        public Universe Universe;
        public MissionDef Mission;
        public MissionTracker Tracker;
        /// <summary>Что было в полёте, но в сохранение не попало: «миссия целиком» и автопилоты с несохраняемым состоянием.</summary>
        public readonly List<string> Dropped = new List<string>();
    }

    /// <summary>
    /// Сохранение полёта (GDD §10.3: JSON, версия формата). Пишется граф объектов ядра целиком, отражением по полям
    /// (публичным и закрытым): борта, их проекты (общие у частей одной ракеты — по ссылке, иначе стыковка
    /// «a.Design == t.Design» после загрузки не сработала бы), орбиты, узлы, пары «свежих» разделений, кеш события рельсов,
    /// частные автопилоты. Почему отражение, а не список полей руками: в Vessel больше сотни полей и они прибавляются
    /// с каждой фичей — забытое поле ломало бы продолжение полёта тихо, а тест «save» сравнивает траектории побитово.
    /// Тела, космодромы, сама вселенная, трекер и описание миссии — внешние ссылки (по id), их не копируем.
    /// Формат: строка 1 — заголовок (SaveHeader), дальше — тело {"universe", "tracker", "objects"}; объекты-классы — в
    /// таблице "objects" и в полях как {"$r": номер}, структуры и массивы — на месте.
    /// </summary>
    public static partial class SaveGame
    {
        public const int Version = 1;
        public const string Format = "kare-save";

        /// <summary>
        /// Поля Universe с автопилотами: их состояние пишем, если оно сериализуемо, иначе — null (пилот выключен, причина в
        /// LoadedGame.Dropped). «Миссия целиком» (Mission) — всегда null: её сценарий — итератор (yield), а состояние
        /// машины итератора — поля, сгенерированные компилятором, с именами, меняющимися от любой правки сценария.
        /// </summary>
        static readonly HashSet<string> Droppable = new HashSet<string> { "Ascent", "NodePilot", "Landing", "Docking", "Lunar", "Recoveries", "Deferred" };
        const string MissionField = "Mission";

        static readonly Assembly CoreAssembly = typeof(Vessel).Assembly;

        // ---------------------------------------------------------------- запись

        /// <summary>Снимок полёта в текст. Вселенную не меняет. tracker может быть null (свободный полёт).</summary>
        public static string Save(Universe u, MissionTracker tracker, bool customDesign = false)
        {
            if (u == null) throw new ArgumentNullException(nameof(u));
            var a = u.Active;
            var header = new Dictionary<string, object>
            {
                ["format"] = Format,
                ["version"] = (long)Version,
                ["saved"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["mission"] = tracker?.Def.Id,
                ["missionTitle"] = tracker?.Def.Title,
                ["vessel"] = a?.Name,
                ["body"] = a?.Body?.Name,
                ["altitude"] = a != null ? a.Altitude : 0.0,
                ["time"] = u.Time,
                ["customDesign"] = customDesign,
            };

            var w = new Writer(u, tracker);
            var body = new Dictionary<string, object>
            {
                ["time"] = u.Time,
                ["nextVesselId"] = (long)NextVesselId,
                ["universe"] = w.EncodeUniverse(),
                ["tracker"] = tracker != null ? w.EncodeFields(tracker) : null,
            };
            body["objects"] = w.Table;
            body["dropped"] = w.Dropped;

            var sb = new StringBuilder();
            WriteJson(sb, header, false, 0);
            sb.Append('\n');
            WriteJson(sb, body, true, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>Статический счётчик Vessel.Id (закрытый nextId): после загрузки новые борта не должны повторить id.</summary>
        static int NextVesselId
        {
            get => NextIdField?.GetValue(null) is int n ? n : 0;
            set => NextIdField?.SetValue(null, value);
        }
        static readonly FieldInfo NextIdField = typeof(Vessel).GetField("nextId", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>Все поля экземпляра с базовыми классами. Поля событий не пишем: подписки восстанавливает Universe.AfterLoad.</summary>
        static List<FieldInfo> Fields(Type t)
        {
            if (fieldCache.TryGetValue(t, out var list)) return list;
            list = new List<FieldInfo>();
            for (var k = t; k != null && k != typeof(object) && k != typeof(ValueType); k = k.BaseType)
                foreach (var f in k.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType))
                    {
                        // Поле-событие (Vessel.Event, Universe.Message) — подписчики живут вне ядра, их не сохраняем.
                        // Любой другой делегат (замыкание в автопилоте) сохранить нельзя — владелец не сериализуем.
                        if (k.GetEvent(f.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null) continue;
                    }
                    list.Add(f);
                }
            fieldCache[t] = list;
            return list;
        }
        static readonly Dictionary<Type, List<FieldInfo>> fieldCache = new Dictionary<Type, List<FieldInfo>>();

        sealed class RefEq : IEqualityComparer<object>
        {
            public static readonly RefEq Instance = new RefEq();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }

        sealed class Writer
        {
            readonly Universe universe;
            readonly MissionTracker tracker;
            public readonly List<object> Table = new List<object>();
            public readonly List<object> Dropped = new List<object>();
            readonly List<object> tableObjects = new List<object>();
            readonly Dictionary<object, int> ids = new Dictionary<object, int>(RefEq.Instance);

            public Writer(Universe u, MissionTracker tr)
            {
                universe = u;
                tracker = tr;
            }

            /// <summary>
            /// Поля вселенной. Борта — первыми: у них младшие номера в таблице, и откат неудачного автопилота (Rollback)
            /// их не задевает. Автопилоты — последними, каждый отдельно: несериализуемый выключается, а не валит сохранение.
            /// </summary>
            public Dictionary<string, object> EncodeUniverse()
            {
                var d = new Dictionary<string, object>();
                var fields = Fields(typeof(Universe));
                foreach (var f in fields)
                    if (f.Name == nameof(Universe.Vessels)) d[f.Name] = Encode(f.GetValue(universe));
                foreach (var f in fields)
                {
                    if (f.Name == nameof(Universe.Vessels) || Droppable.Contains(f.Name)) continue;
                    if (f.Name == MissionField)
                    {
                        if (f.GetValue(universe) != null) Dropped.Add("Mission: сценарий «миссия целиком» не сохраняется — включите Y заново");
                        d[f.Name] = null;
                        continue;
                    }
                    d[f.Name] = Encode(f.GetValue(universe));
                }
                foreach (var f in fields)
                {
                    if (!Droppable.Contains(f.Name)) continue;
                    var v = f.GetValue(universe);
                    if (v is IList list && !(v is Array))
                    {
                        var outList = new List<object>();
                        foreach (var e in list)
                            if (TryEncode(e, f.Name, out var node)) outList.Add(node);
                        d[f.Name] = outList;
                    }
                    else d[f.Name] = TryEncode(v, f.Name, out var node) ? node : null;
                }
                return d;
            }

            bool TryEncode(object v, string what, out object node)
            {
                int mark = Table.Count;
                try
                {
                    node = Encode(v);
                    return true;
                }
                catch (NotSupportedException e)
                {
                    Rollback(mark);
                    Dropped.Add($"{what}: {e.Message}");
                    node = null;
                    return false;
                }
            }

            /// <summary>Отменить объекты, добавленные в таблицу с номера mark (недописанный автопилот).</summary>
            void Rollback(int mark)
            {
                for (int i = Table.Count - 1; i >= mark; i--)
                {
                    ids.Remove(tableObjects[i]);
                    tableObjects.RemoveAt(i);
                    Table.RemoveAt(i);
                }
            }

            public Dictionary<string, object> EncodeFields(object o)
            {
                var d = new Dictionary<string, object>();
                foreach (var f in Fields(o.GetType()))
                {
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType))
                        throw new NotSupportedException($"делегат {o.GetType().Name}.{f.Name}");
                    d[f.Name] = Encode(f.GetValue(o));
                }
                return d;
            }

            object Encode(object v)
            {
                if (v == null) return null;
                var t = v.GetType();
                if (t == typeof(double)) return v;
                if (t == typeof(float)) return (double)(float)v;
                if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte) ||
                    t == typeof(ushort) || t == typeof(uint)) return Convert.ToInt64(v, CultureInfo.InvariantCulture);
                if (t == typeof(bool) || t == typeof(string)) return v;
                if (t.IsEnum) return v.ToString();
                if (t == typeof(Vector3d))
                {
                    var p = (Vector3d)v;
                    return new List<object> { p.x, p.y, p.z };
                }
                if (t == typeof(QuaternionD))
                {
                    var q = (QuaternionD)v;
                    return new List<object> { q.x, q.y, q.z, q.w };
                }
                if (t.IsArray)
                {
                    if (t.GetArrayRank() != 1) throw new NotSupportedException($"многомерный массив {t.Name}");
                    var l = new List<object>();
                    foreach (var e in (Array)v) l.Add(Encode(e));
                    return l;
                }
                if (t.IsGenericType)
                {
                    var g = t.GetGenericTypeDefinition();
                    if (g == typeof(List<>) || g == typeof(HashSet<>))
                    {
                        var l = new List<object>();
                        foreach (var e in (IEnumerable)v) l.Add(Encode(e));
                        return l;
                    }
                    if (g == typeof(Dictionary<,>))
                    {
                        var l = new List<object>();
                        foreach (DictionaryEntry e in (IDictionary)v) l.Add(new List<object> { Encode(e.Key), Encode(e.Value) });
                        return l;
                    }
                    if (IsValueTuple(t))
                    {
                        var l = new List<object>();
                        int n = t.GetGenericArguments().Length;
                        for (int i = 1; i <= n; i++) l.Add(Encode(t.GetField("Item" + i).GetValue(v)));
                        return l;
                    }
                }
                // Внешние ссылки: тела и космодромы — по id, вселенная, система тел, трекер и миссия — те, что у загрузки.
                if (v is CelestialBody b) return new Dictionary<string, object> { ["$body"] = b.Id };
                if (v is LaunchSite s) return new Dictionary<string, object> { ["$site"] = s.Id };
                if (v is MissionDef md) return new Dictionary<string, object> { ["$mission"] = md.Id };
                if (ReferenceEquals(v, universe)) return new Dictionary<string, object> { ["$x"] = "universe" };
                if (ReferenceEquals(v, universe.System)) return new Dictionary<string, object> { ["$x"] = "system" };
                if (tracker != null && ReferenceEquals(v, tracker)) return new Dictionary<string, object> { ["$x"] = "tracker" };
                if (typeof(Delegate).IsAssignableFrom(t)) throw new NotSupportedException($"делегат {t.Name}");
                // Только типы ядра: чужой класс (Random, перечислитель LINQ) и машина итератора (имя с '<') — не данные.
                if (t.Assembly != CoreAssembly || t.Name.IndexOf('<') >= 0 || t.IsDefined(typeof(CompilerGeneratedAttribute), false) ||
                    v is Universe || v is SolarSystem || v is MissionTracker)
                    throw new NotSupportedException($"тип {t.FullName}");
                if (t.IsValueType)
                {
                    var d = EncodeFields(v);
                    d["$t"] = t.FullName;
                    return d;
                }
                if (!ids.TryGetValue(v, out int id))
                {
                    id = Table.Count;
                    ids[v] = id;
                    tableObjects.Add(v);
                    Table.Add(null);
                    var d = new Dictionary<string, object> { ["$t"] = t.FullName };
                    foreach (var kv in EncodeFields(v)) d[kv.Key] = kv.Value;
                    Table[id] = d;
                }
                return new Dictionary<string, object> { ["$r"] = (long)id };
            }
        }

        // ---------------------------------------------------------------- JSON-текст

        /// <summary>
        /// Число без потерь: "R" (кратчайшее) с проверкой обратного чтения, при расхождении — "G17". "R" в .NET Framework/Mono
        /// на части значений не возвращает то же число (известная ошибка, x64), а G17 — всегда. NaN, ±∞ и −0 в JSON нет —
        /// пишем строками ("NaN", "Infinity", "-Infinity", "-0"): у борта NaN — штатное «не задано» (LaunchTime, CatchPin).
        /// </summary>
        static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d)) { sb.Append("\"NaN\""); return; }
            if (double.IsPositiveInfinity(d)) { sb.Append("\"Infinity\""); return; }
            if (double.IsNegativeInfinity(d)) { sb.Append("\"-Infinity\""); return; }
            if (d == 0 && BitConverter.DoubleToInt64Bits(d) != 0) { sb.Append("\"-0\""); return; }
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            if (BitConverter.DoubleToInt64Bits(double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)) != BitConverter.DoubleToInt64Bits(d))
                s = d.ToString("G17", CultureInfo.InvariantCulture);
            sb.Append(s);
        }

        /// <summary>Запись дерева: объекты таблицы и поля вселенной — по строке на поле, мелочь (векторы, массивы чисел) — в строку.</summary>
        static void WriteJson(StringBuilder sb, object v, bool pretty, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: sb.Append(Json.Quote(s)); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case double d: WriteNumber(sb, d); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case Dictionary<string, object> o:
                {
                    // Отступы — только у первых уровней (тело, вселенная, таблица, объект): глубже файл и так читается.
                    bool nl = pretty && depth < 3;
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in o)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        if (nl) sb.Append('\n').Append(' ', 2 * (depth + 1));
                        sb.Append(Json.Quote(kv.Key)).Append(':');
                        WriteJson(sb, kv.Value, pretty, depth + 1);
                    }
                    if (nl && !first) sb.Append('\n').Append(' ', 2 * depth);
                    sb.Append('}');
                    return;
                }
                case List<object> list:
                {
                    bool nl = pretty && depth < 2;
                    sb.Append('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (nl) sb.Append('\n').Append(' ', 2 * (depth + 1));
                        WriteJson(sb, list[i], pretty, depth + 1);
                    }
                    if (nl && list.Count > 0) sb.Append('\n').Append(' ', 2 * depth);
                    sb.Append(']');
                    return;
                }
                default: throw new InvalidOperationException($"JSON: {v.GetType()}");
            }
        }
    }
}
