using System;
using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Схема траектории миссии не в масштабе (GDD §7.1, подсказка «?» в таблице миссий): рисуется на процессоре
    /// в Texture2D по MissionProfile — тело, выведение, орбиты, перелёт, посадка, возврат. Рисуем в текстуру, а не GL
    /// в OnGUI: один раз на миссию, без зависимости от порядка отрисовки HDRP; кеш по id миссии.
    /// Координаты — пиксели от левого верхнего угла (как у IMGUI), переворот по Y — при выгрузке в текстуру.
    /// </summary>
    public sealed class MissionSketch
    {
        /// <summary>Размер схемы, пикс. Пара: раскладки в Earth*/Target* рассчитаны на эти 440 × 250.</summary>
        public const int W = 440, H = 250;

        public Texture2D Texture;
        /// <summary>Подписи поверх схемы (рисует IMGUI — шрифт в текстуру не растеризуем).</summary>
        public readonly List<(Vector2 pos, string text)> Labels = new List<(Vector2, string)>();

        static readonly Dictionary<string, MissionSketch> Cache = new Dictionary<string, MissionSketch>();

        public static MissionSketch Get(MissionDef def)
        {
            if (Cache.TryGetValue(def.Id, out var s) && s.Texture != null) return s;
            s = new MissionSketch();
            s.Build(MissionProfile.Of(def), def);
            Cache[def.Id] = s;
            return s;
        }

        // Палитра схемы: выведение — оранжевое (работает двигатель), баллистика — светлая, спуск — красный (нагрев).
        static readonly Color Bg = new Color(0.035f, 0.05f, 0.09f), Powered = new Color(1f, 0.62f, 0.2f),
            Coast = new Color(0.82f, 0.9f, 1f), OrbitCol = new Color(0.45f, 0.78f, 1f), Reentry = new Color(1f, 0.35f, 0.25f),
            Station = new Color(0.95f, 0.85f, 0.35f), Mark = new Color(1f, 1f, 1f);

        readonly Color[] px = new Color[W * H];
        readonly float[] cov = new float[W * H];

        void Build(MissionProfile p, MissionDef def)
        {
            for (int i = 0; i < px.Length; i++) px[i] = Bg;
            Stars();
            if (p.Target != null) TargetLayout(p);
            else if (p.Dock) DockLayout(p);
            else if (p.EarthOrbit) OrbitLayout(p);
            else SuborbitalLayout(p);

            var flipped = new Color[W * H];
            for (int y = 0; y < H; y++) Array.Copy(px, y * W, flipped, (H - 1 - y) * W, W);
            Texture = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            Texture.SetPixels(flipped);
            Texture.Apply(false, true);
        }

        // ---------- Раскладки ----------

        void SuborbitalLayout(MissionProfile p)
        {
            // Земля — дуга большого круга внизу; пунктир — 100 км (линия Кармана).
            var c = new Vector2(220, 790); const float R = 600;
            Planet(c, R, new Color(0.15f, 0.35f, 0.75f), new Color(0.05f, 0.12f, 0.3f));
            Stroke(Arc(c, R + 8, 230, 310), new Color(0.4f, 0.7f, 1f, 0.35f), 3);
            Stroke(Arc(c, R + 95, 235, 305), new Color(0.6f, 0.7f, 0.85f, 0.7f), 1.2f, 5);
            Labels.Add((new Vector2(350, 84), "100 км"));
            var a = OnCircle(c, R, 261); var b = OnCircle(c, R, 279);
            var path = Bezier(a, new Vector2(226, -86), b);
            int split = path.Count * 3 / 10;
            Stroke(path.GetRange(0, split + 1), Powered, 2.6f);
            Stroke(path.GetRange(split, path.Count - split), Coast, 1.8f);
            Arrow(path, 0.62f, Coast);
            Labels.Add((a + new Vector2(-46, -6), "Старт"));
            Labels.Add((new Vector2(196, 24), "Апогей"));
            if (p.Return)
            {
                var chute = path[path.Count * 9 / 10];
                Parachute(chute + new Vector2(6, -14));
                Labels.Add((chute + new Vector2(18, -26), p.Splashdown ? "Парашют, приводнение" : "Парашют"));
            }
        }

        void OrbitLayout(MissionProfile p)
        {
            var c = new Vector2(220, 128); const float R = 52;
            Planet(c, R, new Color(0.2f, 0.45f, 0.9f), new Color(0.04f, 0.1f, 0.28f));
            Atmosphere(c, R);
            Labels.Add((c + new Vector2(-22, -8), "Земля"));
            // Орбита — кеплеров эллипс с фокусом в центре Земли: перицентр слева, апоцентр справа.
            Func<float, float> orbit = a => Conic(a, 100, 0.12f, 180);
            Stroke(Polar(c, orbit, 0, 360), OrbitCol, 1.6f);
            Arrow(Polar(c, orbit, 270, 330), 0.5f, OrbitCol);
            Labels.Add((c + new Vector2(70, -112), "Орбита"));
            var up = Ascent(c, R, orbit, 160, 235);
            Stroke(up, Powered, 2.6f);
            Labels.Add((up[0] + new Vector2(-64, 2), "Старт"));
            if (p.Return) Descent(c, R, orbit, 20, 75, p);
        }

        void DockLayout(MissionProfile p)
        {
            var c = new Vector2(220, 128); const float R = 48;
            Planet(c, R, new Color(0.2f, 0.45f, 0.9f), new Color(0.04f, 0.1f, 0.28f));
            Atmosphere(c, R);
            Labels.Add((c + new Vector2(-22, -8), "Земля"));
            Func<float, float> high = a => 108, low = a => 76;
            Stroke(Polar(c, high, 0, 360), Station, 1.4f);
            Stroke(Polar(c, low, 215, 330), OrbitCol, 1.4f, 4);
            var up = Ascent(c, R, low, 165, 215);
            Stroke(up, Powered, 2.6f);
            Labels.Add((up[0] + new Vector2(-64, 2), "Старт"));
            // Фазирование на нижней орбите и подъём к станции: догоняем снизу, как «Союз» и Dragon.
            var raise = Spiral(c, low, high, 330, 380);
            Stroke(raise, OrbitCol, 2f);
            Arrow(raise, 0.5f, OrbitCol);
            var dock = OnCircle(c, 108, 20);
            StationGlyph(dock);
            Labels.Add((dock + new Vector2(12, -26), (p.StationName ?? "Станция") + ": стыковка"));
            Labels.Add((OnCircle(c, 76, 275) + new Vector2(-40, 8), "Фазирование"));
            if (p.Return) Descent(c, R, high, 60, 115, p);
        }

        void TargetLayout(MissionProfile p)
        {
            var e = new Vector2(70, 150); const float Re = 28;
            var m = new Vector2(340, 100); const float Rm = 16;
            Planet(e, Re, new Color(0.2f, 0.45f, 0.9f), new Color(0.04f, 0.1f, 0.28f));
            Atmosphere(e, Re);
            Labels.Add((e + new Vector2(-22, Re + 4), "Земля"));
            BodyColors(p.Target, out var lit, out var dark);
            Planet(m, Rm, lit, dark);
            string name = BodyName(p.Target);
            Labels.Add((m + new Vector2(-64, -50), name));

            // Опорная орбита у Земли и разгон к цели в её верхней точке.
            Func<float, float> park = a => 38;
            var up = Ascent(e, Re, park, 150, 230);
            Stroke(up, Powered, 2.4f);
            Stroke(Polar(e, park, 230, 290), OrbitCol, 1.4f, 3);
            var tli = OnCircle(e, 38, 290);
            var tan = Tangent(290);
            Labels.Add((tli + new Vector2(-30, -26), "Разгон"));

            if (p.TargetSoi && !p.Impact && !p.Landing && !p.TargetOrbit && !p.Flyby)
            {
                Stroke(Circle(m, 62), new Color(0.75f, 0.75f, 0.8f, 0.6f), 1.2f, 4);
                Labels.Add((m + new Vector2(-50, 62), "Сфера влияния"));
                var path = Bezier(tli, tli + tan * 110, m + new Vector2(-70, 70), m + new Vector2(30, 40));
                Burn(path, 0.08f);
                Arrow(path, 0.75f, Coast);
                Stroke(Bezier(m + new Vector2(30, 40), m + new Vector2(60, 25), m + new Vector2(80, 30)), Coast, 1.4f, 3);
                return;
            }
            if (p.Impact)
            {
                var hit = OnCircle(m, Rm, 160);
                var path = Bezier(tli, tli + tan * 110, hit + new Vector2(-90, 40), hit);
                Burn(path, 0.08f);
                Arrow(path, 0.6f, Coast);
                Cross(hit, 6, Reentry);
                Labels.Add((hit + new Vector2(-46, 10), "Удар"));
                return;
            }
            if (p.Flyby || p.FarSide)
            {
                // Луна-3: проход под Луной, облёт обратной (дальней от Земли) стороны и уход назад к Земле сверху.
                var a0 = m + new Vector2(-10, 40);
                var path = Bezier(tli, tli + tan * 110, a0 + new Vector2(-90, 10), a0);
                Burn(path, 0.08f);
                Arrow(path, 0.55f, Coast);
                Func<float, float> loop = a => 38;
                var around = Polar(m, loop, 105, -60);
                Stroke(around, Coast, 1.8f);
                var back = around[around.Count - 1];
                var home = Bezier(back, back + new Vector2(-60, -50), new Vector2(200, 10), new Vector2(120, 30));
                Stroke(home, Coast, 1.4f, 4);
                Arrow(home, 0.7f, Coast);
                if (p.FarSide)
                {
                    var cam = OnCircle(m, 38, 10);
                    CameraGlyph(cam);
                    Labels.Add((cam + new Vector2(-30, 12), "Снимок"));
                    SunGlyph(new Vector2(420, 22));
                    Labels.Add((new Vector2(352, 12), "Солнце"));
                }
                return;
            }

            // Посадка или окололунная орбита.
            Func<float, float> lo = a => 30;
            Vector2 arrive;
            if (p.TargetOrbit)
            {
                arrive = OnCircle(m, 30, 150);
                Stroke(Circle(m, 30), OrbitCol, 1.5f);
                Arrow(Polar(m, lo, 200, 260), 0.5f, OrbitCol);
                Labels.Add((m + new Vector2(-94, 24), "Торможение"));
            }
            else arrive = OnCircle(m, Rm, 175);
            var transfer = Bezier(tli, tli + tan * 110, arrive + new Vector2(-80, 30), arrive);
            Burn(transfer, 0.08f);
            Arrow(transfer, 0.55f, Coast);

            if (p.Landing)
            {
                Vector2 site;
                if (p.TargetOrbit)
                {
                    var down = Spiral(m, lo, a => Rm, 250, 300);
                    Stroke(down, Powered, 2.2f);
                    site = down[down.Count - 1];
                    Lander(site, m);
                }
                else
                {
                    site = arrive;
                    Lander(site, m);
                }
                Labels.Add((site + (site - m).normalized * 14 + new Vector2(4, -10), "Посадка"));
                if (p.Ascent)
                {
                    var upm = Spiral(m, a => Rm, lo, 300, 350);
                    Stroke(upm, Powered, 1.6f, 3);
                    Labels.Add((m + new Vector2(36, 2), "Взлёт"));
                }
                if (p.Drive)
                {
                    var track = Polar(m, a => Rm + 3, 300, 330);
                    Stroke(track, Mark, 1.2f, 2);
                    Labels.Add((m + new Vector2(36, 2), "Луноход"));
                }
            }
            if (p.Return)
            {
                // Возврат нижней ветвью «восьмёрки» к Земле, вход в атмосферу, парашют.
                var leave = OnCircle(m, 30, 100);
                var entry = OnCircle(e, Re, 25);
                var home = Bezier(leave, leave + new Vector2(-40, 90), entry + new Vector2(120, 70), entry);
                Stroke(home, Coast, 1.6f);
                Arrow(home, 0.45f, Coast);
                Stroke(home.GetRange(home.Count * 9 / 10, home.Count - home.Count * 9 / 10), Reentry, 2.4f);
                Parachute(entry + new Vector2(16, 6));
                Labels.Add((entry + new Vector2(28, 8), p.Splashdown ? "Приводнение" : "Посадка"));
            }
        }

        /// <summary>Сход с орбиты: короткий импульс (метка) и спуск сквозь атмосферу, парашют у поверхности.</summary>
        void Descent(Vector2 c, float R, Func<float, float> orbit, float a0, float a1, MissionProfile p)
        {
            var down = new List<Vector2>();
            const int n = 48;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n, a = Mathf.Lerp(a0, a1, t);
                down.Add(OnCircle(c, Mathf.Lerp(orbit(a), R, t * t), a));
            }
            Stroke(down.GetRange(0, n * 6 / 10 + 1), Coast, 1.8f);
            Stroke(down.GetRange(n * 6 / 10, n - n * 6 / 10 + 1), Reentry, 2.6f);
            Cross(down[0], 4, Powered);
            Labels.Add((down[0] + new Vector2(8, -8), "Торможение"));
            var land = down[n];
            var outward = (land - c).normalized;
            // Крылатые садятся планированием на полосу — парашют на схеме врал бы.
            if (!p.Runway) Parachute(land + outward * 14);
            string where = p.Runway ? "Посадка на ВПП" : p.Splashdown ? "Приводнение" : "Спуск на парашюте";
            Labels.Add((land + outward * 26 + new Vector2(-30, 0), where));
        }

        // ---------- Кривые ----------

        static Vector2 OnCircle(Vector2 c, float r, float deg)
        {
            float a = deg * Mathf.Deg2Rad;
            return c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
        }

        /// <summary>Направление движения по окружности при росте угла (на экране — по часовой).</summary>
        static Vector2 Tangent(float deg)
        {
            float a = deg * Mathf.Deg2Rad;
            return new Vector2(-Mathf.Sin(a), Mathf.Cos(a));
        }

        /// <summary>Радиус конического сечения с фокусом в центре тела: перицентр в направлении peDeg.</summary>
        static float Conic(float deg, float semiLatus, float e, float peDeg) =>
            semiLatus / (1 + e * Mathf.Cos((deg - peDeg) * Mathf.Deg2Rad));

        static List<Vector2> Polar(Vector2 c, Func<float, float> r, float a0, float a1)
        {
            var pts = new List<Vector2>();
            int n = Mathf.Max(8, (int)(Mathf.Abs(a1 - a0) / 2));
            for (int i = 0; i <= n; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)n);
                pts.Add(OnCircle(c, r(a), a));
            }
            return pts;
        }

        static List<Vector2> Circle(Vector2 c, float r) => Polar(c, a => r, 0, 360);
        static List<Vector2> Arc(Vector2 c, float r, float a0, float a1) => Polar(c, a => r, a0, a1);

        /// <summary>Выведение: радиус растёт быстро вначале и касательно входит в орбиту (гравитационный разворот).</summary>
        static List<Vector2> Ascent(Vector2 c, float R, Func<float, float> orbit, float a0, float a1)
        {
            var pts = new List<Vector2>();
            const int n = 40;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n, a = Mathf.Lerp(a0, a1, t), k = 1 - (1 - t) * (1 - t);
                pts.Add(OnCircle(c, Mathf.Lerp(R, orbit(a), k), a));
            }
            return pts;
        }

        static List<Vector2> Spiral(Vector2 c, Func<float, float> r0, Func<float, float> r1, float a0, float a1)
        {
            var pts = new List<Vector2>();
            const int n = 40;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n, a = Mathf.Lerp(a0, a1, t);
                pts.Add(OnCircle(c, Mathf.Lerp(r0(a), r1(a), Mathf.SmoothStep(0, 1, t)), a));
            }
            return pts;
        }

        static List<Vector2> Bezier(Vector2 p0, Vector2 p1, Vector2 p2)
        {
            var pts = new List<Vector2>();
            const int n = 60;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n, u = 1 - t;
                pts.Add(u * u * p0 + 2 * u * t * p1 + t * t * p2);
            }
            return pts;
        }

        static List<Vector2> Bezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            var pts = new List<Vector2>();
            const int n = 80;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n, u = 1 - t;
                pts.Add(u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3);
            }
            return pts;
        }

        /// <summary>Перелёт: первые frac пути — работающий разгонный блок, дальше пассивный участок.</summary>
        void Burn(List<Vector2> path, float frac)
        {
            int k = Mathf.Max(1, (int)(path.Count * frac));
            Stroke(path.GetRange(0, k + 1), Powered, 2.6f);
            Stroke(path.GetRange(k, path.Count - k), Coast, 1.8f);
        }

        // ---------- Растр ----------

        /// <summary>Линия с мягким краем: покрытие копится максимумом (без пересвета на стыках), потом смешивается.</summary>
        void Stroke(List<Vector2> pts, Color col, float width, float dash = 0)
        {
            Array.Clear(cov, 0, cov.Length);
            float r = width * 0.5f, s = 0;
            int x0 = W, y0 = H, x1 = 0, y1 = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1]; var b = pts[i];
                float len = Vector2.Distance(a, b);
                int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.5f));
                for (int k = 0; k < steps; k++)
                {
                    float t = k / (float)steps;
                    float sd = s + len * t;
                    if (dash > 0 && sd % (2 * dash) > dash) continue;
                    var p = Vector2.Lerp(a, b, t);
                    Stamp(p, r, ref x0, ref y0, ref x1, ref y1);
                }
                s += len;
            }
            if (dash <= 0 && pts.Count > 0) Stamp(pts[pts.Count - 1], r, ref x0, ref y0, ref x1, ref y1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * W + x;
                    if (cov[i] > 0) px[i] = Color.Lerp(px[i], new Color(col.r, col.g, col.b, 1), cov[i] * col.a);
                }
        }

        void Stamp(Vector2 p, float r, ref int x0, ref int y0, ref int x1, ref int y1)
        {
            int xa = Mathf.Max(0, (int)(p.x - r - 1)), xb = Mathf.Min(W - 1, (int)(p.x + r + 1));
            int ya = Mathf.Max(0, (int)(p.y - r - 1)), yb = Mathf.Min(H - 1, (int)(p.y + r + 1));
            if (xa > xb || ya > yb) return;
            x0 = Mathf.Min(x0, xa); x1 = Mathf.Max(x1, xb); y0 = Mathf.Min(y0, ya); y1 = Mathf.Max(y1, yb);
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), p);
                    float a = Mathf.Clamp01(r + 0.5f - d);
                    int i = y * W + x;
                    if (a > cov[i]) cov[i] = a;
                }
        }

        /// <summary>Шар с освещением слева сверху (Солнце условно там же, где на схеме Луны-3).</summary>
        void Planet(Vector2 c, float R, Color lit, Color dark)
        {
            var light = new Vector2(-0.55f, -0.6f).normalized;
            int xa = Mathf.Max(0, (int)(c.x - R - 1)), xb = Mathf.Min(W - 1, (int)(c.x + R + 1));
            int ya = Mathf.Max(0, (int)(c.y - R - 1)), yb = Mathf.Min(H - 1, (int)(c.y + R + 1));
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++)
                {
                    var d = (new Vector2(x + 0.5f, y + 0.5f) - c) / R;
                    float dist = d.magnitude;
                    float a = Mathf.Clamp01((1 - dist) * R + 0.5f);
                    if (a <= 0) continue;
                    float z = Mathf.Sqrt(Mathf.Max(0, 1 - dist * dist));
                    float sh = Mathf.Clamp01(0.35f + 0.65f * (Vector2.Dot(d, light) + z * 0.6f));
                    int i = y * W + x;
                    px[i] = Color.Lerp(px[i], Color.Lerp(dark, lit, sh), a);
                }
        }

        void Atmosphere(Vector2 c, float R) => Stroke(Circle(c, R + 3), new Color(0.45f, 0.75f, 1f, 0.45f), 3);

        void Stars()
        {
            var rnd = new System.Random(7);
            for (int k = 0; k < 90; k++)
            {
                int i = rnd.Next(W * H);
                float b = 0.25f + 0.45f * (float)rnd.NextDouble();
                px[i] = Color.Lerp(px[i], Color.white, b);
            }
        }

        void Arrow(List<Vector2> path, float frac, Color col)
        {
            int i = Mathf.Clamp((int)(path.Count * frac), 1, path.Count - 1);
            var tip = path[i];
            var dir = (path[i] - path[i - 1]).normalized;
            if (dir.sqrMagnitude < 1e-6f) return;
            var n = new Vector2(-dir.y, dir.x);
            Stroke(new List<Vector2> { tip - dir * 7 + n * 4.5f, tip, tip - dir * 7 - n * 4.5f }, col, 1.8f);
        }

        void Cross(Vector2 c, float s, Color col)
        {
            Stroke(new List<Vector2> { c + new Vector2(-s, -s), c + new Vector2(s, s) }, col, 2);
            Stroke(new List<Vector2> { c + new Vector2(-s, s), c + new Vector2(s, -s) }, col, 2);
        }

        void Parachute(Vector2 c)
        {
            Stroke(Arc(c, 7, 180, 360), Mark, 1.6f);
            Stroke(new List<Vector2> { c + new Vector2(-7, 0), c + new Vector2(0, 9), c + new Vector2(7, 0) }, Mark, 1f);
            Stroke(new List<Vector2> { c, c + new Vector2(0, 9) }, Mark, 1f);
        }

        /// <summary>Посадочный аппарат: корпус и две опоры, ось — от центра тела наружу.</summary>
        void Lander(Vector2 site, Vector2 bodyCenter)
        {
            var up = (site - bodyCenter).normalized;
            var side = new Vector2(-up.y, up.x);
            var b = site + up * 5;
            Stroke(new List<Vector2> { b - side * 3, b + side * 3, b + side * 3 + up * 5, b - side * 3 + up * 5, b - side * 3 }, Station, 1.6f);
            Stroke(new List<Vector2> { b - side * 3, site - side * 6 }, Station, 1.2f);
            Stroke(new List<Vector2> { b + side * 3, site + side * 6 }, Station, 1.2f);
        }

        void StationGlyph(Vector2 c)
        {
            Stroke(new List<Vector2> { c + new Vector2(-12, 0), c + new Vector2(12, 0) }, Station, 2.4f);
            Stroke(new List<Vector2> { c + new Vector2(-8, -6), c + new Vector2(-8, 6) }, Station, 2f);
            Stroke(new List<Vector2> { c + new Vector2(8, -6), c + new Vector2(8, 6) }, Station, 2f);
        }

        void CameraGlyph(Vector2 c)
        {
            Stroke(new List<Vector2> { c + new Vector2(-5, -4), c + new Vector2(5, -4), c + new Vector2(5, 4), c + new Vector2(-5, 4), c + new Vector2(-5, -4) }, Mark, 1.4f);
            Stroke(Circle(c, 2), Mark, 1.2f);
        }

        void SunGlyph(Vector2 c)
        {
            Planet(c, 7, new Color(1f, 0.95f, 0.6f), new Color(1f, 0.7f, 0.2f));
            for (int k = 0; k < 8; k++)
                Stroke(new List<Vector2> { OnCircle(c, 10, k * 45), OnCircle(c, 14, k * 45) }, new Color(1f, 0.85f, 0.4f), 1.2f);
        }

        static void BodyColors(string id, out Color lit, out Color dark)
        {
            switch (id)
            {
                case "mars": lit = new Color(0.85f, 0.45f, 0.25f); dark = new Color(0.25f, 0.1f, 0.05f); break;
                case "venus": lit = new Color(0.95f, 0.85f, 0.55f); dark = new Color(0.35f, 0.28f, 0.12f); break;
                default: lit = new Color(0.82f, 0.82f, 0.8f); dark = new Color(0.2f, 0.2f, 0.22f); break;
            }
        }

        static string BodyName(string id) => GameBootstrap.U?.System.Get(id)?.Name ?? id;
    }
}
