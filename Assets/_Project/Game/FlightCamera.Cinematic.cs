using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Кинокамера (F7), «взгляд на планету» (F8) и подсказки камеры (GDD §10.3). Всё это — надстройки над обычным видом:
    /// базовый кадр (режим F5, Yaw/Pitch, Distance) считается как всегда, а здесь поза переопределяется.
    /// Поэтому выход из кино возвращает ровно тот вид, что был до него.
    /// </summary>
    public sealed partial class FlightCamera
    {
        // ------------------------------------------------------------------ подсказки

        static string flashText;
        static float flashUntil;
        /// <summary>Сколько держится надпись смены камеры и сколько из этого она гаснет, с. Пара: время реальное
        /// (unscaled) — при ускорении времени надпись не должна мелькать.</summary>
        const float FlashTime = 3f, FlashFade = 0.7f;
        GUIStyle flashStyle;

        /// <summary>Показать надпись сверху по центру (гаснет сама).</summary>
        public static void Flash(string text, float seconds = FlashTime)
        {
            flashText = text;
            flashUntil = Time.unscaledTime + seconds;
        }

        static readonly string[] ShortNames = { "Авто", "Свободная", "Орбитальная", "Погоня", "Связанная" };

        /// <summary>Короткое имя режима для HUD: «Авто», …, «Кино».</summary>
        public static string ModeShort => CinematicOn ? "Кино" : ShortNames[(int)Mode];

        /// <summary>
        /// Постоянная подсказка для HUD: «F5 камера: Авто». Клавиши берутся из KeyBinds — переназначил в настройках,
        /// поменялась и строка. В кино добавляется ракурс, при взгляде на планету — пометка.
        /// </summary>
        public static string ModeHint
        {
            get
            {
                string s = $"{KeyBinds.Label(Bind.CameraMode)} камера: {ModeShort}";
                if (CinematicOn) s += $" · {ShotNames[(int)shot]} ({KeyBinds.Label(Bind.CinematicCamera)} — далее)";
                if (LookAtBodyOn) s += $" · на планету ({KeyBinds.Label(Bind.LookAtBody)} — назад)";
                return s;
            }
        }

        void OnGUI()
        {
            // Одна надпись на оба вида сплита; на карте у неё своя подсказка.
            if (Secondary || Event.current.type != EventType.Repaint || MapView.IsOpen) return;
            if (CinematicOn)
            {
                // Монтажная склейка: короткая вспышка чёрного скрывает скачок камеры между ракурсами.
                float k = (Time.unscaledTime - shotStart) / CutFlash;
                if (k < 1)
                {
                    GUI.color = new Color(0, 0, 0, (1 - k) * 0.8f);
                    GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
            }
            // Склейка выше — часть картинки и остаётся; надпись ракурса — интерфейс, её прячет F12.
            float left = flashUntil - Time.unscaledTime;
            if (left <= 0 || string.IsNullOrEmpty(flashText) || FlightHud.UiHidden) return;
            if (flashStyle == null)
                flashStyle = new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(14, 14, 6, 6) };
            var size = flashStyle.CalcSize(new GUIContent(flashText));
            // Ниже верхней строки HUD (≈ 60 px), чтобы не перекрывать статус миссии.
            var r = new Rect((Screen.width - size.x) * 0.5f, 64, size.x, size.y);
            GUI.color = new Color(1, 1, 1, Mathf.Clamp01(left / FlashFade));
            GUI.Label(r, flashText, flashStyle);
            GUI.color = Color.white;
        }

        // ------------------------------------------------------------------ клавиши

        void HandleKeys()
        {
            var u = GameBootstrap.U;
            if (KeyBinds.Down(Bind.LookAtBody))
            {
                LookAtBodyOn = !LookAtBodyOn;
                string body = u?.Active?.Body?.Name ?? "тело";
                Flash(LookAtBodyOn
                    ? $"Взгляд на: {body}   [{KeyBinds.Label(Bind.LookAtBody)} — вернуть вид]"
                    : "Вид возвращён");
            }
            if (KeyBinds.Down(Bind.CinematicCamera))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (CinematicOn && shift)
                {
                    CinematicOn = false;
                    Flash($"Камера: {ModeName}");
                }
                else if (!CinematicOn)
                {
                    CinematicOn = true;
                    LookAtBodyOn = false; // два переопределения сразу не нужны: кино — это уже «чей-то» взгляд
                    BeginShot(Shot.Tracking);
                }
                else NextShot(true);
            }
            if (CinematicOn && Time.unscaledTime >= shotStart + shotLen) NextShot(false);
        }

        // ------------------------------------------------------------------ кино

        public static bool CinematicOn { get; private set; }
        public static bool LookAtBodyOn { get; private set; }

        enum Shot { Tracking, Side, LongLens, Engines, Orbit, Tripod, Overhead }
        const int ShotCount = 7;
        static readonly string[] ShotNames =
        {
            "Снизу-сзади", "Профиль", "Телеобъектив", "Двигатели крупно", "Облёт", "Штатив", "Сверху",
        };
        public static string ShotName => ShotNames[(int)shot];

        static Shot shot;
        static float shotStart, shotLen = 8, shotSign = 1, shotPhase;
        /// <summary>Номер ракурса с начала сеанса: у каждой камеры сплита свои якоря, они сбрасываются по смене номера.</summary>
        static int shotSerial;
        /// <summary>Длительность ракурса, с: достаточно, чтобы рассмотреть, и не надоесть. Реальное время — при ускорении
        /// склейки не должны сыпаться.</summary>
        const float ShotMinLen = 6, ShotMaxLen = 10;
        /// <summary>Длина вспышки при склейке, с.</summary>
        const float CutFlash = 0.18f;
        /// <summary>Скорость доворота камеры за целью в «штативе», 1/с: оператор отстаёт от пролетающего борта.
        /// Пара: меньше — борт выходит из кадра при пролёте у самой камеры.</summary>
        const float AimRate = 7;
        /// <summary>«Штатив» на земле — ниже этой высоты над рельефом, м; выше борт слишком далёк от точки на грунте.
        /// Пара: TripodMaxDist — на таком удалении от борта якорь переставляется.</summary>
        const float GroundShotAlt = 2000, TripodMaxDist = 4000;
        /// <summary>Ускорение времени, выше которого неподвижный якорь на земле бессмыслен: борт улетает за кадр.</summary>
        const double TripodMaxWarp = 1.5;

        int seenShot = -1;
        bool aimValid;
        Quaternion aimRot;
        // Якорь «штатива»: либо точка на земле (в осях тела — вращается вместе с ним), либо смещение от борта.
        bool anchorInit, anchorGround;
        CelestialBody anchorBody;
        Vector3d anchorBF;
        Vector3 anchorOffset, anchorDrift;
        float anchorTime, tripodFov;

        static void NextShot(bool manual)
        {
            Shot s;
            if (manual) s = (Shot)(((int)shot + 1) % ShotCount); // вручную — по кругу, чтобы можно было пройти все
            else do s = (Shot)UnityEngine.Random.Range(0, ShotCount); while (s == shot);
            BeginShot(s);
        }

        static void BeginShot(Shot s)
        {
            shot = s;
            shotStart = Time.unscaledTime;
            shotLen = UnityEngine.Random.Range(ShotMinLen, ShotMaxLen);
            shotSign = UnityEngine.Random.value < 0.5f ? -1 : 1;
            shotPhase = UnityEngine.Random.value;
            shotSerial++;
            Flash($"Кино: {ShotNames[(int)s]}   [{KeyBinds.Label(Bind.CinematicCamera)} — следующий, Shift+{KeyBinds.Label(Bind.CinematicCamera)} — выход]", 2.5f);
        }

        /// <summary>Расстояние, с которого длина len занимает долю fill высоты кадра при вертикальном FOV fov.</summary>
        static float FrameDist(float len, float fov, float fill) => len / (2 * fill * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad));

        /// <summary>Любой единичный вектор, перпендикулярный n.</summary>
        static Vector3 AnyPerp(Vector3 n)
        {
            var p = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right);
            return p.normalized;
        }

        /// <summary>LookRotation, не ломающийся на вырожденном «верхе»: если он параллелен взгляду, берётся запасной.</summary>
        static Quaternion LookQ(Vector3 fwd, Vector3 upHint, Vector3 alt)
        {
            if (fwd.sqrMagnitude < 1e-8f) return Quaternion.identity;
            if (Vector3.Cross(fwd, upHint).sqrMagnitude < 1e-4f * fwd.sqrMagnitude * upHint.sqrMagnitude) upHint = alt;
            return Quaternion.LookRotation(fwd, upHint);
        }

        /// <summary>
        /// Направление полёта (Unity): у поверхности и в атмосфере — относительно грунта (у Луны тоже: на старте орбитальная
        /// скорость смотрит вбок), выше — орбитальное. Медленнее 3 м/с — нос борта, как у «погони».
        /// </summary>
        static Vector3 ShotDir(Vessel v, Quaternion att, out double speed)
        {
            var vel = v.Velocity;
            if (v.Altitude < System.Math.Max(v.Body.AtmosphereTop, 100000)) vel -= v.Body.SurfaceVelocity(v.Position);
            speed = vel.magnitude;
            return speed > 3 ? FloatingOrigin.DirToUnity(vel.normalized) : att * Vector3.up;
        }

        /// <summary>
        /// Поза кинокамеры для текущего ракурса. Все ракурсы привязаны к борту (он в нуле Unity), поэтому одинаково
        /// работают в атмосфере, в космосе и на ускорении времени: ничего не зависит от скорости симуляции.
        /// Размеры — от длины борта: «Восток» и «Сатурн-5» снимаются с одинаковым кадрированием.
        /// </summary>
        void CinematicPose(Universe u, Vessel v, Quaternion att, Vector3 up, float length, float com,
                           ref Vector3 pos, ref Quaternion rot, ref Vector3 aim, ref float fov)
        {
            if (seenShot != shotSerial) { seenShot = shotSerial; aimValid = false; anchorInit = false; }
            float t = Time.unscaledTime - shotStart, s = shotSign, ph = shotPhase * Mathf.PI * 2;
            float L = Mathf.Max(length, 4f);
            var origin = FloatingOrigin.ToUnity(FloatingOrigin.WorldP(v));
            var mid = origin + att * new Vector3(0, length * 0.5f - com, 0);
            var bottom = origin + att * new Vector3(0, -com, 0); // низ связки: Position — центр масс, он на com выше
            var fl = ShotDir(v, att, out double speed);
            var side = Vector3.Cross(up, fl);
            if (side.sqrMagnitude < 0.01f) side = Vector3.ProjectOnPlane(att * Vector3.right, up); // вертикальный полёт: «вбок» — по оси борта
            side = side.sqrMagnitude < 0.01f ? AnyPerp(up) : side.normalized;
            var upHint = up;
            bool smooth = false;
            aim = mid;

            switch (shot)
            {
                case Shot.Tracking:
                {
                    // Из-под хвоста вдогонку: на подъёме ракеты это взгляд вдоль струи, в горизонтали — низкий сзади-сбоку.
                    fov = 55;
                    var dir = (-fl * 1f - up * 0.2f + side * (s * 0.5f)).normalized;
                    dir = Quaternion.AngleAxis(8 * Mathf.Sin(0.5f * t + ph), up) * dir; // лёгкое покачивание — «рука оператора»
                    float d = FrameDist(L, fov, 0.45f) * (1.1f - 0.02f * t);          // и медленный наезд
                    pos = mid + dir * d;
                    break;
                }
                case Shot.Side:
                {
                    fov = 45;
                    float d = FrameDist(L, fov, 0.6f);
                    pos = mid + side * (s * d) + up * (d * 0.08f) + fl * (Mathf.Sin(t * 0.35f + ph) * d * 0.2f);
                    break;
                }
                case Shot.LongLens:
                {
                    // Узкий FOV из дали. Камера «глядит вниз» на угол, чуть меньший понижения горизонта на этой высоте, —
                    // тогда линия горизонта и планета остаются в кадре за бортом. Пара: dip по радиусу тела и высоте борта.
                    fov = Mathf.Max(8f, 13f - 0.15f * t);
                    float d = FrameDist(L, fov, 0.3f);
                    double r = v.Position.magnitude;
                    float dip = (float)(System.Math.Acos(System.Math.Min(1.0, v.Body.Radius / r)) * Constants.Rad2Deg);
                    float p = Mathf.Clamp(dip * 0.8f, 3f, 30f) * Mathf.Deg2Rad;
                    var h = Vector3.ProjectOnPlane(side * (s * 0.85f) - fl * 0.5f, up);
                    h = Quaternion.AngleAxis(2f * t * s, up) * (h.sqrMagnitude < 1e-4f ? side : h.normalized);
                    pos = mid + (h * Mathf.Cos(p) + up * Mathf.Sin(p)) * d;
                    break;
                }
                case Shot.Engines:
                {
                    fov = 65;
                    float rr = Mathf.Max(L * 0.35f, 6f);
                    float a = (42 + 10 * Mathf.Sin(t * 0.6f + ph)) * Mathf.Deg2Rad; // угол от оси «вниз»
                    float phi = ph + 9f * t * s * Mathf.Deg2Rad;
                    var down = att * Vector3.down;
                    var ring = att * Vector3.right * Mathf.Cos(phi) + att * Vector3.forward * Mathf.Sin(phi);
                    pos = bottom + (down * Mathf.Cos(a) + ring * Mathf.Sin(a)) * rr;
                    aim = bottom + att * new Vector3(0, L * 0.1f, 0);
                    upHint = att * Vector3.up;
                    break;
                }
                case Shot.Orbit:
                {
                    fov = 50;
                    float d = FrameDist(L, fov, 0.4f);
                    float az = ph + 12f * t * s * Mathf.Deg2Rad;
                    float el = (10 + 8 * Mathf.Sin(0.3f * t + ph)) * Mathf.Deg2Rad;
                    var h = Quaternion.AngleAxis(az * Mathf.Rad2Deg, up) * side;
                    pos = mid + (h * Mathf.Cos(el) + up * Mathf.Sin(el)) * d;
                    break;
                }
                case Shot.Tripod:
                    TripodPose(u, v, up, fl, side, speed, L, s, mid, ref pos, ref fov);
                    smooth = true;
                    break;
                default: // Overhead
                {
                    // Над бортом, фон — планета; чуть сзади по ходу, чтобы борт не казался нарисованным в плане.
                    fov = 70;
                    float d = FrameDist(L, fov, 0.3f) * (1 + 0.03f * t);
                    var back = Vector3.ProjectOnPlane(-fl, up);
                    back = back.sqrMagnitude < 0.01f ? side : back.normalized;
                    const float tilt = 15 * Mathf.Deg2Rad;
                    pos = mid + (up * Mathf.Cos(tilt) + back * Mathf.Sin(tilt)) * d;
                    var f = (mid - pos).normalized;
                    upHint = Quaternion.AngleAxis(3f * t * s, f) * -back; // медленный крен кадра — жизнь в статике
                    break;
                }
            }

            var want = LookQ(aim - pos, upHint, side);
            rot = smooth && aimValid ? Quaternion.Slerp(aimRot, want, 1 - Mathf.Exp(-AimRate * Time.unscaledDeltaTime)) : want;
            aimRot = rot;
            aimValid = true;
        }

        /// <summary>
        /// «Штатив»: камера стоит, борт пролетает мимо. У земли (ниже GroundShotAlt, ускорение ≈ ×1) — точка на грунте
        /// сбоку и впереди по ходу, как у стартовых камер; в осях тела, поэтому не уплывает с вращением планеты.
        /// По мере удаления борта оператор «зумит»: FOV сужается. В космосе и на ускорении времени грунт не годится
        /// (борт уходит за кадр) — камера дрейфует рядом с бортом и обгоняет его, звёзды стоят.
        /// </summary>
        void TripodPose(Universe u, Vessel v, Vector3 up, Vector3 fl, Vector3 side, double speed, float L, float s, Vector3 mid,
                        ref Vector3 pos, ref float fov)
        {
            double agl = v.Body.AltitudeAboveTerrain(v.Position);
            bool ground = u.EffectiveWarp < TripodMaxWarp && agl < GroundShotAlt;
            var worldV = FloatingOrigin.WorldP(v);
            if (anchorInit && (anchorBody != v.Body || anchorGround != ground)) anchorInit = false;
            if (anchorInit && anchorGround)
            {
                var ap = anchorBody.Position + anchorBody.Orientation * anchorBF;
                if (Vector3d.Distance(ap, worldV) > TripodMaxDist) anchorInit = false;
            }
            if (!anchorInit)
            {
                anchorInit = true;
                anchorGround = ground;
                anchorBody = v.Body;
                anchorTime = Time.unscaledTime;
                tripodFov = 38;
                if (ground)
                {
                    var upD = v.Position.normalized;
                    var hor = Vector3d.ProjectOnPlane(new Vector3d(fl.x, fl.z, fl.y), upD); // Unity → P: SwapYZ
                    double horMag = hor.magnitude;
                    hor = horMag > 0.2 ? hor / horMag : Vector3d.AnyPerpendicular(upD).normalized;
                    double speedH = speed * horMag;
                    // Впереди по ходу — ровно столько, сколько борт пролетит за пару секунд; с места (на старте) — просто сбоку.
                    double ahead = speedH > 20 ? System.Math.Min(speedH * 2.5, 1500) : 0;
                    double lateral = System.Math.Max(80, 4 * L) + System.Math.Min(speedH * 0.3, 600);
                    var sd = Vector3d.Cross(upD, hor).normalized * s;
                    var p = v.Position + hor * ahead + sd * lateral;
                    // Глаза оператора над грунтом (+ запас к GroundClearance, чтобы базовая защита не дёргала позу).
                    // Выше земли — выше и камера: иначе борт на высоте километра виден «в зените».
                    double want = GroundClearance + 0.3 + 0.3 * System.Math.Max(0, agl);
                    p += p.normalized * (want - v.Body.AltitudeAboveTerrain(p));
                    anchorBF = v.Body.Orientation.Inverse * p;
                }
                else
                {
                    float d0 = Mathf.Max(4 * L, 30);
                    anchorOffset = (-fl * 0.8f + side * (s * 0.6f)).normalized * d0;
                    anchorDrift = fl * (d0 * 0.18f); // камера обгоняет борт: за 8 с проходит ≈ 1,4 d0
                }
            }

            float wantFov = 38;
            if (anchorGround)
            {
                pos = FloatingOrigin.ToUnity(anchorBody.Position + anchorBody.Orientation * anchorBF);
                float dist = Mathf.Max((mid - pos).magnitude, 1f);
                wantFov = Mathf.Clamp(2 * Mathf.Atan2(L * 1.8f, dist) * Mathf.Rad2Deg, 8f, 45f);
            }
            else pos = mid + anchorOffset + anchorDrift * (Time.unscaledTime - anchorTime);
            tripodFov = Mathf.Lerp(tripodFov, wantFov, 1 - Mathf.Exp(-3 * Time.unscaledDeltaTime));
            fov = tripodFov;
        }

        // ------------------------------------------------------------------ взгляд на планету

        float lookBlend, lookYaw, lookPitch;
        /// <summary>Время поворота к планете и обратно, с. Наклон от строго радиального взгляда назад по ходу, °:
        /// строго «вниз» борт виден сверху, как на плане, а так — сбоку, и планета остаётся фоном.</summary>
        const float LookTime = 0.5f, LookTilt = 15;

        /// <summary>
        /// Камера уходит за борт (по другую сторону от тела) и смотрит на центр тела: борт на переднем плане, планета за ним.
        /// Это переопределение ориентации поверх любого режима: плавно (LookTime) включается и так же возвращается к
        /// прежнему виду. Расстояние до цели остаётся прежним — зум колесом работает и здесь (вне кино).
        /// </summary>
        void ApplyLook(Vessel v, Quaternion att, Vector3 up, Vector3 aim, ref Vector3 pos, ref Quaternion rot)
        {
            lookBlend = Mathf.MoveTowards(lookBlend, LookAtBodyOn ? 1 : 0, Time.unscaledDeltaTime / LookTime);
            if (lookBlend <= 0) { lookYaw = lookPitch = 0; return; }
            var fl = ShotDir(v, att, out _);
            var back = Vector3.ProjectOnPlane(-fl, up);
            back = back.sqrMagnitude < 0.01f ? Vector3.ProjectOnPlane(att * Vector3.right, up) : back; // вертикальный подъём: сбоку
            back = back.sqrMagnitude < 0.01f ? AnyPerp(up) : back.normalized;
            const float tilt = LookTilt * Mathf.Deg2Rad;
            var fwd = -(up * Mathf.Cos(tilt) + back * Mathf.Sin(tilt));
            // «Верх» кадра — по ходу полёта: борт летит вверх по экрану, планета внизу.
            var look = Quaternion.LookRotation(fwd, -back) * Quaternion.Euler(lookPitch, lookYaw, 0);
            float k = lookBlend * lookBlend * (3 - 2 * lookBlend);
            float dist = Mathf.Max((aim - pos).magnitude, 3f);
            rot = Quaternion.Slerp(rot, look, k);
            pos = aim - rot * Vector3.forward * dist;
        }
    }
}
