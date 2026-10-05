using System.Collections.Generic;
using UnityEngine;
using Kare.Space.Core;

namespace Kare.Space.Game
{
    /// <summary>
    /// Детали кораблей, которые не укладываются в общую схему «сопло под днищем» (§6.4, §6.12):
    /// факелы Draco на боку Crew Dragon, веер куполов с корнем сбоку оси, откидной носок и створки СБ на шарнирах.
    /// </summary>
    public sealed partial class VesselView
    {
        // ------------------------------------------------------------------ Draco

        /// <summary>
        /// Draco Crew Dragon (§6.4): 4 группы на конической стенке над нишами SuperDraco (b_crew_dragon: пары на
        /// az 45 + 90k, z 1,6–2,4), срез на высоте DracoY. Радиус DracoR — стенка капсулы на этой высоте
        /// (профиль 1,97 м на 0,55 → 1,32 м на 3,0: на 2,85 ≈ 1,36), чуть внутрь, чтобы сопло не висело в воздухе.
        /// Факел наклонён наружу на DracoTilt от «вниз»: струя не бьёт в багажник (раньше осевой факел шёл сквозь него).
        /// </summary>
        const float DracoY = 2.85f, DracoR = 1.33f, DracoTilt = 40, DracoAz0 = 45, DracoNozzleR = 0.1f;
        const int DracoGroups = 4;

        static bool HasDraco(SectionDef s) => s.Model == SectionModel.CrewDragon;

        /// <summary>Сопло первой группы — для OwnPlume: позиция среза, радиус струи. Кольца нет — rr = 0.</summary>
        static void DracoPlume(ref Vector3 at, ref float rr, ref float nr)
        {
            float a = DracoAz0 * Mathf.Deg2Rad;
            at = new Vector3(DracoR * Mathf.Cos(a), DracoY, DracoR * Mathf.Sin(a));
            rr = 0;
            nr = DracoNozzleR;
        }

        /// <summary>
        /// Наклон первой группы и копии факела на остальные три. Копии повторяют масштаб и яркость основного
        /// (SyncMirrors), свет — только у основного: четыре точечных источника на 0,4 кН — лишняя цена кадра.
        /// Дыма у Draco нет (NoSmoke): гидразин в вакууме, и шлейф ExhaustTrail тянулся бы из середины капсулы.
        /// </summary>
        void AddDracoMirrors(Part part, Transform nozzle)
        {
            Quaternion Tilt(float azDeg)
            {
                float a = azDeg * Mathf.Deg2Rad;
                var outward = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var dir = Quaternion.AngleAxis(DracoTilt, Vector3.Cross(Vector3.down, outward)) * Vector3.down;
                return Quaternion.FromToRotation(Vector3.down, dir);
            }
            nozzle.localRotation = Tilt(DracoAz0);
            part.NoSmoke = true;
            part.Mirrors = new List<Mirror>();
            for (int k = 1; k < DracoGroups; k++)
            {
                float az = DracoAz0 + 360f * k / DracoGroups, a = az * Mathf.Deg2Rad;
                var holder = new GameObject("Nozzle Draco " + k).transform;
                holder.SetParent(nozzle.parent, false);
                holder.localPosition = new Vector3(DracoR * Mathf.Cos(a), DracoY, DracoR * Mathf.Sin(a));
                holder.localRotation = Tilt(az);
                var plume = Instantiate(part.Plume.gameObject, holder, false);
                var glow = Instantiate(part.Glow.gameObject, holder, false);
                part.Mirrors.Add(new Mirror
                {
                    Plume = plume.transform, Glow = glow.transform,
                    CoreR = plume.GetComponent<Renderer>(), GlowR = glow.GetComponent<Renderer>(),
                });
            }
        }

        /// <summary>Копия факела: те же меши, масштаб и PropertyBlock, что у основного факела детали.</summary>
        sealed class Mirror
        {
            public Transform Plume, Glow;
            public Renderer CoreR, GlowR;
        }

        void SyncMirrors(Part p, bool burning)
        {
            foreach (var m in p.Mirrors)
            {
                m.Plume.gameObject.SetActive(burning);
                m.Glow.gameObject.SetActive(burning);
                if (!burning) continue;
                m.Plume.localScale = p.Plume.localScale;
                m.Glow.localScale = p.Glow.localScale;
                // Общий mpb — Clear перед каждым рендерером (соглашение проекта): GetPropertyBlock перезаписывает целиком.
                mpb.Clear();
                p.CoreR.GetPropertyBlock(mpb);
                m.CoreR.SetPropertyBlock(mpb);
                mpb.Clear();
                p.GlowR.GetPropertyBlock(mpb);
                m.GlowR.SetPropertyBlock(mpb);
            }
        }

