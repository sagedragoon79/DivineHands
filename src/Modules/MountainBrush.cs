using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using MelonLoader;
using HarmonyLib;
using LibTerrain2; // Heightmap

namespace DivineHands.Modules
{
    /// <summary>
    /// Mountain brush — a pared-down port of Pangu's mountain brush (ApplyMountainBrush). Raises rocky
    /// terrain (dome + ridged noise), repaints the ground to rock, sets the mountain biome, and — when the
    /// amount sliders are &gt; 0 — scatters stone/ore deposits and mountain wildlife. Seven sliders (Height,
    /// Max Height, Edge Softness, Ruggedness, Rocky Texture, Rock/Ore, Wildlife) + shared shape/size;
    /// Pangu's fine noise-tuning knobs (sample divisors, blob size, y-offset, peak flood-fill) are baked
    /// to constants / dropped.
    ///
    /// Works in heightmap CELL space via the shared <see cref="TerrainElevation"/> Heightmap (world =
    /// cell·res), so it inherits DH's proven terrain read/write/refresh. Key API asymmetry (verified):
    /// Heightmap.GetHeight/SetHeight both take CELL indices here; the refresh is SmoothHeightsNotify.
    /// No undo (raising terrain is destructive — like Pangu). Texture + biome + resources are best-effort
    /// and fully guarded: if any fail, you still get the raised landform.
    /// </summary>
    public static class MountainBrush
    {
        private static readonly System.Random _rng = new System.Random();

        // ---- texture/biome reflection (resolved once per map) ----
        private static bool _reflResolved, _reflOk;
        private static object? _generator;         // TerrainGenerator
        private static MethodInfo? _mSetPixelComp, _mUpload;
        private static PropertyInfo? _pData, _pControlTextures, _pControlSize, _pTextureLayers, _pDataAreas;
        private static FieldInfo? _fAreas; // GenerationData.areas is a public FIELD (482993), not a property

        public static void OnMapLoaded() { _reflResolved = false; _reflOk = false; _generator = null; }
        public static void OnSceneExit() { _reflResolved = false; _reflOk = false; _generator = null; }

        public static void OnUpdate()
        {
            if (!Config.MountainEnable.Value) return;

            if (DivineHands.Core.DivinePanel.MountainModeActive)
            {
                if (Input.GetKeyDown(KeyCode.RightArrow)) Bump(Config.MountainGridWidth, +1);
                if (Input.GetKeyDown(KeyCode.LeftArrow))  Bump(Config.MountainGridWidth, -1);
                if (Input.GetKeyDown(KeyCode.UpArrow))    Bump(Config.MountainGridHeight, +1);
                if (Input.GetKeyDown(KeyCode.DownArrow))  Bump(Config.MountainGridHeight, -1);
            }

            if (DivineHands.Core.DivinePanel.MountainModeActive
                && !DivineHands.Core.DivinePanel.BlocksGameInput
                && Hotkey.Pressed(Config.MountainApplyKey.Value))
            {
                try { Apply(); }
                catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Mountain brush failed: {ex.Message}"); }
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
            fhw = Mathf.Clamp(Config.MountainGridWidth.Value, 1, 10);
            fhh = Mathf.Clamp(Config.MountainGridHeight.Value, 1, 10);
            circle = Config.MountainShape.Value == 1;
            return true;
        }

        private static void Apply()
        {
            if (!TryGetFootprint(out int cx, out int cz, out int fhw, out int fhh, out bool circle, out float res)) return;
            if (!TerrainElevation.TryGetGridContext(out Heightmap hm, out int size, out _) || hm == null) return;

            int minX = Mathf.Clamp(cx - fhw, 0, size - 1);
            int maxX = Mathf.Clamp(cx + fhw, 0, size - 1);
            int minZ = Mathf.Clamp(cz - fhh, 0, size - 1);
            int maxZ = Mathf.Clamp(cz + fhh, 0, size - 1);
            if (minX >= maxX || minZ >= maxZ) return;

            // 1) Height sculpt. One refresh is enough — SmoothHeightsNotify [491080] only invalidates the
            // mesh + rebuilds collision/pathing (it never mutates heights), so repeat calls are pure cost.
            int changed = SculptHeight(hm, res, cx, cz, fhw, fhh, circle, minX, minZ, maxX, maxZ);
            if (changed > 0)
                TerrainElevation.RefreshTerrainRect(minX, minZ, maxX, maxZ);

            // World rect for texture/biome/resource passes.
            float cwx = cx * res, cwz = cz * res, hwW = fhw * res, hhW = fhh * res;
            var worldRect = new Rect(cwx - hwW, cwz - hhW, 2f * hwW, 2f * hhW);

            // 2) Biome + rocky texture (best-effort).
            try { PaintBiomeAndTexture(worldRect, circle); }
            catch (Exception ex) { if (Config.DebugLog.Value) MelonLogger.Warning($"[DivineHands] Mountain texture/biome: {ex.Message}"); }

            // 3) Resources (opt-in — default 0).
            int deposits = 0, animals = 0;
            if (Config.MountainRockOre.Value > 0f) deposits = SpawnDeposits(cwx, cwz, hwW, hhW, circle);
            if (Config.MountainWildlife.Value > 0f) animals = SpawnWildlife(cwx, cwz, hwW, hhW, circle);

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Mountain @ ({cwx:0},{cwz:0}) — raised {changed} node(s), " +
                                $"{deposits} deposit(s), {animals} animal group(s)");
        }

