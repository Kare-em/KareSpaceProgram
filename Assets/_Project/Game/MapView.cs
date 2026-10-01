using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Карта (GDD §9.6, клавиша M): коники прогноза u.PredictActive() до 256 точек на патч, Ap/Pe,
    /// смены SOI. Масштаб истинный — сжатие §2.7 на карте выключено, экспозиция фиксированная и без
    /// bloom (SkyController). Камера облетает тело, вокруг которого летит борт.
    /// Ниже верха атмосферы прогноз Core обрывается (дальше только физика) — карта дорисовывает
    /// баллистику без сопротивления до поверхности и ставит «Падение»: иначе суборбита выглядит
    /// оборванной линией, и непонятно, куда летишь.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public sealed class MapView : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }
        static MapView instance;

        public Camera Camera;
        [Tooltip("HDRP/Unlit; цвет линий задаётся здесь.")]
        public Material LineMaterial;

        /// <summary>Точек на патч (§9.6).</summary>
        const int Points = 256;
        /// <summary>Яркость линий: _UnlitColor HDRP не проходит экспозицию (1 = белый на экране),
        /// поэтому не ниты, а доля белого. Больше 1 — ушло бы в пересвет и bloom.</summary>
        const float LineBrightness = 0.9f;
        /// <summary>Толщина линий — доля расстояния камеры до тела: ≈ 3 px при 1080 и FOV 60°.</summary>
        const float LineWidth = 0.004f;
        /// <summary>Наклон камеры карты над бортом при открытии, °: видно и борт, и плоскость орбиты.</summary>
        const float ViewTilt = 25;
        /// <summary>Метка за диском тела — полупрозрачная: точка падения на обратной стороне просвечивала сквозь Землю.</summary>
        const float HiddenAlpha = 0.35f;
        /// <summary>Пересчёт прогноза, с: он дорогой (поиск встреч), а коника за 0,25 с не меняется.</summary>
        const float PredictPeriod = 0.25f;

        static readonly Color[] PatchColors =
        {
            new Color(0.3f, 0.8f, 1f), new Color(1f, 0.7f, 0.2f), new Color(0.6f, 1f, 0.4f), new Color(1f, 0.4f, 0.8f),
        };
        /// <summary>Баллистика внутри атмосферы — красная: это оценка без сопротивления.</summary>
        static readonly Color DescentColor = new Color(1f, 0.35f, 0.25f);
        static readonly Color AtmosphereColor = new Color(0.35f, 0.55f, 1f, 1f) * 0.6f;

        float distance, yaw = 20, pitch = 50;
        float near0, far0;
        readonly List<LineRenderer> lines = new List<LineRenderer>();
        List<OrbitPatch> patches;
        float nextPredict;
        readonly Vector3[] buf = new Vector3[Points + 1];
        readonly List<Mark> marks = new List<Mark>();
        readonly List<Rect> placed = new List<Rect>();
        GUIStyle markStyle;

        struct Mark { public Vector3 World; public string Text; public Color Color; }

        void Awake()
        {
            instance = this;
            IsOpen = false;
        }

        void OnDestroy()
        {
            if (instance == this) { instance = null; IsOpen = false; BodyRenderer.Compression = true; }
        }

        public static void Toggle()
        {
            if (instance == null) return;
            IsOpen = !IsOpen;
            BodyRenderer.Compression = !IsOpen;
            instance.OnToggle();
        }

        void OnToggle()
        {
            var u = GameBootstrap.U;
            if (Camera == null) Camera = Camera.main;
            if (IsOpen)
            {
                near0 = Camera.nearClipPlane;
                far0 = Camera.farClipPlane;
                var v = u.Active;
                distance = (float)System.Math.Max(v.Body.Radius * 4, v.Position.magnitude * 2.5);
                // Камера — со стороны борта и чуть сверху: иначе борт и вход в атмосферу часто оказывались у лимба
                // или за планетой, и траектория читалась наполовину.
                var pole = FloatingOrigin.DirToUnity(v.Body.Orientation * Vector3d.forward);
                var f = -(Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up, pole)) * FloatingOrigin.DirToUnity(v.Position).normalized);
                pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(f.y, -1, 1)) * Mathf.Rad2Deg + ViewTilt, -89, 89);
                yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                nextPredict = 0;
            }
            else
            {
                Camera.nearClipPlane = near0;
                Camera.farClipPlane = far0;
                foreach (var l in lines) l.gameObject.SetActive(false);
            }
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (!IsOpen || u?.Active == null || Camera == null) return;
            var v = u.Active;
            var body = v.Body;

            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxis("Mouse X") * 3;
                pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 3, -89, 89);
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0) distance = Mathf.Clamp(distance * Mathf.Pow(0.85f, wheel), (float)body.Radius * 1.2f, 5e11f);

            // Ось карты — полюс тела, чтобы экватор лежал «горизонтально».
            var pole = FloatingOrigin.DirToUnity(body.Orientation * Vector3d.forward);
            var rot = Quaternion.FromToRotation(Vector3.up, pole) * Quaternion.Euler(pitch, yaw, 0);
            var center = FloatingOrigin.ToUnity(body.Position);
            Camera.transform.SetPositionAndRotation(center - rot * Vector3.forward * distance, rot);
            Camera.nearClipPlane = Mathf.Max(1, distance * 1e-4f);
            Camera.farClipPlane = Mathf.Max(distance * 20, 1e9f);

            if (Time.unscaledTime >= nextPredict)
            {
                nextPredict = Time.unscaledTime + PredictPeriod;
                patches = v.Alive && !v.IsLanded ? u.PredictActive() : null;
            }
            Draw(u, v);
        }

        void Draw(Universe u, Vessel v)
        {
            int used = 0;
            marks.Clear();
            marks.Add(new Mark { World = FloatingOrigin.ToUnity(FloatingOrigin.WorldP(v)), Text = "▲ " + v.Name, Color = Color.white });
            float width = distance * LineWidth;

            if (patches != null)
            {
                for (int i = 0; i < patches.Count; i++)
                {
                    var p = patches[i];
                    var col = PatchColors[i % PatchColors.Length];
                    int n = Sample(p.Orbit, p.Body, System.Math.Max(p.StartTime, u.Time), EndOf(p, u.Time), buf);
                    SetLine(used++, n, col, width);
                    MarkApsides(p, u.Time, col);

                    if (p.EndType == TransitionType.Atmosphere) Descent(p, ref used, width);
                    else if (p.NextBody != null && !double.IsInfinity(p.EndTime))
                        marks.Add(new Mark { World = At(p, p.EndTime), Text = $"SOI → {p.NextBody.Name}", Color = col });
                }
            }
            // Кольцо верха атмосферы в плоскости орбиты — видно, на какой высоте начнётся торможение.
            if (v.Body.HasAtmosphere && v.Alive && !v.IsLanded)
            {
                var o = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, u.Time);
                if (Ring(v.Body, o.Normal, v.Body.Radius + v.Body.AtmosphereTop, buf) is int n)
                    SetLine(used++, n, AtmosphereColor, width * 0.5f);
            }
            for (int i = used; i < lines.Count; i++) lines[i].gameObject.SetActive(false);
        }

        /// <summary>Вход в атмосферу: метка и баллистика до поверхности (оценка без сопротивления).</summary>
        void Descent(OrbitPatch p, ref int used, float width)
        {
            var o = p.Orbit;
            marks.Add(new Mark { World = At(p, p.EndTime), Text = $"Вход в атмосферу {p.Body.AtmosphereTop / 1000:0} км", Color = DescentColor });
            double tHit = o.NextTimeAtRadius(p.Body.Radius, p.EndTime, false);
            if (double.IsNaN(tHit) || tHit <= p.EndTime)
            {
                marks.Add(new Mark { World = At(p, o.TimeOfTrueAnomaly(0, p.EndTime)), Text = $"Pe {(o.PeriapsisRadius - p.Body.Radius) / 1000:0} км (в атмосфере)", Color = DescentColor });
                return;
            }
            int n = Sample(o, p.Body, p.EndTime, tHit, buf);
            SetLine(used++, n, DescentColor, width);
            marks.Add(new Mark { World = At(p, tHit), Text = "✕ Падение (без учёта сопротивления)", Color = DescentColor });
        }

        static double EndOf(OrbitPatch p, double now)
        {
            var o = p.Orbit;
            double t0 = System.Math.Max(p.StartTime, now);
            double t1 = p.EndTime;
            if (o.IsElliptic) t1 = System.Math.Min(t1, t0 + o.Period);
            if (double.IsInfinity(t1) || double.IsNaN(t1))
            {
                double rMax = double.IsInfinity(p.Body.SoiRadius) ? o.PeriapsisRadius * 50 : p.Body.SoiRadius;
                t1 = o.NextTimeAtRadius(rMax, t0, true);
                if (double.IsNaN(t1) || double.IsInfinity(t1)) t1 = t0 + 30 * Constants.Day;
            }
            return t1;
        }

        void MarkApsides(OrbitPatch p, double now, Color col)
        {
            var o = p.Orbit;
            double after = System.Math.Max(p.StartTime, now);
            double end = p.EndType == TransitionType.Atmosphere ? p.EndTime : EndOf(p, now);
            double tPe = o.TimeOfTrueAnomaly(0, after);
            if (tPe <= end) marks.Add(new Mark { World = At(p, tPe), Text = $"Pe {(o.PeriapsisRadius - p.Body.Radius) / 1000:0} км", Color = col });
            if (o.IsElliptic)
            {
                double tAp = o.TimeOfTrueAnomaly(System.Math.PI, after);
                if (tAp <= end) marks.Add(new Mark { World = At(p, tAp), Text = $"Ap {(o.ApoapsisRadius - p.Body.Radius) / 1000:0} км", Color = col });
            }
        }

        /// <summary>Отрезок камера → точка пересекает диск тела (точка на обратной стороне).</summary>
        bool Occluded(CelestialBody body, Vector3 world)
        {
            var o = Camera.transform.position;
            var c = FloatingOrigin.ToUnity(body.Position);
            Vector3 d = world - o, oc = o - c;
            float a = Vector3.Dot(d, d), b = Vector3.Dot(oc, d), cc = Vector3.Dot(oc, oc) - (float)(body.Radius * body.Radius * 0.998);
            float disc = b * b - a * cc;
            if (disc <= 0) return false;
            float t = (-b - Mathf.Sqrt(disc)) / a;
            return t > 0 && t < 0.999f;
        }

        static Vector3 At(OrbitPatch p, double t) => FloatingOrigin.ToUnity(p.Body.Position + p.Orbit.PositionAt(t));

        /// <summary>Точки коники в Unity относительно текущего положения её тела (коника рисуется в системе тела).</summary>
        static int Sample(KeplerOrbit o, CelestialBody body, double t0, double t1, Vector3[] outPts)
        {
            var bodyU = body.Position;
            int n = outPts.Length;
            for (int i = 0; i < n; i++)
            {
                double t = t0 + (t1 - t0) * i / (n - 1);
                outPts[i] = FloatingOrigin.ToUnity(bodyU + o.PositionAt(t));
            }
            return n;
        }

        static int? Ring(CelestialBody body, Vector3d normal, double radius, Vector3[] outPts)
        {
            if (normal.sqrMagnitude < 1e-12) return null;
            var e1 = Vector3d.AnyPerpendicular(normal).normalized;
            var e2 = Vector3d.Cross(normal.normalized, e1);
            int n = outPts.Length;
            for (int i = 0; i < n; i++)
            {
                double a = 2 * System.Math.PI * i / (n - 1);
                outPts[i] = FloatingOrigin.ToUnity(body.Position + (e1 * System.Math.Cos(a) + e2 * System.Math.Sin(a)) * radius);
            }
            return n;
        }

        void SetLine(int i, int n, Color col, float width)
        {
            var line = GetLine(i);
            line.sharedMaterial.SetColor("_UnlitColor", col * LineBrightness);
            line.positionCount = n;
            line.SetPositions(buf);
            line.widthMultiplier = width;
            line.gameObject.SetActive(true);
        }

        LineRenderer GetLine(int i)
        {
            while (lines.Count <= i)
            {
                var go = new GameObject($"Orbit {lines.Count}");
                go.transform.SetParent(transform, false);
                var l = go.AddComponent<LineRenderer>();
                l.useWorldSpace = true;
                l.numCapVertices = 0;
                l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                l.receiveShadows = false;
                l.sharedMaterial = LineMaterial != null ? new Material(LineMaterial) : new Material(Shader.Find("HDRP/Unlit"));
                lines.Add(l);
            }
            return lines[i];
        }

        void OnGUI()
        {
            var u = GameBootstrap.U;
            if (!IsOpen || u?.Active == null || Camera == null) return;
            if (markStyle == null)
                markStyle = new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(6, 6, 2, 2) };
            // Подписи с подложкой и раздвижкой: Ap/Pe/вход в атмосферу у суборбиты лежат рядом и сливались.
            placed.Clear();
            foreach (var m in marks)
            {
                var sp = Camera.WorldToScreenPoint(m.World);
                if (sp.z <= 0) continue;
                var size = markStyle.CalcSize(new GUIContent(m.Text));
                var r = new Rect(sp.x + 8, Screen.height - sp.y - size.y * 0.5f, size.x, size.y);
                for (int k = 0; k < placed.Count; k++)
                    if (placed[k].Overlaps(r)) { r.y = placed[k].yMax + 2; k = -1; }
                placed.Add(r);
                var dot = new Rect(sp.x - 3, Screen.height - sp.y - 3, 6, 6);
                float alpha = Occluded(u.Active.Body, m.World) ? HiddenAlpha : 1;
                GUI.color = new Color(m.Color.r, m.Color.g, m.Color.b, alpha);
                GUI.DrawTexture(dot, Texture2D.whiteTexture);
                // Текст — светлее цвета метки: тёмно-красный на тёмной подложке не читался.
                GUI.color = new Color(1, 1, 1, alpha);
                markStyle.normal.textColor = Color.Lerp(m.Color, Color.white, 0.4f);
                GUI.Label(r, m.Text, markStyle);
            }
            GUI.color = Color.white;
            markStyle.normal.textColor = Color.white;
            var hint = "КАРТА · ПКМ — вращать, колесо — масштаб, M — назад";
            float hw = markStyle.CalcSize(new GUIContent(hint)).x;
            GUI.Label(new Rect((Screen.width - hw) * 0.5f, Screen.height - 165, hw, 22), hint, markStyle); // над нижними панелями HUD
        }
    }
}
