using System;
using System.Reflection;
using UnityEngine;

namespace DivineHands.Modules
{
    /// <summary>
    /// Map-wide god tools. v0.1 ships <b>Reveal Map</b>.
    ///
    /// Reveal Map drives <c>FOWSystem.instance.revealCompletely</c>. While that's true, FF's
    /// background fog thread floods the <i>visible</i> channel (<c>mBuffer1.r</c>) every tick
    /// (<c>RevealCompletely</c>, decompile 401003); that lerps into the persistent <i>explored</i>
    /// channel <c>mBuffer0.g</c> (UpdateBuffer 400900, sticky via <c>RevealMap</c> 401199:
    /// g = max(g, r)). <c>IsExplored()</c> reads <c>mBuffer0.g</c> (401287), so once revealed the
    /// whole map reads explored — fog shows as explored shading AND every resource-site minimap icon
    /// appears.
    ///
    /// To give a real toggle we snapshot ALL THREE fog buffers before revealing and copy them back
    /// on un-reveal. (Single-buffer mBuffer1 restore is NOT enough: the explored state IsExplored
    /// reads lives in mBuffer0, and BlurVisibility swaps the mBuffer1/mBuffer2 field references each
    /// tick — decompile 401194 — so we restore by array-content copy into whatever array each field
    /// currently holds, never by reassigning references. mBuffer0 never swaps, so its restore is
    /// exact regardless of swap parity; mBuffer1/2 are the visible+scratch buffers and any one-frame
    /// tear there is cosmetic and self-heals.)
    ///
    /// Minimap icons need one extra step: <c>MinimapPOI.isInFoW</c> (128327) is flipped false the
    /// frame a POI becomes explored (Minimap.PerformUpdate 127818) and never re-set by the fog
    /// restore. The engine's own <c>Minimap.ResetPOIs()</c> (127943) re-fogs every POI (isInFoW=true
    /// + Release()) — after we restore the buffers, Minimap.PerformUpdate (127673, reached every
    /// LateUpdate via 127558) re-reveals only the POIs whose position IsExplored() still reports true
    /// (127816), processing a few POIs per frame. Family-agnostic: works for mineral / clay-sand-stone
    /// pits / animal spawn / excavation / salvage POIs with no per-family handling.
    ///
    /// Caveat (unchanged): FOWSystem.Save() (400667) serializes mBuffer1, so saving WHILE revealed
    /// still bakes exploration into the .sav. Turn Reveal Map off before saving for clean fog.
    /// </summary>
    public static class GodTools
    {
        /// <summary>Runtime live ON/OFF for Reveal Map — toggled in the in-game panel, NOT a saved pref.
        /// Reset to false on every map load / scene exit (see <see cref="ResetActive"/>), so the power
        /// always starts off when entering a map even if its Enable pref is on.</summary>
        public static bool RevealActive;

        private static bool _appliedReveal;
        private static bool _hasApplied;

        // Cached reflection for the three protected Color32[] fog buffers.
        private static FieldInfo? _buf0Field, _buf1Field, _buf2Field;
        private static bool _bufFieldsResolved;

        private static Color32[]? _snap0, _snap1, _snap2;

        public static void OnMapLoaded()
        {
            _hasApplied = false;
            ClearSnapshot();
        }

        public static void OnSceneExit()
        {
            _hasApplied = false;
            ClearSnapshot();
        }

        /// <summary>Force the live god-power(s) OFF. Called on map load and scene exit so a fresh
        /// map always starts with Reveal Map inactive regardless of the Enable pref.</summary>
        public static void ResetActive() => RevealActive = false;

        public static void OnUpdate()
        {
            SyncRevealMap();
        }

        private static void SyncRevealMap()
        {
            var fow = FOWSystem.instance;
            if (fow == null) return;

            // Live state is the runtime flag, gated by master + the Enable pref. If the power is
            // disabled in config, RevealActive is held false so the effect can never fire.
            bool want = Config.MasterEnable.Value && Config.EnableRevealMap.Value && RevealActive;
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
                RestoreFog(fow);          // 1) put the explored buffers back
                RestoreMinimapIcons();    // 2) then re-fog the POIs so icons re-evaluate
            }

