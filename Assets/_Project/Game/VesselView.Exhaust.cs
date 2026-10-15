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
            public Color Light;
        }

        static KindLook Look(PlumeKind k)
        {
            switch (k)
            {
                // Алюминий в смесевом топливе: раскалённые частицы Al₂O₃ — самый яркий и длинный факел, и в пустоте тоже.
                case PlumeKind.Solid: return new KindLook { Bright = 1.6f, Length = 1.4f, Vacuum = 0.6f, Light = new Color(1f, 0.82f, 0.6f) };
                // Водород горит почти без сажи: бледно-голубая полупрозрачная струя, в пустоте её почти не видно.
                case PlumeKind.Hydrolox: return new KindLook { Bright = 0.3f, Length = 0.8f, Vacuum = 0.5f, Light = new Color(0.72f, 0.75f, 1f) };
                // Метан — тоже без сажи: синее ядро, у земли розово-оранжевый хвост (Raptor).
                case PlumeKind.Methalox: return new KindLook { Bright = 0.6f, Length = 1f, Vacuum = 0.6f, Light = new Color(0.8f, 0.72f, 1f) };
                // АТ/НДМГ: прозрачная оранжево-розовая струя, у земли с бурым NO₂.
                case PlumeKind.Hypergolic: return new KindLook { Bright = 0.5f, Length = 0.9f, Vacuum = 0.8f, Light = new Color(1f, 0.66f, 0.5f) };
                default: return new KindLook { Bright = 1, Length = 1, Vacuum = 1, Light = new Color(1f, 0.72f, 0.45f) };
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

        /// <summary>Материал факела вида k: копия plumeMat со своим градиентом. Ключ _EMISSIVE_COLOR_MAP — руками
        /// (рантайм-материалы не валидируются, pitfalls-render).</summary>
        Material PlumeMaterial(PlumeKind k)
        {
            if (k == PlumeKind.Kerolox) return plumeMat;
            if (kindMats.TryGetValue(k, out var m) && m != null) return m;
            m = new Material(plumeMat) { name = "Plume " + k + " (runtime)" };
            m.SetTexture("_EmissiveColorMap", PlumeGradient(k));
            m.EnableKeyword("_EMISSIVE_COLOR_MAP");
            return kindMats[k] = m;
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
