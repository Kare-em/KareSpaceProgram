using System;
using System.Collections.Generic;

namespace Kare.Space.Core
{
    /// <summary>
    /// Цена пуска (GDD §8.4), M₢. Одна линейная формула для деталей конструктора и для секций исторических ракет, чтобы
    /// своя «Семёрка» стоила примерно как пресетная: двигатель — по тяге, конструкция — по сухой массе, кресло экипажа,
    /// надбавки за сложные детали (таблица по id) и операции пуска по стартовой массе.
    /// Проверка калибра (тест career): «Кара-1» ≈ 60 M₢ (§8.4: 36 + 12 + 6 + 6), Р-7 ≈ 40–50, «Сатурн V» ≈ 300–350.
    /// </summary>
    public static class CareerPricing
    {
        /// <summary>Камера ЖРД/РДТТ: фикс + за МН пустотной тяги. Пара: StructurePerTonne — вместе дают ступень «Кара-1» ≈ 36.</summary>
        public const double EngineBase = 0.5, EnginePerMN = 2.5;
        /// <summary>Конструкция (баки, отсеки, обтекатели), M₢ за тонну сухой массы.</summary>
        public const double StructurePerTonne = 0.6;
        /// <summary>Кресло экипажа: жизнеобеспечение и САС.</summary>
        public const double CrewSeat = 8;
        /// <summary>Операции пуска (§8.4: 6 M₢ у «Кара-1», ≈ 550 т): фикс + за тонну стартовой массы.</summary>
        public const double OpsBase = 2, OpsPerTonne = 0.008;
        /// <summary>Возврат целого корабля/ступени на Землю: доля стоимости железа (§8.4: ремонт ступени — 10 %, 30 % — остаток).</summary>
        public const double RecoveryFraction = 0.3;

        /// <summary>Надбавки сложных деталей, M₢ (электроника, механизмы, сверх массы). Ключ — id PartCatalog.</summary>
        public static readonly Dictionary<string, double> PartExtra = new Dictionary<string, double>
        {
            { "probe-core", 2 }, { "antenna", 1 }, { "solar", 2 }, { "rtg", 6 }, { "rcs", 1.5 }, { "dock-1", 4 },
            { "chute", 0.5 }, { "legs", 2 }, { "gear", 3 }, { "gridfins", 2 }, { "flaps", 2 }, { "wing", 4 },
            { "wing-tail", 1.5 }, { "wing-fin", 1 }, { "pl-sputnik", 1 }, { "pl-1t", 3 }, { "pl-10t", 5 },
            { "cmd-vostok", 4 }, { "cmd-mercury", 4 }, { "cmd-gemini", 8 }, { "cmd-apollo", 20 },
        };

        static double EnginePrice(EngineDef e, int count) =>
            e == null ? 0 : Math.Max(1, count) * (EngineBase + EnginePerMN * e.ThrustVac / 1e6);

        /// <summary>Цена детали без операций пуска.</summary>
        public static double PartPrice(PartDef p)
        {
            if (p == null) return 0;
            double c = StructurePerTonne * p.DryMass / 1000 + CrewSeat * p.Crew;
            if (p.Engine != null) c += EnginePrice(p.Engine(), p.EngineCount);
            if (p.Id != null && PartExtra.TryGetValue(p.Id, out var extra)) c += extra;
            return c;
        }

        static double Ops(double massKg) => OpsBase + OpsPerTonne * massKg / 1000;

        /// <summary>Цена ракеты из деталей (§5.4): ядро + радиальные группы × симметрия + операции.</summary>
        public static double CraftPrice(Craft craft)
        {
            if (craft == null) return 0;
            double c = 0, m = 0;
            foreach (var cp in craft.Stack)
            {
                var p = PartCatalog.Resolve(cp);
                if (p == null) continue;
                c += PartPrice(p);
                m += p.DryMass + p.Propellant;
            }
            foreach (var r in craft.Radials)
                foreach (var id in r.Parts)
                {
                    var p = PartCatalog.Get(id);
                    if (p == null) continue;
                    int n = Math.Max(1, r.Symmetry);
                    c += n * PartPrice(p);
                    m += n * (p.DryMass + p.Propellant);
                }
            return Math.Round(c + Ops(m), 1);
        }

        /// <summary>Неизученные детали ракеты (id без повторов) — с ними карьера пуск не засчитывает.</summary>
        public static List<string> LockedParts(Craft craft, CareerState state)
        {
            var list = new List<string>();
            if (craft == null || state == null) return list;
            void Check(string id)
            {
                if (id != null && !state.IsPartUnlocked(id) && !list.Contains(id)) list.Add(id);
            }
            foreach (var cp in craft.Stack) Check(cp.Id);
            foreach (var r in craft.Radials)
                foreach (var id in r.Parts) Check(id);
            return list;
        }

        /// <summary>Цена секции пакета (исторические пресеты, возврат) — по той же формуле, что детали.</summary>
        public static double SectionPrice(SectionDef s)
        {
            double c = StructurePerTonne * s.DryMass / 1000 + CrewSeat * s.Crew + EnginePrice(s.Engine, s.EngineCount);
            if (s.DockingPort) c += PartExtra["dock-1"];
            if (s.LandingLegs) c += PartExtra["legs"];
            if (s.ParachuteArea > 0) c += PartExtra["chute"];
            if (s.RcsTorque > 0) c += PartExtra["rcs"];
            if (s.GridFins > 0) c += PartExtra["gridfins"];
            if (s.Wings != null) c += PartExtra["wing"] * s.Wings.Count;
            if (s.Kind == SectionKind.Capsule) c += PartExtra["cmd-vostok"];
            return c;
        }

        /// <summary>Цена исторической ракеты по секциям + операции.</summary>
        public static double DesignPrice(VesselDesign d)
        {
            if (d == null) return 0;
            double c = 0;
            foreach (var s in d.Sections) c += SectionPrice(s);
            return Math.Round(c + Ops(d.TotalMass), 1);
        }

        /// <summary>Возврат за целые секции борта (Attached), севшие на Землю: RecoveryFraction железа без операций.</summary>
        public static double RecoveryValue(Vessel v)
        {
            if (v?.Design == null || v.Attached == null) return 0;
            double c = 0;
            for (int i = 0; i < v.Attached.Length && i < v.Design.Sections.Count; i++)
                if (v.Attached[i]) c += SectionPrice(v.Design.Sections[i]);
            return Math.Round(RecoveryFraction * c, 1);
        }
    }
}
