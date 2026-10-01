using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.Game
{
    /// <summary>
    /// Вид борта (GDD §5, §9.5): процедурный меш из Design.Sections снизу вверх, начало ноды — центр масс,
    /// ориентация — Vessel.Attitude (уже в осях Unity, +Y = нос), факел под работающими двигателями.
    /// Отделённые ступени — отдельные Vessel со своим видом (их заводит GameBootstrap).
    /// </summary>
    public sealed class VesselView : MonoBehaviour
    {
        /// <summary>Дальше этого от активного борта обломки не рисуем — на таком расстоянии они меньше пикселя.</summary>
        const float DrawDistance = 50000;
        /// <summary>Длина факела в калибрах сопла у земли и рост в вакууме (§9.5: расширение струи).</summary>
        const float PlumeLengthSL = 12, PlumeVacuumGrowth = 3;
        /// <summary>Связка сопел: кольцо на этой доле радиуса днища, сопло — доля шага кольца (зазор между
        /// раструбами). Пара: PlumeClusterFill — общий факел накрывает кольцо целиком (у земли струи сливаются).</summary>
        const float NozzleRing = 0.62f, NozzleGap = 0.85f, PlumeClusterFill = 0.85f;
        /// <summary>Свечение длиннее ядра во столько раз; ядро в вакууме почти не раздувается.</summary>
        const float GlowLength = 2.2f, CoreSpread = 0.25f;
        /// <summary>Яркость ядра и свечения у среза, нит. Пара: дневная экспозиция EV 14 (§9.3) — серое 18 %
        /// ≈ 2000 нит. Физичные 2·10⁵ не годятся: аддитив складывает 4 стенки, bloom размазывает перебор
        /// на весь экран — кадр белый (замер 01.10.2026). 3·10³ при EV 14 — белое ядро с ореолом.</summary>
        const float CoreNits = 3e3f, GlowNits = 3e2f;
        /// <summary>В вакууме керосиновый факел тусклее: доля яркости при p = 0.</summary>
        const float VacuumBrightness = 0.3f;
        /// <summary>Сила света факела на дросселе 1, кд: на 30 м ≈ 2000 лк — подсветка стола и борта ночью
        /// заметна, днём (Солнце 127 000 лк) почти нет. Пара: PlumeLightRange.</summary>
        const float PlumeCandela = 2e6f, PlumeLightRange = 600;
        const double SeaLevelPressure = 101325;

        public Vessel Vessel { get; private set; }

        sealed class Part
        {
            public int Index;
            public Transform Tr;
            public Transform Plume, Glow;
            public Renderer CoreR, GlowR;
            public Light PlumeLight;
            public float PlumeRadius;
        }

        readonly List<Part> parts = new List<Part>();
        Material bodyMat, plumeMat;
        string builtSignature;
        double[] baseHeight;
        MaterialPropertyBlock mpb;

        static readonly Color StageColor = new Color(0.82f, 0.83f, 0.80f);
        static readonly Color PayloadColor = new Color(0.65f, 0.62f, 0.55f);
        static readonly Color CapsuleColor = new Color(0.30f, 0.28f, 0.26f);
        static readonly Color FairingColor = new Color(0.92f, 0.92f, 0.90f);
        static readonly Color NozzleColor = new Color(0.20f, 0.18f, 0.17f);

        public void Init(Vessel v, Material mat, Material plume)
        {
            Vessel = v;
            bodyMat = mat != null ? mat : new Material(Shader.Find("HDRP/Lit"));
            // Копия в рантайме: HDMaterial.ValidateMaterial в редакторе снимает _EMISSIVE_COLOR_MAP с ассета,
            // если карта задана только в MPB, — факел терял градиент и был серо-белым конусом (01.10.2026).
            plumeMat = new Material(plume != null ? plume : bodyMat) { name = "Plume (runtime)" };
            plumeMat.SetTexture("_EmissiveColorMap", PlumeGradient());
            plumeMat.EnableKeyword("_EMISSIVE_COLOR_MAP");
            baseHeight = new double[v.Design.Sections.Count];
            mpb = new MaterialPropertyBlock();
            Rebuild();
            LateUpdate();
        }

        string Signature()
        {
            var a = Vessel.Attached;
            var c = new char[a.Length];
            for (int i = 0; i < a.Length; i++) c[i] = a[i] ? (Vessel.IsEnclosed(i) ? 'e' : '1') : '0';
            return new string(c);
        }

        void Rebuild()
        {
            foreach (Transform ch in transform) Destroy(ch.gameObject);
            parts.Clear();
            builtSignature = Signature();
            var secs = Vessel.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Vessel.Attached[i] || Vessel.IsEnclosed(i)) continue;
                var s = secs[i];
                var go = new GameObject(s.Name);
                go.transform.SetParent(transform, false);
                float len = (float)s.Length, r = (float)s.Radius;
                Mesh mesh; Color col;
                switch (s.Kind)
                {
                    case SectionKind.Capsule: mesh = ProcMesh.Frustum(r, r * 0.35f, len, 24, true); col = CapsuleColor; break;
                    case SectionKind.Fairing: mesh = ProcMesh.Fairing(r, len, 24); col = FairingColor; break;
                    case SectionKind.Payload: mesh = ProcMesh.Frustum(r, r, len, 24, true); col = PayloadColor; break;
                    default: mesh = ProcMesh.Frustum(r, r, len, 24, true); col = StageColor; break;
                }
                AddRenderer(go, mesh, col);
                var part = new Part { Index = i, Tr = go.transform };

                if (s.HasEngine)
                {
                    // Связка сопел под днищем: одно — по центру, больше — кольцо (+ центральное от 5 штук).
                    int n = s.EngineCount, ring = n >= 5 ? n - 1 : n == 1 ? 0 : n;
                    float rr = ring > 0 ? r * NozzleRing : 0;
                    float nr = ring > 0 ? Mathf.Min(rr * Mathf.Sin(Mathf.PI / Mathf.Max(ring, 2)) * NozzleGap, r * 0.32f)
                                        : Mathf.Min(r * 0.45f, 1.2f);
                    var nozzle = new GameObject("Nozzle");
                    nozzle.transform.SetParent(go.transform, false);
                    nozzle.transform.localPosition = new Vector3(0, -nr * 1.4f, 0);
                    var bell = ProcMesh.Frustum(nr, nr * 0.45f, nr * 1.4f, 16, false);
                    for (int k = 0; k < n; k++)
                    {
                        var b = new GameObject("Bell");
                        b.transform.SetParent(nozzle.transform, false);
                        float a = 2 * Mathf.PI * k / Mathf.Max(ring, 1);
                        b.transform.localPosition = k < ring ? new Vector3(rr * Mathf.Cos(a), 0, rr * Mathf.Sin(a)) : Vector3.zero;
                        AddRenderer(b, bell, NozzleColor);
                    }
                    // Общий факел на связку: струи у земли сливаются в один столб, отдельные 9 факелов
                    // дали бы 18 аддитивных слоёв и пересвет на стыках.
                    nr = ring > 0 ? (rr + nr) * PlumeClusterFill : nr;

                    // Факел: ядро (сужается) + свечение (расширяется), оба аддитивные двусторонние —
                    // передняя и задняя стенки складываются, к оси струя плотнее, как у объёма.
                    var plume = new GameObject("Plume");
                    plume.transform.SetParent(nozzle.transform, false);
                    part.CoreR = AddPlume(plume, ProcMesh.Plume(1, -0.55f, 1, 16, 12));
                    var glow = new GameObject("Glow");
                    glow.transform.SetParent(nozzle.transform, false);
                    part.GlowR = AddPlume(glow, ProcMesh.Plume(1.1f, 1.6f, 0.6f, 16, 12));
                    var lgo = new GameObject("Plume Light");
                    lgo.transform.SetParent(nozzle.transform, false);
                    lgo.transform.localPosition = new Vector3(0, -nr * 3, 0);
                    var light = lgo.AddComponent<Light>();
                    light.type = LightType.Point;
                    lgo.AddComponent<HDAdditionalLightData>();
                    light.lightUnit = UnityEngine.Rendering.LightUnit.Candela;
                    light.color = new Color(1f, 0.72f, 0.45f);
                    light.range = PlumeLightRange;
                    light.shadows = LightShadows.None;
                    part.PlumeLight = light;
                    part.Plume = plume.transform;
                    part.Glow = glow.transform;
                    part.PlumeRadius = nr;
                    plume.SetActive(false);
                    glow.SetActive(false);
                    lgo.SetActive(false);
                }
                parts.Add(part);
            }
        }

        Renderer AddPlume(GameObject go, Mesh mesh)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = plumeMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mr;
        }

        /// <summary>Градиент по длине струи (V = 0 у среза): белый → жёлтый → оранжево-красный, яркость спадает.</summary>
        static Texture2D gradient;
        static Texture2D PlumeGradient()
        {
            if (gradient != null) return gradient;
            const int n = 64;
            gradient = new Texture2D(1, n, TextureFormat.RGBAHalf, false, true) { name = "Plume Gradient", wrapMode = TextureWrapMode.Clamp };
            for (int i = 0; i < n; i++)
            {
                float t = i / (n - 1f);
                var c = Color.Lerp(Color.Lerp(new Color(1f, 0.95f, 0.85f), new Color(1f, 0.7f, 0.3f), Mathf.Clamp01(t * 3)),
                                   new Color(0.9f, 0.3f, 0.1f), Mathf.Clamp01(t * 1.5f - 0.3f));
                // Срез без резкой кромки, хвост гаснет в ноль.
                float fade = Mathf.Clamp01(t * 12) * Mathf.Exp(-3.5f * t) * (1 - t);
                // Альфа = 1: аддитив HDRP умножает цвет на альфу — затухание только в RGB.
                gradient.SetPixel(0, i, new Color(c.r * fade, c.g * fade, c.b * fade, 1));
            }
            gradient.Apply(false, true);
            return gradient;
        }

        void SetPlumeColor(Renderer r, float nits)
        {
            mpb.Clear();
            // Через эмиссию, а не _UnlitColor: цвет Unlit HDRP не умножается на экспозицию (1 нит уже белый),
            // эмиссия — умножается. Альфа 1: аддитив HDRP умножает цвет на альфу. Градиент — в материале.
            mpb.SetColor("_EmissiveColor", new Color(nits, nits, nits, 1));
            r.SetPropertyBlock(mpb);
        }

        void AddRenderer(GameObject go, Mesh mesh, Color col)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = bodyMat;
            // mpb общий с факелом: без Clear корпус после отделения ступени наследовал эмиссию факела
            // (3·10³ нит) и ночью при EV −5 выбеливал кадр целиком.
            mpb.Clear();
            mpb.SetColor("_BaseColor", col);
            mr.SetPropertyBlock(mpb);
        }

        /// <summary>Самый яркий видимый факел за кадр, нит — SkyController поднимает по нему нижний предел EV
        /// (иначе ночью предэкспонированный факел переполняет half-буфер). Порядок LateUpdate не важен:
        /// значение живёт и следующий кадр.</summary>
        public static float PlumePeakNits => Time.frameCount - peakFrame <= 1 ? peak : 0;
        static float peak;
        static int peakFrame;

        static void ReportPlume(float nits)
        {
            if (peakFrame != Time.frameCount) { peakFrame = Time.frameCount; peak = 0; }
            peak = Mathf.Max(peak, nits);
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (Vessel == null || u?.Active == null) return;
            bool show = Vessel.Alive && !MapView.IsOpen;
            var pos = FloatingOrigin.ToUnity(FloatingOrigin.WorldP(Vessel));
            if (Vessel != u.Active && pos.magnitude > DrawDistance) show = false;
            SetVisible(show);
            if (!show) return;

            if (Signature() != builtSignature) Rebuild();
            Vessel.MassProperties(out _, out double com, out _, out _);
            Vessel.Layout(baseHeight);
            transform.SetPositionAndRotation(pos, FloatingOrigin.ToQuaternion(Vessel.Attitude));

            float pressure = (float)(Vessel.StaticPressure / SeaLevelPressure);
            foreach (var p in parts)
            {
                p.Tr.localPosition = new Vector3(0, (float)(baseHeight[p.Index] - com), 0);
                if (p.Plume == null) continue;
                bool on = Vessel.Running[p.Index];
                float thr = on ? (float)Vessel.EffectiveThrottle(p.Index) : 0;
                if (thr > 0.01f) ReportPlume(CoreNits * thr);
                bool burning = thr > 0.01f;
                p.Plume.gameObject.SetActive(burning);
                p.Glow.gameObject.SetActive(burning);
                p.PlumeLight.gameObject.SetActive(burning);
                if (!burning) continue;
                // В вакууме струя раздувается и удлиняется; яркость/длина — от дросселя (§9.5).
                float vac = 1 - Mathf.Clamp01(pressure);
                float spread = 1 + vac * PlumeVacuumGrowth;
                float len = p.PlumeRadius * PlumeLengthSL * (0.4f + 0.6f * thr) * spread;
                float flicker = 1 + 0.05f * Mathf.Sin(Time.time * 53f + p.Index) + 0.03f * Mathf.Sin(Time.time * 31f);
                float coreR = p.PlumeRadius * (1 + vac * PlumeVacuumGrowth * CoreSpread);
                p.Plume.localScale = new Vector3(coreR, len * flicker, coreR);
                p.Glow.localScale = new Vector3(p.PlumeRadius * spread, len * GlowLength * flicker, p.PlumeRadius * spread);
                // Яркость на единицу площади: раздувшаяся струя тусклее (§9.5).
                float bright = thr * Mathf.Lerp(1, VacuumBrightness, vac) * flicker;
                SetPlumeColor(p.CoreR, CoreNits * bright);
                SetPlumeColor(p.GlowR, GlowNits * bright / spread);
                p.PlumeLight.intensity = PlumeCandela * thr * flicker;
            }
        }

        bool visible = true;

        void SetVisible(bool on)
        {
            if (on == visible) return;
            visible = on;
            foreach (Transform ch in transform) ch.gameObject.SetActive(on);
        }
    }

    /// <summary>Процедурные тела вращения вокруг +Y для бортов.</summary>
    public static class ProcMesh
    {
        /// <summary>Усечённый конус от y=0 (радиус r0) до y=h (радиус r1); h &lt; 0 — вниз. caps — торцы.</summary>
        public static Mesh Frustum(float r0, float r1, float h, int seg, bool caps)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                verts.Add(d * r0);
                verts.Add(d * r1 + Vector3.up * h);
            }
            for (int i = 0; i < seg; i++)
            {
                int a = i * 2;
                Quad(tris, a, a + 2, a + 3, a + 1, h > 0);
            }
            if (caps)
            {
                Cap(verts, tris, r0, 0, seg, false);
                Cap(verts, tris, r1, h, seg, true);
            }
            return Finish(verts, tris, "Frustum");
        }

        /// <summary>
        /// Струя вниз от y=0 до y=−1: радиус r(t) = r0·(1 + grow·t^power), t — доля длины. UV.y = t — по нему
        /// градиент яркости. Без торцов: срез закрыт соплом, хвост гаснет в ноль по градиенту.
        /// </summary>
        public static Mesh Plume(float r0, float grow, float power, int seg, int rings)
        {
            var verts = new List<Vector3>();
            var uv = new List<Vector2>();
            var tris = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                for (int k = 0; k <= rings; k++)
                {
                    float t = (float)k / rings;
                    float r = Mathf.Max(0.02f, r0 * (1 + grow * Mathf.Pow(t, power)));
                    verts.Add(d * r - Vector3.up * t);
                    uv.Add(new Vector2((float)i / seg, t));
                }
            }
            int rc = rings + 1;
            for (int i = 0; i < seg; i++)
            for (int k = 0; k < rings; k++)
            {
                int a = i * rc + k, b = (i + 1) * rc + k;
                Quad(tris, a, b, b + 1, a + 1, false);
            }
            var m = new Mesh { name = "Plume" };
            m.SetVertices(verts);
            m.SetUVs(0, uv);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Обтекатель: цилиндр на 55 % длины + оживальный нос.</summary>
        public static Mesh Fairing(float r, float len, int seg)
        {
            const int rings = 10;
            const float cyl = 0.55f;
            var prof = new List<Vector2> { new Vector2(r, 0), new Vector2(r, len * cyl) };
            for (int k = 1; k <= rings; k++)
            {
                float t = (float)k / rings;
                prof.Add(new Vector2(r * Mathf.Sqrt(Mathf.Max(0, 1 - t * t)), len * (cyl + (1 - cyl) * t)));
            }
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int pc = prof.Count;
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                foreach (var p in prof) verts.Add(d * p.x + Vector3.up * p.y);
            }
            for (int i = 0; i < seg; i++)
            for (int k = 0; k < pc - 1; k++)
            {
                int a = i * pc + k, b = (i + 1) * pc + k;
                Quad(tris, a, b, b + 1, a + 1, true);
            }
            Cap(verts, tris, r, 0, seg, false);
            return Finish(verts, tris, "Fairing");
        }

        static void Quad(List<int> t, int a, int b, int c, int d, bool outward)
        {
            // Порядок обхода Unity — по часовой с лицевой стороны; при h < 0 профиль идёт вниз и нормаль переворачивается.
            if (outward) { t.Add(a); t.Add(d); t.Add(c); t.Add(a); t.Add(c); t.Add(b); }
            else { t.Add(a); t.Add(c); t.Add(d); t.Add(a); t.Add(b); t.Add(c); }
        }

        static void Cap(List<Vector3> v, List<int> t, float r, float y, int seg, bool up)
        {
            if (r <= 0) return;
            int c = v.Count;
            v.Add(new Vector3(0, y, 0));
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                v.Add(new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r));
            }
            for (int i = 0; i < seg; i++)
            {
                if (up) { t.Add(c); t.Add(c + i + 2); t.Add(c + i + 1); }
                else { t.Add(c); t.Add(c + i + 1); t.Add(c + i + 2); }
            }
        }

        static Mesh Finish(List<Vector3> v, List<int> t, string name)
        {
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
