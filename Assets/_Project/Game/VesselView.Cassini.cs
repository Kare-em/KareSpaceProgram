using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// «Гюйгенс» на боку «Кассини» (GDD §5.3): в раскладке ядра три секции зонда стоят стопкой над орбитером (так проще
    /// ЦМ и ступени), а рисуются, как у настоящего, сбоку двигательного модуля: ось зонда горизонтальна, экран наружу.
    /// Геометрию подвеса считает ядро (SectionDef.SideMount*, Vessel.SideMountPoint) — по ней же Vessel.Split выпускает
    /// зонд, поэтому при отделении он не прыгает. После отделения зонд рисуется обычной стопкой своего борта.
    /// </summary>
    public sealed partial class VesselView
    {
        /// <summary>Поворот секций зонда на подвесе: их ось −Y (низ экрана) смотрит наружу (Vessel.SideMountTurn).</summary>
        static readonly Quaternion HuygensMountTurn = FloatingOrigin.ToQuaternion(Vessel.SideMountTurn);

        /// <summary>Секция i — часть груза, висящего на боку несущей (несущая ещё присоединена).</summary>
        bool HuygensOnSide(int i) => Vessel.SideMountPoint(i, baseHeight, 0, out _);

        /// <summary>Место секции зонда i в осях вида (начало — ЦМ борта, com — в осях раскладки baseHeight).</summary>
        Vector3 HuygensMount(int i, double com)
        {
            if (Vessel.SideMountPoint(i, baseHeight, com, out var at)) return new Vector3((float)at.x, (float)at.y, (float)at.z);
            return new Vector3(0, (float)(baseHeight[i] - com), 0);
        }
    }
}
