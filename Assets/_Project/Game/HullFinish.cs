namespace Kare.Space.Game
{
    /// <summary>
    /// Отделка обшивки (GDD §9.5): набор карт (альбедо, нормаль, Mask) и материал VesselLit_&lt;имя&gt;.mat.
    /// Карты — Tools/gen-finish-textures.py → Textures/Hull/&lt;имя&gt;, материалы — FlightSceneBuilder.HullFinishes,
    /// выбор по цвету слота — VesselView.FinishOf. Порядок = индекс в GameBootstrap.FinishMaterials (Painted = 0 —
    /// это сам VesselLit.mat).
    /// </summary>
    public enum HullFinish
    {
        /// <summary>Окрашенный алюминий с панелями и заклёпками; цвет — палитра ступени.</summary>
        Painted,
        /// <summary>Голый алюминий с гофром/стрингерами (рамы, баки, отсеки); оттенок — палитра.</summary>
        Stringer,
        /// <summary>Нержавейка/полированный металл (Атлас, Центавр, сопла); оттенок — палитра.</summary>
        Steel,
        /// <summary>Оранжевая теплоизоляционная пена (внешний бак Шаттла); цвет в текстуре.</summary>
        Foam,
        /// <summary>Чёрные плитки HRSI (низ Шаттла/Бурана); цвет в текстуре.</summary>
        TilesBlack,
        /// <summary>Белые плитки (верх Бурана); цвет в текстуре.</summary>
        TilesWhite,
        /// <summary>Обугленная абляционная теплозащита СА; цвет в текстуре.</summary>
        Ablative,
        /// <summary>Золотая ЭВТИ-фольга (посадочные ступени, зонды); цвет в текстуре.</summary>
        FoilGold,
        /// <summary>Серебристая ЭВТИ-фольга; цвет в текстуре.</summary>
        FoilSilver,
    }
}
