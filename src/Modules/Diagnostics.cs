using System;
using System.Collections.Generic;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace DivineHands.Modules
{
    /// <summary>
    /// Opt-in reference dumps about the LIVE game data — for modding/wiki research, not gameplay.
    /// Everything here is read-only, runs only while its pref is on, and writes to the MelonLoader log.
    ///
    /// Each dump runs ONCE per enable: flip the pref on to produce it, and it won't repeat (or spam a
    /// frame loop). Toggle off and on again to re-run on the current map.
    ///
    /// Current dumps:
    ///   • ANIMAL-SCARE TABLE — which buildings scare wildlife away. FF divides the map into 64 m
    ///     spawn-area cells; 3+ buildings whose <c>Building.scaresAnimals</c> is true inside one cell
    ///     evict that cell's herd (AnimalManager.OnBuildingChange → RemoveScaredAnimals). scaresAnimals
    ///     is a per-PREFAB inspector flag (public field, default true, never assigned in code), so the
    ///     only way to know the real values is to read the loaded prefabs — which is what this does.
    ///     Crop fields are a separate path (+100 per field → always evicts) and aren't in this table.
    /// </summary>
    internal static class Diagnostics
    {
        private static bool _scareDumpDone;

        /// <summary>New map: allow the dumps to run again.</summary>
        public static void OnMapLoaded() => _scareDumpDone = false;

        public static void OnSceneExit() => _scareDumpDone = false;

        public static void OnUpdate()
        {
            // Run-once-per-enable: turning the pref off re-arms it, so the user can re-run a dump
            // on the current map without reloading.
            if (!Config.DiagBuildingScare.Value) { _scareDumpDone = false; return; }
            if (_scareDumpDone) return;
            _scareDumpDone = true;
            DumpAnimalScareTable();
        }

        /// <summary>Log every building prefab's <c>scaresAnimals</c> flag, grouped so the exempt ones
        /// (the interesting answer) come first.</summary>
        private static void DumpAnimalScareTable()
        {
            try
            {
                var setup = GlobalAssets.buildingSetupData;
                var all = setup != null ? setup.buildingData : null;
                if (all == null)
                {
                    MelonLogger.Warning("[DivineHands] Scare table: buildingSetupData unavailable (load a map first).");
                    return;
                }

                // identifier -> scares. One entry per building; prefab tiers of the same building share
                // the flag in practice, but disagreements are surfaced rather than hidden.
                var exempt = new List<string>();
                var scares = new List<string>();
                var mixed = new List<string>();
                int noBuilding = 0;

                foreach (var bd in all)
                {
                    if (bd == null || string.IsNullOrEmpty(bd.identifier)) continue;
                    bool sawTrue = false, sawFalse = false, sawAny = false;

                    if (bd.prefabEntries != null)
                        foreach (var pe in bd.prefabEntries)
                        {
                            var prefab = pe?.PREFAB();
                            var b = prefab != null ? prefab.GetComponent<Building>() : null;
                            if (b == null) continue;
                            sawAny = true;
                            if (b.scaresAnimals) sawTrue = true; else sawFalse = true;
                        }

                    if (!sawAny) { noBuilding++; continue; }
                    if (sawTrue && sawFalse) mixed.Add(bd.identifier);
                    else if (sawFalse) exempt.Add(bd.identifier);
                    else scares.Add(bd.identifier);
                }

                exempt.Sort(StringComparer.OrdinalIgnoreCase);
                scares.Sort(StringComparer.OrdinalIgnoreCase);
                mixed.Sort(StringComparer.OrdinalIgnoreCase);

                MelonLogger.Msg("[DivineHands] ===== ANIMAL-SCARE TABLE =====");
                MelonLogger.Msg($"[DivineHands] 3+ scaring buildings in one 64m spawn cell evicts that cell's herd. " +
                                $"({scares.Count} scare, {exempt.Count} exempt, {mixed.Count} mixed, {noBuilding} no Building component)");

                LogList("DO NOT scare animals", exempt);
                LogList("MIXED across tiers (check per tier)", mixed);
                LogList("DO scare animals", scares);
                MelonLogger.Msg("[DivineHands] ===== END SCARE TABLE ===== (turn the setting off, then on, to re-run)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] Scare table dump failed: {ex.Message}");
            }
        }

        // Wrap long lists so no single log line is unreadably wide.
        private static void LogList(string title, List<string> names)
        {
            MelonLogger.Msg($"[DivineHands] --- {title} ({names.Count}) ---");
            if (names.Count == 0) { MelonLogger.Msg("[DivineHands]   (none)"); return; }

            var sb = new StringBuilder("[DivineHands]   ");
            foreach (var n in names)
            {
                if (sb.Length + n.Length + 2 > 160) { MelonLogger.Msg(sb.ToString()); sb.Length = 0; sb.Append("[DivineHands]   "); }
                if (sb.Length > 18) sb.Append(", ");
                sb.Append(n);
            }
            if (sb.Length > 18) MelonLogger.Msg(sb.ToString());
        }
    }
}
