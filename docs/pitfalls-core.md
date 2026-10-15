# Pitfalls: core, physics, autopilots

Read when editing `Core/` and the flight game logic (`VesselView`, `FlightInput`, `FlightHud`).

## Model and atmosphere
- **ISA-1976 altitudes are geopotential**: 22,632 Pa at 11 km geopotential, not geometric.
- **Draft in water** — `FlightPhysics.Draft`: the fraction of the hull sphere's volume m/(ρV); without it the capsule sat on the water surface like a ball.
- **A parachute without reefing gave 40 g** (Vostok). Reefing: 3 % of the area for 1 s, hold 4 s, opening 4 s
  (`FlightPhysics.ChuteReef*`) → 8.5 g.
- **A stack without SAS falls apart at max-Q — this is correct**: nose ahead of the CoM, α 6° at q 39 kPa → aerodynamic breakup;
  SAS Prograde holds α ≤ 1° (test `stability`). **An unguided rocket needs stabilizers**: "Kara-G" without
  `FinArea` tumbled (karman: q 14.7 kPa, α 16°) — with `FinArea = 3` it flies.
- **Damage is off by default** (`FlightPhysics.AeroBreakup/HeatDamage/GLoadLimit`, from `GameBootstrap` and the Esc menu);
  the core tests enable both — autopilots are checked against the strict rules.
- `Vessel.AngleOfAttack` is in **degrees** (Qα in the H panel used to be multiplied by Rad2Deg a second time).
- Keys: W — nose to +X of the body axes, S — to −X, D — to −Z, A — to +Z; on the pad X is north, Z is west
  (`PlaceOnSurface`), so D = east, as in KSP; at the start the camera looks north (east is on the right).

## Separation and stages
- **Separation: `Vessel.Position` is the CoM**, the view draws sections around its own CoM. `Split` put both parts at the old shared
  CoM → stage I ended up inside stage II (20 m offset). Now each part sits at its own CoM (+ ω×r), with a kick and recoil
  (test `separation`: error 0.0000 m, impulse 4e-14).
- **A one-piece fairing is "pass-through"**: a 0.5 m/s kick against ≈15 m/s² of the running stage II — the rocket passes through 13 m
  of the shell in ≈1.3 s. Jettison splits it into two halves (`Vessel.FairingHalf`, ±2 m/s sideways, tilt-out 0.2 rad/s; the bottom moves
  outward at 0.57 m/s). Debug: `FlightDebug.Lift(alt, up)` + `Stage()`.
- **Closest points of parallel capsules**: in `ClosestPoints` (Ericson) with parallel axes the denominator → 0, and a point
  on the end face was taken — the collision impulse went into rotation, and the boosters converged after the "bounce" (test `collide`: −0.75 m/s).
  For parallel axes — the middle of the projection overlap; now +0.05 m/s (they diverge), impulse conserved.
- **Side blocks are separate boosters**: give each its own CoM (`Layout` + `RadialOffset`) and ω×r, otherwise
  the "Semyorka" loses the symmetry of liftoff separation; check the impulse with the `craft` test (7e-14).
- **The TDU (braking engine) is armed by the separation of stage II** (`Separate(1, igniteNext)`), otherwise there is an extra step during braking.
- **`Vessel.RemainingStats` counted only running engines** → a shut-down stage before landing "had no Δv",
  and the landing planner could do nothing. Now: armed + fuel + (running or has restarts).
- **Only one section is ignited from the pad** (`Ignite, 0`); "block + boosters" bundles — a section with `EngineCount`.
- **Arming ≠ ignition**: `Separate(i, igniteNext)` only arms the next one; without restarts (`Ignitions`) or
  without fuel the stage is skipped, a spare step (fallback stage) is needed, otherwise the sequence stalls.
- **The "burned out — separate" rule** fired on the braking stage (retro of "Mercury"/"Gemini"): it was separated
  with full tanks. Burnout applies only to a stage that has been running.
- **Solid-fuel upper stages (Juno I, "Baby Sergeant")** cannot be shut down or restarted: ascent → coast
  to apocenter → kick with the cluster. `Guided` steering pitched the nose up at launch — `KickLoftTime` (time-based kick lift).
  The 237 km target did not close (−80 m/s short of circular), 200 km — orbit ✅.
- **Lunokhod** (`SectionDef.Rover`): on the ground — `FlightPhysics.DriveRover`, input `PilotInput.x` forward (+Z of the craft),
  `.y` turn; any pilot input cancels the autopilots (`pilot.sqrMagnitude > 0`). On the ground the `Recenter` anchor is the
  landing point, otherwise the floating origin carries the rover away.

## Rails and autopilots
- **After an SOI change on rails recompute `WarpLimitTime`** (`AdvanceRails`), otherwise time warp jumps over the moment
  of ignition planned already in the Moon's sphere.
- **The node autopilot burned through restarts**: set the `started` flag at the start of `Update`, otherwise each ullage cycle counted as a new ignition.
- **The farside flyby of the Moon (farside)** ended up in darkness: needs a launch window by plane (`LaunchWindow`) +
  a transfer time limit `maxTransfer` of 2.5 days.
- **Landing on the Moon**: the minimum TDU thrust (25 % of 16 kN) exceeds the lunar weight — hovering is impossible. Scheme: RK2 prediction
  (step 0.5 s) + bisection of the ignition moment so that a speed of 40 m/s is reached at the "gate" ≈136 m; then a terminal
  constant-deceleration law of 6 m/s², the last 3 m — free fall (touchdown 3.3 m/s). Ignition at 51 km at
  2485 m/s, 717 kg left.
