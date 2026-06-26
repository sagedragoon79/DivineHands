using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using MelonLoader;
using LibTerrain2; // Heightmap (Terrain2/Terrain2Manager/Terrain2Data touched only via reflection)

namespace DivineHands.Modules
{
    /// <summary>
    /// Terrain sculpting god-power: Raise / Lower / Smooth / Flatten over an NxN heightmap-cell
    /// grid centered on the cursor, with a strength slider and stroke-level undo.
    ///
    /// FF API path (all VERIFIED against Assembly-CSharp decompile — no invented names):
    ///   - GameManager.Instance.terrainManager : TerrainManagerBase  (concrete: Terrain2Manager)
    ///       · public bool GetTerrainWorldPointUnderCursor(out Vector3 point, bool includeBridges=false)  [218221]
    ///       · public void RebuildCollisionAtRectOverlap(Rect rectArea)                                   [218137]
    ///   - Terrain2Manager.terrain : Terrain2   — PRIVATE field, reached by reflection once             [217029]
    ///       · public Terrain2Data  Data => data;                                                         [490273]
    ///       · public void SmoothHeightsNotify(TerrainManagerBase, int,int,int,int, bool)  (refresh)      [491080]
    ///       · public void InvalidateSpace(int minX, int minZ, int maxX, int maxZ)        (fallback only)  [490604]
    ///       · public void RebuildCollisionAtRectOverlap(Rect rectArea)                   (fallback only)  [490627]
    ///   - Terrain2Data
    ///       · public Heightmap Heightmap => heightmap;                                                   [491824]
    ///       · public int   Size       (heightmap cells per side, square)                                 [491844]
    ///       · public float Resolution (world metres per heightmap cell)                                  [491860]
    ///   - Heightmap
    ///       · public float GetHeight(int x, int z)   (coords clamped to [0,size-1])                      [489562]
    ///       · public void  SetHeight(int x, int z, float height)  (coords clamped; height NOT clamped)  [489618]
    ///
    /// Why we do NOT call Terrain2Tools.SetElevation/SmoothElevation: those three brush methods are
    /// PRIVATE on Terrain2Tools [493333/493349/493306] and depend on other private helpers
    /// (ClampBrushBounds, SampleAverageHeight). Their bodies are trivial, so we MIRROR the exact logic
    /// here over the public Heightmap.Get/SetHeight — deterministic, no fragile private-method
    /// reflection. RoughenElevation is the only public one [493380] and we don't need it.
    ///
    /// Why we do NOT use TerrainManagerBase.SmoothTerrain() as the refresh: it runs a Laplacian
    /// smoothing FILTER over the rect on a coroutine [490914] — it mutates heights and is async, so it
    /// would distort our explicit edits. Instead we call the engine's OWN full post-edit notify,
    /// Terrain2.SmoothHeightsNotify [491080], which synchronously does InvalidateSpace (visual mesh) +
    /// RebuildCollisionAtRectOverlap (MeshColliders/NavMesh) + PathingUtilities.UpdatePathingPhysics
    /// (so villager AI re-paths onto the new terrain) + SelectTreesInRect(AdjustHeightsToTerrain) (trees
    /// re-sit on the new surface) + raises TerrainSmoothedEvent. Coords are index-space, so we pass our
    /// brush rect directly. A two-call InvalidateSpace + RebuildCollision path is kept ONLY as a fallback
    /// for the (not expected) case where SmoothHeightsNotify can't be reflected.
    ///
    /// All private-field access is wrapped in try/catch and gated on DebugLog for diagnostics.
    /// </summary>
    public static class TerrainElevation
    {
        public enum Mode { Raise, Lower, Smooth, Flatten, Average }

