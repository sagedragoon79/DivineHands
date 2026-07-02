using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DivineHands.UI
{
    /// <summary>
    /// Borrows FF's native UI sprites/fonts at runtime so the Divine Hands panel wears the
    /// game's own chrome (same approach as Keep Clarity's settings manager — pattern copied,
    /// NOT referenced: KC stays a soft dep). Sprites are 9-slice panel borders / button faces
    /// probed by name from live Image components; fonts from live TMP_Text components.
    /// Everything degrades gracefully — a missing sprite renders as a plain tinted quad.
    /// </summary>
    internal static class FFAssets
    {
        private static bool _probed;
        private static readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, TMP_FontAsset> _fonts = new Dictionary<string, TMP_FontAsset>();

        // === Panel / border 9-slices (FF naming: IMG_Border*, BTN_*) ===
        public static Sprite? PanelBorderCarved => GetSprite("IMG_Border_BuildingInfo01"); // ornate carved backdrop (FF info windows)
        public static Sprite? PanelBorder => GetSprite("IMG_BorderSimpleThick01B");        // main panel frame
        public static Sprite? PanelBorderDark => GetSprite("IMG_BorderSimpleThickDark01B");
        public static Sprite? PanelBorderSimple => GetSprite("IMG_BorderSimpleDark01");    // small simple border
        public static Sprite? PanelHeaderTop => GetSprite("IMG_BorderTopFancy01B");        // fancy top header strip
        public static Sprite? AccentDouble => GetSprite("IMG_BtnAccent-Double01_UP");      // gold double-line divider

        // === Buttons / controls ===
        public static Sprite? ButtonGeneric => GetSprite("BTN_Border02_UP");
        public static Sprite? ButtonFocus => GetSprite("BTN_BorderFocus01_UP");
        public static Sprite? ExitRoundButton => GetSprite("BTN_ExitRound_UP");            // FF's round window X (32x32)
        public static Sprite? SliderTrack => GetSprite("IMG_BorderSimpleDark01");
        public static Sprite? SliderFill => GetSprite("IMG_Slider02");
        public static Sprite? SliderHandle => GetSprite("BTN_Slider01_UP");                // diamond handle
        public static Sprite? CheckboxBox => GetSprite("IMG_BorderSimpleDark01");
        public static Sprite? CheckboxCheck => GetSprite("BTN_Checkbox01_DOWN");           // orange checkmark

        // === Fonts (FF's building-info faces; Andada has a fully-built Latin atlas) ===
        public static TMP_FontAsset? FontBody => GetFont("building info body text (Andada-Regular) SDF")
                                                 ?? GetFont("Andada-Regular") ?? AnyFont();
        public static TMP_FontAsset? FontTitle => FontBody;   // Bold + SmallCaps at call site = FF title look
        public static TMP_FontAsset? FontNumbers => GetFont("resource numbers text (Andada-Bold) SDF")
                                                    ?? GetFont("Andada-Bold") ?? FontBody;

        // === Palette (matches Keep Clarity's FF-tuned tints) ===
        public static readonly Color TextPrimary = new Color(0.95f, 0.92f, 0.85f, 1f); // cream
        public static readonly Color TextDim = new Color(0.70f, 0.66f, 0.58f, 1f);     // muted tan
        public static readonly Color TextMuted = new Color(0.55f, 0.50f, 0.42f, 1f);
        public static readonly Color Amber = new Color(1.00f, 0.90f, 0.55f, 1f);       // golden highlight
        public static readonly Color PanelTintHeader = new Color(0.78f, 0.74f, 0.66f, 1f);
        public static readonly Color ChipOn = new Color(0.98f, 0.80f, 0.45f, 1f);      // armed/active chip tint
        public static readonly Color ChipOff = new Color(0.72f, 0.68f, 0.62f, 1f);     // idle chip tint
        public static readonly Color ChipDisabled = new Color(0.45f, 0.43f, 0.40f, 0.55f);

        public static void EnsureProbed()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                ProbeFonts();
                ProbeSprites();
                Plugin.Log.Msg($"[Panel] FF asset probe: {_sprites.Count} sprites, {_fonts.Count} fonts " +
                    $"(border={(PanelBorderCarved != null ? "carved" : PanelBorder != null ? "plain" : "MISSING")}, body font={(FontBody != null ? "ok" : "MISSING")})");
            }
            catch (Exception e) { Plugin.Log.Warning("[Panel] FF asset probe threw: " + e.Message); }
        }

        private static void ProbeFonts()
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TMP_Text>(includeInactive: true))
            {
                if (t == null || t.font == null || string.IsNullOrEmpty(t.font.name)) continue;
                if (!_fonts.ContainsKey(t.font.name)) _fonts[t.font.name] = t.font;
            }
        }

        private static void ProbeSprites()
        {
            // Resources.FindObjectsOfTypeAll also sees inactive panels whose Images carry sprites.
            foreach (var img in Resources.FindObjectsOfTypeAll<Image>())
            {
                if (img == null || img.sprite == null || string.IsNullOrEmpty(img.sprite.name)) continue;
                if (!_sprites.ContainsKey(img.sprite.name)) _sprites[img.sprite.name] = img.sprite;
            }
        }

        // A sprite may live in a prefab FF hasn't loaded yet — reprobe a few times, then give up.
        private static readonly Dictionary<string, int> _missAttempts = new Dictionary<string, int>();
        private const int MaxReprobes = 8;

        private static Sprite? GetSprite(string name)
        {
            if (!_probed) EnsureProbed();
            if (_sprites.TryGetValue(name, out var s)) return s;
            _missAttempts.TryGetValue(name, out int attempts);
            if (attempts >= MaxReprobes) return null;
            _missAttempts[name] = attempts + 1;
            ProbeSprites();
            return _sprites.TryGetValue(name, out s) ? s : null;
        }

        private static TMP_FontAsset? GetFont(string name)
        {
            if (!_probed) EnsureProbed();
            if (_fonts.TryGetValue(name, out var f)) return f;
            foreach (var kv in _fonts)
                if (kv.Key.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
            return null;
        }

        private static TMP_FontAsset? AnyFont()
        {
            if (!_probed) EnsureProbed();
            foreach (var f in _fonts.Values) return f;
            return TMP_Settings.defaultFontAsset;
        }
    }
}
