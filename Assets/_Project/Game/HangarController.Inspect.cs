using System;
using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Конструктор: окно детали щелчком по предпросмотру и окно «Ступени подробно» (§5.4, §10.2) — те же PartInspector
    /// и StageTable, что в полёте, чтобы паспорт детали и таблица ступеней читались в ангаре и в полёте одинаково.
    /// Отдельным файлом: основной HangarController правят параллельно (каталог, аэродинамика).
    /// </summary>
    public sealed partial class HangarController
    {
        const string StagesKey = "hangar.stages";
        /// <summary>Ширина окна ступеней, px IMGUI (база 1080). Пара: кнопка «Ступени» прижата к тому же правому краю.</summary>
        const float StagesWinW = 440;

        /// <summary>Щелчок здесь выбирает деталь: в предпросмотре, не над панелями и не над окнами.</summary>
        public bool CanInspect(Vector3 m) => build != null && InPreview(m);

        // ---------------------------------------------------------------- выбор детали лучом

        /// <summary>
        /// Деталь под мышью: луч против вертикальных цилиндров деталей по той же раскладке, что строит RebuildPreview
        /// (Vessel.Layout + PartOffset; боковые копии — по кругу RadialOffset). Обтекатель — в последнюю очередь:
        /// он в разрезе, и щелчок по видимому под ним грузу должен выбирать груз.
        /// </summary>
        public PartInfo InspectAt(Vector3 m)
        {
            if (drag != null || !Hit(m, out int hitStack, out int hitRadial, out int hitRadialPart, out _)) return null;
            if (hitRadial >= 0)
            {
                selRadial = hitRadial;
                selStack = craft.Radials[hitRadial].Parent;
                dirty = true;
                return RadialInfo(craft.Radials[hitRadial], hitRadialPart);
            }
            selStack = hitStack;
            selRadial = -1;
            dirty = true;
            return StackInfo(craft.Stack[hitStack]);
        }

        /// <summary>Деталь под мышью (общая для окна детали и переноса): индекс в стеке или группа + деталь блока, t — по лучу root.</summary>
        bool Hit(Vector3 m, out int hitStack, out int hitRadial, out int hitRadialPart, out float best)
        {
            hitStack = hitRadial = hitRadialPart = -1;
            best = float.PositiveInfinity;
            if (Camera == null || build == null) return false;
            var d = build.Design;
            var ray = LocalRay(m);
            var layout = Layout();

            float fairT = float.PositiveInfinity;
            int fairStack = -1;
            double y = 0;
            for (int i = 0; i < craft.Stack.Count; i++)
            {
                // Размеры детали стека (растянутый бак, обтекатель другого Ø) — как в RebuildPreview, иначе луч промахивается.
                var p = PartCatalog.Resolve(craft.Stack[i]);
                if (p == null) continue;
                if (p.Fairing && d != null && layout != null)
                {
                    int fs = d.Sections.FindIndex(q => q.Kind == SectionKind.Fairing);
                    if (fs >= 0)
                    {
                        var s = d.Sections[fs];
                        if (Cylinder(ray, Vector3.zero, (float)layout[fs], (float)(layout[fs] + s.Length), (float)s.Radius, out float tf) && tf < fairT)
                        { fairT = tf; fairStack = i; }
                        continue;
                    }
                }
                double baseY = layout != null && !p.Fairing ? layout[build.PartSection[i]] + build.PartOffset[i] : y;
                if (PartHit(ray, p, Vector3.zero, (float)baseY, out float t) && t < best) { best = t; hitStack = i; hitRadial = -1; }
                y = baseY + p.Length;
            }
            if (layout != null)
                for (int g = 0; g < craft.Radials.Count; g++)
                {
                    var rad = craft.Radials[g];
                    var s = d.Sections[build.RadialSection[g]];
                    for (int c = 0; c < s.RadialCount; c++)
                    {
                        float ang = 2 * Mathf.PI * c / s.RadialCount;
                        var at = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * (float)s.RadialOffset;
                        double h = layout[build.RadialSection[g]];
                        for (int j = 0; j < rad.Parts.Count; j++)
                        {
                            var p = PartCatalog.Get(rad.Parts[j]);
                            if (p == null) continue;
                            if (PartHit(ray, p, at, (float)h, out float t) && t < best) { best = t; hitRadial = g; hitRadialPart = j; hitStack = -1; }
                            h += p.Length;
                        }
                    }
                }
            if (hitStack < 0 && hitRadial < 0 && fairStack >= 0) { hitStack = fairStack; best = fairT; }
            return hitStack >= 0 || hitRadial >= 0;
        }

        static bool PartHit(Ray ray, PartDef p, Vector3 at, float baseY, out float t)
        {
            float r = (float)Math.Max(p.Diameter, p.Top) * 0.5f, len = (float)p.Length;
            // Навесные (длина 0) в предпросмотре — кольцо-метка 0.12 м; ловим по поясу пошире, иначе в него не попасть.
            if (len <= 0.01f) return Cylinder(ray, at, baseY - 0.15f, baseY + 0.3f, r + 0.1f, out t);
            return Cylinder(ray, at, baseY, baseY + len, r, out t);
        }

        /// <summary>Луч против вертикального цилиндра (ось через at, высоты y0…y1): бок и торцы, ближайшее t ≥ 0.</summary>
        static bool Cylinder(Ray ray, Vector3 at, float y0, float y1, float r, out float t)
        {
            t = float.PositiveInfinity;
            Vector3 o = ray.origin, dir = ray.direction;
            float ox = o.x - at.x, oz = o.z - at.z;
            float a = dir.x * dir.x + dir.z * dir.z;
            if (a > 1e-8f)
            {
                float b = 2 * (ox * dir.x + oz * dir.z), c = ox * ox + oz * oz - r * r;
                float disc = b * b - 4 * a * c;
                if (disc >= 0)
                {
                    float sq = Mathf.Sqrt(disc);
                    foreach (float s in new[] { (-b - sq) / (2 * a), (-b + sq) / (2 * a) })
                    {
                        if (s < 0 || s >= t) continue;
                        float hy = o.y + dir.y * s;
                        if (hy >= y0 && hy <= y1) t = s;
                    }
                }
            }
            if (Mathf.Abs(dir.y) > 1e-8f)
                foreach (float cy in new[] { y0, y1 })
                {
                    float s = (cy - o.y) / dir.y;
                    if (s < 0 || s >= t) continue;
                    float hx = ox + dir.x * s, hz = oz + dir.z * s;
                    if (hx * hx + hz * hz <= r * r) t = s;
                }
            return !float.IsPositiveInfinity(t);
        }

        // ---------------------------------------------------------------- содержимое окон

        PartInfo StackInfo(CraftPart cp)
        {
            var p = PartCatalog.Resolve(cp);
            var info = new PartInfo
            {
                Key = cp,
                // Resolve каждый раз: окно живёт, пока игрок меняет размеры, — имя и массы должны идти за ними.
                Title = () => PartCatalog.Resolve(cp)?.Name ?? cp.Id,
                Text = () =>
                {
                    var q = PartCatalog.Resolve(cp);
                    int k = craft.Stack.IndexOf(cp);
                    var d = build?.Design;
                    if (q == null || k < 0 || d == null || build.PartSection == null || k >= build.PartSection.Length)
                        return "<color=#9aa4ad>деталь снята со сборки</color>";
                    return PartInfoText.Part(q, d.Sections[build.PartSection[k]], cp.Stage);
                },
            };
            if (p != null && p.ParachuteArea > 0)
            {
                info.ChuteGet = () => FlightPhysics.ClampChuteAltitude(cp.ChuteAltitude);
                info.ChuteSet = a => SetChute(ref cp.ChuteAltitude, a);
            }
            return info;
        }

        PartInfo RadialInfo(CraftRadial rad, int j)
        {
            string id = j >= 0 && j < rad.Parts.Count ? rad.Parts[j] : null;
            var p = id != null ? PartCatalog.Get(id) : null;
            var info = new PartInfo
            {
                Key = Tuple.Create<object, int>(rad, j),
                Title = () => (p?.Name ?? id ?? "Боковой блок") + $" · боковой ×{rad.Symmetry}",
                Text = () =>
                {
                    int g = craft.Radials.IndexOf(rad);
                    var d = build?.Design;
                    if (p == null || g < 0 || d == null || build.RadialSection == null || g >= build.RadialSection.Length)
                        return "<color=#9aa4ad>блок снят со сборки</color>";
                    return PartInfoText.Part(p, d.Sections[build.RadialSection[g]], rad.EngineStage);
                },
            };
            if (p != null && p.ParachuteArea > 0)
            {
                info.ChuteGet = () => FlightPhysics.ClampChuteAltitude(rad.ChuteAltitude);
                info.ChuteSet = a => SetChute(ref rad.ChuteAltitude, a);
            }
            return info;
        }

        /// <summary>
        /// Уставка высоты ввода парашюта (§4.8) живёт в Craft (CraftPart/CraftRadial.ChuteAltitude, JSON-ключ chuteAlt) и
        /// попадает в секцию при компиляции — поэтому перекомпиляция. «Штатно» (7 км) хранится как NaN: ключ не пишется.
        /// </summary>
        void SetChute(ref double field, double a)
        {
            a = FlightPhysics.ClampChuteAltitude(a);
            field = Math.Abs(a - FlightPhysics.ChuteDeployAltitude) < 1 ? double.NaN : a;
            dirty = true;
        }

        // ---------------------------------------------------------------- ступени подробно

        /// <summary>Кнопка в правом верхнем углу предпросмотра; рисует PartInspector (поверх, в том же масштабе).</summary>
        public void InspectorButtons(GUIStyle style)
        {
            if (build == null || showFiles) return;
            bool open = PartInspector.IsOpen(StagesKey);
            var r = new Rect(vw - RightW - Pad - 176, TopH + 6, 170, 26);
            HudHits.Add(r);
            if (!GUI.Button(r, open ? "▾ Ступени подробно" : "▸ Ступени подробно", style)) return;
            if (open) PartInspector.Close(StagesKey);
            else PartInspector.Show(StagesInfo(), new Vector2(r.xMax - StagesWinW - 24, r.yMax - 8), pinned: true);
        }

        PartInfo StagesInfo() => new PartInfo
        {
            Key = StagesKey,
            Width = StagesWinW,
            Title = () => "Ступени подробно",
            Text = () =>
            {
                if (build?.Design == null || build.Stats.Count == 0) return "<color=#9aa4ad>ракета не собрана</color>";
                double dv = 0;
                foreach (var s in build.Stats) dv += s.DeltaVVac;
                return $"Всего Δv <b>{dv:0}</b> м/с вак. · ступеней {build.Stats.Count} · полные баки, Δv и TWR у земли в скобках";
            },
            Custom = area => build?.Design == null || build.Stats.Count == 0 ? 0
                : StageTable.Draw(area, build.Stats, build.Design, null, true, build.Stats.Count, false),
        };
    }
}
