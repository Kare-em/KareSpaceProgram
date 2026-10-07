# Project state (detailed, as of 06.10.2026)

The short version is in `CLAUDE.md`. Here: what is already done.

## Core (M0)
Ephemerides, Kepler, patched conics, US Standard Atmosphere 1976, RK4, stages, engines (starts, ullage), autopilots for ascent /
node execution / landing, launch windows, transfer planner, missions. Tests ✅ (24 groups) including the full "Luna-9" run:
launch → orbit → transfer → soft landing at 3.3 m/s.

## Game layer
- Scene `Scenes/Flight.unity` is built by the menu **Kare/Build Flight Scene** (idempotent). Default mission —
  `GameBootstrap.MissionId = "vostok"`. Input — the old Input Manager (no Input System).
- HUD v2: icons, stage stack, autopilot mode; H — detailed telemetry.
- Launch table with trusses (`LaunchPadView`, retracts on liftoff), patch ground (tile in metres + detail map),
  camera frames the vessel with its plume.
- Earth: `EarthSurface` biomes (temperature + humidity), ocean glint via mask, clouds as a separate sphere at +8 km, up close —
  patch water (Car_Train normal, ripple drift) and seabed down to −200 m. Startup: Earth texture 2048 ≈ 2.3 s, clouds ≈ 0.45 s.
- Parachute in `VesselView`, Esc menu (`PauseMenu`), on the map labels behind the planet are semi-transparent (`MapView.Occluded`).
- Mission selection (05.10.2026): Esc → "select from table…" — `MissionPicker` (date, mission, country, vehicle, target, brief;
  sorted by launch date, click — restart). "?" — hint: a paragraph from `Core/Missions/MissionInfo.cs` (`MissionInfoCatalog`)
  and a not-to-scale trajectory diagram `MissionSketch` (Texture2D, diagram type and "Target" — from the conditions, `MissionProfile.Of`).
  A new mission without an entry in `MissionInfoCatalog` will appear in the table with Brief. Esc in the table — back to the menu.
  Winged vehicles (`ObjectiveType.Runway`) → `MissionProfile.Runway`: target "Orbit and runway landing", diagram without a parachute.
- Light (02.10.2026): Sun flare — SRP lens flare (`SunFlare`), night — reflected Moon/Earth light and sky glow
  (`NightLight`), EvMin −7. Sun RT shadows and DLSS (`RenderQuality`), distant RT ray offset 50 m
  (`SkyController.DistantRayBias`). Spaceport basin `LaunchSite.BasinRadius` 150 km without lakes.
- Parts from Blender (`Models/*.fbx`, `GameBootstrap` fields): Vostok descent module, instrument module with retro engine, PS-1, Ye-6 station,
  RD-107 blocks, RD-0110 of the upper stage, hot-separation truss (lower-stage body is shorter by its height),
  fairing halves, G-1 tail with stabilizers, legs, table trusses. Section model — `SectionDef.Model`.
- Vehicles at full scale (03.10.2026, `GameBootstrap.CraftMeshes`, scale 1, own nozzles and legs):
  Lunokhod-1, Luna-17 landing stage, LM (descent/ascent), Apollo CM and SM, "Mercury", "Gemini" + adapter,
  "Surveyor", "Ranger", "Explorer-1". Palette by slot — `VesselView.CraftPalette`.

## Historic missions (03.10.2026, `HistoricRockets.cs`)
Juno I/"Explorer-1", "Redstone"/Freedom 7, "Atlas"/Friendship 7, "Titan-2"/Gemini 3, "Atlas-Agena"/Ranger 7,
"Atlas-Centaur"/Surveyor 1, "Proton-K"/"Luna-17" with the rover (W/S — drive, A/D — turn), "Saturn-5"/Apollo 8 and 11.
Docking §6.6 (`DockingAutopilot`, Lambert `Orbits/Lambert.cs`, capture `Universe.CheckDocking`): "Apollo" rearrangement
(the CSM turns around and picks up the LM from the S-IVB), LM undocking, liftoff into the CSM plane (`AimAtPlane`) and
rendezvous. Apollo fairings: LES with CM cap (`Apollo_LES`) and 4 SLA panels (`Apollo_SLA_Half` ×2).
Core test groups — 24.

## Autopilots and keys (03.10.2026)
H — ascent to 200 km (from an airless body with a docking target — 30 km into its plane), in orbit around an airless body —
landing (deploys the legs itself). **G — deployables** (§6.12): constructor and Ye-6K station legs — deploy/retract; LM and "Surveyor" — pyro locks, deploy only; Luna-17 landing-stage ramps — only on the ground, until they are down, space will not drop the stage with the rover; after that G opens/closes
the rover lid (on the ground, 6 s). Touchdown on stowed legs faster than 2 m/s — destruction. F1 — HUD details. **Y — whole-mission autopilot** (`Core/Flight/MissionAutopilot.cs`: scenario by mission objectives — ascent,
revolutions, departure, transfer to the Moon, braking, landing, liftoff, docking, return and reentry; it drives time acceleration itself —
`Universe.AutoWarp`, checkbox in Esc "Autopilot controls acceleration itself"; on leaving the autopilot — ×1; "."/"," take over acceleration from the autopilot until it ends, HUD "manual", "/" back to ×1 —
to return; during a maneuver and in the window before it the rails are closed, physics up to ×10 remains). All 16 missions
complete hands-free (test `auto`); "Apollo-11" returns: perigee correction at the SOI exit and 3 h before
perigee (`FixPerigee`), entry −6.8°, 8.4 g. R — the old "to the Moon" (`LunarAutopilot`: reference orbit, burn aimed at
the pericenter, rearrangement, correction, braking, fine-tuning `Trim`). **P — maneuver to target with auto-calculation**
(`Core/Flight/ManeuverAutoPlan.cs`): the target is the body in the map focus (M, Tab), otherwise Moon from Earth / Earth from Moon;
satellite — `PlanIntercept` + pericenter fine-tuning to 100 km by forecast, parent — `PlanReturn` (entry corridor 45 km),
neighbouring planet — `PlanInterplanetary` (Lambert). It only places the node, B executes it (test `autoplan`).
**Tutorial after ascent** — `Core/Missions/MissionGuide.cs` (a "step — what to press" panel in `FlightHud.Guide`, checkbox
"Flight hints" in Esc): node, liftoff, descent, orbit, departure, entry correction, burn to the Moon, transfer, return.
Map (M): orbits of bodies from the focus list, vessel trajectory in 6 segments with SOI transitions, other vessels (△). V — rendezvous and docking with the nearest target. The tutorial (`FlightHud.Tutor`) guides by the law
`AscentAutopilot.GuidedSin`; it suggests cutoff only if a restart is available.
Esc: "Infinite fuel", "Infinite engine restarts" (`Vessel.UnlimitedIgnitions`), brightness sliders
(`BrightnessSettings`: overall brightness `Ev` and plume brightness `Plume`, stored in PlayerPrefs).

