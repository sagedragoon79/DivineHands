using HarmonyLib;

namespace DivineHands.Patches
{
    /// <summary>
    /// In Free Cam, Space is the "ascend" key — but Space is also FF's default <c>pauseGame</c> hotkey,
    /// so every ascend press was toggling the game's pause state. The chain is
    /// pause hotkey → <c>PauseGameSignal</c> → <c>OnPauseSignal</c> (decompile 113370) →
    /// <c>GameManager.TogglePause()</c> (96200) → <c>SetPaused(!paused)</c>.
    ///
    /// This prefix swallows <c>TogglePause</c> while Free Cam is active, so Space (or whatever pause is
    /// bound to) flies the camera up without flickering pause. Net effect: <b>pause is frozen at whatever
    /// it was when you entered Free Cam</b> — pause first if you want to survey a still scene, then fly;
    /// exit Free Cam (Ctrl+F) to change pause again.
    ///
    /// Only the user-facing toggle is blocked: game-driven <c>SetPaused</c> calls (dialogs, load screens)
    /// bypass <c>TogglePause</c> and are unaffected, and the load-time unpause (<c>GameManager.Start</c>,
    /// 96123) runs before Free Cam can be active since live state resets OFF on every map load.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "TogglePause")]
    internal static class FreeCamPauseGuardPatch
    {
        // Return false to skip the original toggle while Free Cam owns the Space key.
        private static bool Prefix() => !DivineHands.Modules.CameraTools.FreeCamActive;
    }
}
