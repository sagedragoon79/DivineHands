using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using MelonLoader;

namespace DivineHands.Modules
{
    /// <summary>
    /// Cursor-raycast spawn dispatcher. Arms behind the panel's Spawner toggle and fires on a
    /// configurable apply key at the terrain point under the cursor (reusing
    /// <see cref="TerrainElevation.TryGetCursorWorld"/> — the SAME FF terrain raycast the sculpt
    /// brush uses, so spawns land exactly under the cursor with correct world height).
    ///
    /// Four families (Config.SpawnFamily): Animal, Mineral, Villager, Resource. Sub-type is
    /// Config.SpawnSubtype within the family. Count is Config.SpawnCount.
    ///
    /// All FF API paths VERIFIED against Assembly-CSharp decompile (no invented names):
    ///   ANIMAL — GameManager.Instance.animalManager (public, 95608); four PUBLIC debug spawners,
    ///     each (int numToSpawn, Vector3 spawnPos):
    ///       - DebugSpawnDeerAtPoint   [93029]
    ///       - DebugSpawnBoarsAtPoint  [93037]
    ///       - DebugSpawnWolvesAtPoint [93045]
    ///       - DebugSpawnBearsAtPoint  [93065]
    ///     They loop SpawnAnimal -> AddSpawnedAnimal (runtime list; NOT serialized — animals are a
    ///     live-only spawn, they will not survive a save/load. Flagged for in-game test.)
    ///
    ///   MINERAL — GameManager.Instance.mineralManager (public, 95628).
    ///     gold/iron/coal => finite MineralSite, optional Deep/infinite:
    ///       - mineralSitePrefabData.Add(new MineralSitePrefabData(type,pos,radius)) [public list/struct]
    ///       - CreateMineralSitePrefab(MineralSite.MineralType, Vector3, float) [PRIVATE 159456] — reflection;
    ///           increments mineralDepositId internally (159497) THEN stamps the visible deposit id.
    ///       - mineralSites.Add(new MineralSite(idAfterIncrement, type, pos, radius, count)) [public ctor 160191]
    ///       - Deep: site.isInfinite=true (settable auto-prop 160128, serialized 160160) + count=99999.
    ///       CRITICAL ordering (verifier): read mineralDepositId AFTER CreateMineralSitePrefab so the
    ///       logical MineralSite id matches the visible deposit prefab id (engine subsite path does the
    ///       same — constructs MineralSite with the post-increment id, 159445/159450).
    ///       Persists: mineralSites + mineralSitePrefabData are serialized [158699/158709] and the prefab
    ///       is re-instantiated in OnGameFinishedLoading [158782-785].
    ///
    ///     stone/clay/sand => INFINITE pit (the design's no-finite-stone rule). We call the PRIVATE
    ///     full-job creators via reflection (they add the Site wrapper to the serialized list):
    ///       - CreateClaySite (Vector2 loc, float radius, int clayCount, bool avoidWater)  [PRIVATE 159588]
    ///       - CreateSandSite (Vector2 loc, float radius, int sandCount, bool avoidWater)  [PRIVATE 159699]
    ///       - CreateStoneSite(Vector2 loc, float radius, int stoneCount, bool avoidWater) [PRIVATE 159774]
    ///     invoked with avoidWater:false, then we grab the just-added Site (last list element) and set
    ///     its live component's infiniteItems=true. Site.isInfinite is a READ-ONLY getter off that
    ///     component (159884/159952/160020); Save() reads it back (159904/...) so infinite persists.
    ///     NOTE clay/sand use Resource.storage.infiniteItems; stone's load path sets
    ///     StonePitResource.infiniteItems DIRECTLY (160062) — we set BOTH on stone.
    ///
    ///   VILLAGER — GameManager.Instance.villagerPopulationManager.SpawnVillagerImmigration(pos, announced)
    ///     [public 394680] looped Count times. Persists natively (Villager.Start -> AddOrRemoveVillager).
    ///     NEVER CheatManager (per design rule).
    ///
    ///   RESOURCE — GlobalAssets.prefabAssetMap.GetPrefab(guid) [public 48987, returns null on miss,
    ///     no throw] -> Object.Instantiate for forageables/rocks/giant-rock; trees go through
    ///     terrainManager.AddGrowingTree(prefab, x, z, Vector2.zero, startGrown, false, 1f)
    ///     [public override 217752]. GUIDs come from user-editable delimited prefs; every GetPrefab is
    ///     try/catch + null-guarded so a missing/DLC prefab is skipped, never a crash.
    /// </summary>
    public static class CursorSpawners
    {
        // Family / sub-type are stored as ints in Config; these enums name them.
        public enum Family { Animal, Mineral, Villager, Resource }

        // Order MUST match the panel pickers and the Config descriptions.
        // Deer..Wolf are base-game; Fox/Groundhog (wildlife) + Dog/Cat (pets) ship in the Cats & Dogs
        // DLC — their prefab/asset only resolves when the DLC is owned, so they self-gate (see
        // DlcAnimalAvailable). Append-only: existing indices must not shift (Config stores the int).
        public enum AnimalKind { Deer, Bear, Boar, Wolf, Fox, Groundhog, Dog, Cat }
        public enum MineralKind { Gold, Iron, Coal, Stone, Clay, Sand }
        public enum ResourceKind { Forageable, Tree, Rock, GiantRock }

        // ---- cached reflection for persistent animal spawning (resolved lazily, once per map) ----
        private static FieldInfo? _validAnimalGroupsField;        // protected List<AnimalGroupDefinition> AnimalManager.validAnimalGroups
        private static PropertyInfo? _qualifiedDenAreasProp;      // public List<AnimalDenSpawnArea> AnimalManager.qualifiedAnimalDenSpawnAreas
        private static PropertyInfo? _spawnAreaGroupTypeProp;     // AnimalGroupDefinition.animalType (getter)
        private static PropertyInfo? _spawnAreaGroupPointTypeProp;// AnimalGroupDefinition.spawnPointType (getter)
        private static Type? _animalDenType;                      // AnimalDen (FindType)
        private static bool _animalReflectionResolved;

        // AnimalGroupDefinition.AnimalType enum values (verified [35809]: None,Deer,Boar,Wolf,Bear,
        // Groundhog,Fox,_Count). We reference the enum members directly elsewhere (compiler-checked);
        // these ints are only for the den/spawn-area group lookups that already used them.
        private const int GroupAnimalType_Deer = 1;
        private const int GroupAnimalType_Boar = 2;
        private const int GroupAnimalType_Wolf = 3;

        // DLC animal availability, probed once per map (prefab/asset actually resolves). Robust to the
        // base-game-vs-DLC question: a kind is offered iff it can really spawn. Reset in OnMapLoaded.
        private static bool _dlcAvailResolved;
        private static bool _foxAvail, _groundhogAvail, _dogAvail, _catAvail;
        // AnimalGroupDefinition.SpawnPointType enum values (verified [35822]).
        private const int GroupSpawnPointType_SpawnArea = 1;
        private const int GroupSpawnPointType_AnimalDen = 2;

        // ---- cached reflection (resolved lazily, once per map) ----
        private static MethodInfo? _createMineralSitePrefab;  // private MineralManager
        private static MethodInfo? _createClaySite;           // private MineralManager (Vector2 overload)
        private static MethodInfo? _createSandSite;           // private MineralManager (Vector2 overload)
        private static MethodInfo? _createStoneSite;          // private MineralManager (Vector2 overload)
        private static bool _mineralReflectionResolved;

