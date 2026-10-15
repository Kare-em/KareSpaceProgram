# HANDOFF — Voyager 1/2 work in progress (15.10.2026)

This file hands the task to another Claude account. Read `CLAUDE.md` first, then this file. Reply to the user in Russian.
Delete this file once the task is finished and the facts below are moved into `docs/STATE.md` and `docs/pitfalls-core.md`.

## The user's task
"Move on to Voyager 1/2: Titan IIIE-Centaur, interplanetary profile, gravity assists."

The user's extra requests, all done:
- **Rails grace after staging.** For 5 s after staging, time must not warp non-physically.
- **Long-burn departure errors.** The user suggested fixing them with orbit corrections. Corrections stay in the profile, but the probe has only ~290 m/s. So the departure error is also removed at the source (see "Departure accuracy" below).

Later, not started: Perseverance, more missions.

## Nothing is committed
`git status` shows many modified and new files. Commit only when the user asks.

New files in this task:
- `Core/Flight/MissionAutopilot.Planetary.cs`
- `Core/Missions/PlanetaryMissions.cs`
- `Core/Orbits/TourPlanner.cs`
- `Core/Vessels/PlanetaryRockets.cs`
- `Game/MapView.Transfers.cs`

The new files carry `.meta` files.

## Done

**Missions and presets**
- `voyager1` and `voyager2` live in `PlanetaryMissions.cs`.
- The rockets live in `PlanetaryRockets.cs`: Titan IIIE plus Centaur D-1T, and the TE-M-364-4 solid kick of ≈2 km/s.
- The probe uses hydrazine: Δv ≈ 290 m/s (105 kg, 4×0.89 N, 825 kg).

**Mission profile** `ProfileType.Planetary` (`MissionAutopilot.Planetary.cs`):
1. Plan the tour (`MissionCatalog.PlanTour` → `TourPlanner.Plan`). It is a Lambert chain near the historical dates, scored with the penalty `HistoryScale = 2 d`.
2. Wait for the launch window. The parking-orbit plane must contain the departure asymptote: `LaunchWindow.NextPlaneWindow`, using the direction overload, `FirstRoot` and `PlaneNormal`.
3. Ascend to the parking orbit, then replan the tour from the real departure time.
4. Depart with `TransferPlanner.PlanDeparture`.
5. Stage until the probe is free.
6. Fly each leg in this order:
   - early correction 5 d after leaving the SOI;
   - late correction 75 d before the flyby;
   - check the flyby corridor objective.

**Gravity-assist aiming** (`TourAim`):
- **Intermediate body.** Rotate v∞ toward the next planet: Lambert from the planet at the encounter time to the next planet's tour date. The periapsis comes from the turn angle, clamped to the corridor.
- **Last body.** Aim at the middle of the corridor.

**Lambert fix** (`Lambert.cs`): hyperbola lower bound. The full suite then gave 207/0 (before the changes below).

**Rails grace** (`Universe.cs`):
- `StageRailsGrace = 5` and `lastStageTime`.
- `RailsBlocker` returns "разделение" ("separation") for 5 s after `Stage()`.

**Departure accuracy** (`FlightControl.cs`, `NodeAutopilot`):
- **Energy-mode remaining Δv is now exact.** `EnergyLeft()` takes the root of dv²/2 + (v·u)dv − ΔE = 0.
  - The old linear ΔE/|v| overestimated by dv²/2v: +3.4 km/s on the Voyager departure.
  - So the burn started early (4° asymptote rotation) and the Centaur was cut late (v∞ +147 m/s).
  - **This affects all long burns, e.g. lunar TLI.**
- **Solid kick guided by asymptote.**
  - `ManeuverNode.TargetExcess` is the wanted v∞ vector (`KeplerOrbit.Excess`, new property).
  - The Centaur cutoff time sets |v∞|. The kick direction sets the asymptote direction.
  - `KickAim` runs Gauss-Newton over 2 angles. The kick phase is a closed loop on `SolidLeftDeltaV`.
  - The kick constants are `KickGuidanceShare 1.5` and `KickHoldDv 30`.
- **Measured on v1:**

  | Change | Departure angle error | First correction |
  |---|---|---|
  | Before | 4.03° | 632 m/s |
  | After `EnergyLeft` | 1.12° | 146 m/s |
  | After kick guidance | 0.01° | 49.46 m/s |

  Final v∞: 10260 vs 10272 m/s.

## Latest `auto_voyager1` run — FAIL at Saturn
```
[6.91 d]   Correction to Jupiter: 49.46 m/s      (executed OK)
[469.24 d] Late correction to Jupiter: 0.73 m/s   (executed OK)
[542.22 d] ★ Jupiter flyby objective done (periapsis 203 519 km)
[594.20 d] Correction to Saturn: 1609.34 m/s  -> propellant exhausted, 1365.6 m/s short -> FAIL
```
Two things are going on:
- **The early correction is mostly B-plane relocation.** The departure error is only 12 m/s, yet the early correction costs 49 m/s. The reason: `PlanDeparture` matches only the miss *distance* at Jupiter, not the B-plane *point*.
- **The Jupiter aim is only patched-conic approximate.**
  - It uses Lambert from Jupiter's centre at encounter, and asymptote = velocity at the SOI edge.
  - At Jupiter the SOI is 48 million km. The asymptote and SOI-exit errors are ~1.5° and ~1% of speed.
  - Over the Saturn leg that becomes tens of millions of km of miss.

## Next steps

