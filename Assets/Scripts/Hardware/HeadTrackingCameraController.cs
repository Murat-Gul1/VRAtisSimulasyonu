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

        [Range(8f, 45f)]
        [SerializeField] private float minSmoothingSpeed = 18f;

        [Range(60f, 180f)]
        [SerializeField] private float maxSmoothingSpeed = 120f;

        [SerializeField] private float fastTurnSpeedDegPerSec = 55f;

        [Header("PC / Editör Fare Önizleme (Sensör Yokken)")]
        [Tooltip("PC Editörde sağ tık basılı tutarak etrafa bakmayı etkinleştirir.")]
        [SerializeField] private bool enableMouseLookInEditor = true;

        [SerializeField] private float mouseSensitivity = 0.16f;

        [Header("Sıfırlama Tuşu ve Mobil Dokunma")]
        [Tooltip("Android'de iki parmakla ekrana dokununca kafa + tüfek yönünü sıfırlar.")]
        [SerializeField] private bool recenterOnTwoFingerTap = true;

        private static readonly Quaternion AttitudeBaseRotation = Quaternion.Euler(90f, 0f, 0f);

        private Quaternion _initialForwardYaw = Quaternion.identity;
        private Quaternion _sensorYawOffset = Quaternion.identity;
        private bool _hasZeroedSensor;
        private bool _sensorReady;
        private float _pcYawOffsetDeg;
        private float _pcPitchDeg;
        private float _twoFingerCooldown;

        // 1-Euro angular velocity tracking across sensor updates
        private AttitudeSensor _activeSensor;
        private Quaternion _lastWorldSample = Quaternion.identity;
        private Quaternion _smoothedHeadRot = Quaternion.identity;
        private Vector3 _readyPosition;
        private float _elapsedSinceSample;
        private float _estimatedHeadSpeedDegPerSec;

        public bool IsUsingMobileSensor => _sensorReady;
        public Vector3 ReadyWorldPosition => _readyPosition;
        public Quaternion ReadyWorldRotation => _smoothedHeadRot;

        private void Awake()
        {
            _readyPosition = transform.position;
            _smoothedHeadRot = transform.rotation;

            if (rifleOrientationController == null)
                rifleOrientationController = FindAnyObjectByType<RifleOrientationController>();

            Vector3 flatFwd = new Vector3(transform.forward.x, 0f, transform.forward.z);
            if (flatFwd.sqrMagnitude > 1e-4f)
            {
                _initialForwardYaw = Quaternion.LookRotation(flatFwd.normalized, Vector3.up);
                _smoothedHeadRot = _initialForwardYaw;
            }
        }

        private void OnEnable()
        {
            if (Application.isMobilePlatform && lockLandscapeOnMobile)
            {
                Screen.orientation = ScreenOrientation.LandscapeLeft;
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
            }

            EnablePhoneSensor();
        }

        private void Update()
        {
            if (_twoFingerCooldown > 0f)
                _twoFingerCooldown -= Time.unscaledDeltaTime;

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
                _elapsedSinceSample += Time.unscaledDeltaTime;

                if ((autoZeroYawOnFirstReading && !_hasZeroedSensor) || wantRecenter)
                {
                    _hasZeroedSensor = true;
                    _lastWorldSample = worldHeadRot;
                    _elapsedSinceSample = 0f;
                    _estimatedHeadSpeedDegPerSec = 0f;

                    ApplySensorYawZero(worldHeadRot);
                    _smoothedHeadRot = _sensorYawOffset * worldHeadRot;
                    transform.rotation = _smoothedHeadRot;

                    if (wantRecenter && rifleOrientationController != null)
                        rifleOrientationController.RecenterToHeadForward();
                    return;
                }

                float sampleDeltaDeg = Quaternion.Angle(_lastWorldSample, worldHeadRot);
                if (sampleDeltaDeg > 0.002f && _elapsedSinceSample > 0.0005f)
                {
                    float dt = Mathf.Clamp(_elapsedSinceSample, 0.002f, 0.1f);
                    float instantSpeed = Mathf.Clamp(sampleDeltaDeg / dt, 0f, 600f);
                    _estimatedHeadSpeedDegPerSec = Mathf.Lerp(_estimatedHeadSpeedDegPerSec, instantSpeed, 0.50f);
                    _lastWorldSample = worldHeadRot;
                    _elapsedSinceSample = 0f;
                }
                else if (_elapsedSinceSample > 0.05f)
                {
                    _estimatedHeadSpeedDegPerSec = Mathf.Lerp(_estimatedHeadSpeedDegPerSec, 0f, 0.35f);
                }

                Quaternion targetRot = _sensorYawOffset * worldHeadRot;
                if (enableAdaptiveSmoothing)
                {
                    float frameGapDeg = Quaternion.Angle(_smoothedHeadRot, targetRot);
                    float speed = Mathf.Max(_estimatedHeadSpeedDegPerSec, frameGapDeg * 12f);
                    float speedFactor = Mathf.Clamp01(speed / Mathf.Max(10f, fastTurnSpeedDegPerSec));
                    float adaptiveCutoff = Mathf.Lerp(minSmoothingSpeed, maxSmoothingSpeed, speedFactor);
                    float t = 1f - Mathf.Exp(-adaptiveCutoff * Time.unscaledDeltaTime);
                    _smoothedHeadRot = Quaternion.Slerp(_smoothedHeadRot, targetRot, t);
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
                transform.rotation = _smoothedHeadRot;
                if (rifleOrientationController != null)
                    rifleOrientationController.RecenterToHeadForward();
                return;
            }

            UpdateEditorMouseLook();
        }

        /// <summary>
        /// Recenters camera horizontal yaw back toward the range forward direction
        /// and immediately aligns the ESP32 rifle to match the head direction.
        /// </summary>
        public void RecenterHeadAndRifle()
        {
            if (TryReadPhoneAttitude(out Quaternion worldHeadRot))
            {
                _hasZeroedSensor = true;
                _lastWorldSample = worldHeadRot;
                _estimatedHeadSpeedDegPerSec = 0f;
                ApplySensorYawZero(worldHeadRot);
                _smoothedHeadRot = _sensorYawOffset * worldHeadRot;
                transform.rotation = _smoothedHeadRot;
            }
            else
            {
                _pcYawOffsetDeg = 0f;
                _pcPitchDeg = 0f;
                _smoothedHeadRot = _initialForwardYaw;
                transform.rotation = _smoothedHeadRot;
            }

            if (rifleOrientationController != null)
                rifleOrientationController.RecenterToHeadForward();
        }

        private AttitudeSensor ResolvePreferredSensor()
        {
#if ENABLE_INPUT_SYSTEM
#if UNITY_EDITOR || UNITY_ANDROID
            if (preferGameRotationVector)
            {
                AndroidGameRotationVector gameRot = InputSystem.GetDevice<AndroidGameRotationVector>();
                if (gameRot != null)
                    return gameRot;
            }
#endif
            return AttitudeSensor.current;
#else
            return null;
#endif
        }

        private void EnablePhoneSensor()
        {
#if ENABLE_INPUT_SYSTEM
            AttitudeSensor sensor = ResolvePreferredSensor();
            if (sensor == null)
                return;

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
                    worldHeadRot = AttitudeBaseRotation * new Quaternion(rawQ.x, rawQ.y, -rawQ.z, -rawQ.w);
                    return true;
                }
            }
#endif
            worldHeadRot = Quaternion.identity;
            return false;
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
