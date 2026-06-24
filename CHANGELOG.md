# Changelog

All notable changes to Divine Hands are documented here.

## [0.1.0] — unreleased

Initial scaffold — the god-power core, ready to grow modules onto.

### Added
- **DivineCore** — MelonMod entry, scene lifecycle, and a single in-game panel (`DivinePanel`) toggled by a **configurable hotkey** (default `Ctrl+G`).
- **Keep Clarity integration** — all settings register reflectively with KC's settings panel (soft-dep); runs standalone if KC is absent.
- **God Tools: Reveal Map** — fog-of-war toggle via `FOWSystem.revealCompletely`. Snapshots all three fog buffers (the explored channel lives in `mBuffer0.g`, and the visible/scratch buffers swap each blur tick) and restores them on toggle-off so the fog reverts. **Also reverts the minimap icons** the reveal uncovered: after restoring the buffers it calls `Minimap.ResetPOIs()`, so the engine re-reveals only sites whose location is still genuinely explored — mineral/pit/spawn/excavation/salvage icons all revert uniformly, without touching the minimap category filters.
- **Terrain Sculpting** — Raise / Lower / Smooth / Flatten brush over an N×N heightmap-cell grid centered on the cursor, with strength slider, configurable apply key (default middle mouse), and **stroke-level undo** (depth 64, default `Ctrl+Z`). Mirrors `Terrain2Tools` brush logic over the public `Heightmap` API and commits each stroke through the engine's own `Terrain2.SmoothHeightsNotify` — which re-meshes, rebuilds colliders/NavMesh, re-paths villager AI, and re-anchors trees. Edits persist natively in the save (heightmap is serialized). Arm the brush in the panel before it applies.
- **Terrain brush cursor grid** — a TerrainHelper-style N×N grid preview (pure code, no AssetBundle) that follows the cursor, conforms to the terrain surface, is colored by mode (green/red/yellow/cyan), and resizes with the grid setting. Outlines the exact cells a stroke will edit. Shown only while the brush is armed.
- **Cursor Spawners** — spawn at the cursor, armed in the panel: **Animals** (deer/bear/boar/wolf), **Minerals** (gold/iron/coal as depleting deposits with an optional Deep/infinite toggle; stone/clay/sand always as infinite pits — no finite-stone bug), **Villagers** (immigration loop, never CheatManager), and **Resources** (forageable/tree/rock/giant-rock from user-editable GUID prefs, DLC-null-guarded). Count 1–50 scatters in a ring so spawns don't stack. The panel uses one shared armed-tool state, so arming the spawner disarms the terrain brush and vice-versa.
- **God Tools: Build Anywhere** — toggle to place normal buildings on ground vanilla rejects (steep slope, no path to town, water/road overlap). Three Harmony prefixes on `Placeable.IsPlacementValid`, `PlacementValidityHelper.CanPathToPoint`, and `WagonShop.CanPathToWagon`, each gated on the toggle and defensively non-throwing. **Scoped around Keep Clarity:** bridges defer entirely to vanilla + KC's Bridge Anywhere (the active-placeable `is PlaceableBridge` check), so the two compose — KC patches four different bridge methods, zero Harmony collision.
- **Hotkey** — chord parser supporting KeyCode names and Ctrl/Alt/Shift chords (e.g. `Ctrl+G`, `F6`, `Alt+Shift+D`).
- Build pipeline auto-stages the DLL to the game `Mods\` folder.

### Fixed
- Panel no longer triggers the game's drag-select marquee while being dragged — a Harmony postfix on `GameManager.pointerIsOverUI` reports the cursor as over-UI while it's over the IMGUI window.
- Corrected the Reveal Map contract: revealing **does** bake explored state into the save (`FOWSystem.Save` serializes the fog buffer), so saving while revealed persists it. Snapshot/restore reverts the toggle; turn it off before saving for clean fog.

### Notes
- `LibTerrain2` confirmed to live inside the game's Assembly-CSharp — terrain editing needs no vendored dependency.
- WickerREST / WickerToolbox assessed during design and **not** adopted (incompatible .NET6/Il2Cpp web framework; redundant with the KC settings panel).