### 1. Apply the prepared edit `aimdep.py` (not yet applied)
It adds the B-plane aim to `PlanDeparture` so the early correction shrinks. The script is in the old session's scratchpad:
`C:\Users\karim\AppData\Local\Temp\claude\C--CocosGames-KareSpaceProgram\e612462f-4cce-42b2-ab17-bbfb6e6cf301\scratchpad\aimdep.py`

What it does:
- In `TransferPlanner.Return.cs` it adds the parameter `AimPoint aimAt = null` to `PlanDeparture`.
- When `aimAt` is given, the cost lambda changes as follows:
  1. Compute the B-vector at closest approach: `rel - u*(rel·u)`, where `u = vrel.normalized`.
  2. Subtract `aimAt(ta, rel, vrel, Cross(rh, vh).normalized)` and take the length.
  3. Add `PlanetDvPenalty*DeltaV`.
- In `MissionAutopilot.Planetary.cs` it passes `TourAim(tour, 0)` as the last argument of the `PlanDeparture` call.

### 2. Main fix: target "through the flyby"
Add a method `TransferPlanner.PlanThroughFlyby(body, r, v, t0, flyby, next, nextAim, lead, guess, rpMin, rpMax)`.

**Inputs**
- `orbit = FromState(r, v)`, `tc = t0 + lead`, and the state `(rc, vc)` at `tc`.
- Basis: `pro = vc.normalized`, `h = Cross(rc, vc).normalized`, `rad = Cross(pro, h)`. This is the same basis as `SolveCorrection`.
- `dv0` is rebuilt from `guess` (Prograde, Normal, Radial).

**`residual(dv)` — exact patched-conic propagation**

The sim is patched conics on rails, so this prediction is exact:
1. `helio = FromState(rc, vc + dv, body.Mu, tc)`.
2. `te = PatchedConics.FindEncounter(helio, flyby, tc, tEnd)`.
3. `ChangeFrame` into the flyby body. The hyperbola is `FromState(…, flyby.Mu, te)`.
4. `tx = hyp.NextTimeAtRadius(flyby.SoiRadius, te, true)`.
5. `ChangeFrame` back to the Sun → `helio2`.
6. `ClosestApproach(helio2, next, tx, span, out ta)`.
7. The residual is B-vector − `nextAim(ta, rel, vrel, h2)`.

**Solver**
- Levenberg / Gauss-Newton on 3 unknowns (dv).
- Finite-difference step ~1e-3 m/s. The sensitivity is huge.
- Small damping μ, so the step stays minimal-norm. The null direction is mostly timing.
- Backtrack by halving.
- Stop at |res| < ~100 km.

**Fallback.** After solving, check Jupiter's periapsis (`hyp.PeriapsisRadius`) against the corridor `[rpMin, rpMax]`. If it is outside, or the solve fails, return `null`. The caller then keeps the B-plane plan.

**Wiring into `Correct()`.** Inside the `lead` pass loop, when `i + 1 < tour.Bodies.Length`, run:
```
plan = PlanThroughFlyby(..., tour.Bodies[i+1], TourAim(tour, i+1), lead, plan, floor, top) ?? plan;
```
Use corridor floor/top from the objective, as `TourAim` computes them. With this, the late correction (75 d before Jupiter) should leave the Saturn correction small.

**Budget.** About 240 m/s are left after the Jupiter corrections. Every post-flyby correction must stay within tens of m/s.

### 3. Remove the temporary DIAG block
Remove the DIAG block in `MissionAutopilot.Planetary.cs`. It is the `{ var o = …; u.Post($"DIAG v∞ …"); }` right after the staging loop.

### 4. Tests
- Run `auto_voyager1`, then `auto_voyager2`. Voyager 2 is Jupiter → Saturn → Uranus → Neptune, so the chain uses `TourAim` for Saturn and Uranus as intermediate bodies.
- `TestAuto` gives tours 13 years. `AutoMissions` includes voyager1 and voyager2.
- Then run the full suite. `EnergyLeft`, kick guidance and the rails grace may shift other missions. The target is ≥207 OK.
- Command: see `CLAUDE.md`, then `-- auto_voyager1`.
- Run it in the background with output to a file. One run takes ~1 min of real time (200 000 frames).

### 5. Documentation
- **`docs/pitfalls-core.md`:**
  - the linear energy-remaining bug, with the numbers above;
  - kick guidance by asymptote;
  - the patched-conic flyby aim error and the through-targeting fix.
- **`docs/STATE.md`:** add a Voyager section covering:
  - the Planetary profile, TourPlanner and TourAim;
  - kick guidance;
  - `StageRailsGrace`;
  - the departure B-plane aim and through-flyby targeting.

## Side sessions (another Claude session; verify before reporting)
- **voyager-models.** Reported done: 4 low-poly FBX files in `Assets/_Project/Models` (e.g. `Titan3_Core.fbx`), wired in Unity.
  - Not verified: the flight up to separation.
  - Check the diff, then tell the user in one line.
- **planet-rings.** Realistic planet rings: `Game/PlanetRings.cs`, `Game/RingProfiles.cs`. It was still working at handoff.
  - Verify the result in Unity before reporting it.
- Do not edit files those sessions own: Game rendering, `Tools/blender`, `Models`, `VesselView*`, `MapView*`.

## Working rules recap
- **Editing files:** use Python scripts from a file, detect the newline style, and write with `newline=''`. Never use `perl -i` on Cyrillic. If a heredoc breaks on quotes, create the script with the Write tool.
- **Code comments:** in Russian, explaining "why", with § references to the GDD. Numbers go in named constants, with the paired dependency noted.
- **Tests:** do not build while a test run is in progress (the exe is locked).
- **Wrap-up:** remind the user about `/compact` at the end.
