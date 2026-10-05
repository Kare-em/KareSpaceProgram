# Pitfalls: MCP, the Unity editor, file editing

Read before working through MCP, debugging in Play, and bulk file edits.

## MCP
- **Since 04.10.2026 the transport is stdio, package and server v10.3.0.** Unity listens for the bridge on 6400 (`~/.unity-mcp/unity-mcp-status-*.json`,
  `last_heartbeat`), `EditorPrefs MCPForUnity.UseHttpTransport = false`. The server is `uvx --from mcpforunityserver==<version>
  mcp-for-unity --transport stdio` in `.mcp.json` (UnityMCP) and in `%APPDATA%/Claude/claude_desktop_config.json` (unityMCP).
  **Updating**: `manage_packages add_package` with a git URL `...#vX.Y.Z` (~16 s, no console errors) + the server version in both
  configs; the new server is picked up only by a new session. Check without a session: a stdio client in the scratchpad
  (initialize → initialized → tools/list → tools/call `execute_code`): 04.10 — 48 tools, response "10.3.0".
  Latest version: `git ls-remote --tags https://github.com/CoplayDev/unity-mcp.git | sort -V`, PyPI `mcpforunityserver`.
- Everything below about HTTP/8767 is the old mode (the `mcp-for-unity.exe --transport http` process may linger from a previous launch).
- `.mcp.json` → `http://127.0.0.1:8767/mcp`. Car_Train occupies 8765 — the port is stored in EditorPrefs machine-wide,
  so `McpPortPin.cs` overwrites it when the editor loads.
- Check: `curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:8767/mcp` → 406 = server is alive.
  If the server is alive but the session reports ECONNREFUSED — reconnect via /mcp (the session started before the editor).
- **The `manage_camera screenshot` screenshot** writes only inside the project: `output_folder: "Temp/Shots"`.
- **Compilation**: `refresh_unity` compile=request, mode=force, scope=all; sometimes it stays "idle" with no build — check that
  the new type/field appears via `execute_code`, repeat if necessary.
- **`execute_code` without `action:"execute"` fails**; codedom (C# 6, no `dynamic`) — no chained `StringBuilder.Append`,
  build the string with `+`. `Object` is ambiguous there (System/UnityEngine) — write `UnityEngine.Object`;
  `Universe` has no list of bodies — use `u.System.Get("moon")`.
- `read_console types` — as a list.
- **A PNG larger than ~1.4 MB does not reach a remote viewer via `SendUserFile`** (timeout, 03.10.2026) — it is visible
  on the desktop. For a phone, shrink it (JPEG/half of Full HD).
- **Port 8767 answers 406 even with the editor closed** — that is the Python server `mcp-for-unity.exe` alive, not Unity.
  Symptom: tools return `no_unity_session`. Check `tasklist | grep Unity.exe`; launch —
  `"/c/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Unity.exe" -projectPath <project> &`.
- **Adding `com.unity.modules.nvidia` did not recompile the HDRP package DLL** (02.10.2026): the HDRP asmdef
  already has `ENABLE_NVIDIA_MODULE`, but `Library/ScriptAssemblies/Unity.RenderPipelines.HighDefinition.Runtime.dll`
  stays yesterday's → `DLSSDetected = false` on an RTX. Symptom: the IL of `DLSSPass.SetupFeature` = 2 bytes (via reflection,
  `GetMethodBody().GetILAsByteArray()`), no NVIDIA in the assembly references. Fixed by
  `CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache)` (~13 s reload).

## Debugging in Play
- `execute_code` → `Kare.Space.EditorTools.FlightDebug` via reflection (`Reentry(alt, speed, γ°)`, `Stage`, `Status`,
  `Lift(alt, up)`); acceleration — `Time.timeScale`; the fields `FlightCamera.Yaw/Pitch/Distance` are public (Yaw from north).
