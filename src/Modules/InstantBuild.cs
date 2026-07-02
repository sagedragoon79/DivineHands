using System;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;

namespace DivineHands.Modules
{
    /// <summary>
    /// Instant Build god-power. While active, every build site completes on the next frame — new
    /// placements via a postfix on <c>BuildSiteResource.Initialize</c> [147324], plus a one-time sweep
    /// of already-pending sites when the toggle turns on.
    ///
    /// Completion path (verified): <c>BuildSiteResource.CompleteBuilding()</c> [147674] — the same
    /// method vanilla's own free-buildings cheat (<c>CheatManager.waiveMinWorkForFreeBuildings</c>,
    /// used @147333) calls via <c>CompleteBuildingNextFrame()</c>. It instantiates the finished
    /// prefab, destroys the build site, and raises <c>BuildSiteCompleteEvent</c> — full registration,
    /// nothing bypassed. Like vanilla, we defer one frame (never complete inside Initialize — the
    /// placement flow still references the site).
    ///
    /// Materials option: <c>CompleteBuilding()</c> itself never deducts anything, so a raw call is a
    /// FREE building. With "Use materials" on we charge the town up-front, mirroring
    /// <c>ExpenseManager.DeductExpense</c> [151383]: affordability via summed
    /// <c>storage.GetNumberOfUnreservedItems(item)</c> over the six storage-building collections
    /// (treasuries / storehouses / stockyards / supply wagons / trading posts / foundries), then
    /// <c>RemoveUnreservedItemsClamped(item, n, instigator)</c> [163575] per building. The labor
    /// pseudo-item (<c>workBucketManager.neededItemWork</c>) is skipped — it's hammer-swings, not a
    /// material. If the town can't afford a site it is left to construct normally (status says so).
    ///
    /// Caveat: a site swept mid-construction already consumed its delivered materials, so charging the
    /// full list double-pays the delivered part. Fine for a god mod; new placements (the normal case)
    /// are charged exactly once.
    /// </summary>
    public static class InstantBuild
    {
        /// <summary>Runtime live ON/OFF — toggled in the panel, NOT a saved pref. Reset on every
        /// map load / scene exit like Build Anywhere (a loaded save never starts insta-building
        /// unannounced).</summary>
        public static bool Active;

        /// <summary>Last action's outcome for the panel status line.</summary>
        public static string Status = "";

        // Sites queued for next-frame completion (mirrors vanilla's CompleteBuildingNextFrame delay).
        private static readonly List<BuildSiteResource> _pending = new List<BuildSiteResource>();
        private static bool _wasActive;

        public static void OnMapLoaded()  { _pending.Clear(); _wasActive = false; Status = ""; }
        public static void OnSceneExit()  { _pending.Clear(); _wasActive = false; Status = ""; }
        public static void ResetActive()  => Active = false;

        private static bool Armed =>
            Active && Config.MasterEnable.Value && Config.InstantBuildEnable.Value && Plugin.InGame;

        /// <summary>Called by the Harmony postfix when a new build site finishes Initialize.</summary>
        public static void OnBuildSitePlaced(BuildSiteResource site)
        {
            if (!Armed || site == null) return;
            _pending.Add(site);
        }

        public static void OnUpdate()
        {
            // Drop a stale arm if the feature was disabled in config since it was toggled.
            if (Active && !Config.InstantBuildEnable.Value) Active = false;

            // Rising edge: sweep sites that were already pending before the toggle, so "turn it on"
            // finishes the whole queue, not just future placements.
            bool armed = Armed;
            if (armed && !_wasActive) SweepExisting();
            _wasActive = armed;

            if (_pending.Count == 0) return;
            if (!armed) { _pending.Clear(); return; } // disarmed with sites queued — let them build normally

            // Complete everything queued last frame. Snapshot + clear first: CompleteBuilding can
            // spawn follow-up sites (relocations etc.) whose Initialize postfix re-queues into _pending.
            var batch = _pending.ToArray();
            _pending.Clear();
            int done = 0, skipped = 0;
            foreach (var site in batch)
            {
                try
                {
                    if (site == null || site.buildSite == null || site.buildSite.isComplete) continue;
                    if (Config.InstantBuildUseMaterials.Value && !TryChargeMaterials(site)) { skipped++; continue; }
                    site.CompleteBuilding();
                    done++;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[DivineHands] Instant build failed: {ex.Message}");
                }
            }
            if (done > 0 || skipped > 0)
                Status = skipped > 0
                    ? $"Built {done}, skipped {skipped} (can't afford — building normally)"
                    : $"Built {done} instantly";
        }

