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
  собирать строку через `+`. `Object` там неоднозначен (System/UnityEngine) — писать `UnityEngine.Object`;
  тел списком у `Universe` нет — брать `u.System.Get("moon")`.
- `read_console types` — списком.
- **PNG больше ~1,4 МБ не доходит через `SendUserFile` до удалённого зрителя** (таймаут, 03.10.2026) — в
  десктопе файл виден. Для телефона ужимать (JPEG/половина Full HD).
- **Порт 8767 отвечает 406 и при закрытом редакторе** — это жив Python-сервер `mcp-for-unity.exe`, а не Unity.
  Признак: тулы возвращают `no_unity_session`. Проверять `tasklist | grep Unity.exe`; запуск —
  `"/c/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Unity.exe" -projectPath <проект> &`.
- **Добавление `com.unity.modules.nvidia` не перекомпилировало DLL пакета HDRP** (02.10.2026): в asmdef HDRP
  `ENABLE_NVIDIA_MODULE` уже есть, а `Library/ScriptAssemblies/Unity.RenderPipelines.HighDefinition.Runtime.dll`
  остаётся вчерашним → `DLSSDetected = false` на RTX. Признак: IL `DLSSPass.SetupFeature` = 2 байта (рефлексией,
  `GetMethodBody().GetILAsByteArray()`), в ссылках сборки нет NVIDIA. Лечит
  `CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache)` (~13 с перезагрузки).

## Отладка в Play
- `execute_code` → `Kare.Space.EditorTools.FlightDebug` через рефлексию (`Reentry(alt, speed, γ°)`, `Stage`, `Status`,
  `Lift(alt, up)`); ускорение — `Time.timeScale`; поля `FlightCamera.Yaw/Pitch/Distance` публичные (Yaw от севера).
- **Телепорт борта**: сначала `v.Situation = Flying` (иначе `UpdateLandedPose` вернёт на стол), и пауза
  `manage_editor pause` — переключатель: второй вызов снимает паузу.
- **Кадр любого аппарата без полёта** (03.10.2026): в редакторе `GameBootstrap.MissionId = "<id>"` → play →
  `var u = GameBootstrap.U; u.Teleport(u.System.Get("moon"), 15e3, false);` + `u.Stage()` до нужного набора
  (`u.Active.Attached`), `Throttle = 0`. После stop вернуть `MissionId = "vostok"`. Секции под обтекателем
  (`Vessel.IsEnclosed`) не рисуются — LM на столе не виден, это не баг.

## Правка файлов (Windows, Git Bash)
- Длинный Python в heredoc Bash падает — писать скрипт в файл (скретчпад) и запускать `python файл`.
- Замены через Python: `io.open(..., newline='')`, assert на число вхождений. **Концы строк разные**: `.cs` игрового
  слоя и ядра (VesselView, LaunchPadView, VesselDesign, FlightSceneBuilder) — LF, не CRLF; определять по файлу.
- `sed -i '<N>r кусок'` после любой `Edit` того же файла — номер строки уже сдвинут (вставка попала внутрь
  doc-комментария, 02.10.2026). Вставлять Python-заменой по точной строке-якорю.
- `perl -i` на файлах с кириллицей не использовать — портит кодировку.
- Якорь Python-замены должен быть уникален: одинаковые строки в соседних пресетах (Karman/Freedom 7) дают
  count=2 — брать якорь с соседней уникальной строкой.
- `sed` со вставкой `\r\n` в LF-файл (FlightSceneBuilder) даёт смешанные концы строк — сначала определить EOL файла.

## Blender MCP (лоу-поли детали)
- Сервер: `.mcp.json` → `uvx --python 3.12 blender-mcp` (ahujasid), аддон `blender_mcp_addon.py` v1.8 в
  `%APPDATA%/Blender Foundation/Blender/5.2/scripts/addons`, Blender 5.2 в `C:/Program Files/Blender Foundation/Blender 5.2`.
  В Blender: N-панель → BlenderMCP → Connect (сокет 9876). Проверка с машины:
  `timeout 40 uvx --offline --python 3.12 blender-mcp < /dev/null` → в логе «Successfully connected to Blender on startup».