        // ---- defaults for spawned sites (kept modest; not user-tunable in v1) ----
        private const float MineralRadius = 12f;      // metres
        private const int MineralFiniteCount = 5000;  // ore units for a non-Deep gold/iron/coal site
        private const int DeepMineCount = 99999;      // matches the engine's deep-deposit count [158664]
        private const float PitRadius = 8f;           // metres
        private const int PitCount = 9999;            // starting items (infinite flag makes it endless anyway)
        private const float TreeStartGrown = 0.85f;   // AddGrowingTree startPercentGrown (near-mature)

        // =====================================================================
        // Lifecycle (called from Plugin)
        // =====================================================================

        public static void OnMapLoaded()
        {
            _createMineralSitePrefab = null;
            _createClaySite = null;
            _createSandSite = null;
            _createStoneSite = null;
            _mineralReflectionResolved = false;
            _mapTreePrefabs = null;

            _validAnimalGroupsField = null;
            _qualifiedDenAreasProp = null;
            _spawnAreaGroupTypeProp = null;
            _spawnAreaGroupPointTypeProp = null;
            _animalDenType = null;
            _animalReflectionResolved = false;
            _isOccupiedThirdParamType = null;

            _dlcAvailResolved = false;
            _foxAvail = _groundhogAvail = _dogAvail = _catAvail = false;
        }

        public static void OnSceneExit() => OnMapLoaded();

        public static void OnUpdate()
        {
            if (!Config.SpawnEnable.Value) return;

            // Only fire while the spawner tool is the armed panel mode and the cursor isn't on the panel.
            bool keyDown = Hotkey.Pressed(Config.SpawnApplyKey.Value);

            // Diagnostic: log every apply-key press with the gate state, so a "nothing spawns" report can
            // be pinpointed to key vs arm vs cursor-over-panel vs downstream spawn. (DebugLog only.)
            if (Config.DebugLog.Value && keyDown)
                MelonLogger.Msg($"[DivineHands] Spawn key '{Config.SpawnApplyKey.Value}' down — " +
                                $"armed={DivineHands.Core.DivinePanel.SpawnerModeActive} " +
                                $"cursorOverPanel={DivineHands.Core.DivinePanel.BlocksGameInput}");

            if (DivineHands.Core.DivinePanel.SpawnerModeActive
                && !DivineHands.Core.DivinePanel.BlocksGameInput
                && keyDown)
            {
                ApplyAtCursor();
            }
        }

        // =====================================================================
        // Apply
        // =====================================================================

        private static void ApplyAtCursor()
        {
            if (!TerrainElevation.TryGetCursorWorld(out Vector3 world))
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Msg("[DivineHands] Spawn: no terrain under cursor");
                return;
            }

            int count = Mathf.Clamp(Config.SpawnCount.Value, 1, 50);
            var family = (Family)Mathf.Clamp(Config.SpawnFamily.Value, 0, 3);

            // Minerals are always a single deposit/pit; Boulders (Resource→GiantRock) are always single.
            // (Other resources/animals/villagers respect the count.)
            if (family == Family.Mineral)
                count = 1;
            else if (family == Family.Resource
                     && (ResourceKind)Mathf.Clamp(Config.SpawnSubtype.Value, 0, 3) == ResourceKind.GiantRock)
                count = 1;

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Spawn apply — family={family} " +
                                $"subtype={Config.SpawnSubtype.Value} count={count} @ {world}");

            try
            {
                switch (family)
                {
                    case Family.Animal: SpawnAnimals(world, count); break;
                    case Family.Mineral: SpawnMineral(world, count); break;
                    case Family.Villager: SpawnVillagers(world, count); break;
                    case Family.Resource: SpawnResources(world, count); break;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] Spawn ({family}) failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------------
        // ANIMAL  (public manager methods — no reflection)
        // ---------------------------------------------------------------------

        private static void SpawnAnimals(Vector3 world, int count)
        {
            var gm = GameManager.Instance;
            var am = gm != null ? gm.animalManager : null;
            if (am == null) return;

            var kind = (AnimalKind)Mathf.Clamp(Config.SpawnSubtype.Value, 0, 7);

            // DLC animals (Fox/Groundhog/Dog/Cat) only spawn when their prefab/asset resolves (DLC owned,
            // or base-game in a future build). The picker greys them otherwise; this guards a stale Config
            // value. Graceful no-op — never a crash.
            if (!DlcAnimalAvailable(kind))
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] {kind} unavailable (needs the Cats & Dogs DLC) — skipped.");
                return;
            }

            // Bear and the DLC kinds are ALWAYS loose (ignore the persistent toggle) — no node/den path.
            // Deer/Wolf/Boar honour the toggle: ON = a persistent node at the cursor (Deer = a registered
            // spawn-area that self-respawns + shows a circular marker; Wolf/Boar = a den), OFF = loose.
            if (kind == AnimalKind.Bear || IsDlcAnimal(kind) || !Config.SpawnPersistent.Value)
            {
                SpawnAnimalsLoose(am, kind, world, count);
                return;
            }

            ResolveAnimalReflection(am);

            bool ok = kind switch
            {
                AnimalKind.Deer => SpawnDeerArea(am, world, count),
                AnimalKind.Wolf => SpawnDen(am, GroupAnimalType_Wolf, count, world),
                AnimalKind.Boar => SpawnDen(am, GroupAnimalType_Boar, count, world),
                _ => false
            };

