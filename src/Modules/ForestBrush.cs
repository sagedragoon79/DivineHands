using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using MelonLoader;

namespace DivineHands.Modules
{
    /// <summary>
    /// Forest brush — the tree-painting half of Pangu's forest brush (ApplyForestBrush, Pangu 3753).
    /// Plants the map's own trees across an adjustable footprint at a target COVERAGE, and (optionally)
    /// writes soil fertility over the same area. All public FF API:
    ///   • Tree species come from the map's <c>Terrain2.Data.TreePrototypes</c> (shared with the Spawner's
    ///     Tree family — <see cref="CursorSpawners.MapTreePrefabs"/>), so no GUIDs and always DLC-correct.
    ///   • Placement uses the scalar-variance <c>AddGrowingTree</c> overload [217795]
    ///     (<see cref="CursorSpawners.PlantTreeWithVariance"/>).
    ///   • Coverage → per-cell probability mirrors Pangu: existing trees in the footprint count toward the
    ///     target (queried via <c>SelectTreesInRect(CountAllTrees)</c>), so re-applying tops up to the
    ///     target instead of stacking.
    ///   • Fertility (optional) reuses <see cref="FertilityBrush.WriteFertilityRect"/>.
    /// Trees are real TreeResource instances and the fertility arrays are serialized, so both persist with
    /// the normal save. Footprint + preview shared with the other brushes (BrushPreview).
    /// </summary>
    public static class ForestBrush
    {
        // Tree-cell size (metres). One tree slot per cell at 100% coverage — Pangu uses ~8 m; keeps a
        // natural spacing so trees don't overlap into a solid wall.
        private const float TreeCellSpacing = 8f;

        private static readonly System.Random _rng = new System.Random();
        private static MethodInfo? _selectTrees;
        private static bool _selectResolved;

        public static void OnMapLoaded() { _selectResolved = false; _selectTrees = null; }
        public static void OnSceneExit() { _selectResolved = false; _selectTrees = null; }

        public static void OnUpdate()
        {
            if (!Config.ForestEnable.Value) return;

            if (DivineHands.Core.DivinePanel.ForestModeActive)
            {
                if (Input.GetKeyDown(KeyCode.RightArrow)) Bump(Config.ForestGridWidth, +1);
                if (Input.GetKeyDown(KeyCode.LeftArrow))  Bump(Config.ForestGridWidth, -1);
                if (Input.GetKeyDown(KeyCode.UpArrow))    Bump(Config.ForestGridHeight, +1);
                if (Input.GetKeyDown(KeyCode.DownArrow))  Bump(Config.ForestGridHeight, -1);
            }

            if (DivineHands.Core.DivinePanel.ForestModeActive
                && !DivineHands.Core.DivinePanel.BlocksGameInput
                && Hotkey.Pressed(Config.ForestApplyKey.Value))
            {
                try { ApplyForest(); }
                catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Forest brush failed: {ex.Message}"); }
            }
        }

        private static void Bump(MelonLoader.MelonPreferences_Entry<int> e, int d)
            => e.Value = Mathf.Clamp(e.Value + d, 1, 10);

        /// <summary>Footprint (half-extent = slider value in cells, directly). Shared with BrushPreview.</summary>
        public static bool TryGetFootprint(out int cx, out int cz, out int fhw, out int fhh, out bool circle, out float res)
        {
            cx = cz = 0; fhw = fhh = 1; circle = false; res = 0f;
            if (!TerrainElevation.TryGetGridContext(out _, out _, out res)) return false;
            if (!TerrainElevation.TryGetCursorWorld(out Vector3 world)) return false;
            cx = Mathf.FloorToInt(world.x / res);
            cz = Mathf.FloorToInt(world.z / res);
            fhw = Mathf.Clamp(Config.ForestGridWidth.Value, 1, 10);
            fhh = Mathf.Clamp(Config.ForestGridHeight.Value, 1, 10);
            circle = Config.ForestShape.Value == 1;
            return true;
        }