        // ---- height sculpt (Pangu SculptMountainHeightNativeLike, simplified: no flood-fill peak mask) ----
        private static int SculptHeight(Heightmap hm, float res, int cx, int cz, int fhw, int fhh, bool circle,
            int minX, int minZ, int maxX, int maxZ)
        {
            float softness = Mathf.Clamp01(Config.MountainEdgeSoftness.Value);
            float edgePow = Mathf.Lerp(2.7f, 0.9f, softness);
            float rug = Mathf.Clamp01(Config.MountainRuggedness.Value * 0.01f);

            float spanM = Mathf.Max(8f, Mathf.Min(fhw, fhh) * 2f * res);
            float rise = Mathf.Clamp(spanM / 100f * Mathf.Max(0f, Config.MountainHeight.Value),
                                     1f, Mathf.Max(2f, Config.MountainMaxHeight.Value));
            float invSpan = 1f / Mathf.Max(20f, spanM);
            float peakFreq = Mathf.Max(0.0012f, 2f * invSpan * 2.4f);
            float baseFreq = Mathf.Max(0.0008f, 2f * invSpan * 1.6f);

            // Base elevation = average height of the flat rim band (edge factor <= 0.12).
            float sum = 0f; int cnt = 0;
            for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                {
                    float ef = EdgeFactor(x, z, cx, cz, fhw, fhh, circle);
                    if (ef <= 0f) continue;
                    if (ef <= 0.12f) { sum += hm.GetHeight(x, z); cnt++; }
                }
            float baseElev = cnt > 0 ? sum / cnt : hm.GetHeight(Mathf.Clamp(cx, minX, maxX), Mathf.Clamp(cz, minZ, maxZ));

            int changed = 0;
            for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                {
                    float ef = EdgeFactor(x, z, cx, cz, fhw, fhh, circle);
                    if (ef <= 0.001f) continue;
                    float edge = Smooth01(ef);
                    float wx = x * res, wz = z * res;
                    float cur = hm.GetHeight(x, z);

                    float dome = Mathf.Pow(edge, edgePow)
                                 * Mathf.Lerp(0.82f, 1.18f, Mathf.PerlinNoise((wx + 13.137f) * baseFreq, (wz + 67.771f) * baseFreq));
                    float ridge = 0.5f * (Ridge(wx, wz, peakFreq, 97.12f, 33.17f)
                                          + Ridge(wx, wz, peakFreq * 1.73f, 181.57f, 211.29f));
                    // Gate the ridge noise to the inner dome (Pangu's num30 peak-proximity smoothstep). Without
                    // it, a ridge crest near the rim spikes the last brushed cell several metres against the
                    // untouched cell just outside the footprint — a vertical seam instead of the edge taper.
                    // peakGate is 0 across the outer skirt (edge <= peakAbove) and ramps to 1 toward the peak.
                    const float peakAbove = 0.5f;
                    float peakGate = edge > peakAbove ? Smooth01((edge - peakAbove) / (1f - peakAbove)) : 0f;
                    float shape = Mathf.Clamp01(dome + ridge * rug * peakGate * (1f - dome));
                    float newH = Mathf.Max(cur, baseElev + rise * shape);
                    if (Mathf.Abs(newH - cur) > 0.02f) { hm.SetHeight(x, z, newH); changed++; }
                }
            return changed;
        }

