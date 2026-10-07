# Pitfalls: graphics, HDRP, scene

Read when editing `Game/` (rendering, sky, materials, patch, plume) and `FlightSceneBuilder`.

## Pipeline and materials
- **The project had no HDRP asset**: `GraphicsSettings.currentRenderPipeline == null` → every HDRP/Lit is magenta.
  The builder creates `Settings/HDRP.asset` and sets it as default; the first install is a long shader reimport, MCP stays silent.
  Also `PlayerSettings.colorSpace = Linear` — HDRP does not render in Gamma; and High Quality lightmaps
  (internal `SetLightmapEncodingQualityForPlatform`, the Wizard complains).
- **HDRP runtime materials are not validated**: set keys by hand. `Plume.mat` lost `_EMISSIVE_COLOR_MAP`
  (Validate strips it when the asset has no texture) → plume without a gradient. Fix: `new Material(plume)` + texture +
  `EnableKeyword`. Same for the ground detail map: `_DETAIL_MAP` + `_UVDetailsMappingMask (0,1,0,0)` for UV1.
- **`VisualEnvironment.planetCenter/planetRadius` are in kilometers**, the scene is in meters (divide by 1000).
- **`Terrain` in the game layer is ambiguous** (`UnityEngine.Terrain`) — alias `using Terrain = Kare.Space.Core.Terrain;`.

## Shadows, exposure, light
- **Sun shadows vanish with a large far plane** (HDRP 17.6, rocket shadow on the pad): near 0.1 — far 1e6 ✓,
  3e6/1e7/2e8 ✗; near 1 — far 1e7 ✓; near 2 and 20 — far 2e8 ✗. The patch cast is not the cause. Fix: near 1 / far 1e7,
  all bodies in a "shell" 5e6…9.5e6 (`BodyRenderer.Project`, the measure is the distance to the horizon, so the active body
  up close is not compressed and matches the patch). GDD §2.7 (far 2e8) is superseded by this.
- **Auto-exposure in space whites out the frame**: the histogram sees the black sky and drifts to EV −5 (white frame at 124 km).
  Fixed with a lower EV limit in sunlight (`SkyController.SunlitEvMin` = 12, smoothly with altitude up to 30 km).
  Read the real EV from `HDCamera.currentExposureTextures.current` (1×1, EV = −log2(r·1.2)), not from the Volume.
- **Grey haze in orbit with the Sun at the frame edge** — the Sun halo (`flareSize/flareMultiplier` of HDAdditionalLightData)
  at disc brightness + bloom: R≈240 across the whole frame; without the halo R=0, a bare disc with bloom 0.2 is a normal spot.
  Suppressed by `SunLight.Awake`.
- **Sun flare is a data-driven SRP lens flare** (`SunFlare`): one element size unit ≈ 4 % of frame width (rays 1.1 —
  30 px at 640), hence `scale` 10. A circle `edgeOffset` of exactly 1 hides the element entirely (invisible), 0.1 — a hard disc,
  0.95 — a soft halo. Brightening toward the frame edge is damped by `radialScreenAttenuationCurve` (1 → 0.25).
- **The night was black** — no light source except the Sun (frame (0,0,0) except stars). `NightLight`: reflected light of the
  body (full Moon ≈ 0.31 lx, Earth from the Moon ≈ 15 lx) + sky glow 0.02 lx (×10 of real) from the zenith, and EvMin −7
  (was −5: moonless ground ≈ 3 % of white). Night lights get `interactsWithSky = false` — otherwise PBSky draws a full
  disc over the Moon phase.
- **RT shadows of a directional light require `useScreenSpaceShadows`** (HDRaytracingManager: ShadowsEnabled &&
  useScreenSpaceShadows && useRayTracedShadows) and screen shadow slots ≥ 1 in the asset — without the first flag RT is silently
  off. `HDRenderPipeline.currentAsset` does not exist in 17.6 — `GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset`.
- **Cascade shadows — only for one directional light** (HDRP 17.6). A second one with shadows (the Moon above the horizon by day)
  → "Cascade Shadow atlasing has failed" every frame. `NightLight` gives shadows to the reflected light only when
  `!SunLight.Shining`; by day its shadows are invisible anyway (0.3 lx vs 10⁵).
- **Spots on distant ground = RT shadow self-intersection** (02.10.2026, camera 30 km, Sun 47°): dark-blue blotches
  all over the patch; without Sun shadows they are gone, CPU tracing of the patch mesh toward the Sun — 0 of 200 points in shadow. HDRP
  takes the ray origin from depth (far away the error is meters), and `RayTracingSettings.distantRayBias` is 0.001 by default. Did not help: RT Off
  on the Earth/Moon spheres, patch DynamicGeometry, distantRayBias 5. 50 helped (`SkyController.DistantRayBias`);
  do not touch `rayBias` (near) — the rocket shadow on the pad. Check trap: random patch vertices include
  unused water vertices at h = 0 under the ground — sample only triangle indices.
- **DLSS works without `ENABLE_UPSCALER_FRAMEWORK`** (02.10.2026, RTX 5060 Ti): `HDCamera.IsDLSSEnabled = true`,
  internal 1344×756 → output 1920×1080, ≈122 fps. If `DLSSDetected = false` — see pitfalls-tools (outdated HDRP DLL).
- **PBSky `groundTint` is both the underside lighting of the hull and the albedo in multiple scattering of the haze**: 0.3 → the ocean from 200 km
  is pale (134,166,203), 0 → (68,100,139). Hence `BodyLook.Disc` is dark; with altitude Low→Disc (`SkyController.AirWeight`).
- **A black capsule under the dome is not a bug**: the Sun is high, the hull is in the dome's shadow; the dome's shadow side R≈25 — there is ambient.
- **Stars** — `Editor/StarFieldBaker.cs`: EXR 4096×2048 in nits (0m = 2.54e-6 lx / texel Ω), imported as a cubemap with
  `maxTextureSize = W/4` (otherwise the face is 2048, 24 MB), BC6H; in PBSky `spaceEmissionTexture`, multiplier 1.

## Emissive (plume, incandescence)
- **Emissive — by emission only.** HDRP/Unlit `_UnlitColor` bypasses exposure: 1 nit at EV 14 is already white.
  Emission is exposed. Physical 2·10⁵ nits additive from 4 walls + bloom = whole frame white; 3·10³ at EV 14 — a normal core.