        // ---- cached reflection (resolved once per map) ----
        private static FieldInfo? _terrainField;     // Terrain2Manager.terrain  (private)
        private static object? _terrainManager;      // boxed TerrainManagerBase
        private static object? _terrain;             // boxed Terrain2
        private static MethodInfo? _smoothNotify;    // Terrain2.SmoothHeightsNotify(TerrainManagerBase,int,int,int,int,bool) — primary refresh
        private static MethodInfo? _invalidateSpace; // Terrain2.InvalidateSpace(int,int,int,int) — fallback
        private static MethodInfo? _rebuildCollision; // Terrain2.RebuildCollisionAtRectOverlap(Rect) — fallback
        private static PropertyInfo? _dataProp;      // Terrain2.Data
        private static bool _resolveFailed;

        // ---- live terrain handles (re-fetched lazily) ----
        private static Heightmap? _heightmap;
        private static int _size;        // heightmap cells per side
        private static float _resolution; // world metres per cell

        // ---- undo (stroke-level) ----
        private struct Snapshot
        {
            public int MinX, MinZ, MaxX, MaxZ; // inclusive heightmap-index rect
            public float[] Heights;            // row-major (MaxX-MinX+1) wide
        }
        private static readonly Stack<Snapshot> _undo = new Stack<Snapshot>();
        private const int MaxUndo = 64;

        // =====================================================================
        // Lifecycle (called from Plugin)
        // =====================================================================

        public static void OnMapLoaded()
        {
            _terrainManager = null;
            _terrain = null;
            _heightmap = null;
            _terrainField = null;
            _invalidateSpace = null;
            _rebuildCollision = null;
            _dataProp = null;
            _smoothNotify = null;
            _resolveFailed = false;
            _undo.Clear();
        }

        public static void OnSceneExit()
        {
            _terrainManager = null;
            _terrain = null;
            _heightmap = null;
            _undo.Clear();
        }

        public static void OnUpdate()
        {
            if (!Config.TerrainEnable.Value) return;

            // Only sculpt when the terrain tool is the active panel mode and the cursor isn't on the panel.
            if (DivineHands.Core.DivinePanel.TerrainModeActive
                && !DivineHands.Core.DivinePanel.BlocksGameInput)
            {
                if (Hotkey.Pressed(Config.TerrainApplyKey.Value))
                    ApplyStroke();
            }

            // Resize the grid while the terrain tool is armed (matches TerrainHelper). Independent
            // dimensions via the arrow keys — Left/Right = width (X), Up/Down = depth (Z) — and Tab
            // swaps them. Keyboard, so it works regardless of cursor position over the panel.
            if (DivineHands.Core.DivinePanel.TerrainModeActive)
            {
                if (Input.GetKeyDown(KeyCode.RightArrow)) BumpGrid(Config.TerrainGridWidth, +1);
                if (Input.GetKeyDown(KeyCode.LeftArrow))  BumpGrid(Config.TerrainGridWidth, -1);
                if (Input.GetKeyDown(KeyCode.UpArrow))    BumpGrid(Config.TerrainGridHeight, +1);
                if (Input.GetKeyDown(KeyCode.DownArrow))  BumpGrid(Config.TerrainGridHeight, -1);
                if (Input.GetKeyDown(KeyCode.Tab))
                {
                    int w = Mathf.Clamp(Config.TerrainGridWidth.Value, 1, 10);
                    int h = Mathf.Clamp(Config.TerrainGridHeight.Value, 1, 10);
                    Config.TerrainGridWidth.Value = h;
                    Config.TerrainGridHeight.Value = w;
                }
            }

            // Undo works whenever terrain editing is enabled (even when not actively painting),
            // but still suppressed while the cursor is over the panel to avoid stealing UI clicks.
            if (!DivineHands.Core.DivinePanel.BlocksGameInput
                && Hotkey.Pressed(Config.TerrainUndoKey.Value))
                Undo();
        }

        // =====================================================================
        // Apply
        // =====================================================================

