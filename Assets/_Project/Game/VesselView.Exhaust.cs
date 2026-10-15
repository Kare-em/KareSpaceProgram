using System.Collections.Generic;
using UnityEngine;
using Kare.Space.Core;

namespace Kare.Space.Game
{
    /// <summary>
    /// Вид факела по топливу (§9.5). Раньше все двигатели рисовались керосиновым факелом, а его ширина шла от диаметра
    /// секции: четыре гидразиновых ЖРД «Вояджера» по 0,89 Н давали общий факел шириной с тарелку антенны 3,66 м.
    /// Теперь ширина — от площади среза сопла (её задаёт ядро: EngineDef.NozzleArea = (Fпуст − Fур.моря) / p₀), цвет,
    /// яркость и длина — от топлива, а монотопливные микродвигатели факела не дают вовсе (как и в жизни: гидразин в
    /// пустоте прозрачен).
    /// </summary>
    public sealed partial class VesselView
    {
        public enum PlumeKind { None, Kerolox, Hydrolox, Methalox, Hypergolic, Solid }

        /// <summary>Мельче этой тяги на одно сопло, Н, — факела нет: струя ЖРД ориентации/коррекции не светится.
        /// Пара: монотопливные (IspVac ниже MonopropIsp) тоже без факела до MonopropThrust.</summary>
        const double VisibleThrust = 50, MonopropIsp = 240, MonopropThrust = 1000;
        /// <summary>Кислород-водород: удельный импульс в пустоте от этого порога (J-2 421, RL10 444–465, RS-25 452).</summary>
        const double HydroloxIsp = 400;
        /// <summary>Факелы связки сливаются в один столб, если радиус струи у среза не меньше этой доли радиуса кольца.
        /// Иначе (редкие мелкие сопла на модели аппарата) — отдельный факел на каждое сопло (копии, как Draco).</summary>
        const float MergeShare = 0.3f;

        /// <summary>Гиперголики (АТ/НДМГ, аэрозин): узнаём по имени — по импульсу они неотличимы от керосина.</summary>
        static readonly string[] HypergolicNames =
        {
            "LR-87", "LR87", "LR-91", "LR91", "РД-253", "РД-275", "РД-0210", "РД-0212", "SPS", "APS", "DPS", "OMS", "Draco",
            "СКД", "КТДУ", "Bell", "ТДУ", "Тормозные ДУ", "Корректирующая", "Посадочная", "AJ10", "ОДУ",
        };

        public static PlumeKind KindOf(EngineDef e, int count)
        {
            if (e == null || count <= 0) return PlumeKind.None;
            if (e.Solid) return PlumeKind.Solid;
            if (e.ThrustVac < VisibleThrust) return PlumeKind.None;
            if (e.IspVac < MonopropIsp && e.ThrustVac < MonopropThrust) return PlumeKind.None;
            string n = e.Name ?? "";
            if (n.Contains("Raptor")) return PlumeKind.Methalox;
            if (e.IspVac >= HydroloxIsp) return PlumeKind.Hydrolox;
            foreach (var h in HypergolicNames)
                if (n.Contains(h)) return PlumeKind.Hypergolic;
            return PlumeKind.Kerolox;
        }

        /// <summary>Радиус среза одного сопла, м, — из площади, которую ядро берёт для потери тяги у земли. Без разницы
        /// тяг (вакуумный двигатель с ThrustSL = Fпуст) — по среднему давлению на срезе ≈ 1,5 атм (RL10: 1,1 → 0,59 м).</summary>
        static float ExitRadius(EngineDef e)
        {
            double a = e.NozzleArea;
            if (a <= 0) a = e.ThrustVac / 1.5e5;
            return Mathf.Sqrt((float)(a / Mathf.PI));
        }

        /// <summary>Свойства вида: множители яркости и длины к керосиновому факелу, доля яркости в пустоте, свет.
        /// Пара: CoreNits/GlowNits, PlumeLengthSL, VacuumBrightness — общие базовые числа.</summary>
        struct KindLook
        {
            public float Bright, Length, Vacuum;
            /// <summary>Сила ударных ромбов у земли (0 — нет): сажа их прячет, прозрачная струя — показывает.</summary>
            public float Shock;
            public Color Light;
        }

