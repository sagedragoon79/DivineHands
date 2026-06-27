using System;
using System.Reflection;
using MelonLoader;

namespace DivineHands
{
    /// <summary>
    /// Optional integration with Keep Clarity's settings panel. If KeepClarity.dll isn't
    /// installed, every method here is a no-op and Divine Hands runs unchanged (prefs still
    /// readable from the MelonPreferences cfg). If KC IS installed, our prefs render with rich
    /// labels, tooltips, sliders, per-group sections, and VisibleWhen gating.
    ///
    /// All access to Keep Clarity is reflective — no compile-time reference — so this file ships
    /// standalone without adding KeepClarity.dll as a hard build dependency. The magic strings
    /// "FFUIOverhaul.Settings.SettingsAPI, KeepClarity" and ".SettingsMeta, KeepClarity" are a
    /// load-bearing soft-dep contract; do not rename them. Canonical template:
    /// WardenOfTheWilds/KeepClarityIntegration.cs and SovereignBoons/src/KeepClarityIntegration.cs.
    /// </summary>
    internal static class KeepClarityIntegration
    {
        private static bool _resolved;
        private static bool _present;
        private static MethodInfo? _registerMod;
        private static MethodInfo? _registerEntry;
        private static Type? _settingsMetaType;

        private const string ModId = "DivineHands";
        private const string ModDisplayName = "Divine Hands";

        // Setting groups (the SettingsMeta.Group field).
        internal const string GroupGeneral  = "General";
        internal const string GroupGodTools = "God Tools";
        internal const string GroupTerrain  = "Terrain Sculpting";
        internal const string GroupSpawning = "Cursor Spawners";
        internal const string GroupInject   = "Selected Building";
        internal const string GroupLake     = "Lake Stamp";

        public static void TryRegisterAll()
        {
            if (!ResolveApi()) return;
            try
            {
                RegisterMod();
                RegisterEntries();
                MelonLogger.Msg("[DivineHands] Registered with Keep Clarity settings panel");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DivineHands] Keep Clarity registration failed: {ex.Message}");
            }
        }

        private static bool ResolveApi()
        {
            if (_resolved) return _present;
            _resolved = true;

            var apiType = Type.GetType("FFUIOverhaul.Settings.SettingsAPI, KeepClarity");
            if (apiType == null) { _present = false; return false; }
            _settingsMetaType = Type.GetType("FFUIOverhaul.Settings.SettingsMeta, KeepClarity");
            if (_settingsMetaType == null) { _present = false; return false; }

            _registerMod = apiType.GetMethod("RegisterMod", BindingFlags.Public | BindingFlags.Static);
            foreach (var m in apiType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "Register" && m.IsGenericMethodDefinition) { _registerEntry = m; break; }

            _present = _registerMod != null && _registerEntry != null;
            return _present;
        }

        private static void RegisterMod()
        {
            _registerMod!.Invoke(null, new object?[] {
                ModId,
                ModDisplayName,
                "God-power & creator tools: terrain sculpting, cursor spawners, and map god-tools " +
                "behind one in-game panel (default hotkey Ctrl+G). World-altering powers default OFF.",
                /*version*/ Plugin.Version,
                /*iconResourcePath*/ null,
                /*accentRgb — deep amethyst (divine theme)*/ new[] { 0.42f, 0.28f, 0.62f, 1f },
                /*order*/ 40
            });
        }

        internal static object NewMeta(string? label = null, string? tooltip = null,
            object? min = null, object? max = null, string? group = null,
            bool restartRequired = false, int order = 0, Func<bool>? visibleWhen = null,
            int indent = 0, bool reloadRequired = false)
        {
            var m = Activator.CreateInstance(_settingsMetaType!);
            void Set(string field, object? value)
            {
                var f = _settingsMetaType!.GetField(field);
                if (f != null) f.SetValue(m, value);
            }
            Set("Label", label);
            Set("Tooltip", tooltip);
            Set("Min", min);
            Set("Max", max);
            Set("Group", group);
            Set("RestartRequired", restartRequired);
            Set("ReloadRequired", reloadRequired);
            Set("Order", order);
            Set("VisibleWhen", visibleWhen);
            Set("Indent", indent);
            return m!;
        }

