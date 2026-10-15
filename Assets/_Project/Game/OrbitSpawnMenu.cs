using System.Collections.Generic;
using System.Globalization;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Esc → «Читы и телепорт…» (для тестов, §4.7): бесконечное топливо и перезапуски, быстрые орбиты, и любое тело —
    /// текущий корабль переносится на круговую орбиту выбранного тела (Universe.Teleport). Своё окно, а не низ панели
    /// паузы: там кнопки уходили за край экрана. Тела — деревом «планета → её спутники», как в Солнечной системе ядра.
    /// Высота по умолчанию — над атмосферой и горами; ниже атмосферы пускаем (проверять вход в атмосферы газовых
    /// гигантов), но с предупреждением. Верх — доля сферы влияния: у её края орбита за виток уходит к хозяину.
    /// </summary>
    public static class OrbitSpawnMenu
    {
        public static bool Open;
        static CelestialBody picked;
        static string altText = "";
        static Vector2 scroll;
        static GUIStyle title, planet, moon, on, info, warn, button, field, toggle, head;

        const float Width = 820, Row = 30, Gap = 4, CellW = 124, Footer = 176;
        /// <summary>Блок над списком тел: тумблеры читов, быстрые орбиты, подпись списка. Пара: код блока в Draw.</summary>
        const float Quick = 2 * (Row + 6) + 30;
        /// <summary>Запас над верхом атмосферы и над высочайшей горой, м: у самой границы сопротивление ещё тормозит.</summary>
        const double Margin = 20e3;
        /// <summary>Высота по умолчанию — не ниже этой доли радиуса: у Луны и Марса «над горами» — почти по грунту.</summary>
        const double DefaultRadiusShare = 0.05;
        /// <summary>Доля сферы влияния, до которой пускаем. Пара: Universe.Teleport — скорость круговая по Mu тела,
        /// без притяжения хозяина; у края сферы влияния его возмущение того же порядка.</summary>
        const double SoiShare = 0.5;
        /// <summary>Ниже этой высоты над рельефом не ставим, м — иначе корабль в грунте.</summary>
        const double Clearance = 1e3;

        static double Highest(CelestialBody b) => b.Terrain?.Map?.MaxWithDetail ?? 2 * (b.Terrain?.Amplitude ?? 0);
        static double MinAlt(CelestialBody b) => Highest(b) + Clearance;
        static double SafeAlt(CelestialBody b) => System.Math.Max(b.AtmosphereTop, Highest(b)) + Margin;
        static double MaxAlt(CelestialBody b) => b.SoiRadius * SoiShare - b.Radius;

        static void Pick(CelestialBody b)
        {
            picked = b;
            // Круглое число км: в поле удобнее править «320», чем «318,503».
            double alt = System.Math.Max(SafeAlt(b), b.Radius * DefaultRadiusShare);
            alt = System.Math.Min(alt, MaxAlt(b));
            altText = (System.Math.Ceiling(alt / 10e3) * 10).ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>Тела дерева: планета (глубина 0), её спутники (1) и далее.</summary>
        static void Flatten(CelestialBody b, int depth, List<(CelestialBody b, int depth)> into)
        {
            into.Add((b, depth));
            foreach (var c in b.Children) Flatten(c, depth + 1, into);
        }

        public static void Draw(Universe u, System.Action close)
        {
            if (u == null) return;
            Styles();
            int per = Mathf.Max(1, (int)((Width - 60) / (CellW + Gap)));
            var groups = new List<List<(CelestialBody b, int depth)>>();
            float listH = 0;
            foreach (var p in u.System.Sun.Children)
            {
                var g = new List<(CelestialBody, int)>();
                Flatten(p, 0, g);
                groups.Add(g);
                listH += Mathf.Ceil(g.Count / (float)per) * (Row + Gap) + 8;
            }

            float h = Mathf.Min(Screen.height - 40, 64 + Quick + listH + Footer);
            var r = new Rect((Screen.width - Width) / 2, (Screen.height - h) / 2, Width, h);
            GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.96f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(r.x, r.y + 12, Width, 32), "ЧИТЫ И ТЕЛЕПОРТ", title);
            DrawQuick(u, new Rect(r.x + 20, r.y + 56, Width - 40, Quick), close);

            var view = new Rect(r.x + 20, r.y + 56 + Quick, Width - 40, h - 56 - Quick - Footer);
            scroll = GUI.BeginScrollView(view, scroll, new Rect(0, 0, Width - 60, listH));
            float y = 0;
            foreach (var g in groups)
            {
                for (int i = 0; i < g.Count; i++)
                {
                    if (i > 0 && i % per == 0) y += Row + Gap;
                    var (b, depth) = g[i];
                    var style = b == picked ? on : depth == 0 ? planet : moon;
                    // Сфера влияния меньше тела (Фобос, Деймос): круговой орбиты вокруг него нет.
                    GUI.enabled = MaxAlt(b) > MinAlt(b);
                    if (GUI.Button(new Rect((i % per) * (CellW + Gap), y, CellW, Row), b.Name, style)) Pick(b);
                    GUI.enabled = true;
                }
                y += Row + Gap + 8;
            }
            GUI.EndScrollView();

            float x = r.x + 20, w = Width - 40;
            y = r.y + h - Footer + 10;
            if (picked == null)
            {
                GUI.Label(new Rect(x, y, w, 24), "Выберите тело. Серые — сфера влияния меньше самого тела, орбиты нет.", info);
                y += Row * 3 + 16;
            }
            else
            {
                string atmo = picked.HasAtmosphere ? $"атмосфера до {picked.AtmosphereTop / 1000:0} км" : "без атмосферы";
                GUI.Label(new Rect(x, y, w, 24),
                    $"{picked.Name}: радиус {picked.Radius / 1000:0} км, {atmo}, " +
                    $"высота от {MinAlt(picked) / 1000:0} до {MaxAlt(picked) / 1000:0} км", info);
                y += 30;
                GUI.Label(new Rect(x, y, 110, Row), "Высота, км", info);
                altText = GUI.TextField(new Rect(x + 110, y + 2, 120, Row - 4), altText, 9, field);
                float px = x + 244, pw = (w - 244 - 3 * 6) / 4;
                if (GUI.Button(new Rect(px, y, pw, Row), "Над атмосферой", button)) Pick(picked);
                if (GUI.Button(new Rect(px + (pw + 6), y, pw, Row), "1 радиус", button)) Set(picked.Radius);
                if (GUI.Button(new Rect(px + 2 * (pw + 6), y, pw, Row), "5 радиусов", button)) Set(5 * picked.Radius);
                if (GUI.Button(new Rect(px + 3 * (pw + 6), y, pw, Row), "Предел", button)) Set(MaxAlt(picked) * 0.99);
                y += Row + 6;

                bool ok = double.TryParse(altText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double km);
                double alt = km * 1000;
                string msg = !ok ? "Высота — число в км."
                    : alt < MinAlt(picked) ? "Ниже гор — корабль окажется в грунте."
                    : alt > MaxAlt(picked) ? "Выше половины сферы влияния — орбиту уведёт притяжение хозяина."
                    : alt < picked.AtmosphereTop ? "Внутри атмосферы: орбита будет тормозиться (вход в атмосферу)."
                    : "";
                ok = ok && alt >= MinAlt(picked) && alt <= MaxAlt(picked);
                GUI.Label(new Rect(x, y, w, 24), msg, warn);
                y += 30;
                GUI.enabled = ok;
                if (GUI.Button(new Rect(x, y, (w - 8) / 2, Row + 4), "Перенести (круговая орбита)", button))
                {
                    u.Teleport(picked, alt, true);
                    Open = false;
                    close();
                }
                GUI.enabled = true;
                if (GUI.Button(new Rect(x + (w + 8) / 2, y, (w - 8) / 2, Row + 4), "Назад (Esc)", button)) Open = false;
                return;
            }
            if (GUI.Button(new Rect(x + (w + 8) / 2, y, (w - 8) / 2, Row + 4), "Назад (Esc)", button)) Open = false;
        }

        /// <summary>
        /// Тумблеры читов и быстрые орбиты — бывший низ панели паузы. Миссия при этом не засчитывается честно — это отладка.
        /// </summary>
        static void DrawQuick(Universe u, Rect r, System.Action close)
        {
            var boot = GameBootstrap.Instance;
            float x = r.x, y = r.y, half = (r.width - 8) / 2;
            if (boot != null)
                boot.InfiniteFuel = GUI.Toggle(new Rect(x, y, half, Row), boot.InfiniteFuel, "  Бесконечное топливо", toggle);
            // Зажигания у исторических двигателей считанные (Vessel.IgnitionsLeft) — тумблер снимает лимит для свободного полёта.
            Vessel.UnlimitedIgnitions = GUI.Toggle(new Rect(x + half + 8, y, half, Row), Vessel.UnlimitedIgnitions,
                "  Бесконечные перезапуски двигателей", toggle);
            y += Row + 6;
            float qw = (r.width - 3 * 6) / 4;
            if (GUI.Button(new Rect(x, y, qw, Row), "Орбита Земли 200 км", button)) Jump(u, "earth", 200e3, true, close);
            if (GUI.Button(new Rect(x + (qw + 6), y, qw, Row), "Орбита Луны 100 км", button)) Jump(u, "moon", 100e3, true, close);
            if (GUI.Button(new Rect(x + 2 * (qw + 6), y, qw, Row), "Над Луной 15 км (H)", button)) Jump(u, "moon", 15e3, false, close);
            if (GUI.Button(new Rect(x + 3 * (qw + 6), y, qw, Row), "Орбита Марса 300 км", button)) Jump(u, "mars", 300e3, true, close);
            y += Row + 6;
            GUI.Label(new Rect(x, y, r.width, 26), "На орбиту любого тела:", head);
        }

        /// <summary>Быстрый перенос: orbital — круговая орбита, иначе неподвижно над точкой (H — автопосадка).</summary>
        static void Jump(Universe u, string id, double alt, bool orbital, System.Action close)
        {
            var body = u?.System.Get(id);
            if (body == null) return;
            u.Teleport(body, alt, orbital);
            Open = false;
            close();
        }

        static void Set(double alt) => altText = System.Math.Round(alt / 1000).ToString("0", CultureInfo.InvariantCulture);

        static void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            planet = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold };
            moon = new GUIStyle(GUI.skin.button) { fontSize = 13 };
            moon.normal.textColor = new Color(0.75f, 0.8f, 0.9f);
            on = new GUIStyle(planet);
            on.normal.textColor = on.hover.textColor = new Color(1f, 0.8f, 0.4f);
            info = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
            info.normal.textColor = Color.white;
            warn = new GUIStyle(info) { fontSize = 14 };
            warn.normal.textColor = new Color(1f, 0.75f, 0.4f);
            button = new GUIStyle(GUI.skin.button) { fontSize = 14 };
            field = new GUIStyle(GUI.skin.textField) { fontSize = 16, alignment = TextAnchor.MiddleRight };
            toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 15 };
            toggle.normal.textColor = toggle.onNormal.textColor = toggle.hover.textColor = toggle.onHover.textColor = Color.white;
            head = new GUIStyle(GUI.skin.label) { fontSize = 15 };
            head.normal.textColor = new Color(0.6f, 0.75f, 1f);
        }
    }
}
