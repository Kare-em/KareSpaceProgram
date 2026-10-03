using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Автопилот «к Луне» (GDD §6.4, §6.11): опорная орбита (если борт ещё на старте — программа выведения),
    /// разгон к спутнику по TransferPlanner, у «Аполлона» — перестроение (КСМ отходит от переходника, причаливает к ЛМ,
    /// S-IVB отбрасывается), коррекция на трассе и торможение в перицентре до круговой окололунной орбиты.
    /// Решения принимает Tick (раз в кадр, и на рельсах тоже), импульсы исполняет Update в физике.
    /// </summary>
    public sealed class LunarAutopilot
    {
        /// <summary>Высота окололунной орбиты, м: как у «Аполлона-11» (≈ 110 км).</summary>
        public const double DefaultAltitude = 110e3;
        /// <summary>Опорная орбита у Земли, м. Пара: клавиша G (FlightInput) — то же выведение на 200 км.</summary>
        const double ParkingAltitude = 200e3;
        /// <summary>Сколько секунд вперёд искать момент разгона (TestLunar — те же 12 суток).</summary>
        const double TransferWindow = 12 * 86400;
        /// <summary>Промах по перицентру у Луны, после которого нужна коррекция на трассе, м. Пара: TestLunar.MidcourseTolerance.</summary>
        const double MidcourseTolerance = 300e3;
        /// <summary>Коррекция — не раньше, чем через столько после разгона: ошибка разгона уже видна, а цена поправки мала.</summary>
        const double MidcourseDelay = 2 * 3600;
        /// <summary>Перестроение «Аполлона»: ступень с ЛМ должна быть ближе, м.</summary>
        const double TranspositionRange = 500;
        /// <summary>Сколько раз уточнять прицельную дальность по прогнозу перицентра (PlanToPeriapsis).</summary>
        const int AimIterations = 5;
        /// <summary>Первое приближение v∞ у Луны, м/с: у перелёта за 3–5 суток ≈ 0,8–1,2 км/с.</summary>
        const double GuessVinf = 1000;
        /// <summary>Сколько раз подправлять перицентр уже в сфере Луны (дорого — только если коррекция не помогла).</summary>
        const int MaxSoiAims = 2;
        /// <summary>Запас на разворот перед импульсом в сфере Луны, с.</summary>
        const double TurnMargin = 120;
        /// <summary>
        /// Допуск перицентра на входе в сферу Луны, м. Там поправка дешёвая: 38 км перицентра ≈ 1 м/с
        /// (замер apollo8: прогноз 72 км при цели 110 — коррекция на трассе держит лишь MidcourseTolerance).
        /// </summary>
        const double SoiTolerance = 10e3;
        /// <summary>
        /// Допуск круговой орбиты после торможения, м. Торможение ~800 м/с идёт минуты, а не импульсом, и опускает
        /// перицентр (замер apollo8: 34 × 109 км) — доводка двумя импульсами в апсидах, как у LOI-2 «Аполлона».
        /// </summary>
        const double TrimTolerance = 10e3;
        const int MaxTrims = 2;

        public enum PhaseType { Ascent, Transfer, Transposition, Docking, Midcourse, Coast, Capture, Trim, Done }

        public readonly CelestialBody Target;
        public readonly double Altitude;
        public PhaseType Phase { get; private set; } = PhaseType.Ascent;
        public string Status { get; private set; } = "";
        public bool Close => dock != null && dock.Close;
        /// <summary>Фаза решается только в физике (Update): рельсы её бы пропустили.</summary>
        public bool NeedsPhysics => Phase == PhaseType.Transposition || Phase == PhaseType.Docking;

        readonly Universe universe;
        NodeAutopilot node;
        DockingAutopilot dock;
        double transferEnd = double.NaN, arrival = double.NaN;
        bool adapterStaged;
        int soiAims, trims;

        public LunarAutopilot(Universe u, CelestialBody target, double altitude = DefaultAltitude)
        {
            universe = u;
            Target = target;
            Altitude = altitude;
        }

        double Miss => Target.Radius + Altitude;

        /// <summary>Планирование, раз в кадр. true — автопилот закончил (или сдался).</summary>
        public bool Tick(Universe u)
        {
            var v = u.Active;
            if (v == null || !v.Alive) return true;
            double t = u.Time;
            switch (Phase)
            {
                case PhaseType.Ascent:
                    if (u.Ascent != null) { Status = "Выведение на опорную орбиту"; return false; }
                    if (v.Body == Target) { Phase = PhaseType.Coast; return false; }
                    if (v.Body != Target.Parent)
                    {
                        v.Raise($"Автопилот к Луне: борт не у {Target.Parent.Name}");
                        return true;
                    }
                    if (!OnParkingOrbit(v, t))
                    {
                        if (v.IsLanded || v.Body.HasAtmosphere && v.Altitude < v.Body.AtmosphereTop)
                        {
                            u.Ascent = new AscentAutopilot { TargetAltitude = ParkingAltitude };
                            Status = "Выведение на опорную орбиту";
                            return false;
                        }
                        v.Raise("Автопилот к Луне: нужна замкнутая орбита выше атмосферы");
                        return true;
                    }
                    var plan = PlanToPeriapsis(v.Body, v.Position, v.Velocity, t, t, TransferWindow);
                    if (plan == null)
                    {
                        v.Raise("Автопилот к Луне: перелёт не найден");
                        return true;
                    }
                    u.SetNode(plan.Time, plan.Prograde, plan.Normal, plan.Radial);
                    node = new NodeAutopilot();
                    arrival = plan.ArrivalTime;
                    Phase = PhaseType.Transfer;
                    u.Post($"К Луне: разгон через {Clock(plan.Time - t)}, Δv {plan.DeltaV:F0} м/с, перелёт {Clock(plan.TransferTime)}");
                    return false;

                case PhaseType.Midcourse:
                    if (node != null) return false;
                    if (v.Body == Target) { Phase = PhaseType.Coast; return false; }
                    var patches = PatchedConics.Predict(v.Body, v.Position, v.Velocity, t, null, 4);
                    var lunar = patches.Find(p => p.Body == Target);
                    if (lunar != null && Math.Abs(lunar.Orbit.PeriapsisRadius - Miss) <= MidcourseTolerance)
                    {
                        Phase = PhaseType.Coast;
                        return false;
                    }
                    double t0 = Math.Max(t, transferEnd + MidcourseDelay);
                    var fix = PlanToPeriapsis(v.Body, v.Position, v.Velocity, t, t0, 86400);
                    if (fix == null) { Phase = PhaseType.Coast; return false; }
                    u.SetNode(fix.Time, fix.Prograde, fix.Normal, fix.Radial);
                    node = new NodeAutopilot();
                    u.Post($"К Луне: коррекция через {Clock(fix.Time - t)}, {fix.DeltaV:F1} м/с");
                    return false;

                case PhaseType.Coast:
                    if (v.Body != Target)
                    {
                        Status = $"Перелёт: до Луны {Clock(Math.Max(0, arrival - t))}";
                        // Прошли мимо сферы влияния — коррекция не помогла, дальше пилот сам.
                        if (!double.IsNaN(arrival) && t > arrival + 86400)
                        {
                            v.Raise("Автопилот к Луне: Луна не достигнута");
                            return true;
                        }
                        return false;
                    }
                    var o = KeplerOrbit.FromState(v.Position, v.Velocity, Target.Mu, t);
                    if (Math.Abs(o.PeriapsisRadius - Miss) > SoiTolerance && soiAims < MaxSoiAims && o.TimeToPeriapsis(t) > 3 * TurnMargin)
                    {
                        soiAims++;
                        double was = o.PeriapsisRadius;
                        AimPeriapsis(u, o, t + TurnMargin);
                        node = new NodeAutopilot();
                        Phase = PhaseType.Midcourse;
                        u.Post($"У Луны: перицентр {(was - Target.Radius) / 1000:F0} км — поправка на {Altitude / 1000:F0} км, {v.Node.Remaining.magnitude:F0} м/с");
                        return false;
                    }
                    double rp = o.PeriapsisRadius, tp = o.TimeToPeriapsis(t);
                    double vp = Math.Sqrt(Target.Mu * (2 / rp - 1 / o.A)), vc = Math.Sqrt(Target.Mu / rp);
                    u.SetNode(t + tp, -(vp - vc), 0, 0);
                    node = new NodeAutopilot();
                    Phase = PhaseType.Capture;
                    u.Post($"У Луны: перицентр {(rp - Target.Radius) / 1000:F0} км через {Clock(tp)}, торможение {vp - vc:F0} м/с");
                    return false;

                case PhaseType.Done:
                    return true;
            }
            return false;
        }

        /// <summary>Исполнение в физике: импульсы, перестроение, стыковка.</summary>
        public AutopilotRequest Update(Vessel v, double t, double dt)
        {
            if (!v.Alive) return AutopilotRequest.Finished;
            if (dock != null)
            {
                if (dock.Docked)
                {
                    dock = null;
                    Status = "Причалили";
                    // S-IVB в связке сверху перевёрнутым — отбросить (шаг программы «Отделение S-IVB»).
                    var na = NextAction(v);
                    if (na is StageAction a && a.Type == StageActionType.Separate && v.Attached[a.Section] && v.Flipped[a.Section])
                    {
                        Phase = PhaseType.Midcourse;
                        return AutopilotRequest.Stage;
                    }
                    Phase = PhaseType.Midcourse;
                    return AutopilotRequest.None;
                }
                var req = dock.Update(v, t, dt);
                Status = "Перестроение: " + dock.Status;
                if (req == AutopilotRequest.Finished) { dock = null; Phase = PhaseType.Midcourse; }
                return req == AutopilotRequest.Stage ? req : AutopilotRequest.None;
            }
            if (Phase == PhaseType.Transposition) return Transpose(v, t);
            if (node == null) return AutopilotRequest.None;
            var r = node.Update(v, t, dt);
            Status = node.Status;
            if (r != AutopilotRequest.Finished) return r;
            node = null;
            switch (Phase)
            {
                case PhaseType.Transfer:
                    transferEnd = t;
                    Phase = PhaseType.Transposition;
                    break;
                case PhaseType.Midcourse:
                    Phase = PhaseType.Coast;
                    break;
                case PhaseType.Capture:
                case PhaseType.Trim:
                    var o = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, t);
                    if (trims < MaxTrims && o.IsElliptic &&
                        (Math.Abs(o.PeriapsisRadius - Miss) > TrimTolerance || Math.Abs(o.ApoapsisRadius - Miss) > TrimTolerance))
                    {
                        trims++;
                        TrimNode(o, t);
                        node = new NodeAutopilot();
                        Phase = PhaseType.Trim;
                        Status = "Доводка орбиты";
                        return AutopilotRequest.None;
                    }
                    Phase = PhaseType.Done;
                    v.Raise($"Окололунная орбита: {(o.PeriapsisRadius - Target.Radius) / 1000:F0} × {(o.ApoapsisRadius - Target.Radius) / 1000:F0} км");
                    return AutopilotRequest.Finished;
            }
            return AutopilotRequest.None;
        }

        /// <summary>
        /// Перестроение (§6.6): шаг программы «отделение переходника» уводит КСМ от S-IVB. Если на ступени остался борт
        /// со стыковочным узлом (ЛМ) — разворот и причаливание; иначе (макет LTA-B «Аполлона-8») — сразу дальше.
        /// </summary>
        AutopilotRequest Transpose(Vessel v, double t)
        {
            var na = NextAction(v);
            if (!adapterStaged && na is StageAction a && a.Type == StageActionType.Separate && v.Design.Sections[a.Section].Kind == SectionKind.Fairing)
            {
                adapterStaged = true;
                Status = "Отделение от переходника";
                return AutopilotRequest.Stage;
            }
            if (adapterStaged && v.HasFreePort)
            {
                Vessel best = null;
                double bd = TranspositionRange;
                foreach (var x in universe.Vessels)
                {
                    if (!Universe.CanDock(v, x)) continue;
                    double d = Vector3d.Distance(x.Position, v.Position);
                    if (d < bd) { bd = d; best = x; }
                }
                if (best != null)
                {
                    dock = new DockingAutopilot(universe, best);
                    Phase = PhaseType.Docking;
                    v.Raise($"Перестроение: причаливание к {best.Name}");
                    return AutopilotRequest.None;
                }
            }
            Phase = PhaseType.Midcourse;
            return AutopilotRequest.None;
        }

        /// <summary>
        /// Планировщик целит в наименьшее расстояние до Луны по орбите вокруг Земли — без притяжения Луны. А оно
        /// фокусирует: при v∞ ≈ 1 км/с прицельные 1847 км дают перицентр ≈ 300 км от центра, то есть удар (замер
        /// apollo8: прогноз −1488 км). Поэтому прицельная дальность b подбирается по прогнозу кусочно-конического
        /// перелёта: b = rp·√(1 + 2μ/(rp·v∞²)), поправка — отношением b(нужный rp) / b(полученный rp).
        /// (r, vel) — на момент t; импульс ищется не раньше t0.
        /// </summary>
        TransferPlan PlanToPeriapsis(CelestialBody body, Vector3d r, Vector3d vel, double t, double t0, double window)
        {
            // Планировщик считает (r, v) взятыми в момент t0 — переносим состояние по орбите.
            var orbit = KeplerOrbit.FromState(r, vel, body.Mu, t);
            orbit.GetState(t0, out var r0, out var v0);
            var orbit0 = KeplerOrbit.FromState(r0, v0, body.Mu, t0);
            double vinf = GuessVinf, b = Impact(Miss, vinf), bestErr = double.PositiveInfinity;
            TransferPlan best = null;
            for (int k = 0; k < AimIterations; k++)
            {
                var plan = TransferPlanner.PlanIntercept(body, r0, v0, t0, Target, b, window);
                if (plan == null) break;
                var node = new ManeuverNode { Time = plan.Time, Prograde = plan.Prograde, Normal = plan.Normal, Radial = plan.Radial }; node.Remaining = node.WorldDeltaV(orbit0);
                var patch = PatchedConics.Predict(body, r0, v0, t0, node, 4).Find(p => p.Body == Target);
                if (patch == null)
                {
                    // Сфера влияния не достигнута — целиться ближе.
                    b *= 0.5;
                    continue;
                }
                double pe = patch.Orbit.PeriapsisRadius, err = Math.Abs(pe - Miss);
                if (err < bestErr) { bestErr = err; best = plan; }
                if (err < MidcourseTolerance / 3) break;
                if (!patch.Orbit.IsElliptic) vinf = Math.Sqrt(-Target.Mu / patch.Orbit.A);
                b *= Impact(Miss, vinf) / Impact(pe, vinf);
            }
            return best;
        }

        /// <summary>Импульс в апсиде, чей радиус ближе к цели: противоположная апсида переносится на Miss.</summary>
        void TrimNode(KeplerOrbit o, double t)
        {
            double rp = o.PeriapsisRadius, ra = o.ApoapsisRadius, period = o.Period;
            bool atPe = Math.Abs(rp - Miss) <= Math.Abs(ra - Miss);
            double r1 = atPe ? rp : ra;
            double dt = o.TimeToPeriapsis(t) + (atPe ? 0 : period / 2);
            dt %= period;
            if (dt < TurnMargin) dt += period;
            double vOld = Math.Sqrt(Target.Mu * (2 / r1 - 1 / o.A));
            double vNew = Math.Sqrt(Target.Mu * (2 / r1 - 2 / (r1 + Miss)));
            universe.SetNode(t + dt, vNew - vOld, 0, 0);
        }

        double Impact(double rp, double vinf) => rp * Math.Sqrt(1 + 2 * Target.Mu / (rp * vinf * vinf));

        /// <summary>
        /// Поправка перицентра уже в сфере Луны: модуль скорости (энергия) сохраняется, поворачивается направление в
        /// плоскости подлёта так, чтобы момент импульса r·v⊥ дал перицентр Miss. На 60 тыс. км это десятки м/с.
        /// </summary>
        void AimPeriapsis(Universe u, KeplerOrbit o, double tn)
        {
            o.GetState(tn, out var r, out var vel);
            double rn = r.magnitude, vn = vel.magnitude;
            var rh = r / rn;
            var perp = vel - rh * Vector3d.Dot(vel, rh);
            var th = perp.sqrMagnitude > 1e-6 ? perp.normalized : Vector3d.Cross(rh, Target.PoleAt(tn)).normalized;
            double energy = vn * vn / 2 - Target.Mu / rn;
            double vp = Math.Sqrt(Math.Max(0, 2 * (energy + Target.Mu / Miss)));
            double vt = Math.Min(Miss * vp / rn, vn * 0.999);
            double vr = -Math.Sqrt(vn * vn - vt * vt);
            var dv = rh * vr + th * vt - vel;
            var pro = vel.normalized;
            var nrm = Vector3d.Cross(r, vel).normalized;
            var rad = Vector3d.Cross(pro, nrm);
            u.SetNode(tn, Vector3d.Dot(dv, pro), Vector3d.Dot(dv, nrm), Vector3d.Dot(dv, rad));
        }

        StageAction? NextAction(Vessel v)
        {
            int k = v.NextApplicable(v.NextStage);
            return k < v.Design.Sequence.Count ? v.Design.Sequence[k] : null;
        }

        bool OnParkingOrbit(Vessel v, double t)
        {
            if (v.IsLanded) return false;
            var o = KeplerOrbit.FromState(v.Position, v.Velocity, v.Body.Mu, t);
            double floor = v.Body.Radius + (v.Body.HasAtmosphere ? v.Body.AtmosphereTop : 10e3);
            return o.IsElliptic && o.PeriapsisRadius > floor;
        }

        static string Clock(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s)) return "—";
            var ts = TimeSpan.FromSeconds(Math.Max(0, s));
            return ts.TotalDays >= 1 ? $"{(int)ts.TotalDays} сут {ts.Hours} ч" : ts.TotalHours >= 1 ? $"{(int)ts.TotalHours} ч {ts.Minutes} мин" : $"{ts.Minutes} мин {ts.Seconds} с";
        }
    }
}