- **Landing gate by thrust** (`GateAltitudeFor`): the weak LM DPS (aMax ≈ 5.3 vs 6 + 1.62 required) did not hold the
  136 m gate — impact of 17–19 m/s with 3.9 t of propellant. Deceleration = min(6, 0.6·(aMax − g)) → LM gate ≈ 370 m, soft touchdown.
- **Braking on a flyby (hyperbolic) trajectory**: the craft is still climbing — the prediction is non-monotonic in throttle,
  bisection picked 10 % and "Surveyor" went up to 172 km. While `r·v > 0` — full throttle.
- **A long burn (translunar injection, LOI) along a fixed direction lost up to 60 m/s** (apogee 227–323 thousand km instead of
  380): a burn > 2 % of the period is flown in the axes of the current orbit with an energy cutoff (`NodeAutopilot`). The residual miss is fixed
  by a mid-course correction (test: tolerance 300 km on periapsis).
- **LM liftoff from the Moon with the Earth pitch program** burned the entire APS: without an atmosphere — go straight to `Guided`.
- **`Ascend` returns true even when the fuel runs out** — check the orbit, not the flag.
- **Freedom 7 (Redstone)**: vertical ascent gave an apogee of 385 km and 14 g on descent (death). Tilt of 10° +
  `LimitAoA` (breakup at α 9°) + apogee cutoff at 180 km → 181 km and 11.1 g as a short peak.
- **Tests steer via `SasHold`**: `FlightControl.Update` zeroes `TorqueCommand` every step.
- **Transfer to the Moon: `TransferPlanner.Miss` is the approach without the Moon's gravity, not the periapsis.** An aim distance of 1847 km
  at v∞ ≈ 1 km/s focuses into an impact (predicted Pe −1488 km, apollo8). `LunarAutopilot.PlanToPeriapsis` selects
  the aim distance b = rp·√(1+2μ/(rp·v∞²)) from the `PatchedConics.Predict` prediction (it needs `node.Remaining`).
  The state in `PlanIntercept` is taken at time t0 — propagate it along the orbit, otherwise an error of hundreds of km.
- **Iterating the aim b by the prediction does not converge** (ManeuverAutoPlan, 03.10.2026): b 4211 → Pe 518 km, b 3718 → −200 km —
  the flyby side flips. What works: one `PlanIntercept`, then a descent over (prograde, normal) directly on
  `PatchedConics.Predict` until |Pe − target| < 50 km: 91 km in 580 ms. Step 0.5 m/s (1 m/s ≈ 1000 km of periapsis at the Moon).
- **Mission autopilot under time warp**: physics is allowed up to ×10 (`MaxPhysicsWarp`), beyond that — rails with the
  `WarpLimit` limit up to the nearest event (node, SOI, atmosphere entry). apollo8 hands-free: 9640 frames, maximum ×100000.
- **Braking at the Moon of ~800 m/s takes minutes and lowers the periapsis**: 34 × 109 km instead of 110 × 110. Fine-tuning
  `Trim` with two impulses at the apsides → 109.8 × 110.4 km.
- **The Apollo lunar orbit is retrograde (i 176°).** LM liftoff to the east (azimuth 90) gave i 4° — the CSM plane
  was off by 172°, rendezvous min 90 km. An azimuth alone is not enough: guidance takes the direction from the horizontal
  velocity, and at launch this is the Moon's rotation (+4.6 m/s to the east) — with azimuth −90° the same i 4.29° resulted. The solution is
  `AscentAutopilot.AimAtPlane`: thrust by the shortfall to circular in the target plane → i 163.21° with the CSM at 163.20°.
- **The docking autopilot without a node (Plan phase) did not block rails**: it plans only in physics, and at ×6 it was never
  called — two days wasted. `Universe.RailsBlocker` → "rendezvous being planned". After that — docking in 2.6 h, Δv 38 m/s.

## Hints
- After splashdown `Splashed` ≠ `Landed`: without a separate branch in `FlightHud.Tutor` it again says "2. Vertical ascent".
- The HUD font has no ⌫ — write key labels in words ("Bksp").
- **Ullage waited forever** (02.10.2026): the fuel settling threshold `FlightPhysics.SettleAccel` was 0.05 m/s², while the RCS of the full
  stack is ≈ 13 kN on 500 t = 0.026 m/s² — the fuel did not settle, after the "Above the Moon 15 km" cheat the landing autopilot waited until
  impact. Threshold 0.01 (like real settling solid motors of the S-IVB / Block D: 0.01–0.1). Test `moondrop` (since 03.10 — only the E-6 station, see "Lunokhod ramp and R-7"): lands at 3.3 m/s.
- **The "cutoff → impulse at apocenter" tutorial led to a dead end**: Block E, Agena and others have a single ignition — after cutoff
  orbit insertion is impossible. The cutoff is hinted only if `CanIgnite`, otherwise "Insertion" without shutdown
  (law `AscentAutopilot.GuidedSin` / `CircularizeSin` — shared with the autopilot).