## Maneuvers
`NodePlanner`; keys N (node; repeat — next reference point) / B (execute/abort) / Bksp (delete) /
C (circularize), I-K / L-J / O-U — Δv along the axes, `[ ]` — time shift, Alt — fine step.

## Part physics
- Stack aerodynamic moment §4.6 (`StackAeroTorque`: nose CNα 2, cross-flow, damping, stabilizers
  `SectionDef.FinArea`).
- Crew death §4.7 (`SectionDef.Crew`, > 9 g for longer than 10 s, `Vessel.CrewLost`, toggle in Esc).
  Vostok peak on a nominal descent 8.5 g — crew survives; entry at −12° gives 32.7 g — death.

## Rocket constructor (M3, §5.4, 03.10.2026)
- Core: `Core/Vessels/PartCatalog.cs` (parts: command, tanks Ø1–10 m, engines RD-107/RD-0110/J-2/F-1/…, axial and radial
  separators, fairings, cones, legs/RCS/panels/RTG, payload), `Core/Vessels/Craft.cs` — a rocket from parts
  (`Craft`: stack bottom to top + `CraftRadial` side groups ×2/3/4/6/8), JSON, `CraftCompiler.Compile` → `VesselDesign` +
  Δv/TWR per stage + errors/warnings (no engine at launch, TWR < 1, taller than 130 m / wider than the 30 m table, etc.),
  `AutoStage`/`Normalize`, presets "Semyorka" and "Kara-1 from parts".
- Scene `Scenes/Hangar.unity` (menu **Kare/Build Hangar Scene**, requires VesselLit.mat from the Flight build),
  `Game/HangarController.cs` (IMGUI): catalog by tabs, assembly with ▲▼✕, "+ Side", symmetry and group lift,
  stages by spaces with ◄►/"Auto", statistics and checks, "Save/Open" (`persistentDataPath/Crafts/*.json`,
  `_last.json` — autosave on launch), mission selection, "LAUNCH" → `GameBootstrap.NextDesign` → Flight.
  Preview from `ProcMesh` primitives, fairing in cutaway (the half is always on the far side), camera orbit
  LMB/RMB, wheel, MMB — height. From flight — Esc → "To constructor".
- Verified: "Semyorka" from parts TWR 1.32, Δv 11 855 m/s; launch from the hangar → Flight, 313 t, 6 sections; test
  `craft_fly` puts it into 197 × 201 km with side-booster jettison.

## Physical separation (03.10.2026)
- Side blocks (`SectionDef.RadialCount/RadialOffset/RadialLift/RadialParent`): separation hands out N separate vessels
  with their own CoM and velocity (+ ω×r), push `RadialPush` 2 m/s outward and tumble `RadialTumble` 0.1 rad/s; momentum
  is conserved (test `craft`: error 7e-14). Separated still-running blocks keep thrusting (`Split`).
- Vessel collisions (`Universe.Collide`, capsules): > 12 m/s (`CrashSpeed`) — both destroyed, otherwise bounce
  with `Restitution` 0.2. Debris — physics with no range limit, up to 32 vessels (`MaxPassivePhysics`). Test `collide`.
- False-impact protection (04.10.2026) — per pair: the parent and parts of one separation are "fresh" (`Universe.Fresh`) — they
  do not collide or dock until the hulls move apart by `SeparationClear` 1.5 m (no longer than 60 s). New parts also share
  `CollisionGrace` 1.5 s; the active vessel no longer shields them (before, it suppressed impacts with other vessels too).
- Manual undocking — V on a docked vessel (`Universe.Undock` → `Vessel.Undock`): springs 0.3 m/s, the departed vessel
  is not debris and is immediately a target. Without the "fresh" pair it would dock right back (nodes adjacent, 0.3 < 0.5 m/s capture).
- Space at q > 10 kPa, if the stage separates something — only a warning, a second press within 3 s separates
  (`FlightInput.StageKey`). Measured: manual separation at 55 s — "Gemini" (26 kPa) and "Atlas" (36 kPa) tumble
  and break up within 1–2 s (control at its limit), R-7 side blocks at 38 kPa are destroyed within 2 s. The autopilot separates at q ≤ 1 kPa.
- The HUD stage stack groups `WithPrevious` actions into one cell (one press).

## Manual docking and vessel switching (04.10.2026)
- Tab — docking mode: the target is the nearest vessel `NearestDockTarget`; arrows and Q/E rotate, W/S/A/D and Shift/Ctrl —
  translational RCS (`Vessel.RcsTranslate`), throttle is untouched in this mode.
