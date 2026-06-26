using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace DivineHands.Patches
{
    /// <summary>
    /// Tames God View's mouse-scroll zoom: in God View the distance range is widened (1..1000), so a raw
    /// scroll notch jumps a huge amount. A prefix on <c>CameraManager.AdjustZoom</c> rescales the zoom
    /// delta by <c>fine × lerp(closeBoost,1,1-currentZoom) × baseStep</c> — <c>fine</c> (the Zoom Fineness
    /// slider) scales the whole range so its effect is visible at any zoom level, a mild taper keeps
    /// close-in a touch finer, and <c>baseStep</c> supplies the overall damping.
    ///
    /// HISTORY / why it's built this way: the original design gated on the live <c>CameraManager.zoomUnlocked</c>
    /// field (and had God View set it so the game's own 5% damping applied). In practice that field — and
    /// sometimes <c>CalculateZoom</c> — failed to resolve on the live build (the patch logged "could not
    /// resolve … — proportional zoom disabled" and was completely inert). So the patch no longer reflects
    /// <c>zoomUnlocked</c> at all: it gates on DH's own <see cref="DivineHands.Modules.CameraTools.GodViewActive"/>
    /// flag (no game reflection), owns the damping itself (<c>baseStep</c>, since God View no longer sets
    /// <c>zoomUnlocked</c>), and treats <c>CalculateZoom()</c> [59689] as OPTIONAL — if it can't be bound
    /// (rename / added overload) the taper is simply dropped and a flat scale is used. Vanilla zoom (God
    /// View off) is untouched. Auto-registered via Plugin's HarmonyInstance.PatchAll.
    /// </summary>
    [HarmonyPatch(typeof(CameraManager), "AdjustZoom")]
    internal static class CameraZoomPatch
    {
        private static MethodInfo? _calculateZoomMethod;  // OPTIONAL — only the close-in taper uses it
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true; // always — the gate now uses DH's GodViewActive, not reflected game internals
            // CalculateZoom is OPTIONAL (close-in taper only). Look up the PARAMETERLESS overload
            // explicitly so an added overload (ambiguous match) or a game-version rename can't break the
            // patch; without it we just use a flat scale (no taper). We no longer reflect zoomUnlocked at
            // all — it failed to resolve in this build (that's why the patch was inert), so the gate moved
            // to DH's own God View flag and the damping moved into this patch (see Prefix).
            try
            {
                _calculateZoomMethod = typeof(CameraManager).GetMethod("CalculateZoom",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
            }
            catch { _calculateZoomMethod = null; }
            if (Config.DebugLog.Value)
                MelonLogger.Msg("[DivineHands] CameraZoomPatch resolved (CalculateZoom taper: " +
                                (_calculateZoomMethod != null ? "yes)." : "no — flat scale)."));
        }

        // Prefix runs before vanilla AdjustZoom; we rescale the zoom delta in place and let the original
        // run (so the vanilla 0.05f damping + SetDesiredZoom still apply). Returns void => never skips
        // vanilla. NOTE: the by-ref delta is injected POSITIONALLY as __0 — the shipped DLL names this
        // parameter "zoomAdj" (the decompile showed "amount"), and Harmony matches prefix params by NAME,
        // so a named "amount" param throws "Parameter not found" and aborts PatchAll for the whole mod.
        // __0 is name-agnostic.
        private static void Prefix(CameraManager __instance, ref float __0)
        {
            if (__instance == null) return;
            if (Mathf.Approximately(__0, 0f)) return;
            if (!Config.MasterEnable.Value || !Config.ProportionalZoom.Value) return;

            // Only act while God View is on — gate on DH's OWN runtime flag (no reflection), so a
            // game-version rename of zoomUnlocked can't silently disable the patch (which is what happened).
            if (!DivineHands.Modules.CameraTools.GodViewActive) return;

            Resolve();

            try
            {
                // currentZoom: 0 (far) .. 1 (close) — only for the optional close-in taper.
                float currentZoom = 0f;
                if (_calculateZoomMethod != null)
                {
                    try { currentZoom = Mathf.Clamp01((float)_calculateZoomMethod.Invoke(__instance, null)!); }
                    catch { currentZoom = 0f; }
                }

                // `fine` (Zoom Fineness slider) scales the whole god-view step (visible at any zoom level),
                // with a mild close-in taper. baseStep tames the widened god-view range — it replaces the
                // game's 5% zoom damping, which DH's God View no longer relies on (that path was reflection-
                // fragile). fine=1 ≈ the old vanilla-god-view feel; lower = finer.
                float fine = Mathf.Clamp(Config.ZoomStepScale.Value, 0.02f, 1f);
                const float closeBoost = 0.5f; // close-in steps = 0.5x of far-out steps
                const float baseStep = 0.05f;  // overall tame factor for the widened god-view zoom range
                float multiplier = Mathf.Max(fine * Mathf.Lerp(closeBoost, 1f, 1f - currentZoom) * baseStep, 0.001f);

                if (Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] Zoom: in={__0:0.#####} curZoom={currentZoom:0.###} " +
                                    $"fine={fine:0.##} mult={multiplier:0.#####} out={__0 * multiplier:0.#####}");

                __0 *= multiplier;
            }
            catch (Exception ex)
            {
                // Fail safe: leave the delta as-is (vanilla behavior).
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] CameraZoomPatch prefix error (passing through): {ex.Message}");
            }
        }
    }
}