- `Color.white * nits` multiplies alpha, and additive is color times alpha (alpha 50 587 → white screen).
- **HDRP fog on transparents tints the whole sphere, even at alpha 0** (clouds made the Earth turquoise from orbit):
  `_EnableFogOnTransparent = 0` on the cloud and plume materials.
- `ValidateMaterial` strips `_EMISSIVE_COLOR_MAP` if the texture is only in the MPB.
- **Shared `MaterialPropertyBlock` — `Clear()` before every renderer**: after stage separation the hull inherited
  the plume's `_EmissiveColor` (22 nits) → at night at EV −5 a white screen with the Sun off (01.10.2026).
  Symptom: the frame does not depend on camera direction, at fixed EV 12 the hull is grey (≈10³ nits with no light).
- **HDRP/Lit emission on the hull is barely damped by exposure** (unlike the Unlit plume): at EV 14.3 — 20 nits → R≈20,
  100 nits → R≈172, 527 nits → a white "creamy disc" on entry. Hull incandescence `VesselView.HeatGlowNits` = 60.

## Meshes and patch
- **Unity triangle winding**: the front face (p0,p1,p2) faces along `Cross(p1−p0, p2−p0)`; the first version of the pad was inside out.
- **`mesh.triangles = …` merges all submeshes into one** — flip the winding with `GetTriangles/SetTriangles` per submesh.
- **Patch shore — a water plane over a lowered bottom**, not "triangle wet/dry": that way the shore went in teeth along the grid step
  (≈600 m). Wet land nodes — down to `max(RawHeight, −ShoreDepth)`, water — own vertices at h = 0.
- **`Terrain.Perm` — `ConcurrentDictionary`**: with `lock` on every call the 2048 cloud was built in 0.94 s, without — 0.44–0.48 s.
- **A "wall" above the horizon at the pad = the spaceport pit, not the body sphere and not PBSky.** The land map (step 20 km,
  the "mountainness" `m` is blurred) puts Baikonur at 1130 m, Plesetsk 1135, Vostochny 1444 against marks 90/120/230, and
  the site was leveled only within `BlendRadius` 9 km → a ~1 km wall with 8° slope, the bottom in its shadow. Diagnosis:
  switch renderers off one by one and measure a pixel (the wall went away only with the patch), then raw terrain by rings around
  the site. Fix — the basin `LaunchSite.BasinRadius` 150 km: raw terrain is lowered by `raw(center) − Elevation`
  with a smoothstep weight, small relief intact. After: 0/9/30/80/150 km = 90/96/166/633/1141 m.
  Do not lower below the site mark (`min(BasinDrop, h − Elevation)`): otherwise basin lowlands went under 0 →
  "lakes" at the pad (02.10.2026). After the water clamp there is none within 50–850 km around Baikonur, Plesetsk, Vostochny.
- **The pad lifts the rocket**: `LaunchSite.PadHeight` (6 m) accounts for `PlaceOnSurface`, otherwise the nozzles are in concrete. Pair — `LaunchPadView`.
- **"Landing through the texture" on the Moon = a face of the far sphere above the patch.** A face is a plane between vertices (21 km
  at 512 segments), the vertices are "conservative" (min over the neighborhood), but in a crater the relief sags below the plane: the sphere
  is above the relief at 0.9 % of random Moon points, up to +494 m, at the Tycho rim +46 m within 10 km (offline probe of the core,
  05.10.2026). Symptom on a screenshot: a smooth "slab" with no ground tile cuts the legs, above it — the patch slope with a straight edge.
  Fix — `BodyRenderer.CutUnderPatch`: faces lying entirely inside the patch square (minus `HoleMargin`) are not drawn;
  without a patch the mesh comes back whole. Do not lower the sphere — a gap opens at the patch edge.
  Measured in Play (Surveyor-1 on legs, 5 points: maria, highlands, Copernicus slope, Tycho rim): feet relative to the core
  −0.16…+0.31 m, relative to the patch mesh −0.29…+0.20 m; the model's foot radius 0.98 m = `foot` in `SettleOnLegs` (1.0).
- **The patch mesh under the legs diverges from the core by ±3.9 m if the craft is 300 m from the patch center**: nodes `x = L·t·|t|`
  are ≈150 m apart there, the detail noise (up to 30 m) is lost. 0/50/150/300 m from the center → max |Δ| 0.13/0.70/1.39/3.93 m
  (9 Moon points: maria, highlands, slopes). Rebuild threshold near the ground — `PatchNearMove` 30 m (±0.4 m), at speed —
  the path in 1 s; node heights — `Parallel.For` (129² in one thread 42 ms).
- **Cape Canaveral "in the water" — the patch ground tone, not the relief.** The core with a 15″ inset gives land (LC-39A +3.0 m, SLC-40
  +2.9, Merritt +1.4), but the patch ground is tinted with a single `SurfaceColor` at the center, and it was taken from `EarthSmall` 2048
  (texel ≈ 20 km): at LC-39A it is (50, 71, 93) — blue, the whole patch looked like water. Now `SampleLand` takes only
  dry texels (water: B ≥ G+10 and B ≥ R+20), otherwise — the nearest ring with land. On the 8k sphere image the cape is also almost
  entirely "water" (texel 4.9 km) — above the patch ceiling (40 km) this stays, as in the real image.

## Textures
- Generate as **atlases** (2×2 surfaces, 4×4 icons): `Tools/gen-texture.mjs` via Node from Cocos
  (`ELECTRON_RUN_AS_NODE=1 CocosCreator.exe Tools/gen-texture.mjs <out> "<prompt>"`, key — env `image_generator_api`),
  slicing `python Tools/slice-atlas.py surfaces|icons|macro` (grid cut 3 %, seamlessness by half-shift).
- There are no ready-made textures of planets/stars/regolith. NASA textures (Blue Marble, LROC, MOLA) and DEMs — download only with explicit
  user consent (name the file, source, size).
- **Sun color**: light 6500 K (not 5778 — after exposure it gave a yellow-orange frame) + scattering 0.3 (`SunLight`).
- **Sun flicker** — aliasing of the plume/disc jitter sine at low fps: keep frequencies below fps/2 or smooth them.
- **Plasma at night overexposed the frame**: the histogram on a black sky holds EV ≈ −3; lower EV limit from the brightness of the
  shock layer (`SkyController.PlasmaWhite` = 2, `VesselView.PlasmaPeakNits`). A "leading" halo — the center was at
  0.85 r in front of the nose; shock standoff 0.1 r (`ShockStandoff`).
