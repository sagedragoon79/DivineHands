using HarmonyLib;

namespace DivineHands.Patches
{
    /// <summary>
    /// Build Anywhere — lets you confirm a NORMAL building on ground vanilla would reject
    /// (steep slope, no path to town, water/road overlap, etc.) when <see cref="Config.BuildAnywhere"/>
    /// is on. Re-implemented natively (no WickerToolbox dependency) as three Harmony prefixes on the
    /// verified placement/pathing gates:
    ///
    ///   1. <c>Placeable.IsPlacementValid</c>             (decompile L348583, public virtual bool)
    ///   2. <c>PlacementValidityHelper.CanPathToPoint</c> (decompile L216399, private static bool)
    ///   3. <c>WagonShop.CanPathToWagon</c>               (decompile L361508, private bool)
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
        /// <summary>Single gate: the feature applies only when the mod master switch AND BuildAnywhere are on.</summary>
        private static bool Active =>
            Config.MasterEnable != null && Config.MasterEnable.Value &&
            Config.BuildAnywhere != null && Config.BuildAnywhere.Value;

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
                    if (!Active) return true;                       // feature off => vanilla
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
                    if (!Active) return true;                   // feature off => vanilla
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
                    if (!Active) return true; // feature off => vanilla

                    __result = true;          // wagon can always reach an isolated WagonShop
                    return false;
                }
                catch
                {
                    return true;
                }
            }
        }
    }
}
