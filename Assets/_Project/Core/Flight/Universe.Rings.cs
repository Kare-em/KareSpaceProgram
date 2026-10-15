using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    public sealed partial class Universe
    {
        /// <summary>Где был борт в конце прошлого кадра: тело, положение, время — для пересечения плоскости колец.</summary>
        readonly Dictionary<Vessel, (CelestialBody body, Vector3d pos, double time)> ringPrev =
            new Dictionary<Vessel, (CelestialBody, Vector3d, double)>();

        /// <summary>
        /// Пересечение плоскости плотных колец (Rings, §2) — раз в кадр, после физики или рельсов. На рельсах кадр —
        /// это сотни секунд и больше, поэтому радиус пересечения берётся не по хорде, а из конического сечения: узел
        /// орбиты на экваторе, r = p / (1 + e·cos ν). Прошло полвитка и больше — проверяются оба узла (знак высоты над
        /// плоскостью мог вернуться к прежнему). Ось колец — ось вращения тела: кольца лежат в экваторе (у Урана тоже).
        /// </summary>
        void CheckRings()
        {
            if (ringPrev.Count > Vessels.Count) ringPrev.Clear(); // погибшие борта удалены из Vessels — не копим ключи
            foreach (var v in Vessels)
            {
                var body = v.Body;
                if (!v.Alive || v.IsLanded || !Rings.HasDense(body)) { ringPrev.Remove(v); continue; }
                bool had = ringPrev.TryGetValue(v, out var prev) && prev.body == body;
                ringPrev[v] = (body, v.Position, Time);
                if (!had) continue;

                var axis = RingAxis(body);
                double z0 = Vector3d.Dot(prev.pos, axis), z1 = Vector3d.Dot(v.Position, axis);
                var h = Vector3d.Cross(v.Position, v.Velocity);
                var node = Vector3d.Cross(axis, h); // к восходящему узлу (снизу вверх по оси)
                if (node.sqrMagnitude < 1e-6 * h.sqrMagnitude) continue; // орбита в самой плоскости колец
                node = node.normalized;
                double mu = body.Mu;
                double p = h.sqrMagnitude / mu;
                var ev = Vector3d.Cross(v.Velocity, h) / mu - v.Position.normalized;
                double energy = 0.5 * v.Velocity.sqrMagnitude - mu / v.Position.magnitude;
                double period = energy < 0 ? 2 * Math.PI * Math.Sqrt(Math.Pow(-mu / (2 * energy), 3) / mu) : double.PositiveInfinity;

                string hit = null;
                double rHit = 0;
                if (Time - prev.time >= 0.5 * period)
                {
                    hit = NodeHit(body, p, ev, node, out rHit) ?? NodeHit(body, p, ev, -node, out rHit);
                }
                else if ((z0 < 0) != (z1 < 0))
                    hit = NodeHit(body, p, ev, z1 > z0 ? node : -node, out rHit);
                if (hit == null) continue;
                double speed = Math.Sqrt(Math.Max(0, 2 * (energy + mu / rHit)));
                v.Destroy($"Столкновение с кольцами: {body.Name}, {hit}, {speed / 1000:0.0} км/с");
                if (v == Active) SetWarp(0);
            }
        }

        /// <summary>Плотная полоса в узле dir (единичный, в плоскости колец); null — узел недостижим или пусто.</summary>
        static string NodeHit(CelestialBody body, double p, Vector3d ev, Vector3d dir, out double r)
        {
            double den = 1 + Vector3d.Dot(dir, ev); // 1 + e·cos ν
            r = den > 1e-9 ? p / den : double.PositiveInfinity; // ветвь гиперболы туда не идёт
            return double.IsInfinity(r) ? null : Rings.DenseAt(body, r);
        }

        static Vector3d RingAxis(CelestialBody body) =>
            body.AngularVelocity.sqrMagnitude > 0 ? body.AngularVelocity.normalized : body.Orientation * Vector3d.forward;
    }
}
