using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using MelonLoader;

namespace DivineHands.Modules
{
    /// <summary>
    /// "Delete Selected" god tool — removes whatever is currently selected, if it's a mining BUILDING
    /// (Deep Mine / Stone Quarry / Iron-Gold-Coal Mine / Clay-Sand Pit — any Building, actually) or a
    /// RESOURCE NODE (ore MineralDeposit / clay-sand-stone pit / tree / rock / forageable). Everything else
    /// — villagers, animals, crop fields, dropped items (all share inputManager.selectedObject) — is a safe no-op.
    ///
    /// Recipe (verified against the decompile):
    ///   • Building → Building.DestroySelf(refund, refundCosts, dropItems, removeWorkers) [331957] — the
    ///     game's own INSTANT demolish (Object.Destroy, no timed job). Mine chain inherits it; ResourceMine
    ///     .OnDestroy auto-cleans the empty surface deposit.
    ///   • Ore MineralDeposit → destroy its MineralSites first (mineralManager.GetMineralSitesForId(id) →
    ///     site.Destroy() + mineralSites.Remove(site)) THEN Object.Destroy the visual GO. ⚠ If the sites
    ///     can't be resolved we ABORT — destroying the GO with live sites still registered would leave
    ///     invisible "ghost" mineable sites. This is a PERMANENT world edit (sites are serialized).
    ///   • Pit + surface node ("node") → Object.Destroy(go): each self-cleans in its own OnDestroy —
    ///     Clay/Sand/StonePitResource unregister from the mineral manager; TreeResource [175115]
    ///     unregisters from resourceManager + terrainManager.RemoveTree + raises ResourceRemovedEvent;
    ///     StoneResource [174153] (rocks/boulders) and ForageableResource [86978] (bushes/berries)
    ///     unregister from resourceManager / foragingManager and cascade to their parent group GO.
    ///     Exactly what the game's own remove button does.
    ///
    /// Reflection-only + fully guarded. Off by default; fires on the delete hotkey or the panel Delete button.
    /// </summary>
    public static class DeleteSelected
    {
        private static bool _resolved, _resolveFailed;
        private static Type? _tBuilding, _tMineralDeposit, _tMineralManager, _tMineralSite, _tClayPit, _tSandPit, _tStonePit;
        private static Type? _tTreeResource, _tStoneResource, _tForageableResource; // surface nodes: trees / rocks / forageables
        private static FieldInfo? _fMdId;
        private static MethodInfo? _mGetSitesForId, _mSiteDestroy;
        private static MemberInfo? _mMineralSitesList;
        private static UnityEngine.Object? _mineralManager;

        public static void OnMapLoaded() { _mineralManager = null; }
        public static void OnSceneExit() { _mineralManager = null; }

        /// <summary>Raw currently-selected GameObject (building OR node OR anything) — before any filter.</summary>
        public static GameObject? RawSelected()
        {
            try { var gm = GameManager.Instance; return gm != null && gm.inputManager != null ? gm.inputManager.selectedObject : null; }
            catch { return null; }
        }

        /// <summary>For the panel: a label for the current selection + whether Delete would act on it.</summary>
        public static bool TryDescribe(out string label, out bool deletable)
        {
            deletable = false;
            var go = RawSelected();
            if (go == null) { label = "(nothing selected)"; return false; }
            Resolve();
            string kind = Classify(go, out _);
            deletable = kind != "none";
            label = deletable ? $"{Name(go)}  [{kind}]" : $"{Name(go)}  [not deletable]";
            return true;
        }

