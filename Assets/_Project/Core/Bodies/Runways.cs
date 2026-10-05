using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Посадочная полоса крылатого борта (GDD §6.4): ровная площадка рельефа (LaunchSite с широкой FlatRadius) и ось с курсом.
    /// Курс — направление посадки, градусы от севера по часовой. Полоса считается в осях тела: вращается вместе с ним.
    /// </summary>
    public sealed class Runway
    {
        public string Id, Name, SiteId;
        public double Heading, Length, Width;
        /// <summary>Порог (начало) полосы — на Length/2 до центра площадки против курса.</summary>
        public double HalfLength => Length * 0.5;

        public LaunchSite Site => SolarSystem.GetSite(SiteId);

        /// <summary>Центр полосы (единичный радиус) и ось посадки в осях тела.</summary>
        public void Frame(out Vector3d up, out Vector3d axis)
        {
            var s = Site;
            up = CelestialBody.LatLonToBodyFixed(s.Latitude, s.Longitude);
            // Те же оси, что у стола (FlightPhysics.PlaceOnSurface): восток = z × up, север = up × восток.
            var east = Vector3d.Cross(Vector3d.forward, up).normalized;
            var north = Vector3d.Cross(up, east);
            double h = Heading * Constants.Deg2Rad;
            axis = north * Math.Cos(h) + east * Math.Sin(h);
        }

        /// <summary>
        /// Точка в осях тела относительно полосы: along — вдоль курса от центра, м; cross — вправо от оси, м.
        /// </summary>
        public void Locate(CelestialBody body, Vector3d bodyFixed, out double along, out double cross)
        {
            Frame(out var up, out var axis);
            var d = bodyFixed.normalized;
            var right = Vector3d.Cross(axis, up);
            // Малые расстояния: дуга по сфере ≈ проекция на касательную плоскость, дальние — через угол.
            double ang = Vector3d.Angle(d, up);
            var t = Vector3d.ProjectOnPlane(d, up);
            double tm = t.magnitude;
            if (tm < 1e-12) { along = cross = 0; return; }
            t /= tm;
            along = Vector3d.Dot(t, axis) * ang * body.Radius;
            cross = Vector3d.Dot(t, right) * ang * body.Radius;
        }
    }

    public static class Runways
    {
        /// <summary>
        /// Полосы посадки. Площадки — ровные круги рельефа (LaunchSite): FlatRadius ≥ Length/2 + запас, иначе конец полосы
        /// уходит в зону сглаживания. Пара: FlatRadius = RunwayFlat ↔ самая длинная полоса 4,6 км.
        /// </summary>
        public const double RunwayFlat = 3500;

        public static readonly List<Runway> All = new List<Runway>
        {
            // Озеро Роджерс, полоса 23: сюда сел STS-1 14.04.1981. Дно высохшего озера ≈ 694 м.
            new Runway { Id = "edwards23", Name = "Эдвардс, полоса 23", SiteId = "edwards", Heading = 236, Length = 4570, Width = 90 },
            // Посадочный комплекс Шаттлов (SLF) у мыса Канаверал, полоса 15.
            new Runway { Id = "slf15", Name = "Мыс Канаверал, SLF 15", SiteId = "slf", Heading = 150, Length = 4570, Width = 91 },
            // Аэродром «Юбилейный» (Байконур), посадка «Бурана» 15.11.1988 — курс ≈ 58° (полоса 06).
            new Runway { Id = "yubileyny", Name = "Юбилейный, ВПП 06", SiteId = "yubileyny", Heading = 58, Length = 4500, Width = 84 },
        };

        public static Runway Get(string id) => All.Find(r => r.Id == id);

        /// <summary>Площадки полос — в общий список рельефа (SolarSystem); стол на них не ставится: он только у Site борта на старте.</summary>
        public static void AddSites(List<LaunchSite> sites)
        {
            sites.Add(new LaunchSite("edwards", "Эдвардс, озеро Роджерс", "earth", 34.935, -117.835, 694) { FlatRadius = RunwayFlat });
            sites.Add(new LaunchSite("slf", "Мыс Канаверал, SLF", "earth", 28.615, -80.695, 3) { FlatRadius = RunwayFlat });
            sites.Add(new LaunchSite("yubileyny", "Байконур, аэродром «Юбилейный»", "earth", 45.958, 63.650, 90) { FlatRadius = RunwayFlat });
        }

        /// <summary>Полоса, на которой стоит точка (в пределах длины и полуширины + запас), или null.</summary>
        public static Runway At(CelestialBody body, Vector3d bodyFixed, double margin = 30)
        {
            foreach (var r in All)
            {
                var s = r.Site;
                if (s == null || s.BodyId != body.Id) continue;
                r.Locate(body, bodyFixed, out double along, out double cross);
                if (Math.Abs(along) <= r.HalfLength + margin && Math.Abs(cross) <= r.Width * 0.5 + margin) return r;
            }
            return null;
        }
    }
}
