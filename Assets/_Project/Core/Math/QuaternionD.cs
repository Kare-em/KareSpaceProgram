using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Кватернион двойной точности. Алгебра (произведение Гамильтона, поворот вектора, LookRotation)
    /// совпадает с UnityEngine.Quaternion один в один, поэтому ориентацию корабля можно вести здесь,
    /// а в Unity отдавать покомпонентно.
    /// </summary>
    [Serializable]
    public struct QuaternionD
    {
        public double x, y, z, w;

        public QuaternionD(double x, double y, double z, double w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static readonly QuaternionD identity = new QuaternionD(0, 0, 0, 1);

        public static QuaternionD operator *(QuaternionD a, QuaternionD b) => new QuaternionD(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
            a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public static Vector3d operator *(QuaternionD q, Vector3d v)
        {
            double x2 = q.x * 2, y2 = q.y * 2, z2 = q.z * 2;
            double xx = q.x * x2, yy = q.y * y2, zz = q.z * z2;
            double xy = q.x * y2, xz = q.x * z2, yz = q.y * z2;
            double wx = q.w * x2, wy = q.w * y2, wz = q.w * z2;
            return new Vector3d(
                (1 - (yy + zz)) * v.x + (xy - wz) * v.y + (xz + wy) * v.z,
                (xy + wz) * v.x + (1 - (xx + zz)) * v.y + (yz - wx) * v.z,
                (xz - wy) * v.x + (yz + wx) * v.y + (1 - (xx + yy)) * v.z);
        }

        public QuaternionD Inverse => new QuaternionD(-x, -y, -z, w);

        public QuaternionD Normalized
        {
            get
            {
                double m = Math.Sqrt(x * x + y * y + z * z + w * w);
                return m < 1e-300 ? identity : new QuaternionD(x / m, y / m, z / m, w / m);
            }
        }

        /// <summary>
        /// Тот же поворот, записанный в системе с переставленными y↔z (физика ↔ Unity).
        /// Перестановка осей — отражение, поэтому векторная часть зеркалится и меняет знак:
        /// q' = (−x, −z, −y, w). Проверено на поворотах вокруг x и z (см. Tools/CoreTests).
        /// </summary>
        public QuaternionD SwapYZ => new QuaternionD(-x, -z, -y, w);

        /// <summary>Поворот на angle радиан вокруг оси axis.</summary>
        public static QuaternionD AngleAxis(double angle, Vector3d axis)
        {
            var n = axis.normalized;
            double s = Math.Sin(angle * 0.5);
            return new QuaternionD(n.x * s, n.y * s, n.z * s, Math.Cos(angle * 0.5));
        }

        /// <summary>Поворот, переводящий +Z в forward и +Y (насколько возможно) в up — как в Unity.</summary>
        public static QuaternionD LookRotation(Vector3d forward, Vector3d up)
        {
            var f = forward.normalized;
            var r = Vector3d.Cross(up, f);
            if (r.sqrMagnitude < 1e-20) r = Vector3d.AnyPerpendicular(f);
            r = r.normalized;
            var u = Vector3d.Cross(f, r);
            return FromBasis(r, u, f);
        }

        /// <summary>Кватернион по столбцам матрицы поворота: образы осей X, Y, Z.</summary>
        public static QuaternionD FromBasis(Vector3d xAxis, Vector3d yAxis, Vector3d zAxis)
        {
            double m00 = xAxis.x, m01 = yAxis.x, m02 = zAxis.x;
            double m10 = xAxis.y, m11 = yAxis.y, m12 = zAxis.y;
            double m20 = xAxis.z, m21 = yAxis.z, m22 = zAxis.z;
            double trace = m00 + m11 + m22;
            QuaternionD q;
            if (trace > 0)
            {
                double s = Math.Sqrt(trace + 1.0) * 2;
                q = new QuaternionD((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
                q = new QuaternionD(0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                double s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
                q = new QuaternionD((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                double s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
                q = new QuaternionD((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s);
            }
            return q.Normalized;
        }

        /// <summary>Кратчайший поворот from → to.</summary>
        public static QuaternionD FromToRotation(Vector3d from, Vector3d to)
        {
            var a = from.normalized;
            var b = to.normalized;
            double d = Vector3d.Dot(a, b);
            if (d > 1 - 1e-15) return identity;
            if (d < -1 + 1e-15) return AngleAxis(Math.PI, Vector3d.AnyPerpendicular(a));
            return AngleAxis(Math.Acos(Math.Max(-1, Math.Min(1, d))), Vector3d.Cross(a, b));
        }

        /// <summary>Угол поворота (0…π) и ось. Для единичного кватерниона.</summary>
        public void ToAngleAxis(out double angle, out Vector3d axis)
        {
            var q = w < 0 ? new QuaternionD(-x, -y, -z, -w) : this;
            double s = Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            angle = 2 * Math.Atan2(s, q.w);
            axis = s > 1e-15 ? new Vector3d(q.x / s, q.y / s, q.z / s) : Vector3d.up;
        }

        /// <summary>Вектор поворота (ось × угол), удобен как ошибка ориентации для регулятора.</summary>
        public Vector3d ToRotationVector()
        {
            ToAngleAxis(out double angle, out Vector3d axis);
            return axis * angle;
        }

        /// <summary>Интегрирование: угловая скорость задана в связанных осях (рад/с).</summary>
        public QuaternionD IntegrateBody(Vector3d omegaBody, double dt)
        {
            double a = omegaBody.magnitude * dt;
            if (a < 1e-15) return this;
            return (this * AngleAxis(a, omegaBody)).Normalized;
        }

        public static double Angle(QuaternionD a, QuaternionD b)
        {
            double d = Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
            return 2 * Math.Acos(Math.Min(1, d));
        }

        public static QuaternionD Slerp(QuaternionD a, QuaternionD b, double t)
        {
            double d = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (d < 0)
            {
                b = new QuaternionD(-b.x, -b.y, -b.z, -b.w);
                d = -d;
            }
            if (d > 0.9995)
                return new QuaternionD(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t,
                    a.w + (b.w - a.w) * t).Normalized;
            double th = Math.Acos(d);
            double s = Math.Sin(th);
            double ka = Math.Sin((1 - t) * th) / s, kb = Math.Sin(t * th) / s;
            return new QuaternionD(a.x * ka + b.x * kb, a.y * ka + b.y * kb, a.z * ka + b.z * kb, a.w * ka + b.w * kb);
        }

        public override string ToString() => $"({x:F5}, {y:F5}, {z:F5}, {w:F5})";
    }
}
