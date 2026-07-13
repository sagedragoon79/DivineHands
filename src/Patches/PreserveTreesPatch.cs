using HarmonyLib;

namespace DivineHands.Patches
{
    /// <summary>
    /// Keeps trees placed over structures (plaza tiles, building footprints — possible with Build
    /// Anywhere / the spawners) alive across save/reload.
    ///
    /// Mechanism (verified): trees SAVE fine in both forms (grid trees via Terrain2Manager.Save,
    /// converted TreeResource GOs via SaveManager.SavePrefabInstances [182597]). They die at LOAD:
    /// Building.Load [328900] calls ConstructionComplete(..., ConstructionType.LOAD, ...) [329754],
    /// which — for any clearMode other than NO_CLEAR — re-runs
    /// MiscUtilities.DestroyResourceObjects(footprint + border) [329780], destroying every tree /
    /// forageable / stone in the rect. Plazas are DecorativeBuilding : Building, so every plaza
    /// re-sweeps its tile on every load. (Buildings-on-buildings survive because nothing re-validates
    /// building overlap at load — only resources get re-swept.)
    ///
    /// Fix: when the completion is the LOAD replay, rewrite clearMode to NO_CLEAR. LOAD is passed
    /// ONLY by Building.Load [328900], so live construction / upgrade / relocate clearing is
    /// untouched, and for vanilla saves the skipped sweep is redundant anyway — the site was already
    /// cleared when it was built (skipping also stops the sweep polluting choppedTrees each load).
    /// All ~13 ConstructionComplete overrides pass clearMode through to base, where the single
    /// clearMode use lives, so patching the base covers every building type.
    ///
    /// Positional __n args (project convention — Harmony matches named args by parameter name, which
    /// broke once before when the shipped DLL's names differed from the decompile).
    /// NOTE: roads have a separate load-only sweep (SplineRoadContainer.CompleteRoad, wasLoaded gate)
    /// — trees on ROADS still vanish on reload; not covered here.
    /// </summary>
    [HarmonyPatch(typeof(Building), "ConstructionComplete")]
    internal static class PreserveTreesPatch
    {
        // __1 = BuildingData.SiteClearingMode clearMode (by ref), __2 = ConstructionType type
        private static void Prefix(ref BuildingData.SiteClearingMode __1, ConstructionType __2)
        {
            if (Config.PreserveOverlapTrees.Value && __2 == ConstructionType.LOAD)
                __1 = BuildingData.SiteClearingMode.NO_CLEAR;
        }
    }
}
