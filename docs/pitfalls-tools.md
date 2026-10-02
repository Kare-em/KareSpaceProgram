# Грабли: MCP, редактор Unity, правка файлов

Читать перед работой через MCP, отладкой в Play и массовой правкой файлов.

## MCP
- `.mcp.json` → `http://127.0.0.1:8767/mcp`. Car_Train занимает 8765 — порт хранится в EditorPrefs на всю машину,
  поэтому `McpPortPin.cs` перезаписывает его при загрузке редактора.
- Проверка: `curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:8767/mcp` → 406 = сервер жив.
  Если сервер жив, а сессия пишет ECONNREFUSED — переподключить через /mcp (сессия стартовала раньше редактора).
- **Скрин `manage_camera screenshot`** пишет только внутрь проекта: `output_folder: "Temp/Shots"`.
- **Компиляция**: `refresh_unity` compile=request, mode=force, scope=all; бывает «idle» без сборки — проверять появление
  нового типа/поля через `execute_code`, при необходимости повторить.
- **`execute_code` без `action:"execute"` падает**; codedom (C# 6, без `dynamic`) — без `StringBuilder.Append` цепочкой,
  собирать строку через `+`.
- `read_console types` — списком.

## Отладка в Play
- `execute_code` → `Kare.Space.EditorTools.FlightDebug` через рефлексию (`Reentry(alt, speed, γ°)`, `Stage`, `Status`,
  `Lift(alt, up)`); ускорение — `Time.timeScale`; поля `FlightCamera.Yaw/Pitch/Distance` публичные (Yaw от севера).
- **Телепорт борта**: сначала `v.Situation = Flying` (иначе `UpdateLandedPose` вернёт на стол), и пауза
  `manage_editor pause` — переключатель: второй вызов снимает паузу.

## Правка файлов (Windows, Git Bash)
- Длинный Python в heredoc Bash падает — писать скрипт в файл (скретчпад) и запускать `python файл`.
- Замены через Python: `io.open(..., newline='')`, assert на число вхождений. **Концы строк разные**: `.cs` игрового
  слоя и ядра (VesselView, LaunchPadView, VesselDesign, FlightSceneBuilder) — LF, не CRLF; определять по файлу.
- `sed -i '<N>r кусок'` после любой `Edit` того же файла — номер строки уже сдвинут (вставка попала внутрь
  doc-комментария, 02.10.2026). Вставлять Python-заменой по точной строке-якорю.
- `perl -i` на файлах с кириллицей не использовать — портит кодировку.

## Blender MCP (лоу-поли детали)
- Сервер: `.mcp.json` → `uvx --python 3.12 blender-mcp` (ahujasid), аддон `blender_mcp_addon.py` v1.8 в
  `%APPDATA%/Blender Foundation/Blender/5.2/scripts/addons`, Blender 5.2 в `C:/Program Files/Blender Foundation/Blender 5.2`.
  В Blender: N-панель → BlenderMCP → Connect (сокет 9876). Проверка с машины:
  `timeout 40 uvx --offline --python 3.12 blender-mcp < /dev/null` → в логе «Successfully connected to Blender on startup».
- Инструменты появляются только в сессии, начатой ПОСЛЕ правки `.mcp.json` (и после одобрения проектного сервера);
  02.10.2026 сервер и сокет были живы, а в текущей сессии тулов `blender` не было.
- Экспорт: `Assets/_Project/Models/<деталь>.fbx`, метры, Apply Transform; ось ракеты в Blender +Z → в Unity +Y
  (нос борта) — подтверждено на 4 деталях 02.10.2026. Начало модели (днище/верх/шарнир) и габарит — в константах
  `VesselView`/`LaunchPadView` и в Tooltip полей `GameBootstrap`; поменял модель — сверь bounds (`mesh.bounds`).
- **В FBX несколько субмешей** (по материалу Blender): `sharedMaterials` — массив длиной `subMeshCount`,
  иначе рисуется только первый субмеш.
- **`manage_camera screenshot` пишет в `Assets/Screenshots/`** (`output_folder` игнорирует, `screenshot_file_name`
  принимает) — после съёмки файл и `Assets/Screenshots(.meta)` удалить. `camera: "<имя>"` снимает без OnGUI-HUD.
  Своя камера для кадра: клон Main Camera, `LookAt(цель, vessel.up)` — мировой Y в сцене не «верх»
  (плавающее начало), без `up` кадр заваливается.
- **Тест у тела: телепорт ниже ~1 км = удар** — `Teleport(Луна, 200 м)` + отстрел ступеней дал «удар 103 м/с»
  и `Alive = false` (вид перестаёт перестраиваться). Брать 15 км, как чит в Esc.

## Как вызывать UnityMCP, если тулов нет в сессии
- 02.10.2026: после /mcp и одобрения тулы `UnityMCP` в сессии так и не появились, а сервер на 8767 жив.
  Обход — HTTP-клиент JSON-RPC (`initialize` → `notifications/initialized` → `tools/call`, заголовок
  `mcp-session-id`, ответ SSE `data:`); C# — через `execute_code`. Скрипт ~50 строк, писать в скретчпад.
- Правка, упавшая с «файл занят» при открытом Unity, — разовая блокировка импорта: перезапустить оставшиеся замены.
  Python `io.open(p,'w')` при этом даёт `OSError [Errno 22] Invalid argument` (02.10.2026 — дважды подряд на
  GameBootstrap.cs, и с абсолютным, и с относительным путём); тул `Edit` в ту же минуту писал без ошибки — им и править.
- **Установка Blender MCP перезаписала `.mcp.json`** — `UnityMCP` пропал из файла. Вернул руками, но сессия,
  стартовавшая без него, сервер не видит (`session_connectors_status` — только `blender`). Лечится только
  пользователем: /mcp → одобрить UnityMCP, или новая сессия. После установки любого MCP — сверить `.mcp.json`.
