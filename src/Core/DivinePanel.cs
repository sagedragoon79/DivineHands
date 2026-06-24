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

        /// <summary>True when the panel is open AND the Terrain tool is the selected mode — read by
        /// <see cref="DivineHands.Modules.TerrainElevation"/> to gate brush application so terrain only
        /// sculpts while the user has explicitly armed the tool. Requires the terrain feature enabled.</summary>
        public static bool TerrainModeActive =>
            _visible && Config.MasterEnable.Value && Config.TerrainEnable.Value && _terrainArmed;

        private static bool _terrainArmed;

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

                GUILayout.Space(6f);
                DrawTerrainSection();

                GUILayout.Space(6f);
                GUILayout.Label("Coming next", SectionStyle);
                GUI.enabled = false;
                GUILayout.Toggle(false, "  Cursor spawners");
                GUILayout.Toggle(false, "  Build anywhere");
                GUI.enabled = true;
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
                _terrainArmed = false;
                return;
            }

            // Arm toggle — only while armed does the brush apply on the apply key.
            _terrainArmed = GUILayout.Toggle(_terrainArmed, "  Arm brush (apply on key)");

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