- **Teleporting a vessel**: first `v.Situation = Flying` (otherwise `UpdateLandedPose` returns it to the pad), and pause
  with `manage_editor pause` — it is a toggle: the second call releases the pause.
- **A frame of any vehicle without flying** (03.10.2026): in the editor `GameBootstrap.MissionId = "<id>"` → play →
  `var u = GameBootstrap.U; u.Teleport(u.System.Get("moon"), 15e3, false);` + `u.Stage()` until the desired set
  (`u.Active.Attached`), `Throttle = 0`. After stop, restore `MissionId = "vostok"`. Sections under the fairing
  (`Vessel.IsEnclosed`) are not drawn — the LM is not visible on the pad, this is not a bug.
- **Changing the mission in Play** (03.10.2026): `GameBootstrap.NextMissionId = "apollo11"; SceneManager.LoadScene(0)` —
  `execute_code` answers with a timeout, but the scene loads; check with the next call. Editing a `.cs` during Play →
  domain reload: a stream of NREs from `GameBootstrap` and `no_unity_session`: stop with `manage_editor stop`, clear the console.
- **Winged entry from a teleport** (05.10.2026): `Reentry(...)` + `new MissionAutopilot` on sts1 starts the *ascent* (OMS burns to orbit)
  — first set `Tracker.Done[<Orbit objective>] = true`, then `WingedGuidance` takes over at α 40°. The mission sets warp ×10 itself and
  `AutoWarp = false` does not stop it: from 120 km peak heat passes in ~20 s real time (missed once, the orbiter flew 2000 s and crashed
  11 000 km short of Edwards). For a plasma frame start at 78 km, 7300 m/s, γ −0.6° and shoot right away (0.3 MW/m², `Pitch = -25` shows the belly).
- **`Mission.Status` freezes during docking** ("Манёвр: осталось 0,1 м/с" for the whole transfer) — the live text is `u.Docking.Status`.

## Editing files (Windows, Git Bash)
- A long Python in a Bash heredoc fails — write the script to a file (scratchpad) and run `python file`.
- Replacements via Python: `io.open(..., newline='')`, assert on the number of occurrences. **Line endings differ**: the `.cs` files of the game
  layer and the core (VesselView, LaunchPadView, VesselDesign, FlightSceneBuilder) are LF, not CRLF; determine it per file.
- `sed -i '<N>r chunk'` after any `Edit` of the same file — the line number has already shifted (the insertion landed inside a
  doc comment, 02.10.2026). Insert via a Python replacement on an exact anchor line.
- Do not use `perl -i` on files with Cyrillic — it corrupts the encoding.
- The anchor of a Python replacement must be unique: identical lines in neighbouring presets (Karman/Freedom 7) give
  count=2 — take an anchor that includes the neighbouring unique line.
- `sed` inserting `\r\n` into an LF file (FlightSceneBuilder) yields mixed line endings — determine the file's EOL first.

## Blender MCP (low-poly parts)
- Server: `.mcp.json` → `uvx --python 3.12 blender-mcp` (ahujasid), addon `blender_mcp_addon.py` v1.8 in
  `%APPDATA%/Blender Foundation/Blender/5.2/scripts/addons`, Blender 5.2 in `C:/Program Files/Blender Foundation/Blender 5.2`.
  In Blender: N-panel → BlenderMCP → Connect (socket 9876). Check from the machine:
  `timeout 40 uvx --offline --python 3.12 blender-mcp < /dev/null` → the log shows "Successfully connected to Blender on startup".
- Tools appear only in a session started AFTER editing `.mcp.json` (and after approving the project server);
  on 02.10.2026 the server and socket were alive, yet the current session had no `blender` tools.