        static KindLook Look(PlumeKind k)
        {
            switch (k)
            {
                // Алюминий в смесевом топливе: раскалённые частицы Al₂O₃ — самый яркий и длинный факел, и в пустоте тоже.
                case PlumeKind.Solid: return new KindLook { Bright = 1.6f, Length = 1.4f, Vacuum = 0.6f, Shock = 0.15f, Light = new Color(1f, 0.82f, 0.6f) };
                // Водород горит почти без сажи: бледно-голубая полупрозрачная струя, в пустоте её почти не видно.
                // Зато ромбы у земли ярче самой струи (RS-25 на старте шаттла): прозрачный газ их не прячет.
                case PlumeKind.Hydrolox: return new KindLook { Bright = 0.3f, Length = 0.8f, Vacuum = 0.5f, Shock = 1.6f, Light = new Color(0.72f, 0.75f, 1f) };
                // Метан — тоже без сажи: синее ядро, у земли розово-оранжевый хвост (Raptor).
                case PlumeKind.Methalox: return new KindLook { Bright = 0.6f, Length = 1f, Vacuum = 0.6f, Shock = 1f, Light = new Color(0.8f, 0.72f, 1f) };
                // АТ/НДМГ: прозрачная оранжево-розовая струя, у земли с бурым NO₂.
                case PlumeKind.Hypergolic: return new KindLook { Bright = 0.5f, Length = 0.9f, Vacuum = 0.8f, Shock = 0.3f, Light = new Color(1f, 0.66f, 0.5f) };
                // Керосин: сажистая оболочка прячет ромбы наполовину (Merlin, РД-107 — видны у самого среза).
                default: return new KindLook { Bright = 1, Length = 1, Vacuum = 1, Shock = 0.7f, Light = new Color(1f, 0.72f, 0.45f) };
            }
        }

        /// <summary>Цвет и яркость струи на доле длины t (0 — срез) по виду топлива. Один источник для текстуры и для
        /// силы света (PlumeShape). Керосин — как было: белый → жёлто-оранжевый → красный (сажа).</summary>
        static Color PlumeGradientColor(PlumeKind k, float t)
        {
            Color a, b, c;
            float decay = 3.5f;
            switch (k)
            {
                case PlumeKind.Solid:
                    a = new Color(1f, 0.97f, 0.92f); b = new Color(1f, 0.8f, 0.5f); c = new Color(1f, 0.55f, 0.25f); decay = 2.2f; break;
                case PlumeKind.Hydrolox:
                    a = new Color(0.78f, 0.82f, 1f); b = new Color(0.6f, 0.55f, 1f); c = new Color(0.5f, 0.35f, 0.8f); decay = 5f; break;
                case PlumeKind.Methalox:
                    a = new Color(0.65f, 0.75f, 1f); b = new Color(0.85f, 0.5f, 0.95f); c = new Color(1f, 0.45f, 0.25f); decay = 4f; break;
                case PlumeKind.Hypergolic:
                    a = new Color(1f, 0.85f, 0.72f); b = new Color(1f, 0.55f, 0.42f); c = new Color(0.85f, 0.32f, 0.28f); decay = 4f; break;
                default:
                    a = new Color(1f, 0.95f, 0.85f); b = new Color(1f, 0.7f, 0.3f); c = new Color(0.9f, 0.3f, 0.1f); break;
            }
            var col = Color.Lerp(Color.Lerp(a, b, Mathf.Clamp01(t * 3)), c, Mathf.Clamp01(t * 1.5f - 0.3f));
            // Срез без резкой кромки, хвост гаснет в ноль.
            float fade = Mathf.Clamp01(t * 12) * Mathf.Exp(-decay * t) * (1 - t);
            return col * fade;
        }

        static readonly Dictionary<PlumeKind, Texture2D> kindGradients = new Dictionary<PlumeKind, Texture2D>();
        readonly Dictionary<PlumeKind, Material> kindMats = new Dictionary<PlumeKind, Material>();

        static Texture2D PlumeGradient(PlumeKind k)
        {
            if (kindGradients.TryGetValue(k, out var tex) && tex != null) return tex;
            const int n = 64;
            tex = new Texture2D(1, n, TextureFormat.RGBAHalf, false, true) { name = "Plume Gradient " + k, wrapMode = TextureWrapMode.Clamp };
            for (int i = 0; i < n; i++)
            {
                // Альфа = 1: аддитив HDRP умножает цвет на альфу — затухание только в RGB.
                var c = PlumeGradientColor(k, i / (n - 1f));
                c.a = 1;
                tex.SetPixel(0, i, c);
            }
            tex.Apply(false, true);
            return kindGradients[k] = tex;
        }

