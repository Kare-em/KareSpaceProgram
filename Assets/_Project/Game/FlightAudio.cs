using System.Collections.Generic;
using Kare.Space.Core;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Звук полёта (GDD §9.3): всё синтезируется кодом при старте сцены — сэмплов в проекте нет. Звуки не привязаны
    /// к скриптам миссий: каждый кадр снимается состояние бортов и разница с прошлым кадром превращается в события
    /// (отделение, зажигание, парашют, касание, гибель), а непрерывные параметры — в громкость/тон/фильтр петель.
    /// Среда решает, что слышно: в воздухе — по расстоянию до камеры с запаздыванием d/343 и завалом верхов,
    /// в пустоте чужие борта не слышны вовсе, а свой — только «через конструкцию» (глухо, низы).
    /// </summary>
    public class FlightAudio : MonoBehaviour
    {
        /// <summary>Доля атмосферного давления, ниже которой среда — пустота (≈ 50 км над Землёй, 80 Па).</summary>
        const float VacuumAir = 8e-4f;
        /// <summary>Звук своего борта «через конструкцию» в пустоте: громкость и срез ФНЧ, Гц.</summary>
        const float StructureGain = 0.35f, StructureCut = 320;
        /// <summary>Тяга, которой соответствует тишина/полная громкость рёва: 10^3 Н … 10^7,5 Н (≈ 30 МН, «Сатурн-5»).
        /// Пара: EngineMinLoud — порог «маленький, но слышный», иначе РСУ-движок в 400 Н звучал бы немо.</summary>
        const float ThrustLogMin = 3, ThrustLogSpan = 4.5f, EngineMinLoud = 0.18f;
        /// <summary>Расстояние, на котором звук слабеет вдвое, м. Рёв и взрыв дальнобойнее хлопков.</summary>
        const float EngineRef = 120, BangRef = 150, BoomRef = 900;
        /// <summary>Скорость звука для запаздывания и предел запаздывания, с.</summary>
        const float SoundSpeed = 343, MaxDelay = 20;
        /// <summary>Высота над рельефом, ниже которой рёв отражается от земли и прибавляет низов, м.</summary>
        const float GroundEcho = 300;
        /// <summary>Напор, на котором ветер в полную силу, Па (≈ max Q у «семёрки»).</summary>
        const float WindFullQ = 35000;
        /// <summary>Тепловой поток начала и полного гула плазмы, Вт/м²: 10 кВт/м² … 2 МВт/м² (лог-шкала).</summary>
        const float PlasmaLogMin = 4, PlasmaLogSpan = 2.3f;
        /// <summary>Ускорение времени, выше которого петли глушатся, а одиночные звуки пропускаются.</summary>
        const float QuietWarp = 4.1f;

        class Loop
        {
            public AudioSource Src;
            public AudioLowPassFilter Lp;
            public float Attack = 0.15f, Release = 0.6f;
            public float Vol, Pitch = 1, Cut = 22000;
            float vol, pitch = 1, cut = 22000;

            public void Tick(float dt, float master)
            {
                vol += (Vol - vol) * (1 - Mathf.Exp(-dt / (Vol > vol ? Attack : Release)));
                float k = 1 - Mathf.Exp(-dt / 0.12f);
                pitch += (Pitch - pitch) * k;
                cut += (Cut - cut) * k;
                Src.volume = vol * master;
                Src.pitch = Mathf.Clamp(pitch, 0.1f, 3);
                Lp.cutoffFrequency = Mathf.Clamp(cut, 60, 22000);
            }
        }

        /// <summary>Снимок борта за прошлый кадр — по разнице с ним рождаются одиночные звуки.</summary>
        class Track
        {
            public bool[] Attached, Running, Chute, ChuteFailed;
            public double[] Deployed;
            public Situation Sit;
            public double Speed, Drive, DriveRate;
            public bool Moving;
        }

        AudioClip roar, rumble, crackle, wind, hiss, servo, rover, alarm, flap;
        AudioClip bang, boom, clunk, click, thud, splash, pop, ignite, sputter, cutoff, rip;
        Loop lRoar, lRumble, lCrackle, lWind, lPlasma, lRcs, lServo, lRover, lAlarm, lFlap;
        Loop[] loops;
        readonly List<AudioSource> pool = new List<AudioSource>();
        readonly List<AudioLowPassFilter> poolLp = new List<AudioLowPassFilter>();
        int poolNext;
        readonly Dictionary<Vessel, Track> tracks = new Dictionary<Vessel, Track>();
        readonly HashSet<Vessel> seen = new HashSet<Vessel>();
        readonly List<Vessel> gone = new List<Vessel>();
        double lastTime = double.NaN;
        float master = 1;
        bool quiet;
        Vessel active;
        Transform cam;

        void Awake()
        {
            Synth.Rate = AudioSettings.outputSampleRate;
            roar = Synth.Clip("roar", Synth.Roar(), true); rumble = Synth.Clip("rumble", Synth.Rumble(), true);
            crackle = Synth.Clip("crackle", Synth.Crackle(), true); wind = Synth.Clip("wind", Synth.Wind(), true);
            hiss = Synth.Clip("hiss", Synth.Hiss(), true); servo = Synth.Clip("servo", Synth.Servo(), true);
            rover = Synth.Clip("rover", Synth.Rover(), true); alarm = Synth.Clip("alarm", Synth.Alarm(), true);
            flap = Synth.Clip("flap", Synth.Flap(), true);
            bang = Synth.Clip("bang", Synth.Bang()); boom = Synth.Clip("boom", Synth.Boom());
            clunk = Synth.Clip("clunk", Synth.Clunk()); click = Synth.Clip("click", Synth.Click());
            thud = Synth.Clip("thud", Synth.Thud()); splash = Synth.Clip("splash", Synth.Splash());
            pop = Synth.Clip("pop", Synth.Pop()); ignite = Synth.Clip("ignite", Synth.Ignite());
            sputter = Synth.Clip("sputter", Synth.Sputter()); cutoff = Synth.Clip("cutoff", Synth.Cutoff());
            rip = Synth.Clip("rip", Synth.Rip());

            lRoar = MakeLoop("Roar", roar); lRumble = MakeLoop("Rumble", rumble); lCrackle = MakeLoop("Crackle", crackle);
            lWind = MakeLoop("Wind", wind, 0.4f, 0.8f); lPlasma = MakeLoop("Plasma", roar, 0.5f, 1.0f);
            lRcs = MakeLoop("RCS", hiss, 0.03f, 0.08f); lServo = MakeLoop("Servo", servo, 0.05f, 0.15f);
            lRover = MakeLoop("Rover", rover, 0.2f, 0.3f); lAlarm = MakeLoop("Alarm", alarm, 0.01f, 0.05f);
            lFlap = MakeLoop("Flap", flap, 0.3f, 0.5f);
            loops = new[] { lRoar, lRumble, lCrackle, lWind, lPlasma, lRcs, lServo, lRover, lAlarm, lFlap };
            for (int i = 0; i < 14; i++)
            {
                var go = new GameObject("Shot " + i);
                go.transform.SetParent(transform, false);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false; s.spatialBlend = 0;
                pool.Add(s);
                poolLp.Add(go.AddComponent<AudioLowPassFilter>());
            }
        }

        void Start()
        {
            // Звуки 2D (spatialBlend = 0): расстояние и панораму считаем сами — Unity не знает про пустоту.
            if (FindAnyObjectByType<AudioListener>() == null)
            {
                var c = GameBootstrap.Instance != null && GameBootstrap.Instance.Camera != null ? GameBootstrap.Instance.Camera : Camera.main;
                if (c != null) c.gameObject.AddComponent<AudioListener>();
            }
        }

        void OnDestroy()
        {
            foreach (var v in tracks.Keys) v.Event -= OnEvent;
            AudioListener.pause = false;
        }

        Loop MakeLoop(string name, AudioClip clip, float attack = 0.15f, float release = 0.6f)
        {
            var go = new GameObject("Loop " + name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.clip = clip; s.loop = true; s.playOnAwake = false; s.spatialBlend = 0; s.volume = 0;
            var lp = go.AddComponent<AudioLowPassFilter>();
            s.Play();
            // Петли на одном клипе (рёв и плазма) разводим по фазе, иначе они звучат одним источником.
            s.timeSamples = Random.Range(0, clip.samples);
            return new Loop { Src = s, Lp = lp, Attack = attack, Release = release };
        }

        void LateUpdate()
        {
            var u = GameBootstrap.U;
            float dt = Time.unscaledDeltaTime;
            AudioListener.pause = PauseMenu.IsOpen;
            AudioListener.volume = SoundSettings.Volume;
            if (u == null) return;
            var c = GameBootstrap.Instance.Camera != null ? GameBootstrap.Instance.Camera : Camera.main;
            cam = c != null ? c.transform : null;
            active = u.Active;
            quiet = u.RailsActive || u.EffectiveWarp > QuietWarp;
            // Карта — взгляд со стороны: звук борта приглушён, но не пропадает, чтобы было слышно работу двигателя.
            master = (MapView.IsOpen ? 0.5f : 1) * (u.EffectiveWarp > 1.5 && !quiet ? 0.7f : 1);
            double dtSim = double.IsNaN(lastTime) ? 0 : u.Time - lastTime;
            lastTime = u.Time;

            seen.Clear();
            foreach (var v in u.Vessels)
            {
                seen.Add(v);
                if (!tracks.TryGetValue(v, out var tr))
                {
                    tracks[v] = tr = new Track();
                    Snapshot(v, tr, dtSim);
                    v.Event += OnEvent;
                    continue;
                }
                Diff(v, tr);
                Snapshot(v, tr, dtSim);
            }
            gone.Clear();
            foreach (var v in tracks.Keys) if (!seen.Contains(v)) gone.Add(v);
            foreach (var v in gone) { v.Event -= OnEvent; tracks.Remove(v); }

            Continuous(active, active != null && tracks.TryGetValue(active, out var at) ? at : null);
            foreach (var l in loops)
            {
                if (quiet || active == null || !active.Alive) l.Vol = 0;
                l.Tick(dt, master);
            }
        }

        static float Air(Vessel v) => v == null ? 0 : Mathf.Clamp01((float)(v.StaticPressure / 101325));

        float CamDist(Vessel v, out Vector3 p)
        {
            p = FloatingOrigin.ToUnity(FloatingOrigin.WorldP(v));
            return cam != null ? Vector3.Distance(cam.position, p) : 0;
        }

        /// <summary>Как слышен одиночный звук борта v: громкость, срез, запаздывание, панорама; false — не слышен.</summary>
        bool Hear(Vessel v, float refDist, out float gain, out float cut, out float delay, out float pan)
        {
            gain = 0; cut = 22000; delay = 0; pan = 0;
            float air = Air(active ?? v);
            float d = CamDist(v, out var p);
            if (air < VacuumAir)
            {
                // Пустота: только то, что случилось с самим бортом, и только через его конструкцию.
                if (v != active) return false;
                gain = StructureGain; cut = StructureCut;
                return true;
            }
            float a = Mathf.Sqrt(air);
            gain = (v == active ? Mathf.Lerp(StructureGain, 1, a) : a) * Mathf.Clamp01(1.5f * refDist / (refDist + d));
            cut = Mathf.Max(250, Mathf.Lerp(StructureCut * 4, 22000, a) / (1 + d / 600));
            if (v != active) delay = Mathf.Min(MaxDelay, d / SoundSpeed);
            if (cam != null && d > 1) pan = Vector3.Dot((p - cam.position) / d, cam.right) * 0.7f;
            return gain > 0.01f;
        }

        void Shot(Vessel v, AudioClip clip, float vol, float pitch, float refDist, float extraDelay = 0, bool always = false)
        {
            if (quiet && !always) return;
            if (!Hear(v, refDist, out float g, out float cut, out float delay, out float pan)) return;
            AudioSource s = null;
            for (int i = 0; i < pool.Count && s == null; i++)
            {
                int k = (poolNext + i) % pool.Count;
                if (!pool[k].isPlaying) s = pool[k];
            }
            if (s == null) s = pool[poolNext];
            int idx = pool.IndexOf(s);
            poolNext = (idx + 1) % pool.Count;
            poolLp[idx].cutoffFrequency = cut;
            s.Stop();
            s.clip = clip;
            s.volume = Mathf.Clamp01(vol * g * master);
            s.pitch = pitch * Random.Range(0.94f, 1.06f);
            s.panStereo = pan;
            s.PlayDelayed(delay + extraDelay);
        }

        void OnEvent(Vessel v, string m)
        {
            // Отрыв от стола: удар отведённых ферм и замков; сорванный запуск — «чих» непрогоревшего топлива.
            if (m == "Есть отрыв!") { Shot(v, clunk, 0.9f, 0.55f, BangRef); Shot(v, clunk, 0.6f, 0.7f, BangRef, 0.35f); }
            else if (m.Contains("запуск сорван")) Shot(v, sputter, 0.8f, 1, BangRef);
        }

        void Diff(Vessel v, Track tr)
        {
            int n = v.Attached.Length;
            if (tr.Attached.Length != n)
            {
                // Число отсеков выросло — стыковка (удар и щелчки защёлок), упало — расстыковка (пиротолкатели).
                if (n > tr.Attached.Length)
                {
                    Shot(v, clunk, 0.9f, 0.9f, BangRef);
                    Shot(v, click, 0.6f, 1, BangRef, 0.25f);
                    Shot(v, click, 0.6f, 1.1f, BangRef, 0.45f);
                }
                else { Shot(v, bang, 0.4f, 1.4f, BangRef); Shot(v, clunk, 0.5f, 1.2f, BangRef, 0.1f); }
                return;
            }
            int detached = 0;
            for (int i = 0; i < n; i++)
            {
                if (tr.Attached[i] && !v.Attached[i]) detached++;
                var s = v.Design.Sections[i];
                if (v.Attached[i] && s.HasEngine)
                {
                    float size = Mathf.Clamp01((Mathf.Log10((float)(s.Engine.ThrustVac * s.EngineCount) + 1) - ThrustLogMin) / ThrustLogSpan);
                    if (!tr.Running[i] && v.Running[i]) Shot(v, ignite, Mathf.Lerp(0.35f, 1, size), Mathf.Lerp(1.3f, 0.75f, size), EngineRef);
                    else if (tr.Running[i] && !v.Running[i] && v.Alive) Shot(v, cutoff, Mathf.Lerp(0.3f, 0.8f, size), Mathf.Lerp(1.3f, 0.8f, size), EngineRef);
                }
                if (!tr.Chute[i] && v.ChuteDeployed[i]) Shot(v, pop, 0.9f, 1, BangRef);
                if (!tr.ChuteFailed[i] && v.ChuteFailed[i]) Shot(v, rip, 0.9f, 1, BangRef);
            }
            // Пакет боковых или ступень уходят одним залпом пиросредств: один хлопок, громче при многих отсеках.
            if (detached > 0) Shot(v, bang, Mathf.Min(1, 0.55f + 0.15f * detached), 1, BangRef);

            bool moving = false;
            for (int i = 0; i < v.Deployed.Length && i < tr.Deployed.Length; i++)
                if (System.Math.Abs(v.Deployed[i] - tr.Deployed[i]) > 1e-6) moving = true;
            if (tr.Moving && !moving) Shot(v, clunk, 0.5f, 1.4f, BangRef);
            tr.Moving = moving;

            // Касание: сила по скорости прошлого кадра (в этом она уже обнулена посадкой).
            if (tr.Sit == Situation.Flying && v.IsLanded && tr.Speed > 0.3)
            {
                float hit = Mathf.Clamp01((float)tr.Speed / 6) * 0.85f + 0.15f;
                if (v.Situation == Situation.Splashed) Shot(v, splash, hit, 1, BangRef);
                else Shot(v, thud, hit, Mathf.Lerp(1.2f, 0.85f, hit), BangRef);
            }
            if (tr.Sit != Situation.Destroyed && !v.Alive)
                Shot(v, boom, 1, Random.Range(0.85f, 1.05f), BoomRef, 0, v == active);
        }

        static void Copy(ref bool[] dst, bool[] src)
        {
            if (dst == null || dst.Length != src.Length) dst = new bool[src.Length];
            System.Array.Copy(src, dst, src.Length);
        }

        void Snapshot(Vessel v, Track tr, double dtSim)
        {
            Copy(ref tr.Attached, v.Attached);
            Copy(ref tr.Running, v.Running);
            Copy(ref tr.Chute, v.ChuteDeployed);
            Copy(ref tr.ChuteFailed, v.ChuteFailed);
            if (tr.Deployed == null || tr.Deployed.Length != v.Deployed.Length) tr.Deployed = new double[v.Deployed.Length];
            System.Array.Copy(v.Deployed, tr.Deployed, v.Deployed.Length);
            tr.Sit = v.Situation;
            tr.Speed = v.SurfaceSpeed;
            if (dtSim > 0) tr.DriveRate = (v.DriveDistance - tr.Drive) / dtSim;
            tr.Drive = v.DriveDistance;
        }

        void Continuous(Vessel a, Track tr)
        {
            if (a == null) return;
            float air = Air(a), sqAir = Mathf.Sqrt(air);
            bool inAir = air >= VacuumAir;
            float d = CamDist(a, out _);
            float t = Time.time;

            // Рёв: громкость — лог тяги, тон ниже у больших движков. Выше М 1 борт обгоняет собственный шум струи.
            float thrust = (float)a.CurrentThrust;
            float size = thrust > 1 ? Mathf.Clamp01((Mathf.Log10(thrust) - ThrustLogMin) / ThrustLogSpan) : 0;
            float loud = thrust > 1 ? Mathf.Lerp(EngineMinLoud, 1, size) : 0;
            float mach = (float)a.Mach;
            float outrun = mach <= 1 ? 1 : Mathf.Lerp(1, 0.35f, Mathf.Clamp01((mach - 1) / 1.5f));
            float distGain = Mathf.Clamp01(1.5f * EngineRef / (EngineRef + d));
            float airborne = inAir ? sqAir * outrun * distGain : 0;
            float distCut = 1 / (1 + d / 600);
            bool solid = false;
            for (int i = 0; i < a.Attached.Length; i++)
                if (a.Attached[i] && a.Running[i] && a.Design.Sections[i].HasEngine && a.Design.Sections[i].Engine.Solid) solid = true;
            float ground = inAir && a.TerrainAltitude < GroundEcho ? (1 - (float)a.TerrainAltitude / GroundEcho) * 0.5f * sqAir : 0;
            // Запас по сумме: на старте «семёрки» (4 МН, 175 м) рёв 0,92 + низы 1,0 упирались в потолок — убавлено.
            float tone = Mathf.Lerp(1.3f, 0.75f, size) * (0.9f + 0.1f * Mathf.Clamp01((float)a.Throttle));
            lRoar.Vol = loud * (StructureGain * 0.6f + 0.75f * airborne);
            lRoar.Pitch = tone;
            lRoar.Cut = Mathf.Lerp(StructureCut, 9000 * distCut, airborne);
            lRumble.Vol = loud * (0.35f + 0.3f * airborne + ground);
            lRumble.Pitch = Mathf.Lerp(1.2f, 0.8f, size);
            lRumble.Cut = 140 + 120 * airborne;
            lCrackle.Vol = loud * airborne * (solid ? 0.9f : 0.3f);
            lCrackle.Pitch = Mathf.Lerp(1.2f, 0.8f, size);
            lCrackle.Cut = 12000 * distCut;

            // Ветер: лог напора; тряска в трансзвуке (М 0,85…1,25) — модуляция шумом Перлина.
            bool flying = a.Situation == Situation.Flying;
            float q = (float)a.DynamicPressure;
            float w = inAir && flying ? Mathf.Clamp01(Mathf.Log10(1 + q / 20) / Mathf.Log10(1 + WindFullQ / 20)) : 0;
            float buffet = Mathf.Clamp01(1 - Mathf.Abs(mach - 1.05f) / 0.2f);
            lWind.Vol = 0.7f * w * (1 + 1.2f * buffet * (Mathf.PerlinNoise(t * 9, 0.3f) - 0.5f));
            lWind.Cut = Mathf.Clamp(300 + (float)a.SurfaceSpeed * 6, 300, 9000);
            lWind.Pitch = 0.8f + 0.2f * Mathf.Min(mach, 2);

            // Плазма входа: гул по лог теплового потока, с мерцанием.
            float hf = (float)a.HeatFlux;
            float pl = hf > 1 ? Mathf.Clamp01((Mathf.Log10(hf) - PlasmaLogMin) / PlasmaLogSpan) : 0;
            lPlasma.Vol = 0.8f * pl * (0.85f + 0.3f * Mathf.PerlinNoise(t * 6, 2.7f));
            lPlasma.Cut = 600 + 2500 * pl;
            lPlasma.Pitch = 0.5f + 0.2f * pl;

            // РСУ: шипение по той же доле команды, что зажигает струи (RcsJets.Command): момент сверх качания сопел
            // и рулей плюс поступательная тяга. Раньше считалась доля полного момента — шипело на взлёте при качании.
            float cmd = RcsJets.Level(a);
            lRcs.Vol = 0.45f * Mathf.Clamp01(cmd) * (inAir ? 1 : 0.7f);
            lRcs.Cut = inAir ? 9000 : 2500;

            // Приводы раскладного, ходовая лунохода, сирена перегрузки/перегрева, хлопанье купола.
            float structCut = inAir ? 8000 : 1500;
            lServo.Vol = tr != null && tr.Moving ? 0.35f : 0;
            lServo.Cut = structCut;
            float drive = tr != null && a.IsLanded ? (float)System.Math.Abs(tr.DriveRate) : 0;
            lRover.Vol = 0.35f * Mathf.Clamp01(drive / 1.5f);
            lRover.Pitch = 0.6f + 0.4f * Mathf.Clamp01(drive / 2);
            lRover.Cut = inAir ? 6000 : 1200;
            lAlarm.Vol = a.HighGTimer > 0 || a.OverheatTimer > 0 ? 0.22f : 0;
            bool chute = false;
            for (int i = 0; i < a.Attached.Length; i++) if (a.Attached[i] && a.ChuteDeployed[i] && !a.ChuteFailed[i]) chute = true;
            lFlap.Vol = chute && flying && inAir ? 0.12f + 0.35f * Mathf.Clamp01(Mathf.Log10(1 + q) / 3.5f) : 0;
            lFlap.Pitch = 0.8f + 0.4f * Mathf.Clamp01((float)a.SurfaceSpeed / 100);
        }

        /// <summary>Синтез клипов. Шумы — фильтрованный белый; петли сшиваются равномощным наложением хвоста на
        /// начало, поэтому повторяются без щелчка. Сид фиксирован — звук одинаков от запуска к запуску.</summary>
        static class Synth
        {
            public static int Rate = 48000;
            const float Tau = 2 * Mathf.PI, LoopFade = 0.25f;
            static readonly System.Random rng = new System.Random(7);

            static float W() => (float)(rng.NextDouble() * 2 - 1);
            static float K(float fc) => 1 - Mathf.Exp(-Tau * fc / Rate);
            static int N(float s) => Mathf.CeilToInt(s * Rate);
            static float[] Buf(float s) => new float[N(s + LoopFade)];
            static float[] Shot(float s) => new float[N(s)];

            /// <summary>Одиночный клип гасится последние 20 мс: огибающие не доходят до нуля, и обрыв щёлкал бы
            /// (замер: cutoff кончался на 0,35 от пика). Петлям хвост не нужен — шов сшит наложением.</summary>
            public static AudioClip Clip(string name, float[] d, bool loop = false)
            {
                int f = loop ? 0 : Mathf.Min(d.Length, N(0.02f));
                for (int i = 0; i < f; i++) d[d.Length - 1 - i] *= (float)i / f;
                var c = AudioClip.Create(name, d.Length, 1, Rate, false);
                c.SetData(d, 0);
                return c;
            }

            static float[] Norm(float[] d, float peak)
            {
                float m = 1e-6f;
                foreach (var x in d) m = Mathf.Max(m, Mathf.Abs(x));
                for (int i = 0; i < d.Length; i++) d[i] *= peak / m;
                return d;
            }

            static float[] Loop(float[] b, float s, float peak = 0.9f)
            {
                int n = N(s), f = b.Length - n;
                var o = new float[n];
                for (int i = 0; i < n; i++)
                {
                    if (i >= f) { o[i] = b[i]; continue; }
                    float k = (float)i / f;
                    o[i] = b[i] * Mathf.Sqrt(k) + b[n + i] * Mathf.Sqrt(1 - k);
                }
                return Norm(o, peak);
            }

            static float Partials(float t, float[] fda)
            {
                float s = 0;
                for (int j = 0; j + 2 < fda.Length; j += 3) s += Mathf.Sin(Tau * fda[j] * t) * fda[j + 2] * Mathf.Exp(-t / fda[j + 1]);
                return s;
            }

            public static float[] Roar()
            {
                var b = Buf(3); float l1 = 0, l2 = 0, am = 0, k1 = K(500), k2 = K(140), ka = K(3);
                for (int i = 0; i < b.Length; i++)
                {
                    float w = W();
                    l1 += (w - l1) * k1; l2 += (w - l2) * k2; am += (W() - am) * ka;
                    b[i] = (l2 * 6 + l1 * 2 + w * 0.1f) * (1 + am * 25);
                }
                return Loop(b, 3);
            }

            public static float[] Rumble()
            {
                var b = Buf(3); float br = 0, l = 0, k = K(70);
                for (int i = 0; i < b.Length; i++) { br = br * 0.995f + W() * 0.1f; l += (br - l) * k; b[i] = l; }
                return Loop(b, 3);
            }

            public static float[] Crackle()
            {
                var b = Buf(2); float e = 0, s = 1, dec = Mathf.Exp(-1f / (0.0015f * Rate)), p = 300f / Rate;
                for (int i = 0; i < b.Length; i++)
                {
                    if (rng.NextDouble() < p) { e = 0.3f + 0.7f * (float)rng.NextDouble(); s = W() > 0 ? 1 : -1; }
                    e *= dec;
                    b[i] = e * s * (0.6f + 0.4f * W());
                }
                return Loop(b, 2, 0.8f);
            }

            public static float[] Wind()
            {
                var b = Buf(4); float b0 = 0, b1 = 0, b2 = 0, g = 0, kg = K(0.7f);
                for (int i = 0; i < b.Length; i++)
                {
                    float w = W();
                    b0 = 0.99765f * b0 + w * 0.0990460f; b1 = 0.96300f * b1 + w * 0.2965164f; b2 = 0.57000f * b2 + w * 1.0526913f;
                    g += (W() - g) * kg;
                    b[i] = (b0 + b1 + b2 + w * 0.1848f) * Mathf.Max(0.2f, 1 + g * 30);
                }
                return Loop(b, 4);
            }

            public static float[] Hiss()
            {
                var b = Buf(2); float l = 0, k = K(1000);
                for (int i = 0; i < b.Length; i++) { float w = W(); l += (w - l) * k; b[i] = w - l; }
                return Loop(b, 2, 0.6f);
            }

            public static float[] Servo()
            {
                var b = Buf(1); float ph = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate;
                    ph += Tau * 220 * (1 + 0.01f * Mathf.Sin(Tau * 3 * t)) / Rate;
                    float tick = Mathf.Repeat(t * 24, 1) < 0.08f ? W() * 0.3f : 0;
                    b[i] = Mathf.Sin(ph) * 0.5f + Mathf.Sin(2 * ph) * 0.3f + Mathf.Sin(3 * ph) * 0.15f + W() * 0.04f + tick;
                }
                return Loop(b, 1, 0.7f);
            }

            public static float[] Rover()
            {
                var b = Buf(1); float ph = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate;
                    ph += Tau * 110 / Rate;
                    float s = 0;
                    for (int h = 1; h <= 8; h++) s += Mathf.Sin(h * ph) / h;
                    float tick = Mathf.Repeat(t * 16, 1) < 0.05f ? W() * 0.5f : 0;
                    b[i] = s * 0.4f + tick + W() * 0.05f;
                }
                return Loop(b, 1, 0.7f);
            }

            public static float[] Alarm()
            {
                var b = Shot(1);
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate, f = 0, t0 = 0;
                    if (t < 0.16f) f = 1050;
                    else if (t >= 0.25f && t < 0.41f) { f = 820; t0 = 0.25f; }
                    if (f == 0) continue;
                    float u = t - t0, env = Mathf.Clamp01(u / 0.005f) * Mathf.Clamp01((0.16f - u) / 0.005f);
                    b[i] = (Mathf.Sin(Tau * f * t) + 0.3f * Mathf.Sin(Tau * 3 * f * t)) * env;
                }
                return Norm(b, 0.7f);
            }

            public static float[] Flap()
            {
                var b = Buf(1); float l = 0, k = K(900);
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate; l += (W() - l) * k;
                    b[i] = l * (0.35f + 0.65f * Mathf.Pow(0.5f + 0.5f * Mathf.Sin(Tau * 13 * t), 3));
                }
                return Loop(b, 1, 0.8f);
            }

            public static float[] Bang()
            {
                var b = Shot(1.2f); float l = 0, k = K(900), ph = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate, w = W(); l += (w - l) * k;
                    ph += Tau * (45 + 60 * Mathf.Exp(-t / 0.05f)) / Rate;
                    b[i] = w * Mathf.Exp(-t / 0.012f) * 0.6f + Mathf.Sin(ph) * Mathf.Exp(-t / 0.22f) + l * Mathf.Exp(-t / 0.35f) * 3;
                }
                return Norm(b, 0.95f);
            }

            public static float[] Boom()
            {
                var b = Shot(4.5f); float br = 0, l = 0, k = K(250), ph = 0, e = 0, s = 1, dec = Mathf.Exp(-1f / (0.002f * Rate));
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate, w = W();
                    br = br * 0.995f + w * 0.1f; l += (br - l) * k;
                    ph += Tau * (30 + 40 * Mathf.Exp(-t / 0.3f)) / Rate;
                    if (rng.NextDouble() < 200 * Mathf.Exp(-t / 1.5f) / Rate) { e = (float)rng.NextDouble(); s = W() > 0 ? 1 : -1; }
                    e *= dec;
                    b[i] = w * Mathf.Exp(-t / 0.03f) * 0.5f + l * (1 - Mathf.Exp(-t / 0.015f)) * Mathf.Exp(-t / 1.1f) * 8
                        + Mathf.Sin(ph) * Mathf.Exp(-t / 0.9f) * 0.8f + e * s * 0.3f;
                }
                return Norm(b, 0.95f);
            }

            public static float[] Clunk()
            {
                var b = Shot(0.6f); var p = new[] { 170f, 0.14f, 1, 415, 0.09f, 0.6f, 860, 0.05f, 0.4f, 1310, 0.03f, 0.25f };
                for (int i = 0; i < b.Length; i++) { float t = (float)i / Rate; b[i] = Partials(t, p) + W() * Mathf.Exp(-t / 0.003f) * 0.5f; }
                return Norm(b, 0.9f);
            }

            public static float[] Click()
            {
                var b = Shot(0.08f); var p = new[] { 2300f, 0.012f, 1, 3700, 0.008f, 0.6f };
                for (int i = 0; i < b.Length; i++) { float t = (float)i / Rate; b[i] = Partials(t, p) + W() * Mathf.Exp(-t / 0.001f) * 0.4f; }
                return Norm(b, 0.8f);
            }

            public static float[] Thud()
            {
                var b = Shot(1); float l = 0, k = K(900), ph = 0, e = 0, dec = Mathf.Exp(-1f / (0.001f * Rate));
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate, w = W(); l += (w - l) * k;
                    ph += Tau * (40 + 30 * Mathf.Exp(-t / 0.06f)) / Rate;
                    if (rng.NextDouble() < 2000 * Mathf.Exp(-t / 0.08f) / Rate) e = (float)rng.NextDouble();
                    e *= dec;
                    b[i] = Mathf.Sin(ph) * Mathf.Exp(-t / 0.16f) + l * Mathf.Exp(-t / 0.1f) * 4 + e * W() * 0.4f;
                }
                return Norm(b, 0.9f);
            }

            public static float[] Splash()
            {
                var b = Shot(2.5f); float l1 = 0, k1 = K(200), l2 = 0, k2 = K(1500), bf = 0, bph = 0, be = 0, bdec = Mathf.Exp(-1f / (0.03f * Rate));
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate, w = W(); l1 += (w - l1) * k1; l2 += (w - l2) * k2;
                    // Пузыри: короткие восходящие чирпы 300…1000 Гц, реже к концу.
                    if (rng.NextDouble() < 40 * Mathf.Exp(-t / 0.8f) / Rate) { bf = 300 + 700 * (float)rng.NextDouble(); be = 1; }
                    bf *= 1 + 8f / Rate; bph += Tau * bf / Rate; be *= bdec;
                    b[i] = (w - l1) * (1 - Mathf.Exp(-t / 0.015f)) * Mathf.Exp(-t / 0.6f) * 0.8f + l2 * Mathf.Exp(-t / 0.25f) * 3
                        + Mathf.Sin(bph) * be * 0.3f;
                }
                return Norm(b, 0.9f);
            }

            public static float[] Pop()
            {
                var b = Shot(0.5f); float l = 0, k = K(400), h = 0, kh = K(2000);
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate, w = W(); l += (w - l) * k; h += (w - h) * kh;
                    b[i] = l * Mathf.Clamp01(t / 0.01f) * Mathf.Exp(-t / 0.07f) * 5 + (w - h) * Mathf.Exp(-t / 0.015f) * 0.5f;
                }
                return Norm(b, 0.9f);
            }

            public static float[] Ignite()
            {
                var b = Shot(1.6f); float l = 0, k = K(300), h = 0, kh = K(1500), e = 0, s = 1, dec = Mathf.Exp(-1f / (0.0015f * Rate));
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate, w = W(); l += (w - l) * k; h += (w - h) * kh;
                    if (rng.NextDouble() < 3000 * Mathf.Exp(-t / 0.2f) / Rate) { e = (float)rng.NextDouble(); s = W() > 0 ? 1 : -1; }
                    e *= dec;
                    b[i] = e * s * 0.6f + l * (1 - Mathf.Exp(-t / 0.05f)) * Mathf.Exp(-t / 0.45f) * 6 + (w - h) * (t / 1.6f) * Mathf.Exp(-t / 0.8f) * 0.3f;
                }
                return Norm(b, 0.9f);
            }

            public static float[] Sputter()
            {
                var b = Shot(1.4f); var at = new[] { 0f, 0.18f, 0.33f, 0.62f, 0.8f, 1.05f }; float l = 0, k = K(600);
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate, env = 0; l += (W() - l) * k;
                    foreach (var t0 in at) if (t >= t0) env = Mathf.Max(env, Mathf.Exp(-(t - t0) / 0.05f) * (1 - t0 * 0.5f));
                    b[i] = l * env;
                }
                return Norm(b, 0.9f);
            }

            public static float[] Cutoff()
            {
                var b = Shot(1); float l = 0, k = K(500);
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate, w = W(); l += (w - l) * k;
                    b[i] = l * Mathf.Exp(-t / 0.3f) * 4 + w * Mathf.Exp(-t / 0.004f) * 0.3f;
                }
                return Norm(b, 0.8f);
            }

            public static float[] Rip()
            {
                var b = Shot(0.9f); float h = 0, kh = K(1500), e = 0, s = 1, dec = Mathf.Exp(-1f / (0.0008f * Rate));
                for (int i = 0; i < b.Length; i++)
                {
                    float t = (float)i / Rate, w = W(); h += (w - h) * kh;
                    float env = t < 0.6f ? 1 : Mathf.Exp(-(t - 0.6f) / 0.08f);
                    if (rng.NextDouble() < 6000f / Rate) { e = (float)rng.NextDouble(); s = W() > 0 ? 1 : -1; }
                    e *= dec;
                    b[i] = (e * s + (w - h) * 0.3f) * env;
                }
                return Norm(b, 0.9f);
            }
        }
    }
}
