using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Наблюдатель полёта карьеры (GDD §8.3): наука за ситуации у тел (каждая — один раз на карьеру) и возврат денег за
    /// целые борта, севшие на Землю (§8.4). Вызывать после каждого шага вселенной, как MissionTracker.Update.
    /// </summary>
    public sealed class CareerObserver
    {
        /// <summary>Базовая наука ситуаций, S (§8.3 V_base). Пара: множители тел BodyFactor.</summary>
        public const double SpaceScience = 2, SoiScience = 4, FlybyScience = 5, OrbitScience = 6, LandedScience = 20, ReturnScience = 6;
        /// <summary>Граница космоса Земли (линия Кармана), м; у других тел — верх атмосферы.</summary>
        public const double Karman = 100e3;
        /// <summary>«Низкий пролёт» — ниже стольких радиусов тела над поверхностью.</summary>
        public const double FlybyRadii = 1;
        /// <summary>Перицентр орбиты Земли не ниже, м (ниже 120 км виток не живёт); у тел с атмосферой — половина её верха.</summary>
        public const double EarthOrbitFloor = 120e3, AirlessOrbitFloor = 5e3;
        /// <summary>Возврат за борт, который поднимался выше этой высоты (взлёт со стола и обратно — не полёт), м.</summary>
        public const double RecoveryMinAltitude = 20e3;

        readonly CareerState state;
        readonly HashSet<int> recovered = new HashSet<int>();
        readonly Dictionary<int, double> maxAltitude = new Dictionary<int, double>();
        /// <summary>Борт побывал в космосе (для науки «возвращение»).</summary>
        readonly HashSet<int> wasInSpace = new HashSet<int>();
        public event Action<string> Message;

        public CareerObserver(CareerState state) => this.state = state;

        /// <summary>Множитель науки тела: дальше и труднее — дороже (§8.3 body_multiplier).</summary>
        public static double BodyFactor(string id)
        {
            switch (id)
            {
                case "earth": return 1;
                case "moon": return 2;
                case "sun": return 3;
                case "venus": case "mars": case "phobos": case "deimos": return 4;
                case "mercury": return 5;
                case "jupiter": return 6;
                case "io": case "europa": case "ganymede": case "callisto": case "saturn": return 7;
                case "titan": case "enceladus": case "uranus": return 9;
                case "neptune": case "triton": return 10;
                case "pluto": case "charon": return 12;
                default: return 6; // кометы и прочее
            }
        }

        public void Update(Universe u)
        {
            foreach (var v in u.Vessels)
            {
                if (!v.Alive || v.Body == null || double.IsNaN(v.LaunchTime)) continue;
                var b = v.Body;
                if (!maxAltitude.TryGetValue(v.Id, out var hmax) || v.Altitude > hmax) maxAltitude[v.Id] = hmax = v.Altitude;
                if (!v.IsDebris) Science(v, b, u);
                Recovery(v, b, hmax);
            }
        }

        void Science(Vessel v, CelestialBody b, Universe u)
        {
            double k = BodyFactor(b.Id);
            bool home = b.Id == "earth";
            if (v.Situation == Situation.Flying)
            {
                double space = home ? Karman : b.AtmosphereTop;
                if (v.Altitude > space)
                {
                    wasInSpace.Add(v.Id);
                    Add(b, "space", SpaceScience * k, $"космос у тела {b.Name}");
                }
                if (!home && b != u.System.Sun) Add(b, "soi", SoiScience * k, $"сфера влияния: {b.Name}");
                if (!home && v.Altitude < FlybyRadii * b.Radius) Add(b, "flyby", FlybyScience * k, $"низкий пролёт: {b.Name}");
                double floor = home ? EarthOrbitFloor : b.HasAtmosphere ? 0.5 * b.AtmosphereTop : AirlessOrbitFloor;
                var o = KeplerOrbit.FromState(v.Position, v.Velocity, b.Mu, u.Time);
                if (o.IsElliptic && o.PeriapsisRadius > b.Radius + floor && o.ApoapsisRadius < b.SoiRadius)
                    Add(b, "orbit", OrbitScience * k, $"орбита: {b.Name}");
            }
            else if (v.IsLanded)
            {
                if (!home) Add(b, "landed", LandedScience * k, $"посадка: {b.Name}");
                else if (wasInSpace.Contains(v.Id) && v.HasCapsule()) Add(b, "return", ReturnScience, "возвращение капсулы из космоса");
            }
        }

        void Add(CelestialBody b, string situation, double amount, string why)
        {
            if (state.AddScience(b.Id + ":" + situation, Math.Round(amount), why))
                Message?.Invoke($"Наука +{Math.Round(amount):F0} S — {why}");
        }

        void Recovery(Vessel v, CelestialBody b, double hmax)
        {
            if (!v.IsLanded || b.Id != "earth" || hmax < RecoveryMinAltitude || recovered.Contains(v.Id)) return;
            recovered.Add(v.Id);
            double value = CareerPricing.RecoveryValue(v);
            if (value <= 0) return;
            state.AddFunds(value, $"Возврат «{v.Name}»");
            Message?.Invoke($"Возврат «{v.Name}»: +{value:F0} M₢");
        }
    }
}