- Instrument `FlightHud.Dock.cs` (draggable): view along the nose, dot — target node, dashes — lateral velocity, ring —
  target axis; scales are logarithmic; distance, closing rate, offset, drift, angle — green within capture limits. A bracket on
  the target node in frame, ◎/✕ on the navball. SAS "to target / align / away from target" (`SasMode.Target/DockAlign/AntiTarget`).
- PgUp/PgDn — next/previous vessel (`Universe.SwitchTo`, only with the engine shut down), Home — back
  to the mission vessel (`MissionVessel`: the task tracker watches it while you control another). The vessel list is in the map.
- The altitude/speed panel is draggable (`FlightHud.Drag.cs`, PlayerPrefs `hud.flight`), double click — back in place.

## Launch complexes (03.10.2026, `Game/LaunchPadView.cs`, `Tools/blender/launch_pads.py`)
Own table per first-section family (`SectionModel` → complex): Sputnik/Vostok/Luna/other → R-7 (4 "tulip" trusses, two
inclined masts, chute); ProtonStage1 → Proton (ring on 6 supports, tall mast, truss rolled away); Redstone, JunoStage1 →
small table on 4 legs + mast; AtlasBooster/Sustainer* → pedestal + tower; TitanStage1 → pedestal, erector, tower;
SaturnSIC → ML 49×41 + LUT 120 m with 9 arms. Concrete is built by C# (boxes), steel — `Models/Pad_*.fbx` (10 pcs, 4 slots).
On liftoff (`releaseT` from Landed→Flying): trusses and masts tilt back (Tilt), tower arms swing aside (Swing, offset
by tiers). Arm length = distance from the hinge to the axis − section radius at its height − 0.3; there are no arms above the top of the stack.
Not verified in Play: the retraction animation, arm landing on the hull. Blender↔C# constants are paired (see comments).

## Detail level and texture cache (03.10.2026, `Game/DetailSettings.cs`, `Game/TextureCache.cs`)
Esc → Graphics → "Detail": Low/Medium/High/Ultra (default High, PlayerPrefs `kare.detail.level`).
Rebuilds the bodies immediately (`BodyRenderer.ApplyDetail`), the mission is not reset. Earth 1024…8192, clouds 1024…8192,
Moon/Mars/lights 512…4096, sphere and cloud mesh density, map orbit points 128…1024. Map orbits are sampled by true
anomaly (smooth pericenter). Textures are compressed to DXT5 and cached on disk: the first pass of a level is long (Ultra ~70 s),
then seconds. Clouds: ragged edge + internal texture (`EarthSurface.Cloud`, octaves grow with size).
Table floodlights at night (4 masts, `LitNits` holds the exposure), concrete apron up to the masts (`ApronMargin`).

## Moon, rover, suspension, camera (03.10.2026)
- Moon map — LROC 8k (NASA SVS), albedo normalized to 0.12 (`BodyRenderer.MoonMap/MoonMapSmall`).
- The mission autopilot (Y) is disengaged only by Y; steering/throttle/stages are locked under it, hint "disengage: Y".
- "Luna-17": orbit 85×85 → pericenter 19 km → landing (as in 1970), rover drives off down the landing-stage ramps (two pairs of rails at
  1.6 m track, 30°), `Vessel.RampDeck/RampTravel`. Leg and wheel suspension — `Vessel.Suspension` (1.5 Hz, ζ 0.6),
  procedural legs in `VesselView` visibly compress.
- HUD: Δv per stage to the right of the stack (vac./at Earth in atmosphere, burn time, total).
- HUD without a central panel: bottom left, going up — throttle/fuel, altitude+speed, maneuver node; bottom right —
  mode (autopilot/SAS). Click on the "SPEED" row — Auto/Orbital/Surface-relative; surface altitude is above
  the ground (`TerrainAltitude`), orbital — above sea level. Navball — bottom centre, dragged with the left button,
  double click — back in place; position and speed mode in PlayerPrefs.
- Camera: held wheel — shift of the rotation centre in vessel axes (up to 3 body sizes), wheel click — reset.
  After destruction the vessel is pinned to the body (`Universe.PinWreck`) — the camera stays above the explosion site.
- Reentry plasma: shock-layer cap as a mesh around the nose and the vessel (`Kare/Plasma Sheath HDRP`) + LineRenderer trail from the neck.
- Map: fixed EV 15, fill light 32 000 lux.

## Wheels, planet maps, R-7 (03.10.2026)
- "Lunokhod" wheels — a separate FBX `Lunokhod_Wheels` (8 objects, `Tools/blender/lunokhod_wheels.py`), they spin along the path
  of their own side `Vessel.WheelPathLeft/Right` / `FlightPhysics.RoverWheelRadius` 0.255; when the vessel turns in place they roll in opposite directions.
- Body maps — Solar System Scope (CC BY 4.0, attribution line in the Esc menu): Mercury and Mars 8k, Venus (clouds) 4k,
  Jupiter and Saturn 4k, Uranus and Neptune 2k; Earth — day 8k, night lights, clouds. Saturn's rings — `BodyRenderer.AddRing`
  (72.2–141.35 thousand km). Conversion and albedo normalization — `Tools/textures/sss_convert.py`. Rings of Uranus/Neptune were not made.
- R-7 family (`HistoricRockets.cs`): "Sputnik" (8K71PS, block A reaches orbit itself), "Vostok" (8K72K + block E),
  "Luna-9/Mechta/Vympel/Flyby" — "Molniya-M" (8K78M, blocks I and L + Ye-6). Block A + 4 side blocks (radial group ×4,
  RD-107 ~120 s), all five fired at once. "Kara-1" remained the 10 t reference. R-7 table trusses rest against the side blocks.

