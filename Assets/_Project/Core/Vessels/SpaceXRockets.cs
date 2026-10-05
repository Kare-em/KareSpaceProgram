using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Многоразовые системы SpaceX (§6.9): возврат I ступени Falcon 9 на баржу и Starship / Super Heavy.
    /// </summary>
    public static class SpaceXRockets
    {
        /// <summary>
        /// Азотная РСУ Falcon 9 у верха ступени, Н·м. Пара: инерция пустой ступени ≈ 9·10⁶ кг·м² → ε ≈ 0,017 рад/с²,
        /// переворот на 180° «разгон — торможение» ≈ 2·√(π/ε) ≈ 27 с (Demo-2: ≈ 30 с).
        /// </summary>
        public const double Falcon9RcsTorque = 1.5e5;

        /// <summary>
        /// Запас топлива на возврат на баржу, кг (≈ 35 т, по оценкам для ASDS-посадок 30–40 т). Пара: входной импульс
        /// Ocisly.EntryDv тремя Merlin и посадка одним (LandingDv) — по Циолковскому от 25,6 т сухих ≈ 22 т + запас.
        /// </summary>
        public const double Falcon9Reserve = 35000;

        /// <summary>
        /// Баржа «Of Course I Still Love You» в Атлантике (Demo-2: ≈ 540 км вниз по трассе от LC-39A). Точка — естественное
        /// падение ступени после номинального входного импульса в этой модели (тест booster печатает прогноз): тогда
        /// разворотный импульс не нужен, как и в настоящей миссии. Палуба ≈ 52 × 91 м, над водой ≈ 3 м.
        /// </summary>
        public static RecoveryDef Ocisly() => new RecoveryDef
        {
            StageName = "Falcon 9 B1058 — I ступень",
            TargetName = "OCISLY",
            TargetLat = OcislyLat, TargetLon = OcislyLon,
            AtSea = true, DeckHeight = 3, DeckRadius = 45,
            Reserve = Falcon9Reserve,
            BurnEngines = 3, LandingEngines = 1,
            // Пара: входной импульс гасит ≈ 1000 м/с (2,25 → 1,2 км/с): медленнее — на 15–25 км тепло выше MaxHeatFlux и
            // напор > 200 кПа, где хвостом вперёд ломает уже 1° (QAlphaLimit). Всего ≈ 17 т из Falcon9Reserve.
            EntryAltitude = 70000, EntryDv = 1000,
            GridFinArea = 4 * 1.5,
            LandingDv = 600,
        };

        public const double OcislyLat = 32.424, OcislyLon = -76.018;

        // ---------------------------------------------------------------- Starship / Super Heavy (IFT-5)

        /// <summary>Стартовый стол OLP-A на Starbase (Бока-Чика, Техас); башня ловли — рядом с ним.</summary>
        public const double StarbaseLat = 25.9969, StarbaseLon = -97.1573;

        /// <summary>
        /// Запас Super Heavy на возврат, кг. Пара: разворотный импульс 13 Raptor к башне (≈ 2,5 км/с: модель не дросселирует
        /// на max Q, и MECO выходит раньше и быстрее настоящего — 2,2 км/с против 1,5) и посадка (StarbaseCatch.LandingDv)
        /// от ≈ 210 т сухих. Замеры (тест starship): 340 т (≈ 10 %, как у настоящего) — удар 68 м/с, 450 т — удар 97 м/с;
        /// 600 т — разворот 459 т, входной 57 т, посадка 64 т, у башни 2,5 м/с с остатком 15 т.
        /// </summary>
        public const double SuperHeavyReserve = 600000;

        /// <summary>
        /// Момент управления Super Heavy вне напора, Н·м: переворот после горячего разделения (на деле — газовые рули и
        /// качание центральных Raptor). Пара: инерция ≈ 550 т × 71² / 12 ≈ 2,3·10⁸ кг·м² → переворот на 180° ≈ 30 с.
        /// </summary>
        public const double SuperHeavyRcsTorque = 3e6;

        /// <summary>
        /// Момент управления корабля, Н·м: РСУ и закрылки вместе (закрылки только вид, их момент — здесь). Пара: инерция
        /// пустого корабля ≈ 130 т × 50² / 12 ≈ 2,7·10⁷ кг·м² → поворот на 90° «брюхом» ≈ 10 с.
        /// </summary>
        public const double StarshipRcsTorque = 3e6;

        /// <summary>
        /// Ловля Super Heavy башней (IFT-5, 13.10.2024 — первая ловля «палочками» Mechazilla). Упрощение: мягкая посадка в
        /// круге DeckRadius у башни (RecoveryDef.TowerCatch). Входной импульс — упрощение (у настоящего его нет): сопротивление
        /// модели «хвостом вперёд» мало, без него на 12 км ≈ 1250 м/с и посадочному не хватает высоты (удар 85 м/с).
        /// </summary>
        public static RecoveryDef StarbaseCatch() => new RecoveryDef
        {
            StageName = "Super Heavy B12",
            TargetName = "башня Starbase",
            TargetLat = StarbaseLat, TargetLon = StarbaseLon,
            AtSea = false, DeckHeight = 0, DeckRadius = 50, TowerCatch = true,
            Reserve = SuperHeavyReserve,
            BurnEngines = 13, LandingEngines = 3,
            EntryAltitude = 70000, EntryDv = 600,
            // 4 решётчатых руля ≈ 3 × 2,7 м (у Super Heavy — несложенные, на верху бака).
            GridFinArea = 4 * 8,
            LandingDv = 500,
        };

        /// <summary>
        /// Приводнение корабля в Индийском океане (IFT-5: ≈ 65 мин после старта, к северо-западу от Австралии). Точка —
        /// естественное падение после SECO на перигее ShipPerigee (тест starship печатает её), как и баржа Falcon 9; в модели
        /// это ≈ 50 мин и 68° в. д. (к востоку от Маврикия): MECO и SECO раньше настоящих, ускорение не дросселируется.
        /// </summary>
        public static RecoveryDef IndianOcean() => new RecoveryDef
        {
            StageName = "Starship S30",
            TargetName = "Индийский океан",
            TargetLat = ShipSplashLat, TargetLon = ShipSplashLon,
            AtSea = true, DeckHeight = 0,
            BellyFlop = true, FlopAltitude = 2500,
            // Переворот — на 3 Raptor, касание — на одном: 3 × 40 % тяги (MinThrottle) больше веса пустого корабля.
            BurnEngines = 3, LandingEngines = 1,
            // Пилот входит в аэроучасток (брюхом) ниже этой высоты — сразу у верха атмосферы.
            EntryAltitude = 120000, EntryDv = 0,
            LandingDv = 300,
        };

        public const double ShipSplashLat = -25.93, ShipSplashLon = 68.42;

        /// <summary>
        /// Перигей трансатмосферной траектории корабля на SECO, м (IFT: «почти орбита», перигей ≈ 50 км). Ниже — вход
        /// раньше (50 км — у Мадагаскара, 49° в. д.), выше — дальше на восток и ближе к второму витку. Пара: ShipSplashLat/Lon.
        /// </summary>
        public const double ShipPerigee = 70e3;

        /// <summary>
        /// Закрылки корабля (§4.6): 2 носовых и 2 кормовых по бокам корпуса, шарнир вдоль оси, хорда — наружу.
        /// Только вид (VesselView.Controls): площадь крыла символическая, момент закрылков — в StarshipRcsTorque.
        /// Тангаж — носовые против кормовых, крен — левые против правых. Оси секции: +Y нос, +X брюхо, Z размах.
        /// </summary>
        public static WingDef StarshipFlaps(double length, double radius)
        {
            var w = new WingDef
            {
                Name = "Закрылки", Area = 1, Span = 2 * radius, Height = length * 0.5, ControlFraction = 0, Cd0 = 0,
                ControlRateDeg = 20, Surfaces = new List<ControlSurface>(),
            };
            for (int side = -1; side <= 1; side += 2)
            {
                double z = side * radius * 0.97;
                // Носовые: на оживале, корень у 0,78 длины, конец у 0,90 — шарнир по обводу носа (радиус там ≈ 0,88 R и
                // ≈ 0,55 R: пара с профилем носа b_starship в Tools/blender/spacex_parts.py), хорда сужается к носу.
                w.Surfaces.Add(new ControlSurface
                {
                    Name = side > 0 ? "Носовой закрылок П" : "Носовой закрылок Л",
                    HingeA = new Vector3d(0.6, length * 0.78, side * radius * 0.84), HingeB = new Vector3d(0.8, length * 0.90, side * radius * 0.51),
                    Aft = new Vector3d(0, 0, side), Up = new Vector3d(-1, 0, 0),
                    ChordA = 3.2, ChordB = 1.8, Thickness = 0.35,
                    MixPitch = 1, MixRoll = side, MaxDeg = 30, RateDeg = 20, DarkBelly = true,
                });
                // Кормовые: крупнее, от низа до 0,26 длины.
                w.Surfaces.Add(new ControlSurface
                {
                    Name = side > 0 ? "Кормовой закрылок П" : "Кормовой закрылок Л",
                    HingeA = new Vector3d(0.8, length * 0.03, z), HingeB = new Vector3d(0.8, length * 0.26, z),
                    Aft = new Vector3d(0, 0, side), Up = new Vector3d(-1, 0, 0),
                    ChordA = 4.8, ChordB = 4.0, Thickness = 0.4,
                    MixPitch = -1, MixRoll = side, MaxDeg = 30, RateDeg = 20, DarkBelly = true,
                });
            }
            return w;
        }
    }

    public static partial class VesselPresets
    {
        /// <summary>Сопоставление id → проект для SpaceX. Пара: VesselPresets.ById (default) и MissionCatalog.AddSpaceX.</summary>
        public static VesselDesign SpaceXById(string id)
        {
            switch (id)
            {
                case "ift5": return StarshipIft5();
                default: return null;
            }
        }

        /// <summary>
        /// Starship IFT-5 (13.10.2024): Super Heavy B12 + Starship S30. Горячее разделение: кольцо — часть ускорителя (масса
        /// и вид), корабль зажигается в момент отделения (igniteNext). Ускоритель уходит к башне, корабль — по
        /// трансатмосферной траектории в Индийский океан, вход «брюхом», переворот и приводнение на 3 Raptor.
        /// Массы и тяги — по открытым оценкам (Raptor 2: 2,26 МН у Земли, 2,45 МН в вакууме).
        /// </summary>
        public static VesselDesign StarshipIft5()
        {
            var d = new VesselDesign { Name = "Starship IFT-5" };
            d.Sections.Add(new SectionDef
            {
                // Сухая ≈ 200 т + кольцо горячего разделения ≈ 10 т. Топливо 3400 т (CH₄/O₂).
                Name = "Super Heavy B12", Kind = SectionKind.Stage, DryMass = 210000, Propellant = 3400000,
                Engine = new EngineDef { Name = "Raptor 2", ThrustVac = 2.45e6, ThrustSL = 2.26e6, IspVac = 350, MinThrottle = 0.4, GimbalDeg = 15, Ignitions = 4 },
                EngineCount = 33, Length = 71 + 1.8, Diameter = 9, MaxHeatFlux = 4e5, Model = SectionModel.SuperHeavy,
                RcsTorque = SpaceXRockets.SuperHeavyRcsTorque, Recovery = SpaceXRockets.StarbaseCatch(),
            });
            const double shipLength = 50.3, shipRadius = 4.5;
            var ship = new SectionDef
            {
                // Двигатель — среднее 3 Raptor (у Земли) и 3 RVac: на посадке (LandingEngines 3) работает тяга «у Земли».
                // MaxHeatFlux — плитки ТЗП на брюхе. Топливо подобрано под остаток после SECO на посадку (настоящие S30 —
                // 1500 т, Isp RVac 380 с). Замер: 1200 т — после SECO 124 т, 900 т — 88 т (из них на посадку ушло 45 т).
                Name = "Starship S30", Kind = SectionKind.Stage, DryMass = 120000, Propellant = 900000,
                Engine = new EngineDef { Name = "Raptor 2 + RVac", ThrustVac = 2.5e6, ThrustSL = 2.2e6, IspVac = 365, MinThrottle = 0.4, GimbalDeg = 15, Ignitions = 4 },
                EngineCount = 6, Length = shipLength, Diameter = 2 * shipRadius, MaxHeatFlux = 1.5e6, Model = SectionModel.Starship,
                RcsTorque = SpaceXRockets.StarshipRcsTorque, Recovery = SpaceXRockets.IndianOcean(),
            };
            ship.Wings = new List<WingDef> { SpaceXRockets.StarshipFlaps(shipLength, shipRadius) };
            d.Sections.Add(ship);
            d.Sequence.Add(new StageAction(StageActionType.Ignite, 0));
            d.Sequence.Add(new StageAction(StageActionType.Separate, 0, igniteNext: true)); // горячее разделение
            return d;
        }
    }
}
