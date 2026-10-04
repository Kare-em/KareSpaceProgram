using System;
using System.IO;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Дисковый кеш процедурных текстур тел (§9.4): генерация Земли 4096 — 7,8 с, огней 2048 — 3,9 с, весь уровень
    /// «Высокая» — 23 с, «Ультра» — 77 с (замер 03.10.2026). Готовая текстура сжимается в DXT5 (1 байт на тексель
    /// против 4 у RGBA32 — и в видеопамяти тоже) и пишется сырыми данными с мипами; следующий старт грузит её за
    /// миллисекунды. Генераторы детерминированы, поэтому ключ — тело, вид и размер; поменял генератор — подними Version.
    /// </summary>
    public static class TextureCache
    {
        /// <summary>Версия генераторов. Пара: BodyRenderer.BuildTexture/BuildClouds/AddNightLights, EarthSurface —
        /// без подъёма после их правки игра будет показывать старые текстуры из кеша.</summary>
        const int Version = 3; // 3 — рельеф по реальным картам высот (bake-dem.py): маска и цвета Земли другие
        const int Magic = 0x4B544331; // «KTC1»

        static string Dir => Path.Combine(Application.persistentDataPath, "BodyTextures");

        static string PathOf(string key) => Path.Combine(Dir, $"{key}_v{Version}.bin");

        /// <summary>Текстура из кеша или null. linear — для масок (данные, не цвет).</summary>
        public static Texture2D Load(string key, bool linear, int aniso = 1)
        {
            var path = PathOf(key);
            if (!File.Exists(path)) return null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < 16 || BitConverter.ToInt32(bytes, 0) != Magic) return null;
                int w = BitConverter.ToInt32(bytes, 4), h = BitConverter.ToInt32(bytes, 8);
                var fmt = (TextureFormat)BitConverter.ToInt32(bytes, 12);
                var tex = new Texture2D(w, h, fmt, true, linear) { name = key };
                Setup(tex, aniso);
                var raw = new byte[bytes.Length - 16];
                Buffer.BlockCopy(bytes, 16, raw, 0, raw.Length);
                tex.LoadRawTextureData(raw);
                tex.Apply(false, true);
                return tex;
            }
            catch (Exception ex)
            {
                // Битый файл (оборвалась запись) — не повод падать: перегенерируем.
                Debug.LogWarning($"TextureCache: {key} не прочитан ({ex.Message}), строю заново");
                return null;
            }
        }

        /// <summary>Сжать свежую текстуру (должна быть читаемой, с мипами), записать в кеш и выгрузить копию из ОЗУ.</summary>
        public static Texture2D Store(string key, Texture2D tex, int aniso = 1)
        {
            tex.Compress(false);
            Setup(tex, aniso);
            try
            {
                Directory.CreateDirectory(Dir);
                var raw = tex.GetRawTextureData();
                var bytes = new byte[16 + raw.Length];
                BitConverter.GetBytes(Magic).CopyTo(bytes, 0);
                BitConverter.GetBytes(tex.width).CopyTo(bytes, 4);
                BitConverter.GetBytes(tex.height).CopyTo(bytes, 8);
                BitConverter.GetBytes((int)tex.format).CopyTo(bytes, 12);
                Buffer.BlockCopy(raw, 0, bytes, 16, raw.Length);
                // Через временный файл: оборванная запись не оставит полуфайл под рабочим именем.
                var path = PathOf(key);
                File.WriteAllBytes(path + ".tmp", bytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"TextureCache: {key} не записан ({ex.Message})");
            }
            tex.Apply(false, true);
            return tex;
        }

        static void Setup(Texture2D tex, int aniso)
        {
            tex.wrapModeU = TextureWrapMode.Repeat;
            tex.wrapModeV = TextureWrapMode.Clamp;
            tex.anisoLevel = aniso;
        }
    }
}
