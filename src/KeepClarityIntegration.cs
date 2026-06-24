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
            Reg(GroupGodTools, Config.RevealMap,
                NewMeta("Reveal Map",
                        "Clear the entire fog of war. Toggling OFF best-effort restores the fog you had " +
                        "explored before. Caveat: FF serializes explored state into the save — if you " +
                        "save while revealed, the whole map stays explored. Turn it off before saving " +
                        "for clean fog. Default: OFF.",
                        order: 100));
            Reg(GroupGodTools, Config.BuildAnywhere,
                NewMeta("Build Anywhere",
                        "Place NORMAL buildings on ground vanilla rejects — steep slopes, no path to " +
                        "town, water/road overlap. Bridges are intentionally left alone so they keep " +
                        "deferring to vanilla and Keep Clarity's Bridge Anywhere. Turning this OFF " +
                        "restores exact vanilla placement. Default: OFF.",
                        order: 110));

            // ===== Terrain Sculpting =====
            Reg(GroupTerrain, Config.TerrainEnable,
                NewMeta("Terrain Sculpting",
                        "Enable the terrain-elevation god-power. With it on, pick Terrain in the panel and " +
                        "apply the brush over the world with the apply key. Default: OFF.",
                        order: 200));
            Reg(GroupTerrain, Config.TerrainMode,
                NewMeta("Brush Mode",
                        "0 = Raise, 1 = Lower, 2 = Smooth, 3 = Flatten. Hold Shift to invert Raise<->Lower.",
                        min: 0, max: 3, order: 201, indent: 20,
                        visibleWhen: () => Config.TerrainEnable.Value));
            Reg(GroupTerrain, Config.TerrainStrength,
                NewMeta("Brush Strength",
                        "Raise/Lower height change in world metres per application (also the Smooth step). " +
                        "Flatten ignores this. Default: 1.0.",
                        min: 0.05f, max: 25f, order: 202, indent: 20,
                        visibleWhen: () => Config.TerrainEnable.Value));
            Reg(GroupTerrain, Config.TerrainGridSize,
                NewMeta("Brush Grid Size",
                        "Brush footprint in heightmap cells per side (1–10). Each cell ≈ Resolution metres " +
                        "(default 5 m). Default: 3.",
                        min: 1, max: 10, order: 203, indent: 20,
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
                        "Index within the family. Animal: 0 Deer/1 Bear/2 Boar/3 Wolf. " +
                        "Mineral: 0 Gold/1 Iron/2 Coal/3 Stone/4 Clay/5 Sand. " +
                        "Resource: 0 Forageable/1 Tree/2 Rock/3 Giant Rock. Villager ignores this.",
                        min: 0, max: 5, order: 302, indent: 20,
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
            Reg(GroupSpawning, Config.SpawnApplyKey,
                NewMeta("Spawn Apply Key",
                        "Key/button that spawns at the cursor. Mouse2 = middle, Mouse0 = left, or a chord. " +
                        "Default: Mouse2.",
                        order: 306, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value));
            Reg(GroupSpawning, Config.SpawnForageableGuids,
                NewMeta("Forageable GUIDs",
                        "Delimited prefab GUIDs for the Forageable type. Each apply cycles the list; " +
                        "unknown/DLC GUIDs are skipped safely.",
                        order: 307, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value
                                        && Config.SpawnFamily.Value == 3 && Config.SpawnSubtype.Value == 0));
            Reg(GroupSpawning, Config.SpawnTreeGuids,
                NewMeta("Tree GUIDs",
                        "Delimited prefab GUIDs for the Tree type (placed via the terrain grow-tree system).",
                        order: 308, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value
                                        && Config.SpawnFamily.Value == 3 && Config.SpawnSubtype.Value == 1));
            Reg(GroupSpawning, Config.SpawnRockGuids,
                NewMeta("Rock GUIDs",
                        "Delimited prefab GUIDs for the Rock type.",
                        order: 309, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value
                                        && Config.SpawnFamily.Value == 3 && Config.SpawnSubtype.Value == 2));
            Reg(GroupSpawning, Config.SpawnGiantRockGuids,
                NewMeta("Giant Rock GUIDs",
                        "Delimited prefab GUIDs for the Giant Rock type.",
                        order: 310, indent: 20,
                        visibleWhen: () => Config.SpawnEnable.Value
                                        && Config.SpawnFamily.Value == 3 && Config.SpawnSubtype.Value == 3));
        }
    }
}
