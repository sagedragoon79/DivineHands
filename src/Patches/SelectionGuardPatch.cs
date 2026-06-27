using HarmonyLib;
using DivineHands.Core;

namespace DivineHands.Patches
{
    /// <summary>
    /// The v0.1 panel is a lightweight IMGUI window, which the game's input system cannot see —
    /// IMGUI isn't part of the uGUI EventSystem, so <c>EventSystem.IsPointerOverGameObject()</c>
    /// returns false over our window. Without this guard, dragging the panel makes the game start a
    /// drag-select marquee underneath it (and clicks fall through to the world).
    ///
    /// FF gates world interactions on <c>GameManager.pointerIsOverUI</c> — e.g. the drag-select
    /// starts only when <c>!gameManager.pointerIsOverUI</c>. We force that property true whenever the
    /// cursor is over the Divine Hands panel, so the game treats the panel like any other UI.
    ///
    /// This whole patch goes away once the panel migrates to the planned uGUI surface (whose
    /// raycast-target background blocks game input natively).
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "get_pointerIsOverUI")]
    internal static class SelectionGuardPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (!__result && DivinePanel.BlocksGameInput)
                __result = true;
        }
    }

    /// <summary>
    /// Belt-and-suspenders for the same click-through problem, applied at the exact handler that
    /// deselects the building. <c>Input_SelectGameObject.OnLMBPressed</c> (decompile 117688) already
    /// returns early when <c>pointerIsOverUI</c> is true — and <see cref="SelectionGuardPatch"/> forces
    /// that property true over the panel — but that path depends on the GUI-space rect math in
    /// <see cref="DivinePanel.BlocksGameInput"/> matching the live cursor exactly. The scrollable item
    /// grid in the "Selected Building" section is where a near-boundary or scrolled click can slip
    /// through that math, deselect the building, and close both windows.
    ///
    /// This prefix skips the deselection handler outright whenever the cursor is over the panel,
    /// independent of the property getter — so a left-click anywhere on the Divine Hands window can
    /// never reach <c>Exit()</c> / re-select. When the cursor is NOT over the panel it returns true and
    /// the original runs unchanged (no behavior change for normal world clicks). Same lifetime as the
    /// property patch: harmless once the panel migrates to a uGUI raycast-target surface.
    /// </summary>
    [HarmonyPatch(typeof(Input_SelectGameObject), "OnLMBPressed")]
    internal static class BuildingDeselectGuardPatch
    {
        // Return false to skip the original deselection handler while the panel owns the click.
        private static bool Prefix() => !DivinePanel.BlocksGameInput;
    }

    /// <summary>
    /// The minimap is a uGUI element (<c>MiniMapInteraction</c> : IPointerClickHandler/IDragHandler) whose
    /// click AND drag both route through the private <c>HandlePointer</c> (decompile 128232), which converts
    /// the cursor to a world point and calls <c>cameraManager.SetLookLocation</c> + <c>inputManager
    /// .MinimapClicked()</c>. It does NOT consult <c>pointerIsOverUI</c>, so a click on a Divine Hands panel
    /// button that happens to sit over the minimap (top-right) falls through and snaps the camera to that map
    /// corner. Skip <c>HandlePointer</c> while the cursor is over the panel so panel clicks can't move the
    /// camera. Same lifetime as the guards above — unneeded once the panel is a uGUI raycast-target surface.
    /// </summary>
    [HarmonyPatch(typeof(MiniMapInteraction), "HandlePointer")]
    internal static class MinimapGuardPatch
    {
        private static bool Prefix() => !DivinePanel.BlocksGameInput;
    }
}
