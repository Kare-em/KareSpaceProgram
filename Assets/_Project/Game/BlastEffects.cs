using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Terrain = Kare.Space.Core.Terrain;

namespace Kare.Space.Game
{
    /// <summary>
    /// Гибель и пиротехника (GDD §9.5): огненный шар, дым, пыль или брызги и горящие обломки при Vessel.Destroy;
    /// хлопок пироболтов при отделении ступени.
    /// Не VFX Graph: граф через MCP не собрать (узлы не редактируются, у шаблонов нет exposed-параметров), а
    /// billboard'ы на материалах факела и дыма уже проверены с плавающим началом и экспозицией HDRP (как ExhaustTrail).
    /// Взрыв живёт в осях тела в двойной точности — облако стоит в воздухе и вращается с планетой; хлопок отделения —
    /// в осях обломка, иначе в вакууме ступень за секунду уходит от него на километры.
    /// Часы свои: в меню Esc огонь замирает вместе с полётом.
    /// </summary>
    public sealed class BlastEffects : MonoBehaviour
    {
        /// <summary>Огненный шар топлива: D = 3,86·m^0,32 м, длительность 0,299·m^0,32 с, m — кг топлива
        /// (эмпирика TNO для облаков горючего). Полный Р-7 (270 т) — шар Ø212 м на 16 с; пустой блок — Ø25 м.</summary>
        const double FireballDiameterK = 3.86, FireballTimeK = 0.299, FireballPower = 0.32;
        /// <summary>Доля сухой массы, идущая «в огонь» (остатки, удар): пустая ступень даёт шар, а не ноль.</summary>
        const double StructureShare = 0.05;
        const float MinRadius = 1.5f, MinTime = 0.8f, MaxTime = 12f;
        /// <summary>Плотность воздуха ниже этой, кг/м³ — вакуум: без кислорода шар — короткая вспышка, дыма нет.</summary>
        const double VacuumDensity = 1e-4;
        const float VacuumFireScale = 0.5f, VacuumTimeScale = 0.3f, WaterFireScale = 0.5f;
        /// <summary>Касание грунта: высота над ним в момент гибели, м — ниже взрыв кладётся на поверхность.</summary>
        const double GroundContact = 0.5;
        /// <summary>Дальше этого от начала координат (активного борта) взрыв не рисуем, м. Пара: VesselView.DrawDistance.</summary>
        const float CullDistance = 50000;

