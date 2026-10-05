using System;
using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.Game
{
    /// <summary>
    /// Стартовый комплекс (GDD §7): у каждого семейства ракет свой стол, как на настоящем космодроме.
    /// Бетон (плиты, котлованы газоотвода) строится здесь из коробок, сталь — FBX из Blender
    /// (Tools/blender/launch_pads.py, Models/Pad_*.fbx). Привязан к телу в точке космодрома, физики не несёт:
    /// высоту стола ядро задаёт само (LaunchSite.PadHeight), модели считаны от неё.
    /// Что отводится при отрыве (по секундам с момента взлёта, у каждой детали своя задержка):
    /// фермы-«тюльпан» и наклонные мачты (Tilt — вокруг горизонтальной оси у основания),
    /// поворотные стрелы башни (Swing — вокруг вертикали у шарнира).
    /// Ночью стол освещают четыре прожекторные мачты (Floodlights): свет — на корпус, включаются по Солнцу над столом.
    /// </summary>
    public sealed class LaunchPadView : MonoBehaviour
    {
        enum Kind { R7, Proton, Redstone, Atlas, Titan, Saturn, Starbase }

        /// <summary>Полуширина бетона и проёма над газоотводом Р-7, м. Пара: связка сопел (≈ радиус днища)
        /// должна пролезать в проём — HoleHalf больше радиуса первой ступени.</summary>
        const float SlabHalf = 22, HoleHalf = 5.5f;
        /// <summary>Газоотводный лоток Р-7 на юг: полуширина, м. Пара: мост в Pad_R7 шире (±3,4) и ложится на кромки.</summary>
        const float TrenchHalf = 3;
        /// <summary>Повтор текстуры бетона, м (на тайле 2×2 плиты — плита 4 м).</summary>
        const float ConcreteTile = 8;
        /// <summary>Где ферма Р-7 касается корпуса над столом, сечение фермы, м.</summary>
        const float ArmReach = 9, ArmWidth = 0.9f;
        /// <summary>Длина фермы в FBX, м (Tools/blender). Пара: ArmWidth — сечение модели 0,9 м, тянем только по длине.</summary>
        const float TrussModelLength = 8.2f;
        /// <summary>Отвод ферм Р-7: на сколько градусов наружу и за сколько секунд.</summary>
        const float ReleaseAngle = 70, ReleaseTime = 1.6f;
        /// <summary>Дальше этого стол не рисуем — меньше пикселя.</summary>
        const float DrawDistance = 60000;

        // ---- пары с Tools/blender/launch_pads.py: меняешь там — меняй здесь
        /// <summary>Pad_Mast: вылет и высота, м; центр стрелы-вылета на высоте MastH − 4,2 (girder_y z0 = MastH − 4,6, h 0,8).</summary>
        const float MastReach = 8.45f, MastH = 24, MastArmFromTop = 4.2f;
        /// <summary>Pad_Arm_Light / Pad_Arm_Heavy: длина от шарнира до упора, м (стрела тянется по длине до корпуса).</summary>
        const float ArmLightLen = 10, ArmHeavyLen = 14;
        /// <summary>Башни: центр x, z; сторона; высота над столом. Пара: PROTON_TOWER, ATLAS_TOWER, TITAN_TOWER, SATURN_TOWER.</summary>
        static readonly Vector4 ProtonTower = new Vector4(-17.5f, 0, 4.2f, 62), AtlasTower = new Vector4(-12, 0, 3, 36),
            TitanTower = new Vector4(0, 12.5f, 3, 38), SaturnTower = new Vector4(-24, 0, 12.2f, 120);
        /// <summary>
        /// Starbase: башня (центр x, —, сторона, высота), м — 146 м, ≈ 12 м в плане, ≈ 30 м на запад от оси стола. Палочки:
        /// высота над столом — цапфы Super Heavy (b_super_heavy: 71 − 6 = 65 м над днищем), полуразнос — радиус 4,5 + 1 м,
        /// вылет до x = +8 (дальний край корпуса). Проём стола — под связку 33 Raptor (радиус юбки 4,5). Пара: spacex_parts.py.
        /// </summary>
        static readonly Vector4 StarbaseTower = new Vector4(-30, 0, 12, 146);
        const float StarbaseColumn = 1.4f, StarbaseBay = 9, StarbaseArmHeight = 65, StarbaseArmGap = 5.5f, StarbaseArmReach = 8,
            StarbaseHole = 6;
        /// <summary>«Протон»: башня на фундаменте от грунта до низа каркаса (PAD_H − 0,1), апрон вокруг стола, м.</summary>
        const float PadH = 6;
        /// <summary>Saturn V: проём ML 13,7 м, низ ML над грунтом (ML_HOLE, ML_BASE) — бетон под ML до этой отметки.</summary>
        const float MlHole = 6.85f, MlBase = 2;

        // ---- кинематика
        /// <summary>Шарнир стрелы отнесён от грани башни, зазор до корпуса при подведённой стреле, м.</summary>
        const float HingeOffset = 0.7f, ArmGap = 0.3f;
        /// <summary>Мачты: зазор упора до корпуса, угол наклона наружу, время, с.</summary>
        const float MastGap = 0.4f, MastTilt = 80, MastTime = 2.2f;
        /// <summary>Стрелы башни: время поворота, задержка между ярусами снизу вверх, с.</summary>
        const float SwingTime = 2.4f, SwingStagger = 0.12f;

        // ---- прожекторы (§7, §9.3: ночной старт — ракета в свете прожекторов, как на фото Байконура и LC-39)
        /// <summary>Мачты на диагоналях: от оси FloodBase + FloodPerHeight·H, высота FloodMastBase + FloodMastPerHeight·H, м
        /// (H — высота ракеты). Пара: башни и мачты обслуживания ближе 30 м к оси (SaturnTower −24, апрон «Протона» по z ±14) —
        /// на диагонали дальше 45 м мачта ни во что не врезается.</summary>
        const float FloodBase = 45, FloodPerHeight = 0.5f, FloodMastBase = 12, FloodMastPerHeight = 0.45f;
        /// <summary>Освещённость корпуса одной мачтой, лк. На каждую сторону светят 2 мачты (FloodHullNits). Экспозицию держит
        /// SkyController по яркости корпуса (LitNits), так что от числа зависит не яркость кадра, а соотношение с луной и небом.</summary>
        const float FloodLux = 15;
        /// <summary>Яркость белого корпуса в свете двух мачт, нит: L = E·ρ/π, ρ ≈ 0,8. Пара: FloodLux.</summary>
        const float FloodHullNits = 2 * FloodLux * 0.8f / Mathf.PI;
        /// <summary>Экспозиция по корпусу работает, пока камера ближе FloodNear, и плавно сходит на нет к FloodFar, м.</summary>
        const float FloodNear = 1500, FloodFar = 4000;
        /// <summary>Бетонная площадка вокруг стола: края дальше мачт на столько, м.</summary>
        const float ApronMargin = 15;
        /// <summary>Панель ламп, нит — с запасом выше белого при ночной экспозиции, чтобы прожектор читался светилом.</summary>
        const float FloodLampNits = 3e3f;
        /// <summary>Металлогалогенные лампы, К.</summary>
        const float FloodTemperature = 4500;
        /// <summary>Синус высоты Солнца над столом: выше FloodSunOff прожекторы погашены, ниже FloodSunOn — горят полностью
        /// (±3°: включают в сумерках, а не в полной темноте).</summary>
        const float FloodSunOff = 0.05f, FloodSunOn = -0.05f;

        static readonly Color PitColor = new Color(0.05f, 0.05f, 0.05f);

        sealed class Mover
        {
            public Transform Pivot;
            public bool Swing;
            /// <summary>Tilt: угол от вертикали к оси в покое (Tilt0) и ход наружу; Swing: курс в покое и ход со знаком.</summary>
            public float Yaw, Tilt0, Angle, Delay, Duration;
        }

        Vessel vessel;
        CelestialBody body;
        Vector3d anchorBf;
        QuaternionD frameBf;
        readonly List<Mover> movers = new List<Mover>();
        float releaseT;
        Renderer[] renderers;
        // стек ракеты: [низ, верх, радиус] секции, м от стола (для длины стрел и высоты ярусов)
        readonly List<Vector3> stack = new List<Vector3>();
        Func<string, Mesh> pads;
        Material[] steel;
        float top;
        readonly List<Light> floods = new List<Light>();
        Material lampMat;
        Color lampColor;

        /// <summary>Яркость корпуса ракеты в свете прожекторов, нит (0 — погашены или стол далеко). По ней SkyController
        /// держит ночную экспозицию: иначе гистограмма тянется по тёмному кадру, и корпус выжигается в белое
        /// (замер 03.10.2026: средняя яркость 58, корпус сплошь 255).</summary>
        public static float LitNits { get; private set; }

        public void Init(Vessel v, Texture2D concrete, Material baseMat, Mesh truss = null, Func<string, Mesh> padMeshes = null)
        {
            vessel = v;
            body = v.Body;
            pads = padMeshes;
            var site = v.Site;
            // Базис стола: X — восток, Y — зенит, Z — север (борт на нём повёрнут креном, см. PlaceOnSurface; стол симметричен).
            var up = CelestialBody.LatLonToBodyFixed(site.Latitude, site.Longitude);
            anchorBf = up * (body.Radius + body.SurfaceHeight(up));
            var east = Vector3d.Cross(Vector3d.forward, up).normalized;
            var north = Vector3d.Cross(up, east);
            frameBf = QuaternionD.FromBasis(east.SwapYZ, up.SwapYZ, north.SwapYZ);

            top = (float)site.PadHeight;
            var kind = Classify(v.Design);
            float z = 0;
            foreach (var s in v.Design.Sections)
            {
                if (s.IsRadial) continue; // боковые блоки стоят рядом с ядром, ярусы ферм — по ядру
                stack.Add(new Vector3(z, z + (float)s.Length, (float)s.Radius));
                z += (float)s.Length;
            }

            var shader = baseMat != null ? baseMat.shader : Shader.Find("HDRP/Lit");
            var concreteMat = new Material(shader) { name = "Pad Concrete" };
            concreteMat.SetTexture("_BaseColorMap", concrete != null ? concrete : Texture2D.grayTexture);
            concreteMat.SetFloat("_Smoothness", 0.2f);
            steel = Palette(kind, shader);
            var pitMat = new Material(shader) { name = "Pad Pit" };
            pitMat.SetColor("_BaseColor", PitColor);

            var slab = new MeshBuilder(ConcreteTile);
            var pit = new MeshBuilder(ConcreteTile);
            switch (kind)
            {
                case Kind.Proton: PlanProton(slab, pit); break;
                case Kind.Redstone: PlanRedstone(slab, pit); break;
                case Kind.Atlas: PlanAtlas(slab, pit); break;
                case Kind.Titan: PlanTitan(slab, pit); break;
                case Kind.Saturn: PlanSaturn(slab, pit); break;
                case Kind.Starbase: PlanStarbase(slab, pit); break;
                default: PlanR7(slab, pit, v, truss); break;
            }
            AddPart("Pad", slab.Build(), concreteMat, transform);
            // Дно газоотвода — тёмное, чтобы проём читался ямой, а не травой под ракетой.
            AddPart("Flame Pit", pit.Build(), pitMat, transform);
            Floodlights(z, shader, concreteMat);
            renderers = GetComponentsInChildren<Renderer>();
            Pose();
        }

        // ------------------------------------------------------------------ семейства

        static Kind Classify(VesselDesign d)
        {
            if (d == null || d.Sections.Count == 0) return Kind.R7;
            switch (d.Sections[0].Model)
            {
                case SectionModel.ProtonStage1: return Kind.Proton;
                case SectionModel.Redstone:
                case SectionModel.JunoStage1: return Kind.Redstone;
                case SectionModel.AtlasBooster:
                case SectionModel.AtlasSustainer:
                case SectionModel.AtlasSustainerAgena:
                case SectionModel.AtlasSustainerCentaur: return Kind.Atlas;
                case SectionModel.TitanStage1: return Kind.Titan;
                case SectionModel.SaturnSIC: return Kind.Saturn;
                case SectionModel.SuperHeavy: return Kind.Starbase;
                default: return Kind.R7;   // Спутник, Восток, «Луна»
            }
        }

        /// <summary>Р-7 (Байконур, «Гагаринский старт»): бетонная плита с проёмом, лоток на юг, четыре фермы-«тюльпан»,
        /// две кабель-заправочные мачты на диагоналях.</summary>
        void PlanR7(MeshBuilder slab, MeshBuilder pit, Vessel v, Mesh truss)
        {
            const float S = SlabHalf, H = HoleHalf, T = TrenchHalf;
            slab.Box(new Vector3(-S, -1, H), new Vector3(S, top, S));
            slab.Box(new Vector3(-S, -1, -S), new Vector3(-T, top, -H));
            slab.Box(new Vector3(T, -1, -S), new Vector3(S, top, -H));
            slab.Box(new Vector3(H, -1, -H), new Vector3(S, top, H));
            slab.Box(new Vector3(-S, -1, -H), new Vector3(-H, top, H));
            pit.Box(new Vector3(-H, -1, -H), new Vector3(H, 0.05f, H));
            pit.Box(new Vector3(-T, -1, -S), new Vector3(T, 0.05f, -H));
            Fixed("Pad_R7", Vector3.zero);

            // Фермы: шарнир на кромке проёма, верх упирается в корпус на ArmReach над столом. Боковые блоки Р-7 стоят
            // на тех же азимутах (0°, 90°…, VesselView), и пакет висит на фермах за них — упор по их внешней стенке.
            float hull = (float)v.Design.Sections[0].Radius;
            foreach (var s in v.Design.Sections)
                if (s.IsRadial && s.RadialParent == 0)
                {
                    // Конус бокового Р-7 к верху тоньше: упор по его стенке на высоте верха фермы (низ пакета — y = 0).
                    double y = top + ArmReach, outer = s.RadialOffset + s.Radius;
                    if (s.Model == SectionModel.R7Booster)
                        outer = s.RadialOffset + VesselPresets.R7BoosterLean(y) + VesselPresets.R7BoosterRadiusAt(y);
                    hull = Mathf.Max(hull, (float)outer);
                }
            float tilt0 = Mathf.Atan2(H - hull, ArmReach) * Mathf.Rad2Deg;
            float len = Mathf.Sqrt(ArmReach * ArmReach + (H - hull) * (H - hull));
            var steelMat = steel[0];
            var arm = new MeshBuilder(ConcreteTile);
            // Решётчатая ферма из Blender (Models/Pad_Truss_Arm, сечение = ArmWidth, длина TrussModelLength) или брус.
            if (truss == null)
                arm.Box(new Vector3(-ArmWidth / 2, 0, -ArmWidth / 2), new Vector3(ArmWidth / 2, len, ArmWidth / 2));
            // Противовес под шарниром — им ферма и откидывается, когда ракета уходит.
            arm.Box(new Vector3(-ArmWidth, -2.5f, -ArmWidth), new Vector3(ArmWidth, 0, ArmWidth));
            var armMesh = arm.Build();
            for (int k = 0; k < 4; k++)
            {
                var pivot = new GameObject($"Arm {k}").transform;
                pivot.SetParent(transform, false);
                // Ферма смотрит на ось ракеты: локальный −Z шарнира — к центру.
                float yaw = 90 * k;
                pivot.localPosition = Quaternion.Euler(0, yaw, 0) * new Vector3(0, top, H);
                pivot.localRotation = Quaternion.Euler(0, yaw, 0);
                AddPart(truss == null ? "Truss" : "Counterweight", armMesh, steelMat, pivot);
                if (truss != null)
                    AddPart("Truss", truss, steelMat, pivot).transform.localScale = new Vector3(1, len / TrussModelLength, 1);
                movers.Add(new Mover { Pivot = pivot, Yaw = yaw, Tilt0 = tilt0, Angle = ReleaseAngle, Duration = ReleaseTime });
            }
            // Кабель-заправочные мачты (на Р-7 их две) — на диагоналях, чтобы не мешать фермам.
            Mast(45, 1, top, true);
            Mast(225, 1, top, true);
        }

        /// <summary>«Протон» (пл. 81/200): стол-кольцо на шести опорах над газоотводом, высокая кабельная мачта
        /// с поворотными стрелами, обслуживающая ферма откачена на рельсы в сторону.</summary>
        void PlanProton(MeshBuilder slab, MeshBuilder pit)
        {
            // Апрон на грунте (стол стоит на опорах от земли, палубы нет); тянется до рельсов фермы на x = 44.
            slab.Box(new Vector3(-26, -1, -14), new Vector3(54, 0.04f, 14));
            // Фундамент мачты: каркас башни начинается на отметке стола − 0,1 (пара: b_pad_proton), под ним — бетон.
            var t = ProtonTower;
            slab.Box(new Vector3(t.x - t.z / 2 - 0.8f, -1, t.y - t.z / 2 - 0.8f), new Vector3(t.x + t.z / 2 + 0.8f, PadH - 0.1f, t.y + t.z / 2 + 0.8f));
            pit.Box(new Vector3(-6.3f, -1, -6.3f), new Vector3(6.3f, 0.07f, 6.3f));
            Fixed("Pad_Proton", Vector3.zero);
            SwingArms("Pad_Arm_Light", ArmLightLen, t, new[] { 9f, 20f, 31f, 42f, 53f });
        }

        /// <summary>«Редстоун»/«Юнона»: малый стальной стол-кольцо на четырёх ножках над отражателем, одна невысокая мачта.</summary>
        void PlanRedstone(MeshBuilder slab, MeshBuilder pit)
        {
            slab.Box(new Vector3(-14, -1, -14), new Vector3(14, 0.04f, 14));
            pit.Box(new Vector3(-3.2f, -1, -3.2f), new Vector3(3.2f, 0.07f, 3.2f));
            Fixed("Pad_Redstone", Vector3.zero);
            // Мачта на грунте, ×0,8: центр стрелы на 0,8·19,8 = 15,8 м от грунта = 9,8 м над столом — у середины корпуса.
            Mast(-45, 0.8f, 0.04f, false);
        }

        /// <summary>«Атлас» (LC-14/12/36): бетонный пьедестал на отметке стола с захватами фланца и башней со стрелами.</summary>
        void PlanAtlas(MeshBuilder slab, MeshBuilder pit)
        {
            Deck(slab, pit, 14, 3.2f);
            Fixed("Pad_Atlas", Vector3.zero);
            SwingArms("Pad_Arm_Light", ArmLightLen, AtlasTower, new[] { 6f, 12f, 18f, 24f });
        }

        /// <summary>«Титан II» (LC-19): пьедестал с дефлектором, захваты юбки, откинутый эректор, башня со стрелами.</summary>
        void PlanTitan(MeshBuilder slab, MeshBuilder pit)
        {
            Deck(slab, pit, 20, 3.3f);
            Fixed("Pad_Titan", Vector3.zero);
            SwingArms("Pad_Arm_Light", ArmLightLen, TitanTower, new[] { 7f, 15f, 23f, 30f });
        }

        /// <summary>Saturn V (LC-39A): подвижная платформа ML над газоотводным лотком Север–Юг и башня LUT с девятью стрелами.</summary>
        void PlanSaturn(MeshBuilder slab, MeshBuilder pit)
        {
            // Бетон под ML до его низа (MlBase); посреди — лоток на всю ширину проёма, струя уходит на север и юг.
            slab.Box(new Vector3(-40, -1, -26), new Vector3(-MlHole, MlBase, 26));
            slab.Box(new Vector3(MlHole, -1, -26), new Vector3(24, MlBase, 26));
            pit.Box(new Vector3(-MlHole, -1, -26), new Vector3(MlHole, 0.05f, 26));
            Fixed("Pad_Saturn_ML", Vector3.zero);
            Fixed("Pad_Saturn_LUT", Vector3.zero);
            SwingArms("Pad_Arm_Heavy", ArmHeavyLen, SaturnTower, new[] { 12f, 26f, 40f, 52f, 64f, 76f, 88f, 99f, 109f });
        }

        /// <summary>
        /// Starbase (Бока-Чика, OLP-A): бетон с проёмом под 33 Raptor и башня ловли «Mechazilla» на запад от стола со
        /// «палочками» на высоте цапф Super Heavy. Палочки стоят сведёнными по обе стороны оси: ускоритель садится в круг
        /// RecoveryDef.DeckRadius у центра стола (SpaceXRockets.StarbaseCatch) — и оказывается между ними, как при ловле.
        /// </summary>
        void PlanStarbase(MeshBuilder slab, MeshBuilder pit)
        {
            Deck(slab, pit, 32, StarbaseHole);
            var tower = new MeshBuilder(ConcreteTile);
            float x0 = StarbaseTower.x, half = StarbaseTower.z * 0.5f, h = StarbaseTower.w, c = StarbaseColumn * 0.5f;
            // Решётчатая башня: четыре стойки и пояса через StarbaseBay — читается фермой и не стоит лишних мешей.
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    tower.Box(new Vector3(x0 + sx * half - c, 0, sz * half - c), new Vector3(x0 + sx * half + c, h, sz * half + c));
            for (float y = StarbaseBay; y < h; y += StarbaseBay)
            {
                tower.Box(new Vector3(x0 - half, y - 0.4f, -half - c), new Vector3(x0 + half, y + 0.4f, -half + c));
                tower.Box(new Vector3(x0 - half, y - 0.4f, half - c), new Vector3(x0 + half, y + 0.4f, half + c));
                tower.Box(new Vector3(x0 - half - c, y - 0.4f, -half), new Vector3(x0 - half + c, y + 0.4f, half));
                tower.Box(new Vector3(x0 + half - c, y - 0.4f, -half), new Vector3(x0 + half + c, y + 0.4f, half));
            }
            // Каретка на грани к столу и две «палочки» до дальнего края корпуса, щель между ними — Ø ускорителя с зазором.
            float yArm = top + StarbaseArmHeight, inner = x0 + half;
            tower.Box(new Vector3(inner, yArm - 4, -StarbaseArmGap - 2), new Vector3(inner + 2.5f, yArm + 4, StarbaseArmGap + 2));
            for (int sz = -1; sz <= 1; sz += 2)
                tower.Box(new Vector3(inner, yArm - 1.2f, sz * StarbaseArmGap - 0.8f), new Vector3(StarbaseArmReach, yArm + 1.2f, sz * StarbaseArmGap + 0.8f));
            // Кран и молниеотвод на макушке.
            tower.Box(new Vector3(x0 - 1, h, -1), new Vector3(x0 + 1, h + 9, 1));
            AddPart("Mechazilla", tower.Build(), steel[0], transform);
        }

        /// <summary>Четыре прожекторные мачты на диагоналях (по 45°): ствол, голова с панелью ламп и Spot-светом,
        /// нацеленным в середину ракеты; конус охватывает корпус от стола до верха. h — высота ракеты, м.</summary>
        void Floodlights(float h, Shader shader, Material concreteMat)
        {
            lampColor = Mathf.CorrelatedColorTemperatureToRGB(FloodTemperature);
            lampMat = new Material(shader) { name = "Pad Lamp" };
            lampMat.SetColor("_BaseColor", new Color(0.9f, 0.9f, 0.85f));
            float dist = FloodBase + FloodPerHeight * h, mastH = FloodMastBase + FloodMastPerHeight * h;
            // Площадка до мачт и чуть дальше: стол не торчит островком в траве, мачты стоят на бетоне. Верх 0,02 — ниже
            // апронов «Протона» и «Редстоуна» (0,04), чтобы не мерцали.
            float apron = (dist + ApronMargin) / Mathf.Sqrt(2) + ApronMargin;
            var slab = new MeshBuilder(ConcreteTile);
            slab.Box(new Vector3(-apron, -1, -apron), new Vector3(apron, 0.02f, apron));
            AddPart("Apron", slab.Build(), concreteMat, transform);
            var aim = new Vector3(0, top + h * 0.5f, 0);
            var column = new MeshBuilder(ConcreteTile);
            column.Box(new Vector3(-0.5f, -3, -0.5f), new Vector3(0.5f, mastH, 0.5f));
            var columnMesh = column.Build();
            var frame = new MeshBuilder(ConcreteTile);
            frame.Box(new Vector3(-2.2f, -1.4f, -0.6f), new Vector3(2.2f, 1.4f, 0));
            var frameMesh = frame.Build();
            var panel = new MeshBuilder(ConcreteTile);
            panel.Box(new Vector3(-2, -1.2f, 0), new Vector3(2, 1.2f, 0.08f));
            var panelMesh = panel.Build();
            for (int k = 0; k < 4; k++)
            {
                float a = (45 + 90 * k) * Mathf.Deg2Rad;
                var basePos = new Vector3(dist * Mathf.Sin(a), 0, dist * Mathf.Cos(a));
                var mast = new GameObject($"Floodlight {k}").transform;
                mast.SetParent(transform, false);
                mast.localPosition = basePos;
                AddPart("Column", columnMesh, steel[0], mast);
                var head = new GameObject("Head").transform;
                head.SetParent(mast, false);
                head.localPosition = new Vector3(0, mastH, 0);
                var headPos = basePos + head.localPosition;
                var toAim = aim - headPos;
                head.localRotation = Quaternion.LookRotation(toAim);
                AddPart("Frame", frameMesh, steel[1], head);
                AddPart("Lamps", panelMesh, lampMat, head);

                // Конус: угол между лучами на низ и верх ракеты плюс запас — свет не обрезается по кромке корпуса.
                float cone = Vector3.Angle(new Vector3(0, top, 0) - headPos, new Vector3(0, top + h, 0) - headPos) * 1.15f + 8;
                var lgo = new GameObject("Spot");
                lgo.transform.SetParent(head, false);
                lgo.transform.localPosition = new Vector3(0, 0, 0.3f);
                var l = lgo.AddComponent<Light>();
                l.type = LightType.Spot;
                lgo.AddComponent<HDAdditionalLightData>();
                l.lightUnit = UnityEngine.Rendering.LightUnit.Candela;
                l.useColorTemperature = true;
                l.colorTemperature = FloodTemperature;
                l.spotAngle = Mathf.Min(cone, 120);
                l.innerSpotAngle = l.spotAngle * 0.7f;
                // Сила света под заданную освещённость в точке прицела: E = I / d². Гасим цветом (Pose), не силой.
                l.intensity = FloodLux * toAim.sqrMagnitude;
                l.range = toAim.magnitude * 3;
                l.shadows = LightShadows.Soft;
                l.enabled = false;
                floods.Add(l);
            }
        }

        /// <summary>Доля «ночи» над столом 0…1 по высоте Солнца над горизонтом стола — не у борта: ракета уже на свету,
        /// а стол ещё в темноте.</summary>
        float FloodNight(QuaternionD o)
        {
            var sun = body;
            while (sun.Parent != null) sun = sun.Parent;
            var up = o * anchorBf;
            double sinEl = Vector3d.Dot(up.normalized, (sun.Position - (body.Position + up)).normalized);
            return Mathf.Clamp01((float)((FloodSunOff - sinEl) / (FloodSunOff - FloodSunOn)));
        }

        // ------------------------------------------------------------------ сборка

        /// <summary>Пьедестал на отметке стола с квадратным проёмом hole по центру (4 плиты вокруг) и ямой под ним.</summary>
        void Deck(MeshBuilder slab, MeshBuilder pit, float half, float hole)
        {
            slab.Box(new Vector3(-half, -1, hole), new Vector3(half, top, half));
            slab.Box(new Vector3(-half, -1, -half), new Vector3(half, top, -hole));
            slab.Box(new Vector3(hole, -1, -hole), new Vector3(half, top, hole));
            slab.Box(new Vector3(-half, -1, -hole), new Vector3(-hole, top, hole));
            pit.Box(new Vector3(-hole, -1, -hole), new Vector3(hole, 0.05f, hole));
        }

        /// <summary>Радиус корпуса на высоте h над столом, м; −1 — выше верха ракеты (ярус не нужен).</summary>
        float RadiusAt(float h)
        {
            foreach (var s in stack)
                if (h >= s.x && h <= s.y) return s.z;
            return -1;
        }

        /// <summary>Деталь из FBX Blender: поворот Y180 переводит оси Blender в базис стола.
        /// Измерено на импорте (bounds Pad_Atlas): Unity = (−x, z, −y) от Blender, то есть восток Blender
        /// становится западом; разворот вокруг вертикали на 180° возвращает x — востоком, y — севером.</summary>
        Transform Place(string meshName, Vector3 localPos, float yaw, Vector3 scale)
        {
            var mesh = pads?.Invoke(meshName);
            if (mesh == null) return null;
            var pivot = new GameObject(meshName).transform;
            pivot.SetParent(transform, false);
            pivot.localPosition = localPos;
            pivot.localRotation = Quaternion.Euler(0, yaw, 0);
            var model = AddPart("Model", mesh, null, pivot).transform;
            model.localRotation = Quaternion.Euler(0, 180, 0);
            model.localScale = scale;
            return pivot;
        }

        void Fixed(string meshName, Vector3 pos) => Place(meshName, pos, 0, Vector3.one);

        /// <summary>Наклонная кабель-заправочная мачта (Pad_Mast): на круге вокруг оси под углом pos°,
        /// на таком расстоянии, чтобы упор вылета касался корпуса на высоте стрелы; при отрыве падает наружу.
        /// onDeck — основание на отметке стола (иначе на грунте, baseY).</summary>
        void Mast(float posDeg, float scale, float baseY, bool onDeck)
        {
            // Высота центра стрелы-вылета над столом: основание на baseY (над грунтом), стол на top.
            float hAboveDeck = (MastH - MastArmFromTop) * scale + baseY - top;
            float r = RadiusAt(hAboveDeck);
            if (r <= 0) return;
            float d = r + MastGap + MastReach * scale;
            float a = posDeg * Mathf.Deg2Rad;
            var p = Place("Pad_Mast", new Vector3(d * Mathf.Sin(a), baseY, d * Mathf.Cos(a)), posDeg, Vector3.one * scale);
            if (p == null) return;
            movers.Add(new Mover { Pivot = p, Yaw = posDeg, Tilt0 = 0, Angle = MastTilt, Duration = MastTime });
        }

        /// <summary>Поворотные стрелы башни tower (x, z, сторона, высота): шарнир у угла грани, обращённой к ракете,
        /// в рабочем положении стрела смотрит на ось и тянется до корпуса (длина по радиусу секции на её высоте),
        /// при отрыве уходит на 90° в сторону и паркуется вдоль грани, за пределы башни.
        /// Ярусы heights — м над столом; выше верха ракеты стрелу не ставим. Чередуем углы грани, чтобы шарниры не слипались.</summary>
        void SwingArms(string meshName, float modelLen, Vector4 tower, float[] heights)
        {
            var c = new Vector2(tower.x, tower.y);
            float w = tower.z;
            bool alongX = Mathf.Abs(c.x) >= Mathf.Abs(c.y);
            float sa = -Mathf.Sign(alongX ? c.x : c.y);   // грань башни, обращённая к оси
            int n = 0;
            foreach (float h in heights)
            {
                float r = RadiusAt(h);
                if (r <= 0) continue;
                float side = n % 2 == 0 ? 1 : -1;
                var hinge = alongX ? new Vector2(c.x + sa * (w / 2 + HingeOffset), c.y + side * w / 2)
                                   : new Vector2(c.x + side * w / 2, c.y + sa * (w / 2 + HingeOffset));
                float dist = hinge.magnitude;
                float len = dist - r - ArmGap;
                if (len < 2) continue;
                var dir = -hinge / dist;
                var parked = alongX ? new Vector2(0, side) : new Vector2(side, 0);
                float yawE = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
                float delta = Mathf.DeltaAngle(yawE, Mathf.Atan2(parked.x, parked.y) * Mathf.Rad2Deg);
                var p = Place(meshName, new Vector3(hinge.x, top + h, hinge.y), yawE, new Vector3(1, 1, len / modelLen));
                if (p == null) return;
                movers.Add(new Mover { Pivot = p, Swing = true, Yaw = yawE, Angle = delta, Delay = SwingStagger * n, Duration = SwingTime });
                n++;
            }
        }

        /// <summary>Четыре слота FBX: 0 сталь комплекса, 1 тёмное (газоотвод, захваты, оборудование), 2 красное, 3 белое (площадки, стрелы).</summary>
        static Material[] Palette(Kind kind, Shader shader)
        {
            var steelC = new Color(0.42f, 0.40f, 0.36f);
            var red = new Color(0.55f, 0.13f, 0.08f);
            if (kind == Kind.Proton) steelC = new Color(0.50f, 0.53f, 0.55f);
            if (kind == Kind.Saturn) { steelC = new Color(0.58f, 0.59f, 0.60f); red = new Color(0.64f, 0.17f, 0.07f); }
            var cols = new[] { steelC, new Color(0.08f, 0.08f, 0.08f), red, new Color(0.78f, 0.78f, 0.75f) };
            var names = new[] { "Steel", "Dark", "Red", "White" };
            var mats = new Material[4];
            for (int i = 0; i < 4; i++)
            {
                mats[i] = new Material(shader) { name = "Pad " + names[i] };
                mats[i].SetColor("_BaseColor", cols[i]);
                mats[i].SetFloat("_Metallic", i == 0 ? 0.5f : 0.1f);
                mats[i].SetFloat("_Smoothness", i == 0 ? 0.35f : 0.3f);
            }
            return mats;
        }

        /// <summary>Часть из меша: mat != null — один материал на все субмеши; null — палитра комплекса по слотам FBX.</summary>
        GameObject AddPart(string name, Mesh mesh, Material mat, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            // У FBX слоты материалов — отдельные субмеши: с одним материалом рисуется только первый.
            var mats = new Material[Mathf.Max(1, mesh.subMeshCount)];
            for (int i = 0; i < mats.Length; i++) mats[i] = mat != null ? mat : steel[Mathf.Min(i, steel.Length - 1)];
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            return go;
        }

        void OnDestroy() => LitNits = 0;

        void LateUpdate()
        {
            if (vessel == null) return;
            // Детали отходят по факту отрыва — ядро переводит борт из Landed в Flying (StepLanded).
            if (releaseT < 60 && (vessel.Situation != Situation.Landed || !vessel.Alive))
                releaseT += Time.deltaTime;
            Pose();
        }

        void Pose()
        {
            var o = body.Orientation;
            var pos = FloatingOrigin.ToUnity(body.Position + o * anchorBf);
            bool show = !MapView.IsOpen && FlightView.Near(pos, DrawDistance);
            foreach (var r in renderers) r.enabled = show;
            float night = show ? FloodNight(o) : 0;
            LitNits = FloodHullNits * night * Mathf.Clamp01((FloodFar - pos.magnitude) / (FloodFar - FloodNear));
            foreach (var l in floods)
            {
                l.enabled = night > 0.01f;
                l.color = Color.white * night;
            }
            if (lampMat != null) lampMat.SetColor("_EmissiveColor", lampColor * (FloodLampNits * night));
            if (!show) return;
            transform.SetPositionAndRotation(pos, FloatingOrigin.ToQuaternion(o.SwapYZ * frameBf));
            foreach (var m in movers)
            {
                // Плавный старт и мягкая остановка — деталь тяжёлая, рывком не ходит.
                float s = Mathf.SmoothStep(0, 1, Mathf.Clamp01((releaseT - m.Delay) / m.Duration));
                m.Pivot.localRotation = m.Swing
                    ? Quaternion.Euler(0, m.Yaw + m.Angle * s, 0)
                    : Quaternion.Euler(-(m.Tilt0 - m.Angle * s), m.Yaw, 0);
            }
        }

        /// <summary>Коробки с UV в метрах / tile по проекции на грань — бетон не тянется по длинной плите.</summary>
        sealed class MeshBuilder
        {
            readonly float tile;
            readonly List<Vector3> v = new List<Vector3>();
            readonly List<Vector3> n = new List<Vector3>();
            readonly List<Vector2> uv = new List<Vector2>();
            readonly List<int> t = new List<int>();

            public MeshBuilder(float tile) { this.tile = tile; }

            public void Box(Vector3 min, Vector3 max)
            {
                var c = (min + max) * 0.5f;
                var e = (max - min) * 0.5f;
                Face(c, e, Vector3.right); Face(c, e, Vector3.left);
                Face(c, e, Vector3.up); Face(c, e, Vector3.down);
                Face(c, e, Vector3.forward); Face(c, e, Vector3.back);
            }

            void Face(Vector3 c, Vector3 e, Vector3 nrm)
            {
                // Две оси грани, Cross(a, b) — наружу: в Unity лицевая у треугольника (p0, p1, p2) та,
                // куда смотрит Cross(p1 − p0, p2 − p0).
                Vector3 a = nrm.y != 0 ? Vector3.right : Vector3.up;
                Vector3 b = Vector3.Cross(nrm, a);
                if (Vector3.Dot(Vector3.Cross(a, b), nrm) < 0) b = -b;
                var center = c + Vector3.Scale(nrm, e);
                var ea = Vector3.Scale(a, e);
                var eb = Vector3.Scale(b, e);
                int i0 = v.Count;
                var corners = new[] { center - ea - eb, center + ea - eb, center + ea + eb, center - ea + eb };
                foreach (var p in corners)
                {
                    v.Add(p);
                    n.Add(nrm);
                    // Проекция на плоскость грани: у верха — XZ, у боков — горизонталь × высота.
                    uv.Add(nrm.y != 0 ? new Vector2(p.x, p.z) / tile
                         : new Vector2(nrm.x != 0 ? p.z : p.x, p.y) / tile);
                }
                t.Add(i0); t.Add(i0 + 1); t.Add(i0 + 2);
                t.Add(i0); t.Add(i0 + 2); t.Add(i0 + 3);
            }

            public Mesh Build()
            {
                var m = new Mesh { name = "Pad" };
                m.SetVertices(v);
                m.SetNormals(n);
                m.SetUVs(0, uv);
                m.SetTriangles(t, 0);
                m.RecalculateTangents();
                m.RecalculateBounds();
                return m;
            }
        }
    }
}
