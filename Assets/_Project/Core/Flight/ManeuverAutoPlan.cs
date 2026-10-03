using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Узел манёвра «к цели» одной клавишей (GDD §6.11): игрок выбирает тело — планировщик сам ищет момент
    /// и импульс. Три случая по иерархии тел:
    /// спутник своего тела (Луна с орбиты Земли) — TransferPlanner.PlanIntercept с доводкой перицентра;
    /// соседняя планета (Марс с орбиты Земли) — PlanInterplanetary: окно по Ламберту вокруг Солнца;
    /// родитель (Земля с орбиты Луны) — PlanReturn с перицентром в коридоре входа.
    /// Узел только ставится: исполняет его B (NodeAutopilot), как и ручной.
    /// </summary>
    public static class ManeuverAutoPlan
    {
        /// <summary>Высота перицентра у спутника, м: низкая орбита захвата, как у «Аполлона» (110 км).</summary>
        public const double MoonPeriapsis = 100e3;
        /// <summary>Перицентр возврата к телу без атмосферы, м.</summary>
        public const double ReturnPeriapsisVacuum = 100e3;
        /// <summary>Сколько вперёд искать разгон к спутнику, с. Сутки — у низкой орбиты это ~16 витков, и импульс
        /// найдётся в любой их точке; дальше счёт растёт линейно, а выигрыш копеечный (замер — тест autoplan).</summary>
        public const double MoonWindow = 86400;
        /// <summary>Спуск доводки перицентра у спутника: начальный и последний шаг, м/с, и ходов на шаг.
        /// 1 м/с разгона сдвигает перицентр у Луны на ~1000 км, отсюда шаг ~0,5 м/с и мельче.</summary>
        const double AimStep = 0.5, AimStepMin = 0.002;
        const int AimMoves = 40;
        /// <summary>Догадка о скорости на бесконечности у спутника, м/с — первое приближение прицельного параметра.</summary>
        const double GuessVinf = 1000;
        /// <summary>Допуск доводки перицентра у спутника, м.</summary>
        const double AimTolerance = 50e3;

        /// <summary>Тело по умолчанию: Луна с орбиты Земли, Земля с орбиты Луны, иначе первый спутник.</summary>
        public static CelestialBody DefaultTarget(Vessel v)
        {
            if (v == null) return null;
            if (v.Body.Children.Count > 0) return v.Body.Children[0];
            return v.Body.Parent != null && v.Body.Parent.Parent != null ? v.Body.Parent : null;
        }

        /// <summary>Поставить узел к телу target. Возвращает сообщение для игрока; plan != null — узел поставлен.</summary>
        public static string Plan(Universe u, CelestialBody target, out TransferPlan plan)
        {
            plan = null;
            var v = u.Active;
            if (v == null || !v.Alive) return "Нет корабля";
            if (target == null) return "Цель не выбрана: на карте Tab — выбор тела";
            if (v.IsLanded) return "Сначала выйдите на орбиту (G)";
            if (target == v.Body) return $"Корабль уже у тела {target.Name}";
            var body = v.Body;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, body.Mu, u.Time);
            if (!orbit.IsElliptic) return "Манёвр к цели планируется с замкнутой орбиты";
            if (orbit.PeriapsisRadius < body.Radius + body.AtmosphereTop) return "Сначала замкните орбиту выше атмосферы";

            string kind;
            if (target.Parent == body)
            {
                kind = "перелёт";
                plan = ToMoon(body, v, u.Time, target);
            }
            else if (body.Parent != null && target == body.Parent)
            {
                kind = "возврат";
                double pe = target.Radius + (target.HasAtmosphere ? MissionAutopilot.ReturnPerigee : ReturnPeriapsisVacuum);
                plan = TransferPlanner.PlanReturn(body, v.Position, v.Velocity, u.Time, pe);
            }
            else if (body.Parent != null && target.Parent == body.Parent && body.Parent.Parent == null)
            {
                kind = "межпланетный перелёт";
                // Промах — десятая сферы влияния, но не ближе трёх радиусов: прицел, а не попадание (коррекция — потом).
                double miss = Math.Max(0.1 * target.SoiRadius, 3 * target.Radius);
                plan = TransferPlanner.PlanInterplanetary(body, v.Position, v.Velocity, u.Time, target, miss);
            }
            else return $"{target.Name}: сначала к телу {(body.Parent?.Name ?? "?")} — прямой перелёт не планируется";

            if (plan == null) return $"{target.Name}: {kind} не найден";
            u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
            return $"{target.Name}: {kind}, разгон через {MissionAutopilot.Clock(plan.Time - u.Time)}, Δv {plan.DeltaV:F0} м/с, " +
                   $"в пути {MissionAutopilot.Clock(plan.TransferTime)}. B — выполнить";
        }

        /// <summary>
        /// Перелёт к спутнику с перицентром MoonPeriapsis. PlanIntercept целит в прицельный параметр без учёта
        /// притяжения цели: b = rp·√(1 + 2μ/(rp·v∞²)) даёт перицентр с ошибкой в сотни км, а пересчёт b по прогнозу
        /// не сходится — сторона облёта перескакивает (замер: b 4211 → 518 км, b 3718 → −200 км). Поэтому доводка —
        /// спуском по (прогрейд, нормаль) прямо по прогнозу: множество «перицентр = цель» — окружность в картинной
        /// плоскости, годится любая её точка.
        /// </summary>
        static TransferPlan ToMoon(CelestialBody body, Vessel v, double t, CelestialBody target)
        {
            double want = target.Radius + MoonPeriapsis;
            var plan = TransferPlanner.PlanIntercept(body, v.Position, v.Velocity, t, target, Impact(target, want, GuessVinf), MoonWindow);
            if (plan == null) return null;
            var orbit = KeplerOrbit.FromState(v.Position, v.Velocity, body.Mu, t);
            double Cost(double pro, double nor)
            {
                var node = new ManeuverNode { Time = plan.Time, Prograde = pro, Normal = nor, Radial = plan.Radial };
                node.Remaining = node.WorldDeltaV(orbit);
                var patch = PatchedConics.Predict(body, v.Position, v.Velocity, t, node, 4).Find(p => p.Body == target);
                return patch == null ? double.PositiveInfinity : Math.Abs(patch.Orbit.PeriapsisRadius - want);
            }
            double best = Cost(plan.Prograde, plan.Normal);
            if (double.IsInfinity(best)) return plan;
            for (double step = AimStep; step >= AimStepMin && best > AimTolerance; step *= 0.5)
            {
                bool moved = true;
                for (int i = 0; i < AimMoves && moved && best > AimTolerance; i++)
                {
                    moved = false;
                    foreach (var (dp, dn) in new[] { (step, 0.0), (-step, 0.0), (0.0, step), (0.0, -step) })
                    {
                        double c = Cost(plan.Prograde + dp, plan.Normal + dn);
                        if (c >= best) continue;
                        best = c; plan.Prograde += dp; plan.Normal += dn; moved = true;
                        break;
                    }
                }
            }
            return plan;
        }

        static double Impact(CelestialBody target, double rp, double vinf) => rp * Math.Sqrt(1 + 2 * target.Mu / (rp * vinf * vinf));
    }
}
