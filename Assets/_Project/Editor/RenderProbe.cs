using System.IO;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Kare.Space.EditorTools
{
    /// <summary>
    /// Замер кадра из MCP (execute_code → рефлексия): рендер главной камеры в RT нужного размера, PNG в указанную
    /// папку (скретчпад — файлов в Assets нет) и средняя яркость. Не игровой код. Экран Game View при этом не
    /// трогается; HUD (OnGUI) в кадр не попадает.
    /// </summary>
    public static class RenderProbe
    {
        /// <summary>Рисует frames кадров (экспозиция догоняет), пишет PNG и возвращает «средняя яркость 0..255 / EV».
        /// Пары: w·h — не больше 960×540 по договорённости (экономия контекста).</summary>
        public static string Shot(string path, int w, int h, int frames)
        {
            var cam = Camera.main;
            if (cam == null) return "нет камеры";
            var old = cam.targetTexture;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            for (int i = 0; i < Mathf.Max(1, frames); i++) cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = old;
            Object.DestroyImmediate(rt);

            var px = tex.GetPixels32();
            double sum = 0; int white = 0;
            foreach (var p in px)
            {
                double l = 0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b;
                sum += l;
                if (l > 250) white++;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return $"mean={sum / px.Length:F1} white%={100.0 * white / px.Length:F1} EV={Ev(cam):F2}";
        }

        /// <summary>EV100 из 1×1 текстуры экспозиции камеры: EV = −log2(r·1,2) (pitfalls-render).</summary>
        public static float Ev(Camera cam)
        {
            var hd = HDCamera.GetOrCreate(cam);
            // currentExposureTextures — internal: достаём рефлексией (структура ExposureTextures, поле current).
            var prop = typeof(HDCamera).GetField("currentExposureTextures",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (prop == null) return float.NaN;
            object et = prop.GetValue(hd);
            var cur = et.GetType().GetField("current");
            var src = cur != null ? cur.GetValue(et) as RenderTexture : null;
            if (src == null) return float.NaN;
            var tmp = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Graphics.Blit(src, tmp);
            var prev = RenderTexture.active;
            RenderTexture.active = tmp;
            var t = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            t.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            float r = t.GetPixel(0, 0).r;
            Object.DestroyImmediate(t);
            return -Mathf.Log(Mathf.Max(r * 1.2f, 1e-9f), 2);
        }
    }
}
