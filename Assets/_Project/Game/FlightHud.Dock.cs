using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Прибор стыковки (§6.6): виден в режиме стыковки (Tab) и при автопилоте V. Квадрат — вид вдоль носа борта
    /// в тех же осях, что навбол (верх — −X, право — −Z): точка — узел цели (куда сдвигаться), стрелка — боковая
    /// скорость относительно цели (её гасить), кольцо — куда смотрит ось узла цели (довернуть в центр). Шкалы
    /// логарифмические: и 300 м, и 0,3 м видны без переключения. Зелёное — в пределах захвата Universe.Dock*.
    /// </summary>
    public sealed partial class FlightHud
    {
        const float DockPanelW = 290, DockPanelH = 330, DockScope = 150;
        /// <summary>Край шкалы смещения, м, и боковой скорости, м/с (логарифм от 0). Пара: AutoPilot RcsRange = 2000 м —
        /// дальше прибор упирается в край, но и ручное причаливание начинается ближе.</summary>
        const double DockOffsetFull = 1000, DockSpeedFull = 10;

        readonly HudDrag dockDrag = new HudDrag("hud.dock");

        static readonly Color Good = new Color(0.4f, 1f, 0.5f);
        static readonly Color TargetColor = new Color(1f, 0.45f, 0.9f);

        void DockPanel(Universe u, Vessel v, float w, float h)
        {
            var tg = v.Target;
            if (!(FlightInput.DockMode || u.Docking != null) || tg == null || !tg.Alive || tg.Body != v.Body) return;

            Universe.StateOf(tg, u.Time, out var rT, out var vT);
            var nT = tg.NoseP;
            var port = v.Position + v.NoseP * v.PortHeight();
            var portT = rT + nT * tg.PortHeight();
            var d = portT - port;
            double dist = d.magnitude;
            var vrel = v.Velocity - vT;
            double closing = dist > 1e-6 ? Vector3d.Dot(vrel, d) / dist : 0;
            double angle = Vector3d.Angle(v.NoseP, -nT) * Constants.Rad2Deg;
            var dl = v.WorldToLocal(d);
            var vl = v.WorldToLocal(vrel);
            var al = v.WorldToLocal(-nT);

            TargetMarker(v, tg, portT, dist);

            var r = Draggable(dockDrag, new Rect(w / 2 - 470, h - 24 - DockPanelH - 10, DockPanelW, DockPanelH), w, h,
                new Rect(8, DockPanelH - 34, DockPanelW - 16, 28));
            Fill(r, Panel);
            float x = r.x + 10, y = r.y + 4;
            GUI.Label(new Rect(x, y, DockPanelW - 20, 22), $"<b>СТЫКОВКА</b> · {tg.Name}", small);
            GUI.color = Dim;
            GUI.Label(new Rect(x, y + 18, DockPanelW - 20, 20), u.Docking != null ? "ведёт автопилот (V — снять)" : "вручную · Tab — выйти", small);
            GUI.color = Color.white;

            // Квадрат прибора.
            var sc = new Rect(x, y + 42, DockScope, DockScope);
            Fill(sc, new Color(0, 0, 0, 0.35f));
            var c = sc.center;
            float R = DockScope / 2 - 6;
            Fill(new Rect(sc.x, c.y - 0.5f, DockScope, 1), new Color(1, 1, 1, 0.25f));
            Fill(new Rect(c.x - 0.5f, sc.y, 1, DockScope), new Color(1, 1, 1, 0.25f));
            bool latOk = new Vector2((float)dl.x, (float)dl.z).magnitude < Universe.DockCaptureRange;
            // Кольцо оси цели: проекция как на навболе (единичный вектор → радиус R).
            var ap = c + new Vector2(-(float)al.z, (float)al.x) * R;
            bool angOk = angle < Universe.DockMaxAngleDeg;
            GUI.color = angOk ? Good : new Color(1f, 0.85f, 0.3f);
            var ring = new GUIStyle(big) { fontSize = 22 };
            GUI.Label(new Rect(ap.x - 15, ap.y - 15, 30, 30), "◯", ring);
            // Узел цели: точка по логарифмической шкале.
            var op = c + LogVec(-dl.z, dl.x, DockOffsetFull) * R;
            Fill(new Rect(op.x - 4, op.y - 4, 8, 8), latOk ? Good : TargetColor);
            // Боковая скорость — штрихами от центра.
            var vp = LogVec(-vl.z, vl.x, DockSpeedFull) * R;
            int n = Mathf.CeilToInt(vp.magnitude / 3);
            GUI.color = new Color(0.4f, 0.85f, 1f);
            for (int i = 1; i <= n; i++) Fill(new Rect(c.x + vp.x * i / n - 1.5f, c.y + vp.y * i / n - 1.5f, 3, 3), GUI.color);
            GUI.color = Color.white;

            // Числа справа от прибора.
            float tx = sc.xMax + 8, tw = r.xMax - tx - 6;
            Row(ref y, tx, tw, "дистанция", dist < 1000 ? $"{dist:0.0} м" : $"{dist / 1000:0.00} км", dist < Universe.DockCaptureRange);
            bool slow = closing < Universe.DockMaxSpeed;
            Row(ref y, tx, tw, "сближение", $"{closing:0.00} м/с", slow && closing > 0);
            Row(ref y, tx, tw, "вбок", $"{new Vector2((float)dl.x, (float)dl.z).magnitude:0.0} м", latOk);
            Row(ref y, tx, tw, "дрейф", $"{new Vector2((float)vl.x, (float)vl.z).magnitude:0.00} м/с", new Vector2((float)vl.x, (float)vl.z).magnitude < 0.1f);
            Row(ref y, tx, tw, "угол осей", $"{angle:0.0}°", angOk);
            GUI.color = Dim;
            GUI.Label(new Rect(x, sc.yMax + 4, DockPanelW - 20, 54),
                $"захват: ближе {Universe.DockCaptureRange:0} м, медленнее {Universe.DockMaxSpeed:0.0} м/с, угол < {Universe.DockMaxAngleDeg:0}°"
                + (v.RcsThrust > 0 ? "" : "\n<color=#ff7060>нет РСУ: сдвиг только двигателем</color>"), small);
            GUI.color = Color.white;

            // SAS к цели — главное, что облегчает ручное причаливание: ось держит автоматика, руками — только сдвиг.
            float bw = (DockPanelW - 16 - 8) / 3, by = r.yMax - 32;
            DockSasButton(u, v, new Rect(r.x + 8, by, bw, 26), SasMode.Target, "на цель");
            DockSasButton(u, v, new Rect(r.x + 12 + bw, by, bw, 26), SasMode.DockAlign, "соосно");
            DockSasButton(u, v, new Rect(r.x + 16 + 2 * bw, by, bw, 26), SasMode.AntiTarget, "от цели");
        }

        void Row(ref float y, float x, float w, string name, string value, bool ok)
        {
            GUI.color = Dim;
            GUI.Label(new Rect(x, y + 42, w, 18), name, small);
            GUI.color = ok ? Good : Color.white;
            GUI.Label(new Rect(x, y + 56, w, 22), value, label);
            GUI.color = Color.white;
            y += 30;
        }

        void DockSasButton(Universe u, Vessel v, Rect r, SasMode mode, string text)
        {
            bool on = v.Sas == mode;
            Fill(r, on ? new Color(Accent.r, Accent.g, Accent.b, 0.55f) : new Color(0.10f, 0.30f, 0.40f, r.Contains(Event.current.mousePosition) ? 0.9f : 0.5f));
            var st = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter };
            if (!GUI.Button(r, text, st)) return;
            v.Sas = on ? SasMode.Stability : mode;
            v.SasHoldValid = false;
            u.Ascent = null;
            u.NodePilot = null;
            u.Landing = null;
        }

        /// <summary>Логарифмическая шкала прибора: (право, низ экрана) из (право, верх) в долях радиуса.</summary>
        static Vector2 LogVec(double right, double top, double full)
        {
            double m = System.Math.Sqrt(right * right + top * top);
            if (m < 1e-9) return Vector2.zero;
            double k = System.Math.Log10(1 + m * 100) / System.Math.Log10(1 + full * 100) / m; // ×100: сантиметры не в нуле
            k = System.Math.Min(k, 1 / m);
            return new Vector2((float)(right * k), -(float)(top * k));
        }

        /// <summary>Скобка на узле цели в кадре — чтобы цель было видно и вдали, на фоне Земли.</summary>
        void TargetMarker(Vessel v, Vessel tg, Vector3d portT, double dist)
        {
            var cam = Camera.main;
            if (cam == null || MapView.IsOpen) return;
            var sp = cam.WorldToScreenPoint(FloatingOrigin.ToUnity(tg.Body.Position + portT));
            if (sp.z <= 0) return;
            float s = GUI.matrix.m00;
            var p = new Vector2(sp.x / s, (Screen.height - sp.y) / s);
            GUI.color = TargetColor;
            const float b = 14;
            Fill(new Rect(p.x - b, p.y - b, 6, 2), GUI.color); Fill(new Rect(p.x - b, p.y - b, 2, 6), GUI.color);
            Fill(new Rect(p.x + b - 6, p.y - b, 6, 2), GUI.color); Fill(new Rect(p.x + b - 2, p.y - b, 2, 6), GUI.color);
            Fill(new Rect(p.x - b, p.y + b - 2, 6, 2), GUI.color); Fill(new Rect(p.x - b, p.y + b - 6, 2, 6), GUI.color);
            Fill(new Rect(p.x + b - 6, p.y + b - 2, 6, 2), GUI.color); Fill(new Rect(p.x + b - 2, p.y + b - 6, 2, 6), GUI.color);
            GUI.Label(new Rect(p.x + b + 4, p.y - 10, 200, 20), dist < 1000 ? $"{tg.Name} · {dist:0} м" : $"{tg.Name} · {dist / 1000:0.0} км", small);
            GUI.color = Color.white;
        }

        /// <summary>Маркеры цели на навболе: ◎ — на цель, ✕ — от цели (как в KSP).</summary>
        void NavTargetMarkers(Universe u, Vessel v, Rect ball, Vector3d nose, Vector3d right, Vector3d top)
        {
            if (v.Target == null) return;
            var cs = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 16 };
            GUI.color = TargetColor;
            if (FlightControl.TryGetSasDirection(v, SasMode.Target, u.Time, out var dt) && Project(ball, dt, nose, right, top, out var p))
                GUI.Label(new Rect(p.x - 12, p.y - 12, 24, 24), "◎", cs);
            if (FlightControl.TryGetSasDirection(v, SasMode.AntiTarget, u.Time, out var da) && Project(ball, da, nose, right, top, out p))
                GUI.Label(new Rect(p.x - 12, p.y - 12, 24, 24), "✕", cs);
            GUI.color = Color.white;
        }
    }
}
