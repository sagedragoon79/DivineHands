# Changelog

All notable changes to Divine Hands are documented here.

## [0.1.0] — unreleased

Initial scaffold — the god-power core, ready to grow modules onto.

### Added
- **DivineCore** — MelonMod entry, scene lifecycle, and a single in-game panel (`DivinePanel`) toggled by a **configurable hotkey** (default `Ctrl+G`).
- **Keep Clarity integration** — all settings register reflectively with KC's settings panel (soft-dep); runs standalone if KC is absent.
- **God Tools: Reveal Map** — fog-of-war toggle via `FOWSystem.revealCompletely`, with snapshot/restore of the explored buffer so toggling off reverts the fog.
- **Terrain Sculpting** — Raise / Lower / Smooth / Flatten brush over an N×N heightmap-cell grid centered on the cursor, with strength slider, configurable apply key (default middle mouse), and **stroke-level undo** (depth 64, default `Ctrl+Z`). Mirrors `Terrain2Tools` brush logic over the public `Heightmap` API and commits each stroke through the engine's own `Terrain2.SmoothHeightsNotify` — which re-meshes, rebuilds colliders/NavMesh, re-paths villager AI, and re-anchors trees. Edits persist natively in the save (heightmap is serialized). Arm the brush in the panel before it applies.
- **Hotkey** — chord parser supporting KeyCode names and Ctrl/Alt/Shift chords (e.g. `Ctrl+G`, `F6`, `Alt+Shift+D`).
- Build pipeline auto-stages the DLL to the game `Mods\` folder.

### Fixed
- Panel no longer triggers the game's drag-select marquee while being dragged — a Harmony postfix on `GameManager.pointerIsOverUI` reports the cursor as over-UI while it's over the IMGUI window.
- Corrected the Reveal Map contract: revealing **does** bake explored state into the save (`FOWSystem.Save` serializes the fog buffer), so saving while revealed persists it. Snapshot/restore reverts the toggle; turn it off before saving for clean fog.

### Notes
- `LibTerrain2` confirmed to live inside the game's Assembly-CSharp — terrain editing needs no vendored dependency.
- WickerREST / WickerToolbox assessed during design and **not** adopted (incompatible .NET6/Il2Cpp web framework; redundant with the KC settings panel).
