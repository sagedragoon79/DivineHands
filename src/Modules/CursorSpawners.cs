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
        public enum AnimalKind { Deer, Bear, Boar, Wolf }
        public enum MineralKind { Gold, Iron, Coal, Stone, Clay, Sand }
        public enum ResourceKind { Forageable, Tree, Rock, GiantRock }

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
        }

        public static void OnSceneExit() => OnMapLoaded();

        public static void OnUpdate()
        {
            if (!Config.SpawnEnable.Value) return;

            // Only fire while the spawner tool is the armed panel mode and the cursor isn't on the panel.
            if (DivineHands.Core.DivinePanel.SpawnerModeActive
                && !DivineHands.Core.DivinePanel.BlocksGameInput
                && Hotkey.Pressed(Config.SpawnApplyKey.Value))
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

            var kind = (AnimalKind)Mathf.Clamp(Config.SpawnSubtype.Value, 0, 3);
            switch (kind)
            {
                case AnimalKind.Deer: am.DebugSpawnDeerAtPoint(count, world); break;
                case AnimalKind.Bear: am.DebugSpawnBearsAtPoint(count, world); break;
                case AnimalKind.Boar: am.DebugSpawnBoarsAtPoint(count, world); break;
                case AnimalKind.Wolf: am.DebugSpawnWolvesAtPoint(count, world); break;
            }
            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Spawned {count}x {kind} @ {world} " +
                                "(runtime-only — animals do not persist through save/load)");
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

            var guids = SplitGuids(guidCsv);
            if (guids.Count == 0)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] No GUIDs configured for {kind}");
                return;
            }

            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                // Cycle through the configured GUIDs for variety.
                string guid = guids[i % guids.Count];
                var prefab = SafeGetPrefab(guid);
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
