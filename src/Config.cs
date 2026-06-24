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

        /// <summary>Build Anywhere: bypass placement/pathing validity for NORMAL buildings so they
        /// can be placed on otherwise-invalid ground (steep slope, no path to town, overlaps). Bridge
        /// placements are deliberately NOT affected — they defer to vanilla + Keep Clarity's Bridge
        /// Anywhere. Off => pure vanilla.</summary>
        public static MelonPreferences_Entry<bool>   BuildAnywhere  { get; private set; } = null!;

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

        // ===== Cursor Spawners =====

        /// <summary>Master switch for the cursor-spawner god-power.</summary>
        public static MelonPreferences_Entry<bool>   SpawnEnable    { get; private set; } = null!;

        /// <summary>Family: 0=Animal, 1=Mineral, 2=Villager, 3=Resource
        /// (<see cref="Modules.CursorSpawners.Family"/>).</summary>
        public static MelonPreferences_Entry<int>    SpawnFamily    { get; private set; } = null!;

        /// <summary>Sub-type index within the selected family (Animal: Deer/Bear/Boar/Wolf;
        /// Mineral: Gold/Iron/Coal/Stone/Clay/Sand; Resource: Forageable/Tree/Rock/GiantRock).
        /// Ignored for Villager.</summary>
        public static MelonPreferences_Entry<int>    SpawnSubtype   { get; private set; } = null!;

        /// <summary>How many to spawn per apply (1–50).</summary>
        public static MelonPreferences_Entry<int>    SpawnCount     { get; private set; } = null!;

        /// <summary>Minerals only: gold/iron/coal spawn as a deep (infinite) deposit when true.
        /// Stone/clay/sand are always infinite pits regardless.</summary>
        public static MelonPreferences_Entry<bool>   SpawnIsDeep    { get; private set; } = null!;

        /// <summary>Villager spawns: fire the immigration "arrived" announcement when true.</summary>
        public static MelonPreferences_Entry<bool>   SpawnAnnounceVillagers { get; private set; } = null!;

        /// <summary>Key/button that spawns the selected family at the cursor.</summary>
        public static MelonPreferences_Entry<string> SpawnApplyKey  { get; private set; } = null!;

        /// <summary>Delimited prefab GUIDs (comma/space/newline) used for the Forageable resource type.</summary>
        public static MelonPreferences_Entry<string> SpawnForageableGuids { get; private set; } = null!;

        /// <summary>Delimited prefab GUIDs for the Tree resource type.</summary>
        public static MelonPreferences_Entry<string> SpawnTreeGuids { get; private set; } = null!;

        /// <summary>Delimited prefab GUIDs for the Rock resource type.</summary>
        public static MelonPreferences_Entry<string> SpawnRockGuids { get; private set; } = null!;

        /// <summary>Delimited prefab GUIDs for the Giant-Rock resource type.</summary>
        public static MelonPreferences_Entry<string> SpawnGiantRockGuids { get; private set; } = null!;

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

            BuildAnywhere = _root.CreateEntry(
                "BuildAnywhere", false,
                display_name: "Build Anywhere",
                description: "Lets you place NORMAL buildings on ground vanilla would reject (steep " +
                             "slopes, no path to town, water/road overlap). Bridges are NOT affected — " +
                             "they defer to vanilla and Keep Clarity's Bridge Anywhere. Turning this OFF " +
                             "restores exact vanilla placement rules. Default: off.");

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

            // ===== Cursor Spawners =====

            SpawnEnable = _root.CreateEntry(
                "SpawnEnable", false,
                display_name: "Cursor Spawners",
                description: "Enable the cursor-spawner god-power. With it on, pick a family in the panel, " +
                             "arm the spawner, and press the apply key over the world to place things at the " +
                             "cursor. Default: off.");

            SpawnFamily = _root.CreateEntry(
                "SpawnFamily", 0,
                display_name: "Spawn Family",
                description: "0 = Animal, 1 = Mineral, 2 = Villager, 3 = Resource. Default: Animal.");

            SpawnSubtype = _root.CreateEntry(
                "SpawnSubtype", 0,
                display_name: "Spawn Sub-type",
                description: "Index within the family. Animal: 0 Deer/1 Bear/2 Boar/3 Wolf. " +
                             "Mineral: 0 Gold/1 Iron/2 Coal/3 Stone/4 Clay/5 Sand. " +
                             "Resource: 0 Forageable/1 Tree/2 Rock/3 Giant Rock. Villager ignores this.");

            SpawnCount = _root.CreateEntry(
                "SpawnCount", 1,
                display_name: "Spawn Count",
                description: "How many to place per apply (1–50). Spawns are scattered in a small ring " +
                             "around the cursor so they don't stack. Default: 1.");

            SpawnIsDeep = _root.CreateEntry(
                "SpawnIsDeep", false,
                display_name: "Deep Deposit (gold/iron/coal)",
                description: "Minerals only. When on, gold/iron/coal spawn as a DEEP (effectively " +
                             "infinite) deposit. Stone/clay/sand always spawn as infinite pits regardless. " +
                             "Default: off.");

            SpawnAnnounceVillagers = _root.CreateEntry(
                "SpawnAnnounceVillagers", false,
                display_name: "Announce Villagers",
                description: "When spawning villagers, fire the usual immigration 'arrived' notification. " +
                             "Default: off (silent).");

            SpawnApplyKey = _root.CreateEntry(
                "SpawnApplyKey", "Mouse2",
                display_name: "Spawn Apply Key",
                description: "Key/button that spawns the selected family at the cursor. A Unity KeyCode name " +
                             "(Mouse2 = middle, Mouse0 = left) or a chord. Default: Mouse2.");

            SpawnForageableGuids = _root.CreateEntry(
                "SpawnForageableGuids",
                "962a696f-f93a-453e-88f6-83a6e8be967a,e4872b2f-b93d-4fb7-a8d2-c4679567a9bc," +
                "12581ec8-a557-450d-bf91-551a3c9a647b,35ac6f63-b9bd-4ebe-8f18-78ddfaf55c00," +
                "a4a0d3c2-e75b-45b0-89fe-2fad33319a3b,ece68f3e-d747-44af-819c-f81dc8318d5c," +
                "53cf417f-fc86-492c-b8cf-1c363725d3b8,203d0522-48f1-4f26-a1f2-a57cbc42c325," +
                "c0041984-26b7-4046-8d5b-a614ae395d7f,66e9363f-8b4f-4d14-a1ad-6990af6f8c96," +
                "18bef916-4cb1-4fde-aa52-2230ef762fba",
                display_name: "Forageable GUIDs",
                description: "Comma/space/newline-delimited prefab GUIDs spawned for the Forageable type. " +
                             "Each apply cycles through the list. Unknown/DLC GUIDs are skipped safely.");

            SpawnTreeGuids = _root.CreateEntry(
                "SpawnTreeGuids", "",
                display_name: "Tree GUIDs",
                description: "Delimited prefab GUIDs for the Tree type (placed via the terrain grow-tree " +
                             "system). Fill in your tree prefab GUIDs. Empty by default.");

            SpawnRockGuids = _root.CreateEntry(
                "SpawnRockGuids",
                "b65637b1-f040-4c17-b2d8-3345c6c73ff1,f8fb5157-0f36-4414-8cb8-0de1b6134ee5," +
                "5ae3e60a-7f2d-47ca-b58b-e0b67983f48a,0b8bf7ad-3494-42c0-a98a-562a2822d5c5",
                display_name: "Rock GUIDs",
                description: "Delimited prefab GUIDs for the Rock type. Each apply cycles the list.");

            SpawnGiantRockGuids = _root.CreateEntry(
                "SpawnGiantRockGuids", "51310685-9865-4e53-b294-f57dd0d086dc",
                display_name: "Giant Rock GUIDs",
                description: "Delimited prefab GUIDs for the Giant Rock type.");

            MelonLogger.Msg("[DivineHands] Config initialized");
        }
    }
}
