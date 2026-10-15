using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kare.Space.Game
{
    /// <summary>
    /// Кома и хвосты комет (§2.3, §9.3). Ядро — обычное тело BodyRenderer (тёмное, комковатое — рельеф из ядра),
    /// здесь только свечение: кома — мягкий ореол, пылевой хвост — широкий изогнутый жёлто-белый (отстаёт по орбите:
    /// между антисолнечным направлением и −скоростью), ионный — узкий прямой голубой строго от Солнца.
    /// Хвосты длиной 1e7–1e8 км: геометрия считается в истинных метрах (double) и сжимается в оболочку §2.7 ПО ВЕРШИНАМ
    /// (BodyRenderer.ShellScale) — направления из начала сохраняются, угловой вид как у настоящего, а всё лежит
    /// ближе дальней плоскости 1e7. На карте сжатия нет — хвост в истинном масштабе рядом с линией орбиты.
    /// Яркость — доля белого мимо экспозиции (как точки тел): поверхностная яркость не зависит от расстояния до
    /// наблюдателя, а там, где хвост уже минимума пикселей, ширина растёт с сохранением потока — издали он тускнеет.
    /// Аллокаций в кадре нет: массивы вершин заведены заранее, Mesh.SetVertices/SetColors их не копируют в кучу.
    /// </summary>
    [DefaultExecutionOrder(120)] // после FlightCamera (100) и MapView (110): поза камеры и режим карты этого кадра
    public sealed class CometView : MonoBehaviour
    {
        const string GlowShader = "Kare/Comet Glow HDRP";

        /// <summary>Сегментов вдоль хвоста. Узлы сгущены к голове (SpacingPow): там изгиб и яркость меняются быстрее всего.</summary>
        const int Segments = 32;
        const double SpacingPow = 1.6;

        /// <summary>Яркость в полную силу (комета на 1 а.е., активность 1), доля белого. Кома — ядро «звезды» с лёгким
        /// bloom; хвосты — как Млечный Путь: видны на звёздном небе, но не засвечивают кадр. Пара: PeakGain.</summary>
        const float ComaBrightness = 1.0f, DustBrightness = 0.18f, IonBrightness = 0.10f;
        /// <summary>Рост яркости к Солнцу ∝ (1 а.е./r)² ограничен этим множителем: Галлей в перигелии (0,59 а.е.) — ×2, не ×2,9.</summary>
        const float PeakGain = 2;
        /// <summary>Днём в атмосфере хвост не виден (Хейл — Бопп днём не наблюдался): остаётся эта доля.
        /// Пара: BodyRenderer.PointDayFloor (0,08) — у диффузного хвоста порог ниже, чем у точки.</summary>
        const float DayFloor = 0.03f;
        /// <summary>Минимальные размеры, px: диаметр комы и полная ширина хвоста. Меньше — растягиваем с сохранением потока.</summary>
        const float ComaMinPx = 6, TailMinPx = 2;
        /// <summary>Кома при угасании сжимается до этой доли полного радиуса (пыль ещё рядом с ядром).</summary>
        const float ComaMinFraction = 0.3f;
        /// <summary>Гасим объект, если ярче этой доли белого нигде не будет.</summary>
        const float VisibleFloor = 1e-3f;
        const int TexRes = 64;

        static readonly Color ComaTint = new Color(0.85f, 1.0f, 0.92f); // C₂ и CN подкрашивают голову в зелёный
        static readonly Color DustTint = new Color(1.0f, 0.90f, 0.72f); // рассеянный пылью солнечный свет, чуть желтее
        static readonly Color IonTint = new Color(0.35f, 0.55f, 1.0f);  // CO⁺ светится синим

        /// <summary>
        /// Размеры по наблюдениям (полуширины, м). Галлей 1986: ионный хвост до ~0,2 а.е., пылевой ~0,1 а.е.;
        /// Хейл — Бопп 1997: ионный ~0,7 а.е., пылевой ~0,4 а.е. и очень широкий, кома ~1 млн км. Gain — собственная
        /// яркость (Хейл — Бопп в абсолютной величине на ~6m ярче, но и крупнее — поверхностная яркость разнится меньше).
        /// </summary>
        sealed class Profile
        {
            public double ComaRadius, IonLength, IonHalf0, IonHalf1, DustLength, DustHalf0, DustHalf1, DustCurve;
            public float Gain;
        }

        static readonly Profile Halley = new Profile
        {
            ComaRadius = 2e8, IonLength = 3e10, IonHalf0 = 1.5e8, IonHalf1 = 1e9,
            DustLength = 1.5e10, DustHalf0 = 2e8, DustHalf1 = 3e9, DustCurve = 0.30, Gain = 0.6f,
        };

        static readonly Profile HaleBopp = new Profile
        {
            ComaRadius = 1e9, IonLength = 1e11, IonHalf0 = 5e8, IonHalf1 = 3e9,
            DustLength = 6e10, DustHalf0 = 1e9, DustHalf1 = 1.2e10, DustCurve = 0.35, Gain = 1.0f,
        };

        static Profile ProfileFor(string id) => id == "halebopp" ? HaleBopp : Halley;

        /// <summary>Строки текстуры хвоста: v = 0,25 — пыль (широкий мягкий профиль), 0,75 — ионы (узкий).</summary>
        const float DustRow = 0.25f, IonRow = 0.75f;

        sealed class Part
        {
            public GameObject Go;
            public Mesh Mesh;
            public Vector3[] V;
            public Color[] C;
        }

        sealed class Comet
        {
            public CelestialBody Body;
            public Profile P;
            public Part Coma, Dust, Ion;
        }

        readonly List<Comet> comets = new List<Comet>();
        SolarSystem system;
        Material tailMat, comaMat;
        int[] stripTris;

        void Start()
        {
            var shader = Shader.Find(GlowShader);
            if (shader == null)
            {
                Debug.LogWarning($"[Comets] нет шейдера {GlowShader} — кома и хвосты не рисуются");
                enabled = false;
                return;
            }
            tailMat = new Material(shader) { name = "Comet Tail (runtime)" };
            tailMat.SetTexture("_MainTex", TailTexture());
            comaMat = new Material(shader) { name = "Comet Coma (runtime)" };
            comaMat.SetTexture("_MainTex", ComaTexture());
            stripTris = StripTriangles();
        }

        void Rebuild(SolarSystem s)
        {
            foreach (var c in comets)
            {
                Destroy(c.Coma.Go);
                Destroy(c.Dust.Go);
                Destroy(c.Ion.Go);
            }
            comets.Clear();
            system = s;
            if (s == null) return;
            foreach (var b in s.Bodies)
            {
                if (!b.IsComet) continue;
                comets.Add(new Comet
                {
                    Body = b, P = ProfileFor(b.Id),
                    Coma = MakePart($"Coma {b.Id}", comaMat, 4, new[] { 0, 2, 1, 0, 3, 2 },
                        new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) }),
                    Dust = MakePart($"Dust tail {b.Id}", tailMat, 2 * (Segments + 1), stripTris, StripUv(DustRow)),
                    Ion = MakePart($"Ion tail {b.Id}", tailMat, 2 * (Segments + 1), stripTris, StripUv(IonRow)),
                });
            }
        }

        Part MakePart(string name, Material mat, int verts, int[] tris, Vector2[] uv)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var m = new Mesh { name = name };
            m.MarkDynamic();
            var part = new Part { Go = go, Mesh = m, V = new Vector3[verts], C = new Color[verts] };
            m.SetVertices(part.V);
            m.SetColors(part.C);
            m.uv = uv;
            m.triangles = tris;
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            go.SetActive(false);
            return part;
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            var cam = Camera.main;
            if (u?.System != system) Rebuild(u?.System);
            if (u == null || cam == null || comets.Count == 0)
            {
                foreach (var c in comets) Hide(c);
                return;
            }
            // Камера в метрах P: в полёте она у начала и вне оболочки, на карте сжатия нет — формула одна.
            var camT = cam.transform;
            var cp = camT.position;
            var eye = FloatingOrigin.OriginP + new Vector3d(cp.x, cp.y, cp.z).SwapYZ;
            var r3 = camT.right;
            var u3 = camT.up;
            var right = new Vector3d(r3.x, r3.y, r3.z).SwapYZ;
            var up = new Vector3d(u3.x, u3.y, u3.z).SwapYZ;
            double pxPerRad = cam.pixelHeight / (2 * System.Math.Tan(cam.fieldOfView * 0.5 * Mathf.Deg2Rad));
            var v = FlightView.Main;
            float day = MapView.IsOpen || v == null ? 0 : (float)SunLight.Visible * (1 - SkyController.AirWeight(v));
            float sky = Mathf.Lerp(1, DayFloor, day);
            foreach (var c in comets) Draw(c, eye, right, up, pxPerRad, sky);
        }

        static void Hide(Comet c)
        {
            if (c.Coma.Go.activeSelf) c.Coma.Go.SetActive(false);
            if (c.Dust.Go.activeSelf) c.Dust.Go.SetActive(false);
            if (c.Ion.Go.activeSelf) c.Ion.Go.SetActive(false);
        }

        /// <summary>Активность 0…1: в полную силу ближе CometActiveFull, к CometActiveOff гаснет (лёд перестаёт сублимировать).</summary>
        static double Activity(CelestialBody b, double r) =>
            1 - MathD.Smoothstep(b.CometActiveFull, b.CometActiveOff, r);

        void Draw(Comet c, Vector3d eye, Vector3d right, Vector3d up, double pxPerRad, float sky)
        {
            var b = c.Body;
            var p = c.P;
            var head = b.Position;
            double r = head.magnitude; // Солнце неподвижно в начале P
            double act = r > 0 ? Activity(b, r) : 0;
            float gain = (float)(act * System.Math.Min(PeakGain, Constants.AU * Constants.AU / (r * r))) * p.Gain * sky;
            if (act <= 0 || gain * ComaBrightness < VisibleFloor)
            {
                Hide(c);
                return;
            }
            var anti = head / r;
            // Пыль отстаёт по орбите: изгиб к −скорости, без её радиальной части (та уже вдоль хвоста).
            var vel = b.Velocity;
            var lag = anti * Vector3d.Dot(vel, anti) - vel;
            double lagLen = lag.magnitude;
            lag = lagLen > 1e-6 ? lag / lagLen : Perpendicular(anti);
            double minHalfRad = TailMinPx * 0.5 / pxPerRad;
            double grow = 0.5 + 0.5 * act;

            Strip(c.Dust, head, anti, lag, p.DustLength * act, p.DustCurve, p.DustHalf0 * grow, p.DustHalf1 * grow, 0.7, 1.5,
                DustTint * (DustBrightness * gain), eye, minHalfRad);
            Strip(c.Ion, head, anti, lag, p.IonLength * act, 0, p.IonHalf0 * grow, p.IonHalf1 * grow, 1.0, 1.0,
                IonTint * (IonBrightness * gain), eye, minHalfRad);

            // Кома — квад к камере в плоскости через ядро; мельче ComaMinPx растягивается, яркость ∝ (R/R')².
            double rc = p.ComaRadius * (ComaMinFraction + (1 - ComaMinFraction) * act);
            double d = (head - eye).magnitude;
            double rp = System.Math.Max(rc, ComaMinPx * 0.5 / pxPerRad * d);
            float k = (float)(rc / rp * (rc / rp));
            var col = ComaTint * (ComaBrightness * gain * k);
            bool on = gain * k * ComaBrightness > VisibleFloor;
            if (c.Coma.Go.activeSelf != on) c.Coma.Go.SetActive(on);
            if (!on) return;
            var x = right * rp;
            var y = up * rp;
            var V = c.Coma.V;
            V[0] = ToUnity(head - x - y);
            V[1] = ToUnity(head + x - y);
            V[2] = ToUnity(head + x + y);
            V[3] = ToUnity(head - x + y);
            for (int i = 0; i < 4; i++) c.Coma.C[i] = col;
            c.Coma.Mesh.SetVertices(V);
            c.Coma.Mesh.SetColors(c.Coma.C);
        }

        /// <summary>
        /// Лента хвоста: ось s от ядра вдоль anti, изгиб curve·s²/L к lag (у ионного 0 — прямая), полуширина
        /// half0 → half1 (степень widthPow), яркость (1 − s/L)^fadePow. Лента повёрнута к глазу в каждом узле.
        /// </summary>
        static void Strip(Part st, Vector3d head, Vector3d anti, Vector3d lag, double len, double curve, double half0,
            double half1, double widthPow, double fadePow, Color tint, Vector3d eye, double minHalfRad)
        {
            bool on = len > 0 && tint.maxColorComponent > VisibleFloor;
            if (st.Go.activeSelf != on) st.Go.SetActive(on);
            if (!on) return;
            var V = st.V;
            var C = st.C;
            for (int i = 0; i <= Segments; i++)
            {
                double f = System.Math.Pow((double)i / Segments, SpacingPow);
                double s = len * f;
                var center = head + anti * s + lag * (curve * s * f);
                var tangent = anti + lag * (2 * curve * f);
                var toC = center - eye;
                double dist = toC.magnitude;
                var side = Vector3d.Cross(tangent, toC);
                double sl = side.magnitude;
                // Взгляд точно вдоль хвоста — сторона не определена; любая перпендикулярная (лента всё равно в точку).
                side = sl > 1e-9 * dist ? side / sl : Perpendicular(tangent.normalized);
                double half = half0 + (half1 - half0) * System.Math.Pow(f, widthPow);
                double hw = System.Math.Max(half, minHalfRad * dist);
                float a = (float)(System.Math.Pow(1 - f, fadePow) * half / hw);
                V[2 * i] = ToUnity(center - side * hw);
                V[2 * i + 1] = ToUnity(center + side * hw);
                C[2 * i] = C[2 * i + 1] = tint * a;
            }
            st.Mesh.SetVertices(V);
            st.Mesh.SetColors(C);
        }

        /// <summary>Точка P (м) → Unity с оболочкой §2.7 по самой точке (см. BodyRenderer.ShellScale).</summary>
        static Vector3 ToUnity(Vector3d worldP)
        {
            var rel = (worldP - FloatingOrigin.OriginP).SwapYZ;
            return FloatingOrigin.ToVector3(rel * BodyRenderer.ShellScale(rel.magnitude));
        }

        static Vector3d Perpendicular(Vector3d n)
        {
            var a = System.Math.Abs(n.z) < 0.9 ? Vector3d.forward : Vector3d.right;
            return Vector3d.Cross(n, a).normalized;
        }

        // ---------------------------------------------------------------- геометрия и текстуры (один раз)

        static int[] StripTriangles()
        {
            var t = new int[Segments * 6];
            for (int i = 0; i < Segments; i++)
            {
                int a = 2 * i;
                t[6 * i] = a;
                t[6 * i + 1] = a + 2;
                t[6 * i + 2] = a + 1;
                t[6 * i + 3] = a + 1;
                t[6 * i + 4] = a + 2;
                t[6 * i + 5] = a + 3;
            }
            return t;
        }

        static Vector2[] StripUv(float row)
        {
            var uv = new Vector2[2 * (Segments + 1)];
            for (int i = 0; i <= Segments; i++)
            {
                uv[2 * i] = new Vector2(0, row);
                uv[2 * i + 1] = new Vector2(1, row);
            }
            return uv;
        }

        /// <summary>Профиль поперёк хвоста: строка 0 — пыль (гауссиана σ ≈ 0,38 полуширины), строка 1 — ионы (σ ≈ 0,25,
        /// узкий жгут). К краю ленты — в ноль, без ступеньки.</summary>
        static Texture2D TailTexture()
        {
            var t = new Texture2D(TexRes, 2, TextureFormat.RGBA32, false, true) { name = "Comet Tail", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[TexRes * 2];
            for (int x = 0; x < TexRes; x++)
            {
                float q = (x + 0.5f) / TexRes * 2 - 1;
                float edge = 1 - q * q;
                px[x] = Gray(Mathf.Exp(-q * q * 3.5f) * edge);
                px[TexRes + x] = Gray(Mathf.Exp(-q * q * 8f) * edge);
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        /// <summary>Кома: ∝ 1/(r + r₀) — у реальных ком яркость спадает как 1/ρ (Jewitt), r₀ — ядро «звезды»; к краю в ноль.</summary>
        static Texture2D ComaTexture()
        {
            const float core = 0.06f;
            var t = new Texture2D(TexRes, TexRes, TextureFormat.RGBA32, false, true) { name = "Comet Coma", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[TexRes * TexRes];
            float c = (TexRes - 1) * 0.5f;
            for (int y = 0; y < TexRes; y++)
                for (int x = 0; x < TexRes; x++)
                {
                    float r = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float e = 1 - r * r;
                    px[y * TexRes + x] = Gray(r >= 1 ? 0 : core / (r + core) * e * e);
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static Color32 Gray(float v)
        {
            byte k = (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255);
            return new Color32(k, k, k, k);
        }

        void OnDestroy()
        {
            Rebuild(null);
            if (tailMat != null) Destroy(tailMat);
            if (comaMat != null) Destroy(comaMat);
        }
    }
}
