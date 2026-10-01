using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Плавающий ноль (GDD §2.6): активный борт всегда в (0,0,0) Unity. Вся цепочка
    /// «гелиоцентрический double в кадре P → смещение от нуля → оси Unity (SwapYZ) → float» живёт здесь,
    /// чтобы ни один рендерер не делал своё преобразование.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public sealed class FloatingOrigin : MonoBehaviour
    {
        /// <summary>Положение нуля Unity в гелиоцентрическом кадре P, м.</summary>
        public static Vector3d OriginP { get; private set; }

        void LateUpdate() => Refresh();

        /// <summary>Вызывается и после шага симуляции, и перед рендером — порядок LateUpdate не важен.</summary>
        public static void Refresh()
        {
            var u = GameBootstrap.U;
            if (u?.Active == null) return;
            OriginP = WorldP(u.Active);
        }

        /// <summary>Гелиоцентрическое положение борта в P.</summary>
        public static Vector3d WorldP(Vessel v) => v.Body.Position + v.Position;

        /// <summary>Точка P (гелиоцентр, double) → позиция Unity (float) относительно нуля.</summary>
        public static Vector3 ToUnity(Vector3d worldP) => ToVector3((worldP - OriginP).SwapYZ);

        /// <summary>Направление/вектор в P → оси Unity без сдвига нуля.</summary>
        public static Vector3 DirToUnity(Vector3d dirP) => ToVector3(dirP.SwapYZ);

        public static Vector3 ToVector3(Vector3d v) => new Vector3((float)v.x, (float)v.y, (float)v.z);

        public static Quaternion ToQuaternion(QuaternionD q) => new Quaternion((float)q.x, (float)q.y, (float)q.z, (float)q.w);

        /// <summary>Ориентация тела (P, тело → инерциальные оси) → Unity. Vessel.Attitude уже в U — ему не нужно.</summary>
        public static Quaternion BodyRotation(CelestialBody b) => ToQuaternion(b.Orientation.SwapYZ);
    }
}