- **The nozzle is not visible from all sides** — a one-sided bell mesh; `ProcMesh.Bell` is now double-sided (the inner
  wall with reversed winding).
- **HDRP/Lit and Unlit have no vertex color** — smoke density along the ribbon (`ExhaustTrail`) is set by width, and
  age by the U coordinate (points every 0.5 s, U = index/(N−1)); tail fade — in the texture along U.
- **Procedural meshes (Frustum/Bell) have no UV** — Metal_30 skin by triplanar in object space
  (`_UVBase` 5, `_ObjectSpaceUVMapping` 1): in world space the pattern would "swim" when the floating origin shifts.
- **The hot-staging truss is not visible** (02.10.2026): it is placed under the upper stage section at `y = −nr·1.4`,
  i.e. entirely inside the upper shell of the lower stage's procedural hull — from outside it is one cylinder. Fix:
  `VesselView.TrussHeight(i + 1)` shortens the lower stage's procedural hull by the truss height (not below
  half the length); the length in physics is unchanged. The condition in `TrussHeight` = the condition for placing the truss — change one,
  change the other.
- **Hull finishes (04.10.2026)** — 9 of them (`HullFinish`): sources `Tools/textures/hull_src`, maps
  `python Tools/gen-finish-textures.py` → `Textures/Hull/<Name>` (Albedo/Normal GL/Mask 1024²), materials
  `VesselLit_<Name>.mat` — `FlightSceneBuilder.HullFinishes`. A part's finish is chosen by the **palette slot color**
  (`VesselView.FinishOf`: FoilColor → foil, Capsule/Shield → ablation, Metal → stringers, Polished/Nozzle → steel),
  so the core and `HistoricRockets` were not touched. A new palette color = Painted until added to `FinishOf`.
  Self-colored finishes (Foam and onward in the enum) get `_BaseColor` white — otherwise the palette recolors the texture.
- **The generator returns 2048–4096 px, 2–17 MB, and "seamless" in the prompt guarantees nothing**: the seam at the edge is
  noticeably stronger than inside, plus uneven light across the frame. `Tools/make-tileable.py` looks for a period (window 0.6–1 frame where column x matches
  x+W), evens out the light by dividing by the circularly blurred (FFT) brightness and blends the edges only if the seam is ≥ 1.6×
  the interior. Result: seam/interior ≤ 1.43 for all albedos and normals.
