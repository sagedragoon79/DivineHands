using System;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using DivineHands.Modules;

namespace DivineHands.Core
{
    /// <summary>
    /// Cursor footprint outline shared by the adjustable brushes (Lake stamp, Fertility painter) — the
    /// Pangu/TerrainHelper-style indicator. Draws one closed outline of the active tool's footprint (ellipse
    /// for Circle, rectangle for Rectangle) at the cursor, conforming to the current terrain, coloured per
    /// tool. Only one brush is ever armed at a time, so a single renderer dispatches on the armed tool.
    ///
    /// Geometry comes from each tool's TryGetFootprint — the SAME call its apply uses — so the ring always
    /// lands where the action will. Rendering: one pooled LineRenderer (loop), built-in "Sprites/Default"
    /// shader (untextured, vertex-coloured, no AssetBundle), positions refreshed each frame.
    /// </summary>
    public static class BrushPreview
    {
        private const float YOffset = 0.35f;   // metres above terrain
        private const float LineWidth = 0.40f; // world metres
        private const int EllipseSegments = 72;

        private static GameObject? _root;
        private static LineRenderer? _line;
        private static Material? _mat;
        private static bool _shaderFailed;

        private static readonly List<Vector3> _pts = new List<Vector3>(128);
        private static readonly Color WaterBlue = new Color(0.25f, 0.7f, 1f, 0.95f);
        private static readonly Color FertGreen = new Color(0.35f, 0.95f, 0.30f, 0.95f);

        public static void OnMapLoaded() { /* lazily rebuilt on first Render */ }
        public static void OnSceneExit() => Teardown();

        /// <summary>Called every frame (Plugin.OnUpdate, after the brush modules' OnUpdate).</summary>
        public static void Render()
        {
            if (DivinePanel.BlocksGameInput) { SetVisible(false); return; }

            int cx = 0, cz = 0, fhw = 1, fhh = 1; bool circle = false; float res = 0f; Color col;
            if (DivinePanel.LakeModeActive && LakeStamp.TryGetFootprint(out cx, out cz, out fhw, out fhh, out circle, out res))
                col = WaterBlue;
            else if (DivinePanel.FertilityModeActive && FertilityBrush.TryGetFootprint(out cx, out cz, out fhw, out fhh, out circle, out res))
                col = FertGreen;
            else { SetVisible(false); return; }

            BuildPerimeter(cx, cz, fhw, fhh, circle, res);
            if (_pts.Count < 3) { SetVisible(false); return; }

            EnsureLine();
            if (_line == null) return;
            _line.loop = true;
            _line.positionCount = _pts.Count;
            for (int i = 0; i < _pts.Count; i++) _line.SetPosition(i, _pts[i]);
            _line.startColor = _line.endColor = col;
            _line.enabled = true;
            _root!.SetActive(true);
        }

        // Build the footprint perimeter in cell space, projected to world (cell*res) + terrain height.
        private static void BuildPerimeter(int cx, int cz, int fhw, int fhh, bool circle, float res)
        {
            _pts.Clear();
            if (circle)
            {
                for (int i = 0; i < EllipseSegments; i++)
                {
                    float a = (Mathf.PI * 2f * i) / EllipseSegments;
                    Add(cx + fhw * Mathf.Cos(a), cz + fhh * Mathf.Sin(a), res);
                }
            }
            else
            {
                for (int x = -fhw; x <= fhw; x++)        Add(cx + x, cz - fhh, res);   // bottom
                for (int z = -fhh + 1; z <= fhh; z++)    Add(cx + fhw, cz + z, res);   // right
                for (int x = fhw - 1; x >= -fhw; x--)    Add(cx + x, cz + fhh, res);   // top
                for (int z = fhh - 1; z >= -fhh + 1; z--) Add(cx - fhw, cz + z, res);  // left
            }
        }

        private static void Add(float cellX, float cellZ, float res)
        {
            float wx = cellX * res, wz = cellZ * res;
            float h = TerrainElevation.TryGetGroundHeight(wx, wz, out float gh) ? gh : 0f;
            _pts.Add(new Vector3(wx, h + YOffset, wz));
        }

        // ---- single pooled LineRenderer ----
        private static void EnsureLine()
        {
            if (_root == null)
            {
                _root = new GameObject("DivineHands_BrushPreview");
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }
            if (_line != null) return;
            var go = new GameObject("brushoutline");
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            _line = go.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.alignment = LineAlignment.View;
            _line.numCornerVertices = 2;
            _line.numCapVertices = 0;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.widthMultiplier = LineWidth;
            _line.material = LineMaterial();
            _line.positionCount = 0;
            _line.enabled = false;
        }

        private static Material LineMaterial()
        {
            if (_mat != null) return _mat;
            try
            {
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                _mat = sh != null ? new Material(sh) : new Material(Shader.Find("Hidden/Internal-Colored"));
            }
            catch (Exception ex)
            {
                if (!_shaderFailed && Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] brush preview shader resolve failed: {ex.Message}");
                _shaderFailed = true;
                _mat = new Material(Shader.Find("Hidden/Internal-Colored"));
            }
            return _mat!;
        }

        private static void SetVisible(bool on)
        {
            if (_root != null && _root.activeSelf != on) _root.SetActive(on);
        }

        private static void Teardown()
        {
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; }
            _line = null;
            _pts.Clear();
        }
    }
}
