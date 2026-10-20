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
        /// <summary>year*12+month of the last gate scan, so monthly mode fires once per month.</summary>
        private static int _lastDumpYM = -1;
        /// <summary>Gate picture at the last scan. Unchanged means monthly mode logs one line.</summary>
        private static string _lastGateSig;
        /// <summary>The factor schema is static data; print it once per load, not per scan.</summary>
        private static bool _schemaDumped;

        /// <summary>Per-day chance (as a PERCENT) marking a villager as genuinely at risk rather
        /// than carrying ordinary background noise. Calibrated against two real saves of one town:
        /// healthy at 451 pop the worst row per villager topped out at 0.0042%/day (a broken-leg
        /// roll); in the collapsed save the affected villager ran 0.4167%/day on Dehydration. 0.01
        /// sits an order of magnitude above the healthy ceiling and far below the real signal.</summary>
        private const float RiskPerDayPct = 0.01f;
        /// <summary>Flagged villagers printed in full. The rest are counted, not listed.</summary>
        private const int DetailVillagers = 8;
        /// <summary>Diseases listed per detailed villager, highest live per-day chance first.</summary>
        private const int TopDiseases = 6;
        /// <summary>Bound on the scan. Each villager costs a factorFunc call per factor per disease
        /// and some of those do a GetComponent, so a very large town is worth capping.</summary>
        private const int MaxScan = 2000;

        /// <summary>New map: allow the dumps to run again.</summary>
        public static void OnMapLoaded() { ResetDumps(); }

        public static void OnSceneExit() { ResetDumps(); }

        private static void ResetDumps()
        {
            _scareDumpDone = false; _diseaseDumpDone = false; _factorDumpDone = false;
            _lastDumpYM = -1; _lastGateSig = null; _schemaDumped = false;
        }

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
                if (!_factorDumpDone)
                {
                    if (DumpDiseaseFactors(allowCompact: false))
                    {
                        _factorDumpDone = true;
                        _lastDumpYM = CurrentYM();
                    }
                }
                else if (Config.DiagDiseaseFactorsMonthly.Value)
                {
                    // Poll the calendar rather than listening for MonthPassedEvent: this module is
                    // already on an update tick and comparing two ints is cheaper than owning an
                    // event subscription that has to be torn down on every scene change.
                    int ym = CurrentYM();
                    if (ym >= 0 && ym != _lastDumpYM)
                    {
                        _lastDumpYM = ym;
                        DumpDiseaseFactors(allowCompact: true);
                    }
                }
            }
            else { _factorDumpDone = false; _lastDumpYM = -1; _lastGateSig = null; }

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
        /// <summary>
        /// One line of water truth per villager. DF_Water and DF_Thirst are the two gates that
        /// opened in the collapse this dump was written for, and they fail for DIFFERENT reasons:
        ///
        ///   DF_Water  → villagerHealth.isNearCleanWater, set by DetermineIfNearWater(). TRUE only
        ///               when the villager's CURRENT position or their RESIDENCE sits inside some
        ///               water-source building's strategicPlanningRadius. Pure geometry:
        ///               IsNearWaterSourceBuilding defaults mustHaveWaterInStorage to FALSE, so a
        ///               FULL well outside the radius does nothing and an EMPTY one inside still
        ///               counts. Shoreline, and a residence's own drinkingWater, set isNearAnyWater
        ///               only — they never count as CLEAN.
        ///   DF_Thirst → needs ALL THREE: residence water count 0, daysWithoutWaterStocked past its
        ///               threshold (ships at 5), and not near ANY water. That one IS a delivery
        ///               failure: residences pull water through stockWaterRequest like any other
        ///               logistics stocking request.
        ///
        /// Printing both, with the nearest well and its radius, separates "no well covers this
        /// house" from "nobody is hauling to this house" at a glance — they need opposite fixes.
        /// </summary>
        private static string WaterLine(ImmuneSystemComponent im, GameManager gm)
        {
            try
            {
                var v = im.gameObject != null ? im.gameObject.GetComponent<Villager>() : null;
                if (v == null) return "";

                var vh = v.villagerHealth;
                string clean = vh != null ? (vh.isNearCleanWater ? "yes" : "NO") : "?";
                string any   = vh != null ? (vh.isNearAnyWater   ? "yes" : "NO") : "?";

                string home = "no residence";
                Vector3 probe = v.gameObject.transform.position;
                var res = v.residence as Residence;
                if (res != null)
                {
                    probe = res.gameObject.transform.position;
                    home = $"H2O {res.GetWaterCount()} (dry {res.DaysWithoutWaterStocked()}d/thr " +
                           $"{res.daysWithoutWaterStockedThreshold}) soap {res.GetSoapCount()}";
                }

                string well = "no wells";
                var sources = gm != null && gm.resourceManager != null ? gm.resourceManager.waterSourcesRO : null;
                if (sources != null && sources.Count > 0)
                {
                    float best = float.MaxValue, bestR = 0f;
                    foreach (var w in sources)
                    {
                        if (w == null) continue;
                        float dd = Vector3.Distance(probe, w.transform.position);
                        if (dd < best) { best = dd; bestR = w.strategicPlanningRadius; }
                    }
                    if (best < float.MaxValue)
                        well = $"nearest well {best:0}m (radius {bestR:0}) {(best <= bestR ? "inside" : "OUTSIDE")}";
                }

                return $"      water: clean={clean} any={any} | {home} | {well}";
            }
            catch { return ""; }
        }

        /// <summary>
        /// Print which villagers have an OPEN DISEASE GATE, and which gate it is.
        ///
        /// Scans every villager rather than sampling, because the failure this was built for was a
        /// POCKET, not a town: in the collapsed save two of three sampled villagers were perfectly
        /// clean and only the third had anything open. A sample answers "is the town sick"; a scan
        /// answers "which houses, and why", which is the question worth asking.
        ///
        /// Why gates rather than rates: chancePerYearMin/Max are only the ENDPOINTS. The live
        /// chance is GetChancePerYearByScore(score) * GetFlavorMultiplierByScore(flavor) / 360,
        /// rolled against every villager every day. `score` is a weighted average over
        /// Disease.factors, and GetFactoredScore returns FALSE outright if any factor marked
        /// isRequired evaluates to ~0. So most diseases in a healthy town are not low - they are
        /// OFF, and nothing happens until a required factor goes non-zero. Those are the gates.
        /// </summary>
        private static bool DumpDiseaseFactors(bool allowCompact)
        {
            try
            {
                var gm = GameManager.Instance;
                var dm = gm != null ? gm.diseaseManager : null;
                // Not-ready is transient (menu, or the first frames of a load), so return quietly
                // and let the caller retry rather than logging a warning every frame.
                if (dm == null) return false;
                if (dm.immuneSystems == null || dm.immuneSystems.Count == 0) return false;

                int scanned = 0;
                var flagged = new List<KeyValuePair<float, ImmuneSystemComponent>>();
                var gateCounts = new Dictionary<string, int>();
                ImmuneSystemComponent reference = null;

                foreach (var im in dm.immuneSystems)
                {
                    if (im == null) continue;
                    if (scanned >= MaxScan) break;
                    scanned++;
                    if (reference == null) reference = im;

                    float worst = 0f;
                    var gatesHere = new HashSet<string>();

                    foreach (var list in new[] { dm.contagiousDiseases?.diseases, dm.personalDiseases?.diseases })
                    {
                        if (list == null) continue;
                        foreach (var d in list)
                        {
                            if (d == null) continue;
                            float perDay;
                            try { perDay = PerDayPct(d, im); } catch { continue; }
                            if (perDay > worst) worst = perDay;
                            if (perDay < RiskPerDayPct || d.factors == null) continue;

                            // Name the REQUIRED factors letting this one through - the gate itself.
                            foreach (var f in d.factors)
                            {
                                if (!f.isRequired || f.diseaseFactor == null || f.diseaseFactor.factorFunc == null) continue;
                                float raw;
                                try { raw = f.diseaseFactor.factorFunc(im); } catch { continue; }
                                if (raw > 0f) gatesHere.Add(FactorLabel(f.diseaseFactor));
                            }
                        }
                    }

                    if (worst >= RiskPerDayPct)
                    {
                        flagged.Add(new KeyValuePair<float, ImmuneSystemComponent>(worst, im));
                        foreach (var g in gatesHere)
                        {
                            int n; gateCounts.TryGetValue(g, out n); gateCounts[g] = n + 1;
                        }
                    }
                }

                // Signature of the gate picture. In monthly mode an unchanged picture logs a single
                // line instead of the whole block, so a long watch stays readable and the moment a
                // gate actually opens stands out rather than scrolling past in a wall of identical
                // dumps. Counts are included, so "same gates, more villagers" still reads as news.
                var gateNames = new List<string>(gateCounts.Keys);
                gateNames.Sort();
                var sigSb = new StringBuilder();
                sigSb.Append(flagged.Count);
                foreach (var g in gateNames)
                {
                    int c; gateCounts.TryGetValue(g, out c);
                    sigSb.Append('|').Append(g).Append(c);
                }
                string sig = sigSb.ToString();
                if (allowCompact && sig == _lastGateSig)
                {
                    MelonLogger.Msg($"[DivineHands] gates {StampYM()}: scanned {scanned}, " +
                                    $"{flagged.Count} open - unchanged | {CensusBrief(dm)}");
                    return true;
                }
                _lastGateSig = sig;

                var sb = new StringBuilder();
                sb.AppendLine($"[DivineHands] ───── DISEASE GATES (live) {StampYM()} ─────");

                AppendRngHealth(sb);
                AppendCensus(sb, dm);

                if (!_schemaDumped)
                {
                    _schemaDumped = true;
                    AppendSchema(sb, dm);
                }

                try
                {
                    var rm = gm.resourceManager;
                    if (rm != null)
                    {
                        int pop = (int)rm.villagerCount, housing = (int)rm.availableHousingCount;
                        sb.AppendLine($"  town: population {pop} / housing {housing}" +
                                      $"   -> DF_PopulationDensity curve input {pop / (float)(housing > 0 ? housing : 1):0.###}");
                    }
                }
                catch { /* HUD context only; never block the dump */ }

                float pct = scanned > 0 ? 100f * flagged.Count / scanned : 0f;
                sb.AppendLine($"  scanned {scanned} villager(s) - {flagged.Count} with an open gate ({pct:0.#}%)" +
                              $"   [threshold {RiskPerDayPct:0.###}%/day]");

                // The single most useful line: which gates are open, and across how many villagers.
                if (gateCounts.Count > 0)
                {
                    var gl = new List<KeyValuePair<string, int>>(gateCounts);
                    gl.Sort((a, b) => b.Value.CompareTo(a.Value));
                    var gsb = new StringBuilder();
                    foreach (var g in gl) gsb.Append($"  {g.Key}x{g.Value}");
                    sb.AppendLine("  OPEN GATES:" + gsb);
                }

                flagged.Sort((a, b) => b.Key.CompareTo(a.Key));

                if (flagged.Count == 0)
                {
                    sb.AppendLine("  Nothing above the threshold - every gate is shut. One villager as reference:");
                    if (reference != null) AppendVillager(sb, reference, gm, dm, 0f);
                }
                else
                {
                    for (int i = 0; i < flagged.Count && i < DetailVillagers; i++)
                        AppendVillager(sb, flagged[i].Value, gm, dm, flagged[i].Key);
                    if (flagged.Count > DetailVillagers)
                        sb.AppendLine($"  ... {flagged.Count - DetailVillagers} more flagged, not listed.");
                }

                sb.AppendLine("  '*' = factor at/above 0.9 of its curve.  REQ = required; 0 there shuts the disease off.");
                MelonLogger.Msg(sb.ToString());
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[DivineHands] Factor dump failed: " + e);
                return true;   // a throwing dump will keep throwing; do not spin on it
            }
        }

        /// <summary>
        /// The live sickness census - who is sick RIGHT NOW, exactly as the in-game panel counts it.
        ///
        /// This is the number that matters and the one the gate scan cannot see. The scan reports
        /// CONTRACTION RISK, but influenza has an empty factor list and chancePerYearMin 0, so its
        /// environmental chance is exactly zero and it never appears in a risk listing - yet a save
        /// was observed with 488 of 488 villagers infected simultaneously. Flu only spreads; it
        /// cannot spawn. So risk and prevalence are different questions, and only the census
        /// answers the second one.
        ///
        /// DiseaseStats.sickCount counts ImmuneSystemComponent.IsSick(), and diseaseInfoMap holds a
        /// per-disease villagerCount - the same figures the health panel shows, so what is logged
        /// here can be compared directly against a screenshot.
        /// </summary>
        private static void AppendCensus(StringBuilder sb, DiseaseManager dm)
        {
            try
            {
                var st = dm.stats;
                if (st == null) return;
                int total = dm.immuneSystems != null ? dm.immuneSystems.Count : 0;
                sb.AppendLine($"  census: {st.sickCount} sick of {total}  ({st.bedriddenCount} bedridden)");

                if (st.diseaseInfoMap == null) return;
                var rows = new List<KeyValuePair<int, string>>();
                foreach (var kv in st.diseaseInfoMap)
                {
                    if (kv.Value == null || kv.Value.villagerCount <= 0) continue;
                    string nm = kv.Key != null ? kv.Key.name : "?";
                    rows.Add(new KeyValuePair<int, string>(kv.Value.villagerCount,
                        $"{nm} {kv.Value.villagerCount}"));
                }
                if (rows.Count == 0) { sb.AppendLine("    (nobody is sick)"); return; }
                rows.Sort((a, b) => b.Key.CompareTo(a.Key));
                var line = new StringBuilder("   ");
                foreach (var r in rows) line.Append("  ").Append(r.Value);
                sb.AppendLine(line.ToString());
            }
            catch (Exception e) { sb.AppendLine("  census failed: " + e.Message); }
        }

        /// <summary>One-line census for the unchanged-month line, so a quiet watch still tracks
        /// prevalence even when no contraction gate has moved.</summary>
        private static string CensusBrief(DiseaseManager dm)
        {
            try
            {
                var st = dm.stats;
                if (st == null) return "census n/a";
                int total = dm.immuneSystems != null ? dm.immuneSystems.Count : 0;
                var rows = new List<KeyValuePair<int, string>>();
                if (st.diseaseInfoMap != null)
                {
                    foreach (var kv in st.diseaseInfoMap)
                    {
                        if (kv.Value == null || kv.Value.villagerCount <= 0) continue;
                        rows.Add(new KeyValuePair<int, string>(kv.Value.villagerCount,
                            $"{(kv.Key != null ? kv.Key.name : "?")} {kv.Value.villagerCount}"));
                    }
                }
                rows.Sort((a, b) => b.Key.CompareTo(a.Key));
                var top = new StringBuilder();
                for (int i = 0; i < rows.Count && i < 4; i++) top.Append(i == 0 ? " " : ", ").Append(rows[i].Value);
                return $"sick {st.sickCount}/{total} (bed {st.bedriddenCount})" +
                       (top.Length > 0 ? " |" + top : "");
            }
            catch { return "census n/a"; }
        }

        /// <summary>
        /// Report whether the GAMEPLAY random generator actually exists.
        ///
        /// This is cheap and worth printing every scan because of how CERandom degrades:
        ///
        ///     public static float Range(Type type, float min, float max)
        ///         => GetGenerator(type)?.Range(min, max) ?? min;
        ///
        /// A MISSING generator does not throw and does not log - it silently returns MIN. Every
        /// probability check in the game is written as `chance >= CERandom.Range(.., 0f, 1f)` or
        /// `CERandom.Range(.., 0, 100) &lt;= pct`, so min means **every roll succeeds**: every
        /// villager contracts every disease whose required factors are open, a fire starts every
        /// month, and every fire spreads. One null field would look exactly like a town that broke
        /// for no reason, with nothing in the log.
        ///
        /// GAMEPLAY is initialized ONLY by CERandom.BeginGeneration, called from the map
        /// generator's GenerateInternal - which does run on load as well as on new maps, so this
        /// should normally read OK. CERandom is a persistent lazy singleton whose Awake seeds only
        /// Type.GLOBAL, so an instance torn down and recreated mid-session would come back with
        /// GAMEPLAY null and stay that way. The game exposes GeneratorExists for exactly this
        /// question; asking it once per scan turns a whole class of "impossible" reports into a
        /// single line of evidence.
        /// </summary>
        private static void AppendRngHealth(StringBuilder sb)
        {
            try
            {
                bool gameplay = CERandom.GeneratorExists(CERandom.Type.GAMEPLAY);
                bool global   = CERandom.GeneratorExists(CERandom.Type.GLOBAL);
                if (gameplay)
                {
                    sb.AppendLine($"  rng: GAMEPLAY ok, GLOBAL {(global ? "ok" : "MISSING")}");
                }
                else
                {
                    sb.AppendLine("  rng: *** GAMEPLAY GENERATOR MISSING - every probability check in " +
                                  "the game is returning its MINIMUM, i.e. every roll succeeds. ***");
                }
            }
            catch (Exception e) { sb.AppendLine("  rng: check failed: " + e.Message); }
        }

        /// <summary>
        /// Print every disease's factor schema once per load: which factors gate it, their weights,
        /// which are REQUIRED, and - for DF_Inventory - the exact ITEM NAMES it counts.
        ///
        /// That last part is the one worth having. DF_Inventory holds a List&lt;string&gt; of item
        /// names, resolves them with `new Item(name)`, and sums ItemStorage.GetItemCount over them,
        /// so a factor named Inventory_HasShoes literally counts "ItemShoes" and nothing else. Any
        /// mod that adds a BETTER replacement garment as a NEW item is invisible here unless it also
        /// patches GetItemCount, and a villager who swapped to the replacement reads as having none
        /// - which opens whatever gate that factor guards. Warmth and the disease gate are separate
        /// paths: patching one does not cover the other. Printing the names makes that checkable
        /// instead of guessable.
        /// </summary>
        private static void AppendSchema(StringBuilder sb, DiseaseManager dm)
        {
            try
            {
                sb.AppendLine("  ── factor schema (static; once per load) ─────────────────────────");
                foreach (var list in new[] { dm.contagiousDiseases?.diseases, dm.personalDiseases?.diseases })
                {
                    if (list == null) continue;
                    foreach (var d in list)
                    {
                        if (d == null || d.factors == null) continue;
                        var fsb = new StringBuilder();
                        foreach (var f in d.factors)
                        {
                            if (f.diseaseFactor == null) continue;
                            fsb.Append($"  {FactorLabel(f.diseaseFactor)}(w{f.weight}{(f.isRequired ? ",REQ" : "")}");
                            var inv = f.diseaseFactor as DF_Inventory;
                            if (inv != null && inv.items != null && inv.items.Count > 0)
                            {
                                fsb.Append(")[");
                                for (int i = 0; i < inv.items.Count; i++)
                                {
                                    if (i > 0) fsb.Append(',');
                                    fsb.Append(inv.items[i]);
                                }
                                fsb.Append(']');
                            }
                            else fsb.Append(')');
                        }
                        sb.AppendLine($"    {d.name,-22} min {d.chancePerYearMin:0.#####} max {d.chancePerYearMax:0.#####}" +
                                      $" flav {d.minFlavorMultiplier:0.##}-{d.maxFlavorMultiplier:0.##} |{fsb}");
                    }
                }
                sb.AppendLine("  ──────────────────────────────────────────────────────────────────");
            }
            catch (Exception e) { sb.AppendLine("    schema failed: " + e.Message); }
        }

        /// <summary>year*12+month, or -1 when there is no clock yet. Monotonic, so it survives a
        /// year rollover that a bare month number would not.</summary>
        private static int CurrentYM()
        {
            try
            {
                var tm = GameManager.Instance != null ? GameManager.Instance.timeManager : null;
                if (tm == null) return -1;
                var d = tm.currentDate;
                return d.year * 12 + d.month;
            }
            catch { return -1; }
        }

        /// <summary>Calendar stamp for a dump header, e.g. "Y12 M7".</summary>
        private static string StampYM()
        {
            try
            {
                var tm = GameManager.Instance != null ? GameManager.Instance.timeManager : null;
                if (tm == null) return "Y? M?";
                var d = tm.currentDate;
                return $"Y{d.year} M{d.month}";
            }
            catch { return "Y? M?"; }
        }

        /// <summary>
        /// The live per-day contraction chance as a PERCENT, exactly as the game rolls it - and 0
        /// when the disease cannot roll at all.
        ///
        /// That second half matters. RollDiseaseByDuration only rolls when GetFactoredScore returns
        /// TRUE, and it returns FALSE the moment any required factor is ~0. But GetControllableScore
        /// reports 0 in that same case, and GetChancePerYearByScore(0) is the MINIMUM endpoint, not
        /// zero - so a disease that is switched off entirely was printing a small non-zero rate and
        /// could trip the risk threshold on its own. Check the gates directly instead.
        /// </summary>
        private static float PerDayPct(Disease d, ImmuneSystemComponent im)
        {
            if (IsGated(d, im)) return 0f;
            float score = d.GetControllableScore(im);
            float flav  = d.GetFlavorMultiplierByScore(d.GetFlavorScore(im));
            return d.GetChancePerYearByScore(score) * flav / 360f * 100f;
        }

        /// <summary>True when some REQUIRED factor sits at ~0, which switches the disease off
        /// outright no matter what every other factor says.</summary>
        private static bool IsGated(Disease d, ImmuneSystemComponent im)
        {
            if (d.factors == null) return false;
            foreach (var f in d.factors)
            {
                if (!f.isRequired || f.diseaseFactor == null || f.diseaseFactor.factorFunc == null) continue;
                float raw;
                try { raw = f.diseaseFactor.factorFunc(im); } catch { continue; }
                if (Mathf.Approximately(raw, 0f)) return true;
            }
            return false;
        }

        /// <summary>A disease lists several assets of the SAME factor type tuned differently -
        /// Typhoid carries two DF_ResidenceStocked entries at different weights, one required - so
        /// always prefer the ASSET name, which is what distinguishes them.</summary>
        private static string FactorLabel(DiseaseFactor f)
        {
            string name = f.name;
            if (string.IsNullOrEmpty(name)) name = f.GetType().Name;
            return name.StartsWith("DF_") ? name.Substring(3) : name;
        }

        /// <summary>One villager block: header, the water readout, and its worst diseases.</summary>
        private static void AppendVillager(StringBuilder sb, ImmuneSystemComponent im,
                                           GameManager gm, DiseaseManager dm, float worst)
        {
            string who = im.gameObject != null ? im.gameObject.name : "?";
            sb.AppendLine(worst > 0f ? $"  -- {who}  [worst {worst:0.####}%/day]" : $"  -- {who}");

            string water = WaterLine(im, gm);
            if (water.Length > 0) sb.AppendLine(water);

            var rows = new List<KeyValuePair<float, string>>();
            foreach (var list in new[] { dm.contagiousDiseases?.diseases, dm.personalDiseases?.diseases })
            {
                if (list == null) continue;
                foreach (var d in list)
                {
                    if (d == null) continue;
                    float score, flav, perDay; bool gated;
                    try
                    {
                        gated  = IsGated(d, im);
                        score  = d.GetControllableScore(im);
                        flav   = d.GetFlavorMultiplierByScore(d.GetFlavorScore(im));
                        perDay = gated ? 0f : d.GetChancePerYearByScore(score) * flav / 360f * 100f;
                    }
                    catch { continue; }

                    var fsb = new StringBuilder();
                    if (d.factors != null)
                    {
                        var seen = new Dictionary<string, int>();
                        foreach (var f in d.factors)
                        {
                            if (f.diseaseFactor == null || f.diseaseFactor.factorFunc == null) continue;
                            float raw;
                            try { raw = f.diseaseFactor.factorFunc(im); } catch { continue; }

                            string name = FactorLabel(f.diseaseFactor);
                            int n;
                            if (seen.TryGetValue(name, out n)) { seen[name] = ++n; name += "#" + n; }
                            else seen[name] = 1;

                            fsb.Append($" {(raw >= 0.9f ? "*" : " ")}{name}={raw:0.##}x{f.weight}{(f.isRequired ? "REQ" : "")}");
                        }
                    }

                    rows.Add(new KeyValuePair<float, string>(perDay,
                        $"      {d.name,-22} score {score:0.###}  xflav {flav:0.##}  -> " +
                        $"{(gated ? "GATED OFF" : $"{perDay:0.####}%/day")}{fsb}"));
                }
            }
            rows.Sort((a, b) => b.Key.CompareTo(a.Key));
            for (int i = 0; i < rows.Count && i < TopDiseases; i++) sb.AppendLine(rows[i].Value);
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