        /// <summary>Шейдер объёмного факела (Settings/Resources — попадает в билд, как шейдер колец).</summary>
        public const string PlumeShaderName = "Kare/Plume HDRP";

        /// <summary>
        /// Материал факела вида k на шейдере Kare/Plume HDRP (мягкий край, клочья, ромбы, раскрытие в пустоте — см.
        /// шапку шейдера). Без шейдера — как раньше, копия plumeMat (HDRP/Unlit) с ключом _EMISSIVE_COLOR_MAP руками
        /// (рантайм-материалы не валидируются, pitfalls-render). Сам plumeMat не меняем: из него сделана плазма входа.
        /// </summary>
        Material PlumeMaterial(PlumeKind k)
        {
            if (kindMats.TryGetValue(k, out var m) && m != null) return m;
            var sh = Shader.Find(PlumeShaderName);
            if (sh != null)
            {
                m = new Material(sh) { name = "Plume " + k + " (runtime)", renderQueue = 3000 };
                m.SetTexture("_EmissiveColorMap", PlumeGradient(k));
                return kindMats[k] = m;
            }
            if (k == PlumeKind.Kerolox) return plumeMat;
            m = new Material(plumeMat) { name = "Plume " + k + " (runtime)" };
            m.SetTexture("_EmissiveColorMap", PlumeGradient(k));
            m.EnableKeyword("_EMISSIVE_COLOR_MAP");
            return kindMats[k] = m;
        }

        /// <summary>Раскрытие недорасширенной струи в пустоте (шейдер: радиус × (1 + E·√t), основание — на срезе):
        /// свечение уходит куполом (J-2, RL10 на снимках — шире ступени), ядро почти не раздувается. Пара:
        /// PlumeBoundsPad — запас границ меша под это раскрытие, иначе отсечение по frustum резало бы хвост.</summary>
        const float GlowVacuumExpand = 2.5f, CoreVacuumExpand = 0.4f, PlumeBoundsPad = 1 + GlowVacuumExpand;
        /// <summary>Клочья (доля модуляции шумом): ядро ровнее, внешнее свечение рвётся сильнее. Снос ячеек шума, 1/с.</summary>
        const float CoreTurb = 0.35f, GlowTurb = 0.75f, PlumeScroll = 5f;
        /// <summary>Ударные ромбы: шаг у земли в диаметрах среза (у одиночного сопла 1–2 D; растёт как √(pₐ₀/pₐ)),
        /// узлов до затухания; ниже ShockPressureEnd атм ромбы гаснут — выше 10–15 км их не видно.</summary>
        const float ShockCalibers = 1.4f, ShockNodes = 4, ShockPressureEnd = 0.25f;
        /// <summary>Мягкое касание стола/грунта и подход камеры — в радиусах среза.</summary>
        const float PlumeSoft = 0.6f, PlumeNear = 0.5f;
        /// <summary>Средний множитель края поперёк столба (∫|cos|^Limb по прицельному параметру, Limb 1,2 в шейдере):
        /// светящаяся площадь стала меньше — на столько же меньше и сила света факела (PlumeLight).</summary>
        const float PlumeLimbMean = 0.75f;

        static readonly int IdEmissive = Shader.PropertyToID("_EmissiveColor"), IdExpand = Shader.PropertyToID("_Expand"),
            IdAspect = Shader.PropertyToID("_Aspect"), IdTurb = Shader.PropertyToID("_Turb"), IdShock = Shader.PropertyToID("_Shock"),
            IdSeed = Shader.PropertyToID("_Seed"), IdSoft = Shader.PropertyToID("_SoftDist"), IdNear = Shader.PropertyToID("_NearFade"),
            IdScroll = Shader.PropertyToID("_Scroll");

        /// <summary>Границы меша струи — с запасом на раскрытие в шейдере (вершины двигает GPU, Unity о них не знает).</summary>
        static Mesh PadPlumeBounds(Mesh m)
        {
            var b = m.bounds;
            b.extents = new Vector3(b.extents.x * PlumeBoundsPad, b.extents.y, b.extents.z * PlumeBoundsPad);
            m.bounds = b;
            return m;
        }

