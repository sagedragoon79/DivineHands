using UnityEngine;

namespace DivineHands.Core
{
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
                GUILayout.Label("God Tools", SectionStyle);
                Config.RevealMap.Value = GUILayout.Toggle(Config.RevealMap.Value, "  Reveal Map (clear fog)");
                Config.BuildAnywhere.Value =
                    GUILayout.Toggle(Config.BuildAnywhere.Value, "  Build anywhere (normal buildings)");
                Config.GodView.Value =
                    GUILayout.Toggle(Config.GodView.Value, "  God View (relax camera limits)");
                Config.FreeCam.Value =
                    GUILayout.Toggle(Config.FreeCam.Value, "  Free Cam (WASD fly — untoggle to restore)");
                if (Config.FreeCam.Value)
                    GUILayout.Label("WASD move · Space/Ctrl up/down · Shift fast · mouse look", HintStyle);

                GUILayout.Space(6f);
                DrawTerrainSection();

                GUILayout.Space(6f);
                DrawSpawnerSection();

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

        private static readonly string[] _terrainModes = { "Raise", "Lower", "Smooth", "Flatten" };

        private static void DrawTerrainSection()
        {
            GUILayout.Label("Terrain Sculpting", SectionStyle);

            Config.TerrainEnable.Value =
                GUILayout.Toggle(Config.TerrainEnable.Value, "  Enable terrain editing");

            if (!Config.TerrainEnable.Value)
            {
                if (_armedTool == ArmedTool.Terrain) _armedTool = ArmedTool.None;
                return;
            }

            // Arm toggle — arming Terrain disarms any other tool (single-armed-tool rule).
            bool terrainArmed = _armedTool == ArmedTool.Terrain;
            bool nowArmed = GUILayout.Toggle(terrainArmed, "  Arm brush (apply on key)");
            if (nowArmed != terrainArmed)
                _armedTool = nowArmed ? ArmedTool.Terrain : ArmedTool.None;

            // Mode selector.
            int mode = Mathf.Clamp(Config.TerrainMode.Value, 0, 3);
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

            // Grid size.
            int grid = Mathf.Clamp(Config.TerrainGridSize.Value, 1, 10);
            GUILayout.Label($"Grid: {grid} x {grid} cells", HintStyle);
            grid = Mathf.RoundToInt(GUILayout.HorizontalSlider(grid, 1f, 10f));
            Config.TerrainGridSize.Value = grid;

            GUILayout.Label($"Apply: {Config.TerrainApplyKey.Value}   Undo: {Config.TerrainUndoKey.Value}" +
                            $"   (undo depth {DivineHands.Modules.TerrainElevation.UndoDepth})", HintStyle);
        }

        private static readonly string[] _families = { "Animal", "Mineral", "Villager", "Resource" };
        private static readonly string[] _animalKinds = { "Deer", "Bear", "Boar", "Wolf" };
        private static readonly string[] _mineralKinds = { "Gold", "Iron", "Coal", "Stone", "Clay", "Sand" };
        private static readonly string[] _resourceKinds = { "Forage", "Tree", "Rock", "Giant" };

        private static void DrawSpawnerSection()
        {
            GUILayout.Label("Cursor Spawners", SectionStyle);

            Config.SpawnEnable.Value =
                GUILayout.Toggle(Config.SpawnEnable.Value, "  Enable cursor spawners");

            if (!Config.SpawnEnable.Value)
            {
                if (_armedTool == ArmedTool.Spawner) _armedTool = ArmedTool.None;
                return;
            }

            // Arm toggle — arming the spawner disarms any other tool.
            bool spawnerArmed = _armedTool == ArmedTool.Spawner;
            bool nowArmed = GUILayout.Toggle(spawnerArmed, "  Arm spawner (apply on key)");
            if (nowArmed != spawnerArmed)
                _armedTool = nowArmed ? ArmedTool.Spawner : ArmedTool.None;

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
                GUILayout.BeginHorizontal();
                for (int i = 0; i < kinds.Length; i++)
                {
                    bool on = GUILayout.Toggle(sub == i, kinds[i], GUI.skin.button);
                    if (on) sub = i;
                }
                GUILayout.EndHorizontal();
                Config.SpawnSubtype.Value = sub;
            }

            // Count slider.
            int count = Mathf.Clamp(Config.SpawnCount.Value, 1, 50);
            GUILayout.Label($"Count: {count}", HintStyle);
            count = Mathf.RoundToInt(GUILayout.HorizontalSlider(count, 1f, 50f));
            Config.SpawnCount.Value = count;

            // Persistent toggle (animals only). On = Deer→spawn-area node, Wolf/Boar→den (self-respawning).
            // Off = loose one-off animals. Bear is always loose regardless.
            if (family == 0)
                Config.SpawnPersistent.Value =
                    GUILayout.Toggle(Config.SpawnPersistent.Value,
                        "  Persistent (node/den — bear always loose)");

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

            // Compact scrollable item grid (4 per row) — keeps the panel short.
            _itemScroll = GUILayout.BeginScrollView(_itemScroll, GUILayout.Height(96f));
            const int perRow = 4;
            for (int i = 0; i < names.Length; i++)
            {
                if (i % perRow == 0) GUILayout.BeginHorizontal();
                bool on = GUILayout.Toggle(idx == i, names[i], GUI.skin.button);
                if (on && idx != i) idx = i;
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
