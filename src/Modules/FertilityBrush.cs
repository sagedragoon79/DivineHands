using System;
using System.Reflection;
using UnityEngine;
using MelonLoader;

namespace DivineHands.Modules
{
    /// <summary>
    /// Fertility painter — an adjustable brush that sets soil fertility over an area, the piece of Pangu's
    /// forest brush we never ported (we only did the tree spawner). Mirrors Pangu's
    /// ApplyAbsoluteFertilityAndMultInRect: writes AgricultureManager.cachedFertilityData (the live soil
    /// fertility crops read, 0..1) and addFertilityMults (per-cell fertilizer effectiveness) for the cells
    /// under the brush footprint. FF mutates cachedFertilityData in place during play (fertilizer adds,
    /// crops deplete) — it isn't recomputed wholesale — so a direct write sticks, and both arrays are
    /// serialized, so the paint PERSISTS through a normal save with no extra work.
    ///
    /// The footprint is the same shape/size the cursor preview draws (BrushPreview). The footprint is in
    /// heightmap cells (the brush half-extents), but the fertility grid has its own resolution
    /// (AgricultureManager.resourceCellSize), so we fill the fertility cells whose world centre falls under
    /// the footprint's world shape. All FF access is typed (no reflection) + guarded.
    /// </summary>
    public static class FertilityBrush
    {
        // Orchard-ideal soil texture/water, found by sampling FF's own fruit-tree penalty curves (cached/map).
        private static bool _idealResolved;
        private static float _idealSandClay = 0.5f, _idealWater = 0.5f;

        public static void OnMapLoaded() { _idealResolved = false; }
        public static void OnSceneExit() { _idealResolved = false; }

        public static void OnUpdate()
        {
            if (!Config.FertilityEnable.Value) return;

            if (DivineHands.Core.DivinePanel.FertilityModeActive)
            {
                if (Input.GetKeyDown(KeyCode.RightArrow)) Bump(Config.FertilityGridWidth, +1);
                if (Input.GetKeyDown(KeyCode.LeftArrow))  Bump(Config.FertilityGridWidth, -1);
                if (Input.GetKeyDown(KeyCode.UpArrow))    Bump(Config.FertilityGridHeight, +1);
                if (Input.GetKeyDown(KeyCode.DownArrow))  Bump(Config.FertilityGridHeight, -1);
            }

            if (DivineHands.Core.DivinePanel.FertilityModeActive
                && !DivineHands.Core.DivinePanel.BlocksGameInput
                && Hotkey.Pressed(Config.FertilityApplyKey.Value))
            {
                try { ApplyFertility(); }
                catch (Exception ex) { MelonLogger.Warning($"[DivineHands] Fertility brush failed: {ex.Message}"); }
            }
        }

        private static void Bump(MelonLoader.MelonPreferences_Entry<int> e, int d)
            => e.Value = Mathf.Clamp(e.Value + d, 1, 10);

        /// <summary>Footprint: the slider value is the half-extent (cells) directly — no fill/extra — so the
        /// minimum is a genuine 1x1. Shared with the cursor preview so the ring matches where paint lands.</summary>
        public static bool TryGetFootprint(out int cx, out int cz, out int fhw, out int fhh, out bool circle, out float res)
        {
            cx = cz = 0; fhw = fhh = 1; circle = false; res = 0f;
            if (!TerrainElevation.TryGetGridContext(out _, out _, out res)) return false;
            if (!TerrainElevation.TryGetCursorWorld(out Vector3 world)) return false;
            cx = Mathf.FloorToInt(world.x / res);
            cz = Mathf.FloorToInt(world.z / res);
            fhw = Mathf.Clamp(Config.FertilityGridWidth.Value, 1, 10);
            fhh = Mathf.Clamp(Config.FertilityGridHeight.Value, 1, 10);
            circle = Config.FertilityShape.Value == 1;
            return true;
        }

        private static void ApplyFertility()
        {
            if (!TryGetFootprint(out int cx, out int cz, out int fhw, out int fhh, out bool circle, out float res)) return;
            WriteFertilityRect(cx, cz, fhw, fhh, circle, res,
                Config.FertilityAmount.Value, Config.FertilityMult.Value, Config.FertilityConditionSoil.Value);
        }

