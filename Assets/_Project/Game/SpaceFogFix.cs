using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.Game
{
    /// <summary>
    /// Обход ошибки PBR-тумана HDRP 17.6 при взгляде на планету из космоса (GDD §9.2): борт рядом с камерой получал
    /// синюю линию лимба и голубую дымку поверх обшивки (жалоба «свечение лимба проходит поверх Crew Dragon»).
    /// Причина — EvaluatePbrAtmosphere (AtmosphericScattering.hlsl): для камеры над атмосферой проверка
    /// «луч кончается внутри» не сравнивает tFrag с tEntry, и фрагмент ДО входа в атмосферу получает
    /// L(вход, выход) − T·L(точка вне атмосферы, выход) ≠ 0. Отключить сам туман нельзя — им же окрашена дымка Земли.
    /// Решение: цвет кадра сохраняется до тумана (AfterOpaqueAndSky) и возвращается после него (BeforePreRefraction)
    /// для пикселей ближе <see cref="RestoreDist"/> — там луч заведомо целиком вне атмосферы, верный туман = 0.
    /// Пара: шейдер Settings/SpaceFogRestoreHDRP.shader. Прозрачное (факел, плазма) туман не трогает — им не нужно.
    /// </summary>
    public static class SpaceFogFix
    {
        /// <summary>Запас к верху атмосферы, м: граница сферы атмосферы в шейдере считается во float от центра планеты
        /// (≈ 6,4e6 м, шаг float ≈ 0,5 м); 100 м с лихвой, а борт на орбите от верха атмосферы в сотнях километров.</summary>
        const float TopMargin = 100;

        static GameObject root;
        static CustomPassVolume saveVol, restoreVol;
        static SavePass save;
        static RestorePass restore;

        /// <summary>Дистанция от камеры, ближе которой туман снимается; ≤ 0 — выключено.</summary>
        public static float RestoreDist { get; private set; }

        public static void Create(Shader shader, Transform parent)
        {
            if (root != null) return;
            if (shader == null) shader = Shader.Find("Hidden/Kare/SpaceFogRestore");
            if (shader == null) { Debug.LogWarning("[Sky] Нет шейдера Hidden/Kare/SpaceFogRestore — лимб будет просвечивать сквозь борт."); return; }
            root = new GameObject("SpaceFogFix");
            root.transform.SetParent(parent, false);
            saveVol = root.AddComponent<CustomPassVolume>();
            saveVol.isGlobal = true;
            saveVol.injectionPoint = CustomPassInjectionPoint.AfterOpaqueAndSky;
            save = (SavePass)saveVol.AddPassOfType(typeof(SavePass));
            restoreVol = root.AddComponent<CustomPassVolume>();
            restoreVol.isGlobal = true;
            restoreVol.injectionPoint = CustomPassInjectionPoint.BeforePreRefraction;
            restore = (RestorePass)restoreVol.AddPassOfType(typeof(RestorePass));
            restore.Shader = shader;
            Set(null, 0);
        }

        /// <summary>Каждый кадр из SkyController: камера, центр планеты (м, мир), радиус сферы планеты в сцене (м,
        /// уже с «оболочкой» k) и высота верха атмосферы (м, не масштабируется — так её считает HDRP).</summary>
        public static void Update(Camera cam, Vector3 center, float radius, float atmoTop, bool hasAtmo)
        {
            float d = cam != null && hasAtmo ? Vector3.Distance(cam.transform.position, center) - (radius + atmoTop + TopMargin) : 0;
            Set(cam, d);
        }

        static void Set(Camera cam, float dist)
        {
            if (root == null) return;
            RestoreDist = dist;
            bool on = dist > 0 && cam != null;
            save.enabled = restore.enabled = on;
            save.Target = restore.Target = cam;
            restore.Dist = dist;
        }

        /// <summary>Верх атмосферы как в PhysicallyBasedSky.GetMaximumAltitude (internal в HDRP 17.6).</summary>
        public static float AtmosphereTop(PhysicallyBasedSky s)
        {
            if (s.type.value == PhysicallyBasedSkyModel.Custom)
                return Mathf.Max(s.airMaximumAltitude.value, s.aerosolMaximumAltitude.value);
            // 8000 и 1200 м — шкалы высот воздуха и аэрозоля по умолчанию, 0,144765 — доля «толщина слоя» HDRP.
            float aerosol = s.type.value == PhysicallyBasedSkyModel.EarthSimple ? 1200 / 0.144765f : s.aerosolMaximumAltitude.value;
            return Mathf.Max(8000 / 0.144765f, aerosol);
        }

        internal static RTHandle Saved;

        [Serializable]
        sealed class SavePass : CustomPass
        {
            [NonSerialized] public Camera Target;
            protected override bool executeInSceneView => false;

            protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
            {
                targetColorBuffer = TargetBuffer.None;
                targetDepthBuffer = TargetBuffer.None;
                Saved ??= RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
                    colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true, name: "KareSpaceFogSaved");
            }

            protected override void Execute(CustomPassContext ctx)
            {
                if (ctx.hdCamera.camera != Target || Saved == null) return;
                CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, Saved);
            }

            protected override void Cleanup()
            {
                Saved?.Release();
                Saved = null;
            }
        }

        [Serializable]
        sealed class RestorePass : CustomPass
        {
            [NonSerialized] public Camera Target;
            [NonSerialized] public Shader Shader;
            Material material;
            [NonSerialized] public float Dist;
            static readonly int SavedId = Shader.PropertyToID("_KareSavedColor"), DistId = Shader.PropertyToID("_KareRestoreDist");
            protected override bool executeInSceneView => false;

            protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
            {
                targetColorBuffer = TargetBuffer.Camera;
                targetDepthBuffer = TargetBuffer.None;
                if (material == null && Shader != null) material = CoreUtils.CreateEngineMaterial(Shader);
            }

            protected override void Execute(CustomPassContext ctx)
            {
                if (ctx.hdCamera.camera != Target || Saved == null || material == null) return;
                ctx.propertyBlock.SetTexture(SavedId, Saved);
                ctx.propertyBlock.SetFloat(DistId, Dist);
                CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
                CoreUtils.DrawFullScreen(ctx.cmd, material, ctx.propertyBlock, shaderPassId: 0);
            }

            protected override void Cleanup() { CoreUtils.Destroy(material); material = null; }
        }
    }
}
