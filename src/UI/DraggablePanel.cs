using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace DivineHands.UI
{
    /// <summary>
    /// Attach to a GameObject that acts as a drag handle; its pointer events drag the
    /// configured Target RectTransform around its canvas. Right-click resets to the default
    /// position. Straight copy of Keep Clarity's DraggablePanel pattern (KC is a soft dep —
    /// copied, not referenced). Position clamps so the panel can't fully leave the screen,
    /// and TopMargin keeps it from sliding under FF's top bar.
    /// </summary>
    public class DraggablePanel : MonoBehaviour, IBeginDragHandler, IDragHandler, IPointerClickHandler
    {
        public RectTransform? Target;
        public Vector2 DefaultNormalizedPosition = new Vector2(0.5f, 0.5f);
        public Action<Vector2>? OnPositionChanged; // normalized 0..1 of Target.pivot in canvas space
        public float TopMargin;                    // reserved px at the top (FF's top bar)

        private Canvas? _canvas;
        private RectTransform? _canvasRt;
        private Vector2 _startPointerLocal;
        private Vector2 _startTargetPos;

        private Canvas? Canvas => _canvas ??= GetComponentInParent<Canvas>();
        private RectTransform? CanvasRt => _canvasRt ??= Canvas?.transform as RectTransform;

        public void OnBeginDrag(PointerEventData e)
        {
            if (Target == null || CanvasRt == null || e.button != PointerEventData.InputButton.Left) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                CanvasRt, e.position, e.pressEventCamera, out _startPointerLocal);
            _startTargetPos = Target.anchoredPosition;
        }

        public void OnDrag(PointerEventData e)
        {
            if (Target == null || CanvasRt == null || e.button != PointerEventData.InputButton.Left) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                CanvasRt, e.position, e.pressEventCamera, out var local);
            ApplyPosition(_startTargetPos + (local - _startPointerLocal), persist: true);
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Right) return; // right-click = reset escape hatch
            ApplyNormalized(DefaultNormalizedPosition, persist: true);
        }

        public void ApplyPosition(Vector2 anchoredPos, bool persist)
        {
            if (Target == null || CanvasRt == null) return;

            var canvasSize = CanvasRt.rect.size;
            var panelSize = Target.rect.size;
            const float Margin = 1f;

            var anchor = Target.anchorMin; // anchorMin == anchorMax for our corner-anchored panel
            var pivot = Target.pivot;
            float anchorX = anchor.x * canvasSize.x;
            float anchorY = anchor.y * canvasSize.y;

            float minX = Margin - anchorX + pivot.x * panelSize.x;
            float maxX = canvasSize.x - Margin - anchorX - (1f - pivot.x) * panelSize.x;
            float minY = Margin - anchorY + pivot.y * panelSize.y;
            float maxY = canvasSize.y - Margin - TopMargin - anchorY - (1f - pivot.y) * panelSize.y;
            if (minX > maxX) maxX = minX;
            if (minY > maxY) maxY = minY;

            anchoredPos.x = Mathf.Clamp(anchoredPos.x, minX, maxX);
            anchoredPos.y = Mathf.Clamp(anchoredPos.y, minY, maxY);
            Target.anchoredPosition = anchoredPos;

            if (persist && OnPositionChanged != null)
                OnPositionChanged(NormalizedFromAnchored(anchoredPos));
        }

        public void ApplyNormalized(Vector2 normalized, bool persist)
        {
            if (Target == null || CanvasRt == null) return;
            var canvasSize = CanvasRt.rect.size;
            var anchor = Target.anchorMin;
            ApplyPosition(new Vector2(
                normalized.x * canvasSize.x - anchor.x * canvasSize.x,
                normalized.y * canvasSize.y - anchor.y * canvasSize.y), persist);
        }

        private Vector2 NormalizedFromAnchored(Vector2 anchoredPos)
        {
            var canvasSize = CanvasRt!.rect.size;
            var anchor = Target!.anchorMin;
            return new Vector2(
                canvasSize.x > 0 ? (anchor.x * canvasSize.x + anchoredPos.x) / canvasSize.x : 0,
                canvasSize.y > 0 ? (anchor.y * canvasSize.y + anchoredPos.y) / canvasSize.y : 0);
        }
    }
}
