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

        public Camera Camera;
        public Light Sun;
        public Volume Volume;
        [Tooltip("Базовый материал HDRP/Lit для бортов; цвет секций — через MaterialPropertyBlock.")]
        public Material VesselMaterial;
        [Tooltip("Эмиссионный материал факела.")]
        public Material PlumeMaterial;
        [Tooltip("Бетон стартового стола (Textures/Ground/Concrete).")]
        public Texture2D PadTexture;

        [Header("Правила (GDD §4.7); в игре — меню Esc")]
        [Tooltip("Разрушение от поперечной аэродинамической нагрузки q·sin α.")]
        public bool AeroBreakup;
        [Tooltip("Разрушение от перегрева обшивки.")]
        public bool HeatDamage;
        [Tooltip("Предел перегрузки в целях миссий (спуск «Востока» ≤ 9 g).")]
        public bool GLoadLimit;
        [Tooltip("Подсказка по углу тангажа при ручном выведении.")]
        public bool AscentTutor = true;

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
            Mission = MissionCatalog.Get(MissionId) ?? MissionCatalog.Get("sputnik");
            universe = MissionTracker.CreateUniverse(Mission, SolarSystem.CreateReal());
            Tracker = new MissionTracker(Mission);
            universe.Message += OnMessage;
            Tracker.Changed += OnMessage;
            OnMessage($"Миссия «{Mission.Title}»: {Mission.Brief}");
            gameObject.AddComponent<PauseMenu>();
            FloatingOrigin.Refresh();
            if (universe.Active?.Site != null)
                new GameObject("Launch Pad").AddComponent<LaunchPadView>().Init(universe.Active, PadTexture, VesselMaterial);
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
