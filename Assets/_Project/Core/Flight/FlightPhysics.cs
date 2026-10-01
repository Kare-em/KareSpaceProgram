using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Полётная физика активного корабля (GDD §4): RK4 по положению и скорости в невращающейся системе
    /// центра тела, тяга с учётом давления, сопротивление по Маху и углу атаки, нагрев, qα, посадка.
    /// Гравитация — только тела текущей сферы влияния: иначе рельсы и физика разойдутся.
    /// </summary>
    public static class FlightPhysics
    {
        public const double MaxStep = 0.02;

        /// <summary>Доля раскрытой площади купола через t секунд после ввода.</summary>
        public static double ChuteFraction(double t)
        {
            if (t < 1) return ChuteReefFraction * t;
            if (t < 1 + ChuteReefTime) return ChuteReefFraction;
            double k = Math.Min(1, (t - 1 - ChuteReefTime) / ChuteFillTime);
            return ChuteReefFraction + (1 - ChuteReefFraction) * k * k;
        }
        /// <summary>Быстрее — разбился (GDD §4.8).</summary>
        public const double CrashSpeed = 10;
        /// <summary>Порог разрушения от поперечной нагрузки: q·sin α, Па (GDD §4.7).</summary>
        public const double QAlphaLimit = 4000;
        public const double ChuteMaxQ = 25000;
        /// <summary>
        /// Раскрытие купола с рифлением (GDD §4.8): за 1 с — 3% площади, так висит 4 с, гася скорость,
        /// затем за 4 с дораскрывается (рост площади ~t²). Мгновенный купол 600 м² на 170 м/с давал
        /// бы сотни g; с рифлением пик ~6 g (оценка интегрированием: 7 км, 170 м/с, 2,5 т).
        /// </summary>
        const double ChuteReefFraction = 0.03, ChuteReefTime = 4, ChuteFillTime = 4;
        /// <summary>Константа Саттона — Грейвса для воздуха, кг^0.5/м.</summary>
        const double SuttonGraves = 1.7415e-4;
        /// <summary>Сколько секунд суммарного перегрева выдерживает обшивка.</summary>
        public const double OverheatTolerance = 3;

        /// <summary>
        /// Правила повреждений (GDD §4.7). По умолчанию выключены: игрок учится разворачивать ракету, а не
        /// теряет её на первом же развороте. qα и нагрев считаются всегда — HUD их показывает; при выключенном
        /// правиле борт просто не разрушается. Тесты ядра включают оба: автопилоты обязаны проходить и по
        /// строгим правилам. Задаются из игры (GameBootstrap) — статика, т.к. правило одно на всю симуляцию.
        /// </summary>
        public static bool AeroBreakup, HeatDamage;

        /// <summary>Превышен ли предел поперечной нагрузки — и для разрушения, и для предупреждения HUD.
        /// Шар капсулы (длина ≤ 4 радиусов) ей не подвержен; ниже 2 кПа не ломается ничего.</summary>
        public static bool QAlphaExceeded(double q, double sinA, double length, double radius) =>
            length > 4 * radius && q > 2000 && q * sinA > QAlphaLimit;

        static readonly double[] CdMach = { 0, 0.6, 0.85, 1.05, 1.2, 2, 4, 10 };
        static readonly double[] CdValue = { 0.30, 0.30, 0.45, 0.80, 0.70, 0.50, 0.35, 0.30 };

        /// <summary>Вектор угловой скорости вращения тела в P, рад/с.</summary>
        public static Vector3d SpinAxis(CelestialBody body, double t) =>
            (body.OrientationAt(t) * Vector3d.forward) * body.RotationRate;

        public static void Step(Vessel v, double t, double dt)
        {
            if (!v.Alive) return;
            if (v.IsLanded) StepLanded(v, t, dt);
            else StepFlying(v, t, dt);
        }

        /// <summary>Телеметрия без шага физики — для корабля на рельсах.</summary>
        public static void UpdateTelemetry(Vessel v, double t)
        {
            if (!v.Alive) return;
            UpdateAir(v, v.Body, v.Position, v.Velocity, SpinAxis(v.Body, t));
            var bf = v.Body.OrientationAt(t).Inverse * v.Position;
            v.TerrainAltitude = v.Altitude - v.Body.SurfaceHeight(bf);
            v.GForce = 0;
            v.CurrentThrust = 0;
        }

        // ---------------------------------------------------------------- на поверхности

        /// <summary>Поставить корабль на поверхность носом в зенит (стартовый стол).</summary>
        public static void PlaceOnSurface(Vessel v, CelestialBody body, double latDeg, double lonDeg, double t,
            double padHeight = 0)
        {
            v.Body = body;
            v.MassProperties(out _, out double com, out _, out _);
            var up = CelestialBody.LatLonToBodyFixed(latDeg, lonDeg);
            double h = body.SurfaceHeight(up);
            var east = Vector3d.Cross(Vector3d.forward, up).normalized;
            var north = Vector3d.Cross(up, east);
            v.AnchorBodyFixed = up * (body.Radius + h + padHeight + com);
            // Связанные оси: X — север, Y — зенит, Z — запад (в координатах U тела). Крен выбран под клавиши
            // FlightControl (D — нос к −Z): по умолчанию D уводит на восток, как в KSP (§10.1). Пара — FlightCamera
            // ставит камеру на столе лицом на север, чтобы восток был справа.
            v.AttitudeBodyFixed = QuaternionD.FromBasis(north.SwapYZ, up.SwapYZ, (-east).SwapYZ);
            v.Situation = Situation.Landed;
            UpdateLandedPose(v, t);
        }

        public static void UpdateLandedPose(Vessel v, double t)
        {
            var o = v.Body.OrientationAt(t);
            v.Position = o * v.AnchorBodyFixed;
            v.Velocity = Vector3d.Cross(SpinAxis(v.Body, t), v.Position);
            v.Attitude = o.SwapYZ * v.AttitudeBodyFixed;
            v.AngularVelocity = Vector3d.zero;
        }

        static void StepLanded(Vessel v, double t, double dt)
        {
            var body = v.Body;
            UpdateLandedPose(v, t + dt);
            v.UpdateEngines();
            UpdateAir(v, body, v.Position, v.Velocity, SpinAxis(body, t + dt));
            double thrust = v.Thrust(v.StaticPressure, out _);
            v.CurrentThrust = thrust;
            double r = v.Position.magnitude;
            double weight = v.Mass * body.Mu / (r * r);
            if (thrust * Vector3d.Dot(v.NoseP, v.Position / r) > weight)
            {
                bool fromWater = v.Situation == Situation.Splashed;
                v.Situation = Situation.Flying;
                if (double.IsNaN(v.LaunchTime)) v.LaunchTime = t;
                v.Raise(fromWater ? "Взлёт с воды" : "Есть отрыв!");
            }
            v.BurnPropellant(dt);
            v.SettledTimer = 0;
            v.TerrainAltitude = 0;
            v.GForce = 1;
        }

        // ---------------------------------------------------------------- в полёте

        struct Geometry
        {
            public double Mass, Com, Length, Radius, DragScale, FrontRadius;
            public double ChuteCdA;
        }

        static void StepFlying(Vessel v, double t, double dt)
        {
            var body = v.Body;
            var spin = SpinAxis(body, t);
            v.UpdateEngines();

            v.MassProperties(out double mass, out double com, out double len, out double maxR);
            var nose = v.NoseP;
            UpdateAir(v, body, v.Position, v.Velocity, spin);
            double thrust = v.Thrust(v.StaticPressure, out _);
            v.CurrentThrust = thrust;

            var g = new Geometry { Mass = mass, Com = com, Length = len, Radius = maxR };
            var vAir0 = v.Velocity - Vector3d.Cross(spin, v.Position);
            bool noseFirst = Vector3d.Dot(nose, vAir0) >= 0;
            int lead = noseFirst ? v.TopSection() : v.BottomSection();
            var leadDef = v.Design.Sections[lead];
            g.DragScale = leadDef.DragScale;
            g.FrontRadius = Math.Max(0.3, leadDef.Radius);
            for (int i = 0; i < v.Attached.Length; i++)
                if (v.Attached[i] && v.ChuteDeployed[i] && !v.ChuteFailed[i])
                    g.ChuteCdA += 1.5 * v.Design.Sections[i].ParachuteArea * ChuteFraction(v.ChuteOpenTime[i]);

            var thrustAcc = nose * ((thrust + v.RcsForward * v.RcsThrust) / mass);

            // RK4: тяга и масса постоянны на шаге, гравитация и сопротивление — от состояния.
            var r0 = v.Position;
            var u0 = v.Velocity;
            var a1 = Accel(body, spin, nose, g, thrustAcc, r0, u0);
            var r1 = r0 + u0 * (dt * 0.5);
            var u1 = u0 + a1 * (dt * 0.5);
            var a2 = Accel(body, spin, nose, g, thrustAcc, r1, u1);
            var r2 = r0 + u1 * (dt * 0.5);
            var u2 = u0 + a2 * (dt * 0.5);
            var a3 = Accel(body, spin, nose, g, thrustAcc, r2, u2);
            var r3 = r0 + u2 * dt;
            var u3 = u0 + a3 * dt;
            var a4 = Accel(body, spin, nose, g, thrustAcc, r3, u3);
            v.Position = r0 + (u0 + u1 * 2 + u2 * 2 + u3) * (dt / 6);
            v.Velocity = u0 + (a1 + a2 * 2 + a3 * 2 + a4) * (dt / 6);

            // Вращение: момент регулятора, обрезанный по возможностям, + аэродинамика капсулы.
            var inertia = v.Inertia();
            var tmax = v.MaxTorque(v.StaticPressure);
            var cmd = v.TorqueCommand;
            var torque = new Vector3d(
                MathD.Clamp(cmd.x, -tmax.x, tmax.x),
                MathD.Clamp(cmd.y, -tmax.y, tmax.y),
                MathD.Clamp(cmd.z, -tmax.z, tmax.z));
            torque += CapsuleAeroTorque(v, vAir0, g, inertia);
            var w = v.AngularVelocity;
            w = new Vector3d(w.x + torque.x / inertia.x * dt, w.y + torque.y / inertia.y * dt, w.z + torque.z / inertia.z * dt);
            v.AngularVelocity = w;
            v.Attitude = v.Attitude.IntegrateBody(w, dt);

            v.BurnPropellant(dt);
            if (v.Node != null && thrust > 0) v.Node.Remaining -= nose * (thrust / mass * dt);

            // Телеметрия на конец шага.
            UpdateAir(v, body, v.Position, v.Velocity, spin);
            var drag = Drag(body, spin, nose, g, v.Position, v.Velocity, out _, out _, out double sinA);
            var nonGrav = thrustAcc + drag / mass;
            v.GForce = nonGrav.magnitude / Constants.G0;

            // Осадка топлива: продольное ускорение вперёд прижимает топливо к заборникам (GDD §6.3).
            if (Vector3d.Dot(nonGrav, nose) > 0.05) v.SettledTimer = 3;
            else v.SettledTimer = Math.Max(0, v.SettledTimer - dt);

            CheckStructure(v, g, sinA, leadDef, dt);
            if (v.Alive) CheckContact(v, body, spin, g, t + dt);
        }

        static Vector3d Accel(CelestialBody body, Vector3d spin, Vector3d nose, Geometry g, Vector3d thrustAcc,
            Vector3d r, Vector3d u)
        {
            double rm = r.magnitude;
            var a = r * (-body.Mu / (rm * rm * rm)) + thrustAcc;
            if (body.Atmosphere != null)
                a += Drag(body, spin, nose, g, r, u, out _, out _, out _) / g.Mass;
            return a;
        }

        /// <summary>Сила сопротивления, Н. Площадь смешивается между лобовой и боковой по углу атаки.</summary>
        static Vector3d Drag(CelestialBody body, Vector3d spin, Vector3d nose, Geometry g, Vector3d r, Vector3d u,
            out double q, out double mach, out double sinA)
        {
            q = mach = sinA = 0;
            var atm = body.Atmosphere;
            if (atm == null) return Vector3d.zero;
            double alt = r.magnitude - body.Radius;
            if (alt >= atm.Top) return Vector3d.zero;
            atm.Sample(alt, out _, out double rho, out double temp);
            var vAir = u - Vector3d.Cross(spin, r);
            double sp = vAir.magnitude;
            if (sp < 1e-6 || rho <= 0) return Vector3d.zero;
            q = 0.5 * rho * sp * sp;
            mach = sp / atm.SpeedOfSound(temp);
            double cosA = Vector3d.Dot(nose, vAir) / sp;
            sinA = Math.Sqrt(Math.Max(0, 1 - cosA * cosA));
            double cd = MathD.Interp(CdMach, CdValue, mach) * g.DragScale;
            double front = Math.PI * g.Radius * g.Radius;
            double side = g.Length * 2 * g.Radius * 0.8;
            double cdA = cd * front * cosA * cosA + 1.2 * side * sinA * sinA + g.ChuteCdA;
            return vAir * (-q * cdA / sp);
        }

        /// <summary>Давление, плотность, скорость звука, нагрев и скорости относительно поверхности.</summary>
        static void UpdateAir(Vessel v, CelestialBody body, Vector3d r, Vector3d u, Vector3d spin)
        {
            double rm = r.magnitude;
            var up = r / rm;
            v.Altitude = rm - body.Radius;
            var vSurf = u - Vector3d.Cross(spin, r);
            v.SurfaceSpeed = vSurf.magnitude;
            v.VerticalSpeed = Vector3d.Dot(u, up);
            v.HorizontalSpeed = Vector3d.ProjectOnPlane(vSurf, up).magnitude;
            v.StaticPressure = v.Density = v.DynamicPressure = v.Mach = v.HeatFlux = 0;
            v.AngleOfAttack = 0;
            var atm = body.Atmosphere;
            if (atm == null || v.Altitude >= atm.Top) return;
            atm.Sample(v.Altitude, out double p, out double rho, out double temp);
            v.StaticPressure = p;
            v.Density = rho;
            double sp = v.SurfaceSpeed;
            v.DynamicPressure = 0.5 * rho * sp * sp;
            v.Mach = sp / atm.SpeedOfSound(temp);
            if (sp > 1e-3) v.AngleOfAttack = Vector3d.Angle(v.NoseP, vSurf) * Constants.Rad2Deg;
        }

        /// <summary>Капсула со смещённым центром масс сама разворачивается теплозащитой вперёд.</summary>
        static Vector3d CapsuleAeroTorque(Vessel v, Vector3d vAirP, Geometry g, Vector3d inertia)
        {
            if (v.DynamicPressure <= 0) return Vector3d.zero;
            var bottom = v.Design.Sections[v.BottomSection()];
            if (bottom.Kind != SectionKind.Capsule) return Vector3d.zero;
            var flowLocal = v.WorldToLocal(vAirP);
            if (flowLocal.sqrMagnitude < 1e-6) return Vector3d.zero;
            // Нос (+Y) тянется против набегающего потока: днище вперёд.
            var target = -flowLocal.normalized;
            var axis = Vector3d.Cross(Vector3d.up, target);
            double s = axis.magnitude;
            double ang = Math.Atan2(s, Vector3d.Dot(Vector3d.up, target));
            double area = Math.PI * g.Radius * g.Radius;
            double k = 0.05 * v.DynamicPressure * area * 2 * g.Radius;
            var restoring = s > 1e-9 ? axis / s * (k * Math.Sin(ang * 0.5) * 2) : Vector3d.zero;
            // Аэродемпфирование, иначе капсула раскачивается вечно.
            var w = v.AngularVelocity;
            double damp = 4 * Math.Sqrt(k * inertia.x);
            return restoring - new Vector3d(w.x * damp, 0, w.z * damp);
        }

        static void CheckStructure(Vessel v, Geometry g, double sinA, SectionDef lead, double dt)
        {
            double q = v.DynamicPressure;
            if (q <= 0) return;

            // Поперечная нагрузка ломает только длинный пакет; шар капсулы ей не подвержен.
            if (AeroBreakup && QAlphaExceeded(q, sinA, g.Length, g.Radius))
            {
                v.Destroy($"Разрушение от аэродинамической нагрузки: q = {q / 1000:F1} кПа, α = {v.AngleOfAttack:F0}°");
                return;
            }

            for (int i = 0; i < v.Attached.Length; i++)
            {
                if (!v.Attached[i] || !v.ChuteDeployed[i] || v.ChuteFailed[i]) continue;
                v.ChuteOpenTime[i] += dt;
                if (q > ChuteMaxQ)
                {
                    v.ChuteFailed[i] = true;
                    v.Raise($"Парашют сорван: скоростной напор {q / 1000:F1} кПа");
                }
            }

            double sp = v.SurfaceSpeed;
            v.HeatFlux = SuttonGraves * Math.Sqrt(v.Density / Math.Max(0.5, lead.Radius)) * sp * sp * sp;
            if (v.HeatFlux > lead.MaxHeatFlux) v.OverheatTimer += dt * v.HeatFlux / lead.MaxHeatFlux;
            else v.OverheatTimer = Math.Max(0, v.OverheatTimer - dt * 0.5);
            // Без правила таймер упирается в предел: HUD показывает «перегрев 100 %», борт цел.
            if (!HeatDamage) v.OverheatTimer = Math.Min(v.OverheatTimer, OverheatTolerance);
            else if (v.OverheatTimer > OverheatTolerance)
                v.Destroy($"Сгорел в атмосфере: тепловой поток {v.HeatFlux / 1e6:F2} МВт/м² ({lead.Name})");
        }

        static void CheckContact(Vessel v, CelestialBody body, Vector3d spin, Geometry g, double t)
        {
            var r = v.Position;
            double rm = r.magnitude;
            var up = r / rm;
            // Грубый отсев: выше любого рельефа — не считаем шум.
            double maxTerrain = body.Terrain != null ? body.Terrain.Amplitude * 1.5 : 0;
            if (rm - body.Radius > maxTerrain + g.Length + 100)
            {
                v.TerrainAltitude = rm - body.Radius;
                return;
            }
            var o = body.OrientationAt(t);
            var bf = o.Inverse * r;
            double h = body.SurfaceHeight(bf);
            double cosUp = Math.Abs(Vector3d.Dot(v.NoseP, up));
            double offset = g.Com * cosUp + g.Radius * Math.Sqrt(Math.Max(0, 1 - cosUp * cosUp));
            v.TerrainAltitude = rm - body.Radius - h - offset;
            if (v.TerrainAltitude > 0) return;

            if (body.IsGasGiant || body.Parent == null)
            {
                v.Destroy($"Раздавлен в глубинах атмосферы: {body.Name}");
                return;
            }
            var vSurf = v.Velocity - Vector3d.Cross(spin, r);
            double speed = vSurf.magnitude;
            if (speed > CrashSpeed)
            {
                v.Destroy($"Удар о поверхность: {body.Name}, {speed:F0} м/с");
                return;
            }
            bool water = body.Terrain != null && body.Terrain.Ocean && h <= 0 &&
                         Terrain.RawHeight(body.Terrain, bf.normalized) < 0;
            var snapped = up * (body.Radius + h + offset);
            v.Situation = water ? Situation.Splashed : Situation.Landed;
            v.AnchorBodyFixed = o.Inverse * snapped;
            v.AttitudeBodyFixed = o.SwapYZ.Inverse * v.Attitude;
            v.TerrainAltitude = 0;
            UpdateLandedPose(v, t);
            for (int i = 0; i < v.Attached.Length; i++)
                if (v.ChuteDeployed[i]) v.ChuteFailed[i] = true; // купол лёг на землю
            v.Raise(water ? $"Приводнение: {speed:F1} м/с" : $"Посадка: {body.Name}, {speed:F1} м/с");
        }
    }
}