- **Small corrections are not executed**: `NodeAutopilot` closes a maneuver when the remainder is < 0.1 m/s, and a perigee correction from
  the edge of the Moon's sphere is fractions of m/s over kilometers. Solution (`MissionAutopilot.FixPerigee`): a correction at SOI exit + a late one
  `LateFixLead` = 3 h before perigee (there 1 km of perigee ≈ 0.1–0.3 m/s), do not place maneuvers < `MinFixDv` = 0.1.
- **The Apollo entry corridor is narrow**: perigee 45.9 km → entry −6.85°, 8.0 g — alive; 41.1 km → −7.02°, 9.2 g for longer than 10 s —
  the crew died. Hence `ReturnTolerance` 1.5 km (was 5). auto_apollo11 after the fix: 47.7 km, −6.78°, 8.4 g, splashdown 6.2 m/s.
- **`Await` instead of `WaitUntil` in passive flight** overshot by ~11.6 days (waited for an event that had already passed). For
  time-based holds — only `WaitUntil`. After undocking `Flipped` stays true — check `Attached && Flipped`.

## Suspension, lunokhod, mission autopilot (03.10.2026)
- **Landing leg/wheel suspension — `Vessel.Suspension` on top of the anchor** (`FlightPhysics.StepSuspension`): 1.5 Hz, ζ 0.6, travel 0.5 m.
  Touchdown passes the vertical velocity into `SuspensionRate`; terrain height differences under the lunokhod go into `Suspension`. The explicit scheme
  is stable only when ω·dt ≪ 1 — at ω·dt > 0.5 (large step) the suspension is zeroed, otherwise it blows up.
- **Lunokhod rollout is a ramp profile, not a teleport.** Previously, after separation on the ground the lunokhod's anchor stood on the landing-stage deck, and the
  first `DriveRover` step put it on the terrain — a 2.3 m jump. Now `Vessel.RampDeck` (deck height, computed in
  `Recenter` at separation) and `RampTravel`: axle heights are taken from the ramp profile (`FlightPhysics.RampHeight`), turning is locked
  until both axles are off. Test `luna17`: deck 2.34 m, rollout 6.1 m in 11 s, max height step 3.2 cm.
  Deck 2.34 m, not 1.9 (landing-stage height): the landing offset of `CheckContact` = Com·cos + Radius·sin — with a tilt at touchdown
  the Ø 4 m stage "hangs" on the edge by ~0.4 m.
- **The mission autopilot (Y) is cancelled only by Y**: `Universe` does not reset it on `PilotInput` (`Mission == null` in the condition),
  and `FlightInput` swallows steering/throttle/stages while it is on and prints a hint. M and time warp `. , /` work.
- **"Luna-17" lands from orbit, not directly** (`MissionDef.LunarOrbit/LunarPerilune`, `MissionAutopilot.LunarOrbitFirst`):
  85×85 km → periapsis 19 km → landing. A direct descent is correct only for "Luna-9" and "Surveyor-1".
  Pitfall: the "already landed" check is `V.IsLanded && V.Body == moon`; a bare `!V.IsLanded` when starting from Earth skipped the entire transfer
  (auto_luna17: 3 frames and "mission on Earth").

## Lunokhod ramp and R-7 (03.10.2026)
- **On the ramp the lunokhod was turned sideways**: the 30° hull tilt on the rail gave a false yaw when projecting the nose onto
  the horizon plane. The heading is restored by an exact inverse rotation: `FromToRotation(v.GroundUp, up) * nose`, then
  projection. On the ramp `turn = 0` — turning is locked until both axles are off (`luna17`: 9 ok).
- **R-7 with side blocks** is a radial group (`R7Boosters`, RadialCount 4, RadialParent 0), not a lower section as with
  Atlas. Order: `Ignite(1)` + `Ignite(0, withPrevious)` → `Separate(1)` when the side blocks are spent (`NextDropsSpentRadial`) →
  `Separate(0, igniteNext)`. "Sputnik" without a stage III: block A at end of burn 10.2 g (RD-108 cannot throttle, payload 84 kg) —
  historical, the crewed "Vostok" is not threatened (block E).
- **The `separation` test did not know about side debris**: it compared the debris position along the stack axis and failed by exactly RadialOffset
  (2.965 m). Debris of a radial group is looked up by project name (`RadialPiece` gives Name = group name), the axial
  error and the lateral offset = RadialOffset are compared.
- **Stack aerodynamics by maxR**: `MassProperties.maxRadius` includes the RadialOffset of the side blocks (R-7: 4.3 m),
  and the frontal area π·R², the nose arm 2R and the side L·2R were computed as a Ø8.6 m cylinder — "Molniya" broke up at T+61 (q 30 kPa, α 8°).
  Now `FlightPhysics.AeroAreas`: the hull uses `HullRadius`, the side blocks use their own end faces, no more than two are visible from the side.
  maxR remains only for ground contact and draft. Without radial groups the areas are as before.
- **The R-7 stack does not land on the Moon** (the "15 km" cheat + autopilot): RD-107/108/0110 cannot throttle or restart,
  thrust is 5–10 lunar weights — on the terminal leg they flame out, the stages are dropped, impact 17 m/s. This is physics, not a bug:
  the `moondrop` test lands the E-6 station after dropping everything below it.
- **Translunar injection by the Block L ran on the "short" law**: `NodeAutopilot.Plan` was built on the first tick, while Block I is active
  without restarts (BurnTime = ∞) → energyMode is off forever; after the block was dropped the plan was not rebuilt.
  Apocenter 271 thousand km instead of 364, correction did not save it. Now on ∞ the plan is reset and rebuilt (luna9 ✅).
