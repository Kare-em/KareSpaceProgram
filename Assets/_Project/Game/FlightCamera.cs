using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Орбитальная камера вокруг активного борта (GDD §10.3): ПКМ — вращение, колесо — дистанция,
    /// зажатое колесо — сдвиг центра (щелчок без сдвига — вернуть на борт).
    /// «Верх» камеры — местная вертикаль тела (в Unity-кадре она произвольна: оси эклиптики, §2.6),
    /// поворачивается вслед за ней без скачков.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(Camera))]
    public sealed class FlightCamera : MonoBehaviour
    {
        /// <summary>
        /// Пара: оболочка BodyRenderer.ShellStart + ShellWidth = 9,5e6 укладывает все тела внутрь FarClip (§2.7).
        /// Не больше 1e7 и far/near не больше 1e7 — иначе HDRP теряет тени Солнца (замер 01.10.2026).
        /// Пара: near 1 м &lt; FlightCamera.MinDistance — борт не режется ближней плоскостью.
        /// </summary>
        public const float NearClip = 1f, FarClip = 1e7f;

        public float Distance = 40;
        public float MinDistance = 5, MaxDistance = 20000;
        public float Yaw = 30, Pitch = 10;
        public float OrbitSpeed = 3;
        /// <summary>Запас кадра при автоподгонке: борт занимает 1/FitMargin высоты экрана (§10.3).</summary>
        const float FitMargin = 1.3f;
        /// <summary>Под соплами в кадр берётся ещё столько длин борта — место факелу и нижней панели HUD.
        /// Пара: VesselView.PlumeLengthSL (факел ≈ 12 радиусов сопла ≈ 0,4 длины «Востока») и высота
        /// нижнего ряда FlightHud (≈ 12 % экрана).</summary>
        const float FitBelow = 0.4f;
        /// <summary>Сдвиг центра СКМ: доля дистанции на единицу оси мыши — точка под курсором идёт вслед за рукой
        /// примерно при FOV 60°. Предел — PanLimit длин борта: дальше центр теряет борт, и вращение уводит его из кадра.</summary>
        const float PanSpeed = 0.06f, PanLimit = 3f;
        /// <summary>Щелчок СКМ без сдвига (сумма осей мыши меньше порога) возвращает центр на борт.</summary>
        const float PanClick = 0.2f;
        /// <summary>
        /// Камера не опускается ниже стольких метров над рельефом (§10.3). Снизу грунт отсечён по задним граням —
        /// виден не грунт, а небо, и всё, что лежит на грунте, рисуется поверх борта: при взлёте «Сервейора» ночью
        /// камера под Луной (Pitch −35°, 6 м) видела клубы пыли под соплом оранжевыми пятнами «сквозь корабль» —
        /// их принимали за свечение Земли (замер 05.10.2026). Пара: NearClip 1 м — ближняя плоскость не режет грунт
        /// под камерой; по физике рельефа (Terrain.Height), меш патча совпадает с ней до долей метра.
        /// </summary>
        const float GroundClearance = 2f;

        /// <summary>
        /// Режимы камеры (§10.3, F5 / Shift+F5): от чего отсчитываются Yaw/Pitch.
        /// Авто — местная вертикаль (горизонт ровный); Свободная — инерциальные оси (звёзды стоят, планета плывёт);
        /// Орбитальная — плоскость орбиты горизонтальна, тело всегда в одной стороне кадра; Погоня — за вектором
        /// скорости (в атмосфере — относительно поверхности); Связанная — оси борта, камера вращается вместе с ним.
        /// </summary>
        public enum CameraMode { Auto, Free, Orbital, Chase, Locked }
        public static CameraMode Mode { get; private set; }
        static readonly string[] ModeNames = { "авто (горизонт)", "свободная", "орбитальная", "погоня (по скорости)", "связанная с бортом" };
        public static string ModeName => ModeNames[(int)Mode];
        /// <summary>Скорость доворота «погони» за скоростью, 1/с: мгновенный поворот дёргал кадр на каждом рыскании.</summary>
        const float ChaseRate = 4;
        /// <summary>Ниже скорости, м/с, направление полёта не определено — «погоня» смотрит по носу.</summary>
        const double ChaseMinSpeed = 1;

        public static string CycleMode(int dir)
        {
            int n = ModeNames.Length;
            Mode = (CameraMode)((((int)Mode + dir) % n + n) % n);
            return ModeName;
        }

        CameraMode appliedMode;

        /// <summary>Камера правой половины сплит-скрина (FlightView): смотрит на FlightView.Second.</summary>
        public bool Secondary;
        /// <summary>Чья половина приняла нажатие ПКМ/СКМ: тянуть мышь можно и через границу половин.</summary>
        static FlightCamera dragOwner;

        Camera cam;
        Quaternion frame = Quaternion.identity;
        Vector3 lastUp = Vector3.up;
        bool frameValid;
        Vessel fitVessel;
        double fitLength;
        /// <summary>Сдвиг центра в осях борта: едет и вращается вместе с ним, как деталь корпуса.</summary>
        Vector3 pan;
        float panTravel;

        void Awake()
        {
            cam = GetComponent<Camera>();
            cam.nearClipPlane = NearClip;
            cam.farClipPlane = FarClip;
        }

        void OnEnable()
        {
            cam = GetComponent<Camera>();
            cam.nearClipPlane = NearClip;
            cam.farClipPlane = FarClip;
            // OnMouse*-событий в игре нет, а SendMouseEvents каждый кадр строит луч через камеру: при far/near = 1e7
            // (в карте ещё больше) матрица вырождается — предупреждение «Screen position out of view frustum».
            cam.eventMask = 0;
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (u?.Active == null || MapView.IsOpen) return;
            var v = Secondary ? FlightView.Second : FlightView.Main;
            if (v == null) return;

            // Мышь — у камеры под курсором; без сплита — целиком у главной.
            bool split = FlightView.Second != null;
            bool under = !split ? !Secondary : cam.pixelRect.Contains(Input.mousePosition);
            if ((Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2)) && under) dragOwner = this;
            bool held = Input.GetMouseButton(1) || Input.GetMouseButton(2);
            bool mine = held ? dragOwner == this : under;

            if (mine && Input.GetMouseButton(1))
            {
                Yaw += Input.GetAxis("Mouse X") * OrbitSpeed;
                Pitch = Mathf.Clamp(Pitch - Input.GetAxis("Mouse Y") * OrbitSpeed, -89, 89);
            }
            float wheel = mine ? Input.mouseScrollDelta.y : 0;
            if (wheel != 0) Distance = Mathf.Clamp(Distance * Mathf.Pow(0.88f, wheel), MinDistance, MaxDistance);

            // Местная вертикаль; кадр камеры доворачивается на её изменение, а не строится заново —
            // так нет закрутки по азимуту при движении по орбите.
            var up = FloatingOrigin.DirToUnity(v.Position.normalized);
            var want = ModeFrame(v, up);
            if (!frameValid || appliedMode != Mode)
            {
                // Смена режима: новый базис, а Yaw/Pitch пересчитываются так, чтобы камера осталась на месте (кроме крена).
                var old = frame * Quaternion.Euler(Pitch, Yaw, 0);
                bool had = frameValid;
                frame = want ?? (Mode == CameraMode.Free && had ? frame : AutoFrame(v, u, up));
                if (had) KeepView(old);
                frameValid = true;
                appliedMode = Mode;
            }
            else if (Mode == CameraMode.Auto) frame = Quaternion.FromToRotation(lastUp, up) * frame;
            else if (Mode == CameraMode.Chase && want.HasValue) frame = Quaternion.Slerp(frame, want.Value, 1 - Mathf.Exp(-ChaseRate * Time.unscaledDeltaTime));
            else if (want.HasValue) frame = want.Value;
            // Свободная — базис не трогаем: оси Unity инерциальные (эклиптика, §2.6).
            lastUp = up;

            // Цель — середина борта вместе с полосой под соплами, а не центр масс: ЦМ у полной ракеты низко, и при старте нос уходил
            // за кадр. Дистанция подгоняется под длину при смене борта/отделении, дальше — колесом.
            v.MassProperties(out _, out double com, out double length, out _);
            // Раскрытый парашют — тоже в кадр (§6.4): цель остаётся на борту, дистанция растёт на высоту купола.
            double fit = length + VesselView.ChuteReach(v);
            if (v != fitVessel) pan = Vector3.zero;
            if (v != fitVessel || System.Math.Abs(fit - fitLength) > 0.05 * fitLength)
            {
                fitVessel = v;
                fitLength = fit;
                float half = (float)fit * (1 + FitBelow) * 0.5f * FitMargin;
                Distance = Mathf.Clamp(half / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), MinDistance, MaxDistance);
            }

            var rot = frame * Quaternion.Euler(Pitch, Yaw, 0);
            var att = FloatingOrigin.ToQuaternion(v.Attitude);
            if (mine && Input.GetMouseButtonDown(2)) panTravel = 0;
            if (mine && Input.GetMouseButton(2))
            {
                float mx = Input.GetAxis("Mouse X"), my = Input.GetAxis("Mouse Y");
                panTravel += Mathf.Abs(mx) + Mathf.Abs(my);
                // Тянем картинку за руку: центр уходит против движения мыши в плоскости экрана.
                var d = rot * new Vector3(-mx, -my, 0) * (Distance * PanSpeed);
                pan = Vector3.ClampMagnitude(pan + Quaternion.Inverse(att) * d, (float)fit * PanLimit);
            }
            if (dragOwner == this && Input.GetMouseButtonUp(2) && panTravel < PanClick) pan = Vector3.zero;
            var target = FloatingOrigin.ToUnity(FloatingOrigin.WorldP(v)) // ≈ 0 — борт и есть ноль
                       + att * (new Vector3(0, (float)(length * (1 - FitBelow) * 0.5 - com), 0) + pan)
                       // Купол висит над бортом по вертикали: цель на середину связки, иначе купол режется верхним краем
                       // (кадр на 6 км, 01.10.2026). Пара: ChuteReach — та же высота, что и в подгонке дистанции.
                       + up * (float)(VesselView.ChuteReach(v) * 0.5);
            var pos = target - rot * Vector3.forward * Distance;
            // Над рельефом (см. GroundClearance): поднимаем по вертикали и смотрим на ту же цель — кадр «с земли вверх».
            var rel = pos - FloatingOrigin.ToUnity(FloatingOrigin.WorldP(v));
            double agl = v.Body.AltitudeAboveTerrain(v.Position + new Vector3d(rel.x, rel.y, rel.z).SwapYZ);
            if (agl < GroundClearance)
            {
                pos += up * (float)(GroundClearance - agl);
                rot = Quaternion.LookRotation(target - pos, up);
            }
            transform.SetPositionAndRotation(pos, rot);
        }

        /// <summary>Лицом на север: восток справа, и D (нос на восток, см. FlightPhysics.PlaceOnSurface) уводит нос вправо (§10.1).</summary>
        static Quaternion AutoFrame(Vessel v, Universe u, Vector3 up)
        {
            var pole = FloatingOrigin.DirToUnity(v.Body.OrientationAt(u.Time) * Vector3d.forward);
            var north = Vector3.ProjectOnPlane(pole, up);
            return north.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(north, up) : Quaternion.FromToRotation(Vector3.up, up);
        }

        /// <summary>Базис режима; null — режим ведёт базис сам (Авто — доворотом за вертикалью, Свободная — никак).</summary>
        Quaternion? ModeFrame(Vessel v, Vector3 up)
        {
            switch (Mode)
            {
                case CameraMode.Orbital:
                {
                    var h = FloatingOrigin.DirToUnity(Vector3d.Cross(v.Position, v.Velocity).normalized);
                    if (h.sqrMagnitude < 0.5f) return null;
                    return Quaternion.LookRotation(-up, h); // вперёд — на тело, верх — нормаль орбиты
                }
                case CameraMode.Chase:
                {
                    // В атмосфере — скорость относительно воздуха (поверхности): на взлёте орбитальная смотрит вбок на 400 м/с.
                    var vel = v.Velocity;
                    if (v.Body.HasAtmosphere && v.Altitude < v.Body.AtmosphereTop) vel -= v.Body.SurfaceVelocity(v.Position);
                    var dir = vel.magnitude > ChaseMinSpeed ? FloatingOrigin.DirToUnity(vel.normalized) : FloatingOrigin.ToQuaternion(v.Attitude) * Vector3.up;
                    // Вертикальный подъём: «верх» по вертикали вырожден — берём верх прошлого кадра.
                    var top = Vector3.Cross(dir, up).sqrMagnitude > 1e-4f ? up : frame * Vector3.up;
                    return Quaternion.LookRotation(dir, top);
                }
                case CameraMode.Locked:
                    return FloatingOrigin.ToQuaternion(v.Attitude); // верх — нос борта (+Y): Yaw — облёт вокруг оси ракеты
                default:
                    return null;
            }
        }

        /// <summary>Подобрать Yaw/Pitch так, чтобы frame·Euler(Pitch, Yaw) смотрел туда же, куда old.</summary>
        void KeepView(Quaternion old)
        {
            var f = Quaternion.Inverse(frame) * (old * Vector3.forward);
            Pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(f.y, -1, 1)) * Mathf.Rad2Deg, -89, 89);
            Yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }
    }
}
