# Kare Space Program — правила проекта

KSP-подобная игра, реальная Солнечная система 1:1, реалистичная графика. **Unity 6000.6.3f1 + HDRP 17.6**, ПК.
Общие правила — `~/.claude/CLAUDE.md` (Cocos-скилы тут не применимы, это Unity).

## Документы (читать по требованию)
| Файл | Когда |
|---|---|
| `docs/GDD.md` | дизайн-документ; ссылаться на §-пункты в комментариях кода |
| `docs/SCENE_SETUP.md` | сборка сцены Flight (вручную / автосборкой), шпаргалка по API ядра |
| `docs/STATE.md` | что уже реализовано, детально; клавиши манёвров |
| `docs/pitfalls-render.md` | правка рендера/HDRP/неба/материалов/факела/патча |
| `docs/pitfalls-core.md` | правка ядра, физики, автопилотов, ступеней |
| `docs/pitfalls-tools.md` | MCP, отладка в Play, правка файлов на Windows |

Нашёл новую неочевидную грабли — дописать в подходящий `docs/pitfalls-*.md` сразу (с числом/признаком/решением).

## Структура
```
Assets/_Project/Core    ядро симуляции, чистый .NET (asmdef noEngineReferences), namespace Kare.Space.Core
Assets/_Project/Game    игровой слой: Bootstrap, FloatingOrigin, BodyRenderer, Sky, Sun, VesselView, камера, ввод, HUD, карта
Assets/_Project/Editor  McpPortPin.cs (порт MCP 8767), FlightSceneBuilder.cs (меню Kare/Build Flight Scene), FlightDebug.cs
Assets/_Project/Settings HDRP.asset, FlightVolume.asset, материалы — пересоздаёт FlightSceneBuilder
Tools/CoreTests         headless-тесты ядра (csproj подхватывает Core/**/*.cs)
```
Library/, Temp/, Logs/, UserSettings/ — не трогать и не коммитить.

## Состояние (03.10.2026)
- Ядро (M0) готово, 24 тест-группы ✅: «Луна-9» 3,3 м/с, moondrop, juno1, freedom7, friendship7, gemini3, ranger7,
  surveyor1, luna17 (луноход), apollo8, apollo11. Пресеты — `Core/Vessels/HistoricRockets.cs` (до «Аполлона», без «Артемиды»).
- Игровой слой написан; сцена `Scenes/Flight.unity` собирается меню **Kare/Build Flight Scene** (идемпотентно).
  Миссия по умолчанию — `GameBootstrap.MissionId = "vostok"`. Ввод — старый Input Manager.
- Есть: HUD v2, стол с фермами, грунт и вода патча, биомы Земли, облака, парашют, меню Esc, манёвры (N/B/C…),
  аэромомент и гибель экипажа, навбол + кнопки SAS, читы и выбор миссии в Esc, G — посадка на теле без атмосферы,
  шлейф выхлопа, текстуры грунта/обшивки, FBX-детали из Blender (СА и приборный отсек «Востока», ПС-1, Е-6, РД-107,
  РД-0110, ферма разделения, створки обтекателя, хвост Г-1, опоры, фермы стола), блик Солнца, ночной свет,
  RT-тени + DLSS, стыковка (V), исторические стартовые столы, яркость и перезапуски в Esc.
- Автопилот всей миссии (Y, сам ведёт ускорение), «к Луне» (R), манёвр к цели с авторасчётом (P), тутор после
  выведения (`MissionGuide`), орбиты тел и кораблей на карте, звёзды за экспозицией.
- Конструктор ракет (M3): сцена `Scenes/Hangar.unity` (меню **Kare/Build Hangar Scene**), `PartCatalog`/`Craft` в ядре,
  Esc → «В конструктор»; боковые блоки отделяются отдельными бортами, столкновения бортов.
