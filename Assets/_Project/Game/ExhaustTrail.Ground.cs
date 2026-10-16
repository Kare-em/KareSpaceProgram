using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Облако газа и пыли у грунта на старте и посадке (GDD §9.5). Две физики:
    /// — в воздухе (ρ ≥ DensityFull): струя бьёт в стол и грунт, газ с паром растекается по земле валом, воздух его тормозит,
    ///   горячее всплывает, клубы растут, облако сносит ветром, висит десятки секунд. У стола с лотком (Р-7 — на юг, LC-39 —
    ///   север и юг, LaunchPadView.FlameTrench) основная масса уходит лотком, а когда ракета поднялась — растекается и по плите;
    /// — без воздуха (Луна) и почти без (Марс): пыль летит плоским веером под малым углом по баллистике тела, без всплытия;
    ///   разреженный воздух её чуть тормозит, и она висит дольше.
    /// Размер, дальность, частота клубов и жизнь — от тяги (Vessel.CurrentThrust): облако Saturn V в разы больше, чем у «Редстоуна».
    /// Подсветка факелом: Lit сам освещает сторону клуба к свету факела, но не умеет рассеяние вперёд — свет, прошедший сквозь
    /// облако к камере (облако у стола ночью горит изнутри). Его добавляем эмиссией по I/d² от «Plume Light» борта.
    /// Пул — один на борт и создаётся при первом клубе: у бортов, что не стартуют с грунта, лишних объектов нет.
    /// </summary>
    public sealed partial class ExhaustTrail
    {
        // ---- масштаб от тяги
        /// <summary>Опорная тяга облака, Н: Р-7 на старте ≈ 4 МН — облако «единичного» размера. Линейный размер ∝ тяга^⅓
        /// (объём газа и пара ∝ расходу): Saturn V 34 МН → ×2,0, Falcon 9 7,6 МН → ×1,2, «Редстоун» 0,37 МН → ×0,45.</summary>
        const double RefThrust = 4e6;
        const float GroundScaleMin = 0.35f, GroundScaleMax = 3f;
        /// <summary>Опорная тяга пыли, Н: посадочный двигатель лунного модуля (45 кН) поднимал пыль с ~30 м (DustHeight; Армстронг:
        /// «picking up some dust» на 100 футах). Starship HLS на Луне — ×3.</summary>
        const double RefDustThrust = 4.5e4;
        const float DustScaleMin = 0.6f, DustScaleMax = 3f;

        // ---- пул
        /// <summary>Клубов на борт. Пара: частота рождения не выше GroundPool / наибольшая жизнь (CloudLifeMax·LifeJitterMax) —
        /// тогда слот к повторному рождению уже погас, и клубы не исчезают рывком.</summary>
        const int GroundPool = 240;
        /// <summary>Залп при зажигании: столько клубов ×k сразу, если до того BurstGap с не было ни одного — вал из лотка
        /// появляется разом, а не по одному клубу. Сверху — GroundBurstMax.</summary>
        const float BurstPuffs = 10;
        const int GroundBurstMax = 24;
        const double BurstGap = 2;
        /// <summary>Разброс жизни клуба: ×(1 ± LifeJitter).</summary>
        const float LifeJitter = 0.2f, LifeJitterMax = 1 + LifeJitter;

        // ---- облако в воздухе
        /// <summary>Клубы рождаются, пока срез ниже CloudHeight·k над грунтом (не ниже CloudHeightMin), CloudRate·k клубов/с.
        /// 100 м — Р-7 набирает их за ≈ 9 с: на хрониках облако у стола растёт столько же. Пара: GroundPool — при 60 м пул
        /// заполнялся на треть, а облако жило 6 с и с камеры за ракетой не читалось (замер 15.10: 35 клубов к T+5 с).</summary>
        const float CloudHeight = 100, CloudHeightMin = 25, CloudRate = 10;
        /// <summary>Полуразмер при рождении и рост, м и м/√с (×k): R-7 через 9 с ≈ 31 м, через 18 с ≈ 41 м, Saturn V через 26 с
        /// ≈ 95 м полуразмера. Было 4 и 4,5: к T+9 клубы по 15 м на поле 200 м, с треноги в 320 м облако — отдельные клочья
        /// (замер 16.10.2026, 49 клубов). Пара: CloudAlpha — крупные клубы перекрываются, суммарная плотность растёт.</summary>
        const float CloudSize0 = 7, CloudGrowth = 8;
        /// <summary>Разлёт по грунту: начальная скорость, м/с (×√k), её гасит воздух за CloudDrag с (×√k) — дальность валa
        /// CloudSpeed·CloudDrag·k ≈ 110 м у Р-7 и ≈ 230 м у Saturn V; дальше облако расползается вовлечением воздуха CloudCreep, м/с.</summary>
        const float CloudSpeed = 32, CloudDrag = 3.5f, CloudCreep = 1.2f;
        /// <summary>Всплытие: горячий газ поднимается RiseHot м/с, остывая за RiseHotTime с (+15 м), дальше — RiseCold м/с.
        /// Множится на близость среза к грунту — клубы под самой струёй горячее.</summary>
        const float RiseHot = 5, RiseHotTime = 3, RiseCold = 0.8f;
        /// <summary>Жизнь, с: CloudLife + CloudLifePerScale·k, не больше CloudLifeMax: R-7 18 с, Saturn V 26 с, Starship 30 с.</summary>
        const float CloudLife = 10, CloudLifePerScale = 8, CloudLifeMax = 30;
        const float CloudAlpha = 0.85f;
        /// <summary>Ветер у грунта, м/с: облако сносит целиком (направление — случайное на борт).</summary>
        const float WindSpeed = 2.5f;
        /// <summary>Вал у грунта приплюснут: высота клуба GroundFlat от ширины при рождении, к FlatTime с — круглый.
        /// Сверху сплющивать нечего — по наклону взгляда к вертикали сходит на 1.</summary>
        const float GroundFlat = 0.6f, FlatTime = 10;
        /// <summary>Лоток: доля клубов в лоток (у плиты), разброс поперёк, рад; доля тает к TrenchFade·высоты облака —
        /// струя поднявшейся ракеты бьёт уже в плиту; в лотке газ разгоняется (TrenchBoost).
        /// 0,55, а не 0,8: пар системы подавления встаёт и вокруг плиты, а камера по умолчанию (FlightCamera.Yaw) стоит
        /// со стороны лотка Р-7 — при 0,8 почти весь газ пролетал сквозь неё и гас вблизи (PuffFadeNear), облака не было видно.</summary>
        const float TrenchShare = 0.55f, TrenchSpread = 0.25f, TrenchFade = 0.5f, TrenchBoost = 1.4f;
        /// <summary>Свежий дым темнее (сажа керосина, пыль с бетона), к SmokeAgeing с — белый пар SmokeColor.</summary>
        static readonly Color YoungSmoke = new Color(0.60f, 0.57f, 0.53f);
        const float SmokeAgeing = 6;
        /// <summary>Примесь грунта в клубе: на бетоне стола (пар системы подавления, LaunchPadView.OnPad) и в поле.</summary>
        const float PadSoil = 0.06f, FieldSoil = 0.3f;
        /// <summary>Случайный наклон клуба, град, и его медленное вращение — PuffSpin.</summary>
        const float RollJitter = 20;

        // ---- подсветка факелом
        /// <summary>Рассеяние вперёд: доля освещённости клуба от факела, видимая сквозь него, и альбедо дыма —
        /// L = FlameScatter·I/d²·SmokeAlbedo/π, нит. Расстояние не меньше GlowMinDist и GlowNear·размера клуба (свет внутри
        /// клуба не даёт бесконечности). Пара: сила света — VesselView.PlumeLight (кд), экспозиция ночью — по факелу.</summary>
        const float FlameScatter = 0.6f, SmokeAlbedo = 0.85f, GlowMinDist = 4, GlowNear = 0.8f;
        /// <summary>Пересбор списка «Plume Light» борта, с: ступени отделяются, источники меняются.</summary>
        const float LightRescan = 1;

        // ---- пыль без воздуха
        /// <summary>Пыль поднимается со среза ниже DustHeight·k, DustRate·k клубов/с.</summary>
        const float DustHeight = 30, DustRate = 20;
        /// <summary>Скорость веера, м/с (×√k, разброс 0,5…1,3), угол к грунту DustTiltMin…DustTiltMax рад (1…5°): пыль летит
        /// почти горизонтально и падает по g тела (Луна: 90 м/с под 3° — ≈ 3 с полёта, ≈ 250 м). Пара: DustLife.</summary>
        const float DustSpeed = 90, DustTiltMin = 0.015f, DustTiltMax = 0.08f;
        /// <summary>Жизнь пыли, с; в разреженном воздухе (Марс) висит дольше: ×(1 + DustLinger·ρ/DensityFull).</summary>
        const float DustLife = 3, DustLinger = 3;
        /// <summary>Торможение пыли воздухом: τ = DustAirDrag/ρ с (Марс 0,015 кг/м³ → 3 с; в вакууме — без торможения).</summary>
        const float DustAirDrag = 0.05f;
        /// <summary>Полуразмер, м, и рост, м/с (×√k; в воздухе ×(1 + DustAirGrowth·ρ/DensityFull)).</summary>
        const float DustSize = 1.5f, DustGrowth = 3, DustAirGrowth = 2;
        const float DustAlpha = 0.3f;
        /// <summary>Веер — плоские струи, а не шары: клуб вытянут вдоль полёта на экране в DustStretch раз, сбоку тонок (DustFlat).</summary>
        const float DustStretch = 3.5f, DustFlat = 0.35f;

        sealed class Puff
        {
            public Vector3d Ground, Dir, Up, Wind;
            public double Born = double.NegativeInfinity;
            /// <summary>Tau: торможение, с (0 — нет); Rise — сила всплытия; Vz, Gravity — баллистика пыли; Soil — примесь грунта.</summary>
            public float Life, Size0, Growth, Speed, Tau, Creep, Rise, Vz, Gravity, Strength, Soil, Roll, Spin, Flip;
            public bool Dust;
            public Transform Tr;
            public Renderer R;
        }

        Material smokeSource;
        Puff[] ground;
        int nextGround;
        double lastGround = double.NegativeInfinity, groundUntil = double.NegativeInfinity;
        bool groundShown;
        /// <summary>Цвет почвы (BodyVisuals.High) — примесь к облаку в воздухе; у пыли без воздуха — dustColor.</summary>
        Color soilColor;
        float windAngle = float.NaN;
        readonly List<Light> plumeLights = new List<Light>();
        float lightsAt = float.NegativeInfinity;
        Vector3 glowPos;
        Color glowColor;
        float glowI;

        void ResetGround()
        {
            if (ground != null) foreach (var p in ground) p.Born = double.NegativeInfinity;
            lastGround = double.NegativeInfinity;
            groundUntil = double.NegativeInfinity;
        }

        void EnsureGroundPool()
        {
            if (ground != null) return;
            var mat = new Material(smokeSource) { name = "Ground Cloud (runtime)" };
            mat.SetTexture("_BaseColorMap", PuffTexture());
            var quad = PuffMesh();
            var root = new GameObject("Ground Cloud").transform;
            root.SetParent(transform, false);
            ground = new Puff[GroundPool];
            for (int i = 0; i < GroundPool; i++)
            {
                var go = new GameObject("Puff");
                go.transform.SetParent(root, false);
                go.AddComponent<MeshFilter>().sharedMesh = quad;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.enabled = false;
                ground[i] = new Puff { Tr = go.transform, R = r };
            }
        }

        float Rnd() => (float)rng.NextDouble();

        /// <summary>Рождение клубов под срезом (§9.5): частота, размер и дальность — от тяги, сила — от близости среза к грунту.</summary>
        void SpawnGround(Vessel v, double now, Vector3d srcBf, Vector3d upBf, double offUp)
        {
            bool air = v.Density >= DensityFull;
            double thrust = System.Math.Max(0, v.CurrentThrust);
            float k = air
                ? Mathf.Clamp((float)System.Math.Pow(thrust / RefThrust, 1.0 / 3), GroundScaleMin, GroundScaleMax)
                : Mathf.Clamp((float)System.Math.Pow(thrust / RefDustThrust, 1.0 / 3), DustScaleMin, DustScaleMax);
            double agl = System.Math.Max(0, v.TerrainAltitude + offUp);
            float height = air ? Mathf.Max(CloudHeightMin, CloudHeight * k) : DustHeight * k;
            if (thrust <= 0 || agl > height) return;
            float thinAir = Mathf.Clamp01((float)(v.Density / DensityFull));
            float life = air ? Mathf.Min(CloudLifeMax, CloudLife + CloudLifePerScale * k) : DustLife * (1 + DustLinger * thinAir);
            float rate = (air ? CloudRate : DustRate) * k;
            double interval = System.Math.Max(1.0 / rate, life * LifeJitterMax / GroundPool);
            if (now < lastGround) lastGround = double.NegativeInfinity; // загрузка сохранения отмотала время назад
            double since = now - lastGround;
            if (since < interval) return;
            // Залп при зажигании; дальше — по частоте (несколько за кадр, если кадр длиннее интервала: варп, низкий FPS).
            int n = since > BurstGap ? Mathf.Min(GroundBurstMax, Mathf.CeilToInt(BurstPuffs * k))
                                     : Mathf.Min(4, (int)(since / interval));
            lastGround = now;
            EnsureGroundPool();

            float near = 1 - (float)(agl / height);
            var groundBf = srcBf - upBf * agl;
            var e1 = Vector3d.AnyPerpendicular(upBf).normalized;
            var e2 = Vector3d.Cross(upBf, e1);
            if (float.IsNaN(windAngle)) windAngle = Rnd() * 2 * Mathf.PI;
            var wind = (e1 * System.Math.Cos(windAngle) + e2 * System.Math.Sin(windAngle)) * WindSpeed;
            Vector3d axis = default;
            bool twoWay = false;
            float trenchLen = 0, trenchShare = 0, soil = FieldSoil;
            if (air)
            {
                if (LaunchPadView.FlameTrench(body, groundBf, out axis, out twoWay, out trenchLen))
                    trenchShare = TrenchShare * Mathf.Clamp01(1 - (float)agl / (TrenchFade * height));
                if (LaunchPadView.OnPad(body, groundBf)) soil = PadSoil;
            }
            float g = (float)(body.Mu / (body.Radius * body.Radius));

            for (int i = 0; i < n; i++)
            {
                var p = ground[nextGround];
                // Слот ещё горит (залп поверх полного пула) — пропускаем, а не обрываем живой клуб.
                if (now - p.Born < p.Life) continue;
                nextGround = (nextGround + 1) % GroundPool;
                double a = Rnd() * 2 * System.Math.PI;
                var radial = e1 * System.Math.Cos(a) + e2 * System.Math.Sin(a);
                p.Born = now;
                p.Up = upBf;
                p.Life = life * (1 + LifeJitter * (2 * Rnd() - 1));
                p.Strength = Mathf.Sqrt(near);
                p.Dust = !air;
                p.Flip = Rnd() < 0.5f ? -1 : 1;
                if (air)
                {
                    float sk = Mathf.Sqrt(k);
                    p.Speed = CloudSpeed * sk * (0.6f + 0.6f * Rnd()) * (0.4f + 0.6f * near);
                    p.Ground = groundBf;
                    p.Dir = radial;
                    if (Rnd() < trenchShare)
                    {
                        // Газ выходит лотком: старт вдоль лотка (гуще у устья), курс по оси с разбросом, в лотке быстрее.
                        var ax = twoWay && Rnd() < 0.5f ? -axis : axis;
                        var lat = Vector3d.Cross(upBf, ax);
                        p.Dir = (ax + lat * (TrenchSpread * (2 * Rnd() - 1))).normalized;
                        p.Ground = groundBf + ax * (trenchLen * Mathf.Sqrt(Rnd()));
                        p.Speed *= TrenchBoost;
                    }
                    p.Tau = CloudDrag * sk;
                    p.Creep = CloudCreep * (0.5f + Rnd());
                    p.Rise = near * (0.6f + 0.8f * Rnd());
                    p.Size0 = CloudSize0 * k * (0.7f + 0.6f * Rnd());
                    p.Growth = CloudGrowth * k * (0.8f + 0.4f * Rnd());
                    p.Wind = wind;
                    p.Soil = soil * (0.5f + Rnd());
                    p.Roll = RollJitter * (2 * Rnd() - 1);
                    p.Spin = PuffSpin * Mathf.Rad2Deg * (2 * Rnd() - 1);
                    p.Vz = 0; p.Gravity = 0;
                }
                else
                {
                    float sk = Mathf.Sqrt(k);
                    p.Ground = groundBf;
                    p.Dir = radial;
                    p.Speed = DustSpeed * sk * (0.5f + 0.8f * Rnd()) * (0.3f + 0.7f * near);
                    p.Vz = p.Speed * (DustTiltMin + (DustTiltMax - DustTiltMin) * Rnd());
                    p.Gravity = g;
                    p.Tau = v.Density > 1e-6 ? DustAirDrag / (float)v.Density : 0;
                    p.Size0 = DustSize * sk;
                    p.Growth = DustGrowth * sk * (1 + DustAirGrowth * thinAir);
                    p.Wind = Vector3d.zero;
                    p.Rise = 0; p.Creep = 0; p.Soil = 0; p.Roll = 0; p.Spin = 0;
                }
                groundUntil = System.Math.Max(groundUntil, now + p.Life);
            }
        }

        /// <summary>Свет факела борта для подсветки облака: сумма сил света «Plume Light» (кд), центр и цвет — по весу силы.</summary>
        void GatherFlameLight()
        {
            if (Time.unscaledTime - lightsAt > LightRescan || Time.unscaledTime < lightsAt)
            {
                lightsAt = Time.unscaledTime;
                plumeLights.Clear();
                foreach (var l in view.GetComponentsInChildren<Light>(true))
                    if (l.gameObject.name == "Plume Light") plumeLights.Add(l);
            }
            glowI = 0;
            var c = Vector3.zero;
            var col = Color.black;
            foreach (var l in plumeLights)
            {
                if (l == null || !l.isActiveAndEnabled || l.intensity <= 0) continue;
                float i = l.intensity;
                glowI += i;
                c += l.transform.position * i;
                col += l.color.linear * i;
            }
            if (glowI > 0) { glowPos = c / glowI; glowColor = col / glowI; }
        }

        void UpdateGround(bool show, double now, QuaternionD q)
        {
            if (ground == null) return;
            bool any = show && now <= groundUntil;
            if (!any)
            {
                if (groundShown) foreach (var p in ground) p.R.enabled = false;
                groundShown = false;
                return;
            }
            groundShown = true;
            var cam = Camera.main;
            var camTr = cam != null ? cam.transform : null;
            var f = camTr != null ? camTr.forward : Vector3.forward;
            GatherFlameLight();
            foreach (var p in ground)
            {
                float age = (float)(now - p.Born);
                bool on = age >= 0 && age < p.Life;
                p.R.enabled = on;
                if (!on) continue;
                float life = age / p.Life, size, alpha, flat;
                Vector3d bf;
                if (!p.Dust)
                {
                    size = p.Size0 + p.Growth * Mathf.Sqrt(age);
                    double run = p.Speed * p.Tau * (1 - System.Math.Exp(-age / p.Tau)) + p.Creep * age;
                    double rise = p.Rise * (RiseHot * RiseHotTime * (1 - System.Math.Exp(-age / RiseHotTime)) + RiseCold * age);
                    flat = Mathf.Lerp(GroundFlat, 1, Mathf.Clamp01(age / FlatTime));
                    // Низ клуба не режет грунт прямой линией: центр не ниже PuffLift·высоты клуба.
                    bf = p.Ground + p.Dir * run + p.Wind * age + p.Up * (rise + size * flat * PuffLift);
                    alpha = CloudAlpha * p.Strength * Mathf.Clamp01(age * 2) * Mathf.Pow(1 - life, 1.5f);
                }
                else
                {
                    size = p.Size0 + p.Growth * age;
                    // Без воздуха — по прямой; в разреженном — скорость гаснет за Tau (и по горизонтали, и вверх).
                    double path = p.Tau > 0 ? p.Tau * (1 - System.Math.Exp(-age / p.Tau)) : age;
                    double h = p.Vz * path - 0.5 * p.Gravity * age * age;
                    // Упавшая пыль ложится и гаснет за высоту клуба, а не уходит под грунт.
                    float landed = h < 0 ? Mathf.Clamp01(1 + (float)h / size) : 1;
                    flat = DustFlat;
                    bf = p.Ground + p.Dir * (p.Speed * path) + p.Up * (System.Math.Max(0, h) + size * DustFlat * PuffLift);
                    alpha = DustAlpha * p.Strength * Mathf.Clamp01(age * 10) * (1 - life) * (1 - life) * landed;
                }
                var pos = FloatingOrigin.ToUnity(body.Position + q * bf);
                var upU = FloatingOrigin.DirToUnity(q * p.Up);
                // Ориентация: лицом к камере, локальная Y — вдоль вертикали (облако) или курса (пыль) на экране,
                // чтобы сплющивать вал и вытягивать струи пыли в нужную сторону.
                var axisU = p.Dust ? FloatingOrigin.DirToUnity(q * p.Dir) : upU;
                var scr = axisU - f * Vector3.Dot(axisU, f);
                float sl = scr.magnitude;
                float tilt = Mathf.Abs(Vector3.Dot(f, upU));   // 1 — смотрим отвесно, сплющивать нечего
                var rot = sl > 1e-3f ? Quaternion.LookRotation(f, scr / sl) : (camTr != null ? camTr.rotation : Quaternion.identity);
                rot *= Quaternion.Euler(0, 0, p.Roll + p.Spin * age);
                float sx, sy;
                if (p.Dust) { sx = size * Mathf.Lerp(DustFlat, 1, tilt); sy = size * Mathf.Lerp(1, DustStretch, Mathf.Clamp01(sl)); }
                else { sx = size; sy = size * Mathf.Lerp(flat, 1, tilt); }
                p.Tr.SetPositionAndRotation(pos, rot);
                // Flip — зеркало узора текстуры: клубы одной текстуры не повторяются (материал двусторонний).
                p.Tr.localScale = new Vector3(sx * p.Flip, sy, size);
                if (camTr != null)
                {
                    float d = Vector3.Distance(camTr.position, pos) / size;
                    alpha *= Mathf.SmoothStep(0, 1, (d - PuffFadeNear) / PuffFadeRange);
                }
                Color tint;
                if (p.Dust) tint = dustColor;
                else tint = Color.Lerp(Color.Lerp(YoungSmoke, SmokeColor, Mathf.Clamp01(age / SmokeAgeing)), soilColor, p.Soil);
                mpb.Clear();
                mpb.SetColor("_BaseColor", new Color(tint.r, tint.g, tint.b, alpha));
                if (glowI > 0)
                {
                    float d = Mathf.Max(Vector3.Distance(pos, glowPos), Mathf.Max(GlowMinDist, size * GlowNear));
                    float nits = FlameScatter * glowI / (d * d) * SmokeAlbedo / Mathf.PI;
                    // Нит — через SetVector: SetColor переводит sRGB → linear и исказил бы величину (pitfalls-render, аврора).
                    mpb.SetVector("_EmissiveColor", new Vector4(glowColor.r * nits, glowColor.g * nits, glowColor.b * nits, 1));
                }
                p.R.SetPropertyBlock(mpb);
            }
        }
    }
}
