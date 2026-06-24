using MelonLoader;
using UnityEngine;
using DivineHands.Core;
using DivineHands.Modules;

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

            LoggerInstance.Msg($"Divine Hands {Version} initialized — panel hotkey: {Config.PanelHotkey.Value}");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            InGame = sceneName == "Map";
            if (!InGame)
            {
                DivinePanel.Hide();
                GodTools.OnSceneExit();
                return;
            }
            GodTools.OnMapLoaded();
        }

        public override void OnUpdate()
        {
            if (!Config.MasterEnable.Value) return;

            // Toggle the shared god-power panel on the configurable hotkey.
            if (Hotkey.Pressed(Config.PanelHotkey.Value))
                DivinePanel.Toggle();

            if (InGame)
                GodTools.OnUpdate();
        }

        public override void OnGUI()
        {
            if (!Config.MasterEnable.Value) return;
            DivinePanel.Render();
        }
    }
}