            _appliedReveal = want;
            _hasApplied = true;

            if (Config.DebugLog.Value)
                MelonLoader.MelonLogger.Msg($"[DivineHands] Reveal Map -> {want}");
        }

        // =====================================================================
        // Fog buffer snapshot / restore  (mBuffer0 + mBuffer1 + mBuffer2)
        // =====================================================================

        // FOWSystem.mBuffer0/1/2 are protected Color32[] (decompile 400613-617). mBuffer0 holds the
        // persistent explored .g channel IsExplored reads; mBuffer1/2 are the visible + scratch
        // buffers that get swapped each blur tick. We snapshot all three so restore is exact.
        private static void ResolveBufferFields()
        {
            if (_bufFieldsResolved) return;
            _bufFieldsResolved = true;
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
            var t = typeof(FOWSystem);
            _buf0Field = t.GetField("mBuffer0", F);
            _buf1Field = t.GetField("mBuffer1", F);
            _buf2Field = t.GetField("mBuffer2", F);
        }

        private static Color32[]? Read(FieldInfo? f, FOWSystem fow) =>
            f?.GetValue(fow) as Color32[];

        private static void SnapshotFog(FOWSystem fow)
        {
            try
            {
                ResolveBufferFields();
                _snap0 = Read(_buf0Field, fow)?.Clone() as Color32[];
                _snap1 = Read(_buf1Field, fow)?.Clone() as Color32[];
                _snap2 = Read(_buf2Field, fow)?.Clone() as Color32[];
            }
            catch (Exception ex)
            {
                ClearSnapshot();
                if (Config.DebugLog.Value)
                    MelonLoader.MelonLogger.Warning($"[DivineHands] FOW snapshot failed: {ex.Message}");
            }
        }

        private static void RestoreFog(FOWSystem fow)
        {
            try
            {
                ResolveBufferFields();
                // Copy snapshot CONTENTS into whatever array each field currently points at.
                // (Never reassign the field — the background thread holds the array refs and may have
                //  swapped mBuffer1<->mBuffer2; a same-length content copy is the safe, tear-tolerant
                //  restore. Cosmetic if it tears.)
                CopyInto(_snap0, Read(_buf0Field, fow));
                CopyInto(_snap1, Read(_buf1Field, fow));
                CopyInto(_snap2, Read(_buf2Field, fow));
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLoader.MelonLogger.Warning($"[DivineHands] FOW restore failed: {ex.Message}");
            }
            finally
            {
                ClearSnapshot();
            }
        }

        private static void CopyInto(Color32[]? src, Color32[]? dst)
        {
            if (src == null || dst == null) return;
            int n = Math.Min(src.Length, dst.Length);
            Array.Copy(src, dst, n);
        }

        private static void ClearSnapshot()
        {
            _snap0 = _snap1 = _snap2 = null;
        }

        // =====================================================================
        // Minimap POI icon revert
        // =====================================================================

        // Minimap.ResetPOIs() (decompile 127943) sets every MinimapPOI.isInFoW back to true and
        // Release()s its icon GameObject. Called AFTER the fog buffers are restored, so the Minimap
        // PerformUpdate loop (127673, reached each LateUpdate via 127558) re-reveals only POIs whose
        // position IsExplored() now reports true (127816) — i.e. exactly the pre-reveal icon set.
        // Public method, no reflection needed.
        private static void RestoreMinimapIcons()
        {
            try
            {
                var mm = Minimap.instance;
                if (mm == null) return;
                mm.ResetPOIs();
                if (Config.DebugLog.Value)
                    MelonLoader.MelonLogger.Msg("[DivineHands] Minimap POIs reset (icons re-fogged)");
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLoader.MelonLogger.Warning($"[DivineHands] ResetPOIs failed: {ex.Message}");
            }
        }
    }
}