- Инструменты появляются только в сессии, начатой ПОСЛЕ правки `.mcp.json` (и после одобрения проектного сервера);
  02.10.2026 сервер и сокет были живы, а в текущей сессии тулов `blender` не было.
- Экспорт: `Assets/_Project/Models/<деталь>.fbx`, метры, Apply Transform; ось ракеты в Blender +Z → в Unity +Y
  (нос борта) — подтверждено на 11 деталях 02.10.2026. Параметры `export_scene.fbx`: `bake_space_transform=True,
  apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y', object_types={'MESH'}, use_selection=True`.
  Без них импорт давал fileScale 0,01, поворот 270° и масштаб 100. Начало модели (днище/верх/шарнир) и габарит — в константах
  `VesselView`/`LaunchPadView` и в Tooltip полей `GameBootstrap`; поменял модель — сверь bounds (`mesh.bounds`).
- **В FBX несколько субмешей** (по материалу Blender): `sharedMaterials` — массив длиной `subMeshCount`,
  иначе рисуется только первый субмеш.
- **`manage_camera screenshot` пишет в `Assets/Screenshots/`** (`output_folder` игнорирует, `screenshot_file_name`
  принимает) — после съёмки файл и `Assets/Screenshots(.meta)` удалить. `camera: "<имя>"` снимает без OnGUI-HUD.
  Его «верх» — мировой Y, а местная вертикаль в сцене наклонена (P со SwapYZ + плавающее начало) — кадр завален.
  Рабочий кадр (HDRP, 02.10.2026): клон `boot.Camera`, выключить все MonoBehaviour кроме `HDAdditionalCameraData`,
  удалить AudioListener, `targetTexture` = RT 1920×1080, `enabled = false`; `rotation = LookRotation(цель − поз,
  vessel.up)`, 12× `cam.Render()` (догоняет автоэкспозиция) → `ReadPixels` → PNG в скретчпад. Файлов в Assets нет.
- **Демо-борт рядом с основным** (показ деталей): `new Vessel(VesselPresets.ById("vostok"))` (у `VesselDesign`
  нет `ById`) + обязательно `Body/Position/Attitude` от активного — иначе NRE в `FloatingOrigin.WorldP` при
  `VesselView.Init`; виду `enabled = false`, позицию ставить руками.
- **Тест у тела: телепорт ниже ~1 км = удар** — `Teleport(Луна, 200 м)` + отстрел ступеней дал «удар 103 м/с»
  и `Alive = false` (вид перестаёт перестраиваться). Брать 15 км, как чит в Esc.

## Как вызывать UnityMCP, если тулов нет в сессии
- 02.10.2026: в сессии два набора тулов — `UnityMCP` (заглавные) отвечает `no_unity_session`, рабочий —
  `unityMCP` (строчные, stdio). Ресурсов у него нет (`editor/state` не читается) — состояние узнавать `execute_code`.
  Сразу после `play` — доменная перезагрузка, «No Unity Editor instances found»/таймаут: просто повторить вызов.
- 02.10.2026: после /mcp и одобрения тулы `UnityMCP` в сессии так и не появились, а сервер на 8767 жив.
  Обход — HTTP-клиент JSON-RPC (`initialize` → `notifications/initialized` → `tools/call`, заголовок
  `mcp-session-id`, ответ SSE `data:`); C# — через `execute_code`. Скрипт ~50 строк, писать в скретчпад.
- Правка, упавшая с «файл занят» при открытом Unity, — разовая блокировка импорта: перезапустить оставшиеся замены.
  Python `io.open(p,'w')` при этом даёт `OSError [Errno 22] Invalid argument` (02.10.2026 — дважды подряд на
  GameBootstrap.cs, и с абсолютным, и с относительным путём); тул `Edit` в ту же минуту писал без ошибки — им и править.
- **Установка Blender MCP перезаписала `.mcp.json`** — `UnityMCP` пропал из файла. Вернул руками, но сессия,
  стартовавшая без него, сервер не видит (`session_connectors_status` — только `blender`). Лечится только
  пользователем: /mcp → одобрить UnityMCP, или новая сессия. После установки любого MCP — сверить `.mcp.json`.
