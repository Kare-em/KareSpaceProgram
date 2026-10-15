using System.Collections.Generic;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Радиальные профили колец (GDD §9.4): оптическая толщина τ(r), доля пыли и альбедо частиц — в двух
    /// одномерных текстурах, которые читает шейдер Kare/Planet Rings HDRP. Радиусы — истинные, км, из
    /// наблюдений «Вояджеров» и «Кассини». Полоса SSS у Сатурна даёт только тонкую рябь внутри колец:
    /// её яркость — не τ, а снимок при одном освещении, и щели в ней размыты.
    /// </summary>
    public static class RingProfiles
    {
        /// <summary>Готовый профиль: τ нормирована на TauScale (RHalf держит ≈ 3 знака), радиусы в м.</summary>
        public sealed class Profile
        {
            public double Inner, Outer;
            public float TauScale;
            /// <summary>R — τ / TauScale.</summary>
            public Texture2D Tau;
            /// <summary>RGB — альбедо частиц (линейное), A — доля пыли (рассеивает вперёд, §9.4).</summary>
            public Texture2D Color;
        }

        /// <summary>Узел кусочно-линейной τ: радиус, км; τ; доля пыли.</summary>
        struct Knot { public double R; public float Tau, Dust; public Knot(double r, float t, float d) { R = r; Tau = t; Dust = d; } }
        /// <summary>Щель (τ внутри почти ноль, остаток — пыль) или колечко (ширина, τ, пыль).</summary>
        struct Band { public double R0, R1; public float Tau, Dust; public Band(double r0, double r1, float t, float d) { R0 = r0; R1 = r1; Tau = t; Dust = d; } }

        /// <summary>Подвыборок на тексель: щели уже текселя (Кейпера 3 км при 9 км на тексель) должны
        /// ослаблять τ на свою долю, а не пропадать или съедать тексель целиком.</summary>
        const int SubSamples = 8;

        public static bool Has(string id) => id == "saturn" || id == "jupiter" || id == "uranus" || id == "neptune";

        public static Profile Build(string id, Texture2D saturnStrip)
        {
            switch (id)
            {
                case "saturn": return Saturn(saturnStrip);
                case "jupiter": return Jupiter();
                case "uranus": return Uranus();
                case "neptune": return Neptune();
            }
            return null;
        }

        // ------------------------------------------------------------------ Сатурн

        /// <summary>Пределы текстуры, км: от внутреннего края D до внешней оболочки F. Пара: SaturnTexels —
        /// 75 000 км / 8192 ≈ 9,2 км на тексель (щель Килера 37 км — четыре текселя).</summary>
        const double SaturnInner = 66000, SaturnOuter = 141000;
        const int SaturnTexels = 8192;

        /// <summary>Края колец, км (Cassini, Nicholson 2008): C, B, деление Кассини, A.</summary>
        const double CInner = 74490, BInner = 91980, BOuter = 117580, AInner = 122170, AOuter = 136775;
        /// <summary>F: ядро σ 15 км, τ 0,5; пыльная оболочка σ 250 км (её и видно на просвет).</summary>
        const double FRadius = 140180, FCoreSigma = 15, FHaloSigma = 250;
        const float FCoreTau = 0.5f, FHaloTau = 0.005f;

        static readonly Knot[] SaturnKnots =
        {
            // D: пыль, τ ~1e-3 — видно только против Солнца.
            new Knot(66900, 0, 1), new Knot(67200, 1e-3f, 1), new Knot(74400, 1.5e-3f, 1),
            // C: полупрозрачное, τ 0,05–0,15, к внешнему краю «пандус» до 0,25 (переход в B).
            new Knot(CInner, 0.06f, 0.1f), new Knot(77000, 0.08f, 0.1f), new Knot(79500, 0.05f, 0.1f),
            new Knot(84000, 0.08f, 0.1f), new Knot(88500, 0.14f, 0.1f), new Knot(90600, 0.12f, 0.1f),
            new Knot(BInner - 10, 0.25f, 0.05f),
            // B: B1 τ 1–1,5, B2 2,5–3, ядро B3 до 5 (непрозрачно под любым углом), B4 2–2,5.
            new Knot(BInner, 1.1f, 0), new Knot(99000, 1.6f, 0), new Knot(99300, 2.6f, 0), new Knot(104500, 3.2f, 0),
            new Knot(104800, 5f, 0), new Knot(110000, 5f, 0), new Knot(110300, 2.6f, 0), new Knot(BOuter, 2.0f, 0),
            // Деление Кассини: дно τ ~0,1, у края A — свой пандус до 0,35.
            new Knot(BOuter + 10, 0.1f, 0.1f), new Knot(121000, 0.12f, 0.1f), new Knot(122050, 0.35f, 0.05f),
            // A: 0,5–0,8 у внутреннего края, к внешнему 0,4.
            new Knot(AInner, 0.55f, 0), new Knot(124000, 0.75f, 0), new Knot(127500, 0.6f, 0), new Knot(131500, 0.55f, 0),
            new Knot(134000, 0.45f, 0), new Knot(AOuter - 10, 0.35f, 0),
            // Деление Роша: пыль почти без частиц, F — отдельной гауссианой.
            new Knot(AOuter, 1e-4f, 1), new Knot(139500, 1e-4f, 1), new Knot(139600, 0, 1),
        };

        /// <summary>Щели, км (Colwell 2009). В щелях остаётся пыль, поэтому τ не ноль, и на просвет они слабо светятся.</summary>
        static readonly Band[] SaturnGaps =
        {
            new Band(77750, 77930, 2e-3f, 1),     // Коломбо
            new Band(87340, 87610, 2e-3f, 1),     // Максвелл
            new Band(88690, 88720, 2e-3f, 1),     // Бонд
            new Band(90200, 90220, 2e-3f, 1),     // Доуз
            new Band(117680, 118030, 2e-3f, 1),   // Гюйгенс
            new Band(118183, 118469, 2e-3f, 1),   // Гершель
            new Band(118597, 118630, 2e-3f, 1),   // Рассел
            new Band(118931, 118969, 2e-3f, 1),   // Джеффрис
            new Band(119403, 119406, 2e-3f, 1),   // Кейпер
            new Band(119848, 120086, 2e-3f, 1),   // Лаплас
            new Band(120236, 120246, 2e-3f, 1),   // Бессель
            new Band(120305, 120318, 2e-3f, 1),   // Барнард
            new Band(133423, 133745, 3e-3f, 1),   // Энке (пыльные колечки внутри)
            new Band(136485, 136522, 1e-3f, 1),   // Килер
        };

        /// <summary>Узкие колечки в щелях, км: ±полуширина, τ (Титана, Максвелла, Гюйгенса).</summary>
        static readonly Band[] SaturnRinglets =
        {
            new Band(77859, 77884, 0.4f, 0.1f),
            new Band(87459, 87523, 1.0f, 0),
            new Band(117805, 117845, 1.0f, 0),
        };

        /// <summary>
        /// Альбедо частиц по зонам (линейное, «ламбертова сфера»): B и A — чистый водяной лёд с примесью
        /// толинов (0,5–0,6), C и деление Кассини — грязнее и темнее (≈0,2–0,25), как в фотометрии «Кассини».
        /// </summary>
        static float SaturnAlbedo(double r) =>
            r < CInner ? 0.3f : r < BInner ? 0.22f : r < BOuter ? 0.58f : r < AInner ? 0.25f : r < AOuter ? 0.48f : 0.5f;

        /// <summary>
        /// Привязка полосы SaturnRing.png (столбец из 8192) к радиусу, км: шаг неравномерный, 7,6–13,3 км на
        /// столбец — линейная подгонка из прежнего AddRing сдвигала деление Кассини на 300 км. Узлы сняты по
        /// краям колец и щелям в самой полосе (03.10 → 15.10.2026). Пара: SaturnRing.png — меняешь одно, правь второе.
        /// </summary>
        static readonly double[] StripCol = { 590, 2330, 5504, 5848, 7320, 7670, 7706 };
        static readonly double[] StripKm = { CInner, BInner, BOuter, AInner, 133423, 136503, AOuter };

        /// <summary>Сглаживание полосы, км: рябь — отношение яркости к её среднему в этом окне. Окно для цвета уже —
        /// у края C/B цвет меняется скачком.</summary>
        const double RippleWindow = 500, TintWindow = 150;
        /// <summary>Усиление ряби: яркость полосы ~ 1 − e^(−τ/μ), при τ ≳ 1 она насыщается и прячет контраст τ.</summary>
        const float RippleGamma = 1.5f, RippleMin = 0.3f, RippleMax = 3f;
        /// <summary>Доля ряби в альбедо: 0,4 при ряби 0,3–3 даёт ×0,62–1,55; потолок — чистый лёд.</summary>
        const float AlbedoRipple = 0.4f, AlbedoMax = 0.9f;

        static Profile Saturn(Texture2D strip)
        {
            int n = SaturnTexels;
            double step = (SaturnOuter - SaturnInner) / n;
            var ripple = new float[n];
            var tint = new Vector3[n];
            for (int i = 0; i < n; i++) { ripple[i] = 1; tint[i] = new Vector3(1.05f, 1f, 0.92f); }
            if (strip != null) StripDetail(strip, n, step, ripple, tint);

            var tau = new float[n];
            var col = new Color[n];
            for (int i = 0; i < n; i++)
            {
                double sumTau = 0, sumDust = 0;
                for (int s = 0; s < SubSamples; s++)
                {
                    double r = SaturnInner + (i + (s + 0.5) / SubSamples) * step;
                    Eval(SaturnKnots, r, out float t, out float d);
                    t *= ripple[i];
                    Overlay(SaturnGaps, r, ref t, ref d, false);
                    Overlay(SaturnRinglets, r, ref t, ref d, true);
                    double x = (r - FRadius) / FCoreSigma, y = (r - FRadius) / FHaloSigma;
                    float fc = FCoreTau * (float)System.Math.Exp(-0.5 * x * x), fh = FHaloTau * (float)System.Math.Exp(-0.5 * y * y);
                    if (fc + fh > 1e-6f)
                    {
                        // Доля пыли смешивается по вкладу в τ: ядро F наполовину пыльное, оболочка — целиком.
                        d = (d * t + 0.5f * fc + fh) / (t + fc + fh);
                        t += fc + fh;
                    }
                    sumTau += t;
                    sumDust += d * t;
                }
                float tm = (float)(sumTau / SubSamples);
                float dm = sumTau > 0 ? (float)(sumDust / sumTau) : 1;
                double rc = SaturnInner + (i + 0.5) * step;
                // Рябь — и в альбедо: у B τ ≈ 2–5, яркость по τ насыщена, и без этого B — ровная серая лента
                // (замер 15.10.2026: 28–29 из 255 по всей ширине B). Пара: RippleMin/RippleMax.
                float a = Mathf.Min(SaturnAlbedo(rc) * Mathf.Pow(ripple[i], AlbedoRipple), AlbedoMax);
                col[i] =new Color(a * tint[i].x, a * tint[i].y, a * tint[i].z, dm);
                tau[i] = tm;
            }
            return Pack(tau, col, SaturnInner, SaturnOuter);
        }

        /// <summary>
        /// Рябь и оттенок из полосы SSS. Текстура импортирована нечитаемой (BC7), поэтому — через GPU:
        /// Blit в sRGB RT того же размера (мип 0, без размытия) и ReadPixels средней полосы строк.
        /// </summary>
        static void StripDetail(Texture2D strip, int n, double step, float[] ripple, Vector3[] tint)
        {
            int w = strip.width, h = strip.height, rows = Mathf.Min(16, h);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(strip, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var read = new Texture2D(w, rows, TextureFormat.RGBA32, false, false);
            read.ReadPixels(new Rect(0, (h - rows) / 2, w, rows), 0, 0, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var px = read.GetPixels32();
            Object.Destroy(read);

            // Строки почти одинаковы (разброс альфы ≤ 7 из 255) — среднее по ним снимает шум сжатия.
            var lum = new double[w];
            var rgb = new Vector3[w];
            for (int c = 0; c < w; c++)
            {
                Vector3 sum = Vector3.zero;
                float al = 0;
                for (int y = 0; y < rows; y++)
                {
                    var p = px[y * w + c];
                    sum += new Vector3(Mathf.GammaToLinearSpace(p.r / 255f), Mathf.GammaToLinearSpace(p.g / 255f), Mathf.GammaToLinearSpace(p.b / 255f));
                    al += p.a / 255f;
                }
                sum /= rows;
                al /= rows;
                rgb[c] = sum;
                // Яркость полосы — как пропускание слоя: цвет × непрозрачность.
                lum[c] = (0.2126 * sum.x + 0.7152 * sum.y + 0.0722 * sum.z) * al;
            }

            // Значение полосы в каждом текселе профиля, затем сглаживание внутри своей зоны (C, B, Кассини, A):
            // окно через край кольца смешало бы B с делением и дало ложный всплеск у края.
            var s = new double[n];
            var c3 = new Vector3[n];
            var zone = new int[n];
            for (int i = 0; i < n; i++)
            {
                double r = SaturnInner + (i + 0.5) * step;
                zone[i] = r < CInner || r >= AOuter ? -1 : r < BInner ? 0 : r < BOuter ? 1 : r < AInner ? 2 : 3;
                if (zone[i] < 0) continue;
                double colF = Interp(StripKm, StripCol, r);
                int c0 = Mathf.Clamp((int)colF, 0, w - 2);
                float f = (float)(colF - c0);
                s[i] = lum[c0] + (lum[c0 + 1] - lum[c0]) * f;
                c3[i] = Vector3.Lerp(rgb[c0], rgb[c0 + 1], f);
            }
            int rw = Mathf.Max(1, (int)(RippleWindow / step)), tw = Mathf.Max(1, (int)(TintWindow / step));
            for (int i = 0; i < n; i++)
            {
                if (zone[i] < 0) continue;
                double avg = 0; int cnt = 0;
                Vector3 ct = Vector3.zero; int cntT = 0;
                for (int j = Mathf.Max(0, i - rw); j <= Mathf.Min(n - 1, i + rw); j++)
                {
                    if (zone[j] != zone[i]) continue;
                    avg += s[j]; cnt++;
                    if (j >= i - tw && j <= i + tw) { ct += c3[j]; cntT++; }
                }
                avg /= cnt;
                if (avg > 1e-5) ripple[i] = Mathf.Clamp(Mathf.Pow((float)(s[i] / avg), RippleGamma), RippleMin, RippleMax);
                ct /= Mathf.Max(1, cntT);
                float l = 0.2126f * ct.x + 0.7152f * ct.y + 0.0722f * ct.z;
                // Оттенок — цвет / яркость; пределы — чтобы шум сжатия в тёмных щелях не красил кольцо в зелёный.
                if (l > 1e-4f) tint[i] = new Vector3(Mathf.Clamp(ct.x / l, 0.7f, 1.35f), Mathf.Clamp(ct.y / l, 0.7f, 1.35f), Mathf.Clamp(ct.z / l, 0.7f, 1.35f));
            }
        }

        // ------------------------------------------------------------------ Юпитер, Уран, Нептун

        /// <summary>
        /// Игровое преувеличение (§9.4): настоящие τ (у Юпитера 1e-6) и альбедо 0,03 дают кольцо, которого глаз не
        /// видит ни под каким углом — у игрока на пролёте «Вояджера» колец просто нет. Поднимаем τ до ~1e-2…1e-1 и
        /// альбедо в 2–4 раза: кольцо бледное, но различимое; Сатурн не трогаем — он и так виден.
        /// Пара: VisualTauCap — узкие кольца Урана (ε, τ 1,5) после усиления не должны стать стеной: мипы усредняют τ,
        /// а не прозрачность, и издали непрозрачная нить раздувалась бы в широкую яркую полосу.
        /// </summary>
        const float JupiterTauGain = 5000, JupiterAlbedoGain = 2.5f;
        const float UranusTauGain = 20, UranusAlbedoGain = 4;
        const float NeptuneTauGain = 15, NeptuneAlbedoGain = 3;
        const float VisualTauCap = 2;

        /// <summary>Юпитер: гало 92–122,5 тыс. км и главное кольцо до 129 тыс. км, τ ~1e-6, сплошь пыль
        /// (Ockert-Bell 1999) — видно только на просвет, как на снимках «Вояджера» из тени.</summary>
        static Profile Jupiter()
        {
            var knots = new[]
            {
                new Knot(92000, 0, 1), new Knot(100000, 3e-7f, 1), new Knot(122500, 1e-6f, 1),
                new Knot(123000, 3e-6f, 1), new Knot(127800, 5e-6f, 1), new Knot(128900, 6e-6f, 1), new Knot(129200, 0, 1),
            };
            return Simple(knots, null, 90000, 130000, 2048, new Color(0.06f, 0.05f, 0.04f), JupiterTauGain, JupiterAlbedoGain);
        }

        /// <summary>Уран: девять узких колец (Elliot, French 1987) — тёмный материал, альбедо ~0,03, и пыльные
        /// λ, ζ. Кольца уже текселя (≈3,7 км): подвыборки дают им τ по доле покрытия.</summary>
        static Profile Uranus()
        {
            var knots = new[] { new Knot(37000, 0, 1), new Knot(38000, 1e-4f, 1), new Knot(41300, 1e-4f, 1), new Knot(41400, 0, 1) };
            var rings = new[]
            {
                R(41837, 1.5, 0.3f, 0), R(42234, 2, 0.5f, 0), R(42571, 2, 0.3f, 0),   // 6, 5, 4
                R(44718, 7, 0.4f, 0), R(45661, 7, 0.3f, 0),                            // α, β
                R(47176, 1.5, 0.4f, 0), R(47176, 27, 1e-3f, 1),                        // η и его пыльная подложка
                R(47627, 3, 1.5f, 0), R(48300, 6, 0.5f, 0),                            // γ, δ
                R(50023, 2, 0.1f, 1),                                                  // λ
                R(51149, 58, 1.5f, 0),                                                 // ε, 20–96 км по эксцентриситету
            };
            return Simple(knots, rings, 37000, 52000, 4096, new Color(0.03f, 0.03f, 0.03f), UranusTauGain, UranusAlbedoGain);
        }

        /// <summary>Нептун: Галле, Леверье, Лассель, Араго, Адамс — пыльные (доля пыли 0,5–0,7), τ 1e-4…4e-3,
        /// красноватые (Porco 1995). Дуги Адамса не рисуются.</summary>
        static Profile Neptune()
        {
            var knots = new[]
            {
                new Knot(40900, 0, 1), new Knot(41000, 1e-4f, 1), new Knot(42900, 1e-4f, 1), new Knot(43000, 0, 1),
                new Knot(53200, 1.5e-4f, 0.6f), new Knot(57100, 1.5e-4f, 0.6f), new Knot(57200, 0, 0.6f),
            };
            var rings = new[] { R(53200, 56, 2e-3f, 0.5f), R(57200, 50, 1e-4f, 0.7f), R(62932, 18, 4e-3f, 0.5f) };
            return Simple(knots, rings, 40000, 63500, 4096, new Color(0.045f, 0.035f, 0.03f), NeptuneTauGain, NeptuneAlbedoGain);
        }

        static Band R(double center, double halfWidth, float tau, float dust) => new Band(center - halfWidth, center + halfWidth, tau, dust);

        static Profile Simple(Knot[] knots, Band[] rings, double inner, double outer, int n, Color albedo, float tauGain, float albedoGain)
        {
            albedo = new Color(Mathf.Min(albedo.r * albedoGain, 1), Mathf.Min(albedo.g * albedoGain, 1), Mathf.Min(albedo.b * albedoGain, 1));
            double step = (outer - inner) / n;
            var tau = new float[n];
            var col = new Color[n];
            for (int i = 0; i < n; i++)
            {
                double sumTau = 0, sumDust = 0;
                for (int s = 0; s < SubSamples; s++)
                {
                    double r = inner + (i + (s + 0.5) / SubSamples) * step;
                    Eval(knots, r, out float t, out float d);
                    if (rings != null) Overlay(rings, r, ref t, ref d, true);
                    t = Mathf.Min(t * tauGain, VisualTauCap);
                    sumTau += t;
                    sumDust += d * t;
                }
                tau[i] = (float)(sumTau / SubSamples);
                col[i] = new Color(albedo.r, albedo.g, albedo.b, sumTau > 0 ? (float)(sumDust / sumTau) : 1);
            }
            return Pack(tau, col, inner, outer);
        }

        // ------------------------------------------------------------------ общее

        static void Eval(Knot[] k, double r, out float tau, out float dust)
        {
            tau = 0; dust = 1;
            if (r <= k[0].R || r >= k[k.Length - 1].R) return;
            for (int i = 1; i < k.Length; i++)
            {
                if (r > k[i].R) continue;
                float f = (float)((r - k[i - 1].R) / (k[i].R - k[i - 1].R));
                tau = Mathf.Lerp(k[i - 1].Tau, k[i].Tau, f);
                dust = Mathf.Lerp(k[i - 1].Dust, k[i].Dust, f);
                return;
            }
        }

        /// <summary>add = false — щель заменяет τ; true — колечко складывается с фоном.</summary>
        static void Overlay(Band[] bands, double r, ref float tau, ref float dust, bool add)
        {
            foreach (var b in bands)
            {
                if (r < b.R0 || r > b.R1) continue;
                if (add)
                {
                    float t = tau + b.Tau;
                    dust = t > 0 ? (dust * tau + b.Dust * b.Tau) / t : b.Dust;
                    tau = t;
                }
                else { tau = b.Tau; dust = b.Dust; }
            }
        }

        static double Interp(double[] xs, double[] ys, double x)
        {
            if (x <= xs[0]) return ys[0] + (x - xs[0]) * (ys[1] - ys[0]) / (xs[1] - xs[0]);
            for (int i = 1; i < xs.Length; i++)
                if (x <= xs[i]) return ys[i - 1] + (x - xs[i - 1]) * (ys[i] - ys[i - 1]) / (xs[i] - xs[i - 1]);
            int m = xs.Length - 1;
            return ys[m] + (x - xs[m]) * (ys[m] - ys[m - 1]) / (xs[m] - xs[m - 1]);
        }

        /// <summary>
        /// Текстуры с мипами, собранными здесь: τ усредняется (боксом), а не берётся каждым вторым текселем —
        /// иначе издали щели мерцают («муар»). Trilinear + анизотропия: кольцо почти всегда видно наискось.
        /// </summary>
        static Profile Pack(float[] tau, Color[] col, double innerKm, double outerKm)
        {
            float max = 0;
            foreach (var t in tau) max = Mathf.Max(max, t);
            if (max <= 0) max = 1;
            int n = tau.Length;
            var tt = new Texture2D(n, 1, TextureFormat.RHalf, true, true) { name = "Ring Tau", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 16 };
            var level = new float[n];
            for (int i = 0; i < n; i++) level[i] = tau[i] / max;
            for (int mip = 0, w = n; mip < tt.mipmapCount; mip++)
            {
                var half = new ushort[w];
                for (int i = 0; i < w; i++) half[i] = Mathf.FloatToHalf(level[i]);
                tt.SetPixelData(half, mip);
                if (w == 1) break;
                int nw = Mathf.Max(1, w / 2);
                var next = new float[nw];
                for (int i = 0; i < nw; i++) next[i] = 0.5f * (level[Mathf.Min(2 * i, w - 1)] + level[Mathf.Min(2 * i + 1, w - 1)]);
                level = next;
                w = nw;
            }
            tt.Apply(false, true);

            var ct = new Texture2D(n, 1, TextureFormat.RGBA32, true, true) { name = "Ring Color", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 16 };
            ct.SetPixels(col);
            ct.Apply(true, true);
            return new Profile { Inner = innerKm * 1e3, Outer = outerKm * 1e3, TauScale = max, Tau = tt, Color = ct };
        }
    }
}
