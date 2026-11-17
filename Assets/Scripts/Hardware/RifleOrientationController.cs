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
        [Tooltip("PCB montaj yönünü seçin. Fotoğraftaki dikey montaj için VerticalBackFacingShooter kullanın.")]
        [SerializeField] private SensorMountPreset mountPreset = SensorMountPreset.VerticalBackFacingShooter;

        [Tooltip("Hangi sensör ekseni Unity sağ (X), yukarı (Y), ileri (Z) eksenine karşılık gelir.")]
        [SerializeField] private Vector3Int unityAxisFromSensor = new Vector3Int(1, 3, 2);
        [SerializeField] private bool invertPitch;
        [SerializeField] private bool invertYaw;
        [SerializeField] private bool invertRoll;

        [Header("İnce Montaj Ofseti (Derece)")]
        [SerializeField] private Vector3 mountOffsetEuler;

        [Header("Nişan Sıfırlama (Yaw Zeroing)")]
        [SerializeField] private bool autoZeroYawOnFirstPacket = true;

        [Header("Wi-Fi Paket Boşluğu Tahminleyici (Dead-Reckoning)")]
        [Tooltip("2.4 GHz Wi-Fi paketleri 10-30 ms geciktiğinde tüfeğin donmasını önlemek için açısal hızla ara kareleri doldurur.")]
        [SerializeField] private bool enableWifiGapPrediction = true;

        [Tooltip("Maksimum ileri tahmin süresi (ms). 2.4 GHz modemdeki 45-75 ms'lik mikro-gecikmeleri sarsıntısız kapatır.")]
        [Range(0f, 90f)]
        [SerializeField] private float maxPredictionMs = 65f;

        [Header("Hıza Duyarlı Akıllı Yumuşatma (1-Euro Adaptive Slerp)")]
        [Tooltip("Yavaş nişanda Wi-Fi paket adımlarını ütüler, hızlı dönüşte gecikmeyi sıfıra indirir.")]
        [SerializeField] private bool enableAdaptiveSmoothing = true;

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

        private void Awake()
        {
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
                Quaternion initialRot = _yawCalibrationOffset * converted;
                _lastCalibratedPacketRot = initialRot;
                _lastPacketSeq = receiver.PacketSequence;
                _elapsedSinceLastDistinctPacket = 0f;
                transform.rotation = initialRot;
                return;
            }

            Quaternion calibratedPacketRot = _yawCalibrationOffset * converted;

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
        /// Aligns the rifle's horizontal forward direction with the headset's current forward direction
        /// while keeping true gravity-referenced Pitch and Roll untouched.
        /// </summary>
        public void RecenterToHeadForward()
        {
            if (receiver == null || !receiver.IsConnected)
            {
                _hasAutoZeroed = false;
                return;
            }

            Quaternion converted = ConvertImuToUnity(receiver.RawImuQuaternion);
            ApplyYawCalibration(converted);
            _lastCalibratedPacketRot = _yawCalibrationOffset * converted;
            _estimatedAngularSpeedDegPerSec = 0f;
            transform.rotation = _lastCalibratedPacketRot;
            Debug.Log("[RifleOrientationController] Tüfek yönü görüş hattına sıfırlandı.");
        }

        private void ApplyYawCalibration(Quaternion converted)
        {
            Transform refCam = headCameraTransform != null ? headCameraTransform : (Camera.main != null ? Camera.main.transform : null);
            Vector3 desiredForward = _headTracker != null
                ? (_headTracker.ReadyWorldRotation * Vector3.forward)
                : (refCam != null ? refCam.forward : Vector3.forward);
            Vector3 flatDesiredForward = new Vector3(desiredForward.x, 0f, desiredForward.z);
            if (flatDesiredForward.sqrMagnitude < 1e-4f)
                flatDesiredForward = Vector3.forward;

            Vector3 currentForward = converted * Vector3.forward;
            Vector3 flatCurrentForward = new Vector3(currentForward.x, 0f, currentForward.z);
            if (flatCurrentForward.sqrMagnitude < 1e-4f)
                flatCurrentForward = Vector3.forward;

            float deltaYaw = Vector3.SignedAngle(flatCurrentForward, flatDesiredForward, Vector3.up);
            _yawCalibrationOffset = Quaternion.AngleAxis(deltaYaw, Vector3.up);
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
