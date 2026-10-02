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
    /// </summary>
    public sealed class ExhaustTrail : MonoBehaviour
    {
        /// <summary>Точек в шлейфе и шаг выборки, сим-с. Пара: Points·SampleInterval = время жизни — 90 с
        /// хватает от стола до ≈ 35 км, где дым кончается (DensityEnd).</summary>
        const int Points = 180;
        const double SampleInterval = 0.5;
        /// <summary>Плотность воздуха, кг/м³: ниже DensityEnd (≈ 40 км) дыма нет, с DensityFull (≈ 25 км) — в полную силу.</summary>
        const double DensityEnd = 0.003, DensityFull = 0.05;
        /// <summary>Ширина дыма у среза — калибров общего факела; расплывание, м/с; в вакууме струя шире.</summary>
        const float WidthCalibers = 3f, SpreadRate = 0.6f, VacuumWidening = 2f;
        /// <summary>Дым выходит из-за ядра факела: до этой длины в радиусах струи ширина растёт от нуля.
        /// Пара: VesselView.PlumeLengthSL = 12 — дым начинается за видимым ядром.</summary>
        const float EmergeCalibers = 20f;
        /// <summary>Облако у стола: клубы рождаются, пока срез ниже CloudHeight над грунтом, раз в CloudInterval сим-с.
        /// Пара: Puffs ≥ PuffLife / CloudInterval за время подъёма на CloudHeight (≈ 6 с → 24 клуба).</summary>
        const float CloudHeight = 80f;
        const double CloudInterval = 0.25;
        const int Puffs = 28;
        /// <summary>Клуб: полуразмер, м; рост, м/√с; разлёт, м/с (гаснет за PuffDrag с); подъём, м/с; жизнь, с.</summary>
        const float PuffSize = 6f, PuffGrowth = 5f, PuffSpeed = 25f, PuffDrag = 4f, PuffRise = 1.2f, PuffLife = 40f;
        const float TrailAlpha = 0.85f, PuffAlpha = 0.7f;
        static readonly Color SmokeColor = new Color(0.86f, 0.85f, 0.83f);

        struct Sample { public Vector3d Bf; public double Time; public float Width; }

        sealed class Puff
        {
            public Vector3d Ground, Dir, Up;
            public double Born = double.NegativeInfinity;
            public float Strength;
            public Transform Tr;
            public Renderer R;
        }

        VesselView view;
        CelestialBody body;
        readonly Sample[] samples = new Sample[Points];
        int newest = -1, count;
        double lastSample = double.NegativeInfinity, lastPuff = double.NegativeInfinity;
        float lastRadius = 1;
        Vector3d lastSrc;

        Mesh mesh;
        MeshRenderer ribbon;
        readonly Vector3[] pts = new Vector3[Points + 1];
        readonly float[] widths = new float[Points + 1];
        readonly Vector3[] verts = new Vector3[2 * (Points + 1)];
        readonly Vector3[] normals = new Vector3[2 * (Points + 1)];

        readonly Puff[] puffs = new Puff[Puffs];
        int nextPuff;
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
            var tris = new int[6 * Points];
            for (int i = 0; i <= Points; i++)
            {
                float u = i / (float)Points;
                uv[2 * i] = new Vector2(u, 0);
                uv[2 * i + 1] = new Vector2(u, 1);
                if (i == Points) continue;
                int a = 2 * i, t = 6 * i;
                tris[t] = a; tris[t + 1] = a + 1; tris[t + 2] = a + 2;
                tris[t + 3] = a + 1; tris[t + 4] = a + 3; tris[t + 5] = a + 2;
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

            var puffMat = new Material(smoke) { name = "Smoke Puff (runtime)" };
            puffMat.SetTexture("_BaseColorMap", PuffTexture());
            var quad = ProcMesh.Billboard();
            for (int i = 0; i < Puffs; i++)
            {
                var p = new GameObject("Puff");
                p.transform.SetParent(transform, false);
                p.AddComponent<MeshFilter>().sharedMesh = quad;
                var r = p.AddComponent<MeshRenderer>();
                r.sharedMaterial = puffMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.enabled = false;
                puffs[i] = new Puff { Tr = p.transform, R = r };
            }
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
                count = 0; newest = -1;
                foreach (var p in puffs) p.Born = double.NegativeInfinity;
            }
            double now = u.Time;
            var q = body.Orientation;
            var qi = q.Inverse;

            bool burning = view.ExhaustSource(out var src, out float radius, out float thr);
            float w0 = 0;
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
                lastRadius = radius;
                if (v.Density > DensityFull) SpawnPuffs(v, now, srcBf, upBf, Vector3d.Dot(offBf, upBf));
            }
            // Выключенный двигатель кладёт точки нулевой ширины в последнее место — хвост стареет дальше.
            if ((burning || count > 0) && (now - lastSample >= SampleInterval || now < lastSample))
            {
                lastSample = now;
                newest = (newest + 1) % Points;
                samples[newest] = new Sample { Bf = srcBf, Time = now, Width = w0 };
                count = System.Math.Min(count + 1, Points);
            }

            bool show = !MapView.IsOpen;
            UpdateRibbon(show && count > 0, now, q, srcBf);
            UpdatePuffs(show, now, q);
        }

        void UpdateRibbon(bool show, double now, QuaternionD q, Vector3d headBf)
        {
            bool any = false;
            for (int i = 0; i < count; i++) if (samples[i].Width > 0) { any = true; break; }
            ribbon.enabled = show && any;
            if (!ribbon.enabled) return;

            int n = Points + 1;
            var tail = At(count - 1).Bf;
            for (int i = 0; i < n; i++)
            {
                Vector3d bf; float w = 0;
                if (i == 0) bf = headBf;
                else if (i - 1 < count)
                {
                    var sm = At(i - 1);
                    bf = sm.Bf;
                    if (sm.Width > 0) w = sm.Width + SpreadRate * (float)(now - sm.Time);
                }
                else bf = tail;
                pts[i] = FloatingOrigin.ToUnity(body.Position + q * bf);
                widths[i] = w;
            }
            // Дым выходит из-за факела: ширина нарастает по пути от среза, а не по времени — на 1 км/с
            // «секунда» растянула бы тонкий хвост на километр.
            float emerge = lastRadius * EmergeCalibers, path = 0;
            var cam = Camera.main;
            var eye = cam != null ? cam.transform.position : Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) path += Vector3.Distance(pts[i], pts[i - 1]);
                float w = widths[i] * Mathf.Clamp01(path / emerge) * 0.5f;
                var tangent = pts[Mathf.Min(i + 1, n - 1)] - pts[Mathf.Max(i - 1, 0)];
                var toCam = eye - pts[i];
                var side = Vector3.Cross(tangent, toCam);
                side = side.sqrMagnitude > 1e-12f ? side.normalized * w : Vector3.zero;
                verts[2 * i] = pts[i] - side;
                verts[2 * i + 1] = pts[i] + side;
                var nrm = toCam.sqrMagnitude > 1e-12f ? toCam.normalized : Vector3.up;
                normals[2 * i] = normals[2 * i + 1] = nrm;
            }
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.RecalculateBounds();
        }

        /// <summary>Клубы у стола (§9.5): из-под среза разлетаются по грунту, растут, медленно поднимаются.</summary>
        void SpawnPuffs(Vessel v, double now, Vector3d srcBf, Vector3d upBf, double offUp)
        {
            double agl = v.TerrainAltitude + offUp;
            if (agl > CloudHeight || now - lastPuff < CloudInterval) return;
            if (now < lastPuff) lastPuff = now;
            lastPuff = now;
            var e1 = Vector3d.AnyPerpendicular(upBf).normalized;
            var e2 = Vector3d.Cross(upBf, e1);
            double a = rng.NextDouble() * 2 * System.Math.PI;
            var p = puffs[nextPuff];
            nextPuff = (nextPuff + 1) % Puffs;
            p.Ground = srcBf - upBf * System.Math.Max(0, agl);
            p.Dir = e1 * System.Math.Cos(a) + e2 * System.Math.Sin(a);
            p.Up = upBf;
            p.Born = now;
            p.Strength = 1 - (float)(System.Math.Max(0, agl) / CloudHeight);
        }

        void UpdatePuffs(bool show, double now, QuaternionD q)
        {
            var cam = Camera.main;
            foreach (var p in puffs)
            {
                float age = (float)(now - p.Born);
                bool on = show && age >= 0 && age < PuffLife;
                p.R.enabled = on;
                if (!on) continue;
                float size = PuffSize + PuffGrowth * Mathf.Sqrt(age);
                double run = PuffSpeed * PuffDrag * (1 - System.Math.Exp(-age / PuffDrag));
                var bf = p.Ground + p.Dir * run + p.Up * (PuffRise * age + size * 0.5);
                p.Tr.position = FloatingOrigin.ToUnity(body.Position + q * bf);
                if (cam != null) p.Tr.rotation = cam.transform.rotation;
                p.Tr.localScale = Vector3.one * size;
                float life = 1 - age / PuffLife;
                float alpha = PuffAlpha * p.Strength * Mathf.Clamp01(age * 2) * life * life;
                mpb.Clear();
                mpb.SetColor("_BaseColor", new Color(SmokeColor.r, SmokeColor.g, SmokeColor.b, alpha));
                p.R.SetPropertyBlock(mpb);
            }
        }

        /// <summary>Лента: U — доля жизни (хвост гаснет в ноль), V — поперёк (мягкий край), клочья — шум Перлина.</summary>
        static Texture2D trailTex, puffTex;
        static Texture2D TrailTexture()
        {
            if (trailTex != null) return trailTex;
            const int nu = 128, nv = 32;
            trailTex = new Texture2D(nu, nv, TextureFormat.RGBA32, true) { name = "Smoke Trail", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < nv; y++)
            for (int x = 0; x < nu; x++)
            {
                float t = x / (nu - 1f), s = (y + 0.5f) / nv * 2 - 1;
                float across = Mathf.Exp(-s * s * 3) * (1 - s * s);
                float along = Mathf.Clamp01((1 - t) * 4);
                float noise = 0.7f + 0.6f * Mathf.PerlinNoise(x * 0.35f, y * 0.25f + 11);
                trailTex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(across * along * noise)));
            }
            trailTex.Apply(true, true);
            return trailTex;
        }

        static Texture2D PuffTexture()
        {
            if (puffTex != null) return puffTex;
            const int n = 64;
            puffTex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "Smoke Puff", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                // Клочья: шум сдвигает край, середина плотная.
                float noise = Mathf.PerlinNoise(x * 0.12f + 3, y * 0.12f + 7);
                float f = Mathf.Clamp01(1 - r * (0.8f + 0.5f * noise));
                puffTex.SetPixel(x, y, new Color(1, 1, 1, f * f * (3 - 2 * f)));
            }
            puffTex.Apply(true, true);
            return puffTex;
        }
    }
}
