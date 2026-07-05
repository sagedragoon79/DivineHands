using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using DivineHands.UI;

namespace DivineHands.Core
{
    /// <summary>
    /// The polished uGUI Divine Hands panel — FF-native chrome in the Keep Clarity mold
    /// (carved border, game fonts, cream/amber palette, header drag) replacing the IMGUI
    /// placeholder. Same behavior surface: identical Config writes, the same armed-tool
    /// state on <see cref="DivinePanel"/> (arm hotkeys + modules keep working unchanged),
    /// and cursor-over feeds <see cref="DivinePanel.BlocksGameInput"/> for the input guard.
    ///
    /// CONDENSED layout vs the IMGUI panel: god tools are toggle CHIPS in a wrapping grid
    /// (5 rows → 2), every slider is label+track+value on ONE row (was 2), Width/Depth
    /// stack tightly, Delete/Kill share one section, and hint spam is reduced to a single
    /// live keybind line per tool. Dynamic state refreshes through UiKit bindings each
    /// frame — external changes (arrow-key resizes, arm hotkeys, KC edits) stay in sync.
    ///
    /// The IMGUI panel remains available behind Config.ClassicPanel as a fallback.
    /// </summary>
    internal static class DivinePanelUgui
    {
        private const float PanelWidth = 302f;
        private const int SortingOrder = 4500;   // above overlays, below KC's settings window

        private static bool _built;
        private static bool _pendingInitialPos;
        private static bool _loggedTickError;
        private static GameObject? _canvasRoot;
        private static RectTransform? _panelRt;
        private static DraggablePanel? _drag;

        // Section containers gated by bindings (config enables / InGame).
        private static GameObject? _notInGame;
        private static GameObject? _godSection;
        private static GameObject? _toolsSection;
        private static GameObject? _injectSection;
        private static GameObject? _dangerSection;

        private static string _injectStatus = "";
        private static string _deleteStatus = "";
        private static string _killStatus = "";

        /// <summary>Cursor over the visible panel — feeds DivinePanel.BlocksGameInput.</summary>
        public static bool CursorOver =>
            _built && _canvasRoot != null && _canvasRoot.activeSelf && _panelRt != null
            && RectTransformUtility.RectangleContainsScreenPoint(_panelRt, Input.mousePosition, null);

