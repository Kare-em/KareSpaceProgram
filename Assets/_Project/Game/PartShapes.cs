using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Процедурная геометрия деталей конструктора (§5.4): одна на предпросмотр ангара (HangarController.Part) и на
    /// запекание иконок (PartIconBaker, меню Kare/Bake Part Icons) — иконка обязана выглядеть как деталь в сборке.
    /// Куски отдаются в Sink в осях детали: +Y — нос, начало — низ детали.
    /// </summary>
    public static class PartShapes
    {
        /// <summary>Приёмник куска: имя, меш, цвет (_BaseColor), положение, поворот, масштаб — в осях детали.</summary>
        public delegate void Sink(string name, Mesh mesh, Color color, Vector3 pos, Quaternion rot, Vector3 scale);

        public static readonly Color NozzleColor = new Color(0.2f, 0.18f, 0.17f);
        public static readonly Color MarkColor = new Color(0.9f, 0.75f, 0.2f);
        public static readonly Color MetalColor = new Color(0.55f, 0.55f, 0.57f);
        public static readonly Color DarkColor = new Color(0.1f, 0.1f, 0.11f);
        public static readonly Color PanelColor = new Color(0.12f, 0.16f, 0.3f);
        public static readonly Color CanopyColor = new Color(0.95f, 0.45f, 0.12f);

        public static Color ColorOf(PartDef p)
        {
            switch (p.Category)
            {
                case PartCategory.Command: return new Color(0.3f, 0.29f, 0.27f);
                case PartCategory.Engine: return p.Propellant > 0 ? new Color(0.85f, 0.85f, 0.82f) : new Color(0.25f, 0.24f, 0.23f);
                case PartCategory.Coupling: return new Color(0.12f, 0.12f, 0.12f);
                case PartCategory.Payload: return new Color(0.8f, 0.62f, 0.28f);
                case PartCategory.Utility: return new Color(0.5f, 0.5f, 0.5f);
                default: return new Color(0.88f, 0.88f, 0.85f);
            }
        }

        static void Put(Sink add, string name, Mesh m, Color c, Vector3 pos) => add(name, m, c, pos, Quaternion.identity, Vector3.one);

        /// <summary>Как в предпросмотре ангара: навесные (длина 0) — кольцо-метка, остальное — по размерам детали.</summary>
        public static void Preview(PartDef p, Sink add)
        {
            float r0 = (float)p.Diameter * 0.5f, r1 = (float)p.Top * 0.5f, len = (float)p.Length;
            var col = ColorOf(p);
            if (len <= 0.01f)
            {
                // Навесные детали (опоры, RCS, стабилизаторы) места в стеке не занимают — метка-кольцо по месту.
                Put(add, "Mark", ProcMesh.Frustum(r0 + 0.08f, r0 + 0.08f, 0.12f, 24, true), MarkColor, Vector3.zero);
                return;
            }
            if (p.HasEngine && p.Propellant <= 0)
            {
                // Двигатель: рама сверху, сопла снизу; связка — кольцом (+ центральное от 5 штук), как в полёте.
                float frame = len * 0.3f, bellH = len - frame;
                Put(add, "Frame", ProcMesh.Frustum(r0 * 0.85f, r1, frame, 24, true), col, Vector3.up * bellH);
                int n = p.EngineCount, ring = n >= 5 ? n - 1 : n == 1 ? 0 : n;
                float rr = ring > 0 ? r0 * 0.62f : 0;
                float nr = ring > 0 ? Mathf.Min(rr * Mathf.Sin(Mathf.PI / Mathf.Max(ring, 2)) * 0.95f, r0 * 0.35f) : r0 * 0.9f;
                for (int k = 0; k < n; k++)
                {
                    float a = 2 * Mathf.PI * k / Mathf.Max(ring, 1);
                    Put(add, "Bell", ProcMesh.Bell(nr, nr * 0.4f, bellH, 16), NozzleColor,
                        k < ring ? new Vector3(rr * Mathf.Cos(a), 0, rr * Mathf.Sin(a)) : Vector3.zero);
                }
                return;
            }
            if (p.HasEngine)
            {
                // РДТТ: корпус с топливом и короткое сопло.
                float noz = Mathf.Min(len * 0.1f, r0 * 1.2f);
                Put(add, "Bell", ProcMesh.Bell(r0 * 0.6f, r0 * 0.3f, noz, 16), NozzleColor, Vector3.zero);
                Put(add, "Case", ProcMesh.Frustum(r0, r1, len - noz, 24, true), col, Vector3.up * noz);
                return;
            }
            Mesh mesh;
            if (p.Sphere) mesh = ProcMesh.Sphere(r0, len * 0.5f, 24, 12);
            else if (p.NoseCone) mesh = ProcMesh.Frustum(r0, r0 * 0.12f, len, 24, true);
            else if (p.Category == PartCategory.Command) mesh = ProcMesh.Frustum(r0, Mathf.Max(r1 * 0.35f, 0.2f), len, 24, true);
            else mesh = ProcMesh.Frustum(r0, r1, len, 24, true);
            Put(add, "Body", mesh, col, Vector3.zero);
        }

        // ---------------------------------------------------------------- иконки

        /// <summary>Обтекатель на иконке — полная оболочка длиной FairingIconLength диаметров (в стеке длину задаёт груз).</summary>
        const float FairingIconLength = 2.2f;
        /// <summary>Стойка шасси/опоры на иконке — доля GearHeight под колесо (остальное — колесо и узел крепления).</summary>
        const float WheelShare = 0.22f;

        /// <summary>
        /// Иконка: то же, что предпросмотр, но навесные детали — узнаваемой формой, а не кольцом-меткой (в стеке их не
        /// видно, а в каталоге надо отличить RCS от антенны). Растянутые размеры — из p (передавать PartCatalog.Resolve).
        /// </summary>
        public static void Icon(PartDef p, Sink add)
        {
            float r = (float)p.Diameter * 0.5f;
            var col = ColorOf(p);
            if (p.Wing != null)
            {
                // Плоскость по тем же WingDef, что считает аэродинамика: корень 1/4 хорды на 0,75 хорды, как CraftCompiler.AddWings.
                var w = p.Wing.Clone();
                w.Height = 0.75 * p.Chord;
                w.Offset = 0;
                var m = WingMesh.Build(w);
                if (m != null) Put(add, "Wing", m, ColorOf(p), Vector3.zero);
                return;
            }
            if (p.Fairing)
            {
                Put(add, "Fairing", ProcMesh.Fairing(r, 2 * r * FairingIconLength, 32), col, Vector3.zero);
                return;
            }
            if (p.ParachuteArea > 0 && p.Category == PartCategory.Utility)
            {
                // Парашютный контейнер и над ним купол — иначе это просто серый цилиндр.
                float len = Mathf.Max((float)p.Length, 0.2f);
                Put(add, "Case", ProcMesh.Frustum(r, r * 0.8f, len, 24, true), col, Vector3.zero);
                add("Canopy", ProcMesh.Dome(60, 24, 6), CanopyColor, Vector3.up * (len + r * 1.4f), Quaternion.identity, Vector3.one * r * 1.6f);
                return;
            }
            if (p.GearHeight > 0)
            {
                // Стойка шасси: узел, амортстойка, колесо (ось колеса — по Z).
                float h = (float)p.GearHeight, wr = h * WheelShare;
                add("Mount", ProcMesh.Box(), MetalColor, new Vector3(0, h + 0.1f, 0), Quaternion.identity, new Vector3(0.5f, 0.2f, 0.5f));
                Put(add, "Strut", ProcMesh.Frustum(0.09f, 0.11f, h - wr, 12, true), MetalColor, Vector3.up * wr);
                for (int side = -1; side <= 1; side += 2)
                    add("Wheel", ProcMesh.Frustum(wr, wr, 0.22f, 20, true), DarkColor, new Vector3(0, wr, side * 0.13f),
                        Quaternion.Euler(side < 0 ? 90 : -90, 0, 0), Vector3.one);
                return;
            }
            if (p.Length > 0.01f) { Preview(p, add); return; }

            switch (p.Id)
            {
                case "legs":
                {
                    // Опора: подкос под углом наружу и тарель — запасная форма, если нет Lander_Leg.fbx.
                    var q = Quaternion.Euler(0, 0, -25);
                    add("Strut", ProcMesh.Frustum(0.07f, 0.07f, 2f, 12, true), MetalColor, Vector3.zero, q, Vector3.one);
                    var foot = q * Vector3.up * 2f;
                    add("Pad", ProcMesh.Frustum(0.35f, 0.3f, 0.08f, 20, true), MetalColor, -foot, Quaternion.identity, Vector3.one);
                    add("Strut2", ProcMesh.Frustum(0.07f, 0.07f, 2f, 12, true), MetalColor, -foot, q, Vector3.one);
                    return;
                }
                case "rcs":
                {
                    // Блок с четырьмя соплами в стороны — как квадрант RCS у «Аполлона».
                    add("Block", ProcMesh.Box(), MetalColor, Vector3.zero, Quaternion.identity, new Vector3(0.5f, 0.6f, 0.5f));
                    var dirs = new[] { Vector3.up, Vector3.down, Vector3.left, Vector3.forward };
                    foreach (var d in dirs)
                        add("Nozzle", ProcMesh.Bell(0.1f, 0.04f, 0.22f, 12), NozzleColor, d * 0.27f,
                            Quaternion.FromToRotation(Vector3.down, d), Vector3.one);
                    return;
                }
                case "solar":
                {
                    // Две створки панелей по бокам корпуса-штанги.
                    Put(add, "Mast", ProcMesh.Frustum(0.12f, 0.12f, 0.6f, 12, true), MetalColor, Vector3.zero);
                    for (int side = -1; side <= 1; side += 2)
                        add("Panel", ProcMesh.Box(), PanelColor, new Vector3(0, 0.3f, side * 1.45f), Quaternion.identity, new Vector3(0.04f, 1.2f, 2.5f));
                    return;
                }
                case "antenna":
                {
                    // Тарелка на мачте, чашей к камере-изометрии (наклон 45°).
                    Put(add, "Mast", ProcMesh.Frustum(0.06f, 0.06f, 0.9f, 10, true), MetalColor, Vector3.zero);
                    add("Dish", ProcMesh.Dome(55, 24, 6), new Color(0.85f, 0.85f, 0.82f), Vector3.up * 0.9f,
                        Quaternion.Euler(0, 0, 45) * Quaternion.Euler(180, 0, 0), Vector3.one * 0.7f);
                    return;
                }
                case "rtg":
                {
                    // Цилиндр с радиальными рёбрами-радиаторами (SNAP-27, MMRTG).
                    Put(add, "Core", ProcMesh.Frustum(0.25f, 0.25f, 1f, 16, true), DarkColor, Vector3.zero);
                    for (int k = 0; k < 6; k++)
                        add("Fin", ProcMesh.Box(), DarkColor, Quaternion.Euler(0, 60 * k, 0) * new Vector3(0.4f, 0.5f, 0),
                            Quaternion.Euler(0, 60 * k, 0), new Vector3(0.35f, 0.9f, 0.03f));
                    return;
                }
                case "fins":
                {
                    // Четыре трапеции-стабилизатора вокруг кольца — запасная форма, если нет Sounding_Fins.fbx.
                    Put(add, "Ring", ProcMesh.Frustum(r, r, 0.3f, 24, true), col, Vector3.zero);
                    for (int k = 0; k < 4; k++)
                    {
                        var q = Quaternion.Euler(0, 90 * k + 45, 0);
                        add("Fin", ProcMesh.Box(), col, q * new Vector3(r + 0.4f, 0.5f, 0), q, new Vector3(0.8f, 1f, 0.05f));
                    }
                    return;
                }
                case "dec-radial":
                {
                    // Пилон с пироболтом: короткая балка и два фланца.
                    add("Beam", ProcMesh.Box(), DarkColor, new Vector3(0.3f, 0, 0), Quaternion.identity, new Vector3(0.6f, 0.25f, 0.25f));
                    add("Flange", ProcMesh.Box(), MetalColor, Vector3.zero, Quaternion.identity, new Vector3(0.06f, 0.5f, 0.5f));
                    add("Flange", ProcMesh.Box(), MetalColor, new Vector3(0.6f, 0, 0), Quaternion.identity, new Vector3(0.06f, 0.5f, 0.5f));
                    return;
                }
            }
            Preview(p, add);
        }
    }
}
