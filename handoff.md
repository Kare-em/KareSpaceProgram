# Handoff — batch of 15 requests (session b8463ba3 → continued in cca97c40, 15.10.2026)

17:44 all 4 agents of b8463ba3 died (limit) with NO code edits; relaunched 17:48 from the same prompts
(prompts saved in cca97c40 scratchpad task_*.md; moon downloads in b8463ba3 scratchpad/moon_dl).

Not committed. Reply to user in Russian, ✅/❌ per item. Python edits with newline='' (CRLF), no perl -i.
Previous handoff (Voyager/Cassini fixes) — all done, see git diff / docs/STATE.md.

User answers: moon textures — download NASA/USGS (consent given); comets — Halley + Hale–Bopp;
manual maneuvers — KSP-style handles on the map; spawn on orbit — Esc menu, current vessel, body + altitude.

## Items
| # | Item | Owner | State |
|---|---|---|---|
| 1 | No warp cut on SOI entry | me | CODED (+ frame break on SOI change, fixes auto_apollo8/11 13 g): Universe.cs HandleTransition block removed, NextSlowEventTime() ignores Encounter/Escape |
| 12 | Autopilot flyby ×10 faster | me | CODED: FlybyRealSeconds 60 → 6 |
| 2,14 | Sun glare at Neptune / too bright | me | CODED: SunFlare.Intensity 0.45, SunLight.FlareDistanceFade 1/au, disc size by distance |
| 5 | Ship weakly lit far from Sun | me | DONE: FarSunEv; Play check: light x exposure = 3.59 at Earth vs 3.58 at Neptune 30 AU |
| 9 | Hide UI key | me | DONE: F12 FlightHud.UiHidden |
| 10 | Spawn on orbit of any body | me | CODED: Game/OrbitSpawnMenu.cs + PauseMenu button; Unity compile not yet checked (fx agent in Play) |
| 8 | Gas giant atmospheres + ring collision check | me | DONE (core test `giants` 7/7): adiabatic deep atmosphere (Atmosphere.EnableDeep), crush 23 bar (FlightPhysics.GasGiantCrushPressure), Core/Bodies/Rings.cs + Flight/Universe.Rings.cs (node radius from conic, works on rails) |
| 6,13 | Burn start time; multi-node + KSP map handles + node menu | agent "maneuvers" | DONE (nodes 19/19, game compiles); map UI not seen in Play. Reported auto_cassini dies on Saturn ring B |
| 3 | Moon textures NASA/USGS | agent "textures" (sonnet) | DONE: 11 maps Resources/MoonMaps, BodyRenderer.MapFor; not seen in Play |
| 4 | Comets Halley + Hale–Bopp | agent "comets" | DONE core (test comets 18/18), CometView.cs + CometGlowHDRP.shader; Play not seen |
| 7,11,15 | Plume/trail volume, RCS realism, aurora | agent "fx" | DONE (wrap-up agent in b03a3ab4 finished: debug leftovers removed, docs) |

## Must re-verify at the end
- Full core suite incl. `auto` (warp cadence changes broke Cassini once — see old notes in pitfalls-core.md).
- Unity compile clean, Play screenshots, MissionId back to "vostok".

## Session b03a3ab4 (19:20)
- auto_cassini: probe arrives at Saturn at i≈0.01° to equator, crosses ring B before capture. Fix in progress in MissionAutopilot.Planetary.cs (TourAim / capture B-plane: pick orientation whose ring-plane nodes lie outside dense rings, like real Cassini F–G gap).
- ArrivalWindowFactor in MissionAutopilot.Planetary.cs is already coded (Venus closest-approach window).
- auto_cassini FIXED (RingSafeSide aims mid safe window + PlanRingTrim in Saturn SOI, omega 89.6, nodes 158/160k km; Huygens lands). Debug removed. Pitfall in pitfalls-core.md. Full suite running.
- Next: item 5 (FarSunEv), then Unity compile + Play check, MissionId "vostok", report.
- Full core suite after ring fix: 282 ok, 0 fail (15.10.2026). All 15 items done.

## Batch 2 items (15.10.2026, session b03a3ab4)
User: "не нашел задание орбиты. внизу в меню убери отдельные кнопки вынеси отдельно. добавь соплам поворот для контроля вектора тяги. улучши эффект разлета газа и пыли на старте" + "добавь визуальный нагрев сопел и вообще деталей. поработай над реалистичной расстыковкой. улучши кастомную сборку. добавь режим карьеры"
| # | Item | Owner | State |
|---|---|---|---|
| A | Orbit spawn not found → own window | me | CODED: PauseMenu cheats block removed, button "Читы и телепорт…" under Controls/Saves, panel scales by GUI.matrix to fit screen (was 1250 px > 1080); OrbitSpawnMenu got DrawQuick (toggles + 4 quick jumps). Not compiled yet |
| B | Nozzle gimbal (TVC) | me | CODED: Game/VesselView.Gimbal.cs (AddGimbal/UpdateGimbals, model nozzles split from FBX slot NozzleColor via SplitNozzles), hooks in VesselView.cs (bells, upper engine, Rebuild clear, LateUpdate). Model hook GimbalModelNozzles NOT yet wired into craft branch. Not compiled |
| C | Launch gas/dust | agent (bg) | CODED, compiles clean via standalone Roslyn on Unity rsp (editor did not recompile during the run — refresh it): new Game/ExhaustTrail.Ground.cs (160-puff lazy pool per vessel, thrust^1/3 scale, drag+buoyancy, trench exit, flame glow I/d², flat dust fan on Moon/Mars with body g); ExhaustTrail.cs old puff/dust code removed; LaunchPadView.cs + FlameTrench/OnPad. NOT seen in Play |
| D | Visual heating of nozzles and parts | me | CODED (Game/VesselView.Heat.cs), compiles offline; not seen in Play |
| E | Realistic separation (stages, fairing/LES) | me | CODED: Core/Flight/Separation.cs (S-IC/S-II retro dV), Vessel.FireRetros/SeparatedAt, Game/VesselView.Separation.cs (retro/ullage/LES/hot-staging jets); compiles offline; tests separation/craft/apollo8 ok; not seen in Play |
| F | Improve custom builder | agent | DONE (not seen in Play): Δv budget of the mission vs rocket (bar + warning), Kerbal Engineer stage table (max g, time, mass), crew g-load warning, stage highlight in preview, career filter `HangarController.PartAvailable` + `CatalogChanged()`. Files: Core/Vessels/CraftAnalysis.cs (new), VesselDesign.cs (StageStats.MaxAccel/HighGTime), Craft.cs (Compile(craft,budget)), Game/HangarController.cs, .Build.cs, Tools/CoreTests/BuilderTests.cs (group `builder`, 29 ok). Compiles clean offline; Unity editor was in Play — not recompiled |
| G | Career mode (hybrid) | agent | DONE (not seen in Play): Core/Career/{CareerState,CareerContracts,CareerTech,CareerPricing,CareerObserver}.cs, Game/{CareerStore,CareerFlight,CareerWindow}.cs, PauseMenu (Awake adds CareerFlight, block "Режим", Esc chain), MissionPicker (hides career-only), Tools/CoreTests/CareerTests.cs (group `career` 30 ok), GDD §8.6, STATE.md. Wiring `HangarController.PartAvailable = Career.IsPartUnlocked` in CareerStore.Init. Core+Game compile clean offline; editor was in Play — not recompiled |
