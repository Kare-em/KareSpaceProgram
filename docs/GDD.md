# Kare Space Program — Game Design Document (GDD)

> Version 0.1 (draft) · 2026-10-01 · M0 "simulation core" in progress.
> Platform: Windows PC (x64), Unity 6000.6.3f1, HDRP, C#.
> Game start: 4 Oct 1957, 19:28:34 UTC, Baikonur, "Gagarin's Start" (45.920° N, 63.342° E).
> Code comments refer to sections of this file (e.g. "see GDD §2") — do not change the numbers and titles of §1–§12.

Contents: §1 Concept · §2 Scale and precision · §3 Time · §4 Flight physics · §5 Vessels · §6 Unique mechanics · §7 Missions · §8 Progression · §9 Graphics · §10 Controls · §11 Roadmap · §12 Risks.
> Data sources: JPL (Standish) and Schlyter (ephemerides), IAU WGCCRE (rotation), USSA76 (atmosphere), WGS84 (Earth figure), NASA Standard Breakup Model (debris); textures — NASA, public domain, downloaded only with the project owner's consent.

All numbers in this document are project values for the start of balancing unless explicitly marked "real". Real data is marked "real.".

## §1 Concept and pillars

### §1.1 The essence of the game
Kare Space Program is a space simulator in the spirit of Kerbal Space Program, but at a true 1:1 scale of the real Solar System. The player is the head of a space program: from the first satellite in 1957 to missions to the moons of Jupiter and Saturn. Earth has a radius of 6378 km, not 600 km, so the orbital speed of 7.8 km/s and the Δv to orbit of ≈ 9.3–9.5 km/s are not an abstraction but something you have to calculate.

The game runs against the real calendar. History moves on schedule (Sputnik, Gagarin, Leonov, Luna 9, Apollo 11…), and every mission is a race: did you make it before the historical date or not.

### §1.2 Pillars
| Pillar | What it means in practice |
|---|---|
| Real scale with no "toy" shrinking | Sizes, masses, Δv, transit times are real. A trip to Mars ≈ 8–9 months. |
| The calendar decides | Launch and interplanetary windows, the historical rival, technology eras are tied to dates. |
| Physics instead of scripts | Radiation belts, plasma blackout, light delay, debris are consequences of the model, not scripted events. |
| A program, not a single rocket | Budget, science, prestige, reusability, engine reliability, flight hours. |
| Realism by choice | Simplified modes (§8.5), ascent autopilot, tutorial — an entry point for newcomers without losing depth. |
| Readable data | Map, porkchop plots, HUD: the player sees the numbers on which decisions are made. |

### §1.3 Game loop
1. Planning: choose a mission, find a window (porkchop), estimate budget and risk.
2. Design: build a rocket/probe (until M3 — the ready-made "Kara-1" and probes from the catalog).
3. Launch and ascent: physics mode, RK4 0.02 s (§4), autopilot or manual control.
4. On-rails flight: time acceleration, maneuvers on the map, onboard computer programs.
5. Result: science (transmission channel), prestige, money, stage recovery.
6. Development: tech tree, era change, next mission; meanwhile the rival's calendar keeps running.

### §1.4 Audience and platform
- A player familiar with KSP/Orbiter, and a newcomer interested in real spaceflight; the latter gets a tutorial, autopilot and simplified modes.
- PC only. Target specs (to be refined by measurements in M1): 4+ CPU cores, 16 GB RAM, GPU with 6–8 GB VRAM, 60 fps at 1080p on the High preset at RTX 3060 level.
- Controls — keyboard and mouse; no gamepad planned in v1.

### §1.5 What is not in the game (scope boundaries)
- N-body for vessels: only patched conics (Lagrange points and low-energy trajectories are not reproduced; see §2.5, §12).
- Multiplayer, VR, mobile platforms.
- Structure deformation and destruction by the finite element method; destruction — by thresholds (§4.7).
- Weather and wind until M6; after that — a simple wind model near Earth.
- Modding as a goal; but data (missions, parts, history) is stored in open JSON.

## §2 Scale and precision (the key decision)

The real Solar System at 1:1 is the decision the whole architecture stands on. Modern PCs can handle it if five rules are followed: double everywhere in the simulation, floating origin, planets "on rails" via analytic ephemerides, vessels as patched conics, rendering with distance compression and chunked terrain.

### §2.1 The precision problem
The step between representable numbers (ulp) at a distance from the origin:

| Distance | float (24 bits) | double (53 bits) |
|---|---|---|
| 1 km | 0.06 mm | 0.1 pm |
| 100 km | 8 mm | 15 pm |
| 6378 km (Earth radius) | 0.5 m | 1 nm |
| 384 400 km (Moon) | 32 m | 60 nm |
| 1 AU (1.496·10¹¹ m) | 16 km | 30 µm |
| 30 AU (Neptune) | 524 km | 1 mm |

At the Moon's distance float chops space into pieces larger than the vessel itself, at 1 AU — larger than an orbital station. Time in float is also unusable: seconds from J2000 in 1957 are about −1.33·10⁹ s, the float step there is 128 s, for double — 0.24 µs.

Rules: (1) the entire simulation, orbits and ephemerides — in double (`Vector3d`, `QuaternionD`, time — double seconds); (2) float appears only at the last step — before handing the position to Unity, and only as a small difference relative to the current origin (§2.6); (3) PhysX is not used for flight (§4), so jitter from large coordinates affects only rendering.

### §2.2 Coordinate systems and units
- SI units: meters, seconds, kilograms, newtons, radians internally (degrees — only in UI and data).
- The base inertial frame — the J2000 ecliptic, right-handed: X — toward the vernal equinox, Z — toward the north ecliptic pole, Y = Z × X. All ephemerides, orbits and positions are stored in it.
- Secondary frames: parent inertial (origin at the center of the SOI body, axes parallel to the ecliptic), body-fixed (rotates per the IAU model, §2.4), topocentric ENU of the site (launch and landing).
- Unity is a left-handed system with the Y axis up. Swapping the y↔z axes turns the right-handed ecliptic into Unity's left-handed one: `Unity = (X, Z, Y)`; swapping two axes flips handedness, so the world does not come out mirrored. The conversion is done in a single place (an adapter class), quaternions and matrices are there too; this is a known source of errors (§12).
- Game time — double seconds from J2000.0 (§3.1). Start epoch: −1 333 038 686 s (4 Oct 1957 19:28:34, without the ΔT correction).

### §2.3 Ephemerides: bodies on Keplerian rails
The position of any body is an analytic function of time, not the result of integration: no error accumulation, you can jump to any date, time acceleration costs nothing, the computation is deterministic.

Planets — JPL elements (Standish, "Keplerian Elements for Approximate Positions of the Major Planets"): semi-major axis, eccentricity, inclination, mean longitude, longitude of perihelion, longitude of the ascending node at epoch J2000 plus secular rates per Julian century.