- **The period along X and Y is searched separately → the tile gets stretched**: the Buran tile came out 1434×1844, cells became
  rectangular. Constraint `MAX_STRETCH` 0.06 (the per-axis windows differ by ≤ 6 %, otherwise the axis with the bigger seam
  is searched next to the other's window): it became 1434².
- **Normal from brightness is too strong by default**: gradient ×SIZE/16 gave 46 % of pixels with slope > 30°
  (the skin "boils" in any light). ×SIZE/64 → 7.8 % > 20°. Foam and ablation also get `hblur` 1.5–2 px: pixel brightness noise
  otherwise tilts the normal everywhere. Black tile — `h < 0`: the light parts there are seams, not bumps.
- **Tile in meters is fitted to the pattern** (`FlightSceneBuilder.FinishTileMeters`): tile 8×8 of 15 cm → 0.75 m
  (TilesBlack), paint 4 m (panels ~1 m), stringers 2 m. Change the image — check the scale.
- Generator — `node` is not in PATH: only Node from Cocos (`ELECTRON_RUN_AS_NODE=1`). Keep the prompt "orthographic top-down,
  no perspective, no cast shadows, even illumination" always (the shared `COMMON` part in the script).
  Paint came out "brick-like" on the first try (large rivets in rows) — prompt "few LARGE panels, sparse rivets".

## Brightness, night, smoke, skin (03.10.2026)
- **A light stripe on the hull in orbit is SSR, not Earthshine** (04.10.2026): bodies are drawn "in the shell" with substituted
  depth, screen-space reflection rays miss and lay the Earth's limb on the skin with an offset. Measured in 300 km orbit
  (camera Pitch 25, Distance 45): SSR on/off differed on 8,419 pixels (> 30 of 255, maximum 131) in a stripe along block A.
  Fix: `VesselLit.mat` `_ReceivesSSR = 0` + `HDMaterial.ValidateMaterial` (sets `_DISABLE_SSR`) — in `FlightSceneBuilder`.
  After: 6 pixels, maximum 38. SSR itself is left in the Volume (water, glass); the reflected Earth light is given by `NightLight`.
- **No stars with the plume at night and in orbit in sunlight**: stars are physical (0m ≈ 1 nit), visible only at EV ≲ −4,
  while the plume holds EV ≈ 7.3, the Sun — ≥ 12. Fix — `spaceEmissionMultiplier = 2^max(0, limitMin − StarsEv)`
  (`SkyController.StarsEv` = EvMin): stars as at the pad on a moonless night, by day in the air the limit is low → multiplier 1.
  Night launch measurement: ×2500 — stars are dim, ×8000 (EV −6) — readable; in 250 km orbit in sunlight — visible.
- **A half-frame plume halo at night is bloom**, not a glow mesh: bloom 0 → halo gone, mean 14.5 → 6.2.
  `SkyController.PlumeNightBloom` = 0.25 by `plumeK`. Night launch result: 4.9 / 0.09 % white.
- **Night at the pad like twilight** at EvMin −7 (mean 35 of 255) → EvMin −6: 18.4, stars and the Milky Way visible.
- The rocket hull is unlit at a night launch — the silhouette against the stars is barely visible (not fixed).
- **Square smoke edges at night**: `Smoke.mat` (HDRP Unlit/Lit, Alpha) has `_EnableBlendModePreserveSpecularLighting` = 1 by default —
  the plume light's specular is NOT multiplied by alpha, and a rectangle shows at the sprite edge. Invisible by day (no speculars), at night
  with plume light — a sharp square. Fix: key to 0 before `HDMaterial.ValidateMaterial` (`FlightSceneBuilder.SmokeMaterial`).
  Additionally: a 128² puff texture with a ragged edge and falloff to zero before the square's edge (`ExhaustTrail.PuffEdge*`),
  puffs raised by `size*PuffLift`, faded out right next to the camera (`PuffFade*`).
- **The plume overexposes the frame at night**: auto-exposure on a dark scene goes to high EV, the plume emission (3e3 nits) burns the frame —
  measurement of a Vostok night launch: mean brightness 87.8 of 255, 3.8 % white pixels. A lower `limitMin` alone is
  not enough (the dark scene stays black) — clamp EV into the band `[plumeEv, plumeEv + PlumeEvSlack]` and the upper limit
  (`limitMax`) too, with ramps (`PlumeRampUp` 1.5 s, `PlumeRampDown` 3 s) from `SunLight.Visible`. After: 8.6 / 0.4 %.
  `PlumeNightWhite` = 8 gave 5.0, 16 — 8.6 (in `SkyController`). Pair: `VesselView.CoreNits/GlowNits` and `BrightnessSettings.Plume`.
- **HDRP exposure does not adapt at `timeScale` ≈ 0** (adaptation runs in scaled time): a night orbit measurement
  at 0.0001 gave mean brightness 0.0. For measurements — `timeScale = 1` and a few frames before the shot.
- **Night in orbit was black** (mean 9.4): there is no sky glow, and the reflected Earth/Moon light is weak. `NightLight.SpaceGlowLux`
  = 0.04 lx in vacuum `(1 − air)·(1 − SunLight.Visible)` -> 46.6 (hull and Earth disc visible). Night is still darker than day.
- **Map at night** (`MapView`, fixed exposure, bloom 0): without light only the orbit is visible — mean 0.2.
  A directional "Map Fill" from the camera (`NightLight.MapFillLux`): at EV 13 2500 lx -> 1.0, 8000 -> 2.6 (Earth disc, orbit and marker readable).
- **Map overexposed by day** (03.10.2026): EV 13 gave the lit Earth (ρE/π ≈ 11,500 nits at 126,000 lx) 1.17 of white,
  and with the Esc brightness slider at +2 — ×4.7, a white disc. `MapEv` = 15 ("sunny 16"): Earth 0.29, clouds 0.78; `MapFillLux`
  ×4 = 32,000. A complaint "everything is overexposed" — first ask/check the slider: +2 EV = a frame four times brighter.
- **Brightness in the Esc menu**: `BrightnessSettings` (PlayerPrefs `kare.brightness.ev`, `.plume`). EV -> `exposure.compensation`,
  and the map's `fixedExposure` is shifted by `MapEv − comp`, otherwise the slider does not work on the map. Plume — a multiplier on
  emission and light (`VesselView`) and in the lower EV limit calculation.
- **HDRP 17.6 exposure compensation is subtracted BEFORE clamping by the limits** (`HistogramExposure.compute`: `clamp(AdaptExposure(avgEV −
  comp), limitMin, limitMax)`): `limitMin/limitMax` are the final EV. `SkyController` adds `+comp` to the plume/pad floor,
  assuming the opposite — at night with the plume the "General" slider works backwards: +2 EV = core 16/2² = 4× of white, i.e. the frame is darker
  (05.10.2026, the user has +2 set). The "Plume" slider at night also changes nothing: `PlumePeakNits` includes its multiplier,
  and exposure adjusts to the core — everything lit by the plume is the same brightness on screen. Not fixed (awaiting measurement).
- **HDR output does not cure overexposure**: HDRP computes in float anyway, overexposure is an exposure choice. An HDR monitor would only give headroom
  for the plume core above paper white. `HDROutputSettings.main.available = false` even with `useHDRDisplay` (Xiaomi monitor
  "Mi Monitor", Windows HDR off) — the setting was reverted.
- **HDRP Lit Mask Map**: R metallic, G AO, B detail mask, A smoothness. When a mask is present `_Metallic`/`_Smoothness` are ignored —
  drive `_MetallicRemapMin/Max`, `_SmoothnessRemapMin/Max`, `_AORemapMin/Max`. Detail Map: R tint, G normal Y, B smoothness, A normal X;
  `_LinkDetailsWithBase` = 1 — the detail follows the base's triplanar. Keys (`_NORMALMAP`, `_MASKMAP`, `_DETAIL_MAP`) — `HDMaterial.ValidateMaterial`.
- **Namespace**: `UnityEngine.Rendering.HighDefinition.HDMaterial` (not `UnityEditor.Rendering.HighDefinition`).
- **Hull textures** — `Tools/gen-hull-textures.py` -> `Assets/_Project/Textures/Hull` (Albedo/Normal/Mask/Detail, tile `HullTileMeters` = 2 m,
  seamless), assigned by `FlightSceneBuilder.HullTexturing` (also called from the build menu). The first version gave a noticeable checkered
  grid — seams 3 px, AO 0.4, stringer 0.1, panels 1×2; the grid is still slightly visible. Soot at the nozzles is NOT implemented: the tile is not tied
  to the stage, only general streaks along the axis.
- **A rocket launched at low `timeScale` falls and breaks up** (Situation Destroyed): launch only at `timeScale = 1`.
  `GameBootstrap.U/Instance` can be null after a script reload — `FindObjectOfType<GameBootstrap>()` or reload the scene.
- **Night pad floodlights burned the hull to white** (frame mean brightness 58, hull solid 255): the histogram drags EV
  by the black frame. Fixed with a lower EV limit by hull brightness: `LaunchPadView.LitNits` → `SkyController` (`PadHullWhite` 0.9).
- **Disk cache of body textures** (`Game/TextureCache.cs`, `persistentDataPath/BodyTextures`): editing the generators
  (`BodyRenderer.BuildTexture/BuildClouds/BuildNightLights`, `EarthSurface`) without bumping `TextureCache.Version` — the game will show
  old textures. DXT5 at runtime: `Compress` 293 ms per 8 Mtexels, raw data read 9 ms. "High" 23 s → 1.9 s from cache,
  "Ultra" 77 s → 4.1 s.
- **Clouds from low orbit are blurry at any texture size**: from 400 km a texel of 10 km (8192) ≈ 45 screen px. Texture size
  does not cure the blur — a local cloud patch or shader noise is needed (not done). On the map 2048 and 4096 are already indistinguishable.

- **RT shadows from the "old" patch (black zones with straight edges on the Moon)**: RTAS holds the BLAS per `Mesh` object; `Clear()` and
  refilling vertices in place with `RayTracingMode.DynamicTransform` do not reach RT — the shadow was cast by the relief of the patch's previous
  location. Measured 03.10.2026: true horizon 1.5° with the Sun at 8.2°, yet the zones are black; raster Sun shadows or `DynamicGeometry` —
  clean. Fix: in `BodyRenderer.RebuildPatch` a new `Mesh` and `Destroy` of the old one (rebuilds are rare).
- **Z-fighting of part bottoms ("glints with different textures")**: in `parts.blend` `Apollo_SM` has two cap discs in the plane
  z −4 (Hull 30 triangles + Metal 32, 11.9 m² each), `LM_Descent` — top/bottom duplicates (Foil/Metal, ~29 m²). Small
  overlays coplanar with the skin — LES, Atlas, LM_Ascent, Saturn_SIC, Vostok_Service. Search: bmesh, faces of the same
  plane (normal + d), a face center of one material inside a face of another. Fixed: duplicates removed (SM — Hull,
  LM_Descent — Metal), overlays raised 4 mm along the normal, FBX re-exported with `hulls_lib.export`. The "open
  edges" counter on the imported FBX lies: Unity splits vertices at UV/normal seams — weld by position (SM 1768 → 24).

## Moon: LROC map (03.10.2026)
- **The color LROC map (`lroc_color_poles_8k.tif`, NASA SVS CGI Moon Kit) is stretched for display**: mean brightness ≈ 0.33,
  while the Moon's geometric albedo ≈ 0.12 — without correction the Moon is overexposed. When converting to `MoonLroc8k.png` pixels × 0.377.
  The 50.6 MB source lies in `Tools/textures/` (in .gitignore), PNGs go into the project.
- **8k — DXT1, not BC7**: 85 MB of video memory versus 21 MB, no visible difference on grey regolith. For the patch color
  (`SurfaceColor`, called from `Parallel.For`) — a separate readable 1024 `MoonLrocSmall`: `Texture2D` must not be touched from threads,
  pixels are copied to an array in advance.

## Solar System Scope planet maps and rings (03.10.2026)
- **SSS maps are stretched for display, like LROC**: mean brightness is not the geometric albedo. `sss_convert.py` brings
  the mean to the albedo from the pair `NightLight.GeometricAlbedo` (multipliers: Mercury 0.62, Venus 1.25, Jupiter 1.32, Saturn 0.81,
  Uranus 0.86, Neptune 3.59 — Neptune clips, result 0.390 instead of the target). Mars — no correction.
- **Earth clouds — JPEG without alpha**: import `alphaSource = FromGrayScale` + `alphaIsTransparency`, otherwise the clouds are opaque.
- **Earth still builds a procedural texture** — for the ocean mask for the glint; after it the texture is destroyed and
  the SSS map is set. The land patch color — from the small readable map (`smallPx`, a pixel copy in Start: `Texture2D` must not be touched from
  `Parallel.For`), ocean — procedural.
- **Saturn's ring** — a flat annulus in local xz (uv.x — radius), normals set explicitly (FixWinding is
  meaningless for a flat ring), HDRP Lit transparent + double-sided, fog-on-transparent off, casts no shadows. Texture — Clamp,
  npotScale None. The Venus surface is not used: only the atmosphere is visible from outside.
