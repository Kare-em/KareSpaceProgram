using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Накал сопел и обшивки (GDD §9.5, §4.6) — только вид, физики не касается. Температура — доля 0…1 с тепловой
    /// инерцией: сопло разогревается за секунды работы и остывает десятки секунд после отсечки, обшивка тлеет после
    /// пика нагрева на входе. Цвет — по шкале каления: тёмно-красный → оранжевый → соломенный.
    /// Сопла с радиационным охлаждением (насадки вакуумных двигателей: RL10, Merlin Vacuum, СПС «Аполлона») каляются
    /// почти добела, регенеративные колокола первых ступеней — едва; твердотопливные — раструбы из углерод-углерода, сильно.
    /// Отделившаяся ступень уносит своё тепло: ядро отдаёт обломку тот же SectionDef (Vessel.Split*), по нему и память.
    /// </summary>
    public sealed partial class VesselView
    {
        /// <summary>Постоянные времени сопла, с: разогрев насадки за несколько секунд работы, остывание излучением в
        /// пустоте — десятки секунд (на видео MVac краснеет ещё ~полминуты после отсечки).</summary>
        const float NozzleHeatTau = 5f, NozzleCoolTau = 35f;
        /// <summary>Накал при полной тяге, доля шкалы: регенеративный колокол (горячий срез, стенка холодная) и РДТТ.</summary>
        const float NozzleRegenShare = 0.25f, NozzleSolidShare = 0.85f;
        /// <summary>Isp у земли / в пустоте: от RegenIspRatio и выше — двигатель первой ступени (Merlin 0,91, РД-107 0,82),
        /// до RadiativeIspRatio — вакуумный с большой насадкой. Между — линейно.</summary>
        const float RegenIspRatio = 0.8f, RadiativeIspRatio = 0.3f;
        /// <summary>Яркость сопла на полном накале, нит. Пара: HeatGlowNits — замер эмиссии на корпусе (100 нит → R≈172
        /// при EV 14,3), здесь вдвое ярче: насадка в пустоте раскалена до ≈1600 K против ≈1500 K абляции.</summary>
        const float NozzleGlowNits = 200f;
        /// <summary>Обшивка на входе, с: прогрев за пару секунд, остывание после пика — полтора десятка секунд.</summary>
        const float SkinHeatTau = 2.5f, SkinCoolTau = 15f;
        /// <summary>Накал детали с подветренной стороны, доля накала лобовой: тепловой поток там в разы меньше.</summary>
        const float SkinLeeShare = 0.3f;
        /// <summary>Обшивка калится не выше этой доли шкалы — оранжевым, а не соломенным: абляция ≈1500 K.</summary>
        const float SkinColorCap = 0.6f;
        /// <summary>Ниже — накала не видно, эмиссию снимаем; перекраска — при изменении больше HeatRepaint.</summary>
        const float HeatVisible = 0.01f, HeatRepaint = 0.004f;

        static readonly Color HeatDull = new Color(0.55f, 0.04f, 0.01f), HeatOrange = new Color(1f, 0.32f, 0.07f),
                              HeatStraw = new Color(1f, 0.78f, 0.5f);

        /// <summary>Тепло секций, переживающее перестроение вида и отделение (обломок — новый VesselView).</summary>
        static readonly Dictionary<SectionDef, (float nozzle, float skin, double time)> heatMemory =
            new Dictionary<SectionDef, (float, float, double)>();

        double heatTime = double.NaN;

        static float NozzleHotShare(EngineDef e)
        {
            if (e == null) return 0;
            if (e.Solid) return NozzleSolidShare;
            // У вакуумного двигателя тяга у земли может уйти в ноль и ниже: отношение — с отсечкой.
            float ratio = e.IspVac > 0 ? Mathf.Max(0, (float)(e.IspSL / e.IspVac)) : 1;
            return Mathf.Lerp(NozzleRegenShare, 1, Mathf.InverseLerp(RegenIspRatio, RadiativeIspRatio, ratio));
        }

        static Color HeatColor(float t) => t < 0.5f ? Color.Lerp(HeatDull, HeatOrange, t * 2)
                                                    : Color.Lerp(HeatOrange, HeatStraw, (t - 0.5f) * 2);

        /// <summary>Экспонента к цели: разные постоянные на разогрев и остывание; dt любой (варп) без перелёта.</summary>
        static float Approach(float cur, float target, float dt, float up, float down) =>
            target + (cur - target) * Mathf.Exp(-dt / (target > cur ? up : down));

        /// <summary>Накал к цвету эмиссии; ниже порога — прозрачный (Paint эмиссию не пишет).</summary>
        static Color GlowOf(float t, float nits, float colorScale)
        {
            if (t < HeatVisible) return Color.clear;
            // Яркость растёт быстрее цвета (Стефан — Больцман ∝ T⁴, здесь мягче — квадрат): тёмно-красное едва видно.
            var c = HeatColor(t * colorScale) * (nits * t * t);
            c.a = 1;
            return c;
        }

        void UpdateHeat(Vector3 airflow, double vesselLen)
        {
            var u = GameBootstrap.U;
            double now = u.Time;
            float dt = double.IsNaN(heatTime) ? 0 : Mathf.Max(0, (float)(now - heatTime));
            heatTime = now;

            // Цель обшивки — та же кривая, что у плазмы (UpdatePlasma): видимый накал начинается вместе с ореолом.
            float q = (float)Vessel.HeatFlux;
            float k = Vessel.SurfaceSpeed > 100 ? Mathf.Clamp01((q - PlasmaStart) / (PlasmaFull - PlasmaStart)) : 0;
            k = k * k * (3 - 2 * k);
            var flow = transform.InverseTransformDirection(airflow);
            float half = Mathf.Max(1, (float)vesselLen * 0.5f);

            foreach (var p in parts)
            {
                var s = Vessel.Design.Sections[p.Index];
                if (!p.HeatRestored)
                {
                    // Секция пришла из другого вида (отделение, перестроение): тепло остывает по прошедшему времени.
                    p.HeatRestored = true;
                    if (heatMemory.TryGetValue(s, out var mem))
                    {
                        float gone = Mathf.Max(0, (float)(now - mem.time));
                        p.NozzleT = Approach(mem.nozzle, 0, gone, NozzleHeatTau, NozzleCoolTau);
                        p.SkinT = Approach(mem.skin, 0, gone, SkinHeatTau, SkinCoolTau);
                    }
                }
                float thr = s.HasEngine && Vessel.Running[p.Index] ? (float)Vessel.EffectiveThrottle(p.Index) : 0;
                p.NozzleT = Approach(p.NozzleT, thr * NozzleHotShare(s.Engine), dt, NozzleHeatTau, NozzleCoolTau);
                // Лобовая деталь (впереди по потоку) греется полностью, кормовая — долю SkinLeeShare.
                float lead = Mathf.Clamp01(0.5f + Vector3.Dot(p.Tr.localPosition, flow) / (2 * half));
                p.SkinT = Approach(p.SkinT, k * Mathf.Lerp(SkinLeeShare, 1, lead), dt, SkinHeatTau, SkinCoolTau);
                heatMemory[s] = (p.NozzleT, p.SkinT, now);

                if (Mathf.Abs(p.NozzleT - p.PaintedNozzle) < HeatRepaint && Mathf.Abs(p.SkinT - p.PaintedSkin) < HeatRepaint
                    && (p.NozzleT >= HeatVisible) == (p.PaintedNozzle >= HeatVisible)
                    && (p.SkinT >= HeatVisible) == (p.PaintedSkin >= HeatVisible)) continue;
                p.PaintedNozzle = p.NozzleT;
                p.PaintedSkin = p.SkinT;
                var skin = GlowOf(p.SkinT, HeatGlowNits, SkinColorCap);
                // Слот сопла: свой накал или накал входа — что горячее (сопла садящейся ступени Falcon тоже на потоке).
                var nozzle = p.NozzleT >= p.SkinT ? GlowOf(p.NozzleT, NozzleGlowNits, 1) : skin;
                for (int j = 0; j < p.BodyR.Count; j++) PaintHeat(p.BodyR[j], p.BodyColors[j], skin, nozzle);
                for (int j = 0; j < p.NozzleR.Count; j++) PaintHeat(p.NozzleR[j], p.NozzleColors[j], skin, nozzle);
            }
        }

        /// <summary>Как Paint, но накал по слотам: слоты цвета сопла — nozzle, остальные — skin.</summary>
        void PaintHeat(Renderer mr, Color[] palette, Color skin, Color nozzle)
        {
            if (mr == null) return;
            for (int i = 0; i < palette.Length; i++)
            {
                mpb.Clear(); // общий блок — без Clear наследуется чужая эмиссия (см. Paint)
                mpb.SetColor("_BaseColor", SlotColor(palette[i]));
                var e = palette[i] == NozzleColor ? nozzle : skin;
                if (e.a > 0) mpb.SetColor("_EmissiveColor", e);
                if (palette.Length == 1) mr.SetPropertyBlock(mpb);
                else mr.SetPropertyBlock(mpb, i);
            }
        }
    }
}
