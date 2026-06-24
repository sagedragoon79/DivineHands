using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace DivineHands.Patches
{
    /// <summary>
    /// Makes God View's mouse-scroll zoom proportional: fine steps when zoomed in close, near-vanilla
    /// steps when zoomed out. Fixes the "jump from normal to too-close with nothing between" that God
    /// View causes — turning on God View flips <c>CameraManager.zoomUnlocked</c>, which applies a FLAT
    /// 5% zoom damping, and because zoom maps linearly across the widened 1..1000 distance range, every
    /// scroll notch is ~5 world units regardless of how close you are.
    ///
    /// Verified against the Assembly-CSharp decompile:
    ///   CameraManager.AdjustZoom(float amount)  [59656] — public; does `if (zoomUnlocked) amount *= 0.05f;`
    ///                                                       then SetDesiredZoom(desiredZoom + amount)
    ///   CameraManager.CalculateZoom()           [59689] — public; normalized 0..1 (1 = zoomed in close)
    ///   CameraManager.zoomUnlocked              [59294] — private bool; set only by UnlockCameraZoom
    ///                                                       [59999], i.e. zoomUnlocked == God View on
    ///   SetDesiredZoom [59665] clamps to [minZoom,maxZoom] with NO step rounding, so a scaled-down
    ///   delta always accumulates (never a no-op).
    ///
    /// We run a prefix that rescales <c>amount</c> BEFORE the vanilla 0.05f damping, only while
    /// zoomUnlocked is true: multiply by lerp(fine, 1, 1-currentZoom) so steps shrink toward the ground
    /// (currentZoom→1) and stay ~vanilla far out (currentZoom→0). Gating on the live <c>zoomUnlocked</c>
    /// field (not the God View pref/flag, which can lag a mid-toggle) means vanilla zoom is untouched when God
    /// View is off. Auto-registered via Plugin's HarmonyInstance.PatchAll.
    /// </summary>
    [HarmonyPatch(typeof(CameraManager), "AdjustZoom")]
    internal static class CameraZoomPatch
    {
        private static FieldInfo? _zoomUnlockedField;     // private bool zoomUnlocked
        private static MethodInfo? _calculateZoomMethod;  // public float CalculateZoom()
        private static bool _resolved;
        private static bool _resolveFailed;

        private static void Resolve()
        {
            if (_resolved || _resolveFailed) return;
            try
            {
                var t = typeof(CameraManager);
                _zoomUnlockedField = t.GetField("zoomUnlocked",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _calculateZoomMethod = t.GetMethod("CalculateZoom",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (_zoomUnlockedField == null || _calculateZoomMethod == null)
                {
                    _resolveFailed = true;
                    if (Config.DebugLog.Value)
                        MelonLogger.Warning("[DivineHands] CameraZoomPatch: could not resolve " +
                            "zoomUnlocked/CalculateZoom — proportional zoom disabled (vanilla unaffected).");
                    return;
                }
                _resolved = true;
            }
            catch (Exception ex)
            {
                _resolveFailed = true;
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] CameraZoomPatch resolve failed: {ex.Message}");
            }
        }

        // Prefix runs before vanilla AdjustZoom; we rescale 'amount' in place and let the original run
        // (so the vanilla 0.05f damping + SetDesiredZoom still apply). Returns void => never skips vanilla.
        private static void Prefix(CameraManager __instance, ref float amount)
        {
            if (__instance == null) return;
            if (Mathf.Approximately(amount, 0f)) return;
            if (!Config.MasterEnable.Value || !Config.ProportionalZoom.Value) return;

            Resolve();
            if (!_resolved) return;

            try
            {
                // Only act while God View is on. Off => pass the scroll delta through untouched.
                if (_zoomUnlockedField!.GetValue(__instance) is not bool unlocked || !unlocked) return;

                // currentZoom: 0 (far) .. 1 (close). Steps should get finer toward 1.
                float currentZoom = Mathf.Clamp01((float)_calculateZoomMethod!.Invoke(__instance, null)!);

                // Close-in fineness floor; far-out (currentZoom→0) lerps back to 1 (~vanilla feel).
                float fine = Mathf.Clamp(Config.ZoomStepScale.Value, 0.02f, 1f);
                float multiplier = Mathf.Max(Mathf.Lerp(fine, 1f, 1f - currentZoom), 0.02f);

                amount *= multiplier;
            }
            catch (Exception ex)
            {
                // Fail safe: leave 'amount' as-is (vanilla behavior).
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] CameraZoomPatch prefix error (passing through): {ex.Message}");
            }
        }
    }
}
