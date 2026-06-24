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

            if (!TerrainElevation.TryGetGridContext(out Heightmap hm, out int size, out float res))
            {
                SetVisible(false);
                return;
            }
            if (!TerrainElevation.TryGetBrushRect(out int minX, out int minZ, out int maxX, out int maxZ))
            {
                SetVisible(false);
                return;
            }

            // Cell block [minX..maxX] -> corner lattice [minX..maxX+1] x [minZ..maxZ+1].
            int x0 = minX, x1 = maxX + 1;
            int z0 = minZ, z1 = maxZ + 1;
            int cols = x1 - x0; // cells across
            int rows = z1 - z0; // cells deep

            // Grid lines: (rows+1) horizontal + (cols+1) vertical lines.
            int needed = (rows + 1) + (cols + 1);
            EnsureLines(needed);
            if (_root == null) return;

            Color col = ColorForMode((TerrainElevation.Mode)Mathf.Clamp(Config.TerrainMode.Value, 0, 3));

            int li = 0;

            // Horizontal lines: constant z = z0..z1, sweeping x = x0..x1.
            for (int z = z0; z <= z1; z++)
            {
                var lr = _lines[li++];
                lr.positionCount = cols + 1;
                for (int xi = 0; xi <= cols; xi++)
                {
                    int x = x0 + xi;
                    lr.SetPosition(xi, CornerWorld(hm, size, res, x, z));
                }
                lr.startColor = lr.endColor = col;
                lr.enabled = true;
            }

            // Vertical lines: constant x = x0..x1, sweeping z = z0..z1.
            for (int x = x0; x <= x1; x++)
            {
                var lr = _lines[li++];
                lr.positionCount = rows + 1;
                for (int zi = 0; zi <= rows; zi++)
                {
                    int z = z0 + zi;
                    lr.SetPosition(zi, CornerWorld(hm, size, res, x, z));
                }
                lr.startColor = lr.endColor = col;
                lr.enabled = true;
            }

            // Disable any leftover lines beyond what we used this frame.
            for (; li < _lines.Length; li++)
                _lines[li].enabled = false;

            _root.SetActive(true);
        }

        // World position of heightmap CORNER (x,z). x/z are corner indices on the cell lattice.
        // World XZ uses the SAME index*resolution mapping the brush math inverts
        // (cx = floor(world.x / res)). Height sampled from the same heightmap; GetHeight clamps
        // coords to [0,size-1] (decompile 489562) so sampling corner == size is safe.
        private static Vector3 CornerWorld(Heightmap hm, int size, float res, int x, int z)
        {
            int sx = Mathf.Clamp(x, 0, size - 1);
            int sz = Mathf.Clamp(z, 0, size - 1);
            float h = hm.GetHeight(sx, sz);
            return new Vector3(x * res, h + YOffset, z * res);
        }

        private static Color ColorForMode(TerrainElevation.Mode mode) => mode switch
        {
            TerrainElevation.Mode.Raise   => new Color(0.30f, 1f, 0.30f, 0.9f), // green
            TerrainElevation.Mode.Lower   => new Color(1f, 0.35f, 0.30f, 0.9f), // red
            TerrainElevation.Mode.Smooth  => new Color(1f, 0.92f, 0.30f, 0.9f), // yellow
            TerrainElevation.Mode.Flatten => new Color(0.35f, 0.85f, 1f, 0.9f), // cyan
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
