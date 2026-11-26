using UnityEngine;

namespace VRAtis.UI
{
    /// <summary>
    /// Constrains a full-stretch RectTransform to Screen.safeArea so notches,
    /// camera cutouts, and Android gesture bars never clip UI controls.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public class SafeAreaFitter : MonoBehaviour
    {
        [SerializeField] private bool centerHorizontally = true;
        [SerializeField] private bool centerVertically = false;

        private RectTransform _rectTransform;
        private Rect _lastSafeArea = Rect.zero;
        private Vector2Int _lastScreenSize = Vector2Int.zero;

        private void Awake()
        {
            _rectTransform = GetComponent<RectTransform>();
            ApplySafeArea(force: true);
        }

        private void Update()
        {
            ApplySafeArea(force: false);
        }

        private void ApplySafeArea(bool force)
        {
            if (_rectTransform == null)
                return;

            int screenW = Screen.width;
            int screenH = Screen.height;
            if (screenW <= 0 || screenH <= 0)
                return;

            Rect safe = Screen.safeArea;
            if (!force && safe == _lastSafeArea && screenW == _lastScreenSize.x && screenH == _lastScreenSize.y)
                return;

            _lastSafeArea = safe;
            _lastScreenSize = new Vector2Int(screenW, screenH);

            float minX = safe.xMin;
            float maxX = safe.xMax;
            float minY = safe.yMin;
            float maxY = safe.yMax;

            if (centerHorizontally)
            {
                float insetX = Mathf.Max(minX, screenW - maxX);
                minX = insetX;
                maxX = screenW - insetX;
            }

            if (centerVertically)
            {
                float insetY = Mathf.Max(minY, screenH - maxY);
                minY = insetY;
                maxY = screenH - insetY;
            }

            Vector2 anchorMin = new Vector2(Mathf.Clamp01(minX / screenW), Mathf.Clamp01(minY / screenH));
            Vector2 anchorMax = new Vector2(Mathf.Clamp01(maxX / screenW), Mathf.Clamp01(maxY / screenH));

            _rectTransform.anchorMin = anchorMin;
            _rectTransform.anchorMax = anchorMax;
            _rectTransform.offsetMin = Vector2.zero;
            _rectTransform.offsetMax = Vector2.zero;
        }
    }
}
