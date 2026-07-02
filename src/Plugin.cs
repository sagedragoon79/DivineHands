using MelonLoader;
using UnityEngine;
using DivineHands.Core;
using DivineHands.Modules;
using DivineHands.Patches;

[assembly: MelonInfo(typeof(DivineHands.Plugin), "Divine Hands", DivineHands.Plugin.Version, "sagedragoon79")]
[assembly: MelonGame("Crate Entertainment", "Farthest Frontier")]

namespace DivineHands
{
    /// <summary>
    /// Divine Hands — the god-power / creator-tool pillar of the FF mod constellation.
    ///
    /// Design rules:
    ///   - One in-game panel (<see cref="DivinePanel"/>) is the shared control surface for
    ///     every god-power; toggled by a configurable hotkey (default Ctrl+G).
    ///   - All persistent settings live in <see cref="Config"/> (MelonPreferences) and render
    ///     in the Keep Clarity settings panel via the reflective SettingsAPI (soft-dep).
    ///   - Every capability resolves the overlaps catalogued from 10 source mods into ONE
    ///     clean call path into the verified FF API — never patch a method another mod owns.
    ///   - Cheats that bake into the save (e.g. infinite storage) get a map-load cleanup sweep.
    ///
    /// v0.1 scope: DivineCore (panel + KC + raycast helper) + GodTools (Reveal Map) as the
    /// proof-of-life. Terrain elevation, cursor spawners, and the rest land per the roadmap.
    /// </summary>
    public class Plugin : MelonMod
    {
        /// <summary>Single source of truth for the version — used by MelonInfo, the init log,
        /// and the Keep Clarity registration so they can't drift. Bump with the .csproj
        /// &lt;Version&gt; on release.</summary>
        public const string Version = "0.1.0";

        public static Plugin Instance { get; private set; } = null!;
        public static MelonLogger.Instance Log => Instance.LoggerInstance;

        /// <summary>True while a "Map" scene is the active scene — gates god-powers that
        /// require a live game (FOWSystem, terrain, managers).</summary>
        public static bool InGame { get; private set; }

        public override void OnInitializeMelon()
        {
            Instance = this;

            Config.Initialize();
            KeepClarityIntegration.TryRegisterAll();

            // Harmony patches: currently just the IMGUI-over-game input guard.
            HarmonyInstance.PatchAll(typeof(Plugin).Assembly);

            LoggerInstance.Msg($"Divine Hands {Version} initialized — panel hotkey: {Config.PanelHotkey.Value}");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            InGame = sceneName == "Map";
            if (!InGame)
            {
                DivinePanel.Hide();
                // Force every live god-power OFF on leaving the map so none can linger into the next
                // load. (Live state is a runtime flag, not a saved pref — see Group A enable/activate split.)
                GodTools.ResetActive();
                CameraTools.ResetActive();
                BuildAnywherePatches.Active = false;
                InstantBuild.ResetActive();
                InstantBuild.OnSceneExit();
                GodTools.OnSceneExit();
                CameraTools.OnSceneExit();
                TerrainElevation.OnSceneExit();
                LakeStamp.OnSceneExit();
                FertilityBrush.OnSceneExit();
                TerrainBrushGrid.OnSceneExit();
                BrushPreview.OnSceneExit();
                CursorSpawners.OnSceneExit();
                DeleteSelected.OnSceneExit();
                ItemInjection.OnSceneExit(); // reverts session-infinite storage BEFORE any save
                return;
            }
            // Entering a Map: live god-powers always start OFF regardless of their Enable prefs.
            GodTools.ResetActive();
            CameraTools.ResetActive();
            BuildAnywherePatches.Active = false;
            InstantBuild.ResetActive();
            InstantBuild.OnMapLoaded();
            GodTools.OnMapLoaded();
            CameraTools.OnMapLoaded();
            TerrainElevation.OnMapLoaded();
            LakeStamp.OnMapLoaded();
            FertilityBrush.OnMapLoaded();
            TerrainBrushGrid.OnMapLoaded();
            BrushPreview.OnMapLoaded();
            CursorSpawners.OnMapLoaded();
            DeleteSelected.OnMapLoaded();
            ItemInjection.OnMapLoaded();

            // Restore the persisted live state for the powers the user wants to survive a save/reload
            // (Reveal Map / Build Anywhere / God View). Set AFTER each module's OnMapLoaded (which forces
            // an off baseline) so the per-frame Sync applies them once the managers are ready. Gated on
            // MasterEnable + each power's Enable pref. Free Cam intentionally stays off on load.
            if (Config.MasterEnable.Value)
            {
                GodTools.RevealActive          = Config.EnableRevealMap.Value    && Config.PersistRevealActive.Value;
                BuildAnywherePatches.Active     = Config.EnableBuildAnywhere.Value && Config.PersistBuildAnywhereActive.Value;
                CameraTools.GodViewActive       = Config.EnableGodView.Value      && Config.PersistGodViewActive.Value;
            }
        }

