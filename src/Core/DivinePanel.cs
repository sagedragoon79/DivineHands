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
                GUILayout.Label("Coming next", SectionStyle);
                GUI.enabled = false;
                GUILayout.Toggle(false, "  Terrain sculpt (elevation)");
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