- **Shuriken prefabs and the floating origin**: particles in `World` stay behind when the origin shifts and stretch into a trail —
  on the instance set `main.simulationSpace = Local`, `scalingMode = Hierarchy` (scale from the root). Vefects Free Fire
  is calibrated for a dark scene (`_EmissionIntensity` 33, quad `_EmissiveIntensity` 123) — for daytime exposure
  runtime copies of the materials ×25 (`BlastEffects.VefectsEmissionBoost`). The multiplier was chosen by calculation, not checked in Play.
- **JMO WarFX (built-in CG shaders) in HDRP**: 11 pack shaders + Legacy Particles without `LightMode` → HDRP draws them
  as SRPDefaultUnlit bypassing exposure and with built-in depth. Fix: our own `Settings/WfxParticleHDRP.shader`
  (HLSL, `LightMode=ForwardOnly`, the pack's formulas by `_Mode`, blending `_SrcBlend/_DstBlend`) + menu
  **Kare/Convert WarFX to HDRP** (`Editor/WarFxConverter.cs`, also called by the scene builder): 90 of 106 materials
  converted, 16 (Standard demo, `WFX/Transparent Diffuse/Specular` bullet holes) — not, they are pink. Reimporting the
  pack rolls shaders back — run the menu again. Color is written "screen-space" (without `GetCurrentExposureMultiplier`):
  the buffer is pre-exposed, 1 = white both by day and by night. Ground softness — `_SoftScale` via property block = scale.
- **Shuriken in our frame**: `gravityModifier` pulls toward world −Y, which is not "down" (Unity = ecliptic) →
  move to `forceOverLifetime` Local with the body's g. `Horizontal/VerticalBillboard` are also by world Y → `Billboard`.
  For a one-shot effect pause is only `simulationSpeed = 0`: `Play()` after `Pause()` restarts the child systems that already
  played out (the explosion repeats). Scale N — simulation speed 1/√N (Froude): then ballistics run with the real g.
- **Entry plasma is not a sphere** (03.10.2026): a billboard halo in front of the nose read as a sphere hanging in front of the capsule. The
  shock layer glows: a cap at Δ ≈ 0.14 r in front of the nose, flows around the shoulder and converges into the wake. Made as a surface-of-revolution mesh
  (`VesselView.SheathMesh`) with its own shader `Kare/Plasma Sheath HDRP`: glow ∝ 1/|N·V| (view path in a thin layer),
  at the silhouette itself a falloff to zero (otherwise a "soap bubble" rim), `Cull Off` — the back wall behind the hull is cut by depth.
  The shader goes into the build by reference `GameBootstrap.PlasmaShader` (Shader.Find — editor only). Not checked in Play.

## Height maps and body textures (04.10.2026)
- **Checking DEM registration to the image — by longitude-shift correlation** (`bake-dem.py --check`, FFT by rows, weight cos φ):
  peak at 0.0° (Moon +0.166, Mars +0.28, Mercury −0.13 by "hillshade"); flipped in latitude — ≈0, meaning north is up.
  Mercury correlates with a minus sign (bright slopes toward the Sun in the image versus height) — that is normal, the shift matters.
- **VenusMap.jpg — a cloud layer, not the surface**: correlation with Magellan ≈0 at any shift (peak 0.009 at 117°).
  Venus registration — by landmarks (Maxwell 9.8 km, Aphrodite 2.8 km).
- **Venus (USGS GeoTIFF) PIL cannot open** ("image file is truncated") — uncompressed strips back to back, read with
  `np.fromfile(offset=33415)`; Mercury (530 MB) — `np.memmap`, ISIS StartByte 92982 (1-based) → offset 92981.
- **PDS/ISIS maps start at 0° E** (LOLA, MOLA, MESSENGER) — `np.roll(−W/2)` so that column 0 becomes −180°;
  USGS Venus and ETOPO are already from −180°.

## RCS jets (05.10.2026, `Game/RcsJets.cs`)
- **Brightness in "screen units": `_EmissiveExposureWeight = 0`.** HDRP Unlit does
  `lerp(emissive * 1/exposure, emissive, weight)` (UnlitData.hlsl:75), at 0 color 1 ≈ white at any exposure —
  the jet is visible both on the daytime Earth behind the frame and in shadow. Symptom of the error — at weight 1 and plume-like nits the RCS jet is
  either a white sheet or zero: its real brightness is orders of magnitude below the plume.
- **A thin cone drowns.** 0.55 at the cut, radius 6 cm, length 1 m — on a Soyuz screenshot from 9 m (960×540) the jet is not visible
  at all (~3 px wide). Working: `JetScreen` 1.8, radius at the cut 0.1, gradient `exp(-2t)`.
- **Blocks at 45° to the axes → cosine 0.707.** A pure X/Z command with activation normalized to 1 gave the nozzle 0.55 —
  below the "solid" threshold (0.85), the jets only blinked by PWM. Normalize to `AlignFull` 0.7, not to 1.
- **RCS — only the torque remainder.** `TorqueCommand` includes nozzle gimbaling and fins; the RCS share =
  `(|cmd| − (tmax − rcs)) / rcs`. Without this at liftoff the RCS "burns" and the sound hisses (was in `FlightAudio` until 05.10).
- `VesselView.Rebuild()` destroys all children of the view — the jet root disappears; `RcsJets` rebuilds it on
  `root == null` and by the section signature (Attached/Flipped/IsEnclosed).
- **RCS nozzles — by model, not by section radius.** Quads at `s.Radius` at the top end on conical capsules and
  asymmetric craft hung in the air (Dragon 1.85 m versus a wall of 1.36 at the Draco height). Now
  `RcsJets.Clusters(SectionDef)`: a table for the Soyuz PAO, Dragon, Apollo SM, Mercury, Gemini, the Shuttle and
  Buran, for others — quads at the mesh radius at the block height (`SurfaceRadius`, vertex rings; an FBX without Read/Write is NOT readable
  in editor Play either — "Not allowed to access vertices" spam on Falcon 9/ISS/R-7 (05.10.2026); check `mesh.isReadable`, else a fallback radius). A new model with RCS — add it to the table.
- **The Apollo CM and the Gemini capsule give no jets while they have their service module** (`Shown`): otherwise at ShownShare 0.3 the
  Apollo's quads of both the SM and the CM burned (4·10³ and 2·10³).
- **The orbiter on the tank is beside the axis** — the RCS anchor must be shifted by `-BesideOffset` in X (the same condition as `Part.Radial` in
  `VesselView`), otherwise the shuttle's nose blocks shine from inside the tank.

## Plume light at night, "Earth glow through the ship" (05.10.2026)
- **The plume light was a constant 2·10⁶ cd on any engine** (× `BrightnessSettings.Plume`) at 3 radii below the cut.
  Surveyor on the Moon at night: 5·10⁵ cd (Plume 0.25) at 1.35 m below the cut, to the nearest skin 0.36 m → on the legs ≈ 2·10⁵ lx,
  at EV 7.29 the hull is hundreds of times brighter than white — "overexposes everything". Fix (`VesselView.PlumeLight`): luminous intensity =
  `PlumeWalls`·(core brightness·side area + the same for the glow), the glow's area and center are computed once from the mesh profile
  and gradient (`PlumeShape`). A surface near an extended source of luminance L is no brighter than ≈ ρ·L — the hull cannot
  overexpose beyond the plume itself. Surveyor result in vacuum, throttle 1: 380 cd,
  center 1.28 m below the cut; EV 7.29 → 5.55; frame mean brightness 960×540 from below 43.6 → 3.6 (white 3.97 % → 0.02 %),
  from above 27.0 → 3.3 (`night_pinned_after.png`, `night_above_after.png`). R-7 by the same formula ≈ 10⁴–10⁵ cd (was 2·10⁶·Plume).
- **`ReportPlume` went without the vacuum multiplier**: exposure was set for a core three times brighter than the drawn one (−1.7 EV).
  Now exposure uses the actual core brightness `coreNits` (without jitter).
- **At night with the plume the surroundings are black — that is physics, not a light bug.** The hull at the cut ≈ 21 nits (380 cd at 1.3 m, ρ 0.3),
  the ground in reflected Earth light 0.8 lx ≈ 0.03 nit — 700 times darker. At any EV where the hull is not burned out, the ground is black.
  The knob — `SkyController.PlumeNightWhite` (16): more — the ground is more visible, but the core and legs go white. Adaptation itself is not to blame: measured EV −6.00 →
  6.36 in 0.06 s → 7.29 by 0.3 s.
- **"Earth glow through the ship" is dust under the nozzle, not the Earth and not SSR.** Orange blurry spots — `ExhaustTrail`
  puffs (Smoke Puff, alpha 0.16–0.24, 3–5 m, at 20–40 m), lit by the same 5·10⁵ cd. Through the hull — because the
  camera (Pitch −35°, 6 m) went BELOW the relief: from below the ground is culled by back faces, puffs on the ground end up between the
  camera and the hull; the dark rectangle is a piece of the patch from below. Verified: the same frames with puffs off — no spots
  (`Temp/Shots/night_pinned_before.png` / `night_pinned_nodust.png`). Fix — `FlightCamera.GroundClearance` 2 m
  above `AltitudeAboveTerrain`. The SSR fix of 04.10 is intact (`_ReceivesSSR 0` on all `VesselLit*.mat`), the Earth is irrelevant.
  Camera after the fix — 2.00 m above the relief at Pitch −35°, 6 m (was under the ground).
  Symptom: the spot "shines through" the hull only when looking from below upward — check the camera height above the relief.

## Part icons (PartIconBaker, 05.10.2026)
- **HDRP has no alpha in the color buffer** (R11G11B10): `ReadPixels` from an ARGB RenderTexture gives alpha 1 everywhere. Transparency —
  by difference matting of two passes: background 0 and background `backgroundColorHDR` = 20000 (at EV 12 ≈ 4 → ACES to white). 1e5 will not do —
  above the R11G11B10 ceiling (~65000), the background goes to Inf/garbage. A sample is part if the difference < 0.5 of the background difference in the corner; render 4× without AA,
  alpha = share of part samples, color — from the black pass. Verified: 0 of 78 icons went onto a dark backing (threshold 0.25·255).
- In the icon camera's frame settings turn off post and screen effects (Bloom, Vignette, CA, FilmGrain, Dithering, MotionBlur,
  DoF, LensDistortion, Panini, SSR, Volumetrics, AtmosphericScattering, RayTracing, LensFlare) and AA/DLSS — otherwise bloom and
  dithering leak into the background difference and give a grey halo.
