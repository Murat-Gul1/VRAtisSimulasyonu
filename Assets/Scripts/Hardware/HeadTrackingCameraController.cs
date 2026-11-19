using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#if UNITY_EDITOR || UNITY_ANDROID
using UnityEngine.InputSystem.Android;
#endif
#endif

namespace VRAtis.Hardware
{
    /// <summary>
    /// Drives the Main Camera using Android's GameRotationVector (drift-free gyro+accel without
    /// magnetometer jumps) or AttitudeSensor fallback at high polling rate (200 Hz) with
    /// 1-Euro velocity-adaptive Slerp smoothing for silk-smooth slow pans and zero-lag fast turns.
    /// Executes before RifleOrientationController so pressing R recenters both camera and rifle synchronously.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class HeadTrackingCameraController : MonoBehaviour
    {
        [Header("Bağlantılar")]
        [Tooltip("Sıfırlama (R) sırasında kamerayla senkron hizalanacak tüfek kontrolcüsü.")]
        [SerializeField] private RifleOrientationController rifleOrientationController;
        [SerializeField] private OpticalRifleTracker opticalTracker;

        [Header("Mobil Kafa Takibi (200 Hz GameRotationVector + 1-Euro Filtre)")]
        [Tooltip("Android açılışında ekranı yatay Cardboard (LandscapeLeft) kilidine ve uyku modunu kapalıya alır.")]
        [SerializeField] private bool lockLandscapeOnMobile = true;

        [Tooltip("Manyetometre sıçraması olmayan GameRotationVector (Jiroskop+İvmeölçer) sensörünü önceliklendirir.")]
        [SerializeField] private bool preferGameRotationVector = true;

        [Tooltip("Sensör örnekleme frekansı (Hz). Varsayılan ~50 Hz takılmasını önlemek için 200 Hz istenir.")]
        [Range(60f, 400f)]
        [SerializeField] private float requestedSensorRateHz = 200f;

        [Tooltip("İlk geçerli sensör verisi geldiğinde bakış yönünü (Yaw) otomatik poligona sıfırlar.")]
        [SerializeField] private bool autoZeroYawOnFirstReading = true;

        [Header("Hıza Duyarlı Akıllı Yumuşatma (Yavaş Çevirme Takılmasını Önler)")]
        [Tooltip("Yavaş kafa hareketlerinde mikro sensör basamaklarını ütüler, hızlı dönüşte gecikmeyi sıfıra yaklaştırır.")]
        [SerializeField] private bool enableAdaptiveSmoothing = true;

        [Range(8f, 60f)]
        [SerializeField] private float minSmoothingSpeed = 32f;

        [Range(60f, 180f)]
        [SerializeField] private float maxSmoothingSpeed = 125f;

        [SerializeField] private float fastTurnSpeedDegPerSec = 45f;

        [Header("PC / Editör Fare Önizleme (Sensör Yokken)")]
        [Tooltip("PC Editörde sağ tık basılı tutarak etrafa bakmayı etkinleştirir.")]
        [SerializeField] private bool enableMouseLookInEditor = true;

        [SerializeField] private float mouseSensitivity = 0.16f;

        [Header("Sıfırlama Tuşu ve Mobil Dokunma")]
        [Tooltip("Android'de iki parmakla ekrana dokununca kafa + tüfek yönünü sıfırlar.")]
        [SerializeField] private bool recenterOnTwoFingerTap = true;

        private static readonly Quaternion AttitudeBaseRotation = Quaternion.Euler(90f, 0f, 0f);
        private static readonly Quaternion Roll180Rotation = Quaternion.Euler(0f, 0f, 180f);

        public static bool IsLandscapeRightActive { get; private set; }

        private Quaternion _initialForwardYaw = Quaternion.identity;
        private Quaternion _sensorYawOffset = Quaternion.identity;
        private bool _hasZeroedSensor;
        private bool _sensorReady;
        private float _pcYawOffsetDeg;
        private float _pcPitchDeg;
        private float _twoFingerCooldown;
        private ScreenOrientation _activeLandscapeOrientation = ScreenOrientation.LandscapeLeft;
        private ScreenOrientation _lastObservedScreenOrientation = ScreenOrientation.Unknown;
        private float _upsideDownHoldTimer;
        private float _orientationSwitchCooldown;
        private bool _isRoll180Compensated;

        // Gyroscope + Attitude velocity-predicted filter state across 120Hz frames
#if ENABLE_INPUT_SYSTEM
        private AttitudeSensor _activeSensor;
        private UnityEngine.InputSystem.Gyroscope _activeGyro;
#endif
        private bool _gyroHasValidReadings;
        private Quaternion _lastWorldSample = Quaternion.identity;
        private Quaternion _lastTargetSample = Quaternion.identity;
        private Quaternion _smoothedHeadRot = Quaternion.identity;
        private Vector3 _readyPosition;
        private Vector3 _attitudeAngularVelocityVec;
        private Vector3 _smoothedAngularVelocityVec;
        private float _elapsedSinceSample;
        private float _estimatedHeadSpeedDegPerSec;
        private float _smoothedFrameDt = 1f / 120f;
        private float _smoothedCutoff = 32f;

        public bool IsUsingMobileSensor => _sensorReady;
        public bool IsRoll180Compensated => _isRoll180Compensated;
        public float EstimatedHeadSpeedDegPerSec => _estimatedHeadSpeedDegPerSec;
        public Vector3 ReadyWorldPosition => _readyPosition;
        public Quaternion ReadyWorldRotation => _smoothedHeadRot;

        private void Awake()
        {
            _readyPosition = transform.position;
            _smoothedHeadRot = transform.rotation;

            // Upgrade legacy serialized filter values that caused slow-turn phase stepping on 120Hz VR
            if (minSmoothingSpeed < 28f)
                minSmoothingSpeed = 32f;
            if (fastTurnSpeedDegPerSec > 48f)
                fastTurnSpeedDegPerSec = 45f;

            _smoothedCutoff = minSmoothingSpeed;

            if (rifleOrientationController == null)
                rifleOrientationController = FindAnyObjectByType<RifleOrientationController>();
            if (opticalTracker == null)
                opticalTracker = FindAnyObjectByType<OpticalRifleTracker>();

            Vector3 flatFwd = new Vector3(transform.forward.x, 0f, transform.forward.z);
            if (flatFwd.sqrMagnitude > 1e-4f)
            {
                _initialForwardYaw = Quaternion.LookRotation(flatFwd.normalized, Vector3.up);
                _smoothedHeadRot = _initialForwardYaw;
                _lastTargetSample = _initialForwardYaw;
            }
        }

        private void OnEnable()
        {
            if (Application.isMobilePlatform)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 120;

                if (lockLandscapeOnMobile)
                {
                    Screen.autorotateToPortrait = false;
                    Screen.autorotateToPortraitUpsideDown = false;
                    Screen.autorotateToLandscapeLeft = true;
                    Screen.autorotateToLandscapeRight = true;
                    _activeLandscapeOrientation = Screen.orientation == ScreenOrientation.LandscapeRight
                        ? ScreenOrientation.LandscapeRight
                        : ScreenOrientation.LandscapeLeft;
                    Screen.orientation = _activeLandscapeOrientation;
                    _lastObservedScreenOrientation = Screen.orientation;
                    IsLandscapeRightActive = _activeLandscapeOrientation == ScreenOrientation.LandscapeRight;
                    Screen.sleepTimeout = SleepTimeout.NeverSleep;
                }
            }

#if ENABLE_INPUT_SYSTEM
            if (InputSystem.settings != null &&
                InputSystem.settings.updateMode == InputSettings.UpdateMode.ProcessEventsInFixedUpdate)
            {
                InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsInDynamicUpdate;
            }
#endif

            EnablePhoneSensor();
            Application.onBeforeRender += OnBeforeRenderHeadTrack;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRenderHeadTrack;
        }

