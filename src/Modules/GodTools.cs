using UnityEngine;

namespace DivineHands.Modules
{
    /// <summary>
    /// Map-wide god tools. v0.1 ships <b>Reveal Map</b> — the cleanest proof-of-life feature:
    /// a single public game field, no Harmony patch, no save contamination.
    ///
    /// Reveal Map drives <c>FOWSystem.instance.revealCompletely</c>, the exact field the game
    /// itself uses to clear fog. It is a live runtime toggle: flipping the pref off restores the
    /// fog the game has already explored (nothing is written to the save).
    ///
    /// Roadmap: God View camera, Free Cam, and the scoped Build-Anywhere toggle land here next.
    /// </summary>
    public static class GodTools
    {
        // Last value pushed into FOWSystem, so we only write on change.
        private static bool _appliedReveal;
        private static bool _hasApplied;

        public static void OnMapLoaded()
        {
            // Force a re-sync against the fresh FOWSystem on the new map.
            _hasApplied = false;
        }

        public static void OnSceneExit()
        {
            _hasApplied = false;
        }

        public static void OnUpdate()
        {
            SyncRevealMap();
        }

        private static void SyncRevealMap()
        {
            var fow = FOWSystem.instance;
            if (fow == null) return;

            bool want = Config.RevealMap.Value;
            if (_hasApplied && want == _appliedReveal) return;

            fow.revealCompletely = want;
            _appliedReveal = want;
            _hasApplied = true;

            if (Config.DebugLog.Value)
                MelonLoader.MelonLogger.Msg($"[DivineHands] Reveal Map -> {want}");
        }
    }
}
