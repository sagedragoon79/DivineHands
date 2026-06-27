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

        /// <summary>ENABLE/AVAILABLE switch for Reveal Map. When true, the Reveal Map control appears
        /// in the in-game panel and its (no) sliders reveal in KC. It does NOT activate the effect —
        /// the live ON/OFF is a runtime flag (<see cref="Modules.GodTools.RevealActive"/>) toggled in
        /// the panel and reset to false on every map load.</summary>
        public static MelonPreferences_Entry<bool>   EnableRevealMap   { get; private set; } = null!;

        /// <summary>ENABLE/AVAILABLE switch for Build Anywhere. When true, the Build Anywhere control
        /// appears in the in-game panel. The live ON/OFF is a runtime flag
        /// (<see cref="Patches.BuildAnywherePatches.Active"/>), reset to false on every map load.
        /// When active it bypasses placement/pathing validity for NORMAL buildings (steep slope, no
        /// path to town, overlaps). Bridges are NOT affected — they defer to vanilla + KC Bridge Anywhere.</summary>
        public static MelonPreferences_Entry<bool>   EnableBuildAnywhere  { get; private set; } = null!;

        /// <summary>ENABLE/AVAILABLE switch for God View. When true, the God View control appears in
        /// the in-game panel and its sliders reveal in KC. The live ON/OFF is a runtime flag
        /// (<see cref="Modules.CameraTools.GodViewActive"/>), reset to false on every map load. When
        /// active it relaxes the RTS camera constraints (zoom far out, flatten/overhead pitch, raise
        /// shadow draw distance), capturing the map's current values on enable and restoring on disable.</summary>
        public static MelonPreferences_Entry<bool>   EnableGodView        { get; private set; } = null!;

        /// <summary>Proportional God-View zoom: while God View is on, make mouse-scroll zoom steps finer
        /// as you zoom in close (the wide god-view range otherwise makes each notch coarse near the
        /// ground). Off = vanilla flat step. Implemented as a Harmony prefix on CameraManager.AdjustZoom,
        /// gated on the live zoomUnlocked flag, so vanilla zoom is untouched when God View is off.</summary>
        public static MelonPreferences_Entry<bool>   ProportionalZoom { get; private set; } = null!;

        /// <summary>God-View zoom step in CELLS per mouse-wheel notch (2–50; 1 cell ≈ 5 m). Higher = bigger
        /// jumps per notch (coarser). Close-in steps taper ~2x finer than far-out. God View + Proportional Zoom only.</summary>
        public static MelonPreferences_Entry<int>    ZoomCellsPerNotch { get; private set; } = null!;

        /// <summary>ENABLE/AVAILABLE switch for Free Cam. When true, the Free Cam control appears in
        /// the in-game panel and its sliders/hotkey reveal in KC. The live ON/OFF is a runtime flag
        /// (<see cref="Modules.CameraTools.FreeCamActive"/>), reset to false on every map load and
        /// toggled by the panel control OR the <see cref="FreeCamHotkey"/> chord (default Ctrl+F).
        /// When active it detaches the camera from RTS control and flies it manually (WASD horizontal,
        /// Space/LeftCtrl up/down, Shift fast, mouse-look).</summary>
        public static MelonPreferences_Entry<bool>   EnableFreeCam        { get; private set; } = null!;

        /// <summary>Hotkey chord that toggles Free Cam on/off in-game (parsed by <see cref="Hotkey"/>).
        /// Default Ctrl+F. The keyboard works even while the cursor is locked for mouse-look, so this is
        /// always the escape out of Free Cam — no soft-lock.</summary>
        public static MelonPreferences_Entry<string> FreeCamHotkey        { get; private set; } = null!;

        /// <summary>Free Cam horizontal/vertical fly speed in world metres per second.</summary>
        public static MelonPreferences_Entry<float>  FreeCamMoveSpeed { get; private set; } = null!;

        /// <summary>Free Cam speed multiplier while holding Shift.</summary>
        public static MelonPreferences_Entry<float>  FreeCamFastMultiplier { get; private set; } = null!;

        /// <summary>Free Cam mouse-look sensitivity.</summary>
        public static MelonPreferences_Entry<float>  FreeCamSensitivity { get; private set; } = null!;

        /// <summary>When true, Free Cam can't descend below the terrain surface (+clearance) — stops the
        /// camera clipping through the world into the backface/sky void. Off lets you fly under the map.</summary>
        public static MelonPreferences_Entry<bool>   FreeCamGroundFloor { get; private set; } = null!;

        /// <summary>Metres the Free Cam floor sits ABOVE the terrain surface. Small = true ground-level
        /// skim shots; 0 = right on the surface (may clip the near plane).</summary>
        public static MelonPreferences_Entry<float>  FreeCamFloorClearance { get; private set; } = null!;

        // ===== Terrain Sculpting =====

        /// <summary>Master switch for the terrain-elevation god-power.</summary>
        public static MelonPreferences_Entry<bool>   TerrainEnable   { get; private set; } = null!;

        /// <summary>Brush mode: 0=Raise, 1=Lower, 2=Smooth, 3=Flatten (cast to
        /// <see cref="Modules.TerrainElevation.Mode"/>).</summary>
        public static MelonPreferences_Entry<int>    TerrainMode     { get; private set; } = null!;

        /// <summary>Raise/Lower height delta (world metres) per stroke; also the Smooth step.</summary>
        public static MelonPreferences_Entry<float>  TerrainStrength { get; private set; } = null!;

        /// <summary>Brush footprint WIDTH (X / columns) in heightmap cells (1–10). Adjust live with the
        /// Left/Right arrows while the brush is armed. Each cell ≈ terrain Resolution metres (~5 m).</summary>
        public static MelonPreferences_Entry<int>    TerrainGridWidth  { get; private set; } = null!;

        /// <summary>Brush footprint DEPTH (Z / rows) in heightmap cells (1–10). Adjust live with the
        /// Up/Down arrows while the brush is armed. Tab swaps width and depth.</summary>
        public static MelonPreferences_Entry<int>    TerrainGridHeight { get; private set; } = null!;

        /// <summary>Fine grid positioning: the grid overlay snaps to HALF-cell steps so you can square up
        /// free-build buildings (TerrainHelper-style). Placement guide — the sculpt still resolves to the
        /// nearest whole heightmap cells on apply. Default off.</summary>
        public static MelonPreferences_Entry<bool>   TerrainGridFineSnap { get; private set; } = null!;

        // ===== Lake / Pond stamp (mirrors Pangu) =====
        public static MelonPreferences_Entry<bool>   LakeEnable     { get; private set; } = null!;
        public static MelonPreferences_Entry<int>    LakeShape      { get; private set; } = null!; // 0 = Rectangle, 1 = Circle
        public static MelonPreferences_Entry<int>    LakeGridWidth  { get; private set; } = null!; // core brush half-extent X (cells)
        public static MelonPreferences_Entry<int>    LakeGridHeight { get; private set; } = null!; // core brush half-extent Z (cells)
        public static MelonPreferences_Entry<float>  LakeFillRatio  { get; private set; } = null!;
        public static MelonPreferences_Entry<float>  LakeCarveDepth { get; private set; } = null!;
        public static MelonPreferences_Entry<float>  LakeShoreWidth { get; private set; } = null!;
        public static MelonPreferences_Entry<float>  LakeNoGoWidth  { get; private set; } = null!;
        public static MelonPreferences_Entry<string> LakeArmHotkey  { get; private set; } = null!;
        public static MelonPreferences_Entry<string> LakeApplyKey   { get; private set; } = null!;

        /// <summary>Keyboard hotkey that arms/disarms the Terrain tool without clicking its tab (opens
        /// the panel when arming). Default End — matches TerrainHelper muscle memory.</summary>
        public static MelonPreferences_Entry<string> TerrainArmHotkey { get; private set; } = null!;

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

        /// <summary>Keyboard hotkey that arms/disarms the Spawner tool without clicking its tab (opens
        /// the panel when arming). Default Home.</summary>
        public static MelonPreferences_Entry<string> SpawnerArmHotkey { get; private set; } = null!;

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

            EnableRevealMap = _root.CreateEntry(
                "EnableRevealMap", false,
                display_name: "Reveal Map",
                description: "Make Reveal Map AVAILABLE in the in-game panel. Enabling here does NOT clear " +
                             "fog — you activate it in-game from the Divine Hands panel (God Tools). " +
                             "Reveal clears the entire fog of war; toggling it off best-effort restores the " +
                             "fog you'd explored. Caveat: FF bakes explored state into the SAVE — if you save " +
                             "while revealed, the whole map stays explored. Default: off.");

            EnableBuildAnywhere = _root.CreateEntry(
                "EnableBuildAnywhere", false,
                display_name: "Build Anywhere",
                description: "Make Build Anywhere AVAILABLE in the in-game panel. Enabling here does NOT " +
                             "change placement — you activate it in-game from the Divine Hands panel. " +
                             "When active it lets you place NORMAL buildings on ground vanilla would reject " +
                             "(steep slopes, no path to town, water/road overlap). Bridges are NOT affected — " +
                             "they defer to vanilla and Keep Clarity's Bridge Anywhere. Default: off.");

            EnableGodView = _root.CreateEntry(
                "EnableGodView", false,
                display_name: "God View",
                description: "Make God View AVAILABLE in the in-game panel. Enabling here does NOT move the " +
                             "camera — you activate it in-game from the Divine Hands panel. When active it " +
                             "relaxes the RTS camera limits so you can zoom far out, tilt to a flat/overhead " +
                             "angle, and survey the whole map, restoring exactly when turned off. Default: off.");

            ProportionalZoom = _root.CreateEntry(
                "ProportionalZoom", true,
                display_name: "Proportional God-View Zoom",
                description: "While God View is on, make mouse-scroll zoom steps finer as you zoom in close " +
                             "(the wide god-view range otherwise jumps from normal to too-close with nothing " +
                             "between). Far-out zoom stays near vanilla. Off = flat vanilla step. Default: on.");

            ZoomCellsPerNotch = _root.CreateEntry(
                "ZoomCellsPerNotch", 10,
                display_name: "Zoom Step (God View)",
                description: "How far one mouse-wheel notch moves the god-view camera, in CELLS (2–50; " +
                             "1 cell ≈ 5 m, so 2 = ~10 m/notch, 50 = ~250 m/notch). Higher = bigger jumps " +
                             "(coarser); lower = finer. Close-in notches taper ~2x finer than far-out. " +
                             "Default: 10 (~50 m/notch, roughly vanilla).");

            EnableFreeCam = _root.CreateEntry(
                "EnableFreeCam", false,
                display_name: "Free Cam",
                description: "Make Free Cam AVAILABLE in the in-game panel. Enabling here does NOT detach " +
                             "the camera — you activate it in-game. Press Ctrl+F (configurable) to enter/exit, " +
                             "or use the panel toggle. WASD move, Space/Ctrl up/down, Shift fast, mouse look. " +
                             "Exiting restores the normal camera and full RTS control exactly. Default: off.");

            FreeCamHotkey = _root.CreateEntry(
                "FreeCamHotkey", "Ctrl+F",
                display_name: "Free Cam Hotkey",
                description: "Key/chord that toggles Free Cam on/off in-game. A Unity KeyCode name or a chord " +
                             "with Ctrl/Alt/Shift — e.g. Ctrl+F, F8, Alt+C. Default: Ctrl+F.");

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

            FreeCamGroundFloor = _root.CreateEntry(
                "FreeCamGroundFloor", true,
                display_name: "Free Cam Ground Floor",
                description: "Keep Free Cam above the terrain surface so it can't clip through the world " +
                             "into the backface/sky void. Turn OFF for under-the-map shots. Default: on.");

            FreeCamFloorClearance = _root.CreateEntry(
                "FreeCamFloorClearance", 1.0f,
                display_name: "Free Cam Floor Clearance",
                description: "Metres the ground floor sits above the terrain surface. Small values let you " +
                             "skim the ground for low cinematic shots; 0 rides the surface (may clip the " +
                             "near plane). Default: 1.0 m.");

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
                description: "0 = Raise, 1 = Lower, 2 = Smooth, 3 = Flatten, 4 = Average. Hold Shift to " +
                             "invert Raise<->Lower. (Average = creep the whole brush toward one flat mean " +
                             "level, TerrainHelper-style; Smooth only de-bumps and keeps slopes.) Default: Raise.");

            TerrainStrength = _root.CreateEntry(
                "TerrainStrength", 1.0f,
                display_name: "Brush Strength",
                description: "Raise/Lower height change in world metres per application (also the Smooth " +
                             "and Average step size). Flatten ignores this — it sets the brush to the " +
                             "cursor's height. Default: 1.0.");

            TerrainGridWidth = _root.CreateEntry(
                "TerrainGridWidth", 3,
                display_name: "Brush Grid Width",
                description: "Brush footprint width (X / columns) in heightmap cells (1–10). Adjust live " +
                             "with Left/Right arrows while the brush is armed. Each cell is ~Resolution " +
                             "metres (~5 m). Default: 3.");

            TerrainGridHeight = _root.CreateEntry(
                "TerrainGridHeight", 3,
                display_name: "Brush Grid Depth",
                description: "Brush footprint depth (Z / rows) in heightmap cells (1–10). Adjust live with " +
                             "Up/Down arrows while the brush is armed; Tab swaps width and depth. " +
                             "Use e.g. 1×10 to carve a path. Default: 3.");

            TerrainGridFineSnap = _root.CreateEntry(
                "TerrainGridFineSnap", false,
                display_name: "Fine Grid Positioning",
                description: "When on, the grid overlay snaps to HALF-cell steps so you can square up " +
                             "free-build buildings (TerrainHelper-style). It's a placement guide — the " +
                             "sculpt still resolves to the nearest whole heightmap cells on apply, so for " +
                             "crisp flat pads leave this off. Default: off.");

            // ===== Lake / Pond stamp =====
            LakeEnable = _root.CreateEntry("LakeEnable", false,
                display_name: "Lake / Pond Stamp",
                description: "Make the Lake stamp available (adds its tab to the in-game panel). Stamps a " +
                             "carved, water-filled lake at the cursor (mirrors Pangu). Default: off.");
            LakeShape = _root.CreateEntry("LakeShape", 0,
                display_name: "Lake Shape", description: "0 = Rectangle, 1 = Circle.");
            LakeGridWidth = _root.CreateEntry("LakeGridWidth", 5,
                display_name: "Lake Width", description: "Core footprint half-width (X) in cells (1–10). Arrow keys resize while armed.");
            LakeGridHeight = _root.CreateEntry("LakeGridHeight", 5,
                display_name: "Lake Depth (cells)", description: "Core footprint half-depth (Z) in cells (1–10). Arrow keys resize while armed.");
            LakeFillRatio = _root.CreateEntry("LakeFillRatio", 1.3f,
                display_name: "Fill Ratio", description: "Multiplies the footprint half-extents (1.0–2.0). Default: 1.3.");
            LakeCarveDepth = _root.CreateEntry("LakeCarveDepth", 4.6f,
                display_name: "Carve Depth", description: "How far the lake bed sits below the water plane, world metres (0.4–12). Default: 4.6.");
            LakeShoreWidth = _root.CreateEntry("LakeShoreWidth", 16f,
                display_name: "Shore Blend", description: "Outer-blend ring width that ramps the banks back up to land (2–40). Default: 16.");
            LakeNoGoWidth = _root.CreateEntry("LakeNoGoWidth", 7f,
                display_name: "No-Go Width", description: "Flat pad added around the footprint (0–24). Default: 7.");
            LakeArmHotkey = _root.CreateEntry("LakeArmHotkey", "Insert",
                display_name: "Lake Arm Hotkey", description: "Key/chord that arms (re-press disarms) the Lake stamp. Default: Insert.");
            LakeApplyKey = _root.CreateEntry("LakeApplyKey", "Ctrl+Mouse1",
                display_name: "Lake Apply Key", description: "Key/button that stamps the lake at the cursor. Default: Ctrl+Mouse1 (Ctrl + right-click).");

            TerrainArmHotkey = _root.CreateEntry(
                "TerrainArmHotkey", "End",
                display_name: "Terrain Arm Hotkey",
                description: "Keyboard key/chord that arms (and re-press disarms) the Terrain brush without " +
                             "clicking its tab — opens the panel when arming. A Unity KeyCode name or chord " +
                             "(e.g. End, Home, F6, Ctrl+T). Default: End (TerrainHelper convention).");

            TerrainApplyKey = _root.CreateEntry(
                "TerrainApplyKey", "Ctrl+Mouse1",
                display_name: "Apply Key",
                description: "Key/button that applies the brush at the cursor. A Unity KeyCode name or a chord " +
                             "— e.g. Ctrl+Mouse1 (Ctrl + right-click), Mouse2 (middle), Mouse0 (left), F. " +
                             "Default: Ctrl+Mouse1 (Ctrl + right-click — prevents accidental application).");

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
                description: "Index within the family. Animal: 0 Deer/1 Bear/2 Boar/3 Wolf/4 Fox/" +
                             "5 Groundhog/6 Dog/7 Cat (4-7 need the Cats & Dogs DLC). " +
                             "Mineral: 0 Gold/1 Iron/2 Coal/3 Stone/4 Clay/5 Sand. " +
                             "Resource: 0 Forageable/1 Tree/2 Rock/3 Boulder. Villager ignores this.");

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
                display_name: "Persistent (nodes/dens)",
                description: "Animals only. When on (default): Deer drop a self-respawning spawn-area node " +
                             "at the cursor (circular marker); Wolf/Boar drop a den. When off, they spawn loose. " +
                             "Bear is always loose (FF has no bear node). Wolf/Boar dens persist through " +
                             "save/load; the deer node respawns this session but is NOT serialized (re-drop " +
                             "after a reload). Loose animals are runtime-only.");

            SpawnWolfDenGuid = _root.CreateEntry(
                "SpawnWolfDenGuid", "465936e7-d613-4d08-af70-147fe603715f",
                display_name: "Wolf Den GUID (fallback)",
                description: "Optional fallback wolf-den prefab GUID, used ONLY if the animal group's weighted " +
                             "den prefab can't be resolved. Leave default unless dens fail to spawn.");

            SpawnerArmHotkey = _root.CreateEntry(
                "SpawnerArmHotkey", "Home",
                display_name: "Spawner Arm Hotkey",
                description: "Keyboard key/chord that arms (and re-press disarms) the Spawner without clicking " +
                             "its tab — opens the panel when arming. A Unity KeyCode name or chord (e.g. Home, " +
                             "End, F7, Ctrl+B). Default: Home.");

            SpawnApplyKey = _root.CreateEntry(
                "SpawnApplyKey", "Ctrl+Mouse1",
                display_name: "Spawn Apply Key",
                description: "Key/button that spawns the selected family at the cursor. A Unity KeyCode name " +
                             "(Mouse2 = middle, Mouse0 = left, Mouse1 = right) or a chord like Ctrl+Mouse1. " +
                             "Default: Ctrl+Mouse1 (Ctrl + right-click — prevents accidental spawning).");

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
                display_name: "Boulder GUIDs",
                description: "Delimited prefab GUIDs for the Boulder type.");

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
