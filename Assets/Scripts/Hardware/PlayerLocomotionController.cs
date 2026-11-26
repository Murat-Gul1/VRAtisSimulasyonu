using UnityEngine;
using VRAtis.UI;
using VRAtis.Weapons;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRAtis.Hardware
{
    /// <summary>
    /// Rifle-barrel-oriented 360-degree VR locomotion controller driven by the rifle-mounted
    /// HW-504 analog joystick (via ESP32-S3 UDP) with optional PC keyboard WASD/Shift test input.
    /// Forward/backward always follows the rifle barrel's horizontal projection (pitch-independent),
    /// allowing free head-look without altering walking direction.
    /// Executes after HeadTrackingCameraController (-50) and RifleOrientationController (0),
    /// and before OpticalRifleTracker (+10) and WeaponAimController (+20) for zero-lag 120 Hz VR sync.
    /// </summary>
    [DefaultExecutionOrder(5)]
    public class PlayerLocomotionController : MonoBehaviour
    {
        public enum LocomotionReferenceMode
        {
            /// <summary>
            /// Uses the physical rifle barrel's horizontal direction whenever ESP32 is connected,
            /// and falls back to the camera horizontal direction during offline PC keyboard testing.
            /// </summary>
            AutoRifleWhenConnected = 0,
            /// <summary>
            /// Always moves relative to the rifle barrel's horizontal direction.
            /// </summary>
            AlwaysRifleBarrel = 1,
            /// <summary>
            /// Moves relative to the head/camera horizontal direction.
            /// </summary>
            AlwaysHeadCamera = 2
        }

        [Header("Referanslar")]
        [SerializeField] private Esp32RifleReceiver rifleReceiver;
        [SerializeField] private Transform rifleTransform;
        [SerializeField] private HeadTrackingCameraController headTracker;
        [SerializeField] private OpticalRifleTracker opticalTracker;
        [SerializeField] private WeaponAimController weaponAimController;
        [SerializeField] private SettingsMenuController settingsMenu;

        [Header("Hareket Yönü Referansı")]
        [Tooltip("AutoRifleWhenConnected: ESP32 bağlıyken ileri yön daima tüfek namlusunun yatay baktığı yöndür (kafa serbest).")]
        [SerializeField] private LocomotionReferenceMode referenceMode = LocomotionReferenceMode.AutoRifleWhenConnected;

        [Tooltip("Ayarlar penceresi açıkken yanlışlıkla hareket etmeyi duraklatır.")]
        [SerializeField] private bool pauseWhileSettingsMenuOpen = true;

        [Header("Hız ve VR Konfor Ayarları (m/sn)")]
        [Tooltip("Normal taktik yürüyüş hızı (VR konforu için 2.2 - 3.0 m/sn önerilir).")]
        [Range(0.5f, 6.0f)]
        [SerializeField] private float walkSpeedMetersPerSec = 2.6f;

        [Tooltip("Omuzlu / Red Dot (ADS) nişan açıkken dengeli yürüyüş hızı.")]
        [Range(0.4f, 4.0f)]
        [SerializeField] private float adsWalkSpeedMetersPerSec = 1.45f;

        [Tooltip("Joystick orta butonuna (SW / GPIO 6) veya Shift'e basılı tutarken koşma hızı.")]
        [Range(1.5f, 8.0f)]
        [SerializeField] private float sprintSpeedMetersPerSec = 4.6f;

        [Tooltip("Kapalı (önerilen): Red Dot omuzluyken tam koşu yerine nişan hızı korunur.")]
        [SerializeField] private bool allowSprintWhileAiming = false;

        [Tooltip("Geri yürüyüş hız çarpanı.")]
        [Range(0.4f, 1.0f)]
        [SerializeField] private float backwardSpeedMultiplier = 0.72f;

        [Tooltip("Sağa/sola yan adım (strafe) hız çarpanı.")]
        [Range(0.5f, 1.0f)]
        [SerializeField] private float strafeSpeedMultiplier = 0.85f;

        [Tooltip("Hızlanma ivmesi (m/sn²). Yüksek değer anlık, düşük değer yumuşak kalkış verir.")]
        [Range(4f, 40f)]
        [SerializeField] private float acceleration = 14f;

        [Tooltip("Joystick bırakıldığında durma ivmesi (m/sn²). Kayma hissini önler.")]
        [Range(6f, 50f)]
        [SerializeField] private float deceleration = 18f;

        [Tooltip("Analog tepki eğrisi (1.0 = doğrusal, 1.35 = küçük itişlerde ince adım hassasiyeti).")]
        [Range(1.0f, 2.5f)]
        [SerializeField] private float analogResponseExponent = 1.35f;

        [Header("Joystick İnce Ayar (İsteğe Bağlı Ters Çevirme)")]
        [Tooltip("Yan adım (Yukarı=Sağ / Aşağı=Sol) ters gelirse buradan tek tıkla ters çevirebilirsin.")]
        [SerializeField] private bool invertStrafeX = false;
        [SerializeField] private bool invertForwardY = false;
        [SerializeField] private bool swapAxes = false;

        [Range(0f, 0.35f)]
        [SerializeField] private float extraSoftwareDeadzone = 0.0f;

        [Header("PC Klavye Testi (WASD + Shift + Home)")]
        [Tooltip("Editör veya PC derlemesinde WASD/Ok tuşları ve Shift ile hareket testini etkinleştirir.")]
        [SerializeField] private bool enableKeyboardTest = true;

        [Header("Poligon Sınırları ve Engel Çarpışması")]
        [SerializeField] private bool enableObstacleCollision = true;
        [SerializeField] private LayerMask obstacleLayers = ~0;
        [Range(0.18f, 0.6f)]
        [SerializeField] private float playerBodyRadiusMeters = 0.32f;
        [SerializeField] private bool clampToValleyBounds = true;
        [SerializeField] private float minWorldX = -340f;
        [SerializeField] private float maxWorldX = 340f;
        [SerializeField] private float minWorldZ = -70f;
        [SerializeField] private float maxWorldZ = 530f;

        [Header("Canlı Durum (Sadece Okunur)")]
        [SerializeField] private Vector2 activeMoveInput;
        [SerializeField] private float currentSpeedMetersPerSec;
        [SerializeField] private bool isSprinting;
        [SerializeField] private Vector3 activeForwardBasis = Vector3.forward;

        public Vector2 ActiveMoveInput => activeMoveInput;
        public Vector3 CurrentVelocity => _currentVelocity;
        public float CurrentSpeedMetersPerSec => currentSpeedMetersPerSec;
        public bool IsSprinting => isSprinting;
        public Vector3 StartWorldPosition => _startEyeWorldPos;

        private readonly RaycastHit[] _castHits = new RaycastHit[8];
        private Vector3 _startEyeWorldPos;
        private float _eyeHeightY;
        private Vector3 _currentVelocity;
        private Vector3 _lastValidRifleFlatForward = Vector3.forward;
        private bool _initialized;

        private void Awake()
        {
            ResolveReferences();
            CaptureInitialPose();
        }

        private void OnEnable()
        {
            ResolveReferences();
            if (!_initialized)
                CaptureInitialPose();
        }

        private void Update()
        {
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.0005f, 0.05f);

            if (enableKeyboardTest && (!Application.isMobilePlatform || Application.isEditor))
            {
#if ENABLE_INPUT_SYSTEM
                if (Keyboard.current != null && Keyboard.current.homeKey.wasPressedThisFrame)
                {
                    ResetToStartPosition();
                    return;
                }
#endif
            }

            bool menuBlocks = pauseWhileSettingsMenuOpen && settingsMenu != null && settingsMenu.IsMenuOpen;
            Vector2 rawInput = Vector2.zero;
            bool sprintHeld = false;

            if (!menuBlocks)
                ReadCombinedInput(out rawInput, out sprintHeld);

            Vector2 shapedInput = ShapeAnalogInput(rawInput);
            activeMoveInput = shapedInput;

            ResolveHorizontalBasis(out Vector3 flatForward, out Vector3 flatRight);
            activeForwardBasis = flatForward;

            bool isAiming = weaponAimController != null && weaponAimController.IsAiming;
            bool sprintAllowed = sprintHeld && (!isAiming || allowSprintWhileAiming) && shapedInput.y > 0.25f;
            isSprinting = sprintAllowed;

            float baseSpeed = isAiming
                ? adsWalkSpeedMetersPerSec
                : (sprintAllowed ? sprintSpeedMetersPerSec : walkSpeedMetersPerSec);

            Vector3 desiredVelocity = Vector3.zero;
            if (shapedInput.sqrMagnitude > 1e-6f)
            {
                float forwardScale = shapedInput.y >= 0f ? 1f : backwardSpeedMultiplier;
                float strafeScale = strafeSpeedMultiplier;

                Vector3 moveDir = flatForward * (shapedInput.y * forwardScale)
                                + flatRight * (shapedInput.x * strafeScale);
                float moveMag = moveDir.magnitude;
                if (moveMag > 1f)
                    moveDir /= moveMag;

                desiredVelocity = moveDir * baseSpeed;
            }

            float accelRate = desiredVelocity.sqrMagnitude > 1e-4f ? acceleration : deceleration;
            _currentVelocity = Vector3.MoveTowards(_currentVelocity, desiredVelocity, accelRate * dt);
            _currentVelocity.y = 0f;
            currentSpeedMetersPerSec = _currentVelocity.magnitude;

            if (_currentVelocity.sqrMagnitude <= 1e-8f)
                return;

            Vector3 currentEyePos = headTracker != null ? headTracker.ReadyWorldPosition : transform.position;
            Vector3 desiredStep = _currentVelocity * dt;
            Vector3 resolvedStep = enableObstacleCollision
                ? ResolveHorizontalObstacleStep(currentEyePos, desiredStep)
                : desiredStep;

            Vector3 nextEyePos = currentEyePos + resolvedStep;
            nextEyePos.y = _eyeHeightY;

            if (clampToValleyBounds)
            {
                nextEyePos.x = Mathf.Clamp(nextEyePos.x, minWorldX, maxWorldX);
                nextEyePos.z = Mathf.Clamp(nextEyePos.z, minWorldZ, maxWorldZ);
            }

            Vector3 actualDelta = nextEyePos - currentEyePos;
            if (actualDelta.sqrMagnitude <= 1e-12f)
            {
                _currentVelocity = Vector3.zero;
                currentSpeedMetersPerSec = 0f;
                return;
            }

            ApplyWorldTranslationToRig(nextEyePos, actualDelta);
        }

        /// <summary>
        /// Teleports the shooter and rifle cleanly back to the initial firing bench position
        /// without altering head or rifle orientation calibration.
        /// </summary>
        public void ResetToStartPosition()
        {
            if (!_initialized)
                return;

            Vector3 currentEyePos = headTracker != null ? headTracker.ReadyWorldPosition : transform.position;
            Vector3 targetEyePos = new Vector3(_startEyeWorldPos.x, _eyeHeightY, _startEyeWorldPos.z);
            Vector3 delta = targetEyePos - currentEyePos;

            _currentVelocity = Vector3.zero;
            currentSpeedMetersPerSec = 0f;
            activeMoveInput = Vector2.zero;
            isSprinting = false;

            ApplyWorldTranslationToRig(targetEyePos, delta);
        }

        private void ApplyWorldTranslationToRig(Vector3 nextEyeWorldPos, Vector3 worldDelta)
        {
            if (headTracker != null)
                headTracker.SetReadyWorldPosition(nextEyeWorldPos);
            else
                transform.position = nextEyeWorldPos;

            if (opticalTracker != null)
                opticalTracker.ApplyPlayerWorldTranslation(worldDelta);
            else if (rifleTransform != null)
                rifleTransform.position += worldDelta;

            if (weaponAimController != null)
                weaponAimController.ApplyPlayerWorldTranslation(worldDelta);
        }

        private void ReadCombinedInput(out Vector2 input, out bool sprintHeld)
        {
            input = Vector2.zero;
            sprintHeld = false;

            if (rifleReceiver != null && rifleReceiver.IsConnected && rifleReceiver.HasJoystickData)
            {
                Vector2 hw = rifleReceiver.JoystickAxis;
                if (swapAxes)
                    hw = new Vector2(hw.y, hw.x);
                if (invertStrafeX)
                    hw.x = -hw.x;
                if (invertForwardY)
                    hw.y = -hw.y;

                if (hw.sqrMagnitude > 1e-6f)
                    input = hw;

                sprintHeld = rifleReceiver.IsJoystickButtonPressed;
            }

            if (enableKeyboardTest && (!Application.isMobilePlatform || Application.isEditor))
            {
#if ENABLE_INPUT_SYSTEM
                Keyboard kb = Keyboard.current;
                if (kb != null)
                {
                    float kx = 0f;
                    float ky = 0f;
                    if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) kx += 1f;
                    if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) kx -= 1f;
                    if (kb.wKey.isPressed || kb.upArrowKey.isPressed) ky += 1f;
                    if (kb.sKey.isPressed || kb.downArrowKey.isPressed) ky -= 1f;

                    Vector2 kbVec = new Vector2(kx, ky);
                    if (kbVec.sqrMagnitude > 1f)
                        kbVec.Normalize();

                    if (kbVec.sqrMagnitude > input.sqrMagnitude)
                        input = kbVec;

                    if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed)
                        sprintHeld = true;
                }
