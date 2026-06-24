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

        /// <summary>God View: relax the RTS camera constraints (zoom far out, flatten/overhead pitch,
        /// raise shadow draw distance) to survey the whole map. Live toggle — captures the map's
        /// current constraint values on enable and restores them exactly on disable.</summary>
        public static MelonPreferences_Entry<bool>   GodView        { get; private set; } = null!;

        /// <summary>Proportional God-View zoom: while God View is on, make mouse-scroll zoom steps finer
        /// as you zoom in close (the wide god-view range otherwise makes each notch coarse near the
        /// ground). Off = vanilla flat step. Implemented as a Harmony prefix on CameraManager.AdjustZoom,
        /// gated on the live zoomUnlocked flag, so vanilla zoom is untouched when God View is off.</summary>
        public static MelonPreferences_Entry<bool>   ProportionalZoom { get; private set; } = null!;

        /// <summary>Close-in zoom fineness (0.02–1.0) as a fraction of the vanilla step. Lower = finer
        /// steps near the ground; far-out zoom stays near vanilla. Used only with God View + Proportional Zoom.</summary>
        public static MelonPreferences_Entry<float>  ZoomStepScale  { get; private set; } = null!;

        /// <summary>Free Cam: detach the camera from RTS control and fly it manually (WASD horizontal,
        /// Space/LeftCtrl up/down, Shift fast, mouse-look). Live toggle — captures camera transform +
        /// controller state on enable and restores full RTS control on disable.</summary>
        public static MelonPreferences_Entry<bool>   FreeCam        { get; private set; } = null!;

        /// <summary>Free Cam horizontal/vertical fly speed in world metres per second.</summary>
        public static MelonPreferences_Entry<float>  FreeCamMoveSpeed { get; private set; } = null!;

        /// <summary>Free Cam speed multiplier while holding Shift.</summary>
        public static MelonPreferences_Entry<float>  FreeCamFastMultiplier { get; private set; } = null!;

        /// <summary>Free Cam mouse-look sensitivity.</summary>
        public static MelonPreferences_Entry<float>  FreeCamSensitivity { get; private set; } = null!;

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

        /// <summary>Animals only: when on (default), spawn PERSISTENT, self-respawning wildlife —
        /// Deer place a spawn-AREA node, Wolf/Boar place a DEN. When off, spawn loose one-off animals
        /// (the legacy DebugSpawn*AtPoint path). Bear is ALWAYS loose regardless (no base-game bear den).</summary>
        public static MelonPreferences_Entry<bool>   SpawnPersistent { get; private set; } = null!;

        /// <summary>Optional fallback wolf-den prefab GUID, used ONLY if the AnimalGroupDefinition's
        /// GetWeightedDenPrefab() returns null (e.g. a misconfigured/DLC group). Leave default unless
        /// dens fail to spawn.</summary>
        public static MelonPreferences_Entry<string> SpawnWolfDenGuid { get; private set; } = null!;

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

        // ===== Item Injection (selected building) =====

        /// <summary>Master switch for the Item Injection god-power (add items/livestock + infinite
        /// storage on the selected building).</summary>
        public static MelonPreferences_Entry<bool>   InjectEnable     { get; private set; } = null!;

        /// <summary>Index into <see cref="Modules.ItemInjection.ItemNames"/> — which item the
        /// Add-Items button injects.</summary>
        public static MelonPreferences_Entry<int>    InjectItemIndex  { get; private set; } = null!;

        /// <summary>How many of the selected item to add per click (1–9999).</summary>
        public static MelonPreferences_Entry<int>    InjectItemCount  { get; private set; } = null!;

        /// <summary>Livestock kind for the Add-Livestock button: 0=Cow, 1=Chicken, 2=Goat, 3=Horse
        /// (<see cref="Modules.ItemInjection.LivestockKind"/>).</summary>
        public static MelonPreferences_Entry<int>    InjectLivestockKind { get; private set; } = null!;

        /// <summary>Cow prefab GUID (Barn). User-editable so a DLC/renamed prefab can be pointed at.</summary>
        public static MelonPreferences_Entry<string> LivestockGuidCow     { get; private set; } = null!;

        /// <summary>Chicken prefab GUID (ChickenCoop).</summary>
        public static MelonPreferences_Entry<string> LivestockGuidChicken { get; private set; } = null!;

        /// <summary>Goat prefab GUID (GoatBarn).</summary>
        public static MelonPreferences_Entry<string> LivestockGuidGoat    { get; private set; } = null!;

        /// <summary>Horse prefab GUID (Stable).</summary>
        public static MelonPreferences_Entry<string> LivestockGuidHorse   { get; private set; } = null!;

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

            GodView = _root.CreateEntry(
                "GodView", false,
                display_name: "God View",
                description: "Relax the RTS camera limits so you can zoom far out, tilt to a flat/overhead " +
                             "angle, and survey the whole map. Captures the map's current camera limits when " +
                             "turned ON and restores them exactly when turned OFF. Default: off.");

            ProportionalZoom = _root.CreateEntry(
                "ProportionalZoom", true,
                display_name: "Proportional God-View Zoom",
                description: "While God View is on, make mouse-scroll zoom steps finer as you zoom in close " +
                             "(the wide god-view range otherwise jumps from normal to too-close with nothing " +
                             "between). Far-out zoom stays near vanilla. Off = flat vanilla step. Default: on.");

            ZoomStepScale = _root.CreateEntry(
                "ZoomStepScale", 0.4f,
                display_name: "Zoom Fineness (close-in)",
                description: "How fine the zoom steps get when zoomed in close, as a fraction of the vanilla " +
                             "step (0.4 ≈ 40% = ~2.5x finer near the ground). Lower = finer. Far-out zoom is " +
                             "unaffected. Range 0.02–1.0. Default: 0.4.");

            FreeCam = _root.CreateEntry(
                "FreeCam", false,
                display_name: "Free Cam",
                description: "Detach the camera from RTS control and fly it manually: WASD to move, " +
                             "Space/Left-Ctrl for up/down, hold Shift for fast, move the mouse to look. " +
                             "Turning it OFF restores the normal camera and full RTS control exactly where " +
                             "you left off. Default: off.");

            FreeCamMoveSpeed = _root.CreateEntry(
                "FreeCamMoveSpeed", 40f,
                display_name: "Free Cam Move Speed",
                description: "Free Cam fly speed in world metres per second. Default: 40.");

            FreeCamFastMultiplier = _root.CreateEntry(
                "FreeCamFastMultiplier", 3f,
                display_name: "Free Cam Fast Multiplier",
                description: "Speed multiplier while holding Shift in Free Cam. Default: 3.");

            FreeCamSensitivity = _root.CreateEntry(
                "FreeCamSensitivity", 2f,
                display_name: "Free Cam Mouse Sensitivity",
                description: "Free Cam mouse-look sensitivity. Default: 2.");

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

            SpawnPersistent = _root.CreateEntry(
                "SpawnPersistent", true,
                display_name: "Persistent (node/den)",
                description: "Animals only. When on (default), spawned wildlife is PERSISTENT and self-respawns: " +
                             "Deer create a spawn-area node, Wolf/Boar create a den. When off, animals spawn loose " +
                             "(one-off, won't survive save/load). Bear is always loose (the game has no bear dens).");

            SpawnWolfDenGuid = _root.CreateEntry(
                "SpawnWolfDenGuid", "465936e7-d613-4d08-af70-147fe603715f",
                display_name: "Wolf Den GUID (fallback)",
                description: "Optional fallback wolf-den prefab GUID, used ONLY if the animal group's weighted " +
                             "den prefab can't be resolved. Leave default unless dens fail to spawn.");

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
                display_name: "Tree GUIDs (optional override)",
                description: "Delimited prefab GUIDs for the Tree type (placed via the terrain grow-tree " +
                             "system). Leave EMPTY (default) to auto-use the current map's own tree species " +
                             "(Terrain2 TreePrototypes) — no setup needed. Fill in GUIDs only to override " +
                             "which trees are planted.");

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

            // ===== Item Injection (selected building) =====

            InjectEnable = _root.CreateEntry(
                "InjectEnable", false,
                display_name: "Item Injection",
                description: "Enable item injection on the selected building. With it on, select a " +
                             "building in-game, then use the panel's Selected Building section to add " +
                             "items, add livestock, or toggle infinite storage. Default: off.");

            InjectItemIndex = _root.CreateEntry(
                "InjectItemIndex", 0,
                display_name: "Item to Add",
                description: "Which item the Add Items button injects into the selected building's " +
                             "storage (index into the panel's item list). Default: first item.");

            InjectItemCount = _root.CreateEntry(
                "InjectItemCount", 100,
                display_name: "Item Count",
                description: "How many of the selected item to add per click (1–9999). Default: 100.");

            InjectLivestockKind = _root.CreateEntry(
                "InjectLivestockKind", 0,
                display_name: "Livestock to Add",
                description: "0 = Cow (Barn), 1 = Chicken (Coop), 2 = Goat (Goat Barn), 3 = Horse " +
                             "(Stable). The selected building must match the animal. Default: Cow.");

            // Default GUIDs from the working AddItemMono reference; user-overridable for DLC/renames.
            LivestockGuidCow = _root.CreateEntry(
                "LivestockGuidCow", "7b65f80b-c40a-4485-84ab-69cb1332ca55",
                display_name: "Cow Prefab GUID",
                description: "Prefab GUID instantiated when adding a Cow to a Barn. Editable in case a " +
                             "DLC or game update changes it. Unknown GUIDs are skipped safely.");

            LivestockGuidChicken = _root.CreateEntry(
                "LivestockGuidChicken", "c5c32c43-4bc4-459b-80af-2cb82c41ed81",
                display_name: "Chicken Prefab GUID",
                description: "Prefab GUID instantiated when adding a Chicken to a Chicken Coop.");

            LivestockGuidGoat = _root.CreateEntry(
                "LivestockGuidGoat", "b5924130-f05c-4eb6-bd10-3271137d8b24",
                display_name: "Goat Prefab GUID",
                description: "Prefab GUID instantiated when adding a Goat to a Goat Barn.");

            LivestockGuidHorse = _root.CreateEntry(
                "LivestockGuidHorse", "4d656c72-240b-4326-bba0-25d36268ca9c",
                display_name: "Horse Prefab GUID",
                description: "Prefab GUID instantiated when adding a Horse to a Stable.");

            MelonLogger.Msg("[DivineHands] Config initialized");
        }
    }
}