        private static void ApplyForest()
        {
            if (!TryGetFootprint(out int cx, out int cz, out int fhw, out int fhh, out bool circle, out float res)) return;

            var prefabs = CursorSpawners.MapTreePrefabs();
            bool haveTrees = prefabs != null && prefabs.Count > 0;

            // Footprint world rect (centre = cell corner cx*res, matches the preview + fertility mapping).
            float cwx = cx * res, cwz = cz * res, hwW = fhw * res, hhW = fhh * res;
            // Clamp the iterated rect to the map's world extents so an edge stamp doesn't feed out-of-range
            // coords into AddGrowingTree (its tree-grid index isn't bounds-checked and throws — silently
            // dropping every edge tree). Centre/half-extents stay unclamped so the circle test is unaffected.
            float worldMax = 0f;
            if (TerrainElevation.TryGetGridContext(out _, out int gsize, out _)) worldMax = (gsize - 1) * res;
            float x0 = cwx - hwW, z0 = cwz - hhW, x1 = cwx + hwW, z1 = cwz + hhW;
            if (worldMax > 0f)
            {
                x0 = Mathf.Clamp(x0, 0f, worldMax); z0 = Mathf.Clamp(z0, 0f, worldMax);
                x1 = Mathf.Clamp(x1, 0f, worldMax); z1 = Mathf.Clamp(z1, 0f, worldMax);
            }
            float w = x1 - x0, h = z1 - z0;

            int planted = 0;
            if (haveTrees && w > 0f && h > 0f)
                planted = PlantForest(prefabs!, cwx, cwz, hwW, hhW, x0, z0, w, h, circle);
            else if (!haveTrees && Config.DebugLog.Value)
                MelonLogger.Warning("[DivineHands] Forest brush: no plantable tree prototypes on this map");

            // Optional soil fertility over the same footprint (Pangu writes it too).
            if (Config.ForestSetFertility.Value)
                FertilityBrush.WriteFertilityRect(cx, cz, fhw, fhh, circle, res,
                    Config.ForestFertility.Value, Config.ForestFertilityMult.Value, conditionSoil: false);

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Forest @ ({cwx:0},{cwz:0}) — planted {planted} tree(s)" +
                                (Config.ForestSetFertility.Value ? $", fertility {Config.ForestFertility.Value:0}%." : "."));
        }

