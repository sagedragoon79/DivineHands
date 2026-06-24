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

        // ===== Terrain Sculpting =====

        /// <summary>Master switch for the terrain-elevation god-power.</summary>
        public static MelonPreferences_Entry<bool>   TerrainEnable   { get; private set; } = null!;

        /// <summary>Brush mode: 0=Raise, 1=Lower, 2=Smooth, 3=Flatten (cast to
        /// <see cref="Modules.TerrainElevation.Mode"/>).</summary>
        public static MelonPreferences_Entry<int>    TerrainMode     { get; private set; } = null!;

        /// <summary>Raise/Lower height delta (world metres) per stroke; also the Smooth step.</summary>
        public static MelonPreferences_Entry<float>  TerrainStrength { get; private set; } = null!;

        /// <summary>Brush grid size in heightmap cells per side (1–10). Each cell ≈ terrain Resolution
        /// metres (default 5 m).</summary>
        public static MelonPreferences_Entry<int>    TerrainGridSize { get; private set; } = null!;

        /// <summary>Hotkey that applies the brush at the cursor. Default middle mouse button.</summary>
        public static MelonPreferences_Entry<string> TerrainApplyKey { get; private set; } = null!;

        /// <summary>Hotkey that undoes the last terrain stroke. Default Ctrl+Z.</summary>
        public static MelonPreferences_Entry<string> TerrainUndoKey  { get; private set; } = null!;

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

            // ===== Terrain Sculpting =====

            TerrainEnable = _root.CreateEntry(
                "TerrainEnable", false,
                display_name: "Terrain Sculpting",
                description: "Enable the terrain-elevation god-power (Raise/Lower/Smooth/Flatten). " +
                             "When on, select Terrain in the panel and apply with the apply key over the " +
                             "world. Default: off.");

            TerrainMode = _root.CreateEntry(
                "TerrainMode", 0,
                display_name: "Brush Mode",
                description: "0 = Raise, 1 = Lower, 2 = Smooth, 3 = Flatten. Hold Shift to invert " +
                             "Raise<->Lower. Default: Raise.");

            TerrainStrength = _root.CreateEntry(
                "TerrainStrength", 1.0f,
                display_name: "Brush Strength",
                description: "Raise/Lower height change in world metres per application (also the Smooth " +
                             "step size). Flatten ignores this — it sets the brush to the cursor's height. " +
                             "Default: 1.0.");

            TerrainGridSize = _root.CreateEntry(
                "TerrainGridSize", 3,
                display_name: "Brush Grid Size",
                description: "Brush footprint in heightmap cells per side (1–10). Each cell is ~Resolution " +
                             "metres (default 5 m), so a 3 grid ≈ 15 m. Default: 3.");

            TerrainApplyKey = _root.CreateEntry(
                "TerrainApplyKey", "Mouse2",
                display_name: "Apply Key",
                description: "Key/button that applies the brush at the cursor. A Unity KeyCode name — e.g. " +
                             "Mouse2 (middle button), Mouse0 (left), F, or a chord like Ctrl+Mouse2. " +
                             "Default: Mouse2 (middle mouse).");

            TerrainUndoKey = _root.CreateEntry(
                "TerrainUndoKey", "Ctrl+Z",
                display_name: "Undo Key",
                description: "Key/chord that undoes the last terrain stroke. Default: Ctrl+Z.");

            MelonLogger.Msg("[DivineHands] Config initialized");
        }
    }
}
