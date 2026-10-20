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
    ///
    ///   • DISEASE RATES — the LIVE contraction and spread numbers on every Disease asset, next to
    ///     the multiplier bookkeeping that modifies them. Worth having because those numbers are
    ///     mutable at runtime and nothing in game surfaces them.
    ///
    ///     GE_DiseaseChanceAndSpreadModify writes straight into the shared Disease ScriptableObjects
    ///     and keeps the pre-modification values in TechTreeManager.geDiseaseChanceAndSpreadMultipliers
    ///     so it can undo itself. It applies `orig - orig * multiplier`, clamped to 0..1 — so a
    ///     multiplier that ends up NEGATIVE does not reduce disease, it multiplies it UP, potentially
    ///     to a flat 100%. This dump makes that visible: compare `live` against `orig`, and read the
    ///     multiplier's sign.
    /// </summary>
    internal static class Diagnostics
    {
        private static bool _scareDumpDone;
        private static bool _diseaseDumpDone;
        private static bool _factorDumpDone;

        /// <summary>Villagers sampled by the factor dump. The town-wide factors read the same for
        /// everyone, so a handful separates shared inputs from personal ones without spamming.</summary>
        private const int SampleVillagers = 3;
        /// <summary>Diseases listed per villager, highest live per-day chance first.</summary>
        private const int TopDiseases = 6;

        /// <summary>New map: allow the dumps to run again.</summary>
        public static void OnMapLoaded() { _scareDumpDone = false; _diseaseDumpDone = false; _factorDumpDone = false; }

        public static void OnSceneExit() { _scareDumpDone = false; _diseaseDumpDone = false; _factorDumpDone = false; }

        public static void OnUpdate()
        {
            // Run-once-per-enable: turning the pref off re-arms it, so the user can re-run a dump
            // on the current map without reloading.
            if (Config.DiagDiseaseRates.Value)
            {
                if (!_diseaseDumpDone) { _diseaseDumpDone = true; DumpDiseaseRates(); }
            }
            else _diseaseDumpDone = false;

            if (Config.DiagDiseaseFactors.Value)
            {
                // Latch only on a dump that actually produced output. OnMapLoaded re-arms this so
                // the dump fires by itself on every save load - but for the first frames after a
                // load the DiseaseManager has no immune systems registered yet, and latching on
                // that empty pass would silently skip the save just loaded. Retry until it lands.
                if (!_factorDumpDone && DumpDiseaseFactors()) _factorDumpDone = true;
            }
            else _factorDumpDone = false;

            if (!Config.DiagBuildingScare.Value) { _scareDumpDone = false; return; }
            if (_scareDumpDone) return;
            _scareDumpDone = true;
            DumpAnimalScareTable();
        }

        /// <summary>Log every building prefab's <c>scaresAnimals</c> flag, grouped so the exempt ones
        /// (the interesting answer) come first.</summary>
        /// <summary>
        /// Print every disease's live contraction and spread rates beside the stored originals.
        ///
        /// Shipped values for reference (read from resources.assets, 2026-10): influenza is one of
        /// the WEAKEST diseases in the game at 0.2% per year and 0.8% spread per day; smallpox is
        /// 0.35%/6%; bubonic plague 8%/6%. If a live number is orders above its shipped value, or
        /// sitting at a flat 1.0, something has inflated it and the multiplier below will show a
        /// negative sign.
        /// </summary>
        /// <summary>
        /// Print the per-villager FACTOR BREAKDOWN behind every disease's contraction chance.
        ///
        /// DumpDiseaseRates prints chancePerYearMin/Max - the ENDPOINTS. Those can read perfectly
        /// normal while the town is being flattened, because the live chance is lerped BETWEEN the
        /// endpoints by a score, then scaled by a second score:
        ///
        ///     perDay = GetChancePerYearByScore(score) * GetFlavorMultiplierByScore(flavorScore) / 360
        ///
        /// DiseaseManager.OnDayPassed rolls EVERY active disease against EVERY villager once per day,
        /// passing a 1-day CETimeSpan to RollDiseaseByDuration, which scales by totalDays / 360f.
        ///
        /// `score` is a WEIGHTED AVERAGE over Disease.factors. Each FactorInfo wraps a DiseaseFactor
        /// ScriptableObject whose factorFunc reads live state - DF_Population and
        /// DF_PopulationDensity (villagerCount / availableHousingCount) are town-wide and identical
        /// for everyone, while DF_Diet, DF_Water, DF_RodentPopulation, DF_ResidenceStocked and the
        /// rest are per villager. A shared factor pinned near the top of its curve raises every
        /// villager's chance on the same day, which is what a "whole town sick at once" event looks
        /// like from the inside - and the endpoint dump shows nothing wrong at all.
        ///
        /// isRequired matters: GetFactoredScore returns FALSE when any required factor is ~0, which
        /// removes the disease's chance entirely. Those are marked REQ; a 0 there means that disease
        /// cannot spawn for that villager regardless of everything else.
        /// </summary>
        private static bool DumpDiseaseFactors()
        {
            try
            {
                var gm = GameManager.Instance;
                var dm = gm != null ? gm.diseaseManager : null;
                // Not-ready is transient (menu, or the first frames of a load), so return quietly
                // and let the caller retry rather than logging a warning every frame.
                if (dm == null) return false;
                if (dm.immuneSystems == null || dm.immuneSystems.Count == 0) return false;

                var sb = new StringBuilder();
                sb.AppendLine("[DivineHands] ───── DISEASE FACTORS (live, per villager) ─────");

                // Print the density input explicitly: it is the one factor whose value you can
                // reason about from the HUD, so it is the fastest thing to confirm or rule out.
                try
                {
                    var rm = gm.resourceManager;
                    if (rm != null)
                    {
                        int pop = (int)rm.villagerCount;
                        int housing = (int)rm.availableHousingCount;
                        float ratio = pop / (float)(housing > 0 ? housing : 1);
                        sb.AppendLine($"  town: population {pop} / housing {housing}" +
                                      $"   → DF_PopulationDensity curve input {ratio:0.###}");
                    }
                }
                catch { /* HUD context only; never block the dump */ }

                int sampled = 0;
                foreach (var im in dm.immuneSystems)
                {
                    if (im == null) continue;
                    if (sampled >= SampleVillagers) break;
                    sampled++;

                    string who = im.gameObject != null ? im.gameObject.name : "?";
                    sb.AppendLine($"  ── villager {sampled}: {who}");

                    var rows = new List<KeyValuePair<float, string>>();

                    foreach (var list in new[] { dm.contagiousDiseases?.diseases, dm.personalDiseases?.diseases })
                    {
                        if (list == null) continue;
                        foreach (var d in list)
                        {
                            if (d == null) continue;

                            float score, flavMult, perDay;
                            try
                            {
                                score    = d.GetControllableScore(im);
                                flavMult = d.GetFlavorMultiplierByScore(d.GetFlavorScore(im));
                                perDay   = d.GetChancePerYearByScore(score) * flavMult / 360f;
                            }
                            catch { continue; }

                            var fsb = new StringBuilder();
                            if (d.factors != null)
                            {
                                // A disease can list SEVERAL assets of the SAME factor type, tuned
                                // differently - Typhoid carries two DF_ResidenceStocked entries at
                                // different weights, one of them required. Printing the type name
                                // for both made the gate unreadable, so prefer the ASSET name and
                                // only fall back to the type, disambiguating any repeat with #n.
                                var seen = new Dictionary<string, int>();
                                foreach (var f in d.factors)
                                {
                                    if (f.diseaseFactor == null || f.diseaseFactor.factorFunc == null) continue;
                                    float raw;
                                    try { raw = f.diseaseFactor.factorFunc(im); } catch { continue; }

                                    string name = f.diseaseFactor.name;
                                    if (string.IsNullOrEmpty(name)) name = f.diseaseFactor.GetType().Name;
                                    if (name.StartsWith("DF_")) name = name.Substring(3);
                                    int n;
                                    if (seen.TryGetValue(name, out n)) { seen[name] = ++n; name += "#" + n; }
                                    else seen[name] = 1;
                                    // '*' = this factor is at the top of its curve. That is the whole
                                    // point of the dump: it names the input doing the damage.
                                    string pin = raw >= 0.9f ? "*" : " ";
                                    fsb.Append($" {pin}{name}={raw:0.##}x{f.weight}{(f.isRequired ? "REQ" : "")}");
                                }
                            }

                            rows.Add(new KeyValuePair<float, string>(perDay,
                                $"      {d.name,-22} score {score:0.###}  ×flav {flavMult:0.##}" +
                                $"  → {perDay * 100f:0.####}%/day{fsb}"));
                        }
                    }

                    rows.Sort((a, b) => b.Key.CompareTo(a.Key));
                    for (int i = 0; i < rows.Count && i < TopDiseases; i++) sb.AppendLine(rows[i].Value);
                }

                sb.AppendLine("  '*' = factor at/above 0.9 of its curve (the one raising the score).");
                sb.AppendLine("  REQ = required factor; 0 there blocks that disease outright.");
                sb.AppendLine("  %/day is PER VILLAGER - multiply by population for the town's daily case rate.");
                MelonLogger.Msg(sb.ToString());
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[DivineHands] Factor dump failed: " + e);
                return true;   // a throwing dump will keep throwing; do not spin on it
            }
        }

        private static void DumpDiseaseRates()
        {
            try
            {
                var gm = GameManager.Instance;
                var dm = gm != null ? gm.diseaseManager : null;
                var ttm = gm != null ? gm.techTreeManager : null;
                if (dm == null) { MelonLogger.Warning("[DivineHands] Disease dump: no DiseaseManager."); return; }

                var mults = ttm != null ? ttm.geDiseaseChanceAndSpreadMultipliers : null;

                var sb = new StringBuilder();
                sb.AppendLine("[DivineHands] ───── DISEASE RATES (live) ─────");
                sb.AppendLine($"  {"asset",-24} {"live min",10} {"live max",10} {"spread/day",11}   multiplier bookkeeping");

                int flagged = 0;
                foreach (var list in new[] { dm.contagiousDiseases?.diseases, dm.personalDiseases?.diseases })
                {
                    if (list == null) continue;
                    foreach (var d in list)
                    {
                        if (d == null) continue;
                        var cd = d as ContagiousDisease;
                        string spread = cd != null ? cd.spreadRatePerDay.ToString("0.#####") : "-";

                        string book = "(none)";
                        bool suspect = false;
                        if (mults != null && mults.TryGetValue(d.name, out var v))
                        {
                            book = $"mult {v.multiplier:+0.###;-0.###;0} | orig {v.origChancePerYearMin:0.#####}/" +
                                   $"{v.origChancePerYearMax:0.#####} spread {v.origSpreadRatePerDay:0.#####}";
                            // A negative multiplier is the inversion case: orig - orig*(-m) > orig.
                            if (v.multiplier < 0f) suspect = true;
                        }
                        // Do NOT flag on an absolute 1.0: several diseases legitimately ship at
                        // that value (Dehydration and Frostbite are condition-gated, not random).
                        // Only a rate that exceeds its OWN recorded original is evidence of
                        // inflation, and that only exists once the multiplier bookkeeping has an
                        // entry. Flagging 1.0 outright produced false positives on first run.
                        if (mults != null && mults.TryGetValue(d.name, out var chk))
                        {
                            if (d.chancePerYearMax > chk.origChancePerYearMax + 1e-6f) suspect = true;
                            if (cd != null && cd.spreadRatePerDay > chk.origSpreadRatePerDay + 1e-6f) suspect = true;
                        }

                        sb.AppendLine($"  {(suspect ? "!" : " ")}{d.name,-23} {d.chancePerYearMin,10:0.#####} " +
                                      $"{d.chancePerYearMax,10:0.#####} {spread,11}   {book}");
                        if (suspect) flagged++;
                    }
                }

                sb.AppendLine(flagged > 0
                    ? $"  ⚠ {flagged} entr(ies) marked '!': a negative multiplier, or a live rate above its own recorded original."
                    : "  No inflation: every multiplier is positive and no live rate exceeds its original.");
                sb.Append("[DivineHands] ───── end disease rates ─────");
                MelonLogger.Msg(sb.ToString());
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DivineHands] Disease dump failed: " + ex.Message);
            }
        }

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
