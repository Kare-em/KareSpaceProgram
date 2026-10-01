using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Окна старта (GDD §6.2): момент, когда плоскость опорной орбиты при пуске с площадки под заданным
    /// азимутом проходит через положение цели к моменту прибытия. Тогда разгон к цели — в пределах
    /// витка и без поворота плоскости, а не через дни ожидания на орбите, пока цель войдёт в плоскость.
    /// </summary>
    public static class LaunchWindow
    {
        const double ScanStep = 600;

        /// <param name="flightTime">От старта до прибытия к цели, с: выведение + полвитка + перелёт.</param>
        /// <returns>Момент старта или NaN, если за span окна нет.</returns>
        public static double NextPlaneWindow(CelestialBody body, LaunchSite site, double azimuthDeg,
            CelestialBody target, double t0, double flightTime, double span = 2 * 86400)
        {
            double tPrev = t0, fPrev = PlaneError(body, site, azimuthDeg, target, t0, flightTime);
            for (double t = t0 + ScanStep; t <= t0 + span; t += ScanStep)
            {
                double f = PlaneError(body, site, azimuthDeg, target, t, flightTime);
                if (Math.Sign(f) != Math.Sign(fPrev))
                {
                    double lo = tPrev, hi = t, flo = fPrev;
                    for (int i = 0; i < 40 && hi - lo > 0.5; i++)
                    {
                        double mid = 0.5 * (lo + hi), fm = PlaneError(body, site, azimuthDeg, target, mid, flightTime);
                        if (Math.Sign(fm) == Math.Sign(flo))
                        {
                            lo = mid;
                            flo = fm;
                        }
                        else hi = mid;
                    }
                    return 0.5 * (lo + hi);
                }
                tPrev = t;
                fPrev = f;
            }
            return double.NaN;
        }

        /// <summary>
        /// Синус угла между направлением на цель (в момент t + flightTime) и плоскостью орбиты при старте
        /// в момент t. Плоскость задают зенит площадки и курс: вращение Земли добавляет скорость на восток
        /// и при азимуте 90° плоскость не меняет, при других — сдвигает на доли градуса.
        /// </summary>
        public static double PlaneError(CelestialBody body, LaunchSite site, double azimuthDeg,
            CelestialBody target, double t, double flightTime)
        {
            var q = body.OrientationAt(t);
            var up = q * site.DirectionBodyFixed;
            var north = Vector3d.ProjectOnPlane(q * Vector3d.forward, up);
            north = north.sqrMagnitude < 1e-12 ? Vector3d.AnyPerpendicular(up) : north.normalized;
            var east = Vector3d.Cross(north, up);
            double az = azimuthDeg * Constants.Deg2Rad;
            var heading = east * Math.Sin(az) + north * Math.Cos(az);
            var normal = Vector3d.Cross(up, heading);
            target.LocalStateAt(t + flightTime, out var rt, out _);
            return Vector3d.Dot(normal, rt.normalized);
        }
    }
}
