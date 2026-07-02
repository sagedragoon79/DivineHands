using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DivineHands.UI
{
    /// <summary>
    /// Small uGUI builder toolkit for the Divine Hands panel — the Keep Clarity look
    /// (FF sliced borders, game fonts, cream/amber palette) in compact, condensed rows:
    /// label + control + value on ONE line, toggle "chips" instead of tall checkbox lists.
    ///
    /// Dynamic state is driven by BINDINGS: builders register a per-frame refresh action
    /// (run from DivinePanelUgui.Tick while the panel is visible) so control visuals always
    /// mirror the Config/runtime flags — including changes made OUTSIDE the panel (arm
    /// hotkeys, arrow-key brush resizes, KC settings edits).
    /// </summary>
    internal static class UiKit
    {
        public static readonly List<Action> Bindings = new List<Action>();
        public static void Bind(Action refresh) => Bindings.Add(refresh);

        // ── primitives ──────────────────────────────────────────────────────

        public static GameObject NewChild(GameObject parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        public static Image ApplyBorder(GameObject go, Sprite? sprite, Color tint, bool raycast = false)
        {
            var img = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            if (sprite != null) { img.sprite = sprite; img.type = Image.Type.Sliced; img.color = tint; }
            else img.color = new Color(0.13f, 0.11f, 0.10f, 0.95f); // plain fallback if the sprite probe missed
            img.raycastTarget = raycast;
            return img;
        }

        public static TextMeshProUGUI NewText(GameObject parent, string name, string text, float size,
            Color color, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft,
            bool bold = false, bool smallCaps = false, bool wrap = false)
        {
            var go = NewChild(parent, name);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.raycastTarget = false;
            t.enableWordWrapping = wrap;
            var f = FFAssets.FontBody; if (f != null) t.font = f;
            var style = FontStyles.Normal;
            if (bold) style |= FontStyles.Bold;
            if (smallCaps) style |= FontStyles.SmallCaps;
            t.fontStyle = style;
            return t;
        }

        /// <summary>Fixed-height horizontal row (HLG) — the workhorse for compact one-line layouts.</summary>
        public static GameObject NewRow(GameObject parent, float height, int spacing = 5, int padLeft = 2)
        {
            var row = NewChild(parent, "Row");
            row.AddComponent<LayoutElement>().preferredHeight = height;
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(padLeft, 2, 0, 0);
            hlg.spacing = spacing;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            return row;
        }

        /// <summary>Section header: small-caps cream label over FF's gold double-line accent.</summary>
        public static void SectionHeader(GameObject parent, string label)
        {
            var lbl = NewText(parent, "Sec:" + label, label, 12, FFAssets.TextPrimary,
                TextAlignmentOptions.MidlineLeft, bold: true, smallCaps: true);
            lbl.gameObject.AddComponent<LayoutElement>().preferredHeight = 16;

            var lineGo = NewChild(parent, "SecLine");
            lineGo.AddComponent<LayoutElement>().preferredHeight = 5;
            var img = lineGo.AddComponent<Image>();
            var accent = FFAssets.AccentDouble;
            if (accent != null) { img.sprite = accent; img.type = Image.Type.Sliced; img.color = new Color(1f, 1f, 1f, 0.85f); }
            else img.color = new Color(0.83f, 0.63f, 0.19f, 0.45f);
            img.raycastTarget = false;
        }

        /// <summary>Dim italic-ish hint line whose text refreshes every frame (keybinds, statuses, readouts).</summary>
        public static TextMeshProUGUI NewHint(GameObject parent, Func<string> text, bool wrap = true)
        {
            var t = NewText(parent, "Hint", "", 9.5f, FFAssets.TextDim, wrap: wrap);
            var le = t.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 13;
            Bind(() =>
            {
                string s = text();
                if (t.text != s) t.text = s;   // change-guard: TMP re-layout only when it moved
            });
            return t;
        }

        // ── chip: a compact toggle-button (tab / mode / picker cell) ────────

        public sealed class Chip
        {
            public GameObject Go = null!;
            public Image Bg = null!;
            public TextMeshProUGUI Label = null!;
            public Button Btn = null!;

            public void SetVisual(bool on, bool enabled = true)
            {
                Btn.interactable = enabled;
                Bg.color = !enabled ? FFAssets.ChipDisabled : on ? FFAssets.ChipOn : FFAssets.ChipOff;
                Label.color = !enabled ? FFAssets.TextMuted : on ? Color.black : FFAssets.TextPrimary;
                Label.fontStyle = on ? FontStyles.Bold : FontStyles.Normal;
            }
        }

        public static Chip NewChip(GameObject parent, string label, Action onClick,
            float fontSize = 10.5f, float minWidth = 0f, bool flexible = true)
        {
            var chip = new Chip();
            chip.Go = NewChild(parent, "Chip:" + label);
            var le = chip.Go.AddComponent<LayoutElement>();
            if (minWidth > 0f) le.minWidth = minWidth;
            if (flexible) le.flexibleWidth = 1;

            chip.Bg = chip.Go.AddComponent<Image>();
            var sprite = FFAssets.ButtonGeneric ?? FFAssets.PanelBorderSimple;
            if (sprite != null) { chip.Bg.sprite = sprite; chip.Bg.type = Image.Type.Sliced; }
            chip.Bg.color = FFAssets.ChipOff;
            chip.Bg.raycastTarget = true;

            chip.Btn = chip.Go.AddComponent<Button>();
            chip.Btn.transition = Selectable.Transition.ColorTint;
            chip.Btn.targetGraphic = chip.Bg;
            var colors = chip.Btn.colors;
            colors.highlightedColor = new Color(1.12f, 1.08f, 1.0f, 1f);
            colors.pressedColor = new Color(0.82f, 0.78f, 0.70f, 1f);
            chip.Btn.colors = colors;
            chip.Btn.onClick.AddListener(() => onClick());

            chip.Label = NewText(chip.Go, "Label", label, fontSize, FFAssets.TextPrimary, TextAlignmentOptions.Center);
            var lrt = chip.Label.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(1, 0); lrt.offsetMax = new Vector2(-1, 0);
            return chip;
        }

        /// <summary>Plain action button (FF focus-border face + cream small-caps label).</summary>
        public static Button NewButton(GameObject parent, string label, Action onClick,
            float height = 22f, float fontSize = 11f, Func<string>? liveLabel = null)
        {
            var go = NewChild(parent, "Btn:" + label);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1;

            var img = go.AddComponent<Image>();
            var sprite = FFAssets.ButtonFocus ?? FFAssets.ButtonGeneric;
            if (sprite != null) { img.sprite = sprite; img.type = Image.Type.Sliced; }
            img.color = Color.white;

            var btn = go.AddComponent<Button>();
            btn.transition = Selectable.Transition.ColorTint;
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = new Color(1.15f, 1.10f, 0.95f, 1f);
            colors.pressedColor = new Color(0.80f, 0.75f, 0.65f, 1f);
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.5f);
            btn.colors = colors;
            btn.onClick.AddListener(() => onClick());

            var lbl = NewText(go, "Label", label, fontSize, FFAssets.TextPrimary,
                TextAlignmentOptions.Center, bold: true, smallCaps: true);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;

            if (liveLabel != null)
                Bind(() => { string s = liveLabel(); if (lbl.text != s) lbl.text = s; });
            return btn;
        }

        // ── one-row slider: [label | ───●── | value] — half the height of IMGUI ──

        public static void NewSliderRow(GameObject parent, string label, float min, float max, bool whole,
            Func<float> get, Action<float> set, Func<string> valueText,
            float labelWidth = 58f, Func<bool>? visibleWhen = null)
        {
            var row = NewRow(parent, 18f);

            var lbl = NewText(row, "Label", label, 10.5f, FFAssets.TextDim);
            lbl.gameObject.AddComponent<LayoutElement>().preferredWidth = labelWidth;

            // slider body
            var sGo = NewChild(row, "Slider");
            var sLe = sGo.AddComponent<LayoutElement>();
            sLe.flexibleWidth = 1;
            sLe.preferredHeight = 16;

            var bg = NewChild(sGo, "Background");
            var bgRt = (RectTransform)bg.transform;
            bgRt.anchorMin = new Vector2(0, 0.5f); bgRt.anchorMax = new Vector2(1, 0.5f);
            bgRt.sizeDelta = new Vector2(0, 7); bgRt.anchoredPosition = Vector2.zero;
            var bgImg = bg.AddComponent<Image>();
            var track = FFAssets.SliderTrack;
            if (track != null) { bgImg.sprite = track; bgImg.type = Image.Type.Sliced; }
            bgImg.color = new Color(0.42f, 0.38f, 0.33f, 1f);
            bgImg.raycastTarget = false;

            var fillArea = NewChild(sGo, "Fill Area");
            var faRt = (RectTransform)fillArea.transform;
            faRt.anchorMin = new Vector2(0, 0.5f); faRt.anchorMax = new Vector2(1, 0.5f);
            faRt.sizeDelta = new Vector2(-8, 7); faRt.anchoredPosition = Vector2.zero;
            var fill = NewChild(fillArea, "Fill");
            var fillRt = (RectTransform)fill.transform;
            fillRt.sizeDelta = new Vector2(4, 0);
            var fillImg = fill.AddComponent<Image>();
            var fillSprite = FFAssets.SliderFill;
            if (fillSprite != null) { fillImg.sprite = fillSprite; fillImg.type = Image.Type.Sliced; }
            fillImg.color = new Color(0.87f, 0.66f, 0.28f, 1f); // amber fill
            fillImg.raycastTarget = false;

            var handleArea = NewChild(sGo, "Handle Slide Area");
            var haRt = (RectTransform)handleArea.transform;
            haRt.anchorMin = new Vector2(0, 0.5f); haRt.anchorMax = new Vector2(1, 0.5f);
            haRt.sizeDelta = new Vector2(-12, 0); haRt.anchoredPosition = Vector2.zero;
            var handle = NewChild(handleArea, "Handle");
            var hRt = (RectTransform)handle.transform;
            hRt.sizeDelta = new Vector2(14, 14);
            var hImg = handle.AddComponent<Image>();
            var hSprite = FFAssets.SliderHandle;
            if (hSprite != null) hImg.sprite = hSprite;
            else hImg.color = new Color(0.9f, 0.8f, 0.6f, 1f);
            hImg.raycastTarget = true;

            var slider = sGo.AddComponent<Slider>();
            slider.fillRect = fillRt;
            slider.handleRect = hRt;
            slider.targetGraphic = hImg;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = whole;
            slider.SetValueWithoutNotify(Mathf.Clamp(get(), min, max));
            slider.onValueChanged.AddListener(v => set(v));

            var val = NewText(row, "Value", "", 10.5f, FFAssets.Amber, TextAlignmentOptions.MidlineRight);
            var vf = FFAssets.FontNumbers; if (vf != null) val.font = vf;
            val.gameObject.AddComponent<LayoutElement>().preferredWidth = 48f;

            Bind(() =>
            {
                if (visibleWhen != null)
                {
                    bool show = visibleWhen();
                    if (row.activeSelf != show) row.SetActive(show);
                    if (!show) return;
                }
                // Sync FROM config so external changes (arrow-key resizes, KC edits) move the
                // slider. During a drag onValueChanged already wrote config, so this is a no-op.
                float cur = Mathf.Clamp(get(), min, max);
                if (!Mathf.Approximately(slider.value, cur)) slider.SetValueWithoutNotify(cur);
                string s = valueText();
                if (val.text != s) val.text = s;
            });
        }

        // ── compact checkbox row (FF box + orange check) ────────────────────

        public static void NewToggleRow(GameObject parent, string label,
            Func<bool> get, Action<bool> set, Func<bool>? visibleWhen = null, int indent = 0)
        {
            var row = NewRow(parent, 17f, padLeft: 2 + indent);

            var boxGo = NewChild(row, "Box");
            boxGo.AddComponent<LayoutElement>().preferredWidth = 15;
            var boxImg = boxGo.AddComponent<Image>();
            var boxSprite = FFAssets.CheckboxBox;
            if (boxSprite != null) { boxImg.sprite = boxSprite; boxImg.type = Image.Type.Sliced; }
            boxImg.color = new Color(0.75f, 0.72f, 0.66f, 1f);
            boxImg.raycastTarget = true;

            var markGo = NewChild(boxGo, "Mark");
            var mRt = (RectTransform)markGo.transform;
            mRt.anchorMin = Vector2.zero; mRt.anchorMax = Vector2.one;
            mRt.offsetMin = new Vector2(1.5f, 1.5f); mRt.offsetMax = new Vector2(-1.5f, -1.5f);
            var markImg = markGo.AddComponent<Image>();
            var check = FFAssets.CheckboxCheck;
            if (check != null) { markImg.sprite = check; markImg.preserveAspect = true; }
            else markImg.color = FFAssets.Amber;
            markImg.raycastTarget = false;

            var toggle = boxGo.AddComponent<Toggle>();
            toggle.targetGraphic = boxImg;
            toggle.graphic = markImg;
            toggle.SetIsOnWithoutNotify(get());
            toggle.onValueChanged.AddListener(v => set(v));

            var lbl = NewText(row, "Label", label, 10.5f, FFAssets.TextDim, wrap: false);
            lbl.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;

            Bind(() =>
            {
                if (visibleWhen != null)
                {
                    bool show = visibleWhen();
                    if (row.activeSelf != show) row.SetActive(show);
                    if (!show) return;
                }
                bool cur = get();
                if (toggle.isOn != cur) toggle.SetIsOnWithoutNotify(cur);
            });
        }
    }
}