## Explosions, separation, dust (03.10.2026, `Game/BlastEffects.cs`, `Game/ExhaustTrail.cs`)
- Vessel destruction (`Watch` catches Alive→false and the dead one vanishing from `Universe.Vessels`): fireball by the empirical formula
  D = 3.86·m^0.32 (m — propellant + 5 % of dry mass), soot in the air, ground dust / water spray column, 6–30 glowing
  debris pieces with ballistics, point light. In vacuum the ball is ×0.5 and ×0.3 in time, no smoke.
- Burning (Vefects Free Fire HDRP, at density > 0.1 kg/m³): hotspot `VFX_Fire_Floor_01_Smoke` at the impact site
  (1–3 spots, 30–180 s), `VFX_Fire_01_Small_Smoke` on 3 debris pieces. Prefabs — `GameBootstrap` fields, set by the scene builder.
- JMO WarFX explosion on top of the ball (except on water): `WFX_Explosion` scaled R/2.5; near the ground in air at R > 40 m —
  `WFX_Nuke` (R/4). Time by Froude 1/√scale, in vacuum the smoke systems are extinguished. Fields `GameBootstrap.BlastPrefab/
  BigBlastPrefab`, materials — menu Kare/Convert WarFX to HDRP. Not verified in Play (brightness, scale, timing).
- Separation: a flash and a white ring of pyro bolts at the upper end of the new debris piece, flying together with it.
- Dust under the jet in vacuum below 30 m (Moon): 60 m/s fan without deceleration, ground colour of the body from `BodyVisuals`.
- Not adopted: Rainy VFX and AQUAS-Lite — not HDRP (see `pitfalls-tools.md`).


## R-7 shape and sound (03.10.2026)
- Block A and the side "sevens" — bodies of revolution `ProcMesh.R7Body` by profiles `VesselPresets.R7CoreProfile/R7BoosterProfile`
  (`SectionModel.R7BlockA/R7Booster`): block A tapers toward the bottom (Ø2.95 → Ø2.15), the side one is an oblique cone, the inner
  generatrix runs along the "waist" of A with gap `R7BoosterGap`. Physical `RadialOffset` = 2.515 m (was 2.965) — one
  number with the geometry. Jettisoned side blocks keep their tilt via `Vessel.RadialYaw` (visual only). Air rudders — a box.
- Sound `Game/FlightAudio.cs`: everything is synthesized at startup (20 clips, ≈0.7 s in the editor), no samples. Frame-to-frame difference
  of vessel state → one-shot sounds: ignition/shutdown (tone by engine size), failed start, liftoff from the table,
  separation/fairing pyro, docking/undocking, parachute pop and burst, ground/water touch (strength by
  speed), explosion (delayed by d/343). Loops: roar (log of thrust, fades above Mach 1, near the ground — low-end echo, SRBs crackle),
  wind by dynamic pressure with transonic buffeting, plasma hum by heat flux, RCS, deployable actuators, rover drive,
  g-load/overheat siren, parachute flapping. Vacuum: others are inaudible, own — muffled "through the structure".
  Warp > ×4 / rails — silence, map — half voice, menu — pause. Volume — Esc → "Sound" (`SoundSettings`).

## Terrain from real maps (04.10.2026, `Core/Bodies/Terrain.cs` HeightMap, `Tools/bake-dem.py`)
- Earth, Moon, Mars, Mercury, Venus — real DEMs: ETOPO 2022 60″ (with bathymetry), LRO LOLA LDEM_16, MGS MOLA MEGDR
  16 ppd, MESSENGER USGS 665 m, Magellan 4641 m. Baking `python Tools/bake-dem.py [bodies] [--check]`, sources
  (≈680 MB) — in `Tools/dem_src/` (in .gitignore, downloaded again). Output `Data/<Body>Height.bytes`: 'KDEM', W, H,
  scale, offset, int16 m; 3600×1800 (0.1°, 13 MB), Venus 2880×1440 (8.3 MB). Layout as in EarthLand.
- Height = bilinear DEM + Fbm finer than a texel (first octave = texel, down to ~30 m), strength — RMS of the neighbouring
  texel difference in an 8×8 block (≤ 400 m). On Earth the noise does not change the sign of height; the coast is jagged by ±12 m noise only at the mask shore.
  Land below 0 inside continents (Qattara, Caspian lowland, polders) is raised to +1 m during baking.
- `TerrainSettings.Amplitude` for bodies with a map = (max + noise)/1.5 (Earth 4572, Moon 7365, Mars 14375, Mercury 3041,
  Venus 7330) — this is the "above the mountains" bound for CheckContact/PredictGate/RailsFloorRadius.
- Host: `SolarSystem.HeightMaps` before `CreateReal` (GameBootstrap — fields `EarthHeight…VenusHeight`, FlightSceneBuilder
  assigns them; CoreTests — from files next to EarthLand, `KARE_NO_DEM=1` — the previous noise). TextureCache.Version 3.
- Comparison with the colour maps: longitude shift 0.0° (Earth, land/water 99.4 %), Moon +0.0…0.1°, Mars −0.1…0.0°,
  Mercury −0.3…+0.1°; Venus — a cloud image, checked by landmarks (Maxwell 9.8 km at 65.2° N, 3.3° E).
