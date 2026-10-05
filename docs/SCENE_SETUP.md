# Flight scene setup — instructions

State as of 01.10.2026: the simulation core (`Assets/_Project/Core`) is done, tests 36/36 green.
The game layer is written, the scene is built by the menu **Kare/Build Flight Scene** (`Editor/FlightSceneBuilder.cs`,
which also creates `Settings/HDRP.asset` and makes it the default pipeline — without it everything is magenta).
Input is the old Input Manager (the Input System package is not in the project), there is no reference to `Unity.InputSystem` in the asmdef.
Below is the original assembly order:
manually (section A) or auto-build from the editor menu, as in Car_Train (section B).

Unity 6000.6.3f1, HDRP 17.6. Open the editor from Hub; MCP for Unity — port **8767**
(`.mcp.json` → `http://127.0.0.1:8767/mcp`, the port is pinned by `Assets/_Project/Editor/McpPortPin.cs`
so as not to conflict with Car_Train on 8765).

---

## 0. What to write before the scene (game layer)

Folder `Assets/_Project/Game`, asmdef `Kare.Space.Game` → references: `Kare.Space.Core`,
`Unity.RenderPipelines.HighDefinition.Runtime`, `Unity.RenderPipelines.Core.Runtime`, `Unity.InputSystem`.

| Script | What it does | GDD |
|---|---|---|
| `GameBootstrap` | creates `Universe`, places "Kara-1" on the Baikonur pad, steps the simulation every frame (`Update` → a step with the current warp), mission tracker | §1.3, §3 |
| `FloatingOrigin` | active vessel = Unity (0,0,0); everything else: `(pos_double − origin_double)` → float. Ecliptic J2000 → Unity conversion via SwapYZ (see the `math` test) | §2.6 |
| `BodyRenderer` | body mesh (first a UV sphere with a procedural texture, then a cube sphere + quadtree), compressing distant bodies into the shell 5e6…9,5e6 m by distance to the horizon | §2.7, §2.8 |
| `SunLight` | Directional Light along the direction to the Sun, 127 000 lx × (1 AU/r)², eclipses by angular discs | §9.1, §9.3 |
| `SkyController` | center/radius of the Physically Based Sky every frame, profile change by SOI, exposure EV [−5, 16] | §9.2, §9.3 |
| `VesselView` | procedural rocket mesh from the sections `Design.Sections` (Length, Diameter), orientation from `Vessel.Attitude` (+Y = nose), flame | §5, §9.5 |
| `FlightCamera` | orbital camera around the vessel (RMB — rotate, wheel — distance) | §10.3 |
| `FlightInput` | keys per §10.1 (Input System) → manual control/throttle/stage/SAS/warp/map | §10.1 |
| `FlightHud` | IMGUI panel per the table in §10.2, font with Cyrillic | §10.2 |
| `MapView` | key M: conics 256 points/orbit, Ap/Pe, SOI changes, no compression §2.7 | §9.6 |

Before writing — read the public Core API (`Flight/Universe.cs`, `Flight/Vessel.cs`,
`Bodies/CelestialBody.cs`, `Bodies/SolarSystem.cs`) and how the world is assembled in
`Tools/CoreTests/Program.cs` (tests `sputnik`, `luna9` — a ready example of "pad → orbit → Moon").

### Core → Unity cheat sheet (checked against the code on 01.10.2026)

```csharp
var def = MissionCatalog.Get("sputnik");                         // karman, sputnik, mechta, vympel, farside, vostok, luna9
var u = MissionTracker.CreateUniverse(def, SolarSystem.CreateReal()); // Universe + Launch(vessel, "baikonur")
var tr = new MissionTracker(def);
u.Message += Debug.Log; tr.Changed += Debug.Log;
// Update(): u.Advance(Time.deltaTime); tr.Update(u);   (realDt is clamped to 0.1 s; warp — u.WarpUp()/WarpDown())
```

- Frames: Core is in **P** (ecliptic J2000, z — pole, right-handed). Unity = P with y↔z swapped: `Vector3d.SwapYZ`,
  `QuaternionD.SwapYZ`. There are no implicit conversions to `Vector3` — `(float)` by hand.
