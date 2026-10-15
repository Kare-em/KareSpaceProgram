using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Дымный шлейф и облако у стола на взлёте (GDD §9.5). Точки дыма живут в осях тела и в двойной точности:
    /// дым стоит в воздухе и вращается с планетой, а не тянется за бортом; в Unity переводятся каждый кадр
    /// через плавающее начало.
    /// Материал — HDRP/Lit прозрачный: дым освещён Солнцем и факелом, ночью тёмный. У Lit нет цвета вершин,
    /// поэтому плотность вдоль шлейфа задаётся шириной, а возраст — координатой U: точки берутся раз в
    /// SampleInterval, и U = номер / (N − 1) — это доля жизни, по ней текстура гасит хвост.
    /// Лента своя, а не LineRenderer: у того ширина задаётся кривой по длине, а нам нужна по точкам.
    /// Объём (15.10.2026): лента из Across столбцов поперёк с «цилиндрическими» нормалями — Lit освещает её как
    /// круглый столб (сторона к Солнцу светлая, теневая — тёмная), а не как плоскую полосу; края рвутся несимметричными
    /// комками (Sample.LumpL/LumpR); в разреженном воздухе дым расплывается быстрее (Sample.Spread). Клубы у стола —
    /// сетка с нормалями полусферы и случайным поворотом, а не плоский квадрат.
    /// Облако газа и пыли у грунта (старт, посадка) — ExhaustTrail.Ground.cs.
    /// </summary>
    public sealed partial class ExhaustTrail : MonoBehaviour
    {
        /// <summary>Точек в шлейфе и шаг выборки, сим-с. Пара: Points·SampleInterval = время жизни — 90 с
        /// хватает от стола до ≈ 35 км, где дым кончается (DensityEnd).</summary>
        const int Points = 180;
        const double SampleInterval = 0.5;
        /// <summary>Плотность воздуха, кг/м³: ниже DensityEnd (≈ 40 км) дыма нет, с DensityFull (≈ 25 км) — в полную силу.</summary>
        const double DensityEnd = 0.003, DensityFull = 0.05;
        /// <summary>Ширина дыма у среза — калибров слитой струи (VesselView.ExhaustSource, √Σr²: у Р-7 ≈ 2,6 м → 16 м);
        /// расплывание, м/с; в вакууме струя шире. SpreadRate 1,5, а не 0,6: дымовой столб у стола за 10 с раздаётся
        /// на десятки метров, при 0,6 он стоял ровной трубой (15.10). Пара: ThinSpread множит именно его.</summary>
        const float WidthCalibers = 3f, SpreadRate = 1.5f, VacuumWidening = 2f;
        /// <summary>Дым выходит из-за ядра факела: до этой длины в радиусах одного факела ширина растёт от нуля —
        /// по корню, конусом, а не линейно: линейный разгон давал иглу под факелом. Пара: VesselView.PlumeLengthSL = 12 —
        /// дым начинается за видимым ядром.</summary>
        const float EmergeCalibers = 20f;
        const float TrailAlpha = 0.85f;
        /// <summary>Текстура клуба в долях полуразмера квадрата: окно гасит альфу в 0 между Start и End (&lt; 1 — не доходит
        /// до края квадрата), рваность края шумом. Пара: PuffLift поднимает центр на PuffLift·размер, чтобы низ клуба не
        /// резал грунт прямой линией.</summary>
        const float PuffEdgeStart = 0.6f, PuffEdgeEnd = 0.88f, PuffRagged = 0.5f, PuffLift = 0.6f;
        /// <summary>Мягкое подхождение камеры: клуб тает на расстоянии от PuffFadeNear до PuffFadeNear + PuffFadeRange
        /// его размеров. Замена soft-particles — у HDRP/Lit их нет, а входящая в клуб камера давала плоский экран-вуаль.</summary>
        const float PuffFadeNear = 0.6f, PuffFadeRange = 1.2f;
        static readonly Color SmokeColor = new Color(0.86f, 0.85f, 0.83f);
        /// <summary>Вершин поперёк ленты (нечётное — есть ось столба). Пара: TrailTexture читает V как −1…1 поперёк.</summary>
        const int Across = 5;
        /// <summary>Наклон нормали к краю столба: 1 — нормаль на краю ровно вбок (резкий терминатор), меньше — мягче.</summary>
        const float RoundNormals = 0.8f;
        /// <summary>Комки: ширина каждого края точки ×(1 ± Lumpiness/2), независимо слева и справа — столб извивается.</summary>
        const float Lumpiness = 0.55f;
        /// <summary>Расплывание в разреженном воздухе: SpreadRate × (1 + ThinSpread·(1 − ρ/ρ₀)) — на 30 км дым
        /// расползается в несколько раз быстрее (на снимках стартов шлейф выше 20 км — широкие ленты). Пара: SpreadRate.</summary>
        const float ThinSpread = 5f;
        const double SeaLevelDensity = 1.225;
        /// <summary>Сетка клуба (вершин на сторону) и медленное вращение клуба, рад/с — неподвижный узор выдаёт спрайт.</summary>
        const int PuffGrid = 6;
        const float PuffSpin = 0.05f;

        struct Sample { public Vector3d Bf; public double Time; public float Width, Spread, LumpL, LumpR; }

        VesselView view;
        CelestialBody body;
        readonly Sample[] samples = new Sample[Points];
        int newest = -1, count;
        double lastSample = double.NegativeInfinity;
        /// <summary>Цвет пыли — средний тон грунта тела (BodyVisuals), пересчитывается при смене тела.</summary>
        Color dustColor;
        float lastRadius = 1;
        Vector3d lastSrc;

        Mesh mesh;
        MeshRenderer ribbon;
        /// <summary>Рядов ленты: срез, точка выхода дыма (EmergeCalibers за срезом), затем выборки. Отдельный ряд выхода —
        /// потому что между срезом и свежей выборкой на 430 м/с лежит 215 м (полшага SampleInterval), и линейная
        /// интерполяция ширины от нуля на срезе растягивала дым в иглу на всю эту длину (замечание 15.10).</summary>
        const int Rows = Points + 2;
        readonly Vector3[] pts = new Vector3[Rows];
        readonly float[] widths = new float[Rows];
        readonly float[] lumpL = new float[Rows], lumpR = new float[Rows];
        readonly Vector3[] verts = new Vector3[Across * Rows];
        readonly Vector3[] normals = new Vector3[Across * Rows];

        MaterialPropertyBlock mpb;
        System.Random rng = new System.Random(1957);

        public void Init(VesselView v, Material smoke)
        {
            view = v;
            mpb = new MaterialPropertyBlock();
            var trailMat = new Material(smoke) { name = "Smoke Trail (runtime)" };
            trailMat.SetTexture("_BaseColorMap", TrailTexture());
            trailMat.SetColor("_BaseColor", new Color(SmokeColor.r, SmokeColor.g, SmokeColor.b, TrailAlpha));

            mesh = new Mesh { name = "Smoke Ribbon" };
            mesh.MarkDynamic();
            var uv = new Vector2[verts.Length];
            var tris = new int[6 * (Rows - 1) * (Across - 1)];
            for (int i = 0, t = 0; i < Rows; i++)
            {
                float u = i / (Rows - 1f);
                for (int c = 0; c < Across; c++) uv[Across * i + c] = new Vector2(u, c / (Across - 1f));
                if (i == Rows - 1) continue;
                for (int c = 0; c < Across - 1; c++, t += 6)
                {
                    int a = Across * i + c, b = a + Across;
                    tris[t] = a; tris[t + 1] = a + 1; tris[t + 2] = b;
                    tris[t + 3] = a + 1; tris[t + 4] = b + 1; tris[t + 5] = b;
                }
            }
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.triangles = tris;
            var go = new GameObject("Ribbon");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            ribbon = go.AddComponent<MeshRenderer>();
            ribbon.sharedMaterial = trailMat;
            ribbon.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ribbon.enabled = false;
            // Пул облака у грунта — лениво, при первом клубе (EnsureGroundPool): большинство бортов грунта не касается.
            smokeSource = smoke;
        }

        Sample At(int age) => samples[(newest - age + Points) % Points];

        void LateUpdate()
        {
            if (view == null) { Destroy(gameObject); return; }
            var u = GameBootstrap.U;
            var v = view.Vessel;
            if (u == null || v == null) return;
            if (v.Body != body)
            {
                // Смена сферы влияния: точки в осях старого тела смысла не имеют.
                body = v.Body;
                var look = BodyVisuals.Get(body.Id);
                dustColor = Color.Lerp(look.Low, look.High, 0.5f);
                soilColor = look.High;
                count = 0; newest = -1;
                ResetGround();
            }
            double now = u.Time;
            var q = body.Orientation;
            var qi = q.Inverse;

            bool burning = view.ExhaustSource(out var src, out float radius, out float nozzleR, out float thr);
            float w0 = 0;
            float thin = 1 - Mathf.Clamp01((float)(v.Density / SeaLevelDensity));
            Vector3d srcBf = lastSrc, upBf = (qi * v.Position).normalized;
            if (burning)
            {
                // Смещение среза от ЦМ — из Unity (метры, float хватает), остальное в двойной точности.
                var off = src - FloatingOrigin.ToUnity(FloatingOrigin.WorldP(v));
                var offBf = qi * new Vector3d(off.x, off.z, off.y);
                srcBf = qi * v.Position + offBf;
                double s = System.Math.Max(0, System.Math.Min(1, (v.Density - DensityEnd) / (DensityFull - DensityEnd)));
                float vac = 1 - Mathf.Clamp01((float)(v.StaticPressure / 101325));
                w0 = (float)System.Math.Sqrt(s) * radius * 2 * WidthCalibers * (0.5f + 0.5f * thr) * (1 + vac * VacuumWidening);
                lastSrc = srcBf;
                lastRadius = nozzleR;
                // Облако у грунта: в воздухе — газ с паром валом, без воздуха (и на Марсе) — пыль веером.
                if (body.Terrain != null && !body.IsGasGiant) SpawnGround(v, now, srcBf, upBf, Vector3d.Dot(offBf, upBf));
            }
            // Выключенный двигатель кладёт точки нулевой ширины в последнее место — хвост стареет дальше.
            if ((burning || count > 0) && (now - lastSample >= SampleInterval || now < lastSample))
            {
                lastSample = now;
                newest = (newest + 1) % Points;
                samples[newest] = new Sample
                {
                    Bf = srcBf, Time = now, Width = w0, Spread = SpreadRate * (1 + ThinSpread * thin),
                    LumpL = 1 + Lumpiness * ((float)rng.NextDouble() - 0.5f),
                    LumpR = 1 + Lumpiness * ((float)rng.NextDouble() - 0.5f),
                };
                count = System.Math.Min(count + 1, Points);
            }

            bool show = !MapView.IsOpen;
            UpdateRibbon(show && count > 0, now, q, srcBf);
            UpdateGround(show, now, q);
        }

        void UpdateRibbon(bool show, double now, QuaternionD q, Vector3d headBf)
        {
            bool any = false;
            for (int i = 0; i < count; i++) if (samples[i].Width > 0) { any = true; break; }
            ribbon.enabled = show && any;
            if (!ribbon.enabled) return;

            int n = Rows;
            var tail = At(count - 1).Bf;
            for (int i = 0; i < n; i++)
            {
                Vector3d bf; float w = 0, ll = 1, lr = 1;
                if (i <= 1) bf = headBf;
                else if (i - 2 < count)
                {
                    var sm = At(i - 2);
                    bf = sm.Bf;
                    if (sm.Width > 0) w = sm.Width + sm.Spread * (float)(now - sm.Time);
                    ll = sm.LumpL; lr = sm.LumpR;
                }
                else bf = tail;
                pts[i] = FloatingOrigin.ToUnity(body.Position + q * bf);
                widths[i] = w;
                lumpL[i] = ll; lumpR[i] = lr;
            }
            // Дым выходит из-за факела: ширина нарастает по пути от среза, а не по времени — на 1 км/с
            // «секунда» растянула бы тонкий хвост на километр.
            float emerge = lastRadius * EmergeCalibers, path = 0;
            // Ряд выхода — на пути к свежей выборке, с её шириной: дальше дым идёт полной шириной, а не клином.
            var fresh = pts[2] - pts[0];
            float freshLen = fresh.magnitude;
            pts[1] = freshLen > 1e-3f ? pts[0] + fresh * (Mathf.Min(emerge, freshLen * 0.95f) / freshLen) : pts[0];
            widths[1] = widths[2];
            lumpL[1] = lumpL[2]; lumpR[1] = lumpR[2];
            var cam = Camera.main;
            var eye = cam != null ? cam.transform.position : Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) path += Vector3.Distance(pts[i], pts[i - 1]);
                float w = widths[i] * Mathf.Sqrt(Mathf.Clamp01(path / emerge)) * 0.5f;
                var tangent = pts[Mathf.Min(i + 1, n - 1)] - pts[Mathf.Max(i - 1, 0)];
                var toCam = eye - pts[i];
                var side = Vector3.Cross(tangent, toCam);
                side = side.sqrMagnitude > 1e-12f ? side.normalized : Vector3.zero;
                var face = toCam.sqrMagnitude > 1e-12f ? toCam.normalized : Vector3.up;
                for (int c = 0; c < Across; c++)
                {
                    // s — поперёк, −1…1; нормаль — как у круглого столба в этой точке силуэта.
                    float s = c / (Across - 1f) * 2 - 1;
                    float lump = s < 0 ? lumpL[i] : lumpR[i];
                    verts[Across * i + c] = pts[i] + side * (w * s * lump);
                    float k = s * RoundNormals;
                    normals[Across * i + c] = face * Mathf.Sqrt(1 - k * k) + side * k;
                }
            }
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.RecalculateBounds();
        }

        /// <summary>Лента: U — доля жизни (хвост гаснет в ноль), V — поперёк (мягкий край), клочья — шум Перлина.</summary>
        static Texture2D trailTex, puffTex;
        static Texture2D TrailTexture()
        {
            if (trailTex != null) return trailTex;
            // Клочья: край сдвигается шумом (рваный силуэт, а не ровный гауссов край), к хвосту дым рвётся на клубы —
            // порог по шуму растёт с возрастом. Два октава: крупные клубы и мелкая рябь.
            const int nu = 256, nv = 48;
            trailTex = new Texture2D(nu, nv, TextureFormat.RGBA32, true) { name = "Smoke Trail", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < nv; y++)
            for (int x = 0; x < nu; x++)
            {
                float t = x / (nu - 1f), s = (y + 0.5f) / nv * 2 - 1;
                float big = Mathf.PerlinNoise(x * 0.09f, y * 0.11f + 11), fine = Mathf.PerlinNoise(x * 0.4f + 5, y * 0.35f + 3);
                float noise = 0.7f * big + 0.3f * fine;
                float se = Mathf.Abs(s) * (1 + 0.45f * (noise - 0.5f));
                // Поперёк — плоская вершина (супергаусс s⁴), а не гаусс: с гауссом на сумерках читалась только сердцевина
                // ≈ шестой части ленты, и 40-метровый столб Р-7 выглядел шестиметровым стержнем (замер 15.10).
                float se2 = se * se;
                float across = Mathf.Exp(-se2 * se2 * 3f) * Mathf.Clamp01(1 - se2);
                float along = Mathf.Clamp01((1 - t) * 4);
                float breakup = Mathf.Clamp01((noise - 0.6f * t * t) * 2.2f + 0.25f);
                float a = across * along * (0.55f + 0.7f * noise) * breakup;
                trailTex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(a)));
            }
            trailTex.Apply(true, true);
            return trailTex;
        }

        /// <summary>Клуб — квадрат ±1 (как ProcMesh.Billboard, лицом к камере по −Z) сеткой PuffGrid², нормали — полусфера
        /// к камере: Lit затеняет клуб с теневой стороны, как шар, а не заливает плоско. За кругом r = 1 нормаль ложится
        /// вбок (там альфа уже 0 — PuffEdgeEnd &lt; 1).</summary>
        static Mesh puffMesh;
        static Mesh PuffMesh()
        {
            if (puffMesh != null) return puffMesh;
            int n = PuffGrid;
            var v = new Vector3[n * n];
            var nr = new Vector3[n * n];
            var uv = new Vector2[n * n];
            var tr = new int[6 * (n - 1) * (n - 1)];
            for (int j = 0, k = 0; j < n; j++)
            for (int i = 0; i < n; i++, k++)
            {
                float x = i / (n - 1f) * 2 - 1, y = j / (n - 1f) * 2 - 1;
                v[k] = new Vector3(x, y, 0);
                uv[k] = new Vector2(i / (n - 1f), j / (n - 1f));
                float r2 = Mathf.Min(1, x * x + y * y);
                nr[k] = new Vector3(x, y, -Mathf.Sqrt(1 - r2) - 0.15f).normalized;
            }
            for (int j = 0, t = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++, t += 6)
            {
                int a = j * n + i, b = a + 1, c = a + n + 1, d = a + n;
                tr[t] = a; tr[t + 1] = d; tr[t + 2] = c;
                tr[t + 3] = a; tr[t + 4] = c; tr[t + 5] = b;
            }
            puffMesh = new Mesh { name = "Smoke Puff" };
            puffMesh.vertices = v;
            puffMesh.normals = nr;
            puffMesh.uv = uv;
            puffMesh.triangles = tr;
            puffMesh.RecalculateBounds();
            return puffMesh;
        }

        internal static Texture2D PuffTexture()
        {
            if (puffTex != null) return puffTex;
            const int n = 128;
            puffTex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "Smoke Puff", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                // Клочья: два октава шума сдвигают край, середина плотная.
                float noise = 0.65f * Mathf.PerlinNoise(x * 0.06f + 3, y * 0.06f + 7)
                            + 0.35f * Mathf.PerlinNoise(x * 0.17f + 21, y * 0.17f + 5);
                float rn = r * (1 + PuffRagged * (noise - 0.5f));
                float f = 1 - Mathf.SmoothStep(0.15f, 0.75f, rn);
                // Жёсткое окно: альфа ровно 0 за PuffEdgeEnd, иначе у квадрата виден прямой край (03.10.2026,
                // старая формула давала 0,10 на середине стороны). Пара: край квадрата r = 1, окно кончается раньше.
                float win = 1 - Mathf.SmoothStep(PuffEdgeStart, PuffEdgeEnd, r);
                f *= win;
                puffTex.SetPixel(x, y, new Color(1, 1, 1, f * f * (3 - 2 * f)));
            }
            puffTex.Apply(true, true);
            return puffTex;
        }
    }
}
