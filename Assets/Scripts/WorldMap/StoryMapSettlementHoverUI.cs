using UnityEngine;
using UnityEngine.UI;

namespace VRAtis.WorldMap
{
    /// <summary>
    /// Displays a crisp Warband/Bannerlord-style floating label right above a Capital, City,
    /// Military Base, or Village whenever the yellow ground cursor ring hovers over it.
    /// Zero per-frame string allocation (updates text only when the hovered settlement changes).
    /// </summary>
    [DisallowMultipleComponent]
    public class StoryMapSettlementHoverUI : MonoBehaviour
    {
        [SerializeField] private WorldMapCameraController mapCamera;
        [SerializeField] private WorldMapSettlementRegistry registry;
        [SerializeField] private RectTransform tooltipRoot;
        [SerializeField] private Image accentBarImage;
        [SerializeField] private Image borderImage;
        [SerializeField] private Text titleText;
        [SerializeField] private Text subtitleText;
        [SerializeField] private float screenVerticalOffsetPx = 46f;

        private Camera _cam;
        private RectTransform _canvasRect;
        private string _lastHoveredId = string.Empty;
        private bool _isVisible;

        private void Awake()
        {
            if (mapCamera == null)
                mapCamera = FindAnyObjectByType<WorldMapCameraController>();
            if (registry == null)
                registry = FindAnyObjectByType<WorldMapSettlementRegistry>();

            _cam = mapCamera != null ? mapCamera.GetComponent<Camera>() : Camera.main;
            Canvas rootCanvas = GetComponentInParent<Canvas>();
            if (rootCanvas != null)
                _canvasRect = rootCanvas.GetComponent<RectTransform>();

            SetVisible(false);
        }

        private void LateUpdate()
        {
            if (mapCamera == null || registry == null || tooltipRoot == null)
                return;

            if (_cam == null)
                _cam = mapCamera.GetComponent<Camera>() ?? Camera.main;
            if (_cam == null)
                return;

            Vector3 cursorPos = mapCamera.CursorWorldPosition;
            if (TryFindHoveredSettlement(cursorPos, out WorldMapSettlementRecord hovered))
            {
                if (_lastHoveredId != hovered.id)
                {
                    _lastHoveredId = hovered.id;
                    ApplyTooltipContent(hovered);
                }

                PositionTooltipAbove(hovered);
                SetVisible(true);
            }
            else if (_isVisible)
            {
                _lastHoveredId = string.Empty;
                SetVisible(false);
            }
        }

        private bool TryFindHoveredSettlement(Vector3 cursorXZ, out WorldMapSettlementRecord match)
        {
            var list = registry.Settlements;
            int bestIdx = -1;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < list.Count; i++)
            {
                WorldMapSettlementRecord rec = list[i];
                float hoverRadius = GetHoverRadius(rec.kind);
                float dx = rec.worldPosition.x - cursorXZ.x;
                float dz = rec.worldPosition.z - cursorXZ.z;
                float dSq = dx * dx + dz * dz;
                if (dSq <= hoverRadius * hoverRadius && dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                match = list[bestIdx];
                return true;
            }

            match = default;
            return false;
        }

        private static float GetHoverRadius(WorldMapSettlementKind kind)
        {
            switch (kind)
            {
                case WorldMapSettlementKind.Capital:      return 34f;
                case WorldMapSettlementKind.City:         return 26f;
                case WorldMapSettlementKind.MilitaryBase: return 22f;
                default:                                  return 16f;
            }
        }

        private void ApplyTooltipContent(WorldMapSettlementRecord rec)
        {
            Color factionCol = GetFactionColor(rec.faction);
            if (accentBarImage != null)
                accentBarImage.color = factionCol;
            if (borderImage != null)
                borderImage.color = new Color(factionCol.r, factionCol.g, factionCol.b, 0.85f);

            if (titleText != null)
                titleText.text = rec.displayName.ToUpperInvariant();

            if (subtitleText != null)
            {
                string fName = GetFactionDisplayName(rec.faction);
                string kName = GetKindDisplayName(rec.kind);

                if (rec.kind == WorldMapSettlementKind.Village &&
                    !string.IsNullOrEmpty(rec.parentHubId) &&
                    registry.TryGetById(rec.parentHubId, out WorldMapSettlementRecord parentHub))
                {
                    subtitleText.text = $"{fName}  •  {kName}  ({parentHub.displayName})";
                }
                else
                {
                    subtitleText.text = $"{fName}  •  {kName}";
                }
            }
        }

        private void PositionTooltipAbove(WorldMapSettlementRecord rec)
        {
            float heightLift = rec.kind == WorldMapSettlementKind.Capital ? 21f
                : (rec.kind == WorldMapSettlementKind.City ? 14f
                : (rec.kind == WorldMapSettlementKind.MilitaryBase ? 11f : 8f));

            Vector3 worldAnchor = rec.worldPosition + new Vector3(0f, heightLift, 0f);
            Vector3 screenPos = _cam.WorldToScreenPoint(worldAnchor);

            if (screenPos.z < 0.1f)
            {
                SetVisible(false);
                return;
            }

            if (_canvasRect != null &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screenPos, null, out Vector2 localPt))
            {
                float halfW = _canvasRect.rect.width * 0.5f - 190f;
                float halfH = _canvasRect.rect.height * 0.5f - 65f;
                localPt.x = Mathf.Clamp(localPt.x, -halfW, halfW);
                localPt.y = Mathf.Clamp(localPt.y + screenVerticalOffsetPx, -halfH, halfH);
                tooltipRoot.anchoredPosition = localPt;
            }
        }

        private void SetVisible(bool visible)
        {
            _isVisible = visible;
            if (tooltipRoot != null && tooltipRoot.gameObject.activeSelf != visible)
                tooltipRoot.gameObject.SetActive(visible);
        }

        public static string GetFactionDisplayName(WorldMapFaction faction)
        {
            switch (faction)
            {
                case WorldMapFaction.Turkey: return "TÜRKİYE";
                case WorldMapFaction.Soviet: return "SOVYETLER";
                case WorldMapFaction.USA:    return "ABD";
                case WorldMapFaction.China:  return "ÇİN";
                default:                     return "İSYANCILAR";
            }
        }

        public static string GetKindDisplayName(WorldMapSettlementKind kind)
        {
            switch (kind)
            {
                case WorldMapSettlementKind.Capital:      return "BAŞKENT";
                case WorldMapSettlementKind.City:         return "ŞEHİR";
                case WorldMapSettlementKind.MilitaryBase: return "ASKERİ ÜS";
                default:                                  return "KÖY";
            }
        }

        public static Color GetFactionColor(WorldMapFaction faction)
        {
            switch (faction)
            {
                case WorldMapFaction.Turkey: return new Color(0.88f, 0.22f, 0.22f, 1f);
                case WorldMapFaction.Soviet: return new Color(0.76f, 0.14f, 0.14f, 1f);
                case WorldMapFaction.USA:    return new Color(0.24f, 0.52f, 0.94f, 1f);
                case WorldMapFaction.China:  return new Color(0.95f, 0.74f, 0.20f, 1f);
                default:                     return new Color(0.72f, 0.58f, 0.36f, 1f);
            }
        }
    }
}
