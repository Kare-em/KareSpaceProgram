# Сборка сцены Flight — инструкция

Состояние на 01.10.2026: ядро симуляции (`Assets/_Project/Core`) готово, тесты 36/36 зелёные.
Игровой слой написан, сцену собирает меню **Kare/Build Flight Scene** (`Editor/FlightSceneBuilder.cs`,
заодно создаёт `Settings/HDRP.asset` и ставит его пайплайном по умолчанию — без него всё пурпурное).
Ввод — старый Input Manager (пакета Input System в проекте нет), ссылки на `Unity.InputSystem` в asmdef нет.
Ниже исходный порядок сборки:
вручную (раздел А) или автосборкой из меню редактора, как в Car_Train (раздел Б).

Unity 6000.6.3f1, HDRP 17.6. Редактор открывать из Hub; MCP for Unity — порт **8767**
(`.mcp.json` → `http://127.0.0.1:8767/mcp`, порт закреплён `Assets/_Project/Editor/McpPortPin.cs`,
чтобы не конфликтовать с Car_Train на 8765).

---

## 0. Что нужно написать до сцены (игровой слой)

Папка `Assets/_Project/Game`, asmdef `Kare.Space.Game` → ссылки: `Kare.Space.Core`,
`Unity.RenderPipelines.HighDefinition.Runtime`, `Unity.RenderPipelines.Core.Runtime`, `Unity.InputSystem`.

| Скрипт | Что делает | GDD |
|---|---|---|
| `GameBootstrap` | создаёт `Universe`, ставит «Кара-1» на площадку Байконура, каждый кадр шагает симуляцию (`Update` → шаг с текущим warp), трекер миссий | §1.3, §3 |
| `FloatingOrigin` | активный борт = (0,0,0) Unity; всё остальное: `(pos_double − origin_double)` → float. Переход эклиптика J2000 → Unity через SwapYZ (см. тест `math`) | §2.6 |
| `BodyRenderer` | меш тела (сначала UV-сфера с процедурной текстурой, потом кубосфера + квадродерево), сжатие дальних тел в оболочку 5e6…9,5e6 м по расстоянию до горизонта | §2.7, §2.8 |
| `SunLight` | Directional Light по направлению на Солнце, 127 000 лк × (1 а.е./r)², затмения по угловым дискам | §9.1, §9.3 |
| `SkyController` | центр/радиус Physically Based Sky каждый кадр, смена профиля по SOI, экспозиция EV [−5, 16] | §9.2, §9.3 |
| `VesselView` | процедурный меш ракеты из секций `Design.Sections` (Length, Diameter), ориентация из `Vessel.Attitude` (+Y = нос), факел | §5, §9.5 |
| `FlightCamera` | орбитальная камера вокруг борта (ПКМ — вращение, колесо — дистанция) | §10.3 |
| `FlightInput` | клавиши по §10.1 (Input System) → ручное управление/дроссель/ступень/SAS/warp/карта | §10.1 |
| `FlightHud` | IMGUI-панель по таблице §10.2, шрифт с кириллицей | §10.2 |
| `MapView` | клавиша M: коники 256 точек/виток, Ap/Pe, смены SOI, без сжатия §2.7 | §9.6 |

Перед написанием — прочитать публичное API Core (`Flight/Universe.cs`, `Flight/Vessel.cs`,
`Bodies/CelestialBody.cs`, `Bodies/SolarSystem.cs`) и как мир собирается в
`Tools/CoreTests/Program.cs` (тесты `sputnik`, `luna9` — готовый пример «площадка → орбита → Луна»).

### Шпаргалка Core → Unity (проверено по коду 01.10.2026)

```csharp
var def = MissionCatalog.Get("sputnik");                         // karman, sputnik, mechta, vympel, farside, vostok, luna9
var u = MissionTracker.CreateUniverse(def, SolarSystem.CreateReal()); // Universe + Launch(борт, "baikonur")
var tr = new MissionTracker(def);
u.Message += Debug.Log; tr.Changed += Debug.Log;
// Update(): u.Advance(Time.deltaTime); tr.Update(u);   (realDt режется до 0.1 с; warp — u.WarpUp()/WarpDown())
```

- Кадры: Core в **P** (эклиптика J2000, z — полюс, правая). Unity = P с перестановкой y↔z: `Vector3d.SwapYZ`,
  `QuaternionD.SwapYZ`. Неявных преобразований в `Vector3` нет — `(float)` руками.
