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
    /// <c>ISelectable</c>, so <c>Input_SelectRaider</c> [118056] holds the selected raider in a private
    /// state field. We reach it by walking the input state machine
    /// (<c>InputManager.inputStateMachine → StateMachine.currentState → StackState.pushedState…</c>) — O(depth),
    /// no scene scan (an earlier FindObjectsOfType approach lagged the whole game while the panel was open).
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

        // Channel-3 (raider) state-machine walk handles — all private FF fields, resolved once.
        private static Type? _tStackState, _tInputSelectRaider;
        private static FieldInfo? _fInputStateMachine, _fCurrentState, _fPushedState, _fRaider;

        /// <summary>Every currently-selected creature GameObject, de-duplicated. All three channels are
        /// cheap: field reads + an O(depth) walk of the input state stack for the raider. No scene scan.</summary>
        private static List<GameObject> SelectedCreatures()
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
                // selected raider leaves 1 & 2 empty. Cheap state-machine walk, no scan.
                if (result.Count == 0)
                {
                    var raiderGo = FindSelectedRaider(im);
                    if (raiderGo != null && IsCreature(raiderGo)) result.Add(raiderGo);
                }
            }
            catch { }
            return result;
        }

        /// <summary>Walk InputManager.inputStateMachine → currentState → pushedState… looking for an
        /// Input_SelectRaider, and return its raider's GameObject. All private, so fully reflected + guarded.</summary>
        private static GameObject? FindSelectedRaider(object inputManager)
        {
            if (_fInputStateMachine == null || _fCurrentState == null || _fRaider == null) return null;
            try
            {
                object? sm = _fInputStateMachine.GetValue(inputManager);
                if (sm == null) return null;
                object? state = _fCurrentState.GetValue(sm);
                for (int guard = 0; state != null && guard < 32; guard++)
                {
                    if (_tInputSelectRaider != null && _tInputSelectRaider.IsInstanceOfType(state))
                    {
                        var raider = _fRaider.GetValue(state) as Component;
                        return raider != null ? raider.gameObject : null;
                    }
                    // Descend the pushed-state chain (StackState.pushedState); stop if not a StackState.
                    state = (_fPushedState != null && _tStackState != null && _tStackState.IsInstanceOfType(state))
                        ? _fPushedState.GetValue(state) : null;
                }
            }
            catch { }
            return null;
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
            var targets = SelectedCreatures();
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

                // Channel-3 raider walk (best-effort — if any handle is missing, raiders just aren't
                // detected; villagers/pets/animals still work).
                const BindingFlags PI = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var tInputManager = AccessTools.TypeByName("InputManager");
                var tStateMachine = AccessTools.TypeByName("StateMachine");
                _tStackState        = AccessTools.TypeByName("StackState");
                _tInputSelectRaider = AccessTools.TypeByName("Input_SelectRaider");
                _fInputStateMachine = tInputManager?.GetField("inputStateMachine", PI);
                _fCurrentState      = tStateMachine?.GetField("currentState", PI);
                _fPushedState       = _tStackState?.GetField("pushedState", PI);
                _fRaider            = _tInputSelectRaider?.GetField("raider", PI);

                _resolveFailed = _mKill == null || _debugDamage == null || (_tCharacter == null && _tLandAnimal == null);
                if (_resolveFailed) MelonLogger.Warning("[DivineHands] Kill: API unresolved — disabled");
                return !_resolveFailed;
            }
            catch (Exception ex) { _resolveFailed = true; MelonLogger.Warning($"[DivineHands] Kill resolve: {ex.Message}"); return false; }
        }
    }
}