        /// <summary>Delete the current selection. Returns a short status string for the panel.</summary>
        public static string DeleteCurrent()
        {
            var go = RawSelected();
            if (go == null) return "Nothing selected";
            if (!Resolve()) return "Delete API unavailable";
            try
            {
                string kind = Classify(go, out object? comp);
                switch (kind)
                {
                    case "building": return DeleteBuilding(comp!, go);
                    case "ore":      return DeleteOre(comp!, go);
                    // Pits and surface nodes (trees/rocks/forageables) all self-clean in their own
                    // OnDestroy — unregister from the resource/foraging/terrain managers, raise
                    // ResourceRemovedEvent, and cascade to the parent group GO — so a plain Destroy is
                    // exactly what the game's own remove does.
                    case "pit":
                    case "node":     { string n = Name(go); UnityEngine.Object.Destroy(go); return $"Deleted {n}"; }
                    default:         return $"{Name(go)} isn't a deletable building/node";
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Delete failed: {ex.Message}"); return "Delete failed (see log)"; }
        }

        private static string Classify(GameObject go, out object? comp)
        {
            comp = null;
            if (_tBuilding != null       && (comp = go.GetComponent(_tBuilding))       != null) return "building";
            if (_tMineralDeposit != null && (comp = go.GetComponent(_tMineralDeposit)) != null) return "ore";
            if (_tClayPit != null        && (comp = go.GetComponent(_tClayPit))        != null) return "pit";
            if (_tSandPit != null        && (comp = go.GetComponent(_tSandPit))        != null) return "pit";
            if (_tStonePit != null       && (comp = go.GetComponent(_tStonePit))       != null) return "pit";
            // Surface nodes. TreeResource covers FruitTreeResource (orchard trees) too — it's the base.
            if (_tTreeResource != null      && (comp = go.GetComponent(_tTreeResource))      != null) return "node";
            if (_tStoneResource != null     && (comp = go.GetComponent(_tStoneResource))     != null) return "node";
            if (_tForageableResource != null && (comp = go.GetComponent(_tForageableResource)) != null) return "node";
            comp = null;
            return "none";
        }

        private static string DeleteBuilding(object building, GameObject go)
        {
            var m = building.GetType().GetMethod("DestroySelf", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(float), typeof(bool), typeof(bool), typeof(bool) }, null);
            if (m == null) return "DestroySelf not found";
            bool refund = Config.DeleteRefund.Value;
            string n = Name(go);
            // (refundAmount, refundBuildingCosts, dropPublicItemsOnGround, removeWorkers). Tidy vaporize by
            // default (no refund pile / no dropped items); removeWorkers:true so no dangling assignments.
            m.Invoke(building, new object[] { refund ? 1f : 0f, refund, refund, true });
            return $"Deleted {n}";
        }

        private static string DeleteOre(object md, GameObject go)
        {
            // Remove the MineralSites FIRST — destroying the GO while sites are still registered leaves
            // invisible mineable "ghost" sites. If we can't resolve them, ABORT (never orphan).
            if (_mineralManager == null || _fMdId == null || _mGetSitesForId == null)
                return "Ore-delete API missing — aborted (won't orphan sites)";
            int id;
            try { id = (int)_fMdId.GetValue(md)!; } catch { return "Ore id unreadable — aborted"; }
            if (!(_mGetSitesForId.Invoke(_mineralManager, new object[] { id }) is IEnumerable sites))
                return "Ore sites unresolved — aborted";

            var master = MineralSitesList();
            var toRemove = new List<object>();
            foreach (var s in sites) if (s != null) toRemove.Add(s);
            foreach (var s in toRemove)
            {
                try { _mSiteDestroy?.Invoke(s, null); } catch { }
                try { master?.Remove(s); } catch { }
            }
            string n = Name(go);
            UnityEngine.Object.Destroy(go);
            return $"Deleted {n} ore deposit ({toRemove.Count} site(s))";
        }

        private static IList? MineralSitesList()
        {
            try
            {
                return _mMineralSitesList switch
                {
                    PropertyInfo p => p.GetValue(_mineralManager) as IList,
                    FieldInfo f    => f.GetValue(_mineralManager) as IList,
                    _              => null,
                };
            }
            catch { return null; }
        }

        private static string Name(GameObject go)
        {
            try
            {
                if (_tBuilding != null)
                {
                    var b = go.GetComponent(_tBuilding);
                    if (b != null)
                    {
                        var t = b.GetType();
                        var dn = t.GetProperty("displayName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(b) as string
                                 ?? t.GetField("displayName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(b) as string;
                        if (!string.IsNullOrEmpty(dn)) return dn!;
                    }
                }
            }
            catch { }
            return go.name;
        }

        private static bool Resolve()
        {
            if (_resolved) { EnsureMineralManager(); return !_resolveFailed; }
            _resolved = true;
            try
            {
                _tBuilding       = AccessTools.TypeByName("Building");
                _tMineralDeposit = AccessTools.TypeByName("MineralDeposit");
                _tMineralManager = AccessTools.TypeByName("MineralManager");
                _tMineralSite    = AccessTools.TypeByName("MineralSite");
                _tClayPit        = AccessTools.TypeByName("ClayPitResource");
                _tSandPit        = AccessTools.TypeByName("SandPitResource");
                _tStonePit       = AccessTools.TypeByName("StonePitResource");
                _tTreeResource      = AccessTools.TypeByName("TreeResource");       // + FruitTreeResource subclass
                _tStoneResource     = AccessTools.TypeByName("StoneResource");      // surface rocks / boulders
                _tForageableResource = AccessTools.TypeByName("ForageableResource"); // bushes / berries / forage

                if (_tMineralDeposit != null)
                    _fMdId = _tMineralDeposit.GetField("id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_tMineralManager != null)
                {
                    _mGetSitesForId = _tMineralManager.GetMethod("GetMineralSitesForId",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                    _mMineralSitesList = (MemberInfo?)_tMineralManager.GetProperty("mineralSites", BindingFlags.Public | BindingFlags.Instance)
                                         ?? _tMineralManager.GetField("mineralSites",
                                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_tMineralSite != null)
                    _mSiteDestroy = _tMineralSite.GetMethod("Destroy", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                // Building is the hard requirement (the common case). Ore is best-effort — if any of its bits
                // are missing DeleteOre aborts rather than corrupting.
                _resolveFailed = _tBuilding == null;
                if (_resolveFailed) MelonLogger.Warning("[DivineHands] Delete: Building type unresolved — disabled");
                EnsureMineralManager();
                return !_resolveFailed;
            }
            catch (Exception ex) { _resolveFailed = true; MelonLogger.Warning($"[DivineHands] Delete resolve: {ex.Message}"); return false; }
        }

        private static void EnsureMineralManager()
        {
            if (_mineralManager != null || _tMineralManager == null) return;
            try { _mineralManager = UnityEngine.Object.FindObjectOfType(_tMineralManager); } catch { }
        }
    }
}
