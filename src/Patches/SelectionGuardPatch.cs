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
}
