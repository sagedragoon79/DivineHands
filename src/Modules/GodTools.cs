using System;
using System.Reflection;
using UnityEngine;

namespace DivineHands.Modules
{
    /// <summary>
    /// Map-wide god tools. v0.1 ships <b>Reveal Map</b>.
    ///
    /// Reveal Map drives <c>FOWSystem.instance.revealCompletely</c> — but that is NOT cleanly
    /// reversible on its own: while it's true, FF's background fog thread marks every tile
    /// <i>explored</i> in <c>mBuffer1</c>, and <c>FOWSystem.Save()</c> serializes that buffer into
    /// the <c>.sav</c>. So a naive toggle leaves the whole map explored (shaded) when turned off, and
    /// bakes the exploration into the save.
    ///
    /// To give a real toggle, we snapshot <c>mBuffer1</c> <b>before</b> revealing and copy it back
    /// when toggled off (best-effort — the buffer lives on a background thread, so a restore is a
    /// fast array copy that may briefly tear; cosmetic only). Caveat the snapshot can't fix: if you
    /// SAVE while revealed, the revealed buffer is what gets written. Turn Reveal Map off before
    /// saving for clean fog.
    ///
    /// Roadmap: God View camera, Free Cam, and the scoped Build-Anywhere toggle land here next.
    /// </summary>
    public static class GodTools
    {
        private static bool _appliedReveal;
        private static bool _hasApplied;

        private static FieldInfo? _fowBufferField;
        private static Color32[]? _fogSnapshot;

        public static void OnMapLoaded()
        {
            _hasApplied = false;
            _fogSnapshot = null;
        }

        public static void OnSceneExit()
        {
            _hasApplied = false;
            _fogSnapshot = null;
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

            if (want)
            {
                // Capture the real fog state BEFORE the reveal thread overwrites it.
                SnapshotFog(fow);
                fow.revealCompletely = true;
            }
            else
            {
                fow.revealCompletely = false;
                RestoreFog(fow);
            }

            _appliedReveal = want;
            _hasApplied = true;

            if (Config.DebugLog.Value)
                MelonLoader.MelonLogger.Msg($"[DivineHands] Reveal Map -> {want}");
        }

        // FOWSystem.mBuffer1 is the protected, serialized fog buffer (explored state lives here).
        private static FieldInfo? BufferField =>
            _fowBufferField ??= typeof(FOWSystem).GetField(
                "mBuffer1", BindingFlags.NonPublic | BindingFlags.Instance);

        private static void SnapshotFog(FOWSystem fow)
        {
            try
            {
                if (BufferField?.GetValue(fow) is Color32[] live)
                    _fogSnapshot = (Color32[])live.Clone();
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLoader.MelonLogger.Warning($"[DivineHands] FOW snapshot failed: {ex.Message}");
            }
        }

        private static void RestoreFog(FOWSystem fow)
        {
            if (_fogSnapshot == null) return;
            try
            {
                if (BufferField?.GetValue(fow) is Color32[] live)
                {
                    int n = Math.Min(live.Length, _fogSnapshot.Length);
                    Array.Copy(_fogSnapshot, live, n);
                }
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLoader.MelonLogger.Warning($"[DivineHands] FOW restore failed: {ex.Message}");
            }
            finally
            {
                _fogSnapshot = null;
            }
        }
    }
}
