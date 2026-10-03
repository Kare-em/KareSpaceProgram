using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Детализация планет и карты (меню Esc, GDD §9.4, §9.6): разрешение процедурных текстур тел и облаков, сетка
    /// сфер, гладкость линий орбит. Уровень лежит в PlayerPrefs и читается лениво, как BrightnessSettings.
    /// Текстуры строятся процедурно на старте, поэтому смена уровня — перестройка тел (BodyRenderer.ApplyDetail),
    /// а не переключатель качества Unity. Цена уровня — время перестройки и память (RGBA32 с мипами ≈ 5,3 байта на тексель).
    /// </summary>
    public static class DetailSettings
    {
        public static readonly string[] Names = { "Низкая", "Средняя", "Высокая", "Ультра" };
        /// <summary>Уровень по умолчанию — «Высокая»: «Средняя» — прежние константы (Земля 2048, тексель 20 км),
        /// на карте и с низкой орбиты облака и берег были мыльными.</summary>
        const int DefaultLevel = 2;
        const string Key = "kare.detail.level";

        // Таблицы по уровням. Земля и облака на Ультра — 8192 (тексель 5 км): ≈ 170 МБ на текстуру с мипами, и у Земли
        // их две (цвет + маска блика). Сетка: стрела прогиба грани R·Δθ²/8 у Земли при 384 ≈ 210 м, при 768 ≈ 53 м —
        // сфера опускается под патч на полторы стрелы (BodyRenderer.BuildLod), меньше прогиб — меньше ступенька у края патча.
        static readonly int[] EarthTex = { 1024, 2048, 4096, 8192 };
        static readonly int[] DetailedTex = { 512, 1024, 2048, 4096 };
        static readonly int[] PlainTex = { 128, 256, 512, 1024 };
        static readonly int[] CloudTex = { 1024, 2048, 4096, 8192 };
        static readonly int[] DetailedSeg = { 256, 384, 512, 768 };
        static readonly int[] PlainSeg = { 64, 96, 192, 256 };
        /// <summary>Сетка облаков. Пара: стрела прогиба при 128 ≈ 1,9 км — много меньше высоты слоя CloudAltitude (8 км).</summary>
        static readonly int[] CloudSeg = { 128, 256, 384, 512 };
        /// <summary>Точек на линию орбиты карты (§9.6).</summary>
        static readonly int[] MapPts = { 128, 256, 512, 1024 };

        static int level = -1;

        public static int Level
        {
            get
            {
                if (level < 0) level = Mathf.Clamp(PlayerPrefs.GetInt(Key, DefaultLevel), 0, Names.Length - 1);
                return level;
            }
            set
            {
                level = Mathf.Clamp(value, 0, Names.Length - 1);
                PlayerPrefs.SetInt(Key, level);
                PlayerPrefs.Save();
            }
        }

        public static int TextureEarth => EarthTex[Level];
        /// <summary>Луна и Марс; огни Земли — тоже этим размером (тексель огней крупнее цвета — точки городов не мельчат).</summary>
        public static int TextureDetailed => DetailedTex[Level];
        public static int TexturePlain => PlainTex[Level];
        public static int TextureClouds => CloudTex[Level];
        public static int SegmentsDetailed => DetailedSeg[Level];
        public static int SegmentsPlain => PlainSeg[Level];
        public static int CloudSegments => CloudSeg[Level];
        public static int MapPoints => MapPts[Level];
    }
}