- Положение борта в мире: `Body.Position + Vessel.Position` (P, double) → минус origin → `.SwapYZ` → float.
- `Vessel.Attitude` **уже в U** — в `Quaternion` напрямую. `CelestialBody.Orientation` в P — сначала `.SwapYZ`.
- Ступени ракеты: `Design.Sections` снизу вверх, высоты баз — `v.Layout(baseHeight)`, скрытые под обтекателем —
  `v.IsEnclosed(i)`; начало ноды борта = центр масс (`MassProperties` → `comHeight`). Отделённые ступени —
  новые `Vessel` с `IsDebris` в `u.Vessels`.
- Рельеф для меша — `body.SurfaceHeight(dirBodyFixed)` (та же функция, что у физики), площадка выровнена.
- Ручное управление: `Active.PilotInput` (x тангаж W=+1, y рыскание D=+1, z крен E=+1), `Active.Throttle`,
  `Active.Sas`, `u.Stage()`. Любой ввод сам выключает автопилоты.
- Автопилоты: `u.Ascent = new AscentAutopilot { TargetAltitude = 200000 }`, `u.SetNode(...)` + `u.NodePilot`,
  `u.Landing = new LandingAutopilot(moon)`. Прогноз для карты — `u.PredictActive()` (список `OrbitPatch`).
- Цветов/текстур у тел в Core нет — визуальные параметры держать в Game (таблица по `body.Id`).
- Дата для HUD — `GameCalendar.Format(u.Time)`; Ap/Pe — `orbit.ApoapsisRadius − body.Radius`.

---

## А. Ручная сборка сцены

1. **File → New Scene → Basic Indoors (HDRP)** удалить всё, кроме Global Volume. Сохранить как
   `Assets/_Project/Scenes/Flight.unity`, добавить в Build Settings первой.
2. **Project Settings → HDRP**:
   - Camera Relative Rendering — вкл. (по умолчанию в HDRP вкл., проверить).
   - Default Volume Profile: Physically Based Sky, Visual Environment → Sky type = Physically Based Sky.
   - Lighting → Sky: «Space Emission» = процедурный кубмап звёзд (пока можно чёрный).
   - **PBSky из космоса:** в локальной копии пакета `com.unity.render-pipelines.high-definition-config`
     (`Packages/` — перенести из `Library/PackageCache` в `Packages/`) в `ShaderConfig.cs`
     выставить `PrecomputedAtmosphericAttenuation = 0` и пересгенерировать `ShaderConfig.cs.hlsl`
     (Edit → Rendering → Generate Shader Includes). Без этого кромка Земли с орбиты неверная (§9.2).
3. **Global Volume** (Profile `Assets/_Project/Settings/FlightVolume.asset`):
   - Visual Environment: Sky = Physically Based Sky, Ambient = Dynamic.
   - Physically Based Sky: Type = Earth (Advanced), Planet Radius = 6 378 137, Planet Center —
     задаёт `SkyController` каждый кадр; Spherical Mode — вкл.
   - Exposure: Mode = Automatic Histogram, Limit Min −5, Max 16, Adaptation Dark→Light 0.5 с,
     Light→Dark 1.5 с.
   - Tonemapping ACES, Bloom 0.2, Fog — выкл. (атмосферу даёт PBSky).
4. **Directional Light «Sun»**: Intensity 127 000 lux, Color Temperature 5778 K, Angular Diameter 0.53°,
   тени: Cascades 4, Max Distance 2000 м. Повесить `SunLight`.
5. **Main Camera**: Near 1, Far 1e7 (§2.7; больше — пропадают тени), FOV 60, Clear = Sky. Повесить `FlightCamera`.
   Physical Camera — вкл. (ISO 100, Shutter 1/125, Aperture 16) — экспозицию всё равно ведёт Volume.
6. Пустой объект **«Game»**: `GameBootstrap`, `FloatingOrigin`, `FlightInput`, `FlightHud`, `MapView`.
   Ссылки в инспекторе: Camera, Sun, Global Volume.
7. Пустой объект **«Bodies»** — `BodyRenderer` создаёт детей по каталогу `SolarSystem` сам, руками ничего
   не класть (одна величина — один источник: радиусы и цвета берутся из Core).
8. Play → проверить: ракета на площадке, Земля под ней, небо голубое; `Z` + `Пробел` — старт;
   `T` SAS; `.`/`,` warp; `M` карта.