- Preview scene (`EditorSceneManager.NewPreviewScene`, `camera.scene`) + layer 31 for the camera, the volume and the lights' `cullingMask` —
  does not dirty the active Flight scene (after baking `isDirty = False`).
- Dark parts (nozzles, black skin) vanish on a dark UI: a rim light is needed (Rim 14000 lx) and an albedo floor of 0.24 in the MPB.
  Wings from the rocket's angle are seen edge-on — for the icon turn them flat to the camera (`FaceCamera`).
- Thin parts are small in frame when fitted by bounds: coverage of Sputnik 1 % (antennas set the bounds), legs 6 %, SRBs 7–9 %.

## Winged FBX: landing gear, "sideways" plumes, palettes (05.10.2026, `VesselView.AddGear/OwnPlume`)
- The section model (`SectionModel`) disables the `WingMesh` plates itself: the wing is drawn only when `model == null`.
  The wing physics stays from the section data.
- The orbiter is a `Beside` section (`Radial = (-BesideOffset,0,0)`), the SSME nozzles are on it, while the engine in the data is on the ET tank.
  Without `OwnPlume` the plume was drawn from under the tank; now the ET with a docked `Beside` moves the plume to the SSME
  (x = −BesideOffset+0.3, rr 1.2, nr 1.12), Buran — to the ODU (rr 0.95, nr 0.42).