            // Graceful fallback to loose if the persistent path couldn't build a node/den.
            if (!ok)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] Persistent {kind} spawn unavailable — falling back to loose.");
                SpawnAnimalsLoose(am, kind, world, count);
            }
        }

        // Legacy one-off loose spawn (DebugSpawn*AtPoint). Animals are runtime-only and do not
        // survive save/load.
        private static void SpawnAnimalsLoose(object am, AnimalKind kind, Vector3 world, int count)
        {
            var animalMgr = (AnimalManager)am;
            switch (kind)
            {
                case AnimalKind.Deer: animalMgr.DebugSpawnDeerAtPoint(count, world); break;
                case AnimalKind.Bear: animalMgr.DebugSpawnBearsAtPoint(count, world); break;
                case AnimalKind.Boar: animalMgr.DebugSpawnBoarsAtPoint(count, world); break;
                case AnimalKind.Wolf: animalMgr.DebugSpawnWolvesAtPoint(count, world); break;
                // DLC wildlife — no DebugSpawn*AtPoint exists, so go through the generic SpawnAnimal
                // pipeline (self-no-ops if the DLC prefab can't resolve).
                case AnimalKind.Fox:       SpawnWildlifeGroup(animalMgr, AnimalGroupDefinition.AnimalType.Fox, world, count); break;
                case AnimalKind.Groundhog: SpawnWildlifeGroup(animalMgr, AnimalGroupDefinition.AnimalType.Groundhog, world, count); break;
                // DLC pets — instantiated from the DLC-populated prefab lists; self-register in Start().
                case AnimalKind.Dog:       SpawnPets(animalMgr, dog: true, world, count); break;
                case AnimalKind.Cat:       SpawnPets(animalMgr, dog: false, world, count); break;
            }
            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Spawned {count}x {kind} @ {world} (loose — " +
                                "runtime-only, does not persist through save/load)");
        }

        // DLC wildlife (Fox/Groundhog): no DebugSpawn*AtPoint exists, so loop the generic
        // AnimalManager.SpawnAnimal(List<AnimalGroupDefinition>, Vector3) [92118], scattering so they
        // don't stack. SpawnAnimal returns false and no-ops if the group prefab can't resolve (no DLC).
        private static void SpawnWildlifeGroup(AnimalManager am, AnimalGroupDefinition.AnimalType type,
                                               Vector3 world, int count)
        {
            var groups = am.GetAllAnimalGroupDefinitions(type);
            if (groups == null || groups.Count == 0) return;
            for (int i = 0; i < count; i++)
                am.SpawnAnimal(groups, ScatterAround(world, i, count, spacing: 6f));
        }

        // DLC pets (Dog/Cat): no SpawnPet/PetManager exists. The base game's starting-pet path just
        // Instantiates from animalManager.dogPrefabs/catPrefabs [90532/90534] (populated only when the
        // DLC is owned) and lets the pet self-register in its own Start() via ResourceManager. We mirror
        // that. Lists are empty without the DLC, so this is a clean no-op then.
        private static void SpawnPets(AnimalManager am, bool dog, Vector3 world, int count)
        {
            var prefabs = dog ? am.dogPrefabs : am.catPrefabs;
            if (prefabs == null || prefabs.Count == 0) return;
            for (int i = 0; i < count; i++)
            {
                var prefab = prefabs[UnityEngine.Random.Range(0, prefabs.Count)];
                if (prefab == null) continue;
                UnityEngine.Object.Instantiate(prefab, ScatterAround(world, i, count, spacing: 4f),
                                               Quaternion.identity);
            }
        }

        // ---- DLC animal availability (probed once per map; robust to base-game-vs-DLC) ----

        private static bool IsDlcAnimal(AnimalKind kind) =>
            kind == AnimalKind.Fox || kind == AnimalKind.Groundhog
            || kind == AnimalKind.Dog || kind == AnimalKind.Cat;

        /// <summary>True if this animal kind can actually spawn right now — base kinds always; the DLC
        /// kinds only when their prefab/asset resolves (DLC owned, or base-game in a future build).
        /// Read by the panel to grey out unavailable kinds and by the spawn path as a final guard.</summary>
        public static bool DlcAnimalAvailable(AnimalKind kind)
        {
            if (!IsDlcAnimal(kind)) return true;
            EnsureDlcAnimalAvailability();
            return kind switch
            {
                AnimalKind.Fox => _foxAvail,
                AnimalKind.Groundhog => _groundhogAvail,
                AnimalKind.Dog => _dogAvail,
                AnimalKind.Cat => _catAvail,
                _ => true
            };
        }

        private static void EnsureDlcAnimalAvailability()
        {
            if (_dlcAvailResolved) return;
            try
            {
                var gm = GameManager.Instance;
                var am = gm != null ? gm.animalManager : null;
                if (am == null) return; // not ready yet — leave unresolved so we retry next query

                _dlcAvailResolved = true;
                _foxAvail       = GroupPrefabResolves(am, AnimalGroupDefinition.AnimalType.Fox);
                _groundhogAvail = GroupPrefabResolves(am, AnimalGroupDefinition.AnimalType.Groundhog);
                _dogAvail       = am.hasDogAssets;   // dogPrefabs.Count > 0 (DLC-populated)
                _catAvail       = am.hasCatAssets;   // catPrefabs.Count > 0
                if (Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] DLC animals available — Fox:{_foxAvail} " +
                                    $"Groundhog:{_groundhogAvail} Dog:{_dogAvail} Cat:{_catAvail}");
            }
            catch (Exception ex)
            {
                _dlcAvailResolved = true; // don't hammer a failing probe every frame
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] DLC availability probe failed: {ex.Message}");
            }
        }

        // A group type is spawnable if ANY of its AnimalGroupDefinitions yields a non-null weighted
        // prefab. For Fox/Groundhog the prefab is null until the DLC asset resolves [36088-36094], so
        // this is exactly "is the DLC content present" without any Steam-API dependency.
        private static bool GroupPrefabResolves(AnimalManager am, AnimalGroupDefinition.AnimalType type)
        {
            var groups = am.GetAllAnimalGroupDefinitions(type);
            if (groups == null) return false;
            foreach (var g in groups)
                if (g != null && g.GetWeightedAnimalPrefab() != null) return true;
            return false;
        }

        // ---- PERSISTENT: Wolf/Boar DEN — placed AT THE CURSOR ----
        // The base game's AnimalManager.SpawnAnimalDens [92635-92700] scatters dens at RANDOM points
        // inside random qualifiedAnimalDenSpawnAreas. For a cursor god-power that's wrong — the den must
        // appear under the cursor. We therefore replicate the per-point grid validation that lives in
        // AnimalDenSpawnArea.GetValidRandomSpawnPoint [35756] but seed it with the CURSOR point instead
        // of a random one: snap the cursor through aiPathfinder.IsPointOccupiedOrUnpathable [478258],
        // build the gridSize-by-gridSize cell array stepping by gridGraph.nodeSize [476778], and place
        // the den at the grid-aligned centre. Only if the cursor cells genuinely can't be built (water /
        // unpathable / occupied) do we fall back to the nearest qualified area's GetValidRandomSpawnPoint.
        // Instantiation/grid/spawn wiring is shared with the base behaviour via SpawnDenAt.
        // count = number of dens to place (clamped). Returns true if at least one den was created.
        private static bool SpawnDen(object am, int groupAnimalType, int denCount, Vector3 cursorWorld)
        {
            try
            {
                var group = FindGroup(groupAnimalType, GroupSpawnPointType_AnimalDen);
                if (group == null)
                {
                    if (Config.DebugLog.Value)
                        MelonLogger.Warning($"[DivineHands] No den AnimalGroupDefinition for animalType={groupAnimalType}");
                    return false;
                }

                // Resolve the den prefab + its AnimalDen component (we read gridSize off it).
                GameObject? denPrefab = InvokeGetWeightedDenPrefab(group);
                if (denPrefab == null && groupAnimalType == GroupAnimalType_Wolf)
                    denPrefab = SafeGetPrefab(Config.SpawnWolfDenGuid.Value); // optional GUID fallback (wolf only)
                if (denPrefab == null)
                {
                    if (Config.DebugLog.Value)
                        MelonLogger.Warning("[DivineHands] GetWeightedDenPrefab returned null and no fallback prefab");
                    return false;
                }

                if (_animalDenType == null) { if (Config.DebugLog.Value) MelonLogger.Warning("[DivineHands] AnimalDen type unresolved"); return false; }
                var denTemplate = denPrefab.GetComponent(_animalDenType);
                if (denTemplate == null)
                {
                    if (Config.DebugLog.Value)
                        MelonLogger.Warning("[DivineHands] Den prefab has no AnimalDen component");
                    return false;
                }

                // gridSize lives on the AnimalDen component (Vector2) [35292].
                var gridSize = (Vector2)(GetMember(denTemplate, _animalDenType, "gridSize") ?? new Vector2(3f, 3f));

                int made = 0;
                // First den goes exactly at the cursor; extra dens (count>1) ring out from it so they
                // don't stack, each snapped/validated independently.
                for (int i = 0; i < denCount; i++)
                {
                    Vector3 target = ScatterAround(cursorWorld, i, denCount, spacing: 6f);

                    // Try the cursor (ringed) point first.
                    if (TryComputeDenCellsAt(target, gridSize, out Vector3 placePoint, out Vector2[] cells))
                    {
                        SpawnDenAt(denTemplate, group, placePoint, cells);
                        made++;
                        continue;
                    }

                    // Cursor point can't host the den (water/unpathable/occupied) — fall back to the
                    // qualified den area NEAREST the cursor, mirroring the base game's valid-area path.
                    if (TrySpawnDenInNearestArea(am, denTemplate, group, gridSize, target))
                        made++;
                    else if (Config.DebugLog.Value)
                        MelonLogger.Warning("[DivineHands] Den: cursor point invalid and no qualified-area fallback found");
                }

                if (Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] Created {made}/{denCount} persistent den(s) at cursor " +
                                    $"(animalType={groupAnimalType}).");
                return made > 0;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] SpawnDen failed: {ex.Message}");
                return false;
            }
        }

        // Replicates the inner grid-validation of AnimalDenSpawnArea.GetValidRandomSpawnPoint [35756]
        // but anchored at an explicit world point (the cursor) rather than a random point in a Rect:
        //   1) snap the point to the AI grid via IsPointOccupiedOrUnpathable (false => buildable);
        //   2) walk gridSize.x * gridSize.y cells stepping by gridGraph.nodeSize, validating each;
        //   3) on full success, place point = grid-aligned centre at terrain height.
        // Returns false if the point or any cell is occupied/unpathable (caller then falls back).
        private static bool TryComputeDenCellsAt(Vector3 world, Vector2 gridSize,
                                                 out Vector3 placePoint, out Vector2[] gridCellLocations)
        {
            placePoint = world;
            int nx = Mathf.Max(1, (int)gridSize.x);
            int nz = Mathf.Max(1, (int)gridSize.y);
            gridCellLocations = new Vector2[nx * nz];

            try
            {
                var gm = GameManager.Instance;
                var pathfinder = gm != null ? gm.aiPathfinder : null;   // public field [95478]
                var terrain = gm != null ? gm.terrainManager : null;     // public [95612]
                if (pathfinder == null) return false;

                float nodeSize = GetNodeSize(pathfinder);
                if (nodeSize <= 0f) return false;

                // 1) snap the seed point. IsPointOccupiedOrUnpathable(Vector2, out Vector3, out IGridOccupant) [478258].
                var point = new Vector2(world.x, world.z);
                if (!TryIsOccupied(pathfinder, point, out Vector3 gridAdjusted))
                    return false; // occupied/unpathable at the seed -> no den here
                point = new Vector2(gridAdjusted.x, gridAdjusted.z);

                // 2) validate every cell of the gridSize footprint.
                for (int z = 0; z < nz; z++)
                {
                    for (int x = 0; x < nx; x++)
                    {
                        var cell = new Vector2(point.x + x * nodeSize, point.y + z * nodeSize);
                        if (!TryIsOccupied(pathfinder, cell, out Vector3 cellAdjusted))
                            return false; // a cell is occupied/unpathable -> abandon this point
                        gridCellLocations[z * nx + x] = new Vector2(cellAdjusted.x, cellAdjusted.z);
                    }
                }

                // 3) grid-aligned centre, terrain height — exactly as the engine computes it [35794-35798].
                float half = nodeSize / 2f;
                float cx = gridCellLocations[0].x - half + nodeSize * nx / 2f;
                float cz = gridCellLocations[0].y - half + nodeSize * nz / 2f;
                float h = terrain != null ? terrain.GetHeight(cx, cz) : world.y;
                placePoint = new Vector3(cx, h, cz);
                return true;
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] TryComputeDenCellsAt failed: {ex.Message}");
                return false;
            }
        }

        // IsPointOccupiedOrUnpathable returns TRUE when the point is occupied/unpathable. We invert it:
        // returns true (buildable) with the grid-adjusted point out, false otherwise.
        private static bool TryIsOccupied(object pathfinder, Vector2 point, out Vector3 gridAdjustedPoint)
        {
            gridAdjustedPoint = default;
            try
            {
                var mi = pathfinder.GetType().GetMethod("IsPointOccupiedOrUnpathable",
                    new[] { typeof(Vector2), typeof(Vector3).MakeByRefType(),
                            GetIGridOccupantRefType(pathfinder) });
                if (mi == null) return false;
                object[] args = { point, null!, null! };
                var occupied = mi.Invoke(pathfinder, args);
                gridAdjustedPoint = args[1] is Vector3 v ? v : default;
                return occupied is bool b && !b; // buildable == !occupied
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] IsPointOccupiedOrUnpathable invoke failed: {ex.Message}");
                return false;
            }
        }

        // The third (out IGridOccupant) parameter type — resolved off the actual method so we match the
        // exact overload without referencing the interface type at compile time.
        private static Type? _isOccupiedThirdParamType;
        private static Type GetIGridOccupantRefType(object pathfinder)
        {
            if (_isOccupiedThirdParamType != null) return _isOccupiedThirdParamType;
            foreach (var m in pathfinder.GetType().GetMethods())
            {
                if (m.Name != "IsPointOccupiedOrUnpathable") continue;
                var ps = m.GetParameters();
                if (ps.Length == 3 && ps[0].ParameterType == typeof(Vector2)
                    && ps[1].ParameterType == typeof(Vector3).MakeByRefType())
                {
                    _isOccupiedThirdParamType = ps[2].ParameterType;
                    return _isOccupiedThirdParamType;
                }
            }
            // Fallback: a by-ref object so GetMethod still resolves something sane.
            _isOccupiedThirdParamType = typeof(object).MakeByRefType();
            return _isOccupiedThirdParamType;
        }

        private static float GetNodeSize(object pathfinder)
        {
            try
            {
                var gg = pathfinder.GetType().GetProperty("gridGraph",
                    BindingFlags.Public | BindingFlags.Instance)?.GetValue(pathfinder);
                if (gg == null) return 0f;
                var ns = gg.GetType().GetField("nodeSize", BindingFlags.Public | BindingFlags.Instance)?.GetValue(gg);
                return ns is float f ? f : 0f;
            }
            catch { return 0f; }
        }

        // Fall back to the qualified den area nearest the cursor (only when the cursor point itself is
        // unbuildable). Uses the engine's own GetValidRandomSpawnPoint within that single nearest area.
        private static bool TrySpawnDenInNearestArea(object am, object denTemplate, object group,
                                                     Vector2 gridSize, Vector3 cursorWorld)
        {
            var denAreas = GetQualifiedDenAreas(am);
            if (denAreas == null || denAreas.Count == 0)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning("[DivineHands] No qualifiedAnimalDenSpawnAreas for den fallback");
                return false;
            }

            // Order areas by distance from cursor (nearest first) using each area's rect centre.
            var ordered = new List<object>();
            foreach (var a in denAreas) if (a != null) ordered.Add(a);
            var cursor2D = new Vector2(cursorWorld.x, cursorWorld.z);
            ordered.Sort((a, b) =>
                Vector2.Distance(GetAreaRectCenter(a), cursor2D)
                    .CompareTo(Vector2.Distance(GetAreaRectCenter(b), cursor2D)));

            foreach (var area in ordered)
            {
                object[] args = { gridSize, null! };
                var mi = area.GetType().GetMethod("GetValidRandomSpawnPoint",
                    new[] { typeof(Vector2), typeof(Vector2[]).MakeByRefType() });
                if (mi == null) return false;
                var spawnPointObj = mi.Invoke(area, args);
                if (spawnPointObj == null) continue;                  // no valid point in this area
                var spawnPoint = (Vector3)spawnPointObj;
                var cells = (Vector2[])args[1];
                SpawnDenAt(denTemplate, group, spawnPoint, cells);
                if (Config.DebugLog.Value)
                    MelonLogger.Msg("[DivineHands] Den placed in nearest qualified area (cursor point was unbuildable).");
                return true;
            }
            return false;
        }

        private static Vector2 GetAreaRectCenter(object area)
        {
            try
            {
                var rectObj = GetMember(area, area.GetType(), "rect");
                if (rectObj is Rect r) return r.center;
            }
            catch { /* ignore — fall through */ }
            return Vector2.zero;
        }

        // Shared den instantiation/wiring — identical to the base SpawnAnimalDens tail [92692-92699]:
        // Instantiate(animalDen, pt, identity); inst.animalGroup = group; inst.SetGridCellLocations(cells);
        // inst.SpawnAnimalsAtDen(maxPerSpawnArea).
        private static void SpawnDenAt(object denTemplate, object group, Vector3 spawnPoint, Vector2[] cells)
        {
            var inst = UnityEngine.Object.Instantiate((Component)denTemplate, spawnPoint, Quaternion.identity);
            if (inst == null) return;
            SetMember(inst, "animalGroup", group);            // AnimalDen.animalGroup [35315]
            InvokeSetGridCellLocations(inst, cells);          // [35512] grid + pathfinding + serialize
            InvokeSpawnAnimalsAtDen(inst);                    // [35587] populate the den
        }

        // ---- PERSISTENT: Deer SPAWN-AREA node AT THE CURSOR ----
        // Mirrors the engine's own SpawnDeer dev command [62750]: make a NEW AnimalSpawnArea centred on
        // the cursor and spawn `count` deer tied to it. FF's version uses the cursor as the rect CORNER
        // (node lands ~100 m off) and never registers the area, so it can't self-respawn — we centre on
        // the cursor AND register the area in the manager's spawn grid (decompile 91789-91791 indexes
        // through spawnAreaGridRandomizedIndexes -> spawnAreaGrid) so the node self-respawns deer over
        // time and shows the circular spawn-area marker. Grid lists are protected -> reflected.
        // NOTE: the area is NOT serialized, so a freshly-made node won't survive save/load — re-drop it.
        private static FieldInfo? _spawnAreaGridField, _spawnAreaIndexesField;

        private static bool SpawnDeerArea(AnimalManager am, Vector3 world, int count)
        {
            try
            {
                var groups = am.GetAllAnimalGroupDefinitions(AnimalGroupDefinition.AnimalType.Deer);
                if (groups == null || groups.Count == 0)
                {
                    if (Config.DebugLog.Value)
                        MelonLogger.Warning("[DivineHands] No Deer AnimalGroupDefinitions — can't make a spawn-area node");
                    return false;
                }

                // ~the map's own 64 m spawn grid; centred so the node/marker lands AT the click (FF's dev
                // command uses the cursor as a corner). new Rect(x,y,w,h) takes a bottom-left origin.
                const float areaSize = 60f;
                var rect = new Rect(world.x - areaSize / 2f, world.z - areaSize / 2f, areaSize, areaSize);
                var area = new AnimalSpawnArea(rect, am.animalSpawnAreaIdCounter++); // public ctor [36206] + counter [90518]

                RegisterSpawnArea(am, area); // so the engine's respawn tick keeps it populated

                int spawned = 0;
                for (int i = 0; i < count; i++)
                    if (am.SpawnAnimal(groups, area, world)) spawned++; // [92059]; ties the deer to the area

                if (Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] Deer spawn-area node @ {world} — {spawned}/{count} deer " +
                                    "(persistent + self-respawning this session).");
                return spawned > 0;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] SpawnDeerArea failed: {ex.Message}");
                return false;
            }
        }

        // Register a runtime spawn-area in the manager's grid so the respawn tick picks it up. Both lists
        // are protected; add to spawnAreaGrid AND spawnAreaGridRandomizedIndexes (the tick indexes through
        // the latter) to keep them in sync. If reflection fails, the area still spawns its initial deer +
        // marker — it just won't self-respawn.
        private static void RegisterSpawnArea(AnimalManager am, AnimalSpawnArea area)
        {
            try
            {
                const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
                _spawnAreaGridField    ??= typeof(AnimalManager).GetField("spawnAreaGrid", F);
                _spawnAreaIndexesField ??= typeof(AnimalManager).GetField("spawnAreaGridRandomizedIndexes", F);
                var grid    = _spawnAreaGridField?.GetValue(am) as List<AnimalSpawnArea>;
                var indexes = _spawnAreaIndexesField?.GetValue(am) as List<int>;
                if (grid == null || indexes == null)
                {
                    if (Config.DebugLog.Value)
                        MelonLogger.Warning("[DivineHands] could not reflect spawn grid — deer node won't self-respawn");
                    return;
                }
                grid.Add(area);
                indexes.Add(grid.Count - 1);
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] RegisterSpawnArea failed: {ex.Message}");
            }
        }

        // ---- persistent-animal reflection helpers ----

        // Find a validAnimalGroups entry by animalType + spawnPointType. validAnimalGroups is PROTECTED.
        private static object? FindGroup(int animalType, int spawnPointType)
        {
            try
            {
                var am = GameManager.Instance?.animalManager;
                if (am == null || _validAnimalGroupsField == null) return null;
                var list = _validAnimalGroupsField.GetValue(am) as System.Collections.IEnumerable;
                if (list == null) return null;
                foreach (var g in list)
                {
                    if (g == null) continue;
                    var atObj = _spawnAreaGroupTypeProp?.GetValue(g);
                    var ptObj = _spawnAreaGroupPointTypeProp?.GetValue(g);
                    if (atObj == null || ptObj == null) continue;
                    if (Convert.ToInt32(atObj) == animalType && Convert.ToInt32(ptObj) == spawnPointType)
                        return g;
                }
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] FindGroup failed: {ex.Message}");
            }
            return null;
        }

        private static System.Collections.IList? GetQualifiedDenAreas(object am)
        {
            try { return _qualifiedDenAreasProp?.GetValue(am) as System.Collections.IList; }
            catch { return null; }
        }

        private static GameObject? InvokeGetWeightedDenPrefab(object group)
        {
            try
            {
                var mi = group.GetType().GetMethod("GetWeightedDenPrefab",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return mi?.Invoke(group, null) as GameObject;
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] GetWeightedDenPrefab threw: {ex.Message}");
                return null;
            }
        }

        private static void InvokeSetGridCellLocations(object denComponent, Vector2[] cells)
        {
            // public void AnimalDen.SetGridCellLocations(Vector2[] cells) [35512] — does
            // RotateOpeningToLowestHeight + aiPathfinder.AddGridOccupantToAIGridGraph + stores for save.
            var mi = denComponent.GetType().GetMethod("SetGridCellLocations",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Vector2[]) }, null);
            mi?.Invoke(denComponent, new object[] { cells });
        }

        private static void InvokeSpawnAnimalsAtDen(object denComponent)
        {
            // public void AnimalDen.SpawnAnimalsAtDen(int numAnimalsToSpawn) [35587].
            // Use the group's own per-area max so the den fills like the base game; fall back to a sane count.
            int numToSpawn = 3;
            try
            {
                var group = GetMember(denComponent, denComponent.GetType(), "animalGroup");
                if (group != null)
                {
                    // GetMaxPerSpawnArea(float animalScore, float cutoff) — use the den-score cutoff if available.
                    // We don't have the area's score here, so use a generous default and let the den clamp.
                    var mi = group.GetType().GetMethod("GetMaxPerSpawnArea",
                        BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { typeof(float), typeof(float) }, null);
                    if (mi != null)
                    {
                        var v = mi.Invoke(group, new object[] { 100f, 0f });
                        if (v is int n && n > 0) numToSpawn = n;
                    }
                }
            }
            catch { /* keep default numToSpawn */ }

            var spawn = denComponent.GetType().GetMethod("SpawnAnimalsAtDen",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            spawn?.Invoke(denComponent, new object[] { numToSpawn });
        }

        // Iterate spawn points like AnimalSpawnerMono does — allSpawnPointsRO is the read-only view
        // populated by CalculateSpawnPoints [36410]; allSpawnPoints is its protected backing list.
        private static List<Vector3>? GetSpawnAreaPoints(object area)
        {
            try
            {
                var ro = GetMember(area, area.GetType(), "allSpawnPointsRO") as System.Collections.IEnumerable;
                if (ro != null)
                {
                    var outList = new List<Vector3>();
                    foreach (var v in ro) if (v is Vector3 p) outList.Add(p);
                    if (outList.Count > 0) return outList;
                }
                // Fallback: the protected backing list directly.
                var backing = GetMember(area, area.GetType(), "allSpawnPoints") as System.Collections.IEnumerable;
                if (backing != null)
                {
                    var outList = new List<Vector3>();
                    foreach (var v in backing) if (v is Vector3 p) outList.Add(p);
                    return outList;
                }
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] GetSpawnAreaPoints failed: {ex.Message}");
            }
            return null;
        }

        private static void ResolveAnimalReflection(object am)
        {
            if (_animalReflectionResolved) return;
            _animalReflectionResolved = true;
            try
            {
                var amType = am.GetType();
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                // protected List<AnimalGroupDefinition> validAnimalGroups [90274] — walk base types.
                for (var t = amType; t != null && _validAnimalGroupsField == null; t = t.BaseType)
                    _validAnimalGroupsField = t.GetField("validAnimalGroups", F);

                // public List<AnimalDenSpawnArea> qualifiedAnimalDenSpawnAreas { get; protected set; } [90520]
                _qualifiedDenAreasProp = amType.GetProperty("qualifiedAnimalDenSpawnAreas", F);

                _animalDenType = FindType("AnimalDen");

                // AnimalGroupDefinition.animalType / spawnPointType getters [35947/35973].
                var groupType = FindType("AnimalGroupDefinition");
                if (groupType != null)
                {
                    _spawnAreaGroupTypeProp = groupType.GetProperty("animalType",
                        BindingFlags.Public | BindingFlags.Instance);
                    _spawnAreaGroupPointTypeProp = groupType.GetProperty("spawnPointType",
                        BindingFlags.Public | BindingFlags.Instance);
                }

                if (Config.DebugLog.Value)
                    MelonLogger.Msg("[DivineHands] Animal persistent reflection: " +
                        $"groups={_validAnimalGroupsField != null} dens={_qualifiedDenAreasProp != null} " +
                        $"denType={_animalDenType != null} typeProp={_spawnAreaGroupTypeProp != null}");
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] ResolveAnimalReflection failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------------
        // MINERAL  (infinite-pit rule for stone/clay/sand; finite+Deep for gold/iron/coal)
        // ---------------------------------------------------------------------

        private static void SpawnMineral(Vector3 world, int count)
        {
            var gm = GameManager.Instance;
            object? mm = gm != null ? gm.mineralManager : null;
            if (mm == null) return;

            ResolveMineralReflection(mm.GetType());

            var kind = (MineralKind)Mathf.Clamp(Config.SpawnSubtype.Value, 0, 5);

            // Spread N sites in a tight ring so they don't perfectly overlap.
            for (int i = 0; i < count; i++)
            {
                Vector3 p = ScatterAround(world, i, count, spacing: 6f);
                switch (kind)
                {
                    case MineralKind.Gold: SpawnOreSite(mm, 1 /*Gold*/, p); break;
                    case MineralKind.Iron: SpawnOreSite(mm, 0 /*Iron*/, p); break;
                    case MineralKind.Coal: SpawnOreSite(mm, 2 /*Coal*/, p); break;
                    case MineralKind.Stone: SpawnPit(_createStoneSite, mm, p, MineralKind.Stone); break;
                    case MineralKind.Clay: SpawnPit(_createClaySite, mm, p, MineralKind.Clay); break;
                    case MineralKind.Sand: SpawnPit(_createSandSite, mm, p, MineralKind.Sand); break;
                }
            }

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Spawned {count}x {kind} mineral @ {world} " +
                                $"(deep={Config.SpawnIsDeep.Value})");
        }

        // gold/iron/coal: replicate MineralManager.CreateMineralSite core [159415-418] deterministically.
        private static void SpawnOreSite(object mineralManager, int mineralTypeValue, Vector3 pos)
        {
            var mmType = mineralManager.GetType();
            var typeEnum = ResolveMineralTypeEnum();
            if (typeEnum == null)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning("[DivineHands] MineralSite.MineralType enum unresolved");
                return;
            }
            object typeBoxed = Enum.ToObject(typeEnum, mineralTypeValue);

            // 1) prefabData entry (public list, struct ctor) — drives prefab re-instantiation on load.
            try
            {
                var prefabDataList = mmType.GetProperty("mineralSitePrefabData")?.GetValue(mineralManager);
                var dataType = FindType("MineralSitePrefabData");
                if (prefabDataList != null && dataType != null)
                {
                    var dataCtor = dataType.GetConstructor(new[] { typeEnum, typeof(Vector3), typeof(float) });
                    var dataInst = dataCtor?.Invoke(new[] { typeBoxed, pos, (object)MineralRadius });
                    if (dataInst != null)
                        prefabDataList.GetType().GetMethod("Add")?.Invoke(prefabDataList, new[] { dataInst });
                }
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] mineral prefabData add failed: {ex.Message}");
            }

            // 2) the visible prefab + decal (PRIVATE, reflection); this INCREMENTS mineralDepositId
            //    (159497) and stamps the post-increment value onto the visible MineralDeposit.id.
            try
            {
                _createMineralSitePrefab?.Invoke(mineralManager,
                    new[] { typeBoxed, (object)pos, (object)MineralRadius });
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] CreateMineralSitePrefab failed: {ex.Message}");
            }

            // 3) read mineralDepositId AFTER the prefab call so the logical MineralSite id matches the
            //    visible deposit (engine subsite path constructs MineralSite with the post-increment id).
            int depositId = 0;
            try
            {
                var fld = mmType.GetField("mineralDepositId", BindingFlags.Public | BindingFlags.Instance);
                if (fld != null) depositId = (int)fld.GetValue(mineralManager);
            }
            catch { /* fall back to 0; only affects id uniqueness, harmless */ }

            // 4) the logical MineralSite (public ctor) added to the serialized list; apply Deep toggle.
            try
            {
                var sitesList = mmType.GetProperty("mineralSites")?.GetValue(mineralManager);
                var siteType = FindType("MineralSite");
                if (sitesList != null && siteType != null)
                {
                    bool deep = Config.SpawnIsDeep.Value;
                    int count = deep ? DeepMineCount : MineralFiniteCount;

                    var siteCtor = siteType.GetConstructor(new[]
                        { typeof(int), typeEnum, typeof(Vector3), typeof(float), typeof(int) });
                    var site = siteCtor?.Invoke(new[]
                        { (object)depositId, typeBoxed, pos, (object)MineralRadius, (object)count });
                    if (site != null)
                    {
                        if (deep)
                            siteType.GetProperty("isInfinite")?.SetValue(site, true); // settable + serialized
                        sitesList.GetType().GetMethod("Add")?.Invoke(sitesList, new[] { site });
                    }
                }
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] MineralSite add failed: {ex.Message}");
            }
        }

        // stone/clay/sand: call the private full-job creator (it adds the Site to the serialized list),
        // then force the just-created pit to infinite via its live component.
        private static void SpawnPit(MethodInfo? creator, object mineralManager, Vector3 pos, MineralKind kind)
        {
            if (creator == null)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] {kind} pit creator unresolved");
                return;
            }
            var mmType = mineralManager.GetType();
            string listName = kind == MineralKind.Stone ? "stoneSites"
                            : kind == MineralKind.Clay ? "claySites" : "sandSites";

            try
            {
                // CreateXSite(Vector2 loc, float radius, int count, bool avoidWater:false)
                var loc = new Vector2(pos.x, pos.z);
                creator.Invoke(mineralManager, new object[] { loc, PitRadius, PitCount, false });

                // Grab the freshly-added Site (last list element) and flip its component infinite.
                var list = mmType.GetProperty(listName)?.GetValue(mineralManager) as System.Collections.IList;
                if (list == null || list.Count == 0) return;
                var site = list[list.Count - 1];
                if (site != null) MakePitInfinite(site, kind);
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] {kind} pit spawn failed: {ex.Message}");
            }
        }

        // Set the live pit component's infiniteItems = true. Save() reads isInfinite off this, so it
        // persists. clay/sand: Resource.storage.infiniteItems. stone: StonePitResource.infiniteItems
        // directly (the load path 160062 uses that field), AND storage to satisfy the getter (160020).
        private static void MakePitInfinite(object site, MineralKind kind)
        {
            try
            {
                var siteType = site.GetType();
                // Site GameObject property: claySite / sandSite / stoneSite
                string goName = kind == MineralKind.Stone ? "stoneSite"
                              : kind == MineralKind.Clay ? "claySite" : "sandSite";
                var go = GetMember(site, siteType, goName) as GameObject;
                if (go == null) return;

                // Find the pit Resource component by name to avoid hard type refs.
                string compName = kind == MineralKind.Stone ? "StonePitResource"
                                : kind == MineralKind.Clay ? "ClayPitResource" : "SandPitResource";
                Component? comp = null;
                foreach (var c in go.GetComponents<Component>())
                    if (c != null && c.GetType().Name == compName) { comp = c; break; }
                if (comp == null) return;

                if (kind == MineralKind.Stone)
                {
                    // StonePitResource.infiniteItems (direct) — matches OnGameFinishedLoading [160062].
                    SetMember(comp, "infiniteItems", true);
                    // Also storage.infiniteItems so the getter at 160020 reports infinite.
                    SetStorageInfinite(comp);
                }
                else
                {
                    SetStorageInfinite(comp); // .storage.infiniteItems for clay/sand
                }
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] MakePitInfinite({kind}) failed: {ex.Message}");
            }
        }

        private static void SetStorageInfinite(Component comp)
        {
            // Resource.storage : ResourceStorage; storage.infiniteItems : bool
            var storage = GetMember(comp, comp.GetType(), "storage");
            if (storage == null) return;
            SetMember(storage, "infiniteItems", true);
        }

        // ---- reflective member get/set helpers (walk base types; field OR property) ----

        private const BindingFlags MemberFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static object? GetMember(object target, Type startType, string name)
        {
            for (var t = startType; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, MemberFlags);
                if (f != null) return f.GetValue(target);
                var p = t.GetProperty(name, MemberFlags);
                if (p != null && p.CanRead) return p.GetValue(target);
            }
            return null;
        }

        private static void SetMember(object target, string name, object value)
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, MemberFlags);
                if (f != null) { f.SetValue(target, value); return; }
                var p = t.GetProperty(name, MemberFlags);
                if (p != null && p.CanWrite) { p.SetValue(target, value); return; }
            }
        }

        private static void ResolveMineralReflection(Type mmType)
        {
            if (_mineralReflectionResolved) return;
            _mineralReflectionResolved = true;
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                var mineralTypeEnum = ResolveMineralTypeEnum();
                if (mineralTypeEnum != null)
                    _createMineralSitePrefab = mmType.GetMethod("CreateMineralSitePrefab", F, null,
                        new[] { mineralTypeEnum, typeof(Vector3), typeof(float) }, null);

                // Pick the Vector2 (loc) overloads explicitly — there are Rect overloads too.
                _createClaySite = FindSiteCreator(mmType, "CreateClaySite");
                _createSandSite = FindSiteCreator(mmType, "CreateSandSite");
                _createStoneSite = FindSiteCreator(mmType, "CreateStoneSite");
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] mineral reflection resolve failed: {ex.Message}");
            }
        }

        // CreateXSite(Vector2, float, int, bool) — the private full-job creators [159588/159699/159774].
        private static MethodInfo? FindSiteCreator(Type mmType, string name)
        {
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var m in mmType.GetMethods(F))
            {
                if (m.Name != name) continue;
                var p = m.GetParameters();
                if (p.Length == 4
                    && p[0].ParameterType == typeof(Vector2)
                    && p[1].ParameterType == typeof(float)
                    && p[2].ParameterType == typeof(int)
                    && p[3].ParameterType == typeof(bool))
                    return m;
            }
            return null;
        }

        private static Type? _mineralTypeEnumCache;
        private static Type? ResolveMineralTypeEnum()
        {
            if (_mineralTypeEnumCache != null) return _mineralTypeEnumCache;
            // MineralSite.MineralType — nested enum.
            var siteType = FindType("MineralSite");
            _mineralTypeEnumCache = siteType?.GetNestedType("MineralType");
            return _mineralTypeEnumCache;
        }

        private static Type? FindType(string simpleName)
        {
            var t = Type.GetType(simpleName + ", Assembly-CSharp");
            if (t != null) return t;
            // Fall back to scanning the Assembly-CSharp assembly (no namespace on these game types).
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.Contains("Assembly-CSharp")) continue;
                t = asm.GetType(simpleName);
                if (t != null) return t;
            }
            return null;
        }

        // ---------------------------------------------------------------------
        // VILLAGER  (public manager method; persists natively)
        // ---------------------------------------------------------------------

        private static void SpawnVillagers(Vector3 world, int count)
        {
            var gm = GameManager.Instance;
            var vpm = gm != null ? gm.villagerPopulationManager : null;
            if (vpm == null) return;

            bool announced = Config.SpawnAnnounceVillagers.Value;
            int made = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 p = ScatterAround(world, i, count, spacing: 2.5f);
                var v = vpm.SpawnVillagerImmigration(p, announced);
                if (v != null) made++;
            }
            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Spawned {made}/{count} villagers @ {world}");
        }

        // ---------------------------------------------------------------------
        // RESOURCE  (forageable / tree / rock / giant-rock via GUID prefs)
        // ---------------------------------------------------------------------

        private static void SpawnResources(Vector3 world, int count)
        {
            var kind = (ResourceKind)Mathf.Clamp(Config.SpawnSubtype.Value, 0, 3);
            string guidCsv = kind switch
            {
                ResourceKind.Forageable => Config.SpawnForageableGuids.Value,
                ResourceKind.Tree => Config.SpawnTreeGuids.Value,
                ResourceKind.Rock => Config.SpawnRockGuids.Value,
                ResourceKind.GiantRock => Config.SpawnGiantRockGuids.Value,
                _ => ""
            };

            // Resolve the candidate prefabs. A configured GUID list always wins (override); otherwise
            // TREES fall back to the map's OWN tree prototypes (Terrain2.Data.TreePrototypes — the same
            // source Pangu plants from), so tree spawning needs zero setup and is always map/DLC-correct.
            var prefabs = new List<GameObject>();
            var guids = SplitGuids(guidCsv);
            if (guids.Count > 0)
            {
                foreach (var g in guids)
                {
                    var pf = SafeGetPrefab(g);
                    if (pf != null) prefabs.Add(pf);
                }
            }
            else if (kind == ResourceKind.Tree)
            {
                prefabs.AddRange(CollectMapTreePrefabs());
            }

            if (prefabs.Count == 0)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning(kind == ResourceKind.Tree
                        ? "[DivineHands] No tree prefabs (map exposed no TreePrototypes and no GUID override is set)"
                        : $"[DivineHands] No GUIDs configured for {kind}");
                return;
            }

            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                // Cycle through the candidate prefabs for variety.
                var prefab = prefabs[i % prefabs.Count];
                if (prefab == null) continue;

                Vector3 p = ScatterAround(world, i, count, spacing: 3f);

                if (kind == ResourceKind.Tree)
                {
                    if (TryAddGrowingTree(prefab, p)) placed++;
                }
                else
                {
                    var go = UnityEngine.Object.Instantiate(prefab, p,
                        Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                    if (go == null) continue;
                    go.SetActive(true);

                    // Forageables need their replenish rate seeded so they actually regrow.
                    if (kind == ResourceKind.Forageable)
                        TrySeedForageable(go);

                    placed++;
                }
            }
            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Spawned {placed}/{count} {kind} @ {world}");
        }

        // The map's own tree species, pulled live from Terrain2.Data.TreePrototypes — exactly what
        // Pangu plants from (CollectAvailableTreePrefabs, Pangu_FF.decompiled.cs:10521). Keeps only
        // prototypes whose prefab is a real TreeResource and isn't regrowth-locked, so the Tree spawner
        // needs no GUIDs and always matches the current map + DLC. Cached per map (cleared in OnMapLoaded).
        private static GameObject[]? _mapTreePrefabs;

        private static List<GameObject> CollectMapTreePrefabs()
        {
            if (_mapTreePrefabs != null) return new List<GameObject>(_mapTreePrefabs);
            var result = new List<GameObject>();
            try
            {
                var gm = GameManager.Instance;
                object? tm = gm != null ? gm.terrainManager : null;
                if (tm == null) return result;

                // Terrain2Manager.terrain (private) -> Terrain2.Data -> Terrain2Data.TreePrototypes
                var terrain = tm.GetType()
                    .GetField("terrain", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(tm);
                var data = terrain?.GetType().GetProperty("Data")?.GetValue(terrain);
                var protos = data?.GetType().GetProperty("TreePrototypes")?.GetValue(data)
                    as System.Collections.IEnumerable;
                if (protos == null) return result;

                foreach (var proto in protos)
                {
                    if (proto == null) continue;
                    var pt = proto.GetType();
                    var prefab = (pt.GetField("prefab")?.GetValue(proto)
                                  ?? pt.GetProperty("prefab")?.GetValue(proto)) as GameObject;
                    if (prefab == null) continue;

                    var prevObj = pt.GetField("preventRegrowth")?.GetValue(proto)
                                  ?? pt.GetProperty("preventRegrowth")?.GetValue(proto);
                    if (prevObj is bool prevent && prevent) continue;

                    if (prefab.GetComponent("TreeResource") == null) continue; // must be a real tree
                    result.Add(prefab);
                }

                _mapTreePrefabs = result.ToArray();
                if (Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] Collected {result.Count} map tree prototype(s)");
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] CollectMapTreePrefabs failed: {ex.Message}");
            }
            return result;
        }

        private static bool TryAddGrowingTree(GameObject prefab, Vector3 p)
        {
            try
            {
                var gm = GameManager.Instance;
                object? tm = gm != null ? gm.terrainManager : null;
                if (tm == null) return false;
                // public override bool AddGrowingTree(GameObject, float worldX, float worldZ,
                //   Vector2 xzOffset, float startPercentGrown, bool checkValidityOnly, float treeGrowth) [217752]
                var mi = tm.GetType().GetMethod("AddGrowingTree", new[]
                {
                    typeof(GameObject), typeof(float), typeof(float),
                    typeof(Vector2), typeof(float), typeof(bool), typeof(float)
                });
                if (mi == null) return false;
                var ok = mi.Invoke(tm, new object[]
                    { prefab, p.x, p.z, Vector2.zero, TreeStartGrown, false, 1f });
                return ok is bool b && b;
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] AddGrowingTree failed: {ex.Message}");
                return false;
            }
        }

        private static void TrySeedForageable(GameObject go)
        {
            try
            {
                // ForageableResource.SetRandomReplenishRateOnSpawn() [public 87902]
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var mi = c.GetType().GetMethod("SetRandomReplenishRateOnSpawn",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (mi != null) { mi.Invoke(c, null); return; }
                }
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] forageable seed failed: {ex.Message}");
            }
        }

        private static GameObject? SafeGetPrefab(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            try
            {
                var map = GlobalAssets.prefabAssetMap; // public static [96552]
                if (map == null) return null;
                return map.GetPrefab(guid.Trim()); // returns null on miss, logs, no throw [48987]
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] GetPrefab('{guid}') threw: {ex.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------------

        private static List<string> SplitGuids(string csv)
        {
            var outp = new List<string>();
            if (string.IsNullOrWhiteSpace(csv)) return outp;
            foreach (var raw in csv.Split(new[] { ',', ';', '\n', '\r', ' ', '\t' },
                                          StringSplitOptions.RemoveEmptyEntries))
            {
                var g = raw.Trim();
                if (g.Length > 0) outp.Add(g);
            }
            return outp;
        }

        // Lay out N spawns in an outward spiral around the cursor so they don't all stack on one point.
        private static Vector3 ScatterAround(Vector3 center, int index, int total, float spacing)
        {
            if (total <= 1 || index == 0) return center;
            const float golden = 2.399963f; // golden-angle radians for an even sunflower spread
            float ang = index * golden;
            float r = spacing * Mathf.Sqrt(index);
            return new Vector3(center.x + Mathf.Cos(ang) * r, center.y, center.z + Mathf.Sin(ang) * r);
        }
    }
}