- Export: `Assets/_Project/Models/<part>.fbx`, metres, Apply Transform; the rocket axis in Blender +Z → in Unity +Y
  (vessel nose) — confirmed on 11 parts on 02.10.2026. `export_scene.fbx` parameters: `bake_space_transform=True,
  apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y', object_types={'MESH'}, use_selection=True`.
  Without them the import gave fileScale 0.01, a 270° rotation and scale 100. The model origin (bottom/top/hinge) and dimensions are in the constants of
  `VesselView`/`LaunchPadView` and in the Tooltips of `GameBootstrap` fields; if you change the model, re-check bounds (`mesh.bounds`).
- **An FBX has several submeshes** (by Blender material): `sharedMaterials` is an array of length `subMeshCount`,
  otherwise only the first submesh is drawn.
- **`manage_camera screenshot` writes to `Assets/Screenshots/`** (ignores `output_folder`, accepts `screenshot_file_name`)
  — after shooting, delete the file and `Assets/Screenshots(.meta)`. `camera: "<name>"` shoots without the OnGUI HUD.
  Its "up" is world Y, while the local vertical in the scene is tilted (P with SwapYZ + floating origin) — the frame comes out tilted.
  Working frame (HDRP, 02.10.2026): a clone of `boot.Camera`, disable all MonoBehaviours except `HDAdditionalCameraData`,
  remove the AudioListener, `targetTexture` = RT 1920×1080, `enabled = false`; `rotation = LookRotation(target − pos,
  vessel.up)`, 12× `cam.Render()` (auto-exposure catches up) → `ReadPixels` → PNG into the scratchpad. No files in Assets.
- **Demo vessel next to the main one** (showing parts): `new Vessel(VesselPresets.ById("vostok"))` (`VesselDesign`
  has no `ById`) + you must set `Body/Position/Attitude` from the active one — otherwise an NRE in `FloatingOrigin.WorldP` during
  `VesselView.Init`; set the view's `enabled = false`, set the position by hand.
- **Test near a body: teleporting below ~1 km = impact** — `Teleport(Moon, 200 m)` + jettisoning stages gave "impact 103 m/s"
  and `Alive = false` (the view stops rebuilding). Use 15 km, like the cheat in Esc.

## How to call UnityMCP when the tools are not in the session
- 03.10.2026: the editor is in HTTP mode — the stdio tools of `unityMCP` answer "No Unity Editor instances found" (port 6400 is not
  listened on, this is normal), and the session's HTTP connector gives ECONNREFUSED if it started before the server. The working path is an
  HTTP JSON-RPC client (`python mcp.py <tool> <json|file.json>` in the scratchpad: initialize → initialized → tools/call,
  `mcp-session-id`, SSE `data:`). Arguments containing C# code go as a JSON file, otherwise quotes break in bash.
- `RenderProbe.Shot` (cam.Render into an RT) in Play gives a black frame and EV NaN — shoot with `manage_camera screenshot`
  (`output_folder: Temp/Shots`), compute brightness with PIL on a downscaled copy.
- Night for measurements: Play → `u.SetWarp(6)` on the pad (×10⁴) → wait for `u.Time` +50 000 s (≈02:30 local time at Baikonur) → `SetWarp(0)`.
- 02.10.2026: the session has two tool sets — `UnityMCP` (capitalised) answers `no_unity_session`, the working one is
  `unityMCP` (lowercase, stdio). It has no resources (`editor/state` cannot be read) — get state via `execute_code`.
  Right after `play` there is a domain reload, "No Unity Editor instances found"/timeout: just repeat the call.
- 02.10.2026: after /mcp and approval, the `UnityMCP` tools never appeared in the session, while the server on 8767 is alive.
  Workaround — a JSON-RPC HTTP client (`initialize` → `notifications/initialized` → `tools/call`, header
  `mcp-session-id`, SSE response `data:`); C# via `execute_code`. A script of ~50 lines, write it to the scratchpad.
- An edit that failed with "file in use" while Unity is open is a one-off import lock: rerun the remaining replacements.
  Python `io.open(p,'w')` then gives `OSError [Errno 22] Invalid argument` (02.10.2026 — twice in a row on
  GameBootstrap.cs, with both an absolute and a relative path); the `Edit` tool wrote without error in that same minute — edit with it.