---

## Б. Автосборка из меню (как в Car_Train)

Сделать редакторский скрипт `Assets/_Project/Editor/FlightSceneBuilder.cs` (asmdef
`Kare.Space.Editor`, только Editor-платформа, ссылки на Game + HDRP), меню
**Kare → Build Flight Scene**. Скрипт повторяет раздел А кодом и идемпотентен (запуск повторно
пересоздаёт сцену, а не дублирует объекты):

```csharp
[MenuItem("Kare/Build Flight Scene")]
static void Build()
{
    var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    var profile = CreateOrLoadVolumeProfile("Assets/_Project/Settings/FlightVolume.asset"); // PBSky, Exposure, Tonemapping, Bloom — значения из п. А3
    var volume = new GameObject("Global Volume").AddComponent<Volume>(); volume.isGlobal = true; volume.sharedProfile = profile;
    var sun = CreateSun();          // п. А4: HDAdditionalLightData, 127000 lux, 5778 K, 0.53°
    var cam = CreateCamera();       // п. А5: near 1, far 1e7, HDAdditionalCameraData
    var game = new GameObject("Game");
    var boot = game.AddComponent<GameBootstrap>(); boot.Camera = cam; boot.Sun = sun; boot.Volume = volume;
    game.AddComponent<FloatingOrigin>(); game.AddComponent<FlightInput>(); game.AddComponent<FlightHud>(); game.AddComponent<MapView>();
    new GameObject("Bodies").AddComponent<BodyRenderer>();
    EditorSceneManager.SaveScene(scene, "Assets/_Project/Scenes/Flight.unity");
    AddToBuildSettings("Assets/_Project/Scenes/Flight.unity");
}
```

Запуск: вручную из меню, через MCP (`execute_menu_item` «Kare/Build Flight Scene») или batch:

```bash
"/c/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Unity.exe" -batchmode -quit -projectPath "C:/CocosGames/KareSpaceProgram" -executeMethod Kare.Space.EditorTools.FlightSceneBuilder.Build -logFile -
```

(batch — только при закрытом редакторе: один проект нельзя открыть дважды.)

Проверка после сборки через MCP: `read_console` без ошибок → Play → скриншот Game view.
### Что взять из Car_Train (`C:\CocosGames\Car_Train_Simulator`, тоже 6000.6.3f1)

| Файл | Зачем |
|---|---|
| `Assets/Editor/HdrpSetup.cs` (меню «PDD/Рендер/Настроить HDRP (всё)», ~630 строк) | шаблон настройки HDRP-профиля из кода (`ConfigureDefaultProfile` ~стр. 200): как добавлять override'ы в VolumeProfile и сохранять ассет |
| `Assets/Editor/CityLighting.cs` | солнце, тени, пост-Volume в сцене из кода |
| `Assets/Editor/BootSceneBuilder.cs` | образец «сцена собирается из меню» |
| `Assets/Scripts/Game/Rendering/HdrpRuntime.cs` | паттерн: глобальный Volume с высоким приоритетом поверх базового профиля в рантайме + `HDAdditionalLightData` солнца |
| `Assets/Scripts/Game/Car/PlayerInputReader.cs` | Input System без .inputactions — `InputAction` с `AddBinding("<Keyboard>/w")` в коде |
| `Assets/Scripts/Game/Debug/DebugOverlay.cs` | IMGUI-HUD (основа для `FlightHud`) |
| `Assets/Shaders/Resources/PddVfxParticle.shader` + `CarVfx.cs` | частицы без текстур — шаблон дыма/факела |

Отличия: в Car_Train небо HDRI и фиксированная экспозиция, двойной URP/HDRP через `#if PDD_HDRP`.
У нас — только HDRP, Physically Based Sky и автоэкспозиция (§9.2–9.3): профили оттуда не копировать,
брать только код-паттерны. Готовых текстур звёзд/планет/реголита там нет.

---

## Проверка ядра без Unity

```bash
D="/c/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Data/DotNetSdk/dotnet.exe"; P=/c/CocosGames/KareSpaceProgram/Tools/CoreTests/CoreTests.csproj; "$D" build $P -nologo -v q && "$D" run --no-build --project $P
```

Без аргумента — все тесты (~30 с), с аргументом — один (`-- luna9`). Не собирать, пока идёт прогон
(exe заблокирован). Ожидается «Итого: 36 ok, 0 fail».
