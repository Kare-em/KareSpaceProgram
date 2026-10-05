using System.Collections.Generic;
using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Иконки деталей конструктора (§5.4): id детали → PNG с прозрачным фоном. Пишет запекание (меню Kare/Bake Part Icons,
    /// PartIconBaker) в Resources/PartIcons.asset; рантайм читает через Resources, сцену трогать не нужно.
    /// Нет ассета или иконки — строка рисуется без неё, как раньше.
    /// </summary>
    public sealed class PartIconSet : ScriptableObject
    {
        /// <summary>Имя ассета в Resources (пара: PartIconBaker.SetPath).</summary>
        public const string ResourceName = "PartIcons";

        public string[] Ids = new string[0];
        public Texture2D[] Icons = new Texture2D[0];

        static Dictionary<string, Texture2D> map;

        public static Texture2D Get(string id)
        {
            if (map == null)
            {
                map = new Dictionary<string, Texture2D>();
                var set = Resources.Load<PartIconSet>(ResourceName);
                if (set != null)
                    for (int i = 0; i < set.Ids.Length && i < set.Icons.Length; i++)
                        if (set.Icons[i] != null) map[set.Ids[i]] = set.Icons[i];
            }
            return id != null && map.TryGetValue(id, out var t) ? t : null;
        }

        /// <summary>Сброс кеша после перезапекания в редакторе.</summary>
        public static void Reload() => map = null;
    }
}
