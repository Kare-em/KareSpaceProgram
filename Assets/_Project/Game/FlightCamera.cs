using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Орбитальная камера вокруг активного борта (GDD §10.3): ПКМ — вращение, колесо — дистанция.
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

        Camera cam;
        Quaternion frame = Quaternion.identity;
        Vector3 lastUp = Vector3.up;
        bool frameValid;
        Vessel fitVessel;
        double fitLength;

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
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (u?.Active == null || MapView.IsOpen) return;
            var v = u.Active;

            if (Input.GetMouseButton(1))
            {
                Yaw += Input.GetAxis("Mouse X") * OrbitSpeed;
                Pitch = Mathf.Clamp(Pitch - Input.GetAxis("Mouse Y") * OrbitSpeed, -89, 89);
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0) Distance = Mathf.Clamp(Distance * Mathf.Pow(0.88f, wheel), MinDistance, MaxDistance);

            // Местная вертикаль; кадр камеры доворачивается на её изменение, а не строится заново —
            // так нет закрутки по азимуту при движении по орбите.
            var up = FloatingOrigin.DirToUnity(v.Position.normalized);
            if (!frameValid)
            {
                // Лицом на север: восток справа, и D (нос на восток, см. FlightPhysics.PlaceOnSurface) уводит нос вправо (§10.1).
                var pole = FloatingOrigin.DirToUnity(v.Body.OrientationAt(u.Time) * Vector3d.forward);
                var north = Vector3.ProjectOnPlane(pole, up);
                frame = north.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(north, up) : Quaternion.FromToRotation(Vector3.up, up);
                frameValid = true;
            }
            else frame = Quaternion.FromToRotation(lastUp, up) * frame;
            lastUp = up;

            // Цель — середина борта вместе с полосой под соплами, а не центр масс: ЦМ у полной ракеты низко, и при старте нос уходил
            // за кадр. Дистанция подгоняется под длину при смене борта/отделении, дальше — колесом.
            v.MassProperties(out _, out double com, out double length, out _);
            if (v != fitVessel || System.Math.Abs(length - fitLength) > 0.05 * fitLength)
            {
                fitVessel = v;
                fitLength = length;
                float half = (float)length * (1 + FitBelow) * 0.5f * FitMargin;
                Distance = Mathf.Clamp(half / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), MinDistance, MaxDistance);
            }

            var rot = frame * Quaternion.Euler(Pitch, Yaw, 0);
            var target = FloatingOrigin.ToUnity(FloatingOrigin.WorldP(v)) // ≈ 0 — борт и есть ноль
                       + FloatingOrigin.ToQuaternion(v.Attitude) * new Vector3(0, (float)(length * (1 - FitBelow) * 0.5 - com), 0);
            transform.SetPositionAndRotation(target - rot * Vector3.forward * Distance, rot);
        }
    }
}
