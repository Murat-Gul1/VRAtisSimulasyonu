using UnityEngine;
using VRAtis.Hardware;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRAtis.Weapons
{
    /// <summary>
    /// Animates the camera smoothly behind the rifle's Red Dot sight (ADS / Cheek-Weld)
    /// and powers on the Red Dot reticle when aiming is triggered via keyboard (Q) or
    /// future ESP32 buttstock shoulder contact sensor.
    /// </summary>
    [DefaultExecutionOrder(20)]
    public class WeaponAimController : MonoBehaviour
    {
        [Header("Referanslar")]
        [SerializeField] private Transform playerRifleTransform;
        [SerializeField] private Transform redDotAnchor;
        [SerializeField] private RedDotSightController redDotSight;
        [SerializeField] private Transform mainCameraTransform;
        [SerializeField] private HeadTrackingCameraController headTracker;
        [SerializeField] private CardboardStereoRig stereoRig;
        [SerializeField] private OpticalRifleTracker opticalTracker;
        [SerializeField] private Esp32RifleReceiver rifleReceiver;

        [Header("Nişan Girişi (Klavye Testi & Donanım)")]
        [Tooltip("Açıkken Q tuşu ile nişan aç/kapa (Toggle); kapalıyken basılı tutma (Hold) çalışır.")]
        [SerializeField] private bool toggleAimOnKey = true;

        [Tooltip("PC'de nişan alırken kamera bakışını da yumuşakça Red Dot nişan hattına hizalar.")]
        [SerializeField] private bool alignCameraRotationWhenNoMobileSensor = true;

        [Header("Kamera Yaklaşma Animasyonu (Omuzlama / ADS)")]
        [Tooltip("Kameranın hazır duruştan Red Dot arkasına kayma süresi (saniye).")]
        [Range(0.08f, 0.8f)]
        [SerializeField] private float aimTransitionDurationSec = 0.24f;

        [Tooltip("Red Dot camının arkasından gözün duracağı mesafe (metre).")]
        [Range(0.06f, 0.35f)]
        [SerializeField] private float eyeReliefMeters = 0.115f;

        [Tooltip("Gerekirse nişan göz yüksekliği ince ayarı (metre).")]
        [Range(-0.05f, 0.05f)]
        [SerializeField] private float adsVerticalOffsetMeters = 0.0f;

        [Tooltip("VR çift göz açıkken sağ baskın gözü doğrudan Red Dot borusunun ortasına hizalar.")]
        [SerializeField] private bool centerDominantRightEyeInVr = true;

        [Header("Optik Odaklanma (Hafif FOV Yakınlaşması)")]
        [Range(0f, 20f)]
        [SerializeField] private float singleScreenFovZoomDeg = 9f;

        [Range(0f, 12f)]
        [SerializeField] private float vrFovZoomDeg = 5f;

        private Vector3 _localSightCenterInRifle;
        private bool _sightOffsetCached;
        private Vector3 _fallbackReadyPos;
        private Quaternion _fallbackReadyRot = Quaternion.identity;
        private Vector3 _initialRifleWorldPos;
        private Vector3 _activeBaseRifleWorldPos;
        private RifleOrientationController _rifleOrientationController;

        private bool _keyAimActive;
        private bool _hardwareShoulderActive;
        private bool _wasShoulderPressed;
        private float _unshoulderedTimer = 1f;
        private float _linearAim01;
        private float _easedAim01;
        private bool _isIdleAtReadyPose = true;
        private bool _wasAligningRotation;
        private float _lastAppliedReticlePower = -1f;

        public bool IsAiming => _keyAimActive || _hardwareShoulderActive;
        public float AimBlend => _easedAim01;

        private void Awake()
        {
            ResolveReferences();
            CacheReadyPoseAndSightOffset();
            ApplyReticlePowerIfChanged(0f);
        }

        private void OnEnable()
        {
            Application.onBeforeRender += OnBeforeRender;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRender;

            _lastAppliedReticlePower = -1f;
            if (redDotSight != null)
                redDotSight.SetReticlePower(0f);

            if (stereoRig != null)
                stereoRig.SetDynamicFovOffset(0f);
        }

        public void SetHardwareShoulderPressed(bool pressed)
        {
            _hardwareShoulderActive = pressed;
        }

        public void SetKeyAimActive(bool active)
        {
            _keyAimActive = active;
        }

        private void Update()
        {
            if (rifleReceiver != null)
                _hardwareShoulderActive = rifleReceiver.IsConnected && rifleReceiver.IsShoulderPressed;

#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null)
            {
                if (toggleAimOnKey)
                {
                    if (Keyboard.current.qKey.wasPressedThisFrame)
                        _keyAimActive = !_keyAimActive;
                }
                else
                {
                    _keyAimActive = Keyboard.current.qKey.isPressed;
                }
            }
#endif

            bool aimingNow = IsAiming;
            if (!aimingNow)
            {
                _unshoulderedTimer += Time.unscaledDeltaTime;
            }
            else if (!_wasShoulderPressed)
            {
                // Re-zero ESP32 IMU once at the instant Red Dot opens (after at least 0.30s unshouldered)
                // so accumulated IMU gyro drift is cleared while head rotation remains free after opening.
                if (_unshoulderedTimer >= 0.30f && _rifleOrientationController != null)
                {
                    _rifleOrientationController.RecenterToHeadForward();
                }
                _unshoulderedTimer = 0f;
            }

            _wasShoulderPressed = aimingNow;
        }

        private void LateUpdate()
        {
            if (mainCameraTransform == null || playerRifleTransform == null)
                return;

            if (!_sightOffsetCached)
                CacheReadyPoseAndSightOffset();

            Vector3 baseRifleWorldPos = (opticalTracker != null && opticalTracker.TryGetSmoothedWorldPos(out Vector3 optWorldPos))
                ? optWorldPos
                : _initialRifleWorldPos;
            _activeBaseRifleWorldPos = baseRifleWorldPos;

            bool aiming = IsAiming;

            // When not aiming and already settled in ready stance, leave camera and rifle transforms untouched
            // so HeadTrackingCameraController and OpticalRifleTracker operate cleanly.
            if (!aiming && _linearAim01 <= 0.0001f)
            {
                if (!_isIdleAtReadyPose)
                {
                    _linearAim01 = 0f;
                    _easedAim01 = 0f;
                    _isIdleAtReadyPose = true;

                    ApplyReticlePowerIfChanged(0f);

                    Vector3 restPos = headTracker != null ? headTracker.ReadyWorldPosition : _fallbackReadyPos;
                    if ((mainCameraTransform.position - restPos).sqrMagnitude > 1e-8f)
                        mainCameraTransform.position = restPos;

                    if (opticalTracker == null || !opticalTracker.TryGetSmoothedWorldPos(out _))
                    {
                        if ((playerRifleTransform.position - _initialRifleWorldPos).sqrMagnitude > 1e-8f)
                            playerRifleTransform.position = _initialRifleWorldPos;
                    }

                    if (_wasAligningRotation)
                    {
                        Quaternion restRot = headTracker != null ? headTracker.ReadyWorldRotation : _fallbackReadyRot;
                        mainCameraTransform.rotation = restRot;
                        _wasAligningRotation = false;
                    }

                    if (stereoRig != null)
                        stereoRig.SetDynamicFovOffset(0f);
                }
                return;
            }

            _isIdleAtReadyPose = false;

            float target = aiming ? 1f : 0f;
            float step = Time.unscaledDeltaTime / Mathf.Max(0.05f, aimTransitionDurationSec);
            _linearAim01 = Mathf.MoveTowards(_linearAim01, target, step);

            // Smooth S-curve ease-in/ease-out for cheek-weld ADS motion
            _easedAim01 = _linearAim01 * _linearAim01 * (3f - 2f * _linearAim01);

            // Illuminate glowing red dot swiftly as optic rises into view
            float ledPower = Mathf.Clamp01(_easedAim01 / 0.32f);
            ApplyReticlePowerIfChanged(ledPower);

            UpdateAimCameraPose();

            if (stereoRig != null)
            {
                float zoomDeg = CardboardStereoRig.IsStereoSplitActive ? vrFovZoomDeg : singleScreenFovZoomDeg;
                stereoRig.SetDynamicFovOffset(-_easedAim01 * zoomDeg);
            }
        }

        private void OnBeforeRender()
        {
            if (_isIdleAtReadyPose || _easedAim01 <= 0.0001f)
                return;

            if (mainCameraTransform == null || playerRifleTransform == null)
                return;

            UpdateAimCameraPose();
        }

        private void UpdateAimCameraPose()
        {
            Vector3 readyPos = headTracker != null ? headTracker.ReadyWorldPosition : _fallbackReadyPos;
            Quaternion readyRot = headTracker != null ? headTracker.ReadyWorldRotation : _fallbackReadyRot;

            bool shouldAlignRotation = alignCameraRotationWhenNoMobileSensor &&
                                       (headTracker == null || !headTracker.IsUsingMobileSensor);
            if (shouldAlignRotation)
            {
                Quaternion rifleAimRot = Quaternion.LookRotation(playerRifleTransform.forward, playerRifleTransform.up);
                mainCameraTransform.rotation = Quaternion.Slerp(readyRot, rifleAimRot, _easedAim01);
                _wasAligningRotation = true;
            }
            else if (_wasAligningRotation)
            {
                // Mobile sensor became active mid-aim: hand rotation back to HeadTrackingCameraController once
                if (headTracker != null)
                    mainCameraTransform.rotation = readyRot;
                _wasAligningRotation = false;
            }

            // Keep Main Camera pinned at fixed ready eye origin; move the RIFLE along the RIFLE's own orientation
            // (never mainCameraTransform.rotation!) so turning your head left/right/up/down leaves the rifle stationary.
            if ((mainCameraTransform.position - readyPos).sqrMagnitude > 1e-8f)
                mainCameraTransform.position = readyPos;

            Quaternion rifleRot = playerRifleTransform.rotation;
            float vrEyeShiftX = (centerDominantRightEyeInVr && stereoRig != null)
                ? stereoRig.HalfVirtualIpdMeters
                : 0f;

            Vector3 eyeWorldPos = readyPos + (rifleRot * Vector3.right) * vrEyeShiftX;
            Vector3 desiredSightWorldPos = eyeWorldPos
                + (rifleRot * Vector3.forward) * eyeReliefMeters
                + (rifleRot * Vector3.up) * adsVerticalOffsetMeters;

            Vector3 sightOffsetWorld = playerRifleTransform.TransformPoint(_localSightCenterInRifle) - playerRifleTransform.position;
            Vector3 adsRifleWorldPos = desiredSightWorldPos - sightOffsetWorld;
            Vector3 targetRiflePos = Vector3.Lerp(_activeBaseRifleWorldPos, adsRifleWorldPos, _easedAim01);

            if ((playerRifleTransform.position - targetRiflePos).sqrMagnitude > 1e-8f)
                playerRifleTransform.position = targetRiflePos;
        }

        private void ApplyReticlePowerIfChanged(float ledPower)
        {
            if (redDotSight == null)
                return;

            if (Mathf.Abs(_lastAppliedReticlePower - ledPower) <= 0.0005f)
                return;

            _lastAppliedReticlePower = ledPower;
            redDotSight.SetReticlePower(ledPower);
        }

        private void ResolveReferences()
        {
            if (playerRifleTransform == null)
            {
                RifleOrientationController orient = FindAnyObjectByType<RifleOrientationController>();
                if (orient != null)
                    playerRifleTransform = orient.transform;
                else
                    playerRifleTransform = transform;
            }

            if (_rifleOrientationController == null && playerRifleTransform != null)
                _rifleOrientationController = playerRifleTransform.GetComponent<RifleOrientationController>();
            if (_rifleOrientationController == null)
                _rifleOrientationController = FindAnyObjectByType<RifleOrientationController>();

            if (redDotSight == null)
                redDotSight = FindAnyObjectByType<RedDotSightController>();

            if (redDotAnchor == null && playerRifleTransform != null)
                redDotAnchor = FindDeepChild(playerRifleTransform, "RedDotAnchor");

            if (mainCameraTransform == null)
            {
                HeadTrackingCameraController tracker = FindAnyObjectByType<HeadTrackingCameraController>();
                if (tracker != null)
                {
                    headTracker = tracker;
                    mainCameraTransform = tracker.transform;
                }
                else if (Camera.main != null)
                {
                    mainCameraTransform = Camera.main.transform;
                }
            }

            if (headTracker == null && mainCameraTransform != null)
                headTracker = mainCameraTransform.GetComponent<HeadTrackingCameraController>();

            if (stereoRig == null && mainCameraTransform != null)
                stereoRig = mainCameraTransform.GetComponent<CardboardStereoRig>();

            if (opticalTracker == null && playerRifleTransform != null)
                opticalTracker = playerRifleTransform.GetComponent<OpticalRifleTracker>();
            if (opticalTracker == null)
                opticalTracker = FindAnyObjectByType<OpticalRifleTracker>();
            if (rifleReceiver == null)
                rifleReceiver = FindAnyObjectByType<Esp32RifleReceiver>();
        }

        private void CacheReadyPoseAndSightOffset()
        {
            if (mainCameraTransform != null)
            {
                _fallbackReadyPos = mainCameraTransform.position;
                _fallbackReadyRot = mainCameraTransform.rotation;
            }

            if (playerRifleTransform == null)
                return;

            _initialRifleWorldPos = playerRifleTransform.position;

            Vector3 sightWorld = Vector3.zero;
            bool found = false;

            if (redDotAnchor != null)
            {
                sightWorld = redDotAnchor.position;
                found = true;
            }
            else if (redDotSight != null)
            {
                Renderer r = redDotSight.GetComponent<Renderer>();
                if (r != null)
                {
                    sightWorld = r.bounds.center;
                    found = true;
                }
            }

            if (found)
            {
                _localSightCenterInRifle = playerRifleTransform.InverseTransformPoint(sightWorld);
                _sightOffsetCached = true;
            }
            else
            {
                // Reasonable AR-15 optic fallback relative to receiver pivot
                _localSightCenterInRifle = new Vector3(0f, 0.145f, 0.08f);
                _sightOffsetCached = true;
            }
        }

        private static Transform FindDeepChild(Transform parent, string targetName)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == targetName)
                    return child;
                Transform nested = FindDeepChild(child, targetName);
                if (nested != null)
                    return nested;
            }
            return null;
        }
    }
}
