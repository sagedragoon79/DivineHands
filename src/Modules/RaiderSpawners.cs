using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace DivineHands.Modules
{
    /// <summary>
    /// RAIDER SPAWNING — the hostile half of the Cursor Spawner (Family.Raider).
    ///
    /// FF ships its own debug spawners for this and left them PUBLIC, so units need no reflection:
    ///   • units — CombatManager.DebugSpawnRaidGroupAtLocation(RaidGroupSetupData, int, Vector3) [70703]
    ///             (builds an incursion tracker + group locations, then spawns the real raid group)
    ///   • ram   — CombatManager.DebugSpawnRamAtPoint(Vector3) [70672]
    ///
    /// Camps/towers are prefab instantiation, mirroring CombatManager.SpawnRaidCamps [70889]:
    /// Instantiate -> SmoothTerrain (best-effort) -> PlaceObjectOnTerrain -> register in the manager's
    /// spawnedRaidCamps list. A RaiderCamp SELF-ASSEMBLES in Start() when it was not loaded from a save
    /// [79263]: ClearResources() (clears trees/details in its footprint), SpawnTowers() (its own ring of
    /// guard towers), SetupStats() (difficulty-scaled HP/damage) — so ONE Instantiate yields a complete,
    /// functioning camp.
    ///
    /// A standalone guard tower is safe: RaiderGuardTower.associatedRaiderCamp is null-guarded before use
    /// [79876], so a tower with no camp simply does not forward its attacked-alert.
    ///
    /// Everything here is deliberately hostile — spawned raiders attack the town for real. That is the
    /// point of a god tool, but it is why the panel labels this family plainly.
    /// </summary>
    internal static class RaiderSpawners
    {
        internal enum RaiderKind { Raiders, BatteringRam, Camp, LargeCamp, GuardTower, LargeGuardTower }

        /// <summary>Kinds that place exactly ONE object regardless of the Count slider.</summary>
        internal static bool IsSingleShot(RaiderKind k) => k != RaiderKind.Raiders;

        // ---- cached reflection (non-public CombatManager members) ----
        private static FieldInfo? _fRaidCampPrefabs, _fLargeRaidCampPrefabs, _fSpawnedRaidCamps;
        private static PropertyInfo? _pRaiderDifficulty;
        private static MethodInfo? _miSpawnRaider;      // protected Raider SpawnRaider(setup, group, point, entry) [70318]
        private static MethodInfo? _miAddGroupDelegates; // private void AddRaiderGroupDelegates(RaiderGroup) [70307]
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            var t = typeof(CombatManager);
            _fRaidCampPrefabs      = t.GetField("raidCampPrefabs", F);
            _fLargeRaidCampPrefabs = t.GetField("largeRaidCampPrefabs", F);
            _fSpawnedRaidCamps     = t.GetField("spawnedRaidCamps", F);
            _pRaiderDifficulty     = t.GetProperty("raiderDifficulty", F);
            _miSpawnRaider         = t.GetMethod("SpawnRaider", F, null,
                new[] { typeof(RaidGroupSetupData), typeof(RaiderGroup), typeof(Vector3), typeof(RaidGroupUnitEntry) }, null);
            _miAddGroupDelegates   = t.GetMethod("AddRaiderGroupDelegates", F, null, new[] { typeof(RaiderGroup) }, null);

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Raider reflection: camps={_fRaidCampPrefabs != null} " +
                                $"large={_fLargeRaidCampPrefabs != null} spawned={_fSpawnedRaidCamps != null} " +
                                $"difficulty={_pRaiderDifficulty != null} spawnRaider={_miSpawnRaider != null} " +
                                $"addDelegates={_miAddGroupDelegates != null}");
        }

        public static void OnMapLoaded() { _units = null; }   // unit table is per-map (difficulty-dependent)

        // =====================================================================
        // UNIT TABLE — every distinct raider type the current difficulty can field.
        //
        // Each option keeps the REAL RaidGroupSetupData asset it came from. That matters: raid groups are
        // referenced back by asset (FF loads them from Resources/ScriptableObjects/Combat/Raids [62476]),
        // so handing the game a synthetic runtime ScriptableObject risks a group that cannot resolve on
        // load. We always pass a genuine asset and steer only WHICH unit entry gets spawned.
        // =====================================================================

        internal sealed class UnitOption
        {
            public RaidIncursionUnit Unit = null!;
            public RaidGroupUnitEntry Entry = null!;
            public RaidGroupSetupData Owner = null!;
            public string Label = "";
        }

        private static List<UnitOption>? _units;

        /// <summary>Distinct raider unit types available on this map (cached per map). Index 0 in the UI is
        /// "Any", so the picker value N maps to <c>Units[N-1]</c>.</summary>
        internal static List<UnitOption> Units
        {
            get
            {
                if (_units != null) return _units;
                _units = new List<UnitOption>();
                try
                {
                    var gm = GameManager.Instance;
                    var cm = gm != null ? gm.combatManager : null;
                    if (cm == null) return _units;   // not in a map yet — retry next call
                    Resolve();

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var setup in AllRaidGroups(cm))
                    {
                        if (setup?.unitEntries == null) continue;
                        foreach (var e in setup.unitEntries)
                        {
                            if (e == null) continue;
                            var u = e.unit;
                            if (u == null || u.raiderPrefab == null) continue;
                            string key = u.name ?? "";
                            if (key.Length == 0 || !seen.Add(key)) continue;
                            _units.Add(new UnitOption { Unit = u, Entry = e, Owner = setup, Label = PrettyName(u) });
                        }
                    }
                    _units.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));

                    if (Config.DebugLog.Value)
                        MelonLogger.Msg($"[DivineHands] Raider unit types available ({_units.Count}): " +
                                        string.Join(", ", _units.ConvertAll(u => u.Label).ToArray()));
                }
                catch (Exception ex)
                {
                    if (Config.DebugLog.Value)
                        MelonLogger.Warning($"[DivineHands] Raider unit discovery failed: {ex.Message}");
                }
                return _units;
            }
        }

        /// <summary>Localized unit name when the game has one, else the asset name split into words
        /// ("RaiderSpearman" -> "Raider Spearman").</summary>
        private static string PrettyName(RaidIncursionUnit u)
        {
            try
            {
                var token = u.localizedDisplayName;
                if (!string.IsNullOrEmpty(token))
                {
                    var loc = GameManager.Instance?.localizationManager?.Localize(token);
                    if (!string.IsNullOrEmpty(loc) && loc != token) return Shorten(loc!);
                }
            }
            catch { }

            string n = u.name ?? "Unit";
            var sb = new System.Text.StringBuilder(n.Length + 4);
            for (int i = 0; i < n.Length; i++)
            {
                if (i > 0 && char.IsUpper(n[i]) && !char.IsUpper(n[i - 1])) sb.Append(' ');
                sb.Append(n[i]);
            }
            return Shorten(sb.ToString());
        }

        /// <summary>Trim the redundant leading "Raider" from a unit name — the family chip already says
        /// Raider, and the panel value box shows a name, not a paragraph ("Raider Spearman" -> "Spearman").
        /// Applied to localized names too, so both paths stay short enough to read.</summary>
        private static string Shorten(string name)
        {
            var n = (name ?? "").Trim();
            if (n.Length > 7 && n.StartsWith("Raider", StringComparison.OrdinalIgnoreCase))
            {
                var rest = n.Substring(6).TrimStart('_', ' ', '-');
                if (rest.Length > 0) return rest;
            }
            return n;
        }

        /// <summary>The label the panel shows for a picker value (0 = Any).</summary>
        internal static string UnitLabel(int pickerValue)
        {
            if (pickerValue <= 0) return "Any (mixed)";
            var list = Units;
            int i = pickerValue - 1;
            return (i >= 0 && i < list.Count) ? list[i].Label : "—";
        }

        /// <summary>Highest valid picker value (0 when the table is empty / not in a map).</summary>
        internal static int MaxUnitPickerValue => Units.Count;

        private static IEnumerable<RaidGroupSetupData> AllRaidGroups(CombatManager cm)
        {
            var result = new List<RaidGroupSetupData>();
            try
            {
                var difficulty = _pRaiderDifficulty?.GetValue(cm);
                if (difficulty == null) return result;
                if (!(AccessTools.Field(difficulty.GetType(), "raidGroups")?.GetValue(difficulty) is IEnumerable incursions))
                    return result;

                foreach (var inc in incursions)
                {
                    if (inc == null) continue;
                    var setup = AccessTools.Field(inc.GetType(), "raidIncursionSetupData")?.GetValue(inc);
                    if (setup == null) continue;
                    if (!(AccessTools.Field(setup.GetType(), "groupEntries")?.GetValue(setup) is IEnumerable entries)) continue;
                    foreach (var e in entries)
                    {
                        if (e == null) continue;
                        if (AccessTools.Field(e.GetType(), "group")?.GetValue(e) is RaidGroupSetupData g
                            && g != null && g.unitEntries != null && g.unitEntries.Count > 0)
                            result.Add(g);
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>Place the selected raider thing at the cursor point.</summary>
        public static void Spawn(Vector3 world, int count)
        {
            var kind = (RaiderKind)Mathf.Clamp(Config.SpawnSubtype.Value, 0, 5);
            var gm = GameManager.Instance;
            var cm = gm != null ? gm.combatManager : null;
            if (cm == null) { MelonLogger.Warning("[DivineHands] Raider spawn: no CombatManager"); return; }

            Resolve();
            switch (kind)
            {
                case RaiderKind.Raiders:         SpawnRaiderUnits(cm, world, count); break;
                case RaiderKind.BatteringRam:    SpawnRam(cm, world);                break;
                case RaiderKind.Camp:            SpawnCamp(cm, world, large: false); break;
                case RaiderKind.LargeCamp:       SpawnCamp(cm, world, large: true);  break;
                case RaiderKind.GuardTower:      SpawnTower(cm, world, large: false); break;
                case RaiderKind.LargeGuardTower: SpawnTower(cm, world, large: true);  break;
            }
        }

        // =====================================================================
        // UNITS
        // =====================================================================

        private static void SpawnRaiderUnits(CombatManager cm, Vector3 world, int count)
        {
            count = Mathf.Max(1, count);

            // A specific unit type was picked -> spawn exactly that. Falls back to a mixed group if the
            // selection is stale (unit table changed) or the SpawnRaider reflection is unavailable.
            int pick = Config.SpawnRaiderUnit.Value;
            if (pick > 0)
            {
                var list = Units;
                int i = pick - 1;
                if (i >= 0 && i < list.Count && _miSpawnRaider != null)
                {
                    SpawnSpecificUnit(cm, list[i], world, count);
                    return;
                }
                if (Config.DebugLog.Value)
                    MelonLogger.Msg("[DivineHands] Raider unit selection unavailable — falling back to a mixed group.");
            }

            var group = PickRaidGroup(cm);
            if (group == null)
            {
                MelonLogger.Warning("[DivineHands] Raider spawn: no raid groups available for the current " +
                                    "difficulty (pacifist mode, or raider data not loaded).");
                return;
            }
            try
            {
                // Public debug entry point — handles the incursion tracker, spacing and navmesh snapping.
                cm.DebugSpawnRaidGroupAtLocation(group, count, world);
                MelonLogger.Msg($"[DivineHands] Spawned {count} raider(s) from group '{group.name}' @ {world}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] Raider unit spawn failed: {ex.Message}");
            }
        }

        /// <summary>Spawn <paramref name="count"/> raiders that are ALL the chosen unit type.
        ///
        /// Mirrors CombatManager.SpawnRaidGroupAtLocations [70226] but skips its weighted unit roll and its
        /// ram/catapult rolls, so the group is exactly what was asked for. Every step uses the game's own
        /// machinery (real setup-data asset, real RaiderGroup, real incursion tracker, real spawn event)
        /// so the raiders behave — and save — like any other raid.</summary>
        private static void SpawnSpecificUnit(CombatManager cm, UnitOption opt, Vector3 world, int count)
        {
            try
            {
                var gm = GameManager.Instance;

                // Spread the spawn points the same way the game does, honouring the unit's navmesh areas.
                int? areaMask = null;
                try
                {
                    var agent = opt.Unit.raiderPrefab != null
                        ? opt.Unit.raiderPrefab.GetComponent<AICrateNavMeshAgent>() : null;
                    if (agent != null) areaMask = agent.areaMask;
                }
                catch { }

                var locations = new List<Vector3>();
                MiscUtilities.GetGroupLocations(ref locations, world, count, 4f, areaMask);
                if (locations.Count == 0) locations.Add(world);

                var tracker = cm.CreateNewRaidIncursionTracker(null);
                var raiderGroup = cm.CreateRaiderGroup(opt.Owner);
                // Depletion cleanup (removes the group + empty trackers when the last raider dies).
                try { _miAddGroupDelegates?.Invoke(cm, new object[] { raiderGroup }); } catch { }

                int spawned = 0;
                foreach (var loc in locations)
                {
                    var r = _miSpawnRaider!.Invoke(cm, new object[] { opt.Owner, raiderGroup, loc, opt.Entry }) as Raider;
                    if (r == null) continue;
                    r.wantsToMeetAtFormation = true;   // regroup then attack, as a normal raid does
                    spawned++;
                }

                tracker.TrackRaiderGroup(raiderGroup);
                if (spawned > 0) gm?.eventManager?.Raise(new RaiderGroupSpawnedEvent(raiderGroup));
                cm.RemoveTrackerIfNoRaidsSpawned(tracker);

                MelonLogger.Msg($"[DivineHands] Spawned {spawned}x {opt.Label} @ {world}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] Raider unit spawn ({opt.Label}) failed: {ex.Message}");
            }
        }

        private static void SpawnRam(CombatManager cm, Vector3 world)
        {
            try
            {
                cm.DebugSpawnRamAtPoint(world);
                MelonLogger.Msg($"[DivineHands] Spawned battering ram @ {world}");
            }
            catch (Exception ex)
            {
                // DebugSpawnRamAtPoint indexes list[0] of ram-capable groups — if the current difficulty
                // defines none, that throws rather than returning.
                MelonLogger.Warning($"[DivineHands] Battering ram spawn failed ({ex.GetType().Name}) — the " +
                                    "current raider difficulty may define no ram-carrying raid groups.");
            }
        }

        /// <summary>A random RaidGroupSetupData from the active difficulty's incursion table:
        /// raiderDifficulty.raidGroups[].raidIncursionSetupData.groupEntries[].group.</summary>
        private static RaidGroupSetupData? PickRaidGroup(CombatManager cm)
        {
            try
            {
                var difficulty = _pRaiderDifficulty?.GetValue(cm);
                if (difficulty == null) return null;

                if (!(AccessTools.Field(difficulty.GetType(), "raidGroups")?.GetValue(difficulty) is IEnumerable incursions))
                    return null;

                var groups = new List<RaidGroupSetupData>();
                foreach (var inc in incursions)
                {
                    if (inc == null) continue;
                    var setup = AccessTools.Field(inc.GetType(), "raidIncursionSetupData")?.GetValue(inc);
                    if (setup == null) continue;
                    if (!(AccessTools.Field(setup.GetType(), "groupEntries")?.GetValue(setup) is IEnumerable entries)) continue;
                    foreach (var e in entries)
                    {
                        if (e == null) continue;
                        if (AccessTools.Field(e.GetType(), "group")?.GetValue(e) is RaidGroupSetupData g
                            && g != null && g.unitEntries != null && g.unitEntries.Count > 0)
                            groups.Add(g);
                    }
                }
                if (groups.Count == 0) return null;
                return groups[UnityEngine.Random.Range(0, groups.Count)];
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] PickRaidGroup failed: {ex.Message}");
                return null;
            }
        }

        // =====================================================================
        // CAMPS + TOWERS
        // =====================================================================

        private static void SpawnCamp(CombatManager cm, Vector3 world, bool large)
        {
            var list = (large ? _fLargeRaidCampPrefabs : _fRaidCampPrefabs)?.GetValue(cm) as IList;
            var prefab = PickPrefab(list);
            if (prefab == null)
            {
                MelonLogger.Warning($"[DivineHands] Raider {(large ? "large camp" : "camp")} spawn: no prefabs available.");
                return;
            }
            try
            {
                var go = UnityEngine.Object.Instantiate(prefab, world, Quaternion.identity);
                if (go == null) return;

                // Vanilla smooths the pad before seating the camp so a multi-cell camp does not float or
                // clip on a slope. Best-effort: a failure here only costs us the flattening.
                TrySmoothPad(world, large);
                PlaceOnTerrain(go);

                // Register with the manager so the camp counts for raid logic / camp-destroyed tracking.
                (_fSpawnedRaidCamps?.GetValue(cm) as IList)?.Add(go);

                MelonLogger.Msg($"[DivineHands] Spawned raider {(large ? "LARGE camp" : "camp")} @ {world} " +
                                "(it clears its own footprint and raises its own guard towers)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] Raider camp spawn failed: {ex.Message}");
            }
        }

        private static void SpawnTower(CombatManager cm, Vector3 world, bool large)
        {
            IList? list = null;
            try { list = large ? (IList?)cm.largeGuardTowerPrefabsRO : cm.guardTowerPrefabsRO; } catch { }
            var prefab = PickPrefab(list);
            if (prefab == null)
            {
                MelonLogger.Warning($"[DivineHands] Raider {(large ? "large tower" : "tower")} spawn: no prefabs available.");
                return;
            }
            try
            {
                var go = UnityEngine.Object.Instantiate(prefab, world, Quaternion.identity);
                if (go == null) return;
                PlaceOnTerrain(go);
                MelonLogger.Msg($"[DivineHands] Spawned raider {(large ? "large guard tower" : "guard tower")} @ {world}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] Raider tower spawn failed: {ex.Message}");
            }
        }

        private static GameObject? PickPrefab(IList? list)
        {
            if (list == null || list.Count == 0) return null;
            return list[list.Count == 1 ? 0 : UnityEngine.Random.Range(0, list.Count)] as GameObject;
        }

        private static void PlaceOnTerrain(GameObject go)
        {
            try
            {
                var gm = GameManager.Instance;
                var tm = gm != null ? gm.terrainManager : null;
                if (tm != null) tm.PlaceObjectOnTerrain(go);
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] PlaceObjectOnTerrain failed: {ex.Message}");
            }
        }

        /// <summary>Flatten the camp footprint the way SpawnRaidCamps does. Optional — skipped silently if
        /// any of the manager handles are not what we expect.</summary>
        private static void TrySmoothPad(Vector3 world, bool large)
        {
            try
            {
                var gm = GameManager.Instance;
                var tm = gm != null ? gm.terrainManager : null;
                var bm = gm != null ? gm.buildManager : null;
                var cm = gm != null ? gm.combatManager : null;
                if (tm == null || bm == null || cm == null) return;

                int gridSize = large ? cm.raidCampLargePrefabGridSize : cm.raidCampPrefabGridSize;
                if (gridSize <= 0) return;

                float side = (gridSize + 2) * bm.cellSize;      // matches RaiderCamp.ClearResources' rect
                var rect = new Rect(0f, 0f, side, side);
                rect.center = new Vector2(world.x, world.z);

                tm.SmoothTerrain(rect, tm.buildSmoothing, tm.allowedDeltaWhenSmoothing,
                                 bm.GetAllBuildingConstructionRects(), rebuildCollision: true, "DivineHandsRaidCamp");
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] Camp pad smoothing skipped: {ex.Message}");
            }
        }
    }
}
