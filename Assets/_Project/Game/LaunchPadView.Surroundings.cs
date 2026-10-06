using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Окружение стартового комплекса (GDD §7, §9.4): подъездная дорога из плит, служебные корпуса с ленточным остеклением,
    /// бункер, склад компонентов топлива (баки — по семейству) и кусты по степи. Без него стол стоял островком бетона
    /// на ровной бежевой равнине до горизонта (Play 06.10.2026). Всё статично, в базисе стола (X — восток, Z — север).
    /// Рельеф в Terrain FlatRadius (1500 м) выровнен под отметку космодрома — основания на y = 0, фундамент под грунт.
    /// </summary>
    public sealed partial class LaunchPadView
    {
        /// <summary>Подъездная дорога на север от края апрона: длина, полуширина, верх над грунтом, м
        /// (выше апрона 0,02 — на стыке не мерцает).</summary>
        const float RoadLength = 900, RoadHalf = 4, RoadTop = 0.04f;
        /// <summary>Фундамент построек под грунт, м: патч рельефа расходится с ядром на дециметры (docs/pitfalls-render.md).</summary>
        const float FoundationDepth = 2;
        /// <summary>Этаж корпуса и лента окон на нём (низ над полом и высота), м.</summary>
        const float FloorHeight = 3.6f, WindowSill = 1.1f, WindowHeight = 1.4f;
        /// <summary>Кусты: радиус россыпи, м, число, ширина min…max, м. Пара: радиус меньше Terrain FlatRadius (1500) —
        /// грунт под ними ровный; 4000 на 0,6 км радиуса — шаг ≈ 17 м, как редкая полынь в степи.</summary>
        const float ScrubRadius = 600, ScrubMin = 0.6f, ScrubMax = 2.4f;
        const int ScrubCount = 4000;
        /// <summary>Зазор кустов от бетона и построек, м.</summary>
        const float ScrubClear = 3;

        /// <summary>Занятые места на грунте (апрон, дорога, корпуса, вторая площадка Starbase): кусты их обходят.</summary>
        readonly List<Rect> occupied = new List<Rect>();

        struct Building { public float X, Z, W, D, H; public int Floors; }

        /// <summary>a — полуширина апрона (до края бетона), м: всё окружение начинается за ним.</summary>
        void Surroundings(Kind kind, float a, Shader shader, Texture2D concrete, Texture2D concreteNormal)
        {
            var roadMat = ConcreteMaterial(shader, concrete, concreteNormal, new Color(0.78f, 0.77f, 0.74f), "Pad Road");
            var wallMat = ConcreteMaterial(shader, concrete, concreteNormal, new Color(0.92f, 0.90f, 0.84f), "Pad Building");
            var roofMat = ConcreteMaterial(shader, concrete, concreteNormal, new Color(0.38f, 0.37f, 0.36f), "Pad Roof");
            var glassMat = new Material(shader) { name = "Pad Glass" };
            glassMat.SetColor("_BaseColor", new Color(0.05f, 0.07f, 0.09f));
            glassMat.SetFloat("_Metallic", 0.2f);
            glassMat.SetFloat("_Smoothness", 0.85f);
            var tankMat = new Material(shader) { name = "Pad Tank" };
            tankMat.SetColor("_BaseColor", new Color(0.86f, 0.86f, 0.84f));
            tankMat.SetFloat("_Metallic", 0.3f);
            tankMat.SetFloat("_Smoothness", 0.45f);

            occupied.Add(Rect.MinMaxRect(-a, -a, a, a));
            // Дорога из бетонных плит на север (у Р-7 лоток на юг — дорога с другой стороны), стыкуется с апроном.
            var road = new MeshBuilder(ConcreteTile);
            road.Box(new Vector3(-RoadHalf, -1, a - 1), new Vector3(RoadHalf, RoadTop, a + RoadLength));
            // Объездная вдоль северного края апрона — к корпусам по обе стороны.
            road.Box(new Vector3(-a - 30, -1, a + 6), new Vector3(a + 40, RoadTop, a + 6 + 2 * RoadHalf));
            occupied.Add(Rect.MinMaxRect(-RoadHalf, a, RoadHalf, a + RoadLength));
            occupied.Add(Rect.MinMaxRect(-a - 30, a + 6, a + 40, a + 6 + 2 * RoadHalf));
            AddPart("Road", road.Build(), roadMat, transform);

            var walls = new MeshBuilder(ConcreteTile);
            var roofs = new MeshBuilder(ConcreteTile);
            var glass = new MeshBuilder(ConcreteTile);
            foreach (var b in Layout(a)) AddBuilding(walls, roofs, glass, b);
            AddPart("Buildings", walls.Build(), wallMat, transform);
            AddPart("Roofs", roofs.Build(), roofMat, transform);
            AddPart("Windows", glass.Build(), glassMat, transform);

            var tanks = new MeshBuilder(ConcreteTile);
            TankFarm(kind, a, tanks);
            AddPart("Tank Farm", tanks.Build(), tankMat, transform);

            Scrub(shader);
        }

        /// <summary>Корпуса вокруг стола, общие для всех комплексов: монтажно-испытательный у дороги, компрессорная,
        /// бункер управления (низкий, без окон), трансформаторные будки по бокам. Расстояния — от края апрона.</summary>
        static IEnumerable<Building> Layout(float a)
        {
            yield return new Building { X = RoadHalf + 32, Z = a + 70, W = 30, D = 50, H = 12, Floors = 2 };
            yield return new Building { X = -(RoadHalf + 24), Z = a + 42, W = 20, D = 14, H = 7.5f, Floors = 2 };
            yield return new Building { X = -(RoadHalf + 26), Z = a + 110, W = 16, D = 34, H = 4.5f, Floors = 1 };
            yield return new Building { X = -(a + 110), Z = -0.3f * a, W = 30, D = 20, H = 4, Floors = 0 };
            yield return new Building { X = a + 35, Z = 0.4f * a, W = 10, D = 8, H = 4, Floors = 0 };
            yield return new Building { X = a + 35, Z = -0.4f * a, W = 10, D = 8, H = 4, Floors = 0 };
        }

        void AddBuilding(MeshBuilder walls, MeshBuilder roofs, MeshBuilder glass, Building b)
        {
            float x0 = b.X - b.W / 2, x1 = b.X + b.W / 2, z0 = b.Z - b.D / 2, z1 = b.Z + b.D / 2;
            walls.Box(new Vector3(x0, -FoundationDepth, z0), new Vector3(x1, b.H, z1));
            // Кровля — с парапетом: плита чуть шире стен и тёмная, корпус не читается белым кубом.
            roofs.Box(new Vector3(x0 - 0.3f, b.H, z0 - 0.3f), new Vector3(x1 + 0.3f, b.H + 0.5f, z1 + 0.3f));
            // Окна — лентой на каждом этаже, на 5 см наружу от стены (не мерцает), с отступом от углов.
            const float Out = 0.05f, Corner = 1.5f;
            for (int f = 0; f < b.Floors; f++)
            {
                float y0 = f * FloorHeight + WindowSill, y1 = y0 + WindowHeight;
                if (y1 > b.H - 0.5f) break;
                glass.Box(new Vector3(x0 + Corner, y0, z0 - Out), new Vector3(x1 - Corner, y1, z0));
                glass.Box(new Vector3(x0 + Corner, y0, z1), new Vector3(x1 - Corner, y1, z1 + Out));
                glass.Box(new Vector3(x0 - Out, y0, z0 + Corner), new Vector3(x0, y1, z1 - Corner));
                glass.Box(new Vector3(x1, y0, z0 + Corner), new Vector3(x1 + Out, y1, z1 - Corner));
            }
            occupied.Add(Rect.MinMaxRect(x0, z0, x1, z1));
        }

        /// <summary>Склад компонентов топлива по семейству: у Starbase — «танковая ферма» вертикальных баков (метан/кислород),
        /// у LC-39 — шары жидкого водорода и кислорода на опорах, у остальных — ряд вертикальных цистерн.</summary>
        void TankFarm(Kind kind, float a, MeshBuilder tanks)
        {
            switch (kind)
            {
                case Kind.Starbase:
                    for (int row = 0; row < 2; row++)
                        for (int k = 0; k < 4; k++)
                            Tank(tanks, new Vector3(20 + 12 * k, 0, -(a + 35) - 14 * row), 4.5f, 24);
                    break;
                case Kind.Saturn:
                    Sphere(tanks, new Vector3(a + 140, 0, a + 60), 10);
                    Sphere(tanks, new Vector3(-(a + 140), 0, a + 60), 10);
                    break;
                default:
                    for (int k = 0; k < 3; k++)
                        Tank(tanks, new Vector3(-(a + 30) - 9 * k, 0, a + 30), 3.2f, 10);
                    break;
            }
        }

        void Tank(MeshBuilder m, Vector3 c, float r, float h)
        {
            m.Cylinder(c + Vector3.down * FoundationDepth, r, h + FoundationDepth, 24);
            occupied.Add(Rect.MinMaxRect(c.x - r, c.z - r, c.x + r, c.z + r));
        }

        /// <summary>Шаровой резервуар на шести опорах: низ шара в 2 м над грунтом.</summary>
        void Sphere(MeshBuilder m, Vector3 c, float r)
        {
            const float Clear = 2, Leg = 0.5f;
            m.Ellipsoid(c + Vector3.up * (r + Clear), Vector3.one * r, 12, 24, 0);
            for (int k = 0; k < 6; k++)
            {
                float ang = k * Mathf.PI / 3;
                var p = c + new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * (r * 0.8f);
                m.Box(new Vector3(p.x - Leg, -FoundationDepth, p.z - Leg), new Vector3(p.x + Leg, r + Clear, p.z + Leg));
            }
            occupied.Add(Rect.MinMaxRect(c.x - r, c.z - r, c.x + r, c.z + r));
        }

        /// <summary>
        /// Кусты по степи: низкие многогранники, утопленные в грунт на треть, три оттенка — одним мешем на оттенок.
        /// Цвет — от биома в точке космодрома (EarthSurface.Sample), сдвинутый к сухой оливе: у Байконура выходит полынь,
        /// у мыса Канаверал — зеленее. Раскладка — от зерна по имени площадки, одинаковая при каждом запуске.
        /// </summary>
        void Scrub(Shader shader)
        {
            if (body.Id != "earth" || vessel.Site == null) return;
            var biome = EarthSurface.Sample(body, vessel.Site.Latitude, vessel.Site.Longitude, out _);
            var olive = Color.Lerp(biome, new Color(0.24f, 0.27f, 0.13f), 0.65f);
            var shades = new[] { 0.62f, 0.8f, 1.0f };
            // Зерно — свой хеш имени: string.GetHashCode в .NET случаен между запусками.
            int seed = 17;
            foreach (char ch in vessel.Site.Id) seed = seed * 31 + ch;
            var rnd = new System.Random(seed);
            var builders = new MeshBuilder[shades.Length];
            for (int i = 0; i < builders.Length; i++) builders[i] = new MeshBuilder(ConcreteTile);
            for (int n = 0; n < ScrubCount; n++)
            {
                // Равномерно по площади круга.
                float r = ScrubRadius * Mathf.Sqrt((float)rnd.NextDouble()), ang = (float)(rnd.NextDouble() * 2 * Mathf.PI);
                var p = new Vector2(r * Mathf.Cos(ang), r * Mathf.Sin(ang));
                if (Blocked(p)) continue;
                float w = Mathf.Lerp(ScrubMin, ScrubMax, Mathf.Pow((float)rnd.NextDouble(), 2));
                float h = w * (0.35f + 0.3f * (float)rnd.NextDouble());
                var radii = new Vector3(w * 0.5f, h, w * (0.35f + 0.15f * (float)rnd.NextDouble()));
                builders[rnd.Next(shades.Length)].Ellipsoid(new Vector3(p.x, h * 0.3f - h * 0.5f, p.y), radii, 3, 6,
                    (float)rnd.NextDouble() * 360);
            }
            for (int i = 0; i < builders.Length; i++)
            {
                var mat = new Material(shader) { name = "Pad Scrub" };
                mat.SetColor("_BaseColor", olive * shades[i]);
                mat.SetFloat("_Smoothness", 0.1f);
                AddPart("Scrub", builders[i].Build(), mat, transform);
            }
        }

        bool Blocked(Vector2 p)
        {
            foreach (var r in occupied)
                if (p.x > r.xMin - ScrubClear && p.x < r.xMax + ScrubClear && p.y > r.yMin - ScrubClear && p.y < r.yMax + ScrubClear)
                    return true;
            return false;
        }

        /// <summary>Бетон с картой нормалей (швы плит, заполнитель — Tools/gen-concrete.py) и оттенком.</summary>
        static Material ConcreteMaterial(Shader shader, Texture2D albedo, Texture2D normal, Color tint, string name)
        {
            var m = new Material(shader) { name = name };
            m.SetTexture("_BaseColorMap", albedo != null ? albedo : Texture2D.grayTexture);
            m.SetColor("_BaseColor", tint);
            m.SetFloat("_Smoothness", 0.2f);
            if (normal != null)
            {
                // Рантайм-материал HDRP не валидируется — ключи ставим сами (CLAUDE.md, соглашения).
                m.SetTexture("_NormalMap", normal);
                m.SetFloat("_NormalScale", 1);
                m.EnableKeyword("_NORMALMAP");
                m.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
            return m;
        }
    }
}