        private static void ApplyStroke()
        {
            if (!ResolveTerrain()) return;
            var hm = _heightmap;
            if (hm == null) return;

            // 1) World point under cursor (FF raycast onto the Terrain layer).
            if (!TryGetCursorWorldPoint(out Vector3 world)) return;

            // 2) Cursor world XZ -> centre heightmap cell.  Index = floor(worldAxis / resolution)
            //    (matches Terrain2Tools.GetHeightBrushBounds [493285-286]).
            int cx = Mathf.FloorToInt(world.x / _resolution);
            int cz = Mathf.FloorToInt(world.z / _resolution);

            // 3) Width × depth grid centred on the cursor cell (independent dimensions — e.g. a 1×10
            //    footprint to carve a path through a ridge). Clamped; bail if entirely off-map.
            BrushRectFromCenter(cx, cz, out int minX, out int minZ, out int maxX, out int maxZ);
            if (minX > maxX || minZ > maxZ) return;

            // 4) Snapshot the affected rect BEFORE writing (for undo).
            PushSnapshot(hm, minX, minZ, maxX, maxZ);

            // 5) Resolve the per-mode target/amount.  For Raise/Lower/Flatten we work off the
            //    cursor's current world height (world.y), exactly like TerrainHelperMono.
            var mode = (Mode)Mathf.Clamp(Config.TerrainMode.Value, 0, 4);
            float strength = Mathf.Max(0f, Config.TerrainStrength.Value);

            // Shift inverts Raise <-> Lower (TerrainHelperMono convention).
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (mode == Mode.Raise && shift) mode = Mode.Lower;
            else if (mode == Mode.Lower && shift) mode = Mode.Raise;

            switch (mode)
            {
                case Mode.Flatten:
                    FillElevation(hm, minX, minZ, maxX, maxZ, world.y);
                    break;
                case Mode.Raise:
                    FillElevation(hm, minX, minZ, maxX, maxZ, world.y + strength);
                    break;
                case Mode.Lower:
                    FillElevation(hm, minX, minZ, maxX, maxZ, world.y - strength);
                    break;
                case Mode.Smooth:
                    SmoothElevation(hm, minX, minZ, maxX, maxZ, strength);
                    break;
                case Mode.Average:
                    AverageElevation(hm, minX, minZ, maxX, maxZ, strength);
                    break;
            }

            // 6) Refresh visual mesh + colliders over the edited rect.
            RefreshRect(minX, minZ, maxX, maxZ);

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Terrain {mode} @ cell ({cx},{cz}) " +
                                $"grid {Config.TerrainGridWidth.Value}x{Config.TerrainGridHeight.Value} " +
                                $"rect [{minX},{minZ}..{maxX},{maxZ}] strength {strength:0.##}");
        }

        // --- brush kernels: mirror Terrain2Tools, but flat (no radial falloff) for a crisp grid ---

        /// <summary>Sets every cell in the rect to <paramref name="value"/> (Flatten / plateau).
        /// Mirrors Terrain2Tools.SetElevation [493333] (which also writes a fixed value per cell).</summary>
        private static void FillElevation(Heightmap hm, int minX, int minZ, int maxX, int maxZ, float value)
        {
            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                    hm.SetHeight(x, z, value);
        }