- **A dead active craft freezes in the non-rotating axes**, while the explosion/debris of `BlastEffects` stand in the body axes —
  the camera "flew away" from the death site at ≈ 400 m/s (Earth's rotation). `Universe.PinWreck` attaches the death site to the body
  (`Vessel.WreckLocal`, by `Body.Orientation` of the same frame as the explosion) and carries it along with the body (03.10.2026).

## Real height maps (04.10.2026)
- **`TerrainSettings.Amplitude` is not only the noise scale but also the "above any mountains" bound**: CheckContact (1.5·A),
  `LandingAutopilot.PredictGate` (1.5·A + 100), `RailsFloorRadius` (1.5·A + 2 km). With the Olympus map of 21.2 km and
  the former A = 7 km for Mars (bound 10.5 km), touchdown on a slope would not have been checked. `ApplyHeightMap` sets
  A = MaxWithDetail/1.5; if you change the map or DetailGain — the bound is recomputed automatically.
- **Height maps — before the first `Terrain.Height`**: the spaceport basin caches `BasinDrop` in the static `Terrain.Sites`
  for the whole session. Setting a map afterwards — the site is leveled to the old terrain.
- **The EarthLand mask counts Arctic sea ice as land**: raising "land lowlands below 0" to +1 m without a latitude cutoff
  raised 5.97 % of texels (Arctic Ocean); with |latitude| < 60° — 0.13 % (Qattara, Caspian lowland, polders).
- **ETOPO 2022 "surface" gives lake bottoms, not their surface**: Caspian −667 m (water by mask — stays a sea), Baikal,
  Ladoga and the Great Lakes — dry basins. Via OPeNDAP row 0 is south (lat[0] = −89.99), flip it.
- **0.1° is too coarse for sites on a spit — "Cape Canaveral on water"** (05.10.2026): bilinear sampling of the global map at LC-39A
  −4.0 m, at SLF −0.2 m (an 11 km texel blends the spit with the ocean and the lagoon). Raising land `landW` around the site (up to 9 km)
  lifted everything within a radius to ≥ 3 m — the site stood on an "island" in the middle of the sea. Now inserts of ETOPO 2022 15″ (≈460 m)
  1.5°×1.5° around all Earth sites and runways (`Data/EarthPatches.bytes`, 6 inserts of 360×360, 1.56 MB,
  `bake-dem.py patches`): raw terrain 39A +2.3 m, SLF +1.1 m, ocean 4 km to the east −6 m → water; insert edge
  (blending `DemPatch.FadeDeg` 0.2°) — a drop of ≤ 2.8 m per 200 m. Inside an insert `landW` is off, and leveling does not
  pull water deeper than `Terrain.PatchSeaCut` (−2 m) farther than `PatchPadCore` (300 m) from the pad.
- **ETOPO 15″ near the coast gives −1 m on land** (stitching of land and bathymetry): 1–2 km S/E of 39A the raw values are −0.7…−1.3 m.
  So the `patches` test checks land by the final `Terrain.Height` (≥ 1.0 m), not by the raw terrain.
  `bake-dem.py` prints ″ to the console — without `PYTHONIOENCODING=utf-8` it crashes on cp1251.
- **Fine terrain noise on the Moon**: slope over a 20 m baseline — mean 4.3°, maximum 39° (20,000 points); landings of
  luna9/surveyor1/luna17/apollo11/moondrop touch down at 3.3–3.5 m/s, same as on procedural terrain (3.3–3.4).
- **Rails epoch in `AdvancePhysics`** (04.10.2026): a craft outside physics went onto rails with the `Position` of the frame start, while
  the orbit epoch was taken from `Time` — already the end of the frame. The departed craft jumped a frame ahead along the orbit: undocking at
  300 km, frame 0.1 s — the target at 773 m (7.73 km/s × 0.1) and "drifts away" at 1.2 m/s instead of 0.3; re-docking 404 s.
  Now `MovePassive(..., since: t0)` → `EnterRails(v, epoch)`: 21 m in 10 s, docking 24 s. Test `undock`.
- **`Split(mask, dv)` is the velocity of the departing part, not the separation rate**: the remainder gets recoil dv·md/mv, the separation rate is
  dv·m/mv. A full S-IVB with the LM (120 t) from the CSM (30 t) separated at 1.6 m/s instead of 0.3 — `Vessel.Undock` recomputes dv.
- **A station is a section of the same design** (04.10.2026, `StationMissions.cs`): `CanDock` requires `a.Design == t.Design`,
  so the ISS is the last section of the "Soyuz"/"Dragon" rocket; `StationSetup.Place` cuts it off into a separate craft in a circular
  orbit whose plane passes over the launch site on the northbound pass `StationLaunchDelay` after launch.
- **`StageUntil` and inapplicable steps**: it looks at the raw `Sequence` index, while `Undock` is inapplicable before docking and
  is skipped — the stage "slipped" further. In the station script — `StageBefore(idx)` by `NextApplicable`.
- **Leftover `RcsTranslate` after capture**: `Universe.CheckDocking` clears `Docking` but not the RCS command, and the RCS thrust of the stack is
  the sum over sections (with the ISS 2·10^5 N). In 10 min the "Dragon"+ISS stack accelerated by ~120 m/s (420×420 → 419×862 km),
  on deorbit entry −2.66° and crew death at 9.3 g. Fixed 04.10 in `CheckDocking`: after `host.Dock` the RCS command of both craft is zeroed.
- **The `Undock` step vs the V key**: the step pushes the departing station with UndockPush with recoil to the ship of 0.3·M/m — from the 420 t ISS
  the 11 t "Dragon" gets +10 m/s (apogee +40 km). The script undocks with `Universe.Undock()` (mass-corrected).
- **Ballistic entry: g-load from the angle at 140 km** (capsules without lift): −1.10° → 8.6 g ("Voskhod-2"),
  −1.30° → 11.1 g (0.5 s above 9 g), −1.37° → 8.3 g for "Dragon" (DragScale 1.3), −1.42° → 11.2 g, −2.66° → 9.3 g for 10 s = death.
  Deorbit perigee from LEO — `StationEntryPerigee` 50 km (with 30 km for "Soyuz" it was 11.2 g).
- **"Freedom 7": death "at touchdown" in the core is reproduced only by g-load** (05.10.2026). The core lands the capsule softly in all
  variants: pitch program and cutoff at 181 km → splashdown 6.9 m/s (lat 28.40 lon −76.00, depth −5 km);
  vertical without a program → apogee 387 km, land at Canaveral h 7.4 m, 1.7–6.9 m/s; late chute arming
  (3 km / 2 km / 1.2 km / 0.8 km at 135–155 m/s) and a 1 km setpoint — also intact; ×1 and ×10, damage on/off.
  "Redstone" separates by 10 m and falls before the capsule (630–740 m/s), no collisions. The only death is
  vertical flight with the g-load rule: entry from 387 km gives a peak of 21 g (> CrewGLimit 9 g for longer than 10 s),
  `CrewLost` is set at entry, while the "Crew died of g-load" failure is written by `MissionTracker` only at touchdown.
  Symptom: the log has "Crew died: g-load …" and no "Surface impact". Regression — `freedom7` checks
  intact/Splashed/chute/touchdown < CrashSpeed.

## Winged vehicles and parametric parts (05.10.2026, `Aerodynamics.cs`, `MissionAutopilot.Winged.cs`, `PartCatalog.Resolve`)
- **An autopilot frame without `WarpLimit` jumps to ×1e7**: an unrestricted step on rails skipped 277 h and the atmosphere entry.
  The winged autopilot sets a warp limit before entry the same way as the capsule ones (`Universe.WarpLimitTime`).
- **Equilibrium glide by inertial speed lies by +350 m/s** (Earth's rotation): compute the equilibrium from the speed
  relative to the atmosphere. Straight-on equilibrium glide gave 5.3 g and 3.7 km/s at the runway; tracking the deceleration
  program (reference drag, `MaxRefDrag` 12 m/s²) — 3.0 g. `MaxRefDrag` 20 → a dive of 3.5 g.
- **Lift-to-drag at α 18° vs 11°**: at hypersonic speeds L/D is higher at large α (the linear part of CN is small, sin²α grows) — hold entry
  at 40°, and at subsonic speeds switch to α ~10° (maximum L/D).
- **QLimit 20 kPa → overshoot of the runway by 15 km**; 30 kPa (runway QBand 10 kPa) — landing in the approach corridor. `FlareSinkGain` 0.5 →
  touchdown 3.9 m/s, 1.0 → 1.7 m/s (STS-1 118 m/s along the runway, "Buran" 105 m/s).
- **`Aerodynamics.CollectPanels` requires `Vessel.MassProperties` before the call**: without it NullReference in `SectionBottom`
  (layoutBuf is empty). The constructor tests call `v.MassProperties(out _, out _, out _, out _)` first.
- **Part dimensions — only via `PartCatalog.Resolve(CraftPart)`**: `Get(id)` returns the catalog part, and the propellant of a tank with
  Length 10 is computed as for a 4-meter one. Radial groups are string ids without parameters (`Get`). The Resolve cache is keyed by
  `ParamKey()`; in the UI the value is rounded to the Range step, otherwise 0.1+0.2 breeds keys.
- **Diameter junction check**: a fairing base wider than the stage is normal ("Semyorka" Ø2 → Ø3.7, "Kara-1" Ø3.7 → Ø5),
  otherwise the presets produce false warnings. Tolerance 0.05 m < the diameter step of 0.1 m.


## Control surfaces and drag chute (05.10.2026, `Aerodynamics.ControlSurface`, `FlightPhysics.StepRollout`)
- Deflection signs derived from r × F in body axes (Attitude is already in Unity axes, Cross is the same in the core and Unity — no mirroring):
  τz > 0 (nose to −X, "up") ⇔ elevon trailing edge to −X; τy > 0 ⇔ trailing edge of the +Z wing up, −Z down; τx > 0 ⇔ rudder edge to +Z.
- The actuator rate limit exists only in the view: introduced into physics, it would lag the command of autopilots tuned without it.
- The flap is `WingPanel.Trim` with ControlArea and zero area: it does not change passive aerodynamics, is not part of `ControlAuthority`
  (otherwise the controller would spend it as a rudder), the moment comes only from `PitchTrim · TrimAuthority`. auto_sts1/auto_buran ✅ after introduction.
- The drag chute is a craft state (`Vessel.DragChute`), not the `ChuteDeployed` arrays of the capsule ones: those are driven by input by altitude
  and the camera's ChuteReach, the rollout is foreign to them.

## Nose cap, panels, deployables by phase (05.10.2026, `Vessel.SetNose/DeployPanels`)
- **A closed nose cap blocks docking**: `Universe.CheckDocking` requires `Vessel.PortOpen` on both. Whoever approaches the port
  opens it themselves: `DockingAutopilot` (SetNose(true)), manual docking Tab, the mission autopilot; closing — the "Deorbit"
  phase. Symptom of a forgotten call — auto_crew_dragon hangs in "Rendezvous" 0.1 m from the port.
- **`StepDeploy` runs only in physics.** On rails `Deployed[]` stays put: panels that deploy "on a timer" will not
  deploy on rails until warp is reset.
- **The solar array deployment condition is "engines silent", not "everything below is separated".** For "Ranger-7" the "Agena" stays
  attached to the end — by the old condition the panels never deployed; `PanelsReady` = `!AnyEngineRunning`.
- Mission test names are `auto_<id>` (`auto_crew_dragon`, `auto_soyuz_tm31`, `auto_ranger7`); `crew_dragon` without the prefix
  gives "0 ok" silently.
- **Pitch sign of the keys:** W — nose toward the belly (`FlightControl`: torque.z = −input.x), while `PitchTrim > 0` is nose up.
  So the Alt+S trimmer gives "+", Alt+W — "−"; on rollout the rudder also has a minus (`ControlDeflection.x = −PilotInput.y`),
  otherwise the rudder on the ground deflects mirrored to flight.

## Booster landing and Starship (05.10.2026)
- **SECO overshoot:** check the cut-off condition (target perigee reached) every physics step, not once per autopilot tick —
  otherwise the high-thrust ship overshoots the target orbit between checks. Without deep throttling, MECO comes early.
- **Engines have no deep throttle (MinThrottle 0.4):** 3 Raptor even at minimum lift the empty Starship (it climbed 2.3 → 4.1 km
  after the flop). Fix: flop on `BurnEngines = 3`, then shut down to `LandingEngines = 1` once speed < 60 m/s.
- **Tail-first drag of Super Heavy is weak:** without an entry burn it reaches 12 km (`IgnitionCeiling`) at ≈ 1250 m/s and crashes
  (Reserve 340 t → 68 m/s, 450 t → 97 m/s). Fix: `EntryDv = 600`, `Reserve = 600 t` → caught at 2.5 m/s with 15 t left.
- **A far recovery target makes ZEM steer sideways:** a placeholder target 2956 km away turned the landing burn into a lateral burn
  and the ship crashed. Put `TargetLat/Lon` at the natural fall point (measured from the test), not at a nominal place.
  Perigee pair: `ShipPerigee` 50 km dropped the ship on Madagascar (49°E), 70 km — on the target.
- **Belly entry vs. QAlpha breakup:** `FlightPhysics` exempts `RecoveryDef.BellyFlop` sections from the QAlpha limit (90° AoA is the
  design). The tower catch is a contact model now (`TowerCatch.Step`, see "Tower catch" below) — no `CaughtByTower` exemption.
- **F9 legs in FBX:** baked legs/fins in `Falcon9_S1.fbx` cannot move — they are generated by `VesselView.SpaceX` instead
  (`F9_BAKED_DEPLOY = False` in station_parts.py; pair of constants `F9*`).
- **Deferring a landing = time shift + rotation by the body spin** (`Universe.Deferred.cs`): atmosphere, terrain and the
  lat/lon target co-rotate with the body, gravity is axisymmetric — so moving the stage Δt ahead and rotating Position/Velocity
  by `OrientationAt(now)·OrientationAt(frozen)⁻¹` (Attitude/SasHold by its `SwapYZ`) is the same problem. Also shift every absolute
  time: `LaunchTime`, `NoCollideUntil`, open `ChuteOpenTime`, `DragChuteTime`, pilot `nextPredict` — a forgotten one fires instantly.
  Valid only near its own body (third bodies are not integrated) — never defer a transfer. Verified: `booster_defer` = `booster`.
- **Watching the stage must not switch `Active`:** `RunAutopilots` drives only the active vessel, so `SwitchTo(stage)` leaves the
  ship without its mission autopilot. The "landing first" mode is a view focus (`FlightView`), control stays on the ship.
- **Starship flaps do not hold the commanded AoA at high q** (`starship_catch`, 06.10.2026): α 60° held up to q ≈ 4.7 kPa (64 km),
  then the flaps lay the hull broadside by themselves — 74° at 6.5 kPa, 84° at 12.8 kPa, 86–87° above 18 kPa. Predict assumed the
  commanded α all the way down → undershoot grew to 36 km. Fix: `BoosterLandingAutopilot.BellyHoldQ/BellyLostQ` (4.5 → 10 kPa,
  linear blend to broadside area in `PredictAccel`) → ship caught at 2.6 m/s, 9 m. AoA is a range rudder only above ≈ 60 km.
- **Boostback from orbit in vehicle axes is degenerate:** the target is a quarter orbit away, the stage's horizontal axes are
  nearly perpendicular to the target's horizontal → det ≈ 0, Δv 0 with a 44 km miss. Project the misses on `TargetAxes` (f1/f2).
- **Deorbit is too sensitive for an engine to aim:** ≈ 150 km along-track per m/s, the NodeAutopilot leaves 0.1–0.3 m/s →
  post-burn miss 45–65 km; boostback ends with "Δv 0" (Δv < 0.3) at 46 km. The belly AoA guidance (`GuideBelly`, share
  0.45…1 of sin²α) absorbs it before 60 km. The planner's reference arc from another point of the orbit was off by 58 km —
  refined by a secant on full `Predict` (`ShipRefineAlong`).
- **Heavy Super Heavy on 13 engines overshoots the entry burn:** with `Reserve` 520 t the 0.1 s predict step let the entry burn
  overshoot by 349 m → crash at 25 m/s; landing at 600 t → 153 t on the catch crashed at 16 m/s (mode chattering at 60 m/s).
  Fix: `EntryFineAlong` 3 km — predict every step and drop to `LandingEngines` → caught at 3.5 m/s, 3 m.


## Tower catch, contact model (06.10.2026)
- **Pins cross the arms by rotation, not by descent:** at vy ≈ 0 the pin height changes from the hull tilt/CoM motion, so
  "above <= 0 && above - vy*dt > 0" missed the crossing (ship fell through: "смещение 2.5 м"). Fix: store the previous pin
  height in `Vessel.CatchPin` (NaN = none) and detect the sign change.
- **Bar contact must always push the position out:** with an 8° tilt the CoM moved inward while the hull at arm height moved
  outward, the "velocity into the bar" test said no contact and the hull sank into the bar. Fix: correct the position by depth
  every step, change velocity only when it points into the bar.
- **Bar push leaves sideways drift:** after closing, the push left vz = 3.8 m/s relative to the tower -> SH drifted and tilted 10°
  ("Сорвалась … наклон"). Fix: clamp vz to the tower's when the arms are fully closed and the hull is inside the gap; the slide
  check uses only the speed along the arms (vx). Result: SH 2.5–2.9 m/s, ship 2.5 m/s +8 m.
- **Separated stage messages:** the catch `Raise` of a separated SH does not reach the test log (only the active ship's do) —
  check `Vessel.TowerCaught` / autopilot status instead. Trace with env `KSP_CATCH=1`.
- **New core file not seen by Unity:** `refresh_unity` scope "scripts" left CS0234 "TowerCatch does not exist" (no .meta yet);
  `refresh_unity mode=force scope=all compile=request` fixed it.

## Landing from the air (06.10.2026, `ApproachStart.cs`, `BoosterLanding.cs`)
- **Belly roll sign:** the belly (+X) must point along `vh` (horizontal air velocity), not `-vh` — with `-vh` the ship flew back
  side first. `auto_ift5` / `starship_catch` unchanged after the fix.
- **Belly-fall spawn range ≈ 1 km:** a belly-flopping Starship barely glides horizontally; at 10+ km out it never reached the
  tower (`starship_landing` Range 1000 m from 15 km works).

## Interplanetary planning (15.10.2026, `TransferPlanner.PlanInterplanetary`, `PatchedConics.DefaultHorizon`)
- **Porkchop must price the real departure, not Σ|v∞|.** Earth→Saturn from an equatorial 300 km orbit: the Σ|v∞| minimum was a
  160° transfer whose asymptote is 24° off the ecliptic and 48.6° off the parking plane → node 13.9 km/s (normal 10.8 km/s).
  Cost now = Oberth burn at periapsis with the out-of-plane angle β (`√(vq² + vp² − 2·vq·vp·cos β)`) + capture into an ellipse
  to ½ SOI: β 0.8°, node 8.46 km/s, 2590 d (> 180° transfer). Pair: `PorkCaptureApoSoi` ↔ `MapView.CaptureApoSoi`.
- **Prediction horizon around the Sun:** the old 2-year cap cut the Saturn transfer (6–9 years) before the encounter → no
  Saturn patch. Heliocentric orbits now predict `min(1.05·P, 40 y)`, hyperbolas 40 y (`HelioHorizonYears`).
- **Half-burn lead must count every stage.** `BurnTime` puts the whole Δv on the current stage's thrust: a full R-7 burning
  8.46 km/s to Saturn gave ≈ 4 min instead of ≈ 8, so the node pilot and the rails stop started 4 min late; right after
  `Teleport` the current stage is silent → `BurnTime` = ∞ → no lead at all ("Ждём ориентацию" at node −16 s).
  `FlightControl.StagedBurnTime` (stage list, 2 s per separation) is now used by `NodeAutopilot` and the warp stop in `Universe`.

- **Correction to a planet must aim through the predicted patch, not a no-gravity miss.** `PlanIntercept` scored candidates
  by the straight-line miss; with the target's gravity the real periapsis differed by hundreds of km — Luna-9 got a correction
  174 m/s off and hit the Moon at 639 m/s. Fix: every candidate is also scored by `PredictedMissError` (PatchedConics patch at
  the target → `b = rp·√(1+2μ/(rp·v∞²))`, no encounter = 1e8), the winner is polished by coordinate descent (`Polish`, 60 iters).
  `auto_luna9` Success again. Heliocentric branch `PlanCorrection` (ManeuverAutoPlan, body = Sun): Saturn Pe 52–56 thousand km.
- **NodeWarpMargin 90 s stays.** Suspected for luna9 (tried 30) — not the cause; 90 restored.
- **Warp passed through a whole SOI.** At ×1e7 the rails crossed Saturn's sphere (period of the flyby ≈ days) in one step and
  the flyby was never seen. `Universe.HandleTransition` on Encounter (not autopilot) caps warp by `Ladder(TimeToPeriapsis)` and
  `AdvanceRails` breaks the step when the warp dropped. Message "Ускорение снижено до ×1000000: подход к телу Сатурн".
  `TimeToPeriapsis` on a hyperbola after periapsis is NaN → cap 0 (guarded).
- **Δv shortfall is now in the node message.** Saturn from R-7: departure 8.1 km/s cut off at ≈ 6 km/s (miss 0.6 mln km), the
  correction 1 km/s vs 156 m/s left → "НЕ ХВАТАЕТ: в ступенях N м/с" (`ManeuverAutoPlan.Plan`). Not a bug: the rocket is too small.
- **`PatchedConics.Predict` adds `node.Remaining` (world vector in P), not the Prograde/Normal/Radial fields.** A node built
  from components only predicts the orbit WITHOUT the burn. `TransferPlanner.PredictedMissError` forgot it → `Polish` tuned
  only the wait/normal penalties; Luna-17 arrived with Pe 13 562 km. Fix: `node.Remaining = node.WorldDeltaV(orbit)` before
  `Predict`. Any new code calling `Predict` with a hand-made node must set `Remaining`.
- **`PlanIntercept(r, v, t0, …)` treats (r, v) as the state AT t0.** Passing the current state with `t0 = now + 2 h` makes the
  craft "lag" by 2 h: the Moon moved ≈ 7000 km and Vympel flew past instead of impacting. Propagate first:
  `KeplerOrbit.FromState(r, v, mu, now).GetState(t0, out r0, out v0)` (MissionAutopilot already does).
- **Separation push shifts the entry corridor.** Dropping the SM along the track 30 min before perigee (`Vessel.StagePush`
  1.5 m/s, capsule gets ≈ 0.8 m/s recoil) moved Apollo 8 perigee 44.6 → 38.9 km: −7.1°, 10.6 g, crew dead. Fix:
  `MissionAutopilot.TurnSideways` — before staging in vacuum, SAS Normal (≤ 120 s, 5° tolerance); a sideways push changes
  inclination, perigee only to second order. After: Pe 44.6 km at entry, −6.89°, peak 8.4 g. LEO capsules unaffected
  (vostok/gemini3 8.5 g, soyuz_tm31 9.7 g for 0.6 s, crew_dragon 8.3 g).
- **A vessel that leaves physics mid-frame must go on rails with the epoch of ITS state, not the frame start.**
  `AdvancePhysics` splits a frame into sub-steps; a piece staged on sub-step s stands where it was at that sub-step, but
  `MovePassive(..., since: t0)` built its orbit with epoch t0 → shifted by s·h along the track. S-IVB+LM landed 852 m away
  (0.11 s × 7.8 km/s) > `TranspositionRange` 500 → no transposition, the CSM flew to the Moon alone, and `LunarLanding`
  "undocking" staged off the SM and armed the chute. Fix: per-vessel `stamp` in `AdvancePhysics` (birth time via `NoteBorn`,
  last stepped time for vessels that drop out of physics, e.g. old active after transferControl). Also: distances between
  vessels inside a step — via `Universe.StateOf(x, t)`; `LunarLanding`'s `while (… NextStageLabel != null) Stage()` spun
  forever without the LM (test hung, no output) — now capped at 8 steps.
- **Aim for the impact parameter at the PREDICTED v∞, not the guessed one.** `ToMoon` passed `targetMiss = Impact(rp, 1000 m/s)`;
  real v∞ ≈ 800 m/s, so that b means rp below the surface. Once `Polish` worked (the `Remaining` fix), it aimed straight at it, and
  `ToMoon`'s coordinate descent couldn't climb back out of the shallow valley (499 km vs 100; 40 moves spent). Fix: `PlanIntercept(...,
  periapsis:)` — `PredictedMissError` recomputes the target b from the periapsis at the v∞ of each prediction. After: perilune 100 km
  (target 100), autoplan earth→moon in 739 ms.
- **Lambert silently returned a parabola for every hyperbolic transfer.** The lower bound of z was "fixed" by halving towards
  `hi` while `Y(lo) < 0`: from −4π² it jumped straight to 0, so all z < 0 (hyperbolas) were cut off, the bisection pinned at
  z = 0 and still returned `true`. Symptom: Voyager legs after Jupiter had e = 1.000 and missed the target point by
  100–1150 million km; the tour planner then wandered hundreds of days off history (mismatch 2–5 km/s). Earth→Jupiter
  (elliptic) was unaffected, which hid it. Fix: only EXTEND the lower bound downwards while the hyperbola there is still too
  slow (`LowerBoundDoublings` 12 → z ≈ −1.6·10⁵, cosh overflow beyond); the bisection already steps over the Y < 0 region.
  Check: propagate v1 with Kepler for tof and compare with r2 (now 0.000 km on all legs). After: V2 tour within −0.7…+7.4 days
  of history, flyby altitudes 635/100/81 thousand km vs historical 650/101/81, C3 103.
- **Game time before 2000 is negative** (seconds from J2000): 1977 dates are ≈ −7·10⁸. `0` or `> 0` cannot be a "no date"
  sentinel — `Objective.Date` defaults to NaN and is tested with `double.IsNaN`. The first tour run returned null in 0 ms.
- **A flyby tour must be anchored to the historical dates.** Zero-mismatch solutions form continuous valleys; without a pull
  term the simplex slid along them towards cheaper departures (V1 Jupiter +235 days). `TourPlanner.HistoryScale` (2 days,
  quadratic) keeps the solution within days of history.
