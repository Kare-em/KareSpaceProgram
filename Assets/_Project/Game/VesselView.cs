using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.Game
{
    /// <summary>
    /// Вид борта (GDD §5, §9.5): процедурный меш из Design.Sections снизу вверх, начало ноды — центр масс,
    /// ориентация — Vessel.Attitude (уже в осях Unity, +Y = нос), факел под работающими двигателями.
    /// Отделённые ступени — отдельные Vessel со своим видом (их заводит GameBootstrap).
    /// </summary>
    public sealed class VesselView : MonoBehaviour
    {
        /// <summary>Дальше этого от активного борта обломки не рисуем — на таком расстоянии они меньше пикселя.</summary>
        const float DrawDistance = 50000;
        /// <summary>Длина факела в калибрах сопла у земли и рост в вакууме (§9.5: расширение струи).</summary>
        const float PlumeLengthSL = 12, PlumeVacuumGrowth = 3;
        /// <summary>Связка сопел: кольцо на этой доле радиуса днища, сопло — доля шага кольца (зазор между
        /// раструбами). Пара: PlumeClusterFill — общий факел накрывает кольцо целиком (у земли струи сливаются).</summary>
        const float NozzleRing = 0.62f, NozzleGap = 0.85f, PlumeClusterFill = 0.85f;
        /// <summary>Свечение длиннее ядра во столько раз; ядро в вакууме почти не раздувается.</summary>
        const float GlowLength = 2.2f, CoreSpread = 0.25f;
        /// <summary>Яркость ядра и свечения у среза, нит. Пара: дневная экспозиция EV 14 (§9.3) — серое 18 %
        /// ≈ 2000 нит. Физичные 2·10⁵ не годятся: аддитив складывает 4 стенки, bloom размазывает перебор
        /// на весь экран — кадр белый (замер 01.10.2026). 3·10³ при EV 14 — белое ядро с ореолом.</summary>
        const float CoreNits = 3e3f, GlowNits = 3e2f;
        /// <summary>В вакууме керосиновый факел тусклее: доля яркости при p = 0.</summary>
        const float VacuumBrightness = 0.3f;
        /// <summary>Сила света факела на дросселе 1, кд: на 30 м ≈ 2000 лк — подсветка стола и борта ночью
        /// заметна, днём (Солнце 127 000 лк) почти нет. Пара: PlumeLightRange.</summary>
        const float PlumeCandela = 2e6f, PlumeLightRange = 600;
        const double SeaLevelPressure = 101325;
        /// <summary>Купол (GDD §6.4): стропы — столько радиусов полностью раскрытого купола; угол — полураствор
        /// сферического сегмента. Пара: площадь Section.ParachuteArea и рифление FlightPhysics.ChuteFraction —
        /// радиус купола считается из той же площади, что тормозит в ядре, поэтому рифлёный купол на экране узкий.</summary>
        const float ChuteRiser = 1.3f, ChuteDomeAngle = 75, ChuteLineWidth = 0.06f;
        /// <summary>Число полотнищ (клиньев) купола: полосы оранжевый/белый, как у «Востока».</summary>
        const int ChuteGores = 16;
        /// <summary>Плазма входа (GDD §4.6): видна с PlasmaStart, полная яркость с PlasmaFull, Вт/м². Пара: «Восток»
        /// на 65 км — 0,7 МВт/м², пик ≈ 1,5 МВт/м² на 46 км (прогон 01.10.2026); MaxHeatFlux СА 3 МВт/м².</summary>
        const float PlasmaStart = 1e5f, PlasmaFull = 1.2e6f;
        /// <summary>Яркость ударного слоя у лба и хвоста, нит. Пара: CoreNits — плазма не ярче ядра факела,
        /// иначе днём при EV 14 выбеливает кадр так же, как физичный факел.</summary>
        /// Ореол 2,5·10³ нит на 2,2 r накрывал шар целиком — вместо аппарата кремовый диск (01.10.2026).
        const float PlasmaNits = 1.2e3f, PlasmaWakeNits = 4e2f;
        /// <summary>Размеры в радиусах борта: ореол ударного слоя (полуширина спрайта), длина следа, дальность
        /// подсветки; вынос центра ореола вперёд от лба. Пара: PlasmaHalo − PlasmaHaloAhead ≥ 1 — ореол выходит
        /// за силуэт шара (сзади виден кольцом), но центр впереди, и сбоку шар виден за ним.
        /// Было 1,8 / 0,6 (+0,25 отступ ударной волны): центр свечения на 0,85 r перед лбом, и шар плазмы
        /// летел «с упреждением» впереди аппарата. Отход ударной волны у сферы ≈ 0,1–0,2 r — отсюда 0,3 и 0,1.</summary>
        const float PlasmaHalo = 1.5f, PlasmaHaloAhead = 0.3f, PlasmaWakeLength = 9, PlasmaLightRange = 15;
        /// <summary>Отход ударной волны от лба, радиусов борта. Пара: PlasmaHaloAhead.</summary>
        const float ShockStandoff = 0.1f;
        static readonly Color PlasmaTint = new Color(1f, 0.5f, 0.32f);
        /// <summary>Накал обшивки на входе, нит при полном нагреве: абляционное покрытие светится тёмно-красным
        /// (≈1500 K), заодно теневая сторона не чёрная дырой. Пара: PlasmaNits — накал вчетверо тусклее ореола,
        /// иначе силуэт аппарата тонет в плазме.
        /// Эмиссия HDRP/Lit на корпусе экспозицией почти не гасится: при EV 14,3 замер 01.10.2026 — 20 нит → R≈20
        /// (тёмно-красный), 100 нит → R≈172, 527 нит → белый «кремовый диск». Поэтому десятки нит, а не сотни.</summary>
        const float HeatGlowNits = 60f;
        static readonly Color HeatGlowTint = new Color(1f, 0.3f, 0.1f);
        /// <summary>След не подходит к камере ближе, радиусов борта: лента, прошедшая сквозь ближнюю плоскость,
        /// режется прямой кромкой на весь кадр (вид сзади, 01.10.2026). Конец ленты в текстуре гаснет в ноль.</summary>
        const float WakeCameraClearance = 2;
        /// <summary>Размеры FBX-деталей из Tools/blender, м: диаметр СА, ширина и высота блока РД-107 (4 камеры),
        /// вынос стопы опоры наружу и вниз от шарнира. Пара: границы мешей в Models/*.fbx — меняешь модель, сверяй.</summary>
        const float CapsuleModelDiameter = 2.3f, EngineModelWidth = 1.96f, EngineModelHeight = 1.6f;
        const float LegModelReach = 1.09f, LegModelDrop = 1.37f;
        /// <summary>Габариты деталей реальных аппаратов, м: шар ПС-1; приборный отсек «Востока» (Ø, высота до
        /// среза ТДУ); станция Е-6; РД-0110 (Ø, высота); ферма горячего разделения (наружный радиус, высота);
        /// створка обтекателя (радиус, высота); корпус хвостового отсека Г-1 под стабилизаторами (радиус).
        /// Пара: границы мешей в Models/*.fbx — меняешь модель, сверяй (mesh.bounds).</summary>
        const float SputnikModelDiameter = 0.58f, ServiceModelDiameter = 2.44f, ServiceModelHeight = 2.25f;
        const float Luna9ModelDiameter = 1.5f, Luna9ModelHeight = 2.7f;
        const float UpperEngineModelDiameter = 2.2f, UpperEngineModelHeight = 1.6f;
        const float InterstageModelRadius = 1.33f, InterstageModelHeight = 1.2f;
        const float FairingModelRadius = 2.6f, FairingModelHeight = 13f, FinsModelBodyRadius = 0.5f;
        /// <summary>Радиус среза ТДУ «Востока» в долях радиуса отсека — по модели (сопло Ø≈0,5 м на Ø2,44).</summary>
        const float ServiceNozzleShare = 0.2f;

        public Vessel Vessel { get; private set; }

        sealed class Part
        {
            public int Index;
            public Transform Tr;
            public Transform Plume, Glow;
            public Renderer CoreR, GlowR;
            public Light PlumeLight;
            public float PlumeRadius, Throttle;
            public Transform Chute, Canopy;
            public LineRenderer Lines;
            // Корпус может состоять из нескольких рендереров (две створки), у каждого — палитра по слотам.
            public readonly List<Renderer> BodyR = new List<Renderer>();
            public readonly List<Color[]> BodyColors = new List<Color[]>();

            public void AddBody(Renderer r, Color[] palette) { BodyR.Add(r); BodyColors.Add(palette); }
        }

        readonly List<Part> parts = new List<Part>();
        Material bodyMat, plumeMat;
        Transform plasma, plasmaHalo;
        Renderer plasmaHaloR;
        LineRenderer plasmaWake;
        Light plasmaLight;
        bool heatGlowOn;
        string builtSignature;
        double[] baseHeight;
        MaterialPropertyBlock mpb;

        static readonly Color StageColor = new Color(0.82f, 0.83f, 0.80f);
        static readonly Color PayloadColor = new Color(0.65f, 0.62f, 0.55f);
        static readonly Color CapsuleColor = new Color(0.30f, 0.28f, 0.26f);
        static readonly Color FairingColor = new Color(0.92f, 0.92f, 0.90f);
        static readonly Color NozzleColor = new Color(0.20f, 0.18f, 0.17f);
        // Слоты FBX «Metal» (рамы, баки, антенны) и «Polished» (полированный шар ПС-1, экраны Е-6).
        static readonly Color MetalColor = new Color(0.55f, 0.55f, 0.53f);
        static readonly Color PolishedColor = new Color(0.85f, 0.85f, 0.83f);

        public void Init(Vessel v, Material mat, Material plume)
        {
            Vessel = v;
            bodyMat = mat != null ? mat : new Material(Shader.Find("HDRP/Lit"));
            // Копия в рантайме: HDMaterial.ValidateMaterial в редакторе снимает _EMISSIVE_COLOR_MAP с ассета,
            // если карта задана только в MPB, — факел терял градиент и был серо-белым конусом (01.10.2026).
            plumeMat = new Material(plume != null ? plume : bodyMat) { name = "Plume (runtime)" };
            plumeMat.SetTexture("_EmissiveColorMap", PlumeGradient());
            plumeMat.EnableKeyword("_EMISSIVE_COLOR_MAP");
            baseHeight = new double[v.Design.Sections.Count];
            mpb = new MaterialPropertyBlock();
            Rebuild();
            LateUpdate();
        }

        string Signature()
        {
            var a = Vessel.Attached;
            var c = new char[a.Length];
            for (int i = 0; i < a.Length; i++) c[i] = a[i] ? (Vessel.IsEnclosed(i) ? 'e' : '1') : '0';
            return new string(c);
        }

        void Rebuild()
        {
            foreach (Transform ch in transform) Destroy(ch.gameObject);
            parts.Clear();
            builtSignature = Signature();
            var secs = Vessel.Design.Sections;
            var boot = GameBootstrap.Instance;
            Mesh capsuleFbx = boot != null ? boot.CapsuleMesh : null, engineFbx = boot != null ? boot.EngineMesh : null;
            Mesh legFbx = boot != null ? boot.LegMesh : null;
            Mesh sputnikFbx = boot != null ? boot.SputnikMesh : null, serviceFbx = boot != null ? boot.VostokServiceMesh : null;
            Mesh luna9Fbx = boot != null ? boot.Luna9Mesh : null, upperEngineFbx = boot != null ? boot.UpperEngineMesh : null;
            Mesh interstageFbx = boot != null ? boot.InterstageMesh : null, fairingFbx = boot != null ? boot.FairingHalfMesh : null;
            Mesh finsFbx = boot != null ? boot.FinsMesh : null;
            // Высота фермы под секцией j (0 — фермы нет). Условие — то же, что у постановки фермы ниже: одиночный
            // двигатель без своего сопла в модели, над другой секцией.
            float TrussHeight(int j)
            {
                if (interstageFbx == null || j <= 0 || j >= secs.Count || !Vessel.Attached[j] || Vessel.IsEnclosed(j)) return 0;
                var sj = secs[j];
                if (!sj.HasEngine || sj.EngineCount != 1) return 0;
                bool own = (sj.Model == SectionModel.VostokService && serviceFbx != null) || (sj.Model == SectionModel.Luna9 && luna9Fbx != null);
                return own ? 0 : Mathf.Min((float)sj.Radius * 0.45f, 1.2f) * 1.4f;
            }
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Vessel.Attached[i] || Vessel.IsEnclosed(i)) continue;
                var s = secs[i];
                var go = new GameObject(s.Name);
                go.transform.SetParent(transform, false);
                float len = (float)s.Length, r = (float)s.Radius;
                Mesh mesh; Color col;
                switch (s.Kind)
                {
                    case SectionKind.Capsule:
                        mesh = s.Sphere ? ProcMesh.Sphere(r, len * 0.5f, 32, 16) : ProcMesh.Frustum(r, r * 0.35f, len, 24, true);
                        col = CapsuleColor; break;
                    case SectionKind.Fairing:
                        mesh = fairingFbx != null ? null : ProcMesh.Fairing(r, len, 24, Vessel.FairingHalf); col = FairingColor; break;
                    case SectionKind.Payload: mesh = ProcMesh.Frustum(r, r, len, 24, true); col = PayloadColor; break;
                    default:
                        // Ферма верхней ступени стоит в верхней части этой: корпус короче на её высоту, иначе ферма
                        // целиком внутри обечайки и не видна. Длина в физике та же.
                        mesh = ProcMesh.Frustum(r, r, Mathf.Max(len - TrussHeight(i + 1), len * 0.5f), 24, true);
                        col = StageColor; break;
                }
                var part = new Part { Index = i, Tr = go.transform };
                // Своя модель аппарата целиком заменяет процедурный корпус; у отсеков с двигателем в ней и сопло.
                Mesh model = s.Model == SectionModel.Sputnik ? sputnikFbx : s.Model == SectionModel.VostokService ? serviceFbx
                           : s.Model == SectionModel.Luna9 ? luna9Fbx : null;
                if (model != null)
                {
                    var m = AddChild(go, "Model");
                    Color[] palette;
                    switch (s.Model)
                    {
                        case SectionModel.Sputnik:
                            // Начало — центр шара: ставим в середину секции. Антенны уходят вниз; под обтекателем их
                            // не видно, после сброса — отогнуты вдоль ступени, как у ПС-1 на носителе.
                            m.localPosition = new Vector3(0, len * 0.5f, 0);
                            m.localScale = Vector3.one * (2 * r / SputnikModelDiameter);
                            palette = new[] { PolishedColor, MetalColor };
                            break;
                        case SectionModel.VostokService:
                            // Начало у верха (стык с СА), срез ТДУ — на днище секции.
                            m.localPosition = new Vector3(0, len, 0);
                            m.localScale = new Vector3(2 * r / ServiceModelDiameter, len / ServiceModelHeight, 2 * r / ServiceModelDiameter);
                            palette = new[] { col, MetalColor, NozzleColor };
                            break;
                        default:
                            // Е-6: начало у среза КТДУ = днище секции, опоры добавляются ниже как у процедурной.
                            m.localScale = new Vector3(2 * r / Luna9ModelDiameter, len / Luna9ModelHeight, 2 * r / Luna9ModelDiameter);
                            palette = new[] { NozzleColor, MetalColor, col, PolishedColor };
                            break;
                    }
                    part.AddBody(AddRenderer(m.gameObject, model, palette), palette);
                }
                else if (s.Kind == SectionKind.Capsule && s.Sphere && capsuleFbx != null)
                {
                    // СА «Восток» из Blender: начало у днища, шар процедурной версии стоит центром на len/2.
                    var m = AddChild(go, "Model");
                    m.localPosition = new Vector3(0, len * 0.5f - r, 0);
                    m.localScale = Vector3.one * (2 * r / CapsuleModelDiameter);
                    var palette = new[] { col, MetalColor, NozzleColor };
                    part.AddBody(AddRenderer(m.gameObject, capsuleFbx, palette), palette);
                }
                else if (s.Kind == SectionKind.Fairing && fairingFbx != null)
                {
                    // Створки из Blender (модель — половина со стороны −X). Целый обтекатель — две, сброшенная
                    // створка (Vessel.FairingHalf ±1, сторона ±X) — одна; +X — та же модель, повёрнутая на 180°.
                    var palette = new[] { FairingColor, MetalColor };
                    for (int side = -1; side <= 1; side += 2)
                    {
                        if (Vessel.FairingHalf != 0 && Vessel.FairingHalf != side) continue;
                        var m = AddChild(go, side < 0 ? "Fairing −X" : "Fairing +X");
                        m.localRotation = Quaternion.Euler(0, side < 0 ? 0 : 180, 0);
                        m.localScale = new Vector3(r / FairingModelRadius, len / FairingModelHeight, r / FairingModelRadius);
                        part.AddBody(AddRenderer(m.gameObject, fairingFbx, palette), palette);
                    }
                }
                else
                {
                    var palette = new[] { col };
                    part.AddBody(AddRenderer(go, mesh, palette), palette);
                }
                if (s.FinArea > 0 && finsFbx != null)
                {
                    // Хвостовой отсек Г-1 со стабилизаторами: начало у низа, корпус модели вписан в радиус секции.
                    var f = AddChild(go, "Fins");
                    f.localScale = Vector3.one * (r / FinsModelBodyRadius);
                    var palette = new[] { col, MetalColor, NozzleColor };
                    part.AddBody(AddRenderer(f.gameObject, finsFbx, palette), palette);
                }
                if (s.LandingLegs && legFbx != null)
                {
                    // Четыре опоры по кромке: шарнир поднят на вынос стопы, чтобы стопы стояли в плоскости днища —
                    // ядро сажает борт по днищу (Vessel.PlaceOnSurface), опоры его не продлевают.
                    for (int k = 0; k < 4; k++)
                    {
                        var leg = new GameObject("Leg");
                        leg.transform.SetParent(go.transform, false);
                        // −X модели — наружу: поворот на 90·k + 45° ставит опоры между связями.
                        var yaw = Quaternion.Euler(0, 90 * k + 45, 0);
                        leg.transform.localRotation = yaw;
                        leg.transform.localPosition = yaw * new Vector3(-r, Mathf.Min(LegModelDrop, len), 0);
                        AddRenderer(leg, legFbx, NozzleColor);
                    }
                }

                if (s.HasEngine)
                {
                    // Связка сопел под днищем: одно — по центру, больше — кольцо (+ центральное от 5 штук).
                    int n = s.EngineCount, ring = n >= 5 ? n - 1 : n == 1 ? 0 : n;
                    float rr = ring > 0 ? r * NozzleRing : 0;
                    float nr = ring > 0 ? Mathf.Min(rr * Mathf.Sin(Mathf.PI / Mathf.Max(ring, 2)) * NozzleGap, r * 0.32f)
                                        : Mathf.Min(r * 0.45f, 1.2f);
                    // Сопло уже в модели аппарата (ТДУ «Востока», КТДУ Е-6): факел — от днища секции.
                    bool ownNozzle = model != null && s.Model != SectionModel.Sputnik;
                    if (ownNozzle && s.Model == SectionModel.VostokService) nr = r * ServiceNozzleShare;
                    var nozzle = new GameObject("Nozzle");
                    nozzle.transform.SetParent(go.transform, false);
                    nozzle.transform.localPosition = new Vector3(0, ownNozzle ? 0 : -nr * 1.4f, 0);
                    var bell = ProcMesh.Bell(nr, nr * 0.45f, nr * 1.4f, 16);
                    if (ring == 0 && !ownNozzle && i > 0 && interstageFbx != null)
                    {
                        // Ферма горячего разделения вокруг двигателя верхней ступени (Блок Е «Востока», II ступень
                        // «Кары»): от днища вниз на высоту колокола, до стыка с нижней ступенью.
                        var t = AddChild(go, "Interstage");
                        t.localPosition = new Vector3(0, -nr * 1.4f, 0);
                        t.localScale = new Vector3(r / InterstageModelRadius, nr * 1.4f / InterstageModelHeight, r / InterstageModelRadius);
                        var palette = new[] { col, MetalColor };
                        part.AddBody(AddRenderer(t.gameObject, interstageFbx, palette), palette);
                    }
                    // Связка (≥ 2) — блоки РД-107 из Blender: двигатель в 4 камеры, как у «семёрки». Одиночный
                    // двигатель — РД-0110 (рама, ТНА, сопло), без модели — колокол.
                    bool blocks = ring > 0 && engineFbx != null, upper = ring == 0 && upperEngineFbx != null;
                    for (int k = 0; k < (ownNozzle ? 0 : n); k++)
                    {
                        var b = new GameObject(blocks ? "Engine" : "Bell");
                        b.transform.SetParent(nozzle.transform, false);
                        float a = 2 * Mathf.PI * k / Mathf.Max(ring, 1);
                        b.transform.localPosition = k < ring ? new Vector3(rr * Mathf.Cos(a), 0, rr * Mathf.Sin(a)) : Vector3.zero;
                        if (upper)
                        {
                            // Начало модели у верха: поднята на высоту колокола, вписана в его габарит.
                            b.name = "Engine";
                            b.transform.localPosition = new Vector3(0, nr * 1.4f, 0);
                            b.transform.localScale = new Vector3(2 * nr / UpperEngineModelDiameter, nr * 1.4f / UpperEngineModelHeight,
                                                                 2 * nr / UpperEngineModelDiameter);
                            AddRenderer(b, upperEngineFbx, StageColor, MetalColor, NozzleColor);
                            continue;
                        }
                        if (!blocks) { AddRenderer(b, bell, NozzleColor); continue; }
                        // Блок вписан в габарит колокола: ширина 2·nr, высота 1,4·nr от среза до днища ступени.
                        // Начало модели — у верха, поэтому поднят на высоту колокола; широкой стороной — по касательной.
                        float w = 2 * nr / EngineModelWidth;
                        b.transform.localPosition += new Vector3(0, nr * 1.4f, 0);
                        b.transform.localRotation = Quaternion.Euler(0, 90 - a * Mathf.Rad2Deg, 0);
                        b.transform.localScale = new Vector3(w, nr * 1.4f / EngineModelHeight, w);
                        AddRenderer(b, engineFbx, NozzleColor);
                    }
                    // Общий факел на связку: струи у земли сливаются в один столб, отдельные 9 факелов
                    // дали бы 18 аддитивных слоёв и пересвет на стыках.
                    nr = ring > 0 ? (rr + nr) * PlumeClusterFill : nr;

                    // Факел: ядро (сужается) + свечение (расширяется), оба аддитивные двусторонние —
                    // передняя и задняя стенки складываются, к оси струя плотнее, как у объёма.
                    var plume = new GameObject("Plume");
                    plume.transform.SetParent(nozzle.transform, false);
                    part.CoreR = AddPlume(plume, ProcMesh.Plume(1, -0.55f, 1, 16, 12));
                    var glow = new GameObject("Glow");
                    glow.transform.SetParent(nozzle.transform, false);
                    part.GlowR = AddPlume(glow, ProcMesh.Plume(1.1f, 1.6f, 0.6f, 16, 12));
                    var lgo = new GameObject("Plume Light");
                    lgo.transform.SetParent(nozzle.transform, false);
                    lgo.transform.localPosition = new Vector3(0, -nr * 3, 0);
                    var light = lgo.AddComponent<Light>();
                    light.type = LightType.Point;
                    lgo.AddComponent<HDAdditionalLightData>();
                    light.lightUnit = UnityEngine.Rendering.LightUnit.Candela;
                    light.color = new Color(1f, 0.72f, 0.45f);
                    light.range = PlumeLightRange;
                    light.shadows = LightShadows.None;
                    part.PlumeLight = light;
                    part.Plume = plume.transform;
                    part.Glow = glow.transform;
                    part.PlumeRadius = nr;
                    plume.SetActive(false);
                    glow.SetActive(false);
                    lgo.SetActive(false);
                }
                if (s.ParachuteArea > 0) AddChute(part);
                parts.Add(part);
            }
            AddPlasma();
        }

        /// <summary>
        /// Плазма входа (GDD §4.6). Первая версия — конусы с градиентом по длине — давала жёсткие кромки
        /// силуэта (01.10.2026): у HDRP/Unlit нет френеля, и спад к краю можно задать только текстурой поперёк
        /// взгляда. Поэтому ореол — спрайт к камере с радиальным спадом (виден с любого ракурса, сзади —
        /// кольцом вокруг шара), след — LineRenderer (лента к камере вдоль оси, спад поперёк в V).
        /// Плюс точечный свет: плазма подсвечивает лоб аппарата, иначе теневая сторона чёрная.
        /// </summary>
        void AddPlasma()
        {
            var root = new GameObject("Plasma");
            root.transform.SetParent(transform, false);
            var halo = new GameObject("Halo");
            halo.transform.SetParent(root.transform, false);
            halo.AddComponent<MeshFilter>().sharedMesh = ProcMesh.Billboard();
            var mr = halo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = PlasmaMaterial(PlasmaHaloTexture());
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            plasmaHaloR = mr;
            plasmaHalo = halo.transform;

            var wake = new GameObject("Wake");
            wake.transform.SetParent(root.transform, false);
            plasmaWake = wake.AddComponent<LineRenderer>();
            plasmaWake.sharedMaterial = PlasmaMaterial(PlasmaWakeTexture());
            plasmaWake.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            plasmaWake.receiveShadows = false;
            plasmaWake.useWorldSpace = true;
            plasmaWake.textureMode = LineTextureMode.Stretch;
            plasmaWake.alignment = LineAlignment.View;
            plasmaWake.positionCount = 3;
            // Ширина в радиусах борта: у лба шире аппарата (обтекает), к хвосту сходится.
            plasmaWake.widthCurve = new AnimationCurve(new Keyframe(0, 2.6f), new Keyframe(0.2f, 2.2f), new Keyframe(1, 0.7f));

            var lgo = new GameObject("Plasma Light");
            lgo.transform.SetParent(root.transform, false);
            plasmaLight = lgo.AddComponent<Light>();
            plasmaLight.type = LightType.Point;
            lgo.AddComponent<HDAdditionalLightData>();
            plasmaLight.lightUnit = UnityEngine.Rendering.LightUnit.Candela;
            plasmaLight.color = PlasmaTint;
            plasmaLight.shadows = LightShadows.None;
            plasma = root.transform;
            root.SetActive(false);
        }

        Material PlasmaMaterial(Texture2D tex)
        {
            var m = new Material(plumeMat) { name = "Plasma (runtime)" };
            m.SetTexture("_EmissiveColorMap", tex);
            m.EnableKeyword("_EMISSIVE_COLOR_MAP");
            return m;
        }

        /// <summary>Ореол: горячее ядро у центра, край гаснет в ноль (спад — квадрат, без кромки).</summary>
        static Texture2D haloTex, wakeTex;
        static Texture2D PlasmaHaloTexture()
        {
            if (haloTex != null) return haloTex;
            const int n = 64;
            haloTex = new Texture2D(n, n, TextureFormat.RGBAHalf, false, true) { name = "Plasma Halo", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float f = Mathf.Clamp01(1 - r);
                f = f * f * f;
                var c = Color.Lerp(new Color(1f, 0.6f, 0.45f), new Color(1f, 0.92f, 0.8f), f);
                haloTex.SetPixel(x, y, new Color(c.r * f, c.g * f, c.b * f, 1));
            }
            haloTex.Apply(false, true);
            return haloTex;
        }

        /// <summary>След: U — вдоль (0 у лба, горячо → красное → ноль), V — поперёк (гаусс, края в ноль).</summary>
        static Texture2D PlasmaWakeTexture()
        {
            if (wakeTex != null) return wakeTex;
            const int nu = 64, nv = 32;
            wakeTex = new Texture2D(nu, nv, TextureFormat.RGBAHalf, false, true) { name = "Plasma Wake", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < nv; y++)
            for (int x = 0; x < nu; x++)
            {
                float t = x / (nu - 1f), v = (y + 0.5f) / nv * 2 - 1;
                float across = Mathf.Exp(-v * v * 5) * (1 - v * v);
                float along = Mathf.Clamp01(t * 8) * Mathf.Exp(-3f * t) * (1 - t);
                var c = Color.Lerp(new Color(1f, 0.75f, 0.55f), new Color(0.9f, 0.3f, 0.2f), Mathf.Clamp01(t * 1.6f));
                float f = across * along;
                wakeTex.SetPixel(x, y, new Color(c.r * f, c.g * f, c.b * f, 1));
            }
            wakeTex.Apply(false, true);
            return wakeTex;
        }

        void UpdatePlasma(Vector3 airflow, double com, double length, double radius)
        {
            float heat = (float)Vessel.HeatFlux;
            float k = Mathf.Clamp01((heat - PlasmaStart) / (PlasmaFull - PlasmaStart));
            bool on = k > 0.001f && Vessel.SurfaceSpeed > 100;
            k = on ? k * k * (3 - 2 * k) : 0;
            // Накал гаснет вместе с плазмой; MPB перезаписывается целиком, поэтому базовый цвет — заново.
            if (on || heatGlowOn)
            {
                // Альфа = 1: Color * float умножает и её (см. SetEmissive).
                var glow = HeatGlowTint * (HeatGlowNits * k);
                glow.a = 1;
                foreach (var p in parts)
                    for (int j = 0; j < p.BodyR.Count; j++) Paint(p.BodyR[j], p.BodyColors[j], glow);
            }
            heatGlowOn = on;
            plasma.gameObject.SetActive(on);
            if (!on) return;
            float r = (float)radius;
            // Лоб — тот торец, что идёт первым; ударная волна стоит чуть впереди него.
            var nose = transform.up;
            float front = Vector3.Dot(nose, airflow) >= 0 ? (float)(length - com) : (float)-com;
            var bow = transform.TransformPoint(0, front, 0) + airflow * (r * ShockStandoff);
            plasma.position = bow;
            float flicker = Flicker(PlasmaFlicker, 17);
            var cam = Camera.main;
            if (cam != null) plasmaHalo.rotation = cam.transform.rotation;
            plasmaHalo.position = bow + airflow * (r * PlasmaHaloAhead);
            plasmaHalo.localScale = Vector3.one * (r * PlasmaHalo * (0.7f + 0.3f * k));
            float wakeLen = r * PlasmaWakeLength * (0.3f + 0.7f * k) * flicker + (float)length;
            float wakeFade = 1;
            if (cam != null)
            {
                // Лента к камере вдоль оси: глядя по оси, она разворачивается в диск во всю ширину и закрывает
                // аппарат светлым пятном (01.10.2026). Гасим по sin² угла между взглядом и потоком.
                float axial = Vector3.Dot(cam.transform.forward, airflow);
                wakeFade = 1 - axial * axial;
                wakeFade *= wakeFade;
                // Камера внутри «трубы» следа — укоротить ленту, чтобы её гаснущий конец был перед камерой.
                var rel = cam.transform.position - bow;
                float along = -Vector3.Dot(rel, airflow);
                float side = (rel + airflow * along).magnitude;
                if (along > 0 && side < r * 3)
                    wakeLen = Mathf.Clamp(along - r * WakeCameraClearance, r * 0.5f, wakeLen);
            }
            plasmaWake.SetPosition(0, bow + airflow * (r * ShockStandoff));
            plasmaWake.SetPosition(1, bow - airflow * (wakeLen * 0.25f));
            plasmaWake.SetPosition(2, bow - airflow * wakeLen);
            plasmaWake.widthMultiplier = r;
            float nits = PlasmaNits * k * flicker;
            ReportPlume(nits);
            ReportPlasma(PlasmaNits * k);
            SetEmissive(plasmaHaloR, PlasmaTint * nits);
            SetEmissive(plasmaWake, PlasmaTint * (PlasmaWakeNits * k * wakeFade));
            // Сила света = яркость × видимая площадь ударного слоя (диск радиуса r).
            plasmaLight.intensity = nits * Mathf.PI * r * r;
            plasmaLight.range = r * PlasmaLightRange;
        }

        /// <summary>Купол и стропы: корень в точке крепления (верх секции), +Y — против набегающего потока.</summary>
        static Transform AddChild(GameObject parent, string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent.transform, false);
            return t;
        }

        void AddChute(Part part)
        {
            var root = new GameObject("Parachute");
            root.transform.SetParent(transform, false);
            var canopy = new GameObject("Canopy");
            canopy.transform.SetParent(root.transform, false);
            canopy.AddComponent<MeshFilter>().sharedMesh = ProcMesh.Dome(ChuteDomeAngle, ChuteGores * 2, 8);
            var mr = canopy.AddComponent<MeshRenderer>();
            mr.sharedMaterial = bodyMat;
            mpb.Clear();
            mpb.SetColor("_BaseColor", Color.white);
            mpb.SetTexture("_BaseColorMap", GoreStripes());
            mr.SetPropertyBlock(mpb);

            var lines = root.AddComponent<LineRenderer>();
            lines.sharedMaterial = bodyMat;
            lines.useWorldSpace = false;
            lines.widthMultiplier = ChuteLineWidth;
            lines.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mpb.Clear();
            mpb.SetColor("_BaseColor", new Color(0.85f, 0.83f, 0.78f));
            lines.SetPropertyBlock(mpb);
            // Ломаная «крепление → кромка → крепление → …»: одна линия вместо отдельной на каждую стропу.
            lines.positionCount = ChuteGores * 2;

            part.Chute = root.transform;
            part.Canopy = canopy.transform;
            part.Lines = lines;
            root.SetActive(false);
        }

        /// <summary>Полотнища вдоль U: чётные оранжевые, нечётные белые.</summary>
        static Texture2D stripes;
        static Texture2D GoreStripes()
        {
            if (stripes != null) return stripes;
            stripes = new Texture2D(ChuteGores, 1, TextureFormat.RGBA32, false) { name = "Chute Gores", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            for (int i = 0; i < ChuteGores; i++)
                stripes.SetPixel(i, 0, i % 2 == 0 ? new Color(0.95f, 0.42f, 0.10f) : new Color(0.93f, 0.92f, 0.88f));
            stripes.Apply(false, true);
            return stripes;
        }

        /// <summary>Сколько метров над бортом занимает раскрытый парашют (0 — нет купола). FlightCamera берёт его
        /// в кадр; полный радиус, а не текущий, — иначе кадр «дышал» бы весь процесс наполнения.</summary>
        public static float ChuteReach(Vessel v)
        {
            float reach = 0;
            var secs = v.Design.Sections;
            for (int i = 0; i < secs.Count; i++)
            {
                if (!v.Attached[i] || !v.ChuteDeployed[i] || v.ChuteFailed[i] || secs[i].ParachuteArea <= 0) continue;
                float r = Mathf.Sqrt((float)secs[i].ParachuteArea / Mathf.PI);
                reach = Mathf.Max(reach, r * (ChuteRiser + 1));
            }
            return reach;
        }

        void UpdateChute(Part p, Vector3 airflow)
        {
            var s = Vessel.Design.Sections[p.Index];
            bool open = Vessel.ChuteDeployed[p.Index] && !Vessel.ChuteFailed[p.Index];
            p.Chute.gameObject.SetActive(open);
            if (!open) return;
            float full = Mathf.Sqrt((float)s.ParachuteArea / Mathf.PI);
            float r = Mathf.Sqrt((float)(s.ParachuteArea * FlightPhysics.ChuteFraction(Vessel.ChuteOpenTime[p.Index])) / Mathf.PI);
            r = Mathf.Max(r, 0.3f);
            // Выпуск: первую секунду купол вытягивается из контейнера на стропах.
            float riser = full * ChuteRiser * Mathf.Clamp01((float)Vessel.ChuteOpenTime[p.Index] + 0.2f);

            // Крепление — верх секции; купол против потока, с лёгким раскачиванием.
            p.Chute.position = p.Tr.TransformPoint(0, (float)s.Length, 0);
            float t = Time.time;
            var sway = Quaternion.Euler(3 * Mathf.Sin(t * 0.9f + p.Index), 0, 3 * Mathf.Sin(t * 0.7f));
            p.Chute.rotation = Quaternion.FromToRotation(Vector3.up, -airflow) * sway;
            p.Canopy.localPosition = new Vector3(0, riser, 0);
            p.Canopy.localScale = new Vector3(r, r, r);
            for (int k = 0; k < ChuteGores; k++)
            {
                float a = 2 * Mathf.PI * k / ChuteGores;
                p.Lines.SetPosition(2 * k, Vector3.zero);
                p.Lines.SetPosition(2 * k + 1, new Vector3(r * Mathf.Cos(a), riser, r * Mathf.Sin(a)));
            }
        }

        Renderer AddPlume(GameObject go, Mesh mesh)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = plumeMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mr;
        }

        /// <summary>Градиент по длине струи (V = 0 у среза): белый → жёлтый → оранжево-красный, яркость спадает.</summary>
        static Texture2D gradient;
        static Texture2D PlumeGradient()
        {
            if (gradient != null) return gradient;
            const int n = 64;
            gradient = new Texture2D(1, n, TextureFormat.RGBAHalf, false, true) { name = "Plume Gradient", wrapMode = TextureWrapMode.Clamp };
            for (int i = 0; i < n; i++)
            {
                float t = i / (n - 1f);
                var c = Color.Lerp(Color.Lerp(new Color(1f, 0.95f, 0.85f), new Color(1f, 0.7f, 0.3f), Mathf.Clamp01(t * 3)),
                                   new Color(0.9f, 0.3f, 0.1f), Mathf.Clamp01(t * 1.5f - 0.3f));
                // Срез без резкой кромки, хвост гаснет в ноль.
                float fade = Mathf.Clamp01(t * 12) * Mathf.Exp(-3.5f * t) * (1 - t);
                // Альфа = 1: аддитив HDRP умножает цвет на альфу — затухание только в RGB.
                gradient.SetPixel(0, i, new Color(c.r * fade, c.g * fade, c.b * fade, 1));
            }
            gradient.Apply(false, true);
            return gradient;
        }

        void SetPlumeColor(Renderer r, float nits) => SetEmissive(r, new Color(nits, nits, nits));

        void SetEmissive(Renderer r, Color nits)
        {
            mpb.Clear();
            // Через эмиссию, а не _UnlitColor: цвет Unlit HDRP не умножается на экспозицию (1 нит уже белый),
            // эмиссия — умножается. Альфа 1: аддитив HDRP умножает цвет на альфу. Градиент — в материале.
            nits.a = 1;
            mpb.SetColor("_EmissiveColor", nits);
            r.SetPropertyBlock(mpb);
        }

        Renderer AddRenderer(GameObject go, Mesh mesh, params Color[] palette)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            // У FBX слоты материалов — отдельные субмеши: с одним материалом рисовался бы только первый.
            if (mesh.subMeshCount > 1)
            {
                var mats = new Material[mesh.subMeshCount];
                for (int i = 0; i < mats.Length; i++) mats[i] = bodyMat;
                mr.sharedMaterials = mats;
            }
            else mr.sharedMaterial = bodyMat;
            Paint(mr, palette, Color.clear);
            return mr;
        }

        /// <summary>
        /// Цвет корпуса и накал. Один цвет — блок на весь рендерер; несколько — по блоку на слот (порядок слотов
        /// FBX — материалы Blender, см. Tooltip полей GameBootstrap): блок слота приоритетнее общего.
        /// Эмиссия с альфой 0 не пишется.
        /// </summary>
        void Paint(Renderer mr, Color[] palette, Color emissive)
        {
            for (int i = 0; i < palette.Length; i++)
            {
                // mpb общий с факелом: без Clear корпус после отделения ступени наследовал эмиссию факела
                // (3·10³ нит) и ночью при EV −5 выбеливал кадр целиком.
                mpb.Clear();
                mpb.SetColor("_BaseColor", palette[i]);
                if (emissive.a > 0) mpb.SetColor("_EmissiveColor", emissive);
                if (palette.Length == 1) mr.SetPropertyBlock(mpb);
                else mr.SetPropertyBlock(mpb, i);
            }
        }

        /// <summary>Самый яркий видимый факел за кадр, нит — SkyController поднимает по нему нижний предел EV
        /// (иначе ночью предэкспонированный факел переполняет half-буфер). Порядок LateUpdate не важен:
        /// значение живёт и следующий кадр.</summary>
        public static float PlumePeakNits => Time.frameCount - peakFrame <= 1 ? peak : 0;
        static float peak;
        static int peakFrame;

        static void ReportPlume(float nits)
        {
            if (peakFrame != Time.frameCount) { peakFrame = Time.frameCount; peak = 0; }
            peak = Mathf.Max(peak, nits);
        }

        /// <summary>Яркость ударного слоя за этот кадр, нит (без дрожания) — для нижнего предела EV на входе.</summary>
        public static float PlasmaPeakNits => Time.frameCount - plasmaFrame <= 1 ? plasmaPeak : 0;
        static float plasmaPeak;
        static int plasmaFrame;

        static void ReportPlasma(float nits)
        {
            if (plasmaFrame != Time.frameCount) { plasmaFrame = Time.frameCount; plasmaPeak = 0; }
            plasmaPeak = Mathf.Max(plasmaPeak, nits);
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (Vessel == null || u?.Active == null) return;
            bool show = Vessel.Alive && !MapView.IsOpen;
            var pos = FloatingOrigin.ToUnity(FloatingOrigin.WorldP(Vessel));
            if (Vessel != u.Active && pos.magnitude > DrawDistance) show = false;
            SetVisible(show);
            if (!show) return;

            if (Signature() != builtSignature) Rebuild();
            Vessel.MassProperties(out _, out double com, out double vesselLen, out double vesselR);
            Vessel.Layout(baseHeight);
            transform.SetPositionAndRotation(pos, FloatingOrigin.ToQuaternion(Vessel.Attitude));

            float pressure = (float)(Vessel.StaticPressure / SeaLevelPressure);
            // Набегающий поток — скорость относительно вращающейся атмосферы; у стоящего борта — местная вертикаль.
            var air = Vessel.Velocity - Vessel.Body.SurfaceVelocity(Vessel.Position);
            var airflow = air.magnitude > 1 ? FloatingOrigin.DirToUnity(air).normalized
                                            : -FloatingOrigin.DirToUnity(Vessel.Position).normalized;
            UpdatePlasma(airflow, com, vesselLen, vesselR);
            foreach (var p in parts)
            {
                p.Tr.localPosition = new Vector3(0, (float)(baseHeight[p.Index] - com), 0);
                if (p.Chute != null) UpdateChute(p, airflow);
                if (p.Plume == null) continue;
                bool on = Vessel.Running[p.Index];
                float thr = on ? (float)Vessel.EffectiveThrottle(p.Index) : 0;
                p.Throttle = thr;
                if (thr > 0.01f) ReportPlume(CoreNits * thr);
                bool burning = thr > 0.01f;
                p.Plume.gameObject.SetActive(burning);
                p.Glow.gameObject.SetActive(burning);
                p.PlumeLight.gameObject.SetActive(burning);
                if (!burning) continue;
                // В вакууме струя раздувается и удлиняется; яркость/длина — от дросселя (§9.5).
                float vac = 1 - Mathf.Clamp01(pressure);
                float spread = 1 + vac * PlumeVacuumGrowth;
                float len = p.PlumeRadius * PlumeLengthSL * (0.4f + 0.6f * thr) * spread;
                float flicker = Flicker(PlumeFlicker, p.Index);
                float coreR = p.PlumeRadius * (1 + vac * PlumeVacuumGrowth * CoreSpread);
                p.Plume.localScale = new Vector3(coreR, len * flicker, coreR);
                p.Glow.localScale = new Vector3(p.PlumeRadius * spread, len * GlowLength * flicker, p.PlumeRadius * spread);
                // Яркость на единицу площади: раздувшаяся струя тусклее (§9.5).
                float bright = thr * Mathf.Lerp(1, VacuumBrightness, vac) * flicker;
                SetPlumeColor(p.CoreR, CoreNits * bright);
                SetPlumeColor(p.GlowR, GlowNits * bright / spread);
                // Свет факела дрожит слабее струи: им освещён весь стол и дым, и та же амплитуда читалась
                // миганием всей сцены.
                p.PlumeLight.intensity = PlumeCandela * thr * (1 + (flicker - 1) * 0.3f);
            }
        }

        /// <summary>
        /// Срез самой нижней работающей связки — источник дыма для ExhaustTrail (GDD §9.5): точка в осях Unity,
        /// радиус общего факела, дроссель. Нижней — потому что дым у старта идёт из-под пакета, а верхние
        /// работающие ступени в шлейфе неотличимы. false — ничего не горит или борт не виден.
        /// </summary>
        public bool ExhaustSource(out Vector3 pos, out float radius, out float throttle)
        {
            pos = default; radius = 0; throttle = 0;
            if (!visible) return false;
            Part best = null;
            foreach (var p in parts)
                if (p.Plume != null && p.Throttle > 0.01f && (best == null || p.Tr.localPosition.y < best.Tr.localPosition.y))
                    best = p;
            if (best == null) return false;
            pos = best.Plume.position;
            radius = best.PlumeRadius;
            throttle = best.Throttle;
            return true;
        }

        /// <summary>Дрожание факела и плазмы, доля. Было 1 + 0,05·sin 53t + 0,03·sin 31t: 8,4 Гц близко к
        /// половине частоты кадров, и при 15–30 к/с синус шёл через кадр «ярко/тускло» — замер 02.10.2026 на взлёте:
        /// средняя яркость кадра скакала ±4,5 из 57 (8 %) кадр к кадру. Шум Перлина ≈ 4 Гц алиасинга не даёт.</summary>
        const float PlumeFlicker = 0.04f, PlasmaFlicker = 0.06f, FlickerHz = 4f;

        static float Flicker(float amp, int seed) =>
            1 + amp * (2 * Mathf.PerlinNoise(Time.time * FlickerHz, seed * 7.31f) - 1);

        bool visible = true;

        void SetVisible(bool on)
        {
            if (on == visible) return;
            visible = on;
            foreach (Transform ch in transform) ch.gameObject.SetActive(on);
        }
    }

    /// <summary>Процедурные тела вращения вокруг +Y для бортов.</summary>
    public static class ProcMesh
    {
        /// <summary>Внутренняя стенка створки обтекателя — доля внешнего радиуса (толщина оболочки ≈ 2 %).</summary>
        const float FairingInner = 0.98f;

        /// <summary>Усечённый конус от y=0 (радиус r0) до y=h (радиус r1); h &lt; 0 — вниз. caps — торцы.</summary>
        public static Mesh Frustum(float r0, float r1, float h, int seg, bool caps)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                verts.Add(d * r0);
                verts.Add(d * r1 + Vector3.up * h);
            }
            for (int i = 0; i < seg; i++)
            {
                int a = i * 2;
                Quad(tris, a, a + 2, a + 3, a + 1, h > 0);
            }
            if (caps)
            {
                Cap(verts, tris, r0, 0, seg, false);
                Cap(verts, tris, r1, h, seg, true);
            }
            return Finish(verts, tris, "Frustum");
        }

        /// <summary>Стенка раструба изнутри — доля внешнего радиуса (толщина ≈ 6 %).</summary>
        const float BellInner = 0.94f;

        /// <summary>
        /// Раструб сопла: срез радиуса rExit на y = 0, горло rThroat на y = h. Наружная стенка, внутренняя
        /// (обратный обход), кромка среза и дно у горла. Прежний односторонний Frustum снизу и сбоку под углом
        /// был виден только наружной половиной: HDRP/Lit режет изнанку, и сопло «пропадало» с части ракурсов.
        /// </summary>
        public static Mesh Bell(float rExit, float rThroat, float h, int seg)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            // 0 — наружная стенка, 1 — внутренняя; вершины раздельные, чтобы нормали не усреднялись.
            for (int w = 0; w < 2; w++)
            {
                int start = verts.Count;
                float k = w == 0 ? 1 : BellInner;
                for (int i = 0; i <= seg; i++)
                {
                    float a = 2 * Mathf.PI * i / seg;
                    var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    verts.Add(d * rExit * k);
                    verts.Add(d * rThroat * k + Vector3.up * h);
                }
                for (int i = 0; i < seg; i++)
                {
                    int a = start + i * 2;
                    Quad(tris, a, a + 2, a + 3, a + 1, w == 0);
                }
            }
            // Кромка среза: кольцо в плоскости y = 0 лицом вниз.
            int lip = verts.Count;
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                verts.Add(d * rExit);
                verts.Add(d * rExit * BellInner);
            }
            for (int i = 0; i < seg; i++)
            {
                int a = lip + i * 2;
                tris.Add(a); tris.Add(a + 2); tris.Add(a + 1);
                tris.Add(a + 1); tris.Add(a + 2); tris.Add(a + 3);
            }
            // Дно у горла лицом вниз: в раструб смотрим и видим тёмную камеру, а не небо сквозь сопло.
            Cap(verts, tris, rThroat * BellInner, h, seg, false);
            return Finish(verts, tris, "Bell");
        }

        /// <summary>
        /// Струя вниз от y=0 до y=−1: радиус r(t) = r0·(1 + grow·t^power), t — доля длины. UV.y = t — по нему
        /// градиент яркости. Без торцов: срез закрыт соплом, хвост гаснет в ноль по градиенту.
        /// </summary>
        public static Mesh Plume(float r0, float grow, float power, int seg, int rings)
        {
            var verts = new List<Vector3>();
            var uv = new List<Vector2>();
            var tris = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                for (int k = 0; k <= rings; k++)
                {
                    float t = (float)k / rings;
                    float r = Mathf.Max(0.02f, r0 * (1 + grow * Mathf.Pow(t, power)));
                    verts.Add(d * r - Vector3.up * t);
                    uv.Add(new Vector2((float)i / seg, t));
                }
            }
            int rc = rings + 1;
            for (int i = 0; i < seg; i++)
            for (int k = 0; k < rings; k++)
            {
                int a = i * rc + k, b = (i + 1) * rc + k;
                Quad(tris, a, b, b + 1, a + 1, false);
            }
            var m = new Mesh { name = "Plume" };
            m.SetVertices(verts);
            m.SetUVs(0, uv);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Купол: сферический сегмент с кромкой радиуса 1 на y = 0, макушка вверх; угол — полураствор сегмента.
        /// Двусторонний (внутренняя поверхность видна снизу). UV.x — по окружности, по полотнищу на 1/16 оборота.
        /// </summary>
        public static Mesh Dome(float angleDeg, int seg, int rings)
        {
            float th = angleDeg * Mathf.Deg2Rad, rho = 1 / Mathf.Sin(th), top = rho * Mathf.Cos(th);
            var verts = new List<Vector3>();
            var uv = new List<Vector2>();
            var tris = new List<int>();
            for (int side = 0; side < 2; side++)
            {
                int start = verts.Count;
                for (int i = 0; i <= seg; i++)
                {
                    float a = 2 * Mathf.PI * i / seg;
                    for (int k = 0; k <= rings; k++)
                    {
                        // k = 0 — кромка, k = rings — макушка; сфера радиуса rho с центром ниже кромки.
                        float t = th * (1 - (float)k / rings);
                        verts.Add(new Vector3(rho * Mathf.Sin(t) * Mathf.Cos(a), rho * Mathf.Cos(t) - top, rho * Mathf.Sin(t) * Mathf.Sin(a)));
                        uv.Add(new Vector2((float)i / seg, (float)k / rings));
                    }
                }
                int rc = rings + 1;
                for (int i = 0; i < seg; i++)
                for (int k = 0; k < rings; k++)
                {
                    int a = start + i * rc + k, b = start + (i + 1) * rc + k;
                    Quad(tris, a, b, b + 1, a + 1, side == 0);
                }
            }
            var m = new Mesh { name = "Dome" };
            m.SetVertices(verts);
            m.SetUVs(0, uv);
            m.SetTriangles(tris, 0);
            // Вершины сторон раздельные — RecalculateNormals даёт каждой стороне свои нормали.
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Квадрат 2×2 в плоскости XY лицом к −Z: спрайт, повёрнутый как камера, смотрит на неё.</summary>
        public static Mesh Billboard()
        {
            var m = new Mesh { name = "Billboard" };
            m.SetVertices(new List<Vector3> { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) });
            m.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            m.SetTriangles(new[] { 0, 3, 2, 0, 2, 1 }, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Шар радиуса r с центром на высоте cy (UV-сфера: seg меридианов, rings поясов).</summary>
        public static Mesh Sphere(float r, float cy, int seg, int rings)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                for (int k = 0; k <= rings; k++)
                {
                    float t = Mathf.PI * k / rings;
                    verts.Add(new Vector3(r * Mathf.Sin(t) * Mathf.Cos(a), cy + r * Mathf.Cos(t), r * Mathf.Sin(t) * Mathf.Sin(a)));
                }
            }
            int rc = rings + 1;
            for (int i = 0; i < seg; i++)
            for (int k = 0; k < rings; k++)
            {
                int a = i * rc + k, b = (i + 1) * rc + k;
                Quad(tris, a, b, b + 1, a + 1, false);
            }
            return Finish(verts, tris, "Sphere");
        }

        /// <summary>
        /// Обтекатель: цилиндр на 55 % длины + оживальный нос. half ±1 — створка со стороны ±X: полуоболочка
        /// с внутренней стенкой (HDRP/Lit режет изнанку, а у раскрытой створки изнанку видно).
        /// </summary>
        public static Mesh Fairing(float r, float len, int seg, int half = 0)
        {
            const int rings = 10;
            const float cyl = 0.55f;
            var prof = new List<Vector2> { new Vector2(r, 0), new Vector2(r, len * cyl) };
            for (int k = 1; k <= rings; k++)
            {
                float t = (float)k / rings;
                prof.Add(new Vector2(r * Mathf.Sqrt(Mathf.Max(0, 1 - t * t)), len * (cyl + (1 - cyl) * t)));
            }
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int pc = prof.Count;
            // Створка — дуга π вокруг своей стороны ±X; изнанка — та же дуга чуть внутри с обратным обходом.
            float arc = half == 0 ? 2 * Mathf.PI : Mathf.PI, a0 = half > 0 ? -Mathf.PI / 2 : Mathf.PI / 2;
            int walls = half == 0 ? 1 : 2;
            for (int w = 0; w < walls; w++)
            {
                int start = verts.Count;
                float k0 = w == 0 ? 1 : FairingInner;
                for (int i = 0; i <= seg; i++)
                {
                    float a = a0 + arc * i / seg;
                    var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    foreach (var p in prof) verts.Add(d * p.x * k0 + Vector3.up * p.y);
                }
                for (int i = 0; i < seg; i++)
                for (int k = 0; k < pc - 1; k++)
                {
                    int a = start + i * pc + k, b = start + (i + 1) * pc + k;
                    Quad(tris, a, b, b + 1, a + 1, w == 0);
                }
            }
            if (half == 0) Cap(verts, tris, r, 0, seg, false);
            return Finish(verts, tris, half == 0 ? "Fairing" : "FairingHalf");
        }

        static void Quad(List<int> t, int a, int b, int c, int d, bool outward)
        {
            // Порядок обхода Unity — по часовой с лицевой стороны; при h < 0 профиль идёт вниз и нормаль переворачивается.
            if (outward) { t.Add(a); t.Add(d); t.Add(c); t.Add(a); t.Add(c); t.Add(b); }
            else { t.Add(a); t.Add(c); t.Add(d); t.Add(a); t.Add(b); t.Add(c); }
        }

        static void Cap(List<Vector3> v, List<int> t, float r, float y, int seg, bool up)
        {
            if (r <= 0) return;
            int c = v.Count;
            v.Add(new Vector3(0, y, 0));
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                v.Add(new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r));
            }
            for (int i = 0; i < seg; i++)
            {
                if (up) { t.Add(c); t.Add(c + i + 2); t.Add(c + i + 1); }
                else { t.Add(c); t.Add(c + i + 1); t.Add(c + i + 2); }
            }
        }

        static Mesh Finish(List<Vector3> v, List<int> t, string name)
        {
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