        private void Update()
        {
            float rawDt = Mathf.Clamp(Time.unscaledDeltaTime, 0.001f, 0.05f);
            _smoothedFrameDt = Mathf.Lerp(_smoothedFrameDt, rawDt, 0.25f);

            if (_twoFingerCooldown > 0f)
                _twoFingerCooldown -= rawDt;
            if (_orientationSwitchCooldown > 0f)
                _orientationSwitchCooldown -= rawDt;

            bool wantRecenter = false;
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
                wantRecenter = true;

            if (recenterOnTwoFingerTap && Touchscreen.current != null && _twoFingerCooldown <= 0f)
            {
                int activeTouches = 0;
                var touches = Touchscreen.current.touches;
                for (int i = 0; i < touches.Count; i++)
                {
                    if (touches[i].press.isPressed)
                        activeTouches++;
                }

                if (activeTouches >= 2)
                {
                    wantRecenter = true;
                    _twoFingerCooldown = 0.8f;
                }
            }
#endif

            if (TryReadPhoneAttitude(out Quaternion worldHeadRot))
            {
                _sensorReady = true;
                _elapsedSinceSample += rawDt;

                UpdateLandscapeOrientationIfInverted(ref worldHeadRot, rawDt, out bool orientationJustChanged);

                if ((autoZeroYawOnFirstReading && !_hasZeroedSensor) || wantRecenter || orientationJustChanged)
                {
                    _hasZeroedSensor = true;
                    _lastWorldSample = worldHeadRot;
                    _elapsedSinceSample = 0f;
                    _estimatedHeadSpeedDegPerSec = 0f;
                    _attitudeAngularVelocityVec = Vector3.zero;
                    _smoothedAngularVelocityVec = Vector3.zero;

                    ApplySensorYawZero(worldHeadRot);
                    _smoothedHeadRot = _sensorYawOffset * worldHeadRot;
                    _lastTargetSample = _smoothedHeadRot;
                    _smoothedCutoff = Mathf.Max(28f, minSmoothingSpeed);
                    transform.rotation = _smoothedHeadRot;

                    if ((wantRecenter || orientationJustChanged) && rifleOrientationController != null)
                        rifleOrientationController.RecenterToHeadForward();
                    if (wantRecenter && opticalTracker != null)
                        opticalTracker.RecenterOpticalZero();
                    return;
                }

                Quaternion targetRot = _sensorYawOffset * worldHeadRot;

                // Use atan2-based exact quaternion delta to avoid Unity Quaternion.Angle's 0.162 deg deadzone
                ComputeExactAngleAxis(_lastTargetSample, targetRot, out float sampleDeltaDeg, out Vector3 sampleAxis);
                if (sampleDeltaDeg > 0.0004f && _elapsedSinceSample > 0.0005f)
                {
                    float sampleDt = Mathf.Clamp(_elapsedSinceSample, 0.0025f, 0.1f);
                    float instantSpeed = Mathf.Clamp(sampleDeltaDeg / sampleDt, 0f, 720f);
                    Vector3 instantVel = sampleAxis * instantSpeed;
                    _attitudeAngularVelocityVec = Vector3.Lerp(_attitudeAngularVelocityVec, instantVel, 0.55f);
                    _lastWorldSample = worldHeadRot;
                    _lastTargetSample = targetRot;
                    _elapsedSinceSample = 0f;
                }
                else if (_elapsedSinceSample > 0.015f)
                {
                    float decay = Mathf.Exp(-(_elapsedSinceSample - 0.015f) * 45f);
                    _attitudeAngularVelocityVec *= decay;
                }

                if (TryReadPhoneGyroWorld(targetRot, out Vector3 gyroWorldVel))
                {
                    if (gyroWorldVel.sqrMagnitude < 0.12f)
                        gyroWorldVel = Vector3.zero;

                    Vector3 fusedVel = Vector3.Lerp(_attitudeAngularVelocityVec, gyroWorldVel, 0.65f);
                    _smoothedAngularVelocityVec = Vector3.Lerp(_smoothedAngularVelocityVec, fusedVel, 0.60f);
                }
                else
                {
                    _smoothedAngularVelocityVec = _attitudeAngularVelocityVec;
                }

                _estimatedHeadSpeedDegPerSec = _smoothedAngularVelocityVec.magnitude;

                if (enableAdaptiveSmoothing)
                {
                    // 1) Dead-reckon targetRot across frames where AttitudeSensor has not ticked yet (100Hz vs 120Hz beat)
                    Quaternion effectiveTargetRot = targetRot;
                    if (_elapsedSinceSample > 0.0005f && _estimatedHeadSpeedDegPerSec > 0.6f)
                    {
                        float extrapSec = Mathf.Min(_elapsedSinceSample, 0.016f);
                        float horizonDecay = Mathf.Exp(-extrapSec * 25f);
                        float extrapAngleDeg = _estimatedHeadSpeedDegPerSec * extrapSec * horizonDecay;
                        Vector3 velAxis = _smoothedAngularVelocityVec / _estimatedHeadSpeedDegPerSec;
                        effectiveTargetRot = Quaternion.AngleAxis(extrapAngleDeg, velAxis) * targetRot;
                    }

                    // 2) Advance current smoothed head rotation by continuous angular velocity feedforward
                    Quaternion predictedHeadRot = _smoothedHeadRot;
                    if (_estimatedHeadSpeedDegPerSec > 0.6f)
                    {
                        float motionGate = Mathf.SmoothStep(0f, 0.85f, (_estimatedHeadSpeedDegPerSec - 0.6f) / 3.0f);
                        float stepAngleDeg = _estimatedHeadSpeedDegPerSec * _smoothedFrameDt * motionGate;
                        Vector3 velAxis = _smoothedAngularVelocityVec / _estimatedHeadSpeedDegPerSec;
                        predictedHeadRot = Quaternion.AngleAxis(stepAngleDeg, velAxis) * _smoothedHeadRot;
                    }

                    // 3) Smoothly filtered adaptive cutoff to eliminate lag-buffer expansion/compression stepping
                    ComputeExactAngleAxis(predictedHeadRot, effectiveTargetRot, out float residualGapDeg, out _);
                    float effectiveMinCutoff = Mathf.Max(28f, minSmoothingSpeed);
                    float speed = Mathf.Max(_estimatedHeadSpeedDegPerSec, residualGapDeg * 18f);
                    float speedFactor = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(speed / Mathf.Max(10f, fastTurnSpeedDegPerSec)));
                    float desiredCutoff = Mathf.Lerp(effectiveMinCutoff, maxSmoothingSpeed, speedFactor);
                    _smoothedCutoff = Mathf.Lerp(_smoothedCutoff, desiredCutoff, 1f - Mathf.Exp(-20f * rawDt));

                    float t = 1f - Mathf.Exp(-_smoothedCutoff * rawDt);
                    _smoothedHeadRot = Quaternion.Slerp(predictedHeadRot, effectiveTargetRot, t);
                    transform.rotation = _smoothedHeadRot;
                }
                else
                {
                    _smoothedHeadRot = targetRot;
                    transform.rotation = targetRot;
                }

                return;
            }

