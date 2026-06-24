using MelonLoader;

namespace DivineHands
{
    /// <summary>
    /// Central registry for every MelonPreferences entry. Modules only ever read from these
    /// properties — they never create their own entries. Each entry also gets a registration
    /// block in <see cref="KeepClarityIntegration"/> so it renders richly in the KC panel.
    ///
    /// Defaults are chosen for safety: the panel is opt-in to open (hotkey), and every
    /// world-altering god-power defaults OFF.
    /// </summary>
    public static class Config
    {
        private static MelonPreferences_Category _root = null!;

        // ===== General =====

        /// <summary>Master switch. When false, the hotkey does nothing and no god-power runs.</summary>
        public static MelonPreferences_Entry<bool>   MasterEnable   { get; private set; } = null!;

        /// <summary>Hotkey (KeyCode name or chord, e.g. "Ctrl+G", "F6", "Alt+Shift+D") that
        /// toggles the in-game Divine Hands panel. Parsed by <see cref="Hotkey"/>.</summary>
        public static MelonPreferences_Entry<string> PanelHotkey    { get; private set; } = null!;

        /// <summary>If true, the panel opens automatically on entering a map.</summary>
        public static MelonPreferences_Entry<bool>   ShowPanelOnMap { get; private set; } = null!;

        /// <summary>Verbose diagnostic logging to MelonLoader.log.</summary>
        public static MelonPreferences_Entry<bool>   DebugLog       { get; private set; } = null!;

        // ===== God Tools =====

        /// <summary>Reveal the entire map (clears fog of war). Live toggle —
        /// drives FOWSystem.instance.revealCompletely while in-game.</summary>
        public static MelonPreferences_Entry<bool>   RevealMap      { get; private set; } = null!;

        public static void Initialize()
        {
            _root = MelonPreferences.CreateCategory("DivineHands", "Divine Hands");

            MasterEnable = _root.CreateEntry(
                "MasterEnable", true,
                display_name: "Divine Hands — Enabled",
                description: "Master switch for the whole mod. When off, the panel hotkey is inert " +
                             "and no god-power applies.");

            PanelHotkey = _root.CreateEntry(
                "PanelHotkey", "Ctrl+G",
                display_name: "Panel Hotkey",
                description: "Key (or chord) that toggles the in-game Divine Hands panel. " +
                             "A Unity KeyCode name or a chord with Ctrl/Alt/Shift modifiers — " +
                             "e.g. Ctrl+G, F6, Alt+Shift+D. Default: Ctrl+G.");

            ShowPanelOnMap = _root.CreateEntry(
                "ShowPanelOnMap", false,
                display_name: "Open Panel on Map Load",
                description: "Automatically open the Divine Hands panel when a map loads. Default: off.");

            DebugLog = _root.CreateEntry(
                "DebugLog", false,
                display_name: "Debug Logging",
                description: "Verbose diagnostic output to MelonLoader.log. Default: off.");

            RevealMap = _root.CreateEntry(
                "RevealMap", false,
                display_name: "Reveal Map",
                description: "Clear the entire fog of war (FOWSystem.revealCompletely). Toggling OFF " +
                             "best-effort restores the fog you had explored before. Caveat: FF bakes " +
                             "explored state into the SAVE — if you save while revealed, the whole map " +
                             "stays explored. Turn it off before saving for clean fog. Default: off.");

            MelonLogger.Msg("[DivineHands] Config initialized");
        }
    }
}
