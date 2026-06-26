using System;
using UnityEngine;

namespace DivineHands.Core
{
    /// <summary>Scoped <c>GUI.enabled</c> override (greys + disables contained controls) that restores
    /// the prior value on dispose — used to grey out storage-ineligible items in the inject picker.</summary>
    internal readonly struct GUIEnabledScope : IDisposable
    {
        private readonly bool _prev;
        public GUIEnabledScope(bool enabled)
        {
            _prev = GUI.enabled;
            GUI.enabled = _prev && enabled;
        }
        public void Dispose() => GUI.enabled = _prev;
    }

    /// <summary>
    /// The shared in-game control surface for every Divine Hands god-power. v0.1 is a lightweight
    /// IMGUI window (fast to ship, proven pattern — same approach as FFSeedScanner) listing the
    /// available powers. The polished code-built uGUI panel (TerrainHelper aesthetic: header drag,
    /// click-through background, grid preview) is the v0.1-finish target once terrain editing lands.
    ///
    /// Persistent configuration (hotkey, defaults, thresholds) lives in Keep Clarity's settings
    /// panel via <see cref="KeepClarityIntegration"/> — this panel is for live, in-session actions.
    /// </summary>
    public static class DivinePanel
    {
        private const int WindowId = 0x44_69_76_31; // "Div1"
        private static bool _visible;
        private static bool _positioned;
        private static Rect _rect = new Rect(40f, 120f, 280f, 0f);

        public static bool Visible => _visible;

        /// <summary>Which god-power (if any) is currently armed. Only ONE tool is armed at a time —
        /// arming spawners disarms terrain and vice-versa — so the apply key is never ambiguous.</summary>
        private enum ArmedTool { None, Terrain, Spawner }
        private static ArmedTool _armedTool = ArmedTool.None;

        /// <summary>True when the panel is open AND the Terrain tool is the armed mode — read by
        /// <see cref="DivineHands.Modules.TerrainElevation"/> to gate brush application so terrain only
        /// sculpts while the user has explicitly armed the tool. Requires the terrain feature enabled.</summary>
        public static bool TerrainModeActive =>
            _visible && Config.MasterEnable.Value && Config.TerrainEnable.Value
            && _armedTool == ArmedTool.Terrain;

        /// <summary>True when the panel is open AND the Spawner tool is the armed mode — read by
        /// <see cref="DivineHands.Modules.CursorSpawners"/> to gate spawning on the apply key.</summary>
        public static bool SpawnerModeActive =>
            _visible && Config.MasterEnable.Value && Config.SpawnEnable.Value
            && _armedTool == ArmedTool.Spawner;