            _sensorReady = false;

            if (wantRecenter)
            {
                _pcYawOffsetDeg = 0f;
                _pcPitchDeg = 0f;
                _smoothedHeadRot = _initialForwardYaw;
                _lastTargetSample = _initialForwardYaw;
                transform.rotation = _smoothedHeadRot;
                if (rifleOrientationController != null)
                    rifleOrientationController.RecenterToHeadForward();
                return;
            }

            UpdateEditorMouseLook();
        }

        private void OnBeforeRenderHeadTrack()
        {
            if (!_sensorReady || !_hasZeroedSensor)
                return;

            if (TryReadPhoneAttitude(out Quaternion worldHeadRot))
            {
                if (_isRoll180Compensated)
                    worldHeadRot = worldHeadRot * Roll180Rotation;

                Quaternion targetRot = _sensorYawOffset * worldHeadRot;
                ComputeExactAngleAxis(_lastTargetSample, targetRot, out float lateDeltaDeg, out _);
                if (lateDeltaDeg > 0.0004f && lateDeltaDeg < 25f)
                {
                    _lastWorldSample = worldHeadRot;
                    _lastTargetSample = targetRot;
                    _elapsedSinceSample = 0f;

                    if (enableAdaptiveSmoothing)
                    {
                        float lateBlend = Mathf.Clamp01(_smoothedCutoff * 0.004f);
                        _smoothedHeadRot = Quaternion.Slerp(_smoothedHeadRot, targetRot, lateBlend);
                    }
                    else
                    {
                        _smoothedHeadRot = targetRot;
                    }
                }
            }

            transform.rotation = _smoothedHeadRot;
        }

        /// <summary>
        /// Recenters only the camera's horizontal yaw toward the range forward direction (+Z)
        /// without restarting the optical countdown. Used when guided optical hold completes.
        /// </summary>
        public void RecenterHeadYawOnly()
        {
            if (TryReadPhoneAttitude(out Quaternion worldHeadRot))
            {
                UpdateLandscapeOrientationIfInverted(ref worldHeadRot, 0f, out _);
                _hasZeroedSensor = true;
                _lastWorldSample = worldHeadRot;
                _elapsedSinceSample = 0f;
                _estimatedHeadSpeedDegPerSec = 0f;
                _attitudeAngularVelocityVec = Vector3.zero;
                _smoothedAngularVelocityVec = Vector3.zero;
                ApplySensorYawZero(worldHeadRot);
                _smoothedHeadRot = _sensorYawOffset * worldHeadRot;
                _lastTargetSample = _smoothedHeadRot;
                transform.rotation = _smoothedHeadRot;
            }
            else
            {
                _pcYawOffsetDeg = 0f;
                _pcPitchDeg = 0f;
                _smoothedHeadRot = _initialForwardYaw;
                _lastTargetSample = _initialForwardYaw;
                transform.rotation = _smoothedHeadRot;
            }
        }

        /// <summary>
        /// Recenters camera horizontal yaw back toward the range forward direction
        /// and immediately aligns the ESP32 rifle to match the head direction.
        /// </summary>
        public void RecenterHeadAndRifle()
        {
            RecenterHeadYawOnly();

            if (rifleOrientationController != null)
                rifleOrientationController.RecenterToHeadForward();
            if (opticalTracker != null)
                opticalTracker.RecenterOpticalZero();
        }

        private void UpdateLandscapeOrientationIfInverted(ref Quaternion worldHeadRot, float dt, out bool orientationJustChanged)
        {
            orientationJustChanged = false;

            if (Application.isMobilePlatform && lockLandscapeOnMobile)
            {
                ScreenOrientation currentScreen = Screen.orientation;
                if ((currentScreen == ScreenOrientation.LandscapeLeft || currentScreen == ScreenOrientation.LandscapeRight) &&
                    currentScreen != _lastObservedScreenOrientation)
                {
                    if (_lastObservedScreenOrientation != ScreenOrientation.Unknown)
                        orientationJustChanged = true;
                    _lastObservedScreenOrientation = currentScreen;
                    _activeLandscapeOrientation = currentScreen;
                }
            }

            Vector3 camUp = worldHeadRot * Vector3.up;
            Vector3 camFwd = worldHeadRot * Vector3.forward;
            bool isUpsideDownRoll = camUp.y < -0.45f && Mathf.Abs(camFwd.y) < 0.75f;

            if (Application.isMobilePlatform && lockLandscapeOnMobile)
            {
                if (isUpsideDownRoll)
                {
                    _upsideDownHoldTimer += dt;
                    if (!_hasZeroedSensor || (_upsideDownHoldTimer >= 0.22f && _orientationSwitchCooldown <= 0f))
                    {
                        _upsideDownHoldTimer = 0f;
                        _orientationSwitchCooldown = 0.85f;
                        _activeLandscapeOrientation = _activeLandscapeOrientation == ScreenOrientation.LandscapeRight
                            ? ScreenOrientation.LandscapeLeft
                            : ScreenOrientation.LandscapeRight;
                        Screen.orientation = _activeLandscapeOrientation;
                        orientationJustChanged = true;
                    }
                }
                else
                {
                    _upsideDownHoldTimer = 0f;
                }
            }

            if (camUp.y < -0.35f && Mathf.Abs(camFwd.y) < 0.75f)
            {
                _isRoll180Compensated = true;
                worldHeadRot = worldHeadRot * Roll180Rotation;
            }
            else
            {
                _isRoll180Compensated = false;
            }

            IsLandscapeRightActive = _activeLandscapeOrientation == ScreenOrientation.LandscapeRight
                || Screen.orientation == ScreenOrientation.LandscapeRight
                || _isRoll180Compensated;
        }

