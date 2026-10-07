using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Аэродинамические эффекты полёта в плотной атмосфере (GDD §4.6, §9.5): конденсационный конус Прандтля — Глауэрта
    /// на трансзвуке и концевые вихри крыльев на больших углах атаки. Это пар, а не свечение: материал — освещённый
    /// прозрачный дым (GameBootstrap.SmokeMaterial, как у шлейфа ExhaustTrail), поэтому днём он белый на солнце, ночью
    /// тёмный и в экспозицию не вмешивается (в отличие от аддитивной плазмы). Физику не трогает — только вид по
    /// Vessel.Mach, DynamicPressure, Density и AngleOfAttack.
    /// </summary>
    public sealed partial class VesselView
    {
        /// <summary>Материал пара: GameBootstrap.SmokeMaterial (HDRP/Lit, прозрачный, без тумана на прозрачных).</summary>
        public static Material VaporMaterial;

        /// <summary>
        /// Конус пара: окно по Маху (вспыхивает к 0,95, держится до звука, гаснет к 1,15 — за скачком воздух снова
        /// сухой), сила — по скоростному напору и плотности (влажный плотный воздух у земли). Пара: ConeFull — напор
        /// полной силы; у Су/МиГ на малой высоте это 30–40 кПа, у ракеты на max Q — 30 кПа.
        /// </summary>
        const float ConeMachIn = 0.86f, ConeMachPeak = 0.96f, ConeMachHold = 1.02f, ConeMachOut = 1.15f;
        const float ConeFull = 25e3f, ConeDensityFull = 0.5f, ConeAlpha = 0.6f;
        /// <summary>Наибольший тангенс полуугла конуса (≈ 50°): у звука угол Маха 90°, конус выродился бы в диск.</summary>
        const float ConeMaxTan = 1.2f;
        /// <summary>
        /// Концевые вихри: видимы при α от VortexAoA до полной силы через VortexAoARange, напоре от VortexQ и не выше
        /// VortexMaxMach (за звуком давление в ядре вихря не падает так глубоко — пар не выпадает).
        /// </summary>
        const float VortexAoA = 6, VortexAoARange = 10, VortexQ = 12e3f, VortexMaxMach = 1.6f, VortexAlpha = 0.5f;
        /// <summary>Крылья мельче этого (рули, гребни) вихрей не рисуют. Пара: WingSheathMinArea — порог оболочки плазмы.</summary>
        const float VortexMinArea = 2;
        const int VortexPoints = 8, ConeSeg = 48, ConeRings = 6;

        struct Tip { public Transform Part; public Vector3 Local; public LineRenderer Line; public float Width; }

        readonly List<Tip> tips = new List<Tip>();
        Transform vaporCone;
        Renderer vaporR;
        float aeroRadius;
        /// <summary>Копии материала пара этого борта: Rebuild сносит ноды, а материалы — нет, освобождаем сами.</summary>
        readonly List<Material> aeroMats = new List<Material>();
        static Mesh coneMesh;
        static Texture2D coneTex, vortexTex;

        void AddAeroFx()
        {
            tips.Clear();
            vaporCone = null;
            vaporR = null;
            foreach (var old in aeroMats) Destroy(old);
            aeroMats.Clear();
            if (VaporMaterial == null) return;
            var root = new GameObject("Aero").transform;
            root.SetParent(transform, false);

            // Габарит поперёк потока: радиус корпуса или полуразмах крыла — конус обнимает и крылья.
            aeroRadius = 0;
            foreach (var p in parts)
            {
                var s = Vessel.Design.Sections[p.Index];
                aeroRadius = Mathf.Max(aeroRadius, (float)s.Radius + p.Radial.magnitude);
                if (s.Wings == null) continue;
                foreach (var w in s.Wings)
                {
                    if (w.Vertical || w.Span <= 0) continue;
                    aeroRadius = Mathf.Max(aeroRadius, (float)w.Span * 0.5f + p.Radial.magnitude);
                    if (w.Area < VortexMinArea) continue;
                    AddTips(root, p, w);
                }
            }

            var cone = new GameObject("Vapor Cone");
            cone.transform.SetParent(root, false);
            cone.AddComponent<MeshFilter>().sharedMesh = ConeMesh();
            var mr = cone.AddComponent<MeshRenderer>();
            var m = new Material(VaporMaterial) { name = "Vapor Cone (runtime)" };
            m.SetTexture("_BaseColorMap", ConeTexture());
            aeroMats.Add(m);
            mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            vaporCone = cone.transform;
            vaporR = mr;
            cone.SetActive(false);
        }

        /// <summary>Сход вихря — задняя кромка законцовки, по той же схеме, что WingMesh и AddWings (оболочка плазмы).</summary>
        void AddTips(Transform root, Part p, WingDef w)
        {
            float c = (float)w.MeanChord, half = (float)w.Span * 0.5f;
            float sweep = Mathf.Tan((float)w.Sweep * Mathf.Deg2Rad);
            float inc = (float)w.Incidence * Mathf.Deg2Rad, dih = (float)w.Dihedral * Mathf.Deg2Rad;
            var rootQ = new Vector3(-(float)w.Offset, (float)w.Height, 0);
            var ch = new Vector3(-Mathf.Sin(inc), Mathf.Cos(inc), 0) * c;
            var m = new Material(VaporMaterial) { name = "Wingtip Vortex (runtime)" };
            m.SetTexture("_BaseColorMap", VortexTexture());
            aeroMats.Add(m);
            for (int side = -1; side <= 1; side += 2)
            {
                var span = new Vector3(-Mathf.Sin(dih), 0, side * Mathf.Cos(dih));
                var tip = rootQ + span * half + Vector3.down * (half * sweep) - ch * 0.75f;
                var go = new GameObject("Vortex");
                go.transform.SetParent(root, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = m;
                lr.useWorldSpace = true;
                lr.alignment = LineAlignment.View;
                lr.textureMode = LineTextureMode.Stretch;
                lr.generateLightingData = true; // освещённый материал: без нормалей лента чёрная
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.positionCount = VortexPoints;
                // Ядро вихря тонкое у законцовки и расплывается вниз по потоку.
                lr.widthCurve = new AnimationCurve(new Keyframe(0, 0.4f), new Keyframe(0.3f, 0.8f), new Keyframe(1, 1.4f));
                lr.enabled = false;
                tips.Add(new Tip { Part = p.Tr, Local = tip, Line = lr, Width = Mathf.Clamp(0.03f * (float)w.Span, 0.12f, 0.5f) });
            }
        }

        void UpdateAeroFx(Vector3 airflow, bool plasmaOn)
        {
            if (vaporCone == null) return;
            float mach = (float)Vessel.Mach, q = (float)Vessel.DynamicPressure;
            float wet = Mathf.Clamp01((float)Vessel.Density / ConeDensityFull);
            bool moving = Vessel.SurfaceSpeed > 30 && !plasmaOn;

            // Конус Прандтля — Глауэрта.
            float window = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(ConeMachIn, ConeMachPeak, mach))
                         * (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(ConeMachHold, ConeMachOut, mach)));
            float kc = moving ? window * Mathf.Clamp01(q / ConeFull) * wet : 0;
            vaporCone.gameObject.SetActive(kc > 0.01f);
            if (kc > 0.01f)
            {
                // Кромка — у миделя (центр масс сдвинут к носу от него мало — для конуса этого хватает), раструб — по потоку.
                // Полуугол: у звука — ConeMaxTan, за звуком сужается к углу Маха sin μ = 1/M.
                float tanMu = mach > 1.01f ? 1 / Mathf.Sqrt(mach * mach - 1) : ConeMaxTan;
                tanMu = Mathf.Min(tanMu, ConeMaxTan);
                float r0 = aeroRadius * 1.05f;
                vaporCone.SetPositionAndRotation(transform.position, Quaternion.LookRotation(-airflow));
                // Меш: радиус 1 + z на длине 1. Масштаб — r0 поперёк и r0 / tg μ вдоль, т. е. наклон образующей — tg μ.
                var ls = new Vector3(r0, r0, r0 * 0.9f / tanMu);
                vaporCone.localScale = new Vector3(ls.x / transform.lossyScale.x, ls.y / transform.lossyScale.y, ls.z / transform.lossyScale.z);
                mpb.Clear();
                mpb.SetColor("_BaseColor", new Color(1, 1, 1, ConeAlpha * kc * Flicker(0.15f, 31)));
                vaporR.SetPropertyBlock(mpb);
            }

            // Концевые вихри.
            float aoa = (float)Vessel.AngleOfAttack;
            float kv = moving && mach < VortexMaxMach
                ? Mathf.Clamp01((aoa - VortexAoA) / VortexAoARange) * Mathf.Clamp01(q / VortexQ) * Mathf.Lerp(0.3f, 1, wet)
                : 0;
            float len = Mathf.Clamp((float)Vessel.SurfaceSpeed * 0.07f, 4, 35) * (0.4f + 0.6f * kv);
            foreach (var t in tips)
            {
                if (t.Line == null) continue;
                bool on = kv > 0.02f && t.Part != null && t.Part.gameObject.activeInHierarchy;
                t.Line.enabled = on;
                if (!on) continue;
                var from = t.Part.TransformPoint(t.Local);
                for (int i = 0; i < VortexPoints; i++)
                    t.Line.SetPosition(i, from - airflow * (len * i / (VortexPoints - 1)));
                t.Line.widthMultiplier = t.Width;
                mpb.Clear();
                mpb.SetColor("_BaseColor", new Color(1, 1, 1, VortexAlpha * kv * Flicker(0.2f, 43)));
                t.Line.SetPropertyBlock(mpb);
            }
        }

        /// <summary>Раструб: кольца по z ∈ [0, 1], радиус 1 + z; UV — (по кругу, вдоль). Нормали наружу (поперёк образующей).</summary>
        static Mesh ConeMesh()
        {
            if (coneMesh != null) return coneMesh;
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();
            for (int j = 0; j <= ConeRings; j++)
            {
                float z = j / (float)ConeRings, r = 1 + z;
                for (int k = 0; k <= ConeSeg; k++)
                {
                    float a = 2 * Mathf.PI * k / ConeSeg;
                    var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                    v.Add(d * r + Vector3.forward * z);
                    n.Add((d - Vector3.forward).normalized);
                    uv.Add(new Vector2(k / (float)ConeSeg, z));
                }
            }
            int row = ConeSeg + 1;
            for (int j = 0; j < ConeRings; j++)
                for (int k = 0; k < ConeSeg; k++)
                {
                    int a = j * row + k, b = a + row;
                    tri.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
                }
            coneMesh = new Mesh { name = "Vapor Cone" };
            coneMesh.SetVertices(v);
            coneMesh.SetNormals(n);
            coneMesh.SetUVs(0, uv);
            coneMesh.SetTriangles(tri, 0);
            coneMesh.RecalculateBounds();
            return coneMesh;
        }

        /// <summary>Плотная кромка у скачка и тающий раструб; по кругу — продольные пряди (шум Перлина).</summary>
        static Texture2D ConeTexture()
        {
            if (coneTex != null) return coneTex;
            const int w = 128, h = 64;
            coneTex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "Vapor Cone", wrapMode = TextureWrapMode.Repeat };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float z = y / (h - 1f);
                    float edge = Mathf.SmoothStep(0, 1, z / 0.06f) * Mathf.Exp(-z * 3.2f) * (1 - z);
                    // Шум по кругу — периодичный: берём окружность в плоскости шума, чтобы шва на u = 0/1 не было.
                    float a = 2 * Mathf.PI * x / w;
                    float streak = 0.55f + 0.45f * Mathf.PerlinNoise(3 + 4 * Mathf.Cos(a), 3 + 4 * Mathf.Sin(a) + z * 2);
                    coneTex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(edge * streak * 1.4f)));
                }
            coneTex.wrapModeV = TextureWrapMode.Clamp;
            coneTex.Apply();
            return coneTex;
        }

        /// <summary>Лента вихря: u — вдоль (от законцовки гаснет), v — поперёк (мягкие края).</summary>
        static Texture2D VortexTexture()
        {
            if (vortexTex != null) return vortexTex;
            const int w = 64, h = 16;
            vortexTex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "Wingtip Vortex", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = x / (w - 1f), s = 2 * y / (h - 1f) - 1;
                    float a = Mathf.SmoothStep(0, 1, u / 0.04f) * Mathf.Pow(1 - u, 1.6f) * (1 - s * s);
                    vortexTex.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            vortexTex.Apply();
            return vortexTex;
        }
    }
}
