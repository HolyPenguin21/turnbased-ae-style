#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Editor-only inspection: never modifies scene objects or gameplay.
public static class ResponsiveUiAudit
{
    [MenuItem("Tools/UI/Audit Responsive Layout")]
    private static void Audit()
    {
        var scalers = Object.FindObjectsByType<CanvasScaler>(FindObjectsInactive.Include);
        var rects = Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include);
        int warnings = 0;
        int overflowWarnings = 0;
        foreach (var scaler in scalers)
        {
            if (!scaler.gameObject.scene.IsValid()) continue;
            if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize ||
                scaler.referenceResolution != new Vector2(1280, 720))
            {
                Debug.LogWarning($"UI scaler needs review: {GetPath(scaler.transform)}", scaler);
                warnings++;
            }
        }
        foreach (var rect in rects)
        {
            if (!rect.gameObject.scene.IsValid() || rect.parent == null) continue;
            if (rect.anchorMin.x < 0 || rect.anchorMin.y < 0 ||
                rect.anchorMax.x > 1 || rect.anchorMax.y > 1 ||
                rect.anchorMin.x > rect.anchorMax.x || rect.anchorMin.y > rect.anchorMax.y)
            {
                Debug.LogWarning($"UI anchors outside parent: {GetPath(rect)}", rect);
                warnings++;
            }
            if (rect.localScale.x == 0 || rect.localScale.y == 0) continue;
            // Only root Canvas children: scroll content and animated cards may overflow intentionally.
            if (rect.parent is RectTransform parentRect &&
                parentRect.GetComponent<Canvas>() is Canvas parentCanvas &&
                parentCanvas.isRootCanvas &&
                parentCanvas.renderMode != RenderMode.WorldSpace)
            {
                var corners = new Vector3[4];
                var canvasCorners = new Vector3[4];
                rect.GetWorldCorners(corners);
                parentRect.GetWorldCorners(canvasCorners);
                float minX = Mathf.Min(canvasCorners[0].x, canvasCorners[2].x);
                float maxX = Mathf.Max(canvasCorners[0].x, canvasCorners[2].x);
                float minY = Mathf.Min(canvasCorners[0].y, canvasCorners[2].y);
                float maxY = Mathf.Max(canvasCorners[0].y, canvasCorners[2].y);
                bool outside = false;
                foreach (var corner in corners)
                    if (corner.x < minX - 1 || corner.x > maxX + 1 ||
                        corner.y < minY - 1 || corner.y > maxY + 1)
                        outside = true;
                if (outside)
                {
                    Debug.LogWarning($"Root UI rect extends beyond Canvas: {GetPath(rect)}", rect);
                    overflowWarnings++;
                }
            }
            // A fixed-size control can be intentional; report, never auto-correct.
            if (rect.anchorMin == rect.anchorMax &&
                (Mathf.Abs(rect.anchoredPosition.x) > 1280 ||
                 Mathf.Abs(rect.anchoredPosition.y) > 720))
            {
                Debug.LogWarning($"Large fixed UI offset: {GetPath(rect)}", rect);
                warnings++;
            }
        }
        Debug.Log($"Responsive UI audit: {scalers.Length} scalers, {rects.Length} RectTransforms, {warnings} configuration warnings, {overflowWarnings} root overflow warnings. Check Game View at 1280x720, 1600x900, 1920x1080 and 2560x1440.");
    }

    private static string GetPath(Transform transform)
    {
        var parts = new List<string>();
        for (var node = transform; node != null; node = node.parent)
            parts.Add(node.name);
        parts.Reverse();
        return string.Join("/", parts);
    }
}
#endif

