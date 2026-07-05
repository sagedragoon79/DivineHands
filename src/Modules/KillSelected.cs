using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using MelonLoader;

namespace DivineHands.Modules
{
    /// <summary>
    /// "Kill Selected" god tool — instantly kills the selected LIVING creature(s): villagers and raiders
    /// (both <c>… : Character</c>) and any animal (wild / livestock / pet — all <c>… : LandAnimal</c>).
    /// Mainly a testing aid, but this is a god mod.
    ///
    /// THREE selection CHANNELS (this is why earlier cuts missed things): (1) wild animals and world
    /// objects go into <c>inputManager.selectedObject</c> (a single GameObject); (2) villagers and pets
    /// select through <c>inputManager.SelectSelectable</c> into <c>inputManager.selectedObjs</c> — an
    /// <c>ObservableHashSet&lt;ISelectable&gt;</c> [110702]; (3) RAIDERS are neither — <c>Raider</c> isn't
    /// <c>ISelectable</c>, so <c>Input_SelectRaider</c> [118056] only flips the raider's
    /// <c>SelectableComponent.IsSelected</c> (and holds it in a private state field). FF is single-select
    /// and deselects cleanly on exit, so we only fall back to scanning <c>SelectableComponent.IsSelected</c>
    /// (throttled) when channels 1 &amp; 2 are empty — the raider case.
    ///
    /// Recipe (verified): every creature carries a <c>DamageableComponent</c> (CombatComponent derives
    /// from it) whose <c>Kill(GameObject damageCauser, DamageType)</c> [74067] sets life to 0 and fires
    /// <c>OnDeath</c> — the game's own death path, so villagers/raiders run ProcessDeath and animals
    /// raise their died-events. We deal a <c>DamageType(DamageTypeFlags.Debug)</c> for a debug death reason.
    ///
    /// Gated to Character/LandAnimal so it can't nuke a building (buildings are IDamageable too — use
    /// Delete Selected for those). Reflection-only + fully guarded. Off by default.
    /// </summary>
    public static class KillSelected
    {
        private static bool _resolved, _resolveFailed;
        private static Type? _tCharacter, _tLandAnimal, _tDamageable, _tDamageType, _tFlags;
        private static MethodInfo? _mKill;
        private static object? _debugDamage; // boxed DamageType(Debug) — struct, built once

        // Throttled cache for the channel-3 (raider) scan, so per-frame TryDescribe never scans the scene
        // more than ~3x/sec.
        private static readonly List<GameObject> _scanCache = new List<GameObject>();
        private static float _lastScanTime = -999f;

        /// <summary>Every currently-selected creature GameObject, de-duplicated. Channels 1 &amp; 2 are cheap
        /// field reads; channel 3 (the raider scan) only runs when 1 &amp; 2 are empty. <paramref name="freshScan"/>
        /// forces an immediate channel-3 scan (used by KillCurrent); otherwise it's throttled.</summary>
        private static List<GameObject> SelectedCreatures(bool freshScan = false)
        {
            var result = new List<GameObject>();
            try
            {
                var gm = GameManager.Instance;
                var im = gm != null ? gm.inputManager : null;
                if (im == null) return result;

                // Channel 1: the single selected world object (wild animals, buildings, nodes…).
                var so = im.selectedObject;
                if (so != null && IsCreature(so)) result.Add(so);

                // Channel 2: the ISelectable set (villagers / pets). ObservableHashSet : HashSet, so it
                // enumerates directly; each element is a MonoBehaviour, so cast to Component for its GO.
                if (im.selectedObjs is IEnumerable set)
                {
                    foreach (var s in set)
                    {
                        var go = (s as Component) != null ? ((Component)s).gameObject : null;
                        if (go != null && IsCreature(go) && !result.Contains(go)) result.Add(go);
                    }
                }

                // Channel 3 (raiders): only when nothing else is selected — FF single-select means a
                // selected raider leaves 1 & 2 empty. Throttled scene scan of SelectableComponent.IsSelected.
                if (result.Count == 0)
                {
                    RefreshRaiderScan(freshScan);
                    result.AddRange(_scanCache);
                }
            }
            catch { }
            return result;
        }