- Blender (x,y,z) → Unity (−x, z, −y): the orbiter's belly at Blender −X ends up at Unity +X (verified with the orbiter's bounds x −10.6…2.64).
  For radial blocks Unity +X is outward, so the attachment side is Blender +X.
- `Landing_Gear.fbx`: origin at the hinge, reach 2.4 = height 1.8 + embedding 0.6 (paired with
  `GearModelReach/GearInset/GearModelHeight`). Stowing by 90° left the tire 0.1 m below the wing — `GearStow=95` was taken.
- ET fittings on the orbiter side (−4.86) go into the belly by ~0.6 m — hidden, not a bug.

## Draco, parachute fan, hinge of a "closed" part (05.10.2026, `Game/VesselView.Dragon.cs`)
- **The Draco plume is not on the axis.** The axial `OwnPlume` of Crew Dragon went through the trunk. The first group's plume is placed on
  the wall (DracoY 2.85, DracoR 1.33, az 45°) tilted 40° outward, three copies — `Part.Mirrors` (scale and PropertyBlock
  are copied every frame in `SyncMirrors`, light only on the main one). No smoke (`Part.NoSmoke`): otherwise `ExhaustTrail`
  streams from the middle of the capsule.
- **Canopies as a cluster:** the area is divided by `ChuteCount`, centers spread by `r·1.1/sin(π/n)`, lines ×1.5. `ChuteReach`
  (the view boundary) computes the same geometry via `ChuteFan` — without this the canopies are clipped by view culling.
- **The part is modeled closed** (the Dragon nose cone), and the `Hinge` hinge rotates by `Stow·(1−D)` from `Base`: take
  `Base` = the open rotation, `Stow` = −angle, `Rest = Pivot + open·(−Pivot)`. Otherwise (as with legs modeled
  open) the closed nose cone hangs rotated by 115°.

## PBR fog above the atmosphere, winged plasma, patch seam, adaptation (05.10.2026)
- **HDRP PBR fog lies on all opaque geometry, even with Fog off.** `Fog.IsPBRFogEnabled` =
  PBSky && `atmosphericScattering` && frame setting; the Fog override has no effect. The `OpaqueAtmosphericScattering` pass
  has no stencil, a material cannot be excluded.
- **A blue limb line and a light-blue haze on the hull with the camera above the atmosphere** (complaint "limb over Crew Dragon", 4.png) —
  an HDRP 17.6 bug, `AtmosphericScattering.hlsl` → `EvaluatePbrAtmosphere`: `rayEndsInsideAtmosphere = tFrag < tExit
  && !hitGround` does not check `tFrag > tEntry`. A fragment closer than the ray's entry into the atmosphere gets
  L(entry, exit) − T·L(point outside the atmosphere, exit) ≠ 0, bright at the limb. In the project config
  `PrecomputedAtmosphericAttenuation = 0`, so it is computed per pixel. Verified by toggling
  `atmosphericScattering`: the line on the hull at the limb row and the haze disappear, but the Earth's haze changes too, so it
  cannot be turned off. Fix — `Game/SpaceFogFix.cs` + `Settings/SpaceFogRestoreHDRP.shader`:
  - the color is saved in AfterOpaqueAndSky and restored in BeforePreRefraction (after the fog) for pixels closer than
    `|camera − center| − (R·k + atmosphere top + 100 m)`;
  - the atmosphere top is a copy of internal `GetMaximumAltitude`, ≈ 55.3 km for Earth, and it is NOT scaled by the shell k;
  - at 400 km `RestoreDist` = 344,657 m;
  - inside the atmosphere the pass is off.
  Transparents with fog near the camera in space are not cured this way (the plume and clouds have `_EnableFogOnTransparent 0`).
- **Winged plasma along the flow.** The axial "cap" on the orbiter at a 40° angle of attack hung over the back. Now the layer
  is built in wing axes (belly +X, span Z) from the windward side: `VesselView.WingedSheathMesh`, `WingPlanform`.
  Layer length ≈ 14.3 r (r 2.6 m for the orbiter). The mesh is rebuilt only when L or the wing shape changes (`sheathWing`).
  Otherwise it was rebuilt every frame.
