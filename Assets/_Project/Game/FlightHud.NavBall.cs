using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Навбол (GDD §10.2) и кнопки режимов SAS (§4.9), как в KSP. Шар — сфера направлений в местных осях
    /// (север / восток / зенит), центр — нос борта. Точка шара на экране = направление, куда уйдёт нос, если
    /// довернуть его туда: верх шара — S (нос к −X связанных осей), право — D (к −Z), поэтому руль и шар
    /// согласованы при любом крене. Рисуется попиксельно в маленькую текстуру: без отдельной камеры HDRP
    /// и без мешей, ≈ 13 тыс. пикселей за кадр.
    /// </summary>
    public sealed partial class FlightHud : MonoBehaviour
    {
        /// <summary>Сторона текстуры шара, px; на экране — NavBallSize (билинейно).</summary>
        const int NavBallTex = 128;
        const float NavBallSize = 156, SasButton = 34, SasGap = 4;
        /// <summary>Полуширина линий шара: горизонт и сетка тангажа, градусы. Пара: NavBallTex — тоньше
        /// пикселя линии рвутся.</summary>
        const float HorizonLine = 0.9f, GridLine = 0.6f;

        static readonly Color32 Sky = new Color32(64, 130, 210, 255);
        static readonly Color32 Ground = new Color32(150, 95, 50, 255);

        Texture2D navTex;
        Color32[] navPx;

        /// <summary>Кнопки по бокам шара, ряды по парам «по / против»: слева орбитальные оси, справа радиальные
        /// и удержание с узлом.</summary>
        static readonly SasMode[] SasLeft = { SasMode.Prograde, SasMode.Retrograde, SasMode.Normal, SasMode.AntiNormal };
        static readonly SasMode[] SasRight = { SasMode.RadialOut, SasMode.RadialIn, SasMode.Stability, SasMode.Maneuver };

        void NavBall(Universe u, Vessel v, float w, float bottomTop)
        {
            if (!v.Alive) return;
            float size = NavBallSize;
            var ball = new Rect((w - size) / 2, bottomTop - size - 26, size, size);

            // Местные оси на борту и связанные оси в P.
            var up = v.Position.normalized;
            var pole = v.Body.Orientation * Vector3d.forward;
            var east = Vector3d.Cross(pole, up);
            if (east.sqrMagnitude < 1e-12) east = Vector3d.Cross(new Vector3d(1, 0, 0), up); // на полюсе
            east = east.normalized;
            var north = Vector3d.Cross(up, east);
            var nose = v.NoseP;
            var right = v.LocalToWorld(new Vector3d(0, 0, -1));
            var top = v.LocalToWorld(new Vector3d(-1, 0, 0));

            PaintBall(nose, right, top, up, north, east);
            GUI.DrawTexture(ball, navTex);

            // Стороны света на горизонте — ориентир курса.
            var cs = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            BallLabel(ball, north, nose, right, top, "С", cs);
            BallLabel(ball, east, nose, right, top, "В", cs);
            BallLabel(ball, -north, nose, right, top, "Ю", cs);
            BallLabel(ball, -east, nose, right, top, "З", cs);

            // Маркеры режимов SAS — то же направление, что держит SAS (в атмосфере — по воздуху).
            for (int m = (int)SasMode.Prograde; m <= (int)SasMode.Maneuver; m++)
            {
                var mode = (SasMode)m;
                if (!FlightControl.TryGetSasDirection(v, mode, u.Time, out var dir)) continue;
                if (!Project(ball, dir, nose, right, top, out var p)) continue;
                GUI.color = mode == SasMode.Maneuver ? Accent : mode == SasMode.Prograde || mode == SasMode.Retrograde
                    ? new Color(1f, 0.9f, 0.3f) : mode == SasMode.Normal || mode == SasMode.AntiNormal
                    ? new Color(0.85f, 0.45f, 1f) : new Color(0.4f, 0.9f, 1f);
                DrawIcon(new Rect(p.x - 13, p.y - 13, 26, 26), SasIcon(mode));
            }
            GUI.color = Color.white;

            // Нос — неподвижная «галка» в центре.
            var c = ball.center;
            var nc = new Color(1f, 0.75f, 0.1f);
            Fill(new Rect(c.x - 22, c.y - 1.5f, 14, 3), nc);
            Fill(new Rect(c.x + 8, c.y - 1.5f, 14, 3), nc);
            Fill(new Rect(c.x - 2, c.y - 2, 4, 4), nc);

            double pitch = System.Math.Asin(Clamp1(Vector3d.Dot(nose, up))) * 180 / System.Math.PI;
            double heading = System.Math.Atan2(Vector3d.Dot(nose, east), Vector3d.Dot(nose, north)) * 180 / System.Math.PI;
            if (heading < 0) heading += 360;
            var hs = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter };
            Fill(new Rect(ball.x - 10, ball.yMax + 2, size + 20, 20), Panel);
            GUI.Label(new Rect(ball.x - 10, ball.yMax + 2, size + 20, 20), $"курс {heading:000}° · тангаж {pitch:0}°", hs);

            SasButtons(u, v, ball);
        }

        void PaintBall(Vector3d nose, Vector3d right, Vector3d top, Vector3d up, Vector3d north, Vector3d east)
        {
            if (navTex == null)
            {
                navTex = new Texture2D(NavBallTex, NavBallTex, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                navPx = new Color32[NavBallTex * NavBallTex];
            }
            // Проекции осей шара на местные оси — заранее, чтобы на пиксель приходилось три скаляра.
            double nu = Vector3d.Dot(nose, up), nn = Vector3d.Dot(nose, north), ne = Vector3d.Dot(nose, east);
            double ru = Vector3d.Dot(right, up), rn = Vector3d.Dot(right, north), re = Vector3d.Dot(right, east);
            double tu = Vector3d.Dot(top, up), tn = Vector3d.Dot(top, north), te = Vector3d.Dot(top, east);
            const double Deg = 180 / System.Math.PI;
            float half = NavBallTex * 0.5f;
            for (int y = 0; y < NavBallTex; y++)
            {
                double sy = (y + 0.5 - half) / half; // текстура снизу вверх — как экранный верх шара
                for (int x = 0; x < NavBallTex; x++)
                {
                    double sx = (x + 0.5 - half) / half;
                    double r2 = sx * sx + sy * sy;
                    int i = y * NavBallTex + x;
                    if (r2 >= 1) { navPx[i] = new Color32(0, 0, 0, 0); continue; }
                    double sz = System.Math.Sqrt(1 - r2);
                    double du = nu * sz + ru * sx + tu * sy;
                    double dn = nn * sz + rn * sx + tn * sy;
                    double de = ne * sz + re * sx + te * sy;
                    double pitch = System.Math.Asin(Clamp1(du)) * Deg;
                    Color32 col = du >= 0 ? Sky : Ground;
                    double ap = System.Math.Abs(pitch);
                    if (ap < HorizonLine) col = new Color32(255, 255, 255, 255);
                    else
                    {
                        // Сетка: тангаж через 30°, курс через 45° (у полюсов шара курс не рисуем — там каша).
                        double mp = ap % 30;
                        bool grid = mp < GridLine || mp > 30 - GridLine;
                        if (!grid && ap < 75)
                        {
                            double hd = System.Math.Atan2(de, dn) * Deg + 360;
                            double mh = hd % 45;
                            double lim = GridLine / System.Math.Cos(pitch / Deg);
                            grid = mh < lim || mh > 45 - lim;
                        }
                        if (grid) col = Color32.Lerp(col, new Color32(255, 255, 255, 255), 0.45f);
                    }
                    // Затемнение к краю — объём шара; край сглажен альфой на полпикселя.
                    float shade = (float)(0.55 + 0.45 * sz);
                    float edge = Mathf.Clamp01((float)((1 - System.Math.Sqrt(r2)) * half * 1.5));
                    navPx[i] = new Color32((byte)(col.r * shade), (byte)(col.g * shade), (byte)(col.b * shade), (byte)(255 * edge));
                }
            }
            navTex.SetPixels32(navPx);
            navTex.Apply(false);
        }

        static double Clamp1(double x) => x < -1 ? -1 : x > 1 ? 1 : x;

        /// <summary>Экранная точка направления dir на шаре; ложь — направление на обратной стороне.</summary>
        static bool Project(Rect ball, Vector3d dir, Vector3d nose, Vector3d right, Vector3d top, out Vector2 p)
        {
            dir = dir.normalized;
            double sz = Vector3d.Dot(dir, nose);
            float rad = ball.width / 2;
            p = ball.center + new Vector2((float)Vector3d.Dot(dir, right) * rad, -(float)Vector3d.Dot(dir, top) * rad);
            return sz > 0.05;
        }

        void BallLabel(Rect ball, Vector3d dir, Vector3d nose, Vector3d right, Vector3d top, string text, GUIStyle st)
        {
            if (!Project(ball, dir, nose, right, top, out var p)) return;
            GUI.color = new Color(1, 1, 1, 0.9f);
            GUI.Label(new Rect(p.x - 10, p.y - 18, 20, 16), text, st);
            GUI.color = Color.white;
        }

        /// <summary>
        /// Кнопки SAS по бокам шара, по 2 × 2. Повторный клик по активному режиму — назад к удержанию, по удержанию —
        /// выключить SAS. Клик — это ручное управление: автопилоты снимаются, как от клавиш (FlightInput).
        /// </summary>
        void SasButtons(Universe u, Vessel v, Rect ball)
        {
            float step = SasButton + SasGap;
            float y0 = ball.center.y - (2 * step - SasGap) / 2;
            Column(u, v, SasLeft, ball.x - 14 - (2 * step - SasGap), y0);
            Column(u, v, SasRight, ball.xMax + 14, y0);
        }

        void Column(Universe u, Vessel v, SasMode[] modes, float x, float y0)
        {
            float step = SasButton + SasGap;
            for (int k = 0; k < modes.Length; k++)
            {
                var mode = modes[k];
                var r = new Rect(x + (k % 2) * step, y0 + (k / 2) * step, SasButton, SasButton);
                bool active = v.Sas == mode;
                bool usable = mode == SasMode.Stability || FlightControl.TryGetSasDirection(v, mode, u.Time, out _);
                Fill(r, active ? new Color(Accent.r, Accent.g, Accent.b, 0.55f) : Panel);
                GUI.color = usable ? Color.white : new Color(1, 1, 1, 0.3f);
                DrawIcon(new Rect(r.x + 4, r.y + 4, r.width - 8, r.height - 8), SasIcon(mode));
                GUI.color = Color.white;
                if (!usable || !GUI.Button(r, new GUIContent("", SasName(mode)), GUIStyle.none)) continue;
                v.Sas = !active ? mode : mode == SasMode.Stability ? SasMode.Off : SasMode.Stability;
                v.SasHoldValid = false;
                u.Ascent = null;
                u.NodePilot = null;
                u.Landing = null;
            }
        }
    }
}
