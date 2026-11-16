using UnityEngine;

namespace VRAtis.Environment
{
    /// <summary>
    /// Recycles a lightweight pool of wind-swaying foliage clusters into the visible ground
    /// corridor ahead of the player's gaze / Red Dot aim direction while keeping clusters
    /// offscreen before relocating (zero visual popping, minimal mobile VR vertex count).
    /// </summary>
    public class DynamicLookFoliageFocus : MonoBehaviour
    {
        [Header("Bakış Odaklı Bitki Havuzu")]
        [SerializeField] private Transform cameraTransform;
        [SerializeField] private Transform[] focusClusters;
        [SerializeField] private float minRangeMeters = 28f;
        [SerializeField] private float maxRangeMeters = 168f;
        [SerializeField] private float keepConeCos = 0.52f; // ~58 deg half-angle stay zone
        [SerializeField] private float minUpdateIntervalSec = 0.20f;

        private float _checkTimer;
        private int _slotCursor;

        // Staggered range distances & side offsets so clusters frame the sightline naturally
        private static readonly float[] StaggeredDistances = { 32f, 52f, 74f, 96f, 118f, 142f, 160f, 84f };
        private static readonly float[] StaggeredSides = { -11f, 12f, -18f, 17f, -9f, 20f, -22f, 9f };

        private void Awake()
        {
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;
        }

        private void Start()
        {
            ArrangeInitialView();
        }

        private void Update()
        {
            if (focusClusters == null || focusClusters.Length == 0 || cameraTransform == null)
                return;

            _checkTimer += Time.deltaTime;
            if (_checkTimer < minUpdateIntervalSec)
                return;
            _checkTimer = 0f;

            Vector3 camPos = cameraTransform.position;
            Vector3 fwd = cameraTransform.forward;
            Vector3 fwdFlat = new Vector3(fwd.x, 0f, fwd.z);
            if (fwdFlat.sqrMagnitude < 1e-4f)
                return;
            fwdFlat.Normalize();

            for (int i = 0; i < focusClusters.Length; i++)
            {
                Transform cluster = focusClusters[i];
                if (cluster == null) continue;

                Vector3 delta = cluster.position - camPos;
                delta.y = 0f;
                float dist = delta.magnitude;

                bool outOfRange = dist < minRangeMeters || dist > (maxRangeMeters + 35f);
                bool outOfView = dist > 1f && Vector3.Dot(fwdFlat, delta / dist) < keepConeCos;

                if (outOfRange || outOfView)
                {
                    PlaceClusterAhead(cluster, camPos, fwdFlat);
                }
            }
        }

        private void ArrangeInitialView()
        {
            if (focusClusters == null || focusClusters.Length == 0 || cameraTransform == null)
                return;

            Vector3 camPos = cameraTransform.position;
            Vector3 fwd = cameraTransform.forward;
            Vector3 fwdFlat = new Vector3(fwd.x, 0f, fwd.z);
            if (fwdFlat.sqrMagnitude < 1e-4f)
                fwdFlat = Vector3.forward;
            fwdFlat.Normalize();

            for (int i = 0; i < focusClusters.Length; i++)
            {
                if (focusClusters[i] != null)
                    PlaceClusterAhead(focusClusters[i], camPos, fwdFlat);
            }
        }

        private void PlaceClusterAhead(Transform cluster, Vector3 camPos, Vector3 fwdFlat)
        {
            int idx = _slotCursor % StaggeredDistances.Length;
            _slotCursor++;

            Vector3 rightFlat = new Vector3(fwdFlat.z, 0f, -fwdFlat.x);
            float forwardDist = Mathf.Clamp(StaggeredDistances[idx], minRangeMeters, maxRangeMeters);
            float sideOffset = StaggeredSides[idx];

            Vector3 pos = camPos + fwdFlat * forwardDist + rightFlat * sideOffset;
            pos.y = 0f;

            // Nudge slightly out of the dead-center 5m target line so targets stay unobstructed
            if (Mathf.Abs(pos.x) < 5.2f && pos.z > 18f && pos.z < 515f)
            {
                pos.x = pos.x >= 0f ? 6.5f : -6.5f;
            }

            cluster.position = pos;
        }
    }
}