        // Edge factor: 1 at centre → 0 half a cell beyond the outermost footprint cell (radial for circle,
        // Chebyshev for rect). Normalising by fhw+0.5 (a cell spans ±0.5 around its centre) keeps the rim
        // cells at a small positive weight — with the old /fhw normalisation a 1-cell brush collapsed to a
        // single-cell spike because every non-centre cell weighed exactly 0.
        private static float EdgeFactor(int x, int z, int cx, int cz, int fhw, int fhh, bool circle)
        {
            float nx = Mathf.Abs(x - cx) / (fhw + 0.5f);
            float nz = Mathf.Abs(z - cz) / (fhh + 0.5f);
            if (circle) return Mathf.Clamp01(1f - Mathf.Sqrt(nx * nx + nz * nz));
            return Mathf.Clamp01(1f - Mathf.Max(nx, nz));
        }

        private static float Smooth01(float v) { v = Mathf.Clamp01(v); return v * v * (3f - 2f * v); }

        // Ridged noise: 1 - |2·perlin - 1| (peaks at the 0.5 crossings).
        private static float Ridge(float wx, float wz, float freq, float ox, float oz)
        {
            float n = Mathf.PerlinNoise((wx + ox) * freq, (wz + oz) * freq);
            return 1f - Mathf.Abs(n * 2f - 1f);
        }

        // ---- resources ----
        private static int SpawnDeposits(float cwx, float cwz, float hwW, float hhW, bool circle)
        {
            float density = Mathf.Clamp01(Config.MountainRockOre.Value * 0.01f);
            // Scale count with area (in ~20 m tiles) × density; cap so a big brush can't dump hundreds.
            float tiles = (2f * hwW / 20f) * (2f * hhW / 20f);
            int target = Mathf.Clamp(Mathf.RoundToInt(tiles * density), 0, 40);
            int placed = 0;
            for (int i = 0; i < target; i++)
            {
                if (!TryPickPoint(cwx, cwz, hwW, hhW, circle, out float wx, out float wz)) continue;
                float gh = TerrainElevation.TryGetGroundHeight(wx, wz, out float h) ? h : 0f;
                var pos = new Vector3(wx, gh, wz);
                // Bias toward stone/iron/coal (a mining mountain); gold rarer.
                int roll = _rng.Next(100);
                int type = roll < 40 ? 3 /*Stone pit*/ : roll < 70 ? 0 /*Iron*/ : roll < 90 ? 2 /*Coal*/ : 1 /*Gold*/;
                CursorSpawners.SpawnMountainDepositAt(pos, type);
                placed++;
            }
            return placed;
        }