        /// <summary>Blends each cell toward the average of its 3-radius neighbourhood, stepping by
        /// <paramref name="amount"/>.  Mirrors Terrain2Tools.SmoothElevation [493349] +
        /// SampleAverageHeight [493230] exactly.</summary>
        private static void SmoothElevation(Heightmap hm, int minX, int minZ, int maxX, int maxZ, float amount)
        {
            const int kernelRadius = 3;
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    float avg = SampleAverageHeight(hm, x, z, kernelRadius);
                    float h = hm.GetHeight(x, z);
                    if (Mathf.Abs(h - avg) < amount) h = avg;
                    else if (h > avg) h -= amount;
                    else if (h < avg) h += amount;
                    hm.SetHeight(x, z, h);
                }
            }
        }

        /// <summary>Steps every cell toward the SINGLE mean height of the whole brush rect (computed
        /// from the original heights before any writes), capped by <paramref name="amount"/> per stroke.
        /// Unlike <see cref="SmoothElevation"/> — local neighbour relaxation that preserves slopes —
        /// repeated Average strokes drive the entire patch toward one flat level (TerrainHelper-style
        /// "creep toward flat"). Target is the arithmetic mean, so the patch flattens around its average
        /// height; because the step is symmetric the mean is ~conserved, so it converges, it doesn't drift.</summary>
        private static void AverageElevation(Heightmap hm, int minX, int minZ, int maxX, int maxZ, float amount)
        {
            float sum = 0f;
            int count = 0;
            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++) { sum += hm.GetHeight(x, z); count++; }
            if (count == 0) return;
            float target = sum / count;

            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                {
                    float h = hm.GetHeight(x, z);
                    if (Mathf.Abs(h - target) < amount) h = target;       // close enough → snap to mean
                    else if (h > target) h -= amount;                     // else step toward it by `amount`
                    else h += amount;
                    hm.SetHeight(x, z, h);
                }
        }

        // Mirror of Terrain2Tools.SampleAverageHeight [493230]: (kernelRadius-1) box average, clamped.
        private static float SampleAverageHeight(Heightmap hm, int cx, int cz, int kernelRadius)
        {
            int r = kernelRadius - 1;
            int x0 = Mathf.Max(cx - r, 0);
            int z0 = Mathf.Max(cz - r, 0);
            int x1 = Mathf.Min(cx + r, _size - 1);
            int z1 = Mathf.Min(cz + r, _size - 1);
            float sum = 0f, count = 0f;
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    sum += hm.GetHeight(x, z);
                    count += 1f;
                }
            return count > 0f ? sum / count : 0f;
        }

        // =====================================================================
        // Undo
        // =====================================================================

        private static void PushSnapshot(Heightmap hm, int minX, int minZ, int maxX, int maxZ)
        {
            int w = maxX - minX + 1;
            int h = maxZ - minZ + 1;
            var buf = new float[w * h];
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                    buf[j + i * w] = hm.GetHeight(minX + j, minZ + i);

            if (_undo.Count >= MaxUndo)
            {
                // Drop the oldest by rebuilding (Stack has no bottom-pop; cheap at this cap).
                var keep = _undo.ToArray(); // newest-first
                _undo.Clear();
                for (int k = keep.Length - 2; k >= 0; k--) _undo.Push(keep[k]);
            }
            _undo.Push(new Snapshot { MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ, Heights = buf });
        }

        public static void Undo()
        {
            if (_undo.Count == 0)
            {
                if (Config.DebugLog.Value) MelonLogger.Msg("[DivineHands] Terrain undo: nothing to undo");
                return;
            }
            if (!ResolveTerrain()) return;
            var hm = _heightmap;
            if (hm == null) return;

            var s = _undo.Pop();
            int w = s.MaxX - s.MinX + 1;
            int h = s.MaxZ - s.MinZ + 1;
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                    hm.SetHeight(s.MinX + j, s.MinZ + i, s.Heights[j + i * w]);

            RefreshRect(s.MinX, s.MinZ, s.MaxX, s.MaxZ);

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Terrain undo rect [{s.MinX},{s.MinZ}..{s.MaxX},{s.MaxZ}] " +
                                $"({_undo.Count} left)");
        }

        public static int UndoDepth => _undo.Count;

        /// <summary>World metres per heightmap cell for the current map (Terrain2.Data.Resolution), or
        /// 0 if terrain hasn't resolved yet (not in game / not loaded). Read-only; the panel multiplies
        /// it by the grid cell count to show the brush footprint in metres.</summary>
        public static float CellMeters => _resolution;

        // ---- Shared read-only accessors for the cursor grid preview (TerrainBrushGrid) ----
        // Expose the SAME resolved terrain handles + cell math the brush uses, so the preview
        // outline lands exactly where ApplyStroke() writes.

        /// <summary>Resolve terrain (cached) and report the live heightmap grid metadata.
        /// Returns false until terrain is ready.</summary>
        public static bool TryGetGridContext(out Heightmap hm, out int size, out float resolution)
        {
            hm = null!;
            size = 0;
            resolution = 0f;
            if (!ResolveTerrain()) return false;
            if (_heightmap == null) return false;
            hm = _heightmap;
            size = _size;
            resolution = _resolution;
            return true;
        }

        /// <summary>World point under the cursor via FF's terrain raycast (same call ApplyStroke uses).</summary>
        public static bool TryGetCursorWorld(out Vector3 world) => TryGetCursorWorldPoint(out world);

        /// <summary>Terrain surface height (world-space Y, metres) at an arbitrary world XZ, bilinearly
        /// sampled from the heightmap so it varies smoothly between cells. Heightmap values ARE world Y
        /// (ApplyStroke writes <c>world.y</c> directly via SetHeight). Used by Free Cam's ground floor.
        /// Returns false until terrain resolves; off-map XZ clamps to the nearest edge cells.</summary>
        public static bool TryGetGroundHeight(float worldX, float worldZ, out float height)
        {
            height = 0f;
            if (!TryGetGridContext(out Heightmap hm, out int size, out float resolution)) return false;
            if (resolution <= 0f || size <= 0) return false;

            float fx = worldX / resolution;
            float fz = worldZ / resolution;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, size - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, size - 1);
            int x1 = Mathf.Min(x0 + 1, size - 1);
            int z1 = Mathf.Min(z0 + 1, size - 1);
            float tx = Mathf.Clamp01(fx - x0);
            float tz = Mathf.Clamp01(fz - z0);

            float h0 = Mathf.Lerp(hm.GetHeight(x0, z0), hm.GetHeight(x1, z0), tx);
            float h1 = Mathf.Lerp(hm.GetHeight(x0, z1), hm.GetHeight(x1, z1), tx);
            height = Mathf.Lerp(h0, h1, tz);
            return true;
        }

        /// <summary>The brush's index-space rect for the current cursor + grid size, clamped to the
        /// heightmap. Mirrors ApplyStroke() EXACTLY. Returns false if off-map or terrain not ready.</summary>
        public static bool TryGetBrushRect(out int minX, out int minZ, out int maxX, out int maxZ)
        {
            minX = minZ = maxX = maxZ = 0;
            if (!ResolveTerrain() || _heightmap == null) return false;
            if (!TryGetCursorWorldPoint(out Vector3 world)) return false;

            int cx = Mathf.FloorToInt(world.x / _resolution);
            int cz = Mathf.FloorToInt(world.z / _resolution);
            BrushRectFromCenter(cx, cz, out minX, out minZ, out maxX, out maxZ);
            return minX <= maxX && minZ <= maxZ;
        }

        // Width (X) × depth (Z) brush footprint in CELLS, centred on cell (cx, cz), clamped to the
        // heightmap. Returns the corner/VERTEX lattice that bounds those cells: w cells need w+1 corner
        // vertices, so maxX = origin + w (NOT w-1). FillElevation sets every vertex in [minX..maxX], which
        // flattens exactly the w×h cells between them — matching the previewed grid (TerrainBrushGrid draws
        // the same lattice). The earlier "+(w-1)" set one fewer vertex, leaving the last cell row/column
        // unflattened. Independent dims so the brush can be 1×10 (a trench) or 5×5 (a plateau).
        private static void BrushRectFromCenter(int cx, int cz,
            out int minX, out int minZ, out int maxX, out int maxZ)
        {
            int w = Mathf.Clamp(Config.TerrainGridWidth.Value, 1, 10);
            int h = Mathf.Clamp(Config.TerrainGridHeight.Value, 1, 10);
            int rawMinX = cx - w / 2;
            int rawMinZ = cz - h / 2;
            minX = Mathf.Max(rawMinX, 0);
            minZ = Mathf.Max(rawMinZ, 0);
            maxX = Mathf.Min(rawMinX + w, _size - 1);
            maxZ = Mathf.Min(rawMinZ + h, _size - 1);
        }

        private static void BumpGrid(MelonLoader.MelonPreferences_Entry<int> entry, int delta)
            => entry.Value = Mathf.Clamp(entry.Value + delta, 1, 10);

        // =====================================================================
        // Refresh — call the engine's own full post-edit notify, Terrain2.SmoothHeightsNotify [491080].
        // That single call does (synchronously, no coroutine/Laplacian): InvalidateSpace (visual mesh)
        // + RebuildCollisionAtRectOverlap (colliders/NavMesh) + UpdatePathingPhysics (AI re-paths onto
        // new terrain) + SelectTreesInRect(AdjustHeightsToTerrain) (trees re-anchor) + raises
        // TerrainSmoothedEvent. Coords are index-space (CR / cell-resolution), so we pass our rect as-is.
        // =====================================================================

        private static void RefreshRect(int minX, int minZ, int maxX, int maxZ)
        {
            try
            {
                if (_smoothNotify != null && _terrainManager != null)
                {
                    // SmoothHeightsNotify(terrainManager, xMinCR, yMinCR, xMaxCR, yMaxCR, rebuildCollision:true)
                    _smoothNotify.Invoke(_terrain, new object[]
                        { _terrainManager, minX, minZ, maxX, maxZ, true });
                    return;
                }

                // ---- Fallback (only if SmoothHeightsNotify couldn't be resolved) ----
                // A strict subset of the notify path: mesh + colliders, but NOT pathing/tree re-anchor.
                // (a) Visual mesh: Terrain2.InvalidateSpace(int,int,int,int)  [490604]
                _invalidateSpace?.Invoke(_terrain, new object[] { minX, minZ, maxX, maxZ });

                // (b) Colliders / NavMesh: RebuildCollisionAtRectOverlap takes a WORLD-space rect.
                //     World rect = index rect * resolution. Pad by 1 cell so edge collision cells rebuild.
                float res = _resolution;
                var worldRect = new Rect(
                    (minX - 1) * res,
                    (minZ - 1) * res,
                    (maxX - minX + 3) * res,
                    (maxZ - minZ + 3) * res);
                _rebuildCollision?.Invoke(_terrain, new object[] { worldRect });

                if (Config.DebugLog.Value)
                    MelonLogger.Warning("[DivineHands] Terrain refresh used FALLBACK path " +
                        "(SmoothHeightsNotify unresolved) — AI pathing / trees may not re-anchor.");
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] Terrain refresh failed: {ex.Message}");
            }
        }

        // Loose resolver for SmoothHeightsNotify in case the exact (TerrainManagerBase,int,int,int,int,bool)
        // param-type lookup misses (e.g. concrete manager type vs declared base). Matches by name + arity +
        // the trailing (int,int,int,int,bool) tail, leaving arg0 (the manager) as whatever the method declares.
        private static MethodInfo? FindSmoothNotify(Type terrain2Type)
        {
            try
            {
                foreach (var m in terrain2Type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "SmoothHeightsNotify") continue;
                    var p = m.GetParameters();
                    if (p.Length != 6) continue;
                    if (p[1].ParameterType == typeof(int) && p[2].ParameterType == typeof(int)
                        && p[3].ParameterType == typeof(int) && p[4].ParameterType == typeof(int)
                        && p[5].ParameterType == typeof(bool)
                        && p[0].ParameterType.IsInstanceOfType(_terrainManager))
                        return m;
                }
            }
            catch { /* fall through to fallback refresh */ }
            return null;
        }

        // =====================================================================
        // Terrain resolution (cursor raycast + reflected handles)
        // =====================================================================

        private static bool TryGetCursorWorldPoint(out Vector3 point)
        {
            point = Vector3.zero;
            // Resolve the terrain manager on demand. The cursor raycast only needs the manager (not the
            // heightmap), and callers like the Spawner never run ResolveTerrain() first — so on a fresh
            // map this returned false ("no terrain under cursor") until a terrain SCULPT happened to
            // populate _terrainManager. Resolve it here directly (the cheap part, no _resolveFailed latch).
            if (_terrainManager == null)
            {
                var gm = GameManager.Instance;
                _terrainManager = gm != null ? gm.terrainManager : null;
            }
            if (_terrainManager == null) return false;
            try
            {
                // public bool GetTerrainWorldPointUnderCursor(out Vector3 point, bool includeBridges=false) [218221]
                var args = new object?[] { null, false };
                var mi = _terrainManager.GetType().GetMethod("GetTerrainWorldPointUnderCursor",
                    new[] { typeof(Vector3).MakeByRefType(), typeof(bool) });
                if (mi == null) return false;
                bool hit = (bool)mi.Invoke(_terrainManager, args)!;
                if (hit && args[0] is Vector3 p) { point = p; return true; }
                return false;
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] cursor raycast failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Resolve the terrain manager, reflect the private Terrain2, and refresh the live
        /// Heightmap/size/resolution. Cheap on the hot path once cached; re-fetches Heightmap each
        /// call (it can be reallocated on map regen).</summary>
        private static bool ResolveTerrain()
        {
            if (_resolveFailed) return false;
            try
            {
                if (_terrainManager == null)
                {
                    var gm = GameManager.Instance;
                    if (gm == null) return false;
                    _terrainManager = gm.terrainManager;
                    if (_terrainManager == null) return false;
                }

                if (_terrain == null)
                {
                    var tmType = _terrainManager.GetType();
                    _terrainField ??= tmType.GetField("terrain",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    _terrain = _terrainField?.GetValue(_terrainManager);
                    if (_terrain == null)
                    {
                        if (Config.DebugLog.Value)
                            MelonLogger.Warning("[DivineHands] could not reflect Terrain2Manager.terrain");
                        _resolveFailed = true;
                        return false;
                    }

                    var t2Type = _terrain.GetType();
                    // Primary refresh — the engine's own post-edit notify. Index-space coords; does
                    // InvalidateSpace + RebuildCollision + UpdatePathingPhysics + tree re-anchor +
                    // TerrainSmoothedEvent, all synchronous/deterministic (NOT the Laplacian SmoothTerrain).
                    // public void SmoothHeightsNotify(TerrainManagerBase, int,int,int,int, bool)  [491080]
                    _smoothNotify = t2Type.GetMethod("SmoothHeightsNotify",
                        new[] { _terrainManager.GetType().BaseType ?? _terrainManager.GetType(),
                                typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool) })
                        // Fall back to a loose name match if the exact param-type lookup misses
                        // (terrainManager's compile-time base may not be TerrainManagerBase directly).
                        ?? FindSmoothNotify(t2Type);
                    // Fallback refresh path (used only if SmoothHeightsNotify can't be resolved).
                    _invalidateSpace = t2Type.GetMethod("InvalidateSpace",
                        new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
                    _rebuildCollision = t2Type.GetMethod("RebuildCollisionAtRectOverlap",
                        new[] { typeof(Rect) });
                    _dataProp = t2Type.GetProperty("Data");
                }

                // Refresh live Heightmap + grid metadata from Terrain2.Data (public getters).
                var data = _dataProp?.GetValue(_terrain);
                if (data == null) return false;
                var dataType = data.GetType();
                _heightmap = dataType.GetProperty("Heightmap")?.GetValue(data) as Heightmap;
                _size = (int)(dataType.GetProperty("Size")?.GetValue(data) ?? 0);
                _resolution = (float)(dataType.GetProperty("Resolution")?.GetValue(data) ?? 0f);

                if (_heightmap == null || _size <= 0 || _resolution <= 0f)
                {
                    if (Config.DebugLog.Value)
                        MelonLogger.Warning("[DivineHands] terrain data not ready " +
                                            $"(size={_size}, res={_resolution})");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] ResolveTerrain failed: {ex.Message}");
                return false;
            }
        }
    }
}
