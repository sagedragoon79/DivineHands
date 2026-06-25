using HarmonyLib;

namespace DivineHands.Patches
{
    /// <summary>
    /// Build Anywhere — lets you confirm a NORMAL building on ground vanilla would reject
    /// (steep slope, no path to town, water/road overlap, etc.) when made available in config
    /// (<see cref="Config.EnableBuildAnywhere"/>) AND activated in the panel (<see cref="Active"/>).
    /// Re-implemented natively (no WickerToolbox dependency) as three Harmony prefixes on the
    /// verified placement/pathing gates:
    ///
    ///   1. <c>Placeable.IsPlacementValid</c>             (decompile L348583, public virtual bool)
    ///   2. <c>PlacementValidityHelper.CanPathToPoint</c> (decompile L216399, private static bool)
    ///   3. <c>WagonShop.CanPathToWagon</c>               (decompile L361508, private bool)
    ///   4. <c>Placeable.excludeValidityCheckFlags</c>    (getter, L348515) — slope bypass (see below)
    ///
    /// === Why "slope too steep" needs its own hook ===
    /// The slope rejection is NOT part of <c>IsPlacementValid</c> — it's a PER-CELL flag
    /// (<c>PlacementGridValidityCheckFlags.TerrainTooSteep</c> 0x10 / <c>TerrainTooSteepForBuilding</c>
    /// 0x20) computed by <c>UpdatePlaceableValidity</c>, which builds the active check set as
    /// <c>(invalidation | required) &amp; ~excludeValidityCheckFlags</c> (L214968) and threads the same
    /// exclude set into per-cell validity (L348726-348727). <c>IsPlacementValid</c> only READS the
    /// resulting failed flags, so forcing it true never relaxed slope — the cells were still red and the
    /// confirm/visual still saw the failure. Patch #4 OR-s the two slope flags into the getter so the
    /// slope check simply doesn't run: cells stay valid, no slope failure flag is ever set, and EVERY
    /// downstream consumer agrees. Bridge-scoped like the rest.
    ///
    /// === Keep Clarity (Bridge Anywhere) coexistence ===
    /// KC's BridgeAnywherePatches selectively clear the <c>Overlap_Water</c> flag and tune the bridge
    /// snap/height pipeline so dry-land bridges validate. KC patches FOUR DIFFERENT methods
    /// (PeformBridgeStartCellValidityChecks, TryToSnapToValidPosition, UpdateBridgeValidity,
    /// BridgeContainer.AssignStartAndEndCells) — none of which are DH's three targets, so there is no
    /// Harmony collision. The risk is purely SEMANTIC: if we force-validate while a PlaceableBridge is
    /// the active placeable, we'd short-circuit the exact flags/pathing KC is steering and bridges
    /// would "validate" everywhere — breaking KC's snap, water-adjacency and height logic.
    ///
    /// SCOPING RULE: whenever the active placeable is a <c>PlaceableBridge</c>, we DO NOT bypass — we
    /// return true from the prefix so the vanilla body (and therefore KC's patches) run unchanged. We
    /// only force-validate for NON-bridge placements. The scoping is also NECESSARY, not just
    /// defensive: PlaceableBridge inherits Placeable.IsPlacementValid (it does NOT override it), so
    /// without the guard the IsPlacementValid prefix would intercept bridge placements too.
    /// WagonShop pathing is logistics, unrelated to placement and untouched by KC, so it needs no
    /// bridge scoping.
    ///
    /// All prefixes are no-ops (run vanilla) when the master switch or the BuildAnywhere toggle is
    /// off, and are wrapped defensively so a prefix can never throw.
    /// </summary>
    internal static class BuildAnywherePatches
    {
        /// <summary>Runtime live ON/OFF for Build Anywhere — toggled in the in-game panel, NOT a saved
        /// pref. Reset to false on every map load / scene exit (by Plugin), so the power always starts
        /// off when entering a map even if its Enable pref is on.</summary>
        public static bool Active;

