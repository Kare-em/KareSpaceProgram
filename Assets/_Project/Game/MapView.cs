using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Карта (GDD §9.6, клавиша M): коники прогноза u.PredictActive() до 256 точек на патч, Ap/Pe,
    /// смены SOI. Масштаб истинный — сжатие §2.7 на карте выключено, экспозиция фиксированная
    /// (SkyController). Камера облетает тело, вокруг которого летит борт.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public sealed class MapView : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }
        static MapView instance;

        public Camera Camera;
        [Tooltip("HDRP/Unlit; цвет линий задаётся здесь, яркость — MapLineNits.")]
        public Material LineMaterial;

        /// <summary>Точек на патч (§9.6).</summary>
        const int Points = 256;
        /// <summary>Яркость линий, нит. Пара: фиксированная экспозиция SkyController.MapEv = 13 —
        /// серое 18 % при EV13 ≈ 1000 нит, линия должна быть заметно ярче.</summary>
        const float MapLineNits = 6000;
        /// <summary>Пересчёт прогноза, с: он дорогой (поиск встреч), а коника за 0,25 с не меняется.</summary>
        const float PredictPeriod = 0.25f;

        static readonly Color[] PatchColors =
        {
            new Color(0.3f, 0.8f, 1f), new Color(1f, 0.7f, 0.2f), new Color(0.6f, 1f, 0.4f), new Color(1f, 0.4f, 0.8f),
        };

        float distance, yaw = 20, pitch = 50;
        float near0, far0;
        readonly List<LineRenderer> lines = new List<LineRenderer>();
        List<OrbitPatch> patches;
        float nextPredict;
        readonly Vector3[] buf = new Vector3[Points + 1];

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
            DrawPatches(u);
        }

        void DrawPatches(Universe u)
        {
            int used = 0;
            if (patches != null)
            {
                foreach (var p in patches)
                {
                    var line = GetLine(used);
                    line.startColor = line.endColor = PatchColors[used % PatchColors.Length];
                    int n = Sample(p, u.Time, buf);
                    line.positionCount = n;
                    line.SetPositions(buf);
                    line.widthMultiplier = distance * 0.002f;
                    line.gameObject.SetActive(true);
                    used++;
                }
            }
            for (int i = used; i < lines.Count; i++) lines[i].gameObject.SetActive(false);
        }

        /// <summary>
        /// Точки патча в Unity относительно текущего положения его тела (коника рисуется в системе тела).
        /// Эллипс — один виток от начала патча, гипербола — до конца патча или границы SOI.
        /// </summary>
        static int Sample(OrbitPatch p, double now, Vector3[] outPts)
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
            var bodyU = p.Body.Position;
            int n = outPts.Length;
            for (int i = 0; i < n; i++)
            {
                double t = t0 + (t1 - t0) * i / (n - 1);
                outPts[i] = FloatingOrigin.ToUnity(bodyU + o.PositionAt(t));
            }
            return n;
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
                var mat = LineMaterial != null ? new Material(LineMaterial) : new Material(Shader.Find("HDRP/Unlit"));
                mat.SetColor("_UnlitColor", PatchColors[lines.Count % PatchColors.Length] * MapLineNits);
                l.sharedMaterial = mat;
                lines.Add(l);
            }
            return lines[i];
        }

        void OnGUI()
        {
            var u = GameBootstrap.U;
            if (!IsOpen || u?.Active == null || Camera == null) return;
            var v = u.Active;
            Label(FloatingOrigin.ToUnity(FloatingOrigin.WorldP(v)), "▲ " + v.Name);
            if (patches == null) return;
            for (int i = 0; i < patches.Count; i++)
            {
                var p = patches[i];
                var o = p.Orbit;
                var b = p.Body.Position;
                double after = System.Math.Max(p.StartTime, u.Time);
                double tPe = o.TimeOfTrueAnomaly(0, after);
                if (tPe <= p.EndTime)
                    Label(FloatingOrigin.ToUnity(b + o.PositionAt(tPe)), $"Pe {(o.PeriapsisRadius - p.Body.Radius) / 1000:0} км");
                if (o.IsElliptic)
                {
                    double tAp = o.TimeOfTrueAnomaly(System.Math.PI, after);
                    if (tAp <= p.EndTime)
                        Label(FloatingOrigin.ToUnity(b + o.PositionAt(tAp)), $"Ap {(o.ApoapsisRadius - p.Body.Radius) / 1000:0} км");
                }
                if (p.NextBody != null && !double.IsInfinity(p.EndTime))
                    Label(FloatingOrigin.ToUnity(b + o.PositionAt(p.EndTime)), $"SOI → {p.NextBody.Name}");
            }
        }

        void Label(Vector3 world, string text)
        {
            var sp = Camera.WorldToScreenPoint(world);
            if (sp.z <= 0) return;
            GUI.Label(new Rect(sp.x + 6, Screen.height - sp.y - 10, 220, 22), text);
        }
    }
}
