using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using MelonLoader;

namespace DivineHands.Modules
{
    /// <summary>One stamped lake, enough to replay it on load.</summary>
    internal struct LakeRecord
    {
        public int cx, cz, fhw, fhh, shore;
        public bool circle, fish;
        public float depth;
    }

    /// <summary>
    /// Per-save sidecar for stamped lakes. FF bakes water areas into the <c>.map</c> (written once at
    /// map-gen) — a normal in-game save only writes the <c>.sav</c>, so stamped lakes' water vanishes on
    /// reload (the carved basin survives, since heightmap edits ARE in the .sav). Rather than force a full
    /// <c>SaveManager.SaveMap()</c> (heavy, re-bakes the whole map, risky around other map mods), we drop a
    /// small companion file next to each save — <c>&lt;save&gt;.divinehands-lakes.csv</c> — and replay the
    /// water surfaces on load (<see cref="LakeStamp"/>). Written on every game save, deleted when the save
    /// is deleted (Harmony hooks in <see cref="Patches.SaveSidecarPatches"/>).
    ///
    /// Path mirrors the .sav exactly: <c>&lt;persistentPath&gt;/Save/&lt;folder&gt;/&lt;name&gt;.divinehands-lakes.csv</c>,
    /// keyed by <c>SaveManager.activeSaveFileName</c> ("folder/name.sav"). One CSV line per lake.
    /// </summary>
    internal static class LakeSidecar
    {
        private const string Ext = ".divinehands-lakes.csv";

        private static MethodInfo? _getPersistentPath;
        private static bool _pathResolved;

        /// <summary>The active save's name without extension ("Settlement_date/SaveName"), or null if none.</summary>
        internal static string? ActiveSaveKey()
        {
            try
            {
                var n = SaveManager.activeSaveFileName;
                if (string.IsNullOrEmpty(n)) return null;
                if (n.EndsWith(SaveManager.fileExtension, StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - SaveManager.fileExtension.Length);
                return n;
            }
            catch { return null; }
        }

        private static string? PathFor(string saveKey)
        {
            try
            {
                if (!_pathResolved)
                {
                    _pathResolved = true;
                    var t = AccessTools.TypeByName("ES2FilenameData");
                    _getPersistentPath = t?.GetMethod("GetPersistentPath", BindingFlags.Public | BindingFlags.Static);
                }
                string root = _getPersistentPath?.Invoke(null, null) as string ?? Application.persistentDataPath;
                // SaveManager.folderName is "Save/"; saveKey already carries the per-settlement subfolder.
                return root + "/" + SaveManager.folderName + saveKey + Ext;
            }
            catch { return null; }
        }

        internal static void Write(string? saveKey, List<LakeRecord> lakes)
        {
            if (string.IsNullOrEmpty(saveKey)) return;
            var path = PathFor(saveKey!);
            if (path == null) return;
            try
            {
                if (lakes == null || lakes.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path); // no lakes → no stale sidecar
                    return;
                }
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# DivineHands stamped lakes: cx,cz,fhw,fhh,circle,shore,depth,fish");
                var ci = CultureInfo.InvariantCulture;
                foreach (var l in lakes)
                    sb.AppendLine($"{l.cx},{l.cz},{l.fhw},{l.fhh},{(l.circle ? 1 : 0)},{l.shore},{l.depth.ToString("0.###", ci)},{(l.fish ? 1 : 0)}");
                File.WriteAllText(path, sb.ToString());
                if (Config.DebugLog.Value) MelonLogger.Msg($"[DivineHands] Lake sidecar written: {lakes.Count} lake(s) -> {path}");
            }
            catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Lake sidecar write failed: {ex.Message}"); }
        }

        internal static List<LakeRecord> Read(string? saveKey)
        {
            var result = new List<LakeRecord>();
            if (string.IsNullOrEmpty(saveKey)) return result;
            var path = PathFor(saveKey!);
            if (path == null || !File.Exists(path)) return result;
            try
            {
                var ci = CultureInfo.InvariantCulture;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var p = line.Split(',');
                    if (p.Length < 8) continue;
                    result.Add(new LakeRecord
                    {
                        cx    = int.Parse(p[0], ci),
                        cz    = int.Parse(p[1], ci),
                        fhw   = int.Parse(p[2], ci),
                        fhh   = int.Parse(p[3], ci),
                        circle = p[4] == "1",
                        shore = int.Parse(p[5], ci),
                        depth = float.Parse(p[6], ci),
                        fish  = p[7] == "1",
                    });
                }
                if (Config.DebugLog.Value) MelonLogger.Msg($"[DivineHands] Lake sidecar read: {result.Count} lake(s) from {path}");
            }
            catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Lake sidecar read failed: {ex.Message}"); }
            return result;
        }

        internal static void Delete(string? saveKey)
        {
            if (string.IsNullOrEmpty(saveKey)) return;
            var path = PathFor(saveKey!);
            try { if (path != null && File.Exists(path)) { File.Delete(path); if (Config.DebugLog.Value) MelonLogger.Msg($"[DivineHands] Lake sidecar deleted: {path}"); } }
            catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Lake sidecar delete failed: {ex.Message}"); }
        }
    }
}
