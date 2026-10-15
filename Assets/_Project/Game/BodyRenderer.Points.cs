using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kare.Space.Game
{
    /// <summary>
    /// Тела издалека — светящиеся точки (§9.3): диск меньше пикселя пропадает в сглаживании, и Юпитер с Земли или
    /// Ганимед на подлёте не видно вовсе. Яркость точки — по видимой звёздной величине (альбедо, фаза, расстояния до
    /// Солнца и камеры), в долях белого экрана мимо экспозиции: в нитах отражённый свет планеты при EV 12+ невидим.
    /// </summary>
    public sealed partial class BodyRenderer
    {
        /// <summary>Геометрическое альбедо (NASA Planetary Fact Sheets). Нет в таблице — DefaultAlbedo.</summary>
        static readonly Dictionary<string, float> Albedo = new Dictionary<string, float>
        {
            ["mercury"] = 0.142f, ["venus"] = 0.689f, ["earth"] = 0.434f, ["moon"] = 0.12f, ["mars"] = 0.17f,
            ["phobos"] = 0.071f, ["deimos"] = 0.068f, ["jupiter"] = 0.538f, ["io"] = 0.63f, ["europa"] = 0.67f,
            ["ganymede"] = 0.43f, ["callisto"] = 0.22f, ["saturn"] = 0.499f, ["enceladus"] = 1.38f, ["titan"] = 0.22f,
            ["uranus"] = 0.488f, ["neptune"] = 0.442f, ["triton"] = 0.76f, ["pluto"] = 0.52f, ["charon"] = 0.41f,
            // Ядра комет — голые, без комы (кому и хвосты рисует CometView): из-за альбедо 0,04 точкой почти не видны.
            ["halley"] = 0.04f, ["halebopp"] = 0.04f,
        };
        const float DefaultAlbedo = 0.3f;
        /// <summary>Звёздная величина Солнца с 1 а.е.: m = SunMagnitude − 2,5·lg(p·(R/d)²·Φ(α)·(1 а.е./r)²).
        /// Проверка: Юпитер в противостоянии — −2,8m (справочно −2,7m).</summary>
        const double SunMagnitude = -26.74;
        /// <summary>
        /// Шкала видимости: ярче PointMagFull — точка в полную силу, к PointMagLimit гаснет линейно по величине
        /// (глаз воспринимает блеск логарифмически). С орбиты Земли: Марс, Юпитер, Сатурн — ярко, Уран (5,7m) едва,
        /// Нептун (7,8m) не виден; у Юпитера галилеевы спутники (−3…−4m) — ярче всех звёзд.
        /// </summary>
        const double PointMagFull = 0, PointMagLimit = 7;
        /// <summary>Диаметр точки, px: тусклая — PointMinPx, яркая — PointMaxPx (ореол гауссианы, ядро ≈ треть).</summary>
        const float PointMinPx = 3, PointMaxPx = 7;
        /// <summary>Яркость ядра в полную силу, доля белого: чуть выше 1 — лёгкий bloom, как у ярких звёзд неба.</summary>
        const float PointBrightness = 1.6f;
        /// <summary>
        /// Переход точки в диск: пока диск меньше PointDiscFull px, точка в полную силу, к PointDiscGone — гаснет.
        /// Пара: PointMaxPx — точка гаснет раньше, чем диск её перерастёт, без «нимба» поверх диска.
        /// </summary>
        const float PointDiscFull = 1.5f, PointDiscGone = 5;
        /// <summary>Днём в атмосфере точка почти не видна: остаётся эта доля (Венера днём — на пределе глаза).
        /// Пара: SkyController.AirWeight — та же шкала высоты, на которой небо чернеет.</summary>
        const float PointDayFloor = 0.08f;
        /// <summary>Точка выносится перед диском на эту долю радиуса — иначе сфера закрыла бы её по глубине.</summary>
        const double PointLift = 1.05;
        const int DotTexRes = 32;

        sealed class Point
        {
            public CelestialBody Body;
            public Transform Tr;
            public MeshRenderer Rend;
            public float Albedo;
            public Color Tint;
        }

        readonly List<Point> points = new List<Point>();
        MaterialPropertyBlock pointMpb;
        static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");

        void BuildPoints()
        {
            var plume = GameBootstrap.Instance != null ? GameBootstrap.Instance.PlumeMaterial : null;
            if (plume == null) return;
            // Копия факела (HDRP/Unlit, аддитив, двусторонний, без тумана); ключ карты — руками (рантайм не валидируется).
            var mat = new Material(plume) { name = "Body Point (runtime)" };
            mat.SetTexture("_EmissiveColorMap", DotTexture());
            mat.EnableKeyword("_EMISSIVE_COLOR_MAP");
            mat.SetFloat("_EmissiveExposureWeight", 0);
            mat.SetFloat("_EnableFogOnTransparent", 0);
            var quad = QuadMesh();
            pointMpb = new MaterialPropertyBlock();
            foreach (var e in entries)
            {
                var go = new GameObject($"Point {e.Body.Id}");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = quad;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                go.SetActive(false);
                points.Add(new Point
                {
                    Body = e.Body, Tr = go.transform, Rend = mr,
                    Albedo = Albedo.TryGetValue(e.Body.Id, out var a) ? a : DefaultAlbedo,
                    Tint = MeanTint(e.Body.Id),
                });
            }
        }

        void UpdatePoints(Vessel v)
        {
            var cam = Camera.main;
            bool show = cam != null && Compression && !MapView.IsOpen && points.Count > 0;
            var sun = GameBootstrap.U.System.Get("sun");
            if (!show || sun == null)
            {
                foreach (var p in points) if (p.Tr.gameObject.activeSelf) p.Tr.gameObject.SetActive(false);
                return;
            }
            var camPos = cam.transform.position;
            var camRot = cam.transform.rotation;
            float pxPerRad = cam.pixelHeight / (2 * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            var eye = FloatingOrigin.OriginP + new Vector3d(camPos.x, camPos.y, camPos.z).SwapYZ;
            float day = (float)SunLight.Visible * (1 - SkyController.AirWeight(v));
            float sky = Mathf.Lerp(1, PointDayFloor, day);
            foreach (var p in points)
            {
                var b = p.Body;
                var toBody = b.Position - eye;
                double d = toBody.magnitude;
                float disc = (float)(2 * b.Radius / d * pxPerRad);
                float fade = Mathf.Clamp01((PointDiscGone - disc) / (PointDiscGone - PointDiscFull));
                float s = fade <= 0 ? 0 : (float)Strength(b, p.Albedo, toBody, d, sun.Position - b.Position);
                float glow = s * fade * sky;
                bool on = glow > 1e-3f;
                if (p.Tr.gameObject.activeSelf != on) p.Tr.gameObject.SetActive(on);
                if (!on) continue;
                var center = Project(b, out double k);
                var dir = center - camPos;
                float dist = dir.magnitude;
                // Перед диском: на радиус (в масштабе оболочки) ближе к камере.
                float lift = Mathf.Min((float)(b.Radius * k * PointLift), dist * 0.5f);
                var pos = center - dir / dist * lift;
                float size = Mathf.Lerp(PointMinPx, PointMaxPx, s) * (dist - lift) / pxPerRad;
                p.Tr.SetPositionAndRotation(pos, camRot);
                p.Tr.localScale = Vector3.one * size;
                pointMpb.Clear();
                pointMpb.SetColor(EmissiveColorId, p.Tint * (PointBrightness * glow));
                p.Rend.SetPropertyBlock(pointMpb);
            }
        }

        /// <summary>
        /// Масштаб оболочки §2.7 для точки на расстоянии d (м) от плавающего начала — та же формула, что в Project,
        /// но по самой точке, а не по горизонту тела: хвосты комет (CometView) сжимаются повершинно, направления
        /// из начала сохраняются — угловой вид тот же, а вершины ложатся в ShellStart…ShellStart + ShellWidth.
        /// </summary>
        public static double ShellScale(double d)
        {
            if (!Compression || d <= ShellStart) return 1;
            double x = System.Math.Log(d / ShellStart);
            return (ShellStart + ShellWidth * x / (x + ShellSoftness)) / d;
        }

        /// <summary>Сила точки 0…1 по видимой звёздной величине (шкала PointMagFull…PointMagLimit).</summary>
        static double Strength(CelestialBody b, float albedo, Vector3d toBody, double d, Vector3d toSun)
        {
            double rs = toSun.magnitude;
            // Фазовый угол — между направлениями «тело → Солнце» и «тело → наблюдатель»; фаза — ламбертов шар.
            double alpha = Vector3d.Angle(toSun, -toBody);
            double phase = (System.Math.Sin(alpha) + (System.Math.PI - alpha) * System.Math.Cos(alpha)) / System.Math.PI;
            double au = Constants.AU / rs, rr = b.Radius / d;
            double flux = albedo * rr * rr * phase * au * au;
            if (!(flux > 0)) return 0;
            double m = SunMagnitude - 2.5 * System.Math.Log10(flux);
            return Mathf.Clamp01((float)((PointMagLimit - m) / (PointMagLimit - PointMagFull)));
        }

        /// <summary>Средний цвет карты, нормированный к самому яркому каналу: точка красит, а не тускнеет.</summary>
        static Color MeanTint(string id)
        {
            if (!smallPx.TryGetValue(id, out var sm)) return Color.white;
            double r = 0, g = 0, bl = 0;
            for (int i = 0; i < sm.Px.Length; i += 7) { r += sm.Px[i].r; g += sm.Px[i].g; bl += sm.Px[i].b; }
            double mx = System.Math.Max(r, System.Math.Max(g, bl));
            if (mx <= 0) return Color.white;
            // Пастель: половина белого — у точки цвет читается оттенком, насыщенная выглядела бы маркером HUD.
            return Color.Lerp(Color.white, new Color((float)(r / mx), (float)(g / mx), (float)(bl / mx)), 0.5f);
        }

        /// <summary>Гауссиана: ядро ≈ треть диаметра, ореол спадает к краю в ноль — без ступеньки по краю квада.</summary>
        static Texture2D DotTexture()
        {
            var t = new Texture2D(DotTexRes, DotTexRes, TextureFormat.RGBA32, false, true)
                { name = "Body Point", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[DotTexRes * DotTexRes];
            float c = (DotTexRes - 1) * 0.5f;
            for (int y = 0; y < DotTexRes; y++)
                for (int x = 0; x < DotTexRes; x++)
                {
                    float r = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float val = r >= 1 ? 0 : Mathf.Exp(-r * r * 9) * (1 - r * r);
                    byte k = (byte)Mathf.RoundToInt(val * 255);
                    px[y * DotTexRes + x] = new Color32(k, k, k, k);
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static Mesh QuadMesh()
        {
            var m = new Mesh { name = "Body Point Quad" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f), new Vector3(0.5f, 0.5f), new Vector3(-0.5f, 0.5f) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            // Границы с запасом: квад крошечный, но позиция задаётся каждый кадр — отсечение по ним не нужно.
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 2);
            return m;
        }
    }
}
