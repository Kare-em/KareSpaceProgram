using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;

namespace Kare.Space.Core
{
    public static partial class SaveGame
    {
        // ---------------------------------------------------------------- чтение

        /// <summary>Только заголовок (первая строка) — для списка слотов без разбора всего файла. null — не сохранение.</summary>
        public static SaveHeader ReadHeader(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int nl = text.IndexOf('\n');
            string line = nl >= 0 ? text.Substring(0, nl) : text;
            try
            {
                return Json.Parse(line) is Dictionary<string, object> h ? Header(h) : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        static SaveHeader Header(Dictionary<string, object> h)
        {
            if (Json.Str(h, "format") != Format) return null;
            return new SaveHeader
            {
                Version = (int)Json.Num(h, "version", 0),
                Saved = Json.Str(h, "saved"),
                MissionId = Json.Str(h, "mission"),
                MissionTitle = Json.Str(h, "missionTitle"),
                Vessel = Json.Str(h, "vessel"),
                Body = Json.Str(h, "body"),
                Altitude = Json.Num(h, "altitude", 0),
                Time = Json.Num(h, "time", 0),
                CustomDesign = h.TryGetValue("customDesign", out var c) && c is bool b && b,
            };
        }

        /// <summary>
        /// Восстановить полёт в новой вселенной на переданной системе тел. Ускорение остаётся сохранённым — игра сама
        /// решает, сбросить ли его (SetWarp(0)). Файл новее версии формата — отказ: поля могли поменять смысл.
        /// </summary>
        public static LoadedGame Load(string text, SolarSystem system)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (system == null) throw new ArgumentNullException(nameof(system));
            int nl = text.IndexOf('\n');
            if (nl < 0) throw new FormatException("Сохранение: нет тела файла");
            var header = ReadHeader(text) ?? throw new FormatException("Сохранение: не тот формат");
            if (header.Version > Version) throw new FormatException($"Сохранение версии {header.Version} новее игры ({Version})");
            var body = Json.Parse(text.Substring(nl + 1)) as Dictionary<string, object> ?? throw new FormatException("Сохранение: тело не объект");

            // Конструктор Vessel при загрузке тратит id счётчика; после — вернуть его так, будто бортов не создавали.
            int idBefore = NextVesselId;
            var game = new LoadedGame { Header = header };
            if (body.TryGetValue("dropped", out var dr) && dr is List<object> dl)
                foreach (var s in dl) if (s is string str) game.Dropped.Add(str);

            double time = Json.Num(body, "time", header.Time);
            var u = new Universe(system, time);
            game.Universe = u;
            if (header.MissionId != null)
            {
                game.Mission = MissionCatalog.Get(header.MissionId);
                if (game.Mission != null && body.TryGetValue("tracker", out var tn) && tn is Dictionary<string, object>)
                    game.Tracker = new MissionTracker(game.Mission);
            }

            var r = new Reader(u, game.Tracker, body.TryGetValue("objects", out var ob) ? ob as List<object> : null);
            r.Fill(u, body["universe"] as Dictionary<string, object> ?? throw new FormatException("Сохранение: нет вселенной"));
            if (game.Tracker != null) r.Fill(game.Tracker, (Dictionary<string, object>)body["tracker"]);
            u.AfterLoad();

            // Новые борта (разделение после загрузки) не должны получить id сохранённых — по id игра различает борта.
            int maxId = 0;
            foreach (var v in r.Vessels) maxId = Math.Max(maxId, v.Id);
            NextVesselId = Math.Max(idBefore, Math.Max(maxId + 1, (int)Json.Num(body, "nextVesselId", 0)));
            return game;
        }

        sealed class Reader
        {
            readonly Universe universe;
            readonly MissionTracker tracker;
            readonly List<object> table;
            readonly object[] objects;
            public readonly List<Vessel> Vessels = new List<Vessel>();

            public Reader(Universe u, MissionTracker tr, List<object> table)
            {
                universe = u;
                tracker = tr;
                this.table = table ?? new List<object>();
                objects = new object[this.table.Count];
            }

            /// <summary>
            /// Поля из сохранения поверх объекта. Нет поля в файле (старое сохранение) — остаётся значение конструктора;
            /// лишнее поле (удалённое из кода) — пропускается: старые сохранения читаются, пока смысл полей не менялся.
            /// </summary>
            public void Fill(object o, Dictionary<string, object> d)
            {
                foreach (var f in Fields(o.GetType()))
                    if (d.TryGetValue(f.Name, out var node))
                        f.SetValue(o, Decode(node, f.FieldType));
                // Старое сохранение (до очереди манёвров, 15.10.2026): у борта было одно поле Node, теперь — список Nodes.
                if (o is Vessel v && !d.ContainsKey(nameof(Vessel.Nodes)) && d.TryGetValue("Node", out var old) &&
                    Decode(old, typeof(ManeuverNode)) is ManeuverNode n)
                    v.Node = n;
            }

            object Materialize(int id)
            {
                if (id < 0 || id >= objects.Length) throw new FormatException($"Сохранение: нет объекта #{id}");
                if (objects[id] != null) return objects[id];
                var d = table[id] as Dictionary<string, object> ?? throw new FormatException($"Сохранение: объект #{id} не объект");
                var type = ResolveType(Json.Str(d, "$t"));
                object o;
                if (type == typeof(Vessel))
                {
                    // Борт — через конструктор: он раскладывает массивы под число секций проекта, и поле, которого
                    // в старом файле нет, получит то же значение, что у свежего борта. Проект нужен до конструктора.
                    var design = d.TryGetValue(nameof(Vessel.Design), out var dn) ? (VesselDesign)Decode(dn, typeof(VesselDesign)) : null;
                    if (objects[id] != null) return objects[id];
                    var v = new Vessel(design ?? throw new FormatException($"Сохранение: борт #{id} без проекта"));
                    Vessels.Add(v);
                    o = v;
                }
                else if (type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) != null)
                    o = Activator.CreateInstance(type, true);
                else
                {
                    // Нет конструктора без параметров (автопилоты, DeferredRecovery): все поля всё равно придут из файла.
#pragma warning disable SYSLIB0050
                    o = FormatterServices.GetUninitializedObject(type);
#pragma warning restore SYSLIB0050
                }
                // Регистрация до заполнения: циклы ссылок (борт → цель → борт) замыкаются на уже созданный объект.
                objects[id] = o;
                Fill(o, d);
                return o;
            }

            object Decode(object node, Type t)
            {
                if (node == null)
                    return t.IsValueType && Nullable.GetUnderlyingType(t) == null ? Activator.CreateInstance(t) : null;
                var under = Nullable.GetUnderlyingType(t);
                if (under != null) t = under;
                if (t == typeof(double)) return Number(node);
                if (t == typeof(float)) return (float)Number(node);
                if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte) ||
                    t == typeof(ushort) || t == typeof(uint))
                    return Convert.ChangeType(Number(node), t, CultureInfo.InvariantCulture);
                if (t == typeof(bool)) return node is bool b && b;
                if (t == typeof(string)) return node as string;
                if (t.IsEnum)
                {
                    // Имя, исчезнувшее из перечисления, — значение по умолчанию, а не отказ всей загрузки.
                    try { return Enum.Parse(t, (string)node); }
                    catch (ArgumentException) { return Activator.CreateInstance(t); }
                }
                if (t == typeof(Vector3d))
                {
                    var l = (List<object>)node;
                    return new Vector3d(Number(l[0]), Number(l[1]), Number(l[2]));
                }
                if (t == typeof(QuaternionD))
                {
                    var l = (List<object>)node;
                    return new QuaternionD(Number(l[0]), Number(l[1]), Number(l[2]), Number(l[3]));
                }
                if (node is Dictionary<string, object> d)
                {
                    if (d.TryGetValue("$r", out var rid)) return Materialize((int)Number(rid));
                    if (d.TryGetValue("$body", out var bid))
                        return universe.System.Get((string)bid) ?? throw new FormatException($"Сохранение: нет тела {bid}");
                    if (d.TryGetValue("$site", out var sid)) return SolarSystem.GetSite((string)sid);
                    if (d.TryGetValue("$mission", out var mid)) return MissionCatalog.Get((string)mid);
                    if (d.TryGetValue("$x", out var x))
                        switch ((string)x)
                        {
                            case "universe": return universe;
                            case "system": return universe.System;
                            case "tracker": return tracker;
                        }
                    // Структура на месте: тип — из "$t" (поле может быть объявлено базовым типом или интерфейсом).
                    var st = d.TryGetValue("$t", out var tn) && tn is string tname ? ResolveType(tname) : t;
                    object box = Activator.CreateInstance(st);
                    Fill(box, d);
                    return box;
                }
                var list = node as List<object> ?? throw new FormatException($"Сохранение: {t.Name} ждёт список");
                if (t.IsArray)
                {
                    var et = t.GetElementType();
                    var a = Array.CreateInstance(et, list.Count);
                    for (int i = 0; i < list.Count; i++) a.SetValue(Decode(list[i], et), i);
                    return a;
                }
                if (t.IsGenericType)
                {
                    var g = t.GetGenericTypeDefinition();
                    var args = t.GetGenericArguments();
                    if (g == typeof(List<>))
                    {
                        var l = (IList)Activator.CreateInstance(t, list.Count);
                        foreach (var e in list) l.Add(Decode(e, args[0]));
                        return l;
                    }
                    if (g == typeof(HashSet<>))
                    {
                        var h = Activator.CreateInstance(t);
                        var add = t.GetMethod("Add");
                        foreach (var e in list) add.Invoke(h, new[] { Decode(e, args[0]) });
                        return h;
                    }
                    if (g == typeof(Dictionary<,>))
                    {
                        var m = (IDictionary)Activator.CreateInstance(t);
                        foreach (var e in list)
                        {
                            var kv = (List<object>)e;
                            m[Decode(kv[0], args[0])] = Decode(kv[1], args[1]);
                        }
                        return m;
                    }
                    if (IsValueTuple(t))
                    {
                        var items = new object[args.Length];
                        for (int i = 0; i < args.Length; i++) items[i] = Decode(list[i], args[i]);
                        return Activator.CreateInstance(t, items);
                    }
                }
                throw new FormatException($"Сохранение: не умею читать {t.FullName} из списка");
            }
        }

        /// <summary>Кортеж (Universe.Log, пары «свежих» разделений) — список элементов: тип берётся из объявления поля.</summary>
        static bool IsValueTuple(Type t) =>
            t.IsValueType && t.IsGenericType && t.Namespace == "System" && t.Name.StartsWith("ValueTuple`", StringComparison.Ordinal);

        static Type ResolveType(string name)
        {
            var t = name != null ? CoreAssembly.GetType(name) : null;
            return t ?? throw new FormatException($"Сохранение: тип {name} не найден (переименован?)");
        }

        /// <summary>Число из узла JSON: обычное или строка для NaN/±∞/−0 (см. WriteNumber).</summary>
        static double Number(object node)
        {
            switch (node)
            {
                case double d: return d;
                case long l: return l;
                case string s:
                    switch (s)
                    {
                        case "NaN": return double.NaN;
                        case "Infinity": return double.PositiveInfinity;
                        case "-Infinity": return double.NegativeInfinity;
                        case "-0": return -0.0;
                    }
                    return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
                case bool b: return b ? 1 : 0;
            }
            throw new FormatException($"Сохранение: не число {node}");
        }
    }
}
