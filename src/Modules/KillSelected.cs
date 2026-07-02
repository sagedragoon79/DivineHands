using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using MelonLoader;

namespace DivineHands.Modules
{
    /// <summary>
    /// "Kill Selected" god tool — instantly kills the selected LIVING creature: a villager
    /// (<c>Villager : Character</c>) or any animal (wild / livestock / pet — all
    /// <c>… : LandAnimal</c>). Mainly a testing aid, but this is a god mod.
    ///
    /// Recipe (verified against the decompile): every creature carries a <c>DamageableComponent</c>
    /// (CombatComponent derives from it) whose <c>Kill(GameObject damageCauser, DamageType)</c> [74067]
    /// sets life to 0 and fires <c>OnDeath</c> — the game's own death path, so villagers run
    /// ProcessDeath / population removal and animals raise their died-events. We deal a
    /// <c>DamageType(DamageTypeFlags.Debug)</c> so the death reason reads as a debug kill.
    ///
    /// Gated to Villager/LandAnimal so it can't nuke a building (buildings are IDamageable too — use
    /// Delete Selected for those). Reflection-only + fully guarded. Off by default.
    /// </summary>
    public static class KillSelected
    {
        private static bool _resolved, _resolveFailed;
        private static Type? _tVillager, _tLandAnimal, _tDamageable, _tDamageType, _tFlags;
        private static MethodInfo? _mKill;
        private static object? _debugDamage; // boxed DamageType(Debug) — struct, built once

        /// <summary>Raw currently-selected GameObject (creature OR anything) — before any filter.</summary>
        private static GameObject? RawSelected()
        {
            try { var gm = GameManager.Instance; return gm != null && gm.inputManager != null ? gm.inputManager.selectedObject : null; }
            catch { return null; }
        }

        /// <summary>For the panel: a label for the current selection + whether Kill would act on it.</summary>
        public static bool TryDescribe(out string label, out bool killable)
        {
            killable = false;
            var go = RawSelected();
            if (go == null) { label = "(nothing selected)"; return false; }
            Resolve();
            killable = IsCreature(go);
            label = killable ? $"{go.name}  [living]" : $"{go.name}  [not a creature]";
            return true;
        }

        /// <summary>Kill the current selection. Returns a short status string for the panel.</summary>
        public static string KillCurrent()
        {
            var go = RawSelected();
            if (go == null) return "Nothing selected";
            if (!Resolve()) return "Kill API unavailable";
            if (!IsCreature(go)) return $"{go.name} isn't a killable creature";
            try
            {
                var dc = FindDamageable(go);
                if (dc == null) return $"{go.name} has no DamageableComponent";
                _mKill!.Invoke(dc, new object?[] { null, _debugDamage });
                return $"Killed {go.name}";
            }
            catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Kill failed: {ex.Message}"); return "Kill failed (see log)"; }
        }

        private static bool IsCreature(GameObject go)
        {
            try
            {
                if (_tVillager != null && go.GetComponent(_tVillager) != null) return true;
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
                _tVillager   = AccessTools.TypeByName("Villager");
                _tLandAnimal = AccessTools.TypeByName("LandAnimal");
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

                _resolveFailed = _mKill == null || _debugDamage == null || (_tVillager == null && _tLandAnimal == null);
                if (_resolveFailed) MelonLogger.Warning("[DivineHands] Kill: API unresolved — disabled");
                return !_resolveFailed;
            }
            catch (Exception ex) { _resolveFailed = true; MelonLogger.Warning($"[DivineHands] Kill resolve: {ex.Message}"); return false; }
        }
    }
}