        /// <summary>Яркость одного огненного спрайта, нит. Аддитив складывает 4–6 слоёв в центре шара — 3–5·10³ нит,
        /// как ядро факела (VesselView.CoreNits = 3·10³); больше — bloom выбеливает кадр.</summary>
        const float FireNits = 8e2f;
        static readonly Color FireHot = new Color(1f, 0.85f, 0.55f), FireMid = new Color(1f, 0.45f, 0.15f),
                              FireCool = new Color(0.6f, 0.12f, 0.04f);
        static readonly Color SootColor = new Color(0.08f, 0.075f, 0.07f), SprayColor = new Color(0.9f, 0.92f, 0.95f),
                              PyroColor = new Color(0.92f, 0.91f, 0.9f), DebrisColor = new Color(0.18f, 0.17f, 0.16f, 1);
        /// <summary>Спрайтов на взрыв: огонь, дым, пыль/брызги.</summary>
        const int FirePerBlast = 12, SmokePerBlast = 14, DustPerBlast = 10;
        /// <summary>Обломки: 6 + m^0,25 штук (пустой блок — 10, полный Р-7 — 28), не больше FragMax; разлёт R·2,5 м/с
        /// в пределах [FragSpeedMin, FragSpeedMax]; торможение в воздухе за FragDrag с; живут FragLife с.</summary>
        const int FragMin = 6, FragMax = 30;
        const float FragSpeedK = 2.5f, FragSpeedMin = 15, FragSpeedMax = 120, FragDrag = 4, FragLife = 25;
        /// <summary>Накал обломка, нит, гаснет за FragCool с. Пара: VesselView.HeatGlowNits — эмиссия Lit на корпусе
        /// экспозицией почти не гасится, сотни нит дают белый кубик.</summary>
        const float FragGlowNits = 40, FragCool = 3;
        static readonly Color FragGlowTint = new Color(1f, 0.35f, 0.1f);
        /// <summary>Пул: огонь, дым, обломки, свет. Больше — старые гасятся раньше срока.</summary>
        const int FirePool = 96, SmokePool = 160, FragPool = 64, LightPool = 4;
        /// <summary>Свет шара: дальность в радиусах шара. Сила света = яркость × площадь диска (как у плазмы).</summary>
        const float LightRangeRadii = 12;
        /// <summary>Горение после взрыва (пак Vefects Free Fire HDRP): только где есть чем гореть — плотный воздух
        /// (Земля 1,2 кг/м³; Марс 0,02 — нет). Очаг на месте падения: масштаб префаба (пламя ≈ 1,6 м) = R·WreckFireK
        /// в [1, WreckFireMax]; горит R·WreckBurnK с в [WreckBurnMin, WreckBurnMax]. Пара: R — радиус шара выше.</summary>
        const double BurnDensity = 0.1;
        const float WreckFireK = 0.12f, WreckFireMax = 12, WreckBurnK = 2, WreckBurnMin = 30, WreckBurnMax = 180;
        /// <summary>Горящих обломков на взрыв и масштаб огня к размеру осколка. Пара: FragLife — огонь гаснет раньше.</summary>
        const int BurningFrags = 3;
        const float FragFireK = 1.5f;
        /// <summary>Сколько секунд после остановки эмиссии дожидаться, пока догорят частицы (дым Vefects живёт до 5 с).</summary>
        const float FireFadeOut = 6;
        /// <summary>Яркость материалов Vefects × это. Пак откалиброван под тёмную сцену (пламя 33 нит, квад 123 нит), у нас
        /// экспозиция дневного Солнца — без множителя огонь днём не виден. ×25 даёт ≈ 800 нит, как FireNits.</summary>
        const float VefectsEmissionBoost = 25;

        /// <summary>Подъём центра клуба над грунтом в его размерах и мягкое подхождение камеры — как у ExhaustTrail.</summary>
        const float PuffLift = 0.6f, PuffFadeNear = 0.6f, PuffFadeRange = 1.2f;

        sealed class Blast
        {
            public CelestialBody Body;
            /// <summary>Центр в осях тела; у хлопка отделения — точка на обломке в его осях (Local).</summary>
            public Vector3d Center, Local, Up;
            public Vessel Follow;
            /// <summary>Сколько метров от центра вниз до грунта; ∞ — грунта рядом нет.</summary>
            public double GroundBelow = double.PositiveInfinity;
            public float Born, End, Radius, Time, Gravity;
            public Light Light;
            public bool Dead;
        }

        sealed class Sprite
        {
            public Blast B;
            public Vector3d Pos, Vel;
            public float Born, Life, Size0, Size1, Drag, Rise, Strength;
            public bool Fire, Gravity, Active;
            public Color Tint;
            public Transform Tr;
            public Renderer R;
        }

        sealed class Frag
        {
            public Blast B;
            public Vector3d Pos, Vel;
            public Vector3 Axis;
            public float Born, Size, Spin, Heat;
            public Quaternion Rot;
            public bool Active, Resting;
            public Transform Tr;
            public Renderer R;
        }

        /// <summary>Экземпляр префаба огня: точка в осях тела (или на осколке); не вращается вместе с осколком.</summary>
        sealed class Fire
        {
            public Blast B;
            public Frag F;
            public Vector3d Off;
            public float StopAt;
            public bool Stopped, Paused;
            public GameObject Go;
            public ParticleSystem Ps;
        }

        GameObject wreckFire, debrisFire;
        readonly List<Fire> burning = new List<Fire>();
        readonly Dictionary<Material, Material> boosted = new Dictionary<Material, Material>();

        readonly Sprite[] fires = new Sprite[FirePool], smokes = new Sprite[SmokePool];
        readonly Frag[] frags = new Frag[FragPool];
        readonly Light[] lights = new Light[LightPool];
        readonly List<Blast> blasts = new List<Blast>();
        int nextFire, nextSmoke, nextFrag, nextLight;
        float clock;
        MaterialPropertyBlock mpb;
        readonly System.Random rng = new System.Random(1961);

