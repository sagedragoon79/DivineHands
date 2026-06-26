using System;
using UnityEngine;
using MelonLoader;
using LibTerrain2; // Heightmap
using DivineHands.Modules;

namespace DivineHands.Core
{
    /// <summary>
    /// Pure-code cursor grid preview for the terrain brush (no AssetBundle). Draws an outline of the
    /// exact heightmap cells <see cref="TerrainElevation"/> will edit, conforming to terrain height
    /// and coloured by the active mode. Shown only while the brush is armed
    /// (<see cref="DivinePanel.TerrainModeActive"/>) and the cursor isn't over the panel.
    ///
    /// It reuses TerrainElevation's resolved handles + cell math via the public accessors
    /// (TryGetBrushRect / TryGetGridContext) so the outline lands exactly where strokes land.
    ///
    /// Rendering: one root GameObject with a LineRenderer per grid line (rows + columns of the
    /// cell-corner lattice). Built-in "Sprites/Default" shader → untextured, vertex-coloured, no
    /// material asset needed. This is mesh-based (MeshRenderer under the hood), NOT GL immediate
    /// mode, so it draws on FF's camera with no camera hook. Line objects are pooled and only grown
    /// when the line COUNT increases; vertex positions + colour refresh every frame so the grid
    /// follows the cursor and conforms to terrain.
    /// </summary>
    public static class TerrainBrushGrid
    {
        private const float YOffset = 0.30f;   // metres above terrain (TerrainHelper used ~0.25f)
        private const float LineWidth = 0.30f; // world metres

        private static GameObject? _root;
        private static LineRenderer[] _lines = Array.Empty<LineRenderer>();
        private static int _lineCount;         // current allocated line count
        private static Material? _lineMaterial;
        private static bool _shaderFailed;

        // ---- lifecycle ----

        public static void OnMapLoaded() { /* lazily rebuilt on first Render */ }

        public static void OnSceneExit() => Teardown();

        /// <summary>Called every frame (from Plugin.OnUpdate, after TerrainElevation.OnUpdate).</summary>
        public static void Render()
        {
            // Only while the terrain brush is armed and the cursor isn't over the panel.
            if (!DivinePanel.TerrainModeActive || DivinePanel.BlocksGameInput)
            {
                SetVisible(false);
                return;
            }

            if (!TerrainElevation.TryGetGridContext(out _, out _, out float res))
            {
                SetVisible(false);
                return;
            }
            // Fractional bottom-left corner (cell units) + cell counts. originX/Z can be half-integer when
            // Fine Grid Positioning is on, so the overlay can sit on half-cell steps for squaring up
            // buildings; corners are drawn at the exact world positions and sampled bilinearly for height.
            if (!TerrainElevation.TryGetGridGeometry(out float originX, out float originZ, out int cols, out int rows))
            {
                SetVisible(false);
                return;
            }

            // Grid lines: (rows+1) horizontal + (cols+1) vertical lines.
            int needed = (rows + 1) + (cols + 1);
            EnsureLines(needed);
            if (_root == null) return;

            Color col = ColorForMode((TerrainElevation.Mode)Mathf.Clamp(Config.TerrainMode.Value, 0, 4));

            int li = 0;

            // Horizontal lines: constant z, sweeping x across the cols+1 corners.
            for (int zi = 0; zi <= rows; zi++)
            {
                float zf = originZ + zi;
                var lr = _lines[li++];
                lr.positionCount = cols + 1;
                for (int xi = 0; xi <= cols; xi++)
                    lr.SetPosition(xi, CornerWorldF(res, originX + xi, zf));
                lr.startColor = lr.endColor = col;
                lr.enabled = true;
            }

            // Vertical lines: constant x, sweeping z across the rows+1 corners.
            for (int xi = 0; xi <= cols; xi++)
            {
                float xf = originX + xi;
                var lr = _lines[li++];
                lr.positionCount = rows + 1;
                for (int zi = 0; zi <= rows; zi++)
                    lr.SetPosition(zi, CornerWorldF(res, xf, originZ + zi));
                lr.startColor = lr.endColor = col;
                lr.enabled = true;
            }

            // Disable any leftover lines beyond what we used this frame.
            for (; li < _lines.Length; li++)
                _lines[li].enabled = false;

            _root.SetActive(true);
        }

        // World position of a grid corner at FRACTIONAL cell coords (xf, zf) — World XZ = coord*resolution
        // (the same mapping the brush inverts). Height is bilinearly sampled at that world point via
        // TerrainElevation.TryGetGroundHeight (which clamps to the map), so half-cell corners conform to
        // the terrain too.
        private static Vector3 CornerWorldF(float res, float xf, float zf)
        {
            float wx = xf * res, wz = zf * res;
            float h = TerrainElevation.TryGetGroundHeight(wx, wz, out float gh) ? gh : 0f;
            return new Vector3(wx, h + YOffset, wz);
        }

        private static Color ColorForMode(TerrainElevation.Mode mode) => mode switch
        {
            TerrainElevation.Mode.Raise   => new Color(0.30f, 1f, 0.30f, 0.9f), // green
            TerrainElevation.Mode.Lower   => new Color(1f, 0.35f, 0.30f, 0.9f), // red
            TerrainElevation.Mode.Smooth  => new Color(1f, 0.92f, 0.30f, 0.9f), // yellow
            TerrainElevation.Mode.Flatten => new Color(0.35f, 0.85f, 1f, 0.9f),  // cyan
            TerrainElevation.Mode.Average => new Color(1f, 0.6f, 0.15f, 0.9f),   // orange
            _ => Color.white
        };

        // ---- line-renderer pool ----

        private static void EnsureLines(int count)
        {
            if (_root == null)
            {
                _root = new GameObject("DivineHands_TerrainBrushGrid");
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }
            if (count <= _lineCount && _lines.Length >= count) return;

            var grown = new LineRenderer[count];
            Array.Copy(_lines, grown, Math.Min(_lines.Length, count));
            for (int i = _lineCount; i < count; i++)
                grown[i] = CreateLine(i);
            _lines = grown;
            _lineCount = count;
        }

        private static LineRenderer CreateLine(int idx)
        {
            var go = new GameObject($"gridline_{idx}");
            go.transform.SetParent(_root!.transform, worldPositionStays: false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.alignment = LineAlignment.View;
            lr.numCornerVertices = 0;
            lr.numCapVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.widthMultiplier = LineWidth;
            lr.material = LineMaterial();
            lr.positionCount = 0;
            lr.enabled = false;
            return lr;
        }

        // Built-in unlit vertex-coloured shader; no AssetBundle. "Sprites/Default" is always present
        // and tints to vertex colour with no texture. Fallback to "Unlit/Color" then Internal-Colored.
        private static Material LineMaterial()
        {
            if (_lineMaterial != null) return _lineMaterial;
            try
            {
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                _lineMaterial = sh != null ? new Material(sh) : new Material(Shader.Find("Hidden/Internal-Colored"));
            }
            catch (Exception ex)
            {
                if (!_shaderFailed && Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] grid shader resolve failed: {ex.Message}");
                _shaderFailed = true;
                _lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
            }
            return _lineMaterial!;
        }

        private static void SetVisible(bool on)
        {
            if (_root != null && _root.activeSelf != on)
                _root.SetActive(on);
        }

        private static void Teardown()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }
            _lines = Array.Empty<LineRenderer>();
            _lineCount = 0;
        }
    }
}