- **Installing Blender MCP overwrote `.mcp.json`** — `UnityMCP` vanished from the file. Restored by hand, but a session
  that started without it does not see the server (`session_connectors_status` — only `blender`). Only the user can fix it:
  /mcp → approve UnityMCP, or a new session. After installing any MCP, check `.mcp.json`.
- **FBX from Blender → Unity: axes (−x, z, −y)** (03.10.2026 measured `mesh.bounds` of Pad_Atlas): the model is rotated 180° around Y.
  For Blender-x to remain east and y north in the pad basis, the child node gets `localRotation = Euler(0,180,0)`;
  then Blender (x,y,z) → pad (x, z, y). Scale along the length (Blender y) = `localScale.z`.
- **`remove_doubles` merges the material-slot placeholders**: Unity collapses an unused FBX slot, the submeshes
  shift. The placeholders of each slot are in different places; check after building: `slots == [0,1,2,3]` for all Pad_*.
- **`get_viewport_screenshot` in Blender returns a stale frame after changing the view via bpy**; reliable — a Workbench render
  from a service camera. `hide_set` hides only in the viewport, for rendering also set `hide_render = True`.
- The `execute_blender_code` namespace is not preserved between calls — in each call use `exec(open(...).read())`.
- **`PlayerPrefs` in a MonoBehaviour field initializer** → UnityException (call from a constructor). Read lazily
  (`DetailSettings.Level`) or in `Awake`; in `MapView` the buffer is empty and is resized in `Draw`.
- **`Destroy` of someone else's texture** ("Destroying object UnityWhite is not allowed"): when rebuilding LODs free only your own —
  `BodyRenderer.Own/Free` with a HashSet, do not touch the placeholder `Texture2D.whiteTexture` and assets.
- **A long `execute_code` (generating the "Ultra" level ~70 s) drops on the MCP timeout**, while Unity finishes the work.
  The result is `Debug.Log` and then `read_console`, not a return value.

- **Recompiling during Play → NRE every frame** (`GameBootstrap.Update`): the domain reload zeroes non-serialized
  fields, `Awake` does not repeat. `Update` has a check with a warning; in EditorPrefs `ScriptCompilationDuringPlay` = 1
  ("recompile after exiting Play", was −1). Before editing code — `manage_editor stop`.
- **Bash `cat > file` with no heredoc/input hangs forever** waiting for stdin (the editor launch hung). For all MCP/Blender commands use
  `< /dev/null` and `timeout`.
- **Blender without the MCP addon**: `"/c/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b Tools/blender/parts.blend
  --python script.py < /dev/null`; export — `hulls_lib.export(name)` (sys.path to `Tools/blender`), before saving
  `preferences.filepaths.save_version = 0`, otherwise `parts.blend1` appears next to it.

## Blender without MCP output (03.10.2026)
- **`execute_blender_code` executes but does not return `print`** — there is no way to check geometry. The working path is headless:
  `"/c/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b Tools/blender/parts.blend --python script.py < /dev/null`
  and grep by a prefix in print. Export — `hulls_lib.export(name)` (add `Tools/blender` to `sys.path`), before saving
  `preferences.filepaths.save_version = 0` (otherwise `.blend1` files multiply), afterwards delete `Tools/blender/__pycache__`.
- **`o.dimensions` is not updated after `bm.to_mesh` in the background** — check the size from vertices or by re-importing the FBX.

## Deployables: legs and ramps as separate FBXs (03.10.2026)
- **The legs of Surveyor/LM and the CT ramps were separated from the hulls** by the script `Tools/blender/split_deploy.py` (one-off: run again
  on an already-split parts.blend it will find no parts). Mesh islands → grouped by the nearest leg azimuth → object
  `<Hull>_Leg_<k>` / `Luna17_Ramp_<k>` in hull coordinates; FBXs `Surveyor_Legs`, `LM_Legs`, `Luna17_Ramps` — one
  object per leg. Result of the split: Surveyor 3×124 vert., LM 4 legs (az 90 — 570 vert. with the ladder, the others 314), CT 2×72.