        readonly HashSet<Vessel> known = new HashSet<Vessel>(), alive = new HashSet<Vessel>();
        readonly List<Vessel> lost = new List<Vessel>();
        bool primed;

        public void Init(Material plume, Material smoke, Material hull)
        {
            mpb = new MaterialPropertyBlock();
            var fireMat = new Material(plume) { name = "Blast Fire (runtime)" };
            fireMat.SetTexture("_EmissiveColorMap", FireTexture());
            fireMat.EnableKeyword("_EMISSIVE_COLOR_MAP");
            var smokeMat = new Material(smoke) { name = "Blast Smoke (runtime)" };
            smokeMat.SetTexture("_BaseColorMap", ExhaustTrail.PuffTexture());
            var quad = ProcMesh.Billboard();
            for (int i = 0; i < FirePool; i++) fires[i] = NewSprite("Fire", quad, fireMat, true);
            for (int i = 0; i < SmokePool; i++) smokes[i] = NewSprite("Smoke", quad, smokeMat, false);
            // Осколок — кривая пятигранная призма: от кубика отличается силуэтом при вращении.
            var shard = ProcMesh.Frustum(0.6f, 0.25f, 1f, 5, true);
            for (int i = 0; i < FragPool; i++)
            {
                var go = new GameObject("Fragment");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = shard;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = hull;
                r.enabled = false;
                frags[i] = new Frag { Tr = go.transform, R = r };
            }
            for (int i = 0; i < LightPool; i++)
            {
                var go = new GameObject("Blast Light");
                go.transform.SetParent(transform, false);
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                go.AddComponent<HDAdditionalLightData>();
                l.lightUnit = UnityEngine.Rendering.LightUnit.Candela;
                l.shadows = LightShadows.None;
                l.enabled = false;
                lights[i] = l;
            }
        }

        /// <summary>Префабы горения (Vefects); без них — только огненный шар и дым кодом.</summary>
        public void SetFirePrefabs(GameObject wreck, GameObject debris)
        {
            wreckFire = wreck;
            debrisFire = debris;
        }

        void SpawnFire(GameObject prefab, Blast b, Frag f, Vector3d off, float scale, float burn)
        {
            var go = Instantiate(prefab, transform);
            go.transform.localScale = Vector3.one * scale;
            // Плавающее начало двигает мир каждый кадр: частицы в World-пространстве остались бы позади и тянулись
            // шлейфом на километры. В Local они живут с якорем; масштаб — через иерархию.
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var m = ps.main;
                m.simulationSpace = ParticleSystemSimulationSpace.Local;
                m.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            foreach (var r in go.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) if (mats[i] != null) mats[i] = Boost(mats[i]);
                r.sharedMaterials = mats;
            }
            var fire = new Fire { B = b, F = f, Off = off, StopAt = clock + burn, Go = go, Ps = go.GetComponent<ParticleSystem>() };
            if (fire.Ps == null) fire.Ps = go.GetComponentInChildren<ParticleSystem>();
            burning.Add(fire);
            PlaceFire(fire);
        }

        Material Boost(Material src)
        {
            if (boosted.TryGetValue(src, out var m)) return m;
            m = new Material(src) { name = src.name + " (boost)" };
            foreach (var p in new[] { "_EmissionIntensity", "_EmissiveIntensity" })
                if (m.HasProperty(p)) m.SetFloat(p, m.GetFloat(p) * VefectsEmissionBoost);
            boosted[src] = m;
            return m;
        }

        void PlaceFire(Fire f)
        {
            var off = f.F != null ? f.F.Pos : f.Off;
            f.Go.transform.SetPositionAndRotation(ToUnity(f.B, off), Quaternion.FromToRotation(Vector3.up, ToUnityDir(f.B.Body, f.B.Up)));
        }

        static Vector3 ToUnityDir(CelestialBody body, Vector3d upBf)
        {
            var d = body.Orientation * upBf;
            return new Vector3((float)d.x, (float)d.z, (float)d.y);
        }

