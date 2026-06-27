using System;
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
        public static void OnMapLoaded() { }
        public static void OnSceneExit() { }

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

            float target = Mathf.Clamp01(Config.FertilityAmount.Value * 0.01f);
            float multTarget = Mathf.Clamp01(Config.FertilityMult.Value * 0.01f);
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
                    if (hasMult) mult![j, i] = multTarget;
                    changed++;
                }
            }

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Fertility painted @ ({cwx:0},{cwz:0}) — {changed} cells -> " +
                                $"{target * 100f:0}% (mult {multTarget * 100f:0}%).");
        }
    }
}
