using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Визуальные параметры тел — только в игровом слое: в Core цветов нет (SCENE_SETUP, шпаргалка).
    /// Этап 1 GDD §9.4: процедурная заглушка — цвет по высоте и широте, без внешних текстур.
    /// </summary>
    public struct BodyLook
    {
        public Color Low, High, Ocean, Ice;
        /// <summary>Средний цвет диска из космоса (сферическое альбедо) — им подсвечен борт на орбите (§9.3).
        /// Альфа 0 — не задан, берётся Low.</summary>
        public Color Disc;
        /// <summary>Широта начала полярной шапки, °; 90 — без шапки.</summary>
        public float IceLatitude;
        /// <summary>Полосы газового гиганта: число полос и контраст.</summary>
        public int Bands;
        public float BandContrast;
    }

    public static class BodyVisuals
    {
        static readonly Color Rock = new Color(0.45f, 0.43f, 0.40f);

        public static BodyLook Get(string id)
        {
            switch (id)
            {
                case "earth":
                    return new BodyLook { Low = new Color(0.20f, 0.36f, 0.14f), High = new Color(0.55f, 0.48f, 0.36f),
                        Ocean = new Color(0.03f, 0.10f, 0.28f), Ice = new Color(0.92f, 0.94f, 0.96f), IceLatitude = 72,
                        // Голубоватый, а не зелень Low: та красила теневую сторону борта в болотный (35, 46, 28).
                        // Не сферическое альбедо 0,30: PBSky берёт groundTint и в многократное рассеяние дымки,
                        // при 0,3 океан с 200 км бледный (134, 166, 203), при 0 — (68, 100, 139) (замер 01.10.2026).
                        // Поэтому альбедо поверхности под облаками (океан ≈ 0,06…0,1) с запасом на подсветку борта.
                        Disc = new Color(0.12f, 0.15f, 0.20f, 1) };
                case "moon":
                    return new BodyLook { Low = new Color(0.30f, 0.30f, 0.30f), High = new Color(0.58f, 0.57f, 0.55f), IceLatitude = 90 };
                case "mars":
                    return new BodyLook { Low = new Color(0.45f, 0.20f, 0.10f), High = new Color(0.72f, 0.42f, 0.24f),
                        Ice = new Color(0.90f, 0.88f, 0.86f), IceLatitude = 80 };
                case "mercury":
                    return new BodyLook { Low = new Color(0.35f, 0.33f, 0.31f), High = new Color(0.55f, 0.52f, 0.49f), IceLatitude = 90 };
                case "venus":
                    return Gas(new Color(0.85f, 0.78f, 0.58f), new Color(0.93f, 0.88f, 0.72f), 6, 0.15f);
                case "jupiter":
                    return Gas(new Color(0.62f, 0.48f, 0.36f), new Color(0.92f, 0.86f, 0.76f), 14, 0.6f);
                case "saturn":
                    return Gas(new Color(0.78f, 0.68f, 0.48f), new Color(0.92f, 0.86f, 0.66f), 12, 0.3f);
                case "uranus":
                    return Gas(new Color(0.55f, 0.78f, 0.82f), new Color(0.66f, 0.86f, 0.88f), 4, 0.1f);
                case "neptune":
                    return Gas(new Color(0.20f, 0.36f, 0.78f), new Color(0.32f, 0.50f, 0.90f), 6, 0.2f);
                case "io":
                    return new BodyLook { Low = new Color(0.75f, 0.65f, 0.25f), High = new Color(0.90f, 0.85f, 0.55f), IceLatitude = 90 };
                case "europa":
                case "enceladus":
                    return new BodyLook { Low = new Color(0.70f, 0.66f, 0.60f), High = new Color(0.92f, 0.92f, 0.90f), IceLatitude = 90 };
                case "titan":
                    return Gas(new Color(0.72f, 0.55f, 0.28f), new Color(0.80f, 0.64f, 0.35f), 3, 0.08f);
                default:
                    return new BodyLook { Low = Rock * 0.7f, High = Rock * 1.3f, IceLatitude = 90 };
            }
        }

        static BodyLook Gas(Color a, Color b, int bands, float contrast) =>
            new BodyLook { Low = a, High = b, IceLatitude = 90, Bands = bands, BandContrast = contrast };
    }
}
