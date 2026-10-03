using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kare.Space.Game
{
    /// <summary>
    /// Точка входа сцены Flight (GDD §1.3, §3): создаёт Universe по миссии, каждый кадр шагает симуляцию
    /// с текущим ускорением и ведёт трекер миссии. Виды бортов (VesselView) заводит по списку u.Vessels.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class GameBootstrap : MonoBehaviour
    {
        public static GameBootstrap Instance { get; private set; }
        public static Universe U => Instance != null ? Instance.universe : null;

        [Tooltip("Миссия из MissionCatalog: karman, sputnik, mechta, vympel, farside, vostok, luna9. " +
                 "По умолчанию «Восток» — старт днём по Байконуру, видно небо.")]
        public string MissionId = "vostok";

        /// <summary>Миссия, выбранная в меню Esc: переживает перезагрузку сцены и перекрывает MissionId.</summary>
        public static string NextMissionId;

        public Camera Camera;
        public Light Sun;
        public Volume Volume;
        [Tooltip("Базовый материал HDRP/Lit для бортов; цвет секций — через MaterialPropertyBlock.")]
        public Material VesselMaterial;
        [Tooltip("Эмиссионный материал факела.")]
        public Material PlumeMaterial;
        [Tooltip("Дым шлейфа и облака у стола: HDRP/Lit прозрачный (§9.5).")]
        public Material SmokeMaterial;
        [Tooltip("Бетон стартового стола (Textures/Ground/Concrete).")]
        public Texture2D PadTexture;
        [Header("Лоу-поли детали (Tools/blender → Models/*.fbx); без них — процедурные меши")]
        [Tooltip("СА «Восток»: начало у днища, Ø2,3 м.")]
        public Mesh CapsuleMesh;
        [Tooltip("Блок РД-107 из 4 камер: начало у верха, сопла вниз, ширина 1,96 м.")]
        public Mesh EngineMesh;
        [Tooltip("Посадочная опора: начало — шарнир, стопа на 1,37 м ниже и 1,09 м наружу (−X).")]
        public Mesh LegMesh;
        [Tooltip("Ферма стола: начало у шарнира, длина 8,2 м по +Y.")]
        public Mesh TrussMesh;
        [Tooltip("ПС-1: начало в центре шара Ø0,58 м, антенны вниз (−Y) на 2,63 м.")]
        public Mesh SputnikMesh;
        [Tooltip("Приборный отсек «Востока» с ТДУ: начало у верха, Ø2,44 м, низ сопла на −2,25 м.")]
        public Mesh VostokServiceMesh;
        [Tooltip("Станция Е-6 («Луна-9») с КТДУ: начало у среза сопла, высота 2,7 м, Ø1,5 м.")]
        public Mesh Luna9Mesh;
        [Tooltip("Одиночный двигатель верхней ступени (РД-0110): начало у верха, Ø2,2 м, высота 1,6 м.")]
        public Mesh UpperEngineMesh;
        [Tooltip("Ферма горячего разделения: начало у низа, Ø2,66 м, высота 1,2 м.")]
        public Mesh InterstageMesh;
        [Tooltip("Створка обтекателя: начало у низа, половина в −X, Ø5,2 м, высота 13 м.")]
        public Mesh FairingHalfMesh;
        [Tooltip("Хвостовой отсек с 4 стабилизаторами: начало у низа, корпус Ø1,0 м, размах Ø1,5 м, высота 1,2 м.")]
        public Mesh FinsMesh;
        [Tooltip("Аппараты в натуральную величину (Луноход, LM, «Аполлон», «Меркурий»…): метры, ось +Y — нос.")]
        public CraftMesh[] CraftMeshes;

        /// <summary>Модель аппарата для детали секции; null — нет (вид берёт процедурный меш).</summary>
        [System.Serializable]
        public struct CraftMesh { public SectionModel Model; public Mesh Mesh; }

        public Mesh CraftMeshFor(SectionModel model)
        {
            if (CraftMeshes == null || model == SectionModel.None) return null;
            foreach (var c in CraftMeshes) if (c.Model == model) return c.Mesh;
            return null;
        }

        [Header("Правила (GDD §4.7); в игре — меню Esc")]
        [Tooltip("Разрушение от поперечной аэродинамической нагрузки q·sin α.")]
        public bool AeroBreakup;
        [Tooltip("Разрушение от перегрева обшивки.")]
        public bool HeatDamage;
        [Tooltip("Предел перегрузки в целях миссий (спуск «Востока» ≤ 9 g).")]
        public bool GLoadLimit;
        [Tooltip("Подсказка по углу тангажа при ручном выведении.")]
        public bool AscentTutor = true;
        [Tooltip("Чит для тестов: баки активного борта каждый кадр полны.")]
        public bool InfiniteFuel;
        [Tooltip("Карта суши Земли (Tools/bake-earth-land.py, §2.8). Без неё материки процедурные.")]
        public TextAsset EarthLand;

        Universe universe;
        public MissionTracker Tracker { get; private set; }
        public MissionDef Mission { get; private set; }

        /// <summary>Последние сообщения для HUD (Universe.Log хранит и время).</summary>
        public readonly List<string> Messages = new List<string>();

        readonly Dictionary<Vessel, VesselView> views = new Dictionary<Vessel, VesselView>();
        readonly List<Vessel> gone = new List<Vessel>();

        void Awake()
        {
            Instance = this;
            Mission = MissionCatalog.Get(NextMissionId ?? MissionId) ?? MissionCatalog.Get("sputnik");
            // Карту читаем один раз: статическое поле переживает перезагрузку сцены (выбор миссии в Esc).
            if (EarthLand != null && SolarSystem.EarthLand == null) SolarSystem.EarthLand = new LandMap(EarthLand.bytes);
            universe = MissionTracker.CreateUniverse(Mission, SolarSystem.CreateReal());
            Tracker = new MissionTracker(Mission);
            universe.Message += OnMessage;
            Tracker.Changed += OnMessage;
            OnMessage($"Миссия «{Mission.Title}»: {Mission.Brief}");
            gameObject.AddComponent<PauseMenu>();
            FloatingOrigin.Refresh();
            if (universe.Active?.Site != null)
                new GameObject("Launch Pad").AddComponent<LaunchPadView>().Init(universe.Active, PadTexture, VesselMaterial, TrussMesh);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void OnMessage(string text)
        {
            Debug.Log(text);
            Messages.Add(text);
            if (Messages.Count > 8) Messages.RemoveAt(0);
        }

        void Update()
        {
            FlightPhysics.AeroBreakup = AeroBreakup;
            FlightPhysics.HeatDamage = HeatDamage;
            FlightPhysics.GLoadLimit = GLoadLimit;
            // Advance сам режет realDt до 0,1 с и выбирает физику/рельсы по WarpIndex. В меню — пауза.
            if (InfiniteFuel && universe.Active != null && universe.Active.Alive) universe.Active.Refuel();
            if (!PauseMenu.IsOpen) universe.Advance(Time.deltaTime);
            Tracker.Update(universe);
            FloatingOrigin.Refresh();
            SyncViews();
        }

        void SyncViews()
        {
            foreach (var v in universe.Vessels)
            {
                if (views.ContainsKey(v)) continue;
                var go = new GameObject(v.IsDebris ? $"Debris {v.Name}" : $"Vessel {v.Name}");
                var view = go.AddComponent<VesselView>();
                view.Init(v, VesselMaterial, PlumeMaterial);
                if (!v.IsDebris && SmokeMaterial != null)
                    new GameObject($"Trail {v.Name}").AddComponent<ExhaustTrail>().Init(view, SmokeMaterial);
                views.Add(v, view);
            }
            gone.Clear();
            foreach (var kv in views)
                if (!universe.Vessels.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var v in gone)
            {
                if (views[v] != null) Destroy(views[v].gameObject);
                views.Remove(v);
            }
        }
    }
}
