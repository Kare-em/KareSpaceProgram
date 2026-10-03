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
- **Смена миссии в Play** (03.10.2026): `GameBootstrap.NextMissionId = "apollo11"; SceneManager.LoadScene(0)` —
  `execute_code` отвечает таймаутом, но сцена грузится; проверять следующим вызовом. Правка `.cs` во время Play →
  перезагрузка домена → поток NRE из `GameBootstrap` и `no_unity_session`: остановить `manage_editor stop`, очистить консоль.

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
- 03.10.2026: редактор в HTTP-режиме — stdio-тулы `unityMCP` отвечают «No Unity Editor instances found» (порт 6400 не
  слушается, это нормально), а HTTP-коннектор сессии — ECONNREFUSED, если стартовал раньше сервера. Рабочий путь —
  HTTP JSON-RPC клиент (`python mcp.py <тул> <json|файл.json>` в скретчпаде: initialize → initialized → tools/call,
  `mcp-session-id`, SSE `data:`). Аргументы с C#-кодом — файлом JSON, иначе кавычки ломаются в bash.
- `RenderProbe.Shot` (cam.Render в RT) в Play даёт чёрный кадр и EV NaN — снимать `manage_camera screenshot`
  (`output_folder: Temp/Shots`), яркость считать PIL по уменьшенной копии.
- Ночь для замеров: Play → `u.SetWarp(6)` на столе (×10⁴) → ждать `u.Time` +50 000 с (≈02:30 местного на Байконуре) → `SetWarp(0)`.
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
- **FBX из Blender → Unity: оси (−x, z, −y)** (03.10.2026 замер `mesh.bounds` у Pad_Atlas): модель повёрнута на 180° вокруг Y.
  Чтобы Blender-x остался востоком, а y — севером в базисе стола, у дочернего узла `localRotation = Euler(0,180,0)`;
  тогда Blender (x,y,z) → стол (x, z, y). Масштаб по длине (Blender y) = `localScale.z`.
- **`remove_doubles` склеивает заглушки слотов материалов**: неиспользуемый слот FBX Unity схлопывает, субмеши
  съезжают. Заглушки каждого слота — в разных местах; проверка после сборки: `slots == [0,1,2,3]` у всех Pad_*.
- **`get_viewport_screenshot` в Blender отдаёт старый кадр после смены вида через bpy**; надёжно — рендер Workbench
  из служебной камеры. `hide_set` скрывает только во вьюпорте, для рендера ещё `hide_render = True`.
- Namespace `execute_blender_code` между вызовами не сохраняется — в каждом вызове `exec(open(...).read())`.
- **`PlayerPrefs` в инициализаторе поля MonoBehaviour** → UnityException (вызов из конструктора). Читать лениво
  (`DetailSettings.Level`) или в `Awake`; в `MapView` буфер пустой и ресайзится в `Draw`.
- **`Destroy` чужой текстуры** («Destroying object UnityWhite is not allowed»): при перестройке LOD освобождать только своё —
  `BodyRenderer.Own/Free` с HashSet, плейсхолдер `Texture2D.whiteTexture` и ассеты не трогать.
- **Долгий `execute_code` (генерация уровня «Ультра» ~70 с) отваливается по таймауту MCP**, а Unity доделывает работу.
  Результат — `Debug.Log` и потом `read_console`, а не return.

- **Перекомпиляция во время Play → NRE каждый кадр** (`GameBootstrap.Update`): перезагрузка домена обнуляет несериализуемые
  поля, `Awake` не повторяется. В `Update` стоит проверка с предупреждением; в EditorPrefs `ScriptCompilationDuringPlay` = 1
  («перекомпилировать после выхода из Play», было −1). Перед правкой кода — `manage_editor stop`.
- **Bash `cat > файл` без heredoc/ввода висит вечно**, ожидая stdin (повис запуск редактора). Всем командам MCP/Blender —
  `< /dev/null` и `timeout`.
- **Blender без MCP-аддона**: `"/c/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b Tools/blender/parts.blend
  --python скрипт.py < /dev/null`; экспорт — `hulls_lib.export(name)` (sys.path на `Tools/blender`), перед сохранением
  `preferences.filepaths.save_version = 0`, иначе рядом появляется `parts.blend1`.

