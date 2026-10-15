namespace Kare.Space.Core
{
    /// <summary>Загрузка сохранения (§10.3): то, что граф полей не переносит, — подписки на события бортов.</summary>
    public sealed partial class Universe
    {
        /// <summary>
        /// После заполнения полей из файла (SaveGame.Load). Подписка Vessel.Event → OnVesselEvent живёт в поле-событии,
        /// которое сохранение пропускает: без неё вселенная не узнаёт о разрушении, касании, стыковке бортов.
        /// Отложенные ступени (Deferred) из Vessels выведены, но подписку держат — ResumeRecovery её не повторяет.
        /// </summary>
        internal void AfterLoad()
        {
            foreach (var v in Vessels) v.Event += OnVesselEvent;
            foreach (var d in Deferred) if (d?.Pilot?.Vessel != null && !Vessels.Contains(d.Pilot.Vessel)) d.Pilot.Vessel.Event += OnVesselEvent;
            System.Update(Time);
        }
    }
}
