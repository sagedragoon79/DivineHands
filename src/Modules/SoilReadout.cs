using UnityEngine;

namespace DivineHands.Modules
{
    /// <summary>
    /// Read-only soil inspector for the cell under the cursor — surfaces the values vanilla never shows you:
    /// crop fertility, the sand/clay mix, water content, and the resulting fruit-tree (orchard) fertility.
    /// (Vanilla already shows wild-tree growth via the work-camp placement % / heatmap, and matching that
    /// number depends on placement-time state, so it's intentionally left out here.)
    ///
    /// All values come from AgricultureManager's public per-cell getters (the int-index overloads), so this
    /// is pure typed reads, no reflection. Indexing matches FF: cell = (int)(world / resourceCellSize),
    /// idx = cellX * maxZ + cellZ (maxZ = array's 2nd dimension = AgricultureManager.maxIndexZ).
    /// </summary>
    public static class SoilReadout
    {
        public struct Sample
        {
            public bool valid;
            public float crop;       // 0..1 crop soil fertility
            public float sandClay;   // 0..1 (0 = all clay, 1 = all sand)
            public float water;      // 0..1 soil water content
            public float fruitTree;  // 0..1 derived orchard fertility
        }

        public static bool TryRead(out Sample s)
        {
            s = default;
            if (!TerrainElevation.TryGetGridContext(out _, out _, out _)) return false;
            if (!TerrainElevation.TryGetCursorWorld(out Vector3 world)) return false;

            var gm = GameManager.Instance;
            var am = gm != null ? gm.agricultureManager : null;
            if (am == null) return false;
            var fert = am.cachedFertilityData;
            if (fert == null) return false;

            int maxX = fert.GetLength(0), maxZ = fert.GetLength(1);
            float fcell = Mathf.Max(0.25f, AgricultureManager.resourceCellSize);
            int cx = (int)(world.x / fcell), cz = (int)(world.z / fcell);
            if (cx < 0 || cx >= maxX || cz < 0 || cz >= maxZ) return false;

            int idx = cx * maxZ + cz; // FF decode: num = idx/maxZ (=cellX), num2 = idx - num*maxZ (=cellZ)
            s.crop = am.GetFertilityPercent(idx);
            s.sandClay = am.GetSandClayPercent(idx);
            s.water = am.GetWaterPercent(idx);
            s.fruitTree = am.GetFruitTreeFertilityPercent(idx);
            s.valid = true;
            return true;
        }

        /// <summary>Plain-language soil texture from the 0..1 sand/clay value (0 = clay, 1 = sand).</summary>
        public static string SandClayLabel(float v)
            => v < 0.35f ? "clay-heavy" : (v > 0.65f ? "sandy" : "loamy");
    }
}
