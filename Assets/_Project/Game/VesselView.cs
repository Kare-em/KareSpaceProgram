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
    public sealed partial class VesselView : MonoBehaviour
    {
        /// <summary>Дальше этого от активного борта обломки не рисуем — на таком расстоянии они меньше пикселя.</summary>
        const float DrawDistance = 50000;
        /// <summary>
        /// Длина факела в радиусах сопла и рост ширины в вакууме (§9.5: расширение струи). Было ×4 и в длину, и в
        /// ширину: свечение у среза 4,4 радиуса сопла, длина ~105 — у верхней ступени факел втрое шире её самой.
        /// Видимая часть вакуумной струи — ядро у среза, раструб размыт и тускл, поэтому растёт только ширина хвоста.
        /// Пара: GlowLength и меш свечения (основание 1,0 — ровно срез сопла).
        /// </summary>
        const float PlumeLengthSL = 12, PlumeVacuumWidth = 0.4f;
        /// <summary>Связка сопел: кольцо на этой доле радиуса днища, сопло — доля шага кольца (зазор между
        /// раструбами). Пара: PlumeClusterFill — общий факел накрывает кольцо целиком (у земли струи сливаются).</summary>
        const float NozzleRing = 0.62f, NozzleGap = 0.85f, PlumeClusterFill = 0.85f;
        /// <summary>Свечение длиннее ядра во столько раз; ядро в вакууме почти не раздувается.</summary>
        const float GlowLength = 1.8f, CoreSpread = 0.25f;
        /// <summary>Яркость ядра и свечения у среза, нит. Пара: дневная экспозиция EV 14 (§9.3) — серое 18 %
        /// ≈ 2000 нит. Физичные 2·10⁵ не годятся: аддитив складывает 4 стенки, bloom размазывает перебор
        /// на весь экран — кадр белый (замер 01.10.2026). 3·10³ при EV 14 — белое ядро с ореолом.</summary>
        const float CoreNits = 3e3f, GlowNits = 3e2f;
        /// <summary>В вакууме керосиновый факел тусклее: доля яркости при p = 0.</summary>
        const float VacuumBrightness = 0.3f;
        /// <summary>Дальность света факела, м. Сила света — из яркости и площади струи (PlumeLight), ≈ 10³–10⁵ кд:
        /// на 600 м это сотые доли люкса.</summary>
        const float PlumeLightRange = 600;
        /// <summary>Профиль мешей факела r(t) = 1 + grow·t^power (ProcMesh.Plume): ядро сужается, свечение раздувается.
        /// Пара: PlumeShape — по тем же числам считается светящаяся площадь для силы света.</summary>
        const float CoreGrow = -0.55f, CorePower = 1, GlowGrow = 2f, GlowPower = 0.6f;
        /// <summary>Стенок аддитивного меша на луче зрения сбоку: передняя и задняя складываются (§9.5).</summary>
        const float PlumeWalls = 2;
        /// <summary>Свет дрожит слабее струи: им освещены стол и дым, и полная амплитуда читалась миганием сцены.</summary>
        const float PlumeLightFlicker = 0.3f;
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
        /// <summary>Длина следа за шейкой оболочки и дальность подсветки, радиусов борта.</summary>
        const float PlasmaWakeLength = 9, PlasmaLightRange = 15;
        /// <summary>Отход ударной волны от лба, радиусов борта: у сферы Δ ≈ 0,14 r. Пара: LimbEdge в
        /// PlasmaSheathHDRP.shader — толщина светящегося слоя того же порядка.</summary>
        const float ShockStandoff = 0.14f;
        /// <summary>Оболочка ударного слоя (§4.6) снята с деталей борта (BuildSheathMesh): силуэт секций и крыльев, раздутый
        /// по нормалям. Раньше оболочка была телом вращения «от лба вдоль потока» — у «Старшипа», падающего брюхом на
        /// 50 м корпуса, она читалась капсулой вдоль потока (05.10.2026: «форма плазмы должна зависеть от деталей»).
        /// Меш статичный и пересобирается только при смене состава секций; поток подаётся в шейдер каждый кадр
        /// (_Flow в осях борта), раздутие и растяжка в след — в вершинной стадии PlasmaSheathHDRP.
        /// Толщина слоя у плеча, доля местного радиуса; на лбу — доля SheathFront от неё (у сферы Δ ≈ 0,14 r против
        /// 0,35 r у плеча: пара ShockStandoff); с подветренной стороны — SheathLeeLayer.</summary>
        const float SheathLayer = 0.35f, SheathLeeLayer = 0.6f;
        const float SheathFront = ShockStandoff / SheathLayer;
        /// <summary>Нижняя граница радиуса для толщины слоя, доля радиуса секции: на заострённом носу и у полюсов слой
        /// не должен схлопываться в ноль, иначе кончик оболочки отстаёт от корпуса и «протыкает» его.</summary>
        const float SheathLayerFloor = 0.4f;
        /// <summary>Яркость: поверхность вдоль потока (плечо) — доля PlasmaNits, как у прежней капсулы (0,2); степень
        /// наветренности (cos² к лбу, как раньше) и степень ухода подветренной стороны в ноль.
        /// Пара: _Shape/_Glow в PlasmaSheathHDRP.shader.</summary>
        const float SheathSideGlow = 0.2f, SheathWindPow = 2, SheathWakePow = 2;
        /// <summary>Яркость по площади наветренной стороны: у орбитера слой в ~20 раз больше площадью, чем у капсулы того
        /// же радиуса, и брюхо втрое тусклее лба капсулы (05.10.2026: иначе плоскость выбеливает кадр). Множитель =
        /// (площадь капсулы того же радиуса / площадь проекции)^AreaGlowExp, не ниже AreaGlowMin; 20^-0,4 ≈ 0,3.</summary>
        const float AreaGlowExp = 0.4f, AreaGlowMin = 0.2f;
        /// <summary>Растяжка оболочки в след, радиусов борта. Пара: WakeStart = SheathWake/2 — линия следа начинается с
        /// середины растянутого хвоста, как было с шейкой.</summary>
        const float SheathWake = 3;
        const float WakeStart = 1.5f;
        /// <summary>Ширина следа — радиус борта, умноженный на (корень площади проекции / диаметр)^WakeWidthExp:
        /// у капсулы ровно радиус, у широкого брюха — шире, но не во весь размах (иначе лента шире аппарата).</summary>
        const float WakeWidthExp = 0.5f;
        /// <summary>Сборка меша: сегментов по окружности корпуса, колец на закругление торца, глубина закругления торца
        /// (доля радиуса) у обычного торца и у торца с двигателем (колокола свисают под днище).</summary>
        const int SheathSeg = 32, SheathCapRings = 5;
        const float SheathCapDepth = 0.35f, SheathEngineDepth = 0.6f;
        /// <summary>Нос «Старшипа» — оживальный конус на верхней доле длины секции (в ядре секция — цилиндр, нос только в
        /// модели); шаг выборки профиля Р-7, м (пара: ProcMesh.R7RingStep).</summary>
        const float StarshipNoseShare = 0.2f, SheathR7Step = 3;
        /// <summary>Крыло и киль в оболочке: только настоящие плоскости. Символические WingDef (створки «Старшипа» площадью
        /// 1 м² на размах диаметра борта) в оболочку не входят — они бы вытянули плоский «плавник» поперёк корпуса.
        /// Слой вокруг крыла — доля слоя фюзеляжа (пара: SheathLayer); колец вдоль размаха, точек вокруг хорды; корень
        /// начинается не у оси, а в долях радиуса фюзеляжа (внутри корпуса слой не нужен: он удвоил бы свечение).</summary>
        const float WingSheathMinArea = 4, WingLayerShare = 0.5f, WingRootShare = 0.7f;
        const int WingSheathSteps = 5, WingSheathSeg = 16;
        /// <summary>Толщина пластины крыла — те же 8 % хорды и минимум, что у WingMesh (пара: WingMesh.Thickness/ThinMin).</summary>
        const float WingThickness = 0.08f, WingThinMin = 0.06f;
        /// <summary>Толщина слоя при слабом нагреве — доля полной (при полном нагреве 1): слой растёт вместе с плазмой.</summary>
        const float SheathThinInflate = 0.6f;
        static readonly Color SheathHot = new Color(1f, 0.78f, 0.58f), SheathCool = new Color(1f, 0.45f, 0.27f);
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
        /// <summary>Сложенная процедурная опора (§6.12, как в KSP): поворот на шарнире вверх, стопа у борта выше шарнира.
        /// Пара: LegModelReach/Drop — стопа под 231° от +X, после поворота — 107°, на 0,5 м снаружи корпуса.</summary>
        const float GenericLegStow = 124f;
        /// <summary>
        /// Стойка шасси (Landing_Gear.fbx, §4.6 посадка на полосу): от шарнира до низа колёс GearModelReach, шарнир утоплен
        /// в брюхо на GearInset — колёса выходят на GearHeight (1,8) за обшивку, ровно на r + GearHeight, где их ищет
        /// FlightPhysics.CheckContact. Пара: Reach = 1,8 + Inset; меняешь модель — сверяй (winged_parts.b_gear).
        /// Стойки — у носа и у середины (доли длины, по «Шаттлу»: передняя ~7 м за носом, основные под крылом), колея —
        /// доля размаха (6,9 м на 23,79). Сложенная — поворот к носу на GearStow: больше 90°, чтобы колесо ушло в брюхо
        /// целиком (на 90° шина выступала из-под крыла на 0,1 м).
        /// </summary>
        const float GearModelReach = 2.4f, GearInset = 0.6f, GearModelHeight = 1.8f, GearStow = 95f;
        const float NoseGearAt = 0.8f, MainGearAt = 0.35f, GearTrackShare = 0.29f;
        /// <summary>Связка SSME на орбитере, м: центр — на 0,3 м к брюху от оси орбитера, разнос сопел и радиус среза
        /// (winged_parts.b_shuttle: верхнее на +1,0, нижние на −0,95 ±1,35; срез 1,12). Факел бака ET ставится сюда.</summary>
        const float SsmeClusterShift = 0.3f, SsmeClusterR = 1.2f, SsmeExitR = 1.12f;
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
        /// <summary>
        /// Начало модели аппарата у верха (служебный модуль «Аполлона» — от стыка с КМ вниз), если над началом
        /// меньше этой доли высоты модели; иначе начало у днища. Модели в натуральную величину, как и секции.
        /// </summary>
        const float CraftTopOriginShare = 0.5f;

        public Vessel Vessel { get; private set; }

        sealed class Part
        {
            public int Index;
            public Transform Tr;
            /// <summary>Радиальный блок (§5.4): смещение от оси и поворот копии. Связанные оси борта и локальные оси вида
            /// совпадают (Attitude уже в U) — копия c стоит там же, где ядро выпустит обломок c (Vessel.SplitRadial).</summary>
            public Vector3 Radial;
            public Quaternion Yaw = Quaternion.identity;
            public Transform Plume, Glow;
            public Renderer CoreR, GlowR;
            public Light PlumeLight;
            public float PlumeRadius, Throttle;
            public Transform Chute;
            /// <summary>Купола веера (SectionDef.ChuteCount) — дети Chute; стропы всех — одна ломаная Lines.</summary>
            public Transform[] Canopies;
            public LineRenderer Lines;
            /// <summary>Копии факела (Draco на боку Crew Dragon); null — факел один. NoSmoke — не источник шлейфа.</summary>
            public List<Mirror> Mirrors;
            public bool NoSmoke;
            // Корпус может состоять из нескольких рендереров (две створки), у каждого — палитра по слотам.
            public readonly List<Renderer> BodyR = new List<Renderer>();
            public readonly List<Color[]> BodyColors = new List<Color[]>();

            public void AddBody(Renderer r, Color[] palette) { BodyR.Add(r); BodyColors.Add(palette); }
        }

        readonly List<Part> parts = new List<Part>();
        /// <summary>
        /// Опоры и трапы на шарнирах (§6.12): Rest/Base — место и поворот в осях родителя в рабочем положении, Pivot —
        /// ось шарнира, Axis — вокруг чего складывается (Cross(наружу, вверх): плюс поднимает деталь), Stow — угол
        /// сложенной. Раскрытие — Vessel.Deployed секции. Squeeze — опора: при просадке подвески стопа стоит на грунте.
        /// </summary>
        struct Hinge
        {
            public Transform T;
            public Vector3 Rest, Pivot, Axis;
            public Quaternion Base;
            public float Stow;
            public int Section;
            public bool Squeeze;
        }

        readonly List<Hinge> legs = new List<Hinge>();

        /// <summary>Колесо лунохода: узел в центре колеса, крутится вокруг оси X модели на путь своего борта.</summary>
        struct Wheel { public Transform T; public bool Left; }
        readonly List<Wheel> wheels = new List<Wheel>();
        Material bodyMat, plumeMat;
        /// <summary>Шейдер оболочки плазмы: GameBootstrap.PlasmaShader (ссылкой — попадает в билд), в редакторе — Shader.Find.</summary>
        public static Shader SheathShader;
        Transform plasma, sheath;
        Renderer sheathR;
        MeshFilter sheathMf;
        LineRenderer plasmaWake;
        Light plasmaLight;
        bool heatGlowOn;
        string builtSignature;
        double[] baseHeight;
        MaterialPropertyBlock mpb;

        static readonly Color StageColor = new Color(0.82f, 0.83f, 0.80f);
        /// <summary>Воздушный руль бокового Р-7, м: вынос от обечайки, высота, толщина, низ над срезом блока.</summary>
        const float R7RudderSpan = 0.75f, R7RudderHeight = 1.5f, R7RudderThickness = 0.08f, R7RudderBottom = 0.2f;
        static readonly Color PayloadColor = new Color(0.65f, 0.62f, 0.55f);
        static readonly Color CapsuleColor = new Color(0.30f, 0.28f, 0.26f);
        static readonly Color FairingColor = new Color(0.92f, 0.92f, 0.90f);
        static readonly Color NozzleColor = new Color(0.20f, 0.18f, 0.17f);
        // Слоты FBX «Metal» (рамы, баки, антенны) и «Polished» (полированный шар ПС-1, экраны Е-6).
        static readonly Color MetalColor = new Color(0.55f, 0.55f, 0.53f);
        static readonly Color PolishedColor = new Color(0.85f, 0.85f, 0.83f);
        static readonly Color FoilColor = new Color(0.80f, 0.62f, 0.28f);
        static readonly Color BlackColor = new Color(0.07f, 0.07f, 0.07f);
        static readonly Color PanelColor = new Color(0.06f, 0.08f, 0.16f);
        static readonly Color ShieldColor = new Color(0.33f, 0.24f, 0.17f);
        static readonly Color WhiteColor = new Color(0.90f, 0.90f, 0.88f);
        // ЭВТИ «Союза» — серо-зелёная ткань; солнечные батареи американского сегмента МКС — медно-коричневые.
        static readonly Color MliGreenColor = new Color(0.42f, 0.47f, 0.36f);
        static readonly Color SawColor = new Color(0.45f, 0.30f, 0.16f);
        // Цвета слотов под отделки без текущих владельцев (Шаттл, Буран, зонды): цвет — то, как слот выглядит без
        // карт отделки (сцена Hangar, нет FinishMaterials); с картами цвет даёт текстура (SelfColored).
        public static readonly Color FoamColor = new Color(0.78f, 0.38f, 0.14f);
        public static readonly Color TileBlackColor = new Color(0.08f, 0.08f, 0.08f);
        public static readonly Color TileWhiteColor = new Color(0.88f, 0.87f, 0.84f);
        public static readonly Color SilverFoilColor = new Color(0.75f, 0.76f, 0.78f);
        // Межбаковый отсек ET и пояса блока Ц — та же пена, но темнее (пена по-разному загорает под УФ, у ET заметно).
        static readonly Color FoamDarkColor = new Color(0.55f, 0.27f, 0.10f);
        // Углерод-углерод носка и передних кромок орбитера — тёмно-серый.
        static readonly Color RccColor = new Color(0.32f, 0.32f, 0.33f);

        /// <summary>
        /// Отделка слота по его цвету (§9.5): цвета палитр уже различают «что это за поверхность», поэтому отдельного
        /// признака в ядре не нужно. Пара: FlightSceneBuilder.HullFinishes (материал на каждую отделку).
        /// </summary>
        public static HullFinish FinishOf(Color c)
        {
            if (c == FoilColor) return HullFinish.FoilGold;
            if (c == SilverFoilColor) return HullFinish.FoilSilver;
            if (c == ShieldColor || c == CapsuleColor) return HullFinish.Ablative;
            if (c == MetalColor) return HullFinish.Stringer;
            if (c == PolishedColor || c == NozzleColor) return HullFinish.Steel;
            if (c == FoamColor) return HullFinish.Foam;
            if (c == TileBlackColor) return HullFinish.TilesBlack;
            if (c == TileWhiteColor) return HullFinish.TilesWhite;
            return HullFinish.Painted;
        }

        /// <summary>Цвет у отделки в самой текстуре (фольга, пена, плитки, абляция) — _BaseColor белый, палитра не умножается.</summary>
        static bool SelfColored(HullFinish f) => f >= HullFinish.Foam;

        /// <summary>Материал отделки из GameBootstrap.FinishMaterials; нет его — общий bodyMat (окрашенный металл).</summary>
        Material FinishMaterial(HullFinish f)
        {
            var list = GameBootstrap.Instance != null ? GameBootstrap.Instance.FinishMaterials : null;
            int i = (int)f;
            return list != null && i < list.Length && list[i] != null ? list[i] : bodyMat;
        }

        /// <summary>_BaseColor слота: палитра, а у отделок с цветом в текстуре (и их материалом) — белый.</summary>
        Color SlotColor(Color c)
        {
            var f = FinishOf(c);
            return SelfColored(f) && FinishMaterial(f) != bodyMat ? Color.white : c;
        }

        /// <summary>Цвета слотов моделей аппаратов — в порядке материалов FBX (Hull/Metal/Foil… из parts.blend).</summary>
        static Color[] CraftPalette(SectionModel m)
        {
            switch (m)
            {
                case SectionModel.Lunokhod: return new[] { PayloadColor, MetalColor, BlackColor, PolishedColor };
                case SectionModel.Luna17KT: return new[] { FoilColor, MetalColor, StageColor, NozzleColor };
                case SectionModel.LMDescent: return new[] { FoilColor, MetalColor, BlackColor, NozzleColor };
                case SectionModel.LMAscent: return new[] { MetalColor, FoilColor, PolishedColor, BlackColor };
                case SectionModel.ApolloCM: return new[] { ShieldColor, MetalColor, PolishedColor };
                case SectionModel.ApolloSM: return new[] { StageColor, MetalColor, NozzleColor };
                // САС и SLA: слоты Hull/Black/Metal/Nozzle (apollo_fairings.py). Колпак и панели белые, башня — металл.
                case SectionModel.ApolloLES:
                case SectionModel.ApolloSLA: return new[] { WhiteColor, BlackColor, MetalColor, NozzleColor };
                case SectionModel.Mercury:
                case SectionModel.Gemini: return new[] { ShieldColor, BlackColor, MetalColor };
                case SectionModel.GeminiAdapter: return new[] { WhiteColor, MetalColor };
                case SectionModel.Surveyor: return new[] { MetalColor, PanelColor, WhiteColor, FoilColor };
                case SectionModel.Ranger: return new[] { FoilColor, MetalColor, PanelColor };
                // Корпуса ступеней: слоты Hull/Black/Metal/Nozzle (hulls_lib.py). Окраска — по фото: Сатурн, Редстоун,
                // Протон белые; Атлас, Центавр, Аджена — полированная нержавейка; Титан и Блок Д — металл.
                case SectionModel.AtlasBooster:
                case SectionModel.AtlasSustainer:
                case SectionModel.AtlasSustainerAgena:
                case SectionModel.AtlasSustainerCentaur:
                case SectionModel.Agena:
                case SectionModel.Centaur: return new[] { PolishedColor, BlackColor, MetalColor, NozzleColor };
                case SectionModel.TitanStage1:
                case SectionModel.TitanStage2:
                case SectionModel.BlokD: return new[] { MetalColor, BlackColor, PolishedColor, NozzleColor };
                case SectionModel.Redstone:
                case SectionModel.JunoStage1:
                case SectionModel.JunoCluster11:
                case SectionModel.JunoCluster3:
                case SectionModel.SaturnSIC:
                case SectionModel.SaturnSII:
                case SectionModel.SaturnSIVB:
                case SectionModel.ProtonStage1:
                case SectionModel.ProtonStage2:
                case SectionModel.ProtonStage3:
                case SectionModel.Falcon9S1:
                case SectionModel.Falcon9S2:
                case SectionModel.VoskhodAirlock:
                case SectionModel.SoyuzShroud: return new[] { WhiteColor, BlackColor, MetalColor, NozzleColor };
                // station_parts.py: смысл слотов у каждой модели — в docstring её функции.
                case SectionModel.R7BlockI: return new[] { StageColor, BlackColor, MetalColor, NozzleColor };
                case SectionModel.SoyuzPAO: return new[] { MliGreenColor, PanelColor, MetalColor, NozzleColor };
                case SectionModel.SoyuzSA: return new[] { MliGreenColor, BlackColor, MetalColor, ShieldColor };
                case SectionModel.SoyuzBO: return new[] { MliGreenColor, BlackColor, MetalColor, NozzleColor };
                case SectionModel.ISS2000: return new[] { WhiteColor, PanelColor, MetalColor, BlackColor };
                case SectionModel.ISS2020: return new[] { WhiteColor, PanelColor, MetalColor, SawColor };
                case SectionModel.DragonTrunk: return new[] { WhiteColor, PanelColor, MetalColor, BlackColor };
                case SectionModel.CrewDragon: return new[] { WhiteColor, BlackColor, MetalColor, ShieldColor };
                // spacex_parts.py: нержавейка, плитки ТЗП / окна кольца, рули и шарниры, Raptor.
                case SectionModel.SuperHeavy:
                case SectionModel.Starship: return new[] { PolishedColor, TileBlackColor, MetalColor, NozzleColor };
                // winged_parts.py: орбитеры — белые и чёрные плитки ТЗП, RCC носка и кромок, сопла.
                case SectionModel.ShuttleOrbiter:
                case SectionModel.Buran: return new[] { TileWhiteColor, TileBlackColor, RccColor, NozzleColor, GlassColor };
                case SectionModel.ShuttleET:
                case SectionModel.EnergiaCore: return new[] { FoamColor, FoamDarkColor, MetalColor, NozzleColor };
                case SectionModel.ShuttleSRB:
                case SectionModel.EnergiaBlockA: return new[] { WhiteColor, BlackColor, MetalColor, NozzleColor };
                default: return new[] { WhiteColor, PolishedColor };
            }
        }

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

        static readonly Quaternion FlipRotation = Quaternion.Euler(180, 0, 0);

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
            legs.Clear();
            wheels.Clear();
            ResetControls();
            ResetSpaceX();
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
                if (!sj.HasEngine || sj.EngineCount != 1 || sj.IsRadial) return 0;
                bool own = (sj.Model == SectionModel.VostokService && serviceFbx != null) || (sj.Model == SectionModel.Luna9 && luna9Fbx != null)
                           || boot != null && boot.CraftMeshFor(sj.Model) != null;
                return own ? 0 : Mathf.Min((float)sj.Radius * 0.45f, 1.2f) * 1.4f;
            }
            // Радиальная группа — N одинаковых блоков: каждый строим своим Part, чтобы факел и парашют жили у каждой копии.
            var slots = new List<(int i, int c)>();
            for (int i = 0; i < secs.Count; i++)
                for (int c = 0; c < (secs[i].IsRadial ? secs[i].RadialCount : 1); c++) slots.Add((i, c));
            foreach (var (i, copy) in slots)
            {
                if (!Vessel.Attached[i] || Vessel.IsEnclosed(i)) continue;
                var s = secs[i];
                var go = new GameObject(s.IsRadial ? $"{s.Name} №{copy + 1}" : s.Name);
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
                        float bodyLen = Mathf.Max(len - (s.IsRadial ? 0 : TrussHeight(Vessel.Design.NextCore(i))), len * 0.5f);
                        // Р-7 — тела вращения по профилю из ядра (VesselPresets.R7*RadiusAt), остальные — цилиндр.
                        mesh = s.Model == SectionModel.R7BlockA ? ProcMesh.R7Body(bodyLen, false)
                             : s.Model == SectionModel.R7Booster ? ProcMesh.R7Body(len, true)
                             : ProcMesh.Frustum(r, r, bodyLen, 24, true);
                        col = StageColor; break;
                }
                var part = new Part { Index = i, Tr = go.transform };
                if (s.IsRadial)
                {
                    float ang = 2 * Mathf.PI * copy / s.RadialCount + (float)s.RadialPhase;
                    part.Radial = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * (float)s.RadialOffset;
                    // Поворот вокруг оси: +X блока смотрит наружу (Euler по Y на −угол переводит +X в (cos, 0, sin)).
                    part.Yaw = Quaternion.Euler(0, -ang * Mathf.Rad2Deg, 0);
                }
                // Орбитер на баке (SectionDef.Beside) — сбоку от оси, к −X; после отделения бака стоит на оси сам.
                else if (s.Beside && i > 0 && Vessel.Attached[0] && !secs[0].IsRadial)
                    part.Radial = new Vector3(-(float)s.BesideOffset, 0, 0);
                else if (Vessel.RadialYaw != 0)
                    part.Yaw = Quaternion.Euler(0, -(float)Vessel.RadialYaw * Mathf.Rad2Deg, 0);
                // Своя модель аппарата целиком заменяет процедурный корпус; у отсеков с двигателем в ней и сопло.
                Mesh model = s.Model == SectionModel.Sputnik ? sputnikFbx : s.Model == SectionModel.VostokService ? serviceFbx
                           : s.Model == SectionModel.Luna9 ? luna9Fbx : boot != null ? boot.CraftMeshFor(s.Model) : null;
                bool craft = model != null && boot.CraftMeshFor(s.Model) == model;
                // Низ модели в осях секции: у верхних ступеней сопла свисают в юбку нижней — факел ставим под срез.
                float ownBottom = 0;
                if (craft && s.Kind == SectionKind.Fairing && !s.JettisonWhole)
                {
                    // Переходник SLA из Blender: половина со стороны −X в натуральную величину. Целый — две, панель
                    // после раскрытия (Vessel.FairingHalf ±1) — одна, как у створок обтекателя ниже.
                    var palette = CraftPalette(s.Model);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        if (Vessel.FairingHalf != 0 && Vessel.FairingHalf != side) continue;
                        var m = AddChild(go, side < 0 ? "Panel −X" : "Panel +X");
                        m.localRotation = Quaternion.Euler(0, side < 0 ? 0 : 180, 0);
                        part.AddBody(AddRenderer(m.gameObject, model, palette), palette);
                    }
                }
                else if (model != null)
                {
                    var m = AddChild(go, "Model");
                    Color[] palette;
                    switch (craft ? SectionModel.None : s.Model)
                    {
                        case SectionModel.None:
                            // Аппарат в натуральную величину: опоры, сопла и панели уже в модели, масштаб 1.
                            var mb = model.bounds;
                            bool top = mb.max.y < CraftTopOriginShare * mb.size.y;
                            m.localPosition = new Vector3(0, top ? len : 0, 0);
                            ownBottom = Mathf.Min(0, m.localPosition.y + mb.min.y);
                            palette = CraftPalette(s.Model);
                            break;
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
                    if (craft) AddDeployParts(m, i, s, boot.DeployFor(s.Model), palette);
                    if (craft) AddWheels(m, boot.WheelsFor(s.Model), palette);
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
                if (s.Model == SectionModel.R7Booster)
                {
                    // Воздушный руль бокового блока — снаружи у хвоста (+X блока смотрит от оси пакета).
                    var rudder = AddChild(go, "Air Rudder");
                    rudder.localPosition = new Vector3(r + R7RudderSpan * 0.5f, R7RudderBottom + R7RudderHeight * 0.5f, 0);
                    rudder.localScale = new Vector3(R7RudderSpan, R7RudderHeight, R7RudderThickness);
                    var palette = new[] { col };
                    part.AddBody(AddRenderer(rudder.gameObject, ProcMesh.Box(), palette), palette);
                }
                if (s.FinArea > 0 && finsFbx != null && !craft)
                {
                    // Хвостовой отсек Г-1 со стабилизаторами: начало у низа, корпус модели вписан в радиус секции.
                    var f = AddChild(go, "Fins");
                    f.localScale = Vector3.one * (r / FinsModelBodyRadius);
                    var palette = new[] { col, MetalColor, NozzleColor };
                    part.AddBody(AddRenderer(f.gameObject, finsFbx, palette), palette);
                }
                if (s.Wings != null && model == null)
                {
                    // Крылья и киль без своей модели — пластинами по тем же WingDef, что считает аэродинамика (WingMesh).
                    var wm = WingMesh.Build(s.Wings);
                    if (wm != null)
                    {
                        var palette = new[] { col };
                        part.AddBody(AddRenderer(AddChild(go, "Wings").gameObject, wm, palette), palette);
                    }
                }
                if (s.LandingLegs && legFbx != null && !craft)
                {
                    // Четыре опоры по кромке: шарнир поднят на вынос стопы, чтобы стопы стояли в плоскости днища —
                    // ядро сажает борт по днищу (Vessel.PlaceOnSurface), опоры его не продлевают.
                    for (int k = 0; k < 4; k++)
                    {
                        var leg = new GameObject("Leg");
                        leg.transform.SetParent(go.transform, false);
                        // −X модели — наружу: поворот на 90·k + 45° ставит опоры между связями.
                        var yaw = Quaternion.Euler(0, 90 * k + 45, 0);
                        var hinge = yaw * new Vector3(-r, Mathf.Min(LegModelDrop, len), 0);
                        AddRenderer(leg, legFbx, NozzleColor);
                        var outward = yaw * Vector3.left;
                        legs.Add(new Hinge
                        {
                            T = leg.transform, Rest = hinge, Pivot = hinge, Base = yaw, Section = i, Squeeze = true,
                            Axis = Vector3.Cross(outward, Vector3.up), Stow = s.Deploy == DeployKind.None ? 0 : GenericLegStow,
                        });
                    }
                }
                if (s.Deploy == DeployKind.Gear && boot != null && boot.GearMesh != null)
                    AddGear(go, i, s, r, len, boot.GearMesh);
                // Рули и тормозной парашют (VesselView.Controls): у моделей орбитеров вырезаны из FBX, у пластин — поверх.
                AddControls(go, part, i, s, craft ? CraftPalette(s.Model) : new[] { col });
                // Опоры и решётчатые рули Falcon 9, баржа (VesselView.SpaceX).
                AddSpaceX(go, i, s, r);

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
                    var nozzlePos = new Vector3(0, ownNozzle ? (craft ? ownBottom : 0) : -nr * 1.4f, 0);
                    if (craft) OwnPlume(s, i, ref nozzlePos, ref rr, ref nr);
                    var nozzle = new GameObject("Nozzle");
                    nozzle.transform.SetParent(go.transform, false);
                    nozzle.transform.localPosition = nozzlePos;
                    var bell = ProcMesh.Bell(nr, nr * 0.45f, nr * 1.4f, 16);
                    if (ring == 0 && !ownNozzle && i > 0 && interstageFbx != null && !s.IsRadial)
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
                    part.CoreR = AddPlume(plume, ProcMesh.Plume(1, CoreGrow, CorePower, 16, 12));
                    var glow = new GameObject("Glow");
                    glow.transform.SetParent(nozzle.transform, false);
                    part.GlowR = AddPlume(glow, ProcMesh.Plume(1f, GlowGrow, GlowPower, 16, 12));
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
                    if (craft && HasDraco(s)) AddDracoMirrors(part, nozzle.transform);
                }
                if (s.ParachuteArea > 0) AddChute(part);
                parts.Add(part);
            }
            AddPlasma();
            AddAeroFx();
        }

        /// <summary>
        /// Плазма входа (GDD §4.6). Первая версия — конусы с градиентом по длине — давала жёсткие кромки
        /// силуэта (01.10.2026): у HDRP/Unlit нет френеля, и спад к краю можно задать только текстурой поперёк
        /// взгляда. Затем ореол-спрайт к камере — читался шаром перед капсулой (03.10.2026). Затем колпак от лба вдоль потока — не зависел от деталей (05.10.2026). Теперь ударный слой —
        /// меш по составу секций и крыльев (BuildSheathMesh) на своём шейдере (PlasmaSheathHDRP: свечение ∝ 1/|N·V|, мягкий край),
        /// след за кормой — LineRenderer (лента к камере вдоль оси, спад поперёк в V).
        /// Плюс точечный свет: плазма подсвечивает лоб аппарата, иначе теневая сторона чёрная.
        /// </summary>
        void AddPlasma()
        {
            // Rebuild снёс прежний узел оболочки, а меш — сам по себе: освобождаем и сбрасываем ключ, UpdatePlasma соберёт заново.
            if (sheathMesh != null) Destroy(sheathMesh);
            sheathMesh = null;
            sheathSig = null;
            var root = new GameObject("Plasma");
            root.transform.SetParent(transform, false);
            var sh = new GameObject("Sheath");
            sh.transform.SetParent(root.transform, false);
            sheathMf = sh.AddComponent<MeshFilter>();
            var mr = sh.AddComponent<MeshRenderer>();
            var shader = SheathShader != null ? SheathShader : Shader.Find("Kare/Plasma Sheath HDRP");
            if (shader != null) mr.sharedMaterial = new Material(shader) { name = "Plasma Sheath (runtime)" };
            else mr.enabled = false;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            sheathR = mr;
            sheath = sh.transform;

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
            // Ширина в радиусах борта: начало — диаметр шейки оболочки в точке WakeStart (≈ 1,7 r), к хвосту сходится.
            plasmaWake.widthCurve = new AnimationCurve(new Keyframe(0, 1.8f), new Keyframe(0.2f, 1.5f), new Keyframe(1, 0.6f));

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

        static Texture2D wakeTex;

        /// <summary>Точка профиля секции: сдвиг оси по X (наклон боковых блоков Р-7), высота в осях пакета, радиус
        /// и радиус для толщины слоя (с нижней границей SheathLayerFloor).</summary>
        struct SheathPt { public float X, Y, R, Layer; }

        /// <summary>Накопитель меша оболочки: вершины, нормали наружу, толщина слоя в UV.x (м), треугольники.</summary>
        sealed class SheathAcc
        {
            public readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int> T = new List<int>();
            public float MaxLayer;

            public void Vertex(Vector3 p, Vector3 n, float layer)
            {
                V.Add(p); N.Add(n); UV.Add(new Vector2(layer, 0));
                if (layer > MaxLayer) MaxLayer = layer;
            }

            /// <summary>Сшивает rings подряд идущих колец по seg вершин, начиная с first. Шейдер Cull Off — обход неважен.</summary>
            public void Stitch(int first, int rings, int seg)
            {
                for (int j = 0; j + 1 < rings; j++)
                for (int k = 0; k < seg; k++)
                {
                    int i0 = first + j * seg + k, i1 = first + j * seg + (k + 1) % seg, i2 = i0 + seg, i3 = i1 + seg;
                    T.Add(i0); T.Add(i2); T.Add(i1);
                    T.Add(i1); T.Add(i2); T.Add(i3);
                }
            }
        }

        /// <summary>Ключ меша оболочки: состав секций (builtSignature) и маска перевёрнутых (§6.6). Лишний раз меш не строим.</summary>
        string sheathSig;
        int sheathFlip;
        Mesh sheathMesh;
        /// <summary>Габарит корпуса без раздутия, в осях пакета (низ пакета — начало): по нему считаются длина следа,
        /// площадь проекции и положение света.</summary>
        Bounds sheathHull;

        /// <summary>
        /// Профиль тела вращения секции снизу вверх (высота — в осях пакета). Форма — та же, что у процедурного корпуса
        /// Rebuild: шар СА «Востока», усечённый конус капсулы, обтекатель с оживальным носом, профиль Р-7 из ядра; у
        /// «Старшипа» нос — оживало на верхней доле длины. Перевёрнутая секция (§6.6) стоит вверх ногами: профиль зеркалится.
        /// </summary>
        static void SectionProfile(SectionDef s, float baseY, bool flip, List<SheathPt> output)
        {
            float len = (float)s.Length, r = (float)s.Radius;
            var loc = new List<SheathPt>();
            void P(float y, float rr, float x = 0) =>
                loc.Add(new SheathPt { X = x, Y = y, R = rr, Layer = Mathf.Max(rr, r * SheathLayerFloor) });
            if (s.Kind == SectionKind.Capsule && s.Sphere)
            {
                for (int k = 0; k <= 8; k++) { float phi = Mathf.PI * k / 8; P(len * 0.5f * (1 - Mathf.Cos(phi)), r * Mathf.Sin(phi)); }
            }
            else if (s.Kind == SectionKind.Capsule) { P(0, r); P(len, r * 0.35f); }
            else if (s.Kind == SectionKind.Fairing)
            {
                P(0, r); P(len * 0.55f, r);
                for (int k = 1; k <= 6; k++) { float t = k / 6f; P(len * (0.55f + 0.45f * t), r * Mathf.Sqrt(Mathf.Max(0, 1 - t * t))); }
            }
            else if (s.Model == SectionModel.R7BlockA || s.Model == SectionModel.R7Booster)
            {
                bool booster = s.Model == SectionModel.R7Booster;
                int n = Mathf.Max(2, Mathf.CeilToInt(len / SheathR7Step));
                for (int k = 0; k <= n; k++)
                {
                    float y = len * k / n;
                    P(y, (float)(booster ? VesselPresets.R7BoosterRadiusAt(y) : VesselPresets.R7CoreRadiusAt(y)),
                      booster ? (float)VesselPresets.R7BoosterLean(y) : 0);
                }
            }
            else if (s.Model == SectionModel.Starship)
            {
                float y0 = len * (1 - StarshipNoseShare);
                P(0, r); P(y0, r);
                for (int k = 1; k <= 6; k++) { float t = k / 6f; P(y0 + (len - y0) * t, r * Mathf.Pow(1 - t, 0.6f)); }
            }
            else { P(0, r); P(len, r); }
            if (flip)
            {
                for (int k = 0; k < loc.Count; k++) { var q = loc[k]; q.Y = len - q.Y; loc[k] = q; }
                loc.Reverse();
            }
            foreach (var p in loc) { var q = p; q.Y += baseY; output.Add(q); }
        }

        /// <summary>Закругляет торцы профиля (оболочка замкнута, открытого края нет); нос, сошедшийся в точку, не трогаем.
        /// Глубина — доля радиуса торца.</summary>
        static void CapEnds(List<SheathPt> p, float depthBottom, float depthTop)
        {
            if (p.Count == 0) return;
            var a = p[0];
            var b = p[p.Count - 1];
            var bottom = new List<SheathPt>();
            var top = new List<SheathPt>();
            if (a.R > 1e-3f)
                for (int j = 0; j < SheathCapRings; j++)
                {
                    float phi = j / (float)SheathCapRings * 0.5f * Mathf.PI;
                    bottom.Add(new SheathPt { X = a.X, Y = a.Y - depthBottom * a.R * Mathf.Cos(phi), R = a.R * Mathf.Sin(phi), Layer = a.Layer });
                }
            if (b.R > 1e-3f)
                for (int j = 1; j <= SheathCapRings; j++)
                {
                    float phi = j / (float)SheathCapRings * 0.5f * Mathf.PI;
                    top.Add(new SheathPt { X = b.X, Y = b.Y + depthTop * b.R * Mathf.Sin(phi), R = b.R * Mathf.Cos(phi), Layer = b.Layer });
                }
            p.InsertRange(0, bottom);
            p.AddRange(top);
        }

        /// <summary>
        /// Тело вращения по профилю: off — смещение оси в осях борта, yaw — поворот блока (наклон X-сдвига профиля).
        /// Нормаль кольца — среднее нормалей соседних отрезков профиля: на цилиндре смотрит наружу, на торце вдоль оси,
        /// на ступеньке между секциями — под 45°. Эти же нормали шейдер использует для раздутия и для наветренности.
        /// </summary>
        static void Revolve(SheathAcc acc, List<SheathPt> src, Vector3 off, Quaternion yaw)
        {
            var p = new List<SheathPt>(src.Count);
            foreach (var q in src)
                if (p.Count == 0 || Mathf.Abs(q.Y - p[p.Count - 1].Y) > 1e-4f || Mathf.Abs(q.R - p[p.Count - 1].R) > 1e-4f) p.Add(q);
            int m = p.Count;
            if (m < 2) return;
            // Нормаль отрезка в (радиально, вдоль оси): профиль идёт снизу вверх, наружу — вправо от хода.
            var sn = new Vector2[m - 1];
            for (int j = 0; j < m - 1; j++) sn[j] = new Vector2(p[j + 1].Y - p[j].Y, -(p[j + 1].R - p[j].R)).normalized;
            int first = acc.V.Count;
            for (int j = 0; j < m; j++)
            {
                var nn = ((j > 0 ? sn[j - 1] : Vector2.zero) + (j < m - 1 ? sn[j] : Vector2.zero)).normalized;
                if (nn.sqrMagnitude < 0.5f) nn = Vector2.right;
                var centre = off + yaw * new Vector3(p[j].X, 0, 0) + new Vector3(0, p[j].Y, 0);
                for (int k = 0; k < SheathSeg; k++)
                {
                    float th = k * 2 * Mathf.PI / SheathSeg;
                    var u = new Vector3(Mathf.Cos(th), 0, Mathf.Sin(th));
                    acc.Vertex(centre + u * p[j].R, u * nn.x + Vector3.up * nn.y, SheathLayer * p[j].Layer);
                }
            }
            acc.Stitch(first, m, SheathSeg);
        }

        /// <summary>
        /// Консоль крыла или киль оболочкой — вытянутый эллипс сечения (хорда × толщина пластины) вдоль линии 1/4 хорд от
        /// корня к концу, с закруглением обоих концов. Геометрия — та же, что у WingMesh (хорда постоянная, стреловидность
        /// сдвигает конец назад), чтобы слой лежал на пластине, которую считает аэродинамика. Передняя кромка получает
        /// нормаль вперёд, поэтому шейдер раздувает и подсвечивает именно её. f0 — доля размаха, с которой начинается слой.
        /// </summary>
        static void WingLoft(SheathAcc acc, Vector3 rootQ, Vector3 tipQ, Vector3 ch, Vector3 nDir, float f0, float th,
                             float layer, float baseY, Vector3 off, Quaternion yaw)
        {
            const int caps = 3;
            float c = ch.magnitude;
            var chd = ch / c;
            nDir = nDir.normalized;
            var span = tipQ - rootQ;
            var spanU = span.normalized;
            float capLen = Mathf.Max(th * 0.5f, 0.2f);
            var st = new List<(Vector3 q, float scale, float side)>();
            for (int j = 0; j < caps; j++)
            {
                float phi = j / (float)caps * 0.5f * Mathf.PI;
                st.Add((rootQ + span * f0 - spanU * (capLen * Mathf.Cos(phi)), Mathf.Sin(phi), -1));
            }
            for (int j = 0; j <= WingSheathSteps; j++) st.Add((rootQ + span * Mathf.Lerp(f0, 1, j / (float)WingSheathSteps), 1, 0));
            for (int j = 1; j <= caps; j++)
            {
                float phi = j / (float)caps * 0.5f * Mathf.PI;
                st.Add((tipQ + spanU * (capLen * Mathf.Sin(phi)), Mathf.Cos(phi), 1));
            }
            int first = acc.V.Count;
            foreach (var (q, scale, side) in st)
            {
                var centre = q - ch * 0.25f;
                for (int k = 0; k < WingSheathSeg; k++)
                {
                    float a = k * 2 * Mathf.PI / WingSheathSeg, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                    var p = centre + chd * (0.5f * c * ca * scale) + nDir * (0.5f * th * sa * scale);
                    // Нормаль эллипса, у концевых колец — с наклоном вдоль размаха наружу.
                    var nr = (chd * (ca / (0.5f * c)) + nDir * (sa / (0.5f * th))).normalized;
                    nr = (nr * scale + spanU * (side * (1 - scale))).normalized;
                    acc.Vertex(off + yaw * p + new Vector3(0, baseY, 0), yaw * nr, layer);
                }
            }
            acc.Stitch(first, st.Count, WingSheathSeg);
        }

        /// <summary>Крылья и килевые плоскости секции (WingDef) в оболочку — по той же схеме, что WingMesh. Мелкие символические
        /// WingDef (WingSheathMinArea) пропускаем.</summary>
        static void AddWings(SheathAcc acc, SectionDef s, float baseY, Vector3 off, Quaternion yaw)
        {
            if (s.Wings == null) return;
            float secR = (float)s.Radius;
            foreach (var w in s.Wings)
            {
                if (w.Area < WingSheathMinArea || w.Span <= 0) continue;
                float c = (float)w.MeanChord, b = (float)w.Span;
                if (c <= 0) continue;
                float sweep = Mathf.Tan((float)w.Sweep * Mathf.Deg2Rad), th = Mathf.Max(WingThinMin, WingThickness * c);
                float layer = SheathLayer * secR * WingLayerShare;
                var rootQ = new Vector3(-(float)w.Offset, (float)w.Height, 0);
                if (w.Vertical)
                {
                    var tipV = rootQ + new Vector3(-b, -b * sweep, 0);
                    WingLoft(acc, rootQ, tipV, Vector3.up * c, Vector3.forward, 0, th, layer, baseY, off, yaw);
                    continue;
                }
                float inc = (float)w.Incidence * Mathf.Deg2Rad, dih = (float)w.Dihedral * Mathf.Deg2Rad, half = b * 0.5f;
                var ch = new Vector3(-Mathf.Sin(inc), Mathf.Cos(inc), 0) * c;
                float f0 = Mathf.Clamp(WingRootShare * secR / half, 0, 0.5f);
                for (int side = -1; side <= 1; side += 2)
                {
                    var span = new Vector3(-Mathf.Sin(dih), 0, side * Mathf.Cos(dih));
                    var tip = rootQ + span * half + Vector3.down * (half * sweep);
                    WingLoft(acc, rootQ, tip, ch, Vector3.Cross(span, ch), f0, th, layer, baseY, off, yaw);
                }
            }
        }

        /// <summary>
        /// Меш оболочки ударного слоя (§4.6) по составу борта: силуэт секций, раздутый по нормалям в шейдере. Основная стопка
        /// (на оси пакета) — один сквозной профиль без швов между секциями; радиальные блоки и орбитер сбоку (Beside) —
        /// отдельные тела на своих осях, как стоят их Part; крылья и киль — эллиптические «колбаски» вдоль пластины.
        /// Секции под обтекателем не светятся. Меш в осях пакета (начало — низ), узел оболочки стоит со сдвигом −ЦМ.
        /// От направления потока меш не зависит — только от состава секций, поэтому пересобирается при отделении ступени.
        /// </summary>
        Mesh BuildSheathMesh(float rMax)
        {
            var secs = Vessel.Design.Sections;
            var acc = new SheathAcc();
            // Профили секций основной стопки, по возрастанию высоты низа (порядок в Design — снизу вверх, но не гарантия).
            var stack = new List<(float y, bool engine, List<SheathPt> pts)>();
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Vessel.Attached[i] || Vessel.IsEnclosed(i)) continue;
                var s = secs[i];
                float baseY = (float)baseHeight[i];
                bool flip = Vessel.Flipped[i];
                int copies = s.IsRadial ? s.RadialCount : 1;
                for (int c = 0; c < copies; c++)
                {
                    // Положение и поворот — те же три случая, что у Part в Rebuild.
                    Vector3 off = Vector3.zero;
                    Quaternion yaw = Quaternion.identity;
                    bool beside = false;
                    if (s.IsRadial)
                    {
                        float ang = 2 * Mathf.PI * c / s.RadialCount + (float)s.RadialPhase;
                        off = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * (float)s.RadialOffset;
                        yaw = Quaternion.Euler(0, -ang * Mathf.Rad2Deg, 0);
                    }
                    else if (s.Beside && i > 0 && Vessel.Attached[0] && !secs[0].IsRadial)
                    {
                        off = new Vector3(-(float)s.BesideOffset, 0, 0);
                        beside = true;
                    }
                    else if (Vessel.RadialYaw != 0) yaw = Quaternion.Euler(0, -(float)Vessel.RadialYaw * Mathf.Rad2Deg, 0);
                    var pts = new List<SheathPt>();
                    SectionProfile(s, baseY, flip, pts);
                    if (s.IsRadial || beside)
                    {
                        CapEnds(pts, s.HasEngine ? SheathEngineDepth : SheathCapDepth, SheathCapDepth);
                        Revolve(acc, pts, off, yaw);
                    }
                    else
                    {
                        int at = stack.Count;
                        while (at > 0 && stack[at - 1].y > baseY) at--;
                        stack.Insert(at, (baseY, s.HasEngine, pts));
                    }
                    // Перевёрнутая секция (ЛМ после перестроения) крыльев не несёт.
                    if (!flip) AddWings(acc, s, baseY, off, yaw);
                }
            }
            if (stack.Count > 0)
            {
                var line = new List<SheathPt>();
                foreach (var e in stack) line.AddRange(e.pts);
                CapEnds(line, stack[0].engine ? SheathEngineDepth : SheathCapDepth, SheathCapDepth);
                Revolve(acc, line, Vector3.zero, Quaternion.identity);
            }
            if (acc.V.Count == 0) return null;
            var hull = new Bounds(acc.V[0], Vector3.zero);
            foreach (var p in acc.V) hull.Encapsulate(p);
            sheathHull = hull;
            var m = new Mesh { name = "Plasma Sheath (parts)" };
            if (acc.V.Count > 60000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(acc.V);
            m.SetNormals(acc.N);
            m.SetUVs(0, acc.UV);
            var white = new Color[acc.V.Count];
            for (int i = 0; i < white.Length; i++) white[i] = Color.white;
            m.colors = white;
            m.SetTriangles(acc.T, 0);
            // Вершинная стадия уносит вершины за габарит: на слой наружу и в след на SheathWake радиусов.
            float pad = acc.MaxLayer + SheathWake * rMax;
            m.bounds = new Bounds(hull.center, hull.size + Vector3.one * (2 * pad));
            return m;
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
            // Меш оболочки — по составу секций, а не по потоку: пересобираем только при смене состава (отделение ступени,
            // перестроение ЛМ). Узел оболочки (ниже) — начало пакета, меш в осях пакета.
            int flipMask = 0;
            for (int i = 0; i < Vessel.Flipped.Length && i < 31; i++) if (Vessel.Flipped[i]) flipMask |= 1 << i;
            if (!ReferenceEquals(sheathSig, builtSignature) || flipMask != sheathFlip)
            {
                if (sheathMesh != null) Destroy(sheathMesh);
                sheathMesh = BuildSheathMesh(r);
                sheathMf.sharedMesh = sheathMesh;
                sheathSig = builtSignature;
                sheathFlip = flipMask;
            }
            if (sheathMesh == null) { plasma.gameObject.SetActive(false); return; }
            sheath.localPosition = new Vector3(0, (float)-com, 0);
            // Курс борта в осях меша: оболочка и растяжка в след зависят от него в вершинной стадии, меш не трогаем.
            var flow = transform.InverseTransformDirection(airflow);
            float fx = Mathf.Abs(flow.x), fy = Mathf.Abs(flow.y), fz = Mathf.Abs(flow.z);
            var size = sheathHull.size;
            var ext = sheathHull.extents;
            // Вынос корпуса от центра габарита вдоль потока и площадь его проекции на плоскость ⟂ потоку (по ящику габарита).
            float extent = ext.x * fx + ext.y * fy + ext.z * fz;
            float proj = Mathf.Max(size.y * size.z * fx + size.x * size.z * fy + size.x * size.y * fz, 1e-3f);
            float areaGlow = Mathf.Clamp(Mathf.Pow(4 * r * r / proj, AreaGlowExp), AreaGlowMin, 1);
            var center = transform.TransformPoint(sheathHull.center + new Vector3(0, (float)-com, 0));
            // Свет — у лба по ходу: корпус, развёрнутый брюхом, подсвечивается с брюха, а не с носа.
            plasmaLight.transform.position = center + airflow * (extent + r * ShockStandoff);
            // След начинается за хвостом корпуса: от центра габарита назад по потоку на вынос и WakeStart.
            Vector3 wakeFrom = center;
            float wakeStart = extent + r * WakeStart;
            float flicker = Flicker(PlasmaFlicker, 17);
            var cam = Camera.main;
            float wakeLen = wakeStart + r * PlasmaWakeLength * (0.3f + 0.7f * k) * flicker;
            float wakeFade = 1;
            if (cam != null)
            {
                // Лента к камере вдоль оси: глядя по оси, она разворачивается в диск во всю ширину и закрывает
                // аппарат светлым пятном (01.10.2026). Гасим по sin² угла между взглядом и потоком.
                float axial = Vector3.Dot(cam.transform.forward, airflow);
                wakeFade = 1 - axial * axial;
                wakeFade *= wakeFade;
                // Камера внутри «трубы» следа — укоротить ленту, чтобы её гаснущий конец был перед камерой.
                var rel = cam.transform.position - wakeFrom;
                float along = -Vector3.Dot(rel, airflow);
                float side = (rel + airflow * along).magnitude;
                if (along > 0 && side < r * 3)
                    wakeLen = Mathf.Min(wakeLen, along - r * WakeCameraClearance);
                if (wakeLen < wakeStart + r * 0.5f) wakeFade = 0;
            }
            wakeLen = Mathf.Max(wakeLen, wakeStart + r * 0.5f);
            plasmaWake.SetPosition(0, wakeFrom - airflow * wakeStart);
            plasmaWake.SetPosition(1, wakeFrom - airflow * Mathf.Lerp(wakeStart, wakeLen, 0.25f));
            plasmaWake.SetPosition(2, wakeFrom - airflow * wakeLen);
            // Ширина — радиус борта, у широкого брюха/крыла шире (WakeWidthExp): у капсулы ровно r, как было.
            plasmaWake.widthMultiplier = r * Mathf.Pow(Mathf.Sqrt(proj) / (2 * r), WakeWidthExp);
            float nits = PlasmaNits * k * flicker;
            ReportPlume(nits);
            ReportPlasma(PlasmaNits * k);
            // Яркость и цвет — в шейдере по потоку и нормалям; мягкое касание корпуса и гашение у камеры — в метрах.
            // Растяжка в след и толщина слоя растут с нагревом, как и длина ленты следа.
            mpb.Clear();
            mpb.SetColor("_EmissiveColor", new Color(nits, nits, nits, 1));
            mpb.SetFloat("_SoftDist", r * 0.15f);
            mpb.SetFloat("_NearFade", r * 2);
            mpb.SetVector("_Flow", new Vector4(flow.x, flow.y, flow.z, 0));
            mpb.SetFloat("_Inflate", Mathf.Lerp(SheathThinInflate, 1, k));
            mpb.SetFloat("_WakeLen", r * SheathWake * (0.3f + 0.7f * k));
            mpb.SetVector("_Shape", new Vector4(SheathFront, SheathLeeLayer, SheathWindPow, SheathWakePow));
            mpb.SetVector("_Glow", new Vector4(SheathSideGlow, areaGlow, 0, 0));
            mpb.SetVector("_HotTint", SheathHot);
            mpb.SetVector("_CoolTint", SheathCool);
            sheathR.SetPropertyBlock(mpb);
            SetEmissive(plasmaWake, PlasmaTint * (PlasmaWakeNits * k * wakeFade));
            // Сила света = яркость × видимая площадь ударного слоя (диск радиуса r).
            plasmaLight.intensity = nits * Mathf.PI * r * r;
            plasmaLight.range = r * PlasmaLightRange;
        }

        /// <summary>
        /// Шарнир раскладного по модели (§6.12), в осях модели: радиус и высота оси, угол сложенной детали.
        /// Пара: Tools/blender/parts.blend (Surveyor_Leg_*, LM_Leg_*, Luna17_Ramp_*) — верх основной стойки опоры и
        /// кромка настила КТ (FlightPhysics.RampDeckEdge = 1,2 м, настил 1,9 м); меняешь модель — сверяй.
        /// «Сервейор» складывает опоры под обтекатель Ø3 м: 107° — стопа на r 1,0 м; LM — к взлётной ступени, 133°;
        /// трапы КТ сложены почти вертикально над кромкой настила (уклон 30° + 86°): верх отклонён на 4° наружу, чтобы
        /// не задевать колёса лунохода — база 1,7 м, колёса до ±1,105 м (пара: FlightPhysics.RoverHalfBase).
        /// Крышка лунохода (Lunokhod_Lid, Tools/blender/lunokhod_lid.py) в модели открыта вперёд, шарнир на передней
        /// кромке корпуса r 0,8 м, h 1,4 м; 162° — закрытая лежит на приборном отсеке.
        /// </summary>
        static bool DeployHinge(SectionModel model, out float radius, out float height, out float stow)
        {
            switch (model)
            {
                case SectionModel.Surveyor: radius = 0.6f; height = 0.9f; stow = 107; return true;
                case SectionModel.LMDescent: radius = 2.15f; height = 3.0f; stow = 133; return true;
                case SectionModel.Luna17KT: radius = 1.2f; height = 1.9f; stow = 116; return true;
                case SectionModel.Lunokhod: radius = 0.8f; height = 1.4f; stow = 162; return true;
                case SectionModel.SoyuzPAO: radius = PanelHingeR; height = PanelHingeY; stow = PanelStow; return true;
            }
            radius = height = stow = 0;
            return false;
        }

        void AddDeployParts(Transform model, int section, SectionDef s, GameBootstrap.DeployPart[] deploy, Color[] palette)
        {
            if (deploy != null && s.Deploy == DeployKind.Nose) { AddNose(model, section, deploy, palette); return; }
            if (deploy == null || !DeployHinge(s.Model, out float hr, out float hh, out float stow)) return;
            foreach (var d in deploy)
            {
                if (d.Mesh == null) continue;
                var t = AddChild(model.gameObject, d.Mesh.name);
                var pal = new Color[d.Slots != null && d.Slots.Length > 0 ? d.Slots.Length : 1];
                for (int k = 0; k < pal.Length; k++) pal[k] = palette[d.Slots != null && d.Slots.Length > 0 ? Mathf.Min(d.Slots[k], palette.Length - 1) : 0];
                AddRenderer(t.gameObject, d.Mesh, pal);
                legs.Add(new Hinge
                {
                    T = t, Rest = Vector3.zero, Pivot = d.Dir * hr + Vector3.up * hh, Base = Quaternion.identity,
                    Axis = Vector3.Cross(d.Dir, Vector3.up), Section = section,
                    Stow = s.Deploy == DeployKind.None ? 0 : stow, Squeeze = s.Deploy == DeployKind.Legs || s.Deploy == DeployKind.PyroLegs,
                });
            }
        }

        void AddWheels(Transform model, GameBootstrap.DeployPart[] list, Color[] palette)
        {
            if (list == null) return;
            foreach (var d in list)
            {
                if (d.Mesh == null) continue;
                var hub = AddChild(model.gameObject, d.Mesh.name);
                hub.localPosition = d.Dir;
                var t = AddChild(hub.gameObject, "Mesh");
                t.localPosition = -d.Dir;
                var pal = new Color[d.Slots != null && d.Slots.Length > 0 ? d.Slots.Length : 1];
                for (int k = 0; k < pal.Length; k++) pal[k] = palette[d.Slots != null && d.Slots.Length > 0 ? Mathf.Min(d.Slots[k], palette.Length - 1) : 0];
                AddRenderer(t.gameObject, d.Mesh, pal);
                wheels.Add(new Wheel { T = hub, Left = d.Dir.x < 0 });
            }
        }

        /// <summary>
        /// Шасси (DeployKind.Gear): передняя стойка и пара основных из Landing_Gear.fbx, масштаб по GearHeight. Шарнир
        /// у брюха (+X секции), стойка складывается к носу (+Y) вокруг оси размаха и прячется в фюзеляж/крыло.
        /// </summary>
        void AddGear(GameObject go, int i, SectionDef s, float r, float len, Mesh gear)
        {
            float k = (float)s.GearHeight / GearModelHeight;
            float track = r;
            if (s.Wings != null)
                foreach (var w in s.Wings)
                    if (!w.Vertical) { track = (float)w.Span * GearTrackShare; break; }
            float x = r - GearInset * k;
            var spots = new[]
            {
                new Vector3(x, len * NoseGearAt, 0), new Vector3(x, len * MainGearAt, track * 0.5f),
                new Vector3(x, len * MainGearAt, -track * 0.5f),
            };
            foreach (var p in spots)
            {
                var g = new GameObject("Gear");
                g.transform.SetParent(go.transform, false);
                g.transform.localScale = Vector3.one * k;
                AddRenderer(g, gear, MetalColor, BlackColor, PolishedColor, NozzleColor);
                legs.Add(new Hinge
                {
                    T = g.transform, Rest = p, Pivot = p, Base = Quaternion.identity, Axis = Vector3.forward,
                    Stow = GearStow, Section = i, Squeeze = false,
                });
            }
        }

        /// <summary>
        /// Факел у сопел, нарисованных в модели не по оси секции. Бак ET: его RS-25 стоят на орбитере (Beside, к −X), факел
        /// — туда, пока орбитер на баке. Орбитер — пара OMS у киля (один факел между гондолами), «Буран» — пара ОДУ.
        /// Координаты — из winged_parts.py (x Blender = −x Unity).
        /// </summary>
        void OwnPlume(SectionDef s, int i, ref Vector3 at, ref float rr, ref float nr)
        {
            var secs = Vessel.Design.Sections;
            switch (s.Model)
            {
                case SectionModel.ShuttleET:
                    for (int j = 0; j < secs.Count; j++)
                        if (j != i && secs[j].Beside && Vessel.Attached[j])
                        {
                            at = new Vector3(-(float)secs[j].BesideOffset + SsmeClusterShift, 0, 0);
                            rr = SsmeClusterR;
                            nr = SsmeExitR;
                            break;
                        }
                    break;
                case SectionModel.ShuttleOrbiter:
                    at = new Vector3(-1.6f, 1.7f, 0);   // гондолы OMS: 1,6 м к верху, срез на 1,7 м над днищем (b_shuttle)
                    rr = 0.5f;
                    nr = 0.4f;
                    break;
                case SectionModel.CrewDragon:
                    DracoPlume(ref at, ref rr, ref nr);   // Draco на боку капсулы, не по оси сквозь багажник
                    break;
                case SectionModel.Buran:
                    at = new Vector3(-1.2f, 0, 0);      // ОДУ: 1,2 м к верху, ±0,95 по размаху, срез на днище (b_buran)
                    rr = 0.95f;
                    nr = 0.42f;
                    break;
            }
        }

        static Transform AddChild(GameObject parent, string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent.transform, false);
            return t;
        }

        /// <summary>Купол и стропы: корень в точке крепления (верх секции), +Y — против набегающего потока.</summary>
        void AddChute(Part part)
        {
            var root = new GameObject("Parachute");
            root.transform.SetParent(transform, false);
            int n = Mathf.Max(1, Vessel.Design.Sections[part.Index].ChuteCount);
            part.Canopies = new Transform[n];
            var dome = ProcMesh.Dome(ChuteDomeAngle, ChuteGores * 2, 8);
            for (int k = 0; k < n; k++)
            {
                var canopy = new GameObject("Canopy");
                canopy.transform.SetParent(root.transform, false);
                canopy.AddComponent<MeshFilter>().sharedMesh = dome;
                var mr = canopy.AddComponent<MeshRenderer>();
                mr.sharedMaterial = bodyMat;
                mpb.Clear();
                mpb.SetColor("_BaseColor", Color.white);
                mpb.SetTexture("_BaseColorMap", GoreStripes());
                mr.SetPropertyBlock(mpb);
                part.Canopies[k] = canopy.transform;
            }

            var lines = root.AddComponent<LineRenderer>();
            lines.sharedMaterial = bodyMat;
            lines.useWorldSpace = false;
            lines.widthMultiplier = ChuteLineWidth;
            lines.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mpb.Clear();
            mpb.SetColor("_BaseColor", new Color(0.85f, 0.83f, 0.78f));
            lines.SetPropertyBlock(mpb);
            // Ломаная «крепление → кромка → крепление → …»: одна линия вместо отдельной на каждую стропу.
            lines.positionCount = ChuteGores * 2 * n;

            part.Chute = root.transform;
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
                // Верх купола веера: стропы + свой радиус (вынос вбок высоты не добавляет). Пара: ChuteFan.
                ChuteFan(secs[i], 0, out _, out float full, out float rise, out _);
                reach = Mathf.Max(reach, rise + full);
            }
            return reach;
        }

        void UpdateChute(Part p, Vector3 airflow)
        {
            var s = Vessel.Design.Sections[p.Index];
            bool open = Vessel.ChuteDeployed[p.Index] && !Vessel.ChuteFailed[p.Index];
            p.Chute.gameObject.SetActive(open);
            if (!open) return;
            int n = p.Canopies.Length;
            float r = Mathf.Sqrt((float)(s.ParachuteArea / n * FlightPhysics.ChuteFraction(Vessel.ChuteOpenTime[p.Index])) / Mathf.PI);
            r = Mathf.Max(r, 0.3f);
            // Вынос веера — от текущего радиуса: рифлёные купола стоят плотно и расходятся по мере наполнения.
            ChuteFan(s, r, out _, out float full, out float rise, out float spread);
            // Выпуск: первую секунду купол вытягивается из контейнера на стропах.
            float riser = rise * Mathf.Clamp01((float)Vessel.ChuteOpenTime[p.Index] + 0.2f);

            // Крепление — верх секции (или сбоку под носком); купола против потока, с лёгким раскачиванием.
            p.Chute.position = p.Tr.TransformPoint(ChuteRoot(s));
            float t = Time.time;
            var sway = Quaternion.Euler(3 * Mathf.Sin(t * 0.9f + p.Index), 0, 3 * Mathf.Sin(t * 0.7f));
            p.Chute.rotation = Quaternion.FromToRotation(Vector3.up, -airflow) * sway;
            for (int c = 0; c < n; c++)
            {
                float b = 2 * Mathf.PI * c / n;
                var center = new Vector3(spread * Mathf.Cos(b), riser, spread * Mathf.Sin(b));
                // Купол наклонён вдоль своей стропы: веер, а не ряд плоских тарелок.
                var tilt = Quaternion.FromToRotation(Vector3.up, center.normalized);
                p.Canopies[c].localPosition = center;
                p.Canopies[c].localRotation = tilt;
                p.Canopies[c].localScale = new Vector3(r, r, r);
                for (int k = 0; k < ChuteGores; k++)
                {
                    float a = 2 * Mathf.PI * k / ChuteGores;
                    int at = 2 * (c * ChuteGores + k);
                    p.Lines.SetPosition(at, Vector3.zero);
                    p.Lines.SetPosition(at + 1, center + tilt * new Vector3(r * Mathf.Cos(a), 0, r * Mathf.Sin(a)));
                }
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
                // Альфа = 1: аддитив HDRP умножает цвет на альфу — затухание только в RGB.
                var c = PlumeGradientColor(i / (n - 1f));
                c.a = 1;
                gradient.SetPixel(0, i, c);
            }
            gradient.Apply(false, true);
            return gradient;
        }

        /// <summary>Цвет и яркость струи на доле длины t (0 — срез). Один источник для текстуры и для силы света.</summary>
        static Color PlumeGradientColor(float t)
        {
            var c = Color.Lerp(Color.Lerp(new Color(1f, 0.95f, 0.85f), new Color(1f, 0.7f, 0.3f), Mathf.Clamp01(t * 3)),
                               new Color(0.9f, 0.3f, 0.1f), Mathf.Clamp01(t * 1.5f - 0.3f));
            // Срез без резкой кромки, хвост гаснет в ноль.
            float fade = Mathf.Clamp01(t * 12) * Mathf.Exp(-3.5f * t) * (1 - t);
            return c * fade;
        }

        /// <summary>Светящаяся площадь мешей факела на единицу длины и радиуса среза (∫ 2·r(t)·яркость(t) dt) и центр
        /// свечения (доля длины). Считается один раз из профиля мешей и градиента — правка любого из них меняет и свет.</summary>
        static bool shapeReady;
        static float coreShape, coreCenter, glowShape, glowCenter;
        static void PlumeShape()
        {
            if (shapeReady) return;
            const int n = 128;
            float ca = 0, cm = 0, ga = 0, gm = 0;
            for (int i = 0; i < n; i++)
            {
                float t = (i + 0.5f) / n;
                var c = PlumeGradientColor(t);
                float lum = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / n;
                float rc = 2 * (1 + CoreGrow * Mathf.Pow(t, CorePower)) * lum;
                float rg = 2 * (1 + GlowGrow * Mathf.Pow(t, GlowPower)) * lum;
                ca += rc; cm += rc * t;
                ga += rg; gm += rg * t;
            }
            coreShape = ca; coreCenter = cm / ca;
            glowShape = ga; glowCenter = gm / ga;
            shapeReady = true;
        }

        /// <summary>
        /// Свет факела (§9.5) — как от протяжённого источника: сила света = яркость × светящаяся площадь сбоку,
        /// точка — в центре свечения струи. Освещённая поверхность у источника яркости L не ярче ≈ ρ·L, поэтому
        /// борт рядом с факелом не может пересветить сам факел. Было постоянных 2·10⁶ кд в 3 радиусах под срезом
        /// на любом двигателе: «Сервейор» ночью на Луне получал на опорах ≈ 2·10⁵ лк — корпус в сотни раз ярче
        /// белого при EV 7,3, а клубы пыли за 20–40 м светились оранжевыми пятнами (замер 05.10.2026).
        /// Пара: CoreNits/GlowNits, PlumeGradientColor, профиль мешей (CoreGrow…GlowPower), GlowLength.
        /// </summary>
        static void PlumeLight(Light light, float coreNits, float glowNits, float coreR, float glowR, float len, float flicker)
        {
            PlumeShape();
            float core = coreNits * coreR * len * coreShape;
            float glowLen = len * GlowLength;
            float glow = glowNits * glowR * glowLen * glowShape;
            light.intensity = PlumeWalls * (core + glow) * (1 + (flicker - 1) * PlumeLightFlicker);
            float center = (core * len * coreCenter + glow * glowLen * glowCenter) / Mathf.Max(core + glow, 1e-6f);
            light.transform.localPosition = new Vector3(0, -center, 0);
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
            // Материал слота — по отделке его цвета (FinishOf); слоты без цвета в палитре — окрашенный металл.
            if (mesh.subMeshCount > 1)
            {
                var mats = new Material[mesh.subMeshCount];
                for (int i = 0; i < mats.Length; i++)
                    mats[i] = palette.Length == 1 ? FinishMaterial(FinishOf(palette[0]))
                            : i < palette.Length ? (palette[i] == GlassColor ? GlassMaterial() : FinishMaterial(FinishOf(palette[i]))) : bodyMat;
                mr.sharedMaterials = mats;
            }
            else mr.sharedMaterial = palette.Length > 0 ? FinishMaterial(FinishOf(palette[0])) : bodyMat;
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
                mpb.SetColor("_BaseColor", SlotColor(palette[i]));
                if (emissive.a > 0) mpb.SetColor("_EmissiveColor", emissive);
                if (palette.Length == 1) mr.SetPropertyBlock(mpb);
                else mr.SetPropertyBlock(mpb, i);
            }
        }

        /// <summary>Самый яркий видимый факел за кадр, нит, без множителя BrightnessSettings.Plume (реальная яркость —
        /// это значение × Plume) — SkyController поднимает по нему нижний предел EV
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
            if (Vessel != FlightView.Main && !FlightView.Near(pos, DrawDistance)) show = false;
            SetVisible(show);
            if (!show) return;

            if (Signature() != builtSignature) Rebuild();
            Vessel.MassProperties(out _, out double com, out double vesselLen, out double vesselR);
            Vessel.Layout(baseHeight);
            transform.SetPositionAndRotation(pos, FloatingOrigin.ToQuaternion(Vessel.Attitude));
            // Корпус проседает вместе с подвеской, стопы опор остаются на месте касания: опора «сжимается».
            var squeeze = transform.up * (float)-Vessel.Suspension;
            foreach (var h in legs)
            {
                if (h.T == null) continue;
                var q = Quaternion.AngleAxis(h.Stow * (1 - (float)Vessel.Deployed[h.Section]), h.Axis);
                h.T.localRotation = q * h.Base;
                h.T.localPosition = h.Pivot + q * (h.Rest - h.Pivot) + (h.Squeeze ? h.T.parent.InverseTransformVector(squeeze) : Vector3.zero);
            }

            // Колёса: путь борта / радиус. Плюс вокруг +X в осях Unity гонит верх колеса к носу (+Z) — качение вперёд.
            foreach (var w in wheels)
            {
                if (w.T == null) continue;
                double path = w.Left ? Vessel.WheelPathLeft : Vessel.WheelPathRight;
                float deg = (float)(path / FlightPhysics.RoverWheelRadius * Mathf.Rad2Deg % 360);
                w.T.localRotation = Quaternion.Euler(deg, 0, 0);
            }

            float pressure = (float)(Vessel.StaticPressure / SeaLevelPressure);
            // Набегающий поток — скорость относительно вращающейся атмосферы; у стоящего борта — местная вертикаль.
            var air = Vessel.Velocity - Vessel.Body.SurfaceVelocity(Vessel.Position);
            var airflow = air.magnitude > 1 ? FloatingOrigin.DirToUnity(air).normalized
                                            : -FloatingOrigin.DirToUnity(Vessel.Position).normalized;
            UpdatePlasma(airflow, com, vesselLen, vesselR);
            UpdateAeroFx(airflow, plasma != null && plasma.gameObject.activeSelf);
            UpdateControls(airflow);
            UpdateSpaceX();
            foreach (var p in parts)
            {
                // После перестроения (§6.6) ЛМ стоит на КСМ вверх ногами: низ секции — сверху её места в пакете.
                bool flip = Vessel.Flipped[p.Index];
                float lift = flip ? (float)Vessel.Design.Sections[p.Index].Length : 0;
                p.Tr.localPosition = p.Radial + new Vector3(0, (float)(baseHeight[p.Index] - com) + lift, 0);
                p.Tr.localRotation = flip ? FlipRotation * p.Yaw : p.Yaw;
                if (p.Chute != null) UpdateChute(p, airflow);
                if (p.Plume == null) continue;
                bool on = Vessel.Running[p.Index];
                float thr = on ? (float)Vessel.EffectiveThrottle(p.Index) : 0;
                p.Throttle = thr;
                bool burning = thr > 0.01f;
                p.Plume.gameObject.SetActive(burning);
                p.Glow.gameObject.SetActive(burning);
                p.PlumeLight.gameObject.SetActive(burning);
                if (!burning) { if (p.Mirrors != null) SyncMirrors(p, false); continue; }
                // В вакууме струя раздувается и удлиняется; яркость/длина — от дросселя (§9.5).
                float vac = 1 - Mathf.Clamp01(pressure);
                float spread = 1 + vac * PlumeVacuumWidth;
                float len = p.PlumeRadius * PlumeLengthSL * (0.4f + 0.6f * thr);
                float flicker = Flicker(PlumeFlicker, p.Index);
                float coreR = p.PlumeRadius * (1 + vac * PlumeVacuumWidth * CoreSpread);
                p.Plume.localScale = new Vector3(coreR, len * flicker, coreR);
                p.Glow.localScale = new Vector3(p.PlumeRadius * spread, len * GlowLength * flicker, p.PlumeRadius * spread);
                // Яркость на единицу площади: раздувшаяся струя тусклее (§9.5).
                float bright = thr * Mathf.Lerp(1, VacuumBrightness, vac) * BrightnessSettings.Plume;
                float coreNits = CoreNits * bright, glowNits = GlowNits * bright / spread;
                // Экспозиции — фактическая яркость ядра, с вакуумным множителем и без дрожания. Раньше шло CoreNits·thr:
                // в вакууме экспозиция ставилась под ядро втрое ярче нарисованного, и всё вокруг было на 1,7 EV темнее.
                // Без ползунка «Факел»: экспозиция под него не подстраивается, и он убавляет факел относительно сцены.
                ReportPlume(coreNits / BrightnessSettings.Plume);
                SetPlumeColor(p.CoreR, coreNits * flicker);
                SetPlumeColor(p.GlowR, glowNits * flicker);
                PlumeLight(p.PlumeLight, coreNits, glowNits, coreR, p.PlumeRadius * spread, len, flicker);
                if (p.Mirrors != null) SyncMirrors(p, true);
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
                if (p.Plume != null && !p.NoSmoke && p.Throttle > 0.01f && (best == null || p.Tr.localPosition.y < best.Tr.localPosition.y))
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

        /// <summary>Шаг колец по высоте у тел Р-7, м: конус бокового и «талия» блока А гладкие на 30–40 колец.</summary>
        const float R7RingStep = 0.6f;

        /// <summary>
        /// Блок А (booster = false) или боковой Р-7 (true) высотой h от y = 0 по профилю VesselPresets.R7*RadiusAt.
        /// Сечения бокового сдвинуты к оси пакета (−X) на R7BoosterLean: внутренняя образующая идёт вдоль «талии»
        /// блока А, носок ложится на него — косой конус, а не осесимметричный.
        /// </summary>
        public static Mesh R7Body(float h, bool booster, int seg = 32)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int rings = Mathf.Max(2, Mathf.CeilToInt(h / R7RingStep) + 1);
            float r0 = 0, r1 = 0, x1 = 0;
            for (int k = 0; k < rings; k++)
            {
                float y = h * k / (rings - 1);
                float r = (float)(booster ? VesselPresets.R7BoosterRadiusAt(y) : VesselPresets.R7CoreRadiusAt(y));
                float x = booster ? (float)VesselPresets.R7BoosterLean(y) : 0;
                if (k == 0) r0 = r;
                r1 = r; x1 = x;
                for (int i = 0; i <= seg; i++)
                {
                    float a = 2 * Mathf.PI * i / seg;
                    verts.Add(new Vector3(x + Mathf.Cos(a) * r, y, Mathf.Sin(a) * r));
                }
            }
            for (int k = 0; k + 1 < rings; k++)
                for (int i = 0; i < seg; i++)
                {
                    int a = k * (seg + 1) + i;
                    Quad(tris, a, a + 1, a + seg + 2, a + seg + 1, true);
                }
            Cap(verts, tris, r0, 0, seg, false);
            Cap(verts, tris, r1, h, seg, true, x1);
            return Finish(verts, tris, booster ? "R7 Booster" : "R7 Block A");
        }

        /// <summary>Единичный куб с центром в нуле, грани с раздельными вершинами — резкие рёбра.</summary>
        public static Mesh Box()
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int axis = 0; axis < 3; axis++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    var n = Vector3.zero; n[axis] = sign;
                    Vector3 u = Vector3.zero, w = Vector3.zero;
                    u[(axis + 1) % 3] = 0.5f; w[(axis + 2) % 3] = 0.5f;
                    if (sign < 0) u = -u;
                    var c = n * 0.5f;
                    int b = verts.Count;
                    verts.Add(c - u - w); verts.Add(c + u - w); verts.Add(c + u + w); verts.Add(c - u + w);
                    tris.Add(b); tris.Add(b + 1); tris.Add(b + 2); tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
                }
            return Finish(verts, tris, "Box");
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

        static void Cap(List<Vector3> v, List<int> t, float r, float y, int seg, bool up, float cx = 0)
        {
            if (r <= 0) return;
            int c = v.Count;
            v.Add(new Vector3(cx, y, 0));
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                v.Add(new Vector3(cx + Mathf.Cos(a) * r, y, Mathf.Sin(a) * r));
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
