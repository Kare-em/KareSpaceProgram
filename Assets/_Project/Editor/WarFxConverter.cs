using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kare.Space.EditorTools
{
    /// <summary>
    /// Переводит материалы пака JMO WarFX на шейдер «Kare/WFX Particle HDRP» (§9.5, взрывы). Родные шейдеры пака —
    /// built-in CG: в HDRP рисуются мимо экспозиции и с чужой глубиной. Режим и смешивание берутся по имени старого
    /// шейдера; текстура, тинт и скорость прокрутки остаются в материале. Повторный запуск ничего не меняет;
    /// переимпорт пака возвращает старые шейдеры — тогда запустить меню снова.
    /// </summary>
    public static class WarFxConverter
    {
        const string PackRoot = "Assets/JMO Assets";
        const string TargetShader = "Kare/WFX Particle HDRP";

        struct Map
        {
            public int Mode;
            public BlendMode Src, Dst;
            public Map(int mode, BlendMode src, BlendMode dst) { Mode = mode; Src = src; Dst = dst; }
        }

        // Номера режимов — ветки Frag в WfxParticleHDRP.shader; меняешь одно — правь второе.
        static readonly Dictionary<string, Map> Maps = new Dictionary<string, Map>
        {
            { "WFX/Additive Alpha8", new Map(0, BlendMode.One, BlendMode.One) },
            { "WFX/Additive (Soft) Alpha8", new Map(1, BlendMode.One, BlendMode.One) },
            { "Legacy Shaders/Particles/Additive", new Map(2, BlendMode.One, BlendMode.One) },
            { "Legacy Shaders/Particles/Additive (Soft)", new Map(3, BlendMode.One, BlendMode.OneMinusSrcColor) },
            { "WFX/Alpha Blended (No Soft Particles)", new Map(4, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha) },
            { "Legacy Shaders/Particles/Alpha Blended", new Map(4, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha) },
            { "WFX/Scroll/Additive", new Map(5, BlendMode.One, BlendMode.One) },
            { "WFX/Scroll/Alpha Blended", new Map(6, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha) },
            { "WFX/Multiply Soft Tint", new Map(7, BlendMode.DstColor, BlendMode.SrcColor) },
            { "WFX/Scroll/Multiply Soft Tint", new Map(7, BlendMode.DstColor, BlendMode.SrcColor) },
            { "WFX/Scroll/Smoke", new Map(8, BlendMode.DstColor, BlendMode.SrcAlpha) },
            { "WFX/Multiply Alpha8", new Map(9, BlendMode.Zero, BlendMode.SrcColor) },
        };

        [MenuItem("Kare/Convert WarFX to HDRP")]
        public static void Convert()
        {
            var target = Shader.Find(TargetShader);
            if (target == null)
            {
                Debug.LogError("[WarFX] нет шейдера " + TargetShader);
                return;
            }
            if (!AssetDatabase.IsValidFolder(PackRoot))
            {
                Debug.LogWarning("[WarFX] пака нет: " + PackRoot);
                return;
            }
            int done = 0, already = 0;
            var skipped = new Dictionary<string, int>();
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { PackRoot }))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (m == null || m.shader == null) continue;
                if (m.shader == target) { already++; continue; }
                if (!Maps.TryGetValue(m.shader.name, out var map))
                {
                    skipped.TryGetValue(m.shader.name, out var n);
                    skipped[m.shader.name] = n + 1;
                    continue;
                }
                m.shader = target;
                m.SetFloat("_Mode", map.Mode);
                m.SetFloat("_SrcBlend", (float)map.Src);
                m.SetFloat("_DstBlend", (float)map.Dst);
                EditorUtility.SetDirty(m);
                done++;
            }
            AssetDatabase.SaveAssets();
            var rest = new List<string>();
            foreach (var kv in skipped) rest.Add(kv.Value + "×" + kv.Key);
            Debug.Log($"[WarFX] переведено {done}, уже было {already}, оставлено как есть: {string.Join(", ", rest)}");
        }
    }
}
