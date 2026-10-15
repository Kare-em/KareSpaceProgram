using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Меню перелётов на карте (GDD §6.11, клавиша F6): все цели, к которым ManeuverAutoPlan умеет строить узел, —
    /// спутники своего тела, родитель (возврат) и соседние планеты — с оценкой Δv отлёта и захвата, ожиданием окна
    /// и временем в пути. Оценка — гомановская (круговые компланарные орбиты): мгновенно для всей таблицы.
    /// Кнопка «Построить» зовёт тот же планировщик, что и P (Ламберт для планет), — точный Δv у узла.
    /// </summary>
    public sealed partial class MapView
    {
        /// <summary>Панель открыта (F6 или кнопка под списком фокуса).</summary>
        bool transfersOpen;
        readonly List<TransferRow> rows = new List<TransferRow>();
        float nextRows;
        string planResult;
        GUIStyle cellStyle, headStyle;

        /// <summary>Перицентр захвата у спутника без атмосферы, м — низкая круговая, как в ManeuverAutoPlan.MoonPeriapsis.</summary>
        const double CapturePeriapsis = ManeuverAutoPlan.MoonPeriapsis;
        /// <summary>Захват у планеты — в эллипс с апоцентром в этой доле SOI: самый дешёвый «остаться у планеты».
        /// Круговая низкая у Сатурна стоила бы ≈ 11 км/с против ≈ 0,7 км/с за эллипс.</summary>
        const double CaptureApoSoi = 0.5;
        /// <summary>Ширина колонок панели, px: цель, отлёт, захват, сумма, окно, в пути, кнопка.</summary>
        static readonly float[] Cols = { 120, 78, 78, 78, 110, 104, 92 };

        struct TransferRow
        {
            public CelestialBody Target;
            public double Depart, Capture, Wait, Tof;
            public string Note;
            public bool Aero;
        }

        /// <summary>F6: открыть меню (вместе с картой) или закрыть.</summary>
        public static void ToggleTransfers()
        {
            if (instance == null) return;
            if (!IsOpen)
            {
                Toggle();
                instance.transfersOpen = true;
            }
            else instance.transfersOpen = !instance.transfersOpen;
            instance.nextRows = 0;
            instance.planResult = null;
        }

        void TransferPanel(Universe u)
        {
            var v = u.Active;
            if (!transfersOpen || v == null) return;
            if (Time.unscaledTime >= nextRows)
            {
                nextRows = Time.unscaledTime + 0.5f;
                BuildRows(u, v);
            }
            if (cellStyle == null)
            {
                cellStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleRight };
                headStyle = new GUIStyle(cellStyle) { fontStyle = FontStyle.Bold };
            }

            double have = 0;
            if (v.Alive) foreach (var s in v.RemainingStats()) have += s.DeltaVVac;

            float w = 0;
            foreach (var c in Cols) w += c;
            float rowH = 24, pad = 10;
            float h = pad * 2 + rowH * (rows.Count + 3) + (planResult != null ? rowH * 2 : 0);
            // Слева, сразу за списком фокуса (x 12, ширина 170 + 60 у аппаратов): справа тулбар и панель ступеней
            // закрывали кнопки «Построить» (скрин 15.10.2026).
            var box = new Rect(12 + 230 + 16, 230, w + pad * 2, h);
            GUI.color = Color.white;
            GUI.Box(box, GUIContent.none);
            GUI.Box(box, GUIContent.none); // вторая подложка — плотнее на ярком диске планеты
            float x0 = box.x + pad, y = box.y + pad;

            cellStyle.alignment = TextAnchor.MiddleLeft;
            GUI.Label(new Rect(x0, y, w, rowH), $"Перелёты (F6) · запас Δv {have:0} м/с (вакуум)", headStyle);
            y += rowH;
            string[] head = { "Цель", "Отлёт", "Захват", "Σ Δv", "Окно через", "В пути", "" };
            float x = x0;
            for (int i = 0; i < head.Length; i++)
            {
                headStyle.alignment = i == 0 ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                GUI.Label(new Rect(x, y, Cols[i] - 6, rowH), head[i], headStyle);
                x += Cols[i];
            }
            headStyle.alignment = TextAnchor.MiddleLeft;
            y += rowH;

            foreach (var r in rows)
            {
                x = x0;
                double sum = r.Depart + r.Capture;
                // Зелёный — хватает на всё, жёлтый — только долететь, красный — не хватает и на разгон.
                var col = r.Note != null ? new Color(0.6f, 0.6f, 0.6f)
                    : sum <= have ? new Color(0.55f, 1f, 0.55f) : r.Depart <= have ? new Color(1f, 0.85f, 0.4f) : new Color(1f, 0.5f, 0.45f);
                cellStyle.normal.textColor = col;
                cellStyle.alignment = TextAnchor.MiddleLeft;
                GUI.Label(new Rect(x, y, Cols[0], rowH), r.Target.Name, cellStyle);
                x += Cols[0];
                cellStyle.alignment = TextAnchor.MiddleRight;
                if (r.Note != null)
                {
                    GUI.Label(new Rect(x, y, w - Cols[0], rowH), r.Note, cellStyle);
                }
                else
                {
                    GUI.Label(new Rect(x, y, Cols[1] - 6, rowH), $"{r.Depart:0}", cellStyle); x += Cols[1];
                    GUI.Label(new Rect(x, y, Cols[2] - 6, rowH), r.Aero ? "аэро" : $"{r.Capture:0}", cellStyle); x += Cols[2];
                    GUI.Label(new Rect(x, y, Cols[3] - 6, rowH), $"{sum:0}", cellStyle); x += Cols[3];
                    GUI.Label(new Rect(x, y, Cols[4] - 6, rowH), double.IsNaN(r.Wait) ? "≤ витка" : MissionAutopilot.Clock(r.Wait), cellStyle); x += Cols[4];
                    GUI.Label(new Rect(x, y, Cols[5] - 6, rowH), MissionAutopilot.Clock(r.Tof), cellStyle); x += Cols[5];
                    GUI.enabled = u.NodePilot == null;
                    if (GUI.Button(new Rect(x, y + 1, Cols[6] - 4, rowH - 2), "Построить"))
                    {
                        planResult = ManeuverAutoPlan.Plan(u, r.Target, out var plan);
                        u.Post(planResult);
                        // Фокус карты — на цель: сразу видно, куда ведёт новая траектория.
                        if (plan != null) SetFocus(v, r.Target);
                    }
                    GUI.enabled = true;
                }
                y += rowH;
            }
            cellStyle.normal.textColor = new Color(0.75f, 0.75f, 0.8f);
            cellStyle.alignment = TextAnchor.MiddleLeft;
            GUI.Label(new Rect(x0, y, w, rowH), "Оценка по Гоману, м/с; захват у планет — эллипс до ½ SOI. B — выполнить узел", cellStyle);
            y += rowH;
            if (planResult != null)
            {
                cellStyle.normal.textColor = Color.white;
                cellStyle.wordWrap = true;
                GUI.Label(new Rect(x0, y, w, rowH * 2), planResult, cellStyle);
                cellStyle.wordWrap = false;
            }
            cellStyle.normal.textColor = Color.white;
        }

        /// <summary>Цели в порядке удаления: спутники своего тела, родитель, соседние планеты.</summary>
        void BuildRows(Universe u, Vessel v)
        {
            rows.Clear();
            if (!v.Alive) return;
            var body = v.Body;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, body.Mu, u.Time);
            bool closed = !v.IsLanded && orbit.IsElliptic && orbit.PeriapsisRadius > PatchedConics.RailsFloorRadius(body);
            // Опорная орбита: на земле или на суборбите — оценка с низкой круговой (сначала выход на орбиту).
            double r1 = closed ? orbit.A : PatchedConics.RailsFloorRadius(body) + 50e3;
            string note = closed ? null : "сначала замкнутая орбита";

            foreach (var m in body.Children)
                rows.Add(ToMoon(u, v, body, m, r1, closed, note));
            if (body.Parent != null && body.Parent.Parent != null)
                rows.Add(ToParent(u, body, r1, note));
            if (body.Parent != null && body.Parent.Parent == null)
                foreach (var p in body.Parent.Children)
                    if (p != body) rows.Add(ToPlanet(u, body, p, r1, note));
            rows.Sort((a, b) => Dist(a.Target, u.Time).CompareTo(Dist(b.Target, u.Time)));
        }

        static double Dist(CelestialBody b, double t)
        {
            b.LocalStateAt(t, out var r, out _);
            return r.magnitude;
        }

        /// <summary>Спутник: гомановский эллипс внутри SOI тела, захват в низкую круговую.</summary>
        static TransferRow ToMoon(Universe u, Vessel v, CelestialBody body, CelestialBody m, double r1, bool closed, string note)
        {
            m.LocalStateAt(u.Time, out var rm, out _);
            double mu = body.Mu, r2 = rm.magnitude, a = 0.5 * (r1 + r2);
            double vInf = System.Math.Abs(System.Math.Sqrt(mu / r2) - System.Math.Sqrt(mu * (2 / r2 - 1 / a)));
            var row = new TransferRow
            {
                Target = m, Note = note,
                Depart = System.Math.Sqrt(mu * (2 / r1 - 1 / a)) - System.Math.Sqrt(mu / r1),
                Capture = CaptureCircular(m, vInf),
                Tof = HalfPeriod(mu, a),
                Wait = double.NaN,
            };
            if (closed) row.Wait = PhaseWait(v.Position, v.Velocity, rm, mu, r1, r2, row.Tof);
            return row;
        }

        /// <summary>Возврат к родителю (Луна → Земля): уход на эллипс с перицентром у атмосферы.</summary>
        static TransferRow ToParent(Universe u, CelestialBody body, double r1, string note)
        {
            var p = body.Parent;
            body.LocalStateAt(u.Time, out var rb, out _);
            double mu = p.Mu, ra = rb.magnitude;
            double rp = p.Radius + (p.HasAtmosphere ? MissionAutopilot.ReturnPerigee : ManeuverAutoPlan.ReturnPeriapsisVacuum);
            double a = 0.5 * (ra + rp);
            double vInf = System.Math.Abs(System.Math.Sqrt(mu / ra) - System.Math.Sqrt(mu * (2 / ra - 1 / a)));
            return new TransferRow
            {
                Target = p, Note = note,
                Depart = Hyperbolic(body.Mu, r1, vInf),
                Aero = p.HasAtmosphere,
                Capture = p.HasAtmosphere ? 0 : System.Math.Sqrt(mu * (2 / rp - 1 / a)) - System.Math.Sqrt(mu / rp),
                Tof = HalfPeriod(mu, a),
                Wait = double.NaN,
            };
        }

        /// <summary>Соседняя планета: гомановский эллипс вокруг Солнца, отлёт по гиперболе (Оберт), окно по фазе.</summary>
        static TransferRow ToPlanet(Universe u, CelestialBody body, CelestialBody target, double r1, string note)
        {
            var sun = body.Parent;
            body.LocalStateAt(u.Time, out var rb, out var vb);
            target.LocalStateAt(u.Time, out var rt, out _);
            double mu = sun.Mu, ra = rb.magnitude, rc = rt.magnitude, a = 0.5 * (ra + rc);
            double vDep = System.Math.Abs(System.Math.Sqrt(mu * (2 / ra - 1 / a)) - System.Math.Sqrt(mu / ra));
            double vArr = System.Math.Abs(System.Math.Sqrt(mu / rc) - System.Math.Sqrt(mu * (2 / rc - 1 / a)));
            double tof = HalfPeriod(mu, a);
            return new TransferRow
            {
                Target = target, Note = note,
                Depart = Hyperbolic(body.Mu, r1, vDep),
                Capture = CaptureEllipse(target, vArr),
                Tof = tof,
                Wait = PhaseWait(rb, vb, rt, mu, ra, rc, tof),
            };
        }

        /// <summary>Импульс с круговой радиуса r до гиперболы с избытком vInf, м/с (эффект Оберта).</summary>
        static double Hyperbolic(double mu, double r, double vInf) => System.Math.Sqrt(vInf * vInf + 2 * mu / r) - System.Math.Sqrt(mu / r);

        static double HalfPeriod(double mu, double a) => System.Math.PI * System.Math.Sqrt(a * a * a / mu);

        static double CaptureCircular(CelestialBody b, double vInf)
        {
            double rp = b.Radius + System.Math.Max(CapturePeriapsis, b.AtmosphereTop + 20e3);
            return Hyperbolic(b.Mu, rp, vInf);
        }

        static double CaptureEllipse(CelestialBody b, double vInf)
        {
            double rp = b.Radius + System.Math.Max(CapturePeriapsis, b.AtmosphereTop + 20e3);
            double ra = System.Math.Max(rp, CaptureApoSoi * b.SoiRadius);
            double a = 0.5 * (rp + ra);
            return System.Math.Sqrt(vInf * vInf + 2 * b.Mu / rp) - System.Math.Sqrt(b.Mu * (2 / rp - 1 / a));
        }

        /// <summary>
        /// Ожидание гомановского окна, с: цель должна опережать нас на π − n₂·T (за перелёт она пройдёт n₂·T).
        /// Фаза — угол от нас до цели вокруг нашей нормали; синодическое сближение — n₁ − n₂.
        /// </summary>
        static double PhaseWait(Vector3d r, Vector3d v, Vector3d rt, double mu, double r1, double r2, double tof)
        {
            var h = Vector3d.Cross(r, v).normalized;
            double n1 = System.Math.Sqrt(mu / (r1 * r1 * r1)), n2 = System.Math.Sqrt(mu / (r2 * r2 * r2));
            double phase = System.Math.Atan2(Vector3d.Dot(h, Vector3d.Cross(r, rt)), Vector3d.Dot(r, rt));
            double need = System.Math.PI - n2 * tof;
            double rel = n1 - n2;
            if (System.Math.Abs(rel) < 1e-15) return double.NaN;
            double d = rel > 0 ? phase - need : need - phase;
            d = (d % (2 * System.Math.PI) + 2 * System.Math.PI) % (2 * System.Math.PI);
            return d / System.Math.Abs(rel);
        }
    }
}