        private static void SweepExisting()
        {
            try
            {
                var sites = UnityEngine.Object.FindObjectsOfType<BuildSiteResource>();
                int queued = 0;
                foreach (var s in sites)
                {
                    if (s == null || s.buildSite == null || s.buildSite.isComplete) continue;
                    _pending.Add(s);
                    queued++;
                }
                if (queued > 0 && Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] Instant build: queued {queued} pending site(s)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] Instant build sweep failed: {ex.Message}");
            }
        }

        // ==================== town-wide material charge ====================

        /// <summary>Charge the site's required materials from town storage (labor pseudo-item
        /// excluded). All-or-nothing: false (and no deduction) if any material is short.</summary>
        private static bool TryChargeMaterials(BuildSiteResource site)
        {
            var gm = GameManager.Instance;
            var rm = gm != null ? gm.resourceManager : null;
            // ConstructionData is a struct in the live build — no null test, just read the dictionary.
            var mats = site.buildSite != null ? site.buildSite.constructionData.materialsRequired : null;
            if (rm == null) return false;
            if (mats == null || mats.Count == 0) return true; // nothing to charge

            Item? workItem = null;
            try { workItem = gm!.workBucketManager != null ? gm.workBucketManager.neededItemWork : null; } catch { }

            // Pass 1: affordability across all storage buildings.
            foreach (var kv in mats)
            {
                if (kv.Key == null || kv.Value <= 0 || kv.Key == workItem) continue;
                uint available = 0;
                ForEachStorageBuilding(rm, b =>
                {
                    if (b != null && b.storage != null)
                        available += b.storage.GetNumberOfUnreservedItems(kv.Key);
                });
                if (available < (uint)kv.Value) return false;
            }

            // Pass 2: deduct (same iteration the game's DeductExpense uses).
            foreach (var kv in mats)
            {
                if (kv.Key == null || kv.Value <= 0 || kv.Key == workItem) continue;
                uint remaining = (uint)kv.Value;
                ForEachStorageBuilding(rm, b =>
                {
                    if (remaining == 0 || b == null || b.storage == null) return;
                    uint have = b.storage.GetNumberOfUnreservedItems(kv.Key);
                    if (have == 0) return;
                    uint take = Math.Min(remaining, have);
                    var bundle = b.storage.RemoveUnreservedItemsClamped(kv.Key, take, site.gameObject);
                    remaining -= bundle != null ? Math.Min(bundle.numberOfItems, take) : 0;
                });
            }
            return true;
        }

        private static void ForEachStorageBuilding(ResourceManager rm, Action<Building> visit)
        {
            // The six collections ExpenseManager.CalculateAvailableTender walks [151521].
            if (rm.treasuriesRO != null)   foreach (var b in rm.treasuriesRO)   visit(b);
            if (rm.storehousesRO != null)  foreach (var b in rm.storehousesRO)  visit(b);
            if (rm.stockyardsRO != null)   foreach (var b in rm.stockyardsRO)   visit(b);
            if (rm.supplyWagonsRO != null) foreach (var b in rm.supplyWagonsRO) visit(b);
            if (rm.tradingPostsRO != null) foreach (var b in rm.tradingPostsRO) visit(b);
            if (rm.foundriesRO != null)    foreach (var b in rm.foundriesRO)    visit(b);
        }
    }
}