        // ------------------------------------------------------------------ веер куполов

        /// <summary>
        /// Связка куполов (§6.4): у Crew Dragon 4, у «Аполлона» 3 (SectionDef.ChuteCount). Площадь делится поровну —
        /// физика в ядре та же, что у одного купола. Центры куполов разнесены на ChuteFanGap радиусов от соседа
        /// (зазор 10 %), стропы веера длиннее в ChuteFanRiser раз, чтобы купола не лежали плашмя.
        /// ChuteSideDrop — корень смещённых строп ниже верха секции: у Crew Dragon отсек под носком
        /// (низ носка на 3,0–3,06 м при длине 4,4 — b_crew_dragon). Пара: ChuteReach считает ту же геометрию.
        /// </summary>
        const float ChuteFanGap = 1.1f, ChuteFanRiser = 1.5f, ChuteSideDrop = 1.3f;

        /// <summary>Купол: n, радиус полностью раскрытого, высота куполов над корнем, вынос центра от оси веера.</summary>
        static void ChuteFan(SectionDef s, float r, out int n, out float full, out float rise, out float spread)
        {
            n = Mathf.Max(1, s.ChuteCount);
            full = Mathf.Sqrt((float)s.ParachuteArea / (n * Mathf.PI));
            rise = full * ChuteRiser * (n > 1 ? ChuteFanRiser : 1);
            spread = n > 1 ? r * ChuteFanGap / Mathf.Sin(Mathf.PI / n) : 0;
        }

        // ------------------------------------------------------------------ носок и створки СБ

        /// <summary>
        /// Откидной носок Crew Dragon (DeployKind.Nose, §6.12): шарнир — коробка на стенке (b_crew_dragon: Blender
        /// (1,3; 0; 3,1) → вид (−1,3; 3,1; 0), ось FBX x зеркальна), ось шарнира — касательная +Z. NoseOpen — угол
        /// открытого: носок уходит за шарнир и не заслоняет узел. Пара: время привода FlightPhysics.NoseDeployTime.
        /// </summary>
        static readonly Vector3 NoseHinge = new Vector3(-1.3f, 3.1f, 0);
        const float NoseOpen = 115;

        /// <summary>
        /// Створки СБ ПАО «Союза»: шарнир на конце корешка (b_soyuz_pao: от PAO_R до 1,55 м на высоте 0,7).
        /// Сложенные — вверх вдоль борта с завалом внутрь на 3°: панель длиной 4 м не выходит за обтекатель Ø3 м
        /// (верх на ≈1,3 м от оси, БО — 1,13). Пара: время привода FlightPhysics.PanelDeployTime.
        /// </summary>
        const float PanelHingeR = 1.55f, PanelHingeY = 0.7f, PanelStow = 93;

        /// <summary>
        /// Деталь смоделирована закрытой (носок на месте), а не в рабочем положении, как опоры: шарнир Hinge
        /// поворачивает на Stow·(1 − раскрытие) от Base. Base = открытый поворот, Stow = −угол — закрытый совпадает с
        /// моделью; Rest — место начала модели в открытом положении.
        /// </summary>
        void AddNose(Transform model, int section, GameBootstrap.DeployPart[] deploy, Color[] palette)
        {
            var open = Quaternion.AngleAxis(NoseOpen, Vector3.forward);
            foreach (var d in deploy)
            {
                if (d.Mesh == null) continue;
                var t = AddChild(model.gameObject, d.Mesh.name);
                var pal = new Color[d.Slots != null && d.Slots.Length > 0 ? d.Slots.Length : 1];
                for (int k = 0; k < pal.Length; k++) pal[k] = palette[d.Slots != null && d.Slots.Length > 0 ? Mathf.Min(d.Slots[k], palette.Length - 1) : 0];
                AddRenderer(t.gameObject, d.Mesh, pal);
                legs.Add(new Hinge
                {
                    T = t, Rest = NoseHinge + open * -NoseHinge, Pivot = NoseHinge, Base = open,
                    Axis = Vector3.forward, Stow = -NoseOpen, Section = section,
                });
            }
        }

        /// <summary>Точка крепления строп в осях секции: верх по оси или сбоку под носком (ChuteOffset).</summary>
        static Vector3 ChuteRoot(SectionDef s) =>
            s.ChuteOffset > 0 ? new Vector3((float)s.ChuteOffset, (float)s.Length - ChuteSideDrop, 0)
                              : new Vector3(0, (float)s.Length, 0);
    }
}
