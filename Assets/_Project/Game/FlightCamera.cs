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
        /// <summary>Пара: сжатие тел BodyRenderer.CompressionStart = 1e7 укладывает всё внутрь 2e8 (§2.7).</summary>
        public const float NearClip = 0.1f, FarClip = 2e8f;

        public float Distance = 40;
        public float MinDistance = 5, MaxDistance = 20000;
        public float Yaw = 30, Pitch = 10;
        public float OrbitSpeed = 3;

        Camera cam;
        Quaternion frame = Quaternion.identity;
        Vector3 lastUp = Vector3.up;
        bool frameValid;

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
                frame = Quaternion.FromToRotation(Vector3.up, up);
                frameValid = true;
            }
            else frame = Quaternion.FromToRotation(lastUp, up) * frame;
            lastUp = up;

            var rot = frame * Quaternion.Euler(Pitch, Yaw, 0);
            var target = FloatingOrigin.ToUnity(FloatingOrigin.WorldP(v)); // ≈ 0 — борт и есть ноль
            transform.SetPositionAndRotation(target - rot * Vector3.forward * Distance, rot);
        }
    }
}
