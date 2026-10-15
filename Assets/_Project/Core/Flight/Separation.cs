namespace Kare.Space.Core
{
    /// <summary>
    /// Тормозные РДТТ отработавшей ступени (GDD §5, «разделение по-честному»): пиропушители дают полтора метра в секунду,
    /// а ступень Saturn V ещё и тормозят твердотопливными ракетами, чтобы она не догнала верхнюю, пока у той
    /// разгоняются J-2. Импульс внешний — РДТТ стоят на самой ступени, отдачи у верхней нет.
    /// </summary>
    public struct RetroMotors
    {
        /// <summary>Сколько РДТТ по окружности.</summary>
        public int Count;
        /// <summary>Сколько они отнимают у ступени, м/с, и время работы, с.</summary>
        public double DeltaV, BurnTime;
        /// <summary>Стоят у верхнего среза ступени (иначе — у днища); сопла смотрят вперёд, с развалом наружу, °.</summary>
        public bool AtTop;
        public double CantDeg;
    }

    public static class Separation
    {
        /// <summary>
        /// Кувыркание ступени после тормозных РДТТ, рад/с: их тяга не идеально соосна, и ступень на видео
        /// уходит, медленно переворачиваясь. Постоянное, а не случайное — прогоны тестов повторяемы.
        /// Пара: Universe.CollisionGrace 1,5 с — за это время верх S-IC (21 м от ЦМ) уходит вбок на 0,03·21·1,5 ≈ 1 м,
        /// а назад — на (15 + 1,5)·1,5 ≈ 25 м: в сопла верхней ступени он не попадает.
        /// </summary>
        public const double RetroTumble = 0.03;

        /// <summary>
        /// Тормозные РДТТ по модели ступени. S-IC — восемь по 391 кН на 0,66 с в обтекателях двигателей, ≈ 2·10⁶ Н·с
        /// на ≈ 136 т сухой массы с остатками → 15 м/с. S-II — четыре по 155 кН на 1,5 с у переднего среза, ≈ 9·10⁵ Н·с
        /// на ≈ 40 т → 23 м/с. Остальные ступени расходятся пружинами/пиротолкателями (Vessel.StagePush).
        /// </summary>
        public static bool Retro(SectionModel model, out RetroMotors r)
        {
            switch (model)
            {
                case SectionModel.SaturnSIC:
                    r = new RetroMotors { Count = 8, DeltaV = 15, BurnTime = 0.66, AtTop = false, CantDeg = 15 };
                    return true;
                case SectionModel.SaturnSII:
                    r = new RetroMotors { Count = 4, DeltaV = 23, BurnTime = 1.5, AtTop = true, CantDeg = 10 };
                    return true;
                default:
                    r = default;
                    return false;
            }
        }
    }
}
