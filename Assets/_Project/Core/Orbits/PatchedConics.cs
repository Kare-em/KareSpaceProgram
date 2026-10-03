using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    public enum TransitionType
    {
        None,
        /// <summary>Выход из сферы влияния к родителю.</summary>
        Escape,
        /// <summary>Вход в сферу влияния спутника тела.</summary>
        Encounter,
        /// <summary>Вход в атмосферу или опускание к рельефу — дальше только полная физика.</summary>
        Atmosphere,
    }

    public struct Transition
    {
        public TransitionType Type;
        public double Time;
        public CelestialBody NewBody;

        public static readonly Transition None = new Transition { Type = TransitionType.None, Time = double.PositiveInfinity };
    }

    /// <summary>Участок прогноза траектории: одна коника в системе одного тела.</summary>
    public sealed class OrbitPatch
    {
        public CelestialBody Body;
        public KeplerOrbit Orbit;
        public double StartTime, EndTime;
        public TransitionType EndType;
        public CelestialBody NextBody;
        /// <summary>Участок начинается после манёвра.</summary>
        public bool AfterNode;
    }

    /// <summary>
    /// Склейка коник (GDD §2.5): поиск событий смены сферы влияния и прогноз на несколько участков.
    /// Аналитика + консервативный шаг — работает при любом ускорении времени.
    /// </summary>
    public static class PatchedConics
    {
        const double BisectTolerance = 1e-3;
        const int MaxSearchSteps = 20000;

        /// <summary>
        /// Радиус, ниже которого корабль не может быть на рельсах: верх атмосферы, а у безатмосферных —
        /// запас над самыми высокими горами (иначе на ускорении пролетим сквозь рельеф).
        /// </summary>
        public static double RailsFloorRadius(CelestialBody body)
        {
            if (body.HasAtmosphere) return body.Radius + body.AtmosphereTop;
            double amp = body.Terrain?.Amplitude ?? 0;
            return body.Radius + Math.Max(10000, 1.5 * amp + 2000);
        }

        /// <summary>Ближайшее событие на орбите после t0, не дальше t0 + horizon.</summary>
        public static Transition FindNext(KeplerOrbit orbit, CelestialBody body, double t0, double horizon)
        {
            var best = Transition.None;
            double tEnd = t0 + horizon;

            if (body.Parent != null && !double.IsInfinity(body.SoiRadius) && orbit.ApoapsisRadius > body.SoiRadius)
            {
                double te = orbit.NextTimeAtRadius(body.SoiRadius, t0, true);
                if (!double.IsNaN(te) && te < best.Time)
                    best = new Transition { Type = TransitionType.Escape, Time = te, NewBody = body.Parent };
            }

            double floor = RailsFloorRadius(body);
            if (orbit.PeriapsisRadius < floor)
            {
                double ta = orbit.NextTimeAtRadius(floor, t0, false);
                if (!double.IsNaN(ta) && ta < best.Time)
                    best = new Transition { Type = TransitionType.Atmosphere, Time = ta, NewBody = body };
            }

            double searchEnd = Math.Min(tEnd, best.Time);
            foreach (var c in body.Children)
            {
                if (!CanReach(orbit, body, c, t0)) continue;
                double te = FindEncounter(orbit, c, t0, searchEnd);
                if (!double.IsNaN(te) && te < best.Time)
                {
                    best = new Transition { Type = TransitionType.Encounter, Time = te, NewBody = c };
                    searchEnd = te;
                }
            }
            return best;
        }

        /// <summary>Грубый отсев: пересекаются ли кольца расстояний корабля и спутника (с запасом на его SOI).</summary>
        static bool CanReach(KeplerOrbit orbit, CelestialBody body, CelestialBody c, double t)
        {
            var co = c.OrbitAt(t);
            double cMin, cMax;
            if (co != null)
            {
                cMin = co.PeriapsisRadius;
                cMax = co.ApoapsisRadius;
            }
            else
            {
                c.LocalStateAt(t, out var r, out _);
                cMin = cMax = r.magnitude;
            }
            double soi = c.SoiRadius;
            double vMax = Math.Min(orbit.ApoapsisRadius, double.IsInfinity(body.SoiRadius) ? double.MaxValue : body.SoiRadius);
            return orbit.PeriapsisRadius <= cMax + soi && vMax >= cMin - soi;
        }

        /// <summary>
        /// Первый вход в SOI спутника на [t0, tEnd]. Консервативный шаг: за dt расстояние меняется не
        /// больше чем на (|v_к| + |v_c|)·dt, поэтому половину зазора можно перешагнуть, не проскочив сферу.
        /// </summary>
        public static double FindEncounter(KeplerOrbit orbit, CelestialBody c, double t0, double tEnd)
        {
            double soi = c.SoiRadius;
            double t = t0;
            double minStep = Math.Max(1, 1e-6 * Math.Min(orbit.Period, 1e9));
            double prev = t;
            // Сразу после выхода из сферы корабль стоит на её границе: сначала дать ему выйти,
            // иначе граница засчитается как новый вход. Только если расстояние растёт: на подлёте в нескольких км от
            // границы (под автоускорением шаг сюда и попадает) «выход» проскакивал вход в сферу Луны целиком.
            bool leaving = Gap(orbit, c, t, out _) < soi * 1.0001 && Receding(orbit, c, t);
            for (int i = 0; i < MaxSearchSteps && t <= tEnd; i++)
            {
                double d = Gap(orbit, c, t, out double closing);
                if (d < soi && !leaving) return Bisect(orbit, c, prev, t);
                if (leaving && d > soi * 1.0001) leaving = false;
                prev = t;
                t += leaving ? Math.Max(minStep, 60) : Math.Max(minStep, 0.5 * (d - soi) / closing);
            }
            return double.NaN;
        }

        /// <summary>Расстояние до спутника растёт (корабль удаляется от него).</summary>
        static bool Receding(KeplerOrbit orbit, CelestialBody c, double t)
        {
            orbit.GetState(t, out var r, out var v);
            c.LocalStateAt(t, out var rc, out var vc);
            return Vector3d.Dot(r - rc, v - vc) > 0;
        }

        static double Gap(KeplerOrbit orbit, CelestialBody c, double t, out double closing)
        {
            orbit.GetState(t, out var r, out var v);
            c.LocalStateAt(t, out var rc, out var vc);
            closing = Math.Max(1e-3, v.magnitude + vc.magnitude);
            return (r - rc).magnitude;
        }

        static double Bisect(KeplerOrbit orbit, CelestialBody c, double lo, double hi)
        {
            double soi = c.SoiRadius;
            for (int i = 0; i < 80 && hi - lo > BisectTolerance; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Gap(orbit, c, mid, out _) < soi) hi = mid;
                else lo = mid;
            }
            return hi;
        }

        /// <summary>Перевести состояние из системы тела from в систему тела to на момент t.</summary>
        public static void ChangeFrame(CelestialBody from, CelestialBody to, double t, ref Vector3d r, ref Vector3d v)
        {
            if (from == to) return;
            if (to.Parent == from)
            {
                to.LocalStateAt(t, out var rc, out var vc);
                r -= rc;
                v -= vc;
                return;
            }
            if (from.Parent == to)
            {
                from.LocalStateAt(t, out var rc, out var vc);
                r += rc;
                v += vc;
                return;
            }
            from.StateAt(t, out var rf, out var vf);
            to.StateAt(t, out var rt, out var vt);
            r += rf - rt;
            v += vf - vt;
        }

        /// <summary>Горизонт поиска для орбиты: период у эллипса (не больше года), полгода у гиперболы.</summary>
        public static double DefaultHorizon(KeplerOrbit orbit)
        {
            const double year = 365.25 * 86400;
            return orbit.IsElliptic ? Math.Min(orbit.Period * 1.05, 2 * year) : year / 2;
        }

        /// <summary>
        /// Прогноз траектории на несколько участков с учётом манёвра (импульсно, в момент узла).
        /// Возвращает пустой список, если корабль на поверхности.
        /// </summary>
        public static List<OrbitPatch> Predict(CelestialBody body, Vector3d r, Vector3d v, double t,
            ManeuverNode node = null, int maxPatches = 4)
        {
            var list = new List<OrbitPatch>();
            bool nodePending = node != null && node.Time > t && node.Total > 1e-3;
            bool afterNode = false;
            for (int k = 0; k < maxPatches; k++)
            {
                var orbit = KeplerOrbit.FromState(r, v, body.Mu, t);
                var next = FindNext(orbit, body, t, DefaultHorizon(orbit));
                var patch = new OrbitPatch
                {
                    Body = body, Orbit = orbit, StartTime = t, AfterNode = afterNode,
                    EndTime = next.Time, EndType = next.Type, NextBody = next.NewBody,
                };
                if (nodePending && node.Time < next.Time)
                {
                    // Манёвр раньше события: режем участок, применяем импульс, ищем заново.
                    patch.EndTime = node.Time;
                    patch.EndType = TransitionType.None;
                    patch.NextBody = body;
                    list.Add(patch);
                    orbit.GetState(node.Time, out r, out v);
                    v += node.Remaining;
                    t = node.Time;
                    nodePending = false;
                    afterNode = true;
                    continue;
                }
                list.Add(patch);
                if (next.Type != TransitionType.Escape && next.Type != TransitionType.Encounter) break;
                orbit.GetState(next.Time, out r, out v);
                ChangeFrame(body, next.NewBody, next.Time, ref r, ref v);
                body = next.NewBody;
                t = next.Time;
            }
            return list;
        }
    }
}
