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
        [Range(0.08f, 0.35f)]
        [SerializeField] private float eyeReliefMeters = 0.165f;

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

        private bool _keyAimActive;
        private bool _hardwareShoulderActive;
        private float _linearAim01;
        private float _easedAim01;

        public bool IsAiming => _keyAimActive || _hardwareShoulderActive;
        public float AimBlend => _easedAim01;

        private void Awake()
        {
            ResolveReferences();
            CacheReadyPoseAndSightOffset();
        }

        private void OnDisable()
        {
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
        }

        private void LateUpdate()
        {
            if (mainCameraTransform == null || playerRifleTransform == null)
                return;

            if (!_sightOffsetCached)
                CacheReadyPoseAndSightOffset();

            float target = IsAiming ? 1f : 0f;
            float step = Time.unscaledDeltaTime / Mathf.Max(0.05f, aimTransitionDurationSec);
            _linearAim01 = Mathf.MoveTowards(_linearAim01, target, step);

            // Smooth S-curve ease-in/ease-out for cinematic cheek-weld motion
            _easedAim01 = _linearAim01 * _linearAim01 * (3f - 2f * _linearAim01);

            if (redDotSight != null)
            {
                // Illuminate glowing red dot swiftly as optic rises into view
                float ledPower = Mathf.Clamp01(_easedAim01 / 0.32f);
                redDotSight.SetReticlePower(ledPower);
            }

            Vector3 readyPos = headTracker != null ? headTracker.ReadyWorldPosition : _fallbackReadyPos;
            Quaternion readyRot = headTracker != null ? headTracker.ReadyWorldRotation : _fallbackReadyRot;

            float vrEyeShiftX = (centerDominantRightEyeInVr && stereoRig != null)
                ? stereoRig.HalfVirtualIpdMeters
                : 0f;

            Vector3 adsLocalOffset = _localSightCenterInRifle + new Vector3(-vrEyeShiftX, adsVerticalOffsetMeters, -eyeReliefMeters);
            Vector3 adsWorldPos = playerRifleTransform.TransformPoint(adsLocalOffset);

            mainCameraTransform.position = Vector3.Lerp(readyPos, adsWorldPos, _easedAim01);

            bool shouldAlignRotation = alignCameraRotationWhenNoMobileSensor &&
                                       (headTracker == null || !headTracker.IsUsingMobileSensor);
            if (shouldAlignRotation)
            {
                Quaternion rifleAimRot = Quaternion.LookRotation(playerRifleTransform.forward, playerRifleTransform.up);
                mainCameraTransform.rotation = Quaternion.Slerp(readyRot, rifleAimRot, _easedAim01);
            }
            else if (headTracker != null)
            {
                mainCameraTransform.rotation = readyRot;
            }

            if (stereoRig != null)
            {
                float zoomDeg = CardboardStereoRig.IsStereoSplitActive ? vrFovZoomDeg : singleScreenFovZoomDeg;
                stereoRig.SetDynamicFovOffset(-_easedAim01 * zoomDeg);
            }
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
