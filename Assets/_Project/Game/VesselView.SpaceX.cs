using System.Collections.Generic;
using UnityEngine;
using Kare.Space.Core;

namespace Kare.Space.Game
{
    /// <summary>
    /// Многоразовые ступени SpaceX (§6.9): посадочные опоры и решётчатые рули I ступени Falcon 9 на шарнирах, баржа
    /// OCISLY. Корпус Falcon9_S1.fbx — без них (station_parts.py, F9_BAKED_DEPLOY = False). Super Heavy держит рули
    /// неподвижными в своей модели, закрылки Starship — общие ControlSurface (VesselView.Controls).
    /// </summary>
    public sealed partial class VesselView
    {
        /// <summary>
        /// Опора Falcon 9: длина, угол от вертикали вниз в раскрытом положении, ширина и толщина, м/°. Высота шарнира —
        /// L·cos θ, чтобы стопа встала в плоскость днища (ядро сажает борт по днищу, Vessel.PlaceOnSurface). Пара: размах
        /// по стопам 2·(r + L·sin θ) ≈ 19,6 м (настоящий ≈ 18 м); сложенная — вдоль бака вверх (поворот на 180° − θ).
        /// </summary>
        const float F9LegLength = 9.4f, F9LegAngle = 58, F9LegWidth = 0.6f, F9LegThick = 0.35f;
        /// <summary>
        /// Решётчатый руль Falcon 9: вылет, ширина, толщина решётки, м; шарнир — у нижней кромки сложенного руля на
        /// межступенчатом (b_f9_s1 клал их на z 39,15–40,65). Пара: F9FinHinge + F9FinLength = верх сложенного.
        /// </summary>
        const float F9FinLength = 1.5f, F9FinWidth = 1.2f, F9FinThick = 0.15f, F9FinHinge = 39.15f;
        /// <summary>Время раскрытия рулей после отделения, с (Demo-2: рули выходят сразу после переворота).</summary>
        const float F9FinDeployTime = 2.5f;

        /// <summary>Решётчатый руль: шарнир, ось складывания, поворот в раскрытом положении.</summary>
        struct GridFin { public Transform T; public Vector3 Pivot, Axis; public Quaternion Base; }
        readonly List<GridFin> gridFins = new List<GridFin>();
        float gridFinOpen;

        /// <summary>Крючок Rebuild: прежние узлы уничтожаются только в конце кадра — список чистим сразу.</summary>
        void ResetSpaceX() => gridFins.Clear();

        /// <summary>Крючок Rebuild: детали SpaceX секции i (после рулей и парашюта).</summary>
        void AddSpaceX(GameObject go, int i, SectionDef s, float r)
        {
            if (s.Recovery != null) RecoveryDeckView.Ensure(Vessel.Body, s.Recovery, bodyMat);
            if (s.Model != SectionModel.Falcon9S1) return;
            float th = F9LegAngle * Mathf.Deg2Rad;
            for (int k = 0; k < 4; k++)
            {
                var outward = Quaternion.Euler(0, 45 + 90 * k, 0) * Vector3.right;
                var down = outward * Mathf.Sin(th) - Vector3.up * Mathf.Cos(th);
                var pivot = outward * (r + 0.15f) + Vector3.up * (F9LegLength * Mathf.Cos(th));
                var leg = Strut(go, "Leg", pivot, outward, down, F9LegLength, F9LegWidth, F9LegThick, BlackColor, out var baseRot);
                // Стопа — металлическая плита на конце.
                var foot = AddChild(leg.gameObject, "Foot");
                foot.localPosition = new Vector3(0, -F9LegLength, 0);
                foot.localScale = new Vector3(0.3f, 0.25f, 1.0f);
                AddRenderer(foot.gameObject, ProcMesh.Box(), MetalColor);
                legs.Add(new Hinge
                {
                    T = leg, Rest = pivot, Pivot = pivot, Base = baseRot, Section = i, Squeeze = true,
                    Axis = Vector3.Cross(outward, Vector3.up), Stow = s.Deploy == DeployKind.None ? 0 : 180 - F9LegAngle,
                });
            }
            for (int k = 0; k < 4; k++)
            {
                var outward = Quaternion.Euler(0, 90 * k, 0) * Vector3.right;
                var pivot = outward * (r + 0.1f) + Vector3.up * F9FinHinge;
                var fin = Strut(go, "Grid Fin", pivot, outward, outward, F9FinLength, F9FinWidth, F9FinThick, MetalColor, out var baseRot);
                gridFins.Add(new GridFin { T = fin, Pivot = pivot, Axis = Vector3.Cross(outward, Vector3.up), Base = baseRot });
            }
        }

        /// <summary>
        /// Брус от шарнира pivot по направлению dir длиной len: узел в шарнире, его −Y — вдоль dir, Z — по касательной
        /// к корпусу (ширина), X — толщина. baseRot — поворот узла в раскрытом положении.
        /// </summary>
        Transform Strut(GameObject go, string name, Vector3 pivot, Vector3 outward, Vector3 dir, float len, float width, float thick,
            Color color, out Quaternion baseRot)
        {
            var t = AddChild(go, name);
            var tangent = Vector3.Cross(Vector3.up, outward).normalized;
            baseRot = Quaternion.LookRotation(tangent, -dir.normalized);
            t.localPosition = pivot;
            t.localRotation = baseRot;
            var mesh = AddChild(t.gameObject, "Mesh");
            mesh.localPosition = new Vector3(0, -len * 0.5f, 0);
            mesh.localScale = new Vector3(thick, len, width);
            AddRenderer(mesh.gameObject, ProcMesh.Box(), color);
            return t;
        }

        /// <summary>
        /// Крючок LateUpdate: рули Falcon 9 сложены на подъёме и раскрываются, когда ступень летит одна (после отделения
        /// её ведёт BoosterLandingAutopilot — переворот, вход и посадка на рулях).
        /// </summary>
        void UpdateSpaceX()
        {
            if (gridFins.Count == 0) return;
            int attached = 0;
            for (int i = 0; i < Vessel.Attached.Length; i++) if (Vessel.Attached[i]) attached++;
            gridFinOpen = Mathf.MoveTowards(gridFinOpen, attached == 1 ? 1 : 0, Time.deltaTime / F9FinDeployTime);
            float s = Mathf.SmoothStep(0, 1, gridFinOpen);
            foreach (var f in gridFins)
            {
                if (f.T == null) continue;
                // Сложенный руль смотрит вверх вдоль межступенчатого: +90° вокруг Cross(наружу, вверх) поднимает «наружу» в «вверх».
                f.T.localRotation = Quaternion.AngleAxis(90 * (1 - s), f.Axis) * f.Base;
                f.T.localPosition = f.Pivot;
            }
        }
    }
}
