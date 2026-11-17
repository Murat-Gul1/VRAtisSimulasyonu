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
        private int _scanCursor;
        private Vector3 _prevFrameFwdFlat = Vector3.forward;
        private Vector3 _lastEvaluatedFwdFlat = Vector3.forward;
        private Vector3 _lastEvaluatedCamPos;
        private bool _hasValidClusters;

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
            _hasValidClusters = HasAnyNonNullCluster();
            if (!_hasValidClusters)
            {
                // Disable per-frame Update overhead when no dynamic clusters are assigned in the scene
                enabled = false;
                return;
            }

            ArrangeInitialView();
        }

        private void Update()
        {
            if (!_hasValidClusters || focusClusters == null || focusClusters.Length == 0 || cameraTransform == null)
                return;

            Vector3 fwd = cameraTransform.forward;
            Vector3 fwdFlat = new Vector3(fwd.x, 0f, fwd.z);
            float fwdSq = fwdFlat.sqrMagnitude;
            if (fwdSq < 1e-4f)
                return;
            fwdFlat *= 1f / Mathf.Sqrt(fwdSq);

            // Detect active head rotation: never relocate foliage transforms while the user is actively turning
            // their head, preventing bounding-volume / shadow culling rebuild spikes mid-pan.
            float frameDot = Mathf.Clamp(Vector3.Dot(fwdFlat, _prevFrameFwdFlat), -1f, 1f);
            _prevFrameFwdFlat = fwdFlat;

            float dt = Mathf.Max(0.0001f, Time.unscaledDeltaTime);
            float approxHeadTurnDeg = Mathf.Acos(frameDot) * Mathf.Rad2Deg;
            float headTurnSpeedDegPerSec = approxHeadTurnDeg / dt;
            if (headTurnSpeedDegPerSec > 2.5f)
            {
                // Reset timer slightly so cluster recycling waits until head motion settles
                _checkTimer = 0f;
                return;
            }

            _checkTimer += dt;
            if (_checkTimer < minUpdateIntervalSec)
                return;
            _checkTimer = 0f;

            Vector3 camPos = cameraTransform.position;

            // If gaze and position changed since the last completed pass, start a new amortized scan pass
            float gazeDotSinceLastPass = Vector3.Dot(fwdFlat, _lastEvaluatedFwdFlat);
            float moveSqSinceLastPass = (camPos - _lastEvaluatedCamPos).sqrMagnitude;
            if (gazeDotSinceLastPass < 0.990f || moveSqSinceLastPass > 1.0f)
            {
                _lastEvaluatedFwdFlat = fwdFlat;
                _lastEvaluatedCamPos = camPos;
                _scanCursor = 0;
            }

            if (_scanCursor >= focusClusters.Length)
                return;

            float minRangeSq = minRangeMeters * minRangeMeters;
            float maxOuterRange = maxRangeMeters + 35f;
            float maxRangeSq = maxOuterRange * maxOuterRange;
            float keepConeCosSq = keepConeCos * Mathf.Abs(keepConeCos);

            // Amortize checks across intervals and relocate at most ONE cluster per interval
            while (_scanCursor < focusClusters.Length)
            {
                Transform cluster = focusClusters[_scanCursor++];
                if (cluster == null)
                    continue;

                Vector3 clusterPos = cluster.position;
                float dx = clusterPos.x - camPos.x;
                float dz = clusterPos.z - camPos.z;
                float distSq = dx * dx + dz * dz;

                bool outOfRange = distSq < minRangeSq || distSq > maxRangeSq;
                bool outOfView = false;
                if (!outOfRange && distSq > 1f)
                {
                    float dot = fwdFlat.x * dx + fwdFlat.z * dz;
                    float signedDotSq = dot >= 0f ? (dot * dot) : -(dot * dot);
                    outOfView = signedDotSq < keepConeCosSq * distSq;
                }

                if (outOfRange || outOfView)
                {
                    PlaceClusterAhead(cluster, camPos, fwdFlat);
                    break;
                }
            }
        }

        private bool HasAnyNonNullCluster()
        {
            if (focusClusters == null || focusClusters.Length == 0)
                return false;

            for (int i = 0; i < focusClusters.Length; i++)
            {
                if (focusClusters[i] != null)
                    return true;
            }
            return false;
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

            _prevFrameFwdFlat = fwdFlat;
            _lastEvaluatedFwdFlat = fwdFlat;
            _lastEvaluatedCamPos = camPos;
            _scanCursor = focusClusters.Length;

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

            if ((cluster.position - pos).sqrMagnitude > 0.01f)
            {
                cluster.position = pos;
            }
        }
    }
}
