using System;
using UnityEditor;

namespace Kare.Space.EditorTools
{
    /// <summary>
    /// Закрепляет порт MCP for Unity за проектом. Пакет хранит адрес сервера в EditorPrefs, а они
    /// общие для всех проектов на машине: без пина второй открытый редактор после перекомпиляции
    /// переезжает на чужой порт. Пара: Car_Train_Simulator — 8765, KareSpaceProgram — 8767
    /// (у каждого свой .mcp.json и такой же файл с собственным портом).
    /// </summary>
    [InitializeOnLoad]
    internal static class McpPortPin
    {
        private const string Url = "http://127.0.0.1:8767";
        private const string PrefKey = "MCPForUnity.HttpUrl";

        static McpPortPin()
        {
            if (EditorPrefs.GetString(PrefKey, string.Empty) == Url) return;
            EditorPrefs.SetString(PrefKey, Url);
            // Кеш настроек пакета мог прочитать чужой порт раньше нас — перечитываем через рефлексию,
            // чтобы не зависеть от сборки пакета при компиляции.
            var cache = Type.GetType("MCPForUnity.Editor.Services.EditorConfigurationCache, MCPForUnity.Editor");
            var instance = cache?.GetProperty("Instance")?.GetValue(null);
            cache?.GetMethod("Refresh")?.Invoke(instance, null);
        }
    }
}