## Blender без MCP-вывода (03.10.2026)
- **`execute_blender_code` выполняет, но `print` не возвращает** — проверять геометрию нечем. Рабочий путь — headless:
  `"/c/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b Tools/blender/parts.blend --python script.py < /dev/null`
  и grep по префиксу в print. Экспорт — `hulls_lib.export(name)` (добавить `Tools/blender` в `sys.path`), перед сохранением
  `preferences.filepaths.save_version = 0` (иначе плодится `.blend1`), после — удалить `Tools/blender/__pycache__`.
- **`o.dimensions` после `bm.to_mesh` в фоне не обновляется** — габарит проверять по вершинам или реимпортом FBX.

## Раскладное: опоры и трапы отдельными FBX (03.10.2026)
- **Опоры «Сервейора»/LM и трапы КТ вынесены из корпусов** скриптом `Tools/blender/split_deploy.py` (разовый: повторно
  на уже разрезанном parts.blend деталей не найдёт). Острова меша → группа по ближайшему азимуту опоры → объект
  `<Корпус>_Leg_<k>` / `Luna17_Ramp_<k>` в координатах корпуса; FBX `Surveyor_Legs`, `LM_Legs`, `Luna17_Ramps` — по
  объекту на опору. Итог разреза: Surveyor 3×124 верш., LM 4 опоры (az 90 — 570 верш. с лестницей, прочие 314), КТ 2×72.
- **У LM площадка у люка (z > 3,05) — тоже остров с r > 2,4**: без отсечки по высоте уезжает в опору. Лестница — на
  передней опоре (az 90), поэтому опоры не клонировать с одной, а резать каждую.
- **Трапы КТ резали колёса лунохода**: в модели «Лунохода» база была 2,2 м (колёса до ±1,36), а сложенный трап
  стоит на r 1,17–1,2. Колёса пересажены на реальную базу 1,7 м (оси ±0,2833/±0,85, край ±1,105 — пара
  `FlightPhysics.RoverHalfBase` 0,85), трап сложен на 116° вместо 120° (верх на 4° наружу, r ≈ 1,4 < обтекатель 2,05).
- **Крышка лунохода — отдельный FBX** `Lunokhod_Lid` (`Tools/blender/lunokhod_lid.py`, разовый: на уже разрезанном
  parts.blend крышку не найдёт): острова z ≥ 1,33 / уходящие вперёд y < −0,85. Модель — в открытом положении,
  шарнир r 0,8 h 1,4, закрыта при 162° (`VesselView.DeployHinge`). Антенны, торчавшие над крышкой, сдвинуты к
  переду (y > 0,64), иначе закрытая крышка шла сквозь них.
- **Направление шарнира — по стопе, не по центру детали**: подкосы «Сервейора» уводят центр bounds на ~10° от оси опоры.
  FlightSceneBuilder берёт центр вершин в полосе 0,3 м над низом детали (`DeployFootBand`).
- **Шарниры (`VesselView.DeployHinge`) — пара с parts.blend**: радиус/высота оси = верх стойки (Surveyor 0,6/0,9, LM
  2,15/3,0, кромка настила КТ 1,2/1,9; меши: Surveyor y≤0,93, LM r≥2,07 y≤3,04, трап z≥1,17 y≤1,94). Правишь модель —
  сверяй. Слоты материалов детали ищутся по имени материала в корпусе.
- **`execute_code` через mcp.py: экранированный перевод строки (обратный слэш + n) в C#-строке приходит настоящим
  переводом** → «Newline in constant». Разделители в выводе — `" ### "`.

## Колёса лунохода и Play (03.10.2026)
- **Колёса — `Tools/blender/lunokhod_wheels.py`** (разовый, после `lunokhod_lid.py`): острова меша в габарите колеса
  (|x| 0,70–0,90, z 0–0,51, оси y ±0,283/±0,845) → `Lunokhod_Wheel_0..7`. Пивот — центр bounds меша
  (`FlightSceneBuilder.DeployParts(wheels: true)`), вид крутит узел вокруг X модели.
- **Пока пользователь в Play, компиляция отложена**: `Kare/Build Flight Scene` падает «cannot be used during play mode», а
  `execute_code` не видит новых членов (`WheelsFor`). Play не останавливать — дождаться выхода, потом собрать сцену.
