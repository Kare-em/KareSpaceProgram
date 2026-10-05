# Kare Space Program — feature description

What the game can do and how it does it. Links point to `docs/GDD.md` (§) and to the code. Numbers are constants from the code.

---

## 1. Large distances and sizes (the main point)

A 1:1 Solar System: Earth R = 6 371 km, the Moon at 384 400 km, the Sun at 1,496·10¹¹ m, Neptune at ≈4,5·10¹² m.
Unity's `float` (24 mantissa bits) is unusable at such numbers, so the world is split into three layers.

### 1.1 The core — `double` only (§2.6)
- `Kare.Space.Core` — pure .NET with no UnityEngine (`Vector3d`, `QuaternionD`). All positions, velocities,
  orbits and ephemerides are in `double`.
- `double` step (ulp): at Earth (6,4·10⁶ m) ≈ 1 nm, at 1 AU ≈ 30 µm, at Neptune ≈ 1 mm. Enough both for touching
  lunar soil with a landing leg and for an interplanetary flight.
- `float` step for comparison: at 1 AU — 16 km, at Earth from its center — 0,5 m. That is why nothing is computed in `float`.
- Frame P — ecliptic J2000, right-handed triad, z — the ecliptic pole. `Vessel.Position` — relative to the center of its own body
  (non-rotating axes), heliocentric = `Body.Position + Position` (`FloatingOrigin.WorldP`).

### 1.2 Floating origin (§2.6, `Game/FloatingOrigin.cs`)
- The active vessel is **always** at Unity (0,0,0). Every frame: heliocentric (`double`) → subtract `OriginP` → SwapYZ
  (Unity is left-handed, y up) → `float`.
- The only place for the conversion is `FloatingOrigin.ToUnity` / `DirToUnity`. No renderer does its own coordinate
  math — otherwise the worlds drift apart by meters.
- Result: near the vessel `float` precision is < 1 mm at 10 km; there is no jitter like KSP had before the Kraken fix.

### 1.3 Camera and the body "shell" (§2.7, `BodyRenderer.Project`)
- Camera: near 1 m / far 1·10⁷ m (`FlightCamera.NearClip/FarClip`). Far cannot be larger: HDRP 17.6 loses Sun
  shadows (measured: near 1 — far 1e7 shadows are present; far 3e6…2e8 at near 0,1/2/20 — absent).
- Everything farther goes into the shell 5·10⁶…9,5·10⁶ m: a body whose distance to the horizon H > `ShellStart`
  is scaled and moved closer so that the angular size is preserved and H lands at
  `ShellStart + ShellWidth·x/(x+7)`, x = ln(H/ShellStart). The Moon from Earth lands at ≈ 6,7·10⁶, Neptune at ≈ 8·10⁶.
- The measure is the distance to the horizon, not to the center: the active body up close is not compressed and joins the patch seamlessly.
- Depth order is preserved (the function is monotonic), so the Moon behind the Earth stays behind the Earth.

### 1.4 Surface up close — the patch (§2.8)
- The body sphere from space is a 384-segment mesh (detailed bodies), 96 for the rest; the Earth texture is 2048 (≈ 2,3 s to generate).
- Below 40 km (`PatchMaxAltitude`) a 128×128-node patch spanning ±80 km is overlaid, heights from `Core/Bodies/Terrain`
  (`double`, `ConcurrentDictionary` cache), ground tiled at 6 m + macro variation of 350 m, water with ripples, seabed down to −200 m.
- The horizon from the patch ceiling is 715 km < `ShellStart` 5000 km, so the patch never ends up in the shell.

### 1.5 Time: physics and rails (§3, `Core/Flight/Universe.cs`)
- Time warp: ×1, 5, 10, 50, 100, 10³, 10⁴, 10⁵, 10⁶, 10⁷. "/" — reset to ×1.
- In physics (atmosphere, thrust, maneuver, surface nearby) — up to ×10 (`MaxPhysicsWarp`): RK4 with substeps.
- Above that — "rails": the vessel flies along a Kepler conic section analytically, the error does not accumulate at any ×.
  Rails are cut off by `WarpLimitTime`: SOI change, atmosphere entry, the node moment (− 30 s `NodeWarpMargin`).
- Spheres of influence — patched conics; after an SOI change on rails the limit is recomputed.
- Passive vessels (jettisoned stages, side blocks) are simulated by physics with no range limit, up to 32 vessels
  (`MaxPassivePhysics`), with collisions; the rest are on rails.

### 1.6 Map (`Game/MapView.cs`)
- A separate camera, range up to 5·10¹¹ m, orbits — 256 points per patch, different colors for different patches, the descent segment in red.
- Tab — cycle focus: vessel → system bodies; labels behind a planet are semi-transparent.

---

## 2. Flight

- **Stages and separation**: each part sits at its own CoM (+ω×r), a push with recoil (`separation` test: error 0 m).
  The fairing opens with its petals.
- **Engines**: number of starts, ullage (RCS propellant settling, threshold 0,01 m/s²), minimum thrust.
- **Aerodynamics**: US Standard Atmosphere 1976, aerodynamic moment of the stack (nose CNα 2, stabilizers), max-Q, heating,
  aerodynamic breakup (off by default).
- **Crew**: death at > 9 g for longer than 10 s. The nominal Vostok descent is 8,5 g.
- **Parachute** with reefing, splashdown with draft.

## 3. Controls and autopilots

- SAS: hold, prograde/retrograde, normal/antinormal, radial, to node — buttons next to the navball (as in KSP).
- **Navball**: a sphere of local axes (sky/ground, grid every 30°/45°), cardinal directions, SAS mode markers, heading and pitch.
- **G**: on a body with an atmosphere — ascent to 200 km; on a body without one — the landing autopilot (RK2 prediction,
  bisection of the ignition moment, terminal law; touchdown ≈ 3,4 m/s). Press again — cancel.
- **Maneuvers**: N — node, B — execute, C — circularize, I/K L/J O/U — Δv, `[ ]` — time, Alt — fine step.

## 4. Esc menu and cheats for testing

- Mission selection with a scene restart (Vostok, Luna-9, Moon flyby, etc.).
- Infinite fuel, teleport: Earth orbit 200 km, Moon orbit 100 km, above the Moon at 15 km (G — landing),
  Mars orbit 300 km. Teleport resets autopilots, the node and time warp.
- Damage toggles (aerodynamic breakup, heating, g-load).

## 5. Graphics (HDRP)

- Physically based sky, Sun 6500 K, auto exposure with lower EV limits (in light, near the flame, near the entry plasma).
- Stars — EXR in nits (`Editor/StarFieldBaker.cs`), BC6H cubemap.
- Earth: biomes (temperature + humidity), ocean glint, clouds as a sphere at +8 km, night lights.
- Flame — emission only, a two-sided nozzle bell; entry plasma with a shock-wave standoff of 0,1 r, hull glow.
- Launch table with trusses, retraction at liftoff; concrete and ground — ground12/13, hull — Metal_30 triplanar.
- Exhaust trail (`ExhaustTrail`) and smoke puffs at the table on launch.
- Low-poly parts from Blender (`Models/*.fbx`, `Tools/blender`): the Vostok descent module, RD-107 blocks on strap-ons, landing
  legs of the E-6K station, lattice trusses of the table. Without the FBX — procedural meshes.

## 6. Verification

- Headless core tests `Tools/CoreTests` (43): from math and Kepler to full missions "Vostok", "Luna-9"
  and the landing from a cheat (`moondrop`). The command is in `CLAUDE.md`.