- Vessel position in the world: `Body.Position + Vessel.Position` (P, double) → minus origin → `.SwapYZ` → float.
- `Vessel.Attitude` is **already in U** — straight into `Quaternion`. `CelestialBody.Orientation` is in P — `.SwapYZ` first.
- Rocket stages: `Design.Sections` bottom to top, base heights — `v.Layout(baseHeight)`, those hidden under the fairing —
  `v.IsEnclosed(i)`; the vessel node origin = center of mass (`MassProperties` → `comHeight`). Jettisoned stages are
  new `Vessel`s with `IsDebris` in `u.Vessels`.
- Terrain for the mesh — `body.SurfaceHeight(dirBodyFixed)` (the same function as the physics uses), the pad is leveled.
- Manual control: `Active.PilotInput` (x pitch W=+1, y yaw D=+1, z roll E=+1), `Active.Throttle`,
  `Active.Sas`, `u.Stage()`. Any input switches the autopilots off by itself.
- Autopilots: `u.Ascent = new AscentAutopilot { TargetAltitude = 200000 }`, `u.SetNode(...)` + `u.NodePilot`,
  `u.Landing = new LandingAutopilot(moon)`. Prediction for the map — `u.PredictActive()` (a list of `OrbitPatch`).
- Bodies have no colors/textures in Core — keep visual parameters in Game (a table keyed by `body.Id`).
- Date for the HUD — `GameCalendar.Format(u.Time)`; Ap/Pe — `orbit.ApoapsisRadius − body.Radius`.

---

## A. Manual scene setup

1. **File → New Scene → Basic Indoors (HDRP)**, delete everything except the Global Volume. Save as
   `Assets/_Project/Scenes/Flight.unity`, add it to Build Settings as the first one.
2. **Project Settings → HDRP**:
   - Camera Relative Rendering — on (on by default in HDRP, verify).
   - Default Volume Profile: Physically Based Sky, Visual Environment → Sky type = Physically Based Sky.
   - Lighting → Sky: "Space Emission" = procedural star cubemap (black for now is fine).
   - **PBSky from space:** in a local copy of the package `com.unity.render-pipelines.high-definition-config`
     (`Packages/` — move it from `Library/PackageCache` to `Packages/`) in `ShaderConfig.cs`
     set `PrecomputedAtmosphericAttenuation = 0` and regenerate `ShaderConfig.cs.hlsl`
     (Edit → Rendering → Generate Shader Includes). Without this the Earth's limb from orbit is wrong (§9.2).
3. **Global Volume** (Profile `Assets/_Project/Settings/FlightVolume.asset`):
   - Visual Environment: Sky = Physically Based Sky, Ambient = Dynamic.
   - Physically Based Sky: Type = Earth (Advanced), Planet Radius = 6 378 137, Planet Center —
     set by `SkyController` every frame; Spherical Mode — on.
   - Exposure: Mode = Automatic Histogram, Limit Min −5, Max 16, Adaptation Dark→Light 0.5 s,
     Light→Dark 1.5 s.
   - Tonemapping ACES, Bloom 0.2, Fog — off (the atmosphere is provided by PBSky).
4. **Directional Light "Sun"**: Intensity 127 000 lux, Color Temperature 5778 K, Angular Diameter 0.53°,
   shadows: Cascades 4, Max Distance 2000 m. Attach `SunLight`.
5. **Main Camera**: Near 1, Far 1e7 (§2.7; larger — shadows disappear), FOV 60, Clear = Sky. Attach `FlightCamera`.
   Physical Camera — on (ISO 100, Shutter 1/125, Aperture 16) — exposure is driven by the Volume anyway.
6. Empty object **"Game"**: `GameBootstrap`, `FloatingOrigin`, `FlightInput`, `FlightHud`, `MapView`.
   References in the inspector: Camera, Sun, Global Volume.
7. Empty object **"Bodies"** — `BodyRenderer` creates the children from the `SolarSystem` catalog itself, put nothing
   there by hand (one value — one source: radii and colors come from Core).
8. Play → check: the rocket is on the pad, the Earth is below it, the sky is blue; `Z` + `Space` — launch;
   `T` SAS; `.`/`,` warp; `M` map.