- **On the LM the platform at the hatch (z > 3.05) is also an island with r > 2.4**: without a height cutoff it goes off into a leg. The ladder is on the
  front leg (az 90), so do not clone the legs from one, cut each.
- **The CT ramps were cut by the Lunokhod wheels**: in the "Lunokhod" model the base was 2.2 m (wheels out to ±1.36), while the folded ramp
  sits at r 1.17–1.2. The wheels were moved to the real base of 1.7 m (axes ±0.2833/±0.85, edge ±1.105 — a pair with
  `FlightPhysics.RoverHalfBase` 0.85), the ramp is folded at 116° instead of 120° (top 4° outward, r ≈ 1.4 < fairing 2.05).
- **The Lunokhod lid is a separate FBX** `Lunokhod_Lid` (`Tools/blender/lunokhod_lid.py`, one-off: it will not find the lid on an already-split
  parts.blend): islands z ≥ 1.33 / extending forward y < −0.85. The model is in the open position,
  hinge r 0.8 h 1.4, closed at 162° (`VesselView.DeployHinge`). Antennas that stuck out above the lid were moved
  forward (y > 0.64), otherwise the closed lid went through them.
- **The hinge direction is taken from the foot, not the part centre**: the Surveyor's struts move the bounds centre ~10° away from the leg axis.
  FlightSceneBuilder takes the centre of vertices in a 0.3 m band above the bottom of the part (`DeployFootBand`).
- **Hinges (`VesselView.DeployHinge`) are a pair with parts.blend**: the axis radius/height = top of the strut (Surveyor 0.6/0.9, LM
  2.15/3.0, CT deck edge 1.2/1.9; meshes: Surveyor y≤0.93, LM r≥2.07 y≤3.04, ramp z≥1.17 y≤1.94). If you change the model,
  re-check. A part's material slots are looked up by material name in the hull.
- **`execute_code` via mcp.py: an escaped newline (backslash + n) in a C# string arrives as a real
  newline** → "Newline in constant". Separators in the output — `" ### "`.

## Lunokhod wheels and Play (03.10.2026)
- **The wheels are `Tools/blender/lunokhod_wheels.py`** (one-off, after `lunokhod_lid.py`): mesh islands within the wheel envelope
  (|x| 0.70–0.90, z 0–0.51, axes y ±0.283/±0.845) → `Lunokhod_Wheel_0..7`. The pivot is the centre of the mesh bounds
  (`FlightSceneBuilder.DeployParts(wheels: true)`), the view rotates the node around the model's X.
- **While the user is in Play, compilation is deferred**: `Kare/Build Flight Scene` fails with "cannot be used during play mode", and
  `execute_code` does not see new members (`WheelsFor`). Do not stop Play — wait for the exit, then build the scene.
- **New maps/assets are not visible in Play** — the scene was not rebuilt after the addition (`BodyRenderer.Maps`, `SaturnRing`
  are assigned only by `Kare/Build Flight Scene`). Symptom: the texture GUID (from `.meta`) does not occur in `Flight.unity`.
- **VFX Graph cannot be built via MCP**: `manage_vfx` creates `.vfx` only from a template and tweaks exposed parameters, while the
  templates have 0 of them, nodes cannot be edited. Effects — in code (billboards) or with third-party Shuriken prefabs.
- **Check Asset Store packs for the render pipeline**: Rainy VFX (material `Default-Particle`, `Legacy Shaders/Particles`) and
  AQUAS-Lite (`CGPROGRAM`, built-in RP) do not render in HDRP; Rainy's README describes files that are not in the pack.
  AQUAS_Lite_Reflection.cs broke compilation of the whole project (`GetInstanceID` in 6000.6 — error CS0619) → replaced with
  `GetHashCode()`. Check: the shader of a prefab's material via `execute_code` (`r.sharedMaterial.shader.name`).