        // Coverage→probability tree distribution (Pangu ApplyForestBrush steps 3-6). Returns count planted.
        private static int PlantForest(List<GameObject> prefabs, float cwx, float cwz, float hwW, float hhW,
            float x0, float z0, float w, float h, bool circle)
        {
            float g = TreeCellSpacing;
            int nx = Mathf.Max(1, Mathf.CeilToInt(w / g));
            int nz = Mathf.Max(1, Mathf.CeilToInt(h / g));

            bool InShape(float wx, float wz)
            {
                if (!circle) return true;
                float a = (wx - cwx) / Mathf.Max(0.01f, hwW), b = (wz - cwz) / Mathf.Max(0.01f, hhW);
                return a * a + b * b <= 1f;
            }

            // Mark cells already holding a tree so coverage counts them (query-only, no side effects).
            var occupied = new HashSet<int>();
            var existing = SelectExistingTrees(new Rect(x0, z0, w, h));
            if (existing != null)
                foreach (var t in existing)
                {
                    float tx = t.x, tz = t.z;
                    if (tx < x0 || tx > x0 + w || tz < z0 || tz > z0 + h) continue;
                    if (!InShape(tx, tz)) continue;
                    int ix = Mathf.Clamp((int)((tx - x0) / g), 0, nx - 1);
                    int iz = Mathf.Clamp((int)((tz - z0) / g), 0, nz - 1);
                    occupied.Add(iz * nx + ix);
                }

            // Cell world bounds, CLAMPED to the footprint (Pangu clamps each cell's max edge with
            // Min(rect.max, cell+g) [~3816] — without this the last row/column of ceil-sized cells
            // extends up to a full 8 m past the rect and trees land outside the preview outline;
            // worst at small brushes where the sole cell can be double the footprint).
            void CellBounds(int ix, int iz, out float cx0, out float cz0, out float cx1, out float cz1)
            {
                cx0 = x0 + ix * g; cz0 = z0 + iz * g;
                cx1 = Mathf.Min(x0 + w, cx0 + g);
                cz1 = Mathf.Min(z0 + h, cz0 + g);
            }

            // Valid cells (centre of the CLAMPED cell inside the shape) + the empty subset available to plant.
            var empty = new List<int>();
            int validCount = 0;
            for (int iz = 0; iz < nz; iz++)
                for (int ix = 0; ix < nx; ix++)
                {
                    CellBounds(ix, iz, out float cx0, out float cz0, out float cx1, out float cz1);
                    if (cx1 - cx0 <= 0.05f || cz1 - cz0 <= 0.05f) continue; // degenerate sliver
                    if (!InShape((cx0 + cx1) * 0.5f, (cz0 + cz1) * 0.5f)) continue;
                    validCount++;
                    int id = iz * nx + ix;
                    if (!occupied.Contains(id)) empty.Add(id);
                }
            if (validCount == 0 || empty.Count == 0) return 0;

            float cov = Mathf.Clamp01(Config.ForestCoverage.Value * 0.01f);
            float targetTrees = cov * validCount;
            float needed = Mathf.Max(0f, targetTrees - (validCount - empty.Count));
            float p = Mathf.Clamp01(needed / empty.Count);
            if (p <= 0f) return 0;

            float variance = Mathf.Max(0f, Config.ForestVariance.Value);
            int planted = 0;
            foreach (int id in empty)
            {
                if (_rng.NextDouble() > p) continue;
                int ix = id % nx, iz = id / nx;
                CellBounds(ix, iz, out float cx0, out float cz0, out float cx1, out float cz1);
                for (int attempt = 0; attempt < 3; attempt++) // up to 3 tries to land inside the shape
                {
                    // Uniform inside the CLAMPED cell, so trees never leave the footprint.
                    float wx = cx0 + (float)_rng.NextDouble() * (cx1 - cx0);
                    float wz = cz0 + (float)_rng.NextDouble() * (cz1 - cz0);
                    if (!InShape(wx, wz)) continue;
                    var prefab = prefabs[_rng.Next(prefabs.Count)];
                    if (CursorSpawners.PlantTreeWithVariance(prefab, wx, wz, variance)) { planted++; break; }
                }
            }
            return planted;
        }

        // terrainManager.SelectTreesInRect(rect, Rect.zero, SelectTreesMode.CountAllTrees, null[, false]) [218601]
        // — returns existing trees as List<Vector4> (x,_,z,_). Query mode, no world change.
        private static List<Vector4>? SelectExistingTrees(Rect rect)
        {
            try
            {
                var gm = GameManager.Instance;
                object? tm = gm != null ? gm.terrainManager : null;
                if (tm == null) return null;
                if (!_selectResolved)
                {
                    _selectResolved = true;
                    foreach (var m in tm.GetType().GetMethods())
                    {
                        if (m.Name != "SelectTreesInRect") continue;
                        var ps = m.GetParameters();
                        if (ps.Length >= 4 && ps[0].ParameterType == typeof(Rect) && ps[2].ParameterType.IsEnum)
                        { _selectTrees = m; break; }
                    }
                }
                if (_selectTrees == null) return null;
                var pars = _selectTrees.GetParameters();
                object mode = Enum.ToObject(pars[2].ParameterType, 4); // CountAllTrees
                object?[] args = pars.Length == 5
                    ? new object?[] { rect, Rect.zero, mode, null, false }
                    : new object?[] { rect, Rect.zero, mode, null };
                return _selectTrees.Invoke(tm, args) as List<Vector4>;
            }
            catch { return null; }
        }
    }
}
