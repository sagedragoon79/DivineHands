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
        }
    }
}