#endif
            }
        }

        private Vector2 ShapeAnalogInput(Vector2 raw)
        {
            float mag = raw.magnitude;
            if (mag <= 1e-5f)
                return Vector2.zero;

            if (mag > 1f)
            {
                raw /= mag;
                mag = 1f;
            }

            if (extraSoftwareDeadzone > 0.001f)
            {
                if (mag <= extraSoftwareDeadzone)
                    return Vector2.zero;

                float rescaled = (mag - extraSoftwareDeadzone) / (1f - extraSoftwareDeadzone);
                raw = (raw / mag) * Mathf.Clamp01(rescaled);
                mag = raw.magnitude;
            }

            if (analogResponseExponent > 1.001f && mag > 1e-5f)
            {
                float curved = Mathf.Pow(mag, analogResponseExponent);
                raw = (raw / mag) * curved;
            }

            return raw;
        }

        /// <summary>
        /// Computes pitch-independent horizontal forward and right basis vectors.
        /// Uses the rifle barrel direction (with gimbal-safe Cross(right, up) fallback when aimed
        /// sharply down/up) so turning the head freely never alters walking direction.
        /// </summary>
        private void ResolveHorizontalBasis(out Vector3 flatForward, out Vector3 flatRight)
        {
            bool useRifle = false;
            if (referenceMode == LocomotionReferenceMode.AlwaysRifleBarrel)
                useRifle = rifleTransform != null;
            else if (referenceMode == LocomotionReferenceMode.AutoRifleWhenConnected)
                useRifle = rifleTransform != null && rifleReceiver != null && rifleReceiver.IsConnected;

            Transform source = useRifle ? rifleTransform : transform;
            if (source != null)
            {
                Vector3 fwd = source.forward;
                fwd.y = 0f;

                if (fwd.sqrMagnitude >= 0.08f)
                {
                    flatForward = fwd.normalized;
                    _lastValidRifleFlatForward = flatForward;
                }
                else
                {
                    // Pitch near +-90 deg (low-ready pointing straight at ground or straight up):
                    // Cross(right, up) stays rock-solid without yaw flip!
                    Vector3 crossFwd = Vector3.Cross(source.right, Vector3.up);
                    crossFwd.y = 0f;
                    if (crossFwd.sqrMagnitude >= 0.04f)
                    {
                        flatForward = crossFwd.normalized;
                        _lastValidRifleFlatForward = flatForward;
                    }
                    else
                    {
                        flatForward = _lastValidRifleFlatForward;
                    }
                }
            }
            else
            {
                flatForward = _lastValidRifleFlatForward;
            }

            // Unity left-handed horizontal right = Cross(Vector3.up, flatForward)
            flatRight = new Vector3(flatForward.z, 0f, -flatForward.x);
        }

        private Vector3 ResolveHorizontalObstacleStep(Vector3 eyePos, Vector3 horizontalStep)
        {
            float dist = horizontalStep.magnitude;
            if (dist <= 1e-5f)
                return Vector3.zero;

            Vector3 dir = horizontalStep / dist;
            if (!TryFindBlockingHit(eyePos, dir, dist, out RaycastHit firstHit))
                return horizontalStep;

            float skin = 0.03f;
            float safeDist = Mathf.Max(0f, firstHit.distance - skin);
            Vector3 moveSoFar = dir * safeDist;

            // Slide smoothly along blocking wall/stand normal
            Vector3 flatNormal = new Vector3(firstHit.normal.x, 0f, firstHit.normal.z);
            if (flatNormal.sqrMagnitude < 1e-4f)
                return moveSoFar;
            flatNormal.Normalize();

            Vector3 remaining = horizontalStep - moveSoFar;
            Vector3 slide = Vector3.ProjectOnPlane(remaining, flatNormal);
            slide.y = 0f;
            float slideDist = slide.magnitude;
            if (slideDist <= 1e-5f)
                return moveSoFar;

            Vector3 slideDir = slide / slideDist;
            if (!TryFindBlockingHit(eyePos + moveSoFar, slideDir, slideDist, out RaycastHit secondHit))
                return moveSoFar + slide;

            float safeSlide = Mathf.Max(0f, secondHit.distance - skin);
            return moveSoFar + slideDir * safeSlide;
        }

        private bool TryFindBlockingHit(Vector3 eyePos, Vector3 dir, float distance, out RaycastHit closestHit)
        {
            closestHit = default;
            float radius = Mathf.Clamp(playerBodyRadiusMeters, 0.15f, 0.65f);
            float groundY = 0f;
            float bottomY = groundY + radius + 0.18f;
            float topY = Mathf.Max(bottomY + 0.25f, eyePos.y - 0.12f);

            Vector3 p1 = new Vector3(eyePos.x, bottomY, eyePos.z);
            Vector3 p2 = new Vector3(eyePos.x, topY, eyePos.z);

            int hitCount = Physics.CapsuleCastNonAlloc(
                p1,
                p2,
                radius,
                dir,
                _castHits,
                distance + 0.03f,
                obstacleLayers,
                QueryTriggerInteraction.Ignore);

            bool found = false;
            float bestDist = float.MaxValue;
            for (int i = 0; i < hitCount; i++)
            {
                Collider col = _castHits[i].collider;
                if (col == null)
                    continue;

                Transform colT = col.transform;
                if (colT == transform || colT.IsChildOf(transform))
                    continue;
                if (rifleTransform != null && (colT == rifleTransform || colT.IsChildOf(rifleTransform)))
                    continue;
                if (col.gameObject.name == "Ground")
                    continue;

                // Ignore mostly-horizontal floors/slopes below 45 deg
                if (_castHits[i].normal.y > 0.72f)
                    continue;

                if (_castHits[i].distance < bestDist)
                {
                    bestDist = _castHits[i].distance;
                    closestHit = _castHits[i];
                    found = true;
                }
            }

            return found;
        }

        private void CaptureInitialPose()
        {
            _startEyeWorldPos = headTracker != null ? headTracker.ReadyWorldPosition : transform.position;
            _eyeHeightY = _startEyeWorldPos.y;

            Vector3 initialFwd = transform.forward;
            initialFwd.y = 0f;
            if (initialFwd.sqrMagnitude > 1e-4f)
                _lastValidRifleFlatForward = initialFwd.normalized;

            _initialized = true;
        }

        private void ResolveReferences()
        {
            if (headTracker == null)
                headTracker = GetComponent<HeadTrackingCameraController>() ?? FindAnyObjectByType<HeadTrackingCameraController>();

            if (rifleReceiver == null)
                rifleReceiver = FindAnyObjectByType<Esp32RifleReceiver>();

            if (rifleTransform == null)
            {
                RifleOrientationController orient = FindAnyObjectByType<RifleOrientationController>();
                if (orient != null)
                    rifleTransform = orient.transform;
            }

            if (opticalTracker == null)
                opticalTracker = rifleTransform != null
                    ? (rifleTransform.GetComponent<OpticalRifleTracker>() ?? FindAnyObjectByType<OpticalRifleTracker>())
                    : FindAnyObjectByType<OpticalRifleTracker>();

            if (weaponAimController == null)
                weaponAimController = rifleTransform != null
                    ? (rifleTransform.GetComponent<WeaponAimController>() ?? FindAnyObjectByType<WeaponAimController>())
                    : FindAnyObjectByType<WeaponAimController>();

            if (settingsMenu == null)
                settingsMenu = FindAnyObjectByType<SettingsMenuController>();
        }
    }
}
