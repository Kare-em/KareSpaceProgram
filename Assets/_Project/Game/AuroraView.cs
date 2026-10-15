using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Полярные сияния у тел с магнитным полем (GDD §2.7, §9.5): два овала (север/юг) вертикальных «занавесов» вокруг
    /// магнитных полюсов — Земля, Юпитер, Сатурн, Уран, Нептун, Ганимед. Шейдер — Kare/Aurora HDRP (Settings/Resources).
    /// Овалы — дети сферы тела (BodyRenderer): плавающее начало, сжатие дальней «скорлупы» §2.7 и поворот тела
    /// наследуются даром, своих расчётов положения нет. Сферу ищем лениво по имени — BodyRenderer строит тела в своём
    /// Start, а при перестроении удаляет их (тогда Find найдёт новую и овалы соберутся заново).
    /// Единица меша овала — радиус тела: корень масштабируется на R / SphereRadius (у Земли, Луны, Марса сфера
    /// опущена под рельеф — BodyRenderer.SphereRadius).
    /// Цена: по 2 меша (≈4–9 тыс. вершин) и 2 материала на тело, без выделений в кадре.
    /// </summary>
    public sealed class AuroraView : MonoBehaviour
    {
        public const string ShaderName = "Kare/Aurora HDRP";

        /// <summary>Сегментов по окружности овала. Пара: у Земли овал ≈ 2·10³ км в радиусе — шаг ≈ 20 км, мельче
        /// складок (_Fold.x ≈ 15 км); лучи рисуются шумом во фрагменте и от сетки не зависят.</summary>
        const int Segments = 640;
        /// <summary>Параллельных лент в овале и шаг между ними, град: сияние — не одна дуга, а пачка дуг.</summary>
        const int Curtains = 3;
        const float CurtainSpacing = 1.3f;
        /// <summary>Запас границ меша на складки (доля радиуса тела) — больше суммы амплитуд _Fold.</summary>
        const float BoundsPad = 0.03f;

        /// <summary>Параметры сияния тела. Высоты — доли радиуса тела, яркость — нит нижней кромки.</summary>
        sealed class Spec
        {
            public string Id;
            /// <summary>Северный магнитный полюс (широта, долгота E), град, в осях тела.</summary>
            public float PoleLat, PoleLon;
            /// <summary>Угловой радиус овала от магнитного полюса, град (90° − магнитная широта), север и юг
            /// отдельно: у Урана и Нептуна диполь смещён от центра — овалы разного размера.</summary>
            public float ColatN, ColatS;
            /// <summary>Сдвиг центра овала на ночную сторону, град: у Земли овал ночью на 67°, днём на 75° магнитной
            /// широты (ветер Солнца поджимает дневную сторону). Пара: ColatN = (67 + 75)/2 отсчёта, сдвиг = половина разницы.</summary>
            public float NightShift;
            public float H0, H1;
            public float Nits;
            public Color Low, High;
            /// <summary>Яркость высокого цвета относительно нижней кромки.</summary>
            public float HighShare;
            public Vector4 Profile;
            public Vector4 Fold;
            public float DaySide;
        }

        // Источники: Земля — геомагнитный полюс 80,8° с. ш., 72,6° з. д. (IGRF), овал 67°/75°, кромки 100 и 300+ км,
        // яркое сияние ≈ 100 кРл ≈ 0,02 кд/м² в линии 557,7 нм (берём 0,035 — «сильная буря»: с 400 км ночью при честной
        // экспозиции EV ≈ −3 при 0,02 занавес едва читается, замер 15.10.2026). Юпитер — наклон 9,6° к 200,8° з. д. System III,
        // главный овал ≈ 75° (UV/ИК сильные, в видимом — розово-фиолетовое Hα/H₂). Сатурн — ось поля совпадает с осью
        // вращения, овал ≈ 75°. Уран — 59°, Нептун — 47°, диполи смещены на 0,3 и 0,55 R (долготы полюсов условны).
        // Ганимед — собственное поле, овалы на ≈ ±40° широты (кислород 630 нм), тусклые.
        static readonly Spec[] Specs =
        {
            new Spec { Id = "earth", PoleLat = 80.8f, PoleLon = -72.6f, ColatN = 19, ColatS = 19, NightShift = 4,
                H0 = 95f / 6371, H1 = 330f / 6371, Nits = 0.035f,
                Low = new Color(0.25f, 1f, 0.4f), High = new Color(1f, 0.12f, 0.2f), HighShare = 0.35f,
                Profile = new Vector4(0.04f, 0.3f, 0.72f, 0.22f), Fold = new Vector4(0.003f, 0.018f, 320, 1), DaySide = 0.15f },
            new Spec { Id = "jupiter", PoleLat = 80.4f, PoleLon = 159.2f, ColatN = 16, ColatS = 13, NightShift = 0,
                H0 = 300f / 69911, H1 = 1500f / 69911, Nits = 0.06f,
                Low = new Color(1f, 0.35f, 0.95f), High = new Color(0.55f, 0.3f, 1f), HighShare = 0.5f,
                Profile = new Vector4(0.05f, 0.4f, 0.65f, 0.3f), Fold = new Vector4(0.0015f, 0.005f, 260, 1.5f), DaySide = 0.5f },
            new Spec { Id = "saturn", PoleLat = 90, PoleLon = 0, ColatN = 15, ColatS = 16, NightShift = 1,
                H0 = 600f / 58232, H1 = 1800f / 58232, Nits = 0.012f,
                Low = new Color(1f, 0.3f, 0.55f), High = new Color(0.7f, 0.3f, 1f), HighShare = 0.5f,
                Profile = new Vector4(0.05f, 0.4f, 0.65f, 0.3f), Fold = new Vector4(0.0015f, 0.004f, 220, 1), DaySide = 0.4f },
            new Spec { Id = "uranus", PoleLat = 31, PoleLon = 50, ColatN = 14, ColatS = 26, NightShift = 0,
                H0 = 250f / 25362, H1 = 800f / 25362, Nits = 0.004f,
                Low = new Color(0.85f, 0.4f, 1f), High = new Color(0.5f, 0.4f, 1f), HighShare = 0.5f,
                Profile = new Vector4(0.05f, 0.4f, 0.65f, 0.3f), Fold = new Vector4(0.0015f, 0.004f, 200, 0.8f), DaySide = 0.4f },
            new Spec { Id = "neptune", PoleLat = 43, PoleLon = 72, ColatN = 12, ColatS = 30, NightShift = 0,
                H0 = 250f / 24622, H1 = 800f / 24622, Nits = 0.004f,
                Low = new Color(0.85f, 0.4f, 1f), High = new Color(0.5f, 0.4f, 1f), HighShare = 0.5f,
                Profile = new Vector4(0.05f, 0.4f, 0.65f, 0.3f), Fold = new Vector4(0.0015f, 0.004f, 200, 0.8f), DaySide = 0.4f },
            new Spec { Id = "ganymede", PoleLat = 90, PoleLon = 0, ColatN = 50, ColatS = 50, NightShift = 0,
                H0 = 10f / 2634, H1 = 80f / 2634, Nits = 0.003f,
                Low = new Color(1f, 0.2f, 0.15f), High = new Color(0.8f, 0.1f, 0.25f), HighShare = 0.4f,
                Profile = new Vector4(0.06f, 0.5f, 0.6f, 0.3f), Fold = new Vector4(0.003f, 0.008f, 160, 0.6f), DaySide = 0.6f },
        };

        sealed class Oval
        {
            public Transform Tr;
            public Material Mat;
            public Mesh Mesh;
            /// <summary>Магнитная ось этого полюса в осях тела и поворот меша (+Y) на неё.</summary>
            public Vector3 Axis;
            public Quaternion Base;
            public float Shift;
        }

        sealed class Entry
        {
            public CelestialBody Body;
            public Spec Spec;
            public Transform Sphere, Root;
            public Oval North, South;
        }

        static readonly int IdSun = Shader.PropertyToID("_SunDirOS");
        readonly List<Entry> entries = new List<Entry>();
        Shader shader;

        void Start()
        {
            shader = Shader.Find(ShaderName);
            if (shader == null) { Debug.LogWarning($"[Aurora] нет шейдера {ShaderName}"); enabled = false; return; }
            var u = GameBootstrap.U;
            if (u == null) { enabled = false; return; }
            foreach (var s in Specs)
            {
                var b = u.System.Get(s.Id);
                if (b != null) entries.Add(new Entry { Body = b, Spec = s });
            }
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            var br = BodyRenderer.Instance;
            if (u?.Active == null || br == null) return;
            var sun = u.System.Sun;
            foreach (var e in entries)
            {
                if (e.Sphere == null && !Attach(e, br.transform)) continue;
                var b = e.Body;
                // Масштаб сферы = SphereRadius·k; тот же k — из Project (порядок с BodyRenderer.LateUpdate не важен:
                // нужно лишь отношение R / SphereRadius).
                BodyRenderer.Project(b, out double k);
                float sphereScale = e.Sphere.localScale.x;
                if (sphereScale <= 0) continue;
                e.Root.localScale = Vector3.one * (float)(b.Radius * k / sphereScale);
                var toSun = sun.Position - b.Position;
                var sunB = Quaternion.Inverse(FloatingOrigin.BodyRotation(b)) * FloatingOrigin.DirToUnity(toSun / toSun.magnitude);
                Orient(e.North, sunB);
                Orient(e.South, sunB);
            }
        }

        /// <summary>Центр овала сдвигается от магнитного полюса к ночной стороне на Shift (поворот меньшим углом —
        /// без закрутки вокруг оси, узор не ползёт), Солнце передаётся шейдеру в осях меша.</summary>
        static void Orient(Oval o, Vector3 sunB)
        {
            var rot = o.Base;
            if (o.Shift > 0)
            {
                var anti = -sunB;
                var perp = anti - o.Axis * Vector3.Dot(anti, o.Axis);
                if (perp.sqrMagnitude > 1e-6f)
                {
                    var m = Vector3.RotateTowards(o.Axis, perp.normalized, o.Shift * Mathf.Deg2Rad, 0);
                    rot = Quaternion.FromToRotation(o.Axis, m) * o.Base;
                }
            }
            o.Tr.localRotation = rot;
            o.Mat.SetVector(IdSun, Quaternion.Inverse(rot) * sunB);
        }

        bool Attach(Entry e, Transform bodies)
        {
            if (e.Root != null) Release(e);
            var sphere = bodies.Find(e.Body.Name);
            if (sphere == null) return false;
            e.Sphere = sphere;
            var root = new GameObject("Aurora");
            root.layer = sphere.gameObject.layer;
            e.Root = root.transform;
            e.Root.SetParent(sphere, false);
            var s = e.Spec;
            var axis = FloatingOrigin.ToVector3(CelestialBody.LatLonToBodyFixed(s.PoleLat, s.PoleLon).SwapYZ).normalized;
            e.North = MakeOval(e, "North", axis, s.ColatN);
            e.South = MakeOval(e, "South", -axis, s.ColatS);
            return true;
        }

        Oval MakeOval(Entry e, string name, Vector3 axis, float colat)
        {
            var s = e.Spec;
            var go = new GameObject(name);
            go.layer = e.Root.gameObject.layer;
            go.transform.SetParent(e.Root, false);
            var o = new Oval { Tr = go.transform, Axis = axis, Base = Quaternion.FromToRotation(Vector3.up, axis), Shift = s.NightShift };
            o.Mesh = Ring(colat, s.H0, s.H1);
            go.AddComponent<MeshFilter>().sharedMesh = o.Mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            o.Mat = new Material(shader) { name = $"Aurora {e.Body.Id} {name}", renderQueue = 3000 };
            // SetVector, а не SetColor: SetColor в линейном проекте переводит цвет из sRGB, и нити искажаются
            // (0,02 → 0,0015) — яркость задана в физических единицах, перевод не нужен.
            o.Mat.SetVector("_ColorLow", (Vector4)(s.Low * s.Nits));
            o.Mat.SetVector("_ColorHigh", (Vector4)(s.High * (s.Nits * s.HighShare)));
            o.Mat.SetVector("_Profile", s.Profile);
            o.Mat.SetVector("_Fold", s.Fold);
            o.Mat.SetFloat("_DaySide", s.DaySide);
            mr.sharedMaterial = o.Mat;
            Orient(o, Vector3.right);
            return o;
        }

        /// <summary>Кольцо лент вокруг +Y: каждая лента — вертикальный лист от H0 до H1 над сферой радиуса 1,
        /// нормаль — по меридиану (поперёк листа, вдоль неё шейдер двигает складки).</summary>
        static Mesh Ring(float colatDeg, float h0, float h1)
        {
            int perRow = Segments + 1;
            var v = new Vector3[Curtains * perRow * 2];
            var n = new Vector3[v.Length];
            var uv = new Vector2[v.Length];
            var uv2 = new Vector2[v.Length];
            var tri = new int[Curtains * Segments * 6];
            int ti = 0;
            for (int c = 0; c < Curtains; c++)
            {
                float th = (colatDeg + (c - (Curtains - 1) * 0.5f) * CurtainSpacing) * Mathf.Deg2Rad;
                float st = Mathf.Sin(th), ct = Mathf.Cos(th);
                int b0 = c * perRow * 2;
                for (int i = 0; i <= Segments; i++)
                {
                    float f = (float)i / Segments, phi = f * Mathf.PI * 2;
                    float cp = Mathf.Cos(phi), sp = Mathf.Sin(phi);
                    var dir = new Vector3(st * cp, ct, st * sp);
                    var nrm = new Vector3(ct * cp, -st, ct * sp);
                    int a = b0 + i * 2;
                    v[a] = dir * (1 + h0); v[a + 1] = dir * (1 + h1);
                    n[a] = n[a + 1] = nrm;
                    uv[a] = new Vector2(f, 0); uv[a + 1] = new Vector2(f, 1);
                    uv2[a] = uv2[a + 1] = new Vector2(c, 0);
                    if (i == Segments) continue;
                    tri[ti++] = a; tri[ti++] = a + 1; tri[ti++] = a + 3;
                    tri[ti++] = a; tri[ti++] = a + 3; tri[ti++] = a + 2;
                }
            }
            var m = new Mesh { name = "Aurora Ring" };
            m.vertices = v; m.normals = n; m.uv = uv; m.uv2 = uv2; m.triangles = tri;
            m.RecalculateBounds();
            var bb = m.bounds;
            bb.Expand(BoundsPad * 2);
            m.bounds = bb;
            return m;
        }

        static void Release(Entry e)
        {
            foreach (var o in new[] { e.North, e.South })
            {
                if (o == null) continue;
                if (o.Mat != null) Destroy(o.Mat);
                if (o.Mesh != null) Destroy(o.Mesh);
            }
            if (e.Root != null) Destroy(e.Root.gameObject);
            e.Root = null; e.North = e.South = null;
        }

        void OnDestroy()
        {
            foreach (var e in entries) Release(e);
        }
    }
}
