using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Старт перед посадкой (GDD §10.2, тренировка посадки): борт — только верхняя секция проекта (орбитер, корабль Starship),
    /// уже в воздухе над целью посадки. Цель — полоса из ObjectiveType.Runway или RecoveryDef секции (башня, баржа).
    /// Расстояния — от цели назад по курсу подхода, скорость — воздушная (относительно вращающейся атмосферы).
    /// </summary>
    public sealed class ApproachStart
    {
        /// <summary>Высота над целью, м, и дальность до неё по горизонтали, м (назад по курсу подхода).</summary>
        public double Altitude, Range;
        /// <summary>Боковое смещение от оси подхода, м (плюс — вправо по курсу): крылатому — заход по конусу, а не в лоб.</summary>
        public double Cross;
        /// <summary>Воздушная скорость, м/с, и угол траектории, ° (минус — вниз).</summary>
        public double Speed, PathDeg;
        /// <summary>Угол атаки, °: нос над вектором скорости в плоскости траектории. 90 — «брюхом» (Starship).</summary>
        public double AlphaDeg;
        /// <summary>Курс подхода, ° от севера; NaN — по оси полосы (крылатый) или с запада на восток.</summary>
        public double HeadingDeg = double.NaN;
        /// <summary>Топливо секции на старте, кг; NaN — как в проекте.</summary>
        public double Propellant = double.NaN;

        /// <summary>
        /// Ставит активный борт в воздух по описанию миссии. Проект урезается до верхней секции ещё до Launch (вызывает
        /// MissionTracker.CreateUniverse), здесь — только положение, скорость и ориентация.
        /// </summary>
        public static void Place(Universe u, Vessel v, MissionDef def)
        {
            var a = def.Approach;
            var body = v.Body;
            double t = u.Time;
            int idx = v.Attached.Length - 1;
            var s = v.Design.Sections[idx];

            // Цель и курс подхода в осях тела.
            double lat, lon, heading = a.HeadingDeg;
            var rwObj = def.Objectives.Find(o => o.Type == ObjectiveType.Runway);
            var runway = rwObj != null ? Runways.Get(rwObj.Site) : null;
            if (runway != null && runway.Site != null)
            {
                lat = runway.Site.Latitude;
                lon = runway.Site.Longitude;
                if (double.IsNaN(heading)) heading = runway.Heading;
            }
            else if (s.Recovery != null)
            {
                lat = s.Recovery.TargetLat;
                lon = s.Recovery.TargetLon;
                if (double.IsNaN(heading)) heading = 90;
            }
            else throw new ArgumentException($"Миссия {def.Id}: нет цели посадки для старта в воздухе");

            var up0 = CelestialBody.LatLonToBodyFixed(lat, lon);
            // Высота — от рельефа в точке цели (площадка выровнена под отметку космодрома, Terrain.Height).
            double elevation = body.SurfaceHeight(up0);
            var east0 = Vector3d.Cross(Vector3d.forward, up0).normalized;
            var north0 = Vector3d.Cross(up0, east0);
            double hd = heading * Constants.Deg2Rad;
            var fwd0 = north0 * Math.Cos(hd) + east0 * Math.Sin(hd);
            var right0 = Vector3d.Cross(fwd0, up0);
            // Точка старта: назад по курсу на Range и вбок на Cross — по дуге (дальности до сотни км, кривизна заметна).
            var off = -fwd0 * a.Range + right0 * a.Cross;
            double ang = off.magnitude / body.Radius;
            var dirBf = ang > 1e-12 ? (up0 * Math.Cos(ang) + off.normalized * Math.Sin(ang)).normalized : up0;
            // Курс в точке старта — на цель (большой круг), Cross берёт на себя автопилот захода.
            var upS = dirBf;
            var toTarget = Vector3d.ProjectOnPlane(up0 - upS, upS);
            var fwdS = a.Range > 1 ? toTarget.normalized : Vector3d.ProjectOnPlane(fwd0, upS).normalized;

            var o = body.OrientationAt(t);
            var up = o * upS;
            var fwd = o * fwdS;
            var pos = up * (body.Radius + elevation + a.Altitude);
            double gamma = a.PathDeg * Constants.Deg2Rad;
            var vAirDir = (fwd * Math.Cos(gamma) + up * Math.Sin(gamma)).normalized;

            // Ориентация: нос (+Y) — над скоростью на α в плоскости траектории, брюхо (+X) — к потоку (вниз).
            double alpha = a.AlphaDeg * Constants.Deg2Rad;
            var lift = Vector3d.Cross(Vector3d.Cross(vAirDir, up), vAirDir).normalized; // перпендикуляр к скорости, вверх
            var nose = (vAirDir * Math.Cos(alpha) + lift * Math.Sin(alpha)).normalized;
            var belly = (vAirDir * Math.Sin(alpha) - lift * Math.Cos(alpha)).normalized;
            // Как в FlightPhysics.PlaceOnSurface: Z = −(X × Y) в осях P, затем SwapYZ.
            var z = -Vector3d.Cross(belly, nose);
            v.Attitude = QuaternionD.FromBasis(belly.SwapYZ, nose.SwapYZ, z.SwapYZ);

            v.Situation = Situation.Flying;
            v.Position = pos;
            v.Velocity = vAirDir * a.Speed + Vector3d.Cross(FlightPhysics.SpinAxis(body, t), pos);
            v.AngularVelocity = Vector3d.zero;
            v.LaunchTime = t;
            v.Throttle = 0;
            v.Sas = SasMode.Off;
            if (!double.IsNaN(a.Propellant)) v.Propellant[idx] = Math.Min(a.Propellant, s.Propellant);
            // Двигатель готов к запуску газом (Z): ступень уже «в работе», как после разделения.
            if (s.HasEngine) v.Armed[idx] = true;
            FlightPhysics.UpdateTelemetry(v, t);
            u.Post($"{v.Name}: {a.Altitude / 1000:F1} км, {a.Speed:F0} м/с, до цели {a.Range / 1000:F1} км");
        }

        /// <summary>Проект из одной верхней секции (орбитер без бака, корабль без Super Heavy). Последовательность пустая.</summary>
        public static VesselDesign TopOnly(VesselDesign d)
        {
            var top = d.Sections[d.Sections.Count - 1].Clone();
            var r = new VesselDesign { Name = top.Name };
            r.Sections.Add(top);
            return r;
        }
    }
}
