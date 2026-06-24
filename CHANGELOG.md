# Changelog

All notable changes to Divine Hands are documented here.

## [0.1.0] — unreleased

Initial scaffold — the god-power core, ready to grow modules onto.

### Added
- **DivineCore** — MelonMod entry, scene lifecycle, and a single in-game panel (`DivinePanel`) toggled by a **configurable hotkey** (default `Ctrl+G`).
- **Keep Clarity integration** — all settings register reflectively with KC's settings panel (soft-dep); runs standalone if KC is absent.
- **God Tools: Reveal Map** — live fog-of-war toggle via `FOWSystem.revealCompletely` (no Harmony patch, no save contamination). First proof-of-life feature.
- **Hotkey** — chord parser supporting KeyCode names and Ctrl/Alt/Shift chords (e.g. `Ctrl+G`, `F6`, `Alt+Shift+D`).
- Build pipeline auto-stages the DLL to the game `Mods\` folder.

### Notes
- `LibTerrain2` confirmed to live inside the game's Assembly-CSharp — terrain editing will need no vendored dependency.
- WickerREST / WickerToolbox assessed during design and **not** adopted (incompatible .NET6/Il2Cpp web framework; redundant with the KC settings panel).