- Луна LROC 8k, «Луна-17» через окололунную орбиту и съезд по трапам, подвеска, Δv ступеней в HUD, сдвиг камеры колесом.
- Колёса лунохода крутятся (`Lunokhod_Wheels.fbx`). Карты планет Solar System Scope (CC BY 4.0, титр в Esc), Земля 8k
  с огнями и облаками, кольца Сатурна. Спутник/Восток/Луна-9 — настоящая Р-7 с 4 боковыми блоками (радиальная группа);
  «Кара-1» — только эталон. Аэродинамика пакета — `FlightPhysics.AeroAreas` (корпус + боковые блоки, не по maxR).
- G — раскладное (опоры, трапы КТ, крышка лунохода); HUD: скорость орб./пов. щелчком, навбол перетаскивается.
- Клавиши: **G — раскладное** (опоры, трапы КТ; LM/«Сервейор» — пиро, только выпуск), **H — автопилот** взлёта/посадки,
  F1 — детали HUD. Опоры и трапы — отдельные FBX на шарнирах (`VesselView.DeployHinge`), посадка на сложенных > 2 м/с — гибель.
  Подробности — `docs/STATE.md`.
- Р-7: блок А «бутылкой», боковые — косые конусы (`ProcMesh.R7Body`). Процедурный звук `FlightAudio` по состоянию
  бортов (двигатели, ветер, плазма, пиро, касание, взрыв), громкость в Esc. Подробности — `docs/STATE.md`.
- Эффекты (`BlastEffects`): взрыв гибели (шар кодом + JMO WarFX на своём HDRP-шейдере), обломки, горение Vefects на месте
  падения, хлопок отделения, лунная пыль под струёй.

## Тесты ядра
```bash
D="/c/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Data/DotNetSdk/dotnet.exe"; P=/c/CocosGames/KareSpaceProgram/Tools/CoreTests/CoreTests.csproj; "$D" build $P -nologo -v q && "$D" run --no-build --project $P
```
`-- <имя>` — один тест (math, orbit, moon, atmo, stats, stability, separation, craft, collide, craft_fly, karman, sputnik, mechta, vympel, farside, vostok, luna9, moondrop,
juno1, freedom7, friendship7, gemini3, ranger7, surveyor1, luna17, apollo8, apollo11, planets, autoplan; `auto` — все миссии
без рук, `auto_<миссия>` — одна).
Не собирать во время прогона — exe заблокирован, сборка падает.

## MCP
MCP for Unity (CoplayDev), порт 8767 (`McpPortPin.cs`). Проверка: `curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:8767/mcp` → 406 = жив.
ECONNREFUSED при живом сервере — переподключить через /mcp. Детали и грабли — `docs/pitfalls-tools.md`.
Blender MCP (`.mcp.json`, сокет 9876) — лоу-поли детали, экспорт FBX в `Assets/_Project/Models`; см. `docs/pitfalls-tools.md`.

## Соглашения
- Кадр P = эклиптика J2000 (правая, z — полюс); Unity = P с SwapYZ. Нос борта = +Y.
- `Vessel.Position` — относительно центра `Body`, невращающиеся оси (и это ЦМ части). `body.OrientationAt(t) * forward` = полюс.
- `Throttle <= 0` глушит двигатель; повторный запуск тратит зажигание и может требовать ullage.
- Автопилоты работают только в физике; рельсы режет `Universe.WarpLimitTime`.
- Комментарии по-русски, «почему», со ссылкой на §GDD. Числа — константами с парой-зависимостью в комментарии.
- Алиас `using Terrain = Kare.Space.Core.Terrain;` в игровом слое (конфликт с `UnityEngine.Terrain`).
- Светящееся (факел) — только эмиссией; общий `MaterialPropertyBlock` — `Clear()` перед каждым рендерером.
- Рантайм-материалы HDRP не валидируются — ключи (`_EMISSIVE_COLOR_MAP`, `_DETAIL_MAP`) ставить руками.
- Дальняя плоскость камеры near 1 / far 1e7, тела в «оболочке» (иначе пропадают тени) — `docs/pitfalls-render.md`.
- Правка файлов: Python скриптом из файла, `newline=''` (CRLF); `perl -i` на кириллице не использовать.
- Текстуры генерить атласами; скачивание текстур NASA — только с явного согласия пользователя.