        internal static void Reg<T>(string group, MelonPreferences_Entry<T> entry, object meta)
        {
            if (!_present) return;
            var closed = _registerEntry!.MakeGenericMethod(typeof(T));
            closed.Invoke(null, new object?[] { ModId, ModDisplayName, group, entry, meta });
        }

        private static void RegisterEntries()
        {
            // ===== General =====
            Reg(GroupGeneral, Config.MasterEnable,
                NewMeta("Enabled",
                        "Master switch for Divine Hands. When off, the panel hotkey is inert and no " +
                        "god-power applies. Default: ON.",
                        order: 10));
            Reg(GroupGeneral, Config.PanelHotkey,
                NewMeta("Panel Hotkey",
                        "Key or chord that toggles the in-game Divine Hands panel. A Unity KeyCode name " +
                        "or a chord with Ctrl/Alt/Shift — e.g. Ctrl+G, F6, Alt+Shift+D. Default: Ctrl+G.",
                        order: 11, indent: 20));
            Reg(GroupGeneral, Config.ShowPanelOnMap,
                NewMeta("Open Panel on Map Load",
                        "Automatically open the panel when a map loads. Default: OFF.",
                        order: 12, indent: 20));
            Reg(GroupGeneral, Config.DebugLog,
                NewMeta("Debug Logging",
                        "Verbose diagnostic output to MelonLoader.log. Default: OFF.",
                        order: 13, indent: 20));

            // ===== God Tools =====
            // These Enable toggles make each power AVAILABLE in the in-game panel; you ACTIVATE it
            // there (its live ON/OFF is a runtime flag reset on every map load — never a saved pref).
            Reg(GroupGodTools, Config.EnableRevealMap,
                NewMeta("Reveal Map",
                        "Makes Reveal Map available in the in-game Divine Hands panel — you activate it " +
                        "there, not here. When activated it clears the entire fog of war; toggling it off " +
                        "best-effort restores the fog you'd explored. Caveat: FF serializes explored state " +
                        "into the save — if you save while revealed, the whole map stays explored. Default: OFF.",
                        order: 100));
            Reg(GroupGodTools, Config.EnableBuildAnywhere,
                NewMeta("Build Anywhere",
                        "Makes Build Anywhere available in the in-game panel — you activate it there, not " +
                        "here. When activated it lets you place NORMAL buildings on ground vanilla rejects " +
                        "(steep slopes, no path to town, water/road overlap). Bridges are left alone so they " +
                        "keep deferring to vanilla and Keep Clarity's Bridge Anywhere. Default: OFF.",
                        order: 110));
            Reg(GroupGodTools, Config.EnableGodView,
                NewMeta("God View",
                        "Makes God View available in the in-game panel — you activate it there, not here. " +
                        "When activated it relaxes the RTS camera limits — zoom far out, tilt to a flat/" +
                        "overhead angle, survey the whole map — restoring exactly when turned off. Default: OFF.",
                        order: 120));
            Reg(GroupGodTools, Config.ProportionalZoom,
                NewMeta("Proportional Zoom",
                        "While God View is active, finer scroll-zoom steps up close (no more jumping from " +
                        "normal to too-close). Far-out zoom stays vanilla. Off = flat vanilla step. Default: ON.",
                        order: 121, indent: 20,
                        visibleWhen: () => Config.EnableGodView.Value));
            Reg(GroupGodTools, Config.ZoomCellsPerNotch,
                NewMeta("Zoom Step (God View)",
                        "Cells one wheel notch moves the god-view camera (2–50; 1 cell ≈ 5 m, so 2 ≈ 10 m, " +
                        "50 ≈ 250 m). Higher = bigger jumps; lower = finer. Close-in tapers ~2x finer. " +
                        "Default: 10 (~50 m/notch).",
                        min: 2, max: 50, order: 122, indent: 40,
                        visibleWhen: () => Config.EnableGodView.Value && Config.ProportionalZoom.Value));
            Reg(GroupGodTools, Config.EnableFreeCam,
                NewMeta("Free Cam",
                        "Makes Free Cam available in the in-game panel — you activate it there, not here. " +
                        "Press Ctrl+F (configurable) to enter/exit, or use the panel toggle. WASD move, " +
                        "Space/Ctrl up/down, Shift fast, mouse look. Exiting restores the normal camera " +
                        "exactly. Default: OFF.",
                        order: 130));
            Reg(GroupGodTools, Config.FreeCamHotkey,
                NewMeta("Free Cam Hotkey",
                        "Key/chord that toggles Free Cam on/off in-game. A Unity KeyCode name or a chord " +
                        "with Ctrl/Alt/Shift — e.g. Ctrl+F, F8, Alt+C. Default: Ctrl+F.",
                        order: 131, indent: 20,
                        visibleWhen: () => Config.EnableFreeCam.Value));
            Reg(GroupGodTools, Config.FreeCamMoveSpeed,
                NewMeta("Free Cam Move Speed",
                        "Free Cam fly speed in world metres per second. Default: 40.",
                        min: 1f, max: 300f, order: 132, indent: 20,
                        visibleWhen: () => Config.EnableFreeCam.Value));
            Reg(GroupGodTools, Config.FreeCamFastMultiplier,
                NewMeta("Free Cam Fast Multiplier",
                        "Speed multiplier while holding Shift in Free Cam. Default: 3.",
                        min: 1f, max: 10f, order: 133, indent: 20,
                        visibleWhen: () => Config.EnableFreeCam.Value));
            Reg(GroupGodTools, Config.FreeCamSensitivity,
                NewMeta("Free Cam Mouse Sensitivity",
                        "Free Cam mouse-look sensitivity. Default: 2.",
                        min: 0.25f, max: 10f, order: 134, indent: 20,
                        visibleWhen: () => Config.EnableFreeCam.Value));
            Reg(GroupGodTools, Config.FreeCamGroundFloor,
                NewMeta("Free Cam Ground Floor",
                        "Keep Free Cam above the terrain so it can't clip through the world into the " +
                        "backface/sky void. Turn OFF for under-the-map shots. Default: on.",
                        order: 135, indent: 20,
                        visibleWhen: () => Config.EnableFreeCam.Value));
            Reg(GroupGodTools, Config.FreeCamFloorClearance,
                NewMeta("Free Cam Floor Clearance",
                        "Metres the floor sits above the surface. Small = ground-level skim shots; 0 rides " +
                        "the surface (may clip the near plane). Default: 1.0 m.",
                        min: 0f, max: 20f, order: 136, indent: 20,
                        visibleWhen: () => Config.EnableFreeCam.Value && Config.FreeCamGroundFloor.Value));

            // ===== Terrain Sculpting =====
            Reg(GroupTerrain, Config.TerrainEnable,
                NewMeta("Terrain Sculpting",
                        "Enable the terrain-elevation god-power. With it on, pick Terrain in the panel and " +
                        "apply the brush over the world with the apply key. Default: OFF.",
                        order: 200));
            Reg(GroupTerrain, Config.TerrainMode,
                NewMeta("Brush Mode",
                        "0 = Raise, 1 = Lower, 2 = Smooth, 3 = Flatten, 4 = Average. Hold Shift to invert " +
                        "Raise<->Lower. Average creeps the whole brush toward one flat mean level " +
                        "(TerrainHelper-style); Smooth only de-bumps and keeps slopes.",
                        min: 0, max: 4, order: 201, indent: 20,
                        visibleWhen: () => Config.TerrainEnable.Value));
            Reg(GroupTerrain, Config.TerrainStrength,
                NewMeta("Brush Strength",
                        "Raise/Lower height change in world metres per application (also the Smooth and " +
                        "Average step). Flatten ignores this. Default: 1.0.",
                        min: 0.05f, max: 25f, order: 202, indent: 20,
                        visibleWhen: () => Config.TerrainEnable.Value));
            Reg(GroupTerrain, Config.TerrainGridWidth,
                NewMeta("Brush Grid Width",
                        "Footprint width (X) in heightmap cells (1–10). Live: Left/Right arrows while armed. " +
                        "Each cell ≈ Resolution metres (~5 m). Default: 3.",
                        min: 1, max: 10, order: 203, indent: 20,
                        visibleWhen: () => Config.TerrainEnable.Value));
            Reg(GroupTerrain, Config.TerrainGridHeight,
                NewMeta("Brush Grid Depth",
                        "Footprint depth (Z) in heightmap cells (1–10). Live: Up/Down arrows while armed; " +
                        "Tab swaps width/depth. Use e.g. 1×10 to carve a path. Default: 3.",
                        min: 1, max: 10, order: 204, indent: 20,
                        visibleWhen: () => Config.TerrainEnable.Value));
            Reg(GroupTerrain, Config.TerrainGridFineSnap,
                NewMeta("Fine Grid Positioning",
                        "Snaps the grid overlay to HALF-cell steps so you can square up free-build buildings " +
                        "(TerrainHelper-style). Placement guide — the sculpt still resolves to whole cells on " +
                        "apply, so leave off for crisp flat pads. Default: off.",
                        order: 205, indent: 20,
                        visibleWhen: () => Config.TerrainEnable.Value));
            Reg(GroupTerrain, Config.TerrainApplyKey,
                NewMeta("Apply Key",
                        "Key/button that applies the brush at the cursor. A Unity KeyCode name (Mouse2 = " +
                        "middle, Mouse0 = left) or a chord. Default: Mouse2.",
                        order: 204, indent: 20,
                        visibleWhen: () => Config.TerrainEnable.Value));
            Reg(GroupTerrain, Config.TerrainUndoKey,
                NewMeta("Undo Key",
                        "Key/chord that undoes the last terrain stroke. Default: Ctrl+Z.",
                        order: 205, indent: 20,
                        visibleWhen: () => Config.TerrainEnable.Value));
            Reg(GroupTerrain, Config.TerrainArmHotkey,
                NewMeta("Terrain Arm Hotkey",
                        "Keyboard key/chord that arms (re-press disarms) the Terrain brush without clicking " +
                        "its tab; opens the panel when arming. Default: End (TerrainHelper convention).",
                        order: 206, indent: 20,
                        visibleWhen: () => Config.TerrainEnable.Value));

            // ===== Cursor Spawners =====
            Reg(GroupSpawning, Config.SpawnEnable,
                NewMeta("Cursor Spawners",
                        "Enable the cursor-spawner god-power. Pick a family, arm the spawner in the panel, " +
                        "and press the apply key over the world to place things at the cursor. Default: OFF.",
                        order: 300));
            Reg(GroupSpawning, Config.SpawnFamily,
                NewMeta("Spawn Family",
                        "0 = Animal, 1 = Mineral, 2 = Villager, 3 = Resource.",
                        min: 0, max: 3, order: 301, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value));
            Reg(GroupSpawning, Config.SpawnSubtype,
                NewMeta("Spawn Sub-type",
                        "Index within the family. Animal: 0 Deer/1 Bear/2 Boar/3 Wolf/4 Fox/5 Groundhog/" +
                        "6 Dog/7 Cat (4-7 require the Cats & Dogs DLC). " +
                        "Mineral: 0 Gold/1 Iron/2 Coal/3 Stone/4 Clay/5 Sand. " +
                        "Resource: 0 Forageable/1 Tree/2 Rock/3 Boulder. Villager ignores this.",
                        min: 0, max: 7, order: 302, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value));
            Reg(GroupSpawning, Config.SpawnCount,
                NewMeta("Spawn Count",
                        "How many to place per apply (1–50), scattered in a small ring around the cursor.",
                        min: 1, max: 50, order: 303, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value));
            Reg(GroupSpawning, Config.SpawnIsDeep,
                NewMeta("Deep Deposit (gold/iron/coal)",
                        "Minerals only. Gold/iron/coal spawn as a deep (infinite) deposit when on. " +
                        "Stone/clay/sand always spawn as infinite pits. Default: OFF.",
                        order: 304, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value && Config.SpawnFamily.Value == 1));
            Reg(GroupSpawning, Config.SpawnAnnounceVillagers,
                NewMeta("Announce Villagers",
                        "Fire the immigration 'arrived' notification when spawning villagers. Default: OFF.",
                        order: 305, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value && Config.SpawnFamily.Value == 2));
            Reg(GroupSpawning, Config.SpawnPersistent,
                NewMeta("Persistent (nodes/dens)",
                        "Animals only. When ON (default): Deer drop a self-respawning spawn-area node at the " +
                        "cursor (circular marker); Wolf/Boar drop a den. When OFF, they spawn loose. Bear is " +
                        "always loose. Dens persist through save/load; the deer node respawns this session but " +
                        "isn't serialized (re-drop after a reload). Default: ON.",
                        order: 307, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value && Config.SpawnFamily.Value == 0));
            // Prefab-GUID prefs: REGISTERED but always-hidden (visibleWhen=false). KC's "Other Settings"
            // catch-all lists UNregistered MelonPreferences, so simply not registering them made them show
            // up THERE — registering with a false predicate keeps them out of that dump AND off every
            // visible page. They keep working defaults; edit in UserData/MelonPreferences.cfg if needed.
            Reg(GroupSpawning, Config.SpawnWolfDenGuid,     NewMeta("Wolf Den GUID",     visibleWhen: () => false));
            Reg(GroupSpawning, Config.SpawnForageableGuids, NewMeta("Forageable GUIDs",  visibleWhen: () => false));
            Reg(GroupSpawning, Config.SpawnTreeGuids,       NewMeta("Tree GUIDs",        visibleWhen: () => false));
            Reg(GroupSpawning, Config.SpawnRockGuids,       NewMeta("Rock GUIDs",        visibleWhen: () => false));
            Reg(GroupSpawning, Config.SpawnGiantRockGuids,  NewMeta("Boulder GUIDs",     visibleWhen: () => false));
            Reg(GroupSpawning, Config.SpawnApplyKey,
                NewMeta("Spawn Apply Key",
                        "Key/button that spawns at the cursor. Mouse2 = middle, Mouse0 = left, or a chord. " +
                        "Default: Mouse2.",
                        order: 306, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value));
            Reg(GroupSpawning, Config.SpawnerArmHotkey,
                NewMeta("Spawner Arm Hotkey",
                        "Keyboard key/chord that arms (re-press disarms) the Spawner without clicking its " +
                        "tab; opens the panel when arming. Default: Home.",
                        order: 307, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value));
            // (Forageable/Tree/Rock/Boulder prefab-GUID prefs are hidden from KC — see note above.)

            // ===== Lake / Pond stamp =====
            Reg(GroupLake, Config.LakeEnable,
                NewMeta("Lake / Pond Stamp",
                        "Adds the Lake tab to the in-game panel. Stamps a carved, water-filled lake at the " +
                        "cursor (mirrors Pangu) — persists natively. Default: OFF.",
                        order: 500));
            Reg(GroupLake, Config.LakeShape,
                NewMeta("Shape", "0 = Rectangle, 1 = Circle.",
                        min: 0, max: 1, order: 501, indent: 20, visibleWhen: () => Config.LakeEnable.Value));
            Reg(GroupLake, Config.LakeGridWidth,
                NewMeta("Width (cells)", "Core footprint half-width in cells (1–10). Arrow keys resize while armed.",
                        min: 1, max: 10, order: 502, indent: 20, visibleWhen: () => Config.LakeEnable.Value));
            Reg(GroupLake, Config.LakeGridHeight,
                NewMeta("Depth (cells)", "Core footprint half-depth in cells (1–10). Arrow keys resize while armed.",
                        min: 1, max: 10, order: 503, indent: 20, visibleWhen: () => Config.LakeEnable.Value));
            Reg(GroupLake, Config.LakeCarveDepth,
                NewMeta("Carve Depth", "Lake bed depth below the water plane, metres (0.45–12). Default: 4.6.",
                        min: 0.45f, max: 12f, order: 504, indent: 20, visibleWhen: () => Config.LakeEnable.Value));
            Reg(GroupLake, Config.LakeFillRatio,
                NewMeta("Fill Ratio", "Grows the lake — multiplies Width/Depth (1.0–2.0). Default: 1.0 (no growth).",
                        min: 1f, max: 2f, order: 505, indent: 20, visibleWhen: () => Config.LakeEnable.Value));
            Reg(GroupLake, Config.LakeShoreWidth,
                NewMeta("Shore Blend", "Bank ring width that ramps back up to land (2–40). Default: 16.",
                        min: 2f, max: 40f, order: 506, indent: 20, visibleWhen: () => Config.LakeEnable.Value));
            Reg(GroupLake, Config.LakeNoGoWidth,
                NewMeta("Extra Size", "Extra cells added to the lake footprint on every side (0–24). Default: 0.",
                        min: 0f, max: 24f, order: 507, indent: 20, visibleWhen: () => Config.LakeEnable.Value));
            Reg(GroupLake, Config.LakeArmHotkey,
                NewMeta("Lake Arm Hotkey", "Key/chord that arms (re-press disarms) the Lake stamp. Default: Insert.",
                        order: 508, indent: 20, visibleWhen: () => Config.LakeEnable.Value));
            Reg(GroupLake, Config.LakeApplyKey,
                NewMeta("Lake Apply Key", "Key/button that stamps the lake at the cursor. Default: Ctrl+Mouse1.",
                        order: 509, indent: 20, visibleWhen: () => Config.LakeEnable.Value));

            // ===== Selected Building (Item Injection) =====
            Reg(GroupInject, Config.InjectEnable,
                NewMeta("Item Injection",
                        "Enable item injection on the SELECTED building. Select a building in-game, " +
                        "then use the panel's Selected Building section to add items, add livestock, " +
                        "or toggle infinite storage. Infinite storage is SESSION-ONLY — it is auto-" +
                        "stripped before every save (manual, autosave, and exit) so it never bakes " +
                        "into your .sav. Default: OFF.",
                        order: 400));
            Reg(GroupInject, Config.InjectItemIndex,
                NewMeta("Item to Add",
                        "Which item the Add Items button injects into the selected building's storage " +
                        "(index into the panel's item list). Pick the item in the panel.",
                        min: 0, max: 999, order: 401, indent: 20,
                        visibleWhen: () => Config.InjectEnable.Value));
            Reg(GroupInject, Config.InjectItemCount,
                NewMeta("Item Count",
                        "How many of the selected item to add per click (1–9999). Default: 100.",
                        min: 1, max: 9999, order: 402, indent: 20,
                        visibleWhen: () => Config.InjectEnable.Value));
            Reg(GroupInject, Config.InjectLivestockKind,
                NewMeta("Livestock to Add",
                        "0 = Cow (Barn), 1 = Chicken (Coop), 2 = Goat (Goat Barn), 3 = Horse (Stable). " +
                        "The selected building must match the animal. Default: Cow.",
                        min: 0, max: 3, order: 403, indent: 20,
                        visibleWhen: () => Config.InjectEnable.Value));
            // Livestock prefab-GUID prefs: registered but always-hidden (see the spawner GUID note above).
            Reg(GroupInject, Config.LivestockGuidCow,     NewMeta("Cow Prefab GUID",     visibleWhen: () => false));
            Reg(GroupInject, Config.LivestockGuidChicken, NewMeta("Chicken Prefab GUID", visibleWhen: () => false));
            Reg(GroupInject, Config.LivestockGuidGoat,    NewMeta("Goat Prefab GUID",    visibleWhen: () => false));
            Reg(GroupInject, Config.LivestockGuidHorse,   NewMeta("Horse Prefab GUID",   visibleWhen: () => false));
        }
    }
}