- **15″ patches at the pads** (05.10.2026, `DemPatch`, `HeightMap.AddPatches`): ETOPO 2022 15″ 1.5°×1.5° around Baikonur
  (+"Yubileyny"), Canaveral (+SLF), Kourou, Plesetsk, Vostochny, Edwards — `Data/EarthPatches.bytes` (1.56 MB,
  `bake-dem.py patches`, cache in `Tools/dem_src/`). Bilinear patch with a smooth 0.2° transition to the global one, noise — by the patch
  step; field `GameBootstrap.EarthPatches` (assigned by FlightSceneBuilder), CoreTests loads it next to EarthHeight. Test `patches`.

## Station and crewed missions after Apollo (04.10.2026, `StationRockets.cs`, `StationMissions.cs`, `MissionAutopilot.Station.cs`)
- Missions: **voskhod2** (R-7 + block I, "Volga" airlock, 18.03.1965, ~200 km orbit, return), **soyuz_tm31** (Soyuz-U, TM-31 to the ISS
  380 km/51.6°, 31.10.2000), **crew_dragon** (Falcon 9 + Crew Dragon Demo-2 to the ISS 420 km, Cape Canaveral, 30.05.2020).
- The station is a project section (`MissionDef.StationSection`), placed as a separate vessel in orbit above the launch site; objective `Dock`
  (HoldSeconds when docked). `Station` profile: plane window → ascent → `DockingAutopilot` → 10 min docked →
  undocking (`Universe.Undock`) → departure to a 50 km pericenter → ballistic descent.
- Not present: Falcon first-stage landing, the 64.8° inclination of "Voskhod-2" (launches east, 45.9°), separate FBXs.

## RCS jets (05.10.2026, `Game/RcsJets.cs`)
- A component on the vessel view object (hook in `GameBootstrap.SyncViews`), works for all vessels, including
  inactive and docked ones. Sections with RCS ≥ 30 % of the strongest; 4 blocks × 4 nozzles at 45° to the axes.
- They fire on command: translational `RcsTranslate` + `RcsForward`, rotation — the remainder of `TorqueCommand` beyond nozzle
  and rudder gimbaling. A partial command — 4 Hz pulses, 50 ms rise/fall, 12 % jitter. Shorter in the atmosphere.
- The `FlightAudio` hiss — by the same fraction (`RcsJets.Level`).