#if ENABLE_INPUT_SYSTEM
        private AttitudeSensor ResolvePreferredSensor()
        {
#if UNITY_EDITOR || UNITY_ANDROID
            if (preferGameRotationVector)
            {
                AndroidGameRotationVector gameRot = InputSystem.GetDevice<AndroidGameRotationVector>();
                if (gameRot != null)
                    return gameRot;
            }
#endif
            return AttitudeSensor.current;
        }

        private UnityEngine.InputSystem.Gyroscope ResolveGyroscope()
        {
#if UNITY_EDITOR || UNITY_ANDROID
            AndroidGyroscope androidGyro = InputSystem.GetDevice<AndroidGyroscope>();
            if (androidGyro != null)
                return androidGyro;
#endif
            return UnityEngine.InputSystem.Gyroscope.current;
        }
#endif

        private void EnablePhoneSensor()
        {
#if ENABLE_INPUT_SYSTEM
            try
            {
                InputSystem.pollingFrequency = Mathf.Max(120f, requestedSensorRateHz);
            }
            catch
            {
                // Ignore if platform backend does not support custom pollingFrequency
            }

            AttitudeSensor sensor = ResolvePreferredSensor();
            if (sensor != null)
            {
                _activeSensor = sensor;
                if (!sensor.enabled)
                    InputSystem.EnableDevice(sensor);

                try
                {
                    sensor.samplingFrequency = requestedSensorRateHz;
                }
                catch
                {
                    // Some devices lock sampling rate or don't support SetSamplingFrequencyCommand
                }
            }

            UnityEngine.InputSystem.Gyroscope gyro = ResolveGyroscope();
            if (gyro != null)
            {
                _activeGyro = gyro;
                if (!gyro.enabled)
                    InputSystem.EnableDevice(gyro);

                try
                {
                    gyro.samplingFrequency = requestedSensorRateHz;
                }
                catch
                {
                    // Ignore if gyroscope sampling frequency is locked by HAL
                }
            }
#endif
        }

        private bool TryReadPhoneAttitude(out Quaternion worldHeadRot)
        {
#if ENABLE_INPUT_SYSTEM
            AttitudeSensor sensor = _activeSensor ?? ResolvePreferredSensor();
            if (sensor != null)
            {
                if (_activeSensor != sensor || !sensor.enabled)
                {
                    _activeSensor = sensor;
                    if (!sensor.enabled)
                        InputSystem.EnableDevice(sensor);
                    try { sensor.samplingFrequency = requestedSensorRateHz; } catch { }
                }

                Quaternion rawQ = sensor.attitude.ReadValue();
                float normSq = rawQ.x * rawQ.x + rawQ.y * rawQ.y + rawQ.z * rawQ.z + rawQ.w * rawQ.w;
                if (normSq > 1e-4f)
                {
                    float invNorm = 1f / Mathf.Sqrt(normSq);
                    worldHeadRot = AttitudeBaseRotation * new Quaternion(
                        rawQ.x * invNorm,
                        rawQ.y * invNorm,
                        -rawQ.z * invNorm,
                        -rawQ.w * invNorm);
                    return true;
                }
            }
#endif
            worldHeadRot = Quaternion.identity;
            return false;
        }

        private bool TryReadPhoneGyroWorld(Quaternion currentTargetRot, out Vector3 worldAngularVelDegPerSec)
        {
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Gyroscope gyro = _activeGyro ?? ResolveGyroscope();
            if (gyro != null)
            {
                if (_activeGyro != gyro || !gyro.enabled)
                {
                    _activeGyro = gyro;
                    if (!gyro.enabled)
                        InputSystem.EnableDevice(gyro);
                    try { gyro.samplingFrequency = requestedSensorRateHz; } catch { }
                }

                Vector3 rawRadPerSec = gyro.angularVelocity.ReadValue();
                float sqrMag = rawRadPerSec.sqrMagnitude;
                if (sqrMag > 1e-7f && sqrMag < 1000f)
                    _gyroHasValidReadings = true;

                if (_gyroHasValidReadings && sqrMag < 1000f)
                {
                    float signXY = _isRoll180Compensated ? 1f : -1f;
                    Vector3 localDegPerSec = new Vector3(signXY * rawRadPerSec.x, signXY * rawRadPerSec.y, rawRadPerSec.z) * Mathf.Rad2Deg;
                    worldAngularVelDegPerSec = Vector3.ClampMagnitude(currentTargetRot * localDegPerSec, 720f);
                    return true;
                }
            }
#endif
            worldAngularVelDegPerSec = Vector3.zero;
            return false;
        }

        private static void ComputeExactAngleAxis(Quaternion from, Quaternion to, out float angleDeg, out Vector3 axis)
        {
            Quaternion invFrom = new Quaternion(-from.x, -from.y, -from.z, from.w);
            Quaternion delta = new Quaternion(
                to.w * invFrom.x + to.x * invFrom.w + to.y * invFrom.z - to.z * invFrom.y,
                to.w * invFrom.y - to.x * invFrom.z + to.y * invFrom.w + to.z * invFrom.x,
                to.w * invFrom.z + to.x * invFrom.y - to.y * invFrom.x + to.z * invFrom.w,
                to.w * invFrom.w - to.x * invFrom.x - to.y * invFrom.y - to.z * invFrom.z);

            if (delta.w < 0f)
            {
                delta.x = -delta.x;
                delta.y = -delta.y;
                delta.z = -delta.z;
                delta.w = -delta.w;
            }

            float sinHalfSq = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
            if (sinHalfSq > 1e-12f)
            {
                float sinHalf = Mathf.Sqrt(sinHalfSq);
                angleDeg = 2f * Mathf.Atan2(sinHalf, Mathf.Clamp(delta.w, 0f, 1f)) * Mathf.Rad2Deg;
                float invSin = 1f / sinHalf;
                axis = new Vector3(delta.x * invSin, delta.y * invSin, delta.z * invSin);
            }
            else
            {
                angleDeg = 0f;
                axis = Vector3.up;
            }
        }

        private void ApplySensorYawZero(Quaternion worldHeadRot)
        {
            Vector3 sensorFwd = worldHeadRot * Vector3.forward;
            Vector3 flatSensorFwd = new Vector3(sensorFwd.x, 0f, sensorFwd.z);
            if (flatSensorFwd.sqrMagnitude < 1e-4f)
            {
                _sensorYawOffset = _initialForwardYaw;
                return;
            }

            Vector3 targetFwd = _initialForwardYaw * Vector3.forward;
            float deltaYaw = Vector3.SignedAngle(flatSensorFwd.normalized, targetFwd, Vector3.up);
            _sensorYawOffset = Quaternion.AngleAxis(deltaYaw, Vector3.up);
        }

        private void UpdateEditorMouseLook()
        {
#if ENABLE_INPUT_SYSTEM
            if (!enableMouseLookInEditor || Mouse.current == null)
                return;

            if (Mouse.current.rightButton.isPressed)
            {
                Vector2 delta = Mouse.current.delta.ReadValue();
                _pcYawOffsetDeg += delta.x * mouseSensitivity;
                _pcPitchDeg = Mathf.Clamp(_pcPitchDeg - delta.y * mouseSensitivity, -80f, 80f);
                _smoothedHeadRot = _initialForwardYaw * Quaternion.Euler(_pcPitchDeg, _pcYawOffsetDeg, 0f);
                transform.rotation = _smoothedHeadRot;
            }
#endif
        }
    }
}
