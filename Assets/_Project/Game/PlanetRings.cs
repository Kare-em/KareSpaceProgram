using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kare.Space.Game
{
    /// <summary>
    /// Кольца планеты (GDD §9.4): кольцо в экваторе — ребёнок сферы тела, поворот и сжатие оболочки §2.7 берёт
    /// от неё (единица меша — SphereRadius). Свет и тени считает шейдер Kare/Planet Rings HDRP: сюда — только
    /// направление на Солнце в осях меша и освещённость. У плотных колец (Сатурн) — ещё и тень колец на диске.
    /// Шейдеры лежат в Settings/Resources: Shader.Find в сборке находит только включённые в неё, а Resources
    /// включается целиком — без правки FlightSceneBuilder и пересборки сцены.
    /// </summary>
    public sealed class PlanetRings : MonoBehaviour
    {
        const string RingShader = "Kare/Planet Rings HDRP", ShadowShader = "Kare/Ring Shadow HDRP";
        /// <summary>Сегментов по кругу. Внешние вершины — на описанной окружности, внутренние — на самой: шейдер
        /// отсекает пиксели вне [Inner, Outer], и края колец — точные круги при любом числе сегментов.</summary>
        const int Segments = 128;
        /// <summary>Тень на диске — только у колец, которые её заметно дают (τ B ≈ 5; у Урана ε — нить в 60 км).</summary>
        const float ShadowMinTau = 0.5f;
        /// <summary>Сфера тени чуть больше планеты, чтобы лечь поверх неё по глубине; луч всё равно бьётся в истинную.</summary>
        const float ShadowShell = 1.002f;
        const int ShadowLon = 96, ShadowLat = 48;
        /// <summary>Очереди: тень на диске — до колец (кольца ложатся поверх затенённого диска). Пара: Queue в шейдерах.</summary>
        const int RingQueue = 3000, ShadowQueue = RingQueue - 50;
        /// <summary>
        /// Подгонка к диску: диск — HDRP/Lit с картой SSS, она растянута по яркости и даёт диску I/F раза в 2–3
        /// выше реального. Физичные I/F колец (B ≈ 0,45) рядом с ним выходили вчетверо темнее диска (замер
        /// 15.10.2026, Сатурн с 400 тыс. км, фаза 30°: B 29 против диска 60–66 из 255; ×3 — B 72). На снимках
        /// «Кассини» при малой фазе B не темнее диска — множитель 2,5. Пара: альбедо карт тел в BodyRenderer.
        /// </summary>
        const float DiscMatch = 2.5f;

        CelestialBody body;
        Material ring, shadow;

        public static void Attach(Transform sphere, CelestialBody b, double sphereRadius, Texture2D saturnStrip)
        {
            var shader = Shader.Find(RingShader);
            if (shader == null) { Debug.LogWarning($"[Rings] нет шейдера {RingShader}"); return; }
            var prof = RingProfiles.Build(b.Id, saturnStrip);
            if (prof == null) return;

            var go = new GameObject("Rings");
            go.transform.SetParent(sphere, false);
            var pr = go.AddComponent<PlanetRings>();
            pr.body = b;
            float rIn = (float)(prof.Inner / sphereRadius), rOut = (float)(prof.Outer / sphereRadius);
            go.AddComponent<MeshFilter>().sharedMesh = Annulus(rIn, rOut);
            var mr = go.AddComponent<MeshRenderer>();
            pr.ring = new Material(shader) { name = $"Rings {b.Id}", renderQueue = RingQueue };
            SetProfile(pr.ring, prof, rIn, rOut, (float)(b.Radius / sphereRadius));
            mr.sharedMaterial = pr.ring;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            var ss = Shader.Find(ShadowShader);
            if (ss != null && prof.TauScale >= ShadowMinTau)
            {
                var sg = new GameObject("Ring Shadow");
                sg.transform.SetParent(sphere, false);
                // Радиус — в вершинах, а не в localScale: шейдер считает в осях меша, они должны совпасть с осями колец.
                sg.AddComponent<MeshFilter>().sharedMesh = Sphere((float)(b.Radius / sphereRadius * ShadowShell));
                var sr = sg.AddComponent<MeshRenderer>();
                pr.shadow = new Material(ss) { name = $"Ring Shadow {b.Id}", renderQueue = ShadowQueue };
                SetProfile(pr.shadow, prof, rIn, rOut, (float)(b.Radius / sphereRadius));
                sr.sharedMaterial = pr.shadow;
                sr.shadowCastingMode = ShadowCastingMode.Off;
                sr.receiveShadows = false;
            }
        }

        static void SetProfile(Material m, RingProfiles.Profile p, float rIn, float rOut, float planetR)
        {
            m.SetTexture("_TauTex", p.Tau);
            if (m.HasProperty("_ColorTex")) m.SetTexture("_ColorTex", p.Color);
            m.SetFloat("_TauScale", p.TauScale);
            m.SetFloat("_RIn", rIn);
            m.SetFloat("_ROut", rOut);
            m.SetFloat("_PlanetR", planetR);
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (u?.Active == null || body == null) return;
            var sun = u.System.Sun;
            // Направление на Солнце — от самой планеты (на карте борт может быть у Земли), в осях тела:
            // поворот тела берётся из Core, а не из transform — порядок LateUpdate с BodyRenderer не важен.
            var toSun = sun.Position - body.Position;
            double dist = toSun.magnitude;
            var dirOS = Quaternion.Inverse(FloatingOrigin.BodyRotation(body)) * FloatingOrigin.DirToUnity(toSun / dist);
            var sunDir = new Vector4(dirOS.x, dirOS.y, dirOS.z, (float)(sun.Radius / dist));
            // Освещённость — та же, что у направленного света (SunLight: от борта), но без затмения: планета и кольца
            // освещены одинаково, а в тени Сатурна диск гаснет, кольца — нет (вид «Кассини» против Солнца).
            var me = FloatingOrigin.WorldP(FlightView.Main);
            double au = Constants.AU / (sun.Position - me).magnitude;
            var c = Mathf.CorrelatedColorTemperatureToRGB(SunLight.ColorTemperature);
            float lux = (float)(SunLight.IlluminanceAt1Au * au * au) * DiscMatch;
            ring.SetVector("_SunDirOS", sunDir);
            ring.SetVector("_SunE", new Vector4(c.r * lux, c.g * lux, c.b * lux, 0));
            if (shadow != null) shadow.SetVector("_SunDirOS", sunDir);
        }

        void OnDestroy()
        {
            if (ring != null) Destroy(ring);
            if (shadow != null) Destroy(shadow);
        }

        /// <summary>
        /// Проверка колец (MCP, Play): борт — на расстоянии dist, м, от центра тела, азимут az от направления на
        /// Солнце и высота el над плоскостью колец, град (плюс — освещённая сторона); камера смотрит на центр тела.
        /// Скорость обнуляется: за секунды съёмки борт сдвигается на метры, а орбита не нужна.
        /// </summary>
        public static string View(string id, double az, double el, double dist, float fov)
        {
            var u = GameBootstrap.U;
            var b = u?.System.Get(id);
            if (b == null) return "нет тела " + id;
            u.Teleport(b, dist - b.Radius, true);
            var v = u.Active;
            var n = b.Orientation * Vector3d.forward;
            var s = (u.System.Sun.Position - b.Position).normalized;
            double sinB = Vector3d.Dot(n, s);
            var sp = (s - n * sinB).normalized;
            var e2 = Vector3d.Cross(n, sp);
            double a = az * Constants.Deg2Rad, h = el * Constants.Deg2Rad * (sinB >= 0 ? 1 : -1);
            v.Position = (sp * System.Math.Cos(a) + e2 * System.Math.Sin(a)) * (dist * System.Math.Cos(h)) + n * (dist * System.Math.Sin(h));
            v.Velocity = Vector3d.zero;
            var fc = FindFirstObjectByType<FlightCamera>();
            if (fc != null) fc.enabled = false;
            var cam = Camera.main;
            cam.fieldOfView = fov;
            var aim = cam.GetComponent<ViewAim>() ?? cam.gameObject.AddComponent<ViewAim>();
            aim.Body = b;
            return $"Солнце над кольцами {System.Math.Asin(sinB) * Constants.Rad2Deg:0.0}°, борт {v.Position.magnitude / 1e3:0} км";
        }

        /// <summary>Держит камеру у борта (150 м в сторону тела, чтобы борт остался за спиной), взгляд — на центр тела, верх — его полюс.</summary>
        sealed class ViewAim : MonoBehaviour
        {
            public CelestialBody Body;

            void LateUpdate()
            {
                if (Body == null) return;
                var p = BodyRenderer.Project(Body, out _);
                var dir = p.normalized;
                var up = FloatingOrigin.DirToUnity(Body.Orientation * Vector3d.forward);
                transform.SetPositionAndRotation(dir * 150, Quaternion.LookRotation(dir, up));
            }
        }

        static Mesh Annulus(float rIn, float rOut)
        {
            int n = Segments;
            float outer = rOut / Mathf.Cos(Mathf.PI / n);
            var verts = new Vector3[n * 2];
            var tris = new int[n * 6];
            for (int i = 0; i < n; i++)
            {
                float a = 2 * Mathf.PI * i / n, c = Mathf.Cos(a), s = Mathf.Sin(a);
                verts[i * 2] = new Vector3(c * rIn, 0, s * rIn);
                verts[i * 2 + 1] = new Vector3(c * outer, 0, s * outer);
                int k = i * 6, v = i * 2, w = (i + 1) % n * 2;
                tris[k] = v; tris[k + 1] = w; tris[k + 2] = v + 1;
                tris[k + 3] = w; tris[k + 4] = w + 1; tris[k + 5] = v + 1;
            }
            // Внутренний край — хорда внутри окружности rIn: пиксели между ними шейдер отсекает.
            var mesh = new Mesh { name = "Rings", vertices = verts, triangles = tris };
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh Sphere(float radius)
        {
            var verts = new Vector3[(ShadowLon + 1) * (ShadowLat + 1)];
            for (int y = 0; y <= ShadowLat; y++)
            {
                float th = Mathf.PI * y / ShadowLat, sy = Mathf.Cos(th), sr = Mathf.Sin(th);
                for (int x = 0; x <= ShadowLon; x++)
                {
                    float ph = 2 * Mathf.PI * x / ShadowLon;
                    // Многоугольник вписан в сферу — радиус вершин поднят на 1/cos полушага, чтобы грани не ушли под планету.
                    verts[y * (ShadowLon + 1) + x] = new Vector3(Mathf.Cos(ph) * sr, sy, Mathf.Sin(ph) * sr) * (radius / Mathf.Cos(Mathf.PI / ShadowLat));
                }
            }
            var tris = new int[ShadowLon * ShadowLat * 6];
            int k = 0;
            for (int y = 0; y < ShadowLat; y++)
                for (int x = 0; x < ShadowLon; x++)
                {
                    int a = y * (ShadowLon + 1) + x, b = a + ShadowLon + 1;
                    tris[k++] = a; tris[k++] = a + 1; tris[k++] = b;
                    tris[k++] = a + 1; tris[k++] = b + 1; tris[k++] = b;
                }
            var mesh = new Mesh { name = "Ring Shadow", indexFormat = IndexFormat.UInt32, vertices = verts, triangles = tris };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
