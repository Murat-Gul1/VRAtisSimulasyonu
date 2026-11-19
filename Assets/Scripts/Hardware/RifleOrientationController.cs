using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRAtis.Hardware
{
    /// <summary>
    /// Converts the ESP32 Madgwick quaternion into Unity's left-handed coordinate space,
    /// bridges brief 2.4 GHz Wi-Fi packet gaps using short-horizon angular velocity dead-reckoning,
    /// and applies a 1-Euro style velocity-adaptive filter for stutter-free, zero-lag tracking.
    /// </summary>
    public class RifleOrientationController : MonoBehaviour
    {
        public enum SensorMountPreset
        {
            /// <summary>
            /// PCB tüfeğin arkasında dikey, ön yüzü atıcıya bakıyor:
            /// Sensör +X = Sağ, +Y = Yukarı (Ray), -Z = İleri (Namlu).
            /// </summary>
            VerticalBackFacingShooter,
            /// <summary>
            /// PCB yatay düz: Sensör +X = Sağ, +Y = İleri (Namlu), +Z = Yukarı.
            /// </summary>
            StandardMpuFlatForward,
            CustomAxisMapping
        }

        [Header("Bağlantılar")]
        [SerializeField] private Esp32RifleReceiver receiver;
        [Tooltip("Yaw sıfırlama sırasında referans alınacak kamera (boş bırakılırsa Main Camera kullanılır).")]
        [SerializeField] private Transform headCameraTransform;

        [Header("Sensör Eksen Eşlemesi (MPU-9250 -> Unity)")]
        [Tooltip("PCB montaj yönünü seçin. Picatinny ray üstü yatay (+X=Sağ, +Y=Namlu, +Z=Tavan) montaj için StandardMpuFlatForward kullanın.")]
        [SerializeField] private SensorMountPreset mountPreset = SensorMountPreset.StandardMpuFlatForward;

        [Tooltip("Hangi sensör ekseni Unity sağ (X), yukarı (Y), ileri (Z) eksenine karşılık gelir.")]
        [SerializeField] private Vector3Int unityAxisFromSensor = new Vector3Int(1, 3, 2);
        [SerializeField] private bool invertPitch;
        [SerializeField] private bool invertYaw;
        [SerializeField] private bool invertRoll;

        [Header("İnce Montaj Ofseti (Derece)")]
        [SerializeField] private Vector3 mountOffsetEuler;

        [Header("Nişan Sıfırlama (Yaw Zeroing)")]
        [SerializeField] private bool autoZeroYawOnFirstPacket = true;

        [Header("Anlık Tepki (Sıfır Gecikme Modu)")]
        [Tooltip("Açıkken tüm yumuşatma ve tahmin filtreleri kapatılır; tüfek 200 Hz ESP32 verisine 0 ms gecikmeyle birebir anlık tepki verir.")]
        [SerializeField] private bool directZeroLagMode = true;

        [Header("Wi-Fi Paket Boşluğu Tahminleyici (Yalnız Hızlı Hareket Koruması)")]
        [Tooltip("Yavaş nişanda 0 ms ham veri korunur; yalnız hızlı savurmada (>18°/sn) Wi-Fi paketi >6 ms gecikirse mikro atlama/takılmayı giderir.")]
        [SerializeField] private bool enableFastTurnGapBridge = true;

        [Tooltip("Hızlı harekette paket boşluğu telafisinin devreye girme açısal hız eşiği (derece/sn).")]
        [Range(8f, 45f)]
        [SerializeField] private float fastTurnGapBridgeMinSpeedDegPerSec = 18f;

        [Tooltip("Eski tam tahminleyici bayrağı (directZeroLagMode kapalıyken geçerlidir).")]
        [SerializeField] private bool enableWifiGapPrediction = false;

        [Tooltip("Maksimum ileri tahmin süresi (ms).")]
        [Range(0f, 90f)]
        [SerializeField] private float maxPredictionMs = 20f;

        [Header("Hıza Duyarlı Akıllı Yumuşatma (1-Euro Adaptive Slerp)")]
        [Tooltip("Açılırsa hareketi yumuşatır; ani/birebir nişan tepkisi için kapalı tutun.")]
        [SerializeField] private bool enableAdaptiveSmoothing = false;

        [Tooltip("Yavaş hareketlerde taban yumuşatma hızı (yüksek = daha keskin, düşük = daha pürüzsüz).")]
        [Range(10f, 60f)]
        [SerializeField] private float minSmoothingSpeed = 32f;

        [Tooltip("Hızlı dönüşlerde maksimum yumuşatma hızı (anında tepki için yüksek tutun).")]
        [Range(40f, 150f)]
        [SerializeField] private float maxSmoothingSpeed = 95f;

        [Tooltip("Tam hıza geçiş açısal hızı (derece/sn).")]
        [SerializeField] private float fastTurnSpeedDegPerSec = 90f;

        private static readonly Quaternion VerticalMountRotation = Quaternion.Euler(90f, 0f, 0f);

        private Quaternion _yawCalibrationOffset = Quaternion.identity;
        private Quaternion _localPitchCalibrationOffset = Quaternion.identity;
        private bool _hasAutoZeroed;
        private HeadTrackingCameraController _headTracker;

        // Cached mount offset quaternion to avoid per-frame Euler conversion
        private Vector3 _lastMountOffsetEuler;
        private Quaternion _cachedMountOffsetQuat = Quaternion.identity;

        // Dead-reckoning state
        private uint _lastPacketSeq;
        private Quaternion _lastCalibratedPacketRot = Quaternion.identity;
        private float _elapsedSinceLastDistinctPacket;
        private Vector3 _estimatedAngularAxis = Vector3.up;
        private float _estimatedAngularSpeedDegPerSec;

        public float EstimatedAngularSpeedDegPerSec => _estimatedAngularSpeedDegPerSec;

        private void Awake()
        {
            // Picatinny ray üstü yatay montaja geçildiği için eski sahne kaydındaki dikey (90° aşağı bakan)
            // VerticalBackFacingShooter ayarını otomatik olarak StandardMpuFlatForward'a yükselt.
            if (mountPreset == SensorMountPreset.VerticalBackFacingShooter)
                mountPreset = SensorMountPreset.StandardMpuFlatForward;

            if (receiver == null)
                receiver = FindAnyObjectByType<Esp32RifleReceiver>();

            _headTracker = FindAnyObjectByType<HeadTrackingCameraController>();

            if (headCameraTransform == null)
            {
                if (_headTracker != null)
                    headCameraTransform = _headTracker.transform;
                else if (Camera.main != null)
                    headCameraTransform = Camera.main.transform;
            }

            _lastMountOffsetEuler = mountOffsetEuler;
            _cachedMountOffsetQuat = mountOffsetEuler.sqrMagnitude > 1e-5f
                ? Quaternion.Euler(mountOffsetEuler)
                : Quaternion.identity;
        }

        private void OnValidate()
        {
            if (mountPreset == SensorMountPreset.VerticalBackFacingShooter)
                mountPreset = SensorMountPreset.StandardMpuFlatForward;
        }

        private void OnEnable()
        {
            Application.onBeforeRender += OnBeforeRenderRifle;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRenderRifle;
        }

        private void OnBeforeRenderRifle()
        {
            if (!directZeroLagMode || !_hasAutoZeroed || receiver == null || !receiver.IsConnected)
                return;

            Quaternion converted = ConvertImuToUnity(receiver.RawImuQuaternion);
            Quaternion latest = ApplyCalibrationOffsets(converted);
            latest = ApplyFastTurnWifiGapBridge(latest);
            if (1f - Mathf.Abs(Quaternion.Dot(transform.rotation, latest)) > 1e-7f)
            {
                transform.rotation = latest;
            }
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (_headTracker == null && Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            {
                RecenterToHeadForward();
            }
#endif

            if (receiver == null || !receiver.IsConnected)
                return;

            _elapsedSinceLastDistinctPacket += Time.unscaledDeltaTime;

            Quaternion converted = ConvertImuToUnity(receiver.RawImuQuaternion);

            if (autoZeroYawOnFirstPacket && !_hasAutoZeroed)
            {
                _hasAutoZeroed = true;
                ApplyYawCalibration(converted);
                Quaternion initialRot = ApplyCalibrationOffsets(converted);
                _lastCalibratedPacketRot = initialRot;
                _lastPacketSeq = receiver.PacketSequence;
                _elapsedSinceLastDistinctPacket = 0f;
                transform.rotation = initialRot;
                return;
            }

            Quaternion calibratedPacketRot = ApplyCalibrationOffsets(converted);

            // Update angular velocity estimate when a new distinct packet arrives
            if (receiver.PacketSequence != _lastPacketSeq)
            {
                _lastPacketSeq = receiver.PacketSequence;
                Quaternion deltaQ = calibratedPacketRot * Quaternion.Inverse(_lastCalibratedPacketRot);
                deltaQ.ToAngleAxis(out float deltaAngleDeg, out Vector3 deltaAxis);

                if (deltaAngleDeg > 180f)
                {
                    deltaAngleDeg = 360f - deltaAngleDeg;
                    deltaAxis = -deltaAxis;
                }

                if (deltaAngleDeg > 0.0005f && _elapsedSinceLastDistinctPacket > 0.001f)
                {
                    float dt = Mathf.Clamp(_elapsedSinceLastDistinctPacket, 0.003f, 0.1f);
                    float instantSpeed = Mathf.Clamp(deltaAngleDeg / dt, 0f, 720f);

                    // Fast EMA on angular speed and axis
                    _estimatedAngularSpeedDegPerSec = Mathf.Lerp(_estimatedAngularSpeedDegPerSec, instantSpeed, 0.55f);
                    if (deltaAxis.sqrMagnitude > 1e-4f)
                    {
                        _estimatedAngularAxis = deltaAxis.normalized;
                    }

                    _lastCalibratedPacketRot = calibratedPacketRot;
                    _elapsedSinceLastDistinctPacket = 0f;
                }
                else
                {
                    // Stationary packet arrived: decay speed so dead-reckoning does not oscillate across packet intervals
                    _estimatedAngularSpeedDegPerSec = Mathf.Lerp(_estimatedAngularSpeedDegPerSec, 0f, 0.65f);
                    _lastCalibratedPacketRot = calibratedPacketRot;
                    _elapsedSinceLastDistinctPacket = 0f;
                }
            }
            else if (_elapsedSinceLastDistinctPacket > 0.05f)
            {
                _estimatedAngularSpeedDegPerSec = Mathf.Lerp(_estimatedAngularSpeedDegPerSec, 0f, 0.35f);
            }

            Quaternion targetRotation = calibratedPacketRot;

            // Direct 1:1 zero-lag mode: 0 ms raw response on slow/fine aiming, with micro Wi-Fi gap bridge ONLY during fast swings
            if (directZeroLagMode)
            {
                Quaternion directRot = ApplyFastTurnWifiGapBridge(targetRotation);
                if (1f - Mathf.Abs(Quaternion.Dot(transform.rotation, directRot)) > 1e-7f)
                {
                    transform.rotation = directRot;
                }
                return;
            }

            // Short-horizon Dead Reckoning when Wi-Fi delays a packet by > 6ms
            if (enableWifiGapPrediction && maxPredictionMs > 0f && _estimatedAngularSpeedDegPerSec > 1.5f)
            {
                float ageSec = receiver.PacketAgeSeconds;
                // Only extrapolate beyond the normal 5ms packet interval, capped at maxPredictionMs
                float extrapolateSec = Mathf.Clamp(ageSec - 0.005f, 0f, maxPredictionMs * 0.001f);
                if (extrapolateSec > 0f)
                {
                    // Decay velocity slightly over the prediction horizon to avoid overshoot on sudden stops
                    float decay = Mathf.Exp(-extrapolateSec * 25f);
                    float predAngle = _estimatedAngularSpeedDegPerSec * extrapolateSec * decay;
                    targetRotation = Quaternion.AngleAxis(predAngle, _estimatedAngularAxis) * calibratedPacketRot;
                }
            }

            Quaternion nextRotation;
            if (!enableAdaptiveSmoothing)
            {
                nextRotation = targetRotation;
            }
            else
            {
                float angularDiffDeg = Quaternion.Angle(transform.rotation, targetRotation);
                float effectiveSpeed = Mathf.Max(_estimatedAngularSpeedDegPerSec, angularDiffDeg * 30f);
                float speedFactor = Mathf.Clamp01(effectiveSpeed / Mathf.Max(10f, fastTurnSpeedDegPerSec));
                float adaptiveCutoff = Mathf.Lerp(minSmoothingSpeed, maxSmoothingSpeed, speedFactor);

                float t = 1f - Mathf.Exp(-adaptiveCutoff * Time.unscaledDeltaTime);
                nextRotation = Quaternion.Slerp(transform.rotation, targetRotation, t);
            }

            // Avoid dirtying the rifle transform hierarchy & child renderer bounds when orientation is unchanged
            if (1f - Mathf.Abs(Quaternion.Dot(transform.rotation, nextRotation)) > 1e-7f)
            {
                transform.rotation = nextRotation;
            }
        }

        /// <summary>
        /// Aligns the rifle's horizontal forward direction (Yaw) with the headset's current forward direction
        /// while keeping true gravity-referenced Pitch and Roll completely faithful to the real rifle.
        /// </summary>
        public void RecenterToHeadForward()
        {
            Transform refCam = headCameraTransform != null ? headCameraTransform : (Camera.main != null ? Camera.main.transform : null);
            Vector3 desiredForward = _headTracker != null
                ? (_headTracker.ReadyWorldRotation * Vector3.forward)
                : (refCam != null ? refCam.forward : Vector3.forward);

            if (receiver == null || !receiver.IsConnected)
            {
                _hasAutoZeroed = false;
                Vector3 flatFwd = new Vector3(desiredForward.x, 0f, desiredForward.z);
                if (flatFwd.sqrMagnitude > 1e-4f)
                    transform.rotation = Quaternion.LookRotation(flatFwd.normalized, Vector3.up);
                return;
            }

            Quaternion converted = ConvertImuToUnity(receiver.RawImuQuaternion);
            ApplyYawCalibrationToDirection(converted, desiredForward);
            _hasAutoZeroed = true;
            _lastCalibratedPacketRot = ApplyCalibrationOffsets(converted);
            _estimatedAngularSpeedDegPerSec = 0f;
            transform.rotation = _lastCalibratedPacketRot;
            Debug.Log("[RifleOrientationController] Tüfek yatay yönü (Yaw) görüş hattına sıfırlandı; dikey eğim gerçek yerçekimine bağlı korundu.");
        }

        /// <summary>
        /// Smoothly slerps the rifle's pitch and yaw calibration offsets toward the headset's current forward
        /// direction so sustained shoulder aiming never drifts off-center from the Red Dot reticle.
        /// </summary>
        public void SoftAlignToHeadForward(float blend01)
        {
            if (receiver == null || !receiver.IsConnected)
                return;

            float t = Mathf.Clamp01(blend01);
            if (t <= 0f)
                return;

            Transform refCam = headCameraTransform != null ? headCameraTransform : (Camera.main != null ? Camera.main.transform : null);
            Vector3 desiredForward = _headTracker != null
                ? (_headTracker.ReadyWorldRotation * Vector3.forward)
                : (refCam != null ? refCam.forward : Vector3.forward);

            Quaternion converted = ConvertImuToUnity(receiver.RawImuQuaternion);
            ComputeCalibrationOffsets(converted, desiredForward, out Quaternion targetYawOffset, out Quaternion targetPitchOffset);

            _yawCalibrationOffset = Quaternion.Slerp(_yawCalibrationOffset, targetYawOffset, t);
            _localPitchCalibrationOffset = Quaternion.Slerp(_localPitchCalibrationOffset, targetPitchOffset, t);
            _hasAutoZeroed = true;
        }

        /// <summary>
        /// Gently nudges the horizontal Yaw calibration offset around world +Y without disturbing
        /// gravity-referenced Pitch/Roll or injecting fake velocity into Wi-Fi dead-reckoning.
        /// Used by OpticalRifleTracker (Layer 2 2-LED Optical-IMU Yaw Lock) to cancel 6-DOF drift.
        /// </summary>
        public void ApplyOpticalYawCorrection(float deltaYawDeg)
        {
            if (receiver == null || !receiver.IsConnected)
                return;
            if (Mathf.Abs(deltaYawDeg) < 1e-5f)
                return;

            Quaternion yawStep = Quaternion.AngleAxis(deltaYawDeg, Vector3.up);
            _yawCalibrationOffset = yawStep * _yawCalibrationOffset;
            _lastCalibratedPacketRot = yawStep * _lastCalibratedPacketRot;
            transform.rotation = yawStep * transform.rotation;
            _hasAutoZeroed = true;
        }

        /// <summary>
        /// Continuously keeps the ESP32 IMU calibration aligned with the given world forward vector.
        /// </summary>
        public void SyncYawToWorldDirection(Vector3 desiredWorldForward)
        {
            if (receiver == null || !receiver.IsConnected)
                return;

            Quaternion converted = ConvertImuToUnity(receiver.RawImuQuaternion);
            ApplyYawCalibrationToDirection(converted, desiredWorldForward);
            _lastCalibratedPacketRot = ApplyCalibrationOffsets(converted);
            _hasAutoZeroed = true;
        }

        private Quaternion ApplyFastTurnWifiGapBridge(Quaternion baseCalibratedRot)
        {
            if (!enableFastTurnGapBridge || receiver == null || maxPredictionMs <= 0f)
                return baseCalibratedRot;

            float speed = _estimatedAngularSpeedDegPerSec;
            if (speed < fastTurnGapBridgeMinSpeedDegPerSec)
                return baseCalibratedRot;

            float ageSec = Mathf.Max(receiver.PacketAgeSeconds, _elapsedSinceLastDistinctPacket);
            // Normal 250 Hz (4 ms) akışta 0 ms ham veri; yalnız Wi-Fi gecikmesi > 5.5 ms olunca ara kare doldurulur
            float extrapolateSec = Mathf.Clamp(ageSec - 0.0055f, 0f, maxPredictionMs * 0.001f);
            if (extrapolateSec <= 0f)
                return baseCalibratedRot;

            float speedWeight = Mathf.SmoothStep(0f, 1f, (speed - fastTurnGapBridgeMinSpeedDegPerSec) / 28f);
            float decay = Mathf.Exp(-extrapolateSec * 28f);
            float predAngle = Mathf.Clamp(speed * extrapolateSec * decay * speedWeight, 0f, 6f);
            if (predAngle < 0.002f || _estimatedAngularAxis.sqrMagnitude < 1e-4f)
                return baseCalibratedRot;

            return Quaternion.AngleAxis(predAngle, _estimatedAngularAxis) * baseCalibratedRot;
        }

        private Quaternion ApplyCalibrationOffsets(Quaternion converted)
        {
            return _yawCalibrationOffset * (converted * _localPitchCalibrationOffset);
        }

        private void ApplyYawCalibration(Quaternion converted)
        {
            Transform refCam = headCameraTransform != null ? headCameraTransform : (Camera.main != null ? Camera.main.transform : null);
            Vector3 desiredForward = _headTracker != null
                ? (_headTracker.ReadyWorldRotation * Vector3.forward)
                : (refCam != null ? refCam.forward : Vector3.forward);
            ApplyYawCalibrationToDirection(converted, desiredForward);
        }

        private void ApplyYawCalibrationToDirection(Quaternion converted, Vector3 desiredForward)
        {
            ComputeCalibrationOffsets(converted, desiredForward, out _yawCalibrationOffset, out _localPitchCalibrationOffset);
        }

        private static void ComputeCalibrationOffsets(
            Quaternion converted,
            Vector3 desiredForward,
            out Quaternion yawOffset,
            out Quaternion localPitchOffset)
        {
            Vector3 safeDesired = desiredForward.sqrMagnitude > 1e-4f ? desiredForward.normalized : Vector3.forward;
            Vector3 flatDesired = new Vector3(safeDesired.x, 0f, safeDesired.z);
            if (flatDesired.sqrMagnitude < 1e-4f)
                flatDesired = Vector3.forward;
            else
                flatDesired.Normalize();

            Vector3 currentForward = converted * Vector3.forward;
            Vector3 flatCurrent = new Vector3(currentForward.x, 0f, currentForward.z);
            if (flatCurrent.sqrMagnitude < 1e-4f)
                flatCurrent = Vector3.forward;
            else
                flatCurrent.Normalize();

            float deltaYaw = Vector3.SignedAngle(flatCurrent, flatDesired, Vector3.up);
            yawOffset = Quaternion.AngleAxis(deltaYaw, Vector3.up);
            // Keep gravity-referenced Pitch untouched so virtual barrel elevation always matches physical barrel elevation.
            localPitchOffset = Quaternion.identity;
        }

        private Quaternion ConvertImuToUnity(Quaternion imuQ)
        {
            Quaternion unityQ;

            if (mountPreset == SensorMountPreset.VerticalBackFacingShooter)
            {
                unityQ = new Quaternion(-imuQ.x, -imuQ.z, -imuQ.y, imuQ.w) * VerticalMountRotation;
            }
            else if (mountPreset == SensorMountPreset.StandardMpuFlatForward)
            {
                unityQ = new Quaternion(-imuQ.x, -imuQ.z, -imuQ.y, imuQ.w);
            }
            else
            {
                float ux = ExtractAxis(imuQ, unityAxisFromSensor.x);
                float uy = ExtractAxis(imuQ, unityAxisFromSensor.y);
                float uz = ExtractAxis(imuQ, unityAxisFromSensor.z);
                unityQ = new Quaternion(-ux, -uy, -uz, imuQ.w);
            }

            if (invertPitch || invertYaw || invertRoll)
            {
                Vector3 euler = unityQ.eulerAngles;
                if (invertPitch) euler.x = -euler.x;
                if (invertYaw) euler.y = -euler.y;
                if (invertRoll) euler.z = -euler.z;
                unityQ = Quaternion.Euler(euler);
            }

            if (mountOffsetEuler.sqrMagnitude > 1e-5f)
            {
                if ((_lastMountOffsetEuler - mountOffsetEuler).sqrMagnitude > 1e-6f)
                {
                    _lastMountOffsetEuler = mountOffsetEuler;
                    _cachedMountOffsetQuat = Quaternion.Euler(mountOffsetEuler);
                }
                unityQ *= _cachedMountOffsetQuat;
            }

            return unityQ;
        }

        private static float ExtractAxis(Quaternion imuQ, int signedAxisIndex)
        {
            int idx = Mathf.Clamp(Mathf.Abs(signedAxisIndex), 1, 3);
            float sign = signedAxisIndex < 0 ? -1f : 1f;
            float val = idx == 1 ? imuQ.x : (idx == 2 ? imuQ.y : imuQ.z);
            return val * sign;
        }
    }
}