- Algorithm: `T = (JD − 2451545.0) / 36525`; elements = value at J2000 + rate·T; `M = L − ϖ`, `ω = ϖ − Ω`; Kepler's equation `E − e·sin E = M` is solved by Newton to 1e-12; coordinates in the orbital plane → rotation by ω, I, Ω → J2000 ecliptic.
- Table 1 (1800–2050) is the primary one. For dates after 2050 — table 2 (3000 BC – 3000 AD) with correction terms for Jupiter–Pluto (`M = L − ϖ + b·T² + c·cos(f·T) + s·sin(f·T)`).
- Body velocity — central difference in time (step 0.5 s; in double the error ≪ 1e-4 m/s) or an analytic derivative; needed for the SOI change (§2.5).
- The Sun is fixed at the origin of the heliocentric system (the Sun's offset from the barycenter ≈ 1 solar radius is ignored).

Earth and the Moon:
- The Standish elements are given for the Earth–Moon system barycenter (EMB). Earth = EMB − k·r_Moon, where `k = GM_Moon / (GM_Earth + GM_Moon) = 0.012150`; r_Moon is the geocentric Moon vector. Earth thus "wobbles" around the barycenter by ≈ 4670 km.
- The Moon — Schlyter elements (geocentric, ecliptic). Linear rates: the node regresses −0.0529538°/day (period 18.6 years), the argument of perigee grows +0.1643573°/day (the longitude of perigee precesses with a period of 8.85 years), the mean anomaly +13.0649930°/day (anomalistic month 27.5546 days). Epoch values — in the data table from the primary source.
- Without perturbations (evection, variation, annual equation) the Moon's position differs from the real one by up to ~1° (≈ 6700 km); acceptable for the Moon's SOI of 66 200 km. Adding the main perturbations is optional (M5, §12.2).

Moons of the outer planets: circular orbits in the parent's equatorial plane (equator orientation — per IAU, §2.4); the phase at epoch — from the data table, the period — from Kepler.

| Moon | Parent | a, km | Period, days (real.) |
|---|---|---|---|
| Io | Jupiter | 421 800 | 1.769 |
| Europa | Jupiter | 671 100 | 3.551 |
| Ganymede | Jupiter | 1 070 400 | 7.155 |
| Callisto | Jupiter | 1 882 700 | 16.689 |
| Titan | Saturn | 1 221 870 | 15.945 |

The remaining moons (Phobos, Deimos, Enceladus, etc.) are added as data as needed (M5).

### §2.4 Orientation and rotation of bodies (IAU)
For each body: the pole position `(α0, δ0)` and the rotation angle `W` — linear functions of time (following the recommendations of the IAU working group on cartographic coordinates):

`α0 = α0₀ + α̇0·T`, `δ0 = δ0₀ + δ̇0·T`, `W = W₀ + Ẇ·d`, where T — centuries, d — days from J2000.

| Body | Ẇ, °/day | Sidereal day |
|---|---|---|
| Earth | 360.9856235 | 23 h 56 m 4 s |
| Moon | 13.17635815 | 27.32 days (synchronous rotation) |
| Mars | 350.89198226 | 24 h 37 m 22 s |
| Venus | −1.4813688 (retrograde) | 243.02 days |

Earth's rotation is taken by UT1 = TT − ΔT (ΔT table; in 1957 ΔT ≈ 31 s). Lunar librations are not modeled. Earth's angular velocity of 7.2921159·10⁻⁵ rad/s gives 465.1 m/s at the equator — the basis of the gain from the launch site latitude (§6.8) and of the speed relative to the rotating atmosphere (§4.6).

### §2.5 Vessel: patched conics and spheres of influence
At each moment a vessel is in the SOI of one body and moves along a conic relative to it; the other bodies do not act on it. The sphere of influence radius: `R_SOI = a · (m / M)^0.4`, where a is the semi-major axis of the body around its parent, m and M are the masses of the body and the parent (in GM).

| Body | GM, m³/s² | Radius, km | R_SOI, km |
|---|---|---|---|
| Sun | 1.32712440·10²⁰ | 695 700 | — |
| Earth | 3.986004418·10¹⁴ | 6378.137 (eq.) | ≈ 925 000 |
| Moon | 4.9028·10¹² | 1737.4 | ≈ 66 200 |
| Venus | 3.24859·10¹⁴ | 6051.8 | ≈ 616 000 |
| Mars | 4.282837·10¹³ | 3396.2 | ≈ 577 000 |
| Jupiter | 1.26687·10¹⁷ | 71 492 | ≈ 48.2·10⁶ |
| Saturn | 3.7931·10¹⁶ | 60 268 | ≈ 55·10⁶ |
| Europa | 3.2027·10¹² | 1560.8 | ≈ 9 700 |
| Titan | 8.978·10¹² | 2574.7 | ≈ 43 000 |

(Io ≈ 7 800 km, Ganymede ≈ 24 300 km, Callisto ≈ 37 600 km. Values other than Earth and the Moon are computed by the formula.)

- Storage: the vessel state — `(r, v)` at epoch in the parent frame + the parent's GM. On-rails propagation — via universal variables (one code path for ellipse, parabola, hyperbola, with no singularity at e≈1).
- SOI change: the moment of crossing `‖r‖ = R_SOI` is found exactly (a root on an interval), the state is transformed: `r_new = r_old − r_body(t)`, `v_new = v_old − v_body(t)` (or the reverse on exit). 1 % radius hysteresis against chatter at the boundary. Time acceleration is reset before the SOI change (§3.3).
- Trajectory prediction on the map: a chain of patches (up to 5), each a conic in its own SOI with a transition point.
- Perturbations: for near-Earth on-rails orbits the secular changes of Ω and ω from J2 are included (`Ω̇ = −1.5·n·J2·(R/p)²·cos i`) — otherwise sun-synchronous orbits and "Molniya" (critical inclination 63.4°) are impossible. Other perturbations are not modeled (M5).

### §2.6 Floating origin
The vessel (active craft) is always at the Unity origin. Every frame before rendering: `Unity_position = (pos_double − origin_double)` → float. Planets, other vessels, terrain chunks get small float offsets relative to the camera. Together with HDRP's camera-relative rendering this removes jitter and shrinks errors to fractions of a millimeter in the visible volume. The chain of transformations ephemeris → origin → Unity is in one class, covered by M0 tests.

Target vessels within 2.5 km — a "physics bubble" (the same integrator, §4.1); farther — on rails.

### §2.7 Rendering distant bodies: distance compression
Distant bodies cannot be drawn in their true place: the camera far plane is limited (10⁷ m with a near plane of 1 m — beyond that HDRP loses Sun shadows, measured 01.10.2026), and Neptune is at 4.5·10¹² m. Bodies are placed in a "shell" in front of the far plane (analogous to KSP's ScaledSpace, but with a single camera). The measure is the distance to the body's horizon `H = √(d² − R²)`: the entire visible part of the sphere is closer than the horizon.

- `H ≤ H0 = 5·10⁶ m` — unchanged (an active body near the surface: from 40 km the horizon is 715 km — the terrain patch coincides with the sphere);
- `H > H0` — `H' = H0 + W·x/(x + s)`, `x = ln(H/H0)`, `W = 4.5·10⁶ m`, `s = 7`; direction is preserved, the body is scaled by `k = H'/H`, so the angular size stays true.

The function is monotonic and continuous at `H0`, the depth order of bodies is preserved, everything falls into 5·10⁶…9.5·10⁶ m. Examples from Earth: Moon → 6.7·10⁶ m, Mars → 7.7·10⁶, Neptune → 8.0·10⁶. Light, shadows and eclipses are computed in true geometry (§9.3), compression affects only the mesh position.

### §2.8 Terrain: cubesphere and quadtree
- The sphere is a cube of 6 faces, each a quadtree; a chunk is subdivided by screen-space error (target ≤ 2–4 pixels), a 33×33-vertex patch with a "skirt" against cracks and geomorphing against "popping".
- Depth up to 18 levels on Earth (≈ 1.2 m between vertices: 10 018 km / 2¹⁸ / 32), on the Moon ≈ 1.3 m at 16 levels.
- Each chunk has its own local zero: the chunk center is double, the vertices are float relative to the center. This removes the precision problem on the surface.
- Height source: M1 — procedural (fBm over the sphere) + sea level; M6 — real DEMs (SRTM/bathymetry for Earth, LOLA — Moon, MOLA — Mars). Generation — Jobs/Burst, asynchronous, ≤ 2 ms on the main thread.
- Terrain collision — not mesh colliders but a height query `h(lat, lon)` from the same source at a fixed "physics LOD" around the vessel, so that visuals and contact match (§4.8).

### §2.9 Earth's figure and gravity
A sphere of radius 6378 km would give a Baikonur height error of ≈ 11 km (the geocentric radius at 46° ≈ 6367.1 km). Therefore Earth is the WGS84 ellipsoid (`a = 6 378 137 m`, `f = 1/298.257223563`): sites, altitude above sea level and landing are computed on the ellipsoid; the geoid is not modeled. Earth's gravity is central plus J2 (`J2 = 1.08263·10⁻³`). The other bodies are spheres with a mean radius until M5, then oblateness of Mars/Jupiter/Saturn.

## §3 Time and time acceleration

### §3.1 Time scale and calendar
- Game time `t` — double seconds from J2000.0 (JD 2451545.0), TT scale. `JD = 2451545.0 + t/86400`.
- Displayed time — UTC: `UTC = TT − ΔT(t)` (ΔT table, leap seconds after 1972). A difference of ≤ 70 s does not affect dynamics; it is needed so that the start shows 19:28:34, as in textbooks.
- Calendar — Gregorian, displayed as "4 Oct 1957, 19:28:34". Month abbreviations: Jan, Feb, Mar, Apr, May, Jun, Jul, Aug, Sep, Oct, Nov, Dec.
- Mission time: "T+ 00:03:25" after launch, "T− 00:10:00" before an event.
- New game start: 4 Oct 1957 19:28:34 UTC. The game runs forward; the open horizon is until 2100 (ephemerides §2.3).

### §3.2 Acceleration levels
Two modes (§4.1): physical and "on rails".

| Mode | Multipliers | Where allowed |
|---|---|---|
| Physical | 1×, 2×, 3×, 4× | Atmosphere, thrust, landing, rendezvous (k RK4 steps of 0.02 s per tick) |
| On rails | 1×, 5×, 10×, 50×, 100×, 1 000×, 10 000×, 100 000×, 1 000 000×, 10 000 000× | Vacuum, engines off, vessel outside the atmosphere or on the surface |

One second of real time under on-rails acceleration:

| Multiplier | 1 s real = |
|---|---|
| 100× | 1 min 40 s |
| 1 000× | 16 min 40 s |
| 10 000× | 2 h 47 min |
| 100 000× | 1 day 3 h 47 min |
| 1 000 000× | 11.6 days |
| 10 000 000× | 115.7 days |

A year passes in ~3.2 s at maximum; an Earth–Mars Hohmann transfer (259 days) — in ~2.2 s; Voyager's grand tour (12 years) — in ~38 s. On rails the step is analytic, so there are no numerical limits; about 2 days can pass per frame.

### §3.3 Automatic acceleration reset
The maximum allowed acceleration is limited by the time to the nearest event: `k ≤ 6 · T_to_event` (s), i.e. no more than 1/10 of the remaining time passes per frame. A forced reset to physical (or 1×) before:

- an SOI change (the exact time of the event is known in advance, §2.5);
- atmospheric entry (periapsis below the atmosphere boundary, §4.4);
- a maneuver node and the start of a programmed onboard computer command (§6.4);
- approach to a target (< 50 km — no higher than 10×; < 2.5 km — physical mode);
- emergency events: collision with debris (§6.5), solar flare warning, overheating, loss of communication, landing.

Forbidden: acceleration above physical with thrust > 0, at altitude < 140 km above a body with an atmosphere, and on contact with the surface in the "not landed" state. A landed vessel is stationary in body coordinates — any acceleration is allowed on rails.

## §4 Flight physics

### §4.1 Two modes and the "on surface" state
| Mode | When | Method |
|---|---|---|
| Physical | Atmosphere, thrust, rendezvous < 2.5 km, ground contact | Custom RK4, step 0.02 s (50 Hz) |
| On rails | Vacuum, thrust 0, outside the "bubble" | Analytic conic, universal variables (§2.5) |
| On surface | Soft landing | Fixed `(lat, lon, height above surface, heading)` in body coordinates, rotates with the body |

The "on rails → physics" transition happens on approaching the atmosphere, on engine ignition, on rendezvous; the reverse — after engine shutdown and exit from the atmosphere onto a stable trajectory. PhysX is not used: determinism, controllable precision and double support are needed.

### §4.2 RK4 integrator
- Vessel state: position `r` and velocity `v` (double, in the parent inertial frame), orientation (quaternion), angular velocity ω, mass m (changes with consumption), stage positions.
- Right-hand side: gravity of the SOI body (§4.3), thrust (§4.5), drag relative to the rotating atmosphere (§4.6), mass flow `ṁ = −F / (Isp · g0)`.
- Fixed step 0.02 s. Throttle and orientation within a step are constant (zero-order hold). Orientation is integrated as a quaternion in the same step with renormalization.
- Choosing RK4: the error on a circular orbit at a 0.02 s step is negligible (`ω·dt ≈ 2·10⁻⁵`), the check is the test "energy drift < 1e-9 over 100 revolutions" (M0). The computation costs 4 right-hand-side calls per step, at physical 4× acceleration — 200 steps/s.
- `g0 = 9.80665 m/s²` in all Isp calculations.

### §4.3 Gravity
The SOI body is a point mass `a = −GM·r/‖r‖³`; for Earth J2 is added. Second-order gravity (the Moon acting on a vessel near Earth) is not accounted for — patched conics (§2.5). On the surface `g` depends on latitude (Earth: 9.780 at the equator, 9.832 at the pole) — this comes out naturally from the ellipsoid and J2.

### §4.4 Atmospheres
| Body | Model | p0 | ρ0, kg/m³ | T0 | Scale height | Boundary |
|---|---|---|---|---|---|---|
| Earth | US Standard Atmosphere 1976 up to 86 km, then a table | 101 325 Pa | 1.225 | 288.15 K | ≈ 8.4 km (in the lower part) | 140 km |
| Mars | exponential | 610 Pa | 0.020 | 210 K | 11.1 km | 100 km (to verify) |
| Venus | exponential | 9.3 MPa (92 atm) | 65 | 737 K (464 °C) | ≈ 15.9 km | 150 km (to verify) |
| Titan | exponential | 147 kPa | 5.3 | 94 K | ≈ 20 km | 600 km (to verify) |

Earth — boundary 140 km: above it is vacuum for the physical mode. USSA76 table values: 11 km — 216.65 K, 22 632 Pa, 0.3639 kg/m³ (control points for M0 tests). Speed of sound `a = √(γ·R·T)`. For other bodies — exponential ρ(h) = ρ0·e^(−h/H) and a temperature profile; the level of detail — as needed by the missions (§7). The atmosphere rotates with the body: air velocity is `Ω × r`.

### §4.5 Thrust and specific impulse
- Thrust and Isp are interpolated between the vacuum value and the sea-level value by pressure: `F(p) = F_vac + (F_sl − F_vac)·p/p0`, same for Isp. Propellant flow is derived from them: `ṁ = F(p)/(Isp(p)·g0)`. For "Kara-1" the stage 1 data give ṁ = 2751 kg/s at sea level and 2697 kg/s in vacuum — the ≈ 2 % discrepancy is a consequence of rounding of the published numbers; we do not correct the values.
- Throttle scales thrust and flow linearly; bounded below by `minThrottle` (§5.3).
- Ignition prohibition: vacuum stage engines do not ignite at `p > maxIgnitionPressure` (≈ 0.1 atm) — flow separation in the nozzle.
- Stage delta-v: `Δv = Isp · g0 · ln(m0/mf)`; a stage is computed with vacuum Isp for the UI and with the real pressure profile in flight.

### §4.6 Aerodynamics
- Drag force: `F_d = ½·ρ·v_rel²·Cd(M)·A`, `v_rel = v − Ω × r` (relative to the rotating atmosphere). Area A — by the midsection of the assembled vessel.
- Cd depends on Mach number: subsonic ≈ 0.30, transonic peak at M ≈ 1.0–1.2 (≈ 0.55 for a streamlined rocket), then dropping to ≈ 0.25–0.30 at hypersonic speeds. For capsules (blunt body) Cd ≈ 1.2–1.4 in hypersonic flow. Cd(M) tables are part/vessel data.
- Aerodynamic moment: the center of pressure is ahead of the center of mass — the rocket is unstable and tumbles without control; a gravity turn keeps the angle of attack near zero (M2: full moment computation, M1: drag only).

### §4.7 Destruction and heating
- Structural failure — by `Qα = q · |sin α|` (dynamic pressure × angle of attack) and by axial q. The "Kara-1" threshold is `Qα ≈ 6 kPa`: at max-Q ≈ 33 kPa this is ≈ 10° angle of attack. Values are calibrated in M2.
- Entry heating: convective flux by Sutton–Graves `q_conv = k·√(ρ/R_n)·v³`, `k = 1.7415·10⁻⁴` (SI, Earth air); skin temperature — a thermal model with a heat shield (ablation consumes mass). Exceeding the material limit — destruction.
- Crew g-load — limit by era: > 9 g for longer than 10 s — death (parameters in §8.2).
- Heating and radiation doses (§6.6) are separate "health" resources of a part.

### §4.8 Landing and surface contact
- Contact: `height_above_surface = ‖r‖ − R(lat, lon) − h(lat, lon)` from the height source (§2.8). Contact — at height ≤ 0.
- Soft (speed relative to the surface < 10 m/s, tilt < 20°): the vessel switches to the "on surface" state (§4.1), crew and equipment are intact, launching from the surface is allowed.
- Hard (≥ 10 m/s or greater tilt): crash; the vessel and mission are lost, the wreckage is an object on the surface.
- The speed limit is a property of the support: landing legs 10 m/s, shock absorber/"ball" up to 25 m/s (Luna 9), parachute capsule 8 m/s (with a retro-engine — 10).

### §4.9 Attitude and SAS
- Moment of inertia — by a cylinder: `I_axial = ½·m·r²`, `I_transverse = m·(3r² + h²)/12`. An estimate for "Kara-1" at launch: m = 528 t, h ≈ 70 m, r = 1.83 m → `I⊥ ≈ 2·10⁸ kg·m²`.
- Control torque — nozzle gimbaling `M = F·L·sin δ` (example: 7607 kN, arm ≈ 20 m, ±5° → 1.3·10⁷ N·m → ≈ 0.06 rad/s², i.e. ≈ 3.5 °/s²) and reaction wheels (small torque, by era), micro attitude-control engines (RCS).
- SAS (T — on/off, F — cycle through modes): attitude hold, prograde, retrograde, normal, antinormal, radial out, radial in; with a target selected, "toward target" and "away from target" are added. Control — a PD controller with angular rate limiting; the velocity reference frame (orbital/surface/relative to target) is selected in the HUD (§10.2).

### §4.10 Orbit decay on rails
Earth's physical atmosphere boundary is 140 km, but weak drag above it is needed for the longevity of debris (§6.5) and stations. For on-rails orbits with a perigee below ≈ 600 km a simplified thermosphere model is applied: the mean drag per revolution reduces the semi-major axis (`ΔP` per revolution from a ρ(h) table for average solar activity). The model is for decay only; flight in the atmosphere below 140 km is always in physical mode. Details — open question §12.2.

## §5 Vessels, stages, engines

### §5.1 Stage model
A stage is described by data: dry mass, propellant mass, engines (count, thrust sea-level/vacuum, Isp sea-level/vacuum, `minThrottle`, number of ignitions, `maxIgnitionPressure`), diameter, length, drag coefficient Cd(M), center of mass position, gimbal range (degrees), limits (q, Qα). A vessel is a list of stages (jettison order), payload, fairing. Structural coefficient `ε = m_dry / (m_dry + m_prop)`.

### §5.2 The "Kara-1" rocket (inspired by Falcon 9)
Two-stage. The reference for M0–M2 (debugging, vertical slice).

| Parameter | Stage 1 | Stage 2 |
|---|---|---|
| Dry mass, t | 25.6 | 3.9 |
| Propellant, t | 395.7 | 92.67 |
| Thrust sea-level / vacuum, kN | 7607 / 8227 | — / 981 |
| Isp sea-level / vacuum, s | 282 / 311 | — / 348 |
| Engines | 9 × 845 / 914 kN (per the analog) | 1 × 981 kN |
| ε | 0.061 | 0.040 |

Payload 10 t (in v1 the mass of the fairing and adapter is included in it — an assumption).

Computed characteristics (for model verification in M0):

| Quantity | Value |
|---|---|
| Liftoff mass | 527.87 t |
| TWR at liftoff | ≈ 1.47 |
| Stage 1 Δv (vacuum / sea level) | ≈ 4.22 / 3.83 km/s |
| Stage 2 Δv | ≈ 6.95 km/s |
| Total Δv (vacuum) | ≈ 11.2 km/s |
| Stage 1 burn time | ≈ 144 s (sea level) – 147 s (vacuum) |
| Stage 2 burn time | ≈ 322 s |
| Acceleration before cutoff of stages 1 / 2 | ≈ 6.4 g / 7.2 g (throttling needed) |
| Δv margin over LEO (≈ 9.4 km/s, with losses) | ≈ 1.4–1.8 km/s |

Estimate: for a trans-lunar trajectory the payload must be reduced to ≈ 3–4 t (more precisely — by calculation in the game). This is a deliberate property: the "Kara-1" rocket is not a lunar one; the lunar rocket is a separate line: "Kara-0" (R-7-like, era I), "Kara-2" (super-heavy, hydrogen on the upper stages, era II), "Kara-3" (stations and probes, eras III–IV); in era V — the reusable "Kara-1" (§12.2).

### §5.3 Engine mechanics
| Mechanic | Rule |
|---|---|
| Limited number of ignitions | The stage 1 engine — 1 ignition (for "Kara-1"; landing maneuvers — in the reusable version, §6.9), stage 2 — 3 ignitions. Exhausted → will not ignite. |
| Propellant settling (ullage) | Ignition in weightlessness requires axial acceleration ≥ 0.05 m/s² for ≥ 5 s (RCS or ullage motors), otherwise the probability of ignition failure grows in proportion to the shortfall. |
| Minimum throttle thrust | Stage 1: 40 % of a single engine (338 kN at sea level). Stage 2: 39 % (≈ 380 kN). |
| Reliability and flight hours | Probability of successful ignition/operation `R(n) = R_max − (R_max − R_0)·e^(−n/τ)`: a new engine `R_0 = 0.90`, `R_max = 0.995`, τ = 15 ignitions; n — series flight hours (flights + static fire tests). |
| Failure types | Ignition failure (nominal abort), in-flight shutdown, loss of thrust by x %, destruction (RUD). Weights — in the engine data. |

Consequence of the minimum thrust: first stage landing. Dry mass 25.6 t → weight 251 kN; one engine at 40 % gives 338 kN (TWR_min ≈ 1.35) — hovering is impossible, landing only "at the last moment" (suicide burn).

### §5.4 Builder (M3)
Assembly from parts: tanks, engines, fairings, decouplers, adapters, RCS, landing legs, parachutes, solar panels, antennas, RTG, command modules, airlocks.

- Cross-section size grid: 1; 2; 3.7; 5; 7.5; 10 m, adapters between neighboring sizes.
- Attachment nodes: axial (stack) and radial, symmetry 2/3/4/6/8; fuel transfer (crossfeed); stage order — in a list.
- Real-time indicators: mass, center of mass, Δv and TWR per stage (sea level and vacuum), center of pressure, static stability margin.
- Pre-launch checks: an engine exists, stage 1 TWR ≥ 1, a separation command exists, the vessel fits on the pad.
- Saving — a JSON tree of parts; the same format as the catalog of ready-made vessels.

## §6 Unique mechanics

Each is laid out in the scheme: essence, why the player cares, how it is implemented, milestone.

### §6.1 Space race against a historical rival
- Essence: the rival is the calendar of real history. A mission has a "world first" date (Sputnik, Gagarin, Leonov, Luna 9, Apollo 11…). If the player makes it earlier — "first"; later — "second" and below.
- Why: gives a deadline and drama without an artificial AI opponent; an era cannot be passed "someday".
- Implementation: an event list `history.json` (date, label, category). The chronicle shows "in 214 days: Vostok 1". The prestige formula for a mission with base `P`:
  - earlier than the rival: `P·(1 + min(1, days_earlier/365))` — up to ×2;
  - later: `P·max(0.1; 0.5·e^(−years_later/2))`.
  Side effects: after the rival's event, the tech tree nodes dependent on it become 20 % cheaper ("experience published"). Modes: "History" (real dates), "Accelerated" (dates shifted by −N years), "Off".
- Milestone: M4 (missions, prestige); a simple chronicle — M1.

### §6.2 Real launch windows and interplanetary windows
- Essence: the launch time determines the available orbital plane, and the positions of the planets determine the cost of the transfer.
- Why: the player waits and plans instead of "flying whenever they want"; strategy emerges.
- Implementation:
  - Near-Earth windows: inclination i ≥ site latitude; azimuth `sin Az = cos i / cos φ`, correction for Earth's rotation `tan Az' = (v_orb·sin Az − ω·R·cos φ)/(v_orb·cos Az)`; the window is when the orbital plane passes through the site (twice a day). The window for docking with the ISS is instantaneous, minutes.
  - Moon: a window once per sidereal month (27.3 days) and twice a day by plane.
  - Interplanetary: a porkchop plot — a grid of launch dates × arrival dates (≈ 200×200), in each cell a Lambert problem solution; `C3`, arrival `v∞` and total Δv are shown; contours, minima are highlighted. 40 000 Lambert solutions take one second on Burst. Synodic periods: Venus 584 days (19.2 months), Mars 780 days (25.6 months), Jupiter 399 days (13.1 months). Example: Earth→Mars by Hohmann — 259 days, `v∞ ≈ 2.94 km/s`, Δv from a 200 km LEO ≈ 3.6 km/s.
  - The Grand Tour window — ≈ 1976–1979 (§7.4); in the game it arises on its own from the real ephemerides.
- Milestone: near-Earth — M1 (azimuth calculation); porkchop — M5.

### §6.3 Engine reliability, limited ignitions, ullage
- Essence: an engine is not a perfect part (§5.3).
- Why: decisions like "one more static fire or fly?", planning upper stage restarts.
- Implementation: the `R(n)` model, an ignition counter in the engine state, an ullage check at the moment of ignition; series flight hours accumulate between vessels (test stand runs in §8.3 raise it for money).
- Milestone: M1 (ullage and ignition count), M4 (flight hours and test stand).

### §6.4 Light delay and the programmable onboard computer
- Essence: commands and telemetry travel at the speed of light `c = 299 792 458 m/s`: Moon — 1.28 s, Mars — 3–22 min, Jupiter — 35–52 min, Saturn — 71–87 min, Voyager 1 — over 20 h.
- Why: manual control of a distant probe is impossible; a program must be written in advance.
- Implementation:
  - Distance to the nearest communication station → delay `τ = d/c`; the view of a remote vessel is the "last known state" plus a forecast.
  - The onboard computer (OBC) executes a command queue: engine on, attitude, throttle, jettison, experiment, transmission; trigger conditions — time, event, altitude, true anomaly, apsis.
  - The program is uploaded through the communication channel: delay + size/transfer rate (§6.10). Limits by era (memory, number of commands, conditions): tier 1 — a timer sequencer (≤ 16 commands), tier 2 — ~256 commands with event conditions, tier 3 — a script with variables and loops, tier 4 — autonomous navigation and landing.
  - The program language — a domain-specific language (DSL) / a visual timeline; the choice — §12.2.
- Milestone: M1 — the first program (autopilot, §6.11); M5 — delay and the full OBC.

### §6.5 Kessler syndrome
- Essence: spent stages and debris remain in orbit. The collision risk grows with object density; a collision creates new debris.
- Why: the price of carelessness — lost satellites and closed orbits; a cleanup market emerges.
- Implementation:
  - Objects: tracked (>10 cm) — a list with on-rails orbits; small ones — statistics by cells "altitude (50 km) × inclination (10°)".
  - Collision rate by the kinetic theory of gases: `rate = s·v_rel·σ`, `s = N/V_shell`, `σ = π(r₁ + r₂)²`. Example: `s = 10⁻⁸ km⁻³`, `v = 10 km/s`, `σ = 10 m²` → ≈ 3·10⁻⁵ collisions per object per year.
  - Fragmentation — NASA Standard Breakup Model (catastrophic: energy/mass ≥ 40 J/g; number of fragments `N(L) = 0.1·M^0.75·L^−1.71`).
  - For readability — a "debris aggressiveness" slider 0×–100×. Calibration: Iridium–Cosmos (10 Feb 2009, 789 km, 11.7 km/s).
  - Decay per §4.10; deorbit — an obligation by era ("25 years"/"5 years" rules).
  - Cleanup missions: rendezvous, capture (manipulator, net, harpoon) and removal; paid by contract.
  - Limit: up to 20 000 individual objects, beyond that — into statistics.
- Milestone: M5.

### §6.6 Van Allen radiation belts
- Essence: charged particles in Earth's magnetosphere dose the crew and electronics.
- Why: trajectory and shielding are decisions, not decoration; GEO and medium orbits (GNSS, ≈ 20 000 km) lie in the electron belt.
- Implementation:
  - The magnetic field — a tilted dipole (≈ 10°) offset by ≈ 500 km (gives the South Atlantic Anomaly). Shell parameter `L = r/(R·cos²λ_m)`.
  - The inner belt — protons, ≈ 1000–6000 km altitude (L ≈ 1.2–2); the outer — electrons, ≈ 13 000–60 000 km (L ≈ 3–7).
  - Dose rate `D(L, B)` — a table, calibrated against historical doses: Apollo 11 ≈ 0.18 rad per crew for the mission, Apollo 14 ≈ 1.14 rad; ISS ≈ 0.5 mSv/day.
  - Shielding mass reduces the dose. Crew dose accumulates (limits by era, ≥ 1 Sv in a short time — radiation sickness, ≈ 4 Sv — lethal). Electronics: total dose (krad) and single-event upsets. Random solar particle events (SPE) with a 30–60 min warning.
  - Jupiter: at Europa ≈ 5 Sv/day (real.) — the same mechanism for the missions of §7.
- Milestone: M5.

### §6.7 Plasma communication blackout on entry
- Essence: on entry the ionized layer blocks radio waves.
- Why: the vessel is "silent" for several minutes — autonomous entry and telemetry recording are needed.
- Implementation: radio communication is lost if the electron density exceeds the critical `n_c = (f / 8.98)²` m⁻³: S-band 2.2 GHz — ≈ 6·10¹⁶ m⁻³, X-band 8.4 GHz — ≈ 8.7·10¹⁷ m⁻³. The density `n_e(v, h)` is an empirical table based on flight experiment data such as RAM-C. A typical window: speed > 4 km/s, altitude 40–90 km. Consequence: the higher the frequency, the shorter the blackout (improving communication via TDRS/relay satellites — later).
- Milestone: M2 (blackout flag), M5 (full model).

### §6.8 Real spaceports
| Site | Latitude, longitude | Rotation speed (m/s) | Min. inclination | Gain vs Baikonur | Plane change at GEO (≈ upper estimate) |
|---|---|---|---|---|---|
| Baikonur, Gagarin's Start | 45.920° N, 63.342° E | 324 | 45.9° | 0 | ≈ 2.4 km/s |
| Canaveral / Kennedy | ≈ 28.5° N, 80.6° W | 409 | 28.5° | +85 m/s | ≈ 1.5 km/s |
| Kourou | 5.24° N, 52.77° W | 463 | 5.2° | +140 m/s | ≈ 0.28 km/s |
| Plesetsk | 62.93° N, 40.58° E | 212 | 62.9° | −112 m/s | ≈ 3.2 km/s |
| Vostochny | 51.88° N, 128.33° E | 287 | 51.9° | −37 m/s | ≈ 2.7 km/s |

- Essence: the site determines the rotational bonus and the available inclinations (not lower than the latitude; without additional maneuvers). Plesetsk — polar and highly elliptical ("Molniya", 62.9°), Kourou — GEO, Canaveral — Moon/ISS, Baikonur — the historical launch site, Vostochny — the Russian line of era V.
- Why: the choice of site is part of strategy and economy.
- Implementation: site data (coordinates, orientation, azimuth restrictions, construction/rental cost); the player unlocks them in the tech tree and economy; a launch converts `(lat, lon, height, heading)` via the ellipsoid (§2.9) into the rocket state.
- Milestone: M1 — Baikonur; M4 — the other sites.

### §6.9 Reusable stages
- Essence: recovery of the first stage (boostback, entry, landing), modeled on Falcon 9.
- Why: saving money (§8.4), the challenge of a precise landing with minimum thrust > weight (§5.3).
- Implementation: propellant reserve for three maneuvers (boostback, entry braking burn, landing); the landing zone — land or a barge (coordinates); landing legs; stage damage/wear; flight hours grow (§5.3). A typical payload reduction — 30–40 % by analogy with Falcon 9 (to be calibrated).
- Milestone: M2 (landing on terrain), M4 (economy).

### §6.10 Science channel bandwidth
- Essence: science does not appear instantly but is transmitted: each experiment has a volume in Mbit; transmission is limited by the channel rate, which depends on the antenna, power and distance.
- Why: a constraint that makes different antennas and relays meaningful; a compressed "expected" data stream.
- Implementation: `rate(d) = rate_ref·(d_ref/d)²`, capped by the antenna ceiling. Benchmarks (real.): Mariner 4 — 8.33 bit/s at Mars (one photograph ≈ 8 h); Voyager at Jupiter — 115.2 kbit/s, at Neptune — 21.6 kbit/s. Onboard storage is limited: on overflow the data is lost. Ground stations — visibility zone (horizon, Earth's rotation), relay satellites at Mars (M5). Sample return gives a ×3 multiplier to science compared with transmission (§8.3).
- Milestone: M5.

### §6.11 Ascent autopilot as the first OBC program
- Essence: the first onboard computer program is the ascent program: vertical rise, pitch program, gravity turn, stage separation, insertion onto a circular orbit.
- Why: an entry point for newcomers and a reference for the program system (§6.4).
- Implementation: parameters — target orbit `(Pe, Ap, i)`, azimuth (§6.2), turn start altitude; phases: vertical until ≈ 100 m/s, small pitch, following the velocity vector (prograde hold), stage cutoff by propellant consumption, separation at apoapsis, computing the time and start of the circularization burn from Δv and acceleration. Later versions — PEG-like "explicit guidance" for the upper stage. The autopilot is one of the OBC "profiles", it can be edited.
- Milestone: M1.

## §7 Missions

### §7.1 Description format
Missions are data (JSON/ScriptableObject), not code. A mission consists of condition objectives, execution logic, penalties, reward and the rival's date.

```json
{
  "id": "venera_lander",
  "era": 2,
  "requires": ["luna9"],
  "title": "Venus: landing",
  "objectives": [
    { "id": "arrive", "type": "Orbit",   "body": "Venus", "peKm": [200, 100000] },
    { "id": "land",   "type": "Landing", "body": "Venus", "maxSpeed": 10, "after": "arrive" },
    { "id": "survive","type": "Survive", "body": "Venus", "after": "land", "seconds": 1380,
      "env": { "pressureAtm": 92, "tempC": 460 } }
  ],
  "success": "all",
  "fail": [ { "type": "Deadline", "date": "1975-01-01" } ],
  "reward": { "prestige": 400, "science": 60, "funds": 150 },
  "rival": { "date": "1970-12-15", "label": "Venera 7" }
}
```

Logic: `all` (all objectives), `any` (any one), `sequence` (by `after`). Conditions are checked by events (SOI change, landing, docking, data transmission) and by polling once a second for continuous ones. Objective states: pending, active, completed, failed. The rival's date is a day (or a moment); if there is no `rival` — the mission has no race.

### §7.2 Condition types
| Type | Parameters |
|---|---|
| `Orbit` | body; ranges of `Pe`, `Ap`, `i`, `e`; number of revolutions or time in orbit |
| `Landing` | body; maximum speed; region (lat, lon, radius); tilt |
| `Survive` | seconds; environment limits (pressure, hull temperature, dose) — a survival timer |
| `Return` | return to the surface of a body; g-load limit; dose limit; landing zone |
| `Crew` | number of people; alive at the time of the event |
| `EVA` | duration; a person outside the vessel |
| `Dock` | target; distance, relative speed ≤ 0.3 m/s, orientation |
| `Flyby` | body; altitude range; order of flybys |
| `Data` | experiment; Mbit transmitted/returned |
| `Mobility` | distance traveled; night survived |
| `Aerial` | number of flights; altitude; range (helicopter) |
| `Drill` | penetration depth (ice) |
| `Assemble` | number of modules docked to the station hull |
| `Deadline` / `NotBefore` | date no later / no earlier than |
| `Within` | no later than N seconds after an event (relative timer) |
| `Debris` | debris objects (IDs) removed from orbit |

### §7.3 Mission table
Reward: P — prestige, S — science, ₢ — money (M₢, millions of conventional credits).

| № | Era | Mission | Goal | Success conditions | Reward | Rival date |
|---|---|---|---|---|---|---|
| 1 | I | Sputnik | The first satellite | `Orbit` Earth: Pe ≥ 150 km, Ap ≤ 1500 km, ≥ 1 revolution; beacon works ≥ 21 days | P100 S5 20 | 4 Oct 1957 (game start) |
| 2 | I | Vostok | A human in orbit | `Orbit` ≥ 1 revolution, Pe ≥ 150 km; `Crew` 1, alive; `Return` Earth, g-load ≤ 9 g | P300 S15 60 | 12 Apr 1961 |
| 3 | I | Lunar flyby | Photo of the far side | `Flyby` Moon ≤ 10 000 km; `Data` ≥ 20 Mbit transmitted | P200 S20 50 | 7 Oct 1959 (Luna 3) |
| 4 | II | Spacewalk | EVA | `Orbit` Earth; `EVA` ≥ 10 min; `Crew` alive, `Return` | P250 S15 50 | 18 Mar 1965 (Leonov) |
| 5 | II | Luna 9 | Soft landing on the Moon | `Landing` Moon: speed < 10 m/s (with a shock absorber < 25 m/s); `Data` ≥ 1 panorama | P300 S30 100 | 3 Feb 1966 |
| 6 | II | Docking | Rendezvous and docking of two vehicles | `Dock`: distance < 0.5 m, speed ≤ 0.3 m/s, orientation ≤ 5°; latch | P250 S20 80 | 16 Mar 1966 (Gemini 8/Agena) |
| 7 | II | Apollo | Landing and return | `Landing` Moon, `Crew` ≥ 2; time on the surface ≥ 20 h; liftoff from the surface, docking in lunar orbit; `Return` Earth, g-load ≤ 9 g, dose ≤ limit | P1000 S100 500 | 20 Jul 1969 (landing); 24 Jul 1969 (return) |
| 8 | II | Lunokhod | Remote-controlled rover | `Landing` Moon; `Mobility` ≥ 10 km; survive ≥ 1 lunar night (≈ 14.8 days) | P250 S50 120 | 17 Nov 1970 |
| 9 | II | Venus | Landing with a survival timer | `Landing` Venus < 10 m/s; `Survive` ≥ 23 min (Venera 7) / 127 min (Venera 13, gold) at 92 atm and 460 °C | P400 S60 150 | 15 Dec 1970 (Venera 7) |
| 10 | II | Mars: landing and rover | Landing, then a rover | `Landing` Mars < 10 m/s; `Mobility` ≥ 100 m; `Data` | P400 S80 200 | 2 Dec 1971 (Mars 3 landing); 4 Jul 1997 (Sojourner rover) |
| 11 | III | Voyager: Grand Tour | Flyby of Jupiter, Saturn, Uranus (+Neptune — gold) | `Flyby` in sequence, Jupiter ≤ 600 000 km, Saturn ≤ 150 000 km, Uranus ≤ 100 000 km, Neptune ≤ 50 000 km; `Data` for each | P800 S200 400 | 25 Aug 1989 (Neptune, Voyager 2) |
| 12 | IV | ISS | Station assembly | `Orbit` i 51.6° ± 0.3°, 380–430 km; `Assemble` ≥ 6 modules; `Crew` ≥ 3 for ≥ 180 days; orbit maintenance | P700 S150 600 | 2 Nov 2000 (Expedition 1) |
| 13 | V | Reusable stage | First stage recovery | `Return` stage: soft landing in the zone; relaunch | P300 S0 150 | 21 Dec 2015 |
| 14 | V | Europa | Subglacial probe | `Orbit` Jupiter; `Landing` Europa < 5 m/s; `Drill` ≥ 20 km of ice; `Data` ≥ 100 Mbit; dose limit | P900 S300 800 | none (planned ≥ 2030s) |
| 15 | V | Titan | Helicopter | `Landing` Titan (parachute, < 10 m/s); `Aerial` ≥ 3 flights, ≥ 10 km total | P600 S250 700 | 14 Jan 2005 (Huygens landing); 2034 (Dragonfly plan) |
| 16 | V | Mars sample return | Samples on Earth | `Landing` Mars; `Data`/samples; liftoff from Mars, docking in orbit; `Return` Earth, capsule intact | P1200 S400 1000 | none |
| 17 | V | Orbit cleanup | Debris removal | `Dock`/capture of objects; `Debris` ≥ 3 objects removed | P300 S50 200 | none |

Missions 3, 13 and 17 are additions to the mandatory minimum: the race to the far side of the Moon, reusability (§6.9), cleanup (§6.5).

### §7.4 Notes on key missions
- Sputnik: the rival's date coincides with the game start. This is a tutorial mission: it counts "as a tie" if orbit is reached within a day; a separate question — §12.2.
- Venus: pressure 92 atm and 460 °C. The hull — a pressure limit (Venera 4 was crushed at ≈ 25 km, 1967), hull temperature — a lumped model `C·dT/dt = k_ins·(T_ext − T_int) − Q_cool`. Survival time depends on insulation and thermal mass (Venera 7 — 23 min, Venera 13 — 127 min); medals — by time.
- Voyager: the window — ≈ 1976–1979, repeats once per ≈ 175 years; in the game it arises on its own from the real ephemerides (Voyager 2 launched 20 Aug 1977). A missed window — the mission is effectively impossible. Acceleration of 10⁷× is needed (≈ 38 s per 12 years).
- Apollo: a chain of 6–8 objectives and 3 vessels (launch vehicle, command module, lander). Radiation (§6.6) and g-load (§4.7) are conditions.
- ISS: station fall due to decay (~2 km/month real.), maintenance — Δv expenditure.
- Europa: Jupiter's radiation (≈ 5 Sv/day), ice thickness 15–25 km, a subglacial probe (nuclear source), delay 35–52 min (§6.4).
- Titan: a dense atmosphere of 1.47 bar and low gravity of 1.35 m/s²; the helicopter flies by an OBC program (delay ≈ 80 min).
- Sample return: the longest chain — landing, collection, liftoff from Mars, docking, return; there is no real date, the rival does not constrain.

## §8 Progression: eras, tech tree, economy

### §8.1 Eras
| Era | Years | Name | Theme | Budget per year, M₢ (guideline) |
|---|---|---|---|---|
| I | 1957–1965 | First Steps | Satellites, a human in orbit, lunar flyby | 300 → 1500 |
| II | 1965–1975 | The Moon | Lunar landings, docking, probes to Venus and Mars | 3000 → 2000 |
| III | 1975–1990 | Stations and Probes | Stations, deep-space probes, Grand Tour | 1500 → 2500 |
| IV | 1990–2010 | International Cooperation | ISS, partnerships, ion engines | ≈ 3000 |
| V | 2010+ | New Space | Reusability, commercial market, Europa, Titan | ≈ 3000 + contracts |

Era change — by the calendar: the era's nodes unlock on their own when the date arrives (but cost science). The player does not have to "finish" an era to start the next.

### §8.2 Tech tree
- Node: `{id, era, minDate, cost{science, funds, days}, requires[], unlocks[]}`. The price — science, money and research time (90–720 days).
- Early access: a node can be unlocked no more than 3 years before the historical date; price ×(1 + 0.5·years_early). The player can get ahead of history but not skip an era.
- Count: ≈ 50 nodes; node cost by level — 5–20 S (1), 30–80 (2), 100–250 (3), 300–500 (4).

| Era | Example nodes |
|---|---|
| I | "Kara-0" rocket, satellite beacon, re-entry capsule, parachute, radio beacon, camera, airlock |
| II | Hydrogen stage, heavy rocket, docking port, landing module, rover, RTG, lunar return heat shield, interplanetary probe |
| III | Orbital module, high-gain antenna, OBC tier 3, gravity-assist navigation |
| IV | Ion engines, station modules, Ka-band, autonomous rover, international partners |
| V | Reusable stage, methane engine, autonomous landing, helicopter for a dense atmosphere, subglacial probe, debris collector, laser communication |

### §8.3 Economy: budget, prestige, science
| Resource | Source | Use |
|---|---|---|
| Budget (₢) | Annual budget (starts 1 January), mission contracts, commercial launches (era V) | Rockets and parts, sites, research, operations, ground stations |
| Prestige (P) | For missions and "firsts" (§6.1) | Budget multiplier: `B(year) = B_base·(1 + 0.5·tanh(P/500))`; unlocks contracts and partners |
| Science (S) | Experiments: `V_base · body_multiplier · 0.5^(repeats)`; transmission × 1, sample return × 3 | Tech tree nodes |

The 1957 starting treasury — 150 M₢, annual budget — 300 M₢. Deficit: if the balance is negative, the next year's budget is cut; the game does not end. Expenses — at the design, launch and operation stages (antennas, ground station upkeep).

### §8.4 Launch cost and reusability
An example for "Kara-1" (conventionally 60 M₢ per expendable launch): stage 1 — 36, stage 2 — 12, fairing — 6, launch operations — 6.

`launch_price = stage1/N_reuses + refurbishment(10 % of stage 1) + recovery(2) + stage2 + fairing + operations`

N = 10: 36/10 + 3.6 + 2 + 12 + 6 + 6 = 33.2 M₢ (≈ 55 % of an expendable launch). At N = 1 there is no benefit (36 + 3.6 + 2 > 36). Reliability grows with flight hours (§5.3); landing reduces the payload (§6.9).

### §8.5 Difficulty modes
| Parameter | Realism (default) | Simplified | Sandbox |
|---|---|---|---|
| Δv (Isp multiplier) | 1.0 | 1.2–1.5 ("boosted Δv") | 1.5–2 |
| q and Qα limit | 1.0 | ×2 | off |
| Radiation, Kessler, plasma | on | configurable | off |
| Communication with delay | on | no delay | off |
| Engine reliability | on | ×0.5 failure probabilities | off |
| Economy | on | on, budget ×2 | unlimited money |
| Ascent autopilot | era I | immediately | immediately |

The scale (1:1) does not change in any mode.

## §9 Graphics

Realistic graphics on HDRP: a physically correct sky, physical light units, real eclipse geometry.

### §9.1 Pipeline
HDRP, physical light units (the Sun at 1 AU ≈ 127 000 lux, inverse-square of distance: at Mars ≈ 0.43 of Earth's). Camera mode — camera-relative. Reversed-Z, near/far plane 1 / 10⁷ m (§2.7). Quality: Low/Medium/High/Ultra — sky sample count, chunk distance, shadows.

### §9.2 Sky and atmosphere
- Physically Based Sky in "space" mode: the atmosphere is visible from orbit (Earth's limb, a sunset from space). For this, in the embedded HDRP configuration package `PrecomputedAtmosphericAttenuation = 0` is set: the precomputed attenuation of light by the atmosphere (designed for an observer near the surface) is disabled, the atmosphere is computed per pixel — otherwise the view of the planet from space is wrong. The change lives in a local copy of the configuration package (a risk on HDRP update — §12).
- The planet center and radius are updated every frame (double → relative float offset from the camera, §2.6). The sky profile switches on SOI change (Earth/Mars/Venus/no atmosphere).
- The starry sky — a procedural cubemap as space emission (Space Emission): distribution of stars by magnitude and the Milky Way band; later — a real star map (Hipparcos/NASA).
- Without an atmosphere (the Moon etc.) — only space emission and the Sun. Mars, Venus, Titan — their own scattering parameters (M6).

### §9.3 Light, eclipses, exposure
- Eclipses: sunlight is attenuated by the overlap of the angular disks of the Sun and the occluding body (in true geometry, without the §2.7 compression); the penumbra gives a partial eclipse; orbital night at Earth is up to ≈ 35 min.
- Auto-exposure: three profiles by situation with a smooth transition (dark → bright 0.5 s, bright → dark 1.5 s):

| Situation | EV100 (guideline) |
|---|---|
| Day at the surface/in the atmosphere | 13–15 |
| Orbit in sunlight | ≈ 15 (fixed) |
| Night/shadow, starlight | from −5 to −2 |

  The EV range is limited to `[−5, 16]`, on the map — fixed exposure.

### §9.4 Planets: textures
- Stage 1: procedural placeholder textures (color by latitude and height, noise, night lights for Earth), without loading external data.
- Stage 2 (M6): real NASA textures — Blue Marble (Earth), LROC/LOLA (Moon), MOLA/Viking (Mars), Magellan (Venus), Cassini/Voyager/Juno (gas giants and moons). NASA materials are generally not copyrighted (public domain), but each source is checked against its usage rules.
- Loading external data — only with the project owner's explicit consent; heavy files — via Git LFS or an external folder; in the build — compressed formats with mip streaming.

### §9.5 Effects
- Engine exhaust (VFX Graph): the plume shape depends on ambient pressure (expansion in vacuum), brightness — on thrust and throttle; shock "diamonds" — optional.
- Entry heating: skin glow (emission by temperature, §4.7) and a plasma trail; a condensation cone at M ≈ 1.
- Stage separation, parachutes, dust on landing (M2+), clouds — M6 (HDRP volumetric clouds are designed for Earth, adapting to other bodies — research).

### §9.6 Map
A separate view (key M): conic orbits (up to 256 points per revolution, hyperbolas — up to the SOI boundary), Ap/Pe, nodes (ascending/descending), SOI changes, maneuver nodes (Δv vectors), closest approach points, porkchop plots (M5). The map scale is its own, without the §2.7 compression; focus on a body/vessel, fixed exposure.

## §10 Controls and interface

### §10.1 Keys
| Key | Action |
|---|---|
| W / S | Pitch down / up (nose) |
| A / D | Yaw left / right |
| Q / E | Roll left / right |
| Shift / Ctrl | Throttle up / down |
| Z / X | Throttle 100 % / 0 % |
| Space | Next stage |
| T | SAS on/off |
| F | Cycle SAS modes (§4.9) |
| M | Map / vessel view |
| `.` | Time acceleration up |
| `,` | Time acceleration down |

Binding — via the Unity Input System, keys are remappable. A proposal, not fixed: `R` — RCS, `V` — switch the velocity reference frame, `C` — camera mode, `F5`/`F9` — quicksave/quickload.

### §10.2 Interface v1: IMGUI HUD
A text panel (IMGUI), without a graphical navball. Contents:

| Field | Description |
|---|---|
| Altitude | Above sea level and above the surface (radar altimeter) |
| Speed | Orbital / surface / relative to target (switchable) |
| Ap / Pe | Apoapsis and periapsis altitudes + time to them |
| Stage Δv | Remaining in vacuum, for the current stage and total |
| TWR | Current, at sea level and in vacuum |
| Date | "4 Oct 1957, 19:28:34", T+ |
| Acceleration | Multiplier and mode (physical / on rails) |
| q | Dynamic pressure, kPa; Qα |
| Mach | Mach number |
| Throttle, SAS, mass, propellant | Indicators |

Scale — by DPI; a font with Cyrillic support is mandatory (the game is localized in Russian).

### §10.3 Next
UI Toolkit (v2): navball, maneuver planner, onboard computer window, porkchop, race chronicle, tech tree and economy menus. Saves — JSON: time, vessel states (r, v, orientation, masses), tech tree, economy, history events; the format is versioned. Tutorial — a series of scenarios (first launch, orbit, Moon).

## §11 Roadmap

| Milestone | Content | Exit criterion | Status |
|---|---|---|---|
| M0 | Simulation core: bodies and ephemerides, orbits, universal variables, SOI, atmosphere, rocket, RK4; tests without Unity | The tests below are green; "Kara-1" in a headless run reaches a ≈ 200 km circular orbit | In progress |
| M1 | Vertical slice: launch from Baikonur → orbit → map → time acceleration | A play session without crashes: launch, autopilot, orbit, map, 1e7× acceleration without jitter | Planned |
| M2 | The Moon: SOI, landing, return, atmospheric entry | Landing and capsule return; q/Qα; communication blackout (flag) | Planned |
| M3 | Rocket builder | A vessel built from parts per §5.4 flies in M1/M2 | Basic: hangar, catalog, symmetry, stages, JSON, launch |
| M4 | Career: missions, race, economy | Missions 1–10, prestige, budget, engine flight hours | Planned |
| M5 | Interplanetary flight and unique mechanics | Porkchop, OBC, Kessler, Van Allen, science channel; missions 11–17 | Planned |
| M6 | Production-level graphics | NASA textures, clouds, ocean, effects | Planned |

### §11.1 M0 tests (without Unity)
- Kepler: round trip between `(r, v)` and elements, error < 1e-9 (ellipse, hyperbola, e≈1).
- Ephemerides: Earth at J2000 — distance to the Sun ≈ 0.9833 AU; Moon — period 27.32 days; cross-check against JPL Horizons control dates.
- Universal variables: forward and backward propagation, energy conservation.
- RK4: circular orbit energy drift < 1e-9 over 100 revolutions; step 0.02 s.
- Atmosphere: USSA76 — 0 km (101 325 Pa, 1.225 kg/m³) and 11 km (216.65 K, 22 632 Pa, 0.3639 kg/m³).
- Rocket: mass, TWR, Δv (11.2 km/s), burn times (144–147 and 322 s) — per §5.2.
- WGS84 ellipsoid: latitude/longitude/height ↔ ECEF conversion; Baikonur — radius ≈ 6367.1 km.
- Coordinate adapter (ecliptic → Unity): orientation without mirroring; rotation by a known angle.
- Headless run: a "Kara-1" gravity turn from Baikonur to orbit.

## §12 Risks and open questions

### §12.1 Risks
| Risk | Probability / impact | Mitigations |
|---|---|---|
| Precision and jitter at 1:1 scale | Medium / high | double everywhere, floating origin, chunks with a local zero, adapter tests; measurement at M1 |
| Terrain performance | High / medium | Quadtree with LOD, Jobs/Burst, a 60 fps frame budget (simulation ≤ 1 ms, terrain ≤ 2 ms, rendering ≤ 10 ms, UI ≤ 0.5 ms, reserve ≈ 3 ms), chunk cache, distance tuning |
| Difficulty for newcomers | High / high | Tutorial, ascent autopilot (§6.11), simplified modes (§8.5), a clear HUD |
| Realism versus "fun" | Medium / medium | Optional simplifications (boosted Δv etc.), realistic mode by default |
| Content volume (missions, parts, history) | High / high | Data instead of code, priority to missions 1–10, M5/M6 — on a leftover basis |
| Z-buffer depth and shadows with a 10⁷ m far plane | Resolved / — | The §2.7 shell (far 2·10⁸ broke HDRP shadows) |
| Editing HDRP Config (`PrecomputedAtmosphericAttenuation`) in the local package | Medium / medium | Document the change, check at every Unity/HDRP update |
| Handedness and rotation order (ecliptic → Unity) | Medium / medium | One adapter, tests, visual check of the Sun/north orientation |
| Moon ephemeris error ~1° | Low / low | Acceptable for the 66 200 km SOI; optional perturbations (M5) |
| Rights to data/textures | Low / medium | Public domain only (NASA), check usage rules, download by consent |
| Numerical stability at 1e7× acceleration | Low / high | Analytic rails, events in a queue, tests on long trajectories |

### §12.2 Open questions
1. The rival: a single "historical" one (who is first in the world) or two factions (USSR/USA)? What to do with the "Sputnik" mission, whose date coincides with the game start (§6.1, §7.4)?
2. The "Kara-1" anachronism: keep it as a debug rocket and hide it in era V, or make it the starting one with "vintage" parameters? The composition and parameters of "Kara-0/2/3" (§5.2).
3. Orbit decay above 140 km (§4.10): a simple thermosphere table, account for solar activity?
4. The onboard computer program language (§6.4): DSL, visual timeline, a C#/Lua subset?
5. Moon accuracy: add Schlyter perturbations (M5)? For lunar windows (error up to 6700 km) — is a correction on the map sufficient?
6. Ephemerides after 2050: the transition between Standish tables (a jump), does the game remain limited to 2100?
7. Budget and "conventional credits": tie to dollars (era V) or an independent scale with inflation?
8. A two-layer camera or a single one with reversed-Z — to be decided by measurement in M1.
9. Plasma model: how detailed should it be (by v and h, by body shape)?
10. No multiplayer/mod scenarios are planned; revisit after M4?