        private static void RefreshRaiderScan(bool force)
        {
            if (!force && Time.realtimeSinceStartup - _lastScanTime < 0.30f) return;
            _lastScanTime = Time.realtimeSinceStartup;
            _scanCache.Clear();
            try
            {
                // SelectableComponent.IsSelected is set on Select() and cleared on Deselect() (raider exit
                // path calls Deselect), so a true flag reliably marks the current selection.
                foreach (var sc in UnityEngine.Object.FindObjectsOfType<SelectableComponent>())
                    if (sc != null && sc.IsSelected && IsCreature(sc.gameObject)) _scanCache.Add(sc.gameObject);
            }
            catch { }
        }

        /// <summary>For the panel: a label for the current selection + whether Kill would act on it.</summary>
        public static bool TryDescribe(out string label, out bool killable)
        {
            killable = false;
            Resolve();
            var targets = SelectedCreatures();
            if (targets.Count == 0) { label = "(no living creature selected)"; return false; }
            killable = true;
            label = targets.Count == 1 ? $"{targets[0].name}  [living]" : $"{targets.Count} creatures  [living]";
            return true;
        }

        /// <summary>Kill every selected creature. Returns a short status string for the panel.</summary>
        public static string KillCurrent()
        {
            if (!Resolve()) return "Kill API unavailable";
            var targets = SelectedCreatures(freshScan: true);
            if (targets.Count == 0) return "No living creature selected";
            int killed = 0;
            string last = "";
            foreach (var go in targets)
            {
                try
                {
                    var dc = FindDamageable(go);
                    if (dc == null) continue;
                    _mKill!.Invoke(dc, new object?[] { null, _debugDamage });
                    killed++; last = go.name;
                }
                catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Kill failed on {go.name}: {ex.Message}"); }
            }
            if (killed == 0) return "Nothing killable (no DamageableComponent)";
            return killed == 1 ? $"Killed {last}" : $"Killed {killed} creatures";
        }

        private static bool IsCreature(GameObject go)
        {
            try
            {
                // Character is the base of Villager AND Raider; LandAnimal the base of wild/livestock/pets.
                if (_tCharacter != null && go.GetComponent(_tCharacter) != null) return true;
                if (_tLandAnimal != null && go.GetComponent(_tLandAnimal) != null) return true;
            }
            catch { }
            return false;
        }

        private static Component? FindDamageable(GameObject go)
        {
            if (_tDamageable == null) return null;
            var dc = go.GetComponent(_tDamageable);
            if (dc == null) dc = go.GetComponentInChildren(_tDamageable);
            return dc;
        }

        private static bool Resolve()
        {
            if (_resolved) return !_resolveFailed;
            _resolved = true;
            try
            {
                _tCharacter  = AccessTools.TypeByName("Character");   // Villager + Raider base
                _tLandAnimal = AccessTools.TypeByName("LandAnimal");  // wild / livestock / pet base
                _tDamageable = AccessTools.TypeByName("DamageableComponent");
                _tDamageType = AccessTools.TypeByName("DamageType");

                if (_tDamageable != null && _tDamageType != null)
                    _mKill = _tDamageable.GetMethod("Kill", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(GameObject), _tDamageType }, null);

                // Build a boxed DamageType(DamageTypeFlags.Debug) once — struct value we pass to every Kill.
                if (_tDamageType != null)
                {
                    _tFlags = _tDamageType.GetNestedType("DamageTypeFlags", BindingFlags.Public | BindingFlags.NonPublic);
                    var ctor = _tDamageType.GetConstructor(new[] { _tFlags!, typeof(int), typeof(string) });
                    object debugFlag = Enum.ToObject(_tFlags!, 2048 /* Debug */);
                    _debugDamage = ctor != null
                        ? ctor.Invoke(new object[] { debugFlag, 0, "Divine Hands" })
                        : Activator.CreateInstance(_tDamageType); // fallback: default(DamageType)
                }

                _resolveFailed = _mKill == null || _debugDamage == null || (_tCharacter == null && _tLandAnimal == null);
                if (_resolveFailed) MelonLogger.Warning("[DivineHands] Kill: API unresolved — disabled");
                return !_resolveFailed;
            }
            catch (Exception ex) { _resolveFailed = true; MelonLogger.Warning($"[DivineHands] Kill resolve: {ex.Message}"); return false; }
        }
    }
}