        /// <summary>Single gate: the feature applies only when the master switch AND the Enable pref
        /// (config: make-available) AND the runtime <see cref="Active"/> flag (panel: activate) are on.</summary>
        private static bool Engaged =>
            Config.MasterEnable != null && Config.MasterEnable.Value &&
            Config.EnableBuildAnywhere != null && Config.EnableBuildAnywhere.Value &&
            Active;

        /// <summary>
        /// True when the placeable currently being positioned is a bridge — in which case we must
        /// defer entirely to vanilla + Keep Clarity and NOT force-validate. Mirrors the exact
        /// extraction vanilla's own CanPathToPoint does (decompile L216409-216410).
        /// </summary>
        private static bool ActivePlaceableIsBridge()
        {
            try
            {
                GameManager? gm = UnitySingleton<GameManager>.Instance;
                InputManager? inputManager = (gm != null) ? gm.inputManager : null;
                Placeable? placeable = (inputManager != null) ? inputManager.placeableInstance : null;
                return placeable is PlaceableBridge;
            }
            catch
            {
                // If we can't tell, treat as "bridge" => don't bypass => safest (vanilla runs).
                return true;
            }
        }

        // ===== 1. Placeable.IsPlacementValid =====
        [HarmonyPatch(typeof(Placeable), "IsPlacementValid")]
        internal static class PatchIsPlacementValid
        {
            // Returning false skips the vanilla body and uses our __result; returning true runs vanilla.
            private static bool Prefix(Placeable __instance, ref bool __result)
            {
                try
                {
                    if (!Engaged) return true;                       // feature off => vanilla
                    if (__instance is PlaceableBridge) return true; // bridge => defer to vanilla + KC

                    __result = true;                                // force this placement valid
                    return false;                                   // skip vanilla body
                }
                catch
                {
                    return true; // never throw from a prefix => fall back to vanilla
                }
            }
        }

        // ===== 2. PlacementValidityHelper.CanPathToPoint (static) =====
        [HarmonyPatch(typeof(PlacementValidityHelper), "CanPathToPoint")]
        internal static class PatchCanPathToPoint
        {
            private static bool Prefix(ref bool __result)
            {
                try
                {
                    if (!Engaged) return true;                   // feature off => vanilla
                    if (ActivePlaceableIsBridge()) return true; // bridge => defer to vanilla + KC

                    __result = true;                            // pretend a path to town exists
                    return false;                               // skip vanilla body
                }
                catch
                {
                    return true;
                }
            }
        }

        // ===== 3. WagonShop.CanPathToWagon =====
        [HarmonyPatch(typeof(WagonShop), "CanPathToWagon")]
        internal static class PatchCanPathToWagon
        {
            // Logistics check, orthogonal to placement and untouched by KC — no bridge scoping needed.
            private static bool Prefix(ref bool __result)
            {
                try
                {
                    if (!Engaged) return true; // feature off => vanilla

                    __result = true;          // wagon can always reach an isolated WagonShop
                    return false;
                }
                catch
                {
                    return true;
                }
            }
        }

        // ===== 4. Placeable.excludeValidityCheckFlags (slope bypass) =====
        // OR-in the two slope flags so the per-cell "terrain too steep" check is excluded from the active
        // check set (decompile L214968) and from per-cell validity (L348726-348727). This is the ONLY
        // path that actually relaxes slope — IsPlacementValid merely reads the resulting flags. We touch
        // only slope (overlaps, edge-of-map, fog, etc. still apply, so you can't stack buildings).
        [HarmonyPatch(typeof(Placeable), "get_excludeValidityCheckFlags")]
        internal static class PatchExcludeSlopeChecks
        {
            private static void Postfix(Placeable __instance, ref PlacementGridValidityCheckFlags __result)
            {
                try
                {
                    if (!Engaged) return;                           // feature off => vanilla
                    if (__instance is PlaceableBridge) return;     // bridge => defer to vanilla + KC
                    __result |= PlacementGridValidityCheckFlags.TerrainTooSteep
                              | PlacementGridValidityCheckFlags.TerrainTooSteepForBuilding;
                }
                catch
                {
                    // Never throw from a postfix — leave the vanilla exclude set untouched.
                }
            }
        }
    }
}
