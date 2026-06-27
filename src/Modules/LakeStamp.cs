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
    /// Lake / Pond stamp — carves a water-filled basin at the cursor, mirroring Pangu's proven recipe
    /// but built onto Divine Hands' own primitives. Three parts:
    ///   1. CARVE a 3-zone basin (deep core → shallow shelf → banked shore) below the global water plane,
    ///      via the shared Heightmap (TerrainElevation.TryGetGridContext) + TerrainElevation.RefreshTerrainRect.
    ///   2. BUILD a TerrainGenerator+WaterArea (footprint mask + traced edges) and append it to
    ///      generator.Data.waterAreas — cloned from the user's RiversRestored RiverWaterAreaBuilder
    ///      (the proven, save-surviving construction; WaterType borrowed from waterSettings.lakeTypes).
    ///   3. RENDER the surface THIS session by invoking WaterPlane.BuildWaterShared(terrain, area, id) +
    ///      WaterChunk.Rebuild(...) — Pangu's ApplyWaterPlaneMutationIncremental path (option B: no reload).
    ///
    /// We APPEND (never merge), so the new area's id is simply waterAreas.Count-1 — no remap bookkeeping.
    /// All FF access is reflective + try/catch (water is the most version-fragile FF subsystem).
    /// Persistence (force a map re-save so it survives reload) is the next step; v1 makes the lake appear.
    /// </summary>
    public static class LakeStamp
    {
        // ---- reflection cache (resolved once per map; reset in OnMapLoaded) ----
        private static bool _resolved;
        private static bool _resolveFailed;
        private static bool _resolveOk;   // all members bound (cached so later stamps honor a partial-resolve)

        private static Type? _genType;        // TerrainGen.TerrainGenerator
        private static object? _generator;    // live TerrainGenerator (re-found if null)
        private static MethodInfo? _getWaterHeight;
        private static MemberInfo? _dataMember;       // TerrainGenerator.Data (prop or field)
        private static FieldInfo? _waterSettingsField;
        private static FieldInfo? _lakeTypesField;

        // WaterArea / WaterEdge / WaterType / Pair<int,int> (clone of RR RiverWaterAreaBuilder.ResolveTypes)
        private static Type? _waterAreaType, _waterEdgeType, _waterTypeType, _pairType;
        private static FieldInfo? _faWaterType, _faPoints, _faEdge, _faShore, _faMinX, _faMinZ, _faMaxX, _faMaxZ, _faWaterAreaId;
        private static FieldInfo? _feX, _feZ, _feNx, _feNz;
        private static ConstructorInfo? _pairCtor;

        // Live water-surface build (Pangu path)
        private static Type? _terrain2Type, _waterPlaneType, _waterChunkType;
        private static MethodInfo? _buildWaterShared, _chunkRebuild;
        private static UnityEngine.Object? _cachedWaterType;

        // =====================================================================
        // Lifecycle (called from Plugin)
        // =====================================================================
        public static void OnMapLoaded()
        {
            _resolved = false;
            _resolveFailed = false;
            _resolveOk = false;
            _generator = null;
            _cachedWaterType = null;
        }

        public static void OnSceneExit() => OnMapLoaded();

        public static void OnUpdate()
        {
            if (!Config.LakeEnable.Value) return;

            // Resize the footprint with the arrow keys while the lake tool is armed (matches the terrain brush).
            if (DivineHands.Core.DivinePanel.LakeModeActive)
            {
                if (Input.GetKeyDown(KeyCode.RightArrow)) Bump(Config.LakeGridWidth, +1);
                if (Input.GetKeyDown(KeyCode.LeftArrow))  Bump(Config.LakeGridWidth, -1);
                if (Input.GetKeyDown(KeyCode.UpArrow))    Bump(Config.LakeGridHeight, +1);
                if (Input.GetKeyDown(KeyCode.DownArrow))  Bump(Config.LakeGridHeight, -1);
            }

            if (DivineHands.Core.DivinePanel.LakeModeActive
                && !DivineHands.Core.DivinePanel.BlocksGameInput
                && Hotkey.Pressed(Config.LakeApplyKey.Value))
            {
                try { ApplyLake(); }
                catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Lake stamp failed: {ex.Message}"); }
            }
        }

        private static void Bump(MelonLoader.MelonPreferences_Entry<int> e, int d)
            => e.Value = Mathf.Clamp(e.Value + d, 1, 10);

        // =====================================================================
        // Footprint geometry — single source of truth for the carve AND the cursor preview.
        // =====================================================================

        /// <summary>Cursor-driven water footprint: centre cell + half-extents (cells) + shape. The cursor
        /// preview (<see cref="DivineHands.Core.LakeBrushPreview"/>) and the carve both call this so the
        /// outline always matches where water lands. Returns false if terrain/cursor aren't ready.</summary>
        public static bool TryGetFootprint(out int cx, out int cz, out int fhw, out int fhh, out bool circle, out float res)
        {
            cx = cz = 0; fhw = fhh = 1; circle = false; res = 0f;
            if (!TerrainElevation.TryGetGridContext(out _, out _, out res)) return false;
            if (!TerrainElevation.TryGetCursorWorld(out Vector3 world)) return false;
            FootprintFromWorld(world, res, out cx, out cz, out fhw, out fhh, out circle);
            return true;
        }

        private static void FootprintFromWorld(Vector3 world, float res,
            out int cx, out int cz, out int fhw, out int fhh, out bool circle)
        {
            cx = Mathf.FloorToInt(world.x / res);
            cz = Mathf.FloorToInt(world.z / res);
            float fill = Mathf.Clamp(Config.LakeFillRatio.Value, 1f, 2f);
            float noGo = Mathf.Clamp(Config.LakeNoGoWidth.Value, 0f, 24f);
            fhw = Mathf.Max(1, Mathf.RoundToInt(Config.LakeGridWidth.Value * fill + noGo));
            fhh = Mathf.Max(1, Mathf.RoundToInt(Config.LakeGridHeight.Value * fill + noGo));
            circle = Config.LakeShape.Value == 1;
        }

        // =====================================================================
        // Apply
        // =====================================================================
        private static void ApplyLake()
        {
            if (!TerrainElevation.TryGetGridContext(out var hm, out int size, out float res)) return;
            if (!TerrainElevation.TryGetCursorWorld(out Vector3 world)) return;
            if (!Resolve()) { if (Config.DebugLog.Value) MelonLogger.Msg("[DivineHands] Lake: FF water API not resolved"); return; }

            float waterH = GetWaterHeight();
            if (waterH <= 0f) { if (Config.DebugLog.Value) MelonLogger.Msg("[DivineHands] Lake: water height unavailable"); return; }

            // Footprint geometry shared with the cursor preview so the outline lands where water lands.
            FootprintFromWorld(world, res, out int cx, out int cz, out int fhw, out int fhh, out bool circle);
            int shore = Mathf.Clamp(Mathf.RoundToInt(Config.LakeShoreWidth.Value), 1, 40);
            float depth = Mathf.Clamp(Config.LakeCarveDepth.Value, 0.45f, 12f);

            // --- footprint bbox (the WaterArea cells), clamped to the map ---
            int fMinX = Mathf.Clamp(cx - fhw, 0, size - 1);
            int fMinZ = Mathf.Clamp(cz - fhh, 0, size - 1);
            int fMaxX = Mathf.Clamp(cx + fhw, 0, size - 1);
            int fMaxZ = Mathf.Clamp(cz + fhh, 0, size - 1);
            int fw = fMaxX - fMinX + 1, fh = fMaxZ - fMinZ + 1;
            if (fw < 2 || fh < 2) return;

            var mask = new bool[fw, fh];
            int filled = 0;
            for (int x = fMinX; x <= fMaxX; x++)
                for (int z = fMinZ; z <= fMaxZ; z++)
                    if (InFootprint(x - cx, z - cz, fhw, fhh, circle))
                    {
                        mask[x - fMinX, z - fMinZ] = true;
                        filled++;
                    }
            if (filled < 24) { if (Config.DebugLog.Value) MelonLogger.Msg($"[DivineHands] Lake too small ({filled} cells); enlarge the footprint."); return; }

            float floorH = waterH - depth;        // deepest (core)
            float edgeH  = waterH - 0.10f;         // just below water at the footprint edge

            // --- carve: footprint cells -> basin; a shore ring beyond -> banks rising back to land ---
            int cMinX = Mathf.Clamp(cx - fhw - shore, 0, size - 1);
            int cMinZ = Mathf.Clamp(cz - fhh - shore, 0, size - 1);
            int cMaxX = Mathf.Clamp(cx + fhw + shore, 0, size - 1);
            int cMaxZ = Mathf.Clamp(cz + fhh + shore, 0, size - 1);

            for (int x = cMinX; x <= cMaxX; x++)
            {
                for (int z = cMinZ; z <= cMaxZ; z++)
                {
                    int dx = x - cx, dz = z - cz;
                    float orig = hm.GetHeight(x, z);
                    float target;

                    if (InFootprint(dx, dz, fhw, fhh, circle))
                    {
                        // radial fraction 0 (centre) .. 1 (edge); deep centre, shallow edge
                        float rf = circle
                            ? Mathf.Sqrt((dx * (float)dx) / (fhw * (float)fhw) + (dz * (float)dz) / (fhh * (float)fhh))
                            : Mathf.Max(Mathf.Abs(dx) / (float)fhw, Mathf.Abs(dz) / (float)fhh);
                        target = Mathf.Lerp(floorH, edgeH, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(rf)));
                    }
                    else
                    {
                        // shore ring: how far beyond the footprint edge (in cells), ramp water->land.
                        // Per-axis overshoot so the band is a CONSTANT cell width on every axis — the old
                        // radial-fraction form scaled by min(fhw,fhh) left elongated circles never reaching
                        // land on the long axis (one-sided gouge + cliff). Circle = rounded (Euclidean),
                        // Rectangle = square (Chebyshev).
                        float ox = Mathf.Max(0f, Mathf.Abs(dx) - fhw);
                        float oz = Mathf.Max(0f, Mathf.Abs(dz) - fhh);
                        float outDist = circle ? Mathf.Sqrt(ox * ox + oz * oz) : Mathf.Max(ox, oz);
                        if (outDist > shore) continue;
                        float t = Mathf.Clamp01(outDist / shore);
                        target = Mathf.Lerp(waterH, orig, Mathf.SmoothStep(0f, 1f, t));
                    }

                    if (orig <= target + 0.02f) continue; // LOWER-only
                    hm.SetHeight(x, z, target);
                }
            }

            // Mesh/collider/NavMesh/tree rebuild over the carve rect (twice, matching Pangu).
            TerrainElevation.RefreshTerrainRect(cMinX, cMinZ, cMaxX, cMaxZ);
            TerrainElevation.RefreshTerrainRect(cMinX, cMinZ, cMaxX, cMaxZ);

            // --- build + register the WaterArea, then render its surface this session ---
            object? area = BuildWaterArea(mask, fMinX, fMinZ, fMaxX, fMaxZ);
            if (area == null) { MelonLogger.Warning("[DivineHands] Lake: WaterArea build failed (basin carved, no water)."); return; }

            int areaId = AppendWaterArea(area);
            if (areaId < 0) { MelonLogger.Warning("[DivineHands] Lake: could not append to waterAreas (basin carved, no water)."); return; }

            bool surfaced = BuildWaterSurface(area, areaId);
            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Lake stamped @ {world} — {filled} cells, depth {depth:0.#}m, " +
                                $"areaId {areaId}, surface {(surfaced ? "live" : "after-reload")}.");
        }

        private static bool InFootprint(int dx, int dz, int hw, int hh, bool circle)
        {
            if (circle)
                return (dx * (float)dx) / (hw * (float)hw) + (dz * (float)dz) / (hh * (float)hh) <= 1f;
            return Mathf.Abs(dx) <= hw && Mathf.Abs(dz) <= hh;
        }

        // =====================================================================
        // WaterArea construction (clone of RiversRestored RiverWaterAreaBuilder.CreateWaterArea)
        // =====================================================================
        private static object? BuildWaterArea(bool[,] mask, int minX, int minZ, int maxX, int maxZ)
        {
            try
            {
                var waterType = ResolveLakeWaterType();
                if (waterType == null) { MelonLogger.Warning("[DivineHands] Lake: no WaterType in waterSettings.lakeTypes"); return null; }

                var edges = new List<object>();
                var shores = new List<object>();
                for (int m = minZ; m <= maxZ; m++)
                {
                    for (int n = minX; n <= maxX; n++)
                    {
                        int gx = n - minX, gz = m - minZ;
                        if (!mask[gx, gz]) continue;
                        bool w = MaskFilled(mask, minX, minZ, maxX, maxZ, n - 1, m);
                        bool e = MaskFilled(mask, minX, minZ, maxX, maxZ, n + 1, m);
                        bool s = MaskFilled(mask, minX, minZ, maxX, maxZ, n, m - 1);
                        bool nn = MaskFilled(mask, minX, minZ, maxX, maxZ, n, m + 1);
                        if (w && e && s && nn) continue; // interior, not an edge

                        int nx = 0, nz = 0;
                        if (!w) nx--;
                        if (!e) nx++;
                        if (!s) nz--;
                        if (!nn) nz++;
                        if (nx == 0 && nz == 0) nz = 1;

                        var edge = Activator.CreateInstance(_waterEdgeType!)!;
                        _feX!.SetValue(edge, n);
                        _feZ!.SetValue(edge, m);
                        _feNx!.SetValue(edge, nx);
                        _feNz!.SetValue(edge, nz);
                        edges.Add(edge);
                        shores.Add(_pairCtor!.Invoke(new object[] { n, m }));
                    }
                }
                if (edges.Count < 8) { MelonLogger.Warning("[DivineHands] Lake: edge trace too small"); return null; }

                var edgeArr = Array.CreateInstance(_waterEdgeType!, edges.Count);
                for (int i = 0; i < edges.Count; i++) edgeArr.SetValue(edges[i], i);
                var shoreArr = Array.CreateInstance(_pairType!, shores.Count);
                for (int i = 0; i < shores.Count; i++) shoreArr.SetValue(shores[i], i);

                object area = Activator.CreateInstance(_waterAreaType!)!;
                _faWaterType!.SetValue(area, waterType);
                _faPoints!.SetValue(area, mask);
                _faEdge!.SetValue(area, edgeArr);
                _faShore!.SetValue(area, shoreArr);
                _faMinX!.SetValue(area, minX);
                _faMinZ!.SetValue(area, minZ);
                _faMaxX!.SetValue(area, maxX);
                _faMaxZ!.SetValue(area, maxZ);
                return area;
            }
            catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Lake BuildWaterArea: {ex.Message}"); return null; }
        }

        private static bool MaskFilled(bool[,] mask, int minX, int minZ, int maxX, int maxZ, int x, int z)
        {
            if (x < minX || x > maxX || z < minZ || z > maxZ) return false;
            return mask[x - minX, z - minZ];
        }

        // Append the boxed WaterArea to generator.Data.waterAreas; return its index (id), or -1.
        private static int AppendWaterArea(object area)
        {
            try
            {
                var list = GetWaterAreas();
                if (list == null) return -1;
                int id = list.Count;
                // Set the persistent waterAreaId if the struct exposes one (FishArea load-matching).
                _faWaterAreaId?.SetValue(area, id);
                list.Add(area);
                return id;
            }
            catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Lake AppendWaterArea: {ex.Message}"); return -1; }
        }

        // Render the surface this session: WaterPlane.BuildWaterShared(terrain, area, id) + WaterChunk.Rebuild(...).
        private static bool BuildWaterSurface(object area, int areaId)
        {
            try
            {
                var terrain2 = TerrainElevation.ResolvedTerrain2;
                // Rebuild is MANDATORY: BuildWaterShared only creates the GameObject+WaterChunk component;
                // the mesh comes solely from WaterChunk.Rebuild (FF + Pangu both call it unconditionally).
                // Treat a null handle as a hard failure so the caller honestly logs "after-reload", not "live".
                if (terrain2 == null || _buildWaterShared == null || _chunkRebuild == null) return false;

                var seaLayer = ((Component)terrain2).transform.Find("Sea Layer");
                if (seaLayer == null) return false;
                seaLayer.localPosition = new Vector3(0f, GetWaterHeight(), 0f);
                var waterPlane = seaLayer.GetComponent(_waterPlaneType!);
                if (waterPlane == null) return false;

                var chunk = _buildWaterShared.Invoke(waterPlane, new object[] { terrain2, area, areaId });
                if (chunk == null) return false;

                var pts = _faPoints!.GetValue(area);
                var shore = _faShore!.GetValue(area);
                var edge = _faEdge!.GetValue(area);
                _chunkRebuild.Invoke(chunk, new object[]
                {
                    terrain2,
                    _faMinX!.GetValue(area), _faMinZ!.GetValue(area),
                    _faMaxX!.GetValue(area), _faMaxZ!.GetValue(area),
                    pts, shore, edge
                });
                return true;
            }
            catch (Exception ex) { if (Config.DebugLog.Value) MelonLogger.Warning($"[DivineHands] Lake BuildWaterSurface: {ex.Message}"); return false; }
        }

        // =====================================================================
        // Reflection resolve
        // =====================================================================
        private static bool Resolve()
        {
            if (_resolved) return _resolveOk && (_generator != null || ReacquireGenerator());
            if (_resolveFailed) return false;
            try
            {
                _genType = AccessTools.TypeByName("TerrainGen.TerrainGenerator");
                if (_genType == null) { _resolveFailed = true; return false; }

                _getWaterHeight = _genType.GetMethod("GetWaterHeight", BindingFlags.Public | BindingFlags.Instance);
                _dataMember = (MemberInfo?)_genType.GetProperty("Data", BindingFlags.Public | BindingFlags.Instance)
                              ?? _genType.GetField("Data", BindingFlags.Public | BindingFlags.Instance)
                              ?? (MemberInfo?)_genType.GetField("_generationData", BindingFlags.NonPublic | BindingFlags.Instance);
                _waterSettingsField = _genType.GetField("waterSettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                // WaterArea / WaterEdge / WaterType / Pair<int,int> (RR ResolveTypes clone)
                _waterAreaType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterArea");
                _waterEdgeType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterEdge");
                _waterTypeType = AccessTools.TypeByName("TerrainGen.WaterType");
                var pairOpen = AccessTools.TypeByName("Pair`2");
                if (pairOpen != null) _pairType = pairOpen.MakeGenericType(typeof(int), typeof(int));
                if (_waterAreaType == null || _waterEdgeType == null || _waterTypeType == null || _pairType == null)
                { _resolveFailed = true; MelonLogger.Warning("[DivineHands] Lake: water types unresolved"); return false; }

                _faWaterType = _waterAreaType.GetField("waterType");
                _faPoints = _waterAreaType.GetField("points");
                _faEdge = _waterAreaType.GetField("edge");
                _faShore = _waterAreaType.GetField("shore");
                _faMinX = _waterAreaType.GetField("minX");
                _faMinZ = _waterAreaType.GetField("minZ");
                _faMaxX = _waterAreaType.GetField("maxX");
                _faMaxZ = _waterAreaType.GetField("maxZ");
                _faWaterAreaId = _waterAreaType.GetField("waterAreaId")
                                 ?? _waterAreaType.GetField("<waterAreaId>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                _feX = _waterEdgeType.GetField("x");
                _feZ = _waterEdgeType.GetField("z");
                _feNx = _waterEdgeType.GetField("nx");
                _feNz = _waterEdgeType.GetField("nz");
                _pairCtor = _pairType.GetConstructor(new[] { typeof(int), typeof(int) });

                // Live surface build (Pangu path). Resolve methods by name + arg-count so a namespaced /
                // overloaded Terrain2 can't silently null these out (which would drop us to after-reload).
                _terrain2Type = AccessTools.TypeByName("LibTerrain2.Terrain2")
                                ?? AccessTools.TypeByName("Terrain2")
                                ?? TerrainElevation.ResolvedTerrain2?.GetType();
                _waterPlaneType = AccessTools.TypeByName("WaterPlane");
                _waterChunkType = AccessTools.TypeByName("WaterChunk");
                _buildWaterShared = FindMethod(_waterPlaneType, "BuildWaterShared", 3); // (Terrain2, WaterArea, int)
                _chunkRebuild     = FindMethod(_waterChunkType, "Rebuild", 8);          // (Terrain2,4×int,bool[,],Pair[],WaterEdge[])

                bool ok = _getWaterHeight != null && _dataMember != null && _waterSettingsField != null
                          && _faWaterType != null && _faPoints != null && _faEdge != null && _faShore != null
                          && _faMinX != null && _faMinZ != null && _faMaxX != null && _faMaxZ != null
                          && _feX != null && _feZ != null && _feNx != null && _feNz != null && _pairCtor != null;
                _resolved = true;
                _resolveOk = ok;
                if (!ok) MelonLogger.Warning("[DivineHands] Lake: some reflection members missing — stamp may no-op");
                return ReacquireGenerator() && ok;
            }
            catch (Exception ex) { _resolveFailed = true; MelonLogger.Warning($"[DivineHands] Lake Resolve: {ex.Message}"); return false; }
        }

        private static bool ReacquireGenerator()
        {
            if (_generator != null) return true;
            if (_genType == null) return false;
            _generator = UnityEngine.Object.FindObjectOfType(_genType);
            return _generator != null;
        }

        // First instance method on <paramref name="t"/> matching name + parameter count (robust to type-name
        // drift in the param list, which an exact GetMethod overload-resolution would choke on).
        private static MethodInfo? FindMethod(Type? t, string name, int argc)
        {
            if (t == null) return null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (m.Name == name && m.GetParameters().Length == argc) return m;
            return null;
        }

        private static float GetWaterHeight()
        {
            try { return _generator != null && _getWaterHeight != null ? (float)_getWaterHeight.Invoke(_generator, null)! : 0f; }
            catch { return 0f; }
        }

        private static IList? GetWaterAreas()
        {
            try
            {
                object? data = _dataMember is PropertyInfo p ? p.GetValue(_generator) : (_dataMember as FieldInfo)?.GetValue(_generator);
                if (data == null) return null;
                var waMember = (MemberInfo?)data.GetType().GetField("waterAreas", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                               ?? data.GetType().GetProperty("waterAreas", BindingFlags.Public | BindingFlags.Instance);
                object? wa = waMember is FieldInfo wf ? wf.GetValue(data) : (waMember as PropertyInfo)?.GetValue(data);
                return wa as IList;
            }
            catch { return null; }
        }

        // Borrow a WaterType from waterSettings.lakeTypes so the SO reference survives save (RR's gotcha).
        private static UnityEngine.Object? ResolveLakeWaterType()
        {
            if (_cachedWaterType != null) return _cachedWaterType;
            try
            {
                var ws = _waterSettingsField?.GetValue(_generator);
                if (ws == null) return null;
                _lakeTypesField ??= ws.GetType().GetField("lakeTypes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var lakeTypes = _lakeTypesField?.GetValue(ws) as IList;
                if (lakeTypes == null || lakeTypes.Count == 0) return null;

                // Prefer an entry whose name contains "Lake", else "Pond", else the first.
                UnityEngine.Object? lake = null, pond = null, first = null;
                foreach (var t in lakeTypes)
                {
                    var uo = t as UnityEngine.Object;
                    if (uo == null) continue;
                    first ??= uo;
                    string n = uo.name ?? "";
                    if (lake == null && n.IndexOf("Lake", StringComparison.OrdinalIgnoreCase) >= 0) lake = uo;
                    if (pond == null && n.IndexOf("Pond", StringComparison.OrdinalIgnoreCase) >= 0) pond = uo;
                }
                _cachedWaterType = lake ?? pond ?? first;
                return _cachedWaterType;
            }
            catch { return null; }
        }
    }
}
