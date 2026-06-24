# Divine Hands

A god-power / creator-tool mod for **Farthest Frontier** (MelonLoader, Mono build) — terrain sculpting, cursor spawners, and map god-tools behind one in-game panel. The creator-tool pillar of the FF mod constellation, alongside **Keep Clarity** (UX), **Sovereign Boons** (power-spike), and the wildlife/QoL mods.

> **Status: v0.1 (early scaffold).** Proof-of-life: the panel + Keep Clarity integration + **Reveal Map**. Terrain editing, spawners, and the rest are on the roadmap below.

## Design

- **One panel.** A single in-game panel is the shared control surface for every god-power, toggled by a **configurable hotkey** (default `Ctrl+G`).
- **Settings live in Keep Clarity.** All persistent options register with KC's settings panel via the reflective SettingsAPI (soft-dep). KC not installed? Everything still works off the MelonPreferences cfg.
- **Granularity of Pangu, look of TerrainHelper.** Terrain editing exposes Pangu's full parameter set through a clean, TerrainHelper-style UI.
- **Safety first.** World-altering powers default OFF. Cheats that bake into the save (e.g. infinite storage) get a map-load cleanup sweep so the mod can be cleanly removed.
- **No vendored binaries.** `LibTerrain2` (Terrain2Manager / Terrain2Tools) ships *inside* the game's Assembly-CSharp, so terrain editing needs no external DLL.

## Roadmap

| Phase | Contents |
|---|---|
| **v0.1** | Panel + KC integration + Reveal Map *(this build)* → terrain elevation (Raise/Lower/Smooth/Flatten, stroke-undo) → cursor spawners (animal/mineral/villager/forageable) → mineral deletion → scoped Build-Anywhere |
| **v0.2** | Item/livestock injection + infinite storage (with map-load cleanup sweep) · God View camera + Free Cam |
| **later** | Full Pangu-granularity Forest/Mountain/Lake biome brushes (native) · FFSeedScanner whole-map evaluation shown during Town Center placement |

## Provenance

Divine Hands re-implements and unifies features cherry-picked from community creator/cheat mods, resolving their overlaps into one clean implementation per capability. Source mods catalogued during design: AddItemModMono, AnimalSpawnerMono, FFSeedScanner, MineralSpawnerMono, MoveResourceMod, Pangu, TerrainHelperMono, VillagerSpawnerMono (WickerREST/WickerToolbox assessed and intentionally **not** adopted — incompatible .NET6/Il2Cpp web framework). Credit to the original authors; see the design notes.

## Build

```
dotnet build src\DivineHands.csproj -c Debug -p:Platform=x64      # fast iteration
dotnet build src\DivineHands.csproj -c Release -p:Platform=x64    # ship
```

Output `bin\<config>\DivineHands.dll` is auto-staged to the game's `Mods\` folder on every successful build. Requires the `GameDir` environment variable (or the default Steam path).

## License / status

Personal mod by **SageDragoon**. Not yet released.
