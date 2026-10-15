namespace Kare.Space.Core
{
    /// <summary>
    /// Плотные кольца — для столкновений (§2, настоящая Солнечная система). Борт, пересёкший плоскость экватора в
    /// плотной полосе, гибнет: лёд от сантиметров до метров на скорости в километры в секунду. Плотная — оптическая
    /// толща τ ≥ 0,01 (на просвет закрыт 1 % площади, там сплошь сантиметровые глыбы). Пылевые кольца (D, E, G Сатурна,
    /// кольца Юпитера и Нептуна, τ ≤ 0,004) и щели проходимы — «Кассини» нырял между D и планетой и сквозь плоскость
    /// за F. Радиусы, км, — те же, что у картинки колец. Пара: Game/RingProfiles (Saturn, Uranus) — меняешь полосу там,
    /// правь и здесь.
    /// </summary>
    public static class Rings
    {
        public readonly struct Band
        {
            public readonly double Inner, Outer;
            public readonly string Name;
            public Band(double innerKm, double outerKm, string name) { Inner = innerKm * 1e3; Outer = outerKm * 1e3; Name = name; }
            public bool Contains(double r) => r >= Inner && r <= Outer;
        }

        static Band B(double r0, double r1, string name) => new Band(r0, r1, name);
        static Band C(double center, double half, string name) => new Band(center - half, center + half, name);

        // Сатурн. Порядок проверки: колечки внутри щелей (плотные) → щели (проходимы) → основные кольца.
        static readonly Band[] SaturnRinglets =
        {
            B(77859, 77884, "колечко Титана"), B(87459, 87523, "колечко Максвелла"), B(117805, 117845, "колечко Гюйгенса"),
        };
        static readonly Band[] SaturnGaps =
        {
            B(77750, 77930, "щель Коломбо"), B(87340, 87610, "щель Максвелла"), B(88690, 88720, "щель Бонда"),
            B(90200, 90220, "щель Доуза"), B(117680, 118030, "щель Гюйгенса"), B(118183, 118469, "щель Гершеля"),
            B(118597, 118630, "щель Рассела"), B(118931, 118969, "щель Джеффриса"), B(119403, 119406, "щель Кейпера"),
            B(119848, 120086, "щель Лапласа"), B(120236, 120246, "щель Бесселя"), B(120305, 120318, "щель Барнарда"),
            B(133423, 133745, "щель Энке"), B(136485, 136522, "щель Килера"),
        };
        static readonly Band[] SaturnMain =
        {
            B(74490, 91980, "кольцо C"), B(91980, 117580, "кольцо B"), B(117580, 122170, "деление Кассини"),
            B(122170, 136775, "кольцо A"),
            // Ядро F: τ = 0,5·exp(−x²/2), σ 15 км (RingProfiles.FCoreTau/FCoreSigma) — до τ 0,01 это ±2,8σ ≈ ±42 км.
            C(140180, 42, "кольцо F"),
        };
        // Уран: узкие плотные кольца (τ 0,1…1,5), между ними пусто.
        static readonly Band[] Uranus =
        {
            C(41837, 1.5, "кольцо 6"), C(42234, 2, "кольцо 5"), C(42571, 2, "кольцо 4"), C(44718, 7, "кольцо α"),
            C(45661, 7, "кольцо β"), C(47176, 1.5, "кольцо η"), C(47627, 3, "кольцо γ"), C(48300, 6, "кольцо δ"),
            C(50023, 2, "кольцо λ"), C(51149, 58, "кольцо ε"),
        };

        /// <summary>У тела есть плотные кольца — иначе пересечения не проверяем.</summary>
        public static bool HasDense(CelestialBody b) => b != null && (b.Id == "saturn" || b.Id == "uranus");

        /// <summary>
        /// Границы всей системы плотных колец, м (с колечками и щелями внутри) — для прицела: через щели автопилот не
        /// целится, узлы орбиты кладёт снаружи (как «Кассини» — между F и G). false — плотных колец нет.
        /// </summary>
        public static bool DenseSpan(CelestialBody b, out double inner, out double outer)
        {
            var bands = b?.Id == "saturn" ? SaturnMain : b?.Id == "uranus" ? Uranus : null;
            inner = outer = 0;
            if (bands == null) return false;
            inner = bands[0].Inner;
            outer = bands[bands.Length - 1].Outer;
            return true;
        }

        /// <summary>Имя плотной полосы на радиусе r (м) в плоскости экватора; null — пусто, пыль или щель.</summary>
        public static string DenseAt(CelestialBody b, double r)
        {
            switch (b?.Id)
            {
                case "saturn":
                    foreach (var x in SaturnRinglets) if (x.Contains(r)) return x.Name;
                    foreach (var x in SaturnGaps) if (x.Contains(r)) return null;
                    foreach (var x in SaturnMain) if (x.Contains(r)) return x.Name;
                    return null;
                case "uranus":
                    foreach (var x in Uranus) if (x.Contains(r)) return x.Name;
                    return null;
            }
            return null;
        }
    }
}
