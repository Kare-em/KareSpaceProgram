using UnityEngine;
using UnityEngine.Rendering;

namespace Kare.Space.Game
{
    /// <summary>
    /// Блик Солнца в объективе (GDD §9.3, «Солнце слепит»): ореол PBSky выключен (см. SunLight.Awake — серая пелена
    /// на орбите), а одного bloom мало — диск давал плотное белое пятно, не слепящее. Data-driven lens flare SRP
    /// собирается в коде (ассетов нет, сцена пересобирается билдером): ядро-свечение, лучи, засветка кадра и
    /// «призраки» линз по оси через центр кадра. Засветка и призраки гаснут к краю кадра (радиальная кривая) —
    /// пелена только когда смотришь на Солнце, а не когда оно у края, как было с ореолом PBSky.
    /// Перекрытие — по буферу глубины (борт, грунт, тело заслоняют блик).
    /// </summary>
    public static class SunFlare
    {
        /// <summary>Размер текстуры лучей, px.</summary>
        const int RayTexSize = 256;
        /// <summary>Число лучей: шесть пар — шестигранная диафрагма объектива.</summary>
        const int RayCount = 12;
        /// <summary>Общий масштаб блика. Единица размера элемента SRP — ≈ 4 % ширины кадра (замер 02.10.2026: лучи
        /// размера 1,1 — 30 px на 640), без множителя блик был звёздочкой. Размеры элементов ниже — в этих долях.</summary>
        const float FlareScale = 10;
        /// <summary>Край круга-свечения: доля радиуса под градиент. Ровно 1 гасит элемент целиком (замер: круги
        /// невидимы), 0,1 — резкий белый диск; 0,95 — мягкий ореол.</summary>
        const float GlowEdge = 0.95f;
        /// <summary>Степень спада свечения к краю: 1 — линейно (плоский диск), 3 — яркое ядро и мягкий хвост.</summary>
        const float GlowFallOff = 3;

        public static LensFlareComponentSRP Attach(GameObject sun)
        {
            var data = ScriptableObject.CreateInstance<LensFlareDataSRP>();
            data.name = "SunFlare";
            data.elements = new[]
            {
                // Ядро: плотное свечение вокруг диска.
                Circle(0, 0.35f, 1.0f, new Color(1f, 0.97f, 0.92f)),
                // Засветка: мягкий круг почти на весь кадр — «слепит».
                Circle(0, 2.6f, 0.3f, new Color(1f, 0.96f, 0.90f)),
                Rays(),
                // Призраки линз: шестиугольники по оси Солнце → центр (отрицательная позиция — за центр).
                Ghost(-0.45f, 0.05f, 0.06f, new Color(0.55f, 0.75f, 1f)),
                Ghost(-0.85f, 0.11f, 0.035f, new Color(0.65f, 1f, 0.70f)),
                Ghost(-1.35f, 0.07f, 0.05f, new Color(1f, 0.70f, 0.45f)),
                Ghost(0.35f, 0.03f, 0.06f, new Color(0.85f, 0.65f, 1f)),
            };

            var c = sun.AddComponent<LensFlareComponentSRP>();
            c.lensFlareData = data;
            c.scale = FlareScale;
            c.useOcclusion = true;
            c.occlusionRadius = 0.05f;
            c.sampleCount = 16;
            c.allowOffScreen = false;
            c.attenuationByLightShape = false;
            // Полная сила в центре кадра, к краю — четверть: Солнце у края экрана не застилает кадр.
            c.radialScreenAttenuationCurve = new AnimationCurve(new Keyframe(0, 1), new Keyframe(1, 0.25f));
            return c;
        }

        static LensFlareDataElementSRP Circle(float pos, float size, float intensity, Color tint)
        {
            return new LensFlareDataElementSRP
            {
                flareType = SRPLensFlareType.Circle,
                position = pos,
                uniformScale = size,
                localIntensity = intensity,
                tint = tint,
                fallOff = GlowFallOff,
                edgeOffset = GlowEdge,
                blendMode = SRPLensFlareBlendMode.Additive,
                modulateByLightColor = false,
            };
        }

        static LensFlareDataElementSRP Ghost(float pos, float size, float intensity, Color tint)
        {
            return new LensFlareDataElementSRP
            {
                flareType = SRPLensFlareType.Polygon,
                sideCount = 6,
                sdfRoundness = 0.3f,
                position = pos,
                uniformScale = size,
                localIntensity = intensity,
                tint = tint,
                fallOff = 0.6f,
                edgeOffset = 0.15f,
                blendMode = SRPLensFlareBlendMode.Additive,
                modulateByLightColor = false,
            };
        }

        static LensFlareDataElementSRP Rays()
        {
            return new LensFlareDataElementSRP
            {
                flareType = SRPLensFlareType.Image,
                lensFlareTexture = RayTexture(),
                position = 0,
                uniformScale = 1.1f,
                localIntensity = 0.6f,
                tint = new Color(1f, 0.97f, 0.93f),
                preserveAspectRatio = true,
                blendMode = SRPLensFlareBlendMode.Additive,
                modulateByLightColor = false,
            };
        }

        /// <summary>Лучи дифракции на диафрагме: узкие клинья с затуханием к краю, длина чередуется.</summary>
        static Texture2D RayTexture()
        {
            var tex = new Texture2D(RayTexSize, RayTexSize, TextureFormat.RGBA32, false, true)
            {
                name = "SunRays", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave,
            };
            var px = new Color32[RayTexSize * RayTexSize];
            float half = RayTexSize * 0.5f;
            for (int y = 0; y < RayTexSize; y++)
            for (int x = 0; x < RayTexSize; x++)
            {
                float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Atan2(dy, dx);
                float v = 0;
                for (int i = 0; i < RayCount; i++)
                {
                    float ang = Mathf.PI * 2 * i / RayCount + 0.13f;
                    float len = i % 2 == 0 ? 1f : 0.6f;
                    float da = Mathf.DeltaAngle(a * Mathf.Rad2Deg, ang * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                    float w = da * r * 60; // ширина луча в пикселях почти постоянна по длине
                    float fade = Mathf.Clamp01(1 - r / len);
                    v += Mathf.Exp(-w * w) * fade * fade;
                }
                v = Mathf.Clamp01(v);
                byte b = (byte)(v * 255);
                px[y * RayTexSize + x] = new Color32(b, b, b, b);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