- **Patch/sphere seam at Earth.** The patch draws its own ground and the sphere draws the map, and at 5–30 km a "patch" is visible.
  Fix — an HDRP albedo decal over the patch with the map's colors (`BodyRenderer.AddPatchDecal`, 128², box 160 km):
  - alpha at the edge 0.45→0.95 of the half-width;
  - from altitude 2→15 km the map also lies over the middle (up to 0.85).
  `DecalProjector.drawDistance` defaults to 1000 m — the decal vanished already from a kilometer, set 1e7.
- **Auto-exposure is slow.** HDRP's default adaptation speed is 1/1; taken dark→light 2, light→dark 5
  (`SkyController.AdaptDarkToLight/AdaptLightToDark`, also set by FlightSceneBuilder). `PlumeRampDown` 3→1 s — otherwise
  the EV clamp from the plume held the frame longer than the adaptation itself.
- **SpaceFogFix check — only with the limb BEHIND the hull.** In a random framing (Teleport 400 km, yaw 300) fix on/off
  differ by 0.04–0.11 mean — nothing to cure there, which looks like "the fix does nothing". Reproduction: orbit 70° from the
  subsolar point, camera aimed at the limb 15° off the sun azimuth (sun elevation ≈ 20°), `timeScale 0`, three shots — fix off /
  fix on / `atmosphericScattering` off. On the 11k changed px (line at rows 546–550): fix off B−R 46, fix on 9.5,
  no-atmosphere reference 9.2; |on − reference| 2.0 vs |off − reference| 47.
- **`Instantiate(VolumeProfile)` is shallow: components stay shared with the asset.** `SkyController.Start` made a "runtime
  copy" this way, so exposure/sky overrides in Play leaked into `Settings/FlightVolume.asset` (on disk: fixedExposure 14.9,
  compensation 0.098, atmosphericScattering flipping 0/1 depending on the last body). Verified: `ReferenceEquals(copy.components[0],
  asset.components[0]) == true`. Fix — `Instantiate` every component of the copy too (after it — `sameObj=False`). The same applies
  to any test code: edits through `vol.sharedProfile` in Play used to hit the rendered profile, now they do not — use `vol.profile`.

## Split screen / second view (05.10.2026, `Game/FlightView.cs`)
- The second view is a clone of the main camera (`Instantiate`, AudioListener removed, tag `Untagged`), FloatingOrigin stays at
  `FlightView.Main`. Measured 05.10.2026 in Play (Demo-2): the landed F9 on OCISLY seen from the ship's origin at 1.25e7 m
  (float ulp ≈ 1 m) — no visible jitter at a 40 m stage. Terrain patch is only built under `Main` — the second view sees the
  coarse sphere mesh. Sky/exposure follow `Main`: once the right half (ship, booster as Main in daylight) showed only stars —
  probably the ship on the night side under day exposure; not confirmed.
- Things that face `Camera.main` (ExhaustTrail billboards, VesselView glow, NightLight, BlastEffects, SpaceFogFix) are oriented
  to the main camera only — in the right half they may look flat/turned. Sky/exposure/sun follow `Main` too.
- Objects hidden by distance (`VesselView`, `RecoveryDeckView`, `LaunchPadView`) check `FlightView.Near(pos, dist)`,
  not `pos.magnitude` — with it the barge is drawn in the right half 12 000 km from the origin (split3, 05.10.2026).

## Plasma sheath follows the parts, not the flow (05.10.2026, `VesselView.BuildSheathMesh`, `PlasmaSheathHDRP.shader`)
- Symptom: Starship falling belly-first got a sheath shaped like a capsule along the flow (old `SheathMesh(L)` was a surface
  of revolution from the leading end; winged vessels had a separate `WingedSheathMesh`).
- Now one mesh for every vessel: hull silhouette from the section stack (profiles mirror `Rebuild`) plus real wings/fins as
  elliptic lofts. The mesh is rebuilt only when `builtSignature` or the flip mask changes (staging), never per frame.
- The flow goes to the shader every frame as `_Flow` (heading, local axes of the mesh): vertices are pushed along their normals
  on the windward side (`N·F > 0`) and dragged back along `-F` on the lee side, brightness `~ (N·F)^2`. Same mesh works for
  nose-first, belly-first and tail-first.
- Starship flaps (`StarshipFlaps`, Area 1) are symbolic and skipped (`WingSheathMinArea = 4`); do not use `Surfaces != null`
  as the "real wing" test — the orbiter and Buran have it too. Wings of flipped sections (§6.6) are skipped.
- Big flat undersides brighten the frame: brightness is scaled by `(4r²/projected area)^AreaGlowExp`, clamped, 1 for capsules.
- Keep `ReportPlasma(PlasmaNits*k)` — night exposure depends on it. Keep `MaterialPropertyBlock.Clear()` and use `SetVector`
  for tints (SetColor would gamma-convert them).
- Not checked in Play (visual): FBX parts are approximated by the procedural profiles of `Rebuild`.

## Starship flaps, pad colours, ground ambient (06.10.2026)
- **Flap stow angle < hinge azimuth (~80°):** at 100/110° the flaps rotated into the hull and "disappeared" in flight. Now 55°/5°.
- **Palette colours are sRGB:** `SetColor("_BaseColor", 0.16)` = 0.02 linear — R-7 equipment shelters read as black cubes
  (19, 19, 17) next to sunlit concrete (111, 106, 94). Dark slot 0.36 → (55, 52, 46) with visible faces.
- **groundTint = ambient of shadowed vertical faces:** Earth `BodyLook.Low` (green) painted the pad walls at Baikonur swamp green
  (26, 41, 28). Fix: `SkyController.LocalGround` — biome hue under the vessel at Low's luminance (keep luminance: groundTint
  also brightens the haze) → (40, 39, 38).
- **Old concrete atlas** had big black stains that repeated as "dirt" every tile on a 44 m pad — replaced by the procedural
  `Tools/gen-concrete.py` (periodic FFT noise, seamless).

## Constructor preview and vapor (06.10.2026)
- "First part under the floor": a swept wing's tips go below the part base (WingMesh: tip y = −half·tan sweep, −2.89 m on a
  wing-only craft). Fit the preview to the floor by **renderer bounds** (`FitToFloor`), not by the part layout.
- `Destroy` is deferred: old preview children still count in bounds in the same frame → `SetParent(null, false)` before Destroy.
- Condensation effects (vapor cone, wingtip vortices) — lit transparent smoke material, not emissive: additive emission is
  invisible in daylight below ~1e4 nits. LineRenderer with a lit material needs `generateLightingData = true`, else it is black.