        /// <summary>
        /// Кадр факела (§9.5): масштаб ядра и свечения и параметры шейдера. Основание свечения — ровно срез (раньше в
        /// пустоте раздувалось всё свечение вместе с основанием, и у среза висело кольцо шире сопла); недорасширение —
        /// раскрытием хвоста в шейдере. Ромбы — только у земли и только на ядре. Без аллокаций: свойства по Id,
        /// общий mpb с Clear перед каждым рендерером (копии — SyncMirrors через GetPropertyBlock).
        /// </summary>
        void UpdatePlumeFx(Part p, float pressure, float len, float coreR, float coreNits, float glowNits, float flicker)
        {
            float pa = Mathf.Clamp01(pressure), vac = 1 - pa;
            float coreLen = len * flicker, glowLen = len * GlowLength * flicker;
            p.Plume.localScale = new Vector3(coreR, coreLen, coreR);
            p.Glow.localScale = new Vector3(p.PlumeRadius, glowLen, p.PlumeRadius);
            float shock = Look(p.Kind).Shock * Mathf.SmoothStep(0, 1, (pa - ShockPressureEnd) / (1 - ShockPressureEnd));
            float step = ShockCalibers * 2 * p.PlumeRadius / Mathf.Sqrt(Mathf.Max(pa, ShockPressureEnd)) / Mathf.Max(coreLen, 1e-3f);
            float seed = p.Index * 3.7f;
            SetPlumeFx(p.CoreR, coreNits * flicker, vac * CoreVacuumExpand, coreLen / Mathf.Max(coreR, 1e-3f), CoreTurb,
                new Vector4(shock, step, ShockNodes, 0), seed, p.PlumeRadius);
            SetPlumeFx(p.GlowR, glowNits * flicker, vac * GlowVacuumExpand, glowLen / Mathf.Max(p.PlumeRadius, 1e-3f), GlowTurb,
                Vector4.zero, seed + 11, p.PlumeRadius);
        }

        void SetPlumeFx(Renderer r, float nits, float expand, float aspect, float turb, Vector4 shock, float seed, float radius)
        {
            mpb.Clear();
            mpb.SetColor(IdEmissive, new Color(nits, nits, nits, 1));
            mpb.SetFloat(IdExpand, expand);
            mpb.SetFloat(IdAspect, aspect);
            mpb.SetFloat(IdTurb, turb);
            mpb.SetFloat(IdScroll, PlumeScroll);
            mpb.SetVector(IdShock, shock);
            mpb.SetFloat(IdSeed, seed);
            mpb.SetFloat(IdSoft, radius * PlumeSoft);
            mpb.SetFloat(IdNear, radius * PlumeNear);
            r.SetPropertyBlock(mpb);
        }

        /// <summary>
        /// Отдельные факелы на редких мелких соплах модели (тормозные ДУ, верньеры): первый — у сопла k = 0, копии —
        /// на остальных местах кольца. Общий столб по оси для них неверен: струи не сливаются, а оси сопла там нет.
        /// Дым — только у основного (NoSmoke не ставим: источник шлейфа один, как у связки).
        /// </summary>
        void AddRingMirrors(Part part, Transform nozzle, int ring, float rr)
        {
            nozzle.localPosition += new Vector3(rr, 0, 0);
            part.Mirrors = new List<Mirror>();
            for (int k = 1; k < ring; k++)
            {
                float a = 2 * Mathf.PI * k / ring;
                var holder = new GameObject("Nozzle " + k).transform;
                holder.SetParent(nozzle.parent, false);
                holder.localPosition = nozzle.localPosition - new Vector3(rr, 0, 0) + new Vector3(rr * Mathf.Cos(a), 0, rr * Mathf.Sin(a));
                var plume = Instantiate(part.Plume.gameObject, holder, false);
                var glow = Instantiate(part.Glow.gameObject, holder, false);
                part.Mirrors.Add(new Mirror
                {
                    Plume = plume.transform, Glow = glow.transform,
                    CoreR = plume.GetComponent<Renderer>(), GlowR = glow.GetComponent<Renderer>(),
                });
            }
        }
    }
}