- **`execute_code` in the editor outside Play does not call `Awake`** on `AddComponent` (a component without `[ExecuteInEditMode]`):
  all fields are empty. To check synthesis/initialisation — `GetMethod("Awake", NonPublic|Instance).Invoke(c, null)`.
- **The `manage_camera` screenshot puts a png into `Assets/Screenshots`** (+ .meta) — delete it after checking, it is not a project asset.

## IMGUI: clicking "through" panels and names in partials (05.10.2026)
- **Picking a part in 3D happens in `Update`, which runs before `OnGUI`**: at the moment of the click IMGUI does not yet know the mouse is
  over a panel, and panels without buttons (`FlightHud.Fill`) do not consume the event. The solution — `HudHits`: rectangles of the panels
  are written in Repaint and checked in the next `Update`, plus `GUIUtility.hotControl != 0` while a button is held
  (the press was caught by a button/field). A click = movement < 6 px and < 0.6 s, otherwise it is camera rotation.
- **In a static class a field and a method with the same name give CS0102** (`Color Fuel` and `void Fuel(...)` in `StageTable`):
  an error in one file breaks the build of the whole Assembly-CSharp for all parallel agents — check new files with common
  names with `refresh_unity` immediately, not at the end.
- **A texture after `Apply(false, true)` is unreadable** — `GetPixels` in `execute_code` fails (`MissionSketch`).
  Check it like this: `Graphics.Blit` into a `RenderTexture` → `ReadPixels` → PNG in `Temp/Shots` (the frame comes out flipped in Y).
- **IMGUI hover cannot be triggered via MCP** — there is no mouse. Open the "?" tooltip of the mission table in Play via the field
  `MissionPicker.DebugTipId = "apollo11"` (+ `PauseMenu.IsOpen` via reflection, `MissionPicker.Open = true`); reset afterwards.
- **Entering Play from MCP breaks the bridge for ~20–30 s** (domain reload): `execute_code` right after `play` answers
  "No Unity Editor instances found" — wait and repeat, do not restart Play.
