using UnityEngine;

namespace Kare.Space.Game
{
    /// <summary>
    /// Перетаскиваемые панели HUD (§10.2): как навбол — левой кнопкой (камера её не занимает), двойной щелчок —
    /// на место по умолчанию. Сдвиг от места по умолчанию в единицах HUD (BaseHeight) живёт в PlayerPrefs, поэтому
    /// панель остаётся на месте после перезапуска и при другом разрешении экрана.
    /// </summary>
    public sealed partial class FlightHud
    {
        sealed class HudDrag
        {
            public readonly string KeyX, KeyY;
            public Vector2 Offset = new Vector2(float.NaN, 0), Grab;
            public bool Active;
            public HudDrag(string key) { KeyX = key + ".x"; KeyY = key + ".y"; }

            public void Save()
            {
                PlayerPrefs.SetFloat(KeyX, Offset.x);
                PlayerPrefs.SetFloat(KeyY, Offset.y);
            }
        }

        /// <summary>Панель высоты и скорости. Ключ «2»: место по умолчанию переехало вниз-влево → наверх по центру (04.10),
        /// а сдвиг хранится от места по умолчанию — старый сдвиг унёс бы панель с экрана.</summary>
        readonly HudDrag flightDrag = new HudDrag("hud.flight2");

        /// <summary>
        /// Место панели с учётом сдвига; ловит перетаскивание. <paramref name="hole"/> — кнопка внутри панели
        /// (в координатах панели), щелчок по ней перетаскиванием не считается. Панель целиком остаётся на экране.
        /// </summary>
        Rect Draggable(HudDrag d, Rect def, float w, float h, Rect hole = default)
        {
            if (float.IsNaN(d.Offset.x)) d.Offset = new Vector2(PlayerPrefs.GetFloat(d.KeyX, 0), PlayerPrefs.GetFloat(d.KeyY, 0));
            var r = new Rect(def.position + d.Offset, def.size);
            var e = Event.current;
            if (d.Active && !Input.GetMouseButton(0)) { d.Active = false; d.Save(); }
            var inHole = hole.width > 0 && new Rect(r.position + hole.position, hole.size).Contains(e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition) && !inHole)
            {
                if (e.clickCount == 2) { d.Offset = Vector2.zero; d.Save(); r.position = def.position; }
                else { d.Active = true; d.Grab = e.mousePosition - r.position; }
                e.Use();
            }
            else if (d.Active && e.type == EventType.MouseDrag)
            {
                r.position = e.mousePosition - d.Grab;
                e.Use();
            }
            else if (d.Active && e.type == EventType.MouseUp && e.button == 0) { d.Active = false; d.Save(); e.Use(); }
            r.x = Mathf.Clamp(r.x, 0, Mathf.Max(0, w - r.width));
            r.y = Mathf.Clamp(r.y, 0, Mathf.Max(0, h - r.height));
            d.Offset = r.position - def.position;
            return r;
        }
    }
}