        /// <summary>True when the cursor is over the visible panel — read by the input guard
        /// (<see cref="DivineHands.Patches.SelectionGuardPatch"/>) so the game treats the panel like
        /// UI and doesn't start a drag-select underneath it. <c>_rect</c> is in GUI space (y down),
        /// so the mouse Y is flipped to match.</summary>
        public static bool BlocksGameInput =>
            _visible
            && Config.MasterEnable.Value
            && _rect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));

        public static void Toggle() => _visible = !_visible;
        public static void Show() => _visible = true;
        public static void Hide() => _visible = false;

        /// <summary>Toggle-arm the Terrain tool from a global hotkey (no tab click). Engaging shows the
        /// panel so its options + cursor grid are visible and <see cref="TerrainModeActive"/> is satisfied;
        /// re-pressing disarms. No-op unless terrain is enabled in the config.</summary>
        public static void ToggleArmTerrain() => HotkeyArm(ArmedTool.Terrain, Config.TerrainEnable.Value);

        /// <summary>Toggle-arm the Spawner tool from a global hotkey — see <see cref="ToggleArmTerrain"/>.</summary>
        public static void ToggleArmSpawner() => HotkeyArm(ArmedTool.Spawner, Config.SpawnEnable.Value);

        private static void HotkeyArm(ArmedTool tool, bool enabled)
        {
            if (!Config.MasterEnable.Value || !enabled) return; // tool not available — ignore the key

            // "Engaged" = this tool armed AND the panel actually showing (so it's live). Re-pressing the
            // key while truly engaged disarms; otherwise engage it — arm (disarming any other tool) and
            // reveal the panel. Gating on _visible too means a dormant armed-but-hidden state re-engages
            // rather than silently toggling off where the user can't see it.
            if (_armedTool == tool && _visible)
            {
                _armedTool = ArmedTool.None;
            }
            else
            {
                _armedTool = tool;
                _visible = true;
            }
        }

        public static void Render()
        {
            if (!_visible) return;

            if (!_positioned)
            {
                // Anchor on first show; left side, below the top bar.
                _rect.x = 40f;
                _rect.y = 120f;
                _positioned = true;
            }

            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, "Divine Hands  v" + Plugin.Version);
        }

        private static void DrawWindow(int id)
        {
            GUILayout.Space(2f);

            if (!Plugin.InGame)
            {
                GUILayout.Label("Load a map to use god-powers.");
            }
            else
            {
                DrawGodToolsSection();

                GUILayout.Space(6f);
                DrawToolsSection();

                GUILayout.Space(6f);
                DrawInjectSection();
            }

            GUILayout.Space(8f);
            GUILayout.Label("Hotkey & all settings: Keep Clarity panel.", HintStyle);

            GUILayout.Space(4f);
            if (GUILayout.Button("Close"))
                _visible = false;

            // Drag by the whole window (no uGUI header yet).
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        // ---- God Tools ----
        // Each power's control renders ONLY when its Enable pref (KC config) is true. The toggle drives
        // the power's RUNTIME Active flag (not a saved pref) — the live ON/OFF that the modules sync off.
        private static void DrawGodToolsSection()
        {
            GUILayout.Label("God Tools", SectionStyle);

            bool anyEnabled = Config.EnableRevealMap.Value || Config.EnableBuildAnywhere.Value
                           || Config.EnableGodView.Value || Config.EnableFreeCam.Value;

            if (!anyEnabled)
            {
                GUILayout.Label("(enable god tools in the Keep Clarity settings)", HintStyle);
                return;
            }

            if (Config.EnableRevealMap.Value)
                DivineHands.Modules.GodTools.RevealActive =
                    GUILayout.Toggle(DivineHands.Modules.GodTools.RevealActive, "  Reveal Map (clear fog)");

            if (Config.EnableBuildAnywhere.Value)
                DivineHands.Patches.BuildAnywherePatches.Active =
                    GUILayout.Toggle(DivineHands.Patches.BuildAnywherePatches.Active, "  Build anywhere");

            if (Config.EnableGodView.Value)
                DivineHands.Modules.CameraTools.GodViewActive =
                    GUILayout.Toggle(DivineHands.Modules.CameraTools.GodViewActive, "  God View");

            if (Config.EnableFreeCam.Value)
            {
                DivineHands.Modules.CameraTools.FreeCamActive =
                    GUILayout.Toggle(DivineHands.Modules.CameraTools.FreeCamActive, "  Free Cam");
                GUILayout.Label("Ctrl+F toggles · WASD fly · mouse look · Shift fast", HintStyle);
            }
        }

        // ---- Terrain / Spawner tabs ----
        // The brush and the spawner are never in use at once (one armed tool, one apply key), so they
        // share a single screen area as tabs. A tool's tab appears only when it's enabled in the Keep
        // Clarity settings; clicking a tab BOTH arms that tool and reveals its options below (the tab
        // IS the arm radial), and clicking the armed tab again disarms it. Same enable(config)/
        // activate(panel) split as God Tools — there's no separate in-panel "Enable" toggle anymore.
        private static void DrawToolsSection()
        {
            bool terrainEnabled = Config.TerrainEnable.Value;
            bool spawnerEnabled = Config.SpawnEnable.Value;

            GUILayout.Label("Terrain & Spawner", SectionStyle);

            if (!terrainEnabled && !spawnerEnabled)
            {
                _armedTool = ArmedTool.None;
                GUILayout.Label("(enable Terrain editing or Cursor spawners in the Keep Clarity settings)",
                                HintStyle);
                return;
            }

            // Drop a stale arm if its tool was disabled in config since it was armed.
            if (_armedTool == ArmedTool.Terrain && !terrainEnabled) _armedTool = ArmedTool.None;
            if (_armedTool == ArmedTool.Spawner && !spawnerEnabled) _armedTool = ArmedTool.None;

            // Tab row — each tab is a toggle-button whose pressed state == that tool being armed.
            // Re-clicking the active tab clears the arm (None); clicking the other tab switches.
            GUILayout.BeginHorizontal();
            if (terrainEnabled)
            {
                bool armed = _armedTool == ArmedTool.Terrain;
                bool now = GUILayout.Toggle(armed, "Terrain", GUI.skin.button);
                if (now != armed) _armedTool = now ? ArmedTool.Terrain : ArmedTool.None;
            }
            if (spawnerEnabled)
            {
                bool armed = _armedTool == ArmedTool.Spawner;
                bool now = GUILayout.Toggle(armed, "Spawner", GUI.skin.button);
                if (now != armed) _armedTool = now ? ArmedTool.Spawner : ArmedTool.None;
            }
            GUILayout.EndHorizontal();

            // Options for whichever tab is armed (shared area). Nothing armed => prompt.
            switch (_armedTool)
            {
                case ArmedTool.Terrain: DrawTerrainOptions(); break;
                case ArmedTool.Spawner: DrawSpawnerOptions(); break;
                default:
                    GUILayout.Label($"Click a tab — or press {Config.TerrainArmHotkey.Value} (Terrain) / " +
                                    $"{Config.SpawnerArmHotkey.Value} (Spawner) — to arm a tool.", HintStyle);
                    break;
            }
        }

        private static readonly string[] _terrainModes = { "Raise", "Lower", "Smooth", "Flatten", "Average" };

        // Terrain options (mode/strength/grid). The section header, the config-enable, and the arm now
        // live in DrawToolsSection — the Terrain tab is the arm — so this just draws the controls.
        private static void DrawTerrainOptions()
        {
            // Mode selector.
            int mode = Mathf.Clamp(Config.TerrainMode.Value, 0, 4);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < _terrainModes.Length; i++)
            {
                bool on = GUILayout.Toggle(mode == i, _terrainModes[i], GUI.skin.button);
                if (on) mode = i;
            }
            GUILayout.EndHorizontal();
            Config.TerrainMode.Value = mode;

            // Strength (hidden for Flatten, which uses cursor height).
            if (mode != 3)
            {
                GUILayout.Label($"Strength: {Config.TerrainStrength.Value:0.0} m", HintStyle);
                Config.TerrainStrength.Value =
                    GUILayout.HorizontalSlider(Config.TerrainStrength.Value, 0.05f, 25f);
            }

            // Grid footprint — independent width (X) × depth (Z). Show metres when the per-cell
            // resolution is known (terrain resolved in-game); otherwise just the cell counts.
            int gw = Mathf.Clamp(Config.TerrainGridWidth.Value, 1, 10);
            int gh = Mathf.Clamp(Config.TerrainGridHeight.Value, 1, 10);
            float cellMeters = DivineHands.Modules.TerrainElevation.CellMeters;
            string gridLabel = cellMeters > 0f
                ? $"Grid: {gw} x {gh} cells ({gw * cellMeters:0.#} x {gh * cellMeters:0.#} m)"
                : $"Grid: {gw} x {gh} cells";
            GUILayout.Label(gridLabel, HintStyle);
            GUILayout.Label("Width", HintStyle);
            gw = Mathf.RoundToInt(GUILayout.HorizontalSlider(gw, 1f, 10f));
            Config.TerrainGridWidth.Value = gw;
            GUILayout.Label("Depth", HintStyle);
            gh = Mathf.RoundToInt(GUILayout.HorizontalSlider(gh, 1f, 10f));
            Config.TerrainGridHeight.Value = gh;
            GUILayout.Label("Arrows resize (←→ width · ↑↓ depth) · Tab swaps", HintStyle);

            GUILayout.Label($"Apply: {Config.TerrainApplyKey.Value}   Undo: {Config.TerrainUndoKey.Value}" +
                            $"   (undo depth {DivineHands.Modules.TerrainElevation.UndoDepth})", HintStyle);
        }

        private static readonly string[] _families = { "Animal", "Mineral", "Villager", "Resource" };
        private static readonly string[] _animalKinds =
            { "Deer", "Bear", "Boar", "Wolf", "Fox", "Groundhog", "Dog", "Cat" };
        private static readonly string[] _mineralKinds = { "Gold", "Iron", "Coal", "Stone", "Clay", "Sand" };
        private static readonly string[] _resourceKinds = { "Forage", "Tree", "Rock", "Boulder" };

        // Last-rendered spawner family/subtype, so we can reset the count to 1 when the user switches
        // type (a fresh type defaults to spawning one). Seeded to an impossible value to skip the first frame.
        private static int _lastSpawnFamily = -1;
        private static int _lastSpawnSubtype = -1;

        // Spawner options (family/subtype/count/…). The section header, the config-enable, and the arm
        // now live in DrawToolsSection — the Spawner tab is the arm — so this just draws the controls.
        private static void DrawSpawnerOptions()
        {
            // Family picker.
            int family = Mathf.Clamp(Config.SpawnFamily.Value, 0, _families.Length - 1);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < _families.Length; i++)
            {
                bool on = GUILayout.Toggle(family == i, _families[i], GUI.skin.button);
                if (on && family != i) { family = i; Config.SpawnSubtype.Value = 0; }
            }
            GUILayout.EndHorizontal();
            Config.SpawnFamily.Value = family;

            // Sub-type picker (per family; Villager has none).
            string[]? kinds = family switch
            {
                0 => _animalKinds,
                1 => _mineralKinds,
                3 => _resourceKinds,
                _ => null
            };
            if (kinds != null)
            {
                int sub = Mathf.Clamp(Config.SpawnSubtype.Value, 0, kinds.Length - 1);
                bool greyedAny = false;
                const int perRow = 4; // wrap so 8 animal kinds (incl. "Groundhog") don't overflow the panel
                for (int i = 0; i < kinds.Length; i++)
                {
                    if (i % perRow == 0) GUILayout.BeginHorizontal();

                    // Animal family: grey out DLC kinds whose prefab/asset isn't available right now
                    // (needs the Cats & Dogs DLC). Index == AnimalKind value, so the cast is direct.
                    bool available = family != 0
                        || DivineHands.Modules.CursorSpawners.DlcAnimalAvailable(
                               (DivineHands.Modules.CursorSpawners.AnimalKind)i);
                    if (!available) greyedAny = true;

                    using (new GUIEnabledScope(available))
                    {
                        bool on = GUILayout.Toggle(sub == i, kinds[i], GUI.skin.button);
                        if (on && available) sub = i;
                    }

                    if (i % perRow == perRow - 1 || i == kinds.Length - 1) GUILayout.EndHorizontal();
                }
                Config.SpawnSubtype.Value = sub;

                if (greyedAny)
                    GUILayout.Label("Greyed animals need the Cats & Dogs DLC.", HintStyle);
            }

            // When the family or subtype changes vs the previous frame, reset the count to 1 (a fresh
            // type defaults to one). Skip the very first frame (seeded to -1) so we don't clobber a
            // user-set count on panel open.
            int curFamily = Config.SpawnFamily.Value;
            int curSubtype = Config.SpawnSubtype.Value;
            if (_lastSpawnFamily != -1
                && (curFamily != _lastSpawnFamily || curSubtype != _lastSpawnSubtype))
                Config.SpawnCount.Value = 1;
            _lastSpawnFamily = curFamily;
            _lastSpawnSubtype = curSubtype;

            // Count slider.
            int count = Mathf.Clamp(Config.SpawnCount.Value, 1, 50);
            GUILayout.Label($"Count: {count}", HintStyle);
            count = Mathf.RoundToInt(GUILayout.HorizontalSlider(count, 1f, 50f));
            Config.SpawnCount.Value = count;

            // Persistent toggle (animals only). On = Deer drop a self-respawning spawn-area node + Wolf/Boar
            // a den at the cursor; off = loose. Bear is always loose.
            // Off = loose one-off animals. Bear is always loose regardless.
            if (family == 0)
                Config.SpawnPersistent.Value =
                    GUILayout.Toggle(Config.SpawnPersistent.Value,
                        "  Persistent (Deer area + Wolf/Boar dens — bear always loose)");

            // Deep toggle (minerals only).
            if (family == 1)
                Config.SpawnIsDeep.Value =
                    GUILayout.Toggle(Config.SpawnIsDeep.Value, "  Deep deposit (gold/iron/coal — infinite)");

            GUILayout.Label($"Apply: {Config.SpawnApplyKey.Value}   (GUIDs & all settings: Keep Clarity panel)",
                            HintStyle);
        }

        // ---- Selected Building (Item Injection) ----

        private static readonly string[] _livestockKinds = { "Cow", "Chicken", "Goat", "Horse" };
        private static string _injectStatus = "";
        private static Vector2 _itemScroll;

        private static void DrawInjectSection()
        {
            GUILayout.Label("Selected Building", SectionStyle);

            Config.InjectEnable.Value =
                GUILayout.Toggle(Config.InjectEnable.Value, "  Enable item injection");
            if (!Config.InjectEnable.Value) return;

            string buildingName = DivineHands.Modules.ItemInjection.GetSelectedBuildingName();
            bool hasBuilding = !string.IsNullOrEmpty(buildingName);

            GUILayout.Label(hasBuilding ? $"Selected: {buildingName}" : "Select a building in-game.",
                            HintStyle);

            if (!hasBuilding)
            {
                if (!string.IsNullOrEmpty(_injectStatus))
                    GUILayout.Label(_injectStatus, HintStyle);
                return;
            }

            // ---- Add Items ----
            var names = DivineHands.Modules.ItemInjection.ItemNames;
            int idx = Mathf.Clamp(Config.InjectItemIndex.Value, 0, names.Length - 1);
            GUILayout.Label($"Item: {names[idx]}", HintStyle);

            // Only storage buildings (warehouse/granary/Preservist pantry…) enforce an allow-list; for
            // those we grey out items the building won't accept. Manufacturing buildings have no list,
            // so every item stays selectable. (See ItemInjection.IsItemEligibleForSelectedBuilding.)
            bool hasAllowList = DivineHands.Modules.ItemInjection.SelectedBuildingHasAllowList();
            if (hasAllowList)
                GUILayout.Label("Greyed items aren't accepted by this storage.", HintStyle);

            // Compact scrollable item grid (4 per row) — keeps the panel short.
            _itemScroll = GUILayout.BeginScrollView(_itemScroll, GUILayout.Height(96f));
            const int perRow = 4;
            for (int i = 0; i < names.Length; i++)
            {
                if (i % perRow == 0) GUILayout.BeginHorizontal();

                bool eligible = !hasAllowList
                    || DivineHands.Modules.ItemInjection.IsItemEligibleForSelectedBuilding(names[i]);

                using (new GUIEnabledScope(eligible))
                {
                    bool on = GUILayout.Toggle(idx == i, names[i], GUI.skin.button);
                    if (on && idx != i && eligible) idx = i;
                }

                if (i % perRow == perRow - 1 || i == names.Length - 1) GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            Config.InjectItemIndex.Value = idx;

            int count = Mathf.Clamp(Config.InjectItemCount.Value, 1, 9999);
            GUILayout.Label($"Count: {count}", HintStyle);
            count = Mathf.RoundToInt(GUILayout.HorizontalSlider(count, 1f, 9999f));
            Config.InjectItemCount.Value = count;

            if (GUILayout.Button($"Add {count}x {names[idx]}"))
                _injectStatus = DivineHands.Modules.ItemInjection.AddItems(names[idx], count);

            GUILayout.Space(4f);

            // ---- Add Livestock ----
            int kind = Mathf.Clamp(Config.InjectLivestockKind.Value, 0, _livestockKinds.Length - 1);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < _livestockKinds.Length; i++)
            {
                bool on = GUILayout.Toggle(kind == i, _livestockKinds[i], GUI.skin.button);
                if (on) kind = i;
            }
            GUILayout.EndHorizontal();
            Config.InjectLivestockKind.Value = kind;

            if (GUILayout.Button($"Add 1x {_livestockKinds[kind]}"))
                _injectStatus = DivineHands.Modules.ItemInjection.AddLivestock(
                    (DivineHands.Modules.ItemInjection.LivestockKind)kind);

            GUILayout.Space(4f);

            // ---- Infinite storage (SESSION-ONLY) ----
            bool infinite = DivineHands.Modules.ItemInjection.IsSelectedInfinite();
            bool nowInfinite = GUILayout.Toggle(infinite, "  Infinite storage (session-only)");
            if (nowInfinite != infinite)
                _injectStatus = DivineHands.Modules.ItemInjection.ToggleSelectedInfinite();
            GUILayout.Label("Session-only: auto-stripped from every save (manual, autosave, exit), " +
                            "so it never bakes into your .sav.", HintStyle);

            if (!string.IsNullOrEmpty(_injectStatus))
                GUILayout.Label(_injectStatus, HintStyle);
        }

        private static GUIStyle? _section;
        private static GUIStyle SectionStyle => _section ??= new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold
        };

        private static GUIStyle? _hint;
        private static GUIStyle HintStyle => _hint ??= new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Italic,
            wordWrap = true,
            fontSize = 10
        };
    }
}