        void UpdateFires(bool show, bool paused)
        {
            for (int i = burning.Count - 1; i >= 0; i--)
            {
                var f = burning[i];
                bool gone = f.F != null && (!f.F.Active || f.F.B != f.B);
                if (!f.Stopped && (clock >= f.StopAt || gone || f.B.Dead))
                {
                    f.Stopped = true;
                    f.StopAt = clock;
                    if (f.Ps != null) f.Ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
                if (f.Stopped && clock > f.StopAt + FireFadeOut)
                {
                    Destroy(f.Go);
                    burning.RemoveAt(i);
                    continue;
                }
                if (f.Go.activeSelf != show)
                {
                    f.Go.SetActive(show);
                    // Включение заново запускает playOnAwake — догорающий очаг не должен вспыхнуть снова.
                    if (show && f.Stopped && f.Ps != null) f.Ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
                if (!show) continue;
                if (f.Ps != null && paused != f.Paused)
                {
                    f.Paused = paused;
                    if (paused) f.Ps.Pause(true);
                    else f.Ps.Play(true);
                    if (!paused && f.Stopped) f.Ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
                if (f.F == null || !gone) PlaceFire(f);
            }
        }

        Sprite NewSprite(string name, Mesh quad, Material m, bool fire)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            return new Sprite { Tr = go.transform, R = r, Fire = fire };
        }

        /// <summary>
        /// Раз в кадр после шага Universe: гибель — по переходу Alive → false; мёртвый неактивный борт Universe
        /// убирает из списка в тот же шаг, поэтому ловим ещё и пропажу. Новый обломок после первого кадра — отделение.
        /// </summary>
        public void Watch(Universe u)
        {
            foreach (var v in u.Vessels)
            {
                if (known.Add(v) && primed && v.IsDebris && v.Alive) Pyro(v);
                if (v.Alive) alive.Add(v);
                else if (alive.Remove(v)) Explode(v);
            }
            lost.Clear();
            foreach (var v in alive) if (!u.Vessels.Contains(v)) lost.Add(v);
            foreach (var v in lost)
            {
                alive.Remove(v);
                known.Remove(v);
                if (!v.Alive) Explode(v);
            }
            primed = true;
        }

        float Rand(float a, float b) => a + (b - a) * (float)rng.NextDouble();

        Vector3d RandomDir(Vector3d up, bool hemisphere)
        {
            Vector3d d;
            do d = new Vector3d(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
            while (d.sqrMagnitude > 1 || d.sqrMagnitude < 1e-4);
            d = d.normalized;
            if (hemisphere && Vector3d.Dot(d, up) < 0) d -= up * (2 * Vector3d.Dot(d, up));
            return d;
        }

        Vector3d Flat(Vector3d up)
        {
            var e1 = Vector3d.AnyPerpendicular(up).normalized;
            var e2 = Vector3d.Cross(up, e1);
            double a = rng.NextDouble() * 2 * System.Math.PI;
            return e1 * System.Math.Cos(a) + e2 * System.Math.Sin(a);
        }

        void Explode(Vessel v)
        {
            var body = v.DestroyedOn ?? v.Body;
            // В глубинах газового гиганта и в Солнце смотреть не на что.
            if (body == null || body.IsGasGiant || body.Parent == null) return;
            if (FloatingOrigin.ToUnity(body.Position + v.Position).magnitude > CullDistance) return;

            var c = body.Orientation.Inverse * v.Position;
            var up = c.normalized;
            double h = body.SurfaceHeight(up);
            double ground = body.Radius + h;
            bool onGround = v.TerrainAltitude <= GroundContact || c.magnitude < ground;
            bool water = onGround && body.Terrain != null && body.Terrain.Ocean && h <= 0 &&
                         Terrain.RawHeight(body.Terrain, up) < 0;
            if (onGround) c = up * ground;
            bool air = v.Density > VacuumDensity;

            v.MassProperties(out double mass, out _, out _, out _);
            double fuel = 0;
            for (int i = 0; i < v.Propellant.Length; i++) if (v.Attached[i]) fuel += v.Propellant[i];
            double k = System.Math.Pow(System.Math.Max(1, fuel + StructureShare * mass), FireballPower);
            float radius = Mathf.Max(MinRadius, (float)(FireballDiameterK * k * 0.5));
            float time = Mathf.Clamp((float)(FireballTimeK * k), MinTime, MaxTime);
            if (!air) { radius *= VacuumFireScale; time *= VacuumTimeScale; }
            if (water) radius *= WaterFireScale;
            float g = (float)(body.Mu / (ground * ground));

            var b = new Blast
            {
                Body = body, Center = c, Up = up, Born = clock, Radius = radius, Time = Mathf.Max(time, MinTime), Gravity = g,
                GroundBelow = onGround ? 0 : c.magnitude - ground,
            };
            float end = b.Time;

            // Огонь: клубы разлетаются до ~0,7 R и всплывают (в воздухе), цвет от жёлто-белого к тёмно-красному.
            float tau = b.Time * 0.25f;
            for (int i = 0; i < FirePerBlast; i++)
            {
                var d = RandomDir(up, onGround);
                var s = Take(fires, ref nextFire);
                Set(s, b, d * (radius * 0.15), d * (radius * Rand(0.4f, 0.7f) / tau), Rand(0, b.Time * 0.15f),
                    b.Time * Rand(0.6f, 1f), radius * 0.35f, radius * 0.7f, tau, air ? radius * 0.5f / b.Time : 0, false, FireHot, 1);
            }

            if (air && !water)
            {
                // Копоть: занимается к середине шара, всплывает и растёт.
                float life = Mathf.Clamp(b.Time * 5, 8, 45);
                for (int i = 0; i < SmokePerBlast; i++)
                {
                    var d = RandomDir(up, true);
                    var s = Take(smokes, ref nextSmoke);
                    Set(s, b, d * (radius * 0.4), d * (radius * 0.2 / tau), b.Time * Rand(0.3f, 0.8f), life * Rand(0.7f, 1f),
                        radius * 0.5f, radius * 1.4f, tau * 4, radius * 0.6f / b.Time + 2, false, SootColor, 0.85f);
                }
                end = Mathf.Max(end, b.Time * 0.8f + life);
            }

            if (onGround && !water)
            {
                // Пыль грунта: кольцом по земле; в вакууме — баллистикой без торможения.
                var look = BodyVisuals.Get(body.Id);
                var dust = Color.Lerp(look.Low, look.High, 0.5f);
                float life = air ? 10 : 6;
                for (int i = 0; i < DustPerBlast; i++)
                {
                    var d = Flat(up);
                    var vel = air ? d * (radius * 1.5 / 1.5) : d * (radius * 0.8) + up * (radius * 0.6);
                    var s = Take(smokes, ref nextSmoke);
                    Set(s, b, Vector3d.zero, vel, Rand(0, 0.2f), life * Rand(0.7f, 1f), radius * 0.4f, radius * 1.2f,
                        air ? 1.5f : 0, 0, !air, dust, 0.6f);
                }
                end = Mathf.Max(end, life);
            }
            else if (water)
            {
                // Столб брызг: вверх на ~1,5 R и обратно.
                float vUp = Mathf.Sqrt(2 * g * radius * 1.5f);
                float life = Mathf.Clamp(2 * vUp / g, 1.5f, 8);
                for (int i = 0; i < DustPerBlast; i++)
                {
                    var vel = up * (vUp * Rand(0.6f, 1f)) + Flat(up) * (vUp * Rand(0.1f, 0.35f));
                    var s = Take(smokes, ref nextSmoke);
                    Set(s, b, Vector3d.zero, vel, Rand(0, 0.15f), life, radius * 0.25f, radius * 0.7f, 0, 0, true, SprayColor, 0.8f);
                }
                end = Mathf.Max(end, life);
            }

            // Обломки корпуса: разлёт, торможение воздухом, падение, лежат на грунте.
            int n = Mathf.Clamp(FragMin + (int)System.Math.Pow(System.Math.Max(1, fuel + StructureShare * mass), 0.25), FragMin, FragMax);
            float hull = (float)v.HullRadius();
            float speed = Mathf.Clamp(radius * FragSpeedK, FragSpeedMin, FragSpeedMax);
            for (int i = 0; i < n; i++)
            {
                var f = frags[nextFrag];
                nextFrag = (nextFrag + 1) % FragPool;
                f.B = b;
                f.Active = true;
                f.Resting = false;
                f.Born = clock;
                f.Pos = Vector3d.zero;
                f.Vel = RandomDir(up, onGround) * (speed * Rand(0.4f, 1f));
                f.Size = Mathf.Clamp(hull * Rand(0.15f, 0.45f), 0.1f, 2.5f);
                f.Axis = Random.onUnitSphere;
                f.Spin = Rand(90, 540);
                f.Rot = Random.rotation;
                f.Heat = water ? 0 : 1;
            }
            end = Mathf.Max(end, FragLife);

            if (onGround && !water && v.Density > BurnDensity && wreckFire != null)
            {
                // Очаг: 1 на мелочь, до 3 на полную ступень — разлитое топливо горит пятнами.
                int spots = radius > 60 ? 3 : radius > 20 ? 2 : 1;
                float scale = Mathf.Clamp(radius * WreckFireK, 1, WreckFireMax);
                float burn = Mathf.Clamp(radius * WreckBurnK, WreckBurnMin, WreckBurnMax);
                for (int i = 0; i < spots; i++)
                    SpawnFire(wreckFire, b, null, i == 0 ? Vector3d.zero : Flat(up) * (radius * Rand(0.2f, 0.45f)),
                              scale * Rand(0.7f, 1f), burn * Rand(0.7f, 1f));
                end = Mathf.Max(end, burn + FireFadeOut);
            }
            if (!water && v.Density > BurnDensity && debrisFire != null)
                for (int i = 0, j = nextFrag; i < Mathf.Min(BurningFrags, n); i++)
                {
                    j = (j - 1 + FragPool) % FragPool;
                    var f = frags[j];
                    SpawnFire(debrisFire, b, f, Vector3d.zero, Mathf.Max(0.5f, f.Size * FragFireK), FragLife - 3);
                }

            b.Light = lights[nextLight];
            nextLight = (nextLight + 1) % LightPool;
            foreach (var other in blasts) if (other.Light == b.Light) other.Light = null;
            b.Light.color = FireMid;
            b.End = clock + end;
            blasts.Add(b);
        }

        /// <summary>Хлопок пироболтов (§6.6): вспышка и белое облачко у верхнего торца обломка, летят вместе с ним.</summary>
        void Pyro(Vessel v)
        {
            if (FloatingOrigin.ToUnity(FloatingOrigin.WorldP(v)).magnitude > CullDistance) return;
            v.MassProperties(out _, out double com, out double len, out _);
            float r = (float)v.HullRadius();
            bool air = v.Density > VacuumDensity;
            var up = v.NoseP;
            var b = new Blast
            {
                Body = v.Body, Follow = v, Local = new Vector3d(0, len - com, 0), Up = up, Born = clock,
                Radius = r, Time = 0.15f,
            };
            var flash = Take(fires, ref nextFire);
            Set(flash, b, Vector3d.zero, Vector3d.zero, 0, 0.15f, r * 1.5f, r * 2, 0, 0, false, FireHot, 2);
            float life = air ? 2.5f : 1.2f;
            for (int i = 0; i < 6; i++)
            {
                var d = (Flat(up) + up * 0.2).normalized;
                var s = Take(smokes, ref nextSmoke);
                Set(s, b, d * (r * 0.8), d * (air ? 8 : 15), Rand(0, 0.05f), life * Rand(0.8f, 1f), r * 0.3f, r * (air ? 1.8f : 3f),
                    air ? 0.6f : 3f, 0, false, PyroColor, air ? 0.7f : 0.4f);
            }
            b.End = clock + life + 0.1f;
            blasts.Add(b);
        }

        static Sprite Take(Sprite[] pool, ref int next)
        {
            var s = pool[next];
            next = (next + 1) % pool.Length;
            return s;
        }

        void Set(Sprite s, Blast b, Vector3d pos, Vector3d vel, float delay, float life, float size0, float size1,
                 float drag, float rise, bool gravity, Color tint, float strength)
        {
            s.B = b; s.Pos = pos; s.Vel = vel; s.Born = clock + delay; s.Life = life; s.Size0 = size0; s.Size1 = size1;
            s.Drag = drag; s.Rise = rise; s.Gravity = gravity; s.Tint = tint; s.Strength = strength; s.Active = true;
        }

        /// <summary>Точка взрыва в Unity: у хлопка — от обломка, у взрыва — от тела.</summary>
        static Vector3 ToUnity(Blast b, Vector3d off)
        {
            if (b.Follow != null) return FloatingOrigin.ToUnity(FloatingOrigin.WorldP(b.Follow) + b.Follow.LocalToWorld(b.Local) + off);
            return FloatingOrigin.ToUnity(b.Body.Position + b.Body.Orientation * (b.Center + off));
        }

        /// <summary>Шаг частицы: торможение exp(−dt/τ), тяжесть по −Up, лежит — если ушла под грунт.</summary>
        static bool Step(Blast b, ref Vector3d pos, ref Vector3d vel, float drag, bool gravity, float dt)
        {
            if (drag > 0) vel *= System.Math.Exp(-dt / drag);
            if (gravity) vel -= b.Up * (b.Gravity * dt);
            pos += vel * dt;
            if (!gravity || double.IsInfinity(b.GroundBelow)) return false;
            double h = Vector3d.Dot(pos, b.Up) + b.GroundBelow;
            if (h >= 0) return false;
            pos -= b.Up * h;
            vel = Vector3d.zero;
            return true;
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            if (u == null || mpb == null) return;
            float dt = PauseMenu.IsOpen ? 0 : Time.deltaTime;
            clock += dt;
            foreach (var b in blasts)
                if (b.Follow != null && !b.Dead && (!b.Follow.Alive || !u.Vessels.Contains(b.Follow))) b.Dead = true;

            bool show = !MapView.IsOpen;
            var cam = Camera.main;
            var camRot = cam != null ? cam.transform.rotation : Quaternion.identity;
            var eye = cam != null ? cam.transform.position : Vector3.zero;
            foreach (var s in fires) UpdateSprite(s, show, dt, camRot, eye);
            foreach (var s in smokes) UpdateSprite(s, show, dt, camRot, eye);
            foreach (var f in frags) UpdateFrag(f, show, dt);
            UpdateFires(show, PauseMenu.IsOpen);

            foreach (var b in blasts)
            {
                if (b.Light == null) continue;
                float age = clock - b.Born;
                bool on = show && !b.Dead && b.Follow == null && age < b.Time;
                b.Light.enabled = on;
                if (!on) continue;
                float glow = 1 - age / b.Time;
                glow *= glow;
                b.Light.transform.position = ToUnity(b, b.Up * (b.Radius * 0.3));
                // Сила света = яркость × площадь диска шара (как у плазмы входа), кд.
                b.Light.intensity = FireNits * 4 * glow * Mathf.PI * b.Radius * b.Radius;
                b.Light.range = b.Radius * LightRangeRadii;
            }
            for (int i = blasts.Count - 1; i >= 0; i--)
            {
                if (clock < blasts[i].End && !(blasts[i].Dead && blasts[i].Follow != null)) continue;
                if (blasts[i].Light != null) blasts[i].Light.enabled = false;
                blasts[i].Dead = true;
                blasts.RemoveAt(i);
            }
        }

        void UpdateSprite(Sprite s, bool show, float dt, Quaternion camRot, Vector3 eye)
        {
            if (!s.Active) return;
            float age = clock - s.Born;
            if (age > s.Life || s.B.Dead) { s.Active = false; s.R.enabled = false; return; }
            if (age < 0) { s.R.enabled = false; return; }
            Step(s.B, ref s.Pos, ref s.Vel, s.Drag, s.Gravity, dt);
            s.Pos += s.B.Up * (s.Rise * dt);
            float a = age / s.Life;
            float size = Mathf.Lerp(s.Size0, s.Size1, Mathf.Sqrt(a));
            var off = s.Pos;
            if (!s.Fire && s.B.GroundBelow == 0)
            {
                // Низ клуба не режет грунт прямой линией (как у ExhaustTrail): центр не ниже PuffLift·размер.
                double lift = size * PuffLift - Vector3d.Dot(off, s.B.Up);
                if (lift > 0) off += s.B.Up * lift;
            }
            var pos = ToUnity(s.B, off);
            s.R.enabled = show;
            if (!show) return;
            s.Tr.SetPositionAndRotation(pos, camRot);
            s.Tr.localScale = Vector3.one * size;
            float fadeIn = Mathf.Clamp01(age * (s.Fire ? 20 : 3));
            float life = 1 - a;
            mpb.Clear();
            if (s.Fire)
            {
                var c = a < 0.4f ? Color.Lerp(FireHot, FireMid, a / 0.4f) : Color.Lerp(FireMid, FireCool, (a - 0.4f) / 0.6f);
                var e = c * (FireNits * s.Strength * fadeIn * life * life);
                e.a = 1;
                mpb.SetColor("_EmissiveColor", e);
            }
            else
            {
                float alpha = s.Strength * fadeIn * Mathf.Pow(life, 1.5f);
                float d = Vector3.Distance(eye, pos) / Mathf.Max(size, 0.01f);
                alpha *= Mathf.SmoothStep(0, 1, (d - PuffFadeNear) / PuffFadeRange);
                mpb.SetColor("_BaseColor", new Color(s.Tint.r, s.Tint.g, s.Tint.b, alpha));
            }
            s.R.SetPropertyBlock(mpb);
        }

        void UpdateFrag(Frag f, bool show, float dt)
        {
            if (!f.Active) return;
            float age = clock - f.Born;
            if (age > FragLife || f.B.Dead) { f.Active = false; f.R.enabled = false; return; }
            bool air = f.B.Follow == null && f.B.Body.HasAtmosphere && !double.IsInfinity(f.B.GroundBelow) && f.B.GroundBelow < 1e5;
            if (!f.Resting && Step(f.B, ref f.Pos, ref f.Vel, air ? FragDrag : 0, true, dt)) f.Resting = true;
            if (!f.Resting) f.Rot = Quaternion.AngleAxis(f.Spin * dt, f.Axis) * f.Rot;
            f.R.enabled = show;
            if (!show) return;
            // Последние 2 с осколок «уходит» в масштаб — без щелчка исчезновения.
            float scale = f.Size * Mathf.Clamp01((FragLife - age) / 2);
            f.Tr.SetPositionAndRotation(ToUnity(f.B, f.Pos), f.Rot);
            f.Tr.localScale = Vector3.one * scale;
            mpb.Clear();
            mpb.SetColor("_BaseColor", DebrisColor);
            var glow = FragGlowTint * (FragGlowNits * f.Heat * Mathf.Exp(-age / FragCool));
            glow.a = 1;
            mpb.SetColor("_EmissiveColor", glow);
            f.R.SetPropertyBlock(mpb);
        }

        /// <summary>Огненный клуб: горячий центр, рваный край шумом, альфа-окно до нуля у края квадрата.</summary>
        static Texture2D fireTex;
        static Texture2D FireTexture()
        {
            if (fireTex != null) return fireTex;
            const int n = 128;
            fireTex = new Texture2D(n, n, TextureFormat.RGBAHalf, true, true) { name = "Blast Fire", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float noise = 0.6f * Mathf.PerlinNoise(x * 0.07f + 5, y * 0.07f + 9) + 0.4f * Mathf.PerlinNoise(x * 0.19f + 13, y * 0.19f + 2);
                float rn = r * (1 + 0.5f * (noise - 0.5f));
                float f = 1 - Mathf.SmoothStep(0.1f, 0.85f, rn);
                f *= 1 - Mathf.SmoothStep(0.8f, 0.98f, r);
                f *= f;
                var c = Color.Lerp(new Color(1f, 0.55f, 0.25f), new Color(1f, 0.95f, 0.8f), f);
                fireTex.SetPixel(x, y, new Color(c.r * f, c.g * f, c.b * f, 1));
            }
            fireTex.Apply(true, true);
            return fireTex;
        }
    }
}