        /// <summary>Per-frame driver (Plugin.OnUpdate): sync visibility, run control bindings.</summary>
        public static void Tick()
        {
            bool want = DivinePanel.Visible && Config.MasterEnable.Value && !Config.ClassicPanel.Value;
            if (!_built)
            {
                if (!want) return;
                try { Build(); _built = true; }
                catch (Exception e)
                {
                    _built = true; // don't retry-spam; classic panel remains available
                    Plugin.Log.Warning($"[Panel] uGUI build failed ({e.Message}) — falling back to classic panel.\n{e.StackTrace}");
                    Config.ClassicPanel.Value = true;
                    return;
                }
            }
            if (_canvasRoot == null) return;
            if (_canvasRoot.activeSelf != want) _canvasRoot.SetActive(want);
            if (!want) return;

            try
            {
                var list = UiKit.Bindings;
                for (int i = 0; i < list.Count; i++) list[i]();
            }
            catch (Exception e)
            {
                if (!_loggedTickError) { _loggedTickError = true; Plugin.Log.Warning("[Panel] binding tick: " + e.Message); }
            }

            if (_pendingInitialPos && _drag != null)
            {
                // First-frame canvas rects aren't laid out yet — place on the tick after build.
                _pendingInitialPos = false;
                _drag.ApplyNormalized(_drag.DefaultNormalizedPosition, persist: false);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  Build
        // ────────────────────────────────────────────────────────────────────

        private static void Build()
        {
            FFAssets.EnsureProbed();
            UiKit.Bindings.Clear();

            _canvasRoot = new GameObject("DivineHands_Panel",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_canvasRoot);
            var canvas = _canvasRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var scaler = _canvasRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // Root panel: fixed width, height grows with content. Blocks clicks under it
            // (matches the IMGUI window) — the SelectionGuard patch handles drag-selects.
            var panelGo = UiKit.NewChild(_canvasRoot, "Panel");
            _panelRt = (RectTransform)panelGo.transform;
            _panelRt.anchorMin = _panelRt.anchorMax = new Vector2(0, 1);
            _panelRt.pivot = new Vector2(0, 1);
            _panelRt.sizeDelta = new Vector2(PanelWidth, 0);
            UiKit.ApplyBorder(panelGo, FFAssets.PanelBorderCarved ?? FFAssets.PanelBorder, Color.white, raycast: true);

            var vlg = panelGo.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 10, 6, 9);
            vlg.spacing = 3;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            panelGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            BuildHeader(panelGo);

            _notInGame = UiKit.NewChild(panelGo, "NotInGame");
            _notInGame.AddComponent<LayoutElement>().preferredHeight = 22;
            UiKit.NewText(_notInGame, "Label", "Load a map to use god-powers.", 11, FFAssets.TextDim,
                TextAlignmentOptions.Center).rectTransform.SetStretch();
            UiKit.Bind(() => SetActive(_notInGame, !Plugin.InGame));

            _godSection = NewSection(panelGo, "GodTools");
            BuildGodTools(_godSection);
            UiKit.Bind(() => SetActive(_godSection, Plugin.InGame));

            _toolsSection = NewSection(panelGo, "Tools");
            BuildTools(_toolsSection);
            UiKit.Bind(() => SetActive(_toolsSection, Plugin.InGame));

            _injectSection = NewSection(panelGo, "Building");
            BuildInject(_injectSection);
            UiKit.Bind(() => SetActive(_injectSection, Plugin.InGame));

            _dangerSection = NewSection(panelGo, "Danger");
            BuildDanger(_dangerSection);
            UiKit.Bind(() => SetActive(_dangerSection,
                Plugin.InGame && (Config.DeleteEnable.Value || Config.KillEnable.Value)));

            UiKit.NewHint(panelGo, () => $"v{Plugin.Version} · Hotkeys & every setting: Keep Clarity panel (F10).", wrap: false);

            _pendingInitialPos = true;
            Plugin.Log.Msg("[Panel] uGUI panel built.");
        }

        private static void BuildHeader(GameObject parent)
        {
            var header = UiKit.NewChild(parent, "Header");
            header.AddComponent<LayoutElement>().preferredHeight = 27;
            var img = UiKit.ApplyBorder(header, FFAssets.PanelHeaderTop ?? FFAssets.PanelBorderDark,
                FFAssets.PanelTintHeader, raycast: true); // raycast: the drag handle
            _ = img;

            _drag = header.AddComponent<DraggablePanel>();
            _drag.Target = _panelRt;
            _drag.DefaultNormalizedPosition = new Vector2(0.02f, 0.88f);
            _drag.TopMargin = 44f; // FF's top bar

            var hlg = header.AddComponent<HorizontalLayoutGroup>();
            // Left padding clears the carved frame's ornate top-left corner sweep, which
            // otherwise curves straight through the title (FF insets its titles the same way).
            hlg.padding = new RectOffset(30, 5, 3, 3);
            hlg.spacing = 4;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;

            var title = UiKit.NewText(header, "Title", "Divine Hands", 14.5f, FFAssets.TextPrimary,
                TextAlignmentOptions.MidlineLeft, bold: true, smallCaps: true);
            title.characterSpacing = 2.5f;
            title.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;

            // (Version lives in the footer hint — the frame's top-right filigree owns this
            // corner, and text meshed into it.)

            // FF's round X close button
            var closeGo = UiKit.NewChild(header, "Close");
            closeGo.AddComponent<LayoutElement>().preferredWidth = 21;
            var closeImg = closeGo.AddComponent<Image>();
            var x = FFAssets.ExitRoundButton;
            if (x != null) { closeImg.sprite = x; closeImg.preserveAspect = true; }
            else closeImg.color = new Color(0.7f, 0.3f, 0.25f, 1f);
            closeImg.raycastTarget = true;
            var closeBtn = closeGo.AddComponent<Button>();
            closeBtn.targetGraphic = closeImg;
            closeBtn.transition = Selectable.Transition.ColorTint;
            closeBtn.onClick.AddListener(DivinePanel.Hide);
        }

        // ── God Tools: wrapping grid of toggle chips ─────────────────────────

        private static void BuildGodTools(GameObject section)
        {
            UiKit.SectionHeader(section, "God Tools");

            var grid = UiKit.NewChild(section, "ChipGrid");
            var glg = grid.AddComponent<GridLayoutGroup>();
            glg.cellSize = new Vector2(90, 19);
            glg.spacing = new Vector2(3, 3);
            glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            glg.constraintCount = 3;
            grid.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GodChip(grid, "Reveal Map", () => Config.EnableRevealMap.Value,
                () => Modules.GodTools.RevealActive, v => Modules.GodTools.RevealActive = v);
            GodChip(grid, "Build Any", () => Config.EnableBuildAnywhere.Value,
                () => Patches.BuildAnywherePatches.Active, v => Patches.BuildAnywherePatches.Active = v);
            GodChip(grid, "God View", () => Config.EnableGodView.Value,
                () => Modules.CameraTools.GodViewActive, v => Modules.CameraTools.GodViewActive = v);
            GodChip(grid, "Free Cam", () => Config.EnableFreeCam.Value,
                () => Modules.CameraTools.FreeCamActive, v => Modules.CameraTools.FreeCamActive = v);
            GodChip(grid, "Instant Build", () => Config.InstantBuildEnable.Value,
                () => Modules.InstantBuild.Active, v => Modules.InstantBuild.Active = v);

            UiKit.NewToggleRow(section, "Use materials (charge town storage)",
                () => Config.InstantBuildUseMaterials.Value, v => Config.InstantBuildUseMaterials.Value = v,
                visibleWhen: () => Config.InstantBuildEnable.Value && Modules.InstantBuild.Active, indent: 10);

            // One shared status/hint line: free-cam controls while flying, instant-build progress.
            UiKit.NewHint(section, () =>
            {
                if (Config.InstantBuildEnable.Value && Modules.InstantBuild.Active
                    && !string.IsNullOrEmpty(Modules.InstantBuild.Status))
                    return Modules.InstantBuild.Status;
                if (Config.EnableFreeCam.Value && Modules.CameraTools.FreeCamActive)
                    return $"{Config.FreeCamHotkey.Value} toggles · WASD fly · mouse look · Shift fast";
                bool any = Config.EnableRevealMap.Value || Config.EnableBuildAnywhere.Value
                        || Config.EnableGodView.Value || Config.EnableFreeCam.Value
                        || Config.InstantBuildEnable.Value;
                return any ? "" : "(enable god tools in the Keep Clarity settings)";
            });
        }

        private static void GodChip(GameObject grid, string label, Func<bool> enabled,
            Func<bool> get, Action<bool> set)
        {
            var chip = UiKit.NewChip(grid, label, () => set(!get()), fontSize: 10, flexible: false);
            UiKit.Bind(() =>
            {
                bool show = enabled();
                if (chip.Go.activeSelf != show) chip.Go.SetActive(show);
                if (show) chip.SetVisual(get());
            });
        }

        // ── Tools: tab chips + per-tool option groups ────────────────────────

        private static void BuildTools(GameObject section)
        {
            UiKit.SectionHeader(section, "Tools");

            var tabs = UiKit.NewRow(section, 20f, spacing: 3);
            ToolTab(tabs, "Terrain", 1, () => Config.TerrainEnable.Value);
            ToolTab(tabs, "Spawner", 2, () => Config.SpawnEnable.Value);
            ToolTab(tabs, "Lake", 3, () => Config.LakeEnable.Value);
            ToolTab(tabs, "Fertility", 4, () => Config.FertilityEnable.Value);

            // Drop a stale arm when its tool gets disabled in config; prompt when nothing armed.
            UiKit.Bind(() =>
            {
                int a = DivinePanel.ArmedIndex;
                if ((a == 1 && !Config.TerrainEnable.Value) || (a == 2 && !Config.SpawnEnable.Value)
                    || (a == 3 && !Config.LakeEnable.Value) || (a == 4 && !Config.FertilityEnable.Value))
                    DivinePanel.ArmedIndex = 0;
            });

            UiKit.NewHint(section, () =>
            {
                bool anyTool = Config.TerrainEnable.Value || Config.SpawnEnable.Value
                            || Config.LakeEnable.Value || Config.FertilityEnable.Value;
                if (!anyTool) return "(enable tools in the Keep Clarity settings)";
                return DivinePanel.ArmedIndex == 0 ? "Click a tab to arm a tool — it applies on its key while armed." : "";
            }, wrap: true);

            var terrain = NewSection(section, "TerrainOpts");
            BuildTerrainOptions(terrain);
            UiKit.Bind(() => SetActive(terrain, DivinePanel.ArmedIndex == 1));

            var spawner = NewSection(section, "SpawnerOpts");
            BuildSpawnerOptions(spawner);
            UiKit.Bind(() => SetActive(spawner, DivinePanel.ArmedIndex == 2));

            var lake = NewSection(section, "LakeOpts");
            BuildLakeOptions(lake);
            UiKit.Bind(() => SetActive(lake, DivinePanel.ArmedIndex == 3));

            var fertility = NewSection(section, "FertilityOpts");
            BuildFertilityOptions(fertility);
            UiKit.Bind(() => SetActive(fertility, DivinePanel.ArmedIndex == 4));
        }

        private static void ToolTab(GameObject row, string label, int index, Func<bool> enabled)
        {
            var chip = UiKit.NewChip(row, label, () =>
                DivinePanel.ArmedIndex = DivinePanel.ArmedIndex == index ? 0 : index, fontSize: 10.5f);
            UiKit.Bind(() =>
            {
                bool show = enabled();
                if (chip.Go.activeSelf != show) chip.Go.SetActive(show);
                if (show) chip.SetVisual(DivinePanel.ArmedIndex == index);
            });
        }

        private static readonly string[] TerrainModes = { "Raise", "Lower", "Smooth", "Flatten", "Avg" };

        private static void BuildTerrainOptions(GameObject box)
        {
            var modes = UiKit.NewRow(box, 18f, spacing: 2);
            for (int i = 0; i < TerrainModes.Length; i++)
            {
                int mode = i;
                var chip = UiKit.NewChip(modes, TerrainModes[i], () => Config.TerrainMode.Value = mode, fontSize: 9.5f);
                UiKit.Bind(() => chip.SetVisual(Mathf.Clamp(Config.TerrainMode.Value, 0, 4) == mode));
            }

            UiKit.NewSliderRow(box, "Strength", 0.05f, 25f, whole: false,
                () => Config.TerrainStrength.Value, v => Config.TerrainStrength.Value = v,
                () => $"{Config.TerrainStrength.Value:0.0} m",
                visibleWhen: () => Config.TerrainMode.Value != 3); // Flatten uses cursor height

            UiKit.NewSliderRow(box, "Width", 1f, 10f, whole: true,
                () => Config.TerrainGridWidth.Value, v => Config.TerrainGridWidth.Value = Mathf.RoundToInt(v),
                () => Config.TerrainGridWidth.Value.ToString());
            UiKit.NewSliderRow(box, "Depth", 1f, 10f, whole: true,
                () => Config.TerrainGridHeight.Value, v => Config.TerrainGridHeight.Value = Mathf.RoundToInt(v),
                () => Config.TerrainGridHeight.Value.ToString());

            UiKit.NewHint(box, () =>
            {
                int gw = Mathf.Clamp(Config.TerrainGridWidth.Value, 1, 10);
                int gh = Mathf.Clamp(Config.TerrainGridHeight.Value, 1, 10);
                float cell = Modules.TerrainElevation.CellMeters;
                return cell > 0f
                    ? $"Grid {gw} × {gh} cells ({gw * cell:0.#} × {gh * cell:0.#} m) · arrows resize, Tab swaps"
                    : $"Grid {gw} × {gh} cells · arrows resize, Tab swaps";
            });

            UiKit.NewToggleRow(box, "Fine positioning (½-grid steps, free-build play)",
                () => Config.TerrainGridFineSnap.Value, v => Config.TerrainGridFineSnap.Value = v);

            UiKit.NewHint(box, () =>
                $"Apply: {Config.TerrainApplyKey.Value}   Undo: {Config.TerrainUndoKey.Value} (depth {Modules.TerrainElevation.UndoDepth})");
        }

        private static readonly string[] Families = { "Animal", "Mineral", "Villager", "Resource" };
        private static readonly string[] AnimalKinds = { "Deer", "Bear", "Boar", "Wolf", "Fox", "Groundhog", "Dog", "Cat" };
        private static readonly string[] MineralKinds = { "Gold", "Iron", "Coal", "Stone", "Clay", "Sand" };
        private static readonly string[] ResourceKinds = { "Forage", "Tree", "Rock", "Boulder" };

        private static GameObject? _subtypeGrid;
        private static int _builtSubtypeFamily = -1;
        private static int _lastSpawnFamily = -1;
        private static int _lastSpawnSubtype = -1;

        private static void BuildSpawnerOptions(GameObject box)
        {
            var fams = UiKit.NewRow(box, 18f, spacing: 2);
            for (int i = 0; i < Families.Length; i++)
            {
                int fam = i;
                var chip = UiKit.NewChip(fams, Families[i], () =>
                {
                    if (Config.SpawnFamily.Value != fam) { Config.SpawnFamily.Value = fam; Config.SpawnSubtype.Value = 0; }
                }, fontSize: 9.5f);
                UiKit.Bind(() => chip.SetVisual(Mathf.Clamp(Config.SpawnFamily.Value, 0, 3) == fam));
            }

            _subtypeGrid = UiKit.NewChild(box, "SubtypeGrid");
            var glg = _subtypeGrid.AddComponent<GridLayoutGroup>();
            glg.cellSize = new Vector2(68, 18);
            glg.spacing = new Vector2(3, 3);
            glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            glg.constraintCount = 4;
            _subtypeGrid.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            UiKit.Bind(RefreshSubtypeGrid);

            // Fresh type defaults to a count of 1 (skip the very first frame, seeded -1).
            UiKit.Bind(() =>
            {
                int fam = Config.SpawnFamily.Value, sub = Config.SpawnSubtype.Value;
                if (_lastSpawnFamily != -1 && (fam != _lastSpawnFamily || sub != _lastSpawnSubtype))
                    Config.SpawnCount.Value = 1;
                _lastSpawnFamily = fam; _lastSpawnSubtype = sub;
            });

            UiKit.NewSliderRow(box, "Count", 1f, 50f, whole: true,
                () => Config.SpawnCount.Value, v => Config.SpawnCount.Value = Mathf.RoundToInt(v),
                () => Config.SpawnCount.Value.ToString());

            UiKit.NewToggleRow(box, "Persistent (Deer area, Wolf/Boar dens)",
                () => Config.SpawnPersistent.Value, v => Config.SpawnPersistent.Value = v,
                visibleWhen: () => Config.SpawnFamily.Value == 0);
            UiKit.NewToggleRow(box, "Deep deposit (gold/iron/coal — infinite)",
                () => Config.SpawnIsDeep.Value, v => Config.SpawnIsDeep.Value = v,
                visibleWhen: () => Config.SpawnFamily.Value == 1);

            UiKit.NewHint(box, () =>
            {
                string dlc = Config.SpawnFamily.Value == 0 ? DlcNote() : "";
                return $"Apply: {Config.SpawnApplyKey.Value}{dlc}";
            });
        }

        private static string DlcNote()
        {
            for (int i = 0; i < AnimalKinds.Length; i++)
                if (!Modules.CursorSpawners.DlcAnimalAvailable((Modules.CursorSpawners.AnimalKind)i))
                    return " · greyed animals need the Cats & Dogs DLC";
            return "";
        }

        private static readonly List<UiKit.Chip> _subtypeChips = new List<UiKit.Chip>();

        private static void RefreshSubtypeGrid()
        {
            if (_subtypeGrid == null) return;
            int family = Mathf.Clamp(Config.SpawnFamily.Value, 0, 3);
            string[]? kinds = family switch { 0 => AnimalKinds, 1 => MineralKinds, 3 => ResourceKinds, _ => null };

            if (kinds == null)   // Villager: no subtype picker
            {
                if (_subtypeGrid.activeSelf) _subtypeGrid.SetActive(false);
                _builtSubtypeFamily = family;
                return;
            }
            if (!_subtypeGrid.activeSelf) _subtypeGrid.SetActive(true);

            if (_builtSubtypeFamily != family)   // rebuild chips for the new family
            {
                _builtSubtypeFamily = family;
                foreach (var c in _subtypeChips) if (c.Go != null) UnityEngine.Object.Destroy(c.Go);
                _subtypeChips.Clear();
                for (int i = 0; i < kinds.Length; i++)
                {
                    int idx = i;
                    _subtypeChips.Add(UiKit.NewChip(_subtypeGrid, kinds[i], () =>
                    {
                        bool ok = family != 0 || Modules.CursorSpawners.DlcAnimalAvailable((Modules.CursorSpawners.AnimalKind)idx);
                        if (ok) Config.SpawnSubtype.Value = idx;
                    }, fontSize: 9.5f, flexible: false));
                }
            }

            int sub = Mathf.Clamp(Config.SpawnSubtype.Value, 0, kinds.Length - 1);
            for (int i = 0; i < _subtypeChips.Count; i++)
            {
                bool available = family != 0
                    || Modules.CursorSpawners.DlcAnimalAvailable((Modules.CursorSpawners.AnimalKind)i);
                _subtypeChips[i].SetVisual(sub == i, available);
            }
        }

        private static readonly string[] Shapes = { "Rectangle", "Circle" };

        private static void ShapeRow(GameObject box, Func<int> get, Action<int> set)
        {
            var row = UiKit.NewRow(box, 18f, spacing: 2);
            for (int i = 0; i < Shapes.Length; i++)
            {
                int shape = i;
                var chip = UiKit.NewChip(row, Shapes[i], () => set(shape), fontSize: 9.5f);
                UiKit.Bind(() => chip.SetVisual(Mathf.Clamp(get(), 0, 1) == shape));
            }
        }

        private static void BuildLakeOptions(GameObject box)
        {
            ShapeRow(box, () => Config.LakeShape.Value, v => Config.LakeShape.Value = v);

            UiKit.NewSliderRow(box, "Width", 1f, 10f, whole: true,
                () => Config.LakeGridWidth.Value, v => Config.LakeGridWidth.Value = Mathf.RoundToInt(v),
                () => Config.LakeGridWidth.Value.ToString());
            UiKit.NewSliderRow(box, "Depth", 1f, 10f, whole: true,
                () => Config.LakeGridHeight.Value, v => Config.LakeGridHeight.Value = Mathf.RoundToInt(v),
                () => Config.LakeGridHeight.Value.ToString());

            UiKit.NewHint(box, () =>
            {
                Modules.LakeStamp.FootprintExtents(out int lfw, out int lfh);
                float cell = Modules.TerrainElevation.CellMeters;
                return cell > 0f
                    ? $"Lake ~{2 * lfw * cell:0} × {2 * lfh * cell:0} m · arrows resize"
                    : $"Lake half-extent {lfw} × {lfh} cells · arrows resize";
            });

            UiKit.NewSliderRow(box, "Carve", 0.45f, 12f, whole: false,
                () => Config.LakeCarveDepth.Value, v => Config.LakeCarveDepth.Value = v,
                () => $"{Config.LakeCarveDepth.Value:0.0} m");
            UiKit.NewSliderRow(box, "Fill", 1f, 2f, whole: false,
                () => Config.LakeFillRatio.Value, v => Config.LakeFillRatio.Value = v,
                () => $"{Config.LakeFillRatio.Value:0.0}×");
            UiKit.NewSliderRow(box, "Shore", 2f, 40f, whole: true,
                () => Config.LakeShoreWidth.Value, v => Config.LakeShoreWidth.Value = Mathf.RoundToInt(v),
                () => Mathf.RoundToInt(Config.LakeShoreWidth.Value).ToString());

            UiKit.NewHint(box, () => $"Apply: {Config.LakeApplyKey.Value}   (more sliders: Keep Clarity)");
        }

        private static void BuildFertilityOptions(GameObject box)
        {
            UiKit.NewHint(box, () =>
            {
                if (Modules.SoilReadout.TryRead(out var soil))
                    return $"Soil @ cursor — crop {soil.crop * 100f:0}% · {Modules.SoilReadout.SandClayLabel(soil.sandClay)} ({soil.sandClay:0.00}) · water {soil.water * 100f:0}% · fruit {soil.fruitTree * 100f:0}%";
                return "Soil readout: hover over the map…";
            });

            ShapeRow(box, () => Config.FertilityShape.Value, v => Config.FertilityShape.Value = v);

            UiKit.NewSliderRow(box, "Width", 1f, 10f, whole: true,
                () => Config.FertilityGridWidth.Value, v => Config.FertilityGridWidth.Value = Mathf.RoundToInt(v),
                () => Config.FertilityGridWidth.Value.ToString());
            UiKit.NewSliderRow(box, "Depth", 1f, 10f, whole: true,
                () => Config.FertilityGridHeight.Value, v => Config.FertilityGridHeight.Value = Mathf.RoundToInt(v),
                () => Config.FertilityGridHeight.Value.ToString());
            UiKit.NewSliderRow(box, "Fertility", 0f, 100f, whole: true,
                () => Config.FertilityAmount.Value, v => Config.FertilityAmount.Value = v,
                () => $"{Mathf.RoundToInt(Config.FertilityAmount.Value)}%");

            UiKit.NewToggleRow(box, "Condition soil for orchards",
                () => Config.FertilityConditionSoil.Value, v => Config.FertilityConditionSoil.Value = v);

            UiKit.NewHint(box, () =>
            {
                int gw = Mathf.Clamp(Config.FertilityGridWidth.Value, 1, 10);
                int gh = Mathf.Clamp(Config.FertilityGridHeight.Value, 1, 10);
                float cell = Modules.TerrainElevation.CellMeters;
                string area = cell > 0f ? $"~{2 * gw * cell:0} × {2 * gh * cell:0} m" : $"{gw} × {gh} cells";
                return $"Area {area} · arrows resize · Apply: {Config.FertilityApplyKey.Value}";
            });
        }

        // ── Selected Building (item injection) ──────────────────────────────

        private static readonly string[] LivestockKinds = { "Cow", "Chicken", "Goat", "Horse" };
        private static GameObject? _injectBody;
        private static GameObject? _itemGridContent;
        private static readonly List<UiKit.Chip> _itemChips = new List<UiKit.Chip>();
        private static string[] _builtItemNames = Array.Empty<string>();

        private static void BuildInject(GameObject section)
        {
            UiKit.SectionHeader(section, "Selected Building");

            UiKit.NewToggleRow(section, "Enable item injection",
                () => Config.InjectEnable.Value, v => Config.InjectEnable.Value = v);

            _injectBody = NewSection(section, "InjectBody");
            var body = _injectBody;

            UiKit.NewHint(body, () =>
            {
                string name = Modules.ItemInjection.GetSelectedBuildingName();
                return string.IsNullOrEmpty(name) ? "Select a building in-game." : $"Selected: {name}";
            }, wrap: false);

            // Scrollable item chip grid (fixed height keeps the panel short — same as IMGUI's 96px view).
            var scrollGo = UiKit.NewChild(body, "ItemScroll");
            scrollGo.AddComponent<LayoutElement>().preferredHeight = 100;
            var scrollBg = scrollGo.AddComponent<Image>();
            var bgSprite = FFAssets.PanelBorderSimple;
            if (bgSprite != null) { scrollBg.sprite = bgSprite; scrollBg.type = Image.Type.Sliced; }
            scrollBg.color = new Color(0.10f, 0.09f, 0.08f, 0.65f);
            scrollBg.raycastTarget = true;
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 18f;

            var viewport = UiKit.NewChild(scrollGo, "Viewport");
            var vpRt = (RectTransform)viewport.transform;
            vpRt.anchorMin = Vector2.zero; vpRt.anchorMax = Vector2.one;
            vpRt.offsetMin = new Vector2(3, 3); vpRt.offsetMax = new Vector2(-3, -3);
            viewport.AddComponent<RectMask2D>();
            scroll.viewport = vpRt;

            _itemGridContent = UiKit.NewChild(viewport, "Content");
            var contentRt = (RectTransform)_itemGridContent.transform;
            contentRt.anchorMin = new Vector2(0, 1); contentRt.anchorMax = new Vector2(1, 1);
            contentRt.pivot = new Vector2(0.5f, 1);
            var glg = _itemGridContent.AddComponent<GridLayoutGroup>();
            glg.cellSize = new Vector2(66, 17);
            glg.spacing = new Vector2(2, 2);
            glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            glg.constraintCount = 4;
            _itemGridContent.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = contentRt;
            UiKit.Bind(RefreshItemGrid);

            UiKit.NewSliderRow(body, "Count", 1f, 9999f, whole: true,
                () => Config.InjectItemCount.Value, v => Config.InjectItemCount.Value = Mathf.RoundToInt(v),
                () => Config.InjectItemCount.Value.ToString());

            UiKit.NewButton(body, "Add Items", () =>
            {
                var names = Modules.ItemInjection.ItemNames;
                if (names.Length == 0) return;
                int idx = Mathf.Clamp(Config.InjectItemIndex.Value, 0, names.Length - 1);
                _injectStatus = Modules.ItemInjection.AddItems(names[idx], Mathf.Clamp(Config.InjectItemCount.Value, 1, 9999));
            }, liveLabel: () =>
            {
                var names = Modules.ItemInjection.ItemNames;
                if (names.Length == 0) return "Add Items";
                int idx = Mathf.Clamp(Config.InjectItemIndex.Value, 0, names.Length - 1);
                return $"Add {Mathf.Clamp(Config.InjectItemCount.Value, 1, 9999)}× {names[idx]}";
            });

            var stock = UiKit.NewRow(body, 18f, spacing: 2);
            for (int i = 0; i < LivestockKinds.Length; i++)
            {
                int kind = i;
                var chip = UiKit.NewChip(stock, LivestockKinds[i], () => Config.InjectLivestockKind.Value = kind, fontSize: 9.5f);
                UiKit.Bind(() => chip.SetVisual(Mathf.Clamp(Config.InjectLivestockKind.Value, 0, 3) == kind));
            }

            UiKit.NewButton(body, "Add Livestock", () =>
            {
                int kind = Mathf.Clamp(Config.InjectLivestockKind.Value, 0, LivestockKinds.Length - 1);
                _injectStatus = Modules.ItemInjection.AddLivestock((Modules.ItemInjection.LivestockKind)kind);
            }, liveLabel: () =>
                $"Add 1× {LivestockKinds[Mathf.Clamp(Config.InjectLivestockKind.Value, 0, 3)]}");

            UiKit.NewToggleRow(body, "Infinite storage (session-only, save-safe)",
                () => Modules.ItemInjection.IsSelectedInfinite(),
                v => _injectStatus = Modules.ItemInjection.ToggleSelectedInfinite());

            UiKit.NewHint(body, () => _injectStatus, wrap: true);

            UiKit.Bind(() => SetActive(_injectBody,
                Config.InjectEnable.Value
                && !string.IsNullOrEmpty(Modules.ItemInjection.GetSelectedBuildingName())));
        }

        private static void RefreshItemGrid()
        {
            if (_itemGridContent == null || _injectBody == null || !_injectBody.activeSelf) return;
            var names = Modules.ItemInjection.ItemNames;
            if (names == null || names.Length == 0) return;

            if (!ReferenceEquals(names, _builtItemNames))   // (re)build once per item-table instance
            {
                _builtItemNames = names;
                foreach (var c in _itemChips) if (c.Go != null) UnityEngine.Object.Destroy(c.Go);
                _itemChips.Clear();
                for (int i = 0; i < names.Length; i++)
                {
                    int idx = i;
                    _itemChips.Add(UiKit.NewChip(_itemGridContent, names[i], () =>
                    {
                        bool eligible = !Modules.ItemInjection.SelectedBuildingHasAllowList()
                            || Modules.ItemInjection.IsItemEligibleForSelectedBuilding(_builtItemNames[idx]);
                        if (eligible) Config.InjectItemIndex.Value = idx;
                    }, fontSize: 8.5f, flexible: false));
                }
            }

            bool hasAllowList = Modules.ItemInjection.SelectedBuildingHasAllowList();
            int sel = Mathf.Clamp(Config.InjectItemIndex.Value, 0, names.Length - 1);
            for (int i = 0; i < _itemChips.Count; i++)
            {
                bool eligible = !hasAllowList || Modules.ItemInjection.IsItemEligibleForSelectedBuilding(names[i]);
                _itemChips[i].SetVisual(sel == i, eligible);
            }
        }

        // ── Danger: Delete / Kill selected (compact shared section) ─────────

        private static void BuildDanger(GameObject section)
        {
            UiKit.SectionHeader(section, "Danger");

            var delRow = UiKit.NewRow(section, 20f);
            var delDesc = UiKit.NewText(delRow, "Desc", "", 9.5f, FFAssets.TextDim);
            delDesc.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            var delBtnGo = UiKit.NewChild(delRow, "BtnHolder");
            delBtnGo.AddComponent<LayoutElement>().preferredWidth = 86;
            var delHl = delBtnGo.AddComponent<HorizontalLayoutGroup>();
            delHl.childControlWidth = true; delHl.childControlHeight = true; delHl.childForceExpandWidth = true;
            var delBtn = UiKit.NewButton(delBtnGo, "Delete", () => _deleteStatus = Modules.DeleteSelected.DeleteCurrent(), height: 19, fontSize: 10);
            UiKit.Bind(() =>
            {
                bool show = Config.DeleteEnable.Value;
                if (delRow.activeSelf != show) delRow.SetActive(show);
                if (!show) return;
                Modules.DeleteSelected.TryDescribe(out string label, out bool deletable);
                if (delDesc.text != label) delDesc.text = label;
                delBtn.interactable = deletable;
            });

            var killRow = UiKit.NewRow(section, 20f);
            var killDesc = UiKit.NewText(killRow, "Desc", "", 9.5f, FFAssets.TextDim);
            killDesc.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            var killBtnGo = UiKit.NewChild(killRow, "BtnHolder");
            killBtnGo.AddComponent<LayoutElement>().preferredWidth = 86;
            var killHl = killBtnGo.AddComponent<HorizontalLayoutGroup>();
            killHl.childControlWidth = true; killHl.childControlHeight = true; killHl.childForceExpandWidth = true;
            var killBtn = UiKit.NewButton(killBtnGo, "Kill", () => _killStatus = Modules.KillSelected.KillCurrent(), height: 19, fontSize: 10);
            UiKit.Bind(() =>
            {
                bool show = Config.KillEnable.Value;
                if (killRow.activeSelf != show) killRow.SetActive(show);
                if (!show) return;
                Modules.KillSelected.TryDescribe(out string label, out bool killable);
                if (killDesc.text != label) killDesc.text = label;
                killBtn.interactable = killable;
            });

            UiKit.NewHint(section, () =>
            {
                string s = "";
                if (Config.DeleteEnable.Value)
                {
                    s = $"Delete: {Config.DeleteHotkey.Value}" + (Config.DeleteRefund.Value ? " (refunds)" : "");
                    if (!string.IsNullOrEmpty(_deleteStatus)) s += " — " + _deleteStatus;
                }
                if (Config.KillEnable.Value)
                {
                    if (s.Length > 0) s += "   ";
                    s += $"Kill: {Config.KillHotkey.Value}";
                    if (!string.IsNullOrEmpty(_killStatus)) s += " — " + _killStatus;
                }
                return s;
            });
        }

        // ── shared bits ──────────────────────────────────────────────────────

        private static GameObject NewSection(GameObject parent, string name)
        {
            var go = UiKit.NewChild(parent, name);
            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 1, 1);
            vlg.spacing = 3;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return go;
        }

        private static void SetActive(GameObject? go, bool active)
        {
            if (go != null && go.activeSelf != active) go.SetActive(active);
        }
    }

    internal static class RectTransformExtensions
    {
        public static void SetStretch(this RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