        private static int SpawnWildlife(float cwx, float cwz, float hwW, float hhW, bool circle)
        {
            float chance = Mathf.Clamp01(Config.MountainWildlife.Value * 0.01f);
            int groups = 0;
            // One independent roll per animal type; deer come in bigger groups than predators.
            for (int kind = 0; kind < 4; kind++)
            {
                if (_rng.NextDouble() > chance) continue;
                if (!TryPickPoint(cwx, cwz, hwW, hhW, circle, out float wx, out float wz)) continue;
                float gh = TerrainElevation.TryGetGroundHeight(wx, wz, out float h) ? h : 0f;
                int count = kind == 0 ? _rng.Next(3, 6) : _rng.Next(1, 3);
                CursorSpawners.SpawnMountainAnimalAt(kind, count, new Vector3(wx, gh, wz));
                groups++;
            }
            return groups;
        }

        private static bool TryPickPoint(float cwx, float cwz, float hwW, float hhW, bool circle, out float wx, out float wz)
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                float dx = (float)(_rng.NextDouble() * 2.0 - 1.0);
                float dz = (float)(_rng.NextDouble() * 2.0 - 1.0);
                if (circle && dx * dx + dz * dz > 1f) continue;
                wx = cwx + dx * hwW; wz = cwz + dz * hhW;
                return true;
            }
            wx = cwx; wz = cwz; return false;
        }

        // ---- biome + rocky texture (Pangu ApplyMountainBiomeToAreas + PaintMountainTextureRectWithClear,
        //      simplified: no clear-loop; Upload(true) to guarantee the GPU sees our writes) ----
        private static void PaintBiomeAndTexture(Rect worldRect, bool circle)
        {
            // Every early-exit logs under DebugLog so a failed paint is diagnosable from one stamp.
            void Skip(string why) { if (Config.DebugLog.Value) MelonLogger.Msg($"[DivineHands] Mountain texture skipped: {why}"); }

            if (!ResolveTextureRefl() || _generator == null) { Skip("reflection unresolved"); return; }

            // Find the mountain biome + set it on overlapping terrain areas (public field write; the
            // visible ground comes from the splat paint below, not this).
            object? mtnBiome = FindMountainBiomeAndAssign(worldRect);
            if (mtnBiome == null) { Skip("no mountain biome on this map"); return; }

            var terrain = TerrainElevation.ResolvedTerrain2;
            if (terrain == null) { Skip("Terrain2 unresolved"); return; }
            var data = _pData?.GetValue(terrain);
            if (data == null) { Skip("Terrain2.Data null"); return; }

            if (!(_pControlTextures?.GetValue(data) is IList controls) || controls.Count == 0) { Skip("no ControlTextures"); return; }
            int csize = _pControlSize != null ? Convert.ToInt32(_pControlSize.GetValue(data)) : 0;
            if (csize < 2) { Skip($"ControlTextureSize={csize}"); return; }
            var layers = _pTextureLayers?.GetValue(data) as IList;

            int baseLayer = ResolveLayerIndex(mtnBiome, "baseTexture", layers);
            int slopeLayer = ResolveLayerIndex(mtnBiome, "slopeTexture", layers);
            if (baseLayer < 0 && slopeLayer < 0)
            { Skip($"splat layers unresolved (layers list: {(layers == null ? "null" : layers.Count.ToString())})"); return; }

            var terrainSize = GetTerrainSize();
            float strength = Mathf.Clamp01(Config.MountainTexture.Value * 0.01f);
            if (strength <= 0f) { Skip("texture strength 0"); return; }

            int px0 = Mathf.Clamp(Mathf.FloorToInt(worldRect.xMin / Mathf.Max(1f, terrainSize.x) * (csize - 1)), 0, csize - 1);
            int px1 = Mathf.Clamp(Mathf.CeilToInt(worldRect.xMax / Mathf.Max(1f, terrainSize.x) * (csize - 1)), 0, csize - 1);
            int pz0 = Mathf.Clamp(Mathf.FloorToInt(worldRect.yMin / Mathf.Max(1f, terrainSize.z) * (csize - 1)), 0, csize - 1);
            int pz1 = Mathf.Clamp(Mathf.CeilToInt(worldRect.yMax / Mathf.Max(1f, terrainSize.z) * (csize - 1)), 0, csize - 1);
            if (px1 <= px0 || pz1 <= pz0) { Skip($"degenerate pixel rect [{px0},{pz0}..{px1},{pz1}] size={terrainSize}"); return; }

            // control index → (control texture, channel): 4 layers per control texture.
            object? baseCtl = baseLayer >= 0 && baseLayer / 4 < controls.Count ? controls[baseLayer / 4] : null;
            int baseChan = baseLayer % 4;
            object? slopeCtl = slopeLayer >= 0 && slopeLayer / 4 < controls.Count ? controls[slopeLayer / 4] : null;
            int slopeChan = slopeLayer % 4;
            if (baseCtl == null && slopeCtl == null) { Skip($"control index out of range (base {baseLayer}, slope {slopeLayer}, controls {controls.Count})"); return; }

            // Circle brush: paint only the pixels inside the ellipse, matching the height sculpt — otherwise
            // the rocky texture fills the full bounding box and spills past the round mound into its corners.
            float halfW = Mathf.Max(0.001f, worldRect.width * 0.5f), halfH = Mathf.Max(0.001f, worldRect.height * 0.5f);
            float cX = worldRect.center.x, cZ = worldRect.center.y;
            float sx = terrainSize.x / Mathf.Max(1, csize - 1), sz = terrainSize.z / Mathf.Max(1, csize - 1);
            for (int k = pz0; k <= pz1; k++)
                for (int l = px0; l <= px1; l++)
                {
                    if (circle)
                    {
                        float nx = (l * sx - cX) / halfW, nz = (k * sz - cZ) / halfH;
                        if (nx * nx + nz * nz > 1f) continue;
                    }
                    float w = strength; // uniform rock weight (simplified — no height/edge blend for v1)
                    if (baseCtl != null) _mSetPixelComp!.Invoke(baseCtl, new object[] { l, k, baseChan, w });
                    if (slopeCtl != null) _mSetPixelComp!.Invoke(slopeCtl, new object[] { l, k, slopeChan, w * 0.6f });
                }

            // Upload(true) forces the CPU data[] -> GPU texture (Upload(false) can miss SetPixelComponent writes).
            if (baseCtl != null) _mUpload!.Invoke(baseCtl, new object[] { true });
            if (slopeCtl != null && !ReferenceEquals(slopeCtl, baseCtl)) _mUpload!.Invoke(slopeCtl, new object[] { true });

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Mountain texture painted: layers base={baseLayer} slope={slopeLayer}, " +
                                $"pixels [{px0},{pz0}..{px1},{pz1}] ({(px1 - px0 + 1) * (pz1 - pz0 + 1)}), strength {strength:0.00}");
        }

        private static object? FindMountainBiomeAndAssign(Rect worldRect)
        {
            try
            {
                var data = _pDataAreas?.GetValue(_generator);
                if (data == null || !(_fAreas?.GetValue(data) is IEnumerable areas)) return null;
                object? mtn = null;
                // First find any mountain biome (biomeType has the Mountain bit = 8).
                foreach (var area in areas)
                {
                    var biome = area?.GetType().GetField("biome")?.GetValue(area)
                                ?? area?.GetType().GetProperty("biome")?.GetValue(area);
                    if (biome == null) continue;
                    var bt = biome.GetType().GetField("biomeType")?.GetValue(biome);
                    if (bt != null && (Convert.ToInt32(bt) & 8) != 0) { mtn = biome; break; }
                }
                if (mtn == null) return null;

                // Assign it to overlapping areas (public field write; invalidate caches so the game rebuilds).
                foreach (var area in areas)
                {
                    if (area == null) continue;
                    var bounds = area.GetType().GetMethod("GetBounds")?.Invoke(area, null);
                    if (bounds is Rect r && r.Overlaps(worldRect))
                        area.GetType().GetField("biome")?.SetValue(area, mtn);
                }
                return mtn;
            }
            catch { return null; }
        }

        private static int ResolveLayerIndex(object biome, string texField, IList? layers)
        {
            try
            {
                var entry = biome.GetType().GetField(texField)?.GetValue(biome);
                if (entry == null) return -1;
                var et = entry.GetType();
                int splat = et.GetField("splatIndex") is FieldInfo sf ? Convert.ToInt32(sf.GetValue(entry)) : -1;
                if (splat >= 0 && (layers == null || splat < layers.Count)) return splat;
                // Fallback: match by diffuse (+ normal) texture reference in the layer list.
                if (layers == null) return -1;
                var diff = et.GetField("texture")?.GetValue(entry) as UnityEngine.Object;
                if (diff == null) return -1;
                for (int i = 0; i < layers.Count; i++)
                {
                    var lay = layers[i]; if (lay == null) continue;
                    var ld = lay.GetType().GetField("diffuse")?.GetValue(lay) as UnityEngine.Object;
                    if (ld == diff) return i;
                }
            }
            catch { }
            return -1;
        }

        private static Vector3 GetTerrainSize()
        {
            try
            {
                var gm = GameManager.Instance;
                var tm = gm != null ? gm.terrainManager : null;
                var m = tm?.GetType().GetMethod("GetTerrainSize", Type.EmptyTypes);
                if (m != null && m.Invoke(tm, null) is Vector3 v) return v;
            }
            catch { }
            return new Vector3(1024f, 0f, 1024f);
        }

        private static bool ResolveTextureRefl()
        {
            if (_reflResolved) return _reflOk;
            _reflResolved = true;
            try
            {
                var terrain = TerrainElevation.ResolvedTerrain2;
                if (terrain == null) return false;

                // TerrainGenerator off the terrain GO (same GO the heightmap terrain lives on).
                var tGen = AccessTools.TypeByName("TerrainGenerator");
                if (terrain is Component tc && tGen != null) _generator = tc.GetComponent(tGen);
                if (_generator == null)
                {
                    var gm = GameManager.Instance; var tmb = gm != null ? gm.terrainManager : null;
                    var tmComp = tmb as Component;
                    if (tmComp != null && tGen != null) _generator = tmComp.GetComponentInChildren(tGen) ?? UnityEngine.Object.FindObjectOfType(tGen);
                }

                _pData = terrain.GetType().GetProperty("Data");
                var dataType = _pData?.PropertyType;
                _pControlTextures = dataType?.GetProperty("ControlTextures");
                _pControlSize = dataType?.GetProperty("ControlTextureSize");
                _pTextureLayers = dataType?.GetProperty("TextureLayers");

                var tGenType = _generator?.GetType();
                _pDataAreas = tGenType?.GetProperty("Data");        // TerrainGenerator.Data (property, 483518)
                var genDataType = _pDataAreas?.PropertyType;
                _fAreas = genDataType?.GetField("areas");           // public List<TerrainArea> areas — a FIELD (482993)

                var tCtl = AccessTools.TypeByName("Terrain2Control");
                if (tCtl != null)
                {
                    _mSetPixelComp = tCtl.GetMethod("SetPixelComponent", new[] { typeof(int), typeof(int), typeof(int), typeof(float) });
                    _mUpload = tCtl.GetMethod("Upload", new[] { typeof(bool) });
                }

                _reflOk = _generator != null && _pData != null && _pControlTextures != null
                          && _pControlSize != null && _mSetPixelComp != null && _mUpload != null
                          && _pDataAreas != null && _fAreas != null;
                if (!_reflOk && Config.DebugLog.Value)
                    MelonLogger.Warning("[DivineHands] Mountain: texture/biome reflection incomplete — sculpt only.");
                return _reflOk;
            }
            catch (Exception ex) { if (Config.DebugLog.Value) MelonLogger.Warning($"[DivineHands] Mountain refl: {ex.Message}"); return false; }
        }
    }
}
