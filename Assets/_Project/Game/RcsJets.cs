using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Струи двигателей ориентации (РСУ, GDD §4.9, §9.5): короткие бледные конусы у блоков сопел на корпусе.
    /// Сопла выбираются по команде ядра, а не по клавишам: поступательная тяга (Vessel.RcsTranslate, RcsForward —
    /// осадка §5.3) и момент регулятора (Vessel.TorqueCommand) — так струи видны и у автопилота, и у соседних бортов.
    /// Компонент живёт на объекте VesselView; свой корень — ребёнок вида: вид прячет детей (SetVisible) и при
    /// перестроении удаляет их — тогда корень собирается заново.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RcsJets : MonoBehaviour
    {
        /// <summary>Блоки показываем у секций с моментом РСУ не меньше этой доли наибольшего в связке: у «Союза»
        /// струи на ПАО (1,5·10³), а не ещё и на СА (400) и БО (100) — иначе корабль «дымит» отовсюду.
        /// Пара: у «Аполлона» СМ 4·10³ и КМ 2·10³ — блоки на обоих, у состыкованной МКС (5·10⁴) — только станция.</summary>
        const double ShownShare = 0.3;
        /// <summary>Блоков сопел по окружности и сдвиг от оси +X, °: четыре «квада», как на СМ «Аполлона»/ПАО «Союза».</summary>
        const int Quads = 4;
        const float QuadPhaseDeg = 45;
        /// <summary>Длина струи, м: доля радиуса секции в пределах [Min, Max]. В пустоте видимое ядро струи
        /// холодного газа/гиперголика ≈ 1–2 м, дальше расширение делает её прозрачной.</summary>
        const float LengthPerRadius = 0.7f, LengthMin = 0.35f, LengthMax = 2f;
        /// <summary>В атмосфере струя короче (противодавление): доля длины при p = 1 атм.</summary>
        const float SeaLevelLength = 0.45f;
        /// <summary>Сопло отступает от обшивки на эту долю длины струи, а соседние сопла блока — друг от друга.</summary>
        const float NozzleStandoff = 0.06f;
        /// <summary>Нарастание и спад струи, с: клапан РСУ открывается за десятки мс.</summary>
        const float FadeTime = 0.05f;
        /// <summary>Неполная команда — импульсами (ШИМ, как у настоящих РСУ): частота, Гц, и порог «сплошной»
        /// работы. Частота ниже fps/2 — иначе алиасинг мигания (pitfalls-render, «Мигание Солнца»).</summary>
        const float PulseHz = 4, SolidAbove = 0.85f;
        /// <summary>Мельче этого команда — не струя: SAS в удержании выдаёт шум момента, весь борт мерцал бы.</summary>
        const float Deadband = 0.04f;
        /// <summary>Сопло работает, если косинус выше AlignMin, и в полную силу от AlignFull. Блоки стоят под 45°
        /// к осям, поэтому чистая команда по X/Z даёт косинус 0,707: при полной силе только от 1 струя
        /// получала 0,55 и уходила в импульсы. Пара: QuadPhaseDeg.</summary>
        const float AlignMin = 0.35f, AlignFull = 0.7f;
        /// <summary>Яркость у среза в единицах экрана (вес экспозиции 0, 1 ≈ белый): аддитив, две стенки конуса.
        /// Струя видна по рассеянному свету, поэтому в тени Земли тусклее. Пара: градиент JetGradient (пик ≈ 0,8).
        /// 0,55 на скрине «Союза» с 9 м было не различить (тонкий конус, ~3 пкс у среза) — поднято до 1,8.</summary>
        const float JetScreen = 1.8f, ShadowShare = 0.45f;
        /// <summary>Дрожание длины и яркости, доля; частота как у факела (VesselView.FlickerHz) — без алиасинга.</summary>
        const float Flicker = 0.12f, FlickerHz = 5f;
        static readonly Color JetTint = new Color(1f, 0.97f, 0.9f);

        sealed class Nozzle
        {
            public Transform T;
            public Renderer R;
            public int Section;
            /// <summary>Место и направление выхлопа в осях секции.</summary>
            public Vector3 Pos, Exhaust;
            public float Length, Level, Seed;
        }

        VesselView view;
        Transform root;
        string builtSignature;
        double[] baseHeight;
        readonly List<Nozzle> nozzles = new List<Nozzle>();
        readonly Dictionary<int, Transform> anchors = new Dictionary<int, Transform>();

        static Material jetMat;
        static Mesh jetMesh;
        static MaterialPropertyBlock mpb;

        public void Init(VesselView v, Material plume)
        {
            view = v;
            if (jetMat == null && plume != null)
            {
                // Копия факела (HDRP/Unlit, аддитив, двусторонний, без тумана) со своим градиентом. Ключ ставим руками:
                // рантайм-материалы не валидируются (pitfalls-render, «Конвейер и материалы»).
                jetMat = new Material(plume) { name = "RCS Jet (runtime)" };
                jetMat.SetTexture("_EmissiveColorMap", JetGradient());
                jetMat.EnableKeyword("_EMISSIVE_COLOR_MAP");
                // Вес экспозиции 0: яркость в долях экрана. Струя светит отражённым светом и должна читаться и при
                // EV 14 днём, и при EV −5 ночью; в нитах при одной яркости она либо невидима днём, либо выжигает ночью.
                jetMat.SetFloat("_EmissiveExposureWeight", 0);
                jetMat.SetFloat("_EnableFogOnTransparent", 0);
            }
            if (jetMesh == null) jetMesh = ProcMesh.Plume(0.1f, 4f, 0.7f, 10, 6);
            if (mpb == null) mpb = new MaterialPropertyBlock();
        }

        string Signature()
        {
            var v = view.Vessel;
            var a = v.Attached;
            var c = new char[a.Length + 1];
            for (int i = 0; i < a.Length; i++) c[i] = !a[i] ? '0' : v.Flipped[i] ? 'f' : v.IsEnclosed(i) ? 'e' : '1';
            c[a.Length] = (char)('0' + Mathf.RoundToInt((float)v.RadialYaw * 100) % 64);
            return new string(c);
        }

        void Rebuild()
        {
            if (root != null) Destroy(root.gameObject);
            nozzles.Clear();
            anchors.Clear();
            builtSignature = Signature();
            var v = view.Vessel;
            baseHeight = new double[v.Design.Sections.Count];
            root = new GameObject("RCS Jets").transform;
            root.SetParent(view.transform, false);
            if (jetMat == null) return;

            var secs = v.Design.Sections;
            double max = 0;
            for (int i = 0; i < secs.Count; i++)
                if (Shown(v, i)) max = System.Math.Max(max, secs[i].RcsTorque);
            for (int i = 0; i < secs.Count; i++)
            {
                if (!Shown(v, i) || secs[i].RcsTorque < max * ShownShare) continue;
                var s = secs[i];
                var anchor = new GameObject(s.Name).transform;
                anchor.SetParent(root, false);
                anchors[i] = anchor;
                float len = (float)s.Length, r = (float)s.Radius;
                // Высота блоков и радиус обшивки там: у ступеней и отсеков — у верхнего торца (квады СМ «Аполлона»,
                // ПАО «Союза»), у конуса капсулы — на середине образующей, у шара — на экваторе.
                float y, rr;
                if (s.Kind == SectionKind.Capsule && s.Sphere) { y = len * 0.5f; rr = r; }
                else if (s.Kind == SectionKind.Capsule) { y = len * 0.55f; rr = r * (1 - 0.65f * 0.55f); }
                else { y = Mathf.Max(len * 0.5f, len - Mathf.Min(0.15f * len, 0.7f)); rr = r; }
                float jet = Mathf.Clamp(r * LengthPerRadius, LengthMin, LengthMax);
                for (int q = 0; q < Quads; q++)
                {
                    float a = (q * 360f / Quads + QuadPhaseDeg) * Mathf.Deg2Rad;
                    var n = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    var t = new Vector3(-Mathf.Sin(a), 0, Mathf.Cos(a));
                    var center = n * (rr + jet * NozzleStandoff) + Vector3.up * y;
                    foreach (var ex in new[] { Vector3.up, Vector3.down, t, -t })
                    {
                        var go = new GameObject("Jet");
                        go.transform.SetParent(anchor, false);
                        var pos = center + ex * (jet * NozzleStandoff);
                        go.transform.localPosition = pos;
                        // Меш струи — вниз от нуля: −Y поворачиваем в направление выхлопа.
                        go.transform.localRotation = Quaternion.FromToRotation(Vector3.down, ex);
                        go.AddComponent<MeshFilter>().sharedMesh = jetMesh;
                        var mr = go.AddComponent<MeshRenderer>();
                        mr.sharedMaterial = jetMat;
                        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        mr.receiveShadows = false;
                        mr.enabled = false;
                        nozzles.Add(new Nozzle { T = go.transform, R = mr, Section = i, Pos = pos, Exhaust = ex, Length = jet, Seed = nozzles.Count * 1.37f });
                    }
                }
            }
        }

        static bool Shown(Vessel v, int i)
        {
            var s = v.Design.Sections[i];
            return v.Attached[i] && !v.IsEnclosed(i) && !s.IsRadial && s.Kind != SectionKind.Fairing && s.RcsTorque > 0;
        }

        /// <summary>
        /// Доля работы РСУ по команде ядра, в связанных осях борта (= локальные оси вида): rot — момент по осям
        /// (−1…1 от момента РСУ), trans — поступательная тяга. Момент сначала покрывают качание сопел и рули
        /// (у них запас больше), РСУ — только остаток: на взлёте «семёрки» струй нет, в пустоте без двигателя — все.
        /// </summary>
        public static bool Command(Vessel v, out Vector3 rot, out Vector3 trans)
        {
            rot = trans = Vector3.zero;
            if (v == null || !v.Alive || v.OnRails || v.IsLanded) return false;
            double rcs = 0;
            for (int i = 0; i < v.Attached.Length; i++) if (v.Attached[i]) rcs += v.Design.Sections[i].RcsTorque;
            if (rcs <= 0) return false;
            var tmax = v.MaxTorque(v.StaticPressure);
            var cmd = v.TorqueCommand;
            rot = new Vector3(Share(cmd.x, tmax.x, rcs), Share(cmd.y, tmax.y, rcs), Share(cmd.z, tmax.z, rcs));
            var tr = v.RcsTranslate;
            trans = new Vector3((float)tr.x, (float)(tr.y + v.RcsForward), (float)tr.z);
            if (trans.sqrMagnitude > 1) trans.Normalize();
            return rot.sqrMagnitude > Deadband * Deadband || trans.sqrMagnitude > Deadband * Deadband;
        }

        static float Share(double cmd, double tmax, double rcs)
        {
            double other = System.Math.Max(0, tmax - rcs);
            double rest = System.Math.Max(0, System.Math.Abs(cmd) - other);
            return (float)(System.Math.Sign(cmd) * System.Math.Min(1, rest / rcs));
        }

        /// <summary>Сила шипения РСУ для звука (FlightAudio), 0…1 — та же доля, что зажигает струи.</summary>
        public static float Level(Vessel v)
        {
            if (!Command(v, out var rot, out var trans)) return 0;
            float r = Mathf.Max(Mathf.Abs(rot.x), Mathf.Abs(rot.y), Mathf.Abs(rot.z));
            return Mathf.Clamp01(r + trans.magnitude);
        }

        void LateUpdate()
        {
            var v = view != null ? view.Vessel : null;
            if (v == null) return;
            if (root == null || Signature() != builtSignature) Rebuild();
            // Видимость — как у остальных детей вида (VesselView.SetVisible переключает их всех разом).
            for (int c = 0; c < view.transform.childCount; c++)
            {
                var ch = view.transform.GetChild(c);
                if (ch == root) continue;
                if (root.gameObject.activeSelf != ch.gameObject.activeSelf) root.gameObject.SetActive(ch.gameObject.activeSelf);
                break;
            }
            if (nozzles.Count == 0 || !root.gameObject.activeSelf) return;

            // Секции — там же, где их ставит вид: от низа связки, начало — ЦМ; перевёрнутая (ЛМ на КСМ) — на 180°.
            v.MassProperties(out _, out double com, out _, out _);
            v.Layout(baseHeight);
            var yaw = v.RadialYaw != 0 ? Quaternion.Euler(0, -(float)v.RadialYaw * Mathf.Rad2Deg, 0) : Quaternion.identity;
            foreach (var kv in anchors)
            {
                int i = kv.Key;
                bool flip = v.Flipped[i];
                float lift = flip ? (float)v.Design.Sections[i].Length : 0;
                kv.Value.localPosition = new Vector3(0, (float)(baseHeight[i] - com) + lift, 0);
                kv.Value.localRotation = flip ? Quaternion.Euler(180, 0, 0) * yaw : yaw;
            }

            bool on = Command(v, out var rot, out var trans);
            float rotMag = rot.magnitude, trMag = trans.magnitude;
            var rotDir = rotMag > 1e-4f ? rot / rotMag : Vector3.zero;
            var trDir = trMag > 1e-4f ? trans / trMag : Vector3.zero;
            float pressure = Mathf.Clamp01((float)(v.StaticPressure / 101325));
            float atmo = Mathf.Lerp(1, SeaLevelLength, pressure);
            float lit = Mathf.Lerp(ShadowShare, 1, (float)SunLight.Visible);
            float dt = Time.deltaTime, time = Time.time;
            foreach (var n in nozzles)
            {
                float target = 0;
                if (on)
                {
                    var a = anchors[n.Section];
                    var p = a.localPosition + a.localRotation * n.Pos;
                    var force = -(a.localRotation * n.Exhaust);
                    // Поступательно: сопло, чья тяга смотрит по команде; вращение: чей момент p × F смотрит по команде.
                    // Векторное произведение — то же, что в ядре (числа осей совпадают, Attitude уже в осях Unity).
                    float tr = trMag > Deadband ? Mathf.Clamp01((Vector3.Dot(force, trDir) - AlignMin) / (AlignFull - AlignMin)) * Mathf.Min(1, trMag) : 0;
                    var torque = Vector3.Cross(p, force);
                    float rt = rotMag > Deadband && torque.sqrMagnitude > 1e-6f
                        ? Mathf.Clamp01((Vector3.Dot(torque.normalized, rotDir) - AlignMin) / (AlignFull - AlignMin)) * Mathf.Min(1, rotMag) : 0;
                    target = Mathf.Clamp01(tr + rt);
                    if (target < Deadband) target = 0;
                    // Неполная команда — импульсами со скважностью = доле команды.
                    else if (target < SolidAbove)
                        target = Mathf.Repeat(time * PulseHz + n.Seed, 1) < target / SolidAbove ? 1 : 0;
                }
                n.Level = Mathf.MoveTowards(n.Level, target, dt / FadeTime);
                bool show = n.Level > 0.003f;
                if (n.R.enabled != show) n.R.enabled = show;
                if (!show) continue;
                float f = 1 + Flicker * (2 * Mathf.PerlinNoise(time * FlickerHz, n.Seed * 3.1f) - 1);
                // Струя «вытекает»: длина растёт с уровнем быстрее яркости.
                float len = n.Length * atmo * f * Mathf.Sqrt(n.Level);
                n.T.localScale = new Vector3(n.Length * atmo, len, n.Length * atmo);
                mpb.Clear();
                var c = JetTint * (JetScreen * lit * n.Level * f);
                c.a = 1; // аддитив HDRP умножает цвет на альфу
                mpb.SetColor("_EmissiveColor", c);
                n.R.SetPropertyBlock(mpb);
            }
        }

        void OnDestroy()
        {
            if (root != null) Destroy(root.gameObject);
        }

        /// <summary>Градиент по длине струи (V = 0 у среза): бело-жёлтое ядро, быстрый спад — газ в пустоте
        /// разлетается и прозрачнеет. Альфа 1, затухание только в RGB (аддитив).</summary>
        static Texture2D gradient;
        static Texture2D JetGradient()
        {
            if (gradient != null) return gradient;
            const int n = 32;
            gradient = new Texture2D(1, n, TextureFormat.RGBAHalf, false, true) { name = "RCS Jet Gradient", wrapMode = TextureWrapMode.Clamp };
            for (int i = 0; i < n; i++)
            {
                float t = i / (n - 1f);
                float fade = Mathf.Clamp01(t * 15) * Mathf.Exp(-2f * t) * (1 - t);
                var c = Color.Lerp(Color.white, new Color(0.85f, 0.88f, 0.95f), t);
                gradient.SetPixel(0, i, new Color(c.r * fade, c.g * fade, c.b * fade, 1));
            }
            gradient.Apply(false, true);
            return gradient;
        }
    }
}