- **`execute_code` compiles with CodeDom (C# 6)**: `Object.FindFirstObjectByType<GameBootstrap>()` does not build ("`Object` ambiguous", game types not visible). What works: get the type via `AppDomain...GetAssemblies()` → `GetType("Kare.Space.Game.GameBootstrap")`, `UnityEngine.Object.FindFirstObjectByType(t)`, the field via reflection. This is how screenshots of different missions are taken: `MissionId` in memory → `EditorApplication.EnterPlaymode()`, do NOT save the scene (after Stop — `OpenScene` again, verified 05.10: `vostok` came back).
- **Full names of game types in `execute_code` (CodeDom) are visible** (05.10.2026): `Kare.Space.Game.GameBootstrap.U.Active`,
  `Kare.Space.Core.FlightPhysics.PlaceOnSurface(...)` compile without reflection; `QuaternionD.Inverse` is a property, not a method.
  The vertices of FBX parts (not Read/Write) are not returned by `mesh.vertices` — read `Mesh.AcquireReadOnlyMeshData(m)[0].GetVertices(NativeArray)`.
  Repeat a long measurement with `execute_code action=replay index=N` (the index is from `get_history`) instead of resending the code.
  A frame without motion blur after a teleport: release the pause, wait ~40 frames via `EditorApplication.update` and set `isPaused`.
- **`execute_code` right after `refresh_unity`/compilation may fail on the MCP timeout yet still finish** (05.10.2026):
  baking icons (~34 s) returned a timeout, but the PNGs were written (file time 10:22:29 with the clock at 10:22:51). Check the result
  by file times/`isCompiling`, do not restart blindly.

## Winged FBXs and Flight screenshots (05.10.2026, `Tools/blender/winged_parts.py`)
- A patch script with Cyrillic and quotes in a Bash heredoc fails with "unexpected EOF while looking for matching `''" — write the
  script with the Write tool into the scratchpad and run `python file.py`.
- The live Unity server is `mcp__unityMCP__*` (lowercase); `mcp__UnityMCP__*` answers `no_unity_session`.
- `manage_camera screenshot` with `view_position`/`view_rotation` in Flight during Play gives a frame "point-blank into a pillar"
  (2 out of 2 attempts on buran, although the vessel is at (0,0,0) and FOV 60), with `view_target` — a tilted horizon (world up is used).
  Reliable: shoot without a position — `camera:"Main Camera"`, the game camera's frame, it already holds the vessel on the pad.
- Change the mission for a screenshot via reflection on `GameBootstrap.MissionId` without saving the scene; after Stop — `OpenScene`
  again (`vostok` came back, `isDirty=False`).

## Cutouts and the fifth slot in winged_parts.py (05.10.2026)
- A non-convex fin outline (cutout for the rudder) is an ngon: the Unity FBX import fans it and closes the cutout. `cut_fin()` triangulates
  the faces in Blender (`bmesh.ops.triangulate`, ear-clipping preserves non-convexity). The wing `wing()` already triangulates top/bottom.
- The fifth slot (`Glass`) is for orbiters only: `main()` temporarily extends the global `SLOTS` from hulls_lib; the others have 4 slots,
  otherwise the CraftPalette palettes shift. Faces are sorted by slot — the glass is the last submesh.
- Running one model without saving parts.blend (parallel agents): a runner with `exec(src.split('if __name__ == "__main__"')[0])`
  + `main(only=[...])` + `sheet(...)`. Do not write `cat > file` without a heredoc in Bash — it hangs on stdin until the timeout.

## Deployables of station vessels and checking without Unity (05.10.2026)
- **A deployable part is a split copy of the object, not a separate build.** `FlightSceneBuilder.DeployParts` matches
  slots by the NAMES of the hull's materials; a separately built mesh gets its own materials — and the palette shifts.
  `Tools/blender/split_station.py`: rebuilds Crew_Dragon/Soyuz_PAO from `station_parts.py`, carries away islands (nose cone —
  min z > 3.07; panels — |x| > 1.5) into `Crew_Dragon_Nose.fbx` / `Soyuz_PAO_Panels.fbx`. The island must not touch the
  hull (the nose cone starts from 3.08 above the deck at 3.06).
- **Compilation in Play is deferred** (`ScriptCompilationDuringPlay` = 1): while another agent is in Play, `isCompiling` = true,
  and the new code is not loaded. Checking without Unity: copy `Kare.Space.Core/Game.csproj` into the scratchpad, replace the explicit `<Compile>`
  with the glob `Assets/_Project/<folder>/**/*.cs` (generated csproj files do not see new files — error
  "SpaceXRockets does not exist"), make paths absolute, `dotnet build` from Unity's DotNetSdk — 5 s, errors the same as Unity's.
- **Line endings in the working copy differ** (autocrlf): some files are LF, others CRLF, a single file may have both.
  The edit script determines `nl` per file; `sed -i` does not damage an LF file.
- **A long heredoc in Bash gets truncated** ("unexpected EOF while looking for matching"), and `\` inside a python heredoc
  collapses. Scripts longer than ~50 lines — Write into the scratchpad, then `python file`.
- **Changing the mission in Play via MCP:** `GameBootstrap.NextMissionId = "sts1"` before `isPlaying = true` is reset by the
  domain reload (vostok remains), and `SceneManager.LoadScene` from execute_code breaks the bridge for a minute. What works is
  `PauseMenu.SelectMission("sts1")` via reflection already in Play (verified 05.10: mission sts1, 7 rudders on "Columbia").
  In codedom execute_code write `UnityEngine.Object.Find…` (plain `Object` is ambiguous), without `$"…"`.
