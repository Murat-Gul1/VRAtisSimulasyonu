using System;
using UnityEngine;

namespace VRAtis.WorldMap
{
    /// <summary>
    /// Controls the player's armed, banner-carrying commander figure on the Warband-style world map.
    /// Listens to WorldMapCameraController.OnCursorConfirmPressed (Joystick center button / Space / Enter)
    /// and marches smoothly toward the selected destination.
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldMapPlayerParty : MonoBehaviour
    {
        [Header("Referanslar")]
        [SerializeField] private WorldMapCameraController mapCamera;
        [SerializeField] private Transform visualRoot;
        [SerializeField] private Transform bannerClothTransform;

        [Header("Harita Hareketi")]
        [SerializeField] private float moveSpeed = 12f;
        [SerializeField] private float turnSpeedDeg = 420f;
        [SerializeField] private float arrivalDistance = 0.25f;
        [SerializeField] private Vector2 mapBoundsMin = new Vector2(-100f, -100f);
        [SerializeField] private Vector2 mapBoundsMax = new Vector2(100f, 100f);

        [Header("Geçici Yürüyüş & Bayrak Hareketi (Mixamo Öncesi)")]
        [SerializeField] private bool enableWalkBob = true;
        [SerializeField] private float walkBobFrequency = 9.5f;
        [SerializeField] private float walkBobAmplitude = 0.08f;
        [SerializeField] private float bannerWindSpeed = 3.2f;
        [SerializeField] private float bannerWindAngleDeg = 4.5f;

        public event Action<Vector3> OnDestinationReached;

        private Vector3 _destination;
        private bool _hasDestination;
        private float _walkPhase;
        private Vector3 _visualBaseLocalPos;
        private Quaternion _bannerBaseLocalRot;

        public bool IsMoving => _hasDestination;
        public Vector3 Destination => _destination;

        private void Awake()
        {
            if (mapCamera == null)
                mapCamera = FindAnyObjectByType<WorldMapCameraController>();

            if (visualRoot != null)
                _visualBaseLocalPos = visualRoot.localPosition;

            if (bannerClothTransform != null)
                _bannerBaseLocalRot = bannerClothTransform.localRotation;

            _destination = transform.position;
        }

        private void OnEnable()
        {
            if (mapCamera == null)
                mapCamera = FindAnyObjectByType<WorldMapCameraController>();

            if (mapCamera != null)
                mapCamera.OnCursorConfirmPressed += HandleCursorConfirmed;
        }

        private void OnDisable()
        {
            if (mapCamera != null)
                mapCamera.OnCursorConfirmPressed -= HandleCursorConfirmed;
        }

        private void Update()
        {
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.0001f, 0.05f);

            UpdateMovement(dt);
            UpdateBannerWind();
        }

        public void MoveTo(Vector3 worldPoint)
        {
            float clampedX = Mathf.Clamp(worldPoint.x, mapBoundsMin.x, mapBoundsMax.x);
            float clampedZ = Mathf.Clamp(worldPoint.z, mapBoundsMin.y, mapBoundsMax.y);
            _destination = new Vector3(clampedX, 0f, clampedZ);

            Vector3 flatDelta = _destination - transform.position;
            flatDelta.y = 0f;
            _hasDestination = flatDelta.sqrMagnitude > (arrivalDistance * arrivalDistance);
        }

        public void StopMovement()
        {
            _hasDestination = false;
            _destination = transform.position;
        }

        private void HandleCursorConfirmed(Vector3 cursorWorldPos)
        {
            MoveTo(cursorWorldPos);
        }

        private void UpdateMovement(float dt)
        {
            if (_hasDestination)
            {
                Vector3 current = transform.position;
                Vector3 toTarget = _destination - current;
                toTarget.y = 0f;
                float dist = toTarget.magnitude;

                if (dist <= arrivalDistance)
                {
                    transform.position = new Vector3(_destination.x, 0f, _destination.z);
                    _hasDestination = false;
                    OnDestinationReached?.Invoke(transform.position);
                }
                else
                {
                    Vector3 dir = toTarget / dist;
                    Quaternion targetRot = Quaternion.LookRotation(dir, Vector3.up);
                    transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, turnSpeedDeg * dt);

                    float step = Mathf.Min(dist, moveSpeed * dt);
                    Vector3 next = current + dir * step;
                    next.y = 0f;
                    transform.position = next;

                    if (enableWalkBob && visualRoot != null)
                    {
                        _walkPhase += dt * walkBobFrequency;
                        float bobY = Mathf.Abs(Mathf.Sin(_walkPhase)) * walkBobAmplitude;
                        visualRoot.localPosition = _visualBaseLocalPos + new Vector3(0f, bobY, 0f);
                    }
                    return;
                }
            }

            if (visualRoot != null && (visualRoot.localPosition - _visualBaseLocalPos).sqrMagnitude > 1e-6f)
            {
                visualRoot.localPosition = Vector3.Lerp(visualRoot.localPosition, _visualBaseLocalPos, 1f - Mathf.Exp(-14f * dt));
            }
        }

        private void UpdateBannerWind()
        {
            if (bannerClothTransform == null)
                return;

            float speedFactor = _hasDestination ? 1.85f : 1.0f;
            float sway = Mathf.Sin(Time.unscaledTime * bannerWindSpeed * speedFactor) * bannerWindAngleDeg;
            bannerClothTransform.localRotation = _bannerBaseLocalRot * Quaternion.Euler(0f, sway, 0f);
        }
    }
}