---

## B. Auto-build from the menu (as in Car_Train)

Make an editor script `Assets/_Project/Editor/FlightSceneBuilder.cs` (asmdef
`Kare.Space.Editor`, Editor platform only, references to Game + HDRP), menu
**Kare → Build Flight Scene**. The script repeats section A in code and is idempotent (running it again
recreates the scene rather than duplicating objects):

```csharp
[MenuItem("Kare/Build Flight Scene")]
static void Build()
{
    var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    var profile = CreateOrLoadVolumeProfile("Assets/_Project/Settings/FlightVolume.asset"); // PBSky, Exposure, Tonemapping, Bloom — values from item A3
    var volume = new GameObject("Global Volume").AddComponent<Volume>(); volume.isGlobal = true; volume.sharedProfile = profile;
    var sun = CreateSun();          // item A4: HDAdditionalLightData, 127000 lux, 5778 K, 0.53°
    var cam = CreateCamera();       // item A5: near 1, far 1e7, HDAdditionalCameraData
    var game = new GameObject("Game");
    var boot = game.AddComponent<GameBootstrap>(); boot.Camera = cam; boot.Sun = sun; boot.Volume = volume;
    game.AddComponent<FloatingOrigin>(); game.AddComponent<FlightInput>(); game.AddComponent<FlightHud>(); game.AddComponent<MapView>();
    new GameObject("Bodies").AddComponent<BodyRenderer>();
    EditorSceneManager.SaveScene(scene, "Assets/_Project/Scenes/Flight.unity");
    AddToBuildSettings("Assets/_Project/Scenes/Flight.unity");
}
```

Run: manually from the menu, via MCP (`execute_menu_item` "Kare/Build Flight Scene") or batch:

```bash
"/c/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Unity.exe" -batchmode -quit -projectPath "C:/CocosGames/KareSpaceProgram" -executeMethod Kare.Space.EditorTools.FlightSceneBuilder.Build -logFile -
```

(batch — only with the editor closed: one project cannot be opened twice.)

Check after the build via MCP: `read_console` with no errors → Play → Game view screenshot.
### What to take from Car_Train (`C:\CocosGames\Car_Train_Simulator`, also 6000.6.3f1)

| File | Why |
|---|---|
| `Assets/Editor/HdrpSetup.cs` (menu "PDD/Рендер/Настроить HDRP (всё)", ~630 lines) | a template for configuring an HDRP profile from code (`ConfigureDefaultProfile` ~line 200): how to add overrides to a VolumeProfile and save the asset |
| `Assets/Editor/CityLighting.cs` | sun, shadows, post Volume in the scene from code |
| `Assets/Editor/BootSceneBuilder.cs` | a sample of "the scene is built from the menu" |
| `Assets/Scripts/Game/Rendering/HdrpRuntime.cs` | pattern: a global Volume with high priority on top of the base profile at runtime + the sun's `HDAdditionalLightData` |
| `Assets/Scripts/Game/Car/PlayerInputReader.cs` | Input System without .inputactions — `InputAction` with `AddBinding("<Keyboard>/w")` in code |
| `Assets/Scripts/Game/Debug/DebugOverlay.cs` | IMGUI HUD (the basis for `FlightHud`) |
| `Assets/Shaders/Resources/PddVfxParticle.shader` + `CarVfx.cs` | textureless particles — a template for smoke/flame |

Differences: Car_Train has an HDRI sky and fixed exposure, dual URP/HDRP via `#if PDD_HDRP`.
We have HDRP only, Physically Based Sky and auto exposure (§9.2–9.3): do not copy profiles from there,
take only code patterns. It has no ready textures for stars/planets/regolith.

---

## Core check without Unity

```bash
D="/c/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Data/DotNetSdk/dotnet.exe"; P=/c/CocosGames/KareSpaceProgram/Tools/CoreTests/CoreTests.csproj; "$D" build $P -nologo -v q && "$D" run --no-build --project $P
```

With no argument — all tests (~30 s), with an argument — one (`-- luna9`). Do not build while a run is in progress
(the exe is locked). Expected: "Total: 36 ok, 0 fail".
