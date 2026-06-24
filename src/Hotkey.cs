using System;
using UnityEngine;

namespace DivineHands
{
    /// <summary>
    /// Parses and tests configurable hotkey strings of the form "Ctrl+Shift+G", "F6", "Alt+D".
    /// Modifier tokens (case-insensitive): Ctrl/Control, Alt, Shift. The final token is a Unity
    /// <see cref="KeyCode"/> name. A malformed spec returns false (and logs once) rather than throwing.
    /// </summary>
    public static class Hotkey
    {
        private static string _lastWarned = "";

        /// <summary>True on the frame the spec's key transitions to down with the exact
        /// modifier set held. Empty/invalid specs return false.</summary>
        public static bool Pressed(string spec)
        {
            if (!TryParse(spec, out var key, out var ctrl, out var alt, out var shift))
                return false;

            if (!Input.GetKeyDown(key)) return false;
            return ModifiersMatch(ctrl, alt, shift);
        }

        /// <summary>True while the spec's key is held with the exact modifier set.</summary>
        public static bool Held(string spec)
        {
            if (!TryParse(spec, out var key, out var ctrl, out var alt, out var shift))
                return false;

            if (!Input.GetKey(key)) return false;
            return ModifiersMatch(ctrl, alt, shift);
        }

        private static bool ModifiersMatch(bool ctrl, bool alt, bool shift)
        {
            bool ctrlDown  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool altDown   = Input.GetKey(KeyCode.LeftAlt)     || Input.GetKey(KeyCode.RightAlt);
            bool shiftDown = Input.GetKey(KeyCode.LeftShift)   || Input.GetKey(KeyCode.RightShift);
            return ctrlDown == ctrl && altDown == alt && shiftDown == shift;
        }

        private static bool TryParse(string spec, out KeyCode key, out bool ctrl, out bool alt, out bool shift)
        {
            key = KeyCode.None; ctrl = alt = shift = false;
            if (string.IsNullOrWhiteSpace(spec)) return false;

            var parts = spec.Split('+');
            bool gotKey = false;
            foreach (var raw in parts)
            {
                var tok = raw.Trim();
                if (tok.Length == 0) continue;
                switch (tok.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": ctrl = true; break;
                    case "alt":     alt = true; break;
                    case "shift":   shift = true; break;
                    default:
                        if (Enum.TryParse<KeyCode>(tok, ignoreCase: true, out var k))
                        {
                            key = k; gotKey = true;
                        }
                        else
                        {
                            WarnOnce(spec, $"unrecognized key token '{tok}'");
                            return false;
                        }
                        break;
                }
            }

            if (!gotKey) { WarnOnce(spec, "no key specified"); return false; }
            return true;
        }

        private static void WarnOnce(string spec, string why)
        {
            if (_lastWarned == spec) return;
            _lastWarned = spec;
            MelonLoader.MelonLogger.Warning($"[DivineHands] Invalid hotkey \"{spec}\": {why}. " +
                                            "Use a KeyCode name or chord like Ctrl+G / F6 / Alt+Shift+D.");
        }
    }
}