        /// <summary>Absolute-write soil fertility (and, if <paramref name="conditionSoil"/>, the orchard-ideal
        /// sand/clay + water) over the footprint. Shared by the Fertility painter and the Forest brush.
        /// <paramref name="fertPct"/>/<paramref name="multPct"/> are 0–100.</summary>
        internal static void WriteFertilityRect(int cx, int cz, int fhw, int fhh, bool circle, float res,
            float fertPct, float multPct, bool conditionSoil)
        {
            var gm = GameManager.Instance;
            var am = gm != null ? gm.agricultureManager : null;
            if (am == null) return;
            float[,] fert = am.cachedFertilityData;
            float[,] mult = am.addFertilityMults;
            if (fert == null) return;
            int lenX = fert.GetLength(0), lenZ = fert.GetLength(1);
            if (lenX <= 0 || lenZ <= 0) return;
            bool hasMult = mult != null && mult.GetLength(0) == lenX && mult.GetLength(1) == lenZ;

            // Footprint world span (centre = cell corner cx*res, half = fhw*res), then to fertility-cell range.
            float fcell = Mathf.Max(0.25f, AgricultureManager.resourceCellSize);
            float cwx = cx * res, cwz = cz * res;
            float hwW = fhw * res, hhW = fhh * res;
            // High edge: nudge in by an epsilon before flooring (mirrors Pangu's -1f on its metre rect) so a
            // footprint edge landing exactly on a fertility-cell boundary doesn't pull in the next cell —
            // which would paint a row/column past the green preview ring (Rectangle shape).
            int jMin = Mathf.Clamp((int)((cwx - hwW) / fcell), 0, lenX - 1);
            int jMax = Mathf.Clamp((int)((cwx + hwW - 0.001f) / fcell), 0, lenX - 1);
            int iMin = Mathf.Clamp((int)((cwz - hhW) / fcell), 0, lenZ - 1);
            int iMax = Mathf.Clamp((int)((cwz + hhW - 0.001f) / fcell), 0, lenZ - 1);
            if (jMax < jMin || iMax < iMin) return;

            float target = Mathf.Clamp01(fertPct * 0.01f);
            float multTarget = Mathf.Clamp01(multPct * 0.01f);

            // "Condition soil for orchards": also set soil texture (sand/clay) + water to the fruit-tree ideal,
            // zeroing the sand/clay + water penalties FF subtracts from fruit-tree fertility. Ideals are read
            // from the game's own penalty curves so they track the real tuning.
            bool condition = conditionSoil;
            float[,]? sand = condition ? am.cachedSandClayData : null;
            float[,]? water = condition ? am.cachedWaterData : null;
            bool hasSand  = sand  != null && sand.GetLength(0)  == lenX && sand.GetLength(1)  == lenZ;
            bool hasWater = water != null && water.GetLength(0) == lenX && water.GetLength(1) == lenZ;
            float idealSC = 0.5f, idealW = 0.5f;
            if (condition) ResolveOrchardIdeal(am, out idealSC, out idealW);

            int changed = 0;
            for (int i = iMin; i <= iMax; i++)
            {
                for (int j = jMin; j <= jMax; j++)
                {
                    if (circle)
                    {
                        // Skip fertility cells whose world centre is outside the ellipse footprint.
                        float wx = (j + 0.5f) * fcell, wz = (i + 0.5f) * fcell;
                        float nx = (wx - cwx) / Mathf.Max(0.01f, hwW), nz = (wz - cwz) / Mathf.Max(0.01f, hhW);
                        if (nx * nx + nz * nz > 1f) continue;
                    }
                    fert[j, i] = target;
                    if (hasMult)  mult![j, i]  = multTarget;
                    if (hasSand)  sand![j, i]  = idealSC;
                    if (hasWater) water![j, i] = idealW;
                    changed++;
                }
            }

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Fertility written @ ({cwx:0},{cwz:0}) — {changed} cells -> " +
                                $"{target * 100f:0}% (mult {multTarget * 100f:0}%)" +
                                (condition ? $", orchard soil: sand/clay {idealSC:0.00}, water {idealW:0.00}." : "."));
        }

        // Find the orchard-ideal soil texture (sand/clay) + water by sampling FF's own fruit-tree penalty
        // curves for the value that minimizes the subtraction (the curve can go negative = a bonus). The
        // curves are private AnimationCurves on AgricultureManager; cached per map. Fallback 0.5 (neutral).
        private static void ResolveOrchardIdeal(object am, out float sandClay, out float water)
        {
            if (!_idealResolved)
            {
                _idealResolved = true;
                try
                {
                    var t = am.GetType();
                    var sc = t.GetField("sandClayFruitTreeFertilitySubtractor", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(am) as AnimationCurve;
                    var wc = t.GetField("waterFruitTreeFertilitySubtractor",   BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(am) as AnimationCurve;
                    _idealSandClay = MinPenaltyX(sc, 0.5f);
                    _idealWater    = MinPenaltyX(wc, 0.5f);
                }
                catch (Exception ex)
                {
                    if (Config.DebugLog.Value) MelonLogger.Warning($"[DivineHands] Fertility orchard-ideal resolve: {ex.Message}");
                }
            }
            sandClay = _idealSandClay;
            water = _idealWater;
        }

        // The X in [0,1] that minimizes the penalty curve's Y (= least subtraction / biggest bonus).
        private static float MinPenaltyX(AnimationCurve? c, float fallback)
        {
            if (c == null || c.length == 0) return fallback;
            float bestX = fallback, bestY = float.MaxValue;
            for (int i = 0; i <= 100; i++)
            {
                float x = i / 100f, y = c.Evaluate(x);
                if (y < bestY) { bestY = y; bestX = x; }
            }
            return bestX;
        }
    }
}
