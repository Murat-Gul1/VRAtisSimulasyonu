using System.Collections.Generic;
using UnityEngine;

namespace VRAtis.WorldMap
{
    /// <summary>
    /// Marks a 3D bridge deck across a river as a walkable zone on the world map.
    /// Zero-allocation static lookup used by both WorldMapCameraController and WorldMapPlayerParty
    /// so players can seamlessly march across rivers between regions.
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldMapBridgeZone : MonoBehaviour
    {
        private static readonly List<WorldMapBridgeZone> ActiveBridges = new List<WorldMapBridgeZone>(8);

        [SerializeField] private float halfLength = 18f;
        [SerializeField] private float halfWidth = 6.5f;
        [SerializeField] private float deckBaseY = 19.6f;
        [SerializeField] private float archHeight = 1.6f;

        public float HalfLength => halfLength;
        public float HalfWidth => halfWidth;
        public float DeckBaseY => deckBaseY;
        public float ArchHeight => archHeight;
        public static IReadOnlyList<WorldMapBridgeZone> AllBridges => ActiveBridges;

        public bool ContainsInnerRoadway(float worldX, float worldZ, float wallMargin = 4.3f)
        {
            Vector3 local = transform.InverseTransformPoint(new Vector3(worldX, deckBaseY, worldZ));
            float innerHalfW = Mathf.Clamp(halfWidth - wallMargin, 1.8f, 3.0f);
            return Mathf.Abs(local.z) <= halfLength && Mathf.Abs(local.x) <= innerHalfW;
        }

        public bool IsOnParapetSideWall(float worldX, float worldZ, float wallMargin = 4.3f)
        {
            Vector3 local = transform.InverseTransformPoint(new Vector3(worldX, deckBaseY, worldZ));
            float innerHalfW = Mathf.Clamp(halfWidth - wallMargin, 1.8f, 3.0f);
            float parapetHalfLen = Mathf.Max(2f, halfLength - 1.5f);
            float absX = Mathf.Abs(local.x);
            return Mathf.Abs(local.z) <= parapetHalfLen && absX > innerHalfW && absX <= (halfWidth + 2.5f);
        }

        public static bool IsInsideAnyBridgeRoadway(float worldX, float worldZ, float wallMargin = 4.3f)
        {
            for (int i = 0; i < ActiveBridges.Count; i++)
            {
                WorldMapBridgeZone b = ActiveBridges[i];
                if (b != null && b.ContainsInnerRoadway(worldX, worldZ, wallMargin))
                    return true;
            }
            return false;
        }

        public static bool IsBlockedByAnyBridgeParapet(float worldX, float worldZ, float wallMargin = 4.3f)
        {
            for (int i = 0; i < ActiveBridges.Count; i++)
            {
                WorldMapBridgeZone b = ActiveBridges[i];
                if (b != null && b.IsOnParapetSideWall(worldX, worldZ, wallMargin))
                    return true;
            }
            return false;
        }

        private void OnEnable()
        {
            if (!ActiveBridges.Contains(this))
                ActiveBridges.Add(this);
        }

        private void OnDisable()
        {
            ActiveBridges.Remove(this);
        }

        private void OnDestroy()
        {
            ActiveBridges.Remove(this);
        }

        public void Configure(float lengthMeters, float widthMeters, float baseDeckY, float archMeters)
        {
            halfLength = Mathf.Max(2f, lengthMeters * 0.5f);
            halfWidth = Mathf.Max(1.5f, widthMeters * 0.5f);
            deckBaseY = baseDeckY;
            archHeight = Mathf.Max(0f, archMeters);
        }

        public bool ContainsAndSample(float worldX, float worldZ, out float deckY)
        {
            Vector3 local = transform.InverseTransformPoint(new Vector3(worldX, deckBaseY, worldZ));
            if (Mathf.Abs(local.z) <= halfLength && Mathf.Abs(local.x) <= halfWidth)
            {
                float u = Mathf.Clamp01(Mathf.Abs(local.z) / Mathf.Max(0.01f, halfLength));
                float archFactor = 1f - (u * u);
                deckY = deckBaseY + archHeight * archFactor;
                return true;
            }

            deckY = 0f;
            return false;
        }

        public static bool TryGetBridgeHeight(float worldX, float worldZ, out float deckY)
        {
            for (int i = 0; i < ActiveBridges.Count; i++)
            {
                WorldMapBridgeZone b = ActiveBridges[i];
                if (b != null && b.ContainsAndSample(worldX, worldZ, out deckY))
                    return true;
            }

            deckY = 0f;
            return false;
        }
    }
}
