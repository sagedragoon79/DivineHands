using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace DivineHands.Modules
{
    /// <summary>
    /// PROGRESSION god tools: unlock every building, grant tech points, or unlock the whole tech tree.
    ///
    /// ── All Buildings ───────────────────────────────────────────────────────────────────────────
    /// BuildManager keeps every building's prerequisites (techs as "Tech_###", other buildings, tiers)
    /// in a private buildingNameToPrereqsDict of Prerequisite objects [145375, 145898]. Setting
    /// Prerequisite.met = true clears that bit in its PrerequisiteGroup32, and when a group's last bit
    /// clears it fires uiButtonMan.OnBuildingPrerequisitesMet — the same callback a real unlock fires
    /// [145942, SetPrerequisiteMet]. So forcing every entry met unlocks every build button through the
    /// game's own path.
    ///
    /// Deliberately NOT SettingsManager.cstmEnableFreeBuildings: that flag is FF's sandbox "Free
    /// Buildings" mode. Besides skipping prerequisites it changes build costs, waives construction work,
    /// locks auto-upgrades, disables achievements, and is written into the save's metadata.
    ///
    /// Prerequisite state is rebuilt from scratch on every map load and is never saved, so this writes
    /// nothing into the save. Vanilla can re-lock entries mid-game (demolishing your only X re-locks what
    /// depends on X), so while active we re-check every couple of seconds and re-meet anything unmet.
    /// SetPrerequisiteMet only fires its callback on a transition, so re-meeting is cheap and quiet.
    /// Turning it off re-locks on the next load (true prerequisite state can't be reconstructed live).
    ///
    /// ── Tech points ─────────────────────────────────────────────────────────────────────────────
    /// TechTreeManager.AddKnowledgePoints [212376] — public, and self-capped at the points still needed
    /// to finish the tree, so a grant can never overshoot. The player spends them in the normal tech tree.
    ///
    /// ── Unlock All Tech ─────────────────────────────────────────────────────────────────────────
    /// FF's tech tree commits in TWO steps. Clicking a node calls ActivateTechOrRank(id, cost, false)
    /// [212679]: rank +1, points spent, Researched event. The game effects and building unlocks only
    /// happen on CONFIRM, in UITechTreeOverview.UndoOrConfirmCachedTechTreeNodeInfo [294300]:
    /// ActivateEffect(stackIfActive) per researched rank, then BuildManager.ActivateTech(id), then a
    /// TechActivated event. We do both steps per node at zero cost. Afterwards we rebuild the overview's
    /// pending-change cache: it counts Researched events so Confirm can apply them later, and an open
    /// tech tree window would otherwise offer to Confirm (re-applying every effect) or Undo (refunding
    /// points) for our unlocks. On load, ActivateTechOrRank(onLoad: true) re-applies saved ranks, so the
    /// result persists like any normal research. It is permanent in the save.
    /// </summary>
    internal static class Progression
    {
        /// <summary>One-line result of the last action, shown in the panel.</summary>
        public static string Status = "";

        // ---- All Buildings ----
        private static FieldInfo? _fPrereqDict;
        private static IDictionary? _prereqDict;   // per-map BuildManager instance's dictionary
        private static float _nextReapply;
        private const float ReapplySeconds = 2f;
        private static bool _wasAllBuildings;

        // ---- Unlock All Tech confirm ----
        private static float _confirmUntil = -1f;

        public static void OnMapLoaded()
        {
            _prereqDict = null;
            _nextReapply = 0f;
            _wasAllBuildings = false;
            _confirmUntil = -1f;
            _treeDoneCheckAt = 0f;
            Status = "";
        }

        public static void OnSceneExit() => OnMapLoaded();

        public static void OnUpdate()
        {
            bool want = Config.MasterEnable.Value && Config.EnableProgression.Value && Config.UnlockAllBuildings.Value;
            if (!want)
            {
                if (_wasAllBuildings) Status = "All Buildings off — buildings re-lock on the next load.";
                _wasAllBuildings = false;
                return;
            }
            if (Time.unscaledTime < _nextReapply) return;
            _nextReapply = Time.unscaledTime + ReapplySeconds;

            int met = ForceAllPrerequisitesMet();
            if (met < 0) return;                       // BuildManager not ready yet — retry
            if (!_wasAllBuildings || met > 0)
                Status = met > 0 ? $"All Buildings on — unlocked {met} prerequisite(s)." : "All Buildings on.";
            _wasAllBuildings = true;
        }

        /// <summary>Mark every unmet building prerequisite as met. Returns how many it flipped, or -1 if
        /// the BuildManager's prerequisite table isn't available yet.</summary>
        private static int ForceAllPrerequisitesMet()
        {
            try
            {
                if (_prereqDict == null)
                {
                    var gm = GameManager.Instance;
                    var bm = gm != null ? gm.buildManager : null;
                    if (bm == null) return -1;
                    _fPrereqDict ??= typeof(BuildManager).GetField("buildingNameToPrereqsDict",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_fPrereqDict == null)
                    {
                        Status = "All Buildings unavailable (game update changed the prerequisite table).";
                        MelonLogger.Warning("[DivineHands] Progression: BuildManager.buildingNameToPrereqsDict not found");
                        return -1;
                    }
                    _prereqDict = _fPrereqDict.GetValue(bm) as IDictionary;
                    if (_prereqDict == null || _prereqDict.Count == 0) { _prereqDict = null; return -1; }
                }

                int flipped = 0;
                foreach (DictionaryEntry kv in _prereqDict)
                {
                    if (!(kv.Value is List<Prerequisite> list)) continue;
                    foreach (var p in list)
                        if (p != null && !p.met) { p.met = true; flipped++; }
                }
                if (flipped > 0 && Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] All Buildings: met {flipped} prerequisite(s)");
                return flipped;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] All Buildings failed: {ex.Message}");
                return -1;
            }
        }

        // =====================================================================
        // Tech points
        // =====================================================================

        public static void GrantTechPoints()
        {
            try
            {
                var tm = GameManager.Instance?.techTreeManager;
                if (tm == null) { Status = "Tech tree not ready."; return; }
                if (!tm.CanPointsBeGained()) { Status = "The tech tree is already fully funded."; return; }

                int want = Mathf.Clamp(Config.TechPointGrant.Value, 1, 50);
                int before = tm.knowledgePoints;
                tm.AddKnowledgePoints(want, silent: false, bonusPoints: true);   // game caps at what's left to buy
                int got = tm.knowledgePoints - before;
                Status = got < want
                    ? $"+{got} tech point(s) — that's all the tree has left to buy."
                    : $"+{got} tech point(s). Spend them in the tech tree.";
            }
            catch (Exception ex)
            {
                Status = "Couldn't add tech points (see log).";
                MelonLogger.Warning($"[DivineHands] Grant tech points failed: {ex.Message}");
            }
        }

        // =====================================================================
        // Unlock All Tech (two-click confirm)
        // =====================================================================

        /// <summary>True while the button is waiting for its confirming second click.</summary>
        public static bool AwaitingConfirm => Time.unscaledTime < _confirmUntil;

        // Cached "whole tree researched?" for the panel chip — IsEntireTreeFullyUnlocked walks every node,
        // so re-check at most once a second rather than every frame the panel is open.
        private static bool _treeDone;
        private static float _treeDoneCheckAt;

        /// <summary>True once every tech is researched at every rank. Lights the panel's All Tech chip.</summary>
        public static bool TreeFullyUnlocked
        {
            get
            {
                if (Time.unscaledTime >= _treeDoneCheckAt)
                {
                    _treeDoneCheckAt = Time.unscaledTime + 1f;
                    try { _treeDone = GameManager.Instance?.techTreeManager?.IsEntireTreeFullyUnlocked() ?? false; }
                    catch { _treeDone = false; }
                }
                return _treeDone;
            }
        }

        public static void UnlockAllTechClicked()
        {
            if (!AwaitingConfirm)
            {
                _confirmUntil = Time.unscaledTime + 4f;
                Status = "Permanent in this save. Click again within 4 seconds to unlock every tech.";
                return;
            }
            _confirmUntil = -1f;
            UnlockAllTech();
        }

        private static void UnlockAllTech()
        {
            try
            {
                var gm = GameManager.Instance;
                var tm = gm != null ? gm.techTreeManager : null;
                var bm = gm != null ? gm.buildManager : null;
                var em = gm != null ? gm.eventManager : null;
                if (tm == null || bm == null || em == null) { Status = "Tech tree not ready."; return; }
                if (tm.IsEntireTreeFullyUnlocked()) { Status = "Every tech is already unlocked."; return; }

                int nodes = 0, ranks = 0;
                foreach (var node in tm.techTreeNodeData.ToArray())
                {
                    if (node == null) continue;
                    int id = node.GetId(), max = node.GetNumRanks(), added = 0;

                    // Step 1 — research each missing rank, free (cost 0). Bounded in case a rank refuses.
                    for (int guard = 0; node.curRank < max && guard < max + 1; guard++)
                    {
                        int before = node.curRank;
                        tm.ActivateTechOrRank(id, 0, onLoad: false);
                        if (node.curRank == before) break;
                        added++;
                    }
                    if (added == 0) continue;

                    // Step 2 — what the tech tree's Confirm button does: effects per researched rank,
                    // then the building unlock and the TechActivated event.
                    bool stack = max > 1;
                    if (node.gameEffectsEntries != null)
                        for (int r = 0; r < added; r++)
                            foreach (var effect in node.gameEffectsEntries)
                                effect?.ActivateEffect(stack);
                    if (node.state == TechTreeNodeData.State.Active)
                        bm.ActivateTech(id);
                    em.Raise(new TechTreeManager.TechTreeEvent(TechTreeManager.TechTreeEvent.Type.TechActivated, id));
                    em.Raise(new TechTreeManager.TechTreeEvent(TechTreeManager.TechTreeEvent.Type.RefreshTechNodeData, id));

                    nodes++; ranks += added;
                }

                ResetTechTreeWindowCache(tm, em);
                _treeDoneCheckAt = 0f;
                Status = nodes > 0 ? $"Unlocked {nodes} tech(s), {ranks} rank(s)." : "Nothing left to unlock.";
                MelonLogger.Msg($"[DivineHands] Unlock All Tech: {nodes} node(s), {ranks} rank(s)");
            }
            catch (Exception ex)
            {
                Status = "Unlock failed partway (see log).";
                MelonLogger.Warning($"[DivineHands] Unlock All Tech failed: {ex.Message}");
            }
        }

        /// <summary>Rebuild the tech tree window's pending-change cache so its Confirm/Undo buttons don't
        /// act on our unlocks (see the class summary). The window re-caches on every Open() anyway; this
        /// covers the case where it's open right now.</summary>
        private static void ResetTechTreeWindowCache(TechTreeManager tm, EventManager em)
        {
            try
            {
                var overview = tm.uiTechTreeOverview;
                if (overview == null) return;
                typeof(UITechTreeOverview).GetMethod("CacheTechTreeNodeInfo", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(overview, null);
                // Any tech tree event makes the window refresh its point text and Confirm/Undo buttons.
                em.Raise(new TechTreeManager.TechTreeEvent(TechTreeManager.TechTreeEvent.Type.RefreshTechNodeData, -1));
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] Tech tree window cache reset failed: {ex.Message}");
            }
        }
    }
}
