using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Струи разделения (GDD §5, §6.6, §9.5) — только вид, импульсы считает ядро (Vessel.FireRetros, LesPush):
    /// · тормозные РДТТ отработавшей ступени (S-IC, S-II — Separation.Retro): короткие яркие факелы вперёд, вдоль корпуса;
    /// · РДТТ осадки верхней ступени (SectionDef.UllageMotors) первые секунды после отделения — назад, с развалом;
    /// · РДТТ увода башни САС «Аполлона»: два скошенных сопла у верха башни, ~1 с;
    /// · горячее разделение (Р-7, «Титан II», «Протон», Starship, взлёт ЛМ): верхняя ступень уже работает, а нижняя ещё
    ///   пристыкована — газ вырывается вбок через ферму. Признак — по состоянию ядра, без списка ракет.
    /// Струи — дети секции (p.Tr): прячутся и удаляются вместе с видом (SetVisible, Rebuild → sepJets.Clear).
    /// </summary>
    public sealed partial class VesselView
    {
        /// <summary>Нарастание и хвост РДТТ, с: твёрдое топливо выходит на режим за десятки мс, догорает за доли секунды.</summary>
        const float SepRise = 0.05f, SepTail = 0.3f;
        /// <summary>Яркость струй разделения, доля CoreNits: сопла мелкие, струя тоньше главного факела.</summary>
        const float SepBright = 0.7f;
        /// <summary>Тормозные: длина струи и радиус среза — в радиусах ступени (S-IC: R 5 м → 12 м и 0,25 м);
        /// РДТТ стоят чуть снаружи обшивки (обтекатели двигателей) — RetroStandoff радиуса.</summary>
        const float RetroLength = 2.4f, RetroNozzle = 0.05f, RetroStandoff = 1.08f;
        /// <summary>Высота тормозных над днищем / под верхом, доля длины ступени (у S-IC — в обтекателях двигателей).</summary>
        const float RetroInset = 0.06f;
        /// <summary>Осадка: время работы, с (РДТТ осадки S-II и S-IVB — ≈ 3,7–3,9 с), длина и срез в радиусах ступени,
        /// развал наружу, °. Пара: Vessel.ArmNext держит топливо осевшим 3 с — струи видны всё это время.</summary>
        const float UllageBurn = 3.8f, UllageLength = 1.2f, UllageNozzle = 0.04f, UllageCant = 10;
        /// <summary>Отделение замечаем, если низ борта сменился не позже чем через столько секунд после SeparatedAt.</summary>
        const double UllageCatch = 1;
        /// <summary>САС «Аполлона»: РДТТ увода башни — 2 сопла на высоте TowerMotorAt длины секции, радиус башни там,
        /// длина струи, м, скос от оси, °, время работы, с. Сопла — на стороне −X: ядро уводит башню вбок на +X (Vessel.LesSide).</summary>
        const float TowerMotorAt = 0.8f, TowerRadius = 0.33f, TowerJetLength = 6, TowerJetNozzle = 0.12f, TowerCant = 35, TowerBurn = 1.1f;
        /// <summary>Горячее разделение: число окон фермы, длина струи и срез в радиусах верхней ступени, наклон вниз
        /// от горизонтали, °. Газ уходит в окна почти поперёк — на видео «Протона» и Starship видно кольцо языков.</summary>
        const int HotVents = 8;
        const float HotVentLength = 1.4f, HotVentNozzle = 0.12f, HotVentDown = 15;
        /// <summary>Профиль струи (ProcMesh.Plume): у РДТТ струя расширяется от среза, а не сужается, как ядро факела
        /// (CoreGrow < 0) — к концу втрое шире среза.</summary>
        const float SepGrow = 2f, SepPower = 0.7f;
        /// <summary>Раскрытие струи в пустоте (как у факела, VesselView.Exhaust) — доля для струй разделения.</summary>
        const float SepVacuumExpand = 0.6f;

        enum SepKind { Retro, Ullage, Tower, HotVent }

        sealed class SepJets
        {
            public Transform[] T;
            public Renderer[] R;
            public float Length, Nozzle;
        }

        readonly Dictionary<(int section, SepKind kind), SepJets> sepJets = new Dictionary<(int, SepKind), SepJets>();
        static Mesh sepMesh;
        int sepBottom = -1, ullageSection = -1;
        double ullageAt = double.NaN;

        /// <summary>Огибающая РДТТ: t — с момента зажигания, burn — время работы. NaN (нет разделения) — ноль.</summary>
        static float Burst(double t, float burn)
        {
            if (!(t >= 0)) return 0;
            return Mathf.Clamp01((float)t / SepRise) * Mathf.Clamp01((burn + SepTail - (float)t) / SepTail);
        }

        void UpdateSeparationFx(float pressure)
        {
            var secs = Vessel.Design.Sections;
            double since = GameBootstrap.U.Time - Vessel.SeparatedAt;
            float vac = 1 - Mathf.Clamp01(pressure);

            // Осадка: низ борта сменился сразу после разделения, и у новой нижней секции есть РДТТ осадки. Сброс САС
            // или обтекателя низ не меняет — осадка не повторяется.
            int bottom = Vessel.BottomSection();
            if (bottom != sepBottom)
            {
                if (sepBottom >= 0 && bottom >= 0 && since <= UllageCatch && secs[bottom].UllageMotors)
                {
                    ullageAt = Vessel.SeparatedAt;
                    ullageSection = bottom;
                }
                sepBottom = bottom;
            }

            foreach (var p in parts)
            {
                if (p.SideMount || Vessel.Flipped[p.Index]) continue;
                var s = secs[p.Index];
                float R = Mathf.Max(0.3f, (float)s.Diameter * 0.5f), L = (float)s.Length;

                if (Vessel.IsDebris && Separation.Retro(s.Model, out var r))
                {
                    float env = Burst(since, (float)r.BurnTime);
                    float y = r.AtTop ? L * (1 - RetroInset) : L * RetroInset;
                    var j = Jets(p, SepKind.Retro, env, () => Ring(p, SepKind.Retro, r.Count, 360f / r.Count / 2, R * RetroStandoff, y,
                        Vector3.up, (float)r.CantDeg, R * RetroLength, R * RetroNozzle));
                    ShowJets(j, env, vac, p.Index * 5.1f);
                }

                if (p.Index == ullageSection)
                {
                    float env = Burst(GameBootstrap.U.Time - ullageAt, UllageBurn);
                    int n = s.Model == SectionModel.SaturnSII ? 4 : 2; // S-II — четыре на межступенчатом отсеке, S-IVB и прочие — два
                    var j = Jets(p, SepKind.Ullage, env, () => Ring(p, SepKind.Ullage, n, 45, R * RetroStandoff, L * RetroInset,
                        Vector3.down, UllageCant, R * UllageLength, R * UllageNozzle));
                    ShowJets(j, env, vac, p.Index * 2.3f);
                }

                if (Vessel.IsDebris && s.Model == SectionModel.ApolloLES && Vessel.FairingHalf == 0)
                {
                    float env = Burst(since, TowerBurn);
                    var j = Jets(p, SepKind.Tower, env, () => TowerJets(p, L));
                    ShowJets(j, env, vac, p.Index * 7.7f);
                }

                if (s.HasEngine && p.Throttle > 0.01f && HotStaging(p.Index))
                {
                    float env = p.Throttle;
                    var j = Jets(p, SepKind.HotVent, env, () => Ring(p, SepKind.HotVent, HotVents, 22.5f, R, 0,
                        Vector3.down, 90 - HotVentDown, R * HotVentLength, R * HotVentNozzle, p.Kind));
                    ShowJets(j, env, vac, p.Index * 3.3f);
                }
                else if (sepJets.TryGetValue((p.Index, SepKind.HotVent), out var hv)) ShowJets(hv, 0, vac, 0);
            }
        }

        /// <summary>Под работающей секцией i ещё висит осевая ступень — газ некуда деть, кроме фермы. Нижняя может и
        /// сама работать: Super Heavy на разделении держит три двигателя. Боковые блоки (Р-7, SRB) и переходники
        /// не в счёт: они не под соплами.</summary>
        bool HotStaging(int i)
        {
            var secs = Vessel.Design.Sections;
            for (int k = 0; k < secs.Count; k++)
            {
                if (k == i || !Vessel.Attached[k] || Vessel.Flipped[k]) continue;
                var s = secs[k];
                if (s.IsRadial || s.SideMounted || s.Kind == SectionKind.Fairing) continue;
                if (baseHeight[k] + s.Length <= baseHeight[i] + 0.1) return true;
            }
            return false;
        }

        /// <summary>Струи по ключу; создаются при первом зажигании (env > 0) — у бортов без разделения ничего не строится.</summary>
        SepJets Jets(Part p, SepKind kind, float env, System.Func<SepJets> make)
        {
            if (sepJets.TryGetValue((p.Index, kind), out var j) && j.T.Length > 0 && j.T[0] != null) return j;
            if (env <= 0) return null;
            return sepJets[(p.Index, kind)] = make();
        }

        /// <summary>Кольцо из n сопел на радиусе ring и высоте y секции: выхлоп по оси axis, развал наружу на cant°.</summary>
        SepJets Ring(Part p, SepKind kind, int n, float phaseDeg, float ring, float y, Vector3 axis, float cant, float len, float nozzle,
                     PlumeKind look = PlumeKind.Solid)
        {
            var pos = new Vector3[n];
            var dir = new Vector3[n];
            float c = Mathf.Cos(cant * Mathf.Deg2Rad), sn = Mathf.Sin(cant * Mathf.Deg2Rad);
            for (int i = 0; i < n; i++)
            {
                float a = (phaseDeg + 360f * i / n) * Mathf.Deg2Rad;
                var outward = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                pos[i] = outward * ring + Vector3.up * y;
                dir[i] = (axis * c + outward * sn).normalized;
            }
            return MakeJets(p, kind.ToString(), pos, dir, len, nozzle, look);
        }

        /// <summary>Два сопла РДТТ увода башни: на стороне −X, ±40° от неё, выхлоп назад-наружу.</summary>
        SepJets TowerJets(Part p, float L)
        {
            var pos = new Vector3[2];
            var dir = new Vector3[2];
            float c = Mathf.Cos(TowerCant * Mathf.Deg2Rad), sn = Mathf.Sin(TowerCant * Mathf.Deg2Rad);
            for (int i = 0; i < 2; i++)
            {
                float a = (180 + (i == 0 ? -40 : 40)) * Mathf.Deg2Rad;
                var outward = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                pos[i] = outward * TowerRadius + Vector3.up * (L * TowerMotorAt);
                dir[i] = (Vector3.down * c + outward * sn).normalized;
            }
            return MakeJets(p, "Tower", pos, dir, TowerJetLength, TowerJetNozzle, PlumeKind.Solid);
        }

        SepJets MakeJets(Part p, string name, Vector3[] pos, Vector3[] dir, float len, float nozzle, PlumeKind look)
        {
            // Меш единичный (срез 1, длина 1), масштаб — у каждой струи; общий на все виды.
            if (sepMesh == null) sepMesh = PadPlumeBounds(ProcMesh.Plume(1, SepGrow, SepPower, 12, 8));
            var mat = PlumeMaterial(look);
            var j = new SepJets { T = new Transform[pos.Length], R = new Renderer[pos.Length], Length = len, Nozzle = nozzle };
            for (int i = 0; i < pos.Length; i++)
            {
                var go = new GameObject(name + " Jet");
                go.transform.SetParent(p.Tr, false);
                go.transform.localPosition = pos[i];
                // Меш струи — вниз от нуля: −Y поворачиваем в направление выхлопа.
                go.transform.localRotation = Quaternion.FromToRotation(Vector3.down, dir[i]);
                go.AddComponent<MeshFilter>().sharedMesh = sepMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                go.SetActive(false);
                j.T[i] = go.transform;
                j.R[i] = mr;
            }
            return j;
        }

        void ShowJets(SepJets j, float env, float vac, float seed)
        {
            if (j == null) return;
            bool on = env > 0.01f;
            for (int i = 0; i < j.T.Length; i++)
            {
                var t = j.T[i];
                if (t == null) continue;
                if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
                if (!on) continue;
                float fl = Flicker(PlumeFlicker, (int)seed + i);
                // Струя выходит на длину вместе с давлением в камере: короче на зажигании и догорании.
                float len = j.Length * (0.5f + 0.5f * env) * fl;
                t.localScale = new Vector3(j.Nozzle, len, j.Nozzle);
                float nits = CoreNits * SepBright * env * fl * BrightnessSettings.Plume;
                SetPlumeFx(j.R[i], nits, vac * SepVacuumExpand, len / Mathf.Max(j.Nozzle, 1e-3f), CoreTurb, Vector4.zero,
                           seed + i * 1.37f, j.Nozzle);
            }
        }
    }
}