        private static bool _wasMasterEnabled = true;

        // Last-mirrored persist values (seeded impossible so the first frame always writes).
        private static int _persistRevealLast = -1, _persistBuildLast = -1, _persistGodViewLast = -1;

        private static void MirrorPersist(MelonLoader.MelonPreferences_Entry<bool> pref, bool live, ref int last)
        {
            int now = live ? 1 : 0;
            if (now == last) return;
            last = now;
            pref.Value = live;
        }

        public override void OnUpdate()
        {
            // If the master switch was just turned off, revert session-only cheats (infinite storage)
            // so they can't linger into a save, then go inert.
            if (_wasMasterEnabled && !Config.MasterEnable.Value)
                ItemInjection.OnMasterDisabled();
            _wasMasterEnabled = Config.MasterEnable.Value;

            if (!Config.MasterEnable.Value) return;

            // Toggle the shared god-power panel on the configurable hotkey.
            if (Hotkey.Pressed(Config.PanelHotkey.Value))
                DivinePanel.Toggle();

            if (InGame)
            {
                // Arm Terrain / Spawner straight from the keyboard (no tab click) — re-press disarms.
                if (Hotkey.Pressed(Config.TerrainArmHotkey.Value))   DivinePanel.ToggleArmTerrain();
                if (Hotkey.Pressed(Config.SpawnerArmHotkey.Value))   DivinePanel.ToggleArmSpawner();
                if (Hotkey.Pressed(Config.LakeArmHotkey.Value))      DivinePanel.ToggleArmLake();
                if (Hotkey.Pressed(Config.FertilityArmHotkey.Value)) DivinePanel.ToggleArmFertility();

                // Delete the current selection on its hotkey (mine/quarry/pit/deep-mine/ore node/any
                // building). Gated on the feature enable; a no-op when nothing deletable is selected.
                if (Config.DeleteEnable.Value && Hotkey.Pressed(Config.DeleteHotkey.Value))
                    DeleteSelected.DeleteCurrent();

                // Kill the selected villager/animal on its hotkey (testing aid). No-op unless a living
                // creature is selected.
                if (Config.KillEnable.Value && Hotkey.Pressed(Config.KillHotkey.Value))
                    KillSelected.KillCurrent();

                GodTools.OnUpdate();
                CameraTools.OnUpdate();
                InstantBuild.OnUpdate();
                TerrainElevation.OnUpdate();
                LakeStamp.OnUpdate();
                FertilityBrush.OnUpdate();
                CursorSpawners.OnUpdate();
                ItemInjection.OnUpdate(); // drives post-save re-apply of session-infinite flags
                TerrainBrushGrid.Render(); // after the brush so it reads fresh cursor/grid state
                BrushPreview.Render();     // lake/fertility footprint outline at the cursor

                // Mirror the live power state into the persist prefs (only in-game, only on change) so
                // Reveal Map / Build Anywhere / God View come back after a save/reload. Change-guarded so
                // we don't dirty MelonPreferences every frame.
                MirrorPersist(Config.PersistRevealActive, GodTools.RevealActive, ref _persistRevealLast);
                MirrorPersist(Config.PersistBuildAnywhereActive, BuildAnywherePatches.Active, ref _persistBuildLast);
                MirrorPersist(Config.PersistGodViewActive, CameraTools.GodViewActive, ref _persistGodViewLast);
            }
        }

        public override void OnGUI()
        {
            if (!Config.MasterEnable.Value) return;
            DivinePanel.Render();
        }
    }
}