## Part window, stages in detail, chute altitude (05.10.2026)
- **Part window** (`Game/PartInspector.cs`, adds itself to Flight and Hangar): LMB click (shift < 6 px, < 0.6 s) on a
  section in flight (`VesselView.Pick.cs`, ray against renderers' `localBounds`) or on a part in the constructor
  (`HangarController.Inspect.cs`, ray against the layout cylinders). Text — `PartInfoText`: masses, sizes, propellant
  now/max/%, thrust vac./sea level, Isp, burn time, RCS, parachute, heat shield, crew, etc. The window is dragged by its
  header, "pin" / "×"; an unpinned one is single and is replaced by the next click, pinned ones live on and update.
  A click on HUD panels does not select a part: panel rectangles are written by `HudHits` (from `FlightHud.Fill`, the navball, windows).
- **Stages in detail** (`StageTable.cs`): in flight, instead of the Δv list — `FlightHud.Stages.cs` (propellant as a bar and kg,
  Δv, TWR, time, engines, mass; the header collapses it, `hud.stagesDetailed`); in the constructor — a
  "Stages in detail" button in the corner of the preview. `StageStats.Sections`/`Propellant` — sections and propellant of a row.
- **Speed/altitude panel** is top centre by default; drag key `hud.flight2` (the old offset is forgotten).
- **Parachute deployment altitude** per section: `SectionDef.ChuteAltitude` (0 = 7 km), `Vessel.ChuteAltitude[]`,
  `SetChuteAltitude` (clamp `FlightPhysics.ChuteAltitudeMin/Max` = 1…15 km), copied on separation/docking.
  Set in the part window (−/+ 500 m, "nominal"); in the constructor it is written to the rocket JSON as an optional key `chuteAlt`
  (`CraftPart`/`CraftRadial.ChuteAltitude`, NaN = nominal, the key is not written; the compiler puts it in the section, test `craftparams`).
  The autopilot waits for arming below `max(7 km, HighestChuteAltitude)`. Test `chutealt`.

## Aerodynamics, winged missions, parametric constructor (05.10.2026)
- `Core/Flight/Aerodynamics.cs`: wing panels and fins (`SectionDef.Wings`, `WingDef`) — plates in the local flow
  (damping via ω×r), CN by Helmbold/DATCOM + vortex part, stall, induced drag, rudders and flap.
- Missions `sts1` and `buran` (`WingedRockets.cs`, `WingedMissions.cs`, `MissionAutopilot.Winged.cs`): ascent, departure,
  planning by a braking program, runway landing on gear (`DeployKind.Gear`). Tests `auto_sts1`, `auto_buran`.
- Constructor: part dimensions (`PartParam`): tanks — length/diameter, adapter — length/bottom/top, cone — length/diameter,
  separator and fairing — diameter, stabilizers — span/chord; wing and tail — span, chord, sweep,
  incidence, dihedral; fin — span, chord, sweep. New parts `wing`, `wing-tail`, `wing-fin`, `gear`.
  In the hangar — a "Dimensions" block under the stack (−−/−/+/++, "As in catalog"); mass/propellant/area — `PartCatalog.Resolve`.
- Rocket JSON: a stack part has optional keys `length, diameter, top, span, chord, sweep, incidence, dihedral`
  (written only if set); old files open as is. Warning "Joint Ø a → Ø b" when the difference is > 0.05 m.
- Wings without FBX are drawn as plates (`Game/WingMesh.cs`) in the hangar and in flight from the same WingDef that the physics uses.

## Constructor part icons (05.10.2026)
- Menu **Kare/Bake Part Icons** (`Editor/PartIconBaker.cs`): renders the actual model of each catalog part in a
  preview scene (isometry yaw 35°/pitch 20°, ortho camera fitted to the bounds, transparent background) → `Textures/Icons/Parts/<id>.png`
  128 px + `Resources/PartIcons.asset` (`PartIconSet.Get(id)`). 78 icons ≈ 34 s, re-runnable; `PartIconBaker.Bake("id")` — a single one.
- Model: `Models/Part_<id>.fbx` (convention for new FBXs) → `IdModels` table → section FBX (`FlightSceneBuilder.CraftFiles`)
  → procedural geometry `PartShapes.Icon` (the same as in the hangar preview, `PartShapes.Preview`). Landing gear — `*Gear*.fbx`.
- In the constructor, a 48 px icon in the part list and 26 px in the stack (`HangarController.CatalogIcon/StackIcon`), name beside it.

## Control surfaces, drag chute, orbiter glazing (05.10.2026)
- **Movable surfaces — a common mechanism via `WingDef.Surfaces`** (`Aerodynamics.ControlSurface`): hinge HingeA→HingeB
  in section axes, Aft/Up, chords, thickness, mixing `MixYaw/MixRoll/MixPitch/MixTrim`, `BrakeDeg` (split), `MaxDeg`, `RateDeg`,
  `DarkBelly`. If it has none — `Aerodynamics.DefaultSurfaces(w)` (a strip along the trailing edge of the WingMesh plate: elevons/rudder/flaps).
  Angle — `ControlSurface.Angle(cmd, trim, brake)`; cmd — `Vessel.ControlDeflection` = actual moment / MaxTorque (what
  the physics applied). View — `Game/VesselView.Controls.cs` (wedge flaps on hinges, the actuator rate limit is visual only).
- "Shuttle"/"Buran" (`WingedRockets.ShuttleSurfaces/BuranSurfaces`): 2 elevons per panel, a rudder-speedbrake of two flaps (±44°),
  a body flap (`WingDef.BodyFlapArea` — a `WingPanel.Trim` panel without area: only the trim moment `TrimAuthority`).
- Settings: `Vessel.ControlLimit` (surface travel 25–100 %), `Vessel.PitchTrim` (−1…1), `AirBrake`. Keys: **1** — air brake,
  **2** — drag chute (deploy/jettison), **3** — trim to 0, **Alt+W/S** — trim (W/S with Alt is not pitch). In the section window
  (click on the orbiter) — the same buttons (`PartInspector.Controls.cs`). Without a body flap the trim shifts the elevon command.
- **Drag chute** (`SectionDef.DragChuteArea/Count/Mount`, `Vessel.DragChute*`): only in rollout, torn off above 120 m/s,
  inflation 2.5 s, Cd 0.55, jettisons itself at 30 m/s (`FlightPhysics.DragChute*`). "Shuttle" 1×117 m² (Ø12.2, since STS-49 —
  kept for STS-1 too for gameplay), "Buran" 3×75 m² (estimate). The mission autopilot deploys it in the "Rollout" phase.
- Glazing: `winged_parts.py` `windows()` — 6 front, 2 upper, one on each side; frame (black) + glass, 5th slot `Glass`
  only on orbiters → `VesselView.GlassColor` / `GlassMaterial()` (a copy of bodyMat without maps, smoothness 0.96). Elevons, rudder and flap
  are cut out of the FBX (wing/fin outline along the hinge lines) — paired with the hinges in `WingedRockets`.
- For Starship (agent G): set `wing.Surfaces` as a list of `ControlSurface` (flaps: MixPitch ±1 fore/aft, MixRoll ±1 per side),
  `ControlMaxDeg/ControlRateDeg`; the view and physics will pick it up without edits.

## Station vehicles: Draco, nose cone, parachutes, solar arrays, RCS by models (05.10.2026)
- **Draco Crew Dragon** (`Game/VesselView.Dragon.cs`): 4 plumes on the capsule wall (y 2.85, az 45+90k), tilted 40° outward,
  no smoke; there is no axial plume through the trunk.
- **Dragon hinged nose cone** (`DeployKind.Nose`, `Crew_Dragon_Nose.fbx`): hinge (−1.3; 3.1; 0), 115°, actuator 6 s.
  Opened by the docking autopilot, Tab (manual docking) and G; closed by the mission autopilot before "Deorbit".
  A closed nose cone — no docking (`Vessel.PortOpen`).
- **Parachutes as a cluster**: `SectionDef.ChuteCount/ChuteOffset` — Dragon 4 parachutes, root 0.9 m from the axis under the nose cone;
  "Apollo" 3. The area is split equally, physics unchanged.
- **Solar arrays on hinges** (`DeployKind.Panels`): Soyuz PAO (`Soyuz_PAO_Panels.fbx`, hinge r 1.55 at 0.7 m, folded along the
  hull under the fairing); they deploy themselves when the engines are silent (`Vessel.PanelsReady`), and via G. "Ranger-7" —
  only a core flag, no separate panel model.
- **RCS by models** (`RcsJets.Clusters`): DPO on the PAO skirt, Draco, Apollo SM quads, "Mercury", "Gemini"/OAMS,
  shuttle and "Buran" FRCS/ARCS; others — by mesh radius at the block height. The CM/capsule is silent when a service module is present.
- Models: `Tools/blender/split_station.py` (rebuilds Crew_Dragon and Soyuz_PAO, cuts parts). After editing an FBX —
  menu **Kare/Build Flight Scene** (DeployFiles).

## Render: fog above the atmosphere, shuttle plasma, patch seam (05.10.2026)
- `SpaceFogFix` (+ `SpaceFogRestoreHDRP.shader`) removes the false HDRP PBR fog from the vessel when the camera is above the atmosphere.
  The blue limb line on the ship is gone. The passes are created by `SkyController.Start`, the shader is set by FlightSceneBuilder
  (in the editor the fallback path is `Shader.Find`).
- Winged-vehicle plasma (orbiter, "Buran") wraps the windward belly and edges, the trail goes behind the tail.
- The patch albedo decal hides the patch/sphere seam at Earth.
- Faster auto-exposure (2/5, plume clamp decay 1 s).
- `SkyController` runtime volume profile is now a deep copy (components cloned): Play no longer writes exposure/sky values into `FlightVolume.asset`.

## SpaceX reusability: Falcon 9 booster landing, Starship IFT-5 (05.10.2026)
- `Core/Flight/BoosterLanding.cs` — `BoosterLandingAutopilot` flies a detached first stage by its `SectionDef.Recovery`
  (`RecoveryDef`: target lat/lon, `AtSea`, deck height/radius, `Reserve`, `BurnEngines`/`LandingEngines`, entry altitude/Δv,
  `GridFinArea`, `BellyFlop` + `FlopAltitude`, `TowerCatch`, `LandingDv`). Phases: Flip → Boostback → Coast → Entry → Aero →
  Flop → Landing → Done/Failed. Started automatically for a detached section with `Recovery` (presets in `SpaceXRockets.cs`).
- Falcon 9 (Demo-2, `crew_dragon`): stage I lands on OCISLY (`SpaceXRockets.Ocisly`), test `booster` — 3.0 m/s, 8 m from deck centre.
- Starship IFT-5 (`ift5`, 13.10.2024, Starbase): Super Heavy (33 Raptor, 3400 t) + Starship (6 Raptor, 4 flaps as
  `ControlSurface`), hot staging, `MissionAutopilot.Starship.cs`. Booster is "caught" by the tower (`TowerCatch` — a landing at the
  tower, legs stowed), ship coasts ≈ 50 min to a splashdown east of Mauritius (−25.93°, 68.42°), belly flop at 2.5 km.
  Test `starship`: MECO 2:07, SECO 6:21 (perigee 71 km, 88 t left), Super Heavy caught at 2.5 m/s 7 m from the tower (15 t left),
  ship splashdown 0.9 m/s 2.4 km from target, peak heat 0.47 MW/m², max q 37 kPa; `auto_ift5` — Success.
- View: `Tools/blender/spacex_parts.py` → `Super_Heavy.fbx`, `Starship.fbx` (steel = `PolishedColor`, tiles = `TileBlackColor`),
  re-exports `Falcon9_S1.fbx` without baked legs/fins (`F9_BAKED_DEPLOY = False` in station_parts.py).
  `VesselView.SpaceX.cs`: F9 legs on `Hinge` (deploy with G / by the autopilot, feet in the bottom plane, span ≈ 20 m) and grid fins
  (open in 2.5 s when the stage flies alone). `RecoveryDeckView.cs` — OCISLY barge (52 × 91 m) at the recovery target.
  `LaunchPadView` Kind.Starbase — tower "Mechazilla" with chopsticks (pad chosen by `SectionModel.SuperHeavy`).
- `starship_catch` (06.10.2026): both stages caught by towers. Super Heavy -> tower A (`Reserve` 520 t), ship -> orbit 267 km
  (16 revs per sidereal day, repeating ground track), `PlanShipDeorbit` picks the deorbit ~22 h later over Texas, boostback in
  vacuum, belly entry with AoA as a range rudder (`GuideBelly`), flop, catch by tower B (`StarbaseShipCatch`, OLP-B ~300 m NE).
  Objective Landing with `Site = Objective.TowerSite`. Test `starship_catch`: SH 3.5 m/s 3 m, ship 2.6 m/s 9 m, peak heat
  0.53 MW/m2, max q 43 kPa, 7.8 g. View: `LaunchPadView` builds the second tower "Mechazilla B" for every Starbase pad.
  Play 06.10.2026: SH 3.1 m/s 3-4 m, ship 2.6 m/s 11 m (post-burn miss 33 km absorbed by `GuideBelly`, cross-track
  456 m at 4 km taken out by the landing burn); F2/F3/F4 work, F4 resume -> booster caught. Split collapses to one
  view 8 s after the booster catch (`HoldAfterLanding`) - the ship entry a day later is single-view by design.
- **Physical chopstick catch** (06.10.2026, `Core/Flight/TowerCatch.cs`, called from `FlightPhysics.StepFlying` before
  `CheckContact`): tower geometry (x = -30, side 12, height 146), arm bars to x = +14, half-gap 15 m open -> 5.5 m closed in
  2.5 s. `RecoveryDef.ArmHeight` / `PinsFromTop`: SH 75.8 m (pins 6 m below the nose, hangs 6 m above the 3 m pad),
  ship 45.3 m (pins 11 m below the nose). The pilot aims the bottom at `ArmHeight - (len - PinsFromTop)` (`TargetBodyFixed`).
  Arms close while the hull spans them; on the pin crossing: both pins must rest on bars (else "Сорвалась: смещение"),
  sink <= 6 m/s, slide along the arms <= 3 m/s, tilt <= 10°, else "Удар о палочки"/"Сорвалась". Hull hits tower -> "Удар о башню";
  bars push the hull out (contact band ±1.2 m). Caught -> `Vessel.TowerCaught`, Landed, anchored. Objective `TowerSite` checks
  `TowerCaught`. Missed arms -> falls to the ground on stowed legs (crash > `StowedCrashSpeed`). View: `LaunchPadView` arms are
  separate bars posed by `TowerCatch.Gap(Vessel.CatchArms)`. Trace: env `KSP_CATCH=1` prints `CATCH …` lines.
  Tests: `starship_catch` SH 2.9 m/s 6 m, ship 2.5 m/s +8 m along the arms; `starship` SH 2.5 m/s 7 m; `booster` unchanged.
- Not verified in Play: the full IFT-5 / Demo-2 flight with the view (barge and catch seen only from the core tests);
  chopstick closing animation and `RunwayView` not seen in Play after the 06.10.2026 change.
- **Order "stage ↔ ship" after separation** (`Game/FlightView.cs`, `Core/Flight/Universe.Deferred.cs`, `FlightHud.View.cs`):
  control always stays on `u.Active` (the ship's mission autopilot flies only the active vessel); only the *view* moves.
  F2 — view on the returning stage ("landing first": ship flies itself, view returns 8 s after touchdown);
  F4 — defer the landing ("ship first": `Universe.DeferRecovery` removes the stage from the world, rails open;
  F4 again or the end of the ship's mission (+4 s) → `ResumeRecovery` puts it back with a time shift); F3 — split screen
  (left: main view, right: the other vessel, cloned main camera). A prompt with the three keys shows 20 s after separation.
  `FlightView.Main` drives FloatingOrigin, terrain patch, sky, sun, night light, audio; `FlightView.Near` keeps pads/barge/vessels
  visible near the second view. Test `booster_defer`: 40 min pause (warp up to ×1000), landing 3.0 m/s, 8 m — same as `booster`.

## Landing training, pad surroundings (06.10.2026)
- **Approach start** (`Core/Missions/ApproachStart.cs`, `MissionDef.Approach`): `CreateUniverse` cuts the design to the top section
  (`TopOnly`) and `Place` puts it in the air before the landing target (runway of the `Runway` objective or `Recovery` target),
  range back along the approach course, air speed, path angle, AoA (90° = belly), optional propellant.
  Missions: `sts1_landing`, `buran_landing` (25 km, 85 km out, 800 m/s, path −6°, α 10°), `starship_landing`
  (15 km, 1 km out, 200 m/s, path −75°, belly, 100 t, caught by tower B). Starship with `Approach` starts at `StartDescent`.
  Tests `auto_sts1_landing`, `auto_buran_landing`, `auto_starship_landing` — Success.
- Landing tutor (`MissionGuide.cs`): «Вход», «Заход на полосу», «Выравнивание», «Пробег»; Starship — «Падение брюхом»,
  «Посадочный импульс». H while descending (vertical speed < −20 m/s) no longer starts the ascent autopilot (`FlightInput.cs`).
- Starship flaps: stow 55° fwd / 5° aft (were 100/110° — flaps folded into the hull and vanished), rate 45°/s; belly (+X, tiles)
  faces the flow (`BoosterLanding` roll target `vh`).
- **Pad surroundings** (`Game/LaunchPadView.Surroundings.cs`, partial): road north + bypass, 6 buildings with window bands and
  parapets (`Layout`), tank farm per kind (Starbase 2×4 vertical, Saturn two spheres on legs, others 3 tanks), 4000 ellipsoid
  bushes within 600 m (colour = biome at the site shifted to olive, seed = hash of `Site.Id`, Earth only), `occupied` rects keep
  them off roads/buildings. Concrete: `Tools/gen-concrete.py` → `Textures/Ground/Concrete.png` + `ConcreteNormal.png`
  (8 m tile, 4 m plates, periodic FFT noise; `GameBootstrap.PadNormal`, set by FlightSceneBuilder).
- Sky ground tint near the surface = local biome hue at `BodyLook.Low` luminance (`SkyController.LocalGround`).
- Checked in Play: Baikonur, Starbase, Saturn pads; `starship_landing` flop with visible flaps; `sts1_landing` spawn.
  Not checked in Play: the full shuttle glide to touchdown with the view (core auto tests only).

## Constructor 2.0: drag-and-drop, plane view, undo (06.10.2026, `Game/HangarController.Build.cs`)
- Drag a part from the catalog (or press-and-drag a part of the craft) onto an attach node: stack top/bottom/between parts,
  side of a stack part (radial group with symmetry), on top of a radial block. Green ghost = snapped, red = no node (drop cancels).
  Drop onto the catalog panel deletes. Node markers and CoM (yellow) / CoL (cyan) / CoT (magenta) are drawn in the preview.
- Keys: X / Shift+X symmetry, V rocket/plane view, 1-4 view presets (iso, side, front/rear, top), F frame, C centers,
  Ctrl+Z undo, Ctrl+Y / Ctrl+Shift+Z redo (100 steps), Del/Backspace delete selected, Esc cancel drag. LMB-drag on empty — orbit,
  RMB orbit, MMB pan, wheel zoom.
- Plane view: the craft lies nose → +Z, belly down (`PlaneRot`); the floor fit is by renderer bounds (`FitToFloor`).

## Aero FX (06.10.2026, `Game/VesselView.Aero.cs`)
- Vapor cone (Prandtl–Glauert): Mach window 0.86→0.96…1.02→1.15, strength by q (25 kPa full) and density (0.5 full);
  bell around the CoM, half-angle → Mach angle above M 1. Off while the plasma sheath is on.
- Wingtip vortices: LineRenderer from the trailing edge of each horizontal wing ≥ 2 m², AoA 6°→16°, q ≥ 12 kPa, M < 1.6.
- Material — lit `GameBootstrap.SmokeMaterial` (`VesselView.VaporMaterial`), not emissive: white in sun, dark at night.
- Test by hand: Play, `Time.timeScale = 0`, set `Vessel.Mach/DynamicPressure/Density/AngleOfAttack/SurfaceSpeed` by reflection —
  physics does not overwrite them while time is frozen, LateUpdate still draws.
